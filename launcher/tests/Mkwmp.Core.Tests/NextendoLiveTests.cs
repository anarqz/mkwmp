// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Text;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Tests;

/// <summary>"Players in lobbies" on the Play card: Nextendo's live status, how it is counted, and the pace it is asked at.</summary>
public sealed class NextendoLiveTests
{
    private const string Mkw = NextendoPresence.MkwTitleId;

    private static int? Count(string json) => NextendoLive.InLobbies(Json.Parse(json), Mkw);

    // ---- the counting rule ----------------------------------------------------------------

    [Fact]
    public void The_players_of_every_lobby_of_the_game_are_added_whatever_its_state()
    {
        // the Status page's own example: 13 online, one lobby "Race · Open · 3/24" -> 3 (not the 13 who are only online)
        Assert.Equal(3, Count($$"""{"updated":1,"games":[{"titles":["{{Mkw}}"],"players":[{},{},{},{},{},{},{},{},{},{},{},{},{}],"lobbies":[{"count":3,"max":24,"state":"open","mode":"Race"}]}]}"""));
        Assert.Equal(3 + 4 + 12 + 24, Count($$"""{"games":[{"titles":["{{Mkw}}"],"lobbies":[{"count":3,"max":24,"state":"searching"},{"count":4,"max":24,"state":"open"},{"count":12,"max":24,"state":"matched"},{"count":24,"max":24,"state":"full"}]}]}"""));
        Assert.Equal(0, Count($$"""{"games":[{"titles":["{{Mkw}}"],"players":[{},{}],"lobbies":[]}]}""")); // online, in no lobby
    }

    [Fact]
    public void Only_the_game_s_entry_counts_found_by_its_title_id_in_any_case()
    {
        Assert.Equal(5, Count($$"""{"games":[{"titles":["0100152000022000"],"lobbies":[{"count":16}]},{"titles":["x","{{Mkw.ToUpperInvariant()}}"],"lobbies":[{"count":5}]}]}"""));
    }

    [Fact]
    public void A_lobby_without_a_count_is_the_players_it_lists_and_a_bad_number_is_nothing()
    {
        Assert.Equal(2, Count($$"""{"games":[{"titles":["{{Mkw}}"],"lobbies":[{"players":[{"name":"a"},{"name":"b"}]}]}]}"""));
        Assert.Equal(7, Count($$"""{"games":[{"titles":["{{Mkw}}"],"lobbies":[{"count":-4},{"count":"x"},7,{"count":7}]}]}"""));
    }

    [Fact]
    public void A_game_that_is_not_listed_has_nobody_and_what_is_not_known_is_not_a_number()
    {
        Assert.Equal(0, Count("""{"updated":1,"games":[]}"""));
        Assert.Equal(0, Count("""{"games":[{"titles":["0100152000022000"],"lobbies":[{"count":9}]}]}"""));
        // rooms that Nextendo cannot tell now, or no list of them at all
        Assert.Null(Count($$"""{"games":[{"titles":["{{Mkw}}"],"players":[{}],"roomsAvailable":false,"lobbies":[]}]}"""));
        Assert.Null(Count($$"""{"games":[{"titles":["{{Mkw}}"],"players":[{}]}]}"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"games":"nope"}""")]
    [InlineData("""{"error":"Session invalide ou expirée."}""")]
    public void An_answer_that_is_not_a_live_status_counts_nothing(string json) => Assert.Null(Count(json));

    // ---- MKWMP servers ----------------------------------------------------------------------

    [Fact]
    public void An_MKWMP_server_s_seated_players_are_its_players_in_lobbies_while_it_is_up()
    {
        Assert.Equal(12, LobbyPlayers.FromServer(new ServerView(ServerState.Online, "MKWMP", 32, 12, 3, null)));
        Assert.Equal(0, LobbyPlayers.FromServer(new ServerView(ServerState.Online, "MKWMP", 32, 0, 0, null)));
        Assert.Null(LobbyPlayers.FromServer(new ServerView(ServerState.Offline, null, 0, 0, 0, "down")));
        Assert.Null(LobbyPlayers.FromServer(new ServerView(ServerState.Checking, null, 0, 0, 0, null)));
        Assert.Null(LobbyPlayers.FromServer(new ServerView(ServerState.NotAsked, null, 0, 0, 0, null)));
    }

    // ---- asking, and how often --------------------------------------------------------------

    private sealed record Wired(NextendoAccount Account, NextendoApi Api, FakeNextendo Server, ManualClock Clock, HttpClient Http) : IDisposable
    {
        public string? Body { get; set; }

        public int Status { get; set; } = 200;

        public int RetryAfter { get; set; }

        public List<string> Calls { get; } = [];

        public void Dispose()
        {
            Account.Dispose();
            Http.Dispose();
        }
    }

    private static Wired Wire(string live)
    {
        var server = new FakeNextendo();
        var clock = new ManualClock();
        var vault = new NextendoVault(Path.Combine(Rig.NewFolder("nextendo-live"), NextendoVault.FileName));
        Assert.True(vault.Save("refresh-0"));
        var http = new HttpClient(server);
        var api = new NextendoApi(http, "test");
        var wired = new Wired(new NextendoAccount(api, vault, clock), api, server, clock, http) { Body = live };
        server.More = (call, _) =>
        {
            wired.Calls.Add(call);
            if (call != "GET /api/status/live")
            {
                return null;
            }
            var res = new HttpResponseMessage((HttpStatusCode)wired.Status) { Content = new StringContent(wired.Body ?? "", Encoding.UTF8, "application/json") };
            if (wired.RetryAfter > 0)
            {
                res.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(wired.RetryAfter));
            }
            return res;
        };
        return wired;
    }

    private static string Live(int players) => $$"""{"games":[{"titles":["{{Mkw}}"],"lobbies":[{"count":{{players}},"max":24,"state":"open"}]}]}""";

    [Fact]
    public async Task It_is_asked_with_the_access_token_every_45_seconds()
    {
        using var wired = Wire(Live(3));
        using var watch = new NextendoLobbies(wired.Account, wired.Api, wired.Clock);

        var first = await watch.PollAsync(CancellationToken.None);
        Assert.Equal((3, null, NextendoLobbies.Every), (first.Players, first.Error, first.Next));
        Assert.Equal(TimeSpan.FromSeconds(45), NextendoLobbies.Every);
        Assert.Contains(("/api/status/live", "access-1"), wired.Server.Bearers); // the friends-scope access token, never the game token
        wired.Body = Live(5);
        Assert.Equal(5, (await watch.PollAsync(CancellationToken.None)).Players);
    }

    [Fact]
    public async Task A_429_is_obeyed_and_a_failure_is_asked_less_and_less_while_the_last_number_stays_for_five_minutes()
    {
        using var wired = Wire(Live(3));
        using var watch = new NextendoLobbies(wired.Account, wired.Api, wired.Clock);
        Assert.Equal(3, (await watch.PollAsync(CancellationToken.None)).Players);

        // 429, Retry-After 120: nothing is asked for two minutes, whoever asks; what was known stays
        (wired.Status, wired.RetryAfter) = (429, 120);
        var limited = await watch.PollAsync(CancellationToken.None);
        Assert.Equal((NextendoError.Limited, TimeSpan.FromSeconds(120), 3), (limited.Error!.Kind, limited.Next, limited.Players));
        (wired.Status, wired.RetryAfter) = (200, 0);
        var asked = wired.Calls.Count(c => c == "GET /api/status/live");
        wired.Clock.Advance(TimeSpan.FromSeconds(50));
        var waiting = await watch.PollAsync(CancellationToken.None);
        Assert.Equal((null, TimeSpan.FromSeconds(70)), (waiting.Error, waiting.Next));
        Assert.Equal(asked, wired.Calls.Count(c => c == "GET /api/status/live"));
        wired.Clock.Advance(TimeSpan.FromSeconds(71));
        Assert.Equal((null, NextendoLobbies.Every), ((await watch.PollAsync(CancellationToken.None)).Error, NextendoLobbies.Every));

        // 5xx: 30 s, 1 min, 2 min, 4 min, then 5 min; the number stays for five minutes after the last good answer, then it is not known
        wired.Status = 503;
        foreach (var seconds in new[] { 30, 60, 120, 240, 300 })
        {
            var poll = await watch.PollAsync(CancellationToken.None);
            Assert.Equal((NextendoError.Unreachable, TimeSpan.FromSeconds(seconds), 3), (poll.Error!.Kind, poll.Next, poll.Players));
            wired.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        wired.Clock.Advance(NextendoLobbies.Stale);
        var gone = await watch.PollAsync(CancellationToken.None);
        Assert.Equal((NextendoError.Unreachable, TimeSpan.FromSeconds(300), (int?)null), (gone.Error!.Kind, gone.Next, gone.Players));

        // an answer that is no live status is Bad; the answer's body is never trusted for a number
        (wired.Status, wired.Body) = (200, """{"hello":1}""");
        wired.Clock.Advance(TimeSpan.FromMinutes(6));
        var bad = await watch.PollAsync(CancellationToken.None);
        Assert.Equal((NextendoError.Bad, (int?)null), (bad.Error!.Kind, bad.Players));
    }

    [Fact]
    public async Task A_link_without_the_friends_scope_or_a_sign_in_that_ended_shows_nothing_and_waits_five_minutes()
    {
        using var wired = Wire(Live(3));
        using var watch = new NextendoLobbies(wired.Account, wired.Api, wired.Clock);
        Assert.Equal(3, (await watch.PollAsync(CancellationToken.None)).Players);
        wired.Status = 403;
        var scope = await watch.PollAsync(CancellationToken.None);
        Assert.Equal((NextendoError.Scope, NextendoRoster.SignedOutWait, (int?)null), (scope.Error!.Kind, scope.Next, scope.Players));

        // another account: nothing of the last stays
        wired.Status = 200;
        await watch.ResetAsync();
        Assert.Equal(3, (await watch.PollAsync(CancellationToken.None)).Players);
    }
}