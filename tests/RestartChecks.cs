using System;
using System.IO;
using System.Linq;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class RestartRegression
{
    internal static void Run(Action<bool,string> check,string helperPath)
    {
        CheckLaunchTiming(check);
        CheckSteamSessionState(check);
        CheckDetachedParent(check);
        string temp=Path.Combine(Path.GetTempPath(),"doorman-status-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try
        {
            string first=Path.Combine(temp,"doorman-"+Guid.NewGuid().ToString("N")+".ready"),second=Path.Combine(temp,"doorman-"+Guid.NewGuid().ToString("N")+".ready");
            RestartStatus.Begin(temp,first);check(RestartStatus.Read(first)=="configuring","a validated restart handoff starts with a configuration stage");
            RestartStatus.Write(temp,"loading");RestartStatus.Write(temp,"ready");check(RestartStatus.Read(first)=="ready","completed startup acknowledges only the current restart handoff");
            RestartStatus.Begin(temp,second);RestartStatus.Clear(temp,first);RestartStatus.Write(temp,"starting");
            check(RestartStatus.Read(second)=="starting"&&!File.Exists(first+".status"),"cleanup of an older helper cannot clear a newer restart session");
            string pointer=Path.Combine(temp,"doorman-restart-session.txt");
            File.WriteAllText(pointer,DateTime.UtcNow.AddMinutes(-14).Ticks+"\n"+Path.GetFileName(second));RestartStatus.Write(temp,"configuring");
            check(RestartStatus.Read(second)=="configuring","the restart handoff remains valid across an extended Steam release and game loading window");
            RestartStatus.Write(temp,"starting");
            File.WriteAllText(pointer,DateTime.UtcNow.AddMinutes(-16).Ticks+"\n"+Path.GetFileName(second));RestartStatus.Write(temp,"ready");
            check(RestartStatus.Read(second)=="starting","expired status markers cannot acknowledge an unrelated later launch");
            File.WriteAllText(pointer,new string('x',161));RestartStatus.Write(temp,"ready");check(RestartStatus.Read(second)=="starting","oversized restart markers are ignored");
            File.WriteAllText(pointer,DateTime.UtcNow.Ticks+"\n../outside.ready");RestartStatus.Write(temp,"ready");
            check(RestartStatus.Read(second)=="starting"&&!RestartStatus.ValidName("../outside.ready"),"restart progress cannot direct the client to an arbitrary file");
            try{RestartStatus.Begin(temp,Path.Combine(temp,"child",Path.GetFileName(first)));check(false,"nested restart pointer");}catch(InvalidDataException){check(true,"restart status is restricted to its exact cache directory");}
            try{RestartStatus.Write(temp,"download anything");check(false,"unknown status");}catch(ArgumentException){check(true,"only fixed informational restart stages are accepted");}
        }
        finally{Directory.Delete(temp,true);}
        using(var helper=AssemblyDefinition.ReadAssembly(helperPath))
        {
            var main=helper.MainModule.GetType("Program").Methods.Single(m=>m.Name=="Main").Body.Instructions.ToList();
            int detached=main.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.RestartProcessFamily"&&m.Name=="VerifyDesktopParent");
            int visual=main.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.Windows.Forms.Application"&&m.Name=="EnableVisualStyles");
            int clean=main.FindIndex(detached+1,i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.RestartProcessFamily"&&m.Name=="CleanSteamContext");
            check(detached>=0&&visual>detached,"the restart helper verifies its desktop parent before opening the status window or acknowledging readiness");
            check(clean>detached&&visual>clean,"the detached helper clears its inherited Steam launch identity before the restart workflow can request the game");
            var desktopBroker=helper.MainModule.GetType("KellysJOINCHECK.DesktopRestartBroker");
            var desktopCalls=AllTypes(new[]{desktopBroker}).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).Where(i=>i.Operand is MethodReference).Select(i=>(MethodReference)i.Operand).ToArray();
            check(desktopCalls.Any(m=>m.DeclaringType.FullName=="System.Threading.Thread"&&m.Name=="SetApartmentState")&&!desktopCalls.Any(m=>m.DeclaringType.FullName=="System.Diagnostics.Process"&&m.Name=="Start"),"the CLR desktop broker uses a managed STA thread and has no direct child-process fallback");
            var bootstrapInstructions=AllTypes(helper.MainModule.Types).SelectMany(t=>t.Methods).Where(m=>m.HasBody).SelectMany(m=>m.Body.Instructions).ToArray();
            check(main.Any(i=>i.Operand as string=="--broker")&&bootstrapInstructions.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.DesktopRestartBroker"&&m.Name=="Start"),"the fixed helper executable routes bootstrap mode through the CLR desktop broker");
            check(helper.MainModule.Kind==ModuleKind.Windows&&helper.MainModule.AssemblyReferences.Any(a=>a.Name=="System.Windows.Forms"),"restart status uses a Windows dialog without a console window");
            var window=helper.MainModule.GetType("StatusWindow");
            check(window.Methods.Any(m=>m.Name=="get_ShowWithoutActivation")&&window.Methods.Any(m=>m.Name=="Finish"),"status window does not take focus and can close after completed game loading");
            check(!helper.MainModule.AssemblyReferences.Any(a=>a.Name.Contains("Unity")||a.Name=="Assembly-CSharp"),"Windows helper progress remains separate from Unity and game assemblies");
            var wait=helper.MainModule.GetType("Program").Methods.Single(m=>m.Name=="WaitForGame").Body.Instructions;
            check(wait.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.RestartLaunchPolicy"&&m.Name=="Next"),"the running restart helper uses the tested cooldown and retry policy");
            check(wait.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.SteamSessionState"&&m.Name=="Read")&&wait.Any(i=>i.Operand is MethodReference m&&m.Name=="FindGame"),"restart dispatch reads Steam state and checks the exact new game process on each policy decision");
            var ordered=wait.ToList();
            int request=ordered.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.Diagnostics.Process"&&m.Name=="Start");
            int committed=ordered.FindIndex(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="KellysJOINCHECK.RestartLaunchPolicy"&&m.Name=="Launched");
            check(request>=0&&committed>request,"the helper commits its launch attempt only after sending the Steam request");
        }
    }
    private static void CheckLaunchTiming(Action<bool,string> check)
    {
        RestartLaunchAction NextAttempt(RestartLaunchPolicy policy,double elapsed,bool? busy,bool found,bool cancelled)
        {
            var action=policy.Next(elapsed,busy,found,cancelled);
            if(action==RestartLaunchAction.Launch)policy.Launched(elapsed);
            return action;
        }
        var idle=new RestartLaunchPolicy();
        check(NextAttempt(idle,0,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(idle,9.999,false,false,false)==RestartLaunchAction.Wait&&idle.Attempts==0,"Steam idle never bypasses the ten-second old-process cooldown");
        check(NextAttempt(idle,10,false,false,false)==RestartLaunchAction.Launch&&idle.Attempts==1,"Steam already idle launches once when the cooldown expires");
        check(NextAttempt(idle,39.999,false,false,false)==RestartLaunchAction.Wait&&idle.Attempts==1,"an accepted launch request gets thirty seconds before a retry");
        check(NextAttempt(idle,40,false,false,false)==RestartLaunchAction.Launch&&idle.Attempts==2,"an idle Steam client gets one retry after thirty seconds without a new game");
        check(NextAttempt(idle,70,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(idle,239.999,null,false,false)==RestartLaunchAction.Wait&&idle.Attempts==2,"restart makes at most two requests even when no game process appears");
        check(NextAttempt(idle,240,false,false,false)==RestartLaunchAction.Timeout&&idle.Attempts==2,"restart timing expires at the overall four-minute deadline");

        var recovering=new RestartLaunchPolicy();
        check(NextAttempt(recovering,0,true,false,false)==RestartLaunchAction.Wait&&NextAttempt(recovering,10,true,false,false)==RestartLaunchAction.Wait&&NextAttempt(recovering,125,true,false,false)==RestartLaunchAction.Wait&&recovering.Attempts==0,"Steam retaining its old process for 125 seconds still blocks launches while running or updating");
        check(NextAttempt(recovering,125.5,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(recovering,127.499,false,false,false)==RestartLaunchAction.Wait,"a newly idle Steam client must remain idle for two seconds");
        check(NextAttempt(recovering,127.5,false,false,false)==RestartLaunchAction.Launch&&recovering.Attempts==1,"Steam recovering after a long old-process release launches after stable idle");
        check(NextAttempt(recovering,157.5,true,false,false)==RestartLaunchAction.Wait&&recovering.Attempts==1,"Steam becoming busy again blocks a due retry");
        check(NextAttempt(recovering,200,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(recovering,202,false,false,false)==RestartLaunchAction.Launch&&recovering.Attempts==2,"a blocked retry resumes only after Steam becomes stably idle again");

        var optional=new RestartLaunchPolicy();
        check(NextAttempt(optional,0,null,false,false)==RestartLaunchAction.Wait&&NextAttempt(optional,9.999,null,false,false)==RestartLaunchAction.Wait,"missing optional Steam status retains the shutdown cooldown");
        check(NextAttempt(optional,10,null,false,false)==RestartLaunchAction.Launch&&optional.Attempts==1,"missing optional Steam status does not prevent launching after the cooldown");
        check(NextAttempt(optional,40,null,false,false)==RestartLaunchAction.Wait&&NextAttempt(optional,129.999,null,false,false)==RestartLaunchAction.Wait&&NextAttempt(optional,130,null,false,false)==RestartLaunchAction.Launch&&optional.Attempts==2,"unknown Steam status allows old crash-handler ownership two minutes before a single retry");
        var lostStatus=new RestartLaunchPolicy();NextAttempt(lostStatus,0,false,false,false);NextAttempt(lostStatus,10,false,false,false);
        check(NextAttempt(lostStatus,40,null,false,false)==RestartLaunchAction.Wait&&NextAttempt(lostStatus,130,null,false,false)==RestartLaunchAction.Launch,"losing known Steam status uses the conservative retry delay instead of the former idle signal");
        var restoredStatus=new RestartLaunchPolicy();NextAttempt(restoredStatus,10,null,false,false);
        check(NextAttempt(restoredStatus,40,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(restoredStatus,42,false,false,false)==RestartLaunchAction.Launch,"restoring known idle status permits the normal retry delay after idle stability is observed");

        var reset=new RestartLaunchPolicy();
        NextAttempt(reset,7,false,false,false);NextAttempt(reset,8,null,false,false);
        check(NextAttempt(reset,9,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(reset,10,false,false,false)==RestartLaunchAction.Wait,"unknown Steam status clears a previously observed idle period");
        NextAttempt(reset,10.5,true,false,false);
        check(NextAttempt(reset,11,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(reset,12.999,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(reset,13,false,false,false)==RestartLaunchAction.Launch,"a busy interruption resets idle stability instead of reusing an older idle observation");

        var cancelled=new RestartLaunchPolicy();
        check(NextAttempt(cancelled,240,false,true,true)==RestartLaunchAction.Cancelled&&cancelled.Attempts==0,"cancellation takes priority over a detected process and the timeout deadline");
        var detected=new RestartLaunchPolicy();
        check(NextAttempt(detected,240,true,true,false)==RestartLaunchAction.Detected&&detected.Attempts==0,"a game process found at the deadline is accepted without requesting another launch");
        var duringRetry=new RestartLaunchPolicy();NextAttempt(duringRetry,10,null,false,false);
        check(NextAttempt(duringRetry,40,null,true,false)==RestartLaunchAction.Detected&&duringRetry.Attempts==1,"a process appearing when the retry becomes due prevents duplicate dispatch");
        var deadline=new RestartLaunchPolicy();
        check(NextAttempt(deadline,239,true,false,false)==RestartLaunchAction.Wait&&NextAttempt(deadline,240,null,false,false)==RestartLaunchAction.Timeout&&deadline.Attempts==0,"the deadline prevents a late first launch even when Steam status becomes unknown");
        var lateIdle=new RestartLaunchPolicy();NextAttempt(lateIdle,238.5,false,false,false);
        check(NextAttempt(lateIdle,239.999,false,false,false)==RestartLaunchAction.Wait&&NextAttempt(lateIdle,240,false,false,false)==RestartLaunchAction.Timeout&&lateIdle.Attempts==0,"idle stability cannot extend the overall launch budget");
        var lateRequest=new RestartLaunchPolicy();NextAttempt(lateRequest,208,false,false,false);NextAttempt(lateRequest,210,false,false,false);
        check(NextAttempt(lateRequest,240,false,false,false)==RestartLaunchAction.Timeout&&lateRequest.Attempts==1,"a retry becoming due at the deadline cannot extend the launch budget");

        var uncommitted=new RestartLaunchPolicy();
        check(uncommitted.Next(10,null,false,false)==RestartLaunchAction.Launch&&uncommitted.Next(10,null,false,false)==RestartLaunchAction.Launch&&uncommitted.Attempts==0,"a suggested launch does not spend the URI request quota");
        check(uncommitted.Next(10.1,true,false,false)==RestartLaunchAction.Wait&&uncommitted.Attempts==0,"Steam becoming busy during the final dispatch check does not spend a launch attempt");
        uncommitted.Next(12,false,false,false);
        check(uncommitted.Next(14,false,false,false)==RestartLaunchAction.Launch&&uncommitted.Attempts==0,"a deferred first request is available after Steam becomes stably idle");
        uncommitted.Launched(14);
        check(uncommitted.Attempts==1&&uncommitted.Next(44,true,false,false)==RestartLaunchAction.Wait&&uncommitted.Attempts==1,"a blocked final retry check preserves the one remaining URI request");
        uncommitted.Next(46,false,false,false);
        check(uncommitted.Next(48,false,false,false)==RestartLaunchAction.Launch&&uncommitted.Attempts==1,"an uncommitted retry remains available after Steam releases the session again");
        uncommitted.Launched(48);
        try {uncommitted.Launched(49);check(false,"excess restart request");}
        catch(InvalidOperationException) {check(uncommitted.Attempts==2,"committing a third restart request is rejected without exceeding the limit");}
        var actualDispatch=new RestartLaunchPolicy();actualDispatch.Next(0,false,false,false);actualDispatch.Next(10,false,false,false);actualDispatch.Launched(12);
        check(actualDispatch.Next(41.999,false,false,false)==RestartLaunchAction.Wait&&actualDispatch.Next(42,false,false,false)==RestartLaunchAction.Launch,"the retry clock starts at actual dispatch rather than the earlier policy snapshot");
    }
    private static void CheckSteamSessionState(Action<bool,string> check)
    {
        const int oldPid=22328;
        const string game=@"C:\Program Files (x86)\Steam\steamapps\common\Nuclear Option\NuclearOption.exe";
        const string stamp="[2026-10-06 18:17:48] ";
        string added=stamp+"AppID 2168680 adding PID "+oldPid+" as a tracked process \""+game+"\"";
        string removed=stamp+"Remove 2168680 from running list";
        string rootGone=stamp+"AppID 2168680 no longer tracking PID "+oldPid+", exit code 0";
        string crashAdded=stamp+"AppID 2168680 adding PID 22329 as a tracked process \"C:\\Game\\UnityCrashHandler64.exe\"";
        string crashGone=stamp+"AppID 2168680 no longer tracking PID 22329, exit code 0";
        string later=stamp+"AppID 2168680 adding PID 44100 as a tracked process \""+game+"\"";
        check(SteamSessionState.Parse(added,oldPid,game)==true,"Steam's tracked root PID and exact executable bind the previous game session");
        check(SteamSessionState.Parse(added,oldPid,game.ToLowerInvariant())==true,"the tracked executable comparison follows Windows path casing");
        string doubled=stamp+"AppID 2168680 adding PID "+oldPid+" as a tracked process \"\""+game+"\"\"";
        check(SteamSessionState.Parse(doubled,oldPid,game)==true,"Steam's double-quoted tracked executable format is accepted");
        check(SteamSessionState.Parse(added+"\r\n"+rootGone,oldPid,game)==true,"removing the old game PID alone does not declare Steam's app session idle");
        check(SteamSessionState.Parse(added+"\n"+crashAdded+"\n"+rootGone+"\n"+crashGone,oldPid,game)==true,"removing both the game and crash handler still waits for Steam's complete app release");
        check(SteamSessionState.Parse(added+"\n"+rootGone+"\n"+crashGone+"\n"+removed,oldPid,game)==false,"only the full bound app session removal marks Steam idle");
        check(SteamSessionState.Parse(added+"\n"+removed+"\n"+later,oldPid,game)==true,"a later tracked Nuclear Option process makes Steam busy again after release");
        check(SteamSessionState.Parse(added+"\n"+removed+"\n"+later+"\n"+removed,oldPid,game)==false,"the latest full app-session removal follows a later Nuclear Option launch");
        check(SteamSessionState.Parse(removed+"\n"+added,oldPid,game)==true,"an older app removal before the bound launch cannot release the current session");
        check(SteamSessionState.Parse(removed+"\n"+later,oldPid,game)==null,"a log without the bound old process is unknown rather than idle");
        check(SteamSessionState.Parse(added.Replace("AppID 2168680","AppID 21686800"),oldPid,game)==null,"another app with a matching numeric prefix cannot bind the old game session");
        check(SteamSessionState.Parse(added.Replace("PID 22328","PID 223280"),oldPid,game)==null,"another tracked PID with a matching numeric prefix cannot bind the old game session");
        check(SteamSessionState.Parse(added.Replace(game,game+".old"),oldPid,game)==null&&SteamSessionState.Parse(added.Replace(game,@"D:\Other\NuclearOption.exe"),oldPid,game)==null,"an executable path prefix or another installation cannot bind the session");
        check(SteamSessionState.Parse(added+"\n"+removed.Replace("2168680","21686800"),oldPid,game)==true,"another app's removal cannot release the bound Nuclear Option session");
        check(SteamSessionState.Parse(added+"\n"+removed+"\n"+later.Replace("2168680","21686800"),oldPid,game)==false,"a different app's later tracked process cannot reopen the Nuclear Option session");
        check(SteamSessionState.Parse("",oldPid,game)==null&&SteamSessionState.Parse(null!,oldPid,game)==null&&SteamSessionState.Parse(added,0,game)==null&&SteamSessionState.Parse(added,-1,game)==null&&SteamSessionState.Parse(added,oldPid,"")==null,"missing log and invalid binding inputs remain unknown");
        string bounded=new string('x',SteamSessionState.MaxTailBytes-added.Length-1)+"\n"+added;
        check(SteamSessionState.Parse(bounded,oldPid,game)==true&&SteamSessionState.Parse("x"+bounded,oldPid,game)==null,"Steam session parsing accepts the bounded tail and rejects an oversized input");
        var started=new DateTime(2026,10,6,18,17,48);
        string staleAdded=added.Replace(stamp,"[2026-10-06 17:17:48] "),staleRemoved=removed.Replace(stamp,"[2026-10-06 17:17:48] ");
        check(SteamSessionState.Parse(staleAdded+"\n"+removed,oldPid,game,started)==null,"a reused PID from an older launch cannot bind the current Steam session");
        check(SteamSessionState.Parse(staleAdded+"\n"+staleRemoved+"\n"+added,oldPid,game,started)==true,"a current tracked launch can bind after stale reused-PID observations are discarded");
        check(SteamSessionState.Parse(added+"\n"+staleRemoved,oldPid,game,started)==true,"an older timestamped removal cannot release a fresh bound session");
        check(SteamSessionState.Parse(added,oldPid,game,started)==true&&SteamSessionState.Parse(added,oldPid,game,started.AddSeconds(1))==null,"the supplied launch timestamp bounds old-session observations inclusively");
        check(SteamSessionState.Parse(added.Replace(stamp,"[2026-99-06 18:17:48] "),oldPid,game)==null&&SteamSessionState.Parse(added.Substring(stamp.Length),oldPid,game)==null,"malformed or missing log timestamps cannot bind a process");
        check(SteamSessionState.Parse("prefix "+added,oldPid,game)==null&&SteamSessionState.Parse(added.Replace(stamp,stamp+"prefix "),oldPid,game)==null,"a tracked-process substring outside the exact timestamped event prefix is ignored");
        check(SteamSessionState.Parse(added+"\n"+removed+" suffix",oldPid,game)==true&&SteamSessionState.Parse(added+"\n"+removed.Replace(stamp,stamp+"prefix "),oldPid,game)==true,"an app-removal substring or suffixed message cannot release the Steam session");
        foreach(bool? registry in new bool?[]{null,false,true})foreach(bool? session in new bool?[]{null,false,true})
        {
            bool? expected=registry==true||session==true?true:registry==false||session==false?false:(bool?)null;
            check(SteamSessionState.Combine(registry,session)==expected,"Steam observations combine conservatively: registry="+registry+", session="+session);
        }
        var policy=new RestartLaunchPolicy();
        bool? busy=SteamSessionState.Combine(false,SteamSessionState.Parse(added+"\n"+rootGone+"\n"+crashGone,oldPid,game));
        check(policy.Next(125,busy,false,false)==RestartLaunchAction.Wait&&policy.Attempts==0,"a registry idle flag cannot override the retained tracked Steam app session");
        busy=SteamSessionState.Combine(false,SteamSessionState.Parse(added+"\n"+rootGone+"\n"+crashGone+"\n"+removed,oldPid,game));
        check(policy.Next(125.5,busy,false,false)==RestartLaunchAction.Wait&&policy.Next(127.5,busy,false,false)==RestartLaunchAction.Launch,"the launch policy proceeds after the parsed full-session release becomes stably idle");
    }
    private static void CheckDetachedParent(Action<bool,string> check)
    {
        check(RestartProcessFamily.IsDetached(918,918,27112),"the desktop Explorer process can parent the helper independently of the old game");
        check(!RestartProcessFamily.IsDetached(27112,918,27112),"the old Nuclear Option process cannot parent its own restart helper");
        check(!RestartProcessFamily.IsDetached(18668,918,27112),"an unrelated intermediary process is not accepted as the verified desktop parent");
        check(!RestartProcessFamily.IsDetached(27112,27112,27112),"matching identifiers cannot disguise a helper still attached to the old game");
        check(!RestartProcessFamily.IsDetached(0,918,27112)&&!RestartProcessFamily.IsDetached(918,0,27112)&&!RestartProcessFamily.IsDetached(918,918,0),"missing parent, desktop or old-game identities fail closed");
        check(!RestartProcessFamily.IsDetached(-1,918,27112)&&!RestartProcessFamily.IsDetached(918,-1,27112)&&!RestartProcessFamily.IsDetached(918,918,-1),"invalid process identifiers cannot pass desktop-parent verification");
        check(RestartProcessFamily.IsDetached(int.MaxValue,int.MaxValue,1),"desktop-parent comparisons do not overflow at the valid PID boundary");
        string[] names={"SteamAppId","SteamGameId","SteamOverlayGameId"};
        string?[] previous=names.Select(name=>Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.Process)).ToArray();
        string unrelated="DOORMAN_RESTART_TEST_"+Guid.NewGuid().ToString("N");
        try
        {
            foreach(string name in names)Environment.SetEnvironmentVariable(name,"2168680",EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(unrelated,"preserved",EnvironmentVariableTarget.Process);
            RestartProcessFamily.CleanSteamContext();
            check(names.All(name=>Environment.GetEnvironmentVariable(name,EnvironmentVariableTarget.Process)==null),"the detached helper clears only its process copy of the inherited Steam app identity");
            check(Environment.GetEnvironmentVariable(unrelated,EnvironmentVariableTarget.Process)=="preserved","Steam identity cleanup preserves unrelated process environment values");
        }
        finally
        {
            for(int i=0;i<names.Length;i++)Environment.SetEnvironmentVariable(names[i],previous[i],EnvironmentVariableTarget.Process);
            Environment.SetEnvironmentVariable(unrelated,null,EnvironmentVariableTarget.Process);
        }
    }
    private static System.Collections.Generic.IEnumerable<TypeDefinition> AllTypes(System.Collections.Generic.IEnumerable<TypeDefinition> roots)
    {
        foreach(var type in roots)
        {
            yield return type;
            foreach(var nested in AllTypes(type.NestedTypes))yield return nested;
        }
    }
}
