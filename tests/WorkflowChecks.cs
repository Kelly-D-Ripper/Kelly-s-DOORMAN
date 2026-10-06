using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class WorkflowRegression
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client,string helperPath,string game)
    {
        if(client.MainModule.AssemblyResolver is DefaultAssemblyResolver resolver)resolver.AddSearchDirectory(Path.Combine(game,"BepInEx","core"));
        const string bp="com.nikkorap.blueprinter";
        var a=new InstalledContent { Id="A",Version="1",Enabled=true,Reloadable=true };
        var b=new InstalledContent { Id="B",Version="2",Enabled=false,Reloadable=true };
        string local="0.34.1_"+bp+"-v2.0.1_--A-v1";
        string modded="0.34.1_"+bp+"-v2.0.1_--B-v2";
        var vanilla=ServerSetupPlan.Create("0.34.1",local,new[]{a,b},"2.0.1",true,false);
        check(vanilla.CanSetup&&vanilla.Vanilla&&vanilla.NeedsRestart&&vanilla.DisabledContent.OrderBy(x=>x).SequenceEqual(new[]{"A","B"}),"vanilla server setup includes the loader restart, not just removing bundles");
        check(ServerSetupPlan.Create("0.34.1","0.34.1",Array.Empty<InstalledContent>(),"2.0.1",false,false).CanSetup&&!ServerSetupPlan.Create("0.34.1","0.34.1",Array.Empty<InstalledContent>(),"2.0.1",false,false).NeedsRestart,"already vanilla client needs no restart");
        var hot=ServerSetupPlan.Create(modded,local,new[]{a,b},"2.0.1",true,true);
        check(hot.CanSetup&&!hot.NeedsRestart&&hot.DisabledContent.SequenceEqual(new[]{"A"}),"reloadable exact local packs produce a hot setup plan");
        check(ServerSetupPlan.Create(modded,local,new[]{a,b},"2.0.1",true,false).NeedsRestart,"unavailable reload exposes a restart plan instead of silently joining");
        check(ServerSetupPlan.Create(modded,local,new[]{a,b},"2.0.1",false,true).NeedsRestart,"a disabled loader is enabled only by restart setup");
        check(!ServerSetupPlan.Create(modded.Replace("B-v2","B-v3"),local,new[]{a,b},"2.0.1",true,true).CanSetup,"wrong local content versions cannot be guessed or partially applied");
        check(!ServerSetupPlan.Create(modded,local,new[]{a},"2.0.1",true,true).CanSetup,"missing content blocks setup without changing the selection");
        check(!ServerSetupPlan.Create(modded,local,new[]{a,b,b},"2.0.1",true,true).CanSetup,"duplicate required packs block setup");
        check(!ServerSetupPlan.Create(modded.Replace("0.34.1","0.35"),local,new[]{a,b},"2.0.1",true,true).CanSetup,"server setup cannot change a game build");
        check(!ServerSetupPlan.Create("0.34.1_abcdef123456",local,new[]{a,b},"2.0.1",true,true).CanSetup,"opaque server lists never imply guessed removals");
        check(!ServerSetupPlan.Create(modded,local,new[]{a,b},"2.0.2",true,true).CanSetup,"server setup requires the exact installed loader");
        string oldViper="0.34.1_"+bp+"-v2.0.1_--aryx_f16m-v1.2.2",newViper=oldViper.Replace("1.2.2","1.2.3");
        var viper=new InstalledContent { Id="aryx_f16m",Version="1.2.2",Enabled=true,Reloadable=true };
        var projected=ServerSetupPlan.Create(newViper,oldViper,new[]{viper},"2.0.1",true,true,new[]{new KeyValuePair<string,string>(viper.Id,"1.2.3")},true);
        check(projected.CanSetup&&projected.NeedsRestart&&projected.DisabledContent.Length==0&&projected.NextVersions[viper.Id]=="1.2.3","downloaded F-16 1.2.3 satisfies server setup only through restart installation");
        check(viper.Version=="1.2.2"&&viper.Enabled,"a queued release never changes the currently running version or join compatibility");
        check(!ServerSetupPlan.Create(oldViper,oldViper,new[]{viper},"2.0.1",true,true,new[]{new KeyValuePair<string,string>(viper.Id,"1.2.3")},true).CanSetup,"a queued version which differs from the server cannot silently replace its required version");
        check(ServerSetupPlan.Create(newViper,oldViper,new[]{viper},"2.0.1",true,true,new[]{new KeyValuePair<string,string>(viper.Id,"v1.2.3")},true).CanSetup,"release tag prefixes can describe a queued exact required version");
        check(ServerSetupPlan.Create(oldViper,oldViper,new[]{viper},"2.0.1",true,true,null,true).NeedsRestart,"an unrelated downloaded plugin also requires restart instead of a false hot-reload success");
        check(!ServerSetupPlan.Create(newViper,oldViper,new[]{viper},"2.0.1",true,true,new[]{new KeyValuePair<string,string>(viper.Id,"1.2.3"),new KeyValuePair<string,string>(viper.Id,"1.2.4")},true).CanSetup,"ambiguous queued content versions reject restart selection");
        var steamServer=new ServerTarget { Name="Ground Zero",Dedicated=true,SteamId=12345,Wire="0.34.1",Expanded="0.34.1" };steamServer.Validate();
        check(steamServer.Matches(true,12345,"","")&&!steamServer.Matches(true,54321,"","")&&!steamServer.Matches(false,12345,"",""),"Steam dedicated identities accept empty UDP fields without matching a different server");
        var emptyServer=new ServerTarget { Dedicated=true,Wire="0.34.1",Expanded="0.34.1" };try{emptyServer.Validate();check(false,"empty identity");}catch(InvalidDataException){check(true,"dedicated servers still require an actual Steam identity or endpoint");}
        check(ServerTarget.Address(0x7f000001)=="127.0.0.1"&&ServerTarget.Address(0)=="","Steam server IPv4 values map to stable endpoint addresses");
        var disabled=new HashSet<string>{bp,"kelly.nuclearoption.joincheck","kelly.nuclearoption.joincheck.server","wrapper"};
        check(!ServerSetupFiles.KeepPlugin(bp,disabled,true)&&ServerSetupFiles.KeepPlugin(bp,disabled,false),"only the vanilla startup selection can exclude protected Blueprinter");
        check(ServerSetupFiles.KeepPlugin("kelly.nuclearoption.joincheck",disabled,true)&&ServerSetupFiles.KeepPlugin("kelly.nuclearoption.joincheck.server",disabled,true),"server setup cannot exclude DOORMAN components");
        check(!ServerSetupFiles.KeepPlugin("wrapper",disabled,true),"vanilla startup excludes a selected content wrapper");
        var pluginGraph=new[]{new KeyValuePair<string,string[]>(bp,Array.Empty<string>()),new KeyValuePair<string,string[]>("wrapper",new[]{bp,"library"}),new KeyValuePair<string,string[]>("library",Array.Empty<string>()),new KeyValuePair<string,string[]>("dependent",new[]{"wrapper"})};
        var moddedPlugins=ServerPluginSelection.Resolve(pluginGraph,new[]{"wrapper","library","dependent"},new[]{"wrapper"},new[]{"wrapper"},false);
        check(!moddedPlugins.Contains("wrapper")&&!moddedPlugins.Contains("library")&&moddedPlugins.Contains("dependent"),"required content enables its hard dependencies without enabling unrelated saved plugins");
        var vanillaPlugins=ServerPluginSelection.Resolve(pluginGraph,Array.Empty<string>(),new[]{"wrapper"},Array.Empty<string>(),true);
        check(vanillaPlugins.Contains(bp)&&vanillaPlugins.Contains("wrapper")&&vanillaPlugins.Contains("dependent")&&!vanillaPlugins.Contains("library"),"vanilla setup also excludes plugins which require an excluded content wrapper");
        var clients=pluginGraph.Concat(new[]{new KeyValuePair<string,string[]>("client.ui",Array.Empty<string>()),new KeyValuePair<string,string[]>("client.replay",Array.Empty<string>()),new KeyValuePair<string,string[]>("client.off",Array.Empty<string>()),new KeyValuePair<string,string[]>("kelly.nuclearoption.joincheck",Array.Empty<string>()),new KeyValuePair<string,string[]>("kelly.nuclearoption.joincheck.server",Array.Empty<string>())}).ToArray();
        var preserved=ServerPluginSelection.Resolve(clients,new[]{"client.off","kelly.nuclearoption.joincheck","kelly.nuclearoption.joincheck.server"},new[]{"wrapper"},Array.Empty<string>(),true);
        check(preserved.Contains("client.off")&&!preserved.Contains("client.ui")&&!preserved.Contains("client.replay"),"vanilla setup preserves unrelated client plugins and the user's explicitly disabled choices");
        check(!preserved.Any(ModProfile.Doorman),"automatic setup removes DOORMAN from any inherited disabled selection");
        check(ServerSetupFiles.KeepPlugin("KELLY.NUCLEAROPTION.JOINCHECK",new HashSet<string>{"KELLY.NUCLEAROPTION.JOINCHECK"},true),"DOORMAN exemption also survives differently cased saved identifiers");
        try{ServerPluginSelection.Resolve(pluginGraph.Where(p=>p.Key!="library"),Array.Empty<string>(),new[]{"wrapper"},new[]{"wrapper"},false);check(false,"missing dependency");}catch(InvalidDataException){check(true,"missing required hard dependencies block restart preparation");}
        var cycle=new[]{new KeyValuePair<string,string[]>(bp,Array.Empty<string>()),new KeyValuePair<string,string[]>("one",new[]{"two"}),new KeyValuePair<string,string[]>("two",new[]{"one"})};
        check(ServerPluginSelection.Resolve(cycle,new[]{"one","two"},new[]{"one"},new[]{"one"},false).Length==0,"dependency cycles terminate while producing a consistent required selection");
        try{ServerPluginSelection.Resolve(pluginGraph.Concat(new[]{new KeyValuePair<string,string[]>("kelly.nuclearoption.joincheck",new[]{"wrapper"})}),Array.Empty<string>(),new[]{"wrapper"},Array.Empty<string>(),true);check(false,"protected dependency");}catch(InvalidDataException){check(true,"vanilla selection cannot strand a protected component's dependency");}
        var gate=new StartupReadyGate();
        check(!gate.Check(0,true,false,true)&&!gate.Check(10,true,false,true),"the first main menu never resumes before Blueprinter completes");
        check(!gate.Check(11,true,true,true)&&!gate.Check(12,true,true,true),"completed loading still requires a stable interactive menu");
        check(!gate.Check(12.5,false,true,true)&&!gate.Check(15,true,true,true)&&gate.Check(17,true,true,true),"another loading transition resets the stability wait");
        check(!gate.Check(20,true,true,false),"a busy network or scene transition blocks return");
        using(var gameAssembly=AssemblyDefinition.ReadAssembly(Path.Combine(game,"NuclearOption_Data","Managed","Assembly-CSharp.dll")))
        {
            var keys=gameAssembly.MainModule.GetType("NuclearOption.SceneLoading.MapLoader").Methods.Single(m=>m.Name==".cctor").Body.Instructions;
            string main=keys.Single(i=>i.Operand is FieldReference f&&f.Name=="MainMenu"&&i.OpCode.Code==Mono.Cecil.Cil.Code.Stsfld).Previous.Operand as string??"";
            string browser=keys.Single(i=>i.Operand is FieldReference f&&f.Name=="MultiplayerMenu"&&i.OpCode.Code==Mono.Cecil.Cil.Code.Stsfld).Previous.Operand as string??"";
            check(main.EndsWith("/MainMenu.unity",StringComparison.Ordinal)&&browser.EndsWith("/MultiplayerMenu.unity",StringComparison.Ordinal),"installed game menu loading keys are full asset paths rather than Scene.name values");
            check(MenuScene.Matches("MainMenu",main,main)&&MenuScene.Matches("MultiplayerMenu",browser,browser),"actual Unity menu paths select both attachment fallbacks");
            check(MenuScene.Matches("MainMenu","",main)&&MenuScene.Matches("MultiplayerMenu","",browser),"scene basenames match actual loading keys when Unity does not supply a path");
            check("MainMenu"!=main&&"MultiplayerMenu"!=browser&&MenuScene.Matches("MainMenu",main,main),"the reported short-name versus full-path failure is reproduced and corrected");
            check(!MenuScene.Matches("MainMenu",main,browser)&&!MenuScene.Matches("MultiplayerMenu",browser,main)&&!MenuScene.Matches("Heartland","Assets/Scenes/Heartland.unity",main),"browser, main menu and gameplay scenes cannot be confused");
            check(!MenuScene.Matches("MainMenu","Assets/Custom/MainMenu.unity",main),"an unrelated scene with the same short name cannot bypass menu safety");
            check(MenuScene.Matches("MainMenu",main.Replace('/','\\'),main)&&MenuScene.Matches("MainMenu","",main.Replace('/','\\')),"path separators are normalized without reading the filesystem");
            check(!MenuScene.Matches("","",main)&&!MenuScene.Matches("MainMenu","","")&&!MenuScene.Matches("","","Assets/Scenes/.unity"),"empty or malformed scene identities never select a menu");
            var loadedMenu=new StartupReadyGate();
            check(!loadedMenu.Check(0,true,false,MenuScene.Matches("MainMenu",main,main))&&!loadedMenu.Check(10,false,true,MenuScene.Matches("MainMenu",main,main))&&!loadedMenu.Check(11,true,true,MenuScene.Matches("MainMenu",main,main))&&loadedMenu.Check(13,true,true,MenuScene.Matches("MainMenu",main,main)),"correct menu identity still waits for native and content loading plus two stable seconds");
        }
        string json="[{\"id\":\"aryx.fs41\",\"displayName\":\"Eclipse\",\"description\":\"Fleet interceptor\",\"authors\":[\"Aryx\"],\"githubOwner\":\"Aryx3D\",\"githubRepoName\":\"Aryx-s-FS-41-Eclipse\",\"urls\":[{\"name\":\"discord\",\"url\":\"https://discord.gg/shipyard\"},{\"name\":\"bad\",\"url\":\"javascript:bad\"}],\"artifacts\":[{\"fileName\":\"Eclipse.dll\",\"hash\":\"sha256:"+new string('a',64)+"\"}]}]";
        var records=NommMetadata.Parse(Encoding.UTF8.GetBytes(json));
        check(records.Single().Name=="Eclipse"&&NommMetadata.Parse(Encoding.UTF8.GetBytes("{\"version\":\"old\",\"manifest\":"+json+"}")).Length==1,"both current registry and NOMM's wrapped local cache are supported");
        check(NommMetadata.Match(records,"Aryx_NavalInterceptor1","","")==records[0],"inspected wrapper ID maps to its NOMNOM identity");
        check(NommMetadata.Match(records,"unknown","","Eclipse.dll")==records[0],"exact artifact names support independently named plugins");
        check(NommMetadata.Match(records,"unknown","","",new string('a',64))==records[0],"exact artifact digest matching is supported");
        check(NommMetadata.Match(records,"Eclipse","","")==null,"similar display names never choose a repository");
        check(NommMetadata.Match(records.Concat(records),"unknown","","Eclipse.dll")==null,"ambiguous registry entries do not produce a fallback");
        var preview=NommMetadata.Preview(records[0],"aryx_interceptor1","Aryx_NavalInterceptor1");
        check(preview.FromNomm&&preview.Author=="Aryx"&&preview.Links.Length==1&&preview.Update?.Repository=="Aryx3D/Aryx-s-FS-41-Eclipse","fallback preserves credits, supplies update repository and rejects unsafe links");
        try{NommMetadata.Parse(new byte[NommMetadata.MaxBytes+1]);check(false,"large registry");}catch(InvalidDataException){check(true,"oversized registry is rejected before parsing");}
        string temp=Path.Combine(Path.GetTempPath(),"doorman-workflow-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            SourceObservationChecks(check);
            ResetRecoveryChecks(check,Path.Combine(temp,"recovery"));
            var ticket=new ServerSetupTicket { Target=new ServerTarget { Name="Test",Dedicated=true,Ip="127.0.0.1",Port="7777",Wire="0.34.1",Expanded="0.34.1" },DisabledPlugins=new[]{bp,"wrapper"},DisabledContent=new[]{"A"},Inventory=new[]{new ContentRecord { Id="A",Name="Pack A",Version="1",Source="Pack.nobp",RelativeFile="Pack.nobp",Digest=new string('a',64) }} };
            ServerSetupFiles.Save(temp,ticket);check(ServerSetupFiles.Pending(temp)?.DisabledPlugins.Contains(bp)==true,"restart selection is persisted separately from normal preferences");
            ticket.ResumePending=false;ServerSetupFiles.Save(temp,ticket);check(ServerSetupFiles.Pending(temp)==null,"consumed return tickets cannot repeat on every normal launch");
            check(File.Exists(Path.Combine(temp,ServerSetupFiles.Filename)+".bak"),"return ticket replacement retains recovery evidence");
            ticket.ResumePending=true;ticket.Target.Expanded=modded;try{ticket.Validate();check(false,"modded loader disable");}catch(InvalidDataException){check(true,"a modded ticket cannot exclude Blueprinter");}
            ticket.Target.Expanded="0.34.1";ticket.DisabledPlugins=new[]{"kelly.nuclearoption.joincheck"};try{ticket.Validate();check(false,"DOORMAN disable");}catch(InvalidDataException){check(true,"ticket validation protects DOORMAN");}
            ticket.DisabledPlugins=Array.Empty<string>();ticket.Inventory[0].RelativeFile="../outside.dll";try{ticket.Validate();check(false,"inventory traversal");}catch(InvalidDataException){check(true,"the startup filter rejects a saved inventory traversal before plugin selection");}ticket.Inventory[0].RelativeFile="Pack.nobp";
            ticket.DisabledPlugins=Array.Empty<string>();ticket.CreatedUtc=DateTimeOffset.UtcNow.AddHours(-7).ToUnixTimeSeconds();try{ticket.Validate();check(false,"expired ticket");}catch(InvalidDataException){check(true,"stale restart tickets expire safely");}
            check(ticket.Target.Matches(true,0,"127.0.0.1","7777")&&!ticket.Target.Matches(true,0,"127.0.0.1","7778"),"return matches exact dedicated endpoint, not just server name");
            check(ServerSetupFiles.RelativeContent(temp,Path.Combine(temp,"Pack.nobp"))=="Pack.nobp","standalone Blueprinter bundles can be snapshotted without treating them as DLL updates");
            try{ServerSetupFiles.RelativeContent(temp,Path.Combine(temp,"..","outside.dll"));check(false,"outside source");}catch(InvalidDataException){check(true,"restart inventory cannot read files outside plugins");}
            string old=Path.Combine(temp,"nomm.json");File.WriteAllText(old,"{\"manifest\":"+json+"}");
            using(var service=new NommMetadataService(temp,old,new Handler(_=>new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))))
            { check(service.GetAsync(CancellationToken.None).GetAwaiter().GetResult().Length==1,"offline metadata lookup retains the local NOMM cache"); }
            var handler=new Handler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
            using(var service=new NommMetadataService(temp,old,handler))
            { check(service.GetAsync(CancellationToken.None).GetAwaiter().GetResult().Length==1&&service.GetAsync(CancellationToken.None).GetAwaiter().GetResult().Length==1&&handler.Calls==1,"registry requests are cached and never run once per card or frame");check(service.ImageAsync("http://127.0.0.1/file.png",CancellationToken.None).GetAwaiter().GetResult()==null,"remote previews reject local/plain HTTP image sources"); }
        }
        finally{Directory.Delete(temp,true);}
        var calls=All(client.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand).ToArray();
        check(calls.Any(m=>m.Name=="SelectMultiplayer")&&calls.Any(m=>m.Name=="ShowLobbyPopup"),"restart return uses the game's native browser and server page");
        check(calls.Any(m=>m.Name=="RequestSavedServer")&&calls.Any(m=>m.Name=="EndSavedServerLookup"),"saved-server return uses and releases the existing dedicated discovery adapter");
        var manager=client.MainModule.GetType("KellysJOINCHECK.ModManager");
        var setup=manager.NestedTypes.Single(t=>t.Name.StartsWith("<SetupAfterFrame>",StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext").Body.Instructions.ToList();
        var temporaryWrite=setup.Single(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld&&i.Operand is FieldReference f&&f.Name=="temporarySelection");
        var restoreLatch=setup.Take(setup.IndexOf(temporaryWrite)).TakeLast(8).ToArray();
        check(restoreLatch.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Or)&&restoreLatch.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Ldfld&&i.Operand is FieldReference f&&f.Name=="temporarySelection"),"repeating a matching Setup mods keeps the earlier temporary selection marked for normal-profile restoration");
        RestartProfileCommitChecks(check,manager);
        var apply=manager.Methods.Single(m=>m.Name=="ApplySelection").Body.Instructions;
        check(!apply.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld&&i.Operand is FieldReference f&&f.Name=="listDisabledContent"),"blocked selection and cancelled restart prompts keep imported absent-content exclusions for the next Reload");
        var committed=manager.Methods.Single(m=>m.Name=="ApplyNow").Body.Instructions.ToList();
        var hotSave=committed.Single(i=>i.Operand is MethodReference m&&m.Name=="Save"&&m.DeclaringType.FullName=="KellysJOINCHECK.ModJson");
        var hotClear=committed.Single(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld&&i.Operand is FieldReference f&&f.Name=="listDisabledContent");
        check(hotClear.Offset>hotSave.Offset,"imported absent-content exclusions clear only after successful hot profile persistence");
        var launchHelper=manager.Methods.Single(m=>m.Name=="LaunchRestartHelper");
        check(launchHelper.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.WindowsRestartLauncher"&&m.Name=="Start")&&!launchHelper.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.Diagnostics.Process"&&m.Name=="Start"),"client restart workflow delegates its fixed CLR bootstrap to the validated launcher");
        var launchFailure=launchHelper.Body.ExceptionHandlers.Where(h=>h.HandlerType==Mono.Cecil.Cil.ExceptionHandlerType.Catch).SelectMany(h=>launchHelper.Body.Instructions.SkipWhile(i=>i!=h.HandlerStart).TakeWhile(i=>i!=h.HandlerEnd)).ToArray();
        check(launchFailure.Any(i=>i.Operand as string==".cancel")&&launchFailure.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.IO.File"&&m.Name=="WriteAllText"),"a failed desktop broker launch leaves a cancellation marker before normal gameplay continues");
        var broker=client.MainModule.GetType("KellysJOINCHECK.WindowsRestartLauncher");
        var brokerCalls=All(new[]{broker}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand).ToArray();
        var bootstrapWords=All(new[]{broker}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand as string).Where(s=>s!=null).ToArray();
        check(brokerCalls.Any(m=>m.DeclaringType.FullName=="System.Diagnostics.Process"&&m.Name=="Start")&&brokerCalls.Any(m=>m.Name=="WaitForExit")&&brokerCalls.Any(m=>m.Name=="get_ExitCode")&&bootstrapWords.Contains("--broker ")&&bootstrapWords.Contains("KellysDOORMANRestart.exe")&&!brokerCalls.Any(m=>m.Name=="Kill"),"client launcher waits for the fixed broker-mode CLR bootstrap without terminating a process");
        check(new[]{"SteamAppId","SteamGameId","SteamOverlayGameId"}.All(bootstrapWords.Contains)&&brokerCalls.Any(m=>m.Name=="get_EnvironmentVariables")&&brokerCalls.Any(m=>m.DeclaringType.FullName=="System.Collections.Specialized.StringDictionary"&&m.Name=="Remove")&&!brokerCalls.Any(m=>m.DeclaringType.FullName=="System.Environment"&&m.Name=="SetEnvironmentVariable"),"the bootstrap strips its three Steam identity variables without altering the game's environment");
        check(!brokerCalls.Any(m=>m.DeclaringType.FullName=="System.Threading.Thread"&&m.Name=="SetApartmentState"||m.Name=="GetTypeFromCLSID"),"client bootstrap launch keeps desktop COM automation outside Unity Mono");
        foreach(string coroutine in new[]{"<AwaitServerRestartLaunch>","<AwaitModsRestartLaunch>"})
        {
            var awaiting=manager.NestedTypes.Single(t=>t.Name.StartsWith(coroutine,StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext").Body.Instructions;
            check(awaiting.Any(i=>i.Operand is MethodReference m&&m.Name=="get_IsCompleted")&&awaiting.Any(i=>i.Operand is FieldReference f&&f.Name=="<>2__current"&&i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld&&i.Previous.OpCode.Code==Mono.Cecil.Cil.Code.Ldnull)&&!awaiting.Any(i=>i.Operand is MethodReference m&&(m.Name=="WaitForExit"||m.Name=="Wait"||m.Name=="Sleep")),coroutine+" yields game frames while the bootstrap task completes");
        }
        var cancelHandshake=manager.Methods.Single(m=>m.Name=="CancelRestartHandshake").Body.Instructions;
        check(manager.Methods.Single(m=>m.Name=="Dispose").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="CancelRestartHandshake")&&cancelHandshake.Any(i=>i.Operand is FieldReference f&&f.Name=="restartExitConfirmed")&&cancelHandshake.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Brtrue||i.OpCode.Code==Mono.Cecil.Cil.Code.Brtrue_S),"disposing the client cancels an unconfirmed handshake while respecting the explicit restart quit");
        var reload=manager.Methods.Single(m=>m.Name=="ApplySelectionCore").Body.Instructions;
        check(reload.Any(i=>i.Operand is MethodReference m&&m.Name=="PendingUpdates")&&reload.Any(i=>i.Operand is MethodReference m&&m.Name=="PromptRestart"),"Reload mods considers queued downloads and shows an explicit restart prompt");
        var snapshot=manager.Methods.Single(m=>m.Name=="Snapshot").Body.Instructions;
        check(snapshot.Any(i=>i.Operand is MethodReference m&&m.Name=="GetIP")&&snapshot.Any(i=>i.Operand is MethodReference m&&m.Name=="GetConnectionPort"),"dedicated snapshots use actual Steam connection details rather than empty UDP properties");
        var ticketSource=manager.Methods.Single(m=>m.Name=="TicketContent").Body.Instructions;
        check(new[]{"ObservedSourceLength","ObservedSourceWriteTicks","Digest"}.All(name=>ticketSource.Any(i=>i.Operand is FieldReference f&&f.Name==name)),"server ticket construction carries running source observations or an existing exact digest instead of inventing receipt freshness");
        var hashing=manager.Methods.Single(m=>m.Name=="HashInventory").Body.Instructions;
        check(hashing.Any(i=>i.Operand is MethodReference m&&m.Name=="VerifySourceObservation")&&hashing.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.IO.FileSystemInfo"&&m.Name=="Refresh"),"restart hashing uses the tested source-binding gate and checks for disk changes during verification");
        check(calls.Any(m=>m.Name=="VerifyPending")&&calls.Any(m=>m.Name=="RestartMods"),"both restart workflows verify queued updates before normal exit");
        var scene=manager.Methods.Single(m=>m.Name=="SceneLoaded").Body.Instructions;
        check(scene.Any(i=>i.Operand is MethodReference m&&m.Name=="BeginMenuAttachment")&&scene.Any(i=>i.Operand is MethodReference m&&m.Name=="AttachBrowserAfterFrame"),"menu and browser attachment have a scene-event fallback independent of cached Unity Start methods");
        check(scene.Count(i=>i.Operand is MethodReference m&&m.Name=="InMenuScene")==2&&manager.Methods.Single(m=>m.Name=="MenuSafety").Body.Instructions.Count(i=>i.Operand is MethodReference m&&m.Name=="InMenuScene")==2,"both scene-event branches and menu safety use the tested path-aware comparison");
        var sceneMatcher=manager.Methods.Single(m=>m.Name=="InMenuScene").Body.Instructions;
        check(sceneMatcher.Any(i=>i.Operand is MethodReference m&&m.Name=="get_path")&&sceneMatcher.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name=="MenuScene"&&m.Name=="Matches"),"Unity scene attachment passes its actual path into the same tested matcher");
        foreach(string coroutine in new[]{"<AttachMenuWhenReady>","<ResumeServerPage>"})
            check(manager.NestedTypes.Single(t=>t.Name.StartsWith(coroutine,StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext").Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="InMenuScene"),coroutine+" uses the tested menu identity before acknowledging startup");
        check(calls.Any(m=>m.Name=="EnsureControls")&&calls.Any(m=>m.DeclaringType.Name=="RestartStatus"&&m.Name=="Write"),"completed native startup can recover browser controls and finish the external restart status");
        var dependencies=client.MainModule.GetType("KellysJOINCHECK.ClientPlugin").CustomAttributes.Where(a=>a.AttributeType.FullName=="BepInEx.BepInDependency").ToArray();
        check(dependencies.Any(a=>(string)a.ConstructorArguments[0].Value==bp&&Convert.ToInt32(a.ConstructorArguments[1].Value)==2),"DOORMAN needs Blueprinter only as a soft dependency and can run on a vanilla client");
        var savedQuery=client.MainModule.GetType("KellysJOINCHECK.BrowserHooks").Methods.Single(m=>m.Name=="SavedServerQuery").Body.Instructions;
        check(savedQuery.Any(i=>i.Operand is FieldReference f&&f.Name=="queryingBrowser")&&savedQuery.Any(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Bne_Un_S||i.OpCode.Code==Mono.Cecil.Cil.Code.Bne_Un)&&savedQuery.Any(i=>i.Operand is FieldReference f&&f.Name=="savedServerBrowser"&&i.OpCode.Code==Mono.Cecil.Cil.Code.Stsfld),"broad return lookup is scoped to the requested browser and consumes its one-use flag");
        check(calls.Any(m=>m.Name=="KeepPlugin")==false,"runtime plugin never filters the already loaded code assemblies");
        var words=All(client.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Select(i=>i.Operand as string).Where(s=>s!=null).ToArray();
        check(words.Contains("Setup mods")&&words.Contains("Join server")&&words.Contains("Proceed")&&words.Contains("Auto match mods when joining a server"),"native setup actions and the exact requested checkbox wording are compiled");
        using(var helper=AssemblyDefinition.ReadAssembly(helperPath))
        { var instructions=All(helper.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();check(instructions.Any(i=>i.Operand as string=="steam://run/2168680")&&instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="WaitForExit")&&!instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="Kill"),"restart helper waits for exit, launches only Nuclear Option and never kills a process");check(instructions.Count(i=>i.Operand as string==".cancel")>=2,"restart helper checks cancellation both before becoming ready and before relaunch"); }
    }
    private static void RestartProfileCommitChecks(Action<bool,string> check,TypeDefinition manager)
    {
        foreach(string coroutine in new[]{"<PrepareModsRestart>","<AwaitModsRestartLaunch>"})
        {
            var method=manager.NestedTypes.Single(t=>t.Name.StartsWith(coroutine,StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext");
            check(!method.Body.Instructions.Any(i=>i.Operand is MethodReference m&&(m.DeclaringType.FullName=="KellysJOINCHECK.ModJson"&&m.Name=="Save"||m.DeclaringType.FullName=="KellysJOINCHECK.ModManager"&&m.Name=="ClearPending")),coroutine+" leaves the normal profile and server return intact during cancellable verification and broker preparation");
        }
        var ready=manager.NestedTypes.Single(t=>t.Name.StartsWith("<QuitForModsWhenReady>",StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext");
        var code=ready.Body.Instructions.ToList();
        var save=code.Single(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.ModJson"&&m.Name=="Save");
        var clear=code.Single(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.ModManager"&&m.Name=="ClearPending");
        var quit=code.Single(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="UnityEngine.Application"&&m.Name=="Quit");
        check(save.Offset<clear.Offset&&clear.Offset<quit.Offset,"a ready restart commits the selected normal profile before clearing the old server ticket and quitting");
        var listClear=code.Single(i=>i.OpCode.Code==Mono.Cecil.Cil.Code.Stfld&&i.Operand is FieldReference f&&f.Name=="listDisabledContent");
        check(save.Offset<listClear.Offset&&listClear.Offset<quit.Offset,"imported absent-content exclusions survive restart cancellation and clear with the ready profile commit");
        var before=code.TakeWhile(i=>i!=save).ToArray();
        check(before.Count(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.IO.File"&&m.Name=="Exists")>=4&&before.Any(i=>i.Operand as string==".cancel")&&before.Any(i=>i.Operand is MethodReference m&&m.Name=="get_IsOpen"),"profile commit remains behind ready-marker, cancellation and open-menu checks");
        var safety=before.Last(i=>i.Operand is MethodReference m&&m.Name=="MenuSafety");
        check(code.Skip(code.IndexOf(safety)).TakeWhile(i=>i!=save).Any(i=>i.OpCode.FlowControl==Mono.Cecil.Cil.FlowControl.Cond_Branch&&i.Operand is Mono.Cecil.Cil.Instruction target&&target.Offset>quit.Offset),"a failed final menu-safety guard skips both profile commit and application quit");
        var failure=ready.Body.ExceptionHandlers.Where(h=>h.HandlerType==Mono.Cecil.Cil.ExceptionHandlerType.Catch).SelectMany(h=>code.SkipWhile(i=>i!=h.HandlerStart).TakeWhile(i=>i!=h.HandlerEnd)).ToArray();
        check(failure.Any(i=>i.Operand is MethodReference m&&m.Name=="CancelRestartHandshake")&&!failure.Any(i=>i.Operand is MethodReference m&&m.Name=="Quit"),"failure saving the selected profile cancels the detached helper and keeps the game open");
    }
    private static void SourceObservationChecks(Action<bool,string> check)
    {
        string digest=new string('a',64),changed=new string('b',64);
        var observed=new ContentRecord {Id="pack",RelativeFile="wrapper.dll",ObservedSourceLength=100,ObservedSourceWriteTicks=200};
        ServerSetupFiles.VerifySourceObservation(observed,100,200,digest);
        check(true,"running bundle metadata can bind to its unchanged observed source");
        foreach(var stamp in new[]{(Length:101L,Ticks:200L),(Length:100L,Ticks:201L)})
        {try{ServerSetupFiles.VerifySourceObservation(observed,stamp.Length,stamp.Ticks,changed);check(false,"stale running metadata");}catch(IOException){check(true,"changed source cannot acquire a fresh digest for stale running bundle metadata");}}
        var cached=new ContentRecord {Id="pack",RelativeFile="wrapper.dll",Digest=digest};
        ServerSetupFiles.VerifySourceObservation(cached,101,201,digest.ToUpperInvariant());
        check(true,"disabled content from a previous server ticket remains usable only with its exact prior source digest");
        foreach(string prior in new[]{"",changed})
        {cached.Digest=prior;try{ServerSetupFiles.VerifySourceObservation(cached,100,200,digest);check(false,"unbound cached metadata");}catch(IOException){check(true,"missing or changed cached source evidence blocks restart preparation");}}
        var queued=new PendingModUpdate {Target="wrapper.dll",OldHash=digest,NewHash=changed};
        ServerSetupFiles.VerifySourceObservation(observed,101,201,digest,queued);
        check(true,"an explicitly verified update can project its new metadata from the exact original DLL hash");
        try{ServerSetupFiles.VerifySourceObservation(observed,100,200,changed,queued);check(false,"changed queued original");}catch(IOException){check(true,"even a projected update rejects a changed original source");}
        queued.Target="other.dll";try{ServerSetupFiles.VerifySourceObservation(observed,100,200,digest,queued);check(false,"unrelated source update");}catch(IOException){check(true,"an unrelated queued DLL cannot validate a stale content source");}
    }
    private static void ResetRecoveryChecks(Action<bool,string> check,string directory)
    {
        Directory.CreateDirectory(directory);string profilePath=Path.Combine(directory,"doorman-mods.json");
        ModJson.Save(profilePath,new ModProfile {DisabledPlugins=new[]{"wrapper"},DisabledContent=new[]{"pack"}});
        var ticket=new ServerSetupTicket {Target=new ServerTarget {Dedicated=true,SteamId=123,Wire="0.34.1",Expanded="0.34.1"},DisabledPlugins=new[]{"wrapper","com.nikkorap.blueprinter"},DisabledContent=new[]{"pack"}};
        ServerSetupFiles.Save(directory,ticket);
        check(ServerSetupFiles.Pending(directory)!=null&&ModJson.Read<ModProfile>(profilePath).DisabledContent.Length==1,"recovery fixture starts with both ordinary and temporary exclusions");
        File.Move(profilePath,profilePath+".bak");File.Move(Path.Combine(directory,ServerSetupFiles.Filename),Path.Combine(directory,ServerSetupFiles.Filename)+".bak");
        var normal=File.Exists(profilePath)?ModJson.Read<ModProfile>(profilePath):new ModProfile();normal.Validate();
        check(normal.DisabledPlugins.Length==0&&normal.DisabledContent.Length==0&&ServerSetupFiles.Pending(directory)==null,"renaming both preference JSON files resets plugin/content exclusions and pending return; backups are not read");
        var kept=new[]{"wrapper","client.ui","com.nikkorap.blueprinter","kelly.nuclearoption.joincheck"}.Where(id=>ServerSetupFiles.KeepPlugin(id,new HashSet<string>(normal.DisabledPlugins,StringComparer.Ordinal),false)).ToArray();
        check(kept.Length==4,"reset ordinary selection preserves all installed plugins including previously excluded loader and wrapper");
    }
    private sealed class Handler:HttpMessageHandler
    { private readonly Func<HttpRequestMessage,HttpResponseMessage> response;internal int Calls;internal Handler(Func<HttpRequestMessage,HttpResponseMessage> response){this.response=response;}protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token){Calls++;return Task.FromResult(response(request));} }
    private static IEnumerable<TypeDefinition> All(IEnumerable<TypeDefinition> types){foreach(var type in types){yield return type;foreach(var nested in All(type.NestedTypes))yield return nested;}}
}
