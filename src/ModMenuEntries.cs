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
        internal ModManifest? Manifest;
        internal string UpdateRepository="",UpdateAsset="",UpdatePlugin="",UpdatePath="",UpdateVersion="";
        internal readonly List<string> Requires=new List<string>();
    }

    internal static class ModMenuEntries
    {
        internal static List<ModEntry> Visible(IEnumerable<ModEntry> inventory)
        {
            var all=inventory.ToList();
            var owners=all.Where(e=>!e.Content).GroupBy(e=>e.Id,StringComparer.Ordinal)
                .Where(g=>g.Count()==1).Select(g=>g.Single())
                .Where(e=>e.Id.Length>0&&e.Enabled&&e.Wanted&&!ModProfile.Protected(e.Id));
            var hidden=new HashSet<ModEntry>(owners.Where(owner=>owner.Path.Length>0&&all.Any(pack=>
                pack.Content&&pack.WrapperPlugin==owner.Id&&pack.WrapperPath.Length>0&&
                pack.WrapperPath.Equals(owner.Path,StringComparison.OrdinalIgnoreCase))));
            // Keep the full inventory for dependencies, updates and saved lists.
            // A disabled or pending wrapper stays accessible for recovery.
            return all.Where(e=>!hidden.Contains(e)).ToList();
        }
    }
}
