using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using KellysJOINCHECK;

internal static class DisabledRestoreQueueRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string temporary=Path.Combine(Path.GetTempPath(),"doorman-disabled-queue-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        try
        {
            PersistenceAndApply(check,temporary);
            Validation(check,temporary);
            EmptyQueueRecovery(check,temporary);
            SourceMapping(check,temporary);
        }
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(temporary).StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(temporary))Directory.Delete(temporary,true);
        }
    }
    private static string Config(string temporary,string name)=>Path.Combine(temporary,name,"config");
    private static string Bep(string temporary,string name)
    {string root=Path.Combine(temporary,name,"BepInEx");Directory.CreateDirectory(Path.Combine(root,"disabledPlugins"));return root;}
    private static string Disabled(string bep,string relative)=>Path.GetFullPath(Path.Combine(bep,"disabledPlugins",relative));
    private static string Active(string bep,string relative)=>Path.GetFullPath(Path.Combine(bep,"plugins",relative));
    private static string Queue(string config)=>Path.Combine(config,DisabledRestoreQueue.Filename);
    private static void FileAt(string path,string text)
    {Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text);}
    private static DisabledModRestore Restore(string bep,string name)=>DisabledModFiles.Prepare(bep,DisabledModFiles.Inventory(bep).Single(c=>c.SourceRelative==name));
    private static void Reject(Action<bool,string> check,Action action,string name)
    {
        bool rejected=false;
        try{action();}catch(Exception ex) when(ex is InvalidDataException||ex is IOException||ex is ArgumentException){rejected=true;}
        check(rejected,name);
    }
    private static void WriteDocument(string config,DisabledRestoreQueueDocument document)
    {
        Directory.CreateDirectory(config);using(var output=new FileStream(Queue(config),FileMode.Create,FileAccess.Write))new DataContractJsonSerializer(typeof(DisabledRestoreQueueDocument)).WriteObject(output,document);
    }

    private static void PersistenceAndApply(Action<bool,string> check,string temporary)
    {
        string config=Config(temporary,"normal"),bep=Bep(temporary,"normal");
        FileAt(Disabled(bep,"A.dll"),"a");FileAt(Disabled(bep,"B.nobp"),"b");
        var a=Restore(bep,"A.dll");var b=Restore(bep,"B.nobp");
        check(DisabledRestoreQueue.Read(config).Length==0&&!Directory.Exists(config),"an absent NOMM restore queue reads empty without filesystem writes");
        DisabledRestoreQueue.Save(config,new[]{a});
        var first=DisabledRestoreQueue.Read(config);
        check(first.Length==1&&first[0].SourceRelative=="A.dll"&&first[0].Digest==a.Digest,"a persisted queue retains the exact selected unit fingerprint");
        check(File.Exists(Disabled(bep,"A.dll"))&&!Directory.Exists(Path.Combine(bep,"plugins")),"saving the normal-mod queue never activates disabled mod files");
        DisabledRestoreQueue.Save(config,new[]{a,b});
        check(DisabledRestoreQueue.Read(config).Length==2&&File.Exists(Queue(config)+".bak")&&!File.Exists(Queue(config)+".tmp"),"atomic replacement saves the new queue and retains its prior recovery receipt");
        FileAt(Queue(config)+".tmp","interrupted temporary bytes");FileAt(Path.Combine(config,"unrelated.cfg"),"keep config");
        DisabledRestoreQueue.Apply(config,bep);
        check(File.ReadAllText(Active(bep,"A.dll"))=="a"&&File.ReadAllText(Active(bep,"B.nobp"))=="b","startup queue apply restores all exact disabled units");
        check(!File.Exists(Queue(config))&&!File.Exists(Queue(config)+".tmp")&&File.Exists(Queue(config)+".bak")&&File.ReadAllText(Path.Combine(config,"unrelated.cfg"))=="keep config","successful application clears only the active and temporary receipts");
        check(DisabledRestoreQueue.Read(config).Length==0,"a recovery backup does not reactivate after the main queue clears");
        DisabledRestoreQueue.Apply(config,bep);
        check(File.Exists(Active(bep,"A.dll")),"repeated startup with no active queue is harmless");
        string collisionConfig=Config(temporary,"collision"),collisionBep=Bep(temporary,"collision");
        FileAt(Disabled(collisionBep,"A.dll"),"a");FileAt(Disabled(collisionBep,"B.dll"),"b");
        var collisionA=Restore(collisionBep,"A.dll");var collisionB=Restore(collisionBep,"B.dll");
        DisabledRestoreQueue.Save(collisionConfig,new[]{collisionA,collisionB});FileAt(Active(collisionBep,"B.dll"),"existing");string saved=File.ReadAllText(Queue(collisionConfig));
        Reject(check,()=>DisabledRestoreQueue.Apply(collisionConfig,collisionBep),"a queued destination collision is reported before any selected unit moves");
        check(File.ReadAllText(Queue(collisionConfig))==saved&&File.Exists(Disabled(collisionBep,"A.dll"))&&!File.Exists(Active(collisionBep,"A.dll"))&&File.ReadAllText(Active(collisionBep,"B.dll"))=="existing","a failed restore retains the queue and both disabled/active state for reviewed retry");
        File.Delete(Active(collisionBep,"B.dll"));File.Move(Disabled(collisionBep,"A.dll"),Active(collisionBep,"A.dll"));
        DisabledRestoreQueue.Apply(collisionConfig,collisionBep);
        check(File.Exists(Active(collisionBep,"A.dll"))&&File.Exists(Active(collisionBep,"B.dll"))&&!File.Exists(Queue(collisionConfig)),"an interrupted atomic move resumes idempotently using the durable queue");
        string interruptedConfig=Config(temporary,"log-failure"),interruptedBep=Bep(temporary,"log-failure");FileAt(Disabled(interruptedBep,"A.dll"),"a");
        DisabledRestoreQueue.Save(interruptedConfig,new[]{Restore(interruptedBep,"A.dll")});
        Reject(check,()=>DisabledRestoreQueue.Apply(interruptedConfig,interruptedBep,message=>throw new IOException("fixture failure")),"startup application failure is surfaced to the caller");
        check(File.Exists(Queue(interruptedConfig))&&File.Exists(Disabled(interruptedBep,"A.dll"))&&!File.Exists(Active(interruptedBep,"A.dll")),"application failure retains a retryable receipt after rollback");
    }

    private static void EmptyQueueRecovery(Action<bool,string> check,string temporary)
    {
        string config=Config(temporary,"empty-recovery"),bep=Bep(temporary,"empty-recovery");
        DisabledRestoreQueue.Save(config,Array.Empty<DisabledModRestore>());
        check(!Directory.Exists(config),"empty restore intent creates no queue or config directory");
        FileAt(Disabled(bep,"A.dll"),"a");var restore=Restore(bep,"A.dll");
        DisabledRestoreQueue.Save(config,new[]{restore});
        DisabledRestoreQueue.Save(config,new[]{restore});string backup=File.ReadAllText(Queue(config)+".bak");
        FileAt(Queue(config)+".tmp","unfinished");
        DisabledRestoreQueue.Save(config,Array.Empty<DisabledModRestore>());
        check(!File.Exists(Queue(config))&&!File.Exists(Queue(config)+".tmp")&&File.ReadAllText(Queue(config)+".bak")==backup,"clearing restore intent removes active/temp queues and preserves recovery backup");
        check(File.Exists(Disabled(bep,"A.dll"))&&!File.Exists(Active(bep,"A.dll")),"empty restore intent never activates a previously selected package");
        WriteDocument(config,new DisabledRestoreQueueDocument {CreatedUtc=DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeSeconds()});
        DisabledRestoreQueue.Apply(config,bep);
        check(!File.Exists(Queue(config))&&File.Exists(Disabled(bep,"A.dll")),"startup consumes a legacy expired empty queue without blocking updates or restoring files");
        DisabledRestoreQueue.Apply(config,bep);
        check(!File.Exists(Queue(config)),"later startups stay clear after consuming an expired empty queue");
        WriteDocument(config,new DisabledRestoreQueueDocument {CreatedUtc=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()});
        DisabledRestoreQueue.Apply(config,bep);
        check(!File.Exists(Queue(config)),"a legacy empty queue has no actions requiring a creation-time freshness gate");
        WriteDocument(config,new DisabledRestoreQueueDocument {Schema=2});
        Reject(check,()=>DisabledRestoreQueue.Apply(config,bep),"an unsupported empty queue schema still rejects");
        WriteDocument(config,new DisabledRestoreQueueDocument {Restores=null!});
        Reject(check,()=>DisabledRestoreQueue.Apply(config,bep),"a malformed empty queue still rejects");
        WriteDocument(config,new DisabledRestoreQueueDocument {CreatedUtc=DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeSeconds(),Restores=new[]{restore}});
        Reject(check,()=>DisabledRestoreQueue.Apply(config,bep),"expired actual restore actions remain blocked");
        check(File.Exists(Queue(config))&&File.Exists(Disabled(bep,"A.dll"))&&!File.Exists(Active(bep,"A.dll")),"expired real restore intent is retained without moving files");
    }

    private static void Validation(Action<bool,string> check,string temporary)
    {
        string config=Config(temporary,"validation"),bep=Bep(temporary,"validation");FileAt(Disabled(bep,"A.dll"),"a");var restore=Restore(bep,"A.dll");
        DisabledRestoreQueue.Save(config,new[]{restore});string saved=File.ReadAllText(Queue(config));
        var tooMany=Enumerable.Range(0,DisabledModFiles.MaximumUnits+1).Select(i=>new DisabledModRestore {SourceRelative="file"+i+".dll",TargetRelative="file"+i+".dll",Digest=restore.Digest}).ToArray();
        Reject(check,()=>DisabledRestoreQueue.Save(config,tooMany),"queue size cannot exceed 128 selected units");
        Reject(check,()=>DisabledRestoreQueue.Save(config,new[]{restore,restore}),"duplicate queue sources and destinations reject before writing");
        check(File.ReadAllText(Queue(config))==saved,"invalid queue saves preserve the previous active receipt");
        var huge=new DisabledModRestore {SourceRelative="A.dll",TargetRelative="A.dll",Digest=restore.Digest,PluginIds=Enumerable.Range(0,256).Select(i=>new string('x',250)+i).ToArray()};
        Reject(check,()=>DisabledRestoreQueue.Save(config,new[]{huge}),"serialized queues are bounded before any receipt file changes");
        check(File.ReadAllText(Queue(config))==saved,"oversized serialized selection leaves its previous queue intact");
        WriteDocument(config,new DisabledRestoreQueueDocument {Schema=2,Restores=new[]{restore}});
        Reject(check,()=>DisabledRestoreQueue.Read(config),"unsupported queue schemas cannot activate mods");
        WriteDocument(config,new DisabledRestoreQueueDocument {CreatedUtc=DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeSeconds(),Restores=new[]{restore}});
        Reject(check,()=>DisabledRestoreQueue.Read(config),"restore queues older than six hours require a fresh selection");
        WriteDocument(config,new DisabledRestoreQueueDocument {CreatedUtc=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),Restores=new[]{restore}});
        Reject(check,()=>DisabledRestoreQueue.Read(config),"far-future queue creation timestamps are rejected");
        WriteDocument(config,new DisabledRestoreQueueDocument {Restores=new[]{new DisabledModRestore {SourceRelative="../A.dll",TargetRelative="A.dll",Digest=restore.Digest}}});
        Reject(check,()=>DisabledRestoreQueue.Read(config),"persisted disabled source traversal is rejected");
        WriteDocument(config,new DisabledRestoreQueueDocument {Restores=new[]{new DisabledModRestore {SourceRelative="A.dll",TargetRelative="../outside.dll",Digest=restore.Digest}}});
        Reject(check,()=>DisabledRestoreQueue.Read(config),"persisted target traversal is rejected");
        File.WriteAllText(Queue(config),"broken JSON");FileAt(Queue(config)+".bak",saved);
        Reject(check,()=>DisabledRestoreQueue.Read(config),"a corrupt main queue cannot silently load its backup");
        check(File.ReadAllText(Queue(config))=="broken JSON","read failure preserves the corrupt receipt for review");
        File.Delete(Queue(config));check(DisabledRestoreQueue.Read(config).Length==0,"an absent main queue never automatically applies an existing backup");
        FileAt(Queue(config),new string('x',DisabledRestoreQueue.MaximumBytes+1));
        Reject(check,()=>DisabledRestoreQueue.Read(config),"oversized receipt files reject before JSON deserialization");
        DisabledRestoreQueue.Clear(config);DisabledRestoreQueue.Clear(config);
        check(File.Exists(Queue(config)+".bak")&&!File.Exists(Queue(config)),"manual clear is idempotent and preserves recovery backup material");
        Reject(check,()=>DisabledRestoreQueue.Read("relative/config"),"queue reads require an absolute configured directory");
        Reject(check,()=>DisabledRestoreQueue.Save("relative/config",new[]{restore}),"queue writes cannot depend on the current working directory");
    }

    private static void SourceMapping(Action<bool,string> check,string temporary)
    {
        string bep=Bep(temporary,"mapping");FileAt(Disabled(bep,"Main.dll"),"dll");FileAt(Disabled(bep,"Parent/main.dll"),"parent dll");FileAt(Disabled(bep,"Parent/nested/aircraft.nobp"),"bundle");FileAt(Disabled(bep,"Addon/addon.nobp"),"addon");
        var main=Restore(bep,"Main.dll");var parent=Restore(bep,"Parent");var addonCandidate=DisabledModFiles.Inventory(bep).Single(c=>c.SourceRelative=="Addon");addonCandidate.TargetRelative="Parent/addons/verified.addon";var addon=DisabledModFiles.Prepare(bep,addonCandidate);var restores=new[]{main,parent,addon};
        DisabledModFiles.Verify(bep,restores);
        check(DisabledRestoreQueue.SourceFor(bep,restores,"Main.dll")==Disabled(bep,"Main.dll"),"a restored DLL target maps to its exact disabled source file");
        check(DisabledRestoreQueue.SourceFor(bep,restores,"Parent/nested/aircraft.nobp")==Disabled(bep,"Parent/nested/aircraft.nobp"),"a whole-unit directory maps nested bundle sources without flattening their paths");
        check(DisabledRestoreQueue.SourceFor(bep,restores,"Parent/addons/verified.addon/addon.nobp")==Disabled(bep,"Addon/addon.nobp"),"overlapping parent/addon receipts select the longest exact target prefix");
        check(DisabledRestoreQueue.SourceFor(bep,restores,"ParentOther/aircraft.nobp")==null,"directory prefix matching requires a complete path segment");
        check(DisabledRestoreQueue.SourceFor(bep,restores,"Unknown.nobp")==null,"unselected targets cannot select arbitrary disabled sources");
        Reject(check,()=>DisabledRestoreQueue.SourceFor(bep,restores,"Parent"),"a directory cannot be returned as a disabled source file");
        Reject(check,()=>DisabledRestoreQueue.SourceFor(bep,restores,"Main.dll/inside.nobp"),"a whole DLL receipt cannot supply a nested source path");
        Reject(check,()=>DisabledRestoreQueue.SourceFor(bep,restores,"Parent/missing.nobp"),"a missing file in a still-disabled selected unit fails instead of rebinding to another active file");
        foreach(string path in new[]{"../Main.dll","Parent/../Main.dll","C:\\outside.dll","Parent//main.dll"})
            Reject(check,()=>DisabledRestoreQueue.SourceFor(bep,restores,path),"disabled source mapping rejects target traversal "+path);
        Reject(check,()=>DisabledRestoreQueue.SourceFor(bep,new[]{main,main},"Main.dll"),"ambiguous receipt mappings cannot select a source file");
        DisabledModFiles.Apply(bep,restores);
        check(DisabledRestoreQueue.SourceFor(bep,restores,"Main.dll")==null&&DisabledRestoreQueue.SourceFor(bep,restores,"Parent/nested/aircraft.nobp")==null&&DisabledRestoreQueue.SourceFor(bep,restores,"Parent/addons/verified.addon/addon.nobp")==null,"already-restored units return no disabled source so the caller can verify active paths");
    }
}
