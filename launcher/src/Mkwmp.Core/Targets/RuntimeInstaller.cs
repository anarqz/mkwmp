// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mkwmp.Core.Targets;

public enum RuntimePhase
{
    Downloading,
    Verifying,
    Extracting,

    /// <summary>Putting the files in place (short: moves inside one folder).</summary>
    Installing,
}

/// <param name="Done">Bytes done in this phase (a resumed download starts above zero).</param>
/// <param name="Total">Bytes of this phase.</param>
/// <param name="BytesPerSecond">Over the last seconds; 0 while not known yet.</param>
/// <param name="Remaining">Of this phase; null while not known yet.</param>
public readonly record struct RuntimeProgress(RuntimePhase Phase, long Done, long Total, double BytesPerSecond, TimeSpan? Remaining)
{
    /// <summary>0..1.</summary>
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>What a folder is, to the installer.</summary>
public enum RuntimeFolderKind
{
    /// <summary>Missing or empty: a new install goes here.</summary>
    Empty,

    /// <summary>An install this launcher made (it carries <see cref="RuntimeInstaller.Marker"/>).</summary>
    Managed,

    /// <summary>An install of the same fork made by hand: updated in place.</summary>
    SameLineage,

    /// <summary>Somebody else's install (the upstream release, an unknown build): never touched without <see cref="RuntimeInstallOptions.ReplaceForeign"/>.</summary>
    Foreign,

    /// <summary>Other files, and no install: refused.</summary>
    Occupied,
}

public sealed record RuntimeInstallOptions
{
    /// <summary>
    /// Install over an install that is not ours (same rules as an update: only the runtime's
    /// files, with a backup). Off unless the player said so in as many words.
    /// </summary>
    public bool ReplaceForeign { get; init; }
}

/// <param name="Updated">False for a new install.</param>
/// <param name="Added">Files put in place that were not there.</param>
/// <param name="Replaced">Files that were there and differed: the old ones are in <paramref name="Backup"/>.</param>
/// <param name="Kept">Files that were already the pinned ones, and entries left alone because they are preserved.</param>
/// <param name="Backup">The folder holding what was replaced; null when nothing was.</param>
public sealed record RuntimeInstallResult(string Folder, string Version, bool Updated, int Added, int Replaced, int Kept, string? Backup);

/// <summary>What the installer asks of the target and of the machine.</summary>
public sealed record RuntimeHost
{
    /// <summary>The version of the runtime in a folder, by the target's own detection; null when it is none it knows.</summary>
    public required Func<string, string?> Identify { get; init; }

    /// <summary>Whether the game in that folder runs.</summary>
    public Func<string, bool> IsRunning { get; init; } = _ => false;

    /// <summary>Free bytes on the drive of a path (which need not exist yet); null when it cannot be told.</summary>
    public Func<string, long?> FreeSpace { get; init; } = RuntimeInstaller.DriveFreeSpace;

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Installs the pinned runtime (<see cref="RuntimeManifest"/>) or updates an install of it:
/// <list type="number">
/// <item>the folder is looked at first: a new install goes into a missing or empty folder; an
/// install of the same fork is updated; anything else is refused;</item>
/// <item>the asset is downloaded from the pinned release into <c>&lt;cache&gt;/&lt;asset&gt;.part</c>,
/// continuing a partial file with an HTTP Range request;</item>
/// <item>its size and SHA-256 must be the pinned ones before a single entry is read;</item>
/// <item>every entry name is checked (no absolute path, no <c>..</c>, nothing outside the
/// folder), then the files are unpacked into <c>&lt;folder&gt;/.mkwmp-update/new</c>;</item>
/// <item>they are moved into place: a file that is already the same is left, one that differs
/// goes to the backup first, and a name in <see cref="RuntimeManifest.Preserved"/> that exists is
/// never touched (the toolchain, the setup's state, the player's settings, saves and mods, and
/// everything built from their game). Any failure puts every file back.</item>
/// </list>
/// Nothing here downloads, makes or looks for the game: only the runtime's release asset.
/// </summary>
public sealed class RuntimeInstaller
{
    /// <summary>Written at the root of an install this launcher made: which build, and the files it put there.</summary>
    public const string Marker = "mkwmp-runtime.json";

    /// <summary>Inside the install while one is being changed: <c>new/</c> (unpacked) and <c>old/</c> (replaced).</summary>
    public const string WorkFolder = ".mkwmp-update";

    /// <summary>Inside the install: the files the last update replaced.</summary>
    public const string BackupFolder = ".mkwmp-backup";

    /// <summary>A download with no byte for this long has stalled (what is on disk stays, for the next try).</summary>
    internal static readonly TimeSpan Stall = TimeSpan.FromSeconds(60);

    private const int BufferSize = 128 * 1024;

    private readonly RuntimeManifest _manifest;
    private readonly HttpClient _http;
    private readonly string _userAgent;
    private readonly string _cache;
    private readonly RuntimeHost _host;

    /// <param name="cacheFolder">Where the download is kept until it is installed.</param>
    public RuntimeInstaller(RuntimeManifest manifest, HttpClient http, string userAgent, string cacheFolder, RuntimeHost host)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrEmpty(cacheFolder);
        ArgumentNullException.ThrowIfNull(host);
        _manifest = manifest;
        _http = http;
        _userAgent = userAgent;
        _cache = cacheFolder;
        _host = host;
    }

    public RuntimeManifest Manifest => _manifest;

    /// <summary>The verified download.</summary>
    public string ArchivePath => Path.Combine(_cache, _manifest.Asset);

    /// <summary>The download in progress: kept across launcher runs, so it continues.</summary>
    public string PartPath => ArchivePath + ".part";

    /// <summary>Bytes of the asset already on disk.</summary>
    public long Downloaded
    {
        get
        {
            try
            {
                return File.Exists(ArchivePath) ? _manifest.Size : File.Exists(PartPath) ? Math.Min(new FileInfo(PartPath).Length, _manifest.Size) : 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }
    }

    public static long? DriveFreeSpace(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path)) is { Length: > 0 } root ? new DriveInfo(root).AvailableFreeSpace : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // a network share: the copy itself will say when it is full
        }
    }

    /// <summary>The version an install says this launcher put there (its <see cref="Marker"/>); null when it has none.</summary>
    public static string? ManagedVersion(string folder)
    {
        try
        {
            var marker = Path.Combine(folder, Marker);
            if (!File.Exists(marker))
            {
                return null;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(marker));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return "";
        }
    }

    public RuntimeFolderKind Classify(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        try
        {
            if (!Directory.Exists(folder))
            {
                return File.Exists(folder) ? RuntimeFolderKind.Occupied : RuntimeFolderKind.Empty;
            }
            if (File.Exists(Path.Combine(folder, Marker)))
            {
                return RuntimeFolderKind.Managed;
            }
            if (File.Exists(Path.Combine(folder, _manifest.Executable)))
            {
                return _manifest.SameLineage(_host.Identify(folder)) ? RuntimeFolderKind.SameLineage : RuntimeFolderKind.Foreign;
            }
            // What an interrupted install of ours left is not somebody's files.
            var others = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).Any(n => !string.Equals(n, WorkFolder, StringComparison.OrdinalIgnoreCase));
            return others ? RuntimeFolderKind.Occupied : RuntimeFolderKind.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"{folder}: {e.Message}", e);
        }
    }

    /// <summary>
    /// Installs into <paramref name="folder"/> or updates the install there. Throws
    /// <see cref="LauncherException"/> with a message for the player (the folder is then as it
    /// was), or <see cref="OperationCanceledException"/>; a partial download is kept either way.
    /// </summary>
    public async Task<RuntimeInstallResult> InstallAsync(string folder, RuntimeInstallOptions? options, IProgress<RuntimeProgress>? progress, CancellationToken cancel)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        try
        {
            folder = Path.GetFullPath(folder);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"{folder}: {e.Message}", e);
        }
        var kind = Classify(folder);
        switch (kind)
        {
            case RuntimeFolderKind.Occupied:
                throw new LauncherException(Strings.RuntimeFolderOccupied);
            case RuntimeFolderKind.Foreign when options?.ReplaceForeign != true:
                throw new LauncherException(Strings.RuntimeFolderForeign(_manifest.Name, _host.Identify(folder)));
            default:
                break;
        }
        var fresh = kind == RuntimeFolderKind.Empty;
        if (kind == RuntimeFolderKind.Managed && ManagedVersion(folder) == _manifest.Version && _host.Identify(folder) == _manifest.Version)
        {
            // Already the pinned build, put there by this installer: nothing to download.
            return new RuntimeInstallResult(folder, _manifest.Version, true, 0, 0, 0, null);
        }
        RefuseWhileRunning(folder, fresh);
        try
        {
            Directory.CreateDirectory(_cache);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"{_cache}: {e.Message}", e);
        }
        // Before the download: its rest, and for a new install what it unpacks to and what the setup then builds.
        RequireSpace((_cache, _manifest.Size - Downloaded), (folder, fresh ? _manifest.InstalledSize + _manifest.SetupSpace : 0));
        var archive = await DownloadAsync(progress, cancel).ConfigureAwait(false);
        var result = await Task.Run(() => Unpack(archive, folder, fresh, progress, cancel), cancel).ConfigureAwait(false);
        // Installed: the download has done its work (a failed install keeps it, so the next try needs no network).
        Quietly(() => File.Delete(archive));
        return result;
    }

    private void RefuseWhileRunning(string folder, bool fresh)
    {
        if (!fresh && _host.IsRunning(folder))
        {
            throw new LauncherException(Strings.RuntimeCloseGame(_manifest.Name));
        }
    }

    /// <summary>Needs per drive, against what is free there.</summary>
    private void RequireSpace(params (string Path, long Bytes)[] needs)
    {
        foreach (var drive in needs.Where(n => n.Bytes > 0).GroupBy(n => Path.GetPathRoot(Path.GetFullPath(n.Path)) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var need = drive.Sum(n => n.Bytes);
            if (_host.FreeSpace(drive.First().Path) is { } free && free < need)
            {
                throw new LauncherException(Strings.RuntimeNoSpace(drive.Key, need, free));
            }
        }
    }

    // ---- download ---------------------------------------------------------------------------

    private async Task<string> DownloadAsync(IProgress<RuntimeProgress>? progress, CancellationToken cancel)
    {
        try
        {
            if (File.Exists(ArchivePath))
            {
                // Verified before, but it has been lying on disk since: checked again.
                if (await VerifyAsync(ArchivePath, progress, cancel).ConfigureAwait(false))
                {
                    return ArchivePath;
                }
                File.Delete(ArchivePath);
            }
            for (var attempt = 0; ; attempt++)
            {
                var have = File.Exists(PartPath) ? new FileInfo(PartPath).Length : 0;
                if (have > _manifest.Size)
                {
                    File.Delete(PartPath);
                    have = 0;
                }
                if (have == _manifest.Size || !await FetchAsync(have, progress, cancel).ConfigureAwait(false))
                {
                    break;
                }
                // The server would not continue this file: once more, from the start.
                File.Delete(PartPath);
                if (attempt > 0)
                {
                    throw new LauncherException(Strings.RuntimeDownloadFailed(_manifest.Asset, Strings.ServerRefused));
                }
            }
            if (new FileInfo(PartPath).Length < _manifest.Size)
            {
                throw new LauncherException(Strings.RuntimeDownloadFailed(_manifest.Asset, Strings.ClosedEarly));
            }
            if (!await VerifyAsync(PartPath, progress, cancel).ConfigureAwait(false))
            {
                // Not the pinned file: nothing of it can be continued or used.
                File.Delete(PartPath);
                throw new LauncherException(Strings.RuntimeBadDownload(_manifest.Asset));
            }
            File.Move(PartPath, ArchivePath, overwrite: true);
            return ArchivePath;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(Strings.RuntimeDownloadFailed(_manifest.Asset, e.Message), e);
        }
    }

    /// <summary>
    /// One request for the bytes from <paramref name="have"/> on, appended to the partial file.
    /// True when the server cannot continue it (the caller starts over).
    /// </summary>
    private async Task<bool> FetchAsync(long have, IProgress<RuntimeProgress>? progress, CancellationToken cancel)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Stall);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _manifest.Url);
            request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            if (have > 0)
            {
                request.Headers.Range = new RangeHeaderValue(have, null);
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
            {
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new LauncherException(Strings.RuntimeDownloadFailed(_manifest.Asset, $"HTTP {(int)response.StatusCode}"));
            }
            // 200 to a Range request: the whole file again, from its first byte.
            var append = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (append && response.Content.Headers.ContentRange is { From: { } from } && from != have)
            {
                return true;
            }
            var start = append ? have : 0;
            if (response.Content.Headers.ContentLength is { } length && start + length != _manifest.Size)
            {
                // Another file under the pinned name: not worth 200 MB to find out by its hash.
                throw new LauncherException(Strings.RuntimeBadDownload(_manifest.Asset));
            }
            using var body = await response.Content.ReadAsStreamAsync(limit.Token).ConfigureAwait(false);
            using var file = new FileStream(PartPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, useAsync: true);
            var meter = new Meter(_host.Clock, RuntimePhase.Downloading, start, _manifest.Size);
            progress?.Report(meter.At(start));
            var buffer = new byte[BufferSize];
            var got = start;
            int n;
            while ((n = await body.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
            {
                got += n;
                if (got > _manifest.Size)
                {
                    throw new LauncherException(Strings.RuntimeBadDownload(_manifest.Asset));
                }
                await file.WriteAsync(buffer.AsMemory(0, n), limit.Token).ConfigureAwait(false);
                limit.CancelAfter(Stall);
                progress?.Report(meter.At(got));
            }
            await file.FlushAsync(limit.Token).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new LauncherException(Strings.RuntimeDownloadFailed(_manifest.Asset, Strings.TimedOut));
        }
        catch (HttpRequestException e)
        {
            throw new LauncherException(Strings.RuntimeDownloadFailed(_manifest.Asset, e.InnerException?.Message ?? e.Message), e);
        }
    }

    /// <summary>The file is the pinned asset: its size, then its SHA-256.</summary>
    private async Task<bool> VerifyAsync(string path, IProgress<RuntimeProgress>? progress, CancellationToken cancel)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        if (file.Length != _manifest.Size)
        {
            return false;
        }
        var meter = new Meter(_host.Clock, RuntimePhase.Verifying, 0, _manifest.Size);
        progress?.Report(meter.At(0));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        long done = 0;
        int n;
        while ((n = await file.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer, 0, n);
            done += n;
            progress?.Report(meter.At(done));
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset()).Equals(_manifest.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    // ---- unpack and put in place ------------------------------------------------------------

    /// <summary>
    /// The archive's files by their path inside the install, every name checked. A single
    /// top-level folder holding everything (<c>mkw-recomp/</c>) is not part of the paths.
    /// </summary>
    internal static IReadOnlyList<(ZipArchiveEntry Entry, string Relative)> Entries(ZipArchive zip, string executable)
    {
        var files = zip.Entries.Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')).ToList();
        var names = files.Select(e => e.FullName.Replace('\\', '/')).ToList();
        foreach (var name in names)
        {
            CheckName(name, name);
        }
        var strip = 0;
        if (names.Count > 0 && !names.Contains(executable, StringComparer.OrdinalIgnoreCase))
        {
            var slash = names[0].IndexOf('/', StringComparison.Ordinal);
            if (slash > 0 && names.All(n => n.Length > slash && string.CompareOrdinal(n, 0, names[0], 0, slash + 1) == 0))
            {
                strip = slash + 1;
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var planned = new List<(ZipArchiveEntry, string)>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            var relative = names[i][strip..];
            if (!seen.Add(relative))
            {
                throw new LauncherException(Strings.RuntimeUnsafeArchive(names[i]));
            }
            planned.Add((files[i], relative));
        }
        if (!seen.Contains(executable))
        {
            throw new LauncherException(Strings.RuntimeArchiveLacks(executable));
        }
        return planned;
    }

    /// <summary>
    /// Refuses a name that could land outside the install (zip-slip): rooted, with a drive or a
    /// stream (<c>:</c>), with an empty, <c>.</c> or <c>..</c> part, or with a part Windows would
    /// rename (trailing dot or space, a character it does not take).
    /// </summary>
    internal static void CheckName(string relative, string shown)
    {
        var bad = relative.Length == 0 || relative[0] == '/' || relative.Contains(':', StringComparison.Ordinal);
        if (!bad)
        {
            var invalid = Path.GetInvalidFileNameChars();
            bad = relative.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part[^1] is '.' or ' ' || part.IndexOfAny(invalid) >= 0);
        }
        if (bad)
        {
            throw new LauncherException(Strings.RuntimeUnsafeArchive(shown));
        }
    }

    /// <summary>The path of an entry under <paramref name="root"/>; a second check that it is inside, by the file system's own rules.</summary>
    internal static string Inside(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : throw new LauncherException(Strings.RuntimeUnsafeArchive(relative));
    }

    private RuntimeInstallResult Unpack(string archive, string folder, bool fresh, IProgress<RuntimeProgress>? progress, CancellationToken cancel)
    {
        var work = Path.Combine(folder, WorkFolder);
        var (staging, replaced) = (Path.Combine(work, "new"), Path.Combine(work, "old"));
        var undo = new Stack<Action>();
        var (done, createdRoot) = (false, false);
        try
        {
            using var zip = ZipFile.OpenRead(archive);
            var all = Entries(zip, _manifest.Executable);
            // A preserved name that is already there is left as it is, whole: its entries are not even unpacked.
            var present = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            bool Skipped(string relative)
            {
                if (fresh || !_manifest.IsPreserved(relative))
                {
                    return false;
                }
                var top = RuntimeManifest.TopName(relative);
                if (!present.TryGetValue(top, out var there))
                {
                    var path = Path.Combine(folder, top);
                    present[top] = there = File.Exists(path) || Directory.Exists(path);
                }
                return there;
            }
            var wanted = all.Where(e => !Skipped(e.Relative)).ToList();
            var kept = present.Count(p => p.Value);
            var bytes = wanted.Sum(e => e.Entry.Length);
            RequireSpace((folder, bytes + (fresh ? _manifest.SetupSpace : 0)));

            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
            createdRoot = !Directory.Exists(folder);
            Directory.CreateDirectory(staging);
            Extract(wanted, staging, bytes, progress, cancel);
            if (!Sha256Of(Path.Combine(staging, _manifest.Executable)).Equals(_manifest.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new LauncherException(Strings.RuntimeNotRecognised(_manifest.Name, _manifest.Version));
            }
            var files = wanted.Select(e => e.Relative).ToList();
            var owned = all.Select(e => e.Relative).Where(r => !_manifest.IsPreserved(r)).Order(StringComparer.Ordinal).ToList();
            var before = OwnedFiles(folder);
            WriteMarker(Path.Combine(staging, Marker), owned);
            files.Add(Marker);

            RefuseWhileRunning(folder, fresh);
            progress?.Report(new RuntimeProgress(RuntimePhase.Installing, 0, files.Count, 0, null));
            var (added, swapped) = (0, 0);
            for (var i = 0; i < files.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                var (from, to) = (Inside(staging, files[i]), Inside(folder, files[i]));
                if (Directory.Exists(to))
                {
                    throw new LauncherException($"{to} is a folder");
                }
                if (File.Exists(to))
                {
                    if (files[i] != Marker && SameFile(from, to))
                    {
                        kept++;
                        continue;
                    }
                    MoveAside(to, Inside(replaced, files[i]), undo);
                    swapped += files[i] == Marker ? 0 : 1;
                }
                else
                {
                    added += files[i] == Marker ? 0 : 1;
                }
                CreateFolders(Path.GetDirectoryName(to)!, folder, undo);
                File.Move(from, to);
                undo.Push(() => File.Delete(to));
                progress?.Report(new RuntimeProgress(RuntimePhase.Installing, i + 1, files.Count, 0, null));
            }
            // What an earlier build of ours put here and this one no longer has.
            var now = new HashSet<string>(owned, StringComparer.OrdinalIgnoreCase);
            foreach (var gone in before.Where(f => !now.Contains(f) && !_manifest.IsPreserved(f)))
            {
                CheckName(gone, gone);
                var path = Inside(folder, gone);
                if (File.Exists(path))
                {
                    MoveAside(path, Inside(replaced, gone), undo);
                }
            }
            // The same look the launcher takes at any game folder.
            if (_host.Identify(folder) != _manifest.Version)
            {
                throw new LauncherException(Strings.RuntimeNotRecognised(_manifest.Name, _manifest.Version));
            }
            done = true;
            undo.Clear();
            string? backup = null;
            if (Directory.Exists(replaced))
            {
                var keep = Path.Combine(folder, BackupFolder);
                Quietly(() =>
                {
                    if (Directory.Exists(keep))
                    {
                        Directory.Delete(keep, recursive: true);
                    }
                    Directory.Move(replaced, keep);
                });
                backup = Directory.Exists(replaced) ? replaced : keep;
            }
            Quietly(() => Directory.Delete(backup == replaced ? staging : work, recursive: true));
            return new RuntimeInstallResult(folder, _manifest.Version, !fresh, added, swapped, kept, backup);
        }
        catch (InvalidDataException e)
        {
            // The pinned hash matched, so this is the disk, not the download.
            throw new LauncherException(Strings.RuntimeInstallFailed(_manifest.Name, e.Message), e);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(Strings.RuntimeInstallFailed(_manifest.Name, e.Message), e);
        }
        finally
        {
            if (!done)
            {
                // Newest first: every file goes back where it was.
                while (undo.TryPop(out var step))
                {
                    Quietly(step);
                }
                Quietly(() =>
                {
                    if (Directory.Exists(work))
                    {
                        Directory.Delete(work, recursive: true);
                    }
                });
                Quietly(() => DeleteIfEmpty(folder, onlyIf: createdRoot));
            }
        }
    }

    private void Extract(List<(ZipArchiveEntry Entry, string Relative)> wanted, string staging, long bytes, IProgress<RuntimeProgress>? progress, CancellationToken cancel)
    {
        var meter = new Meter(_host.Clock, RuntimePhase.Extracting, 0, bytes);
        progress?.Report(meter.At(0));
        var buffer = new byte[BufferSize];
        long done = 0;
        foreach (var (entry, relative) in wanted)
        {
            cancel.ThrowIfCancellationRequested();
            var path = Inside(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (var from = entry.Open())
            using (var to = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                int n;
                while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    to.Write(buffer, 0, n);
                    done += n;
                    progress?.Report(meter.At(done));
                }
            }
            Quietly(() => File.SetLastWriteTimeUtc(path, entry.LastWriteTime.UtcDateTime));
        }
    }

    private static void MoveAside(string file, string backup, Stack<Action> undo)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Move(file, backup, overwrite: true);
        undo.Push(() => File.Move(backup, file, overwrite: true));
    }

    /// <summary>Creates the folders down to <paramref name="directory"/>; the ones it made are removed again on undo.</summary>
    private static void CreateFolders(string directory, string root, Stack<Action> undo)
    {
        var missing = new Stack<string>();
        for (var d = directory; d.Length > root.Length && !Directory.Exists(d); d = Path.GetDirectoryName(d)!)
        {
            missing.Push(d);
        }
        while (missing.TryPop(out var d))
        {
            Directory.CreateDirectory(d);
            undo.Push(() => DeleteIfEmpty(d));
        }
    }

    private static void DeleteIfEmpty(string directory, bool onlyIf = true)
    {
        if (onlyIf && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
        }
    }

    /// <summary>For cleaning up: a file that will not go away must not hide the real error, or undo the rest.</summary>
    private static void Quietly(Action step)
    {
        try
        {
            step();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // left behind: the next install clears the work folder first
        }
    }

    private static string Sha256Of(string path)
    {
        using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(file));
    }

    private static bool SameFile(string a, string b) => new FileInfo(a).Length == new FileInfo(b).Length && Sha256Of(a) == Sha256Of(b);

    private void WriteMarker(string path, IReadOnlyList<string> files)
    {
        using var file = File.Create(path);
        using var json = new Utf8JsonWriter(file, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("name", _manifest.Name);
        json.WriteString("version", _manifest.Version);
        json.WriteString("repository", _manifest.Repository);
        json.WriteString("tag", _manifest.Tag);
        json.WriteString("asset", _manifest.Asset);
        json.WriteString("sha256", _manifest.Sha256);
        json.WriteString("installed", _host.Clock.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        json.WriteStartArray("files");
        foreach (var f in files)
        {
            json.WriteStringValue(f);
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>The files the install's marker says the launcher put there (not the preserved ones); empty without a readable marker.</summary>
    private static List<string> OwnedFiles(string folder)
    {
        var list = new List<string>();
        try
        {
            var marker = Path.Combine(folder, Marker);
            if (!File.Exists(marker))
            {
                return list;
            }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(marker));
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                list.AddRange(files.EnumerateArray().Where(f => f.ValueKind == JsonValueKind.String).Select(f => f.GetString()!.Replace('\\', '/')));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            list.Clear();
        }
        // A marker is a file anyone can edit: a name that points outside is not ours to move.
        list.RemoveAll(f =>
        {
            try
            {
                CheckName(f, f);
                return false;
            }
            catch (LauncherException)
            {
                return true;
            }
        });
        return list;
    }

    /// <summary>Speed over the last seconds and what is left at that speed.</summary>
    private sealed class Meter(TimeProvider clock, RuntimePhase phase, long start, long total)
    {
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
        private readonly Queue<(long At, long Bytes)> _samples = new([(clock.GetTimestamp(), start)]);

        public RuntimeProgress At(long done)
        {
            var now = clock.GetTimestamp();
            while (_samples.Count > 1 && clock.GetElapsedTime(_samples.Peek().At, now) > Window)
            {
                _samples.Dequeue();
            }
            var (at, bytes) = _samples.Peek();
            // At most a few samples a second are kept: enough for a speed, cheap for a fast disk.
            if (_samples.Count == 1 || clock.GetElapsedTime(_samples.Last().At, now) >= TimeSpan.FromMilliseconds(250))
            {
                _samples.Enqueue((now, done));
            }
            var seconds = clock.GetElapsedTime(at, now).TotalSeconds;
            var speed = seconds >= 0.5 && done > bytes ? (done - bytes) / seconds : 0;
            TimeSpan? remaining = done >= total ? TimeSpan.Zero : speed > 0 ? TimeSpan.FromSeconds((total - done) / speed) : null;
            return new RuntimeProgress(phase, done, total, speed, remaining);
        }
    }
}
