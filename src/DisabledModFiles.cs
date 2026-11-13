using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;

namespace KellysJOINCHECK
{
    internal sealed class DisabledModCandidate
    {
        internal string SourceRelative="",SourcePath="",TargetRelative="";
        internal string[] Files=Array.Empty<string>();
    }

    // This record is the durable receipt. Persist it before restart; an atomic
    // move followed by process interruption can then be recognised by its bytes.
    [DataContract] internal sealed class DisabledModRestore
    {
        [DataMember] public string SourceRelative="",TargetRelative="",Digest="";
        [DataMember(EmitDefaultValue=false)] public string[] PluginIds=Array.Empty<string>();
        internal void Validate()
        {
            DisabledModFiles.ValidateSource(SourceRelative);
            DisabledModFiles.ValidateTarget(TargetRelative);
            PluginIds=PluginIds??Array.Empty<string>();
            if(Digest==null||Digest.Length!=64||Digest.Any(c=>!Uri.IsHexDigit(c))||PluginIds.Length>256||PluginIds.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length>256||id.Any(char.IsControl)))
                throw new InvalidDataException("Invalid disabled-mod restore receipt.");
        }
    }

    internal static class DisabledModFiles
    {
        internal const int MaximumUnits=128,MaximumFiles=512,MaximumDirectories=512;
        internal const long MaximumFileBytes=InstalledFilePolicy.MaximumBytes;
        private static readonly object ApplyLock=new object();

        // NOMM moves a complete direct child to BepInEx/disabledPlugins. Never
        // follow links or invent a filename from a plugin/content identifier.
        internal static DisabledModCandidate[] Inventory(string bepRoot,Action<string>? log=null)
        {
            string root=Root(bepRoot),disabled=Child(root,"disabledPlugins");
            if(!Directory.Exists(disabled))return Array.Empty<DisabledModCandidate>();
            var units=Directory.EnumerateFileSystemEntries(disabled).Take(MaximumUnits+1).OrderBy(p=>Path.GetFileName(p),StringComparer.Ordinal).ToArray();
            if(units.Length>MaximumUnits)throw new InvalidDataException("Too many NOMM-disabled mod units. Review disabledPlugins before using DOORMAN.");
            if(units.GroupBy(p=>Path.GetFileName(p),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("NOMM-disabled mod filenames are ambiguous.");
            var result=new List<DisabledModCandidate>();
            foreach(string path in units)
            {
                try
                {
                    string leaf=Path.GetFileName(path);ValidateSource(leaf);var tree=ReadTree(path);
                    result.Add(new DisabledModCandidate {SourceRelative=leaf,SourcePath=path,TargetRelative=leaf,Files=tree.Files});
                }
                catch(Exception ex) when(ex is InvalidDataException||ex is IOException||ex is UnauthorizedAccessException)
                { log?.Invoke("Skipped unsafe/unreadable NOMM-disabled unit "+Path.GetFileName(path)+": "+ex.Message); }
            }
            return result.ToArray();
        }

        internal static DisabledModRestore Prepare(string bepRoot,DisabledModCandidate candidate)
        {
            if(candidate==null)throw new InvalidDataException("No disabled mod was selected.");
            ValidateSource(candidate.SourceRelative);ValidateTarget(candidate.TargetRelative);
            string root=Root(bepRoot),source=Child(Child(root,"disabledPlugins"),candidate.SourceRelative);
            if(!string.Equals(Path.GetFullPath(candidate.SourcePath),source,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The disabled-mod source changed after discovery.");
            Child(Child(root,"plugins"),candidate.TargetRelative);
            return new DisabledModRestore {SourceRelative=candidate.SourceRelative,TargetRelative=candidate.TargetRelative,Digest=Fingerprint(source)};
        }

        // Reopening one cached unit must not walk every unrelated disabled tree.
        // Resolve the same guarded root/leaf and re-read the complete selected
        // tree so newly added files, links and changed path types still fail.
        internal static DisabledModCandidate Rescan(string bepRoot,DisabledModCandidate candidate)
        {
            if(candidate==null)throw new InvalidDataException("No disabled mod was selected.");
            ValidateSource(candidate.SourceRelative);ValidateTarget(candidate.TargetRelative);
            string root=Root(bepRoot),source=Child(Child(root,"disabledPlugins"),candidate.SourceRelative);
            if(!string.Equals(Path.GetFullPath(candidate.SourcePath),source,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("The disabled-mod source changed after discovery.");
            var tree=ReadTree(source);
            return new DisabledModCandidate {SourceRelative=candidate.SourceRelative,SourcePath=source,TargetRelative=candidate.TargetRelative,Files=tree.Files};
        }

        // Called only by the pre-chainloader startup path. Inventory/Prepare are
        // read-only and can be used in menus; moving a DLL during a game is unsafe.
        internal static void Apply(string bepRoot,IEnumerable<DisabledModRestore> restores,Action<string>? log=null)
        {
            lock(ApplyLock)ApplyCore(bepRoot,restores,log);
        }

        internal static void Verify(string bepRoot,IEnumerable<DisabledModRestore> restores)
        {
            Plans(bepRoot,restores);
        }

        // Preflight accepts both ready-to-move sources and completed renames.
        // A saved server session may resume only after every selected unit is
        // physically restored and its exact destination bytes are verified.
        internal static void VerifyApplied(string bepRoot,IEnumerable<DisabledModRestore> restores)
        {
            lock(ApplyLock)
            {
                var plans=Plans(bepRoot,restores);
                foreach(var plan in plans)
                {
                    CheckAncestors(plan.Source);
                    if(!plan.AlreadyRestored||Exists(plan.Source))throw new IOException("The selected NOMM mod has not finished restoring: "+plan.Receipt.SourceRelative);
                }
            }
        }

        private sealed class Plan
        {
            internal DisabledModRestore Receipt=new DisabledModRestore();
            internal string Source="",Target="";
            internal bool AlreadyRestored,Directory;
        }

        private static Plan[] Plans(string bepRoot,IEnumerable<DisabledModRestore> restores)
        {
            if(restores==null)throw new InvalidDataException("No disabled-mod restore receipts were supplied.");
            var records=restores.Take(MaximumUnits+1).ToArray();
            if(records.Length>MaximumUnits||records.Any(p=>p==null))throw new InvalidDataException("Too many or invalid disabled-mod restore receipts.");
            foreach(var record in records)record.Validate();
            if(records.GroupBy(p=>p.SourceRelative,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1)||records.GroupBy(p=>Canonical(p.TargetRelative),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("Two disabled-mod receipts target the same unit.");
            string root=Root(bepRoot),disabled=Child(root,"disabledPlugins"),plugins=Child(root,"plugins");
            var plans=records.Select(r=>new Plan {Receipt=r,Source=Child(disabled,r.SourceRelative),Target=Child(plugins,r.TargetRelative)}).OrderBy(p=>p.Target.Length).ThenBy(p=>p.Target,StringComparer.Ordinal).ToArray();
            // A whole unit and a separate addon may both target the same parent.
            // Do not let either unit replace a path contained in the other unit.
            foreach(var plan in plans)
            {
                bool source=Exists(plan.Source),target=Exists(plan.Target);
                if(source&&target)throw new IOException("A mod already occupies the restore destination: "+plan.Receipt.TargetRelative);
                if(!source&&!target)throw new IOException("The NOMM-disabled mod disappeared: "+plan.Receipt.SourceRelative);
                plan.AlreadyRestored=!source;plan.Directory=Directory.Exists(source?plan.Source:plan.Target);
                var descendants=plans.Where(p=>p!=plan&&Below(plan.Target,p.Target)).ToArray();
                if(descendants.Length>0&&!plan.Directory)throw new IOException("A file cannot contain another disabled-mod restore destination.");
                string actual=source?plan.Source:plan.Target;
                string[] omit=source?Array.Empty<string>():descendants.Where(p=>!Exists(p.Source)&&Exists(p.Target)).Select(p=>p.Target.Substring(plan.Target.Length+1)).ToArray();
                if(!Equal(Fingerprint(actual,omit),plan.Receipt.Digest))throw new IOException("The disabled mod changed after it was selected: "+plan.Receipt.SourceRelative);
                if(source)
                    foreach(var descendant in descendants)
                    {
                        string projected=Child(plan.Source,descendant.Target.Substring(plan.Target.Length+1));
                        if(Exists(projected))throw new IOException("The disabled parent already contains an addon restore destination.");
                        ValidateParents(projected);
                    }
                ValidateParents(plan.Target);
            }
            return plans;
        }

        private static void ApplyCore(string bepRoot,IEnumerable<DisabledModRestore> restores,Action<string>? log)
        {
            var plans=Plans(bepRoot,restores);var moved=new List<Plan>();var created=new List<string>();
            try
            {
                foreach(var plan in plans)
                {
                    if(plan.AlreadyRestored){log?.Invoke("Verified restored NOMM mod: "+plan.Receipt.TargetRelative);continue;}
                    // Re-check immediately before each rename. The preflight
                    // above ensures a collision/tamper in any unit moves none.
                    Child(Child(Root(bepRoot),"disabledPlugins"),plan.Receipt.SourceRelative);
                    Child(Child(Root(bepRoot),"plugins"),plan.Receipt.TargetRelative);
                    if(Exists(plan.Target))throw new IOException("The restore destination appeared after verification.");
                    if(!Equal(Fingerprint(plan.Source),plan.Receipt.Digest))throw new IOException("The disabled mod changed during restart.");
                    CreateParents(Path.GetDirectoryName(plan.Target)!,created);
                    Move(plan.Source,plan.Target,plan.Directory);moved.Add(plan);
                    if(!Equal(Fingerprint(plan.Target),plan.Receipt.Digest))throw new IOException("The restored mod changed during restart.");
                    log?.Invoke("Restored NOMM-disabled mod: "+plan.Receipt.TargetRelative);
                }
                Plans(bepRoot,plans.Select(p=>p.Receipt));
            }
            catch(Exception failure)
            {
                Exception? rollbackFailure=null;
                foreach(var plan in moved.AsEnumerable().Reverse())
                {
                    try
                    {
                        if(Exists(plan.Source)||!Exists(plan.Target))throw new IOException("The restore rollback destination changed.");
                        CheckAncestors(plan.Source);ReadTree(plan.Target);
                        Move(plan.Target,plan.Source,plan.Directory);
                    }
                    catch(Exception ex){rollbackFailure=ex;}
                }
                foreach(string directory in created.AsEnumerable().Reverse())
                {
                    try
                    {
                        if(Directory.Exists(directory)&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)==0&&!Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory,false);
                    }
                    catch(Exception ex){rollbackFailure=rollbackFailure??ex;}
                }
                if(rollbackFailure!=null)throw new IOException("Disabled-mod restore was interrupted and its receipt needs recovery. No existing file was overwritten.",new AggregateException(failure,rollbackFailure));
                throw;
            }
        }

        private static void Move(string source,string target,bool directory)
        {
            // Both locations share BepInEx, so rename the entire directory/file
            // atomically. Never fall back to copy/delete or overwrite a target.
            if(directory)Directory.Move(source,target);else File.Move(source,target);
        }

        private sealed class Tree
        {
            internal bool Directory;
            internal string[] Files=Array.Empty<string>();
        }

        private static Tree ReadTree(string path)
        {
            CheckAncestors(path);
            if(!Exists(path))throw new IOException("The disabled mod no longer exists.");
            if(!Directory.Exists(path)){BoundedFile(path);return new Tree {Files=new[]{path}};}
            var files=new List<string>();var folders=new Stack<string>();folders.Push(path);int directories=0;
            while(folders.Count>0)
            {
                string folder=folders.Pop();if(++directories>MaximumDirectories)throw new InvalidDataException("The disabled mod contains too many folders.");
                CheckAncestors(folder);
                var entries=Directory.EnumerateFileSystemEntries(folder).Take(MaximumFiles+MaximumDirectories+1).ToArray();
                if(entries.Length>MaximumFiles+MaximumDirectories||entries.GroupBy(p=>Path.GetFileName(p),StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()>1))throw new InvalidDataException("The disabled mod has too many or ambiguous paths.");
                foreach(string entry in entries)
                {
                    ValidateLeaf(Path.GetFileName(entry));CheckAncestors(entry);
                    if(Directory.Exists(entry))folders.Push(entry);
                    else {BoundedFile(entry);files.Add(entry);if(files.Count>MaximumFiles)throw new InvalidDataException("The disabled mod contains too many files.");}
                }
            }
            return new Tree {Directory=true,Files=files.OrderBy(p=>p,StringComparer.Ordinal).ToArray()};
        }

        internal static string Fingerprint(string path,string[]? omit=null)
        {
            var tree=ReadTree(path);var excluded=omit??Array.Empty<string>();
            using(var manifest=new MemoryStream())
            using(var writer=new BinaryWriter(manifest,Encoding.UTF8,true))
            using(var hash=SHA256.Create())
            {
                writer.Write("DOORMAN-NOMM-unit-v1");writer.Write(tree.Directory);
                foreach(string file in tree.Files)
                {
                    string relative=tree.Directory?Canonical(file.Substring(path.Length+1)):"";
                    if(excluded.Any(p=>relative.Equals(Canonical(p),StringComparison.OrdinalIgnoreCase)||relative.StartsWith(Canonical(p)+"/",StringComparison.OrdinalIgnoreCase)))continue;
                    BoundedFile(file);
                    using(var input=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        InstalledFilePolicy.ValidateLength(input.Length);
                        writer.Write(relative);writer.Write(input.Length);writer.Write(hash.ComputeHash(input));
                    }
                }
                // New/deleted paths while hashing cannot silently enter the
                // selected unit after its manifest has been enumerated.
                var after=ReadTree(path);
                if(after.Directory!=tree.Directory||!after.Files.SequenceEqual(tree.Files,StringComparer.Ordinal))throw new IOException("The disabled-mod file list changed during verification.");
                writer.Flush();manifest.Position=0;return Hex(hash.ComputeHash(manifest));
            }
        }

        private static void BoundedFile(string path)
        {
            var file=new FileInfo(path);
            if(!file.Exists||(file.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Device))!=0)throw new InvalidDataException("Linked or special disabled-mod files are unsupported.");
            InstalledFilePolicy.ValidateLength(file.Length);
        }


        internal static void ValidateSource(string relative)
        {
            if(relative==null||relative.IndexOfAny(new[]{'/','\\'})>=0)throw new InvalidDataException("The disabled mod must be a direct child of disabledPlugins.");
            ValidateLeaf(relative);
        }

        internal static void ValidateTarget(string relative)
        {
            if(string.IsNullOrWhiteSpace(relative)||relative.Length>500||Path.IsPathRooted(relative))throw new InvalidDataException("Invalid disabled-mod target.");
            foreach(string leaf in relative.Split('/','\\'))ValidateLeaf(leaf);
        }

        private static void ValidateLeaf(string leaf)
        {
            if(string.IsNullOrWhiteSpace(leaf)||leaf.Length>255||leaf=="."||leaf==".."||leaf.EndsWith(".",StringComparison.Ordinal)||leaf.EndsWith(" ",StringComparison.Ordinal)||leaf.Any(c=>char.IsControl(c)||"<>:\"/\\|?*".IndexOf(c)>=0))throw new InvalidDataException("Invalid disabled-mod path.");
            string device=leaf.Split('.')[0];
            if(new[]{"CON","PRN","AUX","NUL","CONIN$","CONOUT$"}.Contains(device,StringComparer.OrdinalIgnoreCase)||device.Length==4&&(device.StartsWith("COM",StringComparison.OrdinalIgnoreCase)||device.StartsWith("LPT",StringComparison.OrdinalIgnoreCase))&&device[3]>='1'&&device[3]<='9')throw new InvalidDataException("Unsupported disabled-mod path name.");
        }

        private static string Root(string bepRoot)
        {
            if(string.IsNullOrWhiteSpace(bepRoot)||!Path.IsPathRooted(bepRoot))throw new InvalidDataException("BepInEx needs an absolute directory.");
            string full=Path.GetFullPath(bepRoot),root=full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(root.Length==0||root.Equals((Path.GetPathRoot(full)??"").TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("BepInEx cannot be a filesystem root.");
            CheckAncestors(root);if(!Directory.Exists(root))throw new IOException("BepInEx could not be found.");return root;
        }

        private static string Child(string root,string relative)
        {
            ValidateTarget(relative);
            string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar).Replace('\\',Path.DirectorySeparatorChar)));
            if(!Below(root,full))throw new InvalidDataException("The disabled-mod path escapes its folder.");
            CheckAncestors(full);
            string parent=root;
            foreach(string leaf in relative.Split('/','\\'))
            {
                if(Directory.Exists(parent))
                {
                    var matches=Directory.EnumerateFileSystemEntries(parent).Where(p=>Path.GetFileName(p).Equals(leaf,StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                    if(matches.Length>1||matches.Length==1&&!Path.GetFileName(matches[0]).Equals(leaf,StringComparison.Ordinal))throw new InvalidDataException("The disabled-mod path has an ambiguous filename.");
                }
                parent=Path.Combine(parent,leaf);
            }
            return full;
        }

        private static void CheckAncestors(string path)
        {
            for(string? current=path;current!=null;current=Path.GetDirectoryName(current))
            {
                FileAttributes attributes;
                try {attributes=File.GetAttributes(current);}
                catch(FileNotFoundException){continue;}
                catch(DirectoryNotFoundException){continue;}
                if((attributes&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked disabled-mod paths are unsupported.");
            }
        }

        private static void ValidateParents(string path)
        {
            for(string? current=Path.GetDirectoryName(path);current!=null;current=Path.GetDirectoryName(current))
                if(File.Exists(current))throw new IOException("A file occupies a disabled-mod destination folder.");
        }

        private static void CreateParents(string parent,List<string> created)
        {
            CheckAncestors(parent);ValidateParents(Path.Combine(parent,"placeholder"));
            var absent=new Stack<string>();
            for(string? path=parent;path!=null&&!Directory.Exists(path);path=Path.GetDirectoryName(path))absent.Push(path);
            while(absent.Count>0)
            {
                string path=absent.Pop();Directory.CreateDirectory(path);CheckAncestors(path);created.Add(path);
            }
        }

        private static bool Exists(string path)=>File.Exists(path)||Directory.Exists(path);
        private static bool Below(string root,string path)=>path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
        private static string Canonical(string path)=>path.Replace('\\','/');
        private static bool Equal(string first,string second)=>string.Equals(first,second,StringComparison.OrdinalIgnoreCase);
        private static string Hex(byte[] bytes)=>BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant();
    }
}
