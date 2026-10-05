using System;
using System.Collections.Generic;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed class NativeJoinUi : IDisposable
    {
        private readonly Action close;
        private readonly GameObject root, shade;
        private readonly RectTransform canvasRect, panel, scrollBox, content, actions;
        private readonly Canvas canvas;
        private readonly TMP_Text template, title, serverName, help, footer, detailsLabel, matchingLabel;
        private readonly Button? buttonTemplate;
        private readonly Button matchingButton;
        private readonly ScrollRect scroll;
        private readonly List<GameObject> rows = new List<GameObject>(), ownedButtons = new List<GameObject>();
        private readonly Vector3[] corners = new Vector3[4];
        private RectTransform? nativePanel;
        private GameObject? nativeHolder, nativeWarning, checkButton;
        private bool originalWarningActive, opened, details, matching, cursorOwned, oldVisible;
        private CursorLockMode oldLock;
        private int oldScene;
        private JoinSummary summary = new JoinSummary();
        private string report = "";
        private float rowWidth = -1;
        private Vector2 laidOutSize;
        private bool layoutDirty = true;
        internal bool IsOpen => opened && root != null && root.activeInHierarchy;
        internal bool CanInteract => EventSystem.current != null && EventSystem.current.isActiveAndEnabled;

        internal NativeJoinUi(Action close)
        {
            this.close = close;
            var prefab = Resources.Load<GameObject>("JoinLobbyOverlayCanvas");
            if (prefab == null) throw new InvalidOperationException("Native join template unavailable");
            var source = prefab.GetComponent<JoinLobbyOverlay>();
            template = Field<TMP_Text>(source, "bodyText") ?? throw new InvalidOperationException("Native join font unavailable");
            buttonTemplate = Field<Button>(source, "closeButton");
            root = new GameObject("JOINCHECK", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            root.SetActive(false);
            try
            {
                UnityEngine.Object.DontDestroyOnLoad(root);
                canvas = root.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 30000;
                canvasRect = (RectTransform)root.transform;
                var scaler = root.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1;
                var backdrop = Rect("Backdrop", canvasRect); Stretch(backdrop);
                shade = backdrop.gameObject; var dim = shade.AddComponent<Image>(); dim.color = new Color(0,0,0,.68f);
                panel = Rect("Mod check", canvasRect); panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(0,1);
                panel.sizeDelta = new Vector2(720,740);
                // Native overlay sprites can have transparent centres/insets. A solid quad makes
                // the visible box match the exact RectTransform used for every child/control.
                var fill = panel.gameObject.AddComponent<Image>(); fill.color = new Color(.16f,.20f,.22f,JoinLayout.BackgroundAlpha);
                var outline = panel.gameObject.AddComponent<Outline>(); outline.effectColor = new Color(.36f,.42f,.46f,1); outline.effectDistance = new Vector2(1,-1);
                title = Text("Title", panel, 28); Top(title.rectTransform,20,20,-20,40);
                serverName = Text("Server",panel,18); Top(serverName.rectTransform,22,63,-22,30); serverName.color = LobbyListItem.TextMutedColor; serverName.enableWordWrapping = false; serverName.overflowMode = TextOverflowModes.Ellipsis;
                help = Text("Next step",panel,19); Top(help.rectTransform,20,105,-20,80);
                scrollBox = Rect("Checklist",panel); Stretch(scrollBox); scrollBox.offsetMin = new Vector2(20,146); scrollBox.offsetMax = new Vector2(-20,-195);
                scroll = scrollBox.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; scroll.scrollSensitivity = 34;
                var viewport = Rect("Viewport",scrollBox); Stretch(viewport); viewport.offsetMax = new Vector2(-12,0); viewport.gameObject.AddComponent<RectMask2D>();
                var hit = viewport.gameObject.AddComponent<Image>(); hit.color = Color.clear;
                content = Rect("Items",viewport); content.anchorMin = new Vector2(0,1); content.anchorMax = Vector2.one; content.pivot = new Vector2(.5f,1); content.offsetMin = content.offsetMax = Vector2.zero;
                scroll.viewport = viewport; scroll.content = content;
                var trackRect = Rect("Scrollbar",scrollBox); trackRect.anchorMin = new Vector2(1,0); trackRect.anchorMax = Vector2.one; trackRect.offsetMin = new Vector2(-6,0); trackRect.offsetMax = Vector2.zero;
                var track = trackRect.gameObject.AddComponent<Image>(); track.color = new Color(.16f,.19f,.23f);
                var handleRect = Rect("Handle",trackRect); Stretch(handleRect); var handle = handleRect.gameObject.AddComponent<Image>(); handle.color = LobbyListItem.TextMutedColor;
                var bar = trackRect.gameObject.AddComponent<Scrollbar>(); bar.direction = Scrollbar.Direction.BottomToTop; bar.handleRect = handleRect; bar.targetGraphic = handle;
                scroll.verticalScrollbar = bar; scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;
                matchingButton = Button("Matching mods",panel,()=> { matching = !matching; DrawRows(); }); Bottom((RectTransform)matchingButton.transform,22,102,-22,32);
                matchingLabel = matchingButton.GetComponentInChildren<TMP_Text>(true);
                footer = Text("Note",panel,16); Bottom(footer.rectTransform,22,62,-22,34); footer.color = LobbyListItem.TextMutedColor;
                actions = Rect("Actions",panel); Bottom(actions,20,20,-20,JoinLayout.ActionHeight);
                var copy = Button("Copy report",actions,()=> { GUIUtility.systemCopyBuffer = report; footer.text = "Copied. Send to the host."; layoutDirty = true; }); Third((RectTransform)copy.transform,0);
                var detail = Button("Details",actions,()=> { details = !details; DrawRows(); }); Third((RectTransform)detail.transform,1); detailsLabel = detail.GetComponentInChildren<TMP_Text>(true);
                var dismiss = Button("Close",actions,()=> { Hide(); close(); }); Third((RectTransform)dismiss.transform,2);
            }
            catch { UnityEngine.Object.Destroy(root); throw; }
        }

        internal void Attach(LobbyDetailsModal modal, UnityAction show, JoinSummary state)
        {
            var name = Field<TMP_Text>(modal,"lobbyNameText"); var join = Field<Button>(modal,"joinButton");
            nativeHolder = Field<GameObject>(modal,"holder"); nativePanel = null;
            if (name != null && join != null)
                for (var p = name.transform.parent; p != null; p = p.parent)
                    if (join.transform.IsChildOf(p)) { nativePanel = p as RectTransform; break; }
            var warning = Field<GameObject>(modal,"moddedWarning");
            if (warning == null || !(warning.transform.parent is RectTransform host)) return;
            if (nativeWarning != warning)
            { RestoreWarning(); nativeWarning = warning; originalWarningActive = warning.activeSelf; }
            var existing = host.Find("JOINCHECK button"); var button = existing != null ? existing.GetComponent<Button>() : null;
            if (button == null)
            {
                button = Button("Check mods",host,show); button.name = "JOINCHECK button"; ownedButtons.Add(button.gameObject);
                var slot = (RectTransform)warning.transform; var rect = (RectTransform)button.transform;
                rect.anchorMin = slot.anchorMin; rect.anchorMax = slot.anchorMax; rect.pivot = slot.pivot; rect.anchoredPosition = slot.anchoredPosition; rect.sizeDelta = slot.sizeDelta;
                var layout = slot.GetComponent<LayoutElement>();
                if (layout != null)
                { var target = button.gameObject.AddComponent<LayoutElement>(); target.preferredWidth = layout.preferredWidth; target.preferredHeight = layout.preferredHeight; target.minWidth = layout.minWidth; target.minHeight = layout.minHeight; }
            }
            button.onClick = new Button.ButtonClickedEvent(); button.onClick.AddListener(show);
            checkButton = button.gameObject; checkButton.SetActive(true); warning.SetActive(false);
            var label = button.GetComponentInChildren<TMP_Text>(true); label.text = state.Status == JoinStatus.Match ? "MODS MATCH - VIEW" : "CHECK MODS"; label.color = StatusColor(state.Status);
        }
        internal void Set(JoinSummary value, string server, string diagnostic, bool reset = false)
        {
            string name = Diagnostics.Clean(server);
            if (!reset && diagnostic == report && serverName.text == name) return;
            summary = value; report = diagnostic; title.text = value.Title; title.color = StatusColor(value.Status); serverName.text = name; help.text = value.Help;
            footer.text = "Restart the game after changing mods.";
            layoutDirty = true;
            if (reset) { details = matching = false; scroll.verticalNormalizedPosition = 1; }
            DrawRows();
        }
        internal bool TryDock()
        {
            if (!CanInteract) return false;
            Open(); Canvas.ForceUpdateCanvases();
            if (!DockBounds(out var bounds)) { Hide(); return false; }
            Place(bounds); LayoutText(); return true;
        }
        internal void Show() { if (CanInteract) { Open(); Canvas.ForceUpdateCanvases(); Tick(); } }
        private void Open()
        {
            if (!opened)
            {
                oldVisible = Cursor.visible; oldLock = Cursor.lockState; oldScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                cursorOwned = !Cursor.visible || Cursor.lockState != CursorLockMode.None;
                if (cursorOwned) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; }
            }
            opened = true; root.SetActive(true);
        }
        internal void Hide()
        {
            opened = false; root.SetActive(false);
            if (cursorOwned && Cursor.visible && Cursor.lockState == CursorLockMode.None && UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle == oldScene && (nativeHolder == null || !nativeHolder.activeInHierarchy))
            { Cursor.lockState = oldLock; Cursor.visible = oldVisible; }
            cursorOwned = false;
        }
        private void RestoreWarning()
        {
            if (nativeWarning != null) nativeWarning.SetActive(originalWarningActive);
            if (checkButton != null) checkButton.SetActive(false);
            nativeWarning = null; checkButton = null;
        }
        internal void Detach() { Hide(); RestoreWarning(); nativePanel = null; nativeHolder = null; }
        internal void Tick()
        {
            if (!opened) return;
            if (!CanInteract) { Hide(); return; }
            if (DockBounds(out var bounds)) Place(bounds);
            else
            {
                shade.SetActive(true);
                float width = Mathf.Min(720,canvasRect.rect.width-32), height = Mathf.Min(740,canvasRect.rect.height-32);
                panel.sizeDelta = new Vector2(width,height); panel.anchoredPosition = new Vector2((canvasRect.rect.width-width)/2,-(canvasRect.rect.height-height)/2);
            }
            LayoutText();
        }
        private void LayoutText()
        {
            var size = panel.rect.size;
            if (!layoutDirty && size == laidOutSize) return;
            float width = Mathf.Max(80,size.x-2*JoinLayout.Margin);
            var layout = JoinLayout.Measure(size.y,
                title.GetPreferredValues(title.text,width,0).y+4,
                help.GetPreferredValues(help.text,width,0).y+4,
                footer.GetPreferredValues(footer.text,width,0).y+4,
                matchingButton.gameObject.activeSelf);
            Top(title.rectTransform,20,20,-20,layout.TitleHeight);
            Top(serverName.rectTransform,20,layout.ServerTop,-20,26);
            Top(help.rectTransform,20,layout.HelpTop,-20,layout.HelpHeight);
            Bottom(footer.rectTransform,20,layout.FooterBottom,-20,layout.FooterHeight);
            Bottom((RectTransform)matchingButton.transform,20,layout.FooterBottom+layout.FooterHeight+JoinLayout.Gap,-20,JoinLayout.MatchingHeight);
            scrollBox.offsetMin = new Vector2(20,layout.ListBottom);
            scrollBox.offsetMax = new Vector2(-20,-layout.ListTop);
            if (Mathf.Abs(Mathf.Max(80,size.x-80)-rowWidth)>1) DrawRows();
            laidOutSize = size; layoutDirty = false;
        }
        private bool DockBounds(out Rect bounds)
        {
            bounds = default;
            if (nativePanel == null || nativeHolder == null || !nativeHolder.activeInHierarchy) return false;
            nativePanel.GetWorldCorners(corners); var owner = nativePanel.GetComponentInParent<Canvas>();
            var camera = owner != null && owner.renderMode != RenderMode.ScreenSpaceOverlay ? owner.worldCamera : null;
            var min = RectTransformUtility.WorldToScreenPoint(camera,corners[0]); var max = RectTransformUtility.WorldToScreenPoint(camera,corners[2]);
            float scale = canvas.scaleFactor, top = Screen.height-max.y;
            float bottom = top+Mathf.Clamp(max.y-min.y,JoinLayout.MinHeight*scale,740*scale);
            if (!JoinLayout.CanDock(Screen.width,Screen.height,max.x,top,bottom,scale)) return false;
            bounds = new Rect((max.x+12*scale)/scale,top/scale,JoinLayout.DockWidth,(bottom-top)/scale); return true;
        }
        private void Place(Rect bounds) { shade.SetActive(false); panel.sizeDelta = bounds.size; panel.anchoredPosition = new Vector2(bounds.x,-bounds.y); }
        private void DrawRows()
        {
            // 40 outer margin + 12 scrollbar gutter + 28 card text margin.
            rowWidth = Mathf.Max(80,panel.rect.width-80);
            foreach (var row in rows) { row.SetActive(false); UnityEngine.Object.Destroy(row); } rows.Clear();
            detailsLabel.text = details ? "Checklist" : "Details"; matchingLabel.text = (matching ? "Hide" : "View")+" matching mods ("+summary.Matching.Count+")";
            matchingButton.gameObject.SetActive(!details && summary.Matching.Count>0);
            layoutDirty = true;
            float y = 0;
            if (details) AddRow("REPORT","",report,ref y,true);
            else
            {
                foreach (var action in summary.Actions) AddRow(action.Action,action.Name,action.Instruction,ref y);
                if (matching) foreach (var action in summary.Matching) AddRow(action.Action,action.Name,action.Instruction,ref y);
                if (summary.Actions.Count==0 && !matching)
                    AddRow(summary.Status==JoinStatus.Match ? "OK" : "ASK HOST",summary.Status==JoinStatus.Match ? "Game and required mod versions" : summary.Title=="Join failed" ? "Check why joining failed" : "Get the server's mod list",summary.Status==JoinStatus.Match ? "You're ready to try joining this server." : "Copy the report below and send it to the host.",ref y);
            }
            content.sizeDelta = new Vector2(0,y);
        }
        private void AddRow(string action,string name,string instruction,ref float y,bool technical=false)
        {
            var row = Rect("Item",content); row.anchorMin = new Vector2(0,1); row.anchorMax = Vector2.one; row.pivot = new Vector2(.5f,1); row.offsetMin = row.offsetMax = Vector2.zero;
            var bg = row.gameObject.AddComponent<Image>(); bg.color = new Color(.12f,.15f,.17f,1); bg.raycastTarget = false;
            var label = Text("Action",row,16); label.text = action; label.color = action=="OK" ? LobbyListItem.StatusSuccessColor : LobbyListItem.StatusWarningColor;
            var heading = Text("Mod",row,20); heading.text = name;
            var body = Text("Instruction",row,technical ? 17 : 18); body.text = instruction; body.color = technical ? Color.white : LobbyListItem.TextMutedColor;
            float nameHeight = technical ? 0 : Mathf.Max(30,heading.GetPreferredValues(name,rowWidth,0).y+4);
            float bodyHeight = Mathf.Max(30,body.GetPreferredValues(instruction,rowWidth,0).y+4), height = 54+nameHeight+bodyHeight;
            Top(label.rectTransform,12,10,-16,24); Top(heading.rectTransform,12,36,-16,nameHeight); Top(body.rectTransform,12,40+nameHeight,-16,bodyHeight);
            row.sizeDelta = new Vector2(0,height); row.anchoredPosition = new Vector2(0,-y); y+=height+10; rows.Add(row.gameObject);
        }
        private TMP_Text Text(string name,RectTransform parent,float size)
        {
            var rect = Rect(name,parent); var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = template.font; text.fontSharedMaterial = template.fontSharedMaterial; text.fontStyle = template.fontStyle; text.fontWeight = template.fontWeight; text.characterSpacing = template.characterSpacing;
            text.fontSize = size; text.color = template.color; text.richText = false; text.raycastTarget = false; text.enableWordWrapping = true; text.overflowMode = TextOverflowModes.Truncate; return text;
        }
        private Button Button(string label,RectTransform parent,UnityAction click)
        {
            var rect = Rect(label,parent); var image = rect.gameObject.AddComponent<Image>();
            image.color = new Color(.20f,.24f,.26f,1);
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = image; if (buttonTemplate!=null) button.colors = buttonTemplate.colors;
            var nav = button.navigation; nav.mode = Navigation.Mode.None; button.navigation = nav; button.onClick.AddListener(click);
            var text = Text("Label",rect,18); Stretch(text.rectTransform,6); text.text = label; text.alignment = TextAlignmentOptions.Center; text.enableWordWrapping = false; text.overflowMode = TextOverflowModes.Ellipsis; text.enableAutoSizing = true; text.fontSizeMin = 14; text.fontSizeMax = 18;
            return button;
        }
        private static Color StatusColor(JoinStatus status) => status==JoinStatus.Match ? LobbyListItem.StatusSuccessColor : status==JoinStatus.Changes ? LobbyListItem.StatusDangerColor : LobbyListItem.StatusWarningColor;
        private static T? Field<T>(object value,string name) where T:class => AccessTools.Field(value.GetType(),name)?.GetValue(value) as T;
        private static RectTransform Rect(string name,Transform parent) { var go = new GameObject(name,typeof(RectTransform)); var rect = (RectTransform)go.transform; rect.SetParent(parent,false); return rect; }
        private static void Stretch(RectTransform rect,float inset=0) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(inset,inset); rect.offsetMax = new Vector2(-inset,-inset); }
        private static void Top(RectTransform rect,float left,float top,float right,float height) { rect.anchorMin = new Vector2(0,1); rect.anchorMax = Vector2.one; rect.pivot = new Vector2(.5f,1); rect.offsetMin = new Vector2(left,-top-height); rect.offsetMax = new Vector2(right,-top); }
        private static void Bottom(RectTransform rect,float left,float bottom,float right,float height) { rect.anchorMin = Vector2.zero; rect.anchorMax = new Vector2(1,0); rect.offsetMin = new Vector2(left,bottom); rect.offsetMax = new Vector2(right,bottom+height); }
        private static void Third(RectTransform rect,int index) { rect.anchorMin = new Vector2(index/3f,0); rect.anchorMax = new Vector2((index+1)/3f,1); rect.offsetMin = new Vector2(3,0); rect.offsetMax = new Vector2(-3,0); }
        public void Dispose() { Detach(); foreach (var button in ownedButtons) if (button!=null) UnityEngine.Object.Destroy(button); UnityEngine.Object.Destroy(root); }
    }
}
