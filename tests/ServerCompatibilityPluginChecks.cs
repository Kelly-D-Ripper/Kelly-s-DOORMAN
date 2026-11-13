using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KellysJOINCHECK;

internal static class ServerCompatibilityPluginRegression
{
    private const string Blueprinter="com.nikkorap.blueprinter";
    private const string HostWire="0.34.1_2b9371678a89";
    private const string LocalWire="0.34.1_5e1096f3bc4f_rsl-v0.8.4-Default";
    // The eleven selected packs and exact versions from the tester's report.
    private const string Selected="0.34.1_com.nikkorap.blueprinter-v2.0.1_--1509_palafighter1-v1.0.1"
        +"_--aryx_expanded_weapons-v1.1.1_--aryx_f16m-v1.2.3_--aryx_interceptor1-v1.1.7.1"
        +"_--aryx_kingraptor-v1.0.4_--aryx_lightfighter1-v1.1.3_--aryx_lighthelicopter1-v1.1.1"
        +"_--aryx_mc260_chimera-v1.2.0_--aryx_mig-15-v1.1.3_--aryx_navalexpansion-v1.0.4"
        +"_--aryx_propattacker1-v1.0.1";

    internal static void Run(Action<bool,string> check)
    {
        string actual=ServerCompatibilityPlugins.Resolute;
        const string command="com.resolute.command";
        string[] installed={Blueprinter,actual,command,"client.ui","client.replay","com.Spiny.GrowlerFrit"};
        var selected=Signature.Parse(Selected);
        check(selected.Complete&&selected.Mods.Count==12,
            "the reported eleven-pack selection also includes the actual Blueprinter version");
        string evidence=ServerCompatibilityPlugins.BlueprinterOnlyWire(Selected);
        check(evidence==HostWire,
            "the tester's exact selected content signature independently produces the advertised host hash");
        check(evidence!=LocalWire&&evidence!=LocalWire.Substring(0,LocalWire.IndexOf("_rsl-v",StringComparison.Ordinal)),
            "diagnostic bundle hashing neither strips Resolute's suffix nor authenticates its already altered hash");
        check(ServerCompatibilityPlugins.BlueprinterOnlyWire(Selected.Replace("2.0.1","2.0.2"))==""
            &&ServerCompatibilityPlugins.BlueprinterOnlyWire(LocalWire)=="",
            "the projection cannot claim a known bundle hash from another loader version or an opaque raw wire");
        string reordered=Selected.Replace("--1509_palafighter1-v1.0.1_--aryx_expanded_weapons-v1.1.1",
            "--aryx_expanded_weapons-v1.1.1_--1509_palafighter1-v1.0.1");
        check(ServerCompatibilityPlugins.BlueprinterOnlyWire(reordered)!=HostWire,
            "bundle-order changes remain distinguishable instead of sorting away compatibility evidence");

        var exclusions=ServerCompatibilityPlugins.Exclusions(installed,HostWire,LocalWire);
        check(exclusions.SequenceEqual(new[]{actual})&&!exclusions.Contains(command),
            "the reported Resolute blocker targets its actual gameplay GUID rather than the separate command addon");
        check(ServerCompatibilityPlugins.Exclusions(installed.Where(id=>id!=actual),HostWire,LocalWire).Length==0,
            "a known-looking suffix without the actual installed plugin grants no automatic exclusion");
        check(ServerCompatibilityPlugins.Exclusions(new[]{"Resolute","com.resolute.command","COM.RESOLUTE.NUCLEAROPTION"},HostWire,LocalWire).Length==0,
            "registry names, command addon identity and similarly cased identifiers cannot stand in for the exact plugin GUID");
        check(ServerCompatibilityPlugins.Exclusions(installed,HostWire,HostWire).Length==0,
            "an installed but inactive Resolute plugin without its runtime tag is preserved");
        check(ServerCompatibilityPlugins.Exclusions(installed,LocalWire,LocalWire).Length==0
            &&ServerCompatibilityPlugins.Exclusions(installed,HostWire+"_rsl-v0.8.3-AirDefence",LocalWire).Length==0,
            "hosts using Resolute preserve it even when its version or outfit needs separate investigation");
        check(ServerCompatibilityPlugins.Exclusions(installed,"0.34.1",LocalWire).SequenceEqual(new[]{actual}),
            "the same explicit gameplay contribution can be excluded for an identified vanilla host");
        foreach(string malformed in new[]{"","unknown_2b9371678a89","0.34.1_unsupported","0.34.1_2b9371678a89\n"})
            check(ServerCompatibilityPlugins.Exclusions(installed,malformed,LocalWire).Length==0,
                "unverified host metadata cannot justify a Resolute exclusion: "+malformed);
        foreach(string malformed in new[]{"0.34.1_5e1096f3bc4f_rsl-vbad-Default",
            "0.34.1_5e1096f3bc4f_rsl-v0.8.4-","0.34.1_5e1096f3bc4f_rsl-v-Default",
            "unknown_5e1096f3bc4f_rsl-v0.8.4-Default","0.34.1_unsupported_rsl-v0.8.4-Default",
            LocalWire+"\n",LocalWire+new string('x',Protocol.MaxText)})
            check(!ServerCompatibilityPlugins.HasResolute(malformed)
                &&ServerCompatibilityPlugins.Exclusions(installed,HostWire,malformed).Length==0,
                "malformed or oversized local contributions never trigger a named exclusion: "+malformed.Substring(0,Math.Min(80,malformed.Length)));
        check(ServerCompatibilityPlugins.HasResolute("0.34.1_rsl-v0.8.4-Default")
            &&ServerCompatibilityPlugins.HasResolute("0.34.1_5e1096f3bc4f_rsl-v0.8.4-Air-Defence"),
            "bounded valid Resolute tags support both game-only prefixes and hyphenated outfit labels");
        check(!ServerCompatibilityPlugins.HasResolute("0.34.1_5e1096f3bc4f_rsl-v0.8.4-"+new string('x',512)),
            "oversized outfit metadata stays outside the bounded known-plugin policy");

        var graph=new[]{Pair(Blueprinter),Pair(actual),Pair(command,actual),Pair("command.extension",command),
            Pair("client.ui"),Pair("client.replay"),Pair("client.off"),Pair("com.Spiny.GrowlerFrit"),
            Pair("kelly.nuclearoption.joincheck"),Pair("kelly.nuclearoption.joincheck.server")};
        var usual=new[]{command,"client.off"};
        var next=ServerPluginSelection.Resolve(graph,usual.Concat(exclusions),Array.Empty<string>(),Array.Empty<string>(),false);
        check(next.Contains(actual)&&next.Contains(command)&&next.Contains("command.extension"),
            "excluding the base Resolute plugin also excludes its actual command addon and transitive dependents");
        check(next.Contains("client.off")&&!next.Contains("client.ui")&&!next.Contains("client.replay")
            &&!next.Contains("com.Spiny.GrowlerFrit")&&!next.Any(ModProfile.Doorman),
            "targeted server setup retains unknown client tools, native E/KR-67 policy and the protected manager");
        check(usual.SequenceEqual(new[]{command,"client.off"}),
            "temporary gameplay exclusion leaves the user's normal disabled list untouched");
        var disabled=new HashSet<string>(next,StringComparer.Ordinal);
        check(!ServerSetupFiles.KeepPlugin(actual,disabled,false)&&!ServerSetupFiles.KeepPlugin(command,disabled,false)
            &&ServerSetupFiles.KeepPlugin("client.ui",disabled,false)&&ServerSetupFiles.KeepPlugin(Blueprinter,disabled,false),
            "startup filtering removes the actual gameplay plugin while retaining the required loader and unrelated client UI");
        foreach(string protectedId in new[]{Blueprinter,"kelly.nuclearoption.joincheck","kelly.nuclearoption.joincheck.server"})
        {
            var conflict=graph.Select(edge=>edge.Key==protectedId?Pair(protectedId,actual):edge).ToArray();
            bool rejected=false;
            try {ServerPluginSelection.Resolve(conflict,exclusions,Array.Empty<string>(),Array.Empty<string>(),false);}
            catch(InvalidDataException){rejected=true;}
            // Required Blueprinter closure re-enables its dependency. The
            // caller must reject that contradiction instead of disabling it.
            if(protectedId==Blueprinter)
            {
                var required=ServerPluginSelection.Resolve(conflict,exclusions,Array.Empty<string>(),Array.Empty<string>(),false);
                check(exclusions.Any(id=>!required.Contains(id,StringComparer.Ordinal)),
                    "a required loader dependency exposes the conflicting exclusion for explicit plan rejection");
            }
            else check(rejected,"a compatibility exclusion cannot strand protected component "+protectedId);
        }

        var named=JoinSummary.Create(Selected,Selected,HostWire,LocalWire,"",exclusions);
        check(named.Status==JoinStatus.Changes&&named.Matching.Count==12&&named.Actions.Count==1
            &&named.Actions[0].Action=="DISABLE"&&named.Actions[0].Name=="Resolute"
            &&named.Actions[0].Instruction.Contains("this session")&&!named.Actions.Any(action=>action.Action=="CHECK FILES"),
            "known actual Resolute mismatch replaces the generic file warning with one named session-only action");
        var unknown=JoinSummary.Create(Selected,Selected,HostWire,LocalWire,"",
            ServerCompatibilityPlugins.Exclusions(installed.Where(id=>id!=actual),HostWire,LocalWire));
        check(unknown.Actions.Count==1&&unknown.Actions[0].Action=="CHECK FILES"&&unknown.Status!=JoinStatus.Match,
            "absence of the actual known plugin retains the unresolved raw-wire investigation");
        var metadata=Protocol.Encode(HostWire,Selected);
        string Read(string key)=>metadata.TryGetValue(key,out var value)?value:"";
        check(Protocol.Decode(Read,HostWire)==Selected&&Protocol.Decode(Read,LocalWire)==null,
            "diagnostic bundle projection leaves exact expected-wire checks intact for host metadata");
        check(LocalWire=="0.34.1_5e1096f3bc4f_rsl-v0.8.4-Default"&&HostWire!=LocalWire
            &&JoinSummary.Create(Selected,Selected,HostWire,LocalWire,"",exclusions).Status!=JoinStatus.Match,
            "a named disable action never authorises joining with the unchanged incompatible application wire");
        GrowlerChecks(check);
    }

    private static void GrowlerChecks(Action<bool,string> check)
    {
        const string growlerWire="0.34.1_5e1096f3bc4f";
        const string main="com.Spiny.GrowlerFrit",blocker="com.Spiny.MpBlocker",debugger="com.Spiny.Debugger";
        string[] family={main,blocker,"com.Spiny.JammerPod2","com.Spiny.ECMUpgrade","com.Spiny.SparkyDome",debugger};
        string physical=Path.GetFullPath(Path.Combine(Path.GetTempPath(),"doorman-ifrit-fixture","KR-EW.67.dll"));
        var sources=family.Select(id=>new KeyValuePair<string,string>(id,physical)).ToArray();
        var unit=ServerCompatibilityPlugins.GrowlerUnit(sources);
        string[] installed=family.Concat(new[]{Blueprinter,"client.hud","client.audio","client.replay",ServerCompatibilityPlugins.Resolute}).ToArray();
        string prefixed="0.34.1_GrowlerFrit-v1.0.0"+Selected.Substring("0.34.1".Length);
        using(var sha=SHA256.Create())
        {
            string reproduced="0.34.1_"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(prefixed)),0,6).Replace("-","").ToLowerInvariant();
            check(reproduced==growlerWire,
                "the reported post-Resolute Ifrit mismatch is independently reproduced by its exact pre-Blueprinter version token");
            check(ServerCompatibilityPlugins.GrowlerWire(Selected)==growlerWire,
                "known Ifrit contribution hashing handles injected prefix outside the canonical BP-only diagnostic parser");
        }
        check(family.Length==6&&unit.SequenceEqual(family)&&ServerCompatibilityPlugins.GrowlerComponents.SequenceEqual(family),
            "all six actual KR-EW.67 plugin GUIDs are grouped only through their shared main-plugin assembly path");
        var excluded=ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,Selected,unit);
        check(excluded.SequenceEqual(family)&&!excluded.Contains(Blueprinter)&&!excluded.Contains(ServerCompatibilityPlugins.Resolute),
            "verified exact Ifrit token mismatch excludes present pack components rather than loader or unrelated Resolute");
        check(ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,Selected).Length==0
            &&ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,"",unit).Length==0,
            "opaque Ifrit-looking hash cannot exclude anything without both actual physical group and selected content evidence");
        check(ServerCompatibilityPlugins.Exclusions(installed,HostWire,"0.34.1_aaaaaaaaaaaa",Selected,unit).Length==0
            &&ServerCompatibilityPlugins.Exclusions(installed,"0.34.1_bbbbbbbbbbbb",growlerWire,Selected,unit).Length==0,
            "different opaque client or host hash cannot be attributed to the known Ifrit token");
        check(ServerCompatibilityPlugins.Exclusions(installed,growlerWire,growlerWire,Selected,unit).Length==0
            &&ServerCompatibilityPlugins.Exclusions(installed,HostWire,HostWire,Selected,unit).Length==0,
            "host using Ifrit and inactive installed Ifrit preserve their current plugin set");
        check(ServerCompatibilityPlugins.Exclusions(installed.Where(id=>id!=main),HostWire,growlerWire,Selected,unit).Length==0
            &&ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,Selected,unit.Where(id=>id!=main)).Length==0,
            "main Ifrit identity must be installed and observed in the exact physical family");
        check(ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,Selected.Replace("aryx_f16m-v1.2.3","aryx_f16m-v1.2.2"),unit).Length==0
            &&ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,Selected.Replace("2.0.1","2.0.2"),unit).Length==0,
            "wrong selected content or unsupported loader version cannot justify known code exclusion");
        foreach(string bad in new[]{Selected+"\n",Selected+"_GrowlerFrit-v1.0.0","0.34.1_5e1096f3bc4f",new string('x',Protocol.MaxText+1)})
            check(ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,bad,unit).Length==0,
                "malformed or already modified diagnostic selection cannot supply Ifrit attribution");
        check(ServerCompatibilityPlugins.Exclusions(new[]{"KR-EW.67","GrowlerIfrit","COM.SPINY.GROWLERFRIT",blocker},HostWire,growlerWire,Selected,unit).Length==0,
            "display names, NOMM package name and case-changed GUIDs cannot stand in for actual Ifrit plugin identity");

        var both=ServerCompatibilityPlugins.Exclusions(installed,HostWire,LocalWire,Selected,unit);
        check(both.Contains(ServerCompatibilityPlugins.Resolute)&&family.All(both.Contains)&&both.Length==7,
            "independently proven Resolute and pre-hash Ifrit blockers can both be excluded in one restart");
        var hostResolute=ServerCompatibilityPlugins.Exclusions(installed,HostWire+"_rsl-v0.8.4-Default",LocalWire,Selected,unit);
        check(hostResolute.SequenceEqual(family)&&!hostResolute.Contains(ServerCompatibilityPlugins.Resolute),
            "server-required Resolute survives while only the additional client Ifrit pack is excluded");

        var standaloneDebugger=sources.Select(p=>p.Key==debugger?new KeyValuePair<string,string>(debugger,Path.Combine(Path.GetDirectoryName(physical)!,"DebugHelpers.dll")):p).ToArray();
        var separateUnit=ServerCompatibilityPlugins.GrowlerUnit(standaloneDebugger);
        var separateExcluded=ServerCompatibilityPlugins.Exclusions(installed,HostWire,growlerWire,Selected,separateUnit);
        check(separateUnit.Length==5&&!separateUnit.Contains(debugger)&&!separateExcluded.Contains(debugger),
            "standalone debugging component in another assembly remains outside the Ifrit physical family");
        var polluted=sources.Concat(new[]{new KeyValuePair<string,string>("client.unknown.same.dll",physical),
            new KeyValuePair<string,string>("client.ui",Path.Combine(Path.GetDirectoryName(physical)!,"HUD.dll"))}).ToArray();
        check(ServerCompatibilityPlugins.GrowlerUnit(polluted).SequenceEqual(family),
            "unknown plugin sharing a DLL is never added to the audited Ifrit exclusion family");
        var duplicateMain=sources.Concat(new[]{new KeyValuePair<string,string>(main,Path.Combine(Path.GetDirectoryName(physical)!,"OtherGrowler.dll"))});
        check(ServerCompatibilityPlugins.GrowlerUnit(duplicateMain).Length==0,
            "ambiguous main-plugin physical owners prevent family-based automatic exclusions");
        check(ServerCompatibilityPlugins.GrowlerUnit(sources.Where(p=>p.Key!=main)).Length==0
            &&ServerCompatibilityPlugins.GrowlerUnit(new[]{new KeyValuePair<string,string>(main,"KR-EW.67.dll")}).Length==0,
            "missing main owner or relative assembly path cannot establish actual plugin group");
        check(ServerCompatibilityPlugins.GrowlerUnit(Enumerable.Repeat(new KeyValuePair<string,string>(main,physical),513)).Length==0,
            "physical family discovery is bounded to the local metadata limit");
        var partialUnit=ServerCompatibilityPlugins.GrowlerUnit(new[]{new KeyValuePair<string,string>(main,physical)});
        check(ServerCompatibilityPlugins.Exclusions(new[]{main,Blueprinter,"client.hud"},HostWire,growlerWire,Selected,partialUnit).SequenceEqual(new[]{main}),
            "partial known family disables only actual observed components without inventing absent plugins");

        var graph=new[]{Pair(Blueprinter),Pair(main,blocker,"com.Spiny.JammerPod2","com.Spiny.ECMUpgrade","com.Spiny.SparkyDome"),
            Pair(blocker),Pair("com.Spiny.JammerPod2"),Pair("com.Spiny.ECMUpgrade"),Pair("com.Spiny.SparkyDome","com.Spiny.ECMUpgrade"),Pair(debugger),
            Pair("ifrit.addon",main),Pair("ifrit.deep.addon","ifrit.addon"),Pair("client.hud"),Pair("client.audio"),Pair("client.replay"),Pair("client.off"),
            Pair("kelly.nuclearoption.joincheck"),Pair("kelly.nuclearoption.joincheck.server")};
        var normal=new ModProfile {DisabledPlugins=new[]{"client.off"},DisabledContent=new[]{"singleplayer.extra"}};
        var resolved=ServerPluginSelection.Resolve(graph,normal.DisabledPlugins.Concat(excluded),Array.Empty<string>(),Array.Empty<string>(),false);
        check(family.All(resolved.Contains)&&resolved.Contains("ifrit.addon")&&resolved.Contains("ifrit.deep.addon"),
            "excluding actual Ifrit components also excludes hard dependent addons through transitive closure");
        check(!resolved.Contains(Blueprinter)&&!resolved.Contains("client.hud")&&!resolved.Contains("client.audio")&&!resolved.Contains("client.replay")
            &&resolved.Contains("client.off")&&!resolved.Any(ModProfile.Doorman),
            "Ifrit session preparation preserves protected manager and unrelated client tools with normal disabled preferences");
        check(normal.DisabledPlugins.SequenceEqual(new[]{"client.off"})&&normal.DisabledContent.SequenceEqual(new[]{"singleplayer.extra"}),
            "temporary Ifrit exclusion does not mutate the normal saved singleplayer mod profile");
        var disabled=new HashSet<string>(resolved,StringComparer.Ordinal);
        var discovery=sources.Concat(new[]{new KeyValuePair<string,string>("client.unknown.same.dll",physical)}).GroupBy(p=>p.Value,StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g=>g.Key,g=>g.Select(p=>p.Key).ToArray(),StringComparer.OrdinalIgnoreCase);
        var filtered=discovery[physical].Where(id=>ServerSetupFiles.KeepPlugin(id,disabled,false)).ToArray();
        check(filtered.SequenceEqual(new[]{"client.unknown.same.dll"})&&family.All(id=>!ServerSetupFiles.KeepPlugin(id,disabled,false)),
            "startup per-GUID filtering removes all six Ifrit controllers from one DLL while preserving unknown same-DLL identity");
        var loaderConflict=graph.Select(edge=>edge.Key==Blueprinter?Pair(Blueprinter,blocker):edge).ToArray();
        var needed=ServerPluginSelection.Resolve(loaderConflict,excluded,Array.Empty<string>(),Array.Empty<string>(),false);
        check(excluded.Any(id=>!needed.Contains(id,StringComparer.Ordinal)),
            "required loader dependency re-enabling an Ifrit component exposes contradiction for plan rejection");
        bool protectedConflict=false;
        try {ServerPluginSelection.Resolve(graph.Select(edge=>edge.Key=="kelly.nuclearoption.joincheck"?Pair(edge.Key,main):edge),excluded,Array.Empty<string>(),Array.Empty<string>(),false);}
        catch(InvalidDataException){protectedConflict=true;}
        check(protectedConflict,"Ifrit exclusion cannot disable a required protected DOORMAN dependency");

        var checklist=JoinSummary.Create(Selected,Selected,HostWire,growlerWire,"",excluded);
        check(checklist.Status==JoinStatus.Changes&&checklist.Matching.Count==12&&checklist.Actions.Count==1
            &&checklist.Actions[0].Action=="DISABLE"&&checklist.Actions[0].Name==ServerCompatibilityPlugins.GrowlerName
            &&checklist.Actions[0].Instruction.Contains("this session")&&!checklist.Actions.Any(action=>action.Action=="CHECK FILES"),
            "proven Ifrit mismatch replaces generic file warning with one explicit session-only pack action");
        check(ServerCompatibilityPlugins.Names(both).Contains("Resolute")&&ServerCompatibilityPlugins.Names(both).Contains(ServerCompatibilityPlugins.GrowlerName)
            &&ServerCompatibilityPlugins.Names(excluded).Split(new[]{ServerCompatibilityPlugins.GrowlerName},StringSplitOptions.None).Length==2,
            "six controller exclusions produce one player-facing Ifrit pack name");
        check(HostWire!=growlerWire&&checklist.Status!=JoinStatus.Match
            &&ServerCompatibilityPlugins.Exclusions(installed,HostWire,HostWire,Selected,unit).Length==0,
            "diagnosed Ifrit exclusion neither authorizes incompatible raw wire nor repeatedly disables it after successful restart");
    }

    private static KeyValuePair<string,string[]> Pair(string id,params string[] requires)
        =>new KeyValuePair<string,string[]>(id,requires);
}
