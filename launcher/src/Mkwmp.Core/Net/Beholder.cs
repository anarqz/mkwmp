// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Net;
using System.Text.Json;

namespace Mkwmp.Core.Net;

/// <summary>One announced server (docs/contracts/BEHOLDER_API.md §3). Every field is the server's own words: shown, never trusted.</summary>
internal sealed record Listing(string PublicKey, string Name, string Endpoint, string? Region, string? Version, int Capacity, int Seated, int Queued, string? FirstSeen, string? LastSeen);

/// <summary>
/// The launcher's side of the beholder, the small public directory MKWMP servers announce
/// themselves to (docs/contracts/BEHOLDER_API.md; the idea and the bounds are Infinity Launcher's):
/// it reads the list and nothing else. Bounded: https only (plain http just for this PC or the
/// LAN, for development), 64 KiB, 500 items, 5 s; the last good list is kept in a file so the
/// picker still shows something while the beholder is down. A listing is a hint: a listed server
/// is asked who it is like any typed address before it is used, and its latency is measured to
/// the server itself, never through the beholder.
/// </summary>
internal static class BeholderClient
{
    /// <summary>
    /// The release's default beholder: the public directory MKWMP servers announce themselves to
    /// (live since 2026-11-20). A <c>beholder</c> in client.toml replaces it; with neither, the
    /// launcher lists no servers and only a typed address connects.
    /// </summary>
    public const string DefaultUrl = "https://beholder.anarqz.dev";

    public const string CacheFileName = "beholder-cache.json";
    public const int MaxItems = 500;
    public const int MaxBytes = 64 * 1024;
    public const int MaxName = 64;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>How many times a server is timed, and how many servers at once.</summary>
    public const int Samples = 3;
    public const int Parallel = 6;
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The beholder's URL when it may be asked: https, or plain http on this PC or the LAN; else null.</summary>
    public static Uri? Allowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Host.Length == 0)
        {
            return null;
        }
        return uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLocal(uri.Host)) ? uri : null;
    }

    /// <summary>This PC or a private network: loopback, 10/8, 172.16/12, 192.168/16, 169.254/16, or a bare or .local/.lan name.</summary>
    internal static bool IsLocal(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip))
        {
            return false;
        }
        if (IPAddress.IsLoopback(ip))
        {
            return true;
        }
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254));
    }

    /// <summary><c>GET {beholder}/beholder/v1/servers</c>. Throws <see cref="ApiException"/> (transient) with why it could not be read.</summary>
    public static async Task<IReadOnlyList<Listing>> ListAsync(HttpClient http, Uri beholder, string userAgent, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Timeout);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(beholder, "/beholder/v1/servers"));
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw ApiException.Transient(Strings.ServerAnswered((int)res.StatusCode));
            }
            if (res.Content.Headers.ContentLength is > MaxBytes)
            {
                throw ApiException.Transient(Strings.BeholderTooLarge);
            }
            await using var body = await res.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            var bytes = new byte[MaxBytes + 1];
            var read = 0;
            for (int n; read < bytes.Length && (n = await body.ReadAsync(bytes.AsMemory(read), limit.Token).ConfigureAwait(false)) > 0;)
            {
                read += n;
            }
            if (read > MaxBytes)
            {
                throw ApiException.Transient(Strings.BeholderTooLarge);
            }
            return Parse(bytes.AsSpan(0, read)) ?? throw ApiException.Transient(Strings.BadAnswerWhy("not a server list"));
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw ApiException.Transient(Strings.CannotReach("timed out"));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            throw ApiException.Transient(Strings.CannotReach(e.InnerException?.Message ?? e.Message));
        }
    }

    /// <summary><c>{"items":[…]}</c>: malformed rows dropped, texts made printable and short, one row per endpoint, at most <see cref="MaxItems"/>; null when it is not that shape.</summary>
    internal static IReadOnlyList<Listing>? Parse(ReadOnlySpan<byte> json)
    {
        if (Json.Parse(json).At("items") is not { ValueKind: JsonValueKind.Array } items)
        {
            return null;
        }
        var list = new List<Listing>();
        foreach (var raw in items.EnumerateArray())
        {
            JsonElement? e = raw;
            var endpoint = Endpoint(e.At("endpoint").Str());
            if (endpoint is null || e.At("publicKey").Str() is not { Length: > 0 and <= 128 } key || list.Any(l => ClientConfig.SameServer(l.Endpoint, endpoint)))
            {
                continue;
            }
            var name = Printable(e.At("name").Str(), MaxName);
            list.Add(new Listing(
                key,
                name.Length > 0 ? name : endpoint,
                endpoint,
                Printable(e.At("region").Str(), 32) is { Length: > 0 } region ? region : null,
                Printable(e.At("version").Str(), 32) is { Length: > 0 } version ? version : null,
                (int)Math.Clamp(e.At("capacity").Long() ?? 0, 0, 100_000),
                (int)Math.Clamp(e.At("seated").Long() ?? 0, 0, 100_000),
                (int)Math.Clamp(e.At("queued").Long() ?? 0, 0, 100_000),
                Printable(e.At("firstSeen").Str(), 32),
                Printable(e.At("lastSeen").Str(), 32)));
            if (list.Count == MaxItems)
            {
                break;
            }
        }
        return list;
    }

    /// <summary>A listed endpoint the launcher would contact: <c>http(s)://host[:port]</c>, nothing after it; else null.</summary>
    internal static string? Endpoint(string? endpoint)
    {
        if (endpoint is not { Length: > 0 and <= 255 } || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.AbsolutePath is not ("/" or ""))
        {
            return null;
        }
        return ClientConfig.ServerBase(endpoint);
    }

    private static string Printable(string? text, int max)
    {
        var kept = new string([.. (text ?? "").Where(c => !char.IsControl(c) && !char.IsSurrogate(c) && c is not (>= '‎' and <= '‏') and not (>= '‪' and <= '‮') and not (>= '⁦' and <= '⁩'))]).Trim();
        return kept.Length > max ? kept[..max] : kept;
    }

    public static void Save(string folder, IReadOnlyList<Listing> servers)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, CacheFileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new { items = servers.Select(s => new
        {
            publicKey = s.PublicKey, name = s.Name, endpoint = s.Endpoint, region = s.Region, version = s.Version, capacity = s.Capacity,
            seated = s.Seated, queued = s.Queued, firstSeen = s.FirstSeen, lastSeen = s.LastSeen,
        }) }));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>The list saved by the last good read; null when there is none.</summary>
    public static IReadOnlyList<Listing>? Load(string folder)
    {
        try
        {
            var path = Path.Combine(folder, CacheFileName);
            return File.Exists(path) && new FileInfo(path).Length <= 4 * MaxBytes ? Parse(File.ReadAllBytes(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The round trip to a server itself: the median of <see cref="Samples"/> timed
    /// <c>GET {endpoint}/ping</c> (or of its status, for a server before 1.4.0), after one status
    /// request that says who it is and gives its seats; null when it did not answer as an MKWMP server.
    /// </summary>
    public static async Task<(int Milliseconds, SeatStatus Status)?> PingAsync(HttpClient http, string endpoint, string userAgent, TimeProvider clock, CancellationToken cancel)
    {
        var api = new SeatApi(http, endpoint, userAgent);
        var times = new List<double>();
        try
        {
            // Who it is, and its seats: the listing is only a hint. This request also opens the connection, so it is not timed.
            using var first = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            first.CancelAfter(PingTimeout);
            var status = await api.StatusAsync(first.Token).ConfigureAwait(false);
            // GET /ping (204, servers from 1.4.0) is the cheap thing to time; an older server's status is timed instead.
            var ping = true;
            for (var i = 0; i < Samples; i++)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(PingTimeout);
                var began = clock.GetTimestamp();
                if (ping && !await api.PingAsync(PingTimeout, limit.Token).ConfigureAwait(false))
                {
                    ping = false;
                    began = clock.GetTimestamp();
                }
                if (!ping)
                {
                    status = await api.StatusAsync(limit.Token).ConfigureAwait(false);
                }
                times.Add(clock.GetElapsedTime(began).TotalMilliseconds);
            }
            times.Sort();
            return ((int)Math.Round(times[times.Count / 2]), status);
        }
        catch (ApiException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return null;
        }
    }
}

/// <summary>What the window may know about the announcer (beholder) address: the release default and whether an address is acceptable.</summary>
public static class Announcer
{
    public static string DefaultUrl => BeholderClient.DefaultUrl;

    /// <summary>https anywhere, http only on this PC or the local network (as the list itself is fetched).</summary>
    public static bool IsAllowed(string? url) => BeholderClient.Allowed(url) is not null;
}
