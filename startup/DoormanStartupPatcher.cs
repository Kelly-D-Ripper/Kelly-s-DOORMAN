using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using KellysJOINCHECK;

// BepInEx reflects every method signature here during discovery. Keep this
// type free of PluginInfo, Chainloader, Harmony and Unity signatures.
public static class DoormanStartupPatcher
{
    public static bool Ready;
    // Optional client diagnostics. Ready means Harmony was installed; this
    // separate flag records that plugin discovery actually reached our filter.
    public static bool SelectionFilterRan;
    public static string[] UnmatchedDisabledPlugins=Array.Empty<string>();
    public static string[] FilteredPlugins=Array.Empty<string>();
    public static string ExternalRestoreError="";
    public static IEnumerable<string> TargetDLLs => Headless()?Array.Empty<string>():new[]{"UnityEngine.CoreModule.dll"};
    internal static bool Headless()=>Environment.GetCommandLineArgs().Any(a=>a.Equals("-batchmode",StringComparison.OrdinalIgnoreCase)||a.Equals("-nographics",StringComparison.OrdinalIgnoreCase));
    public static void Patch(AssemblyDefinition assembly)
    {
        if(Headless()) return;
        var name=typeof(DoormanStartupPatcher).Assembly.GetName();
        string error=DoormanStartupPatch.Inject(assembly,new AssemblyNameReference(name.Name,name.Version));
        if(error.Length>0) Logger.CreateLogSource("DOORMAN startup").LogWarning("Keeping the normal loader startup: "+error);
    }
    public static void Finish()
    {
        if(Headless()) return;
        var log=Logger.CreateLogSource("DOORMAN startup");
        RestartStatus.Write(Paths.CachePath,"configuring");
        try { AddonRemovalQueue.Apply(Paths.CachePath,Paths.BepInExRootPath,Paths.ConfigPath,message=>log.LogInfo(message)); }
        catch(Exception ex) { ExternalRestoreError="Addon deletion could not finish. "+ex.Message;log.LogWarning(ExternalRestoreError); }
        try { if(ExternalRestoreError.Length==0)SelfUpdateTransaction.Apply(Paths.CachePath,Paths.BepInExRootPath,message=>log.LogInfo(message)); }
        catch(Exception ex) { log.LogWarning("Keeping the installed DOORMAN client; update could not be read: "+ex.Message); }
        try { AutoSetupBatch.Recover(Paths.CachePath,Paths.PluginPath,message=>log.LogInfo(message)); }
        catch(Exception ex) { ExternalRestoreError="Automatic setup recovery failed: "+ex.Message;log.LogWarning(ExternalRestoreError); }
        try
        {
            if(ExternalRestoreError.Length>0)throw new InvalidDataException(ExternalRestoreError);
            DisabledRestoreQueue.Apply(Paths.ConfigPath,Paths.BepInExRootPath,message=>log.LogInfo(message));
            // A missing queue is not proof that a saved server's files arrived.
            // Recovery/cancellation may have removed intent after its ticket
            // was written. Only use that temporary selection once every unit
            // is present at its exact destination with the verified bytes.
            var session=ServerSetupFiles.Pending(Paths.ConfigPath);
            if(session!=null&&session.Restores.Length>0)DisabledModFiles.VerifyApplied(Paths.BepInExRootPath,session.Restores);
        }
        catch(Exception ex){ExternalRestoreError="NOMM restore failed: "+ex.Message;log.LogWarning(ExternalRestoreError);}
        // Verify both queues before changing any plugin or standalone content.
        // Cross-file writes are not one filesystem transaction; if a later write
        // fails, block the temporary server selection and retain rollback copies.
        try
        {
            if(ExternalRestoreError.Length==0)
            {
                // Check both map queues before any queued update can change the tree.
                ServerMapUpdates.VerifyPending(Paths.CachePath,Paths.PluginPath);
                IronjawPackageUpdates.VerifyPending(Paths.CachePath,Paths.PluginPath);
                IronjawPackageUpdates.CheckQueueConflicts(Paths.CachePath,Paths.PluginPath,ModUpdateFiles.ReadPending(Path.Combine(Paths.CachePath,"DOORMAN-updates"),Paths.PluginPath));
                StartupUpdatePreflight.ApplyQueues(Path.Combine(Paths.CachePath,"DOORMAN-updates"),Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath,Paths.ConfigPath,message=>log.LogInfo(message));
                ServerMapUpdates.Apply(Paths.CachePath,Paths.PluginPath,message=>log.LogInfo(message));
                IronjawPackageUpdates.Apply(Paths.CachePath,Paths.PluginPath,message=>log.LogInfo(message));
            }
        }
        catch(Exception ex)
        {
            ExternalRestoreError="Mod setup could not finish. Some updates may have installed; previous files are retained in the DOORMAN update cache. "+ex.Message;
            log.LogWarning(ExternalRestoreError);
        }
        RestartStatus.Write(Paths.CachePath,"loading");
        // Runtime reflection is deferred until Unity's normal entrypoint.
    }
}
