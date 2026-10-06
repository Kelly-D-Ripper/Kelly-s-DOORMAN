using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KellysJOINCHECK;

internal static class SavedListRegression
{
    internal static void Run(Action<bool,string> check)
    {
        SelectionChecks(check);
        StorageChecks(check);
    }

    private static SavedModItem Item(bool content,string id,string version="1",bool enabled=true,string name="") => new SavedModItem { Content=content, Id=id, Name=name.Length==0 ? id : name, Version=version, Enabled=enabled };
    private static SavedModList List(params SavedModItem[] mods) => new SavedModList { Name="Flight night", GameVersion="0.34.2", Mods=mods };
    private static void Reject(Action<bool,string> check,Action action,string message)
    {
        bool rejected=false;
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is ArgumentException) { rejected=true; }
        check(rejected,message);
    }

    private static void SelectionChecks(Action<bool,string> check)
    {
        var installed=new[]
        {
            Item(true,"pack.a","1",false), Item(true,"pack.b","2"), Item(true,"extra.pack"),
            Item(false,"hud.mod","1",false), Item(false,"unrelated.enabled"), Item(false,"unrelated.disabled","1",false)
        };
        var saved=List(Item(true,"pack.a"),Item(true,"pack.b","2",false),Item(false,"hud.mod"),Item(true,"absent.disabled","1",false));
        var plan=SavedModListPlan.Create(saved,installed);
        check(plan.CanApply && plan.Warnings.Length==0 && plan.Selection.Length==installed.Length,"saved list yields a complete installed selection and ignores absent disabled entries");
        check(plan.Selection.Single(m=>m.Id=="pack.a").Enabled && !plan.Selection.Single(m=>m.Id=="pack.b").Enabled && !plan.Selection.Single(m=>m.Id=="extra.pack").Enabled,"saved list enables requested content and disables listed or extra content");
        check(plan.DisabledContent.SequenceEqual(new[]{"absent.disabled","extra.pack","pack.b"}),"the complete disabled content set retains absent disabled packs for the next supporting-plugin startup scan");
        check(!plan.DisabledContent.Contains("pack.a",StringComparer.Ordinal) && !plan.DisabledContent.Contains("hud.mod",StringComparer.Ordinal),"enabled content and plugin-only identities are never added to disabled content");
        check(plan.Selection.Single(m=>m.Id=="hud.mod").Enabled && plan.Selection.Single(m=>m.Id=="unrelated.enabled").Enabled && !plan.Selection.Single(m=>m.Id=="unrelated.disabled").Enabled,"listed plugins use saved state while unrelated plugin selections are preserved");
        check(!installed[0].Enabled && saved.Mods[0].Enabled,"planning never mutates installed or saved selections");
        plan.Selection[0].Name="changed";plan.Selection[0].Enabled=false;
        check(installed[0].Name=="pack.a" && saved.Mods[0].Name=="pack.a" && saved.Mods[0].Enabled,"a returned plan has independent item snapshots");

        var missing=SavedModListPlan.Create(List(Item(true,"pack.a"),Item(false,"missing.plugin")),installed);
        check(!missing.CanApply && missing.Selection.Length==0 && missing.DisabledContent.Length==0 && missing.Reason.Contains("missing.plugin"),"one missing enabled plugin blocks the entire saved list before any selection or disabled content is returned");
        check(!SavedModListPlan.Create(List(Item(true,"missing.pack")),installed).CanApply,"a missing enabled content pack blocks list loading");
        var versions=SavedModListPlan.Create(List(Item(true,"pack.a","9"),Item(false,"hud.mod","2")),installed);
        check(versions.CanApply && versions.Warnings.Length==2 && versions.Warnings.All(w=>w.Contains("installed version")),"enabled content and plugin version differences require explicit UI acknowledgement through warnings");
        check(versions.Selection.Single(m=>m.Id=="pack.a").Version=="1" && versions.Selection.Single(m=>m.Id=="hud.mod").Version=="1","version warnings never pretend to install a saved version");
        check(SavedModListPlan.Create(List(Item(true,"pack.a","9",false)),installed).Warnings.Length==0,"version differences for disabled items do not produce compatibility warnings");
        check(!SavedModListPlan.Create(List(Item(true,"pack.a"),Item(true,"pack.a","2")),installed).CanApply,"duplicate saved IDs block instead of picking a version");
        check(!SavedModListPlan.Create(saved,installed.Concat(new[]{Item(true,"pack.a","2")})).CanApply,"duplicate installed IDs block even when their versions differ");
        check(SavedModListPlan.Create(List(Item(true,"shared.id"),Item(false,"shared.id")),new[]{Item(true,"shared.id"),Item(false,"shared.id")}).CanApply,"content and plugin identities occupy distinct namespaces");
        check(!SavedModListPlan.Create(saved,new SavedModItem[]{null!}).CanApply && !SavedModListPlan.Create(new SavedModList { Name="bad", Mods=null! },installed).CanApply,"invalid saved or installed snapshots fail closed");
        check(!SavedModListPlan.Create(saved,Enumerable.Range(0,SavedModLists.MaximumMods+1).Select(i=>Item(true,"pack."+i))).CanApply,"installed inventory enumeration is bounded");

        foreach (string id in new[]{"kelly.nuclearoption.joincheck","kelly.nuclearoption.joincheck.server","com.nikkorap.blueprinter","COM.NIKKORAP.BLUEPRINTER"})
        {
            var protectedPlan=SavedModListPlan.Create(List(),new[]{Item(false,id,"1",false)});
            check(protectedPlan.CanApply && protectedPlan.Selection.Single().Enabled,"required plugin snapshots remain enabled: "+id);
            check(!SavedModListPlan.Create(List(Item(false,id,"1",false)),Array.Empty<SavedModItem>()).CanApply,"imported lists cannot disable a required plugin: "+id);
            check(SavedModListPlan.Create(List(Item(false,id)),Array.Empty<SavedModItem>()).CanApply,"enabled locked plugins omitted by the UI do not become false missing-mod errors: "+id);
        }
    }

    private static void StorageChecks(Action<bool,string> check)
    {
        string temp=Path.Combine(Path.GetTempPath(),"doorman-saved-lists-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string plugins=Path.Combine(temp,"plugins"), peerPlugins=Path.Combine(temp,"peer","plugins");
            var store=new SavedModLists(plugins);
            var logs=new List<string>();
            check(store.DirectoryPath==Path.Combine(plugins,"DOORMAN-Lists") && !Directory.Exists(store.DirectoryPath),"constructing the saved-list store does not create folders");
            check(store.Read(logs.Add).Length==0 && Directory.Exists(store.DirectoryPath),"explicitly opening saved lists creates the import destination before the first save");
            var mods=new[]{Item(true,"pack.a","1"),Item(false,"hud.mod","2",false)};
            var first=store.Save("Flight night: PvP","0.34.2",mods);
            check(Path.GetFileName(first.FilePath).StartsWith("Flight-night-PvP-",StringComparison.Ordinal) && first.FilePath.EndsWith(SavedModLists.Suffix,StringComparison.Ordinal),"saved lists use readable safe basenames and the shareable extension");
            string json=File.ReadAllText(first.FilePath);
            using (var parsed=JsonDocument.Parse(json))
            {
                check(parsed.RootElement.EnumerateObject().Select(p=>p.Name).SequenceEqual(new[]{"schema","name","gameVersion","mods"}),"share files contain only schema, display name, game version and mod snapshots");
                check(parsed.RootElement.GetProperty("mods")[0].EnumerateObject().Select(p=>p.Name).SequenceEqual(new[]{"content","id","name","version","enabled"}),"share items contain stable identity and selection without local paths or download URLs");
            }
            check(!json.Contains(temp,StringComparison.OrdinalIgnoreCase) && !json.Contains("FilePath",StringComparison.Ordinal),"local list receipt and plugin folder are never serialized");
            mods[0].Enabled=false;
            check(first.Mods[0].Enabled,"saving copies the caller's mutable snapshots");
            var roundtrip=store.Read(logs.Add).Single();
            check(roundtrip.Schema==1 && roundtrip.Name==first.Name && roundtrip.GameVersion=="0.34.2" && roundtrip.Mods[0].Enabled && !roundtrip.Mods[1].Enabled && roundtrip.FilePath==first.FilePath,"saved item identity, version, enabled state and local receipt roundtrip");

            var same=store.Save(first.Name,"0.34.2",first.Mods);
            check(first.FilePath!=same.FilePath && File.ReadAllText(first.FilePath)==json,"same-name saves never overwrite without an explicit replacement path");
            var replacement=store.Save("Renamed flight","0.34.2",new[]{Item(true,"pack.b","3")},first.FilePath);
            check(replacement.FilePath==first.FilePath && File.ReadAllText(first.FilePath+".bak")==json && store.Read(logs.Add).Length==2,"explicit replacement is atomic and keeps the previous list as a backup");
            Reject(check,()=>store.Save("New","0.34.2",first.Mods,Path.Combine(store.DirectoryPath,"missing"+SavedModLists.Suffix)),"a stale replacement path cannot silently become a new list");
            Reject(check,()=>store.Save("New","0.34.2",first.Mods,Path.Combine(temp,"outside"+SavedModLists.Suffix)),"replacement cannot escape the saved-list folder");
            Reject(check,()=>store.Delete(new SavedModList { Name="Forged", FilePath=Path.Combine(temp,"outside"+SavedModLists.Suffix) }),"a forged delete receipt cannot escape the saved-list folder");
            Reject(check,()=>store.Save("New","0.34.2",first.Mods,first.FilePath+".bak"),"a backup cannot be selected as a share-list replacement target");

            var peer=new SavedModLists(peerPlugins);
            Directory.CreateDirectory(peer.DirectoryPath);
            string importedPath=Path.Combine(peer.DirectoryPath,"community"+SavedModLists.Suffix);
            File.Copy(same.FilePath,importedPath);
            var imported=peer.Read(logs.Add).Single();
            check(imported.Name==same.Name && imported.Mods[0].Id=="pack.a" && imported.FilePath==importedPath && SavedModListPlan.Create(imported,new[]{Item(true,"pack.a"),Item(false,"hud.mod","2")}).CanApply,"a copied share file imports under a different plugin root without rebinding paths");
            peer.Delete(imported);
            check(!File.Exists(importedPath) && File.Exists(importedPath+".bak") && peer.Read(logs.Add).Length==0,"deleting a list retains recovery bytes and removes it from the picker");
            File.WriteAllText(Path.Combine(peer.DirectoryPath,"zeta"+SavedModLists.Suffix),json);
            peer.Save("alpha","0.34.2",Array.Empty<SavedModItem>());
            check(peer.Read(logs.Add).Select(l=>l.Name).SequenceEqual(new[]{"alpha",first.Name}),"saved lists sort by display name rather than source filename");
            File.WriteAllText(Path.Combine(peer.DirectoryPath,"broken"+SavedModLists.Suffix),"{broken");
            File.WriteAllText(Path.Combine(peer.DirectoryPath,"oversize"+SavedModLists.Suffix),new string('x',SavedModLists.MaximumBytes+1));
            File.WriteAllText(Path.Combine(peer.DirectoryPath,"schema"+SavedModLists.Suffix),"{\"schema\":999,\"name\":\"old\",\"gameVersion\":\"0.34.2\",\"mods\":[]}");
            File.WriteAllText(Path.Combine(peer.DirectoryPath,"duplicate"+SavedModLists.Suffix),"{\"schema\":1,\"name\":\"bad\",\"gameVersion\":\"0.34.2\",\"mods\":[{\"content\":true,\"id\":\"a\",\"name\":\"a\",\"version\":\"1\",\"enabled\":true},{\"content\":true,\"id\":\"a\",\"name\":\"a\",\"version\":\"2\",\"enabled\":true}]}");
            int before=logs.Count;
            check(peer.Read(logs.Add).Length==2 && logs.Count==before+4,"invalid, oversized, unsupported and duplicate imports are skipped and logged");
            Reject(check,()=>peer.Save("New","0.34.2",Array.Empty<SavedModItem>(),Path.Combine(peer.DirectoryPath,"broken"+SavedModLists.Suffix)),"explicit replacement rejects a damaged selected file");
            File.WriteAllText(Path.Combine(peer.DirectoryPath,"missing-selection"+SavedModLists.Suffix),"{\"schema\":1,\"name\":\"Incomplete\",\"gameVersion\":\"0.34.2\",\"mods\":[{\"id\":\"a\",\"name\":\"a\",\"version\":\"1\"}]}");
            check(peer.Read(logs.Add).Length==2,"imports must explicitly record item type and enabled state");
            File.WriteAllText(Path.Combine(peer.DirectoryPath,"minimal"+SavedModLists.Suffix),"{\"schema\":1,\"name\":\"Minimal\",\"mods\":[{\"content\":true,\"id\":\"pack.a\",\"enabled\":true}]}");
            var minimal=peer.Read(logs.Add).Single(l=>l.Name=="Minimal");
            check(minimal.GameVersion=="" && minimal.Mods[0].Name=="" && minimal.Mods[0].Version=="","optional display and version metadata default safely without changing explicit selections");
            var unknownVersion=SavedModListPlan.Create(minimal,new[]{Item(true,"pack.a","1")});
            check(unknownVersion.CanApply && unknownVersion.Warnings.Single().Contains("saved version unknown"),"an imported enabled item with an omitted version warns before loading the installed version");

            foreach (string id in new[]{"", " ", "../pack.dll", "C:\\plugins\\mod.dll", "https://example.com/mod.dll", "bad\nmod"})
                Reject(check,()=>store.Save("Unsafe","0.34.2",new[]{Item(true,id)}),"saved lists reject unstable or unsafe identifiers: "+id.Replace('\n',' '));
            foreach (string name in new[]{"", " ", "bad\nname", "C:\\plugins\\mod.dll", "https://example.com/mod", new string('x',81)})
                Reject(check,()=>store.Save(name,"0.34.2",Array.Empty<SavedModItem>()),"saved list names reject invalid, oversized, path or URL text");
            Reject(check,()=>store.Save("Too many","0.34.2",Enumerable.Range(0,SavedModLists.MaximumMods+1).Select(i=>Item(true,"pack."+i))),"saved item enumeration and item counts are bounded");
            Reject(check,()=>store.Save("Duplicate","0.34.2",new[]{Item(false,"hud.mod"),Item(false,"hud.mod","2")}),"duplicate IDs cannot be written as shareable lists");
            Reject(check,()=>store.Save("Too large","0.34.2",Enumerable.Range(0,300).Select(i=>Item(true,"pack."+i,new string('v',128),true,new string('n',160)))),"serialized byte count is bounded independently from item count");
            check(!Directory.EnumerateFiles(store.DirectoryPath).Any(f=>f.EndsWith(".tmp",StringComparison.Ordinal)),"failed saves leave no temporary payloads");
            CheckReparsePoints(check,store,temp,first.Mods,logs);
            var many=new SavedModLists(Path.Combine(temp,"many"));
            Directory.CreateDirectory(many.DirectoryPath);
            for (int i=0;i<SavedModLists.MaximumLists+1;i++) File.WriteAllText(Path.Combine(many.DirectoryPath,"list-"+i+SavedModLists.Suffix),json);
            before=logs.Count;
            check(many.Read(logs.Add).Length==SavedModLists.MaximumLists && logs.Count==before+1,"saved list file enumeration stops at the display limit and logs extra files");
            Reject(check,()=>many.Save("Another","0.34.2",Array.Empty<SavedModItem>()),"a full list folder rejects new saves instead of creating invisible picker entries");
            var atLimit=many.Read(logs.Add);
            many.Save("Replaced at limit","0.34.2",Array.Empty<SavedModItem>(),atLimit[0].FilePath);
            check(File.Exists(atLimit[0].FilePath+".bak"),"explicit replacements remain available when the list folder is at capacity");
            many.Delete(atLimit[0]);
            many.Delete(atLimit[1]);
            many.Save("Capacity recovered","0.34.2",Array.Empty<SavedModItem>());
            check(many.Read(logs.Add).Length==SavedModLists.MaximumLists && many.Read(logs.Add).Any(l=>l.Name=="Capacity recovered"),"deleting saved files frees capacity while recovery backups do not count toward the picker limit");
        }
        finally
        {
            string expected=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(temp).StartsWith(expected,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped the temporary folder.");
            Directory.Delete(temp,true);
        }
    }

    private static void CheckReparsePoints(Action<bool,string> check,SavedModLists store,string temp,SavedModItem[] mods,List<string> logs)
    {
        string target=Path.Combine(temp,"link-target"+SavedModLists.Suffix), link=Path.Combine(store.DirectoryPath,"linked"+SavedModLists.Suffix);
        File.WriteAllText(target,"{\"schema\":1,\"name\":\"Linked\",\"gameVersion\":\"0.34.2\",\"mods\":[]}");
        bool created=false;
        try
        {
            try { File.CreateSymbolicLink(link,target); created=true; }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException)
            { Console.WriteLine("Saved-list reparse check unavailable on this host: "+ex.GetType().Name); return; }
            check(!store.Read(logs.Add).Any(l=>l.Name=="Linked"),"linked share files are rejected before deserialization");
            Reject(check,()=>store.Save("Replacement","0.34.2",mods,link),"explicit replacement cannot follow a linked share file");
            var backupList=store.Save("Linked backup","0.34.2",mods);
            string backupLink=backupList.FilePath+".bak";
            try
            {
                File.CreateSymbolicLink(backupLink,target);
                Reject(check,()=>store.Save("Replacement","0.34.2",mods,backupList.FilePath),"replacement refuses a linked backup destination");
                Reject(check,()=>store.Delete(backupList),"deletion refuses a linked backup destination");
            }
            finally { if (File.Exists(backupLink)) File.Delete(backupLink); }
            string linkedPlugins=Path.Combine(temp,"linked-plugins");
            try
            {
                Directory.CreateSymbolicLink(linkedPlugins,Path.GetDirectoryName(store.DirectoryPath)!);
                var linkedStore=new SavedModLists(linkedPlugins);
                check(linkedStore.Read(logs.Add).Length==0,"linked plugin-root ancestors cannot be used to read lists");
                Reject(check,()=>linkedStore.Save("Linked root","0.34.2",mods),"linked plugin-root ancestors cannot be used to save lists");
            }
            finally { if (Directory.Exists(linkedPlugins)) Directory.Delete(linkedPlugins); }
        }
        finally { if (created) File.Delete(link); }
    }
}
