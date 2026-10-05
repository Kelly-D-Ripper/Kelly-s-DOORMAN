using System;
using System.Collections.Generic;
using System.Text;

namespace KellysJOINCHECK
{
    internal sealed class Advertisement
    {
        public readonly KeyValuePair<string,string>[] Chunks;
        public readonly string Header;
        public readonly int Bytes;
        public Advertisement(Dictionary<string,string> values)
        {
            Header = values[Protocol.Header];
            Chunks = new KeyValuePair<string,string>[values.Count-1];
            int index = 0, bytes = 0;
            foreach (var pair in values)
            {
                bytes += Encoding.UTF8.GetByteCount(pair.Key)+Encoding.UTF8.GetByteCount(pair.Value);
                if (pair.Key != Protocol.Header) Chunks[index++] = pair;
            }
            Bytes = bytes;
        }
        public int ReservedBytes(int previousSlots) => Bytes + Math.Max(0,previousSlots-Chunks.Length)*5;
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
