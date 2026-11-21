// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core.Diagnostics;

namespace Mkwmp.Core.Tests;

/// <summary>logs/launcher-crash.log: a launcher that disappears leaves a start without an exit, and the next one says so.</summary>
public class FatalLogTests
{
    private static readonly Func<int, bool> Gone = _ => false;

    [Fact]
    public void A_session_that_started_and_left_is_nothing_to_report()
    {
        var text = "2026-11-20T23:13:32.807Z start pid=320 mkwmp-launcher 1.4.0 (Wine)\r\n2026-11-20T23:15:00.000Z exit pid=320 code=0\r\n";
        Assert.Null(FatalLog.Unfinished(text, Gone));
        Assert.Null(FatalLog.Unfinished("", Gone));
        Assert.Null(FatalLog.Unfinished("not a line of ours\r\n", Gone));
    }

    [Fact]
    public void A_start_without_an_exit_is_the_last_session_s_crash_with_what_it_wrote()
    {
        var text = string.Join("\r\n",
            "2026-11-20T23:08:27.303Z start pid=316 mkwmp-launcher 1.4.0 (Wine)",
            "2026-11-20T23:09:33.000Z exit pid=316 code=0",
            "2026-11-20T23:13:32.807Z start pid=320 mkwmp-launcher 1.4.0 (Wine)",
            "2026-11-20T23:15:32.700Z unhandled exception (the process ends): System.InvalidOperationException: boom",
            "   at Somewhere.Deep()",
            "");
        var report = FatalLog.Unfinished(text, Gone);
        Assert.NotNull(report);
        Assert.StartsWith("2026-11-20T23:13:32.807Z start pid=320", report, StringComparison.Ordinal);
        Assert.Contains("boom", report, StringComparison.Ordinal);
        Assert.Contains("Somewhere.Deep", report, StringComparison.Ordinal);
        Assert.DoesNotContain("pid=316", report, StringComparison.Ordinal);
    }

    [Fact]
    public void An_exit_of_another_process_does_not_close_the_session()
    {
        // Wine gives the same small pids again and again; a second launcher (it hands over and leaves) writes its own pair
        var text = "2026-11-20T23:13:32.807Z start pid=320 mkwmp-launcher 1.4.0\r\n2026-11-20T23:13:40.000Z exit pid=332 code=0\r\n";
        Assert.NotNull(FatalLog.Unfinished(text, Gone));
    }

    [Fact]
    public void A_launcher_that_still_runs_did_not_crash()
    {
        var text = "2026-11-20T23:13:32.807Z start pid=320 mkwmp-launcher 1.4.0\r\n";
        Assert.Null(FatalLog.Unfinished(text, pid => pid == 320));
        Assert.NotNull(FatalLog.Unfinished(text, Gone));
    }

    [Fact]
    public void The_file_is_cut_to_its_end_on_a_line_when_it_grew()
    {
        var folder = Directory.CreateTempSubdirectory("mkwmp-fatal-").FullName;
        try
        {
            var path = Path.Combine(folder, FatalLog.FileName);
            var line = "2026-11-20T23:13:32.807Z start pid=320 mkwmp-launcher 1.4.0\r\n";
            File.WriteAllText(path, string.Concat(Enumerable.Repeat(line, 6000)));
            var text = FatalLog.Trim(path);
            Assert.InRange(text.Length, 1, 64 * 1024);
            Assert.StartsWith("2026-11-20T", text, StringComparison.Ordinal);
            Assert.Equal(text.Length, new FileInfo(path).Length);
            Assert.Equal("", FatalLog.Trim(Path.Combine(folder, "none.log")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Install_writes_the_start_at_once_and_every_line_is_on_the_disk_when_Write_returns()
    {
        var folder = Directory.CreateTempSubdirectory("mkwmp-fatal-").FullName;
        // One per process: the first Install wins. No standard error here: the test host keeps its own.
        Assert.Null(FatalLog.Install(folder, "9.9.9", standardError: false));
        var path = Path.Combine(folder, "logs", FatalLog.FileName);
        Assert.Equal(path, FatalLog.Path);
        FatalLog.Write("ticket mkw1.secretsecret and more");
        string text;
        using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(read))
        {
            text = reader.ReadToEnd();
        }
        var pid = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        Assert.Contains($" start pid={pid} mkwmp-launcher 9.9.9", text, StringComparison.Ordinal);
        Assert.Contains("ticket mkw1.<redacted> and more", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secretsecret", text, StringComparison.Ordinal);
        // this very process started and has not left: a second launcher reading the file now reports nothing
        Assert.Null(FatalLog.Unfinished(text, alive => alive == Environment.ProcessId));
        Assert.NotNull(FatalLog.Unfinished(text, Gone));
        Assert.Null(FatalLog.Install(folder, "9.9.9", standardError: false)); // again: nothing happens
    }
}
