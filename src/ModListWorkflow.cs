using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private static SavedModItem[] ListSnapshot(IEnumerable<ModEntry> catalog) => catalog
            .Where(e=>e.Content || !ModProfile.Protected(e.Id))
            .Select(e=>new SavedModItem { Content=e.Content,Id=e.Id,Name=e.Name.Trim(),Version=e.Version.Trim(),Enabled=e.Wanted }).ToArray();

        private string ListGameVersion() => loader.GameVersion.Length>0 ? loader.GameVersion : Signature.Parse(Application.version).Game;

        private ContentRecord[] ObservedListContent()
        {
            var records=new List<ContentRecord>();
            foreach(var entry in entries.Where(e=>e.Content&&(!e.Path.StartsWith("resource:",StringComparison.Ordinal)||e.WrapperPath.Length>0)))
            {
                try
                {
                    var observed=loader.Content.FirstOrDefault(c=>c.Id==entry.Id);
                    if(observed==null || observed.ObservedSourceLength<0) continue;
                    string source=entry.WrapperPath.Length>0?entry.WrapperPath:entry.Path;
                    var info=new FileInfo(source);
                    if(!info.Exists || info.Length!=observed.ObservedSourceLength || info.LastWriteTimeUtc.Ticks!=observed.ObservedSourceWriteTicks)
                    { log("Skipped content receipt whose source changed since Blueprinter loaded it: "+entry.Id);continue; }
                    records.Add(new ContentRecord { Id=entry.Id,Name=entry.Name.Trim(),Version=entry.Version.Trim(),Source=entry.Path,RelativeFile=ServerSetupFiles.RelativeContent(Paths.PluginPath,source) });
                }
                catch(Exception ex) { log("Skipped optional content receipt: "+ex.GetType().Name); }
            }
            return records.ToArray();
        }

        private void OpenSavedLists()
        {
            if(busy) return;
            try
            {
                var messages=new List<string>();
                var lists=savedLists.Read(message=>{log(message);messages.Add(message);});
                ui?.ShowLists(lists,messages.Count==0 ? "Save your ticks. Load a set. Go fly." : "Some list files could not be read. Check the BepInEx log.");
            }
            catch(Exception ex) { ListFailure("Could not open saved lists",ex); }
        }

        private void SaveModList(string name)
        {
            if(busy) return;
            try
            {
                string title=(name??"").Trim();
                SavedModLists.ValidateText(title,80,false,"list name");
                var snapshot=ListSnapshot(entries);
                // Validate before asking to replace anything, and freeze the ticks the player chose.
                new SavedModList { Name=title,GameVersion=ListGameVersion(),Mods=snapshot }.Validate();
                var matches=savedLists.Read(log).Where(l=>l.Name.Equals(title,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(matches.Length>1) { ui?.ListsStatus("Several imported lists have that name. Use a different name to save this selection.");return; }
                if(matches.Length==1)
                {
                    var previous=matches[0];
                    ui?.PromptConfirmation("REPLACE MOD LIST?","Replace '"+previous.Name+"' with your current ticks? The previous file is kept as a backup.","Replace",()=>WriteModList(title,snapshot,previous.FilePath));
                }
                else WriteModList(title,snapshot,null);
            }
            catch(Exception ex) { ListFailure("Could not save this list",ex); }
        }

        private void WriteModList(string name,SavedModItem[] snapshot,string? replacePath)
        {
            if(busy || ui?.IsOpen!=true || ui.ListsOpen!=true) return;
            try
            {
                var inventory=ObservedListContent();
                var messages=new List<string>();
                string game=ListGameVersion();
                busy=true;ui.Busy(true,"Saving your mod list...");
                var task=Task.Run(()=>
                {
                    try { knownContent.Save(inventory); }
                    catch(Exception ex) { messages.Add("Saved-list content receipt unavailable: "+ex.Message); }
                    return savedLists.Save(name,game,snapshot,replacePath);
                },updateLifetime.Token);
                owner.StartCoroutine(FinishSaveList(task,name,messages));
            }
            catch(Exception ex) { ListFailure("Could not save this list",ex); }
        }

        private IEnumerator FinishSaveList(Task<SavedModList> task,string name,List<string> messages)
        {
            while(!task.IsCompleted) yield return null;
            busy=false;ui?.Busy(false);
            foreach(var message in messages)log(message);
            if(task.IsCanceled) { ui?.ListsStatus("Saving was cancelled.");yield break; }
            if(task.IsFaulted) { ListFailure("Could not save this list",task.Exception!.GetBaseException());yield break; }
            if(ui?.ListsOpen==true) ui.ShowLists(savedLists.Read(log),"Saved '"+name+"'. Share its JSON file from BepInEx/plugins/DOORMAN-Lists.");
        }

        private SavedModList? CurrentSavedList(SavedModList selected)
        {
            var current=savedLists.Read(log).FirstOrDefault(l=>l.FilePath.Equals(selected.FilePath,StringComparison.OrdinalIgnoreCase));
            if(current==null) ui?.ShowLists(savedLists.Read(log),"That list was moved or changed into an unreadable file. Select a valid list again.");
            return current;
        }

        private void LoadModList(SavedModList selected)
        {
            if(busy) return;
            try
            {
                var current=CurrentSavedList(selected);if(current==null)return;
                if(current.Mods.Any(m=>m.Content&&!entries.Any(e=>e.Content&&e.Id==m.Id)))
                {
                    busy=true;ui?.Busy(true,"Checking installed content from earlier saved lists...");
                    var messages=new List<string>();
                    var task=Task.Run(()=>knownContent.Read(message=>messages.Add(message)),updateLifetime.Token);
                    owner.StartCoroutine(FinishKnownContent(task,messages,current));
                }
                else PrepareSavedList(current,false);
            }
            catch(Exception ex) { ListFailure("Could not load this list",ex); }
        }

        private IEnumerator FinishKnownContent(Task<ContentRecord[]> task,List<string> messages,SavedModList list)
        {
            bool cancelled=false;
            while(!task.IsCompleted) { if(ui?.ListsOpen!=true)cancelled=true;yield return null; }
            busy=false;ui?.Busy(false);
            foreach(var message in messages) log(message);
            if(cancelled || ui?.ListsOpen!=true) yield break;
            if(task.IsCanceled) { ui.ListsStatus("List loading was cancelled.");yield break; }
            if(task.IsFaulted) { ListFailure("Could not check installed content",task.Exception!.GetBaseException());yield break; }
            foreach(var record in task.Result)
            {
                if(entries.Any(e=>e.Content&&e.Id==record.Id)) continue;
                string source=record.Source.StartsWith("resource:",StringComparison.Ordinal) ? record.Source : Path.Combine(Paths.PluginPath,record.RelativeFile);
                var entry=new ModEntry { Content=true,Id=record.Id,Name=record.Name,Version=record.Version,Path=source,Enabled=false,Wanted=!profile.DisabledContent.Contains(record.Id,StringComparer.Ordinal) };
                if(source.StartsWith("resource:",StringComparison.Ordinal))
                {
                    string assembly=source.Split(':').ElementAtOrDefault(1)??"";
                    var wrappers=entries.Where(e=>!e.Content&&e.AssemblyName==assembly&&ServerSetupFiles.RelativeContent(Paths.PluginPath,e.Path).Equals(record.RelativeFile,StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                    if(wrappers.Length!=1) continue;
                    entry.WrapperPlugin=wrappers[0].Id;entry.WrapperPath=wrappers[0].Path;entry.AssemblyName=assembly;
                }
                entries.Add(entry);
            }
            ui.Set(entries,profile.AutoMatchServers,"Choose your mods, then press Reload mods.");
            PrepareSavedList(list,false);
        }

        private void PrepareSavedList(SavedModList list,bool acceptedWarnings)
        {
            if(busy || ui?.IsOpen!=true || ui.ListsOpen!=true) return;
            string reason=MenuSafety();
            if(reason.Length==0 && !loader.LoadingFinished) reason="Wait for Blueprinter to finish loading before applying mods.";
            if(reason.Length>0) { ui.ListsStatus(reason);return; }
            var plan=SavedModListPlan.Create(list,ListSnapshot(entries));
            if(!plan.CanApply)
            {
                ui.ListsStatus(plan.Reason+" If an installed pack's supporting plugin is disabled and has never been seen here, enable it and restart once so Blueprinter can discover it.");return;
            }
            var warnings=plan.Warnings.ToList();
            string game=ListGameVersion();
            if(list.GameVersion.Length>0 && game.Length>0 && list.GameVersion!=game)
                warnings.Insert(0,"Saved game version "+list.GameVersion+"; you have "+game+". Loading keeps your installed game version.");
            if(warnings.Count>0 && !acceptedWarnings)
            {
                string details=string.Join("\n",warnings.Take(6));
                if(warnings.Count>6) details+="\nAnd "+(warnings.Count-6)+" more version differences.";
                ui.PromptConfirmation("MOD LIST VERSIONS DIFFER",details+"\n\nLoad using the versions you have installed?","Load anyway",()=>PrepareSavedList(list,true));return;
            }
            var desired=plan.Selection.ToDictionary(item=>item.Key,StringComparer.Ordinal);
            var old=entries.Select(e=>e.Wanted).ToArray();
            foreach(var entry in entries)
            {
                if(!desired.TryGetValue((entry.Content?"content:":"plugin:")+entry.Id,out var value)) continue;
                if(entry.Locked && entry.Wanted!=value.Enabled) { ui.ListsStatus("Install the full client package and its helpers to change plugin selections.");return; }
            }
            foreach(var entry in entries)
                if(desired.TryGetValue((entry.Content?"content:":"plugin:")+entry.Id,out var value)) entry.Wanted=value.Enabled;
            reason=ModCatalog.ValidatePlugins(entries);
            if(reason.Length==0)
            {
                foreach(var plugin in entries.Where(e=>!e.Content&&e.Wanted))
                {
                    string? missing=plugin.Requires.FirstOrDefault(id=>entries.Count(e=>!e.Content&&e.Id==id)!=1);
                    if(missing!=null) {reason=plugin.Name+" needs one installed copy of "+missing+". Install or resolve that dependency first.";break;}
                }
            }
            if(reason.Length==0)
            {
                var unwired=entries.FirstOrDefault(e=>e.Content&&e.Wanted&&e.WrapperPlugin.Length>0&&!entries.Any(p=>!p.Content&&p.Id==e.WrapperPlugin&&p.Wanted));
                if(unwired!=null) reason=unwired.Name+" needs its supporting plugin enabled.";
            }
            if(reason.Length>0)
            {
                for(int i=0;i<entries.Count;i++) entries[i].Wanted=old[i];
                ui.ListsStatus(reason+" No mod selections were changed.");return;
            }
            ui.Set(entries,profile.AutoMatchServers,"Saved list selected. Reload mods to apply.");ui.HideLists();
            // This is the same transaction as Reload mods, including update queues,
            // mission checks, rollback and an explicit restart confirmation.
            listDisabledContent=plan.DisabledContent;ApplySelection();
        }

        private void DeleteModList(SavedModList selected)
        {
            if(busy) return;
            try
            {
                var current=CurrentSavedList(selected);if(current==null)return;
                ui?.PromptConfirmation("DELETE MOD LIST?","Delete '"+current.Name+"'? Your installed mods stay where they are. A backup of the list is kept.","Delete",()=>
                {
                    if(busy || ui?.ListsOpen!=true) return;
                    try { savedLists.Delete(current);ui.ShowLists(savedLists.Read(log),"Deleted '"+current.Name+"'."); }
                    catch(Exception ex) { ListFailure("Could not delete this list",ex); }
                });
            }
            catch(Exception ex) { ListFailure("Could not delete this list",ex); }
        }

        private void ListFailure(string action,Exception ex)
        {
            log(action+": "+ex.Message);
            if(ui?.ListsOpen==true) ui.ListsStatus(action+": "+ex.Message);
            else ui?.Status(action+": "+ex.Message);
        }
    }
}
