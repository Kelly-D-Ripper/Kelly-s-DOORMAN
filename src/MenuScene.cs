using System;

namespace KellysJOINCHECK
{
    internal static class MenuScene
    {
        internal static bool Matches(string name,string path,string loadingKey)
        {
            if(string.IsNullOrEmpty(loadingKey))return false;
            string key=loadingKey.Replace('\\','/');
            // Unity's loading key is an asset path, while Scene.name is only
            // its basename. Prefer the real path when Unity supplies one.
            if(!string.IsNullOrEmpty(path))return string.Equals(path.Replace('\\','/'),key,StringComparison.Ordinal);
            string shortName=key.Substring(key.LastIndexOf('/')+1);
            if(shortName.EndsWith(".unity",StringComparison.Ordinal))shortName=shortName.Substring(0,shortName.Length-6);
            return shortName.Length>0&&string.Equals(name,shortName,StringComparison.Ordinal);
        }
    }
}
