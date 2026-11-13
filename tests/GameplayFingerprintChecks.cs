using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KellysJOINCHECK;

internal static class GameplayFingerprintRegression
{
    // Exact published Northstar 1.0/1.0a assets: same GUID, plugin version,
    // assembly version and size, but different compiled gameplay behavior.
    private const string NorthstarOld = "d41a259f4a5b54296d4e6a10b9c0e8990c128f6f770fba1dee4b7d1fead7d808";
    private const string NorthstarNew = "64f08e7c1964b4c444fb402f9ce38265bbd2a5bc66ba82bfe37be7bf79e07f02";
    private const string OaDonor = "f6597577e26214ad537b23fba6b26c8242c7e6b51ba289135a96380091f37405";
    private const string OaModifier = "0f9476268ba92d2ef08f0a8530ddf231533fc5062860ddea26412857e842b66a";
    private const string Wire = "0.34.1_2b9371678a89";
    private const string Northstar = "com.ta30plus.northstar";
    private const string GrowlerHash = "2d0fa84f2a7c7b10fee1d030b6bbc1adca1dc275f6e5f111ea2e30395dbd18e0";
    private static GameplayFileRecord[] GrowlerFiles(string hash = GrowlerHash) => new[] {
        File("plugin", "com.Spiny.GrowlerFrit", "0.0.2", hash),
        File("plugin", "com.Spiny.MpBlocker", "0.0.1", hash),
        File("plugin", "com.Spiny.JammerPod2", "0.0.1", hash),
        File("plugin", "com.Spiny.ECMUpgrade", "0.0.1", hash),
        File("plugin", "com.Spiny.SparkyDome", "0.0.1", hash),
        File("plugin", "com.Spiny.Debugger", "1.0.0", hash)
    };
    private static GameplayFileRecord File(string kind, string id, string version, string hash)
        => new GameplayFileRecord { Kind = kind, Id = id, Version = version, Hash = hash };
    private static GameplayFingerprintSnapshot Snapshot(params GameplayFileRecord[] records)
        => new GameplayFingerprintSnapshot { Ready = true, Complete = true, Records = records, LoaderVersion = records.Any(x => x.Kind == "content") ? "2.0.1" : "" };
    private static string Read(Dictionary<string, string> data, string key) => data.TryGetValue(key, out string? value) ? value : "";
    private static GameplayFingerprintSnapshot? Decode(Dictionary<string, string> data, string wire = Wire)
        => GameplayFingerprintProtocol.Decode(k => Read(data, k), wire);
    private static string Field(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private static string Raw(string wire, string scope, string records, int count = 1)
        => "1\n" + Field(wire) + "\n" + scope + "\n1\n1\n\n\n\n0\n" + count + "\n" + records + "\nend";
    private static string Record(string kind, string id, string version, string hash)
        => kind + "\t" + Field(id) + "\t" + Field(version) + "\t" + hash;
    private static string RawV2(string notice, string record, string loader = "2.0.1", string ready = "1", string complete = "1")
        => "2\n" + Field(Wire) + "\n" + GameplayFingerprintPolicy.Scope + "\n" + ready + "\n" + complete
            + "\n\n" + Field(loader) + "\n\n" + notice + "\n0\n1\n" + record + "\nend";
    private static string RawV3(string notice, string record, string loader = "2.0.1", string ready = "1", string complete = "1")
        => "3\n" + RawV2(notice, record, loader, ready, complete).Substring(2);
    private static Dictionary<string, string> Frame(string raw, string version = "1") => Frame(Encoding.UTF8.GetBytes(raw), version);
    private static Dictionary<string, string> Frame(byte[] raw, string version = "1")
    {
        using var stream = new MemoryStream();
        using (var zip = new DeflateStream(stream, CompressionLevel.Optimal, true)) zip.Write(raw, 0, raw.Length);
        byte[] bytes = stream.ToArray();
        string encoded = Convert.ToBase64String(bytes);
        int count = (encoded.Length + GameplayFingerprintProtocol.ChunkSize - 1) / GameplayFingerprintProtocol.ChunkSize;
        var result = new Dictionary<string, string>();
        for (int i = 0; i < count; i++) result.Add("gfp" + i.ToString("D2"), encoded.Substring(i * GameplayFingerprintProtocol.ChunkSize,
            Math.Min(GameplayFingerprintProtocol.ChunkSize, encoded.Length - i * GameplayFingerprintProtocol.ChunkSize)));
        result.Add(GameplayFingerprintProtocol.Header, version + ":" + count + ":" + Hex(bytes));
        return result;
    }
    private static string Hex(byte[] bytes)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
    }
    private static bool RejectEncode(string wire, GameplayFingerprintSnapshot snapshot, string contains = "")
    {
        try { GameplayFingerprintProtocol.Encode(wire, snapshot); return false; }
        catch (InvalidDataException ex) { return contains.Length == 0 || ex.Message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0; }
    }

    public static void Run(Action<bool, string> check)
    {
        var empty = Snapshot();
        var oldNorthstar = Snapshot(File("plugin", Northstar, "1.0.0", NorthstarOld));
        var newNorthstar = Snapshot(File("plugin", Northstar, "1.0.0", NorthstarNew));
        var changed = GameplayFingerprintComparison.Compare(newNorthstar, oldNorthstar);
        check(changed.Available && !changed.Verified && changed.Differences.Count == 1, "same printed Northstar version does not conceal changed gameplay DLL");
        var change = changed.Differences.Single();
        check(change.Type == GameplayFingerprintDifferenceType.Different && change.Kind == "plugin" && change.Id == Northstar
            && change.ServerVersion == "1.0.0" && change.LocalVersion == "1.0.0"
            && change.ServerHash == NorthstarNew && change.LocalHash == NorthstarOld, "same-version difference retains exact audited identity and both complete hashes");
        check(changed.ExtraPluginIds.Length == 0, "different required gameplay DLL is not treated as an extra plugin exclusion");
        check(GameplayFingerprintComparison.Compare(newNorthstar, newNorthstar).Verified, "same exact aircraft modifier file matches");
        check(GameplayFingerprintComparison.Compare(empty, empty).Verified, "complete empty gameplay inventory is a valid verified set");

        // OA-27 ships Aryx's untouched donor bundle, then modifies it in a DLL.
        var donor = File("content", "Aryx_PropAttacker1", "1.0.1", OaDonor);
        var donorOnlyServer = Snapshot(donor);
        var modifiedClient = Snapshot(donor, File("plugin", "com.ial.oa27variant", "1.1.18", OaModifier));
        var extra = GameplayFingerprintComparison.Compare(donorOnlyServer, modifiedClient);
        check(extra.Available && !extra.Verified && extra.Differences.Count == 1, "identical OA-27 donor does not conceal additional modifier DLL");
        check(extra.Differences.Single().Type == GameplayFingerprintDifferenceType.Extra
            && extra.ExtraPluginIds.SequenceEqual(new[] { "com.ial.oa27variant" }), "exclude only OA-27 modifier while preserving server-required donor");
        var missing = GameplayFingerprintComparison.Compare(modifiedClient, donorOnlyServer);
        check(missing.Available && !missing.Verified && missing.Differences.Single().Type == GameplayFingerprintDifferenceType.Missing
            && missing.Differences.Single().Id == "com.ial.oa27variant", "server-required modifier is named separately from matching donor");
        var differentDonor = Snapshot(File("content", donor.Id, donor.Version, NorthstarOld));
        check(GameplayFingerprintComparison.Compare(donorOnlyServer, differentDonor).Differences.Single().Kind == "content", "same-version edited content bundle is detected by full file fingerprint");
        var extraContent = GameplayFingerprintComparison.Compare(empty, donorOnlyServer);
        check(extraContent.Differences.Single().Type == GameplayFingerprintDifferenceType.Extra && extraContent.ExtraPluginIds.Length == 0,
            "extra content is never translated into disabling its supporting or unrelated client plugin");

        string[] unrelated = { "kelly.doorman.client", "com.nikkorap.blueprinter", "com.offiry.qol", "com.example.replay", "com.example.hud", "com.Spiny.UnrelatedHud",
            "kelly.nuclearoption.talon.publicui", "kelly.nuclearoption.medals.publicui", "kelly.nuclearoption.airlift.publicui" };
        check(unrelated.All(id => !GameplayFingerprintPolicy.IsAuditedPlugin(id)), "unrelated HUD/replay/QoL/infrastructure plugins stay outside audited gameplay exclusions");
        check(GameplayFingerprintPolicy.AuditedPluginIds.Count == 13
            && GameplayFingerprintPolicy.AuditedPluginIds.Contains("com.ial.mig15s")
            && GameplayFingerprintPolicy.AuditedPluginIds.Contains("com.iallemege.kh38mt")
            && GameplayFingerprintPolicy.AuditedPluginIds.Contains("com.mursisru.x77warewind")
            && GameplayFingerprintPolicy.AuditedPluginIds.Contains("com.resolute.nuclearoption"), "audited GUID policy uses actual plugin identities and includes independent missile modifier");
        var growler = Snapshot(GrowlerFiles());
        check(GameplayFingerprintPolicy.Scope == "audited-v2" && growler.Records.All(file => GameplayFingerprintPolicy.IsAuditedPlugin(file.Id)),
            "audited-v2 explicitly covers all six actual KR-EW.67 release declarations including inseparable logging helper");
        check(growler.Records.Select(file => file.Hash).Distinct().Single() == GrowlerHash
            && growler.Records.Select(file => file.Version).Distinct().Count() == 3,
            "Ifrit release records keep actual differing component versions while binding each to one whole DLL hash");
        var growlerExtra = GameplayFingerprintComparison.Compare(empty, growler);
        check(growlerExtra.Available && !growlerExtra.Verified && growlerExtra.ExtraPluginIds.OrderBy(id => id, StringComparer.Ordinal)
            .SequenceEqual(GrowlerFiles().Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal)),
            "stock host identifies every Ifrit component for whole assembly exclusion");
        var editedGrowler = GameplayFingerprintComparison.Compare(growler, Snapshot(GrowlerFiles(NorthstarOld)));
        check(editedGrowler.Differences.Count == 6 && editedGrowler.Differences.All(difference => difference.Type == GameplayFingerprintDifferenceType.Different)
            && editedGrowler.ExtraPluginIds.Length == 0,
            "same printed Ifrit component versions do not conceal a changed whole assembly");
        check(GameplayFingerprintComparison.Compare(Decode(GameplayFingerprintProtocol.Encode(Wire, growler)), growler).Verified,
            "all six same-file Ifrit identities survive complete transport without collapsing away companion evidence");
        var oldScope = Snapshot(GrowlerFiles()); oldScope.Scope = "audited-v1";
        check(!GameplayFingerprintComparison.Compare(oldScope, growler).Available && RejectEncode(Wire, oldScope)
            && Decode(Frame(Raw(Wire, "audited-v1", Record("plugin", Northstar, "1.0.0", NorthstarOld)))) == null,
            "previous audited-v1 publication cannot claim current expanded gameplay coverage");
        var supportPolluted = Snapshot(File("plugin", "com.example.hud", "1.0.0", NorthstarOld));
        var unsupported = GameplayFingerprintComparison.Compare(empty, supportPolluted);
        check(!unsupported.Available && !unsupported.Verified && unsupported.Differences.Count == 0 && unsupported.ExtraPluginIds.Length == 0,
            "unsupported plugin does not become an automatic exclusion or a verification claim");
        check(RejectEncode(Wire, supportPolluted), "publisher cannot broaden scope to arbitrary client plugins");
        check(!GameplayFingerprintPolicy.IsAuditedPlugin("COM.TA30PLUS.NORTHSTAR"), "actual BepInPlugin GUID comparison preserves ordinal identity");

        var upperHash = Snapshot(File("plugin", Northstar, "1.0.0", NorthstarNew.ToUpperInvariant()));
        check(GameplayFingerprintComparison.Compare(newNorthstar, upperHash).Verified, "hexadecimal hash casing does not invent a file difference");
        check(upperHash.Records[0].Hash == NorthstarNew.ToUpperInvariant(), "comparison does not mutate caller-owned records");
        var versionDisagreement = Snapshot(File("plugin", Northstar, "9.0.0", NorthstarNew));
        check(GameplayFingerprintComparison.Compare(newNorthstar, versionDisagreement).Differences.Single().Type == GameplayFingerprintDifferenceType.Different,
            "conflicting version claim on same hash is still reported rather than asserted identical");

        var partial = new GameplayFingerprintSnapshot { Ready = true, Complete = false, Error = "One file could not be read.", Records = modifiedClient.Records };
        foreach (var pair in new[] { new[] { partial, empty }, new[] { empty, partial } })
        {
            var comparison = GameplayFingerprintComparison.Compare(pair[0], pair[1]);
            check(!comparison.Available && !comparison.Verified && comparison.Differences.Count == 0 && comparison.ExtraPluginIds.Length == 0,
                "partial remote or local inventory cannot generate comparisons or exclusions");
        }
        var unavailable = GameplayFingerprintSnapshot.Unavailable("Not ready.");
        check(!GameplayFingerprintComparison.Compare(unavailable, empty).Available, "not-ready inventory never claims verified empty set");
        var dirtyStatus = GameplayFingerprintSnapshot.Unavailable(" \nHash failed\t\ud800 " + new string('x', 400));
        check(Decode(GameplayFingerprintProtocol.Encode(Wire, dirtyStatus)) is GameplayFingerprintSnapshot safeStatus
            && safeStatus.Error.Length <= GameplayFingerprintPolicy.MaxError && !safeStatus.Error.Any(char.IsControl),
            "unavailable status safely bounds and cleans exception text without Unicode or control injection");
        var bogusScope = Snapshot(); bogusScope.Scope = "automatic-everything-v2";
        check(!GameplayFingerprintComparison.Compare(bogusScope, empty).Available && RejectEncode(Wire, bogusScope), "unsupported scope fails closed locally and at publication");
        var duplicate = Snapshot(newNorthstar.Records[0], newNorthstar.Records[0]);
        check(!GameplayFingerprintComparison.Compare(duplicate, empty).Available && RejectEncode(Wire, duplicate), "duplicate gameplay identity fails closed instead of choosing an arbitrary file");
        var nullRecords = Snapshot(); nullRecords.Records = null!;
        check(!GameplayFingerprintComparison.Compare(nullRecords, empty).Available && RejectEncode(Wire, nullRecords), "missing inventory cannot masquerade as a complete empty set");
        var contradictory = Snapshot(); contradictory.Error = "Hashing failed.";
        check(!GameplayFingerprintComparison.Compare(contradictory, empty).Available, "ready complete status carrying an error is not verified");

        var encoded = GameplayFingerprintProtocol.Encode(Wire, modifiedClient);
        var decoded = Decode(encoded);
        check(decoded != null && GameplayFingerprintComparison.Compare(decoded, modifiedClient).Verified, "gameplay fingerprint transport roundtrip retains exact named inventory");
        check(encoded.All(x => x.Key.StartsWith("gfp", StringComparison.Ordinal) && x.Key.Length + x.Value.Length <= 127), "fingerprint namespace is independent and respects per-rule byte limit");
        check(encoded.Keys.Last() == GameplayFingerprintProtocol.Header, "fingerprint publication map places commit header after its chunks");
        var reordered = GameplayFingerprintProtocol.Encode(Wire, Snapshot(modifiedClient.Records.Reverse().ToArray()));
        check(encoded.Count == reordered.Count && encoded.All(x => Read(reordered, x.Key) == x.Value), "fingerprint transport is stable across inventory enumeration order");
        check(Decode(encoded, Wire + "_rsl-v0.8.4-Default") == null, "transport remains bound to exact server wire identity including compatibility suffix");
        check(Decode(new Dictionary<string, string>()) == null, "legacy server with no fingerprints is unavailable rather than a verified empty server");
        var legacy = Protocol.Encode(Wire, "0.34.1_com.nikkorap.blueprinter-v2.0.1_NOBUNDLES");
        check(Protocol.Decode(k => Read(legacy, k), Wire) == "0.34.1_com.nikkorap.blueprinter-v2.0.1_NOBUNDLES"
            && legacy.Keys.All(x => x.StartsWith("kjc", StringComparison.Ordinal)), "legacy kjc compatibility protocol remains independent and unchanged");
        var combined = new Dictionary<string, string>(encoded);
        foreach (var item in legacy) combined.Add(item.Key, item.Value);
        check(Decode(combined) != null && Protocol.Decode(k => Read(combined, k), Wire) != null, "both fingerprint and legacy namespaces coexist");

        var partialFrame = GameplayFingerprintProtocol.Encode(Wire, partial);
        var partialDecoded = Decode(partialFrame);
        check(partialDecoded != null && !partialDecoded.Complete && partialDecoded.Records.Length == 0
            && !GameplayFingerprintComparison.Compare(partialDecoded, empty).Available, "incomplete publication provides status but strips partial records");
        var emptyDecoded = Decode(GameplayFingerprintProtocol.Encode(Wire, empty));
        check(emptyDecoded != null && emptyDecoded.Ready && emptyDecoded.Complete && emptyDecoded.Records.Length == 0, "complete empty transport remains valid");
        PendingAssetStatusChecks(check, donorOnlyServer, differentDonor, partial, empty);

        foreach (var bad in new[] { "", new string('a', 12), new string('a', 63), new string('a', 65), new string('a', 63) + "z" })
        {
            var badHash = Snapshot(File("plugin", Northstar, "1.0.0", bad));
            check(RejectEncode(Wire, badHash) && !GameplayFingerprintComparison.Compare(badHash, empty).Available, "only complete 64-hex SHA256 can represent a file fingerprint");
        }
        foreach (var bad in new[] { "", " plugin", "map", "PLUGIN" })
            check(RejectEncode(Wire, Snapshot(File(bad, Northstar, "1.0.0", NorthstarNew))), "unknown gameplay record kind rejected");
        foreach (var bad in new[] { "", "\nwrong", "wrong\t", " wrong", new string('x', GameplayFingerprintPolicy.MaxId + 1), "bad\ud800" })
            check(RejectEncode(Wire, Snapshot(File("content", bad, "1.0.0", NorthstarNew))), "invalid or oversized record identity rejected");
        check(RejectEncode(Wire, Snapshot(File("content", "bundle", "", NorthstarNew))), "missing record version rejected");
        check(RejectEncode("", empty) && RejectEncode(Wire + "\n", empty), "empty or injected server wire rejected");

        var missingChunk = new Dictionary<string, string>(encoded); missingChunk.Remove(GameplayFingerprintProtocol.Key(0));
        check(Decode(missingChunk) == null, "missing fingerprint chunk fails closed");
        var malformed = new Dictionary<string, string>(encoded); malformed[GameplayFingerprintProtocol.Key(0)] = "@@@@";
        check(Decode(malformed) == null, "malformed base64 fails closed");
        var checksum = new Dictionary<string, string>(encoded);
        var headerParts = checksum[GameplayFingerprintProtocol.Header].Split(':');
        checksum[GameplayFingerprintProtocol.Header] = headerParts[0] + ":" + headerParts[1] + ":" + new string('0', 64);
        check(Decode(checksum) == null, "payload checksum rejects tampering and stale chunks");
        var oldEncoded = GameplayFingerprintProtocol.Encode(Wire, oldNorthstar);
        var newEncoded = GameplayFingerprintProtocol.Encode(Wire, newNorthstar);
        string mixedKey = oldEncoded.Keys.First(k => k != GameplayFingerprintProtocol.Header && newEncoded.ContainsKey(k) && oldEncoded[k] != newEncoded[k]);
        var mixed = new Dictionary<string, string>(newEncoded) { [mixedKey] = oldEncoded[mixedKey] };
        check(Decode(mixed) == null, "mixed same-wire generations cannot compare as current exact files");
        int headerReads = 0;
        check(GameplayFingerprintProtocol.Decode(k => k == GameplayFingerprintProtocol.Header && ++headerReads > 1 ? Read(encoded, k) + "x" : Read(encoded, k), Wire) == null,
            "header change during metadata delivery invalidates snapshot");
        var badHeader = new Dictionary<string, string>(encoded) { [GameplayFingerprintProtocol.Header] = "4:1:" + new string('a', 64) };
        check(Decode(badHeader) == null, "unsupported fingerprint transport version rejected");
        badHeader[GameplayFingerprintProtocol.Header] = "1:33:" + new string('a', 64);
        check(Decode(badHeader) == null, "unbounded fingerprint chunk count rejected before reads");
        badHeader[GameplayFingerprintProtocol.Header] = "1:01:" + new string('a', 64);
        check(Decode(badHeader) == null, "ambiguous chunk count frame rejected");
        check(GameplayFingerprintProtocol.Decode(_ => "1:" + new string('1', 10000) + ":" + new string('a', 64), Wire) == null,
            "incoming commit header is bounded before splitting or chunk allocation");
        check(GameplayFingerprintProtocol.Decode(_ => throw new InvalidOperationException("read failed"), Wire) == null, "failed metadata read cannot crash diagnostics");

        string normalRecord = Record("plugin", Northstar, "1.0.0", NorthstarNew);
        check(Decode(Frame(Raw(Wire, "unreviewed-v2", normalRecord))) == null, "unsupported payload scope rejected after decompression");
        check(Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, normalRecord + "\n" + normalRecord, 2))) == null, "duplicate identities rejected from hostile payload");
        check(Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, Record("plugin", "com.example.hud", "1.0", NorthstarOld)))) == null, "unknown client plugin cannot be injected as gameplay exclusion");
        check(Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, Record("plugin", Northstar, "1.0.0", new string('a', 12))))) == null, "truncated fingerprints rejected from payload");
        check(Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, normalRecord).Replace("\nend", ""))) == null, "truncated payload has no valid terminal frame");
        check(Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, normalRecord).Replace("1\n" + Field(Wire), "2\n" + Field(Wire)))) == null, "payload and commit-header schema disagreement rejected");
        check(Decode(Frame(new byte[] { 0xc3, 0x28 })) == null, "invalid UTF8 rejected even with valid compressed checksum");
        string nonCanonicalField = "plugin\t" + Field(Northstar).Insert(2, " ") + "\t" + Field("1.0.0") + "\t" + NorthstarNew;
        check(Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, nonCanonicalField))) == null,
            "noncanonical encoded identity reports unavailable instead of throwing an InvalidDataException");
        string oversizedField = "content\t" + Field(new string('x', GameplayFingerprintPolicy.MaxId + 1)) + "\t" + Field("1.0.0") + "\t" + NorthstarNew;
        check(Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, oversizedField))) == null,
            "oversized decoded identity reports unavailable without escaping field-bound exception");
        check(Decode(Frame(new string('A', GameplayFingerprintProtocol.MaxBytes + 1))) == null, "compressed expansion cannot exceed bounded payload memory");
        var injectedPartial = Raw(Wire, GameplayFingerprintPolicy.Scope, normalRecord).Replace("\n1\n1\n", "\n1\n0\n");
        check(Decode(Frame(injectedPartial)) == null, "partial wire inventory cannot carry records for exclusion");

        var overflowRecords = Enumerable.Range(0, GameplayFingerprintPolicy.MaxRecords).Select(i => File("content", "bundle-" + i,
            "1.0.0", Hex(Encoding.UTF8.GetBytes("independent-content-fingerprint-" + i)))).ToArray();
        check(RejectEncode(Wire, Snapshot(overflowRecords), "budget"), "transport overflow reports unavailable rather than publishing truncated inventory");
        var tooMany = Snapshot(overflowRecords.Concat(new[] { File("content", "one-more", "1.0.0", NorthstarNew) }).ToArray());
        check(RejectEncode(Wire, tooMany) && !GameplayFingerprintComparison.Compare(tooMany, empty).Available, "bounded record count cannot be bypassed by local comparison");
        var safeOverflow = GameplayFingerprintSnapshot.Unavailable("Fingerprint inventory exceeds the transport budget.");
        check(Decode(GameplayFingerprintProtocol.Encode(Wire, safeOverflow)) is GameplayFingerprintSnapshot unavailableDecoded
            && !GameplayFingerprintComparison.Compare(unavailableDecoded, empty).Available, "publisher can explicitly advertise unavailable status after overflow");

        var sameIdDifferentKind = Snapshot(File("plugin", Northstar, "1.0.0", NorthstarNew), File("content", Northstar, "1.0.0", OaDonor));
        check(GameplayFingerprintComparison.Compare(sameIdDifferentKind, sameIdDifferentKind).Verified, "content and plugin identity namespaces remain distinct");
        var compact = GameplayFingerprintPolicy.CompactContent(modifiedClient);
        check(compact.Records.Length == 1 && compact.Records[0].Kind == "plugin" && compact.ContentSelection.Length == 1
            && compact.ContentSelection[0].Id == donor.Id && compact.ContentSetHash.Length == 64 && compact.LoaderVersion == "2.0.1",
            "compact frame keeps every selected content identity/version and a full content-set digest");
        check(modifiedClient.Records.Length == 2 && modifiedClient.ContentSetHash == "" && modifiedClient.ContentSelection.Length == 0,
            "compaction never mutates caller-owned full inventory");
        check(GameplayFingerprintComparison.Compare(compact, modifiedClient).Verified
            && GameplayFingerprintComparison.Compare(modifiedClient, compact).Verified,
            "full and compact exact inventories verify symmetrically");
        var compactDecoded = Decode(GameplayFingerprintProtocol.Encode(Wire, compact));
        check(compactDecoded != null && compactDecoded.ContentSetHash == compact.ContentSetHash
            && compactDecoded.LoaderVersion == "2.0.1" && GameplayFingerprintComparison.Compare(compactDecoded, modifiedClient).Verified,
            "compact transport roundtrip preserves selected membership and exact content-set hash");
        var compactEmpty = GameplayFingerprintPolicy.CompactContent(empty);
        check(compactEmpty.ContentSetHash == Hex(Encoding.UTF8.GetBytes("content-set-v1\n"))
            && compactEmpty.ContentSelection.Length == 0 && GameplayFingerprintComparison.Compare(compactEmpty, empty).Verified,
            "compact empty content set has a real complete canonical digest");
        check(GameplayFingerprintPolicy.ContentHash(modifiedClient.Records) == GameplayFingerprintPolicy.ContentHash(modifiedClient.Records.Reverse()),
            "content-set digest is deterministic across record enumeration order");
        var compactDonor = GameplayFingerprintPolicy.CompactContent(donorOnlyServer);
        var compactChanged = GameplayFingerprintComparison.Compare(compactDonor, differentDonor);
        check(compactChanged.Differences.Count == 1 && compactChanged.Differences[0].Kind == "content"
            && compactChanged.Differences[0].Type == GameplayFingerprintDifferenceType.Different
            && compactChanged.Differences[0].Id == "Selected content files" && compactChanged.Differences[0].ServerHash.Length == 64,
            "same named content set with edited bytes reports aggregate difference without inventing which member changed");
        check(GameplayFingerprintComparison.Compare(compactDonor, modifiedClient).ExtraPluginIds.SequenceEqual(new[] { "com.ial.oa27variant" }),
            "compact donor fingerprint still excludes separately audited modifier while preserving donor");
        check(GameplayFingerprintComparison.Compare(compact, donorOnlyServer).Differences.Single().Type == GameplayFingerprintDifferenceType.Missing,
            "compact content transport retains named missing gameplay plugin comparison");
        var twoContents = Snapshot(donor, File("content", "Second aircraft", "2.0", NorthstarNew));
        var compactTwo = GameplayFingerprintPolicy.CompactContent(twoContents);
        var missingContent = GameplayFingerprintComparison.Compare(compactTwo, donorOnlyServer);
        check(missingContent.Differences.Count == 1 && missingContent.Differences[0].Id == "Second aircraft"
            && missingContent.Differences[0].Type == GameplayFingerprintDifferenceType.Missing,
            "compact selection can name a missing content member without claiming its standalone hash");
        var extraSelected = GameplayFingerprintComparison.Compare(compactDonor, twoContents);
        check(extraSelected.Differences.Count == 1 && extraSelected.Differences[0].Id == "Second aircraft"
            && extraSelected.Differences[0].Type == GameplayFingerprintDifferenceType.Extra && extraSelected.ExtraPluginIds.Length == 0,
            "compact content extras remain content rather than plugin exclusions");
        var wrongNamedVersion = Snapshot(File("content", donor.Id, "9.0", OaDonor));
        var compactVersionDifference = GameplayFingerprintComparison.Compare(compactDonor, wrongNamedVersion);
        check(compactVersionDifference.Differences.Count == 1 && compactVersionDifference.Differences[0].Id == donor.Id
            && compactVersionDifference.Differences[0].ServerVersion == "1.0.1" && compactVersionDifference.Differences[0].LocalVersion == "9.0",
            "compact named content versions remain individually actionable");
        var mixedShape = GameplayFingerprintPolicy.CompactContent(donorOnlyServer); mixedShape.Records = donorOnlyServer.Records;
        check(RejectEncode(Wire, mixedShape) && !GameplayFingerprintComparison.Compare(mixedShape, donorOnlyServer).Available,
            "mixed full and compact content shapes fail closed");
        var duplicateSelected = GameplayFingerprintPolicy.CompactContent(donorOnlyServer);
        duplicateSelected.ContentSelection = new[] { duplicateSelected.ContentSelection[0], duplicateSelected.ContentSelection[0] };
        check(RejectEncode(Wire, duplicateSelected) && !GameplayFingerprintComparison.Compare(duplicateSelected, donorOnlyServer).Available,
            "duplicate compact member identity is rejected");
        var noSetHash = GameplayFingerprintPolicy.CompactContent(donorOnlyServer); noSetHash.ContentSetHash = "";
        check(RejectEncode(Wire, noSetHash), "compact membership without complete aggregate hash is unavailable");
        var shortSetHash = GameplayFingerprintPolicy.CompactContent(donorOnlyServer); shortSetHash.ContentSetHash = new string('a', 12);
        check(RejectEncode(Wire, shortSetHash), "compact content digest retains all 256 hash bits");
        var badLoader = Snapshot(); badLoader.LoaderVersion = "not-a-version";
        check(RejectEncode(Wire, badLoader), "compact metadata cannot supply an invalid content loader version");
        string expandedCompact = GameplayFingerprintPolicy.Expanded(compact, Wire);
        check(expandedCompact == GameplayFingerprintPolicy.Expanded(modifiedClient, Wire)
            && Signature.Parse(expandedCompact).Mods.TryGetValue(donor.Id, out string? donorVersion) && donorVersion == "1.0.1",
            "actual compact selection reconstructs named diagnostic expansion when legacy frame is withdrawn");
        check(GameplayFingerprintPolicy.Expanded(compact, Wire + "_rsl-v0.8.4-Default") == expandedCompact
            && GameplayFingerprintPolicy.Expanded(unavailable, Wire) == "",
            "diagnostic expansion preserves raw compatibility suffix separately and never expands unavailable state");
        check(GameplayFingerprintPolicy.Expanded(empty, Wire) == "0.34.1", "actual no-loader empty snapshot expands as vanilla game for diagnostics only");
        var loaderEmpty = Snapshot(); loaderEmpty.LoaderVersion = "2.0.1";
        check(GameplayFingerprintPolicy.Expanded(GameplayFingerprintPolicy.CompactContent(loaderEmpty), Wire)
            == "0.34.1_com.nikkorap.blueprinter-v2.0.1_NOBUNDLES", "actual loader with no selected content still contributes named loader requirement");
        var compactPartial = GameplayFingerprintPolicy.CompactContent(donorOnlyServer); compactPartial.Complete = false;
        var compactPartialDecoded = Decode(GameplayFingerprintProtocol.Encode(Wire, compactPartial));
        check(compactPartialDecoded != null && !compactPartialDecoded.Complete && compactPartialDecoded.ContentSelection.Length == 0
            && compactPartialDecoded.ContentSetHash == "" && !GameplayFingerprintComparison.Compare(compactPartialDecoded, donorOnlyServer).Available,
            "partial compact publication strips selected members and digest instead of enabling partial equality");
        var unicodeContent = Snapshot(File("content", "Aircraft with spaces \u03a9", "1.2-preview", OaDonor));
        check(Decode(GameplayFingerprintProtocol.Encode(Wire, unicodeContent)) is GameplayFingerprintSnapshot unicodeDecoded
            && GameplayFingerprintComparison.Compare(unicodeContent, unicodeDecoded).Verified, "bounded Unicode and spaced Blueprinter names survive strict UTF8 transport");
    }

    private static void PendingAssetStatusChecks(Action<bool,string> check, GameplayFingerprintSnapshot content,
        GameplayFingerprintSnapshot changed, GameplayFingerprintSnapshot partial, GameplayFingerprintSnapshot empty)
    {
        var server = Snapshot(content.Records); server.PendingAssetPatches = 4;
        check(GameplayFingerprintPolicy.Normalize(server, out var normalized, out _) && normalized.PendingAssetPatches == 4
            && normalized.Records.Length == content.Records.Length && normalized.Records[0].Hash == content.Records[0].Hash,
            "pending asset status preserves full immutable file coverage during normalization");
        check(server.PendingAssetPatches == 4 && normalized != server,
            "normalization copies loading health without modifying caller snapshot");
        var encoded = GameplayFingerprintProtocol.Encode(Wire, server);
        var decoded = Decode(encoded);
        check(GameplayFingerprintProtocol.Version == 3 && encoded[GameplayFingerprintProtocol.Header].StartsWith("3:", StringComparison.Ordinal)
            && decoded?.PendingAssetPatches == 4 && decoded.Ready && decoded.Complete,
            "protocol v3 transports explicit bounded pending asset count with complete file fingerprints");
        var compared = GameplayFingerprintComparison.Compare(decoded, content);
        check(compared.Verified && compared.Available && compared.ServerPendingAssetPatches == 4 && compared.LocalPendingAssetPatches == 0,
            "matching hashes retain server loading warning independently of exact file verification");
        var localPending = Snapshot(content.Records); localPending.PendingAssetPatches = 7;
        var localCompared = GameplayFingerprintComparison.Compare(content, localPending);
        check(localCompared.Verified && localCompared.Available && localCompared.ServerPendingAssetPatches == 0 && localCompared.LocalPendingAssetPatches == 7,
            "matching files remain verified when only local assets are pending");
        var bothCompared = GameplayFingerprintComparison.Compare(server, localPending);
        check(bothCompared.Verified && bothCompared.ServerPendingAssetPatches == 4 && bothCompared.LocalPendingAssetPatches == 7,
            "different server and local pending counts do not invent a file mismatch");
        var different = GameplayFingerprintComparison.Compare(decoded, changed);
        check(different.Available && !different.Verified && different.Differences.Count == 1 && different.ServerPendingAssetPatches == 4,
            "pending asset warning never excuses different selected content bytes");
        var changedPending = Snapshot(changed.Records); changedPending.PendingAssetPatches = 2;
        check(!GameplayFingerprintComparison.Compare(server, changedPending).Verified,
            "pending assets on both sides cannot bypass mismatched hashes");
        var incomplete = GameplayFingerprintComparison.Compare(decoded, partial);
        check(!incomplete.Available && !incomplete.Verified && incomplete.Differences.Count == 0 && incomplete.ServerPendingAssetPatches == 4,
            "loading warnings cannot make incomplete client file discovery available or verified");
        var compact = GameplayFingerprintPolicy.CompactContent(server);
        var compactDecoded = Decode(GameplayFingerprintProtocol.Encode(Wire, compact));
        check(compact.PendingAssetPatches == 4 && compact.ContentSetHash == GameplayFingerprintPolicy.ContentHash(content.Records)
            && compactDecoded?.PendingAssetPatches == 4 && GameplayFingerprintComparison.Compare(compactDecoded, content).Verified,
            "compaction and transport keep loading health plus complete selected-content aggregate hash");
        var bound = Snapshot(content.Records); bound.PendingAssetPatches = GameplayFingerprintPolicy.MaxPendingAssetPatches;
        check(Decode(GameplayFingerprintProtocol.Encode(Wire, bound))?.PendingAssetPatches == 8192,
            "maximum generic pending asset count transports canonically");
        foreach (int count in new[] { -1, 8193, int.MinValue, int.MaxValue })
        {
            var bad = Snapshot(content.Records); bad.PendingAssetPatches = count;
            check(RejectEncode(Wire, bad) && !GameplayFingerprintComparison.Compare(bad, content).Available,
                "negative or oversized pending count cannot qualify file snapshot locally or over transport");
        }
        foreach (string loader in new[] { "2.0.0", "2.0.2", "3.0.0" })
        {
            var generic = Snapshot(content.Records); generic.PendingAssetPatches = 1; generic.LoaderVersion = loader;
            check(Decode(GameplayFingerprintProtocol.Encode(Wire, generic))?.PendingAssetPatches == 1
                && GameplayFingerprintComparison.Compare(generic, content).Verified,
                "generic pending warning has no game or loader version allowance pin");
        }
        foreach (string loader in new[] { "", "not-a-version" })
        {
            var bad = Snapshot(content.Records); bad.PendingAssetPatches = 1; bad.LoaderVersion = loader;
            check(RejectEncode(Wire, bad) && !GameplayFingerprintComparison.Compare(bad, content).Available,
                "positive pending count requires valid nonempty loader identity");
        }
        foreach (var flags in new[] { new[] { false, false }, new[] { false, true }, new[] { true, false } })
        {
            var pending = Snapshot(content.Records); pending.Ready = flags[0]; pending.Complete = flags[1]; pending.PendingAssetPatches = 1;
            var pendingDecoded = Decode(GameplayFingerprintProtocol.Encode(Wire, pending));
            check(pendingDecoded != null && pendingDecoded.PendingAssetPatches == 1 && pendingDecoded.Records.Length == 0
                && !GameplayFingerprintComparison.Compare(pendingDecoded, content).Verified,
                "loading health can be reported without exposing or verifying incomplete file inventory");
        }
        var errored = Snapshot(content.Records); errored.PendingAssetPatches = 1; errored.Error = "Source discovery failed.";
        check(RejectEncode(Wire, errored), "pending notice cannot hide a contradictory complete file status carrying a fatal error");
        string record = Record("content", content.Records[0].Id, content.Records[0].Version, content.Records[0].Hash);
        check(Decode(Frame(RawV3("8192", record), "3"))?.PendingAssetPatches == 8192,
            "independently framed v3 payload accepts bounded general pending count");
        foreach (string value in new[] { "-1", "8193", "01", "+1", "1.0", "", " 1", "x", "1\n0" })
            check(Decode(Frame(RawV3(value, record), "3")) == null,
                "malformed, injected or noncanonical incoming v3 count fails closed");
        check(Decode(Frame(RawV3("1", record, loader: ""), "3")) == null
            && Decode(Frame(RawV3("1", record, complete: "0"), "3")) == null,
            "v3 payload cannot claim pending work without loader or publish partial records");
        check(Decode(Frame(RawV2("4", record), "2"))?.PendingAssetPatches == 4,
            "previous v2 headless deferral becomes a loading health warning");
        foreach (string value in new[] { "-1", "33", "01", "+1", "1.0", "", " 1", "x", "1\n0" })
            check(Decode(Frame(RawV2(value, record), "2")) == null,
                "legacy v2 count retains its canonical frame bounds");
        check(Decode(Frame(RawV2("1", record, loader: "2.0.2"), "2")) == null
            && Decode(Frame(RawV2("1", record, complete: "0"), "2")) == null,
            "legacy v2 evidence retains its original loader and ready status validation");
        string[] payloads = { Raw(Wire, GameplayFingerprintPolicy.Scope, record), RawV2("4", record), RawV3("4", record) };
        for (int payload = 0; payload < payloads.Length; payload++)
            for (int header = 0; header < payloads.Length; header++)
                if (payload != header) check(Decode(Frame(payloads[payload], (header + 1).ToString())) == null,
                    "v1 v2 v3 mixed payload and header generations fail closed");
        var legacy = Decode(Frame(Raw(Wire, GameplayFingerprintPolicy.Scope, record)));
        check(legacy != null && legacy.PendingAssetPatches == 0 && GameplayFingerprintComparison.Compare(legacy, content).Verified,
            "previous strict protocol v1 host remains readable without inventing pending assets");
        var unavailable = GameplayFingerprintSnapshot.Unavailable("Unknown source file.");
        var unavailableDecoded = Decode(GameplayFingerprintProtocol.Encode(Wire, unavailable));
        check(unavailableDecoded != null && unavailableDecoded.PendingAssetPatches == 0 && unavailableDecoded.Records.Length == 0
            && !GameplayFingerprintComparison.Compare(unavailableDecoded, empty).Verified,
            "unavailable sources publish no misleading file equality or partial records");
    }
}
