using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace KellysJOINCHECK
{
    // Collect only DOORMAN's own support events. Formatting a report never reads
    // the game's logs, configuration, or installed files.
    internal sealed class SupportReport
    {
        internal const int MaxEvents = 24;
        internal const int MaxEventChars = 2048;
        internal const int MaxOutputBytes = 48 * 1024;
        private const string Truncated = "[truncated]";
        private readonly object sync = new object();
        private readonly List<Entry> events = new List<Entry>();
        private readonly string[] roots;
        private readonly string[] aliases;
        private readonly Func<DateTime> clock;
        private Entry? latestFailure;

        private sealed class Entry
        {
            internal DateTime Time;
            internal string Category = "", Message = "";
            internal bool Failure;
            internal string Text => "[" + Time.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture) + "] " + Category + ": " + Message;
        }

        internal SupportReport(string gameRoot, string userRoot, Func<DateTime>? clock = null)
        {
            this.clock = clock ?? (() => DateTime.UtcNow);
            var replacements = new[] { new { Root = Root(gameRoot), Alias = "<Game>" }, new { Root = Root(userRoot), Alias = "<User>" } }
                .Where(x => x.Root.Length >= 3).OrderByDescending(x => x.Root.Length).ToArray();
            roots = replacements.Select(x => x.Root).ToArray();
            aliases = replacements.Select(x => x.Alias).ToArray();
        }

        internal void Record(string category, string message, bool failure = false)
        {
            string safeCategory = Clean(category, 80).Replace('\n', ' ').Replace('\t', ' ');
            string safeMessage = Clean(message, MaxEventChars);
            if (safeCategory.Length == 0) safeCategory = "DOORMAN";
            lock (sync)
            {
                var previous = events.Count > 0 ? events[events.Count - 1] : null;
                if (previous != null && previous.Category == safeCategory && previous.Message == safeMessage && previous.Failure == failure) return;
                var entry = new Entry { Time = clock().ToUniversalTime(), Category = safeCategory, Message = safeMessage, Failure = failure };
                events.Add(entry);
                if (events.Count > MaxEvents) events.RemoveAt(0);
                if (failure) latestFailure = entry;
            }
        }

        internal string Compose(string compatibilityReport, IEnumerable<string> context)
        {
            Entry? failure;
            Entry[] recent;
            lock (sync) { failure = latestFailure; recent = events.ToArray(); }
            var heading = new StringBuilder("DOORMAN support report\n\nLast recorded failure\n");
            heading.Append(failure == null ? "None recorded in this session." : failure.Text).Append('\n');
            heading.Append("\nCurrent context\n");
            var details = new StringBuilder();
            int count = 0;
            foreach (string value in context ?? Array.Empty<string>())
            {
                if (count++ >= 512 || details.Length >= 16384) { details.Append(Truncated).Append('\n'); break; }
                details.Append(Clean(value, MaxEventChars)).Append('\n');
            }
            heading.Append(details.Length == 0 ? "No additional context.\n" : Clip(details.ToString(), 16384));
            heading.Append("\nRecent DOORMAN events (newest first)\n");
            string compatibility = "\nCompatibility report\n" + Clip(Clean(compatibilityReport, 16384), 16384);
            var history = new StringBuilder();
            for (int i = recent.Length - 1; i >= 0; i--) history.Append(recent[i].Text).Append('\n');
            if (recent.Length == 0) history.Append("None recorded in this session.\n");
            // Reserve compatibility space before fitting history. A noisy failed
            // operation must not remove the original missing/wrong mod report.
            int budget = Math.Max(0, MaxOutputBytes - Encoding.UTF8.GetByteCount(heading.ToString()) - Encoding.UTF8.GetByteCount(compatibility));
            return Clip(heading.ToString() + Clip(history.ToString(), budget) + compatibility, MaxOutputBytes);
        }

        private static string Root(string value) => (value ?? "").Replace('\\', '/').TrimEnd('/');

        private string Clean(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var result = new StringBuilder(Math.Min(value.Length, limit));
            int position = 0;
            while (position < value.Length && result.Length < limit)
            {
                bool replaced = false;
                for (int r = 0; r < roots.Length; r++)
                {
                    if (!MatchesRoot(value, position, roots[r])) continue;
                    result.Append(aliases[r]); position += roots[r].Length; replaced = true; break;
                }
                if (replaced) continue;
                char c = value[position++];
                if (c == '\r') { if (position >= value.Length || value[position] != '\n') result.Append('\n'); }
                else if (!char.IsControl(c) || c == '\n' || c == '\t') result.Append(c);
            }
            if (position < value.Length)
            {
                int remaining = Math.Max(0, limit - Truncated.Length);
                if (result.Length > remaining) result.Length = remaining;
                if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1])) result.Length--;
                result.Append(Truncated);
            }
            else if (result.Length > limit) result.Length = limit;
            return result.ToString();
        }

        private static bool MatchesRoot(string value, int position, string root)
        {
            if (position + root.Length > value.Length) return false;
            for (int i = 0; i < root.Length; i++)
            {
                char actual = value[position + i];
                if (actual == '\\') actual = '/';
                if (char.ToUpperInvariant(actual) != char.ToUpperInvariant(root[i])) return false;
            }
            int end = position + root.Length;
            if (end == value.Length) return true;
            char next = value[end];
            return next == '\\' || next == '/' || (!char.IsLetterOrDigit(next) && next != '_' && next != '-' && next != '.' && next != ':');
        }

        private static string Clip(string value, int bytes)
        {
            if (bytes <= 0) return "";
            if (Encoding.UTF8.GetByteCount(value) <= bytes) return value;
            int suffix = Encoding.UTF8.GetByteCount(Truncated);
            if (bytes < suffix) return Truncated.Substring(0, bytes);
            int low = 0, high = value.Length;
            while (low < high)
            {
                int middle = low + (high - low + 1) / 2;
                if (Encoding.UTF8.GetByteCount(value.Substring(0, middle)) <= bytes - suffix) low = middle;
                else high = middle - 1;
            }
            if (low > 0 && char.IsHighSurrogate(value[low - 1])) low--;
            return value.Substring(0, low) + Truncated;
        }
    }
}
