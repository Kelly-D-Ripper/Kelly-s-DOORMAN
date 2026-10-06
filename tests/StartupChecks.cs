using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StartupRegression
{
    internal static void Run(Action<bool,string> check,string game,string startupPath,AssemblyDefinition client)
    {
        HostProtectionChecks(check);
        using(var helper=AssemblyDefinition.ReadAssembly(startupPath))
        {
            var patcher=helper.MainModule.GetType("DoormanStartupPatcher");
            check(!helper.MainModule.AssemblyReferences.Any(a=>a.Name.StartsWith("Unity",StringComparison.Ordinal)||a.Name=="Assembly-CSharp"),"preloader helper has no direct Unity/game assembly reference");
            var signatureTypes=patcher.Methods.Select(m=>m.ReturnType.FullName).Concat(patcher.Methods.SelectMany(m=>m.Parameters).Select(p=>p.ParameterType.FullName)).ToArray();
            check(!signatureTypes.Any(t=>t.Contains("BepInEx.Bootstrap")||t.Contains("BepInEx.PluginInfo")||t.Contains("Harmony")||t.Contains("Unity")),"all reflected patcher signatures including private methods remain independent of runtime types");
            var finish=patcher.Methods.Single(m=>m.Name=="Finish");
            check(!finish.Body.Instructions.Any(i=>i.Operand is MethodReference m&&(m.DeclaringType.FullName.Contains("Harmony")||m.DeclaringType.FullName.Contains("DoormanRuntimeHooks")))&&!finish.Body.Instructions.Any(i=>i.Operand is TypeReference t&&t.FullName.Contains("Chainloader")),"Finish never registers runtime Harmony hooks or resolves Chainloader");
            check(helper.MainModule.GetType("DoormanRuntimeHooks").Methods.All(m=>m.Name!="get_TargetDLLs"&&m.Name!="Patch"),"runtime hook signatures live on a type which BepInEx cannot discover as a patcher");
            var hooks=helper.MainModule.GetType("DoormanRuntimeHooks");
            var filter=hooks.Methods.Single(m=>m.Name=="Filter").Body.Instructions.ToList();
            check(filter.Any(i=>i.Operand is MethodReference m&&m.Name=="FilterPlugins")&&!filter.Any(i=>i.Operand is MethodReference m&&m.Name=="get_Dependencies"),"dependency drift never abandons valid disabled exclusions after filtering");
            int protect=filter.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="ProtectRuntimeHost");
            int config=filter.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="BepInEx.Paths"&&m.Name=="get_ConfigPath");
            check(protect>=0&&config>protect&&!filter.Take(protect).Any(i=>i.OpCode.Code==Code.Ret),"host protection runs before configuration lookup or its early return");
            var protectHost=hooks.Methods.Single(m=>m.Name=="ProtectRuntimeHost").Body.Instructions.ToList();
            int headless=protectHost.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="Headless");
            int apply=protectHost.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="DoormanRuntimeHost"&&m.Name=="Apply");
            check(headless>=0&&apply>headless&&protectHost.Skip(headless+1).Take(apply-headless-1).Any(i=>i.OpCode.Code==Code.Ret),"headless startup returns before changing runtime host protection");
            var hostLogic=helper.MainModule.GetType("DoormanRuntimeHost").Methods.Single(m=>m.Name=="Apply");
            check(hostLogic.Parameters.Count==1&&hostLogic.Parameters[0].ParameterType.FullName=="System.Object"&&!hostLogic.Body.Instructions.Any(i=>i.Operand is MemberReference m&&m.DeclaringType?.FullName.StartsWith("Unity",StringComparison.Ordinal)==true),"host protection helper exposes only an object signature and resolves Unity through reflection");
        }
        using(var core=AssemblyDefinition.ReadAssembly(Path.Combine(game,"NuclearOption_Data","Managed","UnityEngine.CoreModule.dll")))
        {
            var hideFlags=core.MainModule.GetType("UnityEngine.HideFlags");
            check(Convert.ToInt32(hideFlags.Fields.Single(f=>f.Name=="HideAndDontSave").Constant)==61,"installed Unity HideAndDontSave mask matches the existing Blueprinter protection baseline");
            var unityObject=core.MainModule.GetType("UnityEngine.Object");
            check(unityObject.Properties.Single(p=>p.Name=="hideFlags").PropertyType.FullName=="UnityEngine.HideFlags"&&unityObject.Methods.Count(m=>m.Name=="DontDestroyOnLoad"&&m.IsStatic&&m.IsPublic&&m.Parameters.Count==1&&m.Parameters[0].ParameterType.FullName=="UnityEngine.Object")==1,"installed Unity exposes the inherited flags property and unambiguous persistence method used by reflection");
            var application=core.MainModule.GetType("UnityEngine.Application");
            var cctor=application.Methods.FirstOrDefault(m=>m.Name==".cctor");
            if(cctor==null) { cctor=new MethodDefinition(".cctor",Mono.Cecil.MethodAttributes.Private|Mono.Cecil.MethodAttributes.Static|Mono.Cecil.MethodAttributes.SpecialName|Mono.Cecil.MethodAttributes.RTSpecialName,core.MainModule.TypeSystem.Void);application.Methods.Add(cctor);cctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }
            var chain=new TypeReference("BepInEx.Bootstrap","Chainloader",core.MainModule,new AssemblyNameReference("BepInEx",new Version(5,4,23,5)));
            var initialize=new MethodReference("Initialize",core.MainModule.TypeSystem.Void,chain);var start=new MethodReference("Start",core.MainModule.TypeSystem.Void,chain);
            var processor=cctor.Body.GetILProcessor();var first=cctor.Body.Instructions[0];processor.InsertBefore(first,Instruction.Create(OpCodes.Call,initialize));processor.InsertBefore(first,Instruction.Create(OpCodes.Call,start));
            var original=cctor.Body.Instructions.ToArray();int methods=application.Methods.Count;
            string error=DoormanStartupPatch.Inject(core,new AssemblyNameReference("KellysDOORMANStartup",new Version(1,1,2,0)));
            check(error.Length==0,"deferred hook accepts the normal loader entrypoint on the actual game core module");
            var calls=cctor.Body.Instructions.Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand).ToArray();
            check(calls[0].Name=="Initialize"&&calls[1].Name=="BeforePlugins"&&calls[2].Name=="Start","native loader initialization precedes the deferred hook and normal plugin loading");
            check(original.All(i=>cctor.Body.Instructions.Contains(i))&&cctor.Body.Instructions.Count==original.Length+1&&application.Methods.Count==methods,"entrypoint repair preserves all original instructions and native methods");
            int count=cctor.Body.Instructions.Count;DoormanStartupPatch.Inject(core,new AssemblyNameReference("KellysDOORMANStartup",new Version(1,1,2,0)));check(cctor.Body.Instructions.Count==count,"repeated patching never duplicates the runtime hook");
            using(var roundtrip=new MemoryStream()) { core.Write(roundtrip);roundtrip.Position=0;using(var read=AssemblyDefinition.ReadAssembly(roundtrip)) check(read.MainModule.GetType("UnityEngine.Application").Methods.Single(m=>m.Name==".cctor").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="BeforePlugins"),"repaired entrypoint survives Cecil serialization without changing the installed game DLL"); }
        }
        using(var bepinex=AssemblyDefinition.ReadAssembly(Path.Combine(game,"BepInEx","core","BepInEx.dll")))
        {
            var chainloader=bepinex.MainModule.GetType("BepInEx.Bootstrap.Chainloader").Methods.Single(m=>m.Name=="Start").Body.Instructions.ToList();
            int manager=chainloader.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="set_ManagerObject");
            int persistence=chainloader.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="DontDestroyOnLoad");
            int discovery=chainloader.FindIndex(i=>i.Operand is GenericInstanceMethod m&&m.DeclaringType.FullName=="BepInEx.Bootstrap.TypeLoader"&&m.Name=="FindPluginTypes"&&m.GenericArguments.Single().FullName=="BepInEx.PluginInfo");
            int component=chainloader.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="UnityEngine.GameObject"&&m.Name=="AddComponent");
            check(manager>=0&&persistence>manager&&discovery>persistence&&component>discovery,"actual BepInEx creates and persists its manager before discovery and adds plugin components afterwards");
            int missingDependency=chainloader.FindIndex(i=>i.Operand as string=="Could not load [{0}] because it has missing dependencies: {1}");
            int unloadedDependency=chainloader.FindIndex(i=>i.Operand as string=="Skipping [{0}] because it has a dependency that was not loaded. See previous errors for details.");
            check(missingDependency>discovery&&missingDependency<component&&unloadedDependency>discovery&&unloadedDependency<component,"installed Chainloader rejects absent and unloaded hard dependencies before adding plugin components");
        }
        var clientPlugin=client.MainModule.GetType("KellysJOINCHECK.ClientPlugin");
        var awake=clientPlugin.Methods.Single(m=>m.Name=="Awake").Body.Instructions.ToList();
        int verify=awake.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="PreserveRuntimeHost");
        int managerCreation=awake.FindIndex(i=>i.OpCode.Code==Code.Newobj&&i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.ModManager");
        check(verify>=0&&managerCreation>verify,"client verifies runtime host protection before creating mod manager and its coroutines");
        check(!clientPlugin.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m&&m.Name=="set_hideFlags"),"client never changes the shared host flags after plugin components have started");
        using(var empty=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("Unsupported",new Version(1,0)),"Unsupported",ModuleKind.Dll))
        { int references=empty.MainModule.AssemblyReferences.Count;check(DoormanStartupPatch.Inject(empty,new AssemblyNameReference("Helper",new Version(1,0))).Length>0&&empty.MainModule.AssemblyReferences.Count==references,"unknown startup layouts fail without changing the original assembly"); }
        string temp=Path.Combine(Path.GetTempPath(),"doorman-cold-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        var context=new ColdContext(Path.Combine(game,"BepInEx","core"));
        try
        {
            var helper=context.LoadFromAssemblyPath(Path.GetFullPath(startupPath));var patcher=helper.GetType("DoormanStartupPatcher",true)!;
            const BindingFlags flags=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.IgnoreCase;
            var signatures=patcher.GetMethods(flags);foreach(var method in signatures) { _=method.ReturnType;foreach(var parameter in method.GetParameters()) _=parameter.ParameterType; }
            check(signatures.Length>=3&&context.Blocked.Count==0,"BepInEx's cold reflection discovery completes with Unity/game loads blocked");
            var runtimeHost=new FakeRuntimeHost(FakeHideFlags.Extra);
            helper.GetType("DoormanRuntimeHost",true)!.GetMethod("Apply",flags)!.Invoke(null,new object[]{runtimeHost});
            check(runtimeHost.hideFlags==(FakeHideFlags.HideAndDontSave|FakeHideFlags.Extra)&&runtimeHost.FlagWrites==1&&runtimeHost.PersistenceCalls==1&&context.Blocked.Count==0,"compiled startup DLL protects an inherited fake host while Unity/game assembly loading remains blocked");
            var bepinex=context.LoadFromAssemblyPath(Path.Combine(game,"BepInEx","core","BepInEx.dll"));
            FilterDependencyChecks(check,helper,bepinex,flags);
            check(context.Blocked.Count==0,"compiled preference filtering operates on inert metadata without loading Unity or game code");
            bepinex.GetType("BepInEx.Paths",true)!.GetMethod("SetExecutablePath",flags)!.Invoke(null,new object?[]{Path.Combine(temp,"NuclearOption.exe"),Path.Combine(temp,"BepInEx"),Path.Combine(temp,"Managed"),Array.Empty<string>()});
            var targets=(IEnumerable<string>)patcher.GetMethod("get_TargetDLLs",flags)!.Invoke(null,null)!;
            check(targets.SequenceEqual(new[]{"UnityEngine.CoreModule.dll"}),"preloader targets only the in-memory engine startup assembly");
            patcher.GetMethod("Finish",flags)!.Invoke(null,null);
            check(context.Blocked.Count==0&&!((bool)patcher.GetField("Ready",flags)!.GetValue(null)!),"cold Finish completes without starting runtime hooks or loading Unity");
        }
        finally { context.Unload();Directory.Delete(temp,true); }
    }
    private static void FilterDependencyChecks(Action<bool,string> check,Assembly helper,Assembly bepinex,BindingFlags flags)
    {
        var infoType=bepinex.GetType("BepInEx.PluginInfo",true)!;
        var metadataType=bepinex.GetType("BepInEx.BepInPlugin",true)!;
        var dependencyType=bepinex.GetType("BepInEx.BepInDependency",true)!;
        var listType=typeof(List<>).MakeGenericType(infoType);
        var dictionaryType=typeof(Dictionary<,>).MakeGenericType(typeof(string),listType);
        object Info(string id,string? requires=null)
        {
            object info=Activator.CreateInstance(infoType,true)!;
            infoType.GetProperty("Metadata")!.SetValue(info,Activator.CreateInstance(metadataType,new object[]{id,id,"1.0.0"}));
            if(requires!=null)
            {
                var dependencies=Array.CreateInstance(dependencyType,1);
                dependencies.SetValue(Activator.CreateInstance(dependencyType,new object[]{requires,"1.0.0"}),0);
                infoType.GetProperty("Dependencies")!.SetValue(info,dependencies);
            }
            return info;
        }
        var original=(System.Collections.IDictionary)Activator.CreateInstance(dictionaryType)!;
        var sameDll=(System.Collections.IList)Activator.CreateInstance(listType)!;
        object disabled=Info("wrapper"),dependent=Info("client.dependent","wrapper"),unrelated=Info("client.ui"),doorman=Info("kelly.nuclearoption.joincheck");
        sameDll.Add(disabled);sameDll.Add(dependent);sameDll.Add(unrelated);sameDll.Add(doorman);original.Add("fixture.dll",sameDll);
        var excluded=new HashSet<string>(new[]{"wrapper","kelly.nuclearoption.joincheck"},StringComparer.Ordinal);
        var filtered=(System.Collections.IDictionary)helper.GetType("DoormanRuntimeHooks",true)!.GetMethod("FilterPlugins",flags)!.Invoke(null,new object[]{original,excluded,true})!;
        var kept=(System.Collections.IList)filtered["fixture.dll"]!;
        check(kept.Count==3&&!kept.Contains(disabled)&&kept.Contains(dependent),"a changed enabled hard dependency does not re-enable its excluded wrapper; native Chainloader can reject the dependent");
        check(kept.Contains(unrelated)&&kept.Contains(doorman),"startup filtering preserves unrelated client code and the protected manager");
        check(sameDll.Count==4&&sameDll.Contains(disabled)&&!ReferenceEquals(kept,sameDll)&&!ReferenceEquals(filtered,original),"dependency-drift filtering leaves the cached discovery dictionary and lists untouched");
        var reset=(System.Collections.IDictionary)helper.GetType("DoormanRuntimeHooks",true)!.GetMethod("FilterPlugins",flags)!.Invoke(null,new object[]{original,new HashSet<string>(StringComparer.Ordinal),false})!;
        check(((System.Collections.IList)reset["fixture.dll"]!).Count==4,"reset preferences retain every discovered plugin, including previously disabled wrappers");
    }
    private static void HostProtectionChecks(Action<bool,string> check)
    {
        var host=new FakeRuntimeHost(FakeHideFlags.Extra);
        DoormanRuntimeHost.Apply(host);
        check(host.hideFlags==(FakeHideFlags.HideAndDontSave|FakeHideFlags.Extra)&&host.FlagWrites==1,"runtime host protection preserves unrelated flags and applies the missing baseline once");
        check(host.PersistenceCalls==1&&host.Events.SequenceEqual(new[]{"flags","persist"}),"runtime host flags are protected before persistence is reasserted");
        host.StartPlugin();
        check(host.Events.SequenceEqual(new[]{"flags","persist","plugin"})&&host.PluginSawProtectedHost,"fake plugin starts after host protection with the complete baseline");
        DoormanRuntimeHost.Apply(host);
        check(host.FlagWrites==1&&host.PersistenceCalls==2,"repeated host protection reasserts persistence without resetting flags or interrupting plugin lifecycle");
        var protectedHost=new FakeRuntimeHost(FakeHideFlags.HideAndDontSave|FakeHideFlags.Extra);
        DoormanRuntimeHost.Apply(protectedHost);
        check(protectedHost.FlagWrites==0&&protectedHost.PersistenceCalls==1&&protectedHost.Events.SequenceEqual(new[]{"persist"}),"already protected hosts avoid every flags setter call");
        check(typeof(FakeRuntimeHost).GetProperty("hideFlags")!.DeclaringType==typeof(FakeRuntimeObject),"host fixture exercises the inherited public flags property and its declaring base persistence method");
        bool missingFlags=false;
        try{DoormanRuntimeHost.Apply(new MissingFlagsHost());}catch(MissingMemberException){missingFlags=true;}
        check(missingFlags,"hosts without a public flags contract fail explicitly");
        var missingPersistence=new MissingPersistenceHost();bool rejected=false;
        try{DoormanRuntimeHost.Apply(missingPersistence);}catch(MissingMethodException){rejected=true;}
        check(rejected&&missingPersistence.FlagWrites==0&&missingPersistence.hideFlags==FakeHideFlags.Extra,"missing persistence rejects protection before any flags mutation");
        bool nullRejected=false;
        try{DoormanRuntimeHost.Apply(null!);}catch(ArgumentNullException){nullRejected=true;}
        check(nullRejected,"null runtime hosts fail before reflection or mutation");
    }
    [Flags]
    private enum FakeHideFlags { HideAndDontSave=61,Extra=128 }
    private class FakeRuntimeObject
    {
        private FakeHideFlags flags;
        internal int FlagWrites,PersistenceCalls;
        internal readonly List<string> Events=new List<string>();
        internal FakeRuntimeObject(FakeHideFlags flags){this.flags=flags;}
        public FakeHideFlags hideFlags {get=>flags;set{flags=value;FlagWrites++;Events.Add("flags");}}
        public static void DontDestroyOnLoad(FakeRuntimeObject host){host.PersistenceCalls++;host.Events.Add("persist");}
    }
    private sealed class FakeRuntimeHost : FakeRuntimeObject
    {
        internal bool PluginSawProtectedHost;
        internal FakeRuntimeHost(FakeHideFlags flags):base(flags){}
        internal void StartPlugin(){PluginSawProtectedHost=(hideFlags&FakeHideFlags.HideAndDontSave)==FakeHideFlags.HideAndDontSave;Events.Add("plugin");}
    }
    private sealed class MissingFlagsHost {}
    private sealed class MissingPersistenceHost
    {
        private FakeHideFlags flags=FakeHideFlags.Extra;
        internal int FlagWrites;
        public FakeHideFlags hideFlags {get=>flags;set{flags=value;FlagWrites++;}}
    }
    private sealed class ColdContext : AssemblyLoadContext
    {
        private readonly string core;
        internal readonly List<string> Blocked=new List<string>();
        internal ColdContext(string core):base(true) { this.core=core; }
        protected override Assembly? Load(AssemblyName name)
        {
            if(name.Name!.StartsWith("Unity",StringComparison.Ordinal)||name.Name=="Assembly-CSharp") { Blocked.Add(name.Name);throw new FileNotFoundException("Early Unity loading blocked."); }
            if(name.Name==typeof(AssemblyDefinition).Assembly.GetName().Name) return typeof(AssemblyDefinition).Assembly;
            string path=Path.Combine(core,name.Name+".dll");return File.Exists(path)?LoadFromAssemblyPath(path):null;
        }
    }
}
