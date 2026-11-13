using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KellysJOINCHECK
{
    // NOSMR reports a host-supplied NOMM pack, not an inventory of loaded files.
    // Its short identifiers are hints; they never authorize mod changes or joins.
    internal static class NosmrReport
    {
        internal const int MaxMods=256;
        internal sealed class Entry { internal string Id="", Version="", Name=""; internal bool Required, Resolved; }
        internal sealed class Result { internal readonly List<Entry> Entries=new List<Entry>(); internal bool Verified; }
        // Enables an explicit native join attempt only; it never certifies the
        // server's files or overrides the game's own compatibility checks.
        internal static bool CanAttemptJoin(bool exactAdvertised,bool reportVerified,bool busy,bool restartArmed,bool updatesPending,string hostWire,string localWire)
        {
            if(exactAdvertised||!reportVerified||busy||restartArmed||updatesPending)return false;
            var host=Signature.Parse(hostWire);var local=Signature.Parse(localWire);
            return host.KnownOpaqueHash&&Signature.KnownGame(host.Game)&&Signature.KnownGame(local.Game)&&host.Game==local.Game;
        }

        internal static string SupportStatus(string version,string catalogState,Result? report)
        {
            if(version.Length==0)return "Not seen in the current server metadata. The server query may still be pending.";
            if(version!="2")return "Unsupported NOSMR protocol: "+Diagnostics.Clean(version)+".";
            if(catalogState=="unavailable")return "Host modpack advertised; the package catalogue is unavailable.";
            if(catalogState=="failed"||catalogState=="cancelled")return "Host modpack advertised; the package catalogue lookup "+catalogState+".";
            if(catalogState!="ready")return "Host modpack advertised; the package catalogue lookup is pending.";
            if(report==null)return "Host modpack metadata is incomplete or invalid. The server query may still be pending.";
            if(!report.Verified)return "Host modpack received, but some package identities could not be verified.";
            return "Host modpack verified. This is a reported list, not proof of the server's loaded files.";
        }

        internal static bool IsKey(string key)
        {
            if(key=="nomm_v"||key=="nomm_c")return true;
            if(!key.StartsWith("nomm_",StringComparison.Ordinal)||key.Length<7||key.Length>9)return false;
            if(key[5]!='d'&&key[5]!='h'&&key[5]!='r')return false;
            string digits=key.Substring(6);
            return int.TryParse(digits,NumberStyles.None,CultureInfo.InvariantCulture,out var index)&&index>=0&&index<MaxMods&&digits==index.ToString(CultureInfo.InvariantCulture);
        }
        internal static string Prefix(string id,string version)
        {
            using(var sha=SHA256.Create())
            { var hash=sha.ComputeHash(Encoding.UTF8.GetBytes(id+"|"+version));return BitConverter.ToString(hash,0,3).Replace("-","").ToLowerInvariant(); }
        }
        private static string Escape(string text)=>text.Replace("\\","\\\\").Replace("\"","\\\"").Replace("\n","\\n").Replace("\r","\\r").Replace("\t","\\t");
        private static string Json(IEnumerable<Entry> entries)=>"["+string.Join(",",entries.Select(e=>"{\"id\":\""+Escape(e.Id)+"\""+(e.Version.Length==0?"":",\"version\":\""+Escape(e.Version)+"\"")+"}"))+"]";
        internal sealed class Index
        {
            internal readonly Dictionary<string,List<Entry>> Lookup=new Dictionary<string,List<Entry>>(StringComparer.Ordinal);
            internal readonly Dictionary<string,NommRecord> Records=new Dictionary<string,NommRecord>(StringComparer.Ordinal);
            internal Index(IEnumerable<NommRecord> records)
            {
            foreach(var record in records??Array.Empty<NommRecord>())
            {
                if(!string.IsNullOrWhiteSpace(record.Id)&&!Records.ContainsKey(record.Id))Records.Add(record.Id,record);
                foreach(var artifact in record.Artifacts??Array.Empty<NommArtifact>())
                {
                    if(artifact==null || string.IsNullOrWhiteSpace(record.Id) || string.IsNullOrWhiteSpace(artifact.Version))continue;
                    string key=Prefix(record.Id,artifact.Version);
                    if(!Lookup.TryGetValue(key,out var matches))Lookup[key]=matches=new List<Entry>();
                    if(!matches.Any(m=>m.Id==record.Id&&m.Version==artifact.Version))matches.Add(new Entry { Id=record.Id,Version=artifact.Version,Name=record.Name,Resolved=true });
                }
            }
            }
        }
        internal static Result? Read(Func<string,string> read,Index index)
        {
            if(read("nomm_v")!="2" || !int.TryParse(read("nomm_c"),NumberStyles.None,CultureInfo.InvariantCulture,out var count) || count<1 || count>MaxMods)return null;
            string advertisedHash=read("nomm_h0")+read("nomm_h1");
            if(advertisedHash.Length!=71 || !advertisedHash.StartsWith("sha256:",StringComparison.Ordinal) || advertisedHash.Substring(7).Any(c=>!Uri.IsHexDigit(c)))return null;
            var required=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var requiredText=new StringBuilder();
            for(int i=0;i<MaxMods;i++){string part=read("nomm_r"+i);if(part.Length==0)break;if(part.Length>64)return null;requiredText.Append(part);}
            foreach(var id in requiredText.ToString().Split(';'))if(id.Length>0)required.Add(id);
            var result=new Result();
            for(int i=0;i<count;i++)
            {
                string raw=read("nomm_d"+i); if(raw.Length==0 || raw.Length>96 || raw.Any(char.IsControl))return null;
                Entry entry;
                if(raw.EndsWith("UNK",StringComparison.Ordinal) && raw.Length>3)
                { string id=raw.Substring(0,raw.Length-3);index.Records.TryGetValue(id,out var record);entry=new Entry { Id=id,Name=record?.Name??id,Resolved=true }; }
                else if(raw.Length==6 && raw.All(Uri.IsHexDigit) && index.Lookup.TryGetValue(raw.ToLowerInvariant(),out var options) && options.Count==1)
                {var m=options[0];entry=new Entry { Id=m.Id,Name=m.Name,Version=m.Version,Resolved=true };}
                else entry=new Entry { Name="Unidentified package ("+raw+")" };
                entry.Required=entry.Resolved&&required.Contains(entry.Id);result.Entries.Add(entry);
            }
            if(result.Entries.All(e=>e.Resolved))
            {
                using(var sha=SHA256.Create())
                { string hash="sha256:"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json(result.Entries)))).Replace("-","").ToLowerInvariant();if(!hash.Equals(advertisedHash,StringComparison.OrdinalIgnoreCase))return null; }
                result.Verified=true;
            }
            return result;
        }
    }
}
