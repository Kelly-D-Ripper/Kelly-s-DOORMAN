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
        private Task<NommRecord[]>? nommTask;
        private readonly CancellationTokenSource updateLifetime=new CancellationTokenSource();
        private CancellationTokenSource? pageCheck;
        private bool startupReady;
        private readonly Dictionary<ModEntry,ModUpdateCheck> updateChecks=new Dictionary<ModEntry,ModUpdateCheck>();
        private NativeModsUi? ui;
        private readonly List<GameObject> menuButtons=new List<GameObject>();
        private List<ModEntry> entries=new List<ModEntry>();
        private bool busy, retrying, temporarySelection;
        private string[]? listDisabledContent;
        internal bool IsOpen=>ui?.IsOpen==true;
        internal ModManager(ClientPlugin owner,Harmony harmony,Action<string> log)
        {
            instance=this; this.owner=owner; this.log=log; profilePath=Path.Combine(Paths.ConfigPath,"doorman-mods.json"); profile=new ModProfile();
            savedLists=new SavedModLists(Paths.PluginPath);
            knownContent=new InstalledContentMetadata(Paths.PluginPath,Paths.CachePath);
            updates=new ModUpdateService(Paths.PluginPath,Paths.CachePath);
            nomm=new NommMetadataService(Paths.CachePath);resume=ServerSetupFiles.Pending(Paths.ConfigPath);
            try { if(File.Exists(profilePath)) { profile=ModJson.Read<ModProfile>(profilePath); profile.Validate(); } }
            catch(Exception ex) { log("Keeping the normal mod set; profile was unreadable: "+ex.Message); profile=new ModProfile(); }
            loader=new BlueprinterReload(harmony,StartupProfile,log,()=>{ if(IsOpen) Refresh(); });
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
            { log("Native MainMenu scene loaded; waiting for completed startup before attaching Mods.");BeginMenuAttachment(); }
            else if(InMenuScene(scene,NuclearOption.SceneLoading.MapLoader.MultiplayerMenu))
            { log("Native MultiplayerMenu scene loaded; attaching browser controls after initialization.");owner.StartCoroutine(AttachBrowserAfterFrame(scene.handle)); }
        }
        private static void MenuPostfix(MainMenu __instance)
        {
            try
            {
                instance?.BeginMenuAttachment();
                if(instance?.temporarySelection==true) instance.owner.StartCoroutine(instance.RestoreUsualSelection());
            } catch(Exception ex) { instance?.log("Mods menu button unavailable: "+ex.Message); }
        }
        private void Attach(MainMenu menu)
        {
            var source=AccessTools.Field(typeof(MainMenu),"missionsButton").GetValue(menu) as Button;
            if(source==null) return;
            var existing=source.transform.parent.Find("DOORMAN Mods");if(existing!=null){existing.gameObject.SetActive(true);return;}
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
            log("Native Mods menu attached after completed startup.");
        }
        internal void Open()
        {
            try { if(ui==null) ui=new NativeModsUi(Toggle,ApplySelection,AutoMatchChanged,CheckUpdates,UpdateMod,ExitForUpdate,OpenSavedLists,SaveModList,LoadModList,DeleteModList,MapsChanged,SelectMap,RefreshMaps); ui.Show();Refresh(); }
            catch(Exception ex) { log("Could not open Mods: "+ex.Message); }
        }
        private void Refresh()
        {
            entries=ModCatalog.Read(loader.Content,profile,log);
            bool startup=AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="KellysDOORMANStartup").Any(a=>a.GetType("DoormanStartupPatcher")?.GetField("Ready")?.GetValue(null) is bool ready && ready);
            startupReady=startup;updateChecks.Clear();
            if(!startup) foreach(var entry in entries.Where(e=>!e.Content)) entry.Locked=true;
            ui?.Set(entries,profile.AutoMatchServers,loader.Ready?"Choose your mods, then press Reload mods.":loader.Unavailable);
            if(!startup) ui?.Status("Content controls are available. Install the full client package to change plugins.");
            if(entries.Any(e=>e.Manifest==null))
            {
                if(nommTask?.IsCompleted!=false)nommTask=Task.Run(()=>nomm.GetAsync(updateLifetime.Token),updateLifetime.Token);
                owner.StartCoroutine(FinishMetadata(entries,nommTask!));
            }
        }
        private ModProfile StartupProfile() { if(resume==null)return profile;var active=profile.Copy();active.DisabledContent=resume.DisabledContent;return active; }
        private IEnumerator FinishMetadata(List<ModEntry> target,Task<NommRecord[]> task)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled||task.IsFaulted||entries!=target||updateLifetime.IsCancellationRequested)yield break;
            ModCatalog.Enrich(target,task.Result);if(IsOpen)ui?.Set(entries,profile.AutoMatchServers,loader.Ready?"Choose your mods, then press Reload mods.":loader.Unavailable);
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
            var task=Task.Run(()=>updates.CheckAsync(item.UpdateRepository,item.UpdateAsset,item.UpdateVersion,item.UpdatePath,token),token);
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
            updateChecks[item]=result;ui?.UpdateFor(item,result);
        }
        private void UpdateMod(ModEntry item)
        {
            if(busy||!startupReady||item.UpdatePath.Length==0||!updateChecks.TryGetValue(item,out var check)||!check.Newer||check.Asset==null||check.Release==null) return;
            string reason=MenuSafety();if(reason.Length>0) { ui?.Status(reason);return; }
            busy=true;ui?.Busy(true,"Downloading "+item.Name+". It will install when the game next starts.");pageCheck?.Cancel();
            int percent=0;
            var task=Task.Run(()=>updates.StageAsync(item.UpdateRepository,item.UpdatePlugin,item.UpdatePath,check.Release,check.Asset,updateLifetime.Token,p=>Volatile.Write(ref percent,p)),updateLifetime.Token);
            owner.StartCoroutine(FinishUpdate(item,task,()=>Volatile.Read(ref percent)));
        }
        private IEnumerator FinishUpdate(ModEntry item,Task task,Func<int> progress)
        {
            int last=-1;
            while(!task.IsCompleted) { int current=progress();if(current!=last) { last=current;ui?.Status("Downloading "+item.Name+": "+current+"%. Restart will apply the update."); }yield return null; }
            busy=false;ui?.Busy(false);
            if(task.IsCanceled) { ui?.Status("Update cancelled. The installed mod is unchanged.");yield break; }
            if(task.IsFaulted) { string message=task.Exception!.GetBaseException().Message;log("Mod update was not installed: "+message);ui?.Status(message);ui?.UpdateFor(item,new ModUpdateCheck { Message=message,Newer=updateChecks[item].Newer,Release=updateChecks[item].Release,Asset=updateChecks[item].Asset });yield break; }
            var result=new ModUpdateCheck { Queued=true,Message="Update downloaded. Restart Nuclear Option to install it. Your previous DLL will be kept as a backup." };updateChecks[item]=result;ui?.UpdateFor(item,result);ui?.Status("Download ready. Press Reload mods to restart and install it.");
        }
        private void ExitForUpdate()
        {
            if(busy) return;string reason=MenuSafety();if(reason.Length>0) { ui?.Status(reason);return; }ApplySelection();
        }
        private void Toggle(ModEntry entry)
        {
            if(busy||entry.Locked) return;
            entry.Wanted=!entry.Wanted; ui?.RefreshSelection();
        }
        private void AutoMatchChanged(bool value)
        {
            if(busy) return;
            var next=profile.Copy(); next.AutoMatchServers=value;
            try { ModJson.Save(profilePath,next); profile=next; }
            catch(Exception ex) { ui?.Status("Could not save that setting: "+ex.Message); ui?.SetAutoMatch(profile.AutoMatchServers); }
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
            try { ApplySelectionCore(additionalDisabled); }catch(Exception ex) { log("Mod selection unavailable: "+ex.Message);ui?.Status(ex.Message); }
        }
        private void ApplySelectionCore(string[]? additionalDisabled=null)
        {
            if(busy) return;
            string reason=MenuSafety();if(reason.Length==0&&!loader.LoadingFinished)reason="Wait for Blueprinter to finish loading before applying mods."; if(reason.Length==0) reason=ModCatalog.ValidatePlugins(entries);
            if(reason.Length>0) { ui?.Status(reason); return; }
            var next=profile.Copy(); next.DisabledContent=entries.Where(e=>e.Content&&!e.Wanted).Select(e=>e.Id).Concat(profile.DisabledContent.Where(id=>!entries.Any(e=>e.Content&&e.Id==id))).Concat((additionalDisabled??Array.Empty<string>()).Where(id=>!entries.Any(e=>e.Content&&e.Id==id))).Distinct().ToArray();
            next.DisabledPlugins=entries.Where(e=>!e.Content&&!e.Wanted&&!ModProfile.Protected(e.Id)).Select(e=>e.Id).Concat(profile.DisabledPlugins.Where(id=>!entries.Any(e=>!e.Content&&e.Id==id))).Distinct().ToArray();
            bool contentChanged=entries.Any(e=>e.Content&&e.Enabled!=e.Wanted);
            var pending=PendingUpdates();
            bool needsRestart=pending.Length>0||entries.Any(e=>!e.Content&&e.Enabled!=e.Wanted)||contentChanged&&(!loader.Ready||entries.Any(e=>e.Content&&e.Enabled!=e.Wanted&&!loader.Content.Any(c=>c.Id==e.Id&&c.Reloadable)));
            if(needsRestart)
            {
                if(!StartupAvailable()){ui?.Status("Install the full client package and its helpers before restarting to apply mods.");return;}
                string message=pending.Length>0?"Downloaded mod updates are ready. Reload cannot replace running plugin code. Restart to install the updates and apply your selection. Your old DLLs will be backed up.":"These plugin or content changes need a game restart. Restart to load your selection.";
                ui?.PromptRestart(message,()=>RestartMods(next,pending));return;
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
            catch(Exception ex) { log("Mod selection failed: "+ex.Message); ui?.Status(ex.Message); }
            finally { busy=false; ui?.Busy(false); }
        }
        private void ApplyNow(ModProfile next,bool contentChanged)
        {
                string safety=MenuSafety(); if(safety.Length>0) { ui?.Status(safety); return; }
                var previous=loader.Content.Where(c=>!c.Enabled).Select(c=>c.Id).ToArray();
                if(contentChanged) { string error=loader.Apply(next.DisabledContent); if(error.Length>0) { ui?.Status(error); return; } }
                try { ModJson.Save(profilePath,next); profile=next; temporarySelection=false;listDisabledContent=null; }
                catch
                {
                    if(contentChanged) { string rollback=loader.Apply(previous); if(rollback.Length>0) throw new InvalidOperationException("Could not save or restore the old profile. Restart before playing."); }
                    throw;
                }
                bool restart=entries.Any(e=>!e.Content&&e.Enabled!=e.Wanted);
                Refresh(); ui?.Status(restart?"Content updated. Plugin changes are saved and need a game restart.":"Mods updated. Ready to play.");
        }
        private IEnumerator RestoreUsualSelection()
        {
            // This short wait is created only when returning from a matched server.
            // It does not poll during flight, and never starts or stops a host.
            float until=Time.unscaledTime+3;
            while(MenuSafety().Length>0 && Time.unscaledTime<until) yield return null;
            if(busy || MenuSafety().Length>0 || !temporarySelection) yield break;
            string error=loader.Apply(profile.DisabledContent);
            if(error.Length==0) temporarySelection=false; else log("Usual mod profile needs attention: "+error);
        }
        internal void Tick() { if(IsOpen) { ui!.Tick();TickMaps(); } }
        public void Dispose() { CloseMaps();CancelRestartHandshake();updateLifetime.Cancel();pageCheck?.Cancel();pageCheck?.Dispose();updates.Dispose();nomm.Dispose();updateLifetime.Dispose();SceneManager.sceneLoaded-=SceneLoaded; ui?.Dispose(); foreach(var button in menuButtons) if(button) UnityEngine.Object.Destroy(button); BlueprinterReload.Dispose(); if(instance==this) instance=null; }
    }
}
