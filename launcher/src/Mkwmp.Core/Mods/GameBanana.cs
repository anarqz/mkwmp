// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core.Modding;

public enum GbSort
{
    Popular,
    Newest,
    Updated,
    Liked,
}

/// <summary>A mod of GameBanana's list. <paramref name="Rated"/>: GameBanana marks its content (its pictures are shown blurred unless the player chose otherwise).</summary>
public sealed record GbMod(int Id, string Name, string Author, int Likes, int Views, string Category, string Url, string Thumb, bool Rated, long Added, long Modified, string Version);

/// <param name="Total">How many mods the query has, over all pages.</param>
public sealed record GbPage(IReadOnlyList<GbMod> Mods, int Page, int Total)
{
    public int Pages => Math.Max(1, (Total + GameBanana.PerPage - 1) / GameBanana.PerPage);
}

/// <param name="Clean">GameBanana's virus scan calls it clean: only such a file is ever downloaded.</param>
public sealed record GbFile(int Id, string Name, long Size, string Md5, bool Clean, string Scan, string Note, string Version, long Added);

/// <param name="Small">530 pixels wide.</param>
public sealed record GbImage(string Small, string Full);

/// <param name="Text">The description, as plain text.</param>
/// <param name="Ratings">GameBanana's content ratings, in its words ("Sexual Themes, Crude Humor"); "" when it has none.</param>
public sealed record GbDetails(GbMod Mod, string Text, string License, string Ratings, IReadOnlyList<GbFile> Files, IReadOnlyList<GbImage> Images);

/// <summary>
/// GameBanana's MKW mods (launcher/docs/MODS.md): the list in an order, a search, a mod's
/// page and the download of one of its files. The public apiv11, no key; what mkw-recomp's own start
/// screen asks (tools/mod/gamebanana.py there), with the same manners: a User-Agent of ours, answers kept
/// ten minutes (in memory and in the launcher's <c>cache\gamebanana\</c>), at most sixty requests a minute,
/// twenty seconds for an answer. Nothing is hosted by MKWMP: the mods are their authors', fetched from
/// GameBanana when the player asks. A download comes only from gamebanana.com, only a file its scan
/// calls clean, and its MD5 is checked.
/// </summary>
public sealed partial class GameBanana
{
    /// <summary>MKW on GameBanana.</summary>
    public const int Game = 22152;
    public const int PerPage = 20;

    private const string Api = "https://gamebanana.com/apiv11";
    private const int MaxJson = 4 << 20;
    private const int MaxImage = 4 << 20;
    private const int PerMinute = 60;
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Answer = TimeSpan.FromSeconds(20);

    private readonly HttpClient _http;
    private readonly string _cache;
    private readonly string _userAgent;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _requests = new();
    private readonly Dictionary<string, (DateTimeOffset At, byte[] Body)> _memory = new(StringComparer.Ordinal);

    public GameBanana(HttpClient http, string cacheFolder, string userAgent, TimeProvider? clock = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _cache = cacheFolder ?? throw new ArgumentNullException(nameof(cacheFolder));
        _userAgent = userAgent ?? throw new ArgumentNullException(nameof(userAgent));
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>How many requests went to GameBanana (a cached answer is none): for the tests.</summary>
    public int Requests { get; private set; }

    internal static string SortName(GbSort sort) => sort switch
    {
        GbSort.Newest => "Generic_Newest",
        GbSort.Updated => "Generic_LatestUpdated",
        GbSort.Liked => "Generic_MostLiked",
        _ => "Generic_MostDownloaded",
    };

    public async Task<GbPage> BrowseAsync(GbSort sort, int page, CancellationToken cancel)
    {
        page = Math.Clamp(page, 1, 500);
        var body = await GetJsonAsync($"{Api}/Mod/Index?_aFilters%5BGeneric_Game%5D={Game}&_nPage={page}&_nPerpage={PerPage}&_sSort={SortName(sort)}", cancel).ConfigureAwait(false);
        return ParsePage(body, page);
    }

    public async Task<GbPage> SearchAsync(string query, int page, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(query);
        page = Math.Clamp(page, 1, 500);
        var text = query.Trim();
        text = text.Length > 80 ? text[..80] : text;
        var body = await GetJsonAsync($"{Api}/Util/Search/Results?_sSearchString={Uri.EscapeDataString(text)}&_idGameRow={Game}&_sModelName=Mod&_nPage={page}&_nPerpage={PerPage}", cancel).ConfigureAwait(false);
        return ParsePage(body, page);
    }

    /// <summary>A mod's page. Throws <see cref="LauncherException"/> for a mod of another game.</summary>
    public async Task<GbDetails> DetailsAsync(int id, CancellationToken cancel)
    {
        var body = await GetJsonAsync($"{Api}/Mod/{id}/ProfilePage", cancel).ConfigureAwait(false);
        return ParseDetails(body, id);
    }

    // ---- parsing ----------------------------------------------------------------------------

    internal static GbPage ParsePage(byte[] json, int page)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var mods = new List<GbMod>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("_aRecords", out var records) && records.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in records.EnumerateArray())
                {
                    if (r.ValueKind == JsonValueKind.Object && (Str(r, "_sModelName") ?? "Mod") == "Mod" && Row(r) is { } mod)
                    {
                        mods.Add(mod);
                    }
                }
            }
            var total = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("_aMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object ? Int(meta, "_nRecordCount") : mods.Count;
            return new GbPage(mods, page, Math.Max(total, mods.Count));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new LauncherException(Strings.GameBananaFailed(e.Message), e);
        }
    }

    private static GbMod? Row(JsonElement r)
    {
        var id = Int(r, "_idRow");
        if (id <= 0)
        {
            return null;
        }
        var thumb = "";
        if (Images(r).FirstOrDefault() is { ValueKind: JsonValueKind.Object } first && Str(first, "_sBaseUrl") is { } baseUrl && (Str(first, "_sFile220") ?? Str(first, "_sFile")) is { } file)
        {
            thumb = Picture($"{baseUrl}/{file}");
        }
        var rated = (r.TryGetProperty("_bHasContentRatings", out var has) && has.ValueKind == JsonValueKind.True)
            || (r.TryGetProperty("_aContentRatings", out var ratings) && ratings.ValueKind == JsonValueKind.Object && ratings.EnumerateObject().Any())
            || Str(r, "_sInitialVisibility") is "hide" or "warn";
        return new GbMod(
            id,
            Plain(Str(r, "_sName"), 128),
            Plain(Obj(r, "_aSubmitter") is { } who ? Str(who, "_sName") : "", 64),
            Int(r, "_nLikeCount"),
            Int(r, "_nViewCount"),
            Plain(Obj(r, "_aRootCategory") is { } root ? Str(root, "_sName") : Obj(r, "_aCategory") is { } category ? Str(category, "_sName") : "", 48),
            $"https://gamebanana.com/mods/{id}",
            thumb,
            rated,
            Long(r, "_tsDateAdded"),
            Math.Max(Long(r, "_tsDateModified"), Long(r, "_tsDateUpdated")),
            Plain(Str(r, "_sVersion"), 32));
    }

    internal static GbDetails ParseDetails(byte[] json, int id)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var p = document.RootElement;
            if (p.ValueKind != JsonValueKind.Object || Obj(p, "_aGame") is not { } game || Int(game, "_idRow") != Game || Row(p) is not { } mod || mod.Id != id)
            {
                throw new LauncherException(Strings.GameBananaNotThisGame);
            }
            var files = new List<GbFile>();
            if (p.TryGetProperty("_aFiles", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in list.EnumerateArray().Take(32))
                {
                    if (f.ValueKind == JsonValueKind.Object && Int(f, "_idRow") is > 0 and var file)
                    {
                        var scan = Plain(Str(f, "_sAvResult"), 32);
                        files.Add(new GbFile(file, Plain(Str(f, "_sFile"), 128), Long(f, "_nFilesize"), (Str(f, "_sMd5Checksum") ?? "").ToLowerInvariant(), scan == "clean", scan,
                            Plain(Str(f, "_sDescription"), 200), Plain(Str(f, "_sVersion"), 32), Long(f, "_tsDateAdded")));
                    }
                }
            }
            var images = Images(p).Take(8).Where(i => i.ValueKind == JsonValueKind.Object && Str(i, "_sBaseUrl") is not null && Str(i, "_sFile") is not null)
                .Select(i => new GbImage(Picture($"{Str(i, "_sBaseUrl")}/{Str(i, "_sFile530") ?? Str(i, "_sFile")}"), Picture($"{Str(i, "_sBaseUrl")}/{Str(i, "_sFile")}")))
                .Where(i => i.Small.Length > 0).ToList();
            var ratings = p.TryGetProperty("_aContentRatings", out var rated) && rated.ValueKind == JsonValueKind.Object
                ? string.Join(", ", rated.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.String).Select(x => Plain(x.Value.GetString(), 40)))
                : "";
            return new GbDetails(mod with { Rated = mod.Rated || ratings.Length > 0 }, Plain(Str(p, "_sText"), 4000, lines: true), License(Str(p, "_sLicense")), ratings, files, images);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            throw new LauncherException(Strings.GameBananaFailed(e.Message), e);
        }
    }

    private static List<JsonElement> Images(JsonElement e) =>
        Obj(e, "_aPreviewMedia") is { } media && media.TryGetProperty("_aImages", out var images) && images.ValueKind == JsonValueKind.Array ? [.. images.EnumerateArray()] : [];

    /// <summary>A picture's address, only when it is on GameBanana's image host; "" otherwise.</summary>
    private static string Picture(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.Host == "images.gamebanana.com" ? u.AbsoluteUri : "";

    private static JsonElement? Obj(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) => (int)Math.Clamp(Long(e, name), 0, int.MaxValue);

    private static long Long(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? Math.Max(0, n) : 0;

    [GeneratedRegex(@"<br\s*/?>|</p>|</li>|</h\d>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"creativecommons\.org/licenses/([a-z-]+)/([\d.]+)")]
    private static partial Regex CreativeCommons();

    /// <summary>HTML as plain text (the launcher draws none), cut at <paramref name="most"/>.</summary>
    internal static string Plain(string? html, int most, bool lines = false)
    {
        var text = Breaks().Replace(html ?? "", "\n");
        text = WebUtility.HtmlDecode(Tags().Replace(text, ""));
        var kept = text.Split('\n').Select(l => string.Join(' ', l.Split([' ', '\t', '\r', ' '], StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0);
        text = new string([.. string.Join(lines ? "\n" : " ", kept).Where(c => !char.IsControl(c) || c == '\n')]);
        return text.Length > most ? text[..most].TrimEnd() + "…" : text;
    }

    private static string License(string? raw) =>
        CreativeCommons().Match(raw ?? "") is { Success: true } m ? $"CC {m.Groups[1].Value.ToUpperInvariant()} {m.Groups[2].Value}" : Plain(raw, 200);

    // ---- the network ------------------------------------------------------------------------

    private static string KeyOf(string url) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..24];

    /// <summary>At most <see cref="PerMinute"/> requests in any minute: a request over that is refused, not queued (the page says so).</summary>
    private void Count()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            while (_requests.Count > 0 && now - _requests.Peek() > TimeSpan.FromMinutes(1))
            {
                _requests.Dequeue();
            }
            if (_requests.Count >= PerMinute)
            {
                throw new LauncherException(Strings.GameBananaBusy);
            }
            _requests.Enqueue(now);
            Requests++;
        }
    }

    private async Task<byte[]> GetJsonAsync(string url, CancellationToken cancel)
    {
        var key = KeyOf(url);
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out var kept) && now - kept.At < Fresh)
            {
                return kept.Body;
            }
        }
        var file = Path.Combine(_cache, key + ".json");
        try
        {
            if (File.Exists(file) && now - File.GetLastWriteTimeUtc(file) < Fresh && new FileInfo(file).Length <= MaxJson)
            {
                var cached = await File.ReadAllBytesAsync(file, cancel).ConfigureAwait(false);
                lock (_gate)
                {
                    _memory[key] = (File.GetLastWriteTimeUtc(file), cached);
                }
                return cached;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a damaged or locked entry is a miss
        }
        var body = await GetAsync(url, "application/json", MaxJson, cancel).ConfigureAwait(false);
        lock (_gate)
        {
            _memory[key] = (now, body);
            if (_memory.Count > 64)
            {
                _memory.Remove(_memory.MinBy(p => p.Value.At).Key);
            }
        }
        try
        {
            Directory.CreateDirectory(_cache);
            var temporary = file + ".tmp";
            await File.WriteAllBytesAsync(temporary, body, cancel).ConfigureAwait(false);
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // not kept on disk: asked again next time
        }
        return body;
    }

    private async Task<byte[]> GetAsync(string url, string accept, int most, CancellationToken cancel)
    {
        Count();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Answer);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(Strings.GameBananaFailed($"HTTP {(int)response.StatusCode}"));
            }
            if (response.Content.Headers.ContentLength > most)
            {
                throw new LauncherException(Strings.GameBananaFailed("too large"));
            }
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var memory = new MemoryStream();
            var buffer = new byte[1 << 15];
            int n;
            while ((n = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (memory.Length + n > most)
                {
                    throw new LauncherException(Strings.GameBananaFailed("too large"));
                }
                memory.Write(buffer, 0, n);
            }
            return memory.ToArray();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new LauncherException(Strings.GameBananaFailed("timeout"));
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            throw new LauncherException(Strings.GameBananaFailed(e.Message), e);
        }
    }

    /// <summary>
    /// A preview picture as a file of the cache (fetched once, named by its address' hash); null when it is
    /// not on GameBanana's image host, too large, or did not come: a picture that does not come is just not shown.
    /// </summary>
    public async Task<string?> ImageAsync(string url, CancellationToken cancel)
    {
        if (Picture(url ?? "") is not { Length: > 0 } address)
        {
            return null;
        }
        var extension = Path.GetExtension(new Uri(address).AbsolutePath).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            return null;
        }
        var folder = Path.Combine(_cache, "img");
        var path = Path.Combine(folder, KeyOf(address) + extension);
        try
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }
            var bytes = await GetAsync(address, "image/*", MaxImage, cancel).ConfigureAwait(false);
            Directory.CreateDirectory(folder);
            var temporary = path + "." + Guid.NewGuid().ToString("N")[..6];
            await File.WriteAllBytesAsync(temporary, bytes, cancel).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            return path;
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads one file of a mod into <paramref name="folder"/> and returns its path. Throws
    /// <see cref="LauncherException"/>: a file that is not the mod's, one the scan does not call clean, an
    /// address or a redirect that leaves gamebanana.com, a size or an MD5 that differs.
    /// </summary>
    public async Task<string> DownloadAsync(GbDetails mod, GbFile file, string folder, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ArgumentNullException.ThrowIfNull(file);
        if (!mod.Files.Contains(file))
        {
            throw new LauncherException(Strings.GameBananaNotThisGame);
        }
        if (!file.Clean)
        {
            throw new LauncherException(Strings.GameBananaNotClean(file.Scan.Length > 0 ? file.Scan : "?"));
        }
        Count();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5); // GameBanana's own checksum of the file: a damaged download, not a signature
        var path = await ModDownloader.FetchAsync(_http, new Uri($"https://gamebanana.com/dl/{file.Id}"), folder, file.Name.Length > 0 ? file.Name : $"{file.Id}.zip", _userAgent,
            host => host == "gamebanana.com" || host.EndsWith(".gamebanana.com", StringComparison.Ordinal), file.Size, md5, progress, cancel).ConfigureAwait(false);
        if (file.Md5.Length > 0 && Convert.ToHexStringLower(md5.GetHashAndReset()) != file.Md5)
        {
            File.Delete(path);
            throw new LauncherException(Strings.GameBananaChecksum);
        }
        return path;
    }
}

/// <summary>
/// A mod's archive from a link into the launcher's <c>downloads\</c> folder (beside the exe: the launcher
/// is portable). HTTPS only, every redirect too; the limits of an install (<c>ModImport</c>);
/// written as <c>.part</c> and renamed when whole.
/// </summary>
public static partial class ModDownloader
{
    public const long MaxBytes = 4L << 30;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);

    [GeneratedRegex(@"[^\w.\- ]")]
    private static partial Regex NotInAName();

    /// <summary>A link a mod can be downloaded from: absolute, https, with a host and no user or password in it; null otherwise.</summary>
    public static Uri? Link(string? text) =>
        Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && uri.UserInfo.Length == 0 ? uri : null;

    internal static string FileName(string? name)
    {
        var clean = NotInAName().Replace(Path.GetFileName((name ?? "").Replace('\\', '/')), "_").Trim(' ', '.');
        clean = clean.Length > 100 ? clean[^100..] : clean;
        return clean.Length == 0 ? "mod.zip" : clean;
    }

    /// <summary>Downloads <paramref name="link"/>; the file's name is the answer's (Content-Disposition), else the link's last part.</summary>
    public static Task<string> DownloadAsync(HttpClient http, Uri link, string folder, string userAgent, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(link);
        return Link(link.OriginalString) is null
            ? throw new LauncherException(Strings.ModsBadLink)
            : FetchAsync(http, link, folder, null, userAgent, _ => true, 0, null, progress, cancel);
    }

    internal static async Task<string> FetchAsync(HttpClient http, Uri link, string folder, string? name, string userAgent, Func<string, bool> hostOk, long expected, IncrementalHash? hash,
        IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(folder);
        string? part = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, link);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            headers.CancelAfter(Idle);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            var final = response.RequestMessage?.RequestUri ?? link;
            if (final.Scheme != Uri.UriSchemeHttps || !hostOk(final.Host))
            {
                throw new LauncherException(Strings.ModsDownloadFailed(final.Host));
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(Strings.ModsDownloadFailed($"HTTP {(int)response.StatusCode}"));
            }
            var length = response.Content.Headers.ContentLength ?? 0;
            if (length > MaxBytes || (expected > 0 && length > 0 && length != expected))
            {
                throw new LauncherException(length > MaxBytes ? Strings.ModsDownloadTooBig : Strings.GameBananaChecksum);
            }
            name ??= response.Content.Headers.ContentDisposition is { } disposition ? disposition.FileNameStar ?? disposition.FileName?.Trim('"') : null;
            name = FileName(string.IsNullOrWhiteSpace(name) ? Uri.UnescapeDataString(final.AbsolutePath) : name);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            part = path + ".part";
            var total = expected > 0 ? expected : length;
            long done = 0;
            await using (var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                var buffer = new byte[1 << 16];
                var last = 0L;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    idle.CancelAfter(Idle);
                    var n = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }
                    if ((done += n) > MaxBytes || (expected > 0 && done > expected))
                    {
                        throw new LauncherException(Strings.ModsDownloadTooBig);
                    }
                    hash?.AppendData(buffer, 0, n);
                    await file.WriteAsync(buffer.AsMemory(0, n), cancel).ConfigureAwait(false);
                    if (done - last >= 1 << 18)
                    {
                        last = done;
                        progress?.Report(new ModProgress("download", done, total));
                    }
                }
            }
            if (expected > 0 && done != expected)
            {
                throw new LauncherException(Strings.GameBananaChecksum);
            }
            progress?.Report(new ModProgress("download", done, Math.Max(total, done)));
            File.Move(part, path, overwrite: true);
            part = null;
            return path;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new LauncherException(Strings.ModsDownloadFailed("timeout"));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(Strings.ModsDownloadFailed(e.Message), e);
        }
        finally
        {
            if (part is not null)
            {
                try
                {
                    File.Delete(part);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // a .part left in downloads\: written over by the next download of that name
                }
            }
        }
    }
}
