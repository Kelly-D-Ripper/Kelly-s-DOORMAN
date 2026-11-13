using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace KellysJOINCHECK
{
    // Shared with the startup helper. These declarations describe identities;
    // they never execute code, download files or choose an installation path.
    [DataContract]
    internal sealed class ModRequisite
    {
        [DataMember(Name="plugin",EmitDefaultValue=false)] public string Plugin="";
        [DataMember(Name="bundle",EmitDefaultValue=false)] public string Bundle="";
        [DataMember(Name="name",EmitDefaultValue=false)] public string Name="";
        [DataMember(Name="minimumVersion",EmitDefaultValue=false)] public string MinimumVersion="";
        [DataMember(Name="repository",EmitDefaultValue=false)] public string Repository="";
        [DataMember(Name="asset",EmitDefaultValue=false)] public string Asset="";
        internal const int Maximum=16;
        internal bool Content => Bundle.Length>0;
        internal string Id => Content?Bundle:Plugin;
        internal string Key => (Content?"bundle:":"plugin:")+Id;
        internal string DisplayName => Name.Length>0?Name:Id;

        [OnDeserializing]
        private void Deserializing(StreamingContext context)
        { Plugin="";Bundle="";Name="";MinimumVersion="";Repository="";Asset=""; }

        internal void Validate()
        {
            if(!Text(Plugin,256)||!Text(Bundle,256)||(Plugin.Length==0)==(Bundle.Length==0))
                throw new InvalidDataException("A prerequisite needs exactly one plugin or bundle identifier.");
            if(!Text(Name,120)||!Text(MinimumVersion,128)||!Text(Repository,140)||!Text(Asset,160))
                throw new InvalidDataException("A prerequisite contains invalid or oversized text.");
            if(MinimumVersion.Length>0&&ModReleaseVersion.Compare(MinimumVersion,MinimumVersion)==null)
                throw new InvalidDataException("A prerequisite minimumVersion must be a numeric release version.");
            if(Repository.Length>0&&!GitHubRepository.Valid(Repository))
                throw new InvalidDataException("A prerequisite repository must be GitHub owner/repository.");
            if(Asset.Length>0&&(Repository.Length==0||Asset.Any(c=>c=='/'||c=='\\'||c==':')))
                throw new InvalidDataException("A prerequisite asset needs a repository and a release filename or pattern.");
        }

        internal ModRequisite Copy()=>new ModRequisite { Plugin=Plugin,Bundle=Bundle,Name=Name,MinimumVersion=MinimumVersion,Repository=Repository,Asset=Asset };

        internal static ModRequisite[] ValidateAll(ModRequisite[]? values)
        {
            if(values==null)return Array.Empty<ModRequisite>();
            if(values.Length>Maximum)throw new InvalidDataException("A preview has too many prerequisites.");
            var identities=new HashSet<string>(StringComparer.Ordinal);
            foreach(var value in values)
            {
                if(value==null)throw new InvalidDataException("A prerequisite declaration cannot be empty.");
                value.Validate();
                if(!identities.Add(value.Key))throw new InvalidDataException("A prerequisite identity is declared more than once.");
            }
            return values;
        }

        private static bool Text(string? value,int maximum)=>value!=null&&value.Length<=maximum&&value==value.Trim()&&!value.Any(char.IsControl);
    }
}
