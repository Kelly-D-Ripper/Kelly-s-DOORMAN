using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace KellysJOINCHECK
{
    [DataContract] internal sealed class ContentRecord
    {
        [DataMember] public string Id="",Name="",Version="",Source="";
        [DataMember] public string RelativeFile="",Digest="";
        // Local observation only. Never persist filesystem timestamps in a
        // portable receipt or use them as proof after another game startup.
        internal long ObservedSourceLength=-1,ObservedSourceWriteTicks=-1;
        internal InstalledContent ToInstalled()=>new InstalledContent { Id=Id,Name=Name,Version=Version,Source=Source,Enabled=false,Reloadable=false };
    }
    [DataContract] internal sealed class ServerTarget
    {
        [DataMember] public string Name="",Host="",Ip="",Port="",Wire="",Expanded="";
        [DataMember] public ulong SteamId=0;
        [DataMember] public bool Dedicated=false;
        internal bool Matches(bool dedicated,ulong steamId,string ip,string port)=>Dedicated==dedicated&&(Dedicated&&Ip.Length>0&&Port.Length>0?Ip==ip&&Port==port:SteamId!=0&&SteamId==steamId);
        internal static string Address(uint ip)=>ip==0?"":string.Join(".",new[]{ip>>24,(ip>>16)&255,(ip>>8)&255,ip&255});
        internal void Validate()
        {
            if(Name==null||Name.Length>256||Wire==null||Wire.Length>Protocol.MaxText||Expanded==null||Expanded.Length>Protocol.MaxText||Host==null||Host.Length>256||Ip==null||Ip.Length>128||Port==null||Port.Length>8) throw new InvalidDataException("Invalid saved server.");
            bool endpoint=IPAddress.TryParse(Ip,out _)&&ushort.TryParse(Port,out var port)&&port>0;
            if(!Dedicated&&SteamId==0||Dedicated&&(!endpoint&&SteamId==0||!endpoint&&(Ip.Length>0||Port.Length>0)))throw new InvalidDataException("This server has no usable Steam identity or connection address. Refresh the server browser.");
            if(!Signature.Parse(Expanded).Complete) throw new InvalidDataException("The server did not provide a complete mod list.");
        }
    }
    [DataContract] internal sealed class ServerSetupTicket
    {
        [DataMember] public int Schema=1;
        [DataMember] public long CreatedUtc=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        [DataMember] public bool ResumePending=true;
        [DataMember] public ServerTarget Target=new ServerTarget();
        [DataMember] public string[] DisabledContent=Array.Empty<string>(),DisabledPlugins=Array.Empty<string>();
        [DataMember] public ContentRecord[] Inventory=Array.Empty<ContentRecord>();
        internal void Validate()
        {
            if(Schema!=1||CreatedUtc>DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds()||CreatedUtc<DateTimeOffset.UtcNow.AddHours(-6).ToUnixTimeSeconds()||Target==null||Inventory==null||Inventory.Length>256||DisabledContent==null||DisabledPlugins==null||DisabledContent.Length>256||DisabledPlugins.Length>512) throw new InvalidDataException("The saved server setup expired or is unsupported.");
            Target.Validate();
            foreach(var id in DisabledContent.Concat(DisabledPlugins)) if(string.IsNullOrWhiteSpace(id)||id.Length>256||id.Any(char.IsControl)) throw new InvalidDataException("Invalid server mod selection.");
            if(DisabledPlugins.Any(ModProfile.Doorman)) throw new InvalidDataException("DOORMAN must stay enabled.");
            if(DisabledPlugins.Contains("com.nikkorap.blueprinter")&&Signature.Parse(Target.Expanded).Mods.Count!=0) throw new InvalidDataException("Blueprinter can be disabled only for a vanilla server.");
            foreach(var item in Inventory)
            {
                if(item==null||string.IsNullOrWhiteSpace(item.Id)||item.Id.Length>256||item.Name==null||item.Name.Length>256||item.Version==null||item.Version.Length>128||item.Source==null||item.Source.Length>2048||item.Digest==null||item.Digest.Length!=64||item.Digest.Any(c=>!Uri.IsHexDigit(c))) throw new InvalidDataException("Invalid content inventory.");
                if(string.IsNullOrWhiteSpace(item.RelativeFile)||item.RelativeFile.Length>500||Path.IsPathRooted(item.RelativeFile)||item.RelativeFile.Any(c=>char.IsControl(c)||c==':')||item.RelativeFile.Split('/','\\').Any(p=>p==".."||p=="."||p.Length==0)||!(item.RelativeFile.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)||item.RelativeFile.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("Invalid content inventory path.");
            }
        }
    }
    internal static class ServerPluginSelection
    {
        internal static string[] Resolve(IEnumerable<KeyValuePair<string,string[]>> plugins,IEnumerable<string> usualDisabled,IEnumerable<string> wrappers,IEnumerable<string> requiredWrappers,bool vanilla)
        {
            const string bp="com.nikkorap.blueprinter";
            var all=plugins.ToArray();var disabled=new HashSet<string>(usualDisabled,StringComparer.Ordinal);
            disabled.RemoveWhere(ModProfile.Doorman);
            if(vanilla) { foreach(var wrapper in wrappers.Where(id=>!ModProfile.Doorman(id)))disabled.Add(wrapper);disabled.Add(bp); }
            else
            {
                var required=new Queue<string>(requiredWrappers.Concat(new[]{bp}));var visited=new HashSet<string>(StringComparer.Ordinal);
                while(required.Count>0)
                {
                    string id=required.Dequeue();if(!visited.Add(id))continue;
                    var matches=all.Where(p=>p.Key==id).Take(2).ToArray();
                    if(matches.Length!=1)throw new InvalidDataException("Required plugin is missing or duplicated: "+Diagnostics.Clean(id));
                    disabled.Remove(id);foreach(var dependency in matches[0].Value)required.Enqueue(dependency);
                }
            }
            bool changed;
            do
            {
                changed=false;
                foreach(var plugin in all.Where(p=>p.Value.Any(disabled.Contains)))
                {
                    if(ModProfile.Protected(plugin.Key)&&!(vanilla&&plugin.Key==bp))throw new InvalidDataException("A required DOORMAN component needs a plugin this server setup would disable.");
                    changed|=disabled.Add(plugin.Key);
                }
            }while(changed);
            return disabled.OrderBy(id=>id,StringComparer.Ordinal).ToArray();
        }
    }
    internal static class ServerSetupFiles
    {
        internal const string Filename="doorman-server-setup.json";
        internal static void VerifySourceObservation(ContentRecord record,long length,long writeTicks,string digest,PendingModUpdate? update=null)
        {
            if(!ModUpdateFiles.HashValid(digest))throw new InvalidDataException("Installed content could not be verified.");
            if(update!=null)
            {
                if(!update.Target.Equals(record.RelativeFile,StringComparison.OrdinalIgnoreCase)||!ModUpdateFiles.HashValid(update.OldHash)||!digest.Equals(update.OldHash,StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The installed content changed after its update was downloaded. Review the update before restarting.");
                return;
            }
            if(record.ObservedSourceLength>=0)
            {
                if(length==record.ObservedSourceLength&&writeTicks==record.ObservedSourceWriteTicks)return;
                throw new IOException("The content file changed since Blueprinter loaded it. Restart normally to discover its current version before setting up a server.");
            }
            if(ModUpdateFiles.HashValid(record.Digest)&&digest.Equals(record.Digest,StringComparison.OrdinalIgnoreCase))return;
            throw new IOException("This content no longer matches its saved local receipt. Restart normally to discover its current version before setting up a server.");
        }
        internal static string RelativeContent(string pluginRoot,string file)
        {
            string root=Path.GetFullPath(pluginRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            string full=Path.GetFullPath(file);if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Content is outside the plugin folder.");
            string relative=full.Substring(root.Length);ModUpdateFiles.Within(pluginRoot,relative);
            if(!relative.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)&&!relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Unsupported content source.");return relative;
        }
        internal static bool KeepPlugin(string id,HashSet<string> disabled,bool vanilla)=>ModProfile.Doorman(id)||!disabled.Contains(id)||(ModProfile.Protected(id)&&!(vanilla&&id=="com.nikkorap.blueprinter"));
        internal static ServerSetupTicket? Pending(string configRoot)
        { try { var path=Path.Combine(configRoot,Filename);if(!File.Exists(path))return null;var ticket=ModJson.Read<ServerSetupTicket>(path);ticket.Validate();return ticket.ResumePending?ticket:null; }catch{return null;} }
        internal static void Save(string configRoot,ServerSetupTicket ticket)
        {
            ticket.Validate();Directory.CreateDirectory(configRoot);string path=Path.Combine(configRoot,Filename),temp=path+".tmp";
            using(var stream=new MemoryStream())
            { new DataContractJsonSerializer(typeof(ServerSetupTicket)).WriteObject(stream,ticket);if(stream.Length>65536)throw new InvalidDataException("Server setup is too large.");using(var output=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None)){stream.Position=0;stream.CopyTo(output);output.Flush(true);} }
            if(File.Exists(path))File.Replace(temp,path,path+".bak");else File.Move(temp,path);
        }
    }
    internal sealed class ServerSetupPlan
    {
        internal bool CanSetup,NeedsRestart,Vanilla;
        internal string Reason="";
        internal string[] DisabledContent=Array.Empty<string>();
        internal string[] DisabledPlugins=Array.Empty<string>();
        internal readonly Dictionary<string,string> NextVersions=new Dictionary<string,string>(StringComparer.Ordinal);
        internal PendingModUpdate[] Updates=Array.Empty<PendingModUpdate>();
        internal static ServerSetupPlan Create(string server,string local,IEnumerable<InstalledContent> content,string installedLoader,bool loaderActive,bool reloadReady,IEnumerable<KeyValuePair<string,string>>? queuedVersions=null,bool updatesPending=false)
        {
            var plan=new ServerSetupPlan();var wanted=Signature.Parse(server);var mine=Signature.Parse(local);var all=content.ToArray();
            if(!wanted.Complete||!mine.Complete){plan.Reason="Mod list unavailable. Ask the host to install DOORMAN Server.";return plan;}
            if(wanted.Game!=mine.Game){plan.Reason="The game versions differ. Match the host's Steam update or beta branch first.";return plan;}
            const string bp="com.nikkorap.blueprinter";plan.Vanilla=wanted.Mods.Count==0;
            if(!plan.Vanilla&&(!wanted.Mods.TryGetValue(bp,out var required)||required!=installedLoader)){plan.Reason="Install the server's exact Blueprinter version first.";return plan;}
            var queued=(queuedVersions??Array.Empty<KeyValuePair<string,string>>()).ToArray();
            if(queued.GroupBy(q=>q.Key,StringComparer.Ordinal).Any(g=>g.Count()>1)){plan.Reason="Multiple updates match one content pack. Review the update queue first.";return plan;}
            foreach(var item in all)
            {
                var update=queued.Where(q=>q.Key==item.Id).ToArray();string version=item.Version;
                if(update.Length==1)
                {
                    version=update[0].Value;
                    if(wanted.Mods.TryGetValue(item.Id,out var exact))
                    {
                        if(ModReleaseVersion.Compare(version,exact)!=0){plan.Reason="The downloaded update for "+Diagnostics.Clean(item.Id)+" is "+Diagnostics.Clean(version)+", but this server needs "+Diagnostics.Clean(exact)+". Choose the required version first.";return plan;}
                        version=exact;
                    }
                }
                plan.NextVersions[item.Id]=version;
            }
            foreach(var mod in wanted.Mods.Where(m=>m.Key!=bp))
                if(all.Count(c=>c.Id==mod.Key&&plan.NextVersions[c.Id]==mod.Value)!=1){plan.Reason="Install "+Diagnostics.Clean(mod.Key)+" "+Diagnostics.Clean(mod.Value)+" first. Setup uses installed mods and downloaded updates.";return plan;}
            plan.DisabledContent=all.Where(c=>!wanted.Mods.TryGetValue(c.Id,out var version)||version!=plan.NextVersions[c.Id]).Select(c=>c.Id).Distinct(StringComparer.Ordinal).ToArray();
            var changes=all.Where(c=>c.Enabled==plan.DisabledContent.Contains(c.Id,StringComparer.Ordinal)).ToArray();
            plan.NeedsRestart=updatesPending||queued.Length>0||(plan.Vanilla?loaderActive:!loaderActive||changes.Length>0&&(!reloadReady||changes.Any(c=>!c.Reloadable)));
            plan.CanSetup=true;plan.Reason=updatesPending?"Downloaded updates are waiting. Press Proceed to restart, install them and return to this server.":plan.NeedsRestart?"A game restart is needed to load this server's mod set. Press Proceed to restart and return here.":"Ready to set up the installed mods for this server.";return plan;
        }
    }
    internal sealed class StartupReadyGate
    {
        private double stableSince=-1;
        internal bool Check(double now,bool nativeLoaded,bool loaderFinished,bool menuSafe)
        { if(!nativeLoaded||!loaderFinished||!menuSafe){stableSince=-1;return false;}if(stableSince<0)stableSince=now;return now-stableSince>=2; }
    }
}
