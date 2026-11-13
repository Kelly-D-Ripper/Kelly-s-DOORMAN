using System;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class DisabledContentRegression
{
    internal static void Run(Action<bool,string> check)
    {
        var real=Parse("aryx_weaponry","{\"schemaVersion\":3,\"modName\":\"Aryx Weapon Pack\",\"modVersion\":\"1.1.1\",\"gameVersion\":\"0.34.2\",\"Ops\":[{\"opId\":\"OpAddToEncyclopedia\",\"Payload\":{\"anything\":true}}]}");
        check(real.Id=="aryx_weaponry"&&real.Name=="Aryx Weapon Pack"&&real.Version=="1.1.1"&&real.GameVersion=="0.34.2"&&real.Schema==3,"disabled bundle identity uses exact bundle name and scalar patch_manifest metadata without operation materialization");
        var defaults=Parse("test_pack","{\"modName\":\"Test pack\",\"modVersion\":\"1.2.0-rc.1\"}");
        check(defaults.Schema==3&&defaults.GameVersion==DisabledContentMetadata.DefaultGameVersion&&defaults.Version=="1.2.0-rc.1","missing schema and game target mirror the verified Blueprinter defaults without normalizing version text");
        foreach(string game in new[]{"\"\"","null"})
            check(Parse("test_pack","{\"modName\":\"Test\",\"modVersion\":\"1\",\"gameVersion\":"+game+"}").GameVersion=="0.34.2","empty and null game targets use Blueprinter's default");
        check(Parse("aryx_fs41","{\"schemaVersion\":2,\"modName\":\"FS-41 Eclipse\",\"modVersion\":\"1.0.0\",\"gameVersion\":\"0.34.1\"}").GameVersion=="0.34.1","explicit supported metadata is retained rather than inferred from NOMM registry or filenames");
        check(Parse("test_pack","{\"modName\":\"Pack \\\"quoted\\\" {name}\",\"modVersion\":\"1\",\"ignored\":{\"text\":\"[brace] } \\\"\"}}").Name=="Pack \"quoted\" {name}","manifest boundaries ignore braces and escaped quotes inside metadata strings");

        foreach(string id in new[]{"", " ", "../pack", "pack\\asset", "resource:pack", "pack|other", "pack#hash", "pack_--second", " pack", "pack\n",new string('i',257)})
            Reject(check,()=>Parse(id,"{\"modName\":\"Test\",\"modVersion\":\"1\"}"),"disabled discovery rejects unsafe, ambiguous or oversized content IDs");
        foreach(string json in new[]{"", "null", "[]", "true", "{", "{}", "{\"modName\":\"Test\"}", "{\"modName\":\"Test\",\"modVersion\":\"\"}", "{\"modName\":null,\"modVersion\":\"1\"}", "{\"modName\":\"Test\",\"modVersion\":1}", "{\"modName\":\"Test\",\"modVersion\":\"1\",\"schemaVersion\":4}", "{\"modName\":\"Test\",\"modVersion\":\"1\",\"schemaVersion\":-1}", "{\"modName\":\"Test\",\"modVersion\":\"1\",\"schemaVersion\":\"3\"}", "{\"modName\":\"Test\",\"modVersion\":\"1\",\"schemaVersion\":2.5}", "{\"modName\":\"Test\",\"modVersion\":\"1\",\"schemaVersion\":null}", "{\"modName\":\"Test\",\"modName\":\"Other\",\"modVersion\":\"1\"}", "{\"modName\":\"Test\",\"modVersion\":\"1\",\"modVersion\":\"2\"}", "{\"modName\":\"Test\",\"modVersion\":\"1\"} {}", "{\"modName\":\"Test\\n\",\"modVersion\":\"1\"}", "{\"modName\":\"Test\",\"modVersion\":\"1_--other\"}"})
            Reject(check,()=>Parse("test_pack",json),"unreadable, contradictory or unsupported disabled manifest metadata fails closed: "+json);
        foreach((string Field,int Limit) text in new[]{("modName",256),("modVersion",128),("gameVersion",128)})
        {
            string name=text.Field=="modName"?new string('x',text.Limit+1):"Test",version=text.Field=="modVersion"?new string('x',text.Limit+1):"1",game=text.Field=="gameVersion"?new string('x',text.Limit+1):"0.34.2";
            Reject(check,()=>Parse("test_pack","{\"modName\":\""+name+"\",\"modVersion\":\""+version+"\",\"gameVersion\":\""+game+"\"}"),"disabled manifest names and versions are bounded");
        }

        Reject(check,()=>DisabledContentMetadata.Parse("test_pack",null!),"null disabled metadata cannot create an inventory entry");
        Reject(check,()=>DisabledContentMetadata.Parse("test_pack",new byte[]{0xC0,0xAF}),"malformed UTF8 is rejected before metadata identity comparison");
        Reject(check,()=>DisabledContentMetadata.Parse("test_pack",new byte[DisabledContentMetadata.MaximumJsonBytes+1]),"oversized disabled metadata is rejected before JSON parsing");
        byte[] withBom=Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"modName\":\"Test\",\"modVersion\":\"1\",\"Ops\":[\""+new string('x',265525)+"\"]}")).ToArray();
        check(DisabledContentMetadata.Parse("test_pack",withBom).Version=="1","large UTF-8 BOM manifests from real content bundles retain their identity");
        string realAssets=Environment.GetEnvironmentVariable("DOORMAN_TEST_REAL_ASSETS")??"";
        if(realAssets.Length>0)
        {
            var manifest=File.ReadAllBytes(Path.Combine(realAssets,"fs3-patch-manifest.json"));
            var fs3=DisabledContentMetadata.Parse("trisurface",manifest);
            check(manifest.Length>1024*1024&&fs3.Name=="FS-3 Ternion"&&fs3.Version=="1.0.1","real FS-3 manifest is accepted beyond the former one-megabyte limit");
        }
        string centurion=Environment.GetEnvironmentVariable("DOORMAN_REAL_CENTURION_MANIFEST")??"";
        if(centurion.Length>0)
        {
            var bytes=File.ReadAllBytes(centurion);var info=DisabledContentMetadata.Parse("centurion",bytes);
            check(bytes.Length>8*1024*1024&&info.Id=="centurion"&&info.Version=="0.1.3","real 11 MB Centurion manifest parses without materialising its operations");
        }
        string realPackages=Environment.GetEnvironmentVariable("DOORMAN_REAL_PACKAGE_MANIFESTS")??"";
        if(realPackages.Length>0)
        {
            foreach(var sample in new[]{("centurion","centurion","0.1.3"),("f117","blacknight2u.f117a.nighthawk.nobp","0.5.0"),("x77","x77warewind.nobp","0.0.0"),("mig29","mig29","0.9.3.1"),("mig29smt","mig29smt","0.2.1.1")})
            {
                var info=DisabledContentMetadata.Parse(sample.Item2,File.ReadAllBytes(Path.Combine(realPackages,sample.Item1+".json")));
                check(info.Id==sample.Item2&&info.Version==sample.Item3,"the real package manifest retains its Blueprinter ID and version: "+sample.Item1);
            }
        }
        string baseJson="{\"modName\":\"Test\",\"modVersion\":\"1\"}";
        byte[] exact=Encoding.UTF8.GetBytes(baseJson+new string(' ',DisabledContentMetadata.MaximumJsonBytes-baseJson.Length));
        check(DisabledContentMetadata.Parse("test_pack",exact).Version=="1","bounded JSON at the exact maximum can still carry valid content metadata");
        string depth="{\"modName\":\"Test\",\"modVersion\":\"1\",\"Ops\":"+string.Concat(Enumerable.Repeat("[",40))+"0"+string.Concat(Enumerable.Repeat("]",40))+"}";
        Reject(check,()=>Parse("test_pack",depth),"untrusted operation payloads remain subject to parser depth limits");
    }

    internal static void Contracts(AssemblyDefinition client,Action<bool,string> check)
    {
        var reader=client.MainModule.GetType("KellysJOINCHECK.DisabledContentManifestReader");
        var read=reader.Methods.Single(m=>m.Name=="Read");
        var code=read.Body.Instructions;
        var calls=code.Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand).ToArray();
        check(calls.Count(m=>m.DeclaringType.FullName=="UnityEngine.AssetBundle"&&m.Name=="LoadFromStream")==1&&code.TakeWhile(i=>!(i.Operand is MethodReference m&&m.Name=="LoadFromStream")).Any(i=>i.Operand is MethodReference m&&m.Name=="ValidateHeader"),"disabled manifest inspection validates bounded source headers before its single native stream load");
        var assets=calls.Where(m=>m.DeclaringType.FullName=="UnityEngine.AssetBundle"&&m.Name.StartsWith("LoadAsset",StringComparison.Ordinal)).ToArray();
        check(assets.Length==1&&assets[0] is GenericInstanceMethod asset&&asset.GenericArguments.Count==1&&asset.GenericArguments[0].FullName=="UnityEngine.TextAsset"&&code.Any(i=>i.OpCode==OpCodes.Ldstr&&Equals(i.Operand,"patch_manifest")),"disabled bundle inspection reads only the patch_manifest TextAsset and never instantiates content prefabs");
        check(read.Body.ExceptionHandlers.Where(h=>h.HandlerType==ExceptionHandlerType.Finally).Any(h=>code.SkipWhile(i=>i!=h.HandlerStart).TakeWhile(i=>i!=h.HandlerEnd).Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="UnityEngine.AssetBundle"&&m.Name=="Unload"&&i.Previous?.OpCode==OpCodes.Ldc_I4_1)),"temporary disabled bundles and inspected metadata always unload with true in a finally handler");
        check(!reader.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m&&(m.DeclaringType.Namespace=="Blueprinter"||m.DeclaringType.FullName=="System.Reflection.Assembly"&&m.Name.StartsWith("Load",StringComparison.Ordinal)||m.Name=="LoadAllAssets"||m.DeclaringType.FullName=="System.Activator")),"disabled bundle inspection never loads plugin assemblies, loader operations or prefab collections");
    }

    private static DisabledBundleInfo Parse(string id,string json)=>DisabledContentMetadata.Parse(id,Encoding.UTF8.GetBytes(json));
    private static void Reject(Action<bool,string> check,Action action,string description)
    {
        bool rejected=false;
        try { action(); }
        catch(InvalidDataException) { rejected=true; }
        check(rejected,description);
    }
}
