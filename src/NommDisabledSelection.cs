using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KellysJOINCHECK
{
    // Resolves an inventory snapshot only. Moving a whole NOMM unit does not
    // select every plugin/content identity shipped inside that unit.
    internal static class NommDisabledSelection
    {
        private const int MaximumGraph=512,MaximumInventory=4096,MaximumEdges=4096;
        internal static NommDisabledUnit[] Required(IEnumerable<ModEntry> all,IEnumerable<ModEntry> selected,IEnumerable<NommDisabledUnit> units)=>Select(all,selected,units).Units.ToArray();
        internal static ModEntry[] RequiredEntries(IEnumerable<ModEntry> all,IEnumerable<ModEntry> selected,IEnumerable<NommDisabledUnit> units)=>Select(all,selected,units).Entries.ToArray();

        // Server setup preserves unrelated local plugins. Duplicate copies of a
        // dependency-free optional plugin need no selection or physical restore.
        // Required gameplay, content owners and every prerequisite edge remain
        // strict; this never chooses, deletes or overwrites a duplicate DLL.
        internal static ModEntry[] RequiredServerEntries(IEnumerable<ModEntry> all,IEnumerable<ModEntry> selected,IEnumerable<NommDisabledUnit> units,IEnumerable<string> requiredGameplay,Action<string>? report=null)
            =>Select(all,selected,units,new HashSet<string>(requiredGameplay,StringComparer.Ordinal),report).Entries.ToArray();

        // A normal restart has no physical restore work if NOMM has no
        // disabled entries. Do not reject BepInEx's otherwise valid duplicate
        // plugin copies merely while checking an empty restore queue. If the
        // inventory claims an external item exists, keep the strict resolver.
        internal static NommDisabledUnit[] RequiredRestores(IEnumerable<ModEntry> all,IEnumerable<ModEntry> selected,IEnumerable<NommDisabledUnit> units)
        {
            if(all==null||selected==null||units==null)throw new ArgumentNullException();
            var inventory=all.Take(MaximumInventory+1).ToArray();
            var disabled=units.Take(DisabledModFiles.MaximumUnits+1).ToArray();
            if(inventory.Length>MaximumInventory||disabled.Length>DisabledModFiles.MaximumUnits||inventory.Any(e=>e==null)||disabled.Any(u=>u==null))throw new InvalidDataException("The mod selection inventory is invalid or too large to verify.");
            if(disabled.Length==0&&!inventory.Any(e=>e.ExternalDisabled))return Array.Empty<NommDisabledUnit>();
            return Required(inventory,selected,disabled);
        }

        private sealed class Selection
        {
            internal readonly List<ModEntry> Entries=new List<ModEntry>();
            internal readonly List<NommDisabledUnit> Units=new List<NommDisabledUnit>();
        }

        private static Selection Select(IEnumerable<ModEntry> inventory,IEnumerable<ModEntry> selected,IEnumerable<NommDisabledUnit> disabled,ISet<string>? requiredGameplay=null,Action<string>? report=null)
        {
            if(inventory==null||selected==null||disabled==null)throw new ArgumentNullException("A mod inventory, selection and disabled-unit snapshot are required.");
            var all=inventory.Take(MaximumInventory+1).ToArray();
            var roots=selected.Take(MaximumGraph+1).ToArray();
            var units=disabled.Take(DisabledModFiles.MaximumUnits+1).ToArray();
            if(all.Length>MaximumInventory||roots.Length>MaximumGraph||units.Length>DisabledModFiles.MaximumUnits||all.Any(e=>e==null)||roots.Any(e=>e==null)||units.Any(u=>u==null))
                throw new InvalidDataException("The mod selection inventory is invalid or too large to verify.");
            var lookup=all.GroupBy(Key,StringComparer.Ordinal).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.Ordinal);
            var pending=new Queue<ModEntry>();var queued=new HashSet<string>(StringComparer.Ordinal);
            var restored=new HashSet<NommDisabledUnit>();var result=new Selection();int edges=0;

            ModEntry Exact(ModEntry requested)
            {
                Validate(requested);
                if(!lookup.TryGetValue(Key(requested),out var matches)||matches.Length==0)
                    throw new InvalidDataException("A selected or required mod identity is missing or duplicated: "+requested.Id+".");
                if(matches.Length!=1)
                {
                    var error=new InvalidDataException("Multiple installed entries match "+Diagnostics.Clean(requested.Name)+". Check Copy report for their locations.");
                    error.Data["Mod ID"]=requested.Id;
                    error.Data["Source"]=string.Join(" | ",matches.Take(4).Select(e=>e.Path));
                    throw error;
                }
                if(!ReferenceEquals(matches[0],requested))throw new InvalidDataException("The selected mod no longer belongs to this inventory snapshot: "+requested.Id+".");
                return requested;
            }
            void Enqueue(ModEntry entry)
            {
                Exact(entry);
                if(!queued.Add(Key(entry)))return;
                if(queued.Count>MaximumGraph)throw new InvalidDataException("The selected prerequisite chain is too large to verify.");
                pending.Enqueue(entry);
            }
            NommDisabledUnit UnitFor(ModEntry entry)
            {
                DisabledModFiles.ValidateSource(entry.RestoreSource);DisabledModFiles.ValidateTarget(entry.RestoreTarget);
                var matches=units.Where(u=>u.Candidate!=null&&Same(u.Candidate.SourceRelative,entry.RestoreSource)&&Same(u.Candidate.TargetRelative,entry.RestoreTarget)).Take(2).ToArray();
                if(matches.Length!=1)throw new InvalidDataException("The selected NOMM-disabled mod has a missing or ambiguous restore source: "+entry.Id+".");
                var unit=matches[0];
                if(!string.IsNullOrEmpty(unit.Error))throw new InvalidDataException(unit.Error);
                var inspected=unit.Entries.Where(e=>e.Content==entry.Content&&e.Id==entry.Id).Take(2).ToArray();
                if(inspected.Length!=1||!MetadataMatches(inspected[0],entry))throw new InvalidDataException("The selected NOMM-disabled mod no longer matches its inspected metadata: "+entry.Id+".");
                return unit;
            }
            ModEntry InventoryRow(ModEntry inspected)
            {
                if(!lookup.TryGetValue(Key(inspected),out var matches)||matches.Length!=1||!MetadataMatches(inspected,matches[0]))
                    throw new InvalidDataException("The required NOMM parent no longer matches this inventory snapshot: "+inspected.Id+".");
                return matches[0];
            }
            void IncludeUnit(NommDisabledUnit unit)
            {
                if(!string.IsNullOrEmpty(unit.Error))throw new InvalidDataException(unit.Error);
                if(unit.Candidate==null||unit.Entries.Count==0||unit.Entries.Count>MaximumGraph)throw new InvalidDataException("A required NOMM-disabled unit has no valid inspected mod identities.");
                DisabledModFiles.ValidateSource(unit.Candidate.SourceRelative);DisabledModFiles.ValidateTarget(unit.Candidate.TargetRelative);
                if(units.Count(u=>u.Candidate!=null&&Same(u.Candidate.SourceRelative,unit.Candidate.SourceRelative)&&Same(u.Candidate.TargetRelative,unit.Candidate.TargetRelative))!=1)
                    throw new InvalidDataException("A required NOMM-disabled restore unit is duplicated.");
                if(!restored.Add(unit))return;
                result.Units.Add(unit);
                if(string.IsNullOrEmpty(unit.ParentId))return;
                if(!Text(unit.ParentId,256))throw new InvalidDataException("A required NOMM addon has an invalid parent registry ID.");
                var parents=units.Where(u=>u.RegistryId==unit.ParentId).Take(2).ToArray();
                if(parents.Length>1)throw new InvalidDataException("The selected NOMM addon's parent registry ID is duplicated: "+unit.ParentId+".");
                // No disabled parent means the catalog already resolved an
                // active parent destination. Never guess a BepInPlugin ID from
                // the NOMM registry ID or select unrelated active plugins.
                if(parents.Length==0)return;
                var parent=parents[0];IncludeUnit(parent);
                // Server planning copies external rows to select them without
                // changing menu state. Parent traversal must use those same
                // supplied inventory rows after verifying inspected metadata.
                foreach(var code in parent.Entries.Where(e=>!e.Content))Enqueue(InventoryRow(code));
            }

            var preserved=new HashSet<string>(StringComparer.Ordinal);
            foreach(var root in roots)
            {
                if(requiredGameplay!=null&&!root.Content&&!root.ExternalDisabled&&!requiredGameplay.Contains(root.Id)&&
                    lookup.TryGetValue(Key(root),out var copies)&&copies.Length>1&&
                    copies.All(e=>!e.ExternalDisabled&&ModRequisites.DeclarationsForMod(e,all).Length==0))
                {
                    Validate(root);
                    if(!copies.Any(e=>ReferenceEquals(e,root)))throw new InvalidDataException("The selected mod no longer belongs to this inventory snapshot: "+root.Id+".");
                    if(preserved.Add(root.Id))report?.Invoke("Kept unrelated duplicate plugin unchanged: "+Diagnostics.Clean(root.Name)+" ["+root.Id+"]. Locations: "+string.Join(" | ",copies.Take(4).Select(e=>e.Path)));
                    continue;
                }
                Enqueue(root);
            }
            while(pending.Count>0)
            {
                var entry=pending.Dequeue();result.Entries.Add(entry);
                if(entry.ExternalDisabled)IncludeUnit(UnitFor(entry));
                if(entry.RequiredRequisites.Count+entry.Requires.Count>MaximumGraph)throw new InvalidDataException("The selected mod declares too many prerequisites.");
                foreach(var requirement in ModRequisites.DeclarationsForMod(entry,all))
                {
                    if(++edges>MaximumEdges)throw new InvalidDataException("The selected prerequisite graph is too large to verify.");
                    requirement.Validate();
                    if(!lookup.TryGetValue(requirement.Key,out var matches)||matches.Length!=1)
                        throw new InvalidDataException("A required mod is missing or duplicated: "+requirement.DisplayName+".");
                    var dependency=matches[0];Validate(dependency);
                    // Every edge checks its version before traversal dedupes a
                    // shared dependency. A weaker path cannot hide a minimum.
                    if(requirement.MinimumVersion.Length>0)
                    {
                        int? comparison=ModReleaseVersion.Compare(dependency.Version,requirement.MinimumVersion);
                        if(comparison==null||comparison<0)throw new InvalidDataException("Requires "+requirement.DisplayName+" "+requirement.MinimumVersion+" or newer; installed "+dependency.Version+".");
                    }
                    Enqueue(dependency);
                }
            }
            return result;
        }

        private static string Key(ModEntry entry)=>(entry.Content?"bundle:":"plugin:")+entry.Id;
        private static bool Same(string a,string b)=>string.Equals(a?.Replace('\\','/'),b?.Replace('\\','/'),StringComparison.OrdinalIgnoreCase);
        private static bool MetadataMatches(ModEntry inspected,ModEntry row)=>inspected.ExternalDisabled&&row.ExternalDisabled&&
            inspected.Id==row.Id&&inspected.Version==row.Version&&inspected.Content==row.Content&&inspected.Path==row.Path&&
            inspected.WrapperPlugin==row.WrapperPlugin&&inspected.WrapperPath==row.WrapperPath&&inspected.AssemblyName==row.AssemblyName&&
            inspected.RestoreSource==row.RestoreSource&&inspected.RestoreTarget==row.RestoreTarget;
        private static void Validate(ModEntry entry)
        {
            var identity=entry.Content?new ModRequisite {Bundle=entry.Id}:new ModRequisite {Plugin=entry.Id};identity.Validate();
            if(!Text(entry.Version,128))throw new InvalidDataException("The selected or required mod has no valid installed version: "+entry.Id+".");
        }
        private static bool Text(string? value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value!.Length<=maximum&&value==value.Trim()&&!value.Any(char.IsControl);
    }
}
