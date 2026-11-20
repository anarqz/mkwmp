// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Mkwmp.Core.Net;

internal enum ApiError
{
    /// <summary>The session is gone (expired, or the server restarted): rejoin.</summary>
    Gone,

    /// <summary>The server refused for a reason the player must fix (wrong PIN, not registered...).</summary>
    Refused,

    /// <summary>REGISTER: the name belongs to someone already.</summary>
    Taken,

    /// <summary>Network or server trouble: retry.</summary>
    Transient,

    /// <summary>The seat server cannot seat this name right now (gamertag in use, queue full): retry in a while.</summary>
    Busy,

    /// <summary>The launcher's presence is gone (server restart, new PIN): sign in again.</summary>
    SignedOut,

    /// <summary>Too many tries: wait <see cref="ApiException.Seconds"/>.</summary>
    Limited,
}

/// <summary>A seat API failure, with the text the player reads (docs/contracts/SEAT_API.md).</summary>
internal sealed class ApiException(ApiError kind, string message, long seconds = 0) : Exception(message)
{
    public ApiError Kind { get; } = kind;
    public long Seconds { get; } = seconds;

    public static ApiException Gone() => new(ApiError.Gone, Strings.SessionExpired);
    public static ApiException Taken() => new(ApiError.Taken, Strings.Taken);
    public static ApiException SignedOut() => new(ApiError.SignedOut, Strings.SignedOut);
    public static ApiException Refused(string message) => new(ApiError.Refused, message);
    public static ApiException Transient(string message) => new(ApiError.Transient, message);
    public static ApiException Busy(string message) => new(ApiError.Busy, message);
    public static ApiException Limited(long seconds) => new(ApiError.Limited, Strings.Limited(seconds), seconds);
}

internal sealed record SeatTicket(string Token, long ExpiresAt, string Lobby);

/// <summary>
/// The lobby room the seat is in, as the lobby last reported it (SEAT_API.md §2, <c>View.room</c>).
/// <paramref name="Ussids"/> is ascending: the first is the mesh host. <paramref name="At"/>: when the lobby said, ms since 1970.
/// </summary>
internal sealed record SeatRoom(string Lobby, long Ussid, IReadOnlyList<long> Ussids, long At);

/// <param name="Room">The seat's lobby room; null when there is none, or from a server older than rooms.</param>
internal sealed record SeatView(string BootId, string SessionId, string Name, string? SessionKey, string State, int? Position, int SeatsTotal, int SeatsFree, long NextPollMs, SeatTicket? Ticket,
    SeatRoom? Room = null);

/// <param name="VoicePort">UDP port of the lobby's voice relay (0 = voice off); servers older than voice leave it out (8907).</param>
/// <param name="PrivacyVersion">The version of the server's privacy policy (SEAT_API.md §2c); null from a server before 1.4.0.</param>
/// <param name="PrivacyEnforced">The server refuses seats, friends and reports until the policy is accepted.</param>
internal sealed record SeatStatus(string Name, int SeatsTotal, int Seated, int Queued, string? LobbyEndpoint, string? LobbyCa, int VoicePort,
    string? PrivacyVersion = null, bool PrivacyEnforced = false);

/// <summary>A sign-in's answer. <paramref name="PrivacyAccepted"/> null: the server does not keep consent (the launcher's own setting decides).</summary>
/// <param name="PrivacyVersion">The server's current policy version.</param>
/// <param name="PrivacyEnforced">The server refuses seats, friends and reports until that version is accepted.</param>
internal sealed record Presence(string Token, bool? PrivacyAccepted, string? PrivacyVersion, bool PrivacyEnforced = false);

internal sealed record FriendList(long Cursor, IReadOnlyList<Friend> Friends, IReadOnlyList<FriendRequest> Incoming, IReadOnlyList<FriendRequest> Outgoing);

internal enum Deleted
{
    Removed,

    /// <summary>The server has no such profile (another server's, or deleted elsewhere).</summary>
    Absent,
}

/// <summary>
/// The seat API client. Every failure becomes an <see cref="ApiException"/> whose kind says what
/// the caller does next (retry, rejoin, sign in again, tell the player); cancellation by the
/// caller stays an <see cref="OperationCanceledException"/>.
/// </summary>
internal sealed class SeatApi(HttpClient http, string baseUrl, string userAgent)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public string Base { get; } = ClientConfig.ServerBase(baseUrl);

    private async Task<(HttpStatusCode Status, JsonElement? Body, HttpResponseHeaders Headers)> SendAsync(
        HttpMethod method, string path, object? json, string? bearer, TimeSpan timeout, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(method, Base + path);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        if (json is not null)
        {
            req.Content = new StringContent(JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(timeout);
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
            var bytes = await res.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            return (res.StatusCode, Json.Parse(bytes), res.Headers);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw ApiException.Transient(Strings.CannotReach(Strings.TimedOut));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            throw ApiException.Transient(Strings.CannotReach(e.InnerException?.Message ?? e.Message));
        }
    }

    private static bool Ok(HttpStatusCode s) => (int)s is >= 200 and < 300;

    private static bool ClientError(HttpStatusCode s) => (int)s is >= 400 and < 500;

    /// <summary>A session answer: the view, or what went wrong.</summary>
    private static SeatView ReadView(HttpStatusCode status, JsonElement? body)
    {
        if (Ok(status))
        {
            return ParseView(body) ?? throw ApiException.Transient(Strings.BadAnswerWhy("not a session"));
        }
        var code = body.At("error").At("code").Str() ?? "";
        // Only a lost session means "rejoin"; a missing profile is the player's to fix.
        if (status == HttpStatusCode.NotFound && (code == "SESSION_NOT_FOUND" || code.Length == 0))
        {
            throw ApiException.Gone();
        }
        switch (code)
        {
            case "NAME_TAKEN": throw ApiException.Busy(Strings.InUse);
            case "QUEUE_FULL": throw ApiException.Busy(Strings.QueueFull);
            default: break;
        }
        var message = code switch
        {
            "PIN_WRONG" => Strings.WrongPin,
            "PROFILE_NOT_FOUND" => Strings.NotRegistered,
            "PRIVACY_REQUIRED" => Strings.NoConsent, // the server holds no consent for the policy it has now
            _ => body.At("error").At("message").Str() ?? Strings.ServerError,
        };
        throw ClientError(status) ? ApiException.Refused(message) : ApiException.Transient(message);
    }

    private static SeatView? ParseView(JsonElement? v)
    {
        if (v.At("bootId").Str() is not { } boot || v.At("sessionId").Str() is not { } sid || v.At("name").Str() is not { } name
            || v.At("state").Str() is not { } state || v.At("seatsTotal").Long() is not { } total || v.At("seatsFree").Long() is not { } free
            || v.At("nextPollMs").Long() is not { } poll)
        {
            return null;
        }
        SeatTicket? ticket = null;
        var t = v.At("ticket");
        if (t is { ValueKind: JsonValueKind.Object })
        {
            if (t.At("token").Str() is not { } token || t.At("lobby").Str() is not { } lobby)
            {
                return null;
            }
            ticket = new SeatTicket(token, t.At("expiresAt").Long() ?? 0, lobby);
        }
        return new SeatView(boot, sid, name, v.At("sessionKey").Str(), state, (int?)v.At("position").Long(), (int)total, (int)free, poll, ticket, ParseRoom(v.At("room")));
    }

    /// <summary>The view's <c>room</c>: absent, null or not a room's shape all mean no room (it only feeds crash records).</summary>
    internal static SeatRoom? ParseRoom(JsonElement? r)
    {
        if (r.At("lobby").Str() is not { Length: > 0 } lobby || r.At("ussid").Long() is not { } ussid || r.At("at").Long() is not { } at
            || r.At("ussids") is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }
        var ussids = new List<long>();
        foreach (var u in list.EnumerateArray())
        {
            if (((JsonElement?)u).Long() is not { } id)
            {
                return null;
            }
            ussids.Add(id);
        }
        return new SeatRoom(lobby, ussid, ussids, at);
    }

    /// <summary>The server's public status.</summary>
    public async Task<SeatStatus> StatusAsync(CancellationToken cancel)
    {
        var (status, v, _) = await SendAsync(HttpMethod.Get, "/seat/v1/status", null, null, Timeout, cancel).ConfigureAwait(false);
        if (!Ok(status))
        {
            throw ApiException.Transient(Strings.ServerAnswered((int)status));
        }
        return ParseStatus(v) ?? throw ApiException.Transient(Strings.NotAnMkwmpServer("no name, seatsTotal, seated and queued"));
    }

    private static SeatStatus? ParseStatus(JsonElement? v) =>
        v.At("name").Str() is not { } name || v.At("seatsTotal").Long() is not { } total || v.At("seated").Long() is not { } seated || v.At("queued").Long() is not { } queued
            ? null
            : new SeatStatus(name, (int)total, (int)seated, (int)queued, v.At("lobbyEndpoint").Str(), v.At("lobbyCa").Str(), (int)(v.At("voicePort").Long() ?? 8907),
                v.At("privacy").At("version").Str(), v.At("privacy").At("enforced").Bool() ?? false);

    /// <summary><c>GET /ping</c> (servers from 1.4.0: 204, empty, unlimited), for the latency measure. False: the server has none (time its status instead).</summary>
    public async Task<bool> PingAsync(TimeSpan timeout, CancellationToken cancel)
    {
        var (status, _, _) = await SendAsync(HttpMethod.Get, "/ping", null, null, timeout, cancel).ConfigureAwait(false);
        return status == HttpStatusCode.NoContent;
    }

    /// <summary>
    /// The status of a server the player is about to choose, telling apart what the status pill does
    /// not need to: nothing answered (<c>Answered</c> false: no network, a wrong address, a server
    /// in trouble), or something answered that is not an MKWMP server (no <c>Status</c>).
    /// </summary>
    public async Task<(SeatStatus? Status, bool Answered, string? Why)> ProbeAsync(CancellationToken cancel)
    {
        HttpStatusCode status;
        JsonElement? v;
        try
        {
            (status, v, _) = await SendAsync(HttpMethod.Get, "/seat/v1/status", null, null, Timeout, cancel).ConfigureAwait(false);
        }
        catch (ApiException e)
        {
            return (null, false, e.Message);
        }
        if ((int)status >= 500)
        {
            return (null, false, Strings.ServerAnswered((int)status)); // a gateway without its server, or a server that is failing
        }
        return Ok(status) && ParseStatus(v) is { } parsed ? (parsed, true, null) : (null, true, Strings.ServerAnswered((int)status));
    }

    /// <summary>Where the game connects: the lobby, the certificate the game should trust for it, the voice relay's UDP port.</summary>
    public async Task<(string Lobby, string? LobbyCa, int VoicePort)> LobbyAsync(CancellationToken cancel)
    {
        var s = await StatusAsync(cancel).ConfigureAwait(false);
        return (s.LobbyEndpoint ?? throw ApiException.Transient(Strings.ServerTooOld), s.LobbyCa, s.VoicePort);
    }

    public async Task<SeatView> JoinAsync(string name, string pin, CancellationToken cancel)
    {
        var (status, body, _) = await SendAsync(HttpMethod.Post, "/seat/v1/sessions", new { name, pin }, null, Timeout, cancel).ConfigureAwait(false);
        return ReadView(status, body);
    }

    public async Task<SeatView> PollAsync(string sessionId, string key, CancellationToken cancel)
    {
        var (status, body, _) = await SendAsync(HttpMethod.Get, $"/seat/v1/sessions/{Uri.EscapeDataString(sessionId)}", null, key, Timeout, cancel).ConfigureAwait(false);
        return ReadView(status, body);
    }

    /// <summary>Frees the seat. Best effort: the server's heartbeat timeout does it otherwise.</summary>
    public async Task LeaveAsync(string sessionId, string key)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, $"/seat/v1/sessions/{Uri.EscapeDataString(sessionId)}", null, key, Timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // unreachable: the seat expires by itself
        }
    }

    /// <summary>Is <paramref name="name"/> registered (or reserved)?</summary>
    public async Task<(bool Registered, bool Reserved)> ProfileAsync(string name, CancellationToken cancel)
    {
        var (_, v, _) = await SendAsync(HttpMethod.Get, $"/seat/v1/profiles/{Uri.EscapeDataString(name)}", null, null, Timeout, cancel).ConfigureAwait(false);
        if (v is null)
        {
            throw ApiException.Transient(Strings.BadAnswer);
        }
        return (v.At("registered").Bool() ?? false, v.At("reserved").Bool() ?? false);
    }

    /// <summary>
    /// Deletes the profile (needs its PIN); the name becomes free. A profile the server does not
    /// have is as good as deleted; a wrong PIN and server trouble are errors.
    /// </summary>
    public async Task<Deleted> DeleteProfileAsync(string name, string pin, CancellationToken cancel)
    {
        var (status, v, _) = await SendAsync(HttpMethod.Delete, $"/seat/v1/profiles/{Uri.EscapeDataString(name)}", new { pin }, null, Timeout, cancel).ConfigureAwait(false);
        if (Ok(status))
        {
            return Deleted.Removed;
        }
        var code = v.At("error").At("code").Str() ?? "";
        var message = v.At("error").At("message").Str() ?? Strings.CouldNotDelete;
        return code switch
        {
            "PROFILE_NOT_FOUND" => Deleted.Absent,
            "PIN_WRONG" => throw ApiException.Refused(Strings.WrongPin),
            _ when status == HttpStatusCode.NotFound => Deleted.Absent,
            _ when status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => throw ApiException.Refused(Strings.WrongPin),
            _ when status == HttpStatusCode.TooManyRequests => throw ApiException.Transient(message),
            _ when ClientError(status) => throw ApiException.Refused(message),
            _ => throw ApiException.Transient(message),
        };
    }

    /// <summary>Registers <paramref name="name"/>; returns its PIN.</summary>
    public Task<string> RegisterAsync(string name, CancellationToken cancel) => RegisterWithAsync(name, null, cancel);

    /// <summary>
    /// Registers <paramref name="name"/> again with the PIN this launcher already has (the server
    /// deleted the profile after a month unused). Returns the PIN the server registered: a server
    /// older than this ignores <paramref name="pin"/> and picks one.
    /// </summary>
    public Task<string> RestoreAsync(string name, string pin, CancellationToken cancel) => RegisterWithAsync(name, pin, cancel);

    private async Task<string> RegisterWithAsync(string name, string? pin, CancellationToken cancel)
    {
        object body = pin is null ? new { name } : new { name, pin };
        var (status, v, _) = await SendAsync(HttpMethod.Post, "/seat/v1/profiles", body, null, Timeout, cancel).ConfigureAwait(false);
        if (Ok(status) && v.At("pin").Str() is { } got)
        {
            return got;
        }
        // A reserved gamertag is also someone's (the admin registered it for them): ask for its
        // PIN the same way. A reserved one never registered fails at sign-in.
        if (v.At("error").At("code").Str() is "NAME_REGISTERED" or "NAME_RESERVED")
        {
            throw ApiException.Taken();
        }
        throw ApiException.Refused(v.At("error").At("message").Str() ?? Strings.CouldNotRegister);
    }

    // ---- launcher presence and friends (no seat) ------------------------------------------

    /// <summary>A friends-API answer: JSON on success, else a friendly error.</summary>
    private async Task<JsonElement?> FriendlyAsync(HttpMethod method, string path, object? json, string? bearer, TimeSpan timeout, CancellationToken cancel)
    {
        var (status, v, headers) = await SendAsync(method, path, json, bearer, timeout, cancel).ConfigureAwait(false);
        if (status == HttpStatusCode.TooManyRequests)
        {
            var seconds = headers.TryGetValues("Retry-After", out var values) && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 60;
            throw ApiException.Limited(seconds);
        }
        if (status == HttpStatusCode.NoContent)
        {
            return null;
        }
        if (Ok(status))
        {
            return v;
        }
        var message = v.At("error").At("message").Str() ?? Strings.ServerError;
        throw (v.At("error").At("code").Str() ?? "") switch
        {
            "PRESENCE_NOT_FOUND" => ApiException.SignedOut(),
            "PIN_WRONG" => ApiException.Refused(Strings.WrongPin),
            "PROFILE_NOT_FOUND" => ApiException.Refused(Strings.NotRegistered),
            "PRIVACY_REQUIRED" => ApiException.Refused(Strings.NoConsent),
            _ when ClientError(status) => ApiException.Refused(message),
            _ => ApiException.Transient(message),
        };
    }

    /// <summary>
    /// Signs the launcher in (no seat): the presence token, and what the server holds of the
    /// player's consent (<c>privacyAccepted</c>, <c>privacyVersion</c>; both absent from a server
    /// older than server-side consent, where the launcher's own <c>adult_consent</c> decides).
    /// </summary>
    public async Task<Presence> PresenceAsync(string name, string pin, CancellationToken cancel)
    {
        var v = await FriendlyAsync(HttpMethod.Post, "/seat/v1/presence", new { name, pin }, null, Timeout, cancel).ConfigureAwait(false);
        return new Presence(v.At("token").Str() ?? throw ApiException.Transient(Strings.BadAnswer), v.At("privacyAccepted").Bool(), v.At("privacyVersion").Str(),
            v.At("privacyEnforced").Bool() ?? false);
    }

    /// <summary>The largest privacy policy the launcher reads.</summary>
    public const int MaxPrivacyBytes = 256 * 1024;

    /// <summary>
    /// The server's own privacy policy (<c>GET /privacy</c>, Markdown, no sign-in), or null when the
    /// server has none to give (older than it, or anything that is not Markdown text): the caller
    /// then shows the project's policy link. Network trouble is an <see cref="ApiException"/>.
    /// </summary>
    public async Task<(string Markdown, string Version)?> PrivacyTextAsync(CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Base + "/privacy");
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "text/markdown");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!Ok(res.StatusCode) || !(type.Equals("text/markdown", StringComparison.OrdinalIgnoreCase) || type.Equals("text/plain", StringComparison.OrdinalIgnoreCase))
                || res.Content.Headers.ContentLength is > MaxPrivacyBytes)
            {
                return null;
            }
            await using var body = await res.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            var bytes = new byte[MaxPrivacyBytes + 1];
            var read = 0;
            for (int n; read < bytes.Length && (n = await body.ReadAsync(bytes.AsMemory(read), limit.Token).ConfigureAwait(false)) > 0;)
            {
                read += n;
            }
            if (read is 0 or > MaxPrivacyBytes)
            {
                return null;
            }
            // The version is the ETag (quoted, as HTTP wants): the lower-case hex SHA-256 of these bytes, which is what
            // stands in when a proxy dropped the header.
            var tag = res.Headers.ETag?.Tag.Trim('"') ?? "";
            var version = tag.Length == 64 && tag.All(char.IsAsciiHexDigitLower)
                ? tag
                : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan(0, read)));
            return (new UTF8Encoding(false).GetString(bytes, 0, read), version);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw ApiException.Transient(Strings.CannotReach(Strings.TimedOut));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or UriFormatException)
        {
            throw ApiException.Transient(Strings.CannotReach(e.InnerException?.Message ?? e.Message));
        }
    }

    /// <summary>
    /// Stores the player's consent on the server (<c>PUT /seat/v1/privacy</c>): accepted, with the
    /// version (sha256) of the policy the launcher showed, or withdrawn. False: the server has no
    /// such route (older than server-side consent), so the launcher's own setting is all there is.
    /// </summary>
    public async Task<bool> PrivacyPutAsync(string token, bool accepted, string? version, CancellationToken cancel)
    {
        object body = accepted ? new { accepted, version = version ?? "" } : new { accepted };
        var (status, v, _) = await SendAsync(HttpMethod.Put, "/seat/v1/privacy", body, token, Timeout, cancel).ConfigureAwait(false);
        if (Ok(status))
        {
            return true;
        }
        var code = v.At("error").At("code").Str() ?? "";
        if (status is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed && code.Length == 0)
        {
            return false;
        }
        throw code switch
        {
            "PRESENCE_NOT_FOUND" => ApiException.SignedOut(),
            "PRIVACY_VERSION" => ApiException.Refused(Strings.PrivacyChanged), // the policy changed since it was shown: read it again
            _ when status == HttpStatusCode.TooManyRequests => ApiException.Transient(Strings.ServerAnswered(429)),
            _ when ClientError(status) => ApiException.Refused(v.At("error").At("message").Str() ?? Strings.ServerError),
            _ => ApiException.Transient(v.At("error").At("message").Str() ?? Strings.ServerAnswered((int)status)),
        };
    }

    /// <summary>The launcher quits: friends see it offline at once. Best effort, short.</summary>
    public async Task PresenceEndAsync(string token)
    {
        try
        {
            await SendAsync(HttpMethod.Delete, "/seat/v1/presence", null, token, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // the server forgets us after its own timeout
        }
    }

    /// <summary>Friends and requests. With <paramref name="since"/> (the last cursor) the server waits up to 25 s for a change.</summary>
    public async Task<FriendList> FriendsAsync(string token, long? since, CancellationToken cancel)
    {
        var path = since is { } c ? $"/seat/v1/friends?since={c.ToString(CultureInfo.InvariantCulture)}" : "/seat/v1/friends";
        var v = await FriendlyAsync(HttpMethod.Get, path, null, token, TimeSpan.FromSeconds(40), cancel).ConfigureAwait(false);
        if (v.At("cursor").Long() is not { } cursor || v.At("friends") is not { ValueKind: JsonValueKind.Array } friends
            || v.At("incoming") is not { ValueKind: JsonValueKind.Array } incoming || v.At("outgoing") is not { ValueKind: JsonValueKind.Array } outgoing)
        {
            throw ApiException.Transient(Strings.BadAnswerWhy("not a friends list"));
        }
        static FriendRequest Request(JsonElement r) => new(((JsonElement?)r).At("name").Str() ?? "", DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(((JsonElement?)r).At("since").Long() ?? 0, 0, 253_402_300_799)));
        return new FriendList(
            cursor,
            [.. friends.EnumerateArray().Select(f => new Friend(((JsonElement?)f).At("name").Str() ?? "", ((JsonElement?)f).At("state").Str() switch
            {
                "playing" => FriendState.Playing,
                "online" => FriendState.Online,
                _ => FriendState.Offline,
            }))],
            [.. incoming.EnumerateArray().Select(Request)],
            [.. outgoing.EnumerateArray().Select(Request)]);
    }

    /// <summary>Asks <paramref name="name"/> to be friends; "pending" or "accepted" (they had asked).</summary>
    public async Task<(string Name, string State)> FriendRequestAsync(string token, string name, CancellationToken cancel)
    {
        var v = await FriendlyAsync(HttpMethod.Post, "/seat/v1/friends", new { name }, token, Timeout, cancel).ConfigureAwait(false);
        return (v.At("name").Str() ?? name, v.At("state").Str() ?? "pending");
    }

    public Task FriendAcceptAsync(string token, string name, CancellationToken cancel) =>
        FriendlyAsync(HttpMethod.Post, $"/seat/v1/friends/{Uri.EscapeDataString(name)}/accept", null, token, Timeout, cancel);

    /// <summary>Reject, cancel or unfriend.</summary>
    public Task FriendRemoveAsync(string token, string name, CancellationToken cancel) =>
        FriendlyAsync(HttpMethod.Delete, $"/seat/v1/friends/{Uri.EscapeDataString(name)}", null, token, Timeout, cancel);

    /// <summary>This profile's report waiting for review, if any.</summary>
    public async Task<OpenReport?> ReportAsync(string token, CancellationToken cancel) =>
        ParseReport(await FriendlyAsync(HttpMethod.Get, "/seat/v1/reports", null, token, Timeout, cancel).ConfigureAwait(false), required: false);

    /// <summary>Reports <paramref name="name"/>'s voice chat (one report waiting at a time).</summary>
    public async Task<OpenReport> ReportFileAsync(string token, string name, string reason, string details, CancellationToken cancel) =>
        ParseReport(await FriendlyAsync(HttpMethod.Post, "/seat/v1/reports", new { name, reason, details }, token, Timeout, cancel).ConfigureAwait(false), required: true)!;

    /// <summary>Takes back the report waiting for review.</summary>
    public Task ReportWithdrawAsync(string token, CancellationToken cancel) =>
        FriendlyAsync(HttpMethod.Delete, "/seat/v1/reports", null, token, Timeout, cancel);

    private static OpenReport? ParseReport(JsonElement? v, bool required)
    {
        var r = v.At("report");
        if (r is null or { ValueKind: JsonValueKind.Null })
        {
            return required ? throw ApiException.Transient(Strings.BadAnswerWhy("no report")) : null;
        }
        if (r.At("reported").Str() is not { } reported || r.At("reason").Str() is not { } reason || r.At("created").Long() is not { } created)
        {
            throw ApiException.Transient(Strings.BadAnswerWhy("not a report"));
        }
        var details = r.At("details").Str();
        return new OpenReport(reported, reason, string.IsNullOrEmpty(details) ? null : details, DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(created, 0, 253_402_300_799)));
    }
}

/// <summary>Reading JSON whose shape is not trusted: every step answers null instead of throwing.</summary>
internal static class Json
{
    public static JsonElement? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty)
        {
            return null;
        }
        try
        {
            var reader = new Utf8JsonReader(utf8);
            return JsonElement.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonElement? Parse(string text) => Parse(Encoding.UTF8.GetBytes(text));

    public static JsonElement? At(this JsonElement? e, string key) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(key, out var v) ? v : null;

    public static string? Str(this JsonElement? e) => e is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    public static bool? Bool(this JsonElement? e) => e switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    public static long? Long(this JsonElement? e) => e is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out var v) ? v : null;
}
