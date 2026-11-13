using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KellysJOINCHECK
{
    internal sealed class SavedModVersion
    {
        internal string Label="", Version="", Backup="", Hash="", Target="", Plugin="", Id="", Repository="";
        internal bool Content;
    }
    // Uses only DOORMAN's own completed receipts and verifies the backup bytes.
    // It never scans or moves unrecognised user files.
    internal static class SavedModVersions
    {
        internal static SavedModVersion[] Read(string cache,string pluginRoot,ModEntry entry,IEnumerable<NommRecord> records)
        {
            if(entry==null||entry.ExternalDisabled||entry.Locked)return Array.Empty<SavedModVersion>();
            string plugin=entry.Content?entry.WrapperPlugin:entry.Id;
            string target=entry.Content&&entry.WrapperPath.Length>0?entry.WrapperPath:entry.Path;
            bool content=entry.Content&&entry.WrapperPath.Length==0;
            if(target.Length==0||!File.Exists(target)||(!content&&(!ModUpdateFiles.PluginIdentifierValid(plugin)||ModUpdateFiles.ProtectedPlugin(plugin))))return Array.Empty<SavedModVersion>();
            // A DLL-only restore would leave its companion bundle/textures on a
            // different version. Until the picker restores the whole package,
            // offer no partial saved versions for managed gameplay packages.
            if(ModUpdateFiles.ManagedPackagePath(pluginRoot,target))return Array.Empty<SavedModVersion>();
            string relative=content?StandaloneBundleUpdates.RelativeTarget(pluginRoot,target):ModUpdateFiles.RelativeTarget(pluginRoot,target);
            string currentHash=ModUpdateFiles.Hash(target);
            var record=NommMetadata.Match(records,content?entry.Id:plugin,"","");
            var items=new List<SavedModVersion>();
            string root=Path.Combine(cache,content?"DOORMAN-content-updates":"DOORMAN-updates");
            foreach(string job in ModUpdateFiles.Jobs(root))
            {
                string receipt=ModUpdateFiles.Within(job,"plan.json");if(!File.Exists(receipt))continue;
                try
                {
                    string oldHash,oldTarget,backup,repo="";
                    if(content)
                    {
                        var p=UpdateJson.ReadFile<StandaloneBundlePlan>(receipt);p.Validate();
                        if(p.State!="complete"||p.Id!=entry.Id)continue;
                        oldHash=p.OldHash;oldTarget=p.Target;backup=ModUpdateFiles.Within(job,"previous.nobp");
                    }
                    else
                    {
                        var p=UpdateJson.ReadFile<PendingModUpdate>(receipt);p.Validate();
                        if(p.State!="complete"||p.Plugin!=plugin)continue;
                        oldHash=p.OldHash;oldTarget=p.Target;backup=ModUpdateFiles.Within(job,"previous.bin");repo=p.Repository;
                    }
                    if(oldHash.Length==0||!oldTarget.Equals(relative,StringComparison.OrdinalIgnoreCase)||!File.Exists(backup)||!ModUpdateFiles.Hash(backup).Equals(oldHash,StringComparison.OrdinalIgnoreCase))continue;
                    if(currentHash.Equals(oldHash,StringComparison.OrdinalIgnoreCase))continue;
                    var releases=(record?.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Hash!=null&&a.Hash.Equals("sha256:"+oldHash,StringComparison.OrdinalIgnoreCase)).Select(a=>a.Version).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                    string version=releases.Length==1?releases[0]:"0.0.0";
                    items.Add(new SavedModVersion {Label=releases.Length==1?"Use "+version:"Use saved copy ("+oldHash.Substring(0,8)+")",Version=version,Backup=backup,Hash=oldHash,Target=target,Plugin=plugin,Id=entry.Id,Content=content,Repository=repo});
                }
                catch(Exception ex) when(ex is IOException||ex is UnauthorizedAccessException||ex is InvalidDataException||ex is ArgumentException) { }
            }
            return items.GroupBy(v=>v.Hash,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).Take(32).ToArray();
        }
        internal static void Stage(string cache,string pluginRoot,SavedModVersion choice)
        {
            if(choice==null||!ModUpdateFiles.Hash(choice.Backup).Equals(choice.Hash,StringComparison.OrdinalIgnoreCase))throw new IOException("That saved version changed. Refresh the version list.");
            if(choice.Content)StandaloneBundleUpdates.Stage(Path.Combine(cache,"DOORMAN-content-updates"),pluginRoot,choice.Backup,choice.Target,choice.Id,choice.Version,choice.Hash);
            else ModUpdateFiles.Stage(Path.Combine(cache,"DOORMAN-updates"),pluginRoot,choice.Target,choice.Backup,choice.Plugin,choice.Repository,choice.Version);
        }
    }
}
