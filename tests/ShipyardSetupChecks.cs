using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using KellysJOINCHECK;

// Actual server fingerprint and source hashes from the 30 October support report
// and the independently captured Shipyard/NOMNOM staging evidence.
internal static class ShipyardSetupChecks
{
    private static GameplayFileRecord File(string id,string version,string hash)
        =>new GameplayFileRecord {Kind="content",Id=id,Version=version,Hash=hash};
    private static NommRecord Package(string id,string version,string filename,string hash)
        =>new NommRecord {Id=id,Name=id,Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact {Version=version,FileName=filename,Hash="sha256:"+hash,DownloadUrl="https://github.com/owner/repo/releases/download/"+version+"/"+filename}}};
    private const string ServerHash="8556b3fd6c889bd81cc88b987a0e7f80f9bdf7823ab0115569fa1b1d757b4014";
    private static GameplayFileRecord[] Captured()
    {
        return new[]{
            File("1509_palafighter1", "1.0.1", "d4bef8d30fb0bb153dfb18c3d13f97237f0ba66e14f1df39c6eeb8a155808092"),
            File("aryx_cargoplane1", "1.2.1", "01350ee52f3313e782490368d9915f7fe59e4c34ddfa220f454fbd7f3a22104f"),
            File("aryx_expanded_weapons", "1.1.1", "acb6e94fff6354bee7a5f28893fd28837cafadf298b945270c029aa1f0b3e996"),
            File("aryx_f16m", "1.2.3", "6b71141b324ae92108125881a4db2e29abebbbd95fd968d80284aff4037dbf13"),
            File("aryx_interceptor1", "1.1.7.1", "a7f62f93228274895a3661a3e676943052471b678a52caf195244d22d38c91d3"),
            File("aryx_kingraptor", "1.0.4", "c57b70ca9859b7638935d30ec3a60b697d8bbdf608f00d59f2ca07aae53eb46e"),
            File("aryx_lightfighter1", "1.1.4", "94a3ad2a29195f70442890f3bd7340803d78967c6709f0aad0e5bd2dec1a64b5"),
            File("aryx_lighthelicopter1", "1.1.2", "287d20334d5540399a2ad3ab6729d0209cc59acc46a55f90688525315680aab7"),
            File("aryx_mig-15", "1.1.3", "dd1cf1c04f56db711c4ed6ab89572ece1878597bd22d86439975e4f7f02e5072"),
            File("aryx_navalexpansion", "1.0.4", "7d43d38b59511baee69e5e9025b4ba6353fee8f1afe2766457e61795e8ac9eb3"),
            File("aryx_propattacker1", "1.0.3", "23fce1e8f71f25f5d289cfb0c6a4a3088053c5d437b6bdf0d856dfa0deac84dc"),
            File("pb22trident", "1.2.1", "d53c67e921bcde55fb3eb401bcda755ea9e07cfdaa02525aea7161225d2250fd"),
        };
    }
    internal static void Run(Action<bool,string> check)
    {
        var files=Captured();
        check(GameplayFingerprintPolicy.ContentHash(files)==ServerHash,"Shipyard captured registry releases reconstruct the exact server content fingerprint");
        var remote=new GameplayFingerprintSnapshot {Ready=true,Complete=true,ContentSetHash=ServerHash,ContentSelection=files.Select(f=>new GameplayContentVersion {Id=f.Id,Version=f.Version}).ToArray()};
        var available=new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=files};
        var loaded=new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=files};
        check(GameplayFingerprintSetup.Prepare(remote,available,loaded).CanSetup,"Shipyard matching server selection passes exact setup preflight");
        var wrong=files.Select(f=>File(f.Id,f.Version,f.Hash)).ToArray();wrong.Single(f=>f.Id=="aryx_lightfighter1").Hash=new string('a',64);
        check(!GameplayFingerprintSetup.Prepare(remote,new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=wrong},loaded).CanSetup,"Shipyard modified same-version aircraft fails server fingerprint");
        var missing=files.Where(f=>f.Id!="aryx_cargoplane1").ToArray();
        check(!GameplayFingerprintSetup.Prepare(remote,new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=missing},loaded).CanSetup,"Shipyard missing cargo cannot be treated as a complete setup");
        var replacements=new[]{"aryx_cargoplane1","aryx_lightfighter1","aryx_lighthelicopter1","aryx_propattacker1","pb22trident"};
        var old=files.Where(f=>!replacements.Contains(f.Id)).ToArray();
        var staged=files.Where(f=>replacements.Contains(f.Id)).ToDictionary(f=>f.Id,f=>f.Hash,StringComparer.Ordinal);
        var before=new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=old};
        check(ServerDownloadProof.Matches(remote,before,staged,out _),"Shipyard five downloaded releases and seven existing files match the server before staging");
        staged["aryx_propattacker1"]=new string('a',64);
        check(!ServerDownloadProof.Matches(remote,before,staged,out _),"Shipyard batch with a tampered download cannot be staged as an exact match");
        staged.Remove("aryx_cargoplane1");
        check(!ServerDownloadProof.Matches(remote,before,staged,out _),"Shipyard incomplete download batch cannot pass exact verification");
        var local=files.Where(f=>!replacements.Contains(f.Id)).Concat(new[]{
            File("aryx_lightfighter1","1.1.3",new string('1',64)),
            File("aryx_lighthelicopter1","1.1.1",new string('2',64)),
            File("aryx_propattacker1","1.0.1",new string('3',64)),
            File("pb22trident","1.1.0",new string('4',64)),
            File("aryx_mc260_chimera","1.2.0",new string('5',64))
        }).ToArray();
        var inventory=new[]{
            new ModEntry {Id="aryx_lightfighter1",Content=true,WrapperPlugin="Aryx_LightFighter1",WrapperPath=@"C:\mods\F99.dll"},
            new ModEntry {Id="Aryx_LightFighter1",Path=@"C:\mods\F99.dll"},
            new ModEntry {Id="aryx_lighthelicopter1",Content=true,WrapperPlugin="Aryx_RAH_72_Knockout",WrapperPath=@"C:\mods\RAH.dll"},
            new ModEntry {Id="Aryx_RAH_72_Knockout",Path=@"C:\mods\RAH.dll"},
            new ModEntry {Id="aryx_propattacker1",Content=true,Path=@"C:\mods\OA27.nobp"},
            new ModEntry {Id="pb22trident",Content=true,WrapperPlugin="nick.nuclearoption.pb22trident",WrapperPath=@"C:\mods\PB22.dll"},
            new ModEntry {Id="nick.nuclearoption.pb22trident",Path=@"C:\mods\PB22.dll"},
            new ModEntry {Id="Aryx_MC260_Chimera",Path=@"C:\mods\Chimera.dll"}
        };
        var registry=new[]{
            Package("aryx.mc260","1.2.1","Chimera.dll",files.Single(f=>f.Id=="aryx_cargoplane1").Hash),
            Package("aryx.f99","1.1.4","F99.dll",files.Single(f=>f.Id=="aryx_lightfighter1").Hash),
            Package("aryx.rah72","1.1.2","RAH.dll",files.Single(f=>f.Id=="aryx_lighthelicopter1").Hash),
            Package("aryx.oa27","1.0.3","OA27.nobp",files.Single(f=>f.Id=="aryx_propattacker1").Hash),
            Package("PB22Trident","1.2.1","PB22.dll",files.Single(f=>f.Id=="pb22trident").Hash)
        };
        var shipyard=ServerDownloadResolver.Resolve(remote,new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=local},inventory,registry,null);
        check(shipyard.Manual.Count==0&&shipyard.Candidates.Count==5,"Shipyard real mismatch resolves all five downloads without NOSMR");
        check(shipyard.Candidates.Single(c=>c.ContentId=="aryx_cargoplane1").OwnerPlugin=="Aryx_MC260_Chimera"&&shipyard.Candidates.Single(c=>c.ContentId=="aryx_propattacker1").Standalone,"Shipyard renamed cargo and standalone OA-27 preserve their installed owners");

        var mig=Package("aryx.mig15","1.13","Aryx.s.MiG-15_1.1.3.nobp",files.Single(f=>f.Id=="aryx_mig-15").Hash);
        var navex=Package("aryx.navex","1.0.4","Aryx.Naval.Expansion_1.0.4.dll",files.Single(f=>f.Id=="aryx_navalexpansion").Hash);
        var twoMissing=new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=files.Where(f=>f.Id!="aryx_mig-15"&&f.Id!="aryx_navalexpansion").ToArray()};
        var reported=ServerDownloadResolver.Resolve(remote,twoMissing,Array.Empty<ModEntry>(),new[]{mig,navex},null);
        check(reported.Manual.Count==0&&reported.Candidates.Count==2,"Shipyard missing MiG-15 and NAVEX resolve using their real registry identities");
        var migCandidate=reported.Candidates.Single(c=>c.ContentId=="aryx_mig-15");
        check(migCandidate.Version=="1.1.3"&&migCandidate.PackageVersion=="1.13"&&migCandidate.MatchingArtifacts().Single().FileName=="Aryx.s.MiG-15_1.1.3.nobp","MiG-15 keeps its mod version distinct from its GitHub release tag");
        check(reported.Candidates.Single(c=>c.ContentId=="aryx_navalexpansion").Record.Id=="aryx.navex","NAVEX missing content maps to its verified registry package");
        var host=new NosmrReport.Result {Verified=true};host.Entries.Add(new NosmrReport.Entry {Id="aryx.mig15",Version="1.13",Resolved=true});host.Entries.Add(new NosmrReport.Entry {Id="aryx.navex",Version="1.0.4",Resolved=true});
        var withHost=ServerDownloadResolver.Resolve(remote,twoMissing,Array.Empty<ModEntry>(),new[]{mig,navex},host);
        check(withHost.Candidates.Count==2&&withHost.Manual.Count==0,"a matching NOSMR release tag and exact server fingerprints can coexist when mod and release versions differ");
        host.Entries[0].Version="1.12";
        var mismatchedHost=ServerDownloadResolver.Resolve(remote,twoMissing,Array.Empty<ModEntry>(),new[]{mig,navex},host);
        check(mismatchedHost.Candidates.All(c=>c.ContentId!="aryx_mig-15")&&mismatchedHost.Manual.Any(e=>e.Contains("NOSMR")),"a conflicting host package report still blocks the guessed download");
        var candidateHashes=reported.Candidates.ToDictionary(c=>c.ContentId,c=>c.MatchingArtifacts().Single().Hash.Substring(7),StringComparer.Ordinal);
        check(ServerDownloadProof.Matches(remote,twoMissing,candidateHashes,out _),"different release tags only become installable when both files match the server's complete fingerprint");
        candidateHashes["aryx_mig-15"]=new string('a',64);
        check(!ServerDownloadProof.Matches(remote,twoMissing,candidateHashes,out _),"a filename hint cannot bypass the server's exact content fingerprint");
        var other=Package("aryx.mig15","1.14","Another_1.1.3.nobp",new string('b',64));
        mig.Artifacts=mig.Artifacts.Concat(other.Artifacts).ToArray();
        var ambiguous=ServerDownloadResolver.Resolve(remote,twoMissing,Array.Empty<ModEntry>(),new[]{mig,navex},null);
        check(ambiguous.Candidates.All(c=>c.ContentId!="aryx_mig-15")&&ambiguous.Manual.Any(e=>e.Contains("aryx_mig-15")),"multiple filename matches remain manual instead of guessing");
        mig.Artifacts=new[]{other.Artifacts[0]};mig.Artifacts[0].FileName="Aircraft_11.1.3.nobp";mig.Artifacts[0].DownloadUrl="https://github.com/owner/repo/releases/download/1.14/Aircraft_11.1.3.nobp";
        var partialVersion=ServerDownloadResolver.Resolve(remote,twoMissing,Array.Empty<ModEntry>(),new[]{mig,navex},null);
        check(partialVersion.Candidates.All(c=>c.ContentId!="aryx_mig-15"),"a filename ending in a partial version cannot select the wrong release");
    }

    // Optional network integration probe, never part of the normal offline
    // regression run. It writes only to a new, isolated evidence directory.
    internal static int RealProbe(string registryFile,string root)
    {
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("The real-download evidence folder must be empty.");
        string plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),config=Path.Combine(root,"config");
        Directory.CreateDirectory(plugins);Directory.CreateDirectory(cache);Directory.CreateDirectory(config);
        var records=NommMetadata.Parse(System.IO.File.ReadAllBytes(registryFile));var files=Captured();
        var remote=new GameplayFingerprintSnapshot {Ready=true,Complete=true,ContentSetHash=ServerHash,ContentSelection=files.Select(f=>new GameplayContentVersion {Id=f.Id,Version=f.Version}).ToArray()};
        var available=new GameplayFingerprintSnapshot {Ready=true,Complete=true,Records=files.Where(f=>f.Id!="aryx_mig-15"&&f.Id!="aryx_navalexpansion").ToArray()};
        var plan=ServerDownloadResolver.Resolve(remote,available,Array.Empty<ModEntry>(),records,null);
        if(plan.Candidates.Count!=2||plan.Manual.Count!=0)throw new InvalidDataException("The captured server requirements did not resolve both GitHub releases.");
        using(var updates=new ModUpdateService(plugins,cache))
        {
            var payloads=new List<ServerDownloadPayload>();
            try
            {
                foreach(var candidate in plan.Candidates)
                {
                    var payload=updates.DownloadServerCandidateAsync(candidate,CancellationToken.None).GetAwaiter().GetResult();payloads.Add(payload);
                    Console.WriteLine("Downloaded "+candidate.ContentId+" content "+candidate.Version+" from release "+candidate.PackageVersion+"; SHA-256 "+payload.Hash);
                }
                if(!ServerDownloadProof.Matches(remote,available,payloads.ToDictionary(p=>p.Candidate.ContentId,p=>p.Hash,StringComparer.Ordinal),out var error))throw new InvalidDataException(error);
                Console.WriteLine("PASS: Both real GitHub downloads match the captured complete Shipyard fingerprint.");
                string contentRoot=Path.Combine(cache,"DOORMAN-content-updates"),updateRoot=Path.Combine(cache,"DOORMAN-updates");
                foreach(var payload in payloads)
                {
                    var candidate=payload.Candidate;
                    if(candidate.Standalone)StandaloneBundleUpdates.Stage(contentRoot,plugins,payload.PayloadPath,Path.Combine(plugins,candidate.ArtifactName),candidate.ContentId,candidate.Version,payload.Hash);
                    else updates.StageServerPlugin(payload,Array.Empty<string>(),CancellationToken.None);
                }
                var content=StandaloneBundleUpdates.Pending(contentRoot,plugins).Single();var plugin=ModUpdateFiles.ReadPending(updateRoot,plugins).Single();
                if(content.Version!="1.1.3"||plugin.Plugin!="AryxNavalExpansion")throw new InvalidDataException("Queued identities or content versions were incorrect.");
                StartupUpdatePreflight.ApplyQueues(updateRoot,contentRoot,plugins,config,message=>Console.WriteLine(message));
                if(ModUpdateFiles.Hash(Path.Combine(plugins,content.Target))!=content.NewHash||ModUpdateFiles.Hash(Path.Combine(plugins,plugin.Target))!=plugin.NewHash)throw new InvalidDataException("The isolated startup files differed from the verified downloads.");
                Console.WriteLine("PASS: Both verified packages install through the isolated startup queues.");return 0;
            }
            finally {foreach(var payload in payloads)payload.Dispose();}
        }
    }
}
