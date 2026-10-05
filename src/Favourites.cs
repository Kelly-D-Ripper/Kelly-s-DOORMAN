using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KellysJOINCHECK
{
    internal enum FavouriteChange { Added, Removed, Unavailable, LimitReached }
    internal sealed class Favourites
    {
        public const int MaxCount = 256, MaxSavedLength = MaxCount*40;
        private readonly HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        public int Count => keys.Count;
        public Favourites(string saved)
        {
            if (saved == null || saved.Length > MaxSavedLength) return;
            foreach (var key in saved.Split(','))
                if (keys.Count < MaxCount && ValidKey(key)) keys.Add(key);
        }
        public static string Dedicated(uint ip, ushort connectionPort) => ip==0 || connectionPort==0 ? "" : "d:"+ip.ToString("X8",CultureInfo.InvariantCulture)+":"+connectionPort.ToString(CultureInfo.InvariantCulture);
        public static string Hosted(ulong owner) => owner==0 ? "" : "h:"+owner.ToString(CultureInfo.InvariantCulture);
        private static bool ValidKey(string key)
        {
            if (key.StartsWith("h:",StringComparison.Ordinal))
                return ulong.TryParse(key.Substring(2),NumberStyles.None,CultureInfo.InvariantCulture,out var owner) && key==Hosted(owner);
            var pieces = key.Split(':');
            return pieces.Length==3 && pieces[0]=="d" &&
                uint.TryParse(pieces[1],NumberStyles.HexNumber,CultureInfo.InvariantCulture,out var ip) &&
                ushort.TryParse(pieces[2],NumberStyles.None,CultureInfo.InvariantCulture,out var port) && key==Dedicated(ip,port);
        }
        public bool Contains(string key) => key.Length>0 && keys.Contains(key);
        public FavouriteChange Toggle(string key)
        {
            if (!ValidKey(key)) return FavouriteChange.Unavailable;
            if (keys.Remove(key)) return FavouriteChange.Removed;
            if (keys.Count>=MaxCount) return FavouriteChange.LimitReached;
            keys.Add(key); return FavouriteChange.Added;
        }
        public string Save() => string.Join(",",keys.OrderBy(x=>x,StringComparer.Ordinal));
    }
}
