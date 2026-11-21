// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

public enum RuntimeState
{
    /// <summary>The game in use has no runtime the launcher installs.</summary>
    Unavailable,

    /// <summary>No install: <see cref="ILauncher.InstallRuntimeAsync"/> makes one in <see cref="RuntimeView.Folder"/>.</summary>
    NotInstalled,

    /// <summary>
    /// An install is the game folder: the pinned build, or (<see cref="RuntimeView.Foreign"/>)
    /// the player's own, which the launcher leaves alone and offers to install its build beside.
    /// </summary>
    Installed,

    /// <summary>An install of MKWMP's build that is not the pinned version: <see cref="ILauncher.InstallRuntimeAsync"/> updates it in place.</summary>
    UpdateAvailable,

    Downloading,
    Verifying,
    Extracting,

    /// <summary>Putting the files in place (a moment).</summary>
    Installing,

    /// <summary>The last install failed: <see cref="RuntimeView.Note"/> says why. Nothing was changed; the download is kept when it can be continued.</summary>
    Failed,
}

/// <summary>The game's runtime as the launcher installs it (<see cref="RuntimeManifest"/>), for the window.</summary>
/// <param name="InstalledVersion">The runtime in the game folder; null when there is none or it is not a known one.</param>
/// <param name="PinnedVersion">The one version this launcher installs; null when <see cref="RuntimeState.Unavailable"/>.</param>
/// <param name="Folder">Where an install would go now: the install in use when it is MKWMP's build, else the default folder.</param>
/// <param name="Foreign">The game folder holds an install that is not MKWMP's build: never replaced; ours goes into <paramref name="Folder"/>, beside it.</param>
/// <param name="Progress">0..1 of the phase in progress.</param>
/// <param name="BytesDone">Of the phase in progress (a continued download starts above zero).</param>
/// <param name="BytesTotal">Of the phase in progress.</param>
/// <param name="BytesPerSecond">0 while not known.</param>
/// <param name="Remaining">Of the phase in progress; null while not known.</param>
/// <param name="DownloadSize">The download, in bytes.</param>
/// <param name="DiskSpace">What a new install needs free: the download, what it unpacks to, and what the runtime's setup then builds from the player's game.</param>
/// <param name="Note">Why it failed, or what just happened (installed, paused); null when nothing to say.</param>
/// <param name="Credit">Whose work the runtime is: shown wherever it is installed or named.</param>
public sealed record RuntimeView(
    RuntimeState State,
    string? InstalledVersion,
    string? PinnedVersion,
    string? Folder,
    bool Foreign,
    double Progress,
    long BytesDone,
    long BytesTotal,
    double BytesPerSecond,
    TimeSpan? Remaining,
    long DownloadSize,
    long DiskSpace,
    string? Note,
    RuntimeCredit? Credit)
{
    public static RuntimeView None { get; } = new(RuntimeState.Unavailable, null, null, null, false, 0, 0, 0, 0, null, 0, 0, null, null);

    /// <summary>Which of the release's builds is pinned for this processor, in the language in use ("no AVX2"); null for its main build.</summary>
    public string? PinnedVariant { get; init; }

    /// <summary>
    /// The install's executable is a developer's test build accepted as the pinned version (<c>[dev] recomp_exe_sha256</c>):
    /// its SHA-256. Never updated or replaced; the window says "Development mkw-recomp". Null for a release.
    /// </summary>
    public string? DevSha256 { get; init; }

    /// <summary>The pinned version as the window names it: "0.1.16-mkwmp.1", or "0.1.16-mkwmp.1 (no AVX2)".</summary>
    public string? PinnedLabel => PinnedVersion is { } version && PinnedVariant is { } variant ? $"{version} ({variant})" : PinnedVersion;

    /// <summary>An install is in progress.</summary>
    public bool Busy => State is RuntimeState.Downloading or RuntimeState.Verifying or RuntimeState.Extracting or RuntimeState.Installing;
}
