// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mkwmp.Core.Net;

/// <summary>
/// How Nextendo draws someone, as the friends list says it without their picture (<c>images=0</c>).
/// </summary>
/// <param name="Color">The background of their picture, "#rrggbb" (lowercase); "" when Nextendo gave none.</param>
/// <param name="Key">Changes when they chose another picture in Nextendo's gallery or another colour: 16 hex digits made of <c>avatar</c> and <c>color</c>.</param>
internal sealed record NextendoLook(string Color, string Key)
{
    /// <summary>Of one entry of <c>/api/friends</c>. Whatever <c>avatar</c> holds (an icon's id, or a composition as JSON) is never kept: only what tells it changed.</summary>
    public static NextendoLook Of(JsonElement? v)
    {
        var color = HexColor(v.At("color").Str());
        var avatar = v.At("avatar").Str() ?? "";
        return new NextendoLook(color, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(avatar + "\n" + color)))[..16]);
    }

    /// <summary>"#rrggbb" in lowercase from "#RRGGBB" or "#rgb"; "" for anything else (the text ends in a brush: nothing but a colour gets there).</summary>
    internal static string HexColor(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length is not (4 or 7) || t[0] != '#' || !t.AsSpan(1).ToString().All(char.IsAsciiHexDigit))
        {
            return "";
        }
        var digits = t.Length == 4 ? string.Concat(t.Skip(1).Select(c => new string(c, 2))) : t[1..];
        return "#" + digits.ToLowerInvariant();
    }
}

/// <summary>Whose picture is wanted, the <see cref="NextendoLook.Key"/> it must be of (<see cref="NextendoAvatars.NoKey"/>: not known), and how old it may get.</summary>
internal readonly record struct NextendoAvatarWant(long Pid, string Key, TimeSpan Fresh);

/// <summary>
/// The profile pictures of the people the window shows on Nextendo Network (the player, friends, who asked, who was
/// asked, who was met), kept as files on this PC.
/// <para>
/// A picture comes from <c>GET https://nextendo.network/api/avatar?pid=…</c>: the public route Nextendo's own site
/// uses (a 256×256 JPEG of some 25 kB, <c>Cache-Control: public, max-age=300</c>, no validator). It is asked without
/// the sign-in: no token ever goes with it. The friends list stays <c>images=0</c> (with the pictures inline it is
/// megabytes, every 30 s).
/// </para>
/// <para>
/// One picture at a time, with a pause between two, and only when it is missing, when the person's
/// <see cref="NextendoLook.Key"/> changed, or when it is older than its <see cref="NextendoAvatarWant.Fresh"/> (a day;
/// an hour for the player's own, which has no key): the 30 s friends poll asks for nothing while nothing is due. "No
/// picture" is remembered the same way. A 429 is obeyed (<c>Retry-After</c>), a failure waits longer and longer.
/// </para>
/// <para>
/// A file is named by a hash of the pid, the key and a hash of its bytes (<c>&lt;who&gt;-&lt;key&gt;-&lt;what&gt;.img</c>):
/// no pid and no name is on disk or in a log. Only JPEG and PNG are kept (by their first bytes; never by what the
/// server calls them), 2 MB at most each and 50 MB in all (the least recently wanted go first). Nothing in a file is
/// ever run: the window decodes it as a bitmap.
/// </para>
/// </summary>
internal sealed class NextendoAvatars : IDisposable
{
    public const string FolderName = "nextendo-avatars";
    public const int MaxBytes = 2 * 1024 * 1024;
    public const long MaxTotal = 50L * 1024 * 1024;

    /// <summary>The key of someone whose look is not known (the player, a player met): any picture in hand is theirs until it is old.</summary>
    public const string NoKey = "0";

    /// <summary>A friend's picture is asked again after this long (or at once when their key changes).</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(24);

    /// <summary>The player's own (no key tells when it changes).</summary>
    public static readonly TimeSpan OwnFresh = TimeSpan.FromHours(1);

    /// <summary>The pause between two pictures.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(250);

    /// <summary>One picture that could not be written is left alone this long.</summary>
    public static readonly TimeSpan RetryOne = TimeSpan.FromMinutes(15);

    /// <summary>A picture nobody wanted for this long is deleted at the next start.</summary>
    public static readonly TimeSpan Unused = TimeSpan.FromDays(30);

    private const string None = "none";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan TouchEvery = TimeSpan.FromHours(12);

    private sealed class Entry
    {
        public required string File { get; set; }
        public required string Key { get; set; }

        /// <summary>A hash of the bytes; <see cref="None"/> for "this person has no picture".</summary>
        public required string Content { get; set; }
        public long Size { get; set; }
        public DateTimeOffset At { get; set; }
        public DateTimeOffset Used { get; set; }
        public DateTimeOffset UsedSaved { get; set; }
    }

    private enum Got { Picture, Nothing, Limited, Failed }

    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly string _folder;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _note;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _ids = [];
    private readonly Dictionary<long, DateTimeOffset> _hold = [];
    private IReadOnlyList<NextendoAvatarWant> _wanted = [];
    private bool _loaded;
    private int _failures;
    private DateTimeOffset _waitUntil = DateTimeOffset.MinValue;

    /// <param name="folder">Where the pictures are kept (made when the first one arrives).</param>
    /// <param name="note">A line for the log: never who, never an address.</param>
    public NextendoAvatars(HttpClient http, string userAgent, string folder, TimeProvider clock, Action<string>? note = null)
    {
        (_http, _userAgent, _folder, _clock, _note) = (http, userAgent, folder, clock, note);
    }

    /// <summary>The most all pictures take together.</summary>
    internal long MostBytes { get; init; } = MaxTotal;

    /// <summary>A picture arrived, changed or went away. Raised outside every lock, on the thread that fetched it.</summary>
    public event Action? Changed;

    /// <summary>The address a picture is asked at: always <see cref="NextendoApi.Origin"/>, whatever Nextendo's answers name.</summary>
    internal static string Url(long pid) => NextendoApi.Origin + "/api/avatar?pid=" + pid.ToString(CultureInfo.InvariantCulture);

    /// <summary>What a file of <paramref name="pid"/> starts with: not the pid, and not to be turned back into it by reading the folder.</summary>
    internal static string Id(long pid) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes("mkwmp nextendo avatar " + pid.ToString(CultureInfo.InvariantCulture))))[..20];

    /// <summary>JPEG or PNG, by the first bytes.</summary>
    internal static bool IsPicture(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 16 && (bytes.StartsWith<byte>([0xFF, 0xD8, 0xFF]) || bytes.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));

    private string IdOf(long pid)
    {
        if (!_ids.TryGetValue(pid, out var id))
        {
            _ids[pid] = id = Id(pid);
        }
        return id;
    }

    /// <summary>The file of someone's picture; null when there is none (yet). Reads no file: cheap from the window's thread.</summary>
    public string? PathOf(long pid)
    {
        lock (_gate)
        {
            Load();
            return _entries.TryGetValue(IdOf(pid), out var entry) && entry.Content != None ? entry.File : null;
        }
    }

    /// <summary>The pictures in hand, for tests: how many, and their bytes together.</summary>
    internal (int Count, long Bytes) Held
    {
        get
        {
            lock (_gate)
            {
                Load();
                return (_entries.Values.Count(e => e.Content != None), _entries.Values.Sum(e => e.Size));
            }
        }
    }

    /// <summary>
    /// Whose pictures the window shows now, the most wanted first (it replaces the last call's). Asks nothing itself:
    /// <see cref="DrainAsync"/> does, for those that are due. True when one is.
    /// </summary>
    public bool Want(IEnumerable<NextendoAvatarWant> wants)
    {
        ArgumentNullException.ThrowIfNull(wants);
        var seen = new HashSet<long>();
        NextendoAvatarWant[] list = [.. wants.Where(w => w.Pid > 0 && seen.Add(w.Pid))];
        lock (_gate)
        {
            Load();
            _wanted = list;
            var now = _clock.GetUtcNow();
            foreach (var want in list)
            {
                if (_entries.TryGetValue(IdOf(want.Pid), out var entry))
                {
                    entry.Used = now;
                    if (now - entry.UsedSaved >= TouchEvery || now < entry.UsedSaved)
                    {
                        entry.UsedSaved = now;
                        Quiet(() => File.SetLastAccessTimeUtc(entry.File, now.UtcDateTime));
                    }
                }
            }
            foreach (var gone in _hold.Keys.Where(pid => !seen.Contains(pid)).ToList())
            {
                _hold.Remove(gone);
            }
            return NextDue(now) is not null;
        }
    }

    /// <summary>Under the lock: the first wanted picture that must be asked for now; null when none, or while Nextendo said to wait.</summary>
    private NextendoAvatarWant? NextDue(DateTimeOffset now)
    {
        if (now < _waitUntil)
        {
            return null;
        }
        foreach (var want in _wanted)
        {
            if (_hold.TryGetValue(want.Pid, out var until) && now < until)
            {
                continue;
            }
            if (!_entries.TryGetValue(IdOf(want.Pid), out var entry)
                || (want.Key != NoKey && entry.Key != want.Key)
                || now - entry.At >= want.Fresh
                || entry.At - now > TimeSpan.FromMinutes(5)) // the PC's clock went back: its age is not known
            {
                return want;
            }
        }
        return null;
    }

    /// <summary>
    /// Asks for the pictures that are due, one after the other with <paramref name="gap"/> between two, until none is
    /// due or Nextendo says to wait (a 429, a failure). One drain at a time (a second call returns at once). How many
    /// were asked for.
    /// </summary>
    public async Task<int> DrainAsync(TimeSpan gap, CancellationToken cancel)
    {
        if (!await _one.WaitAsync(0, cancel).ConfigureAwait(false))
        {
            return 0;
        }
        try
        {
            var asked = 0;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                NextendoAvatarWant? next;
                lock (_gate)
                {
                    Load();
                    next = NextDue(_clock.GetUtcNow());
                }
                if (next is not { } want)
                {
                    return asked;
                }
                if (asked > 0 && gap > TimeSpan.Zero)
                {
                    await Task.Delay(gap, cancel).ConfigureAwait(false);
                }
                var (got, bytes, wait, why) = await FetchAsync(want.Pid, cancel).ConfigureAwait(false);
                asked++;
                bool changed;
                lock (_gate)
                {
                    changed = Take(want, got, bytes, wait, why);
                }
                if (changed)
                {
                    Changed?.Invoke();
                }
                if (got is Got.Limited or Got.Failed)
                {
                    return asked;
                }
            }
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>One picture, without the sign-in. Never throws for what Nextendo or the network does.</summary>
    private async Task<(Got Got, byte[] Bytes, TimeSpan Wait, string Why)> FetchAsync(long pid, CancellationToken cancel)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, Url(pid));
        req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        req.Headers.TryAddWithoutValidation("Accept", "image/jpeg, image/png");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            var status = (int)res.StatusCode;
            if (status == 429)
            {
                return (Got.Limited, [], NextendoApi.RetryAfter(res.Headers, _clock.GetUtcNow()), "");
            }
            if (status is 408 or >= 500)
            {
                return (Got.Failed, [], TimeSpan.Zero, "HTTP " + status.ToString(CultureInfo.InvariantCulture));
            }
            // somewhere else than nextendo.network (a redirect that was followed), or no picture at all: "none", for a day
            if (res.StatusCode != HttpStatusCode.OK || res.RequestMessage?.RequestUri is { } from && from is not { Scheme: "https", Host: "nextendo.network" }
                || res.Content.Headers.ContentLength > MaxBytes)
            {
                return (Got.Nothing, [], TimeSpan.Zero, "");
            }
            using var body = await res.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var kept = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int n;
            while ((n = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                if (kept.Length + n > MaxBytes)
                {
                    return (Got.Nothing, [], TimeSpan.Zero, ""); // too large to be a profile picture
                }
                kept.Write(buffer, 0, n);
            }
            var bytes = kept.ToArray();
            return IsPicture(bytes) ? (Got.Picture, bytes, TimeSpan.Zero, "") : (Got.Nothing, [], TimeSpan.Zero, "");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return (Got.Failed, [], TimeSpan.Zero, "timed out");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return (Got.Failed, [], TimeSpan.Zero, e.GetType().Name); // not the message: it may name an address
        }
    }

    /// <summary>Under the lock: what was fetched becomes the person's file. True when what the window shows of them changed.</summary>
    private bool Take(NextendoAvatarWant want, Got got, byte[] bytes, TimeSpan wait, string why)
    {
        var now = _clock.GetUtcNow();
        if (got == Got.Limited)
        {
            _waitUntil = now + wait;
            return false;
        }
        if (got == Got.Failed)
        {
            _failures++;
            _waitUntil = now + NextendoRoster.Backoff(_failures);
            if (_failures == 1)
            {
                _note?.Invoke($"nextendo: profile pictures do not come ({why}); trying again later");
            }
            return false;
        }
        _failures = 0;
        var id = IdOf(want.Pid);
        _entries.TryGetValue(id, out var old);
        var key = want.Key != NoKey ? want.Key : old?.Key ?? NoKey;
        var content = got == Got.Picture ? Convert.ToHexStringLower(SHA256.HashData(bytes))[..12] : None;
        var file = Path.Combine(_folder, $"{id}-{key}-{content}.img");
        try
        {
            Directory.CreateDirectory(_folder);
            if (old is null || !string.Equals(old.File, file, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                var part = file + ".tmp";
                File.WriteAllBytes(part, bytes);
                File.Move(part, file, overwrite: true);
                if (old is not null && !string.Equals(old.File, file, StringComparison.OrdinalIgnoreCase))
                {
                    Quiet(() => File.Delete(old.File));
                }
            }
            File.SetLastWriteTimeUtc(file, now.UtcDateTime);
            File.SetLastAccessTimeUtc(file, now.UtcDateTime);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _hold[want.Pid] = now + RetryOne; // a folder that cannot be written: what was shown stays
            return false;
        }
        var changed = (old?.Content ?? None) != content;
        _entries[id] = new Entry { File = file, Key = key, Content = content, Size = bytes.Length, At = now, Used = now, UsedSaved = now };
        Evict(id);
        return changed;
    }

    /// <summary>Under the lock: the least recently wanted pictures go until all fit in <see cref="MostBytes"/> (never <paramref name="keep"/>, the one that just came).</summary>
    private void Evict(string keep)
    {
        var total = _entries.Values.Sum(e => e.Size);
        foreach (var (id, entry) in _entries.Where(e => e.Key != keep && e.Value.Size > 0).OrderBy(e => e.Value.Used).ThenBy(e => e.Value.At).ToList())
        {
            if (total <= MostBytes)
            {
                return;
            }
            total -= entry.Size;
            _entries.Remove(id);
            Quiet(() => File.Delete(entry.File));
        }
    }

    /// <summary>Under the lock, once: what the folder holds from before. A file that is not one of these, too large, a second one of the same person, or unused for long is deleted.</summary>
    private void Load()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        var now = _clock.GetUtcNow();
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }
            foreach (var info in new DirectoryInfo(_folder).EnumerateFiles().OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var parts = Path.GetFileNameWithoutExtension(info.Name).Split('-');
                var ours = info.Extension == ".img" && parts.Length == 3 && parts[0].Length == 20 && parts.All(p => p.Length is > 0 and <= 20 && p.All(char.IsAsciiLetterOrDigit));
                var used = new DateTimeOffset(info.LastAccessTimeUtc, TimeSpan.Zero);
                if (!ours || info.Length > MaxBytes || now - used > Unused || _entries.ContainsKey(parts[0]) || (info.Length == 0) != (parts[2] == None))
                {
                    if (info.Extension is ".img" or ".tmp")
                    {
                        Quiet(info.Delete);
                    }
                    continue;
                }
                _entries[parts[0]] = new Entry
                {
                    File = info.FullName, Key = parts[1], Content = parts[2], Size = info.Length,
                    At = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), Used = used, UsedSaved = used,
                };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // a folder that cannot be read: the pictures are fetched again
        }
    }

    /// <summary>The account was unlinked: no picture of anyone it knew stays on this PC.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Load();
            foreach (var entry in _entries.Values)
            {
                Quiet(() => File.Delete(entry.File));
            }
            _entries.Clear();
            _ids.Clear();
            _hold.Clear();
            _wanted = [];
            (_failures, _waitUntil) = (0, DateTimeOffset.MinValue);
        }
    }

    private static void Quiet(Action io)
    {
        try
        {
            io();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // a file in use, a folder that went away: the next start tidies up
        }
    }

    public void Dispose() => _one.Dispose();
}
