using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class UpdateRegression
{
    internal static int RealVersionProbe(string root)
    {
        if(Directory.Exists(root)&&Directory.EnumerateFileSystemEntries(root).Any())throw new IOException("The update evidence folder must be empty.");
        string plugins=Path.Combine(root,"plugins");Directory.CreateDirectory(plugins);
        using(var service=new ModUpdateService(plugins,root))
        {
            var current=service.CheckAsync("Aryx3D/Aryx-MiG-15","","1.1.3","",CancellationToken.None).GetAwaiter().GetResult();
            if(current.Release==null||current.Newer||current.DisplayVersion!="1.1.3"||current.Release.Tag!="1.13")throw new InvalidDataException("The real MiG-15 release was not recognized as current: "+current.Message);
            var older=service.CheckAsync("Aryx3D/Aryx-MiG-15","","1.1.2","",CancellationToken.None).GetAwaiter().GetResult();
            if(!older.Newer)throw new InvalidDataException("An older MiG-15 version did not offer the real update.");
            Console.WriteLine("PASS: GitHub release title 1.1.3 with tag 1.13 is current for 1.1.3 and newer than 1.1.2.");return 0;
        }
    }
    internal static void Run(Action<bool,string> check,AssemblyDefinition client,AssemblyDefinition server,string startupPath)
    {
        StandaloneBundleUpdateChecks.Run(check);
        AutoSetupBatchChecks.Run(check);
        SavedVersions(check);
        check(ModReleaseVersion.Compare("v1.1.7.1","1.1.7")==1,"four-part mod hotfix versions compare correctly");
        check(ModReleaseVersion.Compare("1.2.0","1.2")==0,"missing numeric components compare as zero");
        check(ModReleaseVersion.Compare("1.0.0","1.0.0-rc.2")==1&&ModReleaseVersion.Compare("1.0-rc.10","1.0-rc.2")==1,"stable and numbered prerelease comparisons");
        check(ModReleaseVersion.Compare("1.0+build","1.0+other")==0,"build metadata never triggers an update");
        foreach(var version in new[]{"Nightly","release-2026","v1","99999999999.0","1.0/etc"}) check(ModReleaseVersion.Compare(version,"1.0")==null,"unknown version names do not trigger guessed upgrades");
        check(GitHubRepository.FromUrl("https://github.com/Aryx3D/Aryx-s-FS-41-Eclipse/releases/latest")=="Aryx3D/Aryx-s-FS-41-Eclipse","GitHub links identify repositories regardless of release paths");
        foreach(var url in new[]{"https://github.com.attacker.test/a/b","http://github.com/a/b","https://user:password@github.com/a/b","https://github.com:8443/a/b","file:///a/b"}) check(GitHubRepository.FromUrl(url)=="","unsafe repository links rejected");
        var installRepo=GitHubInstallLink.Parse("https://github.com/owner/repo/releases/latest");
        check(installRepo.Repository=="owner/repo"&&installRepo.Tag=="","install link accepts a GitHub repository latest release");
        var installTag=GitHubInstallLink.Parse("https://github.com/owner/repo/releases/tag/v1.2.3");
        check(installTag.Repository=="owner/repo"&&installTag.Tag=="v1.2.3","install link preserves an explicitly requested release");
        var installAsset=GitHubInstallLink.Parse("https://github.com/owner/repo/releases/download/v1.2.3/test.dll");
        check(installAsset.Tag=="v1.2.3"&&installAsset.Asset=="test.dll","install link preserves an explicitly requested asset");
        foreach(string badLink in new[]{"https://github.com.attacker.test/a/b","http://github.com/a/b","https://github.com/a/b/issues/2","https://github.com/a/b/releases/tag/v1%2F2","https://github.com/a/b?x=1","file:///a/b"})Throws(check,()=>GitHubInstallLink.Parse(badLink),"unsafe or ambiguous install links fail closed");
        var releaseFixture=new GitHubRelease {Tag="v1.2.3",Url="https://github.com/owner/repo/releases/tag/v1.2.3",Assets=new[]{new GitHubAsset {Name="test.dll",Size=5,State="uploaded",Url="https://github.com/owner/repo/releases/download/v1.2.3/test.dll"},new GitHubAsset {Name="other.nobp",Size=5,State="uploaded",Url="https://github.com/owner/repo/releases/download/v1.2.3/other.nobp"}}};
        check(installRepo.Select(releaseFixture)==null&&installAsset.Select(releaseFixture)?.Name=="test.dll","multiple assets require a specific link rather than an arbitrary choice");
        check(!GitHubRepository.Valid("a/../b")&&!GitHubRepository.Valid("a/.."),"repositories cannot redirect the API path");
        check(GitHubRepository.AssetMatches("AryxWeaponsPack_1.2.dll","AryxWeaponsPack_*.dll")&&!GitHubRepository.AssetMatches("Other.dll","AryxWeaponsPack_*.dll"),"optional manifest patterns select only intended release assets");
        check(!GitHubRepository.DownloadUrl("https://github.com/other/repo/releases/download/1.2/a.dll","a/b"),"assets must come from the chosen repository");
        check(!GitHubRepository.RedirectUrl(new Uri("https://release-assets.githubusercontent.com.evil.test/a"))&&!GitHubRepository.RedirectUrl(new Uri("http://objects.githubusercontent.com/a")),"download redirects remain HTTPS on exact GitHub hosts");
        string temp=Path.Combine(Path.GetTempPath(),"doorman-update-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try { GameplayPackages(check,temp); Files(check,temp); QueueCapacity(check,temp); StartupQueueConflicts(check,temp); JsonWriteSafety(check,temp); DanglingWriteSafety(check,temp); PreviewFiles(check,temp); InstallFiles(check,temp); Network(check,temp).GetAwaiter().GetResult(); InstallNetwork(check,temp).GetAwaiter().GetResult(); ReportedDownloads(check,temp).GetAwaiter().GetResult(); ReportedUpdate(check,temp).GetAwaiter().GetResult(); ServerCandidateDownload(check,temp).GetAwaiter().GetResult(); InstallLinkNetwork(check,temp).GetAwaiter().GetResult(); FailedConnectionEvidence(check,temp).GetAwaiter().GetResult(); ReleaseTitleVersions(check,temp).GetAwaiter().GetResult(); NommImages(check,temp).GetAwaiter().GetResult(); StalledBodies(check,temp).GetAwaiter().GetResult(); }
        finally { Directory.Delete(temp,true); }
        check(server.MainModule.GetType("KellysJOINCHECK.ModUpdateService")==null&&server.MainModule.GetType("KellysJOINCHECK.ModUpdateFiles")==null,"server has no updater or download work");
        check(!client.MainModule.GetType("KellysJOINCHECK.ModUpdateService").Methods.Any(m=>m.Name=="Update"||m.Name=="Tick"),"update checks have no background polling loop");
        var update=client.MainModule.GetType("KellysJOINCHECK.ModManager").Methods.Single(m=>m.Name=="UpdateMod");
        check(update.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="MenuSafety"),"downloads are offered only from a safe game menu");
        var exit=client.MainModule.GetType("KellysJOINCHECK.ModManager").Methods.Single(m=>m.Name=="ExitForUpdate");
        check(exit.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="MenuSafety"),"explicit exit prompt never quits an active server or mission");
        using(var startup=AssemblyDefinition.ReadAssembly(startupPath))
        {
            var finish=startup.MainModule.GetType("DoormanStartupPatcher").Methods.Single(m=>m.Name=="Finish");
            check(finish.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="ApplyQueues"),"updates install before Chainloader plugin discovery");
            check(!startup.MainModule.AssemblyReferences.Any(a=>a.Name=="System.Net.Http"),"startup installation cannot make network requests");
        }
    }
    private static void GameplayPackages(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"gameplay-package"),plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),queue=Path.Combine(cache,"DOORMAN-updates"),download=Path.Combine(root,"downloads");
        Directory.CreateDirectory(plugins);Directory.CreateDirectory(download);
        string dll=Path.Combine(root,"source.dll"),zip=Path.Combine(root,"release.zip"),extracted=Path.Combine(download,"plugin.bin");Fixture(dll,"test.gamepackage","1.0");
        byte[] image=Png(32,16);Archive(zip,new[]{("Aircraft/plugin.dll",File.ReadAllBytes(dll)),("Aircraft/jet.nobp",Encoding.UTF8.GetBytes("content one")),("Aircraft/Textures/jet.png",image),("Aircraft/README.md",Encoding.UTF8.GetBytes("read me"))});
        var files=ModUpdateService.ExtractGameplayPackage(zip,extracted,download,CancellationToken.None);
        check(files.Length==2&&files.Any(f=>f.Relative=="jet.nobp")&&files.Any(f=>f.Relative=="Textures/jet.png"),"gameplay ZIP preserves the bundle and relative texture layout");
        var bundle=files.Single(f=>f.Relative.EndsWith(".nobp"));bundle.ContentId="test.content";bundle.ContentVersion="1.0";
        string archiveHash=ModUpdateFiles.Hash(zip);
        string networkPlugins=Path.Combine(root,"network-plugins"),networkCache=Path.Combine(root,"network-cache");Directory.CreateDirectory(networkPlugins);
        var networkRecord=new NommRecord{Id="owner/repo",Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact{Version="1.0",FileName="release.zip",DownloadUrl="https://github.com/owner/repo/releases/download/1.0/release.zip",Hash="sha256:"+archiveHash}}};
        using(var service=new ModUpdateService(networkPlugins,networkCache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(File.ReadAllBytes(zip))})))
        using(var networkPayload=service.DownloadServerCandidateAsync(new ServerDownloadCandidate{Record=networkRecord,Version="1.0"},CancellationToken.None).GetAwaiter().GetResult())
        {
            var networkBundle=networkPayload.PackageFiles.Single(f=>f.Relative.EndsWith(".nobp"));networkBundle.ContentId="test.content";networkBundle.ContentVersion="1.0";
            service.StageServerPlugin(networkPayload,Array.Empty<string>(),CancellationToken.None);
            var networkPlan=ModUpdateFiles.ReadPending(Path.Combine(networkCache,"DOORMAN-updates"),networkPlugins).Single();
            check(networkPlan.Package&&networkPlan.PackageSourceHash==archiveHash&&networkPlan.PackageRecordId=="owner/repo","a checksummed GitHub ZIP flows through download and stages the entire package");
        }
        ModUpdateFiles.StageGameplayPackage(queue,plugins,extracted,"test.gamepackage","owner/repo","1.0","",archiveHash,"test.registry","plugin.dll",files,CancellationToken.None);
        var pending=ModUpdateFiles.ReadPending(queue,plugins).Single();ModUpdateFiles.VerifyPending(new[]{pending},plugins);
        check(pending.Package&&pending.Install&&!File.Exists(Path.Combine(plugins,pending.Target)),"gameplay package queues every file without changing installed mods");
        var record=new NommRecord{Id="test.registry",Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact{Version="1.0",FileName="release.zip",Hash="sha256:"+archiveHash}}};
        var candidate=new ServerDownloadCandidate{Record=record,Version="1.0"};
        check(ServerDownloadResolver.AlreadyQueued(candidate,new[]{pending},Array.Empty<StandaloneBundlePlan>(),plugins),"a previously queued gameplay ZIP is recognized by its catalogue ID and archive checksum");
        string[] stagedIds=Directory.GetFiles(pending.Job,"package-*.bin");string stagedBundle=Path.Combine(pending.Job,pending.PackageFiles!.Single(f=>f.Relative=="jet.nobp").Payload);
        File.AppendAllText(stagedBundle,"tampered");Throws(check,()=>ModUpdateFiles.VerifyPending(new[]{pending},plugins),"tampered queued package fails before restart");
        File.WriteAllBytes(stagedBundle,File.ReadAllBytes(bundle.Path));
        StartupUpdatePreflight.ApplyQueues(queue,Path.Combine(cache,"DOORMAN-content-updates"),plugins,Path.Combine(root,"config"),_=>{});
        string target=Path.Combine(plugins,pending.Target),folder=Path.GetDirectoryName(target)!;
        check(File.Exists(target)&&File.ReadAllText(Path.Combine(folder,"jet.nobp"))=="content one"&&File.Exists(Path.Combine(folder,"Textures","jet.png")),"startup installs the DLL, bundle and texture in the original relative layout");
        check(ServerDownloadResolver.InstalledPackage(candidate,queue,plugins)!=null,"a completed gameplay package is recognized without re-downloading its ZIP");
        File.AppendAllText(Path.Combine(folder,"Textures","jet.png"),"changed");check(ServerDownloadResolver.InstalledPackage(candidate,queue,plugins)==null,"a changed companion prevents an installed package from being treated as the checked archive");File.WriteAllBytes(Path.Combine(folder,"Textures","jet.png"),image);
        Throws(check,()=>ModUpdateFiles.Stage(queue,plugins,target,extracted,"test.gamepackage","owner/repo","1.0"),"a managed package cannot be updated as a DLL alone");
        Throws(check,()=>StandaloneBundleUpdates.Stage(Path.Combine(cache,"content"),plugins,bundle.Path,Path.Combine(folder,"jet.nobp"),"test.content","1.0",bundle.Hash),"a managed package bundle cannot be updated independently of its plugin");
        string next=Path.Combine(root,"next.dll");Fixture(next,"test.gamepackage","2.0");string nextContent=Path.Combine(root,"next.nobp");File.WriteAllText(nextContent,"content two");
        var replacement=new[]{new StagedPackageFile{Relative="new-jet.nobp",Path=nextContent,Hash=ModUpdateFiles.Hash(nextContent),ContentId="test.content",ContentVersion="2.0"}};
        string unrelatedTemp=Path.Combine(folder,"new-jet.nobp.doorman-new");File.WriteAllText(unrelatedTemp,"user file");
        ModUpdateFiles.StageGameplayPackage(queue,plugins,next,"test.gamepackage","owner/repo","2.0",target,new string('a',64),"test.registry","plugin.dll",replacement,CancellationToken.None);
        var update=ModUpdateFiles.ReadPending(queue,plugins).Single();
        check(File.Exists(Path.Combine(folder,"jet.nobp"))&&File.Exists(Path.Combine(folder,"Textures","jet.png")),"updating keeps old files intact until startup");
        ModUpdateFiles.ApplyPending(queue,plugins,_=>{});
        check(!File.Exists(Path.Combine(folder,"jet.nobp"))&&!File.Exists(Path.Combine(folder,"Textures","jet.png"))&&File.ReadAllText(Path.Combine(folder,"new-jet.nobp"))=="content two"&&File.Exists(Path.Combine(update.Job,"previous.bin")),"package update removes only previously managed files and retains rollback copies");
        check(File.ReadAllText(unrelatedTemp)=="user file","package replacement never overwrites a pre-existing temporary-looking file");
        File.AppendAllText(Path.Combine(folder,"new-jet.nobp"),"user change");
        Throws(check,()=>ModUpdateFiles.StageGameplayPackage(queue,plugins,next,"test.gamepackage","owner/repo","2.0",target,new string('b',64),"test.registry","plugin.dll",replacement,CancellationToken.None),"edited managed content is never silently overwritten");
        string unrelated=Path.Combine(plugins,"external.dll");Fixture(unrelated,"test.external","1.0");
        Throws(check,()=>ModUpdateFiles.StageGameplayPackage(queue,plugins,unrelated,"test.external","owner/repo","1.0",unrelated,new string('c',64),"external","external.dll",replacement,CancellationToken.None),"an unrelated existing plugin is never assumed to be a managed package");
        string bad=Path.Combine(root,"bad.zip");Archive(bad,new[]{("../bad.dll",File.ReadAllBytes(dll)),("jet.nobp",Encoding.UTF8.GetBytes("x"))});
        Throws(check,()=>ModUpdateService.ExtractGameplayPackage(bad,Path.Combine(download,"bad.bin"),download,CancellationToken.None),"gameplay ZIP rejects traversal before extraction");
        string windows=Path.Combine(root,"windows.zip");Archive(windows,new[]{("Mod\\plugin.dll",File.ReadAllBytes(dll)),("Mod\\jet.nobp",Encoding.UTF8.GetBytes("x")),("Mod\\Textures\\jet.png",image)});
        var windowsFiles=ModUpdateService.ExtractGameplayPackage(windows,Path.Combine(download,"windows.bin"),download,CancellationToken.None);
        check(windowsFiles.Any(f=>f.Relative=="Textures/jet.png"),"Windows archive separators are normalized while retaining relative texture paths");
        string backslashTraversal=Path.Combine(root,"backslash-traversal.zip");Archive(backslashTraversal,new[]{("Mod\\..\\plugin.dll",File.ReadAllBytes(dll)),("Mod\\jet.nobp",Encoding.UTF8.GetBytes("x"))});
        Throws(check,()=>ModUpdateService.ExtractGameplayPackage(backslashTraversal,Path.Combine(download,"backslash-bad.bin"),download,CancellationToken.None),"normalized Windows archive traversal is rejected");
        string script=Path.Combine(root,"script.zip");Archive(script,new[]{("plugin.dll",File.ReadAllBytes(dll)),("jet.nobp",Encoding.UTF8.GetBytes("x")),("install.cmd",Encoding.UTF8.GetBytes("x"))});
        Throws(check,()=>ModUpdateService.ExtractGameplayPackage(script,Path.Combine(download,"script.bin"),download,CancellationToken.None),"gameplay ZIP rejects extra executables and scripts");
        string collision=Path.Combine(root,"collision.zip");Archive(collision,new[]{("plugin.dll",File.ReadAllBytes(dll)),("jet.nobp",Encoding.UTF8.GetBytes("x")),("JET.NOBP",Encoding.UTF8.GetBytes("x"))});
        Throws(check,()=>ModUpdateService.ExtractGameplayPackage(collision,Path.Combine(download,"collision.bin"),download,CancellationToken.None),"gameplay ZIP rejects case-colliding files");
        string failureRoot=Path.Combine(root,"failure"),failurePlugins=Path.Combine(failureRoot,"plugins"),failureQueue=Path.Combine(failureRoot,"queue");Directory.CreateDirectory(failurePlugins);
        string failDll=Path.Combine(root,"failure.dll");Fixture(failDll,"test.rollback","1.0");
        var first=new[]{files.Single(f=>f.Relative=="Textures/jet.png"),bundle};
        ModUpdateFiles.StageGameplayPackage(failureQueue,failurePlugins,failDll,"test.rollback","owner/repo","1.0","",archiveHash,"rollback","failure.dll",first,CancellationToken.None);
        var failing=ModUpdateFiles.ReadPending(failureQueue,failurePlugins).Single();string failFolder=Path.GetDirectoryName(Path.Combine(failurePlugins,failing.Target))!;Directory.CreateDirectory(Path.Combine(failFolder,"jet.nobp"));
        ModUpdateFiles.ApplyPending(failureQueue,failurePlugins,_=>{});
        check(!File.Exists(Path.Combine(failFolder,"Textures","jet.png"))&&!File.Exists(Path.Combine(failurePlugins,failing.Target))&&UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(failing.Job,"plan.json")).State=="failed","a failure after the first package file rolls the entire install back");
        string interruptedRoot=Path.Combine(root,"interrupted"),interruptedPlugins=Path.Combine(interruptedRoot,"plugins"),interruptedQueue=Path.Combine(interruptedRoot,"queue");Directory.CreateDirectory(interruptedPlugins);
        string interruptedDll=Path.Combine(root,"interrupted.dll");Fixture(interruptedDll,"test.interruptedpackage","1.0");
        ModUpdateFiles.StageGameplayPackage(interruptedQueue,interruptedPlugins,interruptedDll,"test.interruptedpackage","owner/repo","1.0","",archiveHash,"interrupted","interrupted.dll",first,CancellationToken.None);
        var interrupted=ModUpdateFiles.ReadPending(interruptedQueue,interruptedPlugins).Single();var firstPart=interrupted.PackageFiles![0];string partialTarget=Path.Combine(interruptedPlugins,firstPart.Target(interrupted.Target));Directory.CreateDirectory(Path.GetDirectoryName(partialTarget)!);File.Copy(Path.Combine(interrupted.Job,firstPart.Payload),partialTarget);
        interrupted.State="applying";UpdateJson.Save(Path.Combine(interrupted.Job,"plan.json"),interrupted);
        ModUpdateFiles.ApplyPending(interruptedQueue,interruptedPlugins,_=>{});
        check(File.Exists(Path.Combine(interruptedPlugins,interrupted.Target))&&UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(interrupted.Job,"plan.json")).State=="complete","an interrupted package install resumes only after every staged file is verified");
        foreach(string real in (Environment.GetEnvironmentVariable("DOORMAN_REAL_GAMEPLAY_ZIPS")??"").Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries))
        {
            string place=Path.Combine(root,"real-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(place);
            var actual=ModUpdateService.ExtractGameplayPackage(real,Path.Combine(place,"dll.bin"),place,CancellationToken.None);
            string[] ids=ModUpdateFiles.PluginIds(Path.Combine(place,"dll.bin"));
            check(ids.Length==1&&actual.Count(f=>f.Relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))==1,"real catalogue package extracts one plugin and its bundle: "+Path.GetFileName(real));
            Console.WriteLine("Real gameplay ZIP: "+Path.GetFileName(real)+"; plugin="+string.Join(",",ids)+"; companion files="+actual.Length);
            var known=new Dictionary<string,(string Id,string Version)>{["baanish.centurion"]=("centurion","0.1.3"),["blacknight2u.f117a.nighthawk"]=("blacknight2u.f117a.nighthawk.nobp","0.5.0"),["com.mursisru.x77warewind"]=("x77warewind.nobp","0.0.0"),["iornman.mig29.instruments"]=("mig29","0.9.3.1"),["iornman.mig29smt.instruments"]=("mig29smt","0.2.1.1")};
            var identity=known[ids.Single()];var realBundle=actual.Single(f=>f.Relative.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase));realBundle.ContentId=identity.Id;realBundle.ContentVersion=identity.Version;
            string realDllName;using(var stream=File.OpenRead(real))using(var archive=new ZipArchive(stream,ZipArchiveMode.Read))realDllName=Path.GetFileName(archive.Entries.Single(e=>e.FullName.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)).FullName.Replace('\\','/'));
            string realPlugins=Path.Combine(place,"plugins"),realQueue=Path.Combine(place,"queue");Directory.CreateDirectory(realPlugins);
            ModUpdateFiles.StageGameplayPackage(realQueue,realPlugins,Path.Combine(place,"dll.bin"),ids[0],"owner/repo","1.0","",ModUpdateFiles.Hash(real),ids[0],realDllName,actual,CancellationToken.None);
            var realPlan=ModUpdateFiles.ReadPending(realQueue,realPlugins).Single();ModUpdateFiles.VerifyPending(new[]{realPlan},realPlugins);ModUpdateFiles.ApplyPending(realQueue,realPlugins,_=>{});
            check(Path.GetFileName(realPlan.Target)==realDllName&&File.Exists(Path.Combine(realPlugins,realPlan.Target))&&realPlan.PackageFiles!.All(f=>ModUpdateFiles.Hash(Path.Combine(realPlugins,f.Target(realPlan.Target)))==f.NewHash),"real package preserves its DLL name and installs all checked bytes into an isolated plugin folder: "+ids[0]);
            Directory.Delete(place,true);
        }
    }
    private static async Task InstallLinkNetwork(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"github-install-link"),plugins=Path.Combine(root,"plugins");Directory.CreateDirectory(plugins);
        const string json="{\"tag_name\":\"1.11\",\"html_url\":\"https://github.com/owner/repo/releases/tag/1.11\",\"prerelease\":true,\"assets\":[{\"name\":\"aircraft.nobp\",\"state\":\"uploaded\",\"size\":123,\"browser_download_url\":\"https://github.com/owner/repo/releases/download/1.11/aircraft.nobp\"}]}";
        string requested="";
        using(var service=new ModUpdateService(plugins,root,new FakeHandler(request=>{requested=request.RequestUri!.AbsoluteUri;return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)};})))
        {
            var result=await service.CheckInstallLinkAsync(GitHubInstallLink.Parse("https://github.com/owner/repo/releases/tag/1.11"),CancellationToken.None);
            check(requested=="https://api.github.com/repos/owner/repo/releases/tags/1.11"&&result.Release?.Tag=="1.11"&&result.Asset?.Name=="aircraft.nobp","a specific release link queries the exact GitHub tag and can select a standalone bundle");
            var named=await service.CheckInstallLinkAsync(GitHubInstallLink.Parse("https://github.com/owner/repo/releases/download/1.11/aircraft.nobp"),CancellationToken.None);
            check(named.Asset?.Name=="aircraft.nobp","a specific release asset remains selected without guessing");
            try {await service.CheckInstallLinkAsync(GitHubInstallLink.Parse("https://github.com/owner/repo/releases/tag/1.12"),CancellationToken.None);check(false,"wrong tag");}catch(InvalidDataException){check(true,"GitHub response with a different tag is refused");}
        }
    }
    private static async Task FailedConnectionEvidence(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"github-failed-connection"),plugins=Path.Combine(root,"plugins");Directory.CreateDirectory(plugins);
        string stage="",evidence="";int calls=0;
        using(var service=new ModUpdateService(plugins,root,new FakeHandler(_=>throw new HttpRequestException("secret signed URL",new IOException("private path")))))
        {
            service.DiagnosticFailure=(s,e)=>{stage=s;evidence=RestartLaunchFailure.Evidence(e);calls++;};
            var result=await service.CheckAsync("owner/repo","","","",CancellationToken.None);
            check(result.Release==null&&calls==1&&stage=="release check"&&evidence.Contains("HttpRequestException")&&evidence.Contains("IOException"),"failed GitHub connection records the bounded exception chain for support");
            check(!evidence.Contains("secret")&&!evidence.Contains("private"),"connection diagnostics do not copy exception messages, paths or signed URLs");
        }
        using(var service=new ModUpdateService(plugins,Path.Combine(root,"observer"),new FakeHandler(_=>throw new HttpRequestException("failed"))))
        {
            service.DiagnosticFailure=(s,e)=>throw new InvalidOperationException("diagnostics failed");
            var result=await service.CheckAsync("owner/repo","","","",CancellationToken.None);
            check(result.Release==null&&result.Message.Contains("Could not check GitHub"),"a broken support observer cannot turn a connection failure into a crash");
        }
    }
    private static async Task ReleaseTitleVersions(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"release-title-version"),plugins=Path.Combine(root,"plugins");Directory.CreateDirectory(plugins);
        string Reply(string title)=>"{\"tag_name\":\"1.13\",\"name\":\""+title+"\",\"html_url\":\"https://github.com/owner/repo/releases/tag/1.13\",\"assets\":[]}";
        int requests=0;
        // A cache from an older build has no release title. It must not keep
        // generating false warnings until its six-hour lifetime expires.
        System.IO.File.WriteAllText(Path.Combine(root,"doorman-releases.json"),"{\"Entries\":[{\"Repository\":\"owner/repo\",\"ETag\":\"\",\"Error\":\"\",\"Expires\":"+DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()+",\"Release\":{\"tag_name\":\"1.13\",\"html_url\":\"https://github.com/owner/repo/releases/tag/1.13\",\"assets\":[]}}]}");
        using(var service=new ModUpdateService(plugins,root,new FakeHandler(_=>{requests++;return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Reply("1.1.3"))};})))
        {
            var result=await service.CheckAsync("owner/repo","","1.1.3","",CancellationToken.None);
            check(requests==1&&!result.Newer&&result.Message.Contains("Up to date")&&result.Release?.Tag=="1.13","a numeric GitHub release title avoids the MiG-15 tag false positive and retains the original download tag");
            var newer=await service.CheckAsync("owner/repo","","1.1.2","",CancellationToken.None);
            check(newer.Newer&&newer.Message.Contains("1.1.3")&&requests==1,"a genuine newer named release still appears and uses the shared cache");
        }
        using(var service=new ModUpdateService(plugins,root,new FakeHandler(_=>throw new InvalidOperationException("The valid disk cache should be used."))))
        {
            var cached=await service.CheckAsync("owner/repo","","1.1.3","",CancellationToken.None);
            check(!cached.Newer&&cached.DisplayVersion=="1.1.3","the cached release title remains available after restarting the service");
        }
        using(var service=new ModUpdateService(plugins,Path.Combine(root,"fallback"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(Reply("Patch notes"))})))
        {
            var result=await service.CheckAsync("owner/repo","","1.1.3","",CancellationToken.None);
            check(result.Newer&&result.DisplayVersion=="1.13","a nonnumeric title falls back to the existing numeric tag comparison");
        }
    }
    private static async Task ReportedDownloads(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"nosmr-download"),plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),payload=Path.Combine(root,"mod.dll");
        Directory.CreateDirectory(plugins);Fixture(payload,"tests.reported","1.0");byte[] data=File.ReadAllBytes(payload);
        var record=new NommRecord {Id="registry.mod",Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact {Version="1.2.3",FileName="mod.dll",DownloadUrl="https://github.com/owner/repo/releases/download/1.2.3/mod.dll",Hash="sha256:"+ModUpdateFiles.Hash(payload)}}};
        using(var service=new ModUpdateService(plugins,cache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data)})))
        {
            string plugin=await service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None);
            var plans=ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins);
            check(plugin=="tests.reported"&&plans.Length==1&&plans[0].Plugin==plugin&&!Directory.EnumerateFiles(plugins,"*.dll").Any(),"NOSMR download checks the exact registry hash and queues the verified DLL without modifying live plugins");
            ModUpdateFiles.ApplyPending(Path.Combine(cache,"DOORMAN-updates"),plugins,_=>{});
            check(File.Exists(Path.Combine(plugins,plans[0].Target))&&ModUpdateFiles.Hash(Path.Combine(plugins,plans[0].Target))==ModUpdateFiles.Hash(payload),"the existing startup queue installs the exact reported release");
        }
        string blocked=Path.Combine(root,"blocked");Directory.CreateDirectory(blocked);
        using(var service=new ModUpdateService(blocked,Path.Combine(root,"disabled-cache"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data)})))
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",new[]{"tests.reported"},CancellationToken.None).GetAwaiter().GetResult(),"a NOMM-disabled copy prevents a duplicate installation");
        string zip=Path.Combine(root,"with-preview.zip");byte[] picture=Png(32,16),card=Encoding.UTF8.GetBytes("{\"plugin\":\"tests.reported\",\"name\":\"Example\",\"image\":\"image.png\"}");
        Archive(zip,new[]{("mod.dll",data),("mod.doorman.json",card),("image.png",picture)});
        var zippedRecord=new NommRecord {Id="registry.mod",Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact {Version="1.2.3",FileName="mod.zip",DownloadUrl="https://github.com/owner/repo/releases/download/1.2.3/mod.zip",Hash="sha256:"+ModUpdateFiles.Hash(zip)}}};
        string previewCache=Path.Combine(root,"preview-cache");
        using(var service=new ModUpdateService(blocked,previewCache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(File.ReadAllBytes(zip))})))
        {
            await service.StageReportedAsync(zippedRecord,"1.2.3",Array.Empty<string>(),CancellationToken.None);
            var staged=ModUpdateFiles.ReadPending(Path.Combine(previewCache,"DOORMAN-updates"),blocked);
            check(staged.Length==1&&staged[0].Sidecars?.Length==2,"standalone GitHub ZIP install queues its verified preview and image with its DLL");
        }
        using(var service=new ModUpdateService(blocked,Path.Combine(root,"bad-cache"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data)})))
        {
            record.Artifacts[0].Hash="sha256:"+new string('0',64);
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None).GetAwaiter().GetResult(),"a changed or corrupt NOSMR release is rejected before queuing");
            record.Artifacts[0].Hash="sha256:"+ModUpdateFiles.Hash(payload);
            Throws(check,()=>service.StageReportedAsync(record,"1.2.4",Array.Empty<string>(),CancellationToken.None).GetAwaiter().GetResult(),"a different version is never substituted for the host's version");
            record.Artifacts[0].FileName="mod.nobp";record.Artifacts[0].DownloadUrl="https://github.com/owner/repo/releases/download/1.2.3/mod.nobp";
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None).GetAwaiter().GetResult(),"unsupported content bundles use the manual GitHub option");
            string bundle=await service.DownloadReportedBundleAsync(record,"1.2.3",CancellationToken.None);
            check(File.Exists(bundle)&&ModUpdateFiles.Hash(bundle)==ModUpdateFiles.Hash(payload)&&!Directory.EnumerateFiles(blocked).Any(),"a standalone reported bundle downloads to a verified temporary file before Unity checks it");
            File.Delete(bundle);
            record.Artifacts[0].Hash="sha256:"+new string('0',64);
            Throws(check,()=>service.DownloadReportedBundleAsync(record,"1.2.3",CancellationToken.None).GetAwaiter().GetResult(),"a corrupt bundle is rejected before it reaches the plugin folder");
        }
    }
    private static async Task ReportedUpdate(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"nosmr-existing-update"),plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),old=Path.Combine(plugins,"Installed","old.dll"),fresh=Path.Combine(root,"fresh.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(old)!);Fixture(old,"tests.reported","1.0");Fixture(fresh,"tests.reported","1.1");
        byte[] oldBytes=File.ReadAllBytes(old),data=File.ReadAllBytes(fresh);string oldHash=ModUpdateFiles.Hash(old);
        var record=new NommRecord {Id="registry.mod",Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact {Version="1.2.3",FileName="mod.dll",DownloadUrl="https://github.com/owner/repo/releases/download/1.2.3/mod.dll",Hash="sha256:"+ModUpdateFiles.Hash(fresh)}}};
        using(var service=new ModUpdateService(plugins,cache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(data)})))
        {
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None,"",old).GetAwaiter().GetResult(),"a reported update cannot infer the identity to overwrite from a path");
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",new[]{"tests.reported"},CancellationToken.None,"tests.reported",old).GetAwaiter().GetResult(),"a NOMM-disabled duplicate blocks an existing reported update");
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None,"tests.other",old).GetAwaiter().GetResult(),"a downloaded plugin cannot replace a different requested identity");
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None,"tests.reported",fresh).GetAwaiter().GetResult(),"an existing update cannot target a DLL outside plugins");
            string duplicate=Path.Combine(plugins,"duplicate.dll");File.Copy(old,duplicate);
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None,"tests.reported",old).GetAwaiter().GetResult(),"a duplicate installed plugin blocks an ambiguous reported update");File.Delete(duplicate);
            await service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None,"tests.reported",old);
            var pending=ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins).Single();
            check(!pending.Install&&pending.Target==Path.Combine("Installed","old.dll")&&pending.OldHash==oldHash&&ModUpdateFiles.Hash(old)==oldHash,"an exact reported update queues the known old path and bytes without changing the installed DLL");
            Throws(check,()=>service.StageReportedAsync(record,"1.2.3",Array.Empty<string>(),CancellationToken.None,"tests.reported",old).GetAwaiter().GetResult(),"the same reported plugin cannot be queued twice");
            File.AppendAllText(old,"changed");Throws(check,()=>ModUpdateFiles.VerifyPending(new[]{pending},plugins),"a changed installed DLL invalidates a queued reported update");File.WriteAllBytes(old,oldBytes);
            ModUpdateFiles.ApplyPending(Path.Combine(cache,"DOORMAN-updates"),plugins,_=>{});
            check(ModUpdateFiles.Hash(old)==ModUpdateFiles.Hash(fresh)&&ModUpdateFiles.Hash(Path.Combine(pending.Job,"previous.bin"))==oldHash,"startup replaces the exact old path and retains its rollback bytes");
        }
    }
    private static async Task ServerCandidateDownload(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"server-candidate"),plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),old=Path.Combine(plugins,"old.dll"),source=Path.Combine(root,"new.dll");
        Directory.CreateDirectory(plugins);Fixture(old,"tests.servercandidate","1.0");Fixture(source,"tests.servercandidate","1.1");string oldHash=ModUpdateFiles.Hash(old);byte[] bytes=File.ReadAllBytes(source);
        var record=new NommRecord {Id="tests.servercandidate",Owner="owner",Repository="repo",Artifacts=new[]{new NommArtifact {Version="1.1",FileName="mod.dll",DownloadUrl="https://github.com/owner/repo/releases/download/1.1/mod.dll",Hash="sha256:"+ModUpdateFiles.Hash(source)}}};
        var candidate=new ServerDownloadCandidate {ContentId="bundle",Version="1.1",Record=record,OwnerPlugin="tests.servercandidate",Target=old};
        using(var service=new ModUpdateService(plugins,cache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})))
        {
            string checkedPath;
            var progress=new ServerDownloadProgress();
            check(progress.Fraction<0,"download progress is indeterminate until a content length is known");
            using(var payload=await service.DownloadServerCandidateAsync(candidate,CancellationToken.None,progress))
            {
                check(progress.Fraction==1f,"server download progress reaches 100 percent after all response bytes arrive");
                checkedPath=payload.PayloadPath;
                check(File.Exists(checkedPath)&&payload.Hash==ModUpdateFiles.Hash(source)&&payload.Plugin=="tests.servercandidate"&&ModUpdateFiles.Hash(old)==oldHash&&ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins).Length==0,"server candidate download is verified in temporary cache without touching live DLLs or queuing changes");
                Throws(check,()=>service.StageServerPlugin(payload,new[]{"tests.servercandidate"},CancellationToken.None),"a NOMM-disabled plugin blocks server candidate staging");
                service.StageServerPlugin(payload,Array.Empty<string>(),CancellationToken.None);
                check(ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins).Length==1&&ModUpdateFiles.Hash(old)==oldHash,"only explicit server candidate staging queues the exact installed DLL and keeps it unchanged");
            }
            check(!File.Exists(checkedPath),"disposing server candidate removes its temporary download");
        }
        string other=Path.Combine(root,"other-cache");
        using(var service=new ModUpdateService(plugins,other,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})))
        {
            record.Artifacts[0].Hash="sha256:"+new string('0',64);
            Throws(check,()=>service.DownloadServerCandidateAsync(candidate,CancellationToken.None).GetAwaiter().GetResult(),"a server candidate with an incorrect registry digest is discarded");
            record.Artifacts[0].Hash="sha256:"+ModUpdateFiles.Hash(source);candidate.OwnerPlugin="tests.wrong";
            Throws(check,()=>service.DownloadServerCandidateAsync(candidate,CancellationToken.None).GetAwaiter().GetResult(),"a server candidate with a different plugin identity is discarded");
        }
    }
    private static void PreviewFiles(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"previews"),plugins=Path.Combine(root,"plugins"),old=Path.Combine(plugins,"Old_1.0.dll"),fresh=Path.Combine(root,"fresh.dll"),zip=Path.Combine(root,"preview.zip"),payload=Path.Combine(root,"extracted.bin"),queue=Path.Combine(root,"queue");Directory.CreateDirectory(plugins);
        Fixture(old,"tests.mod","1.0");Fixture(fresh,"tests.mod","1.1");byte[] image=Png(32,16),oldImage=Png(16,16);
        byte[] card=Encoding.UTF8.GetBytes("{\"plugin\":\"tests.mod\",\"name\":\"New card\",\"description\":\"New description\",\"image\":\"art/preview.png\",\"links\":[{\"label\":\"GitHub\",\"url\":\"https://github.com/owner/repo\"}]}");
        string managed=Path.Combine(plugins,ModUpdateFiles.MetadataDirectory("tests.mod")),manifest=Path.Combine(managed,"preview.doorman.json"),picture=Path.Combine(managed,"image.png");Directory.CreateDirectory(managed);
        string oldCard="{\"plugin\":\"tests.mod\",\"name\":\"Old card\",\"image\":\"image.png\"}";File.WriteAllText(manifest,oldCard);File.WriteAllBytes(picture,oldImage);string oldDllHash=ModUpdateFiles.Hash(old),oldCardHash=ModUpdateFiles.Hash(manifest),oldPictureHash=ModUpdateFiles.Hash(picture);
        Archive(zip,new[]{("BepInEx/plugins/Mod/NewName.dll",File.ReadAllBytes(fresh)),("BepInEx/plugins/Mod/card.doorman.json",card),("BepInEx/plugins/Mod/art/preview.png",image),("README.md",Encoding.UTF8.GetBytes("ignored"))});
        var previews=ModUpdateService.ExtractSingleDll(zip,payload,"tests.mod");
        var discovered=ModUpdateService.ExtractSingleDll(zip,Path.Combine(root,"discovered.bin"));
        check(discovered.Length==2,"standalone GitHub installs discover plugin identity and retain its matching preview");
        var parsed=ModJson.ReadBytes<ModManifest>(previews.Single(p=>p.Name=="preview.doorman.json").Data);
        check(previews.Length==2&&parsed.Plugin=="tests.mod"&&parsed.Image=="image.png"&&parsed.Description=="New description","nested single-DLL ZIP binds one plugin preview and normalizes only its referenced image");
        check(!File.Exists(Path.Combine(plugins,"not-installed.cfg"))&&Directory.GetFiles(root).Length==4,"archive read extracts only the two requested DLL payloads");
        check(ModUpdateFiles.MetadataDirectory("tests.mod")==Path.Combine("DOORMAN-Metadata","30265220020830c7cd855b965b28ff2bd02bd4da78222b3ba270695237d1fc68"),"managed preview folder is stable lowercase SHA256 of the exact plugin ID");
        ModUpdateFiles.Stage(queue,plugins,old,payload,"tests.mod","owner/repo","1.1",previews);
        var pending=ModUpdateFiles.ReadPending(queue,plugins).Single();ModUpdateFiles.VerifyPending(new[]{pending},plugins);
        check(pending.Schema==2&&pending.Sidecars!.Length==2&&pending.Sidecars.Single(f=>f.Name=="preview.doorman.json").OldHash==oldCardHash,"queue receipt binds old and new preview bytes before any replacement");
        check(ModUpdateFiles.Hash(old)==oldDllHash&&ModUpdateFiles.Hash(manifest)==oldCardHash&&ModUpdateFiles.Hash(picture)==oldPictureHash,"staging a preview update leaves all installed files intact");
        File.AppendAllText(manifest," ");Throws(check,()=>ModUpdateFiles.VerifyPending(new[]{pending},plugins),"restart preparation rejects a preview changed after download");File.WriteAllText(manifest,oldCard);
        string imagePayload=Path.Combine(pending.Job,"image.payload.bin");File.AppendAllText(imagePayload,"tampered");Throws(check,()=>ModUpdateFiles.VerifyPending(new[]{pending},plugins),"restart preparation rejects corrupt queued artwork");File.WriteAllBytes(imagePayload,image);
        var messages=new List<string>();ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);
        check(ModUpdateFiles.Hash(old)==ModUpdateFiles.Hash(fresh)&&File.ReadAllBytes(picture).SequenceEqual(image)&&ModJson.Read<ModManifest>(manifest).Name=="New card","startup installs DLL and its exact validated preview/image as one recorded transaction");
        check(ModUpdateFiles.Hash(Path.Combine(pending.Job,"previous.bin"))==oldDllHash&&ModUpdateFiles.Hash(Path.Combine(pending.Job,"preview.previous.bin"))==oldCardHash&&ModUpdateFiles.Hash(Path.Combine(pending.Job,"image.previous.bin"))==oldPictureHash,"all original DLL, manifest and artwork backups remain outside plugins");
        check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(pending.Job,"plan.json")).State=="complete","a preview receipt completes only after every new hash matches");
        ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);check(messages.Count==1,"completed preview updates do not reinstall at later startup");
        // Simulate a crash which replaced the DLL but left the old preview. An
        // applying receipt may resume, but cannot call that partial state complete.
        Fixture(old,"tests.mod","1.0");File.WriteAllText(manifest,oldCard);File.WriteAllBytes(picture,oldImage);string interrupted=Path.Combine(root,"interrupted");ModUpdateFiles.Stage(interrupted,plugins,old,payload,"tests.mod","owner/repo","1.1",previews);
        var partial=ModUpdateFiles.ReadPending(interrupted,plugins).Single();File.Copy(old,Path.Combine(partial.Job,"previous.bin"));File.Copy(manifest,Path.Combine(partial.Job,"preview.previous.bin"));File.Copy(picture,Path.Combine(partial.Job,"image.previous.bin"));partial.State="applying";UpdateJson.Save(Path.Combine(partial.Job,"plan.json"),partial);File.Copy(payload,old,true);
        File.AppendAllText(Path.Combine(partial.Job,"image.payload.bin"),"tampered");ModUpdateFiles.ApplyPending(interrupted,plugins,messages.Add);
        check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(partial.Job,"plan.json")).State=="applying"&&ModUpdateFiles.Hash(manifest)==oldCardHash,"partial DLL-plus-preview state with corrupt payload is never blessed complete");
        File.WriteAllBytes(Path.Combine(partial.Job,"image.payload.bin"),image);ModUpdateFiles.VerifyPending(ModUpdateFiles.ReadPending(interrupted,plugins),plugins);ModUpdateFiles.ApplyPending(interrupted,plugins,messages.Add);
        check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(partial.Job,"plan.json")).State=="complete"&&ModJson.Read<ModManifest>(manifest).Name=="New card","interrupted preview transactions reconcile every verified old/new file before completion");
        partial.State="applying";UpdateJson.Save(Path.Combine(partial.Job,"plan.json"),partial);File.AppendAllText(Path.Combine(partial.Job,"preview.previous.bin"),"tampered");ModUpdateFiles.ApplyPending(interrupted,plugins,messages.Add);
        check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(partial.Job,"plan.json")).State=="applying","even fully replaced previews require their verified originals before crash recovery completes");
        // A later raw DLL can carry an embedded preview. Remove only the old
        // managed card, with backups, so it cannot mask the new DLL's resource.
        string next=Path.Combine(root,"next.dll"),rawQueue=Path.Combine(root,"raw-queue");Fixture(next,"tests.mod","1.2");string unrelated=Path.Combine(plugins,"custom.doorman.json");File.WriteAllText(unrelated,"custom");
        ModUpdateFiles.Stage(rawQueue,plugins,old,next,"tests.mod","owner/repo","1.2");var removal=ModUpdateFiles.ReadPending(rawQueue,plugins).Single();ModUpdateFiles.VerifyPending(new[]{removal},plugins);
        check(removal.Schema==2&&removal.Sidecars!.All(f=>f.Remove),"raw DLL updates queue removal of only the obsolete managed preview");
        ModUpdateFiles.ApplyPending(rawQueue,plugins,messages.Add);
        check(!File.Exists(manifest)&&!File.Exists(picture)&&File.ReadAllText(unrelated)=="custom"&&ModUpdateFiles.Hash(old)==ModUpdateFiles.Hash(next),"raw DLL update exposes embedded preview without deleting manual cards or other files");
        check(File.Exists(Path.Combine(removal.Job,"preview.previous.bin"))&&File.Exists(Path.Combine(removal.Job,"image.previous.bin")),"obsolete managed preview removal retains rollback copies");
        string flat=Path.Combine(root,"flat.zip"),flatPayload=Path.Combine(root,"flat.bin");Archive(flat,new[]{("new.dll",File.ReadAllBytes(next)),("new.doorman.json",Encoding.UTF8.GetBytes("{\"plugin\":\"tests.mod\",\"name\":\"Flat card\"}"))});
        check(ModUpdateService.ExtractSingleDll(flat,flatPayload,"tests.mod").Length==1,"flat ZIP plugin-only preview works without boilerplate image fields");
        void Reject(string label,(string Name,byte[] Data)[] entries)
        {string badZip=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");Archive(badZip,entries);Throws(check,()=>ModUpdateService.ExtractSingleDll(badZip,Path.Combine(root,Guid.NewGuid().ToString("N")+".bin"),"tests.mod"),label);}
        byte[] dll=File.ReadAllBytes(next),wrong=Encoding.UTF8.GetBytes("{\"plugin\":\"other.mod\",\"name\":\"Wrong\"}"),bundle=Encoding.UTF8.GetBytes("{\"plugin\":\"tests.mod\",\"bundle\":\"unrelated.bundle\",\"name\":\"Wrong\"}");
        Reject("unrelated preview plugin identity rejects the ZIP",new[]{("new.dll",dll),("card.doorman.json",wrong)});
        Reject("unrecognised companion asset requires manual install instead of being dropped",new[]{("new.dll",dll),("assets/sounds.bank",image)});
        Reject("configuration in a release package requires manual install instead of being dropped",new[]{("new.dll",dll),("BepInEx/config/mod.cfg",image)});
        Reject("mixed bundle targets need a manual installation",new[]{("new.dll",dll),("card.doorman.json",bundle)});
        Reject("multiple ZIP preview manifests are ambiguous",new[]{("new.dll",dll),("one.doorman.json",card),("two.doorman.json",card)});
        Reject("ZIP paths colliding by case are rejected",new[]{("new.dll",dll),("README.md",image),("readme.MD",image)});
        Reject("duplicate exact ZIP paths are rejected",new[]{("new.dll",dll),("note.txt",image),("note.txt",image)});
        Reject("ZIP file and parent-directory collisions are rejected",new[]{("new.dll",dll),("art",image),("art/image.png",image)});
        foreach(string unsafePath in new[]{"../new.dll","art//new.dll","folder./new.dll","folder /new.dll","CON/new.dll","art\\new.dll","new.dll:stream","new.dll/../other.dll"})Reject("unsafe ZIP paths including Windows aliases reject",new[]{(unsafePath,dll)});
        Reject("oversized ZIP preview JSON rejects",new[]{("new.dll",dll),("card.doorman.json",new byte[65537])});
        Reject("missing referenced artwork rejects",new[]{("new.dll",dll),("card.doorman.json",card)});
        Reject("oversized artwork rejects",new[]{("new.dll",dll),("card.doorman.json",card),("art/preview.png",new byte[ModUpdateFiles.MaximumImageBytes+1])});
        Reject("large pixel dimensions reject compressed artwork",new[]{("new.dll",dll),("card.doorman.json",card),("art/preview.png",Png(8192,8192))});
        Reject("image traversal in the manifest rejects",new[]{("new.dll",dll),("card.doorman.json",Encoding.UTF8.GetBytes("{\"plugin\":\"tests.mod\",\"name\":\"Bad\",\"image\":\"../preview.png\"}")),("preview.png",image)});
        string linked=Path.Combine(root,"linked.zip");Archive(linked,new[]{("new.dll",dll),("README.md",image)});using(var file=File.Open(linked,FileMode.Open,FileAccess.ReadWrite))using(var archive=new ZipArchive(file,ZipArchiveMode.Update))archive.GetEntry("README.md")!.ExternalAttributes=unchecked((int)0xA0000000);Throws(check,()=>ModUpdateService.ExtractSingleDll(linked,Path.Combine(root,"linked.bin"),"tests.mod"),"linked ZIP entries reject even when they are not selected for extraction");
    }
    private static byte[] Png(int width,int height)
    {var bytes=new byte[24];bytes[0]=137;bytes[1]=80;bytes[2]=78;bytes[3]=71;for(int i=0;i<4;i++){bytes[16+i]=(byte)(width>>(24-i*8));bytes[20+i]=(byte)(height>>(24-i*8));}return bytes;}
    private static void QueueCapacity(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"queue-capacity"),plugins=Path.Combine(root,"plugins"),queue=Path.Combine(root,"queue"),old=Path.Combine(plugins,"old.dll"),fresh=Path.Combine(root,"new.dll");Directory.CreateDirectory(plugins);
        Fixture(old,"tests.mod","1.0");Fixture(fresh,"tests.mod","1.1");string original=ModUpdateFiles.Hash(old),newHash=ModUpdateFiles.Hash(fresh);
        var history=new List<string>();
        for(int i=0;i<ModUpdateFiles.MaximumPendingJobs*2+9;i++)
        {
            string job=Path.Combine(queue,Guid.NewGuid().ToString("N"));history.Add(job);
            UpdateJson.Save(Path.Combine(job,"plan.json"),new PendingModUpdate { Plugin="tests.mod",Repository="owner/repo",Version="1.1",Target="old.dll",OldHash=original,NewHash=newHash,State=new[]{"complete","failed","cancelled"}[i%3] });
        }
        File.Copy(old,Path.Combine(history[0],"previous.bin"));
        check(ModUpdateFiles.ReadPending(queue,plugins).Length==0,"hundreds of terminal update receipts do not occupy pending capacity");
        ModUpdateFiles.Stage(queue,plugins,old,fresh,"tests.mod","owner/repo","1.1");
        check(ModUpdateFiles.ReadPending(queue,plugins).Length==1&&ModUpdateFiles.PendingFor(queue,"old.dll")!=null,"a new pending update remains visible beyond retained history's former folder limit");
        check(history.All(job=>File.Exists(Path.Combine(job,"plan.json")))&&ModUpdateFiles.Hash(Path.Combine(history[0],"previous.bin"))==original,"queueing after retained history preserves every receipt and the original rollback file");
        for(int i=1;i<ModUpdateFiles.MaximumPendingJobs;i++)
            UpdateJson.Save(Path.Combine(queue,Guid.NewGuid().ToString("N"),"plan.json"),new PendingModUpdate { Plugin="tests.queued."+i,Repository="owner/repo",Version="1.1",Target="queued-"+i+".dll",OldHash=original,NewHash=newHash,State=i%2==0?"applying":"pending" });
        check(ModUpdateFiles.ReadPending(queue,plugins).Length==ModUpdateFiles.MaximumPendingJobs,"both pending and interrupted applying receipts count toward active capacity");
        string second=Path.Combine(plugins,"second.dll"),secondFresh=Path.Combine(root,"second-new.dll");Fixture(second,"tests.second","1.0");Fixture(secondFresh,"tests.second","1.1");string secondOriginal=ModUpdateFiles.Hash(second);int jobs=Directory.EnumerateDirectories(queue).Count();
        Throws(check,()=>ModUpdateFiles.Stage(queue,plugins,second,secondFresh,"tests.second","owner/repo","1.1"),"an active queue at capacity refuses another update before creating hidden work");
        check(Directory.EnumerateDirectories(queue).Count()==jobs&&ModUpdateFiles.Hash(old)==original&&ModUpdateFiles.Hash(second)==secondOriginal,"active-capacity refusal creates no job and never changes installed files");
        UpdateJson.Save(Path.Combine(queue,Guid.NewGuid().ToString("N"),"plan.json"),new PendingModUpdate { Plugin="tests.mod",Repository="owner/repo",Version="1.1",Target="excess.dll",OldHash=original,NewHash=newHash });
        Throws(check,()=>ModUpdateFiles.ReadPending(queue,plugins),"restart preparation explicitly rejects an externally overfull active queue");
        Throws(check,()=>ModUpdateFiles.PendingFor(queue,"old.dll"),"details checks cannot report a truncated active queue as usable");
        Throws(check,()=>ModUpdateFiles.ApplyPending(queue,plugins,_=>{}),"startup refuses excess active jobs before applying any update");
        check(ModUpdateFiles.Hash(old)==original&&ModUpdateFiles.Hash(Path.Combine(history[0],"previous.bin"))==original,"active overflow leaves installed DLLs and retained backups intact");
    }
    private static void StartupQueueConflicts(Action<bool,string> check,string temp)
    {
        foreach(bool duplicateTarget in new[]{true,false})
        {
            string root=Path.Combine(temp,duplicateTarget?"duplicate-startup-target":"duplicate-startup-plugin"),plugins=Path.Combine(root,"plugins"),queue=Path.Combine(root,"queue"),old=Path.Combine(plugins,"old.dll"),other=Path.Combine(plugins,"other.dll"),fresh=Path.Combine(root,"fresh.dll");
            Directory.CreateDirectory(plugins);Fixture(old,"tests.mod","1.0");File.Copy(old,other);Fixture(fresh,"tests.mod","1.1");
            string original=ModUpdateFiles.Hash(old);ModUpdateFiles.Stage(queue,plugins,old,fresh,"tests.mod","owner/repo","1.1");
            var first=ModUpdateFiles.ReadPending(queue,plugins).Single();string second=Path.Combine(queue,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(second);File.Copy(fresh,Path.Combine(second,"payload.bin"));
            UpdateJson.Save(Path.Combine(second,"plan.json"),new PendingModUpdate {Plugin=duplicateTarget?"tests.other":"tests.mod",Repository="owner/repo",Version="1.1",PluginVersion="1.1",Target=duplicateTarget?"old.dll":"other.dll",OldHash=original,NewHash=ModUpdateFiles.Hash(fresh)});
            Throws(check,()=>ModUpdateFiles.ApplyPending(queue,plugins,_=>{}),duplicateTarget?"normal Steam startup rejects duplicate update targets before installation":"normal Steam startup rejects duplicate plugin IDs before installation");
            check(ModUpdateFiles.Hash(old)==original&&ModUpdateFiles.Hash(other)==original&&UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(first.Job,"plan.json")).State=="pending","a conflicting startup queue preserves all installed DLLs and pending receipts");
        }
    }
    private static void DanglingWriteSafety(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"dangling-write-safety"),receipt=Path.Combine(root,"plan.json"),external=Path.Combine(root,"outside","new-file.txt");Directory.CreateDirectory(root);Directory.CreateDirectory(Path.GetDirectoryName(external)!);bool linked=false;
        try
        {
            try {File.CreateSymbolicLink(receipt+".tmp",external);linked=true;}
            catch(Exception ex) when(ex is UnauthorizedAccessException||ex is IOException||ex is PlatformNotSupportedException)
            {Console.WriteLine("Dangling update symlink checks unavailable on this host: "+ex.GetType().Name);return;}
            Throws(check,()=>ModUpdateFiles.Within(root,"plan.json.tmp"),"update paths reject dangling symbolic links before a write can create their external target");
            Throws(check,()=>UpdateJson.Save(receipt,"replacement"),"JSON writes reject a dangling adjacent temporary link");
            check(!File.Exists(external)&&!File.Exists(receipt),"a rejected dangling update link creates no external file or receipt");
        }
        finally {if(linked)File.Delete(receipt+".tmp");}
    }
    private static void JsonWriteSafety(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"json-write-safety"),path=Path.Combine(root,"plan.json"),external=Path.Combine(root,"external.txt");Directory.CreateDirectory(root);
        File.WriteAllText(external,"Keep these original bytes.");bool linked=false;
        try
        {
            try { File.CreateSymbolicLink(path+".tmp",external);linked=true; }
            catch(Exception ex) when(ex is UnauthorizedAccessException||ex is IOException||ex is PlatformNotSupportedException)
            { Console.WriteLine("Update JSON symlink checks unavailable on this host: "+ex.GetType().Name);return; }
            Throws(check,()=>UpdateJson.Save(path,"replacement"),"JSON writes reject a linked adjacent temporary file before opening it");
            check(File.ReadAllText(external)=="Keep these original bytes."&&!File.Exists(path),"rejected linked JSON staging preserves external files and creates no receipt");
        }
        finally { if(linked)File.Delete(path+".tmp"); }
    }
    private static async Task NommImages(Action<bool,string> check,string temp)
    {
        string cache=Path.Combine(temp,"nomm-images"),url="https://raw.githubusercontent.com/owner/repo/main/card.png",imageRoot=Path.Combine(cache,"DOORMAN-previews");Directory.CreateDirectory(imageRoot);
        string key;using(var sha=System.Security.Cryptography.SHA256.Create())key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-","");
        string path=Path.Combine(imageRoot,key+".png");File.WriteAllBytes(path,new byte[]{137,80});byte[] good=Png(32,16);int calls=0;bool offline=false;
        var handler=new FakeHandler(_=>{calls++;return offline?new HttpResponseMessage(HttpStatusCode.ServiceUnavailable):new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(good)};});
        using(var service=new NommMetadataService(cache,null,handler))
        {
            check(await service.ImageAsync(url,CancellationToken.None)==path&&File.ReadAllBytes(path).SequenceEqual(good)&&calls==1,"a partial cached NOMM image is downloaded again and atomically replaced with validated bytes");
            offline=true;
            check(await service.ImageAsync(url,CancellationToken.None)==path&&calls==1,"a valid cached NOMM image remains usable offline without another request");
            File.WriteAllBytes(path,new byte[]{137,80});
            check(await service.ImageAsync(url,CancellationToken.None)==null&&calls==2,"offline failure never returns corrupt cached artwork as usable");
            offline=false;
            check(await service.ImageAsync(url,CancellationToken.None)==path&&calls==3,"corrupt cached artwork can recover on a later explicit request");
            using(var file=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.None))file.SetLength(8*1024*1024+1);
            check(await service.ImageAsync(url,CancellationToken.None)==path&&File.ReadAllBytes(path).SequenceEqual(good)&&calls==4,"oversized cached NOMM images are rejected before reading and replaced by bounded downloads");
            check(Directory.EnumerateFiles(imageRoot,"*.tmp").Count()==0,"successful and failed image requests leave no temporary cache payloads");
        }
    }
    private static void Files(Action<bool,string> check,string temp)
    {
        string plugins=Path.Combine(temp,"plugins"),queue=Path.Combine(temp,"queue"),old=Path.Combine(plugins,"Mod_1.0.dll"),fresh=Path.Combine(temp,"new.dll");Directory.CreateDirectory(plugins);
        Fixture(old,"tests.mod","1.0");Fixture(fresh,"tests.mod","1.1");string oldHash=ModUpdateFiles.Hash(old),newHash=ModUpdateFiles.Hash(fresh);
        foreach(var path in new[]{"../outside.dll","/outside.dll","C:\\outside.dll","folder\\..\\outside.dll","mod.dll:stream"})
            Throws(check,()=>ModUpdateFiles.Within(plugins,path),"update destination traversal and streams rejected");
        string bad=Path.Combine(temp,"bad.dll");Fixture(bad,"unrelated.mod","1.1");Throws(check,()=>ModUpdateFiles.VerifyIdentity(old,bad,"tests.mod"),"a different plugin cannot replace the selected mod");
        Fixture(bad,"tests.mod","1.1","extra.plugin");Throws(check,()=>ModUpdateFiles.VerifyIdentity(old,bad,"tests.mod"),"a release adding unrelated plugins needs manual installation");
        Fixture(bad,"com.nikkorap.blueprinter","2.1");Throws(check,()=>ModUpdateFiles.VerifyIdentity(bad,bad,"com.nikkorap.blueprinter"),"loader updates keep their dedicated install procedure");
        ModUpdateFiles.Stage(queue,plugins,old,fresh,"tests.mod","owner/repo","1.1");
        var pending=ModUpdateFiles.ReadPending(queue,plugins);ModUpdateFiles.VerifyPending(pending,plugins);
        check(pending.Length==1&&pending[0].Version=="1.1"&&pending[0].Target=="Mod_1.0.dll","downloaded updates can be discovered and verified without another download or installation");
        check(ModUpdateFiles.Hash(old)==oldHash,"in-game Update only queues a download; installed DLL stays intact");
        var job=ModUpdateFiles.Jobs(queue).Single();check(!Directory.EnumerateFiles(plugins,"*.dll",SearchOption.AllDirectories).Skip(1).Any(),"queue payloads cannot become duplicate BepInEx plugins");
        Throws(check,()=>ModUpdateFiles.Stage(queue,plugins,old,fresh,"tests.mod","owner/repo","1.1"),"duplicate queued update is rejected");
        var messages=new List<string>();ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);
        check(ModUpdateFiles.Hash(old)==newHash&&File.Exists(old)&&!File.Exists(Path.Combine(plugins,"new.dll")),"restart replaces the old path even when the release filename changes");
        check(ModUpdateFiles.Hash(Path.Combine(job,"previous.bin"))==oldHash,"replacement retains a verified original outside the plugin folder");
        check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(job,"plan.json")).State=="complete"&&ModUpdateFiles.PendingFor(queue,"Mod_1.0.dll")==null,"completed update is recorded and no longer queued");
        ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);check(ModUpdateFiles.Hash(old)==newHash&&messages.Count==1,"subsequent starts do not reinstall completed updates");
        check(ModUpdateFiles.ReadPending(queue,plugins).Length==0,"completed updates never keep prompting for a restart");
        var plan=UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(job,"plan.json"));plan.State="applying";UpdateJson.Save(Path.Combine(job,"plan.json"),plan);
        ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(job,"plan.json")).State=="complete","interruption after atomic replacement is reconciled by both hashes");
        Fixture(old,"tests.mod","1.0");string changedQueue=Path.Combine(temp,"changed-queue");ModUpdateFiles.Stage(changedQueue,plugins,old,fresh,"tests.mod","owner/repo","1.1");Fixture(old,"tests.mod","1.0.1");string changed=ModUpdateFiles.Hash(old);
        ModUpdateFiles.ApplyPending(changedQueue,plugins,messages.Add);check(ModUpdateFiles.Hash(old)==changed&&UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(ModUpdateFiles.Jobs(changedQueue).Single(),"plan.json")).State=="failed","a manually changed DLL is preserved instead of overwritten");
        Fixture(old,"tests.mod","1.0");string corruptQueue=Path.Combine(temp,"corrupt-queue");ModUpdateFiles.Stage(corruptQueue,plugins,old,fresh,"tests.mod","owner/repo","1.1");File.AppendAllText(Path.Combine(ModUpdateFiles.Jobs(corruptQueue).Single(),"payload.bin"),"tampered");string before=ModUpdateFiles.Hash(old);
        Throws(check,()=>ModUpdateFiles.VerifyPending(ModUpdateFiles.ReadPending(corruptQueue,plugins),plugins),"restart preparation refuses a corrupt queued download before quitting the game");
        ModUpdateFiles.ApplyPending(corruptQueue,plugins,messages.Add);check(ModUpdateFiles.Hash(old)==before,"corrupt staged payload never changes the installed mod");
        string zip=Path.Combine(temp,"one.zip");Archive(zip,new[]{("BepInEx/plugins/release.dll",File.ReadAllBytes(fresh)),("README.md",Encoding.UTF8.GetBytes("instructions"))});string extracted=Path.Combine(temp,"extracted.bin");ModUpdateService.ExtractSingleDll(zip,extracted);check(ModUpdateFiles.Hash(extracted)==newHash,"a single-plugin ZIP can be installed without extracting other files");
        string unsafeZip=Path.Combine(temp,"unsafe.zip");Archive(unsafeZip,new[]{("../release.dll",File.ReadAllBytes(fresh))});Throws(check,()=>ModUpdateService.ExtractSingleDll(unsafeZip,Path.Combine(temp,"unsafe.bin")),"traversal inside release ZIP rejected");
        string multiZip=Path.Combine(temp,"multi.zip");Archive(multiZip,new[]{("release.dll",File.ReadAllBytes(fresh)),("dependency.dll",File.ReadAllBytes(fresh))});Throws(check,()=>ModUpdateService.ExtractSingleDll(multiZip,Path.Combine(temp,"multi.bin")),"multi-DLL packages use their manual release instructions");
        string manifest=Path.Combine(temp,"card.json");File.WriteAllText(manifest,"{\"plugin\":\"tests.mod\",\"name\":\"Test\",\"update\":{\"repository\":\"owner/repo\"}}");var card=ModJson.Read<ModManifest>(manifest);card.Directory=temp;card.Validate();check(card.Update?.Asset=="","repository-only update manifests do not require boilerplate fields");
    }
    private static async Task Network(Action<bool,string> check,string temp)
    {
        string plugins=Path.Combine(temp,"network-plugins"),cache=Path.Combine(temp,"network-cache");Directory.CreateDirectory(plugins);string old=Path.Combine(plugins,"old.dll"),newDll=Path.Combine(temp,"network-new.dll");Fixture(old,"tests.mod","1.0");Fixture(newDll,"tests.mod","1.1");byte[] bytes=File.ReadAllBytes(newDll);DateTimeOffset now=DateTimeOffset.UtcNow;
        var asset=new GitHubAsset { Name="Mod_1.1.dll",State="uploaded",Size=bytes.Length,Url="https://github.com/owner/repo/releases/download/1.1/Mod_1.1.dll",Digest="sha256:"+ModUpdateFiles.Hash(newDll) };
        var release=new GitHubRelease { Tag="1.1",Url="https://github.com/owner/repo/releases/tag/1.1",Assets=new[]{asset} };
        string jsonPath=Path.Combine(temp,"release.json");UpdateJson.Save(jsonPath,release);string json=File.ReadAllText(jsonPath);int calls=0;bool conditional=false;
        var handler=new FakeHandler(request=>
        {
            calls++;
            if(request.RequestUri!.Host=="github.com") return new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(bytes) };
            if(request.Headers.IfNoneMatch.Count>0) { conditional=true;return new HttpResponseMessage(HttpStatusCode.NotModified); }
            var response=new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(json) };response.Headers.ETag=new EntityTagHeaderValue("\"release-1\"");return response;
        });
        using(var service=new ModUpdateService(plugins,cache,handler,()=>now))
        {
            var first=await service.CheckAsync("owner/repo","","1.0",old,CancellationToken.None);check(first.Newer&&first.Asset?.Name==asset.Name,"details page offers a newer stable release");
            var same=await service.CheckAsync("owner/repo","","1.1",old,CancellationToken.None);check(!same.Newer&&same.Message.StartsWith("Up to date")&&calls==1,"six-hour cache reuses metadata and compares each installed version separately");
            var concurrent=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>service.CheckAsync("owner/repo","","1.0",old,CancellationToken.None)));check(calls==1&&concurrent.All(c=>c.Newer),"repeated or simultaneous page opens do not duplicate GitHub requests");
            now=now.AddHours(7);await service.CheckAsync("owner/repo","","1.0",old,CancellationToken.None);check(calls==2&&conditional,"expired session cache uses an ETag conditional request");
            var progress=new List<int>();await service.StageAsync("owner/repo","tests.mod",old,release,asset,CancellationToken.None,progress.Add);
            check(progress.Last()==100&&ModUpdateFiles.PendingFor(Path.Combine(cache,"DOORMAN-updates"),"old.dll")!=null,"explicit Update streams and verifies the release before queueing it");
            check(ModUpdateFiles.Hash(old)!=ModUpdateFiles.Hash(newDll),"successful download does not replace a loaded DLL in the running game");
            var queued=await service.CheckAsync("owner/repo","","1.0",old,CancellationToken.None);check(queued.Queued&&calls==3,"queued update appears without another network request");
            var blockedPlan=ModUpdateFiles.PendingFor(Path.Combine(cache,"DOORMAN-updates"),ModUpdateFiles.RelativeTarget(plugins,old))!;
            blockedPlan.Message="Startup update blocked: Required plugin is not installed: tests.dependency.";UpdateJson.Save(Path.Combine(blockedPlan.Job,"plan.json"),blockedPlan);
            var blocked=await service.CheckAsync("owner/repo","","1.0",old,CancellationToken.None);
            check(blocked.Queued&&blocked.Message==blockedPlan.Message&&calls==3,"mod pages display a deferred startup dependency reason instead of repeatedly asking for restart");
        }
        int diskCalls=0;using(var service=new ModUpdateService(plugins,cache,new FakeHandler(_=>{diskCalls++;return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);}),()=>now))
        {var cached=await service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);check(cached.Newer&&diskCalls==0,"release cache persists across game restarts");}
        int failures=0;using(var service=new ModUpdateService(plugins,Path.Combine(temp,"failure-cache"),new FakeHandler(_=>{failures++;return new HttpResponseMessage(HttpStatusCode.Forbidden);}),()=>now))
        { var failed=await service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);await service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);await service.CheckAsync("other/repo","","1.0","",CancellationToken.None);check(!failed.Newer&&failures==1,"offline or rate-limited checks back off instead of retrying every page open"); }
        var multi=new GitHubRelease { Assets=new[]{asset,new GitHubAsset {Name="Other.dll",State="uploaded",Size=10,Url="https://github.com/owner/repo/releases/download/1.1/Other.dll"}} };
        check(multi.SelectAsset("owner/repo","")==null&&multi.SelectAsset("owner/repo","Mod_*.dll")==asset,"ambiguous downloads require an explicit manifest selector");
        asset.Digest="sha256:"+new string('0',64);using(var service=new ModUpdateService(plugins,Path.Combine(temp,"digest-cache"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(bytes) })))
        { try { await service.StageAsync("owner/repo","tests.mod",old,release,asset,CancellationToken.None);check(false,"bad GitHub checksum"); }catch(InvalidDataException){check(true,"wrong GitHub checksum rejects download before queue commit");}check(!ModUpdateFiles.Jobs(Path.Combine(temp,"digest-cache","DOORMAN-updates")).Any(),"failed checksum leaves no installable queue item"); }
        asset.Digest=null;using(var service=new ModUpdateService(plugins,Path.Combine(temp,"short-cache"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(bytes.Take(bytes.Length-1).ToArray()) })))
        { try { await service.StageAsync("owner/repo","tests.mod",old,release,asset,CancellationToken.None);check(false,"short download"); }catch(InvalidDataException){check(true,"incomplete download never reaches installation");} }
        using(var cancelled=new CancellationTokenSource()) using(var service=new ModUpdateService(plugins,Path.Combine(temp,"cancel-cache"),new FakeHandler(_=>throw new Exception("Network should not run"))))
        { cancelled.Cancel();try{await service.CheckAsync("owner/repo","","1.0","",cancelled.Token);check(false,"cancelled page");}catch(OperationCanceledException){check(true,"closing or changing a page can cancel its pending check");} }
        string zipped=Path.Combine(temp,"previews","preview.zip");byte[] zipBytes=File.ReadAllBytes(zipped);var zipAsset=new GitHubAsset { Name="Mod_1.1.zip",State="uploaded",Size=zipBytes.Length,Url="https://github.com/owner/repo/releases/download/1.1/Mod_1.1.zip",Digest="sha256:"+ModUpdateFiles.Hash(zipped) };
        var zipRelease=new GitHubRelease { Tag="1.1",Url="https://github.com/owner/repo/releases/tag/1.1",Assets=new[]{zipAsset} };string zipCache=Path.Combine(temp,"zip-cache");
        using(var service=new ModUpdateService(plugins,zipCache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(zipBytes) })))
        {
            await service.StageAsync("owner/repo","tests.mod",old,zipRelease,zipAsset,CancellationToken.None);
            var staged=ModUpdateFiles.ReadPending(Path.Combine(zipCache,"DOORMAN-updates"),plugins);ModUpdateFiles.VerifyPending(staged,plugins);
            check(staged.Length==1&&staged[0].Schema==2&&staged[0].Sidecars!.Length==2,"the real asynchronous ZIP download path queues preview and image hashes with the DLL");
            ModUpdateFiles.ApplyPending(Path.Combine(zipCache,"DOORMAN-updates"),plugins,_=>{});
            string installedCard=Path.Combine(plugins,ModUpdateFiles.MetadataDirectory("tests.mod"),"preview.doorman.json");
            check(ModUpdateFiles.Hash(old)==staged[0].NewHash&&ModJson.Read<ModManifest>(installedCard).Name=="New card","first managed preview installation creates only the stable plugin folder at startup");
        }
    }
    private static void InstallFiles(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"prerequisite-files"),plugins=Path.Combine(root,"plugins"),queue=Path.Combine(root,"queue"),payload=Path.Combine(root,"new.dll"),plugin="tests.prerequisite";
        Directory.CreateDirectory(plugins);Fixture(payload,plugin,"1.4");string fresh=ModUpdateFiles.Hash(payload),target=ModUpdateFiles.Within(plugins,ModUpdateFiles.InstallTarget(plugin));
        string config=Path.Combine(root,"settings.cfg");File.WriteAllText(config,"Keep my settings.");
        check(Path.GetDirectoryName(target)==plugins&&Path.GetFileName(target).StartsWith("DOORMAN-dependency-")&&Path.GetFileName(target).EndsWith(".dll"),"new prerequisite DLL lives beside existing plugins so relative map folders remain usable");
        foreach(string id in new[]{"kelly.nuclearoption.joincheck","KELLY.NUCLEAROPTION.JOINCHECK.SERVER","COM.NIKKORAP.BLUEPRINTER"})
            Throws(check,()=>ModUpdateFiles.VerifyInstallIdentity(payload,id,""),"protected DOORMAN and Blueprinter install identities cannot be queued");
        Throws(check,()=>ModUpdateFiles.VerifyInstallIdentity(payload,"other.plugin","1.0"),"prerequisite downloads must declare the expected plugin GUID");
        Throws(check,()=>ModUpdateFiles.VerifyInstallIdentity(payload,plugin,"1.5"),"actual DLL metadata below minimum is rejected regardless of release tags");
        Throws(check,()=>ModUpdateFiles.VerifyInstallIdentity(payload,plugin,"nightly"),"invalid prerequisite minimum versions cannot enter the queue");
        string multiple=Path.Combine(root,"multi.dll");Fixture(multiple,plugin,"1.4","another.plugin");
        Throws(check,()=>ModUpdateFiles.StageInstall(queue,plugins,multiple,plugin,"owner/repo","1.0"),"new prerequisite installs cannot smuggle a second BepInPlugin identity");
        ModUpdateFiles.StageInstall(queue,plugins,payload,plugin,"owner/repo","1.3");var plan=ModUpdateFiles.ReadPending(queue,plugins).Single();ModUpdateFiles.VerifyPending(new[]{plan},plugins);
        check(plan.Schema==3&&plan.Install&&plan.OldHash==""&&plan.PluginVersion=="1.4"&&plan.Version=="1.4"&&plan.MinimumVersion=="1.3"&&ModUpdateFiles.PendingPlugin(queue,plugin)?.Target==plan.Target,"prerequisite receipts bind actual DLL version and minimum without inventing a previous DLL");
        check(!File.Exists(target)&&Directory.GetFiles(plugins,"*.dll").Length==0&&File.ReadAllText(config)=="Keep my settings.","clicking prerequisite download stages outside plugins and preserves configuration");
        Throws(check,()=>ModUpdateFiles.StageInstall(queue,plugins,payload,plugin,"owner/repo","1.3"),"a duplicate pending prerequisite is rejected before another job is created");
        check(ModUpdateFiles.Jobs(queue).Count()==1,"duplicate prerequisite request leaves one exact committed install job");
        string planPath=Path.Combine(plan.Job,"plan.json"),savedTarget=plan.Target;plan.Target="elsewhere.dll";
        Throws(check,plan.Validate,"a prerequisite receipt cannot redirect installation to an arbitrary plugin path");plan.Target=savedTarget;
        plan.MinimumVersion="1.5";Throws(check,plan.Validate,"a tampered minimum version above the staged actual DLL is rejected");plan.MinimumVersion="1.3";
        var messages=new List<string>();ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);
        check(ModUpdateFiles.Hash(target)==fresh&&UpdateJson.ReadFile<PendingModUpdate>(planPath).State=="complete"&&!File.Exists(Path.Combine(plan.Job,"previous.bin")),"startup installs a new prerequisite without replacing or backing up nonexistent plugins");
        check(ModUpdateFiles.PendingPlugin(queue,plugin)==null&&File.ReadAllText(config)=="Keep my settings.","installed prerequisite leaves the active queue and keeps all settings");
        ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);check(messages.Count==1,"complete prerequisite installs are not repeated on later starts");
        plan.State="applying";UpdateJson.Save(planPath,plan);ModUpdateFiles.VerifyPending(ModUpdateFiles.ReadPending(queue,plugins),plugins);ModUpdateFiles.ApplyPending(queue,plugins,messages.Add);
        check(UpdateJson.ReadFile<PendingModUpdate>(planPath).State=="complete"&&ModUpdateFiles.Hash(target)==fresh,"an interruption after new DLL replacement reconciles by its verified hash");
        Throws(check,()=>ModUpdateFiles.StageInstall(Path.Combine(root,"already-installed"),plugins,payload,plugin,"owner/repo","1.3"),"an already present managed prerequisite target is never overwritten as a new install");
        File.Delete(target);string otherFolder=Path.Combine(plugins,"other");Directory.CreateDirectory(otherFolder);string alternate=Path.Combine(otherFolder,"different-name.dll");Fixture(alternate,plugin,"1.1");
        Throws(check,()=>ModUpdateFiles.StageInstall(Path.Combine(root,"alternate-queue"),plugins,payload,plugin,"owner/repo","1.3"),"existing prerequisite GUID under another DLL filename is updated through its existing entry");File.Delete(alternate);
        Fixture(target,"unrelated.plugin","1.0");string collided=ModUpdateFiles.Hash(target);
        Throws(check,()=>ModUpdateFiles.StageInstall(Path.Combine(root,"collision-queue"),plugins,payload,plugin,"owner/repo","1.3"),"deterministic prerequisite destination collisions cannot overwrite unrelated files");check(ModUpdateFiles.Hash(target)==collided,"destination collision preserves the original DLL bytes");File.Delete(target);
        string raced=Path.Combine(root,"raced-queue");ModUpdateFiles.StageInstall(raced,plugins,payload,plugin,"owner/repo","1.3");Fixture(alternate,plugin,"1.1");ModUpdateFiles.ApplyPending(raced,plugins,messages.Add);
        check(!File.Exists(target)&&UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(ModUpdateFiles.Jobs(raced).Single(),"plan.json")).State=="failed","a plugin added manually after download blocks a duplicate startup installation");File.Delete(alternate);
        string corrupt=Path.Combine(root,"corrupt-queue");ModUpdateFiles.StageInstall(corrupt,plugins,payload,plugin,"owner/repo","1.3");File.AppendAllText(Path.Combine(ModUpdateFiles.Jobs(corrupt).Single(),"payload.bin"),"tampered");
        Throws(check,()=>ModUpdateFiles.VerifyPending(ModUpdateFiles.ReadPending(corrupt,plugins),plugins),"corrupt new prerequisite payload is rejected before quitting the game");ModUpdateFiles.ApplyPending(corrupt,plugins,messages.Add);check(!File.Exists(target),"corrupt prerequisite staging never becomes a loaded plugin");
        string cancelled=Path.Combine(root,"cancelled-queue");ModUpdateFiles.StageInstall(cancelled,plugins,payload,plugin,"owner/repo","1.3");var cancelledPlan=ModUpdateFiles.ReadPending(cancelled,plugins).Single();cancelledPlan.State="cancelled";UpdateJson.Save(Path.Combine(cancelledPlan.Job,"plan.json"),cancelledPlan);ModUpdateFiles.ApplyPending(cancelled,plugins,messages.Add);
        check(!File.Exists(target)&&ModUpdateFiles.ReadPending(cancelled,plugins).Length==0,"cancelled prerequisite receipt never installs or retains a restart prompt");
        using(var token=new CancellationTokenSource()){token.Cancel();ThrowsCancelled(check,()=>ModUpdateFiles.StageInstall(Path.Combine(root,"cancel-token"),plugins,payload,plugin,"owner/repo","1.3",null,token.Token),"cancelled prerequisite staging commits no job");}
        string badZip=Path.Combine(root,"multicomponent.zip");Archive(badZip,new[]{("Required.dll",File.ReadAllBytes(payload)),("map.nomap",new byte[]{1})});
        Throws(check,()=>ModUpdateService.ExtractSingleDll(badZip,Path.Combine(root,"not-extracted.bin"),plugin),"mixed map and plugin packages need their release page installation instructions");
        string invalidPreview=Path.Combine(root,"invalid-preview.zip");byte[] invalidCard=Encoding.UTF8.GetBytes("{\"plugin\":\""+plugin+"\",\"name\":\"Required\",\"requisites\":[{\"plugin\":\"another.plugin\",\"minimumVersion\":\"nightly\"}]}");Archive(invalidPreview,new[]{("Required.dll",File.ReadAllBytes(payload)),("Required.doorman.json",invalidCard)});
        Throws(check,()=>ModUpdateService.ExtractSingleDll(invalidPreview,Path.Combine(root,"invalid-preview.bin"),plugin),"downloaded preview prerequisites remain strictly validated even though local invalid cards stay visible");
        Throws(check,()=>ModUpdateFiles.ValidatePreviews(new[]{new StagedModSidecar { Name="preview.doorman.json",Data=invalidCard }},plugin,plugins),"staged prerequisite preview rejects malformed requisite declarations");
        string scriptZip=Path.Combine(root,"script.zip");Archive(scriptZip,new[]{("Required.dll",File.ReadAllBytes(payload)),("install.py",Encoding.UTF8.GetBytes("not run"))});
        Throws(check,()=>ModUpdateService.ExtractSingleDll(scriptZip,Path.Combine(root,"script.bin"),plugin),"prerequisite ZIPs containing installation scripts stay on the manual release-page path");
        string previewPlugins=Path.Combine(root,"preview-plugins"),previewQueue=Path.Combine(root,"preview-queue");Directory.CreateDirectory(previewPlugins);
        byte[] previewCard=Encoding.UTF8.GetBytes("{\"plugin\":\""+plugin+"\",\"name\":\"Required\",\"image\":\"image.png\"}");var newPreviews=new[]{new StagedModSidecar {Name="preview.doorman.json",Data=previewCard},new StagedModSidecar {Name="image.png",Data=Png(32,16)}};
        ModUpdateFiles.StageInstall(previewQueue,previewPlugins,payload,plugin,"owner/repo","1.3",newPreviews);var previewPlan=ModUpdateFiles.ReadPending(previewQueue,previewPlugins).Single();previewPlan.State="applying";UpdateJson.Save(Path.Combine(previewPlan.Job,"plan.json"),previewPlan);
        var imageRecord=previewPlan.Sidecars!.Single(f=>f.Name=="image.png");string installedImage=ModUpdateFiles.Within(previewPlugins,imageRecord.Target(plugin));Directory.CreateDirectory(Path.GetDirectoryName(installedImage)!);File.Copy(Path.Combine(previewPlan.Job,imageRecord.Payload),installedImage);
        ModUpdateFiles.VerifyPending(new[]{previewPlan},previewPlugins);ModUpdateFiles.ApplyPending(previewQueue,previewPlugins,messages.Add);
        check(UpdateJson.ReadFile<PendingModUpdate>(Path.Combine(previewPlan.Job,"plan.json")).State=="complete"&&File.Exists(ModUpdateFiles.Within(previewPlugins,previewPlan.Target))&&File.Exists(ModUpdateFiles.Within(previewPlugins,previewPlan.Sidecars!.Single(f=>f.Name=="preview.doorman.json").Target(plugin))),"new prerequisite DLL and preview installation resumes safely after only its image was written");
        string updateOld=Path.Combine(plugins,"update-existing.dll"),updateQueue=Path.Combine(root,"required-update");Fixture(updateOld,plugin,"1.0");
        Throws(check,()=>ModUpdateFiles.Stage(updateQueue,plugins,updateOld,payload,plugin,"owner/repo","99.0",null,"1.5"),"updating an existing prerequisite validates actual DLL minimum independently of latest release tag");
        ModUpdateFiles.Stage(updateQueue,plugins,updateOld,payload,plugin,"owner/repo","99.0",null,"1.3");var update=ModUpdateFiles.ReadPending(updateQueue,plugins).Single();
        check(update.Schema==1&&!update.Install&&update.Version=="99.0"&&update.PluginVersion=="1.4"&&update.MinimumVersion=="1.3","existing prerequisite updates retain old queue schema and separately record the actual plugin version");
        ModUpdateFiles.VerifyPending(new[]{update},plugins);ModUpdateFiles.ApplyPending(updateQueue,plugins,messages.Add);
        check(ModUpdateFiles.Hash(updateOld)==fresh&&File.Exists(Path.Combine(update.Job,"previous.bin")),"existing prerequisite update retains the original path and a verified rollback DLL");
    }
    private static async Task InstallNetwork(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"prerequisite-network"),plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),plugin="tests.required",payload=Path.Combine(root,"required.dll");Directory.CreateDirectory(plugins);Fixture(payload,plugin,"1.4");byte[] dll=File.ReadAllBytes(payload);
        string zip=Path.Combine(root,"release.zip");Archive(zip,new[]{("Required.dll",dll),("README.md",Encoding.UTF8.GetBytes("Installation details"))});byte[] bytes=File.ReadAllBytes(zip);
        var asset=new GitHubAsset { Name="Required.zip",State="uploaded",Size=bytes.Length,Url="https://github.com/owner/repo/releases/download/99.0/Required.zip",Digest="sha256:"+ModUpdateFiles.Hash(zip) };
        var release=new GitHubRelease { Tag="99.0",Url="https://github.com/owner/repo/releases/tag/99.0",Assets=new[]{asset} };string metadata=Path.Combine(root,"release.json");UpdateJson.Save(metadata,release);string json=File.ReadAllText(metadata);int requests=0;
        using(var service=new ModUpdateService(plugins,cache,new FakeHandler(request=>{requests++;return request.RequestUri!.Host=="api.github.com"?new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)}:new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};})))
        {
            var checks=await Task.WhenAll(Enumerable.Range(0,8).Select(_=>service.CheckPrerequisiteAsync("owner/repo","Required*.zip",plugin,"1.3",CancellationToken.None)));
            check(requests==1&&checks.All(c=>c.Asset?.Name==asset.Name&&c.Release?.Tag=="99.0"),"prerequisite details pages share the bounded GitHub cache and concurrent request gate");
            var progress=new List<int>();await service.StageInstallAsync("owner/repo",plugin,"1.3",release,asset,CancellationToken.None,progress.Add);
            var pending=ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins).Single();
            check(progress.Last()==100&&pending.PluginVersion=="1.4"&&pending.Version=="1.4"&&requests==2&&!Directory.EnumerateFiles(plugins,"*.dll").Any(),"explicit prerequisite download accepts a single-DLL ZIP and queues its actual metadata version without live installation");
            var queued=await service.CheckPrerequisiteAsync("owner/repo","Required*.zip",plugin,"1.3",CancellationToken.None);
            check(queued.Queued&&queued.Message.Contains("1.4")&&requests==2,"queued prerequisite shows a restart message without another metadata request");
            var blockedPlan=ModUpdateFiles.PendingPlugin(Path.Combine(cache,"DOORMAN-updates"),plugin)!;
            blockedPlan.Message="Startup update blocked: Required plugin is disabled: tests.dependency.";UpdateJson.Save(Path.Combine(blockedPlan.Job,"plan.json"),blockedPlan);
            var blocked=await service.CheckPrerequisiteAsync("owner/repo","Required*.zip",plugin,"1.3",CancellationToken.None);
            check(blocked.Queued&&blocked.Message==blockedPlan.Message&&requests==2,"prerequisite pages expose the persisted blocked-update reason without extra HTTP");
        }
        string cancelledCache=Path.Combine(root,"cancel-cache");using(var token=new CancellationTokenSource())using(var service=new ModUpdateService(plugins,cancelledCache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})))
        {
            try{await service.StageInstallAsync("owner/repo",plugin,"1.3",release,asset,token.Token,p=>{if(p==100)token.Cancel();});check(false,"cancelled download");}catch(OperationCanceledException){check(true,"cancellation at the end of a prerequisite download prevents queue commit");}
            check(!ModUpdateFiles.Jobs(Path.Combine(cancelledCache,"DOORMAN-updates")).Any()&&Directory.GetFiles(Path.Combine(cancelledCache,"DOORMAN-updates","downloads")).Length==0,"cancelled prerequisite download removes temporary files and creates no pending install");
        }
        string minimumCache=Path.Combine(root,"minimum-cache");using(var service=new ModUpdateService(plugins,minimumCache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)})))
        {
            try{await service.StageInstallAsync("owner/repo",plugin,"1.5",release,asset,CancellationToken.None);check(false,"required DLL minimum");}catch(InvalidDataException){check(true,"a high release tag cannot bypass the actual prerequisite DLL minimum version");}
            check(!ModUpdateFiles.Jobs(Path.Combine(minimumCache,"DOORMAN-updates")).Any(),"prerequisite DLL below minimum produces no installable receipt");
        }
    }
    private static async Task ReleaseBodyDeadlines(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"release-body-deadlines"),plugins=Path.Combine(root,"plugins");Directory.CreateDirectory(plugins);
        const string json="{\"tag_name\":\"1.1\",\"html_url\":\"https://github.com/owner/repo/releases/tag/1.1\",\"assets\":[]}";
        foreach(bool ignoresCancellation in new[]{false,true})
        {
            var body=new StalledStream(ignoresCancellation);int calls=0;var now=DateTimeOffset.UtcNow;
            using(var service=new ModUpdateService(plugins,Path.Combine(root,ignoresCancellation?"ignores-token":"honours-token"),new FakeHandler(_=>
                new HttpResponseMessage(HttpStatusCode.OK){Content=++calls==1?(HttpContent)new StreamContent(body):new StringContent(json)}),()=>now,releaseTimeout:TimeSpan.FromMilliseconds(100)))
            {
                var first=service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);
                await WithinDeadline(body.ReadStarted.Task);
                var queued=service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);
                await WithinDeadline(Task.WhenAll(first,queued));
                var failed=await first;var cached=await queued;
                check(failed.Message.Contains("timed out")&&!failed.Newer&&failed.Release==null,"release JSON body timeout produces a readable failure without a partial release");
                check(body.Disposed&&body.Cancelled!=ignoresCancellation,"release JSON deadline disposes cooperative and non-cooperative streams");
                check(calls==1&&cached.Message==failed.Message,"release JSON timeout releases the request gate and queued checks honour failure backoff");
                now=now.Add(ModUpdateService.FailureLifetime).AddSeconds(1);
                var retry=service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);await WithinDeadline(retry);
                check(calls==2&&(await retry).Newer&&(await retry).Release?.Tag=="1.1","a release JSON check succeeds after its timeout backoff expires");
            }
        }
        var cancelledBody=new StalledStream(true);int cancellationCalls=0;
        using(var cancellation=new CancellationTokenSource())
        using(var service=new ModUpdateService(plugins,Path.Combine(root,"caller-cancelled"),new FakeHandler(_=>
            new HttpResponseMessage(HttpStatusCode.OK){Content=++cancellationCalls==1?(HttpContent)new StreamContent(cancelledBody):new StringContent(json)})))
        {
            var attempt=service.CheckAsync("owner/repo","","1.0","",cancellation.Token);
            await WithinDeadline(cancelledBody.ReadStarted.Task);cancellation.Cancel();await WithinDeadline(attempt);
            try{await attempt;check(false,"release JSON caller cancellation");}
            catch(OperationCanceledException ex){check(ex.CancellationToken==cancellation.Token&&attempt.IsCanceled&&cancelledBody.Disposed,"caller cancellation aborts a non-cooperative release JSON body and preserves cancellation semantics");}
            var retry=service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);await WithinDeadline(retry);
            check(cancellationCalls==2&&(await retry).Newer,"cancelled release JSON checks release their gate and do not cache a failure against the next page request");
        }
        using(var service=new ModUpdateService(plugins,Path.Combine(root,"not-found"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.NotFound))))
        {
            var failed=await service.CheckAsync("owner/repo","","1.0","",CancellationToken.None);
            check(failed.Message=="No public stable GitHub release found.","ordinary release HTTP errors retain their original guidance rather than becoming timeouts");
        }
    }
    private static async Task StalledBodies(Action<bool,string> check,string temp)
    {
        await ReleaseBodyDeadlines(check,temp);
        string root=Path.Combine(temp,"stalled-bodies"),plugins=Path.Combine(root,"plugins"),old=Path.Combine(plugins,"old.dll"),fresh=Path.Combine(root,"fresh.dll"),cache=Path.Combine(root,"cache");Directory.CreateDirectory(plugins);
        Fixture(old,"tests.mod","1.0");Fixture(fresh,"tests.mod","1.1");byte[] bytes=File.ReadAllBytes(fresh);string oldHash=ModUpdateFiles.Hash(old);
        var asset=new GitHubAsset {Name="Mod.dll",State="uploaded",Size=bytes.Length,Url="https://github.com/owner/repo/releases/download/1.1/Mod.dll"};
        var release=new GitHubRelease {Tag="1.1",Url="https://github.com/owner/repo/releases/tag/1.1",Assets=new[]{asset}};
        var stalled=new StalledStream();int calls=0;
        using(var service=new ModUpdateService(plugins,cache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=++calls==1?(HttpContent)new StreamContent(stalled):new ByteArrayContent(bytes)}),downloadTimeout:TimeSpan.FromMilliseconds(100)))
        {
            var attempt=service.StageAsync("owner/repo","tests.mod",old,release,asset,CancellationToken.None);
            await WithinDeadline(attempt);
            try {await attempt;check(false,"stalled download");}catch(IOException ex){check(ex.Message.Contains("timed out"),"a release body stalled after headers reports a readable timeout");}
            check(stalled.Cancelled&&stalled.Disposed,"download deadline cancels its body read and disposes the response stream");
            check(ModUpdateFiles.Hash(old)==oldHash&&!ModUpdateFiles.Jobs(Path.Combine(cache,"DOORMAN-updates")).Any()&&Directory.GetFiles(Path.Combine(cache,"DOORMAN-updates","downloads")).Length==0,"timed-out downloads preserve installed DLLs and leave no temporary or pending update");
            await service.StageAsync("owner/repo","tests.mod",old,release,asset,CancellationToken.None);
            check(calls==2&&ModUpdateFiles.ReadPending(Path.Combine(cache,"DOORMAN-updates"),plugins).Length==1,"the download request gate is released after a body timeout and an explicit retry can succeed");
        }
        var metadataBody=new StalledStream();string local=Path.Combine(root,"nomm-local.json");File.WriteAllText(local,"[{\"id\":\"tests.mod\",\"displayName\":\"Local card\"}]");
        using(var service=new NommMetadataService(Path.Combine(root,"nomm-cache"),local,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(metadataBody)}),TimeSpan.FromMilliseconds(100)))
        {
            var attempt=service.GetAsync(CancellationToken.None);await WithinDeadline(attempt);var records=await attempt;
            check(records.Length==1&&records[0].Name=="Local card"&&metadataBody.Cancelled&&metadataBody.Disposed,"stalled NOMM metadata times out through the body and preserves the usable local index");
            check((await service.GetAsync(CancellationToken.None)).Length==1,"metadata timeout releases its request gate and retains failure backoff");
        }
        var imageBody=new StalledStream();int imageCalls=0;
        using(var service=new NommMetadataService(Path.Combine(root,"image-cache"),local,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=++imageCalls==1?(HttpContent)new StreamContent(imageBody):new ByteArrayContent(Png(32,16))}),TimeSpan.FromMilliseconds(100)))
        {
            const string url="https://raw.githubusercontent.com/owner/repo/main/card.png";
            var attempt=service.ImageAsync(url,CancellationToken.None);await WithinDeadline(attempt);
            check(await attempt==null&&imageBody.Cancelled&&imageBody.Disposed,"stalled preview image body times out without publishing a cache file");
            check(await service.ImageAsync(url,CancellationToken.None)!=null&&imageCalls==2,"an explicit later page request can recover its image after a body timeout");
        }
        var cancelledBody=new StalledStream();
        using(var cancellation=new CancellationTokenSource())using(var service=new ModUpdateService(plugins,Path.Combine(root,"cancel-cache"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(cancelledBody)})))
        {
            var attempt=service.StageAsync("owner/repo","tests.mod",old,release,asset,cancellation.Token);cancellation.CancelAfter(50);await WithinDeadline(attempt);
            try{await attempt;check(false,"cancelled stalled body");}catch(OperationCanceledException){check(true,"caller cancellation of a stalled download remains cancellation rather than a timeout failure");}
        }
        var ignoredBody=new StalledStream(true);string ignoredCache=Path.Combine(root,"ignored-cancellation");
        using(var service=new ModUpdateService(plugins,ignoredCache,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(ignoredBody)}),downloadTimeout:TimeSpan.FromMilliseconds(100)))
        {
            var attempt=service.StageAsync("owner/repo","tests.mod",old,release,asset,CancellationToken.None);await WithinDeadline(attempt);
            try{await attempt;check(false,"ignored body cancellation");}catch(IOException ex){check(ex.Message.Contains("timed out"),"a download stream that ignores ReadAsync cancellation is aborted and its disposal error is reported as a timeout");}
            check(ignoredBody.Disposed&&!ignoredBody.Cancelled&&ModUpdateFiles.Hash(old)==oldHash&&!ModUpdateFiles.Jobs(Path.Combine(ignoredCache,"DOORMAN-updates")).Any(),"aborting a non-cooperative download preserves the installed file and creates no queued update");
        }
        var ignoredMetadata=new StalledStream(true);
        using(var service=new NommMetadataService(Path.Combine(root,"ignored-metadata"),local,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(ignoredMetadata)}),TimeSpan.FromMilliseconds(100)))
        {
            var attempt=service.GetAsync(CancellationToken.None);await WithinDeadline(attempt);
            check((await attempt).Length==1&&ignoredMetadata.Disposed&&!ignoredMetadata.Cancelled,"NOMM body deadline disposes a stream that ignores cancellation and retains its local index");
        }
        var ignoredImage=new StalledStream(true);
        using(var service=new NommMetadataService(Path.Combine(root,"ignored-image"),local,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(ignoredImage)}),TimeSpan.FromMilliseconds(100)))
        {
            var attempt=service.ImageAsync("https://raw.githubusercontent.com/owner/repo/main/card.png",CancellationToken.None);await WithinDeadline(attempt);
            check(await attempt==null&&ignoredImage.Disposed&&!ignoredImage.Cancelled,"preview image deadline aborts non-cooperative body reads without publishing an image");
        }
        var ignoredCaller=new StalledStream(true);
        using(var cancellation=new CancellationTokenSource())using(var service=new NommMetadataService(Path.Combine(root,"ignored-caller"),local,new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(ignoredCaller)})))
        {
            var attempt=service.ImageAsync("https://raw.githubusercontent.com/owner/repo/main/card.png",cancellation.Token);cancellation.CancelAfter(50);await WithinDeadline(attempt);
            try{await attempt;check(false,"non-cooperative caller cancellation");}catch(OperationCanceledException){check(ignoredCaller.Disposed,"caller cancellation that aborts a non-cooperative image stream remains cancellation");}
        }
        using(var service=new ModUpdateService(plugins,Path.Combine(root,"http-failure"),new FakeHandler(_=>new HttpResponseMessage(HttpStatusCode.NotFound))))
        {
            try{await service.StageAsync("owner/repo","tests.mod",old,release,asset,CancellationToken.None);check(false,"normal HTTP failure");}catch(HttpRequestException){check(true,"ordinary HTTP failure before a deadline retains its original error rather than becoming a timeout");}
        }
    }
    private static async Task WithinDeadline(Task task)
    {if(await Task.WhenAny(task,Task.Delay(TimeSpan.FromSeconds(5)))!=task)throw new Exception("FAILED: an HTTP body remained stalled beyond its deadline");}
    private sealed class StalledStream:Stream
    {
        internal bool Cancelled,Disposed;
        internal readonly TaskCompletionSource<bool> ReadStarted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool ignoreCancellation;
        private readonly TaskCompletionSource<int> aborted=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        internal StalledStream(bool ignoreCancellation=false){this.ignoreCancellation=ignoreCancellation;}
        public override bool CanRead=>true;
        public override bool CanSeek=>false;
        public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();
        public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override async Task<int> ReadAsync(byte[] buffer,int offset,int count,CancellationToken token)
        {ReadStarted.TrySetResult(true);if(ignoreCancellation)return await aborted.Task;try{await Task.Delay(Timeout.Infinite,token);return 0;}catch(OperationCanceledException){Cancelled=true;throw;}}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        protected override void Dispose(bool disposing){Disposed=true;if(ignoreCancellation)aborted.TrySetException(new IOException("Response stream aborted."));base.Dispose(disposing);}
    }
    private static void ThrowsCancelled(Action<bool,string> check,Action action,string message){try{action();check(false,message);}catch(OperationCanceledException){check(true,message);}}
    private static void SavedVersions(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-versions-"+Guid.NewGuid().ToString("N")),plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),target=Path.Combine(plugins,"aircraft.dll"),newFile=Path.Combine(root,"new.dll");
        try
        {
            Directory.CreateDirectory(plugins);Directory.CreateDirectory(cache);Fixture(target,"test.aircraft","1.0");Fixture(newFile,"test.aircraft","2.0");
            string old=ModUpdateFiles.Hash(target),newHash=ModUpdateFiles.Hash(newFile),queue=Path.Combine(cache,"DOORMAN-updates");
            ModUpdateFiles.Stage(queue,plugins,target,newFile,"test.aircraft","owner/repo","2.0");ModUpdateFiles.ApplyPending(queue,plugins,_=>{});
            var entry=new ModEntry {Id="test.aircraft",Path=target};
            var record=new NommRecord {Id="test.aircraft",Name="Aircraft",Artifacts=new[]{new NommArtifact {Version="1.0",Hash="sha256:"+old}}};
            var saved=SavedModVersions.Read(cache,plugins,entry,new[]{record});
            check(saved.Length==1&&saved[0].Version=="1.0"&&ModUpdateFiles.Hash(target)==newHash,"version list identifies a hash-verified previous plugin without changing its live file");
            SavedModVersions.Stage(cache,plugins,saved[0]);check(ModUpdateFiles.Hash(target)==newHash,"selecting a saved plugin version only queues it before restart");
            ModUpdateFiles.ApplyPending(queue,plugins,_=>{});check(ModUpdateFiles.Hash(target)==old,"startup restores the selected saved plugin version");
            var again=SavedModVersions.Read(cache,plugins,entry,new[]{record});check(again.Length==1&&again[0].Hash==newHash,"version switches preserve the replaced version for switching back");
            File.AppendAllText(again[0].Backup,"tampered");Throws(check,()=>SavedModVersions.Stage(cache,plugins,again[0]),"tampered saved versions cannot be installed");
            string bundle=Path.Combine(plugins,"pack.nobp"),replacement=Path.Combine(root,"new.nobp"),cq=Path.Combine(cache,"DOORMAN-content-updates");
            File.WriteAllText(bundle,"original content");File.WriteAllText(replacement,"replacement content");string bundleOld=ModUpdateFiles.Hash(bundle),bundleNew=ModUpdateFiles.Hash(replacement);
            StandaloneBundleUpdates.Stage(cq,plugins,replacement,bundle,"test.bundle","2.0",bundleNew);StandaloneBundleUpdates.Apply(cq,plugins,_=>{});
            var bundleEntry=new ModEntry {Id="test.bundle",Path=bundle,Content=true};var bundleRecord=new NommRecord {Id="test.bundle",Name="Bundle",Artifacts=new[]{new NommArtifact {Version="1.0",Hash="sha256:"+bundleOld}}};
            var savedBundle=SavedModVersions.Read(cache,plugins,bundleEntry,new[]{bundleRecord});check(savedBundle.Length==1&&savedBundle[0].Version=="1.0","standalone content versions use the stored bundle backup");
            SavedModVersions.Stage(cache,plugins,savedBundle[0]);check(ModUpdateFiles.Hash(bundle)==bundleNew,"saved bundle selection leaves the running content unchanged");
            StandaloneBundleUpdates.Apply(cq,plugins,_=>{});check(ModUpdateFiles.Hash(bundle)==bundleOld,"restart restores the selected standalone content version");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private static void Fixture(string path,string id,string version,params string[] otherIds)
    {
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("TestMod",new Version(version)),"TestMod",ModuleKind.Dll))
        {
            var module=assembly.MainModule;var plugin=new TypeReference("BepInEx","BepInPlugin",module,new AssemblyNameReference("BepInEx",new Version(5,4,23,0)));var constructor=new MethodReference(".ctor",module.TypeSystem.Void,plugin) { HasThis=true };
            for(int i=0;i<3;i++) constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
            foreach(var guid in new[]{id}.Concat(otherIds)) { var type=new TypeDefinition("Tests","Plugin"+module.Types.Count,TypeAttributes.Public,module.TypeSystem.Object);module.Types.Add(type);var attribute=new CustomAttribute(constructor);foreach(var value in new[]{guid,"Test Mod",version}) attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,value));type.CustomAttributes.Add(attribute); }
            assembly.Write(path);
        }
    }
    private static void Archive(string path,(string Name,byte[] Data)[] entries)
    { using(var output=File.Create(path)) using(var zip=new ZipArchive(output,ZipArchiveMode.Create)) foreach(var entry in entries) using(var stream=zip.CreateEntry(entry.Name).Open()) stream.Write(entry.Data); }
    private static void Throws(Action<bool,string> check,Action action,string message) { try { action();check(false,message); }catch(InvalidDataException){check(true,message);}catch(IOException){check(true,message);}catch(InvalidOperationException){check(true,message);} }
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage,HttpResponseMessage> respond;
        internal FakeHandler(Func<HttpRequestMessage,HttpResponseMessage> respond)=>this.respond=respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>Task.FromResult(respond(request));
    }
}

