using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private void DeleteAddon(ModEntry item)
        {
            if(busy)return;
            try
            {
                string reason=MenuSafety();if(reason.Length>0)throw new InvalidOperationException(reason);
                if(!startupReady)throw new InvalidOperationException("Install the full DOORMAN package before deleting addons.");
                if(!loader.LoadingFinished)throw new InvalidOperationException("Wait for mods to finish loading.");
                AddonRemovalQueue.EnsureNoOtherJobs(Paths.CachePath,Paths.BepInExRootPath,Paths.ConfigPath);
                var queued=AddonRemovalQueue.PendingPaths(Paths.CachePath,Paths.BepInExRootPath);
                var inventory=entries.ToArray();var units=externalUnits.ToArray();
                busy=true;ui?.Busy(true,"Checking which files belong to "+item.Name+"...");
                var task=Task.Run(()=>AddonRemovalOwnership.Resolve(item,inventory,units,Paths.BepInExRootPath,Paths.CachePath,queued),updateLifetime.Token);
                owner.StartCoroutine(FinishRemovalInspection(item,inventory,task));
            }
            catch(Exception ex){SupportFailure("Addon deletion inspection",ex);ui?.Status("Cannot delete this addon: "+ex.Message);}
        }
        private IEnumerator FinishRemovalInspection(ModEntry item,ModEntry[] inventory,Task<RemovalSelection> task)
        {
            while(!task.IsCompleted)yield return null;
            busy=false;ui?.Busy(false);
            if(task.IsCanceled||updateLifetime.IsCancellationRequested)yield break;
            if(task.IsFaulted){var ex=task.Exception!.GetBaseException();SupportFailure("Addon deletion inspection",ex);ui?.Status("Cannot delete this addon: "+ex.Message);yield break;}
            if(ui?.IsOpen!=true||!entries.SequenceEqual(inventory)||!entries.Contains(item))yield break;
            var plan=task.Result;
            string components=plan.Components.Length>0?string.Join(", ",plan.Components.Take(8)):plan.Name;
            if(plan.Components.Length>8)components+=" and "+(plan.Components.Length-8)+" more";
            string filenames=string.Join(", ",plan.Files.Take(5).Select(f=>System.IO.Path.GetFileName(f.Relative)));
            if(plan.Files.Length>5)filenames+=" and "+(plan.Files.Length-5)+" more";
            string message="Delete "+components+"?\n\n"+plan.Files.Length+" verified installed file(s) will be permanently removed the next time the game starts: "+filenames+".\n\nSettings and saved update backups are kept.";
            ui.PromptConfirmation("DELETE ADDON?",message,"Delete",()=>ConfirmRemoval(item,inventory,plan));
        }
        private void ConfirmRemoval(ModEntry item,ModEntry[] inventory,RemovalSelection plan)
        {
            if(busy||ui?.IsOpen!=true||!entries.SequenceEqual(inventory)||!entries.Contains(item))return;
            string reason=MenuSafety();if(reason.Length>0){ui.Status(reason);return;}
            busy=true;ui.Busy(true,"Preparing deletion...");
            var task=Task.Run(()=>AddonRemovalQueue.Stage(Paths.CachePath,Paths.BepInExRootPath,Paths.ConfigPath,plan.Name,plan.Files),updateLifetime.Token);
            owner.StartCoroutine(FinishRemovalStage(task));
        }
        private IEnumerator FinishRemovalStage(Task task)
        {
            while(!task.IsCompleted)yield return null;
            busy=false;ui?.Busy(false);
            if(task.IsCanceled||updateLifetime.IsCancellationRequested)yield break;
            if(task.IsFaulted){var ex=task.Exception!.GetBaseException();SupportFailure("Addon deletion",ex);ui?.Status("Could not prepare deletion: "+ex.Message);yield break;}
            owner.RecordSupport("Addon deletion","Deletion confirmed. It will finish on the next game start.");
            if(ui?.IsOpen==true){ui.SetPendingRemovalPaths(AddonRemovalQueue.PendingPaths(Paths.CachePath,Paths.BepInExRootPath));ui.Status("Addon marked for deletion. Select more, or restart when ready.");}
        }
    }
}
