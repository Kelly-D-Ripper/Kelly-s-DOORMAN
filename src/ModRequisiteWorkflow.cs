using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private PendingModUpdate[] queuedRequisites=Array.Empty<PendingModUpdate>();
        private System.Collections.Generic.IReadOnlyDictionary<string,QueuedModRequisiteMetadata> queuedRequisiteMetadata=new System.Collections.Generic.Dictionary<string,QueuedModRequisiteMetadata>(StringComparer.Ordinal);
        private string requisiteQueueError="";
        private void RefreshRequisiteQueue()
        {
            try { queuedRequisites=PendingUpdates();queuedRequisiteMetadata=QueuedModRequisiteMetadata.Read(queuedRequisites,Paths.PluginPath);requisiteQueueError=queuedRequisiteMetadata.Values.FirstOrDefault(m=>m.Error.Length>0)?.Error??""; }
            catch(Exception ex) { queuedRequisites=Array.Empty<PendingModUpdate>();queuedRequisiteMetadata=new System.Collections.Generic.Dictionary<string,QueuedModRequisiteMetadata>(StringComparer.Ordinal);requisiteQueueError="Prerequisite queue needs attention: "+ex.Message;log(requisiteQueueError); }
        }
        private ModEntry[] RequisiteInventory()=>ModRequisiteSelection.WithPending(entries,queuedRequisites,queuedRequisiteMetadata,Paths.PluginPath);
        private ModRequisiteIssue[] ModRequisiteIssues(ModEntry item)
        {
            var inventory=RequisiteInventory();var future=inventory.FirstOrDefault(e=>e.Content==item.Content&&e.Id==item.Id&&e.Path==item.Path)??item;
            return WithQueuedRequisites(ModRequisites.ForMod(future,inventory));
        }
        private ModRequisiteIssue[] MapRequisiteIssues(MapEntry item)=>item.RequisiteError.Length>0
            ?new[]{new ModRequisiteIssue { Status=ModRequisiteStatus.Invalid,Message=item.RequisiteError }}
            :WithQueuedRequisites(item.NOMapLoaderBundle?ModRequisites.Evaluate(MapDeclarations(item),RequisiteInventory()):ModRequisites.ForMap(item.Requisites,RequisiteInventory()));
        private ModRequisite[] ModDeclarations(ModEntry item)
        { try{var inventory=RequisiteInventory();return ModRequisites.DeclarationsForMod(inventory.FirstOrDefault(e=>e.Content==item.Content&&e.Id==item.Id&&e.Path==item.Path)??item,inventory);}catch(InvalidDataException){return Array.Empty<ModRequisite>();} }
        private ModRequisite[] MapDeclarations(MapEntry item)
        { try{return item.RequisiteError.Length>0?Array.Empty<ModRequisite>():item.NOMapLoaderBundle?new[]{new ModRequisite { Plugin="NOMapLoader",Name="NOMapLoader",Repository="MinecrackTyler/NOMapLoader" }}:ModRequisites.DeclarationsForMap(item.Requisites);}catch(InvalidDataException){return Array.Empty<ModRequisite>();} }
        private ModRequisiteIssue[] WithQueuedRequisites(ModRequisiteIssue[] issues)
        {
            return issues.Select(issue=>
            {
                var plan=queuedRequisites.FirstOrDefault(p=>ModRequisiteSelection.Satisfies(issue.Requirement,p));
                if(plan==null||issue.Status==ModRequisiteStatus.Disabled||issue.Status==ModRequisiteStatus.Invalid||issue.Status==ModRequisiteStatus.Ambiguous)return issue;
                return new ModRequisiteIssue { Requirement=issue.Requirement,Dependency=issue.Dependency,Via=issue.Via,Status=ModRequisiteStatus.RestartRequired,Message=issue.Requirement.DisplayName+" is downloaded. Restart Nuclear Option to install and load it." };
            }).ToArray();
        }
        private string ValidateRequisiteSelection(System.Collections.Generic.IEnumerable<ModEntry> inventory)
            =>requisiteQueueError.Length>0?requisiteQueueError:ModRequisites.ValidateSelection(ModRequisiteSelection.WithPending(inventory,queuedRequisites,queuedRequisiteMetadata,Paths.PluginPath));

        private void RequisiteAction(ModRequisiteIssue displayed)
        {
            if(busy)return;
            string reason=MenuSafety();if(reason.Length>0){ui?.Status(reason);return;}
            var requirement=displayed.Requirement;
            try { requirement.Validate(); }
            catch(Exception ex) { ui?.Status("Invalid prerequisite: "+ex.Message);return; }
            var refreshed=WithQueuedRequisites(ModRequisites.Evaluate(new[]{requirement},RequisiteInventory()));
            var current=ModRequisiteSelection.ActionIssue(displayed,refreshed);
            if(current==null) {ui?.RefreshSelection();ui?.Status("This prerequisite is ready.");return;}
            if(current.Status==ModRequisiteStatus.RestartRequired) {ApplySelection();return;}
            var dependency=current.Dependency;
            if(dependency!=null)dependency=entries.FirstOrDefault(e=>e.Content==dependency.Content&&e.Id==dependency.Id&&e.Path==dependency.Path)??dependency;
            if(current.Status==ModRequisiteStatus.Disabled&&dependency!=null)
            {
                if(dependency.Locked){ui?.Status("Install the complete client package to change plugins.");return;}
                dependency.Wanted=true;ui?.RefreshSelection();ui?.Status(dependency.Name+" selected. Press Reload mods to apply; plugin changes need a restart.");return;
            }
            if(current.Status!=ModRequisiteStatus.Missing&&current.Status!=ModRequisiteStatus.WrongVersion) {ui?.Status(current.Message);return;}
            string repository=requirement.Repository.Length>0?requirement.Repository:dependency?.UpdateRepository??"";
            string pattern=requirement.Asset.Length>0?requirement.Asset:dependency?.UpdateAsset??"";
            if(!GitHubRepository.Valid(repository)){ui?.Status("No GitHub download supplied for "+requirement.DisplayName+". Install it manually, then restart.");return;}
            if(requirement.Content||ModUpdateFiles.ProtectedPlugin(requirement.Plugin))
            {ui?.Status("This prerequisite uses its own installation procedure. Open its release page.");Application.OpenURL("https://github.com/"+repository+"/releases/latest");return;}
            if(!startupReady){ui?.Status("Install the complete CLIENT or FULL package and its helpers before installing prerequisites.");return;}
            busy=true;pageCheck?.Cancel();ui?.Busy(true,"Checking the download for "+requirement.DisplayName+"...");
            var copy=requirement.Copy();copy.Repository=repository;copy.Asset=pattern;
            var check=Task.Run(()=>updates.CheckPrerequisiteAsync(repository,pattern,copy.Plugin,copy.MinimumVersion,updateLifetime.Token),updateLifetime.Token);
            owner.StartCoroutine(FinishRequisiteCheck(copy,dependency,check));
        }
        private IEnumerator FinishRequisiteCheck(ModRequisite requirement,ModEntry? dependency,Task<ModUpdateCheck> task)
        {
            while(!task.IsCompleted)yield return null;
            if(task.IsCanceled||updateLifetime.IsCancellationRequested){busy=false;ui?.Busy(false,"Prerequisite download cancelled.");yield break;}
            if(task.IsFaulted){RequisiteFailure(task.Exception!.GetBaseException());yield break;}
            var check=task.Result;
            if(check.Queued){busy=false;RefreshRequisiteQueue();ui?.Busy(false,check.Message);ui?.RefreshSelection();yield break;}
            if(check.Release==null||check.Asset==null)
            {
                busy=false;ui?.Busy(false,check.Message+" Use the release page for manual installation.");
                if(check.Release!=null)Application.OpenURL("https://github.com/"+requirement.Repository+"/releases/latest");
                yield break;
            }
            int percent=0;
            var download=Task.Run(()=>dependency==null
                ?updates.StageInstallAsync(requirement.Repository,requirement.Plugin,requirement.MinimumVersion,check.Release,check.Asset,updateLifetime.Token,p=>Volatile.Write(ref percent,p))
                :updates.StageAsync(requirement.Repository,requirement.Plugin,dependency.Path,check.Release,check.Asset,updateLifetime.Token,p=>Volatile.Write(ref percent,p),requirement.MinimumVersion),updateLifetime.Token);
            owner.StartCoroutine(FinishRequisiteInstall(requirement,dependency,download,()=>Volatile.Read(ref percent)));
        }
        private IEnumerator FinishRequisiteInstall(ModRequisite requirement,ModEntry? dependency,Task task,Func<int> progress)
        {
            int last=-1;
            while(!task.IsCompleted)
            {int current=progress();if(current!=last){last=current;ui?.Status("Downloading "+requirement.DisplayName+": "+current+"%.");}yield return null;}
            if(task.IsCanceled){busy=false;ui?.Busy(false,"Download cancelled. Nothing was installed.");yield break;}
            if(task.IsFaulted){RequisiteFailure(task.Exception!.GetBaseException());yield break;}
            if(dependency!=null)dependency.Wanted=true;
            busy=false;RefreshRequisiteQueue();ui?.Busy(false,"Download ready. Press Reload mods to restart, install and load "+requirement.DisplayName+".");ui?.RefreshSelection();
        }
        private void RequisiteFailure(Exception exception)
        {busy=false;log("Prerequisite was not installed: "+exception.Message);ui?.Busy(false,exception.Message);ui?.RefreshSelection();}
    }
}
