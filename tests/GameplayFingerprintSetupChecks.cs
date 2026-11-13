using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KellysJOINCHECK;

internal static class GameplayFingerprintSetupChecks
{
    private const string Oa = "com.ial.oa27variant";
    private const string Northstar = "com.ta30plus.northstar";
    private const string Mig = "com.ial.mig15s";
    private const string OaDonor = "f6597577e26214ad537b23fba6b26c8242c7e6b51ba289135a96380091f37405";
    private const string OaModifier = "0f9476268ba92d2ef08f0a8530ddf231533fc5062860ddea26412857e842b66a";
    private const string NorthstarOld = "d41a259f4a5b54296d4e6a10b9c0e8990c128f6f770fba1dee4b7d1fead7d808";
    private const string NorthstarNew = "64f08e7c1964b4c444fb402f9ce38265bbd2a5bc66ba82bfe37be7bf79e07f02";
    private static GameplayFileRecord File(string kind, string id, string version, string hash)
        => new GameplayFileRecord { Kind = kind, Id = id, Version = version, Hash = hash };
    private static GameplayFingerprintSnapshot Snapshot(params GameplayFileRecord[] records)
        => new GameplayFingerprintSnapshot { Ready = true, Complete = true, Records = records };

    internal static void Run(Action<bool, string> check)
    {
        var empty = Snapshot();
        var donor = File("content", "Aryx_PropAttacker1", "1.0.1", OaDonor);
        var modifier = File("plugin", Oa, "1.1.18", OaModifier);
        var donorOnly = Snapshot(donor);
        var oaVariant = Snapshot(donor, modifier);
        var result = GameplayFingerprintSetup.Prepare(donorOnly, oaVariant, oaVariant);
        check(result.CanSetup && result.ExcludedPluginIds.SequenceEqual(new[] { Oa }) && result.RequiredPluginIds.Length == 0,
            "server keeps identical original OA donor and excludes independent aircraft variant modifier");
        check(result.NeedsRestart, "excluding a loaded OA gameplay modifier requires restart");
        check(GameplayFingerprintSetup.Prepare(donorOnly, oaVariant, donorOnly).CanSetup
            && !GameplayFingerprintSetup.Prepare(donorOnly, oaVariant, donorOnly).NeedsRestart,
            "already-disabled extra modifier does not force a repeated restart");

        var newNorthstar = Snapshot(File("plugin", Northstar, "1.0.0", NorthstarNew));
        var oldNorthstar = Snapshot(File("plugin", Northstar, "1.0.0", NorthstarOld));
        const string matchingWire="0.34.6_19b548e59773";
        var remoteReady=GameplayFingerprintPolicy.CompactContent(oaVariant);
        check(GameplayFingerprintSetup.AlreadyReady(matchingWire,matchingWire,remoteReady,oaVariant,false,true),
            "already loaded exact server content needs no selection plan, even when unrelated client utilities are duplicated");
        check(!GameplayFingerprintSetup.AlreadyReady(matchingWire,matchingWire,remoteReady,donorOnly,false,true),
            "a required gameplay modifier missing from the loaded set must not skip setup");
        check(!GameplayFingerprintSetup.AlreadyReady(matchingWire,matchingWire,newNorthstar,oldNorthstar,false,true),
            "same-version gameplay DLLs with different bytes must not skip setup");
        check(!GameplayFingerprintSetup.AlreadyReady(matchingWire,"0.34.6_different",remoteReady,oaVariant,false,true)
            &&!GameplayFingerprintSetup.AlreadyReady("","",remoteReady,oaVariant,false,true),
            "different or missing game compatibility strings must not skip setup");
        check(!GameplayFingerprintSetup.AlreadyReady(matchingWire,matchingWire,remoteReady,oaVariant,true,true),
            "queued changes still go through normal setup and restart planning");
        check(!GameplayFingerprintSetup.AlreadyReady(matchingWire,matchingWire,remoteReady,oaVariant,false,false),
            "an unfinished loader must not skip setup");
        check(!GameplayFingerprintSetup.AlreadyReady(matchingWire,matchingWire,null,oaVariant,false,true)
            &&!GameplayFingerprintSetup.AlreadyReady(matchingWire,matchingWire,remoteReady,GameplayFingerprintSnapshot.Unavailable("loading"),false,true),
            "missing host or unfinished local fingerprints must not skip setup");
        result = GameplayFingerprintSetup.Prepare(newNorthstar, oldNorthstar, oldNorthstar);
        check(!result.CanSetup && result.Error.Contains("exact file") && result.Error.Contains(Northstar),
            "same published Northstar version with different compiled bytes blocks setup");
        check(result.RequiredPluginIds.Length == 0 && result.ExcludedPluginIds.Length == 0 && !result.NeedsRestart,
            "failed exact-byte preflight never returns a partial plugin mutation");
        result = GameplayFingerprintSetup.Prepare(newNorthstar, newNorthstar, oldNorthstar);
        check(result.CanSetup && result.NeedsRestart && result.RequiredPluginIds.SequenceEqual(new[] { Northstar }),
            "exact new Northstar installed but old code loaded requires restart");
        result = GameplayFingerprintSetup.Prepare(newNorthstar, newNorthstar, empty);
        check(result.CanSetup && result.NeedsRestart && result.RequiredPluginIds.SequenceEqual(new[] { Northstar }),
            "server-required exact plugin available locally but disabled is enabled through required roots and restart");
        result = GameplayFingerprintSetup.Prepare(newNorthstar, newNorthstar, newNorthstar);
        check(result.CanSetup && !result.NeedsRestart && result.ExcludedPluginIds.Length == 0,
            "fully matching loaded exact gameplay files need no code-selection restart");
        check(!GameplayFingerprintSetup.Prepare(newNorthstar, empty, oldNorthstar).CanSetup,
            "loaded code without a verified installed exact file cannot be used for restart preparation");
        check(!GameplayFingerprintSetup.Prepare(newNorthstar,
            Snapshot(File("plugin", Northstar, "1.0.1", NorthstarNew)), newNorthstar).CanSetup,
            "exact hash does not waive server-required plugin version identity");

        result = GameplayFingerprintSetup.Prepare(donorOnly, donorOnly, empty);
        check(result.CanSetup && !result.NeedsRestart && result.RequiredPluginIds.Length == 0,
            "enabling available original content leaves live-reload decision to the ordinary content loader");
        var changedDonor = Snapshot(File("content", donor.Id, donor.Version, NorthstarOld));
        check(!GameplayFingerprintSetup.Prepare(donorOnly, changedDonor, changedDonor).CanSetup,
            "same-version modified aircraft content is rejected before selection");
        check(GameplayFingerprintSetup.Prepare(donorOnly, donorOnly, changedDonor).NeedsRestart,
            "different loaded content bytes require restart even when correct disk content is available");
        result = GameplayFingerprintSetup.Prepare(empty, oaVariant, oaVariant);
        check(result.CanSetup && result.NeedsRestart && result.ExcludedPluginIds.SequenceEqual(new[] { Oa }),
            "complete empty host gameplay scope excludes audited extra code without treating unrelated content as a plugin");
        result = GameplayFingerprintSetup.Prepare(empty, Snapshot(), Snapshot(modifier));
        check(result.CanSetup && result.NeedsRestart && result.ExcludedPluginIds.SequenceEqual(new[] { Oa }),
            "extra audited plugin still loaded after disk removal is excluded from startup selection");
        result = GameplayFingerprintSetup.Prepare(empty, Snapshot(donor), Snapshot(donor));
        check(result.CanSetup && !result.NeedsRestart && result.ExcludedPluginIds.Length == 0,
            "extra content remains the ordinary content planner's responsibility");

        check(!GameplayFingerprintSetup.Prepare(null, empty, empty).CanSetup,
            "missing host fingerprints cannot prepare exact setup");
        check(!GameplayFingerprintSetup.Prepare(new GameplayFingerprintSnapshot { Ready = true, Complete = false }, empty, empty).CanSetup,
            "partial server fingerprint scope cannot authorize selection");
        check(!GameplayFingerprintSetup.Prepare(empty, GameplayFingerprintSnapshot.Unavailable("hashing"), empty).CanSetup,
            "pending available-file hashing cannot prepare selection");
        check(!GameplayFingerprintSetup.Prepare(empty, empty, GameplayFingerprintSnapshot.Unavailable("loading")).CanSetup,
            "unverified loaded-code snapshot cannot prepare selection");
        check(!GameplayFingerprintSetup.Prepare(Snapshot(File("plugin", Mig, "1", "abcd")), empty, empty).CanSetup,
            "short fingerprint is rejected instead of accepting a hash prefix");
        check(!GameplayFingerprintSetup.Prepare(newNorthstar, Snapshot(newNorthstar.Records[0], newNorthstar.Records[0]), empty).CanSetup,
            "duplicated installed identity blocks ambiguous exact setup");
        check(!GameplayFingerprintSetup.Prepare(empty, empty, Snapshot(modifier, modifier)).CanSetup,
            "ambiguous loaded identity is rejected without partial exclusions");
        foreach (string protectedOrClient in new[] { "kelly.nuclearoption.joincheck", "com.nikkorap.blueprinter", "example.client.hud" })
        {
            result = GameplayFingerprintSetup.Prepare(empty, Snapshot(File("plugin", protectedOrClient, "1", OaModifier)), empty);
            check(!result.CanSetup && result.ExcludedPluginIds.Length == 0 && result.RequiredPluginIds.Length == 0,
                "protected or unaudited client identity cannot enter gameplay selection: " + protectedOrClient);
        }
        result = GameplayFingerprintSetup.Prepare(oaVariant, oaVariant,
            Snapshot(File("content", donor.Id, donor.Version, donor.Hash.ToUpperInvariant()),
                File("plugin", Oa, modifier.Version, modifier.Hash.ToUpperInvariant())));
        check(result.CanSetup && !result.NeedsRestart, "full hexadecimal fingerprints compare case-insensitively after normalization");
        check(donor.Hash == OaDonor && modifier.Hash == OaModifier,
            "preparation does not mutate caller-owned fingerprint records");

        var compactDonor = GameplayFingerprintPolicy.CompactContent(donorOnly);
        result = GameplayFingerprintSetup.Prepare(compactDonor, oaVariant, oaVariant);
        check(result.CanSetup && result.NeedsRestart && result.ExcludedPluginIds.SequenceEqual(new[] { Oa }),
            "compact exact donor set excludes independent modifier without discarding shared donor");
        result = GameplayFingerprintSetup.Prepare(compactDonor, donorOnly, donorOnly);
        check(result.CanSetup && !result.NeedsRestart,
            "compact required content verifies against full installed and active file fingerprints");
        result = GameplayFingerprintSetup.Prepare(compactDonor, changedDonor, changedDonor);
        check(!result.CanSetup && result.Error.Contains("selected content files") && !result.Error.Contains(donor.Id),
            "compact same-version byte mismatch reports entire set without falsely identifying the changed bundle");
        check(result.RequiredPluginIds.Length == 0 && result.ExcludedPluginIds.Length == 0,
            "failed compact set preflight returns no partial exclusions or enabling roots");
        result = GameplayFingerprintSetup.Prepare(compactDonor, empty, empty);
        check(!result.CanSetup && result.Error.Contains(donor.Id) && result.Error.Contains(donor.Version),
            "compact required content missing locally retains actionable named version");
        result = GameplayFingerprintSetup.Prepare(compactDonor,
            Snapshot(File("content", donor.Id, "9.0", donor.Hash)), donorOnly);
        check(!result.CanSetup && result.Error.Contains(donor.Id),
            "compact exact aggregate hash does not waive required named content version");
        result = GameplayFingerprintSetup.Prepare(compactDonor, donorOnly, changedDonor);
        check(result.CanSetup && result.NeedsRestart,
            "compact host set with matching disk and differing active bundle requires restart");
        result = GameplayFingerprintSetup.Prepare(compactDonor, donorOnly, empty);
        check(result.CanSetup && !result.NeedsRestart,
            "compact host absent active content delegates actual reload capability to loader planner");
        var extraDonor = File("content", "Unrelated installed aircraft", "2.0", NorthstarNew);
        result = GameplayFingerprintSetup.Prepare(compactDonor, Snapshot(donor, extraDonor), donorOnly);
        check(result.CanSetup && !result.NeedsRestart,
            "compact installed aggregate hashes only host-selected content and ignores unrelated installed packs");
        result = GameplayFingerprintSetup.Prepare(GameplayFingerprintPolicy.CompactContent(newNorthstar), newNorthstar, newNorthstar);
        check(result.CanSetup && !result.NeedsRestart && result.RequiredPluginIds.SequenceEqual(new[] { Northstar }),
            "compact empty content digest preserves ordinary strict required gameplay DLL matching");
        result = GameplayFingerprintSetup.Prepare(GameplayFingerprintPolicy.CompactContent(empty), oaVariant, oaVariant);
        check(result.CanSetup && result.NeedsRestart && result.ExcludedPluginIds.SequenceEqual(new[] { Oa }),
            "compact complete empty host scope safely excludes known extra gameplay code");
        check(!GameplayFingerprintSetup.Prepare(compactDonor, compactDonor, donorOnly).CanSetup
            && !GameplayFingerprintSetup.Prepare(compactDonor, donorOnly, compactDonor).CanSetup,
            "available and active setup inventories must contain full member file fingerprints");
        var compactWithCode = GameplayFingerprintPolicy.CompactContent(oaVariant);
        check(!GameplayFingerprintSetup.Prepare(compactWithCode,
            Snapshot(donor, File("plugin", Oa, modifier.Version, NorthstarNew)), oaVariant).CanSetup,
            "compact content aggregate never replaces full byte checking for gameplay DLL records");
        check(compactDonor.ContentSelection.Single().Id == donor.Id && compactDonor.Records.Length == 0
            && compactDonor.ContentSetHash == GameplayFingerprintPolicy.ContentHash(donorOnly.Records),
            "compact preparation leaves caller-owned membership and full aggregate fingerprint intact");

        check(GameplayFingerprintSetup.ValidateResolved(new[] { Northstar }, new[] { Oa }, new[] { Oa }) == "",
            "dependency resolver may enable required code and leave excluded gameplay code disabled");
        check(GameplayFingerprintSetup.ValidateResolved(new[] { Northstar }, new[] { Oa }, new[] { Oa, Northstar }).Contains(Northstar),
            "dependent exclusion closure cannot silently disable a required server gameplay plugin");
        check(GameplayFingerprintSetup.ValidateResolved(new[] { Northstar }, new[] { Oa }, Array.Empty<string>()).Contains(Oa),
            "NOMM parent or required dependency cannot silently re-enable an extra excluded gameplay plugin");
        check(GameplayFingerprintSetup.ValidateResolved(new[] { Oa }, new[] { Oa }, Array.Empty<string>()).Length > 0,
            "contradictory required and excluded gameplay identities are rejected");
        check(GameplayFingerprintSetup.ValidateResolved(Array.Empty<string>(), Array.Empty<string>(), new[] { "example.client.hud" }) == "",
            "ordinary disabled client preference is preserved outside audited gameplay scope");
        check(GameplayFingerprintSetup.ValidateResolved(new[] { "example.client.hud" }, Array.Empty<string>(), Array.Empty<string>()).Length > 0,
            "unknown client cannot become a gameplay-required root");
        check(GameplayFingerprintSetup.ValidateResolved(Array.Empty<string>(), new[] { "com.nikkorap.blueprinter" }, Array.Empty<string>()).Length > 0,
            "protected loader cannot become an audited gameplay exclusion");
        check(GameplayFingerprintSetup.ValidateResolved(null, Array.Empty<string>(), Array.Empty<string>()).Length > 0,
            "missing resolved-selection input fails safely");
        check(GameplayFingerprintSetup.ValidateResolved(Array.Empty<string>(), Array.Empty<string>(), Enumerable.Repeat("client", 513)).Length > 0,
            "resolved plugin selection is bounded");

        const string growlerHash = "2d0fa84f2a7c7b10fee1d030b6bbc1adca1dc275f6e5f111ea2e30395dbd18e0";
        var growler = Snapshot(
            File("plugin", "com.Spiny.GrowlerFrit", "0.0.2", growlerHash),
            File("plugin", "com.Spiny.MpBlocker", "0.0.1", growlerHash),
            File("plugin", "com.Spiny.JammerPod2", "0.0.1", growlerHash),
            File("plugin", "com.Spiny.ECMUpgrade", "0.0.1", growlerHash),
            File("plugin", "com.Spiny.SparkyDome", "0.0.1", growlerHash),
            File("plugin", "com.Spiny.Debugger", "1.0.0", growlerHash));
        var growlerIds = growler.Records.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        result = GameplayFingerprintSetup.Prepare(empty, growler, growler);
        check(result.CanSetup && result.NeedsRestart && result.RequiredPluginIds.Length == 0
            && result.ExcludedPluginIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(growlerIds),
            "extra Ifrit remake excludes every same-DLL gameplay and logging component before restart");
        check(GameplayFingerprintSetup.ValidateResolved(result.RequiredPluginIds, result.ExcludedPluginIds,
            growlerIds.Where(id => id != "com.Spiny.Debugger")).Contains("com.Spiny.Debugger"),
            "Ifrit companion logger cannot silently survive a whole-assembly exclusion");
        result = GameplayFingerprintSetup.Prepare(growler, growler, empty);
        check(result.CanSetup && result.NeedsRestart && result.RequiredPluginIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(growlerIds),
            "required Ifrit host enables all six actual exact-file components from one installed release");
        result = GameplayFingerprintSetup.Prepare(growler, Snapshot(growler.Records.Take(5).ToArray()), empty);
        check(!result.CanSetup && result.RequiredPluginIds.Length == 0 && result.ExcludedPluginIds.Length == 0
            && result.Error.Contains("com.Spiny.Debugger"),
            "missing same-DLL Ifrit companion blocks setup before any partial enabling");
        var changedGrowler = Snapshot(growler.Records.Select(file => File(file.Kind, file.Id, file.Version, NorthstarOld)).ToArray());
        result = GameplayFingerprintSetup.Prepare(growler, changedGrowler, growler);
        check(!result.CanSetup && result.RequiredPluginIds.Length == 0 && result.ExcludedPluginIds.Length == 0,
            "same-version changed Ifrit assembly cannot enter a restart ticket as matching installed code");
        PendingFamily(check);
    }
    private static void PendingFamily(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-candidate-owner-"+Guid.NewGuid().ToString("N")),target=Path.Combine(root,"KR-EW.67.dll");
        const string owner="com.Spiny.GrowlerFrit",child="com.Spiny.Debugger";
        var update=new PendingModUpdate {Plugin=owner,Target="KR-EW.67.dll",Version="0.0.3",PluginVersion="0.0.3"};
        var metadata=new Dictionary<string,QueuedModRequisiteMetadata>(StringComparer.Ordinal) {
            [owner]=new QueuedModRequisiteMetadata {Plugin=owner,OwnerPlugin=owner,Target=target,Version="0.0.3"},
            [child]=new QueuedModRequisiteMetadata {Plugin=child,OwnerPlugin=owner,Target=target,Version="1.0.1"}
        };
        var primary=new ModEntry {Id=owner,Path=target,Version="0.0.3"};
        var companion=new ModEntry {Id=child,Path=target,Version="1.0.1"};
        check(ReferenceEquals(GameplayFingerprintCandidates.PendingFor(primary,new[]{update},metadata,root),update),
            "queued gameplay primary hashes the replacement payload owned by its actual installed DLL");
        check(ReferenceEquals(GameplayFingerprintCandidates.PendingFor(companion,new[]{update},metadata,root),update),
            "queued Ifrit companion hashes the same new payload instead of its old installed assembly");
        var projected=ModRequisiteSelection.WithPending(new[]{new ModEntry {Id=owner,Path=target,Version="0.0.2"},new ModEntry {Id=child,Path=target,Version="1.0.0"}},new[]{update},metadata,root);
        check(projected.Single(e=>e.Id==child).Version=="1.0.1"&&projected.All(e=>ReferenceEquals(GameplayFingerprintCandidates.PendingFor(e,new[]{update},metadata,root),update)),
            "future-version projection and fingerprint source resolver agree for every existing companion in one pending DLL");
        var unqueued=new ModEntry {Id=Northstar,Path=Path.Combine(root,"Northstar.dll"),Version="1.0.0"};
        check(GameplayFingerprintCandidates.PendingFor(unqueued,new[]{update},metadata,root)==null,
            "unrelated gameplay plugin retains its installed source when another DLL has a queued update");
        bool wrongPath=false,wrongVersion=false,wrongOwnerTarget=false;
        companion.Path=Path.Combine(root,"other.dll");
        try {GameplayFingerprintCandidates.PendingFor(companion,new[]{update},metadata,root);}catch(InvalidDataException){wrongPath=true;}
        companion.Path=target;companion.Version="1.0.0";
        try {GameplayFingerprintCandidates.PendingFor(companion,new[]{update},metadata,root);}catch(InvalidDataException){wrongVersion=true;}
        companion.Version="1.0.1";metadata[child].Target=Path.Combine(root,"other.dll");
        try {GameplayFingerprintCandidates.PendingFor(companion,new[]{update},metadata,root);}catch(InvalidDataException){wrongOwnerTarget=true;}
        check(wrongPath&&wrongVersion&&wrongOwnerTarget,"queued gameplay companion cannot borrow a payload across paths, versions or mismatched metadata ownership");
        metadata[child].Target=target;
        var content=new ModEntry {Id="ifrit-content",Content=true,WrapperPlugin=owner,WrapperPath=target,Version="0.0.2"};
        check(ReferenceEquals(GameplayFingerprintCandidates.PendingFor(content,new[]{update},metadata,root),update),
            "embedded content keeps its verified wrapper update source while its bundle version is projected separately");
        var newlyInstalled=new ModEntry {Id=owner,Path="KR-EW.67.dll",Version="0.0.3"};
        check(ReferenceEquals(GameplayFingerprintCandidates.PendingFor(newlyInstalled,new[]{update},metadata,root),update),
            "newly installed prerequisite's relative projected path binds to the same verified queued target");
        PendingEmbeddedContent(check,root,target,update,metadata);
    }

    private static void PendingEmbeddedContent(Action<bool,string> check,string root,string target,PendingModUpdate update,
        Dictionary<string,QueuedModRequisiteMetadata> metadata)
    {
        const string child="com.Spiny.Debugger";
        var content=new ModEntry {Id="multi-plugin-content",Content=true,Path="resource:MultiPlugin:pack.nobp",WrapperPath=target,Version="0.0.2"};
        var pending=new[]{update};
        check(ReferenceEquals(GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root),update),
            "embedded content with no single wrapper GUID binds to its verified physical DLL replacement");
        var versions=GameplayFingerprintCandidates.ContentVersions(new[]{content},pending,metadata,root);
        check(versions.Length==1&&versions[0].Key==content.Id&&versions[0].Value==update.Version,
            "multi-plugin content version projection uses the same queued source as exact fingerprinting");
        content.WrapperPlugin=child;
        check(ReferenceEquals(GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root),update),
            "embedded content named by a companion GUID follows its verified queued owner");
        content.WrapperPath=Path.Combine(root,"different.dll");
        check(Rejected(()=>GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root)),
            "a named content wrapper cannot borrow its owner's queued bytes for another physical DLL");
        content.WrapperPlugin="";
        check(GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root)==null,
            "unrelated multi-plugin content retains its installed source");
        content.WrapperPath=target;
        var sameTarget=new PendingModUpdate {Plugin=child,Target=update.Target,Version="1.0.0"};
        check(Rejected(()=>GameplayFingerprintCandidates.PendingFor(content,new[]{update,sameTarget},metadata,root)),
            "two queued owners of one content DLL are rejected before choosing a payload");
        var duplicateOwner=new PendingModUpdate {Plugin=update.Plugin,Target="different.dll",Version=update.Version};
        check(Rejected(()=>GameplayFingerprintCandidates.PendingFor(content,new[]{update,duplicateOwner},metadata,root)),
            "one queued owner cannot ambiguously claim two replacement DLLs");
        var owner=metadata[update.Plugin];owner.Target=Path.Combine(root,"different.dll");
        check(Rejected(()=>GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root)),
            "matching queued target still requires verified owner metadata for that same physical source");
        owner.Target=target;owner.Error="Unverified payload";
        check(Rejected(()=>GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root)),
            "failed queued owner verification never supplies embedded content bytes");
        owner.Error="";
        var missing=new Dictionary<string,QueuedModRequisiteMetadata>(StringComparer.Ordinal);
        check(Rejected(()=>GameplayFingerprintCandidates.PendingFor(content,pending,missing,root)),
            "physical target matching alone cannot substitute unverified queued payload metadata");
        var second=new ModEntry {Id="second-pack",Content=true,WrapperPath=target,Version="0.0.2"};
        check(Rejected(()=>GameplayFingerprintCandidates.ContentVersions(new[]{content,second},pending,metadata,root)),
            "multiple content packs in one queued DLL retain the existing explicit restart-and-rescan requirement");
        content.ExternalDisabled=true;
        check(GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root)==null
            &&GameplayFingerprintCandidates.ContentVersions(new[]{content},pending,metadata,root).Length==0,
            "NOMM-disabled content never borrows an unrelated active-folder update");
        content.ExternalDisabled=false;content.WrapperPath="";
        check(GameplayFingerprintCandidates.PendingFor(content,pending,metadata,root)==null,
            "standalone content with no wrapper keeps its installed source");
        content.WrapperPath=target;
        PendingContentPayload(check,root,content,update,metadata);
    }

    private static bool Rejected(Action action)
    {try{action();return false;}catch(InvalidDataException){return true;}}

    private static void PendingContentPayload(Action<bool,string> check,string root,ModEntry content,PendingModUpdate update,
        Dictionary<string,QueuedModRequisiteMetadata> metadata)
    {
        string job=Path.Combine(root,"queued");Directory.CreateDirectory(job);
        string payload=Path.Combine(job,"payload.bin");update.Job=job;
        string releaseVersion=update.Version;update.Version=content.Version;
        try
        {
            using(var assembly=Mono.Cecil.AssemblyDefinition.CreateAssembly(new Mono.Cecil.AssemblyNameDefinition("MultiPlugin",new Version(1,0,0,0)),"MultiPlugin",Mono.Cecil.ModuleKind.Dll))
                assembly.Write(payload);
            var chosen=GameplayFingerprintCandidates.PendingFor(content,new[]{update},metadata,root)!;
            var available=GameplayFingerprintInventory.ReadFiles(new[]{new GameplayFingerprintSource {
                Kind="content",Id=content.Id,Version=chosen.Version,Path=ModUpdateFiles.Within(chosen.Job,"payload.bin"),AllowedRoot=chosen.Job,Staged=true
            }},System.Threading.CancellationToken.None);
            check(available.Ready&&available.Complete&&available.Records.Single().Hash==ModUpdateFiles.Hash(payload),
                "queued multi-plugin embedded content fingerprints the actual replacement payload bytes");
            var server=Snapshot(File("content",content.Id,chosen.Version,ModUpdateFiles.Hash(payload)));
            var loaded=Snapshot(File("content",content.Id,chosen.Version,NorthstarOld));
            var prepared=GameplayFingerprintSetup.Prepare(server,available,loaded);
            check(prepared.CanSetup&&prepared.NeedsRestart,
                "matching same-version queued embedded content passes exact setup while old loaded bytes still require restart");
        }
        finally
        {
            // Only this test-created file and its now-empty folders are removed.
            update.Version=releaseVersion;
            if(System.IO.File.Exists(payload))System.IO.File.Delete(payload);
            Directory.Delete(job);Directory.Delete(root);
        }
    }
}
