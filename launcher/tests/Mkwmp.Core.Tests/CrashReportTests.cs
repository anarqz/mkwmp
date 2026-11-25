// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using System.Text;
using Mkwmp.Core.Diagnostics;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The game's crash records as the launcher reads them (Fixtures/mkwmp-crash.log: two records a
/// real process wrote, native/probe/tests/test_crash.c), which crashes are offered and never
/// twice, and the report: its title, text, link and zip.
/// </summary>
public class CrashReportTests
{
    private const string Guest = "20261120T201303Z-25332"; // the fixture's first record: a fault in "lifted" code, no room known
    private const string Room = "20261120T201303Z-21788";  // its second: a fault in ntdll, in a room of three as the host

    private static string Fixture => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "mkwmp-crash.log"));

    private static string GameWith(string log, params string[] dumps)
    {
        var game = Rig.NewFolder("crash-game");
        File.WriteAllText(Path.Combine(game, CrashRecord.FileName), log);
        foreach (var dump in dumps)
        {
            File.WriteAllBytes(Path.Combine(game, dump), [.. "MDMP"u8, .. new byte[64]]);
        }
        return game;
    }

    [Fact]
    public void Reads_the_records_the_game_wrote()
    {
        var records = CrashRecord.Parse(Fixture);
        Assert.Equal([Guest, Room], records.Select(r => r.Id));

        var room = records[1];
        Assert.Equal("ACCESS_VIOLATION in ntdll.dll+0x16bbd2 (players=3, host=1, mesh=created)", room.Summary);
        Assert.Equal(("0xc0000005", "ACCESS_VIOLATION", "ntdll.dll+0x16bbd2", "unhandled"), (room.Code, room.CodeName, room.Where, room.Stage));
        Assert.Equal("read 0x0000000000000020", room.Access);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 20, 13, 3, 613, TimeSpan.Zero), room.Time);
        Assert.Equal((3, true, true, "created"), (room.Players, room.PlayersExact, room.Host, room.Mesh));
        Assert.Equal(("0b4e7a10-1111-4222-8333-444455556666", "3", "6f1c2d3e-0000-5000-8000-00000000abcd"), (room.Lobby, room.Ussid, room.Uid));
        Assert.Equal(("dev", "1.8.0"), (room.State["mkwmp"], room.State["game"]));
        Assert.StartsWith("Windows ", room.Os, StringComparison.Ordinal);
        Assert.Equal("ntdll.dll+0x16bbd2", room.Frames[0]);
        Assert.StartsWith("test_crash.exe+0x", room.Frames[1], StringComparison.Ordinal);
        Assert.Empty(room.GuestFrames);
        Assert.Equal($"mkwmp-crash-{Room}.dmp", room.Dump);
        Assert.False(room.Compacted);
        Assert.Contains("room: lobby=0b4e7a10-1111-4222-8333-444455556666 ussid=3 host=1 players=3 ussids=12,5,3", room.Text, StringComparison.Ordinal);

        // no room told by the server: the size is an estimate from the Pia endpoints, the host is not known
        var guest = records[0];
        Assert.Equal((4, false, (bool?)null, ""), (guest.Players, guest.PlayersExact, guest.Host, guest.Lobby));
        Assert.Equal("6f1c2d3e-0000-5000-8000-00000000abcd", guest.Uid);
        Assert.Equal(["main+0x5678", "main+0xabcc"], guest.GuestFrames);
        Assert.Contains("pc=main+0x1234 lr=main+0x4444", guest.Guest, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_a_cut_down_record_and_one_cut_short()
    {
        var old = "=== MKWMP CRASH 20261101T101010Z-77 ===\r\nsummary: STACK_OVERFLOW in mkw.exe+0x10 (players=?, host=?, mesh=?)\r\ntime_utc: 2026-11-01T10:10:10.000Z\r\n=== END 20261101T101010Z-77 ===\r\n";
        var cut = "=== MKWMP CRASH 20261102T101010Z-78 ===\r\nsummary: HEAP_CORRUPTION in ntdll.dll+0x99 (players=~5, host=?, mesh=joined)\r\ntime_utc: 2026-11-02T10:10:10.000Z\r\nexception: 0xc0000374 HEAP_CORRUPTION\r\nstack:\r\n  #00 ntdll.dll+0x99\r\n";
        var records = CrashRecord.Parse(old + "noise between records\r\n" + cut);
        Assert.Equal(2, records.Count);
        Assert.True(records[0].Compacted);
        Assert.Equal(("STACK_OVERFLOW", "mkw.exe+0x10", (int?)null, (string?)null), (records[0].CodeName, records[0].Where, records[0].Players, records[0].Dump));
        Assert.False(records[1].Compacted);
        Assert.Equal((5, false, "joined"), (records[1].Players, records[1].PlayersExact, records[1].Mesh));
        Assert.Equal(["ntdll.dll+0x99"], records[1].Frames);
        // an id that could leave the folder as a file name is not a record
        Assert.Empty(CrashRecord.Parse("=== MKWMP CRASH ..\\..\\x ===\r\nsummary: x\r\n=== END x ===\r\n"));
        Assert.Empty(CrashRecord.ReadAll(Path.Combine(Path.GetTempPath(), "mkwmp-no-such-folder")));
    }

    [Fact]
    public void A_crash_is_offered_once()
    {
        var game = GameWith(Fixture, $"mkwmp-crash-{Room}.dmp");
        var home = Rig.NewFolder("crash-home");
        var store = new CrashIncidentStore(home);
        var started = new DateTimeOffset(2026, 11, 20, 20, 0, 0, TimeSpan.Zero);
        var now = started.AddMinutes(14);

        // the game ended with a record written since it started: the newest one, with its dump
        var crash = store.Detect(game, started, exitCode: -1073741819, now);
        Assert.NotNull(crash);
        Assert.Equal((Room, "ACCESS_VIOLATION in ntdll.dll+0x16bbd2", $"mkwmp-crash-{Room}.dmp"), (crash.Id, crash.What, crash.Dump));
        Assert.Equal([Room, Guest], store.Pending(game).Select(i => i.Id));
        Assert.Null(store.Find(game, Guest)!.Dump); // the record names a dump that is not there

        // answered: neither comes back, also for a new store (a restart) and a later end of the game
        store.MarkHandled([Room, Guest]);
        Assert.Empty(new CrashIncidentStore(home).Pending(game));
        Assert.Null(new CrashIncidentStore(home).Detect(game, started, 0, now));
        Assert.True(store.IsHandled(Room));

        // records from before this start are not this session's crash
        Assert.Null(new CrashIncidentStore(Rig.NewFolder("crash-home")).Detect(game, started.AddHours(1), 0, now.AddHours(1)));
    }

    [Fact]
    public void An_abnormal_end_without_a_record_is_a_crash_too()
    {
        var game = GameWith("", "mkwmp-crash-20261120T201500Z-1.dmp");
        var home = Rig.NewFolder("crash-home");
        var store = new CrashIncidentStore(home);
        var now = DateTimeOffset.UtcNow;
        Assert.Null(store.Detect(game, now.AddMinutes(-5), 0, now));      // ended well
        Assert.Null(store.Detect(game, now.AddMinutes(-5), 1, now));      // the runtime's own small error codes
        Assert.Null(store.Detect(game, now.AddMinutes(-5), null, now));   // not ours to wait for, and no record
        Assert.True(CrashIncidentStore.AbnormalExit(71));                 // mkw-recomp's crash reporter
        Assert.True(CrashIncidentStore.AbnormalExit(3));                  // abort()

        var crash = store.Detect(game, now.AddMinutes(-5), unchecked((int)0xC0000409), now);
        Assert.NotNull(crash);
        Assert.Equal(("exit code 0xC0000409 (FAIL_FAST)", "mkwmp-crash-20261120T201500Z-1.dmp"), (crash.What, crash.Dump));
        Assert.StartsWith("exit-", crash.Id, StringComparison.Ordinal);
        // kept across a restart until answered
        var again = Assert.Single(new CrashIncidentStore(home).Pending(game));
        Assert.Equal((crash.Id, 0xC0000409u), (again.Id, again.ExitCode));
        store.MarkHandled([crash.Id]);
        Assert.Empty(new CrashIncidentStore(home).Pending(game));
    }

    [Fact]
    public void Title_text_and_link()
    {
        var store = new CrashIncidentStore(Rig.NewFolder("crash-home"));
        var game = GameWith(Fixture);
        var room = store.Find(game, Room)!;
        Assert.Equal("Crash: ACCESS_VIOLATION in ntdll.dll+0x16bbd2 (3 players, host)", BugReportBuilder.CrashTitle(room));
        Assert.Equal("Crash: ACCESS_VIOLATION in test_crash.exe+0x32a4 (about 4 players)".Split('+')[0], BugReportBuilder.CrashTitle(store.Find(game, Guest)!).Split('+')[0]);
        Assert.EndsWith("(about 4 players)", BugReportBuilder.CrashTitle(store.Find(game, Guest)!), StringComparison.Ordinal);

        var text = BugReportBuilder.CrashBody(room, "1.4.0", "game folder: C:\\Games\nmkw-recomp: Supported(\"0.1.15-mkwmp\")\n", $"mkwmp-crash-{Room}.zip");
        foreach (var part in new[]
        {
            "- **Crash**: `ACCESS_VIOLATION in ntdll.dll+0x16bbd2`", $"- **Crash id**: `{Room}`", "- **Time (UTC)**: `2026-11-20T20:13:03.613Z`",
            "- **Access**: `read 0x0000000000000020`", "- **Room**: `3 players, this player was the host, mesh created`",
            "- **Lobby**: `0b4e7a10-1111-4222-8333-444455556666`", "- **ussid**: `3`", "- **Game**: `1.8.0`", "- **Launcher**: `1.4.0`",
            "**Stack** (top 5 of 5)", "#00 ntdll.dll+0x16bbd2", "mkw-recomp: Supported(\"0.1.15-mkwmp\")", $"drag `mkwmp-crash-{Room}.zip`",
        })
        {
            Assert.Contains(part, text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("6f1c2d3e", text, StringComparison.Ordinal); // the user id stays in the zip's record

        var url = BugReportBuilder.Url("crash.yml", "crash", BugReportBuilder.CrashTitle(room), "report", text);
        Assert.StartsWith("https://github.com/anarqz/mkwmp/issues/new?template=crash.yml&labels=crash&title=Crash%3A%20ACCESS_VIOLATION%20in%20ntdll.dll%2B0x16bbd2", url, StringComparison.Ordinal);
        Assert.Contains("&report=", url, StringComparison.Ordinal);
        Assert.True(url.Length <= BugReportBuilder.MaxUrl);
        Assert.Equal(text, Uri.UnescapeDataString(url[(url.IndexOf("&report=", StringComparison.Ordinal) + 8)..])); // it fits: nothing cut

        // a text far over the limit is cut at a line, says so, and closes the code block it cut into
        var big = text + "\n```\n" + string.Concat(Enumerable.Range(0, 2000).Select(i => $"#{i:00} some.dll+0x{i:x} ção\n")) + "```\n";
        var cut = BugReportBuilder.Url("crash.yml", "crash", "t", "report", big);
        Assert.True(cut.Length <= BugReportBuilder.MaxUrl && cut.Length > BugReportBuilder.MaxUrl - 400);
        var shown = Uri.UnescapeDataString(cut[(cut.IndexOf("&report=", StringComparison.Ordinal) + 8)..]);
        Assert.EndsWith("_(cut to fit the link: the full text is on the clipboard and in the zip)_\n", shown, StringComparison.Ordinal);
        Assert.StartsWith(text, shown, StringComparison.Ordinal);
        Assert.Equal(1, shown.Split("```").Length % 2);

        // a crash with only an exit code, and "Found a bug?"
        var exit = new CrashIncident("exit-20261120T201500Z-c0000409", DateTimeOffset.UnixEpoch, null, 0xC0000409, null);
        Assert.Equal("Crash: exit code 0xC0000409 (FAIL_FAST)", BugReportBuilder.CrashTitle(exit));
        Assert.Contains("The game left no crash record", BugReportBuilder.CrashBody(exit, "1.4.0", null, null), StringComparison.Ordinal);
        Assert.Contains("Save Logs for a Bug Report", BugReportBuilder.BugBody("1.4.0", null, null), StringComparison.Ordinal);
    }

    [Fact]
    public void The_zip_has_the_crash_files_and_no_secrets()
    {
        var game = GameWith(Fixture);
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        File.WriteAllText(Path.Combine(game, "mkwmp.toml"), "name = \"Ana\"\nticket = \"mkw1.secret-ticket\"\nlauncher_token = \"tok-123456\"\n");
        File.WriteAllText(Path.Combine(game, "mkwmp.log"), "[       0ms] === mkwmp session log ===\r\n");
        // a dump with the ticket in it as the game holds it (bytes) and as a wide string
        byte[] dump = [.. "MDMP"u8, .. "..mkw1.secret-ticket.."u8, .. Encoding.Unicode.GetBytes("123456"), .. "tail"u8];
        File.WriteAllBytes(Path.Combine(game, $"mkwmp-crash-{Room}.dmp"), dump);
        var home = Rig.NewFolder("crash-home");
        var desktop = Rig.NewFolder("crash-desktop");
        // mkw-recomp's own files: a long mkw.log, its rotation (old runs), the setup's logs, its state, mkw.ini, the mod's settings
        var logs = Path.Combine(game, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "mkw.log"), string.Concat(Enumerable.Range(0, 120000).Select(i => $"line {i:000000} of the run, some text to make it long\n")) + "FATAL: access violation (0xc0000005) at host 0x1 (mkwgame+0x10)\n");
        // older runs, dated before the crash's session (SaveZip: the 12 h before the crash at 2026-11-20 20:13:03 UTC), not
        // before today: dated from the clock, a run 3 days old fell inside that session once the fixture was 3 days old
        var sessionStart = new DateTime(2026, 11, 20, 20, 13, 3, DateTimeKind.Utc).AddHours(-12);
        for (var i = 1; i <= 6; i++)
        {
            File.WriteAllText(Path.Combine(logs, $"mkw.{i}.log"), $"run {i} ago\n");
            File.SetLastWriteTimeUtc(Path.Combine(logs, $"mkw.{i}.log"), sessionStart.AddDays(-i));
        }
        File.WriteAllText(Path.Combine(logs, "warm.log"), "warm\n");
        File.WriteAllText(Path.Combine(game, "setup-state.json"), "{\"port\":\"0.1.15-mkwmp\"}");
        File.WriteAllText(Path.Combine(game, "mkwgame.id"), "d00e4f39 1.8.0\n");
        File.WriteAllText(Path.Combine(game, "mkw.ini"), "romfs = C:\\Games\\World.nup\n[launcher]\nmods = mkwmp\npin = 123456\n");
        Directory.CreateDirectory(Path.Combine(game, "mod-settings"));
        File.WriteAllText(Path.Combine(game, "mod-settings", "mkwmp.ini"), "pin = 123456\n");
        Directory.CreateDirectory(Path.Combine(home, "logs"));
        File.WriteAllText(Path.Combine(home, "logs", "setup-20261120-200000.log"), "the setup driver\n");
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        var install = target.Detect(game)!;
        var crash = new CrashIncidentStore(home).Find(game, Room)!;

        var (zip, onDesktop, system) = BugReportBuilder.SaveZip(home, "1.4.0", target, install, [crash], "the report text, ticket mkw1.abc.def", desktop,
            ["mkw1.secret-ticket", "123456", "tok-123456", ""]);
        Assert.Equal((Path.Combine(desktop, $"mkwmp-crash-{Room}.zip"), true), (zip, onDesktop));
        Assert.Contains("launcher: 1.4.0", system, StringComparison.Ordinal);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Superset(new HashSet<string> { "crash.txt", "system.txt", "mkwmp-crash.log", "mkwmp.log", "mkwmp.toml", $"mkwmp-crash-{Room}.dmp" }, names);
        Assert.Contains("mkw-recomp's own logs", BugReportBuilder.CrashBody(crash, "1.4.0", null, "x.zip"), StringComparison.Ordinal);
        string Read(string name)
        {
            using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.Latin1);
            return reader.ReadToEnd();
        }
        var scrubbed = Read($"mkwmp-crash-{Room}.dmp");
        Assert.Equal(dump.Length, scrubbed.Length);
        Assert.StartsWith("MDMP..xxxxxxxxxxxxxxxxxx..", scrubbed, StringComparison.Ordinal);
        Assert.EndsWith("xxxxxxxxxxxxtail", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-ticket", Read("mkwmp.toml"), StringComparison.Ordinal);
        Assert.DoesNotContain("tok-123456", Read("mkwmp.toml"), StringComparison.Ordinal);
        Assert.Contains($"=== MKWMP CRASH {Room} ===", Read("mkwmp-crash.log"), StringComparison.Ordinal);

        // mkw-recomp's own: this session's logs and the newest four of the folder, the long one by its tail; its state; no PIN
        Assert.Superset(new HashSet<string> { "mkw-recomp/mkw.log", "mkw-recomp/mkw.1.log", "mkw-recomp/mkw.2.log", "mkw-recomp/warm.log", "mkw-recomp/setup-state.json",
            "mkw-recomp/mkwgame.id", "mkw-recomp/mkw.ini", "setup-20261120-200000.log", "index.txt" }, names);
        Assert.DoesNotContain("mkw-recomp/mkw.3.log", names); // older than the session and not among the newest
        Assert.DoesNotContain("mkw.log", names);              // once, in mkw-recomp/
        Assert.DoesNotContain(names, n => n.Contains("mod-settings", StringComparison.Ordinal));
        var tail = Read("mkw-recomp/mkw.log");
        Assert.True(tail.Length <= RuntimeFiles.MaxFileBytes + 200 && tail.Length > RuntimeFiles.MaxFileBytes - 200);
        Assert.StartsWith("[the last ", tail, StringComparison.Ordinal);
        Assert.EndsWith("FATAL: access violation (0xc0000005) at host 0x1 (mkwgame+0x10)\n", tail, StringComparison.Ordinal);
        Assert.Equal("romfs = C:\\Games\\World.nup\n[launcher]\nmods = mkwmp\npin = <redacted>\n\n", Read("mkw-recomp/mkw.ini"));
        Assert.Contains("mkwgame.id: d00e4f39 1.8.0\n", system, StringComparison.Ordinal);
        Assert.Contains($"mkw.exe sha256: {Updates.Updater.Sha256Hex(Rig.TestExe)}\n", system, StringComparison.Ordinal);
        var index = Read("index.txt");
        foreach (var name in names.Where(n => n != "index.txt"))
        {
            Assert.Contains("  " + name + "\n", index, StringComparison.Ordinal);
        }
        Assert.True(new FileInfo(zip).Length < RuntimeFiles.MaxTotalBytes);

        // no Desktop: beside the launcher's logs
        var (fallback, there, _) = BugReportBuilder.SaveZip(home, "1.4.0", target, install, [], "text", Path.Combine(desktop, "gone"), []);
        Assert.False(there);
        Assert.Equal(Path.Combine(home, "logs"), Path.GetDirectoryName(fallback));
        Assert.StartsWith("mkwmp-bug-", Path.GetFileName(fallback), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launcher_offers_reports_and_forgets()
    {
        var server = new FakeSeatServer();
        var root = Rig.NewFolder("crash-launcher");
        var game = Path.Combine(root, "game");
        var desktop = Path.Combine(root, "desktop");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(desktop);
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { Name = "Ana", Game = game, AutoUpdate = false } }.Save(path);
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        await using var launcher = new Launcher(path, Path.Combine(root, "launcher"), new LauncherOptions
        {
            Version = "1.4.0", BindLocalApi = false, BackgroundLoops = false, NoUpdate = true, Headless = true, Targets = [target], UpdateApi = "http://github.test", DesktopFolder = desktop,
        }, server);
        var l = launcher;
        var changes = 0;
        l.Changed += (_, _) => Interlocked.Increment(ref changes);

        // nothing yet; "Found a bug?" works without a crash
        launcher.RefreshCrashes();
        Assert.Null(l.PendingCrash);
        var bug = await l.PrepareReportAsync(null);
        Assert.False(bug.Crash);
        Assert.True(File.Exists(bug.Zip) && bug.OnDesktop && Path.GetDirectoryName(bug.Zip) == desktop);
        Assert.StartsWith("https://github.com/anarqz/mkwmp/issues/new?template=bug_report.yml&labels=bug&title=&version=1.4.0&logs=Drag%20mkwmp-bug-", bug.Url, StringComparison.Ordinal);

        // the game crashes: the records are in its folder when it ends
        launcher.GameStarting();
        File.WriteAllText(Path.Combine(game, CrashRecord.FileName), Fixture.Replace("2026-11-20T20:13:03", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        File.WriteAllBytes(Path.Combine(game, $"mkwmp-crash-{Room}.dmp"), "MDMP"u8.ToArray());
        launcher.GameEnded(target.Detect(game)!, unchecked((int)0xC0000005));
        var crash = l.PendingCrash;
        Assert.NotNull(crash);
        Assert.Equal((Room, "ACCESS_VIOLATION in ntdll.dll+0x16bbd2", 3, true, true, true, 1), (crash.Id, crash.What, crash.Players, crash.Host, crash.HasRecord, crash.HasDump, crash.Earlier));
        Assert.True(changes > 0);

        var report = await l.PrepareReportAsync(crash.Id);
        Assert.True(report.Crash);
        Assert.Equal("Crash: ACCESS_VIOLATION in ntdll.dll+0x16bbd2 (3 players, host)", report.Title);
        Assert.Equal(Path.Combine(desktop, $"mkwmp-crash-{Room}.zip"), report.Zip);
        Assert.Contains("&report=", report.Url, StringComparison.Ordinal);
        Assert.Contains($"drag `mkwmp-crash-{Room}.zip`", report.Text, StringComparison.Ordinal);
        Assert.Contains("mkw-recomp:", report.Text, StringComparison.Ordinal); // system.txt's lines
        using (var archive = ZipFile.OpenRead(report.Zip!))
        {
            Assert.Contains(archive.Entries, e => e.FullName == $"mkwmp-crash-{Room}.dmp");
            Assert.Contains(archive.Entries, e => e.FullName == "crash.txt");
        }

        // answered: gone, with the earlier one shown with it, and for the next launcher too
        await l.DismissCrashAsync(crash.Id);
        Assert.Null(l.PendingCrash);
        launcher.RefreshCrashes();
        Assert.Null(l.PendingCrash);
        Assert.Empty(new CrashIncidentStore(Path.Combine(root, "launcher")).Pending(game));
    }
}
