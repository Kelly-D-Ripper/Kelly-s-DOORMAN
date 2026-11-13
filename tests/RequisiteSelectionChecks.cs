using System;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class RequisiteSelectionRegression
{
    internal static void Run(Action<bool,string> check)
    {
        var requirement=new ModRequisite { Plugin="dependency.test",Name="Dependency",MinimumVersion="2.0" };
        var parent=new ModEntry { Id="parent",Name="Parent",Enabled=true,Wanted=true,Manifest=new ModManifest { Plugin="parent",Name="Parent",Requisites=new[]{requirement} } };
        var install=new PendingModUpdate { Schema=3,Plugin="dependency.test",Version="2.1",PluginVersion="2.1",Target="fresh.dll" };
        var future=ModRequisiteSelection.WithPending(new[]{parent},new[]{install});
        check(future.Length==2&&future.Single(e=>e.Id==install.Plugin).Wanted&&!future.Single(e=>e.Id==install.Plugin).Enabled,"queued install projects next selection without pretending plugin is loaded");
        check(ModRequisites.ValidateSelection(future)=="","valid downloaded prerequisite lets Reload reach its restart prompt");
        check(ModRequisites.ForMod(parent,new[]{parent}).Single().Status==ModRequisiteStatus.Missing,"queued prerequisite projection leaves actual inventory missing until restart");
        check(ModRequisiteSelection.Satisfies(requirement,install),"queued actual DLL version satisfies its declared minimum");
        requirement.MinimumVersion="3.0";
        check(!ModRequisiteSelection.Satisfies(requirement,install)&&ModRequisites.ValidateSelection(future).Length>0,"a lower queued version cannot clear a stronger requisite");
        requirement.MinimumVersion="2.0";
        var old=new ModEntry { Id=install.Plugin,Name="Dependency",Version="1.0",Enabled=true,Wanted=true };
        old.RequiredRequisites.Add(new ModRequisite { Plugin="child" });
        var child=new ModEntry { Id="child",Name="Child",Version="1.0",Enabled=true,Wanted=true };
        var update=new PendingModUpdate { Plugin=old.Id,Version="v99.0",PluginVersion="2.1" };
        var projected=ModRequisiteSelection.WithPending(new[]{parent,old,child},new[]{update});
        check(projected.Single(e=>e.Id==old.Id).Version=="2.1"&&old.Version=="1.0"&&old.Enabled,"queued validation uses actual DLL version and does not modify loaded state");
        check(projected.Single(e=>e.Id==old.Id).RequiredRequisites.Count==1&&ModRequisites.ValidateSelection(projected)=="","projected update keeps hard prerequisite declarations");
        old.Wanted=false;
        projected=ModRequisiteSelection.WithPending(new[]{parent,old,child},new[]{update});
        check(ModRequisites.ValidateSelection(projected).Contains("disabled"),"downloaded update cannot silently enable a deliberately unselected dependency");
        var legacy=new PendingModUpdate { Plugin=old.Id,Version="v99.0" };
        check(!ModRequisiteSelection.Satisfies(requirement,legacy),"old queues without verified DLL versions cannot falsely satisfy a minimum from their release tag");
        requirement.MinimumVersion="";
        check(ModRequisiteSelection.Satisfies(requirement,legacy),"an unversioned requisite can recognize a previously verified legacy queue");
        var disabled=new ModRequisiteIssue { Requirement=requirement,Status=ModRequisiteStatus.Disabled };
        var wrong=new ModRequisiteIssue { Requirement=requirement,Status=ModRequisiteStatus.WrongVersion };
        var restarting=new ModRequisiteIssue { Requirement=requirement,Status=ModRequisiteStatus.RestartRequired };
        check(ReferenceEquals(ModRequisiteSelection.ActionIssue(disabled,new[]{wrong,disabled}),disabled),"Enable selects disabled issue rather than downloading an outdated dependency");
        check(ReferenceEquals(ModRequisiteSelection.ActionIssue(disabled,new[]{restarting,disabled}),disabled),"queued prerequisite update still permits Enable before restarting");
        check(ReferenceEquals(ModRequisiteSelection.ActionIssue(wrong,new[]{restarting}),restarting),"a stale Download button recognizes the fresh queued restart state");
        QueuedMetadata(check);
    }

    private static void QueuedMetadata(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-queued-requisites-"+Guid.NewGuid().ToString("N"));
        string plugins=Path.Combine(root,"BepInEx","plugins"),queue=Path.Combine(root,"queue");Directory.CreateDirectory(plugins);
        try
        {
            const string dependency="queue.dependency",hardChild="queue.hard.child",extra="queue.extra";
            string payload=Path.Combine(root,"fresh.dll");
            Fixture(payload,dependency,"2.1",new[]{(hardChild,"1.0",1),("queue.optional","",2),("queue.mixed.flags","",3)},Card(dependency,extra));
            ModUpdateFiles.StageInstall(queue,plugins,payload,dependency,"owner/dependency","2.0");
            var pending=ModUpdateFiles.ReadPending(queue,plugins);
            var metadata=QueuedModRequisiteMetadata.Read(pending,plugins);
            check(metadata[dependency].Error.Length==0&&metadata[dependency].Version=="2.1","queued metadata reads the actual staged DLL version without executing it");
            check(metadata[dependency].Hard.Any(r=>r.Plugin==hardChild&&r.MinimumVersion=="1.0")&&!metadata[dependency].Hard.Any(r=>r.Plugin=="queue.optional")&&metadata[dependency].Hard.Any(r=>r.Plugin=="queue.mixed.flags"),"queued metadata keeps hard minimum versions, excludes optional flags and recognizes the hard bit in mixed flags");
            var parent=Plugin("queue.parent");parent.Manifest=Preview(parent.Id,dependency);
            var projected=ModRequisiteSelection.WithPending(new[]{parent},pending,metadata,plugins);
            var issues=ModRequisites.ForMod(parent,projected);
            check(issues.Any(i=>i.Requirement.Plugin==hardChild&&i.Status==ModRequisiteStatus.Missing)&&issues.Any(i=>i.Requirement.Plugin==extra&&i.Status==ModRequisiteStatus.Missing),"fresh queued dependency exposes its transitive hard and embedded JSON prerequisites before restarting");
            check(ModRequisites.ValidateSelection(projected).Length>0,"fresh queued dependency with missing children cannot pass restart selection validation");
            var child=Plugin(hardChild);var extraEntry=Plugin(extra);var mixed=Plugin("queue.mixed.flags");
            projected=ModRequisiteSelection.WithPending(new[]{parent,child,extraEntry,mixed},pending,metadata,plugins);
            check(ModRequisites.ValidateSelection(projected)=="","fresh queued dependency can restart once all actual child prerequisites are available");
            check(!projected.Single(e=>e.Id==dependency).Enabled&&projected.Single(e=>e.Id==dependency).Wanted,"queued metadata does not pretend its new dependency is loaded already");

            string updateQueue=Path.Combine(root,"updates"),oldPath=Path.Combine(plugins,"Old.dll"),updatePath=Path.Combine(root,"updated.dll");
            Fixture(oldPath,dependency,"1.0",new[]{("queue.removed.child","",1)},Card(dependency,"queue.old.preview"));
            Fixture(updatePath,dependency,"3.0",new[]{(hardChild,"2.0",1)},Card(dependency,extra));
            ModUpdateFiles.Stage(updateQueue,plugins,oldPath,updatePath,dependency,"owner/dependency","v9.0");
            var updatePending=ModUpdateFiles.ReadPending(updateQueue,plugins);var updateMetadata=QueuedModRequisiteMetadata.Read(updatePending,plugins);
            var oldEntry=Plugin(dependency);oldEntry.Path=oldPath;oldEntry.RequiredRequisites.Add(new ModRequisite { Plugin="queue.removed.child" });
            oldEntry.Manifest=Preview(dependency,"queue.old.preview");oldEntry.Manifest.EmbeddedAssembly=oldPath;
            child.Version="2.0";
            projected=ModRequisiteSelection.WithPending(new[]{parent,oldEntry,child,extraEntry},updatePending,updateMetadata,plugins);
            var future=projected.Single(e=>e.Id==dependency);
            check(future.Version=="3.0"&&future.RequiredRequisites.Single().Plugin==hardChild&&!future.Requires.Contains("queue.removed.child")&&future.Manifest!.Requisites.Single().Plugin==extra,"queued update replaces old hard dependencies and embedded declarations with actual new payload metadata");
            check(ModRequisites.ValidateSelection(projected)==""&&oldEntry.RequiredRequisites.Single().Plugin=="queue.removed.child"&&oldEntry.Manifest.Requisites.Single().Plugin=="queue.old.preview","queued update validates the new dependency graph while preserving actual loaded metadata");
            var pack=new ModEntry { Id="queue.pack",Name="Pack",Content=true,Version="7.0",Enabled=true,Wanted=true,WrapperPlugin=dependency,WrapperPath=oldPath,Path="resource:QueuedTest:pack",Manifest=oldEntry.Manifest };
            var packProjection=ModRequisiteSelection.WithPending(new[]{pack,oldEntry,child,extraEntry},updatePending,updateMetadata,plugins);
            check(packProjection.Single(e=>e.Content).Manifest!.Requisites.Single().Plugin==extra&&ModRequisites.ValidateSelection(packProjection)=="","content inherited wrapper card uses its queued preview and does not retain removed old prerequisites");
            check(packProjection.Single(e=>e.Content).Version=="7.0"&&packProjection.Single(e=>e.Content).Enabled&&pack.Manifest.Requisites.Single().Plugin=="queue.old.preview","inherited card projection preserves actual content version, runtime state and original preview");
            child.Version="1.9";
            projected=ModRequisiteSelection.WithPending(new[]{parent,oldEntry,child,extraEntry},updatePending,updateMetadata,plugins);
            check(ModRequisites.ForMod(parent,projected).Any(i=>i.Requirement.Plugin==hardChild&&i.Status==ModRequisiteStatus.WrongVersion),"queued update new hard minimum version is enforced before restarting");
            child.Version="2.0";

            oldEntry.Manifest=Preview(dependency,"queue.manual.child");oldEntry.Manifest.Directory=Path.Combine(plugins,"ManualPreviews");
            projected=ModRequisiteSelection.WithPending(new[]{parent,oldEntry,child,Plugin("queue.manual.child")},updatePending,updateMetadata,plugins);
            check(ReferenceEquals(projected.Single(e=>e.Id==dependency).Manifest,oldEntry.Manifest)&&ModRequisites.ValidateSelection(projected)=="","existing manual preview remains authoritative over a queued embedded card while actual hard DLL prerequisites still apply");
            pack.Manifest=oldEntry.Manifest;
            packProjection=ModRequisiteSelection.WithPending(new[]{pack,oldEntry,child,Plugin("queue.manual.child")},updatePending,updateMetadata,plugins);
            check(ReferenceEquals(packProjection.Single(e=>e.Content).Manifest,pack.Manifest)&&ModRequisites.ValidateSelection(packProjection)=="","manual inherited content preview keeps its explicit prerequisites above queued embedded metadata");
            var omitted=ModRequisiteSelection.WithPending(new[]{parent,oldEntry},updatePending,new System.Collections.Generic.Dictionary<string,QueuedModRequisiteMetadata>(),plugins);
            check(ModRequisites.ForMod(parent,omitted).Any(i=>i.Status==ModRequisiteStatus.Invalid),"explicit metadata validation cannot quietly fall back when a pending job has no parsed result");

            string sidecarQueue=Path.Combine(root,"sidecar-queue"),sidecarPlugins=Path.Combine(root,"sidecar-BepInEx","plugins");Directory.CreateDirectory(sidecarPlugins);
            var sidecar=new StagedModSidecar { Name="preview.doorman.json",Data=Encoding.UTF8.GetBytes(Card(dependency,"queue.managed.child")) };
            ModUpdateFiles.StageInstall(sidecarQueue,sidecarPlugins,payload,dependency,"owner/dependency","2.0",new[]{sidecar});
            var sidecarMetadata=QueuedModRequisiteMetadata.Read(ModUpdateFiles.ReadPending(sidecarQueue,sidecarPlugins),sidecarPlugins);
            check(sidecarMetadata[dependency].Manifest!.Requisites.Single().Plugin=="queue.managed.child","queued managed sidecar declaration takes priority over its embedded preview");

            File.AppendAllText(Path.Combine(pending[0].Job,"payload.bin"),"changed");
            var corrupt=QueuedModRequisiteMetadata.Read(pending,plugins);
            projected=ModRequisiteSelection.WithPending(new[]{parent},pending,corrupt,plugins);
            check(corrupt[dependency].Error.Contains("changed")&&ModRequisites.ForMod(parent,projected).Any(i=>i.Status==ModRequisiteStatus.Invalid),"modified queued DLL is visibly invalid and cannot be treated as dependency-ready");

            string badPayload=Path.Combine(root,"bad-preview.dll"),badQueue=Path.Combine(root,"bad-preview-queue"),badPlugins=Path.Combine(root,"bad-BepInEx","plugins");Directory.CreateDirectory(badPlugins);
            Fixture(badPayload,dependency,"2.1",Array.Empty<(string,string,int)>(),"{\"plugin\":\""+dependency+"\",\"name\":\"Bad preview\",\"requisites\":\"oops\"}");
            ModUpdateFiles.StageInstall(badQueue,badPlugins,badPayload,dependency,"owner/dependency","2.0");
            var badPlans=ModUpdateFiles.ReadPending(badQueue,badPlugins);var badMetadata=QueuedModRequisiteMetadata.Read(badPlans,badPlugins);
            check(badMetadata[dependency].Error.Length>0&&ModRequisites.ValidateSelection(ModRequisiteSelection.WithPending(new[]{parent},badPlans,badMetadata,badPlugins)).Length>0,"unreadable queued embedded JSON fails closed before installation rather than losing its declarations");
            check(!Directory.GetFiles(plugins,"*.dll").Any(p=>p.Contains("DOORMAN-dependency")),"queued prerequisite metadata inspection never installs a DLL into plugins");
        }
        finally { Directory.Delete(root,true); }
    }

    private static ModEntry Plugin(string id)=>new ModEntry { Id=id,Name=id,Version="1.0",Enabled=true,Wanted=true };
    private static ModManifest Preview(string id,string prerequisite)=>new ModManifest { Plugin=id,Name=id,Requisites=new[]{new ModRequisite { Plugin=prerequisite }} };
    private static string Card(string id,string prerequisite)=>"{\"plugin\":\""+id+"\",\"name\":\""+id+"\",\"requisites\":[{\"plugin\":\""+prerequisite+"\"}]}";
    private static void Fixture(string path,string id,string version,(string Plugin,string Version,int Flags)[] dependencies,string? manifest)
    {
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("QueuedTest",new Version(version)),"QueuedTest",ModuleKind.Dll))
        {
            var module=assembly.MainModule;var source=new AssemblyNameReference("BepInEx",new Version(5,4,23,0));
            var plugin=new TypeReference("BepInEx","BepInPlugin",module,source);var constructor=new MethodReference(".ctor",module.TypeSystem.Void,plugin) { HasThis=true };
            for(int i=0;i<3;i++)constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            var type=new TypeDefinition("QueuedTests","Plugin",TypeAttributes.Public,module.TypeSystem.Object);module.Types.Add(type);
            var attribute=new CustomAttribute(constructor);foreach(var value in new[]{id,id,version})attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,value));type.CustomAttributes.Add(attribute);
            foreach(var required in dependencies)
            {
                var dependency=new TypeReference("BepInEx","BepInDependency",module,source);var dependencyConstructor=new MethodReference(".ctor",module.TypeSystem.Void,dependency) { HasThis=true };
                dependencyConstructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));var parameter=required.Version.Length>0?module.TypeSystem.String:module.TypeSystem.Int32;dependencyConstructor.Parameters.Add(new ParameterDefinition(parameter));
                var declaration=new CustomAttribute(dependencyConstructor);declaration.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,required.Plugin));declaration.ConstructorArguments.Add(new CustomAttributeArgument(parameter,required.Version.Length>0?(object)required.Version:required.Flags));type.CustomAttributes.Add(declaration);
            }
            if(manifest!=null)module.Resources.Add(new EmbeddedResource("doorman.json",ManifestResourceAttributes.Private,Encoding.UTF8.GetBytes(manifest)));
            assembly.Write(path);
        }
    }
}
