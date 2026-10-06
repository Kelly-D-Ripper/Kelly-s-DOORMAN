using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using KellysJOINCHECK;

internal static class InstalledContentRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string temporary=Path.Combine(Path.GetTempPath(),"doorman-content-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Storage(check,temporary);
            Validation(check,temporary);
            Limits(check,temporary);
            Reparse(check,temporary);
        }
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(temporary).StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(temporary))Directory.Delete(temporary,true);
        }
    }

    private static ContentRecord Pack(string id,string relative="pack.dll",string version="1")=>new ContentRecord { Id=id,Name="Pack "+id,Version=version,Source="resource:Pack:Pack."+id+".nobp",RelativeFile=relative };
    private static ContentRecord Standalone(string plugins,string id="solo")=>new ContentRecord { Id=id,Name=id,Version="1",Source=Path.Combine(plugins,"solo.nobp"),RelativeFile="solo.nobp" };
    private static void Write(string path,string bytes="known content") { Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,bytes); }
    private static string Cache(string root)=>Path.Combine(root,InstalledContentMetadata.Filename);
    private static void Reject(Action<bool,string> check,Action action,string message)
    {
        bool rejected=false;
        try { action(); }
        catch(Exception ex) when(ex is InvalidDataException||ex is IOException||ex is ArgumentException||ex is System.Runtime.Serialization.SerializationException) { rejected=true; }
        check(rejected,message);
    }

    private static void Storage(Action<bool,string> check,string temporary)
    {
        string plugins=Path.Combine(temporary,"storage","plugins"),cache=Path.Combine(temporary,"storage","cache");
        Write(Path.Combine(plugins,"nested","pack.dll"));Write(Path.Combine(plugins,"solo.nobp"));
        var store=new InstalledContentMetadata(plugins,cache);
        var logs=new List<string>();
        check(store.Read(logs.Add).Length==0&&logs.Count==0&&!Directory.Exists(cache),"unseen content is not invented and reading an absent inventory creates no files");
        var first=new[]{Pack("a","nested/pack.dll"),Pack("b","nested\\pack.dll"),Standalone(plugins)};
        store.Save(first);
        var receipt=store.Read(logs.Add);
        check(receipt.Length==3&&receipt.Select(r=>r.Id).SequenceEqual(new[]{"a","b","solo"}),"observed wrapper and standalone packs retain stable IDs and deterministic order");
        check(receipt[0].RelativeFile=="nested/pack.dll"&&receipt[0].Digest==receipt[1].Digest&&receipt[0].Digest==ModUpdateFiles.Hash(Path.Combine(plugins,"nested","pack.dll")),"shared source packs bind to one normalized source path and current SHA256");
        check(first.All(r=>r.Digest=="")&&first[1].RelativeFile.Contains('\\'),"inventory saves copy loader records without modifying their fields");
        check(receipt[2].Source==Path.Combine(plugins,"solo.nobp")&&!receipt[2].ToInstalled().Enabled&&!receipt[2].ToInstalled().Reloadable,"cached standalone sources resolve to this installation and always require restart for activation");
        string json=File.ReadAllText(Cache(cache));
        check(!json.Contains(temporary,StringComparison.OrdinalIgnoreCase)&&json.Contains("file:solo.nobp",StringComparison.Ordinal),"local cache persists portable relative file sources without machine paths");
        store.Save(Array.Empty<ContentRecord>());
        check(store.Read(logs.Add).Length==3&&File.Exists(Cache(cache)+".bak")&&File.ReadAllText(Cache(cache)+".bak")==json,"previously seen absent disabled wrappers survive when exact source bytes remain and writes retain a backup");
        receipt[0].Name="tampered returned object";
        check(store.Read(logs.Add)[0].Name=="Pack a","mutating a returned record does not alter the receipt");

        Write(Path.Combine(plugins,"other.dll"),"second source");
        store.Save(new[]{Pack("c","other.dll")});
        check(store.Read(logs.Add).Length==4,"new observations merge with valid receipts from other disabled sources");
        Write(Path.Combine(plugins,"nested","pack.dll"),"updated bytes");
        store.Save(new[]{Pack("a","nested/pack.dll","2")});
        receipt=store.Read(logs.Add);
        check(receipt.Length==3&&receipt.Single(r=>r.Id=="a").Version=="2"&&!receipt.Any(r=>r.Id=="b"),"changed wrapper bytes replace observed metadata and remove every unobserved stale pack from the same source");
        Write(Path.Combine(plugins,"other.dll"),"changed while disabled");
        check(store.Read(logs.Add).Select(r=>r.Id).SequenceEqual(new[]{"a","solo"}),"Read rehashes unchanged paths and excludes wrappers updated since discovery");
        store.Save(Array.Empty<ContentRecord>());
        check(store.Read(logs.Add).Length==2&&!File.ReadAllText(Cache(cache)).Contains("Pack c",StringComparison.Ordinal),"Save removes changed absent receipts instead of blessing a new hash for old content");
        File.Delete(Path.Combine(plugins,"solo.nobp"));
        check(store.Read(logs.Add).Length==1&&logs.Count>0,"deleted standalone files are excluded and logged without blocking valid wrapper receipts");
        check(!Directory.EnumerateFiles(cache).Any(p=>p.EndsWith(".tmp",StringComparison.Ordinal)),"atomic inventory writes leave no temporary files");
    }

    private static void Validation(Action<bool,string> check,string temporary)
    {
        string plugins=Path.Combine(temporary,"validation","plugins"),cache=Path.Combine(temporary,"validation","cache");
        Write(Path.Combine(plugins,"pack.dll"));Write(Path.Combine(plugins,"solo.nobp"));
        var store=new InstalledContentMetadata(plugins,cache);
        store.Save(new[]{Pack("a")});
        string good=File.ReadAllText(Cache(cache));
        var logs=new List<string>();
        foreach(string id in new[]{"", " ", "bad\npack", "../pack", "path\\pack", "resource:pack",new string('a',257)})
        { var bad=Pack(id);Reject(check,()=>store.Save(new[]{bad}),"inventory rejects unsafe or oversized stable content IDs"); }
        foreach(string relative in new[]{"../outside.dll","C:\\outside.dll","folder/../pack.dll","folder//pack.dll","folder./pack.dll","pack.txt","pack.dll:stream","/pack.dll","NUL.dll","COM1/pack.dll","bad*/pack.dll"})
        { var bad=Pack("a",relative);Reject(check,()=>store.Save(new[]{bad}),"inventory rejects escaped, ambiguous or unsupported local source paths"); }
        foreach(string source in new[]{"resource:Pack", "resource:Pack:asset.nobp:extra", "resource:../Pack:asset.nobp", "resource:Pack:asset.txt", "resource::asset.nobp", "resource:Pack:asset\n.nobp", "https://example.com/pack.nobp"})
        { var bad=Pack("a");bad.Source=source;Reject(check,()=>store.Save(new[]{bad}),"inventory requires the loader's exact source format"); }
        var wrongType=Pack("a","solo.nobp");
        Reject(check,()=>store.Save(new[]{wrongType}),"embedded source receipts cannot bind to standalone .nobp files");
        var mismatch=Standalone(plugins);mismatch.Source=Path.Combine(temporary,"outside.nobp");
        Reject(check,()=>store.Save(new[]{mismatch}),"standalone source must match its verified plugin-relative path");
        Reject(check,()=>store.Save(new[]{Pack("a"),Pack("a")}),"duplicate content IDs cannot create ambiguous receipts");
        Reject(check,()=>store.Save(new[]{Pack("missing","missing.dll")}),"an observed but missing source cannot be recorded as installed");
        check(File.ReadAllText(Cache(cache))==good,"failed observations leave the existing cache untouched");

        var corruptions=new List<string>{"{broken", "null", "{}", new string('x',InstalledContentMetadata.MaximumBytes+1)};
        foreach(Action<JsonObject> mutate in new Action<JsonObject>[] {
            root=>root["Schema"]=99,
            root=>root["Records"]=null,
            root=>root["Records"]!.AsArray().Add(root["Records"]![0]!.DeepClone()),
            root=>root["Records"]![0]!["Digest"]="not a hash",
            root=>root["Records"]![0]!["RelativeFile"]="../outside.dll",
            root=>root["Records"]![0]!["Source"]="resource:Pack:asset.exe",
            root=>root["Records"]![0]!["Name"]="bad\nname"
        }) { var root=JsonNode.Parse(good)!.AsObject();mutate(root);corruptions.Add(root.ToJsonString()); }
        foreach(string bad in corruptions)
        {
            File.WriteAllText(Cache(cache),bad);int before=logs.Count;
            check(store.Read(logs.Add).Length==0&&logs.Count==before+1,"corrupt, unsafe, oversized or unsupported receipt JSON fails closed and logs once");
        }
        File.WriteAllText(Cache(cache),"{broken");
        store.Save(new[]{Pack("a")});
        check(store.Read(logs.Add).Length==1&&File.ReadAllText(Cache(cache)+".bak")=="{broken","new real observations rebuild a corrupt ordinary receipt and retain its recovery bytes");
        var duplicateSource=JsonNode.Parse(good)!.AsObject();
        var second=duplicateSource["Records"]![0]!.DeepClone();second["Id"]="b";second["Digest"]=new string('0',64);duplicateSource["Records"]!.AsArray().Add(second);
        File.WriteAllText(Cache(cache),duplicateSource.ToJsonString());
        check(store.Read(logs.Add).Length==0,"contradictory hash claims for one installed source are rejected before use");
        store.Save(new[]{Pack("a")});
        File.Delete(Cache(cache)+".bak");Directory.CreateDirectory(Cache(cache)+".bak");
        Reject(check,()=>store.Save(new[]{Pack("a")}),"a directory at the cache backup target cannot be overwritten");
        check(store.Read(logs.Add).Length==1,"failed backup preparation preserves the readable current inventory");
    }

    private static void Limits(Action<bool,string> check,string temporary)
    {
        string plugins=Path.Combine(temporary,"limits","plugins"),cache=Path.Combine(temporary,"limits","cache");
        Write(Path.Combine(plugins,"pack.dll"));
        var store=new InstalledContentMetadata(plugins,cache);
        var many=Enumerable.Range(0,InstalledContentMetadata.MaximumRecords).Select(i=>Pack("pack."+i)).ToArray();
        store.Save(many);
        var receipt=store.Read(_=>{});
        check(receipt.Length==InstalledContentMetadata.MaximumRecords&&receipt.Select(r=>r.Digest).Distinct().Count()==1,"the cache supports 512 known packs sharing one verified source");
        Reject(check,()=>store.Save(many.Concat(new[]{Pack("one.too.many")})),"observation enumeration stops at the record cap");
        Write(Path.Combine(plugins,"second.dll"));
        Reject(check,()=>store.Save(new[]{Pack("new","second.dll")}),"merging retained receipts cannot silently exceed the record cap");
        check(store.Read(_=>{}).Length==InstalledContentMetadata.MaximumRecords,"a merged overflow leaves the prior inventory intact");
        var oversized=Enumerable.Range(0,InstalledContentMetadata.MaximumRecords).Select(i=>Pack("pack."+i)).ToArray();
        foreach(var item in oversized) { item.Name=new string('n',256);item.Version=new string('v',128);item.Source="resource:Pack:"+new string('r',1450)+".nobp"; }
        Reject(check,()=>store.Save(oversized),"serialized receipt bytes are bounded separately from record count");
        string huge=Path.Combine(plugins,"huge.dll");
        using(var file=new FileStream(huge,FileMode.CreateNew,FileAccess.Write,FileShare.None))file.SetLength(InstalledContentMetadata.MaximumSourceBytes+1);
        Reject(check,()=>store.Save(new[]{Pack("huge","huge.dll")}),"oversized source files are refused before hashing");
        File.Delete(huge);
    }

    private static void Reparse(Action<bool,string> check,string temporary)
    {
        string plugins=Path.Combine(temporary,"links","plugins"),cache=Path.Combine(temporary,"links","cache");
        Write(Path.Combine(plugins,"pack.dll"));
        var store=new InstalledContentMetadata(plugins,cache);store.Save(new[]{Pack("a")});
        string good=File.ReadAllText(Cache(cache)),external=Path.Combine(temporary,"links","external.json"),source=Path.Combine(plugins,"pack.dll"),original=source+".original";
        Write(external,good);
        bool link=false;
        try
        {
            File.Move(source,original);
            try { File.CreateSymbolicLink(source,original);link=true; }
            catch(Exception ex) when(ex is UnauthorizedAccessException||ex is IOException||ex is PlatformNotSupportedException)
            { Console.WriteLine("Installed content symlink checks unavailable on this host: "+ex.GetType().Name);return; }
            check(store.Read(_=>{}).Length==0,"a linked content source is never accepted even when its bytes match the receipt");
            Reject(check,()=>store.Save(new[]{Pack("a")}),"new observations cannot hash through a linked source");
            File.Delete(source);link=false;File.Move(original,source);
            File.Delete(Cache(cache));File.CreateSymbolicLink(Cache(cache),external);
            check(store.Read(_=>{}).Length==0,"linked inventory JSON cannot import receipts from outside its cache folder");
            Reject(check,()=>store.Save(new[]{Pack("a")}),"inventory saves cannot replace an external linked cache");
            check(File.ReadAllText(external)==good,"rejected linked writes leave the external target untouched");
            File.Delete(Cache(cache));File.WriteAllText(Cache(cache),good);
            File.Delete(Cache(cache)+".bak");File.CreateSymbolicLink(Cache(cache)+".bak",external);
            Reject(check,()=>store.Save(new[]{Pack("a")}),"linked backup targets cannot receive receipt bytes");
            File.Delete(Cache(cache)+".bak");
            string alias=Path.Combine(temporary,"links","alias");Directory.CreateSymbolicLink(alias,plugins);
            try
            {
                var aliasStore=new InstalledContentMetadata(alias,cache);
                check(aliasStore.Read(_=>{}).Length==0,"a linked plugin root is rejected even with a valid receipt");
                Reject(check,()=>aliasStore.Save(new[]{Pack("a")}),"source ancestor reparse points are rejected during Save");
            }
            finally { Directory.Delete(alias); }
        }
        finally
        {
            if(link)File.Delete(source);
            if(File.Exists(original)&&!File.Exists(source))File.Move(original,source);
            if(File.Exists(Cache(cache))&&(File.GetAttributes(Cache(cache))&FileAttributes.ReparsePoint)!=0)File.Delete(Cache(cache));
            if(File.Exists(Cache(cache)+".bak")&&(File.GetAttributes(Cache(cache)+".bak")&FileAttributes.ReparsePoint)!=0)File.Delete(Cache(cache)+".bak");
        }
    }
}
