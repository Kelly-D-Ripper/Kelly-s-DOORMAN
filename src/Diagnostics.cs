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
        public readonly Dictionary<string, string> Mods = new Dictionary<string, string>(StringComparer.Ordinal);
        public static Signature Parse(string text)
        {
            var result = new Signature();
            if (string.IsNullOrWhiteSpace(text) || text.Length > Protocol.MaxText) { result.Complete = false; return result; }
            int split = text.IndexOf('_');
            result.Game = split < 0 ? text : text.Substring(0, split);
            if (split < 0) return result;
            string tail = text.Substring(split + 1);
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
    }

    internal static class Diagnostics
    {
        public static string Clean(string value) => new string((value ?? "").Take(Protocol.MaxText)
            .Where(c => !char.IsControl(c) || c == '\n' || c == '\t').ToArray());

        public static string Compare(string server, string local)
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
                if (server == local) output.AppendLine("The advertised game and mod compatibility strings match.");
            }
            else
                output.AppendLine("Mod list unavailable. Ask the host to install DOORMAN Server.");
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
