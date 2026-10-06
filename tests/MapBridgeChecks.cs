using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class MapBridgeChecks
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client)
    {
        var all=Types(client.MainModule.Types).ToArray();
        var bridge=all.SingleOrDefault(t=>t.FullName=="KellysJOINCHECK.MapBundleBridge");
        check(bridge!=null,"the client includes its optional NOCustomMaps metadata bridge");
        if(bridge==null)return;
        var bridgeMethods=Types(new[]{bridge!}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
        var manager=all.Single(t=>t.FullName=="KellysJOINCHECK.ModManager");
        var workflowMethods=Types(new[]{manager}).SelectMany(t=>t.Methods).Where(m=>m.HasBody&&(m.Name.IndexOf("Map",StringComparison.Ordinal)>=0||m.DeclaringType.Name.IndexOf("Map",StringComparison.Ordinal)>=0)).ToArray();
        var calls=bridgeMethods.Concat(workflowMethods).SelectMany(Calls).ToArray();
        var strings=bridgeMethods.SelectMany(m=>m.Body.Instructions).Where(i=>i.OpCode.Code==Code.Ldstr).Select(i=>(string)i.Operand).ToArray();
        check(!client.MainModule.AssemblyReferences.Any(a=>a.Name.IndexOf("CustomMaps",StringComparison.OrdinalIgnoreCase)>=0),"NOCustomMaps remains optional without a hard assembly reference");
        check(!calls.Any(m=>m.DeclaringType.FullName=="System.Reflection.Assembly"&&(m.Name.StartsWith("Load",StringComparison.Ordinal)||m.Name=="UnsafeLoadFrom")),"the map bridge never executes or loads a plugin assembly to discover installed maps");
        check(!calls.Any(m=>m.DeclaringType.FullName=="UnityEngine.AssetBundle"&&(m.Name.StartsWith("LoadFrom",StringComparison.Ordinal)||m.Name.StartsWith("LoadAllAssets",StringComparison.Ordinal)||m.Name.StartsWith("Unload",StringComparison.Ordinal))),"map previews do not reopen bundles, load every asset or unload the loader's live bundles");
        check(!calls.Any(m=>m.DeclaringType.FullName=="UnityEngine.Object"&&m.Name.StartsWith("Instantiate",StringComparison.Ordinal)),"reading a map preview never instantiates terrain or its root prefab");
        var assetCalls=calls.Where(m=>m.DeclaringType.FullName=="UnityEngine.AssetBundle"&&m.Name.StartsWith("LoadAsset",StringComparison.Ordinal)).ToArray();
        check(assetCalls.Length==3&&assetCalls.All(m=>m.Name=="LoadAssetAsync"&&m is GenericInstanceMethod generic&&generic.GenericArguments.All(a=>new[]{"UnityEngine.TextAsset","UnityEngine.Texture2D","UnityEngine.Sprite"}.Contains(a.FullName))),"selected map previews asynchronously load only explicitly typed text or image assets");
        check(!strings.Contains("Maps",StringComparer.Ordinal)&&!strings.Contains("Root",StringComparer.Ordinal)&&!strings.Contains("RootPrefab",StringComparer.Ordinal)&&!strings.Contains("ScanAndLoad",StringComparer.Ordinal)&&!calls.Any(m=>m.Name=="get_Maps"&&m.DeclaringType.FullName=="CustomMaps.BundleLoader"),"optional reflection avoids the blocking registry property, root resolution and loader rescans");
        check(strings.Contains("_settled",StringComparer.Ordinal)&&strings.Contains("_opened",StringComparer.Ordinal)&&strings.Contains("File",StringComparer.Ordinal)&&strings.Contains("Manifest",StringComparer.Ordinal)&&strings.Contains("Details",StringComparer.Ordinal)&&strings.Contains("Bundle",StringComparer.Ordinal),"the bridge reads NOCustomMaps 1.3.0's inert opened-map fields and settlement state");
        check(calls.Any(m=>m.DeclaringType.FullName=="KellysJOINCHECK.MapRegistrySnapshot"&&m.Name=="Take")&&strings.Any(s=>s.StartsWith("Map registry limit reached.",StringComparison.Ordinal)),"the runtime bridge uses the tested bounded registry snapshot and reports incomplete results at its cap");
        check(!calls.Any(m=>m.Name=="ComputeHash"||m.DeclaringType.FullName=="KellysJOINCHECK.ModUpdateFiles"&&m.Name=="Hash"),"preview enrichment does not rehash multi-gigabyte installed maps");

        Watcher(check,all);
        Lifecycle(check,all);
    }

    private static void Watcher(Action<bool,string> check,TypeDefinition[] all)
    {
        var watcher=all.Single(t=>t.FullName=="KellysJOINCHECK.MapFolderMonitor");
        foreach(string name in new[]{"Changed","Renamed","Error"})
        {
            var callback=watcher.Methods.Single(m=>m.Name==name);
            check(Calls(callback).All(m=>m.DeclaringType.FullName=="System.Threading.Interlocked"&&m.Name=="Exchange"),"map watcher "+name+" callback only marks work pending without Unity, logging or file scans");
        }
        var refresh=watcher.Methods.Single(m=>m.Name=="Refresh").Body.Instructions;
        var recursive=refresh.Where(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.IO.FileSystemWatcher"&&m.Name=="set_IncludeSubdirectories").ToArray();
        check(recursive.Length==1&&recursive[0].Previous.OpCode.Code==Code.Ldc_I4_0,"map folder monitoring does not watch the whole plugins tree recursively");
        var dispose=watcher.Methods.Single(m=>m.Name=="Dispose");
        var stop=watcher.Methods.Single(m=>m.Name=="Stop");
        check(Calls(dispose).Any(m=>m.Name=="Stop"&&m.DeclaringType.FullName==watcher.FullName)&&Calls(stop).Any(m=>m.Name=="Dispose"&&m.DeclaringType.FullName=="System.ComponentModel.Component"),"disposing the map monitor releases its operating-system watcher");
        check(new[]{"Created","Changed","Deleted","Renamed","Error"}.All(name=>Calls(stop).Any(m=>m.Name=="remove_"+name)),"map watcher callbacks are unsubscribed before its handle is disposed");
    }

    private static void Lifecycle(Action<bool,string> check,TypeDefinition[] all)
    {
        var ui=all.Single(t=>t.FullName=="KellysJOINCHECK.NativeModsUi");
        var hide=ui.Methods.Single(m=>m.Name=="Hide");
        check(Calls(hide).Any(m=>m.Name=="Hide"&&m.DeclaringType.FullName=="KellysJOINCHECK.NativeMapsUi"),"closing Mods also closes its native Maps view");
        var hideInstructions=hide.Body.Instructions.ToArray();
        check(hideInstructions.Any(i=>i.Operand is FieldReference f&&f.Name=="mapsChanged"&&i.Next.OpCode.Code==Code.Ldc_I4_0&&i.Next.Next.Operand is MethodReference m&&m.Name=="Invoke"),"closing the Mods UI sends the Maps callback an explicit closed state");
        var manager=all.Single(t=>t.FullName=="KellysJOINCHECK.ModManager");
        var changed=manager.Methods.Single(m=>m.Name=="MapsChanged");
        var close=manager.Methods.Single(m=>m.Name=="CloseMaps");
        check(Calls(changed).Any(m=>m.DeclaringType.FullName==manager.FullName&&m.Name=="CloseMaps")&&Calls(close).Any(m=>m.DeclaringType.FullName=="KellysJOINCHECK.MapFolderMonitor"&&m.Name=="Dispose"),"Maps tab changes dispose its prior monitor through CloseMaps");
        check(Calls(manager.Methods.Single(m=>m.Name=="Dispose")).Any(m=>m.DeclaringType.FullName==manager.FullName&&m.Name=="CloseMaps"),"disposing the mod manager closes its map watcher even if the UI was lost");
        check(Calls(manager.Methods.Single(m=>m.Name=="Open")).Any(m=>m.DeclaringType.FullName==manager.FullName&&m.Name=="MapsChanged"),"the native Maps callback is wired to the manager's scoped monitor lifecycle");
    }

    private static IEnumerable<MethodReference> Calls(MethodDefinition method)=>method.HasBody?method.Body.Instructions.Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand):Array.Empty<MethodReference>();
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
    { foreach(var type in roots) { yield return type;foreach(var nested in Types(type.NestedTypes))yield return nested; } }
}
