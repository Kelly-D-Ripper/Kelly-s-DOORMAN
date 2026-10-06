using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace KellysJOINCHECK
{
    internal sealed class NommMetadataService : IDisposable
    {
        internal const string RegistryUrl="https://raw.githubusercontent.com/KopterBuzz/NOMNOM/main/manifest/manifest.json";
        private readonly string cache,localCache,imageRoot;
        private readonly HttpClient http;
        private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        private NommRecord[] records=Array.Empty<NommRecord>();
        private DateTime nextCheck;
        internal NommMetadataService(string cacheRoot,string? nommCache=null,HttpMessageHandler? handler=null)
        {
            cache=Path.Combine(cacheRoot,"doorman-nomnom.json");imageRoot=Path.Combine(cacheRoot,"DOORMAN-previews");
            localCache=nommCache??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"NOMM","manifest.json");
            http=new HttpClient(handler??new HttpClientHandler { AllowAutoRedirect=false });http.Timeout=TimeSpan.FromSeconds(15);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Kellys-DOORMAN/1.3.4");
        }
        internal async Task<NommRecord[]> GetAsync(CancellationToken token)
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if(DateTime.UtcNow<nextCheck) return records;
                foreach(var path in new[]{cache,localCache})
                {
                    try { if(File.Exists(path)&&new FileInfo(path).Length<=NommMetadata.MaxBytes) { records=NommMetadata.Parse(File.ReadAllBytes(path));break; } }catch { }
                }
                if(File.Exists(cache)&&File.GetLastWriteTimeUtc(cache)>DateTime.UtcNow.AddHours(-24)&&records.Length>0) { nextCheck=DateTime.UtcNow.AddHours(1);return records; }
                nextCheck=DateTime.UtcNow.AddMinutes(15);
                try
                {
                    var bytes=await Fetch(RegistryUrl,NommMetadata.MaxBytes,token).ConfigureAwait(false);
                    var fresh=NommMetadata.Parse(bytes);if(fresh.Length==0) return records;
                    records=fresh;Directory.CreateDirectory(Path.GetDirectoryName(cache)!);string temp=cache+".tmp";File.WriteAllBytes(temp,bytes);
                    if(File.Exists(cache)) File.Replace(temp,cache,null);else File.Move(temp,cache);
                    nextCheck=DateTime.UtcNow.AddHours(24);
                }
                catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
                catch { /* The local index stays usable offline. */ }
                return records;
            }
            finally { gate.Release(); }
        }
        internal async Task<string?> ImageAsync(string url,CancellationToken token)
        {
            if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length>0||!(uri.Host=="raw.githubusercontent.com"||uri.Host=="user-images.githubusercontent.com"||uri.Host=="media.githubusercontent.com")) return null;
            string extension=Path.GetExtension(uri.AbsolutePath);if(!new[]{".png",".jpg",".jpeg"}.Contains(extension,StringComparer.OrdinalIgnoreCase)) return null;
            string key;using(var sha=SHA256.Create()) key=BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(url))).Replace("-","");
            string? temporary=null;
            try
            {
                string path=ModUpdateFiles.Within(imageRoot,key+extension);
                if(File.Exists(path))
                {
                    try { if(ModImageHeader.Allowed(ModUpdateFiles.ReadBounded(path,8*1024*1024)))return path; }
                    catch(InvalidDataException) { }catch(IOException) { }catch(UnauthorizedAccessException) { }
                }
                var bytes=await Fetch(url,8*1024*1024,token).ConfigureAwait(false);if(!ModImageHeader.Allowed(bytes)) return null;
                Directory.CreateDirectory(imageRoot);path=ModUpdateFiles.Within(imageRoot,key+extension);
                temporary=ModUpdateFiles.Within(imageRoot,key+"."+Guid.NewGuid().ToString("N")+".tmp");
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(bytes,0,bytes.Length);output.Flush(true);}
                if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
                temporary=null;return path;
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }catch { return null; }
            finally { if(temporary!=null)try { if(File.Exists(temporary))File.Delete(temporary); }catch(IOException) { }catch(UnauthorizedAccessException) { } }
        }
        private async Task<byte[]> Fetch(string url,int limit,CancellationToken token)
        {
            using(var response=await http.GetAsync(url,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();if(response.Content.Headers.ContentLength>limit) throw new InvalidDataException("Metadata download is too large.");
                using(var stream=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))using(var output=new MemoryStream())
                { var buffer=new byte[16384];int count;while((count=await stream.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0) { if(output.Length+count>limit) throw new InvalidDataException("Metadata download is too large.");output.Write(buffer,0,count); }return output.ToArray(); }
            }
        }
        public void Dispose(){http.Dispose();}
    }
}
