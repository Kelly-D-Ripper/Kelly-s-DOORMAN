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
    internal static void Run(Action<bool,string> check,AssemblyDefinition client,AssemblyDefinition server,string startupPath)
    {
        check(ModReleaseVersion.Compare("v1.1.7.1","1.1.7")==1,"four-part mod hotfix versions compare correctly");
        check(ModReleaseVersion.Compare("1.2.0","1.2")==0,"missing numeric components compare as zero");
        check(ModReleaseVersion.Compare("1.0.0","1.0.0-rc.2")==1&&ModReleaseVersion.Compare("1.0-rc.10","1.0-rc.2")==1,"stable and numbered prerelease comparisons");
        check(ModReleaseVersion.Compare("1.0+build","1.0+other")==0,"build metadata never triggers an update");
        foreach(var version in new[]{"Nightly","release-2026","v1","99999999999.0","1.0/etc"}) check(ModReleaseVersion.Compare(version,"1.0")==null,"unknown version names do not trigger guessed upgrades");
        check(GitHubRepository.FromUrl("https://github.com/Aryx3D/Aryx-s-FS-41-Eclipse/releases/latest")=="Aryx3D/Aryx-s-FS-41-Eclipse","GitHub links identify repositories regardless of release paths");
        foreach(var url in new[]{"https://github.com.attacker.test/a/b","http://github.com/a/b","https://user:password@github.com/a/b","https://github.com:8443/a/b","file:///a/b"}) check(GitHubRepository.FromUrl(url)=="","unsafe repository links rejected");
        check(!GitHubRepository.Valid("a/../b")&&!GitHubRepository.Valid("a/.."),"repositories cannot redirect the API path");
        check(GitHubRepository.AssetMatches("AryxWeaponsPack_1.2.dll","AryxWeaponsPack_*.dll")&&!GitHubRepository.AssetMatches("Other.dll","AryxWeaponsPack_*.dll"),"optional manifest patterns select only intended release assets");
        check(!GitHubRepository.DownloadUrl("https://github.com/other/repo/releases/download/1.2/a.dll","a/b"),"assets must come from the chosen repository");
        check(!GitHubRepository.RedirectUrl(new Uri("https://release-assets.githubusercontent.com.evil.test/a"))&&!GitHubRepository.RedirectUrl(new Uri("http://objects.githubusercontent.com/a")),"download redirects remain HTTPS on exact GitHub hosts");
        string temp=Path.Combine(Path.GetTempPath(),"doorman-update-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        try { Files(check,temp); QueueCapacity(check,temp); JsonWriteSafety(check,temp); PreviewFiles(check,temp); Network(check,temp).GetAwaiter().GetResult(); NommImages(check,temp).GetAwaiter().GetResult(); }
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
            check(finish.Body.Instructions.Any(i=>i.Operand is MethodReference m&&m.Name=="ApplyPending"),"updates install before Chainloader plugin discovery");
            check(!startup.MainModule.AssemblyReferences.Any(a=>a.Name=="System.Net.Http"),"startup installation cannot make network requests");
        }
    }
    private static void PreviewFiles(Action<bool,string> check,string temp)
    {
        string root=Path.Combine(temp,"previews"),plugins=Path.Combine(root,"plugins"),old=Path.Combine(plugins,"Old_1.0.dll"),fresh=Path.Combine(root,"fresh.dll"),zip=Path.Combine(root,"preview.zip"),payload=Path.Combine(root,"extracted.bin"),queue=Path.Combine(root,"queue");Directory.CreateDirectory(plugins);
        Fixture(old,"tests.mod","1.0");Fixture(fresh,"tests.mod","1.1");byte[] image=Png(32,16),oldImage=Png(16,16);
        byte[] card=Encoding.UTF8.GetBytes("{\"plugin\":\"tests.mod\",\"name\":\"New card\",\"description\":\"New description\",\"image\":\"art/preview.png\",\"links\":[{\"label\":\"GitHub\",\"url\":\"https://github.com/owner/repo\"}]}");
        string managed=Path.Combine(plugins,ModUpdateFiles.MetadataDirectory("tests.mod")),manifest=Path.Combine(managed,"preview.doorman.json"),picture=Path.Combine(managed,"image.png");Directory.CreateDirectory(managed);
        string oldCard="{\"plugin\":\"tests.mod\",\"name\":\"Old card\",\"image\":\"image.png\"}";File.WriteAllText(manifest,oldCard);File.WriteAllBytes(picture,oldImage);string oldDllHash=ModUpdateFiles.Hash(old),oldCardHash=ModUpdateFiles.Hash(manifest),oldPictureHash=ModUpdateFiles.Hash(picture);
        Archive(zip,new[]{("BepInEx/plugins/Mod/NewName.dll",File.ReadAllBytes(fresh)),("BepInEx/plugins/Mod/card.doorman.json",card),("BepInEx/plugins/Mod/art/preview.png",image),("README.md",Encoding.UTF8.GetBytes("ignored")),("BepInEx/config/not-installed.cfg",Encoding.UTF8.GetBytes("ignored"))});
        var previews=ModUpdateService.ExtractSingleDll(zip,payload,"tests.mod");
        var parsed=ModJson.ReadBytes<ModManifest>(previews.Single(p=>p.Name=="preview.doorman.json").Data);
        check(previews.Length==2&&parsed.Plugin=="tests.mod"&&parsed.Image=="image.png"&&parsed.Description=="New description","nested single-DLL ZIP binds one plugin preview and normalizes only its referenced image");
        check(!File.Exists(Path.Combine(plugins,"not-installed.cfg"))&&Directory.GetFiles(root).Length==3,"archive read never extracts unrelated configs or arbitrary files");
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
            UpdateJson.Save(Path.Combine(queue,Guid.NewGuid().ToString("N"),"plan.json"),new PendingModUpdate { Plugin="tests.mod",Repository="owner/repo",Version="1.1",Target="queued-"+i+".dll",OldHash=original,NewHash=newHash,State=i%2==0?"applying":"pending" });
        check(ModUpdateFiles.ReadPending(queue,plugins).Length==ModUpdateFiles.MaximumPendingJobs,"both pending and interrupted applying receipts count toward active capacity");
        string second=Path.Combine(plugins,"second.dll");File.Copy(old,second);int jobs=Directory.EnumerateDirectories(queue).Count();
        Throws(check,()=>ModUpdateFiles.Stage(queue,plugins,second,fresh,"tests.mod","owner/repo","1.1"),"an active queue at capacity refuses another update before creating hidden work");
        check(Directory.EnumerateDirectories(queue).Count()==jobs&&ModUpdateFiles.Hash(old)==original&&ModUpdateFiles.Hash(second)==original,"active-capacity refusal creates no job and never changes installed files");
        UpdateJson.Save(Path.Combine(queue,Guid.NewGuid().ToString("N"),"plan.json"),new PendingModUpdate { Plugin="tests.mod",Repository="owner/repo",Version="1.1",Target="excess.dll",OldHash=original,NewHash=newHash });
        Throws(check,()=>ModUpdateFiles.ReadPending(queue,plugins),"restart preparation explicitly rejects an externally overfull active queue");
        Throws(check,()=>ModUpdateFiles.PendingFor(queue,"old.dll"),"details checks cannot report a truncated active queue as usable");
        Throws(check,()=>ModUpdateFiles.ApplyPending(queue,plugins,_=>{}),"startup refuses excess active jobs before applying any update");
        check(ModUpdateFiles.Hash(old)==original&&ModUpdateFiles.Hash(Path.Combine(history[0],"previous.bin"))==original,"active overflow leaves installed DLLs and retained backups intact");
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
