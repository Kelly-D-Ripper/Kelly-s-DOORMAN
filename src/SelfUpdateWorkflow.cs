using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private bool selfAnnounced;
        private Task<ModUpdateCheck>? selfAnnouncementTask;
        private void PromptSelfManualRestart()
        {
            string reason=MenuSafety();if(reason.Length>0){ui?.Status(reason);return;}
            ui?.PromptConfirmation("RESTART REQUIRED","DOORMAN is ready to update. Close the game and start it again through Steam. Changes to the mod selection on this page will not be applied.","Close game",()=>{string safety=MenuSafety();if(safety.Length>0){ui?.Status(safety);return;}Application.Quit();});
        }
        private void CheckSelfOnMenu()
        {
            if(selfAnnounced||!startupReady)return;selfAnnounced=true;
            try{if(selfAnnouncementTask==null||selfAnnouncementTask.IsCanceled||selfAnnouncementTask.IsFaulted)selfAnnouncementTask=CheckSelf(updateLifetime.Token,SelfHasServer());owner.StartCoroutine(AnnounceSelf(selfAnnouncementTask));}
            catch(Exception ex){selfAnnounced=false;log("DOORMAN update check unavailable: "+ex.Message);}
        }
        private IEnumerator AnnounceSelf(Task<ModUpdateCheck> task)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled||task.IsFaulted){selfAnnounced=false;selfAnnouncementTask=null;yield break;}
            if(ui?.IsOpen!=true){selfAnnounced=false;yield break;}
            if(task.Result.Newer)ui.Status("A DOORMAN update is available. Select DOORMAN in the Mods list for details.");
            else if(task.Result.Queued)ui.Status("A DOORMAN update is ready. Press Reload mods to restart.");
        }
        private bool SelfHasServer()
        {
            string expected=ModUpdateFiles.Within(Paths.PluginPath,"KellysJOINCHECKServer.dll");
            var matches=entries.Where(e=>!e.Content&&e.Id=="kelly.nuclearoption.joincheck.server").ToArray();
            if(matches.Length>1||matches.Any(e=>!Path.GetFullPath(e.Path).Equals(expected,StringComparison.OrdinalIgnoreCase)))throw new IOException("DOORMAN server is in a nonstandard location. Install updates manually.");
            return File.Exists(expected);
        }
        private Task<ModUpdateCheck> CheckSelf(CancellationToken token,bool hasServer)
        {
            return Task.Run(async ()=>
            {
                var pending=SelfUpdateTransaction.Read(Paths.CachePath);
                if(pending!=null&&(pending.State=="pending"||pending.State=="applying"))return new ModUpdateCheck {Queued=true,Message="DOORMAN update ready. Restart to install it."};
                var release=await SelfUpdateRelease.Check(token).ConfigureAwait(false);
                if(release==null)return new ModUpdateCheck {Message=pending?.State=="failed"?"Update could not be installed. Open GitHub for the full package.":"DOORMAN is up to date."};
                if(pending?.State=="failed")return new ModUpdateCheck {Release=release,Newer=true,Message="The last DOORMAN update failed. Open the release page and install the full package manually."};
                var asset=SelfUpdateRelease.Asset(release,hasServer);
                return new ModUpdateCheck {Release=release,Asset=asset,Newer=true,Message=asset==null?"DOORMAN "+release.Tag+" is available. Open the release page to install it.":"DOORMAN "+release.Tag+" is available. Update, then restart."};
            },token);
        }
        private static string SelfHash(byte[] data)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
        private void BeginSelfUpdate(ModEntry item,ModUpdateCheck check,Action? afterDownload=null,Action<string>? failed=null)
        {
            busy=true;ui?.Busy(true,"Downloading DOORMAN. The game will need a restart.");pageCheck?.Cancel();
            var progress=new ServerDownloadProgress();
            Task task;
            try{task=StageSelfUpdate(check,progress);}catch{busy=false;ui?.Busy(false);throw;}
            owner.StartCoroutine(FinishUpdate(item,task,()=>progress.Fraction<0?-1:(int)(progress.Fraction*100),afterDownload,failed));
        }
        private Task StageSelfUpdate(ModUpdateCheck check,ServerDownloadProgress progress)
        {
            bool hasServer=SelfHasServer();string cache=Paths.CachePath,root=Paths.BepInExRootPath;
            var task=Task.Run(async ()=>
            {
                string downloaded=await updates.DownloadMapPackageAsync(SelfUpdateRelease.Repository,check.Release!,check.Asset!,updateLifetime.Token,progress).ConfigureAwait(false);
                string? client=null;
                try
                {
                    var files=SelfUpdatePackage.Read(downloaded,check.Asset!.Digest!,hasServer,updateLifetime.Token);
                    if(!SelfUpdatePackage.CanReplaceClientOnly(files,root,hasServer))throw new InvalidOperationException("This update changes DOORMAN's helpers or server component. Close the game, then install the full package from the release page.");
                    string staging=ModUpdateFiles.Within(cache,"DOORMAN-updates\\downloads");Directory.CreateDirectory(staging);client=ModUpdateFiles.Within(staging,Guid.NewGuid().ToString("N")+".bin");
                    using(var output=new FileStream(client,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var bytes=files[SelfUpdatePackage.Required[0]];output.Write(bytes,0,bytes.Length);output.Flush(true);}
                    updateLifetime.Token.ThrowIfCancellationRequested();
                    SelfUpdateTransaction.Stage(cache,root,client,SelfUpdateRelease.Comparable(check.Release!.Tag),SelfHash(files[SelfUpdatePackage.Required[1]]),SelfHash(files[SelfUpdatePackage.Required[2]]),hasServer?SelfHash(files[SelfUpdatePackage.Server]):"");
                }
                finally{foreach(var file in new[]{downloaded,client})try{if(file!=null&&File.Exists(file))File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}}
            },updateLifetime.Token);
            return task;
        }
    }
}
