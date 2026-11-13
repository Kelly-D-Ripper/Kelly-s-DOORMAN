using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class AddonRemovalChecks
{
    private static void Reject(Action<bool,string> check,Action action,string message){try{action();check(false,message);}catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is InvalidOperationException){check(true,message);}}
    private static RemovalFile Entry(string root,string relative,string path)=>new RemovalFile {Root=root,Relative=relative,Hash=AddonRemovalQueue.Digest(path)};
    internal static void Run(Action<bool,string> check,string client)
    {
        string temp=Path.Combine(Path.GetTempPath(),"doorman-delete-test-"+Guid.NewGuid().ToString("N"));
        string bep=Path.Combine(temp,"BepInEx"),plugins=Path.Combine(bep,"plugins"),disabled=Path.Combine(bep,"disabledPlugins"),cache=Path.Combine(bep,"cache"),config=Path.Combine(bep,"config");
        Directory.CreateDirectory(plugins);Directory.CreateDirectory(disabled);Directory.CreateDirectory(cache);Directory.CreateDirectory(config);
        try
        {
            string target=Path.Combine(plugins,"example.nobp"),other=Path.Combine(plugins,"other.nobp");File.WriteAllText(target,"one");File.WriteAllText(other,"other");
            var entry=new ModEntry {Id="example",Name="Example",Version="1.0",Content=true,Path=target};
            var unrelated=new ModEntry {Id="other",Name="Other",Version="1.0",Content=true,Path=other};
            var selection=AddonRemovalOwnership.Resolve(entry,new[]{entry,unrelated},Array.Empty<NommDisabledUnit>(),bep,cache);
            check(selection.Files.Length==1&&selection.Files[0].Relative=="example.nobp","deletion identifies the exact selected standalone file, not adjacent mods");
            unrelated.RequiredRequisites.Add(new ModRequisite {Bundle="example"});
            Reject(check,()=>AddonRemovalOwnership.Resolve(entry,new[]{entry,unrelated},Array.Empty<NommDisabledUnit>(),bep,cache),"another addon's declared dependency prevents deleting its prerequisite");
            check(AddonRemovalOwnership.Resolve(entry,new[]{entry,unrelated},Array.Empty<NommDisabledUnit>(),bep,cache,new[]{other}).Files.Length==1,"a dependent already queued for deletion does not block its prerequisite");unrelated.RequiredRequisites.Clear();
            AddonRemovalQueue.Stage(cache,bep,config,selection.Name,selection.Files);
            check(File.Exists(target)&&AddonRemovalQueue.Pending(cache,bep),"confirmation stages deletion without removing a loaded mod");
            string queuedHash=AddonRemovalQueue.Digest(Path.Combine(cache,"doorman-removal.json"));
            Reject(check,()=>AddonRemovalQueue.Stage(cache,bep,config,selection.Name,selection.Files),"the same file cannot be queued twice");
            check(AddonRemovalQueue.Digest(Path.Combine(cache,"doorman-removal.json"))==queuedHash,"a rejected duplicate preserves the original pending receipt");
            string extra=Path.Combine(plugins,"extra.nobp");File.WriteAllText(extra,"extra");
            var extraFile=Entry("plugins","extra.nobp",extra);
            File.WriteAllText(target,"changed while queued");
            Reject(check,()=>AddonRemovalQueue.Stage(cache,bep,config,"Extra",new[]{extraFile}),"a changed previously queued file prevents appending another deletion");
            check(AddonRemovalQueue.Digest(Path.Combine(cache,"doorman-removal.json"))==queuedHash&&File.Exists(extra),"failed batch append preserves both receipt and unqueued file");
            File.WriteAllText(target,"one");AddonRemovalQueue.Stage(cache,bep,config,"Extra",new[]{extraFile});
            check(AddonRemovalQueue.PendingPaths(cache,bep).Length==2&&File.Exists(target)&&File.Exists(extra),"multiple confirmed addons share one pending restart without removing files early");
            AddonRemovalQueue.Apply(cache,bep,config,_=>{});
            check(!File.Exists(target)&&!File.Exists(extra)&&File.Exists(other)&&!AddonRemovalQueue.Pending(cache,bep),"one startup deletes both confirmed addons and preserves unrelated mods");
            string packageTarget=ModUpdateFiles.PackageInstallTarget("test.package","package.dll");string packageDll=ModUpdateFiles.Within(plugins,packageTarget);Directory.CreateDirectory(Path.GetDirectoryName(packageDll)!);Fixture(packageDll,"test.package");
            string packageContent=Path.Combine(Path.GetDirectoryName(packageDll)!,"content.nobp");File.WriteAllText(packageContent,"package content");
            string job=Path.Combine(cache,"DOORMAN-updates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
            var receipt=new PendingModUpdate {Schema=5,Plugin="test.package",Repository="example/package",Version="1.0.0",PluginVersion="1.0.0",Target=packageTarget,NewHash=AddonRemovalQueue.Digest(packageDll),PackageSourceHash=new string('a',64),PackageRecordId="test.package",PackageDllName="package.dll",State="complete",PackageFiles=new[]{new PendingPackageFile {Index=0,Relative="content.nobp",NewHash=AddonRemovalQueue.Digest(packageContent),ContentId="test.bundle",ContentVersion="1.0.0"}}};receipt.Validate();UpdateJson.Save(Path.Combine(job,"plan.json"),receipt);
            var packagePlugin=new ModEntry {Id="test.package",Name="Package plugin",Version="1.0.0",Path=packageDll};var packagePack=new ModEntry {Id="test.bundle",Name="Package content",Version="1.0.0",Content=true,Path=packageContent};
            var package=AddonRemovalOwnership.Resolve(packagePack,new[]{packagePlugin,packagePack,unrelated},Array.Empty<NommDisabledUnit>(),bep,cache);
            check(package.Files.Length==2&&package.Components.Length==2,"a verified DLL plus content package is grouped from its exact installation receipt");
            AddonRemovalQueue.Stage(cache,bep,config,"Package",package.Files);AddonRemovalQueue.Apply(cache,bep,config,_=>{});
            check(!File.Exists(packageDll)&&!File.Exists(packageContent)&&File.Exists(other),"both verified DLL and content are deleted without removing another addon");
            File.WriteAllText(target,"again");var changed=Entry("plugins","example.nobp",target);File.WriteAllText(target,"changed after inspection");
            Reject(check,()=>AddonRemovalQueue.Stage(cache,bep,config,"Example",new[]{changed}),"a changed file cannot be queued after confirmation");
            check(File.ReadAllText(target)=="changed after inspection","failed confirmation does not change a file");
            var current=Entry("plugins","example.nobp",target);AddonRemovalQueue.Stage(cache,bep,config,"Example",new[]{current});File.WriteAllText(target,"changed before restart");
            Reject(check,()=>AddonRemovalQueue.Apply(cache,bep,config,_=>{}),"a changed file blocks startup deletion");check(File.ReadAllText(target)=="changed before restart","changed file is preserved");
            // Isolated fixture resets only its own receipt to exercise an interrupted move.
            File.Delete(Path.Combine(cache,"doorman-removal.json"));File.WriteAllText(target,"one");string second=Path.Combine(plugins,"two.nobp");File.WriteAllText(second,"two");
            AddonRemovalQueue.Stage(cache,bep,config,"Package",new[]{Entry("plugins","example.nobp",target),Entry("plugins","two.nobp",second)});
            var plan=AddonRemovalQueue.Read(cache,bep)!;plan.State="moving";UpdateJson.Save(Path.Combine(cache,"doorman-removal.json"),plan);
            string moved=Path.Combine(cache,"DOORMAN-removal-files",plan.Token);Directory.CreateDirectory(moved);File.Move(target,Path.Combine(moved,"0000.bin"));
            AddonRemovalQueue.Apply(cache,bep,config,_=>{});check(!File.Exists(target)&&!File.Exists(second)&&File.Exists(other),"a restart resumes an interrupted confirmed deletion");
            string legacy=Path.Combine(disabled,"Legacy");Directory.CreateDirectory(legacy);string disabledFile=Path.Combine(legacy,"content.nobp");File.WriteAllText(disabledFile,"disabled");
            string separate=Path.Combine(disabled,"Unrelated");Directory.CreateDirectory(separate);string separateFile=Path.Combine(separate,"keep.nobp");File.WriteAllText(separateFile,"keep");
            var candidate=DisabledModFiles.Inventory(bep).Single(c=>c.SourceRelative=="Legacy");
            var unit=NommDisabledCatalog.ReadUnit(bep,candidate,_=>new DisabledBundleInfo {Id="legacy.bundle",Name="Legacy addon",Version="1.0.0",GameVersion="0.34.6",Schema=3});
            check(unit.Error.Length==0&&unit.Entries.Count==1,"NOMM-disabled package is discovered with an exact physical receipt: "+unit.Error);
            var disabledSelection=AddonRemovalOwnership.Resolve(unit.Entries[0],unit.Entries,new[]{unit},bep,cache);
            AddonRemovalQueue.Stage(cache,bep,config,"Disabled",disabledSelection.Files);AddonRemovalQueue.Apply(cache,bep,config,_=>{});
            check(!File.Exists(disabledFile)&&!Directory.Exists(legacy)&&File.Exists(other)&&File.Exists(separateFile),"a verified NOMM-disabled package is removed without touching unrelated disabled or active mods");
            string protectedFile=Path.Combine(plugins,"protected.dll");File.Copy(client,protectedFile);
            Reject(check,()=>AddonRemovalQueue.Stage(cache,bep,config,"Protected",new[]{Entry("plugins","protected.dll",protectedFile)}),"DOORMAN plugin cannot be queued for deletion");
            check(File.Exists(protectedFile),"protected client bytes remain in place");
            Reject(check,()=>AddonRemovalQueue.Stage(cache,bep,config,"Escape",new[]{new RemovalFile {Root="plugins",Relative="../outside.nobp",Hash=new string('a',64)}}),"path traversal cannot be queued");
            string marker=Path.Combine(cache,"DOORMAN-auto-setup.json");File.WriteAllText(marker,"{}");
            Reject(check,()=>AddonRemovalQueue.Stage(cache,bep,config,"Other",new[]{Entry("plugins","other.nobp",other)}),"incomplete server setup blocks conflicting deletion");
            check(File.Exists(other),"blocked deletion preserves the other addon");
        }
        finally{Directory.Delete(temp,true);}
    }
    private static void Fixture(string path,string id)
    {
        using(var a=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("TestRemoval",new Version(1,0,0)),"TestRemoval",ModuleKind.Dll))
        {
            var m=a.MainModule;var attribute=new TypeReference("BepInEx","BepInPlugin",m,new AssemblyNameReference("BepInEx",new Version(5,4,23,0)));var ctor=new MethodReference(".ctor",m.TypeSystem.Void,attribute){HasThis=true};for(int i=0;i<3;i++)ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));
            var t=new TypeDefinition("Tests","Plugin",TypeAttributes.Public,m.TypeSystem.Object);m.Types.Add(t);var ca=new CustomAttribute(ctor);foreach(string value in new[]{id,"Test package","1.0.0"})ca.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,value));t.CustomAttributes.Add(ca);a.Write(path);
        }
    }
}
