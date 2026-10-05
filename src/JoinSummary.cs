using System;
using System.Collections.Generic;
using System.Linq;

namespace KellysJOINCHECK
{
    internal enum JoinStatus { Match, Changes, Unknown }
    internal sealed class ModAction
    {
        public string Action = "", Name = "", Instruction = "";
    }
    internal sealed class JoinSummary
    {
        public JoinStatus Status = JoinStatus.Unknown;
        public string Title = "Mod list unavailable", Help = "Ask the host for their required mod list.";
        public readonly List<ModAction> Actions = new List<ModAction>();
        public readonly List<ModAction> Matching = new List<ModAction>();
        public static JoinSummary Create(string server, string local, string serverWire, string localWire, string failure)
        {
            var view = new JoinSummary();
            var wanted = Signature.Parse(server); var mine = Signature.Parse(local);
            bool build = failure.IndexOf("Different game build", StringComparison.OrdinalIgnoreCase) >= 0;
            bool map = failure.StartsWith("MAP UNAVAILABLE:", StringComparison.OrdinalIgnoreCase);
            if (build || (server.Length > 0 && wanted.Game != mine.Game))
                view.Actions.Add(new ModAction { Action = "UPDATE", Name = "Nuclear Option", Instruction = build ? "Use the host's game update and Steam beta branch." : "Use version " + Diagnostics.Clean(wanted.Game) + ". You have " + Diagnostics.Clean(mine.Game) + "." });
            if (map) view.Actions.Add(new ModAction { Action = "INSTALL", Name = "Server map", Instruction = "Get the host's exact map package and version." });
            if (wanted.Complete && mine.Complete)
            {
                foreach (var mod in wanted.Mods.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    string name = Friendly(mod.Key);
                    if (!mine.Mods.TryGetValue(mod.Key, out var installed))
                        view.Actions.Add(new ModAction { Action = "INSTALL", Name = name, Instruction = "Install version " + Diagnostics.Clean(mod.Value) + ". You don't have this mod." });
                    else if (installed != mod.Value)
                        view.Actions.Add(new ModAction { Action = "CHANGE VERSION", Name = name, Instruction = "Use version " + Diagnostics.Clean(mod.Value) + ". You have " + Diagnostics.Clean(installed) + "." });
                    else view.Matching.Add(new ModAction { Action = "OK", Name = name, Instruction = "Version " + Diagnostics.Clean(mod.Value) });
                }
                foreach (var mod in mine.Mods.Where(x => !wanted.Mods.ContainsKey(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
                    view.Actions.Add(new ModAction { Action = "REMOVE", Name = Friendly(mod.Key), Instruction = "Remove this mod from the set you use for this server." });
                if (view.Actions.Count == 0 && serverWire.Length > 0 && serverWire != localWire)
                    view.Actions.Add(new ModAction { Action = "CHECK FILES", Name = "Mod files or loading order", Instruction = "Use the host's exact mod files and loading order." });
            }
            bool same = serverWire.Length > 0 && serverWire == localWire;
            if (view.Actions.Count > 0)
            {
                view.Status = JoinStatus.Changes;
                view.Title = view.Actions.Count + (view.Actions.Count == 1 ? " change needed" : " changes needed");
                view.Help = "Make these changes, then restart Nuclear Option.";
                if (!wanted.Complete || !mine.Complete) view.Help += " Ask the host for the mod list too.";
            }
            else if (same && failure.Length == 0)
            {
                view.Status = JoinStatus.Match; view.Title = "Required mods match";
                view.Help = "Your game and mod versions match. Use the game's Join button.";
            }
            else if (same)
            {
                view.Title = "Join failed"; view.Help = "The mod versions match. Copy the report and send it to the host.";
            }
            return view;
        }
        private static string Friendly(string name) => name == "com.nikkorap.blueprinter" ? "Blueprinter" : Diagnostics.Clean(name).Replace('_', ' ');
    }
    internal static class JoinLayout
    {
        public const float DockWidth = 400, MinHeight = 580;
        public const float Margin = 20, Gap = 12, ActionHeight = 44, MatchingHeight = 36;
        public const float BackgroundAlpha = 1;
        public static bool CanDock(float width, float height, float right, float top, float bottom, float scale)
        {
            float sum = width + height + right + top + bottom + scale;
            if (float.IsNaN(sum) || float.IsInfinity(sum) || scale <= 0 || right < 0) return false;
            return width - right >= (DockWidth + 24) * scale && top >= 12 * scale && bottom <= height - 12 * scale && bottom - top >= MinHeight * scale;
        }
        public static PanelLayout Measure(float height, float titleHeight, float helpHeight, float footerHeight, bool matching)
        {
            // The header grows with wrapped text; the footer always reserves its own space.
            var result = new PanelLayout();
            result.TitleHeight = Math.Max(34, titleHeight);
            result.ServerTop = Margin + result.TitleHeight + 6;
            result.HelpTop = result.ServerTop + 26 + Gap;
            result.HelpHeight = Math.Max(28, helpHeight);
            result.ListTop = result.HelpTop + result.HelpHeight + Gap;
            result.FooterHeight = Math.Max(24, footerHeight);
            result.FooterBottom = Margin + ActionHeight + Gap;
            result.ListBottom = result.FooterBottom + result.FooterHeight + Gap;
            if (matching) result.ListBottom += MatchingHeight + Gap;
            result.ListHeight = Math.Max(0, height - result.ListTop - result.ListBottom);
            return result;
        }
    }
    internal sealed class PanelLayout
    {
        public float TitleHeight, ServerTop, HelpTop, HelpHeight, ListTop, ListBottom, ListHeight, FooterBottom, FooterHeight;
    }
}
