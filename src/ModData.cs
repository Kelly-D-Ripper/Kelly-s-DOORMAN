using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class ModProfile
    {
        [DataMember(Name="schema", Order=0)] public int Schema = 1;
        [DataMember(Name="autoMatchServers", Order=1)] public bool AutoMatchServers = true;
        [DataMember(Name="disabledContent", Order=2)] public string[] DisabledContent = Array.Empty<string>();
        [DataMember(Name="disabledPlugins", Order=3)] public string[] DisabledPlugins = Array.Empty<string>();
        internal ModProfile Copy() => new ModProfile { AutoMatchServers=AutoMatchServers, DisabledContent=(string[])DisabledContent.Clone(), DisabledPlugins=(string[])DisabledPlugins.Clone() };
        internal static bool Doorman(string id) => string.Equals(id,"kelly.nuclearoption.joincheck",StringComparison.OrdinalIgnoreCase) || string.Equals(id,"kelly.nuclearoption.joincheck.server",StringComparison.OrdinalIgnoreCase);
        internal static bool Protected(string id) => Doorman(id) || id == "com.nikkorap.blueprinter";
        internal void Validate()
        {
            if (Schema != 1 || DisabledContent == null || DisabledPlugins == null || DisabledContent.Length > 512 || DisabledPlugins.Length > 512)
                throw new InvalidDataException("Unsupported mod profile.");
            foreach (var id in DisabledContent.Concat(DisabledPlugins))
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl)) throw new InvalidDataException("Invalid mod identifier.");
            if (DisabledPlugins.Any(Protected)) throw new InvalidDataException("DOORMAN and Blueprinter must stay enabled.");
            DisabledContent = DisabledContent.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            DisabledPlugins = DisabledPlugins.Distinct(StringComparer.Ordinal).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
    }

    [DataContract]
    internal sealed class ModLink
    {
        [DataMember(Name="label")] public string Label = "";
        [DataMember(Name="url")] public string Url = "";
    }
    [DataContract]
    internal sealed class ModManifest
    {
        [DataMember(Name="bundle")] public string Bundle = "";
        [DataMember(Name="plugin")] public string Plugin = "";
        [DataMember(Name="name")] public string Name = "";
        [DataMember(Name="author")] public string Author = "";
        [DataMember(Name="description")] public string Description = "";
        [DataMember(Name="image")] public string Image = "";
        [DataMember(Name="links")] public ModLink[] Links = Array.Empty<ModLink>();
        [DataMember(Name="update",EmitDefaultValue=false)] public ModUpdateSource? Update=null;
        internal string Directory = "";
        internal bool FromNomm=false;
        internal string RemoteImage="";
        internal string EmbeddedAssembly="",EmbeddedImage="";
        internal Guid EmbeddedModule;
        internal const int MaximumImageBytes=12*1024*1024;
        internal byte[]? ReadImageBytes()=>EmbeddedModPreview.ReadImageBytes(this);
        internal void Validate()
        {
            Name = Clean(Name,120); Author = Clean(Author,120); Description = Clean(Description,4000);
            Bundle = Clean(Bundle,256); Plugin = Clean(Plugin,256); Image = Clean(Image,260);
            if (Name.Length == 0 || (Bundle.Length == 0 && Plugin.Length == 0)) throw new InvalidDataException("A preview needs a name and bundle or plugin identifier.");
            Links = (Links ?? Array.Empty<ModLink>()).Take(6).Where(l=>l != null && SafeLink(l.Url)).Select(l=>new ModLink { Label=Clean(l.Label,32), Url=l.Url }).Where(l=>l.Label.Length>0).ToArray();
            if (Image.Length > 0 && ImagePath(Directory,Image) == null) Image = "";
            Update?.Validate();
        }
        internal static bool SafeLink(string url) => Uri.TryCreate(url,UriKind.Absolute,out var uri) && (uri.Scheme==Uri.UriSchemeHttps || uri.Scheme==Uri.UriSchemeHttp) && uri.UserInfo.Length==0 && url.Length<=2048;
        internal static ModManifest? Match(IDictionary<string,ModManifest> previews,bool content,string id,string wrapperPlugin)
        {
            // A direct card wins, including an ambiguous duplicate sentinel.
            // Wrapper ownership comes from installed DLL metadata, so disabled
            // plugins need not execute merely to show a preview.
            if(previews.TryGetValue((content?"content:":"plugin:")+id,out var direct))return direct;
            if(content&&wrapperPlugin.Length>0&&previews.TryGetValue("plugin:"+wrapperPlugin,out var inherited))return inherited;
            return null;
        }
        internal static bool Ambiguous(IDictionary<string,ModManifest> previews,bool content,string id,string wrapperPlugin)
        {
            if(previews.TryGetValue((content?"content:":"plugin:")+id,out var direct))return direct==null;
            return content&&wrapperPlugin.Length>0&&previews.TryGetValue("plugin:"+wrapperPlugin,out var inherited)&&inherited==null;
        }
        internal static string PluginUpdateVersion(string pluginVersion,IEnumerable<string> ownedContentVersions)
        {
            var versions=ownedContentVersions.Take(2).ToArray();
            return versions.Length==0?pluginVersion:versions.Length==1?versions[0]:"";
        }
        internal static string? ImagePath(string directory,string relative)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":") || relative.Split('/', '\\').Any(p=>p=="..")) return null;
                string root=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
                string full=Path.GetFullPath(Path.Combine(root,relative));
                string extension=Path.GetExtension(full);
                if (!full.StartsWith(root,StringComparison.OrdinalIgnoreCase) || !(extension.Equals(".png",StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpg",StringComparison.OrdinalIgnoreCase) || extension.Equals(".jpeg",StringComparison.OrdinalIgnoreCase))) return null;
                for(var p=Path.GetDirectoryName(full);p!=null && p.Length>=root.TrimEnd(Path.DirectorySeparatorChar).Length;p=Path.GetDirectoryName(p))
                    if (System.IO.Directory.Exists(p) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) return null;
                if (File.Exists(full) && (File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0) return null;
                return full;
            } catch { return null; }
        }
        private static string Clean(string? value,int max) => new string((value??"").Take(max).Where(c=>!char.IsControl(c)||c=='\n'||c=='\t').ToArray());
    }

    internal static class ModJson
    {
        internal static T Read<T>(string path)
        {
            var info=new FileInfo(path);
            if (info.Length>65536) throw new InvalidDataException("Mod metadata is too large.");
            using(var stream=File.OpenRead(path)) return (T)new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=4096 }).ReadObject(stream)!;
        }
        internal static T ReadBytes<T>(byte[] data)
        {
            if(data==null||data.Length>65536)throw new InvalidDataException("Mod metadata is too large.");
            using(var stream=new MemoryStream(data,false))return (T)new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=4096 }).ReadObject(stream)!;
        }
        internal static void Save(string path,ModProfile value)
        {
            value.Validate(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp=path+".tmp", backup=path+".bak";
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) { new DataContractJsonSerializer(typeof(ModProfile)).WriteObject(stream,value); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temp,path,backup); else File.Move(temp,path);
        }
    }
    internal sealed class InstalledContent
    {
        internal string Id="", Version="", Name="", Source="";
        internal bool Enabled=true, Reloadable=false;
        internal long ObservedSourceLength=-1,ObservedSourceWriteTicks=-1;
    }
    internal sealed class ModMatchPlan
    {
        internal bool CanApply;
        internal string Reason="";
        internal readonly List<InstalledContent> Enable=new List<InstalledContent>(), Disable=new List<InstalledContent>();
        internal static ModMatchPlan Create(string server,string local,IEnumerable<InstalledContent> installed)
        {
            var plan=new ModMatchPlan(); var wanted=Signature.Parse(server); var mine=Signature.Parse(local);
            if (!wanted.Complete || !mine.Complete) { plan.Reason="The server has not shared its mod list. Ask the host to install DOORMAN Server."; return plan; }
            if (wanted.Game!=mine.Game) { plan.Reason="Your game version differs. Use the host's game update and Steam branch."; return plan; }
            const string loader="com.nikkorap.blueprinter";
            if (!wanted.Mods.TryGetValue(loader,out var required) || !mine.Mods.TryGetValue(loader,out var current) || required!=current) { plan.Reason="The server needs a different Blueprinter setup. Match the host's loader first."; return plan; }
            var all=installed.ToList();
            foreach(var mod in wanted.Mods.Where(m=>m.Key!=loader))
            {
                var candidates=all.Where(m=>m.Id==mod.Key && m.Version==mod.Value).ToList();
                if (candidates.Count!=1) { plan.Reason="Install the server's exact "+Diagnostics.Clean(mod.Key)+" "+Diagnostics.Clean(mod.Value)+" first."; return plan; }
                if (!candidates[0].Enabled) plan.Enable.Add(candidates[0]);
            }
            foreach(var mod in all.Where(m=>m.Enabled))
                if (!wanted.Mods.TryGetValue(mod.Id,out var version) || version!=mod.Version) plan.Disable.Add(mod);
            if (plan.Enable.Concat(plan.Disable).Any(m=>!m.Reloadable)) { plan.Reason="This mod set needs a game restart. Open Mods to change it."; return plan; }
            plan.CanApply=true; return plan;
        }
    }
}
