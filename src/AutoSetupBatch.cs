using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class AutoSetupBatchPlan
    {
        [DataMember] public int Schema=1;
        [DataMember] public string Token="";
        [DataMember] public string[] Plugins=Array.Empty<string>(),Content=Array.Empty<string>();
        [DataMember(EmitDefaultValue=false)] public string[] Maps=Array.Empty<string>(),Ironjaw=Array.Empty<string>();
        internal void Validate()
        {
            Maps=Maps??Array.Empty<string>();Ironjaw=Ironjaw??Array.Empty<string>();
            if(Schema!=1||!Guid.TryParseExact(Token,"N",out _)||Plugins==null||Content==null||Plugins.Length+Content.Length+Maps.Length+Ironjaw.Length>1536||Plugins.Concat(Content).Concat(Maps).Concat(Ironjaw).Any(p=>!Guid.TryParseExact(p,"N",out _))||Plugins.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Plugins.Length||Content.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Content.Length||Maps.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Maps.Length||Ironjaw.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=Ironjaw.Length)throw new InvalidDataException("Invalid automatic setup batch.");
        }
    }
    // A batch marker is written before the first download is queued. If the
    // process exits before Commit, startup cancels the newly queued receipts
    // before applying *any* updates. Original plugin/content files remain intact.
    internal static class AutoSetupBatch
    {
        private static string Marker(string cache)=>ModUpdateFiles.Within(cache,"DOORMAN-auto-setup.json");
        private static string PluginRoot(string cache)=>ModUpdateFiles.Within(cache,"DOORMAN-updates");
        private static string ContentRoot(string cache)=>ModUpdateFiles.Within(cache,"DOORMAN-content-updates");
        private static string[] Names(string root)=>ModUpdateFiles.Jobs(root).Select(p=>Path.GetFileName(p)!).OrderBy(n=>n,StringComparer.OrdinalIgnoreCase).ToArray();
        internal static string Begin(string cache,string plugins)
        {
            string marker=Marker(cache);if(File.Exists(marker)||Directory.Exists(marker))throw new InvalidOperationException("A previous automatic setup needs recovery before starting another.");
            // Capture every existing folder, including failed receipts and orphaned
            // partial downloads, so recovery never claims an older user's data.
            var plan=new AutoSetupBatchPlan {Token=Guid.NewGuid().ToString("N"),Plugins=Names(PluginRoot(cache)),Content=Names(ContentRoot(cache)),Maps=Names(ModUpdateFiles.Within(cache,ServerMapUpdates.DirectoryName)),Ironjaw=Names(ModUpdateFiles.Within(cache,IronjawPackageUpdates.DirectoryName))};plan.Validate();
            Directory.CreateDirectory(cache);UpdateJson.Save(marker,plan);return plan.Token;
        }
        private static AutoSetupBatchPlan Read(string cache,string token)
        {
            var p=UpdateJson.ReadFile<AutoSetupBatchPlan>(Marker(cache));p.Validate();
            if(token.Length>0&&p.Token!=token)throw new InvalidOperationException("Automatic setup batch changed.");return p;
        }
        internal static void Commit(string cache,string token)
        {if(!Guid.TryParseExact(token,"N",out _))throw new InvalidOperationException("Invalid automatic setup batch token.");Read(cache,token);File.Delete(Marker(cache));if(File.Exists(Marker(cache)))throw new IOException("Could not finish the automatic setup batch.");}
        internal static void Abort(string cache,string plugins,string token)
        {
            if(token.Length>0&&!Guid.TryParseExact(token,"N",out _))throw new InvalidOperationException("Invalid automatic setup batch token.");
            var plan=Read(cache,token);
            CancelNew(PluginRoot(cache),plan.Plugins,false);
            CancelNew(ContentRoot(cache),plan.Content,true);
            foreach(var p in ServerMapUpdates.Read(cache,plugins,true).Where(p=>!plan.Maps.Contains(Path.GetFileName(p.Job),StringComparer.OrdinalIgnoreCase)))ServerMapUpdates.Cancel(cache,plugins,p.Job);
            foreach(var p in IronjawPackageUpdates.Read(cache,plugins,true).Where(p=>!plan.Ironjaw.Contains(Path.GetFileName(p.Job),StringComparer.OrdinalIgnoreCase)))IronjawPackageUpdates.Cancel(cache,plugins,p.Job);
            File.Delete(Marker(cache));if(File.Exists(Marker(cache)))throw new IOException("Could not complete setup recovery.");
        }
        internal static void Recover(string cache,string plugins,Action<string> log)
        {
            if(!File.Exists(Marker(cache)))return;
            Abort(cache,plugins,"");log("An interrupted automatic setup was cancelled. Your installed mods and earlier update queue were kept.");
        }
        private static void CancelNew(string root,string[] before,bool content)
        {
            foreach(string job in ModUpdateFiles.Jobs(root).Where(p=>!before.Contains(Path.GetFileName(p),StringComparer.OrdinalIgnoreCase)))
            {
                string receipt=ModUpdateFiles.Within(job,"plan.json");if(!File.Exists(receipt))continue;
                if(content)
                {
                    var p=UpdateJson.ReadFile<StandaloneBundlePlan>(receipt);p.Validate();
                    if(p.State=="applying"||p.State=="complete")throw new IOException("An incomplete setup already modified content; recovery needs attention.");
                    if(p.State=="pending"){p.State="cancelled";UpdateJson.Save(receipt,p);}
                }
                else
                {
                    var p=UpdateJson.ReadFile<PendingModUpdate>(receipt);p.Validate();
                    if(p.State=="applying"||p.State=="complete")throw new IOException("An incomplete setup already modified a plugin; recovery needs attention.");
                    if(p.State=="pending"){p.State="cancelled";p.Message="Cancelled after an incomplete automatic server setup.";UpdateJson.Save(receipt,p);}
                }
            }
        }
    }
}
