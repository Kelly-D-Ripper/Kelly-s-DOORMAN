using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using Mono.Cecil;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed class GameplayFingerprintSource
    {
        public string Kind="",Id="",Version="",Path="",AllowedRoot="",ModuleVersionId="",ContentLoadIdentity="";
        public bool Staged=false;
    }
    // A fresh read is still required on every explicit generation. This is an
    // immutable consistency baseline for loaded DLLs, never a reusable hash
    // cache: new disk bytes must not be reported as already loaded code.
    internal sealed class GameplayLoadedFileGuard
    {
        private readonly Dictionary<string,string> observed=new Dictionary<string,string>(System.IO.Path.DirectorySeparatorChar=='\\'?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        private readonly Dictionary<string,(string Identity,string Hash)> contentObserved=new Dictionary<string,(string Identity,string Hash)>(System.IO.Path.DirectorySeparatorChar=='\\'?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        internal GameplayFingerprintSnapshot Apply(IEnumerable<GameplayFingerprintSource> sources,GameplayFingerprintSnapshot snapshot)
        {
            if(!snapshot.Ready||!snapshot.Complete)return snapshot;
            try
            {
                if(!GameplayFingerprintPolicy.Normalize(snapshot,out var normalized,out string error))throw new InvalidDataException(error);
                var records=normalized.Records.ToDictionary(r=>r.Kind+"\n"+r.Id,StringComparer.Ordinal);
                var additions=new Dictionary<string,string>(observed.Comparer);
                var contentAdditions=new Dictionary<string,(string Identity,string Hash)>(observed.Comparer);
                var input=sources.Take(GameplayFingerprintPolicy.MaxRecords+1).ToArray();
                if(input.Length>GameplayFingerprintPolicy.MaxRecords)throw new InvalidDataException("Loaded gameplay inventory exceeds its safe limit.");
                foreach(var source in input)
                {
                    if(source==null||source.ModuleVersionId==null||source.ContentLoadIdentity==null)throw new InvalidDataException("A loaded gameplay source is invalid.");
                    if(source.Staged)continue;
                    if(source.ModuleVersionId.Length==0)
                    {
                        if(source.ContentLoadIdentity.Length==0)continue;
                        if(source.Kind!="content"||!Guid.TryParseExact(source.ContentLoadIdentity,"N",out _)
                            ||!System.IO.Path.IsPathRooted(source.Path)||!records.TryGetValue(source.Kind+"\n"+source.Id,out var content))
                            throw new InvalidDataException("A loaded content source has no verified load identity or file evidence.");
                        string contentKey=System.IO.Path.GetFullPath(source.Path);
                        if(contentObserved.TryGetValue(contentKey,out var loaded)&&loaded.Identity==source.ContentLoadIdentity&&loaded.Hash!=content.Hash
                            ||contentAdditions.TryGetValue(contentKey,out var concurrentContent)
                                &&(concurrentContent.Identity!=source.ContentLoadIdentity||concurrentContent.Hash!=content.Hash))
                            throw new InvalidDataException("Loaded content file '"+source.Id+"' changed on disk. Restart before comparing it.");
                        contentAdditions[contentKey]=(source.ContentLoadIdentity,content.Hash);
                        continue;
                    }
                    if(!Guid.TryParse(source.ModuleVersionId,out var mvid)||!System.IO.Path.IsPathRooted(source.Path)||!records.TryGetValue(source.Kind+"\n"+source.Id,out var record))throw new InvalidDataException("A loaded gameplay source has no verified file evidence.");
                    string key=System.IO.Path.GetFullPath(source.Path)+"\n"+mvid.ToString("D");
                    if(observed.TryGetValue(key,out var previous)&&previous!=record.Hash||additions.TryGetValue(key,out var concurrent)&&concurrent!=record.Hash)
                        throw new InvalidDataException("Loaded gameplay DLL '"+source.Id+"' changed on disk. Restart before comparing it.");
                    if(!observed.ContainsKey(key))additions[key]=record.Hash;
                }
                if(observed.Count+additions.Count+contentObserved.Count+contentAdditions.Keys.Count(key=>!contentObserved.ContainsKey(key))>GameplayFingerprintPolicy.MaxRecords*2)throw new InvalidDataException("Loaded gameplay file history exceeds its safe limit. Restart before comparing it.");
                // Failed snapshots never establish or change a baseline.
                foreach(var pair in additions)observed.Add(pair.Key,pair.Value);
                foreach(var pair in contentAdditions)contentObserved[pair.Key]=pair.Value;
                return normalized;
            }
            catch(Exception ex) when(ex is InvalidDataException||ex is ArgumentException||ex is NotSupportedException)
            {return GameplayFingerprintSnapshot.Unavailable("Gameplay loaded-file verification unavailable: "+ex.Message);}
        }
    }
    internal sealed class GameplayFingerprintDriver : MonoBehaviour { }

    // Unity/plugin observations are captured on the main thread. Only bounded
    // file reads and metadata verification run in the background. No Update,
    // directory scan, remote lookup or idle filesystem polling is used.
    internal sealed class GameplayFingerprintInventory : IDisposable
    {
        private sealed class Observation
        {
            internal string Path="",Hash="",ModuleVersionId="";
            internal long Length,Written,Created;
        }
        private static GameplayFingerprintInventory? active;
        // Internal collector semantics are versioned independently of Steam's
        // compatible protocol. A new client must not borrow an older server
        // companion's collector that has no standalone loaded-content guard.
        private const string SharedKey="KellysDOORMAN.GameplayFingerprints."+GameplayFingerprintPolicy.Scope+".v3.loaded-content-v2";
        private readonly List<Action> observers=new List<Action>();
        private readonly Action observer;
        private object? primary;
        private object? primaryValue;
        private GameplayFingerprintSnapshot? primaryCopy;
        private MonoBehaviour? driver;
        private readonly MonoBehaviour owner;
        private readonly Action<string> log;
        private readonly Action? changed;
        private readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        private readonly GameplayLoadedFileGuard loadedFiles=new GameplayLoadedFileGuard();
        // The registry's bundle object represents a particular successful load.
        // A content reload creates a new object; rehashing the same object must
        // never relabel edited on-disk .nobp bytes as already loaded content.
        private sealed class ContentLoadIdentity { internal readonly string Value=Guid.NewGuid().ToString("N"); }
        private readonly ConditionalWeakTable<object,ContentLoadIdentity> contentLoads=new ConditionalWeakTable<object,ContentLoadIdentity>();
        private readonly GameplayFingerprintSnapshot pending=GameplayFingerprintSnapshot.Unavailable("Gameplay fingerprints are preparing for the current mod set.");
        private GameplayFingerprintSnapshot snapshot=GameplayFingerprintSnapshot.Unavailable("Gameplay fingerprints are preparing.");
        private object? registry,runner;
        private Harmony? harmony;
        private bool hooksInstalled,hookFailed,loadingFinished,loadingFailed,running,notifying,disposed,released;
        private int requested;
        private string snapshotWire="";
        private static StringComparer PathComparer=>System.IO.Path.DirectorySeparatorChar=='\\'?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
        private static StringComparison PathComparison=>System.IO.Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
        internal string CurrentWire=>primary==null?snapshotWire:(string)(Bridge("SharedWire")??"");

        internal GameplayFingerprintInventory(MonoBehaviour owner,Action<string> log,Action? changed=null)
        { this.owner=owner;this.log=log;this.changed=changed;observer=changed??(()=>{}); }

        internal void Start(Harmony harmony)
        {
            if(disposed)throw new ObjectDisposedException(nameof(GameplayFingerprintInventory));
            var existing=AppDomain.CurrentDomain.GetData(SharedKey);
            if(existing!=null&&!ReferenceEquals(existing,this)&&existing.GetType().FullName==typeof(GameplayFingerprintInventory).FullName)
            {
                primary=existing;Bridge("SharedAdd",observer);return;
            }
            this.harmony=new Harmony(SharedKey);active=this;observers.Add(observer);
            var host=new GameObject("DOORMAN gameplay fingerprints"){hideFlags=HideFlags.HideAndDontSave};
            UnityEngine.Object.DontDestroyOnLoad(host);driver=host.AddComponent<GameplayFingerprintDriver>();
            AppDomain.CurrentDomain.SetData(SharedKey,this);
            InstallHooks();RequestRefresh();
        }

        internal GameplayFingerprintSnapshot ForWire(string wire)
        {
            if(primary==null)return !disposed&&wire==snapshotWire?snapshot:pending;
            var value=Bridge("SharedSnapshot",wire);
            if(value==null)return pending;
            if(ReferenceEquals(value,primaryValue)&&primaryCopy!=null)return primaryCopy;
            // The primary may live in the companion server assembly, so its
            // internal snapshot type has a different assembly identity.
            var fields=value.GetType();
            var result=new GameplayFingerprintSnapshot {Ready=fields.GetField("Ready")?.GetValue(value) is true,Complete=fields.GetField("Complete")?.GetValue(value) is true,Error=fields.GetField("Error")?.GetValue(value) as string??"",Scope=fields.GetField("Scope")?.GetValue(value) as string??"",LoaderVersion=fields.GetField("LoaderVersion")?.GetValue(value) as string??"",ContentSetHash=fields.GetField("ContentSetHash")?.GetValue(value) as string??"",PendingAssetPatches=fields.GetField("PendingAssetPatches")?.GetValue(value) is int count?count:0};
            var records=fields.GetField("Records")?.GetValue(value) as IEnumerable;
            var copied=new List<GameplayFileRecord>();
            if(records!=null)foreach(var record in records)
            {
                if(record==null||copied.Count>=GameplayFingerprintPolicy.MaxRecords)return GameplayFingerprintSnapshot.Unavailable("Shared gameplay inventory is invalid.");
                var type=record.GetType();copied.Add(new GameplayFileRecord {Kind=type.GetField("Kind")?.GetValue(record) as string??"",Id=type.GetField("Id")?.GetValue(record) as string??"",Version=type.GetField("Version")?.GetValue(record) as string??"",Hash=type.GetField("Hash")?.GetValue(record) as string??""});
            }
            result.Records=copied.ToArray();
            var selection=fields.GetField("ContentSelection")?.GetValue(value) as IEnumerable;
            var selected=new List<GameplayContentVersion>();
            if(selection!=null)foreach(var item in selection)
            {
                if(item==null||selected.Count>=GameplayFingerprintPolicy.MaxRecords)return GameplayFingerprintSnapshot.Unavailable("Shared content selection is invalid.");
                var type=item.GetType();selected.Add(new GameplayContentVersion {Id=type.GetField("Id")?.GetValue(item) as string??"",Version=type.GetField("Version")?.GetValue(item) as string??""});
            }
            result.ContentSelection=selected.ToArray();
            primaryValue=value;
            return primaryCopy=GameplayFingerprintPolicy.Normalize(result,out var normalized,out string error)?normalized:GameplayFingerprintSnapshot.Unavailable(error);
        }
        private object? Bridge(string method,params object[] args)=>primary?.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic)?.Invoke(primary,args);
        private string SharedWire()=>snapshotWire;
        private GameplayFingerprintSnapshot SharedSnapshot(string wire)=>ForWire(wire);
        private void SharedAdd(Action callback){if(disposed)throw new ObjectDisposedException(nameof(GameplayFingerprintInventory));observers.Add(callback);}
        private void SharedRemove(Action callback){observers.Remove(callback);if(observers.Count==0)Shutdown();}
        private void SharedRefresh()=>RequestRefresh();
        private void SharedCompleted()=>NotifyLoaderCompleted();

        internal void RequestRefresh()
        {
            if(disposed)return;
            if(primary!=null){Bridge("SharedRefresh");return;}
            // Observers may discover a new host wire while invalidation is
            // being published. Reserve the sole worker before notifying them.
            bool begin=!running;if(begin)running=true;
            requested++;snapshotWire="";snapshot=GameplayFingerprintSnapshot.Unavailable("Gameplay fingerprints are preparing.");
            NotifyObservers();
            if(begin&&!disposed)(driver??owner).StartCoroutine(Refresh());
        }
        private void NotifyObservers()
        {
            if(notifying||disposed)return;
            notifying=true;
            try
            {
                foreach(var callback in observers.ToArray())
                    try{callback();}catch(Exception ex){log("Gameplay fingerprint observer unavailable ("+ex.GetType().Name+").");}
            }
            finally{notifying=false;}
        }
        internal void NotifyLoaderCompleted()
        {
            if(disposed)return;
            if(primary!=null){Bridge("SharedCompleted");return;}
            loadingFinished=true;RequestRefresh();
        }

        private void InstallHooks()
        {
            if(hooksInstalled||hookFailed||harmony==null)return;
            if(!Chainloader.PluginInfos.TryGetValue("com.nikkorap.blueprinter",out var info)||info.Instance==null)return;
            try
            {
                var loaderVersion=info.Metadata.Version.ToString();
                if(loaderVersion!="2.0.1" && loaderVersion!="2.1.1")throw new NotSupportedException("Blueprinter version is not supported for exact fingerprints.");
                var assembly=info.Instance.GetType().Assembly;
                Patch(assembly.GetType("Blueprinter.BundleRegistry",true)!,"FastLoad",nameof(FastPostfix));
                Patch(assembly.GetType("Blueprinter.BundleRegistry",true)!,"ScanAndLoadCoroutine",nameof(ScanPostfix));
                Patch(assembly.GetType("Blueprinter.PatchRunner",true)!,"ApplyAllOps",nameof(OpsPostfix));
                Patch(info.Instance.GetType(),"RunRoutine",nameof(RoutinePrefix),true);
                var iterator=assembly.GetTypes().Single(t=>t.FullName!=null&&t.FullName.StartsWith("Blueprinter.Plugin+<RunRoutine>",StringComparison.Ordinal));
                Patch(iterator,"MoveNext",nameof(RoutineMoveNextPostfix));
                harmony.Patch(AccessTools.Method(iterator,"MoveNext"),finalizer:new HarmonyMethod(typeof(GameplayFingerprintInventory),nameof(RoutineFinalizer)));
                hooksInstalled=true;
            }
            catch(Exception ex){hookFailed=true;log("Gameplay fingerprint loading hooks unavailable ("+ex.GetType().Name+").");}
        }

        private void Patch(Type type,string name,string callback,bool prefix=false)
        {
            var original=AccessTools.Method(type,name)??throw new MissingMethodException(type.FullName,name);
            var hook=new HarmonyMethod(AccessTools.Method(typeof(GameplayFingerprintInventory),callback)){priority=Priority.Last};
            if(prefix)harmony!.Patch(original,prefix:hook);else harmony!.Patch(original,postfix:hook);
        }
        private static void FastPostfix(object __instance){if(active!=null)active.registry=__instance;}
        private static void ScanPostfix(object __instance,ref IEnumerator __result)
        {
            var self=active;if(self==null)return;
            __result=new FinishedEnumerator(__result,()=>self.registry=__instance,()=>self.LoadFailed());
        }
        private static void OpsPostfix(object __instance)
        {
            var self=active;if(self==null)return;
            self.runner=__instance;
            self.registry=Read(__instance,"<bundles>P")??self.registry;
        }
        private static void RoutinePrefix()
        {
            var self=active;if(self==null)return;
            self.loadingFinished=false;self.loadingFailed=false;self.runner=null;self.registry=null;self.RequestRefresh();
        }
        private static void RoutineMoveNextPostfix(object __instance,bool __result)
        {
            var self=active;if(self==null)return;
            if(__result)return;
            self.registry=Read(__instance,"<bundleRegistry>5__3")??Read(__instance,"<bundleRegistry>5__5")??self.registry;
            self.runner=Read(__instance,"<runner>5__6")??Read(__instance,"<runner>5__9")??self.runner;
            if(!self.loadingFinished){self.loadingFinished=true;self.RequestRefresh();}
        }
        private static Exception? RoutineFinalizer(Exception? __exception)
        {if(__exception!=null)active?.LoadFailed();return __exception;}
        private void LoadFailed(){loadingFailed=true;loadingFinished=true;RequestRefresh();}
        private sealed class FinishedEnumerator : IEnumerator,IDisposable
        {
            private readonly IEnumerator inner;private readonly Action done,failed;private bool finished;
            internal FinishedEnumerator(IEnumerator inner,Action done,Action failed){this.inner=inner;this.done=done;this.failed=failed;}
            public object Current=>inner.Current;
            public bool MoveNext()
            {
                if(finished)return false;
                try{if(inner.MoveNext())return true;finished=true;done();return false;}
                catch{finished=true;failed();throw;}
            }
            public void Reset()=>throw new NotSupportedException();
            public void Dispose(){if(!finished){finished=true;failed();}(inner as IDisposable)?.Dispose();}
        }

        private IEnumerator Refresh()
        {
            // Let Chainloader finish instantiating plugins before observing its
            // loaded inventory. The bounded startup wait reads no files.
            yield return null;
            try
            {
                while(!disposed)
                {
                    int generation=requested;int waits=0;
                    InstallHooks();
                    while(!disposed&&Chainloader.PluginInfos.ContainsKey("com.nikkorap.blueprinter")&&!loadingFinished&&!hookFailed&&waits++<300)
                    {yield return new WaitForSecondsRealtime(1);InstallHooks();}
                    if(disposed)yield break;
                    string wire=Compatibility.Wire;
                    GameplayFingerprintSource[]? sources=null;GameplayFingerprintSnapshot? result=null;string loaderVersion="";int pendingAssetPatches=0;
                    try{sources=Capture(out loaderVersion,out pendingAssetPatches);}
                    catch(Exception ex){result=GameplayFingerprintSnapshot.Unavailable("Gameplay file inventory unavailable: "+ex.Message);}
                    if(sources!=null)
                    {
                        // Cache bytes only within this explicit generation.
                        // Preserved timestamps must not hide edited file data.
                        var work=Task.Run(()=>
                        {
                            var files=ReadFiles(sources,lifetime.Token,loaderVersion);
                            // Loading health is separate from complete file coverage.
                            // Pending assets never substitute for source provenance,
                            // full-byte hashing or loaded-module consistency checks.
                            if(files.Ready&&files.Complete)files.PendingAssetPatches=pendingAssetPatches;
                            return loadedFiles.Apply(sources,files);
                        },lifetime.Token);
                        while(!work.IsCompleted&&!disposed)yield return null;
                        if(disposed)yield break;
                        if(work.Status==TaskStatus.RanToCompletion)result=work.Result;
                        else result=GameplayFingerprintSnapshot.Unavailable("Gameplay file verification did not complete.");
                    }
                    if(generation!=requested||wire!=Compatibility.Wire)continue;
                    snapshot=result??GameplayFingerprintSnapshot.Unavailable("Gameplay file inventory is unavailable.");snapshotWire=wire;
                    if(!snapshot.Complete)log(snapshot.Error);
                    else
                    {
                        log("Exact mod file inventory ready: "+snapshot.Records.Length+" identified file records. Asset application is reported separately.");
                        if(snapshot.PendingAssetPatches>0)log(AssetLoadingDiagnostics.Notice(snapshot.PendingAssetPatches));
                    }
                    NotifyObservers();
                    if(generation==requested)yield break;
                }
            }
            finally{running=false;}
        }

        private GameplayFingerprintSource[] Capture(out string loaderVersion,out int pendingAssetPatches)
        {
            loaderVersion="";pendingAssetPatches=0;
            var result=new List<GameplayFingerprintSource>();
            foreach(var info in Chainloader.PluginInfos.Values.ToArray())
            {
                if(info.Instance==null||!GameplayFingerprintPolicy.IsAuditedPlugin(info.Metadata.GUID))continue;
                var assembly=info.Instance.GetType().Assembly;
                result.Add(new GameplayFingerprintSource {Kind="plugin",Id=info.Metadata.GUID,Version=info.Metadata.Version.ToString(),Path=assembly.Location,AllowedRoot=Paths.PluginPath,ModuleVersionId=assembly.ManifestModule.ModuleVersionId.ToString("D")});
            }
            if(Chainloader.PluginInfos.TryGetValue("com.nikkorap.blueprinter",out var loader))
            {
                loaderVersion=loader.Metadata.Version.ToString();
                if(hookFailed||!hooksInstalled||!loadingFinished||loadingFailed)throw new InvalidDataException("Blueprinter loading is incomplete or unsupported.");
                if(registry==null||Read(registry,"ScanSucceeded") is not bool success||!success)throw new InvalidDataException("Blueprinter did not supply a completed content inventory.");
                if(runner==null||Read(runner,"DeferredPatches") is not IList deferred)throw new InvalidDataException("Blueprinter deferred patch inventory is unavailable.");
                var inventory=Read(registry,"Bundles") as IEnumerable??throw new InvalidDataException("Blueprinter content inventory is unavailable.");
                var bundles=inventory.Cast<object>().Take(GameplayFingerprintPolicy.MaxRecords+1).ToArray();
                if(bundles.Length>GameplayFingerprintPolicy.MaxRecords)throw new InvalidDataException("The selected gameplay inventory exceeds its safe limit.");
                pendingAssetPatches=AssetLoadingDiagnostics.CapturePendingCount(runner,bundles);
                foreach(var bundle in bundles)
                {
                    if(bundle==null)throw new InvalidDataException("Blueprinter returned an invalid content entry.");
                    var manifest=Read(bundle,"Manifest")??throw new InvalidDataException("Blueprinter content metadata is unavailable.");
                    string id=Read(bundle,"bundleName") as string??"",version=Read(manifest,"modVersion") as string??"",source=Read(bundle,"source") as string??"";
                    var item=new GameplayFingerprintSource {Kind="content",Id=id,Version=version,AllowedRoot=Paths.PluginPath};
                    if(source.StartsWith("resource:",StringComparison.Ordinal))
                    {
                        string[] parts=source.Split(':');
                        if(parts.Length!=3||!parts[2].EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("An embedded content source is invalid.");
                        var assemblies=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name==parts[1]&&a.GetManifestResourceNames().Contains(parts[2],StringComparer.Ordinal)).Take(2).ToArray();
                        if(assemblies.Length!=1)throw new InvalidDataException("An embedded content source is missing or ambiguous.");
                        item.Path=assemblies[0].Location;item.ModuleVersionId=assemblies[0].ManifestModule.ModuleVersionId.ToString("D");
                    }
                    else
                    {
                        if(!source.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("A standalone content source is invalid.");
                        item.Path=source;item.ContentLoadIdentity=contentLoads.GetValue(bundle,_=>new ContentLoadIdentity()).Value;
                    }
                    result.Add(item);
                    if(result.Count>GameplayFingerprintPolicy.MaxRecords)throw new InvalidDataException("The selected gameplay inventory exceeds its safe limit.");
                }
            }
            return result.ToArray();
        }

        private static object? Read(object value,string field)=>AccessTools.Field(value.GetType(),field)?.GetValue(value);

        internal static GameplayFingerprintSnapshot ReadFiles(IEnumerable<GameplayFingerprintSource> sources,CancellationToken token,string loaderVersion="")
            =>ReadFiles(sources,token,loaderVersion,new Dictionary<string,Observation>(PathComparer));

        private static GameplayFingerprintSnapshot ReadFiles(IEnumerable<GameplayFingerprintSource> sources,CancellationToken token,string loaderVersion,Dictionary<string,Observation> cache)
        {
            try
            {
                var input=(sources??throw new InvalidDataException("Gameplay sources are missing.")).Take(GameplayFingerprintPolicy.MaxRecords+1).ToArray();
                if(input.Length>GameplayFingerprintPolicy.MaxRecords)throw new InvalidDataException("Too many gameplay files.");
                var records=new List<GameplayFileRecord>();var unique=new HashSet<string>(StringComparer.Ordinal);
                foreach(var source in input)
                {
                    token.ThrowIfCancellationRequested();
                    if(source==null||source.Kind!="plugin"&&source.Kind!="content"||!GameplayFingerprintPolicy.Text(source.Id,GameplayFingerprintPolicy.MaxId)||!GameplayFingerprintPolicy.Text(source.Version,GameplayFingerprintPolicy.MaxVersion)||source.ModuleVersionId==null||source.ModuleVersionId.Length>36||source.ModuleVersionId.Length>0&&!Guid.TryParse(source.ModuleVersionId,out _)||source.Kind=="plugin"&&!GameplayFingerprintPolicy.IsAuditedPlugin(source.Id))throw new InvalidDataException("Invalid gameplay file identity.");
                    if(!unique.Add(source.Kind+"\n"+source.Id))throw new InvalidDataException("Duplicate gameplay file identity.");
                    string path=Physical(source.AllowedRoot,source.Path,source.Staged);
                    var before=Observe(path,source.ModuleVersionId);
                    if(!cache.TryGetValue(path,out var previous)||!Same(before,previous))
                    {
                        using(var inputFile=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,64*1024,FileOptions.SequentialScan))
                        {
                            InstalledFilePolicy.ValidateLength(inputFile.Length);
                            if(inputFile.Length!=before.Length)throw new IOException("A gameplay file changed during verification.");
                            if(source.Kind=="plugin"||source.Staged||source.ModuleVersionId.Length>0||path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))VerifyDll(source,inputFile);
                            inputFile.Position=0;
                            using(var hash=System.Security.Cryptography.SHA256.Create())
                            {
                                var buffer=new byte[64*1024];int read;long total=0;
                                while((read=inputFile.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();hash.TransformBlock(buffer,0,read,buffer,0);total+=read;}
                                hash.TransformFinalBlock(Array.Empty<byte>(),0,0);
                                if(total!=before.Length||inputFile.Length!=before.Length)throw new IOException("A gameplay file changed during verification.");
                                before.Hash=BitConverter.ToString(hash.Hash!).Replace("-","").ToLowerInvariant();
                            }
                        }
                        Physical(source.AllowedRoot,path,source.Staged);
                        if(!Same(before,Observe(path,source.ModuleVersionId)))throw new IOException("A gameplay file changed during verification.");
                        cache[path]=before;previous=before;
                    }
                    else if(source.Kind=="plugin")
                    {
                        // A DLL can provide several plugin GUIDs. Reused file
                        // bytes still need each requested plugin identity checked.
                        using(var metadataFile=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))VerifyDll(source,metadataFile);
                    }
                    Physical(source.AllowedRoot,path,source.Staged);
                    if(!Same(before,Observe(path,source.ModuleVersionId)))throw new IOException("A gameplay file changed during verification.");
                    records.Add(new GameplayFileRecord {Kind=source.Kind,Id=source.Id,Version=source.Version,Hash=previous.Hash});
                }
                var snapshot=new GameplayFingerprintSnapshot {Ready=true,Complete=true,LoaderVersion=loaderVersion,Records=records.ToArray()};
                if(!GameplayFingerprintPolicy.Normalize(snapshot,out var normalized,out string error))throw new InvalidDataException(error);
                return normalized;
            }
            catch(OperationCanceledException){throw;}
            catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is NotSupportedException||ex is BadImageFormatException)
            {return GameplayFingerprintSnapshot.Unavailable("Gameplay file verification unavailable: "+ex.Message);}
        }

        private static Observation Observe(string path,string mvid)
        {
            var file=new FileInfo(path);file.Refresh();
            if(!file.Exists)throw new IOException("A gameplay file moved or disappeared after loading.");
            InstalledFilePolicy.ValidateLength(file.Length);
            return new Observation {Path=path,Length=file.Length,Written=file.LastWriteTimeUtc.Ticks,Created=file.CreationTimeUtc.Ticks,ModuleVersionId=mvid};
        }
        private static bool Same(Observation a,Observation b)=>PathComparer.Equals(a.Path,b.Path)&&a.Length==b.Length&&a.Written==b.Written&&a.Created==b.Created&&a.ModuleVersionId==b.ModuleVersionId;

        private static string Physical(string root,string path,bool staged)
        {
            if(string.IsNullOrWhiteSpace(root)||string.IsNullOrWhiteSpace(path)||root.Length>4096||path.Length>4096||!System.IO.Path.IsPathRooted(root)||!System.IO.Path.IsPathRooted(path)||root.Any(char.IsControl)||path.Any(char.IsControl))throw new InvalidDataException("A gameplay file has no safe installed location.");
            string fullRoot=System.IO.Path.GetFullPath(root).TrimEnd(System.IO.Path.DirectorySeparatorChar,System.IO.Path.AltDirectorySeparatorChar);
            string full=System.IO.Path.GetFullPath(path);
            bool supported=full.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||full.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)||staged&&System.IO.Path.GetFileName(full)=="payload.bin";
            if(!full.StartsWith(fullRoot+System.IO.Path.DirectorySeparatorChar,PathComparison)||!supported)throw new InvalidDataException("A gameplay file is outside its expected mod folder or has an unsupported type.");
            for(string? walk=full;walk!=null;walk=System.IO.Path.GetDirectoryName(walk))
            {
                var attributes=File.GetAttributes(walk);
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked gameplay files require manual verification.");
                if(PathComparer.Equals(walk,fullRoot))break;
            }
            return full;
        }

        private static void VerifyDll(GameplayFingerprintSource source,Stream input)
        {
            if(input.Length>InstalledFilePolicy.MaximumMetadataDllBytes)throw new InvalidDataException("A gameplay DLL exceeds the safe metadata inspection limit.");
            // In-memory copy is deliberately avoided for large mod DLLs.
            using(var assembly=AssemblyDefinition.ReadAssembly(input,new ReaderParameters {ReadSymbols=false,ReadingMode=ReadingMode.Deferred}))
            {
                if(source.ModuleVersionId.Length>0&&assembly.MainModule.Mvid.ToString("D")!=source.ModuleVersionId)throw new InvalidDataException("A loaded gameplay DLL was replaced on disk. Restart before comparing it.");
                if(source.Kind!="plugin")return;
                var matching=Types(assembly.MainModule.Types).SelectMany(t=>t.CustomAttributes).Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin"&&a.ConstructorArguments.Count==3&&a.ConstructorArguments[0].Value as string==source.Id).ToArray();
                if(matching.Length!=1||!Version.TryParse(matching[0].ConstructorArguments[2].Value as string,out var version)||version.ToString()!=source.Version)throw new InvalidDataException("The installed gameplay DLL does not match its loaded plugin identity and version.");
            }
        }
        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
        {foreach(var type in roots){yield return type;foreach(var nested in Types(type.NestedTypes))yield return nested;}}

        public void Dispose()
        {if(released)return;released=true;if(primary!=null){Bridge("SharedRemove",observer);return;}SharedRemove(observer);}
        private void Shutdown()
        {
            if(disposed)return;disposed=true;lifetime.Cancel();harmony?.UnpatchSelf();
            if(ReferenceEquals(active,this))active=null;
            if(ReferenceEquals(AppDomain.CurrentDomain.GetData(SharedKey),this))AppDomain.CurrentDomain.SetData(SharedKey,null);
            if(driver!=null)UnityEngine.Object.Destroy(driver.gameObject);
        }
    }
}
