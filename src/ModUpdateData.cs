using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class ModUpdateSource
    {
        [DataMember(Name="repository")] public string Repository="";
        [DataMember(Name="asset",EmitDefaultValue=false)] public string Asset="";
        internal void Validate()
        {
            Asset=Asset??"";
            if(!GitHubRepository.Valid(Repository) || Asset.Length>160 || Asset.Any(c=>char.IsControl(c)||c=='/'||c=='\\'||c==':')) throw new InvalidDataException("Invalid GitHub update source.");
        }
    }
    internal static class GitHubRepository
    {
        private static readonly Regex Pattern=new Regex(@"^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$",RegexOptions.CultureInvariant);
        internal static bool Valid(string? repository)=>repository!=null&&Pattern.IsMatch(repository)&&repository.Split('/')[1]!="."&&repository.Split('/')[1]!="..";
        internal static string FromUrl(string? url)
        {
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||!uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||!uri.IsDefaultPort||uri.UserInfo.Length!=0) return "";
            var parts=uri.AbsolutePath.Trim('/').Split('/'); if(parts.Length<2) return "";
            string repository=parts[0]+"/"+parts[1]; if(repository.EndsWith(".git",StringComparison.OrdinalIgnoreCase)) repository=repository.Substring(0,repository.Length-4);
            return Valid(repository)?repository:"";
        }
        internal static bool DownloadUrl(string url,string repository)
        {
            return Valid(repository)&&Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)&&uri.AbsolutePath.StartsWith("/"+repository+"/releases/download/",StringComparison.OrdinalIgnoreCase)&&uri.Query.Length==0;
        }
        internal static bool RedirectUrl(Uri uri)=>uri.Scheme=="https"&&uri.IsDefaultPort&&uri.UserInfo.Length==0&&(uri.Host.Equals("github.com",StringComparison.OrdinalIgnoreCase)||uri.Host.Equals("release-assets.githubusercontent.com",StringComparison.OrdinalIgnoreCase)||uri.Host.Equals("objects.githubusercontent.com",StringComparison.OrdinalIgnoreCase));
        internal static bool AssetMatches(string name,string pattern)
        {
            if(pattern.Length==0) return true;
            return Regex.IsMatch(name,"^"+Regex.Escape(pattern).Replace("\\*",".*")+"$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        }
    }
    internal static class ModReleaseVersion
    {
        private static readonly Regex Pattern=new Regex(@"^v?([0-9]+(?:\.[0-9]+){1,3})(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        internal static int? Compare(string? latest,string? installed)
        {
            if(latest==null||installed==null||latest.Length>128||installed.Length>128) return null;
            var a=Pattern.Match(latest); var b=Pattern.Match(installed); if(!a.Success||!b.Success) return null;
            var av=a.Groups[1].Value.Split('.'); var bv=b.Groups[1].Value.Split('.');
            for(int i=0;i<4;i++)
            {
                if(!int.TryParse(i<av.Length?av[i]:"0",out int an)||!int.TryParse(i<bv.Length?bv[i]:"0",out int bn)) return null;
                if(an!=bn) return an.CompareTo(bn);
            }
            string ap=a.Groups[2].Value,bp=b.Groups[2].Value; if(ap==bp) return 0; if(ap.Length==0) return 1; if(bp.Length==0) return -1;
            var ax=ap.Split('.'); var bx=bp.Split('.');
            for(int i=0;i<Math.Min(ax.Length,bx.Length);i++)
            {
                bool ai=int.TryParse(ax[i],out int an), bi=int.TryParse(bx[i],out int bn);
                int order=ai&&bi?an.CompareTo(bn):ai!=bi?(ai?-1:1):string.CompareOrdinal(ax[i],bx[i]); if(order!=0) return order;
            }
            return ax.Length.CompareTo(bx.Length);
        }
    }
    [DataContract]
    internal sealed class GitHubAsset
    {
        [DataMember(Name="name")] public string Name="";
        [DataMember(Name="browser_download_url")] public string Url="";
        [DataMember(Name="size")] public long Size=0;
        [DataMember(Name="digest")] public string? Digest=null;
        [DataMember(Name="state")] public string State="";
    }
    [DataContract]
    internal sealed class GitHubRelease
    {
        [DataMember(Name="tag_name")] public string Tag="";
        [DataMember(Name="html_url")] public string Url="";
        [DataMember(Name="draft")] public bool Draft=false;
        [DataMember(Name="prerelease")] public bool Prerelease=false;
        [DataMember(Name="assets")] public GitHubAsset[] Assets=Array.Empty<GitHubAsset>();
        internal GitHubAsset? SelectAsset(string repository,string pattern)
        {
            var usable=(Assets??Array.Empty<GitHubAsset>()).Where(a=>a!=null&&a.State=="uploaded"&&a.Size>0&&a.Size<=ModUpdateFiles.MaximumBytes&&GitHubRepository.DownloadUrl(a.Url,repository)&&SafeName(a.Name)&&GitHubRepository.AssetMatches(a.Name,pattern)&&(a.Name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||a.Name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
            return usable.Length==1?usable[0]:null;
        }
        private static bool SafeName(string name)=>name!=null&&name.Length>0&&name.Length<=160&&!name.Any(c=>char.IsControl(c)||c=='/'||c=='\\'||c==':');
    }
    internal static class UpdateJson
    {
        internal static T Read<T>(Stream stream)=>(T)new DataContractJsonSerializer(typeof(T),new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=8192 }).ReadObject(stream)!;
        internal static T ReadFile<T>(string path,int maximum=65536)
        {
            if(new FileInfo(path).Length>maximum) throw new InvalidDataException("Update metadata is too large.");
            using(var stream=File.OpenRead(path)) return Read<T>(stream);
        }
        internal static void Save<T>(string path,T value)
        {
            path=Path.GetFullPath(path);string directory=Path.GetDirectoryName(path)!,name=Path.GetFileName(path);
            path=ModUpdateFiles.Within(directory,name);string temp=ModUpdateFiles.Within(directory,name+".tmp");
            Directory.CreateDirectory(directory);
            path=ModUpdateFiles.Within(directory,name);temp=ModUpdateFiles.Within(directory,name+".tmp");
            using(var stream=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)) { new DataContractJsonSerializer(typeof(T)).WriteObject(stream,value); stream.Flush(true); }
            if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
        }
    }
}
