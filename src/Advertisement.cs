using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KellysJOINCHECK
{
    internal sealed class Advertisement
    {
        public readonly KeyValuePair<string,string>[] Chunks;
        public readonly string Header;
        public readonly string HeaderKey;
        public readonly int Bytes;
        public Advertisement(Dictionary<string,string> values):this(values,Protocol.Header) { }
        public Advertisement(Dictionary<string,string> values,string headerKey)
        {
            HeaderKey=headerKey;
            Header = values[headerKey];
            Chunks = new KeyValuePair<string,string>[values.Count-1];
            int index = 0, bytes = 0;
            foreach (var pair in values)
            {
                bytes += Encoding.UTF8.GetByteCount(pair.Key)+Encoding.UTF8.GetByteCount(pair.Value);
                if (pair.Key != headerKey) Chunks[index++] = pair;
            }
            Bytes = bytes;
        }
        public int ReservedBytes(int previousSlots) => Bytes + Math.Max(0,previousSlots-Chunks.Length)*5;
    }
    internal sealed class GameplayAdvertisementCache
    {
        private readonly Func<string,GameplayFingerprintSnapshot> read;
        private string? wire;
        private GameplayFingerprintSnapshot? observed;
        private Advertisement? value,compact,budgetUnavailable;
        internal GameplayAdvertisementCache(Func<string,GameplayFingerprintSnapshot> read){this.read=read;}
        internal Advertisement? Get(string currentWire)
        {
            var snapshot=read(currentWire);
            if(currentWire==wire&&ReferenceEquals(snapshot,observed))return value;
            wire=currentWire;observed=snapshot;value=null;compact=null;budgetUnavailable=null;
            try{value=Encode(currentWire,snapshot);}
            catch(InvalidDataException)
            {
                // A single canonical content-set hash keeps every selected
                // file covered when individual hashes exceed Steam's rules.
            }
            if(snapshot.Ready&&snapshot.Complete)
                try{compact=Encode(currentWire,GameplayFingerprintPolicy.CompactContent(snapshot));}
                catch(InvalidDataException){compact=null;}
            if(value==null)value=compact;
            if(value==null)
                try{value=Encode(currentWire,GameplayFingerprintSnapshot.Unavailable("The server gameplay inventory exceeds the metadata transport limit."));}
                catch(InvalidDataException){value=null;}
            return value;
        }
        private static Advertisement Encode(string wire,GameplayFingerprintSnapshot snapshot)
            =>new Advertisement(GameplayFingerprintProtocol.Encode(wire,snapshot),GameplayFingerprintProtocol.Header);
        internal Advertisement? GetCompact(string currentWire)
        {Get(currentWire);return compact;}
        internal Advertisement? BudgetUnavailable(string currentWire)
        {
            Get(currentWire);
            if(budgetUnavailable!=null)return budgetUnavailable;
            try{return budgetUnavailable=new Advertisement(GameplayFingerprintProtocol.Encode(currentWire,GameplayFingerprintSnapshot.Unavailable("Exact gameplay checks are unavailable: the server rule budget is full.")),GameplayFingerprintProtocol.Header);}
            catch(InvalidDataException){return null;}
        }
    }
    internal sealed class GameplayPublicationBudget
    {
        internal const string Marker="gfpv";
        internal const int Limit=1300;
        private static readonly HashSet<string> ownedKeys=CreateOwnedKeys();
        internal readonly Advertisement? Legacy,Fingerprints;
        internal readonly bool HasMarker;
        internal readonly int Bytes;
        private GameplayPublicationBudget(Advertisement? legacy,Advertisement? fingerprints,bool marker,int bytes)
        {Legacy=legacy;Fingerprints=fingerprints;HasMarker=marker;Bytes=bytes;}
        internal static GameplayPublicationBudget Select(Dictionary<string,string> game,Advertisement? legacy,Advertisement? fingerprints,Advertisement? unavailable,int legacySlots,int fingerprintSlots,Advertisement? compact=null)
        {
            int used=0;
            foreach(var pair in game)
                if(pair.Key!=Marker&&!OwnedKey(pair.Key))used+=Encoding.UTF8.GetByteCount(pair.Key)+Encoding.UTF8.GetByteCount(pair.Value??"");
            int markerBytes=Encoding.UTF8.GetByteCount(Marker)+Encoding.UTF8.GetByteCount(GameplayFingerprintPolicy.Scope);
            int emptyLegacy=Encoding.UTF8.GetByteCount(Protocol.Header)+Math.Max(0,legacySlots)*5;
            int emptyFingerprint=Encoding.UTF8.GetByteCount(GameplayFingerprintProtocol.Header)+Math.Max(0,fingerprintSlots)*5;
            bool marker=used+markerBytes+emptyLegacy+emptyFingerprint<=Limit;
            int reserved=used+(marker?markerBytes:Encoding.UTF8.GetByteCount(Marker));
            int Bytes(Advertisement? details,Advertisement? files)=>reserved+(details?.ReservedBytes(legacySlots)??emptyLegacy)+(files?.ReservedBytes(fingerprintSlots)??emptyFingerprint);
            bool Fits(Advertisement? details,Advertisement? files)=>marker&&files!=null&&Bytes(details,files)<=Limit;
            // Atomic complete fingerprint coverage takes priority over legacy
            // details. New clients derive the named versions from that frame.
            if(Fits(legacy,fingerprints))return new GameplayPublicationBudget(legacy,fingerprints,true,Bytes(legacy,fingerprints));
            if(Fits(legacy,compact))return new GameplayPublicationBudget(legacy,compact,true,Bytes(legacy,compact));
            if(Fits(null,compact))return new GameplayPublicationBudget(null,compact,true,Bytes(null,compact));
            // A compact frame exists only for a complete source snapshot; an
            // unavailable status must never displace usable legacy details.
            if(compact!=null&&Fits(null,fingerprints))return new GameplayPublicationBudget(null,fingerprints,true,Bytes(null,fingerprints));
            if(legacy==null||Bytes(legacy,null)>Limit)legacy=null;
            if(!Fits(legacy,unavailable))unavailable=null;
            return new GameplayPublicationBudget(legacy,unavailable,marker,Bytes(legacy,unavailable));
        }
        private static bool OwnedKey(string key)=>ownedKeys.Contains(key);
        private static HashSet<string> CreateOwnedKeys()
        {
            var result=new HashSet<string>(StringComparer.Ordinal){Protocol.Header,GameplayFingerprintProtocol.Header};
            for(int i=0;i<Protocol.MaxChunks;i++)result.Add(Protocol.Key(i));
            for(int i=0;i<GameplayFingerprintProtocol.MaxChunks;i++)result.Add(GameplayFingerprintProtocol.Key(i));
            return result;
        }
    }
    internal sealed class AdvertisementCache
    {
        private readonly Func<string,string> expand;
        private string? wire;
        private bool prepared;
        private Advertisement? value;
        public AdvertisementCache(Func<string,string> expand) { this.expand = expand; }
        public Advertisement? Get(string currentWire)
        {
            if (prepared && currentWire == wire) return value;
            // Remember a failed encoding too, so an invalid signature cannot cause repeated
            // compression/errors on subsequent mission advertisements.
            prepared = true; wire = currentWire; value = null;
            value = new Advertisement(Protocol.Encode(currentWire,expand(currentWire)));
            return value;
        }
    }
    internal enum Publication { Keep, Publish, Clear }
    internal sealed class PublicationState
    {
        private Advertisement? published;
        public int Slots { get; private set; }
        public Publication Decide(Advertisement? value, int usedBytes)
        {
            if (value == null || usedBytes+value.ReservedBytes(Slots)>1300)
                return published == null ? Publication.Keep : Publication.Clear;
            return ReferenceEquals(value,published) ? Publication.Keep : Publication.Publish;
        }
        // The caller already chose a complete, budgeted frame. Hosted lobby
        // metadata has its own Steam limits, not the dedicated A2S rule cap.
        internal Publication DecidePrepared(Advertisement? value)
            =>value==null?(published==null?Publication.Keep:Publication.Clear):(ReferenceEquals(value,published)?Publication.Keep:Publication.Publish);
        public void Commit(Advertisement value) { Slots = Math.Max(Slots,value.Chunks.Length); published = value; }
        public void Clear() { published = null; }
        public void Reset() { published = null; Slots = 0; }
        public static int UsedBytes(Dictionary<string,string> values)
        {
            int used = 0;
            foreach (var pair in values)
                used += Encoding.UTF8.GetByteCount(pair.Key)+Encoding.UTF8.GetByteCount(pair.Value ?? "");
            return used;
        }
    }
}
