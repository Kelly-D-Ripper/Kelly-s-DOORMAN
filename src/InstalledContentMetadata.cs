using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;

namespace KellysJOINCHECK
{
    // These local receipts describe content previously observed by Blueprinter.
    // They do not discover unknown bundles or replace the loader's live inventory.
    internal sealed class InstalledContentMetadata
    {
        internal const string Filename="doorman-content-inventory.json";
        internal const int MaximumRecords=512,MaximumBytes=512*1024;
        internal const long MaximumSourceBytes=256L*1024*1024;
        private readonly string pluginRoot,cacheRoot;
        private readonly object gate=new object();

        [DataContract]
        private sealed class Receipt
        {
            [DataMember(IsRequired=true)] public int Schema=1;
            [DataMember(IsRequired=true)] public ContentRecord[] Records=Array.Empty<ContentRecord>();
        }

        internal InstalledContentMetadata(string pluginRoot,string cacheRoot)
        {
            if(string.IsNullOrWhiteSpace(pluginRoot)||string.IsNullOrWhiteSpace(cacheRoot))throw new ArgumentException("Content inventory folders are required.");
            this.pluginRoot=Path.GetFullPath(pluginRoot);
            this.cacheRoot=Path.GetFullPath(cacheRoot);
        }

        internal void Save(IEnumerable<ContentRecord> records)
        {
            if(records==null)throw new ArgumentNullException(nameof(records));
            lock(gate)
            {
                string path=CachePath(Filename);
                var observed=records.Take(MaximumRecords+1).Select(r=>Normalize(r,false)).ToArray();
                CheckCountAndIds(observed);
                ContentRecord[] previous;
                try { previous=Load(path); }
                catch(Exception ex) when(MetadataFailure(ex)) { previous=Array.Empty<ContentRecord>(); }

                // A source shared by several packs is read only once in this operation.
                var hashes=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
                foreach(var item in observed)
                {
                    string? hash=Digest(item.RelativeFile,hashes);
                    if(hash==null)throw new IOException("An observed content source is missing or could not be read.");
                    item.Digest=hash;
                }
                var merged=new Dictionary<string,ContentRecord>(StringComparer.Ordinal);
                foreach(var item in previous)
                {
                    string? hash=Digest(item.RelativeFile,hashes);
                    if(hash!=null&&string.Equals(hash,item.Digest,StringComparison.OrdinalIgnoreCase))merged.Add(item.Id,item);
                }
                foreach(var item in observed)merged[item.Id]=item;
                var result=merged.Values.OrderBy(r=>r.Id,StringComparer.Ordinal).ToArray();
                CheckCountAndIds(result);
                byte[] bytes=Serialize(new Receipt { Records=result });
                Write(path,bytes);
            }
        }

        internal ContentRecord[] Read(Action<string> log)
        {
            lock(gate)
            {
                try
                {
                    var receipt=Load(CachePath(Filename));
                    var hashes=new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
                    var result=new List<ContentRecord>();
                    var skipped=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(var item in receipt)
                    {
                        string? hash=Digest(item.RelativeFile,hashes);
                        if(hash==null||!string.Equals(hash,item.Digest,StringComparison.OrdinalIgnoreCase))
                        { if(skipped.Add(item.RelativeFile))log?.Invoke("Skipped cached content whose installed source changed or is unavailable.");continue; }
                        result.Add(new ContentRecord { Id=item.Id,Name=item.Name,Version=item.Version,
                            Source=item.Source.StartsWith("file:",StringComparison.Ordinal)?SourcePath(item.RelativeFile):item.Source,
                            RelativeFile=item.RelativeFile,Digest=hash });
                    }
                    return result.ToArray();
                }
                catch(Exception ex) when(MetadataFailure(ex))
                { log?.Invoke("Cached content inventory is unavailable ("+ex.GetType().Name+"). Enable the pack once to discover it again.");return Array.Empty<ContentRecord>(); }
            }
        }

        private ContentRecord[] Load(string path)
        {
            if(!File.Exists(path))return Array.Empty<ContentRecord>();
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if(stream.Length>MaximumBytes)throw new InvalidDataException("Content inventory is too large.");
                var receipt=(Receipt?)Serializer().ReadObject(stream);
                if(receipt==null||receipt.Schema!=1||receipt.Records==null)throw new InvalidDataException("Unsupported content inventory.");
                if(receipt.Records.Length>MaximumRecords)throw new InvalidDataException("Too many cached content packs.");
                var result=receipt.Records.Select(r=>Normalize(r,true)).ToArray();
                CheckCountAndIds(result);
                // Contradictory hashes for one source cannot both be receipts for its bytes.
                if(result.GroupBy(r=>r.RelativeFile,StringComparer.OrdinalIgnoreCase).Any(g=>g.Select(r=>r.Digest).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=1))
                    throw new InvalidDataException("Conflicting content source receipts.");
                return result;
            }
        }

        private ContentRecord Normalize(ContentRecord item,bool stored)
        {
            if(item==null||!Text(item.Id,256,false)||item.Id.Any(c=>c=='/'||c=='\\'||c==':')||!Text(item.Name,256,true)||!Text(item.Version,128,true)||!Text(item.Source,2048,false)||item.RelativeFile==null)
                throw new InvalidDataException("Invalid cached content metadata.");
            string relative=item.RelativeFile.Replace('\\','/');
            string sourcePath=SourcePath(relative),source;
            if(item.Source.StartsWith("resource:",StringComparison.Ordinal))
            {
                string[] parts=item.Source.Split(':');
                if(parts.Length!=3||!Text(parts[1],256,false)||!Text(parts[2],1536,false)||parts[1].Any(c=>c=='/'||c=='\\')||parts[2].Any(c=>c=='/'||c=='\\')||!parts[2].EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)||!relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid embedded content source.");
                source=item.Source;
            }
            else
            {
                if(!relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Standalone content needs a .nobp source.");
                if(stored)
                { if(item.Source!="file:"+relative)throw new InvalidDataException("Invalid standalone content receipt."); }
                else
                { if(!Path.IsPathRooted(item.Source)||!Path.GetFullPath(item.Source).Equals(sourcePath,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Standalone content does not match its installed path."); }
                source="file:"+relative;
            }
            if(stored&&!ModUpdateFiles.HashValid(item.Digest))throw new InvalidDataException("Invalid content source hash.");
            return new ContentRecord { Id=item.Id,Name=item.Name,Version=item.Version,Source=source,RelativeFile=relative,Digest=stored?item.Digest.ToLowerInvariant():"" };
        }

        private string SourcePath(string relative)
        {
            string path=ModUpdateFiles.Within(pluginRoot,relative);
            if(!(relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Unsupported cached content file.");
            if(relative.Split('/','\\').Any(UnsafeSegment))throw new InvalidDataException("Invalid cached content path.");
            return path;
        }

        private static bool UnsafeSegment(string segment)
        {
            if(segment.EndsWith(".",StringComparison.Ordinal)||segment.EndsWith(" ",StringComparison.Ordinal)||segment.IndexOfAny(new[]{'<','>','"','|','?','*'})>=0)return true;
            string stem=segment.Split('.')[0].ToUpperInvariant();
            return stem=="CON"||stem=="PRN"||stem=="AUX"||stem=="NUL"||stem.Length==4&&(stem.StartsWith("COM",StringComparison.Ordinal)||stem.StartsWith("LPT",StringComparison.Ordinal))&&stem[3]>='1'&&stem[3]<='9';
        }

        private string? Digest(string relative,Dictionary<string,string?> hashes)
        {
            if(hashes.TryGetValue(relative,out var cached))return cached;
            string? result=null;
            try
            {
                string path=SourcePath(relative);
                using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    if(stream.Length>MaximumSourceBytes)throw new InvalidDataException("Cached content source is too large.");
                    using(var sha=SHA256.Create())result=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                }
            }
            catch(Exception ex) when(MetadataFailure(ex)) { }
            hashes.Add(relative,result);
            return result;
        }

        private string CachePath(string relative)
        {
            string path=ModUpdateFiles.Within(cacheRoot,relative);
            if(Directory.Exists(path))throw new InvalidDataException("A content inventory file is a folder.");
            return path;
        }

        private void Write(string path,byte[] bytes)
        {
            CachePath(Filename);CachePath(Filename+".bak");
            Directory.CreateDirectory(cacheRoot);
            CachePath(Filename);
            string temporary=CachePath(Filename+"."+Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { stream.Write(bytes,0,bytes.Length);stream.Flush(true); }
                CachePath(Filename);CachePath(Filename+".bak");
                if(File.Exists(path))File.Replace(temporary,path,path+".bak");else File.Move(temporary,path);
            }
            finally { if(File.Exists(temporary)) { CachePath(Path.GetFileName(temporary));File.Delete(temporary); } }
        }

        private static byte[] Serialize(Receipt receipt)
        {
            using(var stream=new MemoryStream())
            { Serializer().WriteObject(stream,receipt);if(stream.Length>MaximumBytes)throw new InvalidDataException("Content inventory is too large.");return stream.ToArray(); }
        }
        private static DataContractJsonSerializer Serializer()=>new DataContractJsonSerializer(typeof(Receipt),new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=8192 });
        private static bool Text(string value,int maximum,bool empty)=>value!=null&&value.Length<=maximum&&(empty||!string.IsNullOrWhiteSpace(value))&&value==value.Trim()&&!value.Any(char.IsControl);
        private static void CheckCountAndIds(ContentRecord[] records)
        { if(records.Length>MaximumRecords||records.Select(r=>r.Id).Distinct(StringComparer.Ordinal).Count()!=records.Length)throw new InvalidDataException("Too many or duplicate cached content packs."); }
        private static bool MetadataFailure(Exception ex)=>ex is IOException||ex is InvalidDataException||ex is UnauthorizedAccessException||ex is SerializationException||ex is ArgumentException||ex is NotSupportedException||ex is System.Security.SecurityException||ex is System.Xml.XmlException;
    }
}
