using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KellysJOINCHECK
{
    // Ask the existing desktop Explorer to create the helper. A direct game
    // child can remain part of Steam's old session while waiting for it to end.
    // Microsoft desktop automation pattern:
    // https://devblogs.microsoft.com/oldnewthing/20131118-00/?p=2643/
    internal static class DesktopRestartBroker
    {
        private const int TimeoutMilliseconds=5000;
        private static readonly Guid ShellWindowsClass=new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        private static readonly Guid TopLevelBrowser=new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        private static readonly Guid ShellBrowserInterface=new Guid("000214E2-0000-0000-C000-000000000046");
        private static readonly Guid DispatchInterface=new Guid("00020400-0000-0000-C000-000000000046");

        // This runs only in the short-lived Windows CLR bootstrap, keeping COM
        // automation out of Unity Mono and off the game's main thread.
        // On ANY exception, the caller must write the helper's .cancel marker:
        // an already-dispatched COM call cannot be revoked by a timeout.
        internal static void Start(string helper,string arguments,string directory)
        {
            if(Environment.OSVersion.Platform!=PlatformID.Win32NT)throw new IOException("Automatic restart needs Windows Explorer.");
            helper=Absolute(helper,"restart helper");directory=Absolute(directory,"helper directory");
            if(!Path.GetFileName(helper).Equals("KellysDOORMANRestart.exe",StringComparison.OrdinalIgnoreCase)||!File.Exists(helper))throw new IOException("The DOORMAN restart helper is missing.");
            if(!Directory.Exists(directory)||!Path.GetDirectoryName(helper)!.Equals(directory,StringComparison.OrdinalIgnoreCase))throw new IOException("The restart helper directory is invalid.");
            if(string.IsNullOrWhiteSpace(arguments)||arguments.Length>32767||arguments.IndexOfAny(new[]{'\0','\r','\n'})>=0)throw new IOException("The restart helper arguments are invalid.");
            var completion=new TaskCompletionSource<Exception?>();int dispatchState=0;
            var worker=new Thread(()=>
            {
                Exception? failure=null;
                try{Execute(helper,arguments,directory,()=>Interlocked.CompareExchange(ref dispatchState,1,0)==0,()=>Volatile.Read(ref dispatchState)==2);}
                catch(Exception ex){failure=ex;}
                finally{completion.TrySetResult(failure);}
            }) { IsBackground=true,Name="DOORMAN Explorer restart broker" };
            worker.SetApartmentState(ApartmentState.STA);worker.Start();
            if(!completion.Task.Wait(TimeoutMilliseconds))
            {
                Interlocked.Exchange(ref dispatchState,2);
                throw new IOException("Windows Explorer did not answer in time. Restart was cancelled; the game was kept open.");
            }
            var failure=completion.Task.Result;
            if(failure!=null)throw new IOException("Windows Explorer could not start the restart helper: "+failure.GetBaseException().Message,failure);
        }

        private static void Execute(string helper,string arguments,string directory,Func<bool> beginDispatch,Func<bool> cancelled)
        {
            uint shellPid=VerifyDesktopExplorer();var owned=new List<object>();
            try
            {
                var type=Type.GetTypeFromCLSID(ShellWindowsClass,true)!;
                object windows=Own(owned,Activator.CreateInstance(type)??throw new IOException("The desktop shell is unavailable."));
                // SWC_DESKTOP=8, SWFO_NEEDDISPATCH=1. This selects the genuine
                // desktop broker, rather than a Shell.Application in our process.
                object?[] find={null,null,8,0,1};var byReference=new ParameterModifier(find.Length);
                byReference[0]=true;byReference[1]=true;byReference[3]=true;
                object desktop=Own(owned,windows.GetType().InvokeMember("FindWindowSW",BindingFlags.InvokeMethod,null,windows,find,new[]{byReference},null,null)??throw new IOException("The desktop Explorer window is unavailable."));
                var provider=(ShellServiceProvider)desktop;Guid service=TopLevelBrowser,requested=ShellBrowserInterface;
                int result=provider.QueryService(ref service,ref requested,out object browserObject);ThrowIfFailed(result);
                object browserOwned=Own(owned,browserObject);var browser=(ShellBrowser)browserOwned;
                ThrowIfFailed(browser.GetWindow(out IntPtr browserWindow));GetWindowThreadProcessId(browserWindow,out uint browserPid);
                if(browserWindow==IntPtr.Zero||browserPid!=shellPid)throw new IOException("The desktop Explorer changed while preparing restart. Try again.");
                ThrowIfFailed(browser.QueryActiveShellView(out ShellView view));Own(owned,view);
                Guid dispatch=DispatchInterface;ThrowIfFailed(view.GetItemObject(0,ref dispatch,out object folder));Own(owned,folder);
                object application=Own(owned,folder.GetType().InvokeMember("Application",BindingFlags.GetProperty,null,folder,null)??throw new IOException("The desktop shell application is unavailable."));
                if(cancelled()||!beginDispatch()||cancelled())throw new OperationCanceledException("Restart broker was cancelled before launch.");
                application.GetType().InvokeMember("ShellExecute",BindingFlags.InvokeMethod,null,application,new object[]{helper,arguments,directory,"open",0});
            }
            finally
            {
                // Every RCW stays inside this owned STA thread and is never
                // handed to another caller. Release in reverse acquisition order.
                for(int index=owned.Count-1;index>=0;index--)
                    try{if(Marshal.IsComObject(owned[index]))Marshal.FinalReleaseComObject(owned[index]);}catch{ }
            }
        }

        private static object Own(List<object> owned,object value)
        {if(!owned.Exists(item=>ReferenceEquals(item,value)))owned.Add(value);return value;}
        private static string Absolute(string path,string label)
        {if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))throw new IOException("The "+label+" path must be absolute.");return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);}
        private static void ThrowIfFailed(int result){if(result<0)Marshal.ThrowExceptionForHR(result);}
        private static uint VerifyDesktopExplorer()
        {
            IntPtr window=GetShellWindow();GetWindowThreadProcessId(window,out uint pid);
            if(window==IntPtr.Zero||pid==0)throw new IOException("Windows Explorer is unavailable. Start the game normally after closing it.");
            IntPtr process=OpenProcess(0x1000,false,pid),token=IntPtr.Zero;
            if(process==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot verify the desktop Explorer process.");
            try
            {
                var executable=new StringBuilder(32768);uint length=(uint)executable.Capacity;
                if(!QueryFullProcessImageName(process,0,executable,ref length))throw new Win32Exception(Marshal.GetLastWin32Error());
                string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"explorer.exe");
                if(!Path.GetFullPath(executable.ToString()).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("The desktop shell is not Windows Explorer.");
                if(!ProcessIdToSessionId(pid,out uint shellSession)||!ProcessIdToSessionId(GetCurrentProcessId(),out uint currentSession)||shellSession!=currentSession)throw new IOException("The desktop Explorer belongs to another Windows session.");
                if(!OpenProcessToken(process,8,out token))throw new Win32Exception(Marshal.GetLastWin32Error());
                using(var shellIdentity=new WindowsIdentity(token))
                using(var currentIdentity=WindowsIdentity.GetCurrent())
                    if(shellIdentity.User==null||currentIdentity.User==null||!shellIdentity.User.Equals(currentIdentity.User))throw new IOException("The desktop Explorer belongs to another Windows user.");
                return pid;
            }
            finally{if(token!=IntPtr.Zero)CloseHandle(token);CloseHandle(process);}
        }

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
        [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr OpenProcess(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint processId);
        [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint length);
        [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId,out uint sessionId);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();
        [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
        [DllImport("kernel32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);

        [ComImport,Guid("6D5140C1-7436-11CE-8034-00AA006009FA"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ShellServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service,ref Guid requested,[MarshalAs(UnmanagedType.Interface)]out object result);
        }
        [ComImport,Guid("000214E2-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ShellBrowser
        {
            [PreserveSig] int GetWindow(out IntPtr window);
            [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)]bool enter);
            [PreserveSig] int InsertMenusSB(IntPtr menu,IntPtr widths);
            [PreserveSig] int SetMenuSB(IntPtr menu,IntPtr reserved,IntPtr activeWindow);
            [PreserveSig] int RemoveMenusSB(IntPtr menu);
            [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)]string text);
            [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)]bool enable);
            [PreserveSig] int TranslateAcceleratorSB(IntPtr message,ushort commandId);
            [PreserveSig] int BrowseObject(IntPtr pidl,uint flags);
            [PreserveSig] int GetViewStateStream(uint mode,out IntPtr stream);
            [PreserveSig] int GetControlWindow(uint id,out IntPtr window);
            [PreserveSig] int SendControlMsg(uint id,uint message,IntPtr word,IntPtr value,out IntPtr result);
            [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)]out ShellView view);
            [PreserveSig] int OnViewWindowActive(IntPtr view);
            [PreserveSig] int SetToolbarItems(IntPtr buttons,uint count,uint flags);
        }
        [ComImport,Guid("000214E3-0000-0000-C000-000000000046"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ShellView
        {
            [PreserveSig] int GetWindow(out IntPtr window);
            [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)]bool enter);
            [PreserveSig] int TranslateAccelerator(IntPtr message);
            [PreserveSig] int EnableModeless([MarshalAs(UnmanagedType.Bool)]bool enable);
            [PreserveSig] int UIActivate(uint state);
            [PreserveSig] int Refresh();
            [PreserveSig] int CreateViewWindow(IntPtr previous,IntPtr settings,IntPtr browser,IntPtr bounds,out IntPtr window);
            [PreserveSig] int DestroyViewWindow();
            [PreserveSig] int GetCurrentInfo(IntPtr settings);
            [PreserveSig] int AddPropertySheetPages(uint reserved,IntPtr callback,IntPtr parameter);
            [PreserveSig] int SaveViewState();
            [PreserveSig] int SelectItem(IntPtr pidl,uint flags);
            [PreserveSig] int GetItemObject(uint item,ref Guid requested,[MarshalAs(UnmanagedType.Interface)]out object result);
        }
    }
}
