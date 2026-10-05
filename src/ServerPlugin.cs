using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using NuclearOption.DedicatedServer;
using NuclearOption.Networking.Lobbies;
using Steamworks;

namespace KellysJOINCHECK
{
    [BepInPlugin(Id, "Kelly's DOORMAN Server", "1.0.0")]
    public sealed class ServerPlugin : BaseUnityPlugin
    {
        public const string Id = "kelly.nuclearoption.joincheck.server";
        private static ServerPlugin? instance;
        private Harmony? harmony;
        private string lastWarning = "";
        private FieldInfo? keyValuesField, tagsField;
        private readonly AdvertisementCache cache = new AdvertisementCache(Compatibility.Expanded);
        private readonly PublicationState dedicated = new PublicationState();
        private DedicatedServerKeyValues? dedicatedOwner;
        private ulong hostedId;
        private Advertisement? hostedPublished;
        private static readonly string[] chunkKeys = CreateKeys();
        private static string[] CreateKeys()
        {
            var keys = new string[Protocol.MaxChunks];
            for (int i=0;i<keys.Length;i++) keys[i] = Protocol.Key(i);
            return keys;
        }
        private void Awake()
        {
            instance = this;
            keyValuesField = AccessTools.Field(typeof(DedicatedServerKeyValues),"keyValues");
            tagsField = AccessTools.Field(typeof(DedicatedServerKeyValues),"tags");
            harmony = new Harmony(Id);
            Patch(typeof(HostedLobbyInstance),"SetData",nameof(HostedPostfix));
            Patch(typeof(DedicatedServerKeyValues),"ApplyValuesToSteam",nameof(DedicatedPostfix));
            Patch(typeof(DedicatedServerManager),"SteamLogOn",nameof(SessionPrefix),true);
            Patch(typeof(SteamGameServer),"ClearAllKeyValues",nameof(ClearPostfix));
            Logger.LogInfo("DOORMAN Server 1.0.0 loaded. Cached metadata only; no gameplay update loop or join-rule changes.");
        }
        private void Patch(Type type,string name,string callback,bool prefix=false)
        {
            try
            {
                var method = AccessTools.Method(type,name) ?? throw new MissingMethodException(type.FullName,name);
                var hook = new HarmonyMethod(AccessTools.Method(typeof(ServerPlugin),callback));
                if (prefix) harmony!.Patch(method,prefix:hook); else harmony!.Patch(method,postfix:hook);
                Logger.LogInfo("Patched "+type.Name+"."+name);
            }
            catch (Exception ex) { Logger.LogWarning("Advertisement hook unavailable: "+type.Name+"."+name+" ("+ex.GetType().Name+")"); }
        }
        private void Warn(string warning)
        {
            if (lastWarning == warning) return;
            lastWarning = warning; Logger.LogWarning(warning);
        }
        private static void HostedPostfix(HostedLobbyInstance __instance,string __0,string __1)
        {
            // Check the key before any delegate, reflection, compression or allocation.
            if (__0 != LobbyInstance.HOST_VERSION_KEY || instance == null) return;
            try { instance.AdvertiseHosted(__instance,__1); }
            catch (Exception ex) { instance.Warn("Compatibility advertisement unavailable: "+ex.GetType().Name); }
        }
        private void AdvertiseHosted(HostedLobbyInstance host,string wire)
        {
            if (hostedId != host.Id.m_SteamID) { hostedId = host.Id.m_SteamID; hostedPublished = null; }
            var value = cache.Get(wire);
            if (ReferenceEquals(value,hostedPublished)) return;
            if (!SteamMatchmaking.SetLobbyData(host.Id,Protocol.Header,"")) return;
            hostedPublished = null;
            if (value == null) return;
            foreach (var pair in value.Chunks)
                if (!SteamMatchmaking.SetLobbyData(host.Id,pair.Key,pair.Value)) return;
            if (SteamMatchmaking.SetLobbyData(host.Id,Protocol.Header,value.Header)) hostedPublished = value;
        }
        private static void DedicatedPostfix(DedicatedServerKeyValues __instance)
        {
            if (instance == null) return;
            try { instance.AdvertiseDedicated(__instance); }
            catch (Exception ex) { instance.Warn("Compatibility advertisement unavailable: "+ex.GetType().Name); }
        }
        private void AdvertiseDedicated(DedicatedServerKeyValues owner)
        {
            if (!ReferenceEquals(owner,dedicatedOwner)) { dedicatedOwner = owner; dedicated.Clear(); }
            var existing = keyValuesField?.GetValue(owner) as Dictionary<string,string>;
            var tags = tagsField?.GetValue(owner) as Dictionary<string,string>;
            // Use the version already published by the game. Do not ask Blueprinter to hash
            // Application.version again on every ApplyValuesToSteam call.
            if (existing == null || tags == null || !tags.TryGetValue(DedicatedServerKeyValues.SHORT_HOST_VERSION_KEY,out var wire)) return;
            var value = cache.Get(wire);
            int used = PublicationState.UsedBytes(existing);
            var operation = dedicated.Decide(value,used);
            if (value != null && used+value.ReservedBytes(dedicated.Slots)>1300)
                Warn("JOINCHECK details exceed the spare server-rule budget; readable details unavailable.");
            if (operation == Publication.Keep) return;
            SteamGameServer.SetKeyValue(Protocol.Header,"");
            // Clear only stale/withdrawn chunks, not every chunk on every advertisement.
            int start = operation == Publication.Clear ? 0 : value!.Chunks.Length;
            for (int i=start;i<dedicated.Slots;i++) SteamGameServer.SetKeyValue(chunkKeys[i],"");
            dedicated.Clear();
            if (operation == Publication.Clear) return;
            foreach (var pair in value!.Chunks) SteamGameServer.SetKeyValue(pair.Key,pair.Value);
            SteamGameServer.SetKeyValue(Protocol.Header,value.Header);
            dedicated.Commit(value);
        }
        // Event hooks invalidate publication after a new Steam session or explicit rule clear.
        // They perform no compression, writes, polling or gameplay work themselves.
        private static void SessionPrefix() { instance?.dedicated.Clear(); }
        private static void ClearPostfix() { instance?.dedicated.Reset(); }
        private void OnDestroy() { harmony?.UnpatchSelf(); if (instance == this) instance = null; }
    }
}
