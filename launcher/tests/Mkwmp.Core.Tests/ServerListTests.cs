// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Net;
using System.Text;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The server list of a directory with many servers: the nearest first (the window shows five), a
/// bounded number of servers contacted, and the servers the player hides on this PC (client.toml).
/// </summary>
public sealed class ServerListTests
{
    private static ListedServer Server(string name, int? latency, bool? online, string? key = null) =>
        new(name, $"https://{name.ToLowerInvariant()}.test", null, null, 32, 0, 0, latency, online) { PublicKey = key ?? "key-" + name };

    [Fact]
    public void The_nearest_that_answered_come_first_and_the_others_only_fill_in()
    {
        ListedServer[] listed =
        [
            Server("Gone", null, false), Server("Waiting", null, null), Server("Far", 240, true), Server("Near", 12, true),
            Server("Mid", 90, true), Server("Also", 90, true), Server("Other", 130, true), Server("Slow", 400, true),
        ];
        var shown = ServerRanking.Visible(listed, []);
        Assert.Equal(["Near", "Also", "Mid", "Other", "Far", "Slow", "Waiting", "Gone"], shown.Select(s => s.Name));
        // The window's first page: the five lowest latencies, all of them servers that answered.
        Assert.All(shown.Take(ServerListView.PageSize), s => Assert.True(s.Online));
        Assert.Equal(5, ServerListView.PageSize);

        // Fewer than five answered: the ones not measured, then the ones that did not answer, fill the page.
        var few = ServerRanking.Visible([Server("Gone", null, false), Server("Waiting", null, null), Server("Near", 12, true)], []);
        Assert.Equal(["Near", "Waiting", "Gone"], few.Select(s => s.Name));
    }

    [Fact]
    public void A_hidden_server_is_out_and_the_next_takes_its_place()
    {
        ListedServer[] listed = [.. Enumerable.Range(1, 8).Select(i => Server("S" + i, i * 10, true))];
        var hidden = new[] { new HiddenServer("key-S2", "S2", "https://s2.test"), new HiddenServer("https://S4.test/", "S4", "https://s4.test") };
        var shown = ServerRanking.Visible(listed, hidden);
        // By public key, and by address (however it is written) for a server hidden without one.
        Assert.Equal(["S1", "S3", "S5", "S6", "S7"], shown.Take(ServerListView.PageSize).Select(s => s.Name));
        Assert.Equal(6, shown.Count);
        // The key wins over the address: the same server on a new address stays hidden, another server on the old one does not.
        Assert.True(hidden[0].Is(Server("Moved", 5, true, key: "key-S2")));
        Assert.False(hidden[0].Is(Server("S2", 5, true, key: "another-key")));
        // A server listed without a key is known by its address.
        var keyless = new ListedServer("Bare", "https://bare.test", null, null, 0, 0, 0, null, null);
        Assert.Equal("https://bare.test", keyless.Id);
        Assert.True(new HiddenServer(keyless.Id, "Bare", keyless.Address).Is(keyless));
    }

    [Fact]
    public void Only_the_first_of_a_long_list_are_measured_plus_the_ones_the_player_used()
    {
        ListedServer[] listed = [.. Enumerable.Range(1, 300).Select(i => Server("S" + i, null, null))];
        var hidden = new[] { new HiddenServer("key-S1", "S1", ""), new HiddenServer("key-S250", "S250", "") };
        var measured = ServerRanking.ToMeasure(listed, hidden, ["https://s200.test", "http://unlisted.test", "s250.test", ""]);
        Assert.Equal(50, ServerListView.MeasuredAtMost);
        Assert.Equal(51, measured.Count);
        // The beholder's order, the hidden one not counted: S2..S51; then the one the player has a profile on.
        Assert.Equal(Enumerable.Range(2, 50).Select(i => "S" + i).Append("S200"), measured.Select(s => s.Name));
        // A hidden server is never contacted, even one the player used.
        Assert.DoesNotContain(measured, s => s.Name is "S1" or "S250");
        // A short list is measured whole.
        Assert.Equal(7, ServerRanking.ToMeasure([.. listed.Take(7)], [], []).Count);
    }

    [Fact]
    public void Hidden_servers_are_kept_in_client_toml()
    {
        var config = new ClientConfig
        {
            Settings = new LauncherSettings { Name = "Ana", Server = "https://mkw.example.net", Pin = "123456" },
            HiddenServers = [new HiddenServer("ed25519:AbC+/=", "Sunset \"Circuit\" Club", "https://sunset.example.org"), new HiddenServer("http://192.168.0.7:8080", "", "http://192.168.0.7:8080")],
        }.Remembering();
        var toml = config.ToToml();
        Assert.Contains("[[server_list.hidden]]\nid = \"ed25519:AbC+/=\"\nname = \"Sunset \\\"Circuit\\\" Club\"\naddress = \"https://sunset.example.org\"\n", toml, StringComparison.Ordinal);
        var back = ClientConfig.Parse(toml);
        Assert.Equal(config.HiddenServers, back.HiddenServers);
        Assert.True(config.SameAs(back));
        // The profiles kept per server are still there, after the list.
        Assert.Equal(config.Servers, back.Servers);
        Assert.False(config.SameAs(back with { HiddenServers = [] }));

        // None hidden: the file says nothing about it (and an older file reads as none).
        Assert.DoesNotContain("server_list", new ClientConfig().ToToml(), StringComparison.Ordinal);
        Assert.Empty(ClientConfig.Parse("name = \"Ana\"\n").HiddenServers);
        // A file edited by hand: rows without an id, and the same id twice, are dropped.
        var odd = ClientConfig.Parse("[[server_list.hidden]]\nname = \"no id\"\n[[server_list.hidden]]\nid = \"k\"\n[[server_list.hidden]]\nid = \"k\"\nname = \"again\"\n");
        Assert.Equal([new HiddenServer("k", "", "")], odd.HiddenServers);
    }

    [Fact]
    public async Task Hiding_a_server_is_saved_and_the_next_nearest_takes_its_place()
    {
        var net = new ManyServers(7, gapMs: 40); // S1 answers at once, S7 after 240 ms: far enough apart for any timer
        var root = Rig.NewFolder("servers");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { AutoUpdate = false, Beholder = "https://beholder.test" } }.Save(path);
        var options = new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] };
        await using (var l = new Launcher(path, root, options, net))
        {
            await l.RefreshServersAsync();
            var view = l.Servers;
            Assert.Equal((ServerListState.Ready, 7, 0), (view.State, view.Items.Count, view.Hidden.Count));
            Assert.Equal(["S1", "S2", "S3", "S4", "S5"], view.Items.Take(ServerListView.PageSize).Select(i => i.Name));
            Assert.Equal("key-2", view.Items[1].PublicKey);

            // Hide the second: the sixth is now among the first five.
            Assert.True(await l.HideServerAsync(view.Items[1]));
            view = l.Servers;
            Assert.Equal(["S1", "S3", "S4", "S5", "S6"], view.Items.Take(ServerListView.PageSize).Select(i => i.Name));
            Assert.Equal([new HiddenServer("key-2", "S2", "https://s2.test")], view.Hidden);
            Assert.Equal(6, view.Items.Count);
            Assert.Contains("[[server_list.hidden]]\nid = \"key-2\"\nname = \"S2\"\naddress = \"https://s2.test\"\n", File.ReadAllText(path), StringComparison.Ordinal);

            // Hidden twice is hidden once; a hidden server is not contacted any more.
            Assert.True(await l.HideServerAsync(new ListedServer("S2", "https://s2.test", null, null, 0, 0, 0, null, null) { PublicKey = "key-2" }));
            Assert.Single(l.Servers.Hidden);
            net.Requests.Clear();
            await l.MeasureServersAsync();
            Assert.Equal(["s1.test", "s3.test", "s4.test", "s5.test", "s6.test", "s7.test"], net.Asked());
        }

        // Another start: still hidden, from the file, before the list is even read.
        await using (var again = new Launcher(path, root, options, net))
        {
            Assert.Equal(["key-2"], again.Servers.Hidden.Select(h => h.Id));
            await again.RefreshServersAsync();
            Assert.Equal(["S1", "S3", "S4", "S5", "S6", "S7"], again.Servers.Items.Select(i => i.Name));

            // "Show again": back in its place, with a number of its own, and out of the file.
            net.Requests.Clear();
            await again.ShowServerAsync("key-2");
            Assert.Empty(again.Servers.Hidden);
            Assert.Equal(["s2.test"], net.Asked()); // only the one that had no number yet
            Assert.Equal(["S1", "S2", "S3"], again.Servers.Items.Take(3).Select(i => i.Name));
            Assert.DoesNotContain("server_list", File.ReadAllText(path), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_long_list_contacts_a_bounded_number_of_servers()
    {
        var net = new ManyServers(60, gapMs: 0);
        var root = Rig.NewFolder("servers-many");
        var path = Path.Combine(root, "client.toml");
        // A profile on S58, far down the list: it is measured whatever its place.
        new ClientConfig
        {
            Settings = new LauncherSettings { AutoUpdate = false, Beholder = "https://beholder.test" },
            Servers = [new ServerProfile("https://s58.test", "Ana", "123456")],
        }.Save(path);
        await using var l = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, net);
        await l.RefreshServersAsync();
        var view = l.Servers;
        Assert.Equal((ServerListState.Ready, 60), (view.State, view.Items.Count));
        // 60 listed: the first 50 and the one with a profile are contacted, the other nine are not.
        var asked = net.Asked();
        Assert.Equal(51, asked.Count);
        Assert.Contains("s50.test", asked);
        Assert.Contains("s58.test", asked);
        Assert.DoesNotContain("s51.test", asked);
        // The ones without a number are listed after every one that answered.
        Assert.Equal(51, view.Items.TakeWhile(i => i.Online == true).Count());
        Assert.All(view.Items.Skip(51), i => Assert.Null(i.Online));
        Assert.Equal(9, view.Items.Count - 51);

        // Hiding one of the first 50 lets the 51st into the measured ones: it alone is contacted now.
        net.Requests.Clear();
        Assert.True(await l.HideServerAsync(view.Items.Single(i => i.Name == "S7")));
        Assert.Equal(["s51.test"], net.Asked());
        Assert.True(l.Servers.Items.Single(i => i.Name == "S51").Online);
        Assert.Equal(59, l.Servers.Items.Count);
        // A hidden server is never contacted, and the bound holds.
        net.Requests.Clear();
        await l.MeasureServersAsync();
        Assert.DoesNotContain("s7.test", net.Asked());
        Assert.Equal(51, net.Asked().Count);
    }
    [Fact]
    public async Task The_server_in_use_cannot_be_hidden()
    {
        var net = new ManyServers(6);
        var root = Rig.NewFolder("servers-in-use");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { AutoUpdate = false, Beholder = "https://beholder.test", Server = "https://s3.test", Name = "Ana", Pin = "123456" } }.Save(path);
        await using var l = new Launcher(path, root, new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] }, net);
        await l.RefreshServersAsync();
        var inUse = l.Servers.Items.Single(i => i.Name == "S3");
        Assert.False(await l.HideServerAsync(inUse));
        Assert.Empty(l.Servers.Hidden);
        Assert.Contains(l.Servers.Items, i => i.Name == "S3");
        Assert.DoesNotContain("server_list", File.ReadAllText(path), StringComparison.Ordinal);
        // Any other one can.
        Assert.True(await l.HideServerAsync(l.Servers.Items.Single(i => i.Name == "S4")));
        Assert.Equal(["key-4"], l.Servers.Hidden.Select(h => h.Id));
    }

    private static readonly string[] OnlyTheBeholder = ["beholder.test"];

    [Fact]
    public void Show_closest_off_keeps_the_directory_order_without_latencies()
    {
        ListedServer[] listed = [Server("Far", 240, true), Server("Gone", null, false), Server("Near", 12, true), Server("Waiting", null, null), Server("Mid", 90, true)];
        var plain = ServerRanking.Visible(listed, [new HiddenServer("key-Mid", "Mid", "")], closest: false);
        Assert.Equal(["Far", "Gone", "Near", "Waiting"], plain.Select(s => s.Name));
        // A measure taken while it was on is not shown: no bars, no ms, no "no answer".
        Assert.All(plain, s => Assert.Equal((null, (bool?)null, 0), (s.LatencyMs, s.Online, s.Bars)));
        // On, the same list is nearest first.
        Assert.Equal(["Near", "Mid", "Far", "Waiting", "Gone"], ServerRanking.Visible(listed, [], closest: true).Select(s => s.Name));
    }

    [Fact]
    public void Show_closest_is_kept_in_client_toml_and_on_by_default()
    {
        Assert.True(new LauncherSettings().ClosestServers);
        Assert.True(ClientConfig.Parse("name = \"Ana\"\n").Settings.ClosestServers); // an older file: on, as before
        var off = new ClientConfig { Settings = new LauncherSettings { ClosestServers = false } };
        Assert.Contains("\nclosest_servers = false\n", off.ToToml(), StringComparison.Ordinal);
        Assert.False(ClientConfig.Parse(off.ToToml()).Settings.ClosestServers);
        Assert.Contains("\nclosest_servers = true\n", new ClientConfig().ToToml(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_closest_off_contacts_no_listed_server()
    {
        var net = new ManyServers(7, gapMs: 40, farthestFirst: true); // S7 answers at once, S1 after 240 ms
        var root = Rig.NewFolder("servers-unsorted");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { AutoUpdate = false, Beholder = "https://beholder.test", ClosestServers = false } }.Save(path);
        var options = new LauncherOptions { BindLocalApi = false, BackgroundLoops = false, Targets = [new MkwRecompTarget("1.4.0")] };
        await using var l = new Launcher(path, root, options, net);

        // The list is read from the beholder, and nothing else is asked.
        await l.RefreshServersAsync();
        Assert.Equal(OnlyTheBeholder, net.Requests.Distinct());
        var view = l.Servers;
        Assert.Equal(["S1", "S2", "S3", "S4", "S5", "S6", "S7"], view.Items.Select(i => i.Name)); // the beholder's order
        Assert.All(view.Items, i => Assert.Null(i.LatencyMs));
        // The page's timer, hiding a server, showing it again: still nobody timed.
        net.Requests.Clear();
        await l.MeasureServersAsync();
        Assert.True(await l.HideServerAsync(view.Items[0]));
        await l.ShowServerAsync("key-1");
        Assert.Empty(net.Requests);

        // On: saved, measured at once, nearest first.
        await l.SetClosestServersAsync(true);
        Assert.Contains("\nclosest_servers = true\n", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Equal(7, net.Asked().Count);
        Assert.Equal(["S7", "S6", "S5", "S4", "S3"], l.Servers.Items.Take(ServerListView.PageSize).Select(i => i.Name));
        Assert.All(l.Servers.Items, i => Assert.True(i.Online));

        // Off again: the directory's order at once, the numbers gone, and no more requests.
        await l.SetClosestServersAsync(false);
        Assert.False(l.Settings.ClosestServers);
        Assert.Equal(["S1", "S2", "S3", "S4", "S5"], l.Servers.Items.Take(ServerListView.PageSize).Select(i => i.Name));
        Assert.All(l.Servers.Items, i => Assert.Null(i.LatencyMs));
        net.Requests.Clear();
        await l.MeasureServersAsync();
        await l.RefreshServersAsync();
        Assert.Equal(OnlyTheBeholder, net.Requests.Distinct());
    }

    /// <summary>A beholder listing <c>S1..Sn</c> (<c>https://s1.test</c>…), each answering later than the one before
    /// (<paramref name="farthestFirst"/>: earlier).</summary>
    private sealed class ManyServers(int count, int gapMs = 0, bool farthestFirst = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        /// <summary>The servers contacted (not the beholder), once each, by host.</summary>
        public List<string> Asked()
        {
            lock (Requests)
            {
                return [.. Requests.Where(h => h != "beholder.test").Distinct().Order(StringComparer.Ordinal)];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Requests)
            {
                Requests.Add(uri.Host);
            }
            static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (uri.Host == "beholder.test")
            {
                return Json("{\"items\":[" + string.Join(',', Enumerable.Range(1, count).Select(i =>
                    string.Create(CultureInfo.InvariantCulture, $"{{\"publicKey\":\"key-{i}\",\"name\":\"S{i}\",\"endpoint\":\"https://s{i}.test\",\"capacity\":32}}"))) + "]}");
            }
            var number = int.Parse(uri.Host[1..^5], CultureInfo.InvariantCulture);
            if (uri.AbsolutePath == "/ping")
            {
                await Task.Delay((farthestFirst ? count - number : number - 1) * gapMs, cancellationToken); // the timed request: farther with every number
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return uri.AbsolutePath == "/seat/v1/status"
                ? Json("""{"name":"s","seatsTotal":32,"seated":1,"queued":0,"lobbyEndpoint":"lobby.test:8905","privacy":{"version":"abc","enforced":false}}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
