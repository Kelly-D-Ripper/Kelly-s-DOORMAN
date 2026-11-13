using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace KellysJOINCHECK
{
    [DataContract] internal sealed class IronjawFile
    {
        [DataMember] public int Index;
        [DataMember] public string Relative="", OldHash="", NewHash="", Plugin="";
    }
    [DataContract] internal sealed class IronjawPlan
    {
        [DataMember] public int Schema=1;
        [DataMember] public string State="pending", ArchiveHash="", Version="";
        [DataMember] public IronjawFile[] Files=Array.Empty<IronjawFile>();
        internal string Job="";
    }

    // The publisher's Ironjaw release contains a raw map, four plugins and
    // data they load beside their DLLs. Never unpack it into the live tree.
    // Exact previous bytes are moved to this receipt's durable backup before
    // verified replacement bytes are published, and interrupted work resumes.
    internal static class IronjawPackageUpdates
    {
        internal const string DirectoryName="DOORMAN-ironjaw-updates";
        internal const string Repository="Talamander/ironjaw-sound";
        private const long MaximumExpanded=2L*1024*1024*1024;
        private static readonly Dictionary<string,string> Plugins=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"TerrainLabGameplay.dll","talamander.terrainlab.gameplay"}, {"TerrainLabNOPatch.dll","talamander.terrainlab.nopatch"},
            {"TerrainLabRace.dll","talamander.terrainlab.race"}, {"TerrainLabScatter.dll","talamander.terrainlab.scatter"} };
        private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        internal static void CheckQueueConflicts(string cache,string plugins,IEnumerable<PendingModUpdate> updates)
        {
            if(Read(cache,plugins,true).Length>0&&updates.Any(p=>Plugins.Values.Contains(p.Plugin,StringComparer.Ordinal)))
                throw new IOException("An Ironjaw plugin update is already queued. Finish that update first.");
        }
        private static string Hash(string path)=>File.Exists(path)?ModUpdateFiles.Hash(path):"";
        private static string Payload(IronjawPlan p,IronjawFile f)=>ModUpdateFiles.Within(p.Job,"new-"+f.Index+".bin");
        private static string Backup(IronjawPlan p,IronjawFile f)=>ModUpdateFiles.Within(p.Job,"old-"+f.Index+".bin");
        private static string Receipt(IronjawPlan p)=>ModUpdateFiles.Within(p.Job,"plan.json");
        private static bool OwnedDestination(string path)=>path.Replace('\\','/').StartsWith("IronjawSound/",StringComparison.OrdinalIgnoreCase);
        private static bool Legacy(string path,string plugin)
        {
            string leaf=Path.GetFileName(path.Replace('/',Path.DirectorySeparatorChar));
            return plugin.Length>0 ? Plugins.TryGetValue(leaf,out var id)&&id==plugin : Same(leaf,"map_ironjawsound");
        }
        private static void Validate(IronjawPlan p,string plugins)
        {
            if(p==null||p.Schema!=1||!ModUpdateFiles.HashValid(p.ArchiveHash)||ModReleaseVersion.Compare(p.Version,p.Version)==null||
                !new[]{"pending","applying","complete","cancelled"}.Contains(p.State)||p.Files==null||p.Files.Length<5||p.Files.Length>256||p.Files.Any(f=>f==null))
                throw new InvalidDataException("Invalid Ironjaw setup receipt.");
            if(p.Files.GroupBy(f=>f.Relative,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()!=1)||p.Files.Select(f=>f.Index).Distinct().Count()!=p.Files.Length)
                throw new InvalidDataException("Duplicate Ironjaw setup paths.");
            foreach(var f in p.Files)
            {
                if(f.Index<0||f.Index>=256||f.OldHash==null||f.NewHash==null||f.Plugin==null||
                   (f.OldHash.Length>0&&!ModUpdateFiles.HashValid(f.OldHash))||(f.NewHash.Length>0&&!ModUpdateFiles.HashValid(f.NewHash))||
                   (f.OldHash.Length==0&&f.NewHash.Length==0))throw new InvalidDataException("Invalid Ironjaw file receipt.");
                ModUpdateFiles.Within(plugins,f.Relative);
                if(!OwnedDestination(f.Relative)&&(f.NewHash.Length!=0||!Legacy(f.Relative,f.Plugin)))throw new InvalidDataException("Ironjaw setup names an unrelated file.");
                if(f.Plugin.Length>0&&(!Plugins.TryGetValue(Path.GetFileName(f.Relative),out var id)||id!=f.Plugin))throw new InvalidDataException("Invalid Ironjaw plugin identity.");
            }
        }
        internal static IronjawPlan[] Read(string cache,string plugins,bool pendingOnly)
        {
            string root=ModUpdateFiles.Within(cache,DirectoryName);if(!Directory.Exists(root))return Array.Empty<IronjawPlan>();
            var dirs=Directory.EnumerateDirectories(root).Where(d=>Guid.TryParseExact(Path.GetFileName(d),"N",out _)).Take(257).ToArray();
            if(dirs.Length>256)throw new InvalidDataException("Too many Ironjaw update receipts.");
            var result=new List<IronjawPlan>();
            foreach(string dir in dirs)
            {
                string job=ModUpdateFiles.Within(root,Path.GetFileName(dir)),path=ModUpdateFiles.Within(job,"plan.json");
                if(!File.Exists(path))continue;var p=UpdateJson.ReadFile<IronjawPlan>(path);p.Job=job;Validate(p,plugins);
                if(!pendingOnly||p.State=="pending"||p.State=="applying")result.Add(p);
            }
            if(pendingOnly&&result.Count>1)throw new InvalidDataException("An Ironjaw update is already waiting for restart.");
            return result.ToArray();
        }
        // Scan only on an explicit setup/restart, and never follow linked folders.
        private static Dictionary<string,string> Existing(string plugins)
        {
            var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);if(!Directory.Exists(plugins))return result;
            var pending=new Stack<string>();pending.Push(Path.GetFullPath(plugins));int dirs=0,files=0;
            string basis=Path.GetFullPath(plugins).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            while(pending.Count>0)
            {
                string dir=pending.Pop();if(++dirs>4096)throw new IOException("Too many plugin folders to check Ironjaw safely.");
                if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0)throw new IOException("A linked plugin folder needs manual review.");
                foreach(string file in Directory.EnumerateFiles(dir))
                {
                    if(++files>16384)throw new IOException("Too many plugin files to check Ironjaw safely.");
                    string leaf=Path.GetFileName(file),relative=Path.GetFullPath(file).Substring(basis.Length);ModUpdateFiles.Within(plugins,relative);
                    if(Same(leaf,"map_ironjawsound")){result.Add(relative,"");continue;}
                    if(!leaf.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))continue;
                    string[] ids;try{ids=ModUpdateFiles.PluginIds(file);}catch(BadImageFormatException){continue;}
                    var wanted=ids.Where(id=>Plugins.Values.Contains(id,StringComparer.Ordinal)).ToArray();
                    if(wanted.Length==0)continue;
                    if(wanted.Length!=1||ids.Length!=1||!Plugins.TryGetValue(leaf,out var expected)||expected!=wanted[0])
                        throw new IOException("An Ironjaw plugin has an unusual filename or shares a DLL. Existing files were kept.");
                    result.Add(relative,wanted[0]);
                }
                foreach(string child in Directory.EnumerateDirectories(dir))pending.Push(child);
            }
            return result;
        }
        internal static string Stage(string cache,string plugins,string archive,string archiveHash,string version,CancellationToken token)
        {
            if(!ModUpdateFiles.HashValid(archiveHash)||!Same(ModUpdateFiles.Hash(archive),archiveHash))throw new InvalidDataException("Ironjaw failed its download checksum.");
            if(ModReleaseVersion.Compare(version,version)==null)throw new InvalidDataException("The Ironjaw release version is invalid.");
            if(Read(cache,plugins,true).Length>0)throw new IOException("An Ironjaw setup is already waiting for restart.");
            string root=ModUpdateFiles.Within(cache,DirectoryName);Directory.CreateDirectory(root);
            string job=ModUpdateFiles.Within(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);bool committed=false;
            try
            {
                var rows=new List<IronjawFile>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long total=0;
                using(var stream=File.OpenRead(archive))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                {
                    if(zip.Entries.Count>256)throw new InvalidDataException("The Ironjaw package contains too many files.");
                    foreach(var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();string name=entry.FullName;
                        if(name.EndsWith("/",StringComparison.Ordinal)){ServerMapUpdates.SafeArchivePath(name.TrimEnd('/'));continue;}
                        ServerMapUpdates.SafeArchivePath(name);if(!seen.Add(name))throw new InvalidDataException("The Ironjaw package contains duplicate paths.");
                        int mode=entry.ExternalAttributes>>16&0xF000;if((mode!=0&&mode!=0x8000)||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("Linked package files are unsupported.");
                        if(entry.Length<0||entry.Length>ServerMapUpdates.MaximumMapBytes||(total+=entry.Length)>MaximumExpanded)throw new InvalidDataException("The Ironjaw package is too large.");
                        string leaf=Path.GetFileName(name);bool dll=leaf.EndsWith(".dll",StringComparison.OrdinalIgnoreCase);
                        if(dll&&(!Plugins.ContainsKey(leaf)||name.Contains("/")))throw new InvalidDataException("The Ironjaw package contains an unexpected plugin.");
                        string ext=Path.GetExtension(leaf);
                        string[] pathParts=name.Split('/');
                        bool missionPreview=pathParts.Length==3&&Same(pathParts[0],"Missions")&&Same(pathParts[2],"preview.jpg");
                        if(!dll&&!(missionPreview||ext.Length==0||new[]{".json",".txt",".md",".bin",".png",".tlmesh"}.Contains(ext,StringComparer.OrdinalIgnoreCase)))throw new InvalidDataException("The Ironjaw package contains an unsupported file: "+Diagnostics.Clean(leaf)+".");
                        var row=new IronjawFile {Index=rows.Count,Relative=Path.Combine("IronjawSound",name.Replace('/',Path.DirectorySeparatorChar)),Plugin=dll?Plugins[leaf]:""};
                        string payload=Payload(new IronjawPlan {Job=job},row);
                        using(var input=entry.Open())using(var output=new FileStream(payload,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {byte[] buffer=new byte[65536];long count=0;int n;while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();count+=n;if(count>entry.Length)throw new InvalidDataException("Ironjaw expanded beyond its declared size.");output.Write(buffer,0,n);}if(count!=entry.Length)throw new InvalidDataException("The Ironjaw package is incomplete.");output.Flush(true);}
                        row.NewHash=Hash(payload);
                        if(dll&&(!ModUpdateFiles.PluginIds(payload).SequenceEqual(new[]{row.Plugin},StringComparer.Ordinal)))throw new InvalidDataException("An Ironjaw plugin identity is incorrect.");
                        if(Same(name,"map_ironjawsound")) {byte[] magic=new byte[8];using(var input=File.OpenRead(payload))if(input.Read(magic,0,8)!=8||System.Text.Encoding.ASCII.GetString(magic)!="UnityFS\0")throw new InvalidDataException("The Ironjaw map bundle is invalid.");}
                        rows.Add(row);
                    }
                }
                if(!seen.Contains("map_ironjawsound")||!seen.Contains("IronjawSound.release.json")||Plugins.Keys.Any(k=>!seen.Contains(k)))throw new InvalidDataException("The Ironjaw release is incomplete.");
                foreach(string name in seen)
                {
                    string[] parts=name.Split('/');
                    for(int i=1;i<parts.Length;i++)if(seen.Contains(string.Join("/",parts.Take(i))))throw new InvalidDataException("The Ironjaw package has conflicting file and folder paths.");
                }
                foreach(var row in rows)
                {
                    string target=ModUpdateFiles.Within(plugins,row.Relative);if(Directory.Exists(target))throw new IOException("A folder occupies an Ironjaw file destination.");
                    row.OldHash=Hash(target);
                }
                foreach(var prior in Existing(plugins))
                {
                    if(rows.Any(r=>Same(r.Relative,prior.Key)))continue;
                    if(!Legacy(prior.Key,prior.Value))throw new IOException("An existing Ironjaw file is ambiguous. Existing files were kept.");
                    rows.Add(new IronjawFile {Index=rows.Count,Relative=prior.Key,OldHash=Hash(ModUpdateFiles.Within(plugins,prior.Key)),Plugin=prior.Value});
                }
                var plan=new IronjawPlan {Job=job,ArchiveHash=archiveHash,Version=version,Files=rows.ToArray()};Validate(plan,plugins);
                Verify(plan,plugins);token.ThrowIfCancellationRequested();UpdateJson.Save(Receipt(plan),plan,false,65000);committed=true;return job;
            }
            finally {if(!committed)try{if(Directory.Exists(job))Directory.Delete(job,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        internal static void VerifyPending(string cache,string plugins){foreach(var p in Read(cache,plugins,true))Verify(p,plugins);}
        private static void Verify(IronjawPlan p,string plugins)
        {
            Validate(p,plugins);
            foreach(var f in p.Files)
            {
                string target=ModUpdateFiles.Within(plugins,f.Relative),current=Hash(target),backup=Hash(Backup(p,f));
                if(f.NewHash.Length>0&&!Same(Hash(Payload(p,f)),f.NewHash))throw new IOException("The queued Ironjaw package changed.");
                bool original=Same(current,f.OldHash)&&backup.Length==0;
                bool moved=f.OldHash.Length>0&&Same(backup,f.OldHash)&&(current.Length==0||f.NewHash.Length>0&&Same(current,f.NewHash));
                bool installed=f.OldHash.Length==0&&p.State=="applying"&&Same(current,f.NewHash)&&backup.Length==0;
                if(!original&&!moved&&!installed)throw new IOException("An Ironjaw file changed after setup. Existing files were kept.");
                if(f.Plugin.Length>0&&f.OldHash.Length>0)
                {
                    string old=backup.Length>0?Backup(p,f):target;
                    if(!ModUpdateFiles.PluginIds(old).SequenceEqual(new[]{f.Plugin},StringComparer.Ordinal))throw new IOException("An existing Ironjaw plugin identity changed.");
                }
            }
            foreach(var observed in Existing(plugins))
                if(!p.Files.Any(f=>Same(f.Relative,observed.Key)))throw new IOException("Another Ironjaw copy appeared after setup. Existing files were kept.");
        }
        internal static void Cancel(string cache,string plugins,string job)
        {
            var p=Read(cache,plugins,true).SingleOrDefault(x=>Same(x.Job,job));if(p==null)return;
            if(p.State!="pending")throw new IOException("Ironjaw installation has already started.");
            p.State="cancelled";UpdateJson.Save(Receipt(p),p,false,65536);
        }
        internal static void Apply(string cache,string plugins,Action<string> log)
        {
            foreach(var p in Read(cache,plugins,true))ApplyOne(p,plugins,log);
        }
        private static void ApplyOne(IronjawPlan p,string plugins,Action<string> log)
        {
            Verify(p,plugins);p.State="applying";UpdateJson.Save(Receipt(p),p,false,65536);
            try
            {
                foreach(var f in p.Files.Where(f=>f.OldHash.Length>0))
                {
                    string target=ModUpdateFiles.Within(plugins,f.Relative),backup=Backup(p,f);
                    if(File.Exists(backup))continue;
                    if(!Same(Hash(target),f.OldHash))throw new IOException("An Ironjaw file changed during restart.");
                    File.Move(target,backup);if(!Same(Hash(backup),f.OldHash))throw new IOException("An Ironjaw backup failed verification.");
                }
                foreach(var f in p.Files.Where(f=>f.NewHash.Length>0))
                {
                    string target=ModUpdateFiles.Within(plugins,f.Relative);if(Same(Hash(target),f.NewHash))continue;
                    if(File.Exists(target)||Directory.Exists(target))throw new IOException("An Ironjaw destination became occupied.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);target=ModUpdateFiles.Within(plugins,f.Relative);
                    // Do not append a long suffix to a deeply nested mission name:
                    // Mono on Windows still encounters MAX_PATH. A unique file
                    // at the plugin root stays on the same volume for atomic rename.
                    string temp=ModUpdateFiles.Within(plugins,".doorman-"+Guid.NewGuid().ToString("N")+".tmp");bool created=false;
                    try{using(var input=File.OpenRead(Payload(p,f)))using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;input.CopyTo(output);output.Flush(true);}if(!Same(Hash(temp),f.NewHash))throw new IOException("An Ironjaw file copy failed verification.");File.Move(temp,target);}
                    finally{try{if(created&&File.Exists(temp))File.Delete(temp);}catch(IOException){}catch(UnauthorizedAccessException){}}
                }
                if(p.Files.Any(f=>f.NewHash.Length>0&&!Same(Hash(ModUpdateFiles.Within(plugins,f.Relative)),f.NewHash)))throw new IOException("The installed Ironjaw package failed verification.");
                p.State="complete";UpdateJson.Save(Receipt(p),p,false,65536);log("Installed Ironjaw Sound "+p.Version+"; previous files are retained in "+p.Job);
            }
            catch
            {
                foreach(var f in p.Files.Reverse())try
                {
                    string target=ModUpdateFiles.Within(plugins,f.Relative),backup=Backup(p,f);
                    if(f.NewHash.Length>0&&Same(Hash(target),f.NewHash)&&(f.OldHash.Length==0||File.Exists(backup)))File.Delete(target);
                    if(File.Exists(backup)&&!File.Exists(target)&&!Directory.Exists(target)&&Same(Hash(backup),f.OldHash))File.Move(backup,target);
                }catch(IOException){}catch(UnauthorizedAccessException){}
                throw;
            }
        }
    }
}
