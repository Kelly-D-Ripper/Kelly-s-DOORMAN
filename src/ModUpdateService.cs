using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class ReleaseCacheEntry
    {
        [DataMember] public string Repository="", ETag="", Error="";
        [DataMember] public long Expires;
        [DataMember] public GitHubRelease? Release;
    }
    [DataContract]
    internal sealed class ReleaseCache
    {
        [DataMember] public ReleaseCacheEntry[] Entries=Array.Empty<ReleaseCacheEntry>();
    }
    internal sealed class ModUpdateCheck
    {
        internal string Message="";
        internal GitHubRelease? Release;
        internal GitHubAsset? Asset;
        internal bool Newer,Queued;
    }
    internal sealed class ModUpdateService : IDisposable
    {
        internal static readonly TimeSpan CacheLifetime=TimeSpan.FromHours(6),FailureLifetime=TimeSpan.FromMinutes(15);
        private readonly string pluginRoot,updateRoot,cachePath;
        private readonly HttpClient http;
        private readonly SemaphoreSlim requestGate=new SemaphoreSlim(1,1);
        private readonly Dictionary<string,ReleaseCacheEntry> cache=new Dictionary<string,ReleaseCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTimeOffset> now;
        private bool loaded;
        private DateTimeOffset pausedUntil;
        internal ModUpdateService(string pluginRoot,string cacheRoot,HttpMessageHandler? handler=null,Func<DateTimeOffset>? now=null)
        {
            this.pluginRoot=pluginRoot; updateRoot=Path.Combine(cacheRoot,"DOORMAN-updates");cachePath=Path.Combine(cacheRoot,"doorman-releases.json");this.now=now??(()=>DateTimeOffset.UtcNow);
            http=new HttpClient(handler??new HttpClientHandler { AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate });http.Timeout=TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kellys-DOORMAN/1.3.4"); http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
        internal async Task<ModUpdateCheck> CheckAsync(string repository,string pattern,string installed,string target,CancellationToken token)
        {
            if(!GitHubRepository.Valid(repository)) return new ModUpdateCheck { Message="No GitHub repository supplied for this mod." };
            await requestGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                LoadCache();
                var queued=target.Length==0?null:ModUpdateFiles.PendingFor(updateRoot,ModUpdateFiles.RelativeTarget(pluginRoot,target));
                if(queued!=null) return new ModUpdateCheck { Queued=true,Message="Version "+queued.Version+" is queued. Restart Nuclear Option to finish installing it." };
                cache.TryGetValue(repository,out var previous);
                if(previous!=null&&previous.Expires>now().ToUnixTimeSeconds()) return Describe(previous,pattern,installed);
                if(now()<pausedUntil) return new ModUpdateCheck { Message="GitHub asked us to slow down. Try again after "+pausedUntil.ToLocalTime().ToString("t")+"." };
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        using(var request=new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/"+repository+"/releases/latest"))
                        {
                            if(previous?.Release!=null&&previous.ETag.Length>0) request.Headers.TryAddWithoutValidation("If-None-Match",previous.ETag);
                            using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                            {
                                ReleaseCacheEntry entry;
                                if(response.StatusCode==HttpStatusCode.NotModified&&previous?.Release!=null) { entry=previous;entry.Error=""; }
                                else
                                {
                                    if(response.StatusCode==HttpStatusCode.Forbidden||(int)response.StatusCode==429)
                                    {
                                        pausedUntil=now().Add(FailureLifetime);
                                        if(response.Headers.TryGetValues("X-RateLimit-Reset",out var resets)&&long.TryParse(resets.FirstOrDefault(),out long reset))
                                        { try { var at=DateTimeOffset.FromUnixTimeSeconds(reset);if(at>now()&&at<now().AddHours(24)) pausedUntil=at; } catch { } }
                                        if(response.Headers.RetryAfter?.Delta is TimeSpan delay&&delay>TimeSpan.Zero&&delay<TimeSpan.FromHours(24)) pausedUntil=now().Add(delay);
                                        throw new IOException("GitHub is busy or rate limited. Cached checks will keep working.");
                                    }
                                    if(response.StatusCode==HttpStatusCode.NotFound) throw new IOException("No public stable GitHub release found.");
                                    response.EnsureSuccessStatusCode();
                                    using(var body=await response.Content.ReadAsStreamAsync().ConfigureAwait(false)) using(var buffer=new MemoryStream())
                                    {
                                        await CopyBounded(body,buffer,512*1024,timeout.Token).ConfigureAwait(false);
                                        buffer.Position=0; var release=UpdateJson.Read<GitHubRelease>(buffer);
                                        if(release.Draft||release.Prerelease||release.Tag==null||release.Tag.Length>128||!GitHubRepository.FromUrl(release.Url).Equals(repository,StringComparison.OrdinalIgnoreCase)||(release.Assets?.Length??0)>128) throw new InvalidDataException("GitHub returned an unsupported release.");
                                        entry=new ReleaseCacheEntry { Repository=repository,Release=release,ETag=response.Headers.ETag?.ToString()??"" };
                                    }
                                }
                                entry.Expires=now().Add(CacheLifetime).ToUnixTimeSeconds();cache[repository]=entry;SaveCache();return Describe(entry,pattern,installed);
                            }
                        }
                    }
                    catch(OperationCanceledException) when(!token.IsCancellationRequested) { return Failure(repository,"GitHub did not answer. Try this page again later."); }
                    catch(Exception) when(token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                    catch(Exception ex) when(!(ex is OperationCanceledException)) { return Failure(repository,ex is IOException?ex.Message:"Could not check GitHub. Your installed mod is unchanged."); }
                }
            }
            finally { requestGate.Release(); }
        }
        private ModUpdateCheck Failure(string repository,string message)
        {
            var entry=new ReleaseCacheEntry { Repository=repository,Error=message,Expires=now().Add(FailureLifetime).ToUnixTimeSeconds() };cache[repository]=entry;SaveCache();return new ModUpdateCheck { Message=message };
        }
        private static ModUpdateCheck Describe(ReleaseCacheEntry entry,string pattern,string installed)
        {
            if(entry.Error.Length>0||entry.Release==null) return new ModUpdateCheck { Message=entry.Error.Length>0?entry.Error:"Could not check this release." };
            var release=entry.Release; int? compared=ModReleaseVersion.Compare(release.Tag,installed);
            var result=new ModUpdateCheck { Release=release,Asset=release.SelectAsset(entry.Repository,pattern),Newer=compared>0 };
            result.Message=installed.Length==0?"Latest release: "+release.Tag+". Enable this plugin and restart to read its content version.":compared==null?"Installed "+installed+" | Latest "+release.Tag+". These version names need a manual comparison.":compared>0?"Update available: "+installed+" to "+release.Tag+".":compared<0?"Installed "+installed+" is ahead of the latest stable release ("+release.Tag+").":"Up to date: "+installed+".";
            if(result.Newer&&result.Asset==null) result.Message+=" Open the release page to choose its download.";
            return result;
        }
        private void LoadCache()
        {
            if(loaded) return;loaded=true;
            try
            {
                if(!File.Exists(cachePath)) return;
                var disk=UpdateJson.ReadFile<ReleaseCache>(cachePath,512*1024);
                foreach(var entry in (disk.Entries??Array.Empty<ReleaseCacheEntry>()).Take(128))
                    if(entry!=null&&GitHubRepository.Valid(entry.Repository)&&entry.Expires<=now().Add(CacheLifetime).ToUnixTimeSeconds()&&entry.Expires>now().ToUnixTimeSeconds()&&entry.ETag!=null&&entry.ETag.Length<512&&!entry.ETag.Any(char.IsControl)&&entry.Error!=null&&entry.Error.Length<1024)
                        if(entry.Release==null||(!entry.Release.Draft&&!entry.Release.Prerelease&&entry.Release.Tag!=null&&entry.Release.Tag.Length<=128&&GitHubRepository.FromUrl(entry.Release.Url).Equals(entry.Repository,StringComparison.OrdinalIgnoreCase))) cache[entry.Repository]=entry;
            }
            catch { cache.Clear(); }
        }
        private void SaveCache()
        {
            try { UpdateJson.Save(cachePath,new ReleaseCache { Entries=cache.Values.OrderByDescending(e=>e.Expires).Take(128).ToArray() }); } catch { /* A read-only cache must not break the mod menu. */ }
        }
        internal async Task StageAsync(string repository,string plugin,string target,GitHubRelease release,GitHubAsset asset,CancellationToken token,Action<int>? progress=null)
        {
            if(!GitHubRepository.DownloadUrl(asset.Url,repository)||asset.Size<=0||asset.Size>ModUpdateFiles.MaximumBytes||release.Draft||release.Prerelease||!release.Assets.Any(a=>a.Url==asset.Url&&a.Name==asset.Name&&a.Size==asset.Size)) throw new InvalidDataException("Unsupported GitHub download.");
            ModUpdateFiles.RelativeTarget(pluginRoot,target);
            await requestGate.WaitAsync(token).ConfigureAwait(false);
            string? download=null,extracted=null;
            try
            {
                string downloads=ModUpdateFiles.Within(updateRoot,"downloads"); Directory.CreateDirectory(downloads);download=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");
                using(var response=await DownloadResponse(asset.Url,token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    if(response.Content.Headers.ContentLength is long length&&length!=asset.Size) throw new InvalidDataException("The release download size changed.");
                    using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false)) using(var output=new FileStream(download,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {
                        long count=await CopyBounded(input,output,asset.Size,token,progress).ConfigureAwait(false);output.Flush(true);
                        if(count!=asset.Size) throw new InvalidDataException("The mod download was incomplete.");
                    }
                }
                if(!string.IsNullOrEmpty(asset.Digest))
                {
                    if(!asset.Digest!.StartsWith("sha256:",StringComparison.Ordinal)||!ModUpdateFiles.HashValid(asset.Digest.Substring(7))||!ModUpdateFiles.Hash(download).Equals(asset.Digest.Substring(7),StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The download failed GitHub's SHA-256 checksum. Nothing was replaced.");
                }
                string payload=download;
                StagedModSidecar[] previews=Array.Empty<StagedModSidecar>();
                if(asset.Name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
                {
                    extracted=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");previews=ExtractSingleDll(download,extracted,plugin);payload=extracted;
                }
                token.ThrowIfCancellationRequested(); ModUpdateFiles.Stage(updateRoot,pluginRoot,target,payload,plugin,repository,release.Tag,previews);
            }
            finally
            {
                foreach(var temporary in new[]{download,extracted})
                    try { if(temporary!=null&&File.Exists(temporary)) File.Delete(temporary); } catch(IOException) { } catch(UnauthorizedAccessException) { }
                requestGate.Release();
            }
        }
        private async Task<HttpResponseMessage> DownloadResponse(string url,CancellationToken token)
        {
            var uri=new Uri(url);
            for(int i=0;i<6;i++)
            {
                using(var request=new HttpRequestMessage(HttpMethod.Get,uri))
                {
                    request.Headers.Accept.Clear();request.Headers.Accept.ParseAdd("application/octet-stream");
                    var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
                    if((int)response.StatusCode<300||(int)response.StatusCode>=400) return response;
                    var location=response.Headers.Location;response.Dispose();if(location==null) throw new IOException("GitHub's download redirect is missing.");
                    uri=location.IsAbsoluteUri?location:new Uri(uri,location);if(!GitHubRepository.RedirectUrl(uri)) throw new InvalidDataException("Unsupported download redirect.");
                }
            }
            throw new IOException("Too many GitHub download redirects.");
        }
        internal static StagedModSidecar[] ExtractSingleDll(string archive,string target,string plugin="")
        {
            using(var stream=File.OpenRead(archive)) using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
            {
                if(zip.Entries.Count>256||zip.Entries.Any(e=>e.Length>ModUpdateFiles.MaximumBytes)||zip.Entries.Sum(e=>e.Length)>ModUpdateFiles.MaximumBytes) throw new InvalidDataException("This archive is too large for an in-game update.");
                var names=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in zip.Entries)
                {
                    string name=entry.FullName.EndsWith("/",StringComparison.Ordinal)?entry.FullName.Substring(0,entry.FullName.Length-1):entry.FullName;SafeArchivePath(name);
                    if(names.ContainsKey(name))throw new InvalidDataException("The update archive contains duplicate or case-colliding paths.");names.Add(name,entry);
                    int kind=entry.ExternalAttributes>>16&0xF000;
                    if((kind!=0&&kind!=0x8000&&kind!=0x4000)||(entry.ExternalAttributes&0x400)!=0) throw new InvalidDataException("Linked files are unsupported in an update archive.");
                }
                foreach(var item in names)
                {
                    string[] parts=item.Key.Split('/');for(int i=1;i<parts.Length;i++)
                    {string parent=string.Join("/",parts.Take(i));if(names.TryGetValue(parent,out var existing)&&!existing.FullName.EndsWith("/",StringComparison.Ordinal))throw new InvalidDataException("The update archive contains conflicting file and folder paths.");}
                }
                var dlls=zip.Entries.Where(e=>e.FullName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&!e.FullName.EndsWith("/",StringComparison.Ordinal)).ToArray();
                if(dlls.Length!=1||zip.Entries.Any(e=>e.FullName.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith(".bat",StringComparison.OrdinalIgnoreCase)||e.FullName.EndsWith(".ps1",StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("This package needs a manual installation. Open its release page.");
                var manifests=zip.Entries.Where(e=>e.FullName.EndsWith(".doorman.json",StringComparison.OrdinalIgnoreCase)).ToArray();
                if(manifests.Length>1)throw new InvalidDataException("This package has multiple previews. Open its release page for a manual installation.");
                var previews=new List<StagedModSidecar>();
                if(manifests.Length==1)
                {
                    if(plugin.Length==0)throw new InvalidDataException("A ZIP preview needs the selected plugin identity.");
                    var manifest=manifests[0];var card=ModJson.ReadBytes<ModManifest>(ReadEntry(manifest,ModUpdateFiles.MaximumPreviewBytes))??throw new InvalidDataException("The ZIP preview manifest is empty.");
                    if(card.Plugin!=plugin||!string.IsNullOrEmpty(card.Bundle))throw new InvalidDataException("Automatic ZIP previews need only this exact plugin ID. Open the release page for a manual installation.");
                    string image=card.Image??"";card.Directory=Path.GetDirectoryName(Path.GetFullPath(target))!;card.Validate();
                    if(card.Image!=image)throw new InvalidDataException("The ZIP preview image path is unsafe.");
                    if(image.Length>0)
                    {
                        SafeArchivePath(image);string prefix=manifest.FullName.Contains("/")?manifest.FullName.Substring(0,manifest.FullName.LastIndexOf('/')+1):"";
                        if(!names.TryGetValue(prefix+image,out var picture)||picture.FullName.EndsWith("/",StringComparison.Ordinal))throw new InvalidDataException("The ZIP preview image is missing.");
                        byte[] data=ReadEntry(picture,ModUpdateFiles.MaximumImageBytes);
                        if(!ModImageHeader.Allowed(data))throw new InvalidDataException("The ZIP preview image is invalid or too large.");
                        card.Image=Path.GetExtension(image).Equals(".png",StringComparison.OrdinalIgnoreCase)?"image.png":"image.jpg";
                        if(card.Image=="image.png"&&data[0]!=137||card.Image=="image.jpg"&&data[0]!=255)throw new InvalidDataException("The ZIP preview image format does not match its filename.");
                        previews.Add(new StagedModSidecar { Name=card.Image,Data=data });
                    }
                    using(var serialized=new MemoryStream())
                    {new DataContractJsonSerializer(typeof(ModManifest)).WriteObject(serialized,card);if(serialized.Length>ModUpdateFiles.MaximumPreviewBytes)throw new InvalidDataException("The ZIP preview manifest is too large.");previews.Add(new StagedModSidecar { Name="preview.doorman.json",Data=serialized.ToArray() });}
                }
                using(var input=dlls[0].Open()) using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    var buffer=new byte[65536];long count=0;int read;while((read=input.Read(buffer,0,buffer.Length))>0) { count+=read;if(count>ModUpdateFiles.MaximumBytes||count>dlls[0].Length) throw new InvalidDataException("The archive expanded beyond its declared size.");output.Write(buffer,0,read); }output.Flush(true);
                    if(count!=dlls[0].Length) throw new InvalidDataException("The archive was incomplete.");
                }
                return previews.ToArray();
            }
        }
        private static void SafeArchivePath(string name)
        {
            if(name.Length==0||name.Length>500||name.StartsWith("/",StringComparison.Ordinal)||name.Contains("\\")||name.Any(c=>char.IsControl(c)||"<>:\"|?*".Contains(c)))throw new InvalidDataException("The update archive contains an unsafe path.");
            foreach(string part in name.Split('/'))
            {
                string device=part.Split('.')[0];
                if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||new[]{"CON","PRN","AUX","NUL"}.Contains(device,StringComparer.OrdinalIgnoreCase)||device.Length==4&&(device.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||device.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&device[3]>='1'&&device[3]<='9')throw new InvalidDataException("The update archive contains an unsafe path.");
            }
        }
        private static byte[] ReadEntry(ZipArchiveEntry entry,int maximum)
        {
            if(entry.Length>maximum)throw new InvalidDataException("The ZIP preview is too large.");
            using(var input=entry.Open())using(var output=new MemoryStream())
            {
                var buffer=new byte[65536];int read;while((read=input.Read(buffer,0,buffer.Length))>0){if(output.Length+read>maximum||output.Length+read>entry.Length)throw new InvalidDataException("The ZIP preview expanded beyond its declared size.");output.Write(buffer,0,read);}if(output.Length!=entry.Length)throw new InvalidDataException("The ZIP preview is incomplete.");return output.ToArray();
            }
        }
        private static async Task<long> CopyBounded(Stream input,Stream output,long maximum,CancellationToken token,Action<int>? progress=null)
        {
            var buffer=new byte[65536];long total=0;int read,last=-1;
            while((read=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0)
            {
                total+=read;if(total>maximum) throw new InvalidDataException("The download exceeded its declared size.");await output.WriteAsync(buffer,0,read,token).ConfigureAwait(false);
                int percent=(int)(total*100/maximum);if(percent!=last) { last=percent;progress?.Invoke(percent); }
            }
            return total;
        }
        public void Dispose()=>http.Dispose();
    }
}
