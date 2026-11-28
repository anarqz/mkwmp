// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Text;
using System.Text.Json.Nodes;
using Mkwmp.Core.Net;
using Mkwmp.Core.Overlay;

namespace Mkwmp.Core.Tests;

/// <summary>A clock a test moves by hand: the wall clock and the timestamps both (the 15 s rule, a toast's time).</summary>
internal sealed class StepClock : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero).AddMilliseconds(Interlocked.Read(ref _ticks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, (long)by.TotalMilliseconds);
}

/// <summary>
/// The current lobby and what the game draws (docs/contracts/LAUNCHER_API.md: "The current lobby", "In-game toasts",
/// "In-game overlay"): the roster route, the lobby held in memory (the rename flag within an online session, "left"
/// clears), that nothing is stored and what "Previous Races" left is deleted, the toast bridge and its routes, and
/// the overlay page's view and actions, beside the older overlay routes.
/// </summary>
public sealed class LobbyAndInGameTests
{
    private const string Auth = "Bearer t0ken";
    private const string Ana = "0a1b2c3d-1111-5222-8333-444455556666";
    private const string Cheat = "9f8e7d6c-aaaa-5bbb-8ccc-ddddeeeeffff";
    private const string Rin = "77777777-2222-5333-8444-555566667777";

    private static readonly UTF8Encoding Utf8 = new(false);

    private sealed class Host : ILocalApiHost
    {
        public string Token => "t0ken";
        public SeatSession? Seat => null;
        public IReadOnlyList<Friend>? FriendsOrNull => null;
        public ClientConfig Config { get; } = new();

        public string? EditVoice(Func<ClientConfig, ClientConfig> edit) => null;
    }

    private static JsonNode Body(LocalResponse r) => JsonNode.Parse(r.Body!)!;

    private static Roster Change(params (string Uid, string Name)[] members) =>
        new(Roster.Change, "", [.. members.Select((m, i) => new RosterMember(m.Uid, m.Name, i == 0))], 0);

    private static Roster Match(string key, params (string Uid, string Name)[] members) =>
        new(Roster.Match, key, [.. members.Select((m, i) => new RosterMember(m.Uid, m.Name, i == 0))], 0);

    private static readonly Roster Left = new(Roster.Left, "", [], 0);

    private static string Log(Launcher launcher)
    {
        using var file = new FileStream(launcher.Log.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    // ---- the roster route -----------------------------------------------------------------

    [Fact]
    public void A_roster_is_parsed_and_rows_without_an_id_are_dropped()
    {
        var roster = LocalApi.ParseRoster(Utf8.GetBytes("""
            {"event":"match","match":"12_3_0_12_1a2b","members":[
              {"ussid":12,"uid":"0a1b2c3d-1111-5222-8333-444455556666","name":"Ana","local":true,"lid":0,"sid":"00112233aabbccdd"},
              {"ussid":13,"uid":"9f8e7d6c-aaaa-5bbb-8ccc-ddddeeeeffff","name":"Ünï 🏎 코드","local":false,"lid":0,"sid":null},
              {"ussid":14,"name":"no id"},
              {"ussid":15,"uid":"","name":"empty id"},
              {"ussid":16,"uid":7,"name":"a number"},
              {"ussid":17,"uid":"77777777-2222-5333-8444-555566667777"}]}
            """))!;
        Assert.Equal((Roster.Match, "12_3_0_12_1a2b", 3), (roster.Event, roster.Key, roster.Dropped));
        Assert.Equal(
            [new RosterMember(Ana, "Ana", true), new RosterMember(Cheat, "Ünï 🏎 코드", false), new RosterMember(Rin, "", false)],
            roster.Members);

        // "left" needs no members and keeps none; a change has no key
        Assert.Equal((Roster.Left, 0), LocalApi.ParseRoster("""{"event":"left","match":null,"members":[]}"""u8) is { } left ? (left.Event, left.Members.Count) : default);
        Assert.Empty(LocalApi.ParseRoster("""{"event":"left"}"""u8)!.Members);
        Assert.Empty(LocalApi.ParseRoster(Utf8.GetBytes($$"""{"event":"left","members":[{"uid":"{{Ana}}","name":"x"}]}"""))!.Members);
        Assert.Equal("", LocalApi.ParseRoster("""{"event":"change","match":"k","members":[]}"""u8)!.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"members":[]}""")]
    [InlineData("""{"event":"joined","members":[]}""")]
    [InlineData("""{"event":5,"members":[]}""")]
    [InlineData("""{"event":"change"}""")]
    [InlineData("""{"event":"change","members":null}""")]
    [InlineData("""{"event":"change","members":{}}""")]
    [InlineData("""{"event":"change","members":["x"]}""")]
    [InlineData("""{"event":"match","match":7,"members":[]}""")]
    public void What_is_not_a_roster_is_refused(string body)
    {
        Assert.Null(LocalApi.ParseRoster(Utf8.GetBytes(body)));
    }

    [Fact]
    public void A_roster_is_capped_32_members_an_id_of_1024_bytes_a_name_of_64()
    {
        static string Members(int n) => string.Join(",", Enumerable.Range(0, n).Select(i => $$"""{"uid":"u{{i}}","name":"n{{i}}"}"""));
        Assert.Equal(32, LocalApi.ParseRoster(Utf8.GetBytes($$"""{"event":"change","members":[{{Members(32)}}]}"""))!.Members.Count);
        Assert.Null(LocalApi.ParseRoster(Utf8.GetBytes($$"""{"event":"change","members":[{{Members(33)}}]}""")));

        var longest = new string('a', 1024);
        var roster = LocalApi.ParseRoster(Utf8.GetBytes(
            $$"""{"event":"change","members":[{"uid":"{{longest}}","name":"ok"},{"uid":"{{longest}}b","name":"too long"},{"uid":"{{new string('é', 513)}}","name":"too long in bytes"},{"uid":"a\u0000b","name":"control"}]}"""))!;
        Assert.Equal((1, 3, longest), (roster.Members.Count, roster.Dropped, roster.Members[0].Uid));

        // a name: 64 bytes of UTF-8 at most, cut between characters; nothing that cannot be shown, or that turns the line around
        Assert.Equal(new string('a', 64), LocalApi.Shown(new string('a', 100), 64));
        Assert.Equal(new string('é', 32), LocalApi.Shown(new string('é', 40), 64));
        Assert.Equal("Ana Lu", LocalApi.Shown(" A\u0000n\na‮ ⁦Lu⁩\t ", 64));
        Assert.Equal("ok", LocalApi.ParseRoster("""{"event":"change","members":[{"uid":"u","name":"o‮k\r\n"}]}"""u8)!.Members[0].Name);
    }

    [Fact]
    public async Task The_route_takes_the_token_answers_204_400_401_and_nothing_is_written_to_disk()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        var bearer = "Bearer " + rig.Launcher.Token;
        Task<LocalResponse> Post(string body, string? authorization) =>
            api.HandleAsync("POST", "/local/v1/roster", authorization, Utf8.GetBytes(body), CancellationToken.None);
        var good = $$"""{"event":"change","match":null,"members":[{"ussid":1,"uid":"{{Ana}}","name":"Ana","local":true,"lid":0,"sid":null},{"ussid":2,"uid":"{{Cheat}}","name":"Turbo","local":false,"lid":0,"sid":null}]}""";
        var before = Directory.GetFileSystemEntries(rig.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains("logs", StringComparison.OrdinalIgnoreCase)).Order().ToList();
        var changes = 0;
        rig.Launcher.Changed += (_, _) => Interlocked.Increment(ref changes);

        Assert.Equal(401, (await Post(good, null)).Status);
        Assert.Equal((401, """{"state":"unauthorized"}"""), ((await Post(good, "Bearer nope")) is { } wrong ? (wrong.Status, wrong.Body) : default));
        Assert.Equal(405, (await api.HandleAsync("GET", "/local/v1/roster", bearer, default, CancellationToken.None)).Status);
        Assert.False(rig.Launcher.Lobby.InLobby); // nothing taken from a caller without the token

        var bad = await Post("""{"event":"change"}""", bearer);
        Assert.Equal((400, """{"state":"invalid"}"""), (bad.Status, bad.Body));
        var taken = await Post(good, bearer);
        Assert.Equal((204, (string?)null), (taken.Status, taken.Body));

        // the rig plays on an MKWMP server: the lobby says which; this PC's player first
        var lobby = rig.Launcher.Lobby;
        Assert.Equal((true, false, new Uri(FakeSeatServer.Url).Authority), (lobby.InLobby, lobby.OnNextendo, lobby.Server));
        Assert.Equal([(Ana, "Ana", true), (Cheat, "Turbo", false)], lobby.Members.Select(m => (m.Uid, m.Name, m.Local)));
        Assert.True(changes >= 1);

        // IN MEMORY ONLY: no races\ folder, no file of any kind beside client.toml; the log has counts, never an id or a name
        Assert.False(Directory.Exists(Path.Combine(rig.Root, "races")));
        Assert.Equal(before, Directory.GetFileSystemEntries(rig.Root, "*", SearchOption.AllDirectories).Where(f => !f.Contains("logs", StringComparison.OrdinalIgnoreCase)).Order().ToList());
        var log = Log(rig.Launcher);
        Assert.Contains("lobby: an online session started (2 players)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Cheat, log, StringComparison.Ordinal);
        Assert.DoesNotContain("Turbo", log, StringComparison.Ordinal);

        // the limit: a burst of 10, then one a second
        var answers = new List<int>();
        for (var i = 0; i < 12; i++)
        {
            answers.Add((await Post(good, bearer)).Status);
        }
        Assert.Contains(429, answers);
        Assert.Equal(204, answers[0]);
    }

    [Fact]
    public async Task On_Nextendo_the_lobby_names_Nextendo_and_no_server_of_ours()
    {
        await using var rig = new Rig(new FakeSeatServer(),
            config: c => c with { Nextendo = NextendoLink.Of("nextendo", 1234567, "bia", "SW-1111-2222-3333") },
            prepare: path => Assert.True(NextendoVault.Beside(path).Save("refresh-0")));
        ((ILocalApiHost)rig.Launcher).Roster(Change((Ana, "Ana"), (Cheat, "Turbo")));
        var lobby = rig.Launcher.Lobby;
        Assert.Equal((true, "nextendo.network"), (lobby.OnNextendo, lobby.Server));
        Assert.All(lobby.Members, m => Assert.False(LobbyRules.CanReportToHost(rig.Launcher, lobby, m))); // there the player copies a report
        Assert.Equal("Nextendo Network (nextendo.network)", LobbyReport.Network(lobby));
    }

    // ---- the lobby in memory --------------------------------------------------------------

    [Fact]
    public void The_lobby_is_who_is_in_it_now_and_a_rename_within_the_session_is_flagged()
    {
        var clock = new StepClock();
        var lobby = new CurrentLobby(clock);
        Assert.False(lobby.Apply(Left, true, "nextendo.network").Changed);     // nothing to leave
        Assert.False(lobby.Apply(Change(), true, "nextendo.network").Changed); // nobody with an id: no lobby yet
        Assert.Same(LobbyView.Empty, lobby.View);

        var first = lobby.Apply(Change((Ana, "Ana"), (Cheat, "Turbo")), true, "nextendo.network");
        Assert.Equal((true, true, false, false, 2), (first.Changed, first.Started, first.Ended, first.Match, first.Members));
        var started = clock.GetUtcNow();
        Assert.Equal([("p1", Ana, "Ana", true, false), ("p2", Cheat, "Turbo", false, false)],
            lobby.View.Members.Select(m => (m.Id, m.Uid, m.Name, m.Local, m.Renamed)));

        // the same roster again changes nothing; a match is said, and Rin joins
        Assert.False(lobby.Apply(Change((Ana, "Ana"), (Cheat, "Turbo")), true, "nextendo.network").Changed);
        clock.Advance(TimeSpan.FromMinutes(3));
        var match = lobby.Apply(Match("7_1_0_12_ab", (Ana, "Ana"), (Cheat, "Turbo"), (Rin, "Rin")), true, "nextendo.network");
        Assert.Equal((true, false, true, 3), (match.Changed, match.Started, match.Match, match.Members));

        // the same Player ID under another name: flagged, with both names, oldest first, and when each was first seen
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(lobby.Apply(Change((Ana, "Ana"), (Cheat, "xX_Drift_Xx"), (Rin, "Rin")), true, "nextendo.network").Changed);
        var cheat = lobby.View.Member("p2")!;
        Assert.Equal((Cheat, "xX_Drift_Xx", true), (cheat.Uid, cheat.Name, cheat.Renamed));
        Assert.Equal([new LobbyName("Turbo", started), new LobbyName("xX_Drift_Xx", started.AddMinutes(8))], cheat.Names);
        Assert.False(lobby.View.Member("p1")!.Renamed);
        Assert.False(lobby.View.Member("p3")!.Renamed);

        // they leave the lobby: gone from it. They come back under a third name: the same handle, still flagged (the session remembers)
        lobby.Apply(Change((Ana, "Ana"), (Rin, "Rin")), true, "nextendo.network");
        Assert.Null(lobby.View.Member("p2"));
        Assert.Equal(2, lobby.View.Members.Count);
        lobby.Apply(Change((Ana, "Ana"), (Rin, "Rin"), (Cheat, "NotTurbo")), true, "nextendo.network");
        Assert.Equal(["Turbo", "xX_Drift_Xx", "NotTurbo"], lobby.View.Member("p2")!.Names.Select(n => n.Name));
        // back to a name already seen: no new name, still flagged
        lobby.Apply(Change((Ana, "Ana"), (Rin, "Rin"), (Cheat, "Turbo")), true, "nextendo.network");
        Assert.Equal(("Turbo", 3), (lobby.View.Member("p2")!.Name, lobby.View.Member("p2")!.Names.Count));

        // a game that shows no name: listed, not a rename; said twice in one roster: once; this PC's player always first
        lobby.Apply(new Roster(Roster.Change, "", [new RosterMember(Rin, "", false), new RosterMember(Rin, "Rin", false), new RosterMember(Ana, "Ana", true)], 0), true, "nextendo.network");
        Assert.Equal([(Ana, "Ana", false), (Rin, "", false)], lobby.View.Members.Select(m => (m.Uid, m.Name, m.Renamed)));
    }

    [Fact]
    public void Left_clears_the_lobby_and_every_name_of_the_session()
    {
        var clock = new StepClock();
        var lobby = new CurrentLobby(clock);
        lobby.Apply(Change((Ana, "Ana"), (Cheat, "Turbo")), false, "mkw.example.net");
        lobby.Apply(Change((Ana, "Ana"), (Cheat, "xX_Drift_Xx")), false, "mkw.example.net");
        Assert.True(lobby.View.Member("p2")!.Renamed);
        Assert.Equal(new[] { Ana, "Ana", Cheat, "Turbo", "xX_Drift_Xx" }.Order(), lobby.Identities().Order());

        var left = lobby.Apply(Left, false, "mkw.example.net");
        Assert.Equal((true, true, 0), (left.Changed, left.Ended, left.Members));
        Assert.Same(LobbyView.Empty, lobby.View);
        Assert.False(lobby.View.InLobby);
        Assert.False(lobby.Apply(Left, false, "mkw.example.net").Changed); // already out
        // what a crash record of that game must leave out is still known (in memory), nothing else
        Assert.Contains(Cheat, lobby.Identities());

        // the next online session starts clean: the old names are forgotten, the handles start again
        Assert.True(lobby.Apply(Change((Ana, "Ana"), (Cheat, "xX_Drift_Xx")), false, "mkw.example.net").Started);
        var again = lobby.View.Member("p2")!;
        Assert.Equal((Cheat, false, 1), (again.Uid, again.Renamed, again.Names.Count));
        Assert.DoesNotContain("Turbo", lobby.Identities());

        // another network is another session too; and the game's end clears as "left" does
        var moved = lobby.Apply(Change((Ana, "Ana"), (Cheat, "Other")), true, "nextendo.network");
        Assert.Equal((true, true, true), (moved.Started, moved.Ended, lobby.View.OnNextendo));
        Assert.False(lobby.View.Member("p2")!.Renamed);
        Assert.True(lobby.End());
        Assert.False(lobby.End());
        Assert.Empty(lobby.View.Members);
    }

    [Fact]
    public async Task The_game_s_end_empties_the_lobby()
    {
        await using var rig = new Rig(new FakeSeatServer());
        ((ILocalApiHost)rig.Launcher).Roster(Change((Ana, "Ana"), (Cheat, "Turbo")));
        Assert.True(rig.Launcher.Lobby.InLobby);
        rig.Launcher.GameEnded(rig.Target.Detect(rig.Game)!, 0);
        Assert.False(rig.Launcher.Lobby.InLobby);
        Assert.Empty(rig.Launcher.Lobby.Members);
    }

    [Fact]
    public void A_report_is_the_id_the_names_of_this_session_and_the_network()
    {
        var clock = new StepClock();
        var lobby = new CurrentLobby(clock);
        lobby.Apply(Change((Ana, "Ana"), (Cheat, "Turbo")), true, "nextendo.network");
        clock.Advance(TimeSpan.FromMinutes(2));
        lobby.Apply(Change((Ana, "Ana"), (Cheat, "xX_Drift_Xx")), true, "nextendo.network");
        var player = lobby.View.Member("p2")!;
        var text = LobbyReport.Text(lobby.View, player, "1.5.5", clock.GetUtcNow());
        Assert.Contains("Player ID: " + Cheat + "\n", text, StringComparison.Ordinal);
        Assert.Contains("Name shown now: \"xX_Drift_Xx\"\n", text, StringComparison.Ordinal);
        Assert.Contains("Network: Nextendo Network (nextendo.network)\n", text, StringComparison.Ordinal);
        Assert.Contains("Launcher: MKWMP 1.5.5\n", text, StringComparison.Ordinal);
        Assert.Contains("in this online session (2):\n- \"Turbo\": first seen 2026-11-27 12:00:00 UTC\n- \"xX_Drift_Xx\": first seen 2026-11-27 12:02:00 UTC\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Ana, text, StringComparison.Ordinal); // nobody else is in it
        Assert.Equal($"Player ID {Cheat}. Names used: Turbo, xX_Drift_Xx", LobbyReport.Brief(player, 500));
        Assert.Equal(20, LobbyReport.Brief(player, 20).Length);
    }

    // ---- "Previous Races" is gone ---------------------------------------------------------

    [Fact]
    public async Task At_start_the_races_folder_of_the_old_history_is_deleted_and_only_a_count_is_logged()
    {
        await using var rig = new Rig(new FakeSeatServer(), prepare: path =>
        {
            var races = Path.Combine(Path.GetDirectoryName(path)!, "races");
            Directory.CreateDirectory(Path.Combine(races, "sub"));
            File.WriteAllText(Path.Combine(races, "lobby-2026-11-20-21-04-00.json"), $$"""{"v":1,"players":[{"uid":"{{Cheat}}","names":[{"name":"Turbo"}]}]}""");
            File.WriteAllText(Path.Combine(races, "lobby-2026-11-21-21-04-00.json.tmp"), "{}");
            File.WriteAllText(Path.Combine(races, "sub", "anything.txt"), "x");
        });
        var races = Path.Combine(rig.Root, "races");
        Assert.True(Directory.Exists(races));
        await rig.Launcher.StartAsync(CancellationToken.None);
        Assert.False(Directory.Exists(races));
        Assert.True(File.Exists(rig.ConfigPath)); // nothing else beside client.toml went
        var log = Log(rig.Launcher);
        Assert.Contains("the stored history of previous races was deleted (3 files)", log, StringComparison.Ordinal);
        Assert.DoesNotContain(Cheat, log, StringComparison.Ordinal);
        Assert.DoesNotContain("Turbo", log, StringComparison.Ordinal);

        // no folder: nothing to do, nothing said; a file named races is not ours
        Assert.Equal(0, CurrentLobby.RemoveLegacyHistory(rig.ConfigPath));
        var other = Rig.NewFolder("races-file");
        File.WriteAllText(Path.Combine(other, "races"), "a file");
        Assert.Equal(0, CurrentLobby.RemoveLegacyHistory(Path.Combine(other, "client.toml")));
        Assert.True(File.Exists(Path.Combine(other, "races")));
    }

    // ---- in-game toasts -------------------------------------------------------------------

    private static GameToastSnapshot Shown(params GameToast[] toasts) => new(toasts, GameToastSnapshot.DarkPalette);

    [Fact]
    public async Task The_toasts_answer_is_what_the_window_published_with_times_from_now()
    {
        var clock = new StepClock();
        using var api = new LocalApi(new Host(), clock);
        var bridge = api.Toasts;
        bridge.AttachWindow();
        var empty = await api.HandleAsync("GET", "/local/v1/toasts", "", Auth, default, default);
        Assert.Equal(200, empty.Status);
        Assert.Equal((1, 0L, 0), (Body(empty)["v"]!.GetValue<int>(), Body(empty)["seq"]!.GetValue<long>(), Body(empty)["toasts"]!.AsArray().Count));

        bridge.Publish(Shown(
            new GameToast("t1", ToastKind.FriendPlaying, "Luna is racing online now", "Nextendo Network", bridge.NowMs + 5000, 5000),
            new GameToast("t2", ToastKind.Warning, "Ünï 🏎 changed their name", "", bridge.NowMs + 6000, 6000)));
        Assert.Equal(1, bridge.Seq);
        bridge.Publish(bridge.Shown); // the same again: nothing moves
        Assert.Equal(1, bridge.Seq);
        clock.Advance(TimeSpan.FromMilliseconds(800));

        var answer = await api.HandleAsync("GET", "/local/v1/toasts", "", Auth, default, default);
        var palette = string.Join(",", GameToastSnapshot.DarkPalette.Select(p => $"\"{p.Key}\":\"{p.Value}\""));
        Assert.Equal("""{"v":1,"seq":1,"toasts":[{"id":"t1","kind":"friend","title":"Luna is racing online now","body":"Nextendo Network","icon":"play","expiresInMs":4200,"stayMs":5000},"""
            + """{"id":"t2","kind":"warning","title":"Ünï 🏎 changed their name","body":"","icon":"warning","expiresInMs":5200,"stayMs":6000}],"suppress":[],"palette":{""" + palette + "}}", answer.Body);

        // plain UTF-8 for the mod's reader: a character outside the basic plane is itself, an escaped backslash stays one
        Assert.Equal("""{"a":"🏎 \\uD83C\\uDFCE \u0001 é"}""", PlainJson.Unescape("""{"a":"🏎 \\uD83C\\uDFCE \u0001 é"}"""));
        // a disc is the launcher's Avatar: the same letters and colour as the window draws
        Assert.Equal(("A", "NT", "AL", "?", "XX"), (GameOverlay.InitialsOf("Alex"), GameOverlay.InitialsOf("NotTurbo"), GameOverlay.InitialsOf("Ana Lu"),
            GameOverlay.InitialsOf("  "), GameOverlay.InitialsOf("xX_Drift_Xx")));
        Assert.Equal("#FF12A86B", GameOverlay.DiscOf("Alex"));
        Assert.Equal(GameOverlay.DiscOf("Alex"), GameOverlay.DiscOf(" Alex "));

        // every kind has its wire name and icon
        Assert.Equal(["friend/user", "friend/play", "friend/userPlus", "info/info", "success/check", "warning/warning", "error/error"],
            Enum.GetValues<ToastKind>().Select(k => ToastBridge.KindName(k) + "/" + ToastBridge.IconName(k)));
        Assert.Equal([5, 6, 8, 6], new[] { (ToastKind.Info, false), (ToastKind.Warning, false), (ToastKind.Error, false), (ToastKind.Success, true) }
            .Select(x => (int)Toast.StayFor(x.Item1, x.Item2).TotalSeconds));
    }

    [Fact]
    public async Task The_toasts_long_poll_ends_at_a_change_and_the_polls_say_the_game_draws()
    {
        var clock = new StepClock();
        using var api = new LocalApi(new Host(), clock);
        var bridge = api.Toasts;
        bridge.AttachWindow();
        Assert.False(bridge.GameDraws); // nobody asked yet: the window over the game shows them

        var wait = api.HandleAsync("GET", "/local/v1/toasts", "wait=20&seq=0", Auth, default, default);
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        Assert.True(bridge.GameDraws); // waiting in a poll
        bridge.Publish(Shown(new GameToast("t1", ToastKind.Info, "Hello", "", bridge.NowMs + 5000, 5000)));
        var answer = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((200, 1L, "t1"), (answer.Status, Body(answer)["seq"]!.GetValue<long>(), Body(answer)["toasts"]![0]!["id"]!.GetValue<string>()));

        // another seq than the current one answers at once; so does no wait
        var stale = api.HandleAsync("GET", "/local/v1/toasts", "wait=20&seq=0", Auth, default, default);
        Assert.True(stale.IsCompleted);

        // asked within 15 s: still the game's; none for 15 s: the window's again
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.True(bridge.GameDraws);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(bridge.GameDraws);

        Assert.Equal(400, (await api.HandleAsync("GET", "/local/v1/toasts", "wait=soon", Auth, default, default)).Status);
        Assert.Equal(400, (await api.HandleAsync("GET", "/local/v1/toasts", "seq=-1", Auth, default, default)).Status);
        Assert.Equal((401, """{"state":"unauthorized"}"""), (await api.HandleAsync("GET", "/local/v1/toasts", "", "Bearer nope", default, default)) is { } r ? (r.Status, r.Body) : default);
        Assert.Equal(405, (await api.HandleAsync("POST", "/local/v1/toasts", "", Auth, default, default)).Status);
    }

    [Fact]
    public async Task A_toast_is_dismissed_from_the_game_and_nothing_else()
    {
        var clock = new StepClock();
        using var api = new LocalApi(new Host(), clock);
        var bridge = api.Toasts;
        bridge.AttachWindow();
        var asked = new List<GameToastRequest>();
        bridge.Requested += (_, r) => asked.Add(r);
        bridge.Publish(Shown(new GameToast("t 7", ToastKind.Info, "Hello", "", bridge.NowMs + 5000, 5000)));
        Task<LocalResponse> Act(string id, string body, string? auth = Auth) =>
            api.HandleAsync("POST", $"/local/v1/toasts/{id}/action", "", auth, Utf8.GetBytes(body), default);

        Assert.Equal((200, """{"ok":true}"""), (await Act("t%207", """{"action":"dismiss"}""")) is { } ok ? (ok.Status, ok.Body) : default); // the id is percent-encoded
        Assert.Equal([new GameToastRequest("t 7", GameToastAction.Dismiss)], asked);
        Assert.Equal(404, (await Act("t9", """{"action":"dismiss"}""")).Status);
        Assert.Equal("gone", Body(await Act("t9", """{"action":"dismiss"}"""))["state"]!.GetValue<string>());
        Assert.Equal(409, (await Act("t%207", """{"action":"accept"}""")).Status);  // reserved: nothing here is accepted
        Assert.Equal(409, (await Act("t%207", """{"action":"decline"}""")).Status);
        Assert.Equal(400, (await Act("t%207", """{"action":"explode"}""")).Status);
        Assert.Equal(400, (await Act("t%207", "nope")).Status);
        Assert.Equal(401, (await Act("t%207", """{"action":"dismiss"}""", "Bearer nope")).Status);
        Assert.Equal(405, (await api.HandleAsync("GET", "/local/v1/toasts/t1/action", "", Auth, default, default)).Status);
        Assert.Equal(404, (await api.HandleAsync("POST", "/local/v1/toasts/a/b/action", "", Auth, default, default)).Status);
        Assert.Equal(404, (await api.HandleAsync("POST", "/local/v1/toasts//action", "", Auth, default, default)).Status);
        Assert.Single(asked);
    }

    [Fact]
    public void Without_a_window_the_bridge_shows_the_core_s_own_toasts_four_at_most_for_their_time()
    {
        var clock = new StepClock();
        var bridge = new ToastBridge(clock);
        bridge.Offer(new Toast("one", "", ToastKind.Info));                         // 5 s
        bridge.Offer(new Toast("two", "", ToastKind.Error));                        // 8 s
        Assert.Equal(["one", "two"], JsonNode.Parse(bridge.Answer())!["toasts"]!.AsArray().Select(t => t!["title"]!.GetValue<string>()));
        Assert.Equal(2, bridge.Seq);

        clock.Advance(TimeSpan.FromSeconds(6));
        var later = JsonNode.Parse(bridge.Answer())!;
        Assert.Equal(("two", 2000L, 8000L), (later["toasts"]![0]!["title"]!.GetValue<string>(), later["toasts"]![0]!["expiresInMs"]!.GetValue<long>(), later["toasts"]![0]!["stayMs"]!.GetValue<long>()));
        Assert.Single(later["toasts"]!.AsArray());

        foreach (var n in Enumerable.Range(3, 5))
        {
            bridge.Offer(new Toast("n" + n, "", ToastKind.Success) { Always = true });
        }
        Assert.Equal(["n4", "n5", "n6", "n7"], bridge.Shown.Toasts.Select(t => t.Title)); // the oldest go
        Assert.All(bridge.Shown.Toasts, t => Assert.Equal(6000, t.StayMs));

        // a dismiss takes one of its own off; once a window attached the bridge shows only what the window publishes
        var id = bridge.Shown.Toasts[0].Id;
        Assert.Equal(200, bridge.Action(id, """{"action":"dismiss"}"""u8.ToArray()).Status);
        Assert.Equal(3, bridge.Shown.Toasts.Count);
        bridge.AttachWindow();
        bridge.Offer(new Toast("late", "", ToastKind.Info));
        Assert.DoesNotContain(bridge.Shown.Toasts, t => t.Title == "late");
    }

    [Fact]
    public async Task The_launcher_s_own_toasts_reach_the_game_when_there_is_no_window()
    {
        await using var rig = new Rig(new FakeSeatServer());
        var heard = new List<Toast>();
        rig.Launcher.Toasted += (_, t) => heard.Add(t);
        rig.Launcher.ShowTestToast();
        var toast = Assert.Single(heard);
        Assert.Equal([toast.Title], rig.Launcher.ToastBridge.Shown.Toasts.Select(t => t.Title));
    }

    // ---- the in-game overlay page ---------------------------------------------------------

    [Fact]
    public async Task The_view_is_503_until_a_launcher_is_attached_and_the_old_overlay_routes_still_answer()
    {
        using var api = new LocalApi(new Host(), TimeProvider.System);
        var view = await api.HandleAsync("GET", "/local/v1/overlay/view", "", Auth, default, default);
        Assert.Equal((503, "unavailable"), (view.Status, Body(view)["state"]!.GetValue<string>()));
        Assert.Equal(503, (await api.HandleAsync("POST", "/local/v1/overlay/action", "", Auth, """{"action":"show","args":{"pane":"lobby"}}"""u8.ToArray(), default)).Status);
        Assert.Equal(401, (await api.HandleAsync("GET", "/local/v1/overlay/view", "", "Bearer nope", default, default)).Status);
        Assert.Equal(405, (await api.HandleAsync("POST", "/local/v1/overlay/view", "", Auth, default, default)).Status);
        Assert.Equal(405, (await api.HandleAsync("GET", "/local/v1/overlay/action", "", Auth, default, default)).Status);

        // the current mod's routes are untouched: both overlays coexist
        var state = await api.HandleAsync("GET", "/local/v1/overlay/state", "", Auth, default, default);
        Assert.Equal((200, """{"open":false,"seq":0,"enabled":false}"""), (state.Status, state.Body));
        Assert.Equal(409, (await api.HandleAsync("POST", "/local/v1/overlay/open", "by=guide&pid=7", Auth, default, default)).Status);
    }

    [Fact]
    public async Task The_view_shows_the_current_lobby_with_the_rename_flag_and_the_own_row()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        api.Page.Attach(rig.Launcher, _ => { });
        var bearer = "Bearer " + rig.Launcher.Token;
        Task<LocalResponse> View(string query = "") => api.HandleAsync("GET", "/local/v1/overlay/view", query, bearer, default, default);
        var host = (ILocalApiHost)rig.Launcher;

        // out of a lobby: the page opens on Game, and the Lobby pane only says so
        var out0 = Body(await View());
        Assert.Equal((1, "game", false, Strings.T("lobby.empty")),
            (out0["v"]!.GetValue<int>(), out0["start"]!.GetValue<string>(), out0["lobby"]!["inLobby"]!.GetValue<bool>(), out0["lobby"]!["note"]!.GetValue<string>()));
        Assert.Empty(out0["lobby"]!["players"]!.AsArray());
        Assert.Equal(["game", "lobby"], out0["entries"]!.AsArray().Select(e => e!["key"]!.GetValue<string>()));
        Assert.Equal(("MKW", Strings.T("quitgame.title")), (out0["game"]!["name"]!.GetValue<string>(), out0["game"]!["quit"]!["title"]!.GetValue<string>()));
        Assert.Equal(GameOverlay.LightPalette.Count, out0["palette"]!.AsObject().Count);
        var seq = out0["seq"]!.GetValue<long>();

        // a roster ends the waiting poll: the players right away
        var wait = View($"wait=20&seq={seq}");
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);
        Assert.True(api.Page.GameDraws);
        host.Roster(Change((Ana, "Ana"), (Cheat, "Turbo"), (Rin, "")));
        var view = Body(await wait.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(view["seq"]!.GetValue<long>() > seq);
        Assert.Equal(("lobby", true, "mkwmp", ""), (view["start"]!.GetValue<string>(), view["lobby"]!["inLobby"]!.GetValue<bool>(),
            view["lobby"]!["network"]!.GetValue<string>(), view["lobby"]!["note"]!.GetValue<string>()));
        var players = view["lobby"]!["players"]!.AsArray();
        Assert.Equal([("p1", "Ana", true, false), ("p2", "Turbo", false, false), ("p3", Strings.T("lobby.no_name"), false, false)],
            players.Select(p => (p!["id"]!.GetValue<string>(), p["name"]!.GetValue<string>(), p["you"]!.GetValue<bool>(), p["renamed"]!.GetValue<bool>())));
        Assert.Equal([false, true, true], players.Select(p => p!["canCopyReport"]!.GetValue<bool>())); // nobody reports themselves
        Assert.Equal(Cheat, players[1]!["playerId"]!.GetValue<string>());

        // a rename mid-session: flagged, with the names
        host.Roster(Change((Ana, "Ana"), (Cheat, "xX_Drift_Xx"), (Rin, "")));
        var renamed = Body(await View())["lobby"]!["players"]![1]!;
        Assert.Equal(("xX_Drift_Xx", true, Strings.F("lobby.used_names", ("n", "2"))),
            (renamed["name"]!.GetValue<string>(), renamed["renamed"]!.GetValue<bool>(), renamed["renamedLabel"]!.GetValue<string>()));
        Assert.Equal(["Turbo", "xX_Drift_Xx"], renamed["names"]!.AsArray().Select(n => n!.GetValue<string>()));

        // "left" empties it again
        host.Roster(Left);
        var gone = Body(await View());
        Assert.Equal(("game", false), (gone["start"]!.GetValue<string>(), gone["lobby"]!["inLobby"]!.GetValue<bool>()));
        Assert.Empty(gone["lobby"]!["players"]!.AsArray());
    }

    [Fact]
    public async Task The_actions_show_copy_a_report_and_refuse_what_cannot_be_done()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        var toasts = new List<Toast>();
        api.Page.Attach(rig.Launcher, toasts.Add);
        var bearer = "Bearer " + rig.Launcher.Token;
        Task<LocalResponse> Act(string body) => api.HandleAsync("POST", "/local/v1/overlay/action", "", bearer, Utf8.GetBytes(body), default);
        ((ILocalApiHost)rig.Launcher).Roster(Change((Ana, "Ana"), (Cheat, "Ünï 🏎"), (Rin, "Rin")));

        // the mod says which pane it shows ("" closed)
        Assert.False(api.Page.IsOpen);
        Assert.Equal((200, """{"ok":true}"""), (await Act("""{"action":"show","args":{"pane":"lobby"}}""")) is { } shown ? (shown.Status, shown.Body) : default);
        Assert.Equal((true, "lobby"), (api.Page.IsOpen, api.Page.Pane));
        Assert.Equal(400, (await Act("""{"action":"show","args":{"pane":"friends"}}""")).Status);
        Assert.Equal(200, (await Act("""{"action":"show","args":{"pane":""}}""")).Status);
        Assert.False(api.Page.IsOpen);

        // Copy Report: refused while there is no window to hold the clipboard; then the text goes there and a toast says where to send it
        var none = await Act("""{"action":"copyReport","args":{"id":"p2"}}""");
        Assert.Equal((409, Strings.T("lobby.copy_failed")), (none.Status, Body(none)["message"]!.GetValue<string>()));
        string? copied = null;
        api.Page.CopyText = text => { copied = text; return true; };
        toasts.Clear();
        Assert.Equal(200, (await Act("""{"action":"copyReport","args":{"id":"p2"}}""")).Status);
        Assert.Contains("Player ID: " + Cheat, copied!, StringComparison.Ordinal);
        Assert.Contains("MKWMP server " + new Uri(FakeSeatServer.Url).Authority, copied!, StringComparison.Ordinal);
        Assert.Equal((Strings.T("lobby.report.copied"), ToastKind.Success), (toasts.Single().Title, toasts.Single().Kind));
        Assert.Equal(404, (await Act("""{"action":"copyReport","args":{"id":"p1"}}""")).Status); // the player's own row
        Assert.Equal(404, (await Act("""{"action":"copyReport","args":{"id":"p9"}}""")).Status);

        // Report to the host: only a member that server can name, by a player who may report there
        Assert.Equal(404, (await Act("""{"action":"report","args":{"id":"p9","reason":"other"}}""")).Status);
        Assert.Equal(409, (await Act("""{"action":"report","args":{"id":"p2","reason":"other"}}""")).Status); // not a gamertag
        var lobby = rig.Launcher.Lobby;
        Assert.False(LobbyRules.CanReportToHost(rig.Launcher, lobby, lobby.Member("p1")!));
        Assert.False(LobbyRules.CanReportToHost(rig.Launcher, lobby, lobby.Member("p2")!));
        Assert.Equal(LobbyRules.ReportAllowed(rig.Launcher), LobbyRules.CanReportToHost(rig.Launcher, lobby, lobby.Member("p3")!));

        // the rest
        Assert.Equal(200, (await Act("""{"action":"quitGame"}""")).Status); // the mod then ends the game through the runtime
        Assert.Equal(400, (await Act("""{"action":"befriend","args":{"id":"p2"}}""")).Status); // no Add Friend in the lobby pane
        Assert.Equal(400, (await Act("""{"action":"show","args":[]}""")).Status);
        Assert.Equal(400, (await Act("nope")).Status);
        Assert.Equal(401, (await api.HandleAsync("POST", "/local/v1/overlay/action", "", "Bearer nope", """{"action":"quitGame"}"""u8.ToArray(), default)).Status);
    }
}
