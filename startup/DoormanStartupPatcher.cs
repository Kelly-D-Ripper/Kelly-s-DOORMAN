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
        try { ModUpdateFiles.ApplyPending(Path.Combine(Paths.CachePath,"DOORMAN-updates"),Paths.PluginPath,message=>log.LogInfo(message)); }
        catch(Exception ex) { log.LogWarning("Keeping installed mods; update queue could not be read: "+ex.Message); }
        RestartStatus.Write(Paths.CachePath,"loading");
        // Runtime reflection is deferred until Unity's normal entrypoint.
    }
}
