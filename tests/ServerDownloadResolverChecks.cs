using System;
using System.IO;
using System.Linq;
using KellysJOINCHECK;

internal static class ServerDownloadResolverChecks
{
    private static string Hash(char c)=>new string(c,64);
    private static GameplayFingerprintSnapshot Local(params GameplayFileRecord[] records)=>new GameplayFingerprintSnapshot {Ready=true,Complete=true,LoaderVersion="2.0.1",Records=records};
    private static GameplayFingerprintSnapshot Remote(params GameplayContentVersion[] contents)=>new GameplayFingerprintSnapshot {Ready=true,Complete=true,LoaderVersion="2.0.1",ContentSetHash=Hash('a'),ContentSelection=contents};
    private static NommRecord Record(string id,string version,string filename,string hash)=>new NommRecord {Id=id,Name=id,Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact {Version=version,FileName=filename,Hash="sha256:"+hash,DownloadUrl="https://github.com/owner/repo/releases/download/"+version+"/"+filename}}};
    internal static void Run(Action<bool,string> check)
    {
        var old=new GameplayFileRecord {Kind="content",Id="aryx_lightfighter1",Version="1.1.3",Hash=Hash('1')};
        var required=new GameplayContentVersion {Id="aryx_lightfighter1",Version="1.1.4"};
        var content=new ModEntry {Id=old.Id,Content=true,Path="resource:shrike:pack",WrapperPath=@"C:\mods\Shrike.dll",WrapperPlugin="Aryx_LightFighter1"};
        var plugin=new ModEntry {Id="Aryx_LightFighter1",Path=@"C:\mods\Shrike.dll"};
        var release=Record("aryx.f99","1.1.4","Shrike.dll",Hash('b'));
        var plan=ServerDownloadResolver.Resolve(Remote(required),Local(old),new[]{content,plugin},new[]{release},null);
        check(plan.Candidates.Count==1&&plan.Manual.Count==0&&plan.Candidates[0].OwnerPlugin==plugin.Id&&plan.Candidates[0].Target==plugin.Path,"server setup resolves installed wrapper by verified owner and exact requested version without NOSMR");
        check(ServerDownloadResolver.Resolve(Remote(required),Local(old),new[]{content,plugin,plugin},new[]{release},null).Candidates.Count==0,"duplicate plugin owners never select an overwrite target");
        var conflict=new NosmrReport.Result{Verified=true};conflict.Entries.Add(new NosmrReport.Entry{Resolved=true,Id="aryx.f99",Version="1.1.3"});
        var mismatch=ServerDownloadResolver.Resolve(Remote(required),Local(old),new[]{content,plugin},new[]{release},conflict);
        check(mismatch.Candidates.Count==0&&mismatch.Manual.Single().Contains("NOSMR"),"stale host report does not substitute a different package version");
        var wrong=Record("aryx.f99","1.1.4","Shrike.dll",Hash('b'));wrong.Artifacts[0].DownloadUrl="https://evil.example/Shrike.dll";
        check(ServerDownloadResolver.Resolve(Remote(required),Local(old),new[]{content,plugin},new[]{wrong},null).Candidates.Count==0,"resolver rejects non-GitHub downloads even when identity matches");
        var direct=Record("PB22Trident","1.2.1","PB22Trident.dll",Hash('b'));
        var missing=ServerDownloadResolver.Resolve(Remote(new GameplayContentVersion{Id="pb22trident",Version="1.2.1"}),Local(),Array.Empty<ModEntry>(),new[]{direct},null);
        check(missing.Candidates.Count==1&&missing.Candidates.Single().Target.Length==0,"exact registry ID supports a missing package without filename or name guessing");
        var oa=Record("aryx.oa27","1.0.3","OA27.nobp",Hash('d'));
        var oaContent=new ModEntry{Id="aryx_propattacker1",Content=true,Path=@"C:\mods\OA27-old.nobp"};
        var oaPlan=ServerDownloadResolver.Resolve(Remote(new GameplayContentVersion{Id=oaContent.Id,Version="1.0.3"}),Local(new GameplayFileRecord{Kind="content",Id=oaContent.Id,Version="1.0.1",Hash=Hash('1')}),new[]{oaContent},new[]{oa},null);
        check(oaPlan.Candidates.Count==1&&oaPlan.Candidates[0].Standalone&&oaPlan.Candidates[0].Target==oaContent.Path,"audited content alias resolves the installed standalone OA-27 bundle");
        var oldTrident=new GameplayFileRecord{Kind="content",Id="pb22trident",Version="1.1.0",Hash=Hash('1')};
        var tridentContent=new ModEntry{Id="pb22trident",Content=true,Path="resource:PB22Trident:asset",WrapperPlugin="nick.nuclearoption.pb22trident",WrapperPath=@"C:\mods\Trident.dll"};
        var tridentPlugin=new ModEntry{Id="nick.nuclearoption.pb22trident",Path=@"C:\mods\Trident.dll"};
        var tridentPlan=ServerDownloadResolver.Resolve(Remote(new GameplayContentVersion{Id="pb22trident",Version="1.2.1"}),Local(oldTrident),new[]{tridentContent,tridentPlugin},new[]{direct},null);
        check(tridentPlan.Candidates.Count==1&&tridentPlan.Candidates[0].OwnerPlugin==tridentPlugin.Id&&tridentPlan.Candidates[0].Target==tridentPlugin.Path,"inspected PB22 plugin alias updates its existing owner rather than installing a duplicate");
        var cargo=Record("aryx.mc260","1.2.1","Chimera.dll","01350ee52f3313e782490368d9915f7fe59e4c34ddfa220f454fbd7f3a22104f");
        var oldCargo=new ModEntry {Id="Aryx_MC260_Chimera",Path=@"C:\mods\Chimera.dll"};
        var cargoRemote=Remote(new GameplayContentVersion{Id="aryx_cargoplane1",Version="1.2.1"});
        var cargoPlan=ServerDownloadResolver.Resolve(cargoRemote,Local(),new[]{oldCargo},new[]{cargo},null);
        check(cargoPlan.Candidates.Count==1&&cargoPlan.Candidates[0].OwnerPlugin==oldCargo.Id&&cargoPlan.Candidates[0].Target==oldCargo.Path,"inspected cargo manifest digest maps its renamed content to the old verified wrapper");
        cargo.Artifacts[0].Hash="sha256:"+Hash('c');
        check(ServerDownloadResolver.Resolve(cargoRemote,Local(),new[]{oldCargo},new[]{cargo},null).Candidates.Count==0,"a changed cargo release digest cannot reuse the inspected alias");
        var unknown=ServerDownloadResolver.Resolve(Remote(new GameplayContentVersion{Id="not-in-registry",Version="1.0"}),Local(),Array.Empty<ModEntry>(),new[]{direct},null);
        check(unknown.Candidates.Count==0&&unknown.Manual.Count==1,"an unknown content identity is reported for manual handling rather than guessed");
        var identical=new GameplayFileRecord{Kind="content",Id="x",Version="1.0",Hash=Hash('1')};
        var sameVersion=ServerDownloadResolver.Resolve(Remote(new GameplayContentVersion{Id="x",Version="1.0"}),Local(identical),Array.Empty<ModEntry>(),Array.Empty<NommRecord>(),null);
        check(sameVersion.Candidates.Count==0&&sameVersion.Manual.Single().Contains("files differ"),"same versions plus aggregate fingerprint mismatch cannot choose a speculative replacement");
        var fallbackRecord=Record("x","1.0","x.nobp",Hash('2'));
        var fallbackContent=new ModEntry{Id="x",Content=true,Path=@"C:\mods\x.nobp"};
        var fallback=ServerDownloadResolver.Resolve(Remote(new GameplayContentVersion{Id="x",Version="1.0"}),Local(identical),new[]{fallbackContent},new[]{fallbackRecord},null,true);
        check(fallback.Candidates.Count==1&&fallback.Candidates[0].Target==fallbackContent.Path,"a same-version alternative is proposed only for later full-server hash proof");
        fallbackRecord.Artifacts[0].Hash="sha256:"+Hash('1');
        var sameBytes=ServerDownloadResolver.Resolve(Remote(new GameplayContentVersion{Id="x",Version="1.0"}),Local(identical),new[]{fallbackContent},new[]{fallbackRecord},null,true);
        check(sameBytes.Candidates.Count==0,"a same-version package whose registry hash is already installed is not downloaded again");
        var agni=Record("1509_PalaFighter1","1.0.1","Agni.nobp",Hash('a'));
        var chimera=Record("aryx.mc260","1.2.0","Chimera.dll",Hash('b'));
        var ternion=Record("blueprinter.ternion","1.0.1","Ternion.nobp",Hash('c'));
        var utility=Record("BepInEx.ConfigurationManager","18.4.1","ConfigurationManager.dll",Hash('d'));
        var host=new NosmrReport.Result{Verified=true};
        foreach(var r in new[]{agni,chimera,ternion,utility})host.Entries.Add(new NosmrReport.Entry{Resolved=true,Id=r.Id,Name=r.Name,Version=r.Artifacts[0].Version});
        var installedAgni=new ModEntry{Id="1509_palafighter1",Name="Agni",Version="1.0.1",Content=true,Enabled=true,Path=@"C:\mods\Agni.nobp"};
        var installedChimera=new ModEntry{Id="aryx_cargoplane1",Name="Chimera",Version="1.2.1",Content=true,Enabled=true,Path="resource:Aryx_MC260_Chimera:x.nobp",WrapperPlugin="Aryx_MC260_Chimera",WrapperPath=@"C:\mods\Chimera.dll"};
        var chimeraOwner=new ModEntry{Id="Aryx_MC260_Chimera",Path=@"C:\mods\Chimera.dll"};
        var clientUtility=new ModEntry{Id="com.bepis.bepinex.configurationmanager",Version="19.0",Path=@"C:\mods\ConfigurationManager.dll"};
        var extra=new ModEntry{Id="aryx_lightfighter1",Name="extra",Version="1.1.4",Content=true,Enabled=true,WrapperPlugin="Aryx_LightFighter1",WrapperPath=@"C:\mods\Shrike.dll"};
        var unknownContent=new ModEntry{Id="unlisted_content",Name="unknown",Content=true,Enabled=true,Path=@"C:\mods\Unknown.nobp"};
        var reported=ServerDownloadResolver.ResolveReported(host,new[]{installedAgni,installedChimera,chimeraOwner,clientUtility,extra,unknownContent},new[]{agni,chimera,ternion,utility,release});
        check(reported.Candidates.Count==2&&reported.Candidates.Any(c=>c.Record.Id=="aryx.mc260"&&c.Target==chimeraOwner.Path)&&reported.Candidates.Any(c=>c.Record.Id=="blueprinter.ternion"&&c.Standalone),"NOSMR setup finds the older installed Chimera and the missing standalone Ternion");
        check(!reported.Candidates.Any(c=>c.Record.Id==utility.Id||c.Record.Id==agni.Id),"NOSMR setup keeps optional utilities and skips an already matching Agni");
        check(reported.DisableContent.SequenceEqual(new[]{extra.Id})&&reported.UnknownContent.Contains("unknown"),"reported gameplay selection excludes only identified extra content and retains unidentified content");
        var bote=Record("bote","1.5.4","Bote.dll",Hash('e'));host.Entries.Add(new NosmrReport.Entry{Resolved=true,Id="bote",Name="BOTE",Version="1.5.4"});
        var boteContent=new ModEntry{Id="bote_content",Name="BOTE",Version="1.5.4",Content=true,Enabled=true,Path="resource:Bote:patch.nobp",WrapperPath=@"C:\mods\Bote.dll"};
        var boteMain=new ModEntry{Id="com.minec.bote",Path=boteContent.WrapperPath};var boteInputs=new ModEntry{Id="com.minec.bote.inputs",Path=boteContent.WrapperPath};
        var botePlan=ServerDownloadResolver.ResolveReported(host,new[]{boteContent,boteMain,boteInputs},new[]{agni,chimera,ternion,utility,bote});
        check(!botePlan.DisableContent.Contains(boteContent.Id)&&!botePlan.Candidates.Any(c=>c.Record.Id=="bote")&&!botePlan.UnknownContent.Contains("BOTE"),"matching multi-plugin BOTE content is recognised as one installed package");
        var missingBote=ServerDownloadResolver.ResolveReported(host,Array.Empty<ModEntry>(),new[]{agni,chimera,ternion,utility,bote});
        check(missingBote.Candidates.Any(c=>c.Record.Id=="bote"&&c.AllowMulti),"the reviewed BOTE gameplay package can use a multi-plugin install");
        var fs41=Record("aryx.fs41","1.1.7.1","eclipse.dll",Hash('c'));fs41.Tags=new[]{"mod","aircraft","blueprinter"};
        var f16=Record("aryx.f16m","1.2.3","viper.dll",Hash('d'));f16.Tags=new[]{"aircraft","blueprinter"};
        var f99=Record("aryx.f99","1.1.4","shrike.dll",Hash('e'));f99.Tags=new[]{"aircraft","blueprinter"};
        var x77=Record("x77","1.1.3","x77.zip",Hash('f'));x77.Tags=new[]{"weapon","blueprinter"};
        var ui=Record("client.ui","1.0","ui.dll",Hash('a'));ui.Tags=new[]{"blueprinter","aircraft","ui"};
        var unclassified=Record("client.utility","1.0","utility.dll",Hash('b'));unclassified.Tags=new[]{"mod","blueprinter"};
        var fresh=new NosmrReport.Result{Verified=true};foreach(var r in new[]{fs41,f16,f99,x77,ui,unclassified})fresh.Entries.Add(new NosmrReport.Entry{Resolved=true,Id=r.Id,Version=r.Artifacts[0].Version});
        var freshPlan=ServerDownloadResolver.ResolveReported(fresh,Array.Empty<ModEntry>(),new[]{fs41,f16,f99,x77,ui,unclassified});
        check(freshPlan.Candidates.Select(c=>c.Record.Id).SequenceEqual(new[]{"aryx.fs41","aryx.f16m","aryx.f99","x77"}),"missing tagged aircraft and weapons are selected without installing client tools or unclassified mods");
        x77.Artifacts[0].Hash="";freshPlan=ServerDownloadResolver.ResolveReported(fresh,Array.Empty<ModEntry>(),new[]{fs41,f16,f99,x77,ui,unclassified});
        check(freshPlan.Manual.Any(m=>m.Contains("x77"))&&!freshPlan.Candidates.Any(c=>c.Record.Id=="x77"),"a missing gameplay mod without a usable checksum is reported rather than silently skipped");
        string catalogue=Environment.GetEnvironmentVariable("DOORMAN_REAL_NOMNOM_CATALOGUE")??"";
        if(catalogue.Length>0)
        {
            var actual=NommMetadata.Parse(File.ReadAllBytes(catalogue));var wantedIds=new[]{"aryx.fs41","aryx.f16m","aryx.f99","blacknight2u.f117a.nighthawk","com.mursisru.x77warewind","MiG29Fulcrum","BepInEx.ConfigurationManager"};
            var choices=wantedIds.Select(id=>actual.Single(r=>r.Id==id)).ToArray();var advertised=new NosmrReport.Result{Verified=true};
            foreach(var r in choices)advertised.Entries.Add(new NosmrReport.Entry{Resolved=true,Id=r.Id,Version=r.Artifacts[0].Version});
            var realistic=ServerDownloadResolver.ResolveReported(advertised,Array.Empty<ModEntry>(),actual);
            check(choices.Take(6).All(r=>realistic.Candidates.Any(c=>c.Record.Id==r.Id))&&!realistic.Candidates.Any(c=>c.Record.Id=="BepInEx.ConfigurationManager"),"the real NOMNOM catalogue selects missing aircraft and excludes Configuration Manager");
        }
        string root=Path.Combine(Path.GetTempPath(),"doorman-queued-test");
        var queuedCandidate=new ServerDownloadCandidate{Record=chimera,Version="1.2.0",Target=Path.Combine(root,"Chimera.dll")};
        var queued=new PendingModUpdate{Plugin="Aryx_MC260_Chimera",Target="Chimera.dll",NewHash=Hash('b')};
        check(ServerDownloadResolver.AlreadyQueued(queuedCandidate,new[]{queued},Array.Empty<StandaloneBundlePlan>(),root),"the matching queued Chimera downgrade is reused rather than downloaded again");
        queued.Target="other.dll";check(!ServerDownloadResolver.AlreadyQueued(queuedCandidate,new[]{queued},Array.Empty<StandaloneBundlePlan>(),root),"a queued download for a different target is not mistaken for the required mod");
        var queuedBote=missingBote.Candidates.Single(c=>c.Record.Id=="bote");var boteJob=new PendingModUpdate{Schema=4,Plugin="com.minec.bote",NewHash=Hash('e')};
        check(ServerDownloadResolver.AlreadyQueued(queuedBote,new[]{boteJob},Array.Empty<StandaloneBundlePlan>(),root),"an existing reviewed BOTE install is reused by package identity and checksum");
        boteJob.NewHash=Hash('f');check(!ServerDownloadResolver.AlreadyQueued(queuedBote,new[]{boteJob},Array.Empty<StandaloneBundlePlan>(),root),"a mismatching queued package checksum is never reused");
        host.Verified=false;reported=ServerDownloadResolver.ResolveReported(host,new[]{installedChimera,chimeraOwner},new[]{chimera});
        check(reported.Candidates.Count==0&&reported.DisableContent.Count==0&&reported.Manual.Count==1,"an unverified NOSMR report never selects or downloads mods");
    }
}



