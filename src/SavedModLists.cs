using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class SavedModItem
    {
        [DataMember(Name="content", Order=0, IsRequired=true)] public bool Content;
        [DataMember(Name="id", Order=1, IsRequired=true)] public string Id="";
        [DataMember(Name="name", Order=2)] public string Name="";
        [DataMember(Name="version", Order=3)] public string Version="";
        [DataMember(Name="enabled", Order=4, IsRequired=true)] public bool Enabled;

        [OnDeserializing]
        private void Deserializing(StreamingContext context) { Name=""; Version=""; }

        internal string Key => (Content ? "content:" : "plugin:") + Id;
        internal SavedModItem Copy() => new SavedModItem { Content=Content, Id=Id, Name=Name, Version=Version, Enabled=Enabled };
        internal static bool Protected(string id) => ModProfile.Doorman(id) || string.Equals(id,"com.nikkorap.blueprinter",StringComparison.OrdinalIgnoreCase);
        internal void Validate()
        {
            SavedModLists.ValidateText(Id,256,false,"mod identifier");
            if (Id.Any(c=>c=='/' || c=='\\' || c==':')) throw new InvalidDataException("Saved lists need stable mod identifiers, not paths or URLs.");
            SavedModLists.ValidateText(Name,160,true,"mod name");
            SavedModLists.ValidateText(Version,128,true,"mod version");
        }
    }

    [DataContract]
    internal sealed class SavedModList
    {
        [DataMember(Name="schema", Order=0, IsRequired=true)] public int Schema=1;
        [DataMember(Name="name", Order=1, IsRequired=true)] public string Name="";
        [DataMember(Name="gameVersion", Order=2)] public string GameVersion="";
        [DataMember(Name="mods", Order=3, IsRequired=true)] public SavedModItem[] Mods=Array.Empty<SavedModItem>();
        // The local receipt is deliberately absent from the shareable JSON.
        internal string FilePath="";

        [OnDeserializing]
        private void Deserializing(StreamingContext context) { GameVersion=""; FilePath=""; }

        internal void Validate()
        {
            if (Schema!=1 || Mods==null || Mods.Length>SavedModLists.MaximumMods) throw new InvalidDataException("Unsupported or oversized saved mod list.");
            SavedModLists.ValidateText(Name,80,false,"list name");
            SavedModLists.ValidateText(GameVersion,128,true,"game version");
            var identities=new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in Mods)
            {
                if (item==null) throw new InvalidDataException("The saved mod list contains an invalid item.");
                item.Validate();
                if (!identities.Add(item.Key)) throw new InvalidDataException("The saved mod list contains a duplicate mod identifier: "+item.Id);
                if (!item.Content && SavedModItem.Protected(item.Id) && !item.Enabled) throw new InvalidDataException("DOORMAN and Blueprinter must stay enabled.");
            }
        }
    }

    internal sealed class SavedModLists
    {
        internal const string Suffix=".doorman-list.json";
        internal const int MaximumBytes=65536, MaximumMods=512, MaximumLists=128;
        internal string DirectoryPath { get; }

        internal SavedModLists(string pluginPath)
        {
            if (string.IsNullOrWhiteSpace(pluginPath)) throw new ArgumentException("The plugin folder is required.",nameof(pluginPath));
            DirectoryPath=Path.Combine(Path.GetFullPath(pluginPath),"DOORMAN-Lists");
        }

        internal SavedModList[] Read(Action<string> log)
        {
            var lists=new List<SavedModList>();
            try
            {
                CheckDirectory();
                // Read is called when opening the picker, so the import destination exists even before the first save.
                Directory.CreateDirectory(DirectoryPath);
                CheckDirectory();
                // Enumerate only direct children and stop before an unbounded directory scan.
                var files=Directory.EnumerateFiles(DirectoryPath,"*"+Suffix,SearchOption.TopDirectoryOnly).Take(MaximumLists+1).ToArray();
                if (files.Length>MaximumLists) log?.Invoke("Saved mod list limit reached; only the first "+MaximumLists+" files were read.");
                foreach (var file in files.Take(MaximumLists))
                {
                    try
                    {
                        string path=OwnedPath(file);
                        var list=Deserialize(ReadBytes(path));
                        list.Validate();
                        list.FilePath=path;
                        lists.Add(list);
                    }
                    catch (Exception ex) { log?.Invoke("Skipped saved mod list '"+Path.GetFileName(file)+"': "+ex.Message); }
                }
            }
            catch (Exception ex) { log?.Invoke("Saved mod lists are unavailable: "+ex.Message); }
            return lists.OrderBy(l=>l.Name,StringComparer.OrdinalIgnoreCase).ThenBy(l=>l.FilePath,StringComparer.Ordinal).ToArray();
        }

        internal SavedModList Save(string name,string gameVersion,IEnumerable<SavedModItem> mods,string? replacePath=null)
        {
            if (mods==null) throw new ArgumentNullException(nameof(mods));
            var items=mods.Take(MaximumMods+1).Select(m=>m==null ? null! : m.Copy()).ToArray();
            var list=new SavedModList { Name=(name??"").Trim(), GameVersion=gameVersion, Mods=items };
            list.Validate();
            byte[] bytes=Serialize(list);
            CheckDirectory();
            Directory.CreateDirectory(DirectoryPath);
            CheckDirectory();
            string path;
            if (replacePath!=null)
            {
                path=OwnedPath(replacePath);
                if (!File.Exists(path)) throw new IOException("The selected saved list no longer exists. Save a new list instead.");
                // Do not replace a malformed or newly damaged import selected by stale UI.
                Deserialize(ReadBytes(path)).Validate();
                CheckLinks(path+".bak");
                if (Directory.Exists(path+".bak")) throw new InvalidDataException("The saved list backup path is a folder.");
            }
            else
            {
                if (Directory.EnumerateFiles(DirectoryPath,"*"+Suffix,SearchOption.TopDirectoryOnly).Take(MaximumLists).Count()>=MaximumLists)
                    throw new InvalidDataException("There are too many saved list files. Archive an old list before saving another.");
                // Even identical names receive separate files unless the caller explicitly selects replacement.
                do { path=OwnedPath(Path.Combine(DirectoryPath,Basename(list.Name)+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+Suffix)); }
                while (File.Exists(path) || Directory.Exists(path));
            }
            AtomicWrite(path,bytes,replacePath!=null,replacePath==null ? null : path+".bak");
            list.FilePath=path;
            return list;
        }

        internal void Delete(SavedModList list)
        {
            if (list==null) throw new ArgumentNullException(nameof(list));
            list.Validate();
            string path=OwnedPath(list.FilePath);
            if (!File.Exists(path)) throw new IOException("The selected saved list no longer exists.");
            byte[] bytes=ReadBytes(path);
            Deserialize(bytes).Validate();
            string backup=path+".bak";
            CheckLinks(backup);
            if (Directory.Exists(backup)) throw new InvalidDataException("The saved list backup path is a folder.");
            AtomicWrite(backup,bytes,File.Exists(backup),null);
            CheckLinks(path);
            File.Delete(path);
        }

        internal static void ValidateText(string value,int maximum,bool allowEmpty,string field)
        {
            if (value==null || value.Length>maximum || (!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Any(char.IsControl) || value!=value.Trim())
                throw new InvalidDataException("Invalid saved "+field+".");
            // Share files contain display text and stable IDs only, never machine paths or download URLs.
            if (value.Contains("://") || value.IndexOf('\\')>=0 || value.StartsWith("/",StringComparison.Ordinal) || (value.Length>=2 && char.IsLetter(value[0]) && value[1]==':'))
                throw new InvalidDataException("Saved lists cannot contain paths or URLs.");
        }

        private string OwnedPath(string value)
        {
            CheckDirectory();
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new InvalidDataException("Invalid saved list path.");
            string full=Path.GetFullPath(value);
            if (!string.Equals(Path.GetDirectoryName(full),DirectoryPath,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).EndsWith(Suffix,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Select a saved list directly inside DOORMAN-Lists.");
            CheckLinks(full);
            if (Directory.Exists(full)) throw new InvalidDataException("The saved list path is a folder.");
            return full;
        }

        private void CheckDirectory()
        {
            CheckLinks(DirectoryPath);
            if (File.Exists(DirectoryPath)) throw new InvalidDataException("DOORMAN-Lists must be a folder.");
        }

        private static void CheckLinks(string value)
        {
            for (string? path=Path.GetFullPath(value);path!=null;path=Path.GetDirectoryName(path))
            {
                try
                {
                    if ((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Saved lists through linked files or folders are unsupported.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        private static string Basename(string name)
        {
            var text=new StringBuilder();
            foreach (char c in name)
            {
                if (text.Length>=60) break;
                if (char.IsLetterOrDigit(c) || c=='-' || c=='_') text.Append(c);
                else if (text.Length>0 && text[text.Length-1]!='-') text.Append('-');
            }
            string result=text.ToString().Trim('-','_');
            return result.Length==0 ? "Mod-list" : result;
        }

        private static DataContractJsonSerializer Serializer() => new DataContractJsonSerializer(typeof(SavedModList),new DataContractJsonSerializerSettings { MaxItemsInObjectGraph=4096 });
        private static SavedModList Deserialize(byte[] bytes)
        {
            try
            {
                using (var stream=new MemoryStream(bytes,false)) return (SavedModList)(Serializer().ReadObject(stream) ?? throw new InvalidDataException("The saved mod list is empty."));
            }
            catch (Exception ex) when (ex is SerializationException || ex is System.Xml.XmlException)
            {
                // Keep malformed imports at the same validation boundary as invalid IDs and selections.
                // No inner exception: async callers use GetBaseException and should keep this readable message.
                throw new InvalidDataException("The saved mod list has invalid JSON or is missing required selection fields.");
            }
        }

        private static byte[] Serialize(SavedModList list)
        {
            using (var stream=new MemoryStream())
            {
                Serializer().WriteObject(stream,list);
                if (stream.Length>MaximumBytes) throw new InvalidDataException("The saved mod list is too large.");
                return stream.ToArray();
            }
        }

        private static byte[] ReadBytes(string path)
        {
            CheckLinks(path);
            using (var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                if (file.Length>MaximumBytes) throw new InvalidDataException("The saved mod list is too large.");
                using (var output=new MemoryStream())
                {
                    byte[] buffer=new byte[4096];
                    int count;
                    while ((count=file.Read(buffer,0,buffer.Length))!=0)
                    {
                        if (output.Length+count>MaximumBytes) throw new InvalidDataException("The saved mod list is too large.");
                        output.Write(buffer,0,count);
                    }
                    return output.ToArray();
                }
            }
        }

        private static void AtomicWrite(string path,byte[] bytes,bool replace,string? backup)
        {
            string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                CheckLinks(path);
                CheckLinks(temp);
                if (backup!=null) CheckLinks(backup);
                using (var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { output.Write(bytes,0,bytes.Length); output.Flush(true); }
                CheckLinks(path);
                if (backup!=null) CheckLinks(backup);
                if (replace) File.Replace(temp,path,backup);
                else File.Move(temp,path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    internal sealed class SavedModListPlan
    {
        internal bool CanApply;
        internal string Reason="";
        internal string[] Warnings=Array.Empty<string>();
        internal SavedModItem[] Selection=Array.Empty<SavedModItem>();
        internal string[] DisabledContent=Array.Empty<string>();

        internal static SavedModListPlan Create(SavedModList list,IEnumerable<SavedModItem> installedSnapshots)
        {
            var plan=new SavedModListPlan();
            SavedModItem[] installed;
            try
            {
                if (list==null) throw new InvalidDataException("Select a saved mod list first.");
                list.Validate();
                if (installedSnapshots==null) throw new InvalidDataException("The installed mod inventory is unavailable.");
                installed=installedSnapshots.Take(SavedModLists.MaximumMods+1).ToArray();
                if (installed.Length>SavedModLists.MaximumMods) throw new InvalidDataException("The installed mod inventory is too large.");
                foreach (var item in installed)
                {
                    if (item==null) throw new InvalidDataException("The installed mod inventory contains an invalid item.");
                    item.Validate();
                }
                if (installed.GroupBy(m=>m.Key,StringComparer.Ordinal).Any(g=>g.Count()>1)) throw new InvalidDataException("Multiple installed mods share an identifier. Resolve duplicates before loading a saved list.");
            }
            catch (Exception ex) { plan.Reason=ex.Message; return plan; }

            var available=installed.ToDictionary(m=>m.Key,StringComparer.Ordinal);
            foreach (var wanted in list.Mods.Where(m=>m.Enabled))
            {
                // The UI omits locked plugins from snapshots; an imported locked entry must not require a fictitious install.
                if (!wanted.Content && SavedModItem.Protected(wanted.Id)) continue;
                if (!available.ContainsKey(wanted.Key))
                {
                    plan.Reason="Install "+(wanted.Name.Length==0 ? wanted.Id : wanted.Name)+" "+wanted.Version+" before loading this list. No mod selections were changed.";
                    return plan;
                }
            }

            var desired=list.Mods.ToDictionary(m=>m.Key,StringComparer.Ordinal);
            var warnings=new List<string>();
            var selection=new List<SavedModItem>();
            foreach (var item in installed)
            {
                var next=item.Copy();
                desired.TryGetValue(item.Key,out var wanted);
                if (!item.Content && SavedModItem.Protected(item.Id)) next.Enabled=true;
                else if (wanted!=null)
                {
                    next.Enabled=wanted.Enabled;
                }
                else if (item.Content) next.Enabled=false;
                if (wanted!=null && wanted.Enabled && !string.Equals(wanted.Version,item.Version,StringComparison.Ordinal))
                    warnings.Add((wanted.Name.Length==0 ? wanted.Id : wanted.Name)+": saved version "+(wanted.Version.Length==0 ? "unknown" : wanted.Version)+", installed version "+(item.Version.Length==0 ? "unknown" : item.Version)+". Loading keeps the installed version.");
                selection.Add(next);
            }
            plan.Selection=selection.ToArray();
            // Absent disabled packs still need exclusion during a wrapper's next startup scan.
            plan.DisabledContent=plan.Selection.Where(m=>m.Content&&!m.Enabled).Select(m=>m.Id)
                .Concat(list.Mods.Where(m=>m.Content&&!m.Enabled).Select(m=>m.Id))
                .Distinct(StringComparer.Ordinal).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
            plan.Warnings=warnings.ToArray();
            plan.CanApply=true;
            plan.Reason=warnings.Count==0 ? "Ready to load the saved mod list." : "Review the version differences before loading this list.";
            return plan;
        }
    }
}
