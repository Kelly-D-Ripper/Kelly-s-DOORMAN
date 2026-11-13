using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class DoormanStartupPatch
{
    internal static string Inject(AssemblyDefinition assembly,AssemblyNameReference helper)
    {
        var module=assembly.MainModule;
        var cctor=module.GetType("UnityEngine.Application")?.Methods.SingleOrDefault(m=>m.Name==".cctor");
        if(cctor==null||!cctor.HasBody) return "The normal BepInEx entrypoint was not found.";
        var body=cctor.Body;var starts=body.Instructions.Where(i=>i.OpCode==OpCodes.Call&&i.Operand is MethodReference m&&m.DeclaringType.FullName=="BepInEx.Bootstrap.Chainloader"&&m.Name=="Start"&&m.Parameters.Count==0).ToArray();
        if(starts.Length!=1) return "The normal BepInEx startup call changed.";
        var start=starts[0];
        if(!body.Instructions.TakeWhile(i=>i!=start).Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="BepInEx.Bootstrap.Chainloader"&&m.Name=="Initialize")) return "The normal loader initialization must run first.";
        if(body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="DoormanRuntimeHooks"&&m.Name=="BeforePlugins")) return "";
        var scope=module.AssemblyReferences.FirstOrDefault(a=>a.Name==helper.Name);
        if(scope==null) { scope=helper;module.AssemblyReferences.Add(scope); }
        var type=new TypeReference("","DoormanRuntimeHooks",module,scope);
        var callback=new MethodReference("BeforePlugins",module.TypeSystem.Void,type) { HasThis=false };
        var injected=Instruction.Create(OpCodes.Call,callback);
        body.GetILProcessor().InsertBefore(start,injected);
        foreach(var instruction in body.Instructions)
        {
            if(instruction.Operand==start) instruction.Operand=injected;
            else if(instruction.Operand is Instruction[] branches) for(int i=0;i<branches.Length;i++) if(branches[i]==start) branches[i]=injected;
        }
        foreach(var handler in body.ExceptionHandlers)
        {
            if(handler.TryStart==start) handler.TryStart=injected;
            if(handler.HandlerStart==start) handler.HandlerStart=injected;
            if(handler.FilterStart==start) handler.FilterStart=injected;
        }
        return "";
    }
}
