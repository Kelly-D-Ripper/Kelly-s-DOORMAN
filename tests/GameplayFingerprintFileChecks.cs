using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class GameplayFingerprintFileRegression
{
    private const string Northstar = "com.ta30plus.northstar";
    private const string Oa = "com.ial.oa27variant";
    private static GameplayFingerprintSource Source(string root, string path, string id = Northstar, string version = "1.0.0", string kind = "plugin", string mvid = "", bool staged = false)
        => new GameplayFingerprintSource { AllowedRoot = root, Path = path, Id = id, Version = version, Kind = kind, ModuleVersionId = mvid, Staged = staged };
    private static GameplayFingerprintSnapshot Read(params GameplayFingerprintSource[] sources)
        => GameplayFingerprintInventory.ReadFiles(sources, CancellationToken.None, "2.0.1");
    private static bool Unavailable(GameplayFingerprintSnapshot value) => !value.Ready && !value.Complete && value.Records.Length == 0 && value.Error.Length > 0;
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    private static string WritePlugin(string path, params string[] ids)
        => WritePlugin(path, _ => "1.0.0", ids);
    private static string WritePlugin(string path, Func<string,string> version, params string[] ids)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var assembly = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("TrustedGameplayFixture", new Version(1, 0, 0, 0)), "TrustedGameplayFixture", ModuleKind.Dll);
        var module = assembly.MainModule;
        var bepin = new AssemblyNameReference("BepInEx", new Version(5, 4, 23, 5));
        module.AssemblyReferences.Add(bepin);
        var attributeType = new TypeReference("BepInEx", "BepInPlugin", module, bepin);
        var ctor = new MethodReference(".ctor", module.TypeSystem.Void, attributeType) { HasThis = true };
        for (int i = 0; i < 3; i++) ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        for (int i = 0; i < ids.Length; i++)
        {
            var plugin = new TypeDefinition("TrustedFixture", "Plugin" + i, TypeAttributes.Class | TypeAttributes.Public,
                new TypeReference("BepInEx", "BaseUnityPlugin", module, bepin));
            module.Types.Add(plugin);
            var identity = new CustomAttribute(ctor);
            foreach (string value in new[] { ids[i], "Trusted fixture " + i, version(ids[i]) })
                identity.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String, value));
            plugin.CustomAttributes.Add(identity);
        }
        var marker = new TypeDefinition("TrustedFixture", "ContentMarker", TypeAttributes.Class | TypeAttributes.Public, module.TypeSystem.Object);
        marker.Fields.Add(new FieldDefinition("Value", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault, module.TypeSystem.Int32) { Constant = 1 });
        module.Types.Add(marker);
        module.Resources.Add(new EmbeddedResource("trusted-fixture.nobp", ManifestResourceAttributes.Private, new byte[] { 1, 2, 3, 4 }));
        string mvid = module.Mvid.ToString("D");
        assembly.Write(path);
        return mvid;
    }
    private static void ChangeMarkerKeepingMvid(string path)
    {
        byte[] original = File.ReadAllBytes(path);
        using var stream = new MemoryStream(original);
        using var assembly = AssemblyDefinition.ReadAssembly(stream);
        assembly.MainModule.Types.Single(t => t.Name == "ContentMarker").Fields.Single(f => f.Name == "Value").Constant = 2;
        assembly.Write(path);
    }

    public static void Run(Action<bool, string> check)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "doorman-gameplay-files-" + Guid.NewGuid().ToString("N"));
        string plugins = Path.Combine(temporary, "plugins"), disabled = Path.Combine(temporary, "disabledPlugins"), staged = Path.Combine(temporary, "updates");
        Directory.CreateDirectory(plugins); Directory.CreateDirectory(disabled); Directory.CreateDirectory(staged);
        try
        {
            string pluginPath = Path.Combine(plugins, "renamed-aircraft.dll");
            string mvid = WritePlugin(pluginPath, Northstar);
            var source = Source(plugins, pluginPath, mvid: mvid);
            var initial = Read(source);
            check(initial.Ready && initial.Complete && initial.LoaderVersion == "2.0.1" && initial.Records.Single().Hash == Hash(pluginPath),
                "real file pipeline verifies generated DLL identity/MVID and streams the complete file fingerprint");
            check(initial.Records[0].Id == Northstar && initial.Records[0].Version == "1.0.0", "renamed DLL filename never substitutes for its actual BepInPlugin identity");

            var sameFile = new FileInfo(pluginPath); long length = sameFile.Length;
            DateTime written = sameFile.LastWriteTimeUtc, created = sameFile.CreationTimeUtc;
            ChangeMarkerKeepingMvid(pluginPath);
            File.SetLastWriteTimeUtc(pluginPath, written); File.SetCreationTimeUtc(pluginPath, created);
            var changed = Read(source);
            check(new FileInfo(pluginPath).Length == length && File.GetLastWriteTimeUtc(pluginPath) == written,
                "generated DLL edit fixture preserves length and timestamp while changing bytes");
            check(changed.Ready && changed.Complete && changed.Records[0].Hash == Hash(pluginPath)
                && changed.Records[0].Hash != initial.Records[0].Hash,
                "explicit inventory refresh rehashes same-size timestamp/MVID-preserving gameplay edits");
            LoadedGuard(check, plugins, source, initial, changed);
            var replaced = Read(Source(plugins, pluginPath, mvid: Guid.NewGuid().ToString("D")));
            check(Unavailable(replaced) && replaced.Error.Contains("replaced on disk", StringComparison.Ordinal),
                "on-disk replacement with a different MVID cannot represent the already loaded aircraft DLL");
            check(Unavailable(Read(Source(plugins, pluginPath, id: Oa))), "installed DLL with a different audited GUID cannot impersonate required aircraft plugin");
            check(Unavailable(Read(Source(plugins, pluginPath, version: "2.0.0"))), "actual BepInPlugin version is checked against captured loaded version");

            string multiPath = Path.Combine(plugins, "two-gameplay-plugins.dll");
            string multiMvid = WritePlugin(multiPath, Northstar, Oa);
            var multiple = Read(Source(plugins, multiPath, mvid: multiMvid), Source(plugins, multiPath, id: Oa, mvid: multiMvid));
            check(multiple.Ready && multiple.Complete && multiple.Records.Length == 2 && multiple.Records.All(r => r.Hash == Hash(multiPath)),
                "shared DLL yields independent audited plugin identities with one exact shared byte fingerprint");
            check(Unavailable(Read(Source(plugins, multiPath, mvid: multiMvid), Source(plugins, multiPath, id: "com.ial.mig15s", mvid: multiMvid))),
                "shared source hash cache still verifies every requested plugin identity");
            var ifritVersions = new Dictionary<string,string>(StringComparer.Ordinal) {
                ["com.Spiny.GrowlerFrit"] = "0.0.2", ["com.Spiny.MpBlocker"] = "0.0.1",
                ["com.Spiny.JammerPod2"] = "0.0.1", ["com.Spiny.ECMUpgrade"] = "0.0.1",
                ["com.Spiny.SparkyDome"] = "0.0.1", ["com.Spiny.Debugger"] = "1.0.0"
            };
            string ifritPath = Path.Combine(plugins, "KR-EW.67.dll");
            string ifritMvid = WritePlugin(ifritPath, id => ifritVersions[id], ifritVersions.Keys.ToArray());
            var ifritSources = ifritVersions.Select(pair => Source(plugins, ifritPath, pair.Key, pair.Value, mvid: ifritMvid)).ToArray();
            var ifrit = Read(ifritSources);
            check(ifrit.Ready && ifrit.Complete && ifrit.Records.Length == 6
                && ifrit.Records.All(record => record.Hash == Hash(ifritPath) && record.Version == ifritVersions[record.Id]),
                "real file pipeline retains six actual Ifrit declaration versions with one complete shared DLL hash");
            var wrongIfrit = ifritSources.Take(5).Concat(new[] { Source(plugins, ifritPath, "com.Spiny.Debugger", "9.0.0", mvid: ifritMvid) }).ToArray();
            check(Unavailable(Read(wrongIfrit)), "last same-DLL Ifrit companion identity is verified despite reuse of previous file hash");
            var ifritGuard = new GameplayLoadedFileGuard();
            check(ifritGuard.Apply(ifritSources, ifrit).Complete, "all six loaded Ifrit components establish one consistent physical assembly baseline");
            ChangeMarkerKeepingMvid(ifritPath);
            var editedIfrit = Read(ifritSources);
            var rejectedIfrit = ifritGuard.Apply(ifritSources, editedIfrit);
            check(editedIfrit.Complete && editedIfrit.Records.Select(record => record.Hash).Distinct().Count() == 1
                && Unavailable(rejectedIfrit) && rejectedIfrit.Error.Contains("Restart before comparing", StringComparison.Ordinal),
                "edited loaded Ifrit DLL invalidates all six cached identities and demands restart instead of reporting disk bytes as loaded code");
            check(Unavailable(Read(source, source)), "duplicate inventory identity fails without partial successful record output");
            string duplicatePath = Path.Combine(plugins, "ambiguous.dll"); WritePlugin(duplicatePath, Northstar, Northstar);
            check(Unavailable(Read(Source(plugins, duplicatePath))), "duplicate BepInPlugin declarations cannot resolve to an arbitrary implementation");

            string stagedPath = Path.Combine(staged, "payload.bin"); File.Copy(pluginPath, stagedPath);
            var downloaded = Read(Source(staged, stagedPath, staged: true));
            check(downloaded.Ready && downloaded.Complete && downloaded.Records.Single().Hash == Hash(pluginPath),
                "staged payload.bin is statically inspected using actual DLL identity before setup");
            check(Unavailable(Read(Source(staged, stagedPath))), "payload.bin cannot masquerade as an installed DLL without explicit staged provenance");
            check(Unavailable(Read(Source(staged, stagedPath, id: Oa, staged: true))), "staged DLL with wrong actual plugin GUID fails closed");
            check(Unavailable(Read(Source(staged, stagedPath, version: "9.0.0", staged: true))), "staged DLL cannot rely on its release tag instead of actual plugin version");
            string disabledPath = Path.Combine(disabled, "aircraft.dll"); File.Copy(pluginPath, disabledPath);
            check(Read(Source(disabled, disabledPath)).Complete, "known disabled-source root can inspect locally available exact gameplay DLL without loading it");
            check(Unavailable(Read(Source(plugins, disabledPath))), "disabled source must use its explicitly allowed root rather than bypassing plugin-folder boundary");

            string bundlePath = Path.Combine(plugins, "stock-donor.nobp"); File.WriteAllBytes(bundlePath, new byte[] { 0, 1, 2, 3, 4, 5 });
            var bundle = Source(plugins, bundlePath, id: "Stock donor", kind: "content");
            var bundleResult = Read(bundle);
            check(bundleResult.Ready && bundleResult.Complete && bundleResult.Records.Single().Hash == Hash(bundlePath),
                "standalone selected content uses full file fingerprint and actual captured content identity");
            StandaloneLoadedGuard(check, bundle, bundleResult);
            var wrapper = Read(Source(plugins, pluginPath, id: "Embedded donor", kind: "content", mvid: mvid), source);
            check(wrapper.Ready && wrapper.Complete && wrapper.Records.Length == 2 && wrapper.Records.Select(r => r.Hash).Distinct().Count() == 1,
                "embedded content and gameplay plugin fingerprints bind to the same verified wrapper file bytes");
            check(Unavailable(Read(Source(plugins, pluginPath, id: "Embedded donor", kind: "content", mvid: Guid.NewGuid().ToString("D")))),
                "embedded content wrapper replacement is checked against loaded assembly MVID");
            check(Unavailable(Read(bundle, Source(plugins, Path.Combine(plugins, "gone.nobp"), id: "Gone", kind: "content"))),
                "one missing content source invalidates whole inventory instead of retaining misleading partial success");

            string outside = Path.Combine(temporary, "outside.nobp"); File.Copy(bundlePath, outside);
            string sibling = Path.Combine(temporary, "plugins-other"); Directory.CreateDirectory(sibling);
            string siblingPath = Path.Combine(sibling, "pack.nobp"); File.Copy(bundlePath, siblingPath);
            check(Unavailable(Read(Source(plugins, outside, id: "outside", kind: "content")))
                && Unavailable(Read(Source(plugins, siblingPath, id: "sibling", kind: "content"))),
                "outside and sibling-prefix roots cannot supply gameplay fingerprint files");
            check(Unavailable(Read(Source(plugins, "relative.nobp", id: "relative", kind: "content"))), "relative content paths require explicit rooted provenance");
            string unsupported = Path.Combine(plugins, "image.png"); File.Copy(bundlePath, unsupported);
            check(Unavailable(Read(Source(plugins, unsupported, id: "image", kind: "content"))), "preview images and arbitrary file extensions are excluded from gameplay hashing");
            string badDll = Path.Combine(plugins, "broken.dll"); File.WriteAllText(badDll, "not a managed plugin");
            check(Unavailable(Read(Source(plugins, badDll))), "invalid managed file is reported unavailable without executing or loading it");
            check(Unavailable(Read(Source(plugins, pluginPath, id: "com.example.hud"))), "file pipeline never expands audited scope to unrelated client plugins");
            using (var held = new FileStream(bundlePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(Unavailable(Read(bundle)), "locked gameplay file prevents verification rather than reusing a stale hash");

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool honoured = false;
                try { GameplayFingerprintInventory.ReadFiles(new[] { bundle }, cancelled.Token); }
                catch (OperationCanceledException) { honoured = true; }
                check(honoured, "cancelled background inventory does not continue hashing installed content");
            }
            var overCount = Enumerable.Range(0, GameplayFingerprintPolicy.MaxRecords + 1)
                .Select(i => Source(plugins, bundlePath, id: "content-" + i, kind: "content"));
            check(Unavailable(GameplayFingerprintInventory.ReadFiles(overCount, CancellationToken.None)), "source count is bounded before any hashing work");

            string largeDll = Path.Combine(plugins, "oversized.dll");
            using (var large = new FileStream(largeDll, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                large.SetLength(InstalledFilePolicy.MaximumMetadataDllBytes + 1);
            var tooLarge = Read(Source(plugins, largeDll));
            check(Unavailable(tooLarge) && tooLarge.Error.Contains("metadata inspection limit", StringComparison.Ordinal),
                "oversized DLL is rejected before unbounded metadata parsing or in-memory copy");

            string streamed = Path.Combine(plugins, "large-stream.nobp");
            using (var file = File.Create(streamed))
            {
                var block = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i * 13)).ToArray();
                for (int i = 0; i < 128; i++) file.Write(block, 0, block.Length);
            }
            var streamResult = Read(Source(plugins, streamed, id: "large-stream", kind: "content"));
            check(streamResult.Ready && streamResult.Complete && streamResult.Records.Single().Hash == Hash(streamed),
                "large standalone content is streamed completely through actual inventory pipeline");
            Links(check, temporary, plugins, outside);
        }
        finally
        {
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(temporary).StartsWith(parent, StringComparison.OrdinalIgnoreCase) && Directory.Exists(temporary)) Directory.Delete(temporary, true);
        }
    }
    private static GameplayFingerprintSnapshot Snapshot(params GameplayFileRecord[] records)
        => new GameplayFingerprintSnapshot { Ready = true, Complete = true, LoaderVersion = "2.0.1", Records = records };
    private static GameplayFileRecord Record(GameplayFileRecord source, string? id = null, string? hash = null)
        => new GameplayFileRecord { Kind = source.Kind, Id = id ?? source.Id, Version = source.Version, Hash = hash ?? source.Hash };
    private static void StandaloneLoadedGuard(Action<bool,string> check,GameplayFingerprintSource source,GameplayFingerprintSnapshot original)
    {
        source.ContentLoadIdentity=Guid.NewGuid().ToString("N");
        var guard=new GameplayLoadedFileGuard();
        check(guard.Apply(new[]{source},original).Complete,"standalone bundle load establishes immutable file bytes for its exact registry object");
        var different=Snapshot(Record(original.Records[0],hash:new string('a',64)));
        var rejected=guard.Apply(new[]{source},different);
        check(Unavailable(rejected)&&rejected.Error.Contains("Restart before comparing",StringComparison.Ordinal),
            "same-version standalone bundle edited on disk cannot claim the new bytes are loaded");
        check(guard.Apply(new[]{source},original).Complete,"failed standalone drift observation leaves original content baseline intact");
        source.ContentLoadIdentity=Guid.NewGuid().ToString("N");
        check(guard.Apply(new[]{source},different).Complete,"actual bundle reload establishes a new registry load identity at the same source path");
        bool reloads=true;
        for(int i=0;i<GameplayFingerprintPolicy.MaxRecords*3;i++)
        {source.ContentLoadIdentity=Guid.NewGuid().ToString("N");reloads&=guard.Apply(new[]{source},i%2==0?original:different).Complete;}
        check(reloads,"reloading one standalone bundle replaces its prior generation without unbounded history growth");
        var invalid=Source(source.AllowedRoot,source.Path,id:source.Id,kind:"content");invalid.ContentLoadIdentity="not-a-load";
        check(Unavailable(new GameplayLoadedFileGuard().Apply(new[]{invalid},original)),"invalid standalone load identity cannot establish loaded-file evidence");
        source.ContentLoadIdentity=Guid.NewGuid().ToString("N");
        var transactional=new GameplayLoadedFileGuard();transactional.Apply(new[]{source},original);
        var newcomer=Source(source.AllowedRoot,Path.Combine(source.AllowedRoot,"new-content.nobp"),id:"new-content",kind:"content");newcomer.ContentLoadIdentity=Guid.NewGuid().ToString("N");
        check(Unavailable(transactional.Apply(new[]{newcomer,source},Snapshot(Record(original.Records[0],newcomer.Id),Record(different.Records[0]))))
            &&transactional.Apply(new[]{newcomer},Snapshot(Record(different.Records[0],newcomer.Id))).Complete,
            "failed mixed content batch establishes no earlier hash baseline for another newly seen standalone source");
        var ambiguous=Source(source.AllowedRoot,source.Path,id:"ambiguous-content",kind:"content");ambiguous.ContentLoadIdentity=Guid.NewGuid().ToString("N");
        check(Unavailable(new GameplayLoadedFileGuard().Apply(new[]{source,ambiguous},Snapshot(original.Records[0],Record(original.Records[0],ambiguous.Id)))),
            "different selected load identities cannot claim one physical standalone source in the same generation");
        source.ContentLoadIdentity="";
    }
    private static void LoadedGuard(Action<bool, string> check, string plugins, GameplayFingerprintSource source,
        GameplayFingerprintSnapshot initial, GameplayFingerprintSnapshot changed)
    {
        var guard = new GameplayLoadedFileGuard();
        check(guard.Apply(new[] { source }, initial).Complete && guard.Apply(new[] { source }, initial).Complete,
            "loaded-file guard accepts a stable first-observed DLL across repeated complete inventories");
        var swapped = guard.Apply(new[] { source }, changed);
        check(Unavailable(swapped) && swapped.Error.Contains("changed on disk", StringComparison.Ordinal)
            && swapped.Error.Contains("Restart", StringComparison.Ordinal),
            "fresh disk bytes with the already-loaded path/MVID require restart instead of becoming loaded-code evidence");
        check(guard.Apply(new[] { source }, initial).Complete,
            "rejected same-MVID disk change never overwrites the immutable loaded-file baseline");
        var newlyLoaded = Source(plugins, source.Path, mvid: Guid.NewGuid().ToString("D"));
        check(guard.Apply(new[] { newlyLoaded }, changed).Complete,
            "a distinct captured loaded-module MVID starts independent file evidence at the same path");

        var pending = new GameplayLoadedFileGuard();
        var unavailable = GameplayFingerprintSnapshot.Unavailable("Not ready yet");
        check(ReferenceEquals(pending.Apply(new[] { source }, unavailable), unavailable)
            && pending.Apply(new[] { source }, changed).Complete,
            "unavailable initial inventory establishes no loaded-file baseline");
        var partial = new GameplayLoadedFileGuard();
        check(!partial.Apply(new[] { source }, new GameplayFingerprintSnapshot { Ready = true, Complete = false, Records = initial.Records }).Complete
            && partial.Apply(new[] { source }, changed).Complete,
            "partial initial inventory does not poison later complete loaded-file evidence");

        var transactional = new GameplayLoadedFileGuard();
        check(transactional.Apply(new[] { source }, initial).Complete, "transactional guard fixture establishes its original loaded DLL");
        var newSource = Source(plugins, Path.Combine(plugins, "guard-new.dll"), id: Oa, mvid: Guid.NewGuid().ToString("D"));
        var conflict = Snapshot(Record(initial.Records[0], Oa), Record(changed.Records[0]));
        check(Unavailable(transactional.Apply(new[] { newSource, source }, conflict)),
            "later conflicting loaded DLL invalidates a whole batch of proposed file baselines");
        check(transactional.Apply(new[] { newSource }, Snapshot(Record(changed.Records[0], Oa))).Complete,
            "failed loaded-file batch commits no earlier additions that could reject legitimate subsequent evidence");
        var missing = new GameplayLoadedFileGuard();
        check(Unavailable(missing.Apply(new[] { newSource }, initial))
            && missing.Apply(new[] { newSource }, Snapshot(Record(changed.Records[0], Oa))).Complete,
            "loaded source without matching verified identity fails without establishing a baseline");
        var ambiguous = new GameplayLoadedFileGuard();
        check(Unavailable(ambiguous.Apply(new[] { source }, Snapshot(Record(initial.Records[0]), Record(changed.Records[0])))),
            "ambiguous duplicate verified identities cannot establish loaded-file evidence");
        check(Unavailable(new GameplayLoadedFileGuard().Apply(new[] { Source(plugins, source.Path, mvid: "invalid") }, initial)),
            "invalid loaded-module identity fails closed in loaded-file consistency guard");
        check(Unavailable(new GameplayLoadedFileGuard().Apply(new[] { Source(plugins, "relative.dll", mvid: source.ModuleVersionId) }, initial)),
            "loaded-file consistency baseline requires an absolute captured path");

        var noMvid = new GameplayLoadedFileGuard();
        var availableSource = Source(plugins, source.Path);
        check(noMvid.Apply(new[] { availableSource }, initial).Complete && noMvid.Apply(new[] { availableSource }, changed).Complete,
            "unloaded locally available files without a captured MVID are freshly hashed rather than assigned a loaded baseline");
        var staging = new GameplayLoadedFileGuard();
        var stagedSource = Source(plugins, source.Path, mvid: source.ModuleVersionId, staged: true);
        check(staging.Apply(new[] { stagedSource }, initial).Complete && staging.Apply(new[] { stagedSource }, changed).Complete,
            "staged downloads remain disk evidence and never receive an immutable loaded-code baseline");

        var history = new GameplayLoadedFileGuard();
        bool retained = true;
        for (int i = 0; i < GameplayFingerprintPolicy.MaxRecords * 2; i++)
            retained &= history.Apply(new[] { Source(plugins, Path.Combine(plugins, "history-" + i + ".dll"), mvid: source.ModuleVersionId) }, initial).Complete;
        check(retained && Unavailable(history.Apply(new[] { Source(plugins, Path.Combine(plugins, "history-overflow.dll"), mvid: source.ModuleVersionId) }, initial)),
            "loaded-file consistency history is bounded and reports unavailable on overflow");
        check(history.Apply(new[] { Source(plugins, Path.Combine(plugins, "history-0.dll"), mvid: source.ModuleVersionId) }, initial).Complete,
            "loaded-file history overflow preserves existing immutable evidence");
    }
    private static void Links(Action<bool, string> check, string temporary, string plugins, string outside)
    {
        try
        {
            string link = Path.Combine(plugins, "linked.nobp"); File.CreateSymbolicLink(link, outside);
            check(Unavailable(Read(Source(plugins, link, id: "link", kind: "content"))), "symbolic gameplay source cannot cross file provenance boundary");
            string rootLink = Path.Combine(temporary, "linked-plugins"); Directory.CreateSymbolicLink(rootLink, plugins);
            check(Unavailable(Read(Source(rootLink, Path.Combine(rootLink, "stock-donor.nobp"), id: "root-link", kind: "content"))),
                "symbolic gameplay root requires manual verification even when descendant is a regular file");
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PlatformNotSupportedException)
        { Console.WriteLine("NOTE: gameplay fingerprint symbolic-link fixtures unavailable (" + ex.GetType().Name + ")."); }
    }
}
