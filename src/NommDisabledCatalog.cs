using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    internal sealed class NommDisabledUnit
    {
        internal DisabledModCandidate Candidate=new DisabledModCandidate();
        internal string RegistryId="",ParentId="",Error="",ObservationKey="";
        internal readonly List<ModEntry> Entries=new List<ModEntry>();
        internal DisabledModRestore? Receipt;
    }

    // NOMM identity is only a routing hint. Plugin identities are read from
    // Cecil and content identities from the bundle reader, never filenames.
    internal static class NommDisabledCatalog
    {
        private const int MaximumTypes=4096,MaximumAttributes=128,MaximumPlugins=256;
        private sealed class Routing { internal string Id="",Parent="",Relative=""; internal bool Active; }

        internal static NommDisabledUnit ReadUnit(string bepRoot,DisabledModCandidate candidate,Func<Stream,DisabledBundleInfo> readBundle,Action<string>? log=null)
        {
            var unit=new NommDisabledUnit { Candidate=candidate,RegistryId=candidate.SourceRelative };
            try
            {
                if(readBundle==null)throw new ArgumentNullException(nameof(readBundle));
                Observe(bepRoot,candidate);
                // Bind the complete unit before reading even its routing JSON.
                // Later size/mtime observations are only cache hints, and cannot
                // establish that an inspected identity still describes bytes.
                var receipt=DisabledModFiles.Prepare(bepRoot,candidate);
                string metadata=Path.Combine(candidate.SourcePath,"meta.json");
                var route=candidate.Files.Contains(metadata,StringComparer.OrdinalIgnoreCase)?ReadRouting(metadata,candidate.SourceRelative):new Routing {Id=candidate.SourceRelative};
                unit.RegistryId=route.Id;unit.ParentId=route.Parent;
                candidate.TargetRelative=route.Parent.Length==0?candidate.SourceRelative:ResolveParent(bepRoot,route.Parent,new HashSet<string>(StringComparer.Ordinal))+"/addons/"+route.Id;
                DisabledModFiles.ValidateTarget(candidate.TargetRelative);
                receipt.TargetRelative=candidate.TargetRelative;
                unit.ObservationKey=Observe(bepRoot,candidate);
                var embedded=new Dictionary<string,ModManifest>(StringComparer.Ordinal);
                var sidecars=new Dictionary<string,ModManifest>(StringComparer.Ordinal);
                foreach(string file in candidate.Files.Where(p=>p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)))
                    ReadDll(bepRoot,file,unit,readBundle,embedded,log);
                foreach(string file in candidate.Files.Where(p=>p.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)))
                {
                    using(var input=Open(file))AddBundle(unit,readBundle(input),file,"","","");
                }
                if(unit.Entries.Count==0)throw new InvalidDataException("The disabled unit contains no supported plugin or content pack.");
                if(unit.Entries.Where(e=>!e.Content).GroupBy(e=>e.Id,StringComparer.Ordinal).Any(g=>g.Count()>1)||unit.Entries.Where(e=>e.Content).GroupBy(e=>e.Id,StringComparer.Ordinal).Any(g=>g.Count()>1))
                    throw new InvalidDataException("The disabled unit repeats a plugin or content identity.");
                foreach(string file in candidate.Files.Where(p=>p.EndsWith(".doorman.json",StringComparison.OrdinalIgnoreCase)))
                {
                    var card=ModJson.Read<ModManifest>(file);card.Directory=Path.GetDirectoryName(file)!;card.Validate();
                    if(!string.IsNullOrEmpty(card.RequisiteError))throw new InvalidDataException(card.RequisiteError);
                    if(card.Plugin.Length>0&&!unit.Entries.Any(e=>!e.Content&&e.Id==card.Plugin)||card.Bundle.Length>0&&!unit.Entries.Any(e=>e.Content&&e.Id==card.Bundle))
                        throw new InvalidDataException("A disabled preview identifies content outside its mod unit.");
                    EmbeddedModPreview.Add(sidecars,card,message=>log?.Invoke(message));
                }
                var cards=EmbeddedModPreview.Merge(embedded,new Dictionary<string,ModManifest>(StringComparer.Ordinal),sidecars);
                foreach(var entry in unit.Entries)
                {
                    entry.ExternalDisabled=true;entry.RestoreSource=candidate.SourceRelative;entry.RestoreTarget=candidate.TargetRelative;
                    entry.PreviewAmbiguous=ModManifest.Ambiguous(cards,entry.Content,entry.Id,entry.WrapperPlugin);
                    entry.Manifest=ModManifest.Match(cards,entry.Content,entry.Id,entry.WrapperPlugin);
                    if(entry.Manifest!=null)entry.Name=entry.Manifest.Name+(entry.Content?"":" (plugin)");
                }
                if(Observe(bepRoot,candidate)!=unit.ObservationKey)throw new IOException("The disabled mod changed while its metadata was being read.");
                DisabledModFiles.Verify(bepRoot,new[]{receipt});
                if(Observe(bepRoot,candidate)!=unit.ObservationKey)throw new IOException("The disabled mod changed while its metadata receipt was being verified.");
                receipt.PluginIds=unit.Entries.Where(e=>!e.Content).Select(e=>e.Id).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
                unit.Receipt=receipt;
            }
            catch(Exception ex)
            {
                unit.Error="Could not inspect NOMM-disabled mod '"+candidate.SourceRelative+"': "+ex.Message;
                unit.Entries.Clear();unit.Receipt=null;log?.Invoke(unit.Error);
            }
            return unit;
        }

        internal static DisabledModRestore Prepare(string bepRoot,NommDisabledUnit unit)
        {
            if(unit==null||unit.Error.Length>0||unit.Entries.Count==0)throw new InvalidDataException(unit?.Error??"The disabled mod has not been inspected.");
            if(Observe(bepRoot,unit.Candidate)!=unit.ObservationKey)throw new IOException("The disabled mod changed after discovery. Reopen Mods to inspect it again.");
            if(unit.ParentId.Length>0&&ResolveParent(bepRoot,unit.ParentId,new HashSet<string>(StringComparer.Ordinal))+"/addons/"+unit.RegistryId!=unit.Candidate.TargetRelative)
                throw new IOException("The disabled addon's parent location changed after discovery.");
            var receipt=unit.Receipt??throw new InvalidDataException("The disabled mod's exact inspected content receipt is unavailable. Reopen Mods to inspect it again.");
            if(receipt.SourceRelative!=unit.Candidate.SourceRelative||receipt.TargetRelative!=unit.Candidate.TargetRelative)throw new InvalidDataException("The disabled mod's restore routing changed after inspection.");
            // Always verify the bytes, including on the first selection and on
            // cached reopens whose size and timestamp observations still match.
            DisabledModFiles.Verify(bepRoot,new[]{receipt});
            if(Observe(bepRoot,unit.Candidate)!=unit.ObservationKey)throw new IOException("The disabled mod changed while its restore receipt was being prepared.");
            return receipt;
        }

        internal static string Observe(string bepRoot,DisabledModCandidate candidate)
        {
            var fresh=DisabledModFiles.Rescan(bepRoot,candidate);
            if(!fresh.SourcePath.Equals(candidate.SourcePath,StringComparison.OrdinalIgnoreCase)||!fresh.Files.SequenceEqual(candidate.Files,StringComparer.Ordinal))
                throw new IOException("The disabled mod's file list changed after discovery.");
            var key=new StringBuilder(candidate.SourceRelative).Append('\n').Append(candidate.TargetRelative).Append('\n');
            foreach(string file in fresh.Files)
            {
                var info=new FileInfo(file);
                if(!info.Exists||(info.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Device))!=0||info.Length>DisabledModFiles.MaximumFileBytes)
                    throw new InvalidDataException("The disabled mod has an unsafe or missing file.");
                key.Append(file).Append('|').Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append('|').Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return key.ToString();
        }

        private static void ReadDll(string bepRoot,string path,NommDisabledUnit unit,Func<Stream,DisabledBundleInfo> readBundle,IDictionary<string,ModManifest> cards,Action<string>? log)
        {
            if(new FileInfo(path).Length>InstalledFilePolicy.MaximumMetadataDllBytes)throw new InvalidDataException("The disabled DLL is too large to inspect safely. Re-enable it in NOMM and restart normally.");
            using(var resolver=new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.Combine(bepRoot,"core"));resolver.AddSearchDirectory(Path.GetDirectoryName(path)!);
                foreach(string directory in unit.Candidate.Files.Select(p=>Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase))resolver.AddSearchDirectory(directory);
                using(var input=Open(path))
                using(var assembly=ReadManaged(input,resolver,path,log))
                {
                    if(assembly==null)return;
                    var owners=new List<ModEntry>();
                    foreach(var type in Types(assembly.MainModule.Types))
                    {
                        if(type.CustomAttributes.Count>MaximumAttributes)throw new InvalidDataException("The disabled DLL has too many metadata attributes.");
                        var identities=type.CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin").Take(2).ToArray();
                        if(identities.Length==0)continue;
                        if(identities.Length!=1||identities[0].ConstructorArguments.Count!=3)throw new InvalidDataException("The disabled DLL has ambiguous plugin metadata.");
                        var args=identities[0].ConstructorArguments;
                        string id=args[0].Value as string??"",name=args[1].Value as string??"",version=args[2].Value as string??"";
                        if(!ModUpdateFiles.PluginIdentifierValid(id)||!Text(name,256)||!Text(version,128)||!System.Version.TryParse(version,out _))throw new InvalidDataException("The disabled DLL has invalid plugin identity, name or version metadata.");
                        if(owners.Any(e=>e.Id==id)||unit.Entries.Count(e=>!e.Content)+owners.Count>=MaximumPlugins)throw new InvalidDataException("The disabled DLL has duplicate or too many plugin identities.");
                        var entry=new ModEntry {Id=id,Name=name,Version=version,Path=path,AssemblyName=assembly.Name.Name,Enabled=false,Wanted=false,Locked=ModProfile.Protected(id)};
                        foreach(var dependency in type.CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInDependency"))
                        {
                            if(dependency.ConstructorArguments.Count<1||dependency.ConstructorArguments.Count>2)throw new InvalidDataException("The disabled plugin has invalid dependency metadata.");
                            bool versioned=dependency.ConstructorArguments.Count==2&&dependency.ConstructorArguments[1].Type.FullName=="System.String";
                            bool hard=dependency.ConstructorArguments.Count==1||versioned||(Convert.ToInt32(dependency.ConstructorArguments[1].Value,CultureInfo.InvariantCulture)&1)!=0;
                            if(!hard)continue;
                            var required=new ModRequisite { Plugin=dependency.ConstructorArguments[0].Value as string??"",MinimumVersion=versioned?dependency.ConstructorArguments[1].Value as string??"":""};
                            required.Validate();entry.Requires.Add(required.Plugin);entry.RequiredRequisites.Add(required);
                        }
                        owners.Add(entry);
                    }
                    unit.Entries.AddRange(owners);
                    var bundles=assembly.MainModule.Resources.Where(r=>r.Name.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)).Take(DisabledModFiles.MaximumFiles+1).ToArray();
                    if(bundles.Length>DisabledModFiles.MaximumFiles)throw new InvalidDataException("The disabled DLL contains too many content resources.");
                    if(bundles.Length>0&&(owners.Count!=1||bundles.GroupBy(r=>r.Name,StringComparer.Ordinal).Any(g=>g.Count()>1)))
                        throw new InvalidDataException("Embedded disabled content needs exactly one proven wrapper plugin and unambiguous resource names.");
                    foreach(var resource in bundles)
                    {
                        if(!(resource is EmbeddedResource embedded))throw new InvalidDataException("An embedded disabled pack cannot use an external resource.");
                        if(!Text(assembly.Name.Name,256)||assembly.Name.Name.Any(c=>c==':'||c=='/'||c=='\\')||!Text(resource.Name,1536)||resource.Name.Any(c=>c==':'||c=='/'||c=='\\'))throw new InvalidDataException("The disabled pack's assembly/resource source is invalid.");
                        using(var stream=embedded.GetResourceStream())
                        {
                            if(stream.Length>InstalledFilePolicy.MaximumMetadataDllBytes)throw new InvalidDataException("The embedded disabled pack is too large.");
                            AddBundle(unit,readBundle(stream),"resource:"+assembly.Name.Name+":"+resource.Name,path,owners[0].Id,assembly.Name.Name);
                        }
                    }
                    var preview=EmbeddedModPreview.Read(assembly,path);
                    if(preview!=null)
                    {
                        if(!string.IsNullOrEmpty(preview.RequisiteError))throw new InvalidDataException(preview.RequisiteError);
                        EmbeddedModPreview.Add(cards,preview,message=>log?.Invoke(message));
                    }
                }
            }
        }

        private static void AddBundle(NommDisabledUnit unit,DisabledBundleInfo info,string source,string wrapperPath,string wrapperPlugin,string assemblyName)
        {
            if(unit.Entries.Count>=512)throw new InvalidDataException("The disabled unit contains too many plugin/content identities.");
            if(info==null||!Text(info.Id,256)||info.Id.Any(c=>c=='/'||c=='\\'||c==':'||c=='|'||c=='#')||info.Id.Contains("_--")||!Text(info.Name,256)||!Text(info.Version,128)||info.Version.Any(c=>c=='_'||c=='|'||c=='#')||!Text(info.GameVersion,128)||info.Schema<0||info.Schema>3)
                throw new InvalidDataException("The disabled bundle reader returned invalid content metadata.");
            unit.Entries.Add(new ModEntry {Id=info.Id,Name=info.Name,Version=info.Version,Path=source,Content=true,Enabled=false,Wanted=false,WrapperPath=wrapperPath,WrapperPlugin=wrapperPlugin,AssemblyName=assemblyName});
        }

        private static string ResolveParent(string bepRoot,string id,HashSet<string> visiting)
        {
            if(!visiting.Add(id)||visiting.Count>16)throw new InvalidDataException("The disabled addon's NOMM parent chain is cyclic or too deep.");
            var routes=new List<Routing>();
            foreach(var candidate in DisabledModFiles.Inventory(bepRoot))
            {
                string meta=Path.Combine(candidate.SourcePath,"meta.json");
                if(!Directory.Exists(candidate.SourcePath))continue;
                try {var route=candidate.Files.Contains(meta,StringComparer.OrdinalIgnoreCase)?ReadRouting(meta,candidate.SourceRelative):new Routing {Id=candidate.SourceRelative};route.Relative=candidate.SourceRelative;if(route.Id==id)routes.Add(route);}
                catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is XmlException||ex is System.Runtime.Serialization.SerializationException) { }
            }
            string plugins=ModUpdateFiles.Within(bepRoot,"plugins");
            if(Directory.Exists(plugins))
            {
                var paths=Directory.EnumerateDirectories(plugins).Take(DisabledModFiles.MaximumUnits+1).ToArray();
                if(paths.Length>DisabledModFiles.MaximumUnits)throw new InvalidDataException("There are too many possible NOMM parent directories.");
                var pending=new Stack<string>(paths.Select(p=>Path.GetFileName(p)!));int count=0;
                while(pending.Count>0)
                {
                    if(++count>DisabledModFiles.MaximumDirectories)throw new InvalidDataException("There are too many possible NOMM addon parent directories.");
                    string relative=pending.Pop(),leaf=Path.GetFileName(relative);
                    try
                    {
                        DisabledModFiles.ValidateTarget(relative);string checkedPath=ModUpdateFiles.Within(plugins,relative),meta=ModUpdateFiles.Within(checkedPath,"meta.json");
                        var route=File.Exists(meta)?ReadRouting(meta,leaf):new Routing {Id=leaf};route.Relative=relative;route.Active=true;if(route.Id==id)routes.Add(route);
                        string addons=ModUpdateFiles.Within(checkedPath,"addons");
                        if(Directory.Exists(addons))
                        {
                            if(relative.Split('/','\\').Length>20)throw new InvalidDataException("NOMM addon folders are nested too deeply.");
                            var children=Directory.EnumerateDirectories(addons).Take(DisabledModFiles.MaximumDirectories+1).ToArray();
                            if(children.Length>DisabledModFiles.MaximumDirectories)throw new InvalidDataException("There are too many NOMM addons.");
                            foreach(string child in children)pending.Push(relative+"/addons/"+Path.GetFileName(child));
                        }
                    }
                    catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is XmlException||ex is System.Runtime.Serialization.SerializationException) { }
                }
            }
            if(routes.Count!=1)throw new InvalidDataException("The disabled addon's NOMM parent is missing or ambiguous: "+id);
            var parent=routes[0];
            return parent.Active||parent.Parent.Length==0?parent.Relative:ResolveParent(bepRoot,parent.Parent,visiting)+"/addons/"+parent.Id;
        }

        private static Routing ReadRouting(string path,string fallback)
        {
            byte[] bytes;
            using(var input=Open(path))
            {
                if(input.Length>65536)throw new InvalidDataException("NOMM unit metadata is too large.");
                using(var output=new MemoryStream()){input.CopyTo(output);bytes=output.ToArray();}
            }
            string text=new UTF8Encoding(false,true).GetString(bytes);CheckBoundary(text);
            var quotas=new XmlDictionaryReaderQuotas {MaxDepth=32,MaxStringContentLength=65536,MaxArrayLength=65536,MaxBytesPerRead=4096,MaxNameTableCharCount=65536};
            var document=new XmlDocument {XmlResolver=null};
            using(var reader=JsonReaderWriterFactory.CreateJsonReader(bytes,quotas))document.Load(reader);
            var root=document.DocumentElement;
            if(root==null||root.GetAttribute("type")!="object")throw new InvalidDataException("NOMM unit metadata must be a JSON object.");
            var route=new Routing {Id=fallback};
            var id=Field(root,"id");if(id!=null)route.Id=RoutingText(id);
            var artifact=Field(root,"artifact");
            if(artifact!=null&&artifact.GetAttribute("type")!="null")
            {
                if(artifact.GetAttribute("type")!="object")throw new InvalidDataException("NOMM artifact metadata must be an object.");
                var extends=Field(artifact,"extends");
                if(extends!=null&&extends.GetAttribute("type")!="null")
                {
                    if(extends.GetAttribute("type")!="object")throw new InvalidDataException("NOMM parent metadata must be an object.");
                    var parent=Field(extends,"id");if(parent==null)throw new InvalidDataException("NOMM addon metadata is missing its parent ID.");route.Parent=RoutingText(parent);
                    DisabledModFiles.ValidateSource(route.Id);
                }
            }
            return route;
        }

        private static XmlElement? Field(XmlElement parent,string name)
        {
            var found=parent.ChildNodes.OfType<XmlElement>().Where(e=>e.LocalName==name).Take(2).ToArray();
            if(found.Length>1)throw new InvalidDataException("NOMM metadata repeats a routing field.");return found.SingleOrDefault();
        }
        private static string RoutingText(XmlElement node)
        {
            if(node.GetAttribute("type")!="string"||!Text(node.InnerText,256))throw new InvalidDataException("NOMM routing metadata needs a nonempty text ID.");return node.InnerText;
        }
        private static void CheckBoundary(string json)
        {
            int start=0;if(json.Length>0&&json[0]=='\uFEFF')start++;
            while(start<json.Length&&char.IsWhiteSpace(json[start]))start++;
            if(start==json.Length||json[start]!='{')throw new InvalidDataException("NOMM unit metadata must be a JSON object.");
            int depth=0;bool quoted=false,escaped=false;
            for(int i=start;i<json.Length;i++)
            {
                char value=json[i];
                if(quoted){if(escaped)escaped=false;else if(value=='\\')escaped=true;else if(value=='\"')quoted=false;continue;}
                if(value=='\"')quoted=true;
                else if(value=='{'||value=='['){if(++depth>32)throw new InvalidDataException("NOMM metadata is nested too deeply.");}
                else if(value=='}'||value==']')
                {
                    if(--depth!=0)continue;
                    if(json.Substring(i+1).Any(c=>!char.IsWhiteSpace(c)))throw new InvalidDataException("NOMM unit metadata has trailing JSON data.");return;
                }
            }
            throw new InvalidDataException("NOMM unit metadata JSON is incomplete.");
        }
        private static FileStream Open(string path)=>new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        private static AssemblyDefinition? ReadManaged(Stream input,IAssemblyResolver resolver,string path,Action<string>? log)
        {
            if(input.Length>InstalledFilePolicy.MaximumMetadataDllBytes)throw new InvalidDataException("The disabled DLL changed size or is too large to inspect safely.");
            try {return AssemblyDefinition.ReadAssembly(input,new ReaderParameters {AssemblyResolver=resolver});}
            catch(BadImageFormatException){log?.Invoke("Kept non-managed NOMM support DLL without plugin metadata: "+Path.GetFileName(path));return null;}
        }
        private static bool Text(string? value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value!.Length<=maximum&&value==value.Trim()&&!value.Any(char.IsControl);
        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
        {
            var pending=new Stack<TypeDefinition>(roots.Reverse());int count=0;
            while(pending.Count>0)
            {
                if(++count>MaximumTypes)throw new InvalidDataException("The disabled DLL has too many metadata types.");
                var type=pending.Pop();yield return type;foreach(var child in type.NestedTypes.Reverse())pending.Push(child);
            }
        }
    }
}
