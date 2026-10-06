using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private readonly string mapsDirectory=Path.Combine(Paths.PluginPath,"NOCustomMaps","maps");
        private MapFolderMonitor? mapMonitor;
        private readonly Dictionary<string,MapFileRecord> mapFileBaseline=new Dictionary<string,MapFileRecord>(StringComparer.OrdinalIgnoreCase);
        private MapEntry[] mapEntries=Array.Empty<MapEntry>();
        private int mapGeneration;
        private bool mapScanPending,mapRefreshPending;
        private float mapRefreshAfter;

        private void MapsChanged(bool open)
        {
            CloseMaps();
            if(!open) return;
            pageCheck?.Cancel();
            try { mapMonitor=new MapFolderMonitor(mapsDirectory,log); }
            catch(Exception ex) { log("Map monitoring unavailable: "+ex.Message); }
            RefreshMaps();
        }
        private void CloseMaps()
        {
            mapGeneration++;mapMonitor?.Dispose();mapMonitor=null;mapScanPending=false;mapRefreshPending=false;
        }
        private void TickMaps()
        {
            if(ui?.IsMapsOpen!=true) return;
            if(mapMonitor?.TakeChanged()==true) { mapRefreshPending=true;mapRefreshAfter=Time.unscaledTime+.4f; }
            if(mapRefreshPending&&!mapScanPending&&Time.unscaledTime>=mapRefreshAfter) RefreshMaps();
        }
        private void RefreshMaps()
        {
            if(ui?.IsMapsOpen!=true) return;
            if(mapScanPending) { mapRefreshPending=true;mapRefreshAfter=Time.unscaledTime+.4f;return; }
            mapMonitor?.Refresh();mapRefreshPending=false;mapScanPending=true;
            int generation=mapGeneration;
            var loaded=MapBundleBridge.ReadLoaded(out string message,out bool complete,out bool settled);
            // Enumerating file metadata never reads or hashes the map bundle.
            var task=Task.Run(()=>MapFiles.Read(mapsDirectory,log));
            owner.StartCoroutine(FinishMapScan(generation,loaded,message,complete,settled,task));
        }
        private IEnumerator FinishMapScan(int generation,MapEntry[] loaded,string message,bool complete,bool settled,Task<MapFileRecord[]> task)
        {
            while(!task.IsCompleted) yield return null;
            if(generation!=mapGeneration||ui?.IsMapsOpen!=true) yield break;
            mapScanPending=false;
            if(task.IsFaulted||task.IsCanceled) { ui.SetMaps(mapEntries,"Could not read the maps folder. Press Refresh to try again.");yield break; }
            var files=task.Result.ToDictionary(f=>f.FilePath,StringComparer.OrdinalIgnoreCase);
            var byPath=loaded.GroupBy(m=>m.FilePath,StringComparer.OrdinalIgnoreCase).Where(g=>g.Count()==1).ToDictionary(g=>g.Key,g=>g.Single(),StringComparer.OrdinalIgnoreCase);
            var rows=new List<MapEntry>();
            foreach(var file in files.Values)
            {
                if(!byPath.TryGetValue(file.FilePath,out var entry))
                {
                    entry=new MapEntry
                    {
                        FilePath=file.FilePath,Name=Path.GetFileNameWithoutExtension(file.FileName),
                        State=!complete?"Installed. Load state unavailable.":!settled?"Waiting for NOCustomMaps to finish loading.":"Not loaded. Restart required.",
                        Description=!complete?"Details are unavailable in the current map registry snapshot. The installed map file is still listed.":!settled?"Refresh after NOCustomMaps finishes loading to see the map details.":"Restart with NOCustomMaps enabled to load this map and read its embedded preview. If it still does not load, check the NOCustomMaps log."
                    };
                }
                else
                {
                    if(mapFileBaseline.TryGetValue(file.FilePath,out var baseline))
                    { if(file.Length!=baseline.Length||file.WriteTicks!=baseline.WriteTicks) entry.State="File changed. Restart required. Showing the loaded build."; }
                    else mapFileBaseline[file.FilePath]=file;
                    KeepMapPreview(entry);
                }
                rows.Add(entry);
            }
            // A borrowed map can remain loaded after its file is removed. Keep
            // that distinction visible instead of claiming it was unloaded.
            foreach(var entry in loaded.Where(m=>!files.ContainsKey(m.FilePath)))
            {
                bool sameFolder=string.Equals(Path.GetDirectoryName(entry.FilePath),mapsDirectory,StringComparison.OrdinalIgnoreCase);
                if(sameFolder) entry.State="File missing or unreadable. Restart required.";
                else entry.State+=" Installed in another maps folder.";
                KeepMapPreview(entry);rows.Add(entry);
            }
            mapEntries=rows.OrderBy(m=>m.Name,StringComparer.OrdinalIgnoreCase).ThenBy(m=>m.FilePath,StringComparer.Ordinal).Take(MapFiles.MaximumFiles).ToArray();
            string status=message.Length>0?message:mapEntries.Length==0?"No .nomap files found in BepInEx/plugins/NOCustomMaps/maps.":mapEntries.Length+" maps. Folder changes appear here; adding or replacing maps needs a restart.";
            ui.SetMaps(mapEntries,status);
        }
        private void KeepMapPreview(MapEntry entry)
        {
            var previous=mapEntries.FirstOrDefault(m=>string.Equals(m.FilePath,entry.FilePath,StringComparison.OrdinalIgnoreCase)&&m.Bundle==entry.Bundle);
            if(previous?.PreviewRead!=true||previous.PreviewLoading) return;
            entry.PreviewRead=true;entry.Description=previous.Description;entry.Links=previous.Links;
            entry.PreviewSprite=previous.PreviewSprite;entry.PreviewTexture=previous.PreviewTexture;
        }
        private void SelectMap(MapEntry entry)
        {
            if(entry.PreviewRead||entry.PreviewLoading||entry.Bundle==null) return;
            entry.PreviewLoading=true;
            owner.StartCoroutine(GuardMapPreview(entry,mapGeneration));
        }
        private bool MapPageCurrent(int generation,MapEntry entry)
            =>generation==mapGeneration&&ui?.IsMapsOpen==true&&mapEntries.Contains(entry);

        private IEnumerator GuardMapPreview(MapEntry entry,int generation)
        {
            var flow=ReadMapPreview(entry,generation);
            try
            {
                while(true)
                {
                    bool next;
                    try { next=flow.MoveNext(); }
                    catch(Exception ex) { log("Keeping map's own preview: "+ex.Message);break; }
                    if(!next) break;
                    yield return flow.Current;
                }
            }
            finally
            { entry.PreviewLoading=false;entry.PreviewRead=MapPageCurrent(generation,entry);(flow as IDisposable)?.Dispose(); }
        }

        private IEnumerator ReadMapPreview(MapEntry entry,int generation)
        {
            var bundle=entry.Bundle!;
            string[] names;
            try { names=bundle.GetAllAssetNames();if(names.Length>4096)throw new InvalidDataException("Too many preview asset names."); }
            catch(Exception ex) { log("Map preview unavailable: "+ex.Message);yield break; }
            string? json=MapPreviewManifest.ResolveAssetName(names,"doorman.json",".json");
            MapPreviewManifest? preview=null;
            if(json!=null)
            {
                AssetBundleRequest? request=null;
                try { request=bundle.LoadAssetAsync<TextAsset>(json); }
                catch(Exception ex) { log("Map preview unavailable: "+ex.Message); }
                if(request!=null)
                {
                    yield return request;
                    if(!MapPageCurrent(generation,entry)) { entry.PreviewRead=false;yield break; }
                    try
                    {
                        var asset=request.asset as TextAsset;
                        if(asset!=null) preview=MapPreviewManifest.Read(asset.bytes,entry.MapId);
                    }
                    catch(Exception ex) { log("Keeping map's own preview: "+ex.Message); }
                }
            }
            if(!MapPageCurrent(generation,entry)) { entry.PreviewRead=false;yield break; }
            if(preview!=null)
            {
                if(preview.Description.Length>0) entry.Description=preview.Description;
                entry.Links=preview.Links;
                string? image=MapPreviewManifest.ResolveAssetName(names,preview.Image,".png",".jpg",".jpeg");
                if(image!=null)
                {
                    AssetBundleRequest? request=null;
                    try { request=bundle.LoadAssetAsync<Texture2D>(image); }
                    catch(Exception ex) { log("Map preview image unavailable: "+ex.Message); }
                    if(request!=null)
                    {
                        yield return request;
                        if(!MapPageCurrent(generation,entry)) { entry.PreviewRead=false;yield break; }
                        var texture=request.asset as Texture2D;
                        if(MapBundleBridge.ImageAllowed(texture)) { entry.PreviewTexture=texture;entry.PreviewSprite=null; }
                    }
                }
            }
            // The normal chart is already loaded by registration. This fallback
            // reads only a Sprite, never the map root or any terrain assets.
            if(entry.PreviewTexture==null&&entry.PreviewSprite==null&&entry.DefaultImage.Length>0)
            {
                string? image=MapPreviewManifest.ResolveAssetName(names,entry.DefaultImage,".png",".jpg",".jpeg");
                AssetBundleRequest? request=null;
                try { if(image!=null) request=bundle.LoadAssetAsync<Sprite>(image); }
                catch(Exception ex) { log("Map chart unavailable: "+ex.Message); }
                if(request!=null)
                {
                    yield return request;
                    if(!MapPageCurrent(generation,entry)) { entry.PreviewRead=false;yield break; }
                    entry.PreviewSprite=request.asset as Sprite;
                }
            }
            if(MapPageCurrent(generation,entry)) ui!.RefreshMapPreview(entry);
        }
    }
}
