// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Tests;

/// <summary>The game's side of the launcher: /local/v1 over a real loopback socket (docs/contracts/LAUNCHER_API.md, VOICE_API.md §4).</summary>
public class LocalApiTests
{
    private sealed class Game : IDisposable
    {
        private readonly HttpClient _http = new();
        private readonly LocalApi _api;
        private readonly string _token;

        public Game(Launcher launcher)
        {
            _api = new LocalApi(launcher, TimeProvider.System) { FriendsWait = TimeSpan.FromMilliseconds(300) };
            _api.Listen(0);
            _token = launcher.Token;
        }

        public int Port => _api.Port;

        public async Task<(int Status, JsonNode? Body, HttpResponseMessage Raw)> Send(HttpMethod method, string path, string? body = null, string? token = "ok")
        {
            using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{Port}{path}");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token == "ok" ? _token : token);
            }
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }
            var response = await _http.SendAsync(request);
            var text = await response.Content.ReadAsStringAsync();
            return ((int)response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text), response);
        }

        public Task<(int Status, JsonNode? Body, HttpResponseMessage Raw)> Get(string path, string? token = "ok") => Send(HttpMethod.Get, path, null, token);

        public Task<(int Status, JsonNode? Body, HttpResponseMessage Raw)> Post(string path, string? body = null, string? token = "ok") => Send(HttpMethod.Post, path, body, token);

        public void Dispose()
        {
            _http.Dispose();
            _api.Dispose();
        }
    }

    [Fact]
    public async Task Voice_routes_over_http()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var game = new Game(rig.Launcher);
        Assert.Equal(401, (await game.Get("/local/v1/voice", token: null)).Status);
        var wrong = await game.Get("/local/v1/voice", token: "nope");
        Assert.Equal((401, "unauthorized"), (wrong.Status, wrong.Body!["state"]!.GetValue<string>()));
        var voice = await game.Get("/local/v1/voice");
        Assert.Equal(200, voice.Status);
        Assert.Equal("application/json", voice.Raw.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            """{"enabled":true,"input":"","output":"","inputGain":100,"outputGain":100,"mutes":[{"account":"42","name":"Bia"}]}""",
            voice.Body!.ToJsonString());

        var devices = await game.Post("/local/v1/voice/devices", """{"inputs":[{"id":"w1","name":"Mic"},{"id":"w1","name":"dup"},{"id":"","name":"x"},{"id":"b"}],"outputs":[]}""");
        Assert.Equal((204, (JsonNode?)null), (devices.Status, devices.Body));
        Assert.Equal([new AudioDevice("w1", "Mic"), new AudioDevice("b", "b")], rig.Launcher.Voice.Inputs);
        Assert.Equal(400, (await game.Post("/local/v1/voice/devices", """{"inputs":{"id":"a"},"outputs":[]}""")).Status);
        Assert.Equal(400, (await game.Post("/local/v1/voice/devices", "not json")).Status);

        // The account is a u64 in canonical decimal, as a string.
        Assert.Equal(204, (await game.Post("/local/v1/voice/mute", """{"account":"18446744073709551615","name":"Cid","muted":true}""")).Status);
        foreach (var bad in new[]
        {
            """{"account":5,"muted":true}""", """{"account":"05","muted":true}""", """{"account":"18446744073709551616","muted":true}""",
            """{"account":"-1","muted":true}""", """{"account":"+1","muted":true}""", """{"account":" 1","muted":true}""", """{"account":"1"}""", """{"account":"1","muted":"yes"}""",
        })
        {
            var refused = await game.Post("/local/v1/voice/mute", bad);
            Assert.Equal((400, "invalid"), (refused.Status, refused.Body!["state"]!.GetValue<string>()));
        }
        Assert.Equal(204, (await game.Post("/local/v1/voice/mute", """{"account":"42","muted":false}""")).Status);
        voice = await game.Get("/local/v1/voice");
        Assert.Equal("""[{"account":"18446744073709551615","name":"Cid"}]""", voice.Body!["mutes"]!.ToJsonString());

        // Persisted; a window save keeps the game's lists.
        var saved = ClientConfig.Load(rig.ConfigPath);
        Assert.Equal((2, 1), (saved.Inputs.Count, saved.Mutes.Count));
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Voice = rig.Launcher.Settings.Voice with { Enabled = false, InputGain = 0 } });
        voice = await game.Get("/local/v1/voice");
        Assert.Equal((false, 1, 1), (voice.Body!["enabled"]!.GetValue<bool>(), voice.Body["inputGain"]!.GetValue<int>(), voice.Body["mutes"]!.AsArray().Count));
        Assert.Equal(2, rig.Launcher.Voice.Inputs.Count);
        await rig.Launcher.UnmuteAsync("18446744073709551615");
        Assert.Empty(rig.Launcher.Voice.Mutes);
        Assert.Empty(ClientConfig.Load(rig.ConfigPath).Mutes);
    }

    [Fact]
    public async Task Seat_and_friends_need_play_and_answer_the_contract_shapes()
    {
        var server = new FakeSeatServer { Mood = SeatMood.QueuedFor, Count = 1000 };
        await using var rig = new Rig(server, s => s with { Launch = false });
        using var game = new Game(rig.Launcher);
        var before = await game.Post("/local/v1/seat");
        Assert.Equal((503, """{"state":"unavailable","message":"press PLAY in the mkwmp launcher"}"""), (before.Status, before.Body!.ToJsonString()));
        Assert.Equal(503, (await game.Get("/local/v1/friends")).Status);
        Assert.Equal(401, (await game.Post("/local/v1/seat", token: null)).Status);
        Assert.Equal(404, (await game.Get("/local/v1/room")).Status);
        Assert.Equal(405, (await game.Get("/local/v1/seat")).Status);

        await rig.Launcher.PlayAsync();
        await Rig.Until("the queue", () => rig.Launcher.Session.Phase == SeatPhase.Queued);
        var queued = await game.Post("/local/v1/seat?x=1");
        // The position moves with the queue (2 at the join, 1 after a heartbeat).
        Assert.Equal((200, """{"state":"queued","position":N,"seatsTotal":2}"""), (queued.Status, queued.Body!.ToJsonString().Replace("\"position\":1", "\"position\":N", StringComparison.Ordinal).Replace("\"position\":2", "\"position\":N", StringComparison.Ordinal)));
        // No friends list yet (no friends loop in this rig): the game is told to retry, not given an empty list.
        var loading = await game.Get("/local/v1/friends");
        Assert.Equal((503, "loading"), (loading.Status, loading.Body!["state"]!.GetValue<string>()));
        await rig.Launcher.StopAsync();
    }

    [Fact]
    public async Task Seated_refused_and_friends_shapes()
    {
        var friends = Enumerable.Range(0, 120).Select(i => new Friend($"F{i}", i == 0 ? FriendState.Playing : i == 1 ? FriendState.Online : FriendState.Offline)).ToList();
        using var http = new HttpClient(new FakeSeatServer());
        using var seated = new SeatSession(new SeatApi(http, FakeSeatServer.Url, "test"), SeatMode.Online, "Ana", "123456", _ => { }, TimeProvider.System);
        var host = new Host { Seat = seated, FriendsOrNull = friends };
        using var api = new LocalApi(host, TimeProvider.System);
        var answer = await api.HandleAsync("POST", "/local/v1/seat", "Bearer t0ken", default, default);
        // A seat server older than rooms leaves View.room out: the game is told "room": null.
        Assert.Equal((200, """{"state":"seated","ticket":"mkw1.ticket.sig","lobby":"lobby.test:8905","name":"Ana","room":null}"""), (answer.Status, answer.Body));
        answer = await api.HandleAsync("GET", "/local/v1/friends", "Bearer t0ken", default, default);
        var list = JsonNode.Parse(answer.Body!)!["friends"]!.AsArray();
        Assert.Equal(LocalApi.MaxFriends, list.Count);
        Assert.Equal("""[{"name":"F0","online":true},{"name":"F1","online":true},{"name":"F2","online":false}]""", new JsonArray([.. list.Take(3).Select(n => n!.DeepClone())]).ToJsonString());

        using var refusing = new HttpClient(new FakeSeatServer { Mood = SeatMood.Refused });
        using var refused = new SeatSession(new SeatApi(refusing, FakeSeatServer.Url, "test"), SeatMode.Online, "Ana", "000000", _ => { }, TimeProvider.System);
        host.Seat = refused;
        answer = await api.HandleAsync("POST", "/local/v1/seat", "Bearer t0ken", default, default);
        Assert.Equal((409, JsonSerializer.Serialize(new { state = "refused", message = Strings.WrongPin })), (answer.Status, answer.Body));
        Assert.Equal(401, (await api.HandleAsync("POST", "/local/v1/seat", "Bearer t0ken ", default, default)).Status);
        Assert.Equal(401, (await api.HandleAsync("POST", "/local/v1/seat", "t0ken", default, default)).Status);
    }

    [Fact]
    public async Task The_seated_answer_carries_the_lobby_room_the_seat_server_reports()
    {
        const string Room = """{"lobby":"0b4e7a10-1111-4222-8333-444455556666","ussid":12,"ussids":[3,12,16],"at":1763647200123}""";
        var server = new FakeSeatServer { RoomJson = "null" };
        using var http = new HttpClient(server);
        using var seat = new SeatSession(new SeatApi(http, FakeSeatServer.Url, "test"), SeatMode.Online, "Ana", "123456", _ => { }, TimeProvider.System);
        using var api = new LocalApi(new Host { Seat = seat }, TimeProvider.System);
        async Task<string> Seated() =>
            JsonNode.Parse((await api.HandleAsync("POST", "/local/v1/seat", "Bearer t0ken", default, default)).Body!)!.AsObject().TryGetPropertyValue("room", out var room)
                ? room?.ToJsonString() ?? "null"
                : "absent";

        // Joined, in no room yet.
        Assert.Equal("null", await Seated());

        // The lobby put the seat in a room: the next heartbeat brings it, and the game gets it as the server said it.
        server.RoomJson = Room;
        await Task.Delay(1100); // the fake's nextPollMs (1 s)
        await seat.TickAsync();
        Assert.Equal(Room, await Seated());

        // The room ended (or the lobby's report went stale): null again.
        server.RoomJson = "null";
        await Task.Delay(1100);
        await seat.TickAsync();
        Assert.Equal("null", await Seated());
        Assert.True(server.Polls >= 2);
    }

    [Theory]
    [InlineData(null, null)] // absent: a server older than rooms
    [InlineData("null", null)]
    [InlineData("""{"lobby":"L","ussid":12,"ussids":[3,12,16],"at":1763647200123}""", "L 12 3,12,16 1763647200123")]
    [InlineData("""{"lobby":"L","ussid":3,"ussids":[3],"at":5}""", "L 3 3 5")]
    [InlineData("""{"lobby":"","ussid":12,"ussids":[12],"at":1}""", null)] // not a room's shape: no room, never a failure
    [InlineData("""{"lobby":"L","ussids":[12],"at":1}""", null)]
    [InlineData("""{"lobby":"L","ussid":12,"ussids":[12]}""", null)]
    [InlineData("""{"lobby":"L","ussid":12,"ussids":"12","at":1}""", null)]
    [InlineData("""{"lobby":"L","ussid":12,"ussids":[12,"x"],"at":1}""", null)]
    [InlineData("[]", null)]
    public async Task The_view_s_room_is_read_whatever_the_server_sends(string? room, string? expected)
    {
        using var http = new HttpClient(new FakeSeatServer { RoomJson = room });
        var view = await new SeatApi(http, FakeSeatServer.Url, "test").JoinAsync("Ana", "123456", default);
        Assert.Equal("mkw1.ticket.sig", view.Ticket?.Token); // the rest of the view is read as before
        Assert.Equal(expected, view.Room is { } r ? $"{r.Lobby} {r.Ussid} {string.Join(',', r.Ussids)} {r.At}" : null);
    }

    [Fact]
    public async Task Limits_are_checked_before_the_token()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var game = new Game(rig.Launcher);
        // Burst 10 on the seat endpoint, wrong token or not.
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(401, (await game.Post("/local/v1/seat", token: "guess")).Status);
        }
        var limited = await game.Post("/local/v1/seat");
        Assert.Equal((429, """{"state":"limited"}"""), (limited.Status, limited.Body!.ToJsonString()));
        Assert.Equal("1", limited.Raw.Headers.GetValues("Retry-After").Single());
        // Each endpoint has its own bucket; the two voice POSTs share one of 20.
        Assert.Equal(503, (await game.Get("/local/v1/friends")).Status);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(400, (await game.Post(i % 2 == 0 ? "/local/v1/voice/mute" : "/local/v1/voice/devices", "{}")).Status);
        }
        Assert.Equal(429, (await game.Post("/local/v1/voice/mute", "{}")).Status);
        Assert.Equal(200, (await game.Get("/local/v1/voice")).Status);
        // It refills: one a second on the seat endpoint.
        await Task.Delay(1100);
        Assert.Equal(503, (await game.Post("/local/v1/seat")).Status);
    }

    [Fact]
    public async Task The_listener_speaks_plain_http_keeps_the_connection_and_is_loopback_only()
    {
        await using var rig = new Rig(new FakeSeatServer());
        using var game = new Game(rig.Launcher);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, game.Port);
        var stream = tcp.GetStream();
        async Task<string> Exchange(string request)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request));
            var buffer = new byte[4096];
            var have = 0;
            while (true)
            {
                have += await stream.ReadAsync(buffer.AsMemory(have));
                var text = Encoding.ASCII.GetString(buffer, 0, have);
                var head = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (head < 0)
                {
                    continue;
                }
                var length = text.Split("\r\n").FirstOrDefault(l => l.StartsWith("content-length:", StringComparison.OrdinalIgnoreCase)) is { } cl ? int.Parse(cl.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
                if (text.Length >= head + 4 + length)
                {
                    return text;
                }
            }
        }
        // What the probe sends (WinHTTP): keep-alive, a POST with Content-Length: 0, then another request on the same connection.
        var first = await Exchange($"POST /local/v1/seat HTTP/1.1\r\nHost: 127.0.0.1\r\nAuthorization: Bearer {rig.Launcher.Token}\r\nContent-Length: 0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 503 Service Unavailable\r\n", first, StringComparison.Ordinal);
        var body = """{"account":"7","name":"Dan","muted":true}""";
        var second = await Exchange($"POST /local/v1/voice/mute HTTP/1.1\r\nHost: x\r\nauthorization: Bearer {rig.Launcher.Token}\r\ncontent-type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
        Assert.StartsWith("HTTP/1.1 204 No Content\r\n", second, StringComparison.Ordinal);
        Assert.Contains(rig.Launcher.Voice.Mutes, m => m == new MutedPlayer("7", "Dan"));
        var last = await Exchange("GET /nothing HTTP/1.1\r\nConnection: close\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 404 Not Found\r\n", last, StringComparison.Ordinal);
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));

        // Bound to 127.0.0.1 only, and a second listener on the port is refused (another launcher).
        using var second2 = new LocalApi(rig.Launcher, TimeProvider.System);
        Assert.Throws<SocketException>(() => second2.Listen(game.Port));
    }

    private sealed class Host : ILocalApiHost
    {
        public string Token => "t0ken";
        public SeatSession? Seat { get; set; }
        public IReadOnlyList<Friend>? FriendsOrNull { get; set; }
        public ClientConfig Config { get; } = new();

        public string? EditVoice(Func<ClientConfig, ClientConfig> edit) => null;
    }
}
