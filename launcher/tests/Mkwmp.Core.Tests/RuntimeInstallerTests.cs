// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core.Tests;

/// <summary>A release host inside the test: one file at one address, with Range, and the ways a download goes wrong.</summary>
internal sealed class FakeReleaseHost : HttpMessageHandler
{
    public const string Url = "http://runtime.test/adriadam10/mkw-recomp/releases/download/v0.1.18/runtime.zip";

    public byte[] File { get; set; } = [];

    /// <summary>The body breaks (or, with <see cref="Hang"/>, stops) after this many bytes of the next answer; then answers are whole again.</summary>
    public int? CutAfter { get; set; }

    public bool Hang { get; set; }

    /// <summary>A server without Range: always the whole file, 200.</summary>
    public bool IgnoreRange { get; init; }

    /// <summary>Every request, as "GET url" and its Range start (null: none).</summary>
    public List<(string Request, long? From)> Requests { get; } = [];

    /// <summary>Done when a <see cref="Hang"/> answer has given its bytes and waits.</summary>
    public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        lock (Requests)
        {
            Requests.Add(($"{request.Method} {request.RequestUri}", from));
        }
        if (request.RequestUri!.ToString() != Url)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
        var start = IgnoreRange ? 0 : from ?? 0;
        if (start >= File.Length && start > 0)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
        }
        var rest = File.AsSpan((int)start).ToArray();
        var (cut, hang) = (CutAfter, Hang);
        CutAfter = null;
        var content = new StreamContent(new BrokenStream(rest, cut ?? int.MaxValue, hang, Stalled));
        content.Headers.ContentLength = rest.Length;
        var response = new HttpResponseMessage(start > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        if (start > 0)
        {
            content.Headers.ContentRange = new ContentRangeHeaderValue(start, File.Length - 1, File.Length);
        }
        return Task.FromResult(response);
    }

    /// <summary>Gives <paramref name="limit"/> bytes in small pieces, then fails like a dropped connection, or never answers again.</summary>
    private sealed class BrokenStream(byte[] bytes, int limit, bool hang, TaskCompletionSource stalled) : Stream
    {
        private int _at;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at >= limit && _at < bytes.Length)
            {
                if (hang)
                {
                    stalled.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                throw new IOException("the connection was reset (the fake pulled the plug)");
            }
            var n = Math.Min(Math.Min(buffer.Length, 1000), Math.Min(limit, bytes.Length) - _at);
            bytes.AsSpan(_at, n).CopyTo(buffer.Span);
            _at += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>A folder, a fake release and an installer on it. The "runtime" is a few small files; mkw.exe stand-ins are known to <see cref="Recomp"/> by hash.</summary>
internal sealed class RuntimeRig : IDisposable
{
    public const string Pinned = "0.1.18";

    /// <summary>The pin before this one, whose manifest kept setup\ whole on an update.</summary>
    public const string PreviousPin = "0.1.15-mkwmp";

    public static readonly byte[] NewExe = "mkw.exe of the pinned fork build"u8.ToArray();
    public static readonly byte[] OldForkExe = "mkw.exe of an older fork build"u8.ToArray();
    public static readonly byte[] PreviousPinExe = "mkw.exe of the previous pin, 0.1.15-mkwmp"u8.ToArray();
    /// <summary>An official release the mod never ran on (no plugin API): the player's own, never updated.</summary>
    public static readonly byte[] UpstreamExe = "mkw.exe of upstream 0.1.13"u8.ToArray();

    /// <summary>The official 0.1.17 installed by hand (no marker): updated in place to the pin.</summary>
    public static readonly byte[] OfficialExe = "mkw.exe of upstream 0.1.17, installed by hand"u8.ToArray();

    /// <summary>The fork's last build, which the launcher pinned before the official 0.1.17 (a point release of the fork).</summary>
    public static readonly byte[] ForkPointExe = "mkw.exe of the fork's 0.1.16-mkwmp.1"u8.ToArray();

    /// <summary>The pinned version's mkw.exe from the release's no-AVX2 zip (<see cref="NewExe"/> is the main zip's).</summary>
    public static readonly byte[] NoAvx2Exe = "mkw.exe of the pinned fork build, for CPUs without AVX2"u8.ToArray();

    static RuntimeRig()
    {
        Recomp.TestExes[Updater.Sha256Hex(NewExe)] = Pinned;
        Recomp.TestExes[Updater.Sha256Hex(NoAvx2Exe)] = Pinned;
        Recomp.TestExes[Updater.Sha256Hex(OldForkExe)] = "0.1.14-mkwmp";
        Recomp.TestExes[Updater.Sha256Hex(PreviousPinExe)] = PreviousPin;
        Recomp.TestExes[Updater.Sha256Hex(UpstreamExe)] = "0.1.13";
        Recomp.TestExes[Updater.Sha256Hex(OfficialExe)] = "0.1.17";
        Recomp.TestExes[Updater.Sha256Hex(ForkPointExe)] = "0.1.16-mkwmp.1";
    }

    /// <summary>What the release zip holds by default: under one folder, as the real one.</summary>
    public static readonly (string Name, string Text)[] Release =
    [
        ("mkw-recomp/mkw.exe", Encoding.UTF8.GetString(NewExe)),
        ("mkw-recomp/SDL3.dll", "new sdl"),
        ("mkw-recomp/libopus-0.dll", "opus"),
        ("mkw-recomp/licenses/mkw-recomp-LICENSE", "MIT"),
        ("mkw-recomp/setup/python/python.exe", "new python"),
        ("mkw-recomp/setup/src/recompile.py", "new script"),
        ("mkw-recomp/mkw.ini", "shipped = 1\n"),
    ];

    public RuntimeRig(IEnumerable<(string Name, string Text)>? entries = null, Func<RuntimeManifest, RuntimeManifest>? edit = null)
    {
        Root = Rig.NewFolder("runtime");
        Folder = Path.Combine(Root, "install");
        Cache = Path.Combine(Root, "downloads");
        Zip = MakeZip(entries ?? Release);
        Host = new FakeReleaseHost { File = Zip };
        // The main build, whatever this machine's processor: a test of the other one says so (NoAvx2Rig).
        var manifest = RecompRuntime.Main with
        {
            Asset = "runtime.zip",
            Size = Zip.Length,
            Sha256 = Updater.Sha256Hex(Zip),
            ExecutableSha256 = Updater.Sha256Hex(NewExe),
            InstalledSize = 100,
            SetupSpace = 1000,
            UrlOverride = new Uri(FakeReleaseHost.Url),
        };
        Manifest = edit?.Invoke(manifest) ?? manifest;
        Http = new HttpClient(Host);
        Installer = New();
    }

    public string Root { get; }
    public string Folder { get; }
    public string Cache { get; }
    public byte[] Zip { get; }
    public FakeReleaseHost Host { get; }
    public RuntimeManifest Manifest { get; }
    public HttpClient Http { get; }
    public RuntimeInstaller Installer { get; private set; }
    public bool Running { get; set; }
    public long? Free { get; set; }

    /// <summary>What the target says of a folder; a test may stop recognising the new build.</summary>
    public Func<string, string?> Identify { get; set; } = folder => new MkwRecompTarget("1.4.0").Detect(folder)?.Version;

    /// <summary>As the launcher has it on a processor without AVX2: the release's no-AVX2 zip, and the main build's mkw.exe unsuitable.</summary>
    public static RuntimeRig NoAvx2Rig() => new(
        Release.Select(e => e.Name == "mkw-recomp/mkw.exe" ? (e.Name, Encoding.UTF8.GetString(NoAvx2Exe)) : e).Append(("mkw-recomp/setup/cpu-level", "v2\n")),
        m => RecompRuntime.For(v3: false) with
        {
            Asset = m.Asset,
            Size = m.Size,
            Sha256 = m.Sha256,
            ExecutableSha256 = Updater.Sha256Hex(NoAvx2Exe),
            InstalledSize = m.InstalledSize,
            SetupSpace = m.SetupSpace,
            UrlOverride = m.UrlOverride,
            Unsuitable = [Updater.Sha256Hex(NewExe)],
        });

    public RuntimeInstaller New() => Installer = new RuntimeInstaller(Manifest, Http, "mkwmp-test", Cache, new RuntimeHost
    {
        Identify = f => Identify(f),
        IsRunning = _ => Running,
        FreeSpace = _ => Free,
    });

    public static byte[] MakeZip(IEnumerable<(string Name, string Text)> entries)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, text) in entries)
            {
                using var to = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                to.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return bytes.ToArray();
    }

    public string At(string relative) => Path.Combine(Folder, relative);

    public string? Read(string relative) => File.Exists(At(relative)) ? File.ReadAllText(At(relative)) : null;

    public void Put(string relative, string text, string? folder = null)
    {
        var path = Path.Combine(folder ?? Folder, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>An install as a player has it after playing: the runtime, the toolchain, their settings and saves, and what the setup built from their game.</summary>
    public void ExistingInstall(byte[] exe, string? folder = null)
    {
        Put("mkw.exe", Encoding.UTF8.GetString(exe), folder);
        Put("SDL3.dll", "old sdl", folder);
        Put("libopus-0.dll", "opus", folder);
        Put("setup/python/python.exe", "old python", folder);
        Put("setup-state.json", """{"done":{"generate":"c0dd6cf12c07c23a 1.8.0 x"}}""", folder);
        Put("mkw.ini", "[launcher]\nmods = mkwmp\n", folder);
        Put("profile/save.bin", "save", folder);
        Put("mods/mkwmp/plugin/mkwmp.dll", "mod", folder);
        Put("logs/mkw.log", "log", folder);
        Put("mkwgame.dll", "built from the player's game", folder);
        Put("mkwgame.id", "c0dd6cf12c07c23a", folder);
        Put("pipeline-cache.bin", "cache", folder);
        Put("spirv/a.spv", "shader", folder);
    }

    /// <summary>Every file under the install (relative, sorted) with its text: to compare before and after.</summary>
    public Dictionary<string, string> Snapshot(string? folder = null)
    {
        var root = folder ?? Folder;
        return !Directory.Exists(root)
            ? []
            : Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText, StringComparer.Ordinal);
    }

    public Task<RuntimeInstallResult> Install(RuntimeInstallOptions? options = null, IProgress<RuntimeProgress>? progress = null, CancellationToken cancel = default) =>
        Installer.InstallAsync(Folder, options, progress, cancel);

    public void Dispose()
    {
        Http.Dispose();
        Host.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public class RuntimeManifestTests
{
    [Fact]
    public void The_pinned_runtime_is_one_official_release_and_never_the_latest()
    {
        var m = RecompRuntime.For(v3: true);
        Assert.Same(RecompRuntime.Main, m);
        Assert.Null(m.Variant);
        Assert.Equal("0.1.18", m.Label);
        Assert.Empty(m.Unsuitable);
        Assert.Equal("0.1.18", m.Version);
        Assert.Equal("adriadam10/mkw-recomp", m.Repository);
        Assert.Equal("v0.1.18", m.Tag);
        Assert.Equal("mkw-recomp-0.1.18-windows-x64.zip", m.Asset);
        Assert.Equal(221_949_886, m.Size);
        Assert.Equal("ff655d6dc566d315c9f33f96b9b94b7dc7e9c1b383091ed36c7f35921b6444c8", m.Sha256);
        Assert.Equal("84874fe56ea156ee7e10095d9f1c70a3877a5c8f280feb43ecc5c9a94678ce06", m.ExecutableSha256);
        Assert.Equal(741_365_270, m.InstalledSize);
        Assert.Equal(3_500_000_000, m.SetupSpace);
        Assert.Equal("https://github.com/adriadam10/mkw-recomp/tree/v0.1.18", m.Credit.SourceUrl.ToString());
        // The address names the tag and the file: no "latest", no API that lists releases.
        Assert.Equal("https://github.com/adriadam10/mkw-recomp/releases/download/v0.1.18/mkw-recomp-0.1.18-windows-x64.zip", m.Url.ToString());
        Assert.DoesNotContain("latest", m.Url.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.github.com", m.Url.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_processor_without_AVX2_gets_the_same_release_s_noavx2_zip()
    {
        var m = RecompRuntime.For(v3: false);
        var main = RecompRuntime.Main;
        Assert.Same(RecompRuntime.NoAvx2, m);
        Assert.Equal((main.Version, main.Repository, main.Tag), (m.Version, m.Repository, m.Tag));
        Assert.Equal("mkw-recomp-0.1.18-windows-x64-noavx2.zip", m.Asset);
        Assert.Equal(221_839_821, m.Size);
        Assert.Equal("7ce3148d23a2fe255ee36536bd24e027bc4313c54eff50a590e73cb33fa32ccc", m.Sha256);
        Assert.Equal("38b545ce7f017183c8919f958311e3d6b6083c61326f27b95d4a6fcff4513f83", m.ExecutableSha256);
        Assert.Equal(741_077_529, m.InstalledSize);
        Assert.Equal("https://github.com/adriadam10/mkw-recomp/releases/download/v0.1.18/mkw-recomp-0.1.18-windows-x64-noavx2.zip", m.Url.ToString());
        // Both builds' mkw.exe are this version to the launcher; the main one does not run where this one is pinned.
        Assert.Contains((m.Version, m.ExecutableSha256), Recomp.Known);
        Assert.Equal([main.ExecutableSha256], m.Unsuitable);
        Assert.Equal("0.1.18 (no AVX2)", m.Label);
        Assert.Equal((main.Preserved, main.Credit, main.FolderName, main.SetupSpace), (m.Preserved, m.Credit, m.FolderName, m.SetupSpace));
        Assert.True(m.SameLineage("0.1.16-mkwmp"));
    }

    [Fact]
    public void The_pin_agrees_with_what_the_launcher_recognises_and_recommends()
    {
        var m = RecompRuntime.Pinned;
        Assert.Equal(Recomp.Recommended, m.Version);
        Assert.Contains(m.Version, Recomp.Supported);
        Assert.Contains((m.Version, m.ExecutableSha256), Recomp.Known);
        Assert.Equal(Recomp.Exe, m.Executable);
        Assert.EndsWith(m.Tag, Recomp.Download, StringComparison.Ordinal);
        Assert.Same(m, new MkwRecompTarget("1.4.0").Runtime);
        Assert.True(m.SameLineage("0.1.15-mkwmp"));
        Assert.True(m.SameLineage("0.1.16-mkwmp"));
        // the fork's last build, which this launcher installed before: updated in place to the official release
        Assert.True(m.SameLineage("0.1.16-mkwmp.1"));
        Assert.True(m.SameLineage("0.1.18"));
        // an official release the mod ran on, installed by hand, is updated in place too: the launcher runs only the pinned one
        Assert.True(m.SameLineage("0.1.15"));
        Assert.True(m.SameLineage("0.1.16"));
        Assert.True(m.SameLineage("0.1.17"));
        Assert.True(RecompRuntime.NoAvx2.SameLineage("0.1.17"));
        // one without the plugin API stays the player's
        Assert.False(m.SameLineage("0.1.13"));
        Assert.False(m.SameLineage("0.1.10"));
        Assert.False(m.SameLineage("0.1.16-mkwmpx"));
        Assert.False(m.SameLineage("0.1.16-mkwmp."));
        Assert.False(m.SameLineage(null));
    }

    [Fact]
    public void The_credit_carries_the_MIT_notice_of_mkw_recomp()
    {
        var c = RecompRuntime.Pinned.Credit;
        Assert.Equal("MIT", c.License);
        Assert.Contains("Copyright (c) 2026 Adrià", c.Notice, StringComparison.Ordinal);
        Assert.Contains("Permission is hereby granted, free of charge", c.Notice, StringComparison.Ordinal);
        Assert.Contains("The above copyright notice and this permission notice shall be included", c.Notice, StringComparison.Ordinal);
        Assert.Contains("mkw-recomp", c.Summary, StringComparison.Ordinal);
        Assert.Contains("Adrià", c.Summary, StringComparison.Ordinal);
        Assert.Equal("https://github.com/adriadam10/mkw-recomp", c.ProjectUrl.ToString());
        Assert.Equal("Adrià", c.Author);
        Assert.Equal("https://github.com/adriadam10", c.AuthorUrl?.ToString());
        Assert.Equal("https://github.com/adriadam10/mkw-recomp/blob/main/LICENSE", c.LicenseUrl?.ToString());
    }

    [Theory]
    [InlineData("setup/zig/zig.exe", false)] // setup\ changed from 0.1.15-mkwmp to 0.1.16-mkwmp: the runtime's, replaced
    [InlineData("SETUP\\python\\python.exe", false)]
    [InlineData("setup-work/obj/a.o", true)]
    [InlineData("setup-state.json", true)]
    [InlineData("mkw.ini", true)]
    [InlineData("profile/save.bin", true)]
    [InlineData("mods/mkwmp/mod.json", true)]
    [InlineData("logs/mkw.log", true)]
    [InlineData("mkwgame.dll", true)]
    [InlineData("pipeline-cache.bin", true)]
    [InlineData("spirv-renodx/x.spv", true)]
    [InlineData("seq_nodes-180.tsv", true)]
    [InlineData("mkw.exe", false)]
    [InlineData("SDL3.dll", false)]
    [InlineData("licenses/SDL3/LICENSE.txt", false)]
    [InlineData("setupx/file", false)]
    public void An_update_keeps_the_players_files_and_what_was_built_from_their_game(string path, bool preserved) =>
        Assert.Equal(preserved, RecompRuntime.Pinned.IsPreserved(path));
}

public class RuntimeInstallerTests
{
    private static readonly string[] Swapped = ["SDL3.dll", "mkw.exe", Path.Combine("setup", "python", "python.exe")];

    [Fact]
    public async Task A_new_install_asks_only_for_the_pinned_asset_and_is_recognised()
    {
        using var rig = new RuntimeRig();
        var phases = new List<RuntimePhase>();
        var result = await rig.Install(progress: new Collect(p => phases.Add(p.Phase)));

        Assert.All(rig.Host.Requests, r => Assert.Equal($"GET {FakeReleaseHost.Url}", r.Request));
        Assert.Single(rig.Host.Requests);
        Assert.False(result.Updated);
        Assert.Equal(RuntimeRig.Pinned, result.Version);
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
        // The zip's single top folder is not part of the install.
        Assert.Equal("new sdl", rig.Read("SDL3.dll"));
        Assert.Equal("new python", rig.Read("setup/python/python.exe"));
        Assert.Equal("shipped = 1\n", rig.Read("mkw.ini"));
        Assert.False(Directory.Exists(rig.At("mkw-recomp")));
        Assert.Equal(RuntimeRig.Pinned, RuntimeInstaller.ManagedVersion(rig.Folder));
        Assert.Equal(RuntimeFolderKind.Managed, rig.Installer.Classify(rig.Folder));
        // Nothing of the work is left: no staging, no backup, no 200 MB zip.
        Assert.False(Directory.Exists(rig.At(RuntimeInstaller.WorkFolder)));
        Assert.False(Directory.Exists(rig.At(RuntimeInstaller.BackupFolder)));
        Assert.Empty(Directory.EnumerateFiles(rig.Cache));
        Assert.Equal(new[] { RuntimePhase.Downloading, RuntimePhase.Verifying, RuntimePhase.Extracting, RuntimePhase.Installing }, phases.Distinct());

        // Asked again: it is already the pinned build, so no download at all.
        var again = await rig.Install();
        Assert.Single(rig.Host.Requests);
        Assert.Equal((0, 0), (again.Added, again.Replaced));
    }

    [Fact]
    public async Task A_download_with_another_checksum_is_thrown_away_and_nothing_is_installed()
    {
        using var rig = new RuntimeRig();
        var tampered = (byte[])rig.Zip.Clone();
        tampered[^30] ^= 1; // same size, another file
        rig.Host.File = tampered;

        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        Assert.Contains("checksum", e.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(rig.Folder));
        Assert.False(File.Exists(rig.Installer.PartPath));
        Assert.False(File.Exists(rig.Installer.ArchivePath));

        // The real file afterwards installs.
        rig.Host.File = rig.Zip;
        await rig.Install();
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
    }

    [Fact]
    public async Task A_file_of_another_size_is_refused_before_it_is_downloaded()
    {
        using var rig = new RuntimeRig();
        rig.Host.File = [.. rig.Zip, 1, 2, 3];
        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        Assert.Contains("not the file this MKWMP expects", e.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(rig.Installer.PartPath));
        Assert.False(Directory.Exists(rig.Folder));
    }

    [Fact]
    public async Task A_broken_download_continues_from_where_it_stopped()
    {
        using var rig = new RuntimeRig();
        rig.Host.CutAfter = 300;
        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        Assert.StartsWith("download runtime.zip:", e.Message, StringComparison.Ordinal);
        Assert.Equal(300, new FileInfo(rig.Installer.PartPath).Length);
        Assert.Equal(300, rig.Installer.Downloaded);
        Assert.False(Directory.Exists(rig.Folder));

        // Another launcher run: a new installer on the same cache folder.
        var first = new List<RuntimeProgress>();
        rig.New();
        await rig.Install(progress: new Collect(p =>
        {
            if (p.Phase == RuntimePhase.Downloading)
            {
                first.Add(p);
            }
        }));
        Assert.Equal(new long?[] { null, 300 }, rig.Host.Requests.Select(r => r.From));
        Assert.Equal(300, first[0].Done);
        Assert.Equal(rig.Zip.Length, first[^1].Done);
        Assert.Equal(TimeSpan.Zero, first[^1].Remaining);
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
    }

    [Fact]
    public async Task A_server_without_range_starts_the_file_over()
    {
        using var rig = new RuntimeRig();
        rig.Host.CutAfter = 300;
        await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        using var plain = new FakeReleaseHost { File = rig.Zip, IgnoreRange = true };
        using var http = new HttpClient(plain);
        var installer = new RuntimeInstaller(rig.Manifest, http, "mkwmp-test", rig.Cache, new RuntimeHost { Identify = rig.Identify });
        await installer.InstallAsync(rig.Folder, null, null, default);
        Assert.Equal(300L, plain.Requests.Single().From); // it asked to continue; the answer was the whole file
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
    }

    [Fact]
    public async Task Cancelling_keeps_the_partial_download()
    {
        using var rig = new RuntimeRig();
        (rig.Host.CutAfter, rig.Host.Hang) = (300, true);
        using var cancel = new CancellationTokenSource();
        var install = rig.Install(progress: new Collect(p =>
        {
            if (p.Done >= 300)
            {
                cancel.Cancel();
            }
        }), cancel: cancel.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => install);
        Assert.Equal(300, new FileInfo(rig.Installer.PartPath).Length);
        Assert.False(Directory.Exists(rig.Folder));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("mkw-recomp/../../evil.txt")]
    [InlineData("mkw-recomp/sub/../../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("mkw-recomp/evil.txt:stream")]
    [InlineData("mkw-recomp\\..\\..\\evil.txt")]
    [InlineData("mkw-recomp/./evil.txt")]
    [InlineData("mkw-recomp/evil.")]
    public async Task An_entry_that_would_land_outside_the_folder_stops_the_install(string name)
    {
        using var rig = new RuntimeRig([.. RuntimeRig.Release, (name, "evil")]);
        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        Assert.Contains("unsafe name", e.Message, StringComparison.Ordinal);
        // Not one file was written anywhere: not the bad one, not the good ones before it.
        Assert.False(Directory.Exists(rig.Folder));
        Assert.Empty(Directory.EnumerateFiles(rig.Root, "evil*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Entry_paths_are_checked_twice()
    {
        var root = Path.Combine(Path.GetTempPath(), "mkwmp-inside");
        Assert.Equal(Path.Combine(root, "a", "b.txt"), RuntimeInstaller.Inside(root, "a/b.txt"));
        Assert.Throws<LauncherException>(() => RuntimeInstaller.Inside(root, "../b.txt"));
        Assert.Throws<LauncherException>(() => RuntimeInstaller.Inside(root, "a/../../b.txt"));
        // "install-evil" is not inside "install".
        Assert.Throws<LauncherException>(() => RuntimeInstaller.Inside(root, "../mkwmp-inside-evil/b.txt"));
    }

    [Fact]
    public async Task An_archive_without_the_executable_or_with_the_same_name_twice_is_refused()
    {
        using var none = new RuntimeRig([("mkw-recomp/SDL3.dll", "x")]);
        Assert.Contains("no mkw.exe", (await Assert.ThrowsAsync<LauncherException>(() => none.Install())).Message, StringComparison.Ordinal);
        using var twice = new RuntimeRig([.. RuntimeRig.Release, ("mkw-recomp/sdl3.DLL", "other")]);
        await Assert.ThrowsAsync<LauncherException>(() => twice.Install());
        Assert.False(Directory.Exists(twice.Folder));
    }

    [Fact]
    public async Task An_update_replaces_only_what_changed_and_keeps_the_players_install()
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(RuntimeRig.OldForkExe);
        Assert.Equal(RuntimeFolderKind.SameLineage, rig.Installer.Classify(rig.Folder));
        var before = rig.Snapshot();

        var result = await rig.Install();

        Assert.True(result.Updated);
        var after = rig.Snapshot();
        // The runtime's files that differ are the new ones.
        Assert.Equal(Encoding.UTF8.GetString(RuntimeRig.NewExe), after["mkw.exe"]);
        Assert.Equal("new sdl", after["SDL3.dll"]);
        Assert.Equal("MIT", after["licenses/mkw-recomp-LICENSE"]);
        // The toolchain is the runtime's: what differs is replaced, what is new is added.
        Assert.Equal("new python", after["setup/python/python.exe"]);
        Assert.Equal("new script", after["setup/src/recompile.py"]);
        // The setup's state, settings, saves, mods, logs and everything built from the game: untouched,
        // even where the zip has a file of that name (mkw.ini).
        foreach (var kept in new[]
        {
            "setup-state.json", "mkw.ini", "profile/save.bin", "mods/mkwmp/plugin/mkwmp.dll", "logs/mkw.log",
            "mkwgame.dll", "mkwgame.id", "pipeline-cache.bin", "spirv/a.spv", "libopus-0.dll",
        })
        {
            Assert.Equal(before[kept], after[kept]);
        }
        // What was replaced is in the backup, and only that.
        Assert.Equal(Path.Combine(rig.Folder, RuntimeInstaller.BackupFolder), result.Backup);
        Assert.Equal(
            Swapped,
            Directory.EnumerateFiles(result.Backup!, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(result.Backup!, f)).Order(StringComparer.Ordinal));
        Assert.Equal(Encoding.UTF8.GetString(RuntimeRig.OldForkExe), File.ReadAllText(Path.Combine(result.Backup!, "mkw.exe")));
        Assert.Equal("old python", File.ReadAllText(Path.Combine(result.Backup!, "setup", "python", "python.exe")));
        Assert.Equal((2, 3), (result.Added, result.Replaced)); // the license and a setup script are new; the exe, SDL3 and python differed
        Assert.False(Directory.Exists(rig.At(RuntimeInstaller.WorkFolder)));
        Assert.Equal(RuntimeRig.Pinned, RuntimeInstaller.ManagedVersion(rig.Folder));
    }

    [Fact]
    public async Task The_fork_s_0_1_16_mkwmp_1_installed_by_hand_is_updated_in_place_to_the_official_build()
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(RuntimeRig.ForkPointExe);
        Assert.Equal(RuntimeFolderKind.SameLineage, rig.Installer.Classify(rig.Folder));
        var before = rig.Snapshot();

        var result = await rig.Install();

        Assert.True(result.Updated);
        var after = rig.Snapshot();
        Assert.Equal(Encoding.UTF8.GetString(RuntimeRig.NewExe), after["mkw.exe"]);
        // the game built from the player's copy, its setup and the player's saves, settings and mod stay
        foreach (var kept in new[] { "setup-state.json", "mkw.ini", "profile/save.bin", "mods/mkwmp/plugin/mkwmp.dll", "mkwgame.dll", "mkwgame.id", "spirv/a.spv" })
        {
            Assert.Equal(before[kept], after[kept]);
        }
        Assert.Equal(RuntimeRig.Pinned, RuntimeInstaller.ManagedVersion(rig.Folder));
    }

    [Fact]
    public async Task A_file_the_previous_build_installed_and_the_new_one_dropped_goes_to_the_backup()
    {
        using var rig = new RuntimeRig();
        await rig.Install();
        // The next launcher pins a build without SDL3.dll and with another exe.
        var newer = "mkw.exe of the next fork build"u8.ToArray();
        Recomp.TestExes[Updater.Sha256Hex(newer)] = "0.1.17-mkwmp";
        var zip = RuntimeRig.MakeZip([("mkw-recomp/mkw.exe", Encoding.UTF8.GetString(newer)), ("mkw-recomp/libopus-0.dll", "opus")]);
        rig.Host.File = zip;
        rig.Put("mkwgame.dll", "built here");
        var next = new RuntimeInstaller(
            rig.Manifest with { Version = "0.1.17-mkwmp", Size = zip.Length, Sha256 = Updater.Sha256Hex(zip), ExecutableSha256 = Updater.Sha256Hex(newer) },
            rig.Http, "mkwmp-test", rig.Cache, new RuntimeHost { Identify = rig.Identify });

        var result = await next.InstallAsync(rig.Folder, null, null, default);

        Assert.Null(rig.Read("SDL3.dll"));
        Assert.Equal("new sdl", File.ReadAllText(Path.Combine(result.Backup!, "SDL3.dll")));
        Assert.Equal("built here", rig.Read("mkwgame.dll"));
        // setup\ is the runtime's too: a file the new build dropped leaves with the rest.
        Assert.Null(rig.Read("setup/python/python.exe"));
        Assert.Equal("new python", File.ReadAllText(Path.Combine(result.Backup!, "setup", "python", "python.exe")));
        Assert.Equal("0.1.17-mkwmp", RuntimeInstaller.ManagedVersion(rig.Folder));
    }

    /// <summary>
    /// The new pin's zip, beside the one the 0.1.15-mkwmp pin installed: setup\ changed in between
    /// (python and a script differ, a tool is new, the zig toolchain is the same).
    /// </summary>
    private static readonly (string Name, string Text)[] Release016 =
    [
        .. RuntimeRig.Release,
        ("mkw-recomp/setup/zig/zig.exe", "zig"),
        ("mkw-recomp/setup/glslc/spirv-cross.exe", "new tool"),
    ];

    /// <summary>An install as the 0.1.15-mkwmp pin left it (its manifest kept setup\ whole), then played.</summary>
    private static async Task InstallPreviousPin(RuntimeRig rig)
    {
        var zip = RuntimeRig.MakeZip(
        [
            ("mkw-recomp/mkw.exe", Encoding.UTF8.GetString(RuntimeRig.PreviousPinExe)),
            ("mkw-recomp/SDL3.dll", "old sdl"),
            ("mkw-recomp/libopus-0.dll", "opus"),
            ("mkw-recomp/licenses/mkw-recomp-LICENSE", "MIT"),
            ("mkw-recomp/setup/python/python.exe", "old python"),
            ("mkw-recomp/setup/src/recompile.py", "old script"),
            ("mkw-recomp/setup/zig/zig.exe", "zig"),
            ("mkw-recomp/mkw.ini", "shipped = 1\n"),
        ]);
        var previous = rig.Manifest with
        {
            Version = RuntimeRig.PreviousPin,
            Tag = "v0.1.15-mkwmp",
            Asset = "previous.zip",
            Size = zip.Length,
            Sha256 = Updater.Sha256Hex(zip),
            ExecutableSha256 = Updater.Sha256Hex(RuntimeRig.PreviousPinExe),
            Preserved = ["setup", .. rig.Manifest.Preserved],
        };
        var shipped = rig.Host.File;
        rig.Host.File = zip;
        await new RuntimeInstaller(previous, rig.Http, "mkwmp-test", rig.Cache, new RuntimeHost { Identify = rig.Identify }).InstallAsync(rig.Folder, null, null, default);
        rig.Host.File = shipped;
        Assert.Equal(RuntimeRig.PreviousPin, RuntimeInstaller.ManagedVersion(rig.Folder));
        // Played: the setup ran, the player changed settings and has saves, mods and shaders.
        rig.Put("setup-state.json", """{"done":{"generate":"c0dd6cf12c07c23a 1.8.0 x"},"port":"0.1.15-mkwmp","version":1}""");
        rig.Put("setup-work/obj/a.o", "object");
        rig.Put("mkw.ini", "[launcher]\nmods = mkwmp\n");
        rig.Put("profile/save.bin", "save");
        rig.Put("mods/mkwmp/plugin/mkwmp.dll", "mod");
        rig.Put("mkwgame.dll", "built from the player's game");
        rig.Put("mkwgame.id", "c0dd6cf12c07c23a");
        rig.Put("spirv/a.spv", "shader");
        rig.Put("spirv-renodx/b.spv", "tone map");
        rig.Put("pipeline-cache.bin", "cache");
    }

    [Fact]
    public async Task An_update_from_the_015_pin_replaces_the_changed_setup_files_and_keeps_the_players_state()
    {
        using var rig = new RuntimeRig(Release016);
        await InstallPreviousPin(rig);
        Assert.Equal(RuntimeFolderKind.Managed, rig.Installer.Classify(rig.Folder));
        var before = rig.Snapshot();

        var result = await rig.Install();

        Assert.True(result.Updated);
        Assert.Equal(RuntimeRig.Pinned, RuntimeInstaller.ManagedVersion(rig.Folder));
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
        var after = rig.Snapshot();
        // setup\ is the new build's: changed files replaced, the new one added, the same one left as it was.
        Assert.Equal("new python", after["setup/python/python.exe"]);
        Assert.Equal("new script", after["setup/src/recompile.py"]);
        Assert.Equal("new tool", after["setup/glslc/spirv-cross.exe"]);
        Assert.Equal("zig", after["setup/zig/zig.exe"]);
        // The player's state and what the setup built from their game: untouched.
        foreach (var kept in new[]
        {
            "setup-state.json", "setup-work/obj/a.o", "mkw.ini", "profile/save.bin", "mods/mkwmp/plugin/mkwmp.dll",
            "mkwgame.dll", "mkwgame.id", "spirv/a.spv", "spirv-renodx/b.spv", "pipeline-cache.bin",
        })
        {
            Assert.Equal(before[kept], after[kept]);
        }
        // The backup holds what was replaced, setup\ included, and nothing that was the same.
        var backup = Directory.EnumerateFiles(result.Backup!, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(result.Backup!, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["SDL3.dll", "mkw.exe", "mkwmp-runtime.json", "setup/python/python.exe", "setup/src/recompile.py"], backup);
        Assert.Equal("old python", File.ReadAllText(Path.Combine(result.Backup!, "setup", "python", "python.exe")));
        Assert.Equal((1, 4), (result.Added, result.Replaced));
        // From now on the marker lists setup\ as the launcher's: a later pin that drops a setup file removes it.
        Assert.Contains("setup/zig/zig.exe", rig.Read(RuntimeInstaller.Marker), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_update_from_the_015_pin_that_fails_puts_setup_back_as_it_was()
    {
        using var rig = new RuntimeRig(Release016);
        await InstallPreviousPin(rig);
        var before = rig.Snapshot();
        LauncherException e;
        // The files are placed in the zip's order: mkw.exe, SDL3.dll and python are already swapped when the script is.
        using (new FileStream(rig.At("setup/src/recompile.py"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        }
        Assert.Contains("put back as it was", e.Message, StringComparison.Ordinal);
        Assert.Equal(before, rig.Snapshot());
        Assert.Equal(RuntimeRig.PreviousPin, Recomp.Identify(rig.Folder).Version);

        await rig.Install();
        Assert.Equal("new script", rig.Read("setup/src/recompile.py"));
        Assert.Equal(RuntimeRig.Pinned, RuntimeInstaller.ManagedVersion(rig.Folder));
    }

    [Fact]
    public async Task An_install_that_is_not_recognised_afterwards_is_rolled_back()
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(RuntimeRig.OldForkExe);
        var before = rig.Snapshot();
        // The target stops knowing the new build once it is in place (as if the pin and the hash table disagreed).
        rig.Identify = folder => File.ReadAllBytes(Path.Combine(folder, "mkw.exe")).AsSpan().SequenceEqual(RuntimeRig.NewExe) ? null : "0.1.14-mkwmp";

        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());

        Assert.Contains("put back as it was", e.Message, StringComparison.Ordinal);
        Assert.Equal(before, rig.Snapshot());
        Assert.False(Directory.Exists(rig.At(RuntimeInstaller.WorkFolder)));
        Assert.False(Directory.Exists(rig.At("licenses")));
    }

    [Fact]
    public async Task A_file_that_cannot_be_replaced_rolls_the_update_back()
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(RuntimeRig.OldForkExe);
        var before = rig.Snapshot();
        LauncherException e;
        // Something holds SDL3.dll open (the files are placed in the zip's order: mkw.exe is already swapped by then).
        using (new FileStream(rig.At("SDL3.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        }
        Assert.Contains("put back as it was", e.Message, StringComparison.Ordinal);
        Assert.Equal(before, rig.Snapshot());
        Assert.Equal("0.1.14-mkwmp", Recomp.Identify(rig.Folder).Version);
        // The verified download is kept: the next try needs no network.
        Assert.True(File.Exists(rig.Installer.ArchivePath));
        var requests = rig.Host.Requests.Count;
        await rig.Install();
        Assert.Equal(requests, rig.Host.Requests.Count);
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
    }

    [Fact]
    public async Task A_new_install_that_fails_leaves_no_folder_behind()
    {
        using var rig = new RuntimeRig();
        rig.Identify = _ => null;
        await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        Assert.False(Directory.Exists(rig.Folder));
    }

    [Fact]
    public async Task Nothing_is_touched_while_the_game_runs()
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(RuntimeRig.OldForkExe);
        var before = rig.Snapshot();
        rig.Running = true;
        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        Assert.Equal("Close the game to update mkw-recomp", e.Message);
        Assert.Empty(rig.Host.Requests);
        Assert.Equal(before, rig.Snapshot());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task An_install_that_is_not_ours_is_refused_and_ours_goes_beside_it(bool upstream)
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(upstream ? RuntimeRig.UpstreamExe : "some other mkw.exe"u8.ToArray());
        var before = rig.Snapshot();
        Assert.Equal(RuntimeFolderKind.Foreign, rig.Installer.Classify(rig.Folder));

        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());

        Assert.Contains("MKWMP did not install", e.Message, StringComparison.Ordinal);
        Assert.Contains(upstream ? "mkw-recomp 0.1.13," : "MKWMP does not know", e.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Host.Requests);
        Assert.Equal(before, rig.Snapshot());

        // Beside it: another folder gets MKWMP's build, and the player's install stays as it was.
        var beside = Path.Combine(rig.Root, "mkwmp-build");
        await rig.Installer.InstallAsync(beside, null, null, default);
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(beside).Version);
        Assert.Equal(before, rig.Snapshot());
    }

    [Fact]
    public async Task Replacing_an_install_that_is_not_ours_takes_the_explicit_flag_and_keeps_a_backup()
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(RuntimeRig.UpstreamExe);
        var result = await rig.Install(new RuntimeInstallOptions { ReplaceForeign = true });
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
        Assert.Equal(Encoding.UTF8.GetString(RuntimeRig.UpstreamExe), File.ReadAllText(Path.Combine(result.Backup!, "mkw.exe")));
        Assert.Equal("new python", rig.Read("setup/python/python.exe"));
        Assert.Equal("old python", File.ReadAllText(Path.Combine(result.Backup!, "setup", "python", "python.exe")));
        Assert.Equal("save", rig.Read("profile/save.bin"));
    }

    [Fact]
    public async Task A_folder_with_other_files_is_refused()
    {
        using var rig = new RuntimeRig();
        rig.Put("holiday.jpg", "not a game");
        Assert.Equal(RuntimeFolderKind.Occupied, rig.Installer.Classify(rig.Folder));
        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install(new RuntimeInstallOptions { ReplaceForeign = true }));
        Assert.Equal(Strings.RuntimeFolderOccupied, e.Message);
        Assert.Empty(rig.Host.Requests);
        Assert.Equal("holiday.jpg", Assert.Single(rig.Snapshot().Keys));
    }

    [Fact]
    public async Task Free_space_is_checked_before_the_download()
    {
        using var rig = new RuntimeRig();
        // The download, what it unpacks to (InstalledSize 100) and the setup's own needs (SetupSpace 1000).
        rig.Free = rig.Zip.Length + 100 + 1000 - 1;
        var e = await Assert.ThrowsAsync<LauncherException>(() => rig.Install());
        Assert.StartsWith("Not enough free space on ", e.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Host.Requests);
        Assert.False(Directory.Exists(rig.Folder));

        rig.Free += 1;
        await rig.Install();
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
    }

    [Fact]
    public async Task An_update_needs_room_only_for_what_it_replaces()
    {
        using var rig = new RuntimeRig();
        rig.ExistingInstall(RuntimeRig.OldForkExe);
        rig.Free = rig.Zip.Length + 200; // no room for the setup's 1000: it has been done here already
        await rig.Install();
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
    }

    [Fact]
    public async Task With_AVX2_an_install_of_the_releases_noavx2_build_is_up_to_date()
    {
        using var rig = new RuntimeRig();
        await rig.Install();
        // The release's other build, put here by a launcher on a processor without AVX2: it runs here too.
        File.WriteAllBytes(rig.At("mkw.exe"), RuntimeRig.NoAvx2Exe);
        Assert.True(rig.Manifest.Suits(rig.Folder));

        var again = await rig.Install();
        Assert.Single(rig.Host.Requests);
        Assert.Equal((0, 0), (again.Added, again.Replaced));
        Assert.Equal(RuntimeRig.NoAvx2Exe, File.ReadAllBytes(rig.At("mkw.exe")));
    }

    [Fact]
    public async Task Without_AVX2_the_main_build_is_replaced_by_the_noavx2_one_and_the_players_files_stay()
    {
        using var main = new RuntimeRig();
        using var rig = RuntimeRig.NoAvx2Rig();
        Assert.Equal("0.1.18 (no AVX2)", rig.Manifest.Label);
        // Installed on a processor with AVX2, then brought here.
        await main.Installer.InstallAsync(rig.Folder, null, null, default);
        rig.Put("profile/save.bin", "save");
        rig.Put("mkwgame.dll", "built from the player's game");
        Assert.False(rig.Manifest.Suits(rig.Folder));

        var result = await rig.Install();
        Assert.True(result.Updated);
        Assert.Single(rig.Host.Requests);
        Assert.Equal(RuntimeRig.NoAvx2Exe, File.ReadAllBytes(rig.At("mkw.exe")));
        Assert.Equal("v2\n", rig.Read("setup/cpu-level"));
        Assert.Equal(("save", "built from the player's game"), (rig.Read("profile/save.bin"), rig.Read("mkwgame.dll")));
        Assert.Equal(RuntimeRig.Pinned, Recomp.Identify(rig.Folder).Version);
        Assert.True(rig.Manifest.Suits(rig.Folder));

        // And now it is up to date.
        await rig.Install();
        Assert.Single(rig.Host.Requests);
    }

    private sealed class Collect(Action<RuntimeProgress> report) : IProgress<RuntimeProgress>
    {
        public void Report(RuntimeProgress value) => report(value);
    }
}

/// <summary>The same through the launcher: the view the window shows, and client.toml.</summary>
public class LauncherRuntimeTests
{
    private sealed class Home : IAsyncDisposable
    {
        public Home(Action<RuntimeRig>? arrange = null, string game = "", Func<RuntimeRig>? files = null, string devExes = "")
        {
            Files = files?.Invoke() ?? new RuntimeRig();
            arrange?.Invoke(Files);
            var home = Path.Combine(Files.Root, "launcher");
            Directory.CreateDirectory(home);
            ConfigPath = Path.Combine(Files.Root, "client.toml");
            new ClientConfig
            {
                Settings = new LauncherSettings
                {
                    Name = "Ana", Pin = "123456", AutoUpdate = false, AdultConsent = true, Game = game.Length > 0 ? Path.Combine(Files.Root, game) : "", DevRecompExes = devExes,
                },
            }.Save(ConfigPath);
            Launcher = new Launcher(ConfigPath, home, new LauncherOptions
            {
                Version = "1.4.0",
                Targets = [new MkwRecompTarget("1.4.0") { RunningOverride = () => false }],
                BindLocalApi = false,
                BackgroundLoops = false,
                Runtime = Files.Manifest,
                RuntimeFolder = Files.Folder,
                RuntimeCache = Files.Cache,
                RuntimeFreeSpace = _ => null,
            }, Files.Host);
            Launcher.Changed += (_, _) =>
            {
                lock (States)
                {
                    States.Add(Launcher.Runtime);
                }
            };
        }

        public RuntimeRig Files { get; }
        public string ConfigPath { get; }
        public Launcher Launcher { get; }
        public List<RuntimeView> States { get; } = [];

        public async ValueTask DisposeAsync()
        {
            await Launcher.DisposeAsync();
            Files.Dispose();
        }
    }

    [Fact]
    public async Task Install_downloads_verifies_unpacks_and_makes_it_the_game_folder()
    {
        await using var home = new Home();
        var view = home.Launcher.Runtime;
        Assert.Equal(RuntimeState.NotInstalled, view.State);
        Assert.Equal((RuntimeRig.Pinned, (string?)null, home.Files.Folder, false), (view.PinnedVersion, view.InstalledVersion, view.Folder, view.Foreign));
        Assert.Equal(home.Files.Zip.Length, view.DownloadSize);
        Assert.Equal(home.Files.Zip.Length + 100 + 1000, view.DiskSpace);
        Assert.Same(RecompRuntime.Pinned.Credit, view.Credit);

        await home.Launcher.InstallRuntimeAsync(null, default);

        view = home.Launcher.Runtime;
        Assert.Equal(RuntimeState.Installed, view.State);
        Assert.Equal((RuntimeRig.Pinned, home.Files.Folder, false), (view.InstalledVersion, view.Folder, view.Foreign));
        Assert.Equal("mkw-recomp 0.1.18 installed", view.Note);
        Assert.False(view.Busy);
        // The window was shown each phase, with bytes.
        var seen = home.States.Select(s => s.State).Distinct().ToList();
        Assert.Contains(RuntimeState.Downloading, seen);
        Assert.Contains(RuntimeState.Verifying, seen);
        Assert.Contains(RuntimeState.Extracting, seen);
        Assert.Contains(home.States, s => s.State == RuntimeState.Downloading && s.BytesTotal == home.Files.Zip.Length && s.BytesDone == s.BytesTotal && s.Progress == 1);
        // Recorded: the game folder, in the view and in client.toml.
        Assert.Equal(home.Files.Folder, home.Launcher.Settings.Game);
        Assert.Equal(home.Files.Folder, ClientConfig.Load(home.ConfigPath).Settings.Game);
        Assert.Equal(RuntimeRig.Pinned, home.Launcher.Game.Install?.Version);
        Assert.All(home.Files.Host.Requests, r => Assert.Equal($"GET {FakeReleaseHost.Url}", r.Request));
    }

    [Fact]
    public async Task The_players_own_upstream_install_is_left_alone_and_ours_is_offered_beside_it()
    {
        await using var home = new Home(f => f.ExistingInstall(RuntimeRig.UpstreamExe, Path.Combine(f.Root, "theirs")), game: "theirs");
        var theirs = Path.Combine(home.Files.Root, "theirs");
        var before = home.Files.Snapshot(theirs);
        var view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.Installed, "0.1.13", true), (view.State, view.InstalledVersion, view.Foreign));
        Assert.Equal(home.Files.Folder, view.Folder); // where ours would go: not their folder

        // Over it: refused, whoever asks.
        await home.Launcher.InstallRuntimeAsync(theirs, default);
        view = home.Launcher.Runtime;
        Assert.Equal(RuntimeState.Failed, view.State);
        Assert.Contains("MKWMP did not install", view.Note, StringComparison.Ordinal);
        Assert.Empty(home.Files.Host.Requests);
        Assert.Equal(before, home.Files.Snapshot(theirs));
        await home.Launcher.CancelRuntimeInstall();
        Assert.Equal((RuntimeState.Installed, (string?)null), (home.Launcher.Runtime.State, home.Launcher.Runtime.Note));

        // Beside it (no folder given): ours is installed and becomes the game folder; theirs is as it was.
        await home.Launcher.InstallRuntimeAsync(null, default);
        view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned, false), (view.State, view.InstalledVersion, view.Foreign));
        Assert.Equal(home.Files.Folder, home.Launcher.Settings.Game);
        Assert.Equal(before, home.Files.Snapshot(theirs));
    }

    [Fact]
    public async Task An_older_build_of_ours_is_an_update_applied_in_place()
    {
        await using var home = new Home(f => f.ExistingInstall(RuntimeRig.OldForkExe, Path.Combine(f.Root, "mine")), game: "mine");
        var mine = Path.Combine(home.Files.Root, "mine");
        var view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.UpdateAvailable, "0.1.14-mkwmp", RuntimeRig.Pinned, mine, false), (view.State, view.InstalledVersion, view.PinnedVersion, view.Folder, view.Foreign));

        await home.Launcher.InstallRuntimeAsync(null, default);

        view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned, "mkw-recomp updated to 0.1.18"), (view.State, view.InstalledVersion, view.Note));
        Assert.Equal(mine, home.Launcher.Settings.Game);
        Assert.Equal("new python",File.ReadAllText(Path.Combine(mine, "setup", "python", "python.exe")));
        Assert.Equal("save", File.ReadAllText(Path.Combine(mine, "profile", "save.bin")));
        Assert.False(Directory.Exists(home.Files.Folder)); // nothing went to the default folder
    }

    [Fact]
    public async Task An_official_release_installed_by_hand_is_updated_in_place_to_the_pin()
    {
        // The player picked their own mkw-recomp 0.1.17 folder (no marker): it was "your own build" and stayed on 0.1.17.
        await using var home = new Home(f => f.ExistingInstall(RuntimeRig.OfficialExe, Path.Combine(f.Root, "theirs")), game: "theirs");
        var theirs = Path.Combine(home.Files.Root, "theirs");
        Assert.Null(RuntimeInstaller.ManagedVersion(theirs));
        var view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.UpdateAvailable, "0.1.17", RuntimeRig.Pinned, theirs, false), (view.State, view.InstalledVersion, view.PinnedVersion, view.Folder, view.Foreign));
        // MKWMP runs on 0.1.18 only: PLAY is refused, and the checklist's way out is the update in place, not a second install
        Assert.NotNull(home.Launcher.PlayProblem);
        Assert.Equal(TargetStatus.Unsupported, home.Launcher.Game.Check!.Status);
        var row = home.Launcher.Checklist.Find(CheckId.Runtime)!;
        Assert.Equal((CheckState.Needed, Strings.RuntimeUpdateNeeded("mkw-recomp", "0.1.18")), (row.State, row.Status));
        Assert.Equal(CheckActionKind.InstallRuntime, row.Action!.Kind);
        Assert.Contains("Update", row.Action.Label, StringComparison.Ordinal);
        Assert.False(home.Launcher.Checklist.Ready);

        await home.Launcher.InstallRuntimeAsync(null, default);

        view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned, "mkw-recomp updated to 0.1.18"), (view.State, view.InstalledVersion, view.Note));
        Assert.Equal(theirs, home.Launcher.Settings.Game);
        Assert.Equal(RuntimeRig.NewExe, File.ReadAllBytes(Path.Combine(theirs, "mkw.exe")));
        Assert.Equal("save", File.ReadAllText(Path.Combine(theirs, "profile", "save.bin")));
        Assert.Equal(RuntimeRig.Pinned, RuntimeInstaller.ManagedVersion(theirs)); // ours from now on: the next pin updates it too
        Assert.False(Directory.Exists(home.Files.Folder)); // nothing went to the default folder

        // mkw-recomp's own updater (on again) moves it to a release MKWMP does not know: refused, and put back where it is
        File.WriteAllBytes(Path.Combine(theirs, "mkw.exe"), "mkw.exe of a newer mkw-recomp than MKWMP knows"u8.ToArray());
        await home.Launcher.FindGameAsync();
        view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.UpdateAvailable, (string?)null, theirs, false), (view.State, view.InstalledVersion, view.Folder, view.Foreign));
        Assert.NotNull(home.Launcher.PlayProblem);
        Assert.Equal(CheckActionKind.InstallRuntime, home.Launcher.Checklist.Find(CheckId.Runtime)!.Action!.Kind);

        await home.Launcher.InstallRuntimeAsync(null, default);

        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned), (home.Launcher.Runtime.State, home.Launcher.Runtime.InstalledVersion));
        Assert.Equal(RuntimeRig.NewExe, File.ReadAllBytes(Path.Combine(theirs, "mkw.exe")));
        Assert.Equal(theirs, home.Launcher.Settings.Game);
        Assert.Equal("save", File.ReadAllText(Path.Combine(theirs, "profile", "save.bin")));
        Assert.False(Directory.Exists(home.Files.Folder)); // still the player's own folder, never the default one
    }

    [Fact]
    public async Task With_AVX2_the_noavx2_build_in_the_game_folder_is_the_pinned_one()
    {
        await using var home = new Home(f => f.ExistingInstall(RuntimeRig.NoAvx2Exe, Path.Combine(f.Root, "mine")), game: "mine");
        var view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned, "0.1.18", (string?)null), (view.State, view.InstalledVersion, view.PinnedLabel, view.PinnedVariant));
    }

    [Fact]
    public async Task Without_AVX2_the_main_build_in_the_game_folder_is_updated_to_the_noavx2_one()
    {
        await using var home = new Home(f => f.ExistingInstall(RuntimeRig.NewExe, Path.Combine(f.Root, "mine")), game: "mine", files: RuntimeRig.NoAvx2Rig);
        var mine = Path.Combine(home.Files.Root, "mine");
        var view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.UpdateAvailable, RuntimeRig.Pinned, "0.1.18 (no AVX2)", mine), (view.State, view.InstalledVersion, view.PinnedLabel, view.Folder));

        await home.Launcher.InstallRuntimeAsync(null, default);

        view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned, "mkw-recomp updated to 0.1.18 (no AVX2)"), (view.State, view.InstalledVersion, view.Note));
        Assert.Equal(RuntimeRig.NoAvx2Exe, File.ReadAllBytes(Path.Combine(mine, "mkw.exe")));
        Assert.Equal("save", File.ReadAllText(Path.Combine(mine, "profile", "save.bin")));
    }

    [Fact]
    public async Task Cancel_pauses_the_download_and_the_next_install_continues_it()
    {
        await using var home = new Home();
        (home.Files.Host.CutAfter, home.Files.Host.Hang) = (300, true);
        var install = home.Launcher.InstallRuntimeAsync(null, default);
        await home.Files.Host.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RuntimeState.Downloading, home.Launcher.Runtime.State);
        Assert.True(home.Launcher.Runtime.Busy);

        await home.Launcher.CancelRuntimeInstall();
        await install;

        var view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.NotInstalled, Strings.RuntimePaused), (view.State, view.Note));
        Assert.Equal("", home.Launcher.Settings.Game);

        await home.Launcher.InstallRuntimeAsync(null, default);
        Assert.Equal(RuntimeState.Installed, home.Launcher.Runtime.State);
        Assert.Equal(new long?[] { null, 300 }, home.Files.Host.Requests.Select(r => r.From));
    }

    [Fact]
    public async Task A_failed_install_says_why_and_changes_nothing()
    {
        await using var home = new Home();
        var tampered = (byte[])home.Files.Zip.Clone();
        tampered[^30] ^= 1;
        home.Files.Host.File = tampered;
        await home.Launcher.InstallRuntimeAsync(null, default);
        var view = home.Launcher.Runtime;
        Assert.Equal(RuntimeState.Failed, view.State);
        Assert.Contains("checksum", view.Note, StringComparison.Ordinal);
        Assert.Equal("", home.Launcher.Settings.Game);
        Assert.False(Directory.Exists(home.Files.Folder));
    }

    // ---- a developer's test build of mkw.exe ([dev] recomp_exe_sha256) ----------------------------

    /// <summary>The launcher's session log, read while it is open.</summary>
    private static string[] LogText(Launcher l)
    {
        using var file = new FileStream(l.Log.Path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file);
        return reader.ReadToEnd().Split('\n');
    }

    /// <summary>An install of ours, from the pin before (its marker says so), with a developer's mkw.exe copied over the release's.</summary>
    private static void DevInstall(RuntimeRig f, byte[] exe)
    {
        var mine = Path.Combine(f.Root, "mine");
        f.ExistingInstall(exe, mine);
        f.Put(RuntimeInstaller.Marker, """{"name":"mkw-recomp","version":"0.1.16-mkwmp","files":["mkw.exe","SDL3.dll","libopus-0.dll"]}""", mine);
    }

    [Fact]
    public async Task A_dev_listed_test_exe_is_the_pinned_version_is_offered_no_update_and_says_so()
    {
        var exe = "mkw.exe of a fork test build, offered no update"u8.ToArray();
        await using var home = new Home(f => DevInstall(f, exe), game: "mine", devExes: Updater.Sha256Hex(exe).ToUpperInvariant());
        var l = home.Launcher;

        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned, Updater.Sha256Hex(exe)), (l.Runtime.State, l.Runtime.InstalledVersion, l.Runtime.DevSha256));
        Assert.Equal((RuntimeRig.Pinned, Updater.Sha256Hex(exe)), (l.Game.Install?.Version, l.Game.Install?.DevSha256));
        Assert.NotEqual(TargetStatus.Unsupported, l.Game.Check?.Status);
        var row = l.Checklist.Find(CheckId.Runtime)!;
        Assert.Equal(CheckState.Ok, row.State);
        Assert.Contains("Development mkw-recomp", row.Status, StringComparison.Ordinal);
        Assert.Contains(LogText(l), line => line.Contains($"mkw.exe {Updater.Sha256Hex(exe)[..16]}: accepted as a DEVELOPMENT build (dev recomp_exe_sha256)", StringComparison.Ordinal));
        Assert.Empty(home.Files.Host.Requests);
    }

    [Fact]
    public async Task An_update_or_repair_of_the_install_never_replaces_a_dev_listed_exe()
    {
        var exe = "mkw.exe of a fork test build, kept by an update"u8.ToArray();
        await using var home = new Home(f => DevInstall(f, exe), game: "mine", devExes: Updater.Sha256Hex(exe));
        var mine = Path.Combine(home.Files.Root, "mine");

        // Asked for in as many words (the window never does: no update is offered): every other file is the pinned one, mkw.exe stays.
        await home.Launcher.InstallRuntimeAsync(null, default);

        var view = home.Launcher.Runtime;
        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned, Updater.Sha256Hex(exe)), (view.State, view.InstalledVersion, view.DevSha256));
        Assert.Equal(exe, File.ReadAllBytes(Path.Combine(mine, "mkw.exe")));
        Assert.Equal("new sdl", File.ReadAllText(Path.Combine(mine, "SDL3.dll")));
        Assert.Equal(RuntimeRig.Pinned, RuntimeInstaller.ManagedVersion(mine));
        Assert.Equal("save", File.ReadAllText(Path.Combine(mine, "profile", "save.bin")));
        Assert.False(File.Exists(Path.Combine(mine, RuntimeInstaller.BackupFolder, "mkw.exe")));

        // Again: the marker is the pinned one now, so nothing is even downloaded.
        var requests = home.Files.Host.Requests.Count;
        await home.Launcher.InstallRuntimeAsync(null, default);
        Assert.Equal(requests, home.Files.Host.Requests.Count);
        Assert.Equal(exe, File.ReadAllBytes(Path.Combine(mine, "mkw.exe")));
    }

    [Fact]
    public async Task Without_AVX2_a_dev_listed_exe_is_not_taken_for_the_other_build()
    {
        var exe = "mkw.exe of a fork test build, on a processor without AVX2"u8.ToArray();
        await using var home = new Home(f => DevInstall(f, exe), game: "mine", files: RuntimeRig.NoAvx2Rig, devExes: Updater.Sha256Hex(exe));
        Assert.Equal((RuntimeState.Installed, RuntimeRig.Pinned), (home.Launcher.Runtime.State, home.Launcher.Runtime.InstalledVersion));
    }

    [Fact]
    public async Task Without_the_dev_setting_the_same_exe_is_unknown_and_blocks_play()
    {
        var exe = "mkw.exe of a fork test build, not listed"u8.ToArray();
        await using var home = new Home(f => DevInstall(f, exe), game: "mine");
        var l = home.Launcher;
        Assert.Null(l.Game.Install?.Version);
        Assert.Null(l.Game.Install?.DevSha256);
        Assert.Equal(TargetStatus.Unsupported, l.Game.Check?.Status);
        Assert.Equal(CheckState.Needed, l.Checklist.Find(CheckId.Runtime)!.State);
        Assert.DoesNotContain(LogText(l), line => line.Contains("DEVELOPMENT build", StringComparison.Ordinal));
    }
}

/// <summary>The dev setting itself: client.toml, the environment, and what a listed mkw.exe is taken for.</summary>
public class DevRecompExeTests
{
    [Fact]
    public void Client_toml_keeps_the_list_under_dev_and_writes_nothing_without_one()
    {
        var (a, b) = (new string('a', 64), new string('B', 64));
        var parsed = ClientConfig.Parse($"[dev]\nrecomp_exe_sha256 = \"{a}, {b} ,not-a-hash\"\n").Settings;
        Assert.Equal($"{a},{b.ToLowerInvariant()}", parsed.DevRecompExes);
        Assert.Equal($"{a},{b.ToLowerInvariant()}", ClientConfig.Parse($"[dev]\nrecomp_exe_sha256 = [\"{a}\", \"{b}\"]\n").Settings.DevRecompExes);
        var toml = new ClientConfig { Settings = parsed }.ToToml();
        Assert.Contains($"[dev]\nrecomp_exe_sha256 = \"{a},{b.ToLowerInvariant()}\"\n", toml, StringComparison.Ordinal);
        Assert.Equal(parsed.DevRecompExes, ClientConfig.Parse(toml).Settings.DevRecompExes);
        Assert.Equal("", ClientConfig.Parse("").Settings.DevRecompExes);
        Assert.DoesNotContain("[dev]", ClientConfig.Parse("").ToToml(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_listed_exe_is_the_pinned_release_with_its_mod_and_game_id_and_without_the_list_unknown()
    {
        var exe = "mkw.exe of a fork test build, by identify"u8.ToArray();
        var hash = Updater.Sha256Hex(exe);
        Assert.Equal(RecompKind.Unknown, Recomp.IdentifyBytes(exe).Kind);

        var owner = new object();
        Recomp.SetDevExes(owner, hash);
        try
        {
            var id = Recomp.IdentifyBytes(exe);
            Assert.Equal((RecompKind.Supported, RecompRuntime.Pinned.Version, hash), (id.Kind, id.Version, id.DevSha256));
            Assert.Contains(id.Version, Recomp.ModRuntimes);
            Assert.Null(Recomp.RebuildProblemFor(id, "c9bbcb75a9ac9a49")); // the pinned release's game id
            Assert.Contains("DEVELOPMENT build", id.ToString(), StringComparison.Ordinal);

            // A release listed by mistake stays the release it is.
            var release = Recomp.Known.First(k => k.Version == "0.1.16-mkwmp");
            Recomp.SetDevExes(owner, release.Sha256);
            Assert.Null(Recomp.IdentifyHash(release.Sha256).DevSha256);
            Assert.Equal("0.1.16-mkwmp", Recomp.IdentifyHash(release.Sha256).Version);
        }
        finally
        {
            Recomp.SetDevExes(owner, "");
        }
        Assert.Equal(RecompKind.Unknown, Recomp.IdentifyBytes(exe).Kind);
    }

    [Fact]
    public void The_environment_lists_builds_too()
    {
        var exe = "mkw.exe of a fork test build, by environment"u8.ToArray();
        var hash = Updater.Sha256Hex(exe);
        var before = Environment.GetEnvironmentVariable(Recomp.DevExeEnv);
        Environment.SetEnvironmentVariable(Recomp.DevExeEnv, $"{new string('c', 64)},{hash.ToUpperInvariant()}");
        try
        {
            Assert.Equal((RecompKind.Supported, RecompRuntime.Pinned.Version, hash), (Recomp.IdentifyBytes(exe).Kind, Recomp.IdentifyBytes(exe).Version, Recomp.IdentifyBytes(exe).DevSha256));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Recomp.DevExeEnv, before);
        }
    }

    [Fact]
    public async Task The_installer_leaves_a_kept_exe_alone_and_replaces_it_otherwise()
    {
        var exe = "mkw.exe of a fork test build, by the installer"u8.ToArray();
        var owner = new object();
        Recomp.SetDevExes(owner, Updater.Sha256Hex(exe));
        try
        {
            using var rig = new RuntimeRig();
            rig.ExistingInstall(exe);
            var target = new MkwRecompTarget("1.4.0");
            var installer = new RuntimeInstaller(rig.Manifest, rig.Http, "mkwmp-test", rig.Cache, new RuntimeHost
            {
                Identify = f => target.Detect(f)?.Version,
                KeepsExecutable = f => target.Detect(f)?.DevSha256 is not null,
                FreeSpace = _ => null,
            });
            var result = await installer.InstallAsync(rig.Folder, null, null, default);
            Assert.Equal(exe, File.ReadAllBytes(rig.At("mkw.exe")));
            Assert.Equal("new sdl", rig.Read("SDL3.dll"));
            Assert.Equal(RuntimeRig.Pinned, result.Version);

            // Taken off the list: an exe no release has, in an install of ours, so a repair puts the release's back.
            Recomp.SetDevExes(owner, "");
            await rig.Install();
            Assert.Equal(RuntimeRig.NewExe, File.ReadAllBytes(rig.At("mkw.exe")));
        }
        finally
        {
            Recomp.SetDevExes(owner, "");
        }
    }

    [Fact]
    public void A_crash_report_says_the_runtime_is_a_development_build()
    {
        var crash = new Diagnostics.CrashIncident("c1", DateTimeOffset.UnixEpoch, null, 0xC0000005, null);
        var text = Diagnostics.BugReportBuilder.CrashBody(crash, "1.4.2", null, null, new string('d', 64));
        Assert.Contains($"DEVELOPMENT build, mkw.exe {new string('d', 64)} (dev recomp_exe_sha256)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DEVELOPMENT", Diagnostics.BugReportBuilder.CrashBody(crash, "1.4.2", null, null), StringComparison.Ordinal);
    }
}
