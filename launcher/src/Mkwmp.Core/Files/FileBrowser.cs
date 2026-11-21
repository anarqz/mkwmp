// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.IO.Enumeration;

namespace Mkwmp.Core.Files;

/// <summary>Where a quick place leads. The window words and draws each kind; <see cref="Place.Name"/> is the disk's or the card's own name.</summary>
public enum PlaceKind
{
    /// <summary>The folder this picker was last used in, for the same purpose.</summary>
    Last,
    Home,
    Downloads,
    Desktop,
    Documents,

    /// <summary>The launcher's own folder (portable: the game it installs is beside it).</summary>
    Launcher,

    /// <summary>An SD card or a USB drive (Linux: under <c>/run/media</c> or <c>/media</c>; Windows: a removable drive letter).</summary>
    Removable,

    /// <summary>A Steam library's <c>steamapps\common</c>.</summary>
    Steam,

    /// <summary>A folder of <c>/mnt</c>.</summary>
    Mount,
    Drive,
    Network,

    /// <summary>The Linux root (<c>/</c>, which Wine shows as <c>Z:\</c>).</summary>
    Root,

    /// <summary>A drive letter of the Wine prefix (<c>C:</c> is the prefix's own, nearly empty disk): only on request.</summary>
    Prefix,

    /// <summary>Every drive, card and mount: the level above a root (its path is "").</summary>
    Computer,
}

/// <param name="Path">The folder as this process opens it (a Windows path, <c>Z:\…</c> under Wine); "" for <see cref="PlaceKind.Computer"/>.</param>
/// <param name="Name">The disk's label or the card's name; "" when the kind says it all.</param>
/// <param name="Free">Free bytes on it, when asked and known.</param>
public sealed record Place(PlaceKind Kind, string Path, string Name = "", long? Free = null);

public enum FileType { Folder, Drive, Package, Program, File }

/// <summary>A line of a listing.</summary>
/// <param name="Match">A file the filter takes, or a folder the filter marks (it holds what is looked for).</param>
/// <param name="Link">A junction or a symbolic link.</param>
/// <param name="Place">For a line of the <see cref="PlaceKind.Computer"/> listing: the drive, card or mount it is.</param>
public sealed record FileEntry(
    string Name, string Path, bool IsFolder, FileType Type, bool Match, long? Size = null, DateTime? Modified = null, bool Link = false, Place? Place = null);

public enum ListProblem
{
    None,

    /// <summary>The folder (or its drive, or its card) is not there.</summary>
    NotFound,

    /// <summary>The system refuses to list it.</summary>
    Denied,

    /// <summary>It is there and cannot be read (<see cref="FolderListing.Reason"/> is the system's word).</summary>
    Unreadable,

    /// <summary>A path longer than any real one: a loop of links.</summary>
    TooDeep,
}

/// <param name="Truncated">The folder holds more than is listed (<see cref="FileBrowser.MaxScan"/>, <see cref="FileBrowser.MaxShown"/>).</param>
/// <param name="Suppressed">Entries that are there and not listed: hidden ones, and files the filter does not take ("show all files" lists them).</param>
/// <param name="SelfMatch">The folder itself is one the filter marks.</param>
public sealed record FolderListing(
    string Path, IReadOnlyList<FileEntry> Entries, ListProblem Problem = ListProblem.None, string? Reason = null, bool Truncated = false, int Suppressed = 0,
    bool SelfMatch = false);

/// <summary>What the picker lists and marks for one purpose.</summary>
public sealed record PickFilter
{
    /// <summary>Files are listed (false: a folder is asked for, and files show only with "show all files", never to be picked).</summary>
    public bool Files { get; init; }

    /// <summary>The extensions of the files that match (<c>.nup</c>), letter case ignored; empty: every file matches.</summary>
    public IReadOnlyList<string> Extensions { get; init; } = [];

    /// <summary>
    /// Whether a folder holds what is looked for (the game's <c>exefs\main</c> and <c>romfs</c>; an <c>mkw.exe</c>): it is
    /// marked in the list. Called off the window's thread, for a few hundred folders at most: one or two file checks.
    /// </summary>
    public Func<string, bool>? FolderMatches { get; init; }

    /// <summary>Every file.</summary>
    public static PickFilter Any { get; } = new() { Files = true };

    /// <summary>Folders only.</summary>
    public static PickFilter Folders { get; } = new();

    public bool Matches(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return Files && (Extensions.Count == 0 || Extensions.Any(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>A step of the path shown above the list. <paramref name="Kind"/>: the place it stands for (Home, a card, "/"), else null and <paramref name="Name"/> is the folder's.</summary>
public sealed record Crumb(string Path, string Name, PlaceKind? Kind = null);

/// <summary>
/// The logic of the launcher's own file and folder picker (the window only draws it): the quick
/// places of this machine, a folder's listing, the path a player typed, the steps of a path.
/// Ported from Infinity Launcher's in-launcher browser (owner rule there, 2026-10-28: never the
/// system's dialog) with what it learned:
/// <list type="bullet">
/// <item>Under Wine / Proton the player's files are the Linux ones behind <c>Z:\</c>: the home
/// (<c>WINEHOMEDIR</c>, <c>HOME</c>, <c>USER</c>), the cards of <c>/run/media/&lt;user&gt;/&lt;card&gt;</c>
/// (the user's folder itself is not a card; an older SteamOS mounts <c>/run/media/&lt;card&gt;</c>),
/// Steam's libraries, <c>/mnt</c> and <c>/media</c>. <c>Z:</c> and the prefix's <c>C:</c> are not
/// offered as drives, and paths are shown the Linux way.</item>
/// <item>A listing is bounded (<see cref="MaxScan"/>, <see cref="MaxShown"/>) and made off the
/// window's thread; only fixed and removable drives are asked whether they are ready.</item>
/// <item>A folder that cannot be listed says why and stays shown (permission, gone, unreadable):
/// <c>IgnoreInaccessible</c> would turn a refused folder into an empty one.</item>
/// <item>Links are listed and followed (under Proton <c>Z:\home</c> itself is one), and a link to
/// one of its own ancestors opens that ancestor, so a loop never grows the path (<see cref="Enter"/>).</item>
/// <item>Names keep the letter case the disk has (Linux has <c>Roms</c> beside <c>roms</c>); only
/// extensions are compared without it, and a typed path takes the disk's own case.</item>
/// </list>
/// Nothing here throws for a bad path or a disk that went away.
/// </summary>
public sealed class FileBrowser
{
    private static readonly char[] Separators = ['\\', '/'];

    public FileBrowser(FileHost host) => Host = host ?? throw new ArgumentNullException(nameof(host));

    public FileHost Host { get; }

    /// <summary>Entries read from one folder before the listing stops (a folder of a million files answers in a blink).</summary>
    public int MaxScan { get; init; } = 20_000;

    /// <summary>Lines of one listing (folders first, so they are never the ones cut).</summary>
    public int MaxShown { get; init; } = 2_000;

    /// <summary>Folders of one listing that are asked whether they hold what is looked for.</summary>
    public int MaxMarked { get; init; } = 400;

    /// <summary>Steps of a path the picker still opens.</summary>
    public const int MaxDepth = 96;

    // ---- paths ------------------------------------------------------------------------------

    /// <summary>A Linux path (<c>/home/deck</c>) as this process opens it (<c>Z:\home\deck</c>); null on Windows.</summary>
    public string? FromLinux(string linuxPath)
    {
        ArgumentNullException.ThrowIfNull(linuxPath);
        if (Host.LinuxRoot is not { } root)
        {
            return null;
        }
        var rest = linuxPath.Trim().TrimStart('/').Replace('/', '\\').TrimEnd('\\');
        return rest.Length == 0 ? root : root + rest;
    }

    /// <summary>The path the player reads: the Linux one under Wine (<c>/home/deck/Downloads</c>, never <c>Z:\</c>), else the path itself.</summary>
    public string Display(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "";
        }
        if (Host.LinuxRoot is { } root && IsUnder(path, root))
        {
            return "/" + path[Math.Min(path.Length, root.Length)..].Replace('\\', '/').Trim('/');
        }
        return path;
    }

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it (letter case ignored: this places a path, it never names a file).</summary>
    public static bool IsUnder(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        var parent = folder.TrimEnd(Separators);
        var child = path.TrimEnd(Separators);
        return parent.Length > 0 && child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            && (child.Length == parent.Length || child[parent.Length] is '\\' or '/');
    }

    /// <summary>The same folder, however its end is written.</summary>
    public static bool Same(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return string.Equals(a.TrimEnd(Separators), b.TrimEnd(Separators), StringComparison.OrdinalIgnoreCase);
    }

    private bool IsRoot(string path) =>
        (Host.LinuxRoot is { } root && Same(path, root)) || (Path.GetPathRoot(path) is { Length: > 0 } own && Same(path, own));

    /// <summary>No separator at the end, a root aside (<c>C:\</c>: without it the path would mean "where C: stands now").</summary>
    private string Trim(string path) =>
        IsRoot(path) && !path.StartsWith(@"\\", StringComparison.Ordinal) ? path.TrimEnd(Separators) + Path.DirectorySeparatorChar : path.TrimEnd(Separators);

    /// <summary>The folder above; "" above a root (every drive and card: <see cref="PlaceKind.Computer"/>); null above that.</summary>
    public string? Parent(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        if (IsRoot(path))
        {
            return "";
        }
        try
        {
            return Path.GetDirectoryName(path.TrimEnd(Separators)) is { Length: > 0 } parent ? Trim(parent) : "";
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException)
        {
            return "";
        }
    }

    /// <summary>
    /// What a player typed or pasted, as a full path: quotes and <c>\\?\</c> taken off, a
    /// <c>file://</c> address read, <c>%NAME%</c> filled in, a bare <c>D:</c> made its root; under
    /// Wine a Linux path (<c>/run/media/deck/SD</c>, <c>~/Downloads</c>) becomes the <c>Z:\</c>
    /// one. A name alone is looked for in <paramref name="current"/>. The result has the letter
    /// case the disk has. Null for nothing or for what is not a path; the path need not exist.
    /// </summary>
    public string? Resolve(string? typed, string current = "")
    {
        var text = (typed ?? "").Trim().Trim('"', '\'').Trim();
        if (text.Length == 0)
        {
            return null;
        }
        try
        {
            if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(text, UriKind.Absolute, out var address))
            {
                text = Host.LinuxRoot is null ? address.LocalPath : Uri.UnescapeDataString(address.AbsolutePath);
            }
            if (text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                text = @"\\" + text[8..];
            }
            else if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\??\", StringComparison.Ordinal))
            {
                text = text[4..];
            }
            if (Host.LinuxRoot is not null)
            {
                if ((text == "~" || text.StartsWith("~/", StringComparison.Ordinal)) && Host.LinuxHome is { } home)
                {
                    text = home + text[1..];
                }
                if (text.StartsWith('/') && !text.StartsWith("//", StringComparison.Ordinal))
                {
                    text = FromLinux(text)!;
                }
            }
            else
            {
                text = Environment.ExpandEnvironmentVariables(text);
            }
            if (text.Length == 2 && text[1] == ':' && char.IsAsciiLetter(text[0]))
            {
                text += Path.DirectorySeparatorChar;
            }
            if (!Path.IsPathFullyQualified(text))
            {
                if (current.Length == 0)
                {
                    return null; // a name alone, with no folder to look in
                }
                text = Path.Combine(current, text);
            }
            var full = Path.GetFullPath(text);
            if (full.Length > 1 && full[1] == ':')
            {
                full = char.ToUpperInvariant(full[0]) + full[1..];
            }
            return RealCase(Trim(full));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException or UriFormatException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The path with each step spelled as the disk has it. Wine finds <c>z:\HOME\Deck</c> whatever
    /// its case, and the picker would then show and remember names that are not the folders' own.
    /// </summary>
    private string RealCase(string path)
    {
        try
        {
            var start = Host.LinuxRoot is { } linux && IsUnder(path, linux) ? linux : Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(start) || start.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return path; // a share: not asked here (a server that is gone answers after seconds), and its name is not a folder to list
            }
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                return path;
            }
            var at = Trim(start);
            foreach (var step in path[Math.Min(path.Length, start.Length)..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var real = step;
                if (step.IndexOfAny(['*', '?']) < 0)
                {
                    var found = Directory.EnumerateFileSystemEntries(at, step).Select(Path.GetFileName).Where(n => n is not null).Take(8).ToList();
                    real = found.FirstOrDefault(n => string.Equals(n, step, StringComparison.Ordinal)) ?? found.FirstOrDefault() ?? step;
                }
                at = Path.Combine(at, real);
            }
            return Trim(at);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return path;
        }
    }

    /// <summary>The path itself when it is a folder, else the nearest folder above that is there; "" when none is (a drive that went away).</summary>
    public string NearestExisting(string? path)
    {
        var at = path ?? "";
        for (var step = 0; at.Length > 0 && step < MaxDepth * 2; step++)
        {
            if (Exists(at))
            {
                return Trim(at);
            }
            at = Parent(at) ?? "";
        }
        return "";
    }

    /// <summary>
    /// The folder to show for one the player opened: itself, or, for a link that points at a folder
    /// it is inside of, that folder. A loop of links then goes round in place instead of making
    /// the path longer at every step. Any other link is opened where it is (its target may be
    /// outside what this process sees, and under Proton <c>Z:\home</c> is a link itself).
    /// </summary>
    public string Enter(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.ResolveLinkTarget(returnFinalTarget: true)?.FullName is not { Length: > 0 } target)
            {
                return folder;
            }
            return Path.GetDirectoryName(folder.TrimEnd(Separators)) is { } parent && IsUnder(parent, target) ? Trim(target) : folder;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return folder;
        }
    }

    /// <summary>
    /// The steps of a path, from a place the player knows (Home, a card, a mount, <c>/</c>; else the
    /// drive or the share) down to the folder. <paramref name="places"/>: what <see cref="Places"/> gave.
    /// </summary>
    public IReadOnlyList<Crumb> Crumbs(string path, IReadOnlyList<Place>? places = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            return [new Crumb("", "", PlaceKind.Computer)];
        }
        var steps = new List<Crumb>();
        var anchor = (places ?? Places())
            .Where(p => p.Path.Length > 0 && (p.Kind == PlaceKind.Home || (Host.LinuxRoot is not null && p.Kind is PlaceKind.Removable or PlaceKind.Mount or PlaceKind.Root))
                && IsUnder(path, p.Path))
            .MaxBy(p => p.Path.TrimEnd(Separators).Length);
        string from;
        if (anchor is not null)
        {
            steps.Add(new Crumb(anchor.Path, anchor.Name, anchor.Kind));
            from = anchor.Path;
        }
        else
        {
            from = Path.GetPathRoot(path) is { Length: > 0 } root ? root : path;
            steps.Add(new Crumb(Trim(from), from.TrimEnd(Separators)));
        }
        var at = Trim(from);
        foreach (var step in path[Math.Min(path.Length, from.TrimEnd(Separators).Length)..].Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            at = Path.Combine(at, step);
            steps.Add(new Crumb(at, step));
        }
        return steps;
    }

    // ---- places -----------------------------------------------------------------------------

    /// <summary>
    /// The quick places, in the order shown: the last folder of this purpose, the player's own
    /// folders, cards and USB drives, Steam's libraries, the launcher's folder, the drives, and
    /// last "this device". Only what is there now (a card taken out is gone from the next call).
    /// <paramref name="showAll"/>: under Wine, also the prefix's drive letters.
    /// </summary>
    public IReadOnlyList<Place> Places(bool showAll = false, string? last = null)
    {
        var list = new List<Place>();
        void Add(PlaceKind kind, string? path, string name = "", bool free = false, bool check = true)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            var full = Trim(path);
            if (list.Any(p => Same(p.Path, full)) || (check && !Exists(full)))
            {
                return;
            }
            list.Add(new Place(kind, full, name, free ? Host.FreeSpace(full) : null));
        }

        if (Host.LinuxRoot is { } root)
        {
            if (Host.LinuxHome is { } linuxHome && FromLinux(linuxHome) is { } home)
            {
                Add(PlaceKind.Home, home);
                Add(PlaceKind.Downloads, Path.Combine(home, "Downloads"));
                Add(PlaceKind.Desktop, Path.Combine(home, "Desktop"));
                Add(PlaceKind.Documents, Path.Combine(home, "Documents"));
            }
            foreach (var card in Media(Path.Combine(root, "run", "media")).Concat(Media(Path.Combine(root, "media"))))
            {
                Add(PlaceKind.Removable, card, Path.GetFileName(card), free: true, check: false);
            }
            foreach (var library in LinuxSteamLibraries())
            {
                Add(PlaceKind.Steam, library);
            }
            Add(PlaceKind.Launcher, Host.LauncherFolder);
            foreach (var mount in Folders(Path.Combine(root, "mnt")))
            {
                Add(PlaceKind.Mount, mount, Path.GetFileName(mount), free: true, check: false);
            }
            Add(PlaceKind.Root, root, free: true);
            if (showAll)
            {
                foreach (var drive in SafeDrives().Where(d => !Same(d.Root, root)))
                {
                    Add(PlaceKind.Prefix, drive.Root, drive.Label, check: drive.Kind != DriveKind.Network);
                }
            }
        }
        else
        {
            Add(PlaceKind.Home, Host.Home);
            Add(PlaceKind.Desktop, Host.Desktop);
            Add(PlaceKind.Downloads, Host.Downloads);
            Add(PlaceKind.Documents, Host.Documents);
            foreach (var library in SafeSteam())
            {
                // Steam's registry spells its own folder in lower case: shown as the disk has it
                var common = Path.Combine(library, "steamapps", "common");
                Add(PlaceKind.Steam, RealCase(Exists(common) ? common : library));
            }
            Add(PlaceKind.Launcher, Host.LauncherFolder);
            foreach (var drive in SafeDrives())
            {
                if (list.Any(p => Same(p.Path, drive.Root)))
                {
                    continue;
                }
                // already asked whether it is ready (a network drive never is asked): no second look
                list.Add(new Place(
                    drive.Kind switch { DriveKind.Removable => PlaceKind.Removable, DriveKind.Network => PlaceKind.Network, _ => PlaceKind.Drive },
                    Trim(drive.Root), drive.Label, drive.Free));
            }
        }
        if (!string.IsNullOrWhiteSpace(last) && NearestExisting(last) is { Length: > 0 } recent && !list.Any(p => Same(p.Path, recent)))
        {
            list.Insert(0, new Place(PlaceKind.Last, recent, Path.GetFileName(recent.TrimEnd(Separators))));
        }
        list.Add(new Place(PlaceKind.Computer, ""));
        return list;
    }

    /// <summary>
    /// The cards and drives of a Linux media folder: <c>&lt;media&gt;/&lt;user&gt;/&lt;card&gt;</c> (SteamOS 3.5+, desktops) and
    /// <c>&lt;media&gt;/&lt;card&gt;</c> (older SteamOS: <c>/run/media/mmcblk0p1</c>). A user's folder holds cards and is not one.
    /// </summary>
    private IEnumerable<string> Media(string folder)
    {
        foreach (var entry in Folders(folder))
        {
            if (IsLinuxUser(Path.GetFileName(entry)))
            {
                foreach (var card in Folders(entry))
                {
                    yield return card;
                }
            }
            else
            {
                yield return entry;
            }
        }
    }

    private bool IsLinuxUser(string name) =>
        string.Equals(name, Host.LinuxUser, StringComparison.Ordinal)
        || (Host.LinuxRoot is { } root && name is not ("root" or "") && Exists(Path.Combine(root, "home", name)));

    /// <summary>Steam's libraries on Linux, from its own <c>libraryfolders.vdf</c> (native, the <c>~/.steam</c> links, Flatpak): each one's <c>steamapps/common</c>.</summary>
    private IEnumerable<string> LinuxSteamLibraries()
    {
        if (Host.LinuxHome is not { } linuxHome || FromLinux(linuxHome) is not { } home)
        {
            yield break;
        }
        string[] installs =
        [
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".steam", "root"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"),
        ];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var install in installs)
        {
            var listed = new List<string>();
            try
            {
                var vdf = Path.Combine(install, "steamapps", "libraryfolders.vdf");
                if (File.Exists(vdf) && new FileInfo(vdf).Length < 1_000_000)
                {
                    listed.AddRange(Targets.MkwRecomp.MkwRecompTarget.LibraryPaths(File.ReadAllText(vdf)).Take(32));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // unreadable: no libraries from this install
            }
            foreach (var library in listed.Where(l => l.StartsWith('/')))
            {
                if (seen.Add(library.TrimEnd('/')) && FromLinux(library + "/steamapps/common") is { } common && Exists(common))
                {
                    yield return common;
                }
            }
            if (listed.Count == 0 && Exists(Path.Combine(install, "steamapps", "common")) && seen.Count == 0)
            {
                seen.Add(install);
                yield return Path.Combine(install, "steamapps", "common");
            }
        }
    }

    private IReadOnlyList<DriveEntry> SafeDrives()
    {
        try
        {
            return Host.Drives();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private IReadOnlyList<string> SafeSteam()
    {
        try
        {
            return Host.SteamLibraries();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private static bool Exists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>The folders of a folder (at most 64: this is for media and mount points), none when it cannot be read.</summary>
    private static List<string> Folders(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? [.. Directory.EnumerateDirectories(folder).Take(64).Order(StringComparer.Ordinal)] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    // ---- a folder's listing -----------------------------------------------------------------

    private readonly record struct Raw(string Name, FileAttributes Attributes, bool IsFolder, long Size, DateTimeOffset Modified);

    /// <summary>
    /// A folder's lines: its folders, then the files the filter takes (<paramref name="showAll"/>:
    /// every file, and hidden entries), each group by name the way people count ("track2" before
    /// "track10"). "" lists the drives, cards and mounts. Never throws but for a cancel: a folder
    /// that cannot be listed comes back empty with its <see cref="FolderListing.Problem"/>.
    /// </summary>
    public FolderListing List(string path, PickFilter filter, bool showAll = false, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(filter);
        if (path.Length == 0)
        {
            return new FolderListing("", [.. Places(showAll)
                .Where(p => p.Kind is PlaceKind.Drive or PlaceKind.Removable or PlaceKind.Network or PlaceKind.Mount or PlaceKind.Root or PlaceKind.Prefix)
                .Select(p => new FileEntry(p.Name, p.Path, true, FileType.Drive, false, Place: p))]);
        }
        if (path.Length > 4000 || path.Split(Separators, StringSplitOptions.RemoveEmptyEntries).Length > MaxDepth)
        {
            return new FolderListing(path, [], ListProblem.TooDeep);
        }
        var raws = new List<Raw>();
        var truncated = false;
        try
        {
            // IgnoreInaccessible would make a folder the system refuses look empty: the refusal is the answer here.
            var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false, ReturnSpecialDirectories = false };
            var entries = new FileSystemEnumerable<Raw>(
                Trim(path),
                (ref FileSystemEntry e) => new Raw(e.FileName.ToString(), e.Attributes, e.IsDirectory, e.IsDirectory ? 0 : e.Length, e.LastWriteTimeUtc),
                options);
            foreach (var raw in entries)
            {
                cancel.ThrowIfCancellationRequested();
                if (raws.Count >= MaxScan)
                {
                    truncated = true;
                    break;
                }
                raws.Add(raw);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new FolderListing(path, [], ListProblem.Denied);
        }
        catch (Exception e) when (e is DirectoryNotFoundException or DriveNotFoundException or FileNotFoundException)
        {
            return new FolderListing(path, [], ListProblem.NotFound);
        }
        catch (Exception e) when (e is IOException or ArgumentException or NotSupportedException)
        {
            return Exists(path) ? new FolderListing(path, [], ListProblem.Unreadable, e.Message) : new FolderListing(path, [], ListProblem.NotFound);
        }

        var folders = new List<Raw>();
        var files = new List<(Raw Raw, bool Match)>();
        var suppressed = 0;
        foreach (var raw in raws)
        {
            // Wine shows Linux's dot files as plain ones unless told otherwise
            var hidden = (raw.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 || (Host.Wine && raw.Name.StartsWith('.'));
            var match = !raw.IsFolder && filter.Matches(raw.Name);
            if ((hidden || (!raw.IsFolder && !match)) && !showAll)
            {
                suppressed++;
            }
            else if (raw.IsFolder)
            {
                folders.Add(raw);
            }
            else
            {
                files.Add((raw, match));
            }
        }
        folders.Sort((a, b) => NaturalComparer.Instance.Compare(a.Name, b.Name));
        files.Sort((a, b) => NaturalComparer.Instance.Compare(a.Raw.Name, b.Raw.Name));
        if (folders.Count + files.Count > MaxShown)
        {
            truncated = true;
            folders = folders.Count > MaxShown ? folders[..MaxShown] : folders;
            files = files[..Math.Min(files.Count, MaxShown - folders.Count)];
        }

        var lines = new List<FileEntry>(folders.Count + files.Count);
        for (var i = 0; i < folders.Count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            var full = Path.Combine(Trim(path), folders[i].Name);
            lines.Add(new FileEntry(
                folders[i].Name, full, true, FileType.Folder, i < MaxMarked && Marks(filter, full), null, folders[i].Modified.LocalDateTime,
                folders[i].Attributes.HasFlag(FileAttributes.ReparsePoint)));
        }
        foreach (var (raw, match) in files)
        {
            var type = filter.Extensions.Count > 0 && match ? FileType.Package
                : raw.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? FileType.Program
                : FileType.File;
            lines.Add(new FileEntry(
                raw.Name, Path.Combine(Trim(path), raw.Name), false, type, match, raw.Size, raw.Modified.LocalDateTime,
                raw.Attributes.HasFlag(FileAttributes.ReparsePoint)));
        }
        return new FolderListing(Trim(path), lines, Truncated: truncated, Suppressed: suppressed, SelfMatch: Marks(filter, Trim(path)));
    }

    private static bool Marks(PickFilter filter, string folder)
    {
        try
        {
            return filter.FolderMatches?.Invoke(folder) ?? false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}

/// <summary>Names in the order people expect: digits by their number ("2" before "10"), letters without their case; equal ones by their exact text, so the order is stable.</summary>
internal sealed class NaturalComparer : IComparer<string>
{
    public static NaturalComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : -1) : 1;
        }
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        int a = 0, b = 0;
        while (a < x.Length && b < y.Length)
        {
            var startA = a;
            var startB = b;
            var digits = char.IsAsciiDigit(x[a]);
            if (digits != char.IsAsciiDigit(y[b]))
            {
                break; // a number against a letter: the plain order decides
            }
            while (a < x.Length && char.IsAsciiDigit(x[a]) == digits)
            {
                a++;
            }
            while (b < y.Length && char.IsAsciiDigit(y[b]) == digits)
            {
                b++;
            }
            int order;
            if (digits)
            {
                var left = x.AsSpan(startA, a - startA).TrimStart('0');
                var right = y.AsSpan(startB, b - startB).TrimStart('0');
                order = left.Length != right.Length ? left.Length.CompareTo(right.Length) : left.SequenceCompareTo(right);
            }
            else
            {
                order = compare.Compare(x, startA, a - startA, y, startB, b - startB, CompareOptions.IgnoreCase);
            }
            if (order != 0)
            {
                return order;
            }
        }
        if (a < x.Length || b < y.Length)
        {
            var rest = compare.Compare(x, a, x.Length - a, y, b, y.Length - b, CompareOptions.IgnoreCase);
            if (rest != 0)
            {
                return rest;
            }
        }
        return string.CompareOrdinal(x, y);
    }
}
