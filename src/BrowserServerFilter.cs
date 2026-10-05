using System;

namespace KellysJOINCHECK
{
    internal static class BrowserServerFilter
    {
        internal static bool Matches(string tags,bool allVersions,string version,string? mission)
            => (allVersions || HasTag(tags,"v="+version)) && (mission==null || HasTag(tags,"t="+mission));
        private static bool HasTag(string tags,string wanted)
        {
            int start=0;
            while(start<tags.Length)
            {
                int end=tags.IndexOf(',',start); if(end<0) end=tags.Length;
                if(end-start==wanted.Length && string.CompareOrdinal(tags,start,wanted,0,wanted.Length)==0) return true;
                start=end+1;
            }
            return false;
        }
    }
}
