using System;
using System.Linq;
using System.Text;
using KellysJOINCHECK;

internal static class SupportReportRegression
{
    internal static void Run(Action<bool, string> check)
    {
        DateTime now = new DateTime(2026, 10, 7, 12, 30, 0, DateTimeKind.Utc);
        var report = new SupportReport(@"C:\Games\Nuclear Option", @"C:\Users\Tester", () => now);
        report.Record("Restart", "Could not restart: Content is outside the plugin folder.", true);
        report.Record("Content source", @"Cannot verify 'Aryx FS-41' [aryx_fs41]: source=resource:Pack:Pack.aircraft.nobp, physical=C:\Games\Nuclear Option\BepInEx\plugins\Pack.dll", true);
        string text = report.Compose("WRONG VERSION: aryx_f16m - server 1.2.3 | you 1.2.2", new[] { "Client: 1.3.8", @"Game: C:\Games\Nuclear Option" });
        check(text.Contains("Could not restart: Content is outside the plugin folder."), "Copy report retains the exact observed restart failure");
        check(text.Contains("Cannot verify 'Aryx FS-41' [aryx_fs41]") && text.Contains("source=resource:Pack:Pack.aircraft.nobp") && text.Contains(@"physical=<Game>\BepInEx\plugins\Pack.dll"), "Copy report retains affected mod, raw resource identity, and redacted physical source");
        check(text.IndexOf("Last recorded failure", StringComparison.Ordinal) < text.IndexOf("Current context", StringComparison.Ordinal) && text.IndexOf("Current context", StringComparison.Ordinal) < text.IndexOf("Compatibility report", StringComparison.Ordinal), "failure and current context lead the copied report");
        check(text.Contains("2026-10-07 12:30:00 UTC"), "recorded support events carry deterministic UTC timestamps");
        check(text.Contains("WRONG VERSION: aryx_f16m - server 1.2.3 | you 1.2.2"), "original compatibility diagnosis remains in the support report");

        report.Record("Setup", "Mods are ready.");
        string latest = report.Compose("MISSING: mod_b - install 2.0", Array.Empty<string>());
        check(LastFailure(latest).Contains("Cannot verify 'Aryx FS-41'") && !LastFailure(latest).Contains("Content is outside the plugin folder"), "newer explicit failure replaces the pinned failure without presenting the previous error as current");
        check(LastFailure(latest).Contains("Cannot verify 'Aryx FS-41'"), "ordinary setup progress does not erase the last explicit failure");
        for (int i = 0; i < 30; i++) report.Record("Step", "progress " + i);
        text = report.Compose("EXTRA: spare_mod 1.0", Array.Empty<string>());
        string recent = Recent(text);
        check(recent.Split('\n').Count(line => line.StartsWith("[2026-", StringComparison.Ordinal)) == SupportReport.MaxEvents, "recent event history retains only 24 observations");
        check(recent.Contains("progress 29") && !recent.Contains("progress 5\n") && !recent.Contains("Cannot verify"), "the event ring discards the oldest observations");
        check(LastFailure(text).Contains("Cannot verify 'Aryx FS-41'"), "the exact last failure survives rotation out of recent history");
        check(recent.IndexOf("progress 29", StringComparison.Ordinal) < recent.IndexOf("progress 28", StringComparison.Ordinal), "newest event appears first when a bounded report omits older history");

        var duplicate = new SupportReport("", "", () => now);
        duplicate.Record("Restart", "Waiting for game exit.");
        now = now.AddMinutes(1);
        duplicate.Record("Restart", "Waiting for game exit.");
        text = duplicate.Compose("MATCH", Array.Empty<string>());
        check(Recent(text).Split('\n').Count(line => line.Contains("Waiting for game exit.")) == 1, "identical consecutive events do not fill the event ring");
        check(text.Contains("2026-10-07 12:30:00 UTC") && !text.Contains("12:31:00 UTC"), "deduplicated observations retain the time of the original event");
        duplicate.Record("Restart", "Waiting for game exit.", true);
        text = duplicate.Compose("MATCH", Array.Empty<string>());
        check(LastFailure(text).Contains("Waiting for game exit."), "a failure with the same text as an ordinary event remains an explicit failure");

        var privacy = new SupportReport(@"C:\Users\Tester\Games\Nuclear Option\", @"C:\Users\Tester\", () => now);
        privacy.Record("Path", "c:/USERS/tester/games/NUCLEAR option/BepInEx/plugins/Pack.dll; c:\\users/tester\\Desktop\\extra.nobp", true);
        text = privacy.Compose("Source C:/Users/Tester/Games/Nuclear Option/BepInEx/plugins\nUser C:\\Users\\Tester\\Desktop", new[] { "Context C:\\Users/Tester\\Games/Nuclear Option", "Sibling C:\\Users\\Tester-other\\mod.dll" });
        check(text.Contains("<Game>/BepInEx/plugins/Pack.dll") && text.Contains(@"<User>\Desktop\extra.nobp"), "known game and user roots are redacted case insensitively with mixed separators");
        check(text.Contains("Source <Game>/BepInEx/plugins") && text.Contains("Context <Game>"), "root redaction applies to compatibility and context as well as events");
        check(text.Contains(@"C:\Users\Tester-other\mod.dll"), "path prefix redaction does not rewrite similarly named sibling roots");
        check(!text.Contains("C:/Users/Tester/Games/Nuclear Option") && !text.Contains("c:/USERS/tester/games/NUCLEAR option"), "full known paths do not leak through alternate separators or casing");

        var control = new SupportReport("", "", () => now);
        control.Record("Re\nst\tart\0", "Failure\0\u0001\u001b\r\nnext\tfield", true);
        text = control.Compose("Compare\u0007\rline", new[] { "Context\u0002\r\nmore" });
        check(!text.Any(c => char.IsControl(c) && c != '\n' && c != '\t'), "clipboard text removes control characters while retaining readable line breaks and tabs");
        check(text.Contains("Re st art: Failure\nnext\tfield") && text.Contains("Compare\nline") && text.Contains("Context\nmore"), "categories stay on one line and Windows line endings remain readable");

        var huge = new SupportReport("", "", () => now);
        string giant = new string('\u754c', 200000);
        huge.Record("Huge", giant, true);
        for (int i = 0; i < 40; i++) huge.Record("Huge " + i, giant);
        text = huge.Compose("MISSING: crucial_mod - install 7.0\n" + giant, Enumerable.Repeat(giant, 100000));
        check(Encoding.UTF8.GetByteCount(text) <= SupportReport.MaxOutputBytes, "giant Unicode input cannot exceed the 48 KiB UTF-8 clipboard limit");
        check(text.Contains("[truncated]"), "bounded fields and report sections explicitly mark truncation");
        check(text.Contains("MISSING: crucial_mod - install 7.0"), "large context and event history cannot crowd out compatibility details");
        check(LastFailure(text).Length < SupportReport.MaxEventChars + 100, "individual captured failure text is bounded independently of its input size");
        check(!text.Contains("Huge 0:") && text.Contains("Huge 39:"), "bounded output prioritises the newest retained events");

        var inventory = new SupportReport("", "", () => now);
        var inventoryContext = Enumerable.Range(0, 200).Select(i => "Loaded plugin example.mod" + i + " version 1.0")
            .Concat(new[] { "Content source: resource:Pack:Pack.aircraft.nobp", "Restart helper: launch failed, process already exited" });
        text = inventory.Compose("MISSING: required_pack - install 2.0", inventoryContext);
        check(text.Contains("Loaded plugin example.mod199 version 1.0") && text.Contains("Content source: resource:Pack:Pack.aircraft.nobp") && text.Contains("Restart helper: launch failed, process already exited"), "large normal plugin inventories retain later content and restart helper diagnostics");
        check(Encoding.UTF8.GetByteCount(text) <= SupportReport.MaxOutputBytes && text.Contains("MISSING: required_pack - install 2.0"), "expanded inventory context still respects the overall report cap and compatibility reservation");
        text = inventory.Compose("MATCH", Enumerable.Range(0, 1000).Select(i => "Context " + i));
        check(text.Contains("Context 511\n") && !text.Contains("Context 512\n") && text.Contains("[truncated]"), "context enumeration remains bounded to 512 items with an explicit truncation marker");

        var empty = new SupportReport("", "", () => now);
        text = empty.Compose("The advertised game and mod compatibility strings match.", Array.Empty<string>());
        check(text.Contains("None recorded in this session.") && text.Contains("The advertised game and mod compatibility strings match."), "a normal session without failures still produces a useful compatibility report");
        text = empty.Compose(null!, null!);
        check(Encoding.UTF8.GetByteCount(text) < 1024 && text.Contains("No additional context."), "missing diagnostic inputs are safe without fabricated failures");
    }

    private static string LastFailure(string value) => value.Split(new[] { "Last recorded failure\n" }, StringSplitOptions.None)[1].Split(new[] { "\nCurrent context" }, StringSplitOptions.None)[0];
    private static string Recent(string value) => value.Split(new[] { "Recent DOORMAN events (newest first)\n" }, StringSplitOptions.None)[1].Split(new[] { "\nCompatibility report" }, StringSplitOptions.None)[0];
}
