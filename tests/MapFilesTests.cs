using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using KellysJOINCHECK;

internal static class MapFilesRegression
{
    internal static void Run(Action<bool,string> check)
    {
        ManifestChecks(check);
        RegistrySnapshotChecks(check);
        DiscoveryAndMonitorChecks(check);
    }

    private static void Invalid(Action<bool,string> check,Action action,string message)
    {
        bool invalid=false;
        try { action(); }
        catch (InvalidDataException) { invalid=true; }
        check(invalid,message);
    }
    private static MapPreviewManifest Read(string json,string id="wake-island") => MapPreviewManifest.Read(Encoding.UTF8.GetBytes(json),id);

    private static void RegistrySnapshotChecks(Action<bool,string> check)
    {
        var empty=MapRegistrySnapshot.Take(Array.Empty<object>(),MapFiles.MaximumFiles,out bool complete);
        check(complete&&empty.Length==0,"an empty loaded-map registry is a complete snapshot");
        var shortRegistry=new object[]{"first","second"};
        var shortRead=MapRegistrySnapshot.Take(shortRegistry,MapFiles.MaximumFiles,out complete);
        check(complete&&shortRead.SequenceEqual(shortRegistry),"a registry below the cap retains its entries and complete state");
        var exact=Enumerable.Range(0,MapFiles.MaximumFiles).Cast<object>().ToArray();
        var exactRead=MapRegistrySnapshot.Take(exact,MapFiles.MaximumFiles,out complete);
        check(complete&&exactRead.SequenceEqual(exact),"a registry containing exactly the displayed limit remains complete");
        var overflow=exact.Concat(new object[]{"extra loaded map"}).ToArray();
        var capped=MapRegistrySnapshot.Take(overflow,MapFiles.MaximumFiles,out complete);
        check(!complete&&capped.SequenceEqual(exact),"a capped registry retains its prefix but never claims omitted loaded maps are unloaded");

        int reads=0;bool disposed=false;
        IEnumerable<object> Endless()
        {
            try {while(true){reads++;yield return reads;}}
            finally {disposed=true;}
        }
        capped=MapRegistrySnapshot.Take(Endless(),MapFiles.MaximumFiles,out complete);
        check(!complete&&capped.Length==MapFiles.MaximumFiles&&reads==MapFiles.MaximumFiles+1&&disposed,"even an endless registry is bounded to one lookahead and disposes its enumerator");
        bool rejected=false;
        try {MapRegistrySnapshot.Take(shortRegistry,MapFiles.MaximumFiles+1,out complete);}
        catch(ArgumentOutOfRangeException){rejected=true;}
        check(rejected,"registry snapshots reject a bound above the maximum display limit");
    }

    private static void ManifestChecks(Action<bool,string> check)
    {
        var minimum=Read("{\"map\":\"wake-island\"}");
        check(minimum.Map=="wake-island" && minimum.Description=="" && minimum.Image=="" && minimum.Links.Length==0,"optional map previews default absent description, image and links safely");
        var complete=Read("{\"map\":\"wake-island\",\"description\":\"Line one\\nLine two\",\"image\":\"assets/map/doorman-preview.png\",\"links\":[{\"label\":\" GitHub \",\"url\":\"https://github.com/owner/repo\"},{\"label\":\"Website\",\"url\":\"http://example.com/map\"}]}");
        check(complete.Description.Contains('\n') && complete.Image=="assets/map/doorman-preview.png" && complete.Links.Length==2 && complete.Links[0].Label=="GitHub","optional map descriptions, internal image names and HTTP/HTTPS links parse without external file reads");
        Invalid(check,()=>Read("{broken"),"malformed optional map JSON is rejected at the validation boundary");
        Invalid(check,()=>Read("{}"),"optional map previews must explicitly bind their map identifier");
        Invalid(check,()=>Read("{\"map\":\"another-map\"}"),"one map's optional preview cannot describe another installed map");
        Invalid(check,()=>Read("{\"map\":\"WAKE-ISLAND\"}"),"map identity binding is exact rather than case-folded");
        Invalid(check,()=>MapPreviewManifest.Read(new byte[]{255,254,253},"wake-island"),"invalid UTF-8 is rejected rather than changing identity text");
        Invalid(check,()=>MapPreviewManifest.Read(new byte[MapPreviewManifest.MaximumBytes+1],"wake-island"),"optional map JSON has a byte limit before deserialization");
        Invalid(check,()=>new MapPreviewManifest { Map="wake-island",Description=new string('x',MapPreviewManifest.MaximumDescription+1) }.Validate("wake-island"),"optional map descriptions have a character limit");
        Invalid(check,()=>new MapPreviewManifest { Map="wake-island",Description="bad\u0000text" }.Validate("wake-island"),"optional descriptions reject non-text control characters");
        foreach (string image in new[]{"/root/preview.png","C:\\maps\\preview.png","\\\\host\\preview.png","../preview.png","assets/../preview.png","assets\\..\\preview.png","assets//preview.png","https://example.com/preview.png","file:///preview.png","assets/./preview.png"})
            Invalid(check,()=>new MapPreviewManifest { Map="wake-island",Image=image }.Validate("wake-island"),"optional image references cannot escape their bundle: "+image);
        var badLinks=new MapPreviewManifest
        {
            Map="wake-island",Links=new[]
            {
                new ModLink { Label="Script",Url="javascript:alert(1)" },new ModLink { Label="File",Url="file:///C:/private" },
                new ModLink { Label="Credentials",Url="https://user:password@example.com" },new ModLink { Label="Relative",Url="page.html" },
                new ModLink { Label="bad\nlabel",Url="https://example.com" },new ModLink { Label="Valid",Url="https://example.com" }
            }
        };
        badLinks.Validate("wake-island");
        check(badLinks.Links.Length==1 && badLinks.Links[0].Label=="Valid","optional map links retain only readable labels and validated HTTP/HTTPS URLs");
        Invalid(check,()=>new MapPreviewManifest { Map="wake-island",Links=Enumerable.Range(0,9).Select(i=>new ModLink { Label="Link",Url="https://example.com" }).ToArray() }.Validate("wake-island"),"raw optional link counts are bounded before filtering");

        var names=new[]{"assets/a/map.json","assets/a/doorman.json","assets/a/poster.png","assets/b/poster.png","assets/a/photo.jpg","assets/a/terrain.prefab"};
        check(MapPreviewManifest.ResolveAssetName(names,"ASSETS/A/POSTER.PNG",".png")=="assets/a/poster.png","exact full image asset paths win even when filenames are duplicated");
        check(MapPreviewManifest.ResolveAssetName(names,"photo.jpg",".png",".jpg")=="assets/a/photo.jpg" && MapPreviewManifest.ResolveAssetName(names,"photo",".png",".jpg")=="assets/a/photo.jpg","unique filename and extensionless asset requests resolve to full names");
        check(MapPreviewManifest.ResolveAssetName(names,"doorman",".json")=="assets/a/doorman.json","the embedded optional TextAsset is found without LoadAllAssets");
        check(MapPreviewManifest.ResolveAssetName(names,"poster",".png")==null && MapPreviewManifest.ResolveAssetName(names,"poster.png",".png")==null,"ambiguous image filenames and basenames never select an arbitrary asset");
        check(MapPreviewManifest.ResolveAssetName(names,"photo.png",".png",".jpg")==null,"an explicit image extension is not silently replaced with another format");
        check(MapPreviewManifest.ResolveAssetName(names,"terrain",".png",".jpg")==null && MapPreviewManifest.ResolveAssetName(names,"../photo.jpg",".jpg")==null,"asset resolution enforces allowed types and safe internal names");
    }

    private static void DiscoveryAndMonitorChecks(Action<bool,string> check)
    {
        string temp=Path.Combine(Path.GetTempPath(),"doorman-map-files-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string parent=Path.Combine(temp,"NOCustomMaps"),directory=Path.Combine(parent,"maps");
            var logs=new List<string>();
            check(MapFiles.Read(directory,logs.Add).Length==0 && !Directory.Exists(directory),"map discovery does not create an absent directory");
            Directory.CreateDirectory(parent);
            using (var awaiting=new MapFolderMonitor(directory,logs.Add))
            {
                check(awaiting.WatchedDirectory==parent && !Directory.Exists(directory),"an absent maps folder watches only its existing NOCustomMaps parent");
                Directory.CreateDirectory(directory);
                check(SpinWait.SpinUntil(awaiting.TakeChanged,TimeSpan.FromSeconds(3)),"creation of the maps folder sets the monitor's dirty flag");
                awaiting.Refresh();
                check(awaiting.WatchedDirectory==directory,"explicit refresh rebinds the watcher to a newly created maps folder");
            }
            var unbounded=new MapFolderMonitor(Path.Combine(temp,"missing","maps"),logs.Add);
            check(unbounded.WatchedDirectory=="","an absent NOCustomMaps folder does not start watching the broader workspace");
            unbounded.Dispose();

            string a=Path.Combine(directory,"Alpha.NOMAP"),b=Path.Combine(directory,"bravo.nomap");
            File.WriteAllText(a,"UnityFS-alpha");File.WriteAllText(b,"UnityFS-bravo");
            foreach (string ignored in new[]{"map.nomap.json","map.nomap.tmp","map.tmp.nomap","map.partial.nomap","map.download.nomap",".hidden.nomap","~working.nomap","readme.txt"}) File.WriteAllText(Path.Combine(directory,ignored),"ignored");
            Directory.CreateDirectory(Path.Combine(directory,"nested"));File.WriteAllText(Path.Combine(directory,"nested","hidden.nomap"),"nested");
            File.WriteAllBytes(Path.Combine(directory,"empty.nomap"),Array.Empty<byte>());
            var files=MapFiles.Read(directory,logs.Add);
            check(files.Select(f=>f.FileName).SequenceEqual(new[]{"Alpha.NOMAP","bravo.nomap"}),"map discovery accepts either extension case and ignores nested, temporary, sidecar and empty files");
            check(files.All(f=>Path.IsPathRooted(f.FilePath) && f.FilePath==Path.GetFullPath(f.FilePath) && f.Length>0 && f.WriteTicks>0),"discovery returns canonical full paths and file metadata without hashing bundles");
            long oldLength=files[0].Length,oldTicks=files[0].WriteTicks;
            File.AppendAllText(a,"-changed");File.SetLastWriteTimeUtc(a,DateTime.UtcNow.AddSeconds(2));
            var changed=MapFiles.Read(directory,logs.Add).Single(f=>f.FilePath==a);
            check(changed.Length>oldLength && changed.WriteTicks!=oldTicks,"changed map metadata is visible on the next explicit discovery");
            File.Delete(b);File.WriteAllText(Path.Combine(directory,"charlie.nomap"),"UnityFS-charlie");
            check(MapFiles.Read(directory,logs.Add).Select(f=>f.FileName).SequenceEqual(new[]{"Alpha.NOMAP","charlie.nomap"}),"removed and added map files change the next explicit discovery");
            using (var locked=new FileStream(a,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                check(!MapFiles.Read(directory,logs.Add).Any(f=>f.FilePath==a),"unreadable locked map files are skipped without failing the entire folder");

            using (var monitor=new MapFolderMonitor(directory,logs.Add))
            {
                check(monitor.WatchedDirectory==directory && !monitor.TakeChanged(),"opening the map view starts a directory-only watcher with a cheap clean flag");
                File.AppendAllText(a,"-watch");
                check(SpinWait.SpinUntil(monitor.TakeChanged,TimeSpan.FromSeconds(3)),"a changed map file signals refresh without a polling worker");
                monitor.Dispose();File.AppendAllText(a,"-after-close");
                check(!monitor.TakeChanged(),"closing the view disposes the watcher and suppresses later change flags");
            }

            string many=Path.Combine(temp,"many");Directory.CreateDirectory(many);
            for (int i=0;i<MapFiles.MaximumFiles+1;i++) File.WriteAllText(Path.Combine(many,"map-"+i+".nomap"),"UnityFS");
            int before=logs.Count;
            check(MapFiles.Read(many,logs.Add).Length==MapFiles.MaximumFiles && logs.Count==before+1,"map discovery limits candidate files and reports a folder above the limit");
        }
        finally
        {
            string expected=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(temp).StartsWith(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Map test cleanup escaped the temporary directory.");
            Directory.Delete(temp,true);
        }
    }
}
