using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using KellysJOINCHECK;
using Mono.Cecil;

internal static class DoormanManifestRegression
{
    internal static void Run(Action<bool,string> check,AssemblyDefinition client,string clientPath)
    {
        const string identity="kelly.nuclearoption.joincheck",repository="Kelly-D-Ripper/Kelly-s-DOORMAN";
        var resources=client.MainModule.Resources.Where(r=>r.Name==EmbeddedModPreview.ManifestResource).ToArray();
        check(resources.Length==1 && resources[0] is EmbeddedResource,"the built DOORMAN client contains one embedded doorman.json resource");
        var card=EmbeddedModPreview.Read(client,clientPath);
        check(card!=null,"the built client's own preview is readable through the normal embedded-preview path");
        if (card==null) return;
        check(card.Plugin==identity && card.Bundle=="" && card.Name=="Kelly's DOORMAN" && card.Author=="Kelly D. Ripper","DOORMAN's compiled preview has its proven identity, display name and author");
        check(card.EmbeddedAssembly==Path.GetFullPath(clientPath) && card.EmbeddedModule==client.MainModule.Mvid,"DOORMAN's preview is bound to the actual built client rather than a sample fixture");
        bool artwork=client.MainModule.Resources.Any(r=>r.Name==EmbeddedModPreview.DefaultImageResource);
        var image=card.ReadImageBytes();
        check(artwork?card.EmbeddedImage==EmbeddedModPreview.DefaultImageResource&&image!=null&&ModImageHeader.Allowed(image):card.Image==""&&card.EmbeddedImage==""&&image==null,"DOORMAN's optional artwork is valid or safely absent without a placeholder");
        check(card.Links.All(l=>ModManifest.SafeLink(l.Url)&&GitHubRepository.FromUrl(l.Url)==repository)
            && card.Links.Any(l=>l.Label=="GitHub"&&l.Url=="https://github.com/"+repository)
            && card.Links.Any(l=>l.Label=="Downloads"&&l.Url=="https://github.com/"+repository+"/releases"),"DOORMAN's preview supplies its GitHub repository and complete package downloads without an invented community link");
        check(card.Update?.Repository==repository && ModProfile.Protected(card.Plugin),"DOORMAN supplies its GitHub check source while remaining a protected plugin");

        var plugin=Types(client.MainModule.Types).SelectMany(t=>t.CustomAttributes)
            .Where(a=>a.AttributeType.FullName=="BepInEx.BepInPlugin" && a.ConstructorArguments.Count==3 && a.ConstructorArguments[0].Value as string==identity).Single();
        string version=plugin.ConstructorArguments[2].Value as string??"";
        check(plugin.ConstructorArguments[1].Value as string==card.Name && Version.TryParse(version,out var parsed)
            && parsed.Major==client.Name.Version.Major && parsed.Minor==client.Name.Version.Minor && parsed.Build==client.Name.Version.Build,
            "the displayed DOORMAN identity and version come from matching compiled BepInPlugin metadata");
        using (var stream=((EmbeddedResource)resources[0]).GetResourceStream())
        using (var json=JsonDocument.Parse(stream))
        {
            check(!json.RootElement.EnumerateObject().Any(p=>p.Name.Equals("version",StringComparison.OrdinalIgnoreCase)),"the embedded preview does not duplicate DOORMAN's release version in JSON");
        }

        string description=card.Description;
        check(Has(description,@"mods?") && Has(description,@"maps?"),"DOORMAN's own description explains both Mods and Maps views");
        check(Has(description,@"lists?") && Has(description,@"sav(?:e|ed|ing)"),"DOORMAN's own description explains saved mod lists");
        check(Has(description,@"servers?") && (Has(description,@"setup") || Regex.IsMatch(description,@"\bset\s+up\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant) || Has(description,@"match(?:ing)?")),"DOORMAN's own description explains server setup or matching");
        check(Has(description,@"favou?rites?"),"DOORMAN's own description explains saved favourites");
        check(Has(description,@"versions?") && (Has(description,@"checks?") || Has(description,@"mismatch(?:es)?") || Has(description,@"compatibility") || Has(description,@"diagnostics?")),"DOORMAN's own description explains version checks or diagnostics");
        check(Has(description,@"restart(?:s|ing)?") && (Has(description,@"install(?:s|ed|ing)?") || Has(description,@"updates?")),"DOORMAN's own description explains restarting to install changes or updates");

        bool protectedUpdate=false;
        try
        {
            new PendingModUpdate { Plugin=identity,Repository=repository,Version=version,Target="KellysJOINCHECK.dll",OldHash=new string('a',64),NewHash=new string('b',64) }.Validate();
        }
        catch (InvalidDataException) { protectedUpdate=true; }
        check(protectedUpdate,"DOORMAN's newly visible release link cannot queue a protected plugin DLL-only self-update");
    }

    private static bool Has(string text,string words) => Regex.IsMatch(text,@"\b(?:"+words+@")\b",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    { foreach (var type in types) { yield return type;foreach (var nested in Types(type.NestedTypes)) yield return nested; } }
}
