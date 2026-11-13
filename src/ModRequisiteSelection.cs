using System;
using System.Collections.Generic;
using System.Linq;

namespace KellysJOINCHECK
{
    internal static class ModRequisiteSelection
    {
        internal static ModRequisiteIssue? ActionIssue(ModRequisiteIssue displayed,IEnumerable<ModRequisiteIssue> refreshed)
        {
            var matching=refreshed.Where(i=>i.Requirement.Key==displayed.Requirement.Key).ToArray();
            return matching.FirstOrDefault(i=>i.Status==displayed.Status)??matching.FirstOrDefault();
        }
        // Project verified queued DLL metadata for validation of the next boot.
        // Never mutate the live inventory or pretend the DLL is already loaded.
        internal static ModEntry[] WithPending(IEnumerable<ModEntry> inventory,IEnumerable<PendingModUpdate> queued,IReadOnlyDictionary<string,QueuedModRequisiteMetadata>? metadata=null,string pluginRoot="")
        {
            var all=inventory.ToArray();var pending=queued.ToArray();
            var result=new List<ModEntry>();
            foreach(var entry in all)
            {
                if(entry.Content&&metadata!=null&&entry.WrapperPlugin.Length>0&&metadata.TryGetValue(entry.WrapperPlugin,out var owner)&&
                    pending.Any(p=>p.Plugin==owner.OwnerPlugin)&&entry.WrapperPath.Length>0&&entry.WrapperPath.Equals(owner.Target,StringComparison.OrdinalIgnoreCase)&&
                    !QueuedModRequisiteMetadata.ManualPreview(entry.Manifest,pluginRoot)&&
                    (entry.Manifest==null||entry.Manifest.Plugin==entry.WrapperPlugin&&(entry.Manifest.Bundle.Length==0||entry.Manifest.FromNomm)))
                {
                    var content=Copy(entry,entry.Version,entry.Enabled);
                    content.Requires.AddRange(entry.Requires);content.RequiredRequisites.AddRange(entry.RequiredRequisites);
                    content.Manifest=owner.Error.Length>0?Failure(content,owner.Error):owner.Manifest??(entry.Manifest?.FromNomm==true?entry.Manifest:null);
                    result.Add(content);continue;
                }
                QueuedModRequisiteMetadata? details=null;
                if(!entry.Content)metadata?.TryGetValue(entry.Id,out details);
                var plan=!entry.Content?pending.SingleOrDefault(p=>p.Plugin==(details?.OwnerPlugin??entry.Id)):null;
                if(plan==null||string.IsNullOrEmpty(plan.PluginVersion)&&details==null) { result.Add(entry);continue; }
                string version=details?.Version.Length>0?details.Version:plan.PluginVersion??entry.Version;
                var future=Copy(entry,version,false);
                if(metadata==null){future.Requires.AddRange(entry.Requires);future.RequiredRequisites.AddRange(entry.RequiredRequisites);}
                else ApplyMetadata(future,details,pluginRoot);
                result.Add(future);
            }
            foreach(var plan in pending.Where(p=>p.Install&&!all.Any(e=>!e.Content&&e.Id==p.Plugin)))
            {
                var future=new ModEntry { Id=plan.Plugin,Name=plan.Plugin,Version=plan.PluginVersion??plan.Version,Path=plan.Target,Wanted=true,Enabled=false };
                if(metadata!=null){metadata.TryGetValue(plan.Plugin,out var details);ApplyMetadata(future,details,pluginRoot);}
                result.Add(future);
            }
            return result.ToArray();
        }
        private static void ApplyMetadata(ModEntry future,QueuedModRequisiteMetadata? details,string pluginRoot)
        {
            if(details==null||details.Error.Length>0)
            {
                future.Manifest=Failure(future,details?.Error??"The downloaded plugin's prerequisites have not been verified.");return;
            }
            future.Version=details.Version;future.UpdateVersion=details.Version;
            future.RequiredRequisites.AddRange(details.Hard);future.Requires.AddRange(details.Hard.Select(r=>r.Plugin));
            if(!QueuedModRequisiteMetadata.ManualPreview(future.Manifest,pluginRoot))future.Manifest=details.Manifest??(future.Manifest?.FromNomm==true?future.Manifest:null);
        }
        private static ModManifest Failure(ModEntry entry,string error)=>new ModManifest { Plugin=entry.Content?entry.WrapperPlugin:entry.Id,Name=entry.Name,RequisiteError=error };
        private static ModEntry Copy(ModEntry entry,string version,bool enabled)=>new ModEntry { Id=entry.Id,Name=entry.Name,Version=version,Path=entry.Path,AssemblyName=entry.AssemblyName,WrapperPlugin=entry.WrapperPlugin,WrapperPath=entry.WrapperPath,Content=entry.Content,Enabled=enabled,Wanted=entry.Wanted,Locked=entry.Locked,Manifest=entry.Manifest,PreviewAmbiguous=entry.PreviewAmbiguous,UpdateRepository=entry.UpdateRepository,UpdateAsset=entry.UpdateAsset,UpdatePlugin=entry.UpdatePlugin,UpdatePath=entry.UpdatePath,UpdateVersion=version,ExternalDisabled=entry.ExternalDisabled,RestoreSource=entry.RestoreSource,RestoreTarget=entry.RestoreTarget };
        internal static bool Satisfies(ModRequisite requirement,PendingModUpdate plan)
        {
            if(requirement.Content||plan.Plugin!=requirement.Plugin)return false;
            string version=plan.PluginVersion??"";
            return requirement.MinimumVersion.Length==0||version.Length>0&&ModReleaseVersion.Compare(version,requirement.MinimumVersion)>=0;
        }
    }
}
