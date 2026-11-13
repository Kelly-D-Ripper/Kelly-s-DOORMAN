using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace KellysJOINCHECK
{
    [DataContract] internal sealed class RemovalFile
    {
        [DataMember] public string Root="",Relative="",Hash="";
    }
    [DataContract] internal sealed class RemovalPlan
    {
        [DataMember] public int Schema=1;
        [DataMember] public string Token="",Name="",State="pending";
        [DataMember] public RemovalFile[] Files=Array.Empty<RemovalFile>();
    }

    // Files are moved out of the loader before deletion, after an explicit
    // confirmation and only on the next startup. A durable receipt permits
    // interrupted moves or deletion to resume without touching unknown files.
    internal static class AddonRemovalQueue
    {
        private const string Queue="doorman-removal.json",Folder="DOORMAN-removal-files";
        private const int MaxFiles=512;
        private static string QueuePath(string cache)=>ModUpdateFiles.Within(cache,Queue);
        private static string BackupRoot(string cache,RemovalPlan p)=>ModUpdateFiles.Within(ModUpdateFiles.Within(cache,Folder),p.Token);
        private static string Backup(string cache,RemovalPlan p,int index)=>ModUpdateFiles.Within(BackupRoot(cache,p),index.ToString("D4")+".bin");
        private static string Source(string bep,RemovalFile f)
        {
            if(f.Root!="plugins"&&f.Root!="disabledPlugins")throw new InvalidDataException("Invalid removal location.");
            string root=ModUpdateFiles.Within(bep,f.Root);
            return ModUpdateFiles.Within(root,f.Relative);
        }
        private static string Hash(string file)
        {
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))return InstalledFilePolicy.Hash(stream);
        }
        private static void CleanEmptyParents(string bep,RemovalFile file)
        {
            string root=ModUpdateFiles.Within(bep,file.Root),current=Path.GetDirectoryName(Source(bep,file))!;
            while(current.Length>root.Length&&current.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            {
                string relative=current.Substring(root.Length+1);current=ModUpdateFiles.Within(root,relative);
                try{if(!Directory.Exists(current)||Directory.EnumerateFileSystemEntries(current).Any())break;Directory.Delete(current,false);}
                catch(IOException){break;}catch(UnauthorizedAccessException){break;}
                current=Path.GetDirectoryName(current)!;
            }
        }
        private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        private static void Validate(RemovalPlan p,string bep)
        {
            if(p==null||p.Schema!=1||!Guid.TryParseExact(p.Token,"N",out _)||string.IsNullOrWhiteSpace(p.Name)||p.Name.Length>160||p.Name.Any(char.IsControl)||!new[]{"pending","moving","finalising","complete"}.Contains(p.State)||p.Files==null||p.Files.Length<1||p.Files.Length>MaxFiles||p.Files.Any(f=>f==null||!ModUpdateFiles.HashValid(f.Hash)))throw new InvalidDataException("Invalid addon removal receipt.");
            var paths=p.Files.Select(f=>Source(bep,f)).ToArray();
            if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length)throw new InvalidDataException("Duplicate addon removal paths.");
        }
        internal static RemovalPlan? Read(string cache,string bep)
        {
            string path=QueuePath(cache);if(!File.Exists(path))return null;
            var p=UpdateJson.ReadFile<RemovalPlan>(path,512*1024);Validate(p,bep);return p;
        }
        internal static bool Pending(string cache,string bep){var p=Read(cache,bep);return p!=null&&p.State!="complete";}
        internal static string[] PendingPaths(string cache,string bep)
        {
            var p=Read(cache,bep);return p==null||p.State=="complete"?Array.Empty<string>():p.Files.Select(f=>Source(bep,f)).ToArray();
        }
        internal static void EnsureNoOtherJobs(string cache,string bep,string config)
        {
            string plugins=ModUpdateFiles.Within(bep,"plugins");
            if(File.Exists(ModUpdateFiles.Within(cache,"DOORMAN-auto-setup.json"))||ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins).Length>0||StandaloneBundleUpdates.Pending(Path.Combine(cache,"DOORMAN-content-updates"),plugins).Length>0||ServerMapUpdates.Read(cache,plugins,true).Length>0||IronjawPackageUpdates.Read(cache,plugins,true).Length>0||DisabledRestoreQueue.Read(config).Length>0||SelfUpdateTransaction.Pending(cache))
                throw new InvalidOperationException("Finish other queued mod changes first, then delete this addon.");
        }
        internal static string Digest(string file)=>Hash(file);
        internal static void Stage(string cache,string bep,string config,string name,IEnumerable<RemovalFile> sources)
        {
            EnsureNoOtherJobs(cache,bep,config);
            var existing=Read(cache,bep);
            if(existing!=null&&existing.State!="complete"&&existing.State!="pending")throw new InvalidOperationException("An interrupted deletion needs recovery before adding more addons.");
            var p=existing!=null&&existing.State=="pending"?existing:new RemovalPlan {Token=Guid.NewGuid().ToString("N"),Name=Diagnostics.Clean(name)};
            var items=(sources??throw new ArgumentNullException(nameof(sources))).Take(MaxFiles+1).ToArray();
            if(items.Length<1||items.Length>MaxFiles)throw new InvalidDataException("The addon's files could not be verified.");
            var added=items.Select(i=>new RemovalFile {Root=i.Root,Relative=i.Relative,Hash=i.Hash}).ToArray();
            if(existing!=null&&existing.State=="pending")
            {
                var queued=new HashSet<string>(existing.Files.Select(f=>Source(bep,f)),StringComparer.OrdinalIgnoreCase);
                if(added.Any(f=>queued.Contains(Source(bep,f))))throw new InvalidOperationException("This addon is already queued for deletion.");
                p.Files=existing.Files.Concat(added).ToArray();p.Name="Selected addons";
            }
            else p.Files=added;
            Validate(p,bep);
            foreach(var item in p.Files)
            {
                string path=Source(bep,item);if(!File.Exists(path))throw new IOException("An addon file is missing or changed.");
                if(path.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&ModUpdateFiles.PluginIds(path).Any(ModUpdateFiles.ProtectedPlugin))throw new InvalidDataException("A protected loader or DOORMAN cannot be deleted here.");
                if(!Same(Hash(path),item.Hash))throw new IOException("An addon file changed after confirmation. Nothing was removed.");
            }
            Validate(p,bep);
            // Refuse changed sources before committing the user-visible intent.
            foreach(var item in p.Files)if(!Same(Hash(Source(bep,item)),item.Hash))throw new IOException("An addon file changed during preparation.");
            EnsureNoOtherJobs(cache,bep,config);UpdateJson.Save(QueuePath(cache),p,false,512*1024);
        }
        internal static void Apply(string cache,string bep,string config,Action<string> log)
        {
            var p=Read(cache,bep);if(p==null||p.State=="complete")return;
            EnsureNoOtherJobs(cache,bep,config);string backupRoot=BackupRoot(cache,p);
            if(p.State=="pending"||p.State=="moving")
            {
                // Re-check the entire selection before moving any files.
                for(int i=0;i<p.Files.Length;i++)
                {
                    string source=Source(bep,p.Files[i]),backup=Backup(cache,p,i);bool a=File.Exists(source),b=File.Exists(backup);
                    if(a==b||!Same(Hash(a?source:backup),p.Files[i].Hash))throw new IOException("An addon file changed before removal. No further files were removed.");
                    if(a&&source.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&ModUpdateFiles.PluginIds(source).Any(ModUpdateFiles.ProtectedPlugin))throw new InvalidDataException("A protected loader or DOORMAN cannot be deleted here.");
                }
                Directory.CreateDirectory(backupRoot);p.State="moving";UpdateJson.Save(QueuePath(cache),p,false,512*1024);
                for(int i=0;i<p.Files.Length;i++)
                {
                    string source=Source(bep,p.Files[i]),backup=Backup(cache,p,i);
                    if(File.Exists(backup))continue;
                    if(!Same(Hash(source),p.Files[i].Hash)||File.Exists(backup))throw new IOException("An addon file changed during removal.");
                    File.Move(source,backup);
                }
                p.State="finalising";UpdateJson.Save(QueuePath(cache),p,false,512*1024);
            }
            if(p.State=="finalising")
            {
                for(int i=0;i<p.Files.Length;i++)
                {
                    string source=Source(bep,p.Files[i]),backup=Backup(cache,p,i);
                    if(File.Exists(source))throw new IOException("An addon file reappeared during removal. Review it manually.");
                    if(File.Exists(backup)){if(!Same(Hash(backup),p.Files[i].Hash))throw new IOException("A staged addon file changed during removal.");File.Delete(backup);}
                }
                foreach(var file in p.Files)CleanEmptyParents(bep,file);
                p.State="complete";UpdateJson.Save(QueuePath(cache),p,false,512*1024);log("Removed addon "+Diagnostics.Clean(p.Name)+".");
            }
        }
    }
}
