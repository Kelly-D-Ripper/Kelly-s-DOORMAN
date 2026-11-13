using System;
using System.Collections.Generic;
using System.Linq;
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
    [BepInPlugin(Id, "Kelly's DOORMAN", "1.4.0")]
    [BepInDependency("com.nikkorap.blueprinter", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed partial class ClientPlugin : BaseUnityPlugin
    {
        public const string Id = "kelly.nuclearoption.joincheck";
        internal static ClientPlugin? Instance;
        private Harmony? harmony;
        private ConfigEntry<KeyCode> reopen = null!;
        private readonly ConditionalWeakTable<ServerLobbyInstance, Dictionary<string, string>> metadata = new ConditionalWeakTable<ServerLobbyInstance, Dictionary<string, string>>();
        private LobbyInstance? lobby;
        private string report = "", lastFailure = "", localVersionNotice = "";
        private MapJoinDiagnostic? lastMapDiagnostic;
        private LobbyInstance? lastMapTarget;
        private readonly ChecklistCursor fallbackCursor = new ChecklistCursor();
        private bool visible, attempted, browserStartPending;
        private Vector2 scroll;
        private float nextRefresh;
        private Rect window;
        private NativeJoinUi? ui;
        private ModManager? mods;
        private readonly SupportReport support = new SupportReport(Paths.GameRootPath, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        private bool nativeUnavailable, preview, technical;
        private LobbyInstance? previewLobby;
        private LobbyDetailsModal? previewModal;
        private JoinSummary summary = new JoinSummary();
        private readonly ConditionalWeakTable<LobbyInstance,Dictionary<string,string>> nosmrDownloads=new ConditionalWeakTable<LobbyInstance,Dictionary<string,string>>();
        private bool nosmrDownloadBusy;
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
            catch(Exception ex) { RecordSupport("Startup", "Mods controls unavailable: "+ex.GetType().Name+": "+ex.Message, true); Logger.LogWarning("Mods controls unavailable; normal menu and diagnostics preserved: "+ex.Message); }
            Patch(typeof(SteamLobby), "TryJoinLobby", nameof(JoinPrefix), true);
            Patch(typeof(NetworkManagerNuclearOption), "StartClient", nameof(StartClientPrefix), true);
            Patch(typeof(NetworkManagerNuclearOption), "ClientDisconnected", nameof(StoppedPostfix), false);
            Patch(typeof(ServerLobbyInstance), "SetRule", nameof(RulePrefix), true);
            Patch(typeof(GameManager), "SetDisconnectReason", nameof(DisconnectPostfix), false);
            Patch(typeof(JoinProgress), "Fail", nameof(FailPostfix), false);
            Patch(typeof(MapLoader), "CanLoad", nameof(MapPostfix), false);
            Patch(typeof(LobbyDetailsModal), "Show", nameof(DetailsPrefix), true);
            Patch(typeof(LobbyDetailsModal), "Show", nameof(DetailsPostfix), false);
            Patch(typeof(LobbyDetailsModal), "Hide", nameof(DetailsHidePostfix), false);
            Patch(typeof(JoinLobbyOverlay), "Open", nameof(OverlayPostfix), false);
            var auth = AccessTools.TypeByName("NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption");
            if (auth != null) Patch(auth, "HandleBuildHashMismatch", nameof(BuildPostfix), false);
            try { StartGameplayChecks(); }
            catch(Exception ex) { RecordSupport("Gameplay file checks",ex.Message,true);Logger.LogWarning("Gameplay file checks unavailable: "+ex.Message); }
            Logger.LogInfo("DOORMAN "+SelfUpdateRelease.Installed+" loaded. Native Mods list groups active content with its supporting plugin; Maps, saved lists and detached Steam restart helper.");
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
                self.lastMapDiagnostic = null;
                self.lastMapTarget = null;
            });
            try
            {
                string expanded=self.ExpandedFor(__0);
                if(self.mods?.BeforeJoin(__instance,__0,__1,__2,expanded)==false){self.browserStartPending=false;return false;}
            }
            catch(Exception ex) { self.Logger.LogWarning("Automatic mod matching unavailable: "+ex.Message); if(self.RequiresGameplayFiles(__0)){self.browserStartPending=false;Guard(()=>self.Show("Exact gameplay files could not be verified: "+ex.Message));return false;} }
            if(!self.GameplayAllowsJoin(__0)){self.browserStartPending=false;Guard(()=>self.Show("Exact gameplay files differ or are still being checked. Use Setup mods before joining."));return false;}
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
                self.lastMapDiagnostic = null;
                self.lastMapTarget = null;
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
            if(__0.StartsWith("gfp",StringComparison.Ordinal))Instance.gameplayRemote.Remove(__instance);
        });
        internal static bool IsMetadataKey(string key)
        {
            if(NosmrReport.IsKey(key))return true;
            if (key == Protocol.Header) return true;
            if (key == "gfpv" || key == GameplayFingerprintProtocol.Header) return true;
            for (int i = 0; i < GameplayFingerprintProtocol.MaxChunks; i++) if (key == GameplayFingerprintProtocol.Key(i)) return true;
            for (int i = 0; i < Protocol.MaxChunks; i++) if (key == Protocol.Key(i)) return true;
            return false;
        }
        private static void DetailsPrefix(LobbyInstance __1) => Guard(() =>
        {
            // Start every on-demand rules query with a fresh snapshot; keys
            // absent from a later response must never survive from an old one.
            if(__1 is ServerLobbyInstance server)Instance?.metadata.Remove(server);
        });
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
        private static void MapPostfix(MapLoader __instance, MapKey __0, bool __result) => Guard(() =>
        {
            var self = Instance;
            if (__result || self == null || !self.attempted) return;
            var maps = (__instance.Maps ?? Array.Empty<MapDetails>()).Where(m => m != null)
                .Take(256).Select(m => new RegisteredMap { Key = m.PrefabName ?? "", Name = m.MapName ?? "" }).ToList();
            // Custom map loaders may register their maps outside the base
            // MapLoader's list. The loader's settled registry is authoritative
            // for these entries; merely finding a file is not.
            var custom = MapBundleBridge.ReadLoaded(out _, out var complete, out var settled);
            if (complete && settled) maps.AddRange(custom.Where(m => m.Registered)
                .Select(m => new RegisteredMap { Key = m.MapKey, Name = m.Name }));
            self.lastMapDiagnostic = MapJoinDiagnostic.Create(__0.Path ?? "", "Map", self.lobby?.MapNameSanitized ?? "", maps);
            self.lastMapTarget = self.lobby;
            self.Show(self.lastMapDiagnostic.Report);
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
            if(ui!=null&&!ui.IsAlive){ui.Dispose();ui=null;}
            if (ui != null || nativeUnavailable) return;
            try { ui = new NativeJoinUi(() => { visible=false;mods?.CancelServerSetup(); },()=>{ visible=false; mods?.Open(); },SetupCurrentServer,JoinCurrentServer,CopyDiagnosticReport,RestartCurrentServerManually,DownloadReportedMod); }
            catch (Exception ex) { nativeUnavailable = true;RecordSupport("Checklist UI",ex.GetType().Name+": "+ex.Message,true); Logger.LogWarning("Native checklist unavailable; using compact fallback: " + ex.GetType().Name); }
        }
        private void RefreshPreview(bool reset = false)
        {
            if (previewLobby == null || previewModal == null) return;
            EnsureUi();
            bool sameMap = MapIssueFor(previewLobby)!=null;
            summary = Describe(previewLobby, sameMap?lastFailure:"", out var text);
            report=text;lobby=previewLobby;
            if(!sameMap){lastFailure="";lastMapDiagnostic=null;lastMapTarget=null;}
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
            if (!failure.StartsWith("MAP UNAVAILABLE:", StringComparison.OrdinalIgnoreCase)) { lastMapDiagnostic = null;lastMapTarget=null; }
            lastFailure = failure;
            RecordSupport("Join failure", failure, true);
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
        internal MapJoinDiagnostic? MapIssueFor(LobbyInstance target)=>target==lastMapTarget?lastMapDiagnostic:null;
        internal void RefreshMapView(LobbyInstance target)
        {if(target!=CurrentServer)return;if(preview)RefreshPreview(true);else RefreshReport(true);}
        internal string ExpandedFor(LobbyInstance target)
        {
            string? legacy=Protocol.Decode(k=>Read(target,k),target.HostVersion);
            if(legacy!=null)return legacy;
            var files=GameplayFilesFor(target);
            string named=files!=null?GameplayFingerprintPolicy.Expanded(files,target.HostVersion):"";
            return named.Length>0?named:target.HostVersion;
        }
        internal string NosmrProtocolFor(LobbyInstance target)=>Read(target,"nomm_v");
        internal string NosmrStamp(LobbyInstance target)=>Read(target,"nomm_v")+"|"+Read(target,"nomm_c")+"|"+Read(target,"nomm_h0")+Read(target,"nomm_h1");
        internal NosmrReport.Result? NosmrFor(LobbyInstance target,NosmrReport.Index index)=>NosmrReport.Read(k=>Read(target,k),index);
        internal bool ReportedDownloadBusy=>nosmrDownloadBusy;
        private void SetupCurrentServer(){var target=CurrentServer;if(target!=null)mods?.SetupServer(target,ExpandedFor(target));}
        private void DownloadReportedMod(ModAction action)
        {
            var target=CurrentServer;if(target==null||mods==null||nosmrDownloadBusy||mods.ServerSetupBusy)return;
            try
            {
                var index=mods.NosmrIndex()??throw new InvalidOperationException("Mod information is still loading.");
                var current=NosmrReport.Read(k=>Read(target,k),index);
                if(current==null||!current.Verified||!current.Entries.Any(e=>e.Resolved&&e.Id==action.PackageId&&e.Version==action.Version))throw new InvalidOperationException("The server's mod list changed. Refresh it before downloading.");
                if(!index.Records.TryGetValue(action.PackageId,out var record))throw new InvalidOperationException("This mod needs a manual download.");
                string installed=mods.ReportedLocalVersion(index,action.PackageId);
                if(installed.Length>0&&ModReleaseVersion.Compare(installed,action.Version)==0)
                {
                    nosmrDownloads.GetOrCreateValue(target)[action.PackageId]="This version is already installed.";
                    if(preview&&target==previewLobby)RefreshPreview(true);else if(visible&&target==lobby)RefreshReport(true);
                    return;
                }
                nosmrDownloads.GetOrCreateValue(target)[action.PackageId]="Downloading version "+action.Version+".";
                var task=mods.DownloadReported(record,action.Version);nosmrDownloadBusy=true;
                if(preview&&target==previewLobby)RefreshPreview(true);else if(visible&&target==lobby)RefreshReport(true);
                StartCoroutine(FinishReportedDownload(target,action,task));
            }
            catch(Exception ex){nosmrDownloads.GetOrCreateValue(target)[action.PackageId]="Download failed: "+Diagnostics.Clean(ex.Message);RecordSupport("NOSMR download",action.Name+": "+ex.Message,true);if(preview&&target==previewLobby)RefreshPreview(true);}
        }
        private System.Collections.IEnumerator FinishReportedDownload(LobbyInstance target,ModAction action,System.Threading.Tasks.Task<string> task)
        {
            while(!task.IsCompleted)yield return null;
            nosmrDownloadBusy=false;
            string status;
            if(task.IsCanceled)status="Download cancelled.";
            else if(task.IsFaulted)status="Download failed: "+Diagnostics.Clean(task.Exception!.GetBaseException().Message);
            else if(action.Bundle)
            {
                try
                {
                    var index=mods?.NosmrIndex()??throw new InvalidOperationException("Mod information is unavailable.");
                    if(!index.Records.TryGetValue(action.PackageId,out var record))throw new InvalidOperationException("Mod information is unavailable.");
                    var current=NosmrReport.Read(k=>Read(target,k),index);
                    if(current==null||!current.Verified||!current.Entries.Any(e=>e.Resolved&&e.Id==action.PackageId&&e.Version==action.Version))throw new InvalidOperationException("The server's reported mod list changed. Refresh it and try again.");
                    string contentVersion=mods!.InstallReportedBundle(record,action.Version,task.Result);
                    status="Package "+action.Version+" installed. Restart the game to load it.";
                    if(contentVersion!=action.Version)status+=" Its content version is "+Diagnostics.Clean(contentVersion)+". Tell the host if the server still rejects it.";
                }
                catch(Exception ex){status="Install failed: "+Diagnostics.Clean(ex.Message);mods?.DiscardReportedBundle(task.Result);}
            }
            else status="Version "+action.Version+" downloaded. Restart to install.";
            nosmrDownloads.GetOrCreateValue(target)[action.PackageId]=status;
            RecordSupport("NOSMR download",action.Name+": "+status,task.IsFaulted||task.IsCanceled||status.StartsWith("Install failed:",StringComparison.Ordinal));
            if(preview&&target==previewLobby)RefreshPreview(true);else if(visible&&target==lobby)RefreshReport(true);
        }
        private void RestartCurrentServerManually(){var target=CurrentServer;if(target!=null)mods?.RestartServerManually(target,ExpandedFor(target));}
        private void JoinCurrentServer(){var target=CurrentServer;if(target==null)return;if(mods?.TryReportedJoin(target)==true)return;if(target.HostVersion==Compatibility.Wire&&GameplayAllowsJoin(target)){HideServerWorkflow();SteamLobby.instance.TryJoinLobby(target,"",true);}}
        internal void RefreshServerActions()
        {
            var target=CurrentServer;if(target==null||mods==null){ui?.SetServerActions(false,"Setup mods",false,false,"");return;}
            mods.ServerActions(target,out var label,out var setup,out var join,out var message);
            bool manual=!RuntimePlatform.IsWine&&mods.CanRestartServerManually(target);
            ui?.SetServerActions(true,label,setup,join,message,manual);
        }
        internal void ShowServerWorkflow(LobbyInstance target)
        {
            if(previewLobby!=target){preview=false;lobby=target;}
            EnsureUi();if(preview)RefreshPreview();else RefreshReport();visible=true;ui?.Show();
        }
        internal void HideServerWorkflow(){visible=false;ui?.Hide();}
        internal void SetServerDownloadProgress(LobbyInstance target,bool active,float fraction)
        {if(!active||CurrentServer==target)ui?.SetDownloadProgress(active,fraction);}
        internal void RecordSupport(string category,string message,bool failure=false)=>support.Record(category,message,failure);
        private string CopyDiagnosticReport()
        {
            string text=report;
            var target=CurrentServer;
            try { if(target!=null){Describe(target,preview&&MapIssueFor(target)==null?"":lastFailure,out var fresh);text=fresh;} }
            catch(Exception ex){RecordSupport("Compatibility refresh",ex.GetType().Name+": "+ex.Message,true);}
            var context=new List<string>
            {
                "DOORMAN package: "+SelfUpdateRelease.Installed+"; client assembly: "+typeof(ClientPlugin).Assembly.GetName().Version,
                "Game: "+Application.version+"; BepInEx: "+typeof(BaseUnityPlugin).Assembly.GetName().Version,
                "Platform: "+Environment.OSVersion.Platform+(RuntimePlatform.IsWine?" (Wine/Proton detected)":"")+"; plugins: "+Paths.PluginPath
            };
            try { mods?.AppendSupportContext(context,target); }
            catch(Exception ex) { context.Add("Some diagnostic context was unavailable: "+ex.GetType().Name+": "+ex.Message); }
            var brief=new System.Text.StringBuilder();
            brief.AppendLine("DOORMAN report for the server host");brief.AppendLine("Server: "+Diagnostics.Clean(target?.LobbyNameSanitized??"Unknown"));
            if(lastFailure.Length>0)brief.AppendLine("Join problem: "+Diagnostics.Clean(lastFailure));
            if(target!=null)
            {
                string protocol=Read(target,"nomm_v");
                var index=protocol=="2"?mods?.NosmrIndex():null;
                var host=index!=null?NosmrReport.Read(k=>Read(target,k),index):null;
                brief.AppendLine("NOSMR: "+NosmrReport.SupportStatus(protocol,mods?.NosmrIndexState()??"unavailable",host));
                if(host!=null)
                {
                    brief.AppendLine("Host's NOSMR modpack"+(host.Verified?":":" (some names could not be verified):"));
                    foreach(var entry in host.Entries)
                    {
                        bool sameVersion=false;
                        brief.Append("- ").Append(Diagnostics.Clean(entry.Name));if(entry.Version.Length>0)brief.Append(" ").Append(Diagnostics.Clean(entry.Version));
                        if(entry.Required)brief.Append(" (host marked required)");
                        if(entry.Resolved&&entry.Id.Length>0)
                        {
                            string local=mods!.ReportedLocalVersion(index!,entry.Id);
                            sameVersion=local.Length>0&&entry.Version.Length>0&&ModReleaseVersion.Compare(local,entry.Version)==0;
                            if(local.Length>0)brief.Append(" | Player reports ").Append(Diagnostics.Clean(local)).Append(sameVersion?" (same version)":" (different number)");
                        }
                        if(!sameVersion&&entry.Id.Length>0&&nosmrDownloads.TryGetValue(target,out var states)&&states.TryGetValue(entry.Id,out var state))brief.Append(" | ").Append(Diagnostics.Clean(state));
                        brief.AppendLine();
                    }
                    brief.AppendLine("Package and in-game versions can use different numbers. Check the server's loaded mods; if the NOSMR modpack is outdated, update its mod list.");
                }
            }
            return brief.ToString()+"\n--- Technical details ---\n"+support.Compose(text,context);
        }
        private JoinSummary Describe(LobbyInstance? target, string failure, out string text)
        {
            string wire = target?.HostVersion ?? "";
            string expanded = wire.Length > 0&&target!=null ? ExpandedFor(target) : "";
            bool localWireVerified;
            string local = mods != null ? mods.DiagnosticCompatibility(out localVersionNotice, out localWireVerified)
                : Compatibility.ForDiagnostics(false, false, "", out localVersionNotice, out localWireVerified);
            text = "Server: " + Diagnostics.Clean(target?.LobbyNameSanitized ?? "Direct connection / unknown server") + "\n\n" + Diagnostics.Clean(failure) + "\n\n";
            if (wire.Length > 0) text += Diagnostics.Compare(expanded, local, localWireVerified);
            else text += "This connection did not provide a server compatibility list. Join through the server browser for pre-join mod details.\n";
            if (localVersionNotice.Length > 0) text += localVersionNotice + "\n";
            text += "\nServer compatibility: " + Diagnostics.Clean(wire.Length > 0 ? wire : "not supplied") + "\nYour compatibility: " + Diagnostics.Clean(Compatibility.Wire) + "\n\nUse Setup mods, then Join server. Proceed restarts and returns here when needed. Copy report to share with the host.";
            var wireExcluded=ServerCompatibilityPlugins.Exclusions(BepInEx.Bootstrap.Chainloader.PluginInfos.Keys,wire,Compatibility.Wire,local,ModManager.LoadedGrowlerUnit());
            if(wireExcluded.Length>0)text+="\n"+ServerCompatibilityPlugins.Names(wireExcluded)+" changes multiplayer compatibility. Setup mods disables its verified components and required dependents for this session.\n";
            var view = JoinSummary.Create(expanded, local, wire, Compatibility.Wire, failure,wireExcluded,
                failure == lastFailure ? lastMapDiagnostic : null);
            if(target!=null){GameplayFingerprintUi.Apply(view,CompareGameplayFiles(target),RequiresGameplayFiles(target));text+=GameplayReport(target);}
            if(target!=null && Read(target,"nomm_v")=="2")
            {
                var index=mods?.NosmrIndex();
                if(index==null) { view.Help+="\nLoading NOSMR mod names...";text+="\nLoading NOSMR mod names.\n"; }
                else
                {
                    var reported=NosmrReport.Read(k=>Read(target,k),index);
                    if(reported!=null)
                    {
                        string note="Host's reported modpack: this may differ from the server's loaded files. Downloaded mods must still match the server.";
                        if(!reported.Verified)note+=" Some package names could not be verified.";
                        if(view.Status==JoinStatus.Unknown){view.Title="Host's reported mod list";view.Help="This is the host's reported list. Download items below; the server's loaded files cannot be verified.";}
                        else view.Help+="\n"+note;
                        text+="\n"+note+"\n";
                        foreach(var item in reported.Entries)
                        {
                            string name=Diagnostics.Clean(item.Name), version=Diagnostics.Clean(item.Version);
                            string label=item.Required?"HOST REPORT: REQUIRED":"HOST REPORT";
                            string instruction=version.Length==0?"Version not supplied.":"Reported version: "+version+".";
                            string installed=item.Resolved?mods!.ReportedLocalVersion(index,item.Id):"";
                            if(installed.Length>0&&version.Length>0&&ModReleaseVersion.Compare(installed,item.Version)==0)
                            {
                                view.Actions.Add(new ModAction {Action="VERSION MATCH",Name=name,Instruction="Installed: "+Diagnostics.Clean(installed)+". Host reports the same version."});
                                text+="VERSION MATCH: "+name+" "+version+" (version only; server file not verified)\n";
                                continue;
                            }
                            if(item.Id.Length>0&&nosmrDownloads.TryGetValue(target,out var states)&&states.TryGetValue(item.Id,out var status))instruction+=" "+status;
                            index.Records.TryGetValue(item.Id,out var record);
                            string repository=record==null?"":(record.Owner??"")+"/"+(record.Repository??"");if(!GitHubRepository.Valid(repository))repository="";
                            var artifacts=(record?.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Version==item.Version).Take(2).ToArray();
                            bool bundle=artifacts.Length==1&&(artifacts[0].FileName??"").EndsWith(".nobp",StringComparison.OrdinalIgnoreCase);
                            bool supported=bundle||artifacts.Length==1&&((artifacts[0].FileName??"").EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||(artifacts[0].FileName??"").EndsWith(".zip",StringComparison.OrdinalIgnoreCase));
                            if(!supported||!reported.Verified)instruction+=repository.Length>0?" Install manually using Visit GitHub.":" Ask the host for a manual download link.";
                            view.Actions.Add(new ModAction { Action=label,Name=name,Instruction=instruction,GitHub=repository,PackageId=item.Id,Version=item.Version,Bundle=bundle,Download=reported.Verified&&item.Resolved&&supported&&repository.Length>0&&!nosmrDownloadBusy });
                            text+=label+": "+name+(version.Length>0?" "+version:"")+" | "+instruction+"\n";
                        }
                    }
                }
            }
            // Loading diagnostics remain in the copied report and Details. They
            // do not need to consume the checklist space on every server.
            return view;
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
            if (visible) Guard(() => {
                if(ui!=null&&!ui.IsAlive)
                { if(preview&&previewLobby!=null&&previewModal!=null)RefreshPreview(true);else{EnsureUi();RefreshReport(true);} }
                if (ui != null && ui.CanInteract && !ui.IsOpen) ui.Show(); ui?.Tick();
            });
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
        private void LateUpdate()
        {
            if (visible && ui?.IsOpen == true) ui.KeepCursorAvailable();
            if (visible && nativeUnavailable)
            { fallbackCursor.Open(); fallbackCursor.KeepAvailable(); }
            else fallbackCursor.Close(false);
        }
        private void DrawWindow(int id)
        {
            var label = new GUIStyle(GUI.skin.label) { wordWrap = true, richText = false, fontSize = 16 };
            GUILayout.Space(12);
            scroll = GUILayout.BeginScrollView(scroll,false,true);
            GUILayout.Label(summary.Title, new GUIStyle(label) { fontSize = 24 });
            GUILayout.Label(summary.Help, label); GUILayout.Space(16);
            if (technical) GUILayout.Label(summary.PlayerDetails(), label);
            else foreach (var action in summary.Actions)
            {
                GUILayout.Label(action.Action + "  " + action.Name, new GUIStyle(label) { fontSize = 20 });
                GUILayout.Label(action.Instruction, label); GUILayout.Space(14);
            }
            GUILayout.EndScrollView();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Copy report", GUILayout.Height(34))) GUIUtility.systemCopyBuffer = CopyDiagnosticReport();
            if (GUILayout.Button(technical ? "Checklist" : "Details", GUILayout.Height(34))) technical = !technical;
            if(CurrentServer!=null&&mods!=null)
            {
                mods.ServerActions(CurrentServer,out var action,out var setup,out var join,out var message);GUILayout.Label(message,label);
                GUI.enabled=setup;if(GUILayout.Button(action,GUILayout.Height(34)))SetupCurrentServer();
                GUI.enabled=join;if(GUILayout.Button("Join server",GUILayout.Height(34)))JoinCurrentServer();GUI.enabled=true;
                if(!RuntimePlatform.IsWine&&mods.CanRestartServerManually(CurrentServer)&&GUILayout.Button("Restart manually",GUILayout.Height(34)))RestartCurrentServerManually();
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
            gameplayInventory?.Dispose();
            if(Instance==this)Logger.LogWarning("DOORMAN runtime destroyed: quitting="+quitting+"; firstUpdate="+runtimeObserved+"; scene="+UnityEngine.SceneManagement.SceneManager.GetActiveScene().path);
            harmony?.UnpatchSelf();
            BrowserHooks.Dispose();
            mods?.Dispose();
            ui?.Dispose();
            fallbackCursor.Close(false);
            if (Instance == this) Instance = null;
        }
    }
}

