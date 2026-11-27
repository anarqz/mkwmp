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
                        ? Json(200, new { pid = 1234567, username = Username, friend_code = "SW-1111-2222-3333", display_name = "Bia" })
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
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
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

        var pem = "-----BEGIN CERTIFICATE-----\r\nAAAA\r\n-----END CERTIFICATE-----";
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
    public void Play_on_nextendo_only_counts_while_an_account_is_linked()
    {
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Default().Nextendo.PlayOn);
        // "nextendo" in the file with no account: MKWMP servers
        var unlinked = ClientConfig.Parse("[nextendo]\nplay_on = \"nextendo\"\n");
        Assert.Equal((false, PlayNetwork.Mkwmp, "mkwmp"), (unlinked.Nextendo.Linked, unlinked.Nextendo.PlayOn, unlinked.Nextendo.PlayOnName));
        Assert.DoesNotContain("[nextendo]", unlinked.ToToml(), StringComparison.Ordinal);
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Parse("[nextendo]\nplay_on = \"nextendo\"\nusername = \"bia\"\n").Nextendo.PlayOn);
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Parse("[nextendo]\nplay_on = \"nextendo\"\npid = 7\n").Nextendo.PlayOn);

        var linked = ClientConfig.Parse("[nextendo]\nplay_on = \"nextendo\"\npid = 1234567\nusername = \"bia\"\nfriend_code = \"SW-1\"\n");
        Assert.Equal((true, PlayNetwork.Nextendo, 1234567L, "bia", "SW-1"),
            (linked.Nextendo.Linked, linked.Nextendo.PlayOn, linked.Nextendo.Pid, linked.Nextendo.Username, linked.Nextendo.FriendCode));
        Assert.Contains("\n[nextendo]\nplay_on = \"nextendo\"\npid = 1234567\nusername = \"bia\"\nfriend_code = \"SW-1\"\n", linked.ToToml(), StringComparison.Ordinal);
        Assert.True(linked.SameAs(ClientConfig.Parse(linked.ToToml())));
        Assert.False(linked.SameAs(linked with { Nextendo = linked.Nextendo.Playing(PlayNetwork.Mkwmp) }));
        Assert.Equal(PlayNetwork.Mkwmp, ClientConfig.Parse("[nextendo]\nplay_on = \"somewhere\"\npid = 7\nusername = \"bia\"\n").Nextendo.PlayOn);

        // unlinking falls back, and so does choosing Nextendo with no account
        Assert.Equal(PlayNetwork.Mkwmp, NextendoLink.None.Playing(PlayNetwork.Nextendo).PlayOn);
        Assert.Same(NextendoLink.None, NextendoLink.Of("nextendo", 0, "bia", ""));
        Assert.Equal(PlayNetwork.Nextendo, linked.Nextendo.Named(1234567, "bia2", "SW-2").PlayOn); // a new name keeps the choice
        // nothing of a token is ever in the file
        Assert.DoesNotContain("token", linked.ToToml(), StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(NextendoView.None, launcher.Nextendo);
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
        var view = launcher.Nextendo;
        Assert.Equal((true, "bia", "SW-1111-2222-3333", PlayNetwork.Mkwmp, false, false), (view.Linked, view.Username, view.FriendCode, view.PlayOn, view.Active, view.Linking));
        Assert.Equal((Strings.NextendoLinked("bia"), false), (view.Note, view.NoteIsError));
        // the exchange carried the verifier of the challenge and the same redirect
        var exchange = nextendo.Forms[0];
        Assert.Equal(("authorization_code", "code-1", HttpUtility.ParseQueryString(opened.Query)["redirect_uri"]), (exchange["grant_type"], exchange["code"], exchange["redirect_uri"]));
        Assert.Equal(nextendo.Challenge, Pkce.Challenge(exchange["code_verifier"]));

        // on this PC: who it is in client.toml, the sign-in protected beside it; no token in the settings or the log
        var saved = File.ReadAllText(rig.ConfigPath);
        Assert.Contains("[nextendo]\nplay_on = \"mkwmp\"\npid = 1234567\nusername = \"bia\"", saved, StringComparison.Ordinal);
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

        // the choice: saved, and back to MKWMP servers when the account is unlinked
        await launcher.SetPlayOnAsync(PlayNetwork.Nextendo);
        Assert.True(launcher.Nextendo.Active);
        Assert.False(launcher.OnlineEnabled);
        Assert.Equal(PlayNetwork.Nextendo, ClientConfig.Load(rig.ConfigPath).Nextendo.PlayOn);
        await Rig.Until("the account's standing", () => launcher.Nextendo is { Checking: false, Status.Length: > 0 });
        Assert.Equal((Strings.NextendoAllowed, "good"), (launcher.Nextendo.Status, launcher.Nextendo.Tone));
        await launcher.UnlinkNextendoAsync();
        Assert.Equal((false, PlayNetwork.Mkwmp, false), (launcher.Nextendo.Linked, launcher.Nextendo.PlayOn, launcher.Nextendo.Active));
        Assert.True(launcher.OnlineEnabled);
        Assert.False(NextendoVault.Beside(rig.ConfigPath).Exists);
        Assert.DoesNotContain("[nextendo]", File.ReadAllText(rig.ConfigPath), StringComparison.Ordinal);
        Assert.Equal("refresh-0", nextendo.Forms[^1]["token"]); // revoked there, with the refresh token that was kept
        Assert.Equal(1, nextendo.Count("POST /api/oauth/revoke"));
        // choosing Nextendo with no account changes nothing
        await launcher.SetPlayOnAsync(PlayNetwork.Nextendo);
        Assert.Equal((PlayNetwork.Mkwmp, Strings.NextendoNotLinked), (launcher.Nextendo.PlayOn, launcher.Nextendo.Note));
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
        await using var rig = new NextendoRig(vault: false);
        Assert.Equal((false, PlayNetwork.Mkwmp, false), (rig.Launcher.Nextendo.Linked, rig.Launcher.Nextendo.PlayOn, rig.Launcher.Nextendo.Active));
        Assert.True(rig.Launcher.OnlineEnabled);
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
