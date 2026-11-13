using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace KellysJOINCHECK
{
    // Read-only loader diagnostics. A pending asset does not change the bytes
    // of its selected source file and is never described as successfully applied.
    internal static class AssetLoadingDiagnostics
    {
        private sealed class References : IEqualityComparer<object>
        {
            internal static readonly References Instance=new References();
            public new bool Equals(object? a,object? b)=>ReferenceEquals(a,b);
            public int GetHashCode(object value)=>RuntimeHelpers.GetHashCode(value);
        }
        internal static int ValidateCount(int count)
        {
            if(count<0||count>GameplayFingerprintPolicy.MaxPendingAssetPatches)
                throw new InvalidDataException("The pending asset inventory exceeds its safe limit.");
            return count;
        }

        internal static int CapturePendingCount(object runner,IEnumerable selectedBundles)
        {
            if(runner==null||selectedBundles==null||runner.GetType().FullName!="Blueprinter.PatchRunner")
                throw new InvalidDataException("Blueprinter asset diagnostics are unavailable.");
            var assembly=runner.GetType().Assembly;
            var bundleType=assembly.GetType("Blueprinter.LoadedBundle",true)!;
            var patchType=assembly.GetType("Blueprinter.AssetPatch",true)!;
            var manifestType=assembly.GetType("Blueprinter.PatchManifest",true)!;
            var deferred=Field(runner,"DeferredPatches") as IList
                ??throw new InvalidDataException("Blueprinter pending asset inventory is unavailable.");
            int count=ValidateCount(deferred.Count);
            var selected=new HashSet<object>(References.Instance);
            foreach(var bundle in selectedBundles)
            {
                if(bundle==null||bundle.GetType()!=bundleType||selected.Count>=GameplayFingerprintPolicy.MaxRecords
                    ||!selected.Add(bundle))
                    throw new InvalidDataException("Selected content ownership is missing or ambiguous.");
            }
            var inspected=new HashSet<object>(References.Instance);
            var manifests=new Dictionary<object,Dictionary<object,int>>(References.Instance);
            foreach(var entry in deferred)
            {
                if(entry==null)throw new InvalidDataException("A pending asset entry is null.");
                var tuple=entry.GetType();
                if(!tuple.IsGenericType||tuple.GetGenericTypeDefinition()!=typeof(ValueTuple<,>))
                    throw new InvalidDataException("A pending asset has an unsupported tuple type.");
                var arguments=tuple.GetGenericArguments();
                if(arguments[0]!=bundleType||arguments[1]!=patchType)
                    throw new InvalidDataException("A pending asset has an unsupported owner type.");
                var bundle=Field(entry,"Item1");var patch=Field(entry,"Item2");
                if(bundle==null||patch==null||bundle.GetType()!=bundleType||patch.GetType()!=patchType
                    ||!selected.Contains(bundle)||!inspected.Add(patch))
                    throw new InvalidDataException("A pending asset has missing or ambiguous selected ownership.");
                if(!manifests.TryGetValue(bundle,out var ownership))
                {
                    var manifest=Field(bundle,"Manifest");
                    if(manifest==null||manifest.GetType()!=manifestType)
                        throw new InvalidDataException("Pending asset manifest ownership is unavailable.");
                    var patches=Field(manifest,"Patches") as IList
                        ??throw new InvalidDataException("Selected manifest patches are unavailable.");
                    ValidateCount(patches.Count);
                    ownership=new Dictionary<object,int>(References.Instance);
                    foreach(var candidate in patches)
                    {
                        if(candidate==null||candidate.GetType()!=patchType)
                            throw new InvalidDataException("Selected manifest patch identity is invalid.");
                        ownership.TryGetValue(candidate,out int occurrences);ownership[candidate]=occurrences+1;
                    }
                    manifests.Add(bundle,ownership);
                }
                if(!ownership.TryGetValue(patch,out int owners)||owners!=1)
                    throw new InvalidDataException("Pending asset manifest ownership is ambiguous.");
            }
            return count;
        }

        internal static string Notice(int count)
            =>ValidateCount(count)==0?"":"Asset loading warning: "+count+" selected asset patches remain pending. Exact source files were compared; successful asset application is not verified.";

        private static object? Field(object value,string name)
            =>value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(value);
    }
}
