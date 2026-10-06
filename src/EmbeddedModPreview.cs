using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    internal static class EmbeddedModPreview
    {
        internal const string ManifestResource="doorman.json",DefaultImageResource="doorman.png";

        internal static ModManifest? Read(AssemblyDefinition assembly,string dllPath)
        {
            var metadata=Resource(assembly,ManifestResource,false);
            if(metadata==null)return null;
            var card=ModJson.ReadBytes<ModManifest>(ResourceBytes(metadata,65536));
            if(card==null)throw new InvalidDataException("The embedded preview is empty.");
            var plugins=Types(assembly.MainModule.Types).SelectMany(t=>t.CustomAttributes)
                .Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin"&&a.ConstructorArguments.Count==3)
                .Select(a=>new { Id=a.ConstructorArguments[0].Value as string??"",Name=a.ConstructorArguments[1].Value as string??"" }).ToArray();
            if(plugins.Length==0||plugins.Any(p=>string.IsNullOrWhiteSpace(p.Id)||p.Id.Length>256||p.Id.Any(char.IsControl))||plugins.GroupBy(p=>p.Id,StringComparer.Ordinal).Any(g=>g.Count()!=1))
                throw new InvalidDataException("The embedded preview needs unambiguous plugin identities.");
            if(string.IsNullOrEmpty(card.Plugin))
            {
                if(plugins.Length!=1)throw new InvalidDataException("A multi-plugin DLL must identify its embedded preview plugin.");
                card.Plugin=plugins[0].Id;
            }
            if(!plugins.Any(p=>p.Id==card.Plugin))throw new InvalidDataException("The embedded preview identifies another plugin.");
            if(!string.IsNullOrEmpty(card.Bundle))throw new InvalidDataException("Embedded previews identify a plugin. Content previews inherit through their actual wrapper.");
            if(string.IsNullOrWhiteSpace(card.Name)&&plugins.Length==1)card.Name=plugins[0].Name;
            card.Directory=Path.GetDirectoryName(Path.GetFullPath(dllPath))!;
            string image=card.Image??"";
            if(image.Length==0&&Resource(assembly,DefaultImageResource,false)!=null)image=DefaultImageResource;
            if(image.Length>0)
            {
                if(image.Any(char.IsControl)||ModManifest.ImagePath(card.Directory,image)==null)
                    throw new InvalidDataException("The embedded preview image name is invalid.");
                Resource(assembly,image,true);
            }
            card.Image=image;card.Validate();
            card.EmbeddedAssembly=Path.GetFullPath(dllPath);card.EmbeddedImage=image;card.EmbeddedModule=assembly.MainModule.Mvid;
            return card;
        }

        internal static byte[]? ReadImageBytes(ModManifest card)
        {
            byte[] bytes;
            if(!string.IsNullOrEmpty(card.EmbeddedAssembly))
            {
                if(string.IsNullOrEmpty(card.EmbeddedImage))return null;
                var info=new FileInfo(card.EmbeddedAssembly);
                if(!info.Exists)return null;
                if(info.Length>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("The preview DLL is too large.");
                using(var assembly=AssemblyDefinition.ReadAssembly(card.EmbeddedAssembly))
                {
                    if(assembly.MainModule.Mvid!=card.EmbeddedModule)throw new InvalidDataException("The preview DLL changed. Reopen Mods to read its metadata.");
                    bytes=ResourceBytes(Resource(assembly,card.EmbeddedImage,true)!,ModManifest.MaximumImageBytes);
                }
            }
            else
            {
                string? path=ModManifest.ImagePath(card.Directory,card.Image);
                if(path==null||!File.Exists(path))return null;
                using(var stream=File.OpenRead(path))bytes=BoundedBytes(stream,ModManifest.MaximumImageBytes);
            }
            if(!ModImageHeader.Allowed(bytes))throw new InvalidDataException("The preview image dimensions are unsupported.");
            return bytes;
        }

        internal static bool IsManagedPath(string pluginRoot,string file,ModManifest card)
        {
            if(string.IsNullOrEmpty(card.Plugin))return false;
            try
            {
                string expected=ModUpdateFiles.Within(pluginRoot,Path.Combine(ModUpdateFiles.MetadataDirectory(card.Plugin),"preview.doorman.json"));
                return Path.GetFullPath(file).Equals(expected,StringComparison.OrdinalIgnoreCase);
            }
            catch{return false;}
        }

        internal static void Add(IDictionary<string,ModManifest> cards,ModManifest card,Action<string> log)
        {
            foreach(string key in new[]{"content:"+card.Bundle,"plugin:"+card.Plugin}.Where(k=>!k.EndsWith(":")))
            {
                if(cards.ContainsKey(key)){cards[key]=null!;log("Duplicate preview identifier: "+key);}
                else cards.Add(key,card);
            }
        }

        internal static Dictionary<string,ModManifest> Merge(IDictionary<string,ModManifest> embedded,IDictionary<string,ModManifest> managed,IDictionary<string,ModManifest> manual)
        {
            var merged=new Dictionary<string,ModManifest>(StringComparer.Ordinal);
            foreach(var layer in new[]{embedded,managed,manual})foreach(var entry in layer)merged[entry.Key]=entry.Value;
            return merged;
        }

        private static EmbeddedResource? Resource(AssemblyDefinition assembly,string name,bool required)
        {
            var found=assembly.MainModule.Resources.Where(r=>r.Name.Equals(name,StringComparison.Ordinal)).Take(2).ToArray();
            if(found.Length==0&&!required)return null;
            if(found.Length!=1||!(found[0] is EmbeddedResource resource))throw new InvalidDataException("The preview resource is missing, external or ambiguous.");
            return resource;
        }
        private static byte[] ResourceBytes(EmbeddedResource resource,int maximum)
        {using(var stream=resource.GetResourceStream())return BoundedBytes(stream,maximum);}
        private static byte[] BoundedBytes(Stream stream,int maximum)
        {
            if(stream.CanSeek&&stream.Length>maximum)throw new InvalidDataException("The preview resource is too large.");
            using(var output=new MemoryStream())
            {
                var buffer=new byte[16384];int read;
                while((read=stream.Read(buffer,0,buffer.Length))>0)
                {if(output.Length+read>maximum)throw new InvalidDataException("The preview resource is too large.");output.Write(buffer,0,read);}
                return output.ToArray();
            }
        }
        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
        {foreach(var type in roots){yield return type;foreach(var nested in Types(type.NestedTypes))yield return nested;}}
    }
}
