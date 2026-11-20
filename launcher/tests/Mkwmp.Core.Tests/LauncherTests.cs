// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text.Json.Nodes;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>PLAY, the profile and the game's integration against a fake seat server (the port of rust/client/src/core/tests.rs).</summary>
public class LauncherTests
{
    private static void AssertForgotten(Rig rig)
    {
        var l = rig.Launcher;
        Assert.Equal(RegisterState.Deleted, l.Profile.Register);
        Assert.Equal(("", ""), (l.Settings.Name, l.Settings.Pin));
        Assert.Empty(l.Voice.Mutes);
        var saved = ClientConfig.Load(rig.ConfigPath);
        Assert.Equal(("", ""), (saved.Settings.Name, saved.Settings.Pin));
        Assert.Empty(saved.Mutes);
        Assert.Null(rig.Ticket); // the old ticket is gone
        Assert.Contains("voice_port = 8907", File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml")), StringComparison.Ordinal);
    }

    private static void AssertKept(Rig rig)
    {
        var saved = ClientConfig.Load(rig.ConfigPath);
        Assert.Equal(("Ana", "123456", 1), (saved.Settings.Name, saved.Settings.Pin, saved.Mutes.Count));
        Assert.Equal("123456", rig.Launcher.Settings.Pin);
        Assert.NotNull(rig.Ticket);
    }

    [Fact]
    public async Task Delete_forgets_the_profile()
    {
        var server = new FakeSeatServer();
        await using var rig = new Rig(server);
        await rig.Launcher.DeleteProfileAsync();
        AssertForgotten(rig);
        Assert.Equal(["Ana"], server.Deletes);
        Assert.Equal(Strings.ProfileDeleted, rig.Launcher.Profile.Message);
    }

    [Theory]
    [InlineData(404, "PROFILE_NOT_FOUND")]
    [InlineData(404, "")]
    public async Task Delete_of_a_profile_the_server_lacks_forgets_it_too(int status, string code)
    {
        await using var rig = new Rig(new FakeSeatServer { Delete = (status, code) });
        await rig.Launcher.DeleteProfileAsync();
        AssertForgotten(rig);
    }

    [Theory]
    [InlineData(401, "PIN_WRONG")]
    [InlineData(403, "FORBIDDEN")]
    public async Task Delete_with_a_wrong_pin_keeps_everything(int status, string code)
    {
        await using var rig = new Rig(new FakeSeatServer { Delete = (status, code) });
        await rig.Launcher.DeleteProfileAsync();
        Assert.Equal((RegisterState.Failed, Strings.WrongPin), (rig.Launcher.Profile.Register, rig.Launcher.Profile.Message));
        AssertKept(rig);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Delete_without_the_server_keeps_everything(bool unreachable)
    {
        await using var rig = new Rig(new FakeSeatServer { Unreachable = unreachable, Delete = (500, "REGISTRY_ERROR") });
        await rig.Launcher.DeleteProfileAsync();
        Assert.Equal(RegisterState.Failed, rig.Launcher.Profile.Register);
        AssertKept(rig);
    }

    [Fact]
    public async Task Play_starts_the_game_once_seated()
    {
        var server = new FakeSeatServer();
        await using var rig = new Rig(server);
        Assert.Null(rig.Launcher.PlayProblem);
        Assert.Equal((Strings.Ready, "plain"), (rig.Launcher.Session.Status, rig.Launcher.Session.Tone));
        await rig.Launcher.PlayAsync();
        await Rig.Until("the game", () => rig.Started);
        Assert.Equal("ticket = \"mkw1.ticket.sig\"", rig.Ticket);
        var toml = File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml"));
        Assert.Contains("server = \"lobby.test:8905\"", toml, StringComparison.Ordinal);
        Assert.Contains($"launcher_token = \"{rig.Launcher.Token}\"", toml, StringComparison.Ordinal);
        Assert.Contains("launcher = \"127.0.0.1:47820\"", toml, StringComparison.Ordinal);
        // The "game" exits at once: the seat goes, the session ends.
        Assert.Equal(SessionState.Idle, (await rig.SettleSession()).State);
        Assert.Contains("DELETE /seat/v1/sessions/s1", server.Requests);
    }

    [Theory]
    [InlineData(401, "PIN_WRONG", Strings.WrongPin)]
    [InlineData(404, "PROFILE_NOT_FOUND", Strings.NotRegistered)]
    public async Task Play_refused_does_not_start_the_game(int status, string code, string message)
    {
        // PROFILE_NOT_FOUND at sign-in means "expired": the launcher registers it again, and the second sign-in is what refuses.
        await using var rig = new Rig(new FakeSeatServer { Mood = SeatMood.Refused, RefuseWith = (status, code) });
        await rig.Launcher.PlayAsync();
        var session = await rig.SettleSession();
        Assert.Equal((SessionState.Failed, message, "bad"), (session.State, session.Status, session.Tone));
        Assert.False(rig.Started);
        Assert.Null(rig.Ticket); // no stale ticket either
    }

    [Fact]
    public async Task An_expired_profile_is_registered_again_with_the_saved_pin()
    {
        var server = new FakeSeatServer { Mood = SeatMood.Expired };
        await using var rig = new Rig(server);
        await rig.Launcher.PlayAsync();
        await Rig.Until("the game", () => rig.Started);
        var asked = JsonNode.Parse(Assert.Single(server.Registers))!;
        Assert.Equal(("Ana", "123456"), (asked["name"]!.GetValue<string>(), asked["pin"]!.GetValue<string>()));
        Assert.Equal("123456", rig.Launcher.Settings.Pin); // the PIN stays
    }

    [Fact]
    public async Task A_server_that_picks_its_own_pin_on_restore_gets_that_pin_saved()
    {
        await using var rig = new Rig(new FakeSeatServer { Mood = SeatMood.Expired, PicksPin = "777000" });
        await rig.Launcher.PlayAsync();
        await Rig.Until("the game", () => rig.Started);
        Assert.Equal("777000", rig.Launcher.Settings.Pin);
        Assert.Contains("777000", File.ReadAllText(rig.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_reaches_the_server_without_consent()
    {
        // The consent comes after signing in (the server keeps it), so a sign-in may reach the server without it;
        // what it gates does not: a seat, friends, reports.
        var server = new FakeSeatServer();
        await using var rig = new Rig(server);
        await rig.Launcher.SetConsentAsync(false);
        server.Requests.Clear();
        Assert.Equal(Strings.NoConsent, rig.Launcher.PlayProblem);
        await rig.Launcher.PlayAsync();
        var session = await rig.SettleSession();
        Assert.Equal((SessionState.Failed, Strings.NoConsent), (session.State, session.Status));
        await rig.Launcher.AddFriendAsync("Bia");
        Assert.Equal((Strings.NoConsent, true), (rig.Launcher.Friends.Note, rig.Launcher.Friends.NoteIsError));
        await rig.Launcher.SendReportAsync("Bia", "other", null);
        Assert.Equal((Strings.NoConsent, true), (rig.Launcher.Report.Note, rig.Launcher.Report.NoteIsError));
        Assert.False(rig.Started);
        Assert.DoesNotContain(server.Requests, r => r is "POST /seat/v1/sessions" or "POST /seat/v1/friends" or "GET /seat/v1/friends" or "POST /seat/v1/reports");
        await rig.Launcher.SetConsentAsync(true);
        Assert.Null(rig.Launcher.PlayProblem);
    }

    [Fact]
    public async Task Play_without_the_server_does_not_start_the_game()
    {
        foreach (var server in new[] { new FakeSeatServer { Unreachable = true }, new FakeSeatServer { Mood = SeatMood.Down } })
        {
            await using var rig = new Rig(server);
            await rig.Launcher.PlayAsync();
            var session = await rig.SettleSession();
            Assert.Equal((SessionState.Failed, Strings.Unreachable), (session.State, session.Status));
            Assert.False(rig.Started);
        }
    }

    [Fact]
    public async Task Play_queued_waits_for_the_seat()
    {
        await using var rig = new Rig(new FakeSeatServer { Mood = SeatMood.QueuedFor, Count = 2 });
        await rig.Launcher.PlayAsync();
        await Rig.Until("the queue", () => rig.Launcher.Session is { Phase: SeatPhase.Queued, GameRunning: false });
        Assert.Equal(Strings.Queued(2), rig.Launcher.Session.Status);
        Assert.Equal(2, rig.Launcher.Session.QueuePosition);
        Assert.False(rig.Started); // not while queued
        await Rig.Until("the game, once seated", () => rig.Started);
        Assert.NotNull(rig.Ticket);
    }

    [Fact]
    public async Task Stop_while_queued_never_starts_the_game()
    {
        var server = new FakeSeatServer { Mood = SeatMood.QueuedFor, Count = 1000 };
        await using var rig = new Rig(server);
        await rig.Launcher.PlayAsync();
        await Rig.Until("the queue", () => rig.Launcher.Session.Phase == SeatPhase.Queued);
        await rig.Launcher.StopAsync();
        Assert.Equal(SessionState.Idle, rig.Launcher.Session.State);
        Assert.False(rig.Started);
        Assert.Contains("DELETE /seat/v1/sessions/s1", server.Requests);
    }

    [Theory]
    [InlineData(SeatMood.Refused, Strings.WrongPin)]
    [InlineData(SeatMood.Down, Strings.Unreachable)]
    [InlineData(SeatMood.Seated, null)]
    public async Task Play_online_checks_the_profile_first(SeatMood mood, string? want)
    {
        var server = new FakeSeatServer { Mood = mood };
        await using var rig = new Rig(server, s => s with { Seat = SeatMode.Online });
        await rig.Launcher.PlayAsync();
        if (want is not null)
        {
            var session = await rig.SettleSession();
            Assert.Equal((SessionState.Failed, want), (session.State, session.Status));
            Assert.False(rig.Started);
            return;
        }
        await Rig.Until("the game", () => rig.Started);
        Assert.Equal(0, server.Joins); // no seat until the game goes online
        Assert.Null(rig.Ticket);
    }

    [Fact]
    public async Task A_gamertag_in_use_is_retried_not_refused()
    {
        var server = new FakeSeatServer { Mood = SeatMood.TakenFor, Count = 1 };
        await using var rig = new Rig(server);
        await rig.Launcher.PlayAsync();
        await Rig.Until("the busy phase", () => rig.Launcher.Session is { Phase: SeatPhase.Busy, Status: Strings.InUse, Tone: "warn" });
        Assert.False(rig.Started); // not while the name is in use
        await Rig.Until("the game, seated on the retry", () => rig.Started);
        Assert.Equal(2, server.Joins);
    }

    [Fact]
    public async Task Online_mode_does_not_cache_a_gamertag_in_use()
    {
        var server = new FakeSeatServer { Mood = SeatMood.TakenFor, Count = 1 };
        using var http = new HttpClient(server);
        using var seat = new SeatSession(new SeatApi(http, FakeSeatServer.Url, "test"), SeatMode.Online, "Ana", "123456", _ => { }, TimeProvider.System, TimeSpan.FromMilliseconds(300));
        Assert.Equal(new SeatAnswer.Unavailable(Strings.InUse), await seat.WantAsync());
        // Asked again at once: no new join (retries are spaced), still busy, not refused.
        Assert.Equal(new SeatAnswer.Unavailable(Strings.InUse), await seat.WantAsync());
        Assert.Equal(1, server.Joins);
        await Task.Delay(350);
        Assert.IsType<SeatAnswer.Seated>(await seat.WantAsync());
    }

    [Fact]
    public async Task Play_waits_for_the_local_api_port()
    {
        // Another launcher holds the port: this one could not bind it.
        using var taken = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        taken.Server.ExclusiveAddressUse = true;
        taken.Start();
        var port = ((System.Net.IPEndPoint)taken.LocalEndpoint).Port;
        var server = new FakeSeatServer();
        var root = Rig.NewFolder("port");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { Server = FakeSeatServer.Url, Name = "Ana", Pin = "123456", AdultConsent = true, LocalPort = port, AutoUpdate = false } }.Save(path);
        await using var launcher = new Launcher(path, root, new LauncherOptions { BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, server);
        Assert.NotNull(launcher.PlayProblem);
        await launcher.StartAsync(CancellationToken.None);
        Assert.False(launcher.LocalApiUp);
        // The settings come first in the order; with a game the port is what is left.
        Assert.Equal(Strings.SetGameFolder("the one with mkw.exe"), launcher.PlayProblem);
        var game = Path.Combine(root, "g");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        Assert.True(await launcher.ChooseGameFolderAsync(game));
        Assert.Contains("Another MKWMP launcher is running", launcher.PlayProblem, StringComparison.Ordinal);
        await launcher.PlayAsync();
        await launcher.SessionTask;
        Assert.Equal(SessionState.Failed, launcher.Session.State);
        Assert.Contains("Another MKWMP launcher is running", launcher.Session.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(game, "started")));
    }

    [Fact]
    public async Task The_mod_wins_only_on_a_runtime_that_loads_it()
    {
        if (Environment.GetEnvironmentVariable("MKW_MODS") is not null || Probe.DevProbe)
        {
            return; // the environment would decide instead
        }
        // 0.1.10 with a leftover mods/mkwmp: the launcher's patch stays.
        await using (var old = new Rig(new FakeSeatServer()))
        {
            old.EnableMod();
            await Assert.ThrowsAsync<LauncherException>(() => old.Launcher.SyncIntegrationAsync(force: false)); // no bundle, updates off
            Assert.True(File.Exists(Path.Combine(old.Game, Probe.ProbeDll)));
            Assert.NotEqual(IntegrationState.Mod, old.Launcher.Game.Integration!.State);
        }

        // 0.1.15 with the patch off in settings: nothing is removed, and the report says the mod carries it.
        await using var rig = new Rig(new FakeSeatServer(), s => s with { Patch = false });
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), Rig.TestModExe);
        rig.EnableMod();
        await rig.Launcher.SyncIntegrationAsync(force: false);
        Assert.True(File.Exists(Path.Combine(rig.Game, Probe.ProbeDll)));
        var report = rig.Launcher.Game.Integration!;
        Assert.True(report is { State: IntegrationState.Mod, Ok: true });
        Assert.Contains(report.Lines, l => l.Contains("still beside mkw.exe", StringComparison.Ordinal));
        Assert.Equal(Strings.InstallPatchFirst, rig.Launcher.PlayProblem);

        // 0.1.15 with the patch on: the launcher's game patch goes, mkwmp.toml stays.
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Patch = true });
        await rig.Launcher.SyncIntegrationAsync(force: false);
        Assert.False(File.Exists(Path.Combine(rig.Game, Probe.ProbeDll)));
        Assert.False(File.Exists(Path.Combine(rig.Game, Probe.Voice)));
        Assert.True(File.Exists(Path.Combine(rig.Game, "mkwmp.toml")));
        report = rig.Launcher.Game.Integration!;
        Assert.Equal((IntegrationState.Mod, "MKWMP runs as a mod in this game"), (report.State, report.Summary));
        Assert.Single(report.Lines);
        Assert.Null(rig.Launcher.PlayProblem);
        Assert.Equal(TargetStatus.Ready, rig.Launcher.Game.Check!.Status);
    }

    [Fact]
    public async Task Play_problems_come_in_the_order_the_player_fixes_them()
    {
        // With a server, what belongs to it comes first: the consent, the gamertag, its PIN. (Without one: Offline_* below.)
        await using var rig = new Rig(new FakeSeatServer(), s => s with { AdultConsent = false, Name = "a b", Pin = "", Patch = false }, installPatch: false);
        var l = rig.Launcher;
        // A server, then a profile on it, then the consent that server keeps.
        Assert.Equal(Strings.PickGamertag, l.PlayProblem);
        Assert.Equal((Strings.PickGamertag.ToUpperInvariant(), "warn"), (l.Session.Status, l.Session.Tone));
        Assert.Equal(ServerState.Checking, l.Server.State);
        Assert.Equal((Strings.NameRule, true), (l.Profile.Message, l.Profile.MessageIsError));
        await l.SaveSettingsAsync(l.Settings with { Name = "Ana" });
        Assert.Equal(Strings.NeedPin, l.PlayProblem);
        Assert.Equal(Strings.RegisterToPlay, l.Profile.Message);
        await l.UsePinAsync("Ana", "123456");
        Assert.True(l.Profile.SignedIn);
        Assert.Equal(Strings.KeepYourPin, l.Profile.Message);
        Assert.Equal(Strings.NoConsent, l.PlayProblem);
        await l.SetConsentAsync(true);
        await l.SaveSettingsAsync(l.Settings with { Game = Path.Combine(rig.Root, "nowhere") });
        Assert.Equal("Set the game folder (the one with mkw.exe)", l.PlayProblem);
        Assert.Null(l.Game.Install);
        Assert.False(await l.ChooseGameFolderAsync(rig.Root));
        Assert.True(l.Game.NoteIsError);
        Assert.True(await l.ChooseGameFolderAsync(rig.Game));
        Assert.Equal(Strings.InstallPatchFirst, l.PlayProblem);
        File.Delete(Path.Combine(rig.Game, "mkwmp.toml"));
        await l.SaveSettingsAsync(l.Settings with { Patch = true });
        await l.FindGameAsync();
        // Nothing installed and nothing to install from (no bundle, updates off).
        Assert.Equal("Not installed: press INSTALL", l.PlayProblem);
        Assert.Equal(TargetStatus.NoIntegration, l.Game.Check!.Status);
        await l.InstallIntegrationAsync();
        Assert.Equal((Strings.NoBundleAndUpdatesOff, true), (l.Game.Note, l.Game.NoteIsError));
        // An unsupported runtime comes before the integration.
        File.WriteAllBytes(Path.Combine(rig.Game, "mkw.exe"), "some other build"u8);
        await l.SaveSettingsAsync(l.Settings with { Slot = 2 });
        await l.FindGameAsync();
        Assert.Equal("Unknown mkw-recomp: MKWMP needs 0.1.10 or 0.1.12 or 0.1.13 or 0.1.15 or 0.1.15-mkwmp or 0.1.16", l.PlayProblem);
        Assert.Equal((TargetStatus.Unsupported, "GET 0.1.15-mkwmp"), (l.Game.Check!.Status, l.Game.Check.FixLabel));
    }

    [Fact]
    public async Task Register_saves_the_pin_and_a_taken_name_asks_for_it()
    {
        var server = new FakeSeatServer();
        await using var rig = new Rig(server, s => s with { Name = "", Pin = "" });
        await rig.Launcher.RegisterAsync("Bia");
        Assert.Equal(("Bia", "654321", RegisterState.Idle), (rig.Launcher.Settings.Name, rig.Launcher.Settings.Pin, rig.Launcher.Profile.Register));
        Assert.Equal("654321", ClientConfig.Load(rig.ConfigPath).Settings.Pin);
        // Another name never goes out with the old name's PIN.
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Name = "Cid" });
        Assert.Equal("", rig.Launcher.Settings.Pin);
    }

    [Fact]
    public async Task Friends_arrive_notify_once_and_actions_leave_a_note()
    {
        var server = new FakeSeatServer();
        JsonObject List(int cursor, string state, params string[] incoming) => new()
        {
            ["cursor"] = cursor,
            ["friends"] = new JsonArray(new JsonObject { ["name"] = "Bia", ["state"] = state }),
            ["incoming"] = new JsonArray([.. incoming.Select(n => new JsonObject { ["name"] = n, ["since"] = 1_700_000_000 })]),
            ["outgoing"] = new JsonArray(),
        };
        server.FriendsBody = List(1, "offline");
        var root = Rig.NewFolder("friends");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { Server = FakeSeatServer.Url, Name = "Ana", Pin = "123456", AdultConsent = true, AutoUpdate = false } }.Save(path);
        await using var launcher = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, NoUpdate = true, Headless = true, Targets = [new MkwRecompTarget("1.4.0")], UpdateApi = "http://github.test" }, server);
        var toasts = new List<Toast>();
        launcher.Toasted += (_, t) =>
        {
            lock (toasts)
            {
                toasts.Add(t);
            }
        };
        Assert.False(launcher.Friends.Loaded);
        await launcher.StartAsync(CancellationToken.None);
        await Rig.Until("the first list", () => launcher.Friends.Loaded);
        await Rig.Until("the server pill", () => launcher.Server.State == ServerState.Online);
        Assert.Equal("ONLINE · 2/2 SEATS FREE", Strings.ServerPill(launcher.Server));
        Assert.Equal((FriendState.Offline, 0), (Assert.Single(launcher.Friends.Friends).State, launcher.Friends.OnlineCount));
        Assert.Empty(toasts); // the first list is only remembered

        // A friend action cuts the long-poll short; the next list is the news.
        server.FriendsBody = List(2, "playing", "Dan");
        await launcher.AddFriendAsync("Cid");
        Assert.Equal((Strings.RequestSent("Cid"), false), (launcher.Friends.Note, launcher.Friends.NoteIsError));
        await Rig.Until("the new list", () => launcher.Friends.Incoming.Count == 1);
        await Rig.Until("the toasts", () => toasts.Count == 2);
        Assert.Equal([Strings.IsPlaying("Bia"), Strings.WantsToBeFriend("Dan")], toasts.Select(t => t.Title));
        Assert.Equal(1, launcher.Friends.OnlineCount);
        await Rig.Until("the report check (once per sign-in)", () => launcher.Report.Loaded == true);
        Assert.Null(launcher.Report.Open);
        launcher.ShowTestToast();
        Assert.Equal(Strings.TestToastTitle, toasts[^1].Title);
    }

    [Fact]
    public async Task Headless_registers_a_free_name_plays_and_ends_with_the_game()
    {
        _ = Rig.TestExe;
        // A free loopback port for this run's local API (47820 may be a real launcher's).
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        }
        (string Root, string Game, LauncherOptions Options) Setup(string pin)
        {
            var root = Rig.NewFolder("headless");
            var game = Path.Combine(root, "mkw");
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
            Probe.InstallProbe(game, "probe"u8, false, out _);
            Probe.InstallFile(game, Probe.Voice, "voice"u8, false);
            File.WriteAllBytes(Path.Combine(game, Probe.Host), Rig.FakeWinmm());
            var path = Path.Combine(root, "client.toml");
            new ClientConfig
            {
                Settings = new LauncherSettings
                {
                    Server = FakeSeatServer.Url, Name = "Ana", Pin = pin, Game = game, AdultConsent = true, LocalPort = port, AutoUpdate = false, LaunchCommand = "echo up > started",
                },
            }.Save(path);
            var options = LauncherOptions.Parse(["--headless", path]) with
            {
                Version = "1.4.0",
                Targets = [new MkwRecompTarget("1.4.0") { RunningOverride = () => false }],
                Tick = TimeSpan.FromMilliseconds(40),
                UpdateApi = "http://github.test",
            };
            return (root, game, options);
        }

        var server = new FakeSeatServer();
        var (root, game, options) = Setup(pin: "");
        Assert.Equal(0, await Headless.RunHeadlessAsync(options, CancellationToken.None, Path.Combine(root, "home"), server));
        Assert.True(File.Exists(Path.Combine(game, "started")));
        Assert.Equal("654321", ClientConfig.Load(options.ConfigPath!).Settings.Pin); // registered, and the PIN saved
        Assert.Contains($"launcher = \"127.0.0.1:{port}\"", File.ReadAllText(Path.Combine(game, "mkwmp.toml")), StringComparison.Ordinal);
        Assert.Contains("DELETE /seat/v1/presence", server.Requests); // signed out on the way out
        var log = File.ReadAllText(Directory.GetFiles(Path.Combine(root, "home", "logs")).Single());
        Assert.Contains("registered Ana", log, StringComparison.Ordinal);
        Assert.DoesNotContain("mkw1.ticket", log, StringComparison.Ordinal);

        // A name that is someone's needs its PIN: nothing is started.
        (root, game, options) = Setup(pin: "");
        Assert.Equal(1, await Headless.RunHeadlessAsync(options, CancellationToken.None, Path.Combine(root, "home"), new FakeSeatServer { TakenName = true }));
        Assert.False(File.Exists(Path.Combine(game, "started")));
        // A refusal is the exit code too.
        (root, game, options) = Setup(pin: "000000");
        Assert.Equal(1, await Headless.RunHeadlessAsync(options, CancellationToken.None, Path.Combine(root, "home"), new FakeSeatServer { Mood = SeatMood.Refused }));
        Assert.False(File.Exists(Path.Combine(game, "started")));
    }
}
