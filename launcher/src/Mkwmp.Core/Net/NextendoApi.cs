// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mkwmp.Core.Net;

internal enum NextendoError
{
    /// <summary>Network trouble, or Nextendo is failing: retry later.</summary>
    Unreachable,

    /// <summary>The sign-in is no longer good (revoked, expired after 30 days unused): link the account again.</summary>
    SignedOut,

    /// <summary>The link lacks a permission the launcher needs (403 from the game token): link again.</summary>
    Scope,

    /// <summary>Too many requests: wait <see cref="NextendoException.RetryAfter"/>.</summary>
    Limited,

    /// <summary>An answer that is not what docs/contracts/NEXTENDO_API.md says.</summary>
    Bad,

    /// <summary>Nextendo said no to this one thing (an unknown friend code, a full friends list): the message says which, for the player.</summary>
    Refused,
}

/// <summary>A failure of a call to Nextendo, with the text the player reads. Never holds a token.</summary>
internal sealed class NextendoException(NextendoError kind, string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public NextendoError Kind { get; } = kind;

    public TimeSpan RetryAfter { get; } = retryAfter ?? TimeSpan.Zero;

    public static NextendoException Unreachable(string detail) => new(NextendoError.Unreachable, detail);

    public static NextendoException Bad(string detail) => new(NextendoError.Bad, detail);
}

/// <summary>What the token endpoint answers: the access token (1 h) and the refresh token that replaces the one used.</summary>
internal sealed record NextendoTokens(string Access, string Refresh, long ExpiresIn, string Scope);

/// <param name="Account">The account's id on the game's servers (<see cref="NextendoApi.NplnAccount"/>); "" while Nextendo does not say it.</param>
internal sealed record NextendoUser(long Pid, string Username, string FriendCode, string DisplayName, string Account = "");

/// <summary>The game token (<c>nx2…</c>, a day) with who it is for.</summary>
/// <param name="Account">The account's id on the game's servers (<see cref="NextendoApi.NplnAccount"/>); "" while Nextendo does not say it.</param>
internal sealed record NextendoGameToken(string Token, long Pid, string FriendCode, string Username, long ExpiresIn, string Account = "");

/// <summary>Where the game connects on Nextendo, and the certificate it trusts there (PEM).</summary>
internal sealed record NextendoNetwork(string ServerHost, string NatHost, string CaPem);

/// <summary><paramref name="Reason"/>: unknown, disabled, suspended, unverified, discord_unlinked or elsewhere; "" when allowed.</summary>
internal sealed record NextendoOnline(bool Allow, string Reason);

/// <summary>PKCE (RFC 7636, S256) and the <c>state</c> of one sign-in.</summary>
internal static class Pkce
{
    /// <summary>43 characters of [A-Za-z0-9-_]: 32 random bytes, base64url without padding.</summary>
    public static string NewVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 challenge of a verifier: base64url(SHA-256(ASCII(verifier))).</summary>
    public static string Challenge(string verifier)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>The value that ties the browser's answer to this sign-in (22 characters, 128 random bits).</summary>
    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>What the browser brought back to <c>/callback</c>.</summary>
internal enum CallbackKind
{
    /// <summary>Not the sign-in's answer (another path, no or a wrong <c>state</c>): ignored, the wait goes on.</summary>
    Ignored,

    /// <summary>The authorization code, for the right <c>state</c>.</summary>
    Code,

    /// <summary>Nextendo (or the player there) refused, for the right <c>state</c>.</summary>
    Denied,
}

internal readonly record struct Callback(CallbackKind Kind, string Value)
{
    /// <summary>
    /// Reads a request target (<c>/callback?code=…&amp;state=…</c>). Only an answer that carries this sign-in's
    /// <paramref name="state"/> counts: anything else on that port is not from the page the launcher opened.
    /// </summary>
    public static Callback Parse(string target, string state)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(state);
        var mark = target.IndexOf('?', StringComparison.Ordinal);
        var path = mark < 0 ? target : target[..mark];
        if (path != NextendoApi.CallbackPath || mark < 0)
        {
            return new Callback(CallbackKind.Ignored, "");
        }
        Dictionary<string, string> query;
        try
        {
            query = LocalApi.Query(target[(mark + 1)..]);
        }
        catch (UriFormatException)
        {
            return new Callback(CallbackKind.Ignored, "");
        }
        if (!query.TryGetValue("state", out var got)
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(state)))
        {
            return new Callback(CallbackKind.Ignored, "");
        }
        if (query.TryGetValue("code", out var code) && code.Length > 0)
        {
            return new Callback(CallbackKind.Code, code);
        }
        return new Callback(CallbackKind.Denied, query.GetValueOrDefault("error") ?? "");
    }
}

/// <summary>
/// Nextendo Network's HTTP API (docs/contracts/NEXTENDO_API.md §1): the OAuth endpoints of a public client (PKCE, no
/// secret), who the account is, the game token, where the game connects, and whether the account may go online.
/// Every call is HTTPS to nextendo.network and nowhere else. A failure is a <see cref="NextendoException"/> whose kind
/// says what the caller does next; cancellation by the caller stays an <see cref="OperationCanceledException"/>.
/// No token is ever logged or put in a message.
/// </summary>
internal sealed class NextendoApi(HttpClient http, string userAgent)
{
    public const string ClientId = "nxc_n38IaT09cMAh";
    public const string Scopes = "identity friends presence game.matchmaking";
    public const string Origin = "https://nextendo.network";
    public const string RegisterUrl = Origin + "/register.html";
    public const string CallbackPath = "/callback";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary><c>http://127.0.0.1:&lt;port&gt;/callback</c>: the loopback address itself, never <c>localhost</c>.</summary>
    public static string RedirectUri(int port) => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{CallbackPath}");

    /// <summary>The page the browser opens to sign in and allow the launcher.</summary>
    public static Uri AuthorizeUrl(string redirectUri, string challenge, string state) => new(
        Origin + "/api/oauth/authorize?response_type=code"
        + "&client_id=" + Uri.EscapeDataString(ClientId)
        + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
        + "&code_challenge=" + Uri.EscapeDataString(challenge)
        + "&code_challenge_method=S256"
        + "&scope=" + Uri.EscapeDataString(Scopes)
        + "&state=" + Uri.EscapeDataString(state));

    private async Task<(HttpStatusCode Status, JsonElement? Body, HttpResponseHeaders Headers)> SendAsync(
        HttpMethod method, string path, string? bearer, IReadOnlyList<KeyValuePair<string, string>>? form, CancellationToken cancel, string? json = null)
    {
        using var req = new HttpRequestMessage(method, Origin + path);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.Accept.ParseAdd("application/json");
        if (bearer is not null)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }
        if (form is not null)
        {
            req.Content = new FormUrlEncodedContent(form);
        }
        else if (json is not null)
        {
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, limit.Token).ConfigureAwait(false);
            var bytes = await res.Content.ReadAsByteArrayAsync(limit.Token).ConfigureAwait(false);
            return (res.StatusCode, Json.Parse(bytes), res.Headers);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw NextendoException.Unreachable(Strings.TimedOut);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            throw NextendoException.Unreachable(e.InnerException?.Message ?? e.Message);
        }
    }

    private static bool Ok(HttpStatusCode s) => (int)s is >= 200 and < 300;

    /// <summary>How long a 429 asks to wait (<c>Retry-After</c> in seconds or as a date); a minute when it does not say.</summary>
    internal static TimeSpan RetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        var wait = headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => TimeSpan.FromSeconds(60),
        };
        return wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
    }

    /// <summary>What a call with a bearer token failed with (the token itself: 401; its permissions: 403; the pace: 429).</summary>
    private static NextendoException Failure(HttpStatusCode status, HttpResponseHeaders headers, bool accessToken)
    {
        switch ((int)status)
        {
            case 401:
                return new NextendoException(NextendoError.SignedOut, Strings.NextendoLinkAgain);
            case 403 when accessToken:
                return new NextendoException(NextendoError.Scope, Strings.NextendoScope);
            case 429:
                var wait = RetryAfter(headers, DateTimeOffset.UtcNow);
                return new NextendoException(NextendoError.Limited, Strings.NextendoLimited((long)Math.Ceiling(wait.TotalSeconds)), wait);
            case >= 500:
                return NextendoException.Unreachable(Strings.ServerAnswered((int)status));
            default:
                return NextendoException.Bad(Strings.ServerAnswered((int)status));
        }
    }

    // ---- OAuth ----------------------------------------------------------------------------

    internal static NextendoTokens? ParseTokens(JsonElement? v, string? keepRefresh = null)
    {
        if (v.At("access_token").Str() is not { Length: > 0 } access)
        {
            return null;
        }
        // The refresh token rotates: the newest one is the one to keep. A server that sends none left the old one good.
        var refresh = v.At("refresh_token").Str() is { Length: > 0 } rotated ? rotated : keepRefresh;
        if (refresh is null)
        {
            return null;
        }
        return new NextendoTokens(access, refresh, Math.Clamp(v.At("expires_in").Long() ?? 3600, 1, 86_400), v.At("scope").Str() ?? "");
    }

    private async Task<NextendoTokens> TokenAsync(IReadOnlyList<KeyValuePair<string, string>> form, string? keepRefresh, CancellationToken cancel)
    {
        var (status, body, headers) = await SendAsync(HttpMethod.Post, "/api/oauth/token", null, form, cancel).ConfigureAwait(false);
        if (Ok(status))
        {
            return ParseTokens(body, keepRefresh) ?? throw NextendoException.Bad(Strings.NextendoBadAnswer);
        }
        // invalid_grant and its kin: the code or the refresh token is not (or no longer) good.
        if ((int)status is 400 or 401 or 403)
        {
            throw new NextendoException(NextendoError.SignedOut, Strings.NextendoLinkAgain);
        }
        throw Failure(status, headers, accessToken: false);
    }

    /// <summary>The code the browser brought back, for tokens (with the verifier of this sign-in).</summary>
    public Task<NextendoTokens> ExchangeAsync(string code, string redirectUri, string verifier, CancellationToken cancel) => TokenAsync(
    [
        new("grant_type", "authorization_code"), new("code", code), new("redirect_uri", redirectUri), new("client_id", ClientId), new("code_verifier", verifier),
    ], null, cancel);

    /// <summary>A new access token. The answer's refresh token replaces <paramref name="refresh"/>: keep the newest.</summary>
    public Task<NextendoTokens> RefreshAsync(string refresh, CancellationToken cancel) => TokenAsync(
    [
        new("grant_type", "refresh_token"), new("refresh_token", refresh), new("client_id", ClientId),
    ], refresh, cancel);

    /// <summary>Ends the sign-in on Nextendo's side (always 200 there). A failure to reach it is the caller's to ignore.</summary>
    public async Task RevokeAsync(string refresh, CancellationToken cancel) =>
        await SendAsync(HttpMethod.Post, "/api/oauth/revoke", null, [new("token", refresh)], cancel).ConfigureAwait(false);

    // ---- the account ----------------------------------------------------------------------

    /// <summary>A number, or a string of digits (ids are written both ways).</summary>
    internal static long? Id(JsonElement? e) =>
        e.Long() ?? (e.Str() is { } text && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null);

    /// <summary>Where an entry may carry <see cref="NplnAccount"/>: <c>nsa</c>, the field every /api/friends entry
    /// already has (Nextendo staff, 2026-11-27); then the names asked of Nextendo and used by its own server.</summary>
    private static readonly string[] AccountKeys = ["nsa", "npln_account_id", "account_hex"];

    /// <summary>
    /// The id an account has on the game's servers (what the game matches a friend by), as 16 lowercase hex digits;
    /// "" when the entry has none. Read from <c>nsa</c> (on every /api/friends entry), else <c>npln_account_id</c>,
    /// else <c>account_hex</c>: a string of 1 to 16 hex digits in either case, and not zero. Anything else is no id.
    /// </summary>
    internal static string NplnAccount(JsonElement? v)
    {
        foreach (var key in AccountKeys)
        {
            if (v.At(key).Str() is { Length: >= 1 and <= 16 } text && text.All(char.IsAsciiHexDigit) && text.Any(c => c != '0'))
            {
                return text.ToLowerInvariant().PadLeft(16, '0');
            }
        }
        return "";
    }

    internal static NextendoUser? ParseUser(JsonElement? v) =>
        Id(v.At("pid")) is { } pid and > 0 && v.At("username").Str() is { Length: > 0 } name
            ? new NextendoUser(pid, name, v.At("friend_code").Str() ?? "", v.At("display_name").Str() ?? name, NplnAccount(v))
            : null;

    internal static NextendoGameToken? ParseGameToken(JsonElement? v) =>
        v.At("nex_token").Str() is { Length: > 0 } token && token.StartsWith("nx2", StringComparison.Ordinal)
        && Id(v.At("pid")) is { } pid and > 0 && v.At("username").Str() is { Length: > 0 } name
            ? new NextendoGameToken(token, pid, v.At("friend_code").Str() ?? "", name, Math.Clamp(v.At("expires_in").Long() ?? 86_400, 1, 7 * 86_400), NplnAccount(v))
            : null;

    internal static NextendoNetwork? ParseNetwork(JsonElement? v) =>
        v.At("server_host").Str() is { } server && HostName(server) && v.At("nat_host").Str() is { } nat && HostName(nat)
        && v.At("ca_pem").Str() is { } pem && pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
        && pem.Contains("-----END CERTIFICATE-----", StringComparison.Ordinal) && pem.Length <= 64 * 1024
            ? new NextendoNetwork(server, nat, pem.ReplaceLineEndings("\n").Trim() + "\n")
            : null;

    internal static NextendoOnline? ParseOnline(JsonElement? v) =>
        v.At("allow").Bool() is { } allow ? new NextendoOnline(allow, allow ? "" : v.At("reason").Str() is { Length: > 0 } why ? why : "unknown") : null;

    /// <summary>A host name as the game's resolver takes it (letters, digits, dots and hyphens).</summary>
    private static bool HostName(string host) =>
        host is { Length: > 0 and <= 253 } && host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-');

    private async Task<T> GetAsync<T>(HttpMethod method, string path, string bearer, bool accessToken, Func<JsonElement?, T?> parse, CancellationToken cancel)
        where T : class
    {
        var (status, body, headers) = await SendAsync(method, path, bearer, null, cancel).ConfigureAwait(false);
        if (!Ok(status))
        {
            throw Failure(status, headers, accessToken);
        }
        return parse(body) ?? throw NextendoException.Bad(Strings.NextendoBadAnswer);
    }

    public Task<NextendoUser> UserInfoAsync(string access, CancellationToken cancel) =>
        GetAsync(HttpMethod.Get, "/api/oauth/userinfo", access, true, ParseUser, cancel);

    /// <summary>A new game token. 403: the link lacks <c>game.matchmaking</c>; 429: wait as asked.</summary>
    public Task<NextendoGameToken> GameTokenAsync(string access, CancellationToken cancel) =>
        GetAsync(HttpMethod.Post, "/api/nex-token", access, true, ParseGameToken, cancel);

    public Task<NextendoNetwork> NetworkAsync(string access, CancellationToken cancel) =>
        GetAsync(HttpMethod.Get, "/api/developers/network-config", access, true, ParseNetwork, cancel);

    /// <summary>Whether the account may go online now. Asked with the game token (<c>nx2…</c>), not the access token.</summary>
    public Task<NextendoOnline> OnlineAsync(string gameToken, CancellationToken cancel) =>
        GetAsync(HttpMethod.Get, "/api/online-status", gameToken, false, ParseOnline, cancel);

    // ---- friends and presence (NextendoFriends.cs; scopes friends and presence) ------------

    /// <summary>A call with the access token and an optional JSON body; <paramref name="refused"/> names what a status means for this call.</summary>
    private async Task<JsonElement?> AskAsync(HttpMethod method, string path, string access, string? json, Func<int, string?>? refused, CancellationToken cancel)
    {
        var (status, body, headers) = await SendAsync(method, path, access, null, cancel, json).ConfigureAwait(false);
        if (Ok(status))
        {
            return body;
        }
        if (refused?.Invoke((int)status) is { } why)
        {
            throw new NextendoException(NextendoError.Refused, why);
        }
        throw Failure(status, headers, accessToken: true);
    }

    private static string PidBody(long pid) => "{\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + "}";

    /// <summary>The account's friends, the requests it got and the ones it sent (without pictures: <c>images=0</c>).</summary>
    public async Task<NextendoFriendList> FriendsAsync(string access, CancellationToken cancel) =>
        NextendoFriendList.Parse(await AskAsync(HttpMethod.Get, "/api/friends?images=0", access, null, null, cancel).ConfigureAwait(false))
        ?? throw NextendoException.Bad(Strings.NextendoBadAnswer);

    /// <summary>
    /// Asks the owner of a friend code to be friends. <c>Already</c>: they are friends now (they had asked first, or
    /// were friends already). Refused: no such code, the player's own, a full list, or a request that cannot be sent.
    /// </summary>
    public async Task<(NextendoPerson Friend, bool Already)> AddFriendAsync(string access, string friendCode, CancellationToken cancel)
    {
        var body = await AskAsync(HttpMethod.Post, "/api/friends", access, JsonSerializer.Serialize(new { friend_code = friendCode }), status => status switch
        {
            404 => Strings.NextendoFriendCodeUnknown,
            400 => Strings.NextendoFriendCodeOwn,
            409 => Strings.NextendoFriendsFull,
            500 => Strings.NextendoFriendNotSent, // one of the two blocked the other
            _ => null,
        }, cancel).ConfigureAwait(false);
        return (NextendoFriendList.Person(body.At("friend")) ?? new NextendoPerson(0, "", friendCode), body.At("already").Bool() ?? false);
    }

    /// <summary><c>accept</c>, <c>decline</c>, <c>remove</c>, <c>block</c> or <c>unblock</c>, for one player.</summary>
    public async Task FriendActionAsync(string access, string verb, long pid, CancellationToken cancel) =>
        await AskAsync(HttpMethod.Post, "/api/friends/" + verb, access, PidBody(pid), status => status == 400 ? Strings.NextendoRequestGone : null, cancel).ConfigureAwait(false);

    public async Task FavoriteAsync(string access, long pid, bool favorite, CancellationToken cancel) =>
        await AskAsync(HttpMethod.Post, "/api/friends/favorite", access, JsonSerializer.Serialize(new { pid, favorite }),
            status => status == 400 ? Strings.NextendoRequestGone : null, cancel).ConfigureAwait(false);

    public async Task<IReadOnlyList<NextendoPerson>> BlockedAsync(string access, CancellationToken cancel) =>
        NextendoFriendList.People(await AskAsync(HttpMethod.Get, "/api/friends/blocked", access, null, null, cancel).ConfigureAwait(false), "blocked")
        ?? throw NextendoException.Bad(Strings.NextendoBadAnswer);

    /// <summary>The players met in online games lately.</summary>
    public async Task<IReadOnlyList<NextendoPerson>> RecentPlayersAsync(string access, CancellationToken cancel) =>
        NextendoFriendList.People(await AskAsync(HttpMethod.Get, "/api/recent-players", access, null, null, cancel).ConfigureAwait(false), "players")
        ?? throw NextendoException.Bad(Strings.NextendoBadAnswer);

    /// <summary>
    /// The players in the lobbies of a game (<see cref="NextendoLive"/>), from the live status; null when the answer does
    /// not say now. An answer that is no live status is <see cref="NextendoError.Bad"/>; a link without <c>friends</c> is <see cref="NextendoError.Scope"/>.
    /// </summary>
    public async Task<int?> LobbyPlayersAsync(string access, string titleId, CancellationToken cancel)
    {
        var body = await AskAsync(HttpMethod.Get, "/api/status/live", access, null, null, cancel).ConfigureAwait(false);
        return body.At("games") is { ValueKind: JsonValueKind.Array } ? NextendoLive.InLobbies(body, titleId) : throw NextendoException.Bad(Strings.NextendoBadAnswer);
    }

    /// <summary>
    /// Says where the player is: 0 offline, 1 online, 2 playing <paramref name="appId"/> (16 hex digits). Nextendo
    /// forgets it after 90 s, so it is said again and again (<see cref="NextendoPresence"/>).
    /// </summary>
    public async Task PresenceAsync(string access, int status, string appId, string detail, CancellationToken cancel) =>
        await AskAsync(HttpMethod.Post, "/api/presence", access,
            JsonSerializer.Serialize(new { status, app_id = appId, app_detail = detail, platform = "app" }), null, cancel).ConfigureAwait(false);
}
