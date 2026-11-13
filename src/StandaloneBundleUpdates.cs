using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace KellysJOINCHECK
{
    // Standalone content is inspected by Unity before staging. The startup helper
    // has no Unity runtime, so it binds the inspected bytes by SHA-256 and checks
    // both the old and new file again before changing anything.
    [DataContract]
    internal sealed class StandaloneBundlePlan
    {
        [DataMember] public int Schema=1;
        [DataMember] public string Id="",Version="",Target="",OldHash="",NewHash="",State="pending";
        internal string Job="";
        internal void Validate()
        {
            if(Schema!=1||!ModUpdateFiles.PluginIdentifierValid(Id)||ModReleaseVersion.Compare(Version,Version)!=0||!ModUpdateFiles.HashValid(NewHash)||OldHash.Length>0&&!ModUpdateFiles.HashValid(OldHash)||!Target.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)||!new[]{"pending","applying","complete","cancelled"}.Contains(State))throw new InvalidDataException("Invalid queued content update.");
        }
    }
    internal static class StandaloneBundleUpdates
    {
        // A completed, fingerprinted inventory is sufficient for restart-only
        // staging even when pending asset patches disable Blueprinter reload.
        internal static bool InventoryAvailable(bool exactProof,bool loadingFinished,bool reloadReady,bool inventoryVerified)
            => loadingFinished && (exactProof ? inventoryVerified : reloadReady || inventoryVerified);
        private static string Target(string root,string value)
        {
            string path=ModUpdateFiles.Within(root,value);
            if(!path.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Only standalone content can be updated here.");
            return path;
        }
        internal static string RelativeTarget(string root,string path)
        {
            string basis=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(path);
            if(!full.StartsWith(basis,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Content is outside the plugin folder.");
            string relative=full.Substring(basis.Length);Target(root,relative);return relative;
        }
        private static string Current(string path)=>File.Exists(path)?ModUpdateFiles.Hash(path):"";
        private static bool Same(string a,string b)=>string.Equals(a,b,StringComparison.OrdinalIgnoreCase);
        internal static StandaloneBundlePlan[] Pending(string root,string plugins)
        {
            if(!Directory.Exists(root))return Array.Empty<StandaloneBundlePlan>();
            var plans=Directory.EnumerateDirectories(root).Where(p=>Guid.TryParseExact(Path.GetFileName(p),"N",out _)).Select(p=>ModUpdateFiles.Within(root,Path.GetFileName(p))).Select(job=>new {job,path=ModUpdateFiles.Within(job,"plan.json")}).Where(x=>File.Exists(x.path)).Select(x=>{var p=UpdateJson.ReadFile<StandaloneBundlePlan>(x.path);p.Validate();p.Job=x.job;Target(plugins,p.Target);return p;}).Where(p=>p.State!="complete"&&p.State!="cancelled").ToArray();
            if(plans.Length>128||plans.GroupBy(p=>p.Target,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||plans.GroupBy(p=>p.Id,StringComparer.Ordinal).Any(g=>g.Count()>1))throw new InvalidDataException("Conflicting queued content updates.");
            return plans;
        }
        private static void Verify(StandaloneBundlePlan plan,string plugins)
        {
            plan.Validate();string target=Target(plugins,plan.Target),payload=ModUpdateFiles.Within(plan.Job,"payload.bin"),backup=ModUpdateFiles.Within(plan.Job,"previous.nobp");
            string current=Current(target);
            if(!Same(current,plan.OldHash)&&!(plan.State=="applying"&&Same(current,plan.NewHash)))throw new IOException("Installed content changed after download. Nothing was replaced.");
            if(!File.Exists(payload)||!Same(ModUpdateFiles.Hash(payload),plan.NewHash))throw new InvalidDataException("Queued content failed its checksum.");
            if(plan.State=="applying"&&plan.OldHash.Length>0&&(!File.Exists(backup)||!Same(ModUpdateFiles.Hash(backup),plan.OldHash)))throw new IOException("The content rollback copy needs attention.");
        }
        internal static void VerifyPending(string root,string plugins)
        {foreach(var plan in Pending(root,plugins))Verify(plan,plugins);}
        internal static void Stage(string root,string plugins,string payload,string destination,string id,string version,string expectedHash,CancellationToken token=default)
        {
            token.ThrowIfCancellationRequested();
            if(ModUpdateFiles.ManagedPackagePath(plugins,destination))throw new InvalidOperationException("This content belongs to a complete gameplay package. Update the package from server setup.");
            string relative=RelativeTarget(plugins,destination),target=Target(plugins,relative);
            if(Directory.Exists(target))throw new IOException("The content destination is a folder.");
            if(!ModUpdateFiles.HashValid(expectedHash)||!Same(ModUpdateFiles.Hash(payload),expectedHash))throw new InvalidDataException("Downloaded content failed its checksum.");
            var plans=Pending(root,plugins);
            if(plans.Length>=128||plans.Any(p=>p.Id==id||p.Target.Equals(relative,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("This content is already queued for restart.");
            var plan=new StandaloneBundlePlan {Id=id,Version=version,Target=relative,OldHash=Current(target),NewHash=expectedHash};plan.Validate();
            Directory.CreateDirectory(root);string job=ModUpdateFiles.Within(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
            try
            {
                string staged=ModUpdateFiles.Within(job,"payload.bin");
                using(var input=File.OpenRead(payload))using(var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {var bytes=new byte[65536];int read;while((read=input.Read(bytes,0,bytes.Length))>0){token.ThrowIfCancellationRequested();output.Write(bytes,0,read);}output.Flush(true);}
                if(!Same(ModUpdateFiles.Hash(staged),expectedHash)||!Same(Current(target),plan.OldHash))throw new IOException("Content changed while the update was being prepared.");
                token.ThrowIfCancellationRequested();UpdateJson.Save(ModUpdateFiles.Within(job,"plan.json"),plan);
            }
            catch {if(!File.Exists(ModUpdateFiles.Within(job,"plan.json")))try{Directory.Delete(job,true);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
        }
        internal static void Apply(string root,string plugins,Action<string> log)
        {
            var plans=Pending(root,plugins);
            // Verify every queued item before changing the first file.
            foreach(var plan in plans)Verify(plan,plugins);
            foreach(var plan in plans)
            {
                string target=Target(plugins,plan.Target),payload=ModUpdateFiles.Within(plan.Job,"payload.bin"),backup=ModUpdateFiles.Within(plan.Job,"previous.nobp"),receipt=ModUpdateFiles.Within(plan.Job,"plan.json");
                if(plan.OldHash.Length>0&&!File.Exists(backup))
                {if(!Same(Current(target),plan.OldHash))throw new IOException("The original content is unavailable for backup.");File.Copy(target,backup,false);if(!Same(ModUpdateFiles.Hash(backup),plan.OldHash))throw new IOException("Content backup verification failed.");}
                plan.State="applying";UpdateJson.Save(receipt,plan);Verify(plan,plugins);
                if(!Same(Current(target),plan.NewHash))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string replacement=ModUpdateFiles.Within(Path.GetDirectoryName(target)!,Path.GetFileName(target)+".doorman-"+Guid.NewGuid().ToString("N")+".tmp");
                    bool created=false;
                    try
                    {
                        using(var input=File.OpenRead(payload))using(var output=new FileStream(replacement,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;input.CopyTo(output);output.Flush(true);}
                        if(!Same(ModUpdateFiles.Hash(replacement),plan.NewHash)||!Same(Current(target),plan.OldHash))throw new IOException("Content changed before installation.");
                        if(plan.OldHash.Length==0)File.Move(replacement,target);else File.Replace(replacement,target,null);
                    }
                    finally {if(created&&File.Exists(replacement))try{File.Delete(replacement);}catch(IOException){}catch(UnauthorizedAccessException){}}
                }
                if(!Same(Current(target),plan.NewHash))throw new IOException("The installed content failed verification.");
                plan.State="complete";UpdateJson.Save(receipt,plan);log("Installed content "+Diagnostics.Clean(plan.Id)+" "+Diagnostics.Clean(plan.Version)+(plan.OldHash.Length>0?"; previous file saved at "+backup:"."));
            }
        }
    }
}
