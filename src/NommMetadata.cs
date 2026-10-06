using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace KellysJOINCHECK
{
    [DataContract] internal sealed class NommUrl { [DataMember(Name="name")] public string Name=""; [DataMember(Name="url")] public string Url=""; }
    [DataContract] internal sealed class NommArtifact { [DataMember(Name="fileName")] public string FileName=""; [DataMember(Name="hash")] public string Hash=""; }
    [DataContract] internal sealed class NommRecord
    {
        [DataMember(Name="id")] public string Id="";
        [DataMember(Name="displayName")] public string Name="";
        [DataMember(Name="description")] public string Description="";
        [DataMember(Name="authors")] public string[] Authors=Array.Empty<string>();
        [DataMember(Name="urls")] public NommUrl[] Urls=Array.Empty<NommUrl>();
        [DataMember(Name="githubOwner")] public string Owner="";
        [DataMember(Name="githubRepoName")] public string Repository="";
        [DataMember(Name="imageUrl")] public string Image="";
        [DataMember(Name="artifacts")] public NommArtifact[] Artifacts=Array.Empty<NommArtifact>();
    }
    [DataContract] internal sealed class NommEnvelope { [DataMember(Name="manifest")] public NommRecord[] Records=Array.Empty<NommRecord>(); }
    internal static class NommMetadata
    {
        internal const int MaxBytes=4*1024*1024;
        internal static NommRecord[] Parse(byte[] bytes)
        {
            if(bytes.Length>MaxBytes) throw new InvalidDataException("NOMNOM metadata is too large.");
            string text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF',' ','\r','\n','\t');
            bool array=text.StartsWith("[",StringComparison.Ordinal);
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(text)))
            {
                var serializer=new DataContractJsonSerializer(array?typeof(NommRecord[]):typeof(NommEnvelope),new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=262144 });
                var value=serializer.ReadObject(stream);
                var records=array?(NommRecord[])value!:((NommEnvelope)value!).Records;
                if(records==null||records.Length>2048) throw new InvalidDataException("Unsupported NOMNOM index.");
                return records.Where(r=>r!=null&&!string.IsNullOrWhiteSpace(r.Id)&&r.Id.Length<=256&&!string.IsNullOrWhiteSpace(r.Name)).ToArray();
            }
        }
        // These aliases bridge registry IDs to the inspected installed plugin IDs.
        // Display-name similarity never grants a repository or update identity.
        private static readonly Dictionary<string,string> Aliases=new Dictionary<string,string>(StringComparer.Ordinal)
        {
            {"Aryx_F16M_KingViper","aryx.f16m"},{"Aryx_F22E_StrikeRaptor","aryx.f22e"},{"Aryx_LightFighter1","aryx.f99"},
            {"Aryx_NavalInterceptor1","aryx.fs41"},{"Aryx_MC260_Chimera","aryx.mc260"},{"Aryx_RAH_72_Knockout","aryx.rah72"},
            {"AryxNavalExpansion","aryx.navex"},{"AryxWeaponryExpansion","aryx.weaponpack"},
            {"com.bepis.bepinex.configurationmanager","BepInEx.ConfigurationManager"},{"com.defensiveautotarget","DefensiveAutoTarget"},
            {"aryx_propattacker1","aryx.oa27"},{"aryx_mig-15","aryx.mig15"},{"1509_palafighter1","1509_PalaFighter1"}
        };
        internal static NommRecord? Match(IEnumerable<NommRecord> records,string plugin,string assembly,string filename,string digest="")
        {
            var all=records.ToArray();
            if(digest.Length>0)
            {
                var exact=all.Where(r=>(r.Artifacts??Array.Empty<NommArtifact>()).Any(a=>a!=null&&a.Hash!=null&&a.Hash.Equals("sha256:"+digest,StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
                if(exact.Length==1) return exact[0]; if(exact.Length>1) return null;
            }
            string alias=Aliases.TryGetValue(plugin,out var mapped)?mapped:plugin;
            var ids=all.Where(r=>r.Id==alias||r.Id==plugin).Take(2).ToArray();
            if(ids.Length==1) return ids[0];if(ids.Length>1) return null;
            // A direct release filename is useful for Blueprinter's standalone bundles.
            var files=all.Where(r=>(r.Artifacts??Array.Empty<NommArtifact>()).Any(a=>a!=null&&a.FileName!=null&&a.FileName.Equals(filename,StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
            return files.Length==1?files[0]:null;
        }
        internal static ModManifest Preview(NommRecord record,string bundle,string plugin)
        {
            string repo=(record.Owner??"")+"/"+(record.Repository??"");
            var card=new ModManifest { Bundle=bundle,Plugin=plugin,Name=record.Name,Author=string.Join(", ",(record.Authors??Array.Empty<string>()).Take(8)),Description=record.Description??"",
                Links=(record.Urls??Array.Empty<NommUrl>()).Where(l=>l!=null).Select(l=>new ModLink { Label=l.Name=="info"?"GitHub / info":l.Name,Url=l.Url }).ToArray(),FromNomm=true,RemoteImage=record.Image??"" };
            if(GitHubRepository.Valid(repo)) card.Update=new ModUpdateSource { Repository=repo };
            card.Validate();return card;
        }
    }
}
