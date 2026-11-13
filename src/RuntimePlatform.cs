using System;
using System.Runtime.InteropServices;

namespace KellysJOINCHECK
{
    // Proton presents Windows APIs to the game. PlatformID alone therefore
    // cannot tell whether the Windows Explorer restart broker is available.
    internal static class RuntimePlatform
    {
        private static readonly Lazy<bool> wine = new Lazy<bool>(DetectWine);
        internal static bool IsWine => wine.Value;
        internal static bool CanUseWindowsRestart => CanUseWindowsRestartFor(Environment.OSVersion.Platform, IsWine);
        internal static bool CanUseWindowsRestartFor(PlatformID platform,bool isWine) => platform==PlatformID.Win32NT&&!isWine;

        [DllImport("kernel32.dll",EntryPoint="GetModuleHandleW",CharSet=CharSet.Unicode,ExactSpelling=true)]
        private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll",EntryPoint="GetProcAddress",CharSet=CharSet.Ansi,ExactSpelling=true)]
        private static extern IntPtr GetProcAddress(IntPtr module,string name);

        private static bool DetectWine()
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)return false;
            try
            {
                IntPtr module=GetModuleHandle("ntdll.dll");
                return module!=IntPtr.Zero&&GetProcAddress(module,"wine_get_version")!=IntPtr.Zero;
            }
            catch(DllNotFoundException){return false;}
            catch(EntryPointNotFoundException){return false;}
            catch(BadImageFormatException){return false;}
        }
    }
}
