using System;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;
using Mono.Cecil;
using Mono.Cecil.Cil;

internal static class ContentSourceRegression
{
    internal static void Run(Action<bool,string> check)
    {
        string temporary=Path.Combine(Path.GetTempPath(),"doorman-source-check-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try { Sources(check,temporary);LargeStandalone(check,temporary); }
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(temporary).StartsWith(parent,StringComparison.OrdinalIgnoreCase)&&Directory.Exists(temporary))Directory.Delete(temporary,true);
        }
    }

    private static void Reject(Action<bool,string> check,Action action,string message)
    {
        bool rejected=false;
        try { action(); }catch(Exception ex) when(ex is InvalidDataException||ex is IOException||ex is ArgumentException){rejected=true;}
        check(rejected,message);
    }
    private static LoadedContentAssembly Loaded(string path,string resource="Pack.aircraft.nobp",string name="Pack")=>new LoadedContentAssembly {AssemblyName=name,Location=path,Resources=new[]{resource}};
    private static string Dll(string plugins,string filename,string marker,int pluginsCount=0,string resource="Pack.aircraft.nobp",string name="Pack")
    {
        string path=Path.GetFullPath(Path.Combine(plugins,filename));Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name,new Version(1,0)),filename,ModuleKind.Dll))
        {
            var module=assembly.MainModule;
            module.Resources.Add(new EmbeddedResource(resource,ManifestResourceAttributes.Public,new byte[]{1,2,3}));
            for(int i=0;i<pluginsCount;i++)
            {
                var type=new TypeDefinition("Example","Plugin"+i,TypeAttributes.Public|TypeAttributes.Class,module.TypeSystem.Object);module.Types.Add(type);
                var attrType=new TypeReference("BepInEx","BepInPlugin",module,new AssemblyNameReference("BepInEx",new Version(5,4,21)));
                var constructor=new MethodReference(".ctor",module.TypeSystem.Void,attrType){HasThis=true};
                foreach(var unused in Enumerable.Range(0,3))constructor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
                var attribute=new CustomAttribute(constructor);
                foreach(var value in new[]{"example.plugin"+i,"Example "+i,"1.0"})attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,value));
                type.CustomAttributes.Add(attribute);
            }
            // The resolver must only read metadata. Executing this initializer
            // would leave a detectable marker, even for a DLL with no plugins.
            var initializer=new MethodDefinition(".cctor",MethodAttributes.Static|MethodAttributes.Private|MethodAttributes.SpecialName|MethodAttributes.RTSpecialName,module.TypeSystem.Void);
            module.Types.Single(t=>t.Name=="<Module>").Methods.Add(initializer);
            var il=initializer.Body.GetILProcessor();il.Emit(OpCodes.Ldstr,marker);il.Emit(OpCodes.Ldstr,"executed");
            il.Emit(OpCodes.Call,module.ImportReference(typeof(File).GetMethod(nameof(File.WriteAllText),new[]{typeof(string),typeof(string)})!));il.Emit(OpCodes.Ret);
            assembly.Write(path);
        }
        return path;
    }

    private static void Sources(Action<bool,string> check,string temporary)
    {
        string root=Path.Combine(temporary,"plugins"),marker=Path.Combine(temporary,"executed.txt"),source="resource:Pack:Pack.aircraft.nobp";
        Directory.CreateDirectory(root);
        string multi=Dll(root,"nested/multi.dll",marker,2),plain=Dll(root,"plain.dll",marker),other=Dll(root,"other.dll",marker,1),wrong=Dll(root,"wrong.dll",marker,0,"Pack.other.nobp");
        string bundle=Path.Combine(root,"nested","standalone.nobp");File.WriteAllText(bundle,"pack");
        var empty=Array.Empty<LoadedContentAssembly>();
        check(ContentSourceFiles.Resolve(root,bundle,"",empty)==bundle,"absolute in-folder standalone content retains its exact file");
        check(ContentSourceFiles.Resolve(root,source,multi,empty)==multi,"a DLL with two BepInPlugin classes has one independently verified physical content source");
        check(ContentSourceFiles.Resolve(root,source,plain,empty)==plain,"a resource DLL without BepInPlugin metadata can still be verified without loading it");
        check(ContentSourceFiles.Resolve(root,source,"",new[]{Loaded(multi),Loaded(multi)})==multi,"duplicate observations of one physical DLL do not invent ambiguous ownership");
        check(ContentSourceFiles.Resolve(root,source,other,new[]{Loaded(multi)})==multi,"the actual loaded DLL takes precedence over a different catalog candidate");
        check(ContentSourceFiles.Resolve(root,source,"",new[]{Loaded(wrong,"Pack.other.nobp"),Loaded(multi)})==multi,"exact resource names distinguish same-name loaded assemblies");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,"",new[]{Loaded(multi),Loaded(other)}),"two distinct physical suppliers of the exact resource are rejected");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,other,new[]{Loaded(Path.Combine(root,"missing.dll"))}),"a vanished loaded DLL is not rebound to another metadata DLL");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,other,new[]{Loaded("")}),"a loaded assembly without a location cannot fall back to a catalog file");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,other,new[]{Loaded(wrong,"Pack.other.nobp")}),"an existing loaded identity with a different resource cannot use a fallback file");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,wrong,empty),"a cached DLL with the wrong exact resource is rejected");
        string renamed=Dll(root,"renamed.dll",marker,0,"Pack.aircraft.nobp","DifferentPack");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,renamed,empty),"a cached DLL with another assembly identity is rejected");
        Reject(check,()=>ContentSourceFiles.Resolve(root,"resource:Pack:pack.aircraft.nobp",multi,empty),"resource matching remains case exact");
        Reject(check,()=>ContentSourceFiles.Resolve(root,"resource:Pack:../aircraft.nobp",multi,empty),"embedded resource sources reject path traversal syntax");
        Reject(check,()=>ContentSourceFiles.Resolve(root,"resource:Pack:Pack.aircraft.nobp:extra",multi,empty),"embedded resource sources require the exact three-part format");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,"",empty),"unlocated resources produce a clear install or normal restart error");
        string outside=Dll(temporary,"external.dll",marker);
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,outside,empty),"catalog fallback does not permit an external DLL");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,multi,new[]{Loaded(outside)}),"a loaded external DLL is not rebound to a safe catalog path");
        string sibling=Dll(Path.Combine(temporary,"plugins-backup"),"pack.dll",marker);
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,sibling,empty),"a sibling folder sharing the plugins name prefix remains outside the allowed root");
        Reject(check,()=>ContentSourceFiles.Resolve(root,Path.Combine(temporary,"outside.nobp"),"",empty),"standalone files outside plugins remain forbidden");
        Reject(check,()=>ContentSourceFiles.Resolve(root,Path.Combine(root,"..","outside.nobp"),"",empty),"absolute traversal cannot escape the plugin root");
        Reject(check,()=>ContentSourceFiles.Resolve(root,"nested/standalone.nobp",bundle,empty),"relative standalone sources are rejected instead of guessing the process working directory");
        Reject(check,()=>ContentSourceFiles.Resolve(root,new Uri(bundle).AbsoluteUri,bundle,empty),"file URIs are not treated as ordinary installed file paths");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,bundle,empty),"a .nobp cannot substitute for a resource-owning DLL");
        string invalid=Path.Combine(root,"invalid.dll");File.WriteAllText(invalid,"not a DLL");
        Reject(check,()=>ContentSourceFiles.Resolve(root,source,invalid,empty),"unreadable fallback DLL metadata fails safely without execution");
        string previous=Environment.CurrentDirectory;
        try { Environment.CurrentDirectory=temporary;check(ContentSourceFiles.Resolve(root,source,multi,empty)==multi&&ContentSourceFiles.Resolve(root,bundle,"",empty)==bundle,"changing the process working directory cannot alter absolute source selection"); }
        finally { Environment.CurrentDirectory=previous; }
        check(!File.Exists(marker)&&!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name=="DifferentPack"),"physical source resolution never executes or runtime-loads the inspected DLLs");
        string link=Path.Combine(root,"linked");
        try
        {
            Directory.CreateSymbolicLink(link,Path.GetDirectoryName(outside)!);
            Reject(check,()=>ContentSourceFiles.Resolve(root,source,Path.Combine(link,"external.dll"),empty),"linked folders cannot bypass the existing plugin root guard");
        }
        catch(Exception ex) when(ex is UnauthorizedAccessException||ex is IOException||ex is PlatformNotSupportedException)
        { Console.WriteLine("Content source symlink checks unavailable on this host: "+ex.GetType().Name); }
        finally { if(Directory.Exists(link))Directory.Delete(link); }
    }

    private static void LargeStandalone(Action<bool,string> check,string temporary)
    {
        string root=Path.Combine(temporary,"large","plugins"),path=Path.Combine(root,"F117","reported-size.nobp");
        LargeInstalledContentFixture.Create(path);
        check(new FileInfo(path).Length==LargeInstalledContentFixture.ReportedBytes,"standalone source regression uses the exact reported F117 size of 462355664 bytes");
        long before=GC.GetAllocatedBytesForCurrentThread();
        string resolved=ContentSourceFiles.Resolve(root,path,"",Array.Empty<LoadedContentAssembly>());
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        check(resolved==path,"an installed report-size F117 .nobp larger than 256 MiB resolves to its exact in-folder source");
        check(allocated<16L*1024*1024,"resolving a 462355664-byte installed bundle does not allocate a source-sized buffer");
        Reject(check,()=>ContentSourceFiles.Resolve(Path.Combine(temporary,"other-plugins"),path,"",Array.Empty<LoadedContentAssembly>()),"raising the installed content bound does not admit a large source outside its plugin root");
        string dll=Path.ChangeExtension(path,".dll");File.Move(path,dll);
        Reject(check,()=>ContentSourceFiles.Resolve(root,"resource:Pack:Pack.aircraft.nobp",dll,Array.Empty<LoadedContentAssembly>()),"a report-size DLL is rejected before disabled metadata parsing while standalone source hashing remains supported");
        check(ContentSourceFiles.Resolve(root,"resource:Pack:Pack.aircraft.nobp",dll,new[]{Loaded(dll)})==dll,"an actual loaded large DLL observation does not require reparsing its metadata to bind its installed source");
    }

    internal static void Contracts(AssemblyDefinition client,Action<bool,string> check)
    {
        var resolver=client.MainModule.GetType("KellysJOINCHECK.ContentSourceFiles").Methods.Single(m=>m.Name=="Resolve").Body.Instructions;
        check(resolver.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName=="System.IO.Stream"&&m.Name=="get_Length")
            &&resolver.Any(i=>i.Operand is MethodReference m&&m.Name=="ReadAssembly"&&m.Parameters[0].ParameterType.FullName=="System.IO.Stream"),"disabled content metadata is bounded on the same open file handle passed to Cecil");
        var hashing=client.MainModule.GetType("KellysJOINCHECK.ModManager").Methods.Single(m=>m.Name=="HashInventory").Body.Instructions;
        check(hashing.Any(i=>i.Operand is MethodReference m&&m.DeclaringType.Name=="InstalledFilePolicy"&&m.Name=="Hash")
            &&!hashing.Any(i=>i.Operand is MethodReference m&&m.Name=="ReadAllBytes"),"restart verification uses the tested streaming installed-file hash policy instead of buffering the entire mod");
    }
}

// Deterministic zero-filled fixture with tiny distant markers. Its independently
// calculated SHA256 covers all 462355664 bytes, including data beyond the old
// 256 MiB boundary and at the final byte; no test buffers the complete source.
internal static class LargeInstalledContentFixture
{
    internal const long ReportedBytes=462355664;
    internal const long FarMarkerOffset=268500993;
    internal const string ExpectedSha256="052113879b68e41ada141104f784e5f456b1834bf72d7e3db033d9bc4495812e";
    internal static void Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            file.SetLength(ReportedBytes);
            Marker(file,0,"DOORMAN-F117-LARGE-SOURCE");Marker(file,FarMarkerOffset,"BeyondOldLimit");
            Marker(file,ReportedBytes-Encoding.ASCII.GetByteCount("END-OF-F117-SOURCE"),"END-OF-F117-SOURCE");file.Flush(true);
        }
    }
    internal static void ChangeFarMarker(string path)
    {
        DateTime writeTime=File.GetLastWriteTimeUtc(path);
        using(var file=new FileStream(path,FileMode.Open,FileAccess.Write,FileShare.None))
        {Marker(file,FarMarkerOffset,"ChangedMarker!");file.Flush(true);}
        File.SetLastWriteTimeUtc(path,writeTime);
    }
    private static void Marker(FileStream file,long offset,string text)
    {var bytes=Encoding.ASCII.GetBytes(text);file.Position=offset;file.Write(bytes,0,bytes.Length);}
}
