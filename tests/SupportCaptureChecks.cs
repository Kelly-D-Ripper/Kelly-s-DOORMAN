using System;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class SupportCaptureChecks
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client,AssemblyDefinition server)
    {
        var plugin=client.MainModule.GetType("KellysJOINCHECK.ClientPlugin");
        var manager=client.MainModule.GetType("KellysJOINCHECK.ModManager");
        var native=client.MainModule.GetType("KellysJOINCHECK.NativeJoinUi");
        check(Calls(plugin.Methods.Single(m=>m.Name=="EnsureUi"),"ClientPlugin","CopyDiagnosticReport"),"native Copy report receives the fresh shared diagnostic provider");
        check(Calls(plugin.Methods.Single(m=>m.Name=="DrawWindow"),"ClientPlugin","CopyDiagnosticReport"),"fallback Copy report uses the same fresh diagnostic provider");
        check(Calls(plugin.Methods.Single(m=>m.Name=="CopyDiagnosticReport"),"ModManager","AppendSupportContext")&&Calls(plugin.Methods.Single(m=>m.Name=="CopyDiagnosticReport"),"SupportReport","Compose"),"Copy combines the current runtime context with failure history and compatibility details");
        check(native.NestedTypes.SelectMany(t=>t.Methods).Where(m=>m.HasBody).Any(m=>Calls(m,"Func`1","Invoke")),"the native clipboard action invokes its provider when clicked");
        check(Calls(manager.Methods.Single(m=>m.Name=="Message"),"ModManager","WorkflowStatus"),"server workflow messages enter report history before showing the checklist");
        check(!manager.Methods.Single(m=>m.Name=="CancelServerSetup").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name=="SupportReport"),"closing or cancelling setup cannot erase the recorded failure");
        string[] stages={"PrepareRestart","AwaitServerRestartLaunch","SetupAfterFrame","PrepareModsRestart","AwaitModsRestartLaunch","QuitForModsWhenReady"};
        foreach(string stage in stages)
        {
            var coroutine=manager.NestedTypes.Single(t=>t.Name.StartsWith("<"+stage+">",StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext");
            check(Calls(coroutine,"ModManager","SupportFailure"),stage+" captures its exception before UI/progress changes");
        }
        var source=manager.Methods.Single(m=>m.Name=="TicketContent");
        string[] fields={"Mod","Mod ID","Source","Physical file"};
        check(fields.All(f=>source.Body.Instructions.Any(i=>i.Operand as string==f))&&fields.All(f=>manager.Methods.Single(m=>m.Name=="SupportFailure").Body.Instructions.Any(i=>i.Operand as string==f)),"source rejection attaches a named mod and physical/resource identity to its copied failure");
        check(fields.All(f=>manager.Methods.Single(m=>m.Name=="HashInventory").Body.Instructions.Any(i=>i.Operand as string==f)),"verification errors also identify the affected content file instead of only reporting a generic hash/file failure");
        var context=manager.Methods.Single(m=>m.Name=="AppendSupportContext");
        check(context.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.DeclaringType.Name=="PendingModUpdate"&&f.Name=="Message"),"Copy report includes persisted reasons for startup updates deferred by prerequisite checks");
        check(Calls(context,"ModManager","PendingUpdates")&&Calls(context,"SupportLogTail","Read")&&!Calls(context,"ModCatalog","Read")&&!Calls(context,"ModUpdateFiles","Hash"),"Copy reads only update receipts and a bounded helper log, using cached/loaded mod metadata without scans or hashing");
        foreach(var type in new[]{plugin,manager,native})
            foreach(var tick in type.Methods.Where(m=>m.HasBody&&(m.Name=="Tick"||m.Name=="Update")))
                check(!Calls(tick,"ClientPlugin","CopyDiagnosticReport")&&!Calls(tick,"ModManager","AppendSupportContext")&&!Calls(tick,"SupportLogTail","Read"),type.Name+" frame work does not gather support files or build clipboard reports");
        check(server.MainModule.GetType("KellysJOINCHECK.SupportReport")==null&&server.MainModule.GetType("KellysJOINCHECK.SupportLogTail")==null,"the released hosting component gains no support-report work");
        var cursor=client.MainModule.GetType("KellysJOINCHECK.ChecklistCursor");
        check(Calls(plugin.Methods.Single(m=>m.Name=="LateUpdate"),"NativeJoinUi","KeepCursorAvailable")&&Calls(native.Methods.Single(m=>m.Name=="KeepCursorAvailable"),"ChecklistCursor","KeepAvailable"),"an open checklist checks cursor availability after the game's frame updates");
        check(Calls(cursor.Methods.Single(m=>m.Name=="KeepAvailable"),"Application","get_isFocused")&&Calls(cursor.Methods.Single(m=>m.Name=="KeepAvailable"),"Cursor","set_visible")&&Calls(cursor.Methods.Single(m=>m.Name=="KeepAvailable"),"Cursor","set_lockState"),"cursor recovery is limited to the focused game and unlocks a hidden cursor");
        check(server.MainModule.GetType("KellysJOINCHECK.ServerMapUpdates")==null,"automatic map setup adds no server-side work");

        string root=Path.Combine(Path.GetTempPath(),"doorman-report-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string file=Path.Combine(root,"DOORMAN-restart.log");
            check(SupportLogTail.Read(file).Contains("not created yet"),"a missing helper log never prevents Copy report");
            File.WriteAllText(file,"old game exited\nlaunch failed: Steam is still busy\n");
            string tail=SupportLogTail.Read(file);
            check(tail.Contains("historical")&&tail.Contains("last written")&&tail.Contains("launch failed: Steam is still busy"),"a helper failure after the old process closes remains available with a historical timestamp");
            File.WriteAllText(file,new string('x',10000)+"\nFINAL helper error\n",new UTF8Encoding(false));
            tail=SupportLogTail.Read(file);
            check(tail.Contains("earlier helper log omitted")&&tail.Contains("FINAL helper error")&&Encoding.UTF8.GetByteCount(tail)<8500,"large helper logs keep only a bounded tail and identify omissions");
            File.WriteAllText(file,new string('x',10000),new UTF8Encoding(false));
            check(SupportLogTail.Read(file).Contains("oversized helper line omitted"),"a huge partial helper line cannot swamp the report");
            check(SupportLogTail.Read(root).Contains("unavailable"),"an unreadable helper log becomes report context rather than a clipboard failure");
        }
        finally {Directory.Delete(root,true);}
    }
    private static bool Calls(MethodDefinition method,string type,string name)=>method.HasBody&&method.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name==type&&m.Name==name);
}
