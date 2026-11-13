using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class StartupUpdatePreflightRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-startup-deps-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            CrossQueues(check,Path.Combine(root,"cross"));
            MissingAndTransitive(check,Path.Combine(root,"missing"));
            SelectionChecks(check,Path.Combine(root,"selection"));
            SameQueue(check,Path.Combine(root,"queue"));
            FailedPrerequisite(check,Path.Combine(root,"failed"));
            UserTemporaryFile(check,Path.Combine(root,"user-temp"));
            ApplyingRecovery(check,Path.Combine(root,"applying"));
            ConsumersAndCompanions(check,Path.Combine(root,"companions"));
            MultiPluginInstall(check,Path.Combine(root,"multi-install"));
            string realAssets=Environment.GetEnvironmentVariable("DOORMAN_TEST_REAL_ASSETS")??"";
            if(realAssets.Length>0)RealNosmrPackages(check,Path.Combine(root,"real-nosmr"),realAssets);
            InstalledWrapperBound(check,Path.Combine(root,"large"));
            EnumMetadata(check,Path.Combine(root,"enum"));
            PreviewDeclarations(check,Path.Combine(root,"previews"));
        }
        finally
        {
            string temp=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string target=Path.GetFullPath(root);
            if(!target.StartsWith(temp,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(target).StartsWith("doorman-startup-deps-",StringComparison.Ordinal))throw new IOException("Refusing unexpected test cleanup path.");
            Directory.Delete(target,true);
        }
    }
    private static void RealNosmrPackages(Action<bool,string> check,string root,string assets)
    {
        var tree=new Tree(root);tree.Installed("com.nikkorap.blueprinter","2.1.1");
        string bote=Path.Combine(assets,"Bote_1.5.4.dll"),kar=Path.Combine(assets,"Kar_1.1.2.dll"),chimera=Path.Combine(assets,"Aryx_MC260_Chimera_1.2.0.dll");
        string installed=Path.Combine(tree.Plugins,"Chimera.dll");
        string game=Environment.GetEnvironmentVariable("NUCLEAR_OPTION_DIR")??throw new InvalidOperationException("Game path is required for the real fixture.");
        File.Copy(Path.Combine(game,"BepInEx","plugins","Aryx_MC260_Chimera_1.2.0.dll"),installed);
        string old=ModUpdateFiles.Hash(installed);
        ModUpdateFiles.Stage(tree.Queue,tree.Plugins,installed,chimera,"Aryx_MC260_Chimera","Aryx3D/Aryx-MC260-Chimera","1.2.0");
        ModUpdateFiles.StageInstall(tree.Queue,tree.Plugins,kar,"Blueprinter.Kar","nikkorap/NOBlueprinter-kar");
        ModUpdateFiles.StageInstall(tree.Queue,tree.Plugins,bote,"com.minec.bote","MinecrackTyler/BoscaliOceanTrainingExercise","",null,default,true);
        var pending=ModUpdateFiles.ReadPending(tree.Queue,tree.Plugins);ModUpdateFiles.VerifyPending(pending,tree.Plugins);
        var metadata=QueuedModRequisiteMetadata.Read(pending,tree.Plugins);check(metadata.ContainsKey("com.minec.bote")&&metadata.ContainsKey("com.minec.bote.inputs")&&metadata.Values.All(m=>m.Error.Length==0),"real BOTE package passes queued prerequisite inspection");
        tree.Apply();
        check(pending.Length==3&&pending.All(p=>Receipt(p).State=="complete")&&ModUpdateFiles.Hash(installed)==ModUpdateFiles.Hash(chimera),"real Chimera, KAR and BOTE releases pass isolated restart preflight and installation");
        var change=pending.Single(p=>p.Plugin=="Aryx_MC260_Chimera");
        check(ModUpdateFiles.Hash(Path.Combine(change.Job,"previous.bin"))==old,"real Chimera downgrade retains its previous DLL as a verified rollback");
    }
    private static void MultiPluginInstall(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string payload=Path.Combine(root,"package.dll");
        Fixture(payload,"tests.package","1.0",Array.Empty<string>(),"tests.package.inputs");
        ModUpdateFiles.StageInstall(tree.Queue,tree.Plugins,payload,"tests.package","tests/repo","",null,default,true);
        var pending=ModUpdateFiles.ReadPending(tree.Queue,tree.Plugins);
        check(pending.Length==1&&pending[0].Schema==4&&pending[0].Install,"reviewed multi-plugin package is staged as a single install");
        var metadata=QueuedModRequisiteMetadata.Read(pending,tree.Plugins);
        check(metadata.Count==2&&metadata.ContainsKey("tests.package")&&metadata.ContainsKey("tests.package.inputs")&&metadata.Values.All(x=>x.Error.Length==0),"queued prerequisite metadata recognises every verified package plugin");
        string overlap=Path.Combine(root,"overlap.dll");Fixture(overlap,"tests.package.inputs","1.0",Array.Empty<string>());
        try{ModUpdateFiles.StageInstall(tree.Queue,tree.Plugins,overlap,"tests.package.inputs","tests/repo");check(false,"queued companion duplicate");}catch(InvalidOperationException){check(true,"another queued install cannot claim a package companion");}
        tree.Apply();check(Receipt(pending[0]).State=="complete"&&File.Exists(ModUpdateFiles.Within(tree.Plugins,pending[0].Target)),"startup preflight installs a verified multi-plugin package");
        try{ModUpdateFiles.StageInstall(tree.Queue,tree.Plugins,overlap,"tests.package.inputs","tests/repo");check(false,"installed companion duplicate");}catch(IOException){check(true,"an installed package companion cannot be installed twice");}
        var blocked=new Tree(root+"-protected");string bad=Path.Combine(blocked.Root,"protected.dll");Fixture(bad,"tests.package","1.0",Array.Empty<string>(),"kelly.nuclearoption.joincheck");
        try{ModUpdateFiles.StageInstall(blocked.Queue,blocked.Plugins,bad,"tests.package","tests/repo","",null,default,true);check(false,"protected package identity");}catch(InvalidDataException){check(true,"multi-plugin installs reject protected DOORMAN identities");}
    }
    private sealed class Tree
    {
        internal readonly string Root,Plugins,Queue,Config;
        internal readonly List<string> Log=new List<string>();
        internal Tree(string root){Root=root;Plugins=Path.Combine(root,"plugins");Queue=Path.Combine(root,"queue");Config=Path.Combine(root,"config");Directory.CreateDirectory(Plugins);Directory.CreateDirectory(Config);Directory.CreateDirectory(Path.Combine(root,"core"));File.Copy(typeof(BepInEx.BepInDependency).Assembly.Location,Path.Combine(root,"core","BepInEx.dll"));}
        internal string Installed(string id,string version,params string[] dependencies)
        {string path=Path.Combine(Plugins,id+".dll");Fixture(path,id,version,dependencies);return path;}
        internal PendingModUpdate Update(string id,string version,params string[] dependencies)
        {
            string payload=Path.Combine(Root,Guid.NewGuid().ToString("N")+".dll");Fixture(payload,id,version,dependencies);
            ModUpdateFiles.Stage(Queue,Plugins,Path.Combine(Plugins,id+".dll"),payload,id,"tests/repo",version);
            return ModUpdateFiles.PendingPlugin(Queue,id)!;
        }
        internal PendingModUpdate Install(string id,string version,params string[] dependencies)
        {
            string payload=Path.Combine(Root,Guid.NewGuid().ToString("N")+".dll");Fixture(payload,id,version,dependencies);
            ModUpdateFiles.StageInstall(Queue,Plugins,payload,id,"tests/repo");return ModUpdateFiles.PendingPlugin(Queue,id)!;
        }
        internal void Apply()=>StartupUpdatePreflight.ApplyPending(Queue,Plugins,Config,Log.Add);
        internal void Disable(params string[] ids)=>ModJson.Save(Path.Combine(Config,"doorman-mods.json"),new ModProfile {DisabledPlugins=ids});
    }
    private static void CrossQueues(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string target=tree.Installed("tests.a","1.0"),old=ModUpdateFiles.Hash(target);var update=tree.Update("tests.a","2.0");
        string contentQueue=Path.Combine(root,"content-queue"),content=Path.Combine(tree.Plugins,"test.nobp"),payload=Path.Combine(root,"content.bin");
        File.WriteAllText(content,"old content");File.WriteAllText(payload,"new content");StandaloneBundleUpdates.Stage(contentQueue,tree.Plugins,payload,content,"content","2.0",ModUpdateFiles.Hash(payload));
        File.WriteAllText(content,"changed content");bool blocked=false;
        try{StartupUpdatePreflight.ApplyQueues(tree.Queue,contentQueue,tree.Plugins,tree.Config,tree.Log.Add);}catch(IOException){blocked=true;}
        check(blocked&&ModUpdateFiles.Hash(target)==old,"cross-queue preflight blocks every change when content changed");
        File.WriteAllText(content,"old content");
        StartupUpdatePreflight.ApplyQueues(tree.Queue,contentQueue,tree.Plugins,tree.Config,tree.Log.Add);
        check(ModUpdateFiles.Hash(target)==update.NewHash&&File.ReadAllText(content)=="new content","cross-queue startup applies both verified updates");
        var missing=new Tree(root+"-missing");string plugin=missing.Installed("tests.a","1.0"),original=ModUpdateFiles.Hash(plugin);missing.Update("tests.a","2.0","tests.missing@1.0");
        string q=Path.Combine(root+"-missing","content-queue"),file=Path.Combine(missing.Plugins,"test.nobp"),download=Path.Combine(root+"-missing","content.bin");
        File.WriteAllText(file,"old");File.WriteAllText(download,"new");StandaloneBundleUpdates.Stage(q,missing.Plugins,download,file,"content","2.0",ModUpdateFiles.Hash(download));
        blocked=false;try{StartupUpdatePreflight.ApplyQueues(missing.Queue,q,missing.Plugins,missing.Config,missing.Log.Add);}catch(InvalidDataException){blocked=true;}
        check(blocked&&ModUpdateFiles.Hash(plugin)==original&&File.ReadAllText(file)=="old","content updates stay queued when plugin preflight fails");
    }
    private static PendingModUpdate Receipt(PendingModUpdate plan)=>UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(plan.Job,"plan.json"));
    private static void MissingAndTransitive(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string target=tree.Installed("tests.a","1.0");string before=ModUpdateFiles.Hash(target);
        var plan=tree.Update("tests.a","2.0","tests.b@2.0");tree.Apply();
        check(ModUpdateFiles.Hash(target)==before&&!File.Exists(Path.Combine(plan.Job,"previous.bin")),"ordinary startup leaves a working DLL and rollback directory untouched when its new hard dependency is missing");
        check(Receipt(plan).State=="pending"&&Receipt(plan).Message.Contains("tests.b")&&tree.Log.Any(m=>m.Contains("blocked")),"blocked startup updates retain a retryable receipt and visible prerequisite reason");
        tree.Installed("tests.b","1.0");tree.Apply();check(ModUpdateFiles.Hash(target)==before&&Receipt(plan).Message.Contains("2.0 or newer"),"startup enforces the declared hard dependency minimum");
        tree.Installed("tests.b","2.0","tests.c@1.0");tree.Apply();check(ModUpdateFiles.Hash(target)==before&&Receipt(plan).Message.Contains("tests.c"),"startup verifies the full transitive installed prerequisite chain");
        tree.Installed("tests.c","1.0");tree.Apply();
        check(Receipt(plan).State=="complete"&&ModUpdateFiles.Hash(target)==plan.NewHash&&ModUpdateFiles.Hash(Path.Combine(plan.Job,"previous.bin"))==before,"resolved prerequisites allow retry while retaining the exact original DLL backup");
    }
    private static void SelectionChecks(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string target=tree.Installed("tests.a","1.0");tree.Installed("tests.b","2.0");
        tree.Installed("tests.unrelated","1.0","tests.never-installed@1.0");tree.Disable("tests.b","tests.unrelated");
        var plan=tree.Update("tests.a","2.0","tests.b@2.0");tree.Apply();
        check(Receipt(plan).State=="pending"&&Receipt(plan).Message.Contains("disabled"),"ordinary startup respects disabled prerequisites in the saved normal profile");
        ServerSetupFiles.Save(tree.Config,new ServerSetupTicket {Target=new ServerTarget {Name="fixture",Dedicated=true,Ip="127.0.0.1",Port="7777",Expanded="0.34",Wire="0.34"},DisabledPlugins=new[]{"tests.unrelated"}});
        tree.Apply();check(Receipt(plan).State=="complete"&&ModUpdateFiles.Hash(target)==plan.NewHash,"a valid saved-server selection overrides the normal profile and disabled unrelated broken mods do not block updates");

        var disabled=new Tree(root+"-disabled");disabled.Installed("tests.a","1.0");disabled.Disable("tests.a");var inactive=disabled.Update("tests.a","2.0","tests.absent@1.0");disabled.Apply();
        check(Receipt(inactive).State=="complete","a deliberately disabled updated plugin need not enable its own unused prerequisites");
        var soft=new Tree(root+"-soft");soft.Installed("tests.a","1.0");soft.Installed("tests.unrelated","1.0","tests.absent@1.0");var softPlan=soft.Update("tests.a","2.0","?tests.optional");soft.Apply();
        check(Receipt(softPlan).State=="complete","optional BepInDependency and unrelated broken installed plugins do not block an independent update");

        var vanilla=new Tree(root+"-vanilla");vanilla.Installed("tests.a","1.0");vanilla.Installed("com.nikkorap.blueprinter","2.0.1");
        var needsLoader=vanilla.Update("tests.a","2.0","com.nikkorap.blueprinter@2.0.1");
        ServerSetupFiles.Save(vanilla.Config,new ServerSetupTicket {Target=new ServerTarget {Name="fixture",Dedicated=true,Ip="127.0.0.1",Port="7777",Expanded="0.34",Wire="0.34"},DisabledPlugins=new[]{"com.nikkorap.blueprinter"}});
        vanilla.Apply();check(Receipt(needsLoader).State=="pending"&&Receipt(needsLoader).Message.Contains("disabled"),"the saved vanilla-server Blueprinter exclusion is honored by startup dependency verification");
    }
    private static void SameQueue(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);tree.Installed("tests.a","1.0");var a=tree.Update("tests.a","2.0","tests.b@2.0");var b=tree.Install("tests.b","2.0","tests.c@1.0");var c=tree.Install("tests.c","1.0");
        tree.Apply();check(new[]{a,b,c}.All(p=>Receipt(p).State=="complete"),"startup installs a same-queue transitive prerequisite chain before its consumers regardless of receipt order");
        check(tree.Log.FindIndex(m=>m.StartsWith("Installed 1.0"))<tree.Log.FindLastIndex(m=>m.StartsWith("Installed 2.0")),"same-queue dependency availability follows completed installations rather than uncommitted projected versions");

        var corrupt=new Tree(root+"-corrupt");string target=corrupt.Installed("tests.a","1.0");string before=ModUpdateFiles.Hash(target);var consumer=corrupt.Update("tests.a","2.0","tests.b@2.0");var provider=corrupt.Install("tests.b","2.0");File.AppendAllText(Path.Combine(provider.Job,"payload.bin"),"corrupt");corrupt.Apply();
        check(ModUpdateFiles.Hash(target)==before&&Receipt(consumer).State=="pending"&&!File.Exists(ModUpdateFiles.Within(corrupt.Plugins,provider.Target)),"an invalid queued prerequisite never authorizes installation of its dependent update");

        var cycle=new Tree(root+"-cycle");string oldA=cycle.Installed("tests.a","1.0"),oldB=cycle.Installed("tests.b","1.0");
        var cycleA=cycle.Update("tests.a","2.0","tests.b@2.0");var cycleB=cycle.Update("tests.b","2.0","tests.a@2.0");cycle.Apply();
        check(Receipt(cycleA).State=="pending"&&Receipt(cycleB).State=="pending"&&ModUpdateFiles.Hash(oldA)==cycleA.OldHash&&ModUpdateFiles.Hash(oldB)==cycleB.OldHash,"a mutually unsatisfied same-queue cycle terminates without partially replacing either DLL");
    }
    private static void FailedPrerequisite(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string target=tree.Installed("tests.a","1.0");string before=ModUpdateFiles.Hash(target);var a=tree.Update("tests.a","2.0","tests.b@2.0");var b=tree.Install("tests.b","2.0");
        // A directory at the desired DLL path is a real installation collision.
        // It survives preflight but prevents an atomic file move.
        string obstacle=ModUpdateFiles.Within(tree.Plugins,b.Target);Directory.CreateDirectory(obstacle);tree.Apply();
        check(Receipt(b).State=="applying"&&Receipt(a).State=="pending"&&ModUpdateFiles.Hash(target)==before,"a failed prerequisite replacement leaves dependent DLLs unchanged and the interrupted transaction recoverable");
        Directory.Delete(obstacle);tree.Apply();check(Receipt(a).State=="complete"&&Receipt(b).State=="complete","a subsequent startup resumes a prerequisite transaction before applying its dependent update");
    }
    private static void ApplyingRecovery(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string target=tree.Installed("tests.a","1.0");tree.Installed("tests.b","2.0");var plan=tree.Update("tests.a","2.0","tests.b@2.0");
        File.Copy(target,Path.Combine(plan.Job,"previous.bin"));File.Copy(Path.Combine(plan.Job,"payload.bin"),target,true);plan.State="applying";UpdateJson.Save(Path.Combine(plan.Job,"plan.json"),plan);
        tree.Apply();check(Receipt(plan).State=="complete"&&ModUpdateFiles.Hash(target)==plan.NewHash&&ModUpdateFiles.Hash(Path.Combine(plan.Job,"previous.bin"))==plan.OldHash,"startup dependency checks allow exact old/new hash reconciliation for an already replaced applying DLL");
    }
    private static void UserTemporaryFile(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string target=tree.Installed("tests.a","1.0");string original=ModUpdateFiles.Hash(target);
        string userFile=target+".doorman-new";File.WriteAllText(userFile,"This belongs to the player.");
        var plan=tree.Update("tests.a","2.0");tree.Apply();
        check(Receipt(plan).State=="complete"&&ModUpdateFiles.Hash(target)==plan.NewHash,"ordinary plugin updates still install with an unrelated old-style temporary filename present");
        check(File.ReadAllText(userFile)=="This belongs to the player."&&ModUpdateFiles.Hash(Path.Combine(plan.Job,"previous.bin"))==original,"a plugin update preserves both an unrelated temporary-looking file and the original plugin backup");
    }
    private static void ConsumersAndCompanions(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);string target=tree.Installed("tests.a","3.0");string before=ModUpdateFiles.Hash(target);tree.Installed("tests.consumer","1.0","tests.a@3.0");var plan=tree.Update("tests.a","2.0");tree.Apply();
        check(ModUpdateFiles.Hash(target)==before&&Receipt(plan).State=="pending"&&Receipt(plan).Message.Contains("3.0 or newer"),"an update cannot downgrade a required DLL underneath an enabled installed consumer");
        tree.Disable("tests.consumer");tree.Apply();check(Receipt(plan).State=="complete","a disabled consumer does not block an otherwise valid replacement");

        var companion=new Tree(root+"-companion");string multi=Path.Combine(companion.Plugins,"tests.a.dll"),payload=Path.Combine(companion.Root,"multi.dll");
        Fixture(multi,"tests.a","1.0",Array.Empty<string>(),"tests.companion");Fixture(payload,"tests.a","2.0",new[]{"tests.companion@2.0"},"tests.companion");
        ModUpdateFiles.Stage(companion.Queue,companion.Plugins,multi,payload,"tests.a","tests/repo","2.0");var both=ModUpdateFiles.PendingPlugin(companion.Queue,"tests.a")!;companion.Apply();
        check(Receipt(both).State=="complete","hard dependencies on a companion GUID use the verified replacement DLL's own metadata");

        var duplicate=new Tree(root+"-duplicate");duplicate.Installed("tests.a","1.0");string provider=duplicate.Installed("tests.b","2.0");File.Copy(provider,Path.Combine(duplicate.Plugins,"another-b.dll"));var ambiguous=duplicate.Update("tests.a","2.0","tests.b@2.0");duplicate.Apply();
        check(Receipt(ambiguous).State=="pending"&&Receipt(ambiguous).Message.Contains("multiple installed identities"),"ambiguous installed prerequisite GUIDs cannot authorize a startup replacement");
    }
    private static void InstalledWrapperBound(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);tree.Installed("tests.a","1.0");string wrapper=tree.Installed("tests.large-wrapper","1.0");
        using(var stream=new FileStream(wrapper,FileMode.Open,FileAccess.Write,FileShare.None))stream.SetLength(ModUpdateFiles.MaximumBytes+1);
        var plan=tree.Update("tests.a","2.0");tree.Apply();
        check(Receipt(plan).State=="complete","metadata scanning accepts installed content wrappers above the updater's download size limit");
    }
    private static void EnumMetadata(Action<bool,string> check,string root)
    {
        var tree=new Tree(root);tree.Installed("tests.a","1.0");var hard=tree.Update("tests.a","2.0","!tests.b");tree.Apply();
        check(Receipt(hard).State=="pending"&&Receipt(hard).Message.Contains("not installed")&&!Receipt(hard).Message.Contains("resolve assembly"),"genuine BepInDependency.DependencyFlags hard metadata resolves from BepInEx/core and detects missing prerequisites");
        tree.Installed("tests.b","1.0");tree.Apply();check(Receipt(hard).State=="complete","a genuine enum hard dependency permits startup replacement once its provider is installed");

        var existing=new Tree(root+"-existing");existing.Installed("tests.a","1.0","tests.b@1.0");existing.Installed("tests.b","1.0");var a=existing.Update("tests.a","2.0","tests.b@2.0");var b=existing.Update("tests.b","2.0");existing.Apply();
        check(Receipt(a).State=="complete"&&Receipt(b).State=="complete","an existing pending consumer does not prevent its backward-compatible provider from updating first");

        var interrupted=new Tree(root+"-interrupted");string old=interrupted.Installed("tests.a","1.0");var consumer=interrupted.Update("tests.a","2.0","tests.b@2.0");var provider=interrupted.Install("tests.b","2.0");
        File.Copy(Path.Combine(provider.Job,"payload.bin"),ModUpdateFiles.Within(interrupted.Plugins,provider.Target));provider.State="applying";UpdateJson.Save(Path.Combine(provider.Job,"plan.json"),provider);File.AppendAllText(Path.Combine(provider.Job,"payload.bin"),"corrupt");interrupted.Apply();
        check(Receipt(consumer).State=="pending"&&Receipt(provider).State=="applying"&&ModUpdateFiles.Hash(old)==consumer.OldHash,"consumer-first startup rejects a new on-disk provider whose applying transaction fails payload verification");
    }
    private static void PreviewDeclarations(Action<bool,string> check,string root)
    {
        const string missing="{\"plugin\":\"tests.a\",\"name\":\"A\",\"requisites\":[{\"plugin\":\"tests.json\",\"minimumVersion\":\"2.0\"}]}";
        var embedded=new Tree(root);embedded.Installed("tests.a","1.0");var a=QueuePreview(embedded,missing,false);embedded.Apply();
        check(Receipt(a).State=="pending"&&Receipt(a).Message.Contains("tests.json"),"startup honors new plugin prerequisites declared only by the replacement's embedded doorman.json");
        embedded.Installed("tests.json","2.0");embedded.Apply();check(Receipt(a).State=="complete","resolved embedded plugin prerequisites allow the retained update to finish");

        var sidecar=new Tree(root+"-sidecar");sidecar.Installed("tests.a","1.0");var staged=QueuePreview(sidecar,missing,true);sidecar.Apply();
        check(Receipt(staged).State=="pending"&&Receipt(staged).Message.Contains("tests.json"),"startup honors plugin prerequisites declared by a verified queued managed preview");
        File.WriteAllText(Path.Combine(sidecar.Plugins,"custom.doorman.json"),"{\"plugin\":\"tests.a\",\"name\":\"Manual override\"}");sidecar.Apply();
        check(Receipt(staged).State=="complete","a user's manual plugin preview retains precedence over queued managed prerequisite metadata");

        var transitive=new Tree(root+"-transitive");transitive.Installed("tests.a","1.0");string dependency=transitive.Installed("tests.b","1.0");Embed(dependency,missing.Replace("tests.a","tests.b"));var chained=transitive.Update("tests.a","2.0","tests.b@1.0");transitive.Apply();
        check(Receipt(chained).State=="pending"&&Receipt(chained).Message.Contains("tests.json"),"installed providers' embedded plugin prerequisites are checked transitively");

        var removal=new Tree(root+"-removal");removal.Installed("tests.a","1.0");string managed=Path.Combine(removal.Plugins,ModUpdateFiles.MetadataDirectory("tests.a"));Directory.CreateDirectory(managed);File.WriteAllText(Path.Combine(managed,"preview.doorman.json"),"{\"plugin\":\"tests.a\",\"name\":\"Old managed\"}");var revealed=QueuePreview(removal,missing,false);removal.Apply();
        check(Receipt(revealed).State=="pending"&&Receipt(revealed).Message.Contains("tests.json")&&File.Exists(Path.Combine(managed,"preview.doorman.json")),"removal of an old managed preview reveals and verifies the replacement's embedded prerequisites before changing files");

        var bundle=new Tree(root+"-bundle");bundle.Installed("tests.a","1.0");var content=QueuePreview(bundle,"{\"plugin\":\"tests.a\",\"name\":\"A\",\"requisites\":[{\"bundle\":\"bundle.required\"}]}",false);bundle.Apply();
        check(Receipt(content).State=="pending"&&Receipt(content).Message.Contains("cannot be verified before startup")&&Receipt(content).Message.Contains("manually"),"active affected content prerequisites defer explicitly instead of being inferred from JSON or Unity-free metadata");
        bundle.Disable("tests.a");bundle.Apply();check(Receipt(content).State=="complete","inactive content prerequisites do not block an explicitly disabled update");
    }
    private static PendingModUpdate QueuePreview(Tree tree,string json,bool sidecar)
    {
        string payload=Path.Combine(tree.Root,"preview-payload.dll");Fixture(payload,"tests.a","2.0",Array.Empty<string>());if(!sidecar)Embed(payload,json);
        ModUpdateFiles.Stage(tree.Queue,tree.Plugins,Path.Combine(tree.Plugins,"tests.a.dll"),payload,"tests.a","tests/repo","2.0",sidecar?new[]{new StagedModSidecar {Name="preview.doorman.json",Data=Encoding.UTF8.GetBytes(json)}}:null);
        return ModUpdateFiles.PendingPlugin(tree.Queue,"tests.a")!;
    }
    private static void Embed(string path,string json)
    {
        using(var assembly=AssemblyDefinition.ReadAssembly(path))
        {assembly.MainModule.Resources.Add(new EmbeddedResource("doorman.json",ManifestResourceAttributes.Public,Encoding.UTF8.GetBytes(json)));assembly.Write(path+".new");}
        File.Delete(path);File.Move(path+".new",path);
    }
    private static void Fixture(string path,string id,string version,string[] dependencies,string companion="")
    {
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("StartupFixture",new Version(version)),"StartupFixture",ModuleKind.Dll))
        using(var bepinex=AssemblyDefinition.ReadAssembly(typeof(BepInEx.BepInDependency).Assembly.Location))
        {
            var module=assembly.MainModule;var scope=new AssemblyNameReference("BepInEx",new Version(5,4,23,0));
            ((DefaultAssemblyResolver)module.AssemblyResolver).AddSearchDirectory(Path.GetDirectoryName(typeof(BepInEx.BepInDependency).Assembly.Location));
            var identity=new MethodReference(".ctor",module.TypeSystem.Void,new TypeReference("BepInEx","BepInPlugin",module,scope)){HasThis=true};
            for(int i=0;i<3;i++)identity.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            foreach(string guid in companion.Length==0?new[]{id}:new[]{id,companion})
            {
                var type=new TypeDefinition("Fixtures","Plugin"+module.Types.Count,TypeAttributes.Public,new TypeReference("BepInEx","BaseUnityPlugin",module,scope));module.Types.Add(type);
                var attribute=new CustomAttribute(identity);foreach(string value in new[]{guid,"Startup fixture",version})attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,value));type.CustomAttributes.Add(attribute);
                if(guid!=id)continue;
                foreach(string text in dependencies)
                {
                    bool optional=text.StartsWith("?",StringComparison.Ordinal),flags=optional||text.StartsWith("!",StringComparison.Ordinal);string[] parts=(flags?text.Substring(1):text).Split('@');
                    var actual=bepinex.MainModule.GetType("BepInEx.BepInDependency").Methods.Single(m=>m.IsConstructor&&m.Parameters.Count==2&&(flags?m.Parameters[1].ParameterType.Name=="DependencyFlags":m.Parameters[1].ParameterType.FullName=="System.String"));
                    var constructor=module.ImportReference(actual);var second=constructor.Parameters[1].ParameterType;
                    var dependency=new CustomAttribute(constructor);dependency.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,parts[0]));dependency.ConstructorArguments.Add(new CustomAttributeArgument(second,flags?(object)(optional?2:1):parts.Length>1?parts[1]:""));type.CustomAttributes.Add(dependency);
                }
            }
            assembly.Write(path);
        }
    }
}
