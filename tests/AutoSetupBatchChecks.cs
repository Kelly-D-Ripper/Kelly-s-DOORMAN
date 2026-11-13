using System;
using System.IO;
using KellysJOINCHECK;

internal static class AutoSetupBatchChecks
{
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-batch-"+Guid.NewGuid().ToString("N")),plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),queue=Path.Combine(cache,"DOORMAN-content-updates"),payload=Path.Combine(root,"payload.bin");
        Directory.CreateDirectory(plugins);Directory.CreateDirectory(cache);
        try
        {
            File.WriteAllText(payload,"candidate");string hash=ModUpdateFiles.Hash(payload);
            StandaloneBundleUpdates.Stage(queue,plugins,payload,Path.Combine(plugins,"earlier.nobp"),"earlier","1.0",hash);
            string pluginQueue=Path.Combine(cache,"DOORMAN-updates");Directory.CreateDirectory(pluginQueue);
            string oldJob=Path.Combine(pluginQueue,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(oldJob);
            UpdateJson.Save(Path.Combine(oldJob,"plan.json"),new PendingModUpdate {Plugin="prior.plugin",Repository="owner/repo",Version="1.0",Target="prior.dll",OldHash=hash,NewHash=hash});
            string token=AutoSetupBatch.Begin(cache,plugins);
            string newJob=Path.Combine(pluginQueue,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(newJob);
            UpdateJson.Save(Path.Combine(newJob,"plan.json"),new PendingModUpdate {Plugin="batch.plugin",Repository="owner/repo",Version="1.0",Target="batch.dll",OldHash=hash,NewHash=hash});
            StandaloneBundleUpdates.Stage(queue,plugins,payload,Path.Combine(plugins,"batch.nobp"),"batch","1.0",hash);
            bool threw=false;try{AutoSetupBatch.Commit(cache,Guid.NewGuid().ToString("N"));}catch(InvalidOperationException){threw=true;}check(threw,"batch marker requires the correct token");
            AutoSetupBatch.Recover(cache,plugins,_=>{});
            var jobs=StandaloneBundleUpdates.Pending(queue,plugins);
            check(jobs.Length==1&&jobs[0].Id=="earlier","interrupted setup preserves preexisting queued updates and cancels batch work");
            check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(oldJob,"plan.json")).State=="pending"&&UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(newJob,"plan.json")).State=="cancelled","recovery cancels new plugin receipts without touching earlier queued work");
            check(!File.Exists(Path.Combine(plugins,"batch.nobp"))&&!File.Exists(Path.Combine(plugins,"earlier.nobp")),"batch recovery does not install content");
            token=AutoSetupBatch.Begin(cache,plugins);
            StandaloneBundleUpdates.Stage(queue,plugins,payload,Path.Combine(plugins,"good.nobp"),"good","1.0",hash);
            AutoSetupBatch.Commit(cache,token);AutoSetupBatch.Recover(cache,plugins,_=>{});
            check(StandaloneBundleUpdates.Pending(queue,plugins).Length==2,"committed batch remains queued on startup");
            StandaloneBundleUpdates.Apply(queue,plugins,_=>{});
            check(File.Exists(Path.Combine(plugins,"good.nobp"))&&File.Exists(Path.Combine(plugins,"earlier.nobp")),"committed and previous work installs on startup");
        }
        finally{Directory.Delete(root,true);}
    }
}
