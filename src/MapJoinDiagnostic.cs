using System;
using System.Collections.Generic;
using System.Linq;

namespace KellysJOINCHECK
{
    // Uses only the map keys already registered by the game's map loader.
    // A file on disk and a map registered for this session are not the same thing.
    internal sealed class RegisteredMap
    {
        internal string Key = "", Name = "";
    }

    internal sealed class MapJoinDiagnostic
    {
        internal string Name = "Server map", Instruction = "The map loader could not load this map. Check the host's map and your map loader, then restart.", Report = "";
        internal string MapId = "", Build = "", Key = "", Repository = "";

        internal static MapJoinDiagnostic Create(string requested, string kind, string advertisedName, IEnumerable<RegisteredMap> registered)
        {
            var result = new MapJoinDiagnostic();
            string key = Text(requested, 96), mapKind = Text(kind, 32);
            string localKeys = "none";
            if (TryCustomKey(key, out var id, out var serverBuild))
            {
                result.MapId = id; result.Build = serverBuild; result.Key = key;
                var matches = (registered ?? Array.Empty<RegisteredMap>())
                    .Where(m => m != null && TryCustomKey(m.Key, out var candidate, out _) && candidate == id)
                    .GroupBy(m => m.Key, StringComparer.Ordinal).Select(g => g.First()).Take(8).ToArray();
                string name = matches.Select(m => Text(m.Name, 64)).FirstOrDefault(n => n.Length > 0) ?? "";
                if (name.Length == 0) name = Text(advertisedName, 64);
                if (name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) name = "";
                if (name.Length == 0) name = id.Replace('-', ' ');
                result.Name = name;
                localKeys = matches.Length == 0 ? "none registered" : string.Join(", ", matches.Select(m => m.Key));
                var builds = matches.Select(m => { TryCustomKey(m.Key, out _, out var hash); return hash; }).Distinct().ToArray();
                if (builds.Contains(serverBuild, StringComparer.Ordinal))
                    result.Instruction = "The required map is registered but could not load. Restart and check the map loader.";
                else if (builds.Length > 0)
                    result.Instruction = "The server needs map build " + serverBuild + "; yours is " + string.Join(", ", builds) + ". Update the map and restart.";
                else
                    result.Instruction = "The required map is not registered. Check the map and its loader are installed and enabled, then restart.";
            }
            else if (key.Equals("IronjawSound", StringComparison.OrdinalIgnoreCase))
            {
                result.Name = "Ironjaw Sound"; result.MapId = "ironjawsound"; result.Key = "IronjawSound";
                result.Repository = "Talamander/ironjaw-sound";
                result.Instruction = "Ironjaw Sound could not load. Setup can install its published map and loader; the server's map version is not advertised.";
                localKeys = (registered ?? Array.Empty<RegisteredMap>()).Any(m => m != null && string.Equals(m.Key, "IronjawSound", StringComparison.OrdinalIgnoreCase)) ? "IronjawSound" : "none registered";
            }
            else if (key.Length > 0)
            {
                string name = Text(advertisedName, 64);
                if (name.Length > 0 && !name.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) result.Name = name;
            }
            result.Report = "MAP UNAVAILABLE: (" + mapKind + "," + key + ")\n"
                + "Map: " + result.Name + "\nRegistered matching map IDs: " + localKeys + "\n" + result.Instruction;
            return result;
        }

        private static string Text(string value, int limit)
            => new string((value ?? "").Where(c => !char.IsControl(c) && c != '<' && c != '>').Take(limit).ToArray()).Trim();

        internal static bool TryCustomKey(string key, out string id, out string hash)
        {
            id = hash = "";
            if (key == null || !key.StartsWith("cm.", StringComparison.Ordinal)) return false;
            int end = key.LastIndexOf('.');
            if (end <= 3 || end != key.Length - 9) return false;
            string candidate = key.Substring(3, end - 3), suffix = key.Substring(end + 1);
            if (candidate.Length < 2 || candidate.Length > 32 || candidate[0] == '-' || candidate[candidate.Length - 1] == '-') return false;
            if (!candidate.All(c => c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-')) return false;
            if (!suffix.All(c => c >= 'a' && c <= 'f' || c >= '0' && c <= '9')) return false;
            id = candidate; hash = suffix; return true;
        }
    }
}
