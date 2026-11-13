using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class RequisiteUiChecks
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client)
    {
        var all=Types(client.MainModule.Types).ToArray();
        var mods=all.Single(t=>t.FullName=="KellysJOINCHECK.NativeModsUi");
        var maps=all.Single(t=>t.FullName=="KellysJOINCHECK.NativeMapsUi");
        var manager=all.Single(t=>t.FullName=="KellysJOINCHECK.ModManager");
        var mapEntry=all.Single(t=>t.FullName=="KellysJOINCHECK.MapEntry");
        check(mapEntry.Fields.Any(f=>f.Name=="Requisites"&&f.FieldType.FullName=="KellysJOINCHECK.ModRequisite[]")&&mapEntry.Fields.Any(f=>f.Name=="RequisiteError"&&f.FieldType.FullName=="System.String"),"map entries retain prerequisite declarations and malformed-declaration diagnostics separately from preview artwork");

        var open=manager.Methods.Single(m=>m.Name=="Open");
        foreach(string callback in new[]{"ModRequisiteIssues","MapRequisiteIssues","RequisiteAction","ModDeclarations","MapDeclarations"})
            check(Calls(open).Any(m=>m.Name==callback&&m.DeclaringType.FullName==manager.FullName),"native prerequisite UI receives the manager's full-inventory callback: "+callback);
        check(Calls(manager.Methods.Single(m=>m.Name=="ModRequisiteIssues")).Any(m=>m.Name=="ForMod"&&m.DeclaringType.FullName=="KellysJOINCHECK.ModRequisites"),"mod UI diagnostics use the canonical prerequisite evaluator");
        check(Calls(manager.Methods.Single(m=>m.Name=="MapRequisiteIssues")).Any(m=>m.Name=="ForMap"&&m.DeclaringType.FullName=="KellysJOINCHECK.ModRequisites"),"map UI diagnostics use the canonical evaluator with the built-in map-loader prerequisite");
        var action=manager.Methods.Single(m=>m.Name=="RequisiteAction");
        check(Calls(action).Any(m=>m.Name=="Evaluate"&&m.DeclaringType.FullName=="KellysJOINCHECK.ModRequisites")&&Reads(action,"busy"),"clicking an old prerequisite card rechecks the current inventory and honours the busy guard");
        check(Calls(action).Any(m=>m.Name=="ApplySelection"&&m.DeclaringType.FullName==manager.FullName)&&Calls(action).Any(m=>m.Name=="MenuSafety"&&m.DeclaringType.FullName==manager.FullName),"prerequisite reload/restart actions reuse the guarded selection workflow");

        Rows(check,mods,"RefreshSelection");
        Rows(check,maps,"RefreshRequisites");
        var labels=Strings(mods.Methods.Single(m=>m.Name=="RequisiteActionLabel"));
        check(new[]{"Enable ","Download and install ","Update ","Reload mods","Restart game"}.All(labels.Contains),"prerequisite actions distinguish disabled, missing, outdated, content reload and plugin restart states");
        var labelMethod=mods.Methods.Single(m=>m.Name=="RequisiteActionLabel");
        check(labels.Contains("Open release page")&&Calls(labelMethod).Any(m=>m.Name=="get_Content"&&m.DeclaringType.FullName=="KellysJOINCHECK.ModRequisite")&&Calls(labelMethod).Any(m=>m.Name=="ProtectedPlugin"&&m.DeclaringType.FullName=="KellysJOINCHECK.ModUpdateFiles"),"content bundles and protected loaders use an honest release-page action instead of promising an unsupported automatic installation");
        check(Strings(mods.Methods.Single(m=>m.Name=="RequisiteSummary")).Contains("Requires: "),"healthy prerequisites retain a visible Requires flag rather than disappearing when satisfied");
        foreach(var ui in new[]{mods,maps})
        {
            var owned=Types(new[]{ui}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
            check(owned.Any(m=>Reads(m,"requisiteAction")&&Calls(m).Any(c=>c.Name=="Invoke")),"prerequisite buttons invoke the supplied manager action: "+ui.Name);
            var tick=ui.Methods.Single(m=>m.Name=="Tick");
            var reachable=Reachable(tick,all).ToArray();
            check(reachable.Any(m=>m.DeclaringType.FullName==ui.FullName&&m.Name=="LayoutRows")&&tick.Body.Instructions.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch),"resizing the UI reflows cached prerequisite rows through the conditional layout path: "+ui.Name);
            check(!reachable.Any(m=>Reads(m,"modRequisites")||Reads(m,"mapRequisites")||Reads(m,"declaredModRequisites")||Reads(m,"declaredMapRequisites"))&&!reachable.SelectMany(Calls).Any(c=>c.DeclaringType.FullName=="KellysJOINCHECK.ModRequisites"),"prerequisite evaluation stays outside the UI's per-frame tick: "+ui.Name);
            check(!reachable.SelectMany(Calls).Any(c=>c.DeclaringType.FullName.StartsWith("System.IO.",StringComparison.Ordinal)||c.DeclaringType.FullName.StartsWith("System.Net.",StringComparison.Ordinal)),"prerequisite UI tick adds no filesystem or HTTP work: "+ui.Name);
        }
        var status=mods.Methods.Single(m=>m.Name=="Status");
        check(Calls(status).Any(m=>m.DeclaringType.FullName==maps.FullName&&m.Name=="Status")&&Calls(status).Any(m=>m.Name=="get_IsMapsOpen"),"download and installation messages reach the visible Maps status pane");
        check(Calls(mods.Methods.Single(m=>m.Name=="Busy")).Any(m=>m.DeclaringType.FullName==mods.FullName&&m.Name=="Status"),"busy messages use the same visible-tab status routing");
        check(!Reads(maps.Methods.Single(m=>m.Name=="RefreshRequisites"),"status"),"refreshing prerequisite rows does not replace download progress with an ordinary map status");

        var previewFlow=Types(new[]{manager}).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&(m.DeclaringType.Name.StartsWith("<ReadMapPreview>",StringComparison.Ordinal)||m.Name=="ReadMapPreview")).ToArray();
        check(previewFlow.Any(m=>Reads(m,"Requisites","KellysJOINCHECK.MapPreviewManifest")&&Writes(m,"Requisites","KellysJOINCHECK.MapEntry"))&&previewFlow.Any(m=>Reads(m,"RequisiteError","KellysJOINCHECK.MapPreviewManifest")&&Writes(m,"RequisiteError","KellysJOINCHECK.MapEntry")),"selected embedded map JSON supplies its declarations and validation error to the live map card");
        check(previewFlow.SelectMany(Calls).Any(m=>m.Name=="RefreshMapPreview"&&m.DeclaringType.FullName==mods.FullName)&&Calls(maps.Methods.Single(m=>m.Name=="RefreshPreview")).Any(m=>m.Name=="RefreshRequisites"&&m.DeclaringType.FullName==maps.FullName),"asynchronous embedded map preview completion refreshes its prerequisite list flag and actions");
        var keep=manager.Methods.Single(m=>m.Name=="KeepMapPreview");
        check(Writes(keep,"Requisites","KellysJOINCHECK.MapEntry")&&Writes(keep,"RequisiteError","KellysJOINCHECK.MapEntry"),"folder refresh preserves already-read map prerequisite metadata without reopening its bundle");
    }

    private static void Rows(Action<bool,string> check,TypeDefinition owner,string refreshName)
    {
        var refresh=owner.Methods.Single(m=>m.Name==refreshName);
        var set=owner.Methods.Single(m=>m.Name=="Set");
        var reflow=Reachable(refresh,Types(new[]{owner}).ToArray()).ToArray();
        check(Strings(set).Contains("Prerequisite error")&&(Reads(refresh,"rowErrors")||Reads(refresh,"requisiteErrors")),"affected entries have their own readable prerequisite line beneath ordinary version/state text: "+owner.Name);
        check(refresh.Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.Name=="StatusDangerColor"&&f.DeclaringType.FullName=="NuclearOption.Networking.Lobbies.LobbyListItem")&&Calls(refresh).Any(m=>m.Name=="set_color"),"affected list entries use the game's native danger colour: "+owner.Name);
        check(reflow.SelectMany(Calls).Any(m=>m.Name=="GetPreferredValues")&&reflow.SelectMany(Calls).Any(m=>m.Name=="Top"&&m.DeclaringType.FullName==owner.FullName)&&reflow.SelectMany(Calls).Any(m=>m.Name=="set_sizeDelta"),"wrapped prerequisite errors expand rows and the scrolling content instead of overlapping the next entry: "+owner.Name);
        check(Strings(refresh).Contains("Dependencies ready")&&Calls(owner.Methods.Single(m=>m.Name=="DrawRequisites")).Any(m=>m.Name=="RequisiteSummary"),"ready prerequisites have a compact list flag and full detail-page summary: "+owner.Name);
    }

    private static IEnumerable<MethodDefinition> Reachable(MethodDefinition root,TypeDefinition[] all)
    {
        var methods=all.SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToDictionary(m=>m.FullName,StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);var pending=new Stack<MethodDefinition>();pending.Push(root);
        while(pending.Count>0)
        {var next=pending.Pop();if(!seen.Add(next.FullName))continue;yield return next;foreach(var call in Calls(next))if(methods.TryGetValue(call.FullName,out var target))pending.Push(target);}
    }
    private static bool Reads(MethodDefinition method,string field,string? owner=null)=>method.HasBody&&method.Body.Instructions.Any(i=>(i.OpCode.Code==Code.Ldfld||i.OpCode.Code==Code.Ldsfld)&&i.Operand is FieldReference f&&f.Name==field&&(owner==null||f.DeclaringType.FullName==owner));
    private static bool Writes(MethodDefinition method,string field,string? owner=null)=>method.HasBody&&method.Body.Instructions.Any(i=>(i.OpCode.Code==Code.Stfld||i.OpCode.Code==Code.Stsfld)&&i.Operand is FieldReference f&&f.Name==field&&(owner==null||f.DeclaringType.FullName==owner));
    private static HashSet<string> Strings(MethodDefinition method)=>new HashSet<string>(method.Body.Instructions.Where(i=>i.OpCode.Code==Code.Ldstr).Select(i=>(string)i.Operand),StringComparer.Ordinal);
    private static IEnumerable<MethodReference> Calls(MethodDefinition method)=>method.HasBody?method.Body.Instructions.Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand):Array.Empty<MethodReference>();
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
    {foreach(var type in roots){yield return type;foreach(var nested in Types(type.NestedTypes))yield return nested;}}
}
