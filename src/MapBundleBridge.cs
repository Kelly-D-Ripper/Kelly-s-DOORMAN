using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed class MapEntry
    {
        internal string FilePath="",Name="",Author="",Version="",Description="",Credits="",Notice="",State="";
        internal Sprite? PreviewSprite;
        internal Texture2D? PreviewTexture;
        internal IReadOnlyList<ModLink> Links=Array.Empty<ModLink>();
        internal string MapId="",DefaultImage="";
        internal AssetBundle? Bundle;
        internal bool Registered,PreviewRead,PreviewLoading;
    }

    // Optional bridge, used on Unity's main thread while the Maps page is open.
    // The loader owns every bundle and image returned here. Reading its Maps
    // property would settle/hash maps synchronously, so read its inert fields.
    internal static class MapBundleBridge
    {
        internal static MapEntry[] ReadLoaded(out string message,out bool complete,out bool settled)
        {
            message="";complete=false;settled=true;
            var assembly=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name=="NOCustomMaps");
            if(assembly==null) { complete=true;message="NOCustomMaps is not loaded. Install or enable it, then restart to load maps.";return Array.Empty<MapEntry>(); }
            var result=new List<MapEntry>();
            try
            {
                var type=assembly.GetType("CustomMaps.BundleLoader");
                var field=type?.GetField("_opened",BindingFlags.Static|BindingFlags.NonPublic);
                if(!(field?.GetValue(null) is IEnumerable opened)) throw new InvalidOperationException("Map registry is unavailable.");
                settled=type!.GetField("_settled",BindingFlags.Static|BindingFlags.NonPublic)?.GetValue(null) is bool ready&&ready;
                var values=MapRegistrySnapshot.Take(opened,MapFiles.MaximumFiles,out bool registryComplete);
                foreach(var value in values)
                {
                    var manifest=Field(value,"Manifest");
                    string file=String(value,"File",1024);
                    if(manifest==null||string.IsNullOrEmpty(file)) continue;
                    string full=Path.GetFullPath(file);
                    var details=Field(value,"Details");
                    result.Add(new MapEntry
                    {
                        FilePath=full,Name=String(manifest,"DisplayName",256),MapId=String(manifest,"MapId",256),
                        Author=String(manifest,"Author",256),Version=String(manifest,"Version",128),
                        Credits=String(manifest,"Credits",8192),Notice=String(manifest,"Notice",8192),
                        DefaultImage=String(manifest,"MapImage",512),Bundle=Field(value,"Bundle") as AssetBundle,
                        PreviewSprite=Field(details,"MapImage") as Sprite,Registered=settled&&details!=null,
                        State=!settled?"NOCustomMaps is finishing map loading.":details==null?"Not registered. Check the NOCustomMaps log.":"Loaded for this session.",
                        Description="No extra description supplied. The map's own details and chart are shown."
                    });
                }
                complete=registryComplete;
                if(!complete) message="Map registry limit reached. Only the first "+MapFiles.MaximumFiles+" loaded maps were read; other installed files have an unknown load state.";
                else if(!settled) message="NOCustomMaps is finishing map loading. Refresh in a moment.";
            }
            catch { message="Map previews are unavailable for this NOCustomMaps version. Files are still listed."; }
            return result.ToArray();
        }

        private static object? Field(object? value,string name)
            =>value?.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(value);
        private static string String(object? value,string name,int limit)
            =>new string(((Field(value,name) as string)??"").Take(limit).Where(c=>!char.IsControl(c)||c=='\n'||c=='\t').ToArray());

        internal static bool ImageAllowed(Texture2D? image)
            =>image!=null&&image.width>0&&image.height>0&&image.width<=8192&&image.height<=8192&&(long)image.width*image.height<=16*1024*1024;
    }
}
