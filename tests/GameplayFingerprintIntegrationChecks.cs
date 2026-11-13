using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class GameplayFingerprintIntegrationRegression
{
    private const string Northstar = "com.ta30plus.northstar";
    private const string Oa = "com.ial.oa27variant";
    private const string Bp = "com.nikkorap.blueprinter";
    private const string Doorman = "kelly.nuclearoption.joincheck";
    private const string Wire = "0.34.1_2b9371678a89";
    private const string NorthstarHash = "64f08e7c1964b4c444fb402f9ce38265bbd2a5bc66ba82bfe37be7bf79e07f02";
    private const string OaHash = "0f9476268ba92d2ef08f0a8530ddf231533fc5062860ddea26412857e842b66a";
    private const string DonorHash = "f6597577e26214ad537b23fba6b26c8242c7e6b51ba289135a96380091f37405";
    private static GameplayFileRecord File(string kind, string id, string version, string hash)
        => new GameplayFileRecord { Kind = kind, Id = id, Version = version, Hash = hash };
    private static GameplayFingerprintSnapshot Snapshot(params GameplayFileRecord[] records)
        => new GameplayFingerprintSnapshot { Ready = true, Complete = true, Records = records,
            LoaderVersion = records.Any(r => r.Kind == "content") ? "2.0.1" : "" };
    private static KeyValuePair<string, string[]> Plugin(string id, params string[] dependencies)
        => new KeyValuePair<string, string[]>(id, dependencies);

    internal static void Run(Action<bool, string> check, AssemblyDefinition client, AssemblyDefinition server)
    {
        SelectionChecks(check);
        BudgetChecks(check);
        PlayerPresentationChecks(check);
        IntegrationChecks(check, client, server);
    }

    private static void PlayerPresentationChecks(Action<bool, string> check)
    {
        const string version="0.34.6";
        var passive=new GameplayFingerprintComparison {Available=true,Verified=true,ServerPendingAssetPatches=1,LocalPendingAssetPatches=2};
        var ready=JoinSummary.Create(version,version,version,version,"");
        string original=ready.Help;
        GameplayFingerprintUi.Apply(ready,passive,true);
        check(ready.Status==JoinStatus.Match&&ready.Help==original&&!ready.PlayerDetails().Contains("unresolved")&&!ready.PlayerDetails().Contains("patch"),
            "non-blocking server and client asset diagnostics do not clutter a matching checklist or its details");
        var optional=JoinSummary.Create(version,version,version,version,"");
        GameplayFingerprintUi.Apply(optional,new GameplayFingerprintComparison {Error="optional check unavailable"},false);
        check(optional.Status==JoinStatus.Match&&!optional.Help.Contains("unavailable")&&optional.PlayerStatus("").Contains("Join server"),
            "a server without optional exact-file checks can still offer a clean join action");
        var blocked=JoinSummary.Create(version,version,version,version,"");
        GameplayFingerprintUi.Apply(blocked,new GameplayFingerprintComparison {Error="sensitive technical failure"},true);
        check(blocked.Status==JoinStatus.Unknown&&blocked.Title.Contains("check")&&blocked.Help.Contains("copy the report",StringComparison.OrdinalIgnoreCase)
            &&!blocked.PlayerDetails().Contains("sensitive technical failure"),
            "a required unavailable check remains a visible blocker with technical cause reserved for Copy report");
        var gameMismatch=JoinSummary.Create("0.35",version,"0.35",version,"");
        GameplayFingerprintUi.Apply(gameMismatch,new GameplayFingerprintComparison {Error="not ready"},true);
        check(gameMismatch.Status==JoinStatus.Changes&&gameMismatch.Actions.Any(a=>a.Action=="UPDATE"&&a.Name=="Nuclear Option")
            &&gameMismatch.PlayerDetails().Contains("UPDATE: Nuclear Option"),
            "a confirmed game update blocker stays visible when an exact-file check is still unavailable");
        var different=new GameplayFingerprintComparison {Available=true,ServerPendingAssetPatches=1,LocalPendingAssetPatches=2};
        different.Differences.Add(new GameplayFingerprintDifference {Type=GameplayFingerprintDifferenceType.Different,Kind="plugin",Id="com.ta30plus.northstar",ServerVersion="1.0",LocalVersion="1.0"});
        var files=JoinSummary.Create(version,version,version,version,"");GameplayFingerprintUi.Apply(files,different,true);
        check(files.Status==JoinStatus.Changes&&files.Actions.Any(a=>a.Action=="DIFFERENT FILE"&&a.Name.Contains("Northstar"))
            &&!files.PlayerDetails().Contains("unresolved")&&files.PlayerStatus("").Contains("Setup mods"),
            "genuine file mismatches remain actionable without showing unrelated loading warnings");
        var aircraft=new GameplayFingerprintComparison {Available=true};
        aircraft.Differences.Add(new GameplayFingerprintDifference {Type=GameplayFingerprintDifferenceType.Missing,Kind="content",Id="aryx_cargoplane1"});
        aircraft.Differences.Add(new GameplayFingerprintDifference {Type=GameplayFingerprintDifferenceType.Missing,Kind="content",Id="pb22trident"});
        var names=JoinSummary.Create(version,version,version,version,"");GameplayFingerprintUi.Apply(names,aircraft,true);
        check(names.Actions.Any(a=>a.Name=="MC-260 Chimera")&&names.Actions.Any(a=>a.Name=="PB-22 Trident"),
            "exact-file checklist gives the cargo aircraft and PB-22 player-friendly names");
        var signature=JoinSummary.Create(version+"_com.nikkorap.blueprinter-v2.1.1_--aryx_cargoplane1-v1.2.1_--pb22trident-v1.2.1",version,
            "host","local","");
        check(signature.Actions.Any(a=>a.Name=="MC-260 Chimera")&&signature.Actions.Any(a=>a.Name=="PB-22 Trident"),
            "ordinary version checklist gives the same readable aircraft names");
        var failed=JoinSummary.Create(version,version,version,version,"Incorrect version");GameplayFingerprintUi.Apply(failed,passive,true);
        check(failed.Status!=JoinStatus.Match&&failed.PlayerDetails().Contains("Copy the report",StringComparison.OrdinalIgnoreCase),
            "a real failed join is not changed into success by matching files or hidden warnings");
        check(files.PlayerStatus("Download failed: install manually.")=="Download failed: install manually.",
            "an explicit setup failure is never replaced by a generic non-blocking status");
    }

    private static void SelectionChecks(Action<bool, string> check)
    {
        var required = Snapshot(File("plugin", Northstar, "1.0.0", NorthstarHash));
        var empty = Snapshot();
        var exact = GameplayFingerprintSetup.Prepare(required, required, empty);
        var codeOnlyGraph = new[] { Plugin(Doorman), Plugin(Bp), Plugin(Northstar, "aircraft.shared"),
            Plugin("aircraft.shared"), Plugin("pure.content.wrapper", Bp), Plugin("client.hud"), Plugin("client.replay") };
        var disabled = ServerPluginSelection.Resolve(codeOnlyGraph,
            new[] { Northstar, "aircraft.shared", "client.replay", Doorman }, new[] { "pure.content.wrapper" },
            Array.Empty<string>(), true, exact.RequiredPluginIds);
        check(exact.CanSetup && exact.NeedsRestart && !disabled.Contains(Northstar) && !disabled.Contains("aircraft.shared"),
            "vanilla-version host with audited code-only aircraft enables exact installed gameplay root and its hard dependency");
        check(disabled.Contains(Bp) && disabled.Contains("pure.content.wrapper") && !disabled.Contains(Doorman),
            "code-only host can exclude unused content loader and pure wrappers while protecting DOORMAN");
        check(!disabled.Contains("client.hud") && disabled.Contains("client.replay"),
            "code-only matching preserves unrelated enabled client mod and existing disabled client preference");
        check(GameplayFingerprintSetup.ValidateResolved(exact.RequiredPluginIds, exact.ExcludedPluginIds, disabled) == "",
            "required code-only roots survive final dependent exclusion closure");

        var bpDependentGraph = new[] { Plugin(Doorman), Plugin(Bp), Plugin(Northstar, Bp), Plugin("pure.content.wrapper", Bp) };
        disabled = ServerPluginSelection.Resolve(bpDependentGraph, new[] { Northstar }, new[] { "pure.content.wrapper" },
            Array.Empty<string>(), true, exact.RequiredPluginIds);
        check(!disabled.Contains(Northstar) && !disabled.Contains(Bp) && disabled.Contains("pure.content.wrapper"),
            "required code-only hard loader dependency is enabled without enabling unrelated pure content wrappers");

        var donor = File("content", "Aryx_PropAttacker1", "1.0.1", DonorHash);
        var variant = File("plugin", Oa, "1.1.18", OaHash);
        var donorHost = Snapshot(donor);
        exact = GameplayFingerprintSetup.Prepare(GameplayFingerprintPolicy.CompactContent(donorHost),
            Snapshot(donor, variant), Snapshot(donor, variant));
        var donorGraph = new[] { Plugin(Doorman), Plugin(Bp), Plugin("original.oa.wrapper", Bp), Plugin(Oa),
            Plugin("variant.cockpit", Oa), Plugin("client.hud"), Plugin("client.audio"), Plugin("client.replay") };
        disabled = ServerPluginSelection.Resolve(donorGraph, exact.ExcludedPluginIds,
            new[] { "original.oa.wrapper" }, new[] { "original.oa.wrapper" }, false, exact.RequiredPluginIds);
        check(disabled.Contains(Oa) && disabled.Contains("variant.cockpit"),
            "extra OA modifier and dependent cockpit are excluded together from original donor host");
        check(!disabled.Contains("original.oa.wrapper") && !disabled.Contains(Bp),
            "same exact server-required OA donor retains its support wrapper and loader independently of variant DLL");
        check(new[] { Doorman, "client.hud", "client.audio", "client.replay" }.All(id => !disabled.Contains(id)),
            "excluding aircraft modifier does not disable unrelated DOORMAN, HUD, audio or replay mods");
        check(GameplayFingerprintSetup.ValidateResolved(exact.RequiredPluginIds, exact.ExcludedPluginIds, disabled) == "",
            "donor preservation and gameplay exclusion resolve to consistent startup set");
        check(GameplayFingerprintSetup.ValidateResolved(exact.RequiredPluginIds, exact.ExcludedPluginIds,
            disabled.Where(id => id != Oa)).Contains(Oa),
            "NOMM restoration cannot silently re-enable a gameplay modifier excluded by the host");

        var contradictionGraph = new[] { Plugin(Doorman), Plugin(Bp), Plugin(Northstar, Oa), Plugin(Oa) };
        disabled = ServerPluginSelection.Resolve(contradictionGraph, new[] { Oa }, Array.Empty<string>(),
            Array.Empty<string>(), true, new[] { Northstar });
        check(GameplayFingerprintSetup.ValidateResolved(new[] { Northstar }, new[] { Oa }, disabled).Contains(Oa),
            "required gameplay plugin depending on an excluded gameplay plugin blocks setup after closure");
        bool missing = false, duplicate = false;
        try { ServerPluginSelection.Resolve(codeOnlyGraph, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true, new[] { "nomm.northstar.alias" }); }
        catch (System.IO.InvalidDataException) { missing = true; }
        try { ServerPluginSelection.Resolve(codeOnlyGraph.Concat(new[] { Plugin(Northstar) }), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), true, new[] { Northstar }); }
        catch (System.IO.InvalidDataException) { duplicate = true; }
        check(missing && duplicate, "required exact gameplay roots use unambiguous real plugin GUIDs rather than NOMM package aliases");

        const string ifritHash = "2d0fa84f2a7c7b10fee1d030b6bbc1adca1dc275f6e5f111ea2e30395dbd18e0";
        var ifritFiles = new[] {
            File("plugin", "com.Spiny.GrowlerFrit", "0.0.2", ifritHash),
            File("plugin", "com.Spiny.MpBlocker", "0.0.1", ifritHash),
            File("plugin", "com.Spiny.JammerPod2", "0.0.1", ifritHash),
            File("plugin", "com.Spiny.ECMUpgrade", "0.0.1", ifritHash),
            File("plugin", "com.Spiny.SparkyDome", "0.0.1", ifritHash),
            File("plugin", "com.Spiny.Debugger", "1.0.0", ifritHash)
        };
        var ifritIds = ifritFiles.Select(file => file.Id).ToArray();
        var ifritGraph = new[] {
            Plugin(Doorman), Plugin(Bp), Plugin("original.oa.wrapper", Bp), Plugin("client.hud"), Plugin("client.audio"), Plugin("client.replay"),
            Plugin("com.Spiny.GrowlerFrit", "com.Spiny.MpBlocker", "com.Spiny.JammerPod2", "com.Spiny.ECMUpgrade", "com.Spiny.SparkyDome"),
            Plugin("com.Spiny.MpBlocker"), Plugin("com.Spiny.JammerPod2"), Plugin("com.Spiny.ECMUpgrade"),
            Plugin("com.Spiny.SparkyDome", "com.Spiny.ECMUpgrade"), Plugin("com.Spiny.Debugger")
        };
        exact = GameplayFingerprintSetup.Prepare(GameplayFingerprintPolicy.CompactContent(donorHost),
            Snapshot(ifritFiles.Concat(new[] { donor }).ToArray()), Snapshot(ifritFiles.Concat(new[] { donor }).ToArray()));
        disabled = ServerPluginSelection.Resolve(ifritGraph, exact.ExcludedPluginIds,
            new[] { "original.oa.wrapper" }, new[] { "original.oa.wrapper" }, false, exact.RequiredPluginIds);
        check(exact.CanSetup && exact.NeedsRestart && ifritIds.All(disabled.Contains),
            "matching donor host excludes all six extra same-DLL Ifrit components together including independent logger");
        check(new[] { Doorman, Bp, "original.oa.wrapper", "client.hud", "client.audio", "client.replay" }.All(id => !disabled.Contains(id))
            && GameplayFingerprintSetup.ValidateResolved(exact.RequiredPluginIds, exact.ExcludedPluginIds, disabled) == "",
            "whole Ifrit exclusion keeps exact donor, loader, DOORMAN and unrelated client tools active");
        exact = GameplayFingerprintSetup.Prepare(Snapshot(ifritFiles), Snapshot(ifritFiles), empty);
        disabled = ServerPluginSelection.Resolve(ifritGraph, ifritIds.Concat(new[] { "client.replay" }),
            new[] { "original.oa.wrapper" }, Array.Empty<string>(), true, exact.RequiredPluginIds);
        check(exact.CanSetup && ifritIds.All(id => !disabled.Contains(id))
            && disabled.Contains("client.replay") && !disabled.Contains("client.hud") && !disabled.Contains(Doorman)
            && GameplayFingerprintSetup.ValidateResolved(exact.RequiredPluginIds, exact.ExcludedPluginIds, disabled) == "",
            "required exact Ifrit assembly enables all actual component roots and hard dependencies while preserving client preferences");
    }

    private static Dictionary<string, string> GameBytes(int bytes)
        => bytes == 0 ? new Dictionary<string, string>() : new Dictionary<string, string> { ["x"] = new string('a', bytes - 1) };
    private static GameplayFingerprintSnapshot? Decode(Advertisement? advertisement)
    {
        if (advertisement == null) return null;
        var data = advertisement.Chunks.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        data.Add(advertisement.HeaderKey, advertisement.Header);
        return GameplayFingerprintProtocol.Decode(key => data.TryGetValue(key, out var value) ? value : "", Wire);
    }
    private static string FixtureHash(string id)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("fixture-file:" + id))).Replace("-", "").ToLowerInvariant();
    }
    private static void BudgetChecks(Action<bool, string> check)
    {
        // Real identifiers and server shape, independent deterministic fixture
        // file hashes. These hashes are not claims about published Aryx bytes.
        string[] ids = { "1509_palafighter1", "aryx_expanded_weapons", "aryx_f16m", "aryx_interceptor1", "aryx_kingraptor",
            "aryx_lightfighter1", "aryx_lighthelicopter1", "aryx_mc260_chimera", "aryx_mig-15", "aryx_navalexpansion", "aryx_propattacker1" };
        var observed = Snapshot(ids.Select(id => File("content", id, "1.1.1", FixtureHash(id))).ToArray());
        var cache = new GameplayAdvertisementCache(_ => observed);
        var full = cache.Get(Wire) ?? throw new InvalidOperationException("Full test advertisement unavailable.");
        var compact = cache.GetCompact(Wire) ?? throw new InvalidOperationException("Compact test advertisement unavailable.");
        var unavailable = cache.BudgetUnavailable(Wire) ?? throw new InvalidOperationException("Unavailable test advertisement could not encode.");
        var legacy = new Advertisement(Protocol.Encode(Wire, GameplayFingerprintPolicy.Expanded(observed, Wire)));
        check(compact.Bytes < full.Bytes,
            "eleven-pack server has smaller complete content-set transport rather than truncated per-file hashes");
        check(ReferenceEquals(cache.Get(Wire), full) && ReferenceEquals(cache.GetCompact(Wire), compact)
            && ReferenceEquals(cache.BudgetUnavailable(Wire), unavailable),
            "unchanged inventory publishes cached frame objects without repeated compression or hashing");
        var decodedCompact = Decode(compact)!;
        check(decodedCompact.Ready && decodedCompact.Complete && decodedCompact.ContentSelection.Length == 11
            && decodedCompact.ContentSetHash == GameplayFingerprintPolicy.ContentHash(observed.Records)
            && GameplayFingerprintComparison.Compare(decodedCompact, observed).Verified,
            "compact eleven-pack frame preserves every content ID/version and full exact-set digest");
        var emptyGame = GameBytes(0);
        var budget = GameplayPublicationBudget.Select(emptyGame, legacy, full, unavailable, 0, 0, compact);
        check(budget.HasMarker && budget.Bytes <= GameplayPublicationBudget.Limit && Decode(budget.Fingerprints)?.Complete == true,
            "eleven-pack dedicated server retains complete exact verification within Steam rule budget");
        check(ReferenceEquals(budget.Fingerprints, compact),
            "eleven-pack full plus legacy overflow falls back to complete compact frame");

        int markerBytes = Encoding.UTF8.GetByteCount(GameplayPublicationBudget.Marker) + Encoding.UTF8.GetByteCount(GameplayFingerprintPolicy.Scope);
        int oldLegacySlots = legacy.Chunks.Length, oldFingerprintSlots = full.Chunks.Length;
        int emptyLegacy = Encoding.UTF8.GetByteCount(Protocol.Header) + oldLegacySlots * 5;
        int roomForCompactOnly = GameplayPublicationBudget.Limit - markerBytes - emptyLegacy - compact.ReservedBytes(oldFingerprintSlots);
        var fullBoundary = GameBytes(roomForCompactOnly);
        budget = GameplayPublicationBudget.Select(fullBoundary, legacy, full, unavailable, oldLegacySlots, oldFingerprintSlots, compact);
        check(budget.HasMarker && budget.Legacy == null && ReferenceEquals(budget.Fingerprints, compact)
            && budget.Bytes == GameplayPublicationBudget.Limit,
            "budget boundary withdraws complete legacy frame to retain whole compact exact set and stale chunk reservations");
        check(Signature.Parse(GameplayFingerprintPolicy.Expanded(Decode(budget.Fingerprints)!, Wire)).Mods.Count == 12,
            "client derives all eleven content versions and loader from complete compact frame when legacy is withdrawn");
        budget = GameplayPublicationBudget.Select(GameBytes(roomForCompactOnly + 1), legacy, full, unavailable,
            oldLegacySlots, oldFingerprintSlots, compact);
        var beyondCapacity = Decode(budget.Fingerprints);
        check(budget.HasMarker && budget.Bytes <= GameplayPublicationBudget.Limit && !ReferenceEquals(budget.Fingerprints, compact)
            && (beyondCapacity == null || !beyondCapacity.Ready && !beyondCapacity.Complete && beyondCapacity.Records.Length == 0),
            "one byte beyond complete frame capacity retains strict marker and withdraws or reports unavailable exact inventory");
        budget = GameplayPublicationBudget.Select(GameBytes(roomForCompactOnly + 1), null, full, unavailable,
            oldLegacySlots, oldFingerprintSlots, compact);
        check(budget.HasMarker && budget.Bytes <= GameplayPublicationBudget.Limit
            && Decode(budget.Fingerprints) is GameplayFingerprintSnapshot unavailableStatus
            && !unavailableStatus.Ready && !unavailableStatus.Complete && unavailableStatus.Records.Length == 0,
            "when status fits after legacy withdrawal, insufficient exact-set capacity publishes explicit unavailable status");
        var ownKeys = new Dictionary<string, string>(emptyGame) { [GameplayPublicationBudget.Marker] = GameplayFingerprintPolicy.Scope,
            [Protocol.Header] = new string('l', 100), [Protocol.Key(0)] = new string('l', 96),
            [GameplayFingerprintProtocol.Header] = new string('f', 100), [GameplayFingerprintProtocol.Key(0)] = new string('f', 96) };
        var ownCopy = new Dictionary<string, string>(ownKeys);
        var cleanBudget = GameplayPublicationBudget.Select(emptyGame, legacy, full, unavailable, 0, 0, compact);
        var replacingBudget = GameplayPublicationBudget.Select(ownKeys, legacy, full, unavailable, 0, 0, compact);
        check(cleanBudget.Bytes == replacingBudget.Bytes && ReferenceEquals(cleanBudget.Fingerprints, replacingBudget.Fingerprints),
            "old owned rules are replaced once without double-counting their existing payload");
        check(ownKeys.Count == ownCopy.Count && ownCopy.All(p => ownKeys[p.Key] == p.Value),
            "budget planning never mutates native game rules or existing advertisement values");
        var utf8Game = new Dictionary<string, string> { ["name"] = "\u03a9" };
        var utf8Budget = GameplayPublicationBudget.Select(utf8Game, legacy, full, unavailable, 0, 0, compact);
        check(utf8Budget.Bytes == cleanBudget.Bytes + 6, "native rule reservation uses UTF8 bytes rather than character count");

        var state = new PublicationState();
        check(state.DecidePrepared(full) == Publication.Publish, "new exact frame is published atomically");
        state.Commit(full);
        check(state.DecidePrepared(full) == Publication.Keep && state.DecidePrepared(null) == Publication.Clear,
            "unchanged exact frame causes no writes and unavailable replacement first withdraws old complete set");
        state.Clear();
        check(state.Slots == oldFingerprintSlots && state.DecidePrepared(null) == Publication.Keep,
            "withdrawal remembers stale chunk slots for rule budget without repeated empty writes");
        state.Commit(compact);
        check(state.Slots == oldFingerprintSlots && compact.ReservedBytes(state.Slots) == compact.Bytes + Math.Max(0, oldFingerprintSlots - compact.Chunks.Length) * 5,
            "full-to-compact replacement reserves every stale empty chunk key");
        state.Reset();
        check(state.Slots == 0 && state.DecidePrepared(compact) == Publication.Publish,
            "fresh Steam session clears cached publication identity and slot accounting");
        var headless = Snapshot(observed.Records); headless.PendingAssetPatches = 4;
        observed = headless;
        var notedFull = cache.Get(Wire)!; var notedCompact = cache.GetCompact(Wire)!;
        check(!ReferenceEquals(notedFull, full) && notedFull.Header != full.Header
            && Decode(notedFull)?.PendingAssetPatches == 4,
            "same-wire publication of headless status creates a distinct committed metadata generation");
        var notedBudget = GameplayPublicationBudget.Select(emptyGame, legacy, notedFull, cache.BudgetUnavailable(Wire), 0, 0, notedCompact);
        var notedDecoded = Decode(notedBudget.Fingerprints);
        check(notedBudget.HasMarker && notedBudget.Bytes <= GameplayPublicationBudget.Limit
            && notedDecoded?.PendingAssetPatches == 4 && notedDecoded.ContentSelection.Length == 11
            && GameplayFingerprintComparison.Compare(notedDecoded, headless).Verified,
            "eleven-pack server budget includes headless notice without losing complete compact exact coverage");
        int notedRoom = GameplayPublicationBudget.Limit - markerBytes - emptyLegacy - notedCompact.ReservedBytes(oldFingerprintSlots);
        var notedFullBoundary = GameplayPublicationBudget.Select(GameBytes(notedRoom), legacy, notedFull, cache.BudgetUnavailable(Wire), oldLegacySlots, oldFingerprintSlots, notedCompact);
        check(notedFullBoundary.HasMarker && notedFullBoundary.Bytes == GameplayPublicationBudget.Limit
            && Decode(notedFullBoundary.Fingerprints)?.PendingAssetPatches == 4,
            "headless status consumes actual bytes and retains full notice at compact metadata boundary");
        var notedOverflow = GameplayPublicationBudget.Select(GameBytes(notedRoom + 1), null, notedFull, cache.BudgetUnavailable(Wire), oldLegacySlots, oldFingerprintSlots, notedCompact);
        var notedOverflowDecoded = Decode(notedOverflow.Fingerprints);
        check(notedOverflow.HasMarker && notedOverflow.Bytes <= GameplayPublicationBudget.Limit
            && (notedOverflowDecoded == null || !notedOverflowDecoded.Complete && notedOverflowDecoded.PendingAssetPatches == 0 && notedOverflowDecoded.Records.Length == 0),
            "insufficient space never strips presentation notice from a deceptively complete frame");
        observed = GameplayFingerprintSnapshot.Unavailable("Startup inventory still preparing.");
        check(cache.GetCompact(Wire) == null && Decode(cache.Get(Wire))?.Ready == false,
            "startup pending inventory cannot reuse a previous complete compact frame");
    }

    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
        => roots.SelectMany(t => new[] { t }.Concat(Types(t.NestedTypes)));
    private static Instruction[] Code(MethodDefinition method)
        => method.HasBody ? method.Body.Instructions.ToArray() : Array.Empty<Instruction>();
    private static bool Calls(MethodDefinition method, string name, string owner = "")
        => Code(method).Any(i => i.Operand is MethodReference m && m.Name == name && (owner.Length == 0 || m.DeclaringType.Name == owner));
    private static int CallIndex(MethodDefinition method, string name)
        => Array.FindIndex(Code(method), i => i.Operand is MethodReference m && m.Name == name);
    private static MethodDefinition Method(AssemblyDefinition assembly, string type, string method)
        => assembly.MainModule.GetType("KellysJOINCHECK." + type).Methods.Single(m => m.Name == method);
    private static MethodDefinition Coroutine(AssemblyDefinition assembly, string type, string method)
        => Types(new[] { assembly.MainModule.GetType("KellysJOINCHECK." + type) })
            .Where(t => t.Name.StartsWith("<" + method + ">", StringComparison.Ordinal)).SelectMany(t => t.Methods).Single(m => m.Name == "MoveNext");
    private static bool HasString(MethodDefinition method, string value)
        => Code(method).Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && (string)i.Operand == value);
    private static bool NoAuthWrites(IEnumerable<MethodDefinition> methods)
        => !methods.SelectMany(Code).Any(i => i.Operand is MethodReference m &&
            (m.Name == "set_version" || m.Name == "SetValue" && m.DeclaringType.FullName == "System.Reflection.FieldInfo"
                || m.Name == "TryJoinLobby" || m.Name == "StartClient" || m.Name == "Authenticate"));

    private static void IntegrationChecks(Action<bool, string> check, AssemblyDefinition client, AssemblyDefinition server)
    {
        var beforeJoin = Method(client, "ModManager", "BeforeJoin");
        var beforeCode = Code(beforeJoin);
        int gate = CallIndex(beforeJoin, "GameplayAllowsJoin");
        int fastPath = Array.FindIndex(beforeCode, i => i.Operand is FieldReference field && field.Name == "retrying");
        check(gate >= 0 && fastPath > gate && CallIndex(beforeJoin, "get_Wire") > gate,
            "exact-file join gate runs before retry, auto-match-off and matching raw-version shortcuts");
        check(beforeCode.Skip(gate + 1).Any(i => i.OpCode.FlowControl == FlowControl.Cond_Branch)
            && beforeCode.Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ret).Any(i => i.Previous?.OpCode.Code == Mono.Cecil.Cil.Code.Ldc_I4_0),
            "unverified exact files return false from normal join workflow");
        var prefix = Method(client, "ClientPlugin", "JoinPrefix");
        var prefixCode = Code(prefix);
        bool closedCatch = prefix.Body.ExceptionHandlers.Any(handler => prefixCode.Where(i => i.Offset >= handler.HandlerStart.Offset
            && i.Offset < (handler.HandlerEnd?.Offset ?? int.MaxValue)).Any(i => i.Operand is MethodReference m && m.Name == "RequiresGameplayFiles"));
        check(closedCatch && Calls(prefix, "GameplayAllowsJoin") && Calls(prefix, "BeforeJoin"),
            "outer join hook independently verifies exact files and fails closed if automatic setup throws on a strict server");

        var metadata = Method(client, "ClientPlugin", "IsMetadataKey");
        check(HasString(metadata, "gfpv") && HasString(metadata, GameplayFingerprintProtocol.Header)
            && Calls(metadata, "Key", "GameplayFingerprintProtocol"),
            "dedicated rule capture accepts strict-scope marker, fingerprint commit header and each fingerprint chunk");
        var shared = Method(client, "GameplayFingerprintInventory", "ForWire");
        check(HasString(shared, "PendingAssetPatches") && Calls(shared, "Normalize", "GameplayFingerprintPolicy"),
            "FULL shared-collector snapshot copies and validates typed loading status across companion assemblies");
        string clientCollector=(string)client.MainModule.GetType("KellysJOINCHECK.GameplayFingerprintInventory").Fields.Single(f=>f.Name=="SharedKey").Constant;
        string serverCollector=(string)server.MainModule.GetType("KellysJOINCHECK.GameplayFingerprintInventory").Fields.Single(f=>f.Name=="SharedKey").Constant;
        check(clientCollector==serverCollector&&clientCollector=="KellysDOORMAN.GameplayFingerprints.audited-v2.v3.loaded-content-v2",
            "matching new companions share one collector and isolate older rc4 collectors lacking standalone loaded-file proof");
        var capture=Method(client,"GameplayFingerprintInventory","Capture");
        check(Calls(capture,"GetValue","ConditionalWeakTable`2")&&Code(capture).Any(i=>i.Operand is FieldReference field&&field.Name=="ContentLoadIdentity"),
            "standalone source load identity follows actual bundle object through weak lookup rather than each explicit rehash request");
        var candidates=Method(client,"ModManager","ReadGameplayCandidates");
        check(Calls(candidates,"PendingFor","GameplayFingerprintCandidates"),
            "every queued gameplay source resolves verified DLL owner and target including companion plugin GUIDs");
        var gameplayReport = Method(client, "ClientPlugin", "GameplayReport");
        check(HasString(gameplayReport, "  Server unresolved asset patches=")
            && Code(gameplayReport).Any(i => i.Operand is FieldReference f && f.Name == "PendingAssetPatches"),
            "Copy report records remote unresolved asset count without claiming failed client verification");
        check(Calls(Method(client,"ClientPlugin","Describe"),"GameplayReport","ClientPlugin")
            &&Calls(Method(client,"ClientPlugin","CopyDiagnosticReport"),"Describe","ClientPlugin")
            &&Calls(Method(client,"ClientPlugin","CopyDiagnosticReport"),"Compose","SupportReport"),
            "the Copy report path still composes fresh full loader and exact-file diagnostics");
        var gameplayUi = Method(client, "GameplayFingerprintUi", "Apply");
        check(!Code(gameplayUi).Any(i=>i.Operand is FieldReference field&&field.Name.Contains("PendingAssetPatches"))
            &&Calls(Method(client,"ClientPlugin","Describe"),"GameplayReport","ClientPlugin"),
            "non-blocking asset diagnostics stay out of checklist presentation while remaining in the copied report");
        check(Calls(Method(client,"NativeJoinUi","DrawRows"),"PlayerDetails","JoinSummary")
            &&Calls(Method(client,"NativeJoinUi","SetServerActions"),"PlayerStatus","JoinSummary")
            &&Calls(Method(client,"ClientPlugin","DrawWindow"),"PlayerDetails","JoinSummary"),
            "native and fallback details use the concise player summary while preserving workflow blockers");
        var ruleBodies = Types(new[] { client.MainModule.GetType("KellysJOINCHECK.ClientPlugin") })
            .SelectMany(t => t.Methods).Where(m => m.Name.Contains("RulePrefix", StringComparison.Ordinal)).ToArray();
        check(ruleBodies.Any(m => Calls(m, "IsMetadataKey") && Calls(m, "set_Item") && Calls(m, "Remove")),
            "late dedicated fingerprint rule chunks are retained and invalidate decoded snapshot cache");
        var directJoin = Method(client, "ClientPlugin", "JoinCurrentServer");
        check(CallIndex(directJoin, "get_Wire") >= 0 && CallIndex(directJoin, "GameplayAllowsJoin") > CallIndex(directJoin, "get_Wire")
            && CallIndex(directJoin, "TryJoinLobby") > CallIndex(directJoin, "GameplayAllowsJoin"),
            "Join server button requires existing raw game signature and strict exact-file gate before native join");
        var actions = Method(client, "ModManager", "ServerActions");
        check(Calls(actions, "get_Wire") && Calls(actions, "GameplayAllowsJoin") && Calls(actions, "MenuSafety"),
            "join button remains unavailable while game signature, exact files or menu safety differ");
        var reload = Coroutine(client, "ModManager", "SetupAfterFrame");
        check(CallIndex(reload, "RefreshGameplayFiles") >= 0 && CallIndex(reload, "GameplayAllowsJoin") > CallIndex(reload, "RefreshGameplayFiles")
            && CallIndex(reload, "TryJoinLobby") > CallIndex(reload, "GameplayAllowsJoin"),
            "live content reload invalidates exact inventory and checks it again before automatic native join");
        var resume = Coroutine(client, "ModManager", "ResumeServerPage");
        check(Calls(resume, "RequestSavedServer") && Calls(resume, "GameplayAllowsJoin") && Calls(resume, "RefreshServerActions"),
            "restart return requests fresh server metadata and rechecks exact files before offering join");
        check(!Calls(resume, "TryJoinLobby"), "restart return displays server page without silently joining from stale receipt");
        var plan = Method(client, "ModManager", "Plan");
        check(Calls(plan, "GameplayPreparation") && Calls(plan, "Resolve", "ServerPluginSelection") && Calls(plan, "ValidateResolved", "GameplayFingerprintSetup"),
            "server setup consumes verified exact roots and validates final dependency/NOMM selection");
        check(CallIndex(plan, "ValidateResolved") > CallIndex(plan, "RequiredEntries"),
            "strict exclusions are validated after NOMM package prerequisite restoration can re-enable plugins");
        check(Calls(candidates, "SelectedContent", "GameplayFingerprintPolicy") && Calls(candidates, "ReadFiles", "GameplayFingerprintInventory"),
            "installed preflight reads every required compact content member through bounded exact hashing");
        var prepare = Coroutine(client, "ModManager", "CheckGameplaySetup");
        check(Calls(prepare, "GameplayHeader") && Calls(prepare, "Prepare", "GameplayFingerprintSetup")
            && prepare.DeclaringType.Methods.Any(m => m.Name.StartsWith("<>m__Finally", StringComparison.Ordinal) && Calls(m, "RefreshServerActions")),
            "background preflight binds result to current server header and restores UI state on completion");
        var restart = Coroutine(client, "ModManager", "PrepareRestart");
        check(CallIndex(restart, "GameplayRestartVerifier") >= 0
            && CallIndex(restart, "Run") > CallIndex(restart, "GameplayRestartVerifier")
            && CallIndex(restart, "GameplayPreparation") >= 0
            && CallIndex(restart, "Save") > CallIndex(restart, "GameplayPreparation"),
            "restart binds exact verifier before background work and rejects changed server requirements before saving ticket");
        var workflowMethods = Types(new[] { client.MainModule.GetType("KellysJOINCHECK.ModManager") }).SelectMany(t => t.Methods).ToArray();
        check(workflowMethods.Any(m => m.Name.Contains("CheckGameplaySetup", StringComparison.Ordinal)
                && Calls(m, "ReadGameplayCandidates") && Calls(m, "Prepare", "GameplayFingerprintSetup"))
            && workflowMethods.Any(m => m.Name.Contains("PrepareRestart", StringComparison.Ordinal)
                && CallIndex(m, "HashInventory") >= 0 && CallIndex(m, "Invoke") > CallIndex(m, "HashInventory")),
            "restart background verification re-reads exact code-only and content candidates after ordinary ticket hashing");

        var newTypes = Types(client.MainModule.Types).Where(t => t.Name.StartsWith("GameplayFingerprint", StringComparison.Ordinal)).ToArray();
        check(NoAuthWrites(newTypes.SelectMany(t => t.Methods)),
            "new exact fingerprint, inventory and setup helpers never rewrite game versions or native authentication");
        var serverType = server.MainModule.GetType("KellysJOINCHECK.ServerPlugin");
        var allServerMethods = Types(server.MainModule.Types).SelectMany(t => t.Methods).ToArray();
        check(!allServerMethods.Any(m => new[] { "Update", "FixedUpdate", "LateUpdate", "OnGUI" }.Contains(m.Name))
            && !allServerMethods.Any(m => Calls(m, "InvokeRepeating")),
            "server component has no per-frame scanner, UI loop or recurring inventory polling timer");
        check(NoAuthWrites(serverType.Methods), "server publication never rewrites game compatibility or adds a replacement join path");
        foreach (string callback in new[] { "AdvertiseHosted", "AdvertiseDedicated" })
        {
            var publication = Method(server, "ServerPlugin", callback);
            check(!Code(publication).Any(i => i.Operand is MethodReference m &&
                (m.DeclaringType.FullName.StartsWith("System.IO.File", StringComparison.Ordinal)
                    || m.DeclaringType.FullName.StartsWith("Mono.Cecil", StringComparison.Ordinal)
                    || m.DeclaringType.FullName.StartsWith("System.Security.Cryptography", StringComparison.Ordinal)))
                && Calls(publication, "Get", "GameplayAdvertisementCache"),
                "cached " + callback + " callback neither rescans nor hashes installed files");
        }
        var dedicated = Method(server, "ServerPlugin", "AdvertiseDedicated");
        check(Calls(dedicated, "GetCompact", "GameplayAdvertisementCache") && Calls(dedicated, "Select", "GameplayPublicationBudget"),
            "dedicated publication evaluates complete compact snapshot alongside full and legacy frames");
        foreach (string publicationName in new[] { "PublishHosted", "PublishDedicated" })
        {
            var publication = Method(server, "ServerPlugin", publicationName);
            var instructions = Code(publication);
            string steamMethod = publicationName == "PublishHosted" ? "SetLobbyData" : "SetKeyValue";
            int firstWrite = CallIndex(publication, steamMethod);
            int lastWrite = Array.FindLastIndex(instructions, i => i.Operand is MethodReference m && m.Name == steamMethod);
            int commit = CallIndex(publication, "Commit");
            check(firstWrite >= 0 && instructions.Take(firstWrite).Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Ldstr && (string)i.Operand == "")
                && lastWrite > firstWrite && commit > lastWrite && Calls(publication, "Clear"),
                publicationName + " withdraws commit header before replacing chunks and commits only the complete final frame");
        }
    }
}
