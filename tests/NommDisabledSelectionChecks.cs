using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KellysJOINCHECK;

internal static class NommDisabledSelectionRegression
{
    internal static void Run(Action<bool,string> check)
    {
        var wrapper=Plugin("example.plugin");var pack=Pack("server_content",wrapper.Id);var extra=Pack("other_pack",wrapper.Id);var dependency=Plugin("example.dependency");var unrelated=Plugin("unrelated.plugin");
        wrapper.RequiredRequisites.Add(new ModRequisite {Plugin=dependency.Id,MinimumVersion="1.2.0"});dependency.Version="1.2.1";
        NommDisabledUnit own=Unit("RegistryPackage",new[]{pack,wrapper,extra},registry:"different_registry_id"),required=Unit("Dependency",new[]{dependency}),other=Unit("Other",new[]{unrelated});
        var all=new[]{pack,wrapper,extra,dependency,unrelated};var units=new[]{own,required,other};
        check(NommDisabledSelection.Required(all,new[]{pack},units).SequenceEqual(new[]{own,required}),"server content ID resolves its exact external package and hard dependency without guessing the NOMM registry ID");
        var rows=NommDisabledSelection.RequiredEntries(all,new[]{pack},units);
        check(rows.SequenceEqual(new[]{pack,wrapper,dependency})&&!rows.Contains(extra)&&!rows.Contains(unrelated),"restoring a whole pack selects only required content, wrapper and dependency rows");
        check(all.All(e=>!e.Enabled&&!e.Wanted),"computing disabled restore selection never claims loaded state or changes checkbox choices");
        check(NommDisabledSelection.Required(all,new[]{unrelated},units).Single()==other,"code-only plugins restore their exact whole package independently");
        other.Error="Unrelated damaged package";
        check(NommDisabledSelection.Required(all,new[]{pack},units).Length==2,"unrelated disabled-unit errors do not block required package restoration");
        Reject(check,()=>NommDisabledSelection.Required(all,new[]{unrelated},units),"a selected disabled-unit discovery error blocks its restoration");other.Error="";
        dependency.Version="1.1.0";Reject(check,()=>NommDisabledSelection.Required(all,new[]{pack},units),"hard dependency minimum versions block restoring an incompatible selection");dependency.Version="1.2.1";
        Reject(check,()=>NommDisabledSelection.Required(all.Concat(new[]{Plugin(wrapper.Id)}),new[]{pack},units),"a required duplicate plugin identity cannot be selected by package or filename");
        var duplicateOne=Plugin("com.bepis.bepinex.configurationmanager");duplicateOne.Name="Configuration Manager";duplicateOne.Path=@"C:\Games\NO\BepInEx\plugins\first\ConfigurationManager.dll";
        var duplicateTwo=Plugin(duplicateOne.Id);duplicateTwo.Path=@"C:\Games\NO\BepInEx\plugins\second\ConfigurationManager.dll";
        check(NommDisabledSelection.RequiredRestores(new[]{duplicateOne,duplicateTwo},new[]{duplicateOne,duplicateTwo},Array.Empty<NommDisabledUnit>()).Length==0,"a normal restart with duplicate active plugin IDs and no NOMM-disabled mods does not attempt a restore");
        var forgedExternal=Plugin("external.missing");forgedExternal.ExternalDisabled=true;
        Reject(check,()=>NommDisabledSelection.RequiredRestores(new[]{forgedExternal},new[]{forgedExternal},Array.Empty<NommDisabledUnit>()),"an externally disabled selection without its physical restore unit still fails closed");
        Reject(check,()=>NommDisabledSelection.RequiredRestores(new[]{duplicateOne,duplicateTwo},new[]{duplicateOne},new[]{other}),"duplicate identities remain blocked when a physical NOMM restore may be required");
        try {NommDisabledSelection.RequiredEntries(new[]{duplicateOne,duplicateTwo},new[]{duplicateOne},Array.Empty<NommDisabledUnit>());check(false,"duplicate setup diagnostic");}
        catch(InvalidDataException error)
        {
            check(error.Message.Contains("Multiple installed entries match Configuration Manager")&&error.Data["Mod ID"] as string==duplicateOne.Id,"duplicate setup identity gives a specific actionable message");
            var report=new SupportReport(@"C:\Games\NO",@"C:\Users\Example");report.Record("Setup",error.Data["Source"] as string??"",true);string text=report.Compose("",Array.Empty<string>());
            check(text.Contains("<Game>\\BepInEx\\plugins\\first\\ConfigurationManager.dll")&&text.Contains("<Game>\\BepInEx\\plugins\\second\\ConfigurationManager.dll")&&!text.Contains(@"C:\Games\NO"),"duplicate report includes both locations with the game path masked");
        }
        Reject(check,()=>NommDisabledSelection.Required(all.Concat(new[]{Pack(pack.Id)}),new[]{pack},units),"duplicate selected content identities block automatic restoration");
        Reject(check,()=>NommDisabledSelection.Required(all,new[]{Pack(pack.Id)},units),"stale selected row copies cannot bind to a fresh restore inventory");
        var sameNamePlugin=Plugin(pack.Id);
        check(NommDisabledSelection.RequiredEntries(all.Concat(new[]{sameNamePlugin}),new[]{pack},units).Length==3,"plugin and content with the same text ID remain separate identities");

        var manifestParent=Plugin("manifest.parent");manifestParent.Manifest=new ModManifest {Name="Manifest parent",Plugin=manifestParent.Id,Requisites=new[]{new ModRequisite {Bundle=pack.Id,MinimumVersion="2.0.0"}}};pack.Version="2.0.0";
        check(NommDisabledSelection.Required(all.Concat(new[]{manifestParent}),new[]{manifestParent},units).Length==2,"normal selected plugins can require externally disabled content through manifest bundle declarations");
        pack.Version="1.9.0";Reject(check,()=>NommDisabledSelection.Required(all.Concat(new[]{manifestParent}),new[]{manifestParent},units),"manifest bundle minimum versions use the bundle's exact metadata");pack.Version="2.0.0";
        manifestParent.Manifest.Requisites=new[]{new ModRequisite {Plugin=dependency.Id,MinimumVersion="2.0.0"}};
        Reject(check,()=>NommDisabledSelection.Required(all.Concat(new[]{manifestParent}),new[]{pack,manifestParent},units),"stronger constraints on a previously reached dependency are still checked");
        manifestParent.Manifest.RequisiteError="Invalid prerequisite manifest";
        Reject(check,()=>NommDisabledSelection.Required(new[]{manifestParent},new[]{manifestParent},Array.Empty<NommDisabledUnit>()),"selected invalid manifest declarations cannot become a restore plan");

        ModEntry parentCode=Plugin("actual.parent.plugin"),parentExtra=Pack("parent_extra"),parentDep=Plugin("parent.dependency"),addon=Plugin("actual.addon.plugin"),unneededAddon=Plugin("second.addon.plugin");parentCode.Requires.Add(parentDep.Id);
        NommDisabledUnit parentUnit=Unit("ParentFiles",new[]{parentCode,parentExtra},registry:"nomm-parent"),parentDepUnit=Unit("ParentDependency",new[]{parentDep});
        NommDisabledUnit addonUnit=Unit("AddonFiles",new[]{addon},registry:"nomm-addon",parent:"nomm-parent",target:"ParentFiles/addons/nomm-addon"),unneededUnit=Unit("SecondAddon",new[]{unneededAddon},registry:"unused-addon",parent:"nomm-parent",target:"ParentFiles/addons/unused-addon");
        var parentAll=new[]{parentCode,parentExtra,parentDep,addon,unneededAddon};var parentUnits=new[]{addonUnit,parentUnit,parentDepUnit,unneededUnit};
        var parentSelection=NommDisabledSelection.Required(parentAll,new[]{addon},parentUnits);
        check(parentSelection.Contains(addonUnit)&&parentSelection.Contains(parentUnit)&&parentSelection.Contains(parentDepUnit)&&!parentSelection.Contains(unneededUnit),"disabled addon restoration includes its exact NOMM parent and parent plugin prerequisites only");
        check(NommDisabledSelection.RequiredEntries(parentAll,new[]{addon},parentUnits).SequenceEqual(new[]{addon,parentCode,parentDep})&&!parentExtra.Wanted,"restoring an addon does not select parent content or sibling addon rows");
        var clones=parentAll.Select(Clone).ToArray();var clonedAddon=clones.Single(e=>e.Id==addon.Id);clonedAddon.Wanted=true;
        var clonedRows=NommDisabledSelection.RequiredEntries(clones,new[]{clonedAddon},parentUnits);
        check(clonedRows.SequenceEqual(new[]{clonedAddon,clones.Single(e=>e.Id==parentCode.Id),clones.Single(e=>e.Id==parentDep.Id)})&&clonedRows.All(clones.Contains),"copied server selection follows addon, parent and separate hard dependency through supplied inventory clones");
        check(NommDisabledSelection.Required(clones,new[]{clonedAddon},parentUnits).SequenceEqual(parentSelection),"verified copied metadata resolves the original inspected restore units");
        check(clonedAddon.Wanted&&clones.Where(e=>e!=clonedAddon).All(e=>!e.Wanted)&&clones.All(e=>!e.Enabled)&&parentAll.All(e=>!e.Enabled&&!e.Wanted),"clone resolution preserves cloned checkbox choices and every original loaded/wanted state");
        foreach(Action<ModEntry> tamper in new Action<ModEntry>[] {
            e=>e.Id="forged.addon",e=>e.Version="99.0.0",e=>e.Content=true,e=>e.Path="changed.dll",e=>e.WrapperPlugin="changed.wrapper",
            e=>e.WrapperPath="changed-wrapper.dll",e=>e.AssemblyName="ChangedAssembly",e=>e.RestoreSource="ChangedSource",e=>e.RestoreTarget="ChangedTarget"
        })
        {
            var changed=parentAll.Select(Clone).ToArray();var changedAddon=changed.Single(e=>e.Id==addon.Id);tamper(changedAddon);
            Reject(check,()=>NommDisabledSelection.Required(changed,new[]{changedAddon},parentUnits),"tampered copied addon identities, versions and source metadata cannot bind to inspected restore units");
        }
        var changedParent=parentAll.Select(Clone).ToArray();changedParent.Single(e=>e.Id==parentCode.Id).Path="changed-parent.dll";
        Reject(check,()=>NommDisabledSelection.RequiredEntries(changedParent,new[]{changedParent.Single(e=>e.Id==addon.Id)},parentUnits),"copied parent code metadata is verified before prerequisite traversal");
        var changedDependency=parentAll.Select(Clone).ToArray();changedDependency.Single(e=>e.Id==parentDep.Id).Version="99.0.0";
        Reject(check,()=>NommDisabledSelection.RequiredEntries(changedDependency,new[]{changedDependency.Single(e=>e.Id==addon.Id)},parentUnits),"copied separate hard dependency versions must match the inspected source even when a minimum would pass");
        var duplicateParent=Unit("DuplicateParent",new[]{Plugin("duplicate.parent")},registry:"nomm-parent");
        Reject(check,()=>NommDisabledSelection.Required(parentAll,new[]{addon},parentUnits.Concat(new[]{duplicateParent})),"duplicate NOMM parent registry IDs block ambiguous addon restoration");
        check(NommDisabledSelection.Required(new[]{addon},new[]{addon},new[]{addonUnit}).Single()==addonUnit,"an already active parent is not guessed from registry ID when no disabled parent exists");
        parentUnit.Error="Parent metadata unavailable";
        Reject(check,()=>NommDisabledSelection.Required(parentAll,new[]{addon},parentUnits),"a required disabled parent discovery error blocks the addon plan");parentUnit.Error="";
        parentUnit.ParentId="nomm-addon";
        check(NommDisabledSelection.Required(parentAll,new[]{addon},parentUnits).Length==3,"cycles in disabled parent routing remain bounded and include each unit once");parentUnit.ParentId="";

        var loop=Enumerable.Range(0,512).Select(i=>Plugin("cycle."+i)).ToArray();
        for(int i=0;i<loop.Length;i++)loop[i].Requires.Add(loop[(i+1)%loop.Length].Id);
        check(NommDisabledSelection.RequiredEntries(loop,new[]{loop[0]},Array.Empty<NommDisabledUnit>()).Length==512,"a prerequisite cycle of 512 identities is traversed once without recursion or state mutation");
        var overflow=Plugin("overflow");loop[511].Requires.Add(overflow.Id);
        Reject(check,()=>NommDisabledSelection.RequiredEntries(loop.Concat(new[]{overflow}),new[]{loop[0]},Array.Empty<NommDisabledUnit>()),"selection refuses prerequisite graphs larger than the bounded 512 identities");
        var missing=Plugin("needs.missing");missing.Requires.Add("absent.plugin");
        Reject(check,()=>NommDisabledSelection.RequiredEntries(new[]{missing},new[]{missing},Array.Empty<NommDisabledUnit>()),"missing hard dependencies do not produce partial restore plans");
        dependency.RestoreTarget="DifferentDestination";
        Reject(check,()=>NommDisabledSelection.Required(all,new[]{pack},units),"external row source and target must match the exact discovered unit");dependency.RestoreTarget=required.Candidate.TargetRelative;
        Reject(check,()=>NommDisabledSelection.Required(all,new[]{pack},units.Concat(new[]{own})),"duplicate physical unit receipts cannot silently choose one restore source");
        dependency.Version="";Reject(check,()=>NommDisabledSelection.Required(all,new[]{pack},units),"selected dependency versions must be explicitly present in the inventory");dependency.Version="1.2.1";
        check(all.Concat(parentAll).All(e=>!e.Enabled&&!e.Wanted),"successful and rejected selections preserve every loaded and wanted state");
        ActiveAndQueuedOwners(check);
        ServerWithUnrelatedDuplicates(check);
    }

    private static void ServerWithUnrelatedDuplicates(Action<bool,string> check)
    {
        const string bpId="com.nikkorap.blueprinter";
        var bp=Plugin(bpId);var cockpit=Plugin("paul.cockpitviewfix");var second=Plugin(cockpit.Id);
        cockpit.Name=second.Name="Cockpit View Fix";cockpit.Version=second.Version="1.2.0";
        cockpit.Path=@"C:\Games\NO\BepInEx\plugins\CockpitViewFix.dll";second.Path=@"C:\Games\NO\BepInEx\plugins\M2K\CockpitViewFix.dll";
        var agni=Pack("1509_palafighter1");var keep=Pack("su-30","kyleisred.nuclearoption.solarsu30");var wrapper=Plugin(keep.WrapperPlugin);
        var all=new[]{bp,cockpit,second,agni,keep,wrapper};foreach(var item in all){item.Enabled=true;item.Wanted=true;}
        string server="0.34.6_com.nikkorap.blueprinter-v1.0.0_--su-30-v1.0.0";
        var plan=ServerSetupPlan.Create(server,server+"_--1509_palafighter1-v1.0.0",new[]{
            new InstalledContent {Id=keep.Id,Version=keep.Version,Enabled=true},new InstalledContent {Id=agni.Id,Version=agni.Version,Enabled=true}},bp.Version,true,false);
        check(plan.CanSetup&&plan.NeedsRestart&&plan.DisabledContent.SequenceEqual(new[]{agni.Id}),"reported Shipyard selection needs only the extra Agni content disabled");
        plan.DisabledPlugins=ServerPluginSelection.Resolve(all.Where(e=>!e.Content).Select(e=>new KeyValuePair<string,string[]>(e.Id,e.Requires.ToArray())),Array.Empty<string>(),new[]{wrapper.Id},new[]{wrapper.Id},false);
        foreach(var item in all)item.Wanted=item.Content?!plan.DisabledContent.Contains(item.Id):!plan.DisabledPlugins.Contains(item.Id);
        var notes=new List<string>();var required=NommDisabledSelection.RequiredServerEntries(all,all.Where(e=>e.Wanted),Array.Empty<NommDisabledUnit>(),new[]{wrapper.Id},notes.Add);
        check(required.Contains(keep)&&required.Contains(wrapper)&&!required.Contains(agni)&&!required.Contains(cockpit)&&!required.Contains(second),"server prerequisite closure omits only the unrelated duplicate roots while preserving required gameplay");
        check(cockpit.Wanted&&second.Wanted&&cockpit.Enabled&&second.Enabled&&!plan.DisabledPlugins.Contains(cockpit.Id),"both Cockpit View Fix files remain untouched and enabled in the planned server selection");
        check(ModRequisites.ValidateSelection(all)==""&&NommDisabledSelection.RequiredRestores(all,required,Array.Empty<NommDisabledUnit>()).Length==0,"Shipyard selection reaches restart validation without attempting to restore or replace optional duplicates");
        check(notes.Count==1&&notes[0].Contains(cockpit.Path)&&notes[0].Contains(second.Path),"copy-report note retains both duplicate file locations once without blocking setup");
        var external=Plugin("external.required");var unit=Unit("Required",new[]{external});keep.Requires.Add(external.Id);
        var withExternal=all.Concat(new[]{external}).ToArray();
        var closure=NommDisabledSelection.RequiredServerEntries(withExternal,all.Where(e=>e.Wanted),new[]{unit},new[]{wrapper.Id});
        check(closure.Contains(external)&&NommDisabledSelection.RequiredRestores(withExternal,closure,new[]{unit}).Single()==unit,"an actual NOMM restore still succeeds beside unrelated duplicate client plugins");
        keep.Requires.Clear();
        Reject(check,()=>NommDisabledSelection.RequiredServerEntries(all,all.Where(e=>e.Wanted),Array.Empty<NommDisabledUnit>(),new[]{cockpit.Id}),"server-required gameplay DLL duplicates still block setup");
        keep.Requires.Add(cockpit.Id);
        Reject(check,()=>NommDisabledSelection.RequiredServerEntries(all,all.Where(e=>e.Wanted),Array.Empty<NommDisabledUnit>(),Array.Empty<string>()),"a dependency edge to an otherwise optional duplicate remains ambiguous");keep.Requires.Clear();
        second.Requires.Add(bp.Id);
        Reject(check,()=>NommDisabledSelection.RequiredServerEntries(all,all.Where(e=>e.Wanted),Array.Empty<NommDisabledUnit>(),Array.Empty<string>()),"duplicate plugin copies with their own prerequisites are not silently skipped");second.Requires.Clear();
        second.ExternalDisabled=true;
        Reject(check,()=>NommDisabledSelection.RequiredServerEntries(all,new[]{cockpit},Array.Empty<NommDisabledUnit>(),Array.Empty<string>()),"an active and NOMM-disabled identity conflict cannot be treated as unrelated local duplicates");second.ExternalDisabled=false;
        Reject(check,()=>NommDisabledSelection.RequiredServerEntries(all.Concat(new[]{Pack(keep.Id)}),new[]{keep},Array.Empty<NommDisabledUnit>(),Array.Empty<string>()),"duplicate content stays blocked in the server-specific resolver");
        Reject(check,()=>NommDisabledSelection.RequiredServerEntries(all,new[]{Clone(cockpit)},Array.Empty<NommDisabledUnit>(),Array.Empty<string>()),"an unrelated duplicate still must belong to the exact inventory snapshot");
    }

    private static void ActiveAndQueuedOwners(Action<bool,string> check)
    {
        var active=Plugin("active.owner");active.Enabled=true;active.Wanted=true;
        var needed=Plugin("externally.disabled.dependency");var unrelated=Plugin("unrelated.external.package");
        active.Manifest=new ModManifest {Plugin=active.Id,Name=active.Name,Requisites=new[]{new ModRequisite {Plugin=needed.Id,MinimumVersion="1.0.0"}}};
        var neededUnit=Unit("DisabledDependency",new[]{needed});var unrelatedUnit=Unit("UnrelatedDisabled",new[]{unrelated});
        var original=new[]{active,needed,unrelated};var copied=original.Select(Clone).ToArray();var unitSet=new[]{neededUnit,unrelatedUnit};
        var rows=NommDisabledSelection.RequiredEntries(copied,copied.Where(e=>e.Wanted),unitSet);
        check(rows.SequenceEqual(new[]{copied[0],copied[1]})&&NommDisabledSelection.Required(copied,copied.Where(e=>e.Wanted),unitSet).Single()==neededUnit,"an active selected owner reaches its exact externally disabled dependency even when no external checkbox was initially selected");
        foreach(var row in rows)row.Wanted=true;
        check(copied[1].Wanted&&!copied[1].Enabled&&!copied[2].Wanted&&active.Enabled&&active.Wanted&&!needed.Enabled&&!needed.Wanted&&!unrelated.Wanted,"marking returned inventory copies selects only the active owner's needed dependency and preserves immutable inspected rows and loaded state");

        var externalOwner=Plugin("external.owner");var normal=Plugin("normal.queued.owner");normal.Enabled=true;normal.Wanted=true;normal.Path=@"C:\Games\Nuclear Option\BepInEx\plugins\NormalOwner.dll";
        var previousChild=Plugin("old.disabled.child");var newChild=Plugin("new.disabled.child");newChild.Version="2.0.0";
        var neverNeeded=Plugin("unrelated.queued.selection");
        externalOwner.RequiredRequisites.Add(new ModRequisite {Plugin=normal.Id,MinimumVersion="2.0.0"});
        normal.RequiredRequisites.Add(new ModRequisite {Plugin=previousChild.Id,MinimumVersion="1.0.0"});
        var externalUnit=Unit("ExternalOwner",new[]{externalOwner});var previousUnit=Unit("PreviousChild",new[]{previousChild});
        var newUnit=Unit("NewChild",new[]{newChild});var neverUnit=Unit("NeverNeeded",new[]{neverNeeded});
        var initial=new[]{externalOwner,normal,previousChild,newChild,neverNeeded};var inventory=initial.Select(Clone).ToArray();inventory[0].Wanted=true;
        var allUnits=new[]{externalUnit,previousUnit,newUnit,neverUnit};
        Reject(check,()=>NommDisabledSelection.RequiredEntries(inventory,inventory.Where(e=>e.Wanted),allUnits),"an old loaded DLL cannot satisfy the external owner's minimum merely because an update is planned");

        var pending=new[]{new PendingModUpdate {Plugin=normal.Id,Version="v2.0.0",PluginVersion="2.0.0",Target="NormalOwner.dll"}};
        var verified=new Dictionary<string,QueuedModRequisiteMetadata>(StringComparer.Ordinal) {
            [normal.Id]=new QueuedModRequisiteMetadata {Plugin=normal.Id,OwnerPlugin=normal.Id,Name=normal.Name,Version="2.0.0",Target=normal.Path,
                Hard=new[]{new ModRequisite {Plugin=newChild.Id,MinimumVersion="2.0.0"}}}
        };
        var projected=ModRequisiteSelection.WithPending(inventory,pending,verified,@"C:\Games\Nuclear Option\BepInEx\plugins");
        var projectedOwner=projected.Single(e=>e.Id==normal.Id);
        var closure=NommDisabledSelection.RequiredEntries(projected,projected.Where(e=>e.Wanted),allUnits);
        check(projectedOwner.Version=="2.0.0"&&!projectedOwner.Enabled&&projectedOwner.RequiredRequisites.Single().Plugin==newChild.Id&&closure.Contains(projected.Single(e=>e.Id==newChild.Id))&&!closure.Any(e=>e.Id==previousChild.Id||e.Id==neverNeeded.Id),"external selection follows a queued owner's verified new hard dependency graph instead of its loaded DLL's old dependencies");
        var restoreUnits=NommDisabledSelection.Required(projected,projected.Where(e=>e.Wanted),allUnits);
        check(restoreUnits.SequenceEqual(new[]{externalUnit,newUnit})&&!restoreUnits.Contains(previousUnit)&&!restoreUnits.Contains(neverUnit),"verified queued metadata restores only the requested external owner and its exact new dependency package");
        check(normal.Version=="1.0.0"&&normal.Enabled&&normal.RequiredRequisites.Single().Plugin==previousChild.Id&&!externalOwner.Wanted&&!newChild.Wanted&&initial.Where(e=>e!=normal).All(e=>!e.Enabled)&&!inventory.Single(e=>e.Id==newChild.Id).Wanted,"queued dependency discovery never changes original live metadata, inspected unit selections or supplied checkbox choices");
        var unverified=ModRequisiteSelection.WithPending(inventory,pending,new Dictionary<string,QueuedModRequisiteMetadata>(),@"C:\Games\Nuclear Option\BepInEx\plugins");
        Reject(check,()=>NommDisabledSelection.RequiredEntries(unverified,unverified.Where(e=>e.Wanted),allUnits),"an update without verified prerequisite metadata cannot silently restore the old graph or produce a partial external plan");
        verified[normal.Id].Hard[0].MinimumVersion="3.0.0";
        var stronger=ModRequisiteSelection.WithPending(inventory,pending,verified,@"C:\Games\Nuclear Option\BepInEx\plugins");
        Reject(check,()=>NommDisabledSelection.RequiredEntries(stronger,stronger.Where(e=>e.Wanted),allUnits),"queued hard minimum versions remain enforced against the exact disabled dependency's installed metadata");
    }

    private static ModEntry Plugin(string id)=>new ModEntry {Id=id,Name=id,Version="1.0.0"};
    private static ModEntry Pack(string id,string wrapper="")=>new ModEntry {Id=id,Name=id,Version="1.0.0",Content=true,WrapperPlugin=wrapper};
    private static ModEntry Clone(ModEntry entry)
    {
        var copy=new ModEntry {Id=entry.Id,Name=entry.Name,Version=entry.Version,Content=entry.Content,Path=entry.Path,WrapperPlugin=entry.WrapperPlugin,
            WrapperPath=entry.WrapperPath,AssemblyName=entry.AssemblyName,ExternalDisabled=entry.ExternalDisabled,RestoreSource=entry.RestoreSource,RestoreTarget=entry.RestoreTarget,
            Manifest=entry.Manifest,Wanted=entry.Wanted,Enabled=entry.Enabled};
        copy.Requires.AddRange(entry.Requires);copy.RequiredRequisites.AddRange(entry.RequiredRequisites);return copy;
    }
    private static NommDisabledUnit Unit(string source,ModEntry[] entries,string registry="",string parent="",string target="")
    {
        var unit=new NommDisabledUnit {RegistryId=registry.Length>0?registry:source,ParentId=parent,Candidate=new DisabledModCandidate {SourceRelative=source,TargetRelative=target.Length>0?target:source}};
        foreach(var entry in entries){entry.ExternalDisabled=true;entry.RestoreSource=source;entry.RestoreTarget=unit.Candidate.TargetRelative;unit.Entries.Add(entry);}
        return unit;
    }
    private static void Reject(Action<bool,string> check,Action action,string description)
    {
        bool rejected=false;try { action(); }catch(InvalidDataException){rejected=true;}check(rejected,description);
    }
}
