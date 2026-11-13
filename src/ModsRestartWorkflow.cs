using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private void RestartMods(ModProfile selection,PendingModUpdate[] pending,bool manual=false)
        {
            if(busy)return;string reason=MenuSafety();
            string helper=Path.Combine(Paths.BepInExRootPath,"DOORMAN","KellysDOORMANRestart.exe");
            if(reason.Length==0&&!StartupAvailable())reason="The startup helper is unavailable. Reinstall the full client package.";
            if(reason.Length==0&&!manual&&RuntimePlatform.IsWine)
            {
                ui?.PromptConfirmation("RESTART REQUIRED","Close the game, then reopen it through Steam to apply your changes.","Close game",()=>RestartMods(selection.Copy(),pending,true));
                return;
            }
            if(reason.Length==0&&!manual&&(!RuntimePlatform.CanUseWindowsRestart||!File.Exists(helper)))
            {
                reason="Automatic restart needs the restart helper. Your downloaded updates are still ready.";
                owner.RecordSupport("Mods restart",reason,true);ui?.Status(reason);
                OfferManualModsRestart(selection,pending);return;
            }
            if(reason.Length>0){owner.RecordSupport("Mods restart",reason,true);ui?.Status(reason);return;}
            if(manual)CancelRestartHandshake();
            DisabledModRestore[] restores;
            try{var projected=RequisiteInventory();restores=PrepareExternalRestores(projected,projected.Where(e=>e.Wanted));CheckRestoreUpdateOverlap(restores,pending);}
            catch(Exception ex){SupportFailure("NOMM restore preparation",ex);ui?.Status(ex.Message);return;}
            busy=true;ui?.Busy(true,"Checking downloaded updates and disabled mods before restarting...");owner.StartCoroutine(PrepareModsRestart(selection,pending,helper,restores,manual));
        }
        private IEnumerator PrepareModsRestart(ModProfile selection,PendingModUpdate[] pending,string helper,DisabledModRestore[] restores,bool manual=false)
        {
            var task=Task.Run(()=>{DisabledModFiles.Verify(Paths.BepInExRootPath,restores);ModUpdateFiles.VerifyPending(pending,Paths.PluginPath);StandaloneBundleUpdates.VerifyPending(Path.Combine(Paths.CachePath,"DOORMAN-content-updates"),Paths.PluginPath);},updateLifetime.Token);
            bool cancelled=false;
            while(!task.IsCompleted){if(ui?.IsOpen!=true)cancelled=true;yield return null;}
            try
            {
                if(task.IsFaulted)throw task.Exception!.GetBaseException();if(task.IsCanceled)throw new IOException("Restart verification was cancelled.");
                if(cancelled||ui?.IsOpen!=true) {busy=false;ui?.Busy(false,"Restart cancelled.");yield break;}
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                if(manual){CommitManualModsRestart(selection,restores);yield break;}
                string ready=BeginRestartHandshake();var launch=Task.Run(()=>LaunchRestartHelper(helper,ready));ui?.Status("Preparing the Windows restart helper...");owner.StartCoroutine(AwaitModsRestartLaunch(launch,ready,selection,restores,pending));
            }
            catch(Exception ex){SupportFailure("Mods restart verification",ex);busy=false;ui?.Busy(false,"Could not restart: "+ex.Message);log("Mods restart failed: "+ex.Message);}
        }
        private void CommitManualModsRestart(ModProfile selection,DisabledModRestore[] restores)
        {
            bool queueSaved=false;
            try
            {
                string previousServer=Path.Combine(Paths.ConfigPath,ServerSetupFiles.Filename);
                byte[]? savedServer=null;
                if(File.Exists(previousServer))
                {
                    if(new FileInfo(previousServer).Length>65536)throw new InvalidDataException("The previous server page is too large to preserve. Use Recovery before restarting.");
                    savedServer=File.ReadAllBytes(previousServer);
                }
                DisabledRestoreQueue.Save(Paths.ConfigPath,restores);queueSaved=true;
                bool serverRemoved=false;
                try{File.Delete(previousServer);serverRemoved=true;ModJson.Save(profilePath,selection);}
                catch
                {
                    if(serverRemoved&&savedServer!=null)File.WriteAllBytes(previousServer,savedServer);
                    throw;
                }
                profile=selection;temporarySelection=false;listDisabledContent=null;
                ownsPendingRestart=true;
                ui?.Status("Selection saved. Nuclear Option will close. Start it again through Steam once Steam shows Play. Mods and updates will apply during startup.");
                owner.RecordSupport("Manual mods restart","Verified selection saved; the player will reopen Nuclear Option through Steam.");
                restartExitConfirmed=true;Application.Quit();
            }
            catch(Exception ex)
            {
                SupportFailure("Manual mods restart selection save",ex);CancelRestartHandshake();
                if(queueSaved)try{DisabledRestoreQueue.Clear(Paths.ConfigPath);}catch(Exception cleanup){log("Could not cancel the NOMM restore queue: "+cleanup.Message);}
                busy=false;ui?.Busy(false,"Could not prepare manual restart: "+ex.Message);log("Manual mods restart failed: "+ex.Message);
            }
        }
        private void OfferManualModsRestart(ModProfile selection,PendingModUpdate[] pending)
        {
            if(ui?.IsOpen!=true||restartExitConfirmed||MenuSafety().Length>0)return;
            ui.PromptRestart("Automatic restart could not start. Your changes are still ready.",
                ()=>RestartMods(selection.Copy(),pending),()=>RestartMods(selection.Copy(),pending,true));
        }
        private void WarnUnconfirmedRestartCancellation()
        {
            const string warning="Windows is still preparing a restart. Close any Windows prompt before closing the game.";
            owner.RecordSupport("Mods restart helper","Could not confirm the delayed helper was cancelled.",true);
            ui?.Busy(false,warning);
        }
        private IEnumerator AwaitModsRestartLaunch(Task<string> launch,string ready,ModProfile selection,DisabledModRestore[] restores,PendingModUpdate[] pending)
        {
            bool cancelled=false;float deadline=Time.unscaledTime+15;
            while(!launch.IsCompleted&&Time.unscaledTime<deadline)
            {if(ui?.IsOpen!=true){cancelled=true;break;}yield return null;}
            if(!launch.IsCompleted)
            {
                CancelRestartHandshake();
                // A delayed Windows prompt may return after we offer the manual
                // choice. The cancellation marker prevents that helper relaunch.
                _=launch.ContinueWith(task=>{var ignored=task.Exception;},TaskContinuationOptions.OnlyOnFaulted|TaskContinuationOptions.ExecuteSynchronously);
                busy=false;
                if(!File.Exists(ready+".cancel"))
                {
                    WarnUnconfirmedRestartCancellation();yield break;
                }
                if(cancelled){ui?.Busy(false,"Restart cancelled.");yield break;}
                var timeout=new TimeoutException("Windows did not finish starting the restart helper. Your downloaded updates are still queued.");
                SupportFailure("Mods restart helper launch",timeout);ui?.Busy(false,RestartLaunchFailure.Guidance(timeout));
                OfferManualModsRestart(selection,pending);yield break;
            }
            try
            {
                if(cancelled||ui?.IsOpen!=true||File.Exists(ready+".cancel")){CancelRestartHandshake();busy=false;ui?.Busy(false,"Restart cancelled.");yield break;}
                if(launch.IsFaulted)throw launch.Exception!.GetBaseException();if(launch.IsCanceled)throw new IOException("Restart launch was cancelled.");
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                ui?.Status("Restarting Nuclear Option to restore selected mods, install updates and load your selection.");owner.StartCoroutine(QuitForModsWhenReady(ready,selection,restores,pending));
            }
            catch(Exception ex){SupportFailure("Mods restart helper launch",ex);CancelRestartHandshake();busy=false;ui?.Busy(false,RestartLaunchFailure.Guidance(ex));log("Mods restart failed: "+ex.Message);if(!cancelled)OfferManualModsRestart(selection,pending);}
        }
        private IEnumerator QuitForModsWhenReady(string ready,ModProfile selection,DisabledModRestore[] restores,PendingModUpdate[] pending)
        {
            float until=Time.unscaledTime+8;
            while(!File.Exists(ready)&&!File.Exists(ready+".cancel")&&Time.unscaledTime<until&&ui?.IsOpen==true)yield return new WaitForSecondsRealtime(.1f);
            if(File.Exists(ready)&&!File.Exists(ready+".cancel")&&ui?.IsOpen==true&&MenuSafety().Length==0)
            {
                bool queueSaved=false;
                try
                {
                    // Persist only once the detached helper is ready and quit
                    // is confirmed. Cancelling preparation keeps the old profile
                    // and any pending server return intact.
                    string previousServer=Path.Combine(Paths.ConfigPath,ServerSetupFiles.Filename);
                    byte[]? savedServer=null;
                    if(File.Exists(previousServer))
                    {
                        if(new FileInfo(previousServer).Length>65536)throw new InvalidDataException("The previous server page is too large to preserve. Use Recovery before restarting.");
                        savedServer=File.ReadAllBytes(previousServer);
                    }
                    DisabledRestoreQueue.Save(Paths.ConfigPath,restores);
                    queueSaved=true;
                    // A queue write failure must leave the current selection and
                    // saved server page alone. A profile write failure removes
                    // only the newly prepared physical restore intent.
                    bool serverRemoved=false;
                    try{File.Delete(previousServer);serverRemoved=true;ModJson.Save(profilePath,selection);}
                    catch
                    {
                        if(serverRemoved&&savedServer!=null)File.WriteAllBytes(previousServer,savedServer);
                        throw;
                    }
                    profile=selection;temporarySelection=false;listDisabledContent=null;
                    ownsPendingRestart=true;
                    restartExitConfirmed=true;Application.Quit();
                }
                catch(Exception ex){SupportFailure("Mods restart selection save",ex);CancelRestartHandshake();if(queueSaved)try{DisabledRestoreQueue.Clear(Paths.ConfigPath);}catch(Exception cleanup){log("Could not cancel the NOMM restore queue: "+cleanup.Message);}busy=false;ui?.Busy(false,"Could not restart: "+ex.Message);log("Mods restart failed: "+ex.Message);}
                yield break;
            }
            try {File.WriteAllText(ready+".cancel","cancel");File.Delete(ready);}catch{}
            busy=false;
            if(!File.Exists(ready+".cancel")){WarnUnconfirmedRestartCancellation();yield break;}
            ui?.Busy(false,RestartLaunchFailure.Guidance(new TimeoutException("The restart helper did not become ready. Your downloaded updates are still queued.")));
            owner.RecordSupport("Mods restart helper","The restart helper did not become ready. Your downloaded updates are still queued.",true);
            OfferManualModsRestart(selection,pending);
        }
    }
}
