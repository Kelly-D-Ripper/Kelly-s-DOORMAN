using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace KellysJOINCHECK
{
    internal static class SelfUpdateRelease
    {
        internal const string Repository="Kelly-D-Ripper/Kelly-s-DOORMAN",Installed="1.4.0-rc.36";
        // Historical RC tags append a build label, which is not the RC number.
        internal static string Comparable(string? tag)
        {
            if(tag==null||tag.Length>128)return "";
            var m=Regex.Match(tag,@"^v?(\d+\.\d+\.\d+)(?:-rc\.(\d+)(?:-[A-Za-z0-9.-]+)?)?$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
            return !m.Success?"":m.Groups[1].Value+(m.Groups[2].Success?"-rc."+m.Groups[2].Value:"");
        }
        internal static GitHubRelease? Choose(GitHubRelease[] releases,bool previews)
        {
            GitHubRelease? best=null;string version="";
            foreach(var r in (releases??Array.Empty<GitHubRelease>()).Take(100))
            {
                if(r==null||r.Draft||r.Prerelease&&!previews||!GitHubRepository.FromUrl(r.Url).Equals(Repository,StringComparison.OrdinalIgnoreCase)||(r.Assets?.Length??0)>128)continue;
                string next=Comparable(r.Tag);if(next.Length==0||ModReleaseVersion.Compare(next,Installed)<=0)continue;
                if(best==null||ModReleaseVersion.Compare(next,version)>0){best=r;version=next;}
            }
            return best;
        }
        internal static GitHubAsset? Asset(GitHubRelease release,bool server)
        {
            // A FULL package is also safe for client-only installs: package
            // validation reads only the client and never installs its server.
            // Prefer CLIENT when offered, but many releases contain only FULL.
            foreach(string suffix in server?new[]{"-FULL.zip"}:new[]{"-CLIENT.zip","-FULL.zip"})
            {
                var candidates=(release.Assets??Array.Empty<GitHubAsset>()).Where(a=>a!=null&&a.State=="uploaded"&&a.Size>0&&a.Size<=SelfUpdatePackage.MaxArchiveBytes&&a.Name.StartsWith("KellysDOORMAN-",StringComparison.OrdinalIgnoreCase)&&a.Name.EndsWith(suffix,StringComparison.OrdinalIgnoreCase)&&Comparable(a.Name.Substring("KellysDOORMAN-".Length,a.Name.Length-"KellysDOORMAN-".Length-suffix.Length))==Comparable(release.Tag)&&Comparable(release.Tag).Length>0&&GitHubRepository.DownloadUrl(a.Url,Repository)&&a.Digest!=null&&a.Digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)&&ModUpdateFiles.HashValid(a.Digest.Substring(7))).Take(2).ToArray();
                if(candidates.Length==1)return candidates[0];
                if(candidates.Length>1)return null;
            }
            return null;
        }
        internal static async Task<GitHubRelease?> Check(CancellationToken token,HttpMessageHandler? handler=null)
        {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
            using(var http=new HttpClient(handler??new HttpClientHandler { AllowAutoRedirect=false }))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));http.Timeout=TimeSpan.FromSeconds(15);http.DefaultRequestHeaders.UserAgent.ParseAdd("Kellys-DOORMAN/"+Installed);
                using(var response=await http.GetAsync("https://api.github.com/repos/"+Repository+"/releases?per_page=100",HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>1024*1024)throw new InvalidDataException("The GitHub release list is too large.");
                    using(var body=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var memory=new MemoryStream())
                    {
                        byte[] b=new byte[16384];int n;while((n=await body.ReadAsync(b,0,b.Length,timeout.Token).ConfigureAwait(false))>0){if(memory.Length+n>1024*1024)throw new InvalidDataException("The GitHub release list is too large.");memory.Write(b,0,n);}
                        memory.Position=0;var releases=(GitHubRelease[])new DataContractJsonSerializer(typeof(GitHubRelease[]),new DataContractJsonSerializerSettings{MaxItemsInObjectGraph=16384}).ReadObject(memory)!;
                        return Choose(releases,Installed.IndexOf("-rc.",StringComparison.OrdinalIgnoreCase)>=0);
                    }
                }
            }
        }
    }
}
