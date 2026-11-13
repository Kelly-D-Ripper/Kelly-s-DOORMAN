using System;
using System.Collections.Generic;
using System.Linq;
using KellysJOINCHECK;

internal static class ModMenuRegression
{
    internal static void Run(Action<bool,string> check)
    {
        var doorman=Plugin("kelly.nuclearoption.joincheck","C:\\plugins\\KellysJOINCHECK.dll");doorman.Version="1.4.0";
        check(ModMenuEntries.DisplayVersion(doorman)==SelfUpdateRelease.Installed&&doorman.Version=="1.4.0","DOORMAN's list and details use the full candidate version without changing plugin metadata");
        var ordinary=Plugin("other.plugin","C:\\plugins\\Other.dll");ordinary.Version="2.3.4";
        check(ModMenuEntries.DisplayVersion(ordinary)=="2.3.4","other plugins retain their own displayed versions");
        var content=new ModEntry{Id="kelly.nuclearoption.joincheck",Content=true,Version="5.6.7"};
        check(ModMenuEntries.DisplayVersion(content)=="5.6.7","a content pack cannot inherit DOORMAN's displayed version");
        var wrapper=Plugin("aryx.shrike","C:\\plugins\\Shrike.dll");
        var pack=Pack("aryx_f99",wrapper);
        pack.Version="1.3.1";wrapper.Version="1.0.0";
        var inventory=new List<ModEntry>{pack,wrapper};
        var shown=ModMenuEntries.Visible(inventory);
        check(shown.Count==1&&ReferenceEquals(shown[0],pack),"Mods shows one content card for an enabled installed wrapper");
        check(pack.Version=="1.3.1"&&wrapper.Version=="1.0.0","Mods projection preserves actual content and wrapper versions");
        check(inventory.Count==2&&ReferenceEquals(inventory[1],wrapper),"hidden wrapper remains in the full installed inventory");
        shown[0].Wanted=false;
        check(!pack.Wanted&&wrapper.Wanted,"content checkbox edits the original pack and keeps supporting plugin selected");
        check(ModMenuEntries.Visible(inventory).Count==1,"disabling content does not bring back a redundant enabled wrapper row");
        pack.Enabled=false;
        shown=ModMenuEntries.Visible(inventory);
        check(shown.Count==1&&ReferenceEquals(shown[0],pack),"disabled cached content remains visible to re-enable");
        shown[0].Wanted=true;
        check(pack.Wanted&&!pack.Enabled&&wrapper.Enabled,"re-enabling cached content is pending without mutating its loaded state");

        wrapper.Enabled=false;
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"disabled wrapper stays visible so its plugin can be re-enabled");
        wrapper.Enabled=true;wrapper.Wanted=false;
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"wrapper pending disable stays visible until restart");
        wrapper.Enabled=false;
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"disabled unwanted wrapper stays visible");
        wrapper.Wanted=true;
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"wrapper pending enable stays visible until restart");
        wrapper.Enabled=true;

        var second=Pack("aryx_extra",wrapper);
        shown=ModMenuEntries.Visible(new[]{pack,wrapper,second});
        check(shown.SequenceEqual(new[]{pack,second}),"one wrapper with multiple content packs keeps every pack in original order");
        var client=Plugin("client.only","C:\\plugins\\Client.dll");
        var loose=new ModEntry{Id="loose.pack",Content=true,Enabled=true,Wanted=true,Path="C:\\plugins\\Loose.nobp"};
        shown=ModMenuEntries.Visible(new[]{client,loose,wrapper,pack});
        check(shown.SequenceEqual(new[]{client,loose,pack}),"pure client plugins and standalone bundles remain visible");

        var duplicate=Plugin(wrapper.Id,wrapper.Path);
        check(ModMenuEntries.Visible(new[]{pack,wrapper,duplicate}).Count==3,"duplicate installed wrapper GUIDs keep all ambiguous rows visible");
        var cohost=Plugin("different.plugin",wrapper.Path);
        var unowned=new ModEntry{Id="unowned",Content=true,Enabled=true,Wanted=true,Path="resource:SharedAssembly:pack"};
        check(ModMenuEntries.Visible(new[]{unowned,wrapper,cohost}).Count==3,"multi-plugin DLL without unique recorded ownership is not grouped by filename");

        var bote=Plugin("com.minec.bote","C:\\plugins\\Bote.dll");
        var inputs=Plugin("com.minec.bote.inputs","c:\\PLUGINS\\BOTE.DLL");
        var shared=new[]{inputs,bote};
        check(ModMenuEntries.Visible(shared).SequenceEqual(new[]{bote}),"same DLL and unambiguous plugin family shows the root component once");
        check(ModMenuEntries.PluginGroup(inputs,shared).SequenceEqual(new[]{bote,inputs}),"a sibling resolves to the same deterministic group");
        check(!ModMenuEntries.ToggleGroup(bote,shared)&&shared.All(e=>!e.Wanted),"disabling grouped component disables every member");
        inputs.Wanted=true;
        check(ModMenuEntries.ToggleGroup(bote,shared)&&shared.All(e=>e.Wanted),"enabling a partially selected group restores all components");
        var unrelated=Plugin("other.component",bote.Path);
        check(ModMenuEntries.Visible(new[]{bote,inputs,unrelated}).Count==3,"unrelated plugin namespaces in the same file remain separate");
        var otherFile=Plugin("com.minec.bote.extra","C:\\plugins\\other.dll");
        check(ModMenuEntries.Visible(new[]{bote,otherFile}).Count==2,"matching namespaces in different files remain separate");
        var duplicateBote=Plugin(bote.Id,"C:\\plugins\\copy.dll");
        check(ModMenuEntries.Visible(new[]{bote,inputs,duplicateBote}).Count==3,"duplicate plugin IDs prevent grouping and retain recovery rows");
        var protectedSibling=Plugin("kelly.nuclearoption.joincheck",bote.Path);protectedSibling.Locked=true;
        check(ModMenuEntries.Visible(new[]{bote,inputs,protectedSibling}).Count==3,"protected plugins prevent grouping a shared file");
        var ambiguousPack=Pack("content.family",bote);
        check(ModMenuEntries.Visible(new[]{bote,inputs,ambiguousPack}).Contains(bote),"an attached content row cannot hide the grouped plugin controls");
        var wholePackage=new[]{ambiguousPack,inputs,bote};
        check(ModMenuEntries.Visible(wholePackage).SequenceEqual(new[]{bote}),"one embedded pack and its same-DLL plugin family appear as one package");
        check(!ModMenuEntries.ToggleGroup(bote,wholePackage)&&wholePackage.All(e=>!e.Wanted),"package disable includes the embedded pack and every plugin");
        check(ModMenuEntries.ToggleGroup(bote,wholePackage)&&wholePackage.All(e=>e.Wanted),"package enable restores the embedded pack and every plugin");
        var extraPack=Pack("content.extra",bote);
        check(ModMenuEntries.Visible(new[]{bote,inputs,ambiguousPack,extraPack}).Count==3,"multiple embedded packs remain individually selectable");
        var otherPack=Pack("content.other",bote);otherPack.WrapperPath="C:\\plugins\\other.dll";
        check(ModMenuEntries.Visible(new[]{bote,inputs,otherPack}).Count==2,"content from another DLL is not grouped");

        foreach(string protectedId in new[]{"kelly.nuclearoption.joincheck","kelly.nuclearoption.joincheck.server","com.nikkorap.blueprinter"})
        {
            var required=Plugin(protectedId,"C:\\plugins\\Required.dll");
            check(ModMenuEntries.Visible(new[]{required,Pack("required.content",required)}).Contains(required),"required plugin remains visible: "+protectedId);
        }

        pack.WrapperPath="C:\\plugins\\Elsewhere.dll";
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"ownership path disagreement never hides a wrapper");
        pack.WrapperPath="c:\\PLUGINS\\shrike.DLL";
        check(!ModMenuEntries.Visible(inventory).Contains(wrapper),"Windows ownership path comparison tolerates casing");
        pack.WrapperPlugin=wrapper.Id.ToUpperInvariant();
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"plugin identifiers remain exact and case-sensitive");
        pack.WrapperPlugin=wrapper.Id;pack.WrapperPath="";
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"missing content ownership path never hides a wrapper");
        wrapper.Path="";
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"blank ownership paths cannot accidentally match");
        wrapper.Id="";pack.WrapperPlugin="";
        check(ModMenuEntries.Visible(inventory).Contains(wrapper),"blank plugin identifiers cannot accidentally match");

        var profile=new ModProfile{DisabledContent=new[]{"disabled.content"},DisabledPlugins=new[]{"disabled.plugin"},AutoMatchServers=false};
        var disabledPlugin=Plugin("disabled.plugin","C:\\plugins\\Disabled.dll");disabledPlugin.Enabled=false;disabledPlugin.Wanted=false;
        var disabledPack=Pack("disabled.content",disabledPlugin);disabledPack.Enabled=false;disabledPack.Wanted=false;
        var snapshot=new[]{disabledPack,disabledPlugin};
        var contentIds=profile.DisabledContent;var pluginIds=profile.DisabledPlugins;
        shown=ModMenuEntries.Visible(snapshot);
        check(shown.SequenceEqual(snapshot)&&shown.All(e=>!e.Enabled&&!e.Wanted),"projection leaves disabled inventory state unchanged");
        check(ReferenceEquals(profile.DisabledContent,contentIds)&&ReferenceEquals(profile.DisabledPlugins,pluginIds)&&!profile.AutoMatchServers&&contentIds.Single()=="disabled.content"&&pluginIds.Single()=="disabled.plugin","projection never rewrites the saved mod profile");
        check(ModMenuEntries.Visible(Array.Empty<ModEntry>()).Count==0,"empty installed inventory produces an empty menu");
    }

    private static ModEntry Plugin(string id,string path)=>new ModEntry{Id=id,Name=id,Path=path,Enabled=true,Wanted=true,Locked=false,PreviewAmbiguous=false,Manifest=new ModManifest{Name=id}};
    private static ModEntry Pack(string id,ModEntry wrapper)=>new ModEntry{Id=id,Name=id,Content=true,Path="resource:ExampleAssembly:"+id,WrapperPlugin=wrapper.Id,WrapperPath=wrapper.Path,Enabled=true,Wanted=true};
}
