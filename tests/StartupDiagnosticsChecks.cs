using System;
using System.Collections.Generic;
using System.Linq;
using KellysJOINCHECK;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class StartupDiagnosticsRegression
{
    internal static void Run(Action<bool,string> check,string startupPath)
    {
        // Cecil examines the built helper without loading BepInEx, executing
        // its preloader entrypoint, or creating Unity plugin components.
        using(var helper=AssemblyDefinition.ReadAssembly(startupPath))
        {
            var patcher=helper.MainModule.GetType("DoormanStartupPatcher");
            var hooks=helper.MainModule.GetType("DoormanRuntimeHooks");
            foreach(var contract in new[]{("SelectionFilterRan","System.Boolean"),("UnmatchedDisabledPlugins","System.String[]"),("FilteredPlugins","System.String[]")})
            {
                var field=patcher.Fields.SingleOrDefault(f=>f.Name==contract.Item1);
                check(field!=null&&field.IsPublic&&field.IsStatic&&field.FieldType.FullName==contract.Item2,
                    "startup reflection exposes "+contract.Item1+" with its optional diagnostic field contract");
            }

            var install=hooks.Methods.Single(m=>m.Name=="BeforePlugins");
            var filter=hooks.Methods.Single(m=>m.Name=="Filter");
            var selection=hooks.Methods.Single(m=>m.Name=="FilterPlugins");
            var instructions=filter.Body.Instructions.ToList();
            int entered=instructions.FindIndex(i=>Writes(i,"SelectionFilterRan"));
            int config=instructions.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="BepInEx.Paths"&&m.Name=="get_ConfigPath");
            check(entered>0&&Integer(instructions[entered-1])==1&&config>entered&&!instructions.Take(entered).Any(i=>i.OpCode.Code==Code.Ret),
                "actual discovery records entry before configuration or no-selection early returns");
            check(!install.Body.Instructions.Any(i=>Writes(i,"SelectionFilterRan"))&&install.Body.Instructions.Any(i=>Writes(i,"Ready")),
                "installing Harmony alone cannot claim that discovery filtering ran");
            check(new[]{"UnmatchedDisabledPlugins","FilteredPlugins"}.All(name=>
                {int reset=instructions.FindIndex(i=>Writes(i,name));return reset>entered&&reset<config;}),
                "each discovery clears both diagnostic snapshots before preferences are read");
            check(filter.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="DoormanRuntimeHooks"&&m.Name=="FilterPlugins"),
                "the actual discovery path reaches the snapshot-producing selection helper");

            check(SnapshotLimit(selection,"UnmatchedDisabledPlugins")==64,
                "the compiled unmatched-exclusion snapshot materializes at most 64 identifiers");
            check(SnapshotLimit(selection,"FilteredPlugins")==256,
                "the compiled filtered-plugin snapshot materializes at most 256 identifiers");
            var selectorCode=AllTypes(new[]{hooks}).SelectMany(t=>t.Methods)
                .Where(m=>m.HasBody&&(m.Name=="FilterPlugins"||m.Name.Contains("<FilterPlugins>")))
                .SelectMany(m=>m.Body.Instructions).ToArray();
            check(selectorCode.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.ServerSetupFiles"&&m.Name=="KeepPlugin")&&
                selectorCode.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="BepInEx.BepInPlugin"&&m.Name=="get_GUID")&&
                !selectorCode.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="BepInEx.BepInPlugin"&&m.Name=="get_Name"),
                "discovery and its snapshots retain exact metadata GUIDs and the existing protected-plugin predicate");
            check(!selectorCode.Any(i=>i.Operand is MethodReference m&&
                (m.DeclaringType.FullName=="System.IO.File"||m.DeclaringType.FullName=="System.IO.Directory"||
                 m.DeclaringType.FullName=="System.Reflection.Assembly"&&m.Name.StartsWith("Load",StringComparison.Ordinal)||
                 m.DeclaringType.FullName.StartsWith("UnityEngine.",StringComparison.Ordinal))),
                "recording discovery snapshots performs no new file scans, assembly loads, or Unity work");
        }

        // Reproduce the support report's stale identifier without executing any
        // plugin. A diagnostic must not silently treat it as an alias or weaken
        // DOORMAN/Blueprinter protections to make its counts look successful.
        var stale=new HashSet<string>(new[]{"com.resolute.command"},StringComparer.Ordinal);
        check(ServerSetupFiles.KeepPlugin("com.resolute.nuclearoption",stale,false)&&!ServerSetupFiles.KeepPlugin("com.resolute.command",stale,false),
            "the reported stale Resolute exclusion does not silently exclude a different actual plugin GUID");
        var protectedIds=new HashSet<string>(new[]{"kelly.nuclearoption.joincheck","kelly.nuclearoption.joincheck.server","com.nikkorap.blueprinter"},StringComparer.Ordinal);
        check(ServerSetupFiles.KeepPlugin("kelly.nuclearoption.joincheck",protectedIds,true)&&
            ServerSetupFiles.KeepPlugin("kelly.nuclearoption.joincheck.server",protectedIds,true)&&
            ServerSetupFiles.KeepPlugin("com.nikkorap.blueprinter",protectedIds,false)&&
            !ServerSetupFiles.KeepPlugin("com.nikkorap.blueprinter",protectedIds,true),
            "diagnostic recording preserves DOORMAN exemptions and the vanilla-only Blueprinter exclusion");
    }

    private static bool Writes(Instruction instruction,string name)=>instruction.OpCode.Code==Code.Stsfld&&
        instruction.Operand is FieldReference field&&field.DeclaringType.FullName=="DoormanStartupPatcher"&&field.Name==name;

    private static int? SnapshotLimit(MethodDefinition method,string field)
    {
        var instructions=method.Body.Instructions.ToList();
        int store=instructions.FindIndex(i=>Writes(i,field));
        if(store<1||!(instructions[store-1].Operand is MethodReference array)||array.DeclaringType.FullName!="System.Linq.Enumerable"||array.Name!="ToArray")return null;
        for(int index=store-2;index>0;index--)
        {
            if(instructions[index].OpCode.Code==Code.Stsfld)return null;
            if(instructions[index].Operand is MethodReference call&&call.DeclaringType.FullName=="System.Linq.Enumerable"&&call.Name=="Take")return Integer(instructions[index-1]);
        }
        return null;
    }

    private static int? Integer(Instruction instruction)
    {
        switch(instruction.OpCode.Code)
        {
            case Code.Ldc_I4: return (int)instruction.Operand;
            case Code.Ldc_I4_S: return (sbyte)instruction.Operand;
            case Code.Ldc_I4_0: return 0;
            case Code.Ldc_I4_1: return 1;
            default: return null;
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach(var type in types)
        {
            yield return type;
            foreach(var nested in AllTypes(type.NestedTypes))yield return nested;
        }
    }
}
