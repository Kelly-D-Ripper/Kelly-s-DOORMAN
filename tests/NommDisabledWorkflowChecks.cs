using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class NommDisabledWorkflowRegression
{
    private const string Loader="com.nikkorap.blueprinter";
    private const string Server="0.34_com.nikkorap.blueprinter-v2.0.1_--exact_pack-v2.0";
    internal static void Run(Action<bool,string> check)
    {
        LegacyTicket(check);
        string temporary=Path.Combine(Path.GetTempPath(),"doorman-nomm-workflow-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temporary);
        try {CombinedRestore(check,temporary);CombinedAddon(check,temporary);CompletedRestoreGuard(check,temporary);}
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(temporary).StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(temporary))Directory.Delete(temporary,true);
        }
    }

    private static void CompletedRestoreGuard(Action<bool,string> check,string temporary)
    {
        string bep=Path.Combine(temporary,"completed-guard","BepInEx"),disabled=Path.Combine(bep,"disabledPlugins"),plugins=Path.Combine(bep,"plugins"),config=Path.Combine(bep,"config");Directory.CreateDirectory(disabled);
        string unit=Path.Combine(disabled,"WholePack"),asset=Path.Combine(unit,"content.nobp"),library=Path.Combine(disabled,"Library.dll");Directory.CreateDirectory(unit);File.WriteAllText(asset,"exact pack bytes");File.WriteAllText(library,"exact library bytes");
        var restores=DisabledModFiles.Inventory(bep).Select(c=>DisabledModFiles.Prepare(bep,c)).ToArray();DisabledModFiles.Verify(bep,restores);
        Reject(check,()=>DisabledModFiles.VerifyApplied(bep,restores),"a valid ready-to-move receipt set is never treated as completed physical restoration");
        check(File.Exists(asset)&&File.Exists(library)&&!Directory.Exists(plugins),"completed-state verification remains read-only for still-disabled units");
        ServerSetupFiles.Save(config,new ServerSetupTicket {Target=Target(),Restores=restores});DisabledRestoreQueue.Apply(config,bep);
        check(DisabledRestoreQueue.Read(config).Length==0&&ServerSetupFiles.Pending(config)!=null,"missing-queue recovery fixture retains a saved server page with unapplied physical receipts");
        Reject(check,()=>DisabledModFiles.VerifyApplied(bep,ServerSetupFiles.Pending(config)!.Restores),"a missing queue cannot allow a saved server page to resume before its selected units are restored");
        Directory.CreateDirectory(plugins);Directory.Move(unit,Path.Combine(plugins,"WholePack"));DisabledModFiles.Verify(bep,restores);
        Reject(check,()=>DisabledModFiles.VerifyApplied(bep,restores),"completed-state verification rejects a receipt set with only some atomic unit moves completed");
        DisabledRestoreQueue.Save(config,restores);DisabledRestoreQueue.Apply(config,bep);DisabledModFiles.VerifyApplied(bep,restores);
        check(File.Exists(Path.Combine(plugins,"WholePack","content.nobp"))&&File.Exists(Path.Combine(plugins,"Library.dll")),"completed-state verification accepts exact whole-package and library restoration after restart recovery");
        File.WriteAllText(Path.Combine(plugins,"WholePack","content.nobp"),"changed pack bytes");
        Reject(check,()=>DisabledModFiles.VerifyApplied(bep,restores),"completed-state verification rejects a changed restored destination rather than trusting source absence");
        File.WriteAllText(Path.Combine(plugins,"WholePack","content.nobp"),"exact pack bytes");File.WriteAllText(library,"exact library bytes");
        Reject(check,()=>DisabledModFiles.VerifyApplied(bep,restores),"completed-state verification rejects reappearing disabled source bytes even when they match the active destination");
    }

    private static void CombinedAddon(Action<bool,string> check,string temporary)
    {
        string bep=Path.Combine(temporary,"addon","BepInEx"),disabled=Path.Combine(bep,"disabledPlugins"),plugins=Path.Combine(bep,"plugins"),config=Path.Combine(bep,"config");Directory.CreateDirectory(disabled);Directory.CreateDirectory(plugins);
        string parent=Path.Combine(disabled,"PhysicalParent"),addon=Path.Combine(disabled,"PhysicalAddon");Directory.CreateDirectory(parent);Directory.CreateDirectory(addon);
        File.WriteAllText(Path.Combine(parent,"meta.json"),"{\"id\":\"parent.registry\"}");
        Dll(parent,"Parent.dll","Parent","addon.parent","1.0",new[]{("addon.library","1.0")},false);
        File.WriteAllText(Path.Combine(addon,"meta.json"),"{\"id\":\"addon.registry\",\"artifact\":{\"extends\":{\"id\":\"parent.registry\"}}}");
        Dll(addon,"Addon.dll","Addon","addon.plugin","1.0",Array.Empty<(string,string)>(),false);
        File.WriteAllText(Path.Combine(addon,"Airframe.nobp"),"{\"modName\":\"Exact pack\",\"modVersion\":\"2.0\"}");
        File.WriteAllText(Path.Combine(addon,"preview.doorman.json"),"{\"bundle\":\"exact_pack\",\"name\":\"Addon pack\",\"requisites\":[{\"plugin\":\"addon.plugin\",\"minimumVersion\":\"1.0\"}]}");
        Dll(disabled,"Library.dll","Library","addon.library","1.0",Array.Empty<(string,string)>(),false);
        var units=DisabledModFiles.Inventory(bep).Select(c=>NommDisabledCatalog.ReadUnit(bep,c,Bundle)).ToArray();check(units.All(u=>u.Error.Length==0),"combined addon discovery resolves registry routing separately from actual plugin/content identities");
        var all=units.SelectMany(u=>u.Entries).Select(Copy).ToArray();var content=all.Single(e=>e.Content);var required=NommDisabledSelection.RequiredEntries(all,new[]{content},units);var selected=NommDisabledSelection.Required(all,new[]{content},units);
        check(selected.Length==3&&required.Any(e=>e.Id=="addon.parent")&&required.Any(e=>e.Id=="addon.library")&&required.Any(e=>e.Id=="addon.plugin"),"copied addon rows retain metadata-proven parent code and that parent's transitive library");
        var restores=selected.Select(u=>NommDisabledCatalog.Prepare(bep,u)).ToArray();DisabledModFiles.Verify(bep,restores);
        string relative="PhysicalParent/addons/addon.registry/Airframe.nobp",source=DisabledRestoreQueue.SourceFor(bep,restores,relative)!;
        check(source==Path.Combine(addon,"Airframe.nobp")&&content.RestoreTarget=="PhysicalParent/addons/addon.registry","nested standalone addon tickets map through the longest metadata-proven restore target");
        var record=new ContentRecord {Id=content.Id,Name=content.Name,Version=content.Version,Source=ModUpdateFiles.Within(plugins,relative),RelativeFile=relative,Digest=ModUpdateFiles.Hash(source)};
        ServerSetupFiles.Save(config,new ServerSetupTicket {Target=Target(),Inventory=new[]{record},Restores=restores});DisabledRestoreQueue.Save(config,restores);DisabledRestoreQueue.Apply(config,bep);
        check(ModUpdateFiles.Hash(ModUpdateFiles.Within(plugins,relative))==record.Digest&&File.Exists(Path.Combine(plugins,"Library.dll")),"preloader queue applies a disabled parent, nested addon and library without flattening the ticket source path");
        DisabledModFiles.Verify(bep,restores);
        DisabledModFiles.VerifyApplied(bep,restores);
        check(ServerSetupFiles.Pending(config)!.Inventory.Single().RelativeFile==relative&&DisabledRestoreQueue.SourceFor(bep,restores,relative)==null,"completed parent/addon restore receipts remain verifiable while the saved server return keeps its projected path");
    }

    private static ServerTarget Target()=>new ServerTarget {Name="Fixture server",SteamId=12345,Wire=Server,Expanded=Server};
    private static void LegacyTicket(Action<bool,string> check)
    {
        // Omitting this newly added optional member reproduces an existing
        // schema-1 ticket, rather than merely constructing a modern empty DTO.
        var original=new ServerSetupTicket {Target=Target(),Restores=null!};
        using(var data=new MemoryStream())
        {
            var serializer=new DataContractJsonSerializer(typeof(ServerSetupTicket));serializer.WriteObject(data,original);
            check(!Encoding.UTF8.GetString(data.ToArray()).Contains("Restores"),"legacy server ticket fixture contains no NOMM restore member");
            data.Position=0;var legacy=(ServerSetupTicket)serializer.ReadObject(data)!;legacy.Validate();
            check(legacy.Restores.Length==0&&legacy.ResumePending&&legacy.Target.SteamId==12345,"existing schema-1 server returns remain valid with an empty NOMM restore selection");
        }
        var receipt=new DisabledModRestore {SourceRelative="one.dll",TargetRelative="one.dll",Digest=new string('a',64)};
        Reject(check,()=>new ServerSetupTicket {Target=Target(),Restores=new[]{receipt,receipt}}.Validate(),"server ticket validation rejects duplicate physical restore receipts");
        Reject(check,()=>new ServerSetupTicket {Target=Target(),Restores=new DisabledModRestore[]{null!}}.Validate(),"server ticket validation rejects null restore members explicitly");
    }

    private static void CombinedRestore(Action<bool,string> check,string temporary)
    {
        string bep=Path.Combine(temporary,"BepInEx"),disabled=Path.Combine(bep,"disabledPlugins"),plugins=Path.Combine(bep,"plugins"),config=Path.Combine(bep,"config");Directory.CreateDirectory(disabled);Directory.CreateDirectory(plugins);
        string package=Path.Combine(disabled,"RegistryNameDoesNotIdentifyPack");Directory.CreateDirectory(package);
        string wrapper=Dll(package,"WrapperDifferentName.dll","ActualWrapperAssembly","fixture.wrapper","1.0",new[]{("fixture.library","1.2"),(Loader,"2.0.1")},true);
        Dll(package,"Companion.dll","Companion","fixture.companion","1.0",Array.Empty<(string,string)>(),false);
        File.WriteAllText(Path.Combine(package,"support.asset"),"whole-package support bytes");
        Dll(disabled,"LibraryDifferentName.dll","LibraryAssembly","fixture.library","1.2",Array.Empty<(string,string)>(),false);
        string unrelated=Path.Combine(disabled,"UnrelatedPackage");Directory.CreateDirectory(unrelated);Dll(unrelated,"Unrelated.dll","Unrelated","fixture.unrelated","1.0",Array.Empty<(string,string)>(),false);File.WriteAllText(Path.Combine(unrelated,"asset.bin"),"stay disabled");
        var units=DisabledModFiles.Inventory(bep).Select(c=>NommDisabledCatalog.ReadUnit(bep,c,Bundle)).ToArray();
        check(units.All(u=>u.Error.Length==0),"combined NOMM fixture discovers real plugin dependencies and embedded content without game loading");
        var inventory=units.SelectMany(u=>u.Entries).Select(Copy).Concat(new[]{new ModEntry {Id=Loader,Name="Blueprinter",Version="2.0.1",Enabled=true,Wanted=true}}).ToArray();
        var pack=inventory.Single(e=>e.Content);var required=NommDisabledSelection.RequiredEntries(inventory,new[]{pack},units);var selected=NommDisabledSelection.Required(inventory,new[]{pack},units);
        check(required.Any(e=>e.Id=="fixture.wrapper")&&required.Any(e=>e.Id=="fixture.library")&&required.Any(e=>e.Id==Loader)&&!required.Any(e=>e.Id=="fixture.companion"||e.Id=="fixture.unrelated"),"copied server rows select exact supporting plugins and transitive dependencies while leaving companions unselected");
        check(selected.Length==2&&selected.Any(u=>u.Candidate.SourceRelative=="RegistryNameDoesNotIdentifyPack")&&selected.Any(u=>u.Candidate.SourceRelative=="LibraryDifferentName.dll"),"content and its separately disabled library select their complete physical NOMM units");
        check(units.SelectMany(u=>u.Entries).All(e=>!e.Wanted),"server planning against copied rows does not change normal Mods-menu ticks");
        var sibling=new ModEntry {Content=true,Id="excluded_sibling",Name="Excluded sibling pack",Version="1.0",WrapperPlugin=pack.WrapperPlugin,Wanted=false};sibling.RequiredRequisites.Add(new ModRequisite {Plugin="fixture.unrelated"});
        var siblingRequired=NommDisabledSelection.RequiredEntries(inventory.Concat(new[]{sibling}),new[]{pack},units);
        check(!siblingRequired.Any(e=>e.Id=="fixture.unrelated"||e.Id=="excluded_sibling"),"a selected pack never inherits prerequisites declared only by an excluded sibling sharing its wrapper");
        var imported=new SavedModList {Name="Content-only import",GameVersion="0.34",Mods=new[]{new SavedModItem {Content=true,Id=pack.Id,Name=pack.Name,Version=pack.Version,Enabled=true}}};
        var listPlan=SavedModListPlan.Create(imported,inventory.Select(e=>new SavedModItem {Content=e.Content,Id=e.Id,Name=e.Name,Version=e.Version,Enabled=e.Wanted}));
        foreach(var entry in inventory)entry.Wanted=listPlan.Selection.Single(s=>s.Content==entry.Content&&s.Id==entry.Id).Enabled;
        foreach(var needed in NommDisabledSelection.RequiredEntries(inventory,inventory.Where(e=>e.ExternalDisabled&&e.Wanted),units))needed.Wanted=true;
        check(listPlan.CanApply&&ModRequisites.ValidateSelection(inventory)==""&&inventory.Single(e=>e.Id=="fixture.wrapper").Wanted&&inventory.Single(e=>e.Id=="fixture.library").Wanted&&!inventory.Single(e=>e.Id=="fixture.unrelated").Wanted,"a content-only saved-list import resolves available NOMM wrapper/library rows before prerequisite validation");
        var graph=inventory.Where(e=>!e.Content).Select(e=>new KeyValuePair<string,string[]>(e.Id,e.Requires.ToArray())).ToArray();
        var disabledPlugins=ServerPluginSelection.Resolve(graph,inventory.Where(e=>e.ExternalDisabled&&!e.Content).Select(e=>e.Id),new[]{pack.WrapperPlugin},new[]{pack.WrapperPlugin},false);
        check(!disabledPlugins.Contains("fixture.wrapper")&&!disabledPlugins.Contains("fixture.library")&&!disabledPlugins.Contains(Loader)&&disabledPlugins.Contains("fixture.companion")&&disabledPlugins.Contains("fixture.unrelated"),"projected server filtering enables required code and keeps other identities disabled even inside a restored package");
        var available=inventory.Where(e=>e.Content).Select(e=>new InstalledContent {Id=e.Id,Name=e.Name,Version=e.Version,Source=e.Path,Enabled=false,Reloadable=false});
        var plan=ServerSetupPlan.Create(Server,"0.34_com.nikkorap.blueprinter-v2.0.1_NOBUNDLES",available,"2.0.1",true,true);
        check(plan.CanSetup&&plan.NeedsRestart,"server setup accepts the actual disabled pack version and requires a restart before using it");
        var restores=selected.Select(u=>NommDisabledCatalog.Prepare(bep,u)).ToArray();DisabledModFiles.Verify(bep,restores);
        string relative="RegistryNameDoesNotIdentifyPack/WrapperDifferentName.dll",source=DisabledRestoreQueue.SourceFor(bep,restores,relative)!;
        check(source==wrapper,"a projected embedded-content ticket resolves its exact disabled wrapper bytes after unit verification");
        var content=new ContentRecord {Id=pack.Id,Name=pack.Name,Version=pack.Version,Source=pack.Path,RelativeFile=relative,Digest=ModUpdateFiles.Hash(source)};
        var ticket=new ServerSetupTicket {Target=Target(),DisabledPlugins=disabledPlugins,Inventory=new[]{content},Restores=restores};
        ServerSetupFiles.Save(config,ticket);DisabledRestoreQueue.Save(config,restores);
        var pending=ServerSetupFiles.Pending(config);
        check(pending!=null&&pending.Restores.Length==2&&pending.Inventory.Single().RelativeFile==relative&&pending.Inventory.Single().Source=="resource:ActualWrapperAssembly:fixture.internal.nobp","durable server return retains physical restore receipts and independent assembly/resource identity");
        check(File.Exists(wrapper)&&!Directory.Exists(Path.Combine(plugins,"RegistryNameDoesNotIdentifyPack")),"writing a projected server return never moves or activates a running DLL");
        DisabledRestoreQueue.Apply(config,bep);
        string restored=ModUpdateFiles.Within(plugins,relative);
        check(File.Exists(restored)&&ModUpdateFiles.Hash(restored)==content.Digest&&File.ReadAllText(Path.Combine(plugins,"RegistryNameDoesNotIdentifyPack","support.asset"))=="whole-package support bytes","simulated preloader startup restores the exact ticket DLL and all support assets");
        check(File.Exists(Path.Combine(plugins,"LibraryDifferentName.dll"))&&Directory.Exists(unrelated)&&File.ReadAllText(Path.Combine(unrelated,"asset.bin"))=="stay disabled","startup restores the transitive library and leaves unrelated NOMM units physically disabled");
        check(File.Exists(Path.Combine(plugins,"RegistryNameDoesNotIdentifyPack","Companion.dll"))&&ticket.DisabledPlugins.Contains("fixture.companion"),"whole-package movement preserves companion bytes without selecting their plugin identity");
        check(DisabledRestoreQueue.SourceFor(bep,restores,relative)==null&&!File.Exists(Path.Combine(config,DisabledRestoreQueue.Filename))&&ServerSetupFiles.Pending(config)!=null,"completed startup consumes only the restore queue and leaves the saved server page available");
        Reject(check,()=>ServerSetupFiles.RelativeContent(plugins,Path.Combine(unrelated,"Unrelated.dll")),"unselected NOMM files remain rejected by the existing plugins-root content guard");
        Reject(check,()=>ContentSourceFiles.Resolve(plugins,"resource:ActualWrapperAssembly:fixture.internal.nobp",Path.Combine(unrelated,"Unrelated.dll"),Array.Empty<LoadedContentAssembly>()),"ordinary content resolution cannot use an unrelated external disabled DLL");

        // Cancellation clears the queued intent while preserving physical source
        // files and backups. Its owner/lifetime gates are asserted below in IL.
        string cancelUnit=Path.Combine(disabled,"Cancel.nobp");File.WriteAllText(cancelUnit,"cancel bytes");
        var cancel=DisabledModFiles.Prepare(bep,DisabledModFiles.Inventory(bep).Single(c=>c.SourceRelative=="Cancel.nobp"));DisabledRestoreQueue.Save(config,new[]{cancel});DisabledRestoreQueue.Save(config,new[]{cancel});
        DisabledRestoreQueue.Clear(config);
        check(File.Exists(cancelUnit)&&!File.Exists(Path.Combine(plugins,"Cancel.nobp"))&&DisabledRestoreQueue.Read(config).Length==0&&File.Exists(Path.Combine(config,DisabledRestoreQueue.Filename)+".bak"),"cancelling a prepared restart removes active restore intent while retaining source bytes and recovery evidence");
    }

    internal static void Contracts(Action<bool,string> check,AssemblyDefinition client,string startupPath)
    {
        var manager=client.MainModule.GetType("KellysJOINCHECK.ModManager");
        var managerMethods=AllTypes(new[]{manager}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).ToArray();
        check(!managerMethods.Any(m=>Calls(m,"DisabledModFiles","Apply")||Calls(m,"DisabledRestoreQueue","Apply")),"client menu/server workflows never move NOMM units inside the running game");
        var prepare=managerMethods.Single(m=>m.Name.StartsWith("<PrepareRestart>b__",StringComparison.Ordinal)&&Calls(m,"ModManager","HashInventory"));
        check(Index(prepare,"DisabledModFiles","Verify")<Index(prepare,"ModManager","HashInventory")&&Index(prepare,"DisabledModFiles","Verify")>=0,"server restart verifies complete selected NOMM units before hashing projected external inventory sources");
        var hash=manager.Methods.Single(m=>m.Name=="HashInventory");
        check(Calls(hash,"DisabledRestoreQueue","SourceFor")&&Calls(hash,"ModUpdateFiles","Within")&&Calls(hash,"ServerSetupFiles","VerifySourceObservation"),"projected ticket hashing retains safe disabled mapping and the existing active-file observation/root guards");
        var ticketContent=manager.Methods.Single(m=>m.Name=="TicketContent");
        check(Calls(ticketContent,"ModManager","RestoredRelative")&&Calls(ticketContent,"ContentSourceFiles","Resolve"),"external ticket paths project through their complete unit while ordinary content retains physical-source verification");
        var refresh=manager.Methods.Single(m=>m.Name=="RefreshExternalUnits");
        check(Index(refresh,"ModManager","MenuSafety")>=0&&Index(refresh,"ModManager","MenuSafety")<Index(refresh,"DisabledModFiles","Inventory")&&Index(refresh,"BlueprinterReload","get_LoadingFinished")<Index(refresh,"DisabledModFiles","Inventory"),"NOMM menu discovery waits for a safe menu and completed content loading");
        var frame=AllTypes(client.MainModule.Types).Where(t=>t.FullName=="KellysJOINCHECK.ModManager"||t.FullName=="KellysJOINCHECK.ClientPlugin").SelectMany(t=>t.Methods).Where(m=>m.HasBody&&new[]{"Update","FixedUpdate","LateUpdate","Tick"}.Contains(m.Name));
        check(!frame.Any(m=>Calls(m,"DisabledModFiles","Inventory")||Calls(m,"NommDisabledCatalog","Observe")||Calls(m,"NommDisabledCatalog","ReadUnit")||Calls(m,"ModManager","RefreshExternalUnits")),"ordinary frame callbacks do not scan disabled folders or parse/hash NOMM metadata");
        var disabledTypes=client.MainModule.Types.Where(t=>new[]{"DisabledModFiles","DisabledRestoreQueue","NommDisabledCatalog","NommDisabledSelection"}.Contains(t.Name));
        check(!AllTypes(disabledTypes).SelectMany(t=>t.Methods).Where(m=>m.HasBody).Any(m=>m.Body.Instructions.Any(i=>i.Operand is MethodReference r&&(r.Name=="InvokeRepeating"||r.DeclaringType.FullName.StartsWith("System.Threading.Timer",StringComparison.Ordinal)||r.DeclaringType.FullName.StartsWith("System.Timers.Timer",StringComparison.Ordinal)))),"disabled-mod support introduces no periodic filesystem timer");
        var normalQuit=Iterator(manager,"QuitForModsWhenReady");int queue=Index(normalQuit,"DisabledRestoreQueue","Save"),profile=Index(normalQuit,"ModJson","Save"),quit=Index(normalQuit,"Application","Quit");
        check(queue>=0&&profile>=0&&queue<quit&&profile<quit,"normal reload persists the queue and committed profile before asking the game to quit");
        check(Index(normalQuit,"File","Exists")>=0&&Index(normalQuit,"ModManager","MenuSafety")>=0&&Index(normalQuit,"ModManager","MenuSafety")<queue,"normal reload checks helper readiness and menu safety before saving restore intent");
        check(!Calls(Iterator(manager,"PrepareModsRestart"),"DisabledRestoreQueue","Save")&&!Calls(Iterator(manager,"AwaitModsRestartLaunch"),"DisabledRestoreQueue","Save"),"cancellable normal-restart verification and helper launch do not persist restore queues");
        check(queue<Index(normalQuit,"File","Delete")&&Index(normalQuit,"File","Delete")<profile&&Calls(normalQuit,"File","ReadAllBytes")&&Calls(normalQuit,"File","WriteAllBytes"),"normal restart preserves bounded old server-page bytes and removes that page before committing the new profile, with a failure recovery path");
        check(CleanupIsConditional(normalQuit,"DisabledRestoreQueue","Clear"),"normal restart failure clears only a newly saved queue and preserves pre-existing intent when Save fails");
        var cancel=manager.Methods.Single(m=>m.Name=="CancelOwnedRestart");
        check(Calls(cancel,"ModManager","ClearPending")&&ReadsField(cancel,"ownsPendingRestart")&&ReadsField(cancel,"restartExitConfirmed"),"restart cancellation clears only this manager's owned queue before confirmed quit");
        check(Calls(manager.Methods.Single(m=>m.Name=="CancelServerSetup"),"ModManager","CancelOwnedRestart")&&Calls(manager.Methods.Single(m=>m.Name=="Dispose"),"ModManager","CancelOwnedRestart"),"both user cancellation and manager disposal synchronously cancel owned unconfirmed restore intent");
        check(Calls(manager.Methods.Single(m=>m.Name=="ClearPending"),"DisabledRestoreQueue","Clear"),"pending server cancellation also clears the independent physical-restore queue");
        var cachedWrappers=managerMethods.Where(m=>m.Name.Contains("<FinishKnownContent>")&&Calls(m,"ServerSetupFiles","RelativeContent")).ToArray();
        check(cachedWrappers.Length>0&&cachedWrappers.All(m=>ReadsField(m,"ExternalDisabled")&&m.Body.Instructions.ToList().FindIndex(i=>i.Operand is FieldReference f&&f.Name=="ExternalDisabled")<Index(m,"ServerSetupFiles","RelativeContent")),"saved-list cached wrapper lookup excludes outside-root NOMM rows before applying its active-plugin path guard");
        var listSelection=manager.Methods.Single(m=>m.Name=="PrepareSavedList");
        check(Index(listSelection,"ModManager","RefreshRequisiteQueue")>=0&&Index(listSelection,"ModManager","RefreshRequisiteQueue")<Index(listSelection,"ModManager","SelectExternalDependencies")&&Index(listSelection,"ModManager","SelectExternalDependencies")<Index(listSelection,"ModManager","ValidateRequisiteSelection"),"saved-list application refreshes queued metadata and resolves available NOMM prerequisites before validating the projected selection");
        var selectDependencies=manager.Methods.Single(m=>m.Name=="SelectExternalDependencies");
        check(Index(selectDependencies,"ModManager","RequisiteInventory")>=0&&Index(selectDependencies,"ModManager","RequisiteInventory")<Index(selectDependencies,"NommDisabledSelection","RequiredEntries"),"NOMM dependency selection traverses verified queued metadata and selected active owners through the projected inventory");
        var applySelection=manager.Methods.Single(m=>m.Name=="ApplySelectionCore");
        check(Index(applySelection,"ModManager","RefreshRequisiteQueue")<Index(applySelection,"ModManager","SelectExternalDependencies")&&Index(applySelection,"ModManager","SelectExternalDependencies")<Index(applySelection,"ModManager","ValidateRequisiteSelection"),"normal reload resolves projected NOMM dependency rows before validating or committing the requested selection");
        var restartMods=manager.Methods.Single(m=>m.Name=="RestartMods");
        check(Index(restartMods,"ModManager","RequisiteInventory")>=0&&Index(restartMods,"ModManager","RequisiteInventory")<Index(restartMods,"ModManager","PrepareExternalRestores"),"normal restart prepares whole NOMM unit receipts from the same projected prerequisite inventory");
        var externalCopy=manager.Methods.Single(m=>m.Name=="ExternalCopy");
        var copyCode=externalCopy.Body.Instructions.ToList();
        check(Calls(manager.Methods.Single(m=>m.Name=="Refresh"),"ModManager","ExternalCopy")&&copyCode.Any(i=>i.OpCode.Code==Code.Stfld&&i.Operand is FieldReference f&&f.Name=="Wanted"&&i.Previous?.OpCode.Code==Code.Ldc_I4_0),"menu refresh creates unselected external row copies so editing ticks cannot mutate or leak through the inspected NOMM catalog cache");
        var siblingPredicates=managerMethods.Where(m=>m.Name.Contains("<Plan>")&&ReadsField(m,"WrapperPlugin")&&ReadsField(m,"Content")&&ReadsField(m,"DisabledContent")).ToArray();
        check(siblingPredicates.Any(m=>Calls(m,"Enumerable","Contains")),"server plugin graph merges prerequisites only from content included in the advertised server selection");
        var constructor=manager.Methods.Single(m=>m.IsConstructor&&!m.IsStatic);
        check(GuardedCall(constructor,"ServerSetupFiles","Pending","externalRestoreFailure"),"failed physical restoration suppresses the client manager's saved-server resume instead of presenting that set as loaded");
        using(var startup=AssemblyDefinition.ReadAssembly(startupPath))
        {
            var finish=startup.MainModule.GetType("DoormanStartupPatcher").Methods.Single(m=>m.Name=="Finish");
            check(Index(finish,"DisabledRestoreQueue","Apply")>=0&&Index(finish,"DisabledRestoreQueue","Apply")<Index(finish,"StartupUpdatePreflight","ApplyQueues"),"preloader applies whole NOMM restore receipts before queued DLL updates and plugin discovery");
            int apply=Index(finish,"DisabledRestoreQueue","Apply"),pending=Index(finish,"ServerSetupFiles","Pending"),completed=Index(finish,"DisabledModFiles","VerifyApplied"),updates=Index(finish,"StartupUpdatePreflight","ApplyQueues");
            check(apply>=0&&pending>apply&&completed>pending&&updates>completed,"preloader verifies saved server receipts are physically completed after applying any queue and before queued DLL updates");
            var startupCode=finish.Body.Instructions;
            check(completed>=0&&finish.Body.ExceptionHandlers.Where(h=>h.HandlerType==ExceptionHandlerType.Catch).Any(h=>new[]{apply,pending,completed}.All(index=>index>=0&&startupCode[index].Offset>=h.TryStart.Offset&&startupCode[index].Offset<(h.TryEnd?.Offset??int.MaxValue))),"queue application and completed server-receipt verification share the restore-failure catch that suppresses updates, session filtering and resume");
            check(Index(finish,"DoormanStartupPatcher","Headless")>=0&&Index(finish,"DoormanStartupPatcher","Headless")<Index(finish,"DisabledRestoreQueue","Apply"),"client physical restoration remains behind the existing headless/server exclusion");
            check(GuardedCall(finish,"StartupUpdatePreflight","ApplyQueues","ExternalRestoreError"),"failed physical restoration prevents queued updates from running against an incomplete projected mod tree");
            var filter=startup.MainModule.GetType("DoormanRuntimeHooks").Methods.Single(m=>m.Name=="Filter");
            check(GuardedCall(filter,"ServerSetupFiles","Pending","ExternalRestoreError"),"startup plugin filtering ignores the saved server session after a physical-restore failure and keeps normal preferences");
        }
    }

    private static ModEntry Copy(ModEntry e)
    {
        var copy=new ModEntry {Id=e.Id,Name=e.Name,Version=e.Version,Path=e.Path,AssemblyName=e.AssemblyName,WrapperPlugin=e.WrapperPlugin,WrapperPath=e.WrapperPath,Content=e.Content,Enabled=false,Wanted=false,ExternalDisabled=e.ExternalDisabled,RestoreSource=e.RestoreSource,RestoreTarget=e.RestoreTarget,Manifest=e.Manifest};copy.Requires.AddRange(e.Requires);copy.RequiredRequisites.AddRange(e.RequiredRequisites);return copy;
    }
    private static DisabledBundleInfo Bundle(Stream input)
    {
        using(var data=new MemoryStream()){input.CopyTo(data);return DisabledContentMetadata.Parse("exact_pack",data.ToArray());}
    }
    private static string Dll(string folder,string name,string assemblyName,string id,string version,(string Id,string Version)[] dependencies,bool pack)
    {
        string path=Path.GetFullPath(Path.Combine(folder,name));
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(assemblyName,new Version(1,0)),assemblyName,ModuleKind.Dll))
        {
            var module=assembly.MainModule;var scope=new AssemblyNameReference("BepInEx",new Version(5,4,23,0));var plugin=new TypeDefinition("Fixture","Plugin",TypeAttributes.Public|TypeAttributes.Class,module.TypeSystem.Object);module.Types.Add(plugin);
            var constructor=new MethodReference(".ctor",module.TypeSystem.Void,new TypeReference("BepInEx","BepInPlugin",module,scope)){HasThis=true};foreach(var unused in Enumerable.Range(0,3))constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            var identity=new CustomAttribute(constructor);foreach(string value in new[]{id,id,version})identity.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,value));plugin.CustomAttributes.Add(identity);
            foreach(var dependency in dependencies)
            {
                var ctor=new MethodReference(".ctor",module.TypeSystem.Void,new TypeReference("BepInEx","BepInDependency",module,scope)){HasThis=true};ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));ctor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                var attribute=new CustomAttribute(ctor);attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,dependency.Id));attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,dependency.Version));plugin.CustomAttributes.Add(attribute);
            }
            if(pack)module.Resources.Add(new EmbeddedResource("fixture.internal.nobp",ManifestResourceAttributes.Public,Encoding.UTF8.GetBytes("{\"modName\":\"Exact pack\",\"modVersion\":\"2.0\"}")));
            assembly.Write(path);
        }
        return path;
    }
    private static void Reject(Action<bool,string> check,Action action,string message)
    {bool rejected=false;try{action();}catch(Exception ex)when(ex is InvalidDataException||ex is IOException||ex is ArgumentException){rejected=true;}check(rejected,message);}
    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {foreach(var type in roots){yield return type;foreach(var nested in AllTypes(type.NestedTypes))yield return nested;}}
    private static MethodDefinition Iterator(TypeDefinition manager,string name)=>manager.NestedTypes.Single(t=>t.Name.StartsWith("<"+name+">",StringComparison.Ordinal)).Methods.Single(m=>m.Name=="MoveNext");
    private static bool Calls(MethodDefinition body,string type,string method)=>Index(body,type,method)>=0;
    private static int Index(MethodDefinition body,string type,string method)=>body.Body.Instructions.ToList().FindIndex(i=>i.Operand is MethodReference r&&r.DeclaringType.Name==type&&r.Name==method);
    private static bool ReadsField(MethodDefinition body,string field)=>body.Body.Instructions.Any(i=>i.OpCode.Code==Code.Ldfld&&i.Operand is FieldReference f&&f.Name==field);
    private static bool GuardedCall(MethodDefinition body,string type,string method,string field)
    {
        var instructions=body.Body.Instructions.ToList();var calls=instructions.Where(i=>i.Operand is MethodReference r&&r.DeclaringType.Name==type&&r.Name==method).ToArray();
        if(calls.Length==0)return false;
        foreach(var call in calls)
        {
            int at=instructions.IndexOf(call),guard=instructions.FindLastIndex(at,i=>i.Operand is FieldReference f&&f.Name==field&&(i.OpCode.Code==Code.Ldfld||i.OpCode.Code==Code.Ldsfld));
            if(guard<0)return false;
            var guarded=instructions.Skip(guard+1).Take(at-guard-1).ToArray();
            // The compiler can branch past a guarded call, or branch into it
            // while the opposite fallthrough returns/nulls and jumps past it.
            bool skip=guarded.Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch&&i.Operand is Instruction target&&
                (target.Offset>call.Offset||target.Offset>i.Offset&&guarded.Any(j=>j.Offset>i.Offset&&j.Offset<target.Offset&&j.OpCode.FlowControl==FlowControl.Branch&&j.Operand is Instruction end&&end.Offset>call.Offset)));
            if(!skip)return false;
        }
        return true;
    }
    private static bool CleanupIsConditional(MethodDefinition body,string type,string method)
    {
        var instructions=body.Body.Instructions.ToList();var calls=instructions.Where(i=>i.Operand is MethodReference r&&r.DeclaringType.Name==type&&r.Name==method).ToArray();
        return calls.Length>0&&calls.All(call=>body.Body.ExceptionHandlers.Where(h=>h.HandlerType==ExceptionHandlerType.Catch&&call.Offset>=h.HandlerStart.Offset&&call.Offset<(h.HandlerEnd?.Offset??int.MaxValue)).Any(h=>instructions.Where(i=>i.Offset>=h.HandlerStart.Offset&&i.Offset<call.Offset).Any(i=>i.OpCode.FlowControl==FlowControl.Cond_Branch&&i.Operand is Instruction target&&target.Offset>call.Offset)));
    }
}
