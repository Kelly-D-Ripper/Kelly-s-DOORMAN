using System;
using System.IO;
using System.Linq;
using KellysJOINCHECK;

internal static class StateWriteSafetyRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-state-write-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            ProfileBounds(check,root);
            TicketBackup(check,root);
            LinkedWriters(check,root);
        }
        finally {Directory.Delete(root,true);}
    }

    private static void ProfileBounds(Action<bool,string> check,string root)
    {
        string path=Path.Combine(root,"profile","doorman-mods.json");
        ModJson.Save(path,new ModProfile {AutoMatchServers=false,DisabledPlugins=new[]{"example.plugin"}});
        ModJson.Save(path,new ModProfile {AutoMatchServers=true,DisabledPlugins=new[]{"second.plugin"}});
        byte[] current=File.ReadAllBytes(path),backup=File.ReadAllBytes(path+".bak");
        check(ModJson.Read<ModProfile>(path).AutoMatchServers&&!ModJson.Read<ModProfile>(path+".bak").AutoMatchServers,"guarded profile writes retain the current schema and the exact previous selection");
        var oversized=new ModProfile {DisabledContent=Enumerable.Range(0,300).Select(i=>"pack."+i+"."+new string('x',240)).ToArray()};
        oversized.Validate();
        Throws(check,()=>ModJson.Save(path,oversized),"a structurally valid profile above its 64 KiB reader limit is refused before replacement");
        check(File.ReadAllBytes(path).SequenceEqual(current)&&File.ReadAllBytes(path+".bak").SequenceEqual(backup)&&!File.Exists(path+".tmp"),"oversized profile refusal preserves both readable selection files and creates no temporary receipt");
    }

    private static ServerSetupTicket Ticket(string name)=>new ServerSetupTicket {Target=new ServerTarget {Name=name,SteamId=42,Wire="0.34",Expanded="0.34"}};

    private static void TicketBackup(Action<bool,string> check,string root)
    {
        string directory=Path.Combine(root,"server-ticket"),path=Path.Combine(directory,ServerSetupFiles.Filename);
        ServerSetupFiles.Save(directory,Ticket("Original server"));ServerSetupFiles.Save(directory,Ticket("Current server"));
        check(ServerSetupFiles.Pending(directory)?.Target.Name=="Current server"&&ModJson.Read<ServerSetupTicket>(path+".bak").Target.Name=="Original server","guarded saved-server writes preserve the pending ticket and its previous backup");
        var oversized=Ticket("Too large");oversized.Inventory=Enumerable.Range(0,256).Select(i=>new ContentRecord {Id="pack."+i,Name=new string('n',256),Version="1.0",Source=new string('s',2048),RelativeFile="pack"+i+".dll",Digest=new string('a',64)}).ToArray();
        byte[] original=File.ReadAllBytes(path),backup=File.ReadAllBytes(path+".bak");
        Throws(check,()=>ServerSetupFiles.Save(directory,oversized),"a saved server ticket above its existing 64 KiB bound is refused before replacement");
        check(File.ReadAllBytes(path).SequenceEqual(original)&&File.ReadAllBytes(path+".bak").SequenceEqual(backup),"an oversized saved-server ticket preserves current and recovery files");
    }

    private static void LinkedWriters(Action<bool,string> check,string root)
    {
        string probe=Path.Combine(root,"link-probe"),missing=Path.Combine(root,"missing-target");
        try {File.CreateSymbolicLink(probe,missing);}
        catch(Exception ex) when(ex is UnauthorizedAccessException||ex is IOException||ex is PlatformNotSupportedException)
        {Console.WriteLine("State-writer symbolic-link checks unavailable on this host: "+ex.GetType().Name);return;}
        // A dangling link may report false from Exists; Delete still removes the
        // link itself without creating or touching its missing target.
        File.Delete(probe);
        foreach(bool ticket in new[]{false,true})
        foreach(string suffix in new[]{"",".tmp",".bak"})
        foreach(bool dangling in new[]{false,true})
        {
            string directory=Path.Combine(root,"linked-"+(ticket?"ticket":"profile")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,ticket?ServerSetupFiles.Filename:"doorman-mods.json"),external=Path.Combine(directory,"external.txt"),linked=path+suffix;
            Action write=()=>{if(ticket)ServerSetupFiles.Save(directory,Ticket("Server"));else ModJson.Save(path,new ModProfile());};
            if(suffix.Length>0)write();
            if(!dangling)File.WriteAllText(external,"Keep these external bytes.");
            File.CreateSymbolicLink(linked,external);
            try
            {
                Throws(check,write,(ticket?"saved-server":"profile")+" writes reject "+(dangling?"dangling ":"")+"linked "+(suffix.Length==0?"main":suffix)+" paths");
                check(dangling?!File.Exists(external):File.ReadAllText(external)=="Keep these external bytes.","rejected state-file links preserve the external target");
            }
            finally {File.Delete(linked);}
        }
    }

    private static void Throws(Action<bool,string> check,Action action,string label)
    {
        bool failed=false;
        try {action();}
        catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is UnauthorizedAccessException){failed=true;}
        check(failed,label);
    }
}
