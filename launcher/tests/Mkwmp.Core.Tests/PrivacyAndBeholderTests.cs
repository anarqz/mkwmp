// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Text;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The consent kept by the server (asked for after signing in), the server's own privacy policy,
/// and the beholder's server list with latencies measured to the servers themselves. All against
/// fakes: no production server, no real beholder.
/// </summary>
public class PrivacyAndBeholderTests
{
    private const string Policy = "# Privacy\n\nThis server keeps **your gamertag**.\n";

    [Fact]
    public async Task Consent_comes_after_signing_in_and_is_stored_on_the_server()
    {
        var server = new FakeSeatServer { PrivacyText = Policy };
        await using var rig = new Rig(server, s => s with { Server = "", Name = "Ana", Pin = "", AdultConsent = false });
        var l = rig.Launcher;

        // Choose the server and register: no consent is needed for either.
        Assert.True((await l.ProbeServerAsync(FakeSeatServer.Url)).Ok);
        await l.SaveSettingsAsync(l.Settings with { Server = FakeSeatServer.Url });
        await l.RegisterAsync("Ana");
        Assert.True(l.Profile.SignedIn);
        // Signed in, not consented: that is what blocks PLAY now, and the server refuses a seat too.
        Assert.Equal(Strings.NoConsent, l.PlayProblem);
        await l.AddFriendAsync("Bia");
        Assert.Equal(Strings.NoConsent, l.Friends.Note);
        Assert.DoesNotContain("POST /seat/v1/friends", server.Requests);

        // The policy is the server's own, and its sha256 is the version.
        var policy = await l.GetPrivacyPolicyAsync();
        Assert.Equal((Policy, true, FakeSeatServer.Url + "/privacy"), (policy.Markdown, policy.FromServer, policy.Link));
        Assert.Equal(Launcher.PrivacyVersion(Policy), policy.Version);
        Assert.Equal(64, policy.Version!.Length);

        // Accepting stores it there, with that version, and only then here.
        await l.SetConsentAsync(true);
        Assert.Equal($$"""{"accepted":true,"version":"{{policy.Version}}"}""", Assert.Single(server.PrivacyPuts));
        Assert.Equal(server.PrivacyVersion, policy.Version); // the quoted ETag, without its quotes
        Assert.True(l.Settings.AdultConsent);
        Assert.Null(l.PlayProblem);
        await l.PlayAsync();
        await Rig.Until("the game", () => rig.Started);

        // Withdrawing is stored too.
        await rig.SettleSession();
        await l.SetConsentAsync(false);
        Assert.Equal("""{"accepted":false}""", server.PrivacyPuts[^1]);
        Assert.False(server.PrivacyAccepted);
        Assert.Equal(Strings.NoConsent, l.PlayProblem);
    }

    [Fact]
    public async Task A_sign_in_brings_the_servers_consent_to_this_device()
    {
        // Another device accepted already: recovering the profile with its PIN needs no second consent.
        var server = new FakeSeatServer { PrivacyText = Policy, PrivacyAccepted = true };
        await using var rig = new Rig(server, s => s with { Pin = "", AdultConsent = false });
        await rig.Launcher.UsePinAsync("Ana", "123456");
        Assert.True(rig.Launcher.Profile.SignedIn);
        Assert.True(rig.Launcher.Settings.AdultConsent);
        Assert.True(ClientConfig.Load(rig.ConfigPath).Settings.AdultConsent);
        Assert.Null(rig.Launcher.PlayProblem);

        // And the other way: the server's policy changed (or the consent was withdrawn elsewhere), so it is asked for again.
        var changed = new FakeSeatServer { PrivacyText = Policy, PrivacyAccepted = false };
        await using var stale = new Rig(changed); // adult_consent = true locally
        await stale.Launcher.AddFriendAsync("Bia");
        Assert.False(stale.Launcher.Settings.AdultConsent);
        Assert.Equal(Strings.NoConsent, stale.Launcher.PlayProblem);
        Assert.Equal(Strings.NoConsent, stale.Launcher.Friends.Note);
    }

    [Fact]
    public async Task A_policy_that_changes_asks_for_the_consent_again()
    {
        var server = new FakeSeatServer { PrivacyText = Policy, PrivacyAccepted = true };
        await using var rig = new Rig(server);
        var l = rig.Launcher;
        await l.AddFriendAsync("Bia"); // signed in: the token is kept from here on
        Assert.True(l.Settings.AdultConsent);

        // The host edits the policy: every profile is without consent again, and the server says so with 403 PRIVACY_REQUIRED.
        server.PrivacyText = Policy + "\nNew paragraph.\n";
        server.PrivacyAccepted = false;
        await l.AddFriendAsync("Cid");
        Assert.Equal((Strings.NoConsent, true), (l.Friends.Note, l.Friends.NoteIsError));
        Assert.False(l.Settings.AdultConsent);
        Assert.Equal(Strings.NoConsent, l.PlayProblem);

        // The player read the old text and accepts: the version is not the current one (409 PRIVACY_VERSION), nothing is kept.
        var old = Launcher.PrivacyVersion(Policy);
        var shown = await l.GetPrivacyPolicyAsync();
        Assert.NotEqual(old, shown.Version);
        server.PrivacyText = Policy + "\nYet another one.\n"; // it changes again while the page is open
        await l.SetConsentAsync(true);
        Assert.False(l.Settings.AdultConsent);
        Assert.Equal((RegisterState.Failed, Strings.PrivacyChanged), (l.Profile.Register, l.Profile.Message));
        // Read again (the page fetches it), accept: stored with the version that is current now.
        await l.SetConsentAsync(true);
        Assert.True(l.Settings.AdultConsent);
        Assert.Contains(server.PrivacyVersion, server.PrivacyPuts[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_server_that_only_records_consent_does_not_block_on_failure()
    {
        // [privacy] enforce = false: the consent page still shows and the answer is sent, but a failed PUT does not stop the player.
        var server = new FakeSeatServer { PrivacyText = Policy, PrivacyEnforced = false, PrivacyPutFails = 500 };
        await using var rig = new Rig(server, s => s with { AdultConsent = false });
        var l = rig.Launcher;
        await l.AddFriendAsync("Bia");
        Assert.Equal(Strings.NoConsent, l.Friends.Note); // not accepted yet: the launcher still asks first
        await l.SetConsentAsync(true);
        Assert.True(l.Settings.AdultConsent);
        Assert.Null(l.PlayProblem);
        server.PrivacyPutFails = 0;
        await l.SetConsentAsync(true);
        Assert.True(server.PrivacyAccepted);
    }

    [Fact]
    public async Task A_consent_the_server_could_not_store_is_not_kept()
    {
        var server = new FakeSeatServer { PrivacyText = Policy, PrivacyPutFails = 500 };
        await using var rig = new Rig(server, s => s with { AdultConsent = false });
        await rig.Launcher.SetConsentAsync(true);
        Assert.False(rig.Launcher.Settings.AdultConsent);
        Assert.Equal((RegisterState.Failed, Strings.ConsentNotStored), (rig.Launcher.Profile.Register, rig.Launcher.Profile.Message));
        server.PrivacyPutFails = 0;
        await rig.Launcher.SetConsentAsync(true);
        Assert.True(rig.Launcher.Settings.AdultConsent);
        Assert.Equal(RegisterState.Idle, rig.Launcher.Profile.Register);
    }

    [Fact]
    public async Task A_server_without_server_side_consent_keeps_working()
    {
        // A 1.3.0 server: no /privacy, no privacyAccepted, no PUT. The local adult_consent decides. The launcher has no
        // policy of its own to show instead: it says the server publishes none, and links nowhere.
        var server = new FakeSeatServer();
        await using var rig = new Rig(server, s => s with { AdultConsent = false });
        var l = rig.Launcher;
        var policy = await l.GetPrivacyPolicyAsync();
        Assert.Equal((false, null, null, "", Strings.PrivacyNone), (policy.FromServer, policy.Version, policy.Link, policy.Markdown, policy.Note));
        await l.SetConsentAsync(true);
        Assert.True(l.Settings.AdultConsent);
        Assert.DoesNotContain("PUT /seat/v1/privacy", server.Requests); // a sign-in without privacyAccepted told it not to try
        Assert.Null(l.PlayProblem);
        await l.PlayAsync();
        await Rig.Until("the game", () => rig.Started);

        // Unreachable: no text, saying why.
        await using var down = new Rig(new FakeSeatServer { Unreachable = true });
        var fallback = await down.Launcher.GetPrivacyPolicyAsync();
        Assert.Equal((false, "", null, Strings.PrivacyUnreachable), (fallback.FromServer, fallback.Markdown, fallback.Link, fallback.Note));
        // Offline: no server is asked.
        await using var offline = new Rig(server, s => s with { Server = "" });
        server.Requests.Clear();
        var none = await offline.Launcher.GetPrivacyPolicyAsync();
        Assert.Equal((false, "", Strings.PrivacyNoServer), (none.FromServer, none.Markdown, none.Note));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Recovering_with_a_wrong_pin_does_not_sign_in()
    {
        await using var rig = new Rig(new FakeSeatServer { Mood = SeatMood.Refused }, s => s with { Pin = "" });
        await rig.Launcher.UsePinAsync("Ana", "000000");
        Assert.False(rig.Launcher.Profile.SignedIn);
        Assert.Equal("", rig.Launcher.Settings.Pin);
        Assert.Equal((RegisterState.Failed, Strings.WrongPin), (rig.Launcher.Profile.Register, rig.Launcher.Profile.Message));
        // A server that cannot be asked keeps the PIN for the next sign-in.
        await using var down = new Rig(new FakeSeatServer { Unreachable = true }, s => s with { Pin = "" });
        await down.Launcher.UsePinAsync("Ana", "123456");
        Assert.Equal(("123456", RegisterState.Idle), (down.Launcher.Settings.Pin, down.Launcher.Profile.Register));
    }

    // ---- the beholder ---------------------------------------------------------------------

    [Fact]
    public void The_beholder_is_https_or_local()
    {
        Assert.NotNull(BeholderClient.Allowed("https://beholder.example.net"));
        Assert.NotNull(BeholderClient.Allowed("http://127.0.0.1:8090"));
        Assert.NotNull(BeholderClient.Allowed("http://192.168.0.10:8090/"));
        Assert.NotNull(BeholderClient.Allowed("http://localhost:8090"));
        Assert.Null(BeholderClient.Allowed("http://beholder.example.net"));
        Assert.Null(BeholderClient.Allowed("ftp://beholder.example.net"));
        Assert.Null(BeholderClient.Allowed(""));
        Assert.Null(BeholderClient.Allowed("not a url"));
        // The production beholder (live since 2026-11-20) is the release default.
        Assert.Equal("https://beholder.anarqz.dev", BeholderClient.DefaultUrl);
        Assert.NotNull(BeholderClient.Allowed(BeholderClient.DefaultUrl));
    }

    [Fact]
    public void A_list_is_cleaned_and_bounded()
    {
        var json = """
            {"items":[
              {"publicKey":"k1","name":"  Alpha\u0007‮ ","endpoint":"https://alpha.test","region":"sa-east","version":"1.4.0","capacity":32,"seated":12,"queued":1,"firstSeen":"2026-10-17T14:00:00Z","lastSeen":"2026-11-20T10:00:00Z"},
              {"publicKey":"k2","name":"","endpoint":"http://beta.test:8080/","capacity":8},
              {"publicKey":"k3","name":"Same endpoint","endpoint":"https://ALPHA.test/"},
              {"publicKey":"k4","name":"A path","endpoint":"https://x.test/admin"},
              {"publicKey":"k5","name":"Credentials","endpoint":"https://user:pw@x.test"},
              {"publicKey":"k6","name":"Not http","endpoint":"ftp://x.test"},
              {"publicKey":"","name":"No key","endpoint":"https://y.test"},
              {"name":"Nothing"}, 7, null
            ]}
            """u8;
        var list = BeholderClient.Parse(json)!;
        Assert.Equal(2, list.Count);
        Assert.Equal(new Listing("k1", "Alpha", "https://alpha.test", "sa-east", "1.4.0", 32, 12, 1, "2026-10-17T14:00:00Z", "2026-11-20T10:00:00Z"), list[0]);
        Assert.Equal(("http://beta.test:8080", "http://beta.test:8080", null, 8, 0), (list[1].Name, list[1].Endpoint, list[1].Region, list[1].Capacity, list[1].Seated));
        Assert.Null(BeholderClient.Parse("[]"u8));
        Assert.Null(BeholderClient.Parse("<html>"u8));
        var many = "{\"items\":[" + string.Join(',', Enumerable.Range(0, 700).Select(i => $"{{\"publicKey\":\"k\",\"name\":\"n\",\"endpoint\":\"https://s{i}.test\"}}")) + "]}";
        Assert.Equal(BeholderClient.MaxItems, BeholderClient.Parse(Encoding.UTF8.GetBytes(many))!.Count);
    }

    [Fact]
    public async Task A_set_beholder_shows_the_list_as_coming_before_anyone_asked()
    {
        // The window opened the server page, saw "off" and never asked: the list never came.
        var root = Rig.NewFolder("beholder-coming");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { AutoUpdate = false } }.Save(path);
        await using var l = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Beholder = "https://beholder.example.net", Targets = [new MkwRecompTarget("1.4.0")] }, new FakeNet());
        Assert.Equal(ServerListState.Loading, l.Servers.State);

        await using var none = new Launcher(Path.Combine(Rig.NewFolder("beholder-none"), "client.toml"), root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Beholder = "", Targets = [new MkwRecompTarget("1.4.0")] }, new FakeNet());
        Assert.Equal(ServerListState.Off, none.Servers.State);
    }
    [Fact]
    public async Task Servers_are_listed_measured_and_sorted_by_latency()
    {
        var net = new FakeNet();
        var root = Rig.NewFolder("beholder");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { AutoUpdate = false } }.Save(path);
        await using var l = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Beholder = "", Targets = [new MkwRecompTarget("1.4.0")] }, net);

        // No beholder (the release default turned off): no list, and nobody is asked.
        Assert.Equal(ServerListState.Off, l.Servers.State);
        await l.RefreshServersAsync();
        Assert.Equal(ServerListState.Off, l.Servers.State);
        Assert.Empty(net.Requests);

        // A beholder that may not be asked (plain http on the internet) is no beholder.
        await l.SaveSettingsAsync(l.Settings with { Beholder = "http://beholder.example.net" });
        await l.RefreshServersAsync();
        Assert.Equal(ServerListState.Off, l.Servers.State);
        Assert.Empty(net.Requests);

        await l.SaveSettingsAsync(l.Settings with { Beholder = "https://beholder.test" });
        Assert.Contains("beholder = \"https://beholder.test\"", File.ReadAllText(path), StringComparison.Ordinal);
        await l.RefreshServersAsync();
        var view = l.Servers;
        Assert.Equal((ServerListState.Ready, false, null), (view.State, view.Busy, view.Note));
        // Sorted by latency; the one that does not answer is last. The latency is to the server, three requests each.
        Assert.Equal(["Near", "Mid", "Far", "Gone"], view.Items.Select(i => i.Name));
        Assert.Equal([true, true, true, false], view.Items.Select(i => i.Online));
        Assert.True(view.Items[0].LatencyMs < view.Items[1].LatencyMs && view.Items[1].LatencyMs < view.Items[2].LatencyMs);
        Assert.Null(view.Items[3].LatencyMs);
        Assert.Equal([3, 2, 1, 0], view.Items.Select(i => i.Bars));
        // A 1.4.0 server is timed on /ping (after one status that says who it is); an older one on its status.
        Assert.Equal((1, BeholderClient.Samples), (net.Requests.Count(r => r == "GET https://near.test/seat/v1/status"), net.Requests.Count(r => r == "GET https://near.test/ping")));
        Assert.Equal((1 + BeholderClient.Samples, 1), (net.Requests.Count(r => r == "GET https://far.test/seat/v1/status"), net.Requests.Count(r => r == "GET https://far.test/ping")));
        Assert.Equal(1, net.Requests.Count(r => r.StartsWith("GET https://beholder.test/", StringComparison.Ordinal)));
        // Seats and queue are the server's own answer, not the beholder's words.
        Assert.Equal((32, 12, 3, "sa-east", "1.4.0"), (view.Items[0].Capacity, view.Items[0].Seated, view.Items[0].Queued, view.Items[0].Region, view.Items[0].Version));
        // Still offline: looking at the list chose nothing.
        Assert.Equal("", l.Settings.Server);

        // Measuring again does not read the list again.
        net.Requests.Clear();
        await l.MeasureServersAsync();
        Assert.DoesNotContain(net.Requests, r => r.Contains("beholder", StringComparison.Ordinal));
        Assert.Equal(4, l.Servers.Items.Count);

        // The beholder goes down: the last good list (from its file), said to be that; latencies are still live.
        net.BeholderDown = true;
        await using var again = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, net);
        await again.RefreshServersAsync();
        Assert.Equal((ServerListState.Cached, Strings.BeholderUnreachable), (again.Servers.State, again.Servers.Note));
        Assert.Equal("Near", again.Servers.Items[0].Name);
        // No file either: failed, empty.
        File.Delete(Path.Combine(root, BeholderClient.CacheFileName));
        await using var bare = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, net);
        await bare.RefreshServersAsync();
        Assert.Equal((ServerListState.Failed, 0), (bare.Servers.State, bare.Servers.Items.Count));
    }

    [Fact]
    public async Task A_list_that_is_too_large_is_refused()
    {
        var net = new FakeNet { Huge = true };
        using var http = new HttpClient(net);
        var e = await Assert.ThrowsAsync<ApiException>(() => BeholderClient.ListAsync(http, new Uri("https://beholder.test"), "test", CancellationToken.None));
        Assert.Equal((ApiError.Transient, Strings.BeholderTooLarge), (e.Kind, e.Message));
    }

    /// <summary>A beholder and four servers: near (fast), mid, far (slow), gone (no answer).</summary>
    private sealed class FakeNet : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public bool BeholderDown { get; set; }

        public bool Huge { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add($"{request.Method.Method} {uri.Scheme}://{uri.Host}{uri.AbsolutePath}");
            }
            static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (uri.Host == "beholder.test")
            {
                if (BeholderDown)
                {
                    throw new HttpRequestException("connection refused");
                }
                if (Huge)
                {
                    return Json("{\"items\":[],\"pad\":\"" + new string('x', BeholderClient.MaxBytes) + "\"}");
                }
                // The beholder's own numbers are stale on purpose: the server's answer wins.
                return Json("""
                    {"items":[
                      {"publicKey":"kf","name":"Far","endpoint":"https://far.test","region":"eu-west","version":"1.4.0","capacity":32,"seated":0,"queued":0},
                      {"publicKey":"kg","name":"Gone","endpoint":"https://gone.test","region":"us-east","version":"1.3.0","capacity":16,"seated":2,"queued":0},
                      {"publicKey":"kn","name":"Near","endpoint":"https://near.test","region":"sa-east","version":"1.4.0","capacity":32,"seated":1,"queued":0},
                      {"publicKey":"km","name":"Mid","endpoint":"https://mid.test","region":"us-east","version":"1.4.0","capacity":32,"seated":30,"queued":0}
                    ]}
                    """);
            }
            var delay = uri.Host switch { "near.test" => 5, "mid.test" => 90, "far.test" => 220, _ => -1 };
            if (delay < 0)
            {
                throw new HttpRequestException("no route to host");
            }
            await Task.Delay(delay, cancellationToken);
            if (uri.AbsolutePath == "/ping")
            {
                // far.test is a server before 1.4.0: no /ping
                return new HttpResponseMessage(uri.Host == "far.test" ? HttpStatusCode.NotFound : HttpStatusCode.NoContent);
            }
            return uri.AbsolutePath == "/seat/v1/status"
                ? Json("""{"name":"s","seatsTotal":32,"seated":12,"queued":3,"lobbyEndpoint":"lobby.test:8905","privacy":{"version":"abc","enforced":false}}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
