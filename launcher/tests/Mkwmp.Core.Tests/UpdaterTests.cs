// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Text;
using System.Net;
using System.Text;
using System.Text.Json;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;
using NSec.Cryptography;

namespace Mkwmp.Core.Tests;

/// <summary>A test release key (seed 5 × 32, the Rust tests' key) and a GitHub that serves one release.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public const string TestKey = "t:bnoc3Smwt4_ROvTFWY_v9O8qlxZuPKby5Pv8zYBQW_E";
    public const string Api = "http://github.test";

    private readonly Dictionary<string, byte[]> _files = [];
    private readonly List<object> _releases = [];

    public int Downloads { get; private set; }

    public static string Sign(string text)
    {
        var ed = SignatureAlgorithm.Ed25519;
        using var key = Key.Import(ed, Enumerable.Repeat((byte)5, 32).ToArray(), KeyBlobFormat.RawPrivateKey);
        Assert.Equal(TestKey, "t:" + Base64Url.EncodeToString(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
        return Base64Url.EncodeToString(ed.Sign(key, Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>Adds a release with a signed SHA256SUMS of its files (<paramref name="listed"/>: what the list claims instead).</summary>
    public FakeGitHub Release(string version, Dictionary<string, byte[]> files, Dictionary<string, byte[]>? listed = null, bool draft = false, string? notes = null)
    {
        var sums = $"# mkwmp {version}\n" + string.Concat((listed ?? files).Select(f => $"{Updater.Sha256Hex(f.Value)}  {f.Key}\n"));
        var all = new Dictionary<string, byte[]>(files) { ["SHA256SUMS"] = Encoding.UTF8.GetBytes(sums), ["SHA256SUMS.sig"] = Encoding.UTF8.GetBytes(Sign(sums)) };
        foreach (var (name, bytes) in all)
        {
            _files[$"/dl/{version}/{name}"] = bytes;
        }
        _releases.Add(new { tag_name = $"v{version}", draft, body = notes, assets = all.Keys.Select(n => new { name = n, browser_download_url = $"{Api}/dl/{version}/{n}" }) });
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (request.RequestUri.Host != "github.test")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        if (Down)
        {
            throw new HttpRequestException("no route to github.test");
        }
        if (path == "/repos/anarqz/mkwmp/releases")
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(_releases)) });
        }
        if (Down)
        {
            throw new HttpRequestException("no route to github.test");
        }
        if (_files.TryGetValue(path, out var bytes))
        {
            Downloads++;
            Requests.Add((Path.GetFileName(path), request.Headers.Range?.Ranges.FirstOrDefault()?.From));
            var from = 0;
            var status = HttpStatusCode.OK;
            if (Ranges && request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } start)
            {
                if (start >= bytes.Length)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                }
                (from, status) = ((int)start, HttpStatusCode.PartialContent);
            }
            // The line drops once, after CutAfter bytes of this file: the body ends early, as a closed connection does.
            var cut = CutOnce is { } once && path.EndsWith(once.Name, StringComparison.Ordinal) ? once.After : (int?)null;
            if (cut is not null)
            {
                CutOnce = null;
            }
            HttpContent content = cut is { } after
                ? new StreamContent(new CutStream(bytes.AsSpan(from, Math.Min(after, bytes.Length - from)).ToArray()))
                : new ByteArrayContent(bytes, from, bytes.Length - from);
            content.Headers.ContentLength = bytes.Length - from;
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>GitHub cannot be reached at all.</summary>
    public bool Down { get; set; }

    /// <summary>Range requests are honoured (GitHub's download servers do).</summary>
    public bool Ranges { get; set; } = true;

    /// <summary>The next download of the asset with this name breaks after that many bytes.</summary>
    public (string Name, int After)? CutOnce { get; set; }

    /// <summary>Every asset request: its name and the offset it asked to continue from.</summary>
    public List<(string Name, long? From)> Requests { get; } = [];

    /// <summary>A body that ends with an error after its bytes.</summary>
    private sealed class CutStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = Read(buffer.Span);
            return n > 0 ? ValueTask.FromResult(n) : ValueTask.FromException<int>(new IOException("the connection was closed"));
        }
    }
}

public class UpdaterTests
{
    private const string Sums = "# mkwmp 0.0.2\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  mkwmp-launcher-x86_64-linux\n";
    private static readonly string[] TestKeys = [FakeGitHub.TestKey];

    private static SemVer V(string s) => SemVer.TryParse(s)!;

    [Fact]
    public void Sums_verify()
    {
        var sums = Updater.VerifySums(Encoding.UTF8.GetBytes(Sums), FakeGitHub.Sign(Sums), TestKeys, V("0.0.2"));
        Assert.Equal(64, sums["mkwmp-launcher-x86_64-linux"].Length);
    }

    [Fact]
    public void Sums_reject_tampering_wrong_key_and_wrong_version()
    {
        var bytes = Encoding.UTF8.GetBytes(Sums);
        var sig = FakeGitHub.Sign(Sums);
        var tampered = (byte[])bytes.Clone();
        tampered[20] ^= 1;
        Assert.Equal("SHA256SUMS signature does not verify", Assert.Throws<LauncherException>(() => Updater.VerifySums(tampered, sig, TestKeys, V("0.0.2"))).Message);
        Assert.Throws<LauncherException>(() => Updater.VerifySums(bytes, sig, Updater.ReleaseKeys, V("0.0.2")));
        // A validly signed file replayed under another tag.
        Assert.Contains("not 0.0.3", Assert.Throws<LauncherException>(() => Updater.VerifySums(bytes, sig, TestKeys, V("0.0.3"))).Message, StringComparison.Ordinal);
        Assert.Equal("malformed signature", Assert.Throws<LauncherException>(() => Updater.VerifySums(bytes, "garbage", TestKeys, V("0.0.2"))).Message);
        Assert.Equal("release1:Zuvb-AuF9DcXEzXRSu49EFrb2ilCQnJl1e4ApQJI9Dw", Assert.Single(Updater.ReleaseKeys));
    }

    [Fact]
    public void Newest_only_upgrades()
    {
        static Release Rel(string tag, bool draft) => new(tag, draft, null, []);
        Release[] list = [Rel("v0.0.1-rc.1", false), Rel("v0.0.1-rc.2", false), Rel("v0.0.2", true), Rel("junk", false)];
        Assert.Equal("v0.0.1-rc.2", Updater.Newest(list, V("0.0.1-rc.1"))!.Tag);
        Assert.Null(Updater.Newest(list, V("0.0.1-rc.2")));
        Assert.Null(Updater.Newest(list, V("0.0.1"))); // 0.0.1 > 0.0.1-rc.2: never downgrade
        Assert.True(V("1.0.0-rc.10") > V("1.0.0-rc.9") && V("1.0.0-rc.1") > V("1.0.0-alpha") && V("1.10.0") > V("1.9.9") && V("1.0.0-1") < V("1.0.0-a"));
        Assert.Null(SemVer.TryParse("1.0"));
        Assert.Null(SemVer.TryParse("01.0.0"));
        Assert.Equal("0.1.15-mkwmp", V("0.1.15-mkwmp").ToString());
    }

    [Fact]
    public async Task Downloads_verify_end_to_end_and_a_swapped_asset_is_rejected()
    {
        var bin = "new launcher"u8.ToArray();
        var files = new Dictionary<string, byte[]> { ["mkwmp-launcher-x86_64-linux"] = bin };
        using var http = new HttpClient(new FakeGitHub().Release("0.0.2", files));
        var u = new Updater(http, "test", FakeGitHub.Api, TestKeys);
        var release = Updater.Newest(await u.ReleasesAsync(default), V("0.0.1-rc.1"))!;
        var sums = await u.SumsAsync(release, default);
        var seen = new List<(long, long?)>();
        Assert.Equal(bin, await u.AssetAsync(release, sums, "mkwmp-launcher-x86_64-linux", new Sink(seen.Add), default));
        Assert.Equal((bin.Length, bin.Length), seen[^1]);
        // The built-in key does not trust the test signature.
        await Assert.ThrowsAsync<LauncherException>(() => new Updater(http, "test", FakeGitHub.Api).SumsAsync(release, default));

        using var evil = new HttpClient(new FakeGitHub().Release("0.0.2", new() { ["x"] = "malware"u8.ToArray() }, listed: new() { ["x"] = "the real one"u8.ToArray() }));
        u = new Updater(evil, "test", FakeGitHub.Api, TestKeys);
        release = (await u.ReleasesAsync(default))[0];
        var error = await Assert.ThrowsAsync<LauncherException>(async () => await u.AssetAsync(release, await u.SumsAsync(release, default), "x", null, default));
        Assert.Equal("x: checksum mismatch", error.Message);
    }

    private sealed class Sink(Action<(long, long?)> report) : IProgress<(long Got, long? Total)>
    {
        public void Report((long Got, long? Total) value) => report(value);
    }

    [Fact]
    public void Bundled_patch_needs_its_signed_list()
    {
        var d = Rig.NewFolder("bundle");
        var assets = new MkwRecompTarget("1.0.0").PackageAssets;
        Assert.Null(Launcher.Bundled(d, "1.0.0", TestKeys, assets));
        File.WriteAllText(Path.Combine(d, "winmm.dll"), "p");
        File.WriteAllText(Path.Combine(d, "mkwmp-voice.dll"), "v");
        var list = $"# mkwmp 1.0.0\n{Updater.Sha256Hex("p"u8)}  winmm.dll\n{Updater.Sha256Hex("v"u8)}  mkwmp-voice.dll\n";
        File.WriteAllText(Path.Combine(d, "SHA256SUMS"), list);
        File.WriteAllText(Path.Combine(d, "SHA256SUMS.sig"), FakeGitHub.Sign(list));
        var b = Launcher.Bundled(d, "1.0.0", TestKeys, assets)!;
        Assert.Equal(("1.0.0", Path.Combine(d, "winmm.dll"), Updater.Sha256Hex("v"u8)), (b.Version, b.Files["winmm.dll"], b.Sha256["mkwmp-voice.dll"]));
        Assert.Null(Launcher.Bundled(d, "1.0.1", TestKeys, assets)); // a self-updated launcher ignores the older zip copy
        Assert.Null(Launcher.Bundled(d, "1.0.0", Updater.ReleaseKeys, assets)); // only the release key
        File.WriteAllText(Path.Combine(d, "mkwmp-voice.dll"), "changed");
        Assert.Null(Launcher.Bundled(d, "1.0.0", TestKeys, assets)); // files must match the list
    }

    private static (Launcher Launcher, string Root, string Game, string Exe) Rigged(FakeGitHub github, bool autoApply = false, bool updates = true)
    {
        _ = Rig.TestExe;
        var root = Rig.NewFolder("update");
        var game = Path.Combine(root, "mkw");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "mkw.exe"), Rig.TestExe);
        var home = Path.Combine(root, "launcher");
        Directory.CreateDirectory(home);
        var exe = Path.Combine(home, "mkwmp-launcher.exe");
        File.WriteAllText(exe, "this launcher");
        var path = Path.Combine(root, "client.toml");
        new ClientConfig { Settings = new LauncherSettings { Server = FakeSeatServer.Url, Game = game, AutoUpdate = updates } }.Save(path);
        var launcher = new Launcher(path, home, new LauncherOptions
        {
            Version = "1.4.0",
            Executable = exe,
            UpdateApi = FakeGitHub.Api,
            ReleaseKeys = [FakeGitHub.TestKey],
            Targets = [new MkwRecompTarget("1.4.0") { RunningOverride = () => false }],
            BindLocalApi = false,
            Headless = true, // no stars, no start-up entry
            ApplyUpdatesAutomatically = autoApply,
            Exit = _ => { },
        }, github);
        return (launcher, root, game, exe);
    }

    [Fact]
    public async Task The_game_patch_is_downloaded_from_this_launchers_release_verified_and_kept()
    {
        var probe = Encoding.UTF8.GetBytes("MZ mkwmp probe 1.4.0");
        var github = new FakeGitHub().Release("1.4.0", new() { ["winmm.dll"] = probe, ["mkwmp-voice.dll"] = "voice"u8.ToArray() });
        var (launcher, root, game, _) = Rigged(github);
        await using (launcher)
        {
            Assert.Equal(IntegrationState.Missing, launcher.Game.Integration!.State);
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            Assert.Equal((UpdateState.UpToDate, Strings.UpToDate), (launcher.Update.State, launcher.Update.Status));
            Assert.Equal(probe, File.ReadAllBytes(Path.Combine(game, "winmm.dll")));
            Assert.Equal((IntegrationState.Installed, "Patches installed (v1.4.0)"), (launcher.Game.Integration.State, launcher.Game.Integration.Summary));
            Assert.Equal(TargetStatus.Ready, launcher.Game.Check!.Status);
            // Kept next to the launcher with its signed list: the next sync needs no network.
            Assert.True(File.Exists(Path.Combine(root, "launcher", "game", "SHA256SUMS.sig")));
            var before = github.Downloads;
            await launcher.SyncIntegrationAsync(force: false);
            Assert.Equal(before, github.Downloads);
            await launcher.UninstallIntegrationAsync();
            Assert.Equal((Strings.PatchRemoved, false, false), (launcher.Game.Note, launcher.Game.NoteIsError, launcher.Settings.Patch));
            Assert.Equal(IntegrationState.Missing, launcher.Game.Integration.State);
            await launcher.InstallIntegrationAsync();
            Assert.Equal((Strings.PatchInstalled, true, true), (launcher.Game.Note, launcher.Settings.Patch, launcher.Game.Integration.Ok));
        }
    }

    [Fact]
    public async Task A_newer_launcher_is_offered_then_downloaded_with_progress_and_swapped_in()
    {
        var fresh = new byte[300_000];
        Random.Shared.NextBytes(fresh);
        var github = new FakeGitHub()
            .Release("1.4.0", new() { ["winmm.dll"] = "MZ mkwmp probe 1.4.0"u8.ToArray() })
            .Release("1.5.0", new() { [Updater.LauncherAsset] = fresh }, notes: "What's new")
            .Release("9.9.9", new() { [Updater.LauncherAsset] = fresh }, draft: true);
        var (launcher, _, _, exe) = Rigged(github);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            // Not applied by itself: the window asks first.
            Assert.Equal((UpdateState.Available, "1.5.0", "What's new"), (launcher.Update.State, launcher.Update.AvailableVersion, launcher.Update.Notes));
            Assert.Equal("this launcher", File.ReadAllText(exe));
            var states = new List<UpdateView>();
            launcher.Changed += (_, _) =>
            {
                lock (states)
                {
                    states.Add(launcher.Update);
                }
            };
            // Headless (and a release with only the executable, as before the zip carried the helper): downloaded,
            // verified, swapped in by this process, the old one kept in the staging folder's backup.
            await launcher.ApplyUpdateAsync(default);
            Assert.Equal((UpdateState.Restarting, Strings.UpdatedRestarting, 1.0), (launcher.Update.State, launcher.Update.Status, launcher.Update.Progress));
            Assert.Contains(states, s => s.State == UpdateState.Downloading);
            Assert.Contains(states, s => s.State == UpdateState.ReadyToInstall);
            Assert.Equal(fresh, File.ReadAllBytes(exe));
            Assert.Equal("this launcher", File.ReadAllText(Path.Combine(Path.GetDirectoryName(exe)!, ".mkwmp-selfupdate", "1.5.0", "backup", "mkwmp-launcher.exe")));
        }
    }

    [Fact]
    public async Task A_launcher_whose_download_does_not_verify_is_left_alone_and_updates_can_be_off()
    {
        var github = new FakeGitHub().Release("1.5.0", new() { [Updater.LauncherAsset] = "malware"u8.ToArray() }, listed: new() { [Updater.LauncherAsset] = "the real one"u8.ToArray() });
        var (launcher, _, _, exe) = Rigged(github, autoApply: true);
        await using (launcher)
        {
            await launcher.StartAsync(default);
            await launcher.StartupUpdates;
            Assert.Equal("this launcher", File.ReadAllText(exe));
            Assert.False(File.Exists(exe + ".old"));
            // The failed update is noted; the launcher goes on as it is.
            Assert.Equal(UpdateState.UpToDate, launcher.Update.State);
            await launcher.CheckForUpdatesAsync();
            Assert.Equal(UpdateState.Available, launcher.Update.State);
            await launcher.ApplyUpdateAsync(default);
            Assert.Equal((UpdateState.Failed, Strings.UpdateFailed($"{Updater.LauncherAsset}: checksum mismatch")), (launcher.Update.State, launcher.Update.Status));
        }

        var (off, _, game, _) = Rigged(github, updates: false);
        var before = github.Downloads;
        await using (off)
        {
            await off.StartAsync(default);
            await off.StartupUpdates;
            Assert.Equal((UpdateState.Off, Strings.UpdatesOff), (off.Update.State, off.Update.Status));
            Assert.Equal(before, github.Downloads); // nothing is fetched with updates off
            Assert.False(File.Exists(Path.Combine(game, "winmm.dll")));
        }
    }
}
