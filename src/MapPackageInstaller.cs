using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace KellysJOINCHECK
{
    // Only the two reviewed multi-file packages receive special treatment.
    // Other ZIPs are accepted only when they contain one map and documentation.
    internal static class MapPackageInstaller
    {
        internal const long MaximumDownload=1024L*1024*1024;
        private const long MaximumExpanded=2L*1024*1024*1024;
        internal const string IronjawHash="83658f65e98bfa6ea43522f488d5c69b384dc66e6b94e44643b5e20234cc9e56";
        internal const string WakeHash="28a0c38fee313497f92938afdad31be7454dd25ee8bd7a126125a52f611c6cf6";
        private sealed class Item { internal string Source="",Root="",Relative=""; }
        private static bool Map(string name)=>name.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase);
        private static bool Raw(string name)=>Path.GetFileName(name).StartsWith("map_",StringComparison.OrdinalIgnoreCase)&&Path.GetExtension(name).Length==0;
        private static void VerifyRawBundle(string file)
        {
            byte[] signature=new byte[8];using(var stream=File.OpenRead(file))if(stream.Read(signature,0,signature.Length)!=signature.Length||!signature.SequenceEqual(new byte[]{85,110,105,116,121,70,83,0}))throw new InvalidDataException("This file is not a supported Unity map bundle.");
        }
        private static bool Docs(string name)=>name.StartsWith("readme",StringComparison.OrdinalIgnoreCase)||name.StartsWith("license",StringComparison.OrdinalIgnoreCase)||name.StartsWith("licence",StringComparison.OrdinalIgnoreCase);
        internal static bool NeedsMapInstall(string repo,string name)=>repo.Equals("Talamander/ironjaw-sound",StringComparison.OrdinalIgnoreCase)||repo.Equals("Kelly-D-Ripper/KELLYS-Wake-Island",StringComparison.OrdinalIgnoreCase)||Map(name)||Raw(name)||name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase);
        internal static bool IsMapArchive(string repo,string name,string path)
        {
            if(repo.Equals("Talamander/ironjaw-sound",StringComparison.OrdinalIgnoreCase)||repo.Equals("Kelly-D-Ripper/KELLYS-Wake-Island",StringComparison.OrdinalIgnoreCase)||Map(name)||Raw(name))return true;
            using(var stream=File.OpenRead(path))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))return zip.Entries.Any(e=>Map(e.FullName)||Raw(e.FullName));
        }
        internal static bool UsesNOMapLoader(string repo,string name,string path)
        {
            if(repo.Equals("Talamander/ironjaw-sound",StringComparison.OrdinalIgnoreCase)||Raw(name))return true;
            if(!name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))return false;
            using(var stream=File.OpenRead(path))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))return zip.Entries.Any(e=>Raw(e.FullName));
        }
        private static string LoaderRelative(string plugins,string? loaderDirectory,string name)
        {
            string root=Path.GetFullPath(plugins).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            string directory=Path.GetFullPath(loaderDirectory??root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(directory.Equals(root,StringComparison.OrdinalIgnoreCase))return name;
            if(!directory.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("NOMapLoader is outside the plugin folder. Install the map manually.");
            return Path.Combine(directory.Substring(root.Length+1),name);
        }
        internal static string Install(string repo,string name,string file,string digest,string cacheRoot,string pluginRoot,string missionRoot,string[] disabled,CancellationToken token,string? noMapLoaderDirectory=null)
        {
            if(!digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.HashValid(digest.Substring(7))||!ModUpdateFiles.Hash(file).Equals(digest.Substring(7),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The map download checksum does not match.");
            bool iron=repo.Equals("Talamander/ironjaw-sound",StringComparison.OrdinalIgnoreCase),wake=repo.Equals("Kelly-D-Ripper/KELLYS-Wake-Island",StringComparison.OrdinalIgnoreCase);
            if(iron&&!digest.Substring(7).Equals(IronjawHash,StringComparison.OrdinalIgnoreCase)||wake&&!digest.Substring(7).Equals(WakeHash,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("This new map release has not been checked yet. Install it manually.");
            if(disabled.Length>0&&iron&&disabled.Any(x=>x.Equals("NOMapLoader",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("NOMapLoader is disabled in NOMM. Enable it there first.");
            string stages=ModUpdateFiles.Within(cacheRoot,"DOORMAN-map-staging");Directory.CreateDirectory(stages);
            string stage=ModUpdateFiles.Within(stages,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            var written=new List<KeyValuePair<string,string>>();string? movedDirectory=null;
            try
            {
                var items=new List<Item>();var pluginIds=new HashSet<string>(StringComparer.Ordinal);
                if(Map(name)||Raw(name))
                {
                    string simple=Path.GetFileName(name);ModUpdateService.SafeArchivePath(simple);
                    items.Add(new Item {Source=file,Root=pluginRoot,Relative=Raw(name)?LoaderRelative(pluginRoot,noMapLoaderDirectory,simple):Path.Combine("NOCustomMaps","maps",simple)});
                }
                else
                {
                    using(var stream=File.OpenRead(file))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                    {
                        if(zip.Entries.Count>256)throw new InvalidDataException("This map package has too many files.");
                        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;int mapCount=0;
                        foreach(var entry in zip.Entries)
                        {
                            token.ThrowIfCancellationRequested();string key=entry.FullName;
                            if(key.EndsWith("/",StringComparison.Ordinal)){ModUpdateService.SafeArchivePath(key.TrimEnd('/'));continue;}
                            ModUpdateService.SafeArchivePath(key);if(!names.Add(key))throw new InvalidDataException("The package contains duplicate paths.");
                            int kind=entry.ExternalAttributes>>16&0xF000;if((kind!=0&&kind!=0x8000)||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("Linked archive files are not supported.");
                            if(entry.Length<0||entry.Length>MaximumDownload||(total+=entry.Length)>MaximumExpanded)throw new InvalidDataException("The map package is too large.");
                            string local=ModUpdateFiles.Within(stage,key.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                            using(var input=entry.Open())using(var output=new FileStream(local,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                            {byte[] buf=new byte[65536];long count=0;int n;while((n=input.Read(buf,0,buf.Length))>0){token.ThrowIfCancellationRequested();count+=n;if(count>entry.Length)throw new InvalidDataException("Unexpected archive size.");output.Write(buf,0,n);}if(count!=entry.Length)throw new InvalidDataException("Incomplete map archive.");output.Flush(true);}
                            if(iron) continue;
                            if(Map(key)||Raw(key)) { mapCount++;string simple=Path.GetFileName(key);items.Add(new Item {Source=local,Root=pluginRoot,Relative=Raw(key)?LoaderRelative(pluginRoot,noMapLoaderDirectory,simple):Path.Combine("NOCustomMaps","maps",simple)}); }
                            else if(wake&&key.StartsWith("plugins/",StringComparison.Ordinal)&&key.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))items.Add(new Item {Source=local,Root=pluginRoot,Relative=Path.Combine("KellysWakeIsland",Path.GetFileName(key))});
                            else if(wake&&key.StartsWith("Missions/",StringComparison.Ordinal)&&key.EndsWith(".json",StringComparison.OrdinalIgnoreCase))items.Add(new Item {Source=local,Root=missionRoot,Relative=key.Substring("Missions/".Length).Replace('/',Path.DirectorySeparatorChar)});
                            else if(!Docs(Path.GetFileName(key)))throw new InvalidDataException("This package has extra files that need a manual install.");
                        }
                        if(!iron&&mapCount!=1)throw new InvalidDataException("This package must contain exactly one map.");
                        if(iron&&(!names.Contains("map_ironjawsound")||!names.Contains("TerrainLabGameplay.dll")||!names.Contains("TerrainLabNOPatch.dll")||!names.Contains("TerrainLabRace.dll")||!names.Contains("TerrainLabScatter.dll")))throw new InvalidDataException("The Ironjaw package is incomplete.");
                    }
                }
                if(iron)
                {
                    foreach(string dll in new[]{"TerrainLabGameplay.dll","TerrainLabNOPatch.dll","TerrainLabRace.dll","TerrainLabScatter.dll"})foreach(string id in ModUpdateFiles.PluginIds(ModUpdateFiles.Within(stage,dll))) {if(!pluginIds.Add(id)||disabled.Contains(id,StringComparer.Ordinal))throw new InvalidDataException("An Ironjaw plugin is duplicated or disabled in NOMM.");ModUpdateFiles.EnsurePluginAbsent(pluginRoot,id);}
                    if(pluginIds.Count!=4)throw new InvalidDataException("Ironjaw plugin identities could not be verified.");
                    string target=ModUpdateFiles.Within(pluginRoot,"IronjawSound");if(Directory.Exists(target)||File.Exists(target))throw new IOException("Ironjaw Sound is already installed. Existing files were not changed.");
                    token.ThrowIfCancellationRequested();Directory.Move(stage,target);movedDirectory=target;
                    return "Ironjaw Sound installed. Restart to load it; check NOMapLoader is enabled.";
                }
                foreach(var item in items.Where(i=>Raw(i.Relative)))VerifyRawBundle(item.Source);
                foreach(var item in items.Where(i=>i.Relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)))
                {
                    var ids=ModUpdateFiles.PluginIds(item.Source);if(ids.Length!=1||!pluginIds.Add(ids[0])||disabled.Contains(ids[0],StringComparer.Ordinal))throw new InvalidDataException("A support plugin is duplicated or disabled in NOMM.");
                    ModUpdateFiles.VerifyInstallIdentity(item.Source,ids[0],"");ModUpdateFiles.EnsurePluginAbsent(pluginRoot,ids[0]);
                }
                if(items.GroupBy(i=>Path.GetFullPath(Path.Combine(i.Root,i.Relative)),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()!=1))throw new InvalidDataException("The package has conflicting destinations.");
                foreach(var item in items){string target=ModUpdateFiles.Within(item.Root,item.Relative);if(File.Exists(target)||Directory.Exists(target))throw new IOException("A map, mission or support plugin already exists. Existing files were not changed.");}
                foreach(var item in items)
                {
                    token.ThrowIfCancellationRequested();string target=ModUpdateFiles.Within(item.Root,item.Relative);Directory.CreateDirectory(Path.GetDirectoryName(target)!);target=ModUpdateFiles.Within(item.Root,item.Relative);
                    string expected=ModUpdateFiles.Hash(item.Source);
                    // The loader must never see a partly copied map, even if the
                    // game or PC closes unexpectedly. Rename only verified bytes.
                    string temporary=ModUpdateFiles.Within(item.Root,item.Relative+"."+Guid.NewGuid().ToString("N")+".doormantmp");
                    bool created=false;
                    try
                    {
                        using(var input=File.OpenRead(item.Source))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {created=true;byte[] buffer=new byte[65536];int count;while((count=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,count);}output.Flush(true);}
                        if(ModUpdateFiles.Hash(temporary)!=expected)throw new IOException("A map file did not copy correctly.");
                        token.ThrowIfCancellationRequested();File.Move(temporary,target);written.Add(new KeyValuePair<string,string>(target,expected));
                    }
                    finally{try{if(created&&File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
                }
                return "Map installed. Restart the game to load it.";
            }
            catch
            {
                bool leftovers=false;
                foreach(var item in written)try{if(File.Exists(item.Key)){if(ModUpdateFiles.Hash(item.Key)==item.Value)File.Delete(item.Key);else leftovers=true;}}catch(IOException){leftovers=true;}catch(UnauthorizedAccessException){leftovers=true;}
                if(movedDirectory!=null||leftovers)throw new InvalidOperationException("Installation stopped. Some newly created files could not be safely removed; check the map folder before retrying.");
                throw;
            }
            finally { try{if(Directory.Exists(stage))Directory.Delete(ModUpdateFiles.Within(stages,Path.GetFileName(stage)),true);}catch(IOException){}catch(UnauthorizedAccessException){} }
        }
    }
}
