using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KellysJOINCHECK
{
    internal static class ServerMapResolver
    {
        // NOMNOM explicitly namespaces NOCustomMaps entries. Names alone are
        // not sufficient to select a publisher or authorize an installation.
        internal static NommRecord? Find(string mapId,IEnumerable<NommRecord> records)
        {
            string norm=Normalize(mapId);
            if(norm.Length<2)return null;
            var candidates=(records??Array.Empty<NommRecord>()).Where(r=>r!=null&&r.Id!=null
                &&r.Id.StartsWith("NOCustomMaps.",StringComparison.OrdinalIgnoreCase)
                &&Normalize(r.Id.Substring("NOCustomMaps.".Length))==norm
                &&GitHubRepository.Valid((r.Owner??"")+"/"+(r.Repository??""))).Take(2).ToArray();
            return candidates.Length==1?candidates[0]:null;
        }
        // A catalogue name can include a suffix that is absent from the game's
        // map ID. Such candidates must prove the *complete* key in their release.
        internal static NommRecord[] Candidates(string mapId,IEnumerable<NommRecord> records)
        {
            var all=(records??Array.Empty<NommRecord>()).Where(r=>r!=null&&r.Id!=null
                &&r.Id.StartsWith("NOCustomMaps.",StringComparison.OrdinalIgnoreCase)
                &&GitHubRepository.Valid((r.Owner??"")+"/"+(r.Repository??""))).ToArray();
            string norm=Normalize(mapId);if(norm.Length<2)return Array.Empty<NommRecord>();
            var exact=all.Where(r=>Normalize(r.Id.Substring("NOCustomMaps.".Length))==norm).Take(2).ToArray();
            if(exact.Length>1)return Array.Empty<NommRecord>();
            if(exact.Length==1)return exact;
            if(norm.Length<4)return Array.Empty<NommRecord>();
            var possible=all.Where(r=>Normalize(r.Id.Substring("NOCustomMaps.".Length)).StartsWith(norm,StringComparison.Ordinal)).Take(9).ToArray();
            return possible.Length>8?Array.Empty<NommRecord>():possible;
        }
        private static string Normalize(string text)=>new string((text??"").Where(c=>c!='-').Select(char.ToLowerInvariant).ToArray());
        internal static bool ReleaseNamesBuild(GitHubRelease release,string build)
        {
            if(release==null||release.Draft||release.Body==null||release.Body.Length>262144||build==null||build.Length!=8)return false;
            var values=Regex.Matches(release.Body,@"(?im)^\s*map\s+hash8\s*:\s*`?([0-9a-f]{8})`?\s*$",RegexOptions.CultureInvariant)
                .Cast<Match>().Select(m=>m.Groups[1].Value.ToLowerInvariant()).Distinct().Take(2).ToArray();
            return values.Length==1&&values[0]==build;
        }
        internal static bool ReleaseNamesKey(GitHubRelease release,string key)
        {
            if(release==null||release.Draft||release.Body==null||release.Body.Length>262144||!MapJoinDiagnostic.TryCustomKey(key,out _,out _))return false;
            var values=Regex.Matches(release.Body,@"(?i)\bmap\s+key\s*[:=]?\s*`?(cm\.[a-z0-9-]+\.[0-9a-f]{8})`?",RegexOptions.CultureInvariant)
                .Cast<Match>().Select(m=>m.Groups[1].Value.ToLowerInvariant()).Distinct().Take(2).ToArray();
            return values.Length==1&&values[0]==key;
        }
    }
}
