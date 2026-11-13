using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using KellysJOINCHECK;

internal static class MapPackageRegression
{
    private static void Reject(Action<bool,string> check,Action action,string message){try{action();check(false,message);}catch(Exception ex) when(ex is IOException||ex is InvalidDataException||ex is UnauthorizedAccessException||ex is InvalidOperationException){check(true,message);}}
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"doorman-map-package-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string plugins=Path.Combine(root,"plugins"),cache=Path.Combine(root,"cache"),missions=Path.Combine(root,"missions");Directory.CreateDirectory(plugins);
            string map=Path.Combine(root,"source.bin");File.WriteAllText(map,"synthetic map payload");string digest="sha256:"+ModUpdateFiles.Hash(map);
            var result=MapPackageInstaller.Install("owner/map","island.nomapz",map,digest,cache,plugins,missions,Array.Empty<string>(),CancellationToken.None);
            string target=Path.Combine(plugins,"NOCustomMaps","maps","island.nomapz");
            check(File.Exists(target)&&File.ReadAllText(target)=="synthetic map payload"&&result.Contains("Restart"),"direct nomapz installs into the custom maps folder with a restart message");
            Reject(check,()=>MapPackageInstaller.Install("owner/map","island.nomapz",map,digest,cache,plugins,missions,Array.Empty<string>(),CancellationToken.None),"existing maps block replacement");
            check(File.ReadAllText(target)=="synthetic map payload","existing map bytes survive a conflicting install");
            Reject(check,()=>MapPackageInstaller.Install("owner/map","wrong.nomap",map,"sha256:"+new string('0',64),cache,plugins,missions,Array.Empty<string>(),CancellationToken.None),"checksum mismatch blocks map installation");
            string zip=Path.Combine(root,"map.zip");using(var file=File.Create(zip))using(var z=new ZipArchive(file,ZipArchiveMode.Create)){var e=z.CreateEntry("maps/simple.nomap");using(var w=new StreamWriter(e.Open()))w.Write("map");var d=z.CreateEntry("README.txt");using(var w=new StreamWriter(d.Open()))w.Write("docs");}
            check(MapPackageInstaller.IsMapArchive("owner/map","map.zip",zip),"map ZIP detection finds nested map files");
            MapPackageInstaller.Install("owner/map","map.zip",zip,"sha256:"+ModUpdateFiles.Hash(zip),cache,plugins,missions,Array.Empty<string>(),CancellationToken.None);
            check(File.Exists(Path.Combine(plugins,"NOCustomMaps","maps","simple.nomap")),"simple map ZIP installs the map to the loader folder");
            string loader=Path.Combine(plugins,"NOMapLoader");Directory.CreateDirectory(loader);
            string raw=Path.Combine(root,"raw.bin");File.WriteAllBytes(raw,System.Text.Encoding.ASCII.GetBytes("UnityFS\0test"));
            check(MapPackageInstaller.UsesNOMapLoader("owner/map","map_test",raw),"raw map selects NOMapLoader");
            MapPackageInstaller.Install("owner/map","map_test",raw,"sha256:"+ModUpdateFiles.Hash(raw),cache,plugins,missions,Array.Empty<string>(),CancellationToken.None,loader);
            check(File.Exists(Path.Combine(loader,"map_test")),"raw map installs beside the actual NOMapLoader DLL");
            string rawZip=Path.Combine(root,"raw.zip");using(var f=File.Create(rawZip))using(var z=new ZipArchive(f,ZipArchiveMode.Create)){var e=z.CreateEntry("map_other");using(var w=e.Open()){var b=System.Text.Encoding.ASCII.GetBytes("UnityFS\0test");w.Write(b,0,b.Length);}var d=z.CreateEntry("README.txt");using(var w=new StreamWriter(d.Open()))w.Write("docs");}
            check(MapPackageInstaller.IsMapArchive("owner/map","map_bundle.zip",rawZip)&&MapPackageInstaller.UsesNOMapLoader("owner/map","map_bundle.zip",rawZip),"ZIPs named map_ are inspected rather than mistaken for raw bundles");
            MapPackageInstaller.Install("owner/map","map_bundle.zip",rawZip,"sha256:"+ModUpdateFiles.Hash(rawZip),cache,plugins,missions,Array.Empty<string>(),CancellationToken.None,loader);
            check(File.Exists(Path.Combine(loader,"map_other")),"raw map ZIP installs beside NOMapLoader");
            Reject(check,()=>MapPackageInstaller.Install("owner/map","map_bad",map,digest,cache,plugins,missions,Array.Empty<string>(),CancellationToken.None,loader),"invalid raw bundles are rejected");
            check(!File.Exists(Path.Combine(loader,"map_bad")),"invalid raw bundles do not alter the loader folder");
            string unsafeZip=Path.Combine(root,"unsafe.zip");using(var file=File.Create(unsafeZip))using(var z=new ZipArchive(file,ZipArchiveMode.Create)){var e=z.CreateEntry("../escape.nomap");using(var w=new StreamWriter(e.Open()))w.Write("unsafe");}
            Reject(check,()=>MapPackageInstaller.Install("owner/map","unsafe.zip",unsafeZip,"sha256:"+ModUpdateFiles.Hash(unsafeZip),cache,plugins,missions,Array.Empty<string>(),CancellationToken.None),"map ZIP traversal is rejected before changing plugins");
            Reject(check,()=>MapPackageInstaller.Install("Talamander/ironjaw-sound","fake.zip",zip,"sha256:"+ModUpdateFiles.Hash(zip),cache,plugins,missions,Array.Empty<string>(),CancellationToken.None),"unreviewed Ironjaw bytes never use the trusted multi-file layout");
            string? workspace=Environment.GetEnvironmentVariable("DOORMAN_REAL_MAP_FIXTURES");
            if(!string.IsNullOrWhiteSpace(workspace))
            {
                string wake=Path.Combine(workspace,"WakeMap","GitHubRelease","Kellys-Wake-Island-0.3.13.zip");
                string iron=Path.Combine(workspace,"server-maintenance","staged","patch-0345-20261027","requested-content-20261027","IronjawSound-1.0.2.zip");
                MapPackageInstaller.Install("Kelly-D-Ripper/KELLYS-Wake-Island","wake.zip",wake,"sha256:"+MapPackageInstaller.WakeHash,cache,plugins,missions,Array.Empty<string>(),CancellationToken.None);
                check(File.Exists(Path.Combine(plugins,"NOCustomMaps","maps","kellys-wake-island-0.3.13.nomap"))&&File.Exists(Path.Combine(plugins,"KellysWakeIsland","KellysWakeFlightSupport.dll"))&&Directory.GetFiles(missions,"*.json",SearchOption.AllDirectories).Length==2,"real Wake release installs map, support DLL and both missions to their distinct intended folders");
                string alps=Path.Combine(workspace,"server-maintenance","staged","rollout-20261029-release-audit","swissalps-0.4.1.zip");
                MapPackageInstaller.Install("BatongMaster/NOSwissAlps","swissalps.zip",alps,"sha256:"+ModUpdateFiles.Hash(alps),cache,plugins,missions,Array.Empty<string>(),CancellationToken.None);
                check(File.Exists(Path.Combine(plugins,"NOCustomMaps","maps","swissalps-0.4.1.nomapz")),"real Swiss Alps release installs its compressed map in the loader folder");
                MapPackageInstaller.Install("Talamander/ironjaw-sound","iron.zip",iron,"sha256:"+MapPackageInstaller.IronjawHash,cache,plugins,missions,Array.Empty<string>(),CancellationToken.None);
                check(File.Exists(Path.Combine(plugins,"IronjawSound","map_ironjawsound"))&&Directory.GetFiles(Path.Combine(plugins,"IronjawSound"),"*.dll").Length==4,"real Ironjaw release installs all four plugins and the map bundle together");
            }
        }
        finally{Directory.Delete(root,true);}
    }
}
