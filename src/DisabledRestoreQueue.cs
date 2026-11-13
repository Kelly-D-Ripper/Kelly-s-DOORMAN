using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace KellysJOINCHECK
{
    [DataContract] internal sealed class DisabledRestoreQueueDocument
    {
        [DataMember] public int Schema=1;
        [DataMember] public long CreatedUtc=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        [DataMember] public DisabledModRestore[] Restores=Array.Empty<DisabledModRestore>();
        internal void Validate()
        {
            if(Schema!=1)throw new InvalidDataException("The saved NOMM restore queue is unsupported. Review the mod selection before restarting.");
            DisabledRestoreQueue.ValidateRecords(Restores);
            // Older versions saved an empty receipt for every restart. It has
            // no actions to authorise, so an overnight delay must not block
            // unrelated updates. Actual restore intent still expires.
            long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if(Restores.Length>0&&(CreatedUtc>now+5*60||CreatedUtc<now-6*60*60))throw new InvalidDataException("The saved NOMM restore queue expired or is unsupported. Review the mod selection before restarting.");
        }
    }

    internal static class DisabledRestoreQueue
    {
        internal const string Filename="doorman-disabled-restore.json";
        internal const int MaximumBytes=65536;
        private static readonly object QueueLock=new object();

        internal static void Save(string configRoot,DisabledModRestore[] restores)
        {
            lock(QueueLock)
            {
                var document=new DisabledRestoreQueueDocument {Restores=restores};document.Validate();
                if(restores.Length==0){Clear(configRoot);return;}
                byte[] bytes;
                using(var data=new MemoryStream())
                {
                    new DataContractJsonSerializer(typeof(DisabledRestoreQueueDocument)).WriteObject(data,document);
                    if(data.Length>MaximumBytes)throw new InvalidDataException("The NOMM restore selection is too large to save.");
                    bytes=data.ToArray();
                }
                string main=PathFor(configRoot,Filename),temporary=PathFor(configRoot,Filename+".tmp"),backup=PathFor(configRoot,Filename+".bak");
                Directory.CreateDirectory(configRoot);
                // Check again after creating the configured directory. Never
                // replace a linked main/temp/backup path or another directory.
                main=PathFor(configRoot,Filename);temporary=PathFor(configRoot,Filename+".tmp");backup=PathFor(configRoot,Filename+".bak");
                if(Directory.Exists(main)||Directory.Exists(temporary)||Directory.Exists(backup))throw new IOException("A directory occupies the NOMM restore receipt path.");
                using(var output=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
                {output.Write(bytes,0,bytes.Length);output.Flush(true);}
                if(File.Exists(main))File.Replace(temporary,main,backup);else File.Move(temporary,main);
            }
        }

        internal static DisabledModRestore[] Read(string configRoot)
        {
            lock(QueueLock)
            {
                string main=PathFor(configRoot,Filename);
                if(Directory.Exists(main))throw new IOException("A directory occupies the NOMM restore receipt path.");
                if(!File.Exists(main))return Array.Empty<DisabledModRestore>();
                try
                {
                    using(var input=new FileStream(main,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        if(input.Length>MaximumBytes)throw new InvalidDataException("The NOMM restore receipt is too large.");
                        var document=new DataContractJsonSerializer(typeof(DisabledRestoreQueueDocument)).ReadObject(input) as DisabledRestoreQueueDocument;
                        if(document==null)throw new InvalidDataException("The NOMM restore receipt is empty.");
                        document.Validate();return document.Restores;
                    }
                }
                catch(SerializationException ex){throw new InvalidDataException("The NOMM restore receipt could not be read. Review the mod selection before restarting.",ex);}
            }
        }

        internal static void Clear(string configRoot)
        {
            lock(QueueLock)
            {
                string main=PathFor(configRoot,Filename),temporary=PathFor(configRoot,Filename+".tmp");
                if(Directory.Exists(main)||Directory.Exists(temporary))throw new IOException("A directory occupies the NOMM restore receipt path.");
                // Keep .bak as explicit recovery material; never activate it
                // implicitly when the main queue is absent or corrupt.
                try{File.Delete(temporary);}catch(DirectoryNotFoundException){/* No receipt directory is already clear. */}
                try{File.Delete(main);}catch(DirectoryNotFoundException){/* No receipt directory is already clear. */}
            }
        }

        internal static void Apply(string configRoot,string bepRoot,Action<string>? log=null)
        {
            lock(QueueLock)
            {
                var restores=Read(configRoot);
                if(restores.Length>0)DisabledModFiles.Apply(bepRoot,restores,log);
                Clear(configRoot);
            }
        }

        // The caller verifies the whole receipt set before building its startup
        // inventory. This helper maps a known target to the exact disabled bytes
        // without hashing every installed mod a second time.
        internal static string? SourceFor(string bepRoot,IEnumerable<DisabledModRestore> restores,string targetRelative)
        {
            if(restores==null)throw new InvalidDataException("No NOMM restore receipts were supplied.");
            var records=restores.Take(DisabledModFiles.MaximumUnits+1).ToArray();ValidateRecords(records);
            DisabledModFiles.ValidateTarget(targetRelative);
            if(string.IsNullOrWhiteSpace(bepRoot)||!Path.IsPathRooted(bepRoot)||!Directory.Exists(bepRoot))throw new InvalidDataException("BepInEx needs an existing absolute directory.");
            string plugins=ModUpdateFiles.Within(bepRoot,"plugins"),disabled=ModUpdateFiles.Within(bepRoot,"disabledPlugins");
            Guard(plugins);Guard(disabled);Guard(ModUpdateFiles.Within(plugins,targetRelative));
            string target=Canonical(targetRelative);
            var matches=records.Where(r=>target.Equals(Canonical(r.TargetRelative),StringComparison.OrdinalIgnoreCase)||target.StartsWith(Canonical(r.TargetRelative)+"/",StringComparison.OrdinalIgnoreCase)).OrderByDescending(r=>Canonical(r.TargetRelative).Length).ToArray();
            if(matches.Length==0)return null;
            var selected=matches[0];string unit=ModUpdateFiles.Within(disabled,selected.SourceRelative);
            Guard(unit);
            if(!File.Exists(unit)&&!Directory.Exists(unit))return null;
            string suffix=target.Substring(Canonical(selected.TargetRelative).Length).TrimStart('/');
            if(suffix.Length>0&&!Directory.Exists(unit))throw new InvalidDataException("A disabled DLL cannot supply a nested restore source.");
            string source=suffix.Length==0?unit:ModUpdateFiles.Within(unit,suffix);
            Guard(source);
            if(!File.Exists(source))throw new IOException("The selected NOMM-disabled content source is missing or is a directory.");
            InstalledFilePolicy.ValidateLength(new FileInfo(source).Length);
            return source;
        }

        internal static void ValidateRecords(DisabledModRestore[] restores)
        {
            if(restores==null||restores.Length>DisabledModFiles.MaximumUnits||restores.Any(r=>r==null))throw new InvalidDataException("Invalid NOMM restore selection.");
            foreach(var restore in restores)restore.Validate();
            if(restores.GroupBy(r=>r.SourceRelative,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||restores.GroupBy(r=>Canonical(r.TargetRelative),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("The NOMM restore queue contains duplicate units or destinations.");
        }

        private static string PathFor(string root,string leaf)
        {
            if(string.IsNullOrWhiteSpace(root)||!Path.IsPathRooted(root))throw new InvalidDataException("The NOMM restore queue needs an absolute config directory.");
            string path=ModUpdateFiles.Within(root,leaf);Guard(path);return path;
        }
        private static void Guard(string path)
        {
            // Attributes also identify dangling links that Exists() reports as
            // absent. A queue write must never follow one to an external file.
            for(string? current=path;current!=null;current=Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try {attributes=File.GetAttributes(current);}
                catch(FileNotFoundException){continue;}
                catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked NOMM restore paths are unsupported.");
            }
        }
        private static string Canonical(string relative)=>relative.Replace('\\','/');
    }
}
