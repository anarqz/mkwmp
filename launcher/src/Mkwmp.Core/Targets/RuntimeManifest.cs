// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Targets;

/// <summary>
/// The one build of a game runtime this launcher installs: a release asset named by repository,
/// tag and file name, with its size and SHA-256. It is written in the launcher's source and
/// changes only with a launcher release: nothing here, nor in <see cref="RuntimeInstaller"/>,
/// asks a server which release is the newest, so a new upstream release can neither break nor
/// replace what players have. A target hands its pinned entry out as
/// <see cref="IGameTarget.Runtime"/> (mkw-recomp: <see cref="MkwRecomp.RecompRuntime.Pinned"/>).
/// <para>
/// A runtime is the program only. The game itself is never part of it: the player brings their
/// own copy, and whatever the runtime makes from it stays on their machine
/// (<see cref="Preserved"/> keeps an update away from all of it).
/// </para>
/// </summary>
public sealed record RuntimeManifest
{
    /// <summary>What the player calls it ("mkw-recomp").</summary>
    public required string Name { get; init; }

    /// <summary>The runtime's version, as the target's <see cref="GameInstall.Version"/> names it ("0.1.15-mkwmp").</summary>
    public required string Version { get; init; }

    /// <summary>The GitHub repository the release is in (<c>owner/name</c>).</summary>
    public required string Repository { get; init; }

    /// <summary>The release's tag ("v0.1.15-mkwmp"). Never a moving name such as a branch.</summary>
    public required string Tag { get; init; }

    /// <summary>The asset's file name in that release (a zip).</summary>
    public required string Asset { get; init; }

    /// <summary>The asset's size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>The asset's SHA-256 (lower-case hex). A download with another one is thrown away.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The runtime's executable, at the root of the install ("mkw.exe").</summary>
    public required string Executable { get; init; }

    /// <summary>The SHA-256 of <see cref="Executable"/> inside the asset: what the target's own detection knows this version by.</summary>
    public required string ExecutableSha256 { get; init; }

    /// <summary>The size of the files the asset unpacks to, in bytes (measured from the asset).</summary>
    public required long InstalledSize { get; init; }

    /// <summary>
    /// Disk space the runtime's own first-run setup needs on top (what it builds from the
    /// player's game), in bytes. Only counted when checking free space before a new install.
    /// </summary>
    public long SetupSpace { get; init; }

    /// <summary>
    /// Versions ending in this are builds of the same fork ("-mkwmp"): an install of one may be
    /// updated in place. Any other install is someone else's and is left alone.
    /// </summary>
    public required string LineageSuffix { get; init; }

    /// <summary>The folder name under the launcher's data folder for the default location.</summary>
    public required string FolderName { get; init; }

    /// <summary>
    /// Top-level names of an install that an update never replaces once they exist: the bundled
    /// toolchain, the setup's state, the player's settings, saves and mods, logs, and everything
    /// made from the player's game. One <c>*</c> stands for any run of characters. Compared in
    /// any case.
    /// </summary>
    public IReadOnlyList<string> Preserved { get; init; } = [];

    /// <summary>Whose work the runtime is, and its license.</summary>
    public required RuntimeCredit Credit { get; init; }

    /// <summary>
    /// Which of the release's builds this is, in English, when the release has more than one for the
    /// platform ("no AVX2"); null for its main build.
    /// </summary>
    public string? Variant { get; init; }

    /// <summary>The catalog key of <see cref="Variant"/>.</summary>
    public string? VariantKey { get; init; }

    /// <summary>
    /// The SHA-256 of executables of this same <see cref="Version"/> that do not run on this machine (the
    /// release's AVX2 build, for its no-AVX2 one): an install holding one is not up to date. Another build of
    /// the version that does run here is (no download only to change builds).
    /// </summary>
    public IReadOnlyList<string> Unsuitable { get; init; } = [];

    /// <summary><see cref="Variant"/> in the language in use; null for the main build.</summary>
    public string? VariantText => Variant is null ? null : VariantKey is { } key ? Strings.T(key, Variant) : Variant;

    /// <summary>The version as the window names it: "0.1.16-mkwmp.1", "0.1.16-mkwmp.1 (no AVX2)".</summary>
    public string Label => VariantText is { } variant ? $"{Version} ({variant})" : Version;

    /// <summary>Whether the executable in <paramref name="folder"/> is not one of <see cref="Unsuitable"/> (true when it cannot be read: the target's own look says the rest).</summary>
    public bool Suits(string folder)
    {
        if (Unsuitable.Count == 0)
        {
            return true;
        }
        try
        {
            using var file = File.OpenRead(Path.Combine(folder, Executable));
            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(file));
            return !Unsuitable.Contains(hash, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Where the asset is downloaded from: the release's own download address for exactly
    /// <see cref="Tag"/> and <see cref="Asset"/>.
    /// </summary>
    public Uri Url => UrlOverride ?? new Uri($"https://github.com/{Repository}/releases/download/{Tag}/{Asset}");

    /// <summary>Tests: a server inside the test.</summary>
    internal Uri? UrlOverride { get; init; }

    /// <summary>Whether an install of <paramref name="version"/> is a build of this fork (this very version included).</summary>
    public bool SameLineage(string? version) =>
        version is not null
        && (version == Version || (LineageSuffix.Length > 0 && version.EndsWith(LineageSuffix, StringComparison.Ordinal)));

    /// <summary>Whether a path inside the install (relative, any separator) is under a <see cref="Preserved"/> name.</summary>
    public bool IsPreserved(string relativePath)
    {
        ArgumentNullException.ThrowIfNull(relativePath);
        var top = TopName(relativePath);
        return Preserved.Any(p => Matches(p, top));
    }

    internal static string TopName(string relativePath)
    {
        var path = relativePath.TrimStart('/', '\\');
        var cut = path.IndexOfAny(['/', '\\']);
        return cut < 0 ? path : path[..cut];
    }

    private static bool Matches(string pattern, string name)
    {
        var star = pattern.IndexOf('*', StringComparison.Ordinal);
        if (star < 0)
        {
            return pattern.Equals(name, StringComparison.OrdinalIgnoreCase);
        }
        var (head, tail) = (pattern[..star], pattern[(star + 1)..]);
        return name.Length >= head.Length + tail.Length
            && name.StartsWith(head, StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Credit for a runtime the launcher installs: shown in About and on the install step, and kept in NOTICE.</summary>
/// <param name="Summary">One or two sentences for the player, naming the project and its author.</param>
/// <param name="Project">The original project's name.</param>
/// <param name="ProjectUrl">The original project's page.</param>
/// <param name="SourceUrl">The source of the exact build that is installed (the fork, at the pinned tag).</param>
/// <param name="License">The license's short name ("MIT").</param>
/// <param name="Notice">The copyright and permission notice, in full, as the license requires with every copy.</param>
/// <param name="LicensesFolder">The folder of the install that holds this notice and those of the libraries the runtime ships.</param>
public sealed record RuntimeCredit(string Summary, string Project, Uri ProjectUrl, Uri SourceUrl, string License, string Notice, string LicensesFolder)
{
    /// <summary>The catalog key of <see cref="Summary"/> (which is its English), when it has one.</summary>
    public string? SummaryKey { get; init; }

    /// <summary>This credit with its summary in the language in use.</summary>
    public RuntimeCredit Localized() => SummaryKey is { } key && Strings.T(key, Summary) is var text && text != Summary ? this with { Summary = text } : this;
}
