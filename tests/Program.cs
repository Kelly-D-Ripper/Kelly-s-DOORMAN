using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class Program
{
    private static int checks;
    private static void Check(bool pass, string name)
    {
        checks++;
        if (!pass) throw new Exception("FAILED: " + name);
    }
    private static string V(string mods) => "0.34_com.nikkorap.blueprinter-v2.0.1_" + mods;
    private static string Read(Dictionary<string,string> data, string key) => data.TryGetValue(key, out var value) ? value : "";
    private static int Main(string[] args)
    {
        string server = V("--Aryx_F99-v1.1.3_--MiG-15-v1.1.2_--Weapons-v1.1.1");
        string local = V("--Aryx_F99-v1.1.2_--Chimera-v1.2.0_--Weapons-v1.1.1");
        string result = Diagnostics.Compare(server, local);
        Check(result.Contains("MISSING: MiG-15"), "missing bundle");
        Check(result.Contains("WRONG VERSION: Aryx_F99"), "underscore name and wrong version");
        Check(result.Contains("EXTRA: Chimera"), "extra bundle");
        Check(!result.Contains("MISSING: Weapons") && !result.Contains("WRONG VERSION: Weapons"), "matching bundle");
        Check(Signature.Parse(V("NOBUNDLES")).Mods.Count == 1 && Signature.Parse(V("NOBUNDLES")).Complete, "no bundles retains loader");
        Check(Diagnostics.Compare(V("NOBUNDLES"), "0.34").Contains("MISSING: com.nikkorap.blueprinter"), "missing loader");
        Check(Diagnostics.Compare("0.35", "0.34").Contains("GAME VERSION"), "game update mismatch");
        Check(Diagnostics.Compare(server, server).Contains("strings match"), "matching signatures");
        Check(Diagnostics.Compare(V("--B-v1_--A-v1"), V("--A-v1_--B-v1")).Contains("loading order"), "order-only mismatch");
        Check(!Signature.Parse("0.34_a1b2c3d4e5f6").Complete, "opaque hash");
        Check(!Diagnostics.Compare("0.34_a1b2c3d4e5f6", local).Contains("EXTRA:"), "no fabricated differences from hash");
        Check(Diagnostics.Compare("0.34_a1b2c3d4e5f6","0.34").Trim() == "Mod list unavailable. Ask the host to install DOORMAN Server.", "unavailable details use one short host instruction");
        Check(!Signature.Parse(V("--A-v1_--A-v2")).Complete, "duplicate ambiguous name");
        Check(!Signature.Parse("0.34_unknown_suffix").Complete, "unsupported loader scheme");
        Check(!Signature.Parse("").Complete, "empty signature");
        var checklist = JoinSummary.Create(server, local, server, local, "");
        Check(checklist.Status == JoinStatus.Changes && checklist.Actions.Count == 3, "only three actionable differences in checklist");
        Check(checklist.Title == "3 changes needed", "plain summary count");
        Check(checklist.Actions.Any(x => x.Action == "INSTALL" && x.Name == "MiG-15" && x.Instruction.Contains("1.1.2")), "missing checklist action");
        Check(checklist.Actions.Any(x => x.Action == "CHANGE VERSION" && x.Name == "Aryx F99" && x.Instruction.Contains("1.1.3") && x.Instruction.Contains("1.1.2")), "friendly name and both versions");
        Check(checklist.Actions.Any(x => x.Action == "REMOVE" && x.Name == "Chimera"), "extra checklist action");
        Check(checklist.Matching.Count == 2 && checklist.Matching.Any(x => x.Name == "Blueprinter"), "matching mods separate and loader name readable");
        Check(!checklist.Help.Contains("hash") && !checklist.Help.Contains("compatibility"), "main instruction contains no implementation details");
        var match = JoinSummary.Create(server,server,server,server,"");
        Check(match.Status == JoinStatus.Match && match.Actions.Count == 0, "matching checklist");
        Check(JoinSummary.Create("0.34_abcdabcdabcd","0.34_abcdabcdabcd","0.34_abcdabcdabcd","0.34_abcdabcdabcd","").Status == JoinStatus.Match, "matching hashes can indicate match without invented names");
        var unknown = JoinSummary.Create("0.34_abcdabcdabcd",local,"0.34_abcdabcdabcd",local,"");
        Check(unknown.Status == JoinStatus.Unknown && unknown.Actions.Count == 0, "opaque checklist does not tell player to remove all mods");
        Check(JoinSummary.Create("","0.34","","0.34","").Status == JoinStatus.Unknown, "no server data cannot indicate ready");
        var build = JoinSummary.Create(server,server,server,server,"Different game build: abc");
        Check(build.Actions.Any(x => x.Name == "Nuclear Option" && x.Action == "UPDATE"), "build error overrides matching mods");
        var map = JoinSummary.Create(server,server,server,server,"MAP UNAVAILABLE: cm.test.abc");
        Check(map.Actions.Any(x => x.Name == "Server map"), "map error remains actionable when mods match");
        Check(JoinSummary.Create(server,server,server,server,"Incorrect version").Status != JoinStatus.Match, "failed join never reassures ready");
        Check(JoinSummary.Create("0.35_xxxxxxxxxxxx",local,"0.35_xxxxxxxxxxxx",local,"").Actions.Any(x => x.Name == "Nuclear Option"), "known game mismatch retained with unknown mod data");
        Check(JoinSummary.Create(V("--A-v1_--B-v1"),V("--B-v1_--A-v1"),"wire1","wire2","").Actions.Single().Action == "CHECK FILES", "order-only actionable guidance");
        Check(JoinLayout.CanDock(1920,1080,1400,140,880,1), "wide browser side dock fits");
        Check(!JoinLayout.CanDock(1280,720,1020,40,650,1), "narrow browser uses popup");
        Check(!JoinLayout.CanDock(1920,1080,1400,20,500,1), "short side dock rejected");
        Check(!JoinLayout.CanDock(1920,1080,1400,-10,750,1), "dock cannot leave top edge");
        Check(!JoinLayout.CanDock(1920,1080,1400,300,1200,1), "dock cannot leave bottom edge");
        Check(!JoinLayout.CanDock(1920,1080,1400,140,880,0), "invalid scale rejected");
        Check(!JoinLayout.CanDock(float.NaN,1080,1400,140,880,1), "invalid viewport rejected");
        Check(JoinLayout.CanDock(3840,2160,2800,280,1760,2), "same side layout at 4K");
        Check(JoinLayout.BackgroundAlpha == 1, "background is fully opaque");
        Check(JoinLayout.CanDock(3440,1440,2469,373,1147,1440f/1080), "reported ultrawide screenshot has space for the complete dock");
        foreach (float panelHeight in new[]{580f,740f})
        foreach (bool showMatching in new[]{false,true})
        foreach (float titleHeight in new[]{34f,70f})
        {
            var geometry = JoinLayout.Measure(panelHeight,titleHeight,104,48,showMatching);
            Check(geometry.ServerTop >= 20+titleHeight && geometry.HelpTop >= geometry.ServerTop+26, "wrapped header text blocks do not overlap");
            Check(geometry.ListTop >= geometry.HelpTop+104 && geometry.ListHeight >= 140, "wrapped instructions leave a useful scrolling area");
            float footerTop = panelHeight-geometry.FooterBottom-geometry.FooterHeight;
            Check(geometry.ListTop+geometry.ListHeight <= footerTop-JoinLayout.Gap, "scroll list stays above the footer controls");
            Check(geometry.FooterBottom >= 20+JoinLayout.ActionHeight+JoinLayout.Gap, "footer cannot overlap action buttons");
            Check(geometry.ListTop+geometry.ListHeight+geometry.ListBottom <= panelHeight, "all reserved regions fit the panel");
        }
        ServerCacheChecks();
        FavouriteChecks();
        IconChecks();
        ServerFilterChecks();
        QueryPatchChecks();
        var encoded = Protocol.Encode("0.34_a1b2c3d4e5f6", server);
        Check(Protocol.Decode(k => Read(encoded,k), "0.34_a1b2c3d4e5f6") == server, "hashed metadata roundtrip");
        Check(Protocol.Decode(k => Read(encoded,k), "0.34_other") == null, "stale metadata");
        Check(encoded.All(x => x.Key.Length + x.Value.Length <= 127), "server per-rule byte ceiling");
        Check(Protocol.Decode(k => "", "0.34") == null, "absent companion");
        Check(Protocol.Decode(k => k == Protocol.Header ? "1:999" : "AAAA", "0.34") == null, "unbounded chunk count rejected");
        Check(Protocol.Decode(k => k == Protocol.Header ? "2:1" : "AAAA", "0.34") == null, "unknown protocol rejected");
        var partial = new Dictionary<string,string>(encoded); partial.Remove(Protocol.Key(0));
        Check(Protocol.Decode(k => Read(partial,k), "0.34_a1b2c3d4e5f6") == null, "partial rule delivery rejected");
        var corrupt = new Dictionary<string,string>(encoded); corrupt[Protocol.Key(0)] = "@@@@";
        Check(Protocol.Decode(k => Read(corrupt,k), "0.34_a1b2c3d4e5f6") == null, "invalid base64 rejected");
        Check(Protocol.Decode(k => k == Protocol.Header ? "1:1" : "AA==", "0.34") == null, "invalid compressed payload");
        using var compressed = new MemoryStream();
        using (var zip = new DeflateStream(compressed, CompressionLevel.Optimal, true))
        { var bytes = Encoding.UTF8.GetBytes(new string('A', 100000)); zip.Write(bytes); }
        string bomb = Convert.ToBase64String(compressed.ToArray());
        int bombChunks = (bomb.Length + Protocol.ChunkSize - 1) / Protocol.ChunkSize;
        var bombData = new Dictionary<string,string> { [Protocol.Header] = "1:" + bombChunks };
        for (int i = 0; i < bombChunks; i++) bombData[Protocol.Key(i)] = bomb.Substring(i*Protocol.ChunkSize, Math.Min(Protocol.ChunkSize,bomb.Length-i*Protocol.ChunkSize));
        Check(Protocol.Decode(k => Read(bombData,k), "0.34") == null, "decompression bomb bounded");
        Check(Diagnostics.Relevant("Incorrect version") && Diagnostics.Relevant("MOD MISMATCH"), "compatibility errors detected");
        Check(!Diagnostics.Relevant("Server full") && !Diagnostics.Relevant("Wrong password"), "unrelated failures ignored");
        Check(!Diagnostics.Clean("name\u0000\u001b[31m").Contains('\u0000'), "control characters removed");
        Check(Diagnostics.Clean(new string('x', 100000)).Length == Protocol.MaxText, "report fields bounded");
        // Fuzz untrusted server metadata: decoder must return null rather than throw.
        var rng = new Random(481);
        for (int i = 0; i < 200; i++)
        {
            string garbage = Convert.ToBase64String(Enumerable.Range(0, rng.Next(1,65)).Select(_ => (byte)rng.Next(256)).ToArray());
            Check(Protocol.Decode(k => k == Protocol.Header ? "1:1" : garbage, "0.34") == null, "malformed metadata " + i);
        }
        if (args.Length != 3) throw new Exception("Expected game directory, client DLL, server DLL");
        var game = AssemblyDefinition.ReadAssembly(Path.Combine(args[0], "NuclearOption_Data", "Managed", "Assembly-CSharp.dll"));
        var client = AssemblyDefinition.ReadAssembly(args[1]);
        var host = AssemblyDefinition.ReadAssembly(args[2]);
        const string lobbies = "NuclearOption.Networking.Lobbies.";
        Contract(game, lobbies+"SteamLobby", "TryJoinLobby", "System.Void", lobbies+"LobbyInstance", "System.String", "System.Boolean");
        Contract(game, lobbies+"ServerLobbyInstance", "SetRule", "System.Void", "System.String", "System.String");
        Contract(game, "GameManager", "SetDisconnectReason", "System.Void", "DisconnectInfo");
        Contract(game, lobbies+"JoinProgress", "Fail", lobbies+"JoinProgress", "System.String");
        Contract(game, lobbies+"LobbyDetailsModal", "Show", "System.Void", lobbies+"LobbyList", lobbies+"LobbyInstance");
        Contract(game, lobbies+"LobbyDetailsModal", "Hide", "System.Void");
        Contract(game, lobbies+"LobbyList", "Start", "System.Void");
        Contract(game, lobbies+"LobbyList", "OnDestroy", "System.Void");
        Contract(game, lobbies+"LobbyList", "GetListOfLobbies", "System.Void");
        Contract(game, lobbies+"LobbyList", "UpdateLobbyList", "System.Void");
        Contract(game, lobbies+"LobbyList", "InsertLobbyDataEntry", "System.Void",lobbies+"LobbyListItem");
        Contract(game, lobbies+"LobbyListItem", "Show", "System.Boolean",lobbies+"LobbyList",lobbies+"LobbyInstance");
        Contract(game, lobbies+"SteamLobby", "GetLobbiesList", "System.Void",lobbies+"LobbySearchFilter");
        Contract(game,lobbies+"SteamLobby/ServerListRequest","RequestServerLobbies","System.Void",lobbies+"LobbySearchFilter");
        Contract(game,lobbies+"SteamLobby/ServerListRequest","OnServerUpdated","System.Void","Steamworks.gameserveritem_t",lobbies+"LobbySearchFilter");
        Contract(game,lobbies+"SteamLobby/ServerListRequest","OnRefreshComplete","System.Void","Steamworks.HServerListRequest","Steamworks.EMatchMakingServerResponse");
        Contract(game,lobbies+"SteamLobby/ServerListRequest","set_InProgress","System.Void","System.Boolean");
        Contract(game,lobbies+"SteamLobby/ServerListRequest","set_searchNumber","System.Void","System.Int32");
        Contract(game,lobbies+"SteamLobby/ServerListRequest","Cancel","System.Void");
        Contract(game,lobbies+"SteamLobby","get_serverLobbiesRefreshInProgress","System.Boolean");
        Contract(game,lobbies+"SteamLobby","CheckPendingRefresh","System.Boolean");
        Check(game.MainModule.GetType(lobbies+"SteamLobby").Fields.Any(f=>f.Name=="playerLobbyRefreshInProgress" && f.FieldType.FullName=="System.Boolean"), "queued browser searches can wait for both native discovery paths");
        foreach (string field in new[]{"lobbyListContent","entryPrefab","sortList"})
            Check(game.MainModule.GetType(lobbies+"LobbyList").Fields.Any(x=>x.Name==field), "browser control field "+field);
        foreach (string field in new[]{"lobbyNameText","missionNameText"})
            Check(game.MainModule.GetType(lobbies+"LobbyListItem").Fields.Any(x=>x.Name==field), "browser row field "+field);
        Check(game.MainModule.GetType(lobbies+"LobbySearchFilter").Fields.Any(x=>x.Name=="ignoreVersionFilter" && x.FieldType.FullName=="System.Boolean"), "stock version-filter override available");
        var queryBody = game.MainModule.GetType(lobbies+"LobbyList").Methods.Single(m=>m.Name=="GetListOfLobbies").Body.Instructions;
        Check(queryBody.Count(i=>i.Operand is MethodReference m && m.DeclaringType.FullName==lobbies+"SteamLobby" && m.Name=="GetLobbiesList")==1, "browser query injection has exactly one target");
        var steamTypes = AllTypes(game.MainModule.Types).Where(t=>t.FullName.StartsWith(lobbies+"SteamLobby")).ToArray();
        Check(steamTypes.SelectMany(t=>t.Methods).Where(m=>new[]{"BuildServerRequestFilters","RequestPlayerHostedLobbies"}.Contains(m.Name)).Count(m=>m.Body.Instructions.Any(i=>i.Operand is FieldReference f && f.Name=="ignoreVersionFilter"))==2, "both dedicated and player-hosted queries honour the override");
        Contract(game, lobbies+"JoinLobbyOverlay", "Open", "System.Void", lobbies+"JoinProgress");
        Contract(game, "NuclearOption.Networking.NetworkManagerNuclearOption", "StartClient", "System.Void", "NuclearOption.Networking.ConnectOptions");
        Contract(game, "NuclearOption.Networking.NetworkManagerNuclearOption", "ClientDisconnected", "System.Void", "Mirage.ClientStoppedReason");
        foreach (string field in new[]{"holder","lobbyNameText","joinButton","moddedWarning"})
            Check(game.MainModule.GetType(lobbies+"LobbyDetailsModal").Fields.Any(x=>x.Name==field), "native browser field " + field);
        foreach (string field in new[]{"bodyText","closeButton"})
            Check(game.MainModule.GetType(lobbies+"JoinLobbyOverlay").Fields.Any(x=>x.Name==field), "native join template field " + field);
        Contract(game, "NuclearOption.SceneLoading.MapLoader", "CanLoad", "System.Boolean", "NuclearOption.SceneLoading.MapKey");
        Contract(game, lobbies+"HostedLobbyInstance", "SetData", "System.Void", "System.String", "System.String");
        Contract(game, lobbies+"DedicatedServerKeyValues", "ApplyValuesToSteam", "System.Void");
        Contract(game, "NuclearOption.DedicatedServer.DedicatedServerManager", "SteamLogOn", "System.Boolean");
        Contract(game, "NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption", "HandleBuildHashMismatch", "System.Void", "Mirage.INetworkPlayer", "NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption/BuildHashMismatch");
        Contract(game, "NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption", "GetBuildHash", "System.UInt32");
        Check(game.MainModule.GetType(lobbies+"DedicatedServerKeyValues").Fields.Any(x => x.Name == "keyValues" && x.FieldType.FullName == "System.Collections.Generic.Dictionary`2<System.String,System.String>"), "server budget field contract");
        Check(game.MainModule.GetType(lobbies+"DedicatedServerKeyValues").Fields.Any(x => x.Name == "tags" && x.FieldType.FullName == "System.Collections.Generic.Dictionary`2<System.String,System.String>"), "actual advertised version can be read from tags");
        var steam = AssemblyDefinition.ReadAssembly(Path.Combine(args[0],"NuclearOption_Data","Managed","com.rlabrecque.steamworks.net.dll"));
        Contract(steam,"Steamworks.SteamGameServer","ClearAllKeyValues","System.Void");
        Contract(steam,"Steamworks.SteamMatchmaking","GetLobbyOwner","Steamworks.CSteamID","Steamworks.CSteamID");
        Contract(steam,"Steamworks.servernetadr_t","GetIP","System.UInt32");
        Contract(steam,"Steamworks.servernetadr_t","GetConnectionPort","System.UInt16");
        Contract(steam,"Steamworks.SteamMatchmakingServers","RefreshQuery","System.Void","Steamworks.HServerListRequest");
        Contract(steam,"Steamworks.SteamMatchmakingServers","CancelQuery","System.Void","Steamworks.HServerListRequest");
        Contract(steam,"Steamworks.SteamMatchmakingServers","ReleaseRequest","System.Void","Steamworks.HServerListRequest");
        Check(client.MainModule.GetType("KellysJOINCHECK.ServerPlugin") == null, "client excludes server component");
        Check(host.MainModule.GetType("KellysJOINCHECK.ClientPlugin") == null, "server excludes client component");
        Check(!Calls(client, "SetKeyValue") && !Calls(client, "SetLobbyData"), "client has no advertisement writes");
        Check(!Calls(host, "OnGUI") && !Calls(host, "ModalWindow"), "server has no UI");
        var hostTypes = AllTypes(host.MainModule.Types).ToArray();
        Check(!hostTypes.SelectMany(t=>t.Methods).Any(m=>new[]{"Update","FixedUpdate","LateUpdate","OnGUI"}.Contains(m.Name)), "server has no gameplay or frame callbacks");
        var serverPaths = host.MainModule.GetType("KellysJOINCHECK.ServerPlugin").Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!serverPaths.Any(i=>i.Operand is MethodReference m && m.DeclaringType.Name.Contains("DisplayClass")), "server callbacks do not allocate capturing closures");
        Check(!serverPaths.Any(i=>i.Operand is MethodReference m && (m.Name=="get_version" || m.Name=="get_Wire" || (m.Name=="Expanded" && m.Parameters.Count==0))), "server does not repeatedly call the Blueprinter-patched version getter");
        Check(!Calls(host,"StartCoroutine") && !Calls(host,"InvokeRepeating"), "server does not schedule recurring work");
        Check(!hostTypes.SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m && (m.DeclaringType.FullName.StartsWith("System.Net.") || m.DeclaringType.FullName.StartsWith("System.Threading."))), "server starts no threads, timers or extra network requests");
        Check(client.Name.Version.ToString(3)=="1.0.0" && host.Name.Version.ToString(3)=="1.0.0", "both components use the stable 1.0 release version");
        var clientMetadata = client.MainModule.GetType("KellysJOINCHECK.ClientPlugin").CustomAttributes.Single(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin").ConstructorArguments;
        var serverMetadata = host.MainModule.GetType("KellysJOINCHECK.ServerPlugin").CustomAttributes.Single(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin").ConstructorArguments;
        Check((string)clientMetadata[0].Value=="kelly.nuclearoption.joincheck" && (string)serverMetadata[0].Value=="kelly.nuclearoption.joincheck.server", "rename retains distinct legacy plugin IDs and existing config ownership");
        Check((string)clientMetadata[1].Value=="Kelly's DOORMAN" && (string)serverMetadata[1].Value=="Kelly's DOORMAN Server", "both displayed plugin names use the new branding");
        Check((string)clientMetadata[2].Value=="1.0.0" && (string)serverMetadata[2].Value=="1.0.0", "BepInEx loader metadata matches each component's numeric version");
        Check(!Diagnostics.Compare(server,local).Contains('\u2014'), "public diagnostics use plain punctuation");
        Check(!Calls(client, "set_version") && !Calls(host, "set_version"), "compatibility identifier not replaced");
        Check(Calls(client,"GetWorldCorners") && Calls(client,"GetPreferredValues"), "native docking and wrapping present");
        Check(!Calls(client,"set_sprite") && !Calls(client,"set_fillCenter"), "panel and buttons cannot inherit a transparent native sprite centre");
        Check(!Calls(client,"Instantiate"), "no native controllers or persistent listeners cloned");
        Check(!Calls(client,"StartClient") && !Calls(client,"StartHost"), "checklist buttons do not trigger networking");
        var browserHook = client.MainModule.GetType("KellysJOINCHECK.BrowserHooks");
        var rewrite = browserHook.Methods.Single(m=>m.Name=="ApplyQueryOption");
        Check(rewrite.Body.Instructions.Where(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld).All(i=>i.Operand is FieldReference f && f.Name=="ignoreVersionFilter"), "query rewrite changes only version-filter flag");
        Check(!Calls(client,"AddRequestLobbyListStringFilter") && !Calls(client,"AddRequestLobbyListDistanceFilter"), "native query builds all filters");
        Check(!Calls(client,"SetLobbyData") && !Calls(client,"SetKeyValue") && !Calls(client,"AddFavoriteGame"), "browser settings stay local and do not write Steam/server metadata");
        var browserUi = client.MainModule.GetType("KellysJOINCHECK.BrowserUi");
        var checkbox = browserUi.Methods.Single(m=>m.Name=="Check").Body.Instructions;
        Check(checkbox.Count(i=>i.Operand is MethodReference m && m.Name=="SetIsOnWithoutNotify")==1, "checkbox receives the saved state before activation");
        var activations=checkbox.Where(i=>i.Operand is MethodReference m && m.Name=="SetActive").ToArray();
        Check(activations.Length==2 && activations[0].Previous.OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4_0 && activations[1].Previous.OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4_1, "checkbox built inactive and activated after its graphic is assigned");
        var checkboxOrder=checkbox.ToList();
        Check(checkboxOrder.IndexOf(activations[1])>checkboxOrder.FindIndex(i=>i.Operand is MethodReference m && m.Name=="set_graphic"), "Toggle.OnEnable sees the completed check graphic");
        var browserCode=AllTypes(new[]{browserUi}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!browserCode.Any(i=>i.Operand is MethodReference m && m.DeclaringType.Name=="GoogleIconFont"), "checkboxes and stars contain no font-dependent private-use glyphs");
        Check(client.MainModule.GetType("KellysJOINCHECK.DrawnBrowserIcon").BaseType.FullName=="UnityEngine.UI.MaskableGraphic", "drawn browser icons participate in native canvas scaling and masking");
        var session=client.MainModule.GetType("KellysJOINCHECK.BrowserServerList");
        Check(session.Fields.Any(f=>f.Name=="response") && session.Fields.Any(f=>f.Name=="request"), "server request and callback stay alive until browser disposal");
        var start=session.Methods.Single(m=>m.Name=="Start").Body.Instructions;
        Check(start.Count(i=>i.Operand is MethodReference m && m.Name=="RefreshQuery")==1 && start.Count(i=>i.Operand is MethodReference m && m.Name=="RequestInternetServerList")==1, "known server lists refresh in place and empty lists allow a new discovery");
        var release=session.Methods.Single(m=>m.Name=="Release").Body.Instructions;
        Check(release.Any(i=>i.Operand is MethodReference m && m.Name=="CancelQuery") && release.Any(i=>i.Operand is MethodReference m && m.Name=="ReleaseRequest") && release.Any(i=>i.Operand is MethodReference m && m.Name=="KeepAlive"), "closing browser cancels discovery before releasing request and callback");
        Check(!Calls(host,"RequestInternetServerList") && !Calls(host,"RefreshQuery") && host.MainModule.GetType("KellysJOINCHECK.BrowserDiscovery")==null, "dedicated discovery repair is excluded from server companion");
        var scope=browserHook.Fields.Single(f=>f.Name=="queryingBrowser");
        Check(scope.CustomAttributes.Any(a=>a.AttributeType.FullName=="System.ThreadStaticAttribute") && browserHook.Methods.Any(m=>m.Name=="SearchFinalizer"), "browser discovery scope is confined to the calling thread and cleared on exceptions");
        var browserLabels = browserUi.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is MethodReference m && m.Name=="GetComponentInChildren").ToArray();
        Check(browserLabels.Length>0 && browserLabels.All(i=>i.Previous.OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4_1), "browser checkbox labels resolve while native list is inactive");
        Check(!browserUi.Methods.Any(m=>m.Name=="Update" || m.Name=="LateUpdate"), "browser enhancements use events rather than frame polling");
        BrowserLayoutChecks(client,args[0]);
        var ordering = browserUi.Methods.Single(m=>m.Name=="ApplyFavourites");
        Check(!ordering.Body.Instructions.Any(i=>i.Operand is MethodReference m && m.DeclaringType.FullName.StartsWith("System.Collections.Generic.List") && new[]{"Sort","Clear","Insert","Remove","set_Item"}.Contains(m.Name)), "favourites ordering preserves native sort list");
        var lookups = client.MainModule.GetType("KellysJOINCHECK.NativeJoinUi").Methods.Where(m=>m.HasBody)
            .SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is MethodReference method && method.Name=="GetComponentInChildren").ToArray();
        Check(lookups.Length > 0 && lookups.All(i=>((MethodReference)i.Operand).Parameters.Count==1 && i.Previous.OpCode.Code==Mono.Cecil.Cil.Code.Ldc_I4_1), "native labels resolve while checklist root is inactive");
        Check(client.MainModule.GetType("KellysJOINCHECK.ClientPlugin").Methods.Where(m => m.Name.EndsWith("Prefix")).All(m => m.ReturnType.FullName == "System.Void"), "diagnostic prefixes cannot skip original join or authentication checks");
        Console.WriteLine($"PASS: {checks} regression and game assembly contract checks.");
        Console.WriteLine(result);
        return 0;
    }
    private static void IconChecks()
    {
        foreach(var icon in new[]{BrowserIconGeometry.Check,BrowserIconGeometry.StarOutline,BrowserIconGeometry.StarFilled})
        {
            Check(icon.Points.All(p=>float.IsFinite(p.X) && float.IsFinite(p.Y) && p.X>=0 && p.X<=1 && p.Y>=0 && p.Y<=1), "drawn icon vertices stay inside the scalable square");
            Check(icon.Triangles.Length>0 && icon.Triangles.Length%3==0 && icon.Triangles.All(i=>i>=0 && i<icon.Points.Length), "drawn icon triangle indices are valid");
            var areas=Enumerable.Range(0,icon.Triangles.Length/3).Select(i=>
            {
                var a=icon.Points[icon.Triangles[i*3]]; var b=icon.Points[icon.Triangles[i*3+1]]; var c=icon.Points[icon.Triangles[i*3+2]];
                return (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);
            }).ToArray();
            Check(areas.All(area=>area<-.0001f), "drawn triangles have consistent winding and visible area");
        }
        Check(BrowserIconGeometry.Check.Points.Length==8 && BrowserIconGeometry.StarFilled.Points.Length==11 && BrowserIconGeometry.StarOutline.Points.Length==40, "checkmark and filled/outline stars have distinct geometry");
    }
    private static void ServerFilterChecks()
    {
        const string tags="v=0.34.1,m=0,p=0,t=2,";
        Check(BrowserServerFilter.Matches(tags,true,"other",null), "show incompatible includes all advertised versions");
        Check(BrowserServerFilter.Matches("",true,"other",null), "show incompatible with all missions does not require tags");
        Check(BrowserServerFilter.Matches(tags,false,"0.34.1",null), "ordinary dedicated results match native version tag");
        Check(!BrowserServerFilter.Matches(tags,false,"0.34.1_hash",null), "hashed mod version stays filtered unless override is enabled");
        Check(!BrowserServerFilter.Matches("v=0.34.10,t=2",false,"0.34.1",null), "version tag comparison is exact, not a prefix");
        Check(!BrowserServerFilter.Matches("notv=0.34.1,t=2",false,"0.34.1",null), "version text in another key cannot match");
        Check(BrowserServerFilter.Matches(tags,true,"other","2") && !BrowserServerFilter.Matches(tags,true,"other","1"), "PvP/PvE filter survives broad master discovery");
        Check(!BrowserServerFilter.Matches("v=old,t=20",true,"other","2"), "mission tag comparison is exact");
        Check(BrowserServerFilter.Matches("t=2,v=0.34.1",false,"0.34.1","2"), "tag order and final comma do not matter");
        Check(!BrowserServerFilter.Matches("",false,"0.34.1",null) && !BrowserServerFilter.Matches("",true,"0.34.1","2"), "missing required tags remain filtered");
    }
    private static void FavouriteChecks()
    {
        Check(!BrowserPresentation.UseFavouriteFilter(true,0), "stale favourites-only preference cannot hide every server when nothing is saved");
        Check(!BrowserPresentation.UseFavouriteFilter(false,1), "saving a favourite does not silently enable filtering");
        Check(BrowserPresentation.UseFavouriteFilter(true,1), "favourites-only remains available after saving a server");
        Check(!BrowserPresentation.UseFavouriteFilter(false,0), "empty first install shows ordinary results");
        const uint ip = 0xC0000201;
        string dedicated = Favourites.Dedicated(ip,7777), host = Favourites.Hosted(76561198000000001);
        var favourites = new Favourites("");
        Check(favourites.Count==0 && !favourites.Contains(dedicated), "new install has no invented favourites");
        Check(favourites.Toggle(dedicated)==FavouriteChange.Added && favourites.Contains(dedicated), "star adds dedicated server");
        Check(favourites.Toggle(host)==FavouriteChange.Added && favourites.Contains(host), "star adds player host independently");
        string saved = favourites.Save();
        var reloaded = new Favourites(saved);
        Check(reloaded.Count==2 && reloaded.Contains(dedicated) && reloaded.Contains(host), "favourites survive config round trip");
        Check(reloaded.Contains(Favourites.Dedicated(ip,7777)), "dedicated favourite depends on endpoint rather than changing Steam server ID");
        Check(reloaded.Contains(Favourites.Hosted(76561198000000001)), "player favourite depends on owner rather than changing lobby ID");
        Check(!reloaded.Contains(Favourites.Dedicated(ip,7778)), "servers on different ports are distinct");
        Check(!reloaded.Contains(Favourites.Dedicated(ip+1,7777)), "different server addresses are distinct");
        Check(!reloaded.Contains(Favourites.Hosted(76561198000000002)), "different player hosts are distinct");
        Check(reloaded.Toggle(dedicated)==FavouriteChange.Removed && !reloaded.Contains(dedicated) && reloaded.Contains(host), "unstar removes only selected favourite");
        Check(new Favourites(reloaded.Save()).Count==1, "removal survives restart");
        var duplicates = new Favourites(saved+","+saved);
        Check(duplicates.Count==2 && duplicates.Save()==saved, "duplicate entries are normalized without duplicates");
        var reverse = new Favourites(host+","+dedicated);
        Check(reverse.Save()==saved, "saved ordering is deterministic");
        foreach (string invalid in new[]{"","h:0","h:-1","h:18446744073709551616","h:0001","d:00000000:7777","d:C0000201:0","d:C0000201:65536","d:C0000201:-1","d:c0000201:7777","d:C0000201:07777","d:C0000201:7777:extra","password=secret","file:///C:/bad"," h:42","h:42\n"})
            Check(new Favourites(invalid).Count==0 && new Favourites("").Toggle(invalid)==FavouriteChange.Unavailable, "malformed saved favourite rejected: "+invalid.Replace('\n',' '));
        Check(new Favourites(new string('x',Favourites.MaxSavedLength+1)).Count==0, "oversized preference data bounded");
        Check(Favourites.Dedicated(0,7777)=="" && Favourites.Dedicated(ip,0)=="" && Favourites.Hosted(0)=="", "unknown identities cannot be favourited");
        var full = new Favourites(string.Join(",",Enumerable.Range(1,Favourites.MaxCount+20).Select(n=>Favourites.Hosted((ulong)n))));
        Check(full.Count==Favourites.MaxCount && full.Toggle(Favourites.Hosted(9000))==FavouriteChange.LimitReached, "favourite count and runtime growth bounded");
        Check(full.Toggle(Favourites.Hosted(1))==FavouriteChange.Removed && full.Toggle(Favourites.Hosted(9000))==FavouriteChange.Added, "removal remains available at limit");
        Check(new Favourites(full.Save()).Count==Favourites.MaxCount, "maximum-size preference round trip");
        var oldCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
            Check(Favourites.Dedicated(ip,7777)==dedicated && Favourites.Hosted(76561198000000001)==host && new Favourites(saved).Count==2, "favourites work across locale changes");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = oldCulture; }
    }
    private static void BrowserLayoutChecks(AssemblyDefinition client,string gameDir)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"NativeBrowserLayout.json")));
        var native = fixture.RootElement;
        string gameAssembly = Path.Combine(gameDir,"NuclearOption_Data","Managed","Assembly-CSharp.dll");
        using var stream = File.OpenRead(gameAssembly);
        Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))==native.GetProperty("gameAssemblyHash").GetString(), "layout fixture belongs to the installed game assembly");
        var scroll = native.GetProperty("scrollRoot");
        var viewport = native.GetProperty("viewport");
        Check(viewport.GetProperty("m_AnchorMax").GetProperty("x").GetSingle()==0 && viewport.GetProperty("m_SizeDelta").GetProperty("x").GetSingle()==0, "native viewport starts collapsed before automatic layout - reproduces 0.3.0 failure");
        Check(scroll.GetProperty("m_AnchorMin").GetProperty("x").GetSingle()==0 && scroll.GetProperty("m_AnchorMax").GetProperty("x").GetSingle()==1 && scroll.GetProperty("m_AnchorMax").GetProperty("y").GetSingle()==1, "outer native scroll view stretches across the list and starts at its top");
        var ui = client.MainModule.GetType("KellysJOINCHECK.BrowserUi");
        var uiCode = AllTypes(new[]{ui}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(!uiCode.Any(i=>i.Operand is MethodReference m && new[]{"get_viewport","set_verticalScrollbarVisibility","set_horizontalScrollbarVisibility"}.Contains(m.Name)), "browser leaves native viewport and scrollbar sizing untouched");
        Check(ui.Fields.Any(f=>f.Name=="scrollBounds") && !ui.Fields.Any(f=>f.Name=="viewport"), "reserved toolbar space belongs to outer scroll view instead of zero-sized viewport");
        Check(native.GetProperty("rowLayout").EnumerateArray().Any(x=>x.GetString()=="HorizontalLayoutGroup") && native.GetProperty("nameLayout").EnumerateArray().Any(x=>x.GetString()=="VerticalLayoutGroup"), "native row and name positions are both driven by layout groups");
        var row = ui.NestedTypes.Single(t=>t.Name=="Row");
        var rowCode = row.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        Check(rowCode.Any(i=>i.Operand is MethodReference m && m.Name=="set_ignoreLayout") && rowCode.Count(i=>i.Operand is MethodReference m && m.Name=="set_padding")==2, "star stays out of native column layout and reserved padding is restored on unload");
        Check(!rowCode.Any(i=>i.Operand is MethodReference m && m.Name=="set_offsetMin"), "row spacing does not edit text offsets overwritten by the native name layout");
    }
    public static int QueryStub(int filter) => filter;
    public static int RewriteStub(int filter) => filter;
    private static void QueryPatchChecks()
    {
        var query = typeof(Program).GetMethod(nameof(QueryStub))!;
        var rewrite = typeof(Program).GetMethod(nameof(RewriteStub))!;
        var label = new System.Reflection.Emit.DynamicMethod("labels",typeof(void),Type.EmptyTypes).GetILGenerator().DefineLabel();
        var target = new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call,query);
        target.labels.Add(label);
        target.blocks.Add(new HarmonyLib.ExceptionBlock(HarmonyLib.ExceptionBlockType.BeginExceptionBlock));
        target.blocks.Add(new HarmonyLib.ExceptionBlock(HarmonyLib.ExceptionBlockType.EndExceptionBlock));
        var original = new[]{new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldc_I4_1),target,new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ret)};
        var patched = BrowserQueryPatch.Inject(original,query,rewrite).ToArray();
        Check(patched.Length==4 && Equals(patched[1].operand,rewrite) && Equals(patched[2].operand,query), "query rewrite runs immediately before original call");
        Check(patched[1].labels.Contains(label) && patched[2].labels.Count==0, "branches targeting query execute rewrite first");
        Check(patched[1].blocks.Single().blockType==HarmonyLib.ExceptionBlockType.BeginExceptionBlock && patched[2].blocks.Single().blockType==HarmonyLib.ExceptionBlockType.EndExceptionBlock, "query rewrite preserves exception boundaries");
        Check(target.labels.Count==1 && target.blocks.Count==2 && original.Length==3, "transpiler does not mutate input instructions");
        foreach (var code in new[]{new[]{new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ret)},new[]{target,target}})
        {
            try { BrowserQueryPatch.Inject(code,query,rewrite).ToArray(); Check(false,"changed query contract must fail closed"); }
            catch (InvalidOperationException) { Check(true,"missing or duplicate query call rejected"); }
        }
        // Execute the generated pure IL path: the rewrite receives the same filter value and
        // its return value reaches the existing query, with no duplicate call or stack change.
        var simple = BrowserQueryPatch.Inject(new[]{new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ldarg_0),new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Call,query),new HarmonyLib.CodeInstruction(System.Reflection.Emit.OpCodes.Ret)},query,rewrite);
        var method = new System.Reflection.Emit.DynamicMethod("query",typeof(int),new[]{typeof(int)});
        var il = method.GetILGenerator();
        foreach (var instruction in simple)
            if (instruction.operand is System.Reflection.MethodInfo called) il.Emit(instruction.opcode,called); else il.Emit(instruction.opcode);
        var run = (Func<int,int>)method.CreateDelegate(typeof(Func<int,int>));
        Check(run(7319)==7319, "rewritten query IL retains filter argument and result");
    }
    private static void ServerCacheChecks()
    {
        int expansions = 0;
        string expanded = V("--Aircraft-v1.0_--Weapons-v2.0");
        var cache = new AdvertisementCache(wire=>{ expansions++; return expanded; });
        var payload = cache.Get("0.34_a1b2c3d4e5f6")!;
        Check(expansions == 1 && ReferenceEquals(payload,cache.Get("0.34_a1b2c3d4e5f6")), "unchanged version reuses payload and expansion");
        var data = payload.Chunks.ToDictionary(x=>x.Key,x=>x.Value); data[Protocol.Header] = payload.Header;
        Check(Protocol.Decode(k=>Read(data,k),"0.34_a1b2c3d4e5f6") == expanded, "cached payload remains wire-compatible with protocol 1");
        Check(payload.Bytes == data.Sum(p=>Encoding.UTF8.GetByteCount(p.Key)+Encoding.UTF8.GetByteCount(p.Value)), "metadata byte budget includes the header");
        var state = new PublicationState();
        Check(state.Decide(payload,100)==Publication.Publish, "first advertisement is published");
        state.Commit(payload);
        Check(state.Decide(payload,100)==Publication.Keep, "unchanged advertisement produces no Steam writes");
        Check(state.Decide(payload,1301-payload.Bytes)==Publication.Clear, "growing mission metadata withdraws details over budget");
        state.Clear();
        Check(state.Decide(payload,1301-payload.Bytes)==Publication.Keep, "over-budget details are not cleared repeatedly");
        Check(state.Decide(payload,1300-payload.Bytes)==Publication.Publish, "details return when the byte budget permits");
        state.Commit(payload);
        state.Clear();
        Check(state.Decide(payload,100)==Publication.Publish, "new Steam session republishes cached details");
        state.Commit(payload); state.Reset();
        Check(state.Slots == 0 && state.Decide(payload,100)==Publication.Publish, "explicit rule clear resets stale slot bookkeeping");
        var changed = cache.Get("0.35_b1b2c3d4e5f6")!;
        Check(expansions==2 && !ReferenceEquals(payload,changed), "version change rebuilds the bound payload");
        state.Commit(payload);
        Check(state.Decide(changed,100)==Publication.Publish, "changed version cannot reuse a stale published header");
        var large = new Advertisement(Protocol.Encode("large",V(string.Join("_",Enumerable.Range(0,40).Select(i=>"--"+GuidFromNumber(i)+"-v1")))));
        state.Commit(large);
        Check(large.Chunks.Length > payload.Chunks.Length && payload.ReservedBytes(state.Slots)==payload.Bytes+(large.Chunks.Length-payload.Chunks.Length)*5, "shrinking payload reserves empty stale rule keys");
        Check(state.Decide(payload,1300-payload.Bytes)==Publication.Clear, "stale empty rule keys cannot silently exceed the budget");
        Check(PublicationState.UsedBytes(new Dictionary<string,string>{{"ma","雪"}})==5, "mission budget counts UTF8 bytes rather than characters");
        int invalidAttempts=0;
        var invalid = new AdvertisementCache(wire=>{ invalidAttempts++; return new string('x',Protocol.MaxText+1); });
        try { invalid.Get("bad"); Check(false,"invalid signature must fail encoding"); }
        catch (InvalidDataException) { Check(true,"oversized signature rejected"); }
        Check(invalid.Get("bad")==null && invalidAttempts==1, "invalid signature is memoized without repeated compression");
        state.Reset(); state.Commit(changed);
        Check(state.Decide(null,100)==Publication.Clear, "invalid replacement withdraws previously published details");

        // Measure only the pure cached logic under .NET 8; this is not a Unity/Steam benchmark.
        var budget = new Dictionary<string,string>{{"mi","Mission"},{"ma","Heartland"},{"d0",new string('a',100)}};
        for (int i=0;i<10000;i++) state.Decide(cache.Get("0.35_b1b2c3d4e5f6"),PublicationState.UsedBytes(budget));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int writes=0;
        for (int i=0;i<100000;i++)
            if (state.Decide(cache.Get("0.35_b1b2c3d4e5f6"),PublicationState.UsedBytes(budget))!=Publication.Keep) writes++;
        long allocated = GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated==0 && writes==0 && expansions==2, "100000 unchanged cached advertisements allocate zero bytes and require zero writes in pure logic");
        Console.WriteLine($"Cached logic: 100000 repeats, {allocated} allocated bytes, {writes} publish/clear decisions; .NET 8 only.");
    }
    private static string GuidFromNumber(int value) => new Guid(value,0,0,new byte[]{1,2,3,4,5,6,7,8}).ToString("N");
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
    {
        foreach (var type in types) { yield return type; foreach (var nested in AllTypes(type.NestedTypes)) yield return nested; }
    }
    private static bool Calls(AssemblyDefinition a, string name) => a.MainModule.Types.SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference mr && mr.Name == name);
    private static void Contract(AssemblyDefinition a, string type, string method, string returns, params string[] parameters)
    {
        var t = a.MainModule.GetType(type);
        Check(t != null && t.Methods.Any(m=>m.Name == method && m.ReturnType.FullName == returns && m.Parameters.Select(p=>p.ParameterType.FullName).SequenceEqual(parameters)), "game hook " + type + "." + method);
    }
}
