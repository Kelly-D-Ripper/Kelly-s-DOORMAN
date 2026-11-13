using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KellysJOINCHECK
{
    internal sealed class RemovalSelection
    {
        internal string Name="";
        internal string[] Components=Array.Empty<string>();
        internal RemovalFile[] Files=Array.Empty<RemovalFile>();
    }
    internal static class AddonRemovalOwnership
    {
        private static string Physical(ModEntry entry)=>entry.Content&&entry.Path.StartsWith("resource:",StringComparison.Ordinal)?entry.WrapperPath:entry.Path;
        internal static RemovalSelection Resolve(ModEntry selected,IEnumerable<ModEntry> inventory,IEnumerable<NommDisabledUnit> disabled,string bep,string cache,IEnumerable<string>? pendingPaths=null)
        {
            if(selected==null||inventory==null||disabled==null)throw new ArgumentNullException();
            var all=inventory.ToArray();if(!all.Any(e=>ReferenceEquals(e,selected)))throw new InvalidDataException("The mod list changed. Reopen Mods and try again.");
            var queued=new HashSet<string>(pendingPaths??Array.Empty<string>(),StringComparer.OrdinalIgnoreCase);
            if(queued.Contains(Physical(selected)))throw new InvalidOperationException("This addon is already queued for deletion.");
            var files=new List<KeyValuePair<string,string>>();var members=new List<ModEntry>();
            if(selected.ExternalDisabled)
            {
                var units=disabled.Where(u=>u.Candidate.SourceRelative==selected.RestoreSource&&u.Candidate.TargetRelative==selected.RestoreTarget).Take(2).ToArray();
                if(units.Length!=1||units[0].Error.Length>0)throw new InvalidDataException("NOMM's disabled package cannot be verified. Delete it in NOMM.");
                var unit=units[0];NommDisabledCatalog.Prepare(bep,unit);
                if(unit.Entries.Any(e=>e.Locked||ModProfile.Protected(e.Id)))throw new InvalidDataException("This package contains a protected loader.");
                members.AddRange(all.Where(e=>e.ExternalDisabled&&e.RestoreSource==selected.RestoreSource&&e.RestoreTarget==selected.RestoreTarget));
                string root=ModUpdateFiles.Within(bep,"disabledPlugins");
                foreach(string path in unit.Candidate.Files)files.Add(new KeyValuePair<string,string>("disabledPlugins",Relative(root,path)));
            }
            else
            {
                if(selected.Locked||ModProfile.Protected(selected.Id))throw new InvalidDataException("DOORMAN and Blueprinter cannot be removed here.");
                string plugins=ModUpdateFiles.Within(bep,"plugins"),source=Physical(selected);
                if(string.IsNullOrWhiteSpace(source)||!Path.IsPathRooted(source))throw new InvalidDataException("The addon's file could not be identified.");
                source=ModUpdateFiles.Within(plugins,Relative(plugins,source));
                if(!File.Exists(source))throw new IOException("The addon's installed file is missing.");
                var owners=all.Where(e=>!e.ExternalDisabled&&Same(Physical(e),source)).ToArray();
                var pluginOwners=owners.Where(e=>!e.Content).ToArray();
                if(owners.Any(e=>e.Locked||ModProfile.Protected(e.Id)))throw new InvalidDataException("This file contains a protected mod.");
                if(pluginOwners.Length>1&&ModMenuEntries.PluginGroup(pluginOwners[0],all).Length!=pluginOwners.Length)throw new InvalidDataException("This file also supplies unrelated plugins. Remove it manually if appropriate.");
                members.AddRange(owners);files.Add(new KeyValuePair<string,string>("plugins",Relative(plugins,source)));
                // Only a complete DOORMAN package receipt proves ownership of
                // separate DLL/content/artwork files. Never infer it from names.
                string updateRoot=Path.Combine(cache,"DOORMAN-updates");
                foreach(string job in ModUpdateFiles.Jobs(updateRoot))
                {
                    string receipt=ModUpdateFiles.Within(job,"plan.json");if(!File.Exists(receipt))continue;
                    var p=UpdateJson.ReadFile<PendingModUpdate>(receipt);p.Validate();
                    if(!p.Package||p.State!="complete")continue;
                    var paths=new[]{p.Target}.Concat((p.PackageFiles??Array.Empty<PendingPackageFile>()).Where(f=>!f.Remove).Select(f=>f.Target(p.Target))).ToArray();
                    if(!paths.Any(path=>Same(ModUpdateFiles.Within(plugins,path),source)))continue;
                    foreach(string path in paths)
                    {
                        string full=ModUpdateFiles.Within(plugins,path);string expected=path==p.Target?p.NewHash:p.PackageFiles!.Single(f=>!f.Remove&&f.Target(p.Target)==path).NewHash;
                        if(!File.Exists(full)||!Same(ModUpdateFiles.Hash(full),expected))throw new IOException("This package changed since installation. Review it manually.");
                        files.Add(new KeyValuePair<string,string>("plugins",path));
                        members.AddRange(all.Where(e=>!e.ExternalDisabled&&Same(Physical(e),full)));
                    }
                }
            }
            var result=files.GroupBy(f=>f.Key+"/"+f.Value,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToArray();
            if(result.Length==0)throw new InvalidDataException("No verified addon files were found.");
            if(members.Any(e=>e.Locked||ModProfile.Protected(e.Id)))throw new InvalidDataException("This package contains a protected loader.");
            var removed=new HashSet<string>(members.Select(e=>(e.Content?"bundle:":"plugin:")+e.Id),StringComparer.Ordinal);
            foreach(var other in all.Where(e=>!members.Contains(e)&&!queued.Contains(Physical(e))))
            {
                var needed=ModRequisites.DeclarationsForMod(other,all).FirstOrDefault(r=>removed.Contains(r.Key));
                if(needed!=null)throw new InvalidOperationException(other.Name+" requires this addon. Remove or keep that mod first.");
            }
            return new RemovalSelection {Name=selected.Name,Files=result.Select(f=>new RemovalFile {Root=f.Key,Relative=f.Value,Hash=AddonRemovalQueue.Digest(ModUpdateFiles.Within(ModUpdateFiles.Within(bep,f.Key),f.Value))}).ToArray(),Components=members.Select(e=>e.Name).Where(n=>!string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase).ToArray()};
        }
        private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        private static string Relative(string root,string path)
        {
            string basis=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(path);if(!full.StartsWith(basis,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The addon's file is outside its expected folder.");
            string relative=full.Substring(basis.Length);ModUpdateFiles.Within(root,relative);return relative;
        }
    }
}
