using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager : IDisposable
    {
        private static ModManager? instance;
        private readonly ClientPlugin owner;
        private readonly Action<string> log;
        private readonly string profilePath;
        private readonly SavedModLists savedLists;
        private readonly InstalledContentMetadata knownContent;
        private ModProfile profile;
        private readonly BlueprinterReload loader;
        private readonly ModUpdateService updates;
        private readonly NommMetadataService nomm;
        private readonly ServerSetupTicket? resume;
        private readonly string externalRestoreFailure="";
        private Task<NommRecord[]>? nommTask;
        private Task<NosmrReport.Index>? nosmrIndexTask;
        private readonly CancellationTokenSource updateLifetime=new CancellationTokenSource();
        private CancellationTokenSource? pageCheck;
        private CancellationTokenSource? githubInstall;
        private const string GameplayZipMarker="DOORMAN_GAMEPLAY_ZIP";
        private bool startupReady;
        private readonly Dictionary<ModEntry,ModUpdateCheck> updateChecks=new Dictionary<ModEntry,ModUpdateCheck>();
        private NativeModsUi? ui;
        private readonly List<GameObject> menuButtons=new List<GameObject>();
        private List<ModEntry> entries=new List<ModEntry>();
        private bool busy, retrying, temporarySelection, restartedServerConnected, restoringUsualSelection;
        private string normalSelectionNotice="";
        private string[]? listDisabledContent;
        internal bool IsOpen=>ui?.IsOpen==true;
        internal bool ServerSetupBusy=>busy;
        internal bool ContentLoadingFinished=>loader.LoadingFinished;
        internal NosmrReport.Index? NosmrIndex()
        {
            if(nosmrIndexTask==null) nosmrIndexTask=Task.Run(async ()=>new NosmrReport.Index(await nomm.GetAsync(updateLifetime.Token)));
            return nosmrIndexTask.Status==TaskStatus.RanToCompletion?nosmrIndexTask.Result:null;
        }
        internal string NosmrIndexState()
        {
            if(nosmrIndexTask==null)return "not started";
            if(nosmrIndexTask.IsCanceled)return "cancelled";
            if(nosmrIndexTask.IsFaulted)return "failed";
            return nosmrIndexTask.IsCompleted?"ready":"loading";
        }
        internal Task<string> DownloadReported(NommRecord record,string version,string? expectedOwner=null,string? expectedTarget=null,CancellationToken? cancellation=null)
        {
            string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
            if(!loader.LoadingFinished)throw new InvalidOperationException("Wait for mods to finish loading before downloading.");
            bool startup=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="KellysDOORMANStartup").Any(a=>a.GetType("DoormanStartupPatcher")?.GetField("Ready")?.GetValue(null) is bool value && value);
            if(!startup)throw new InvalidOperationException("Install the full DOORMAN client package first.");
            RefreshExternalUnits();
            if(externalUnits.Any(u=>u.Error.Length>0))throw new InvalidOperationException("A NOMM-disabled mod could not be inspected. Use NOMM or install this mod manually.");
            string[] disabled=ExternalEntries().Where(e=>!e.Content).Select(e=>e.Id).ToArray();
            var artifacts=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Version==version).Take(2).ToArray();
            if(artifacts.Length!=1)throw new InvalidOperationException("The host's exact version is unavailable. Visit GitHub.");
            var index=NosmrIndex()??throw new InvalidOperationException("Mod information is still loading.");
            var dependencies=(artifacts[0].Dependencies??Array.Empty<NommDependency>()).Concat(artifacts[0].Extends==null?Array.Empty<NommDependency>():new[]{artifacts[0].Extends!}).ToArray();
            if(dependencies.Length>64||dependencies.Any(d=>d==null||string.IsNullOrWhiteSpace(d.Id)))throw new InvalidOperationException("This release has unsupported prerequisites. Visit GitHub.");
            foreach(var dependency in dependencies)
            {
                string local=ReportedLocalVersion(index,dependency.Id);
                int? comparison=string.IsNullOrWhiteSpace(dependency.Version)?0:ModReleaseVersion.Compare(local,dependency.Version);
                if(local.Length==0||comparison==null||comparison<0)throw new InvalidOperationException("Install or enable prerequisite "+Diagnostics.Clean(dependency.Id)+(string.IsNullOrWhiteSpace(dependency.Version)?"":" "+Diagnostics.Clean(dependency.Version)+" or newer")+" first.");
            }
            if(artifacts.Length==1&&(artifacts[0].FileName??"").EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))
            {
                if(!StandaloneBundleUpdates.InventoryAvailable(false,loader.LoadingFinished,loader.Ready,loader.InventoryCaptured))throw new InvalidOperationException("Content inventory is unavailable. Install this bundle manually.");
                var token=cancellation??updateLifetime.Token;
                return Task.Run(()=>updates.DownloadReportedBundleAsync(record,version,token),token);
            }
            var inventory=ModCatalog.Read(loader.Content,profile,log);
            var installed=NommMetadata.InstalledPluginForRecord(index.Records.Values,inventory,record.Id);
            string plugin=installed?.Id??"",target=installed?.Path??"";
            if(expectedOwner!=null&&(!string.Equals(expectedOwner,plugin,StringComparison.Ordinal)||!string.Equals(expectedTarget??"",target,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The installed package changed while preparing the download. Press Setup mods again.");
            var pluginToken=cancellation??updateLifetime.Token;
            if((artifacts[0].FileName??"").EndsWith(".zip",StringComparison.OrdinalIgnoreCase))return QueueGameplayZip(record,version,plugin,target,pluginToken);
            return Task.Run(()=>updates.StageReportedAsync(record,version,disabled,pluginToken,plugin,target),pluginToken);
        }
        private void InstallGitHub(string text)
        {
            if(busy)return;
            try
            {
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                if(!startupReady)throw new InvalidOperationException("Install the complete DOORMAN package before adding mods.");
                var link=GitHubInstallLink.Parse(text);
                githubInstall=CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
                var token=githubInstall.Token;
                busy=true;pageCheck?.Cancel();ui?.Busy(true,"Checking GitHub...");ui?.InstallCancelable(true);
                var task=Task.Run(()=>updates.CheckInstallLinkAsync(link,token),token);
                owner.StartCoroutine(FinishGitHubCheck(link,task));
            }
            catch(Exception ex){EndGitHubInstall("Could not install: "+Diagnostics.Clean(ex.Message));}
        }
        private void CancelGitHubInstall()
        {
            var operation=githubInstall;if(operation==null||operation.IsCancellationRequested)return;
            ui?.InstallCancelling();ui?.Status("Cancelling download...");
            try{operation.Cancel();}catch(Exception ex){log("Could not cancel GitHub install: "+ex.Message);}
        }
        private void EndGitHubInstall(string message)
        {
            busy=false;var operation=githubInstall;githubInstall=null;operation?.Dispose();
            ui?.InstallCancelable(false);ui?.Busy(false,message);
        }
        private void RecordGitHubFailure(string stage,Exception error)
        { owner.RecordSupport("GitHub "+stage,RestartLaunchFailure.Evidence(error),true); }
        private System.Collections.IEnumerator FinishGitHubCheck(GitHubInstallLink link,Task<ModUpdateCheck> task)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled||task.IsFaulted&&githubInstall?.IsCancellationRequested==true){EndGitHubInstall("Download cancelled.");yield break;}
            if(task.IsFaulted){RecordGitHubFailure("release check",task.Exception!);EndGitHubInstall("Could not check GitHub: "+Diagnostics.Clean(task.Exception!.GetBaseException().Message));yield break;}
            try
            {
                var token=githubInstall?.Token??throw new OperationCanceledException("The install was cancelled.");
                token.ThrowIfCancellationRequested();
                var check=task.Result;var release=check.Release;var asset=check.Asset;
                if(release==null)throw new InvalidOperationException(check.Message);
                if(asset==null)throw new InvalidOperationException("Choose a release with one supported DLL, NOBP or ZIP. Install other packages manually from GitHub.");
                if(asset.Digest==null||!asset.Digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.HashValid(asset.Digest.Substring(7)))throw new InvalidOperationException("GitHub did not provide a SHA-256 checksum. Install this release manually.");
                string[] repo=link.Repository.Split('/');
                var record=new NommRecord { Id=link.Repository,Name=link.Repository,Owner=repo[0],Repository=repo[1],Artifacts=new[]{new NommArtifact { Version=release.Tag,FileName=asset.Name,DownloadUrl=asset.Url,Hash=asset.Digest }} };
                if(MapPackageInstaller.NeedsMapInstall(link.Repository,asset.Name))
                {
                    if(!loader.LoadingFinished)throw new InvalidOperationException("Wait for mods to finish loading before installing a map.");
                    RefreshExternalUnits();if(externalUnits.Any(u=>u.Error.Length>0))throw new InvalidOperationException("A NOMM-disabled mod could not be inspected. Install this map manually.");
                    string[] disabledMap=ExternalEntries().Where(e=>!e.Content).Select(e=>e.Id).ToArray();
                    string[] doormanDisabled=profile.DisabledPlugins.Concat(entries.Where(e=>!e.Content&&!e.Wanted).Select(e=>e.Id)).Distinct(StringComparer.Ordinal).ToArray();
                    var running=BepInEx.Bootstrap.Chainloader.PluginInfos;
                    bool hasNoMap=running.TryGetValue("NOMapLoader",out var existingLoader),hasCustom=running.ContainsKey("com.javoski.custommaps");
                    string noMapDirectory=hasNoMap?Path.GetDirectoryName(existingLoader.Location):Paths.PluginPath;
                    string noMapVersion=hasNoMap?existingLoader.Metadata.Version.ToString():"";
                    var queuedMapLoaders=PendingUpdates().Where(p=>p.Install&&(p.Plugin=="NOMapLoader"||p.Plugin=="com.javoski.custommaps")).Select(p=>new {p.Plugin,p.Version}).ToArray();
                    string cache=Paths.CachePath,plugins=Paths.PluginPath,missions=Path.Combine(Application.persistentDataPath,"Missions");
                    ui?.Status("Downloading "+asset.Name+"...");
                    bool loaderQueued=false;var progress=new ServerDownloadProgress();
                    var mapTask=Task.Run(async ()=>
                    {
                        string downloaded=await updates.DownloadMapPackageAsync(link.Repository,release,asset,token,progress).ConfigureAwait(false);
                        try
                        {
                            if(!MapPackageInstaller.IsMapArchive(link.Repository,asset.Name,downloaded))
                                return GameplayZipMarker;
                            bool noMap=MapPackageInstaller.UsesNOMapLoader(link.Repository,asset.Name,downloaded);
                            string dependency=noMap?"NOMapLoader":"com.javoski.custommaps";
                            if(disabledMap.Contains(dependency,StringComparer.Ordinal))throw new InvalidOperationException(dependency+" is disabled in NOMM. Enable it there first.");
                            if(doormanDisabled.Contains(dependency,StringComparer.Ordinal))throw new InvalidOperationException(dependency+" is disabled. Enable it in Mods and restart first.");
                            string queued=queuedMapLoaders.FirstOrDefault(p=>p.Plugin==dependency)?.Version??"";
                            if(noMap&&link.Repository.Equals("Talamander/ironjaw-sound",StringComparison.OrdinalIgnoreCase)&&((noMapVersion.Length>0&&ModReleaseVersion.Compare(noMapVersion,"1.0.5")<0)||(queued.Length>0&&ModReleaseVersion.Compare(queued,"1.0.5")<0)))throw new InvalidOperationException("Ironjaw needs NOMapLoader 1.0.5 or newer. Update it first.");
                            bool available=(noMap?hasNoMap:hasCustom)||queued.Length>0;
                            if(!available)
                            {
                                string loaderRepo=noMap?"MinecrackTyler/NOMapLoader":"BatongMaster/NOCustomMaps";
                                var loaderLink=GitHubInstallLink.Parse("https://github.com/"+loaderRepo);
                                var loaderCheck=await updates.CheckInstallLinkAsync(loaderLink,token).ConfigureAwait(false);
                                if(loaderCheck.Release==null||loaderCheck.Asset==null||loaderCheck.Asset.Digest==null)throw new InvalidOperationException("Could not verify "+dependency+". Install it manually first.");
                                var loaderAsset=loaderCheck.Asset;string[] loaderParts=loaderRepo.Split('/');
                                var loaderRecord=new NommRecord {Id=dependency,Owner=loaderParts[0],Repository=loaderParts[1],Artifacts=new[]{new NommArtifact {Version=loaderCheck.Release.Tag,FileName=loaderAsset.Name,DownloadUrl=loaderAsset.Url,Hash=loaderAsset.Digest}}};
                                await updates.StageReportedAsync(loaderRecord,loaderCheck.Release.Tag,disabledMap,token,dependency).ConfigureAwait(false);
                                loaderQueued=true;
                            }
                            return MapPackageInstaller.Install(link.Repository,asset.Name,downloaded,asset.Digest,cache,plugins,missions,disabledMap,token,noMapDirectory);
                        }
                        finally { try{File.Delete(downloaded);}catch(IOException){}catch(UnauthorizedAccessException){} }
                    },token);
                    owner.StartCoroutine(FinishMapInstall(mapTask,()=>loaderQueued,record,release.Tag,token,progress,asset.Name));yield break;
                }
                bool bundle=asset.Name.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase);
                if(!loader.LoadingFinished)throw new InvalidOperationException("Wait for mods to finish loading before downloading.");
                RefreshExternalUnits();if(externalUnits.Any(u=>u.Error.Length>0))throw new InvalidOperationException("A NOMM-disabled mod could not be inspected. Install this mod manually.");
                string[] disabled=ExternalEntries().Where(e=>!e.Content).Select(e=>e.Id).ToArray();
                if(bundle&&!loader.Ready)throw new InvalidOperationException("Blueprinter content inventory is unavailable. Install this bundle manually.");
                var download=bundle?Task.Run(()=>updates.DownloadReportedBundleAsync(record,release.Tag,token),token):asset.Name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)?QueueGameplayZip(record,release.Tag,"","",token):Task.Run(()=>updates.StageReportedAsync(record,release.Tag,disabled,token),token);
                ui?.DownloadProgress(true,-1);ui?.Status("Downloading "+asset.Name+"...");owner.StartCoroutine(FinishGitHubInstall(record,release.Tag,bundle,download));
            }
            catch(OperationCanceledException){EndGitHubInstall("Download cancelled.");}
            catch(Exception ex){EndGitHubInstall("Could not install: "+Diagnostics.Clean(ex.Message));}
        }
        private System.Collections.IEnumerator FinishGitHubInstall(NommRecord record,string version,bool bundle,Task<string> task)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled||task.IsFaulted&&githubInstall?.IsCancellationRequested==true){EndGitHubInstall("Download cancelled. Nothing was installed.");yield break;}
            if(task.IsFaulted){RecordGitHubFailure("download or installation",task.Exception!);string message=Diagnostics.Clean(task.Exception!.GetBaseException().Message);log("GitHub install failed: "+message);EndGitHubInstall("Could not install: "+message);yield break;}
            try
            {
                if(bundle){string installed=InstallReportedBundle(record,version,task.Result);EndGitHubInstall("Content "+installed+" downloaded. Restart the game to install it.");}
                else{RefreshRequisiteQueue();EndGitHubInstall("Download ready. Press Reload mods to restart and install it.");}
            }
            catch(Exception ex){if(bundle)DiscardReportedBundle(task.Result);log("GitHub install failed: "+ex.Message);EndGitHubInstall("Could not install: "+Diagnostics.Clean(ex.Message));}
        }
        private System.Collections.IEnumerator FinishMapInstall(Task<string> task,Func<bool> loaderQueued,NommRecord record,string version,CancellationToken token,ServerDownloadProgress progress,string name)
        {
            int previous=-2;
            while(!task.IsCompleted)
            {
                if(githubInstall?.IsCancellationRequested!=true&&ui?.IsOpen==true)
                {
                    int percent=progress.Fraction<0?-1:(int)(progress.Fraction*100);
                    if(percent!=previous){previous=percent;ui.DownloadProgress(true,percent);ui.Status(percent<0?"Downloading "+name+"...":percent<100?"Downloading "+name+"... "+percent+"%":"Download complete. Preparing files...");}
                }
                yield return new WaitForSecondsRealtime(.25f);
            }
            string note=loaderQueued()?" The map loader download is still queued.":"";
            if(task.IsCanceled||task.IsFaulted&&token.IsCancellationRequested){RefreshRequisiteQueue();EndGitHubInstall("Installation cancelled."+note);yield break;}
            if(task.IsFaulted){RecordGitHubFailure("package download or installation",task.Exception!);string message=Diagnostics.Clean(task.Exception!.GetBaseException().Message);log("Package install failed: "+message);RefreshRequisiteQueue();EndGitHubInstall("Could not install: "+message+note);yield break;}
            if(task.Result==GameplayZipMarker)
            {
                try{ui?.Status("Checking mod package...");var next=QueueGameplayZip(record,version,"","",token);owner.StartCoroutine(FinishGitHubInstall(record,version,false,next));}
                catch(Exception ex){EndGitHubInstall("Could not install: "+Diagnostics.Clean(ex.Message));}
                yield break;
            }
            RefreshRequisiteQueue();EndGitHubInstall(task.Result+" Close and restart Nuclear Option through Steam.");
        }
        internal string ReportedLocalVersion(NosmrReport.Index index,string registryId)
        {
            var records=index.Records.Values;var content=new List<string>();
            foreach(var item in loader.Content.Where(c=>c.Enabled))
            {
                try {
                    string owner="";
                    if(item.Source.StartsWith("resource:",StringComparison.OrdinalIgnoreCase))
                    {var parts=item.Source.Split(new[]{':'},3);if(parts.Length==3)owner=parts[1];}
                    var match=NommMetadata.Match(records,owner.Length>0?owner:item.Id,"",owner.Length>0?"":Path.GetFileName(item.Source));
                    if(match?.Id==registryId)content.Add(item.Version);
                }catch(ArgumentException){}
            }
            var contentVersions=content.Distinct(StringComparer.Ordinal).Take(2).ToArray();
            if(contentVersions.Length==1)return contentVersions[0];if(contentVersions.Length>1)return "";
            var plugins=new List<string>();
            foreach(var pair in BepInEx.Bootstrap.Chainloader.PluginInfos)
            {
                try { var match=NommMetadata.Match(records,pair.Key,"",Path.GetFileName(pair.Value.Location));if(pair.Key==registryId||match?.Id==registryId)plugins.Add(pair.Value.Metadata.Version.ToString()); }catch(ArgumentException){}
            }
            var versions=plugins.Distinct(StringComparer.Ordinal).Take(2).ToArray();return versions.Length==1?versions[0]:"";
        }
        internal void DiscardReportedBundle(string downloaded)
        {
            try
            {
                string root=ModUpdateFiles.Within(Paths.CachePath,Path.Combine("DOORMAN-updates","downloads"));
                string file=ModUpdateFiles.Within(root,Path.GetFileName(downloaded));
                if(Path.GetFullPath(downloaded).Equals(file,StringComparison.OrdinalIgnoreCase)&&File.Exists(file))File.Delete(file);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}catch(ArgumentException){}
        }
        internal string InstallReportedBundle(NommRecord record,string version,string downloaded)
            =>StageReportedBundle(record,version,downloaded,"","",false);
        internal string StageReportedBundle(NommRecord record,string version,string downloaded,string expectedId,string expectedVersion,bool exactHashProven,string? verifiedTarget=null,string? verifiedArtifactName=null)
        {
            string root=ModUpdateFiles.Within(Paths.CachePath,Path.Combine("DOORMAN-updates","downloads"));
            string file=ModUpdateFiles.Within(root,Path.GetFileName(downloaded));
            if(!Path.GetFullPath(downloaded).Equals(file,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Invalid downloaded bundle location.");
            try
            {
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                var inventory=owner.LocalGameplayFiles;
                if(!StandaloneBundleUpdates.InventoryAvailable(exactHashProven,loader.LoadingFinished,loader.Ready,exactHashProven?inventory.Ready&&inventory.Complete:loader.InventoryCaptured))throw new InvalidOperationException("Content inventory is unavailable. Install this bundle manually.");
                var artifacts=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Version==version&&(!exactHashProven||string.IsNullOrEmpty(verifiedArtifactName)||a.FileName==verifiedArtifactName)).Take(2).ToArray();
                if(artifacts.Length!=1)throw new InvalidOperationException("The reported version is no longer unique.");
                var asset=artifacts[0];if(asset.Hash==null||!asset.Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.Hash(file).Equals(asset.Hash.Substring(7),StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("The downloaded bundle changed. Download it again.");
                DisabledBundleInfo info;
                if(exactHashProven)
                {
                    // Only the batch setup workflow may assert this: it has
                    // already matched the entire selected-content fingerprint,
                    // including these exact SHA-256 bytes and this ID/version.
                    if(!GameplayFingerprintPolicy.Text(expectedId,GameplayFingerprintPolicy.MaxId)||!GameplayFingerprintPolicy.Text(expectedVersion,GameplayFingerprintPolicy.MaxVersion))throw new InvalidOperationException("The server content identity is invalid.");
                    info=new DisabledBundleInfo {Id=expectedId,Version=expectedVersion};
                }
                else
                {
                    try { using(var stream=File.OpenRead(file))info=DisabledContentManifestReader.Read(stream); }
                    catch(Exception ex) { throw new InvalidOperationException("Could not inspect the downloaded content: "+Diagnostics.Clean(ex.Message),ex); }
                    if(expectedId.Length>0&&!info.Id.Equals(expectedId,StringComparison.Ordinal)||expectedVersion.Length>0&&!info.Version.Equals(expectedVersion,StringComparison.Ordinal))throw new InvalidOperationException("The downloaded content does not match the server's required ID and version.");
                }
                RefreshExternalUnits();if(externalUnits.Any(u=>u.Error.Length>0))throw new InvalidOperationException("A NOMM-disabled mod could not be inspected. Install this bundle manually.");
                if(ExternalEntries().Any(e=>e.Content&&e.Id==info.Id))throw new InvalidOperationException("This content is disabled in NOMM. Enable or update it there.");
                if(string.IsNullOrWhiteSpace(asset.FileName)||Path.GetFileName(asset.FileName)!=asset.FileName||!asset.FileName.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Invalid bundle filename.");
                var installed=loader.Content.Where(c=>c.Id==info.Id).Take(2).ToArray();
                if(installed.Length>1)throw new InvalidOperationException("Several installed content files have this ID. Install it manually.");
                string target;
                if(installed.Length==1)
                {
                    string path=installed[0].Source;
                    if(path.StartsWith("resource:",StringComparison.OrdinalIgnoreCase)||!Path.IsPathRooted(path)||!path.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("This content is embedded or has an unknown location. Update its owning plugin manually.");
                    StandaloneBundleUpdates.RelativeTarget(Paths.PluginPath,path);target=path;
                }
                else
                {
                    target=ModUpdateFiles.Within(Paths.PluginPath,asset.FileName);
                    if(File.Exists(target)||Directory.Exists(target))throw new InvalidOperationException("A file with this name already exists. Install this bundle manually.");
                }
                if(exactHashProven && verifiedTarget!=null && (verifiedTarget.Length==0 ? installed.Length!=0 : installed.Length!=1 || !Path.GetFullPath(target).Equals(Path.GetFullPath(verifiedTarget),StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The installed content location changed during setup. Press Setup mods again.");
                StandaloneBundleUpdates.Stage(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath,file,target,info.Id,info.Version,asset.Hash.Substring(7),updateLifetime.Token);
                return info.Version;
            }
            finally{try{if(File.Exists(file))File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        internal string DiagnosticCompatibility(out string notice,out bool wireVerified)=>Compatibility.ForDiagnostics(loader.LoadingFinished,loader.Ready,loader.Unavailable,out notice,out wireVerified);
        internal ModManager(ClientPlugin owner,Harmony harmony,Action<string> log)
        {
            instance=this; this.owner=owner;
            var sink=log;this.log=message=>{owner.RecordSupport("DOORMAN",message);sink(message);};log=this.log;
            profilePath=Path.Combine(Paths.ConfigPath,"doorman-mods.json"); profile=new ModProfile();
            savedLists=new SavedModLists(Paths.PluginPath);
            knownContent=new InstalledContentMetadata(Paths.PluginPath,Paths.CachePath);
            updates=new ModUpdateService(Paths.PluginPath,Paths.CachePath);
            updates.DiagnosticFailure=RecordGitHubFailure;
            nomm=new NommMetadataService(Paths.CachePath);
            foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="KellysDOORMANStartup"))
                if(assembly.GetType("DoormanStartupPatcher")?.GetField("ExternalRestoreError")?.GetValue(null) is string error&&error.Length>0)
                {externalRestoreFailure=error;owner.RecordSupport("NOMM startup restore",error,true);log(error);}
            resume=externalRestoreFailure.Length==0?ServerSetupFiles.Pending(Paths.ConfigPath):null;
            try { if(File.Exists(profilePath)) { profile=ModJson.Read<ModProfile>(profilePath); profile.Validate(); } }
            catch(Exception ex) { log("Keeping the normal mod set; profile was unreadable: "+ex.Message); profile=new ModProfile(); }
            loader=new BlueprinterReload(harmony,StartupProfile,log,()=>{ owner.NotifyGameplayLoaderCompleted();if(IsOpen) Refresh(); });
            if(resume!=null) owner.StartCoroutine(GuardServerReturn(ResumeServerPage()));
            harmony.Patch(AccessTools.Method(typeof(MainMenu),"Start"),postfix:new HarmonyMethod(typeof(ModManager),nameof(MenuPostfix)));
            BeginMenuAttachment();
            SceneManager.sceneLoaded+=SceneLoaded;
        }
        private void SceneLoaded(Scene scene,LoadSceneMode mode)
        {
            log("DOORMAN scene event: name="+scene.name+"; path="+scene.path+"; mode="+mode);
            if(IsOpen) ui!.Hide();
            if(InMenuScene(scene,NuclearOption.SceneLoading.MapLoader.MainMenu))
            { log("Native MainMenu scene loaded; waiting for completed startup before attaching Mods.");BeginMenuAttachment();StartUsualRestore(); }
            else if(InMenuScene(scene,NuclearOption.SceneLoading.MapLoader.MultiplayerMenu))
            { log("Native MultiplayerMenu scene loaded; attaching browser controls after initialization.");owner.StartCoroutine(AttachBrowserAfterFrame(scene.handle)); }
        }
        private static void MenuPostfix(MainMenu __instance)
        {
            try
            {
                instance?.BeginMenuAttachment();
                instance?.StartUsualRestore();
            } catch(Exception ex) { instance?.log("Mods menu button unavailable: "+ex.Message); }
        }
        private void Attach(MainMenu menu)
        {
            var source=AccessTools.Field(typeof(MainMenu),"missionsButton").GetValue(menu) as Button;
            if(source==null) return;
            var existing=source.transform.parent.Find("DOORMAN Mods");if(existing!=null){existing.gameObject.SetActive(true);AttachUpdateNotice(menu);return;}
            var go=UnityEngine.Object.Instantiate(source.gameObject,source.transform.parent,false); go.name="DOORMAN Mods";
            var button=go.GetComponent<Button>(); button.onClick=new Button.ButtonClickedEvent(); button.onClick.AddListener(Open);
            go.SetActive(true);button.enabled=true;button.interactable=source.interactable;
            foreach(var text in go.GetComponentsInChildren<TMPro.TMP_Text>(true)) text.text="MODS";
            foreach(var text in go.GetComponentsInChildren<UnityEngine.UI.Text>(true)) text.text="MODS";
            // Use the menu's own vertical layout where present. Older menus place
            // their buttons explicitly; in that case use the native row spacing.
            var parent=source.transform.parent;
            var group=parent.GetComponent<VerticalLayoutGroup>();
            if(group!=null)
            {
                go.transform.SetAsLastSibling(); LayoutRebuilder.ForceRebuildLayoutImmediate((RectTransform)parent);
                var bounds=(RectTransform)parent; bounds.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical,Mathf.Max(bounds.rect.height,group.preferredHeight));
            }
            else
            {
                var peers=parent.GetComponentsInChildren<Button>(true).Where(b=>b!=button && b.transform.parent==parent && b.onClick.GetPersistentEventCount()>0 && Enumerable.Range(0,b.onClick.GetPersistentEventCount()).Any(i=>b.onClick.GetPersistentMethodName(i).StartsWith("Select",StringComparison.Ordinal))).Select(b=>(RectTransform)b.transform).OrderByDescending(r=>r.anchoredPosition.y).ToList();
                if(peers.Count<2) { UnityEngine.Object.Destroy(go); throw new InvalidOperationException("Native menu row positions could not be found."); }
                var rect=(RectTransform)go.transform; var last=peers.Last(); float step=Mathf.Abs(peers[0].anchoredPosition.y-peers[1].anchoredPosition.y);
                rect.anchorMin=last.anchorMin; rect.anchorMax=last.anchorMax; rect.pivot=last.pivot; rect.sizeDelta=last.sizeDelta; rect.anchoredPosition=last.anchoredPosition-new Vector2(0,step);
            }
            menuButtons.Add(go);
            AttachUpdateNotice(menu);
            log("Native Mods menu attached after completed startup.");
        }
        internal void Open()
        {
            try { if(ui!=null&&!ui.IsAlive){CloseMaps();ui.Dispose();ui=null;} if(ui==null) ui=new NativeModsUi(Toggle,ApplySelection,AutoMatchChanged,CheckUpdates,UpdateMod,ExitForUpdate,OpenSavedLists,SaveModList,LoadModList,DeleteModList,MapsChanged,SelectMap,RefreshMaps,ModRequisiteIssues,MapRequisiteIssues,RequisiteAction,ModDeclarations,MapDeclarations,InstallGitHub,ShowSavedVersions,UseSavedVersion,CancelGitHubInstall,DeleteAddon); ui.Show();Refresh();ui.Busy(busy); }
            catch(Exception ex) { log("Could not open Mods: "+ex.Message); }
        }
        private void Refresh()
        {
            entries=ModCatalog.Read(loader.Content,profile,log);
            RefreshExternalUnits();entries.AddRange(ExternalEntries().Select(ExternalCopy));ModCatalog.SetUpdateSources(entries);
            bool startup=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="KellysDOORMANStartup").Any(a=>a.GetType("DoormanStartupPatcher")?.GetField("Ready")?.GetValue(null) is bool ready && ready);
            startupReady=startup;updateChecks.Clear();RefreshRequisiteQueue();
            if(!startup) foreach(var entry in entries.Where(e=>!e.Content)) entry.Locked=true;
            RenderModList();CheckSelfOnMenu();
            if(entries.Any(e=>e.Manifest==null))
            {
                if(nommTask?.IsCompleted!=false)nommTask=Task.Run(()=>nomm.GetAsync(updateLifetime.Token),updateLifetime.Token);
                owner.StartCoroutine(FinishMetadata(entries,nommTask!));
            }
        }
        private void RenderModList()
        {
            try{ui?.SetPendingRemovalPaths(AddonRemovalQueue.PendingPaths(Paths.CachePath,Paths.BepInExRootPath));}
            catch(Exception ex){SupportFailure("Addon deletion status",ex);ui?.Status("Deletion queue needs attention. See Recovery.");}
            ui?.Set(entries,profile.AutoMatchServers,loader.LoadingFinished?"Choose your mods, then press Reload mods.":"Mods are still loading. Please wait.");
            ui?.SetAvailableUpdates(noticeUpdates,noticeSelf?.Newer==true&&noticeSelf?.Queued!=true);
            if(normalSelectionNotice.Length>0)ui?.Status(normalSelectionNotice);
            if(externalUnits.Any(u=>u.Error.Length>0))ui?.Status("Some NOMM-disabled mods could not be inspected. Re-enable those in NOMM and restart; Copy report includes the reason.");
            if(externalRestoreFailure.Length>0)ui?.Status(externalRestoreFailure+" Normal selection kept. Review NOMM files, then use Reload mods to retry.");
            if(!startupReady)ui?.Status("Content controls are available. Install the full client package to change plugins.");
        }
        private ModProfile StartupProfile() { if(resume==null)return profile;var active=profile.Copy();active.DisabledContent=resume.DisabledContent;return active; }
        private IEnumerator FinishMetadata(List<ModEntry> target,Task<NommRecord[]> task)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled||task.IsFaulted||entries!=target||updateLifetime.IsCancellationRequested)yield break;
            ModCatalog.Enrich(target,task.Result);if(IsOpen)RenderModList();
        }
        private void CheckUpdates(ModEntry item)
        {
            pageCheck?.Cancel();pageCheck?.Dispose();pageCheck=CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
            var token=pageCheck.Token;
            if(item.Manifest?.FromNomm==true&&item.Manifest.Image.Length==0&&item.Manifest.RemoteImage.Length>0)
            { var image=Task.Run(()=>nomm.ImageAsync(item.Manifest.RemoteImage,token),token);owner.StartCoroutine(FinishImage(item,image,token)); }
            if(item.UpdateRepository.Length==0) { ui?.UpdateFor(item,new ModUpdateCheck { Message="No GitHub repository supplied for this mod." });return; }
            // Disk, HTTP, checksums and Cecil work run outside Unity's thread. A
            // coroutine exists only while an explicitly opened page is checking.
            Task<ModUpdateCheck> task;
            try { task=item.Id=="kelly.nuclearoption.joincheck"?CheckSelf(token,SelfHasServer()):Task.Run(()=>updates.CheckAsync(item.UpdateRepository,item.UpdateAsset,item.UpdateVersion,item.UpdatePath,token),token); }
            catch(Exception ex){ui?.UpdateFor(item,new ModUpdateCheck{Message=ex.Message});return;}
            owner.StartCoroutine(FinishCheck(item,task,token));
        }
        private IEnumerator FinishImage(ModEntry item,Task<string?> task,CancellationToken token)
        {
            while(!task.IsCompleted)yield return null;
            if(token.IsCancellationRequested||task.IsCanceled||task.IsFaulted||task.Result==null||item.Manifest==null)yield break;
            item.Manifest.Directory=Path.GetDirectoryName(task.Result)!;item.Manifest.Image=Path.GetFileName(task.Result);ui?.RefreshPreview(item);
        }
        private IEnumerator FinishCheck(ModEntry item,Task<ModUpdateCheck> task,CancellationToken token)
        {
            while(!task.IsCompleted) { if(ui?.IsOpen!=true) { pageCheck?.Cancel();yield break; }yield return null; }
            if(token.IsCancellationRequested||task.IsCanceled) yield break;
            var result=task.IsFaulted?new ModUpdateCheck { Message="Could not check GitHub. Your installed mod is unchanged." }:task.Result;
            if(!startupReady&&result.Newer) { result.Asset=null;result.Message+=" Install the full client package to enable in-game updates."; }
            updateChecks[item]=result;NoticeModCheck(item,result);ui?.UpdateFor(item,result);
        }
        private void UpdateMod(ModEntry item)
        {
            if(busy||!startupReady||item.UpdatePath.Length==0||!updateChecks.TryGetValue(item,out var check)||!check.Newer||check.Asset==null||check.Release==null) return;
            string reason=MenuSafety();if(reason.Length>0) { ui?.Status(reason);return; }
            if(item.Id=="kelly.nuclearoption.joincheck"){try{BeginSelfUpdate(item,check);}catch(Exception ex){ui?.Status(ex.Message);}return;}
            busy=true;ui?.Busy(true,"Downloading "+item.Name+". It will install when the game next starts.");pageCheck?.Cancel();
            int percent=0;
            var task=Task.Run(()=>updates.StageAsync(item.UpdateRepository,item.UpdatePlugin,item.UpdatePath,check.Release,check.Asset,updateLifetime.Token,p=>Volatile.Write(ref percent,p)),updateLifetime.Token);
            owner.StartCoroutine(FinishUpdate(item,task,()=>Volatile.Read(ref percent)));
        }
        private void ShowSavedVersions(ModEntry item)
        {
            if(busy)return;
            ui?.Status("Checking saved versions...");
            var task=Task.Run(async ()=>{NommRecord[] records;try{records=await nomm.GetAsync(updateLifetime.Token).ConfigureAwait(false);}catch(OperationCanceledException){throw;}catch{records=Array.Empty<NommRecord>();}return SavedModVersions.Read(Paths.CachePath,Paths.PluginPath,item,records);},updateLifetime.Token);
            owner.StartCoroutine(FinishSavedVersions(item,task));
        }
        private IEnumerator FinishSavedVersions(ModEntry item,Task<SavedModVersion[]> task)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled)yield break;
            if(task.IsFaulted){ui?.Status("Could not read saved versions: "+Diagnostics.Clean(task.Exception!.GetBaseException().Message));yield break;}
            ui?.ShowVersions(item,task.Result);
        }
        private void UseSavedVersion(ModEntry item,SavedModVersion version)
        {
            if(busy)return;
            try
            {
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                if(!loader.LoadingFinished||!StartupAvailable())throw new InvalidOperationException("Wait for mods to finish loading and check that the DOORMAN startup helper is installed.");
                SavedModVersions.Stage(Paths.CachePath,Paths.PluginPath,version);
                ui?.Status("Version queued. Press Reload mods to restart and switch. Your current version will be backed up.");
            }
            catch(Exception ex){ui?.Status("Could not switch version: "+Diagnostics.Clean(ex.Message));}
        }
        private IEnumerator FinishUpdate(ModEntry item,Task task,Func<int> progress,Action? afterDownload=null,Action<string>? failed=null)
        {
            updateChecks.TryGetValue(item,out var previousCheck);
            int last=-1;
            ui?.DownloadProgress(true,0);
            while(!task.IsCompleted) { int current=progress();if(current!=last) { last=current;ui?.DownloadProgress(true,current);ui?.Status("Downloading "+item.Name+(current<0?"...":": "+current+"%.")+" Restart will apply the update."); }yield return null; }
            busy=false;ui?.Busy(false);
            if(task.IsCanceled) { ui?.Status("Update cancelled. The installed mod is unchanged.");yield break; }
            if(task.IsFaulted) { string message=task.Exception!.GetBaseException().Message;log("Mod update was not installed: "+message);ui?.Status(message);ui?.UpdateFor(item,new ModUpdateCheck { Message=message,Newer=previousCheck?.Newer==true,Release=previousCheck?.Release,Asset=item.Id=="kelly.nuclearoption.joincheck"?null:previousCheck?.Asset });failed?.Invoke(message);yield break; }
            var result=new ModUpdateCheck { Queued=true,Message="Update downloaded. Restart Nuclear Option to install it. Your previous files will be kept as a backup." };updateChecks[item]=result;NoticeModCheck(item,result);ui?.UpdateFor(item,result);RefreshRequisiteQueue();ui?.RefreshSelection();ui?.Status("Download ready. Press Reload mods to restart and install it.");
            if(afterDownload!=null&&ui?.IsOpen==true&&MenuSafety().Length==0)afterDownload();
        }
        private void ExitForUpdate()
        {
            if(busy) return;string reason=MenuSafety();if(reason.Length>0) { ui?.Status(reason);return; }ApplySelection();
        }
        private void Toggle(ModEntry entry)
        {
            if(busy||entry.Locked) return;
            ModMenuEntries.ToggleGroup(entry,entries);ui?.RefreshSelection();
            if(entry.Wanted&&entry.ExternalDisabled)
            {
                try{RefreshRequisiteQueue();SelectExternalDependencies(new[]{entry});}
                catch(Exception ex){entry.Wanted=false;SupportFailure("NOMM prerequisite selection",ex);ui?.Status(ex.Message);}
                ui?.RefreshSelection();
            }
        }
        private void AutoMatchChanged(bool value)
        {
            if(busy) return;
            var next=profile.Copy(); next.AutoMatchServers=value;
            try { ModJson.Save(profilePath,next); profile=next; }
            catch(Exception ex) { SupportFailure("Auto match setting save",ex);ui?.Status("Could not save that setting: "+ex.Message); ui?.SetAutoMatch(profile.AutoMatchServers); }
        }
        internal static string MenuSafety()
        {
            var manager=NetworkManagerNuclearOption.i;
            if(manager!=null && (manager.Client.Active||manager.Server.Active||NetworkManagerNuclearOption.IsLoadingScene)) return "Leave the mission or server before reloading mods.";
            var scene=SceneManager.GetActiveScene();
            if(!InMenuScene(scene,NuclearOption.SceneLoading.MapLoader.MainMenu)&&!InMenuScene(scene,NuclearOption.SceneLoading.MapLoader.MultiplayerMenu)) return "Return to the main menu before reloading mods.";
            return "";
        }
        private void ApplySelection()
        {
            var additionalDisabled=listDisabledContent;
            try { ApplySelectionCore(additionalDisabled); }catch(Exception ex) { SupportFailure("Mod selection",ex);log("Mod selection unavailable: "+ex.Message);ui?.Status(ex.Message); }
        }
        private void ApplySelectionCore(string[]? additionalDisabled=null)
        {
            if(busy) return;
            bool selfPending=false;
            try{selfPending=SelfUpdateTransaction.Pending(Paths.CachePath);}
            catch(Exception ex){log("DOORMAN update receipt needs recovery: "+ex.Message);owner.RecordSupport("DOORMAN self-update","Update receipt could not be read. See Recovery or install the full package.",true);ui?.Status("DOORMAN update needs recovery. See Recovery or install the full package.");}
            if(selfPending&&!loader.LoadingFinished){PromptSelfManualRestart();return;}
            RefreshRequisiteQueue();
            SelectExternalDependencies();
            string reason=MenuSafety();if(reason.Length==0&&!loader.LoadingFinished)reason="Wait for Blueprinter to finish loading before applying mods."; if(reason.Length==0) reason=ValidateRequisiteSelection(entries);
            if(reason.Length>0) { owner.RecordSupport("Mod selection",reason,true);ui?.Status(reason); return; }
            var next=profile.Copy(); next.DisabledContent=entries.Where(e=>e.Content&&!e.Wanted).Select(e=>e.Id).Concat(profile.DisabledContent.Where(id=>!entries.Any(e=>e.Content&&e.Id==id))).Concat((additionalDisabled??Array.Empty<string>()).Where(id=>!entries.Any(e=>e.Content&&e.Id==id))).Distinct().ToArray();
            next.DisabledPlugins=entries.Where(e=>!e.Content&&!e.Wanted&&!ModProfile.Protected(e.Id)).Select(e=>e.Id).Concat(profile.DisabledPlugins.Where(id=>!entries.Any(e=>!e.Content&&e.Id==id))).Distinct().ToArray();
            bool contentChanged=entries.Any(e=>e.Content&&e.Enabled!=e.Wanted);
            var pending=PendingUpdates();
            var pendingContent=StandaloneBundleUpdates.Pending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath);
            next.DisabledPlugins=next.DisabledPlugins.Except(pending.Where(p=>p.Install).Select(p=>p.Plugin),StringComparer.Ordinal).ToArray();
            bool removalPending=AddonRemovalQueue.Pending(Paths.CachePath,Paths.BepInExRootPath);
            bool needsRestart=removalPending||selfPending||entries.Any(e=>e.ExternalDisabled&&e.Wanted)||pending.Length>0||pendingContent.Length>0||entries.Any(e=>!e.Content&&e.Enabled!=e.Wanted)||contentChanged&&(!loader.Ready||entries.Any(e=>e.Content&&e.Enabled!=e.Wanted&&!loader.Content.Any(c=>c.Id==e.Id&&c.Reloadable)));
            if(needsRestart)
            {
                if(!StartupAvailable()){ui?.Status("Install the full client package and its helpers before restarting to apply mods.");return;}
                string message=removalPending?"Deletion is ready. Restart to remove the confirmed addon files and apply your selection.":selfPending||pending.Length>0||pendingContent.Length>0?"Downloaded updates are ready. Restart to install them and apply your selection. Your previous files will be backed up.":"These plugin or content changes need a game restart. Restart to load your selection.";
                if(RuntimePlatform.IsWine)ui?.PromptConfirmation("RESTART REQUIRED",message+"\n\nClose the game, then reopen it through Steam.","Close game",()=>RestartMods(next,pending,true));
                else ui?.PromptRestart(message,()=>RestartMods(next,pending),()=>RestartMods(next,pending,true));
                return;
            }
            busy=true; ui?.Busy(true,"Applying your mod selection..."); owner.StartCoroutine(ApplyAfterFrame(next,contentChanged));
        }
        private IEnumerator ApplyAfterFrame(ModProfile next,bool contentChanged)
        {
            yield return null;
            try
            {
                ApplyNow(next,contentChanged);
            }
            catch(Exception ex) { SupportFailure("Mod selection reload",ex);log("Mod selection failed: "+ex.Message); ui?.Status(ex.Message); }
            finally { busy=false; ui?.Busy(false); }
        }
        private void ApplyNow(ModProfile next,bool contentChanged)
        {
                string safety=MenuSafety(); if(safety.Length>0) { ui?.Status(safety); return; }
                var previous=loader.Content.Where(c=>!c.Enabled).Select(c=>c.Id).ToArray();
                if(contentChanged) { string error=loader.Apply(next.DisabledContent); if(error.Length>0) { owner.RecordSupport("Mod selection reload",error,true);ui?.Status(error); return; } }
                try { ModJson.Save(profilePath,next); profile=next; temporarySelection=false;listDisabledContent=null; }
                catch
                {
                    if(contentChanged) { string rollback=loader.Apply(previous); if(rollback.Length>0) throw new InvalidOperationException("Could not save or restore the old profile. Restart before playing."); }
                    throw;
                }
                bool restart=entries.Any(e=>!e.Content&&e.Enabled!=e.Wanted);
                Refresh(); ui?.Status(restart?"Content updated. Plugin changes are saved and need a game restart.":"Mods updated. Ready to play.");
        }
        private void StartUsualRestore()
        {
            if(!temporarySelection||restoringUsualSelection)return;
            restoringUsualSelection=true;owner.StartCoroutine(RestoreUsualSelection());
        }
        private IEnumerator RestoreUsualSelection()
        {
            try
            {
            // Wait for the menu and loader rather than restoring into a loading scene.
            float until=Time.unscaledTime+30;
            while((MenuSafety().Length>0||!loader.LoadingFinished||busy) && Time.unscaledTime<until) yield return new WaitForSecondsRealtime(.25f);
            if(!temporarySelection)yield break;
            if(busy || MenuSafety().Length>0 || !loader.LoadingFinished)
            {normalSelectionNotice="Your usual mods could not be restored yet. Open Mods or restart the game to load your saved selection.";owner.RecordSupport("Usual mod selection",normalSelectionNotice,true);if(IsOpen)ui?.Status(normalSelectionNotice);yield break;}
            string error=loader.Apply(profile.DisabledContent);
            if(error.Length==0) temporarySelection=false;
            else { normalSelectionNotice="Could not restore your usual content. Open Mods and restart to load your saved selection.";log("Usual mod profile needs attention: "+error); }
            if(normalSelectionNotice.Length>0) { owner.RecordSupport("Usual mod selection",normalSelectionNotice,error.Length>0);if(IsOpen)ui?.Status(normalSelectionNotice); }
            }
            finally { restoringUsualSelection=false; }
        }
        internal void Tick()
        {
            TickUpdateNotice();
            // The resumed server selection must remain active on the initial
            // menu. Arm restoration only after a real connection begins.
            if(resume!=null&&!restartedServerConnected&&NetworkManagerNuclearOption.i!=null&&NetworkManagerNuclearOption.i.Client.Active)
            {
                restartedServerConnected=true;temporarySelection=true;
                bool pluginsDiffer=!new HashSet<string>(resume.DisabledPlugins,StringComparer.Ordinal).SetEquals(profile.DisabledPlugins);
                if(resume.Restores.Length>0) normalSelectionNotice="NOMM-disabled mods were enabled for the server. Disable them again in NOMM after closing the game. Restart to restore other plugin changes.";
                else if(pluginsDiffer) normalSelectionNotice="Your usual plugin selection needs a game restart. Open Mods to review it.";
            }
            if(IsOpen) { ui!.Tick();TickMaps(); }
        }
        public void Dispose() { CloseMaps();CancelRestartHandshake();CancelOwnedRestart();updateLifetime.Cancel();githubInstall?.Dispose();githubInstall=null;pageCheck?.Cancel();pageCheck?.Dispose();updates.Dispose();nomm.Dispose();updateLifetime.Dispose();SceneManager.sceneLoaded-=SceneLoaded; ui?.Dispose();DestroyUpdateNotice(); foreach(var button in menuButtons) if(button) UnityEngine.Object.Destroy(button); BlueprinterReload.Dispose(); if(instance==this) instance=null; }
    }
}

