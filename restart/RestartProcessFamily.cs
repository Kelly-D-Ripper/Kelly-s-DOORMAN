using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace KellysJOINCHECK
{
    internal static class RestartProcessFamily
    {
        internal readonly struct Snapshot
        {
            internal readonly int CurrentPid,ParentPid,DesktopPid;
            internal Snapshot(int currentPid,int parentPid,int desktopPid)
            {CurrentPid=currentPid;ParentPid=parentPid;DesktopPid=desktopPid;}
        }

        internal static bool IsDetached(int parentPid,int desktopPid,int oldGamePid)
            =>parentPid>0&&desktopPid>0&&oldGamePid>0&&parentPid==desktopPid&&parentPid!=oldGamePid;

        internal static void VerifyDesktopParent(int oldGamePid)
        {
            var family=CaptureCurrent();
            if(!IsDetached(family.ParentPid,family.DesktopPid,oldGamePid))
                throw new IOException("The restart helper is still attached to the game instead of the Windows desktop. Nuclear Option will stay open. Close the old restart box and install the full DOORMAN package before trying again.");
        }

        internal static Snapshot CaptureCurrent()
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)
                throw new PlatformNotSupportedException("DOORMAN restart requires the Windows desktop.");
            IntPtr desktop=GetShellWindow();
            if(desktop==IntPtr.Zero||GetWindowThreadProcessId(desktop,out uint desktopPid)==0||desktopPid==0||desktopPid>int.MaxValue)
                throw new IOException("The Windows desktop is unavailable. Nuclear Option will stay open; restart it normally to apply your saved setup.");
            int currentPid;
            using(var current=Process.GetCurrentProcess())currentPid=current.Id;
            IntPtr snapshot=CreateToolhelp32Snapshot(2,0);
            if(snapshot==IntPtr.Zero||snapshot==new IntPtr(-1))
                throw NativeFailure("Could not verify the restart helper's parent process.");
            try
            {
                var entry=new ProcessEntry { Size=(uint)Marshal.SizeOf(typeof(ProcessEntry)) };
                if(!Process32First(snapshot,ref entry))
                    throw NativeFailure("Could not read the restart helper's process family.");
                do
                {
                    if(entry.ProcessId!=(uint)currentPid)continue;
                    if(entry.ParentProcessId>int.MaxValue)
                        throw new IOException("The restart helper's parent could not be verified. Nuclear Option will stay open.");
                    return new Snapshot(currentPid,(int)entry.ParentProcessId,(int)desktopPid);
                }
                while(Process32Next(snapshot,ref entry));
                throw new IOException("The restart helper's parent could not be found. Nuclear Option will stay open.");
            }
            finally {CloseHandle(snapshot);}
        }

        internal static void CleanSteamContext()
        {
            // Clear only this helper's inherited launch identity. The game's
            // environment and the user's saved Windows variables stay intact.
            foreach(string name in new[]{"SteamAppId","SteamGameId","SteamOverlayGameId"})
                Environment.SetEnvironmentVariable(name,null,EnvironmentVariableTarget.Process);
        }

        private static IOException NativeFailure(string message)
            =>new IOException(message+" Nuclear Option will stay open.",new Win32Exception(Marshal.GetLastWin32Error()));

        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
        private struct ProcessEntry
        {
            internal uint Size,Usage,ProcessId;
            internal UIntPtr DefaultHeap;
            internal uint ModuleId,Threads,ParentProcessId;
            internal int BasePriority;
            internal uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] internal string Executable;
        }
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint processId);
        [DllImport("kernel32.dll",EntryPoint="Process32FirstW",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32.dll",EntryPoint="Process32NextW",CharSet=CharSet.Unicode,SetLastError=true)]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
        [DllImport("kernel32.dll")]
        [return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll",SetLastError=true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    }
}
