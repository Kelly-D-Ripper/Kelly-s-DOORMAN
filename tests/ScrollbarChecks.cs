using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ScrollbarRegression
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client)
    {
        const string helperName="KellysJOINCHECK.NativeScrollbars",scrollType="UnityEngine.UI.ScrollRect";
        var helper=client.MainModule.GetType(helperName);
        check(helper!=null,"the built client includes the shared owned-UI scrollbar helper");
        if (helper==null) return;
        check(helper.IsAbstract && helper.IsSealed && helper.BaseType?.FullName=="System.Object","scrollbar attachment is a static helper without a Unity component lifetime");
        var attach=helper.Methods.SingleOrDefault(m=>m.Name=="Attach" && m.IsStatic && m.Parameters.Count==1 && m.Parameters[0].ParameterType.FullName==scrollType);
        check(attach!=null && attach.HasBody,"the shared scrollbar entry point receives one runtime ScrollRect instance");
        if (attach==null || !attach.HasBody) return;
        var code=attach.Body.Instructions;
        check(code.Count(i=>Creates(i,"UnityEngine.UI.Scrollbar"))==1,"each attachment creates one native vertical Scrollbar");
        check(CallCount(code,"set_verticalScrollbar")==1 && SetsInteger(code,"set_vertical",1) && SetsInteger(code,"set_verticalScrollbarVisibility",0),"the attached Scrollbar is assigned vertically with Permanent visibility");
        check(CallCount(code,"set_handleRect")==1 && CallCount(code,"set_targetGraphic")>=1 && SetsInteger(code,"set_direction",2),"the native Scrollbar receives its sliding handle, target graphic and BottomToTop direction");
        check(CallCount(code,"get_viewport")>0 && CallCount(code,"set_offsetMax")>0,"the scrollbar reserves a viewport gutter through offsetMax");

        var helperMethods=Types(new[]{helper}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
        var helperCode=helperMethods.SelectMany(m=>m.Body.Instructions).ToArray();
        check(SetsInteger(helperCode,"set_mode",0) && CallCount(helperCode,"set_navigation")>0,"scrollbars disable keyboard navigation without stealing native menu focus");
        check(CallCount(helperCode,"set_colors")>0 && helperCode.Any(IsDefaultColorBlock),"scrollbars initialize the standard ColorBlock and assign it to the native control");
        var opaqueColors=new[]{"set_normalColor","set_highlightedColor","set_pressedColor","set_disabledColor"};
        check(opaqueColors.All(setter=>helperCode.Any(i=>ColorBlockCall(i,setter) && i.Previous?.OpCode.Code==Code.Newobj && i.Previous.Operand is MethodReference ctor && ctor.DeclaringType.FullName=="UnityEngine.Color" && ctor.Parameters.Count==4 && Float(i.Previous.Previous)==1f)) && helperCode.Any(i=>ColorBlockCall(i,"set_selectedColor") && i.Previous!=null && ColorBlockCall(i.Previous,"get_highlightedColor")),"scrollbars assign opaque transition colors and copy the highlighted color for selection");
        check(helperCode.Any(i=>ColorBlockCall(i,"set_colorMultiplier") && Float(i.Previous)==1f),"scrollbar transition colors retain a visible unit color multiplier");

        var owners=new[]{"KellysJOINCHECK.NativeJoinUi","KellysJOINCHECK.NativeModsUi","KellysJOINCHECK.NativeMapsUi","KellysJOINCHECK.NativeModListsUi"};
        foreach (string owner in owners)
        {
            var type=client.MainModule.GetType(owner);
            check(type!=null,"the built client retains scrollbar owner "+owner);
            if (type==null) continue;
            var methods=Types(new[]{type}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
            var creators=methods.Where(m=>m.Body.Instructions.Any(i=>Creates(i,scrollType))).ToArray();
            check(creators.Length==1,"owned UI scroll creation has one constructor or reusable factory: "+owner);
            foreach (var creator in creators)
            {
                var body=creator.Body.Instructions;
                check(body.Count(i=>CallsAttach(i,helperName))==1,"each owned ScrollRect creation routes through Attach exactly once: "+owner);
                int made=body.ToList().FindIndex(i=>Creates(i,scrollType)),attached=body.ToList().FindIndex(i=>CallsAttach(i,helperName));
                check(made>=0 && attached>made,"the helper receives an already-created owned ScrollRect: "+owner);
            }
            check(methods.SelectMany(m=>m.Body.Instructions).Count(i=>CallsAttach(i,helperName))==1,"owned UI does not add a second scrollbar elsewhere: "+owner);
            check(!methods.SelectMany(m=>m.Body.Instructions).Any(i=>Creates(i,"UnityEngine.UI.Scrollbar")),"owned UI uses the shared scrollbar construction instead of retaining duplicate inline bars: "+owner);
        }

        var all=Types(client.MainModule.Types).ToArray();
        var callers=all.Where(t=>t.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>CallsAttach(i,helperName))).ToArray();
        check(callers.All(t=>owners.Any(owner=>t.FullName==owner || t.FullName.StartsWith(owner+"/",StringComparison.Ordinal))),"scrollbar attachment is restricted to DOORMAN's four owned UI classes rather than native game or browser controls");
        check(!helperMethods.Any(m=>new[]{"Update","LateUpdate","FixedUpdate","OnGUI","Tick","OnEnable","OnDisable","OnDestroy"}.Contains(m.Name)),"the shared scrollbar helper has no gameplay callbacks or frame timers");
        var forbidden=new HashSet<string>(StringComparer.Ordinal) { "StartCoroutine","InvokeRepeating","Invoke","DontDestroyOnLoad","FindObjectOfType","FindObjectsOfType","FindObjectsOfTypeAll","FindFirstObjectByType","FindAnyObjectByType","UnloadAllAssetBundles" };
        check(!helperCode.Any(i=>i.Operand is MethodReference m && (forbidden.Contains(m.Name) || m.DeclaringType.FullName.StartsWith("System.Threading.",StringComparison.Ordinal) || m.DeclaringType.FullName.StartsWith("System.Timers.",StringComparison.Ordinal))),"scrollbar construction has no global object search, persistence, gameplay scheduling or background worker behavior");
        PreviewSelection(check,client);
        StatusPane(check,client);
    }

    private static void StatusPane(Action<bool,string> check,AssemblyDefinition client)
    {
        var expected=new Dictionary<string,int>(StringComparer.Ordinal)
        {
            ["KellysJOINCHECK.NativeModsUi"]=4,
            ["KellysJOINCHECK.NativeMapsUi"]=3,
            ["KellysJOINCHECK.NativeModListsUi"]=2
        };
        foreach(var entry in expected)
        {
            var type=client.MainModule.GetType(entry.Key);
            var constructor=type.Methods.Single(m=>m.IsConstructor&&!m.IsStatic);
            check(constructor.Body.Instructions.Count(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName==entry.Key&&m.Name=="Scroll")==entry.Value,"all owned list, preview and text scroll areas are constructed through the shared factory: "+entry.Key);
        }
        var mods=client.MainModule.GetType("KellysJOINCHECK.NativeModsUi");
        var layout=mods.Methods.Single(m=>m.Name=="LayoutStatus").Body.Instructions;
        check(layout.Any(i=>i.Operand is FieldReference f&&f.Name=="statusContent")&&CallCount(layout,"get_width")>0&&CallCount(layout,"GetPreferredValues")==1&&CallCount(layout,"set_sizeDelta")==1,"long Mods status messages measure the narrowed scroll content and expand their readable height");
        var visibility=mods.Methods.Single(m=>m.Name=="ApplyTabVisibility").Body.Instructions.ToArray();
        int pane=Array.FindIndex(visibility,i=>i.Operand is FieldReference f&&f.Name=="modStatusPane");
        check(pane>=0&&visibility.Skip(pane).Take(8).Any(i=>i.Operand is MethodReference m&&m.Name=="SetActive"),"switching to Maps hides the complete Mods status pane including its scrollbar");
        var constructorCode=mods.Methods.Single(m=>m.IsConstructor&&!m.IsStatic).Body.Instructions;
        check(constructorCode.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName==mods.FullName&&m.Name=="Bottom"&&Float(i.Previous)==34f&&Float(i.Previous.Previous)==-24f&&Float(i.Previous.Previous.Previous)==55f),"the scrollable Mods status keeps its existing 34-pixel footer footprint");
    }

    private static void PreviewSelection(Action<bool,string> check,AssemblyDefinition client)
    {
        foreach(string owner in new[]{"KellysJOINCHECK.NativeModsUi","KellysJOINCHECK.NativeMapsUi"})
        {
            var type=client.MainModule.GetType(owner);
            var select=type.Methods.Single(m=>m.Name=="Select");
            var code=select.Body.Instructions;
            var resets=code.Where(i=>i.Operand is MethodReference m && m.DeclaringType.FullName=="UnityEngine.UI.ScrollRect" && m.Name=="set_verticalNormalizedPosition").ToArray();
            check(resets.Length==1 && Float(resets[0].Previous)==1f,"changing the selected preview scrolls its own pane back to the top: "+owner);
            if(resets.Length!=1)continue;
            var reset=resets[0];
            check(code.Any(i=>i.Offset<reset.Offset && i.OpCode.FlowControl==FlowControl.Cond_Branch && i.Operand is Instruction target && target.Offset>reset.Offset),"preview scroll reset is conditional so asynchronous redraws preserve the reader's position: "+owner);
            string layout=owner.EndsWith("NativeModsUi",StringComparison.Ordinal)?"LayoutPreview":"DrawPreview";
            check(code.Any(i=>i.Offset<reset.Offset && i.Operand is MethodReference m && m.DeclaringType.FullName==owner && m.Name==layout),"preview scroll resets after the new card has been laid out: "+owner);
            var refresh=type.Methods.Single(m=>m.Name=="RefreshPreview");
            check(!refresh.Body.Instructions.Any(i=>i.Operand is MethodReference m && m.Name=="set_verticalNormalizedPosition"),"asynchronous preview refresh does not reset the scroll position directly: "+owner);
            if(owner.EndsWith("NativeMapsUi",StringComparison.Ordinal))
            {
                check(code.Count(i=>i.Operand is FieldReference f && f.Name=="FilePath")==2 && code.Any(i=>i.Operand is MethodReference m && m.DeclaringType.FullName=="System.String" && m.Name=="Equals" && Integer(i.Previous)==5),"watcher refresh compares map file identity case-insensitively before deciding to reset its preview");
            }
        }
    }

    private static bool CallsAttach(Instruction instruction,string helper) => instruction.Operand is MethodReference method && method.DeclaringType.FullName==helper && method.Name=="Attach";
    private static bool Creates(Instruction instruction,string type) => instruction.Operand is GenericInstanceMethod method && method.Name=="AddComponent" && method.GenericArguments.Count==1 && method.GenericArguments[0].FullName==type;
    private static int CallCount(IEnumerable<Instruction> instructions,string name) => instructions.Count(i=>i.Operand is MethodReference m && m.Name==name);
    private static bool ColorBlockCall(Instruction instruction,string name) => instruction.Operand is MethodReference method && method.DeclaringType.FullName=="UnityEngine.UI.ColorBlock" && method.Name==name;
    private static bool IsDefaultColorBlock(Instruction instruction) => instruction.OpCode.Code==Code.Ldsfld && instruction.Operand is FieldReference field && field.DeclaringType.FullName=="UnityEngine.UI.ColorBlock" && field.Name=="defaultColorBlock" || ColorBlockCall(instruction,"get_defaultColorBlock");
    private static float? Float(Instruction? instruction) => instruction?.OpCode.Code==Code.Ldc_R4 ? Convert.ToSingle(instruction.Operand) : (float?)null;
    private static bool SetsInteger(IEnumerable<Instruction> instructions,string setter,int expected) => instructions.Any(i=>i.Operand is MethodReference m && m.Name==setter && Integer(i.Previous)==expected);
    private static int? Integer(Instruction? instruction)
    {
        if (instruction==null) return null;
        switch (instruction.OpCode.Code)
        {
            case Code.Ldc_I4_M1:return -1;
            case Code.Ldc_I4_0:return 0;
            case Code.Ldc_I4_1:return 1;
            case Code.Ldc_I4_2:return 2;
            case Code.Ldc_I4_3:return 3;
            case Code.Ldc_I4_4:return 4;
            case Code.Ldc_I4_5:return 5;
            case Code.Ldc_I4_6:return 6;
            case Code.Ldc_I4_7:return 7;
            case Code.Ldc_I4_8:return 8;
            case Code.Ldc_I4:case Code.Ldc_I4_S:return Convert.ToInt32(instruction.Operand);
            default:return null;
        }
    }
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    { foreach (var type in types) { yield return type;foreach (var nested in Types(type.NestedTypes)) yield return nested; } }
}
