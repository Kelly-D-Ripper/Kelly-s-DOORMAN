using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class SelfUpdateTransactionRegression
{
    private static readonly string FutureVersion = (Version.Parse(SelfUpdateRelease.Installed.Split('-')[0]).Major + 1) + ".0.0-rc.1";
    private static void Fixture(string path,int version)
    {
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("KellysJOINCHECK",new Version(1,4,version)),"KellysJOINCHECK",ModuleKind.Dll))
        {
            var m=assembly.MainModule;var a=new TypeReference("BepInEx","BepInPlugin",m,new AssemblyNameReference("BepInEx",new Version(5,4,23,0)));var ctor=new MethodReference(".ctor",m.TypeSystem.Void,a){HasThis=true};
            for(int i=0;i<3;i++)ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));var t=new TypeDefinition("Test","Plugin",TypeAttributes.Public,m.TypeSystem.Object);m.Types.Add(t);var attribute=new CustomAttribute(ctor);
            foreach(string value in new[]{"kelly.nuclearoption.joincheck","DOORMAN","1.4.0"})attribute.ConstructorArguments.Add(new CustomAttributeArgument(m.TypeSystem.String,value));t.CustomAttributes.Add(attribute);
            var release=new TypeDefinition("KellysJOINCHECK","SelfUpdateRelease",TypeAttributes.NotPublic,m.TypeSystem.Object);m.Types.Add(release);
            var installed=new FieldDefinition("Installed",FieldAttributes.Public|FieldAttributes.Static|FieldAttributes.Literal|FieldAttributes.HasDefault,m.TypeSystem.String){Constant="1.4.0-rc."+(18+version)};release.Fields.Add(installed);assembly.Write(path);
        }
    }
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-self-transaction-"+Guid.NewGuid().ToString("N"));string bep=Path.Combine(root,"BepInEx"),cache=Path.Combine(bep,"cache"),plugin=Path.Combine(bep,"plugins","KellysJOINCHECK.dll"),startup=Path.Combine(bep,"patchers","KellysDOORMANStartup.dll"),restart=Path.Combine(bep,"DOORMAN","KellysDOORMANRestart.exe"),fresh=Path.Combine(root,"new.dll");
        try
        {
            foreach(string p in new[]{plugin,startup,restart})Directory.CreateDirectory(Path.GetDirectoryName(p)!);Directory.CreateDirectory(cache);Fixture(plugin,0);Fixture(fresh,1);File.WriteAllText(startup,"startup");File.WriteAllText(restart,"restart");
            string old=ModUpdateFiles.Hash(plugin),newHash=ModUpdateFiles.Hash(fresh),sh=ModUpdateFiles.Hash(startup),rh=ModUpdateFiles.Hash(restart);
            try{SelfUpdateTransaction.Stage(cache,bep,fresh,"1.4.0-rc.20",sh,rh,"");check(false,"mismatched release version");}catch(InvalidDataException){check(true,"a stale client cannot be presented as a newer release");}
            SelfUpdateTransaction.Stage(cache,bep,fresh,"1.4.0-rc.19",sh,rh,"");check(SelfUpdateTransaction.Pending(cache)&&ModUpdateFiles.Hash(plugin)==old,"self update stages without changing the running client");
            check(SelfUpdateTransaction.Apply(cache,bep,_=>{})&&ModUpdateFiles.Hash(plugin)==newHash,"startup atomically installs the verified DOORMAN client");
            check(ModUpdateFiles.Hash(Path.Combine(SelfUpdateTransaction.Root(cache),"previous.bin"))==old&&SelfUpdateTransaction.Read(cache)!.State=="complete","self update retains a verified rollback copy");
            Fixture(fresh,2);SelfUpdateTransaction.Stage(cache,bep,fresh,"1.4.0-rc.20",sh,rh,"");check(Directory.GetDirectories(cache,"DOORMAN-self-history-*").Length==1,"later self updates preserve earlier receipts and backups");
            File.WriteAllText(restart,"changed");check(!SelfUpdateTransaction.Apply(cache,bep,_=>{})&&ModUpdateFiles.Hash(plugin)==newHash&&SelfUpdateTransaction.Read(cache)!.State=="failed","changed helper blocks the client replacement");
            File.WriteAllText(restart,"restart");SelfUpdateTransaction.Stage(cache,bep,fresh,"1.4.0-rc.20",sh,rh,"");var plan=SelfUpdateTransaction.Read(cache)!;File.Copy(plugin,Path.Combine(SelfUpdateTransaction.Root(cache),"previous.bin"));plan.State="applying";UpdateJson.Save(Path.Combine(SelfUpdateTransaction.Root(cache),"plan.json"),plan);File.Copy(fresh,plugin,true);
            check(SelfUpdateTransaction.Apply(cache,bep,_=>{})&&SelfUpdateTransaction.Read(cache)!.State=="complete","a restart after replacement reconciles the verified interrupted update");
            Fixture(fresh,3);SelfUpdateTransaction.Stage(cache,bep,fresh,"1.4.0-rc.21",sh,rh,"");string current=ModUpdateFiles.Hash(plugin);File.WriteAllText(plugin+".doorman-new","unrelated file");
            check(!SelfUpdateTransaction.Apply(cache,bep,_=>{})&&SelfUpdateTransaction.Read(cache)!.State=="failed"&&ModUpdateFiles.Hash(plugin)==current&&File.ReadAllText(plugin+".doorman-new")=="unrelated file","caught replacement failures stop restart loops and preserve the installed client and unrelated files");
            check(SelfUpdateRelease.Comparable("v1.4.0-rc.19-map-install")=="1.4.0-rc.19"&&SelfUpdateRelease.Comparable("v1.4.0")=="1.4.0","release comparison understands labelled previews and stable versions");
            var releases=new[]{new GitHubRelease{Tag="v1.4.0-rc.19-preview",Url="https://github.com/"+SelfUpdateRelease.Repository+"/releases/tag/v1.4.0-rc.19-preview",Prerelease=true},new GitHubRelease{Tag="v1.4.0",Url="https://github.com/"+SelfUpdateRelease.Repository+"/releases/tag/v1.4.0"}};
            check(SelfUpdateRelease.Choose(releases,true)?.Tag=="v1.4.0"&&SelfUpdateRelease.Choose(new[]{releases[0]},false)==null,"stable releases outrank previews and stable channel excludes previews");
            var api=new ReleaseHandler();var fetched=SelfUpdateRelease.Check(CancellationToken.None,api).GetAwaiter().GetResult();
            check(api.CorrectUrl&&fetched?.Tag=="v"+FutureVersion&&SelfUpdateRelease.Asset(fetched,false)?.Name=="KellysDOORMAN-"+FutureVersion+"-CLIENT.zip","self update fetches a published preview and selects the verified client package");
            fetched!.Assets[0].Name="KellysDOORMAN-1.4.0-rc.18-CLIENT.zip";check(SelfUpdateRelease.Asset(fetched,false)==null,"a release cannot silently select an older named package");
            fetched.Assets[0].Name="KellysDOORMAN-"+FutureVersion+"-FULL.zip";
            fetched.Assets[0].Url=fetched.Assets[0].Url.Replace("-CLIENT.zip","-FULL.zip");
            check(SelfUpdateRelease.Asset(fetched,false)==fetched.Assets[0]&&SelfUpdateRelease.Asset(fetched,true)==fetched.Assets[0],"a verified FULL-only release can update a client-only install without installing its server");
        }
        finally {if(Directory.Exists(root))Directory.Delete(root,true);}
    }
    private sealed class ReleaseHandler:HttpMessageHandler
    {
        internal bool CorrectUrl;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            CorrectUrl=request.RequestUri?.ToString()=="https://api.github.com/repos/"+SelfUpdateRelease.Repository+"/releases?per_page=100";
            string body="[{\"tag_name\":\"v9.0.0-rc.1\",\"html_url\":\"https://github.com/"+SelfUpdateRelease.Repository+"/releases/tag/v9.0.0-rc.1\",\"prerelease\":true,\"assets\":[{\"name\":\"KellysDOORMAN-9.0.0-rc.1-CLIENT.zip\",\"state\":\"uploaded\",\"size\":100,\"digest\":\"sha256:"+new string('a',64)+"\",\"browser_download_url\":\"https://github.com/"+SelfUpdateRelease.Repository+"/releases/download/v9.0.0-rc.1/KellysDOORMAN-9.0.0-rc.1-CLIENT.zip\"}]}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body.Replace("9.0.0-rc.1",FutureVersion))});
        }
    }
}
