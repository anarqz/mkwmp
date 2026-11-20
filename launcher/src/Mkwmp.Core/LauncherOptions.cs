// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Reflection;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// How the launcher was started (the Rust launcher's command line, unchanged, so shortcuts,
/// the start-up entry and Steam launch options keep working) and what the host overrides.
/// <code>
/// mkwmp-launcher [client.toml]                 the window
/// mkwmp-launcher --tray [client.toml]          the window, started in the tray
/// mkwmp-launcher --headless [--no-launch] [--no-update] [client.toml]
/// mkwmp-launcher --save-logs [client.toml]
/// mkwmp-launcher --version
/// </code>
/// </summary>
public sealed record LauncherOptions
{
    public const string Usage = "usage: mkwmp-launcher [--headless | --tray] [--no-launch] [--no-update] [--save-logs] [--version] [client.toml]";

    /// <summary>client.toml (null: next to the launcher).</summary>
    public string? ConfigPath { get; init; }

    /// <summary>No window: updates, registers a free name, plays, exits with the session.</summary>
    public bool Headless { get; init; }

    /// <summary>Start hidden in the tray (the start-up entry).</summary>
    public bool Tray { get; init; }

    /// <summary>Only hold the seat for this run (not saved): the game starts elsewhere.</summary>
    public bool NoLaunch { get; init; }

    /// <summary>No update check and no downloads for this run (not saved).</summary>
    public bool NoUpdate { get; init; }

    /// <summary>
    /// Started by the mod of a running game as its overlay host (<c>--overlay-host</c>, see
    /// <see cref="Overlay.OverlayHostArgs"/>; not a flag <see cref="Parse"/> reads: the window sets
    /// it). Nothing is done for a game that already runs: no update check, no sync of the game's
    /// integration, no start-up entry, until <see cref="Launcher.LeaveOverlayHostMode"/>.
    /// </summary>
    public bool OverlayHost { get; init; }

    /// <summary>Write the bug-report zip and exit.</summary>
    public bool SaveLogs { get; init; }

    public bool ShowVersion { get; init; }

    public bool ShowHelp { get; init; }

    /// <summary>The arguments as given, to hand to the new launcher after a self-update.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// A newer launcher found at start is downloaded and restarted into without asking (the Rust
    /// launcher's behaviour, and what <c>--headless</c> does). False: <see cref="ILauncher.Update"/>
    /// says Available and waits for <see cref="ILauncher.ApplyUpdateAsync"/>, so the window can ask first.
    /// </summary>
    public bool ApplyUpdatesAutomatically { get; init; }

    /// <summary>
    /// The file a self-update replaces (null: this process's executable, unless that is the
    /// <c>dotnet</c> host or a test runner, which must never be replaced).
    /// </summary>
    public string? Executable { get; init; }

    /// <summary>This launcher's version (null: the assembly's).</summary>
    public string? Version { get; init; }

    /// <summary>The GitHub API base (null: <c>MKWMP_UPDATE_API</c>, else api.github.com).</summary>
    public string? UpdateApi { get; init; }

    /// <summary>The beholder (server directory) to use when client.toml names none (tests: a fake); null = the release default.</summary>
    public string? Beholder { get; init; }

    /// <summary>The game targets (null: the built-in ones, mkw-recomp).</summary>
    public IReadOnlyList<IGameTarget>? Targets { get; init; }

    /// <summary>The start-up entry (null: the per-user Run key).</summary>
    public IAutostart? Autostart { get; init; }

    /// <summary>Where the look-around for a game searches (null: the targets' own roots).</summary>
    public IReadOnlyList<string>? SearchRoots { get; init; }

    // ---- tests ----------------------------------------------------------------------------
    internal IReadOnlyList<string>? ReleaseKeys { get; init; }

    /// <summary>The PLAY loop's beat (1 s).</summary>
    internal TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);

    internal TimeSpan? BusyRetry { get; init; }

    /// <summary>False: no status, friends, stars, game-refresh and update loops (a test drives each by hand).</summary>
    internal bool BackgroundLoops { get; init; } = true;

    /// <summary>False: the local API is not bound by <see cref="ILauncher.StartAsync"/> (and counts as up).</summary>
    internal bool BindLocalApi { get; init; } = true;

    /// <summary>What ends the process after a self-update (tests: nothing).</summary>
    internal Action<int>? Exit { get; init; }

    /// <summary>What starts the update helper (its path, its arguments) instead of a process; false: it could not be started.</summary>
    internal Func<string, IReadOnlyList<string>, bool>? StartUpdateHelper { get; init; }

    /// <summary>Where a report's zip goes instead of the Desktop.</summary>
    internal string? DesktopFolder { get; init; }

    /// <summary>The runtime to install instead of the target's pinned one (a small zip served by the test).</summary>
    internal RuntimeManifest? Runtime { get; init; }

    /// <summary>Where the runtime is installed by default (null: <c>%LOCALAPPDATA%\MKWMP\&lt;name&gt;</c>).</summary>
    internal string? RuntimeFolder { get; init; }

    /// <summary>Where the runtime's download is kept (null: <c>%LOCALAPPDATA%\MKWMP\downloads</c>).</summary>
    internal string? RuntimeCache { get; init; }

    /// <summary>Free bytes on a path's drive (null: the drive's own answer).</summary>
    internal Func<string, long?>? RuntimeFreeSpace { get; init; }

    public static LauncherOptions Parse(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var all = args.ToArray();
        var o = new LauncherOptions { Arguments = all };
        foreach (var arg in all)
        {
            o = arg switch
            {
                "--save-logs" => o with { SaveLogs = true },
                "--headless" => o with { Headless = true, ApplyUpdatesAutomatically = true },
                "--no-launch" => o with { NoLaunch = true },
                "--no-update" => o with { NoUpdate = true },
                "--tray" => o with { Tray = true },
                "--version" or "-V" => o with { ShowVersion = true },
                "-h" or "--help" => o with { ShowHelp = true },
                _ => o with { ConfigPath = arg },
            };
        }
        return o;
    }

    /// <summary><c>mkwmp-launcher 1.4.0</c>, for <c>--version</c>.</summary>
    public string VersionLine => $"mkwmp-launcher {Version ?? AssemblyVersion}";

    /// <summary>The folder of the executable: where client.toml and <c>logs/</c> live.</summary>
    public static string LauncherFolder => AppContext.BaseDirectory.TrimEnd('\\', '/') is { Length: > 0 } dir ? dir : ".";

    public static string DefaultConfigPath(string launcherFolder) => Path.Combine(launcherFolder, "client.toml");

    /// <summary>The version this build was stamped with (Directory.Build.props), without the source revision.</summary>
    public static string AssemblyVersion
    {
        get
        {
            var info = typeof(LauncherOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus < 0 ? info : info[..plus];
        }
    }

    /// <summary>The executable a self-update may replace; null under <c>dotnet</c> or a test host.</summary>
    internal static string? SelfExecutable()
    {
        var path = Environment.ProcessPath;
        var name = Path.GetFileNameWithoutExtension(path) ?? "";
        return path is null || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ? null : path;
    }
}
