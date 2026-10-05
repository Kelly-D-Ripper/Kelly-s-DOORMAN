using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;

namespace KellysJOINCHECK
{
    internal sealed class BrowserSettings
    {
        public readonly ConfigEntry<bool> ShowIncompatible, OnlyFavourites;
        public readonly ConfigEntry<string> SavedFavourites;
        public readonly Favourites Favourites;
        public bool QueryAvailable;
        public BrowserSettings(ConfigFile config)
        {
            ShowIncompatible = config.Bind("Browser","ShowIncompatibleServers",false,"Include different game/mod versions in browser searches. Other browser filters and all join checks still apply.");
            OnlyFavourites = config.Bind("Browser","FavouritesOnly",false,"Show only saved favourites in the current search results.");
            SavedFavourites = config.Bind("Browser","Favourites","","Saved public server endpoints and player-host Steam identities. Edited automatically by the star buttons.");
            Favourites = new Favourites(SavedFavourites.Value);
            // Recover an empty list left by an invisible 0.3.0 checkbox. The filter becomes
            // available as soon as the player saves a server, without deleting any favourites.
            if (!BrowserPresentation.UseFavouriteFilter(OnlyFavourites.Value,Favourites.Count)) OnlyFavourites.Value = false;
        }
    }
    internal static class BrowserHooks
    {
        private static BrowserSettings? settings;
        private static Action<string>? warn;
        private static Action<string>? info;
        [ThreadStatic] private static BrowserUi? queryingBrowser;
        private static readonly Dictionary<LobbyList,BrowserUi> browsers = new Dictionary<LobbyList,BrowserUi>();
        internal static void Initialize(Harmony harmony,ConfigFile config,Action<string> warning,Action<string> status)
        {
            settings = new BrowserSettings(config); warn = warning; info = status;
            Patch(harmony,typeof(LobbyList),"Start",nameof(BrowserStartPrefix),true);
            Patch(harmony,typeof(LobbyList),"OnDestroy",nameof(BrowserDestroyedPrefix),true);
            Patch(harmony,typeof(LobbyList),"UpdateLobbyList",nameof(ListPostfix));
            Patch(harmony,typeof(LobbyList),"InsertLobbyDataEntry",nameof(ListPostfix));
            Patch(harmony,typeof(LobbyListItem),"Show",nameof(RowPostfix));
            try
            {
                var original = AccessTools.Method(typeof(LobbyList),"GetListOfLobbies");
                BrowserDiscovery.ValidateContract();
                harmony.Patch(AccessTools.Method(BrowserDiscovery.ControllerType,"RequestServerLobbies"),prefix:new HarmonyMethod(typeof(BrowserHooks),nameof(DedicatedSearchPrefix)));
                harmony.Patch(original,prefix:new HarmonyMethod(typeof(BrowserHooks),nameof(SearchPrefix)),
                    postfix:new HarmonyMethod(typeof(BrowserHooks),nameof(SearchPostfix)),
                    finalizer:new HarmonyMethod(typeof(BrowserHooks),nameof(SearchFinalizer)),
                    transpiler:new HarmonyMethod(typeof(BrowserHooks),nameof(QueryTranspiler)),ilmanipulator:null);
                settings.QueryAvailable = true;
            }
            catch (Exception ex) { Warn("Browser compatibility checkbox unavailable: "+ex.GetType().Name); }
        }
        private static bool SearchPrefix(LobbyList __instance)
        {
            queryingBrowser=null;
            if(!browsers.TryGetValue(__instance,out var ui)) return true;
            if(!ui.BeforeSearch()) return false;
            queryingBrowser=ui; return true;
        }
        private static void SearchPostfix() { queryingBrowser=null; }
        private static Exception? SearchFinalizer(Exception? __exception) { queryingBrowser=null; return __exception; }
        private static bool DedicatedSearchPrefix(object __instance,LobbySearchFilter __0)
        {
            if(queryingBrowser==null) return true;
            try { queryingBrowser.SearchDedicated(__instance,__0); return false; }
            catch(Exception ex) { Warn("Dedicated browser discovery unavailable: "+ex.GetType().Name); return true; }
        }
        private static void Patch(Harmony harmony,Type type,string method,string callback,bool prefix=false)
        {
            try
            {
                var original = AccessTools.Method(type,method) ?? throw new MissingMethodException(type.FullName,method);
                var hook = new HarmonyMethod(typeof(BrowserHooks),callback);
                if (prefix) harmony.Patch(original,prefix:hook); else harmony.Patch(original,postfix:hook);
            }
            catch (Exception ex) { Warn("Browser hook unavailable: "+type.Name+"."+method+" ("+ex.GetType().Name+")"); }
        }
        private static IEnumerable<CodeInstruction> QueryTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var query = AccessTools.Method(typeof(SteamLobby),nameof(SteamLobby.GetLobbiesList));
            var rewrite = AccessTools.Method(typeof(BrowserHooks),nameof(ApplyQueryOption));
            return BrowserQueryPatch.Inject(instructions,query,rewrite);
        }
        // This call is injected only in LobbyList.GetListOfLobbies, immediately before its
        // existing Steam query. Direct joins, authentication and other queries are untouched.
        private static LobbySearchFilter ApplyQueryOption(LobbySearchFilter filter)
        {
            filter.ignoreVersionFilter |= settings?.ShowIncompatible.Value == true;
            foreach (var ui in browsers.Values)
                try { ui.BeginSearch(); } catch (Exception ex) { Warn("Browser version unavailable: "+ex.GetType().Name); }
            return filter;
        }
        private static void BrowserStartPrefix(LobbyList __instance)
        {
            if (settings==null || browsers.ContainsKey(__instance)) return;
            try
            {
                browsers.Add(__instance,new BrowserUi(__instance,settings,Warn));
                info?.Invoke("Browser controls attached above the native list; saved favourites: "+settings.Favourites.Count+"; version override: "+settings.QueryAvailable);
            }
            catch (Exception ex) { Warn("Browser controls unavailable: "+ex); }
        }
        private static void BrowserDestroyedPrefix(LobbyList __instance)
        {
            if (!browsers.TryGetValue(__instance,out var ui)) return;
            browsers.Remove(__instance); ui.Dispose(false);
        }
        private static void RowPostfix(LobbyListItem __instance,LobbyList __0,bool __result)
        {
            if (!__result || !browsers.TryGetValue(__0,out var ui)) return;
            try { ui.UpdateRow(__instance); }
            catch (Exception ex) { Warn("Browser row unavailable: "+ex.GetType().Name); }
        }
        private static void ListPostfix(LobbyList __instance)
        {
            if (!browsers.TryGetValue(__instance,out var ui)) return;
            try { ui.ApplyFavourites(); }
            catch (Exception ex) { Warn("Favourite filter unavailable: "+ex.GetType().Name); }
        }
        private static string lastWarning = "";
        private static void Warn(string text) { if (text==lastWarning) return; lastWarning = text; warn?.Invoke(text); }
        internal static void Dispose()
        {
            var owned = browsers.Values.ToArray(); browsers.Clear();
            foreach (var ui in owned) ui.Dispose();
            settings = null; warn = null; info = null; lastWarning = "";
            queryingBrowser=null;
        }
    }
}
