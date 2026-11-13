using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace KellysJOINCHECK
{
    // This is package validation only. It must not change the installed files.
    internal static class SelfUpdatePackage
    {
        internal const long MaxArchiveBytes = 32L * 1024 * 1024;
        internal const long MaxFileBytes = 16L * 1024 * 1024;
        internal static readonly string[] Required = {
            "BepInEx/plugins/KellysJOINCHECK.dll",
            "BepInEx/patchers/KellysDOORMANStartup.dll",
            "BepInEx/DOORMAN/KellysDOORMANRestart.exe"
        };
        internal const string Server = "BepInEx/plugins/KellysJOINCHECKServer.dll";
        private static readonly string[] Documents = { "README.md", "LICENSE", "docs/RECOVERY.md", "docs/DEVELOPER.md" };

        // Byte equality is deliberately stricter than guessing compatibility from
        // assembly versions: an unknown helper/server build requires manual install.
        internal static bool CanReplaceClientOnly(Dictionary<string, byte[]> files, string bepinexRoot, bool hasServer)
        {
            foreach (var relative in new[] { Required[1], Required[2] }.Concat(hasServer ? new[] { Server } : Array.Empty<string>()))
            {
                if (!files.TryGetValue(relative, out var payload)) return false;
                string installed = ModUpdateFiles.Within(bepinexRoot, relative.Substring("BepInEx/".Length).Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(installed) || new FileInfo(installed).Length != payload.LongLength) return false;
                using (var sha = SHA256.Create())
                {
                    string expected = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "");
                    if (!ModUpdateFiles.Hash(installed).Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            return true;
        }

        internal static Dictionary<string, byte[]> Read(string archive, string digest, bool updateServer, CancellationToken token)
        {
            if (!File.Exists(archive) || new FileInfo(archive).Length > MaxArchiveBytes ||
                digest == null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ||
                !ModUpdateFiles.HashValid(digest.Substring(7)) ||
                !ModUpdateFiles.Hash(archive).Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DOORMAN package checksum or size is invalid.");

            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var input = File.OpenRead(archive))
            using (var zip = new ZipArchive(input, ZipArchiveMode.Read))
            {
                if (zip.Entries.Count > 16) throw new InvalidDataException("Unexpected DOORMAN package contents.");
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string name = entry.FullName;
                    ModUpdateService.SafeArchivePath(name);
                    if (!seen.Add(name)) throw new InvalidDataException("Duplicate package path.");
                    int type = (entry.ExternalAttributes >> 16) & 0xF000;
                    if ((type != 0 && type != 0x8000) || (entry.ExternalAttributes & 0x400) != 0)
                        throw new InvalidDataException("Package links are not supported.");
                    if (!Required.Contains(name, StringComparer.Ordinal) && name != Server && !Documents.Contains(name, StringComparer.Ordinal))
                        throw new InvalidDataException("Unexpected file in DOORMAN package.");
                    if (entry.Length <= 0 || entry.Length > MaxFileBytes) throw new InvalidDataException("Package file size is invalid.");
                    if (Documents.Contains(name, StringComparer.Ordinal) || (name == Server && !updateServer)) continue;
                    using (var body = entry.Open())
                    using (var buffer = new MemoryStream())
                    {
                        var bytes = new byte[65536]; long count = 0; int n;
                        while ((n = body.Read(bytes, 0, bytes.Length)) != 0)
                        {
                            token.ThrowIfCancellationRequested();
                            count += n;
                            if (count > entry.Length || count > MaxFileBytes) throw new InvalidDataException("Package file is too large.");
                            buffer.Write(bytes, 0, n);
                        }
                        if (count != entry.Length) throw new InvalidDataException("Package file is incomplete.");
                        result.Add(name, buffer.ToArray());
                    }
                }
            }
            if (Required.Any(name => !result.ContainsKey(name)) || (updateServer && !result.ContainsKey(Server)))
                throw new InvalidDataException("The matching complete DOORMAN package is required.");
            return result;
        }
    }
}
