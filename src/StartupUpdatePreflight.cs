using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    // Pre-chainloader code: inspect metadata only, never load plugin assemblies
    // or depend on Unity/PluginInfo while BepInEx reflects patcher signatures.
    internal static class StartupUpdatePreflight
    {
        private const int MaximumFiles=4096,MaximumTypes=4096,MaximumAttributes=128;
        private sealed class Plugin
        {
            internal string Id="",Version="",Path="",Error="";
            internal readonly List<ModRequisite> Hard=new List<ModRequisite>();
            internal ModManifest? Manifest;
            internal string ManifestError="";
            internal ModRequisite[] Declared=Array.Empty<ModRequisite>();
            internal IEnumerable<ModRequisite> Dependencies=>Hard.Concat(Declared);
        }
        private sealed class Inventory
        {
            internal Plugin[] Plugins=Array.Empty<Plugin>();
            internal readonly Dictionary<string,ModManifest?> Managed=new Dictionary<string,ModManifest?>(StringComparer.Ordinal),Manual=new Dictionary<string,ModManifest?>(StringComparer.Ordinal);
        }
        private sealed class Selection
        {
            internal readonly HashSet<string> Disabled=new HashSet<string>(StringComparer.Ordinal);
            internal bool Vanilla;
            internal bool Enabled(string id)=>ServerSetupFiles.KeepPlugin(id,Disabled,Vanilla);
        }

        internal static bool ApplyPending(string updateRoot,string pluginRoot,string configRoot,Action<string> log)
        {
            // Duplicate receipts and target paths reject before any mutation.
            var waiting=ModUpdateFiles.ReadPending(updateRoot,pluginRoot).ToList();
            if(waiting.Count==0)return true;
            bool allComplete=true;
            var selection=ReadSelection(configRoot,log);
            var installed=ReadInstalled(pluginRoot);
            var reasons=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            // A target with any active transaction is not a completed provider,
            // even when an interrupted prior boot already replaced its DLL.
            var incomplete=new HashSet<string>(waiting.Select(p=>ModUpdateFiles.Within(pluginRoot,p.Target)),StringComparer.OrdinalIgnoreCase);
            bool progress;
            do
            {
                progress=false;
                foreach(var plan in waiting.ToArray())
                {
                    string target=ModUpdateFiles.Within(pluginRoot,plan.Target);
                    try
                    {
                        ModUpdateFiles.VerifyPending(new[]{plan},pluginRoot);
                        var replacement=ReadPlugins(ModUpdateFiles.Within(plan.Job,"payload.bin"),target,pluginRoot);
                        AttachPreviews(replacement,installed,pluginRoot,plan);
                        var projected=installed.Plugins.Where(p=>!p.Path.Equals(target,StringComparison.OrdinalIgnoreCase)).Concat(replacement).ToArray();
                        string reason=Problem(projected,replacement,selection,incomplete,target);
                        if(reason.Length>0){reasons[plan.Job]=reason;continue;}
                    }
                    catch(Exception ex){reasons[plan.Job]=ex.Message;continue;}

                    // Only an actually completed prerequisite can unlock a later
                    // job. Do not project a failed download/rename as installed.
                    bool complete=ModUpdateFiles.ApplyOne(plan,pluginRoot,log);
                    waiting.Remove(plan);
                    if(!complete)allComplete=false;
                    if(complete)incomplete.Remove(target);
                    installed=ReadInstalled(pluginRoot);
                    progress|=complete;
                }
            }while(progress&&waiting.Count>0);

            foreach(var plan in waiting)
            {
                string reason=reasons.TryGetValue(plan.Job,out var value)?value:"Its prerequisites could not be installed safely.";
                string message="Startup update blocked for "+Diagnostics.Clean(plan.Plugin)+": "+reason;
                log(message);
                // Keep the payload and original receipt state for retry/recovery.
                // Persist the explanation alongside the retained download.
                try
                {
                    string path=ModUpdateFiles.Within(plan.Job,"plan.json");
                    var current=UpdateJson.ReadFile<PendingModUpdate>(path);current.Validate();
                    if(current.State=="pending"||current.State=="applying")
                    {current.Message=message.Length>1500?message.Substring(0,1500):message;UpdateJson.Save(path,current);}
                }
                catch(Exception ex){log("Could not save the blocked update reason: "+ex.Message);}
            }
            return allComplete&&waiting.Count==0;
        }

        internal static void ApplyQueues(string updateRoot,string contentRoot,string pluginRoot,string configRoot,Action<string> log)
        {
            // Both queues are checked before any file changes. A later failure
            // still requires recovery because files cannot be replaced atomically
            // as one group; callers must block temporary server selection.
            var pending=ModUpdateFiles.ReadPending(updateRoot,pluginRoot);
            ModUpdateFiles.VerifyPending(pending,pluginRoot);
            StandaloneBundleUpdates.VerifyPending(contentRoot,pluginRoot);
            ModUpdateFiles.CheckPackageConflicts(pending,StandaloneBundleUpdates.Pending(contentRoot,pluginRoot));
            if(!ApplyPending(updateRoot,pluginRoot,configRoot,log))
                throw new InvalidDataException("A plugin update did not complete; content updates were not applied.");
            StandaloneBundleUpdates.Apply(contentRoot,pluginRoot,log);
        }

        private static Selection ReadSelection(string configRoot,Action<string> log)
        {
            try
            {
                string path=Path.Combine(configRoot,"doorman-mods.json");
                var profile=File.Exists(path)?ModJson.Read<ModProfile>(path):new ModProfile();profile.Validate();
                var session=ServerSetupFiles.Pending(configRoot);
                var selection=new Selection {Vanilla=session!=null&&!session.ReportedOnly&&Signature.Parse(session.Target.Expanded).Mods.Count==0};
                selection.Disabled.UnionWith(session?.DisabledPlugins??profile.DisabledPlugins);return selection;
            }
            catch(Exception ex)
            {
                // Match DoormanRuntimeHooks.Filter: an invalid profile leaves
                // the normal unfiltered plugin set, including protected IDs.
                log("Update preflight uses the normal plugin set; profile was unreadable: "+ex.Message);
                return new Selection();
            }
        }

        private static string Problem(Plugin[] projected,Plugin[] replacement,Selection selection,HashSet<string> incomplete,string ownTarget)
        {
            var byId=projected.GroupBy(p=>p.Id,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var affected=new HashSet<string>(replacement.Select(p=>p.Id),StringComparer.Ordinal);
            // A replacement must not break an enabled installed consumer (for
            // example a release whose actual DLL version went backwards).
            bool changed;
            do
            {
                changed=false;
                foreach(var plugin in projected.Where(p=>selection.Enabled(p.Id)&&p.Dependencies.Any(d=>!d.Content&&affected.Contains(d.Plugin))))changed|=affected.Add(plugin.Id);
            }while(changed);
            var done=new HashSet<string>(StringComparer.Ordinal);var visiting=new HashSet<string>(StringComparer.Ordinal);
            string Visit(string id,bool provider=false)
            {
                if(!selection.Enabled(id))return "Required plugin is disabled: "+Diagnostics.Clean(id)+".";
                if(!byId.TryGetValue(id,out var choices)||choices.Length==0)return "Required plugin is not installed: "+Diagnostics.Clean(id)+".";
                if(choices.Length!=1)return "Required plugin has multiple installed identities: "+Diagnostics.Clean(id)+".";
                var plugin=choices[0];
                if(plugin.Error.Length>0)return plugin.Error;
                if(plugin.ManifestError.Length>0)return plugin.ManifestError;
                if(provider&&incomplete.Contains(plugin.Path)&&!plugin.Path.Equals(ownTarget,StringComparison.OrdinalIgnoreCase))return "A prerequisite update did not complete: "+Diagnostics.Clean(id)+".";
                if(done.Contains(id))return "";
                if(!visiting.Add(id))return "The hard prerequisite chain contains a cycle: "+Diagnostics.Clean(id)+".";
                foreach(var dependency in plugin.Dependencies)
                {
                    if(dependency.Content)return "Content prerequisite "+Diagnostics.Clean(dependency.Bundle)+" cannot be verified before startup. Install this update manually using its release instructions.";
                    string problem=Visit(dependency.Plugin,true);if(problem.Length>0)return problem;
                    var required=byId[dependency.Plugin][0];
                    if(dependency.MinimumVersion.Length>0)
                    {
                        int? compare=ModReleaseVersion.Compare(required.Version,dependency.MinimumVersion);
                        if(compare==null||compare<0)return Diagnostics.Clean(id)+" requires "+Diagnostics.Clean(dependency.Plugin)+" "+dependency.MinimumVersion+" or newer; installed "+Diagnostics.Clean(required.Version)+".";
                    }
                }
                visiting.Remove(id);done.Add(id);return "";
            }
            foreach(string id in affected.Where(selection.Enabled))
            {string problem=Visit(id);if(problem.Length>0)return problem;}
            return "";
        }

        private static Inventory ReadInstalled(string pluginRoot)
        {
            var inventory=new Inventory();var result=new List<Plugin>();var folders=new Stack<string>();folders.Push(Path.GetFullPath(pluginRoot));int files=0,directories=0,cards=0;
            while(folders.Count>0)
            {
                string folder=folders.Pop();if(!Directory.Exists(folder))continue;
                if(++directories>MaximumFiles||(File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Plugin folders could not be safely inspected for startup updates.");
                foreach(string path in Directory.EnumerateFiles(folder,"*",SearchOption.TopDirectoryOnly))
                {
                    if(path.EndsWith(".doorman.json",StringComparison.OrdinalIgnoreCase))
                    {
                        if(++cards>512)throw new InvalidDataException("Too many installed previews to verify startup prerequisites.");
                        string relative=Path.GetFullPath(path).Substring(Path.GetFullPath(pluginRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Length+1);
                        string safeCard=ModUpdateFiles.Within(pluginRoot,relative);
                        try
                        {
                            var card=ModJson.Read<ModManifest>(safeCard);card.Directory=Path.GetDirectoryName(safeCard)!;card.Validate();
                            if(card.Plugin.Length==0)continue;
                            bool managed=relative.Replace('\\','/').StartsWith("DOORMAN-Metadata/",StringComparison.OrdinalIgnoreCase);
                            if(managed&&(!EmbeddedModPreview.IsManagedPath(pluginRoot,safeCard,card)||card.Bundle.Length>0))continue;
                            var layer=managed?inventory.Managed:inventory.Manual;
                            if(layer.ContainsKey(card.Plugin))layer[card.Plugin]=null;else layer.Add(card.Plugin,card);
                        }
                        catch(Exception){/* Match the normal catalog: an unreadable/unbound preview is not a declaration. */}
                        continue;
                    }
                    if(!path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
                    if(++files>MaximumFiles)throw new InvalidDataException("Too many installed DLLs to verify startup prerequisites.");
                    string safe=ModUpdateFiles.Within(pluginRoot,ModUpdateFiles.RelativeTarget(pluginRoot,path));
                    try{result.AddRange(ReadPlugins(safe,safe,pluginRoot));}catch(BadImageFormatException){/* Native dependency, not a BepInPlugin. */}
                }
                foreach(string child in Directory.EnumerateDirectories(folder))folders.Push(child);
            }
            inventory.Plugins=result.ToArray();AttachPreviews(inventory.Plugins,inventory,pluginRoot,null);return inventory;
        }

        private static void AttachPreviews(Plugin[] plugins,Inventory inventory,string pluginRoot,PendingModUpdate? plan)
        {
            foreach(var plugin in plugins)
            {
                var card=plugin.Manifest;bool declared=card!=null;
                var queued=plan!=null&&plan.Plugin==plugin.Id?(plan.Sidecars??Array.Empty<PendingModSidecar>()).SingleOrDefault(s=>s.Name=="preview.doorman.json"):null;
                if(queued!=null)
                {
                    if(!queued.Remove)
                    {
                        card=ModJson.Read<ModManifest>(ModUpdateFiles.Within(plan!.Job,queued.Payload));card.Directory=ModUpdateFiles.Within(pluginRoot,ModUpdateFiles.MetadataDirectory(plugin.Id));card.Validate();declared=true;
                    }
                    // A removed managed card reveals the replacement's embedded
                    // card; it does not erase its declared prerequisites.
                }
                else if(inventory.Managed.TryGetValue(plugin.Id,out var managed)){card=managed;declared=true;}
                if(inventory.Manual.TryGetValue(plugin.Id,out var manual)){card=manual;declared=true;}
                plugin.Manifest=card;
                if(declared&&card==null)plugin.ManifestError="Prerequisite preview is ambiguous for "+Diagnostics.Clean(plugin.Id)+".";
                else if(card!=null)
                {
                    if(card.RequisiteError.Length>0)plugin.ManifestError=card.RequisiteError;
                    else plugin.Declared=ModRequisite.ValidateAll(card.Requisites);
                }
            }
        }

        private static Plugin[] ReadPlugins(string source,string installedPath,string pluginRoot)
        {
            // Installed content wrappers can legitimately exceed the updater's
            // 256 MiB download limit. Cecil reads their metadata without loading
            // embedded bundles; apply the installed-file bound to this scan.
            InstalledFilePolicy.ValidateLength(new FileInfo(source).Length);
            using(var resolver=new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.GetFullPath(Path.Combine(pluginRoot,"..","core")));
                resolver.AddSearchDirectory(Path.GetDirectoryName(installedPath)!);
                resolver.AddSearchDirectory(Path.GetDirectoryName(source)!);
                using(var assembly=AssemblyDefinition.ReadAssembly(source,new ReaderParameters {AssemblyResolver=resolver}))
                {
                var result=new List<Plugin>();var types=new Stack<TypeDefinition>(assembly.MainModule.Types);int count=0;
                while(types.Count>0)
                {
                    var type=types.Pop();if(++count>MaximumTypes)throw new InvalidDataException("An installed DLL has too many types to inspect for startup prerequisites.");
                    foreach(var child in type.NestedTypes)types.Push(child);
                    var attributes=type.CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin").ToArray();
                    if(attributes.Length==0)continue;
                    if(attributes.Length!=1||attributes[0].ConstructorArguments.Count!=3)throw new InvalidDataException("An installed DLL has ambiguous plugin metadata.");
                    var attribute=attributes[0];string id=attribute.ConstructorArguments[0].Value as string??"";
                    if(!ModUpdateFiles.PluginIdentifierValid(id))throw new InvalidDataException("An installed DLL has an invalid plugin identity.");
                    var plugin=new Plugin {Id=id,Version=attribute.ConstructorArguments[2].Value as string??"",Path=installedPath};result.Add(plugin);
                    try
                    {
                        if(type.CustomAttributes.Count>MaximumAttributes)throw new InvalidDataException("Too many plugin metadata attributes.");
                        if(ModReleaseVersion.Compare(plugin.Version,plugin.Version)!=0)throw new InvalidDataException("The installed plugin version could not be verified.");
                        foreach(var dependency in type.CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInDependency"))
                        {
                            if(dependency.ConstructorArguments.Count<1||dependency.ConstructorArguments.Count>2)throw new InvalidDataException("Invalid hard dependency metadata.");
                            bool versioned=dependency.ConstructorArguments.Count==2&&dependency.ConstructorArguments[1].Type.FullName=="System.String";
                            bool hard=dependency.ConstructorArguments.Count==1||versioned||(Convert.ToInt32(dependency.ConstructorArguments[1].Value)&1)!=0;
                            if(!hard)continue;
                            var requirement=new ModRequisite {Plugin=dependency.ConstructorArguments[0].Value as string??"",MinimumVersion=versioned?dependency.ConstructorArguments[1].Value as string??"":""};
                            requirement.Validate();plugin.Hard.Add(requirement);
                        }
                    }
                    catch(Exception ex){plugin.Error="Could not verify prerequisites for "+Diagnostics.Clean(id)+": "+ex.Message;}
                }
                if(result.Count>0)
                {
                    try
                    {
                        var card=EmbeddedModPreview.Read(assembly,source);
                        if(card!=null)foreach(var plugin in result.Where(p=>p.Id==card.Plugin))plugin.Manifest=card;
                    }
                    catch(Exception ex){foreach(var plugin in result)plugin.ManifestError="Could not verify the embedded prerequisite preview: "+ex.Message;}
                }
                return result.ToArray();
                }
            }
        }
    }
}
