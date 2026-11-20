// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mkwmp.Core.Net;
using NSec.Cryptography;

namespace Mkwmp.Core.Updates;

public sealed record ReleaseAsset(string Name, Uri Url);

/// <summary>A GitHub release of the project (<paramref name="Notes"/>: its description, for the update dialog).</summary>
public sealed record Release(string Tag, bool Draft, string? Notes, IReadOnlyList<ReleaseAsset> Assets)
{
    public SemVer? Version => Tag.StartsWith('v') ? SemVer.TryParse(Tag[1..]) : null;

    public bool Has(string name) => Assets.Any(a => a.Name == name);

    internal Uri Url(string name) => Assets.FirstOrDefault(a => a.Name == name)?.Url ?? throw new LauncherException($"release {Tag} has no {name}");
}

/// <summary>
/// Self-update from GitHub Releases, and the files that put MKWMP into the game for this
/// launcher's release. Every release carries <c>SHA256SUMS</c> and <c>SHA256SUMS.sig</c>:
/// <code>
/// # mkwmp 0.0.1-rc.1
/// &lt;sha256 hex&gt;  mkwmp-launcher-x86_64-windows.exe
/// </code>
/// The signature is Ed25519 (base64url, no padding) over the exact bytes of SHA256SUMS, by the
/// release key whose public half is <see cref="ReleaseKeys"/>. A download is used only if the
/// signature verifies, the version inside the signed file is the release's tag, and the file's
/// SHA-256 matches. Any failure leaves what is installed alone; updating never blocks play.
/// </summary>
public sealed class Updater
{
    public const string Repo = "anarqz/mkwmp";
    /// <summary>The launcher alone: what a launcher from before the zip-with-helper downloads (and the 1.3.0 Rust launcher).</summary>
    public const string LauncherAsset = "mkwmp-launcher-x86_64-windows.exe";

    /// <summary>The release's zip for Windows (tools/release/make_zips.py): every file of the launcher, the helper included.</summary>
    public static string ZipAsset(string version) => $"mkwmp-{version}-windows-x64.zip";
    public const string DefaultApi = "https://api.github.com";

    /// <summary>Public release keys (<c>kid:base64url</c>). The private half never leaves the release machine.</summary>
    public static IReadOnlyList<string> ReleaseKeys => Mkwmp.Update.ReleaseSums.ReleaseKeys;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private readonly HttpClient _http;
    private readonly string _api;
    private readonly IReadOnlyList<string> _keys;
    private readonly string _userAgent;

    public Updater(HttpClient http, string userAgent, string? apiBase = null, IReadOnlyList<string>? keys = null)
    {
        _http = http;
        _userAgent = userAgent;
        // MKWMP_UPDATE_API points at a test server; the keys stay the built-in ones.
        _api = (apiBase ?? Environment.GetEnvironmentVariable("MKWMP_UPDATE_API") ?? DefaultApi).TrimEnd('/');
        _keys = keys ?? ReleaseKeys;
    }

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>
    /// Checks SHA256SUMS against its signature and the expected version; the verified list
    /// (asset name → sha256 hex). The version check stops a validly signed list of an old release
    /// being replayed under a newer tag.
    /// </summary>
    public static IReadOnlyDictionary<string, string> VerifySums(ReadOnlySpan<byte> sums, string signature, IEnumerable<string> keys, SemVer version)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(version);
        byte[] sig;
        try
        {
            sig = Base64Url.DecodeFromChars(signature.Trim());
        }
        catch (FormatException)
        {
            throw new LauncherException("malformed signature");
        }
        var ed = SignatureAlgorithm.Ed25519;
        if (sig.Length != ed.SignatureSize)
        {
            throw new LauncherException("malformed signature");
        }
        var trusted = false;
        foreach (var entry in keys)
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }
            try
            {
                var raw = Base64Url.DecodeFromChars(entry.AsSpan(colon + 1));
                if (raw.Length == ed.PublicKeySize && PublicKey.TryImport(ed, raw, KeyBlobFormat.RawPublicKey, out var key) && key is not null && ed.Verify(key, sums, sig))
                {
                    trusted = true;
                    break;
                }
            }
            catch (FormatException)
            {
                // not a key: skipped
            }
        }
        if (!trusted)
        {
            throw new LauncherException("SHA256SUMS signature does not verify");
        }
        string text;
        try
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(sums);
        }
        catch (ArgumentException)
        {
            throw new LauncherException("SHA256SUMS is not text");
        }
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var header = lines.Length > 0 ? lines[0] : "";
        if (header.Trim() != $"# mkwmp {version}")
        {
            throw new LauncherException($"SHA256SUMS is for \"{header}\", not {version}");
        }
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1).Where(l => l.Trim().Length > 0))
        {
            var split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split < 0)
            {
                throw new LauncherException("malformed SHA256SUMS line");
            }
            var hash = line[..split];
            if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
            {
                throw new LauncherException("malformed SHA256SUMS hash");
            }
            map[line[(split + 2)..].Trim()] = hash.ToLowerInvariant();
        }
        return map;
    }

    /// <summary>The newest non-draft release strictly newer than <paramref name="current"/>.</summary>
    public static Release? Newest(IEnumerable<Release> releases, SemVer current)
    {
        ArgumentNullException.ThrowIfNull(releases);
        return releases.Where(r => !r.Draft && r.Version is { } v && v > current).MaxBy(r => r.Version);
    }

    private async Task<byte[]> GetAsync(Uri url, IProgress<(long Got, long? Total)>? progress, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        // A big download on a slow line is fine as long as bytes keep coming.
        limit.CancelAfter(Timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw new LauncherException($"download {url}: HTTP {(int)res.StatusCode}");
            }
            var total = res.Content.Headers.ContentLength;
            using var body = await res.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var all = new MemoryStream(total is > 0 and < int.MaxValue ? (int)total.Value : 0);
            var buffer = new byte[81920];
            int n;
            while ((n = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                all.Write(buffer, 0, n);
                limit.CancelAfter(Timeout);
                progress?.Report((all.Length, total));
            }
            return all.ToArray();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new LauncherException($"download {url}: timed out");
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new LauncherException($"download {url}: {e.InnerException?.Message ?? e.Message}", e);
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> into <paramref name="path"/>, through <c>&lt;path&gt;.part</c>. A part
    /// left by an earlier try (the launcher was closed, the line dropped) is continued with an HTTP
    /// Range request; a server that ignores the range sends the whole file and the part starts over.
    /// The caller checks the result against SHA256SUMS: a part that was continued wrongly fails there.
    /// </summary>
    public async Task DownloadAsync(Uri url, string path, IProgress<(long Got, long? Total)>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(path);
        var part = path + ".part";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var have = File.Exists(part) ? new FileInfo(part).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                if (have > 0)
                {
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                }
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                if (res.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && attempt == 0)
                {
                    File.Delete(part); // longer than the file: not this file's part
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                {
                    throw new LauncherException($"download {url}: HTTP {(int)res.StatusCode}");
                }
                var resumed = have > 0 && res.StatusCode == System.Net.HttpStatusCode.PartialContent;
                var start = resumed ? have : 0;
                var total = res.Content.Headers.ContentLength is { } length ? start + length : (long?)null;
                using var body = await res.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
                using (var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    var buffer = new byte[81920];
                    var got = start;
                    progress?.Report((got, total));
                    int n;
                    while ((n = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, n), limit.Token).ConfigureAwait(false);
                        got += n;
                        limit.CancelAfter(Timeout);
                        progress?.Report((got, total));
                    }
                    if (total is { } all && got != all)
                    {
                        throw new LauncherException($"download {url}: the connection closed early"); // the part stays: the next try continues it
                    }
                }
                File.Move(part, path, overwrite: true);
                return;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new LauncherException($"download {url}: timed out");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"download {url}: {e.InnerException?.Message ?? e.Message}", e);
        }
    }

    public async Task<IReadOnlyList<Release>> ReleasesAsync(CancellationToken cancel)
    {
        var body = await GetAsync(new Uri($"{_api}/repos/{Repo}/releases?per_page=20"), null, cancel).ConfigureAwait(false);
        if (Json.Parse(body) is not { ValueKind: JsonValueKind.Array } list)
        {
            throw new LauncherException("release list: not a list");
        }
        var releases = new List<Release>();
        foreach (var item in list.EnumerateArray())
        {
            JsonElement? r = item;
            if (r.At("tag_name").Str() is not { } tag || r.At("assets") is not { ValueKind: JsonValueKind.Array } assets)
            {
                throw new LauncherException("release list: not a release");
            }
            var files = new List<ReleaseAsset>();
            foreach (var a in assets.EnumerateArray())
            {
                if (((JsonElement?)a).At("name").Str() is { } name && Uri.TryCreate(((JsonElement?)a).At("browser_download_url").Str(), UriKind.Absolute, out var url))
                {
                    files.Add(new ReleaseAsset(name, url));
                }
            }
            releases.Add(new Release(tag, r.At("draft").Bool() ?? false, r.At("body").Str(), files));
        }
        return releases;
    }

    /// <summary>The release's verified SHA256SUMS, with the bytes as signed (to keep a copy that verifies offline).</summary>
    public async Task<SignedSums> SumsAsync(Release release, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(release);
        var version = release.Version ?? throw new LauncherException("release tag is not a version");
        var sums = await GetAsync(release.Url("SHA256SUMS"), null, cancel).ConfigureAwait(false);
        var sig = Encoding.UTF8.GetString(await GetAsync(release.Url("SHA256SUMS.sig"), null, cancel).ConfigureAwait(false));
        return new SignedSums(VerifySums(sums, sig, _keys, version), sums, sig);
    }

    /// <summary>Downloads <paramref name="name"/> from the release and checks it against the verified sums.</summary>
    public async Task<byte[]> AssetAsync(Release release, SignedSums sums, string name, IProgress<(long Got, long? Total)>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(sums);
        if (!sums.Hashes.TryGetValue(name, out var want))
        {
            throw new LauncherException($"{name} is not in SHA256SUMS");
        }
        var bytes = await GetAsync(release.Url(name), progress, cancel).ConfigureAwait(false);
        return Sha256Hex(bytes) == want ? bytes : throw new LauncherException($"{name}: checksum mismatch");
    }

    /// <summary>This launcher's own release (tag <c>v&lt;version&gt;</c>), verified.</summary>
    public async Task<(Release Release, SignedSums Sums)> OwnReleaseAsync(string version, CancellationToken cancel)
    {
        var tag = $"v{version}";
        var release = (await ReleasesAsync(cancel).ConfigureAwait(false)).FirstOrDefault(r => r.Tag == tag && !r.Draft) ?? throw new LauncherException($"no release {tag}");
        return (release, await SumsAsync(release, cancel).ConfigureAwait(false));
    }

    /// <summary>Removes the <c>&lt;exe&gt;.old</c> left by an update of a launcher from before the helper (it renamed the running executable aside).</summary>
    public static void CleanOld(string exe)
    {
        try
        {
            File.Delete(exe + ".old");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the previous launcher is still closing: next start
        }
    }
}

/// <summary>A verified SHA256SUMS: the hashes, and the signed bytes and signature they came from.</summary>
public sealed record SignedSums(IReadOnlyDictionary<string, string> Hashes, byte[] Bytes, string Signature);
