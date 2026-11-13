using System;
using System.IO;
using System.Linq;
using System.Text;
using KellysJOINCHECK;

internal static class RequisiteRegression
{
    internal static void Run(Action<bool,string> check)
    {
        var legacy=ModJson.ReadBytes<ModManifest>(Encoding.UTF8.GetBytes("{\"plugin\":\"old.plugin\",\"name\":\"Old mod\"}"));
        legacy.Validate();
        check(legacy.Requisites.Length==0,"legacy mod previews omit requisites without gaining requirements");
        var mapLegacy=MapPreviewManifest.Read(Encoding.UTF8.GetBytes("{\"map\":\"map.test\"}"),"map.test");
        check(mapLegacy.Requisites.Length==0,"legacy embedded map previews still parse without requisites");
        var json=ModJson.ReadBytes<ModManifest>(Encoding.UTF8.GetBytes("{\"plugin\":\"map.helper\",\"name\":\"Map helper\",\"requisites\":[{\"plugin\":\"com.javoski.custommaps\",\"name\":\"NOCustomMaps\",\"minimumVersion\":\"1.3.0\",\"repository\":\"BatongMaster/NOCustomMaps\",\"asset\":\"NOCustomMaps-*.zip\"}]}"));
        json.Validate();
        check(json.Requisites.Length==1&&json.Requisites[0].Plugin==ModRequisites.CustomMapLoaderPlugin&&json.Requisites[0].MinimumVersion=="1.3.0"&&json.Requisites[0].Asset=="NOCustomMaps-*.zip","optional JSON requisite retains exact identifier, minimum version and asset selector");

        void Rejected(ModRequisite[] values,string message)
        {
            bool rejected=false;
            try { ModRequisite.ValidateAll(values); } catch(InvalidDataException) { rejected=true; }
            check(rejected,message);
        }
        Rejected(new[]{new ModRequisite()},"empty requisite is rejected instead of discarded");
        Rejected(new[]{new ModRequisite { Plugin="x",Bundle="y" }},"requisite with two target identifiers is rejected");
        Rejected(new[]{new ModRequisite { Plugin=" x" }},"whitespace does not change requisite identity silently");
        Rejected(new[]{new ModRequisite { Plugin="x\n" }},"control bytes cannot enter requisite identity");
        Rejected(new[]{new ModRequisite { Plugin="x",MinimumVersion="latest" }},"noncomparable required version is rejected");
        Rejected(new[]{new ModRequisite { Plugin="x",Repository="https://github.com/BatongMaster/NOCustomMaps" }},"requisite repository uses owner/repository rather than arbitrary URL");
        Rejected(new[]{new ModRequisite { Plugin="x",Asset="x.dll" }},"asset selector without repository is rejected");
        Rejected(new[]{new ModRequisite { Plugin="x",Repository="owner/repo",Asset="../x.dll" }},"requisite asset cannot select a path");
        Rejected(new ModRequisite[]{null!},"null prerequisite item cannot silently remove a requirement");
        Rejected(new[]{new ModRequisite { Plugin="x" },new ModRequisite { Plugin="x",MinimumVersion="2.0" }},"duplicate JSON target declarations are rejected");
        Rejected(Enumerable.Range(0,17).Select(i=>new ModRequisite { Plugin="p."+i }).ToArray(),"prerequisite array is bounded at sixteen");
        check(ModRequisite.ValidateAll(Enumerable.Range(0,16).Select(i=>new ModRequisite { Plugin="p."+i }).ToArray()).Length==16,"sixteen valid prerequisites are supported");
        var longestMap="{\"map\":\"map.test\",\"requisites\":["+string.Join(",",Enumerable.Range(0,16).Select(i=>"{\"plugin\":\"p."+i+"\",\"name\":\"Plugin "+i+"\",\"minimumVersion\":\"1.0\",\"repository\":\"owner/repo\",\"asset\":\"plugin.dll\"}"))+"]}";
        check(MapPreviewManifest.Read(Encoding.UTF8.GetBytes(longestMap),"map.test").Requisites.Length==16,"map serializer graph permits all sixteen fully described requirements");
        var invalidCard=ModJson.ReadBytes<ModManifest>(Encoding.UTF8.GetBytes("{\"plugin\":\"broken.card\",\"name\":\"Broken dependency card\",\"description\":\"Keep this visible\",\"requisites\":[{\"plugin\":\"x\",\"bundle\":\"y\"}]}"));
        invalidCard.Validate();
        check(invalidCard.RequisiteError.Length>0&&invalidCard.Description=="Keep this visible"&&invalidCard.Requisites.Length==1,"malformed mod requisites retain the card with an explicit error instead of discarding requirements");
        var invalidMap=MapPreviewManifest.Read(Encoding.UTF8.GetBytes("{\"map\":\"map.test\",\"description\":\"Keep map visible\",\"requisites\":[null]}"),"map.test");
        check(invalidMap.RequisiteError.Length>0&&invalidMap.Description=="Keep map visible"&&ModRequisites.ForMap(invalidMap.Requisites,Array.Empty<ModEntry>()).Single().Status==ModRequisiteStatus.Invalid,"malformed map requirement remains visible and fails closed in the evaluator");
        check(ModRequisites.ForMod(new ModEntry { Id="broken.card",Manifest=invalidCard },Array.Empty<ModEntry>()).Single().Status==ModRequisiteStatus.Invalid,"malformed mod requisite card cannot falsely appear dependency-ready");

        var parent=Plugin("parent");
        parent.Manifest=new ModManifest { Name="Parent",Plugin="parent",Requisites=new[]{Require("needed",repository:"owner/needed")} };
        var missing=ModRequisites.ForMod(parent,new[]{parent}).Single();
        check(missing.Status==ModRequisiteStatus.Missing&&missing.CanDownload&&missing.Message.Contains("not installed")&&missing.Dependency==null,"missing declared plugin exposes accurate explanation and a download source");
        var noSource=ModRequisites.Evaluate(new[]{Require("unknown")},new[]{parent}).Single();
        check(noSource.Status==ModRequisiteStatus.Missing&&!noSource.CanDownload,"a missing plugin without a source remains flagged without inventing a download");
        var dependency=Plugin("needed");
        check(ModRequisites.ForMod(parent,new[]{parent,dependency}).Length==0,"loaded selected prerequisite satisfies a mod declaration");
        check(ModRequisites.DeclarationsForMod(parent,new[]{parent,dependency}).Single().Plugin=="needed","healthy prerequisite remains available to display a Requires flag");
        dependency.Enabled=false;dependency.Wanted=false;
        var disabled=ModRequisites.ForMod(parent,new[]{parent,dependency}).Single();
        check(disabled.Status==ModRequisiteStatus.Disabled&&ReferenceEquals(disabled.Dependency,dependency)&&!disabled.CanDownload,"installed disabled prerequisite needs enabling rather than downloading");
        dependency.Enabled=true;
        check(ModRequisites.ForMod(parent,new[]{parent,dependency}).Single().Message.Contains("selected to be disabled"),"loaded prerequisite selected for disabling is not reported ready");
        dependency.Enabled=false;dependency.Wanted=true;
        var pending=ModRequisites.ForMod(parent,new[]{parent,dependency}).Single();
        check(pending.Status==ModRequisiteStatus.RestartRequired&&pending.Message.Contains("not loaded"),"pending plugin enable explains that the loaded runtime has not changed");
        check(ModRequisites.ValidateSelection(new[]{parent,dependency})=="","selected pending plugin can pass apply validation and request a restart");
        dependency.Wanted=false;
        check(ModRequisites.ValidateSelection(new[]{parent,dependency}).Contains("disabled"),"selected mod cannot apply while prerequisite remains unselected");
        parent.Wanted=false;
        check(ModRequisites.ValidateSelection(new[]{parent,dependency})=="","unselected mods do not block applying another set");
        parent.Wanted=true;dependency.Wanted=true;dependency.Enabled=true;

        parent.Manifest.Requisites[0].MinimumVersion="1.2.0";
        dependency.Version="1.1.9";
        var old=ModRequisites.ForMod(parent,new[]{parent,dependency}).Single();
        check(old.Status==ModRequisiteStatus.WrongVersion&&old.CanDownload&&old.Message.Contains("1.2.0")&&old.Message.Contains("1.1.9"),"outdated prerequisite identifies both installed and required versions");
        dependency.Version="1.2.0";
        check(ModRequisites.ForMod(parent,new[]{parent,dependency}).Length==0,"exact minimum version is accepted");
        dependency.Version="1.3.0-beta.1";
        check(ModRequisites.ForMod(parent,new[]{parent,dependency}).Length==0,"newer prerelease remains above an older stable minimum");
        dependency.Version="1.2.0-beta.1";
        check(ModRequisites.ForMod(parent,new[]{parent,dependency}).Single().Status==ModRequisiteStatus.WrongVersion,"prerelease does not satisfy the matching stable version minimum");
        dependency.Version="mystery";
        check(ModRequisites.ForMod(parent,new[]{parent,dependency}).Single().Message.Contains("could not be verified"),"unparseable installed version does not falsely satisfy a minimum");
        dependency.Version="1.2.0";
        var duplicate=Plugin("needed");
        var ambiguous=ModRequisites.ForMod(parent,new[]{parent,dependency,duplicate}).Single();
        check(ambiguous.Status==ModRequisiteStatus.Ambiguous&&ambiguous.Dependency==null&&!ambiguous.CanDownload,"duplicate installed IDs block unsafe prerequisite resolution");
        check(ModRequisites.ForMod(parent,new[]{parent,Plugin("NEEDED")}).Single().Status==ModRequisiteStatus.Missing,"prerequisite stable identifiers remain case-sensitive");

        var content=new ModEntry { Id="content.required",Content=true,Name="Required content",Version="2.0",Enabled=true,Wanted=true };
        var bundleRequirement=new ModRequisite { Bundle=content.Id,Name=content.Name,MinimumVersion="2.0" };
        check(ModRequisites.Evaluate(new[]{bundleRequirement},new[]{content}).Length==0,"bundle prerequisite matches content identity and its actual version");
        check(ModRequisites.Evaluate(new[]{bundleRequirement},new[]{Plugin(content.Id)}).Single().Status==ModRequisiteStatus.Missing,"plugin with the same ID cannot satisfy a bundle prerequisite");
        content.Enabled=false;
        check(ModRequisites.Evaluate(new[]{bundleRequirement},new[]{content}).Single().Status==ModRequisiteStatus.RestartRequired,"selected unloaded content does not appear runtime-ready");

        var wrapper=Plugin("wrapper");wrapper.Path="C:\\plugins\\Wrapper.dll";wrapper.Requires.Add("hard.required");
        var pack=new ModEntry { Id="pack",Name="Pack",Content=true,Enabled=true,Wanted=true,WrapperPlugin=wrapper.Id,WrapperPath=wrapper.Path,Version="3.0" };
        var wrapperInventory=new[]{pack,wrapper};
        check(ModMenuEntries.Visible(wrapperInventory).Count==1,"prerequisite test uses a wrapper hidden by menu projection");
        var inherited=ModRequisites.ForMod(pack,wrapperInventory).Single();
        check(inherited.Requirement.Plugin=="hard.required"&&inherited.Via.Contains("wrapper"),"hidden wrapper hard dependency remains visible through the content pack");
        check(ModRequisites.DeclarationsForMod(pack,wrapperInventory).Single().Plugin==wrapper.Id,"content pack records its supporting wrapper as a direct prerequisite");
        wrapper.RequiredRequisites.Add(Require("hard.required","2.0"));
        var hard=Plugin("hard.required");hard.Version="1.0";
        check(ModRequisites.ForMod(pack,new[]{pack,wrapper,hard}).Single().Status==ModRequisiteStatus.WrongVersion,"hard loader metadata retains minimum version beyond the legacy ID list");

        var a=Plugin("a");var b=Plugin("b");
        a.Requires.Add("b");b.Requires.Add("a");
        check(ModRequisites.ForMod(a,new[]{a,b}).Length==0,"loaded mutual prerequisites are traversed without an infinite cycle or false missing warning");
        b.Requires.Add("leaf");
        check(ModRequisites.ForMod(a,new[]{a,b}).Single().Requirement.Plugin=="leaf","missing transitive prerequisite is detected even after traversing a cycle");
        a.Requires.Add("leaf");
        check(ModRequisites.ForMod(a,new[]{a,b}).Count(i=>i.Requirement.Plugin=="leaf")==1,"shared missing prerequisite is reported once");
        b.Requires.Remove("leaf");a.Requires.Remove("leaf");
        a.Manifest=new ModManifest { Plugin="a",Name="A",Requisites=new[]{Require("shared","1.0")} };
        b.Manifest=new ModManifest { Plugin="b",Name="B",Requisites=new[]{Require("shared","2.0")} };
        var shared=Plugin("shared");shared.Version="1.5";
        check(ModRequisites.ForMod(a,new[]{a,b,shared}).Single().Requirement.MinimumVersion=="2.0","a stronger transitive minimum is checked after a weaker shared edge");

        var mapMissing=ModRequisites.ForMap(null,Array.Empty<ModEntry>()).Single();
        check(mapMissing.Requirement.Plugin==ModRequisites.CustomMapLoaderPlugin&&mapMissing.Requirement.Repository=="BatongMaster/NOCustomMaps"&&mapMissing.CanDownload,"every nomap map detects and can download NOCustomMaps even without a preview JSON");
        var mapLoader=Plugin(ModRequisites.CustomMapLoaderPlugin);
        check(ModRequisites.ForMap(null,new[]{mapLoader}).Length==0,"installed loaded NOCustomMaps satisfies the implicit map prerequisite");
        var mapDeclared=new[]{new ModRequisite { Plugin=ModRequisites.CustomMapLoaderPlugin,MinimumVersion="1.3.0" },Require("map.extra")};
        check(ModRequisites.DeclarationsForMap(mapDeclared).Count(r=>r.Plugin==ModRequisites.CustomMapLoaderPlugin)==1,"explicit map loader declaration merges with the built-in requirement once");
        var explicitLoader=ModRequisites.DeclarationsForMap(mapDeclared).Single(r=>r.Plugin==ModRequisites.CustomMapLoaderPlugin);
        check(explicitLoader.MinimumVersion=="1.3.0"&&explicitLoader.Repository=="BatongMaster/NOCustomMaps","implicit map download details preserve an explicit stronger minimum");
        mapLoader.Version="1.3.0";
        check(ModRequisites.ForMap(mapDeclared,new[]{mapLoader}).Single().Requirement.Plugin=="map.extra","optional map declarations are checked in addition to its loader");
        var conflict=ModRequisites.ForMap(new[]{new ModRequisite { Plugin=ModRequisites.CustomMapLoaderPlugin,Repository="other/untrusted" }},new[]{mapLoader}).Single();
        check(conflict.Status==ModRequisiteStatus.Invalid&&!conflict.CanDownload,"conflicting sources for the same required loader cannot select an arbitrary download");

        var graph=Enumerable.Range(0,520).Select(i=>Plugin("graph."+i)).ToArray();
        for(int i=0;i<graph.Length-1;i++)graph[i].Requires.Add(graph[i+1].Id);
        check(ModRequisites.ForMod(graph[0],graph).Any(i=>i.Status==ModRequisiteStatus.Invalid&&i.Message.Contains("too large")),"excessive dependency graphs fail closed within the traversal bound");
        check(dependency.Enabled&&dependency.Wanted&&parent.Manifest.Requisites[0].Plugin=="needed","evaluation never changes loaded state, wanted selection or source declarations");
    }

    private static ModEntry Plugin(string id)=>new ModEntry { Id=id,Name=id,Version="1.0",Enabled=true,Wanted=true,Path="C:\\plugins\\"+id+".dll" };
    private static ModRequisite Require(string plugin,string minimum="",string repository="")=>new ModRequisite { Plugin=plugin,MinimumVersion=minimum,Repository=repository };
}
