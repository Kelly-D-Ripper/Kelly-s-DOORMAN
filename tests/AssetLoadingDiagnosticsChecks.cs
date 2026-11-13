using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KellysJOINCHECK;
using Mono.Cecil;
using Mono.Cecil.Cil;

// Trusted metadata fixtures only. No third-party plugin is executed.
namespace Blueprinter
{
    public sealed class AssetPatch { }
    public sealed class PatchManifest { public IList Patches=new ArrayList(); }
    public sealed class LoadedBundle { public PatchManifest Manifest=new PatchManifest(); }
    public sealed class PatchRunner { public IList DeferredPatches=new ArrayList(); }
}

internal static class AssetLoadingDiagnosticsRegression
{
    private static bool Reject(Action action)
    {try{action();return false;}catch(InvalidDataException){return true;}}

    internal static void Run(Action<bool,string> check,AssemblyDefinition client,AssemblyDefinition server)
    {
        check(AssetLoadingDiagnostics.ValidateCount(0)==0&&AssetLoadingDiagnostics.ValidateCount(8192)==8192,
            "asset warning count accepts complete bounded evidence without mod or graphics exceptions");
        foreach(int count in new[]{-1,8193,int.MaxValue})
            check(Reject(()=>AssetLoadingDiagnostics.ValidateCount(count)),"invalid pending asset count is rejected: "+count);
        check(AssetLoadingDiagnostics.Notice(0)==""&&AssetLoadingDiagnostics.Notice(4).Contains("successful asset application is not verified",StringComparison.Ordinal),
            "asset diagnostics explicitly avoid claiming successful asset application");
        var owner=new Blueprinter.LoadedBundle();var patch=new Blueprinter.AssetPatch();owner.Manifest.Patches.Add(patch);
        var runner=new Blueprinter.PatchRunner();runner.DeferredPatches.Add((owner,patch));
        check(AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})==1,
            "any pending selected asset can be disclosed independently of source or destination type");
        check(runner.DeferredPatches.Count==1&&owner.Manifest.Patches.Count==1,
            "capturing pending asset warnings never removes loader work");
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,Array.Empty<object>())),
            "an unresolved asset outside selected content cannot create a complete file claim");
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner,owner})),
            "duplicate selected content ownership is rejected");
        var other=new Blueprinter.LoadedBundle();other.Manifest.Patches.Add(patch);
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{other})),
            "equal-looking manifests cannot substitute for the actual selected owner");
        owner.Manifest.Patches.Add(patch);
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})),
            "ambiguous original manifest ownership remains unverified");
        owner.Manifest.Patches.RemoveAt(1);
        runner.DeferredPatches.Add((owner,patch));
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})),
            "duplicate deferred entries do not produce misleading diagnostics");
        runner.DeferredPatches.Clear();runner.DeferredPatches.Add((owner,new Blueprinter.AssetPatch()));
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})),
            "pending asset absent from selected manifest is rejected");
        runner.DeferredPatches.Clear();runner.DeferredPatches.Add(null);
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})),"null pending asset evidence is rejected");
        runner.DeferredPatches.Clear();runner.DeferredPatches.Add(Tuple.Create(owner,patch));
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})),"unsupported deferred tuple layout is rejected");
        runner.DeferredPatches.Clear();
        check(AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})==0,"empty pending queue has no warning");
        check(Reject(()=>AssetLoadingDiagnostics.CapturePendingCount(new object(),new[]{owner})),"unknown loader schema remains unverified");
        // Multiple new assets require no release-specific allowlist.
        for(int i=0;i<256;i++){var p=new Blueprinter.AssetPatch();owner.Manifest.Patches.Add(p);runner.DeferredPatches.Add((owner,p));}
        check(AssetLoadingDiagnostics.CapturePendingCount(runner,new[]{owner})==256,
            "generic diagnostics handle new pack assets without the previous four-patch limit");

        foreach(var assembly in new[]{client,server})Contracts(check,assembly);
    }

    private static void Contracts(Action<bool,string> check,AssemblyDefinition assembly)
    {
        var inventory=assembly.MainModule.GetType("KellysJOINCHECK.GameplayFingerprintInventory");
        var capture=inventory.Methods.Single(m=>m.Name=="Capture");
        var code=capture.Body.Instructions;
        bool Calls(string type,string name)=>code.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name==type&&m.Name==name);
        bool Field(string name)=>code.Any(i=>i.Operand is FieldReference f&&f.Name==name);
        bool Text(string text)=>code.Any(i=>i.OpCode==OpCodes.Ldstr&&(string)i.Operand==text);
        check(Field("loadingFinished")&&Field("loadingFailed")&&Field("hooksInstalled")&&Text("ScanSucceeded"),
            "completed source discovery and actual loader failures remain inventory gates: "+assembly.Name.Name);
        check(Calls("AssetLoadingDiagnostics","CapturePendingCount")&&!Calls("HeadlessPresentationPatches","Evaluate"),
            "pending assets become generic diagnostics rather than a per-aircraft completeness exception: "+assembly.Name.Name);
        check(!code.Any(i=>i.Operand is MethodReference m&&new[]{"get_isBatchMode","get_IsHeadless","get_graphicsDeviceType"}.Contains(m.Name)),
            "file inventory semantics do not depend on client or server graphics mode: "+assembly.Name.Name);
        check(assembly.MainModule.GetType("KellysJOINCHECK.HeadlessPresentationPatches")==null,
            "release-specific asset policy is absent from shipped assembly: "+assembly.Name.Name);
        var diagnostics=assembly.MainModule.GetType("KellysJOINCHECK.AssetLoadingDiagnostics");
        var methods=diagnostics.Methods.Where(m=>m.HasBody).ToArray();
        check(!methods.SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m&&new[]{"ApplyAllOps","LoadAsset","Clear","Remove","RemoveAt"}.Contains(m.Name)),
            "diagnostics neither patch assets nor remove Blueprinter deferred work: "+assembly.Name.Name);
        var refresh=inventory.NestedTypes.Single(t=>t.Name.StartsWith("<Refresh>",StringComparison.Ordinal));
        var worker=inventory.NestedTypes.SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
        check(worker.Any(i=>i.Operand is MethodReference m&&m.Name=="ReadFiles")
            &&worker.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name=="GameplayLoadedFileGuard"&&m.Name=="Apply"),
            "pending warnings still require full byte hashes and loaded-file consistency: "+assembly.Name.Name);
        check(refresh.Methods.Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name=="AssetLoadingDiagnostics"&&m.Name=="Notice"),
            "operator warning is emitted separately from successful exact file inventory: "+assembly.Name.Name);
    }
}
