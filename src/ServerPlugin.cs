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
    [BepInPlugin(Id, "Kelly's DOORMAN Server", "1.1.0")]
    [BepInDependency("com.nikkorap.blueprinter", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ServerPlugin : BaseUnityPlugin
    {
        public const string Id="kelly.nuclearoption.joincheck.server";
        private static ServerPlugin? instance;
        private Harmony? harmony;
        private GameplayFingerprintInventory? inventory;
        private GameplayAdvertisementCache? fingerprintCache;
        private string hostedWire="",requestedWire="";
        private readonly HashSet<string> warnings=new HashSet<string>(StringComparer.Ordinal);
        private FieldInfo? keyValuesField,tagsField;
        private readonly AdvertisementCache cache=new AdvertisementCache(Compatibility.Expanded);
        private readonly PublicationState dedicated=new PublicationState(),dedicatedFingerprints=new PublicationState();
        private readonly PublicationState hosted=new PublicationState(),hostedFingerprints=new PublicationState();
        private DedicatedServerKeyValues? dedicatedOwner;
        private HostedLobbyInstance? hostedOwner;
        private ulong hostedId;
        private bool dedicatedMarker,hostedMarker;
        private static readonly string[] chunkKeys=CreateKeys(false),fingerprintKeys=CreateKeys(true);
        private static string[] CreateKeys(bool fingerprints)
        {
            int count=fingerprints?GameplayFingerprintProtocol.MaxChunks:Protocol.MaxChunks;
            var keys=new string[count];
            for(int i=0;i<count;i++)keys[i]=fingerprints?GameplayFingerprintProtocol.Key(i):Protocol.Key(i);
            return keys;
        }
        private void Awake()
        {
            instance=this;
            keyValuesField=AccessTools.Field(typeof(DedicatedServerKeyValues),"keyValues");
            tagsField=AccessTools.Field(typeof(DedicatedServerKeyValues),"tags");
            harmony=new Harmony(Id);
            inventory=new GameplayFingerprintInventory(this,message=>Warn(message),InventoryChanged);
            fingerprintCache=new GameplayAdvertisementCache(inventory.ForWire);
            inventory.Start(harmony);
            Patch(typeof(HostedLobbyInstance),"SetData",nameof(HostedPostfix));
            Patch(typeof(DedicatedServerKeyValues),"ApplyValuesToSteam",nameof(DedicatedPostfix));
            Patch(typeof(DedicatedServerManager),"SteamLogOn",nameof(SessionPrefix),true);
            Patch(typeof(SteamGameServer),"ClearAllKeyValues",nameof(ClearPostfix));
            Logger.LogInfo("DOORMAN Server 1.1.0 candidate loaded. Cached audited gameplay fingerprints; startup/event hashing runs off-thread, with no gameplay update loop or join-rule changes.");
        }
        private void Patch(Type type,string name,string callback,bool prefix=false)
        {
            try
            {
                var method=AccessTools.Method(type,name)??throw new MissingMethodException(type.FullName,name);
                var hook=new HarmonyMethod(AccessTools.Method(typeof(ServerPlugin),callback));
                if(prefix)harmony!.Patch(method,prefix:hook);else harmony!.Patch(method,postfix:hook);
                Logger.LogInfo("Patched "+type.Name+"."+name);
            }
            catch(Exception ex){Logger.LogWarning("Advertisement hook unavailable: "+type.Name+"."+name+" ("+ex.GetType().Name+")");}
        }
        private void Warn(string warning)
        {if(warnings.Add(warning))Logger.LogWarning(warning);}
        private void ObserveWire(string wire)
        {
            if(requestedWire==wire)return;
            requestedWire=wire;warnings.Clear();
            if(inventory!=null&&inventory.CurrentWire!=wire)inventory.RequestRefresh();
        }
        private Advertisement? Legacy(string wire)
        {
            try{return cache.Get(wire);}
            catch(Exception ex){Warn("Readable compatibility metadata unavailable ("+ex.GetType().Name+").");return null;}
        }
        private void InventoryChanged()
        {
            try
            {
                if(hostedOwner.HasValue&&hostedOwner.Value.Id.m_SteamID==hostedId)AdvertiseHosted(hostedOwner.Value,hostedWire);
                if(dedicatedOwner!=null)AdvertiseDedicated(dedicatedOwner);
            }
            catch(Exception ex){Warn("Gameplay fingerprint publication unavailable ("+ex.GetType().Name+").");}
        }
        private static void HostedPostfix(HostedLobbyInstance __instance,string __0,string __1)
        {
            if(__0!=LobbyInstance.HOST_VERSION_KEY||instance==null)return;
            try{instance.AdvertiseHosted(__instance,__1);}
            catch(Exception ex){instance.Warn("Compatibility advertisement unavailable: "+ex.GetType().Name);}
        }
        private void AdvertiseHosted(HostedLobbyInstance host,string wire)
        {
            if(hostedId!=host.Id.m_SteamID)
            {hostedId=host.Id.m_SteamID;hosted.Reset();hostedFingerprints.Reset();hostedMarker=false;}
            hostedOwner=host;hostedWire=wire;ObserveWire(wire);
            // This tiny marker survives pending/withdrawn fingerprint headers.
            if(!hostedMarker)
            {if(!SteamMatchmaking.SetLobbyData(host.Id,GameplayPublicationBudget.Marker,GameplayFingerprintPolicy.Scope))return;hostedMarker=true;}
            PublishHosted(host,hosted,Legacy(wire),Protocol.Header,chunkKeys);
            PublishHosted(host,hostedFingerprints,fingerprintCache?.Get(wire),GameplayFingerprintProtocol.Header,fingerprintKeys);
        }
        private static void PublishHosted(HostedLobbyInstance host,PublicationState state,Advertisement? value,string header,string[] keys)
        {
            var operation=state.DecidePrepared(value);
            if(operation==Publication.Keep)return;
            if(!SteamMatchmaking.SetLobbyData(host.Id,header,""))return;
            int start=operation==Publication.Clear?0:value!.Chunks.Length;
            for(int i=start;i<state.Slots;i++)if(!SteamMatchmaking.SetLobbyData(host.Id,keys[i],""))return;
            state.Clear();
            if(operation==Publication.Clear)return;
            foreach(var pair in value!.Chunks)if(!SteamMatchmaking.SetLobbyData(host.Id,pair.Key,pair.Value))return;
            if(SteamMatchmaking.SetLobbyData(host.Id,header,value.Header))state.Commit(value);
        }
        private static void DedicatedPostfix(DedicatedServerKeyValues __instance)
        {
            if(instance==null)return;
            try{instance.AdvertiseDedicated(__instance);}
            catch(Exception ex){instance.Warn("Compatibility advertisement unavailable: "+ex.GetType().Name);}
        }
        private void AdvertiseDedicated(DedicatedServerKeyValues owner)
        {
            if(!ReferenceEquals(owner,dedicatedOwner))
            {dedicatedOwner=owner;dedicated.Clear();dedicatedFingerprints.Clear();dedicatedMarker=false;}
            var existing=keyValuesField?.GetValue(owner) as Dictionary<string,string>;
            var tags=tagsField?.GetValue(owner);
            string? wire=null;
            // 0.34.5 switched the dedicated tag dictionary from string keys to char keys;
            // both versions use the same documented host-version tag 'v'.
            if(tags is Dictionary<string,string> oldTags) oldTags.TryGetValue("v",out wire);
            else if(tags is Dictionary<char,string> newTags) newTags.TryGetValue('v',out wire);
            if(existing==null||string.IsNullOrEmpty(wire))return;
            ObserveWire(wire!);
            var value=Legacy(wire!);var fingerprints=fingerprintCache?.Get(wire!);var compact=fingerprintCache?.GetCompact(wire!);
            var budget=GameplayPublicationBudget.Select(existing,value,fingerprints,fingerprintCache?.BudgetUnavailable(wire!),dedicated.Slots,dedicatedFingerprints.Slots,compact);
            if(value!=null&&budget.Legacy==null)Warn("Legacy compatibility details exceed the spare server-rule budget; updated clients use the complete gameplay inventory when available.");
            if(fingerprints!=null&&!ReferenceEquals(budget.Fingerprints,fingerprints)&&!ReferenceEquals(budget.Fingerprints,compact))Warn("Exact gameplay fingerprints exceed the spare server-rule budget; complete verification unavailable.");
            if(budget.HasMarker!=dedicatedMarker)
            {SteamGameServer.SetKeyValue(GameplayPublicationBudget.Marker,budget.HasMarker?GameplayFingerprintPolicy.Scope:"");dedicatedMarker=budget.HasMarker;}
            PublishDedicated(dedicated,budget.Legacy,Protocol.Header,chunkKeys);
            PublishDedicated(dedicatedFingerprints,budget.Fingerprints,GameplayFingerprintProtocol.Header,fingerprintKeys);
        }
        private static void PublishDedicated(PublicationState state,Advertisement? value,string header,string[] keys)
        {
            var operation=state.DecidePrepared(value);
            if(operation==Publication.Keep)return;
            SteamGameServer.SetKeyValue(header,"");
            int start=operation==Publication.Clear?0:value!.Chunks.Length;
            for(int i=start;i<state.Slots;i++)SteamGameServer.SetKeyValue(keys[i],"");
            state.Clear();
            if(operation==Publication.Clear)return;
            foreach(var pair in value!.Chunks)SteamGameServer.SetKeyValue(pair.Key,pair.Value);
            SteamGameServer.SetKeyValue(header,value.Header);state.Commit(value);
        }
        // Only event invalidation; no gameplay or filesystem polling loop.
        private static void SessionPrefix()
        {if(instance==null)return;instance.dedicated.Clear();instance.dedicatedFingerprints.Clear();instance.dedicatedMarker=false;}
        private static void ClearPostfix()
        {if(instance==null)return;instance.dedicated.Reset();instance.dedicatedFingerprints.Reset();instance.dedicatedMarker=false;}
        private void OnDestroy()
        {inventory?.Dispose();harmony?.UnpatchSelf();if(instance==this)instance=null;}
    }
}
