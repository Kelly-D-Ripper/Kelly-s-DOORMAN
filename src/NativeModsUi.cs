using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed class NativeModsUi : IDisposable
    {
        private readonly Action<ModEntry> toggle;
        private readonly Action<ModEntry> selectedChanged,updateMod;
        private readonly Action<string> saveList;
        private readonly Action<SavedModList> loadList,deleteList;
        private readonly Action<bool> mapsChanged;
        private readonly Action<MapEntry> selectedMap;
        private readonly UnityAction refreshMaps;
        private readonly GameObject root;
        private readonly RectTransform panel,listContent,previewContent,statusContent;
        private readonly TMP_Text font,status,previewName,previewByline,description,headerHelp;
        private readonly GameObject modListPane,modPreviewPane,modStatusPane;
        private readonly TMP_Text updateStatus;
        private readonly RectTransform updateBox;
        private readonly Button updateButton,releaseButton,exitButton;
        private ModUpdateCheck? updateCheck;
        private readonly RawImage picture;
        private readonly ScrollRect previewScroll,statusScroll;
        private readonly RectTransform pictureBox,links;
        private readonly Toggle autoMatch;
        private readonly Button reload,back,listsButton,modsTab,mapsTab;
        private NativeModListsUi? savedLists;
        private NativeMapsUi? mapsUi;
        private readonly GameObject restartShade;
        private readonly RectTransform restartDialog;
        private readonly TMP_Text restartBody,restartTitle;
        private readonly RectTransform restartBodyViewport,restartBodyContent;
        private readonly ScrollRect restartScroll;
        private readonly Button restartProceed,restartCancel;
        private UnityAction? restartAction;
        private readonly List<GameObject> rows=new List<GameObject>(),linkButtons=new List<GameObject>();
        private readonly Dictionary<ModEntry,Toggle> checks=new Dictionary<ModEntry,Toggle>();
        private readonly Dictionary<ModEntry,TMP_Text> states=new Dictionary<ModEntry,TMP_Text>();
        private IReadOnlyList<ModEntry> entries=Array.Empty<ModEntry>();
        private ModEntry? selected;
        private Texture2D? texture;
        private Vector2 layoutSize;
        private bool busy,oldVisible,mapstab;
        private CursorLockMode oldLock;
        private int oldScene;
        internal bool IsOpen=>root!=null && root.activeSelf;
        internal bool ListsOpen=>savedLists?.IsOpen==true;
        internal bool IsMapsOpen=>IsOpen&&mapstab;
        internal NativeModsUi(Action<ModEntry> toggle,UnityAction reloadMods,UnityAction<bool> matchChanged,Action<ModEntry> selectedChanged,Action<ModEntry> updateMod,UnityAction exitGame,UnityAction openLists,Action<string> saveList,Action<SavedModList> loadList,Action<SavedModList> deleteList,Action<bool> mapsChanged,Action<MapEntry> selectedMap,UnityAction refreshMaps)
        {
            this.toggle=toggle;this.selectedChanged=selectedChanged;this.updateMod=updateMod;
            this.saveList=saveList;this.loadList=loadList;this.deleteList=deleteList;
            this.mapsChanged=mapsChanged;this.selectedMap=selectedMap;this.refreshMaps=refreshMaps;
            var source=Resources.Load<GameObject>("JoinLobbyOverlayCanvas").GetComponent<JoinLobbyOverlay>();
            font=(TMP_Text)AccessTools.Field(typeof(JoinLobbyOverlay),"bodyText").GetValue(source);
            root=new GameObject("DOORMAN Mods",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster)); root.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(root);
            try
            {
            var canvas=root.GetComponent<Canvas>(); canvas.renderMode=RenderMode.ScreenSpaceOverlay; canvas.sortingOrder=30010;
            var scaler=root.GetComponent<CanvasScaler>(); scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution=new Vector2(1920,1080); scaler.matchWidthOrHeight=1;
            var backdrop=Rect("Backdrop",root.transform); Stretch(backdrop); backdrop.gameObject.AddComponent<Image>().color=new Color(0,0,0,.72f);
            panel=Rect("Mods",root.transform); panel.anchorMin=new Vector2(.08f,.08f); panel.anchorMax=new Vector2(.92f,.92f); panel.offsetMin=panel.offsetMax=Vector2.zero; panel.gameObject.AddComponent<Image>().color=new Color(.16f,.20f,.22f,1);
            var outline=panel.gameObject.AddComponent<Outline>(); outline.effectColor=new Color(.36f,.42f,.46f,1); outline.effectDistance=new Vector2(1,-1);
            modsTab=Button("Mods",panel,()=>SwitchMaps(false));mapsTab=Button("Maps",panel,()=>SwitchMaps(true));
            var modsRect=(RectTransform)modsTab.transform;modsRect.anchorMin=modsRect.anchorMax=modsRect.pivot=new Vector2(0,1);modsRect.sizeDelta=new Vector2(112,40);modsRect.anchoredPosition=new Vector2(24,-18);
            var mapsRect=(RectTransform)mapsTab.transform;mapsRect.anchorMin=mapsRect.anchorMax=mapsRect.pivot=new Vector2(0,1);mapsRect.sizeDelta=new Vector2(112,40);mapsRect.anchoredPosition=new Vector2(144,-18);
            headerHelp=Text("Help",panel,18);headerHelp.text="Tick what you want. Reload. Go fly.";headerHelp.color=LobbyListItem.TextMutedColor;Top(headerHelp.rectTransform,26,65,-26,30);
            listsButton=Button("Saved lists",panel,()=>{if(!busy)openLists();});var listsRect=(RectTransform)listsButton.transform;listsRect.anchorMin=listsRect.anchorMax=listsRect.pivot=Vector2.one;listsRect.sizeDelta=new Vector2(172,38);listsRect.anchoredPosition=new Vector2(-24,-24);
            var list=Rect("Installed mods",panel); list.anchorMin=new Vector2(0,0); list.anchorMax=new Vector2(.42f,1); list.offsetMin=new Vector2(24,152); list.offsetMax=new Vector2(-10,-112);
            modListPane=list.gameObject;listContent=Scroll(list);
            var preview=Rect("Preview",panel); preview.anchorMin=new Vector2(.42f,0); preview.anchorMax=Vector2.one; preview.offsetMin=new Vector2(10,152); preview.offsetMax=new Vector2(-24,-112); preview.gameObject.AddComponent<Image>().color=new Color(.12f,.15f,.17f,1);
            modPreviewPane=preview.gameObject;previewContent=Scroll(preview);previewScroll=preview.GetComponent<ScrollRect>();
            previewName=Text("Mod name",previewContent,26); Top(previewName.rectTransform,18,16,-18,40);
            previewByline=Text("Author",previewContent,17); previewByline.color=LobbyListItem.TextMutedColor; Top(previewByline.rectTransform,18,64,-18,32);
            pictureBox=Rect("Image",previewContent); Top(pictureBox,18,106,-18,280); pictureBox.gameObject.AddComponent<Image>().color=new Color(.10f,.12f,.14f,1);
            var image=Rect("Artwork",pictureBox); Stretch(image); picture=image.gameObject.AddComponent<RawImage>(); picture.raycastTarget=false;
            var aspect=image.gameObject.AddComponent<AspectRatioFitter>(); aspect.aspectMode=AspectRatioFitter.AspectMode.FitInParent;
            description=Text("Description",previewContent,20); description.enableWordWrapping=true;
            updateBox=Rect("GitHub update",previewContent);updateBox.gameObject.AddComponent<Image>().color=new Color(.16f,.20f,.22f,1);
            updateStatus=Text("Version check",updateBox,18);Top(updateStatus.rectTransform,12,10,-12,72);
            updateButton=Button("Update mod",updateBox,()=>{ if(selected!=null) this.updateMod(selected); });
            Top((RectTransform)updateButton.transform,12,90,-12,38);updateButton.GetComponent<Image>().color=LobbyListItem.StatusSuccessColor;updateButton.GetComponentInChildren<TMP_Text>(true).color=new Color(.08f,.12f,.10f,1);
            releaseButton=Button("Release page",updateBox,()=>{ if(selected!=null&&GitHubRepository.Valid(selected.UpdateRepository)) Application.OpenURL("https://github.com/"+selected.UpdateRepository+"/releases/latest"); });Top((RectTransform)releaseButton.transform,12,136,-12,38);
            exitButton=Button("Restart to finish update",updateBox,exitGame);Top((RectTransform)exitButton.transform,12,90,-12,38);
            links=Rect("Links",previewContent);
            autoMatch=Check("Auto match mods when joining a server",panel,true,matchChanged); Bottom((RectTransform)autoMatch.transform,24,94,-24,36);
            var statusArea=Rect("Mod status",panel);Bottom(statusArea,24,55,-24,34);modStatusPane=statusArea.gameObject;statusContent=Scroll(statusArea);statusScroll=statusArea.GetComponent<ScrollRect>();
            status=Text("Status",statusContent,17);status.color=LobbyListItem.TextMutedColor;status.overflowMode=TextOverflowModes.Overflow;
            reload=Button("Reload mods",panel,reloadMods); Bottom((RectTransform)reload.transform,24,18,-210,38);
            back=Button("Back",panel,Hide); var backRect=(RectTransform)back.transform; backRect.anchorMin=backRect.anchorMax=new Vector2(1,0); backRect.pivot=Vector2.one; backRect.sizeDelta=new Vector2(160,38); backRect.anchoredPosition=new Vector2(-24,56);
            reload.GetComponent<Image>().color=LobbyListItem.StatusSuccessColor; reload.GetComponentInChildren<TMP_Text>(true).color=new Color(.08f,.12f,.10f,1);
            var shade=Rect("Restart confirmation",panel);Stretch(shade);restartShade=shade.gameObject;shade.gameObject.AddComponent<Image>().color=new Color(0,0,0,.78f);
            restartDialog=Rect("Restart required",shade);restartDialog.anchorMin=restartDialog.anchorMax=restartDialog.pivot=new Vector2(.5f,.5f);restartDialog.gameObject.AddComponent<Image>().color=new Color(.16f,.20f,.22f,1);
            restartTitle=Text("Title",restartDialog,26);restartTitle.text="RESTART REQUIRED";Top(restartTitle.rectTransform,24,20,-24,40);
            restartBodyViewport=Rect("Reason area",restartDialog);restartBodyContent=Scroll(restartBodyViewport);restartScroll=restartBodyViewport.GetComponent<ScrollRect>();restartBody=Text("Reason",restartBodyContent,20);
            restartProceed=Button("Restart game",restartDialog,()=>{if(busy)return;var action=restartAction;DismissRestart();action?.Invoke();});Bottom((RectTransform)restartProceed.transform,24,20,-210,40);restartProceed.GetComponent<Image>().color=LobbyListItem.StatusSuccessColor;restartProceed.GetComponentInChildren<TMP_Text>(true).color=new Color(.08f,.12f,.10f,1);
            restartCancel=Button("Cancel",restartDialog,DismissRestart);var cancelRect=(RectTransform)restartCancel.transform;cancelRect.anchorMin=cancelRect.anchorMax=cancelRect.pivot=new Vector2(1,0);cancelRect.sizeDelta=new Vector2(160,40);cancelRect.anchoredPosition=new Vector2(-24,20);
            restartShade.SetActive(false);
            ApplyTabVisibility();
            }
            catch { UnityEngine.Object.Destroy(root); throw; }
        }
        internal void Show()
        {
            if(!IsOpen) { oldVisible=Cursor.visible; oldLock=Cursor.lockState; oldScene=UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle; }
            root.SetActive(true); Cursor.visible=true; Cursor.lockState=CursorLockMode.None; Canvas.ForceUpdateCanvases(); LayoutPreview();
        }
        internal void Hide()
        {
            DismissRestart();HideLists();if(mapstab){mapstab=false;mapsUi?.Hide();ApplyTabVisibility();mapsChanged(false);}if(!IsOpen) return; root.SetActive(false);
            if(texture!=null) UnityEngine.Object.Destroy(texture); texture=null; picture.texture=null;
            if(UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle==oldScene) { Cursor.visible=oldVisible; Cursor.lockState=oldLock; }
        }
        internal void Tick()
        {
            if(!busy && Input.GetKeyDown(KeyCode.Escape))
            {if(restartShade.activeSelf)DismissRestart();else if(ListsOpen){if(!savedLists!.EscapeTyping())HideLists();}else Hide();}
            if(ListsOpen)savedLists!.Tick();
            if(IsMapsOpen)mapsUi?.Tick();
            if(panel.rect.size!=layoutSize) { layoutSize=panel.rect.size; LayoutPreview();if(restartShade.activeSelf)LayoutRestart(); }
        }
        internal void PromptRestart(string message,UnityAction proceed)
            =>PromptConfirmation("RESTART REQUIRED",message,"Restart game",proceed);
        internal void PromptConfirmation(string title,string message,string proceedLabel,UnityAction proceed)
        { savedLists?.StopTyping();restartTitle.text=title;restartBody.text=message;restartProceed.GetComponentInChildren<TMP_Text>(true).text=proceedLabel;restartAction=proceed;restartShade.SetActive(true);restartShade.transform.SetAsLastSibling();LayoutRestart();restartScroll.verticalNormalizedPosition=1; }
        internal void ShowLists(IReadOnlyList<SavedModList> lists,string message="")
        {
            if(!IsOpen||mapstab)return;
            if(savedLists==null)savedLists=new NativeModListsUi(panel,font,saveList,loadList,deleteList,HideLists);
            savedLists.Show(lists,message);savedLists.Busy(busy);
        }
        internal void ListsStatus(string message)=>savedLists?.Status(message);
        internal void HideLists()=>savedLists?.Hide();
        internal void SetMaps(IReadOnlyList<MapEntry> maps,string message)
        {if(!IsMapsOpen)return;EnsureMapsUi();mapsUi!.Set(maps,message);mapsUi.Busy(busy);}
        internal void RefreshMapPreview(MapEntry item)=>mapsUi?.RefreshPreview(item);
        private void EnsureMapsUi()
        {if(mapsUi==null)mapsUi=new NativeMapsUi(panel,font,selectedMap,refreshMaps);}
        private void SwitchMaps(bool value)
        {
            if(busy||value==mapstab||!IsOpen)return;
            DismissRestart();HideLists();mapstab=value;
            if(value){EnsureMapsUi();mapsUi!.Show();mapsUi.Busy(busy);ApplyTabVisibility();mapsChanged(true);}
            else{mapsUi?.Hide();ApplyTabVisibility();mapsChanged(false);Select(selected);}
        }
        private void ApplyTabVisibility()
        {
            modListPane.SetActive(!mapstab);modPreviewPane.SetActive(!mapstab);autoMatch.gameObject.SetActive(!mapstab);reload.gameObject.SetActive(!mapstab);modStatusPane.SetActive(!mapstab);listsButton.gameObject.SetActive(!mapstab);
            headerHelp.text=mapstab?"Installed maps, previews and credits.":"Tick what you want. Reload. Go fly.";
            foreach(var tab in new[]{modsTab,mapsTab}){bool active=tab==mapsTab?mapstab:!mapstab;tab.GetComponent<Image>().color=active?LobbyListItem.StatusSuccessColor:new Color(.20f,.24f,.26f,1);tab.GetComponentInChildren<TMP_Text>(true).color=active?new Color(.08f,.12f,.10f,1):Color.white;}
        }
        private void DismissRestart(){restartAction=null;restartShade.SetActive(false);}
        private void LayoutRestart()
        {
            float width=Mathf.Min(640,Mathf.Max(300,panel.rect.width-48));float titleHeight=Mathf.Min(80,Mathf.Max(40,restartTitle.GetPreferredValues(restartTitle.text,width-48,0).y+6));
            restartDialog.sizeDelta=new Vector2(width,restartDialog.sizeDelta.y);Top(restartBodyViewport,24,36+titleHeight,-24,60);
            float textHeight=Mathf.Max(84,restartBody.GetPreferredValues(restartBody.text,Mathf.Max(1,restartBodyContent.rect.width),0).y+12);
            float visibleHeight=Mathf.Min(textHeight,Mathf.Max(60,panel.rect.height-164-titleHeight));restartDialog.sizeDelta=new Vector2(width,116+titleHeight+visibleHeight);Top(restartTitle.rectTransform,24,20,-24,titleHeight);Top(restartBodyViewport,24,36+titleHeight,-24,visibleHeight);Top(restartBody.rectTransform,0,0,0,textHeight);restartBodyContent.sizeDelta=new Vector2(0,textHeight);
            float cancelWidth=Mathf.Min(160,(width-60)*.4f);Bottom((RectTransform)restartProceed.transform,24,20,-36-cancelWidth,40);((RectTransform)restartCancel.transform).sizeDelta=new Vector2(cancelWidth,40);
        }
        internal void Set(IReadOnlyList<ModEntry> value,bool matching,string message)
        {
            entries=value; SetAutoMatch(matching); Status(message);
            foreach(var row in rows) UnityEngine.Object.Destroy(row); rows.Clear(); checks.Clear(); states.Clear();
            float y=0;
            foreach(var item in entries)
            {
                var row=Rect(item.Id,listContent); Top(row,0,y,0,82); row.gameObject.AddComponent<Image>().color=new Color(.12f,.15f,.17f,1);
                var check=Check("",row,item.Wanted,v=>{ if(v!=item.Wanted) toggle(item); }); check.interactable=!item.Locked&&!busy; var checkRect=(RectTransform)check.transform; checkRect.anchorMin=new Vector2(0,0);checkRect.anchorMax=new Vector2(0,1);checkRect.offsetMin=new Vector2(4,8);checkRect.offsetMax=new Vector2(48,-8); checks.Add(item,check);
                var choose=Button("",row,()=>Select(item)); var chooseRect=(RectTransform)choose.transform; Stretch(chooseRect); chooseRect.offsetMin=new Vector2(54,0);chooseRect.offsetMax=Vector2.zero; choose.GetComponent<Image>().color=Color.clear; choose.GetComponent<Image>().canvasRenderer.cullTransparentMesh=false;
                var label=choose.GetComponentInChildren<TMP_Text>(true); label.text=item.Name; label.fontSize=20; label.fontSizeMax=20; label.fontSizeMin=15; label.alignment=TextAlignmentOptions.MidlineLeft; Top(label.rectTransform,8,7,-12,32);
                var state=Text("State",chooseRect,16); Top(state.rectTransform,8,44,-12,26); state.color=LobbyListItem.TextMutedColor; states.Add(item,state);
                rows.Add(row.gameObject); y+=90;
            }
            listContent.sizeDelta=new Vector2(0,y);RefreshSelection();var next=selected!=null&&value.Contains(selected)?selected:entries.Count>0?entries[0]:null;if(mapstab)selected=next;else Select(next);
        }
        internal void SetAutoMatch(bool value)=>autoMatch.SetIsOnWithoutNotify(value);
        internal void RefreshPreview(ModEntry item) { if(!mapstab&&selected==item)Select(item); }
        internal void RefreshSelection()
        {
            foreach(var item in entries)
            {
                checks[item].SetIsOnWithoutNotify(item.Wanted);
                string pending=item.Wanted==item.Enabled?"":item.Content?" | Reload to apply":" | Restart to apply";
                states[item].text=(item.Content?"Content pack":"Plugin")+" "+item.Version+" | "+(item.Enabled?"Enabled":"Disabled")+(item.Locked?" | Required":pending);
            }
        }
        internal void Status(string message) { status.text=message;LayoutStatus();statusScroll.verticalNormalizedPosition=1; }
        private void LayoutStatus()
        {
            float height=Mathf.Max(34,status.GetPreferredValues(status.text,Mathf.Max(1,statusContent.rect.width),0).y+8);
            Top(status.rectTransform,0,0,0,height);statusContent.sizeDelta=new Vector2(0,height);
        }
        internal void Busy(bool value,string message="")
        {
            busy=value;reload.interactable=back.interactable=autoMatch.interactable=listsButton.interactable=modsTab.interactable=mapsTab.interactable=restartProceed.interactable=restartCancel.interactable=!value;savedLists?.Busy(value);mapsUi?.Busy(value);
            foreach(var entry in checks) entry.Value.interactable=!value&&!entry.Key.Locked;
            updateButton.interactable=!value&&updateCheck?.Newer==true&&updateCheck.Asset!=null&&selected?.UpdatePath.Length>0;
            exitButton.interactable=!value;
            if(message.Length>0) Status(message);
        }
        private void Select(ModEntry? item)
        {
            bool changed=selected!=item;
            selected=item; if(texture!=null) UnityEngine.Object.Destroy(texture); texture=null; picture.texture=null;
            updateCheck=null;updateStatus.text=item==null?"":item.UpdateRepository.Length==0?"No GitHub repository supplied for this mod.":"Checking GitHub...";updateButton.gameObject.SetActive(false);exitButton.gameObject.SetActive(false);releaseButton.gameObject.SetActive(item?.UpdateRepository.Length>0);
            foreach(var button in linkButtons) UnityEngine.Object.Destroy(button); linkButtons.Clear();
            previewName.text=item?.Name??"No mods found";
            previewByline.text=item==null?"":(item.Manifest?.Author??"No author supplied")+" | "+item.Version;
            description.text=string.IsNullOrWhiteSpace(item?.Manifest?.Description)?"No description available.":item!.Manifest!.Description;
            if(item!=null) description.text+="\n\n"+(item.Content?"Reload switches this pack's game content. Its supporting plugin code stays loaded.":item.Locked?"This plugin is required and stays enabled.":"Plugin changes take effect after restarting the game.");
            if(item?.Manifest!=null)
            {
                try
                {
                    var data=item.Manifest.ReadImageBytes();
                    if(data!=null)
                    {
                        texture=new Texture2D(2,2,TextureFormat.RGBA32,false); if(!ImageConversion.LoadImage(texture,data,true)) throw new InvalidDataException("Preview image could not be read.");
                        picture.texture=texture; picture.GetComponent<AspectRatioFitter>().aspectRatio=(float)texture.width/texture.height;
                    }
                } catch { if(texture!=null) UnityEngine.Object.Destroy(texture); texture=null; picture.texture=null; }
            }
            pictureBox.gameObject.SetActive(picture.texture!=null);
            if(item?.Manifest!=null)
                foreach(var link in item.Manifest.Links)
                { var chosen=link; var button=Button(chosen.Label,links,()=>{ if(ModManifest.SafeLink(chosen.Url)) Application.OpenURL(chosen.Url); }); linkButtons.Add(button.gameObject); }
            LayoutPreview();
            if(changed)previewScroll.verticalNormalizedPosition=1;
            if(item!=null&&!mapstab)selectedChanged(item);
        }
        internal void UpdateFor(ModEntry item,ModUpdateCheck result)
        {
            if(selected!=item) return;updateCheck=result;updateStatus.text=result.Message;
            if(result.Newer&&item.UpdatePath.Length==0) updateStatus.text+=" This mod uses a manual installation.";
            updateButton.gameObject.SetActive(result.Newer&&result.Asset!=null&&item.UpdatePath.Length>0&&!result.Queued);
            updateButton.GetComponentInChildren<TMP_Text>(true).text="Update to "+(result.Release?.Tag??"");updateButton.interactable=!busy;
            exitButton.gameObject.SetActive(result.Queued);exitButton.interactable=!busy;LayoutPreview();
        }
        private void LayoutPreview()
        {
            if(previewContent==null) return;
            LayoutStatus();
            float width=Mathf.Max(1,previewContent.rect.width-36);
            float titleHeight=Mathf.Max(40,previewName.GetPreferredValues(previewName.text,width,0).y+6);
            Top(previewName.rectTransform,18,16,-18,titleHeight); Top(previewByline.rectTransform,18,22+titleHeight,-18,34);
            float top=68+titleHeight;
            if(pictureBox.gameObject.activeSelf) { Top(pictureBox,18,top,-18,Mathf.Min(280,width*.55f)); top+=Mathf.Min(280,width*.55f)+18; }
            float height=Mathf.Max(80,description.GetPreferredValues(description.text,width,0).y+12); Top(description.rectTransform,18,top,-18,height); top+=height+16;
            float updateHeight=Mathf.Max(60,updateStatus.GetPreferredValues(updateStatus.text,width-24,0).y+8);Top(updateStatus.rectTransform,12,10,-12,updateHeight);
            float buttonTop=updateHeight+20;
            if(updateButton.gameObject.activeSelf) { Top((RectTransform)updateButton.transform,12,buttonTop,-12,38);buttonTop+=46; }
            if(exitButton.gameObject.activeSelf) { Top((RectTransform)exitButton.transform,12,buttonTop,-12,38);buttonTop+=46; }
            if(releaseButton.gameObject.activeSelf) { Top((RectTransform)releaseButton.transform,12,buttonTop,-12,38);buttonTop+=46; }
            Top(updateBox,18,top,-18,buttonTop+6);top+=buttonTop+22;
            Top(links,18,top,-18,linkButtons.Count*44);
            for(int i=0;i<linkButtons.Count;i++) Top((RectTransform)linkButtons[i].transform,0,i*44,0,36);
            previewContent.sizeDelta=new Vector2(0,top+linkButtons.Count*44+18);
        }
        private TMP_Text Text(string name,RectTransform parent,float size)
        {
            var text=Rect(name,parent).gameObject.AddComponent<TextMeshProUGUI>(); text.font=font.font; text.fontSharedMaterial=font.fontSharedMaterial; text.fontSize=size; text.color=Color.white; text.richText=false; text.raycastTarget=false; text.enableWordWrapping=true; text.overflowMode=TextOverflowModes.Ellipsis; return text;
        }
        private Button Button(string label,RectTransform parent,UnityAction action)
        {
            var rect=Rect(label,parent); var fill=rect.gameObject.AddComponent<Image>(); fill.color=new Color(.20f,.24f,.26f,1); var button=rect.gameObject.AddComponent<Button>(); button.targetGraphic=fill; button.onClick.AddListener(action); var nav=button.navigation;nav.mode=Navigation.Mode.None;button.navigation=nav;
            var text=Text("Label",rect,18); Stretch(text.rectTransform,6);text.text=label;text.alignment=TextAlignmentOptions.Center;text.enableWordWrapping=false;text.enableAutoSizing=true;text.fontSizeMin=14;text.fontSizeMax=18; return button;
        }
        private Toggle Check(string label,RectTransform parent,bool value,UnityAction<bool> changed)
        {
            var rect=Rect(label,parent); rect.gameObject.SetActive(false); var hit=rect.gameObject.AddComponent<Image>(); hit.color=Color.clear;hit.canvasRenderer.cullTransparentMesh=false;
            var check=rect.gameObject.AddComponent<Toggle>();check.toggleTransition=Toggle.ToggleTransition.None;var nav=check.navigation;nav.mode=Navigation.Mode.None;check.navigation=nav;
            var square=Rect("Box",rect);square.anchorMin=square.anchorMax=new Vector2(0,.5f);square.sizeDelta=new Vector2(26,26);square.anchoredPosition=new Vector2(18,0);var fill=square.gameObject.AddComponent<Image>();fill.color=new Color(.19f,.21f,.23f,1);check.targetGraphic=fill;
            var icon=Rect("Tick",square);Stretch(icon);var tick=icon.gameObject.AddComponent<DrawnBrowserIcon>();tick.color=LobbyListItem.StatusSuccessColor;tick.raycastTarget=false;check.graphic=tick;
            var text=Text("Label",rect,18);Stretch(text.rectTransform);text.rectTransform.offsetMin=new Vector2(40,0);text.text=label;text.alignment=TextAlignmentOptions.MidlineLeft;
            check.SetIsOnWithoutNotify(value);check.onValueChanged.AddListener(changed);rect.gameObject.SetActive(true);return check;
        }
        private static RectTransform Scroll(RectTransform parent)
        {
            var viewport=Rect("Viewport",parent);Stretch(viewport);viewport.gameObject.AddComponent<RectMask2D>();var hit=viewport.gameObject.AddComponent<Image>();hit.color=Color.clear;hit.canvasRenderer.cullTransparentMesh=false;
            var content=Rect("Content",viewport);content.anchorMin=new Vector2(0,1);content.anchorMax=Vector2.one;content.pivot=new Vector2(.5f,1);content.offsetMin=content.offsetMax=Vector2.zero;
            var scroll=parent.gameObject.AddComponent<ScrollRect>();scroll.horizontal=false;scroll.viewport=viewport;scroll.content=content;scroll.movementType=ScrollRect.MovementType.Clamped;scroll.scrollSensitivity=40;NativeScrollbars.Attach(scroll);return content;
        }
        private static RectTransform Rect(string name,Transform parent) { var rect=(RectTransform)new GameObject(name,typeof(RectTransform)).transform;rect.SetParent(parent,false);return rect; }
        private static void Stretch(RectTransform rect,float inset=0) { rect.anchorMin=Vector2.zero;rect.anchorMax=Vector2.one;rect.offsetMin=new Vector2(inset,inset);rect.offsetMax=new Vector2(-inset,-inset); }
        private static void Top(RectTransform rect,float left,float top,float right,float height) { rect.anchorMin=new Vector2(0,1);rect.anchorMax=Vector2.one;rect.pivot=new Vector2(.5f,1);rect.offsetMin=new Vector2(left,-top-height);rect.offsetMax=new Vector2(right,-top); }
        private static void Bottom(RectTransform rect,float left,float bottom,float right,float height) { rect.anchorMin=Vector2.zero;rect.anchorMax=new Vector2(1,0);rect.offsetMin=new Vector2(left,bottom);rect.offsetMax=new Vector2(right,bottom+height); }
        public void Dispose() {Hide();mapsUi?.Dispose();if(texture!=null)UnityEngine.Object.Destroy(texture);UnityEngine.Object.Destroy(root);}
    }
}
