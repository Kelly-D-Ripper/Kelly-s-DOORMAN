using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using BepInEx.Bootstrap;
using HarmonyLib;
using Mirage;
using UnityEngine;
using UnityEngine.AddressableAssets;

namespace KellysJOINCHECK
{
    internal sealed class BlueprinterReload
    {
        // Only builds whose startup and prefab-allocation contracts have been audited.
        private const string LoaderHash201="A2CEC71BDA003824695B5C5B55879A50C4DAD579A5597C795600428636F1F7EC";
        private const string LoaderHash211="2DBC8E40FEF56BEA9A0FC2C0242614642C2A832C8062673E50FF47F677D44FE2";
        private static BlueprinterReload? instance;
        private readonly Action<string> log;
        private readonly Func<ModProfile> profile;
        private readonly Action ready;
        private object? plugin, registry;
        private Assembly? assembly;
        private Type? registryType, runnerType;
        private ModStateSnapshot? baseline, expected;
        private readonly List<object> bundles=new List<object>();
        private readonly Dictionary<object,List<(NetworkIdentity Identity,PrefabHashInput Input)>> identities=new Dictionary<object,List<(NetworkIdentity,PrefabHashInput)>>();
        private readonly List<int> nativeHashes=new List<int>();
        private readonly List<object> baseLocators=new List<object>();
        private readonly List<object> ownedLocators=new List<object>();
        private bool failed, rebuilding, assigningInitial;
        private NetworkIdentity[] initialIdentities=Array.Empty<NetworkIdentity>();
        private string reloadWarning="";
        private object? initialRunner;
        internal bool Ready { get; private set; }
        internal bool LoadingFinished { get; private set; }
        internal bool StartupSupported { get; private set; }
        internal bool InventoryCaptured { get; private set; }
        internal string Unavailable="Blueprinter is still loading.";
        internal readonly List<InstalledContent> Content=new List<InstalledContent>();
        // Snapshot only at loader completion or an explicit reload. Opening a
        // support report must not walk assets or retry failed content patches.
        internal string[] SupportDetails { get; private set; }=Array.Empty<string>();
        internal string GameVersion="";

        internal BlueprinterReload(Harmony harmony,Func<ModProfile> profile,Action<string> log,Action ready)
        {
            this.profile=profile; this.log=log; this.ready=ready; instance=this;
            try
            {
                if (!Chainloader.PluginInfos.TryGetValue("com.nikkorap.blueprinter",out var info)) { LoadingFinished=true;throw new InvalidOperationException("Blueprinter is disabled. Content changes need a restart."); }
                using(var sha=SHA256.Create()) using(var stream=File.OpenRead(info.Location))
                {
                    var hash=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                    if(hash!=LoaderHash201 && hash!=LoaderHash211) throw new InvalidOperationException("This Blueprinter build needs a reload adapter update.");
                }
                plugin=info.Instance; assembly=plugin.GetType().Assembly; GameVersion=(string)Get(plugin,"GameVersion");
                registryType=assembly.GetType("Blueprinter.BundleRegistry",true); runnerType=assembly.GetType("Blueprinter.PatchRunner",true);
                Hook(harmony,registryType!,"ScanAndLoadCoroutine",nameof(ScanPostfix),false);
                Hook(harmony,registryType!,"FastLoad",nameof(FastPostfix),false);
                StartupSupported=true;
                Hook(harmony,assembly.GetType("Blueprinter.PrefabHashAssigner",true)!,"AssignFromBundles",nameof(HashPrefix),true);
                Hook(harmony,assembly.GetType("Blueprinter.PrefabHashAssigner",true)!,"AssignFromBundles",nameof(HashPostfix),false);
                var assigner=assembly.GetType("Blueprinter.PrefabHashAssigner",true)!;
                var allocation=AccessTools.Method(assigner,"AssignFromBundles") ?? throw new MissingMethodException(assigner.FullName,"AssignFromBundles");
                // Blueprinter 2.1.1 moved the exact reservation query into this helper.
                // The transpiler still requires exactly one matching query and fails closed.
                var resourceTarget=AccessTools.Method(assigner,"CollectExistingHashes") ?? allocation;
                harmony.Patch(resourceTarget,transpiler:new HarmonyMethod(typeof(BlueprinterReload),nameof(HashResourcesTranspiler)));
                harmony.Patch(allocation,finalizer:new HarmonyMethod(typeof(BlueprinterReload),nameof(HashFinalizer)));
                Hook(harmony,runnerType!,"ApplyAllOps",nameof(OpsPrefix),true);
                Hook(harmony,plugin.GetType(),"RunRoutine",nameof(RoutinePostfix),false);
                Hook(harmony,typeof(BepInEx.Logging.ManualLogSource),"Log",nameof(LogPrefix),true);
            }
            catch(Exception ex) { failed=true; Unavailable=ex.Message; log("Mod reload unavailable: "+ex.Message); }
        }
        private static void ScanPostfix(object __instance,ref IEnumerator __result)
        {
            var self=instance;if(self==null)return;
            __result=new CompletionEnumerator(__result,()=>self.FilterInitial(__instance),ex=>self.Fail("Could not select startup content: "+ex.Message));
        }
        private static void FastPostfix(object __instance)=>instance?.FilterInitial(__instance);
        private void FilterInitial(object loadedRegistry)
        {
            IList? list=null;object[]? original=null;bool changed=false;
            try
            {
                list=(IList)Get(loadedRegistry,"Bundles");original=list.Cast<object>().ToArray();
                if(list.IsReadOnly||list.IsFixedSize)throw new InvalidOperationException("The loader's content list cannot be selected.");
                var disabled=new HashSet<string>(profile().DisabledContent,StringComparer.Ordinal);
                var sources=ModCatalog.LoadedSourceAssemblies(log);
                var inventory=original.Select(bundle=>
                {
                    var manifest=Get(bundle,"Manifest");string id=(string)Get(bundle,"bundleName");
                    var item=new InstalledContent { Id=id,Name=(string)Get(manifest,"modName"),Version=(string)Get(manifest,"modVersion"),Source=(string)Get(bundle,"source"),Enabled=!disabled.Contains(id) };
                    ObserveSource(item,sources);return item;
                }).ToArray();
                // Inspect the whole list before changing the native registry. A
                // rejected selection must leave the game's normal load intact.
                for(int i=list.Count-1;i>=0;i--) if(disabled.Contains(inventory[i].Id)) { list.RemoveAt(i);changed=true; }
                Content.Clear();Content.AddRange(inventory);
                InventoryCaptured=true;
                if(disabled.Count>0) log("Selected startup content before Blueprinter prefab preparation.");
            }
            catch(Exception ex)
            {
                if(changed&&list!=null&&original!=null)try { list.Clear();foreach(var bundle in original)list.Add(bundle); }catch(Exception restore) { log("Startup content restoration failed: "+restore.Message); }
                Fail("Startup selection unavailable; normal content loading preserved: "+ex.Message);
            }
        }
        private static void Hook(Harmony harmony,Type type,string method,string hook,bool prefix)
        {
            var original=AccessTools.Method(type,method) ?? throw new MissingMethodException(type.FullName,method);
            var callback=new HarmonyMethod(typeof(BlueprinterReload),hook);
            if(prefix) harmony.Patch(original,prefix:callback); else harmony.Patch(original,postfix:callback);
        }
        private static void HashPrefix(object __0)
        {
            var self=instance; if(self==null||self.failed||self.rebuilding) return;
            try { self.CaptureHashes((IEnumerable)__0); }
            catch(Exception ex) { self.Fail("Live reload unavailable: "+ex.Message+" Restart to apply mod changes."); }
        }
        private static void HashPostfix()
        {
            var self=instance; if(self==null||self.rebuilding) return;
            try
            {
                if(self.failed) return;
                var calculated=ContentPrefabHashes.Assign(self.nativeHashes,self.identities.Values.SelectMany(x=>x).Select(x=>x.Input));
                if(self.identities.Values.SelectMany(x=>x).Any(x=>x.Identity.PrefabHash!=calculated[x.Input])) self.Fail("The loader's prefab allocation changed. Restart is required.");
            }
            finally { self.assigningInitial=false; }
        }
        private static IEnumerable<CodeInstruction> HashResourcesTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code=instructions.ToList(); int replaced=0;
            foreach(var instruction in code)
                if(instruction.operand is MethodInfo method && method.Name=="FindObjectsOfTypeAll" && method.DeclaringType==typeof(Resources) && method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[]{typeof(NetworkIdentity)}))
                { instruction.opcode=OpCodes.Call; instruction.operand=AccessTools.Method(typeof(BlueprinterReload),nameof(OriginalIdentities)); replaced++; }
            if(replaced!=1) throw new InvalidOperationException("The loader's resource query changed.");
            return code;
        }
        private static NetworkIdentity[] OriginalIdentities()
        {
            // Inspecting bundle prefabs must not make extra identities visible to
            // the loader's initial reservation pass. Return its original view.
            var self=instance; return self?.assigningInitial==true?self.initialIdentities:Resources.FindObjectsOfTypeAll<NetworkIdentity>();
        }
        private static Exception? HashFinalizer(Exception? __exception)
        {
            if(instance!=null) { instance.assigningInitial=false; if(__exception!=null) instance.Fail("Prefab allocation failed. Restart is required."); }
            return __exception;
        }
        private static void OpsPrefix(object __instance,Encyclopedia __0)
        {
            var self=instance; if(self==null||self.failed||self.rebuilding) return;
            try
            {
                self.registry=Get(__instance,"<bundles>P"); self.initialRunner=__instance;
                self.baseline=self.CaptureState(__0);
                self.baseLocators.AddRange(Addressables.ResourceLocators.Cast<object>());
            } catch(Exception ex) { self.Fail("Could not capture the original mod lists: "+ex.Message); }
        }
        private static void LogPrefix(BepInEx.Logging.ManualLogSource __instance,BepInEx.Logging.LogLevel __0,object __1)
        {
            var self=instance;
            if(self==null||!self.rebuilding||self.plugin==null||(__0&(BepInEx.Logging.LogLevel.Warning|BepInEx.Logging.LogLevel.Error|BepInEx.Logging.LogLevel.Fatal))==0) return;
            var source=AccessTools.Property(self.plugin.GetType(),"Log")?.GetValue(null,null);
            if(ReferenceEquals(source,__instance)) self.reloadWarning=Diagnostics.Clean(__1?.ToString()??"A content operation failed.");
        }
        private static void RoutinePostfix(ref IEnumerator __result)
        {
            var self=instance; if(self==null) return;
            __result=new CompletionEnumerator(__result,()=>{ try { self.Complete(); } finally { self.CaptureSupportDetails();self.LoadingFinished=true;self.ready(); } },ex=>{self.LoadingFinished=true;self.Fail("Blueprinter loading failed: "+ex.Message);self.CaptureSupportDetails();});
        }
        private sealed class CompletionEnumerator : IEnumerator,IDisposable
        {
            private readonly IEnumerator inner; private readonly Action done; private readonly Action<Exception> failed; private bool finished;
            internal CompletionEnumerator(IEnumerator inner,Action done,Action<Exception> failed) { this.inner=inner;this.done=done;this.failed=failed; }
            public object Current=>inner.Current;
            public bool MoveNext()
            {
                if(finished) return false;
                try { if(inner.MoveNext()) return true; }
                catch(Exception ex) { finished=true; failed(ex); throw; }
                finished=true;
                try { done(); }catch(Exception ex) { failed(ex); }
                return false;
            }
            public void Reset()=>throw new NotSupportedException();
            public void Dispose() { if(!finished) { finished=true; failed(new OperationCanceledException()); } (inner as IDisposable)?.Dispose(); }
        }
        private void Fail(string reason) { failed=true; Ready=false; Unavailable=reason; foreach(var item in Content) item.Reloadable=false; log(reason); }
        private void Complete()
        {
            if(failed) return;
            if(baseline==null||registry==null||bundles.Count==0 && Content.Count!=0) { Fail("The loader did not finish its normal setup. Restart is required."); return; }
            try
            {
                if(initialRunner==null){Fail("Blueprinter's completed patch inventory is unavailable. Live reload is unavailable.");return;}
                int unresolved=((IList)Get(initialRunner,"DeferredPatches")).Count;
                if(unresolved>0){Fail("Blueprinter left "+unresolved+" asset patches unresolved. Live reload is unavailable. Restart may not fix missing assets; copy report for details.");return;}
                ownedLocators.AddRange(Addressables.ResourceLocators.Cast<object>().Where(l=>!baseLocators.Contains(l)));
                expected=baseline.CaptureCurrent(); Ready=true; Unavailable="";
                foreach(var mod in Content) mod.Reloadable=bundles.Any(b=>(string)Get(b,"bundleName")==mod.Id);
            } catch(Exception ex) { Fail(ex.Message); }
        }
        private void CaptureSupportDetails()
        {
            const int maximumPatches=12;
            var details=new List<string>();
            try
            {
                string? signature=plugin==null?null:AccessTools.Field(plugin.GetType(),"BundlesSignature")?.GetValue(null) as string;
                details.Add("Blueprinter selected signature: "+SupportText(signature,1800)+(signature?.Length>1800?" [truncated]":""));
                details.Add("Blueprinter recorded game version: "+SupportText(GameVersion,180));
                details.Add("Blueprinter content snapshot: known="+Content.Count+"; selected="+Content.Count(c=>c.Enabled)+"; prepared="+bundles.Count+"; reload ready="+Ready);
                var deferred=ReadField(initialRunner,"DeferredPatches") as IList;
                details.Add("Blueprinter unresolved patches: "+(deferred==null?"unavailable":deferred.Count.ToString()));
                if(deferred!=null)
                {
                    for(int i=0;i<Math.Min(deferred.Count,maximumPatches);i++)
                    {
                        var entry=deferred[i];var bundle=ReadField(entry,"Item1");var patch=ReadField(entry,"Item2");
                        var manifest=ReadField(bundle,"Manifest");var location=ReadField(patch,"GameAsset");
                        string line="  Unresolved patch: bundle="+SupportText(ReadField(bundle,"bundleName"),100)+"; mod="+SupportText(ReadField(manifest,"modName"),100)+" "+SupportText(ReadField(manifest,"modVersion"),32)+"; game target="+SupportLocation(location);
                        var targets=ReadField(patch,"PatchLocations") as IList;
                        if(targets!=null)
                        {
                            line+="; patch locations="+targets.Count;
                            for(int n=0;n<Math.Min(targets.Count,2);n++)line+="; destination="+SupportLocation(targets[n]);
                        }
                        details.Add(line.Length<=2200?line:line.Substring(0,2200)+" [truncated]");
                    }
                    if(deferred.Count>maximumPatches)details.Add("  Additional unresolved patches omitted: "+(deferred.Count-maximumPatches));
                }
            }
            catch(Exception ex){details.Add("Blueprinter support snapshot unavailable: "+SupportText(ex.GetType().Name,80));}
            SupportDetails=details.ToArray();
        }
        private static object? ReadField(object? value,string field)=>value==null?null:AccessTools.Field(value.GetType(),field)?.GetValue(value);
        private static string SupportText(object? value,int limit)
        {
            string text=value as string??"unknown";
            return new string(text.Take(limit).Select(c=>char.IsControl(c)?' ':c).ToArray());
        }
        private static string SupportLocation(object? location)
        {
            var asset=ReadField(location,"asset");
            return SupportText(ReadField(location,"id"),180)+" ["+SupportText(ReadField(asset,"locator"),180)+":"+SupportText(ReadField(asset,"name"),100)+"; type="+SupportText(ReadField(asset,"type"),80)+"; path="+SupportText(ReadField(location,"hierarchyPath"),180)+"; component="+SupportText(ReadField(location,"componentType"),100)+"; index="+(ReadField(location,"componentIndex") is int index?index.ToString():"unknown")+"; member="+SupportText(ReadField(location,"memberPath"),100)+"]";
        }
        private void CaptureHashes(IEnumerable loaded)
        {
            var initiallyVisible=Resources.FindObjectsOfTypeAll<NetworkIdentity>().Where(x=>x).ToDictionary(x=>x,x=>x.PrefabHash);
            initialIdentities=initiallyVisible.Keys.ToArray();
            // CaptureHashes loads Unity prefabs. Keep Blueprinter's original
            // reservation view even if any later validation or load fails.
            // HashPostfix/HashFinalizer clear this guard after native allocation.
            assigningInitial=true;
            var source=loaded.Cast<object>().ToArray();
            var allowed=new HashSet<string>(new[]{"OpAddToEncyclopedia","OpAddAircraftToHangars","OpAddToHangar","OpFindAircraftToHangar","OpAddLoadingScreens","OpAddWeaponToHardpoint","OpAddWeaponMountToWeaponManager","OpAddMissions"});
            // Reject unsupported live-reload operations before loading any
            // bundle's prefabs. Normal Blueprinter loading still continues.
            foreach(var bundle in source)
            {
                var manifest=Get(bundle,"Manifest");
                var ops=Get(manifest,"Ops") as IEnumerable;
                if(ops!=null)foreach(var op in ops)if(!allowed.Contains((string)Get(op,"opId")))
                    throw new InvalidOperationException((string)Get(manifest,"modName")+" uses an unsupported loader operation.");
            }
            foreach(var bundle in source)
            {
                bundles.Add(bundle); string id=(string)Get(bundle,"bundleName"); var manifest=Get(bundle,"Manifest");
                var mod=Content.SingleOrDefault(c=>c.Id==id)??new InstalledContent { Id=id,Name=(string)Get(manifest,"modName"),Version=(string)Get(manifest,"modVersion"),Source=(string)Get(bundle,"source") };if(!Content.Contains(mod)){ObserveSource(mod);Content.Add(mod);}
                var asset=(AssetBundle)Get(bundle,"AssetBundle"); var entries=new List<(NetworkIdentity,PrefabHashInput)>();
                foreach(var go in asset.LoadAllAssets<GameObject>().Where(x=>x))
                    foreach(var identity in go.GetComponentsInChildren<NetworkIdentity>(true).Where(x=>x))
                    {
                        var path=new Stack<string>(); for(var t=identity.transform;t!=null;t=t.parent) { path.Push(t.name); if(t==go.transform) break; }
                        entries.Add((identity,new PrefabHashInput { Key=id+"|"+go.name+"|"+string.Join("/",path),Sibling=identity.transform.GetSiblingIndex(),Original=identity.PrefabHash,InitiallyVisible=initiallyVisible.ContainsKey(identity) }));
                    }
                identities.Add(bundle,entries);
            }
            var owned=new HashSet<NetworkIdentity>(identities.Values.SelectMany(x=>x).Select(x=>x.Identity));
            nativeHashes.AddRange(initiallyVisible.Where(x=>!owned.Contains(x.Key)).Select(x=>x.Value));
        }
        private void ObserveSource(InstalledContent item,LoadedContentAssembly[]? sources=null)
        {
            // Record only cheap file stats during discovery. Later list saves hash
            // off-thread and refuse to bind old live metadata to replacement files.
            try
            {
                string path=item.Source;
                if(path.StartsWith("resource:",StringComparison.Ordinal))
                {
                    path=ContentSourceFiles.Resolve(BepInEx.Paths.PluginPath,path,"",sources??ModCatalog.LoadedSourceAssemblies(log));
                }
                var file=new FileInfo(path);if(!file.Exists)return;
                item.ObservedSourceLength=file.Length;item.ObservedSourceWriteTicks=file.LastWriteTimeUtc.Ticks;
            }
            catch { }
        }
        private ModStateSnapshot CaptureState(Encyclopedia encyclopedia)
        {
            var snapshot=new ModStateSnapshot();
            foreach(var name in new[]{"aircraft","vehicles","missiles","buildings","ships","scenery","otherUnits","weaponMounts","IndexLookup","Lookup","WeaponLookup"}) snapshot.Field(encyclopedia,typeof(Encyclopedia),name,true,name!="IndexLookup");
            snapshot.Field(encyclopedia,typeof(Encyclopedia),"aircraftAndVehiclesCache",false);
            snapshot.Field(null,typeof(Encyclopedia),"massLookup",false);
            foreach(var wm in Resources.FindObjectsOfTypeAll<WeaponManager>().Where(x=>x))
                if(wm.hardpointSets!=null) foreach(var set in wm.hardpointSets.Where(x=>x!=null)) snapshot.Field(set,set.GetType(),"weaponOptions");
            foreach(var screen in Resources.FindObjectsOfTypeAll<LoadingScreen>().Where(x=>x)) snapshot.Field(screen,typeof(LoadingScreen),"images");
            foreach(var type in new[]{"OpAddAircraftToHangarsHandler","OpAddToHangarHandler","OpFindAircraftToHangarHandler"}) snapshot.Field(null,assembly!.GetType("Blueprinter.Ops."+type,true)!,"Pending");
            var groups=(IDictionary)GetStatic(assembly!.GetType("Blueprinter.Ops.OpAddMissionsHandler",true)!,"resourceGroups");
            foreach(var group in groups.Values) { snapshot.Field(group,group.GetType(),"assets"); snapshot.Field(group,group.GetType(),"names"); }
            snapshot.Value(()=>Addressables.ResourceManager.ResourceProviders,v=>{ throw new InvalidOperationException("Resource provider collection was replaced."); });
            foreach(var definition in Resources.FindObjectsOfTypeAll<UnitDefinition>().Cast<INetworkDefinition>().Concat(Resources.FindObjectsOfTypeAll<WeaponMount>().Cast<INetworkDefinition>()))
            { var d=definition; snapshot.Value(()=>d.LookupIndex,v=>d.LookupIndex=(int?)v,false); }
            foreach(var entry in identities.Values.SelectMany(x=>x)) { var identity=entry.Identity; snapshot.Value(()=>identity.PrefabHash,v=>identity.PrefabHash=(int)v!,false); }
            return snapshot;
        }
        internal string Apply(IEnumerable<string> disabledNames)
        {
            if(!Ready||failed||baseline==null||expected==null) return Unavailable.Length>0?Unavailable:"Mod reload is unavailable.";
            string safety=ModManager.MenuSafety(); if(safety.Length>0) return safety;
            if(!expected.Unchanged()) return "Another plugin changed the shared mod lists. Restart the game to apply this safely.";
            var disabled=new HashSet<string>(disabledNames,StringComparer.Ordinal);
            var selected=bundles.Where(b=>!disabled.Contains((string)Get(b,"bundleName"))).ToList();
            var rollback=baseline.CaptureCurrent(); var oldSignature=(string)GetStatic(plugin!.GetType(),"BundlesSignature");
            var oldLocators=Addressables.ResourceLocators.ToArray(); var oldBundles=((IList)Get(registry!,"Bundles")).Cast<object>().ToArray();
            var oldOwnedLocators=ownedLocators.ToArray();
            rebuilding=true;
            try
            {
                baseline.Restore();
                SetBundles(selected); AssignHashes(selected);
                // Every bundle was wired by the normal initial load. Reload only
                // registrations; rerunning asset patching can mutate retained prefabs.
                var runner=Activator.CreateInstance(runnerType!,new object?[]{registry,null})!;
                var progress=(IDictionary)Get(runner,"progress"); var progressType=runnerType!.GetNestedType("Progress",BindingFlags.NonPublic)!;
                foreach(var bundle in selected)
                {
                    var value=Activator.CreateInstance(progressType)!;
                    AccessTools.Field(progressType,"Total").SetValue(value,AccessTools.Method(runnerType!,"GetTotalWork").Invoke(null,new[]{Get(bundle,"Manifest")}));
                    progress.Add(bundle,value);
                }
                reloadWarning="";
                AccessTools.Method(runnerType!,"ApplyAllOps").Invoke(runner,new object[]{Encyclopedia.i});
                if(reloadWarning.Length>0) throw new InvalidOperationException(reloadWarning);
                foreach(var locator in Addressables.ResourceLocators.ToArray()) if(ownedLocators.Contains(locator)) Addressables.RemoveResourceLocator(locator);
                var beforeLocators=Addressables.ResourceLocators.Cast<object>().ToArray();
                AccessTools.Method(plugin!.GetType(),"RegisterLiveries").Invoke(plugin,new object[]{Get(registry!,"Bundles")});
                ownedLocators.Clear(); ownedLocators.AddRange(Addressables.ResourceLocators.Cast<object>().Where(l=>!beforeLocators.Contains(l)));
                string signature=selected.Count==0?"NOBUNDLES":string.Join("_",selected.Select(b=>"--"+Get(b,"bundleName")+"-v"+Get(Get(b,"Manifest"),"modVersion")));
                AccessTools.Field(plugin!.GetType(),"BundlesSignature").SetValue(null,signature);
                foreach(var mod in Content) mod.Enabled=!disabled.Contains(mod.Id);
                expected=baseline.CaptureCurrent();
                return "";
            }
            catch(Exception ex)
            {
                try
                {
                    rollback.Restore(); SetBundles(oldBundles);
                    AccessTools.Field(plugin!.GetType(),"BundlesSignature").SetValue(null,oldSignature);
                    foreach(var locator in Addressables.ResourceLocators.ToArray()) if(!oldLocators.Contains(locator)) Addressables.RemoveResourceLocator(locator);
                    foreach(var locator in oldLocators) if(!Addressables.ResourceLocators.Contains(locator)) Addressables.AddResourceLocator(locator);
                    ownedLocators.Clear(); ownedLocators.AddRange(oldOwnedLocators);
                    expected=baseline.CaptureCurrent();
                } catch(Exception restore) { Fail("Mod reset failed. Restart the game before playing. "+restore.GetType().Name); }
                string reason=(ex.InnerException??ex).Message; log("Reload rejected: "+reason); return reason;
            }
            finally { rebuilding=false;CaptureSupportDetails(); }
        }
        private void SetBundles(IEnumerable<object> selected) { var list=(IList)Get(registry!,"Bundles"); list.Clear(); foreach(var item in selected) list.Add(item); }
        private void AssignHashes(List<object> selected)
        {
            foreach(var entry in identities.Values.SelectMany(x=>x)) entry.Identity.PrefabHash=0;
            var entries=selected.SelectMany(b=>identities[b]).ToArray();
            var values=ContentPrefabHashes.Assign(nativeHashes,entries.Select(e=>e.Input));
            foreach(var entry in entries) entry.Identity.PrefabHash=values[entry.Input];
        }
        private static object Get(object value,string field) => AccessTools.Field(value.GetType(),field)?.GetValue(value) ?? throw new MissingFieldException(value.GetType().FullName,field);
        private static object GetStatic(Type type,string field) => AccessTools.Field(type,field)?.GetValue(null) ?? throw new MissingFieldException(type.FullName,field);
        internal static void Dispose() { instance=null; }
    }
}
