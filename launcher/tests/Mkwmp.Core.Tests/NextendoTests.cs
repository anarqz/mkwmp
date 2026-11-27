// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>Nextendo Network inside the test (docs/contracts/NEXTENDO_API.md §1): no socket, no real server, no browser.</summary>
internal sealed class FakeNextendo : HttpMessageHandler
{
    public const string CaPem = "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n";

    private readonly Lock _gate = new();
    private int _refreshes;
    private int _grants;

    public List<string> Requests { get; } = [];

    /// <summary>The form of every POST to the token endpoint, and of the revoke.</summary>
    public List<Dictionary<string, string>> Forms { get; } = [];

    /// <summary>The bearer of every call that had one, by path.</summary>
    public List<(string Path, string Bearer)> Bearers { get; } = [];

    /// <summary>The challenge of the sign-in the "browser" was sent to: the token endpoint checks the verifier against it.</summary>
    public string? Challenge { get; set; }

    public string Username { get; set; } = "bia";

    /// <summary><c>online-status</c>: "" allows, anything else is the reason.</summary>
    public string Refuse { get; set; } = "";

    /// <summary>The game token answers 429 with this <c>Retry-After</c> (0: it answers a token).</summary>
    public int LimitSeconds { get; set; }

    /// <summary>The game token answers 403 (the link lacks game.matchmaking).</summary>
    public bool NoScope { get; set; }

    public bool Unreachable { get; set; }

    /// <summary>The refresh token is no longer good.</summary>
    public bool Revoked { get; set; }

    public int GameTokenSeconds { get; set; } = 86_400;

    /// <summary><c>npln_account_id</c> of <c>userinfo</c> (null: not sent, as Nextendo does today).</summary>
    public string? UserAccount { get; set; }

    /// <summary>
    /// What answers the calls this fake does not know itself (the friends list, presence: NextendoFriendsTests.cs), once
    /// their access token was found good: the method and the path with its query, then the body. Null: 404.
    /// </summary>
    public Func<string, string, HttpResponseMessage?>? More { get; set; }

    /// <summary>
    /// <c>GET /api/avatar?pid=…</c>, the public picture route (NextendoAvatarsTests.cs): what answers for that pid
    /// (null: 404, as when nothing is set). It is asked without a sign-in: a call that carries one fails the test.
    /// </summary>
    public Func<long, HttpResponseMessage?>? Avatar { get; set; }

    /// <summary>The pid of every picture asked for, in order.</summary>
    public List<long> AvatarsAsked { get; } = [];

    public int Grants => _grants;

    public int Count(string request)
    {
        lock (_gate)
        {
            return Requests.Count(r => r == request);
        }
    }

    private static HttpResponseMessage Json(int status, object body) =>
        new((HttpStatusCode)status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Assert.Equal(("https", "nextendo.network"), (uri.Scheme, uri.Host)); // every call is HTTPS to nextendo.network only
        if (Unreachable)
        {
            throw new HttpRequestException("no route to nextendo (the fake is unplugged)");
        }
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var form = HttpUtility.ParseQueryString(body);
        var bearer = request.Headers.Authorization?.Parameter;
        lock (_gate)
        {
            var path = uri.AbsolutePath;
            Requests.Add($"{request.Method.Method} {path}");
            if (bearer is not null)
            {
                Bearers.Add((path, bearer));
            }
            switch (request.Method.Method, path)
            {
                case ("POST", "/api/oauth/token"):
                    Forms.Add(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
                    Assert.Equal(NextendoApi.ClientId, form["client_id"]);
                    if (form["grant_type"] == "authorization_code")
                    {
                        if (form["code"] != "code-1" || Pkce.Challenge(form["code_verifier"] ?? "") != Challenge)
                        {
                            return Json(400, new { error = "invalid_grant" });
                        }
                        return Json(200, new { access_token = "access-0", refresh_token = "refresh-0", expires_in = 3600, scope = NextendoApi.Scopes });
                    }
                    if (Revoked || form["refresh_token"] != $"refresh-{_refreshes}")
                    {
                        return Json(400, new { error = "invalid_grant" }); // a rotated-out refresh token is dead
                    }
                    _refreshes++;
                    return Json(200, new { access_token = $"access-{_refreshes}", refresh_token = $"refresh-{_refreshes}", expires_in = 3600, scope = NextendoApi.Scopes });
                case ("POST", "/api/oauth/revoke"):
                    Forms.Add(form.AllKeys.ToDictionary(k => k!, k => form[k]!));
                    Revoked = true;
                    return Json(200, new { });
                case ("GET", "/api/oauth/userinfo"):
                    return bearer == $"access-{_refreshes}"
                        ? UserAccount is { } account
                            ? Json(200, new { pid = 1234567, username = Username, friend_code = "SW-1111-2222-3333", display_name = "Bia", npln_account_id = account, npln_user_id = "u-1" })
                            : Json(200, new { pid = 1234567, username = Username, friend_code = "SW-1111-2222-3333", display_name = "Bia" })
                        : Json(401, new { error = "invalid_token" });
                case ("POST", "/api/nex-token"):
                    if (bearer != $"access-{_refreshes}")
                    {
                        return Json(401, new { error = "invalid_token" });
                    }
                    if (NoScope)
                    {
                        return Json(403, new { error = "insufficient_scope" });
                    }
                    if (LimitSeconds > 0)
                    {
                        var limited = Json(429, new { error = "rate_limited" });
                        limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(LimitSeconds));
                        return limited;
                    }
                    _grants++;
                    return Json(200, new { nex_token = $"nx2-game-token-{_grants}", pid = 1234567, friend_code = "SW-1111-2222-3333", username = Username, expires_in = GameTokenSeconds });
                case ("GET", "/api/developers/network-config"):
                    return Json(200, new { server_host = "nex.nextendo.network", nat_host = "nat.nextendo.network", npln = new { }, ca_pem = CaPem });
                case ("GET", "/api/online-status"):
                    Assert.StartsWith("nx2", bearer, StringComparison.Ordinal); // asked with the game token, never the access token
                    return Json(200, Refuse.Length == 0 ? new { allow = true, reason = "" } : new { allow = false, reason = Refuse });
                case ("GET", "/api/avatar"):
                    Assert.Null(request.Headers.Authorization); // a public route: no token ever goes with a picture
                    var who = long.Parse(HttpUtility.ParseQueryString(uri.Query)["pid"]!, System.Globalization.CultureInfo.InvariantCulture);
                    AvatarsAsked.Add(who);
                    return Avatar?.Invoke(who) ?? new HttpResponseMessage(HttpStatusCode.NotFound);
                default:
                    if (More is null)
                    {
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    }
                    return bearer == $"access-{_refreshes}"
                        ? More($"{request.Method.Method} {uri.PathAndQuery}", body) ?? new HttpResponseMessage(HttpStatusCode.NotFound)
                        : Json(401, new { error = "invalid_token" });
            }
        }
    }
}

/// <summary>One handler for the launcher: nextendo.network goes to the fake Nextendo, everything else to the fake seat server.</summary>
internal sealed class TwoNetworks(FakeSeatServer seat, FakeNextendo nextendo) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _seat = new(seat, disposeHandler: false);
    private readonly HttpMessageInvoker _nextendo = new(nextendo, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        (request.RequestUri!.Host == "nextendo.network" ? _nextendo : _seat).SendAsync(request, cancellationToken);
}

/// <summary>A clock the test moves.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// Playing on Nextendo Network (docs/contracts/NEXTENDO_API.md): the sign-in (PKCE, state, the answer on 127.0.0.1),
/// the tokens kept current, what client.toml and mkwmp.toml hold, PLAY without any MKWMP server, and the game's route.
/// </summary>
public sealed class NextendoTests
{
    private static string Scratch(string name) => Path.Combine(Rig.NewFolder("nextendo"), name);

    // ---- PKCE and state -------------------------------------------------------------------

    [Fact]
    public void Pkce_makes_a_fresh_verifier_and_its_S256_challenge()
    {
        var verifier = Pkce.NewVerifier();
        Assert.Equal(43, verifier.Length);
        Assert.All(verifier, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.NotEqual(verifier, Pkce.NewVerifier());
        // RFC 7636, appendix B
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Assert.Equal(Pkce.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), Pkce.Challenge(verifier));
        Assert.DoesNotContain('=', Pkce.Challenge(verifier));
        var state = Pkce.NewState();
        Assert.Equal(22, state.Length);
        Assert.NotEqual(state, Pkce.NewState());
    }

    [Fact]
    public void The_sign_in_page_is_nextendo_s_with_the_contract_s_parameters()
    {
        var url = NextendoApi.AuthorizeUrl(NextendoApi.RedirectUri(51234), "chal", "st8");
        Assert.Equal("https://nextendo.network/api/oauth/authorize", url.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("nxc_n38IaT09cMAh", query["client_id"]);
        Assert.Equal("http://127.0.0.1:51234/callback", query["redirect_uri"]); // the loopback address, never "localhost"
        Assert.Equal(("chal", "S256", "st8"), (query["code_challenge"], query["code_challenge_method"], query["state"]));
        Assert.Equal("identity friends presence game.matchmaking", query["scope"]);
        Assert.Equal(7, query.Count);
    }

    [Theory]
    [InlineData("/callback?code=abc&state=good", "Code", "abc")]
    [InlineData("/callback?state=good&code=a%2Fb", "Code", "a/b")]
    [InlineData("/callback?error=access_denied&state=good", "Denied", "access_denied")]
    [InlineData("/callback?state=good", "Denied", "")]
    [InlineData("/callback?code=abc&state=bad", "Ignored", "")]
    [InlineData("/callback?code=abc&state=goo", "Ignored", "")]
    [InlineData("/callback?code=abc", "Ignored", "")]
    [InlineData("/callback", "Ignored", "")]
    [InlineData("/other?code=abc&state=good", "Ignored", "")]
    [InlineData("/callback/x?code=abc&state=good", "Ignored", "")]
    [InlineData("/favicon.ico", "Ignored", "")]
    public void Only_an_answer_with_this_sign_in_s_state_counts(string target, string kind, string value)
    {
        Assert.Equal(new Callback(Enum.Parse<CallbackKind>(kind), value), Callback.Parse(target, "good"));
    }

    [Fact]
    public async Task The_loopback_takes_the_right_answer_and_nothing_else()
    {
        using var http = new HttpClient();
        using var loopback = new NextendoLoopback();
        Assert.Equal($"http://127.0.0.1:{loopback.Port}/callback", loopback.RedirectUri);
        var wait = loopback.WaitAsync("st8", TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"http://127.0.0.1:{loopback.Port}/")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"{loopback.RedirectUri}?code=stolen&state=guess")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await http.PostAsync($"{loopback.RedirectUri}?code=x&state=st8", null)).StatusCode);
        Assert.False(wait.IsCompleted);
        var page = await http.GetAsync($"{loopback.RedirectUri}?code=the-code&state=st8");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains(Strings.NextendoBrowserDoneTitle, html, StringComparison.Ordinal);
        Assert.DoesNotContain("the-code", html, StringComparison.Ordinal); // nothing of the request is echoed
        Assert.Equal(new Callback(CallbackKind.Code, "the-code"), await wait);
    }

    [Fact]
    public async Task The_loopback_gives_up_after_its_patience_and_when_cancelled()
    {
        using (var loopback = new NextendoLoopback())
        {
            await Assert.ThrowsAsync<TimeoutException>(() => loopback.WaitAsync("st8", TimeSpan.FromMilliseconds(150), CancellationToken.None));
        }
        using var second = new NextendoLoopback();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.WaitAsync("st8", TimeSpan.FromSeconds(20), cancel.Token));
    }

    // ---- JSON -----------------------------------------------------------------------------

    [Fact]
    public void Nextendo_s_answers_are_read_as_the_contract_writes_them()
    {
        var tokens = NextendoApi.ParseTokens(Json.Parse("""{"access_token":"a","refresh_token":"r2","expires_in":3600,"scope":"identity"}"""), keepRefresh: "r1")!;
        Assert.Equal(new NextendoTokens("a", "r2", 3600, "identity"), tokens); // the newest refresh token is the one kept
        Assert.Equal("r1", NextendoApi.ParseTokens(Json.Parse("""{"access_token":"a"}"""), keepRefresh: "r1")!.Refresh);
        Assert.Null(NextendoApi.ParseTokens(Json.Parse("""{"access_token":"a"}""")));
        Assert.Null(NextendoApi.ParseTokens(Json.Parse("""{"refresh_token":"r"}"""), "r1"));
        Assert.Null(NextendoApi.ParseTokens(Json.Parse("not json")));

        Assert.Equal(new NextendoUser(42, "bia", "SW-1", "Bia B"), NextendoApi.ParseUser(Json.Parse("""{"pid":42,"username":"bia","friend_code":"SW-1","display_name":"Bia B"}""")));
        Assert.Equal(new NextendoUser(42, "bia", "", "bia"), NextendoApi.ParseUser(Json.Parse("""{"pid":"42","username":"bia"}""")));
        Assert.Null(NextendoApi.ParseUser(Json.Parse("""{"pid":0,"username":"bia"}""")));
        Assert.Null(NextendoApi.ParseUser(Json.Parse("""{"pid":42}""")));

        Assert.Equal(new NextendoGameToken("nx2abc", 42, "SW-1", "bia", 86400),
            NextendoApi.ParseGameToken(Json.Parse("""{"nex_token":"nx2abc","pid":42,"friend_code":"SW-1","username":"bia","expires_in":86400}""")));
        Assert.Null(NextendoApi.ParseGameToken(Json.Parse("""{"nex_token":"abc","pid":42,"username":"bia"}"""))); // not a game token
        Assert.Null(NextendoApi.ParseGameToken(Json.Parse("""{"pid":42,"username":"bia"}""")));

        // the account's id on the game's servers: optional (Nextendo does not send it yet), under either name
        Assert.Equal(new NextendoUser(42, "bia", "", "bia", "00ab12cd34ef5601"),
            NextendoApi.ParseUser(Json.Parse("""{"pid":42,"username":"bia","npln_account_id":"00AB12cd34EF5601","npln_user_id":"u-1"}""")));
        Assert.Equal(new NextendoGameToken("nx2abc", 42, "", "bia", 86400, "000000000000f00d"),
            NextendoApi.ParseGameToken(Json.Parse("""{"nex_token":"nx2abc","pid":42,"username":"bia","account_hex":"F00D"}""")));
        Assert.Equal("", NextendoApi.ParseUser(Json.Parse("""{"pid":42,"username":"bia","npln_account_id":"not hex"}"""))!.Account);

        var pem ="-----BEGIN CERTIFICATE-----\r\nAAAA\r\n-----END CERTIFICATE-----";
        var network = NextendoApi.ParseNetwork(Json.Parse(JsonSerializer.Serialize(new { server_host = "nex.nextendo.network", nat_host = "nat.nextendo.network", npln = new { x = 1 }, ca_pem = pem })))!;
        Assert.Equal(("nex.nextendo.network", "nat.nextendo.network", "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"), (network.ServerHost, network.NatHost, network.CaPem));
        Assert.Null(NextendoApi.ParseNetwork(Json.Parse("""{"server_host":"a.b","nat_host":"c.d","ca_pem":"not a certificate"}""")));
        Assert.Null(NextendoApi.ParseNetwork(Json.Parse(JsonSerializer.Serialize(new { server_host = "a b\"", nat_host = "c.d", ca_pem = pem }))));

        Assert.Equal(new NextendoOnline(true, ""), NextendoApi.ParseOnline(Json.Parse("""{"allow":true,"reason":"unknown"}""")));
        Assert.Equal(new NextendoOnline(false, "unverified"), NextendoApi.ParseOnline(Json.Parse("""{"allow":false,"reason":"unverified"}""")));
        Assert.Equal(new NextendoOnline(false, "unknown"), NextendoApi.ParseOnline(Json.Parse("""{"allow":false}""")));
        Assert.Null(NextendoApi.ParseOnline(Json.Parse("""{"reason":"x"}""")));
    }

    [Fact]
    public void Every_reason_of_online_status_has_its_own_text()
    {
        string[] reasons = ["unknown", "disabled", "suspended", "unverified", "discord_unlinked", "elsewhere"];
        var texts = reasons.Select(Strings.NextendoRefused).ToArray();
        Assert.Equal(reasons.Length, texts.Distinct().Count());
        Assert.All(texts, t => Assert.DoesNotContain("core.", t, StringComparison.Ordinal));
        Assert.Equal(Strings.NextendoRefused("unknown"), Strings.NextendoRefused("something new"));
    }

    // ---- the tokens, kept current ---------------------------------------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData(3601, false)]
    [InlineData(3600, false)]
    [InlineData(3599, true)]
    [InlineData(0, true)]
    [InlineData(-5, true)]
    public void A_game_token_with_less_than_an_hour_left_is_stale(int? secondsLeft, bool stale)
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(stale, NextendoAccount.Stale(now, secondsLeft is { } left ? now.AddSeconds(left) : null, NextendoAccount.RenewGameTokenBefore));
    }

    private static (NextendoAccount Account, FakeNextendo Server, ManualClock Clock, NextendoVault Vault, HttpClient Http) Linked()
    {
        var server = new FakeNextendo();
        var clock = new ManualClock();
        var vault = new NextendoVault(Scratch(NextendoVault.FileName));
        Assert.True(vault.Save("refresh-0"));
        var http = new HttpClient(server);
        return (new NextendoAccount(new NextendoApi(http, "test"), vault, clock), server, clock, vault, http);
    }

    [Fact]
    public async Task The_game_token_is_cached_and_renewed_when_less_than_an_hour_is_left()
    {
        var (account, server, clock, vault, http) = Linked();
        using var a = account;
        using var h = http;
        var first = await account.GameTokenAsync(CancellationToken.None);
        Assert.Equal(("nx2-game-token-1", clock.GetUtcNow().AddDays(1)), (first.Token.Token, first.ExpiresAt));
        // the access token came from the kept refresh token, and the one it rotated to is on disk
        Assert.Equal("refresh-1", vault.Load());
        Assert.Equal(1, server.Count("POST /api/oauth/token"));

        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Same(first, await account.GameTokenAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(22)); // 22 h 30 min in: 1 h 30 min left
        Assert.Same(first, await account.GameTokenAsync(CancellationToken.None));
        Assert.Equal(1, server.Grants);

        clock.Advance(TimeSpan.FromMinutes(31)); // 59 min left
        var second = await account.GameTokenAsync(CancellationToken.None);
        Assert.Equal("nx2-game-token-2", second.Token.Token);
        Assert.Equal(2, server.Grants);
        // the access token had ended meanwhile (an hour): refreshed on demand, rotated again
        Assert.Equal("refresh-2", vault.Load());
        Assert.Equal(["refresh-0", "refresh-1"], server.Forms.Select(f => f["refresh_token"]));
        Assert.All(server.Forms, f => Assert.Equal("refresh_token", f["grant_type"]));
    }

    [Fact]
    public async Task A_429_is_waited_out_and_the_token_in_hand_is_used_meanwhile()
    {
        var (account, server, clock, _, http) = Linked();
        using var a = account;
        using var h = http;
        server.LimitSeconds = 120;
        var limited = await Assert.ThrowsAsync<NextendoException>(() => account.GameTokenAsync(CancellationToken.None));
        Assert.Equal((NextendoError.Limited, TimeSpan.FromSeconds(120)), (limited.Kind, limited.RetryAfter));
        Assert.Equal(1, server.Count("POST /api/nex-token"));
        // not asked again before Retry-After is over
        clock.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<NextendoException>(() => account.GameTokenAsync(CancellationToken.None));
        Assert.Equal(1, server.Count("POST /api/nex-token"));
        clock.Advance(TimeSpan.FromSeconds(61));
        server.LimitSeconds = 0;
        var grant = await account.GameTokenAsync(CancellationToken.None);
        Assert.Equal(2, server.Count("POST /api/nex-token"));

        // less than an hour left and Nextendo says wait: the one in hand still works, and is handed out
        clock.Advance(TimeSpan.FromHours(23.5));
        server.LimitSeconds = 300;
        Assert.Same(grant, await account.GameTokenAsync(CancellationToken.None));
        Assert.Same(grant, await account.GameTokenAsync(CancellationToken.None));
        Assert.Equal(3, server.Count("POST /api/nex-token"));
        // ... until it has ended
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Same(grant, await account.GameTokenAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromHours(1));
        server.LimitSeconds = 0;
        Assert.NotSame(grant, await account.GameTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Online_status_is_asked_with_the_game_token_at_most_once_a_minute()
    {
        var (account, server, clock, _, http) = Linked();
        using var a = account;
        using var h = http;
        Assert.True((await account.OnlineAsync(fresh: false, CancellationToken.None)).Allow);
        server.Refuse = "suspended";
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.True((await account.OnlineAsync(fresh: false, CancellationToken.None)).Allow);
        Assert.Equal(1, server.Count("GET /api/online-status"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(new NextendoOnline(false, "suspended"), await account.OnlineAsync(fresh: false, CancellationToken.None));
        server.Refuse = "";
        Assert.True((await account.OnlineAsync(fresh: true, CancellationToken.None)).Allow); // PLAY asks now
        Assert.Equal(3, server.Count("GET /api/online-status"));
        Assert.All(server.Bearers.Where(b => b.Path == "/api/online-status"), b => Assert.Equal("nx2-game-token-1", b.Bearer));
        Assert.Equal(1, server.Grants);
    }

    [Fact]
    public async Task A_dead_sign_in_and_a_missing_scope_say_link_again()
    {
        var (account, server, _, _, http) = Linked();
        using var a = account;
        using var h = http;
        server.NoScope = true;
        var scope = await Assert.ThrowsAsync<NextendoException>(() => account.GameTokenAsync(CancellationToken.None));
        Assert.Equal((NextendoError.Scope, Strings.NextendoScope), (scope.Kind, scope.Message));

        var (dead, gone, _, _, http2) = Linked();
        using var d = dead;
        using var h2 = http2;
        gone.Revoked = true;
        var signedOut = await Assert.ThrowsAsync<NextendoException>(() => dead.UserAsync(CancellationToken.None));
        Assert.Equal(NextendoError.SignedOut, signedOut.Kind);

        var (cut, off, _, _, http3) = Linked();
        using var c = cut;
        using var h3 = http3;
        off.Unreachable = true;
        Assert.Equal(NextendoError.Unreachable, (await Assert.ThrowsAsync<NextendoException>(() => cut.UserAsync(CancellationToken.None))).Kind);
    }

    [Fact]
    public void The_sign_in_is_kept_protected_for_this_user()
    {
        var vault = new NextendoVault(Scratch(NextendoVault.FileName));
        Assert.Null(vault.Load());
        Assert.True(vault.Save("refresh-token-secret-value"));
        var bytes = File.ReadAllBytes(vault.Path);
        Assert.DoesNotContain("refresh-token-secret-value", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-token-secret-value", Encoding.Unicode.GetString(bytes), StringComparison.Ordinal);
        Assert.Equal("refresh-token-secret-value", vault.Load());
        File.WriteAllBytes(vault.Path, [1, 2, 3, 4]);
        Assert.Null(vault.Load()); // not this user's, or not such a file: linked again
        vault.Clear();
        Assert.False(vault.Exists);
    }

    // ---- client.toml and mkwmp.toml -------------------------------------------------------

    [Fact]
    public void Nextendo_is_where_play_goes_unless_the_file_says_otherwise_or_already_has_an_mkwmp_setup()
    {
        // a fresh install: Nextendo Network, and nothing about it in the file until something is chosen
        Assert.Equal((PlayNetwork.Nextendo, (PlayNetwork?)null, false), (ClientConfig.Default().PlayOn, ClientConfig.Default().Nextendo.Choice, ClientConfig.Default().HasMkwmpSetup));
        Assert.DoesNotContain("[nextendo]", ClientConfig.Default().ToToml(), StringComparison.Ordinal);
        // set up, with a local name, never on a server: Nextendo too
        Assert.Equal(PlayNetwork.Nextendo, ClientConfig.Parse("name = \"Ana\"\n\n[ui]\nsetup_done = true\n").PlayOn);

        // a file from before play_on existed that has an MKWMP setup stays on MKWMP servers: a server, a PIN, a kept profile
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Parse("server = \"https://mkw.example.net\"\n").PlayOn);
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Parse("name = \"Ana\"\npin = \"123456\"\n").PlayOn);
        var disconnected = ClientConfig.Parse("name = \"Ana\"\n\n[servers.\"https://mkw.example.net\"]\nname = \"Ana\"\npin = \"123456\"\n");
        Assert.Equal(("", true, PlayNetwork.Mkwmp), (disconnected.Settings.Server, disconnected.HasMkwmpSetup, disconnected.PlayOn));
        Assert.Null(disconnected.Nextendo.Choice); // not said by the file, and not written back by a mere round trip
        Assert.DoesNotContain("[nextendo]", disconnected.ToToml(), StringComparison.Ordinal);
        Assert.True(disconnected.SameAs(ClientConfig.Parse(disconnected.ToToml())));

        // what the file says wins, either way round, with or without an account
        var chose = ClientConfig.Parse("server = \"https://mkw.example.net\"\n\n[nextendo]\nplay_on = \"nextendo\"\n");
        Assert.Equal((false, PlayNetwork.Nextendo), (chose.Nextendo.Linked, chose.PlayOn));
        Assert.Contains("\n[nextendo]\nplay_on = \"nextendo\"\n", chose.ToToml(), StringComparison.Ordinal);
        Assert.DoesNotContain("pid", chose.ToToml(), StringComparison.Ordinal);
        Assert.True(chose.SameAs(ClientConfig.Parse(chose.ToToml())));
        var mkwmp = ClientConfig.Parse("[nextendo]\nplay_on = \"MKWMP\"\n");
        Assert.Equal((PlayNetwork.Mkwmp, false), (mkwmp.PlayOn, mkwmp.HasMkwmpSetup));
        Assert.Contains("\n[nextendo]\nplay_on = \"mkwmp\"\n", mkwmp.ToToml(), StringComparison.Ordinal);
        // a network that is neither is no choice
        Assert.Equal(PlayNetwork.Nextendo, ClientConfig.Parse("[nextendo]\nplay_on = \"somewhere\"\n").PlayOn);
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Parse("server = \"s.test\"\n\n[nextendo]\nplay_on = \"somewhere\"\n").PlayOn);

        // half an account is no account; the choice is its own and stays
        var half = ClientConfig.Parse("[nextendo]\nplay_on = \"nextendo\"\nusername = \"bia\"\n");
        Assert.Equal((false, PlayNetwork.Nextendo), (half.Nextendo.Linked, half.PlayOn));
        Assert.Equal((false, PlayNetwork.Mkwmp), (ClientConfig.Parse("[nextendo]\nplay_on = \"mkwmp\"\npid = 7\n").Nextendo.Linked, ClientConfig.Parse("[nextendo]\nplay_on = \"mkwmp\"\npid = 7\n").PlayOn));

        var linked = ClientConfig.Parse("[nextendo]\nplay_on = \"nextendo\"\npid = 1234567\nusername = \"bia\"\nfriend_code = \"SW-1\"\n");
        Assert.Equal((true, PlayNetwork.Nextendo, 1234567L, "bia", "SW-1"),
            (linked.Nextendo.Linked, linked.PlayOn, linked.Nextendo.Pid, linked.Nextendo.Username, linked.Nextendo.FriendCode));
        Assert.Contains("\n[nextendo]\nplay_on = \"nextendo\"\npid = 1234567\nusername = \"bia\"\nfriend_code = \"SW-1\"\n", linked.ToToml(), StringComparison.Ordinal);
        Assert.True(linked.SameAs(ClientConfig.Parse(linked.ToToml())));
        Assert.False(linked.SameAs(linked with { Nextendo = linked.Nextendo.Playing(PlayNetwork.Mkwmp) }));
        // an account with MKWMP servers chosen: both are kept
        var both = linked with { Nextendo = linked.Nextendo.Playing(PlayNetwork.Mkwmp) };
        Assert.Equal((true, PlayNetwork.Mkwmp), (ClientConfig.Parse(both.ToToml()).Nextendo.Linked, ClientConfig.Parse(both.ToToml()).PlayOn));

        // unlinking keeps the choice, choosing needs no account, and a new name keeps the choice
        Assert.Equal((false, (PlayNetwork?)PlayNetwork.Nextendo), (linked.Nextendo.Unlinked().Linked, linked.Nextendo.Unlinked().Choice));
        Assert.Equal((false, (PlayNetwork?)PlayNetwork.Mkwmp), (both.Nextendo.Unlinked().Linked, both.Nextendo.Unlinked().Choice));
        Assert.Equal(PlayNetwork.Mkwmp, NextendoLink.None.Playing(PlayNetwork.Mkwmp).Choice);
        Assert.Equal(NextendoLink.None, NextendoLink.Of("", 0, "bia", ""));
        Assert.Equal((PlayNetwork?)PlayNetwork.Nextendo, linked.Nextendo.Named(1234567, "bia2", "SW-2").Choice);
        Assert.Null(NextendoLink.None.Named(1234567, "bia", "SW-1").Choice); // naming an account chooses nothing by itself
        // nothing of a token is ever in the file
        Assert.DoesNotContain("token", linked.ToToml(), StringComparison.OrdinalIgnoreCase);

        // home's invitation, once closed, stays closed; the key is only written then
        Assert.DoesNotContain("nextendo_invite_dismissed", ClientConfig.Default().ToToml(), StringComparison.Ordinal);
        var closed = new ClientConfig { Settings = new LauncherSettings { Ui = new UiSettings { NextendoInviteDismissed = true } } };
        Assert.Contains("\nnextendo_invite_dismissed = true\n", closed.ToToml(), StringComparison.Ordinal);
        Assert.True(ClientConfig.Parse(closed.ToToml()).Settings.Ui.NextendoInviteDismissed);
    }
    [Fact]
    public void Mkwmp_toml_for_an_mkwmp_server_is_unchanged_and_for_nextendo_is_the_contract_s()
    {
        var mkwmp = Probe.SessionText(new SessionFile("lobby.test:8905", "Ana", "127.0.0.1:47820", new string('a', 64), 8907, "mkw1.t", 1, null, "C:/mkwmp/mkwmp.exe"));
        Assert.Equal(
            "# Generated by the mkwmp launcher. Do not edit; edit client.toml.\nserver = \"lobby.test:8905\"\nname = \"Ana\"\nlauncher = \"127.0.0.1:47820\"\n"
            + $"launcher_token = \"{new string('a', 64)}\"\nlauncher_exe = \"C:/mkwmp/mkwmp.exe\"\nvoice_port = 8907\nticket = \"mkw1.t\"\n",
            mkwmp);
        Assert.DoesNotContain("backend", mkwmp, StringComparison.Ordinal);

        var session = SessionFile.Nextendo("bia", "127.0.0.1:47820", new string('b', 64), @"C:\mkwmp\mkwmp.exe", FakeNextendo.CaPem);
        Assert.True(session.IsNextendo);
        Assert.False(session.IsOffline);
        var nextendo = Probe.SessionText(session);
        // plain key = "value" lines, no comment (the hook's reader does not strip one), LF, and none of an MKWMP session's keys
        Assert.Equal(
            "backend = \"nextendo\"\nname = \"bia\"\nlauncher = \"127.0.0.1:47820\"\n"
            + $"launcher_token = \"{new string('b', 64)}\"\nnextendo_ca = \"nextendo-ca.pem\"\nlauncher_exe = \"C:/mkwmp/mkwmp.exe\"\n",
            nextendo);
        foreach (var absent in new[] { "#", "\r", "server", "ticket", "voice_port", "nx2", "slot" })
        {
            Assert.DoesNotContain(absent, nextendo, StringComparison.Ordinal);
        }
        Assert.Equal("backend = \"nextendo\"\nname = \"bi a\"\n", Probe.SessionText(SessionFile.Nextendo("b\"i\\ a\n", "l", "t", null, "pem"))[..35]);
        Assert.True(SessionFile.Offline("127.0.0.1:47820", "t", null).IsOffline);

        // a bug report's copy of it names nobody
        var redacted = SupportBundle.RedactToml(nextendo);
        Assert.Contains("name = \"<redacted>\"", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("bia", redacted, StringComparison.Ordinal);
        Assert.Contains("name = \"Ana\"", SupportBundle.RedactToml(mkwmp), StringComparison.Ordinal); // an MKWMP gamertag stays, as before
        Assert.Equal("the game got nx2<redacted> today", SessionLog.Redact("the game got nx2-game-token-1.abcDEF today"));
    }

    // ---- the launcher ---------------------------------------------------------------------

    private static ClientConfig WithLink(ClientConfig c, string playOn) => c with { Nextendo = NextendoLink.Of(playOn, 1234567, "bia", "SW-1111-2222-3333") };

    [Fact]
    public async Task Linking_in_the_browser_keeps_the_sign_in_on_this_pc_and_tells_no_mkwmp_server()
    {
        var seat = new FakeSeatServer();
        var nextendo = new FakeNextendo();
        await using var rig = new Rig(seat, s => s with { Launch = false }, http: new TwoNetworks(seat, nextendo));
        var launcher = rig.Launcher;
        // the rig's install plays on an MKWMP server: it was never moved to Nextendo by itself
        Assert.Equal((false, PlayNetwork.Mkwmp, false, false), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.Preferred, launcher.Nextendo.NeedsSignIn));
        Assert.True(launcher.OnlineEnabled);
        Uri? opened = null;
        using var browser = new HttpClient();
        await launcher.LinkNextendoAsync(page =>
        {
            opened = page;
            var query = HttpUtility.ParseQueryString(page.Query);
            nextendo.Challenge = query["code_challenge"];
            Assert.True(launcher.Nextendo.Linking);
            // the browser, after the player signed in on nextendo.network: back to 127.0.0.1 with the code and the state
            _ = Task.Run(async () =>
            {
                await browser.GetAsync($"{query["redirect_uri"]}?code=wrong&state=forged");
                await browser.GetAsync($"{query["redirect_uri"]}?code=code-1&state={query["state"]}");
            });
            return true;
        });
        Assert.StartsWith("https://nextendo.network/api/oauth/authorize?", opened!.AbsoluteUri, StringComparison.Ordinal);
        // signing in is the choice of Nextendo: PLAY goes there from now on, and the MKWMP server is left alone
        var view = launcher.Nextendo;
        Assert.Equal((true, "bia", "SW-1111-2222-3333", PlayNetwork.Nextendo, true, false), (view.Linked, view.Username, view.FriendCode, view.PlayOn, view.Active, view.Linking));
        Assert.Equal((Strings.NextendoLinked("bia"), false), (view.Note, view.NoteIsError));
        Assert.Equal("Signed in as bia", view.Note);
        Assert.False(launcher.OnlineEnabled);
        // the exchange carried the verifier of the challenge and the same redirect
        var exchange = nextendo.Forms[0];
        Assert.Equal(("authorization_code", "code-1", HttpUtility.ParseQueryString(opened.Query)["redirect_uri"]), (exchange["grant_type"], exchange["code"], exchange["redirect_uri"]));
        Assert.Equal(nextendo.Challenge, Pkce.Challenge(exchange["code_verifier"]));

        // on this PC: who it is in client.toml, the sign-in protected beside it; no token in the settings or the log
        var saved = File.ReadAllText(rig.ConfigPath);
        Assert.Contains("[nextendo]\nplay_on = \"nextendo\"\npid = 1234567\nusername = \"bia\"", saved, StringComparison.Ordinal);
        Assert.Equal("refresh-0", NextendoVault.Beside(rig.ConfigPath).Load());
        await Rig.Until("the account to be asked about", () => !launcher.Nextendo.Checking && nextendo.Count("GET /api/oauth/userinfo") >= 2);
        var log = ReadShared(launcher.Log.Path!);
        foreach (var token in new[] { "refresh-0", "access-0", "code-1" })
        {
            Assert.DoesNotContain(token, saved, StringComparison.Ordinal);
            Assert.DoesNotContain(token, log, StringComparison.Ordinal);
        }
        foreach (var who in new[] { "bia", "1234567", "SW-1111" })
        {
            Assert.DoesNotContain(who, log, StringComparison.Ordinal); // logs go into bug reports: they name nobody
        }
        // and nothing of it reached the MKWMP server
        Assert.Empty(seat.Requests);

        // the choice is the player's (Settings): MKWMP servers with the account kept, and back
        await Rig.Until("the account's standing", () => launcher.Nextendo is { Checking: false, Status.Length: > 0 });
        Assert.Equal((Strings.NextendoAllowed, "good"), (launcher.Nextendo.Status, launcher.Nextendo.Tone));
        await launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        Assert.Equal((true, PlayNetwork.Mkwmp, false), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.Active));
        Assert.True(launcher.OnlineEnabled);
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Load(rig.ConfigPath).PlayOn);
        await launcher.SetPlayOnAsync(PlayNetwork.Nextendo);
        Assert.True(launcher.Nextendo.Active);
        Assert.False(launcher.OnlineEnabled);
        Assert.Equal(PlayNetwork.Nextendo, ClientConfig.Load(rig.ConfigPath).PlayOn);
        // signing in again (the same account) keeps a choice of MKWMP servers: only the first sign-in chooses
        await launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        await launcher.LinkNextendoAsync(page =>
        {
            var query = HttpUtility.ParseQueryString(page.Query);
            nextendo.Challenge = query["code_challenge"];
            _ = Task.Run(() => browser.GetAsync($"{query["redirect_uri"]}?code=code-1&state={query["state"]}"));
            return true;
        });
        Assert.Equal((true, PlayNetwork.Mkwmp, false), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.NoteIsError));
        await launcher.SetPlayOnAsync(PlayNetwork.Nextendo);

        // unlinked: Nextendo stays where PLAY goes (offline until someone signs in), and no MKWMP server comes back by itself
        await launcher.UnlinkNextendoAsync();
        Assert.Equal((false, PlayNetwork.Nextendo, false, true), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.Active, launcher.Nextendo.NeedsSignIn));
        Assert.False(launcher.OnlineEnabled);
        Assert.Equal(Strings.ReadyOffline, launcher.Session.Status);
        Assert.False(NextendoVault.Beside(rig.ConfigPath).Exists);
        var after = File.ReadAllText(rig.ConfigPath);
        Assert.Contains("\n[nextendo]\nplay_on = \"nextendo\"\n", after, StringComparison.Ordinal);
        Assert.DoesNotContain("bia", after, StringComparison.Ordinal);
        Assert.DoesNotContain("pid", after, StringComparison.Ordinal);
        Assert.Equal(1, nextendo.Count("POST /api/oauth/revoke")); // revoked there, with the refresh token that was kept
        Assert.StartsWith("refresh-", nextendo.Forms[^1]["token"], StringComparison.Ordinal);
        Assert.Empty(seat.Requests);
        // the choice needs no account, either way
        await launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        Assert.Equal((PlayNetwork.Mkwmp, true), (launcher.Nextendo.PlayOn, launcher.OnlineEnabled));
        await launcher.SetPlayOnAsync(PlayNetwork.Nextendo);
        Assert.Equal((PlayNetwork.Nextendo, false, (string?)null), (launcher.Nextendo.PlayOn, launcher.OnlineEnabled, launcher.Nextendo.Note));
    }

    /// <summary>A launcher on a client.toml with no MKWMP setup at all (no server, no PIN), as a fresh install after its welcome; both fake networks.</summary>
    private static Rig Fresh(FakeSeatServer seat, FakeNextendo nextendo) =>
        new(seat, s => s with { Server = "", Pin = "", AdultConsent = false, Launch = false }, http: new TwoNetworks(seat, nextendo));

    [Fact]
    public async Task A_fresh_install_plays_on_nextendo_and_play_is_offline_until_someone_signs_in()
    {
        var seat = new FakeSeatServer();
        var nextendo = new FakeNextendo();
        await using var rig = Fresh(seat, nextendo);
        var launcher = rig.Launcher;
        var view = launcher.Nextendo;
        Assert.Equal((false, PlayNetwork.Nextendo, true, true, false), (view.Linked, view.PlayOn, view.Preferred, view.NeedsSignIn, view.Active));
        Assert.False(launcher.OnlineEnabled);
        Assert.Equal(ServerState.NotAsked, launcher.Server.State);

        // the welcome ends without any MKWMP profile (no server, no gamertag registered, no PIN, no consent): PLAY is ready, offline
        await launcher.SaveSettingsAsync(launcher.Settings with { Ui = launcher.Settings.Ui with { SetupDone = true } });
        Assert.True(launcher.Settings.Ui.SetupDone);
        Assert.Equal(("", "", false), (launcher.Settings.Server, launcher.Settings.Pin, launcher.Settings.AdultConsent));
        Assert.Null(launcher.PlayProblem);
        Assert.Equal((Strings.ReadyOffline, "plain"), (launcher.Session.Status, launcher.Session.Tone));
        Assert.Equal(Strings.LocalNameNextendo, launcher.Profile.Message);
        Assert.Contains("\n[nextendo]\nplay_on = \"nextendo\"\n", File.ReadAllText(rig.ConfigPath), StringComparison.Ordinal); // said, from the first save on
        // the checklist's online row asks to sign in with Nextendo, never for an MKWMP server; it does not block PLAY
        var row = launcher.Checklist.Find(CheckId.Online)!;
        Assert.Equal((CheckState.NotApplicable, false, CheckActionKind.SignInNextendo), (row.State, row.Blocks, row.Action!.Kind));
        Assert.Equal(("Offline: sign in with Nextendo to race online", "Sign in with Nextendo"), (row.Status, row.Action.Label));
        Assert.True(launcher.Checklist.Ready);

        // PLAY, not signed in: the game starts offline, pointed at no network, and nothing is contacted
        await launcher.PlayAsync();
        await Rig.Until("the session to run", () => launcher.Session.State is SessionState.Running or SessionState.Failed);
        Assert.Equal((SessionState.Running, (SeatPhase?)null), (launcher.Session.State, launcher.Session.Phase));
        var keys = File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml")).Split('\n')
            .Where(line => line.Contains('=', StringComparison.Ordinal) && !line.StartsWith('#')).Select(line => line.Split('=')[0].Trim()).ToList();
        Assert.Contains("launcher_token", keys);
        foreach (var absent in new[] { "backend", "server", "ticket", "name", "nextendo_ca" })
        {
            Assert.DoesNotContain(absent, keys);
        }
        Assert.False(File.Exists(Path.Combine(rig.Game, "nextendo-ca.pem")));
        using var api = new LocalApi(launcher, TimeProvider.System);
        var bearer = "Bearer " + launcher.Token;
        // the game's Online Play is told where to sign in (not to connect to a server)
        var asked = await api.HandleAsync("POST", "/local/v1/seat", bearer, default, default);
        Assert.Equal((409, "refused", Strings.NextendoSignInInGame),
            (asked.Status, JsonNode.Parse(asked.Body!)!["state"]!.GetValue<string>(), JsonNode.Parse(asked.Body!)!["message"]!.GetValue<string>()));
        var session = await api.HandleAsync("GET", "/local/v1/nextendo/session", bearer, default, default);
        Assert.Equal((409, "unlinked"), (session.Status, JsonNode.Parse(session.Body!)!["reason"]!.GetValue<string>()));
        // signing in waits for STOP: the game that runs is offline, and nothing changes under it
        await launcher.LinkNextendoAsync(_ => throw new InvalidOperationException("no browser while the game runs"));
        Assert.Equal((false, Strings.PressStopFirst, true), (launcher.Nextendo.Linked, launcher.Nextendo.Note, launcher.Nextendo.NoteIsError));
        await launcher.StopAsync();
        Assert.Equal(SessionState.Idle, launcher.Session.State);
        Assert.Empty(seat.Requests);
        Assert.Empty(nextendo.Requests);

        // signed in: PLAY goes online on Nextendo, with no MKWMP server ever chosen
        using var browser = new HttpClient();
        await launcher.LinkNextendoAsync(page =>
        {
            var query = HttpUtility.ParseQueryString(page.Query);
            nextendo.Challenge = query["code_challenge"];
            _ = Task.Run(() => browser.GetAsync($"{query["redirect_uri"]}?code=code-1&state={query["state"]}"));
            return true;
        });
        Assert.Equal((true, PlayNetwork.Nextendo, true, false), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.Active, launcher.Nextendo.NeedsSignIn));
        Assert.Equal(Strings.NextendoReady, launcher.Session.Status);
        Assert.Equal(CheckState.Ok, launcher.Checklist.Find(CheckId.Online)!.State);
        await launcher.PlayAsync();
        await Rig.Until("the session to run", () => launcher.Session.State is SessionState.Running or SessionState.Failed);
        Assert.Equal(SessionState.Running, launcher.Session.State);
        Assert.StartsWith("backend = \"nextendo\"\nname = \"bia\"\n", File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml")), StringComparison.Ordinal);
        await launcher.StopAsync();
        Assert.Empty(seat.Requests);
    }

    [Fact]
    public async Task Mkwmp_servers_are_the_player_s_choice_and_only_then_is_a_server_s_consent_asked()
    {
        var seat = new FakeSeatServer();
        await using var rig = Fresh(seat, new FakeNextendo());
        var launcher = rig.Launcher;
        Assert.Null(launcher.PlayProblem); // on Nextendo nothing of an MKWMP server is asked for: no gamertag, PIN or consent

        // "Use MKWMP Servers Instead": no server yet, so still nothing is contacted, and the checklist now speaks of a server
        await launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        Assert.Equal((PlayNetwork.Mkwmp, false, false), (launcher.Nextendo.PlayOn, launcher.Nextendo.Preferred, launcher.Nextendo.NeedsSignIn));
        Assert.False(launcher.OnlineEnabled);
        Assert.Null(launcher.PlayProblem);
        Assert.Equal(CheckActionKind.ChooseServer, launcher.Checklist.Find(CheckId.Online)!.Action!.Kind);
        Assert.Equal(Strings.LocalNameOnly, launcher.Profile.Message);
        Assert.Contains("play_on = \"mkwmp\"", File.ReadAllText(rig.ConfigPath), StringComparison.Ordinal);
        Assert.Empty(seat.Requests);

        // a server chosen: its profile, then its consent, gate PLAY, here and not before
        await launcher.SaveSettingsAsync(launcher.Settings with { Server = FakeSeatServer.Url });
        Assert.True(launcher.OnlineEnabled);
        Assert.False(launcher.Settings.AdultConsent);
        Assert.Equal(Strings.NeedPin, launcher.PlayProblem);
        await launcher.SaveSettingsAsync(launcher.Settings with { Pin = "123456" });
        Assert.Equal(Strings.NoConsent, launcher.PlayProblem);
        Assert.Equal(CheckActionKind.ReviewPrivacy, launcher.Checklist.Find(CheckId.Online)!.Action!.Kind);

        // and back: Nextendo again, the MKWMP server left alone whatever is set, PLAY ready (offline until signed in)
        await launcher.SetPlayOnAsync(PlayNetwork.Nextendo);
        Assert.Equal((PlayNetwork.Nextendo, false), (launcher.Nextendo.PlayOn, launcher.OnlineEnabled));
        Assert.Equal(FakeSeatServer.Url, launcher.Settings.Server); // kept for when MKWMP servers are chosen again
        Assert.Null(launcher.PlayProblem);
        Assert.Equal(CheckActionKind.SignInNextendo, launcher.Checklist.Find(CheckId.Online)!.Action!.Kind);
    }

    [Fact]
    public async Task Choosing_a_server_is_choosing_mkwmp_servers()
    {
        var seat = new FakeSeatServer();
        await using var rig = Fresh(seat, new FakeNextendo());
        Assert.True(rig.Launcher.Nextendo.Preferred);
        // a server set by any other way than Settings › Online's switch (the headless launcher, a hand-edited file's reload):
        // it would be contacted by nothing while PLAY goes to Nextendo, so the choice follows
        await rig.Launcher.SaveSettingsAsync(rig.Launcher.Settings with { Server = FakeSeatServer.Url });
        Assert.Equal((PlayNetwork.Mkwmp, true), (rig.Launcher.Nextendo.PlayOn, rig.Launcher.OnlineEnabled));
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Load(rig.ConfigPath).PlayOn);
    }

    [Fact]
    public async Task An_install_with_an_mkwmp_setup_stays_on_mkwmp_servers_also_once_its_server_or_profile_is_gone()
    {
        var seat = new FakeSeatServer();
        await using var rig = new Rig(seat, s => s with { Launch = false }, http: new TwoNetworks(seat, new FakeNextendo()));
        var launcher = rig.Launcher;
        Assert.DoesNotContain("[nextendo]", File.ReadAllText(rig.ConfigPath), StringComparison.Ordinal); // a file from before Nextendo
        Assert.Equal((PlayNetwork.Mkwmp, true, Strings.Ready), (launcher.Nextendo.PlayOn, launcher.OnlineEnabled, launcher.Session.Status));
        // home's invitation is closed: kept, and MKWMP servers are now said in the file
        await launcher.SaveSettingsAsync(launcher.Settings with { Ui = launcher.Settings.Ui with { NextendoInviteDismissed = true } });
        var saved = File.ReadAllText(rig.ConfigPath);
        Assert.Contains("\nnextendo_invite_dismissed = true\n", saved, StringComparison.Ordinal);
        Assert.Contains("\n[nextendo]\nplay_on = \"mkwmp\"\n", saved, StringComparison.Ordinal);
        // disconnected: locked "Configure Online" as before, not Nextendo by surprise
        await launcher.SaveSettingsAsync(launcher.Settings with { Server = "" });
        Assert.Equal((PlayNetwork.Mkwmp, false, false), (launcher.Nextendo.PlayOn, launcher.OnlineEnabled, launcher.Nextendo.NeedsSignIn));
        Assert.Equal(CheckActionKind.ChooseServer, launcher.Checklist.Find(CheckId.Online)!.Action!.Kind);
    }

    [Fact]
    public async Task A_link_that_never_comes_back_times_out_and_one_can_be_cancelled()
    {
        var seat = new FakeSeatServer();
        await using var rig = new Rig(seat, http: new TwoNetworks(seat, new FakeNextendo()), linkPatience: TimeSpan.FromMilliseconds(200));
        await rig.Launcher.LinkNextendoAsync(_ => true);
        Assert.Equal((false, false, Strings.NextendoLinkTimeout, true), (rig.Launcher.Nextendo.Linked, rig.Launcher.Nextendo.Linking, rig.Launcher.Nextendo.Note, rig.Launcher.Nextendo.NoteIsError));
        await rig.Launcher.LinkNextendoAsync(_ => false);
        Assert.Equal(Strings.NextendoLinkNoBrowser, rig.Launcher.Nextendo.Note);

        await using var patient = new Rig(seat, http: new TwoNetworks(seat, new FakeNextendo()));
        var linking = patient.Launcher.LinkNextendoAsync(_ => true);
        await Rig.Until("the link to wait for the browser", () => patient.Launcher.Nextendo.Note == Strings.NextendoLinkWaiting);
        patient.Launcher.CancelNextendoLink();
        await linking;
        Assert.Equal((false, Strings.NextendoLinkCancelled, false), (patient.Launcher.Nextendo.Linking, patient.Launcher.Nextendo.Note, patient.Launcher.Nextendo.NoteIsError));
    }

    /// <summary>A launcher that opens on a client.toml with a linked account and its sign-in in the vault, and both fake networks.</summary>
    private sealed class NextendoRig : IAsyncDisposable
    {
        private readonly Rig _rig;

        public NextendoRig(string playOn = "nextendo", bool vault = true)
        {
            _rig = new Rig(Seat, s => s with { Launch = false }, http: new TwoNetworks(Seat, Nextendo), config: c => WithLink(c, playOn), prepare: path =>
            {
                if (vault)
                {
                    Assert.True(NextendoVault.Beside(path).Save("refresh-0"));
                }
            });
        }

        public FakeSeatServer Seat { get; } = new();
        public FakeNextendo Nextendo { get; } = new();
        public Launcher Launcher => _rig.Launcher;
        public string Game => _rig.Game;
        public string ConfigPath => _rig.ConfigPath;

        public ValueTask DisposeAsync() => _rig.DisposeAsync();
    }
    [Fact]
    public async Task An_account_named_in_the_settings_without_its_sign_in_is_not_linked()
    {
        // the choice of Nextendo stays: PLAY is offline until someone signs in on this PC, and the MKWMP server in the file is left alone
        await using var rig = new NextendoRig(vault: false);
        Assert.Equal((false, PlayNetwork.Nextendo, false, true), (rig.Launcher.Nextendo.Linked, rig.Launcher.Nextendo.PlayOn, rig.Launcher.Nextendo.Active, rig.Launcher.Nextendo.NeedsSignIn));
        Assert.False(rig.Launcher.OnlineEnabled);
        Assert.Null(rig.Launcher.PlayProblem);
        Assert.Equal(Strings.ReadyOffline, rig.Launcher.Session.Status);
        // and one that had MKWMP servers chosen stays on them
        await using var mkwmp = new NextendoRig(playOn: "mkwmp", vault: false);
        Assert.Equal((false, PlayNetwork.Mkwmp, true), (mkwmp.Launcher.Nextendo.Linked, mkwmp.Launcher.Nextendo.PlayOn, mkwmp.Launcher.OnlineEnabled));
    }

    [Theory]
    [InlineData("0123456789abcdef", "0123456789abcdef")]
    [InlineData("0123456789ABCDEF", "0123456789abcdef")]
    [InlineData("1f", "000000000000001f")]
    [InlineData("8000000000000000", "8000000000000000")]
    [InlineData("", "")]
    [InlineData("0", "")]
    [InlineData("0000000000000000", "")]
    [InlineData("0123456789abcdef0", "")] // 17 digits
    [InlineData("0x1f", "")]
    [InlineData(" 1f", "")]
    [InlineData("1f-2e", "")]
    [InlineData("u-12345", "")]
    [InlineData("１２", "")] // digits, but not ASCII
    public void An_account_s_id_on_the_game_s_servers_is_hex_or_nothing(string sent, string read)
    {
        Assert.Equal(read, NextendoApi.NplnAccount(Json.Parse(JsonSerializer.Serialize(new { npln_account_id = sent }))));
        Assert.Equal(read, NextendoApi.NplnAccount(Json.Parse(JsonSerializer.Serialize(new { account_hex = sent }))));
        // the first name that holds one counts
        Assert.Equal(read.Length > 0 ? read : "00000000000000aa", NextendoApi.NplnAccount(Json.Parse(JsonSerializer.Serialize(new { npln_account_id = sent, account_hex = "AA" }))));
    }

    [Fact]
    public void An_account_s_id_that_is_not_a_string_is_no_id()
    {
        Assert.Equal("", NextendoApi.NplnAccount(Json.Parse("""{"npln_account_id":1234}""")));
        Assert.Equal("", NextendoApi.NplnAccount(Json.Parse("""{"npln_account_id":null,"account_hex":["1f"]}""")));
        Assert.Equal("", NextendoApi.NplnAccount(Json.Parse("""{"pid":1}""")));
        Assert.Equal("", NextendoApi.NplnAccount(Json.Parse("[]")));
        Assert.Equal("", NextendoApi.NplnAccount(null));
    }

    [Fact]
    public async Task The_game_s_session_carries_the_account_s_id_once_nextendo_says_it()
    {
        await using var rig = new NextendoRig();
        var launcher = rig.Launcher;
        using var api = new LocalApi(launcher, TimeProvider.System);
        async Task<JsonObject> Session()
        {
            var answer = await api.HandleAsync("GET", "/local/v1/nextendo/session", "Bearer " + launcher.Token, default, default);
            Assert.Equal(200, answer.Status);
            return JsonNode.Parse(answer.Body!)!.AsObject();
        }

        // today: Nextendo says no id, and the session has no "accountId" at all (the hook derives one from the pid)
        await Rig.Until("the account to be asked about", () => !launcher.Nextendo.Checking);
        await launcher.RefreshNextendoAsync();
        Assert.False((await Session()).ContainsKey("accountId"));

        // junk is no id either
        rig.Nextendo.UserAccount = "u-1234567";
        await launcher.RefreshNextendoAsync();
        Assert.False((await Session()).ContainsKey("accountId"));

        // once it does: 16 lowercase hex digits, last
        rig.Nextendo.UserAccount = "AB12CD34EF5601";
        await launcher.RefreshNextendoAsync();
        var session = await Session();
        Assert.Equal("00ab12cd34ef5601", session["accountId"]!.GetValue<string>());
        Assert.Equal(
            ["state", "nexToken", "expiresAt", "pid", "username", "friendCode", "serverHost", "natHost", "caFile", "accountId"],
            session.Select(p => p.Key));
        // an answer without it later (an older server behind a balancer) does not take it back
        rig.Nextendo.UserAccount = null;
        await launcher.RefreshNextendoAsync();
        Assert.Equal("00ab12cd34ef5601", (await Session())["accountId"]!.GetValue<string>());

        // it is in no log (bug reports carry the log)
        var log = ReadShared(launcher.Log.Path!);
        Assert.DoesNotContain("ab12cd34ef5601", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Play_on_nextendo_writes_the_certificate_and_the_session_file_and_contacts_no_mkwmp_server()
    {
        await using var rig = new NextendoRig();
        var launcher = rig.Launcher;
        Assert.Equal((true, PlayNetwork.Nextendo, true), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.Active));
        Assert.False(launcher.OnlineEnabled); // the MKWMP side is off: no status, presence, friends or seat
        Assert.Null(launcher.PlayProblem);
        Assert.Equal(Strings.NextendoReady, launcher.Session.Status);
        var row = launcher.Checklist.Find(CheckId.Online)!;
        Assert.Equal((CheckState.Ok, false), (row.State, row.Blocks));
        Assert.Contains("bia", row.Status, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(rig.Game, Probe.LobbyCa), "an MKWMP lobby's certificate");

        await launcher.PlayAsync();
        await Rig.Until("the session to run", () => launcher.Session.State is SessionState.Running or SessionState.Failed);
        Assert.Equal((SessionState.Running, (SeatPhase?)null), (launcher.Session.State, launcher.Session.Phase));

        Assert.Equal(FakeNextendo.CaPem, File.ReadAllText(Path.Combine(rig.Game, "nextendo-ca.pem")));
        Assert.Equal(
            $"backend = \"nextendo\"\nname = \"bia\"\nlauncher = \"127.0.0.1:47820\"\nlauncher_token = \"{launcher.Token}\"\nnextendo_ca = \"nextendo-ca.pem\"\n",
            File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml")));
        Assert.False(File.Exists(Path.Combine(rig.Game, Probe.LobbyCa)));
        Assert.Empty(rig.Seat.Requests); // not one request to the MKWMP seat server
        Assert.Equal(1, rig.Nextendo.Count("GET /api/online-status"));
        Assert.Equal(1, rig.Nextendo.Count("GET /api/developers/network-config"));

        // the game's side: its session instead of a seat; no MKWMP friends, voice off
        using var api = new LocalApi(launcher, TimeProvider.System);
        var bearer = "Bearer " + launcher.Token;
        Assert.Equal(401, (await api.HandleAsync("GET", "/local/v1/nextendo/session", "Bearer nope", default, default)).Status);
        Assert.Equal(405, (await api.HandleAsync("POST", "/local/v1/nextendo/session", bearer, default, default)).Status);
        var answer = await api.HandleAsync("GET", "/local/v1/nextendo/session", bearer, default, default);
        Assert.Equal(200, answer.Status);
        var body = JsonNode.Parse(answer.Body!)!;
        Assert.Equal(
            ["state", "nexToken", "expiresAt", "pid", "username", "friendCode", "serverHost", "natHost", "caFile"],
            body.AsObject().Select(p => p.Key));
        Assert.Equal(("ready", "nx2-game-token-1", 1234567L, "bia", "SW-1111-2222-3333", "nex.nextendo.network", "nat.nextendo.network", "nextendo-ca.pem"),
            (body["state"]!.GetValue<string>(), body["nexToken"]!.GetValue<string>(), body["pid"]!.GetValue<long>(), body["username"]!.GetValue<string>(),
                body["friendCode"]!.GetValue<string>(), body["serverHost"]!.GetValue<string>(), body["natHost"]!.GetValue<string>(), body["caFile"]!.GetValue<string>()));
        var left = DateTimeOffset.FromUnixTimeMilliseconds(body["expiresAt"]!.GetValue<long>()) - DateTimeOffset.UtcNow;
        Assert.InRange(left, TimeSpan.FromHours(23), TimeSpan.FromHours(24.1));
        // served from the cache: no second game token, online-status at most once a minute, the network as PLAY read it
        await api.HandleAsync("GET", "/local/v1/nextendo/session", bearer, default, default);
        Assert.Equal((1, 1, 1), (rig.Nextendo.Grants, rig.Nextendo.Count("GET /api/online-status"), rig.Nextendo.Count("GET /api/developers/network-config")));

        Assert.Equal((200, """{"friends":[]}"""), await Answer(api.HandleAsync("GET", "/local/v1/friends", bearer, default, default)));
        var voice = await api.HandleAsync("GET", "/local/v1/voice", bearer, default, default);
        Assert.False(JsonNode.Parse(voice.Body!)!["enabled"]!.GetValue<bool>());
        var seat = await api.HandleAsync("POST", "/local/v1/seat", bearer, default, default);
        Assert.Equal((409, "refused"), (seat.Status, JsonNode.Parse(seat.Body!)!["state"]!.GetValue<string>()));
        Assert.Empty(rig.Seat.Requests);

        // the choice and the link wait for STOP
        await launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        await launcher.UnlinkNextendoAsync();
        Assert.Equal((true, PlayNetwork.Nextendo, Strings.PressStopFirst), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.Note));

        await launcher.StopAsync();
        Assert.Equal((SessionState.Idle, Strings.NextendoReady), (launcher.Session.State, launcher.Session.Status));

        // no token and nobody's name in the log; a report's dump is scrubbed of the tokens in memory
        var log = ReadShared(launcher.Log.Path!);
        foreach (var secret in new[] { "nx2", "access-1", "refresh-", "bia", "1234567" })
        {
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
        }
        Assert.Contains("playing on Nextendo Network (nex.nextendo.network)", log, StringComparison.Ordinal);

        // back on MKWMP servers PLAY is as it was: the lobby, a seat, and no Nextendo file left
        await launcher.SetPlayOnAsync(PlayNetwork.Mkwmp);
        Assert.True(launcher.OnlineEnabled);
        Assert.Equal(Strings.Ready, launcher.Session.Status);
        await launcher.PlayAsync();
        await Rig.Until("a seat", () => launcher.Session.Phase == SeatPhase.Seated || launcher.Session.State == SessionState.Failed);
        Assert.Equal(SeatPhase.Seated, launcher.Session.Phase);
        var toml = File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml"));
        Assert.Contains("server = \"lobby.test:8905\"", toml, StringComparison.Ordinal);
        Assert.Contains("ticket = \"mkw1.ticket.sig\"", toml, StringComparison.Ordinal);
        Assert.DoesNotContain("backend", toml, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(rig.Game, "nextendo-ca.pem")));
        var refused = await api.HandleAsync("GET", "/local/v1/nextendo/session", bearer, default, default);
        Assert.Equal((409, "unlinked"), (refused.Status, JsonNode.Parse(refused.Body!)!["reason"]!.GetValue<string>()));
        await launcher.StopAsync();
    }

    /// <summary>The session log, while the launcher still writes it.</summary>
    private static string ReadShared(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd();
    }

    private static async Task<(int, string?)> Answer(Task<LocalResponse> response)
    {
        var r = await response;
        return (r.Status, r.Body);
    }

    [Theory]
    [InlineData("unverified")]
    [InlineData("discord_unlinked")]
    [InlineData("suspended")]
    [InlineData("disabled")]
    [InlineData("elsewhere")]
    public async Task Play_says_why_nextendo_does_not_let_the_account_online(string reason)
    {
        await using var rig = new NextendoRig();
        rig.Nextendo.Refuse = reason;
        File.WriteAllText(Path.Combine(rig.Game, "mkwmp.toml"), "untouched\n");
        await rig.Launcher.PlayAsync();
        await Rig.Until("the session to end", () => !rig.Launcher.Session.Busy);
        Assert.Equal((SessionState.Failed, Strings.NextendoRefused(reason)), (rig.Launcher.Session.State, rig.Launcher.Session.Status));
        Assert.Equal((Strings.NextendoRefused(reason), reason == "suspended" ? "bad" : "warn"), (rig.Launcher.Nextendo.Status, rig.Launcher.Nextendo.Tone));
        Assert.Equal("untouched\n", File.ReadAllText(Path.Combine(rig.Game, "mkwmp.toml"))); // the game is not pointed anywhere
        Assert.Equal(0, rig.Nextendo.Count("GET /api/developers/network-config"));
        Assert.Empty(rig.Seat.Requests);

        // the game asking for its session is told the same, with the reason
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        var answer = await api.HandleAsync("GET", "/local/v1/nextendo/session", "Bearer " + rig.Launcher.Token, default, default);
        var body = JsonNode.Parse(answer.Body!)!;
        Assert.Equal((409, "refused", reason, Strings.NextendoRefused(reason)),
            (answer.Status, body["state"]!.GetValue<string>(), body["reason"]!.GetValue<string>(), body["message"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Nextendo_out_of_reach_or_a_dead_sign_in_keep_the_game_offline()
    {
        await using var rig = new NextendoRig();
        using var api = new LocalApi(rig.Launcher, TimeProvider.System);
        var bearer = "Bearer " + rig.Launcher.Token;
        rig.Nextendo.Unreachable = true;
        await rig.Launcher.PlayAsync();
        await Rig.Until("the session to end", () => !rig.Launcher.Session.Busy);
        Assert.Equal((SessionState.Failed, Strings.NextendoUnreachable), (rig.Launcher.Session.State, rig.Launcher.Session.Status));
        var down = await api.HandleAsync("GET", "/local/v1/nextendo/session", bearer, default, default);
        Assert.Equal((503, "unavailable"), (down.Status, JsonNode.Parse(down.Body!)!["state"]!.GetValue<string>()));

        rig.Nextendo.Unreachable = false;
        rig.Nextendo.NoScope = true;
        var scope = await api.HandleAsync("GET", "/local/v1/nextendo/session", bearer, default, default);
        Assert.Equal((409, "scope"), (scope.Status, JsonNode.Parse(scope.Body!)!["reason"]!.GetValue<string>()));

        rig.Nextendo.NoScope = false;
        rig.Nextendo.LimitSeconds = 30;
        var limited = await api.HandleAsync("GET", "/local/v1/nextendo/session", bearer, default, default);
        Assert.Equal(503, limited.Status);
        Assert.Empty(rig.Seat.Requests);
    }
}
