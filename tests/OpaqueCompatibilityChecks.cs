using System;
using System.Collections.Generic;
using System.Linq;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class OpaqueCompatibilityRegression
{
    private const string Wire = "0.34.1_1daee97e5e09_rsl-v0.8.4-Default";
    // Exact required content IDs/versions shown in the attached 1.3.8 report.
    private const string Bundles = "--1509_palafighter1-v1.0.1_--aryx_expanded_weapons-v1.1.1_--aryx_f16m-v1.2.3"
        + "_--aryx_interceptor1-v1.1.7.1_--aryx_kingraptor-v1.0.4_--aryx_lightfighter1-v1.1.3"
        + "_--aryx_lighthelicopter1-v1.1.1_--aryx_mc260_chimera-v1.2.0_--aryx_mig-15-v1.1.3"
        + "_--aryx_navalexpansion-v1.0.4_--aryx_propattacker1-v1.0.1";
    private static string Named(string bundles) => "0.34.1_com.nikkorap.blueprinter-v2.0.1_" + bundles;
    private static string Local(string? bundles, out string notice, bool finished = true, bool ready = false)
        => DiagnosticSignature.Select(Wire, Wire, "2.0.1", bundles, finished, ready,
            "Some content patches did not finish loading. Restart is required.", out notice);

    internal static void Run(Action<bool,string> check)
    {
        var opaque = Signature.Parse(Wire);
        check(!opaque.Complete && opaque.Game == "0.34.1" && opaque.Mods.Count == 0,
            "reported opaque Blueprinter hash with Resolute suffix yields no invented mod identity");
        check(opaque.KnownOpaqueHash && Signature.Parse("0.34.1_1daee97e5e09").KnownOpaqueHash,
            "the parser recognises the verified Blueprinter hash frame without inferring its hidden contents");
        check(!Signature.Parse("0.34.1_1DAEE97E5E09_rsl-v0.8.4-Default").Complete,
            "opaque-prefix rejection also handles uppercase hexadecimal");
        check(!Signature.Parse("0.34.1_" + new string('a',64) + "_other-v1").Complete,
            "long opaque prefixes never become named mod rows through appended plugin suffixes");
        check(Signature.Parse(Named("--1daee97e5e09_rsl-v1.0.0")).Complete,
            "explicit bundle names remain distinct from an unexpanded leading opaque hash");
        string unknown = Diagnostics.Compare(Named(Bundles), Wire);
        check(!unknown.Contains("MISSING:") && !unknown.Contains("EXTRA:") && unknown.Contains("Your local mod versions"),
            "a complete server list with unknown local data does not fabricate install/remove instructions or blame the host");
        var unknownView = JoinSummary.Create(Named(Bundles), Wire, "0.34.1_2b9371678a89", Wire, "");
        check(unknownView.Status == JoinStatus.Unknown && unknownView.Actions.Count == 0,
            "the raw report wire no longer creates the false thirteen-change checklist");

        string selected = Local(Bundles, out var warning);
        var actual = Signature.Parse(selected);
        check(actual.Complete && actual.Mods.Count == 12 && actual.Mods["com.nikkorap.blueprinter"] == "2.0.1"
            && actual.Mods["aryx_interceptor1"] == "1.1.7.1",
            "runtime signature fallback preserves exact required bundle and actual loaded Blueprinter versions");
        check(warning.Contains("selected content versions") && warning.Contains("finished its loading routine")
            && warning.Contains("Some content patches did not finish loading"),
            "unresolved patch loading remains explicitly visible alongside exact runtime version metadata");
        string report = Diagnostics.Compare(Named(Bundles), selected, false);
        check(!report.Contains("MISSING:") && !report.Contains("EXTRA:") && report.Contains("named game and mod versions match")
            && !report.Contains("compatibility strings match"),
            "present exact versions avoid all false missing rows without claiming that raw wire identities match");
        var view = JoinSummary.Create(Named(Bundles), selected, "0.34.1_2b9371678a89", Wire, "");
        check(view.Matching.Count == 12 && view.Actions.Count == 1 && view.Actions[0].Action == "CHECK FILES"
            && view.Status != JoinStatus.Match,
            "known versions can match while the unchanged raw wire still requires investigation");

        string extra = Local(Bundles + "_--f117a-v0.2.10", out _);
        report = Diagnostics.Compare(Named(Bundles), extra, false);
        check(report.Contains("EXTRA: f117a 0.2.10") && !report.Contains("1daee97e5e09_rsl") && !report.Contains("MISSING:"),
            "extra content diagnostics use only an actual selected bundle instead of the hash-plus-suffix pseudo-mod");
        string wrong = Local(Bundles.Replace("aryx_f16m-v1.2.3", "aryx_f16m-v1.2.2"), out _);
        report = Diagnostics.Compare(Named(Bundles), wrong, false);
        check(report.Contains("WRONG VERSION: aryx_f16m - server 1.2.3 | you 1.2.2") && !report.Contains("MISSING: aryx_f16m"),
            "runtime fallback reports an actual wrong version instead of reinstalling a present bundle");
        string missing = Local(Bundles.Replace("_--aryx_f16m-v1.2.3", ""), out _);
        check(Diagnostics.Compare(Named(Bundles), missing, false).Contains("MISSING: aryx_f16m"),
            "an absent bundle is actionable only when the loader supplies a complete valid selected signature");
        check(Local(Bundles, out warning, finished:false) == Wire && warning.Contains("still loading"),
            "unfinished loading cannot promote an intermediate bundle signature to the runtime fallback");
        check(Local(null, out _) == Wire && Local("", out _) == Wire,
            "missing runtime metadata preserves the opaque wire instead of guessing an empty mod inventory");
        check(Local("--A-v1_--A-v2", out _) == Wire && Local("--A-v1_--broken", out _) == Wire,
            "duplicate or incomplete loader bundle signatures cannot drive fabricated differences");
        check(Local("--A-v1\n", out _) == Wire && Local(new string('x',Protocol.MaxText+1), out _) == Wire,
            "runtime fallback rejects control characters and oversized metadata");
        check(Local("NOBUNDLES", out _) == Named("NOBUNDLES"),
            "authoritative no-bundle metadata still reports the loaded Blueprinter version");
        check(Local(Bundles, out warning, ready:true) == selected && !warning.Contains("live reload is unavailable"),
            "healthy loading keeps opaque-wire uncertainty separate from a patch-loading warning");
        check(DiagnosticSignature.Select(Wire, Wire, "3.0.0", Bundles, true, true, "", out _) == Wire,
            "unknown Blueprinter signature schemes remain opaque");
        check(DiagnosticSignature.Select(Wire, Wire, "", Bundles, true, true, "", out _) == Wire,
            "bundle-looking text cannot supply a loader that is absent from actual loaded plugin metadata");
        check(DiagnosticSignature.Select(Wire, Named(Bundles), "2.0.1", Bundles, true, true, "", out warning) == Named(Bundles)
            && warning.Length == 0, "already verified expansion retains its normal diagnostics without fallback uncertainty");
        string verified = DiagnosticSignature.Select(Named(Bundles), Named(Bundles), "2.0.1", Bundles,
            false, false, "Blueprinter is still loading.", out warning, out bool wireVerified);
        check(verified == Named(Bundles) && wireVerified && warning.Contains("still loading"),
            "a verified expansion retains a loading-in-progress warning without downgrading its wire verification");
        check(Diagnostics.Compare(Named(Bundles), verified, wireVerified).Contains("compatibility strings match"),
            "a loading-only notice does not falsely report an otherwise verified wire as unverified");
        verified = DiagnosticSignature.Select(Named(Bundles), Named(Bundles), "2.0.1", Bundles,
            true, false, "Some content patches did not finish loading. Restart is required.", out warning, out wireVerified);
        check(verified == Named(Bundles) && wireVerified && warning.Contains("finished its loading routine")
            && warning.Contains("Some content patches did not finish loading"),
            "a verified expansion still exposes unresolved patches without treating a completed loading routine as unfinished");
        DiagnosticSignature.Select(Named(Bundles), Named(Bundles), "2.0.1", Bundles,
            true, false, "Blueprinter loading failed: native content error.", out warning, out wireVerified);
        check(wireVerified && warning.Contains("Blueprinter loading failed: native content error"),
            "verified named versions never suppress a recorded content-loading failure");
        DiagnosticSignature.Select(Wire, Wire, "2.0.1", Bundles, true, true, "", out warning, out wireVerified);
        check(!wireVerified && warning.Contains("unverified"),
            "successful version fallback remains explicitly separate from verified raw wire expansion");
        const string unresolvedAssets = "Blueprinter left 4 asset patches unresolved. Live reload is unavailable. "
            + "Restart may not fix missing assets; copy report for details.";
        check(DiagnosticSignature.FriendlyNotice("Blueprinter is still loading.")=="Mods are still loading. Please wait.","player-facing loading notice is concise");
        check(DiagnosticSignature.FriendlyNotice("The full compatibility string remains unverified. "+unresolvedAssets).Contains("Some mod assets could not load")
            &&DiagnosticSignature.FriendlyNotice(unresolvedAssets).Contains("restart may not fix")
            &&!DiagnosticSignature.FriendlyNotice(unresolvedAssets).Contains("patches"),"player notice summarizes unresolved assets while retaining honest restart guidance");
        string unresolvedSelected = DiagnosticSignature.Select(Wire, Wire, "2.0.1", Bundles,
            true, false, unresolvedAssets, out warning, out wireVerified);
        check(unresolvedSelected == Named(Bundles) && !wireVerified
            && warning.Contains("finished its loading routine") && warning.Contains("4 asset patches unresolved")
            && warning.Contains("live reload is unavailable") && warning.Contains("Restart may not fix missing assets")
            && !warning.Contains("Restart is required") && !warning.Contains("still loading"),
            "a finished routine with unresolved assets reports unavailable live reload without promising that restart repairs missing assets");
        var unresolvedView = JoinSummary.Create(Named(Bundles), unresolvedSelected,
            "0.34.1_2b9371678a89", Wire, "");
        check(unresolvedView.Status != JoinStatus.Match && unresolvedView.Matching.Count == 12
            && unresolvedView.Actions.Count == 1 && unresolvedView.Actions[0].Action == "CHECK FILES",
            "an honest unresolved-asset notice never promotes matching selected versions into a raw compatibility match");
        check(DiagnosticSignature.Select(Wire, Wire, "2.0.1", Bundles,
            false, false, unresolvedAssets, out warning, out wireVerified) == Wire && !wireVerified
            && warning.Contains("still loading") && !warning.Contains("finished its loading routine"),
            "unfinished loading still withholds selected-version diagnostics even when an earlier asset error is available");
        check(DiagnosticSignature.Select(Named(Bundles), Named(Bundles), "2.0.1", Bundles,
            true, false, unresolvedAssets, out warning, out wireVerified) == Named(Bundles) && wireVerified
            && warning.Contains("4 asset patches unresolved") && warning.Contains("live reload is unavailable"),
            "a verified compatibility expansion does not suppress a separate unresolved-asset live-reload warning");
        DiagnosticSignature.Select(Wire, Wire, "2.0.1", Bundles,
            true, false, "", out warning, out wireVerified);
        check(!wireVerified && warning.Contains("finished its loading routine")
            && warning.Contains("Copy report") && !warning.Contains("Restart is required"),
            "an unavailable live reload without asset details directs support capture rather than inventing a restart cure");
        var forged = Protocol.Encode(Wire, Wire);
        check(Protocol.Decode(k => forged.TryGetValue(k,out var value) ? value : "", Wire) == null,
            "host metadata cannot advertise the opaque hash-plus-suffix as a fabricated named mod list");

        var installed = actual.Mods.Where(m => m.Key != "com.nikkorap.blueprinter")
            .Select(m => new InstalledContent { Id=m.Key,Version=m.Value,Name=m.Key,Enabled=true }).ToArray();
        var plan = ServerSetupPlan.Create(Named(Bundles), Wire, installed, "2.0.1", true, false);
        check(plan.CanSetup && plan.DisabledContent.Length == 0,
            "server setup uses inspected exact local content and known game even when the local wire remains opaque");
        check(!ServerSetupPlan.Create(Named(Bundles), Wire, installed.Skip(1), "2.0.1", true, false).CanSetup,
            "allowing opaque local game metadata does not bypass a missing server-required bundle");
        check(!ServerSetupPlan.Create(Named(Bundles), Wire, installed, "2.0.0", true, false).CanSetup,
            "allowing opaque local game metadata does not bypass the exact installed loader requirement");
        check(!ServerSetupPlan.Create(Named(Bundles), Wire.Replace("0.34.1_", "0.34.2_"), installed, "2.0.1", true, false).CanSetup,
            "server setup still refuses a known local game version mismatch");
        check(!ServerSetupPlan.Create(Named(Bundles), "unknown_1daee97e5e09_rsl-v0.8.4-Default", installed, "2.0.1", true, false).CanSetup,
            "server setup cannot use an opaque local identifier with an unverifiable game version");
        check(!ServerSetupPlan.Create(Named(Bundles), new string('x',Protocol.MaxText+1), installed, "2.0.1", true, false).CanSetup,
            "oversized local wire metadata cannot contribute a supposedly known game");
        check(!ServerSetupPlan.Create("0.34.1_2b9371678a89", Wire, installed, "2.0.1", true, false).CanSetup,
            "the server's required list must still be complete and never inferred from either opaque hash");
        foreach(string malformed in new[]{Wire + "\n", "0.34.1_\0hash", "0.34.1_unknown", "0.34.1_",
            "0.34.1_com.nikkorap.blueprinter-v2.0.1_--broken", "0.34.1_1daee97e5e09_",
            "0.34.1_1daee97e5e09_other-v", "0.34.1_1daee97e5e09_other-v?1", "0.34.1_" + new string('a',64)})
            check(!ServerSetupPlan.Create(Named(Bundles), malformed, installed, "2.0.1", true, false).CanSetup,
                "server setup rejects malformed or unrecognised incomplete local compatibility data: " + malformed);
        check(ServerSetupPlan.Create(Named(Bundles), "0.34.1_1daee97e5e09", installed, "2.0.1", true, false).CanSetup,
            "the recognised bare Blueprinter hash frame still permits setup from inspected exact inventory");
        check(ServerSetupPlan.Create("0.34.1", "0.34.1", Array.Empty<InstalledContent>(), "", false, false).CanSetup,
            "valid game-only vanilla compatibility does not require an opaque prefix or Blueprinter metadata");
    }

    internal static void Contracts(AssemblyDefinition client, AssemblyDefinition server, Action<bool,string> check)
    {
        var compatibility = client.MainModule.GetType("KellysJOINCHECK.Compatibility");
        var expanded = compatibility.Methods.Single(m => m.Name == "Expanded" && m.Parameters.Count == 1);
        check(!expanded.Body.Instructions.Any(i => i.Operand is MethodReference m &&
            (m.Name == "StartsWith" || m.DeclaringType.Name == "DiagnosticSignature" || m.Name == "ForDiagnostics")),
            "verified host expansion keeps strict complete-wire equality with no suffix tolerance or diagnostics fallback");
        var describe = client.MainModule.GetType("KellysJOINCHECK.ClientPlugin").Methods.Single(m => m.Name == "Describe");
        check(describe.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "DiagnosticCompatibility" && m.Parameters.Count == 2)
            && describe.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "Compare" && m.Parameters.Count == 3),
            "fresh join descriptions and copied reports use the runtime diagnostic provider with explicit wire-verification state");
        check(!Types(server.MainModule.Types).Where(t => t.Name != "Compatibility").SelectMany(t => t.Methods).Where(m => m.HasBody)
            .SelectMany(m => m.Body.Instructions).Any(i => i.Operand is MethodReference m && m.Name == "ForDiagnostics"),
            "host advertisements never call the local runtime diagnostics projection");
    }
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots)
    {
        foreach(var type in roots)
        {
            yield return type;
            foreach(var child in Types(type.NestedTypes)) yield return child;
        }
    }
}
