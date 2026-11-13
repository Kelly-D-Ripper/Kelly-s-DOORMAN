using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class SelfUpdateReceipt
    {
        [DataMember] public int Schema=1;
        [DataMember] public string Version="",OldHash="",NewHash="",StartupHash="",RestartHash="",ServerHash="",State="pending",Message="";
        internal void Validate()
        {
            if(Schema!=1 || ModReleaseVersion.Compare(Version,Version)!=0 || !ModUpdateFiles.HashValid(OldHash) || !ModUpdateFiles.HashValid(NewHash) || !ModUpdateFiles.HashValid(StartupHash) || !ModUpdateFiles.HashValid(RestartHash) || (ServerHash.Length>0&&!ModUpdateFiles.HashValid(ServerHash)) || !new[]{"pending","applying","complete","failed"}.Contains(State)) throw new InvalidDataException("Invalid DOORMAN update receipt.");
        }
    }
    // Only replaces the exact client DLL before BepInEx loads plugins. The
    // helpers and server component must be byte-identical to the release.
    internal static class SelfUpdateTransaction
    {
        private const string Folder="DOORMAN-self-update";
        internal static string Root(string cache)=>ModUpdateFiles.Within(cache,Folder);
        private static string Plan(string cache)=>ModUpdateFiles.Within(Root(cache),"plan.json");
        private static string Client(string bepinex)=>ModUpdateFiles.Within(bepinex,Path.Combine("plugins","KellysJOINCHECK.dll"));
        private static string Startup(string bepinex)=>ModUpdateFiles.Within(bepinex,Path.Combine("patchers","KellysDOORMANStartup.dll"));
        private static string Restart(string bepinex)=>ModUpdateFiles.Within(bepinex,Path.Combine("DOORMAN","KellysDOORMANRestart.exe"));
        private static string Server(string bepinex)=>ModUpdateFiles.Within(bepinex,Path.Combine("plugins","KellysJOINCHECKServer.dll"));
        internal static SelfUpdateReceipt? Read(string cache)
        {
            string path=Plan(cache);if(!File.Exists(path))return null;
            var receipt=UpdateJson.ReadFile<SelfUpdateReceipt>(path);receipt.Validate();return receipt;
        }
        internal static bool Pending(string cache){var p=Read(cache);return p!=null&&(p.State=="pending"||p.State=="applying");}
        private static bool ReleaseIdentity(string path,string version)
        {
            using(var assembly=AssemblyDefinition.ReadAssembly(path))
            {
                var field=assembly.MainModule.GetType("KellysJOINCHECK.SelfUpdateRelease")?.Fields.SingleOrDefault(f=>f.Name=="Installed");
                return field!=null&&field.HasConstant&&field.Constant is string declared&&declared==version;
            }
        }
        internal static void Stage(string cache,string bepinex,string source,string version,string startupHash,string restartHash,string serverHash)
        {
            if(ModReleaseVersion.Compare(version,version)!=0)throw new InvalidDataException("This DOORMAN version needs a manual update.");
            string target=Client(bepinex),startup=Startup(bepinex),restart=Restart(bepinex),server=Server(bepinex);
            if(!File.Exists(target)||!File.Exists(startup)||!File.Exists(restart))throw new IOException("Install the complete DOORMAN package first.");
            if(!ModUpdateFiles.HashValid(startupHash)||!ModUpdateFiles.HashValid(restartHash)||(serverHash.Length>0&&!ModUpdateFiles.HashValid(serverHash))||ModUpdateFiles.Hash(startup)!=startupHash||ModUpdateFiles.Hash(restart)!=restartHash||(File.Exists(server)?ModUpdateFiles.Hash(server):"")!=serverHash)throw new IOException("This release needs a full manual installation.");
            if(!ModUpdateFiles.PluginIds(target).SequenceEqual(new[]{"kelly.nuclearoption.joincheck"})||!ModUpdateFiles.PluginIds(source).SequenceEqual(new[]{"kelly.nuclearoption.joincheck"}))throw new InvalidDataException("The DOORMAN client identity could not be verified.");
            if(!ReleaseIdentity(source,version))throw new InvalidDataException("The downloaded DOORMAN client version does not match this release. Install the full package manually.");
            var old=Read(cache);if(old!=null&&(old.State=="pending"||old.State=="applying"))throw new IOException("A DOORMAN update is already waiting for restart.");
            string root=Root(cache);
            if(old!=null)
            {
                // Keep every previous receipt and rollback copy without occupying
                // the current transaction slot. The move is within one cache root.
                string history=ModUpdateFiles.Within(cache,"DOORMAN-self-history-"+Guid.NewGuid().ToString("N"));
                Directory.Move(root,history);
            }
            Directory.CreateDirectory(root);
            string payload=ModUpdateFiles.Within(root,"payload.bin"),backup=ModUpdateFiles.Within(root,"previous.bin");
            // A completed receipt and backup are kept; do not silently discard a recovery copy.
            if(File.Exists(payload)||File.Exists(backup)||File.Exists(Plan(cache)))throw new IOException("A previous DOORMAN update receipt exists. Install this release manually.");
            try
            {
                using(var input=File.OpenRead(source))using(var output=new FileStream(payload,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
                var p=new SelfUpdateReceipt {Version=version,OldHash=ModUpdateFiles.Hash(target),NewHash=ModUpdateFiles.Hash(payload),StartupHash=startupHash,RestartHash=restartHash,ServerHash=serverHash};p.Validate();UpdateJson.Save(Plan(cache),p);
            }
            catch {try{if(!File.Exists(Plan(cache))&&File.Exists(payload))File.Delete(payload);}catch{}throw;}
        }
        internal static bool Apply(string cache,string bepinex,Action<string> log)
        {
            var p=Read(cache);if(p==null||p.State=="complete"||p.State=="failed")return false;
            string root=Root(cache),target=Client(bepinex),payload=ModUpdateFiles.Within(root,"payload.bin"),backup=ModUpdateFiles.Within(root,"previous.bin"),temp=ModUpdateFiles.Within(Path.GetDirectoryName(target)!,"KellysJOINCHECK.dll.doorman-new");
            try
            {
                string server=Server(bepinex);
                if(ModUpdateFiles.Hash(Startup(bepinex))!=p.StartupHash || ModUpdateFiles.Hash(Restart(bepinex))!=p.RestartHash || (File.Exists(server)?ModUpdateFiles.Hash(server):"")!=p.ServerHash)throw new IOException("DOORMAN helpers changed. Install the full package manually.");
                if(ModUpdateFiles.Hash(payload)!=p.NewHash)throw new IOException("The downloaded DOORMAN update changed. Install it manually.");
                string current=ModUpdateFiles.Hash(target);
                if(current!=p.OldHash&&!(p.State=="applying"&&current==p.NewHash))throw new IOException("The installed DOORMAN client changed. Install the full package manually.");
                if(File.Exists(backup)){if(ModUpdateFiles.Hash(backup)!=p.OldHash)throw new IOException("The DOORMAN backup changed.");}
                else {if(current!=p.OldHash)throw new IOException("The DOORMAN backup is missing.");File.Copy(target,backup,false);if(ModUpdateFiles.Hash(backup)!=p.OldHash)throw new IOException("Could not verify the DOORMAN backup.");}
                p.State="applying";UpdateJson.Save(Plan(cache),p);
                if(current==p.OldHash)
                {
                    if(File.Exists(temp)) { if(ModUpdateFiles.Hash(temp)!=p.NewHash)throw new IOException("A different file occupies the DOORMAN update staging path."); }
                    else using(var input=File.OpenRead(payload))using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
                    if(ModUpdateFiles.Hash(temp)!=p.NewHash||ModUpdateFiles.Hash(target)!=p.OldHash)throw new IOException("DOORMAN update files changed before installation.");
                    File.Replace(temp,target,null);
                }
                if(ModUpdateFiles.Hash(target)!=p.NewHash)throw new IOException("DOORMAN update verification failed.");
                p.State="complete";p.Message="DOORMAN "+p.Version+" installed. Previous client saved in the update cache.";UpdateJson.Save(Plan(cache),p);log(p.Message);return true;
            }
            catch(Exception ex)
            {
                log("DOORMAN update needs attention: "+ex.Message);
                // A caught error is different from a power loss: do not keep
                // asking the player to restart indefinitely. The old/new bytes,
                // receipt and verified backup are retained for manual recovery.
                try{p.State="failed";p.Message=ex.Message.Length>1500?ex.Message.Substring(0,1500):ex.Message;UpdateJson.Save(Plan(cache),p);}catch{}
                return false;
            }
        }
    }
}
