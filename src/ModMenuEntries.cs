using System;
using System.Collections.Generic;
using System.Linq;

namespace KellysJOINCHECK
{
    internal sealed class ModEntry
    {
        internal string Id="",Name="",Version="",Path="";
        internal string AssemblyName="",WrapperPlugin="",WrapperPath="";
        internal bool Content, Enabled, Wanted, Locked, PreviewAmbiguous;
        internal bool ExternalDisabled;
        internal string RestoreSource="",RestoreTarget="";
        internal ModManifest? Manifest;
        internal string UpdateRepository="",UpdateAsset="",UpdatePlugin="",UpdatePath="",UpdateVersion="";
        internal readonly List<string> Requires=new List<string>();
        internal readonly List<ModRequisite> RequiredRequisites=new List<ModRequisite>();
    }

    internal static class ModMenuEntries
    {
        internal static string UpdateKey(ModEntry item)=>item.UpdateRepository+"\n"+item.UpdateVersion;
        internal static bool HasUpdate(ModEntry item,ISet<string> available,bool selfAvailable)
            =>item.Id=="kelly.nuclearoption.joincheck"?selfAvailable:available.Contains(UpdateKey(item));

        // A namespace family within the same physical DLL is one switchable
        // unit. Do not infer package ownership from a directory or name alone.
        internal static ModEntry[] PluginGroup(ModEntry item,IEnumerable<ModEntry> inventory)
        {
            if(item.Content||item.ExternalDisabled||item.Locked||ModProfile.Protected(item.Id)||string.IsNullOrWhiteSpace(item.Id)||!item.Path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))return new[]{item};
            var all=inventory.ToArray();
            var siblings=all.Where(e=>!e.Content&&!e.ExternalDisabled&&e.Path.Equals(item.Path,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(siblings.Length<2||siblings.Any(e=>e.Locked||ModProfile.Protected(e.Id)||string.IsNullOrWhiteSpace(e.Id)||all.Count(x=>!x.Content&&x.Id==e.Id)!=1))return new[]{item};
            var roots=siblings.Where(root=>siblings.All(e=>ReferenceEquals(root,e)||e.Id.StartsWith(root.Id+".",StringComparison.Ordinal))).ToArray();
            if(roots.Length!=1)return new[]{item};
            return siblings.OrderBy(e=>ReferenceEquals(e,roots[0])?0:1).ThenBy(e=>e.Id,StringComparer.Ordinal).ToArray();
        }
        internal static ModEntry[] PackageGroup(ModEntry item,IEnumerable<ModEntry> inventory)
        {
            var all=inventory.ToArray();
            if(item.ExternalDisabled)return new[]{item};
            var owner=item.Content?all.FirstOrDefault(e=>!e.Content&&!e.ExternalDisabled&&item.WrapperPath.Length>0&&e.Path.Equals(item.WrapperPath,StringComparison.OrdinalIgnoreCase)):item;
            if(owner==null)return new[]{item};
            var plugins=PluginGroup(owner,all);
            if(plugins.Length<2)return item.Content?new[]{item}:plugins;
            var packs=all.Where(e=>e.Content&&!e.ExternalDisabled&&e.Path.StartsWith("resource:",StringComparison.Ordinal)&&e.WrapperPath.Length>0&&e.WrapperPath.Equals(owner.Path,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(packs.Length!=1||packs[0].Locked||string.IsNullOrWhiteSpace(packs[0].Id)||all.Count(e=>e.Content&&e.Id==packs[0].Id)!=1)
                return item.Content?new[]{item}:plugins;
            return plugins.Concat(packs).ToArray();
        }
        internal static bool ToggleGroup(ModEntry item,IEnumerable<ModEntry> inventory)
        {
            var group=PackageGroup(item,inventory);bool wanted=!group.All(e=>e.Wanted);
            foreach(var member in group)member.Wanted=wanted;
            return wanted;
        }

        internal static string DisplayVersion(ModEntry item)
            => !item.Content&&item.Id=="kelly.nuclearoption.joincheck"?SelfUpdateRelease.Installed:item.Version;

        internal static List<ModEntry> Visible(IEnumerable<ModEntry> inventory)
        {
            var all=inventory.ToList();
            var owners=all.Where(e=>!e.Content).GroupBy(e=>e.Id,StringComparer.Ordinal)
                .Where(g=>g.Count()==1).Select(g=>g.Single())
                .Where(e=>e.Id.Length>0&&e.Enabled&&e.Wanted&&!ModProfile.Protected(e.Id)&&PluginGroup(e,all).Length==1);
            var hidden=new HashSet<ModEntry>(owners.Where(owner=>owner.Path.Length>0&&all.Any(pack=>
                pack.Content&&pack.WrapperPlugin==owner.Id&&pack.WrapperPath.Length>0&&
                pack.WrapperPath.Equals(owner.Path,StringComparison.OrdinalIgnoreCase))));
            foreach(var item in all.Where(e=>!e.Content))
            {
                var group=PackageGroup(item,all);
                if(group.Length>1)foreach(var member in group.Skip(1))hidden.Add(member);
            }
            // Keep the full inventory for dependencies, updates and saved lists.
            // A disabled or pending wrapper stays accessible for recovery.
            return all.Where(e=>!hidden.Contains(e)).ToList();
        }
    }
}
