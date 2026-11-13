using System;
using System.Globalization;
using System.IO;

namespace KellysJOINCHECK
{
    // A small, expiring handoff between the Windows helper and the next client.
    // It is never read on a gameplay timer or used to choose/install files.
    internal static class RestartStatus
    {
        private const string Pointer="doorman-restart-session.txt";
        internal static bool ValidName(string name)=>name.Length==46&&name.StartsWith("doorman-",StringComparison.Ordinal)&&name.EndsWith(".ready",StringComparison.Ordinal)&&Guid.TryParseExact(name.Substring(8,32),"N",out _);
        internal static void Begin(string cache,string ready)
        {
            string name=Path.GetFileName(ready);
            if(!ValidName(name)||!Path.GetFullPath(ready).Equals(Path.Combine(Path.GetFullPath(cache),name),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Invalid restart status path.");
            Directory.CreateDirectory(cache);File.WriteAllText(Path.Combine(cache,Pointer),DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture)+"\n"+name);Write(cache,"configuring");
        }
        private static string? Current(string cache)
        {
            string file=Path.Combine(cache,Pointer);if(!File.Exists(file)||new FileInfo(file).Length>160)return null;
            string[] lines=File.ReadAllText(file).Split('\n');
            if(lines.Length!=2||!long.TryParse(lines[0],NumberStyles.None,CultureInfo.InvariantCulture,out var ticks)||ticks>DateTime.UtcNow.AddMinutes(1).Ticks||ticks<DateTime.UtcNow.AddMinutes(-15).Ticks||!ValidName(lines[1]))return null;
            return Path.Combine(cache,lines[1]);
        }
        internal static void Write(string cache,string state)
        {
            if(state!="configuring"&&state!="starting"&&state!="loading"&&state!="ready")throw new ArgumentException("Invalid restart stage.");
            try {string? path=Current(cache);if(path!=null)File.WriteAllText(path+".status",state);}catch { }
        }
        internal static string Read(string ready)
        {try {string path=ready+".status";return File.Exists(path)&&new FileInfo(path).Length<=32?File.ReadAllText(path):"";}catch{return "";} }
        internal static void Clear(string cache,string ready)
        {try {if(Current(cache)==ready)File.Delete(Path.Combine(cache,Pointer));File.Delete(ready+".status");}catch { } }
    }
}
