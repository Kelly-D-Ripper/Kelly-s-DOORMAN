using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace KellysJOINCHECK
{
    // A registry snapshot looks one item beyond the display cap so reaching the
    // cap exactly is not confused with omitting already-loaded maps.
    internal static class MapRegistrySnapshot
    {
        internal static object?[] Take(IEnumerable registry,int maximum,out bool complete)
        {
            if(registry==null)throw new ArgumentNullException(nameof(registry));
            if(maximum<1||maximum>MapFiles.MaximumFiles)throw new ArgumentOutOfRangeException(nameof(maximum));
            complete=false;
            var values=new List<object?>();
            var iterator=registry.GetEnumerator();
            try
            {
                bool more=true;
                while(values.Count<maximum&&(more=iterator.MoveNext()))values.Add(iterator.Current);
                complete=!more||!iterator.MoveNext();
                return values.ToArray();
            }
            finally { (iterator as IDisposable)?.Dispose(); }
        }
    }

    internal sealed class MapFileRecord
    {
        internal string FilePath="",FileName="";
        internal long Length,WriteTicks;
    }

    internal static class MapFiles
    {
        internal const int MaximumFiles=256;

        internal static MapFileRecord[] Read(string directory,Action<string>? log=null)
        {
            var result=new List<MapFileRecord>();
            try
            {
                string root=Path.GetFullPath(directory);
                CheckLinks(root);
                if (!Directory.Exists(root)) return result.ToArray();
                // Case-independent filtering also works in the pure tests on non-Windows hosts.
                var files=Directory.EnumerateFiles(root,"*",SearchOption.TopDirectoryOnly)
                    .Where(IsMapFile).Take(MaximumFiles+1).ToArray();
                if (files.Length>MaximumFiles) log?.Invoke("Map file limit reached; only the first "+MaximumFiles+" files were read.");
                foreach (string file in files.Take(MaximumFiles))
                {
                    try
                    {
                        string full=Path.GetFullPath(file);
                        CheckLinks(full);
                        var info=new FileInfo(full);
                        if ((info.Attributes&FileAttributes.Temporary)!=0) continue;
                        // Opening tests read permission; no bundle data is read or hashed.
                        using (var stream=new FileStream(full,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                        {
                            if (stream.Length==0) { log?.Invoke("Skipped empty map file '"+info.Name+"'.");continue; }
                            result.Add(new MapFileRecord { FilePath=full,FileName=info.Name,Length=stream.Length,WriteTicks=info.LastWriteTimeUtc.Ticks });
                        }
                    }
                    catch (Exception ex) when (FileFailure(ex)) { log?.Invoke("Skipped map file '"+Path.GetFileName(file)+"' ("+ex.GetType().Name+")."); }
                }
            }
            catch (Exception ex) when (FileFailure(ex)) { log?.Invoke("Map folder is unavailable ("+ex.GetType().Name+")."); }
            return result.OrderBy(f=>f.FileName,StringComparer.OrdinalIgnoreCase).ThenBy(f=>f.FilePath,StringComparer.Ordinal).ToArray();
        }

        private static bool IsMapFile(string path)
        {
            string name=Path.GetFileName(path);
            return name.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase) && !name.StartsWith(".",StringComparison.Ordinal) && !name.StartsWith("~",StringComparison.Ordinal)
                && !new[]{".tmp.nomap",".partial.nomap",".download.nomap"}.Any(suffix=>name.EndsWith(suffix,StringComparison.OrdinalIgnoreCase));
        }

        internal static void CheckLinks(string value)
        {
            for (string? path=Path.GetFullPath(value);path!=null;path=Path.GetDirectoryName(path))
            {
                try { if ((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Linked map files and folders are unsupported."); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static bool FileFailure(Exception ex) => ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is System.Security.SecurityException;
    }

    // Exists only while the map view is open. Call Refresh after a reported change
    // so a newly created/deleted maps folder swaps between the folder and its parent.
    internal sealed class MapFolderMonitor:IDisposable
    {
        private readonly string directory;
        private readonly Action<string>? log;
        private readonly object gate=new object();
        private FileSystemWatcher? watcher;
        private string watched="";
        private int dirty,disposed;
        internal string DirectoryPath => directory;
        internal string WatchedDirectory { get { lock (gate) return watched; } }

        internal MapFolderMonitor(string directory,Action<string>? log=null)
        {
            this.directory=Path.GetFullPath(directory);
            this.log=log;
            Refresh();
        }

        internal bool TakeChanged() => Volatile.Read(ref disposed)==0 && Interlocked.Exchange(ref dirty,0)!=0;

        internal void Refresh()
        {
            lock (gate)
            {
                if (disposed!=0) return;
                try
                {
                    MapFiles.CheckLinks(directory);
                    string selected="",selectedFilter="*";
                    if (Directory.Exists(directory)) selected=directory;
                    else
                    {
                        string? parent=Path.GetDirectoryName(directory);
                        // Do not watch the entire plugins tree if NOCustomMaps itself is absent.
                        if (parent!=null && Path.GetFileName(parent).Equals("NOCustomMaps",StringComparison.OrdinalIgnoreCase) && Directory.Exists(parent))
                        { selected=parent;selectedFilter=Path.GetFileName(directory); }
                    }
                    // Explicit refresh also rebinds after a folder was replaced at the same path,
                    // or an OS watcher error invalidated its old native handle.
                    Stop();
                    if (selected.Length==0) return;
                    var next=new FileSystemWatcher(selected,selectedFilter)
                    {
                        IncludeSubdirectories=false,
                        NotifyFilter=NotifyFilters.FileName|NotifyFilters.DirectoryName|NotifyFilters.LastWrite|NotifyFilters.Size|NotifyFilters.CreationTime
                    };
                    next.Created+=Changed;next.Changed+=Changed;next.Deleted+=Changed;next.Renamed+=Renamed;next.Error+=Error;
                    watcher=next;watched=selected;
                    next.EnableRaisingEvents=true;
                }
                catch (Exception ex) when (MapFiles.FileFailure(ex)) { Stop();log?.Invoke("Map folder monitoring is unavailable ("+ex.GetType().Name+"). Refresh the map view to check files."); }
            }
        }

        // OS callbacks do not log, scan files, invoke application code, or touch Unity.
        private void Changed(object sender,FileSystemEventArgs args) { Interlocked.Exchange(ref dirty,1); }
        private void Renamed(object sender,RenamedEventArgs args) { Interlocked.Exchange(ref dirty,1); }
        private void Error(object sender,ErrorEventArgs args) { Interlocked.Exchange(ref dirty,1); }
        private void Stop()
        {
            var old=watcher;watcher=null;watched="";
            if (old==null) return;
            old.EnableRaisingEvents=false;
            old.Created-=Changed;old.Changed-=Changed;old.Deleted-=Changed;old.Renamed-=Renamed;old.Error-=Error;
            old.Dispose();
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (Interlocked.Exchange(ref disposed,1)!=0) return;
                Stop();Interlocked.Exchange(ref dirty,0);
            }
        }
    }

    [DataContract]
    internal sealed class MapPreviewManifest
    {
        internal const int MaximumBytes=65536,MaximumDescription=8192;
        [DataMember(Name="map",IsRequired=true)] public string Map="";
        [DataMember(Name="description")] public string Description="";
        [DataMember(Name="image")] public string Image="";
        [DataMember(Name="links")] public ModLink[] Links=Array.Empty<ModLink>();

        [OnDeserializing]
        private void Deserializing(StreamingContext context) { Description="";Image="";Links=Array.Empty<ModLink>(); }

        internal static MapPreviewManifest Read(byte[] utf8,string expectedMapId)
        {
            if (utf8==null || utf8.Length==0 || utf8.Length>MaximumBytes) throw new InvalidDataException("The optional map preview is empty or too large.");
            try
            {
                // Reject malformed UTF-8 rather than replacing invalid identity bytes silently.
                new UTF8Encoding(false,true).GetString(utf8);
                using (var stream=new MemoryStream(utf8,false))
                {
                    var serializer=new DataContractJsonSerializer(typeof(MapPreviewManifest),new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=128 });
                    var preview=(MapPreviewManifest?)serializer.ReadObject(stream) ?? throw new InvalidDataException("The optional map preview is empty.");
                    preview.Validate(expectedMapId);
                    return preview;
                }
            }
            catch (Exception ex) when (ex is SerializationException || ex is System.Xml.XmlException || ex is DecoderFallbackException)
            { throw new InvalidDataException("The optional map preview contains invalid JSON or UTF-8."); }
        }

        internal void Validate(string expectedMapId)
        {
            if (string.IsNullOrWhiteSpace(expectedMapId) || expectedMapId.Length>256 || Map!=expectedMapId || Map.Any(char.IsControl)) throw new InvalidDataException("The optional preview does not match this map's identifier.");
            if (Description==null || Description.Length>MaximumDescription || Description.Any(c=>char.IsControl(c)&&c!='\n'&&c!='\r'&&c!='\t')) throw new InvalidDataException("The map preview description is invalid or too long.");
            if (Image==null || Image.Length>512 || Image.Length>0 && !SafeAssetName(Image)) throw new InvalidDataException("The map preview image must name an asset inside this bundle.");
            if (Links==null || Links.Length>8 || Links.Any(l=>l==null)) throw new InvalidDataException("The map preview has invalid or too many links.");
            // Keep readable valid links, following the existing mod-preview link policy.
            Links=Links.Where(l=>l.Label!=null && l.Label.Trim().Length>0 && l.Label.Length<=80 && !l.Label.Any(char.IsControl) && ModManifest.SafeLink(l.Url))
                .Select(l=>new ModLink { Label=l.Label.Trim(),Url=l.Url }).ToArray();
        }

        private static bool SafeAssetName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length<=512 && value==value.Trim() && !value.Any(c=>char.IsControl(c)||c==':')
            && !value.StartsWith("/",StringComparison.Ordinal) && !value.StartsWith("\\",StringComparison.Ordinal)
            && !value.Split('/','\\').Any(part=>part.Length==0||part=="."||part=="..");

        internal static string? ResolveAssetName(IEnumerable<string> names,string requested,params string[] allowedExtensions)
        {
            if (names==null || !SafeAssetName(requested) || allowedExtensions==null || allowedExtensions.Length==0) return null;
            var extensions=allowedExtensions.Where(e=>!string.IsNullOrWhiteSpace(e)&&e.TrimStart('.').All(char.IsLetterOrDigit))
                .Select(e=>"."+e.TrimStart('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (extensions.Length==0) return null;
            var assets=names.Where(n=>n!=null && SafeAssetName(n)).Select(n=>n.Replace('\\','/'))
                .Where(n=>extensions.Any(e=>n.EndsWith(e,StringComparison.OrdinalIgnoreCase))).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string query=requested.Replace('\\','/');
            var exact=assets.Where(n=>n.Equals(query,StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (exact.Length==1) return exact[0];
            if (exact.Length>1) return null;
            string leaf=Leaf(query);
            var matches=assets.Where(n=>Leaf(n).Equals(leaf,StringComparison.OrdinalIgnoreCase) || WithoutExtension(Leaf(n)).Equals(leaf,StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            return matches.Length==1 ? matches[0] : null;
        }

        private static string Leaf(string value) { int slash=value.LastIndexOf('/');return slash<0 ? value : value.Substring(slash+1); }
        private static string WithoutExtension(string value) { int dot=value.LastIndexOf('.');return dot<0 ? value : value.Substring(0,dot); }
    }
}
