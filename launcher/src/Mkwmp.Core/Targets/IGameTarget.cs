// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Targets;

/// <summary>
/// One way of running the game that MKWMP can hook into: mkw-recomp today; an emulator or another
/// recompilation later. The server is one service for all of them; a target owns everything that
/// differs per solution: finding and identifying an install, the version gates, the files that
/// put MKWMP into it (the probe beside the exe, or a mod), the session file the hook reads, and
/// how the game starts. The launcher core and the window only ever talk to this interface.
/// </summary>
public interface IGameTarget
{
    /// <summary>Stable id, saved in the settings (<c>target = "mkw-recomp"</c>).</summary>
    string Id { get; }

    /// <summary>What the player sees, e.g. "mkw-recomp".</summary>
    string DisplayName { get; }

    /// <summary>What a folder must hold, for the folder picker's title and hints ("the folder with mkw.exe").</summary>
    string FolderHint { get; }

    TargetCapabilities Capabilities { get; }

    /// <summary>The install in this folder, or null when the folder is not one (never throws for a bad folder).</summary>
    GameInstall? Detect(string folder);

    /// <summary>Where to look when nothing was chosen yet (shallow look-around roots).</summary>
    IEnumerable<string> SearchRoots();

    /// <summary>Version and rebuild gates for an install: what blocks play, and how to fix it.</summary>
    TargetCheck Check(GameInstall install);

    /// <summary>The state of MKWMP's integration in this install (probe/mod files), against the package the launcher carries.</summary>
    IntegrationReport Inspect(GameInstall install, IntegrationPackage? package);

    /// <summary>Installs or repairs the integration. The game must be closed.</summary>
    Task<IntegrationReport> InstallAsync(GameInstall install, IntegrationPackage package, CancellationToken cancel);

    /// <summary>Removes every MKWMP file and puts back what was there.</summary>
    Task UninstallAsync(GameInstall install, CancellationToken cancel);

    /// <summary>Writes (atomically) what the hook reads at start: the session file (mkwmp.toml for mkw-recomp), the lobby CA.</summary>
    void WriteSession(GameInstall install, SessionFile session);

    /// <summary>Drops the ticket from the session file (after a profile delete).</summary>
    void ForgetTicket(GameInstall install);

    /// <summary>Starts the game; null when the player starts it elsewhere (Steam).</summary>
    IGameProcess? Launch(GameInstall install, LaunchOptions options);

    /// <summary>Whether this target's game is running (also when not started by us).</summary>
    bool IsRunning(GameInstall install);

    /// <summary>Extra files for a bug report (game logs, redacted session file, a system line per file).</summary>
    IEnumerable<SupportItem> SupportItems(GameInstall install);

    // ---- additions with defaults (a target that needs none of them implements nothing) -----

    /// <summary>
    /// The release assets that make this target's <see cref="IntegrationPackage"/>. The launcher
    /// core finds them (the signed <c>game/</c> folder next to the launcher, else this launcher's
    /// GitHub release), verifies them against the signed SHA256SUMS and hands them over.
    /// </summary>
    IReadOnlyList<PackageAsset> PackageAssets => [];

    /// <summary>
    /// False when <see cref="InstallAsync"/> needs no files for this install right now (mkw-recomp
    /// with the MKWMP mod enabled: installing then only takes the launcher's own patch out). The
    /// core then calls it with <see cref="IntegrationPackage.Empty"/> even when offline.
    /// </summary>
    bool NeedsPackage(GameInstall install) => true;

    /// <summary>
    /// A developer keeps their own build of the integration in the game (MKWMP_DEV_PROBE=1): the
    /// core then installs only when the player presses INSTALL, never by itself on start or PLAY.
    /// </summary>
    bool KeepsOwnIntegration => false;

    /// <summary>Folders of this install that may hold MKWMP's native libraries (mkwmp-camera.dll) for the launcher's own device tests.</summary>
    IEnumerable<string> NativeLibraryFolders(GameInstall install) => [];
}

/// <param name="Name">The file's name in the release and in SHA256SUMS.</param>
/// <param name="Required">A release without it has no integration for this target.</param>
/// <param name="Label">What the player calls it, for errors ("game patch download failed: …").</param>
public sealed record PackageAsset(string Name, bool Required, string Label);

[Flags]
public enum TargetCapabilities
{
    None = 0,
    Friends = 1,
    VoiceChat = 2,
    Camera = 4,
    FriendRooms = 8,
    /// <summary>The launcher can start the game itself (else: "start it from Steam / the emulator").</summary>
    Launch = 16,
}

/// <summary>A found install. <paramref name="Version"/> is the runtime's (mkw-recomp 0.1.15-mkwmp), <paramref name="GameVersion"/> the game's (1.8.0).</summary>
public sealed record GameInstall(string TargetId, string Root, string Executable, string? Version, string? GameVersion, string? DataRoot);

public enum TargetStatus { Missing, Unsupported, NeedsRebuild, NoIntegration, Ready }

/// <param name="Problem">What blocks play, verbatim for the player; null when nothing does.</param>
/// <param name="Explain">Longer text (hover/details).</param>
/// <param name="FixUrl">Where to get a supported build (opened in the browser), if any.</param>
/// <param name="FixLabel">Button text for <paramref name="FixUrl"/>, e.g. "Get 0.1.15-mkwmp".</param>
public sealed record TargetCheck(TargetStatus Status, string? Problem, string? Explain, Uri? FixUrl, string? FixLabel);

public enum IntegrationState { Installed, Unverified, Missing, Outdated, Damaged, HostMissing, Dev, Mod }

/// <param name="Lines">Per-file lines ("winmm.dll: v1.4.0, verified").</param>
public sealed record IntegrationReport(IntegrationState State, string Summary, IReadOnlyList<string> Lines)
{
    public bool Ok => State is IntegrationState.Installed or IntegrationState.Unverified or IntegrationState.Dev or IntegrationState.Mod;
}

/// <summary>The verified files the launcher carries or downloaded for this version (name → bytes on disk).</summary>
public sealed record IntegrationPackage(string Version, IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Sha256)
{
    /// <summary>No files: for an install whose target says <see cref="IGameTarget.NeedsPackage"/> is false.</summary>
    public static IntegrationPackage Empty(string version) => new(version, new Dictionary<string, string>(), new Dictionary<string, string>());
}

/// <summary>What the hook reads (mkwmp.toml): see docs/contracts/LAUNCHER_API.md.</summary>
public sealed record SessionFile(string Server, string Name, string Launcher, string LauncherToken, int VoicePort, string? Ticket, int Slot, string? LobbyCaPem);

public sealed record LaunchOptions(string? CustomCommand);

public interface IGameProcess : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
}

public sealed record SupportItem(string Name, string? SourcePath, string? Text);
