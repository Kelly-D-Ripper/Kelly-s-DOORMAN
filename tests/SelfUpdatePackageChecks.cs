using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using KellysJOINCHECK;

internal static class SelfUpdatePackageRegression
{
    internal static void Run(Action<bool, string> check)
    {
        string dir = Path.Combine(Path.GetTempPath(), "doorman-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string full = Path.Combine(dir, "full.zip");
            using (var file = File.Create(full)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                foreach (string name in SelfUpdatePackage.Required) Put(zip, name);
                Put(zip, SelfUpdatePackage.Server); Put(zip, "README.md");
            }
            string hash = "sha256:" + ModUpdateFiles.Hash(full);
            var client = SelfUpdatePackage.Read(full, hash, false, CancellationToken.None);
            check(client.Count == 3 && !client.ContainsKey(SelfUpdatePackage.Server), "self update leaves the server component alone for client-only users");
            var host = SelfUpdatePackage.Read(full, hash, true, CancellationToken.None);
            check(host.Count == 4 && host.ContainsKey(SelfUpdatePackage.Server), "self update preserves full installations as full installations");
            string bep = Path.Combine(dir, "BepInEx");
            foreach (var pair in host)
            {
                string path = Path.Combine(bep, pair.Key.Substring("BepInEx/".Length).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, pair.Value);
            }
            check(SelfUpdatePackage.CanReplaceClientOnly(host, bep, true), "identical helpers and server allow a client-only update");
            string helper = Path.Combine(bep, "DOORMAN", "KellysDOORMANRestart.exe"); File.WriteAllText(helper, "changed helper");
            check(!SelfUpdatePackage.CanReplaceClientOnly(host, bep, true), "different restart helper requires the full manual package");
            File.WriteAllBytes(helper, host[SelfUpdatePackage.Required[2]]);
            File.WriteAllText(Path.Combine(bep, "plugins", "KellysJOINCHECKServer.dll"), "changed server");
            check(!SelfUpdatePackage.CanReplaceClientOnly(host, bep, true) && SelfUpdatePackage.CanReplaceClientOnly(client, bep, false), "a changed host component blocks FULL updates but does not affect CLIENT installs");
            Reject(check, () => SelfUpdatePackage.Read(full, "sha256:" + new string('0', 64), false, CancellationToken.None), "self update rejects checksum mismatch");
            string bad = Path.Combine(dir, "bad.zip");
            using (var file = File.Create(bad)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            { foreach (string name in SelfUpdatePackage.Required) Put(zip, name); Put(zip, "BepInEx/config/player.cfg"); }
            Reject(check, () => SelfUpdatePackage.Read(bad, "sha256:" + ModUpdateFiles.Hash(bad), false, CancellationToken.None), "self update rejects unexpected config files");
            string incomplete = Path.Combine(dir, "incomplete.zip");
            using (var file = File.Create(incomplete)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) Put(zip, SelfUpdatePackage.Required[0]);
            Reject(check, () => SelfUpdatePackage.Read(incomplete, "sha256:" + ModUpdateFiles.Hash(incomplete), false, CancellationToken.None), "self update rejects a DLL-only package");
            string? real = Environment.GetEnvironmentVariable("DOORMAN_SELF_UPDATE_FIXTURE");
            if (!string.IsNullOrWhiteSpace(real))
            {
                var checkedPackage = SelfUpdatePackage.Read(real, "sha256:" + ModUpdateFiles.Hash(real), true, CancellationToken.None);
                check(checkedPackage.Count == 4, "real FULL release archive contains the four expected DOORMAN executables");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
    private static void Put(ZipArchive zip, string name) { using (var stream = zip.CreateEntry(name).Open()) { var data = Encoding.ASCII.GetBytes("test"); stream.Write(data, 0, data.Length); } }
    private static void Reject(Action<bool,string> check, Action action, string message)
    { try { action(); check(false, message); } catch (InvalidDataException) { check(true, message); } }
}
