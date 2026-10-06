using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    [DataContract]
    internal sealed class PendingModUpdate
    {
        [DataMember] public int Schema=1;
        [DataMember] public string Plugin="", Repository="", Version="", Target="", OldHash="", NewHash="", State="pending", Message="";
        [DataMember(EmitDefaultValue=false)] public PendingModSidecar[]? Sidecars;
        internal string Job="";
        internal void Validate()
        {
            if((Schema!=1&&Schema!=2)||!GitHubRepository.Valid(Repository)||Plugin==null||Plugin.Length==0||Plugin.Length>256||Plugin.Any(char.IsControl)||ModReleaseVersion.Compare(Version,Version)!=0||!ModUpdateFiles.HashValid(OldHash)||!ModUpdateFiles.HashValid(NewHash)||!new[]{"pending","applying","complete","failed","cancelled"}.Contains(State)) throw new InvalidDataException("Invalid pending update.");
            if(ModProfile.Protected(Plugin)) throw new InvalidDataException("This loader/plugin needs its own installation procedure.");
            var files=Sidecars??Array.Empty<PendingModSidecar>();
            if(Schema==1&&files.Length!=0||Schema==2&&(files.Length<1||files.Length>2)||files.Any(f=>f==null)||files.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Length||files.Length>0&&files.Any(f=>f.Remove)!=files.All(f=>f.Remove))throw new InvalidDataException("Invalid update preview files.");
            foreach(var file in files)file.Validate(Plugin);
            if(Schema==2&&files.Count(f=>f.Name=="preview.doorman.json")!=1)throw new InvalidDataException("An update preview needs one manifest.");
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
    internal static class ModUpdateFiles
    {
        internal const long MaximumBytes=256L*1024*1024;
        internal const int MaximumPendingJobs=128;
        internal const int MaximumPreviewBytes=65536,MaximumImageBytes=12*1024*1024;
        internal static string MetadataDirectory(string plugin)
        {
            if(string.IsNullOrWhiteSpace(plugin)||plugin.Length>256||plugin.Any(char.IsControl))throw new InvalidDataException("Invalid preview plugin identifier.");
            using(var sha=SHA256.Create())return Path.Combine("DOORMAN-Metadata",BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(plugin))).Replace("-","").ToLowerInvariant());
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
                if((File.Exists(path)||Directory.Exists(path))&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Updates through linked folders are unsupported.");
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
            if(oldIds.Length==0||oldIds.Length>16||!oldIds.Contains(plugin,StringComparer.Ordinal)||oldIds.Any(ModProfile.Protected)||!oldIds.SequenceEqual(newIds,StringComparer.Ordinal)) throw new InvalidDataException("The download's plugin IDs do not match this installed mod.");
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
        internal static PendingModUpdate[] ReadPending(string updateRoot,string pluginRoot)
        {
            var result=ActivePlans(updateRoot);
            foreach(var plan in result)RelativeTarget(pluginRoot,Within(pluginRoot,plan.Target));
            if(result.GroupBy(p=>p.Target,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("Multiple queued updates target the same mod. Review the update queue before restarting.");
            return result;
        }
        internal static void VerifyPending(IEnumerable<PendingModUpdate> pending,string pluginRoot)
        {
            foreach(var plan in pending)
            {
                VerifyPlan(plan,pluginRoot,plan.State=="applying");
            }
        }
        private static void VerifyPlan(PendingModUpdate plan,string pluginRoot,bool resume)
        {
            plan.Validate();string target=Within(pluginRoot,plan.Target),payload=Within(plan.Job,"payload.bin"),backup=Within(plan.Job,"previous.bin");
            RelativeTarget(pluginRoot,target);string current=Hash(target);
            if(!SameHash(current,plan.OldHash)&&!(resume&&SameHash(current,plan.NewHash)))throw new IOException("The installed mod changed after its update was downloaded. Review that update before restarting.");
            if(!SameHash(Hash(payload),plan.NewHash))throw new InvalidDataException("The downloaded update failed verification. Download it again before restarting.");
            if(resume)
            {
                if(!File.Exists(backup)||!SameHash(Hash(backup),plan.OldHash))throw new IOException("The queued update's backup needs attention.");
                VerifyIdentity(backup,payload,plan.Plugin);
            }
            else VerifyIdentity(target,payload,plan.Plugin);
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
            if(card.Image!=image||image.Length>0&&image!="image.png"&&image!="image.jpg"||files.Count!=(image.Length>0?2:1))throw new InvalidDataException("The update preview image is invalid or missing.");
            foreach(var file in files.Where(f=>f!=manifest))
            {
                if(file.Name!=image||file.Data.Length>MaximumImageBytes||!ModImageHeader.Allowed(file.Data)||file.Name=="image.png"&&file.Data[0]!=137||file.Name=="image.jpg"&&file.Data[0]!=255)throw new InvalidDataException("The update preview image is invalid or too large.");
            }
        }
        internal static void Stage(string updateRoot,string pluginRoot,string oldPath,string payload,string plugin,string repository,string version,IReadOnlyList<StagedModSidecar>? sidecars=null)
        {
            string target=RelativeTarget(pluginRoot,oldPath);
            var pending=ActivePlans(updateRoot);
            if(pending.Any(plan=>string.Equals(plan.Target,target,StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("An update for this mod is already queued. Restart the game to finish it.");
            if(pending.Length>=MaximumPendingJobs)throw new InvalidOperationException("The pending update queue is full. Restart to finish the queued updates before downloading another one.");
            VerifyIdentity(oldPath,payload,plugin);
            if(sidecars!=null&&sidecars.Count>0)ValidatePreviews(sidecars,plugin,pluginRoot);
            var plan=new PendingModUpdate { Plugin=plugin, Repository=repository, Version=version, Target=target, OldHash=Hash(oldPath),NewHash=Hash(payload) }; plan.Validate();
            if(sidecars==null||sidecars.Count==0)
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
        internal static void ApplyPending(string updateRoot,string pluginRoot,Action<string> log)
        {
            foreach(var queued in ActivePlans(updateRoot))
            {
                string job=queued.Job;
                string planPath=Within(job,"plan.json"); if(!File.Exists(planPath)) continue;
                PendingModUpdate? plan=null;
                try
                {
                    plan=UpdateJson.ReadFile<PendingModUpdate>(planPath); plan.Validate(); if(plan.State!="pending"&&plan.State!="applying") continue;plan.Job=job;
                    string target=Within(pluginRoot,plan.Target), payload=Within(job,"payload.bin"), backup=Within(job,"previous.bin");
                    VerifyPlan(plan,pluginRoot,plan.State=="applying");
                    Backup(target,backup,plan.OldHash);
                    foreach(var file in plan.Sidecars??Array.Empty<PendingModSidecar>())
                        if(file.OldHash.Length>0)Backup(Within(pluginRoot,file.Target(plan.Plugin)),Within(job,file.Backup),file.OldHash);
                    plan.State="applying"; UpdateJson.Save(planPath,plan);
                    // Image first, then its manifest, and DLL last. A crash leaves
                    // an applying receipt; every old/new hash must reconcile before
                    // the entire DLL plus preview transaction can become complete.
                    foreach(var file in (plan.Sidecars??Array.Empty<PendingModSidecar>()).OrderBy(f=>f.Remove?(f.Name=="preview.doorman.json"?0:1):(f.Name=="preview.doorman.json"?1:0)))
                        if(file.Remove)Remove(Within(pluginRoot,file.Target(plan.Plugin)),file.OldHash);else Replace(Within(job,file.Payload),Within(pluginRoot,file.Target(plan.Plugin)),file.OldHash,file.NewHash);
                    Replace(payload,target,plan.OldHash,plan.NewHash);
                    if(!SameHash(Hash(target),plan.NewHash)||(plan.Sidecars??Array.Empty<PendingModSidecar>()).Any(f=>!SameHash(CurrentHash(Within(pluginRoot,f.Target(plan.Plugin))),f.NewHash)))throw new IOException("The installed update or preview failed verification.");
                    plan.State="complete"; plan.Message="Installed "+plan.Version+". Previous DLL: "+backup; UpdateJson.Save(planPath,plan); log(plan.Message);
                }
                catch(Exception ex)
                {
                    log("DOORMAN update needs attention: "+ex.Message);
                    if(plan!=null&&plan.State=="pending") { try { plan.State="failed"; plan.Message=ex.Message; UpdateJson.Save(planPath,plan); } catch { } }
                    // An interrupted 'applying' plan is reconciled by hashes on the
                    // next start. Never guess which file to remove after an error.
                }
            }
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
            string current=CurrentHash(target);if(SameHash(current,newHash))return;
            if(!SameHash(current,oldHash))throw new IOException("An installed file changed before update installation.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);string replacement=Within(Path.GetDirectoryName(target)!,Path.GetFileName(target)+".doorman-new");
            using(var input=File.OpenRead(payload))using(var output=new FileStream(replacement,FileMode.Create,FileAccess.Write,FileShare.None)){input.CopyTo(output);output.Flush(true);}
            if(!SameHash(Hash(replacement),newHash)||!SameHash(CurrentHash(target),oldHash))throw new IOException("The replacement or installed file changed before installation.");
            if(oldHash.Length==0)File.Move(replacement,target);else File.Replace(replacement,target,null);
            if(!SameHash(Hash(target),newHash))throw new IOException("The installed update failed verification.");
        }
    }
}
