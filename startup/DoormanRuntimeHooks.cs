using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using HarmonyLib;
using KellysJOINCHECK;

// This is not a patcher type. Loader signatures are resolved only when Unity
// executes BepInEx's normal runtime entrypoint.
public static class DoormanRuntimeHooks
{
    public static void BeforePlugins()
    {
        if(DoormanStartupPatcher.Headless()||DoormanStartupPatcher.Ready) return;
        var log=Logger.CreateLogSource("DOORMAN startup");
        try
        {
            new Harmony("kelly.nuclearoption.doorman.startup").Patch(AccessTools.Method(typeof(Chainloader),"Start"),transpiler:new HarmonyMethod(typeof(DoormanRuntimeHooks),nameof(AfterDiscovery)));
            DoormanStartupPatcher.Ready=true;
            log.LogInfo("Client plugin preferences ready; normal Unity startup preserved.");
        } catch(Exception ex) { log.LogWarning("Plugin preferences unavailable; normal startup preserved: "+ex.GetType().Name); }
    }
    private static IEnumerable<CodeInstruction> AfterDiscovery(IEnumerable<CodeInstruction> instructions)
    {
        var result=new List<CodeInstruction>(); int matches=0;
        foreach(var instruction in instructions)
        {
            result.Add(instruction);
            if(instruction.operand is MethodInfo method && method.DeclaringType==typeof(TypeLoader) && method.Name=="FindPluginTypes" && method.IsGenericMethod && method.GetGenericArguments().SequenceEqual(new[]{typeof(PluginInfo)}))
            { result.Add(new CodeInstruction(OpCodes.Call,AccessTools.Method(typeof(DoormanRuntimeHooks),nameof(Filter)))); matches++; }
        }
        if(matches!=1) throw new InvalidOperationException("BepInEx plugin discovery changed.");
        return result;
    }
    private static Dictionary<string,List<PluginInfo>> Filter(Dictionary<string,List<PluginInfo>> original)
    {
        ProtectRuntimeHost();
        string path=Path.Combine(Paths.ConfigPath,"doorman-mods.json");
        if (!File.Exists(path)&&ServerSetupFiles.Pending(Paths.ConfigPath)==null) return original;
        try
        {
            var profile=File.Exists(path)?ModJson.Read<ModProfile>(path):new ModProfile(); profile.Validate();
            var session=ServerSetupFiles.Pending(Paths.ConfigPath);
            var disabled=new HashSet<string>(session?.DisabledPlugins??profile.DisabledPlugins,StringComparer.Ordinal);
            bool vanilla=session!=null&&Signature.Parse(session.Target.Expanded).Mods.Count==0;
            // Keep the exclusions even if an installed plugin's dependencies
            // changed since this selection was saved. Chainloader already skips
            // plugins whose hard dependencies are absent from the filtered set.
            return FilterPlugins(original,disabled,vanilla);
        } catch(Exception ex) { Logger.CreateLogSource("DOORMAN startup").LogWarning("Keeping the normal plugin set: "+ex.Message); return original; }
    }
    private static Dictionary<string,List<PluginInfo>> FilterPlugins(Dictionary<string,List<PluginInfo>> original,HashSet<string> disabled,bool vanilla)
        =>original.ToDictionary(p=>p.Key,p=>p.Value.Where(info=>ServerSetupFiles.KeepPlugin(info.Metadata.GUID,disabled,vanilla)).ToList());
    private static void ProtectRuntimeHost()
    {
        if(DoormanStartupPatcher.Headless())return;
        var log=Logger.CreateLogSource("DOORMAN startup");
        try
        {
            // Chainloader has created its manager here, but no plugin component
            // has been added yet. Changing flags now cannot interrupt another
            // plugin's coroutine or trigger its OnDisable lifecycle.
            // Reflection keeps early patcher discovery free of Unity signatures.
            var host=typeof(Chainloader).GetProperty("ManagerObject",BindingFlags.Public|BindingFlags.Static)!.GetValue(null,null)??throw new InvalidOperationException("The plugin runtime host is missing.");
            DoormanRuntimeHost.Apply(host);
            log.LogInfo("Plugin runtime host protected before plugin startup; Blueprinter is not required for lifetime protection.");
        }
        catch(Exception ex){log.LogWarning("Plugin runtime host protection unavailable: "+ex.GetBaseException().Message);}
    }
}
