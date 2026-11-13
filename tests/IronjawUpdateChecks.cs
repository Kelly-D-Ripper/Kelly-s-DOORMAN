using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class IronjawUpdateChecks
{
    private static readonly string[] Names={"TerrainLabGameplay.dll","TerrainLabNOPatch.dll","TerrainLabRace.dll","TerrainLabScatter.dll"};
    private static readonly string[] Ids={"talamander.terrainlab.gameplay","talamander.terrainlab.nopatch","talamander.terrainlab.race","talamander.terrainlab.scatter"};
    private static void Dll(string file,string id,string version)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(Path.GetFileNameWithoutExtension(file),new Version(1,0,0)),Path.GetFileNameWithoutExtension(file),ModuleKind.Dll))
        {
            var m=assembly.MainModule;var a=new TypeReference("BepInEx","BepInPlugin",m,new AssemblyNameReference("BepInEx",new Version(5,4,23,0)));
            var ctor=new MethodReference(".ctor",m.TypeSystem.Void,a){HasThis=true};for(int i=0;i<3;i++)ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));
            var t=new TypeDefinition("Tests","Plugin",TypeAttributes.Public,m.TypeSystem.Object);m.Types.Add(t);var attr=new CustomAttribute(ctor);
            foreach(string value in new[]{id,id,version})attr.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,value));t.CustomAttributes.Add(attr);assembly.Write(file);
        }
    }
    private static string Archive(string root,string extra="")
    {
        string source=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(source);
        for(int i=0;i<Names.Length;i++)Dll(Path.Combine(source,Names[i]),Ids[i],"1.0.2");
        File.WriteAllBytes(Path.Combine(source,"map_ironjawsound"),Encoding.ASCII.GetBytes("UnityFS\0new map"));
        File.WriteAllText(Path.Combine(source,"IronjawSound.release.json"),"{}");
        string zip=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");
        using(var z=ZipFile.Open(zip,ZipArchiveMode.Create))
        {
            foreach(string f in Directory.GetFiles(source))z.CreateEntryFromFile(f,Path.GetFileName(f));
            var mission=z.CreateEntry("Missions/Ironjaw Sound - Test/test.json");using(var w=new StreamWriter(mission.Open()))w.Write("mission");
            var preview=z.CreateEntry("Missions/Ironjaw Sound - Test/preview.jpg");using(var w=new StreamWriter(preview.Open()))w.Write("preview");
            if(extra.Length>0){var e=z.CreateEntry(extra);using(var w=new StreamWriter(e.Open()))w.Write("unexpected");}
        }
        return zip;
    }
    private static void Reject(Action<bool,string> check,Action act,string name)
    {try{act();check(false,name);}catch(Exception e) when(e is IOException||e is InvalidDataException||e is InvalidOperationException){check(true,name);}}
    internal static int RealSetupProbe(string archive,string root)
    {
        if(Directory.Exists(root)||File.Exists(root))throw new IOException("Choose a new isolated fixture directory.");
        string expected="17ea5e0a9654ce00da2aa81a5628e024143357b086b82fd393c77f5cb28a70e1";
        if(!ModUpdateFiles.Hash(archive).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The actual 1.1.1 archive does not match GitHub's published SHA-256.");
        string plugins=Path.Combine(root,"BepInEx","plugins"),cache=Path.Combine(root,"BepInEx","cache"),old=Path.Combine(plugins,"IronjawSound-1.1.1");
        Directory.CreateDirectory(old);Directory.CreateDirectory(cache);
        using(var zip=ZipFile.OpenRead(archive))foreach(string name in Names)(zip.GetEntry(name)??throw new InvalidDataException("Missing "+name)).ExtractToFile(Path.Combine(old,name));
        string oldMap=Path.Combine(old,"map_ironjawsound");File.WriteAllBytes(oldMap,Encoding.ASCII.GetBytes("UnityFS\0previous fixture"));
        string oldHash=ModUpdateFiles.Hash(oldMap);string keep=Path.Combine(old,"player-notes.txt");File.WriteAllText(keep,"leave this alone");
        string job=IronjawPackageUpdates.Stage(cache,plugins,archive,expected,"v1.1.1",CancellationToken.None);
        if(!File.Exists(oldMap)||!Names.All(n=>File.Exists(Path.Combine(old,n))))throw new Exception("Staging changed installed fixture files.");
        IronjawPackageUpdates.VerifyPending(cache,plugins);IronjawPackageUpdates.Apply(cache,plugins,_=>{});
        var plan=IronjawPackageUpdates.Read(cache,plugins,false).Single(p=>p.Job==job);
        var prior=plan.Files.Single(f=>f.Relative.EndsWith("IronjawSound-1.1.1"+Path.DirectorySeparatorChar+"map_ironjawsound",StringComparison.OrdinalIgnoreCase));
        if(plan.State!="complete"||ModUpdateFiles.Hash(Path.Combine(job,"old-"+prior.Index+".bin"))!=oldHash||File.ReadAllText(keep)!="leave this alone")throw new Exception("Previous fixture data was not preserved.");
        string installed=Path.Combine(plugins,"IronjawSound");
        if(!File.Exists(Path.Combine(installed,"map_ironjawsound"))||!Names.All(n=>File.Exists(Path.Combine(installed,n))))throw new Exception("The real package did not install its map and all plugins.");
        int images=Directory.GetFiles(Path.Combine(installed,"Missions"),"preview.jpg",SearchOption.AllDirectories).Length;
        if(images!=17)throw new Exception("Expected all 17 official mission preview images.");
        if(Names.Any(n=>File.Exists(Path.Combine(old,n)))||File.Exists(oldMap))throw new Exception("Legacy fixture components were not handled.");
        Console.WriteLine("PASS: GitHub SHA-256 verified; 1.1.1 staged without changing existing files, then installed map, four plugins and 17 mission previews. Previous map and unrelated notes preserved.");
        Console.WriteLine("Evidence: "+root);return 0;
    }
    internal static void Run(Action<bool,string> check)
    {
        var diag=MapJoinDiagnostic.Create("IronjawSound","Map","",Array.Empty<RegisteredMap>());
        check(diag.Name=="Ironjaw Sound"&&diag.MapId=="ironjawsound"&&diag.Instruction.Contains("not advertised"),"Ironjaw's stable unversioned map key is recognised without pretending its version is exact");
        check(MapJoinDiagnostic.Create("IronjawSoundExtra","Map","",Array.Empty<RegisteredMap>()).MapId=="","other raw map names do not select Ironjaw");
        string root=Path.Combine(Path.GetTempPath(),"doorman-ironjaw-test-"+Guid.NewGuid().ToString("N"));
        string plugins=Path.Combine(root,"BepInEx","plugins"),cache=Path.Combine(root,"BepInEx","cache");Directory.CreateDirectory(plugins);Directory.CreateDirectory(cache);
        try
        {
            DynamicWake(check,root);
            string zip=Archive(root),hash=ModUpdateFiles.Hash(zip),oldMap=Path.Combine(plugins,"NOMapLoader","map_ironjawsound");
            Directory.CreateDirectory(Path.GetDirectoryName(oldMap)!);File.WriteAllBytes(oldMap,Encoding.ASCII.GetBytes("UnityFS\0old map"));string oldMapHash=ModUpdateFiles.Hash(oldMap);
            for(int i=0;i<Names.Length;i++)Dll(Path.Combine(plugins,Path.GetFileNameWithoutExtension(Names[i]),Names[i]),Ids[i],"1.0.1");
            string extra=Path.Combine(plugins,"IronjawSound","user-notes.txt");Directory.CreateDirectory(Path.GetDirectoryName(extra)!);File.WriteAllText(extra,"keep my notes");
            string job=IronjawPackageUpdates.Stage(cache,plugins,zip,hash,"1.0.2",CancellationToken.None);
            check(File.Exists(oldMap)&&!File.Exists(Path.Combine(plugins,"IronjawSound","map_ironjawsound")),"Ironjaw setup does not move live files before restart");
            IronjawPackageUpdates.VerifyPending(cache,plugins);IronjawPackageUpdates.Apply(cache,plugins,_=>{});
            var plan=IronjawPackageUpdates.Read(cache,plugins,false).Single(p=>p.Job==job);var old=plan.Files.Single(f=>f.Relative.EndsWith("NOMapLoader\\map_ironjawsound",StringComparison.OrdinalIgnoreCase));
            check(!File.Exists(oldMap)&&ModUpdateFiles.Hash(Path.Combine(job,"old-"+old.Index+".bin"))==oldMapHash,"the old split-layout map is kept in the verified backup");
            check(Names.All(n=>File.Exists(Path.Combine(plugins,"IronjawSound",n)))&&File.Exists(Path.Combine(plugins,"IronjawSound","map_ironjawsound"))&&File.ReadAllText(extra)=="keep my notes","Ironjaw's complete folder is installed while unrelated existing files remain");
            check(Names.All(n=>!File.Exists(Path.Combine(plugins,Path.GetFileNameWithoutExtension(n),n))),"old split-layout plugin copies are retired to prevent duplicates");

            string changedJob=IronjawPackageUpdates.Stage(cache,plugins,zip,hash,"1.0.2",CancellationToken.None),activeMap=Path.Combine(plugins,"IronjawSound","map_ironjawsound");
            File.AppendAllText(activeMap,"user change");string changed=ModUpdateFiles.Hash(activeMap);
            Reject(check,()=>IronjawPackageUpdates.Apply(cache,plugins,_=>{}),"a map edited after setup blocks the replacement");
            check(ModUpdateFiles.Hash(activeMap)==changed,"a rejected replacement keeps the changed map");IronjawPackageUpdates.Cancel(cache,plugins,changedJob);

            string retry=IronjawPackageUpdates.Stage(cache,plugins,zip,hash,"1.0.2",CancellationToken.None);
            var interrupted=IronjawPackageUpdates.Read(cache,plugins,true).Single();var mapFile=interrupted.Files.Single(f=>f.Relative.EndsWith("IronjawSound\\map_ironjawsound",StringComparison.OrdinalIgnoreCase));
            interrupted.State="applying";UpdateJson.Save(Path.Combine(retry,"plan.json"),interrupted);File.Move(activeMap,Path.Combine(retry,"old-"+mapFile.Index+".bin"));
            IronjawPackageUpdates.VerifyPending(cache,plugins);IronjawPackageUpdates.Apply(cache,plugins,_=>{});
            check(File.Exists(activeMap)&&ModUpdateFiles.Hash(Path.Combine(retry,"old-"+mapFile.Index+".bin"))==changed,"interrupted Ironjaw installation resumes from the preserved backup");
            check(File.Exists(Path.Combine(plugins,"IronjawSound","Missions","Ironjaw Sound - Test","preview.jpg")),"official mission JPEG previews are installed with the package");

            string token=AutoSetupBatch.Begin(cache,plugins);string pending=IronjawPackageUpdates.Stage(cache,plugins,zip,hash,"1.0.2",CancellationToken.None);
            AutoSetupBatch.Recover(cache,plugins,_=>{});
            check(IronjawPackageUpdates.Read(cache,plugins,true).Length==0&&IronjawPackageUpdates.Read(cache,plugins,false).Single(p=>p.Job==pending).State=="cancelled","interrupted setup cancels its Ironjaw queue without changing installed files");
            Reject(check,()=>IronjawPackageUpdates.Stage(cache,plugins,Archive(root,"../escape.json"),ModUpdateFiles.Hash(Archive(root,"../escape.json")),"1.0.2",CancellationToken.None),"a mismatched archive is rejected");
            string dangerous=Archive(root,"../escape.json");Reject(check,()=>IronjawPackageUpdates.Stage(cache,plugins,dangerous,ModUpdateFiles.Hash(dangerous),"1.0.2",CancellationToken.None),"path traversal is rejected");
            dangerous=Archive(root,"extra.exe");Reject(check,()=>IronjawPackageUpdates.Stage(cache,plugins,dangerous,ModUpdateFiles.Hash(dangerous),"1.0.2",CancellationToken.None),"unexpected executable files are rejected");
            dangerous=Archive(root,"unrelated.jpg");Reject(check,()=>IronjawPackageUpdates.Stage(cache,plugins,dangerous,ModUpdateFiles.Hash(dangerous),"1.0.2",CancellationToken.None),"JPEGs outside the mission preview layout remain unsupported");
            dangerous=Archive(root,"Missions");Reject(check,()=>IronjawPackageUpdates.Stage(cache,plugins,dangerous,ModUpdateFiles.Hash(dangerous),"1.0.2",CancellationToken.None),"file and folder name collisions are rejected before restart");
            check(File.ReadAllText(extra)=="keep my notes"&&File.Exists(activeMap),"rejected packages leave existing files intact");

            string? workspace=Environment.GetEnvironmentVariable("DOORMAN_REAL_MAP_FIXTURES");
            if(!string.IsNullOrWhiteSpace(workspace))RealArchives(check,root,workspace);
            if(Environment.GetEnvironmentVariable("DOORMAN_TEST_MAP_LOADER_NETWORK")=="1")RealLoaders(check,root);
        }
        finally{Directory.Delete(root,true);}
    }
    private static void DynamicWake(Action<bool,string> check,string root)
    {
        string basedir=Path.Combine(root,"dynamic-wake"),plugins=Path.Combine(basedir,"plugins"),cache=Path.Combine(basedir,"cache");Directory.CreateDirectory(plugins);Directory.CreateDirectory(cache);
        string map=Path.Combine(basedir,"future.nomap"),dll=Path.Combine(basedir,"support.dll");File.WriteAllText(map,"future Wake map");Dll(dll,"kellys.wake.flight-support","0.2.0");
        string key="cm.kellys-wake-island."+ModUpdateFiles.Hash(map).Substring(0,8),archive=Path.Combine(basedir,"wake.zip");
        using(var zip=ZipFile.Open(archive,ZipArchiveMode.Create)){zip.CreateEntryFromFile(map,"maps/kellys-wake-island-future.nomap");zip.CreateEntryFromFile(dll,"plugins/KellysWakeFlightSupport.dll");var mission=zip.CreateEntry("Missions/example.json");using(var w=new StreamWriter(mission.Open()))w.Write("{}");}
        string hash=ModUpdateFiles.Hash(archive),url="https://github.com/Kelly-D-Ripper/KELLYS-Wake-Island/releases/download/v9.0/wake.zip";
        var discovered=ServerMapUpdates.InspectWakeRelease(cache,archive,hash,key,url,CancellationToken.None);
        check(discovered.MapHash==ModUpdateFiles.Hash(map)&&discovered.SupportHash==ModUpdateFiles.Hash(dll)&&discovered.SupportVersion=="0.2.0","a future Wake release is matched from its actual map and support DLL without a separate manifest");
        Reject(check,()=>ServerMapUpdates.InspectWakeRelease(cache,archive,hash,"cm.kellys-wake-island.00000000",url,CancellationToken.None),"a latest Wake release with the wrong server map key is rejected");
        ServerMapUpdates.Stage(cache,plugins,archive,"wake.zip",hash,key,Array.Empty<ServerMapOldFile>(),CancellationToken.None,discovered);
        ServerMapUpdates.StageWakeSupport(cache,plugins,archive,discovered,"",CancellationToken.None);
        ModUpdateFiles.ApplyPending(Path.Combine(cache,"DOORMAN-updates"),plugins,_=>{});ServerMapUpdates.Apply(cache,plugins,_=>{});
        check(File.Exists(Path.Combine(plugins,"NOCustomMaps","maps","kellys-wake-island-future.nomap"))&&File.Exists(Path.Combine(plugins,ModUpdateFiles.InstallTarget("kellys.wake.flight-support"))),"the discovered Wake release installs its verified map and support plugin together");
        Reject(check,()=>ServerMapUpdates.InspectWakeRelease(cache,archive,hash,key,"https://github.com/other/repo/releases/download/v9.0/wake.zip",CancellationToken.None),"a future Wake release must come from the official repository");
    }
    private static void RealLoaders(Action<bool,string> check,string root)
    {
        string baseDir=Path.Combine(root,"real-loaders"),plugins=Path.Combine(baseDir,"plugins"),cache=Path.Combine(baseDir,"cache");Directory.CreateDirectory(plugins);Directory.CreateDirectory(cache);
        using(var service=new ModUpdateService(plugins,cache))
        {
            foreach(var item in new[]{("BatongMaster/NOCustomMaps","com.javoski.custommaps",""),("MinecrackTyler/NOMapLoader","NOMapLoader","1.0.5")})
            {
                var link=GitHubInstallLink.Parse("https://github.com/"+item.Item1);var release=service.CheckInstallLinkAsync(link,CancellationToken.None).GetAwaiter().GetResult();
                if(release.Release==null||release.Asset==null||string.IsNullOrEmpty(release.Asset.Digest))throw new Exception("Unable to verify actual map-loader release: "+item.Item1);
                service.StageInstallAsync(item.Item1,item.Item2,item.Item3,release.Release,release.Asset,CancellationToken.None).GetAwaiter().GetResult();
            }
            var queue=ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins);ModUpdateFiles.VerifyPending(queue,plugins);
            check(queue.Any(p=>p.Plugin=="com.javoski.custommaps")&&queue.Any(p=>p.Plugin=="NOMapLoader"),"actual GitHub releases of both map loaders download and queue with verified plugin identities");
            ModUpdateFiles.ApplyPending(Path.Combine(cache,"DOORMAN-updates"),plugins,_=>{});
            check(File.Exists(Path.Combine(plugins,ModUpdateFiles.InstallTarget("com.javoski.custommaps")))&&File.Exists(Path.Combine(plugins,ModUpdateFiles.InstallTarget("NOMapLoader"))),"both downloaded map loaders install into the isolated plugin tree");
        }
    }
    private static void RealArchives(Action<bool,string> check,string root,string workspace)
    {
        string baseDir=Path.Combine(root,"real"),plugins=Path.Combine(baseDir,"plugins"),cache=Path.Combine(baseDir,"cache");Directory.CreateDirectory(plugins);Directory.CreateDirectory(cache);
        string iron=Path.Combine(workspace,"server-maintenance","staged","patch-0345-20261027","requested-content-20261027","IronjawSound-1.0.2.zip");
        string job=IronjawPackageUpdates.Stage(cache,plugins,iron,MapPackageInstaller.IronjawHash,"1.0.2",CancellationToken.None);
        IronjawPackageUpdates.VerifyPending(cache,plugins);IronjawPackageUpdates.Apply(cache,plugins,_=>{});
        check(Names.All(n=>File.Exists(Path.Combine(plugins,"IronjawSound",n)))&&File.Exists(Path.Combine(plugins,"IronjawSound","map_ironjawsound"))&&File.Exists(Path.Combine(plugins,"IronjawSound","terrainlab_rocks")),"the real Ironjaw 1.0.2 release installs all plugins, map and companion assets together");
        string a=Path.Combine(workspace,"WakeMap","GitHubRelease","Kellys-Wake-Island-0.3.11.zip"),b=Path.Combine(workspace,"WakeMap","GitHubRelease","Kellys-Wake-Island-0.3.13.zip");
        var wakeA=KnownWakeRelease.Find("cm.kellys-wake-island.82d17a35")!;var wakeB=KnownWakeRelease.Find("cm.kellys-wake-island.dc9ed557")!;
        var discovered=ServerMapUpdates.InspectWakeRelease(cache,b,wakeB.ArchiveHash,wakeB.Key,wakeB.Url,CancellationToken.None);
        check(discovered.MapHash==wakeB.MapHash&&discovered.SupportHash==wakeB.SupportHash&&discovered.SupportVersion==wakeB.SupportVersion,"the discovery path identifies the real Wake release from its archive without release-note metadata");
        ServerMapUpdates.Stage(cache,plugins,a,Path.GetFileName(a),wakeA.ArchiveHash,wakeA.Key,Array.Empty<ServerMapOldFile>(),CancellationToken.None);
        ServerMapUpdates.StageWakeSupport(cache,plugins,a,wakeA,"",CancellationToken.None);
        ModUpdateFiles.ApplyPending(Path.Combine(cache,"DOORMAN-updates"),plugins,_=>{});ServerMapUpdates.Apply(cache,plugins,_=>{});
        string old=Path.Combine(plugins,"NOCustomMaps","maps","kellys-wake-island-0.3.11.nomap"),support=Path.Combine(plugins,ModUpdateFiles.InstallTarget("kellys.wake.flight-support"));
        var previous=ServerMapUpdates.DiscoverConventional(plugins,"kellys-wake-island",CancellationToken.None);
        check(File.Exists(old)&&ModUpdateFiles.Hash(support)==wakeA.SupportHash&&previous.Single().Key==wakeA.Key,"the real Wake 0.3.11 map and its matching flight support install and are discoverable without a loaded map registry");
        job=ServerMapUpdates.Stage(cache,plugins,b,Path.GetFileName(b),wakeB.ArchiveHash,wakeB.Key,previous,CancellationToken.None);
        ServerMapUpdates.StageWakeSupport(cache,plugins,b,wakeB,support,CancellationToken.None);
        ModUpdateFiles.ApplyPending(Path.Combine(cache,"DOORMAN-updates"),plugins,_=>{});ServerMapUpdates.Apply(cache,plugins,_=>{});
        check(!File.Exists(old)&&ModUpdateFiles.Hash(Path.Combine(job,"old-0.bin"))==wakeA.MapHash&&ModUpdateFiles.Hash(support)==wakeB.SupportHash&&File.Exists(Path.Combine(plugins,"NOCustomMaps","maps","kellys-wake-island-0.3.13.nomap")),"switching real Wake builds saves the previous map and installs the matching support plugin");
    }
    internal static int PrepareIsolatedRun(string game,string wakeArchive,string ironArchive)
    {
        string expected=Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Nuclear Option mods","KellysDOORMAN-candidates","1.4.0-rc.19-package-support","evidence","game-build-26327221","isolated-game"));
        if(!Path.GetFullPath(game).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("This probe only stages to its named isolated game copy.");
        string plugins=Path.Combine(expected,"BepInEx","plugins"),cache=Path.Combine(expected,"BepInEx","cache");
        string archiveHash=ModUpdateFiles.Hash(wakeArchive);var wake=KnownWakeRelease.Published.SingleOrDefault(r=>r.ArchiveHash==archiveHash)??throw new IOException("This isolated probe needs a verified Wake release.");
        var old=ServerMapUpdates.DiscoverConventional(plugins,"kellys-wake-island",CancellationToken.None);
        string support=Path.Combine(plugins,"KellysWakeFlightSupport-rc21-test.dll");
        if(old.Length!=1||KnownWakeRelease.Find(old[0].Key)==null||old[0].Key==wake.Key||!File.Exists(support))throw new IOException("The isolated test fixture is not in its expected initial state.");
        string batch=AutoSetupBatch.Begin(cache,plugins);
        try
        {
            IronjawPackageUpdates.Stage(cache,plugins,ironArchive,MapPackageInstaller.IronjawHash,"1.0.2",CancellationToken.None);
            ServerMapUpdates.Stage(cache,plugins,wakeArchive,Path.GetFileName(wakeArchive),wake.ArchiveHash,wake.Key,old,CancellationToken.None);
            ServerMapUpdates.StageWakeSupport(cache,plugins,wakeArchive,wake,support,CancellationToken.None);
            IronjawPackageUpdates.VerifyPending(cache,plugins);ServerMapUpdates.VerifyPending(cache,plugins);ModUpdateFiles.VerifyPending(ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins),plugins);
            AutoSetupBatch.Commit(cache,batch);Console.WriteLine("Prepared verified isolated Wake and Ironjaw startup queues.");return 0;
        }
        catch{AutoSetupBatch.Abort(cache,plugins,batch);throw;}
    }
}
