using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using KellysJOINCHECK;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool broker=args.Length>0&&args[0]=="--broker";
        if(broker){var values=new string[args.Length-1];Array.Copy(args,1,values,0,values.Length);args=values;}
        if(args.Length==1&&args[0]=="--self-test") { Console.WriteLine("DOORMAN restart helper: wait-for-exit, fixed Steam app 2168680, no process termination.");return 0; }
        if(args.Length==2&&args[0]=="--probe-parent")
        {
            try
            {
                if(broker){RestartProcessFamily.CleanSteamContext();LaunchFromDesktop("--probe-parent \""+Path.GetFullPath(args[1])+"\"");return 0;}
                var family=RestartProcessFamily.CaptureCurrent();RestartProcessFamily.CleanSteamContext();
                bool clean=Environment.GetEnvironmentVariable("SteamAppId")==null&&Environment.GetEnvironmentVariable("SteamGameId")==null&&Environment.GetEnvironmentVariable("SteamOverlayGameId")==null;
                File.WriteAllText(Path.GetFullPath(args[1]),"{\"currentPid\":"+family.CurrentPid+",\"parentPid\":"+family.ParentPid+",\"desktopPid\":"+family.DesktopPid+",\"steamContextCleared\":"+(clean?"true":"false")+"}");return 0;
            }
            catch{return 1;}
        }
        try
        {
            if(args.Length!=4||!int.TryParse(args[0],out int pid)||!long.TryParse(args[1],out long started))return 2;
            string game=Path.GetFullPath(args[2]),ready=Path.GetFullPath(args[3]);
            string root=Path.GetDirectoryName(game)!+Path.DirectorySeparatorChar;
            if(!Path.GetFileName(game).Equals("NuclearOption.exe",StringComparison.OrdinalIgnoreCase)||!ready.StartsWith(Path.Combine(root,"BepInEx","cache")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!RestartStatus.ValidName(Path.GetFileName(ready)))return 2;
            if(File.Exists(ready+".cancel")){File.Delete(ready+".cancel");return 3;}
            using(var parent=Process.GetProcessById(pid))
            {
                if(parent.StartTime.ToUniversalTime().Ticks!=started||!parent.MainModule.FileName.Equals(game,StringComparison.OrdinalIgnoreCase))return 2;
                if(broker)
                {
                    try
                    {
                        RestartProcessFamily.CleanSteamContext();
                        LaunchFromDesktop(pid+" "+started+" \""+game+"\" \""+ready+"\"");
                        Log(Path.GetDirectoryName(ready)!,"Explorer restart dispatch completed; bootstrap exiting before the game closes.");return 0;
                    }
                    catch(Exception ex){try{File.WriteAllText(ready+".cancel","cancel");}catch{}Log(Path.GetDirectoryName(ready)!,"Explorer restart dispatch failed: "+ex.Message);return 1;}
                }
                RestartProcessFamily.VerifyDesktopParent(pid);RestartProcessFamily.CleanSteamContext();
                var family=RestartProcessFamily.CaptureCurrent();Log(Path.GetDirectoryName(ready)!,"Detached restart helper PID "+family.CurrentPid+"; desktop parent PID "+family.ParentPid+" verified.");
                Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                using(var window=new StatusWindow())
                {
                    window.Shown+=async(sender,e)=>
                    {
                        int code=await Task.Run(()=>Restart(parent,game,ready,window));
                        window.Finish(code==0||code==3);
                    };
                    Application.Run(window);return window.Result;
                }
            }
        }
        catch { return 1; }
    }
    private static void LaunchFromDesktop(string arguments)
    {
        using(var current=Process.GetCurrentProcess())
        {string helper=current.MainModule.FileName;DesktopRestartBroker.Start(helper,arguments,Path.GetDirectoryName(helper)!);}
    }
    private static int Restart(Process parent,string game,string ready,StatusWindow window)
    {
        string cache=Path.GetDirectoryName(ready)!;var clock=Stopwatch.StartNew();DateTime startedLocal=parent.StartTime;
        try
        {
            Directory.CreateDirectory(cache);
            if(File.Exists(ready+".cancel")){File.Delete(ready+".cancel");return 3;}
            RestartStatus.Begin(cache,ready);File.WriteAllText(ready,"ready");Log(cache,"Ready; waiting for the old game process to exit.");
            while(!parent.WaitForExit(250))
            {
                if(File.Exists(ready+".cancel")){File.Delete(ready);File.Delete(ready+".cancel");return 3;}
                if(clock.Elapsed.TotalMinutes>=2)throw new IOException("The game has not finished closing. Start it normally after it closes.");
            }
            Log(cache,"Old process exited after "+clock.Elapsed.TotalSeconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" seconds.");
            if(File.Exists(ready+".cancel")){File.Delete(ready);File.Delete(ready+".cancel");return 3;}
            if(!File.Exists(ready))return 3;File.Delete(ready);
            window.Stage("Waiting for Steam","The game has closed. Waiting for Steam to finish the previous session before reopening it.");RestartStatus.Write(cache,"starting");
            using(var next=WaitForGame(game,parent.Id,DateTime.UtcNow,startedLocal,ready,cache,window))
            {
                if(next==null)throw new IOException("Steam has not reopened Nuclear Option yet. Start it normally; your saved setup is still available.");
                Log(cache,"New process detected after "+clock.Elapsed.TotalSeconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" seconds.");
                window.Stage("Loading game and mods","The game is loading. This window closes when the final menu is ready.");
                string last="";var loading=Stopwatch.StartNew();
                while(loading.Elapsed.TotalMinutes<5&&!next.HasExited)
                {
                    string state=RestartStatus.Read(ready);
                    if(state=="ready"){Log(cache,"Final menu ready after "+clock.Elapsed.TotalSeconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" seconds.");return 0;}
                    if(state!=last)
                    {
                        last=state;
                        if(state=="configuring")window.Stage("Configuring mods","Applying downloaded updates and your saved mod selection.");
                        else if(state=="loading")window.Stage("Loading game and mods","Waiting for Nuclear Option and its mods to finish loading. The first menu flash does not count.");
                    }
                    Thread.Sleep(1000);
                }
                throw new IOException(next.HasExited?"The new game process closed before loading finished. Check the BepInEx log.":"The game is taking longer than expected to load. You can close this box; the game can keep loading.");
            }
        }
        catch(OperationCanceledException){Log(cache,"Restart cancelled before launch.");return 3;}
        catch(Exception ex){Log(cache,"Restart status: "+ex.Message);window.Stage("Restart needs attention",ex.Message,true);return 1;}
        finally {RestartStatus.Clear(cache,ready);try{File.Delete(ready);File.Delete(ready+".cancel");}catch{}}
    }
    private static Process? WaitForGame(string game,int oldId,DateTime exited,DateTime startedLocal,string ready,string cache,StatusWindow window)
    {
        var wait=Stopwatch.StartNew();var policy=new RestartLaunchPolicy();string last="";
        while(true)
        {
            var next=FindGame(game,exited);
            bool? busy=SteamSessionState.Read(oldId,game,startedLocal);
            string state=busy.HasValue?(busy.Value?"busy":"idle"):"unavailable";
            if(state!=last){last=state;Log(cache,"Steam session state: "+state+"; "+wait.Elapsed.TotalSeconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" seconds since game exit.");}
            var action=policy.Next(wait.Elapsed.TotalSeconds,busy,next!=null,File.Exists(ready+".cancel"));
            if(action==RestartLaunchAction.Detected)return next;
            next?.Dispose();
            if(action==RestartLaunchAction.Cancelled)throw new OperationCanceledException();
            if(action==RestartLaunchAction.Timeout)return null;
            if(action==RestartLaunchAction.Launch)
            {
                // A manual/late Steam launch may have appeared since the policy snapshot.
                if(File.Exists(ready+".cancel"))throw new OperationCanceledException();
                next=FindGame(game,exited);if(next!=null)return next;
                action=policy.Next(wait.Elapsed.TotalSeconds,SteamSessionState.Read(oldId,game,startedLocal),false,File.Exists(ready+".cancel"));
                if(action==RestartLaunchAction.Cancelled)throw new OperationCanceledException();
                if(action==RestartLaunchAction.Timeout)return null;
                if(action==RestartLaunchAction.Launch)
                {
                    next=FindGame(game,exited);if(next!=null)return next;
                    if(File.Exists(ready+".cancel"))throw new OperationCanceledException();
                    window.Stage("Starting game",policy.Attempts==0?"Launching Nuclear Option through Steam. Your usual client plugins are preserved.":"Steam did not start the game. Retrying once; your saved setup is still available.");
                    using(var request=Process.Start(new ProcessStartInfo("steam://run/2168680") { UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden })) { }
                    policy.Launched(wait.Elapsed.TotalSeconds);
                    Log(cache,"Launch requested through Steam (attempt "+policy.Attempts+") after "+wait.Elapsed.TotalSeconds.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" seconds since game exit.");
                }
            }
            Thread.Sleep(1000);
        }
    }
    private static Process? FindGame(string game,DateTime exited)
    {
        var candidates=Process.GetProcessesByName("NuclearOption");Process? found=null;
        try
        {
            foreach(var candidate in candidates)
            {
                try {if(candidate.StartTime.ToUniversalTime()>=exited.AddSeconds(-5)&&candidate.MainModule.FileName.Equals(game,StringComparison.OrdinalIgnoreCase)){found=candidate;break;}}catch{}
            }
            return found;
        }
        finally {foreach(var candidate in candidates)if(candidate!=found)candidate.Dispose();}
    }
    private static void Log(string cache,string message)
    {
        try {string file=Path.Combine(cache,"DOORMAN-restart.log");if(File.Exists(file)&&new FileInfo(file).Length>65536)File.WriteAllText(file,"");File.AppendAllText(file,DateTime.UtcNow.ToString("o")+" "+message+Environment.NewLine);}catch{}
    }
}
