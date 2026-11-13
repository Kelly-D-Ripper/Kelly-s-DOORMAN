using System;

namespace KellysJOINCHECK
{
    internal static class BrowserServerFilter
    {
        internal static bool Matches(string tags,bool allVersions,string version,string? mission)
            => (allVersions || HasTag(tags,'v',version)) && (mission==null || HasTag(tags,'t',mission));
        private static bool HasTag(string tags,char key,string value)
        {
            if(tags==null) return false;
            int start=0;
            while(start<tags.Length)
            {
                int end=tags.IndexOf(',',start); if(end<0) end=tags.Length;
                if(tags[start]==key)
                {
                    int valueStart=start+1;
                    if(valueStart<end && tags[valueStart]=='=') valueStart++;
                    if(end-valueStart==value.Length && string.CompareOrdinal(tags,valueStart,value,0,value.Length)==0) return true;
                }
                start=end+1;
            }
            return false;
        }
    }
}
