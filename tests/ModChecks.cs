using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KellysJOINCHECK;
using Mono.Cecil;

internal static partial class ModRegression
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client,AssemblyDefinition host,string game,string startupPath)
    {
        var a=new InstalledContent { Id="A",Version="1.0",Enabled=false,Reloadable=true };
        var b=new InstalledContent { Id="B",Version="1.0",Enabled=true,Reloadable=true };
        string local="0.34.2_com.nikkorap.blueprinter-v2.0.1_--B-v1.0";
        string server="0.34.2_com.nikkorap.blueprinter-v2.0.1_--A-v1.0";
        var plan=ModMatchPlan.Create(server,local,new[]{a,b});
        check(plan.CanApply&&plan.Enable.Single()==a&&plan.Disable.Single()==b,"matching enables installed content and disables extra content");
        check(!ModMatchPlan.Create(server.Replace("A-v1.0","A-v1.1"),local,new[]{a,b}).CanApply,"wrong installed version never substitutes a newer or older pack");
        check(!ModMatchPlan.Create(server,local,new[]{b}).CanApply,"missing packs do not produce a partial change plan");
        check(!ModMatchPlan.Create("0.34.2_abcdefabcdef",local,new[]{a,b}).CanApply,"opaque server hashes never disable guessed mods");
        check(!ModMatchPlan.Create(server.Replace("0.34.2","0.35"),local,new[]{a,b}).CanApply,"game updates cannot be fixed by enabling mods");
        check(!ModMatchPlan.Create(server.Replace("blueprinter-v2.0.1","blueprinter-v2.1.0"),local,new[]{a,b}).CanApply,"loader mismatch requires the actual loader change");
        check(!ModMatchPlan.Create(server,local,new[]{a,a,b}).CanApply,"ambiguous duplicate versions prevent automatic matching");
        a.Reloadable=false;check(!ModMatchPlan.Create(server,local,new[]{a,b}).CanApply,"unsupported reload requires restart");a.Reloadable=true;
        check(ModMatchPlan.Create(server,local.Replace("B-v1.0","A-v1.0"),new[]{new InstalledContent { Id="A",Version="1.0",Enabled=true }}).CanApply,"matching content does not require a needless reload");
        var native=new List<int>{2,4};var first=new PrefabHashInput { Key="a",Original=0 };var second=new PrefabHashInput { Key="b",Original=0 };
        var full=ContentPrefabHashes.Assign(native,new[]{second,first});
        check(full[first]==1&&full[second]==3,"network hashes use native reservations and sorted bundle keys");
        var subset=ContentPrefabHashes.Assign(native,new[]{second});check(subset[second]==1,"removing the first pack rebuilds the subset's fresh startup hash");
        check(ContentPrefabHashes.Assign(native,new[]{second,first})[second]==full[second],"repeated profile changes reproduce the same network hashes");
        first.Original=10;first.InitiallyVisible=true;check(ContentPrefabHashes.Assign(native,new[]{first})[first]==1,"already visible hashes follow Blueprinter collision behavior");
        first.InitiallyVisible=false;check(ContentPrefabHashes.Assign(native,new[]{first})[first]==10,"newly loaded unused prefab hashes are reused");
        var list=new List<int>{1,2};var dictionary=new Dictionary<string,int>{{"base",1}};int[] array={3,4}; object? field=list;
        var snapshot=new ModStateSnapshot();snapshot.Value(()=>field,v=>field=v);snapshot.Value(()=>dictionary,v=>dictionary=(Dictionary<string,int>)v!);snapshot.Value(()=>array,v=>array=(int[])v!);
        list.Add(9);dictionary.Add("mod",2);array[0]=7;check(!snapshot.Unchanged(),"collection mutations are detected before another reload");
        field=new List<int>{8};snapshot.Restore();check(ReferenceEquals(field,list)&&list.SequenceEqual(new[]{1,2})&&dictionary.Count==1&&array[0]==3,"rollback restores contents and original shared references");
        var rollback=snapshot.CaptureCurrent();list.Add(6);var expected=snapshot.CaptureCurrent();check(expected.Unchanged(),"current state captures the committed selection");snapshot.Restore();rollback.Restore();check(list.SequenceEqual(new[]{1,2}),"repeated restores do not accumulate entries");
        dictionary["base"]=5;check(!snapshot.Unchanged(),"foreign dictionary changes cannot be silently overwritten");snapshot.Restore();
        var sorted=new List<int>{3,2,1};var sortable=new ModStateSnapshot();sortable.Value(()=>sorted,v=>sorted=(List<int>)v!,true,true);sorted.Sort();check(sortable.Unchanged(),"native display sorting does not block a content reload");sorted[0]=9;check(!sortable.Unchanged(),"replacing a definition still blocks reload even when ordering is ignored");
        object? nothing=null;var empty=new ModStateSnapshot();empty.Value(()=>nothing,v=>nothing=v);nothing=new List<int>();empty.Restore();check(nothing==null,"uninitialised lists return to their original null state");
        check(ModManifest.SafeLink("https://discord.gg/shipyard")&&ModManifest.SafeLink("https://github.com/Aryx3D/AryxWeaponryPack"),"supplied social links are accepted");
        foreach(var url in new[]{"javascript:alert(1)","file:///C:/secret","https://name:password@example.com","relative","data:text/plain,test"}) check(!ModManifest.SafeLink(url),"unsafe link rejected: "+url.Split(':')[0]);
        string temp=Path.Combine(Path.GetTempPath(),"doorman-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            check(ModManifest.ImagePath(temp,"card.png")!=null,"local preview image accepted");
            foreach(var path in new[]{"../secret.png","..\\secret.png","C:\\secret.png","https://host/card.png","card.dll"}) check(ModManifest.ImagePath(temp,path)==null,"unsafe image path rejected");
            string file=Path.Combine(temp,"profile.json");var profile=new ModProfile { DisabledContent=new[]{"A","A"},DisabledPlugins=new[]{"test.mod"},AutoMatchServers=false };
            ModJson.Save(file,profile);var parsed=ModJson.Read<ModProfile>(file);parsed.Validate();check(parsed.DisabledContent.SequenceEqual(new[]{"A"})&&!parsed.AutoMatchServers,"profile persists and normalises identifiers");
            profile.AutoMatchServers=true;ModJson.Save(file,profile);check(File.Exists(file+".bak")&&ModJson.Read<ModProfile>(file+".bak").AutoMatchServers==false,"profile replacement retains the previous profile backup");
            foreach(var id in new[]{"kelly.nuclearoption.joincheck","kelly.nuclearoption.joincheck.server","com.nikkorap.blueprinter"})
            { try {new ModProfile {DisabledPlugins=new[]{id}}.Validate();check(false,"protected plugin disable");}catch(InvalidDataException){check(true,"required plugin remains enabled");} }
            var copy=profile.Copy();copy.DisabledContent[0]="X";check(profile.DisabledContent[0]=="A","a staged profile cannot mutate the saved selection");
            string manifest=Path.Combine(temp,"card.doorman.json");File.WriteAllText(manifest,"{\"plugin\":\"my.mod\",\"name\":\"A <mod>\",\"description\":\"Hello\",\"links\":[{\"label\":\"Bad\",\"url\":\"javascript:alert(1)\"},{\"label\":\"GitHub\",\"url\":\"https://github.com\"}]}");
            var card=ModJson.Read<ModManifest>(manifest);card.Directory=temp;card.Validate();check(card.Name=="A <mod>"&&card.Links.Length==1,"optional preview fields and untrusted links are handled safely");
            File.WriteAllText(manifest,new string('x',65537));try{ModJson.Read<ModManifest>(manifest);check(false,"oversized JSON");}catch(InvalidDataException){check(true,"oversized metadata rejected before deserialisation");}
        }
        finally { Directory.Delete(temp,true); }
        CheckPreviewMatching(check,client,game);
        var png=new byte[24];png[0]=137;png[1]=80;png[2]=78;png[3]=71;png[18]=7;png[19]=8;png[22]=4;png[23]=176;check(ModImageHeader.Allowed(png),"1800 by 1200 preview accepted");
        png[16]=255;check(!ModImageHeader.Allowed(png),"oversized image rejected before texture allocation");
        check(!ModImageHeader.Allowed(new byte[10]),"truncated image rejected");
        var jpeg=new byte[]{255,216,255,192,0,8,8,0,100,0,100,0,255,217,0,0,0,0,0,0,0,0,0,0};check(ModImageHeader.Allowed(jpeg),"JPEG dimensions are checked before allocation");
        check(host.MainModule.GetType("KellysJOINCHECK.ModManager")==null&&host.MainModule.GetType("KellysJOINCHECK.BlueprinterReload")==null,"server binary excludes client mod management and reload code");
        var manager=client.MainModule.GetType("KellysJOINCHECK.ModManager");
        check(manager!=null&&!manager.Methods.Any(m=>m.Name=="Update"),"mod discovery has no background frame scan");
        check(!client.MainModule.GetType("KellysJOINCHECK.BlueprinterReload").Methods.Any(m=>m.Name=="Update"),"content reload does no work between explicit actions");
        check(client.MainModule.GetType("KellysJOINCHECK.BlueprinterReload").Methods.Single(m=>m.Name=="Apply").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="MenuSafety"),"the reload transaction itself rejects active missions and connections");
        var labels=client.MainModule.GetType("KellysJOINCHECK.NativeModsUi").Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is MethodReference m&&m.Name=="GetComponentInChildren").ToList();
        check(labels.Count>0&&labels.All(i=>((MethodReference)i.Operand).Parameters.Count==1&&i.Previous.OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4_1),"Mods labels resolve while the new screen is still inactive");
        var join=client.MainModule.GetType("KellysJOINCHECK.ClientPlugin").Methods.Single(m=>m.Name=="JoinPrefix");
        check(join.ReturnType.FullName=="System.Boolean"&&join.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="BeforeJoin"),"only pre-join selection intercepts the original request");
        var methods=AllTypes(client.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand).ToList();
        check(!methods.Any(m=>m.Name=="Unload"&&m.DeclaringType.FullName=="UnityEngine.AssetBundle"),"reload retains bundles instead of destroying live references");
        using(var bp=AssemblyDefinition.ReadAssembly(Path.Combine(game,"BepInEx","plugins","Blueprinter_2.0.1.dll")))
        {
            var runner=bp.MainModule.GetType("Blueprinter.PatchRunner");
            var registry=bp.MainModule.GetType("Blueprinter.BundleRegistry");
            check(registry.Methods.Any(m=>m.Name=="ScanAndLoadCoroutine"&&m.ReturnType.FullName=="System.Collections.IEnumerator")&&registry.Methods.Any(m=>m.Name=="FastLoad"),"startup selection covers both installed Blueprinter scan paths");
            var routine=bp.MainModule.GetType("Blueprinter.Plugin").NestedTypes.Single(t=>t.Name.StartsWith("<RunRoutine>")).Methods.Single(m=>m.Name=="MoveNext").Body.Instructions.ToList();
            int scan=routine.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="ScanAndLoadCoroutine"),fast=routine.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="FastLoad"),hash=routine.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="AssignFromBundles");
            check(scan>=0&&fast>=0&&hash>scan&&hash>fast,"native startup selects bundles before assigning hashes in either loading mode");
            check(runner.Fields.Any(f=>f.Name=="<bundles>P")&&runner.Fields.Any(f=>f.Name=="progress"),"installed loader stores the captured registry and operation progress");
            check(runner.Methods.Any(m=>m.Name=="ApplyAllOps"&&m.Parameters.Count==1),"installed loader exposes registration application");
            check(bp.MainModule.GetType("Blueprinter.PrefabHashAssigner").Methods.Any(m=>m.Name=="AssignFromBundles"),"installed loader has the validated prefab allocation entrypoint");
            check(bp.MainModule.GetType("Blueprinter.PrefabHashAssigner").Methods.Single(m=>m.Name=="AssignFromBundles").Body.Instructions.Count(i=>i.Operand is GenericInstanceMethod m&&m.Name=="FindObjectsOfTypeAll"&&m.GenericArguments.Any(t=>t.FullName=="Mirage.NetworkIdentity"))==1,"startup hash reservation has one exact resource query to preserve");
        }
        using(var bepinex=AssemblyDefinition.ReadAssembly(Path.Combine(game,"BepInEx","core","BepInEx.dll")))
            check(bepinex.MainModule.GetType("BepInEx.Bootstrap.Chainloader").Methods.Single(m=>m.Name=="Start").Body.Instructions.Count(i=>i.Operand is GenericInstanceMethod m&&m.Name=="FindPluginTypes"&&m.GenericArguments.Any(t=>t.FullName=="BepInEx.PluginInfo"))==1,"startup helper targets discovery after the cache-aware loader call");
        using(var startup=AssemblyDefinition.ReadAssembly(startupPath))
        {
            var patcher=startup.MainModule.GetType("DoormanStartupPatcher");
            check(patcher.Methods.Any(m=>m.Name=="Finish"&&m.IsPublic&&m.IsStatic),"startup helper exposes the preloader Finish contract");
            check(patcher.Methods.Any(m=>m.Name=="Patch"&&m.Parameters.Single().ParameterType.FullName=="Mono.Cecil.AssemblyDefinition"),"helper satisfies the BepInEx patcher contract");
            check(AllTypes(startup.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand as string=="-batchmode"),"startup helper skips headless hosts");
            check(startup.MainModule.GetType("KellysJOINCHECK.ClientPlugin")==null,"startup helper is packaged separately from the game plugin");
            var filter=startup.MainModule.GetType("DoormanRuntimeHooks").Methods.Single(m=>m.Name=="FilterPlugins");
            check(filter.ReturnType.FullName.Contains("Dictionary")&&filter.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="ToDictionary"),"cached discovery is copied instead of mutating cached plugin lists");
            check(!AllTypes(startup.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m&&m.Name=="Save"&&m.DeclaringType.FullName=="KellysJOINCHECK.ModJson"),"startup filtering never rewrites user preferences");
        }
        using(var mirage=AssemblyDefinition.ReadAssembly(Path.Combine(game,"NuclearOption_Data","Managed","Mirage.dll")))
            check(mirage.MainModule.GetType("Mirage.ClientObjectManager").Methods.Single(m=>m.Name=="OnClientDisconnected").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="ClearSpawners"),"native disconnect clears stale prefab registrations before another profile joins");
        using(var gameAssembly=AssemblyDefinition.ReadAssembly(Path.Combine(game,"NuclearOption_Data","Managed","Assembly-CSharp.dll")))
        {
            var network=gameAssembly.MainModule.GetType("NuclearOption.Networking.NetworkManagerNuclearOption");
            check(network.Methods.Single(m=>m.Name=="ClientStarted").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="RegisterPrefabs"),"native connection registers the current content profile");
            check(network.Methods.Single(m=>m.Name=="RegisterPrefabs").Body.Instructions.Any(i=>i.Operand is FieldReference f&&f.FullName.Contains("Encyclopedia::aircraft")),"native prefab registration reads the rebuilt encyclopedia lists");
        }
    }
    private static void CheckPreviewMatching(Action<bool,string> check,AssemblyDefinition client,string game)
    {
        var wrapper=new ModManifest {Plugin="Aryx_NavalInterceptor1",Bundle="interceptor1",Name="Custom Eclipse",Image="fs41.png"};
        var exact=new ModManifest {Plugin="another.plugin",Bundle="aryx_interceptor1",Name="Exact content card",Image="exact.png"};
        var cards=new Dictionary<string,ModManifest>(StringComparer.Ordinal) {{"plugin:"+wrapper.Plugin,wrapper},{"content:"+wrapper.Bundle,wrapper}};
        check(ReferenceEquals(ModManifest.Match(cards,true,"aryx_interceptor1",wrapper.Plugin),wrapper),"a legacy bundle ID inherits its exact installed wrapper's preview even when the wrapper is disabled");
        check(ReferenceEquals(ModManifest.Match(cards,false,wrapper.Plugin,""),wrapper),"a plugin page uses its own exact preview");
        cards.Add("content:"+exact.Bundle,exact);
        check(ReferenceEquals(ModManifest.Match(cards,true,exact.Bundle,wrapper.Plugin),exact),"an exact content preview takes priority over a wrapper preview");
        cards["content:"+exact.Bundle]=null!;
        check(ModManifest.Match(cards,true,exact.Bundle,wrapper.Plugin)==null,"an ambiguous direct content preview cannot silently inherit a wrapper preview");
        cards.Remove("content:"+exact.Bundle);cards["plugin:"+wrapper.Plugin]=null!;
        check(ModManifest.Match(cards,true,exact.Bundle,wrapper.Plugin)==null,"duplicate wrapper previews remain blocked when the wrapper is disabled");
        cards["plugin:"+wrapper.Plugin]=wrapper;
        check(ModManifest.Match(cards,true,"unknown","unrelated.plugin")==null&&ModManifest.Match(cards,true,"unknown","")==null,"unrelated or missing wrapper ownership never guesses a preview from names");
        check(ModManifest.Match(cards,false,"unknown",wrapper.Plugin)==null,"a plugin entry cannot borrow a different plugin's preview");
        check(ModManifest.Match(cards,true,"aryx_interceptor1","aryx_navalinterceptor1")==null,"preview identifiers remain case-sensitive and exact");

        var catalog=client.MainModule.GetType("KellysJOINCHECK.ModCatalog");
        var read=catalog.Methods.Single(m=>m.Name=="Read").Body.Instructions;
        check(read.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.ModManifest"&&m.Name=="Match")&&read.Any(i=>i.Operand is FieldReference f&&f.Name=="WrapperPlugin"),"catalog preview lookup passes the wrapper identity discovered from installed DLLs");
        check(!read.Any(i=>i.Operand is FieldReference f&&f.Name=="Instance"||i.Operand is MethodReference m&&m.Name=="get_Instance"),"preview attachment does not require a loaded plugin instance");
        var enrichFilter=AllTypes(new[]{catalog}).SelectMany(t=>t.Methods).Where(m=>m.Name.StartsWith("<Enrich>")&&m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        check(enrichFilter.Any(i=>i.Operand is FieldReference f&&f.Name=="Manifest")&&enrichFilter.Any(i=>i.Operand is FieldReference f&&f.Name=="PreviewAmbiguous"),"NOMNOM enrichment respects existing previews and ambiguous custom identities");
    }
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots) {foreach(var type in roots){yield return type;foreach(var nested in AllTypes(type.NestedTypes))yield return nested;}}
}
