using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SceneLoading;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private GameObject? updateNoticeRoot;
        private MainMenu? updateNoticeMenu;
        private TMP_Text? updateNoticeTitle,updateNoticeBody;
        private Button? updateNoticeSelf,updateNoticeMods;
        private ModUpdateCheck? noticeSelf;
        private readonly HashSet<string> noticeUpdates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> noticeQueued=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int noticeModCount=>noticeUpdates.Count;
        private bool noticeStarted,noticeManual,noticeDismissed;

        // One bounded check per launch, only after the native menu and loader
        // finish startup. Opening the menu or returning to it does not poll.
        private void AttachUpdateNotice(MainMenu menu)
        {
            updateNoticeMenu=menu;
            if(!noticeStarted)
            {
                noticeStarted=true;
                try
                {
                    bool server=File.Exists(Path.Combine(Paths.PluginPath,"KellysJOINCHECKServer.dll"));
                    selfAnnouncementTask=CheckSelf(updateLifetime.Token,server);
                    owner.StartCoroutine(FinishNoticeSelf(selfAnnouncementTask));
                    var content=loader.Content.ToArray();var selected=profile.Copy();
                    var task=Task.Run(async ()=>
                    {
                        var inventory=ModCatalog.Read(content,selected,_=>{});
                        try{ModCatalog.Enrich(inventory,await nomm.GetAsync(updateLifetime.Token).ConfigureAwait(false));}
                        catch(OperationCanceledException){throw;}catch{ /* Existing manifests still work offline. */ }
                        return ModMenuEntries.Visible(inventory).Where(e=>e.Id!="kelly.nuclearoption.joincheck"
                            &&GitHubRepository.Valid(e.UpdateRepository)&&ModReleaseVersion.Compare(e.UpdateVersion,e.UpdateVersion)==0)
                            .GroupBy(e=>e.UpdateRepository+"\n"+e.UpdateVersion,StringComparer.OrdinalIgnoreCase)
                            .Select(g=>g.First()).Take(128).ToArray();
                    },updateLifetime.Token);
                    owner.StartCoroutine(CheckNoticeMods(task));
                }
                catch(Exception ex){log("Main menu update check unavailable: "+ex.Message);}
            }
            RefreshUpdateNotice();
        }
        private IEnumerator FinishNoticeSelf(Task<ModUpdateCheck> task)
        {
            while(!task.IsCompleted)yield return new WaitForSecondsRealtime(.2f);
            if(task.Status==TaskStatus.RanToCompletion){if(noticeSelf?.Queued!=true)noticeSelf=task.Result;ui?.SetAvailableUpdates(noticeUpdates,noticeSelf?.Newer==true&&noticeSelf?.Queued!=true);RefreshUpdateNotice();}
            else if(task.IsFaulted)log("DOORMAN update check unavailable: "+task.Exception!.GetBaseException().Message);
        }
        private IEnumerator CheckNoticeMods(Task<ModEntry[]> inventory)
        {
            while(!inventory.IsCompleted)yield return new WaitForSecondsRealtime(.2f);
            if(inventory.Status!=TaskStatus.RanToCompletion)yield break;
            foreach(var entry in inventory.Result)
            {
                if(updateLifetime.IsCancellationRequested)yield break;
                // Checks and filesystem work use the existing shared cache and
                // request gate; no network operation runs on Unity's thread.
                var check=Task.Run(()=>updates.CheckAsync(entry.UpdateRepository,entry.UpdateAsset,entry.UpdateVersion,entry.UpdatePath,updateLifetime.Token),updateLifetime.Token);
                while(!check.IsCompleted)yield return new WaitForSecondsRealtime(.2f);
                if(check.IsCanceled)yield break;
                if(check.Status!=TaskStatus.RanToCompletion)continue;
                NoticeModCheck(entry,check.Result);
                if(check.Result.Message.IndexOf("slow down",StringComparison.OrdinalIgnoreCase)>=0)yield break;
            }
        }
        private void NoticeModCheck(ModEntry item,ModUpdateCheck result)
        {
            if(item.Id=="kelly.nuclearoption.joincheck")
            {
                if(noticeSelf?.Queued!=true||result.Queued)noticeSelf=result;
                if(result.Queued){noticeManual=false;selfAnnouncementTask=Task.FromResult(result);}
            }
            else
            {
                string key=ModMenuEntries.UpdateKey(item);
                if(result.Queued)noticeQueued.Add(key);
                if(result.Newer&&!result.Queued&&!noticeQueued.Contains(key))noticeUpdates.Add(key);
                else noticeUpdates.Remove(key);
            }
            ui?.SetAvailableUpdates(noticeUpdates,noticeSelf?.Newer==true&&noticeSelf?.Queued!=true);
            RefreshUpdateNotice();
        }
        private void RefreshUpdateNotice()
        {
            bool self=noticeSelf?.Newer==true||noticeSelf?.Queued==true;
            if(!self&&noticeModCount==0){if(updateNoticeRoot)updateNoticeRoot!.SetActive(false);return;}
            try
            {
                if(!updateNoticeRoot)CreateUpdateNotice();
                if(!updateNoticeRoot)return;
                updateNoticeTitle!.text=self?(noticeSelf!.Queued?"DOORMAN UPDATE READY":"DOORMAN UPDATE AVAILABLE"):"MOD UPDATES AVAILABLE";
                string extra=noticeModCount>0?noticeModCount+(self?" other mod":" mod")+(noticeModCount==1?"":"s")+" can be updated.":"";
                updateNoticeBody!.text=self?(noticeSelf!.Queued?"DOORMAN is ready to install. ":(noticeManual||noticeSelf.Asset==null||!StartupAvailable()?"Close the game to install the update. ":"DOORMAN "+Diagnostics.Clean(noticeSelf.Release?.Tag??"")+" is available. "))+extra:extra;
                updateNoticeSelf!.gameObject.SetActive(self);
                updateNoticeSelf.GetComponentInChildren<TMP_Text>(true).text=noticeSelf?.Queued==true?"Restart to update":noticeManual||noticeSelf?.Asset==null||!StartupAvailable()?"Open GitHub":"Update DOORMAN";
                updateNoticeMods!.gameObject.SetActive(noticeModCount>0);
                float bodyHeight=Mathf.Max(34,updateNoticeBody.GetPreferredValues(updateNoticeBody.text,316,0).y+4);
                updateNoticeBody.rectTransform.sizeDelta=new Vector2(316,bodyHeight);
                float buttonTop=45+bodyHeight;
                ((RectTransform)updateNoticeSelf.transform).anchoredPosition=new Vector2(12,-buttonTop);
                ((RectTransform)updateNoticeMods.transform).anchoredPosition=new Vector2(12,-buttonTop-(self?42:0));
                var panel=(RectTransform)updateNoticeTitle.transform.parent;
                panel.sizeDelta=new Vector2(340,buttonTop+(self&&noticeModCount>0?84:42));
                TickUpdateNotice();
            }
            catch(Exception ex){log("Main menu update notice unavailable: "+ex.Message);DestroyUpdateNotice();}
        }
        private void CreateUpdateNotice()
        {
            var source=Resources.Load<GameObject>("JoinLobbyOverlayCanvas").GetComponent<JoinLobbyOverlay>();
            var font=(TMP_Text)AccessTools.Field(typeof(JoinLobbyOverlay),"bodyText").GetValue(source);
            var root=new GameObject("DOORMAN update notice",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster));
            updateNoticeRoot=root;UnityEngine.Object.DontDestroyOnLoad(root);root.SetActive(false);
            var canvas=root.GetComponent<Canvas>();canvas.renderMode=RenderMode.ScreenSpaceOverlay;canvas.sortingOrder=100;
            var scaler=root.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=1;
            var panel=NoticeRect("Update panel",root.transform,0,0,340,118);panel.anchorMin=panel.anchorMax=panel.pivot=new Vector2(.5f,.5f);panel.anchoredPosition=Vector2.zero;
            panel.gameObject.AddComponent<Image>().color=new Color(.13f,.17f,.19f,.97f);
            var outline=panel.gameObject.AddComponent<Outline>();outline.effectColor=new Color(.36f,.42f,.46f,1);outline.effectDistance=new Vector2(1,-1);
            updateNoticeTitle=NoticeText("Title",panel,font,18,12,-10,278,26);updateNoticeTitle.color=LobbyListItem.StatusSuccessColor;
            NoticeButton("X",panel,font,300,-8,30,28,()=>{noticeDismissed=true;TickUpdateNotice();},false);
            updateNoticeBody=NoticeText("Status",panel,font,16,12,-37,316,34);
            updateNoticeSelf=NoticeButton("Update DOORMAN",panel,font,12,-76,316,32,NoticeUpdateSelf,true);
            updateNoticeMods=NoticeButton("View mod updates",panel,font,12,-118,316,32,()=>{Open();ui?.ShowUpdatesOnly();},false);
        }
        private static RectTransform NoticeRect(string name,Transform parent,float x,float y,float width,float height)
        {
            var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.SetParent(parent,false);
            rect.anchorMin=rect.anchorMax=rect.pivot=new Vector2(0,1);rect.anchoredPosition=new Vector2(x,y);rect.sizeDelta=new Vector2(width,height);return rect;
        }
        private static TMP_Text NoticeText(string name,Transform parent,TMP_Text font,float size,float x,float y,float width,float height)
        {
            var text=NoticeRect(name,parent,x,y,width,height).gameObject.AddComponent<TextMeshProUGUI>();
            text.font=font.font;text.fontSharedMaterial=font.fontSharedMaterial;text.fontSize=size;text.color=Color.white;text.richText=false;text.raycastTarget=false;text.enableWordWrapping=true;text.overflowMode=TextOverflowModes.Ellipsis;return text;
        }
        private static Button NoticeButton(string label,Transform parent,TMP_Text font,float x,float y,float width,float height,UnityEngine.Events.UnityAction action,bool green)
        {
            var rect=NoticeRect(label,parent,x,y,width,height);var image=rect.gameObject.AddComponent<Image>();image.color=green?LobbyListItem.StatusSuccessColor:new Color(.20f,.24f,.26f,1);
            var button=rect.gameObject.AddComponent<Button>();button.targetGraphic=image;button.onClick.AddListener(action);var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
            var text=NoticeText("Label",rect,font,16,4,-1,width-8,height-2);text.text=label;text.alignment=TextAlignmentOptions.Center;text.enableWordWrapping=false;text.enableAutoSizing=true;text.fontSizeMin=12;text.fontSizeMax=16;if(green)text.color=new Color(.08f,.12f,.10f,1);return button;
        }
        private void TickUpdateNotice()
        {
            if(!updateNoticeRoot)return;
            bool wanted=updateNoticeMenu&&updateNoticeMenu!.isActiveAndEnabled&&InMenuScene(SceneManager.GetActiveScene(),MapLoader.MainMenu)
                &&!noticeDismissed&&!IsOpen&&(noticeSelf?.Newer==true||noticeSelf?.Queued==true||noticeModCount>0);
            if(updateNoticeRoot!.activeSelf!=wanted)updateNoticeRoot.SetActive(wanted);
        }
        private void DestroyUpdateNotice(){if(updateNoticeRoot)UnityEngine.Object.Destroy(updateNoticeRoot);updateNoticeRoot=null;}
        private void NoticeUpdateSelf()
        {
            if(busy||noticeSelf==null)return;
            if(noticeManual||noticeSelf.Asset==null&&!noticeSelf.Queued||!StartupAvailable())
            {Application.OpenURL(NativeModsUi.ReleasePage(SelfUpdateRelease.Repository,noticeSelf.Release?.Url));return;}
            if(MenuSafety().Length>0)return;
            Open();if(ui?.IsOpen!=true)return;
            var item=entries.FirstOrDefault(e=>e.Id=="kelly.nuclearoption.joincheck"&&!e.Content);
            if(item==null){ui.Status("DOORMAN could not find its own installed plugin. Open GitHub for the full package.");return;}
            ui.SelectEntry(item);updateChecks[item]=noticeSelf;
            if(noticeSelf.Queued){RestartMods(profile.Copy(),PendingUpdates());return;}
            try
            {
                BeginSelfUpdate(item,noticeSelf,()=>RestartMods(profile.Copy(),PendingUpdates()),message=>
                {
                    if(message.IndexOf("helpers or server component",StringComparison.OrdinalIgnoreCase)>=0)
                    {noticeManual=true;RefreshUpdateNotice();}
                });
            }
            catch(Exception ex)
            {
                if(ex.Message.IndexOf("nonstandard location",StringComparison.OrdinalIgnoreCase)>=0){noticeManual=true;RefreshUpdateNotice();}
                ui.Status("Could not update DOORMAN: "+Diagnostics.Clean(ex.Message));
            }
        }
    }
}
