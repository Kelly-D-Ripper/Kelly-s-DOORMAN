using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private ServerTarget? armedRestart,workflowTarget;
        private string workflowMessage="";
        private string armedDownloadsHeader="";
        private ServerSetupPlan? reportedPlan;
        private System.Threading.CancellationTokenSource? serverDownloadCancel;
        private int setupGeneration;
        private string? restartHandshake;
        private bool restartExitConfirmed;
        private bool ownsPendingRestart;
        internal void CancelServerSetup(){setupGeneration++;serverDownloadCancel?.Cancel();if(!restartExitConfirmed)CancelMapBatch();armedRestart=null;armedDownloadsHeader="";reportedPlan=null;workflowMessage="";CancelRestartHandshake();CancelOwnedRestart();}
        private void CancelOwnedRestart(){if(ownsPendingRestart&&!restartExitConfirmed)ClearPending();}
        private void CancelRestartHandshake()
        {if(restartExitConfirmed||restartHandshake==null)return;try{File.WriteAllText(restartHandshake+".cancel","cancel");File.Delete(restartHandshake);}catch{}}
        private string BeginRestartHandshake()
        {CancelRestartHandshake();restartExitConfirmed=false;return restartHandshake=Path.Combine(Paths.CachePath,"doorman-"+Guid.NewGuid().ToString("N")+".ready");}
        private static ServerTarget Snapshot(LobbyInstance target,string expanded)
        {
            string ip=target.UdpAddress??"",port=target.UdpPort??"";
            if(target is ServerLobbyInstance server&&server.details!=null)
            { uint address=server.details.m_NetAdr.GetIP();ushort value=server.details.m_NetAdr.GetConnectionPort();ip=address!=0&&value>0?ServerTarget.Address(address):"";port=ip.Length==0?"":value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return new ServerTarget { Name=target.LobbyNameSanitized??"",Host=target.HostAddress??"",Ip=ip,Port=port,SteamId=target.LobbyId.m_SteamID,Dedicated=target.DedicatedServer,Wire=target.HostVersion,Expanded=expanded };
        }
        private static bool Same(ServerTarget? saved,LobbyInstance target)
        { if(saved==null)return false;var actual=Snapshot(target,"");return saved.Matches(actual.Dedicated,actual.SteamId,actual.Ip,actual.Port)&&saved.Wire==actual.Wire; }
        internal bool CanRestartServerManually(LobbyInstance target)
            =>!busy&&!restartExitConfirmed&&Same(armedRestart,target)&&StartupAvailable();
        internal void RestartServerManually(LobbyInstance target,string expanded)
        {
            if(!CanRestartServerManually(target))return;
            if(Same(armedRestart,target)&&(armedDownloadsHeader.StartsWith("nosmr|",StringComparison.Ordinal)||armedDownloadsHeader.StartsWith("map|",StringComparison.Ordinal))){RestartDownloadedForServer(target,expanded,true);return;}
            if(owner.RequiresGameplayFiles(target)){BeginGameplaySetup(target,expanded,false,"",true,true);return;}
            try{RestartForServer(target,expanded,true);}
            catch(Exception ex){SupportFailure("Manual server restart",ex);busy=false;ClearPending();Message(target,"Could not prepare manual restart: "+ex.Message,true);}
        }
        private PendingModUpdate[] PendingUpdates()=>ModUpdateFiles.ReadPending(Path.Combine(Paths.CachePath,"DOORMAN-updates"),Paths.PluginPath);
        private ServerSetupPlan Plan(string expanded,string serverWire,LobbyInstance? target=null)
        {
            var installed=ModCatalog.Read(loader.Content,profile,log);
            RefreshExternalUnits();installed.AddRange(ExternalEntries().Select(ExternalCopy));
            string bp=installed.FirstOrDefault(e=>!e.Content&&e.Id=="com.nikkorap.blueprinter")?.Version??"";
            var pending=PendingUpdates();
            var metadata=QueuedModRequisiteMetadata.Read(pending,Paths.PluginPath);
            var versions=GameplayFingerprintCandidates.ContentVersions(installed,pending,metadata,Paths.PluginPath);
            var available=loader.Content.Concat(installed.Where(e=>e.ExternalDisabled&&e.Content).Select(ExternalContent));
            var result=ServerSetupPlan.Create(expanded,Compatibility.Expanded(),available,bp,Chainloader.PluginInfos.ContainsKey("com.nikkorap.blueprinter"),loader.Ready,versions,pending.Length>0);result.Updates=pending;
            if(result.CanSetup)
            {
                var exact=target!=null&&owner.RequiresGameplayFiles(target)?GameplayPreparation(target):null;
                if(target!=null&&owner.RequiresGameplayFiles(target)&&(exact==null||!exact.CanSetup))
                {result.CanSetup=false;result.Reason="Press Setup mods to verify exact installed gameplay files first.";return result;}
                var metadataError=metadata.Values.FirstOrDefault(m=>m.Error.Length>0)?.Error;
                if(metadataError!=null){result.CanSetup=false;result.Reason=metadataError+" Open Mods to resolve its prerequisites first.";return result;}
                var prepared=ModRequisiteSelection.WithPending(installed,pending,metadata,Paths.PluginPath);
                var wrappers=installed.Where(e=>e.Content&&e.WrapperPlugin.Length>0).ToArray();
                var hard=prepared.Where(e=>!e.Content).Select(e=>new KeyValuePair<string,string[]>(e.Id,
                    ModRequisites.DeclarationsForMod(e,prepared).Concat(prepared.Where(w=>w.Content&&w.WrapperPlugin==e.Id&&!result.DisabledContent.Contains(w.Id,StringComparer.Ordinal)).SelectMany(w=>ModRequisites.DeclarationsForMod(w,prepared)))
                    .Where(r=>!r.Content&&r.Plugin!=e.Id).Select(r=>r.Plugin).Distinct(StringComparer.Ordinal).ToArray())).ToArray();
                var wireExcluded=ServerCompatibilityPlugins.Exclusions(prepared.Where(e=>!e.Content).Select(e=>e.Id),serverWire,Compatibility.Wire,DiagnosticCompatibility(out _,out _),LoadedGrowlerUnit());
                var exactExcluded=exact?.ExcludedPluginIds??Array.Empty<string>();
                var exactRequired=exact?.RequiredPluginIds??Array.Empty<string>();
                var normalDisabled=profile.DisabledPlugins.Concat(installed.Where(e=>e.ExternalDisabled&&!e.Content).Select(e=>e.Id)).Except(pending.Where(p=>p.Install).Select(p=>p.Plugin),StringComparer.Ordinal).Concat(wireExcluded).Concat(exactExcluded).ToArray();
                result.DisabledPlugins=ServerPluginSelection.Resolve(hard,normalDisabled,wrappers.Select(e=>e.WrapperPlugin),wrappers.Where(e=>!result.DisabledContent.Contains(e.Id,StringComparer.Ordinal)).Select(e=>e.WrapperPlugin),result.Vanilla,exactRequired);
                foreach(var entry in prepared)entry.Wanted=entry.Content?!result.DisabledContent.Contains(entry.Id,StringComparer.Ordinal):!result.DisabledPlugins.Contains(entry.Id,StringComparer.Ordinal);
                var requiredEntries=NommDisabledSelection.RequiredServerEntries(prepared,prepared.Where(e=>e.Wanted),externalUnits,exactRequired,
                    note=>owner.RecordSupport("Server setup: preserved local plugins",note));
                foreach(var needed in requiredEntries)
                {
                    if(needed.Content&&result.DisabledContent.Contains(needed.Id,StringComparer.Ordinal))throw new InvalidDataException("The server's advertised set excludes a required content prerequisite: "+needed.Name);
                    needed.Wanted=true;
                }
                result.DisabledPlugins=result.DisabledPlugins.Except(requiredEntries.Where(e=>!e.Content).Select(e=>e.Id),StringComparer.Ordinal).ToArray();
                string exactSelection=GameplayFingerprintSetup.ValidateResolved(exactRequired,exactExcluded,result.DisabledPlugins);
                if(exactSelection.Length>0){result.CanSetup=false;result.Reason=exactSelection;return result;}
                if(exact?.NeedsRestart==true)
                {result.NeedsRestart=true;result.Reason="Exact gameplay DLL changes need a restart. Press Proceed to load the server's matching installed files and return here. Your normal mod list is preserved.";}
                if(wireExcluded.Any(id=>!result.DisabledPlugins.Contains(id,StringComparer.Ordinal)))
                {result.CanSetup=false;result.Reason="An enabled mod requires "+ServerCompatibilityPlugins.Names(wireExcluded)+", but this server's verified compatibility excludes it. Resolve that prerequisite in Mods first.";return result;}
                if(wireExcluded.Any(Chainloader.PluginInfos.ContainsKey))
                {
                    result.NeedsRestart=true;
                    result.Reason=ServerCompatibilityPlugins.Names(wireExcluded)+" changes multiplayer compatibility. Press Proceed to restart with its components disabled for this server session. Your normal mod list is preserved.";
                }
                string prerequisite=ModRequisites.ValidateSelection(prepared);
                if(prerequisite.Length>0){result.CanSetup=false;result.Reason=prerequisite+" Open Mods to resolve its prerequisites first.";return result;}
                result.Restores=PrepareExternalRestores(prepared,requiredEntries);
                if(result.Restores.Length>0)
                {
                    result.NeedsRestart=true;
                    result.Reason="Found "+result.Restores.Length+" required NOMM-disabled package(s). Proceed will enable them and restart. To disable them again, close the game and use NOMM.";
                    CheckRestoreUpdateOverlap(result.Restores,pending);
                }
                var dependencies=installed.Where(e=>!e.Content&&result.DisabledPlugins.Contains(e.Id,StringComparer.Ordinal)&&!profile.DisabledPlugins.Contains(e.Id,StringComparer.Ordinal)&&e.Id!="com.nikkorap.blueprinter"&&!wireExcluded.Contains(e.Id,StringComparer.Ordinal)&&!wrappers.Any(w=>w.WrapperPlugin==e.Id)).Select(e=>Diagnostics.Clean(e.Name)).Distinct().Take(5).ToArray();
                if(dependencies.Length>0)result.Reason+=" Also disabled for this session because they require excluded plugins: "+string.Join(", ",dependencies)+". Unrelated client plugins stay enabled.";
                if(result.NeedsRestart)result.Reason+=" After playing, restart the game to load your usual plugin selection.";
            }
            return result;
        }
        internal void ServerActions(LobbyInstance target,out string label,out bool setup,out bool join,out string message)
        {
            label=Same(armedRestart,target)?(RuntimePlatform.IsWine?"Restart manually":"Proceed"):"Setup mods";setup=!busy;join=!busy&&owner.MapIssueFor(target)==null&&MenuSafety().Length==0&&(target.HostVersion==Compatibility.Wire&&owner.GameplayAllowsJoin(target)||ReportedJoinAvailable(target));
            message=Same(workflowTarget,target)?workflowMessage:"";
            if(Same(armedRestart,target)&&RuntimePlatform.IsWine)
                message=message.Replace("Press Proceed","Choose Restart manually").Replace("Proceed will","Restarting manually will")+"\nClose the game, then reopen it through Steam.";
        }
        private bool ReportedJoinAvailable(LobbyInstance target)
        {
            try
            {
                if(owner.NosmrProtocolFor(target)!="2")return false;
                var index=NosmrIndex();var host=index==null?null:owner.NosmrFor(target,index);
                bool pending=PendingUpdates().Length>0||StandaloneBundleUpdates.Pending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath).Length>0;
                return NosmrReport.CanAttemptJoin(owner.RequiresGameplayFiles(target),host?.Verified==true,busy,Same(armedRestart,target),pending,target.HostVersion,Compatibility.Wire);
            }
            catch(Exception ex){log("Reported join check unavailable: "+ex.Message);return false;}
        }
        internal bool TryReportedJoin(LobbyInstance target)
        {
            if(MenuSafety().Length>0||!ReportedJoinAvailable(target))return false;
            owner.HideServerWorkflow();retrying=true;
            try{SteamLobby.instance.TryJoinLobby(target,"",true);return true;}
            catch(Exception ex){SupportFailure("Reported server join",ex);Message(target,"Could not try joining: "+Diagnostics.Clean(ex.Message),true);return true;}
            finally{retrying=false;}
        }
        internal void SetupServer(LobbyInstance target,string expanded,bool joinAfter=false,string password="",bool prompt=true)
        {
            if(RuntimePlatform.IsWine&&CanRestartServerManually(target)){RestartServerManually(target,expanded);return;}
            if(Same(armedRestart,target)&&armedDownloadsHeader.StartsWith("map|",StringComparison.Ordinal)){RestartDownloadedForServer(target,expanded,false);return;}
            var map=owner.MapIssueFor(target);
            if(map!=null){BeginMapSetup(target,expanded,map);return;}
            if(owner.RequiresGameplayFiles(target)){BeginGameplaySetup(target,expanded,joinAfter,password,prompt);return;}
            if(!Signature.Parse(expanded).Complete&&owner.NosmrProtocolFor(target)=="2"){BeginReportedSetup(target,expanded);return;}
            try { SetupServerCore(target,expanded,joinAfter,password,prompt); }
            catch(Exception ex) { busy=false;armedRestart=null;SupportFailure("Server setup",ex);log("Server setup failed: "+ex.Message);Message(target,"Could not set up mods: "+ex.Message); }
        }
        private void SetupServerCore(LobbyInstance target,string expanded,bool joinAfter,string password,bool prompt)
        {
            if(busy)return;
            if(Same(armedRestart,target)){RestartForServer(target,expanded);return;}
            setupGeneration++;workflowTarget=Snapshot(target,expanded);armedRestart=null;
            string reason=MenuSafety();if(reason.Length==0&&!loader.LoadingFinished)reason="Wait for Blueprinter to finish loading, then press Setup mods.";
            if(reason.Length>0){Message(target,reason);return;}
            if(!Signature.Parse(expanded).Complete&&owner.NosmrProtocolFor(target)=="2")
            {Message(target,"This server provides a reported modpack. Use Download mod on each item. Automatic setup for this list is not available yet; the host's installed files cannot be verified.",true);return;}
            var plan=Plan(expanded,target.HostVersion,target);
            if(!plan.CanSetup){Message(target,plan.Reason,true);return;}
            if(plan.NeedsRestart)
            {
                if(!StartupAvailable()){Message(target,"Install the FULL client package, including its startup helper, before using restart setup.",true);return;}
                if(!plan.Vanilla&&Chainloader.PluginInfos.ContainsKey("com.nikkorap.blueprinter")&&!loader.StartupSupported){Message(target,loader.Unavailable,true);return;}
                armedRestart=Snapshot(target,expanded);Message(target,plan.Reason);return;
            }
            busy=true;Message(target,"Setting up this server's mods...");owner.StartCoroutine(SetupAfterFrame(target,expanded,plan,joinAfter,password,prompt,setupGeneration));
        }
        private static bool StartupAvailable()=>AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="KellysDOORMANStartup").Any(a=>a.GetType("DoormanStartupPatcher")?.GetField("Ready")?.GetValue(null) is bool ready&&ready);
        private void Message(LobbyInstance target,string message,bool failure=false)
        {
            WorkflowStatus(target,message,failure);owner.ShowServerWorkflow(target);owner.RefreshServerActions();
        }
        private IEnumerator SetupAfterFrame(LobbyInstance target,string expanded,ServerSetupPlan plan,bool joinAfter,string password,bool prompt,int generation)
        {
            yield return null;
            var previous=loader.Content.Where(c=>!c.Enabled).Select(c=>c.Id).ToArray();
            bool applied=false;
            try
            {
                if(generation!=setupGeneration||!Same(workflowTarget,target))yield break;
                string error=MenuSafety();
                if(error.Length==0&&loader.Content.Any(c=>c.Enabled==plan.DisabledContent.Contains(c.Id,StringComparer.Ordinal)))
                { error=loader.Apply(plan.DisabledContent);applied=error.Length==0; }
                if(applied)owner.RefreshGameplayFiles();
                if(error.Length>0){armedRestart=Snapshot(target,expanded);owner.RecordSupport("Content reload",error,true);WorkflowStatus(target,"Reload could not apply this safely. Press Proceed to restart and return to this server.");yield break;}
                if(target.HostVersion!=Compatibility.Wire)
                {
                    if(applied)loader.Apply(previous);
                    armedRestart=Snapshot(target,expanded);WorkflowStatus(target,"The live mod signature still differs. Press Proceed to try a clean restart with this set.",true);yield break;
                }
                // A repeated setup may already match. It must not forget the
                // earlier temporary reload that still needs normal restoration.
                temporarySelection|=applied;WorkflowStatus(target,"Mods ready. Click Join server.");
                if(!owner.GameplayAllowsJoin(target)){WorkflowStatus(target,"Content selected. Wait for the exact gameplay file check to finish before joining.");yield break;}
                if(joinAfter)
                { owner.HideServerWorkflow();retrying=true;try{SteamLobby.instance.TryJoinLobby(target,password,prompt);}finally{retrying=false;} }
            }
            catch(Exception ex){SupportFailure("Content reload",ex);if(applied)loader.Apply(previous);WorkflowStatus(target,"Could not set up mods: "+ex.Message);log(workflowMessage);}
            finally{busy=false;owner.RefreshServerActions();}
        }
        internal bool BeforeJoin(SteamLobby steam,LobbyInstance target,string password,bool prompt,string expanded)
        {
            if(!owner.GameplayAllowsJoin(target))
            {if(!retrying&&profile.AutoMatchServers)SetupServer(target,expanded,true,password,prompt);else Message(target,"Exact gameplay files differ or are not verified. Use Setup mods before joining.",true);return false;}
            if(retrying||!profile.AutoMatchServers||target.HostVersion==Compatibility.Wire)return true;
            SetupServer(target,expanded,true,password,prompt);return false;
        }
        private ServerSetupTicket Ticket(LobbyInstance target,string expanded,ServerSetupPlan plan)
        {
            var catalog=ModCatalog.Read(loader.Content,profile,log);
            catalog.AddRange(ExternalEntries().Select(ExternalCopy).Where(e=>plan.Restores.Any(r=>r.SourceRelative==e.RestoreSource)));
            var sources=ModCatalog.LoadedSourceAssemblies(log);
            return new ServerSetupTicket { Target=Snapshot(target,expanded),DisabledContent=plan.DisabledContent,DisabledPlugins=plan.DisabledPlugins,Restores=plan.Restores,Inventory=catalog.Where(c=>c.Content).Select(c=>TicketContent(c,plan,sources)).ToArray() };
        }
        private ContentRecord TicketContent(ModEntry entry,ServerSetupPlan plan,LoadedContentAssembly[] sources)
        {
            if(entry.ExternalDisabled)
            {
                string relativeFile=RestoredRelative(entry);
                return new ContentRecord {Id=entry.Id,Name=entry.Name,Version=entry.Version,RelativeFile=relativeFile,Source=entry.Path.StartsWith("resource:",StringComparison.Ordinal)?entry.Path:ModUpdateFiles.Within(Paths.PluginPath,relativeFile)};
            }
            string physical=entry.WrapperPath;
            if(physical.Length==0&&entry.Path.StartsWith("resource:",StringComparison.Ordinal))
            {
                var saved=resume?.Inventory.FirstOrDefault(c=>c.Id==entry.Id&&c.Version==entry.Version&&c.Source==entry.Path&&ModUpdateFiles.HashValid(c.Digest));
                if(saved!=null)physical=ModUpdateFiles.Within(Paths.PluginPath,saved.RelativeFile);
            }
            string relative;
            try { relative=ServerSetupFiles.RelativeContent(Paths.PluginPath,ContentSourceFiles.Resolve(Paths.PluginPath,entry.Path,physical,sources)); }
            catch(Exception ex)
            {
                log("Restart source rejected for '"+Diagnostics.Clean(entry.Name)+"' ["+Diagnostics.Clean(entry.Id)+"], source="+entry.Path+", physical="+physical+": "+ex.Message);
                var failure=new InvalidDataException("Cannot verify '"+Diagnostics.Clean(entry.Name)+"': "+ex.Message,ex);
                failure.Data["Mod"]=entry.Name;failure.Data["Mod ID"]=entry.Id;failure.Data["Source"]=entry.Path;failure.Data["Physical file"]=physical;
                throw failure;
            }
            var observed=loader.Content.FirstOrDefault(c=>c.Id==entry.Id);
            var cached=resume?.Inventory.FirstOrDefault(c=>c.Id==entry.Id&&c.Version==entry.Version&&c.RelativeFile.Equals(relative,StringComparison.OrdinalIgnoreCase));
            return new ContentRecord { Id=entry.Id,Name=entry.Name,Version=plan.NextVersions.TryGetValue(entry.Id,out var version)?version:entry.Version,Source=entry.Path,RelativeFile=relative,Digest=cached?.Digest??"",ObservedSourceLength=observed?.ObservedSourceLength??-1,ObservedSourceWriteTicks=observed?.ObservedSourceWriteTicks??-1 };
        }
        private static void HashInventory(ServerSetupTicket ticket,string pluginRoot,bool verify,PendingModUpdate[]? pending=null)
        {
            var hashes=new Dictionary<string,(string Digest,long Length,long WriteTicks)>(StringComparer.OrdinalIgnoreCase);
            foreach(var record in ticket.Inventory)
            {
                try
                {
                string? external=!verify?DisabledRestoreQueue.SourceFor(Path.GetDirectoryName(pluginRoot)!,ticket.Restores,record.RelativeFile):null;
                string path=external??ModUpdateFiles.Within(pluginRoot,record.RelativeFile);
                if(!hashes.TryGetValue(path,out var source))
                {
                    try
                    {
                        var file=new FileInfo(path);long length=file.Length,ticks=file.LastWriteTimeUtc.Ticks;
                        InstalledFilePolicy.ValidateLength(length);
                        string digest;using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.SequentialScan))digest=InstalledFilePolicy.Hash(stream);
                        file.Refresh();if(!file.Exists||file.Length!=length||file.LastWriteTimeUtc.Ticks!=ticks)throw new IOException("An installed content file changed during verification.");
                        source=(digest,length,ticks);hashes[path]=source;
                    }
                    catch when(verify){record.Version="";continue;}
                }
                if(!verify&&external==null)ServerSetupFiles.VerifySourceObservation(record,source.Length,source.WriteTicks,source.Digest,pending?.SingleOrDefault(p=>p.Target.Equals(record.RelativeFile,StringComparison.OrdinalIgnoreCase)));
                if(verify&&!source.Digest.Equals(record.Digest,StringComparison.OrdinalIgnoreCase))record.Version="";else record.Digest=source.Digest;
                }
                catch(Exception ex)when(!verify)
                {
                    var failure=new InvalidDataException("Cannot verify '"+Diagnostics.Clean(record.Name)+"': "+ex.Message,ex);
                    failure.Data["Mod"]=record.Name;failure.Data["Mod ID"]=record.Id;failure.Data["Source"]=record.Source;failure.Data["Physical file"]=record.RelativeFile;
                    throw failure;
                }
            }
        }
        private void RestartForServer(LobbyInstance target,string expanded,bool manual=false)
        {
            // An earlier Explorer dispatch can remain behind a Windows prompt.
            // Cancel its receipt before staging a separately confirmed manual exit.
            if(manual)CancelRestartHandshake();
            string reason=MenuSafety();if(reason.Length>0){Message(target,reason);return;}
            var plan=Plan(expanded,target.HostVersion,target);if(!plan.CanSetup){armedRestart=null;Message(target,plan.Reason,true);return;}
            if(!StartupAvailable()){Message(target,"The startup helper is unavailable. Reinstall the full client package.",true);return;}
            if(!plan.Vanilla&&Chainloader.PluginInfos.ContainsKey("com.nikkorap.blueprinter")&&!loader.StartupSupported){Message(target,loader.Unavailable,true);return;}
            string helper=Path.Combine(Paths.BepInExRootPath,"DOORMAN","KellysDOORMANRestart.exe");
            if(!manual&&(!RuntimePlatform.CanUseWindowsRestart||!File.Exists(helper))){Message(target,"Automatic restart is unavailable. Choose Restart manually, then reopen the game through Steam.",true);return;}
            try
            {
                var ticket=Ticket(target,expanded,plan);ticket.Target.Validate();busy=true;Message(target,"Checking installed files and downloaded updates before restarting...");owner.StartCoroutine(PrepareRestart(target,ticket,helper,setupGeneration,plan.Updates,manual));
            }
            catch(Exception ex){SupportFailure("Restart inventory",ex);ClearPending();log("Server restart blocked: "+ex.Message);Message(target,"Could not restart: "+ex.Message);}
        }
        private IEnumerator PrepareRestart(LobbyInstance target,ServerSetupTicket ticket,string helper,int generation,PendingModUpdate[] pending,bool manual=false)
        {
            bool mapDownload=armedDownloadsHeader.StartsWith("map|",StringComparison.Ordinal);
            Action? verifyGameplay=null;Exception? preflightFailure=null;
            try{if(!ticket.DownloadsOnly)verifyGameplay=GameplayRestartVerifier(target);}catch(Exception ex){preflightFailure=ex;}
            if(preflightFailure!=null){SupportFailure("Restart exact file check",preflightFailure);busy=false;ClearPending();Message(target,"Could not restart: "+preflightFailure.Message);yield break;}
            var task=Task.Run(()=>
            {
                DisabledModFiles.Verify(Paths.BepInExRootPath,ticket.Restores);ModUpdateFiles.VerifyPending(pending,Paths.PluginPath);HashInventory(ticket,Paths.PluginPath,false,pending);
                if(ticket.DownloadsOnly)StandaloneBundleUpdates.VerifyPending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath);
                if(mapDownload){ServerMapUpdates.VerifyPending(Paths.CachePath,Paths.PluginPath);IronjawPackageUpdates.VerifyPending(Paths.CachePath,Paths.PluginPath);IronjawPackageUpdates.CheckQueueConflicts(Paths.CachePath,Paths.PluginPath,pending);}
                foreach(var record in ticket.Inventory)
                { var update=pending.SingleOrDefault(p=>p.Target.Equals(record.RelativeFile,StringComparison.OrdinalIgnoreCase));if(update!=null)record.Digest=update.NewHash; }
                verifyGameplay?.Invoke();
            },updateLifetime.Token);
            while(!task.IsCompleted)yield return null;
            try
            {
                if(generation!=setupGeneration||!Same(ticket.Target,target)){busy=false;yield break;}
                if(task.IsFaulted)throw task.Exception!.GetBaseException();if(task.IsCanceled)throw new IOException("Restart verification was cancelled.");
                if(MenuSafety().Length>0)throw new InvalidOperationException(MenuSafety());
                if(ticket.DownloadsOnly&&!DownloadHeaderMatches(target))throw new IOException("The server's information changed. Press Setup mods again.");
                if(verifyGameplay!=null&&GameplayPreparation(target)==null)throw new IOException("The server's exact files changed during restart verification. Press Setup mods again.");
                ServerSetupFiles.Save(Paths.ConfigPath,ticket);
                ownsPendingRestart=true;
                DisabledRestoreQueue.Save(Paths.ConfigPath,ticket.Restores);
                if(manual)
                {
                    if(mapDownload)CommitMapBatch();
                    Message(target,"Setup saved. Nuclear Option will close. Start it again through Steam once Steam shows Play. This server page will reopen after loading.");
                    owner.RecordSupport("Manual server restart","Verified setup saved; the player will reopen Nuclear Option through Steam.");
                    restartExitConfirmed=true;Application.Quit();yield break;
                }
                string ready=BeginRestartHandshake();var launch=Task.Run(()=>LaunchRestartHelper(helper,ready));
                Message(target,"Preparing the Windows restart helper...");owner.StartCoroutine(AwaitServerRestartLaunch(launch,ready,ticket,target,generation));
            }
            catch(Exception ex){SupportFailure("Restart verification",ex);busy=false;ClearPending();Message(target,"Could not restart: "+ex.Message);}
        }
        private IEnumerator AwaitServerRestartLaunch(Task<string> launch,string ready,ServerSetupTicket ticket,LobbyInstance target,int generation)
        {
            float deadline=Time.unscaledTime+15;
            while(!launch.IsCompleted&&generation==setupGeneration&&Time.unscaledTime<deadline)yield return null;
            if(!launch.IsCompleted)
            {
                CancelRestartHandshake();
                _=launch.ContinueWith(task=>{var ignored=task.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
                busy=false;ClearPending();
                if(!File.Exists(ready+".cancel"))
                {
                    owner.RecordSupport("Server restart helper","Could not confirm the delayed helper was cancelled.",true);
                    if(generation==setupGeneration)Message(target,"Windows is still preparing a restart. Close any Windows prompt before closing the game.",true);
                    yield break;
                }
                if(generation==setupGeneration)
                {
                    var timeout=new TimeoutException("Restart helper did not start. The selected server setup remains ready.");
                    SupportFailure("Restart helper launch",timeout);Message(target,RestartLaunchFailure.Guidance(timeout),true);
                }
                yield break;
            }
            try
            {
                if(generation!=setupGeneration||!Same(ticket.Target,target)||File.Exists(ready+".cancel")){CancelRestartHandshake();busy=false;ClearPending();yield break;}
                if(launch.IsFaulted)throw launch.Exception!.GetBaseException();if(launch.IsCanceled)throw new IOException("Restart launch was cancelled.");
                if(MenuSafety().Length>0)throw new InvalidOperationException(MenuSafety());
                Message(target,"Restarting with the server's mods. Loading will finish before this page reopens.");owner.StartCoroutine(QuitWhenHelperReady(ready,ticket,target,generation));
            }
            catch(Exception ex){SupportFailure("Restart helper launch",ex);CancelRestartHandshake();busy=false;ClearPending();Message(target,RestartLaunchFailure.Guidance(ex));}
        }
        private static string LaunchRestartHelper(string helper,string ready)
        {
            try
            {
                if(File.Exists(ready+".cancel"))throw new IOException("Restart launch was cancelled.");
                using(var process=Process.GetCurrentProcess())
                {
                    string exe=process.MainModule.FileName;
                    string arguments=process.Id+" "+process.StartTime.ToUniversalTime().Ticks+" \""+exe+"\" \""+ready+"\"";
                    WindowsRestartLauncher.Start(helper,arguments,Path.GetDirectoryName(helper)!);return ready;
                }
            }
            catch{try{File.WriteAllText(ready+".cancel","cancel");File.Delete(ready);}catch{}throw;}
        }
        private void ClearPending(){try{string path=Path.Combine(Paths.ConfigPath,ServerSetupFiles.Filename);if(File.Exists(path))File.Delete(path);DisabledRestoreQueue.Clear(Paths.ConfigPath);ownsPendingRestart=false;}catch{}}
        private IEnumerator QuitWhenHelperReady(string ready,ServerSetupTicket ticket,LobbyInstance target,int generation)
        {
            float until=Time.unscaledTime+8;
            while(!File.Exists(ready)&&!File.Exists(ready+".cancel")&&Time.unscaledTime<until&&generation==setupGeneration)yield return new WaitForSecondsRealtime(.1f);
            if(File.Exists(ready)&&!File.Exists(ready+".cancel")&&generation==setupGeneration&&Same(ticket.Target,target)&&MenuSafety().Length==0&&(!ticket.DownloadsOnly||DownloadHeaderMatches(target)))
            {
                Exception? failure=null;try{if(armedDownloadsHeader.StartsWith("map|",StringComparison.Ordinal))CommitMapBatch();}catch(Exception ex){failure=ex;}
                if(failure!=null){busy=false;ClearPending();SupportFailure("Map setup commit",failure);Message(target,"Could not restart: "+Diagnostics.Clean(failure.Message),true);yield break;}
                restartExitConfirmed=true;Application.Quit();yield break;
            }
            busy=false;try{File.WriteAllText(ready+".cancel","cancel");File.Delete(ready);}catch{}ClearPending();
            if(generation==setupGeneration)Message(target,RestartLaunchFailure.Guidance(new TimeoutException("Restart helper did not become ready. The game and normal mod selection were kept.")),true);
        }
        private IEnumerator GuardServerReturn(IEnumerator routine)
        {
            while(true)
            {
                bool next=false;object? current=null;Exception? failure=null;
                try { next=routine.MoveNext();if(next)current=routine.Current; }catch(Exception ex) { failure=ex; }
                if(failure!=null)
                {
                    SupportFailure("Saved server return",failure);
                    log("Saved server return unavailable: "+failure.Message);
                    if(MenuSafety().Length==0){Open();ui?.Status("Mods loaded, but the saved server page could not reopen. Open Multiplayer and select it again.");}
                    yield break;
                }
                if(!next)yield break;yield return current;
            }
        }
        private IEnumerator ResumeServerPage()
        {
            if(resume==null)yield break;
            var gate=new StartupReadyGate();float until=Time.unscaledTime+300;
            log("Waiting for the native menu and Blueprinter's completed load before returning to the saved server.");
            while(Time.unscaledTime<until&&!gate.Check(Time.unscaledTime,MainMenu.State==MainMenu.LoadingState.Loaded,loader.LoadingFinished,InMenuScene(SceneManager.GetActiveScene(),NuclearOption.SceneLoading.MapLoader.MainMenu)&&UnityEngine.EventSystems.EventSystem.current!=null&&UnityEngine.EventSystems.EventSystem.current.isActiveAndEnabled&&MenuSafety().Length==0))yield return new WaitForSecondsRealtime(.25f);
            if(Time.unscaledTime>=until){log("Saved server return timed out while loading. Open the server browser when loading finishes.");yield break;}
            if(loader.Content.Count==0&&!resume.ReportedOnly)
            {
                var verify=Task.Run(()=>HashInventory(resume,Paths.PluginPath,true),updateLifetime.Token);while(!verify.IsCompleted)yield return null;
                if(!verify.IsFaulted&&!verify.IsCanceled)foreach(var record in resume.Inventory)loader.Content.Add(record.ToInstalled());
            }
            resume.ResumePending=false;try{ServerSetupFiles.Save(Paths.ConfigPath,resume);}catch(Exception ex){log("Could not consume saved server return: "+ex.Message);yield break;}
            var menu=Resources.FindObjectsOfTypeAll<MainMenu>().FirstOrDefault(m=>m&&m.isActiveAndEnabled&&m.gameObject.scene.IsValid());
            if(menu==null)yield break;menu.SelectMultiplayer();
            LobbyList? browser=null;until=Time.unscaledTime+60;
            while(Time.unscaledTime<until){browser=Resources.FindObjectsOfTypeAll<LobbyList>().FirstOrDefault(b=>b&&b.isActiveAndEnabled&&b.gameObject.scene.IsValid());if(browser!=null&&MenuSafety().Length==0)break;yield return new WaitForSecondsRealtime(.25f);}
            if(browser==null){log("Could not open the server browser after restart.");yield break;}
            yield return new WaitForSecondsRealtime(1);
            BrowserHooks.RequestSavedServer(browser);
            until=Time.unscaledTime+90;LobbyInstance? found=null;
            try
            {
                while(Time.unscaledTime<until&&browser!=null&&browser.isActiveAndEnabled)
                {
                    var cache=AccessTools.Field(typeof(SteamLobby),"_lobbyData").GetValue(SteamLobby.instance) as IDictionary;
                    found=cache?.Values.OfType<LobbyInstance>().FirstOrDefault(l=>{var actual=Snapshot(l,"");return resume.Target.Matches(actual.Dedicated,actual.SteamId,actual.Ip,actual.Port)&&actual.Wire.Length>0;});
                    if(found!=null)break;yield return new WaitForSecondsRealtime(.25f);
                }
            }
            finally { BrowserHooks.EndSavedServerLookup(browser!); }
            if(found==null){Open();ui?.Status("Mods loaded, but "+resume.Target.Name+" was not found. Refresh the browser or try again later.");yield break;}
            workflowTarget=Snapshot(found,owner.ExpandedFor(found));WorkflowStatus(found,found.HostVersion==Compatibility.Wire&&owner.GameplayAllowsJoin(found)?"Mods ready after restart. Click Join server.":"Review the current exact file check before joining. The server may have changed or verification may still be preparing.");
            browser!.ShowLobbyPopup(found);owner.RefreshServerActions();
            if(resume.DownloadsOnly)
            {
                if(resume.ReportedOnly){Message(found,loader.InventoryCaptured?"Restart complete. Check the host's current list, then try joining. Exact server files are not verified.":"Restart complete, but the installed mod list could not be checked. Copy the report and review the mods before joining.",!loader.InventoryCaptured);yield break;}
                until=Time.unscaledTime+75;bool fingerprintsReady=false;
                while(Time.unscaledTime<until&&browser!=null&&browser.isActiveAndEnabled)
                {
                    var remote=owner.GameplayFilesFor(found);
                    if(remote!=null&&remote.Ready&&remote.Complete&&owner.GameplayCheckSettled){fingerprintsReady=true;break;}
                    yield return new WaitForSecondsRealtime(.25f);
                }
                if(fingerprintsReady&&browser!=null&&browser.isActiveAndEnabled&&MenuSafety().Length==0)
                    SetupServer(found,owner.ExpandedFor(found));
                else if(browser!=null&&browser.isActiveAndEnabled)
                    Message(found,"Downloads installed. Wait for the server check, then press Setup mods to finish.",true);
            }
        }
    }
}
