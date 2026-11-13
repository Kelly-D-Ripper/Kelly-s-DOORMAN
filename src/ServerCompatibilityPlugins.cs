using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KellysJOINCHECK
{
    internal static class ServerCompatibilityPlugins
    {
        internal const string Resolute="com.resolute.nuclearoption";
        internal const string Growler="com.Spiny.GrowlerFrit";
        internal const string GrowlerName="E/Kr-67 (Ifrit remake)";
        internal static readonly string[] GrowlerComponents={Growler,"com.Spiny.MpBlocker","com.Spiny.JammerPod2","com.Spiny.ECMUpgrade","com.Spiny.SparkyDome","com.Spiny.Debugger"};

        // Resolute advertises its gameplay version/outfit separately from the
        // Blueprinter bundle list. Use its actual BepInPlugin GUID, never the
        // optional command addon or a NOMM package ID. Unknown tools stay alone.
        internal static string[] Exclusions(IEnumerable<string> installedPlugins,string serverWire,string localWire,string selected="",IEnumerable<string>? growlerUnit=null)
        {
            if(string.IsNullOrEmpty(serverWire)||string.IsNullOrEmpty(localWire))return Array.Empty<string>();
            var host=Signature.Parse(WithoutResolute(serverWire));
            if(!Signature.KnownGame(host.Game)||!host.Complete&&!host.KnownOpaqueHash)return Array.Empty<string>();
            var installed=new HashSet<string>(installedPlugins,StringComparer.Ordinal);
            var excluded=new List<string>();
            if(HasResolute(localWire)&&!HasResolute(serverWire)&&installed.Contains(Resolute))excluded.Add(Resolute);
            // MpBlocker's getter patch adds this literal BEFORE Blueprinter
            // hashes Application.version. An opaque mismatch alone cannot
            // justify excluding it: reproduce both exact observed wires.
            string expected=BlueprinterOnlyWire(selected),withGrowler=GrowlerWire(selected);
            var unit=(growlerUnit??Array.Empty<string>()).Where(id=>IsGrowler(id)&&installed.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
            if(unit.Contains(Growler,StringComparer.Ordinal)&&expected.Length>0&&withGrowler.Length>0
                &&WithoutResolute(serverWire)==expected&&WithoutResolute(localWire)==withGrowler)
                excluded.AddRange(unit);
            return excluded.ToArray();
        }

        internal static bool IsGrowler(string id)=>GrowlerComponents.Contains(id,StringComparer.Ordinal);
        internal static string[] GrowlerUnit(IEnumerable<KeyValuePair<string,string>> sources)
        {
            try
            {
                var items=sources.Take(513).ToArray();if(items.Length>512)return Array.Empty<string>();
                var comparison=Path.DirectorySeparatorChar=='\\'?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
                var roots=items.Where(item=>item.Key==Growler).Select(item=>Path.GetFullPath(item.Value)).Distinct(comparison).ToArray();
                if(roots.Length!=1||!items.Where(item=>item.Key==Growler).All(item=>Path.IsPathRooted(item.Value)))return Array.Empty<string>();
                return items.Where(item=>IsGrowler(item.Key)&&Path.IsPathRooted(item.Value)&&comparison.Equals(Path.GetFullPath(item.Value),roots[0]))
                    .Select(item=>item.Key).Distinct(StringComparer.Ordinal).ToArray();
            }
            catch(Exception ex) when(ex is ArgumentException||ex is IOException||ex is NotSupportedException){return Array.Empty<string>();}
        }
        internal static string Names(IEnumerable<string> ids)
        {
            var names=new List<string>();var values=ids.ToArray();
            if(values.Contains(Resolute,StringComparer.Ordinal))names.Add("Resolute");
            if(values.Any(IsGrowler))names.Add(GrowlerName);
            return string.Join(" and ",names);
        }
        private static string WithoutResolute(string wire)=>HasResolute(wire)?wire.Substring(0,wire.LastIndexOf("_rsl-v",StringComparison.Ordinal)):wire;
        internal static string GrowlerWire(string selected)
        {
            if(BlueprinterOnlyWire(selected).Length==0)return "";
            var parsed=Signature.Parse(selected);
            if(!selected.StartsWith(parsed.Game+"_com.nikkorap.blueprinter-v2.0.1_",StringComparison.Ordinal)
                ||parsed.Mods.ContainsKey("GrowlerFrit"))return "";
            string expanded=parsed.Game+"_GrowlerFrit-v1.0.0"+selected.Substring(parsed.Game.Length);
            if(expanded.Length<=100)return expanded;
            using(var sha=SHA256.Create())
                return parsed.Game+"_"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(expanded)),0,6).Replace("-","").ToLowerInvariant();
        }

        internal static bool HasResolute(string wire)
        {
            if(wire==null||wire.Length>Protocol.MaxText||wire.Any(char.IsControl))return false;
            int marker=wire.LastIndexOf("_rsl-v",StringComparison.Ordinal);
            if(marker<0)return false;
            string prefix=wire.Substring(0,marker),suffix=wire.Substring(marker+6);
            var source=Signature.Parse(prefix);
            if(!Signature.KnownGame(source.Game)||!source.Complete&&!source.KnownOpaqueHash)return false;
            int outfit=suffix.IndexOf('-');
            return outfit>0&&outfit<suffix.Length-1&&suffix.Length<=512
                &&Version.TryParse(suffix.Substring(0,outfit),out _);
        }

        // Diagnostic evidence only. This projection cannot replace the raw
        // application wire or authenticate any other code plugin's contribution.
        internal static string BlueprinterOnlyWire(string expanded)
        {
            var parsed=Signature.Parse(expanded);
            if(!parsed.Complete||!Signature.KnownGame(parsed.Game)
                ||!parsed.Mods.TryGetValue("com.nikkorap.blueprinter",out var version)||version!="2.0.1")return "";
            if(expanded.Length<=100)return expanded;
            using(var sha=SHA256.Create())
                return parsed.Game+"_"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(expanded)),0,6).Replace("-","").ToLowerInvariant();
        }
    }
}
