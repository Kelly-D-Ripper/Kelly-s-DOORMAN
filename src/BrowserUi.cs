using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace KellysJOINCHECK
{
    internal sealed class BrowserUi : IDisposable
    {
        private readonly LobbyList list;
        private readonly BrowserSettings settings;
        private readonly Action<string> warn;
        private readonly RectTransform scrollBounds, toolbar;
        private readonly Vector2 originalScrollTop;
        private readonly TMP_Text template, favouritesLabel, hint;
        private readonly Toggle favouritesToggle;
        private readonly FieldInfo sortListField;
        private readonly MethodInfo updateList;
        private readonly Dictionary<LobbyListItem,Row> rows = new Dictionary<LobbyListItem,Row>();
        private readonly HashSet<LobbyListItem> visited = new HashSet<LobbyListItem>();
        private string localVersion = "";
        private bool disposed;
        private BrowserDiscovery? discovery;
        private Coroutine? queuedSearch;
        private bool dedicatedEmpty;
        private readonly SteamLobby manager;
        private readonly MethodInfo serverBusy;
        private readonly FieldInfo playerBusy;

        internal BrowserUi(LobbyList list,BrowserSettings settings,Action<string> warn)
        {
            this.list = list; this.settings = settings; this.warn = warn;
            manager=SteamLobby.instance;
            serverBusy=AccessTools.Method(typeof(SteamLobby),"get_serverLobbiesRefreshInProgress") ?? throw new MissingMethodException("server refresh state");
            playerBusy=AccessTools.Field(typeof(SteamLobby),"playerLobbyRefreshInProgress") ?? throw new MissingFieldException("player refresh state");
            var content = Field<RectTransform>(list,"lobbyListContent") ?? throw new InvalidOperationException("Browser content unavailable");
            var prefab = Field<LobbyListItem>(list,"entryPrefab") ?? throw new InvalidOperationException("Browser row unavailable");
            template = Field<TMP_Text>(prefab,"lobbyNameText") ?? throw new InvalidOperationException("Browser font unavailable");
            sortListField = AccessTools.Field(typeof(LobbyList),"sortList") ?? throw new MissingFieldException("sortList");
            updateList = AccessTools.Method(typeof(LobbyList),"UpdateLobbyList") ?? throw new MissingMethodException("UpdateLobbyList");
            var scroll = content.GetComponentInParent<ScrollRect>() ?? throw new InvalidOperationException("Browser scroll view unavailable");
            scrollBounds = (RectTransform)scroll.transform;
            originalScrollTop = scrollBounds.offsetMax;
            // The native viewport is zero-sized before ScrollRect's first layout pass.
            // It is driven by AutoHideAndExpandViewport: never copy or edit its geometry.
            // Reserve space on the outer scroll view and attach outside its viewport/mask.
            if (scrollBounds.anchorMin.x!=0 || scrollBounds.anchorMax.x!=1 || scrollBounds.anchorMax.y!=1)
                throw new InvalidOperationException("Browser scroll anchors unsupported");
            toolbar = Rect("JOINCHECK browser controls",scrollBounds.parent);
            try
            {
                toolbar.anchorMin = new Vector2(0,1); toolbar.anchorMax = Vector2.one; toolbar.pivot = new Vector2(.5f,1);
                toolbar.offsetMin = new Vector2(scrollBounds.offsetMin.x,originalScrollTop.y-BrowserPresentation.ToolbarHeight); toolbar.offsetMax = originalScrollTop;
                toolbar.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                // Use the browser's own backdrop instead of painting a separate teal band.
                var background = toolbar.gameObject.AddComponent<Image>(); background.color = Color.clear; background.raycastTarget = false;
                scrollBounds.offsetMax = new Vector2(originalScrollTop.x,originalScrollTop.y-BrowserPresentation.ReservedHeight);
                hint = Text("Hint",toolbar,14); hint.text = DefaultHint; hint.color = LobbyListItem.TextMutedColor;
                hint.rectTransform.anchorMin = new Vector2(0,0); hint.rectTransform.anchorMax = new Vector2(1,0); hint.rectTransform.offsetMin = new Vector2(12,4); hint.rectTransform.offsetMax = new Vector2(-12,25);
                var incompatible = Check("Show incompatible servers",0,.62f,settings.ShowIncompatible.Value,value=>
                { settings.ShowIncompatible.Value = value; hint.text = DefaultHint; list.GetListOfLobbies(); });
                incompatible.interactable = settings.QueryAvailable;
                if (!settings.QueryAvailable) hint.text = "Version filter unavailable for this game build.";
                favouritesToggle = Check("Only favourites",.62f,1,settings.OnlyFavourites.Value,value=>
                { settings.OnlyFavourites.Value = value; hint.text = DefaultHint; RefreshList(); });
                favouritesLabel = favouritesToggle.GetComponentInChildren<TMP_Text>(true);
                UpdateCount(); BeginSearch();
                manager.OnLobbyRefreshFinished+=SearchFinished;
            }
            catch
            {
                scrollBounds.offsetMax = originalScrollTop;
                toolbar.gameObject.SetActive(false); UnityEngine.Object.Destroy(toolbar.gameObject); throw;
            }
        }
        private string DefaultHint => "Show incompatible: "+(settings.ShowIncompatible.Value ? "ON" : "OFF")+". "+
            (settings.Favourites.Count==0 ? "No favourites yet. Star a server to save it." : "Other browser filters still apply.");
        internal void BeginSearch() { localVersion = Compatibility.Wire; dedicatedEmpty=false; hint.text="Searching servers..."; }
        private bool SearchBusy => (bool)serverBusy.Invoke(manager,null) || (bool)playerBusy.GetValue(manager);
        internal bool BeforeSearch()
        {
            if(queuedSearch==null && !SearchBusy) return true;
            if(queuedSearch==null) queuedSearch=list.StartCoroutine(SearchLater());
            hint.text="Filters changed. Updating after the current search..."; return false;
        }
        private IEnumerator SearchLater()
        {
            yield return new WaitForSecondsRealtime(.4f);
            while(!disposed && SearchBusy) yield return new WaitForSecondsRealtime(.25f);
            queuedSearch=null;
            if(!disposed && list!=null) list.GetListOfLobbies();
        }
        internal void SearchDedicated(object controller,LobbySearchFilter filter)
        {
            if(discovery==null) discovery=new BrowserDiscovery(controller,warn,empty=>
            { dedicatedEmpty=empty; SearchFinished(); });
            if(!discovery.Owns(controller)) throw new InvalidOperationException("Browser server controller changed");
            discovery.Start(filter);
        }
        private void SearchFinished() { hint.text=queuedSearch!=null ? "Updating filters..." : dedicatedEmpty ? "Steam returned no dedicated servers. Press Refresh to try again." : DefaultHint; }
        private void UpdateCount()
        {
            bool enabled = BrowserPresentation.UseFavouriteFilter(settings.OnlyFavourites.Value,settings.Favourites.Count);
            if (settings.OnlyFavourites.Value!=enabled) settings.OnlyFavourites.Value = enabled;
            favouritesToggle.SetIsOnWithoutNotify(enabled);
            favouritesToggle.interactable = settings.Favourites.Count>0;
            favouritesLabel.text = "Only favourites ("+settings.Favourites.Count+")";
            hint.text = DefaultHint;
        }
        private Toggle Check(string label,float left,float right,bool value,UnityAction<bool> changed)
        {
            var rect = Rect(label,toolbar); rect.anchorMin = new Vector2(left,1); rect.anchorMax = new Vector2(right,1); rect.offsetMin = new Vector2(8,-36); rect.offsetMax = new Vector2(-8,-4);
            // Build inactive so Toggle.OnEnable sees the assigned graphic on its first pass.
            rect.gameObject.SetActive(false);
            var hit = rect.gameObject.AddComponent<Image>(); hit.color = Color.clear; hit.canvasRenderer.cullTransparentMesh = false;
            var toggle = rect.gameObject.AddComponent<Toggle>(); toggle.toggleTransition = Toggle.ToggleTransition.None;
            var nav = toggle.navigation; nav.mode = Navigation.Mode.None; toggle.navigation = nav;
            // The only text child is the label; icons do not depend on TMP font glyphs.
            var text = Text("Label",rect,18); text.text = label; text.alignment = TextAlignmentOptions.MidlineLeft;
            Stretch(text.rectTransform); text.rectTransform.offsetMin = new Vector2(36,0); text.rectTransform.offsetMax = new Vector2(-4,0);
            text.enableAutoSizing = true; text.fontSizeMin = 14; text.fontSizeMax = 18;
            var square = Rect("Checkbox",rect); square.anchorMin = square.anchorMax = new Vector2(0,.5f); square.sizeDelta = new Vector2(24,24); square.anchoredPosition = new Vector2(16,0);
            var fill = square.gameObject.AddComponent<Image>(); fill.color = new Color(.19f,.21f,.23f,1); toggle.targetGraphic = fill;
            var iconRect = Rect("Check",square); Stretch(iconRect);
            var tick = iconRect.gameObject.AddComponent<DrawnBrowserIcon>(); tick.raycastTarget = false; tick.color = LobbyListItem.StatusSuccessColor;
            toggle.graphic = tick;
            toggle.SetIsOnWithoutNotify(value);
            toggle.onValueChanged.AddListener(changed); rect.gameObject.SetActive(true); return toggle;
        }
        internal void UpdateRow(LobbyListItem item)
        {
            if (!rows.TryGetValue(item,out var row))
            { row = new Row(this,item); rows.Add(item,row); }
            row.Bind(localVersion);
        }
        private void Favourite(Row row)
        {
            var result = settings.Favourites.Toggle(row.Key);
            if (result==FavouriteChange.Unavailable) { hint.text = "Server identity unavailable. Refresh and try again."; return; }
            if (result==FavouriteChange.LimitReached) { hint.text = "Favourite limit reached ("+Favourites.MaxCount+"). Remove one first."; return; }
            try { settings.SavedFavourites.Value = settings.Favourites.Save(); }
            catch (Exception ex) { warn("Favourite saving failed: "+ex.GetType().Name); hint.text = "Favourite changed for this session; saving failed."; }
            foreach (var value in rows.Values) value.UpdateStar();
            UpdateCount(); RefreshList();
        }
        private void RefreshList() { if (list!=null) updateList.Invoke(list,null); }
        internal void ApplyFavourites()
        {
            if (disposed || !(sortListField.GetValue(list) is List<LobbyListItem> sorted)) return;
            int position = 0; visited.Clear();
            // Stable partition the visible transforms. Keep the native sortList untouched,
            // so incremental insertion and name/ping/search sorting still use native order.
            for (int pass=0;pass<2;pass++)
            foreach (var item in sorted)
            {
                if (item==null || !rows.TryGetValue(item,out var row)) continue;
                bool favourite = settings.Favourites.Contains(row.Key);
                if (favourite != (pass==0) || !visited.Add(item)) continue;
                bool show = !BrowserPresentation.UseFavouriteFilter(settings.OnlyFavourites.Value,settings.Favourites.Count) || favourite;
                item.gameObject.SetActive(show);
                if (show) item.transform.SetSiblingIndex(position++);
            }
        }
        private TMP_Text Text(string name,RectTransform parent,float size)
        {
            var rect = Rect(name,parent); var text = rect.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = template.font; text.fontSharedMaterial = template.fontSharedMaterial; text.fontStyle = template.fontStyle; text.fontWeight = template.fontWeight;
            text.fontSize = size; text.color = Color.white; text.richText = false; text.raycastTarget = false; text.enableWordWrapping = false; text.overflowMode = TextOverflowModes.Ellipsis;
            return text;
        }
        private static T? Field<T>(object value,string name) where T:class => AccessTools.Field(value.GetType(),name)?.GetValue(value) as T;
        private static RectTransform Rect(string name,Transform parent) { var go = new GameObject(name,typeof(RectTransform)); go.layer = parent.gameObject.layer; var rect = (RectTransform)go.transform; rect.SetParent(parent,false); return rect; }
        private static void Stretch(RectTransform rect) { rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero; }
        public void Dispose() => Dispose(true);
        internal void Dispose(bool restoreList)
        {
            if (disposed) return; disposed = true;
            if(queuedSearch!=null && list!=null) list.StopCoroutine(queuedSearch); queuedSearch=null;
            if(manager!=null) manager.OnLobbyRefreshFinished-=SearchFinished;
            try { discovery?.Dispose(); }
            catch(Exception ex) { warn("Browser discovery cleanup failed: "+ex.GetType().Name); }
            discovery=null;
            if (toolbar!=null) { toolbar.gameObject.SetActive(false); UnityEngine.Object.Destroy(toolbar.gameObject); }
            if (scrollBounds!=null) scrollBounds.offsetMax = originalScrollTop;
            foreach (var row in rows.Values) row.Dispose(); rows.Clear();
            if (restoreList && list!=null) RefreshList();
        }

        private sealed class Row : IDisposable
        {
            private readonly BrowserUi owner;
            private readonly LobbyListItem item;
            private readonly TMP_Text name, mission;
            private readonly DrawnBrowserIcon star;
            private readonly HorizontalLayoutGroup layout;
            private readonly RectOffset originalPadding;
            private readonly Color missionColor;
            private readonly bool wrapping;
            private readonly TextOverflowModes overflow;
            private readonly Button button;
            private string originalMission = "";
            internal string Key { get; private set; } = "";
            internal Row(BrowserUi owner,LobbyListItem item)
            {
                this.owner = owner; this.item = item;
                name = Field<TMP_Text>(item,"lobbyNameText") ?? throw new InvalidOperationException("Browser name unavailable");
                mission = Field<TMP_Text>(item,"missionNameText") ?? throw new InvalidOperationException("Browser mission unavailable");
                layout = item.GetComponent<HorizontalLayoutGroup>() ?? throw new InvalidOperationException("Browser row layout unavailable");
                originalPadding = layout.padding;
                missionColor = mission.color; wrapping = mission.enableWordWrapping; overflow = mission.overflowMode;
                var rect = Rect("JOINCHECK favourite",item.transform); rect.anchorMin = rect.anchorMax = new Vector2(0,.5f); rect.sizeDelta = new Vector2(32,32); rect.anchoredPosition = new Vector2(20,0);
                rect.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
                var fill = rect.gameObject.AddComponent<Image>(); fill.color = new Color(.16f,.20f,.22f,1);
                button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = fill;
                var nav = button.navigation; nav.mode = Navigation.Mode.None; button.navigation = nav;
                var starRect = Rect("Star",rect); starRect.anchorMin=starRect.anchorMax=new Vector2(.5f,.5f); starRect.sizeDelta=new Vector2(24,24);
                star = starRect.gameObject.AddComponent<DrawnBrowserIcon>(); star.raycastTarget = false;
                button.onClick.AddListener(()=>owner.Favourite(this));
                // Name/mission positions are driven by native layout groups too. Reserve the
                // star using row padding so later rebuilds cannot overwrite the text inset.
                layout.padding = new RectOffset(originalPadding.left+40,originalPadding.right,originalPadding.top,originalPadding.bottom);
            }
            internal void Bind(string local)
            {
                var lobby = item.lobby;
                Key = "";
                if (lobby is ServerLobbyInstance server && server.details!=null)
                {
                    var address = server.details.m_NetAdr;
                    Key = Favourites.Dedicated(address.GetIP(),address.GetConnectionPort());
                }
                else if (lobby is PlayerLobbyInstance player)
                    Key = Favourites.Hosted(SteamMatchmaking.GetLobbyOwner(player.LobbyId).m_SteamID);
                else Key = "";
                originalMission = mission.text;
                bool incompatible = lobby.HostVersion.Length>0 && local.Length>0 && lobby.HostVersion!=local;
                mission.text = (incompatible ? "[Incompatible] " : "")+originalMission;
                mission.color = incompatible ? LobbyListItem.StatusDangerColor : missionColor;
                mission.enableWordWrapping = false; mission.overflowMode = TextOverflowModes.Ellipsis;
                UpdateStar();
            }
            internal void UpdateStar()
            {
                bool favourite = owner.settings.Favourites.Contains(Key);
                star.Use(favourite ? BrowserIconGeometry.StarFilled : BrowserIconGeometry.StarOutline);
                star.color = favourite ? LobbyListItem.StatusWarningColor : LobbyListItem.TextMutedColor;
                button.interactable = Key.Length>0;
            }
            public void Dispose()
            {
                if (button!=null) { button.gameObject.SetActive(false); UnityEngine.Object.Destroy(button.gameObject); }
                if (layout!=null) layout.padding = originalPadding;
                if (mission!=null) { mission.text = originalMission; mission.color = missionColor; mission.enableWordWrapping = wrapping; mission.overflowMode = overflow; }
            }
        }
    }
}
