// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Targets;

/// <summary>
/// The game targets this launcher knows, in the order they are tried and shown. The launcher core
/// picks one by <see cref="LauncherSettings.Target"/>; a folder the player chooses is offered to
/// each (see launcher/docs/TARGETS.md for adding one).
/// </summary>
public sealed class TargetRegistry
{
    private readonly List<IGameTarget> _targets;

    public TargetRegistry(IEnumerable<IGameTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = [.. targets];
        if (_targets.Count == 0)
        {
            throw new ArgumentException("at least one game target", nameof(targets));
        }
    }

    public IReadOnlyList<IGameTarget> All => _targets;

    /// <summary>The target with that id; the first one for an id this build does not know (a newer launcher's settings).</summary>
    public IGameTarget Get(string? id) => _targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? _targets[0];

    public IReadOnlyList<TargetInfo> Infos => [.. _targets.Select(t => new TargetInfo(t.Id, t.DisplayName, t.FolderHint, t.Capabilities) { GameVersions = t.GameVersions })];

    /// <summary>The first target that recognises the folder, with its install.</summary>
    public (IGameTarget Target, GameInstall Install)? Detect(string? folder, string? preferredId = null)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }
        foreach (var target in _targets.OrderBy(t => string.Equals(t.Id, preferredId, StringComparison.OrdinalIgnoreCase) ? 0 : 1))
        {
            if (GameFinder.Detect(target, folder) is { } install)
            {
                return (target, install);
            }
        }
        return null;
    }
}

/// <summary>
/// Finding the game without asking (Infinity Launcher's picker logic): the explicit folder, the
/// one the player chose before, the launcher's own folder and its parent, then a shallow look
/// (two levels) under the usual places. Among several found by the look-around, an install the
/// target can play wins. Nothing is searched deeper: a whole-disk scan would take minutes and
/// find backups and old copies.
/// </summary>
public static class GameFinder
{
    public const int Depth = 2;

    /// <summary>Per folder, so one huge Downloads folder cannot stall the look-around.</summary>
    private const int MaxSubfolders = 200;

    public static (IGameTarget Target, GameInstall Install)? Find(
        TargetRegistry targets, string? explicitFolder, string? savedFolder, string launcherFolder, string? preferredId = null,
        IEnumerable<string>? searchRoots = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(launcherFolder);
        foreach (var folder in new[] { explicitFolder, savedFolder, launcherFolder, Path.GetDirectoryName(launcherFolder.TrimEnd('\\', '/')) })
        {
            if (targets.Detect(folder, preferredId) is { } known)
            {
                return known;
            }
        }
        var found = new List<(IGameTarget Target, GameInstall Install)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = searchRoots ?? targets.All.SelectMany(t => SafeRoots(t));
        foreach (var root in roots)
        {
            foreach (var dir in Shallow(root, Depth))
            {
                cancel.ThrowIfCancellationRequested();
                if (seen.Add(dir) && targets.Detect(dir, preferredId) is { } hit)
                {
                    found.Add(hit);
                }
            }
        }
        foreach (var hit in found)
        {
            if (hit.Target.Check(hit.Install).Status is TargetStatus.Ready or TargetStatus.NoIntegration or TargetStatus.NeedsRebuild)
            {
                return hit;
            }
        }
        return found.Count > 0 ? found[0] : null;
    }

    private static IEnumerable<string> SafeRoots(IGameTarget target)
    {
        try
        {
            return [.. target.SearchRoots()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>A target's Detect, never throwing for a bad folder (a target written carelessly must not break the picker).</summary>
    internal static GameInstall? Detect(IGameTarget target, string folder)
    {
        try
        {
            return target.Detect(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The folder and its subfolders down to <paramref name="depth"/>, skipping what cannot be read.</summary>
    internal static IEnumerable<string> Shallow(string root, int depth)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            yield break;
        }
        var level = new List<string> { root };
        for (var d = 0; d <= depth && level.Count > 0; d++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                yield return dir;
                if (d == depth)
                {
                    continue;
                }
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(dir).Take(MaxSubfolders));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // unreadable: skipped
                }
            }
            level = next;
        }
    }
}
