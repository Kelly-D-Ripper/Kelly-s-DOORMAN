using System;
using System.Linq;

namespace KellysJOINCHECK
{
    internal static class GameplayFingerprintUi
    {
        internal const string GrowlerName="E/Kr-67 (Ifrit remake)";
        internal static bool IsGrowlerComponent(string id)
        {
            switch(id)
            {
                case "com.Spiny.GrowlerFrit":case "com.Spiny.MpBlocker":case "com.Spiny.JammerPod2":
                case "com.Spiny.ECMUpgrade":case "com.Spiny.SparkyDome":case "com.Spiny.Debugger":return true;
                default:return false;
            }
        }
        internal static string Name(string id)
        {
            if(IsGrowlerComponent(id))return GrowlerName;
            switch(id)
            {
                case "aryx_cargoplane1":case "aryx_mc260_chimera":return "MC-260 Chimera";
                case "pb22trident":return "PB-22 Trident";
                case "com.ial.mig15s":return "MiG-15S";
                case "com.ial.oa27variant":return "OA-27 variants";
                case "com.nuclearoption.fs20plus":return "Supervore / FS-20+";
                case "com.ta30plus.northstar":return "Northstar / T/A-30+";
                case "com.mursisru.x77warewind":return "X-77 Warewind";
                case "com.iallemege.kh38mt":return "KH38MT";
                case "com.resolute.nuclearoption":return "Resolute";
                default:return Diagnostics.Clean(id).Replace('_',' ');
            }
        }
        internal static void Apply(JoinSummary view,GameplayFingerprintComparison compared,bool required)
        {
            // Optional checks and loader health remain in Copy report. Do not
            // present them as a reason the player cannot join.
            if(!required)return;
            if(!compared.Available)
            {
                // A separate confirmed game, map or mod problem remains the
                // primary action while the exact-file check is unavailable.
                if(view.Status==JoinStatus.Changes&&view.Actions.Count>0)return;
                view.Status=JoinStatus.Unknown;view.Title="Can't check mod files";
                view.Help="Try Setup mods. If the check still fails, copy the report.";return;
            }
            if(compared.Verified)return;
            view.Actions.RemoveAll(action=>action.Action=="CHECK FILES");
            var family=compared.Differences.Where(difference=>difference.Kind=="plugin"&&IsGrowlerComponent(difference.Id)).ToArray();
            if(family.Length>0)
            {
                view.Actions.RemoveAll(action=>action.Name==GrowlerName);
                bool different=family.Any(difference=>difference.Type==GameplayFingerprintDifferenceType.Different);
                bool missing=family.Any(difference=>difference.Type==GameplayFingerprintDifferenceType.Missing);
                view.Actions.Add(new ModAction {Name=GrowlerName,Action=different?"DIFFERENT FILE":missing?"ENABLE / INSTALL":"DISABLE",
                    Instruction=different?"Your E/Kr-67 DLL differs from the server's. Get the host's exact file before joining.":
                        missing?"Use the host's exact E/Kr-67 DLL. Setup mods can enable an installed matching copy.":
                        "Setup mods disables the whole Ifrit remake and its bundled components for this server. A restart is required."});
            }
            foreach(var difference in compared.Differences.Where(difference=>difference.Kind!="plugin"||!IsGrowlerComponent(difference.Id)))
            {
                string name=Name(difference.Id);
                string action=difference.Type==GameplayFingerprintDifferenceType.Extra?(difference.Kind=="plugin"?"DISABLE":"REMOVE"):
                    difference.Type==GameplayFingerprintDifferenceType.Missing?(difference.Kind=="plugin"?"ENABLE / INSTALL":"INSTALL"):"DIFFERENT FILE";
                string instruction=difference.Type==GameplayFingerprintDifferenceType.Extra?"Setup mods excludes this from the server session. Gameplay DLL changes require a restart.":
                    difference.Type==GameplayFingerprintDifferenceType.Missing?"Use the host's exact file. Setup mods can enable an installed matching copy.":
                    "Your file differs from the server's"+(difference.ServerVersion==difference.LocalVersion?", even though the version number matches.":".")+" Get the host's exact file before joining.";
                if(!view.Actions.Exists(item=>item.Name==name&&item.Action==action))view.Actions.Add(new ModAction { Action=action,Name=name,Instruction=instruction });
            }
            view.Status=JoinStatus.Changes;view.Title=view.Actions.Count+(view.Actions.Count==1?" change needed":" changes needed");
            view.Help="Click Setup mods, then restart if asked.";
        }
    }
}
