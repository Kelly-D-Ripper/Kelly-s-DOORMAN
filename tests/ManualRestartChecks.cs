using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ManualRestartRegression
{
    private static bool Calls(MethodDefinition method,string type,string name)=>method.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name==type&&m.Name==name);
    private static int Offset(MethodDefinition method,string type,string name)=>method.Body.Instructions.First(i=>i.Operand is MethodReference m&&m.DeclaringType.Name==type&&m.Name==name).Offset;
    private static MethodDefinition Coroutine(TypeDefinition manager,string name)=>manager.NestedTypes.Single(t=>t.Name.StartsWith("<"+name+">",StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext");
    internal static void Run(AssemblyDefinition client,AssemblyDefinition server,Action<bool,string> check)
    {
        var manager=client.MainModule.GetType("KellysJOINCHECK.ModManager");
        var normal=manager.Methods.Single(m=>m.Name=="CommitManualModsRestart");
        var prepare=Coroutine(manager,"PrepareModsRestart");
        check(Calls(prepare,"ModManager","CommitManualModsRestart")&&Calls(prepare,"Task","get_IsCompleted")&&Calls(prepare,"ModManager","MenuSafety"),"manual normal restart is reached after cancellable background verification and final menu safety");
        check(!Calls(normal,"WindowsRestartLauncher","Start")&&!Calls(normal,"ModManager","LaunchRestartHelper")&&!Calls(normal,"ModManager","BeginRestartHandshake"),"manual normal restart commits without launching or awaiting the Windows executable");
        int queue=Offset(normal,"DisabledRestoreQueue","Save"),remove=Offset(normal,"File","Delete"),save=Offset(normal,"ModJson","Save"),quit=Offset(normal,"Application","Quit");
        check(queue<remove&&remove<save&&save<quit,"manual normal restart saves physical intent, removes temporary server selection, commits profile and then quits");
        check(Calls(normal,"File","ReadAllBytes")&&Calls(normal,"File","WriteAllBytes")&&Calls(normal,"DisabledRestoreQueue","Clear"),"manual normal profile failure has bounded old server-page recovery and owned-queue cleanup");
        var confirmed=normal.Body.Instructions.Single(i=>i.OpCode.Code==Code.Stfld&&i.Operand is FieldReference f&&f.Name=="restartExitConfirmed");
        check(save<confirmed.Offset&&confirmed.Offset<quit,"manual normal selection is preserved through shutdown only after its profile commit");
        var invoke=manager.Methods.Single(m=>m.Name=="RestartMods");
        check(Calls(invoke,"ModManager","CancelRestartHandshake")&&invoke.Parameters.Any(p=>p.Name=="manual"),"manual normal preparation cancels an earlier blocked helper handshake before verified staging");
        check(Calls(invoke,"RuntimePlatform","get_IsWine")&&Calls(invoke,"NativeModsUi","PromptConfirmation"),"Wine mod restarts ask before closing and route through the manual transaction");
        var setup=manager.Methods.Single(m=>m.Name=="SetupServer");
        check(Calls(setup,"RuntimePlatform","get_IsWine")&&Calls(setup,"ModManager","RestartServerManually"),"Wine server setup routes the armed restart through the verified manual transaction");
        var actions=manager.Methods.Single(m=>m.Name=="ServerActions");
        check(Calls(actions,"RuntimePlatform","get_IsWine")&&actions.Body.Instructions.Any(i=>i.Operand is string s&&s=="Restart manually"),"Wine users see a manual restart action before the game is closed");
        var serverReady=Coroutine(manager,"PrepareRestart");
        int serverSave=Offset(serverReady,"ServerSetupFiles","Save"),serverQueue=Offset(serverReady,"DisabledRestoreQueue","Save"),serverQuit=Offset(serverReady,"Application","Quit"),launch=Offset(serverReady,"ModManager","BeginRestartHandshake");
        check(serverSave<serverQueue&&serverQueue<serverQuit&&serverQuit<launch,"manual server restart persists verified return and restore intent before closing, ahead of automatic helper preparation");
        var code=serverReady.Body.Instructions.ToList();
        var manual=code.Single(i=>i.OpCode.Code==Code.Ldfld&&i.Operand is FieldReference f&&f.Name=="manual");
        check(code.Skip(code.IndexOf(manual)).TakeWhile(i=>i.Offset<serverQuit).Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch&&i.Operand is Instruction destination&&destination.Offset>serverQuit),"automatic server restart bypasses the explicitly chosen manual quit branch");
        check(Calls(serverReady,"ModManager","MenuSafety")&&Calls(serverReady,"ModManager","Same")&&code.Any(i=>i.Operand is FieldReference f&&f.Name=="setupGeneration"),"manual server commit retains exact-server generation and final menu-safety checks");
        var serverStart=manager.Methods.Single(m=>m.Name=="RestartForServer");
        check(Offset(serverStart,"ModManager","CancelRestartHandshake")<Offset(serverStart,"ModManager","Ticket"),"manual server preparation invalidates earlier Windows launch before creating a new verified ticket");
        var available=manager.Methods.Single(m=>m.Name=="CanRestartServerManually");
        check(Calls(available,"ModManager","Same")&&Calls(available,"ModManager","StartupAvailable")&&!Calls(available,"File","Exists"),"manual server option requires the armed target and startup patcher while accepting a blocked or missing restart EXE");
        foreach(string name in new[]{"AwaitServerRestartLaunch","AwaitModsRestartLaunch"})
            check(Calls(Coroutine(manager,name),"RestartLaunchFailure","Guidance")&&Calls(Coroutine(manager,name),"ModManager","SupportFailure"),name+" keeps the original support failure and offers safe manual guidance");
        var serverWait=Coroutine(manager,"AwaitServerRestartLaunch");
        check(Calls(serverWait,"Time","get_unscaledTime")&&Calls(serverWait,"ModManager","CancelRestartHandshake")&&Calls(serverWait,"File","Exists"),"stalled server helper is bounded and its cancellation receipt is checked before manual recovery");
        var plugin=client.MainModule.GetType("KellysJOINCHECK.ClientPlugin");
        check(Calls(plugin.Methods.Single(m=>m.Name=="RefreshServerActions"),"ModManager","CanRestartServerManually")&&Calls(plugin.Methods.Single(m=>m.Name=="DrawWindow"),"ModManager","CanRestartServerManually"),"native and fallback checklists both expose the same explicit manual server action");
        var native=client.MainModule.GetType("KellysJOINCHECK.NativeJoinUi");
        check(native.Methods.Single(m=>m.Name=="LayoutText").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="manualRestartButton"),"the checklist reserves vertical space for its full-width manual restart button");
        var mods=client.MainModule.GetType("KellysJOINCHECK.NativeModsUi");
        check(mods.Methods.Single(m=>m.Name=="PromptRestart").Parameters.Any(p=>p.Name=="manual")&&mods.Methods.Single(m=>m.Name=="PromptConfirmation").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="restartManualAction"),"restart-only manual choice is cleared before unrelated list/delete confirmation dialogs");
        check(server.MainModule.GetType("KellysJOINCHECK.RestartLaunchFailure")==null,"the released hosting component acquires no client launch classification or UI work");
        check(server.MainModule.GetType("KellysJOINCHECK.RuntimePlatform")==null,"Wine detection does not add work to the server component");
    }
}
