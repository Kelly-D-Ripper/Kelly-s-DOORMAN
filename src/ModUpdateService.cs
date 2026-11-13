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
        [DataMember] public string Repository="", ETag="", Error="", ReleaseName="";
        [DataMember] public long Expires;
        [DataMember] public GitHubRelease? Release;
    }
    [DataContract]
    internal sealed class ReleaseCache
    {
        [DataMember] public int Schema=3;
        [DataMember] public ReleaseCacheEntry[] Entries=Array.Empty<ReleaseCacheEntry>();
    }
    internal sealed class ModUpdateCheck
    {
        internal string Message="",DisplayVersion="";
        internal GitHubRelease? Release;
        internal GitHubAsset? Asset;
        internal bool Newer,Queued;
    }
    [DataContract]
    internal sealed class GitHubReleaseTitle
    {
        [DataMember(Name="name")] public string Name="";
    }
    // Lives only in the update cache until the complete server set is proven.
    internal sealed class ServerDownloadPayload : IDisposable
    {
        internal ServerDownloadCandidate Candidate=null!;
        internal string PayloadPath="", Hash="", Plugin="",PluginHash="",SourceHash="",PackageDllName="";
        internal StagedModSidecar[] Sidecars=Array.Empty<StagedModSidecar>();
        internal StagedPackageFile[] PackageFiles=Array.Empty<StagedPackageFile>();
        internal string ArchivePath="";
        public void Dispose()
        {
            foreach(string path in new[]{PayloadPath,ArchivePath}.Concat(PackageFiles.Select(f=>f.Path)).Where(p=>!string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase))
                try{if(File.Exists(path))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}
            PayloadPath="";ArchivePath="";PackageFiles=Array.Empty<StagedPackageFile>();
        }
    }
    // Network callbacks run off the Unity thread; the menu reads these counters
    // on its own thread. A missing length is shown as indeterminate progress.
    internal sealed class ServerDownloadProgress
    {
        private long received,total;
        internal void SetTotal(long? value)=>Interlocked.Exchange(ref total,value.GetValueOrDefault()>0?value!.Value:0);
        internal void Report(long value)=>Interlocked.Exchange(ref received,Math.Max(0,value));
        internal float Fraction { get { long size=Interlocked.Read(ref total),done=Interlocked.Read(ref received);return size<=0?-1f:(float)Math.Min(1d,(double)done/size); } }
    }
    internal sealed class ModUpdateService : IDisposable
    {
        internal static readonly TimeSpan CacheLifetime=TimeSpan.FromHours(6),FailureLifetime=TimeSpan.FromMinutes(15);
        private readonly string pluginRoot,updateRoot,cachePath;
        private readonly HttpClient http;
        private readonly SemaphoreSlim requestGate=new SemaphoreSlim(1,1);
        private readonly Dictionary<string,ReleaseCacheEntry> cache=new Dictionary<string,ReleaseCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Func<DateTimeOffset> now;
        private readonly TimeSpan downloadTimeout,releaseTimeout;
        private bool loaded;
        private DateTimeOffset pausedUntil;
        internal Action<string,Exception>? DiagnosticFailure { get; set; }
        private void ReportFailure(string stage,Exception error)
        {
            try{DiagnosticFailure?.Invoke(stage,error);}catch{/* Diagnostics must not break downloads. */}
        }
        internal ModUpdateService(string pluginRoot,string cacheRoot,HttpMessageHandler? handler=null,Func<DateTimeOffset>? now=null,TimeSpan? downloadTimeout=null,TimeSpan? releaseTimeout=null)
        {
            this.pluginRoot=pluginRoot; updateRoot=Path.Combine(cacheRoot,"DOORMAN-updates");cachePath=Path.Combine(cacheRoot,"doorman-releases.json");this.now=now??(()=>DateTimeOffset.UtcNow);
            this.downloadTimeout=downloadTimeout??TimeSpan.FromMinutes(10);
            if(this.downloadTimeout<=TimeSpan.Zero||this.downloadTimeout>TimeSpan.FromMinutes(10))throw new ArgumentOutOfRangeException(nameof(downloadTimeout));
            this.releaseTimeout=releaseTimeout??TimeSpan.FromSeconds(15);
            if(this.releaseTimeout<=TimeSpan.Zero||this.releaseTimeout>TimeSpan.FromSeconds(15))throw new ArgumentOutOfRangeException(nameof(releaseTimeout));
            http=new HttpClient(handler??new HttpClientHandler { AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.GZip|DecompressionMethods.Deflate });http.Timeout=TimeSpan.FromMinutes(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kellys-DOORMAN/"+SelfUpdateRelease.Installed); http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }
        internal async Task<ModUpdateCheck> CheckAsync(string repository,string pattern,string installed,string target,CancellationToken token)
        {
            if(!GitHubRepository.Valid(repository)) return new ModUpdateCheck { Message="No GitHub repository supplied for this mod." };
            await requestGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                LoadCache();
                var queued=target.Length==0?null:ModUpdateFiles.PendingFor(updateRoot,ModUpdateFiles.RelativeTarget(pluginRoot,target));
                if(queued!=null) return new ModUpdateCheck { Queued=true,Message=QueuedMessage(queued,"Version "+queued.Version+" is queued. Restart Nuclear Option to finish installing it.") };
                cache.TryGetValue(repository,out var previous);
                if(previous!=null&&previous.Expires>now().ToUnixTimeSeconds()) return Describe(previous,pattern,installed);
                if(now()<pausedUntil) return new ModUpdateCheck { Message="GitHub asked us to slow down. Try again after "+pausedUntil.ToLocalTime().ToString("t")+"." };
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(releaseTimeout);
                    try
                    {
                        using(var request=new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/"+repository+"/releases/latest"))
                        {
                            if(previous?.Release!=null&&previous.ETag.Length>0) request.Headers.TryAddWithoutValidation("If-None-Match",previous.ETag);
                            using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                            using(timeout.Token.Register(()=>{try{response.Dispose();}catch{/* Abort must not throw on the timer thread. */}}))
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
                                        buffer.Position=0; var releaseName=UpdateJson.Read<GitHubReleaseTitle>(buffer).Name??"";
                                        if(release.Draft||release.Prerelease||release.Tag==null||release.Tag.Length>128||!GitHubRepository.FromUrl(release.Url).Equals(repository,StringComparison.OrdinalIgnoreCase)||(release.Assets?.Length??0)>128) throw new InvalidDataException("GitHub returned an unsupported release.");
                                        entry=new ReleaseCacheEntry { Repository=repository,Release=release,ReleaseName=releaseName,ETag=response.Headers.ETag?.ToString()??"" };
                                    }
                                }
                                entry.Expires=now().Add(CacheLifetime).ToUnixTimeSeconds();cache[repository]=entry;SaveCache();return Describe(entry,pattern,installed);
                            }
                        }
                    }
                    catch(Exception ex) when(timeout.IsCancellationRequested&&(ex is OperationCanceledException||ex is IOException||ex is ObjectDisposedException||ex is HttpRequestException))
                    {
                        if(token.IsCancellationRequested)throw new OperationCanceledException(token);
                        ReportFailure("release check timed out",ex);
                        return Failure(repository,"GitHub release check timed out. Try this page again later.");
                    }
                    catch(OperationCanceledException) when(!token.IsCancellationRequested) { return Failure(repository,"GitHub did not answer. Try this page again later."); }
                    catch(Exception) when(token.IsCancellationRequested) { throw new OperationCanceledException(token); }
                    catch(Exception ex) when(!(ex is OperationCanceledException)) { ReportFailure("release check",ex);return Failure(repository,ex is IOException?ex.Message:"Could not check GitHub. Your installed mod is unchanged."); }
                }
            }
            finally { requestGate.Release(); }
        }
        internal async Task<ModUpdateCheck> CheckInstallLinkAsync(GitHubInstallLink link,CancellationToken token,long maximumBytes=1024L*1024*1024)
        {
            if(link.Tag.Length==0)
            {
                var latest=await CheckAsync(link.Repository,"","","",token).ConfigureAwait(false);
                if(latest.Release!=null)latest.Asset=link.Select(latest.Release,maximumBytes);
                return latest;
            }
            await requestGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(releaseTimeout);
                    using(var request=new HttpRequestMessage(HttpMethod.Get,"https://api.github.com/repos/"+link.Repository+"/releases/tags/"+Uri.EscapeDataString(link.Tag)))
                    using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false))
                    using(timeout.Token.Register(()=>{try{response.Dispose();}catch{}}))
                    {
                        response.EnsureSuccessStatusCode();
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var buffer=new MemoryStream())
                        {
                            await CopyBounded(input,buffer,512*1024,timeout.Token).ConfigureAwait(false);buffer.Position=0;
                            var release=UpdateJson.Read<GitHubRelease>(buffer);
                            if(release.Draft||release.Tag!=link.Tag||!GitHubRepository.FromUrl(release.Url).Equals(link.Repository,StringComparison.OrdinalIgnoreCase)||(release.Assets?.Length??0)>128)throw new InvalidDataException("This release needs manual installation.");
                            return new ModUpdateCheck { Release=release,Asset=link.Select(release,maximumBytes) };
                        }
                    }
                }
            }
            finally{requestGate.Release();}
        }
        internal async Task<ModUpdateCheck> CheckPrerequisiteAsync(string repository,string pattern,string plugin,string minimumVersion,CancellationToken token)
        {
            if(!ModUpdateFiles.PluginIdentifierValid(plugin)||ModUpdateFiles.ProtectedPlugin(plugin)||!string.IsNullOrEmpty(minimumVersion)&&ModReleaseVersion.Compare(minimumVersion,minimumVersion)!=0)return new ModUpdateCheck { Message="This prerequisite needs a manual installation." };
            new ModUpdateSource { Repository=repository,Asset=pattern }.Validate();
            await requestGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var queued=ModUpdateFiles.PendingPlugin(updateRoot,plugin);
                if(queued!=null)return new ModUpdateCheck { Queued=true,Message=QueuedMessage(queued,"Plugin version "+(queued.PluginVersion??queued.Version)+" is queued. Restart Nuclear Option to finish installing it.") };
            }
            finally{requestGate.Release();}
            // Reuse the same bounded, persisted cache as ordinary mod pages.
            // Opening a prerequisite page adds no background polling loop.
            var result=await CheckAsync(repository,pattern,"","",token).ConfigureAwait(false);
            if(result.Release!=null)
            {
                result.Message="Latest release: "+result.Release.Tag+". The required plugin ID and DLL version are checked before installation.";
                if(minimumVersion.Length>0&&ModReleaseVersion.Compare(result.Release.Tag,minimumVersion)<0){result.Asset=null;result.Message="The latest stable release is older than the required "+minimumVersion+". Open the release page.";}
                else if(result.Asset==null)result.Message+=" Open the release page to choose a supported download.";
            }
            return result;
        }
        private static string QueuedMessage(PendingModUpdate queued,string fallback)
            =>string.IsNullOrWhiteSpace(queued.Message)?fallback:Diagnostics.Clean(queued.Message);
        private ModUpdateCheck Failure(string repository,string message)
        {
            var entry=new ReleaseCacheEntry { Repository=repository,Error=message,Expires=now().Add(FailureLifetime).ToUnixTimeSeconds() };cache[repository]=entry;SaveCache();return new ModUpdateCheck { Message=message };
        }
        private static ModUpdateCheck Describe(ReleaseCacheEntry entry,string pattern,string installed)
        {
            if(entry.Error.Length>0||entry.Release==null) return new ModUpdateCheck { Message=entry.Error.Length>0?entry.Error:"Could not check this release." };
            var release=entry.Release;string title=(entry.ReleaseName??"").Trim();string latest=ModReleaseVersion.Compare(title,title)==0?title:release.Tag;int? compared=ModReleaseVersion.Compare(latest,installed);
            var result=new ModUpdateCheck { Release=release,Asset=release.SelectAsset(entry.Repository,pattern),Newer=compared>0,DisplayVersion=latest };
            result.Message=installed.Length==0?"Latest release: "+latest+". Enable this plugin and restart to read its content version.":compared==null?"Installed "+installed+" | Latest "+latest+". These version names need a manual comparison.":compared>0?"Update available: "+installed+" to "+latest+".":compared<0?"Installed "+installed+" is ahead of the latest stable release ("+latest+").":"Up to date: "+installed+".";
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
                // Older cache records did not preserve release titles and can
                // give false updates for repos whose tags use other numbering.
                if(disk.Schema!=3)return;
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
        internal Task StageAsync(string repository,string plugin,string target,GitHubRelease release,GitHubAsset asset,CancellationToken token,Action<int>? progress=null,string minimumVersion="")=>StageDownloadAsync(repository,plugin,target,minimumVersion,false,release,asset,token,progress);
        internal Task StageInstallAsync(string repository,string plugin,string minimumVersion,GitHubRelease release,GitHubAsset asset,CancellationToken token,Action<int>? progress=null)=>StageDownloadAsync(repository,plugin,"",minimumVersion,true,release,asset,token,progress);
        internal async Task<string> DownloadReportedBundleAsync(NommRecord record,string version,CancellationToken token)
        {
            string repository=(record.Owner??"")+"/"+(record.Repository??"");
            if(!GitHubRepository.Valid(repository)||ModReleaseVersion.Compare(version,version)!=0)throw new InvalidDataException("This mod needs a manual download.");
            var choices=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Version==version).Take(2).ToArray();
            if(choices.Length!=1)throw new InvalidDataException("The exact reported version is not uniquely available in NOMNOM. Visit GitHub.");
            var asset=choices[0];string url=string.IsNullOrEmpty(asset.DownloadUrl)?asset.DownloadURL:asset.DownloadUrl;
            if(!GitHubRepository.DownloadUrl(url,repository)||string.IsNullOrWhiteSpace(asset.FileName)||Path.GetFileName(asset.FileName)!=asset.FileName||!Uri.UnescapeDataString(new Uri(url).Segments.Last()).Equals(asset.FileName,StringComparison.Ordinal)||!asset.FileName.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("This package needs a manual installation. Visit GitHub.");
            if(asset.Hash==null||!asset.Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.HashValid(asset.Hash.Substring(7)))throw new InvalidDataException("This release has no usable checksum. Visit GitHub.");
            await requestGate.WaitAsync(token).ConfigureAwait(false);string? download=null;bool keep=false;
            try
            {
                string downloads=ModUpdateFiles.Within(updateRoot,"downloads");Directory.CreateDirectory(downloads);download=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(downloadTimeout);
                    using(var response=await DownloadResponse(url,timeout.Token).ConfigureAwait(false))
                    using(timeout.Token.Register(()=>{try{response.Dispose();}catch{}}))
                    {
                        response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("The download is too large.");
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(download,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {long count=await CopyBounded(input,output,ModUpdateFiles.MaximumBytes,timeout.Token).ConfigureAwait(false);output.Flush(true);if(count==0||response.Content.Headers.ContentLength is long expected&&count!=expected)throw new InvalidDataException("The download was incomplete.");}
                    }
                }
                token.ThrowIfCancellationRequested();
                if(!ModUpdateFiles.Hash(download).Equals(asset.Hash.Substring(7),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The download checksum does not match NOMNOM. Visit GitHub.");
                keep=true;return download;
            }
            finally{if(!keep&&download!=null)try{if(File.Exists(download))File.Delete(download);}catch(IOException){}catch(UnauthorizedAccessException){}requestGate.Release();}
        }
        internal async Task<string> DownloadMapPackageAsync(string repository,GitHubRelease release,GitHubAsset asset,CancellationToken token,ServerDownloadProgress? progress=null,long maximumBytes=MapPackageInstaller.MaximumDownload,TimeSpan? maximumTime=null)
        {
            if(maximumBytes<=0||maximumBytes>ServerMapUpdates.MaximumMapBytes||maximumTime.HasValue&&(maximumTime<=TimeSpan.Zero||maximumTime>TimeSpan.FromHours(1)))throw new ArgumentOutOfRangeException(nameof(maximumBytes));
            if(!GitHubRepository.DownloadUrl(asset.Url,repository)||asset.State!="uploaded"||asset.Size<=0||asset.Size>maximumBytes||release.Draft||!release.Assets.Any(a=>a.Name==asset.Name&&a.Url==asset.Url&&a.Size==asset.Size)||asset.Digest==null||!asset.Digest.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.HashValid(asset.Digest.Substring(7)))throw new InvalidDataException("This map release needs a manual installation.");
            await requestGate.WaitAsync(token).ConfigureAwait(false);string? file=null;bool keep=false;
            try
            {
                string folder=ModUpdateFiles.Within(updateRoot,"downloads");Directory.CreateDirectory(folder);file=ModUpdateFiles.Within(folder,Guid.NewGuid().ToString("N")+".bin");
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(maximumTime??downloadTimeout);
                    using(var response=await DownloadResponse(asset.Url,timeout.Token).ConfigureAwait(false))
                    using(timeout.Token.Register(()=>{try{response.Dispose();}catch{}}))
                    {
                        response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength is long length&&length!=asset.Size)throw new InvalidDataException("Download size changed.");
                        progress?.SetTotal(asset.Size);EnsureServerDownloadSpace(folder,pluginRoot,asset.Size);
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {long count=await CopyBounded(input,output,asset.Size,timeout.Token,bytesProgress:progress==null?null:(Action<long>)progress.Report).ConfigureAwait(false);output.Flush(true);if(count!=asset.Size)throw new InvalidDataException("Download was incomplete.");}
                    }
                }
                token.ThrowIfCancellationRequested();if(!ModUpdateFiles.Hash(file).Equals(asset.Digest.Substring(7),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("GitHub checksum does not match.");
                keep=true;return file;
            }
            finally{if(!keep&&file!=null)try{File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}requestGate.Release();}
        }
        // NOSMR selects an exact NOMNOM artifact, not GitHub's latest release.
        // A registry hash binds its bytes before the existing startup queue is used.
        internal async Task<string> StageReportedAsync(NommRecord record,string version,IEnumerable<string> disabledPluginIds,CancellationToken token,string expectedPlugin="",string existingTarget="")
        {
            string repository=(record.Owner??"")+"/"+(record.Repository??"");
            if(!GitHubRepository.Valid(repository)||ModReleaseVersion.Compare(version,version)!=0)throw new InvalidDataException("This mod needs a manual download.");
            if(existingTarget.Length>0&&!ModUpdateFiles.PluginIdentifierValid(expectedPlugin))throw new InvalidDataException("An existing update needs an exact plugin identity.");
            var choices=(record.Artifacts??Array.Empty<NommArtifact>()).Where(a=>a!=null&&a.Version==version).Take(2).ToArray();
            if(choices.Length!=1)throw new InvalidDataException("The exact reported version is not uniquely available in NOMNOM. Visit GitHub.");
            var asset=choices[0];string url=string.IsNullOrEmpty(asset.DownloadUrl)?asset.DownloadURL:asset.DownloadUrl;
            if(!GitHubRepository.DownloadUrl(url,repository)||string.IsNullOrWhiteSpace(asset.FileName)||Path.GetFileName(asset.FileName)!=asset.FileName||!Uri.UnescapeDataString(new Uri(url).Segments.Last()).Equals(asset.FileName,StringComparison.Ordinal)||!(asset.FileName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||asset.FileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("This package needs a manual installation. Visit GitHub.");
            if(asset.Hash==null||!asset.Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.HashValid(asset.Hash.Substring(7)))throw new InvalidDataException("This release has no usable checksum. Visit GitHub.");
            await requestGate.WaitAsync(token).ConfigureAwait(false);
            string? download=null,extracted=null;
            try
            {
                string downloads=ModUpdateFiles.Within(updateRoot,"downloads");Directory.CreateDirectory(downloads);
                download=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(downloadTimeout);
                    using(var response=await DownloadResponse(url,timeout.Token).ConfigureAwait(false))
                    using(timeout.Token.Register(()=>{try{response.Dispose();}catch{}}))
                    {
                        response.EnsureSuccessStatusCode();
                        if(response.Content.Headers.ContentLength>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("The download is too large.");
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(download,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {long count=await CopyBounded(input,output,ModUpdateFiles.MaximumBytes,timeout.Token).ConfigureAwait(false);output.Flush(true);if(count==0||response.Content.Headers.ContentLength is long expected&&count!=expected)throw new InvalidDataException("The download was incomplete.");}
                    }
                }
                if(!ModUpdateFiles.Hash(download).Equals(asset.Hash.Substring(7),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The download checksum does not match NOMNOM. Visit GitHub.");
                string payload=download;StagedModSidecar[] previews=Array.Empty<StagedModSidecar>();
                if(asset.FileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
                {extracted=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");previews=ExtractSingleDll(download,extracted);payload=extracted;}
                var ids=ModUpdateFiles.PluginIds(payload);
                if(ids.Length!=1||ModUpdateFiles.ProtectedPlugin(ids[0])||expectedPlugin.Length>0&&ids[0]!=expectedPlugin)throw new InvalidDataException("The download has an unexpected plugin identity. Visit GitHub.");
                if(disabledPluginIds.Contains(ids[0],StringComparer.Ordinal))throw new InvalidDataException("This mod is disabled in NOMM. Enable it there instead of downloading another copy.");
                token.ThrowIfCancellationRequested();
                if(existingTarget.Length==0)ModUpdateFiles.StageInstall(updateRoot,pluginRoot,payload,ids[0],repository,"",previews,token);
                else
                {
                    // The caller provides the known installed path and identity. Never
                    // infer an overwrite target from a filename or a registry label.
                    string relative=ModUpdateFiles.RelativeTarget(pluginRoot,existingTarget);
                    string target=ModUpdateFiles.Within(pluginRoot,relative);
                    ModUpdateFiles.VerifyInstallIdentity(target,ids[0],"");
                    ModUpdateFiles.EnsurePluginAbsent(pluginRoot,ids[0],target);
                    token.ThrowIfCancellationRequested();
                    ModUpdateFiles.Stage(updateRoot,pluginRoot,target,payload,ids[0],repository,version,previews);
                }
                return ids[0];
            }
            finally
            {
                foreach(var path in new[]{download,extracted})try{if(path!=null&&File.Exists(path))File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}
                requestGate.Release();
            }
        }
        // Download into a disposable cache file only. The caller must verify the
        // downloaded manifest and the server's aggregate before staging anything.
        internal async Task<ServerDownloadPayload> DownloadServerCandidateAsync(ServerDownloadCandidate candidate,CancellationToken token,ServerDownloadProgress? progress=null)
        {
            if(candidate==null||candidate.Record==null)throw new InvalidDataException("Missing server download identity.");
            var record=candidate.Record;string version=candidate.PackageVersion;
            string repository=(record.Owner??"")+"/"+(record.Repository??"");
            if(!GitHubRepository.Valid(repository)||ModReleaseVersion.Compare(version,version)!=0)throw new InvalidDataException("This mod needs a manual download.");
            var matches=candidate.MatchingArtifacts();
            if(matches.Length!=1)throw new InvalidDataException("The exact release is not unique in NOMNOM. Visit GitHub.");
            var asset=matches[0];string url=string.IsNullOrEmpty(asset.DownloadUrl)?asset.DownloadURL:asset.DownloadUrl;
            if(!GitHubRepository.DownloadUrl(url,repository)||string.IsNullOrWhiteSpace(asset.FileName)||Path.GetFileName(asset.FileName)!=asset.FileName
                ||!Uri.UnescapeDataString(new Uri(url).Segments.Last()).Equals(asset.FileName,StringComparison.Ordinal)
                ||!(asset.FileName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||asset.FileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase)||asset.FileName.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))
                ||asset.Hash==null||!asset.Hash.StartsWith("sha256:",StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.HashValid(asset.Hash.Substring(7)))
                throw new InvalidDataException("This package needs a manual installation. Visit GitHub.");
            await requestGate.WaitAsync(token).ConfigureAwait(false);string? download=null,extracted=null;bool keep=false;string packageDllName="";StagedPackageFile[] package=Array.Empty<StagedPackageFile>();
            try
            {
                string downloads=ModUpdateFiles.Within(updateRoot,"downloads");Directory.CreateDirectory(downloads);
                download=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(downloadTimeout);
                    using(var response=await DownloadResponse(url,timeout.Token).ConfigureAwait(false))
                    using(timeout.Token.Register(()=>{try{response.Dispose();}catch{}}))
                    {
                        response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>ModUpdateFiles.MaximumBytes)throw new InvalidDataException("The download is too large.");
                        progress?.SetTotal(response.Content.Headers.ContentLength);
                        EnsureServerDownloadSpace(downloads,pluginRoot,response.Content.Headers.ContentLength);
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new FileStream(download,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {long count=await CopyBounded(input,output,ModUpdateFiles.MaximumBytes,timeout.Token,bytesProgress:progress==null?null:(Action<long>)progress.Report).ConfigureAwait(false);output.Flush(true);if(count==0||response.Content.Headers.ContentLength is long expected&&count!=expected)throw new InvalidDataException("The download was incomplete.");}
                    }
                }
                token.ThrowIfCancellationRequested();
                if(!ModUpdateFiles.Hash(download).Equals(asset.Hash.Substring(7),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The download checksum does not match NOMNOM. Visit GitHub.");
                string path=download;StagedModSidecar[] sidecars=Array.Empty<StagedModSidecar>();
                if(asset.FileName.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))
                {
                    extracted=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");
                    if(ContainsBundle(download))
                    {
                        long expanded;using(var stream=File.OpenRead(download))using(var archive=new ZipArchive(stream,ZipArchiveMode.Read))expanded=archive.Entries.Sum(e=>e.Length);
                        if(expanded>1024L*1024*1024)throw new InvalidDataException("This package is too large for automatic installation.");
                        EnsureServerDownloadSpace(downloads,pluginRoot,expanded);package=ExtractGameplayPackage(download,extracted,downloads,token);
                        using(var stream=File.OpenRead(download))using(var archive=new ZipArchive(stream,ZipArchiveMode.Read))packageDllName=Path.GetFileName(archive.Entries.Single(e=>e.FullName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)).FullName.Replace('\\','/'));
                    }
                    else sidecars=ExtractSingleDll(download,extracted,candidate.OwnerPlugin);
                    path=extracted;
                }
                string plugin="";
                if(!candidate.Standalone)
                {
                    var ids=ModUpdateFiles.PluginIds(path);
                    bool reviewedMulti=candidate.AllowMulti&&ids.SequenceEqual(new[]{"com.minec.bote","com.minec.bote.inputs"},StringComparer.Ordinal);
                    if((ids.Length!=1&&!reviewedMulti)||ids.Any(ModUpdateFiles.ProtectedPlugin)||candidate.OwnerPlugin.Length>0&&!ids.Contains(candidate.OwnerPlugin,StringComparer.Ordinal))
                        throw new InvalidDataException("The download has an unexpected plugin identity. Visit GitHub.");
                    plugin=candidate.OwnerPlugin.Length>0?candidate.OwnerPlugin:ids[0];
                }
                token.ThrowIfCancellationRequested();
                string pluginHash=ModUpdateFiles.Hash(path);
                var payload=new ServerDownloadPayload {Candidate=candidate,PayloadPath=path,ArchivePath=extracted==null?"":download,Hash=pluginHash,PluginHash=pluginHash,SourceHash=asset.Hash.Substring(7),Plugin=plugin,Sidecars=sidecars,PackageFiles=package,PackageDllName=packageDllName};
                keep=true;return payload;
            }
            finally
            {
                if(!keep)foreach(var p in new[]{download,extracted}.Concat(package.Select(f=>f.Path)))try{if(p!=null&&File.Exists(p))File.Delete(p);}catch(IOException){}catch(UnauthorizedAccessException){}
                requestGate.Release();
            }
        }
        private static void EnsureServerDownloadSpace(string cacheRoot,string pluginRoot,long? length)
        {
            // Reserve enough room for the download, extracted payload, staged copy,
            // and a rollback copy. Unknown sizes use a conservative initial floor;
            // writes can still fail safely if an unknown response is much larger.
            long size=Math.Max(1,length??64L*1024*1024);long reserve=64L*1024*1024;
            string cacheDrive=Path.GetPathRoot(Path.GetFullPath(cacheRoot))??"",pluginDrive=Path.GetPathRoot(Path.GetFullPath(pluginRoot))??"";
            long cacheFree,pluginFree;
            try {cacheFree=new DriveInfo(cacheDrive).AvailableFreeSpace;pluginFree=new DriveInfo(pluginDrive).AvailableFreeSpace;}
            catch(Exception ex) when(ex is IOException||ex is UnauthorizedAccessException||ex is ArgumentException||ex is NotSupportedException){return;}
            if(cacheDrive.Equals(pluginDrive,StringComparison.OrdinalIgnoreCase))
            {if(cacheFree<size*4+reserve)throw new IOException("Not enough disk space to download, stage and back up this mod. Free space and try again.");}
            else if(cacheFree<size*3+reserve||pluginFree<size*2+reserve)
                throw new IOException("Not enough disk space to download, stage and back up this mod. Free space and try again.");
        }
        // The coordinator provides its durable batch-commit gate and rollback.
        // Standalone content uses its own queue; it cannot be staged here.
        internal void StageServerPlugin(ServerDownloadPayload payload,string[] disabledPluginIds,CancellationToken token)
        {
            var candidate=payload.Candidate;
            if(candidate.Standalone||payload.Plugin.Length==0||!File.Exists(payload.PayloadPath)||!ModUpdateFiles.Hash(payload.PayloadPath).Equals(payload.PluginHash.Length>0?payload.PluginHash:payload.Hash,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The checked plugin download changed before staging.");
            var ids=ModUpdateFiles.PluginIds(payload.PayloadPath);
            if(ids.Any(id=>disabledPluginIds.Contains(id,StringComparer.Ordinal)))throw new InvalidDataException("This mod is disabled in NOMM. Enable it there first.");
            string repository=candidate.Record.Owner+"/"+candidate.Record.Repository;token.ThrowIfCancellationRequested();
            if(payload.PackageFiles.Length>0)
            {
                ModUpdateFiles.StageGameplayPackage(updateRoot,pluginRoot,payload.PayloadPath,payload.Plugin,repository,candidate.Version,candidate.Target,payload.SourceHash,candidate.Record.Id,payload.PackageDllName,payload.PackageFiles,token);
            }
            else if(candidate.Target.Length==0)ModUpdateFiles.StageInstall(updateRoot,pluginRoot,payload.PayloadPath,payload.Plugin,repository,"",payload.Sidecars,token,ids.Length>1&&candidate.AllowMulti);
            else
            {
                string relative=ModUpdateFiles.RelativeTarget(pluginRoot,candidate.Target);string target=ModUpdateFiles.Within(pluginRoot,relative);
                ModUpdateFiles.VerifyIdentity(target,payload.PayloadPath,payload.Plugin);foreach(var id in ids)ModUpdateFiles.EnsurePluginAbsent(pluginRoot,id,target);
                token.ThrowIfCancellationRequested();ModUpdateFiles.Stage(updateRoot,pluginRoot,target,payload.PayloadPath,payload.Plugin,repository,candidate.Version,payload.Sidecars);
            }
        }
        private async Task StageDownloadAsync(string repository,string plugin,string target,string minimumVersion,bool install,GitHubRelease release,GitHubAsset asset,CancellationToken token,Action<int>? progress)
        {
            if(!GitHubRepository.DownloadUrl(asset.Url,repository)||asset.Size<=0||asset.Size>ModUpdateFiles.MaximumBytes||asset.State!="uploaded"||!(asset.Name.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||asset.Name.EndsWith(".zip",StringComparison.OrdinalIgnoreCase))||release.Draft||release.Prerelease||!GitHubRepository.FromUrl(release.Url).Equals(repository,StringComparison.OrdinalIgnoreCase)||!release.Assets.Any(a=>a.Url==asset.Url&&a.Name==asset.Name&&a.Size==asset.Size)) throw new InvalidDataException("Unsupported GitHub download.");
            if(!ModUpdateFiles.PluginIdentifierValid(plugin)||ModUpdateFiles.ProtectedPlugin(plugin)||!string.IsNullOrEmpty(minimumVersion)&&ModReleaseVersion.Compare(minimumVersion,minimumVersion)!=0)throw new InvalidDataException("Invalid prerequisite plugin or version.");
            if(!install)ModUpdateFiles.RelativeTarget(pluginRoot,target);
            await requestGate.WaitAsync(token).ConfigureAwait(false);
            string? download=null,extracted=null;
            try
            {
                string downloads=ModUpdateFiles.Within(updateRoot,"downloads"); Directory.CreateDirectory(downloads);download=ModUpdateFiles.Within(downloads,Guid.NewGuid().ToString("N")+".bin");
                // ResponseHeadersRead ends HttpClient's timeout at the headers.
                // Keep our own deadline alive until the entire body is written,
                // so a stalled CDN cannot trap the mod menu in its busy state.
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(downloadTimeout);
                    try
                    {
                        using(var response=await DownloadResponse(asset.Url,timeout.Token).ConfigureAwait(false))
                        using(timeout.Token.Register(()=>{try{response.Dispose();}catch{/* Abort must not throw on the timer thread. */}}))
                        {
                            response.EnsureSuccessStatusCode();
                            if(response.Content.Headers.ContentLength is long length&&length!=asset.Size) throw new InvalidDataException("The release download size changed.");
                            using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false)) using(var output=new FileStream(download,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                            {
                                long count=await CopyBounded(input,output,asset.Size,timeout.Token,progress).ConfigureAwait(false);output.Flush(true);
                                if(count!=asset.Size) throw new InvalidDataException("The mod download was incomplete.");
                            }
                        }
                    }
                    catch(Exception ex) when(timeout.IsCancellationRequested&&(ex is OperationCanceledException||ex is IOException||ex is ObjectDisposedException||ex is HttpRequestException))
                    {
                        if(token.IsCancellationRequested)throw new OperationCanceledException(token);
                        throw new IOException("The mod download timed out. Your installed mod is unchanged. Try again.");
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
                token.ThrowIfCancellationRequested();
                if(install)ModUpdateFiles.StageInstall(updateRoot,pluginRoot,payload,plugin,repository,minimumVersion,previews,token);
                else ModUpdateFiles.Stage(updateRoot,pluginRoot,target,payload,plugin,repository,release.Tag,previews,minimumVersion);
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
        internal static bool ContainsBundle(string archive)
        {
            using(var stream=File.OpenRead(archive))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                return zip.Entries.Any(e=>e.FullName.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase));
        }
        // Only the common one-plugin, one-bundle package is accepted here.
        // Files are streamed into disposable staging paths, never into plugins.
        internal static StagedPackageFile[] ExtractGameplayPackage(string archive,string dllTarget,string staging,CancellationToken token)
        {
            var created=new List<string>();
            try
            {
                using(var stream=File.OpenRead(archive))using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))
                {
                    const long maxExpanded=1024L*1024*1024;
                    if(zip.Entries.Count>256||zip.Entries.Sum(e=>e.Length)>maxExpanded)throw new InvalidDataException("This package is too large for automatic installation.");
                    var names=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    var normalized=new Dictionary<ZipArchiveEntry,string>();
                    foreach(var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();string normalizedName=entry.FullName.Replace('\\','/');normalized.Add(entry,normalizedName);
                        string name=normalizedName.EndsWith("/",StringComparison.Ordinal)?normalizedName.Substring(0,normalizedName.Length-1):normalizedName;
                        SafeArchivePath(name);if(names.ContainsKey(name))throw new InvalidDataException("The package contains duplicate paths.");names.Add(name,entry);
                        int kind=entry.ExternalAttributes>>16&0xF000;
                        if((kind!=0&&kind!=0x8000&&kind!=0x4000)||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("Linked files are unsupported in a package.");
                        string[] parts=name.Split('/');for(int i=1;i<parts.Length;i++){string ancestor=string.Join("/",parts.Take(i));if(names.TryGetValue(ancestor,out var existing)&&!normalized[existing].EndsWith("/",StringComparison.Ordinal))throw new InvalidDataException("The package contains conflicting paths.");}
                    }
                    // Check parents again after the complete directory index exists.
                    foreach(var item in names){string[] parts=item.Key.Split('/');for(int i=1;i<parts.Length;i++){string ancestor=string.Join("/",parts.Take(i));if(names.TryGetValue(ancestor,out var existing)&&!normalized[existing].EndsWith("/",StringComparison.Ordinal))throw new InvalidDataException("The package contains conflicting paths.");}}
                    var dlls=zip.Entries.Where(e=>normalized[e].EndsWith(".dll",StringComparison.OrdinalIgnoreCase)).ToArray();
                    var bundles=zip.Entries.Where(e=>normalized[e].EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)).ToArray();
                    if(dlls.Length!=1||bundles.Length!=1)throw new InvalidDataException("This package needs one plugin and one content file. Install it manually.");
                    string dll=normalized[dlls[0]];string parent=dll.Contains("/")?dll.Substring(0,dll.LastIndexOf('/')+1):"";
                    var files=new List<StagedPackageFile>();var selected=new List<ZipArchiveEntry>{dlls[0]};
                    foreach(var entry in zip.Entries)
                    {
                        string entryName=normalized[entry];if(entryName.EndsWith("/",StringComparison.Ordinal)||entry==dlls[0])continue;
                        string name=Path.GetFileName(entryName);string ext=Path.GetExtension(name);
                        bool doc=(name.StartsWith("readme",StringComparison.OrdinalIgnoreCase)||name.StartsWith("licen",StringComparison.OrdinalIgnoreCase)||name.StartsWith("credit",StringComparison.OrdinalIgnoreCase)||name.Equals("meta.json",StringComparison.OrdinalIgnoreCase))&&new[]{"",".txt",".md",".rst",".json"}.Contains(ext,StringComparer.OrdinalIgnoreCase);
                        if(doc)continue;
                        if(!entryName.StartsWith(parent,StringComparison.OrdinalIgnoreCase)||entryName.Length==parent.Length||!(ext.Equals(".nobp",StringComparison.OrdinalIgnoreCase)||ext.Equals(".png",StringComparison.OrdinalIgnoreCase)||ext.Equals(".jpg",StringComparison.OrdinalIgnoreCase)||ext.Equals(".jpeg",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("This package has extra files that need a manual installation.");
                        if(!ext.Equals(".nobp",StringComparison.OrdinalIgnoreCase)&&entry.Length>ModUpdateFiles.MaximumImageBytes)throw new InvalidDataException("A package image is too large.");
                        selected.Add(entry);
                    }
                    if(selected.Count>64||dlls[0].Length>ModUpdateFiles.MaximumBytes||bundles[0].Length>maxExpanded)throw new InvalidDataException("This package is too large for automatic installation.");
                    foreach(var entry in selected)
                    {
                        token.ThrowIfCancellationRequested();string target=entry==dlls[0]?dllTarget:ModUpdateFiles.Within(staging,Guid.NewGuid().ToString("N")+".bin");
                        using(var input=entry.Open())using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                        {created.Add(target);var buffer=new byte[65536];long count=0;int n;while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();count+=n;if(count>entry.Length)throw new InvalidDataException("The package expanded beyond its declared size.");output.Write(buffer,0,n);}output.Flush(true);if(count!=entry.Length)throw new InvalidDataException("The package was incomplete.");}
                        if(entry!=dlls[0])
                        {
                            string ext=Path.GetExtension(normalized[entry]);
                            if(!ext.Equals(".nobp",StringComparison.OrdinalIgnoreCase))
                            {
                                byte[] picture=ModUpdateFiles.ReadBounded(target,ModUpdateFiles.MaximumImageBytes);
                                if(!ModImageHeader.Allowed(picture)||(ext.Equals(".png",StringComparison.OrdinalIgnoreCase)?picture[0]!=137:picture[0]!=255))throw new InvalidDataException("A package image has an invalid format.");
                            }
                            files.Add(new StagedPackageFile{Relative=normalized[entry].Substring(parent.Length),Path=target,Hash=ModUpdateFiles.Hash(target)});
                        }
                    }
                    return files.ToArray();
                }
            }
            catch{foreach(var path in created)try{File.Delete(path);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
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
                if(dlls.Length!=1||zip.Entries.Any(e=>new[]{".nobp",".nomap",".exe",".bat",".cmd",".ps1",".psm1",".sh",".vbs",".js",".py",".msi",".com",".scr",".lnk"}.Any(extension=>e.FullName.EndsWith(extension,StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("This package needs a manual installation. Open its release page.");
                var manifests=zip.Entries.Where(e=>e.FullName.EndsWith(".doorman.json",StringComparison.OrdinalIgnoreCase)).ToArray();
                if(manifests.Length>1)throw new InvalidDataException("This package has multiple previews. Open its release page for a manual installation.");
                // A new installation has no selected plugin yet. Read its identity
                // from the DLL metadata, without executing the downloaded code.
                bool extractedForIdentity=false;
                if(manifests.Length==1&&plugin.Length==0)
                {
                    using(var input=dlls[0].Open())using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                    {var buffer=new byte[65536];long count=0;int read;while((read=input.Read(buffer,0,buffer.Length))>0){count+=read;if(count>ModUpdateFiles.MaximumBytes||count>dlls[0].Length)throw new InvalidDataException("The archive expanded beyond its declared size.");output.Write(buffer,0,read);}output.Flush(true);if(count!=dlls[0].Length)throw new InvalidDataException("The archive was incomplete.");}
                    var identities=ModUpdateFiles.PluginIds(target);
                    if(identities.Length!=1)throw new InvalidDataException("This package does not contain exactly one identified plugin. Install it manually.");
                    plugin=identities[0];extractedForIdentity=true;
                }
                var previews=new List<StagedModSidecar>();
                var accepted=new HashSet<string>(StringComparer.OrdinalIgnoreCase){dlls[0].FullName};
                if(manifests.Length==1)
                {
                    if(plugin.Length==0)throw new InvalidDataException("A ZIP preview needs the selected plugin identity.");
                    var manifest=manifests[0];accepted.Add(manifest.FullName);var card=ModJson.ReadBytes<ModManifest>(ReadEntry(manifest,ModUpdateFiles.MaximumPreviewBytes))??throw new InvalidDataException("The ZIP preview manifest is empty.");
                    if(card.Plugin!=plugin||!string.IsNullOrEmpty(card.Bundle))throw new InvalidDataException("Automatic ZIP previews need only this exact plugin ID. Open the release page for a manual installation.");
                    string image=card.Image??"";card.Directory=Path.GetDirectoryName(Path.GetFullPath(target))!;card.Validate();
                    if(card.RequisiteError.Length>0)throw new InvalidDataException("The ZIP preview contains invalid prerequisites.");
                    if(card.Image!=image)throw new InvalidDataException("The ZIP preview image path is unsafe.");
                    if(image.Length>0)
                    {
                        SafeArchivePath(image);string prefix=manifest.FullName.Contains("/")?manifest.FullName.Substring(0,manifest.FullName.LastIndexOf('/')+1):"";
                        if(!names.TryGetValue(prefix+image,out var picture)||picture.FullName.EndsWith("/",StringComparison.Ordinal))throw new InvalidDataException("The ZIP preview image is missing.");
                        accepted.Add(picture.FullName);byte[] data=ReadEntry(picture,ModUpdateFiles.MaximumImageBytes);
                        if(!ModImageHeader.Allowed(data))throw new InvalidDataException("The ZIP preview image is invalid or too large.");
                        card.Image=Path.GetExtension(image).Equals(".png",StringComparison.OrdinalIgnoreCase)?"image.png":"image.jpg";
                        if(card.Image=="image.png"&&data[0]!=137||card.Image=="image.jpg"&&data[0]!=255)throw new InvalidDataException("The ZIP preview image format does not match its filename.");
                        previews.Add(new StagedModSidecar { Name=card.Image,Data=data });
                    }
                    using(var serialized=new MemoryStream())
                    {new DataContractJsonSerializer(typeof(ModManifest)).WriteObject(serialized,card);if(serialized.Length>ModUpdateFiles.MaximumPreviewBytes)throw new InvalidDataException("The ZIP preview manifest is too large.");previews.Add(new StagedModSidecar { Name="preview.doorman.json",Data=serialized.ToArray() });}
                }
                foreach(var entry in zip.Entries)
                {
                    if(entry.FullName.EndsWith("/",StringComparison.Ordinal)||accepted.Contains(entry.FullName))continue;
                    string name=Path.GetFileName(entry.FullName);
                    string ext=Path.GetExtension(name);
                    if((name.StartsWith("readme",StringComparison.OrdinalIgnoreCase)||name.StartsWith("license",StringComparison.OrdinalIgnoreCase)||name.StartsWith("licence",StringComparison.OrdinalIgnoreCase))&&new[]{"",".txt",".md",".rst"}.Contains(ext,StringComparer.OrdinalIgnoreCase))continue;
                    throw new InvalidDataException("This package contains additional files. Install it manually from GitHub.");
                }
                if(extractedForIdentity)return previews.ToArray();
                using(var input=dlls[0].Open()) using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    var buffer=new byte[65536];long count=0;int read;while((read=input.Read(buffer,0,buffer.Length))>0) { count+=read;if(count>ModUpdateFiles.MaximumBytes||count>dlls[0].Length) throw new InvalidDataException("The archive expanded beyond its declared size.");output.Write(buffer,0,read); }output.Flush(true);
                    if(count!=dlls[0].Length) throw new InvalidDataException("The archive was incomplete.");
                }
                return previews.ToArray();
            }
        }
        internal static void SafeArchivePath(string name)
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
        private static async Task<long> CopyBounded(Stream input,Stream output,long maximum,CancellationToken token,Action<int>? progress=null,Action<long>? bytesProgress=null)
        {
            var buffer=new byte[65536];long total=0;int read,last=-1;
            while((read=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0)
            {
                total+=read;if(total>maximum) throw new InvalidDataException("The download exceeded its declared size.");await output.WriteAsync(buffer,0,read,token).ConfigureAwait(false);bytesProgress?.Invoke(total);
                int percent=(int)(total*100/maximum);if(percent!=last) { last=percent;progress?.Invoke(percent); }
            }
            return total;
        }
        public void Dispose()=>http.Dispose();
    }
}


