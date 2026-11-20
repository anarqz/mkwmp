// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mkwmp.Core.Overlay;

namespace Mkwmp.Core.Net;

/// <summary>What the local API needs of the launcher.</summary>
internal interface ILocalApiHost
{
    /// <summary>The bearer token written into the game's session file.</summary>
    string Token { get; }

    /// <summary>The live seat (null until PLAY).</summary>
    SeatSession? Seat { get; }

    /// <summary>A server is chosen. False (offline): the game never gets a seat, and its Online Play is told why.</summary>
    bool Online => true;

    /// <summary>Accepted friends as the server orders them; null until the first list arrives.</summary>
    IReadOnlyList<Friend>? FriendsOrNull { get; }

    ClientConfig Config { get; }

    /// <summary>Changes the voice lists in place and saves; the error text when the save failed.</summary>
    string? EditVoice(Func<ClientConfig, ClientConfig> edit);
}

internal sealed record LocalResponse(int Status, string? Body = null, bool RetryAfter = false);

/// <summary>
/// The launcher's local API (docs/contracts/LAUNCHER_API.md, VOICE_API.md §4): the hook inside the
/// game asks for its seat, its friends and its voice settings on 127.0.0.1 with the bearer token
/// from its session file. A raw <see cref="TcpListener"/> with a minimal HTTP/1.1 reader:
/// HttpListener needs a URL reservation on Windows and is unreliable under Wine, and the only
/// client is the game patch.
/// </summary>
internal sealed class LocalApi(ILocalApiHost host, TimeProvider clock) : IDisposable
{
    public const int MaxFriends = 100;
    private const int MaxText = 256;
    private const int MaxHead = 16 * 1024;
    private const int MaxBody = 256 * 1024;

    /// <summary>The overlay's picture (POST /local/v1/overlay/open): 1920 x 1080 RGBA, read only from a caller with the token.</summary>
    private const int MaxFrameBody = OverlayFrame.MaxWidth * OverlayFrame.MaxHeight * 4;

    /// <summary>How long GET /local/v1/friends waits for the first friends fetch.</summary>
    internal TimeSpan FriendsWait { get; init; } = TimeSpan.FromSeconds(3);

    // The probe calls seat every <= 20 s and friends every 15 s; voice is polled every 2 s.
    private readonly TokenBucket _seat = new(10, 1, clock);
    private readonly TokenBucket _friends = new(10, 1, clock);
    private readonly TokenBucket _voice = new(10, 2, clock);
    private readonly TokenBucket _voicePost = new(20, 2, clock);
    // The overlay: a state poll every 2 s while it is open and every 10 s otherwise; an open per button press.
    private readonly TokenBucket _overlayOpen = new(10, 2, clock);
    private readonly TokenBucket _overlayState = new(20, 2, clock);
    private readonly TokenBucket _instance = new(5, 1, clock);    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    /// <summary>The console-style overlay (/local/v1/overlay/...; launcher/docs/OVERLAY.md).</summary>
    public OverlayBridge Overlay { get; } = new(clock);

    /// <summary>Standing by for a game the launcher did not start, and a second launcher's requests (/local/v1/instance/...).</summary>
    public OverlayHost Instance { get; } = new();

    /// <summary>The port being served (0: not listening).</summary>
    public int Port { get; private set; }

    /// <summary>Binds 127.0.0.1:<paramref name="port"/> (0: any free port) and serves. Throws <see cref="SocketException"/> when the port is taken.</summary>
    public void Listen(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(() => AcceptAsync(listener, _stop.Token));
    }

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            _ = Task.Run(() => ServeAsync(client, cancel), CancellationToken.None);
        }
    }

    /// <summary>One connection: requests one after another (keep-alive) until the client closes or goes quiet.</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken cancel)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                var buffer = new byte[MaxHead];
                var have = 0;
                while (!cancel.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    idle.CancelAfter(TimeSpan.FromSeconds(15));
                    int headEnd;
                    while ((headEnd = buffer.AsSpan(0, have).IndexOf("\r\n\r\n"u8)) < 0)
                    {
                        if (have == buffer.Length)
                        {
                            await WriteAsync(stream, new LocalResponse(431), close: true, idle.Token).ConfigureAwait(false);
                            return;
                        }
                        var n = await stream.ReadAsync(buffer.AsMemory(have), idle.Token).ConfigureAwait(false);
                        if (n == 0)
                        {
                            return;
                        }
                        have += n;
                    }
                    var head = Encoding.Latin1.GetString(buffer, 0, headEnd).Split("\r\n");
                    var request = head[0].Split(' ');
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in head.Skip(1))
                    {
                        var colon = line.IndexOf(':', StringComparison.Ordinal);
                        if (colon > 0)
                        {
                            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                        }
                    }
                    if (request.Length < 3 || !request[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                    {
                        await WriteAsync(stream, new LocalResponse(400), close: true, idle.Token).ConfigureAwait(false);
                        return;
                    }
                    if (headers.ContainsKey("Transfer-Encoding"))
                    {
                        await WriteAsync(stream, new LocalResponse(411), close: true, idle.Token).ConfigureAwait(false);
                        return;
                    }
                    var length = 0;
                    // Only the overlay's picture is larger than MaxBody, and only the game (the token) may send one.
                    var maxBody = request[1].StartsWith("/local/v1/overlay/open", StringComparison.Ordinal) && Authorized(headers.GetValueOrDefault("Authorization")) ? MaxFrameBody : MaxBody;
                    if (headers.TryGetValue("Content-Length", out var cl) && (!int.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > maxBody))
                    {
                        await WriteAsync(stream, new LocalResponse(413), close: true, idle.Token).ConfigureAwait(false);
                        return;
                    }
                    var body = new byte[length];
                    var start = headEnd + 4;
                    var buffered = Math.Min(length, have - start);
                    buffer.AsSpan(start, buffered).CopyTo(body);
                    for (var got = buffered; got < length;)
                    {
                        var n = await stream.ReadAsync(body.AsMemory(got), idle.Token).ConfigureAwait(false);
                        if (n == 0)
                        {
                            return;
                        }
                        got += n;
                    }
                    // What follows in the buffer is the next request (pipelining).
                    var rest = have - start - buffered;
                    buffer.AsSpan(start + buffered, rest).CopyTo(buffer);
                    have = rest;

                    var path = request[1];
                    var query = path.IndexOf('?', StringComparison.Ordinal);
                    var response = await HandleAsync(request[0], query < 0 ? path : path[..query], query < 0 ? "" : path[(query + 1)..], headers.GetValueOrDefault("Authorization"), body, cancel).ConfigureAwait(false);
                    var close = request[2] == "HTTP/1.0" || string.Equals(headers.GetValueOrDefault("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                    await WriteAsync(stream, response, close, cancel).ConfigureAwait(false);
                    if (close)
                    {
                        return;
                    }
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // the game went away mid-request
            }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, LocalResponse r, bool close, CancellationToken cancel)
    {
        var body = r.Body is null ? [] : Encoding.UTF8.GetBytes(r.Body);
        var head = new StringBuilder().Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {r.Status} {Reason(r.Status)}\r\n");
        if (r.Body is not null)
        {
            head.Append("content-type: application/json\r\n");
        }
        if (r.Status != 204)
        {
            head.Append(CultureInfo.InvariantCulture, $"content-length: {body.Length}\r\n");
        }
        if (r.RetryAfter)
        {
            head.Append("retry-after: 1\r\n");
        }
        if (close)
        {
            head.Append("connection: close\r\n");
        }
        head.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(head.ToString());
        var all = new byte[bytes.Length + body.Length];
        bytes.CopyTo(all, 0);
        body.CopyTo(all, bytes.Length);
        await stream.WriteAsync(all, cancel).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        401 => "Unauthorized",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Error",
    };

    // ---- the routes -----------------------------------------------------------------------

    /// <summary>One request. The token bucket is asked before the token is checked: a wrong token cannot be guessed faster than the limit.</summary>
    public Task<LocalResponse> HandleAsync(string method, string path, string? authorization, ReadOnlyMemory<byte> body, CancellationToken cancel) =>
        HandleAsync(method, path, "", authorization, body, cancel);

    /// <summary>The same with the query string (after the '?', may be empty): the overlay and instance routes read it.</summary>
    public async Task<LocalResponse> HandleAsync(string method, string path, string query, string? authorization, ReadOnlyMemory<byte> body, CancellationToken cancel)
    {
        (string Method, TokenBucket Bucket)? route = path switch
        {
            "/local/v1/seat" => ("POST", _seat),
            "/local/v1/friends" => ("GET", _friends),
            "/local/v1/voice" => ("GET", _voice),
            "/local/v1/voice/devices" => ("POST", _voicePost),
            "/local/v1/voice/mute" => ("POST", _voicePost),
            "/local/v1/overlay/open" => ("POST", _overlayOpen),
            "/local/v1/overlay/state" => ("GET", _overlayState),
            "/local/v1/instance/show" => ("POST", _instance),
            "/local/v1/instance/host" => ("POST", _instance),
            _ => null,
        };
        if (route is not { } r)
        {
            return new LocalResponse(404);
        }
        if (method != r.Method)
        {
            return new LocalResponse(405);
        }
        if (!r.Bucket.Allow())
        {
            return new LocalResponse(429, """{"state":"limited"}""", RetryAfter: true);
        }
        if (!Authorized(authorization))
        {
            return new LocalResponse(401, """{"state":"unauthorized"}""");
        }
        return path switch
        {
            "/local/v1/overlay/open" => Overlay.Open(Query(query), body),
            "/local/v1/overlay/state" => await Overlay.StateAsync(Query(query), cancel).ConfigureAwait(false),
            "/local/v1/instance/show" => Instance.Show(),
            "/local/v1/instance/host" => Instance.Host(Query(query), Port, host.Token),
            "/local/v1/seat" => await SeatAsync(cancel).ConfigureAwait(false),
            "/local/v1/friends" => await FriendsAsync(cancel).ConfigureAwait(false),
            "/local/v1/voice" => new LocalResponse(200, VoiceJson(host.Config)),
            "/local/v1/voice/devices" => VoiceDevices(body),
            _ => VoiceMute(body),
        };
    }

    private bool Authorized(string? authorization) =>
        authorization is not null && authorization.StartsWith("Bearer ", StringComparison.Ordinal) && FixedTimeEquals(authorization["Bearer ".Length..], host.Token);

    /// <summary><c>a=1&amp;b=two</c> as a dictionary (percent-decoded; a repeated key keeps its last value).</summary>
    internal static Dictionary<string, string> Query(string query)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                map[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            }
        }
        return map;
    }

    private static bool FixedTimeEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static LocalResponse State(int status, string state, string message) =>
        new(status, JsonSerializer.Serialize(new { state, message }));

    private async Task<LocalResponse> SeatAsync(CancellationToken cancel)
    {
        if (!host.Online)
        {
            // Offline: a refusal (409) ends the game's wait at once and its text is what the mod shows; the game stays offline.
            return State(409, "refused", Strings.OnlineOffInGame);
        }
        if (host.Seat is not { } seat)
        {
            return State(503, "unavailable", Strings.PressPlayInLauncher);
        }
        return await seat.WantAsync(cancel).ConfigureAwait(false) switch
        {
            SeatAnswer.Seated s => new LocalResponse(200, JsonSerializer.Serialize(new { state = "seated", ticket = s.Ticket, lobby = s.Lobby, name = s.Name })),
            SeatAnswer.Queued q => new LocalResponse(200, JsonSerializer.Serialize(new { state = "queued", position = q.Position, seatsTotal = q.SeatsTotal })),
            SeatAnswer.Refused x => State(409, "refused", x.Message),
            SeatAnswer.Unavailable x => State(503, "unavailable", x.Message),
            _ => new LocalResponse(500),
        };
    }

    private async Task<LocalResponse> FriendsAsync(CancellationToken cancel)
    {
        if (host.Seat is null)
        {
            return State(503, "unavailable", Strings.PressPlayInLauncher);
        }
        // The game reads this once per sign-in, so a cold cache must not answer an empty list:
        // give the first friends fetch a moment.
        var asked = clock.GetTimestamp();
        IReadOnlyList<Friend>? all;
        while ((all = host.FriendsOrNull) is null)
        {
            if (clock.GetElapsedTime(asked) >= FriendsWait)
            {
                return State(503, "loading", Strings.FriendsStillLoading);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancel).ConfigureAwait(false);
        }
        // The server already orders them: playing, online, offline; by name.
        var friends = all.Take(MaxFriends).Select(f => new { name = f.Name, online = f.State != FriendState.Offline });
        return new LocalResponse(200, JsonSerializer.Serialize(new { friends }));
    }

    // ---- voice ----------------------------------------------------------------------------

    public static string VoiceJson(ClientConfig c)
    {
        var v = c.Settings.Voice;
        return JsonSerializer.Serialize(new
        {
            enabled = v.Enabled,
            input = v.Input,
            output = v.Output,
            inputGain = Math.Clamp(v.InputGain, 1, 100),
            outputGain = Math.Clamp(v.OutputGain, 1, 100),
            mutes = c.Mutes.Select(m => new { account = m.Account, name = m.Name }),
        });
    }

    internal static string Text(string s) => new([.. s.Where(c => !char.IsControl(c)).Take(MaxText)]);

    /// <summary><c>[{"id","name"}]</c>, deduplicated by id, without empty ids (that is "System default"); null when it is not that.</summary>
    internal static List<AudioDevice>? Devices(JsonElement? v)
    {
        if (v is not { ValueKind: JsonValueKind.Array } array)
        {
            return null;
        }
        var list = new List<AudioDevice>();
        foreach (var d in array.EnumerateArray())
        {
            if (((JsonElement?)d).At("id").Str() is not { } rawId)
            {
                return null;
            }
            var id = Text(rawId);
            if (id.Length == 0 || list.Count == ClientConfig.MaxDevices || list.Any(x => x.Id == id))
            {
                continue;
            }
            var name = ((JsonElement?)d).At("name").Str() ?? "";
            list.Add(new AudioDevice(id, string.IsNullOrWhiteSpace(name) ? id : Text(name)));
        }
        return list;
    }

    private LocalResponse VoiceDevices(ReadOnlyMemory<byte> body)
    {
        if (Json.Parse(body.Span) is not { } v)
        {
            return State(400, "invalid", "not JSON");
        }
        if (Devices(((JsonElement?)v).At("inputs")) is not { } inputs || Devices(((JsonElement?)v).At("outputs")) is not { } outputs)
        {
            return State(400, "invalid", "inputs and outputs: [{id, name}]");
        }
        return host.EditVoice(c => c with { Inputs = inputs, Outputs = outputs }) is { } error ? State(500, "error", error) : new LocalResponse(204);
    }

    /// <summary>Applies one mute or unmute to the list (by account; the newest name wins).</summary>
    internal static IReadOnlyList<MutedPlayer> ApplyMute(IReadOnlyList<MutedPlayer> mutes, string account, string name, bool muted)
    {
        var list = mutes.Where(m => m.Account != account).ToList();
        if (muted)
        {
            list.Add(new MutedPlayer(account, Text(name)));
            if (list.Count > ClientConfig.MaxMutes)
            {
                list.RemoveAt(0);
            }
        }
        return list;
    }

    private LocalResponse VoiceMute(ReadOnlyMemory<byte> body)
    {
        if (Json.Parse(body.Span) is not { } parsed)
        {
            return State(400, "invalid", "not JSON");
        }
        JsonElement? v = parsed;
        // The account id is a u64 in canonical decimal: "05" or a JSON number is another client's bug, not a player.
        var account = v.At("account").Str();
        if (account is null || !ulong.TryParse(account, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || id.ToString(CultureInfo.InvariantCulture) != account || v.At("muted").Bool() is not { } muted)
        {
            return State(400, "invalid", "account (u64 decimal string) and muted (bool)");
        }
        var name = v.At("name").Str() ?? "";
        return host.EditVoice(c => c with { Mutes = ApplyMute(c.Mutes, account, name, muted) }) is { } error ? State(500, "error", error) : new LocalResponse(204);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        _listener?.Dispose();
        Instance.Dispose();
        _stop.Dispose();
    }
}

/// <summary>A token bucket per endpoint (one client: the game on this machine).</summary>
internal sealed class TokenBucket(double burst, double perSecond, TimeProvider clock)
{
    private readonly Lock _gate = new();
    private readonly double _burst = burst;
    private double _tokens = burst;
    private long? _at;

    public bool Allow()
    {
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            if (_at is { } at)
            {
                _tokens = Math.Min(_burst, _tokens + (clock.GetElapsedTime(at, now).TotalSeconds * perSecond));
            }
            _at = now;
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return true;
            }
            return false;
        }
    }
}
