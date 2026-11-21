// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Platform;

namespace Mkwmp.Core.Files;

public enum DriveKind { Fixed, Removable, Network, Other }

/// <summary>A drive letter of this machine. <paramref name="Label"/> and <paramref name="Free"/> are not asked of a network drive (it may take seconds to say it is gone).</summary>
public sealed record DriveEntry(string Root, DriveKind Kind, string Label = "", long? Free = null);

/// <summary>
/// The machine as the launcher's own file picker sees it (<see cref="FileBrowser"/>): Windows, or
/// Wine / Proton, where <c>Z:\</c> is the Linux root and the player's files are under
/// <c>/home/&lt;user&gt;</c> and <c>/run/media</c>, not in the prefix's empty <c>C:\users\steamuser</c>.
/// Everything is a value or a function, so the tests (and the screenshot harness) stand a temp
/// folder in for <c>Z:\</c>.
/// </summary>
public sealed record FileHost
{
    /// <summary>Wine / Proton: Linux places are offered, dot files are hidden, the prefix's drives are shown only on request.</summary>
    public bool Wine { get; init; }

    /// <summary>The Linux root as this process reaches it (<c>Z:\</c>); null on Windows.</summary>
    public string? LinuxRoot { get; init; }

    /// <summary>The player's Linux home as a Linux path (<c>/home/deck</c>); null when not known.</summary>
    public string? LinuxHome { get; init; }

    /// <summary>The Linux user (<c>deck</c>): <c>/run/media/&lt;user&gt;</c> holds the cards, it is not one.</summary>
    public string? LinuxUser { get; init; }

    public string Home { get; init; } = "";
    public string Desktop { get; init; } = "";
    public string Documents { get; init; } = "";
    public string Downloads { get; init; } = "";

    /// <summary>Where the launcher is (portable: the game it installs and its settings are beside it).</summary>
    public string LauncherFolder { get; init; } = "";

    public Func<IReadOnlyList<DriveEntry>> Drives { get; init; } = () => [];

    /// <summary>Steam's library folders on Windows (read from Steam's files; Steam is never started or asked).</summary>
    public Func<IReadOnlyList<string>> SteamLibraries { get; init; } = () => [];

    /// <summary>Free bytes of the volume a folder is on (under Wine every card is a folder of <c>Z:</c>, so the drive letter's own answer is the wrong disk); null when not known.</summary>
    public Func<string, long?> FreeSpace { get; init; } = _ => null;

    /// <summary>This machine.</summary>
    public static FileHost Current()
    {
        var wine = WineHost.Detected;
        var root = wine ? WineHost.LinuxRoot ?? (SafeExists(@"Z:\") ? @"Z:\" : null) : null;
        var home = root is null ? null : LinuxHomeFromEnvironment(root);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new FileHost
        {
            Wine = wine,
            LinuxRoot = root,
            LinuxHome = home,
            LinuxUser = UserOf(home) ?? Environment.GetEnvironmentVariable("USER"),
            Home = profile,
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Downloads = KnownDownloads() ?? Path.Combine(profile, "Downloads"),
            LauncherFolder = LauncherOptions.LauncherFolder,
            Drives = SystemDrives,
            FreeSpace = FolderFreeSpace,
            SteamLibraries = () => wine ? [] : [.. Targets.MkwRecomp.MkwRecompTarget.SteamLibraries()],
        };
    }

    /// <summary>
    /// A Linux machine seen through Wine, with <paramref name="root"/> standing in for <c>Z:\</c>
    /// (tests, the screenshot harness): <c>root\home\&lt;user&gt;</c> is the home.
    /// </summary>
    public static FileHost FakeWine(string root, string user = "deck", IReadOnlyList<DriveEntry>? drives = null) => new()
    {
        Wine = true,
        LinuxRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        LinuxHome = "/home/" + user,
        LinuxUser = user,
        Home = @"C:\users\steamuser",
        Desktop = @"C:\users\steamuser\Desktop",
        Documents = @"C:\users\steamuser\Documents",
        Downloads = @"C:\users\steamuser\Downloads",
        LauncherFolder = "",
        Drives = () => drives ?? [new DriveEntry(@"C:\", DriveKind.Fixed)],
    };

    /// <summary>
    /// The Linux home under Wine: <c>WINEHOMEDIR</c> (<c>\??\Z:\home\deck</c>, Wine's own word), else
    /// <c>HOME</c> when it is a Linux path, else <c>/home/$USER</c>, else the only folder of
    /// <c>/home</c> (Proton's container passes few variables through).
    /// </summary>
    private static string? LinuxHomeFromEnvironment(string root)
    {
        var wine = Environment.GetEnvironmentVariable("WINEHOMEDIR");
        if (wine is { Length: > 0 })
        {
            var path = wine.StartsWith(@"\??\", StringComparison.Ordinal) ? wine[4..] : wine;
            if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return "/" + path[root.Length..].Replace('\\', '/').Trim('/');
            }
            if (path.StartsWith('/'))
            {
                return path.TrimEnd('/');
            }
        }
        if (Environment.GetEnvironmentVariable("HOME") is { Length: > 1 } home && home[0] == '/')
        {
            return home.TrimEnd('/');
        }
        if (Environment.GetEnvironmentVariable("USER") is { Length: > 0 } user && SafeExists(Path.Combine(root, "home", user)))
        {
            return "/home/" + user;
        }
        try
        {
            var homes = Directory.GetDirectories(Path.Combine(root, "home"));
            return homes.Length == 1 ? "/home/" + Path.GetFileName(homes[0]) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? UserOf(string? linuxHome) =>
        linuxHome is not null && linuxHome.StartsWith("/home/", StringComparison.Ordinal) && linuxHome.Length > 6 && !linuxHome[6..].Contains('/', StringComparison.Ordinal)
            ? linuxHome[6..]
            : null;

    private static bool SafeExists(string folder)
    {
        try
        {
            return Directory.Exists(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every drive letter. Only fixed and removable drives are asked whether they are ready, for
    /// their label and free space: a network drive that went away answers after many seconds.
    /// </summary>
    private static List<DriveEntry> SystemDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        var list = new List<DriveEntry>();
        foreach (var drive in drives)
        {
            try
            {
                switch (drive.DriveType)
                {
                    case DriveType.Fixed or DriveType.Removable when drive.IsReady:
                        list.Add(new DriveEntry(drive.Name, drive.DriveType == DriveType.Fixed ? DriveKind.Fixed : DriveKind.Removable, drive.VolumeLabel ?? "", drive.AvailableFreeSpace));
                        break;
                    case DriveType.Network:
                        list.Add(new DriveEntry(drive.Name, DriveKind.Network));
                        break;
                    case DriveType.Ram or DriveType.Unknown when drive.IsReady:
                        list.Add(new DriveEntry(drive.Name, DriveKind.Other, "", null));
                        break;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // gone between the list and the question: not offered
            }
        }
        return list;
    }

    /// <summary>The Downloads folder where the player moved it (Windows keeps it in the shell's known folders, not in <see cref="Environment.SpecialFolder"/>).</summary>
    private static string? KnownDownloads()
    {
        try
        {
            var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var text) == 0 && text != IntPtr.Zero)
            {
                try
                {
                    return System.Runtime.InteropServices.Marshal.PtrToStringUni(text) is { Length: > 0 } path ? path : null;
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeCoTaskMem(text);
                }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // an older shell (or Wine without it): the profile's Downloads
        }
        return null;
    }

    private static long? FolderFreeSpace(string folder)
    {
        try
        {
            return GetDiskFreeSpaceExW(folder, out var free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

#pragma warning disable SYSLIB1054 // plain DllImport, as WineHost's
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string folder, out ulong freeToCaller, out ulong total, out ulong free);

    [System.Runtime.InteropServices.DllImport("shell32.dll", ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
#pragma warning restore SYSLIB1054
}
