// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The friends side of the fake Nextendo (<see cref="FakeNextendo.More"/>): the lists of one account, changed by the
/// same calls as the real one (nextendo-account's handlers), and what was asked of it. No socket, no real server.
/// </summary>
internal sealed class FakePeople
{
    private readonly Lock _gate = new();

    public FakePeople(FakeNextendo nextendo) => nextendo.More = Answer;

    public List<JsonObject> Friends { get; } = [];
    public List<JsonObject> Requests { get; } = [];
    public List<JsonObject> Sent { get; } = [];
    public List<JsonObject> Blocked { get; } = [];
    public List<JsonObject> Recent { get; } = [];

    /// <summary>Every call: "GET /api/friends?images=0", "POST /api/friends/accept {\"pid\":21}"…</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The body of every <c>POST /api/presence</c>.</summary>
    public List<JsonObject> Presences { get; } = [];

    /// <summary>The list (and everything else here) answers 429 with this <c>Retry-After</c> (0: it answers).</summary>
    public int LimitSeconds { get; set; }

    /// <summary>Everything here answers this status (0: it answers).</summary>
    public int Fail { get; set; }

    /// <summary>The list has no <c>sent</c> (an older server).</summary>
    public bool NoSent { get; set; }

    public static JsonObject Person(long pid, string name, string code, int status = 0, string appId = "", string detail = "", bool favorite = false) => new()
    {
        ["pid"] = pid,
        ["username"] = name.ToLowerInvariant(),
        ["name"] = name,
        ["friend_code"] = code,
        ["avatar"] = "a1",
        ["color"] = "#3366ff",
        ["presence"] = new JsonObject { ["status"] = status, ["app_field"] = "", ["app_id"] = appId, ["app_detail"] = detail },
        ["favorite"] = favorite,
    };

    public int Count(string start)
    {
        lock (_gate)
        {
            return Calls.Count(c => c.StartsWith(start, StringComparison.Ordinal));
        }
    }

    private static HttpResponseMessage Json(int status, JsonNode body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static JsonArray Copy(IEnumerable<JsonObject> list) => [.. list.Select(p => p.DeepClone())];

    private HttpResponseMessage? Answer(string call, string body)
    {
        lock (_gate)
        {
            Calls.Add(body.Length > 0 ? call + " " + body : call);
            if (LimitSeconds > 0)
            {
                var limited = Json(429, new JsonObject { ["error"] = "rate_limited" });
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(LimitSeconds));
                return limited;
            }
            if (Fail != 0)
            {
                return Json(Fail, new JsonObject { ["error"] = "nope" });
            }
            var sent = JsonNode.Parse(body.Length > 0 ? body : "{}")!.AsObject();
            var pid = sent["pid"]?.GetValue<long>() ?? 0;
            bool Is(JsonObject p) => p["pid"]!.GetValue<long>() == pid;
            switch (call)
            {
                case "GET /api/friends?images=0":
                    var list = new JsonObject { ["friends"] = Copy(Friends), ["requests"] = Copy(Requests) };
                    if (!NoSent)
                    {
                        list["sent"] = Copy(Sent);
                    }
                    return Json(200, list);
                case "POST /api/friends":
                    var code = sent["friend_code"]!.GetValue<string>();
                    if (code == "SW-1111-2222-3333")
                    {
                        return Json(400, new JsonObject { ["error"] = "own" });
                    }
                    if (code == "SW-3003-0030-0300")
                    {
                        return Json(409, new JsonObject { ["error"] = "full" });
                    }
                    if (Recent.Concat(Requests).FirstOrDefault(p => p["friend_code"]!.GetValue<string>() == code) is not { } target)
                    {
                        return Json(404, new JsonObject { ["error"] = "unknown" });
                    }
                    var asked = Requests.Remove(target); // they had asked first: friends at once
                    (asked ? Friends : Sent).Add(target);
                    return Json(200, new JsonObject { ["friend"] = target.DeepClone(), ["already"] = asked });
                case "POST /api/friends/accept":
                    if (Requests.FirstOrDefault(Is) is not { } from)
                    {
                        return Json(400, new JsonObject { ["error"] = "gone" });
                    }
                    Requests.Remove(from);
                    Friends.Add(from);
                    return Json(200, new JsonObject { ["friend"] = from.DeepClone() });
                case "POST /api/friends/decline":
                    Requests.RemoveAll(Is);
                    return Json(200, new JsonObject { ["ok"] = true });
                case "POST /api/friends/remove":
                    Friends.RemoveAll(Is);
                    return Json(200, new JsonObject { ["ok"] = true });
                case "POST /api/friends/block":
                    Blocked.AddRange(Friends.Concat(Requests).Where(Is).Select(p => (JsonObject)p.DeepClone()));
                    Friends.RemoveAll(Is);
                    Requests.RemoveAll(Is);
                    return Json(200, new JsonObject { ["ok"] = true });
                case "POST /api/friends/unblock":
                    Blocked.RemoveAll(Is);
                    return Json(200, new JsonObject { ["ok"] = true });
                case "POST /api/friends/favorite":
                    if (Friends.FirstOrDefault(Is) is not { } friend)
                    {
                        return Json(400, new JsonObject { ["error"] = "not a friend" });
                    }
                    friend["favorite"] = sent["favorite"]!.GetValue<bool>();
                    return Json(200, new JsonObject { ["pid"] = pid, ["favorite"] = friend["favorite"]!.GetValue<bool>() });
                case "GET /api/friends/blocked":
                    return Json(200, new JsonObject { ["blocked"] = Copy(Blocked) });
                case "GET /api/recent-players":
                    return Json(200, new JsonObject { ["players"] = Copy(Recent) });
                case "POST /api/presence":
                    Presences.Add(sent);
                    return Json(200, new JsonObject { ["ok"] = true, ["platform"] = "app" });
                default:
                    return null;
            }
        }
    }
}

/// <summary>
/// Friends on Nextendo Network (Net/NextendoFriends.cs, Launcher.NextendoFriends.cs): reading the lists, what is new in
/// them (and the one summary of the first), the actions, the pace of asking (a 429, a network that is away), the
/// presence said again and again and taken back, and a friend code as it is typed. Nothing here leaves the test.
/// </summary>
public sealed class NextendoFriendsTests
{
    private const long Self = 1234567;
    private static readonly BackgroundSettings All = new();
    private static readonly DateTimeOffset Noon = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    // ---- a friend code --------------------------------------------------------------------

    [Theory]
    [InlineData("SW-1234-5678-9012", "SW-1234-5678-9012")]
    [InlineData("sw-1234-5678-9012", "SW-1234-5678-9012")]
    [InlineData("  SW 1234 5678 9012 ", "SW-1234-5678-9012")]
    [InlineData("1234-5678-9012", "SW-1234-5678-9012")]
    [InlineData("123456789012", "SW-1234-5678-9012")]
    [InlineData("1234 5678 9012", "SW-1234-5678-9012")]
    [InlineData("SW123456789012", "SW-1234-5678-9012")]
    [InlineData("１２３４５６７８９０１２", "SW-1234-5678-9012")]
    [InlineData("SW-1234-5678-901", null)]
    [InlineData("SW-1234-5678-90123", null)]
    [InlineData("SW-1234-5678-9O12", null)]
    [InlineData("AB-1234-5678-9012", null)]
    [InlineData("Luna", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_friend_code_is_read_as_typed_and_written_as_nextendo_writes_it(string? typed, string? code)
    {
        Assert.Equal(code, NextendoFriendCode.Normalize(typed));
        Assert.Equal(code is not null, NextendoFriendCode.IsValid(typed));
    }

    // ---- reading the lists ----------------------------------------------------------------

    [Fact]
    public void The_friends_list_is_read_as_nextendo_writes_it()
    {
        var list = NextendoFriendList.Parse(Json.Parse("""
            {"friends":[
              {"pid":11,"username":"luna","name":"Luna","friend_code":"SW-2071-4456-9902","image":"","avatar":"a","color":"#f00","badges":[],
               "presence":{"status":2,"app_field":"","app_id":"0400C3F00006E000","app_detail":"Grand Prix"},"favorite":true,"since":"2026-11-01T00:00:00Z"},
              {"pid":"12","username":"kai","friend_code":"SW-5530-1187-2264","presence":{"status":1,"app_id":"","app_detail":""},"favorite":false},
              {"pid":13,"username":"rin","name":"Rin","friend_code":"SW-7705-2291-4480","presence":{"status":2,"title":"Splatoon 2","detail":"Turf War"}},
              {"pid":14,"username":"gone","name":"gone","friend_code":"","banned":true,"favorite":false,"presence":{"status":2}},
              {"pid":15,"username":"off","name":"Off\u0007line\n","friend_code":"SW-1-2-3"},
              {"pid":11,"username":"twice","name":"Twice"},
              {"pid":0,"username":"nobody"},
              {"username":"no pid"},
              "junk"
            ],
            "requests":[{"pid":21,"username":"nico","name":"Nico","friend_code":"SW-6621-5508-3317","presence":{"status":0}}, 5],
            "sent":[{"pid":31,"username":"carol","name":"","friend_code":"SW-5587-1120-9964"}]}
            """))!;
        Assert.Equal(
        [
            new NextendoFriend(11, "Luna", "SW-2071-4456-9902", FriendState.Playing, "MKW", "Grand Prix", true),
            new NextendoFriend(12, "kai", "SW-5530-1187-2264", FriendState.Online, "", "", false),
            new NextendoFriend(13, "Rin", "SW-7705-2291-4480", FriendState.Playing, "Splatoon 2", "Turf War", false),
            new NextendoFriend(14, "gone", "", FriendState.Offline, "", "", false, Banned: true), // closed by Nextendo: never online
            new NextendoFriend(15, "Offline", "SW-1-2-3", FriendState.Offline, "", "", false), // no control characters, no presence: offline
        ], list.Friends);
        Assert.Equal([new NextendoPerson(21, "Nico", "SW-6621-5508-3317"), new NextendoPerson(5, "", "")], list.Incoming);
        Assert.Equal([new NextendoPerson(31, "carol", "SW-5587-1120-9964")], list.Sent);

        // an older server has no "sent"; an answer without "friends" is not a friends list
        Assert.Empty(NextendoFriendList.Parse(Json.Parse("""{"friends":[],"requests":[]}"""))!.Sent);
        Assert.Null(NextendoFriendList.Parse(Json.Parse("""{"requests":[]}""")));
        Assert.Null(NextendoFriendList.Parse(Json.Parse("""{"friends":{}}""")));
        Assert.Null(NextendoFriendList.Parse(Json.Parse("not json")));

        // the blocked list and the players met: objects, or bare pids
        Assert.Equal([new NextendoPerson(51, "Grumpy", "SW-7"), new NextendoPerson(52, "", "")],
            NextendoFriendList.People(Json.Parse("""{"blocked":[{"pid":51,"name":"Grumpy","friend_code":"SW-7"},52,52,"x"]}"""), "blocked"));
        Assert.Equal([new NextendoPerson(41, "Dario", "SW-3302-7719-4458")],
            NextendoFriendList.People(Json.Parse("""{"players":[{"pid":41,"name":"Dario","known":true,"friend_code":"SW-3302-7719-4458","avatar_url":"","title_id":"0400c3f00006e000","seen_at":1,"jeu":"x","icone":"y"}]}"""), "players"));
        Assert.Null(NextendoFriendList.People(Json.Parse("""{"players":{}}"""), "players"));
    }

    [Fact]
    public void A_friend_s_id_on_the_game_s_servers_is_read_when_nextendo_sends_it()
    {
        var list = NextendoFriendList.Parse(Json.Parse("""
            {"friends":[
              {"pid":11,"name":"Luna","npln_account_id":"0123456789ABCDEF","npln_user_id":"u-11","presence":{"status":1}},
              {"pid":12,"name":"Kai","account_hex":"1f"},
              {"pid":13,"name":"Rin","npln_account_id":"nope","account_hex":"00000000000000AA"},
              {"pid":14,"name":"Bea","npln_account_id":"12345678901234567"},
              {"pid":15,"name":"Abe","npln_account_id":7},
              {"pid":16,"name":"Theo"}
            ],
            "requests":[{"pid":21,"name":"Nico","npln_account_id":"00000000000000b1"}, 5],
            "sent":[{"pid":31,"name":"Carol","account_hex":"C1"}]}
            """))!;
        Assert.Equal(["0123456789abcdef", "000000000000001f", "00000000000000aa", "", "", ""], list.Friends.Select(f => f.Account));
        Assert.Equal(new NextendoFriend(11, "Luna", "", FriendState.Online, "", "", false, Account: "0123456789abcdef"), list.Friends[0]);
        Assert.Equal([new NextendoPerson(21, "Nico", "", "00000000000000b1"), new NextendoPerson(5, "", "")], list.Incoming);
        Assert.Equal([new NextendoPerson(31, "Carol", "", "00000000000000c1")], list.Sent);
    }

    [Fact]
    public void The_nsa_field_nextendo_sends_on_every_friend_is_the_game_id()
    {
        // GET /api/friends?images=0 as Nextendo sends it today: "nsa" is the 16-hex NPLN account id
        var list = NextendoFriendList.Parse(Json.Parse("""
            {"friends":[{"pid":11,"name":"Luna","nsa":"0123456789ABCDEF","presence":{"status":1}},
                        {"pid":12,"name":"Kai","nsa":"zz","account_hex":"1f"}],
             "requests":[]}
            """))!;
        Assert.Equal(["0123456789abcdef", "000000000000001f"], list.Friends.Select(f => f.Account));
    }

    [Fact]
    public void The_game_is_given_only_friends_with_an_id_who_plays_first()
    {
        static NextendoFriend F(long pid, string name, FriendState state, string account) => new(pid, name, "", state, "", "", false, Account: account);
        NextendoFriend[] friends =
        [
            F(1, "zed", FriendState.Offline, "00000000000000a1"), F(2, "Bia", FriendState.Online, "00000000000000a2"), F(3, "noid", FriendState.Playing, ""),
            F(4, "ana", FriendState.Online, "00000000000000a4"), F(5, "Kai", FriendState.Playing, "00000000000000a5"), F(6, "me", FriendState.Online, "00000000000000a6"),
            F(7, "twin", FriendState.Offline, "00000000000000a5"), F(8, "mine", FriendState.Online, "00000000000000ff"),
        ];
        // never the player (by pid, or by the id Nextendo gave them), nobody without an id, no id twice
        Assert.Equal(["Kai", "ana", "Bia", "zed"], NextendoFriendList.ForGame(friends, 6, "00000000000000ff", 100).Select(f => f.Name));
        Assert.Equal(["Kai", "ana"], NextendoFriendList.ForGame(friends, 6, "", 2).Select(f => f.Name));
        Assert.Empty(NextendoFriendList.ForGame(friends.Select(f => f with { Account = "" }), 6, "", 100));

        // names as the hook takes them: 1 to 20 characters
        Assert.Equal("Player", NextendoFriendList.NameForGame(F(1, "", FriendState.Online, "a")));
        Assert.Equal("abcdefghijklmnopqrst", NextendoFriendList.NameForGame(F(1, "abcdefghijklmnopqrstuvwxyz", FriendState.Online, "a")));
        Assert.Equal("""{"friends":[]}""", LocalApi.NextendoFriendsJson([]));
        Assert.Equal("""{"friends":[]}""", LocalApi.NextendoFriendsJson([F(3, "noid", FriendState.Playing, "")]));
    }

    [Theory]
    [InlineData("0400c3f00006e000", "", "MKW")]
    [InlineData("0400C3F00006E000", "Whatever It Is Called", "MKW")]
    [InlineData("", "Mario Kart World", "MKW")] // the game's full name is never shown
    [InlineData("0100f8f0000a2000", "Splatoon 2", "Splatoon 2")]
    [InlineData("0100000000010000", "Nintendo Something", "")] // nor its maker's
    [InlineData("0100f8f0000a2000", null, "")]
    public void What_a_friend_plays_is_mkw_or_the_other_game_s_own_title(string appId, string? title, string shown)
    {
        Assert.Equal(shown, NextendoFriendList.GameName(appId, title));
        Assert.Equal(16, NextendoPresence.MkwTitleId.Length);
        Assert.All(NextendoPresence.MkwTitleId, c => Assert.True(char.IsAsciiHexDigitLower(c)));
    }

    // ---- what is new in a list ------------------------------------------------------------

    private static NextendoFriend Friend(long pid, string name, FriendState state, string game = "") => new(pid, name, $"SW-{pid}", state, game, "", false);

    private static NextendoFriendList List(NextendoFriend[] friends, NextendoPerson[]? incoming = null, NextendoPerson[]? sent = null) => new(friends, incoming ?? [], sent ?? []);

    [Fact]
    public void The_first_list_is_one_summary_and_never_a_name()
    {
        var first = List(
            [Friend(1, "Luna", FriendState.Playing, "MKW"), Friend(2, "Kai", FriendState.Online), Friend(3, "Bea", FriendState.Offline), Friend(Self, "bia", FriendState.Online)],
            [new NextendoPerson(21, "Nico", ""), new NextendoPerson(22, "Yuki", "")]);
        var events = new NextendoFriendWatch().Diff(first, All, Self, Noon);
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: 2, Requests: 2)], events); // the player themself is not counted
        Assert.Equal(new Toast("Friends online: 2", "Friend requests: 2", ToastKind.FriendOnline), events[0].Toast());

        // only what the player asked to hear about
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: 0, Requests: 2)],
            new NextendoFriendWatch().Diff(first, All with { NotifyOnline = false, NotifyPlaying = false }, Self, Noon));
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: 2, Requests: 0)],
            new NextendoFriendWatch().Diff(first, All with { NotifyRequests = false }, Self, Noon));
        Assert.Empty(new NextendoFriendWatch().Diff(first, new BackgroundSettings { NotifyOnline = false, NotifyPlaying = false, NotifyRequests = false }, Self, Noon));
        // nobody on, nothing asked: nothing said
        Assert.Empty(new NextendoFriendWatch().Diff(List([Friend(3, "Bea", FriendState.Offline)]), All, Self, Noon));
        Assert.Empty(new NextendoFriendWatch().Diff(List([]), All, Self, Noon));

        Assert.Equal(new Toast("Friend requests: 2", Strings.FriendRequestBody, ToastKind.FriendRequest), new NextendoFriendEvent(NextendoFriendEventKind.Summary, Requests: 2).Toast());
        Assert.Equal(new Toast("Friends online: 3", "Nextendo Network", ToastKind.FriendOnline), new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: 3).Toast());
    }

    [Fact]
    public void After_the_first_list_a_friend_who_comes_online_or_starts_playing_and_a_new_request_are_news()
    {
        var watch = new NextendoFriendWatch();
        var luna = Friend(1, "Luna", FriendState.Offline);
        var kai = Friend(2, "Kai", FriendState.Online);
        var me = Friend(Self, "bia", FriendState.Offline);
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: 1)], watch.Diff(List([luna, kai, me]), All, Self, Noon));

        // Luna comes online, Kai starts the game, the player themself too (never news), Nico asks
        var events = watch.Diff(
            List([luna with { State = FriendState.Online }, kai with { State = FriendState.Playing, Game = "MKW" }, me with { State = FriendState.Playing, Game = "MKW" }],
                [new NextendoPerson(21, "Nico", "SW-21"), new NextendoPerson(Self, "bia", "")]),
            All, Self, Noon.AddSeconds(30));
        Assert.Equal(
        [
            new NextendoFriendEvent(NextendoFriendEventKind.Playing, "Kai", "MKW"),
            new NextendoFriendEvent(NextendoFriendEventKind.Online, "Luna"),
            new NextendoFriendEvent(NextendoFriendEventKind.Request, "Nico"),
        ], events);
        Assert.Equal(
        [
            new Toast("Kai is playing MKW", "Nextendo Network", ToastKind.FriendPlaying),
            new Toast("Luna is online", "Nextendo Network", ToastKind.FriendOnline),
            new Toast("Nico sent you a friend request", Strings.FriendRequestBody, ToastKind.FriendRequest),
        ], events.Select(e => e.Toast()));

        // the same list again: nothing; playing -> online is not news; online -> playing is (a game Nextendo does not name: "is playing")
        var same = List([luna with { State = FriendState.Online }, kai with { State = FriendState.Playing, Game = "MKW" }], [new NextendoPerson(21, "Nico", "SW-21")]);
        Assert.Empty(watch.Diff(same, All, Self, Noon.AddSeconds(60)));
        var next = watch.Diff(List([luna with { State = FriendState.Playing }, kai with { State = FriendState.Online }], [new NextendoPerson(21, "Nico", "SW-21")]), All, Self, Noon.AddSeconds(90));
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Playing, "Luna")], next);
        Assert.Equal(new Toast("Luna is playing", "Nextendo Network", ToastKind.FriendPlaying), next[0].Toast());
    }

    [Fact]
    public void A_presence_that_flickers_notifies_once_per_five_minutes_and_the_settings_are_obeyed()
    {
        var watch = new NextendoFriendWatch();
        var off = List([Friend(1, "Luna", FriendState.Offline)]);
        var on = List([Friend(1, "Luna", FriendState.Online)]);
        Assert.Empty(watch.Diff(off, All, Self, Noon));
        Assert.Single(watch.Diff(on, All, Self, Noon.AddSeconds(30)));
        Assert.Empty(watch.Diff(off, All, Self, Noon.AddSeconds(60)));
        Assert.Empty(watch.Diff(on, All, Self, Noon.AddSeconds(90))); // again within five minutes: quiet
        Assert.Empty(watch.Diff(off, All, Self, Noon.AddSeconds(120)));
        Assert.Single(watch.Diff(on, All, Self, Noon.AddSeconds(30) + NextendoFriendWatch.Debounce));

        // each of the three settings turns its own news off
        var quiet = new NextendoFriendWatch();
        var before = List([Friend(1, "Luna", FriendState.Offline), Friend(2, "Kai", FriendState.Online)]);
        var after = List([Friend(1, "Luna", FriendState.Online), Friend(2, "Kai", FriendState.Playing, "MKW")], [new NextendoPerson(21, "Nico", "")]);
        quiet.Diff(before, All, Self, Noon);
        Assert.Equal([NextendoFriendEventKind.Playing], quiet.Diff(after, All with { NotifyOnline = false, NotifyRequests = false }, Self, Noon.AddHours(1)).Select(e => e.Kind));
        quiet.Diff(before with { Incoming = [] }, All, Self, Noon.AddHours(2));
        Assert.Equal([NextendoFriendEventKind.Online, NextendoFriendEventKind.Request],
            quiet.Diff(after, All with { NotifyPlaying = false }, Self, Noon.AddHours(3)).Select(e => e.Kind));
    }

    [Fact]
    public void Someone_who_accepted_the_player_s_request_is_news_and_a_request_the_player_accepted_is_not()
    {
        var watch = new NextendoFriendWatch();
        var carol = new NextendoPerson(31, "Carol", "SW-31");
        var nico = new NextendoPerson(21, "Nico", "SW-21");
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Summary, Requests: 1)], watch.Diff(List([], [nico], [carol]), All, Self, Noon));

        // Carol accepted (she was asked); Nico was accepted by the player: both are friends now, both online
        var events = watch.Diff(List([Friend(31, "Carol", FriendState.Online), Friend(21, "Nico", FriendState.Online)]), All, Self, Noon.AddSeconds(30));
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Accepted, "Carol")], events);
        Assert.Equal(new Toast("Carol accepted your friend request", "Nextendo Network", ToastKind.FriendRequest), events[0].Toast());
        // and with the requests' news off, not even that
        var quiet = new NextendoFriendWatch();
        quiet.Diff(List([], null, [carol]), All, Self, Noon);
        Assert.Empty(quiet.Diff(List([Friend(31, "Carol", FriendState.Online)]), All with { NotifyRequests = false }, Self, Noon.AddSeconds(30)));
        // someone Nextendo gives no name for is "A player"
        Assert.Equal(new Toast("A player sent you a friend request", Strings.FriendRequestBody, ToastKind.FriendRequest), new NextendoFriendEvent(NextendoFriendEventKind.Request).Toast());
    }

    [Fact]
    public void Many_friends_at_once_are_one_count()
    {
        var watch = new NextendoFriendWatch();
        NextendoFriend[] Five(FriendState state) => [.. Enumerable.Range(1, 5).Select(i => Friend(i, $"F{i}", state))];
        Assert.Empty(watch.Diff(List(Five(FriendState.Offline)), All, Self, Noon));
        var events = watch.Diff(List(Five(FriendState.Online), [new NextendoPerson(21, "Nico", "")]), All, Self, Noon.AddMinutes(10));
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Request, "Nico"), new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: 5)], events);
        // up to three are named
        var three = new NextendoFriendWatch();
        three.Diff(List(Five(FriendState.Offline)), All, Self, Noon);
        Assert.Equal(["F1", "F2", "F3"], three.Diff(List([.. Five(FriendState.Online).Take(3), .. Five(FriendState.Offline).Skip(3)]), All, Self, Noon.AddMinutes(1)).Select(e => e.Name));
    }

    // ---- asking, and how often ------------------------------------------------------------

    private sealed record Wired(NextendoAccount Account, NextendoApi Api, FakeNextendo Server, FakePeople People, ManualClock Clock, HttpClient Http) : IDisposable
    {
        public void Dispose()
        {
            Account.Dispose();
            Http.Dispose();
        }
    }

    private static Wired Wire()
    {
        var server = new FakeNextendo();
        var people = new FakePeople(server);
        var clock = new ManualClock();
        var vault = new NextendoVault(Path.Combine(Rig.NewFolder("nextendo-friends"), NextendoVault.FileName));
        Assert.True(vault.Save("refresh-0"));
        var http = new HttpClient(server);
        var api = new NextendoApi(http, "test");
        return new Wired(new NextendoAccount(api, vault, clock), api, server, people, clock, http);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(5, 300)]
    [InlineData(50, 300)]
    public void A_list_that_does_not_come_is_asked_for_more_and_more_slowly(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), NextendoRoster.Backoff(failures));
    }

    [Fact]
    public async Task The_list_is_asked_with_the_access_token_every_thirty_seconds_and_sooner_after_an_action()
    {
        using var wired = Wire();
        using var roster = new NextendoRoster(wired.Account, wired.Api, wired.Clock);
        wired.People.Friends.Add(FakePeople.Person(11, "Luna", "SW-2071-4456-9902", status: 2, appId: NextendoPresence.MkwTitleId));
        wired.People.Requests.Add(FakePeople.Person(21, "Nico", "SW-6621-5508-3317"));

        var first = await roster.PollAsync(All, Self, CancellationToken.None);
        Assert.Null(first.Error);
        Assert.Equal(NextendoRoster.Every, first.Next);
        Assert.Equal([new NextendoFriend(11, "Luna", "SW-2071-4456-9902", FriendState.Playing, "MKW", "", false)], first.List!.Friends);
        Assert.Equal([new NextendoFriendEvent(NextendoFriendEventKind.Summary, Online: 1, Requests: 1)], first.Events);
        Assert.Equal(["GET /api/friends?images=0"], wired.People.Calls); // without the pictures
        Assert.Contains(("/api/friends", "access-1"), wired.Server.Bearers); // the access token of the kept sign-in, never the game token

        // nothing new: no event; after an action (or the panel opening) it is asked every 10 s for a minute
        Assert.Empty((await roster.PollAsync(All, Self, CancellationToken.None)).Events);
        roster.Hurry();
        Assert.Equal(NextendoRoster.Fast, (await roster.PollAsync(All, Self, CancellationToken.None)).Next);
        wired.Clock.Advance(NextendoRoster.FastFor + TimeSpan.FromSeconds(1));
        Assert.Equal(NextendoRoster.Every, (await roster.PollAsync(All, Self, CancellationToken.None)).Next);

        // another account: nothing of the last one stays, and its first list is a summary again
        await roster.ResetAsync();
        Assert.Equal(NextendoFriendEventKind.Summary, Assert.Single((await roster.PollAsync(All, Self, CancellationToken.None)).Events).Kind);
    }

    [Fact]
    public async Task A_429_is_obeyed_and_a_network_that_is_away_is_asked_less_and_less()
    {
        using var wired = Wire();
        using var roster = new NextendoRoster(wired.Account, wired.Api, wired.Clock);
        wired.People.Friends.Add(FakePeople.Person(11, "Luna", "SW-2071-4456-9902", status: 1));
        Assert.Null((await roster.PollAsync(All, Self, CancellationToken.None)).Error);

        // 429 with Retry-After: 120 -> nothing is asked for two minutes, whoever asks (the loop, an action's refresh, the panel)
        wired.People.LimitSeconds = 120;
        var limited = await roster.PollAsync(All, Self, CancellationToken.None);
        Assert.Equal((NextendoError.Limited, TimeSpan.FromSeconds(120)), (limited.Error!.Kind, limited.Next));
        Assert.NotNull(limited.List); // what was known stays shown
        wired.People.LimitSeconds = 0;
        var asked = wired.People.Count("GET /api/friends");
        wired.Clock.Advance(TimeSpan.FromSeconds(50));
        var waiting = await roster.PollAsync(All, Self, CancellationToken.None);
        Assert.Equal((null, TimeSpan.FromSeconds(70)), (waiting.Error, waiting.Next));
        Assert.Equal((null, null), await roster.ExtrasAsync(force: true, CancellationToken.None));
        Assert.Equal(asked, wired.People.Count("GET /api/friends"));
        Assert.Equal(0, wired.People.Count("GET /api/recent-players"));
        wired.Clock.Advance(TimeSpan.FromSeconds(71));
        Assert.Equal((null, NextendoRoster.Every), ((await roster.PollAsync(All, Self, CancellationToken.None)).Error, (await roster.PollAsync(All, Self, CancellationToken.None)).Next));

        // a short Retry-After after failures is not a reason to hurry: the longer of the two waits
        wired.People.Fail = 503;
        Assert.Equal((NextendoError.Unreachable, TimeSpan.FromSeconds(30)), await Failed());
        Assert.Equal((NextendoError.Unreachable, TimeSpan.FromSeconds(60)), await Failed());
        Assert.Equal((NextendoError.Unreachable, TimeSpan.FromSeconds(120)), await Failed());
        (wired.People.Fail, wired.People.LimitSeconds) = (0, 5);
        Assert.Equal((NextendoError.Limited, TimeSpan.FromSeconds(240)), await Failed());
        wired.Clock.Advance(TimeSpan.FromSeconds(241));
        wired.People.LimitSeconds = 0;

        // the network itself is away: the same slowing down, and a list that comes again starts over
        wired.Server.Unreachable = true;
        Assert.Equal((NextendoError.Unreachable, TimeSpan.FromSeconds(300)), await Failed());
        wired.Server.Unreachable = false;
        var back = await roster.PollAsync(All, Self, CancellationToken.None);
        Assert.Equal((null, NextendoRoster.Every), (back.Error, back.Next));
        wired.People.Fail = 500;
        Assert.Equal((NextendoError.Unreachable, TimeSpan.FromSeconds(30)), await Failed());
        // an answer that is no friends list
        wired.People.Fail = 404;
        Assert.Equal((NextendoError.Bad, TimeSpan.FromSeconds(60)), await Failed());
        // a link without the permission, or a sign-in that ended: nothing to ask until the player links again
        wired.People.Fail = 403;
        Assert.Equal((NextendoError.Scope, NextendoRoster.SignedOutWait), await Failed());

        async Task<(NextendoError, TimeSpan)> Failed()
        {
            var poll = await roster.PollAsync(All, Self, CancellationToken.None);
            Assert.Empty(poll.Events);
            return (poll.Error!.Kind, poll.Next);
        }
    }

    [Fact]
    public async Task An_access_token_that_is_refused_is_renewed_once_without_holding_up_the_game_s_token()
    {
        using var wired = Wire();
        using var roster = new NextendoRoster(wired.Account, wired.Api, wired.Clock);
        Assert.Null((await roster.PollAsync(All, Self, CancellationToken.None)).Error);
        Assert.Equal(1, wired.Server.Count("POST /api/oauth/token"));
        // an hour later the access token is renewed before it is used; the game token is asked with the same one
        wired.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.Null((await roster.PollAsync(All, Self, CancellationToken.None)).Error);
        Assert.Equal(2, wired.Server.Count("POST /api/oauth/token"));
        Assert.Equal("nx2-game-token-1", (await wired.Account.GameTokenAsync(CancellationToken.None)).Token.Token);
        Assert.Equal(2, wired.Server.Count("POST /api/oauth/token"));
        // a sign-in that ended at Nextendo: said as such, and asked again only much later
        wired.Server.Revoked = true;
        wired.Clock.Advance(TimeSpan.FromMinutes(61));
        var ended = await roster.PollAsync(All, Self, CancellationToken.None);
        Assert.Equal((NextendoError.SignedOut, NextendoRoster.SignedOutWait), (ended.Error!.Kind, ended.Next));
    }

    // ---- presence -------------------------------------------------------------------------

    [Fact]
    public async Task Presence_is_said_at_once_again_every_minute_when_it_changes_and_taken_back_once()
    {
        using var wired = Wire();
        using var presence = new NextendoPresence(wired.Account, wired.Api, wired.Clock);
        var people = wired.People;

        // nothing to take back before anything was said
        Assert.False(await presence.StepAsync(null, CancellationToken.None));
        Assert.Empty(people.Presences);

        // online: said now, as the contract writes it
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        Assert.Equal("""{"status":1,"app_id":"","app_detail":"","platform":"app"}""", people.Presences[0].ToJsonString());
        Assert.Contains(("/api/presence", "access-1"), wired.Server.Bearers);

        // not again while it stands (the loop looks every few seconds), again after a minute: well inside Nextendo's 90 s
        wired.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        wired.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        Assert.Equal(2, people.Presences.Count);
        Assert.True(NextendoPresence.Every <= TimeSpan.FromSeconds(60));

        // the game starts: said at once, with the game's title id; then every minute again
        wired.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(await presence.StepAsync(NextendoPresence.InGame, CancellationToken.None));
        Assert.Equal("""{"status":2,"app_id":"0400c3f00006e000","app_detail":"Online","platform":"app"}""", people.Presences[2].ToJsonString());
        Assert.False(await presence.StepAsync(NextendoPresence.InGame, CancellationToken.None));
        wired.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.True(await presence.StepAsync(NextendoPresence.InGame, CancellationToken.None));

        // the game ends: online again at once
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        Assert.Equal(1, people.Presences[^1]["status"]!.GetValue<int>());

        // the launcher closes (or the account is unlinked, or the player hides): offline, once
        Assert.True(await presence.StepAsync(null, CancellationToken.None));
        Assert.Equal("""{"status":0,"app_id":"","app_detail":"","platform":"app"}""", people.Presences[^1].ToJsonString());
        Assert.Null(presence.Said);
        var said = people.Presences.Count;
        Assert.False(await presence.StepAsync(null, CancellationToken.None));
        wired.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.False(await presence.StepAsync(null, CancellationToken.None));
        Assert.Equal(said, people.Presences.Count);
        // and shown again later: said at once
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
    }

    [Fact]
    public async Task Presence_that_cannot_be_said_waits_before_it_tries_again()
    {
        using var wired = Wire();
        using var presence = new NextendoPresence(wired.Account, wired.Api, wired.Clock);
        var people = wired.People;

        people.Fail = 503;
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        Assert.Null(presence.Said);
        var tried = people.Count("POST /api/presence");
        wired.Clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None)); // 30 s after the first failure
        Assert.False(await presence.StepAsync(NextendoPresence.InGame, CancellationToken.None)); // also for another state
        wired.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        wired.Clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None)); // then a minute
        Assert.Equal(tried + 1, people.Count("POST /api/presence"));

        // 429: Retry-After is obeyed
        (people.Fail, people.LimitSeconds) = (0, 600);
        wired.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        people.LimitSeconds = 0;
        wired.Clock.Advance(TimeSpan.FromSeconds(599));
        Assert.False(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        wired.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await presence.StepAsync(NextendoPresence.InLauncher, CancellationToken.None));
        Assert.Equal(NextendoPresence.InLauncher, presence.Said);
        Assert.DoesNotContain(people.Presences, p => p["status"]!.GetValue<int>() == 0); // nothing was ever said, so nothing was taken back
    }

    // ---- the launcher ---------------------------------------------------------------------

    /// <summary>A launcher with a linked Nextendo account (bia, pid 1234567) that plays on Nextendo, or on MKWMP servers.</summary>
    private sealed class FriendsRig : IAsyncDisposable
    {
        private readonly Rig _rig;

        public FriendsRig(string playOn = "nextendo", Func<LauncherSettings, LauncherSettings>? edit = null)
        {
            People = new FakePeople(Nextendo);
            _rig = new Rig(Seat, s => (edit ?? (x => x))(s with { Launch = false }), http: new TwoNetworks(Seat, Nextendo),
                config: c => c with { Nextendo = NextendoLink.Of(playOn, Self, "bia", "SW-1111-2222-3333") },
                prepare: path => Assert.True(NextendoVault.Beside(path).Save("refresh-0")));
            Launcher.Toasted += (_, toast) =>
            {
                lock (Toasts)
                {
                    Toasts.Add(toast);
                }
            };
        }

        public FakeSeatServer Seat { get; } = new();
        public FakeNextendo Nextendo { get; } = new();
        public FakePeople People { get; }
        public List<Toast> Toasts { get; } = [];
        public Launcher Launcher => _rig.Launcher;
        public Targets.MkwRecomp.MkwRecompTarget Target => _rig.Target;
        public NextendoFriendsView View => Launcher.NextendoFriends;

        public ValueTask DisposeAsync() => _rig.DisposeAsync();
    }

    private static void Populate(FakePeople people)
    {
        people.Friends.Add(FakePeople.Person(11, "Luna", "SW-2071-4456-9902", status: 2, appId: NextendoPresence.MkwTitleId, favorite: true));
        people.Friends.Add(FakePeople.Person(12, "Kai", "SW-5530-1187-2264", status: 1));
        people.Friends.Add(FakePeople.Person(13, "Bea", "SW-9048-3317-6695"));
        people.Friends.Add(FakePeople.Person(14, "Abe", "SW-4416-7702-8843", favorite: true));
        people.Requests.Add(FakePeople.Person(21, "Nico", "SW-6621-5508-3317"));
        people.Sent.Add(FakePeople.Person(31, "Carol", "SW-5587-1120-9964"));
        people.Blocked.Add(FakePeople.Person(51, "Grumpy42", "SW-7719-0034-5562"));
        // met in a race: a friend already, the player themself, someone asked, someone blocked, someone without a code, and two to add
        people.Recent.AddRange(
        [
            FakePeople.Person(11, "Luna", "SW-2071-4456-9902"), FakePeople.Person(Self, "bia", "SW-1111-2222-3333"),
            FakePeople.Person(31, "Carol", "SW-5587-1120-9964"), FakePeople.Person(51, "Grumpy42", "SW-7719-0034-5562"),
            FakePeople.Person(43, "Guest", ""), FakePeople.Person(41, "Dario", "SW-3302-7719-4458"), FakePeople.Person(42, "Mei", "SW-8870-2265-1139"),
        ]);
    }

    [Fact]
    public async Task On_nextendo_the_launcher_shows_the_account_s_friends_and_says_once_who_is_there()
    {
        await using var rig = new FriendsRig();
        Populate(rig.People);
        Assert.Equal((true, false), (rig.View.Enabled, rig.View.Loaded)); // on Nextendo, nothing asked yet
        Assert.False(rig.Launcher.OnlineEnabled);

        await rig.Launcher.RefreshNextendoFriendsAsync();
        var view = rig.View;
        Assert.True(view is { Enabled: true, Loaded: true, Busy: false, Note: null, ShowOnline: true });
        Assert.Equal(2, view.OnlineCount);
        // who plays, who is online, then who is offline; the favourites first in each group, then by name
        Assert.Equal(["Luna", "Kai", "Abe", "Bea"], view.Ordered(StringComparer.OrdinalIgnoreCase).Select(f => f.Name));
        Assert.Equal(new NextendoFriend(11, "Luna", "SW-2071-4456-9902", FriendState.Playing, "MKW", "", true), view.Friends[0]);
        Assert.Equal([new NextendoPerson(21, "Nico", "SW-6621-5508-3317")], view.Incoming);
        Assert.Equal([new NextendoPerson(31, "Carol", "SW-5587-1120-9964")], view.Sent);
        Assert.Equal([new NextendoPerson(51, "Grumpy42", "SW-7719-0034-5562")], view.Blocked);
        // "Players you met": only those who can be asked and are nobody the lists already hold
        Assert.Equal(["Dario", "Mei"], view.Recent.Select(p => p.Name));

        // one notification for the whole first list, nobody by name
        Assert.Equal([new Toast("Friends online: 2", "Friend requests: 1", ToastKind.FriendOnline)], rig.Toasts);

        // later: Bea comes online and Carol accepts; the same list again says nothing more
        rig.People.Friends[2]["presence"]!["status"] = 1;
        rig.People.Friends.Add(rig.People.Sent[0]);
        rig.People.Sent.Clear();
        await rig.Launcher.RefreshNextendoFriendsAsync();
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal(["Friends online: 2", "Bea is online", "Carol accepted your friend request"], rig.Toasts.Select(t => t.Title));

        // not one MKWMP server was asked anything, and the game still gets no MKWMP friends (docs/contracts/NEXTENDO_API.md)
        Assert.Empty(rig.Seat.Requests);
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        var answer = await api.HandleAsync("GET", "/local/v1/friends", "Bearer " + rig.Launcher.Token, default, default);
        Assert.Equal((200, """{"friends":[]}"""), (answer.Status, answer.Body));

        // no friend's name, id or friend code is in the log (bug reports carry it)
        var log = ReadLog(rig.Launcher);
        Assert.All(ListSecrets, secret => Assert.DoesNotContain(secret, log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_game_gets_the_nextendo_friends_whose_id_nextendo_told()
    {
        await using var rig = new FriendsRig();
        Populate(rig.People);
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        async Task<(int, string?)> Friends()
        {
            var answer = await api.HandleAsync("GET", "/local/v1/friends", "Bearer " + rig.Launcher.Token, default, default);
            return (answer.Status, answer.Body);
        }

        // as Nextendo answers today: friends, and nobody's id: the game gets none (what it got before)
        Assert.Equal((200, """{"friends":[]}"""), await Friends());
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal((200, """{"friends":[]}"""), await Friends());

        // once it tells ids: those friends, each with its id; who plays, who is online, who is offline
        rig.People.Friends[0]["npln_account_id"] = "0123456789ABCDEF"; // Luna, playing
        rig.People.Friends[1]["npln_account_id"] = "junk"; // Kai, online: not an id, so not for the game
        rig.People.Friends[2]["account_hex"] = "1f"; // Bea, offline
        rig.People.Requests[0]["npln_account_id"] = "00000000000000b1"; // Nico asked: not a friend yet
        var zoe = FakePeople.Person(16, "Zoë \"Z\" <3 & co", "SW-0000-0000-0016", status: 1);
        zoe["npln_account_id"] = "8000000000000016";
        rig.People.Friends.Add(zoe);
        var self = FakePeople.Person(Self, "bia", "SW-1111-2222-3333", status: 1); // a server that lists the player
        self["npln_account_id"] = "00000000000000ee";
        rig.People.Friends.Add(self);
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal((200, """{"friends":[{"name":"Luna","online":true,"account":"0123456789abcdef"},"""
            + """{"name":"Zoë \"Z\" <3 & co","online":true,"account":"8000000000000016"},"""
            + """{"name":"Bea","online":false,"account":"000000000000001f"}]}"""), await Friends());

        // no id and no name is in the log, and no MKWMP server was asked anything
        var log = ReadLog(rig.Launcher);
        Assert.All(IdSecrets, secret => Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(rig.Seat.Requests);
    }

    private static readonly string[] IdSecrets = ["0123456789abcdef", "8000000000000016", "000000000000001f", "Zoë"];

    private static readonly string[] ListSecrets =["Luna", "Kai", "Bea", "Nico", "Carol", "Grumpy42", "Dario", "SW-2071", "SW-6621", "1234567", "access-"];
    private static readonly string[] ActionSecrets = ["Dario", "Yuki", "Kai", "Grumpy42", "SW-3302", "\"pid\""];

    private static string ReadLog(Launcher launcher)
    {
        using var file = new FileStream(launcher.Log.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task The_notification_settings_are_those_of_nextendo_friends_too()
    {
        await using var rig = new FriendsRig(edit: s => s with { Background = s.Background with { NotifyOnline = false, NotifyPlaying = false, NotifyRequests = false } });
        Populate(rig.People);
        await rig.Launcher.RefreshNextendoFriendsAsync();
        rig.People.Friends[2]["presence"]!["status"] = 2;
        rig.People.Requests.Add(FakePeople.Person(22, "Yuki", "SW-0193-8846-2275"));
        await rig.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal(2, rig.View.Incoming.Count);
        Assert.Empty(rig.Toasts);
    }

    [Fact]
    public async Task Friends_are_managed_through_nextendo_s_api()
    {
        await using var rig = new FriendsRig();
        var (launcher, people) = (rig.Launcher, rig.People);
        Populate(people);
        await launcher.RefreshNextendoFriendsAsync();
        string Last(string start) => people.Calls.Last(c => c.StartsWith(start, StringComparison.Ordinal));

        // add: the code as typed is written as Nextendo writes it
        await launcher.AddNextendoFriendAsync(" sw 3302 7719 4458 ");
        Assert.Equal("""POST /api/friends {"friend_code":"SW-3302-7719-4458"}""", Last("POST /api/friends "));
        Assert.Equal((Strings.RequestSent("Dario"), false), (rig.View.Note, rig.View.NoteIsError));
        Assert.Equal(["Carol", "Dario"], rig.View.Sent.Select(p => p.Name)); // the list was asked again
        Assert.Equal(["Mei"], rig.View.Recent.Select(p => p.Name)); // and Dario is no longer someone to add

        // what is not a friend code, and the player's own, never leave this PC
        var posts = people.Count("POST /api/friends ");
        await launcher.AddNextendoFriendAsync("Luna");
        Assert.Equal((Strings.NextendoFriendCodeRule, true), (rig.View.Note, rig.View.NoteIsError));
        await launcher.AddNextendoFriendAsync("sw-1111-2222-3333");
        Assert.Equal((Strings.NextendoFriendCodeOwn, true), (rig.View.Note, rig.View.NoteIsError));
        Assert.Equal(posts, people.Count("POST /api/friends "));
        // what Nextendo refuses is said in the player's words
        await launcher.AddNextendoFriendAsync("SW-0000-0000-0000");
        Assert.Equal((Strings.NextendoFriendCodeUnknown, true), (rig.View.Note, rig.View.NoteIsError));
        await launcher.AddNextendoFriendAsync("SW-3003-0030-0300");
        Assert.Equal((Strings.NextendoFriendsFull, true), (rig.View.Note, rig.View.NoteIsError));
        // someone who had asked first: friends at once
        await launcher.AddNextendoFriendAsync("SW-6621-5508-3317");
        Assert.Equal((Strings.FriendsNow("Nico"), false), (rig.View.Note, rig.View.NoteIsError));
        Assert.Contains(rig.View.Friends, f => f.Pid == 21);
        Assert.Empty(rig.View.Incoming);

        // accept, decline
        people.Requests.Add(FakePeople.Person(22, "Yuki", "SW-0193-8846-2275"));
        people.Requests.Add(FakePeople.Person(23, "Rin", "SW-7705-2291-4480"));
        await launcher.RefreshNextendoFriendsAsync();
        await launcher.AcceptNextendoFriendAsync(22);
        Assert.Equal("""POST /api/friends/accept {"pid":22}""", Last("POST /api/friends/accept"));
        Assert.Equal(Strings.FriendsNow("Yuki"), rig.View.Note);
        Assert.Contains(rig.View.Friends, f => f.Name == "Yuki");
        await launcher.DeclineNextendoFriendAsync(23);
        Assert.Equal("""POST /api/friends/decline {"pid":23}""", Last("POST /api/friends/decline"));
        Assert.Equal(Strings.RequestDeclined("Rin"), rig.View.Note);
        Assert.Empty(rig.View.Incoming);
        // a request that is gone meanwhile
        await launcher.AcceptNextendoFriendAsync(23);
        Assert.Equal((Strings.NextendoRequestGone, true), (rig.View.Note, rig.View.NoteIsError));

        // favourite: nothing to say, the list says it
        await launcher.FavoriteNextendoFriendAsync(12, true);
        Assert.Equal("""POST /api/friends/favorite {"pid":12,"favorite":true}""", Last("POST /api/friends/favorite"));
        Assert.True(rig.View.Friends.Single(f => f.Pid == 12).Favorite);
        Assert.Null(rig.View.Note);
        await launcher.FavoriteNextendoFriendAsync(12, false);
        Assert.False(rig.View.Friends.Single(f => f.Pid == 12).Favorite);

        // remove, block (the blocked list is asked again), unblock
        await launcher.RemoveNextendoFriendAsync(13);
        Assert.Equal("""POST /api/friends/remove {"pid":13}""", Last("POST /api/friends/remove"));
        Assert.Equal(Strings.FriendRemoved("Bea"), rig.View.Note);
        Assert.DoesNotContain(rig.View.Friends, f => f.Pid == 13);
        await launcher.BlockNextendoPlayerAsync(12);
        Assert.Equal("""POST /api/friends/block {"pid":12}""", Last("POST /api/friends/block"));
        Assert.Equal(Strings.NextendoBlocked("Kai"), rig.View.Note);
        Assert.DoesNotContain(rig.View.Friends, f => f.Pid == 12);
        Assert.Equal(["Grumpy42", "Kai"], rig.View.Blocked.Select(p => p.Name));
        await launcher.UnblockNextendoPlayerAsync(51);
        Assert.Equal("""POST /api/friends/unblock {"pid":51}""", Last("POST /api/friends/unblock"));
        Assert.Equal(Strings.NextendoUnblocked("Grumpy42"), rig.View.Note);
        Assert.Equal(["Kai"], rig.View.Blocked.Select(p => p.Name));

        // Nextendo away: the action says so, and what was known stays shown
        rig.Nextendo.Unreachable = true;
        var friends = rig.View.Friends.Count;
        await launcher.RemoveNextendoFriendAsync(11);
        Assert.Equal((Strings.NextendoUnreachable, true, false), (rig.View.Note, rig.View.NoteIsError, rig.View.Busy));
        Assert.Equal(friends, rig.View.Friends.Count);
        rig.Nextendo.Unreachable = false;

        Assert.Empty(rig.Seat.Requests);
        var log = ReadLog(launcher);
        Assert.All(ActionSecrets, secret => Assert.DoesNotContain(secret, log, StringComparison.Ordinal));
    }

    [Fact]
    public async Task On_mkwmp_servers_or_unlinked_nextendo_friends_are_off_and_nothing_is_asked()
    {
        await using var mkwmp = new FriendsRig(playOn: "mkwmp");
        Populate(mkwmp.People);
        Assert.True(mkwmp.Launcher.OnlineEnabled);
        Assert.False(mkwmp.View.Enabled);
        await mkwmp.Launcher.RefreshNextendoFriendsAsync();
        await mkwmp.Launcher.AddNextendoFriendAsync("SW-3302-7719-4458");
        await mkwmp.Launcher.AcceptNextendoFriendAsync(21);
        Assert.Null(mkwmp.Launcher.NextendoPresenceWanted);
        Assert.False(await mkwmp.Launcher.NextendoPresenceStepAsync(CancellationToken.None));
        Assert.Empty(mkwmp.People.Calls);
        Assert.Same(NextendoFriendsView.Off.Friends, mkwmp.View.Friends);

        // switching to Nextendo turns them on; unlinking turns them off and takes the presence back first
        await mkwmp.Launcher.SetPlayOnAsync(PlayNetwork.Nextendo);
        Assert.True(mkwmp.View.Enabled);
        await mkwmp.Launcher.RefreshNextendoFriendsAsync();
        Assert.Equal(4, mkwmp.View.Friends.Count);
        Assert.True(await mkwmp.Launcher.NextendoPresenceStepAsync(CancellationToken.None));
        await mkwmp.Launcher.UnlinkNextendoAsync();
        Assert.Equal((false, 0, false), (mkwmp.View.Enabled, mkwmp.View.Friends.Count, mkwmp.View.Loaded));
        Assert.Equal([1, 0], mkwmp.People.Presences.Select(p => p["status"]!.GetValue<int>()));
        Assert.Null(mkwmp.Launcher.NextendoPresenceWanted);
    }

    [Fact]
    public async Task The_launcher_says_online_then_playing_while_the_game_runs_and_offline_when_it_closes()
    {
        var rig = new FriendsRig();
        var launcher = rig.Launcher;
        Assert.Equal(NextendoPresence.InLauncher, launcher.NextendoPresenceWanted);
        Assert.True(await launcher.NextendoPresenceStepAsync(CancellationToken.None));
        Assert.False(await launcher.NextendoPresenceStepAsync(CancellationToken.None));

        // PLAY on Nextendo, the game runs: playing, with the game's title id
        await launcher.PlayAsync();
        rig.Target.RunningOverride = () => true;
        await Rig.Until("the game to run", () => launcher.Session is { State: SessionState.Running, GameRunning: true } || launcher.Session.State == SessionState.Failed);
        Assert.Equal(NextendoPresence.InGame, launcher.NextendoPresenceWanted);
        Assert.True(await launcher.NextendoPresenceStepAsync(CancellationToken.None));
        Assert.Equal((2, NextendoPresence.MkwTitleId), (rig.People.Presences[^1]["status"]!.GetValue<int>(), rig.People.Presences[^1]["app_id"]!.GetValue<string>()));
        rig.Target.RunningOverride = () => false;
        await launcher.StopAsync();
        await Rig.Until("the session to end", () => !launcher.Session.Busy);
        Assert.Equal(NextendoPresence.InLauncher, launcher.NextendoPresenceWanted);
        await launcher.NextendoPresenceStepAsync(CancellationToken.None);
        Assert.Equal(1, rig.People.Presences[^1]["status"]!.GetValue<int>());

        // "Show me online to friends" off: taken back, and nothing more is said
        await launcher.SaveSettingsAsync(launcher.Settings with { NextendoShowOnline = false });
        Assert.False(rig.View.ShowOnline);
        Assert.Null(launcher.NextendoPresenceWanted);
        Assert.True(await launcher.NextendoPresenceStepAsync(CancellationToken.None));
        Assert.Equal(0, rig.People.Presences[^1]["status"]!.GetValue<int>());
        var said = rig.People.Presences.Count;
        Assert.False(await launcher.NextendoPresenceStepAsync(CancellationToken.None));
        Assert.Contains("show_online = false", File.ReadAllText(launcher.ConfigPath), StringComparison.Ordinal);

        // on again; closing the launcher says offline
        await launcher.SaveSettingsAsync(launcher.Settings with { NextendoShowOnline = true });
        Assert.DoesNotContain("show_online", File.ReadAllText(launcher.ConfigPath), StringComparison.Ordinal);
        Assert.True(await launcher.NextendoPresenceStepAsync(CancellationToken.None));
        Assert.Equal(said + 1, rig.People.Presences.Count);
        await rig.DisposeAsync();
        Assert.Equal(0, rig.People.Presences[^1]["status"]!.GetValue<int>());
        Assert.Equal(said + 2, rig.People.Presences.Count);
    }

    [Fact]
    public void Show_online_is_kept_in_the_settings_file_only_when_it_is_off()
    {
        var fresh = ClientConfig.Parse("");
        Assert.True(fresh.Settings.NextendoShowOnline);
        Assert.DoesNotContain("show_online", fresh.ToToml(), StringComparison.Ordinal);
        var off = fresh with { Settings = fresh.Settings with { NextendoShowOnline = false } };
        Assert.Contains("\n[nextendo]\nshow_online = false\n", off.ToToml(), StringComparison.Ordinal);
        var back = ClientConfig.Parse(off.ToToml());
        Assert.False(back.Settings.NextendoShowOnline);
        Assert.Null(back.Nextendo.Choice); // saying it names no network
        Assert.True(back.SameAs(off));
        var linked = off with { Nextendo = NextendoLink.Of("nextendo", 42, "bia", "SW-1") };
        Assert.Contains("[nextendo]\nplay_on = \"nextendo\"\nshow_online = false\npid = 42\n", linked.ToToml(), StringComparison.Ordinal);
        Assert.True(ClientConfig.Parse(linked.ToToml()).SameAs(linked));
    }
}
