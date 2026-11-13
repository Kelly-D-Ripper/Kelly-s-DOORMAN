using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KellysJOINCHECK
{
    // Package identities are selection hints, not proof that bytes match the host.
    // GameplayFingerprintSetup remains the final authority on the complete set.
    internal sealed class ServerDownloadCandidate
    {
        internal string ContentId="", Version="", OwnerPlugin="", Target="";
        // A GitHub release tag can differ from the version inside its mod.
        // These fields only identify an asset; server fingerprint proof is still required.
        internal string ReleaseVersion="",ArtifactName="";
        internal NommRecord Record=null!;
        internal bool Standalone,AllowMulti;
        internal string PackageVersion=>ReleaseVersion.Length>0?ReleaseVersion:Version;
        internal NommArtifact[] MatchingArtifacts()=>(Record.Artifacts??Array.Empty<NommArtifact>())
            .Where(a=>a!=null&&a.Version==PackageVersion&&(ArtifactName.Length==0||a.FileName==ArtifactName)).Take(2).ToArray();
    }
    internal class ServerDownloadPlan
    {
        internal readonly List<ServerDownloadCandidate> Candidates=new List<ServerDownloadCandidate>();
        internal readonly List<string> Manual=new List<string>();
    }
    internal sealed class NosmrSetupPlan : ServerDownloadPlan
    {
        internal readonly List<string> DisableContent=new List<string>();
        internal readonly List<string> UnknownContent=new List<string>();
    }
    internal static class ServerDownloadResolver
    {
        // These are known gameplay packages with code rather than a standalone
        // Blueprinter bundle. Other code-only entries are left to the player.
        private static readonly HashSet<string> NosmrGameplayPlugins=new HashSet<string>(new[]{"kar","bote"},StringComparer.OrdinalIgnoreCase);
        internal static bool IsReportedGameplayPlugin(string id)=>NosmrGameplayPlugins.Contains(id);
        // NOSMR describes a host's pack, including optional client utilities.
        // Only catalogue records explicitly tagged both Blueprinter and a
        // gameplay category may become first-install candidates on that basis.
        internal static bool IsReportedGameplay(NommRecord record)
        {
            if(NosmrGameplayPlugins.Contains(record.Id))return true;
            var tags=new HashSet<string>(record.Tags??Array.Empty<string>(),StringComparer.OrdinalIgnoreCase);
            return tags.Contains("blueprinter")&&tags.Overlaps(new[]{"aircraft","weapon","weapons","ship","ships","naval","vehicle","vehicles","missile","torpedo"})
                &&!tags.Overlaps(new[]{"client","client-only","clientonly","ui","utility","tool"});
        }
        internal static bool AlreadyQueued(ServerDownloadCandidate candidate,PendingModUpdate[] plugins,StandaloneBundlePlan[] bundles,string pluginRoot)
        {
            var artifacts=candidate.MatchingArtifacts();
            if(artifacts.Length!=1||artifacts[0].Hash==null||!artifacts[0].Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase))return false;
            string name=artifacts[0].FileName??"",hash=artifacts[0].Hash.Substring(7);
            if(!ModUpdateFiles.HashValid(hash))return false;
            if(name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
                return plugins.Any(p=>p.Package&&p.PackageRecordId==candidate.Record.Id&&p.PackageSourceHash!=null&&p.PackageSourceHash.Equals(hash,StringComparison.OrdinalIgnoreCase));
            if(candidate.Standalone)
            {
                string target=candidate.Target.Length>0?StandaloneBundleUpdates.RelativeTarget(pluginRoot,candidate.Target):"";
                return bundles.Any(p=>p.NewHash.Equals(hash,StringComparison.OrdinalIgnoreCase)&&(target.Length>0?p.Target.Equals(target,StringComparison.OrdinalIgnoreCase):p.OldHash.Length==0&&Path.GetFileName(p.Target).Equals(name,StringComparison.OrdinalIgnoreCase)));
            }
            string expected=candidate.Target.Length>0?ModUpdateFiles.RelativeTarget(pluginRoot,candidate.Target):"";
            return plugins.Any(p=>p.NewHash.Equals(hash,StringComparison.OrdinalIgnoreCase)&&(expected.Length>0?p.Target.Equals(expected,StringComparison.OrdinalIgnoreCase):p.Install&&NommMetadata.Match(new[]{candidate.Record},p.Plugin,"","")?.Id==candidate.Record.Id));
        }
        // Only a completed receipt plus matching installed bytes proves that a
        // ZIP release is already present. Archive hashes cannot be compared to
        // the extracted DLL or bundle individually.
        internal static PendingModUpdate? InstalledPackage(ServerDownloadCandidate candidate,string updateRoot,string pluginRoot)
        {
            var releases=candidate.MatchingArtifacts();
            if(releases.Length!=1||!(releases[0].FileName??"").EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||!(releases[0].Hash??"").StartsWith("sha256:",StringComparison.OrdinalIgnoreCase))return null;
            string hash=releases[0].Hash.Substring(7);if(!ModUpdateFiles.HashValid(hash))return null;
            foreach(string job in ModUpdateFiles.Jobs(updateRoot).OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                try
                {
                    string path=ModUpdateFiles.Within(job,"plan.json");if(!File.Exists(path))continue;
                    var plan=UpdateJson.ReadFile<PendingModUpdate>(path);plan.Validate();
                    if(!plan.Package||plan.State!="complete"||plan.PackageRecordId!=candidate.Record.Id||!string.Equals(plan.PackageSourceHash,hash,StringComparison.OrdinalIgnoreCase))continue;
                    string target=ModUpdateFiles.Within(pluginRoot,plan.Target);
                    if(candidate.Target.Length>0&&!Path.GetFullPath(candidate.Target).Equals(target,StringComparison.OrdinalIgnoreCase))continue;
                    if(!File.Exists(target)||!ModUpdateFiles.Hash(target).Equals(plan.NewHash,StringComparison.OrdinalIgnoreCase))continue;
                    if(plan.PackageFiles!.Any(f=>f.Remove?File.Exists(ModUpdateFiles.Within(pluginRoot,f.Target(plan.Target))):!File.Exists(ModUpdateFiles.Within(pluginRoot,f.Target(plan.Target)))||!ModUpdateFiles.Hash(ModUpdateFiles.Within(pluginRoot,f.Target(plan.Target))).Equals(f.NewHash,StringComparison.OrdinalIgnoreCase)))continue;
                    plan.Job=job;return plan;
                }
                catch(Exception ex)when(ex is IOException||ex is UnauthorizedAccessException||ex is InvalidDataException||ex is ArgumentException){}
            }
            return null;
        }
        internal static NommRecord? MatchReportedContent(ModEntry content,ModEntry[] entries,NommRecord[] records)
        {
            var direct=NommMetadata.Match(records,content.WrapperPlugin.Length>0?content.WrapperPlugin:content.Id,"",content.WrapperPlugin.Length>0||content.Path.StartsWith("resource:",StringComparison.Ordinal)?"":Path.GetFileName(content.Path));
            if(direct!=null||content.WrapperPath.Length==0)return direct;
            // Multi-plugin assemblies have no single wrapper GUID. Accept a
            // package only if every observed owner maps to the same registry ID.
            var owners=entries.Where(e=>!e.Content&&!e.ExternalDisabled&&e.Path.Equals(content.WrapperPath,StringComparison.OrdinalIgnoreCase)).ToArray();
            var names=owners.Select(e=>NommMetadata.Match(records,e.Id,"","")?.Id??"").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return owners.Length>0&&names.Length==1&&names[0].Length>0?records.SingleOrDefault(r=>r.Id==names[0]):null;
        }
        internal static NosmrSetupPlan ResolveReported(NosmrReport.Result report,IEnumerable<ModEntry> inventory,IEnumerable<NommRecord> registry)
        {
            var plan=new NosmrSetupPlan();
            if(report==null||!report.Verified){plan.Manual.Add("The host's reported mod list could not be verified. Refresh and try again.");return plan;}
            var entries=(inventory??Array.Empty<ModEntry>()).ToArray();var records=(registry??Array.Empty<NommRecord>()).Where(r=>r!=null).ToArray();
            var wanted=new HashSet<string>(report.Entries.Where(e=>e.Resolved).Select(e=>e.Id),StringComparer.OrdinalIgnoreCase);
            foreach(var content in entries.Where(e=>e.Content&&!e.ExternalDisabled))
            {
                var record=MatchReportedContent(content,entries,records);
                if(record==null){if(content.Enabled)plan.UnknownContent.Add(content.Name);continue;}
                if(!wanted.Contains(record.Id))plan.DisableContent.Add(content.Id);
            }
            foreach(var reported in report.Entries.Where(e=>e.Resolved))
            {
                var match=records.Where(r=>r.Id==reported.Id).Take(2).ToArray();if(match.Length!=1)continue;
                var record=match[0];var artifacts=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Version==reported.Version).Take(2).ToArray();
                var local=entries.Where(e=>e.Content&&!e.ExternalDisabled&&MatchReportedContent(e,entries,records)?.Id==record.Id).Take(2).ToArray();
                bool standalone=artifacts.Length==1&&(artifacts[0].FileName??"").EndsWith(".nobp",StringComparison.OrdinalIgnoreCase);
                if(!standalone&&local.Length==0&&!IsReportedGameplay(record))continue;
                string label=Diagnostics.Clean(record.Name)+" "+Diagnostics.Clean(reported.Version);
                if(local.Length>1){plan.Manual.Add(label+": multiple installed copies. Check them manually.");continue;}
                if(artifacts.Length!=1||!Usable(record,artifacts[0])){plan.Manual.Add(label+": no unique checksummed release. Visit GitHub.");continue;}
                ModEntry? plugin=null;
                try
                {
                    if(record.Id.Equals("bote",StringComparison.OrdinalIgnoreCase))
                    {
                        var owners=entries.Where(e=>!e.Content&&!e.ExternalDisabled&&(e.Id=="com.minec.bote"||e.Id=="com.minec.bote.inputs")).ToArray();
                        if(owners.Length>0&&(owners.Length!=2||owners.Select(e=>e.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1))throw new InvalidDataException("The installed BOTE package is ambiguous.");
                        plugin=owners.SingleOrDefault(e=>e.Id=="com.minec.bote");
                    }
                    else plugin=NommMetadata.InstalledPluginForRecord(records,entries,record.Id);
                }
                catch(InvalidDataException ex){plan.Manual.Add(label+": "+ex.Message);continue;}
                string path=local.Length==1?(local[0].WrapperPath.Length>0?local[0].WrapperPath:local[0].Path):plugin?.Path??"";
                if(path.Length>0&&File.Exists(path)&&artifacts[0].Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)&&ModUpdateFiles.Hash(path).Equals(artifacts[0].Hash.Substring(7),StringComparison.OrdinalIgnoreCase))continue;
                // Version labels are a hint only; the host has not provided a
                // digest. An equal version can already be a modified file.
                if(local.Length==1&&ModReleaseVersion.Compare(local[0].Version,reported.Version)==0)continue;
                if(standalone&&local.Length==1&&local[0].WrapperPath.Length>0){plan.Manual.Add(label+": installed inside a plugin; update it manually.");continue;}
                if(!standalone&&local.Length==1&&local[0].WrapperPlugin.Length>0&&(plugin==null||plugin.Id!=local[0].WrapperPlugin)){plan.Manual.Add(label+": plugin owner is ambiguous.");continue;}
                plan.Candidates.Add(new ServerDownloadCandidate {ContentId=local.Length==1?local[0].Id:record.Id,Version=reported.Version,Record=record,OwnerPlugin=plugin?.Id??"",Target=standalone?(local.Length==1?local[0].Path:""):(plugin?.Path??""),Standalone=standalone,AllowMulti=record.Id.Equals("bote",StringComparison.OrdinalIgnoreCase)});
            }
            return plan;
        }
        // Inspected MC260 1.2.1 declares aryx_cargoplane1 in its embedded
        // Blueprinter manifest. The older content identifier was different.
        // Pin the published package digest so a later release is never inferred.
        private const string CargoHash="01350ee52f3313e782490368d9915f7fe59e4c34ddfa220f454fbd7f3a22104f";
        internal static ServerDownloadPlan Resolve(GameplayFingerprintSnapshot remote,GameplayFingerprintSnapshot available,
            IEnumerable<ModEntry> inventory,IEnumerable<NommRecord> registry,NosmrReport.Result? host,bool includeSameVersionCandidates=false)
        {
            var result=new ServerDownloadPlan();
            if(!GameplayFingerprintPolicy.Normalize(remote,out var wanted,out var error)||!wanted.Ready||!wanted.Complete)
            { result.Manual.Add("Server content information is unavailable: "+error);return result; }
            if(!GameplayFingerprintPolicy.Normalize(available,out var present,out error)||!present.Ready||!present.Complete||present.ContentSetHash.Length>0)
            { result.Manual.Add("Installed content information is unavailable: "+error);return result; }
            var entries=(inventory??Array.Empty<ModEntry>()).ToArray();var records=(registry??Array.Empty<NommRecord>()).Where(r=>r!=null).ToArray();
            var actual=present.Records.Where(r=>r.Kind=="content").ToDictionary(r=>r.Id,StringComparer.Ordinal);
            var needed=GameplayFingerprintPolicy.SelectedContent(wanted);
            var full=wanted.Records.Where(r=>r.Kind=="content").ToDictionary(r=>r.Id,StringComparer.Ordinal);
            foreach(var selection in needed)
            {
                bool installed=actual.TryGetValue(selection.Id,out var seen);
                bool same=installed&&seen!.Version==selection.Version&&(!full.TryGetValue(selection.Id,out var required)||required.Hash==seen.Hash);
                if(same&&!includeSameVersionCandidates)continue;
                string label=Diagnostics.Clean(selection.Id)+" "+Diagnostics.Clean(selection.Version);
                var local=entries.Where(e=>e.Content&&e.Id==selection.Id&&!e.ExternalDisabled).Take(2).ToArray();
                if(local.Length>1){if(!same)result.Manual.Add(label+": more than one installed copy was found.");continue;}
                NommRecord? record=null;
                if(local.Length==1&&local[0].WrapperPlugin.Length>0)
                    record=NommMetadata.Match(records,local[0].WrapperPlugin,"","");
                if(record==null)
                {
                    record=NommMetadata.Match(records,selection.Id,"","");
                    var direct=record==null?records.Where(r=>r.Id.Equals(selection.Id,StringComparison.OrdinalIgnoreCase)).Take(2).ToArray():Array.Empty<NommRecord>();
                    if(direct.Length==1)record=direct[0];
                }
                if(record==null&&selection.Id=="aryx_cargoplane1"&&selection.Version=="1.2.1")
                {
                    var pinned=records.Where(r=>r.Id=="aryx.mc260"&&(r.Artifacts??Array.Empty<NommArtifact>()).Any(a=>a!=null&&a.Version=="1.2.1"&&string.Equals(a.Hash,"sha256:"+CargoHash,StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
                    if(pinned.Length==1)record=pinned[0];
                }
                if(record==null){if(!same)result.Manual.Add(label+": no verified download identity. Ask the host for the mod or its GitHub link.");continue;}
                var artifacts=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Version==selection.Version).Take(2).ToArray();
                // Some releases tag their GitHub package differently from the
                // embedded mod version. A unique filename ending in the exact
                // content version is a download hint only. Full-server proof
                // still occurs before any file can be staged or installed.
                if(artifacts.Length!=1||!Usable(record,artifacts[0]))
                    artifacts=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&ModReleaseVersion.Compare(a.Version,a.Version)==0&&Usable(record,a)&&FilenameVersion(a.FileName,selection.Version)).Take(2).ToArray();
                if(artifacts.Length!=1||!Usable(record,artifacts[0]))
                {if(!same)result.Manual.Add(label+": this exact release has no unique supported, checksummed download. Visit GitHub.");continue;}
                var artifact=artifacts[0];
                if(host!=null&&host.Verified)
                {
                    var reports=host.Entries.Where(e=>e.Resolved&&e.Id==record.Id).Take(2).ToArray();
                    if(reports.Length!=1||(reports[0].Version!=selection.Version&&reports[0].Version!=artifact.Version))
                    {if(!same)result.Manual.Add(label+": the host's NOSMR package list differs from its loaded content. Send the report to the host.");continue;}
                }
                // A direct DLL or bundle with the installed SHA cannot improve the set.
                // ZIP hashes describe the archive, so compare its extracted payload later.
                if(same&&!artifact.FileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)&&artifact.Hash.Substring(7).Equals(seen!.Hash,StringComparison.OrdinalIgnoreCase))continue;
                bool standalone=artifact.FileName.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase);
                string owner="",target="";
                if(standalone)
                {
                    if(local.Length==1)
                    {
                        if(local[0].WrapperPath.Length>0||local[0].Path.StartsWith("resource:",StringComparison.Ordinal))
                        {if(!same)result.Manual.Add(label+": the installed content is embedded in a different package. Update it manually.");continue;}
                        target=local[0].Path;
                    }
                }
                else
                {
                    ModEntry? wrapper;
                    try{wrapper=NommMetadata.InstalledPluginForRecord(records,entries,record.Id);}
                    catch(InvalidDataException){if(!same)result.Manual.Add(label+": more than one installed plugin claims this package.");continue;}
                    if(local.Length==1&&local[0].WrapperPlugin.Length>0&&(wrapper==null||wrapper.Id!=local[0].WrapperPlugin))
                    {if(!same)result.Manual.Add(label+": the installed plugin owner could not be verified.");continue;}
                    if(wrapper!=null){owner=wrapper.Id;target=wrapper.Path;}
                }
                result.Candidates.Add(new ServerDownloadCandidate {ContentId=selection.Id,Version=selection.Version,ReleaseVersion=artifact.Version,ArtifactName=artifact.FileName,Record=record,OwnerPlugin=owner,Target=target,Standalone=standalone});
            }
            if(result.Candidates.GroupBy(c=>c.Record.Id,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))
            {result.Candidates.Clear();result.Manual.Add("A package claims several server contents. Install it manually so its complete contents can be checked.");}
            if(result.Candidates.Count==0&&result.Manual.Count==0&&wanted.ContentSetHash.Length>0)
            {
                var selected=needed.Where(x=>actual.ContainsKey(x.Id)&&actual[x.Id].Version==x.Version).Select(x=>actual[x.Id]).ToArray();
                if(selected.Length==needed.Length&&GameplayFingerprintPolicy.ContentHash(selected)!=wanted.ContentSetHash)
                    result.Manual.Add("Versions match, but the selected content files differ. Ask the host for the exact files.");
            }
            return result;
        }
        private static bool FilenameVersion(string name,string version)
        {
            if(string.IsNullOrEmpty(name)||string.IsNullOrEmpty(version)||ModReleaseVersion.Compare(version,version)!=0)return false;
            string stem=Path.GetFileNameWithoutExtension(name);
            if(!stem.EndsWith(version,StringComparison.OrdinalIgnoreCase))return false;
            int start=stem.Length-version.Length;
            if(start==0)return true;
            char before=stem[start-1];
            if(!char.IsLetterOrDigit(before))return true;
            return (before=='v'||before=='V')&&(start==1||!char.IsLetterOrDigit(stem[start-2]));
        }
        private static bool Usable(NommRecord record,NommArtifact artifact)
        {
            string repo=(record.Owner??"")+"/"+(record.Repository??"");string url=string.IsNullOrEmpty(artifact.DownloadUrl)?artifact.DownloadURL:artifact.DownloadUrl;
            if(!GitHubRepository.Valid(repo)||!GitHubRepository.DownloadUrl(url,repo)||string.IsNullOrWhiteSpace(artifact.FileName)||Path.GetFileName(artifact.FileName)!=artifact.FileName)return false;
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||!Uri.UnescapeDataString(uri.Segments.Last()).Equals(artifact.FileName,StringComparison.Ordinal))return false;
            return (artifact.FileName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||artifact.FileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||artifact.FileName.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))
                &&artifact.Hash!=null&&artifact.Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)&&ModUpdateFiles.HashValid(artifact.Hash.Substring(7));
        }
    }
}



