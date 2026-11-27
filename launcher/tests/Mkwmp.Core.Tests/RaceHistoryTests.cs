// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using System.Text;
using Mkwmp.Core.Diagnostics;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// Previous races (docs/contracts/LAUNCHER_API.md, <c>POST /local/v1/roster</c>): the route, the store in
/// <c>races\</c> (one file per online session, the names each id used, the caps, the retention, a broken file), the
/// report a player copies, and that none of it reaches a bug report or the log.
/// </summary>
public sealed class RaceHistoryTests
{
    private const string Ana = "0a1b2c3d-1111-5222-8333-444455556666";
    private const string Cheat = "9f8e7d6c-aaaa-5bbb-8ccc-ddddeeeeffff";
    private const string Rin = "77777777-2222-5333-8444-555566667777";

    private static readonly UTF8Encoding Utf8 = new(false);

    private static (RaceHistory History, ManualClock Clock, string Folder) Store()
    {
        var clock = new ManualClock();
        var folder = Path.Combine(Rig.NewFolder("races"), RaceHistory.FolderName);
        return (new RaceHistory(folder, clock), clock, folder);
    }

    private static Roster Change(params (string Uid, string Name)[] members) =>
        new(Roster.Change, "", [.. members.Select((m, i) => new RosterMember(m.Uid, m.Name, i == 0))], 0);

    private static Roster Match(string key, params (string Uid, string Name)[] members) =>
        new(Roster.Match, key, [.. members.Select((m, i) => new RosterMember(m.Uid, m.Name, i == 0))], 0);

    private static readonly Roster Left = new(Roster.Left, "", [], 0);

    private static RosterOutcome Put(RaceHistory history, Roster roster, string backend = RaceSession.Nextendo, string server = "nextendo.network") =>
        history.Record(roster, backend, server);

    // ---- the route ------------------------------------------------------------------------

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
        Assert.Equal(string.Concat(Enumerable.Repeat("🏎", 16)), LocalApi.Shown(string.Concat(Enumerable.Repeat("🏎", 20)), 64));
        Assert.Equal(new string('a', 63), LocalApi.Shown(new string('a', 63) + "é", 64));
        Assert.Equal("Ana Lu", LocalApi.Shown(" A\u0000n\na\u202E \u2066Lu\u2069\t ", 64));
        Assert.Equal("ok", LocalApi.ParseRoster("""{"event":"change","members":[{"uid":"u","name":"o\u202Ek\r\n"}]}"""u8)!.Members[0].Name);
    }

    [Fact]
    public async Task The_route_takes_the_token_and_answers_204_400_401()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        var bearer = "Bearer " + rig.Launcher.Token;
        Task<LocalResponse> Post(string body, string? authorization) =>
            api.HandleAsync("POST", "/local/v1/roster", authorization, Utf8.GetBytes(body), CancellationToken.None);
        var good = $$"""{"event":"change","match":null,"members":[{"ussid":1,"uid":"{{Ana}}","name":"Ana","local":true,"lid":0,"sid":null}]}""";

        Assert.Equal(401, (await Post(good, null)).Status);
        Assert.Equal((401, """{"state":"unauthorized"}"""), ((await Post(good, "Bearer nope")) is { } wrong ? (wrong.Status, wrong.Body) : default));
        Assert.Equal(405, (await api.HandleAsync("GET", "/local/v1/roster", bearer, default, CancellationToken.None)).Status);
        Assert.Empty(await rig.Launcher.RaceSessionsAsync()); // nothing kept from a caller without the token

        var bad = await Post("""{"event":"change"}""", bearer);
        Assert.Equal((400, """{"state":"invalid"}"""), (bad.Status, bad.Body));
        var stored = await Post(good, bearer);
        Assert.Equal((204, (string?)null), (stored.Status, stored.Body));

        var sessions = await rig.Launcher.RaceSessionsAsync();
        var session = Assert.Single(sessions);
        // the rig plays on an MKWMP server: the session says which
        Assert.Equal((RaceSession.Mkwmp, new Uri(FakeSeatServer.Url).Authority), (session.Backend, session.Server));
        Assert.Equal([new RacePlayer(Ana, true, session.Players[0].Names)], session.Players);
        Assert.Equal("Ana", session.Players[0].Latest);
        Assert.True(File.Exists(Path.Combine(rig.Root, "races", session.Id + ".json"))); // races\ beside client.toml
        Assert.Equal(1, rig.Launcher.RaceHistoryRevision);

        // the limit: a burst of 10, then one a second (three were asked with the token above)
        var answers = new List<int>();
        for (var i = 0; i < 12; i++)
        {
            answers.Add((await Post(good, bearer)).Status);
        }
        Assert.Contains(429, answers);
        Assert.Equal(204, answers[0]);
    }

    [Fact]
    public async Task On_Nextendo_the_session_names_Nextendo_and_no_server_of_ours()
    {
        await using var rig = new Rig(new FakeSeatServer(),
            config: c => c with { Nextendo = NextendoLink.Of("nextendo", 1234567, "bia", "SW-1111-2222-3333") },
            prepare: path => Assert.True(NextendoVault.Beside(path).Save("refresh-0")));
        ((ILocalApiHost)rig.Launcher).Roster(Change((Ana, "Ana"), (Cheat, "Turbo")));
        var session = Assert.Single(await rig.Launcher.RaceSessionsAsync());
        Assert.Equal((RaceSession.Nextendo, "nextendo.network", true), (session.Backend, session.Server, session.OnNextendo));
    }

    // ---- the store ------------------------------------------------------------------------

    [Fact]
    public void A_session_is_one_file_named_by_the_local_time_of_its_first_roster()
    {
        var (history, clock, folder) = Store();
        Assert.False(Put(history, Left).Changed);           // nothing to leave
        Assert.False(Put(history, Change()).Changed);       // nobody with an id: no session yet
        Assert.False(Directory.Exists(folder));

        var first = Put(history, Change((Ana, "Ana"), (Cheat, "Turbo")));
        Assert.Equal((true, true, false, 2), (first.Changed, first.Started, first.Match, first.Members));
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), TimeZoneInfo.Local);
        var name = $"lobby-{local:yyyy-MM-dd-HH-mm-ss}.json";
        Assert.Equal([name], Directory.GetFiles(folder).Select(Path.GetFileName));
        var started = clock.GetUtcNow();

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(Put(history, Match("7_1_0_12_ab", (Ana, "Ana"), (Cheat, "Turbo"), (Rin, "Rin"))).Match);
        clock.Advance(TimeSpan.FromMinutes(5));
        Put(history, Change((Ana, "Ana"), (Cheat, "xX_Drift_Xx"), (Rin, "Rin")));
        clock.Advance(TimeSpan.FromMinutes(1));
        Put(history, Match("7_2_0_12_ab", (Ana, "Ana"), (Cheat, "xX_Drift_Xx"), (Rin, "Rin")));
        Put(history, Match("7_2_0_12_ab", (Ana, "Ana"), (Cheat, "xX_Drift_Xx"))); // said twice: still one match
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Put(history, Left).Ended);
        Assert.Single(Directory.GetFiles(folder)); // one file, and no temporary left behind

        var session = Assert.Single(history.Sessions());
        Assert.Equal((Path.GetFileNameWithoutExtension(name), started, started.AddMinutes(11), RaceSession.Nextendo, "nextendo.network"),
            (session.Id, session.Started, session.Ended, session.Backend, session.Server));
        Assert.Equal([Ana, Cheat, Rin], session.Players.Select(p => p.Uid));
        Assert.Equal([true, false, false], session.Players.Select(p => p.Local));
        Assert.Equal(1, session.Renamed);

        // the same id, two names, each with when it was first and last seen
        var cheat = session.Players[1];
        Assert.True(cheat.Renamed);
        Assert.Equal("xX_Drift_Xx", cheat.Latest);
        Assert.Equal(
            [new RaceName("Turbo", started, started.AddMinutes(3)), new RaceName("xX_Drift_Xx", started.AddMinutes(8), started.AddMinutes(9))],
            cheat.Names);
        Assert.Equal([new RaceName("Ana", started, started.AddMinutes(9))], session.Players[0].Names);

        Assert.Equal(["7_1_0_12_ab", "7_2_0_12_ab"], session.Matches.Select(m => m.Key));
        Assert.Equal([started.AddMinutes(3), started.AddMinutes(9)], session.Matches.Select(m => m.Time));
        Assert.Equal([new RaceMember(Ana, "Ana"), new RaceMember(Cheat, "Turbo"), new RaceMember(Rin, "Rin")], session.Matches[0].Members);
        Assert.Equal([new RaceMember(Ana, "Ana"), new RaceMember(Cheat, "xX_Drift_Xx")], session.Matches[1].Members);
        Assert.Equal(session.Id, history.Load(session.Id)?.Id);
    }

    [Fact]
    public void Ten_minutes_without_a_roster_or_left_or_another_network_ends_the_session()
    {
        var (history, clock, folder) = Store();
        Assert.True(Put(history, Change((Ana, "Ana"))).Started);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(Put(history, Change((Ana, "Ana"), (Rin, "Rin"))).Started); // within ten minutes: the same session
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(Put(history, Change((Ana, "Ana"))).Started);
        Assert.Single(Directory.GetFiles(folder));

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.True(Put(history, Change((Ana, "Ana"), (Cheat, "Turbo"))).Started); // ten minutes of silence: another
        Assert.Equal(2, Directory.GetFiles(folder).Length);

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(Put(history, Left).Ended);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(Put(history, Change((Ana, "Ana"))).Started); // after "left": another
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(Put(history, Change((Ana, "Ana")), RaceSession.Mkwmp, "mkw.example.net").Started); // another network: another

        var sessions = history.Sessions();
        Assert.Equal(4, sessions.Count);
        Assert.Equal(sessions.OrderByDescending(s => s.Started).Select(s => s.Id), sessions.Select(s => s.Id)); // newest first
        Assert.Equal((RaceSession.Mkwmp, "mkw.example.net"), (sessions[0].Backend, sessions[0].Server));
        // the first one ended at its last roster, not when the silence was noticed
        Assert.Equal(sessions[3].Started.AddMinutes(18), sessions[3].Ended);
        Assert.Equal([Ana, Rin], sessions[3].Players.Select(p => p.Uid));

        // two sessions that start in the same second are two files
        Put(history, Left);
        Assert.True(Put(history, Change((Ana, "Ana"))).Started);
        Assert.Equal(5, history.Sessions().Count);
        Assert.Contains(Directory.GetFiles(folder), f => f.EndsWith("-2.json", StringComparison.Ordinal));
    }

    [Fact]
    public void A_player_s_names_are_gathered_across_every_stored_session()
    {
        var (history, clock, _) = Store();
        var t0 = clock.GetUtcNow();
        Put(history, Match("a", (Ana, "Ana"), (Cheat, "Turbo")));
        Put(history, Left);
        clock.Advance(TimeSpan.FromHours(1));
        Put(history, Change((Ana, "Ana"), (Rin, "Rin")));
        Put(history, Left);
        clock.Advance(TimeSpan.FromDays(1));
        Put(history, Match("b", (Ana, "Ana"), (Cheat, "NotTurbo")));
        clock.Advance(TimeSpan.FromMinutes(4));
        Put(history, Match("c", (Ana, "Ana"), (Cheat, "Turbo")));
        Put(history, Left);

        var cheat = history.Player(Cheat)!;
        Assert.Equal((Cheat, false, true, "Turbo"), (cheat.Uid, cheat.Local, cheat.Renamed, cheat.Latest));
        var t2 = t0.AddHours(25);
        Assert.Equal([new RaceName("Turbo", t0, t2.AddMinutes(4)), new RaceName("NotTurbo", t2, t2)], cheat.Names);
        Assert.Equal(2, cheat.Sightings.Count); // not the session they were not in
        Assert.Equal([t2, t0], cheat.Sightings.Select(s => s.Session.Started));
        Assert.Equal(["b", "c"], cheat.Sightings[0].Matches.Select(m => m.Key));

        var me = history.Player(Ana)!;
        Assert.True(me.Local);
        Assert.False(me.Renamed);
        Assert.Equal(3, me.Sightings.Count);
        Assert.Null(history.Player("nobody"));
        Assert.Superset(new HashSet<string> { Ana, Cheat, "Ana", "NotTurbo", "Turbo" }, history.RecentIdentities().ToHashSet());
    }

    [Fact]
    public void A_file_is_capped_in_players_names_and_matches()
    {
        var (history, clock, _) = Store();
        // more ids than a file keeps: the ones after are left out
        for (var i = 0; i < 10; i++)
        {
            Put(history, Change([.. Enumerable.Range(0, 32).Select(n => ($"uid-{i}-{n}", $"p{n}"))]));
        }
        Assert.Equal(RaceHistory.MaxPlayers, Assert.Single(history.Sessions()).Players.Count);
        Put(history, Left);

        // more names under one id than a file keeps
        clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < RaceHistory.MaxNames + 10; i++)
        {
            Put(history, Change((Cheat, $"name{i}")));
        }
        var names = history.Sessions()[0].Players[0].Names;
        Assert.Equal((RaceHistory.MaxNames, "name0"), (names.Count, names[0].Name));
        Put(history, Left);

        // more matches than a file keeps: the next file
        clock.Advance(TimeSpan.FromHours(1));
        for (var i = 0; i < RaceHistory.MaxMatches + 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Put(history, Match($"key{i}", (Ana, "Ana"), (Cheat, "Turbo")));
        }
        var sessions = history.Sessions();
        Assert.Equal(4, sessions.Count);
        Assert.Equal((3, RaceHistory.MaxMatches), (sessions[0].Matches.Count, sessions[1].Matches.Count));
        Assert.All(sessions, s => Assert.True(new FileInfo(Path.Combine(history.Folder, s.Id + ".json")).Length < 512 * 1024));
    }

    [Fact]
    public void Sessions_are_kept_30_days_and_200_files_at_most()
    {
        var (history, clock, folder) = Store();
        Directory.CreateDirectory(folder);
        string Name(DateTimeOffset utc) => $"lobby-{TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.Local):yyyy-MM-dd-HH-mm-ss}.json";
        var now = clock.GetUtcNow();
        File.WriteAllText(Path.Combine(folder, Name(now.AddDays(-31))), "{}");
        File.WriteAllText(Path.Combine(folder, Name(now.AddDays(-29))), "{}");
        File.WriteAllText(Path.Combine(folder, "lobby-2026-11-01-10-00-00.json.tmp"), "half");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not ours");
        Assert.Equal(1, history.Prune());
        Assert.Equal(new[] { Name(now.AddDays(-29)), "notes.txt" }.Order(), Directory.GetFiles(folder).Select(Path.GetFileName).Order());

        // 200 files at most: making one more drops the oldest
        for (var i = 0; i < 205; i++)
        {
            File.WriteAllText(Path.Combine(folder, Name(now.AddDays(-20).AddMinutes(i))), "{}");
        }
        Assert.Equal(6, history.Prune()); // 206 session files
        Assert.Equal(200, Directory.GetFiles(folder, "lobby-*.json").Length);
        Assert.False(File.Exists(Path.Combine(folder, Name(now.AddDays(-29)))));
        Assert.True(Put(history, Change((Ana, "Ana"))).Started);
        Assert.Equal(200, Directory.GetFiles(folder, "lobby-*.json").Length);
        Assert.True(File.Exists(Path.Combine(folder, Name(now))));
        Assert.False(File.Exists(Path.Combine(folder, Name(now.AddDays(-20).AddMinutes(5)))));
        Assert.True(File.Exists(Path.Combine(folder, Name(now.AddDays(-20).AddMinutes(6)))));

        // a month later the launcher starts: all of it is gone, but what is not ours
        clock.Advance(TimeSpan.FromDays(31));
        Assert.Equal(200, new RaceHistory(folder, clock).Prune());
        Assert.Equal(["notes.txt"], Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_launcher_prunes_at_start()
    {
        await using var rig = new Rig(new FakeSeatServer());
        var folder = Path.Combine(rig.Root, RaceHistory.FolderName);
        Directory.CreateDirectory(folder);
        var old = Path.Combine(folder, $"lobby-{DateTime.Now.AddDays(-40):yyyy-MM-dd-HH-mm-ss}.json");
        var recent = Path.Combine(folder, $"lobby-{DateTime.Now.AddDays(-2):yyyy-MM-dd-HH-mm-ss}.json");
        File.WriteAllText(old, "{}");
        File.WriteAllText(recent, "{}");
        await rig.Launcher.StartAsync(CancellationToken.None);
        Assert.Equal((false, true), (File.Exists(old), File.Exists(recent)));
    }

    [Fact]
    public void A_broken_file_is_left_out_and_breaks_nothing()
    {
        var (history, clock, folder) = Store();
        Put(history, Match("a", (Ana, "Ana"), (Cheat, "Turbo")));
        Put(history, Left);
        var good = Assert.Single(history.Sessions());
        File.WriteAllText(Path.Combine(folder, "lobby-2026-11-20-10-00-00.json"), "{ \"started\": \"2026-11-20T10:00");
        File.WriteAllText(Path.Combine(folder, "lobby-2026-11-20-10-00-01.json"), "");
        File.WriteAllText(Path.Combine(folder, "lobby-2026-11-20-10-00-02.json"), "[1,2,3]");
        File.WriteAllText(Path.Combine(folder, "lobby-2026-11-20-10-00-03.json"), """{"started":"yesterday","players":[]}""");
        File.WriteAllBytes(Path.Combine(folder, "lobby-2026-11-20-10-00-04.json"), [0x7b, 0x22, 0xff, 0xfe, 0x22, 0x3a, 0x31, 0x7d]);
        File.WriteAllText(Path.Combine(folder, "lobby-2026-11-20-10-00-05.json"), """{"started":"2026-11-20T10:00:05Z","players":"many"}""");
        File.WriteAllText(Path.Combine(folder, "lobby-evil.json"), """{"started":"2026-11-20T10:00:05Z","players":[]}""");
        // a file someone edited: what can be read of it is read
        File.WriteAllText(Path.Combine(folder, "lobby-2026-11-20-10-00-06.json"),
            """
            {"started":"2026-11-20T10:00:06Z","backend":"other","players":[{"uid":"u1","names":[{"name":"A"},{"name":"A"},{"name":7},{"name":"B","first":"x","last":"2026-11-20T10:05:00Z"}]},
              {"uid":"u1"},{"uid":5},{"name":"no uid"},"text",{"uid":"u2","local":true,"names":"none"}],
             "matches":[{"time":"2026-11-20T10:01:00Z","members":[{"p":0,"name":"A"},{"p":99},{"p":-1},{"p":"0"},{"p":5,"name":"u2"}]},{"key":"no time"},7]}
            """);

        var sessions = history.Sessions();
        Assert.Equal(2, sessions.Count);
        Assert.Equal(good.Id, sessions[0].Id);
        var edited = sessions[1];
        Assert.Equal((RaceSession.Mkwmp, "", edited.Started), (edited.Backend, edited.Server, edited.Ended));
        Assert.Equal(["u1", "u2"], edited.Players.Select(p => p.Uid));
        Assert.Equal(["A", "B"], edited.Players[0].Names.Select(n => n.Name));
        Assert.True(edited.Players[1].Local);
        Assert.Equal([new RaceMember("u1", "A"), new RaceMember("u2", "u2")], Assert.Single(edited.Matches).Members);

        // an id is a file name of ours and nothing else
        Assert.Null(history.Load("..\\client"));
        Assert.Null(history.Load("lobby-evil"));
        Assert.Null(history.Load("lobby-2026-11-20-10-00-00"));
        Assert.Null(history.Load("lobby-2099-01-01-00-00-00"));
        Assert.NotNull(history.Load(good.Id));

        // and writing goes on beside them
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(Put(history, Change((Ana, "Ana"))).Started);
        Assert.Equal(3, history.Sessions().Count);

        // "Clear history": every session file, the broken ones too
        Assert.Equal(10, history.Clear()); // "lobby-evil.json" too
        Assert.Empty(history.Sessions());
        Assert.Empty(Directory.GetFiles(folder));
        Assert.True(Put(history, Change((Ana, "Ana"))).Started); // the session under way was forgotten too
    }

    [Fact]
    public void A_file_that_cannot_be_written_is_said_once_without_its_path()
    {
        var clock = new ManualClock();
        var blocked = Path.Combine(Rig.NewFolder("races-blocked"), "races");
        File.WriteAllText(blocked, "a file where the folder should be");
        var history = new RaceHistory(blocked, clock);
        var first = Put(history, Change((Ana, "Ana")));
        Assert.NotNull(first.SaveError);
        Assert.DoesNotContain("races", first.SaveError, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Put(history, Change((Ana, "Ana"), (Rin, "Rin"))).SaveError);
        Assert.Empty(history.Sessions());
    }

    // ---- the report -----------------------------------------------------------------------

    [Fact]
    public void The_report_names_the_id_every_name_the_sessions_and_the_launcher()
    {
        var (history, clock, _) = Store();
        Put(history, Match("7_1_0_12_ab", (Ana, "Ana"), (Cheat, "Turbo")));
        clock.Advance(TimeSpan.FromMinutes(5));
        Put(history, Match("7_2_0_12_ab", (Ana, "Ana"), (Cheat, "xX_Drift_Xx")));
        Put(history, Left);
        clock.Advance(TimeSpan.FromDays(1));
        Put(history, Change((Ana, "Ana"), (Cheat, "")), RaceSession.Mkwmp, "mkw.example.net:8443");
        Put(history, Left, RaceSession.Mkwmp, "mkw.example.net:8443");

        var text = RaceReport.Text(history.Player(Cheat)!, "1.5.3");
        Assert.Equal(
            """
            MKW online player report (from the MKWMP launcher's local history)
            Player ID: 9f8e7d6c-aaaa-5bbb-8ccc-ddddeeeeffff
            Network: MKWMP server mkw.example.net:8443; Nextendo Network (nextendo.network)
            Launcher: MKWMP 1.5.3

            Names shown under this Player ID (2):
            - "Turbo": first seen 2026-11-27 12:00:00 UTC, last seen 2026-11-27 12:00:00 UTC
            - "xX_Drift_Xx": first seen 2026-11-27 12:05:00 UTC, last seen 2026-11-27 12:05:00 UTC

            Sessions where this Player ID was seen (2):
            - 2026-11-28 12:05:00 UTC to 2026-11-28 12:05:00 UTC, MKWMP server mkw.example.net:8443
            - 2026-11-27 12:00:00 UTC to 2026-11-27 12:05:00 UTC, Nextendo Network (nextendo.network), as "Turbo", "xX_Drift_Xx"
              - match at 2026-11-27 12:00:00 UTC as "Turbo" (match key 7_1_0_12_ab)
              - match at 2026-11-27 12:05:00 UTC as "xX_Drift_Xx" (match key 7_2_0_12_ab)

            The Player ID is the id the game server gave this player; the names are what their game showed.

            """.ReplaceLineEndings("\n"),
            text);
        Assert.DoesNotContain(Ana, text, StringComparison.Ordinal); // only the reported player: nobody else of the lobby
        Assert.DoesNotContain("Nintendo", text, StringComparison.OrdinalIgnoreCase);

        var brief = RaceReport.Brief(history.Player(Cheat)!, 500);
        Assert.Equal($"Player ID {Cheat}. Names used: Turbo, xX_Drift_Xx", brief);
        Assert.Equal(40, RaceReport.Brief(history.Player(Cheat)!, 40).Length);
    }

    // ---- local only -----------------------------------------------------------------------

    [Fact]
    public void A_path_of_the_history_is_never_one_a_report_copies()
    {
        Assert.True(RaceHistory.IsPrivate(@"C:\mkwmp\races\lobby-2026-11-27-12-00-00.json"));
        Assert.True(RaceHistory.IsPrivate("/home/deck/mkwmp/Races/anything.txt"));
        Assert.True(RaceHistory.IsPrivate(@"C:\elsewhere\lobby-2026-11-27-12-00-00.json"));
        Assert.True(RaceHistory.IsPrivate(@"C:\elsewhere\lobby-2026-11-27-12-00-00.json.tmp"));
        Assert.False(RaceHistory.IsPrivate(@"C:\mkwmp\logs\launcher-20261127-120000.log"));
        Assert.False(RaceHistory.IsPrivate(@"C:\mkwmp\logs\mkw.log"));
        Assert.False(RaceHistory.IsPrivate(""));

        // RuntimeFiles takes the newest files of a logs folder: never one of these
        var logs = Rig.NewFolder("races-logs");
        File.WriteAllText(Path.Combine(logs, "mkw.log"), "log");
        File.WriteAllText(Path.Combine(logs, "lobby-2026-11-27-12-00-00.json"), "{}");
        Assert.Equal(["mkw.log"], RuntimeFiles.Pick(logs, "*", DateTimeOffset.UtcNow.AddHours(-1)).Select(f => f.Name));

        // and whatever put one in a report's folder, it is gone before the zip
        var bundle = Rig.NewFolder("races-bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "mkw-recomp", "races"));
        File.WriteAllText(Path.Combine(bundle, "mkw-recomp", "races", "lobby-2026-11-27-12-00-00.json"), "{}");
        File.WriteAllText(Path.Combine(bundle, "lobby-2026-11-27-12-00-01.json"), "{}");
        File.WriteAllText(Path.Combine(bundle, "system.txt"), "kept");
        RaceHistory.RemoveFrom(bundle);
        Assert.Equal(["system.txt"], Directory.GetFiles(bundle, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
        RaceHistory.RemoveFrom(Path.Combine(bundle, "gone")); // nothing to do, nothing thrown
    }

    [Fact]
    public async Task Nothing_of_the_history_is_in_a_bug_report_or_in_the_log()
    {
        // the worst place for it: client.toml beside the executable, so races\ lies in the launcher's own folder, beside logs\
        var server = new FakeSeatServer();
        var root = Rig.NewFolder("races-report");
        var (home, game, desktop) = (Path.Combine(root, "launcher"), Path.Combine(root, "game"), Path.Combine(root, "desktop"));
        foreach (var folder in new[] { home, game, desktop })
        {
            Directory.CreateDirectory(folder);
        }
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        var config = Path.Combine(home, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { Name = "Ana", Game = game, AutoUpdate = false, Server = FakeSeatServer.Url } }.Save(config);
        var target = new MkwRecompTarget("1.4.0") { RunningOverride = () => false };
        string[] secrets = [Cheat, Rin, "TurboShell", "xX_Drift_Xx", "RinTheRacer", "7_1_0_12_ab"];
        string logText;
        var zips = new List<string>();
        await using (var launcher = new Launcher(config, home, new LauncherOptions
        {
            Version = "1.4.0", BindLocalApi = false, BackgroundLoops = false, NoUpdate = true, Headless = true, Targets = [target], UpdateApi = "http://github.test", DesktopFolder = desktop,
        }, server))
        {
            await launcher.StartAsync(CancellationToken.None);
            using var api = new LocalApi(launcher, TimeProvider.System);
            foreach (var body in new[]
            {
                $$"""{"event":"change","match":null,"members":[{"uid":"{{Ana}}","name":"Ana","local":true},{"uid":"{{Cheat}}","name":"TurboShell"},{"name":"no id"}]}""",
                $$"""{"event":"match","match":"7_1_0_12_ab","members":[{"uid":"{{Ana}}","name":"Ana","local":true},{"uid":"{{Cheat}}","name":"xX_Drift_Xx"},{"uid":"{{Rin}}","name":"RinTheRacer"}]}""",
                """{"event":"left","match":null,"members":[]}""",
            })
            {
                Assert.Equal(204, (await api.HandleAsync("POST", "/local/v1/roster", "Bearer " + launcher.Token, Utf8.GetBytes(body), CancellationToken.None)).Status);
            }
            var session = Assert.Single(await launcher.RaceSessionsAsync());
            Assert.Equal(1, session.Renamed);
            Assert.True(File.Exists(Path.Combine(home, "races", session.Id + ".json")));
            Assert.Contains("xX_Drift_Xx", RaceReport.Text((await launcher.RacePlayerAsync(Cheat))!, launcher.Version), StringComparison.Ordinal);

            zips.Add(await launcher.SaveBugReportAsync());
            var bug = await launcher.PrepareReportAsync(null);
            zips.Add(bug.Zip!);
            foreach (var text in new[] { bug.Text, bug.Url, bug.Title })
            {
                Assert.All(secrets, s => Assert.DoesNotContain(s, text, StringComparison.Ordinal));
            }

            await launcher.ClearRaceHistoryAsync();
            Assert.Empty(await launcher.RaceSessionsAsync());
            Assert.Empty(Directory.GetFiles(Path.Combine(home, "races")));
            Assert.Equal(4, launcher.RaceHistoryRevision);
        }
        logText = string.Concat(Directory.GetFiles(Path.Combine(home, "logs"), "*.log").Select(File.ReadAllText));

        // the log: that it happened, in counts; never who
        Assert.Contains("previous races: an online session started (2 players)", logText, StringComparison.Ordinal);
        Assert.Contains("previous races: a match started (3 players)", logText, StringComparison.Ordinal);
        Assert.Contains("previous races: 1 members without an id were left out", logText, StringComparison.Ordinal);
        Assert.Contains("previous races: the online session ended", logText, StringComparison.Ordinal);
        Assert.Contains("previous races: history cleared (1 sessions)", logText, StringComparison.Ordinal);
        Assert.All(secrets, s => Assert.DoesNotContain(s, logText, StringComparison.Ordinal));
        Assert.DoesNotContain("lobby-", logText, StringComparison.Ordinal);

        // the zips: no file of it, and no byte of it in any file
        Assert.Equal(2, zips.Count);
        foreach (var zip in zips)
        {
            using var archive = ZipFile.OpenRead(zip);
            Assert.NotEmpty(archive.Entries);
            foreach (var entry in archive.Entries)
            {
                Assert.DoesNotContain("races", entry.FullName, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("lobby-", entry.FullName, StringComparison.OrdinalIgnoreCase);
                using var reader = new StreamReader(entry.Open(), Encoding.Latin1);
                var content = reader.ReadToEnd();
                Assert.All(secrets, s => Assert.DoesNotContain(s, content, StringComparison.Ordinal));
            }
        }
        // nor in the folders the zips were made from
        Assert.Empty(Directory.GetFiles(Path.Combine(home, "logs"), "lobby-*", SearchOption.AllDirectories));
    }
}
