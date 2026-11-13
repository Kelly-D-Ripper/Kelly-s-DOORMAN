using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Threading;

namespace KellysJOINCHECK
{
    // Wake's public ZIPs predate map-key release metadata. These fingerprints
    // bind each published archive to the full map digest, not just its short key.
    internal sealed class KnownWakeRelease
    {
        internal string Key="", MapHash="", ArchiveHash="", Url="", SupportHash="", SupportVersion="";
        internal static readonly KnownWakeRelease[] Published={
            new KnownWakeRelease { Key="cm.kellys-wake-island.eeaed7cf", MapHash="eeaed7cf6464b22bc8f5b93984efa89cc58847dbcd6751dcdffd5ee8acdc9eb9", ArchiveHash="a54dbcf08a56bf1147bbdddbf86f941a686e8875fe28f8d5b0b43971b17e61ea", SupportHash="e6a358fd9347ce3ac74ecfa56f1e9075112d108a16b94d73aad53df7d6410976",SupportVersion="0.1.1", Url="https://github.com/Kelly-D-Ripper/KELLYS-Wake-Island/releases/download/v0.3.9/Kellys-Wake-Island-0.3.9.zip" },
            new KnownWakeRelease { Key="cm.kellys-wake-island.82d17a35", MapHash="82d17a3589c7a35500a0a09d074b131d7f433f2d14ab977e0c1295a00b5b2283", ArchiveHash="f416f198664c77310397fb54e24f8a81ae7f75b32b2eee41fc2200012495b1ab", SupportHash="40652dad8f454a67e0b4438b855057a07d9ba17a7da452dabe29bd929d41fc75",SupportVersion="0.1.2", Url="https://github.com/Kelly-D-Ripper/KELLYS-Wake-Island/releases/download/v0.3.11/Kellys-Wake-Island-0.3.11.zip" },
            new KnownWakeRelease { Key="cm.kellys-wake-island.dc9ed557", MapHash="dc9ed557a635863e8c828a0649bc0269d0b35e7bfdd016d93bf92bafbd1eb154", ArchiveHash="28a0c38fee313497f92938afdad31be7454dd25ee8bd7a126125a52f611c6cf6", SupportHash="179038efab9be15f98716fd7523de722d1159f897c7a6e94c22f0eb60caaf1b8",SupportVersion="0.1.3", Url="https://github.com/Kelly-D-Ripper/KELLYS-Wake-Island/releases/download/v0.3.13/Kellys-Wake-Island-0.3.13.zip" }
        };
        internal static KnownWakeRelease? Find(string key)=>Published.SingleOrDefault(r=>r.Key==key);
    }
    [DataContract] internal sealed class ServerMapOldFile
    {
        [DataMember] public string Relative = "", Hash = "", Key = "";
    }
    [DataContract] internal sealed class ServerMapPlan
    {
        [DataMember] public int Schema = 1;
        [DataMember] public string MapKey = "", Target = "", NewHash = "", State = "pending";
        [DataMember] public ServerMapOldFile[] Old = Array.Empty<ServerMapOldFile>();
        internal string Job = "";
    }

    // A map is never removed or replaced during gameplay. Each saved receipt
    // names exact old bytes; startup moves them to its durable backup before
    // placing verified new bytes. A partial move is resumable and reversible.
    internal static class ServerMapUpdates
    {
        internal const string DirectoryName = "DOORMAN-map-updates";
        internal const long MaximumMapBytes = 4L*1024*1024*1024;
        private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        private static string Maps(string plugins)=>ModUpdateFiles.Within(plugins,Path.Combine("NOCustomMaps","maps"));
        private static string FileInMaps(string plugins,string relative)
        {
            string root=Maps(plugins), full=ModUpdateFiles.Within(plugins,relative);
            if(!string.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase)
                || !(full.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase)||full.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The map is outside the standard NOCustomMaps map folder.");
            return full;
        }
        private static string OldMapFile(string plugins,string relative)
        {
            string full=ModUpdateFiles.Within(plugins,relative);
            if(!(full.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase)||full.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("The previous map file has an unsupported format.");
            return full;
        }
        internal static string Relative(string plugins,string path)
        {
            string root=Path.GetFullPath(plugins).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(path);if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The existing map is outside the plugin folder.");
            string relative=full.Substring(root.Length);OldMapFile(plugins,relative);return relative;
        }
        // When the loader is disabled, it cannot report its registered maps.
        // Recognise only conventional files for the requested ID and keep their
        // actual build identities so they remain recoverable after a switch.
        internal static ServerMapOldFile[] DiscoverConventional(string plugins,string mapId,CancellationToken token)
        {
            if(!MapJoinDiagnostic.TryCustomKey("cm."+mapId+".00000000",out _,out _))throw new InvalidDataException("Invalid map identity.");
            string dir=Maps(plugins);if(!Directory.Exists(dir))return Array.Empty<ServerMapOldFile>();
            var paths=Directory.EnumerateFiles(dir).Where(p=>Path.GetFileName(p).StartsWith(mapId+"-",StringComparison.OrdinalIgnoreCase)&&
                (p.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase)||p.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase))).Take(33).ToArray();
            if(paths.Length>32)throw new IOException("Too many copies of this map to switch safely.");
            var result=new List<ServerMapOldFile>();
            foreach(string path in paths)
            {
                token.ThrowIfCancellationRequested();string relative=Relative(plugins,path),hash=ModUpdateFiles.Hash(path),build;
                if(path.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase))
                {
                    var bytes=new byte[28];using(var stream=File.OpenRead(path))if(stream.Read(bytes,0,bytes.Length)!=bytes.Length)throw new InvalidDataException("An installed map is incomplete.");
                    if(bytes[0]!=78||bytes[1]!=79||bytes[2]!=77||bytes[3]!=90||bytes[4]!=1||bytes[5]!=0||bytes[6]!=1||bytes[7]!=0)throw new InvalidDataException("An installed map has an unsupported format.");
                    build=BitConverter.ToString(bytes,24,4).Replace("-","").ToLowerInvariant();
                }
                else build=hash.Substring(0,8);
                result.Add(new ServerMapOldFile {Relative=relative,Hash=hash,Key="cm."+mapId+"."+build});
            }
            return result.ToArray();
        }
        private static string Backup(ServerMapPlan plan,int index)=>ModUpdateFiles.Within(plan.Job,"old-"+index+".bin");
        private static string Payload(ServerMapPlan plan)=>ModUpdateFiles.Within(plan.Job,"payload.bin");
        private static string Receipt(ServerMapPlan plan)=>ModUpdateFiles.Within(plan.Job,"plan.json");
        private static string Current(string path)=>File.Exists(path)?ModUpdateFiles.Hash(path):"";
        internal static string SupportHash(string path)=>Current(path);
        private static void Validate(ServerMapPlan p,string plugins)
        {
            if(p==null||p.Schema!=1||!MapJoinDiagnostic.TryCustomKey(p.MapKey,out _,out _)||!ModUpdateFiles.HashValid(p.NewHash)
                || !new[]{"pending","applying","complete","cancelled"}.Contains(p.State)||p.Old==null||p.Old.Length>32)
                throw new InvalidDataException("Invalid queued map update.");
            FileInMaps(plugins,p.Target);MapJoinDiagnostic.TryCustomKey(p.MapKey,out var requestedId,out _);
            foreach(var old in p.Old)
            {
                if(old==null||!ModUpdateFiles.HashValid(old.Hash)||old.Key==null||old.Key.Length>96)throw new InvalidDataException("Invalid map backup receipt.");
                string oldPath=OldMapFile(plugins,old.Relative);
                // The game's registered map can live in NOMM's add-on folders.
                // Require the same verified map identity outside the standard folder.
                if(!string.Equals(Path.GetDirectoryName(oldPath),Maps(plugins),StringComparison.OrdinalIgnoreCase)&&
                    (!MapJoinDiagnostic.TryCustomKey(old.Key,out var oldId,out _)||oldId!=requestedId))throw new InvalidDataException("The prior map outside the standard folder is not identified.");
            }
            if(p.Old.GroupBy(x=>x.Relative,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()!=1))throw new InvalidDataException("Duplicate old map paths.");
        }
        internal static ServerMapPlan[] Read(string cache,string plugins,bool pendingOnly)
        {
            string root=ModUpdateFiles.Within(cache,DirectoryName);if(!Directory.Exists(root))return Array.Empty<ServerMapPlan>();
            var dirs=Directory.EnumerateDirectories(root).Where(d=>Guid.TryParseExact(Path.GetFileName(d),"N",out _)).Take(257).ToArray();
            if(dirs.Length>256)throw new InvalidDataException("Too many map update receipts.");
            var result=new List<ServerMapPlan>();
            foreach(string dir in dirs)
            {
                string job=ModUpdateFiles.Within(root,Path.GetFileName(dir)),receipt=ModUpdateFiles.Within(job,"plan.json");
                if(!File.Exists(receipt))continue;
                var plan=UpdateJson.ReadFile<ServerMapPlan>(receipt);plan.Job=job;Validate(plan,plugins);
                if(!pendingOnly || plan.State=="pending" || plan.State=="applying")result.Add(plan);
            }
            if(pendingOnly&&(result.Count>1||result.GroupBy(p=>p.Target,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)))throw new InvalidDataException("Resolve the existing queued map before adding another.");
            return result.ToArray();
        }
        private static void CheckMapFile(string file,string requestedKey)
        {
            if(!MapJoinDiagnostic.TryCustomKey(requestedKey,out _,out var wanted))throw new InvalidDataException("The server map identity is unavailable.");
            if(file.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase))
            {
                var header=new byte[88];using(var input=File.OpenRead(file))if(input.Read(header,0,header.Length)!=header.Length)throw new InvalidDataException("The map archive is incomplete.");
                if(header[0]!=78||header[1]!=79||header[2]!=77||header[3]!=90||header[4]!=1||header[5]!=0||header[6]!=1||header[7]!=0)
                    throw new InvalidDataException("This map format is unsupported.");
                string identity=BitConverter.ToString(header,24,4).Replace("-","").ToLowerInvariant();
                if(identity!=wanted)throw new InvalidDataException("The downloaded map is not the server's requested build.");
            }
            else if(file.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase))
            { if(!ModUpdateFiles.Hash(file).StartsWith(wanted,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The downloaded map is not the server's requested build."); }
            else throw new InvalidDataException("This map format is unsupported.");
        }
        internal static string Stage(string cache,string plugins,string archive,string assetName,string archiveHash,string key,IEnumerable<ServerMapOldFile> old,CancellationToken token,KnownWakeRelease? discoveredWake=null)
        {
            if(!ModUpdateFiles.HashValid(archiveHash)||!Same(ModUpdateFiles.Hash(archive),archiveHash))throw new InvalidDataException("The map download failed its checksum.");
            var wake=KnownWakeRelease.Find(key)??discoveredWake;
            if(discoveredWake!=null&&(discoveredWake.Key!=key||!GitHubRepository.DownloadUrl(discoveredWake.Url,"Kelly-D-Ripper/KELLYS-Wake-Island")||!ModUpdateFiles.HashValid(discoveredWake.MapHash)))throw new InvalidDataException("The discovered Wake release is invalid.");
            bool knownWake=wake!=null&&Same(wake.ArchiveHash,archiveHash);
            if(Read(cache,plugins,true).Length>0)throw new InvalidOperationException("A map update is already waiting for restart.");
            string root=ModUpdateFiles.Within(cache,DirectoryName);Directory.CreateDirectory(root);
            string job=ModUpdateFiles.Within(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
            bool committed=false;
            try
            {
                string extracted;string leaf;
                if(assetName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
                {
                    using(var stream=File.OpenRead(archive))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                    {
                        if(zip.Entries.Count>256)throw new InvalidDataException("This map package contains too many files.");
                        var mapEntries=new List<ZipArchiveEntry>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach(var entry in zip.Entries)
                        {
                            string path=entry.FullName;
                            if(path.EndsWith("/",StringComparison.Ordinal)){SafeArchivePath(path.TrimEnd('/'));continue;}
                            SafeArchivePath(path);
                            if(!names.Add(path))throw new InvalidDataException("The map archive has duplicate entries.");
                            int mode=entry.ExternalAttributes>>16&0xF000;if((mode!=0&&mode!=0x8000)||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("Linked map archive entries are unsupported.");
                            string name=Path.GetFileName(path.Replace('/',Path.DirectorySeparatorChar));
                            if(name.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase))mapEntries.Add(entry);
                            else if(!knownWake&&!(name.StartsWith("readme",StringComparison.OrdinalIgnoreCase)||name.StartsWith("license",StringComparison.OrdinalIgnoreCase)||name.StartsWith("licence",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("This map package contains extra files and needs manual installation.");
                        }
                        if(mapEntries.Count!=1||mapEntries[0].Length<=0||mapEntries[0].Length>MaximumMapBytes)throw new InvalidDataException("This package needs exactly one supported map.");
                        var selectedEntry=mapEntries[0];leaf=Path.GetFileName(selectedEntry.FullName.Replace('/',Path.DirectorySeparatorChar));
                        extracted=ModUpdateFiles.Within(job,"payload"+Path.GetExtension(leaf));
                        using(var input=selectedEntry.Open())using(var output=new FileStream(extracted,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {var buffer=new byte[65536];long total=0;int n;while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();total+=n;if(total>selectedEntry.Length)throw new InvalidDataException("The map expanded beyond its declared size.");output.Write(buffer,0,n);}if(total!=selectedEntry.Length)throw new InvalidDataException("The map archive is incomplete.");output.Flush(true);}
                    }
                }
                else
                {
                    leaf=Path.GetFileName(assetName);if(leaf!=assetName)throw new InvalidDataException("Invalid map filename.");
                    extracted=ModUpdateFiles.Within(job,"payload"+Path.GetExtension(leaf));
                    using(var input=File.OpenRead(archive))using(var output=new FileStream(extracted,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {var buffer=new byte[65536];int n;while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,n);}output.Flush(true);}
                }
                SafeArchivePath(leaf);CheckMapFile(extracted,key);
                if(knownWake&&!Same(ModUpdateFiles.Hash(extracted),wake!.MapHash))throw new InvalidDataException("The Wake map failed its full checksum.");
                token.ThrowIfCancellationRequested();
                var oldFiles=(old??Array.Empty<ServerMapOldFile>()).Take(33).ToArray();
                if(oldFiles.Length>32)throw new InvalidDataException("Too many installed copies of this map.");
                foreach(var item in oldFiles)
                {
                    string path=OldMapFile(plugins,item.Relative);
                    if(!File.Exists(path)||Directory.Exists(path))throw new IOException("An installed map changed while preparing the update.");
                    item.Hash=Current(path);
                }
                var plan=new ServerMapPlan {Job=job,MapKey=key,Target=Relative(plugins,Path.Combine(Maps(plugins),leaf)),NewHash=Current(extracted),Old=oldFiles};
                Validate(plan,plugins);
                string target=FileInMaps(plugins,plan.Target);
                if((File.Exists(target)||Directory.Exists(target))&&!oldFiles.Any(o=>Same(o.Relative,plan.Target)))throw new IOException("A different file already occupies the map destination. Existing files were kept.");
                File.Move(extracted,Payload(plan));
                token.ThrowIfCancellationRequested();UpdateJson.Save(Receipt(plan),plan);committed=true;return job;
            }
            finally {if(!committed)try{if(Directory.Exists(job))Directory.Delete(job,true);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        internal static string StageSaved(string cache,string plugins,string key,IEnumerable<ServerMapOldFile> current,CancellationToken token)
        {
            var matches=new List<Tuple<string,string,string>>();
            foreach(var plan in Read(cache,plugins,false).Where(p=>p.State=="complete"))
                for(int i=0;i<plan.Old.Length;i++)
                    if(plan.Old[i].Key==key&&File.Exists(Backup(plan,i)))matches.Add(Tuple.Create(Backup(plan,i),plan.Old[i].Hash,Path.GetFileName(plan.Old[i].Relative)));
            if(matches.Count==0)return "";
            if(matches.Select(m=>m.Item2).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).Count()!=1)throw new IOException("More than one saved map has this short identity. Check the map manually.");
            var selected=matches[0];
            return Stage(cache,plugins,selected.Item1,selected.Item3,selected.Item2,key,current,token);
        }
        internal static void StageWakeSupport(string cache,string plugins,string archive,KnownWakeRelease release,string supportPath,CancellationToken token)
        {
            if(!Same(ModUpdateFiles.Hash(archive),release.ArchiveHash))throw new InvalidDataException("The Wake archive failed verification.");
            string folder=ModUpdateFiles.Within(cache,"DOORMAN-updates");Directory.CreateDirectory(folder);
            string temporary=ModUpdateFiles.Within(folder,Guid.NewGuid().ToString("N")+".wake-support.tmp");
            try
            {
                using(var stream=File.OpenRead(archive))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                {
                    var entries=zip.Entries.Where(e=>e.FullName=="plugins/KellysWakeFlightSupport.dll").Take(2).ToArray();
                    if(entries.Length!=1||entries[0].Length<=0||entries[0].Length>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("Wake Flight Support is missing from the release.");
                    using(var input=entries[0].Open())using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {byte[] buffer=new byte[65536];long count=0;int n;while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();count+=n;if(count>entries[0].Length)throw new InvalidDataException("The Wake plugin expanded beyond its declared size.");output.Write(buffer,0,n);}if(count!=entries[0].Length)throw new InvalidDataException("Wake Flight Support is incomplete.");output.Flush(true);}
                }
                if(!Same(ModUpdateFiles.Hash(temporary),release.SupportHash))throw new InvalidDataException("Wake Flight Support failed verification.");
                string root=Path.Combine(cache,"DOORMAN-updates"),id="kellys.wake.flight-support";
                if(supportPath.Length==0)ModUpdateFiles.StageInstall(root,plugins,temporary,id,"Kelly-D-Ripper/KELLYS-Wake-Island",release.SupportVersion,null,token);
                else ModUpdateFiles.Stage(root,plugins,supportPath,temporary,id,"Kelly-D-Ripper/KELLYS-Wake-Island",release.SupportVersion,null,"",true);
            }
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        // New Wake releases need no extra manifest. The published archive is
        // checksum-verified, and its actual map is streamed to match the exact
        // map key reported by the game before either map or plugin is queued.
        internal static KnownWakeRelease InspectWakeRelease(string cache,string archive,string archiveHash,string key,string url,CancellationToken token)
        {
            if(!MapJoinDiagnostic.TryCustomKey(key,out var id,out var build)||id!="kellys-wake-island"||
                !GitHubRepository.DownloadUrl(url,"Kelly-D-Ripper/KELLYS-Wake-Island")||!ModUpdateFiles.HashValid(archiveHash)||!Same(ModUpdateFiles.Hash(archive),archiveHash))
                throw new InvalidDataException("The Wake download could not be verified.");
            string folder=ModUpdateFiles.Within(cache,DirectoryName);Directory.CreateDirectory(folder);
            string temporary=ModUpdateFiles.Within(folder,Guid.NewGuid().ToString("N")+".support.tmp");
            try
            {
                using(var stream=File.OpenRead(archive))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                {
                    if(zip.Entries.Count>256)throw new InvalidDataException("The Wake release contains too many files.");
                    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);ZipArchiveEntry? map=null,support=null;
                    foreach(var entry in zip.Entries)
                    {
                        string name=entry.FullName;if(name.EndsWith("/",StringComparison.Ordinal)){SafeArchivePath(name.TrimEnd('/'));continue;}
                        SafeArchivePath(name);if(!names.Add(name))throw new InvalidDataException("The Wake release has duplicate paths.");
                        int kind=entry.ExternalAttributes>>16&0xF000;if((kind!=0&&kind!=0x8000)||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("Linked Wake files are unsupported.");
                        string leaf=Path.GetFileName(name);
                        if(name.StartsWith("maps/kellys-wake-island-",StringComparison.OrdinalIgnoreCase)&&(name.EndsWith(".nomap",StringComparison.OrdinalIgnoreCase)||name.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase)))
                        {if(map!=null)throw new InvalidDataException("The Wake release has multiple maps.");map=entry;}
                        else if(name.Equals("plugins/KellysWakeFlightSupport.dll",StringComparison.OrdinalIgnoreCase))support=entry;
                        else if(!(name.StartsWith("Missions/",StringComparison.OrdinalIgnoreCase)&&name.EndsWith(".json",StringComparison.OrdinalIgnoreCase))&&
                            !(leaf.StartsWith("readme",StringComparison.OrdinalIgnoreCase)||leaf.StartsWith("license",StringComparison.OrdinalIgnoreCase)||leaf.StartsWith("licence",StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidDataException("This Wake release has extra files that need review.");
                    }
                    if(map==null||support==null||map.Length<=0||map.Length>MaximumMapBytes||support.Length<=0||support.Length>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("The Wake release is incomplete.");
                    string mapHash;byte[] header=new byte[88];int captured=0;
                    using(var input=map.Open())using(var sha=SHA256.Create())
                    {
                        byte[] buffer=new byte[65536];long count=0;int n;
                        while((n=input.Read(buffer,0,buffer.Length))>0)
                        {token.ThrowIfCancellationRequested();count+=n;if(count>map.Length)throw new InvalidDataException("The Wake map expanded beyond its declared size.");int take=Math.Min(n,header.Length-captured);if(take>0){Array.Copy(buffer,0,header,captured,take);captured+=take;}sha.TransformBlock(buffer,0,n,buffer,0);}
                        if(count!=map.Length)throw new InvalidDataException("The Wake map is incomplete.");sha.TransformFinalBlock(Array.Empty<byte>(),0,0);mapHash=BitConverter.ToString(sha.Hash!).Replace("-","").ToLowerInvariant();
                    }
                    if(map.FullName.EndsWith(".nomapz",StringComparison.OrdinalIgnoreCase))
                    {
                        if(captured<88||header[0]!=78||header[1]!=79||header[2]!=77||header[3]!=90||header[4]!=1||header[5]!=0||header[6]!=1||header[7]!=0||BitConverter.ToString(header,24,4).Replace("-","").ToLowerInvariant()!=build)
                            throw new InvalidDataException("The latest Wake release does not match the server's map build.");
                    }
                    else if(!mapHash.StartsWith(build,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The latest Wake release does not match the server's map build.");
                    using(var input=support.Open())using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {byte[] buffer=new byte[65536];long count=0;int n;while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();count+=n;if(count>support.Length)throw new InvalidDataException("The Wake plugin expanded beyond its declared size.");output.Write(buffer,0,n);}if(count!=support.Length)throw new InvalidDataException("Wake Flight Support is incomplete.");output.Flush(true);}
                    string version=ModUpdateFiles.VerifyInstallIdentity(temporary,"kellys.wake.flight-support","");
                    return new KnownWakeRelease {Key=key,MapHash=mapHash,ArchiveHash=archiveHash,Url=url,SupportHash=ModUpdateFiles.Hash(temporary),SupportVersion=version};
                }
            }
            finally{try{if(File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
        internal static void Cancel(string cache,string plugins,string job)
        {
            var plan=Read(cache,plugins,true).SingleOrDefault(p=>Same(p.Job,job));
            if(plan==null)return;
            if(plan.State!="pending")throw new IOException("The map update has already started; its backup was retained.");
            plan.State="cancelled";UpdateJson.Save(Receipt(plan),plan);
        }
        internal static void Apply(string cache,string plugins,Action<string> log)
        {
            foreach(var plan in Read(cache,plugins,true))ApplyOne(plan,plugins,log);
        }
        internal static void VerifyPending(string cache,string plugins)
        {foreach(var plan in Read(cache,plugins,true))Verify(plan,plugins);}
        private static void Verify(ServerMapPlan p,string plugins)
        {
            Validate(p,plugins);if(!Same(Current(Payload(p)),p.NewHash))throw new IOException("The queued map failed verification. Existing maps were kept.");
            string target=FileInMaps(plugins,p.Target);bool targetIsOld=p.Old.Any(x=>Same(x.Relative,p.Target));
            for(int i=0;i<p.Old.Length;i++)
            {
                string original=OldMapFile(plugins,p.Old[i].Relative),backup=Backup(p,i),originalHash=Current(original),backupHash=Current(backup);
                bool alreadyNew=Same(original,target)&&Same(originalHash,p.NewHash)&&Same(backupHash,p.Old[i].Hash);
                if(!alreadyNew && !(Same(originalHash,p.Old[i].Hash)&&backupHash.Length==0) && !(originalHash.Length==0&&Same(backupHash,p.Old[i].Hash)))
                    throw new IOException("An installed map changed after setup. No files were replaced.");
            }
            if(!targetIsOld&&File.Exists(target)&&(p.State!="applying"||!Same(Current(target),p.NewHash)))throw new IOException("The map destination is occupied. Existing files were kept.");
        }
        private static void ApplyOne(ServerMapPlan p,string plugins,Action<string> log)
        {
            Verify(p,plugins);string target=FileInMaps(plugins,p.Target);
            p.State="applying";UpdateJson.Save(Receipt(p),p);
            bool installedHere=false;
            try
            {
                for(int i=0;i<p.Old.Length;i++)
                {
                    string original=OldMapFile(plugins,p.Old[i].Relative),backup=Backup(p,i);
                    if(File.Exists(backup))continue;
                    if(!Same(Current(original),p.Old[i].Hash))throw new IOException("An installed map changed during restart.");
                    File.Move(original,backup);
                    if(!Same(Current(backup),p.Old[i].Hash))throw new IOException("A map backup could not be verified.");
                }
                if(!Same(Current(target),p.NewHash))
                {
                    if(File.Exists(target)||Directory.Exists(target))throw new IOException("The map destination appeared during setup.");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);target=FileInMaps(plugins,p.Target);
                    string temporary=ModUpdateFiles.Within(plugins,".doorman-map-"+Guid.NewGuid().ToString("N")+".tmp");
                    bool created=false;
                    try
                    {
                        using(var input=File.OpenRead(Payload(p)))using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;input.CopyTo(output);output.Flush(true);}
                        if(!Same(Current(temporary),p.NewHash))throw new IOException("The new map copy failed verification.");
                        File.Move(temporary,target);installedHere=true;
                    }
                    finally {try{if(created&&File.Exists(temporary))File.Delete(temporary);}catch(IOException){}catch(UnauthorizedAccessException){}}
                }
                if(!Same(Current(target),p.NewHash))throw new IOException("The installed map failed verification.");
                p.State="complete";UpdateJson.Save(Receipt(p),p);log("Installed map "+p.MapKey+"; previous map files are retained in "+p.Job);
            }
            catch
            {
                // Remove only the newly installed, verified file. Old files are
                // moved back only to empty destinations; a conflict is kept for
                // manual recovery rather than overwriting anyone's work.
                try
                {
                    if(installedHere&&File.Exists(target)&&Same(Current(target),p.NewHash))File.Delete(target);
                    for(int i=p.Old.Length-1;i>=0;i--)
                    {
                        string original=OldMapFile(plugins,p.Old[i].Relative),backup=Backup(p,i);
                        if(File.Exists(backup)&&!File.Exists(original)&&!Directory.Exists(original)&&Same(Current(backup),p.Old[i].Hash))File.Move(backup,original);
                    }
                }
                catch(IOException){}catch(UnauthorizedAccessException){}
                throw;
            }
        }
        internal static void SafeArchivePath(string name)
        {
            if(name.Length==0||name.Length>500||name.StartsWith("/",StringComparison.Ordinal)||name.Contains("\\")||name.Any(c=>char.IsControl(c)||"<>:\"|?*".Contains(c)))throw new InvalidDataException("The map archive contains an unsafe path.");
            foreach(string part in name.Split('/'))
            {
                string device=part.Split('.')[0];
                if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||new[]{"CON","PRN","AUX","NUL"}.Contains(device,StringComparer.OrdinalIgnoreCase)||device.Length==4&&(device.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||device.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&device[3]>='1'&&device[3]<='9')throw new InvalidDataException("The map archive contains an unsafe path.");
            }
        }
    }
}
