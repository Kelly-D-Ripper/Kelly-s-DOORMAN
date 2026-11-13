using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class NommDisabledCatalogRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string temporary=Path.Combine(Path.GetTempPath(),"doorman-nomm-catalog-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string bep=Root(temporary,"real-identity"),unit=Unit(bep,"Registry_WrongName");
            File.WriteAllText(Path.Combine(unit,"meta.json"),"{\"id\":\"EntirelyDifferentRegistryId\",\"artifact\":{\"version\":\"99.0\"}}");
            string wrapper=Dll(unit,"DifferentFilename.dll","ActualAssembly",new[]{"actual.wrapper"},new[]{"UnrelatedResourceName.nobp"},"{\"plugin\":\"actual.wrapper\",\"name\":\"Embedded preview\"}",true);
            string helper=Dll(unit,"Helper.dll","HelperAssembly",new[]{"actual.helper"},Array.Empty<string>(),null);
            File.WriteAllText(Path.Combine(unit,"native-support.asset"),"required whole-unit bytes");
            File.WriteAllText(Path.Combine(unit,"preview.doorman.json"),"{\"plugin\":\"actual.wrapper\",\"name\":\"Manual preview\",\"requisites\":[{\"plugin\":\"actual.helper\",\"minimumVersion\":\"1.0\"}]}");
            int reads=0;
            var inspected=Read(bep,"Registry_WrongName",stream=>{reads++;return Bundle(stream);});
            check(inspected.Error==""&&inspected.RegistryId=="EntirelyDifferentRegistryId"&&inspected.ParentId==""&&inspected.Entries.Count==3,"NOMM catalog preserves a whole multi-DLL unit without treating registry metadata as content identity");
            var pack=inspected.Entries.Single(e=>e.Content);var plugin=inspected.Entries.Single(e=>e.Id=="actual.wrapper");
            check(pack.Id=="actual_content_id"&&pack.Version=="2.7.3"&&plugin.Version=="1.0","NOMM bundle identity and version come from the actual reader, independently of wrapper/version/registry/resource/file names");
            check(pack.Path=="resource:ActualAssembly:UnrelatedResourceName.nobp"&&pack.WrapperPath==wrapper&&pack.WrapperPlugin=="actual.wrapper"&&pack.AssemblyName=="ActualAssembly","embedded disabled content binds to an exact assembly/resource and physical wrapper DLL");
            check(inspected.Entries.All(e=>!e.Enabled&&!e.Wanted&&e.ExternalDisabled&&e.RestoreSource=="Registry_WrongName"&&e.RestoreTarget=="Registry_WrongName"),"discovering NOMM mods leaves every plugin and content row disabled and unselected");
            check(inspected.Entries.Single(e=>e.Id=="actual.helper").Path==helper&&reads==1,"all bundled plugin DLLs are inspected and only embedded content reaches the injected bundle reader");
            check(plugin.Requires.Contains("actual.helper")&&plugin.Requires.Contains("outside.versioned")&&!plugin.Requires.Contains("optional.soft")&&plugin.RequiredRequisites.Single(r=>r.Plugin=="outside.versioned").MinimumVersion=="2.1","Cecil discovery retains hard dependencies and version minimums while ignoring optional soft dependencies");
            check(plugin.Manifest?.Name=="Manual preview"&&pack.Manifest?.Name=="Manual preview"&&pack.Manifest.Requisites.Single().Plugin=="actual.helper","disabled manual sidecars override embedded previews and retain their actual prerequisites");
            check(inspected.Receipt?.Digest.Length==64&&Directory.Exists(unit)&&!Directory.Exists(Path.Combine(bep,"plugins","Registry_WrongName")),"catalog binds inspected metadata to an exact content receipt while leaving disabled files in place");
            var receipt=NommDisabledCatalog.Prepare(bep,inspected);
            check(receipt.PluginIds.SequenceEqual(new[]{"actual.helper","actual.wrapper"})&&receipt.Digest.Length==64&&ReferenceEquals(receipt,NommDisabledCatalog.Prepare(bep,inspected)),"selection verifies the original content-bound receipt containing all actual plugin identities");
            File.AppendAllText(Path.Combine(unit,"native-support.asset"),"tampered");
            Reject(check,()=>NommDisabledCatalog.Prepare(bep,inspected),"a cached restore receipt rejects changed support assets instead of trusting only DLL metadata");

            bep=Root(temporary,"preserved-observation-before-selection");unit=Unit(bep,"IdentityChanged");string exactPack=Path.Combine(unit,"pack.nobp");
            File.WriteAllBytes(exactPack,BundleBytes("original_id","Exact pack","1.2.3"));inspected=Read(bep,"IdentityChanged",Bundle);
            long originalLength=new FileInfo(exactPack).Length;DateTime originalWrite=File.GetLastWriteTimeUtc(exactPack);
            File.WriteAllBytes(exactPack,BundleBytes("tampered_id","Exact pack","9.8.7"));File.SetLastWriteTimeUtc(exactPack,originalWrite);
            check(new FileInfo(exactPack).Length==originalLength&&NommDisabledCatalog.Observe(bep,inspected.Candidate)==inspected.ObservationKey&&inspected.Entries.Single().Id=="original_id"&&inspected.Entries.Single().Version=="1.2.3","same-size/timestamp pack tampering can retain cache observations while its discovered identity remains the original one");
            Reject(check,()=>NommDisabledCatalog.Prepare(bep,inspected),"first selection rejects changed bytes under a preserved observation instead of binding stale identities to the replacement");

            bep=Root(temporary,"preserved-observation-after-selection");unit=Unit(bep,"CachedChanged");Dll(unit,"Wrapper.dll","Wrapper",new[]{"wrapper"},new[]{"pack.nobp"},null);
            string cachedAsset=Path.Combine(unit,"asset.bin");File.WriteAllText(cachedAsset,"before");inspected=Read(bep,"CachedChanged",Bundle);NommDisabledCatalog.Prepare(bep,inspected);
            DateTime cachedWrite=File.GetLastWriteTimeUtc(cachedAsset);File.WriteAllText(cachedAsset,"after!");File.SetLastWriteTimeUtc(cachedAsset,cachedWrite);
            check(NommDisabledCatalog.Observe(bep,inspected.Candidate)==inspected.ObservationKey,"same-size/timestamp support-asset tampering also preserves the catalog cache hint");
            Reject(check,()=>NommDisabledCatalog.Prepare(bep,inspected),"cached selection always hashes and verifies the original receipt rather than trusting matching observations");

            bep=Root(temporary,"preserved-observation-during-read");unit=Unit(bep,"ChangedDuringRead");Dll(unit,"Wrapper.dll","Wrapper",new[]{"wrapper"},new[]{"pack.nobp"},null);
            string readAsset=Path.Combine(unit,"asset.bin");File.WriteAllText(readAsset,"before");DateTime readWrite=File.GetLastWriteTimeUtc(readAsset);
            inspected=Read(bep,"ChangedDuringRead",stream=>{var info=Bundle(stream);File.WriteAllText(readAsset,"after!");File.SetLastWriteTimeUtc(readAsset,readWrite);return info;});
            check(inspected.Error.Contains("changed")&&inspected.Entries.Count==0&&inspected.Receipt==null,"exact receipt verification rejects same-size/timestamp mutations inside the bundle reader and exposes no unbound identity rows");

            bep=Root(temporary,"standalone");unit=Unit(bep,"RegistryStandalone");
            string standalone=Path.Combine(unit,"not_the_pack_id.nobp");File.WriteAllBytes(standalone,BundleBytes("standalone_exact","Standalone pack","4.1.0"));
            inspected=Read(bep,"RegistryStandalone",Bundle);pack=inspected.Entries.Single();
            check(inspected.Error==""&&inspected.RegistryId=="RegistryStandalone"&&pack.Id=="standalone_exact"&&pack.Path==standalone&&pack.WrapperPath==""&&pack.WrapperPlugin=="","standalone NOMM content works without registry metadata or a wrapper and keeps its actual disabled source path");
            string raw=Path.Combine(bep,"disabledPlugins","DirectFile.nobp");File.WriteAllBytes(raw,BundleBytes("direct_exact","Direct pack","1"));
            check(Read(bep,"DirectFile.nobp",Bundle).Entries.Single().Id=="direct_exact","NOMM directly disabled files are supported as complete restore units");
            string code=Dll(Path.Combine(bep,"disabledPlugins"),"CodeOnly.dll","CodeOnly",new[]{"code.only"},Array.Empty<string>(),null);
            inspected=Read(bep,"CodeOnly.dll",_=>throw new Exception("Code-only metadata must not read a bundle"));
            check(inspected.Error==""&&inspected.Entries.Single().Id=="code.only"&&inspected.Entries.Single().Path==code,"ordinary disabled code-only DLLs need no content reader or meta.json");

            bep=Root(temporary,"native-support");unit=Unit(bep,"WrapperWithNative");Dll(unit,"Wrapper.dll","Wrapper",new[]{"wrapper.plugin"},new[]{"pack.nobp"},null);
            string native=Path.Combine(unit,"NativeSupport.dll");File.WriteAllBytes(native,NativeDll());var nativeMessages=new List<string>();
            inspected=Read(bep,"WrapperWithNative",Bundle,nativeMessages.Add);
            check(inspected.Error==""&&inspected.Entries.Count==2&&nativeMessages.Any(m=>m.Contains("NativeSupport.dll")),"a native support DLL does not block metadata discovery of its valid wrapper and content pack");
            var nativeReceipt=NommDisabledCatalog.Prepare(bep,inspected);DisabledModFiles.Apply(bep,new[]{nativeReceipt});
            check(File.ReadAllBytes(Path.Combine(bep,"plugins","WrapperWithNative","NativeSupport.dll")).SequenceEqual(NativeDll()),"the native library stays in the exact whole-unit receipt and move without ever being executed");
            unit=Unit(bep,"NativeOnly");File.WriteAllBytes(Path.Combine(unit,"Only.dll"),NativeDll());inspected=Read(bep,"NativeOnly",Bundle);
            check(inspected.Error.Length>0&&inspected.Entries.Count==0,"native or damaged DLL-only units cannot invent plugin identities or become selected restores");

            bep=Root(temporary,"rescan-isolation");unit=Unit(bep,"Selected");Dll(unit,"Selected.dll","Selected",new[]{"selected"},Array.Empty<string>(),null);
            var selectedCandidate=DisabledModFiles.Inventory(bep).Single();inspected=NommDisabledCatalog.ReadUnit(bep,selectedCandidate,Bundle);
            string oversized=Unit(bep,"UnrelatedOversized");for(int i=0;i<=DisabledModFiles.MaximumFiles;i++)File.WriteAllText(Path.Combine(oversized,"file-"+i+".asset"),"unrelated");
            check(NommDisabledCatalog.Observe(bep,selectedCandidate)==inspected.ObservationKey,"observing a selected unit does not rescan an unrelated oversized disabled file tree");
            for(int i=0;i<=DisabledModFiles.MaximumUnits;i++)Unit(bep,"UnrelatedUnit"+i);
            check(NommDisabledCatalog.Observe(bep,selectedCandidate)==inspected.ObservationKey,"per-unit observation does not trigger a full inventory after unrelated unit count grows beyond the discovery bound");
            check(DisabledModFiles.Rescan(bep,selectedCandidate).Files.SequenceEqual(selectedCandidate.Files),"per-unit rescanning returns the complete safely sorted source file list");
            var escape=new DisabledModCandidate {SourceRelative="../Selected",SourcePath=selectedCandidate.SourcePath,TargetRelative="Selected"};
            Reject(check,()=>DisabledModFiles.Rescan(bep,escape),"per-unit rescanning rejects source traversal before touching another tree");
            escape=new DisabledModCandidate {SourceRelative="Selected",SourcePath=oversized,TargetRelative="Selected"};
            Reject(check,()=>DisabledModFiles.Rescan(bep,escape),"per-unit rescanning refuses a source path that no longer matches its guarded NOMM leaf");
            File.WriteAllText(Path.Combine(unit,"appeared.asset"),"new");
            Reject(check,()=>NommDisabledCatalog.Observe(bep,selectedCandidate),"optimized per-unit observations still reject new files added after discovery");

            bep=Root(temporary,"disabled-parent");string parent=Unit(bep,"ParentFolderDifferentFromRegistry");
            File.WriteAllText(Path.Combine(parent,"meta.json"),"{\"id\":\"parent.registry\"}");Dll(parent,"Parent.dll","Parent",new[]{"parent.plugin"},Array.Empty<string>(),null);
            unit=Unit(bep,"ChildFolderDifferentFromRegistry");File.WriteAllText(Path.Combine(unit,"meta.json"),"{\"id\":\"child.registry\",\"artifact\":{\"extends\":{\"id\":\"parent.registry\"}}}");Dll(unit,"Child.dll","Child",new[]{"child.plugin"},Array.Empty<string>(),null);
            inspected=Read(bep,"ChildFolderDifferentFromRegistry",Bundle);
            check(inspected.Error==""&&inspected.RegistryId=="child.registry"&&inspected.ParentId=="parent.registry"&&inspected.Candidate.TargetRelative=="ParentFolderDifferentFromRegistry/addons/child.registry","disabled NOMM addon routing follows parent registry metadata while retaining actual physical folder names");
            check(NommDisabledCatalog.Prepare(bep,inspected).TargetRelative=="ParentFolderDifferentFromRegistry/addons/child.registry","addon receipt targets its actual enabled parent/addons location");
            Directory.Move(parent,Path.Combine(bep,"plugins","ParentFolderDifferentFromRegistry"));
            check(Read(bep,"ChildFolderDifferentFromRegistry",Bundle).Candidate.TargetRelative=="ParentFolderDifferentFromRegistry/addons/child.registry","NOMM addon discovery can bind an already enabled metadata-proven parent");

            bep=Root(temporary,"active-nested-parent");parent=Path.Combine(bep,"plugins","RootFolder","addons","ExistingAddonFolder");Directory.CreateDirectory(parent);
            File.WriteAllText(Path.Combine(parent,"meta.json"),"{\"id\":\"existing.addon\",\"artifact\":{\"extends\":{\"id\":\"root.registry\"}}}");
            unit=Unit(bep,"Child");File.WriteAllText(Path.Combine(unit,"meta.json"),"{\"id\":\"child.addon\",\"artifact\":{\"extends\":{\"id\":\"existing.addon\"}}}");Dll(unit,"Child.dll","Child",new[]{"child.plugin"},Array.Empty<string>(),null);
            inspected=Read(bep,"Child",Bundle);
            check(inspected.Error==""&&inspected.Candidate.TargetRelative=="RootFolder/addons/ExistingAddonFolder/addons/child.addon","already enabled addon parents retain their actual nested NOMM addons path");

            bep=Root(temporary,"missing-parent");unit=Unit(bep,"Child");File.WriteAllText(Path.Combine(unit,"meta.json"),"{\"id\":\"child\",\"artifact\":{\"extends\":{\"id\":\"missing.parent\"}}}");Dll(unit,"Child.dll","Child",new[]{"child.plugin"},Array.Empty<string>(),null);
            check(Read(bep,"Child",Bundle).Error.Contains("missing or ambiguous"),"missing NOMM parent routing fails closed without inventing a destination");
            foreach(string name in new[]{"FirstParent","SecondParent"})
            {
                parent=Unit(bep,name);File.WriteAllText(Path.Combine(parent,"meta.json"),"{\"id\":\"missing.parent\"}");
            }
            check(Read(bep,"Child",Bundle).Error.Contains("missing or ambiguous"),"several folders claiming one NOMM parent ID are rejected");

            bep=Root(temporary,"cyclic-parent");foreach(string name in new[]{"First","Second"})
            {
                unit=Unit(bep,name);File.WriteAllText(Path.Combine(unit,"meta.json"),"{\"id\":\""+name+"\",\"artifact\":{\"extends\":{\"id\":\""+(name=="First"?"Second":"First")+"\"}}}");Dll(unit,name+".dll",name,new[]{name+".plugin"},Array.Empty<string>(),null);
            }
            check(Read(bep,"First",Bundle).Error.Contains("cyclic"),"cyclic NOMM addon metadata cannot recurse indefinitely or prepare a restore");

            foreach(string metadata in new[]{"{", "[]", "{\"id\":true}", "{\"id\":\"first\",\"id\":\"second\"}", "{\"id\":\"ok\"} {}", "{\"id\":\"child\",\"artifact\":{\"extends\":{}}}", "{\"id\":\"../unsafe\",\"artifact\":{\"extends\":{\"id\":\"parent\"}}}"})
            {
                bep=Root(temporary,"invalid-meta-"+Guid.NewGuid().ToString("N"));unit=Unit(bep,"BrokenMetadata");File.WriteAllText(Path.Combine(unit,"meta.json"),metadata);Dll(unit,"Plugin.dll","Valid",new[]{"valid.plugin"},Array.Empty<string>(),null);
                var messages=new List<string>();inspected=Read(bep,"BrokenMetadata",Bundle,messages.Add);
                check(inspected.Error.Length>0&&inspected.Entries.Count==0&&messages.Any(m=>m.Contains("BrokenMetadata")),"malformed NOMM routing metadata fails its unit with a visible cause: "+metadata);
            }

            bep=Root(temporary,"invalid-pack");unit=Unit(bep,"BrokenPack");Dll(unit,"Wrapper.dll","Wrapper",new[]{"wrapper"},new[]{"pack.nobp"},null);
            inspected=Read(bep,"BrokenPack",_=>throw new InvalidDataException("patch_manifest is missing"));
            check(inspected.Error.Contains("patch_manifest is missing")&&inspected.Entries.Count==0,"unreadable embedded pack metadata fails the whole restore unit without an inferred content version");
            inspected=Read(bep,"BrokenPack",_=>new DisabledBundleInfo {Id="invalid",Name="Pack",Version="",GameVersion="0.34.2",Schema=3});
            check(inspected.Error.Length>0&&inspected.Entries.Count==0,"missing content version from an injected reader cannot become a disabled mod row");
            inspected=Read(bep,"BrokenPack",_=>throw new BadImageFormatException("Damaged bundle bytes"));
            check(inspected.Error.Contains("Damaged bundle bytes")&&inspected.Entries.Count==0,"BadImageFormatException from managed content inspection is not mistaken for an ignorable native support DLL");

            foreach(string[] owners in new[]{Array.Empty<string>(),new[]{"first.owner","second.owner"},new[]{"duplicate.owner","duplicate.owner"}})
            {
                bep=Root(temporary,"ambiguous-owner-"+Guid.NewGuid().ToString("N"));unit=Unit(bep,"OwnerUnsupported");Dll(unit,"Wrapper.dll","Wrapper",owners,new[]{"pack.nobp"},null);
                inspected=Read(bep,"OwnerUnsupported",Bundle);
                check(inspected.Error.Length>0&&inspected.Entries.Count==0,"embedded NOMM content rejects missing, multiple or duplicate wrapper owners instead of guessing");
            }
            bep=Root(temporary,"duplicate-pack");unit=Unit(bep,"DuplicatePacks");Dll(unit,"Wrapper.dll","Wrapper",new[]{"wrapper"},new[]{"first.nobp","second.nobp"},null);
            check(Read(bep,"DuplicatePacks",Bundle).Error.Length>0,"duplicate exact bundle identities inside one disabled unit fail closed");

            bep=Root(temporary,"mid-read-change");unit=Unit(bep,"Mutable");Dll(unit,"Wrapper.dll","Wrapper",new[]{"wrapper"},new[]{"pack.nobp"},null);string asset=Path.Combine(unit,"asset.bin");File.WriteAllText(asset,"before");
            inspected=Read(bep,"Mutable",stream=>{var info=Bundle(stream);File.AppendAllText(asset,"after");return info;});
            check(inspected.Error.Contains("changed")&&inspected.Entries.Count==0,"NOMM catalog refuses a unit that changes while embedded metadata is read");
            inspected=Read(bep,"Mutable",Bundle);File.WriteAllText(Path.Combine(unit,"new-file.asset"),"new");
            Reject(check,()=>NommDisabledCatalog.Prepare(bep,inspected),"new files appearing after discovery cannot silently join a restore receipt");

            bep=Root(temporary,"unrelated-errors");unit=Unit(bep,"Bad");File.WriteAllText(Path.Combine(unit,"meta.json"),"{");Dll(unit,"Bad.dll","Bad",new[]{"bad"},Array.Empty<string>(),null);
            unit=Unit(bep,"Good");Dll(unit,"Good.dll","Good",new[]{"good"},Array.Empty<string>(),null);
            var all=DisabledModFiles.Inventory(bep).Select(candidate=>NommDisabledCatalog.ReadUnit(bep,candidate,Bundle)).ToArray();
            check(all.Single(u=>u.Candidate.SourceRelative=="Bad").Error.Length>0&&all.Single(u=>u.Candidate.SourceRelative=="Good").Entries.Single().Id=="good","a malformed disabled unit does not prevent independent discovery of unrelated valid mods");
        }
        finally {Directory.Delete(temporary,true);}
    }

    private static string Root(string basis,string name)
    {
        string path=Path.Combine(basis,name,"BepInEx");Directory.CreateDirectory(Path.Combine(path,"disabledPlugins"));Directory.CreateDirectory(Path.Combine(path,"plugins"));return path;
    }
    private static string Unit(string root,string name){string path=Path.Combine(root,"disabledPlugins",name);Directory.CreateDirectory(path);return path;}
    private static NommDisabledUnit Read(string root,string name,Func<Stream,DisabledBundleInfo> reader,Action<string>? log=null)=>NommDisabledCatalog.ReadUnit(root,DisabledModFiles.Inventory(root).Single(c=>c.SourceRelative==name),reader,log);
    private static byte[] BundleBytes(string id="actual_content_id",string name="Actual pack",string version="2.7.3")=>Encoding.UTF8.GetBytes(id+"\n{\"modName\":\""+name+"\",\"modVersion\":\""+version+"\"}");
    private static DisabledBundleInfo Bundle(Stream stream)
    {
        using(var copy=new MemoryStream())
        {
            stream.CopyTo(copy);var bytes=copy.ToArray();int split=Array.IndexOf(bytes,(byte)'\n');
            if(split<0)throw new InvalidDataException("Test bundle has no patch_manifest.");
            return DisabledContentMetadata.Parse(Encoding.UTF8.GetString(bytes,0,split),bytes.Skip(split+1).ToArray());
        }
    }
    private static string Dll(string folder,string filename,string assemblyName,string[] ids,string[] bundles,string? manifest,bool dependencies=false)
    {
        string path=Path.Combine(folder,filename);
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(assemblyName,new Version(1,0)),assemblyName,ModuleKind.Dll))
        {
            var module=assembly.MainModule;var bep=new AssemblyNameReference("BepInEx",new Version(5,4,23,0));
            for(int p=0;p<ids.Length;p++)
            {
                var type=new TypeDefinition("Fixture","Plugin"+p,Mono.Cecil.TypeAttributes.Public|Mono.Cecil.TypeAttributes.Class,module.TypeSystem.Object);module.Types.Add(type);
                var constructor=new MethodReference(".ctor",module.TypeSystem.Void,new TypeReference("BepInEx","BepInPlugin",module,bep)) {HasThis=true};
                for(int i=0;i<3;i++)constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                var plugin=new CustomAttribute(constructor);plugin.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,ids[p]));plugin.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,"Plugin "+p));plugin.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,"1.0"));type.CustomAttributes.Add(plugin);
                if(dependencies&&p==0)
                {
                    Dependency(module,type,bep,"actual.helper",null,null);
                    Dependency(module,type,bep,"outside.versioned","2.1",module.TypeSystem.String);
                    Dependency(module,type,bep,"optional.soft",2,module.TypeSystem.Int32);
                }
            }
            foreach(string bundle in bundles)module.Resources.Add(new EmbeddedResource(bundle,ManifestResourceAttributes.Private,BundleBytes()));
            if(manifest!=null)module.Resources.Add(new EmbeddedResource("doorman.json",ManifestResourceAttributes.Private,Encoding.UTF8.GetBytes(manifest)));
            assembly.Write(path);
        }
        return path;
    }
    private static void Dependency(ModuleDefinition module,TypeDefinition owner,AssemblyNameReference scope,string id,object? value,TypeReference? kind)
    {
        var constructor=new MethodReference(".ctor",module.TypeSystem.Void,new TypeReference("BepInEx","BepInDependency",module,scope)) {HasThis=true};constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        if(kind!=null)constructor.Parameters.Add(new ParameterDefinition(kind));
        var attribute=new CustomAttribute(constructor);attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,id));if(kind!=null)attribute.ConstructorArguments.Add(new CustomAttributeArgument(kind,value));owner.CustomAttributes.Add(attribute);
    }
    private static byte[] NativeDll()
    {
        // A minimal native PE header with no CLR directory. Cecil rejects its
        // absence of managed metadata; neither the OS nor reflection loads it.
        var bytes=new byte[512];bytes[0]=(byte)'M';bytes[1]=(byte)'Z';bytes[0x3c]=0x80;
        bytes[0x80]=(byte)'P';bytes[0x81]=(byte)'E';bytes[0x84]=0x4c;bytes[0x85]=1;
        bytes[0x94]=224;bytes[0x96]=2;bytes[0x97]=0x20;bytes[0x98]=0x0b;bytes[0x99]=1;bytes[0xf4]=16;
        return bytes;
    }
    private static void Reject(Action<bool,string> check,Action action,string description)
    {
        bool rejected=false;try{action();}catch(Exception ex)when(ex is IOException||ex is InvalidDataException){rejected=true;}check(rejected,description);
    }
}
