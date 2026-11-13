using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    internal sealed class QueuedModRequisiteMetadata
    {
        internal string Plugin="",OwnerPlugin="",Name="",Version="",Target="",Error="";
        internal ModRequisite[] Hard=Array.Empty<ModRequisite>();
        internal ModManifest? Manifest;
        internal const int MaximumTypes=4096,MaximumAttributes=128;

        // Called only for an explicitly refreshed pending queue. The staged DLL
        // is read as metadata, never loaded into the runtime or executed.
        internal static IReadOnlyDictionary<string,QueuedModRequisiteMetadata> Read(IEnumerable<PendingModUpdate> queued,string pluginRoot)
        {
            var result=new Dictionary<string,QueuedModRequisiteMetadata>(StringComparer.Ordinal);
            var plans=queued.Take(ModUpdateFiles.MaximumPendingJobs+1).ToArray();
            if(plans.Length>ModUpdateFiles.MaximumPendingJobs)throw new InvalidDataException("Too many queued plugins to verify prerequisites.");
            foreach(var plan in plans)
            {
                try
                {
                    foreach(var value in ReadPlan(plan,pluginRoot))
                    {
                        if(result.TryGetValue(value.Plugin,out var duplicate))duplicate.Error="Several queued DLLs declare the same prerequisite plugin ID.";
                        else result.Add(value.Plugin,value);
                    }
                }
                catch(Exception ex)
                {
                    var failed=new QueuedModRequisiteMetadata { Plugin=plan.Plugin,OwnerPlugin=plan.Plugin,Version=plan.PluginVersion??"",Error="Could not verify the downloaded plugin's prerequisites: "+ex.Message };
                    result[plan.Plugin]=failed;
                }
            }
            return result;
        }

        private static QueuedModRequisiteMetadata[] ReadPlan(PendingModUpdate plan,string pluginRoot)
        {
            plan.Validate();
            if(string.IsNullOrEmpty(plan.Job))throw new InvalidDataException("The queued download has no staging folder.");
            string payload=ModUpdateFiles.Within(plan.Job,"payload.bin"),target=ModUpdateFiles.Within(pluginRoot,plan.Target);
            var info=new FileInfo(payload);
            if(!info.Exists||info.Length==0||info.Length>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("The queued DLL is missing, empty or too large.");
            if(!ModUpdateFiles.Hash(payload).Equals(plan.NewHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The queued DLL changed after downloading.");
            using(var resolver=new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.GetFullPath(Path.Combine(pluginRoot,"..","core")));
                resolver.AddSearchDirectory(Path.GetDirectoryName(payload)!);
                using(var assembly=AssemblyDefinition.ReadAssembly(payload,new ReaderParameters { AssemblyResolver=resolver }))
                {
                var entries=new List<QueuedModRequisiteMetadata>();
                foreach(var type in Types(assembly.MainModule.Types))
                {
                    if(type.CustomAttributes.Count>MaximumAttributes)throw new InvalidDataException("The queued plugin has too many metadata attributes.");
                    var plugins=type.CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin").Take(2).ToArray();
                    if(plugins.Length==0)continue;
                    if(plugins.Length!=1||plugins[0].ConstructorArguments.Count!=3)throw new InvalidDataException("The queued plugin identity is ambiguous.");
                    var attribute=plugins[0];
                    string id=attribute.ConstructorArguments[0].Value as string??"",name=attribute.ConstructorArguments[1].Value as string??"",version=attribute.ConstructorArguments[2].Value as string??"";
                    if(!ModUpdateFiles.PluginIdentifierValid(id)||ModReleaseVersion.Compare(version,version)!=0||entries.Any(e=>e.Plugin==id)||entries.Count>=16)
                        throw new InvalidDataException("The queued plugin has invalid identities or versions.");
                    var required=new List<ModRequisite>();
                    foreach(var dependency in type.CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInDependency"))
                    {
                        if(dependency.ConstructorArguments.Count<1||dependency.ConstructorArguments.Count>2)throw new InvalidDataException("The queued plugin has invalid dependency metadata.");
                        bool versioned=dependency.ConstructorArguments.Count==2&&dependency.ConstructorArguments[1].Type.FullName=="System.String";
                        bool hard=dependency.ConstructorArguments.Count==1||versioned||(Convert.ToInt32(dependency.ConstructorArguments[1].Value)&1)!=0;
                        if(!hard)continue;
                        var requirement=new ModRequisite { Plugin=dependency.ConstructorArguments[0].Value as string??"",MinimumVersion=versioned?dependency.ConstructorArguments[1].Value as string??"":"" };
                        requirement.Validate();required.Add(requirement);
                    }
                    entries.Add(new QueuedModRequisiteMetadata { Plugin=id,OwnerPlugin=plan.Plugin,Name=name,Version=version,Target=target,Hard=required.ToArray() });
                }
                var owner=entries.SingleOrDefault(e=>e.Plugin==plan.Plugin);
                if(owner==null||!string.IsNullOrEmpty(plan.PluginVersion)&&owner.Version!=plan.PluginVersion)
                    throw new InvalidDataException("The queued DLL no longer matches its verified plugin/version.");
                if(plan.Schema==3&&entries.Count!=1)throw new InvalidDataException("A prerequisite installation must contain one plugin.");
                if(plan.Schema==4)ModUpdateFiles.VerifyPackageIdentities(payload,plan.Plugin);

                var embedded=EmbeddedModPreview.Read(assembly,payload);
                if(embedded!=null)
                {
                    if(!string.IsNullOrEmpty(embedded.RequisiteError))throw new InvalidDataException(embedded.RequisiteError);
                    entries.Single(e=>e.Plugin==embedded.Plugin).Manifest=embedded;
                }
                var sidecars=(plan.Sidecars??Array.Empty<PendingModSidecar>()).Where(s=>s.Name=="preview.doorman.json"&&!s.Remove).ToArray();
                if(sidecars.Length>1)throw new InvalidDataException("The queued plugin preview is ambiguous.");
                if(sidecars.Length==1)
                {
                    var sidecar=sidecars[0];string source=ModUpdateFiles.Within(plan.Job,sidecar.Payload);
                    if(!ModUpdateFiles.Hash(source).Equals(sidecar.NewHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The queued preview changed after downloading.");
                    var card=ModJson.Read<ModManifest>(source);
                    card.Directory=ModUpdateFiles.Within(pluginRoot,ModUpdateFiles.MetadataDirectory(plan.Plugin));card.Validate();
                    if(card.Plugin!=plan.Plugin||card.Bundle.Length>0||!string.IsNullOrEmpty(card.RequisiteError))throw new InvalidDataException("The queued preview has invalid prerequisite metadata.");
                    // This mirrors normal catalog precedence: managed sidecar over
                    // embedded metadata, followed by the user's manual preview.
                    owner.Manifest=card;
                }
                return entries.ToArray();
                }
            }
        }

        internal static bool ManualPreview(ModManifest? card,string pluginRoot)
        {
            if(card==null||card.FromNomm||!string.IsNullOrEmpty(card.EmbeddedAssembly)||string.IsNullOrEmpty(card.Directory))return false;
            if(string.IsNullOrEmpty(card.Plugin))return true;
            // Catalog and queue reads already validated these paths. Projection
            // only classifies their spelling, with no disk checks or link walk.
            try
            {
                string actual=Path.GetFullPath(Path.Combine(card.Directory,"preview.doorman.json"));
                string managed=Path.GetFullPath(Path.Combine(pluginRoot,ModUpdateFiles.MetadataDirectory(card.Plugin),"preview.doorman.json"));
                return !actual.Equals(managed,StringComparison.OrdinalIgnoreCase);
            }
            catch(ArgumentException){return true;}
            catch(NotSupportedException){return true;}
            catch(PathTooLongException){return true;}
        }

        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
        {
            var pending=new Stack<TypeDefinition>(roots.Reverse());int count=0;
            while(pending.Count>0)
            {
                if(++count>MaximumTypes)throw new InvalidDataException("The queued DLL has too many metadata types.");
                var type=pending.Pop();yield return type;
                foreach(var child in type.NestedTypes.Reverse())pending.Push(child);
            }
        }
    }
}
