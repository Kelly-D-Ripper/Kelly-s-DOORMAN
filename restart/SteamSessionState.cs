using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace KellysJOINCHECK
{
    // Optional Steam observations used only by the external restart helper.
    // An unavailable observation is unknown, never evidence that Steam is idle.
    internal static class SteamSessionState
    {
        internal const int MaxTailBytes=64*1024;
        private static string? steamLogPath;

        internal static bool? Read(int oldPid,string gamePath,DateTime startedLocal)
        {
            bool? registry=ReadRegistry();
            if(registry==true)return true;
            bool? session=null;
            try
            {
                if(steamLogPath==null)steamLogPath=FindSteamLog();
                if(steamLogPath!=null)session=Parse(ReadTail(steamLogPath),oldPid,gamePath,startedLocal.AddSeconds(-5));
            }
            catch { /* Steam may rotate its log or deny optional access. */ }
            return Combine(registry,session);
        }

        internal static bool? Combine(bool? registry,bool? session)
        {
            if(registry==true||session==true)return true;
            if(registry==false||session==false)return false;
            return null;
        }

        internal static bool? Parse(string tail,int oldPid,string gamePath,DateTime? notBefore=null)
        {
            if(oldPid<=0||string.IsNullOrWhiteSpace(gamePath)||string.IsNullOrEmpty(tail)||tail.Length>MaxTailBytes)return null;
            string added="AppID 2168680 adding PID "+oldPid.ToString(CultureInfo.InvariantCulture)+" as a tracked process ";
            string quotedPath="\""+gamePath+"\"";
            bool bound=false,busy=false;
            using(var lines=new StringReader(tail))
            {
                string? line;
                while((line=lines.ReadLine())!=null)
                {
                    if(line.Length<22||line[0]!='['||line[20]!=']'||line[21]!=' '||!DateTime.TryParseExact(line.Substring(1,19),"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out var stamp)||(notBefore.HasValue&&stamp<notBefore.Value))continue;
                    string entry=line.Substring(22);
                    // Bind this PID to this exact executable before considering
                    // any removal. A PID alone may belong to an older launch.
                    if(entry.StartsWith(added,StringComparison.Ordinal)&&entry.IndexOf(quotedPath,added.Length,StringComparison.OrdinalIgnoreCase)>=0)
                    { bound=true;busy=true;continue; }
                    if(!bound)continue;
                    if(entry.Equals("Remove 2168680 from running list",StringComparison.Ordinal))
                    { busy=false;continue; }
                    // Steam may retain a crash handler after its parent exits.
                    // Only removing the complete app session is an idle signal.
                    if(entry.StartsWith("AppID 2168680 adding PID ",StringComparison.Ordinal))busy=true;
                }
            }
            return bound?(bool?)busy:null;
        }

        private static bool? ReadRegistry()
        {
#if NET8_0_OR_GREATER
            if(!OperatingSystem.IsWindows())return null;
#endif
            bool idle=false;
            foreach(var view in new[]{RegistryView.Registry32,RegistryView.Registry64})
            {
                try
                {
                    using(var user=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,view))
                    using(var app=user.OpenSubKey(@"Software\Valve\Steam\Apps\2168680"))
                    {
                        if(app==null)continue;
                        bool? running=Flag(app.GetValue("Running")),updating=Flag(app.GetValue("Updating"));
                        if(running==true||updating==true)return true;
                        if(running==false)idle=true;
                    }
                }
                catch { /* Another view may still provide a usable signal. */ }
            }
            return idle?(bool?)false:null;
        }

        private static bool? Flag(object? value)
        {
            if(value is int number)return number==0?(bool?)false:number==1?(bool?)true:null;
            if(value is long wide)return wide==0?(bool?)false:wide==1?(bool?)true:null;
            return null;
        }

        private static string? FindSteamLog()
        {
            var candidates=Process.GetProcessesByName("steam");
            try
            {
                foreach(var steam in candidates)
                {
                    try
                    {
                        string? executable=steam.MainModule?.FileName;
                        if(string.IsNullOrEmpty(executable)||!Path.GetFileName(executable).Equals("steam.exe",StringComparison.OrdinalIgnoreCase))continue;
                        string path=Path.Combine(Path.GetDirectoryName(executable)!,"logs","gameprocess_log.txt");
                        if(File.Exists(path))return path;
                    }
                    catch { /* Other-user or transient Steam processes are optional. */ }
                }
                return null;
            }
            finally {foreach(var steam in candidates)steam.Dispose();}
        }

        private static string ReadTail(string path)
        {
            using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            {
                long length=stream.Length,offset=Math.Max(0,length-MaxTailBytes);
                stream.Position=offset;
                var bytes=new byte[(int)(length-offset)];
                int used=0;
                while(used<bytes.Length)
                {
                    int read=stream.Read(bytes,used,bytes.Length-used);
                    if(read==0)break;
                    used+=read;
                }
                string text=Encoding.UTF8.GetString(bytes,0,used);
                if(offset>0)
                {
                    int newline=text.IndexOf('\n');
                    return newline<0?"":text.Substring(newline+1);
                }
                return text;
            }
        }
    }
}
