using System;
using System.Diagnostics;
using System.IO;

namespace KellysJOINCHECK
{
    internal static class WindowsRestartLauncher
    {
        // The short bootstrap runs on the Windows CLR, where desktop COM is
        // supported. It must exit before Unity quits. The long-lived helper is
        // created by Explorer and proves that parent before writing readiness.
        internal static void Start(string helper,string arguments,string directory)
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT||!Path.IsPathRooted(helper)||!Path.GetFileName(helper).Equals("KellysDOORMANRestart.exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(helper))throw new IOException("The full Windows restart helper is unavailable.");
            helper=Path.GetFullPath(helper);directory=Path.GetFullPath(directory);
            if(!Path.GetDirectoryName(helper)!.Equals(directory,StringComparison.OrdinalIgnoreCase)||string.IsNullOrWhiteSpace(arguments)||arguments.IndexOfAny(new[]{'\0','\r','\n'})>=0)throw new IOException("The restart helper request is invalid.");
            var start=new ProcessStartInfo(helper) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=directory,Arguments="--broker "+arguments };
            foreach(string name in new[]{"SteamAppId","SteamGameId","SteamOverlayGameId"})start.EnvironmentVariables.Remove(name);
            using(var bootstrap=Process.Start(start))
            {
                if(bootstrap==null)throw new IOException("Windows could not start restart preparation.");
                if(!bootstrap.WaitForExit(8000))throw new IOException("Windows restart preparation timed out. The game was kept open.");
                if(bootstrap.ExitCode!=0)throw new IOException("Windows Explorer could not prepare the separate restart helper. The game was kept open; check DOORMAN-restart.log.");
            }
        }
    }
}
