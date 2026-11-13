using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using KellysJOINCHECK;

internal static class ServerMapUpdateChecks
{
    private static void Map(string path,string build,string contents)
    {
        var bytes=new byte[88+contents.Length];bytes[0]=78;bytes[1]=79;bytes[2]=77;bytes[3]=90;bytes[4]=1;bytes[6]=1;
        for(int i=0;i<4;i++)bytes[24+i]=Convert.ToByte(build.Substring(i*2,2),16);
        System.Text.Encoding.ASCII.GetBytes(contents).CopyTo(bytes,88);File.WriteAllBytes(path,bytes);
    }
    private static string Archive(string root,string leaf,string build,string contents)
    {
        string file=Path.Combine(root,Guid.NewGuid().ToString("N")+".nomapz");Map(file,build,contents);
        string archive=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");
        using(var z=ZipFile.Open(archive,ZipArchiveMode.Create))z.CreateEntryFromFile(file,leaf);
        return archive;
    }
    private static ServerMapOldFile Old(string plugins,string file,string key)=>new ServerMapOldFile {Relative=ServerMapUpdates.Relative(plugins,file),Key=key};
    internal static void Run(Action<bool,string> check)
    {
        const string a="5e83b9c6",b="3363c1ec",keyA="cm.swissalps."+a,keyB="cm.swissalps."+b;
        var record=new NommRecord {Id="NOCustomMaps.SwissAlps",Name="Swiss Alps",Owner="BatongMaster",Repository="NOSwissAlps"};
        check(ServerMapResolver.Find("swissalps",new[]{record})==record,"map lookup uses the exact NOCustomMaps registry identity");
        check(ServerMapResolver.Find("other",new[]{record})==null&&ServerMapResolver.Find("swissalps",new[]{record,record})==null,"unknown and ambiguous maps are never guessed");
        check(ServerMapResolver.ReleaseNamesBuild(new GitHubRelease{Body="Map hash8: `3363c1ec`\nOther notes"},b),"publisher's exact map build in release notes is recognised");
        check(!ServerMapResolver.ReleaseNamesBuild(new GitHubRelease{Body="Map hash8: `3363c1ec`\nMap hash8: `5e83b9c6`"},b)&&!ServerMapResolver.ReleaseNamesBuild(new GitHubRelease{Body="Map hash8: `5e83b9c6`"},b),"ambiguous and mismatched release notes do not authorize map downloads");
        var cascadia=new NommRecord {Id="NOCustomMaps.CascadiaFalling",Name="Cascadia Falling",Owner="netmaster296",Repository="CascadiaFalling"};
        check(ServerMapResolver.Candidates("cascadia",new[]{record,cascadia}).SequenceEqual(new[]{cascadia}),"a catalogue entry with a longer name is considered only as a map candidate");
        var cascRelease=new GitHubRelease {Body="The map key cm.cascadia.2546c698. Put the map in the maps folder."};
        check(ServerMapResolver.ReleaseNamesKey(cascRelease,"cm.cascadia.2546c698"),"Cascadia's publisher identifies its full map key in the release notes");
        check(!ServerMapResolver.ReleaseNamesKey(cascRelease,"cm.cascadia.11111111")&&!ServerMapResolver.ReleaseNamesKey(new GitHubRelease{Body="Use cm.cascadia.2546c698 as an example."},"cm.cascadia.2546c698"),"fallback catalogue candidates need the publisher's explicit exact map key");
        check(!ServerMapResolver.ReleaseNamesKey(new GitHubRelease{Body="Map key cm.cascadia.2546c698; map key cm.cascadia.11111111"},"cm.cascadia.2546c698"),"conflicting publisher map identities cannot authorize automatic installation");
        var link=GitHubInstallLink.Parse("https://github.com/netmaster296/CascadiaFalling/releases/download/v0.1.0/cascadia-0.1.0.zip");
        var big=new GitHubRelease {Assets=new[]{new GitHubAsset {Name="cascadia-0.1.0.zip",Url="https://github.com/netmaster296/CascadiaFalling/releases/download/v0.1.0/cascadia-0.1.0.zip",Size=1800000000,State="uploaded"}}};
        check(link.Select(big)==null&&link.Select(big,ServerMapUpdates.MaximumMapBytes)==big.Assets[0],"server map discovery accepts large maps within its explicit bound while ordinary installs keep their limit");
        string root=Path.Combine(Path.GetTempPath(),"doorman-server-map-"+Guid.NewGuid().ToString("N"));
        string plugins=Path.Combine(root,"BepInEx","plugins"),cache=Path.Combine(root,"BepInEx","cache"),maps=Path.Combine(plugins,"NOCustomMaps","maps");
        Directory.CreateDirectory(maps);Directory.CreateDirectory(cache);
        try
        {
            if(Environment.GetEnvironmentVariable("DOORMAN_TEST_REAL_CASCADIA")=="1")RealCascadia(check,plugins,cache);
            string old=Path.Combine(maps,"swissalps-0.4.0.nomapz"),target=Path.Combine(maps,"swissalps-0.4.1.nomapz");Map(old,a,"old map");
            string oldHash=ModUpdateFiles.Hash(old),archive=Archive(root,Path.GetFileName(target),b,"new map");
            string job=ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyB,new[]{Old(plugins,old,keyA)},CancellationToken.None);
            check(File.Exists(old)&&ModUpdateFiles.Hash(old)==oldHash&&!File.Exists(target),"queueing a server map leaves the running game's maps intact");
            ServerMapUpdates.VerifyPending(cache,plugins);ServerMapUpdates.Apply(cache,plugins,_=>{});
            check(!File.Exists(old)&&File.Exists(target)&&ModUpdateFiles.Hash(Path.Combine(job,"old-0.bin"))==oldHash,"restart installs the requested map and retains exact old bytes");
            string newHash=ModUpdateFiles.Hash(target);
            string restored=ServerMapUpdates.StageSaved(cache,plugins,keyA,new[]{Old(plugins,target,keyB)},CancellationToken.None);
            check(restored.Length>0&&File.Exists(target),"previous map build is queued from its saved copy without a download");
            ServerMapUpdates.Apply(cache,plugins,_=>{});
            check(File.Exists(old)&&ModUpdateFiles.Hash(old)==oldHash&&!File.Exists(target)&&ModUpdateFiles.Hash(Path.Combine(restored,"old-0.bin"))==newHash,"switching back restores the older map and saves the newer one");

            string conflict=ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyB,new[]{Old(plugins,old,keyA)},CancellationToken.None);
            File.AppendAllText(old,"user changed the file");string changed=ModUpdateFiles.Hash(old);
            try{ServerMapUpdates.Apply(cache,plugins,_=>{});check(false,"changed map rejected");}catch(IOException){check(ModUpdateFiles.Hash(old)==changed&&!File.Exists(target),"a changed old map blocks replacement without removing user bytes");}
            ServerMapUpdates.Cancel(cache,plugins,conflict);
            check(ServerMapUpdates.Read(cache,plugins,true).Length==0&&ModUpdateFiles.Hash(old)==changed,"cancelling a queued map does not touch installed files");

            File.WriteAllText(target,"unrelated existing file");
            try{ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyB,new[]{Old(plugins,old,keyA)},CancellationToken.None);check(false,"occupied destination rejected");}catch(IOException){check(File.ReadAllText(target)=="unrelated existing file"&&ModUpdateFiles.Hash(old)==changed,"an occupied map destination is never overwritten");}
            File.Delete(target);
            try{ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyA,new[]{Old(plugins,old,keyA)},CancellationToken.None);check(false,"wrong map rejected");}catch(InvalidDataException){check(ModUpdateFiles.Hash(old)==changed&&!File.Exists(target),"a downloaded map with the wrong build cannot be staged");}

            string interrupted=ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyB,new[]{Old(plugins,old,keyA)},CancellationToken.None);
            var plan=ServerMapUpdates.Read(cache,plugins,true).Single();plan.State="applying";UpdateJson.Save(Path.Combine(interrupted,"plan.json"),plan);File.Move(old,Path.Combine(interrupted,"old-0.bin"));
            ServerMapUpdates.Apply(cache,plugins,_=>{});
            check(File.Exists(target)&&ModUpdateFiles.Hash(Path.Combine(interrupted,"old-0.bin"))==changed,"an interrupted map move resumes from its intact backup");
            check(ServerMapUpdates.Read(cache,plugins,true).Length==0,"a completed map transaction is not reapplied on later launches");
            string batch=AutoSetupBatch.Begin(cache,plugins);
            string batchJob=ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyB,new[]{Old(plugins,target,keyB)},CancellationToken.None);
            AutoSetupBatch.Recover(cache,plugins,_=>{});
            check(batchJob.Length>0&&ServerMapUpdates.Read(cache,plugins,true).Length==0&&File.Exists(target),"an interrupted map setup cancels its queue without touching the installed map");
            string addonDir=Path.Combine(plugins,"NOCustomMaps","addons","SwissAlps");Directory.CreateDirectory(addonDir);
            string addon=Path.Combine(addonDir,"swissalps-old.nomapz");Map(addon,a,"NOMM add-on");string addonHash=ModUpdateFiles.Hash(addon);
            var registered=Old(plugins,addon,keyA);
            try{ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyB,new[]{Old(plugins,addon,"cm.othermap.5e83b9c6")},CancellationToken.None);check(false,"unrelated addon rejected");}catch(InvalidDataException){check(File.Exists(addon),"an add-on path cannot be moved without the same map identity");}
            string addonJob=ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),keyB,new[]{Old(plugins,target,keyB),registered},CancellationToken.None);
            ServerMapUpdates.Apply(cache,plugins,_=>{});
            check(!File.Exists(addon)&&ModUpdateFiles.Hash(Path.Combine(addonJob,"old-1.bin"))==addonHash,"a registered NOMM add-on map is switched with its old bytes preserved");
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static void RealCascadia(Action<bool,string> check,string plugins,string cache)
    {
        using(var nomm=new NommMetadataService(cache))using(var updates=new ModUpdateService(plugins,cache))
        {
            var index=nomm.GetAsync(CancellationToken.None).GetAwaiter().GetResult();
            var record=ServerMapResolver.Candidates("cascadia",index).Single(r=>r.Id=="NOCustomMaps.CascadiaFalling");
            var artifact=record.Artifacts.Single(a=>a.Version=="0.1.0");
            var link=GitHubInstallLink.Parse(artifact.DownloadUrl);
            var release=updates.CheckInstallLinkAsync(link,CancellationToken.None,ServerMapUpdates.MaximumMapBytes).GetAwaiter().GetResult();
            check(release.Release!=null&&release.Asset!=null&&ServerMapResolver.ReleaseNamesKey(release.Release,"cm.cascadia.2546c698")
                &&release.Asset.Digest==artifact.Hash&&release.Asset.Size>1024L*1024*1024,
                "live NOMNOM and GitHub metadata identify the exact large Cascadia map without downloading its archive");
            Console.WriteLine("Real Cascadia metadata: key=cm.cascadia.2546c698; archive bytes="+release.Asset!.Size);
        }
    }
    internal static int RealProbe(string root,string original,string archive)
    {
        if(Directory.Exists(root))throw new IOException("The isolated test directory already exists.");
        string plugins=Path.Combine(root,"BepInEx","plugins"),cache=Path.Combine(root,"BepInEx","cache"),maps=Path.Combine(plugins,"NOCustomMaps","maps");
        Directory.CreateDirectory(maps);Directory.CreateDirectory(cache);
        string old=Path.Combine(maps,"swissalps-0.4.0.nomapz"),newFile=Path.Combine(maps,"swissalps-0.4.1.nomapz");
        File.Copy(original,old);string oldHash=ModUpdateFiles.Hash(old);
        string job=ServerMapUpdates.Stage(cache,plugins,archive,Path.GetFileName(archive),ModUpdateFiles.Hash(archive),"cm.swissalps.3363c1ec",new[]{Old(plugins,old,"cm.swissalps.5e83b9c6")},CancellationToken.None);
        ServerMapUpdates.VerifyPending(cache,plugins);ServerMapUpdates.Apply(cache,plugins,Console.WriteLine);
        if(File.Exists(old)||!File.Exists(newFile)||ModUpdateFiles.Hash(Path.Combine(job,"old-0.bin"))!=oldHash)throw new Exception("Real map update did not preserve the old file.");
        string newHash=ModUpdateFiles.Hash(newFile);
        string back=ServerMapUpdates.StageSaved(cache,plugins,"cm.swissalps.5e83b9c6",new[]{Old(plugins,newFile,"cm.swissalps.3363c1ec")},CancellationToken.None);
        if(back.Length==0)throw new Exception("The original map was not rediscovered.");
        ServerMapUpdates.VerifyPending(cache,plugins);ServerMapUpdates.Apply(cache,plugins,Console.WriteLine);
        if(!File.Exists(old)||ModUpdateFiles.Hash(old)!=oldHash||File.Exists(newFile)||ModUpdateFiles.Hash(Path.Combine(back,"old-0.bin"))!=newHash)throw new Exception("Real map return did not preserve both versions.");
        Console.WriteLine("PASS: Real Swiss Alps 0.4.0 -> 0.4.1 -> 0.4.0 with verified backups. Old SHA256="+oldHash+"; new SHA256="+newHash);
        return 0;
    }
}
