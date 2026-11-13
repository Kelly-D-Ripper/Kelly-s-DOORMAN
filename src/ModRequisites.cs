using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KellysJOINCHECK
{
    internal enum ModRequisiteStatus { Missing, Disabled, RestartRequired, WrongVersion, Ambiguous, Invalid }

    internal sealed class ModRequisiteIssue
    {
        internal ModRequisite Requirement=new ModRequisite();
        internal ModEntry? Dependency;
        internal ModRequisiteStatus Status;
        internal string Message="",Via="";
        internal bool Problem => true;
        internal bool CanDownload => (Status==ModRequisiteStatus.Missing||Status==ModRequisiteStatus.WrongVersion)&&GitHubRepository.Valid(Requirement.Repository);
    }

    // Runs on an inventory snapshot when menus or selections change, with no
    // filesystem, network, Unity, timer or per-frame work.
    internal static class ModRequisites
    {
        internal const string CustomMapLoaderPlugin="com.javoski.custommaps";
        internal const string CustomMapLoaderRepository="BatongMaster/NOCustomMaps";
        private const int MaximumGraph=512;

        internal static ModRequisite[] DeclarationsForMod(ModEntry item,IEnumerable<ModEntry> inventory)
        {
            if(item==null)throw new ArgumentNullException(nameof(item));
            var values=new List<ModRequisite>();
            if(item.Manifest!=null)
            {
                if(!string.IsNullOrEmpty(item.Manifest.RequisiteError))throw new InvalidDataException(item.Manifest.RequisiteError);
                values.AddRange(ModRequisite.ValidateAll(item.Manifest.Requisites));
            }
            values.AddRange(item.RequiredRequisites);
            values.AddRange(item.Requires.Select(id=>new ModRequisite { Plugin=id }));
            if(item.Content&&item.WrapperPlugin.Length>0)
            {
                var owner=inventory.Where(e=>!e.Content&&e.Id==item.WrapperPlugin).Take(2).ToArray();
                values.Add(new ModRequisite { Plugin=item.WrapperPlugin,Name=owner.Length==1?owner[0].Name:"" });
            }
            return Merge(values);
        }

        internal static ModRequisite[] DeclarationsForMap(IEnumerable<ModRequisite>? declared)
        {
            var values=new List<ModRequisite>(ModRequisite.ValidateAll(declared?.ToArray()));
            values.Add(new ModRequisite { Plugin=CustomMapLoaderPlugin,Name="NOCustomMaps",Repository=CustomMapLoaderRepository });
            return Merge(values);
        }

        internal static ModRequisiteIssue[] ForMod(ModEntry item,IEnumerable<ModEntry> inventory)
        {
            var all=inventory.ToArray();
            try { return Evaluate(DeclarationsForMod(item,all),all); }
            catch(InvalidDataException ex) { return new[]{Invalid(ex.Message)}; }
        }

        internal static ModRequisiteIssue[] ForMap(IEnumerable<ModRequisite>? declared,IEnumerable<ModEntry> inventory)
        {
            try { return Evaluate(DeclarationsForMap(declared),inventory); }
            catch(InvalidDataException ex) { return new[]{Invalid(ex.Message)}; }
        }

        internal static ModRequisiteIssue[] Evaluate(IEnumerable<ModRequisite>? declared,IEnumerable<ModEntry> inventory)
        {
            var all=inventory.ToArray();
            var found=all.GroupBy(e=>(e.Content?"bundle:":"plugin:")+e.Id,StringComparer.Ordinal)
                .ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var issues=new List<ModRequisiteIssue>();
            var expanded=new HashSet<string>(StringComparer.Ordinal);
            var issueKeys=new HashSet<string>(StringComparer.Ordinal);
            int visited=0;
            bool capped=false;
            void Add(ModRequisite requirement,ModEntry? dependency,ModRequisiteStatus status,string message,string via)
            {
                if(issueKeys.Add(requirement.Key+":"+status+":"+requirement.MinimumVersion))
                    issues.Add(new ModRequisiteIssue { Requirement=requirement,Dependency=dependency,Status=status,Message=message,Via=via });
            }
            void Visit(ModRequisite requirement,string via)
            {
                if(++visited>MaximumGraph)
                {
                    if(!capped) { capped=true;issues.Add(Invalid("The prerequisite chain is too large to verify.")); }
                    return;
                }
                try { requirement.Validate(); }
                catch(InvalidDataException ex) { issues.Add(Invalid(ex.Message));return; }
                string name=requirement.DisplayName;
                if(!found.TryGetValue(requirement.Key,out var candidates)||candidates.Length==0)
                { Add(requirement,null,ModRequisiteStatus.Missing,"Requires "+name+". It is not installed.",via);return; }
                if(candidates.Length!=1)
                { Add(requirement,null,ModRequisiteStatus.Ambiguous,"Requires "+name+", but multiple installed copies match its identifier.",via);return; }
                var dependency=candidates[0];
                if(requirement.MinimumVersion.Length>0)
                {
                    int? comparison=ModReleaseVersion.Compare(dependency.Version,requirement.MinimumVersion);
                    if(comparison==null||comparison<0)
                        Add(requirement,dependency,ModRequisiteStatus.WrongVersion,"Requires "+name+" "+requirement.MinimumVersion+" or newer. "+(comparison==null?"Its installed version could not be verified.":"Installed: "+dependency.Version+"."),via);
                }
                if(!dependency.Wanted)
                    Add(requirement,dependency,ModRequisiteStatus.Disabled,"Requires "+name+". "+(dependency.Enabled?"It is selected to be disabled.":"It is disabled."),via);
                else if(!dependency.Enabled)
                    Add(requirement,dependency,ModRequisiteStatus.RestartRequired,"Requires "+name+". It is selected but not loaded. "+(dependency.Content?"Apply the selected mods first.":"Restart the game to load it."),via);
                // Check each edge's version constraint before deduplicating the
                // traversal, so a shared dependency cannot hide a stronger one.
                if(!expanded.Add(requirement.Key))return;
                ModRequisite[] children;
                try { children=DeclarationsForMod(dependency,all); }
                catch(InvalidDataException ex) { issues.Add(Invalid(ex.Message));return; }
                foreach(var child in children)
                {
                    if(capped)break;
                    Visit(child,via.Length==0?name:via+" > "+name);
                }
            }
            try
            {
                foreach(var requirement in Merge(declared??Array.Empty<ModRequisite>()))
                { if(capped)break;Visit(requirement,""); }
            }
            catch(InvalidDataException ex) { issues.Add(Invalid(ex.Message)); }
            return issues.ToArray();
        }

        internal static string ValidateSelection(IEnumerable<ModEntry> inventory)
        {
            var all=inventory.ToArray();
            foreach(var item in all.Where(e=>e.Wanted))
            {
                var issue=ForMod(item,all).FirstOrDefault(e=>e.Status!=ModRequisiteStatus.RestartRequired);
                if(issue!=null)return item.Name+": "+issue.Message;
            }
            return "";
        }

        private static ModRequisite[] Merge(IEnumerable<ModRequisite> values)
        {
            var merged=new Dictionary<string,ModRequisite>(StringComparer.Ordinal);
            foreach(var value in values)
            {
                if(value==null)throw new InvalidDataException("A prerequisite declaration cannot be empty.");
                value.Validate();
                if(!merged.TryGetValue(value.Key,out var existing)) { merged.Add(value.Key,value.Copy());continue; }
                if(existing.Name.Length==0)existing.Name=value.Name;
                if(value.MinimumVersion.Length>0&&(existing.MinimumVersion.Length==0||ModReleaseVersion.Compare(value.MinimumVersion,existing.MinimumVersion)>0))existing.MinimumVersion=value.MinimumVersion;
                if(existing.Repository.Length==0) { existing.Repository=value.Repository;existing.Asset=value.Asset; }
                else if(value.Repository.Length>0&&(!existing.Repository.Equals(value.Repository,StringComparison.OrdinalIgnoreCase)||existing.Asset.Length>0&&value.Asset.Length>0&&!existing.Asset.Equals(value.Asset,StringComparison.Ordinal)))
                    throw new InvalidDataException("Conflicting download sources for prerequisite "+existing.DisplayName+".");
                else if(existing.Asset.Length==0)existing.Asset=value.Asset;
            }
            return merged.Values.ToArray();
        }

        private static ModRequisiteIssue Invalid(string message)=>new ModRequisiteIssue { Status=ModRequisiteStatus.Invalid,Message=message };
    }
}
