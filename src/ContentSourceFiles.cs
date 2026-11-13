using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;

namespace KellysJOINCHECK
{
    // Runtime assembly observations identify the actual DLL that supplied a
    // loaded resource. This is deliberately separate from BepInPlugin ownership.
    internal sealed class LoadedContentAssembly
    {
        internal string AssemblyName="",Location="";
        internal string[] Resources=Array.Empty<string>();
    }

    internal static class ContentSourceFiles
    {
        internal static string Resolve(string pluginRoot,string source,string knownPhysicalPath,IEnumerable<LoadedContentAssembly> loaded)
        {
            if(string.IsNullOrWhiteSpace(source)||source.Length>2048||source.Any(char.IsControl))
                throw new InvalidDataException("The content source is invalid. Reinstall this mod and restart normally.");
            if(!source.StartsWith("resource:",StringComparison.Ordinal))
            {
                // Blueprinter normally provides absolute paths. Guessing a base
                // for a relative source can bind its version to the wrong file.
                if(!Path.IsPathRooted(source))
                    throw new InvalidDataException("The content source has no absolute file path. Reinstall this mod inside BepInEx/plugins and restart normally.");
                if(!source.EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The standalone content source is not a .nobp file. Reinstall this mod and restart normally.");
                return Physical(pluginRoot,source,false);
            }

            var parts=source.Split(':');
            if(parts.Length!=3||!Name(parts[1],256)||!Name(parts[2],1536)||!parts[2].EndsWith(".nobp",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded content source is invalid. Reinstall this mod and restart normally.");
            string assemblyName=parts[1],resourceName=parts[2];
            var identities=(loaded??Array.Empty<LoadedContentAssembly>()).Where(a=>a!=null&&a.AssemblyName==assemblyName).Take(4097).ToArray();
            if(identities.Length>4096)throw new InvalidDataException("Too many matching content assemblies. Restart normally to rebuild the mod list.");
            if(identities.Length>0)
            {
                var matches=identities.Where(a=>a.Resources!=null&&a.Resources.Contains(resourceName,StringComparer.Ordinal)).ToArray();
                if(matches.Length==0)
                    throw new InvalidDataException("The loaded DLL does not contain this exact content resource. Restart normally to discover the installed version.");
                // A missing, moved or external loaded DLL must never be rebound
                // to a different file discovered by the metadata catalog.
                var paths=matches.Select(a=>Physical(pluginRoot,a.Location,true)).Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                if(paths.Length!=1)
                    throw new InvalidDataException("More than one installed DLL supplies this content resource. Remove the duplicate mod and restart normally.");
                return paths[0];
            }

            // A disabled plugin is absent from AppDomain. A cached/catalog file
            // is acceptable only after its exact assembly and resource identity
            // have been read without executing it.
            if(string.IsNullOrWhiteSpace(knownPhysicalPath))
                throw new InvalidDataException("The DLL supplying this content resource could not be located. Reinstall this mod inside BepInEx/plugins and restart normally.");
            string known=Physical(pluginRoot,knownPhysicalPath,true);
            try
            {
                using(var input=new FileStream(known,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                if(input.Length>InstalledFilePolicy.MaximumMetadataDllBytes)
                    throw new InvalidDataException("This disabled content DLL is too large to inspect safely. Enable it manually and restart normally first.");
                using(var assembly=AssemblyDefinition.ReadAssembly(input,new ReaderParameters { ReadSymbols=false,ReadingMode=ReadingMode.Deferred }))
                    if(assembly.Name.Name!=assemblyName||!assembly.MainModule.Resources.OfType<EmbeddedResource>().Any(r=>r.Name==resourceName))
                        throw new InvalidDataException("The installed DLL does not match this exact content resource. Restart normally to discover the installed version.");
                }
            }
            catch(Exception ex) when(ex is BadImageFormatException||ex is ArgumentException||ex is NotSupportedException)
            { throw new InvalidDataException("The content DLL metadata could not be read. Reinstall this mod and restart normally.",ex); }
            return known;
        }

        private static bool Name(string value,int maximum)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=maximum&&!value.Any(c=>char.IsControl(c)||c=='/'||c=='\\'||c==':');

        private static string Physical(string pluginRoot,string path,bool dll)
        {
            if(string.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path))
                throw new InvalidDataException("The DLL supplying this content has no usable installed location. Reinstall this mod inside BepInEx/plugins and restart normally.");
            string relative;
            try { relative=ServerSetupFiles.RelativeContent(pluginRoot,path); }
            catch(InvalidDataException ex) when(ex.Message=="Content is outside the plugin folder.")
            { throw new InvalidDataException("This content is installed outside BepInEx/plugins. Move or reinstall this mod inside that folder, then restart normally.",ex); }
            string full=ModUpdateFiles.Within(pluginRoot,relative);
            if(dll&&!full.EndsWith(".dll",StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The embedded content source is not a DLL. Reinstall this mod and restart normally.");
            var file=new FileInfo(full);
            if(!file.Exists)throw new InvalidDataException("The content file moved or disappeared after loading. Restore it and restart normally.");
            InstalledFilePolicy.ValidateLength(file.Length);
            return full;
        }
    }
}
