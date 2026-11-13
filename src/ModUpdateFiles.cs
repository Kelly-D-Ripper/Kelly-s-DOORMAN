using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class PendingModUpdate
    {
        [DataMember] public int Schema=1;
        [DataMember] public string Plugin="", Repository="", Version="", Target="", OldHash="", NewHash="", State="pending", Message="";
        [DataMember(EmitDefaultValue=false)] public PendingModSidecar[]? Sidecars;
        [DataMember(EmitDefaultValue=false)] public string? MinimumVersion;
        [DataMember(EmitDefaultValue=false)] public string? PluginVersion;
        [DataMember(EmitDefaultValue=false)] public PendingPackageFile[]? PackageFiles;
        [DataMember(EmitDefaultValue=false)] public string? PackageSourceHash;
        [DataMember(EmitDefaultValue=false)] public string? PackageRecordId;
        [DataMember(EmitDefaultValue=false)] public string? PackageDllName;
        internal bool Install=>Schema==3||Schema==4||Schema==5;
        internal bool Package=>Schema==5||Schema==6;
        internal string Job="";
        internal void Validate()
        {
            if((Schema!=1&&Schema!=2&&Schema!=3&&Schema!=4&&Schema!=5&&Schema!=6)||!GitHubRepository.Valid(Repository)||!ModUpdateFiles.PluginIdentifierValid(Plugin)||ModReleaseVersion.Compare(Version,Version)!=0||(Install?OldHash!="":!ModUpdateFiles.HashValid(OldHash))||!ModUpdateFiles.HashValid(NewHash)||!new[]{"pending","applying","complete","failed","cancelled"}.Contains(State)) throw new InvalidDataException("Invalid pending update.");
            if(ModUpdateFiles.ProtectedPlugin(Plugin)) throw new InvalidDataException("This loader/plugin needs its own installation procedure.");
            if(Install&&((Package?Target!=ModUpdateFiles.PackageInstallTarget(Plugin,PackageDllName??""):Target!=ModUpdateFiles.InstallTarget(Plugin))||!string.IsNullOrEmpty(MinimumVersion)&&ModReleaseVersion.Compare(Version,MinimumVersion)<0||!string.IsNullOrEmpty(MinimumVersion)&&ModReleaseVersion.Compare(MinimumVersion,MinimumVersion)!=0))throw new InvalidDataException("Invalid prerequisite installation.");
            if(!string.IsNullOrEmpty(MinimumVersion)&&(ModReleaseVersion.Compare(MinimumVersion,MinimumVersion)!=0||string.IsNullOrEmpty(PluginVersion)||ModReleaseVersion.Compare(PluginVersion,MinimumVersion)<0||ModReleaseVersion.Compare(PluginVersion,PluginVersion)!=0))throw new InvalidDataException("Invalid prerequisite version requirement.");
            if(!string.IsNullOrEmpty(PluginVersion)&&ModReleaseVersion.Compare(PluginVersion,PluginVersion)!=0)throw new InvalidDataException("Invalid queued plugin version.");
            if(Install&&PluginVersion!=Version)throw new InvalidDataException("Invalid installed prerequisite version.");
            var files=Sidecars??Array.Empty<PendingModSidecar>();
            if(Schema==1&&files.Length!=0||Schema==2&&(files.Length<1||files.Length>2)||files.Any(f=>f==null)||files.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Length||files.Length>0&&files.Any(f=>f.Remove)!=files.All(f=>f.Remove))throw new InvalidDataException("Invalid update preview files.");
            foreach(var file in files)file.Validate(Plugin);
            if(Schema==2&&files.Count(f=>f.Name=="preview.doorman.json")!=1)throw new InvalidDataException("An update preview needs one manifest.");
            if(Install&&(files.Length>2||files.Any(f=>f.Remove||f.OldHash.Length>0)||files.Length>0&&files.Count(f=>f.Name=="preview.doorman.json")!=1))throw new InvalidDataException("Invalid prerequisite preview files.");
            var parts=PackageFiles??Array.Empty<PendingPackageFile>();
            if(Package)
            {
                if(Target!=ModUpdateFiles.PackageInstallTarget(Plugin,PackageDllName??"")||files.Length!=0||!ModUpdateFiles.HashValid(PackageSourceHash??"")||!ModUpdateFiles.PackageRecordValid(PackageRecordId??"")||parts.Length<1||parts.Length>128||parts.Any(f=>f==null)||parts.Select(f=>f.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=parts.Length||parts.Select(f=>f.Index).Distinct().Count()!=parts.Length||parts.Count(f=>!f.Remove&&f.Relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))!=1)throw new InvalidDataException("Invalid gameplay package receipt.");
                foreach(var part in parts)part.Validate();
                if(Install&&parts.Any(f=>f.Remove||f.OldHash.Length>0))throw new InvalidDataException("A new package cannot replace existing files.");
            }
            else if(parts.Length!=0||!string.IsNullOrEmpty(PackageSourceHash)||!string.IsNullOrEmpty(PackageRecordId)||!string.IsNullOrEmpty(PackageDllName))throw new InvalidDataException("Unexpected gameplay package files.");
        }
    }
    [DataContract] internal sealed class PendingPackageFile
    {
        [DataMember] public int Index;
        [DataMember] public string Relative="",OldHash="",NewHash="",ContentId="",ContentVersion="";
        [DataMember(EmitDefaultValue=false)] public bool Remove;
        internal string Payload=>"package-"+Index+".bin";
        internal string Backup=>"package-"+Index+".previous";
        internal string Target(string dllTarget)=>Path.Combine(Path.GetDirectoryName(dllTarget)!,Relative.Replace('/',Path.DirectorySeparatorChar));
        internal void Validate()
        {
            ModUpdateFiles.SafePackageRelative(Relative);
            string ext=Path.GetExtension(Relative);
            if(Index<0||Index>=128||!(ext.Equals(".nobp",StringComparison.OrdinalIgnoreCase)||ext.Equals(".png",StringComparison.OrdinalIgnoreCase)||ext.Equals(".jpg",StringComparison.OrdinalIgnoreCase)||ext.Equals(".jpeg",StringComparison.OrdinalIgnoreCase))||OldHash==null||OldHash.Length>0&&!ModUpdateFiles.HashValid(OldHash)||(Remove?OldHash.Length==0||NewHash!="":!ModUpdateFiles.HashValid(NewHash)))throw new InvalidDataException("Invalid gameplay package file.");
            if(ext.Equals(".nobp",StringComparison.OrdinalIgnoreCase)&&(!ModUpdateFiles.ContentText(ContentId,256)||!ModUpdateFiles.ContentText(ContentVersion,128)))throw new InvalidDataException("Invalid gameplay content identity.");
            if(!ext.Equals(".nobp",StringComparison.OrdinalIgnoreCase)&&(ContentId.Length>0||ContentVersion.Length>0))throw new InvalidDataException("Unexpected gameplay content identity.");
        }
    }
    [DataContract]
    internal sealed class PendingModSidecar
    {
        [DataMember] public string Name="",OldHash="",NewHash="";
        [DataMember(EmitDefaultValue=false)] public bool Remove;
        internal string Payload=>Name=="preview.doorman.json"?"preview.payload.bin":"image.payload.bin";
        internal string Backup=>Name=="preview.doorman.json"?"preview.previous.bin":"image.previous.bin";
        internal string Target(string plugin)=>Path.Combine(ModUpdateFiles.MetadataDirectory(plugin),Name);
        internal void Validate(string plugin)
        {
            if(!(Name=="preview.doorman.json"||Name=="image.png"||Name=="image.jpg")||OldHash==null||OldHash.Length>0&&!ModUpdateFiles.HashValid(OldHash)||(Remove?OldHash.Length==0||NewHash!="":!ModUpdateFiles.HashValid(NewHash)))throw new InvalidDataException("Invalid update preview file.");
        }
    }
    internal sealed class StagedModSidecar
    {
        internal string Name="";
        internal byte[] Data=Array.Empty<byte>();
    }
    internal sealed class StagedPackageFile
    {
        internal string Relative="",Path="",Hash="",ContentId="",ContentVersion="";
    }
    internal static class ModUpdateFiles
    {
        internal const long MaximumBytes=256L*1024*1024;
        internal const int MaximumPendingJobs=128;
        internal const int MaximumPreviewBytes=65536,MaximumImageBytes=12*1024*1024;
        internal static bool PluginIdentifierValid(string? plugin)=>!string.IsNullOrWhiteSpace(plugin)&&plugin!.Length<=256&&!plugin.Any(char.IsControl);
        internal static bool ProtectedPlugin(string plugin)=>ModProfile.Doorman(plugin)||plugin.Equals("com.nikkorap.blueprinter",StringComparison.OrdinalIgnoreCase);
        internal static string InstallTarget(string plugin)
        {
            if(!PluginIdentifierValid(plugin))throw new InvalidDataException("Invalid prerequisite plugin identifier.");
            // Keep loaders beside existing plugins. Some resolve their map folders
            // relative to their DLL, so a dependency-specific subfolder breaks them.
            using(var sha=SHA256.Create())return "DOORMAN-dependency-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(plugin))).Replace("-","").ToLowerInvariant()+".dll";
        }
        internal static string MetadataDirectory(string plugin)
        {
            if(string.IsNullOrWhiteSpace(plugin)||plugin.Length>256||plugin.Any(char.IsControl))throw new InvalidDataException("Invalid preview plugin identifier.");
            using(var sha=SHA256.Create())return Path.Combine("DOORMAN-Metadata",BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(plugin))).Replace("-","").ToLowerInvariant());
        }
        internal static string PackageInstallTarget(string plugin,string dllName)
        {
            SafePackageRelative(dllName);if(dllName.Contains('/')||!dllName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid gameplay plugin filename.");
            return Path.Combine("DOORMAN-Packages",Path.GetFileNameWithoutExtension(InstallTarget(plugin)).Substring("DOORMAN-dependency-".Length),dllName);
        }
        internal static bool ManagedPackagePath(string root,string path)
        {
            string basis=Path.GetFullPath(Path.Combine(root,"DOORMAN-Packages")).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(basis,StringComparison.OrdinalIgnoreCase);
        }
        internal static bool ContentText(string value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=maximum&&value==value.Trim()&&!value.Any(char.IsControl)&&!value.Any(c=>c=='/'||c=='\\'||c==':'||c=='|'||c=='#');
        internal static bool PackageRecordValid(string value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=256&&value==value.Trim()&&!value.Any(char.IsControl);
        internal static void SafePackageRelative(string name)
        {
            if(string.IsNullOrEmpty(name)||name.Length>500||name.StartsWith("/",StringComparison.Ordinal)||name.Contains("\\")||name.Any(c=>char.IsControl(c)||"<>:\"|?*".Contains(c)))throw new InvalidDataException("Invalid package path.");
            foreach(string part in name.Split('/'))
            {
                string device=part.Split('.')[0];
                if(part.Length==0||part=="."||part==".."||part.EndsWith(".",StringComparison.Ordinal)||part.EndsWith(" ",StringComparison.Ordinal)||new[]{"CON","PRN","AUX","NUL"}.Contains(device,StringComparer.OrdinalIgnoreCase)||device.Length==4&&(device.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||device.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&device[3]>='1'&&device[3]<='9')throw new InvalidDataException("Invalid package path.");
            }
        }
        internal static bool HashValid(string hash)=>hash!=null&&hash.Length==64&&hash.All(c=>c>='0'&&c<='9'||c>='a'&&c<='f'||c>='A'&&c<='F');
        internal static string Hash(string path) { using(var stream=File.OpenRead(path)) using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant(); }
        internal static string Within(string root,string relative)
        {
            if(string.IsNullOrWhiteSpace(relative)||relative.Length>500||Path.IsPathRooted(relative)||relative.Any(c=>char.IsControl(c)||c==':')||relative.Split('/','\\').Any(p=>p==".."||p=="."||p.Length==0)) throw new InvalidDataException("Invalid update path.");
            string basis=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar), full=Path.GetFullPath(Path.Combine(basis,relative));
            if(!full.StartsWith(basis+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update path escapes its folder.");
            // Walk all existing ancestors, including the configured root itself.
            for(string? path=full;path!=null;path=Path.GetDirectoryName(path))
            {
                // Exists() returns false for a dangling symbolic link. Its
                // attributes still identify the link, and a later Create write
                // must never follow it to a presently missing external file.
                FileAttributes attributes;
                try {attributes=File.GetAttributes(path);}
                catch(FileNotFoundException){continue;}
                catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Updates through linked folders are unsupported.");
            }
            return full;
        }
        internal static string RelativeTarget(string pluginRoot,string path)
        {
            string root=Path.GetFullPath(pluginRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(path); if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The installed mod is outside the plugin folder.");
            string relative=full.Substring(root.Length); Within(pluginRoot,relative);
            if(!relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("This format needs a manual update.");
            return relative;
        }
        internal static string[] PluginIds(string path)
        {
            if(new FileInfo(path).Length>MaximumBytes) throw new InvalidDataException("Mod DLL is too large.");
            using(var assembly=AssemblyDefinition.ReadAssembly(path))
                return Types(assembly.MainModule.Types).SelectMany(t=>t.CustomAttributes).Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin"&&a.ConstructorArguments.Count==3).Select(a=>(string)a.ConstructorArguments[0].Value).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
        }
        internal static void VerifyIdentity(string oldPath,string newPath,string plugin)
        {
            var oldIds=PluginIds(oldPath); var newIds=PluginIds(newPath);
            if(oldIds.Length==0||oldIds.Length>16||!oldIds.Contains(plugin,StringComparer.Ordinal)||oldIds.Any(ProtectedPlugin)||!oldIds.SequenceEqual(newIds,StringComparer.Ordinal)) throw new InvalidDataException("The download's plugin IDs do not match this installed mod.");
        }
        internal static string VerifyInstallIdentity(string path,string plugin,string minimumVersion)
        {
            if(!PluginIdentifierValid(plugin)||ProtectedPlugin(plugin)||!string.IsNullOrEmpty(minimumVersion)&&ModReleaseVersion.Compare(minimumVersion,minimumVersion)!=0)throw new InvalidDataException("This prerequisite needs its own installation procedure.");
            if(new FileInfo(path).Length>MaximumBytes)throw new InvalidDataException("Prerequisite DLL is too large.");
            using(var assembly=AssemblyDefinition.ReadAssembly(path))
            {
                var attributes=Types(assembly.MainModule.Types).SelectMany(t=>t.CustomAttributes).Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin"&&a.ConstructorArguments.Count==3).Take(2).ToArray();
                if(attributes.Length!=1||!(attributes[0].ConstructorArguments[0].Value is string id)||id!=plugin||!(attributes[0].ConstructorArguments[2].Value is string version)||ModReleaseVersion.Compare(version,version)!=0)throw new InvalidDataException("The download must contain only the exact required plugin ID.");
                if(minimumVersion.Length>0&&ModReleaseVersion.Compare(version,minimumVersion)<0)throw new InvalidDataException("The downloaded plugin is older than the required version. Open its release page.");
                return version;
            }
        }
        internal static string VerifyPluginVersion(string path,string plugin,string minimumVersion)
        {
            if(!PluginIdentifierValid(plugin)||ProtectedPlugin(plugin)||!string.IsNullOrEmpty(minimumVersion)&&ModReleaseVersion.Compare(minimumVersion,minimumVersion)!=0)throw new InvalidDataException("Invalid prerequisite version requirement.");
            if(new FileInfo(path).Length>MaximumBytes)throw new InvalidDataException("Prerequisite DLL is too large.");
            using(var assembly=AssemblyDefinition.ReadAssembly(path))
            {
                var attributes=Types(assembly.MainModule.Types).SelectMany(t=>t.CustomAttributes).Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin"&&a.ConstructorArguments.Count==3&&a.ConstructorArguments[0].Value is string id&&id==plugin).Take(2).ToArray();
                if(attributes.Length!=1||!(attributes[0].ConstructorArguments[2].Value is string version)||ModReleaseVersion.Compare(version,version)!=0)throw new InvalidDataException("The required plugin has no unambiguous version in this download.");
                if(minimumVersion.Length>0&&ModReleaseVersion.Compare(version,minimumVersion)<0)throw new InvalidDataException("The downloaded plugin is older than the required version. Open its release page.");
                return version;
            }
        }
        internal static string[] VerifyPackageIdentities(string path,string primary)
        {
            var ids=PluginIds(path);
            if(ids.Length<2||ids.Length>16||ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length||!ids.Contains(primary,StringComparer.Ordinal)||ids.Any(id=>!PluginIdentifierValid(id)||ProtectedPlugin(id)))throw new InvalidDataException("The package has invalid or protected plugin identities.");
            foreach(string id in ids)VerifyPluginVersion(path,id,"");
            return ids;
        }
        internal static void EnsurePluginAbsent(string pluginRoot,string plugin,string allowedTarget="")
        {
            if(!Directory.Exists(pluginRoot))return;
            var folders=new Stack<string>();folders.Push(Path.GetFullPath(pluginRoot));int files=0,dirs=0;
            while(folders.Count>0)
            {
                string directory=folders.Pop();if(++dirs>4096)throw new InvalidDataException("Too many plugin folders. Install this prerequisite manually.");
                // Linked folders are not followed. A DLL at a linked path cannot
                // safely be ruled out, so installation requires manual review.
                if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked plugin folders need a manual prerequisite installation.");
                foreach(string path in Directory.EnumerateFiles(directory,"*",SearchOption.TopDirectoryOnly).Where(p=>p.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)))
                {
                    if(++files>4096)throw new InvalidDataException("Too many plugin DLLs. Install this prerequisite manually.");
                    string relative=RelativeTarget(pluginRoot,path);string checkedPath=Within(pluginRoot,relative);
                    if(allowedTarget.Length>0&&checkedPath.Equals(allowedTarget,StringComparison.OrdinalIgnoreCase))continue;
                    string[] ids;
                    try{ids=PluginIds(checkedPath);}catch(BadImageFormatException){continue;}
                    if(ids.Contains(plugin,StringComparer.Ordinal))throw new IOException("This prerequisite is already installed. Enable or update its existing entry instead.");
                }
                foreach(string child in Directory.EnumerateDirectories(directory))folders.Push(child);
            }
        }
        private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> roots) { foreach(var type in roots) { yield return type; foreach(var child in Types(type.NestedTypes)) yield return child; } }
        internal static IEnumerable<string> Jobs(string updateRoot)
        {
            if(!Directory.Exists(updateRoot)) return Array.Empty<string>();
            return Directory.EnumerateDirectories(updateRoot).Where(p=>Guid.TryParseExact(Path.GetFileName(p),"N",out _)).Select(p=>Within(updateRoot,Path.GetFileName(p))).ToArray();
        }
        private static PendingModUpdate[] ActivePlans(string updateRoot)
        {
            var result=new List<PendingModUpdate>();
            foreach(var job in Jobs(updateRoot))
            {
                string file=Within(job,"plan.json");if(!File.Exists(file))continue;
                var plan=UpdateJson.ReadFile<PendingModUpdate>(file);
                // Terminal receipts and their rollback files remain at their
                // original locations, but never occupy active queue capacity.
                if(plan.State=="complete"||plan.State=="failed"||plan.State=="cancelled")continue;
                plan.Validate();plan.Job=job;result.Add(plan);
                if(result.Count>MaximumPendingJobs)throw new InvalidDataException("Too many pending mod updates. Review the active update queue before restarting.");
            }
            return result.ToArray();
        }
        internal static PendingModUpdate? PendingFor(string updateRoot,string target)
        {
            return ActivePlans(updateRoot).FirstOrDefault(plan=>string.Equals(plan.Target,target,StringComparison.OrdinalIgnoreCase));
        }
        internal static PendingModUpdate? PendingPlugin(string updateRoot,string plugin)=>ActivePlans(updateRoot).FirstOrDefault(plan=>plan.Plugin==plugin);
        internal static PendingModUpdate[] ReadPending(string updateRoot,string pluginRoot)
        {
            var result=ActivePlans(updateRoot);
            foreach(var plan in result)RelativeTarget(pluginRoot,Within(pluginRoot,plan.Target));
            if(result.GroupBy(p=>p.Target,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||result.GroupBy(p=>p.Plugin,StringComparer.Ordinal).Any(g=>g.Count()>1))throw new InvalidDataException("Multiple queued updates target the same mod. Review the update queue before restarting.");
            var paths=result.SelectMany(p=>(p.PackageFiles??Array.Empty<PendingPackageFile>()).Select(f=>f.Target(p.Target))).ToArray();
            if(paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=paths.Length)throw new InvalidDataException("Multiple queued packages target the same file.");
            return result;
        }
        internal static void VerifyPending(IEnumerable<PendingModUpdate> pending,string pluginRoot)
        {
            var identities=new HashSet<string>(StringComparer.Ordinal);
            foreach(var plan in pending)
            {
                VerifyPlan(plan,pluginRoot,plan.State=="applying");
                foreach(string id in PluginIds(Within(plan.Job,"payload.bin")))if(!identities.Add(id))throw new InvalidDataException("Two queued packages contain the same plugin. Review the update queue.");
            }
        }
        internal static void CheckPackageConflicts(IEnumerable<PendingModUpdate> plugins,IEnumerable<StandaloneBundlePlan> contents)
        {
            var parts=plugins.SelectMany(p=>(p.PackageFiles??Array.Empty<PendingPackageFile>()).Where(f=>!f.Remove&&f.ContentId.Length>0).Select(f=>new{Path=f.Target(p.Target),f.ContentId})).ToArray();
            if(parts.GroupBy(f=>f.ContentId,StringComparer.Ordinal).Any(g=>g.Count()>1))throw new InvalidDataException("Two queued packages contain the same content.");
            if(contents.Any(c=>parts.Any(f=>f.ContentId==c.Id||f.Path.Equals(c.Target,StringComparison.OrdinalIgnoreCase))))throw new InvalidDataException("A standalone update and a gameplay package target the same content.");
        }
        private static void VerifyPlan(PendingModUpdate plan,string pluginRoot,bool resume)
        {
            plan.Validate();string target=Within(pluginRoot,plan.Target),payload=Within(plan.Job,"payload.bin"),backup=Within(plan.Job,"previous.bin");
            RelativeTarget(pluginRoot,target);string current=CurrentHash(target);
            if(!SameHash(current,plan.OldHash)&&!(resume&&SameHash(current,plan.NewHash)))throw new IOException("The installed mod changed after its update was downloaded. Review that update before restarting.");
            if(!SameHash(Hash(payload),plan.NewHash))throw new InvalidDataException("The downloaded update failed verification. Download it again before restarting.");
            if(plan.Install)
            {
                string version=plan.Schema==4?VerifyPluginVersion(payload,plan.Plugin,""):VerifyInstallIdentity(payload,plan.Plugin,plan.MinimumVersion??"");
                if(version!=plan.Version)throw new InvalidDataException("The queued prerequisite version changed.");
                var ids=plan.Schema==4?VerifyPackageIdentities(payload,plan.Plugin):new[]{plan.Plugin};
                foreach(string id in ids)EnsurePluginAbsent(pluginRoot,id,resume&&SameHash(current,plan.NewHash)?target:"");
            }
            else if(resume)
            {
                if(!File.Exists(backup)||!SameHash(Hash(backup),plan.OldHash))throw new IOException("The queued update's backup needs attention.");
                VerifyIdentity(backup,payload,plan.Plugin);
            }
            else VerifyIdentity(target,payload,plan.Plugin);
            if(!string.IsNullOrEmpty(plan.PluginVersion)&&VerifyPluginVersion(payload,plan.Plugin,plan.MinimumVersion??"")!=plan.PluginVersion)throw new InvalidDataException("The queued prerequisite version changed.");
            var previews=new List<StagedModSidecar>();
            foreach(var file in plan.Sidecars??Array.Empty<PendingModSidecar>())
            {
                string installed=Within(pluginRoot,file.Target(plan.Plugin)),data=Within(plan.Job,file.Payload),previous=Within(plan.Job,file.Backup);
                if(File.Exists(installed))ReadBounded(installed,file.Name=="preview.doorman.json"?MaximumPreviewBytes:MaximumImageBytes);
                if(File.Exists(previous))ReadBounded(previous,file.Name=="preview.doorman.json"?MaximumPreviewBytes:MaximumImageBytes);
                string original=CurrentHash(installed);
                if(!SameHash(original,file.OldHash)&&!(resume&&SameHash(original,file.NewHash)))throw new IOException("The installed preview changed after this update was downloaded. Review the update before restarting.");
                if(resume&&file.OldHash.Length>0&&(!File.Exists(previous)||!SameHash(Hash(previous),file.OldHash)))throw new IOException("The queued preview's backup needs attention.");
                if(file.Remove)continue;
                byte[] bytes=ReadBounded(data,file.Name=="preview.doorman.json"?MaximumPreviewBytes:MaximumImageBytes);
                if(!SameHash(Hash(data),file.NewHash))throw new InvalidDataException("The downloaded preview failed verification.");
                previews.Add(new StagedModSidecar { Name=file.Name,Data=bytes });
            }
            foreach(var file in plan.PackageFiles??Array.Empty<PendingPackageFile>())
            {
                string installed=Within(pluginRoot,file.Target(plan.Target)),data=Within(plan.Job,file.Payload),previous=Within(plan.Job,file.Backup);
                string currentFile=CurrentHash(installed);
                if(!SameHash(currentFile,file.OldHash)&&!(resume&&SameHash(currentFile,file.NewHash)))throw new IOException("A package file changed after download. Review the update before restarting.");
                if(resume&&file.OldHash.Length>0&&(!File.Exists(previous)||!SameHash(Hash(previous),file.OldHash)))throw new IOException("A package rollback copy needs attention.");
                if(!file.Remove&&(!File.Exists(data)||!SameHash(Hash(data),file.NewHash)))throw new InvalidDataException("A queued package file failed its checksum.");
            }
            if(previews.Count>0)ValidatePreviews(previews,plan.Plugin,pluginRoot);
            else if(plan.Schema==2)ValidateRemoval(plan,pluginRoot,resume);
        }
        private static bool SameHash(string left,string right)=>left.Equals(right,StringComparison.OrdinalIgnoreCase);
        private static string CurrentHash(string file)=>File.Exists(file)?Hash(file):"";
        internal static byte[] ReadBounded(string path,int maximum)
        {
            if(new FileInfo(path).Length>maximum)throw new InvalidDataException("The update preview is too large.");
            using(var input=File.OpenRead(path))using(var output=new MemoryStream())
            {
                var buffer=new byte[65536];int read;while((read=input.Read(buffer,0,buffer.Length))>0){if(output.Length+read>maximum)throw new InvalidDataException("The update preview changed size.");output.Write(buffer,0,read);}return output.ToArray();
            }
        }
        internal static void ValidatePreviews(IReadOnlyList<StagedModSidecar> files,string plugin,string pluginRoot)
        {
            if(files.Count<1||files.Count>2||files.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count)throw new InvalidDataException("The update preview is ambiguous.");
            var manifest=files.SingleOrDefault(f=>f.Name=="preview.doorman.json")??throw new InvalidDataException("The update preview manifest is missing.");
            if(manifest.Data.Length>MaximumPreviewBytes)throw new InvalidDataException("The update preview manifest is too large.");
            var card=ModJson.ReadBytes<ModManifest>(manifest.Data)??throw new InvalidDataException("The update preview manifest is empty.");
            if(card.Plugin!=plugin||!string.IsNullOrEmpty(card.Bundle))throw new InvalidDataException("An automatic update preview must name only this exact plugin, without a bundle identifier.");
            card.Directory=Within(pluginRoot,MetadataDirectory(plugin));string image=card.Image??"";card.Validate();
            if(card.RequisiteError.Length>0)throw new InvalidDataException("The downloaded preview contains invalid prerequisites.");
            if(card.Image!=image||image.Length>0&&image!="image.png"&&image!="image.jpg"||files.Count!=(image.Length>0?2:1))throw new InvalidDataException("The update preview image is invalid or missing.");
            foreach(var file in files.Where(f=>f!=manifest))
            {
                if(file.Name!=image||file.Data.Length>MaximumImageBytes||!ModImageHeader.Allowed(file.Data)||file.Name=="image.png"&&file.Data[0]!=137||file.Name=="image.jpg"&&file.Data[0]!=255)throw new InvalidDataException("The update preview image is invalid or too large.");
            }
        }
        internal static void Stage(string updateRoot,string pluginRoot,string oldPath,string payload,string plugin,string repository,string version,IReadOnlyList<StagedModSidecar>? sidecars=null,string minimumVersion="",bool preservePreview=false)
        {
            if(ManagedPackagePath(pluginRoot,oldPath))throw new InvalidOperationException("This mod has companion files. Update the complete package from server setup.");
            string target=RelativeTarget(pluginRoot,oldPath);
            var pending=ActivePlans(updateRoot);
            if(pending.Any(plan=>string.Equals(plan.Target,target,StringComparison.OrdinalIgnoreCase)||plan.Plugin==plugin)) throw new InvalidOperationException("An update for this mod is already queued. Restart the game to finish it.");
            if(pending.Length>=MaximumPendingJobs)throw new InvalidOperationException("The pending update queue is full. Restart to finish the queued updates before downloading another one.");
            VerifyIdentity(oldPath,payload,plugin);
            if(sidecars!=null&&sidecars.Count>0)ValidatePreviews(sidecars,plugin,pluginRoot);
            var plan=new PendingModUpdate { Plugin=plugin, Repository=repository, Version=version, Target=target, OldHash=Hash(oldPath),NewHash=Hash(payload),PluginVersion=VerifyPluginVersion(payload,plugin,minimumVersion),MinimumVersion=minimumVersion.Length>0?minimumVersion:null }; plan.Validate();
            if((sidecars==null||sidecars.Count==0)&&!preservePreview)
            {
                var removal=ManagedRemoval(pluginRoot,plugin);
                if(removal.Length>0){plan.Schema=2;plan.Sidecars=removal;plan.Validate();}
            }
            string job=Within(updateRoot,Guid.NewGuid().ToString("N")); Directory.CreateDirectory(job);
            File.Copy(payload,Within(job,"payload.bin"),false);
            if(Hash(Within(job,"payload.bin"))!=plan.NewHash) throw new IOException("The staged download changed.");
            if(sidecars!=null&&sidecars.Count>0)
            {
                var files=new List<PendingModSidecar>();
                foreach(var file in sidecars)
                {
                    string installed=Within(pluginRoot,Path.Combine(MetadataDirectory(plugin),file.Name));
                    if(File.Exists(installed))ReadBounded(installed,file.Name=="preview.doorman.json"?MaximumPreviewBytes:MaximumImageBytes);
                    var record=new PendingModSidecar { Name=file.Name,OldHash=CurrentHash(installed) };
                    string staged=Within(job,record.Payload);using(var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(file.Data,0,file.Data.Length);output.Flush(true);}record.NewHash=Hash(staged);files.Add(record);
                }
                plan.Schema=2;plan.Sidecars=files.ToArray();plan.Validate();
            }
            // The plan is the commit marker. Nothing is installed before it exists.
            UpdateJson.Save(Within(job,"plan.json"),plan);
        }
        internal static void StageInstall(string updateRoot,string pluginRoot,string payload,string plugin,string repository,string minimumVersion="",IReadOnlyList<StagedModSidecar>? sidecars=null,CancellationToken token=default,bool allowMulti=false)
        {
            token.ThrowIfCancellationRequested();
            string target=InstallTarget(plugin),installed=Within(pluginRoot,target);RelativeTarget(pluginRoot,installed);
            if(File.Exists(installed)||Directory.Exists(installed))throw new IOException("The prerequisite installation destination already exists. Nothing was replaced.");
            var ids=allowMulti?VerifyPackageIdentities(payload,plugin):new[]{plugin};
            foreach(string id in ids)EnsurePluginAbsent(pluginRoot,id);
            var pending=ActivePlans(updateRoot);
            if(pending.Any(p=>p.Target.Equals(target,StringComparison.OrdinalIgnoreCase)||PluginIds(Within(p.Job,"payload.bin")).Intersect(ids,StringComparer.Ordinal).Any()))throw new InvalidOperationException("This prerequisite is already queued. Restart the game to finish installing it.");
            if(pending.Length>=MaximumPendingJobs)throw new InvalidOperationException("The pending update queue is full. Restart before downloading another prerequisite.");
            if(allowMulti&&!string.IsNullOrEmpty(minimumVersion))throw new InvalidDataException("A multi-plugin package cannot stand in for a versioned prerequisite.");
            string version=allowMulti?VerifyPluginVersion(payload,plugin,""):VerifyInstallIdentity(payload,plugin,minimumVersion);
            if(sidecars!=null&&sidecars.Count>0)ValidatePreviews(sidecars,plugin,pluginRoot);
            var plan=new PendingModUpdate { Schema=allowMulti?4:3,Plugin=plugin,Repository=repository,Version=version,PluginVersion=version,MinimumVersion=minimumVersion,Target=target,OldHash="",NewHash=Hash(payload) };plan.Validate();
            string job=Within(updateRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
            try
            {
                string staged=Within(job,"payload.bin");
                using(var input=File.OpenRead(payload))using(var output=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                {
                    var buffer=new byte[65536];int read;while((read=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,read);}output.Flush(true);
                }
                if(Hash(staged)!=plan.NewHash)throw new IOException("The staged prerequisite changed.");
                var files=new List<PendingModSidecar>();
                foreach(var file in sidecars??Array.Empty<StagedModSidecar>())
                {
                    token.ThrowIfCancellationRequested();string current=Within(pluginRoot,Path.Combine(MetadataDirectory(plugin),file.Name));
                    if(File.Exists(current)||Directory.Exists(current))throw new IOException("A prerequisite preview already exists. Nothing was replaced.");
                    var record=new PendingModSidecar { Name=file.Name,OldHash="" };string data=Within(job,record.Payload);
                    using(var output=new FileStream(data,FileMode.CreateNew,FileAccess.Write,FileShare.None)){output.Write(file.Data,0,file.Data.Length);output.Flush(true);}record.NewHash=Hash(data);files.Add(record);
                }
                plan.Sidecars=files.Count>0?files.ToArray():null;plan.Validate();token.ThrowIfCancellationRequested();
                UpdateJson.Save(Within(job,"plan.json"),plan);
            }
            catch
            {
                // No receipt means no committed job. Remove only this newly-created
                // bounded staging folder; loaded plugins and configs were untouched.
                if(!File.Exists(Within(job,"plan.json")))try{Directory.Delete(job,true);}catch(IOException){}catch(UnauthorizedAccessException){}
                throw;
            }
        }
        internal static void StageGameplayPackage(string updateRoot,string pluginRoot,string payload,string plugin,string repository,string releaseVersion,string existingTarget,string sourceHash,string recordId,string dllName,IReadOnlyList<StagedPackageFile> files,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(!HashValid(sourceHash)||files==null||files.Count<1||files.Count>63||files.Select(f=>f.Relative).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count||files.Count(f=>f.Relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))!=1)throw new InvalidDataException("The downloaded gameplay package is ambiguous.");
            string relative=PackageInstallTarget(plugin,dllName),target=Within(pluginRoot,relative);RelativeTarget(pluginRoot,target);
            bool install=string.IsNullOrEmpty(existingTarget);
            if(!install&&!Path.GetFullPath(existingTarget).Equals(target,StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(ManagedPackagePath(pluginRoot,existingTarget)?"This release changes the plugin filename. Update the complete package manually.":"This installed package was not installed by DOORMAN. Update it manually to preserve its other files.");
            if(install&&(File.Exists(target)||Directory.Exists(target)))throw new IOException("This package destination already exists. Nothing was replaced.");
            string version=VerifyInstallIdentity(payload,plugin,"");
            EnsurePluginAbsent(pluginRoot,plugin,install?"":target);
            var pending=ActivePlans(updateRoot);if(pending.Length>=MaximumPendingJobs||pending.Any(p=>p.Plugin==plugin||p.Target.Equals(relative,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("This package is already queued for restart.");
            PendingModUpdate? previous=null;
            if(!install)
            {
                VerifyIdentity(target,payload,plugin);string current=Hash(target);
                foreach(string previousJob in Jobs(updateRoot).OrderByDescending(Directory.GetLastWriteTimeUtc))
                {
                    string receipt=Within(previousJob,"plan.json");if(!File.Exists(receipt))continue;
                    var prior=UpdateJson.ReadFile<PendingModUpdate>(receipt);prior.Validate();
                    if(prior.Package&&prior.State=="complete"&&prior.Plugin==plugin&&prior.Target==relative&&SameHash(prior.NewHash,current)){previous=prior;break;}
                }
                if(previous==null||previous.PackageRecordId!=recordId||!previous.Repository.Equals(repository,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The installed package has no matching verified DOORMAN receipt. Update it manually.");
                string newId=files.Single(f=>f.Relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)).ContentId;
                var priorBundle=previous.PackageFiles!.Single(f=>!f.Remove&&f.Relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase));
                if(newId!=priorBundle.ContentId)throw new InvalidDataException("The package's gameplay content identity changed. Install it manually.");
            }
            var old=(previous?.PackageFiles??Array.Empty<PendingPackageFile>()).Where(f=>!f.Remove).ToDictionary(f=>f.Relative,StringComparer.OrdinalIgnoreCase);
            foreach(var prior in old.Values){string path=Within(pluginRoot,prior.Target(relative));if(!SameHash(CurrentHash(path),prior.NewHash))throw new IOException("An installed package file changed. Nothing was replaced.");}
            foreach(var file in files)
            {
                SafePackageRelative(file.Relative);if(!HashValid(file.Hash)||!File.Exists(file.Path)||!SameHash(Hash(file.Path),file.Hash))throw new InvalidDataException("The downloaded package file failed verification.");
                string path=Within(pluginRoot,Path.Combine(Path.GetDirectoryName(relative)!,file.Relative.Replace('/',Path.DirectorySeparatorChar)));
                if(!old.ContainsKey(file.Relative)&&(File.Exists(path)||Directory.Exists(path)))throw new IOException("A package destination already exists. Nothing was replaced.");
            }
            Directory.CreateDirectory(updateRoot);string job=Within(updateRoot,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(job);
            try
            {
                string sourcePluginHash=Hash(payload),oldPluginHash=install?"":Hash(target);string staged=Within(job,"payload.bin");CopyPackageFile(payload,staged,token);
                if(!SameHash(Hash(staged),sourcePluginHash)||!SameHash(Hash(payload),sourcePluginHash))throw new IOException("The downloaded plugin changed while staging.");
                var list=new List<PendingPackageFile>();
                foreach(var file in files)
                {
                    token.ThrowIfCancellationRequested();old.TryGetValue(file.Relative,out var prior);
                    var item=new PendingPackageFile{Index=list.Count,Relative=file.Relative,OldHash=prior?.NewHash??"",NewHash=file.Hash,ContentId=file.ContentId,ContentVersion=file.ContentVersion};item.Validate();
                    CopyPackageFile(file.Path,Within(job,item.Payload),token);if(!SameHash(Hash(Within(job,item.Payload)),item.NewHash))throw new IOException("A staged package file changed.");list.Add(item);
                }
                foreach(var prior in old.Values.Where(f=>!files.Any(x=>x.Relative.Equals(f.Relative,StringComparison.OrdinalIgnoreCase))))
                {var item=new PendingPackageFile{Index=list.Count,Relative=prior.Relative,OldHash=prior.NewHash,Remove=true,ContentId=prior.ContentId,ContentVersion=prior.ContentVersion};item.Validate();list.Add(item);}
                if(!install&&(!SameHash(Hash(target),oldPluginHash)||old.Values.Any(f=>!SameHash(CurrentHash(Within(pluginRoot,f.Target(relative))),f.NewHash))))throw new IOException("The installed package changed while staging.");
                var plan=new PendingModUpdate{Schema=install?5:6,Plugin=plugin,Repository=repository,Version=version,PluginVersion=version,Target=relative,OldHash=oldPluginHash,NewHash=sourcePluginHash,PackageSourceHash=sourceHash,PackageRecordId=recordId,PackageDllName=dllName,PackageFiles=list.ToArray()};plan.Validate();
                token.ThrowIfCancellationRequested();UpdateJson.Save(Within(job,"plan.json"),plan);
            }
            catch{if(!File.Exists(Within(job,"plan.json")))try{Directory.Delete(job,true);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
        }
        private static void CopyPackageFile(string source,string target,CancellationToken token)
        {using(var input=File.OpenRead(source))using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)){var buffer=new byte[65536];int n;while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();output.Write(buffer,0,n);}output.Flush(true);}}
        internal static void ApplyPending(string updateRoot,string pluginRoot,Action<string> log)
        {
            // Startup can be reached through Steam without the menu restart
            // preflight. Reject conflicting receipts before changing any DLL.
            foreach(var queued in ReadPending(updateRoot,pluginRoot))ApplyOne(queued,pluginRoot,log);
        }
        internal static bool ApplyOne(PendingModUpdate queued,string pluginRoot,Action<string> log)
        {
                string job=queued.Job;
                string planPath=Within(job,"plan.json"); if(!File.Exists(planPath)) return false;
                PendingModUpdate? plan=null;
                try
                {
                    plan=UpdateJson.ReadFile<PendingModUpdate>(planPath); plan.Validate(); if(plan.State!="pending"&&plan.State!="applying") return plan.State=="complete";plan.Job=job;
                    string target=Within(pluginRoot,plan.Target), payload=Within(job,"payload.bin"), backup=Within(job,"previous.bin");
                    VerifyPlan(plan,pluginRoot,plan.State=="applying");
                    if(plan.OldHash.Length>0)Backup(target,backup,plan.OldHash);
                    foreach(var file in plan.Sidecars??Array.Empty<PendingModSidecar>())
                        if(file.OldHash.Length>0)Backup(Within(pluginRoot,file.Target(plan.Plugin)),Within(job,file.Backup),file.OldHash);
                    foreach(var file in plan.PackageFiles??Array.Empty<PendingPackageFile>())
                        if(file.OldHash.Length>0)Backup(Within(pluginRoot,file.Target(plan.Target)),Within(job,file.Backup),file.OldHash);
                    plan.State="applying"; UpdateJson.Save(planPath,plan);
                    // Image first, then its manifest, and DLL last. A crash leaves
                    // an applying receipt; every old/new hash must reconcile before
                    // the entire DLL plus preview transaction can become complete.
                    foreach(var file in (plan.Sidecars??Array.Empty<PendingModSidecar>()).OrderBy(f=>f.Remove?(f.Name=="preview.doorman.json"?0:1):(f.Name=="preview.doorman.json"?1:0)))
                        if(file.Remove)Remove(Within(pluginRoot,file.Target(plan.Plugin)),file.OldHash);else Replace(Within(job,file.Payload),Within(pluginRoot,file.Target(plan.Plugin)),file.OldHash,file.NewHash);
                    foreach(var file in plan.PackageFiles??Array.Empty<PendingPackageFile>())
                        if(file.Remove)Remove(Within(pluginRoot,file.Target(plan.Target)),file.OldHash);else ReplacePackage(Within(job,file.Payload),Within(pluginRoot,file.Target(plan.Target)),file.OldHash,file.NewHash);
                    if(plan.Package)ReplacePackage(payload,target,plan.OldHash,plan.NewHash);else Replace(payload,target,plan.OldHash,plan.NewHash);
                    if(!SameHash(Hash(target),plan.NewHash)||(plan.Sidecars??Array.Empty<PendingModSidecar>()).Any(f=>!SameHash(CurrentHash(Within(pluginRoot,f.Target(plan.Plugin))),f.NewHash))||(plan.PackageFiles??Array.Empty<PendingPackageFile>()).Any(f=>!SameHash(CurrentHash(Within(pluginRoot,f.Target(plan.Target))),f.NewHash)))throw new IOException("The installed update or package failed verification.");
                    plan.State="complete"; plan.Message="Installed "+plan.Version+(plan.Install?" prerequisite.":". Previous DLL: "+backup); UpdateJson.Save(planPath,plan); log(plan.Message);
                    return true;
                }
                catch(Exception ex)
                {
                    log("DOORMAN update needs attention: "+ex.Message);
                    if(plan!=null&&plan.Package&&plan.State=="applying")
                    {
                        try{RollbackPackage(plan,pluginRoot);plan.State="failed";plan.Message="Package installation rolled back: "+ex.Message;UpdateJson.Save(planPath,plan);log(plan.Message);}
                        catch(Exception rollback){log("Package rollback needs attention: "+rollback.Message);}
                    }
                    if(plan!=null&&plan.State=="pending") { try { plan.State="failed"; plan.Message=ex.Message; UpdateJson.Save(planPath,plan); } catch { } }
                    // An interrupted 'applying' plan is reconciled by hashes on the
                    // next start. Never guess which file to remove after an error.
                    return false;
                }
        }
        private static void RollbackPackage(PendingModUpdate plan,string pluginRoot)
        {
            void Restore(string target,string backup,string oldHash,string newHash)
            {
                string current=CurrentHash(target);if(SameHash(current,oldHash))return;
                if(!SameHash(current,newHash))throw new IOException("A package file changed during rollback.");
                if(oldHash.Length==0){File.Delete(target);if(File.Exists(target))throw new IOException("A new package file could not be removed.");}
                else {if(!File.Exists(backup)||!SameHash(Hash(backup),oldHash))throw new IOException("A package rollback copy is missing.");ReplacePackage(backup,target,newHash,oldHash);}
            }
            Restore(Within(pluginRoot,plan.Target),Within(plan.Job,"previous.bin"),plan.OldHash,plan.NewHash);
            foreach(var file in (plan.PackageFiles??Array.Empty<PendingPackageFile>()).Reverse())Restore(Within(pluginRoot,file.Target(plan.Target)),Within(plan.Job,file.Backup),file.OldHash,file.NewHash);
        }
        private static PendingModSidecar[] ManagedRemoval(string pluginRoot,string plugin)
        {
            string manifest=Within(pluginRoot,Path.Combine(MetadataDirectory(plugin),"preview.doorman.json"));if(!File.Exists(manifest))return Array.Empty<PendingModSidecar>();
            var card=RemovalCard(manifest,plugin);var result=new List<PendingModSidecar> { new PendingModSidecar { Name="preview.doorman.json",OldHash=Hash(manifest),Remove=true } };
            if(card.Image.Length>0)
            {
                string image=Within(pluginRoot,Path.Combine(MetadataDirectory(plugin),card.Image));
                if(File.Exists(image)){ReadBounded(image,MaximumImageBytes);result.Add(new PendingModSidecar { Name=card.Image,OldHash=Hash(image),Remove=true });}
            }
            return result.ToArray();
        }
        private static ModManifest RemovalCard(string manifest,string plugin)
        {
            var card=ModJson.ReadBytes<ModManifest>(ReadBounded(manifest,MaximumPreviewBytes))??throw new InvalidDataException("The managed preview manifest is empty.");string image=card.Image??"";
            if(card.Plugin!=plugin||!string.IsNullOrEmpty(card.Bundle))throw new InvalidDataException("The managed preview belongs to another mod. Use its release page for a manual update.");
            card.Directory=Path.GetDirectoryName(manifest)!;card.Validate();
            if(card.RequisiteError.Length>0)throw new InvalidDataException("The managed preview contains invalid prerequisites. Use its release page.");
            if(card.Image!=image||image.Length>0&&image!="image.png"&&image!="image.jpg")throw new InvalidDataException("The managed preview image needs a manual update.");return card;
        }
        private static void ValidateRemoval(PendingModUpdate plan,string pluginRoot,bool resume)
        {
            var files=plan.Sidecars!;var manifest=files.Single(f=>f.Name=="preview.doorman.json");
            string original=resume?Within(plan.Job,manifest.Backup):Within(pluginRoot,manifest.Target(plan.Plugin));var card=RemovalCard(original,plan.Plugin);
            if(files.Where(f=>f.Name!="preview.doorman.json").Any(f=>f.Name!=card.Image))throw new InvalidDataException("The removed preview image belongs to another mod.");
        }
        private static void Remove(string target,string oldHash)
        {string current=CurrentHash(target);if(current.Length==0)return;if(!SameHash(current,oldHash))throw new IOException("The managed preview changed before removal.");File.Delete(target);if(File.Exists(target))throw new IOException("The old managed preview could not be removed.");}
        private static void Backup(string target,string backup,string oldHash)
        {
            if(File.Exists(backup)){if(!SameHash(Hash(backup),oldHash))throw new IOException("The rollback copy changed. Keeping the installed mod.");return;}
            if(!SameHash(CurrentHash(target),oldHash))throw new IOException("The original update file is unavailable for backup.");
            File.Copy(target,backup,false);if(!SameHash(Hash(backup),oldHash))throw new IOException("Could not verify the rollback copy.");
        }
        private static void Replace(string payload,string target,string oldHash,string newHash)
        {
            // Use the same collision-safe transaction as gameplay packages.
            // A user-owned file ending in .doorman-new is never our scratch file.
            ReplacePackage(payload,target,oldHash,newHash);
        }
        private static void ReplacePackage(string payload,string target,string oldHash,string newHash)
        {
            string current=CurrentHash(target);if(SameHash(current,newHash))return;
            if(!SameHash(current,oldHash))throw new IOException("An installed package file changed before replacement.");
            string directory=Path.GetDirectoryName(target)!;Directory.CreateDirectory(directory);
            string replacement=Within(directory,Path.GetFileName(target)+".doorman-"+Guid.NewGuid().ToString("N")+".tmp");bool created=false;
            try
            {
                using(var input=File.OpenRead(payload))using(var output=new FileStream(replacement,FileMode.CreateNew,FileAccess.Write,FileShare.None)){created=true;input.CopyTo(output);output.Flush(true);}
                if(!SameHash(Hash(replacement),newHash)||!SameHash(CurrentHash(target),oldHash))throw new IOException("A package file changed before installation.");
                if(oldHash.Length==0)File.Move(replacement,target);else File.Replace(replacement,target,null);
                if(!SameHash(Hash(target),newHash))throw new IOException("The installed package file failed verification.");
            }
            finally{if(created&&File.Exists(replacement))try{File.Delete(replacement);}catch(IOException){}catch(UnauthorizedAccessException){}}
        }
    }
}
