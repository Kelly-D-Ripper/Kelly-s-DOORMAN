using System;
using System.IO;
using System.Linq;
using KellysJOINCHECK;

internal static class StandaloneBundleUpdateChecks
{
    private static void Throws(Action<bool,string> check,Action action,string name)
    {bool threw=false;try{action();}catch(Exception){threw=true;}check(threw,name);}
    internal static void Run(Action<bool,string> check)
    {
        check(StandaloneBundleUpdates.InventoryAvailable(true,true,false,true),"server-verified standalone update can stage after loader completion even when pending patches prevent live reload");
        check(!StandaloneBundleUpdates.InventoryAvailable(true,false,false,true)&&!StandaloneBundleUpdates.InventoryAvailable(true,true,false,false),"server-verified staging still requires finished loading and a complete verified inventory");
        check(StandaloneBundleUpdates.InventoryAvailable(false,true,false,true)&&StandaloneBundleUpdates.InventoryAvailable(false,true,true,true),"manual bundle staging uses a completed inventory even if live reload is unavailable");
        check(!StandaloneBundleUpdates.InventoryAvailable(false,true,false,false)&&!StandaloneBundleUpdates.InventoryAvailable(false,false,true,true),"bundle staging still requires loading and a usable inventory");
        string root=Path.Combine(Path.GetTempPath(),"doorman-bundle-"+Guid.NewGuid().ToString("N"));
        string plugins=Path.Combine(root,"plugins"),queue=Path.Combine(root,"queue"),payload=Path.Combine(root,"download.bin");
        Directory.CreateDirectory(plugins);
        try
        {
            string target=Path.Combine(plugins,"plane.nobp");File.WriteAllText(target,"old");File.WriteAllText(payload,"new");
            string old=ModUpdateFiles.Hash(target),next=ModUpdateFiles.Hash(payload);
            StandaloneBundleUpdates.Stage(queue,plugins,payload,target,"plane","1.2.3",next);
            check(File.ReadAllText(target)=="old","content download does not change installed files");
            StandaloneBundleUpdates.VerifyPending(queue,plugins);
            Throws(check,()=>StandaloneBundleUpdates.Stage(queue,plugins,payload,target,"plane","1.2.3",next),"duplicate content updates are rejected");
            File.WriteAllText(target,"changed");
            Throws(check,()=>StandaloneBundleUpdates.VerifyPending(queue,plugins),"content preflight detects changed installed file");
            Throws(check,()=>StandaloneBundleUpdates.Apply(queue,plugins,_=>{}),"changed file blocks content install");
            File.WriteAllText(target,"old");StandaloneBundleUpdates.Apply(queue,plugins,_=>{});
            check(File.ReadAllText(target)=="new","content update is applied on startup");
            string job=Directory.GetDirectories(queue).Single();
            check(ModUpdateFiles.Hash(Path.Combine(job,"previous.nobp"))==old,"content update retains a verified old copy");
            check(StandaloneBundleUpdates.Pending(queue,plugins).Length==0,"completed content update leaves queue");
            string newTarget=Path.Combine(plugins,"new.nobp");StandaloneBundleUpdates.Stage(queue,plugins,payload,newTarget,"new","1.0",next);
            check(!File.Exists(newTarget),"new content is not installed before startup");
            StandaloneBundleUpdates.Apply(queue,plugins,_=>{});check(File.ReadAllText(newTarget)=="new","new content installs at startup");
            Throws(check,()=>StandaloneBundleUpdates.Stage(queue,plugins,payload,Path.Combine(root,"outside.nobp"),"evil","1.0",next),"content updates reject external targets");
            Throws(check,()=>StandaloneBundleUpdates.Stage(queue,plugins,payload,Path.Combine(plugins,"bad.dll"),"evil","1.0",next),"content updates cannot target DLLs");
            Throws(check,()=>StandaloneBundleUpdates.Stage(queue,plugins,payload,Path.Combine(plugins,"bad.nobp"),"evil","1.0",new string('0',64)),"content staging verifies its download hash");
        }
        finally {Directory.Delete(root,true);}
    }
}
