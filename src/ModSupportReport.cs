using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Bootstrap;
using NuclearOption.Networking.Lobbies;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private void SupportFailure(string stage,Exception error)
        {
            string message=error.GetType().Name+": "+error.Message;
            message+="\nWindows/error codes: "+RestartLaunchFailure.Evidence(error);
            // Only these local mod-source fields are attached. Never serialize
            // a server ticket, join arguments, passwords or arbitrary Data.
            foreach(string key in new[]{"Mod","Mod ID","Source","Physical file"})
                if(error.Data[key] is string value)message+="\n"+key+": "+value;
            bool serverStage=stage.StartsWith("Server",StringComparison.Ordinal)||stage.StartsWith("Restart",StringComparison.Ordinal)||stage=="Content reload"||stage=="Saved server return";
            owner.RecordSupport(stage+(!serverStage||workflowTarget==null?"":"; server: "+workflowTarget.Name),message,true);
        }
        private void WorkflowStatus(LobbyInstance target,string message,bool failure=false)
        {
            workflowMessage=message;
            owner.RecordSupport("Server setup: "+(target.LobbyNameSanitized??"Unknown server"),message,failure);
        }
        internal static string[] LoadedGrowlerUnit()
        {
            try
            {
                return ServerCompatibilityPlugins.GrowlerUnit(Chainloader.PluginInfos.Values.Where(info=>info.Instance!=null&&ServerCompatibilityPlugins.IsGrowler(info.Metadata.GUID))
                    .Select(info=>new KeyValuePair<string,string>(info.Metadata.GUID,info.Instance.GetType().Assembly.Location)));
            }
            catch{return Array.Empty<string>();}
        }
        internal void AppendSupportContext(List<string> lines,LobbyInstance? target)
        {
            lines.Add("Startup helper ready: "+StartupAvailable()+"; Blueprinter reload ready: "+loader.Ready+"; loading finished: "+loader.LoadingFinished);
            lines.Add("Busy: "+busy+"; temporary selection: "+temporarySelection+"; auto match: "+profile.AutoMatchServers+"; restart confirmed: "+restartExitConfirmed);
            lines.Add("Blueprinter status: "+loader.Unavailable);
            int detailBudget=4500;
            foreach(string detail in loader.SupportDetails)
            {
                if(detailBudget<=0){lines.Add("[additional Blueprinter patch details omitted]");break;}
                int limit=Math.Min(700,detailBudget);string value=detail.Length<=limit?detail:detail.Substring(0,limit)+" [detail truncated]";
                lines.Add("Blueprinter: "+value);detailBudget-=value.Length;
            }
            try
            {
                string selected=DiagnosticCompatibility(out _,out _),expected=ServerCompatibilityPlugins.BlueprinterOnlyWire(selected);
                if(expected.Length>0)lines.Add("Blueprinter-only expected wire (diagnostic, excludes other code plugins): "+expected+"; matches server: "+(target!=null&&expected==target.HostVersion));
                var startup=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="KellysDOORMANStartup")?.GetType("DoormanStartupPatcher");
                lines.Add("Startup selection filter ran: "+(startup?.GetField("SelectionFilterRan")?.GetValue(null) is bool ran?ran.ToString():"unavailable"));
                foreach(string field in new[]{"UnmatchedDisabledPlugins","FilteredPlugins"})
                    if(startup?.GetField(field)?.GetValue(null) is string[] ids)lines.Add("Startup "+field+": "+string.Join(", ",ids));
            }
            catch(Exception ex){lines.Add("Loader selection audit unavailable: "+ex.GetType().Name);}
            if(externalRestoreFailure.Length>0)lines.Add("NOMM startup restore failure: "+externalRestoreFailure);
            if(ui!=null)lines.Add("Mods menu status: "+ui.DiagnosticStatus);
            if(target!=null)
            {
                ServerActions(target,out var action,out _,out _,out var status);
                lines.Add("Current action: "+action+"; current message: "+(status.Length>0?status:"none"));
            }
            lines.Add("Last setup server: "+(workflowTarget?.Name??"none")+"; last setup message: "+(workflowMessage.Length>0?workflowMessage:"none"));
            lines.Add("Normal profile disabled plugins: "+string.Join(", ",profile.DisabledPlugins));
            lines.Add("Normal profile disabled content: "+string.Join(", ",profile.DisabledContent));
            if(resume!=null)
            {
                lines.Add("Startup server selection: "+resume.Target.Name+"; return pending: "+resume.ResumePending);
                lines.Add("Startup server disabled plugins: "+string.Join(", ",resume.DisabledPlugins));
                lines.Add("Startup server disabled content: "+string.Join(", ",resume.DisabledContent));
                var stillLoaded=resume.DisabledPlugins.Where(Chainloader.PluginInfos.ContainsKey).Take(32).ToArray();
                if(stillLoaded.Length>0)lines.Add("WARNING: excluded plugin IDs still loaded: "+string.Join(", ",stillLoaded));
            }
            if(target!=null)
            {
                string selected=DiagnosticCompatibility(out _,out _);
                var excluded=ServerCompatibilityPlugins.Exclusions(Chainloader.PluginInfos.Keys,target.HostVersion,Compatibility.Wire,selected,LoadedGrowlerUnit());
                if(excluded.Length>0)lines.Add("Compatibility blocker: "+ServerCompatibilityPlugins.Names(excluded)+"; verified session exclusions="+string.Join(", ",excluded));
                string growler=ServerCompatibilityPlugins.GrowlerWire(selected);
                if(growler.Length>0)lines.Add("Ifrit-remake expected wire (diagnostic): "+growler+"; matches loaded native wire: "+(growler==Compatibility.Wire));
            }
            if(Chainloader.PluginInfos.ContainsKey("com.Spiny.GrowlerFrit"))
                lines.Add("E/Kr-67 code plugins are loaded. Blueprinter's bundle list does not verify their multiplayer mode or the host's use of this pack.");
            lines.Add("Selected content versions (cached): "+loader.Content.Count(p=>p.Enabled));
            foreach(var pack in loader.Content.Where(p=>p.Enabled).OrderBy(p=>p.Id,StringComparer.Ordinal).Take(64))
                lines.Add("  Selected: "+pack.Id+" "+pack.Version+"; "+pack.Name);
            var helperLog=SupportLogTail.Read(System.IO.Path.Combine(Paths.CachePath,"DOORMAN-restart.log")).Split('\n');
            lines.Add(helperLog[0]+"; newest lines first");
            foreach(string line in helperLog.Skip(1).Reverse().Where(s=>s.Length>0).Take(48))lines.Add("  "+line);
            if(helperLog.Length>49)lines.Add("  [older helper log lines omitted]");
            // Read the tiny update receipts only on Copy report. Existing loaded
            // metadata supplies the inventory; no plugin scan, hashing or HTTP.
            try
            {
                var pending=PendingUpdates();lines.Add("Queued updates (not yet loaded): "+pending.Length);
                foreach(var update in pending.Take(64))lines.Add("  "+update.Plugin+" -> "+update.Version+"; file="+update.Target+"; new install="+update.Install+(string.IsNullOrWhiteSpace(update.Message)?"":"; status="+Diagnostics.Clean(update.Message)));
                if(pending.Length>64)lines.Add("  [remaining queued updates omitted]");
            }
            catch(Exception ex){lines.Add("Queued updates could not be read: "+ex.GetType().Name+": "+ex.Message);}
            try
            {
                var restores=DisabledRestoreQueue.Read(Paths.ConfigPath);lines.Add("Queued NOMM restores: "+restores.Length);
                foreach(var restore in restores)lines.Add("  "+restore.SourceRelative+" -> "+restore.TargetRelative+"; plugins="+string.Join(", ",restore.PluginIds));
            }
            catch(Exception ex){lines.Add("NOMM restore queue could not be read: "+ex.GetType().Name+": "+ex.Message);}
            var plugins=Chainloader.PluginInfos.Values.ToArray();lines.Add("Loaded plugins: "+plugins.Length);
            foreach(var plugin in plugins.OrderBy(p=>p.Metadata.GUID,StringComparer.Ordinal).Take(256))
                lines.Add("  "+plugin.Metadata.GUID+" "+plugin.Metadata.Version+"; "+plugin.Metadata.Name+"; file="+plugin.Location);
            if(plugins.Length>256)lines.Add("  [remaining loaded plugins omitted]");
            lines.Add("Known content (including disabled): "+loader.Content.Count);
            lines.Add("NOMM disabled packages inspected: "+externalUnits.Length);
            foreach(var unit in externalUnits)lines.Add("  NOMM: "+unit.Candidate.SourceRelative+" -> "+unit.Candidate.TargetRelative+"; registry="+unit.RegistryId+"; problem="+(unit.Error.Length>0?unit.Error:"none"));
            foreach(var pack in loader.Content.OrderByDescending(p=>p.Enabled).ThenBy(p=>p.Id,StringComparer.Ordinal).Take(256))
                lines.Add("  "+pack.Id+" "+pack.Version+"; "+pack.Name+"; enabled="+pack.Enabled+"; reloadable="+pack.Reloadable+"; source="+pack.Source+"; observed bytes="+pack.ObservedSourceLength+"; observed write ticks="+pack.ObservedSourceWriteTicks);
            if(loader.Content.Count>256)lines.Add("  [remaining content omitted]");
            if(entries.Count>0)
            {
                lines.Add("Last Mods menu inventory (cached, may predate external file changes): "+entries.Count);
                foreach(var entry in entries.Take(256))lines.Add("  "+entry.Id+" "+entry.Version+"; "+entry.Name+"; content="+entry.Content+"; enabled="+entry.Enabled+"; wanted="+entry.Wanted+"; source="+entry.Path+"; wrapper="+entry.WrapperPlugin+"; physical="+entry.WrapperPath);
                if(entries.Count>256)lines.Add("  [remaining cached entries omitted]");
            }
        }
    }
}
