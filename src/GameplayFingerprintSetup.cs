using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KellysJOINCHECK
{
    internal static class GameplayFingerprintCandidates
    {
        // One update job can replace a DLL containing several plugin GUIDs.
        // Every projected companion must use that same verified payload, not
        // the old file that happens to declare the companion on disk.
        internal static PendingModUpdate? PendingFor(ModEntry entry,IEnumerable<PendingModUpdate> pending,
            IReadOnlyDictionary<string,QueuedModRequisiteMetadata> metadata,string pluginRoot)
        {
            if(entry.Content)return ContentPendingFor(entry,pending,metadata,pluginRoot);
            string owner=entry.Id;
            QueuedModRequisiteMetadata? projected=null;
            if(metadata.TryGetValue(entry.Id,out projected))
            {
                if(projected.Error.Length>0)throw new InvalidDataException(projected.Error);
                owner=projected.OwnerPlugin;
            }
            var update=pending.SingleOrDefault(plan=>plan.Plugin==owner);
            if(update==null)return null;
            string declared=entry.Path;
            string target=ModUpdateFiles.Within(pluginRoot,update.Target);
            string actual=Path.IsPathRooted(declared)?Path.GetFullPath(declared):ModUpdateFiles.Within(pluginRoot,declared);
            var comparison=Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            if(!actual.Equals(target,comparison)||projected!=null&&!projected.Target.Equals(target,comparison))
                throw new InvalidDataException("A queued gameplay update does not own the installed source for "+entry.Id+".");
            if(projected!=null&&projected.Version!=entry.Version)
                throw new InvalidDataException("A queued gameplay companion has inconsistent projected metadata: "+entry.Id+".");
            return update;
        }

        private static PendingModUpdate? ContentPendingFor(ModEntry entry,IEnumerable<PendingModUpdate> pending,
            IReadOnlyDictionary<string,QueuedModRequisiteMetadata> metadata,string pluginRoot)
        {
            // Multi-plugin assemblies have no single WrapperPlugin, but the
            // catalog still verifies the exact DLL supplying their resource.
            // Bind that DLL to one verified replacement, never to a guessed GUID.
            if(entry.ExternalDisabled||entry.WrapperPath.Length==0)return null;
            string actual=Path.IsPathRooted(entry.WrapperPath)?Path.GetFullPath(entry.WrapperPath):ModUpdateFiles.Within(pluginRoot,entry.WrapperPath);
            var comparison=Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            var plans=pending.ToArray();
            var matches=plans.Where(plan=>ModUpdateFiles.Within(pluginRoot,plan.Target).Equals(actual,comparison)).ToArray();
            metadata.TryGetValue(entry.WrapperPlugin,out var wrapper);
            string namedOwner=wrapper?.OwnerPlugin??entry.WrapperPlugin;
            if(matches.Length==0)
            {
                if(namedOwner.Length>0&&plans.Any(plan=>plan.Plugin==namedOwner))
                    throw new InvalidDataException("A queued gameplay update does not own the installed source for "+entry.Id+".");
                return null;
            }
            if(matches.Length!=1)throw new InvalidDataException("Several queued updates claim the embedded content source for "+entry.Id+".");
            var update=matches[0];
            if(plans.Count(plan=>plan.Plugin==update.Plugin)!=1)
                throw new InvalidDataException("Several queued updates claim the embedded content owner for "+entry.Id+".");
            if(!metadata.TryGetValue(update.Plugin,out var owner)||owner.Error.Length>0||owner.Plugin!=update.Plugin
                ||owner.OwnerPlugin!=update.Plugin||!owner.Target.Equals(actual,comparison))
                throw new InvalidDataException("The queued embedded content owner could not be verified for "+entry.Id+".");
            if(entry.WrapperPlugin.Length>0&&(wrapper==null||wrapper.Error.Length>0||wrapper.Plugin!=entry.WrapperPlugin
                ||wrapper.OwnerPlugin!=update.Plugin||!wrapper.Target.Equals(actual,comparison)))
                throw new InvalidDataException("A queued gameplay update does not own the installed source for "+entry.Id+".");
            return update;
        }

        internal static KeyValuePair<string,string>[] ContentVersions(IEnumerable<ModEntry> inventory,PendingModUpdate[] pending,
            IReadOnlyDictionary<string,QueuedModRequisiteMetadata> metadata,string pluginRoot)
        {
            var bound=inventory.Where(entry=>entry.Content&&!entry.ExternalDisabled)
                .Select(entry=>new {Entry=entry,Update=PendingFor(entry,pending,metadata,pluginRoot)})
                .Where(item=>item.Update!=null).ToArray();
            if(bound.GroupBy(item=>item.Update).Any(group=>group.Count()>1))
                throw new InvalidDataException("This downloaded plugin contains several packs. Use Reload mods to restart and read their new versions first.");
            return bound.Select(item=>new KeyValuePair<string,string>(item.Entry.Id,item.Update!.Version)).ToArray();
        }
    }

    internal sealed class GameplayFingerprintSetupResult
    {
        internal bool CanSetup;
        internal string Error = "";
        internal string[] RequiredPluginIds = Array.Empty<string>();
        internal string[] ExcludedPluginIds = Array.Empty<string>();
        internal bool NeedsRestart;
    }

    // Pure selection preflight. These records cover the audited gameplay scope,
    // not every installed plugin. File inspection and active-code observations
    // belong to the caller's bounded background inventory, never this helper.
    internal static class GameplayFingerprintSetup
    {
        // If the game and the server already agree on the loaded set, setup
        // has nothing to change. In particular, do not make unrelated client
        // utilities part of a selection plan that will never be applied.
        internal static bool AlreadyReady(string serverWire,string localWire,GameplayFingerprintSnapshot? remote,
            GameplayFingerprintSnapshot? loaded,bool pendingWork,bool loadingFinished)
        {
            if(!loadingFinished||pendingWork||string.IsNullOrEmpty(serverWire)||serverWire!=localWire)return false;
            return GameplayFingerprintComparison.Compare(remote,loaded).Verified;
        }

        internal static GameplayFingerprintSetupResult Prepare(GameplayFingerprintSnapshot? remote,
            GameplayFingerprintSnapshot? available, GameplayFingerprintSnapshot? loaded)
        {
            var result = new GameplayFingerprintSetupResult();
            if (!Ready(remote, "The server", out var wanted, out result.Error)
                || !Ready(available, "Your installed files", out var installed, out result.Error)
                || !Ready(loaded, "Your loaded files", out var active, out result.Error)) return result;
            if (installed.ContentSetHash.Length > 0 || active.ContentSetHash.Length > 0)
            {
                result.Error = "Installed and loaded inventories need each full file fingerprint before preparing setup.";
                return result;
            }

            var availableFiles = installed.Records.ToDictionary(Key, StringComparer.Ordinal);
            var activeFiles = active.Records.ToDictionary(Key, StringComparer.Ordinal);
            var requiredFiles = wanted.Records.ToList();
            foreach (var required in wanted.Records)
            {
                if (!availableFiles.TryGetValue(Key(required), out var actual))
                {
                    result.Error = "Install the server's exact file for " + required.Id + " " + required.Version + " first.";
                    return result;
                }
                if (!Matches(required, actual))
                {
                    result.Error = "The installed file for " + required.Id + " differs from the server's exact file. Setup will not substitute the latest release.";
                    return result;
                }
            }
            if (wanted.ContentSetHash.Length > 0)
            {
                var selectedInstalled = new List<GameplayFileRecord>();
                foreach (var required in GameplayFingerprintPolicy.SelectedContent(wanted))
                {
                    if (!availableFiles.TryGetValue("content\n" + required.Id, out var actual)
                        || actual.Version != required.Version)
                    {
                        result.Error = "Install the server's exact content for " + required.Id + " " + required.Version + " first.";
                        return result;
                    }
                    selectedInstalled.Add(actual);
                }
                if (GameplayFingerprintPolicy.ContentHash(selectedInstalled) != wanted.ContentSetHash)
                {
                    result.Error = "Your selected content files differ from the server's exact set. Get the host's exact files; setup will not substitute the latest release.";
                    return result;
                }
                requiredFiles.AddRange(selectedInstalled);
            }

            var requiredPlugins = new HashSet<string>(wanted.Records.Where(r => r.Kind == "plugin")
                .Select(r => r.Id), StringComparer.Ordinal);
            result.RequiredPluginIds = requiredPlugins.OrderBy(id => id, StringComparer.Ordinal).ToArray();
            result.ExcludedPluginIds = installed.Records.Concat(active.Records)
                .Where(r => r.Kind == "plugin" && GameplayFingerprintPolicy.IsAuditedPlugin(r.Id)
                    && !requiredPlugins.Contains(r.Id))
                .Select(r => r.Id).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();

            foreach (var required in requiredFiles)
            {
                bool found = activeFiles.TryGetValue(Key(required), out var actual);
                // Missing code cannot be loaded safely by a content reload.
                // A missing content record can still use the ordinary loader's
                // reload decision. Different active bytes always need restart.
                if (!found && required.Kind == "plugin" || found && !Matches(required, actual!))
                    result.NeedsRestart = true;
            }
            if (active.Records.Any(r => r.Kind == "plugin" && !requiredPlugins.Contains(r.Id)))
                result.NeedsRestart = true;
            result.CanSetup = true;
            return result;
        }

        internal static string ValidateResolved(IEnumerable<string>? requiredPluginIds,
            IEnumerable<string>? excludedPluginIds, IEnumerable<string>? finalDisabledPluginIds)
        {
            if (requiredPluginIds == null || excludedPluginIds == null || finalDisabledPluginIds == null)
                return "The resolved gameplay plugin selection is unavailable.";
            var required = requiredPluginIds.Take(GameplayFingerprintPolicy.MaxRecords + 1).ToArray();
            var excluded = excludedPluginIds.Take(GameplayFingerprintPolicy.MaxRecords + 1).ToArray();
            var disabled = finalDisabledPluginIds.Take(513).ToArray();
            if (required.Length > GameplayFingerprintPolicy.MaxRecords || excluded.Length > GameplayFingerprintPolicy.MaxRecords
                || disabled.Length > 512 || required.Concat(excluded).Any(id => !GameplayFingerprintPolicy.IsAuditedPlugin(id))
                || disabled.Any(id => !GameplayFingerprintPolicy.Text(id, GameplayFingerprintPolicy.MaxId)))
                return "The resolved gameplay plugin selection is invalid or too large.";
            var disabledSet = new HashSet<string>(disabled, StringComparer.Ordinal);
            var excludedSet = new HashSet<string>(excluded, StringComparer.Ordinal);
            foreach (string id in required)
                if (disabledSet.Contains(id) || excludedSet.Contains(id))
                    return "A required server gameplay plugin would stay disabled: " + id + ". Resolve its prerequisites first.";
            foreach (string id in excluded)
                if (!disabledSet.Contains(id))
                    return "An enabled mod requires a gameplay plugin this server excludes: " + id + ". Resolve that prerequisite first.";
            return "";
        }

        private static bool Ready(GameplayFingerprintSnapshot? source, string owner,
            out GameplayFingerprintSnapshot snapshot, out string error)
        {
            if (!GameplayFingerprintPolicy.Normalize(source, out snapshot, out error))
            { error = owner + ": " + error; return false; }
            if (!snapshot.Ready || !snapshot.Complete)
            {
                error = owner + ": " + (snapshot.Error.Length > 0 ? snapshot.Error : "Exact gameplay files are not ready or complete.");
                return false;
            }
            error = "";
            return true;
        }

        private static string Key(GameplayFileRecord record) => record.Kind + "\n" + record.Id;
        private static bool Matches(GameplayFileRecord required, GameplayFileRecord actual)
            => required.Version == actual.Version && required.Hash == actual.Hash;
    }
}
