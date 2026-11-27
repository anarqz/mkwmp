// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// Offline first: a fresh install has no server, contacts none, and PLAY starts the game alone;
/// choosing a server turns online on, Disconnect turns it off and keeps the name and each server's PIN.
/// </summary>
public class OfflineFirstTests
{
    private static Rig Offline(FakeSeatServer server, Func<LauncherSettings, LauncherSettings>? edit = null) =>
        new(server, s => (edit ?? (x => x))(s with { Server = "", Pin = "", AdultConsent = false }));

    [Fact]
    public void A_fresh_install_is_offline()
    {
        Assert.Equal("", new LauncherSettings().Server);
        Assert.Equal("", ClientConfig.Default().Settings.Server);
        // A file without `server` (or with an empty one) is offline; one with a server keeps it.
        Assert.Equal("", ClientConfig.Parse("name = \"Ana\"\n").Settings.Server);
        Assert.Equal("", ClientConfig.Parse("server = \"  \"\n").Settings.Server);
        Assert.Equal("https://mkw.example.net", ClientConfig.Parse("server = \"https://mkw.example.net/\"\n").Settings.Server);
        Assert.False(Launcher.HasServer(new LauncherSettings()));
        Assert.True(Launcher.HasServer(new LauncherSettings { Server = "x:1" }));
    }

    [Fact]
    public async Task Offline_asks_only_for_the_game_and_contacts_no_server()
    {
        var server = new FakeSeatServer();
        await using var rig = Offline(server, s => s with { Name = "", Game = "" });
        var l = rig.Launcher;
        Assert.False(((ILauncher)l).OnlineEnabled);
        Assert.Equal(ServerState.NotAsked, l.Server.State);
        // No consent, no gamertag, no PIN: none of them is asked for. Only the game.
        Assert.Equal("Set the game folder (the one with mkw.exe)", l.PlayProblem);
        Assert.True(await l.ChooseGameFolderAsync(rig.Game));
        Assert.Null(l.PlayProblem);
        Assert.Equal((Strings.ReadyOffline, "plain"), (l.Session.Status, l.Session.Tone));
        Assert.Equal("READY · PLAY OFFLINE", Strings.ReadyOffline);

        // A local name is allowed before any server; it is not a profile anywhere.
        await l.SaveSettingsAsync(l.Settings with { Name = "Ana" });
        // (An install with no MKWMP setup goes online on Nextendo Network: the name's line says so, not "connect to a server".)
        Assert.Equal(("Ana", true, false, Strings.LocalNameNextendo), (l.Profile.Name, l.Profile.NameValid, l.Profile.SignedIn, l.Profile.Message));

        // Everything online is refused here, without a request.
        await l.RegisterAsync("Ana");
        Assert.Equal((RegisterState.Failed, Strings.OnlineOff), (l.Profile.Register, l.Profile.Message));
        await l.AddFriendAsync("Bia");
        Assert.Equal((Strings.OnlineOff, true), (l.Friends.Note, l.Friends.NoteIsError));
        await l.SendReportAsync("Bia", "other", null);
        Assert.Equal((Strings.OnlineOff, true), (l.Report.Note, l.Report.NoteIsError));
        await l.DeleteProfileAsync();
        Assert.Equal("Ana", l.Settings.Name); // nothing was deleted anywhere
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Offline_background_loops_stay_silent()
    {
        var server = new FakeSeatServer();
        var root = Rig.NewFolder("offline-loops");
        var path = Path.Combine(root, "client.toml");
        // Even with a name, a PIN and the consent: without a server there is nobody to talk to.
        new ClientConfig { Settings = new LauncherSettings { Name = "Ana", Pin = "123456", AdultConsent = true, AutoUpdate = false } }.Save(path);
        await using var launcher = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, NoUpdate = true, Headless = true, Targets = [new MkwRecompTarget("1.4.0")], UpdateApi = "http://github.test" }, server);
        Assert.Equal(ServerState.NotAsked, launcher.Server.State);
        Assert.False(launcher.Settings.Ui.SetupDone); // not an online profile: the welcome is still due
        await launcher.StartAsync(CancellationToken.None);
        await Task.Delay(700);
        Assert.Empty(server.Requests);
        Assert.Equal(ServerState.NotAsked, launcher.Server.State);
        Assert.False(launcher.Friends.Loaded);
        Assert.Null(launcher.Report.Loaded);

        // Connecting wakes them: status, presence and friends arrive without a restart.
        await launcher.SaveSettingsAsync(launcher.Settings with { Server = FakeSeatServer.Url });
        Assert.Equal("", launcher.Settings.Pin); // a PIN with no server was nobody's: it does not go out to the first server chosen
        Assert.False(launcher.Settings.AdultConsent); // nor was the consent this server's
        await launcher.UsePinAsync("Ana", "123456");
        await launcher.SetConsentAsync(true);
        await Rig.Until("the server pill", () => launcher.Server.State == ServerState.Online);
        await Rig.Until("the friends list", () => launcher.Friends.Loaded);

        // Disconnect: signed out there, locked here, and quiet again.
        await launcher.SaveSettingsAsync(launcher.Settings with { Server = "" });
        Assert.Equal(ServerState.NotAsked, launcher.Server.State);
        Assert.False(launcher.Friends.Loaded);
        await Rig.Until("the sign-out", () => server.Requests.Contains("DELETE /seat/v1/presence"));
        await Task.Delay(300);
        server.Requests.Clear();
        await Task.Delay(700);
        Assert.Empty(server.Requests);
    }

    /// <summary>
    /// The run after a required update could not be installed and the player chose "Continue Offline" (UPDATES.md,
    /// section 1): the launcher's own offline state although a server is set, with its own reason, until it is closed.
    /// </summary>
    [Fact]
    public async Task After_a_failed_update_the_run_goes_on_offline_only_and_the_next_start_requires_it_again()
    {
        var server = new FakeSeatServer();
        var root = Rig.NewFolder("offline-update");
        var path = Path.Combine(root, "client.toml");
        var exe = Path.Combine(root, "mkwmp.exe");
        File.WriteAllText(exe, "launcher");
        new ClientConfig { Settings = new LauncherSettings { Server = FakeSeatServer.Url, Name = "Ana", Pin = "123456", AdultConsent = true } }.Save(path);
        // as the helper starts the previous launcher after a rollback: nothing is checked, the failure is said
        Launcher Rolled() => new(path, root, new LauncherOptions
        {
            BindLocalApi = false, RequireUpdates = true, Executable = exe, Version = "1.4.4", Targets = [new MkwRecompTarget("1.4.4")], UpdateApi = "http://github.test",
            UpdateEnvironment = variable => variable == Mkwmp.Update.UpdateRunner.UpdatedVariable ? "1" : variable == Mkwmp.Update.UpdateRunner.FailedVariable ? "mkwmp.exe is in use" : null,
        }, server);

        await using (var launcher = Rolled())
        {
            await launcher.StartAsync(CancellationToken.None);
            await launcher.StartupUpdates;
            Assert.Equal((UpdateState.Failed, true), (launcher.Update.State, launcher.Update.Required));
            // Until the player decides, it is the launcher as it was: online.
            Assert.True(launcher.OnlineEnabled);
            await Rig.Until("the sign-in", () => server.Requests.Contains("POST /seat/v1/presence"));

            launcher.ContinueWithoutUpdate();
            Assert.Equal((false, true, false), (launcher.Update.Required, launcher.OnlineBlockedByUpdate, launcher.OnlineEnabled));
            Assert.Equal(FakeSeatServer.Url, launcher.Settings.Server); // nothing saved is touched: the server, the PIN, the consent
            Assert.Equal(("123456", true), (launcher.Settings.Pin, launcher.Settings.AdultConsent));
            Assert.Equal(ServerState.NotAsked, launcher.Server.State);
            Assert.False(launcher.Friends.Loaded);
            await Rig.Until("the sign-out", () => server.Requests.Contains("DELETE /seat/v1/presence"));
            await Task.Delay(300);
            server.Requests.Clear();

            // Everything that talks to the game server is refused, with the one reason, and without a request.
            await launcher.RegisterAsync("Ana");
            Assert.Equal((RegisterState.Failed, Strings.UpdateToPlayOnline), (launcher.Profile.Register, launcher.Profile.Message));
            await launcher.AddFriendAsync("Bia");
            Assert.Equal((Strings.UpdateToPlayOnline, true), (launcher.Friends.Note, launcher.Friends.NoteIsError));
            await launcher.SendReportAsync("Bia", "other", null);
            Assert.Equal((Strings.UpdateToPlayOnline, true), (launcher.Report.Note, launcher.Report.NoteIsError));
            Assert.Equal("Update MKWMP to play online", Strings.UpdateToPlayOnline);

            // The game asking this launcher for a seat is refused too (409: it stays offline), in words the mod can show.
            using var api = new LocalApi(launcher, TimeProvider.System);
            var answer = await api.HandleAsync("POST", "/local/v1/seat", $"Bearer {launcher.Token}", ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            Assert.Equal(409, answer.Status);
            Assert.Contains(Strings.UpdateToPlayOnlineInGame, answer.Body, StringComparison.Ordinal);
            Assert.True(Strings.UpdateToPlayOnlineInGame.Length < 128 && Strings.UpdateToPlayOnlineInGame.All(c => c is >= ' ' and <= '~' and not ('"' or '<' or '>' or '&' or '\\' or '\'' or '+')));

            // PLAY is allowed, and is the offline one: no seat is asked for.
            await launcher.PlayAsync();
            await Task.Delay(700);
            Assert.Equal(0, server.Joins);
            Assert.Empty(server.Requests);
        }

        // The next start: online again as far as the server goes, and the update required again.
        server.Requests.Clear();
        await using var next = Rolled();
        await next.StartAsync(CancellationToken.None);
        await next.StartupUpdates;
        Assert.Equal((true, false, true), (next.Update.Required, next.OnlineBlockedByUpdate, next.OnlineEnabled));
    }

    [Fact]
    public async Task Offline_play_starts_the_game_with_no_seat()
    {
        var server = new FakeSeatServer();
        await using var rig = Offline(server);
        var l = rig.Launcher;
        Assert.Null(l.PlayProblem);
        await l.PlayAsync();
        await Rig.Until("the game", () => rig.Started);
        Assert.Equal(SessionState.Idle, (await rig.SettleSession()).State);
        Assert.Empty(server.Requests); // no status, no presence, no seat
        Assert.Equal(0, server.Joins);

        // What the mod reads: only how to reach this launcher (for the overlay).
        var toml = File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml"));
        Assert.Contains($"launcher_token = \"{l.Token}\"", toml, StringComparison.Ordinal);
        Assert.Contains("launcher = \"127.0.0.1:47820\"", toml, StringComparison.Ordinal);
        var keys = toml.Split('\n').Where(line => line.Contains('=', StringComparison.Ordinal) && !line.StartsWith('#')).Select(line => line.Split('=')[0].Trim()).ToList();
        Assert.DoesNotContain("server", keys);
        Assert.DoesNotContain("name", keys);
        Assert.DoesNotContain("ticket", keys); // the old ticket of the rig's file is gone too
        Assert.Contains("voice_port = 0", toml, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(rig.Game, Probe.LobbyCa)));
    }

    [Fact]
    public void The_offline_session_file_names_the_launcher_and_nothing_else()
    {
        var text = Probe.SessionText(SessionFile.Offline("127.0.0.1:47820", "tok", "C:/MKWMP/mkwmp.exe"));
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith('#')).ToList();
        Assert.Equal(["launcher = \"127.0.0.1:47820\"", "launcher_token = \"tok\"", "launcher_exe = \"C:/MKWMP/mkwmp.exe\"", "voice_port = 0"], lines);
        // An online one is as before.
        var online = Probe.SessionText(new SessionFile("lobby:8905", "Ana", "127.0.0.1:47820", "tok", 8907, "mkw1.t", 1, null));
        Assert.Contains("server = \"lobby:8905\"\nname = \"Ana\"\n", online, StringComparison.Ordinal);
        Assert.Contains("ticket = \"mkw1.t\"", online, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_the_game_asking_for_a_seat_is_refused_with_the_reason()
    {
        await using var rig = Offline(new FakeSeatServer());
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        var answer = await api.HandleAsync("POST", "/local/v1/seat", $"Bearer {rig.Launcher.Token}", ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        // 409 = refused: the mod shows the message and the game stays offline (503 would read "launcher not reachable").
        // The reason is where this install goes online: Nextendo Network (nobody is signed in), so "sign in with Nextendo".
        Assert.Equal(409, answer.Status);
        Assert.Contains(Strings.NextendoSignInInGame, answer.Body, StringComparison.Ordinal); // as written: the mod's reader undoes no JSON escape
        // With MKWMP servers chosen and none set: "connect to a server".
        await rig.Launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        answer = await api.HandleAsync("POST", "/local/v1/seat", $"Bearer {rig.Launcher.Token}", ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal(409, answer.Status);
        Assert.Contains(Strings.OnlineOffInGame, answer.Body, StringComparison.Ordinal);
        foreach (var text in new[] { Strings.OnlineOffInGame, Strings.NextendoSignInInGame })
        {
            Assert.True(text.Length < 128 && text.All(c => c is >= ' ' and <= '~' and not ('"' or '<' or '>' or '&' or '\\' or '\'' or '+')));
        }
        // The overlay's routes do not care about a server.
        var overlay = await api.HandleAsync("GET", "/local/v1/overlay/state", $"Bearer {rig.Launcher.Token}", ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal(200, overlay.Status);
    }

    [Fact]
    public async Task Probe_tells_an_mkwmp_server_from_everything_else()
    {
        var root = Rig.NewFolder("probe");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { AdultConsent = true, AutoUpdate = false } }.Save(path);
        using var handler = new ProbeServer();
        await using var l = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, handler);
        var ok = await l.ProbeServerAsync("good.test:8080/");
        Assert.Equal((ServerProbeState.Ok, "http://good.test:8080", "fake", 32, 12, 20), (ok.State, ok.Address, ok.Name, ok.SeatsTotal, ok.Seated, ok.SeatsFree));
        // the players in seats out of the seats, not the free ones
        Assert.Equal("MKWMP server · 12/32 players", Strings.ProbeOk(ok));
        Assert.Equal("MKWMP server · 12/32 players · 3 in queue", Strings.ProbeOk(ok with { Queued = 3 }));
        Assert.Null(ok.Message);
        Assert.Equal((ServerProbeState.Unreachable, Strings.ProbeUnreachable), await Probe("https://down.test"));
        Assert.Equal((ServerProbeState.Unreachable, Strings.ProbeUnreachable), await Probe("https://failing.test"));
        Assert.Equal((ServerProbeState.NotMkwmp, Strings.ProbeNotMkwmp), await Probe("https://website.test"));
        Assert.Equal((ServerProbeState.NotMkwmp, Strings.ProbeNotMkwmp), await Probe("https://json.test"));
        Assert.Equal((ServerProbeState.TooOld, Strings.ProbeTooOld), await Probe("https://old.test"));
        Assert.Equal((ServerProbeState.Invalid, Strings.ProbeInvalid), await Probe("  "));
        Assert.Equal((ServerProbeState.Invalid, Strings.ProbeInvalid), await Probe("ftp://x.test"));
        // Asking saves nothing: still offline.
        Assert.Equal("", l.Settings.Server);
        Assert.Equal("", ClientConfig.Load(path).Settings.Server);

        async Task<(ServerProbeState, string?)> Probe(string address)
        {
            var p = await l.ProbeServerAsync(address);
            return (p.State, p.Message);
        }
    }

    [Fact]
    public async Task Connect_register_disconnect_and_each_server_keeps_its_pin()
    {
        var server = new FakeSeatServer();
        await using var rig = Offline(server, s => s with { Name = "Ana" });
        var l = rig.Launcher;
        Assert.True((await l.ProbeServerAsync(FakeSeatServer.Url)).Ok);

        // Connect = save the server; the name from before is the one registered there.
        await l.SaveSettingsAsync(l.Settings with { Server = FakeSeatServer.Url });
        Assert.True(((ILauncher)l).OnlineEnabled);
        Assert.Equal(Strings.NeedPin, l.PlayProblem);
        await l.RegisterAsync("Ana");
        Assert.Equal(("Ana", "654321", true), (l.Settings.Name, l.Settings.Pin, l.Profile.SignedIn));
        Assert.Equal(Strings.NoConsent, l.PlayProblem); // signed in: now the consent, for this server
        await l.SetConsentAsync(true);
        Assert.Null(l.PlayProblem);
        Assert.Equal(Strings.Ready, l.Session.Status);
        Assert.Equal([new SavedServer(FakeSeatServer.Url, "Ana", true)], l.SavedServers);

        // Disconnect: the name stays, the PIN is no longer in use, online is locked.
        await l.SaveSettingsAsync(l.Settings with { Server = "" });
        Assert.Equal(("", "Ana", ""), (l.Settings.Server, l.Settings.Name, l.Settings.Pin));
        Assert.False(l.Profile.SignedIn);
        Assert.Null(l.PlayProblem);
        Assert.Equal(Strings.ReadyOffline, l.Session.Status);
        Assert.Single(l.SavedServers);

        // Another server: its own PIN; the first one's is kept.
        await l.SaveSettingsAsync(l.Settings with { Server = "http://other.test" });
        Assert.Equal("", l.Settings.Pin);
        Assert.False(l.Settings.AdultConsent); // the consent was the first server's
        await l.UsePinAsync("Ana", "111222");
        Assert.Equal(2, l.SavedServers.Count);

        // Back to the first: its PIN is there again, in memory and in the file, also after a restart.
        await l.SaveSettingsAsync(l.Settings with { Server = FakeSeatServer.Url + "/" });
        Assert.Equal((FakeSeatServer.Url, "654321", true), (l.Settings.Server, l.Settings.Pin, l.Settings.AdultConsent));
        var saved = ClientConfig.Load(rig.ConfigPath);
        Assert.Equal("654321", saved.Settings.Pin);
        Assert.Equal(("654321", "111222"), (saved.PinFor(FakeSeatServer.Url, "ana"), saved.PinFor("other.test", "Ana")));
        var text = File.ReadAllText(rig.ConfigPath);
        Assert.Contains("[servers.\"http://seat.test\"]\nname = \"Ana\"\npin = \"654321\"\nconsent = true\n", text, StringComparison.Ordinal);
        Assert.Contains("[servers.\"http://other.test\"]", text, StringComparison.Ordinal);

        // A PIN belongs to a name: another name on this server has none, the first name gets its own back.
        await l.SaveSettingsAsync(l.Settings with { Name = "Cid" });
        Assert.Equal("", l.Settings.Pin);
        await l.SaveSettingsAsync(l.Settings with { Name = "Ana" });
        Assert.Equal("654321", l.Settings.Pin);

        // Deleting the profile on a server forgets that server's PIN, and only that one.
        await l.DeleteProfileAsync();
        Assert.Equal(RegisterState.Deleted, l.Profile.Register);
        Assert.Equal(["http://other.test"], l.SavedServers.Select(s => s.Address));
        Assert.Equal("", ClientConfig.Load(rig.ConfigPath).PinFor(FakeSeatServer.Url, "Ana"));
    }

    [Fact]
    public async Task The_old_single_pin_becomes_its_servers_pin()
    {
        // client.toml as the Rust launcher (and this one, before) wrote it.
        var root = Rig.NewFolder("migrate");
        var path = Path.Combine(root, "client.toml");
        File.WriteAllText(path, "server = \"https://mkw.example.net\"\nname = \"Ana\"\npin = \"123456\"\nadult_consent = true\nupdate = false\n", new UTF8Encoding(false));
        var loaded = ClientConfig.Load(path);
        Assert.Equal([new ServerProfile("https://mkw.example.net", "Ana", "123456", true)], loaded.Servers);

        await using var launcher = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, new FakeSeatServer());
        Assert.Equal("123456", launcher.Settings.Pin);
        // A working online profile never meets the first-start welcome.
        Assert.True(launcher.Settings.Ui.SetupDone);
        // The first save writes both forms: `pin` for the server in use, and the table.
        await launcher.SaveSettingsAsync(launcher.Settings with { Slot = 2 });
        var text = File.ReadAllText(path);
        Assert.Contains("\npin = \"123456\"\n", text, StringComparison.Ordinal);
        Assert.Contains("[servers.\"https://mkw.example.net\"]\nname = \"Ana\"\npin = \"123456\"\nconsent = true\n", text, StringComparison.Ordinal);
        Assert.Contains("setup_done = true", text, StringComparison.Ordinal);
        Assert.True(ClientConfig.Load(path).SameAs(launcher.Config));

        // Half a profile does not skip the welcome: no PIN, or no consent, or no server.
        foreach (var toml in new[]
        {
            "server = \"https://mkw.example.net\"\nname = \"Ana\"\nadult_consent = true\n",
            "server = \"https://mkw.example.net\"\nname = \"Ana\"\npin = \"123456\"\n",
            "name = \"Ana\"\npin = \"123456\"\nadult_consent = true\n",
        })
        {
            var folder = Rig.NewFolder("migrate-not");
            var file = Path.Combine(folder, "client.toml");
            File.WriteAllText(file, toml, new UTF8Encoding(false));
            await using var other = new Launcher(file, folder, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, new FakeSeatServer());
            Assert.False(other.Settings.Ui.SetupDone);
        }
    }

    /// <summary>What can be at an address: an MKWMP server, an old one, a web site, JSON that is something else, a failing gateway, nothing.</summary>
    private sealed class ProbeServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage Answer(int status, string body, string type = "application/json") =>
                new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, type) };
            return request.RequestUri!.Host switch
            {
                "good.test" => Task.FromResult(Answer(200, """{"name":"fake","seatsTotal":32,"seated":12,"queued":0,"lobbyEndpoint":"lobby.test:8905"}""")),
                "old.test" => Task.FromResult(Answer(200, """{"name":"fake","seatsTotal":32,"seated":12,"queued":0}""")),
                "website.test" => Task.FromResult(Answer(404, "<html>not found</html>", "text/html")),
                "json.test" => Task.FromResult(Answer(200, """{"hello":"world"}""")),
                "failing.test" => Task.FromResult(Answer(502, "bad gateway", "text/plain")),
                _ => throw new HttpRequestException("no such host (the fake has none)"),
            };
        }
    }
}
