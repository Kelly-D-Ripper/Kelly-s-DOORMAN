using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Bootstrap;
using UnityEngine;

namespace KellysJOINCHECK
{
    internal static class Compatibility
    {
        public static string Wire => Application.version;
        public static string Expanded() => Expanded(Wire);
        public static string Expanded(string wire)
        {
            // Read the loader's actual loaded bundle signature, never scan filenames or infer requirements from all plugins.
            if (!Chainloader.PluginInfos.TryGetValue("com.nikkorap.blueprinter", out var info)) return wire;
            var type = info.Instance.GetType();
            string? bundles = type.GetField("BundlesSignature", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string;
            if (bundles == null) return wire;
            int split = wire.IndexOf('_');
            string game = split < 0 ? wire : wire.Substring(0, split);
            string expanded = game + "_com.nikkorap.blueprinter-v" + info.Metadata.Version + "_" + bundles;
            // Only expand the verified Blueprinter 2.x scheme. Other loaders fail to the raw identifier.
            string expected = expanded;
            if (expanded.Length > 100)
            {
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(expanded));
                expected = game + "_" + BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
            }
            return expected == wire ? expanded : wire;
        }
        public static uint? BuildHash()
        {
            var method = HarmonyLib.AccessTools.Method("NuclearOption.Networking.Authentication.NetworkAuthenticatorNuclearOption:GetBuildHash");
            return method == null ? null : (uint?)method.Invoke(null, null);
        }
    }
}
