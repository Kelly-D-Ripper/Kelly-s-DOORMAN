using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using NuclearOption.Networking.Lobbies;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private GameplayFingerprintSetupResult? gameplaySetup;
        private LobbyInstance? gameplaySetupTarget;
        private string gameplaySetupWire="",gameplaySetupHeader="";
        private Action? gameplayRestartVerification;
        private GameplayFingerprintSetupResult? GameplayPreparation(LobbyInstance? target)
            =>target!=null&&ReferenceEquals(target,gameplaySetupTarget)&&target.HostVersion==gameplaySetupWire
                &&owner.GameplayHeader(target)==gameplaySetupHeader?gameplaySetup:null;

        private void BeginGameplaySetup(LobbyInstance target,string expanded,bool joinAfter,string password,bool prompt,bool manual=false)
        {
            if(busy)return;
            if(owner.ReportedDownloadBusy){Message(target,"Wait for the current download to finish before setting up mods.");return;}
            string reason=MenuSafety();
            if(reason.Length>0){Message(target,reason);return;}
            if(Same(armedRestart,target)&&armedDownloadsHeader.Length>0)
            {RestartDownloadedForServer(target,expanded,manual);return;}
            busy=true;gameplaySetup=null;gameplaySetupTarget=null;gameplayRestartVerification=null;
            serverDownloadCancel=System.Threading.CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
            int generation=++setupGeneration;
            workflowTarget=Snapshot(target,expanded);
            Message(target,"Checking exact installed gameplay files against the server...");
            owner.StartCoroutine(GuardGameplaySetup(CheckGameplaySetup(target,expanded,joinAfter,password,prompt,manual,generation),target));
        }
        private IEnumerator GuardGameplaySetup(IEnumerator routine,LobbyInstance target)
        {
            while(true)
            {
                bool next=false;object? current=null;Exception? failed=null;
                try {next=routine.MoveNext();if(next)current=routine.Current;}catch(Exception ex){failed=ex;}
                if(failed!=null){busy=false;armedRestart=null;SupportFailure("Exact gameplay setup",failed);Message(target,"Could not check exact gameplay files: "+failed.Message,true);yield break;}
                if(!next)yield break;yield return current;
            }
        }
        private IEnumerator CheckGameplaySetup(LobbyInstance target,string expanded,bool joinAfter,string password,bool prompt,bool manual,int generation)
        {
            string wire=target.HostVersion,header=owner.GameplayHeader(target);
            bool continueSetup=false;
            try
            {
                float until=Time.unscaledTime+75;
                while(!owner.GameplayCheckSettled&&Time.unscaledTime<until&&generation==setupGeneration)
                    yield return new WaitForSecondsRealtime(.25f);
                if(generation!=setupGeneration)yield break;
                var remote=owner.GameplayFilesFor(target);
                var loaded=owner.LocalGameplayFiles;
                var pending=PendingUpdates();
                bool pendingWork=pending.Length>0
                    ||StandaloneBundleUpdates.Pending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath).Length>0
                    ||ServerMapUpdates.Read(Paths.CachePath,Paths.PluginPath,true).Length>0
                    ||IronjawPackageUpdates.Read(Paths.CachePath,Paths.PluginPath,true).Length>0
                    ||DisabledRestoreQueue.Read(Paths.ConfigPath).Length>0
                    ||ServerSetupFiles.Pending(Paths.ConfigPath)!=null
                    ||SelfUpdateTransaction.Pending(Paths.CachePath);
                if(target.HostVersion==wire&&owner.GameplayHeader(target)==header
                    &&GameplayFingerprintSetup.AlreadyReady(wire,Compatibility.Wire,remote,loaded,pendingWork,loader.LoadingFinished))
                {
                    armedRestart=null;armedDownloadsHeader="";
                    WorkflowStatus(target,"Mods ready. Click Join server.");
                    if(joinAfter)
                    {
                        owner.HideServerWorkflow();retrying=true;
                        try{SteamLobby.instance.TryJoinLobby(target,password,prompt);}finally{retrying=false;}
                    }
                    yield break;
                }
                var catalog=ModCatalog.Read(loader.Content,profile,log);
                RefreshExternalUnits();catalog.AddRange(ExternalEntries().Select(ExternalCopy));
                var metadata=QueuedModRequisiteMetadata.Read(pending,Paths.PluginPath);
                var prepared=ModRequisiteSelection.WithPending(catalog,pending,metadata,Paths.PluginPath);
                var sources=ModCatalog.LoadedSourceAssemblies(log);
                var availableTask=Task.Run(()=>ReadGameplayCandidates(prepared,pending,sources,remote,metadata),updateLifetime.Token);
                while(!availableTask.IsCompleted&&generation==setupGeneration)yield return null;
                if(generation!=setupGeneration)yield break;
                if(target.HostVersion!=wire||owner.GameplayHeader(target)!=header)
                {armedRestart=null;WorkflowStatus(target,"The server's files changed during setup. Press Setup mods to check its current requirements.",true);yield break;}
                if(availableTask.IsCanceled||availableTask.IsFaulted)
                {armedRestart=null;WorkflowStatus(target,"Exact installed files could not be checked. No mod selection was changed.",true);yield break;}
                var result=GameplayFingerprintSetup.Prepare(remote,availableTask.Result,loaded);
                if(!result.CanSetup)
                {
                    // The advertised content IDs and published registry are
                    // download hints. The host's complete hash is still the
                    // final authority after these packages have loaded.
                    Message(target,"Checking which required mods can be downloaded...");
                    var index=NosmrIndex();float indexDeadline=Time.unscaledTime+30;
                    while(index==null&&NosmrIndexState()=="loading"&&Time.unscaledTime<indexDeadline&&generation==setupGeneration)
                    {yield return new WaitForSecondsRealtime(.25f);index=NosmrIndex();}
                    if(generation!=setupGeneration)yield break;
                    if(target.HostVersion!=wire||owner.GameplayHeader(target)!=header)
                    {armedRestart=null;WorkflowStatus(target,"The server's files changed. Press Setup mods again.",true);yield break;}
                    if(index==null)
                    {armedRestart=null;WorkflowStatus(target,"Could not load the mod catalogue. "+Diagnostics.Clean(result.Error),true);owner.RecordSupport("Automatic mod downloads","Catalogue unavailable: "+NosmrIndexState(),true);yield break;}
                    string protocol=owner.NosmrProtocolFor(target);var host=protocol=="2"?owner.NosmrFor(target,index):null;
                    if(protocol.Length>0&&(protocol!="2"||host==null||!host.Verified))
                    {armedRestart=null;WorkflowStatus(target,"The host's mod list is incomplete or could not be verified. Refresh the server and try again.",true);owner.RecordSupport("Automatic mod downloads","Host NOSMR metadata unavailable or unverified.",true);yield break;}
                    var downloads=ServerDownloadResolver.Resolve(remote!,availableTask.Result,catalog,index.Records.Values,host);
                    if(downloads.Candidates.Count==0&&!ServerDownloadProof.Matches(remote!,availableTask.Result,new Dictionary<string,string>(StringComparer.Ordinal),out _))
                    {
                        var sameVersion=ServerDownloadResolver.Resolve(remote!,availableTask.Result,catalog,index.Records.Values,host,true);
                        if(sameVersion.Candidates.Count>0)downloads=sameVersion;
                    }
                    var errors=new List<string>(downloads.Manual);
                    var downloaded=new List<ServerDownloadPayload>();
                    try
                    {
                        int attempt=0;
                        foreach(var candidate in downloads.Candidates)
                        {
                            if(generation!=setupGeneration)yield break;
                            if(target.HostVersion!=wire||owner.GameplayHeader(target)!=header)
                            {armedRestart=null;WorkflowStatus(target,"The server's files changed during download. Press Setup mods again.",true);yield break;}
                            Message(target,"Downloading "+Diagnostics.Clean(candidate.ContentId)+" "+Diagnostics.Clean(candidate.Version)+" ("+(++attempt)+" of "+downloads.Candidates.Count+")...");
                            var progress=new ServerDownloadProgress();owner.SetServerDownloadProgress(target,true,-1);
                            Task<ServerDownloadPayload>? download=null;Exception? failed=null;
                            try {download=updates.DownloadServerCandidateAsync(candidate,serverDownloadCancel!.Token,progress);}
                            catch(Exception ex){failed=ex;}
                            if(failed==null&&download!=null)
                            {
                                while(!download.IsCompleted&&generation==setupGeneration){owner.SetServerDownloadProgress(target,true,progress.Fraction);yield return null;}
                                if(generation!=setupGeneration)
                                {
                                    _=download.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);
                                    yield break;
                                }
                                if(download.IsCanceled)failed=new IOException("Download cancelled.");
                                else if(download.IsFaulted)failed=download.Exception!.GetBaseException();
                                else {var item=download.Result;try{InspectGameplayPackage(item,catalog,true);downloaded.Add(item);}catch(Exception ex){item.Dispose();failed=ex;}}
                            }
                            owner.SetServerDownloadProgress(target,false,0);
                            if(failed!=null)
                            {
                                string detail=Diagnostics.Clean(candidate.ContentId)+": "+Diagnostics.Clean(failed.Message);
                                errors.Add(detail);owner.RecordSupport("Automatic mod download",detail,true);
                            }
                        }
                        if(errors.Count==0&&downloaded.Count>0)
                        {
                            Message(target,"Verifying the server's exact mod files...");
                            var recheck=Task.Run(()=>ReadGameplayCandidates(prepared,pending,sources,remote,metadata),updateLifetime.Token);
                            while(!recheck.IsCompleted&&generation==setupGeneration)yield return null;
                            if(generation!=setupGeneration)yield break;
                            if(target.HostVersion!=wire||owner.GameplayHeader(target)!=header)errors.Add("The server changed during download. Press Setup mods again.");
                            else if(recheck.IsCanceled||recheck.IsFaulted)errors.Add("Installed mods could not be checked again.");
                            else if(!ServerDownloadProof.Matches(remote!,recheck.Result,downloaded.ToDictionary(d=>d.Candidate.ContentId,d=>d.Hash,StringComparer.Ordinal),out var proofError))
                            {
                                // Versions can match while a player has altered
                                // bytes. Try known same-version releases too,
                                // but never stage them unless the full set then
                                // proves identical to the server.
                                var tried=new HashSet<string>(downloaded.Select(d=>d.Candidate.ContentId),StringComparer.Ordinal);
                                var extras=ServerDownloadResolver.Resolve(remote!,recheck.Result,catalog,index.Records.Values,host,true)
                                    .Candidates.Where(c=>!tried.Contains(c.ContentId)).ToArray();
                                if(extras.Length==0)errors.Add(proofError);
                                foreach(var extra in extras)
                                {
                                    if(generation!=setupGeneration)yield break;
                                    Message(target,"Checking another copy of "+Diagnostics.Clean(extra.ContentId)+" against the server...");
                                    var progress=new ServerDownloadProgress();owner.SetServerDownloadProgress(target,true,-1);
                                    Task<ServerDownloadPayload>? download=null;Exception? failed=null;
                                    try{download=updates.DownloadServerCandidateAsync(extra,serverDownloadCancel!.Token,progress);}catch(Exception ex){failed=ex;}
                                    if(failed==null&&download!=null)
                                    {
                                        while(!download.IsCompleted&&generation==setupGeneration){owner.SetServerDownloadProgress(target,true,progress.Fraction);yield return null;}
                                        if(generation!=setupGeneration)
                                        {_=download.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);yield break;}
                                        if(download.IsCanceled)failed=new IOException("Download cancelled.");
                                        else if(download.IsFaulted)failed=download.Exception!.GetBaseException();
                                        else {var item=download.Result;try{InspectGameplayPackage(item,catalog,true);downloaded.Add(item);}catch(Exception ex){item.Dispose();failed=ex;}}
                                    }
                                    owner.SetServerDownloadProgress(target,false,0);
                                    if(failed!=null)errors.Add(Diagnostics.Clean(extra.ContentId)+": "+Diagnostics.Clean(failed.Message));
                                }
                                if(errors.Count==0)
                                {
                                    var finalCheck=Task.Run(()=>ReadGameplayCandidates(prepared,pending,sources,remote,metadata),updateLifetime.Token);
                                    while(!finalCheck.IsCompleted&&generation==setupGeneration)yield return null;
                                    if(generation!=setupGeneration)yield break;
                                    if(target.HostVersion!=wire||owner.GameplayHeader(target)!=header)errors.Add("The server changed during download. Press Setup mods again.");
                                    else if(finalCheck.IsCanceled||finalCheck.IsFaulted)errors.Add("Installed mods could not be checked again.");
                                    else if(!ServerDownloadProof.Matches(remote!,finalCheck.Result,downloaded.ToDictionary(d=>d.Candidate.ContentId,d=>d.Hash,StringComparer.Ordinal),out var finalError))errors.Add(finalError);
                                }
                            }
                        }
                        if(errors.Count==0&&downloaded.Count>0)
                        {
                            string batch="";
                            try
                            {
                                if(!StartupAvailable())throw new IOException("Install the FULL DOORMAN package before using automatic setup.");
                                RefreshExternalUnits();
                                if(externalUnits.Any(u=>u.Error.Length>0))throw new IOException("A NOMM-disabled mod could not be inspected. Use NOMM or install manually.");
                                string[] disabled=ExternalEntries().Where(e=>!e.Content).Select(e=>e.Id).ToArray();
                                batch=AutoSetupBatch.Begin(Paths.CachePath,Paths.PluginPath);
                                foreach(var item in downloaded)
                                {
                                    serverDownloadCancel!.Token.ThrowIfCancellationRequested();
                                    if(item.Candidate.Standalone)StageReportedBundle(item.Candidate.Record,item.Candidate.PackageVersion,item.PayloadPath,item.Candidate.ContentId,item.Candidate.Version,true,item.Candidate.Target,item.Candidate.ArtifactName);
                                    else updates.StageServerPlugin(item,disabled,serverDownloadCancel.Token);
                                }
                                ModUpdateFiles.CheckPackageConflicts(PendingUpdates(),StandaloneBundleUpdates.Pending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath));
                                AutoSetupBatch.Commit(Paths.CachePath,batch);batch="";
                            }
                            catch(Exception ex)
                            {
                                if(batch.Length>0)try{AutoSetupBatch.Abort(Paths.CachePath,Paths.PluginPath,batch);}catch(Exception rollback){owner.RecordSupport("Automatic setup recovery",rollback.Message,true);}
                                errors.Add("Could not prepare the downloads: "+Diagnostics.Clean(ex.Message));
                            }
                        }
                        if(errors.Count==0&&downloaded.Count>0)
                        {
                            armedRestart=Snapshot(target,expanded);armedDownloadsHeader=header;
                            foreach(var item in downloaded)owner.RecordSupport("Automatic mod download",item.Candidate.ContentId+" "+item.Candidate.Version+" matched the server's exact content fingerprint and is queued for restart.");
                            WorkflowStatus(target,downloaded.Count+" mod download"+(downloaded.Count==1?"":"s")+" match the server. Press Proceed to restart and finish setup.");yield break;
                        }
                        armedRestart=null;armedDownloadsHeader="";
                        string problem=errors.Count>0?string.Join("; ",errors.Take(5)):Diagnostics.Clean(result.Error);
                        WorkflowStatus(target,problem,true);owner.RecordSupport("Exact gameplay setup",problem,true);yield break;
                    }
                    finally {foreach(var item in downloaded)item.Dispose();}
                }
                armedDownloadsHeader="";
                gameplaySetup=result;gameplaySetupTarget=target;gameplaySetupWire=wire;gameplaySetupHeader=header;
                // A code-only DLL is not part of the older content restart ticket.
                // Re-read the prepared files before committing that ticket too.
                gameplayRestartVerification=()=>
                {
                    var fresh=ReadGameplayCandidates(prepared,pending,sources,remote,metadata);
                    var checkedAgain=GameplayFingerprintSetup.Prepare(remote,fresh,loaded);
                    if(!checkedAgain.CanSetup)throw new IOException(checkedAgain.Error);
                    if(!checkedAgain.RequiredPluginIds.SequenceEqual(result.RequiredPluginIds)
                        ||!checkedAgain.ExcludedPluginIds.SequenceEqual(result.ExcludedPluginIds))
                        throw new IOException("Installed gameplay plugins changed during setup. Press Setup mods again.");
                };
                continueSetup=true;
            }
            finally {owner.SetServerDownloadProgress(target,false,0);serverDownloadCancel?.Dispose();serverDownloadCancel=null;busy=false;owner.RefreshServerActions();}
            if(!continueSetup)yield break;
            try { if(manual)RestartForServer(target,expanded,true);else SetupServerCore(target,expanded,joinAfter,password,prompt); }
            catch(Exception ex){armedRestart=null;busy=false;SupportFailure("Exact gameplay setup",ex);Message(target,"Could not set up exact gameplay files: "+ex.Message,true);}
        }
        private void RestartDownloadedForServer(LobbyInstance target,string expanded,bool manual)
        {
            try
            {
                if(!Same(armedRestart,target)||!DownloadHeaderMatches(target))throw new IOException("The server changed. Press Setup mods again.");
                if(!StartupAvailable())throw new IOException("Install the FULL DOORMAN package before restarting.");
                string helper=Path.Combine(Paths.BepInExRootPath,"DOORMAN","KellysDOORMANRestart.exe");
                if(!manual&&(!RuntimePlatform.CanUseWindowsRestart||!File.Exists(helper))){Message(target,"Automatic restart is unavailable. Choose Restart manually, then reopen the game through Steam.",true);return;}
                if(manual)CancelRestartHandshake();
                bool map=armedDownloadsHeader.StartsWith("map|",StringComparison.Ordinal);
                bool reported=armedDownloadsHeader.StartsWith("nosmr|",StringComparison.Ordinal);
                var ticket=reported?Ticket(target,expanded,reportedPlan??throw new IOException("The reported mod selection is unavailable. Press Setup mods again.")):new ServerSetupTicket {Target=Snapshot(target,expanded),DisabledContent=profile.DisabledContent.ToArray(),DisabledPlugins=profile.DisabledPlugins.ToArray()};
                if(map){ticket.DisabledPlugins=ticket.DisabledPlugins.Except(mapRequiredPlugins,StringComparer.Ordinal).ToArray();ticket.Restores=mapSetupRestores;CheckRestoreUpdateOverlap(ticket.Restores,PendingUpdates());}
                ticket.DownloadsOnly=true;ticket.ReportedOnly=reported||map&&!Signature.Parse(expanded).Complete;
                ticket.Target.Validate(ticket.ReportedOnly);busy=true;Message(target,"Checking downloaded files before restarting...");
                owner.StartCoroutine(PrepareRestart(target,ticket,helper,setupGeneration,PendingUpdates(),manual));
            }
            catch(Exception ex){armedRestart=null;armedDownloadsHeader="";busy=false;SupportFailure("Download restart",ex);Message(target,"Could not restart: "+Diagnostics.Clean(ex.Message),true);}
        }
        private bool ReportedUnchanged(LobbyInstance target,string stamp,NosmrReport.Index index)
            =>owner.NosmrStamp(target)==stamp&&owner.NosmrFor(target,index)?.Verified==true;
        private bool DownloadHeaderMatches(LobbyInstance target)
        {
            if(armedDownloadsHeader.Length==0)return false;
            if(armedDownloadsHeader.StartsWith("map|",StringComparison.Ordinal))return target.HostVersion==armedDownloadsHeader.Substring(4);
            if(!armedDownloadsHeader.StartsWith("nosmr|",StringComparison.Ordinal))return owner.GameplayHeader(target)==armedDownloadsHeader;
            var index=NosmrIndex();return index!=null&&ReportedUnchanged(target,armedDownloadsHeader.Substring(6),index);
        }
        private void BeginReportedSetup(LobbyInstance target,string expanded)
        {
            if(busy)return;
            if(owner.ReportedDownloadBusy){Message(target,"Wait for the current download to finish.");return;}
            if(Same(armedRestart,target)&&armedDownloadsHeader.StartsWith("nosmr|",StringComparison.Ordinal)){RestartDownloadedForServer(target,expanded,false);return;}
            string reason=MenuSafety();if(reason.Length>0){Message(target,reason);return;}
            if(!loader.LoadingFinished||!loader.InventoryCaptured||!loader.StartupSupported){Message(target,"Wait for the mod list to finish loading, then try again. If it is unavailable, use the individual download buttons.",true);return;}
            if(!StartupAvailable()){Message(target,"Install the full DOORMAN package before setting up mods.",true);return;}
            var server=Signature.Parse(target.HostVersion);var local=Signature.Parse(Compatibility.Wire);
            if(!server.KnownOpaqueHash||!Signature.KnownGame(server.Game)||server.Game!=local.Game){Message(target,"Match the server's game version first.",true);return;}
            serverDownloadCancel=System.Threading.CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);busy=true;reportedPlan=null;armedRestart=null;armedDownloadsHeader="";
            int generation=++setupGeneration;workflowTarget=Snapshot(target,expanded);Message(target,"Checking the host's reported gameplay mods...");
            owner.StartCoroutine(GuardGameplaySetup(CheckReportedSetup(target,expanded,generation),target));
        }
        private IEnumerator CheckReportedSetup(LobbyInstance target,string expanded,int generation)
        {
            string wire=target.HostVersion,stamp=owner.NosmrStamp(target);var downloaded=new List<ServerDownloadPayload>();
            try
            {
                var index=NosmrIndex();float until=Time.unscaledTime+30;
                while(index==null&&NosmrIndexState()=="loading"&&Time.unscaledTime<until&&generation==setupGeneration){yield return new WaitForSecondsRealtime(.25f);index=NosmrIndex();}
                if(generation!=setupGeneration)yield break;
                if(index==null){Message(target,"Could not load the mod catalogue. Try again or use Visit GitHub.",true);yield break;}
                var host=owner.NosmrFor(target,index);
                if(host==null||!host.Verified){Message(target,"The host's reported mod list is incomplete. Refresh the server and try again.",true);yield break;}
                var catalog=ModCatalog.Read(loader.Content,profile,log);RefreshExternalUnits();catalog.AddRange(ExternalEntries().Select(ExternalCopy));
                var existingPlugins=PendingUpdates();var contentRoot=Path.Combine(Paths.CachePath,"DOORMAN-content-updates");var existingBundles=StandaloneBundleUpdates.Pending(contentRoot,Paths.PluginPath);
                var planning=Task.Run(()=>
                {
                    ModUpdateFiles.VerifyPending(existingPlugins,Paths.PluginPath);StandaloneBundleUpdates.VerifyPending(contentRoot,Paths.PluginPath);
                    var wanted=ServerDownloadResolver.ResolveReported(host,catalog,index.Records.Values);
                    var installed=wanted.Candidates.Select(c=>ServerDownloadResolver.InstalledPackage(c,Path.Combine(Paths.CachePath,"DOORMAN-updates"),Paths.PluginPath)).Where(p=>p!=null&&!existingPlugins.Any(q=>q.Plugin==p.Plugin||q.Target.Equals(p.Target,StringComparison.OrdinalIgnoreCase))).Select(p=>p!).ToArray();
                    return Tuple.Create(wanted,installed);
                },serverDownloadCancel!.Token);
                while(!planning.IsCompleted&&generation==setupGeneration)yield return null;
                if(generation!=setupGeneration)yield break;
                if(planning.IsCanceled||planning.IsFaulted){Message(target,"Could not check installed mods. Copy the report for details.",true);if(planning.IsFaulted)SupportFailure("Reported mod planning",planning.Exception!.GetBaseException());yield break;}
                var plan=planning.Result.Item1;var installedPackages=planning.Result.Item2;var errors=new List<string>(plan.Manual);
                plan.Candidates.RemoveAll(c=>installedPackages.Any(p=>p.PackageRecordId==c.Record.Id&&p.PackageSourceHash!=null&&(c.Record.Artifacts??Array.Empty<NommArtifact>()).Any(a=>a!=null&&a.Version==c.Version&&string.Equals(a.Hash,"sha256:"+p.PackageSourceHash,StringComparison.OrdinalIgnoreCase))));
                var selectedQueued=existingPlugins.Where(p=>plan.Candidates.Any(c=>ServerDownloadResolver.AlreadyQueued(c,new[]{p},Array.Empty<StandaloneBundlePlan>(),Paths.PluginPath))).ToArray();
                int alreadyQueued=plan.Candidates.RemoveAll(c=>ServerDownloadResolver.AlreadyQueued(c,existingPlugins,existingBundles,Paths.PluginPath));
                if(target.HostVersion!=wire||!ReportedUnchanged(target,stamp,index)){Message(target,"The host's mod list changed. Press Setup mods again.",true);yield break;}
                int number=0;
                foreach(var candidate in plan.Candidates)
                {
                    if(generation!=setupGeneration)yield break;
                    if(target.HostVersion!=wire||!ReportedUnchanged(target,stamp,index)){Message(target,"The host's mod list changed. Press Setup mods again.",true);yield break;}
                    Message(target,"Downloading "+Diagnostics.Clean(candidate.Record.Name)+" "+Diagnostics.Clean(candidate.Version)+" ("+(++number)+" of "+plan.Candidates.Count+")...");
                    var progress=new ServerDownloadProgress();owner.SetServerDownloadProgress(target,true,-1);
                    var task=updates.DownloadServerCandidateAsync(candidate,serverDownloadCancel!.Token,progress);
                    while(!task.IsCompleted&&generation==setupGeneration){owner.SetServerDownloadProgress(target,true,progress.Fraction);yield return null;}
                    if(generation!=setupGeneration){_=task.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();},TaskScheduler.Default);yield break;}
                    owner.SetServerDownloadProgress(target,false,0);
                    if(task.IsCanceled||task.IsFaulted){string detail=Diagnostics.Clean(candidate.Record.Name)+": "+Diagnostics.Clean(task.IsCanceled?"Download cancelled.":task.Exception!.GetBaseException().Message);errors.Add(detail);owner.RecordSupport("NOSMR automatic download",detail,true);}
                    else
                    {
                        var item=task.Result;
                        try{InspectGameplayPackage(item,catalog,false);downloaded.Add(item);}
                        catch(Exception ex){item.Dispose();string detail=Diagnostics.Clean(candidate.Record.Name)+": "+Diagnostics.Clean(ex.Message);errors.Add(detail);owner.RecordSupport("NOSMR automatic download",detail,true);}
                    }
                }
                if(target.HostVersion!=wire||!ReportedUnchanged(target,stamp,index)){Message(target,"The host's mod list changed. Press Setup mods again.",true);yield break;}
                string batch="";int staged=0;var stagedPayloads=new List<ServerDownloadPayload>();
                try
                {
                    if(externalUnits.Any(u=>u.Error.Length>0))throw new IOException("A NOMM-disabled package could not be inspected. Resolve it in NOMM first.");
                    string[] disabled=ExternalEntries().Where(e=>!e.Content).Select(e=>e.Id).ToArray();
                    if(downloaded.Count>0)batch=AutoSetupBatch.Begin(Paths.CachePath,Paths.PluginPath);
                    foreach(var item in downloaded)
                    {
                        try
                        {
                            if(item.Candidate.Standalone)StageReportedBundle(item.Candidate.Record,item.Candidate.Version,item.PayloadPath,"","",false);
                            else updates.StageServerPlugin(item,disabled,serverDownloadCancel!.Token);
                            staged++;stagedPayloads.Add(item);owner.RecordSupport("NOSMR automatic download",item.Candidate.Record.Name+" "+item.Candidate.Version+" queued from the host's reported list. The server's loaded files are not verified.");
                        }
                        catch(Exception ex)when(!(ex is OperationCanceledException)){string detail=Diagnostics.Clean(item.Candidate.Record.Name)+": "+Diagnostics.Clean(ex.Message);errors.Add(detail);owner.RecordSupport("NOSMR automatic download",detail,true);}
                    }
                    if(batch.Length>0){ModUpdateFiles.CheckPackageConflicts(PendingUpdates(),StandaloneBundleUpdates.Pending(contentRoot,Paths.PluginPath));AutoSetupBatch.Commit(Paths.CachePath,batch);batch="";}
                }
                catch(Exception ex){if(batch.Length>0)try{AutoSetupBatch.Abort(Paths.CachePath,Paths.PluginPath,batch);}catch(Exception rollback){owner.RecordSupport("Automatic setup recovery",rollback.Message,true);}Message(target,"Could not prepare downloads: "+Diagnostics.Clean(ex.Message),true);yield break;}
                var hostIds=new HashSet<string>(host.Entries.Where(e=>e.Resolved).Select(e=>e.Id),StringComparer.OrdinalIgnoreCase);
                var allEntries=catalog.ToArray();var allRecords=index.Records.Values.ToArray();
                var required=catalog.Where(e=>e.Content&&!e.ExternalDisabled&&ServerDownloadResolver.MatchReportedContent(e,allEntries,allRecords) is NommRecord r&&hostIds.Contains(r.Id)).ToArray();
                var selectedContent=required.Select(e=>e.Id).Concat(stagedPayloads.SelectMany(d=>d.PackageFiles).Where(f=>f.ContentId.Length>0).Select(f=>f.ContentId)).Concat(selectedQueued.Concat(installedPackages).SelectMany(p=>p.PackageFiles??Array.Empty<PendingPackageFile>()).Where(f=>!f.Remove&&f.ContentId.Length>0).Select(f=>f.ContentId));
                var disabledContent=profile.DisabledContent.Except(selectedContent,StringComparer.Ordinal).Concat(plan.DisableContent).Distinct(StringComparer.Ordinal).ToArray();
                var requiredWrappers=required.Select(e=>e.WrapperPlugin).Concat(catalog.Where(e=>!e.Content&&!e.ExternalDisabled&&NommMetadata.Match(allRecords,e.Id,"","") is NommRecord r&&hostIds.Contains(r.Id)&&ServerDownloadResolver.IsReportedGameplay(r)).Select(e=>e.Id)).Concat(stagedPayloads.Where(d=>d.Plugin.Length>0).Select(d=>d.Plugin)).Concat(selectedQueued.Concat(installedPackages).Select(p=>p.Plugin)).Where(id=>id.Length>0).Distinct(StringComparer.Ordinal).ToArray();
                var additions=stagedPayloads.Select(d=>d.Plugin).Concat(selectedQueued.Concat(installedPackages).Select(p=>p.Plugin)).Where(id=>id.Length>0&&!catalog.Any(e=>!e.Content&&e.Id==id)).Distinct(StringComparer.Ordinal);
                var pluginRequirements=catalog.Where(e=>!e.Content&&!e.ExternalDisabled).Select(e=>new KeyValuePair<string,string[]>(e.Id,e.Requires.ToArray())).Concat(additions.Select(id=>new KeyValuePair<string,string[]>(id,Array.Empty<string>()))).ToArray();
                string[] disabledPlugins;
                try{disabledPlugins=ServerPluginSelection.Resolve(pluginRequirements,profile.DisabledPlugins,Array.Empty<string>(),requiredWrappers,false);}
                catch(Exception ex){Message(target,"Could not select this mod set: "+Diagnostics.Clean(ex.Message),true);yield break;}
                var pending=PendingUpdates();
                var currentContent=StartupProfile().DisabledContent;
                var currentPlugins=resume?.DisabledPlugins??profile.DisabledPlugins;
                bool selectionChanged=!new HashSet<string>(currentContent,StringComparer.Ordinal).SetEquals(disabledContent)||!new HashSet<string>(currentPlugins,StringComparer.Ordinal).SetEquals(disabledPlugins);
                if(plan.UnknownContent.Count>0)owner.RecordSupport("NOSMR setup","Kept unrecognised installed content: "+string.Join(", ",plan.UnknownContent.Select(Diagnostics.Clean))+". Check the host's list.",true);
                foreach(var error in errors)owner.RecordSupport("NOSMR setup needs manual help",error,true);
                if(staged==0&&!selectionChanged&&pending.Length==0&&StandaloneBundleUpdates.Pending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath).Length==0)
                {
                    string result=errors.Count>0?"Some mods need manual help. Use Visit GitHub or Copy report.":"The reported gameplay mods are already selected. The host's exact files are not verified.";
                    if(target.HostVersion!=Compatibility.Wire)result+=" The game's compatibility still differs; send the report to the host.";
                    WorkflowStatus(target,result,errors.Count>0||target.HostVersion!=Compatibility.Wire);yield break;
                }
                reportedPlan=new ServerSetupPlan {CanSetup=true,NeedsRestart=true,DisabledContent=disabledContent,DisabledPlugins=disabledPlugins,Updates=pending};
                armedRestart=Snapshot(target,expanded);armedDownloadsHeader="nosmr|"+stamp;
                int ready=staged+alreadyQueued;
                string notice=ready+" mod"+(ready==1?"":"s")+" ready. Press Proceed to restart.";
                if(errors.Count>0)notice+=" "+errors.Count+" need manual help; see Details.";
                if(plan.UnknownContent.Count>0)notice+=" Some unrecognised mods were kept.";
                WorkflowStatus(target,notice,errors.Count>0);
            }
            finally{foreach(var item in downloaded)item.Dispose();owner.SetServerDownloadProgress(target,false,0);serverDownloadCancel?.Dispose();serverDownloadCancel=null;busy=false;owner.RefreshServerActions();}
        }
        // Unity can inspect content bundles only on the main thread. No assets
        // or plugin code are loaded; the inspected bundle is unloaded immediately.
        private static void InspectGameplayPackage(ServerDownloadPayload payload,IEnumerable<ModEntry> inventory,bool exact)
        {
            if(payload.PackageFiles.Length==0)return;
            var bundle=payload.PackageFiles.Single(f=>f.Relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase));
            DisabledBundleInfo info;
            using(var stream=File.OpenRead(bundle.Path))info=DisabledContentManifestReader.Read(stream);
            if(exact&&(info.Id!=payload.Candidate.ContentId||info.Version!=payload.Candidate.Version))throw new InvalidDataException("The downloaded content ID or version differs from the server.");
            var entries=inventory.ToArray();
            var plugins=entries.Where(e=>!e.Content&&e.Id==payload.Plugin).Take(2).ToArray();
            if(plugins.Length>1||plugins.Any(e=>e.ExternalDisabled))throw new InvalidDataException("This plugin has multiple copies or is disabled in NOMM. Resolve that first.");
            if(plugins.Length==1)
            {
                if(payload.Candidate.Target.Length>0&&!Path.GetFullPath(plugins[0].Path).Equals(Path.GetFullPath(payload.Candidate.Target),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The installed plugin location changed.");
                payload.Candidate.Target=plugins[0].Path;
            }
            var contents=entries.Where(e=>e.Content&&e.Id==info.Id).Take(2).ToArray();
            if(contents.Length>1||contents.Any(e=>e.ExternalDisabled))throw new InvalidDataException("This content has multiple copies or is disabled in NOMM. Resolve that first.");
            if(contents.Length>0&&payload.Candidate.Target.Length==0)throw new InvalidDataException("This content is already installed in another package. Update it manually.");
            bundle.ContentId=info.Id;bundle.ContentVersion=info.Version;payload.Hash=bundle.Hash;
        }
        // Individual server-list and GitHub installs share the same safe ZIP
        // extractor. Downloading stays off-thread; Unity bundle inspection and
        // the single batch commit return to the main thread.
        private Task<string> QueueGameplayZip(NommRecord record,string version,string plugin,string target,System.Threading.CancellationToken token)
        {
            var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var candidate=new ServerDownloadCandidate{Record=record,Version=version,ContentId=record.Id,OwnerPlugin=plugin,Target=target};
            var task=updates.DownloadServerCandidateAsync(candidate,token);
            owner.StartCoroutine(FinishGameplayZip(task,completion,token));return completion.Task;
        }
        private IEnumerator FinishGameplayZip(Task<ServerDownloadPayload> task,TaskCompletionSource<string> completion,System.Threading.CancellationToken token)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled){completion.TrySetCanceled();yield break;}
            if(task.IsFaulted){completion.TrySetException(task.Exception!.GetBaseException());yield break;}
            using(var payload=task.Result)
            {
                string batch="";
                try
                {
                    token.ThrowIfCancellationRequested();string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                    if(!loader.LoadingFinished||payload.PackageFiles.Length>0&&!loader.InventoryCaptured)throw new InvalidOperationException("Wait for the mod list to finish loading.");
                    RefreshExternalUnits();if(externalUnits.Any(u=>u.Error.Length>0))throw new InvalidOperationException("A NOMM-disabled package could not be inspected. Resolve it in NOMM first.");
                    var entries=ModCatalog.Read(loader.Content,profile,log);entries.AddRange(ExternalEntries().Select(ExternalCopy));
                    InspectGameplayPackage(payload,entries,false);
                    string[] disabled=ExternalEntries().Where(e=>!e.Content).Select(e=>e.Id).ToArray();
                    batch=AutoSetupBatch.Begin(Paths.CachePath,Paths.PluginPath);updates.StageServerPlugin(payload,disabled,token);
                    ModUpdateFiles.CheckPackageConflicts(PendingUpdates(),StandaloneBundleUpdates.Pending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath));
                    AutoSetupBatch.Commit(Paths.CachePath,batch);batch="";completion.TrySetResult(payload.Plugin);
                }
                catch(OperationCanceledException){if(batch.Length>0)try{AutoSetupBatch.Abort(Paths.CachePath,Paths.PluginPath,batch);}catch(Exception ex){completion.TrySetException(ex);yield break;}completion.TrySetCanceled();}
                catch(Exception ex){if(batch.Length>0)try{AutoSetupBatch.Abort(Paths.CachePath,Paths.PluginPath,batch);}catch(Exception rollback){completion.TrySetException(new IOException("Package recovery needs attention: "+rollback.Message,ex));yield break;}completion.TrySetException(ex);}
            }
        }
        private Action? GameplayRestartVerifier(LobbyInstance target)
        {
            if(!owner.RequiresGameplayFiles(target))return null;
            if(GameplayPreparation(target)==null||gameplayRestartVerification==null)
                throw new IOException("The server's exact files changed. Press Setup mods again before restarting.");
            return gameplayRestartVerification;
        }
        private GameplayFingerprintSnapshot ReadGameplayCandidates(IEnumerable<ModEntry> inventory,PendingModUpdate[] pending,LoadedContentAssembly[] loaded,GameplayFingerprintSnapshot? remote,IReadOnlyDictionary<string,QueuedModRequisiteMetadata> metadata)
        {
            try
            {
                if(remote==null||!remote.Ready||!remote.Complete)return GameplayFingerprintSnapshot.Unavailable("The server's exact gameplay file list is unavailable or still preparing.");
                var requiredContent=new HashSet<string>(GameplayFingerprintPolicy.SelectedContent(remote).Select(r=>r.Id),StringComparer.Ordinal);
                var files=new List<GameplayFingerprintSource>();
                foreach(var entry in inventory.Where(e=>e.Content?requiredContent.Contains(e.Id):GameplayFingerprintPolicy.IsAuditedPlugin(e.Id)))
                {
                    updateLifetime.Token.ThrowIfCancellationRequested();
                    var update=GameplayFingerprintCandidates.PendingFor(entry,pending,metadata,Paths.PluginPath);
                    string root=entry.ExternalDisabled?Paths.BepInExRootPath:Paths.PluginPath,path,version=entry.Version;
                    if(update!=null)
                    {
                        root=update.Job;path=ModUpdateFiles.Within(root,"payload.bin");
                        if(entry.Content)version=update.Version;
                    }
                    else if(entry.Content)path=ContentSourceFiles.Resolve(root,entry.Path,entry.WrapperPath,entry.ExternalDisabled?Array.Empty<LoadedContentAssembly>():loaded);
                    else path=entry.Path;
                    files.Add(new GameplayFingerprintSource { Kind=entry.Content?"content":"plugin",Id=entry.Id,Version=version,Path=path,AllowedRoot=root,Staged=update!=null });
                }
                return GameplayFingerprintInventory.ReadFiles(files,updateLifetime.Token);
            }
            catch(Exception ex){return GameplayFingerprintSnapshot.Unavailable("Installed gameplay file check failed: "+ex.Message);}
        }
    }
}
