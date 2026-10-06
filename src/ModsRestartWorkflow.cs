using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private void RestartMods(ModProfile selection,PendingModUpdate[] pending)
        {
            if(busy)return;string reason=MenuSafety();
            string helper=Path.Combine(Paths.BepInExRootPath,"DOORMAN","KellysDOORMANRestart.exe");
            if(reason.Length==0&&(!StartupAvailable()||Environment.OSVersion.Platform!=PlatformID.Win32NT||!File.Exists(helper)))reason="Automatic restart needs both helpers from the full Windows client package.";
            if(reason.Length>0){ui?.Status(reason);return;}
            busy=true;ui?.Busy(true,"Checking downloaded updates before restarting...");owner.StartCoroutine(PrepareModsRestart(selection,pending,helper));
        }
        private IEnumerator PrepareModsRestart(ModProfile selection,PendingModUpdate[] pending,string helper)
        {
            var task=Task.Run(()=>ModUpdateFiles.VerifyPending(pending,Paths.PluginPath),updateLifetime.Token);
            bool cancelled=false;
            while(!task.IsCompleted){if(ui?.IsOpen!=true)cancelled=true;yield return null;}
            try
            {
                if(task.IsFaulted)throw task.Exception!.GetBaseException();if(task.IsCanceled)throw new IOException("Restart verification was cancelled.");
                if(cancelled||ui?.IsOpen!=true) {busy=false;ui?.Busy(false,"Restart cancelled.");yield break;}
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                string ready=BeginRestartHandshake();var launch=Task.Run(()=>LaunchRestartHelper(helper,ready));ui?.Status("Preparing the Windows restart helper...");owner.StartCoroutine(AwaitModsRestartLaunch(launch,ready,selection));
            }
            catch(Exception ex){busy=false;ui?.Busy(false,"Could not restart: "+ex.Message);log("Mods restart failed: "+ex.Message);}
        }
        private IEnumerator AwaitModsRestartLaunch(Task<string> launch,string ready,ModProfile selection)
        {
            bool cancelled=false;
            while(!launch.IsCompleted){if(ui?.IsOpen!=true){cancelled=true;CancelRestartHandshake();}yield return null;}
            try
            {
                if(cancelled||ui?.IsOpen!=true||File.Exists(ready+".cancel")){CancelRestartHandshake();busy=false;ui?.Busy(false,"Restart cancelled.");yield break;}
                if(launch.IsFaulted)throw launch.Exception!.GetBaseException();if(launch.IsCanceled)throw new IOException("Restart launch was cancelled.");
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                ui?.Status("Restarting Nuclear Option to install updates and load your mods.");owner.StartCoroutine(QuitForModsWhenReady(ready,selection));
            }
            catch(Exception ex){CancelRestartHandshake();busy=false;ui?.Busy(false,"Could not restart: "+ex.Message);log("Mods restart failed: "+ex.Message);}
        }
        private IEnumerator QuitForModsWhenReady(string ready,ModProfile selection)
        {
            float until=Time.unscaledTime+8;
            while(!File.Exists(ready)&&!File.Exists(ready+".cancel")&&Time.unscaledTime<until&&ui?.IsOpen==true)yield return new WaitForSecondsRealtime(.1f);
            if(File.Exists(ready)&&!File.Exists(ready+".cancel")&&ui?.IsOpen==true&&MenuSafety().Length==0)
            {
                try
                {
                    // Persist only once the detached helper is ready and quit
                    // is confirmed. Cancelling preparation keeps the old profile
                    // and any pending server return intact.
                    ModJson.Save(profilePath,selection);profile=selection;temporarySelection=false;listDisabledContent=null;ClearPending();
                    restartExitConfirmed=true;Application.Quit();
                }
                catch(Exception ex){CancelRestartHandshake();busy=false;ui?.Busy(false,"Could not restart: "+ex.Message);log("Mods restart failed: "+ex.Message);}
                yield break;
            }
            try {File.WriteAllText(ready+".cancel","cancel");File.Delete(ready);}catch{}
            busy=false;ui?.Busy(false,"The restart helper did not become ready. Your downloaded updates are still queued.");
        }
    }
}
