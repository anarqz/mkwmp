// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Mkwmp.Core.Net;
using Mkwmp.Core.Overlay;

namespace Mkwmp.Core.Tests;

/// <summary>The console-style overlay's side of the local API ("The overlay" in docs/contracts/LAUNCHER_API.md) and the overlay host.</summary>
public class OverlayTests
{
    private const string Auth = "Bearer t0ken";

    private sealed class Host : ILocalApiHost
    {
        public string Token => "t0ken";
        public SeatSession? Seat => null;
        public IReadOnlyList<Friend>? FriendsOrNull => null;
        public ClientConfig Config { get; } = new();

        public string? EditVoice(Func<ClientConfig, ClientConfig> edit) => null;
    }

    private static byte[] Rgba(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (r, g, b, 7);
        }
        return px;
    }

    private static JsonNode Body(LocalResponse r) => JsonNode.Parse(r.Body!)!;

    [Fact]
    public async Task Nothing_opens_until_the_shell_is_ready()
    {
        using var api = new LocalApi(new Host(), TimeProvider.System);
        var state = await api.HandleAsync("GET", "/local/v1/overlay/state", "", Auth, default, default);
        Assert.Equal((200, """{"open":false,"seq":0,"enabled":false}"""), (state.Status, state.Body));
        // Enabled without anyone listening is still no overlay, and so is a listener that is not enabled.
        api.Overlay.Enabled = true;
        Assert.False(Body(await api.HandleAsync("GET", "/local/v1/overlay/state", "", Auth, default, default))["enabled"]!.GetValue<bool>());
        api.Overlay.Enabled = false;
        api.Overlay.Requested += (_, _) => Assert.Fail("not enabled");
        var refused = await api.HandleAsync("POST", "/local/v1/overlay/open", "by=guide&pid=7", Auth, default, default);
        Assert.Equal((409, """{"open":false,"seq":0,"enabled":false}"""), (refused.Status, refused.Body));
        Assert.False(api.Overlay.IsOpen);
        Assert.False(api.Overlay.RequestFromLauncher(7, null));
        Assert.True(api.Overlay.GameListening);
    }

    [Fact]
    public async Task Open_hands_the_picture_over_and_the_return_ends_the_wait()
    {
        using var api = new LocalApi(new Host(), TimeProvider.System);
        var requests = new List<OverlayRequest>();
        var closed = new List<OverlayReturn>();
        api.Overlay.Requested += (_, r) => requests.Add(r);
        api.Overlay.Closed += (_, how) => closed.Add(how);
        api.Overlay.Enabled = true;

        var opened = await api.HandleAsync("POST", "/local/v1/overlay/open", "by=hold&pid=4242&w=32&h=18&format=rgba", Auth, Rgba(32, 18, 10, 20, 30), default);
        Assert.Equal((200, """{"open":true,"seq":1,"enabled":true}"""), (opened.Status, opened.Body));
        var request = Assert.Single(requests);
        Assert.Equal((OverlayTrigger.Hold, 4242, false), (request.By, request.GamePid, request.Again));
        Assert.Equal((32, 18, 128), (request.Frame!.Width, request.Frame.Height, request.Frame.Stride));
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, request.Frame.Bgra[..4]); // blue first, opaque
        Assert.Same(request.Frame, api.Overlay.LastFrame);
        Assert.Equal((true, 4242), (api.Overlay.IsOpen, api.Overlay.GamePid));

        // The mod waits for a change of state 1; nothing changes for a while.
        var wait = api.HandleAsync("GET", "/local/v1/overlay/state", "wait=20&seq=1", Auth, default, default);
        await Task.Delay(150);
        Assert.False(wait.IsCompleted);
        // A second press while it is open (the game took the focus back): the same overlay, told as "again", the old picture kept.
        var again = await api.HandleAsync("POST", "/local/v1/overlay/open", "by=guide", Auth, default, default);
        Assert.Equal((200, """{"open":true,"seq":1,"enabled":true}"""), (again.Status, again.Body));
        Assert.Equal((2, OverlayTrigger.Guide, true, null), (requests.Count, requests[1].By, requests[1].Again, requests[1].Frame));
        Assert.Same(request.Frame, api.Overlay.LastFrame);
        Assert.False(wait.IsCompleted);

        Assert.True(api.Overlay.ReturnToGame(OverlayReturn.Menu));
        var done = await wait.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal((200, """{"open":false,"seq":2,"enabled":true,"action":"menu"}"""), (done.Status, done.Body));
        Assert.Equal([OverlayReturn.Menu], closed);
        Assert.False(api.Overlay.ReturnToGame()); // already back
        // A poll that knows an older state is answered at once; one without wait too.
        var stale = await api.HandleAsync("GET", "/local/v1/overlay/state", "wait=20&seq=1", Auth, default, default);
        Assert.Equal(2, Body(stale)["seq"]!.GetValue<long>());
        // A wait that nothing ends comes back with the same state.
        var quiet = await api.HandleAsync("GET", "/local/v1/overlay/state", "wait=1&seq=2", Auth, default, default);
        Assert.Equal("""{"open":false,"seq":2,"enabled":true,"action":"menu"}""", quiet.Body);

        // The launcher's own trigger opens it the same way; the game going away closes it and forgets the picture.
        Assert.True(api.Overlay.RequestFromLauncher(4242, null));
        Assert.Equal((OverlayTrigger.Launcher, true), (requests[^1].By, api.Overlay.IsOpen));
        api.Overlay.GameExited();
        Assert.Equal((false, null, 0), (api.Overlay.IsOpen, api.Overlay.LastFrame, api.Overlay.GamePid));
        Assert.Equal([OverlayReturn.Menu, OverlayReturn.Resume], closed);
    }

    [Fact]
    public async Task Bad_requests_are_refused()
    {
        using var api = new LocalApi(new Host(), TimeProvider.System);
        api.Overlay.Requested += (_, _) => { };
        api.Overlay.Enabled = true;
        foreach (var (query, body) in new (string, byte[])[]
        {
            ("", []), ("by=touch", []), ("by=guide&pid=x", []),
            ("by=guide&w=32&h=18&format=rgba", new byte[10]), // not w*h*4
            ("by=guide&w=32&h=18", Rgba(32, 18, 1, 2, 3)), // no format
            ("by=guide&w=8&h=8&format=rgba", Rgba(8, 8, 1, 2, 3)), // too small
            ("by=guide&w=32&h=18&format=png", Rgba(32, 18, 1, 2, 3)),
        })
        {
            var r = await api.HandleAsync("POST", "/local/v1/overlay/open", query, Auth, body, default);
            Assert.Equal((400, "invalid"), (r.Status, Body(r)["state"]!.GetValue<string>()));
        }
        Assert.False(api.Overlay.IsOpen);
        Assert.Equal(400, (await api.HandleAsync("GET", "/local/v1/overlay/state", "wait=soon", Auth, default, default)).Status);
        Assert.Equal(400, (await api.HandleAsync("GET", "/local/v1/overlay/state", "wait=1&seq=-1", Auth, default, default)).Status);
        Assert.Equal(401, (await api.HandleAsync("POST", "/local/v1/overlay/open", "by=guide", "Bearer nope", default, default)).Status);
        Assert.Equal(401, (await api.HandleAsync("GET", "/local/v1/overlay/state", "", null, default, default)).Status);
        Assert.Equal(405, (await api.HandleAsync("GET", "/local/v1/overlay/open", "", Auth, default, default)).Status);
        Assert.Equal(405, (await api.HandleAsync("POST", "/local/v1/overlay/state", "", Auth, default, default)).Status);
        // A black picture (a capture that got nothing) opens the overlay without a thumbnail.
        var black = await api.HandleAsync("POST", "/local/v1/overlay/open", "by=keys&w=32&h=18&format=rgba", Auth, Rgba(32, 18, 0, 0, 0), default);
        Assert.Equal((200, null), (black.Status, api.Overlay.LastFrame));
    }

    [Fact]
    public async Task A_full_picture_crosses_the_socket_only_with_the_token()
    {
        using var api = new LocalApi(new Host(), TimeProvider.System);
        api.Listen(0);
        OverlayRequest? got = null;
        api.Overlay.Requested += (_, r) => got = r;
        api.Overlay.Enabled = true;
        using var http = new HttpClient();
        var picture = Rgba(1280, 720, 200, 100, 50); // 3.7 MB: over the 256 KiB of every other route
        async Task<int> Post(string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{api.Port}/local/v1/overlay/open?by=chord&pid=9&w=1280&h=720&format=rgba");
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            request.Content = new ByteArrayContent(picture);
            try
            {
                using var response = await http.SendAsync(request);
                return (int)response.StatusCode;
            }
            catch (HttpRequestException)
            {
                return 413; // refused and closed before the body was read
            }
        }
        Assert.Equal(413, await Post("guess"));
        Assert.Equal(413, await Post(null));
        Assert.Null(got);
        Assert.Equal(200, await Post("t0ken"));
        Assert.Equal((OverlayTrigger.Chord, 9, 1280, 720), (got!.By, got.GamePid, got.Frame!.Width, got.Frame.Height));
        Assert.Equal(new byte[] { 50, 100, 200, 255 }, got.Frame.Bgra[^4..]);
    }

    [Fact]
    public void Host_arguments_and_the_launcher_record()
    {
        var folder = Directory.CreateTempSubdirectory("mkwmp-overlay-").FullName;
        try
        {
            var session = Path.Combine(folder, OverlayHostArgs.SessionFile);
            Assert.Equal(new OverlayHostArgs(1234, session), OverlayHostArgs.Parse(["--overlay-host", "--parent-pid", "1234", "--session", session]));
            Assert.Null(OverlayHostArgs.Parse(["--parent-pid", "1234", "--session", session])); // not asked
            Assert.Null(OverlayHostArgs.Parse(["--overlay-host", "--session", session]));
            Assert.Null(OverlayHostArgs.Parse(["--overlay-host", "--parent-pid", "0", "--session", session]));
            Assert.Null(OverlayHostArgs.Parse(["--overlay-host", "--parent-pid", "5", "--session", Path.Combine(folder, "client.toml")])); // only its own file name
            Assert.Null(OverlayHostArgs.Parse(["--overlay-host", "--parent-pid", "5", "--session", Path.Combine(folder, "missing", OverlayHostArgs.SessionFile)]));

            var path = Path.Combine(folder, "sub", "launcher.json");
            var record = new LauncherRecord("C:/Games/MKWMP/mkwmp-launcher.exe", 77, 47820, "abc");
            Assert.True(record.Write(path));
            Assert.Equal("""{"exe":"C:/Games/MKWMP/mkwmp-launcher.exe","pid":77,"port":47820,"token":"abc"}""", File.ReadAllText(path));
            Assert.Equal(record, LauncherRecord.Read(path));
            File.WriteAllText(path, """{"exe":"x"}""");
            Assert.Null(LauncherRecord.Read(path));
            Assert.Null(LauncherRecord.Read(Path.Combine(folder, "none.json")));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task The_host_writes_the_session_and_leaves_with_the_game()
    {
        var folder = Directory.CreateTempSubdirectory("mkwmp-overlay-").FullName;
        using var game = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause") { RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true })!;
        try
        {
            using var api = new LocalApi(new Host(), TimeProvider.System);
            api.Listen(0);
            var session = Path.Combine(folder, OverlayHostArgs.SessionFile);
            var exited = new TaskCompletionSource();
            var shown = 0;
            api.Instance.ParentExited += (_, _) => exited.TrySetResult();
            api.Instance.ShowRequested += (_, _) => shown++;

            // What a second launcher does: it finds the running one in the record and hands its request over.
            var running = new LauncherRecord("x", Environment.ProcessId, api.Port, "t0ken");
            Assert.False(await SingleInstance.HandOffAsync(null, running with { Token = "wrong" }));
            Assert.True(await SingleInstance.HandOffAsync(null, running));
            Assert.Equal(1, shown);
            Assert.False(await SingleInstance.HandOffAsync(new OverlayHostArgs(game.Id, Path.Combine(folder, "other.toml")), running));
            Assert.False(File.Exists(session));
            Assert.True(await SingleInstance.HandOffAsync(new OverlayHostArgs(game.Id, session), running));
            Assert.Equal($"launcher = \"127.0.0.1:{api.Port}\"\nlauncher_token = \"t0ken\"\n", File.ReadAllText(session));
            Assert.Equal(game.Id, api.Instance.ParentPid);
            Assert.False(exited.Task.IsCompleted);

            game.Kill();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(session));
            Assert.Equal(0, api.Instance.ParentPid);
        }
        finally
        {
            if (!game.HasExited)
            {
                game.Kill();
            }
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task One_launcher_per_user()
    {
        var name = $"mkwmp-test-{Guid.NewGuid():N}";
        using var first = SingleInstance.TryAcquire(name);
        Assert.NotNull(first);
        // A mutex is re-entrant on its own thread: the second launcher is another one.
        Assert.Null(await Task.Factory.StartNew(() => SingleInstance.TryAcquire(name), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }
}
