using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace KellysJOINCHECK
{
    internal sealed class GameplayFileRecord
    {
        public string Kind = "";
        public string Id = "";
        public string Version = "";
        public string Hash = "";
    }

    internal sealed class GameplayContentVersion
    {
        public string Id = "";
        public string Version = "";
    }

    internal sealed class GameplayFingerprintSnapshot
    {
        public bool Ready;
        // Complete means every identified source file was hashed. Asset patch
        // application is separate loading health, not a claim made by hashes.
        public bool Complete;
        public string Error = "";
        public string Scope = GameplayFingerprintPolicy.Scope;
        public GameplayFileRecord[] Records = Array.Empty<GameplayFileRecord>();
        public string LoaderVersion = "";
        public string ContentSetHash = "";
        public GameplayContentVersion[] ContentSelection = Array.Empty<GameplayContentVersion>();
        // No visual/gameplay classification is inferred from this count.
        public int PendingAssetPatches;

        public static GameplayFingerprintSnapshot Unavailable(string error) => new GameplayFingerprintSnapshot {
            Error = CleanError(error ?? "")
        };
        private static string CleanError(string error)
        {
            var text = new StringBuilder();
            for (int i = 0; i < error.Length && text.Length < GameplayFingerprintPolicy.MaxError; i++)
            {
                char c = error[i];
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 < error.Length && char.IsLowSurrogate(error[i + 1]))
                    {
                        if (text.Length + 2 > GameplayFingerprintPolicy.MaxError) break;
                        text.Append(c).Append(error[++i]);
                    }
                    else text.Append('?');
                }
                else text.Append(char.IsControl(c) ? ' ' : char.IsLowSurrogate(c) ? '?' : c);
            }
            return text.ToString().Trim();
        }
    }

    internal static class GameplayFingerprintPolicy
    {
        public const string Scope = "audited-v2";
        public const int MaxRecords = 128, MaxId = 256, MaxVersion = 64, MaxError = 256, MaxPendingAssetPatches = 8192;
        // Audited gameplay modifiers only. Aircraft/Harmony references alone
        // do not classify unrelated HUD, audio, replay or input plugins.
        private static readonly HashSet<string> audited = new HashSet<string>(new[] {
            "com.ial.mig15s", "com.ial.oa27variant", "com.nuclearoption.fs20plus",
            "com.ta30plus.northstar", "com.mursisru.x77warewind", "com.iallemege.kh38mt",
            "com.resolute.nuclearoption",
            // KR-EW.67.dll declares this entire inseparable release family,
            // including its companion logger. Each keeps its actual GUID.
            "com.Spiny.GrowlerFrit", "com.Spiny.MpBlocker", "com.Spiny.JammerPod2",
            "com.Spiny.ECMUpgrade", "com.Spiny.SparkyDome", "com.Spiny.Debugger"
        }, StringComparer.Ordinal);
        public static IReadOnlyCollection<string> AuditedPluginIds { get; } = Array.AsReadOnly(audited.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        public static bool IsAuditedPlugin(string id) => id != null && audited.Contains(id);

        internal static bool Text(string? text, int max, bool empty = false) => text != null
            && text.Length <= max && (empty || text.Length > 0)
            && text == text.Trim() && !text.Any(char.IsControl)
            && ValidUnicode(text);
        internal static bool FullHash(string? hash) => hash != null && hash.Length == 64
            && hash.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F');
        private static bool ValidUnicode(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]))
                {
                    if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false;
                }
                else if (char.IsLowSurrogate(text[i])) return false;
            }
            return true;
        }

        // Copies caller-owned records before comparison/transport and rejects
        // ambiguity instead of presenting a partial inventory as verified.
        internal static bool Normalize(GameplayFingerprintSnapshot? source, out GameplayFingerprintSnapshot copy, out string error)
        {
            copy = GameplayFingerprintSnapshot.Unavailable("");
            error = "Gameplay file fingerprints are unavailable.";
            if (source == null) return false;
            if (source.Scope != Scope) { error = "Unsupported gameplay fingerprint scope."; return false; }
            if (!Text(source.Error, MaxError, true)) { error = "Invalid gameplay fingerprint status."; return false; }
            if (source.Ready && source.Complete && source.Error.Length > 0) { error = "Gameplay fingerprints reported an error."; return false; }
            if (!Text(source.LoaderVersion, MaxVersion, true) || source.LoaderVersion.Length > 0 && !Version.TryParse(source.LoaderVersion, out _))
            { error = "Invalid gameplay content loader version."; return false; }
            if (source.PendingAssetPatches < 0 || source.PendingAssetPatches > MaxPendingAssetPatches
                || source.PendingAssetPatches > 0 && source.LoaderVersion.Length == 0)
            { error = "Invalid pending asset patch status."; return false; }
            if (source.ContentSetHash == null || source.ContentSetHash.Length > 0 && !FullHash(source.ContentSetHash))
            { error = "Invalid gameplay content set fingerprint."; return false; }
            if (source.ContentSelection == null || source.ContentSelection.Length > MaxRecords)
            { error = "Invalid gameplay content selection."; return false; }
            if (source.ContentSelection.Length > 0 && source.ContentSetHash.Length == 0)
            { error = "A compact gameplay content selection has no complete set fingerprint."; return false; }
            if (source.Records == null || source.Records.Length > MaxRecords) { error = "Gameplay fingerprint inventory is too large or invalid."; return false; }
            if (source.Records.Length + source.ContentSelection.Length > MaxRecords) { error = "Gameplay fingerprint inventory is too large."; return false; }
            var records = new List<GameplayFileRecord>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var record in source.Records)
            {
                if (record == null || record.Kind != "plugin" && record.Kind != "content"
                    || !Text(record.Id, MaxId) || !Text(record.Version, MaxVersion)
                    || !FullHash(record.Hash)) { error = "Invalid gameplay file identity or fingerprint."; return false; }
                if (record.Kind == "plugin" && !IsAuditedPlugin(record.Id)) { error = "Unsupported gameplay plugin identity."; return false; }
                if (!ids.Add(record.Kind + "\n" + record.Id)) { error = "Duplicate gameplay file identity."; return false; }
                records.Add(new GameplayFileRecord { Kind = record.Kind, Id = record.Id, Version = record.Version, Hash = record.Hash.ToLowerInvariant() });
            }
            if (source.ContentSetHash.Length > 0 && records.Any(x => x.Kind == "content"))
            { error = "Mixed full and compact content fingerprints are invalid."; return false; }
            var selected = new List<GameplayContentVersion>();
            foreach (var entry in source.ContentSelection)
            {
                if (entry == null || !Text(entry.Id, MaxId) || !Text(entry.Version, MaxVersion))
                { error = "Invalid gameplay content selection identity."; return false; }
                if (!ids.Add("content\n" + entry.Id)) { error = "Duplicate gameplay content selection identity."; return false; }
                selected.Add(new GameplayContentVersion { Id = entry.Id, Version = entry.Version });
            }
            copy = new GameplayFingerprintSnapshot {
                Ready = source.Ready, Complete = source.Complete, Error = source.Error, Scope = Scope,
                Records = records.OrderBy(x => x.Kind, StringComparer.Ordinal).ThenBy(x => x.Id, StringComparer.Ordinal).ToArray(),
                LoaderVersion = source.LoaderVersion, ContentSetHash = source.ContentSetHash.ToLowerInvariant(),
                ContentSelection = selected.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray(),
                PendingAssetPatches = source.PendingAssetPatches
            };
            error = "";
            return true;
        }

        public static GameplayContentVersion[] SelectedContent(GameplayFingerprintSnapshot source)
        {
            if (!Normalize(source, out var copy, out string error)) throw new InvalidDataException(error);
            return Selection(copy);
        }
        private static GameplayContentVersion[] Selection(GameplayFingerprintSnapshot source)
            => source.ContentSetHash.Length > 0 ? source.ContentSelection : source.Records.Where(x => x.Kind == "content")
                .Select(x => new GameplayContentVersion { Id = x.Id, Version = x.Version }).ToArray();

        public static string ContentHash(IEnumerable<GameplayFileRecord> source)
        {
            var records = (source ?? throw new InvalidDataException("Content files are missing.")).Take(MaxRecords + 1).ToArray();
            if (records.Length > MaxRecords || records.Any(x => x == null)) throw new InvalidDataException("Content fingerprint inventory is invalid.");
            var content = records.Where(x => x.Kind == "content").OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var raw = new StringBuilder("content-set-v1\n");
            foreach (var record in content)
            {
                if (!Text(record.Id, MaxId) || !Text(record.Version, MaxVersion) || !FullHash(record.Hash) || !ids.Add(record.Id))
                    throw new InvalidDataException("Content fingerprint identity is invalid or duplicated.");
                raw.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(record.Id))).Append('\t')
                    .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(record.Version))).Append('\t')
                    .Append(record.Hash.ToLowerInvariant()).Append('\n');
            }
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(raw.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        public static GameplayFingerprintSnapshot CompactContent(GameplayFingerprintSnapshot source)
        {
            if (!Normalize(source, out var copy, out string error)) throw new InvalidDataException(error);
            if (!copy.Ready || !copy.Complete || copy.ContentSetHash.Length > 0) return copy;
            copy.ContentSelection = Selection(copy);
            copy.ContentSetHash = ContentHash(copy.Records);
            copy.Records = copy.Records.Where(x => x.Kind == "plugin").ToArray();
            return copy;
        }

        // This is an actual-inventory diagnostic expansion, not a replacement
        // for the server's raw wire or the game's authentication decisions.
        public static string Expanded(GameplayFingerprintSnapshot source, string rawWire)
        {
            if (!Normalize(source, out var copy, out _) || !copy.Ready || !copy.Complete
                || !Text(rawWire, GameplayFingerprintProtocol.MaxWire)) return "";
            string game = Signature.Parse(rawWire).Game;
            if (!Signature.KnownGame(game)) return "";
            var content = Selection(copy);
            if (copy.LoaderVersion.Length == 0) return content.Length == 0 ? game : "";
            string value = game + "_com.nikkorap.blueprinter-v" + copy.LoaderVersion + "_"
                + (content.Length == 0 ? "NOBUNDLES" : string.Join("_", content.Select(x => "--" + x.Id + "-v" + x.Version)));
            var parsed = Signature.Parse(value);
            return parsed.Complete && parsed.Mods.Count == content.Length + 1
                && content.All(x => parsed.Mods.TryGetValue(x.Id, out var actual) && actual == x.Version) ? value : "";
        }
    }

    internal enum GameplayFingerprintDifferenceType { Missing, Different, Extra }

    internal sealed class GameplayFingerprintDifference
    {
        public GameplayFingerprintDifferenceType Type;
        public string Kind = "";
        public string Id = "";
        public string ServerVersion = "";
        public string LocalVersion = "";
        public string ServerHash = "";
        public string LocalHash = "";
    }

    internal sealed class GameplayFingerprintComparison
    {
        // Verified covers this explicit scope only. It does not replace game
        // wire/build authentication or attest an untrusted client's runtime.
        public bool Available;
        public bool Verified;
        public string Error = "";
        public int ServerPendingAssetPatches;
        public int LocalPendingAssetPatches;
        public readonly List<GameplayFingerprintDifference> Differences = new List<GameplayFingerprintDifference>();
        public string[] ExtraPluginIds => Available ? Differences
            .Where(x => x.Type == GameplayFingerprintDifferenceType.Extra && x.Kind == "plugin")
            .Select(x => x.Id).ToArray() : Array.Empty<string>();

        public static GameplayFingerprintComparison Compare(GameplayFingerprintSnapshot? remote, GameplayFingerprintSnapshot? local)
        {
            var result = new GameplayFingerprintComparison();
            if (!GameplayFingerprintPolicy.Normalize(remote, out var wanted, out string error)) { result.Error = error; return result; }
            if (!wanted.Ready || !wanted.Complete) { result.Error = wanted.Error.Length > 0 ? wanted.Error : "The server's gameplay fingerprints are not ready or complete."; return result; }
            result.ServerPendingAssetPatches = wanted.PendingAssetPatches;
            if (!GameplayFingerprintPolicy.Normalize(local, out var mine, out error)) { result.Error = error; return result; }
            result.LocalPendingAssetPatches = mine.PendingAssetPatches;
            if (!mine.Ready || !mine.Complete) { result.Error = mine.Error.Length > 0 ? mine.Error : "Your gameplay fingerprints are not ready or complete."; return result; }
            bool compact = wanted.ContentSetHash.Length > 0 || mine.ContentSetHash.Length > 0;
            var wantedFiles = compact ? wanted.Records.Where(x => x.Kind == "plugin").ToArray() : wanted.Records;
            var mineFiles = compact ? mine.Records.Where(x => x.Kind == "plugin").ToArray() : mine.Records;
            var installed = mineFiles.ToDictionary(x => x.Kind + "\n" + x.Id, StringComparer.Ordinal);
            var required = wantedFiles.ToDictionary(x => x.Kind + "\n" + x.Id, StringComparer.Ordinal);
            foreach (var record in wantedFiles)
            {
                if (!installed.TryGetValue(record.Kind + "\n" + record.Id, out var actual))
                    result.Differences.Add(Difference(GameplayFingerprintDifferenceType.Missing, record, null));
                else if (record.Hash != actual.Hash || record.Version != actual.Version)
                    result.Differences.Add(Difference(GameplayFingerprintDifferenceType.Different, record, actual));
            }
            foreach (var record in mineFiles)
                if (!required.ContainsKey(record.Kind + "\n" + record.Id))
                    result.Differences.Add(Difference(GameplayFingerprintDifferenceType.Extra, null, record));
            if (compact)
            {
                var wantedContent = GameplayFingerprintPolicy.SelectedContent(wanted);
                var mineContent = GameplayFingerprintPolicy.SelectedContent(mine);
                var selected = mineContent.ToDictionary(x => x.Id, StringComparer.Ordinal);
                var expected = wantedContent.ToDictionary(x => x.Id, StringComparer.Ordinal);
                int changes = 0;
                foreach (var record in wantedContent)
                {
                    if (!selected.TryGetValue(record.Id, out var actual))
                    {
                        result.Differences.Add(Difference(GameplayFingerprintDifferenceType.Missing,
                            new GameplayFileRecord { Kind = "content", Id = record.Id, Version = record.Version }, null));
                        changes++;
                    }
                    else if (record.Version != actual.Version)
                    {
                        result.Differences.Add(Difference(GameplayFingerprintDifferenceType.Different,
                            new GameplayFileRecord { Kind = "content", Id = record.Id, Version = record.Version },
                            new GameplayFileRecord { Kind = "content", Id = actual.Id, Version = actual.Version }));
                        changes++;
                    }
                }
                foreach (var record in mineContent)
                    if (!expected.ContainsKey(record.Id))
                    {
                        result.Differences.Add(Difference(GameplayFingerprintDifferenceType.Extra, null,
                            new GameplayFileRecord { Kind = "content", Id = record.Id, Version = record.Version }));
                        changes++;
                    }
                string wantedHash = wanted.ContentSetHash.Length > 0 ? wanted.ContentSetHash : GameplayFingerprintPolicy.ContentHash(wanted.Records);
                string mineHash = mine.ContentSetHash.Length > 0 ? mine.ContentSetHash : GameplayFingerprintPolicy.ContentHash(mine.Records);
                if (changes == 0 && wantedHash != mineHash)
                    result.Differences.Add(Difference(GameplayFingerprintDifferenceType.Different,
                        new GameplayFileRecord { Kind = "content", Id = "Selected content files", Hash = wantedHash },
                        new GameplayFileRecord { Kind = "content", Id = "Selected content files", Hash = mineHash }));
            }
            result.Available = true;
            result.Verified = result.Differences.Count == 0;
            return result;
        }

        private static GameplayFingerprintDifference Difference(GameplayFingerprintDifferenceType type, GameplayFileRecord? wanted, GameplayFileRecord? actual)
            => new GameplayFingerprintDifference {
                Type = type, Kind = (wanted ?? actual)!.Kind, Id = (wanted ?? actual)!.Id,
                ServerVersion = wanted?.Version ?? "", LocalVersion = actual?.Version ?? "",
                ServerHash = wanted?.Hash ?? "", LocalHash = actual?.Hash ?? ""
            };
    }

    internal static class GameplayFingerprintProtocol
    {
        public const string Header = "gfp";
        public const int Version = 3;
        public const int MaxChunks = 32, ChunkSize = 96, MaxWire = 8192, MaxBytes = 32768;
        private static readonly UTF8Encoding utf8 = new UTF8Encoding(false, true);
        public static string Key(int index)
        {
            if (index < 0 || index >= MaxChunks) throw new ArgumentOutOfRangeException(nameof(index));
            return "gfp" + index.ToString("D2", CultureInfo.InvariantCulture);
        }
        public static Dictionary<string, string> Encode(string wire, GameplayFingerprintSnapshot snapshot)
        {
            if (!GameplayFingerprintPolicy.Text(wire, MaxWire)) throw new InvalidDataException("Invalid server wire identity.");
            if (!GameplayFingerprintPolicy.Normalize(snapshot, out var copy, out string error)) throw new InvalidDataException(error);
            // An incomplete scan advertises only its unavailable status. It
            // must never deliver a deceptively useful partial comparison.
            if (!copy.Ready || !copy.Complete)
            { copy.Records = Array.Empty<GameplayFileRecord>(); copy.ContentSelection = Array.Empty<GameplayContentVersion>(); copy.ContentSetHash = ""; }
            var raw = new StringBuilder("3\n").Append(Field(wire)).Append('\n').Append(copy.Scope).Append('\n')
                .Append(copy.Ready ? "1" : "0").Append('\n').Append(copy.Complete ? "1" : "0").Append('\n')
                .Append(Field(copy.Error)).Append('\n').Append(Field(copy.LoaderVersion)).Append('\n').Append(copy.ContentSetHash).Append('\n')
                .Append(copy.PendingAssetPatches.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append(copy.ContentSelection.Length.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(copy.Records.Length.ToString(CultureInfo.InvariantCulture));
            foreach (var content in copy.ContentSelection)
                raw.Append('\n').Append(Field(content.Id)).Append('\t').Append(Field(content.Version));
            foreach (var record in copy.Records)
                raw.Append('\n').Append(record.Kind).Append('\t').Append(Field(record.Id)).Append('\t')
                    .Append(Field(record.Version)).Append('\t').Append(record.Hash);
            raw.Append("\nend");
            byte[] bytes = utf8.GetBytes(raw.ToString());
            if (bytes.Length > MaxBytes) throw new InvalidDataException("Gameplay fingerprint payload is too large.");
            using var stream = new MemoryStream();
            using (var zip = new DeflateStream(stream, CompressionLevel.Optimal, true)) zip.Write(bytes, 0, bytes.Length);
            byte[] compressed = stream.ToArray();
            string encoded = Convert.ToBase64String(compressed);
            int count = (encoded.Length + ChunkSize - 1) / ChunkSize;
            if (count < 1 || count > MaxChunks) throw new InvalidDataException("Gameplay fingerprint transport budget exceeded; verification is unavailable.");
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++) result.Add(Key(i), encoded.Substring(i * ChunkSize, Math.Min(ChunkSize, encoded.Length - i * ChunkSize)));
            // Publisher writes this header last. Digest + repeated header read
            // reject mixed generations even if their game wire is unchanged.
            result.Add(Header, "3:" + count.ToString(CultureInfo.InvariantCulture) + ":" + Digest(compressed));
            return result;
        }
        public static GameplayFingerprintSnapshot? Decode(Func<string, string> read, string expectedWire)
        {
            if (read == null || !GameplayFingerprintPolicy.Text(expectedWire, MaxWire)) return null;
            try
            {
                string header = read(Header) ?? "";
                if (header.Length == 0 || header.Length > 127) return null;
                var frame = header.Split(':');
                if (frame.Length != 3 || frame[0] != "1" && frame[0] != "2" && frame[0] != "3" || !Number(frame[1], MaxChunks, out int count)
                    || count == 0 || !GameplayFingerprintPolicy.FullHash(frame[2])) return null;
                var encoded = new StringBuilder();
                for (int i = 0; i < count; i++)
                {
                    string part = read(Key(i)) ?? "";
                    if (part.Length == 0 || part.Length > ChunkSize || i < count - 1 && part.Length != ChunkSize) return null;
                    encoded.Append(part);
                }
                if (read(Header) != header) return null;
                byte[] compressed = Convert.FromBase64String(encoded.ToString());
                if (Convert.ToBase64String(compressed) != encoded.ToString() || Digest(compressed) != frame[2].ToLowerInvariant()) return null;
                using var stream = new MemoryStream(compressed);
                using var zip = new DeflateStream(stream, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[512];
                int n;
                while ((n = zip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (output.Length + n > MaxBytes) return null;
                    output.Write(buffer, 0, n);
                }
                string raw = utf8.GetString(output.ToArray());
                var lines = raw.Split('\n');
                int statusFields = frame[0] == "1" ? 0 : 1, pendingPatches = 0;
                int maxPending = frame[0] == "2" ? 32 : GameplayFingerprintPolicy.MaxPendingAssetPatches;
                if (lines.Length < 11 + statusFields || lines[0] != frame[0] || Unfield(lines[1], MaxWire) != expectedWire
                    || lines[2] != GameplayFingerprintPolicy.Scope || lines[3] != "0" && lines[3] != "1"
                    || lines[4] != "0" && lines[4] != "1"
                    || statusFields > 0 && !Number(lines[8], maxPending, out pendingPatches)
                    || !Number(lines[8 + statusFields], GameplayFingerprintPolicy.MaxRecords, out int selected)
                    || !Number(lines[9 + statusFields], GameplayFingerprintPolicy.MaxRecords, out int records) || records + selected > GameplayFingerprintPolicy.MaxRecords
                    || lines.Length != records + selected + 11 + statusFields || lines[lines.Length - 1] != "end") return null;
                var result = new GameplayFingerprintSnapshot {
                    Ready = lines[3] == "1", Complete = lines[4] == "1", Error = Unfield(lines[5], GameplayFingerprintPolicy.MaxError),
                    Scope = lines[2], Records = new GameplayFileRecord[records], LoaderVersion = Unfield(lines[6], GameplayFingerprintPolicy.MaxVersion),
                    ContentSetHash = lines[7], ContentSelection = new GameplayContentVersion[selected], PendingAssetPatches = pendingPatches
                };
                // v2's count was a narrowly audited headless exception. Keep
                // its original status contract when reading legacy evidence,
                // but expose it as loading health rather than an exemption.
                if (frame[0] == "2" && pendingPatches > 0
                    && (!result.Ready || !result.Complete || result.LoaderVersion != "2.0.1")) return null;
                if ((!result.Ready || !result.Complete) && (records != 0 || selected != 0 || result.ContentSetHash.Length != 0)) return null;
                for (int i = 0; i < selected; i++)
                {
                    var fields = lines[i + 10 + statusFields].Split('\t');
                    if (fields.Length != 2) return null;
                    result.ContentSelection[i] = new GameplayContentVersion { Id = Unfield(fields[0], GameplayFingerprintPolicy.MaxId), Version = Unfield(fields[1], GameplayFingerprintPolicy.MaxVersion) };
                }
                for (int i = 0; i < records; i++)
                {
                    var fields = lines[i + selected + 10 + statusFields].Split('\t');
                    if (fields.Length != 4) return null;
                    result.Records[i] = new GameplayFileRecord {
                        Kind = fields[0], Id = Unfield(fields[1], GameplayFingerprintPolicy.MaxId),
                        Version = Unfield(fields[2], GameplayFingerprintPolicy.MaxVersion), Hash = fields[3]
                    };
                }
                return GameplayFingerprintPolicy.Normalize(result, out var copy, out _) ? copy : null;
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is FormatException || ex is ArgumentException || ex is InvalidOperationException) { return null; }
        }
        private static bool Number(string text, int max, out int number) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number >= 0 && number <= max && text == number.ToString(CultureInfo.InvariantCulture);
        private static string Field(string text) => Convert.ToBase64String(utf8.GetBytes(text));
        private static string Unfield(string text, int max)
        {
            if (text.Length > (max * 4 + 2) / 3 * 4) throw new InvalidDataException("Fingerprint field is too large.");
            byte[] bytes = Convert.FromBase64String(text);
            if (Convert.ToBase64String(bytes) != text) throw new InvalidDataException("Invalid fingerprint field encoding.");
            string value = utf8.GetString(bytes);
            if (value.Length > max) throw new InvalidDataException("Fingerprint field is too large.");
            return value;
        }
        private static string Digest(byte[] bytes)
        {
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }
    }
}
