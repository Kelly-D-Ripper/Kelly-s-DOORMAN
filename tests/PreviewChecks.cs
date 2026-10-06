using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class PreviewRegression
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client)
    {
        string temp=Path.Combine(Path.GetTempPath(),"doorman-preview-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string basic=Fixture(temp,"basic",new[]{"test.single"},"{\"description\":\"Included inside the DLL.\",\"links\":[{\"label\":\"GitHub\",\"url\":\"https://github.com/Aryx3D/AryxWeaponryPack\"}]}",new[]{Image("doorman.png",Png(1800,1200))});
            var card=Read(basic)!;
            check(card.Plugin=="test.single"&&card.Name=="Plugin 0"&&card.Description=="Included inside the DLL."&&card.Links.Length==1,"single-plugin embedded metadata infers its exact identity and name");
            check(card.Image=="doorman.png"&&card.ReadImageBytes()!.SequenceEqual(Png(1800,1200)),"default embedded artwork is reopened lazily from its DLL");
            var cards=new Dictionary<string,ModManifest>(StringComparer.Ordinal);EmbeddedModPreview.Add(cards,card,_=>{});
            check(ReferenceEquals(ModManifest.Match(cards,true,"legacy.content.id","test.single"),card),"disabled wrapper content inherits an embedded preview without a loaded plugin");
            check(ModManifest.Match(cards,true,"legacy.content.id","other.plugin")==null,"embedded cards cannot inherit through an unrelated wrapper");
            check(!typeof(ModManifest).GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic).Any(f=>f.FieldType==typeof(byte[])),"catalog cards retain resource locations rather than every image byte array");
            string serial=Path.Combine(temp,"serialized.json");using(var stream=File.Create(serial))new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ModManifest)).WriteObject(stream,card);
            string json=File.ReadAllText(serial);
            check(!json.Contains("EmbeddedAssembly")&&!json.Contains("EmbeddedImage")&&!json.Contains("EmbeddedModule")&&!json.Contains(basic),"embedded file locations are not serialized into developer manifests");

            string explicitCard=Fixture(temp,"explicit",new[]{"test.first","test.second"},"{\"plugin\":\"test.second\",\"name\":\"Second mod\",\"image\":\"SecondMod.jpg\",\"update\":{\"repository\":\"Aryx3D/AryxWeaponryPack\"}}",new[]{Image("SecondMod.jpg",Jpeg())});
            var second=Read(explicitCard)!;
            check(second.Plugin=="test.second"&&second.Name=="Second mod"&&second.Update?.Repository=="Aryx3D/AryxWeaponryPack"&&second.ReadImageBytes()!.SequenceEqual(Jpeg()),"multi-plugin DLL previews require and preserve a proven explicit identity");
            Reject(check,()=>Read(Fixture(temp,"ambiguous",new[]{"test.first","test.second"},"{\"name\":\"Guess me\"}")),"multi-plugin manifests do not infer a plugin from names or resource order");
            Reject(check,()=>Read(Fixture(temp,"multi-name",new[]{"test.first","test.second"},"{\"plugin\":\"test.second\"}")),"multi-plugin manifests do not infer a missing display name");
            Reject(check,()=>Read(Fixture(temp,"foreign",new[]{"test.single"},"{\"plugin\":\"other.plugin\",\"name\":\"Wrong\"}")),"embedded metadata cannot claim another DLL's plugin identity");
            Reject(check,()=>Read(Fixture(temp,"foreign-bundle",new[]{"test.single"},"{\"bundle\":\"another.pack\"}")),"embedded previews cannot directly claim another content bundle");
            Reject(check,()=>Read(Fixture(temp,"duplicate-plugin",new[]{"test.same","test.same"},"{}")),"duplicate DLL plugin identities fail closed");
            Reject(check,()=>Read(Fixture(temp,"no-plugin",Array.Empty<string>(),"{\"plugin\":\"test.single\",\"name\":\"Missing\"}")),"a resource without a proven BepInPlugin identity is rejected");
            Reject(check,()=>Read(Fixture(temp,"malformed","{not json")),"malformed embedded JSON is rejected");
            Reject(check,()=>Read(Fixture(temp,"oversized-json",new string('x',65537))),"embedded JSON obeys the sidecar metadata size limit");
            check(Read(Fixture(temp,"no-preview",new[]{"test.single"},null))==null,"ordinary plugin DLLs without a preview continue to work");
            check(Read(Fixture(temp,"wrong-case",new[]{"test.single"},null,new[]{Image("Doorman.json",Encoding.UTF8.GetBytes("{}"))}))==null,"only exact doorman.json resource names opt into the contract");
            Reject(check,()=>Read(Fixture(temp,"duplicate-manifest",new[]{"test.single"},"{}",new[]{Image("doorman.json",Encoding.UTF8.GetBytes("{}"))})),"duplicate manifest resources fail closed");
            Reject(check,()=>Read(Fixture(temp,"external-manifest",new[]{"test.single"},null,new Resource[]{new LinkedResource("doorman.json",ManifestResourceAttributes.Private,"outside.json")})),"linked manifest resources never read external files");
            Reject(check,()=>Read(Fixture(temp,"external-image",new[]{"test.single"},"{}",new Resource[]{new LinkedResource("doorman.png",ManifestResourceAttributes.Private,"outside.png")})),"linked image resources never read external files");
            foreach(string image in new[]{"../outside.png","C:\\outside.png","https://host/card.png","card.dll","bad\nname.png"})
            {
                string encoded=JsonString(image);
                Reject(check,()=>Read(Fixture(temp,"bad-image-"+Guid.NewGuid().ToString("N"),new[]{"test.single"},"{\"image\":"+encoded+"}")),"unsafe embedded image name is rejected: "+image.Replace('\n',' '));
            }
            Reject(check,()=>Read(Fixture(temp,"missing-image",new[]{"test.single"},"{\"image\":\"missing.png\"}")),"explicit image resources must exist exactly");
            Reject(check,()=>Read(Fixture(temp,"duplicate-image",new[]{"test.single"},"{}",new[]{Image("doorman.png",Png(1,1)),Image("doorman.png",Png(1,1))})),"duplicate image resource names fail closed");
            var noArt=Read(Fixture(temp,"no-art","{}"))!;
            check(noArt.Image==""&&noArt.ReadImageBytes()==null,"metadata-only manifests remain useful without artwork");

            var invalidArt=Read(Fixture(temp,"invalid-art",new[]{"test.single"},"{}",new[]{Image("doorman.png",new byte[24])}))!;
            check(invalidArt.Name=="Plugin 0","catalog discovery does not decode embedded images");
            Reject(check,()=>invalidArt.ReadImageBytes(),"invalid image bytes are rejected only when artwork is selected");
            var bigArt=Read(Fixture(temp,"big-art",new[]{"test.single"},"{}",new[]{Image("doorman.png",new byte[ModManifest.MaximumImageBytes+1])}))!;
            Reject(check,()=>bigArt.ReadImageBytes(),"embedded images larger than twelve MiB are rejected before texture allocation");
            var manyPixels=Read(Fixture(temp,"pixels",new[]{"test.single"},"{}",new[]{Image("doorman.png",Png(8192,8192))}))!;
            Reject(check,()=>manyPixels.ReadImageBytes(),"embedded image dimensions obey the sixteen megapixel limit");
            var zeroPixels=Read(Fixture(temp,"zero-pixels",new[]{"test.single"},"{}",new[]{Image("doorman.png",Png(0,1))}))!;
            Reject(check,()=>zeroPixels.ReadImageBytes(),"zero-sized embedded images are rejected");

            string changed=Fixture(temp,"changed",new[]{"test.single"},"{}",new[]{Image("doorman.png",Png(1,1))});var old=Read(changed)!;
            Fixture(temp,"changed",new[]{"test.single"},"{}",new[]{Image("doorman.png",Png(2,2))});
            Reject(check,()=>old.ReadImageBytes(),"a changed DLL cannot silently reuse the previous catalog resource binding");
            File.Delete(changed);check(old.ReadImageBytes()==null,"a removed DLL does not leave an image file handle or stale artwork");

            string imagePath=Path.Combine(temp,"local.png");File.WriteAllBytes(imagePath,Png(1800,1200));
            var sidecar=new ModManifest{Plugin="test.single",Name="Local",Directory=temp,Image="local.png"};sidecar.Validate();
            check(sidecar.ReadImageBytes()!.SequenceEqual(Png(1800,1200)),"existing sidecar artwork uses the shared lazy image reader");
            File.WriteAllBytes(imagePath,new byte[ModManifest.MaximumImageBytes+1]);Reject(check,()=>sidecar.ReadImageBytes(),"sidecar artwork obeys the same byte limit");
            File.WriteAllBytes(imagePath,Png(8192,8192));Reject(check,()=>sidecar.ReadImageBytes(),"sidecar artwork obeys the same pixel limit");
            sidecar.Image="../outside.png";check(sidecar.ReadImageBytes()==null,"sidecar image traversal is never opened");
            CheckLayers(check,temp,card);
        }
        finally {Directory.Delete(temp,true);}
        CheckIntegration(check,client);
    }

    private static void CheckLayers(Action<bool,string> check,string temp,ModManifest embeddedCard)
    {
        var embedded=new Dictionary<string,ModManifest>(StringComparer.Ordinal);var managed=new Dictionary<string,ModManifest>(StringComparer.Ordinal);var manual=new Dictionary<string,ModManifest>(StringComparer.Ordinal);
        var downloaded=new ModManifest{Plugin=embeddedCard.Plugin,Name="Downloaded"};var custom=new ModManifest{Plugin=embeddedCard.Plugin,Name="Manual"};
        EmbeddedModPreview.Add(embedded,embeddedCard,_=>{});EmbeddedModPreview.Add(managed,downloaded,_=>{});
        check(ReferenceEquals(ModManifest.Match(EmbeddedModPreview.Merge(embedded,managed,manual),false,embeddedCard.Plugin,""),downloaded),"managed update metadata overrides a DLL's embedded default");
        EmbeddedModPreview.Add(manual,custom,_=>{});
        check(ReferenceEquals(ModManifest.Match(EmbeddedModPreview.Merge(embedded,managed,manual),true,"legacy.bundle",embeddedCard.Plugin),custom),"manual sidecars override managed and embedded previews including disabled content");
        EmbeddedModPreview.Add(manual,custom,_=>{});var merged=EmbeddedModPreview.Merge(embedded,managed,manual);
        check(ModManifest.Match(merged,false,embeddedCard.Plugin,"")==null&&ModManifest.Ambiguous(merged,false,embeddedCard.Plugin,""),"manual duplicate identities block lower-priority metadata");
        check(ModManifest.Ambiguous(merged,true,"legacy.bundle",embeddedCard.Plugin),"duplicate wrapper metadata also blocks NOMNOM fallback for disabled content");
        manual.Clear();EmbeddedModPreview.Add(managed,downloaded,_=>{});merged=EmbeddedModPreview.Merge(embedded,managed,manual);
        check(ModManifest.Ambiguous(merged,false,embeddedCard.Plugin,""),"managed duplicate identities block embedded fallback");
        EmbeddedModPreview.Add(manual,custom,_=>{});merged=EmbeddedModPreview.Merge(embedded,managed,manual);
        check(ReferenceEquals(ModManifest.Match(merged,false,embeddedCard.Plugin,""),custom)&&!ModManifest.Ambiguous(merged,false,embeddedCard.Plugin,""),"a unique manual card can explicitly override ambiguity in a lower layer");
        check(!ModManifest.Ambiguous(merged,true,"unknown","unrelated")&&!ModManifest.Ambiguous(merged,false,"unknown",""),"missing metadata remains eligible for NOMNOM discovery");
        var exact=new ModManifest{Bundle="legacy.bundle",Name="Exact content"};EmbeddedModPreview.Add(manual,exact,_=>{});merged=EmbeddedModPreview.Merge(embedded,managed,manual);
        check(ReferenceEquals(ModManifest.Match(merged,true,"legacy.bundle",embeddedCard.Plugin),exact),"exact manual content cards preserve priority over inherited plugin previews");
        string proper=Path.Combine(temp,ModUpdateFiles.MetadataDirectory(embeddedCard.Plugin),"preview.doorman.json");
        check(EmbeddedModPreview.IsManagedPath(temp,proper,embeddedCard),"downloaded metadata is bound to the stable exact plugin hash directory");
        check(!EmbeddedModPreview.IsManagedPath(temp,Path.Combine(temp,"DOORMAN-Metadata","other","preview.doorman.json"),embeddedCard),"copied metadata cannot claim another managed directory");
        check(!EmbeddedModPreview.IsManagedPath(temp,Path.Combine(Path.GetDirectoryName(proper)!,"other.doorman.json"),embeddedCard),"arbitrary filenames inside managed metadata are not trusted");
        check(!EmbeddedModPreview.IsManagedPath(temp,proper,new ModManifest{Bundle="pack",Name="No plugin"}),"managed metadata always supplies an explicit stable plugin identity");
        check(!EmbeddedModPreview.IsManagedPath(temp,proper,new ModManifest{Plugin=embeddedCard.Plugin.ToUpperInvariant(),Name="Different ID"}),"plugin identity casing changes the managed metadata binding");
        check(ModManifest.PluginUpdateVersion("1.0",new[]{"1.2.3"})=="1.2.3","plugin-only wrapper previews compare releases against their actual content version");
        check(ModManifest.PluginUpdateVersion("2.4.1",Array.Empty<string>())=="2.4.1","ordinary code plugins retain their own version without associated content");
        check(ModManifest.PluginUpdateVersion("1.0",new[]{"1.2.3","4.5.6"})=="","a wrapper owning multiple packs never guesses one content version");
        check(ModManifest.PluginUpdateVersion("1.0",new[]{""})=="","an unavailable disabled content version does not fall back to a constant wrapper version");
    }

    private static void CheckIntegration(Action<bool,string> check,AssemblyDefinition client)
    {
        var catalog=client.MainModule.GetType("KellysJOINCHECK.ModCatalog");var read=catalog.Methods.Single(m=>m.Name=="Read").Body.Instructions;
        check(read.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.EmbeddedModPreview"&&m.Name=="Read"),"catalog discovers embedded metadata during its existing Cecil DLL scan");
        check(read.Any(i=>i.Operand is MethodReference m&&m.Name=="IsManagedPath")&&read.Any(i=>i.Operand is MethodReference m&&m.Name=="Merge"),"catalog validates downloaded metadata ownership before combining preview layers");
        check(catalog.Methods.Single(m=>m.Name=="SetUpdateSources").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="PluginUpdateVersion"),"catalog applies content version comparison to plugin-only preview manifests");
        var filters=Types(new[]{catalog}).SelectMany(t=>t.Methods).Where(m=>m.Name.StartsWith("<Enrich>")&&m.HasBody).SelectMany(m=>m.Body.Instructions);
        check(filters.Any(i=>i.Operand is FieldReference f&&f.Name=="PreviewAmbiguous"),"NOMNOM cannot replace a rejected duplicate preview identity");
        var select=client.MainModule.GetType("KellysJOINCHECK.NativeModsUi").Methods.Single(m=>m.Name=="Select").Body.Instructions.ToList();
        int imageRead=select.FindIndex(i=>i.Operand is MethodReference m&&m.Name=="ReadImageBytes");int texture=select.FindIndex(i=>i.Operand is MethodReference m&&m.Name==".ctor"&&m.DeclaringType.FullName=="UnityEngine.Texture2D");
        check(imageRead>=0&&texture>imageRead&&!select.Any(i=>i.Operand is MethodReference m&&m.Name=="ReadAllBytes"),"Mods selection validates image bytes before creating a texture");
        var helper=client.MainModule.GetType("KellysJOINCHECK.EmbeddedModPreview");
        check(!Types(new[]{helper}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m&&(m.DeclaringType.FullName=="System.Reflection.Assembly"||m.DeclaringType.FullName=="System.AppDomain")&&(m.Name.StartsWith("Load")||m.Name=="CreateInstance")),"preview discovery never executes plugin DLLs through reflection loading");
    }

    private static string Fixture(string directory,string name,string json)=>Fixture(directory,name,new[]{"test.single"},json);
    private static string Fixture(string directory,string name,string[] plugins,string? json,IEnumerable<Resource>? resources=null)
    {
        string path=Path.Combine(directory,name+".dll");
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name,new Version(1,0)),name,ModuleKind.Dll))
        {
            assembly.MainModule.Mvid=Guid.NewGuid();var module=assembly.MainModule;
            for(int p=0;p<plugins.Length;p++)
            {
                var type=new TypeDefinition("Fixture","Plugin"+p,Mono.Cecil.TypeAttributes.Public|Mono.Cecil.TypeAttributes.Class,module.TypeSystem.Object);module.Types.Add(type);
                var ctor=new MethodReference(".ctor",module.TypeSystem.Void,new TypeReference("BepInEx","BepInPlugin",module,new AssemblyNameReference("BepInEx",new Version(5,4)))){HasThis=true};
                for(int a=0;a<3;a++)ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                var attribute=new CustomAttribute(ctor);attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,plugins[p]));attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,"Plugin "+p));attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,"1.0"));type.CustomAttributes.Add(attribute);
            }
            if(json!=null)module.Resources.Add(Image("doorman.json",Encoding.UTF8.GetBytes(json)));
            var linkedFiles=new List<string>();
            try
            {
                if(resources!=null)foreach(var resource in resources)
                {
                    if(resource is LinkedResource linked)
                    {
                        linked.File=Path.Combine(directory,"linked-"+Guid.NewGuid().ToString("N")+".bin");
                        File.WriteAllText(linked.File,"This external file must not be followed.");linkedFiles.Add(linked.File);
                    }
                    module.Resources.Add(resource);
                }
                assembly.Write(path);
            }
            finally{foreach(string external in linkedFiles)File.Delete(external);}
        }
        return path;
    }
    private static ModManifest? Read(string path){using(var assembly=AssemblyDefinition.ReadAssembly(path))return EmbeddedModPreview.Read(assembly,path);}
    private static EmbeddedResource Image(string name,byte[] bytes)=>new EmbeddedResource(name,ManifestResourceAttributes.Private,bytes);
    private static byte[] Png(uint width,uint height){var bytes=new byte[24];bytes[0]=137;bytes[1]=80;bytes[2]=78;bytes[3]=71;for(int b=0;b<4;b++){bytes[16+b]=(byte)(width>>(24-8*b));bytes[20+b]=(byte)(height>>(24-8*b));}return bytes;}
    private static byte[] Jpeg()=>new byte[]{255,216,255,192,0,8,8,0,100,0,100,0,255,217,0,0,0,0,0,0,0,0,0,0};
    private static string JsonString(string value)=>System.Text.Json.JsonSerializer.Serialize(value);
    private static void Reject(Action<bool,string> check,Action action,string reason){try{action();check(false,reason);}catch(Exception ex)when(ex is InvalidDataException||ex is System.Runtime.Serialization.SerializationException){check(true,reason);}}
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots){foreach(var type in roots){yield return type;foreach(var nested in Types(type.NestedTypes))yield return nested;}}
}
