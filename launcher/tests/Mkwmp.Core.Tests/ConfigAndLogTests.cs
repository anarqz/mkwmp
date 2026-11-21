// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

public class ConfigTests
{
    [Fact]
    public void Loads_with_defaults_and_skips_keys_it_does_not_know()
    {
        // An old `friends = [...]` line (friends now live on the server) and a future table still load.
        var c = ClientConfig.Parse("server=\"10.0.0.5:7000/\"\nname=\"Ana\"\nfriends=[\"Bia\",\"Cid\"]\ngame=\"C:/game\"\n[future]\nx = 1\n[voice]\nnew_knob = 3\n");
        var s = c.Settings;
        Assert.Equal("http://10.0.0.5:7000", s.Server);
        Assert.Equal((1, true, SeatMode.Start, 47820, true, true, false), (s.Slot, s.Launch, s.Seat, s.LocalPort, s.AutoUpdate, s.Patch, s.AdultConsent));
        Assert.Equal(new VoiceSettings(), s.Voice);
        Assert.Equal(new BackgroundSettings(), s.Background);
        Assert.Equal(new UiSettings(), s.Ui);
        Assert.Equal("mkw-recomp", s.Target);
        Assert.Equal(SeatMode.Online, ClientConfig.Parse("server=\"x\"\nname=\"Ana\"\ngame=\"g\"\nseat=\"online\"\n").Settings.Seat);
        Assert.Throws<LauncherException>(() => ClientConfig.Parse("server = "));
    }

    [Fact]
    public void Save_round_trips_everything_atomically_with_the_header()
    {
        var dir = Rig.NewFolder("config");
        var path = Path.Combine(dir, "client.toml");
        var c = new ClientConfig
        {
            Settings = new LauncherSettings
            {
                Name = "Ana",
                Pin = "123456",
                Game = @"C:\Games\Mario ""Kart"" World\mkw-recomp",
                Launch = false,
                Seat = SeatMode.Online,
                LocalPort = 5000,
                AutoUpdate = false,
                Slot = 2,
                LaunchCommand = "~/mkwmp/run-game.sh",
                Patch = false,
                ClosestServers = false,
                AdultConsent = true,
                Target = "some-emulator",
                Background = new BackgroundSettings { CloseToTray = false, StartWithSystem = true, NotifyPlaying = false, TrayHintShown = true },
                Voice = new VoiceSettings { Enabled = false, Input = "{0.0.1.00000000}.{abc}", InputGain = 40, OutputGain = 70, Camera = @"\\?\usb#vid_046d&pid_085e#cam", CameraName = "Logitech BRIO", CameraMirror = false, ChatIntroDone = true },
                Ui = new UiSettings { Fullscreen = false, Theme = "dark", Locale = "pt-BR", SetupDone = true },
            },
            Mutes = [new MutedPlayer("123", "Bia"), new MutedPlayer("18446744073709551615", "Cid")],
            Inputs = [new AudioDevice("a", "Mic")],
        };
        c.Save(path);
        var text = File.ReadAllText(path);
        Assert.StartsWith("# mkwmp launcher settings (edited by the launcher window).\n# Guide: https://anarqz.github.io/mkwmp/\n\n", text, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
        Assert.True(ClientConfig.Load(path).SameAs(c));
        // The defaults too (empty lists are written inline).
        new ClientConfig().Save(path);
        Assert.True(ClientConfig.Load(path).SameAs(new ClientConfig()));
        Assert.Contains("mutes = []", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Play_starts_the_game_by_default_also_under_proton_and_the_old_steamos_default_goes()
    {
        // A first start: PLAY starts the game everywhere (on SteamOS the Steam shortcut is the launcher itself).
        Assert.True(ClientConfig.Default().Settings.Launch);
        Assert.DoesNotContain("launch_chosen", ClientConfig.Default().ToToml(), StringComparison.Ordinal);

        // `launch = false` as the launcher used to write it on SteamOS (under Proton): the default again under Wine, kept on Windows.
        Assert.True(ClientConfig.Parse("launch = false\n", wine: true).Settings.Launch);
        Assert.False(ClientConfig.Parse("launch = false\n").Settings.Launch);

        // The player's own choice (Settings › Game) is kept under Wine too, and written back.
        var chosen = ClientConfig.Parse("launch = false\nlaunch_chosen = true\n", wine: true);
        Assert.Equal((false, true), (chosen.Settings.Launch, chosen.Settings.LaunchChosen));
        Assert.Contains("launch_chosen = true", chosen.ToToml(), StringComparison.Ordinal);
        Assert.False(ClientConfig.Parse(chosen.ToToml(), wine: true).Settings.Launch);

        // Through the file: the migrated value is what the next save writes.
        var path = Path.Combine(Rig.NewFolder("config"), "client.toml");
        File.WriteAllText(path, "name = \"Ana\"\nlaunch = false\n");
        var migrated = ClientConfig.Load(path, wine: true);
        Assert.True(migrated.Settings.Launch);
        migrated.Save(path);
        Assert.Contains("launch = true", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Names_and_pins()
    {
        Assert.True(ClientConfig.ValidName("Ana_1-x"));
        Assert.False(ClientConfig.ValidName("ab"));
        Assert.False(ClientConfig.ValidName("a b c"));
        Assert.False(ClientConfig.ValidName("abcdefghijklmnopq"));
        Assert.True(ClientConfig.ValidPin("012345"));
        Assert.False(ClientConfig.ValidPin("12345a"));
    }

    [Fact]
    public void Options_parse_the_rust_command_line()
    {
        var o = LauncherOptions.Parse(["--headless", "--no-launch", "--no-update", @"D:\x\client.toml"]);
        Assert.True(o is { Headless: true, NoLaunch: true, NoUpdate: true, Tray: false, SaveLogs: false, ApplyUpdatesAutomatically: true, ConfigPath: @"D:\x\client.toml" });
        Assert.True(LauncherOptions.Parse(["--tray"]) is { Tray: true, Headless: false, ConfigPath: null, ApplyUpdatesAutomatically: false });
        Assert.True(LauncherOptions.Parse(["-V"]).ShowVersion);
        Assert.True(LauncherOptions.Parse(["--save-logs", "--help"]) is { SaveLogs: true, ShowHelp: true });
        Assert.StartsWith("mkwmp ", new LauncherOptions().VersionLine, StringComparison.Ordinal);
    }
}

public class LogTests
{
    [Fact]
    public void Redacts_tickets_tokens_and_pins()
    {
        var hex = string.Concat(Enumerable.Repeat("ab", 32));
        Assert.Equal("ticket mkw1.<redacted>== token <redacted> end", SessionLog.Redact($"ticket mkw1.abc_DEF-1.sig== token {hex} end"));
        Assert.Equal("Your PIN is <pin>, ok", SessionLog.Redact("Your PIN is 123456, ok"));
        Assert.Equal("pin = \"<pin>\"", SessionLog.Redact("pin = \"123456\""));
        // Not secrets: ports, short hex, numbers on lines without "pin".
        Assert.Equal("port 47820 id 123456 abcdef", SessionLog.Redact("port 47820 id 123456 abcdef"));
        Assert.Equal("pin 1234567 pinned 12345", SessionLog.Redact("pin 1234567 pinned 12345"));
    }

    [Fact]
    public void Keeps_the_newest_sessions()
    {
        var dir = Rig.NewFolder("logs");
        for (var i = 0; i < 14; i++)
        {
            File.WriteAllText(Path.Combine(dir, $"launcher-20260101-0000{i:00}.log"), "x");
        }
        File.WriteAllText(Path.Combine(dir, "other.txt"), "x");
        SessionLog.Prune(dir, SessionLog.Keep);
        var left = SessionLog.Sessions(dir);
        Assert.Equal(SessionLog.Keep, left.Count);
        Assert.EndsWith("launcher-20260101-000013.log", left[0], StringComparison.Ordinal);
        Assert.EndsWith("launcher-20260101-000004.log", left[^1], StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(dir, "other.txt")));
    }

    [Fact]
    public void A_session_log_is_written_redacted_with_a_timestamp()
    {
        var dir = Rig.NewFolder("session");
        string path;
        using (var log = new SessionLog(dir, echo: false))
        {
            log.Note("seated with mkw1.secret.sig");
            path = log.Path!;
        }
        var line = File.ReadAllLines(path).Single();
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z seated with mkw1\.<redacted>$", line);
        Assert.Equal(24, SessionLog.Timestamp(DateTimeOffset.UtcNow).Length);
        Assert.Equal("20261115-093012", SessionLog.FileStamp(new DateTimeOffset(2026, 11, 15, 9, 30, 12, TimeSpan.Zero)));
    }

    [Fact]
    public void Toml_secrets_are_masked()
    {
        var r = SupportBundle.RedactToml("server = \"x:1\"\nlauncher_token = \"abc\"\nticket = \"mkw1.a.b\"\nname = \"Ana\"\n");
        Assert.Equal("server = \"x:1\"\nlauncher_token = \"<redacted>\"\nticket = \"<redacted>\"\nname = \"Ana\"\n", r);
    }

    [Fact]
    public void The_bug_report_has_the_files_and_no_secrets()
    {
        var dir = Rig.NewFolder("support");
        var game = Path.Combine(dir, "game");
        Directory.CreateDirectory(Path.Combine(game, "logs"));
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "logs", "launcher-20260101-000000.log"), "hi");
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        File.WriteAllText(Path.Combine(game, "mkwmp.toml"), "ticket = \"mkw1.a.b\"\n");
        File.WriteAllText(Path.Combine(game, "mkwmp.log"), "probe");
        File.WriteAllText(Path.Combine(game, "logs", "mkw.log"), "recomp");
        var target = new MkwRecompTarget("1.4.0");
        var (folder, zip) = SupportBundle.Save(dir, "1.4.0", target, target.Detect(game));
        var system = File.ReadAllText(Path.Combine(folder, "system.txt"));
        Assert.StartsWith("launcher: 1.4.0\nos: windows ", system, StringComparison.Ordinal);
        Assert.Contains("mkw-recomp: Supported(\"0.1.10\")\ngame version: unknown", system, StringComparison.Ordinal);
        Assert.Contains("winmm.dll: missing\nmkwmp-voice.dll: missing\nwinmm_host.dll: missing\npatch check: Modified or damaged", system, StringComparison.Ordinal);
        Assert.Contains("\ntime: 20", system, StringComparison.Ordinal);
        Assert.DoesNotContain("mkw1.a", File.ReadAllText(Path.Combine(folder, "mkwmp.toml")), StringComparison.Ordinal);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["launcher-20260101-000000.log", "mkw.log", "mkwmp.log", "mkwmp.toml", "system.txt"], archive.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
    }
}

public class FriendWatchTests
{
    private static FriendList List(long cursor, (string Name, FriendState State)[] friends, params string[] incoming) =>
        new(cursor, [.. friends.Select(f => new Friend(f.Name, f.State))], [.. incoming.Select(n => new FriendRequest(n, DateTimeOffset.UnixEpoch))], []);

    [Fact]
    public void First_list_is_quiet_then_changes_notify()
    {
        var p = new BackgroundSettings();
        var w = new FriendWatch();
        var clock = TimeProvider.System;
        var t = clock.GetTimestamp();
        long After(TimeSpan d) => t + (long)(d.TotalSeconds * clock.TimestampFrequency);
        Assert.Empty(w.Diff(List(1, [("Ana", FriendState.Online), ("Bia", FriendState.Offline)], "Cid"), p, clock, t));
        var got = w.Diff(List(2, [("Ana", FriendState.Playing), ("Bia", FriendState.Online)], "Cid", "Dan"), p, clock, t);
        Assert.Equal([(ToastKind.FriendPlaying, "Ana"), (ToastKind.FriendOnline, "Bia"), (ToastKind.FriendRequest, "Dan")], got);
        // Going offline and back within the debounce: quiet.
        w.Diff(List(3, [("Ana", FriendState.Playing), ("Bia", FriendState.Offline)]), p, clock, t);
        Assert.Empty(w.Diff(List(4, [("Ana", FriendState.Playing), ("Bia", FriendState.Online)]), p, clock, After(TimeSpan.FromSeconds(60))));
        w.Diff(List(5, [("Ana", FriendState.Playing), ("Bia", FriendState.Offline)]), p, clock, t);
        Assert.Single(w.Diff(List(6, [("Ana", FriendState.Playing), ("Bia", FriendState.Online)]), p, clock, After(FriendWatch.Debounce)));
    }

    [Fact]
    public void Settings_switch_kinds_off()
    {
        var p = new BackgroundSettings { NotifyOnline = false };
        var w = new FriendWatch();
        var t = TimeProvider.System.GetTimestamp();
        w.Diff(List(1, [("Ana", FriendState.Offline)]), p, TimeProvider.System, t);
        Assert.Empty(w.Diff(List(2, [("Ana", FriendState.Online)]), p, TimeProvider.System, t));
        Assert.Equal(new Toast("Ana wants to be your friend", "Open MKWMP to accept or decline.", ToastKind.FriendRequest), FriendWatch.ToastFor(ToastKind.FriendRequest, "Ana"));
    }
}

public class GameFinderTests
{
    private static string Install(string folder, byte[] exe)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "mkw.exe"), exe);
        return folder;
    }

    [Fact]
    public void Finds_in_the_picker_order_and_a_playable_install_wins_the_look_around()
    {
        _ = Rig.TestExe; // registers the stand-in hashes
        var targets = new TargetRegistry([new MkwRecompTarget("1.4.0")]);
        var root = Rig.NewFolder("finder");
        var launcher = Path.Combine(root, "tools", "launcher");
        Directory.CreateDirectory(launcher);
        var downloads = Path.Combine(root, "Downloads");
        Directory.CreateDirectory(downloads);
        string[] roots = [downloads, Path.Combine(root, "missing")];
        Assert.Null(GameFinder.Find(targets, null, null, launcher, searchRoots: roots));

        // The look-around: two levels down, not three; a supported runtime wins over an unknown one found first.
        Install(Path.Combine(downloads, "a", "b", "too-deep"), Rig.TestExe);
        Assert.Null(GameFinder.Find(targets, null, null, launcher, searchRoots: roots));
        Install(Path.Combine(downloads, "a-unknown"), "another build"u8.ToArray());
        var good = Install(Path.Combine(downloads, "GAMES", "mkw-recomp"), Rig.TestExe);
        Assert.Equal(good, GameFinder.Find(targets, null, null, launcher, searchRoots: roots)!.Value.Install.Root);

        // The launcher's own folder and its parent come before the look-around; the saved choice before them; the explicit one first.
        var parent = Install(Path.Combine(root, "tools"), Rig.TestExe);
        Assert.Equal(parent, GameFinder.Find(targets, null, null, launcher, searchRoots: roots)!.Value.Install.Root);
        var saved = Install(Path.Combine(root, "saved"), Rig.TestExe);
        Assert.Equal(saved, GameFinder.Find(targets, null, saved, launcher, searchRoots: roots)!.Value.Install.Root);
        Assert.Equal(good, GameFinder.Find(targets, good, saved, launcher, searchRoots: roots)!.Value.Install.Root);
        // A saved folder that no longer detects is skipped, not trusted.
        Assert.Equal(parent, GameFinder.Find(targets, null, Path.Combine(root, "gone"), launcher, searchRoots: roots)!.Value.Install.Root);
        Assert.Null(targets.Detect(downloads));
    }

    [Fact]
    public void Steam_library_folders_are_read_from_its_file()
    {
        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\t\"label\"\t\t\"\"\n\t}\n}\n";
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], MkwRecompTarget.LibraryPaths(vdf));
        var roots = new MkwRecompTarget("1.4.0").SearchRoots().ToList();
        Assert.Contains(roots, r => r.EndsWith(@"Downloads\GAMES", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(roots, r => r.EndsWith("Downloads", StringComparison.OrdinalIgnoreCase));
    }
}
