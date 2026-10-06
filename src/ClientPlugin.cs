using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace KellysJOINCHECK
{
    [BepInPlugin(Id, "Kelly's DOORMAN", "1.3.4")]
    [BepInDependency("com.nikkorap.blueprinter", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ClientPlugin : BaseUnityPlugin
    {
        public const string Id = "kelly.nuclearoption.joincheck";
        internal static ClientPlugin? Instance;
        private Harmony? harmony;
        private ConfigEntry<KeyCode> reopen = null!;
        private readonly ConditionalWeakTable<ServerLobbyInstance, Dictionary<string, string>> metadata = new ConditionalWeakTable<ServerLobbyInstance, Dictionary<string, string>>();
        private LobbyInstance? lobby;
        private string report = "", lastFailure = "";
        private bool visible, attempted, browserStartPending;
        private Vector2 scroll;
        private float nextRefresh;
        private Rect window;
        private NativeJoinUi? ui;
        private ModManager? mods;
        private bool nativeUnavailable, preview, technical;
        private LobbyInstance? previewLobby;
        private LobbyDetailsModal? previewModal;
        private JoinSummary summary = new JoinSummary();
        private bool runtimeObserved,quitting;

        private void Awake()
        {
            if (Application.isBatchMode || SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) { enabled = false; return; }
            PreserveRuntimeHost();
            Instance = this;
            reopen = Config.Bind("UI", "ReopenKey", KeyCode.F8, "Reopen the last join diagnostic. Nothing is shown until a compatibility error occurs.");
            harmony = new Harmony(Id);
            BrowserHooks.Initialize(harmony,Config,message=>Logger.LogWarning(message),message=>Logger.LogInfo(message));
            try { mods=new ModManager(this,harmony,message=>Logger.LogWarning(message)); }
            catch(Exception ex) { Logger.LogWarning("Mods controls unavailable; normal menu and diagnostics preserved: "+ex.Message); }
            Patch(typeof(SteamLobby), "TryJoinLobby", nameof(JoinPrefix), true);
            Patch(typeof(NetworkManagerNuclearOption), "StartClient", nameof(StartClientPrefix), true);
            Patch(typeof(NetworkManagerNuclearOption), "ClientDisconnected", nameof(StoppedPostfix), false);
            Patch(typeof(ServerLobbyInstance), "SetRule", nameof(RulePrefix), true);
            Patch(typeof(GameManager), "SetDisconnectReason", nameof(DisconnectPostfix), false);
            Patch(typeof(JoinProgress), "Fail", nameof(FailPostfix), false);
            Patch(typeof(MapLoader), "CanLoad", nameof(MapPostfix), false);
            Patch(typeof(LobbyDetailsModal), "Show", nameof(DetailsPostfix), false);
            Patch(typeof(LobbyDetailsModal), "Hide", nameof(DetailsHidePostfix), false);
            Patch(typeof(JoinLobbyOverlay), "Open", nameof(OverlayPostfix), false);
            var auth = AccessTools.TypeByName("NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption");
            if (auth != null) Patch(auth, "HandleBuildHashMismatch", nameof(BuildPostfix), false);
            Logger.LogInfo("DOORMAN 1.3.4 loaded. Native Maps tab, embedded map previews, saved mod lists and detached Steam restart helper.");
        }

        private void PreserveRuntimeHost()
        {
            // The startup helper protects the shared host before any plugin
            // components exist. Avoid changing flags partway through loading:
            // that could fire OnDisable on another plugin's component.
            var host=gameObject;
            if((host.hideFlags&HideFlags.HideAndDontSave)!=HideFlags.HideAndDontSave)
                Logger.LogWarning("DOORMAN runtime host lacks lifetime protection. Replace the startup DLL from the complete client package.");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Logger.LogInfo("DOORMAN runtime host: "+host.name+"; flags="+host.hideFlags+"; active="+host.activeInHierarchy+"; enabled="+enabled);
        }

        private void Patch(Type type, string method, string callback, bool prefix)
        {
            try
            {
                var original = AccessTools.Method(type, method) ?? throw new MissingMethodException(type.FullName, method);
                var patch = new HarmonyMethod(AccessTools.Method(typeof(ClientPlugin), callback));
                if (prefix) harmony!.Patch(original, prefix: patch); else harmony!.Patch(original, postfix: patch);
                Logger.LogInfo("Patched " + type.Name + "." + method);
            }
            catch (Exception ex) { Logger.LogWarning("Diagnostic hook unavailable: " + type.Name + "." + method + " (" + ex.GetType().Name + ")"); }
        }
        private static void Guard(Action action)
        {
            try { action(); }
            catch (Exception ex) { Instance?.Logger.LogWarning("DOORMAN diagnostic failed: " + ex.GetType().Name); }
        }
        private static bool JoinPrefix(SteamLobby __instance,LobbyInstance __0,string __1,bool __2)
        {
            var self = Instance;
            if (self == null) return true;
            Guard(() =>
            {
                self.lobby = __0;
                self.attempted = false; // Map diagnostics start only when a real client connection starts.
                self.browserStartPending = __0.HostVersion == Compatibility.Wire;
                self.visible = false;
                self.ui?.Hide();
                self.preview = false;
                self.report = self.lastFailure = "";
            });
            try
            {
                string expanded=Protocol.Decode(k=>self.Read(__0,k),__0.HostVersion)??__0.HostVersion;
                if(self.mods?.BeforeJoin(__instance,__0,__1,__2,expanded)==false) return false;
            }
            catch(Exception ex) { self.Logger.LogWarning("Automatic mod matching unavailable: "+ex.Message); }
            if (__0.HostVersion != Compatibility.Wire) Guard(()=>self.Show("Incorrect version or mod compatibility signature"));
            return true;
        }
        private static void StartClientPrefix(ConnectOptions __0) => Guard(() =>
        {
            var self = Instance;
            if (self == null) return;
            bool sameTarget = self.lobby != null && self.browserStartPending &&
                ((!string.IsNullOrEmpty(__0.SteamLobbyIDString) && __0.SteamLobbyIDString == self.lobby.HostAddress) ||
                 (!string.IsNullOrEmpty(__0.UdpHost) && __0.UdpHost == self.lobby.UdpAddress && __0.UdpPort?.ToString() == self.lobby.UdpPort));
            if (!sameTarget)
            {
                self.lobby = null;
                self.report = self.lastFailure = "";
                self.visible = false;
                self.ui?.Detach();
                self.previewLobby = null; self.previewModal = null;
            }
            self.preview = false;
            self.attempted = true;
            self.browserStartPending = false;
        });
        private static void StoppedPostfix() => Guard(() =>
        {
            if (Instance == null) return;
            Instance.attempted = false;
            Instance.browserStartPending = false;
        });
        private static void RulePrefix(ServerLobbyInstance __instance, string __0, string __1) => Guard(() =>
        {
            if (Instance == null || !IsMetadataKey(__0) || __1 == null || __1.Length > Protocol.ChunkSize) return;
            Instance.metadata.GetOrCreateValue(__instance)[__0] = __1;
        });
        internal static bool IsMetadataKey(string key)
        {
            if (key == Protocol.Header) return true;
            for (int i = 0; i < Protocol.MaxChunks; i++) if (key == Protocol.Key(i)) return true;
            return false;
        }
        private static void FailPostfix(string __0) => Guard(() =>
        {
            if (Diagnostics.Relevant(__0)) Instance?.Show(__0);
        });
        private static void DisconnectPostfix(DisconnectInfo __0) => Guard(() =>
        {
            if (__0 != null && __0.ShowReason && Diagnostics.Relevant(__0.Message)) Instance?.Show(__0.Message);
        });
        private static void BuildPostfix(object __1) => Guard(() =>
        {
            object? value = AccessTools.Field(__1.GetType(), "BuildHash")?.GetValue(__1);
            if (!(value is uint server)) return;
            uint? mine = Compatibility.BuildHash();
            if (mine.HasValue && mine.Value == server) return;
            Instance?.Show("Different game build: server " + server.ToString("X8") + " | you " + (mine?.ToString("X8") ?? "unknown") + ". Update Nuclear Option and match the host's Steam beta branch.");
        });
        private static void MapPostfix(MapKey __0, bool __result) => Guard(() =>
        {
            var self = Instance;
            if (__result || self == null || !self.attempted) return;
            self.Show("MAP UNAVAILABLE: " + Diagnostics.Clean(__0.ToString()) + "\nInstall the host's exact map package/build. This key was rejected by the local map loader.");
        });

        private static void DetailsPostfix(LobbyDetailsModal __instance, LobbyInstance __1) => Guard(() =>
        {
            var self = Instance;
            if (self == null) return;
            self.previewModal = __instance; self.previewLobby = __1; self.preview = true;
            self.visible = false; self.ui?.Hide();
            self.RefreshPreview(true);
            if (self.ui != null) self.visible = self.ui.TryDock();
        });
        private static void DetailsHidePostfix() => Guard(() =>
        {
            var self = Instance;
            if (self == null) return;
            if (self.preview) { self.visible = false; self.ui?.Hide(); }
            self.mods?.CancelServerSetup();
            self.preview = false; self.ui?.Detach();
            self.previewModal = null; self.previewLobby = null;
        });
        private static void OverlayPostfix(JoinProgress __0) => Guard(() =>
        {
            var self = Instance;
            // Hide only the duplicate compatibility-error presentation once our checklist is interactive.
            if (self != null && self.visible && !self.preview && !__0.Join && self.ui?.IsOpen == true && self.ui.CanInteract && Diagnostics.Relevant(__0.Body))
                JoinLobbyOverlay.Close();
        });
        private void EnsureUi()
        {
            if (ui != null || nativeUnavailable) return;
            try { ui = new NativeJoinUi(() => { visible=false;mods?.CancelServerSetup(); },()=>{ visible=false; mods?.Open(); },SetupCurrentServer,JoinCurrentServer); }
            catch (Exception ex) { nativeUnavailable = true; Logger.LogWarning("Native checklist unavailable; using compact fallback: " + ex.GetType().Name); }
        }
        private void RefreshPreview(bool reset = false)
        {
            if (previewLobby == null || previewModal == null) return;
            EnsureUi();
            summary = Describe(previewLobby, "", out var text);
            ui?.Set(summary, previewLobby.LobbyNameSanitized, text, reset);
            RefreshServerActions();
            ui?.Attach(previewModal, () => { preview = true; RefreshPreview(true); visible = true; ui?.Show(); scroll = Vector2.zero; }, summary);
        }
        private string Read(LobbyInstance? target, string key)
        {
            if (target is ServerLobbyInstance server)
                return metadata.TryGetValue(server, out var values) && values.TryGetValue(key, out var value) ? value : "";
            if (target is PlayerLobbyInstance player) return Steamworks.SteamMatchmaking.GetLobbyData(player.LobbyId, key);
            return "";
        }
        private void Show(string failure)
        {
            lastFailure = failure;
            preview = false; technical = false;
            EnsureUi(); RefreshReport(true);
            visible = true;
            ui?.Show();
            scroll = Vector2.zero;
            Logger.LogInfo("Join diagnostic: " + report);
        }
        private void RefreshReport(bool reset = false)
        {
            summary = Describe(lobby, lastFailure, out report);
            ui?.Set(summary, lobby?.LobbyNameSanitized ?? "Direct connection", report, reset);
            RefreshServerActions();
        }
        private LobbyInstance? CurrentServer=>preview?previewLobby:lobby;
        internal string ExpandedFor(LobbyInstance target)=>Protocol.Decode(k=>Read(target,k),target.HostVersion)??target.HostVersion;
        private void SetupCurrentServer(){var target=CurrentServer;if(target!=null)mods?.SetupServer(target,ExpandedFor(target));}
        private void JoinCurrentServer(){var target=CurrentServer;if(target!=null&&target.HostVersion==Compatibility.Wire){HideServerWorkflow();SteamLobby.instance.TryJoinLobby(target,"",true);}}
        internal void RefreshServerActions()
        {
            var target=CurrentServer;if(target==null||mods==null){ui?.SetServerActions(false,"Setup mods",false,false,"");return;}
            mods.ServerActions(target,out var label,out var setup,out var join,out var message);ui?.SetServerActions(true,label,setup,join,message);
        }
        internal void ShowServerWorkflow(LobbyInstance target)
        {
            if(previewLobby!=target){preview=false;lobby=target;}
            EnsureUi();if(preview)RefreshPreview();else RefreshReport();visible=true;ui?.Show();
        }
        internal void HideServerWorkflow(){visible=false;ui?.Hide();}
        private JoinSummary Describe(LobbyInstance? target, string failure, out string text)
        {
            string wire = target?.HostVersion ?? "";
            string expanded = wire.Length > 0 ? Protocol.Decode(k => Read(target, k), wire) ?? wire : "";
            string local = Compatibility.Expanded();
            text = "Server: " + Diagnostics.Clean(target?.LobbyNameSanitized ?? "Direct connection / unknown server") + "\n\n" + Diagnostics.Clean(failure) + "\n\n";
            if (wire.Length > 0) text += Diagnostics.Compare(expanded, local);
            else text += "This connection did not provide a server compatibility list. Join through the server browser for pre-join mod details.\n";
            text += "\nServer compatibility: " + Diagnostics.Clean(wire.Length > 0 ? wire : "not supplied") + "\nYour compatibility: " + Diagnostics.Clean(Compatibility.Wire) + "\n\nUse Setup mods, then Join server. Proceed restarts and returns here when needed. Copy report to share with the host.";
            return JoinSummary.Create(expanded, local, wire, Compatibility.Wire, failure);
        }
        private void Update()
        {
            if(!runtimeObserved)
            {
                runtimeObserved=true;
                Logger.LogInfo("DOORMAN first runtime update: scene="+UnityEngine.SceneManagement.SceneManager.GetActiveScene().path+"; hostActive="+gameObject.activeInHierarchy+"; flags="+gameObject.hideFlags);
            }
            mods?.Tick();
            if (Input.GetKeyDown(reopen.Value) && (report.Length > 0 || previewLobby != null))
            {
                visible = !visible;
                if (visible) { if (preview && previewLobby != null) RefreshPreview(true); else { preview = false; EnsureUi(); RefreshReport(); } ui?.Show(); }
                else { ui?.Hide();mods?.CancelServerSetup(); }
            }
            if (visible && Input.GetKeyDown(KeyCode.Escape)) { visible = false; ui?.Hide();mods?.CancelServerSetup(); }
            // Rule queries can finish after a failed join. Refresh the visible report, never the join decision.
            if ((visible || previewLobby != null) && Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + 1;
                Guard(() => { if (preview && previewLobby != null) RefreshPreview(); else if (visible) RefreshReport(); });
            }
            if (visible) Guard(() => { if (ui != null && ui.CanInteract && !ui.IsOpen) ui.Show(); ui?.Tick(); });
        }
        private void OnGUI()
        {
            if (!visible || ui?.IsOpen == true) return;
            var previousMatrix = GUI.matrix;
            int previousDepth = GUI.depth;
            try
            {
                float scale = Mathf.Clamp(Mathf.Min(Screen.width / 1000f, Screen.height / 720f), 0.5f, 2.5f);
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
                GUI.depth = -10000;
                float w = Screen.width / scale, h = Screen.height / scale;
                float width = Mathf.Min(820, w - 16);
                window = new Rect((w - width) / 2, Mathf.Max(8, (h - 560) / 2), width, Mathf.Min(560, h - 16));
                var previousColor = GUI.color;
                GUI.color = new Color(.16f,.20f,.22f,1);
                GUI.DrawTexture(window,Texture2D.whiteTexture);
                GUI.color = previousColor;
                GUI.ModalWindow(731904, window, DrawWindow, "SERVER MOD CHECK");
            }
            finally { GUI.matrix = previousMatrix; GUI.depth = previousDepth; }
        }
        private void DrawWindow(int id)
        {
            var label = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false, fontSize = 16 };
            GUILayout.Space(12);
            scroll = GUILayout.BeginScrollView(scroll,false,true);
            GUILayout.Label(summary.Title, new GUIStyle(label) { fontSize = 24 });
            GUILayout.Label(summary.Help, label); GUILayout.Space(16);
            string text = report;
            if (preview && previewLobby != null) Describe(previewLobby, "", out text);
            if (technical) GUILayout.Label(text, label);
            else foreach (var action in summary.Actions)
            {
                GUILayout.Label(action.Action + "  " + action.Name, new GUIStyle(label) { fontSize = 20 });
                GUILayout.Label(action.Instruction, label); GUILayout.Space(14);
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy report", GUILayout.Height(34))) GUIUtility.systemCopyBuffer = text;
            if (GUILayout.Button(technical ? "Checklist" : "Details", GUILayout.Height(34))) technical = !technical;
            if(CurrentServer!=null&&mods!=null)
            {
                mods.ServerActions(CurrentServer,out var action,out var setup,out var join,out var message);GUILayout.Label(message,label);
                GUI.enabled=setup;if(GUILayout.Button(action,GUILayout.Height(34)))SetupCurrentServer();
                GUI.enabled=join;if(GUILayout.Button("Join server",GUILayout.Height(34)))JoinCurrentServer();GUI.enabled=true;
            }
            if (GUILayout.Button("Close", GUILayout.Height(34))) { visible = false;mods?.CancelServerSetup(); }
            GUILayout.EndHorizontal();
        }
        private void OnApplicationQuit(){quitting=true;}
        private void OnDisable()
        {
            if(Instance==this)Logger.LogWarning("DOORMAN runtime disabled: quitting="+quitting+"; firstUpdate="+runtimeObserved+"; scene="+UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
        }
        private void OnDestroy()
        {
            if(Instance==this)Logger.LogWarning("DOORMAN runtime destroyed: quitting="+quitting+"; firstUpdate="+runtimeObserved+"; scene="+UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            harmony?.UnpatchSelf();
            BrowserHooks.Dispose();
            mods?.Dispose();
            ui?.Dispose();
            if (Instance == this) Instance = null;
        }
    }
}
