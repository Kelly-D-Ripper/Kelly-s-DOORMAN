using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KellysJOINCHECK
{
    internal sealed class Signature
    {
        public string Game = "";
        public bool Complete = true;
        internal bool KnownOpaqueHash;
        public readonly Dictionary<string, string> Mods = new Dictionary<string, string>(StringComparer.Ordinal);
        public static Signature Parse(string text)
        {
            var result = new Signature();
            if (string.IsNullOrWhiteSpace(text) || text.Length > Protocol.MaxText) { result.Complete = false; return result; }
            int split = text.IndexOf('_');
            result.Game = split < 0 ? text : text.Substring(0, split);
            if (text.Any(char.IsControl)) { result.Complete = false; return result; }
            if (split < 0) return result;
            string tail = text.Substring(split + 1);
            if (tail.Length == 0) { result.Complete = false; return result; }
            // Another plugin may append its own version after Blueprinter's
            // opaque hash. That suffix cannot reveal the hashed mod inventory.
            int suffix = tail.IndexOf('_');
            string first = suffix < 0 ? tail : tail.Substring(0, suffix);
            if (first.Length >= 12 && first.Length <= 64 && first.All(c =>
                c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'))
            {
                result.Complete = false;
                result.KnownOpaqueHash = first.Length == 12 && (suffix < 0 || OpaqueSuffix(tail.Substring(suffix + 1)));
                return result;
            }
            // Blueprinter 2.x separates bundles with _--. Underscores inside names are significant.
            var parts = tail.Split(new[] { "_--" }, StringSplitOptions.None);
            foreach (string raw in parts)
            {
                string token = raw.StartsWith("--", StringComparison.Ordinal) ? raw.Substring(2) : raw;
                // The loader token ends in _NOBUNDLES when no bundles were loaded.
                if (token.EndsWith("_NOBUNDLES", StringComparison.Ordinal)) token = token.Substring(0, token.Length - 10);
                if (token == "NOBUNDLES" || token == "") continue;
                int v = token.LastIndexOf("-v", StringComparison.Ordinal);
                if (v <= 0 || v + 2 >= token.Length) { result.Complete = false; continue; }
                string name = token.Substring(0, v), version = token.Substring(v + 2);
                if (version.Contains("_") || result.Mods.ContainsKey(name)) { result.Complete = false; continue; }
                result.Mods.Add(name, version);
            }
            return result;
        }
        internal static bool KnownGame(string game) => game.Length <= 64 && game == game.Trim()
            && Version.TryParse(game, out _);
        private static bool OpaqueSuffix(string suffix)
        {
            // Recognise only a bounded version-extension frame. Its name and
            // payload stay opaque and never become a join-mod identity.
            int marker = suffix.LastIndexOf("-v", StringComparison.Ordinal);
            return suffix.Length <= 512 && marker > 0 && marker + 2 < suffix.Length
                && suffix[marker + 2] >= '0' && suffix[marker + 2] <= '9'
                && suffix.All(c => c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z'
                    || c >= '0' && c <= '9' || c == '.' || c == '-' || c == '_');
        }
    }

    internal static class DiagnosticSignature
    {
        // Keep the loader's full status in the support report while the join
        // screen uses a short explanation of what the player can do next.
        internal static string FriendlyNotice(string details)
        {
            if(string.IsNullOrWhiteSpace(details))return "";
            if(details.IndexOf("still loading",StringComparison.OrdinalIgnoreCase)>=0)return "Mods are still loading. Please wait.";
            var parts=new List<string>();
            if(details.IndexOf("unverified",StringComparison.OrdinalIgnoreCase)>=0)parts.Add("The complete mod match could not be verified.");
            if(details.IndexOf("asset patches unresolved",StringComparison.OrdinalIgnoreCase)>=0)
                parts.Add("Some mod assets could not load. A restart may not fix this. Copy the report for details.");
            else if(details.IndexOf("reload is unavailable",StringComparison.OrdinalIgnoreCase)>=0)
                parts.Add("Mods cannot reload in game. Copy the report for details.");
            return parts.Count>0?string.Join(" ",parts):"Mod loading needs attention. Copy the report for details.";
        }
        // This projection describes the loader's selected versions. It is never
        // a replacement for the wire identity or a host advertisement.
        internal static string Select(string wire, string verified, string loaderVersion, string? bundles,
            bool loadingFinished, bool reloadReady, string loadingStatus, out string notice)
            => Select(wire, verified, loaderVersion, bundles, loadingFinished, reloadReady, loadingStatus, out notice, out _);

        internal static string Select(string wire, string verified, string loaderVersion, string? bundles,
            bool loadingFinished, bool reloadReady, string loadingStatus, out string notice, out bool wireVerified)
        {
            notice = "";
            wireVerified = Signature.Parse(verified).Complete;
            if (wireVerified)
            {
                notice = LoadingNotice(loaderVersion, loadingFinished, reloadReady, loadingStatus);
                return verified;
            }
            if (loaderVersion.Length == 0) return wire;
            if (!loadingFinished)
            {
                notice = "Blueprinter is still loading. Local mod versions are not yet available.";
                return wire;
            }
            string game = Signature.Parse(wire).Game;
            if (!Signature.KnownGame(game) || loaderVersion.Length > 64 || !Version.TryParse(loaderVersion, out var version) || version.Major != 2
                || bundles == null || bundles.Length > Protocol.MaxText
                || !(bundles == "NOBUNDLES" || bundles.StartsWith("--", StringComparison.Ordinal))) return wire;
            string selected = game + "_com.nikkorap.blueprinter-v" + loaderVersion + "_" + bundles;
            var parsed = Signature.Parse(selected);
            if (!parsed.Complete || !parsed.Mods.TryGetValue("com.nikkorap.blueprinter", out var actual) || actual != loaderVersion) return wire;
            notice = "Showing Blueprinter's selected content versions. The full game compatibility string remains unverified.";
            string loading = LoadingNotice(loaderVersion, loadingFinished, reloadReady, loadingStatus);
            if (loading.Length > 0) notice += " " + loading;
            return selected;
        }
        private static string LoadingNotice(string loaderVersion, bool loadingFinished, bool reloadReady, string loadingStatus)
        {
            if (loaderVersion.Length == 0) return "";
            if (!loadingFinished) return "Blueprinter is still loading. Wait for content loading to finish.";
            return reloadReady ? "" : "Blueprinter finished its loading routine, but live reload is unavailable. "
                + Diagnostics.Clean(loadingStatus.Length > 0 ? loadingStatus : "Copy report to review its loading errors.");
        }
    }

    internal static class Diagnostics
    {
        public static string Clean(string value) => new string((value ?? "").Take(Protocol.MaxText)
            .Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray());

        public static string Compare(string server, string local, bool localWireVerified = true)
        {
            var wanted = Signature.Parse(server);
            var mine = Signature.Parse(local);
            var output = new StringBuilder();
            if (wanted.Game != mine.Game)
                output.AppendLine($"GAME VERSION: server {Clean(wanted.Game)} | you {Clean(mine.Game)}\nUse the same Nuclear Option update and Steam beta branch as the host.");
            if (wanted.Complete && mine.Complete)
            {
                foreach (var mod in wanted.Mods.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    if (!mine.Mods.TryGetValue(mod.Key, out var installed))
                        output.AppendLine($"MISSING: {Clean(mod.Key)} - install {Clean(mod.Value)}");
                    else if (installed != mod.Value)
                        output.AppendLine($"WRONG VERSION: {Clean(mod.Key)} - server {Clean(mod.Value)} | you {Clean(installed)}");
                }
                foreach (var mod in mine.Mods.Where(x => !wanted.Mods.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
                    output.AppendLine($"EXTRA: {Clean(mod.Key)} {Clean(mod.Value)} - remove from this server's mod set");
                if (server != local && output.Length == 0)
                    output.AppendLine("The named versions match, but their compatibility strings differ. Match the host's exact files and bundle loading order.");
                if (server == local) output.AppendLine(localWireVerified ? "The advertised game and mod compatibility strings match."
                    : "The named game and mod versions match. The full game compatibility string remains unverified.");
            }
            else if (!wanted.Complete)
                output.AppendLine("Mod list unavailable. Ask the host to install DOORMAN Server.");
            else
                output.AppendLine("Your local mod versions could not be verified. Wait for content loading to finish, then copy the report if the problem remains.");
            if (wanted.Mods.Count > 0 || mine.Mods.Count > 0)
                output.AppendLine("Only required join mods are compared.");
            return output.ToString();
        }

        public static bool Relevant(string text) => Regex.IsMatch(text ?? "", "incorrect version|version mismatch|mod.?mismatch|build number|build.?hash|missing.*(mod|map)|incompatible", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    internal static class Protocol
    {
        public const string Header = "kjc";
        public const int MaxText = 8192, ChunkSize = 96, MaxChunks = 32;
        public static Dictionary<string, string> Encode(string wire, string expanded)
        {
            string raw = "1\n" + wire + "\n" + expanded;
            if (raw.Length > MaxText || wire.Contains("\n") || expanded.Contains("\n")) throw new InvalidDataException("Signature too large or invalid");
            byte[] bytes = Encoding.UTF8.GetBytes(raw);
            using var stream = new MemoryStream();
            using (var zip = new DeflateStream(stream, CompressionLevel.Optimal, true)) zip.Write(bytes, 0, bytes.Length);
            string encoded = Convert.ToBase64String(stream.ToArray());
            int count = (encoded.Length + ChunkSize - 1) / ChunkSize;
            if (count > MaxChunks) throw new InvalidDataException("Compressed signature too large");
            var result = new Dictionary<string, string>();
            for (int i = 0; i < count; i++) result[Key(i)] = encoded.Substring(i * ChunkSize, Math.Min(ChunkSize, encoded.Length - i * ChunkSize));
            result[Header] = "1:" + count.ToString(CultureInfo.InvariantCulture);
            return result;
        }
        public static string Key(int index) => "kjc" + index.ToString("D2", CultureInfo.InvariantCulture);
        public static string? Decode(Func<string, string> read, string expectedWire)
        {
            try
            {
                string header = read(Header);
                if (!header.StartsWith("1:", StringComparison.Ordinal) || !int.TryParse(header.Substring(2), out int count) || count < 1 || count > MaxChunks) return null;
                var encoded = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    string part = read(Key(i));
                    if (part.Length == 0 || part.Length > ChunkSize) return null;
                    encoded.Append(part);
                }
                using var stream = new MemoryStream(Convert.FromBase64String(encoded.ToString()));
                using var zip = new DeflateStream(stream, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[512];
                int n;
                while ((n = zip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + n > MaxText * 4) return null;
                    output.Write(buffer, 0, n);
                }
                string raw = new UTF8Encoding(false, true).GetString(output.ToArray());
                if (raw.Length > MaxText) return null;
                var fields = raw.Split('\n');
                if (fields.Length != 3 || fields[0] != "1" || fields[1] != expectedWire || !Signature.Parse(fields[2]).Complete) return null;
                return fields[2];
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is FormatException || ex is ArgumentException) { return null; }
        }
    }
}
