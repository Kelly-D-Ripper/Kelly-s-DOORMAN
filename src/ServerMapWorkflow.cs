using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using NuclearOption.Networking.Lobbies;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private DisabledModRestore[] mapSetupRestores=Array.Empty<DisabledModRestore>();
        private string[] mapRequiredPlugins=Array.Empty<string>();
        private string mapOwnedBatch="";
        private void CancelMapBatch()
        {
            if(mapOwnedBatch.Length==0)return;
            try{AutoSetupBatch.Abort(Paths.CachePath,Paths.PluginPath,mapOwnedBatch);mapOwnedBatch="";}
            catch(Exception ex){SupportFailure("Map setup cancellation",ex);log("Map setup cancellation needs recovery: "+ex.Message);}
        }
        private void CommitMapBatch()
        {
            if(mapOwnedBatch.Length==0)return;
            AutoSetupBatch.Commit(Paths.CachePath,mapOwnedBatch);mapOwnedBatch="";
        }
        private sealed class MapLoaderChoice
        {
            internal string Id="", Repository="", Version="", Path="", Minimum="";
            internal string SupportPath="",SupportHash="";
            internal bool Restore, Queued, Loaded, SupportRestore,SupportQueued,IronjawRestore;
            internal ModEntry[] Inventory=Array.Empty<ModEntry>(),RestoreEntries=Array.Empty<ModEntry>();
            internal NommDisabledUnit[] Units=Array.Empty<NommDisabledUnit>();
        }
        private sealed class MapSetupResult {internal string Job="",Batch="";internal DisabledModRestore[] Restores=Array.Empty<DisabledModRestore>();}
        private sealed class MapSetupProgress
        {
            internal volatile string Message="Checking the map...";
            internal readonly ServerDownloadProgress Download=new ServerDownloadProgress();
            internal volatile bool Downloading;
        }

        private void BeginMapSetup(LobbyInstance target,string expanded,MapJoinDiagnostic issue)
        {
            if(busy)return;
            try
            {
                if(owner.ReportedDownloadBusy)throw new InvalidOperationException("Wait for the current download to finish.");
                string safety=MenuSafety();if(safety.Length>0)throw new InvalidOperationException(safety);
                if(!StartupAvailable())throw new InvalidOperationException("Install the complete DOORMAN client package before changing maps.");
                if(issue.MapId.Length==0)throw new InvalidOperationException("This map does not provide a supported exact identity. Ask the host for its map download.");
                bool iron=issue.Key=="IronjawSound";
                if(!loader.LoadingFinished)throw new InvalidOperationException("Wait for mods to finish loading, then try again.");
                RefreshExternalUnits();if(externalUnits.Any(u=>u.Error.Length>0))throw new InvalidOperationException("A NOMM-disabled mod could not be inspected. Check the report.");
                var catalog=ModCatalog.Read(loader.Content,profile,log);catalog.AddRange(ExternalEntries().Select(ExternalCopy));
                string loaderId=iron?"NOMapLoader":"com.javoski.custommaps", loaderRepo=iron?"MinecrackTyler/NOMapLoader":"BatongMaster/NOCustomMaps";
                string minimum=iron?"1.0.5":"";
                var matches=catalog.Where(e=>!e.Content&&e.Id==loaderId).Take(2).ToArray();
                if(matches.Length>1)throw new InvalidOperationException("More than one copy of the map loader was found. Check the report.");
                var selected=matches.FirstOrDefault();
                bool wakeIsland=issue.MapId=="kellys-wake-island";
                var support=!wakeIsland?Array.Empty<ModEntry>():catalog.Where(e=>!e.Content&&e.Id=="kellys.wake.flight-support").Take(2).ToArray();
                if(support.Length>1)throw new InvalidOperationException("More than one Wake Flight Support plugin was found. Check the report.");
                string[] ironIds={"talamander.terrainlab.gameplay","talamander.terrainlab.nopatch","talamander.terrainlab.race","talamander.terrainlab.scatter"};
                var ironExternal=iron?catalog.Where(e=>e.ExternalDisabled&&!e.Content&&ironIds.Contains(e.Id,StringComparer.Ordinal)).ToArray():Array.Empty<ModEntry>();
                bool restoreIron=ironExternal.Length>0;
                if(restoreIron)
                {
                    string dir=Path.GetDirectoryName(ironExternal[0].Path)??"";
                    if(ironExternal.Length!=4||ironIds.Any(id=>ironExternal.Count(e=>e.Id==id)!=1)||ironExternal.Select(e=>e.RestoreSource).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1||
                        ironExternal.Any(e=>!string.Equals(Path.GetDirectoryName(e.Path),dir,StringComparison.OrdinalIgnoreCase))||
                        !File.Exists(Path.Combine(dir,"map_ironjawsound"))||!File.Exists(Path.Combine(dir,"IronjawSound.release.json")))
                        throw new InvalidOperationException("The NOMM-disabled Ironjaw package is incomplete or split. Check it in NOMM first.");
                }
                var selectedRestores=new[]{selected,support.FirstOrDefault()}.Where(e=>e!=null&&e.ExternalDisabled).Cast<ModEntry>().Concat(ironExternal).ToArray();
                var choice=new MapLoaderChoice {Id=loaderId,Repository=loaderRepo,Minimum=minimum,Version=selected?.Version??"",Path=selected?.Path??"",Restore=selected?.ExternalDisabled==true,Loaded=Chainloader.PluginInfos.ContainsKey(loaderId)};
                choice.Inventory=catalog.ToArray();choice.RestoreEntries=selectedRestores;choice.Units=externalUnits.ToArray();mapSetupRestores=Array.Empty<DisabledModRestore>();
                choice.IronjawRestore=restoreIron;
                if(support.Length==1){choice.SupportPath=support[0].Path;choice.SupportRestore=support[0].ExternalDisabled;}
                var pendingSupport=PendingUpdates().Where(p=>p.Plugin=="kellys.wake.flight-support").Take(2).ToArray();
                if(pendingSupport.Length>1)throw new InvalidOperationException("More than one Wake Flight Support update is queued.");
                if(pendingSupport.Length==1){choice.SupportQueued=true;choice.SupportHash=pendingSupport[0].NewHash;}
                var pendingLoader=PendingUpdates().Where(p=>p.Plugin==loaderId).Take(2).ToArray();
                if(pendingLoader.Length>1)throw new InvalidOperationException("More than one loader update is queued.");
                if(pendingLoader.Length==1){choice.Queued=true;choice.Version=pendingLoader[0].PluginVersion??pendingLoader[0].Version;}
                if(choice.Restore&&minimum.Length>0&&ModReleaseVersion.Compare(choice.Version,minimum)<0)throw new InvalidOperationException("The disabled map loader needs an update. Enable it in NOMM and update it first.");
                mapRequiredPlugins=iron?new[]{loaderId,"talamander.terrainlab.gameplay","talamander.terrainlab.nopatch","talamander.terrainlab.race","talamander.terrainlab.scatter"}:wakeIsland?new[]{loaderId,"kellys.wake.flight-support"}:new[]{loaderId};
                if(wakeIsland)issue.Repository="Kelly-D-Ripper/KELLYS-Wake-Island";
                if(iron&&PendingUpdates().Any(p=>mapRequiredPlugins.Contains(p.Plugin,StringComparer.Ordinal)&&p.Plugin!=loaderId))throw new InvalidOperationException("Finish the queued Ironjaw plugin update first.");
                var maps=MapBundleBridge.ReadLoaded(out var detail,out var complete,out var settled);
                if(!iron&&(!complete||!settled))throw new InvalidOperationException("Wait for the installed map list to finish loading. "+detail);
                var existing=iron?Array.Empty<MapEntry>():maps.Where(m=>m.MapId==issue.MapId).ToArray();
                var old=existing.Select(m=>new ServerMapOldFile {Relative=ServerMapUpdates.Relative(Paths.PluginPath,m.FilePath),Key=m.MapKey}).ToArray();
                bool opaque=!Signature.Parse(expanded).Complete;Snapshot(target,expanded).Validate(opaque);
                if(!StartupAvailable())throw new InvalidOperationException("The restart helper is unavailable.");
                var source=NosmrIndex();
                busy=true;armedRestart=null;armedDownloadsHeader="";workflowTarget=Snapshot(target,expanded);
                int generation=++setupGeneration;serverDownloadCancel=CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
                string wire=target.HostVersion;
                Message(target,"Checking "+Diagnostics.Clean(issue.Name)+"...");
                owner.StartCoroutine(GuardMapSetup(MapSetup(target,expanded,issue,old,source,choice,wire,generation),target));
            }
            catch(Exception ex){busy=false;SupportFailure("Server map setup",ex);Message(target,"Could not set up the map: "+Diagnostics.Clean(ex.Message),true);}
        }
        private IEnumerator GuardMapSetup(IEnumerator routine,LobbyInstance target)
        {
            while(true)
            {
                bool next=false;object? current=null;Exception? error=null;
                try {next=routine.MoveNext();if(next)current=routine.Current;}catch(Exception ex){error=ex;}
                if(error!=null){busy=false;armedRestart=null;SupportFailure("Server map setup",error);Message(target,"Could not set up the map: "+Diagnostics.Clean(error.Message),true);yield break;}
                if(!next)yield break;yield return current;
            }
        }
        private IEnumerator MapSetup(LobbyInstance target,string expanded,MapJoinDiagnostic issue,ServerMapOldFile[] old,NosmrReport.Index? index,MapLoaderChoice choice,string wire,int generation)
        {
            Task<MapSetupResult>? task=null;var token=serverDownloadCancel!.Token;var progress=new MapSetupProgress();
            try
            {
                float deadline=Time.unscaledTime+30;
                while(issue.Key!="IronjawSound"&&issue.MapId!="kellys-wake-island"&&index==null&&NosmrIndexState()=="loading"&&Time.unscaledTime<deadline&&generation==setupGeneration)
                {yield return new WaitForSecondsRealtime(.25f);index=NosmrIndex();}
                if(generation!=setupGeneration)yield break;
                // Saved, verified copies are preferred and work without GitHub.
                var records=index?.Records.Values.ToArray()??Array.Empty<NommRecord>();
                var knownRecord=ServerMapResolver.Find(issue.MapId,records);
                if(knownRecord!=null){issue.Repository=knownRecord.Owner+"/"+knownRecord.Repository;owner.RefreshMapView(target);}
                var candidates=ServerMapResolver.Candidates(issue.MapId,records);
                string cache=Paths.CachePath,plugins=Paths.PluginPath;
                task=Task.Run(async ()=>
                {
                    string batch=AutoSetupBatch.Begin(cache,plugins);
                    try
                    {
                    var restores=choice.RestoreEntries.Length==0?Array.Empty<DisabledModRestore>():NommDisabledSelection.Required(choice.Inventory,choice.RestoreEntries,choice.Units).Select(u=>NommDisabledCatalog.Prepare(Paths.BepInExRootPath,u)).ToArray();
                    if(!choice.Queued&&(!choice.Restore&&(choice.Path.Length==0||choice.Minimum.Length>0&&ModReleaseVersion.Compare(choice.Version,choice.Minimum)<0)))
                    {
                        progress.Message="Checking the map loader...";
                        var loaderLink=GitHubInstallLink.Parse("https://github.com/"+choice.Repository);
                        var loaderCheck=await updates.CheckInstallLinkAsync(loaderLink,token).ConfigureAwait(false);
                        if(loaderCheck.Release==null||loaderCheck.Asset==null||string.IsNullOrEmpty(loaderCheck.Asset.Digest))throw new IOException("Could not verify the map loader download. Try again.");
                        if(choice.Path.Length==0)await updates.StageInstallAsync(choice.Repository,choice.Id,choice.Minimum,loaderCheck.Release,loaderCheck.Asset,token).ConfigureAwait(false);
                        else await updates.StageAsync(choice.Repository,choice.Id,choice.Path,loaderCheck.Release,loaderCheck.Asset,token,null,choice.Minimum).ConfigureAwait(false);
                        choice.Version=loaderCheck.Release.Tag;
                    }
                    if(choice.Minimum.Length>0&&(ModReleaseVersion.Compare(choice.Version,choice.Minimum) is not int loaderComparison||loaderComparison<0))throw new IOException("Ironjaw needs NOMapLoader "+choice.Minimum+" or newer.");
                    if(issue.Key=="IronjawSound")
                    {
                        if(choice.IronjawRestore)return new MapSetupResult {Batch=batch,Restores=restores};
                        var link=GitHubInstallLink.Parse("https://github.com/"+IronjawPackageUpdates.Repository);
                        var result=await updates.CheckInstallLinkAsync(link,token).ConfigureAwait(false);
                        if(result.Release==null||result.Asset==null||string.IsNullOrEmpty(result.Asset.Digest))throw new IOException("Could not verify the Ironjaw release. Try again.");
                        progress.Message="Downloading Ironjaw Sound...";progress.Downloading=true;
                        string archive=await updates.DownloadMapPackageAsync(link.Repository,result.Release,result.Asset,token,progress.Download,ServerMapUpdates.MaximumMapBytes,TimeSpan.FromMinutes(45)).ConfigureAwait(false);
                        progress.Downloading=false;progress.Message="Checking Ironjaw Sound and preserving your previous files...";
                        try
                        {
                            string job=IronjawPackageUpdates.Stage(cache,plugins,archive,result.Asset.Digest!.Substring(7),result.Release.Tag,token);
                            return new MapSetupResult {Job=job,Batch=batch,Restores=restores};
                        }
                        finally{try{File.Delete(archive);}catch(IOException){}catch(UnauthorizedAccessException){}}
                    }
                    var discovered=ServerMapUpdates.DiscoverConventional(plugins,issue.MapId,token);
                    old=old.Concat(discovered.Where(d=>!old.Any(o=>string.Equals(o.Relative,d.Relative,StringComparison.OrdinalIgnoreCase)))).ToArray();
                    var knownWake=KnownWakeRelease.Find(issue.Key);
                    string supportHash=choice.SupportQueued?choice.SupportHash:choice.SupportPath.Length>0?ServerMapUpdates.SupportHash(choice.SupportPath):"";
                    bool needWakeSupport=knownWake!=null&&!string.Equals(supportHash,knownWake.SupportHash,StringComparison.OrdinalIgnoreCase);
                    if(needWakeSupport&&choice.SupportQueued)throw new IOException("A different Wake Flight Support version is queued. Finish it first.");
                    if(needWakeSupport&&choice.SupportRestore)throw new IOException("The NOMM-disabled Wake Flight Support needs updating. Enable it in NOMM first.");
                    if(old.Any(o=>o.Key==issue.Key)&&!choice.Loaded&&!needWakeSupport&&!(issue.MapId=="kellys-wake-island"&&knownWake==null))
                    return new MapSetupResult {Batch=batch,Restores=restores};
                    string saved=needWakeSupport||issue.MapId=="kellys-wake-island"&&knownWake==null?"":ServerMapUpdates.StageSaved(cache,plugins,issue.Key,old,token);
                    if(saved.Length>0)return new MapSetupResult {Job=saved,Batch=batch,Restores=restores};
                    var wake=knownWake;
                    if(wake!=null)
                    {
                        var pinned=GitHubInstallLink.Parse(wake.Url);
                        var checkedRelease=await updates.CheckInstallLinkAsync(pinned,token,ServerMapUpdates.MaximumMapBytes).ConfigureAwait(false);
                        if(checkedRelease.Release==null||checkedRelease.Asset==null||!string.Equals(checkedRelease.Asset.Digest,"sha256:"+wake.ArchiveHash,StringComparison.OrdinalIgnoreCase))
                            throw new IOException("The published Wake map could not be verified. Try again later.");
                        progress.Message="Downloading Wake Island...";progress.Downloading=true;
                        string archive=await updates.DownloadMapPackageAsync(pinned.Repository,checkedRelease.Release,checkedRelease.Asset,token,progress.Download,ServerMapUpdates.MaximumMapBytes,TimeSpan.FromMinutes(45)).ConfigureAwait(false);
                        progress.Downloading=false;progress.Message="Verifying Wake Island and preserving your current map...";
                        try
                        {
                            string job=ServerMapUpdates.Stage(cache,plugins,archive,checkedRelease.Asset.Name,wake.ArchiveHash,issue.Key,old,token);
                            if(needWakeSupport)ServerMapUpdates.StageWakeSupport(cache,plugins,archive,wake,choice.SupportPath,token);
                            return new MapSetupResult {Job=job,Batch=batch,Restores=restores};
                        }
                        finally{try{File.Delete(archive);}catch(IOException){}catch(UnauthorizedAccessException){}}
                    }
                    if(issue.MapId=="kellys-wake-island")
                    {
                        var link=GitHubInstallLink.Parse("https://github.com/Kelly-D-Ripper/KELLYS-Wake-Island");
                        var latest=await updates.CheckInstallLinkAsync(link,token,ServerMapUpdates.MaximumMapBytes).ConfigureAwait(false);
                        if(latest.Release==null||latest.Asset==null||!(latest.Asset.Digest??"").StartsWith("sha256:",StringComparison.OrdinalIgnoreCase))throw new IOException("Could not verify the latest Wake release. Try again.");
                        progress.Message="Downloading Wake Island...";progress.Downloading=true;
                        string archive=await updates.DownloadMapPackageAsync(link.Repository,latest.Release,latest.Asset,token,progress.Download,ServerMapUpdates.MaximumMapBytes,TimeSpan.FromMinutes(45)).ConfigureAwait(false);
                        progress.Downloading=false;progress.Message="Checking Wake Island against the server...";
                        try
                        {
                            var found=ServerMapUpdates.InspectWakeRelease(cache,archive,latest.Asset.Digest!.Substring(7),issue.Key,latest.Asset.Url,token);
                            bool needSupport=!string.Equals(supportHash,found.SupportHash,StringComparison.OrdinalIgnoreCase);
                            if(needSupport&&choice.SupportQueued)throw new IOException("A different Wake Flight Support version is queued. Finish it first.");
                            if(needSupport&&choice.SupportRestore)throw new IOException("The NOMM-disabled Wake Flight Support needs updating. Enable it in NOMM first.");
                            string job=ServerMapUpdates.Stage(cache,plugins,archive,latest.Asset.Name,found.ArchiveHash,issue.Key,old,token,found);
                            if(needSupport)ServerMapUpdates.StageWakeSupport(cache,plugins,archive,found,choice.SupportPath,token);
                            return new MapSetupResult {Job=job,Batch=batch,Restores=restores};
                        }
                        finally{try{File.Delete(archive);}catch(IOException){}catch(UnauthorizedAccessException){}}
                    }
                    if(candidates.Length==0)throw new InvalidOperationException("The map is not uniquely identified in NOMNOM. Ask the host for the download link.");
                    GitHubRelease? release=null;GitHubAsset? asset=null;NommArtifact? selected=null;string repository="";
                    using(var lookup=CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        lookup.CancelAfter(TimeSpan.FromSeconds(60));
                        foreach(var record in candidates)
                        {
                            string repo=record.Owner+"/"+record.Repository;
                            var artifacts=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Hash!=null&&a.Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)
                                &&ModUpdateFiles.HashValid(a.Hash.Substring(7))&&!string.IsNullOrEmpty(a.DownloadUrl)).Take(17).ToArray();
                            if(artifacts.Length>16)throw new InvalidOperationException("The map's release list cannot be checked automatically. Visit its GitHub page.");
                            foreach(var candidate in artifacts)
                            {
                                if(lookup.IsCancellationRequested){token.ThrowIfCancellationRequested();throw new IOException("The map catalogue check timed out. Try again.");}
                                GitHubInstallLink link;
                                try{link=GitHubInstallLink.Parse(candidate.DownloadUrl);}catch(InvalidDataException){continue;}
                                if(!link.Repository.Equals(repo,StringComparison.OrdinalIgnoreCase)||link.Tag.Length==0||link.Asset!=candidate.FileName)continue;
                                ModUpdateCheck check;
                                try{check=await updates.CheckInstallLinkAsync(link,lookup.Token,ServerMapUpdates.MaximumMapBytes).ConfigureAwait(false);}
                                catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
                                catch(OperationCanceledException) when(lookup.IsCancellationRequested){throw new IOException("The map catalogue check timed out. Try again.");}
                                catch(Exception){continue;}
                                if(check.Release==null||check.Asset==null)continue;
                                bool fullKey=ServerMapResolver.ReleaseNamesKey(check.Release,issue.Key);
                                if(!fullKey&&!(record==knownRecord&&ServerMapResolver.ReleaseNamesBuild(check.Release,issue.Build)))continue;
                                if(check.Asset.Digest==null||!check.Asset.Digest.Equals(candidate.Hash,StringComparison.OrdinalIgnoreCase))continue;
                                if(selected!=null&&(!selected.Hash.Equals(candidate.Hash,StringComparison.OrdinalIgnoreCase)||asset!.Url!=check.Asset.Url))throw new InvalidOperationException("More than one map release claims the server's build. Visit GitHub.");
                                selected=candidate;release=check.Release;asset=check.Asset;repository=repo;
                            }
                        }
                    }
                    if(release==null||asset==null||selected==null)throw new InvalidOperationException("No published map release identifies this exact server build. Visit GitHub.");
                    if(selected.Extends!=null&&selected.Extends.Id=="NOCustomMaps"&&!string.IsNullOrWhiteSpace(selected.Extends.Version)
                        &&(ModReleaseVersion.Compare(choice.Version,selected.Extends.Version) is not int comparison||comparison<0))
                        throw new InvalidOperationException("This map needs NOCustomMaps "+selected.Extends.Version+" or later. Update the loader first.");
                    progress.Message="Downloading "+issue.Name+"...";progress.Downloading=true;
                    string download=await updates.DownloadMapPackageAsync(repository,release,asset,token,progress.Download,ServerMapUpdates.MaximumMapBytes,TimeSpan.FromMinutes(45)).ConfigureAwait(false);
                    progress.Downloading=false;progress.Message="Verifying the map and preserving your current version...";
                    try {string job=ServerMapUpdates.Stage(cache,plugins,download,asset.Name,asset.Digest!.Substring(7),issue.Key,old,token);return new MapSetupResult {Job=job,Batch=batch,Restores=restores};}
                    finally {try{File.Delete(download);}catch(IOException){}catch(UnauthorizedAccessException){}}
                    }
                    catch{if(batch.Length>0)AutoSetupBatch.Abort(cache,plugins,batch);throw;}
                },token);
                string previous="";
                while(!task.IsCompleted&&generation==setupGeneration)
                {
                    if(previous!=progress.Message){previous=progress.Message;Message(target,previous);}
                    owner.SetServerDownloadProgress(target,progress.Downloading,progress.Download.Fraction);
                    yield return null;
                }
                if(generation!=setupGeneration)
                {
                    _=task.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)try{AutoSetupBatch.Abort(cache,plugins,t.Result.Batch);}catch{}},TaskScheduler.Default);
                    yield break;
                }
                if(task.IsCanceled)throw new OperationCanceledException("Map setup was cancelled.");
                if(task.IsFaulted)throw task.Exception!.GetBaseException();
                if(target.HostVersion!=wire||owner.MapIssueFor(target)!=issue)
                {AutoSetupBatch.Abort(cache,plugins,task.Result.Batch);throw new IOException("The server changed. Press Setup mods again.");}
                mapOwnedBatch=task.Result.Batch;mapSetupRestores=task.Result.Restores;
                armedRestart=Snapshot(target,expanded);armedDownloadsHeader="map|"+wire;
                WorkflowStatus(target,issue.Key=="IronjawSound"?"Ironjaw ready. Press Proceed to restart. The server does not report its map version.":"Map ready. Press Proceed to restart. Your previous map is saved.");
                owner.RecordSupport("Server map setup",issue.Key=="IronjawSound"?"Queued the published Ironjaw package. The server's map version is not advertised; previous files are preserved.":"Verified "+issue.Key+" and queued it for restart; the existing map will be preserved.");
            }
            finally
            {
                owner.SetServerDownloadProgress(target,false,0);serverDownloadCancel?.Dispose();serverDownloadCancel=null;busy=false;owner.RefreshServerActions();
            }
        }
    }
}
