using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private NommDisabledUnit[] externalUnits=Array.Empty<NommDisabledUnit>();
        // Inspect only explicit menu/setup actions after the loader has finished.
        // Cache exact file observations so reopening a page does not reload bundles.
        private void RefreshExternalUnits()
        {
            if(MenuSafety().Length>0||!loader.LoadingFinished)return;
            var units=new List<NommDisabledUnit>();
            foreach(var candidate in DisabledModFiles.Inventory(Paths.BepInExRootPath,log))
            {
                var previous=externalUnits.FirstOrDefault(u=>u.Candidate.SourceRelative==candidate.SourceRelative);
                bool same=false;
                try {same=previous!=null&&previous.Error.Length==0&&previous.ParentId.Length==0&&NommDisabledCatalog.Observe(Paths.BepInExRootPath,previous.Candidate)==previous.ObservationKey;}
                catch { }
                units.Add(same?previous!:NommDisabledCatalog.ReadUnit(Paths.BepInExRootPath,candidate,DisabledContentManifestReader.Read,log));
            }
            externalUnits=units.ToArray();
        }
        private IEnumerable<ModEntry> ExternalEntries()=>externalUnits.Where(u=>u.Error.Length==0).SelectMany(u=>u.Entries);
        private void SelectExternalDependencies(IEnumerable<ModEntry>? roots=null)
        {
            if(!entries.Any(e=>e.ExternalDisabled))return;
            var selected=(roots??entries.Where(e=>e.Wanted)).ToArray();
            var projected=RequisiteInventory();
            var keys=new HashSet<string>(selected.Select(e=>(e.Content?"bundle:":"plugin:")+e.Id),StringComparer.Ordinal);
            var needed=NommDisabledSelection.RequiredEntries(projected,projected.Where(e=>keys.Contains((e.Content?"bundle:":"plugin:")+e.Id)),externalUnits);
            foreach(var dependency in needed)
            {
                var installed=entries.Where(e=>e.Content==dependency.Content&&e.Id==dependency.Id).Take(2).ToArray();
                if(installed.Length==1)installed[0].Wanted=true;
            }
        }
        private static ModEntry ExternalCopy(ModEntry entry)
        {
            var copy=new ModEntry {Id=entry.Id,Name=entry.Name,Version=entry.Version,Path=entry.Path,AssemblyName=entry.AssemblyName,
                WrapperPlugin=entry.WrapperPlugin,WrapperPath=entry.WrapperPath,Content=entry.Content,Enabled=false,Wanted=false,
                Locked=entry.Locked,PreviewAmbiguous=entry.PreviewAmbiguous,Manifest=entry.Manifest,ExternalDisabled=true,RestoreSource=entry.RestoreSource,RestoreTarget=entry.RestoreTarget};
            copy.Requires.AddRange(entry.Requires);copy.RequiredRequisites.AddRange(entry.RequiredRequisites);return copy;
        }
        private DisabledModRestore[] PrepareExternalRestores(IEnumerable<ModEntry> all,IEnumerable<ModEntry> selected)
        {
            var units=NommDisabledSelection.RequiredRestores(all,selected,externalUnits);
            return units.Select(u=>NommDisabledCatalog.Prepare(Paths.BepInExRootPath,u)).ToArray();
        }
        private static void CheckRestoreUpdateOverlap(DisabledModRestore[] restores,PendingModUpdate[] pending)
        {
            foreach(var restore in restores)
                if(pending.Any(p=>p.Target.Replace('\\','/').Equals(restore.TargetRelative.Replace('\\','/'),StringComparison.OrdinalIgnoreCase)||p.Target.Replace('\\','/').StartsWith(restore.TargetRelative.Replace('\\','/').TrimEnd('/')+"/",StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("A queued update overlaps a NOMM restore. Apply the update or restore first, then select these mods again.");
        }
        private static string RestoredRelative(ModEntry entry)
        {
            string source=entry.Content&&entry.WrapperPath.Length>0?entry.WrapperPath:entry.Path;
            string root=Path.Combine(Paths.BepInExRootPath,"disabledPlugins",entry.RestoreSource);
            string suffix=source.Equals(root,StringComparison.OrdinalIgnoreCase)?"":source.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)?source.Substring(root.Length+1):throw new InvalidDataException("The disabled mod source is outside its verified NOMM unit.");
            string relative=entry.RestoreTarget.Replace('\\','/')+(suffix.Length==0?"":"/"+suffix.Replace('\\','/'));
            ModUpdateFiles.Within(Paths.PluginPath,relative);return relative;
        }
        private static InstalledContent ExternalContent(ModEntry entry)=>new InstalledContent {Id=entry.Id,Name=entry.Name,Version=entry.Version,Source=entry.Path,Enabled=false,Reloadable=false};
    }
}
