using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    internal sealed class ModEntry
    {
        internal string Id="",Name="",Version="",Path="";
        internal string AssemblyName="",WrapperPlugin="",WrapperPath="";
        internal bool Content, Enabled, Wanted, Locked, PreviewAmbiguous;
        internal ModManifest? Manifest;
        internal string UpdateRepository="",UpdateAsset="",UpdatePlugin="",UpdatePath="",UpdateVersion="";
        internal readonly List<string> Requires=new List<string>();
    }
    internal static class ModCatalog
    {
        internal static List<ModEntry> Read(IEnumerable<InstalledContent> content,ModProfile profile,Action<string> log)
        {
            var result=content.Select(m=>new ModEntry { Id=m.Id,Name=m.Name,Version=m.Version,Content=true,Enabled=m.Enabled,Wanted=!profile.DisabledContent.Contains(m.Id,StringComparer.Ordinal),Path=m.Source }).ToList();
            var embedded=new Dictionary<string,ModManifest>(StringComparer.Ordinal);
            // Cecil reads metadata without executing DLLs or loading disabled plugins.
            foreach(var path in Directory.EnumerateFiles(Paths.PluginPath,"*.dll",SearchOption.AllDirectories))
            {
                try
                {
                    if(new FileInfo(path).Length>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("Mod DLL is too large.");
                    using(var resolver=new DefaultAssemblyResolver())
                    {
                    resolver.AddSearchDirectory(System.IO.Path.Combine(Paths.BepInExRootPath,"core"));resolver.AddSearchDirectory(Paths.ManagedPath);resolver.AddSearchDirectory(System.IO.Path.GetDirectoryName(path));
                    using(var assembly=AssemblyDefinition.ReadAssembly(path,new ReaderParameters { AssemblyResolver=resolver }))
                    {
                        foreach(var type in Types(assembly.MainModule.Types))
                        {
                            var attribute=type.CustomAttributes.FirstOrDefault(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin");
                            if(attribute==null||attribute.ConstructorArguments.Count!=3) continue;
                            string id=(string)attribute.ConstructorArguments[0].Value;
                            var item=new ModEntry { Id=id,Name=(string)attribute.ConstructorArguments[1].Value,Version=(string)attribute.ConstructorArguments[2].Value,Path=path,AssemblyName=assembly.Name.Name,Enabled=Chainloader.PluginInfos.ContainsKey(id),Wanted=!profile.DisabledPlugins.Contains(id,StringComparer.Ordinal),Locked=ModProfile.Protected(id) };
                            foreach(var dependency in type.CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInDependency"))
                            {
                                bool hard=dependency.ConstructorArguments.Count<2 || dependency.ConstructorArguments[1].Type.FullName=="System.String" || Convert.ToInt32(dependency.ConstructorArguments[1].Value)==1;
                                if(hard) item.Requires.Add((string)dependency.ConstructorArguments[0].Value);
                            }
                            result.Add(item);
                        }
                        try
                        {
                            var preview=EmbeddedModPreview.Read(assembly,path);
                            if(preview!=null)EmbeddedModPreview.Add(embedded,preview,log);
                        }
                        catch(Exception ex){log("Skipped embedded preview: "+System.IO.Path.GetFileName(path)+" ("+ex.GetType().Name+")");}
                    }
                    }
                } catch(Exception ex) { log("Skipped mod metadata: "+System.IO.Path.GetFileName(path)+" ("+ex.GetType().Name+")"); }
            }
            foreach(var item in result.Where(e=>e.Content&&e.Path.StartsWith("resource:",StringComparison.Ordinal)))
            {
                string name=item.Path.Split(':').ElementAtOrDefault(1)??"";
                var owners=result.Where(e=>!e.Content&&e.AssemblyName==name).Take(2).ToArray();
                if(owners.Length==1) { item.WrapperPlugin=owners[0].Id;item.WrapperPath=owners[0].Path;item.AssemblyName=name; }
            }
            var managed=new Dictionary<string,ModManifest>(StringComparer.Ordinal);
            var manual=new Dictionary<string,ModManifest>(StringComparer.Ordinal);
            string managedRoot=System.IO.Path.GetFullPath(System.IO.Path.Combine(Paths.PluginPath,"DOORMAN-Metadata"))+System.IO.Path.DirectorySeparatorChar;
            foreach(var path in Directory.EnumerateFiles(Paths.PluginPath,"*.doorman.json",SearchOption.AllDirectories).Take(512))
            {
                try
                {
                    var manifest=ModJson.Read<ModManifest>(path); manifest.Directory=System.IO.Path.GetDirectoryName(path)!; manifest.Validate();
                    bool downloaded=System.IO.Path.GetFullPath(path).StartsWith(managedRoot,StringComparison.OrdinalIgnoreCase);
                    if(downloaded&&(!EmbeddedModPreview.IsManagedPath(Paths.PluginPath,path,manifest)||manifest.Bundle.Length>0))
                    {
                        log("Skipped preview outside its managed plugin location: "+System.IO.Path.GetFileName(path));continue;
                    }
                    EmbeddedModPreview.Add(downloaded?managed:manual,manifest,log);
                } catch(Exception ex) { log("Skipped preview: "+System.IO.Path.GetFileName(path)+" ("+ex.GetType().Name+")"); }
            }
            var previews=EmbeddedModPreview.Merge(embedded,managed,manual);
            foreach(var item in result)
            {
                item.PreviewAmbiguous=ModManifest.Ambiguous(previews,item.Content,item.Id,item.WrapperPlugin);
                var preview=ModManifest.Match(previews,item.Content,item.Id,item.WrapperPlugin);
                if(preview!=null) { item.Manifest=preview; item.Name=preview.Name+(item.Content?"":" (plugin)"); }
            }
            SetUpdateSources(result);
            return result.OrderBy(m=>m.Locked).ThenByDescending(m=>m.Content).ThenBy(m=>m.Name,StringComparer.OrdinalIgnoreCase).ToList();
        }
        internal static void Enrich(List<ModEntry> result,NommRecord[] records)
        {
            foreach(var item in result.Where(e=>e.Manifest==null&&!e.PreviewAmbiguous))
            {
                var card=NommMetadata.Match(records,item.Content?(item.WrapperPlugin.Length>0?item.WrapperPlugin:item.Id):item.Id,item.AssemblyName,System.IO.Path.GetFileName(item.WrapperPath.Length>0?item.WrapperPath:item.Path));
                if(card==null) continue;
                string bundle=item.Content?item.Id:result.SingleOrDefaultSafe(e=>e.Content&&e.WrapperPlugin==item.Id)?.Id??"";
                item.Manifest=NommMetadata.Preview(card,bundle,item.Content?item.WrapperPlugin:item.Id);item.Name=item.Manifest.Name+(item.Content?"":" (plugin)");
            }
            SetUpdateSources(result);
        }
        internal static void SetUpdateSources(List<ModEntry> result)
        {
            foreach(var item in result)
            {
                item.UpdateRepository=item.Manifest?.Update?.Repository??"";item.UpdateAsset=item.Manifest?.Update?.Asset??"";
                if(item.UpdateRepository.Length==0&&item.Manifest!=null)
                    item.UpdateRepository=item.Manifest.Links.Select(l=>GitHubRepository.FromUrl(l.Url)).FirstOrDefault(r=>r.Length>0)??"";
                item.UpdateVersion=item.Version;
                var plugin=!item.Content?item:result.SingleOrDefaultSafe(p=>!p.Content&&p.Id==item.Manifest?.Plugin);
                if(plugin==null&&item.Content&&item.Path.StartsWith("resource:",StringComparison.Ordinal))
                {
                    string assemblyName=item.Path.Split(':').ElementAtOrDefault(1)??"";
                    var loaded=Chainloader.PluginInfos.Values.Where(p=>p.Instance!=null&&p.Instance.GetType().Assembly.GetName().Name==assemblyName).ToArray();
                    if(loaded.Length==1) plugin=result.SingleOrDefaultSafe(p=>!p.Content&&p.Id==loaded[0].Metadata.GUID);
                }
                if(plugin!=null&&result.Count(p=>!p.Content&&p.Id==plugin.Id)==1&&!ModProfile.Protected(plugin.Id)) { item.UpdatePlugin=plugin.Id;item.UpdatePath=plugin.Path; }
                if(!item.Content&&item.Manifest!=null&&item.Manifest.Bundle.Length>0)
                {
                    var pack=result.SingleOrDefaultSafe(p=>p.Content&&p.Id==item.Manifest.Bundle);
                    // Wrapper DLL versions often stay at 1.0. Use the content's
                    // real version, or admit it is unavailable when disabled.
                    item.UpdateVersion=pack?.Version??"";
                }
                else if(!item.Content&&item.Manifest!=null)
                    item.UpdateVersion=ModManifest.PluginUpdateVersion(item.Version,result.Where(p=>p.Content&&p.WrapperPlugin==item.Id).Select(p=>p.Version));
            }
        }
        private static T? SingleOrDefaultSafe<T>(this IEnumerable<T> values,Func<T,bool> predicate) where T:class { var found=values.Where(predicate).Take(2).ToArray();return found.Length==1?found[0]:null; }
        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots) {foreach(var type in roots){yield return type;foreach(var nested in Types(type.NestedTypes))yield return nested;}}
        internal static string ValidatePlugins(IEnumerable<ModEntry> entries)
        {
            var all=entries.Where(e=>!e.Content).ToList();
            foreach(var item in all.Where(e=>e.Wanted)) foreach(var dependency in item.Requires)
                if(all.Any(e=>e.Id==dependency&&!e.Wanted)) return item.Name+" needs "+all.First(e=>e.Id==dependency).Name+". Enable that first.";
            return "";
        }
    }
}
