using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using HarmonyLib;
using NuclearOption.Networking.Lobbies;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace KellysJOINCHECK
{
    internal sealed partial class ModManager
    {
        private ServerTarget? armedRestart,workflowTarget;
        private string workflowMessage="";
        private int setupGeneration;
        private string? restartHandshake;
        private bool restartExitConfirmed;
        internal void CancelServerSetup(){setupGeneration++;armedRestart=null;workflowMessage="";CancelRestartHandshake();}
        private void CancelRestartHandshake()
        {if(restartExitConfirmed||restartHandshake==null)return;try{File.WriteAllText(restartHandshake+".cancel","cancel");File.Delete(restartHandshake);}catch{}}
        private string BeginRestartHandshake()
        {CancelRestartHandshake();restartExitConfirmed=false;return restartHandshake=Path.Combine(Paths.CachePath,"doorman-"+Guid.NewGuid().ToString("N")+".ready");}
        private static ServerTarget Snapshot(LobbyInstance target,string expanded)
        {
            string ip=target.UdpAddress??"",port=target.UdpPort??"";
            if(target is ServerLobbyInstance server&&server.details!=null)
            { uint address=server.details.m_NetAdr.GetIP();ushort value=server.details.m_NetAdr.GetConnectionPort();ip=address!=0&&value>0?ServerTarget.Address(address):"";port=ip.Length==0?"":value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return new ServerTarget { Name=target.LobbyNameSanitized??"",Host=target.HostAddress??"",Ip=ip,Port=port,SteamId=target.LobbyId.m_SteamID,Dedicated=target.DedicatedServer,Wire=target.HostVersion,Expanded=expanded };
        }
        private static bool Same(ServerTarget? saved,LobbyInstance target)
        { if(saved==null)return false;var actual=Snapshot(target,"");return saved.Matches(actual.Dedicated,actual.SteamId,actual.Ip,actual.Port)&&saved.Wire==actual.Wire; }
        private PendingModUpdate[] PendingUpdates()=>ModUpdateFiles.ReadPending(Path.Combine(Paths.CachePath,"DOORMAN-updates"),Paths.PluginPath);
        private ServerSetupPlan Plan(string expanded)
        {
            var installed=ModCatalog.Read(loader.Content,profile,log);
            string bp=installed.FirstOrDefault(e=>!e.Content&&e.Id=="com.nikkorap.blueprinter")?.Version??"";
            var pending=PendingUpdates();var versions=new List<KeyValuePair<string,string>>();
            foreach(var update in pending)
            {
                var packs=installed.Where(e=>e.Content&&e.WrapperPlugin==update.Plugin&&e.WrapperPath.Length>0&&ServerSetupFiles.RelativeContent(Paths.PluginPath,e.WrapperPath).Equals(update.Target,StringComparison.OrdinalIgnoreCase)).ToArray();
                if(packs.Length>1)throw new InvalidDataException("This downloaded plugin contains several packs. Use Reload mods to restart and read their new versions first.");
                if(packs.Length==1)versions.Add(new KeyValuePair<string,string>(packs[0].Id,update.Version));
            }
            var result=ServerSetupPlan.Create(expanded,Compatibility.Expanded(),loader.Content,bp,Chainloader.PluginInfos.ContainsKey("com.nikkorap.blueprinter"),loader.Ready,versions,pending.Length>0);result.Updates=pending;
            if(result.CanSetup)
            {
                var wrappers=installed.Where(e=>e.Content&&e.WrapperPlugin.Length>0).ToArray();
                result.DisabledPlugins=ServerPluginSelection.Resolve(installed.Where(e=>!e.Content).Select(e=>new KeyValuePair<string,string[]>(e.Id,e.Requires.ToArray())),profile.DisabledPlugins,wrappers.Select(e=>e.WrapperPlugin),wrappers.Where(e=>!result.DisabledContent.Contains(e.Id,StringComparer.Ordinal)).Select(e=>e.WrapperPlugin),result.Vanilla);
                var dependencies=installed.Where(e=>!e.Content&&result.DisabledPlugins.Contains(e.Id,StringComparer.Ordinal)&&!profile.DisabledPlugins.Contains(e.Id,StringComparer.Ordinal)&&e.Id!=bp&&!wrappers.Any(w=>w.WrapperPlugin==e.Id)).Select(e=>Diagnostics.Clean(e.Name)).Distinct().Take(5).ToArray();
                if(dependencies.Length>0)result.Reason+=" Also disabled for this session because they require excluded plugins: "+string.Join(", ",dependencies)+". Unrelated client plugins stay enabled.";
            }
            return result;
        }
        internal void ServerActions(LobbyInstance target,out string label,out bool setup,out bool join,out string message)
        {
            label=Same(armedRestart,target)?"Proceed":"Setup mods";setup=!busy;join=!busy&&target.HostVersion==Compatibility.Wire&&MenuSafety().Length==0;
            message=Same(workflowTarget,target)?workflowMessage:"";
        }
        internal void SetupServer(LobbyInstance target,string expanded,bool joinAfter=false,string password="",bool prompt=true)
        {
            try { SetupServerCore(target,expanded,joinAfter,password,prompt); }
            catch(Exception ex) { busy=false;armedRestart=null;log("Server setup failed: "+ex.Message);Message(target,"Could not set up mods: "+ex.Message); }
        }
        private void SetupServerCore(LobbyInstance target,string expanded,bool joinAfter,string password,bool prompt)
        {
            if(busy)return;
            if(Same(armedRestart,target)){RestartForServer(target,expanded);return;}
            setupGeneration++;workflowTarget=Snapshot(target,expanded);armedRestart=null;
            string reason=MenuSafety();if(reason.Length==0&&!loader.LoadingFinished)reason="Wait for Blueprinter to finish loading, then press Setup mods.";
            if(reason.Length>0){Message(target,reason);return;}
            var plan=Plan(expanded);
            if(!plan.CanSetup){Message(target,plan.Reason);return;}
            if(plan.NeedsRestart)
            {
                if(!StartupAvailable()){Message(target,"Install the FULL client package, including its startup helper, before using restart setup.");return;}
                if(!plan.Vanilla&&Chainloader.PluginInfos.ContainsKey("com.nikkorap.blueprinter")&&!loader.StartupSupported){Message(target,loader.Unavailable);return;}
                armedRestart=Snapshot(target,expanded);Message(target,plan.Reason);return;
            }
            busy=true;Message(target,"Setting up this server's mods...");owner.StartCoroutine(SetupAfterFrame(target,expanded,plan,joinAfter,password,prompt,setupGeneration));
        }
        private static bool StartupAvailable()=>AppDomain.CurrentDomain.GetAssemblies().Where(a=>a.GetName().Name=="KellysDOORMANStartup").Any(a=>a.GetType("DoormanStartupPatcher")?.GetField("Ready")?.GetValue(null) is bool ready&&ready);
        private void Message(LobbyInstance target,string message)
        {
            workflowMessage=message;owner.ShowServerWorkflow(target);owner.RefreshServerActions();
        }
        private IEnumerator SetupAfterFrame(LobbyInstance target,string expanded,ServerSetupPlan plan,bool joinAfter,string password,bool prompt,int generation)
        {
            yield return null;
            var previous=loader.Content.Where(c=>!c.Enabled).Select(c=>c.Id).ToArray();
            bool applied=false;
            try
            {
                if(generation!=setupGeneration||!Same(workflowTarget,target))yield break;
                string error=MenuSafety();
                if(error.Length==0&&loader.Content.Any(c=>c.Enabled==plan.DisabledContent.Contains(c.Id,StringComparer.Ordinal)))
                { error=loader.Apply(plan.DisabledContent);applied=error.Length==0; }
                if(error.Length>0){armedRestart=Snapshot(target,expanded);workflowMessage="Reload could not apply this safely. Press Proceed to restart and return to this server.";yield break;}
                if(target.HostVersion!=Compatibility.Wire)
                {
                    if(applied)loader.Apply(previous);
                    armedRestart=Snapshot(target,expanded);workflowMessage="The live mod signature still differs. Press Proceed to try a clean restart with this set.";yield break;
                }
                // A repeated setup may already match. It must not forget the
                // earlier temporary reload that still needs normal restoration.
                temporarySelection|=applied;workflowMessage="Mods ready. Click Join server.";
                if(joinAfter)
                { owner.HideServerWorkflow();retrying=true;try{SteamLobby.instance.TryJoinLobby(target,password,prompt);}finally{retrying=false;} }
            }
            catch(Exception ex){if(applied)loader.Apply(previous);workflowMessage="Could not set up mods: "+ex.Message;log(workflowMessage);}
            finally{busy=false;owner.RefreshServerActions();}
        }
        internal bool BeforeJoin(SteamLobby steam,LobbyInstance target,string password,bool prompt,string expanded)
        {
            if(retrying||!profile.AutoMatchServers||target.HostVersion==Compatibility.Wire)return true;
            SetupServer(target,expanded,true,password,prompt);return false;
        }
        private ServerSetupTicket Ticket(LobbyInstance target,string expanded,ServerSetupPlan plan)
        {
            var catalog=ModCatalog.Read(loader.Content,profile,log);
            return new ServerSetupTicket { Target=Snapshot(target,expanded),DisabledContent=plan.DisabledContent,DisabledPlugins=plan.DisabledPlugins,Inventory=catalog.Where(c=>c.Content).Select(c=>TicketContent(c,plan)).ToArray() };
        }
        private ContentRecord TicketContent(ModEntry entry,ServerSetupPlan plan)
        {
            string relative=ServerSetupFiles.RelativeContent(Paths.PluginPath,entry.WrapperPath.Length>0?entry.WrapperPath:entry.Path);
            var observed=loader.Content.FirstOrDefault(c=>c.Id==entry.Id);
            var cached=resume?.Inventory.FirstOrDefault(c=>c.Id==entry.Id&&c.Version==entry.Version&&c.RelativeFile.Equals(relative,StringComparison.OrdinalIgnoreCase));
            return new ContentRecord { Id=entry.Id,Name=entry.Name,Version=plan.NextVersions.TryGetValue(entry.Id,out var version)?version:entry.Version,Source=entry.Path,RelativeFile=relative,Digest=cached?.Digest??"",ObservedSourceLength=observed?.ObservedSourceLength??-1,ObservedSourceWriteTicks=observed?.ObservedSourceWriteTicks??-1 };
        }
        private static void HashInventory(ServerSetupTicket ticket,string pluginRoot,bool verify,PendingModUpdate[]? pending=null)
        {
            var hashes=new Dictionary<string,(string Digest,long Length,long WriteTicks)>(StringComparer.OrdinalIgnoreCase);
            foreach(var record in ticket.Inventory)
            {
                string path=ModUpdateFiles.Within(pluginRoot,record.RelativeFile);
                if(!hashes.TryGetValue(path,out var source))
                {
                    try
                    {
                        var file=new FileInfo(path);long length=file.Length,ticks=file.LastWriteTimeUtc.Ticks;
                        if(length>256L*1024*1024)throw new InvalidDataException("Installed content is too large to verify automatically.");
                        string digest;using(var sha=SHA256.Create())using(var stream=File.OpenRead(path))digest=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
                        file.Refresh();if(!file.Exists||file.Length!=length||file.LastWriteTimeUtc.Ticks!=ticks)throw new IOException("An installed content file changed during verification.");
                        source=(digest,length,ticks);hashes[path]=source;
                    }
                    catch when(verify){record.Version="";continue;}
                }
                if(!verify)ServerSetupFiles.VerifySourceObservation(record,source.Length,source.WriteTicks,source.Digest,pending?.SingleOrDefault(p=>p.Target.Equals(record.RelativeFile,StringComparison.OrdinalIgnoreCase)));
                if(verify&&!source.Digest.Equals(record.Digest,StringComparison.OrdinalIgnoreCase))record.Version="";else record.Digest=source.Digest;
            }
        }
        private void RestartForServer(LobbyInstance target,string expanded)
        {
            string reason=MenuSafety();if(reason.Length>0){Message(target,reason);return;}
            var plan=Plan(expanded);if(!plan.CanSetup){armedRestart=null;Message(target,plan.Reason);return;}
            if(!StartupAvailable()){Message(target,"The startup helper is unavailable. Reinstall the full client package.");return;}
            if(!plan.Vanilla&&Chainloader.PluginInfos.ContainsKey("com.nikkorap.blueprinter")&&!loader.StartupSupported){Message(target,loader.Unavailable);return;}
            string helper=Path.Combine(Paths.BepInExRootPath,"DOORMAN","KellysDOORMANRestart.exe");
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT||!File.Exists(helper)){Message(target,"Automatic restart needs the Windows helper from the FULL package. No settings changed.");return;}
            try
            {
                var ticket=Ticket(target,expanded,plan);ticket.Target.Validate();busy=true;Message(target,"Checking installed files and downloaded updates before restarting...");owner.StartCoroutine(PrepareRestart(target,ticket,helper,setupGeneration,plan.Updates));
            }
            catch(Exception ex){ClearPending();Message(target,"Could not restart: "+ex.Message);}
        }
        private IEnumerator PrepareRestart(LobbyInstance target,ServerSetupTicket ticket,string helper,int generation,PendingModUpdate[] pending)
        {
            var task=Task.Run(()=>
            {
                ModUpdateFiles.VerifyPending(pending,Paths.PluginPath);HashInventory(ticket,Paths.PluginPath,false,pending);
                foreach(var record in ticket.Inventory)
                { var update=pending.SingleOrDefault(p=>p.Target.Equals(record.RelativeFile,StringComparison.OrdinalIgnoreCase));if(update!=null)record.Digest=update.NewHash; }
            },updateLifetime.Token);
            while(!task.IsCompleted)yield return null;
            try
            {
                if(generation!=setupGeneration||!Same(ticket.Target,target)){busy=false;yield break;}
                if(task.IsFaulted)throw task.Exception!.GetBaseException();if(task.IsCanceled)throw new IOException("Restart verification was cancelled.");
                if(MenuSafety().Length>0)throw new InvalidOperationException(MenuSafety());
                ServerSetupFiles.Save(Paths.ConfigPath,ticket);
                string ready=BeginRestartHandshake();var launch=Task.Run(()=>LaunchRestartHelper(helper,ready));
                Message(target,"Preparing the Windows restart helper...");owner.StartCoroutine(AwaitServerRestartLaunch(launch,ready,ticket,target,generation));
            }
            catch(Exception ex){busy=false;ClearPending();Message(target,"Could not restart: "+ex.Message);}
        }
        private IEnumerator AwaitServerRestartLaunch(Task<string> launch,string ready,ServerSetupTicket ticket,LobbyInstance target,int generation)
        {
            while(!launch.IsCompleted){if(generation!=setupGeneration)CancelRestartHandshake();yield return null;}
            try
            {
                if(generation!=setupGeneration||!Same(ticket.Target,target)||File.Exists(ready+".cancel")){CancelRestartHandshake();busy=false;ClearPending();yield break;}
                if(launch.IsFaulted)throw launch.Exception!.GetBaseException();if(launch.IsCanceled)throw new IOException("Restart launch was cancelled.");
                if(MenuSafety().Length>0)throw new InvalidOperationException(MenuSafety());
                Message(target,"Restarting with the server's mods. Loading will finish before this page reopens.");owner.StartCoroutine(QuitWhenHelperReady(ready,ticket,target,generation));
            }
            catch(Exception ex){CancelRestartHandshake();busy=false;ClearPending();Message(target,"Could not restart: "+ex.Message);}
        }
        private static string LaunchRestartHelper(string helper,string ready)
        {
            try
            {
                if(File.Exists(ready+".cancel"))throw new IOException("Restart launch was cancelled.");
                using(var process=Process.GetCurrentProcess())
                {
                    string exe=process.MainModule.FileName;
                    string arguments=process.Id+" "+process.StartTime.ToUniversalTime().Ticks+" \""+exe+"\" \""+ready+"\"";
                    WindowsRestartLauncher.Start(helper,arguments,Path.GetDirectoryName(helper)!);return ready;
                }
            }
            catch{try{File.WriteAllText(ready+".cancel","cancel");File.Delete(ready);}catch{}throw;}
        }
        private void ClearPending(){try{string path=Path.Combine(Paths.ConfigPath,ServerSetupFiles.Filename);if(File.Exists(path))File.Delete(path);}catch{}}
        private IEnumerator QuitWhenHelperReady(string ready,ServerSetupTicket ticket,LobbyInstance target,int generation)
        {
            float until=Time.unscaledTime+8;
            while(!File.Exists(ready)&&!File.Exists(ready+".cancel")&&Time.unscaledTime<until&&generation==setupGeneration)yield return new WaitForSecondsRealtime(.1f);
            if(File.Exists(ready)&&!File.Exists(ready+".cancel")&&generation==setupGeneration&&Same(ticket.Target,target)&&MenuSafety().Length==0){restartExitConfirmed=true;Application.Quit();yield break;}
            busy=false;try{File.WriteAllText(ready+".cancel","cancel");File.Delete(ready);}catch{}ClearPending();
            if(generation==setupGeneration)Message(target,"Restart helper did not become ready. The game and normal mod selection were kept.");
        }
        private IEnumerator GuardServerReturn(IEnumerator routine)
        {
            while(true)
            {
                bool next=false;object? current=null;Exception? failure=null;
                try { next=routine.MoveNext();if(next)current=routine.Current; }catch(Exception ex) { failure=ex; }
                if(failure!=null)
                {
                    log("Saved server return unavailable: "+failure.Message);
                    if(MenuSafety().Length==0){Open();ui?.Status("Mods loaded, but the saved server page could not reopen. Open Multiplayer and select it again.");}
                    yield break;
                }
                if(!next)yield break;yield return current;
            }
        }
        private IEnumerator ResumeServerPage()
        {
            if(resume==null)yield break;
            var gate=new StartupReadyGate();float until=Time.unscaledTime+300;
            log("Waiting for the native menu and Blueprinter's completed load before returning to the saved server.");
            while(Time.unscaledTime<until&&!gate.Check(Time.unscaledTime,MainMenu.State==MainMenu.LoadingState.Loaded,loader.LoadingFinished,InMenuScene(SceneManager.GetActiveScene(),NuclearOption.SceneLoading.MapLoader.MainMenu)&&UnityEngine.EventSystems.EventSystem.current!=null&&UnityEngine.EventSystems.EventSystem.current.isActiveAndEnabled&&MenuSafety().Length==0))yield return new WaitForSecondsRealtime(.25f);
            if(Time.unscaledTime>=until){log("Saved server return timed out while loading. Open the server browser when loading finishes.");yield break;}
            if(loader.Content.Count==0)
            {
                var verify=Task.Run(()=>HashInventory(resume,Paths.PluginPath,true),updateLifetime.Token);while(!verify.IsCompleted)yield return null;
                if(!verify.IsFaulted&&!verify.IsCanceled)foreach(var record in resume.Inventory)loader.Content.Add(record.ToInstalled());
            }
            resume.ResumePending=false;try{ServerSetupFiles.Save(Paths.ConfigPath,resume);}catch(Exception ex){log("Could not consume saved server return: "+ex.Message);yield break;}
            var menu=Resources.FindObjectsOfTypeAll<MainMenu>().FirstOrDefault(m=>m&&m.isActiveAndEnabled&&m.gameObject.scene.IsValid());
            if(menu==null)yield break;menu.SelectMultiplayer();
            LobbyList? browser=null;until=Time.unscaledTime+60;
            while(Time.unscaledTime<until){browser=Resources.FindObjectsOfTypeAll<LobbyList>().FirstOrDefault(b=>b&&b.isActiveAndEnabled&&b.gameObject.scene.IsValid());if(browser!=null&&MenuSafety().Length==0)break;yield return new WaitForSecondsRealtime(.25f);}
            if(browser==null){log("Could not open the server browser after restart.");yield break;}
            yield return new WaitForSecondsRealtime(1);
            BrowserHooks.RequestSavedServer(browser);
            until=Time.unscaledTime+90;LobbyInstance? found=null;
            try
            {
                while(Time.unscaledTime<until&&browser!=null&&browser.isActiveAndEnabled)
                {
                    var cache=AccessTools.Field(typeof(SteamLobby),"_lobbyData").GetValue(SteamLobby.instance) as IDictionary;
                    found=cache?.Values.OfType<LobbyInstance>().FirstOrDefault(l=>{var actual=Snapshot(l,"");return resume.Target.Matches(actual.Dedicated,actual.SteamId,actual.Ip,actual.Port)&&actual.Wire.Length>0;});
                    if(found!=null)break;yield return new WaitForSecondsRealtime(.25f);
                }
            }
            finally { BrowserHooks.EndSavedServerLookup(browser!); }
            if(found==null){Open();ui?.Status("Mods loaded, but "+resume.Target.Name+" was not found. Refresh the browser or try again later.");yield break;}
            workflowTarget=Snapshot(found,owner.ExpandedFor(found));workflowMessage=found.HostVersion==Compatibility.Wire?"Mods ready after restart. Click Join server.":"The server changed or these files still differ. Review its current checklist before joining.";
            browser!.ShowLobbyPopup(found);owner.RefreshServerActions();
        }
    }
}
