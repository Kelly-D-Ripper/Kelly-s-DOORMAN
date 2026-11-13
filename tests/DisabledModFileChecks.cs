using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using KellysJOINCHECK;

internal static class DisabledModFileRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string temporary=Path.Combine(Path.GetTempPath(),"doorman-disabled-check-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        try
        {
            DiscoveryAndRestore(check,Root(temporary,"basic"));
            LargeWholeUnit(check,Root(temporary,"large"));
            ConflictsAndChanges(check,Root(temporary,"conflicts"));
            RetryAndRollback(check,Root(temporary,"retry"));
            Addons(check,Root(temporary,"addons"));
            BoundsAndPaths(check,Root(temporary,"bounds"),temporary);
        }
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(temporary).StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(temporary))Directory.Delete(temporary,true);
        }
    }

    private static string Root(string temporary,string name)
    {
        string root=Path.Combine(temporary,name,"BepInEx");Directory.CreateDirectory(Path.Combine(root,"disabledPlugins"));return root;
    }
    private static string Disabled(string root,string relative)=>Path.Combine(root,"disabledPlugins",relative);
    private static string Active(string root,string relative)=>Path.Combine(root,"plugins",relative);
    private static void FileAt(string path,string text)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,text); }
    private static DisabledModCandidate Candidate(string root,string leaf)=>DisabledModFiles.Inventory(root).Single(c=>c.SourceRelative==leaf);
    private static DisabledModRestore Receipt(string root,string leaf)=>DisabledModFiles.Prepare(root,Candidate(root,leaf));
    private static void Reject(Action<bool,string> check,Action action,string name)
    {
        bool rejected=false;
        try{action();}catch(Exception ex) when(ex is InvalidDataException||ex is IOException||ex is ArgumentException){rejected=true;}
        check(rejected,name);
    }

    private static void DiscoveryAndRestore(Action<bool,string> check,string root)
    {
        FileAt(Disabled(root,"plain.dll"),"DLL bytes, never executed");
        FileAt(Disabled(root,"aircraft.nobp"),"standalone content");
        FileAt(Disabled(root,"FullPack/main.dll"),"main bytes");
        FileAt(Disabled(root,"FullPack/nested/content.nobp"),"embedded assets");
        FileAt(Disabled(root,"FullPack/doorman.json"),"{\"id\":\"fullpack\"}");
        FileAt(Disabled(root,"FullPack/image.png"),"image bytes");
        FileAt(Disabled(root,"FullPack/config/private.json"),"mod asset settings");
        Directory.CreateDirectory(Disabled(root,"FullPack/empty"));
        FileAt(Disabled(root,"Unrelated.dll"),"leave disabled");
        var inventory=DisabledModFiles.Inventory(root);
        check(inventory.Length==4,"NOMM direct-child DLL, .nobp and complete directory units are discovered");
        check(inventory.Single(c=>c.SourceRelative=="FullPack").Files.Length==5,"directory discovery retains every DLL, bundle, JSON and image asset");
        check(inventory.All(c=>Path.IsPathRooted(c.SourcePath)&&c.Files.All(Path.IsPathRooted)),"disabled metadata sources have absolute paths");
        check(inventory.All(c=>c.SourceRelative==c.TargetRelative),"ordinary NOMM units restore under their exact original leaf names");
        var plain=Receipt(root,"plain.dll");var bundle=Receipt(root,"aircraft.nobp");var pack=Receipt(root,"FullPack");
        check(plain.Digest.Length==64&&bundle.Digest.Length==64&&pack.Digest.Length==64,"selected units receive bounded SHA256 receipts");
        using(var serialized=new MemoryStream())
        {
            var serializer=new DataContractJsonSerializer(typeof(DisabledModRestore));serializer.WriteObject(serialized,pack);serialized.Position=0;
            var recovered=(DisabledModRestore)serializer.ReadObject(serialized)!;recovered.Validate();
            check(recovered.SourceRelative==pack.SourceRelative&&recovered.TargetRelative==pack.TargetRelative&&recovered.Digest==pack.Digest,"disabled-mod receipts preserve exact selected units through durable JSON serialization");
        }
        using(var serialized=new MemoryStream(Encoding.UTF8.GetBytes("{\"SourceRelative\":\"plain.dll\",\"TargetRelative\":\"plain.dll\",\"Digest\":\""+plain.Digest+"\"}")))
        {
            var recovered=(DisabledModRestore)new DataContractJsonSerializer(typeof(DisabledModRestore)).ReadObject(serialized)!;recovered.Validate();
            check(recovered.PluginIds.Length==0,"restore receipts can omit optional plugin identities");
        }
        check(Receipt(root,"FullPack").Digest==pack.Digest,"whole-unit fingerprints are deterministic");
        check(!Directory.Exists(Path.Combine(root,"plugins"))&&inventory.All(c=>File.Exists(c.SourcePath)||Directory.Exists(c.SourcePath)),"discovery and prepare perform no filesystem writes or moves");
        DisabledModFiles.Verify(root,new[]{plain,bundle,pack});
        check(!Directory.Exists(Path.Combine(root,"plugins")),"receipt preflight remains read-only");
        DisabledModFiles.Apply(root,new[]{plain,bundle,pack});
        check(File.ReadAllText(Active(root,"plain.dll"))=="DLL bytes, never executed"&&File.ReadAllText(Active(root,"aircraft.nobp"))=="standalone content","selected DLL and .nobp restore without changing bytes");
        check(File.ReadAllText(Active(root,"FullPack/image.png"))=="image bytes"&&File.ReadAllText(Active(root,"FullPack/config/private.json"))=="mod asset settings","whole-directory restoration preserves companion assets");
        check(Directory.Exists(Active(root,"FullPack/empty")),"empty asset folders move with their complete NOMM unit");
        check(!File.Exists(Disabled(root,"plain.dll"))&&!Directory.Exists(Disabled(root,"FullPack")),"restoration moves complete units instead of copying duplicate mods");
        check(File.ReadAllText(Disabled(root,"Unrelated.dll"))=="leave disabled"&&!File.Exists(Active(root,"Unrelated.dll")),"unselected NOMM-disabled mods stay disabled");
        DisabledModFiles.Apply(root,new[]{plain,bundle,pack});
        check(File.Exists(Active(root,"plain.dll"))&&Directory.Exists(Active(root,"FullPack")),"repeated startup accepts exactly matching restored receipts");
        Reject(check,()=>DisabledModFiles.Inventory("relative/BepInEx"),"disabled-mod discovery cannot depend on process working directory");
        Reject(check,()=>DisabledModFiles.Inventory(Path.GetPathRoot(root)!),"a configured filesystem root cannot be used as BepInEx");
    }

    private static void LargeWholeUnit(Action<bool,string> check,string root)
    {
        string large=Disabled(root,"F117Pack/blacknight2u.f117a.nighthawk.nobp");
        LargeInstalledContentFixture.Create(large);FileAt(Disabled(root,"F117Pack/support.json"),"large-pack companion assets");FileAt(Disabled(root,"Unrelated.dll"),"stay disabled");
        var candidate=Candidate(root,"F117Pack");
        check(candidate.Files.Length==2&&new FileInfo(large).Length==LargeInstalledContentFixture.ReportedBytes,"whole-unit discovery includes the exact reported F-117 bundle size and its companion assets without reading bulk bytes");
        var receipt=DisabledModFiles.Prepare(root,candidate);DisabledModFiles.Verify(root,new[]{receipt});
        check(receipt.Digest.Length==64&&File.Exists(large)&&!Directory.Exists(Path.Combine(root,"plugins")),"a report-sized NOMM bundle prepares and verifies a complete read-only restore receipt through streamed hashing");
        check(DisabledRestoreQueue.SourceFor(root,new[]{receipt},"F117Pack/blacknight2u.f117a.nighthawk.nobp")==Path.GetFullPath(large),"projected source mapping accepts the same large installed-source policy as whole-unit verification");
        DisabledModFiles.Apply(root,new[]{receipt});DisabledModFiles.VerifyApplied(root,new[]{receipt});
        string restored=Active(root,"F117Pack/blacknight2u.f117a.nighthawk.nobp");
        check(new FileInfo(restored).Length==LargeInstalledContentFixture.ReportedBytes&&!Directory.Exists(Disabled(root,"F117Pack"))&&File.ReadAllText(Active(root,"F117Pack/support.json"))=="large-pack companion assets","a report-sized NOMM pack moves atomically with exact bundle bytes and companion assets");
        check(File.ReadAllText(Disabled(root,"Unrelated.dll"))=="stay disabled"&&!File.Exists(Active(root,"Unrelated.dll")),"large-unit restoration leaves unrelated disabled packages untouched");
        DateTime observedWrite=File.GetLastWriteTimeUtc(restored);
        using(var file=new FileStream(restored,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
        {file.Position=file.Length-1;int original=file.ReadByte();file.Position=file.Length-1;file.WriteByte((byte)(original^255));}
        File.SetLastWriteTimeUtc(restored,observedWrite);
        Reject(check,()=>DisabledModFiles.VerifyApplied(root,new[]{receipt}),"same-size same-timestamp changed bytes at the end of a report-sized restored bundle fail complete streamed receipt verification");
    }

    private static void ConflictsAndChanges(Action<bool,string> check,string root)
    {
        FileAt(Disabled(root,"First.dll"),"first");FileAt(Disabled(root,"Second.dll"),"second");
        var first=Receipt(root,"First.dll");var second=Receipt(root,"Second.dll");
        FileAt(Active(root,"Second.dll"),"existing active mod");
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{first,second}),"all restore destination collisions are checked before any move");
        check(File.Exists(Disabled(root,"First.dll"))&&!File.Exists(Active(root,"First.dll"))&&File.ReadAllText(Active(root,"Second.dll"))=="existing active mod","collision preflight preserves earlier selected units and existing active bytes");
        File.Delete(Active(root,"Second.dll"));
        DateTime previous=File.GetLastWriteTimeUtc(Disabled(root,"Second.dll"));File.WriteAllText(Disabled(root,"Second.dll"),"SECOND");File.SetLastWriteTimeUtc(Disabled(root,"Second.dll"),previous);
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{first,second}),"same-size and same-timestamp changed disabled bytes reject restoration");
        check(File.Exists(Disabled(root,"First.dll"))&&!File.Exists(Active(root,"First.dll")),"source tampering in any selected unit moves none of the other units");
        FileAt(Disabled(root,"Pack/main.dll"),"main");var pack=Receipt(root,"Pack");
        FileAt(Disabled(root,"Pack/new.json"),"new asset");
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{pack}),"added files change a directory restore receipt");
        File.Delete(Disabled(root,"Pack/new.json"));File.Delete(Disabled(root,"Pack/main.dll"));FileAt(Disabled(root,"Pack/renamed.dll"),"main");
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{pack}),"renamed relative paths change a directory restore receipt even with identical bytes");
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{first,first}),"duplicate source receipts are rejected");
        var duplicateTarget=new DisabledModRestore {SourceRelative="Second.dll",TargetRelative="First.dll",Digest=second.Digest};
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{first,duplicateTarget}),"two source units cannot restore onto one destination");
        FileAt(Active(root,"case.dll"),"case occupant");FileAt(Disabled(root,"CASE.dll"),"disabled case");
        Reject(check,()=>Receipt(root,"CASE.dll"),"case-only destination ambiguity is rejected across filesystems");
    }

    private static void RetryAndRollback(Action<bool,string> check,string root)
    {
        FileAt(Disabled(root,"One.dll"),"one");FileAt(Disabled(root,"Two.dll"),"two");
        var one=Receipt(root,"One.dll");var two=Receipt(root,"Two.dll");
        int moved=0;
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{one,two},message=>{if(++moved==1)throw new IOException("fixture interrupted apply");}),"an interrupted multi-unit application reports its failure");
        check(File.ReadAllText(Disabled(root,"One.dll"))=="one"&&File.ReadAllText(Disabled(root,"Two.dll"))=="two"&&!Directory.Exists(Path.Combine(root,"plugins")),"ordinary failure rolls back moved units and newly created empty destination scaffolding");
        Directory.CreateDirectory(Path.Combine(root,"plugins"));File.Move(Disabled(root,"One.dll"),Active(root,"One.dll"));
        DisabledModFiles.Apply(root,new[]{one,two});
        check(File.ReadAllText(Active(root,"One.dll"))=="one"&&File.ReadAllText(Active(root,"Two.dll"))=="two","a crash between atomic unit moves recovers the rest from durable receipts");
        FileAt(Disabled(root,"One.dll"),"one");
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{one}),"both source and destination present remains a collision even when bytes match");
        File.Delete(Disabled(root,"One.dll"));File.WriteAllText(Active(root,"One.dll"),"ONE");
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{one}),"crash recovery rejects a changed already-restored destination");
        FileAt(Disabled(root,"RaceA.dll"),"aaa");FileAt(Disabled(root,"RaceB.dll"),"bbb");
        var a=Receipt(root,"RaceA.dll");var b=Receipt(root,"RaceB.dll");
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{a,b},message=>File.WriteAllText(Disabled(root,"RaceB.dll"),"changed after preflight")),"a source changed between unit moves is checked again");
        check(File.ReadAllText(Disabled(root,"RaceA.dll"))=="aaa"&&File.ReadAllText(Disabled(root,"RaceB.dll"))=="changed after preflight"&&!File.Exists(Active(root,"RaceA.dll")),"mid-apply tampering rolls back earlier moves without deleting changed source bytes");
        FileAt(Disabled(root,"FinalA.dll"),"original");FileAt(Disabled(root,"FinalB.dll"),"other");
        var finalA=Receipt(root,"FinalA.dll");var finalB=Receipt(root,"FinalB.dll");int messages=0;
        Reject(check,()=>DisabledModFiles.Apply(root,new[]{finalA,finalB},message=>{if(++messages==2)File.WriteAllText(Active(root,"FinalA.dll"),"changed after move");}),"final receipt verification detects an earlier destination changed after a later move");
        check(File.ReadAllText(Disabled(root,"FinalA.dll"))=="changed after move"&&File.ReadAllText(Disabled(root,"FinalB.dll"))=="other"&&!File.Exists(Active(root,"FinalA.dll")),"final verification failure rolls back every new move and preserves intervening bytes");
    }

    private static void Addons(Action<bool,string> check,string root)
    {
        FileAt(Disabled(root,"Parent/main.dll"),"parent");FileAt(Disabled(root,"Addon/addon.nobp"),"addon");
        var parent=Receipt(root,"Parent");var candidate=Candidate(root,"Addon");candidate.TargetRelative="Parent/addons/verified.addon";var addon=DisabledModFiles.Prepare(root,candidate);
        DisabledModFiles.Apply(root,new[]{addon,parent});
        check(File.ReadAllText(Active(root,"Parent/main.dll"))=="parent"&&File.ReadAllText(Active(root,"Parent/addons/verified.addon/addon.nobp"))=="addon","metadata-verified addon targets restore after their selected parent whole unit");
        DisabledModFiles.Apply(root,new[]{parent,addon});
        check(File.Exists(Active(root,"Parent/main.dll")),"parent receipts verify on retry with separately receipted nested addons");
        string crash=Root(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!,"addon-crash");
        FileAt(Disabled(crash,"Parent/main.dll"),"parent");FileAt(Disabled(crash,"Addon/addon.nobp"),"addon");
        var crashParent=Receipt(crash,"Parent");var crashCandidate=Candidate(crash,"Addon");crashCandidate.TargetRelative="Parent/addons/verified.addon";var crashAddon=DisabledModFiles.Prepare(crash,crashCandidate);
        Directory.CreateDirectory(Path.Combine(crash,"plugins"));Directory.Move(Disabled(crash,"Parent"),Active(crash,"Parent"));
        DisabledModFiles.Apply(crash,new[]{crashParent,crashAddon});
        check(File.Exists(Active(crash,"Parent/addons/verified.addon/addon.nobp")),"crash after a parent move resumes its still-disabled addon");
        string collision=Root(Path.GetDirectoryName(Path.GetDirectoryName(root)!)!,"addon-collision");
        FileAt(Disabled(collision,"Parent/main.dll"),"parent");FileAt(Disabled(collision,"Parent/addons/taken/existing.nobp"),"existing");FileAt(Disabled(collision,"Addon/addon.nobp"),"addon");
        var occupiedParent=Receipt(collision,"Parent");var occupiedCandidate=Candidate(collision,"Addon");occupiedCandidate.TargetRelative="Parent/addons/taken";var occupiedAddon=DisabledModFiles.Prepare(collision,occupiedCandidate);
        Reject(check,()=>DisabledModFiles.Apply(collision,new[]{occupiedParent,occupiedAddon}),"a selected parent containing the addon destination is rejected before either unit moves");
        check(Directory.Exists(Disabled(collision,"Parent"))&&Directory.Exists(Disabled(collision,"Addon"))&&!Directory.Exists(Path.Combine(collision,"plugins")),"nested-addon collision preflight preserves both disabled units");
    }

    private static void BoundsAndPaths(Action<bool,string> check,string root,string temporary)
    {
        FileAt(Disabled(root,"Valid.dll"),"valid");var valid=Receipt(root,"Valid.dll");
        foreach(string leaf in new[]{"../Valid.dll","nested/Valid.dll","..","CON.dll","NUL","file.dll:stream","file.dll.","file.dll ",""})
        {
            var bad=new DisabledModRestore {SourceRelative=leaf,TargetRelative="Valid.dll",Digest=valid.Digest};
            Reject(check,()=>DisabledModFiles.Verify(root,new[]{bad}),"disabled source path rejects "+leaf);
        }
        foreach(string target in new[]{"../outside.dll","a/../outside.dll","a//b.dll","/absolute.dll","C:\\outside.dll","a/CON.dll","a/./b.dll","a\\..\\b.dll"})
        {
            var bad=new DisabledModRestore {SourceRelative="Valid.dll",TargetRelative=target,Digest=valid.Digest};
            Reject(check,()=>DisabledModFiles.Verify(root,new[]{bad}),"disabled target path rejects "+target);
        }
        var forged=Candidate(root,"Valid.dll");forged.SourcePath=Path.Combine(temporary,"outside.dll");
        Reject(check,()=>DisabledModFiles.Prepare(root,forged),"a cached candidate cannot substitute an outside source path");
        Reject(check,()=>DisabledModFiles.Verify(root,new[]{new DisabledModRestore {SourceRelative="Valid.dll",TargetRelative="Valid.dll",Digest="bad"}}),"malformed persisted fingerprints reject safely");
        check(DisabledModFiles.MaximumFileBytes==InstalledFilePolicy.MaximumBytes&&DisabledModFiles.MaximumFileBytes>462355664L,"NOMM physical-unit limits share the installed-source policy and allow the reported F-117 bundle size");
        Reject(check,()=>InstalledFilePolicy.ValidateLength(DisabledModFiles.MaximumFileBytes+1),"oversized disabled source lengths reject without creating or hashing a multi-gigabyte fixture");
        for(int i=0;i<=DisabledModFiles.MaximumFiles;i++)FileAt(Disabled(root,"ManyFiles/file"+i+".json"),"x");
        check(!DisabledModFiles.Inventory(root).Any(c=>c.SourceRelative=="ManyFiles"),"per-unit enumeration has a file limit");
        Reject(check,()=>DisabledModFiles.Prepare(root,new DisabledModCandidate {SourceRelative="ManyFiles",SourcePath=Disabled(root,"ManyFiles"),TargetRelative="ManyFiles"}),"over-limit directory units cannot be prepared");
        string link=Disabled(root,"Linked"),outside=Path.Combine(temporary,"external-assets");Directory.CreateDirectory(outside);FileAt(Path.Combine(outside,"external.dll"),"external");
        try
        {
            Directory.CreateSymbolicLink(link,outside);
            check(!DisabledModFiles.Inventory(root).Any(c=>c.SourceRelative=="Linked"),"discovery omits linked disabled units");
            Reject(check,()=>DisabledModFiles.Prepare(root,new DisabledModCandidate {SourceRelative="Linked",SourcePath=link,TargetRelative="Linked"}),"linked disabled source units cannot be prepared");
            string nested=Disabled(root,"NestedLink");Directory.CreateDirectory(nested);Directory.CreateSymbolicLink(Path.Combine(nested,"assets"),outside);
            check(!DisabledModFiles.Inventory(root).Any(c=>c.SourceRelative=="NestedLink"),"a linked asset inside a directory invalidates that complete unit");
            Directory.Delete(Path.Combine(nested,"assets"));
            Directory.CreateDirectory(Path.Combine(root,"plugins"));string destination=Active(root,"linked-parent");Directory.CreateSymbolicLink(destination,outside);
            var target=new DisabledModRestore {SourceRelative="Valid.dll",TargetRelative="linked-parent/Valid.dll",Digest=valid.Digest};
            Reject(check,()=>DisabledModFiles.Apply(root,new[]{target}),"linked target ancestors cannot escape BepInEx/plugins");
            Directory.Delete(destination);
            check(File.ReadAllText(Path.Combine(outside,"external.dll"))=="external"&&File.Exists(Disabled(root,"Valid.dll")),"link rejection preserves outside files and disabled bytes");
        }
        catch(Exception ex) when(ex is UnauthorizedAccessException||ex is IOException||ex is PlatformNotSupportedException)
        { Console.WriteLine("Disabled-mod symlink fixtures unavailable on this host: "+ex.GetType().Name); }
        finally { if(Directory.Exists(link))Directory.Delete(link); }
    }
}
