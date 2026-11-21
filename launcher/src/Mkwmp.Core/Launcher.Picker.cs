// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Files;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>What the window's file picker asks of the targets: what to list and mark, and whether a choice is one they take.</summary>
public sealed partial class Launcher
{
    private PickFilter? _gameFolderFilter;

    public PickFilter GameFolderFilter => _gameFolderFilter ??= new PickFilter
    {
        FolderMatches = folder => _targets.All.Any(t => t.LooksLikeInstall(folder)),
    };

    public string? GameFolderProblem(string folder) =>
        _targets.Detect(folder, Settings.Target) is null
            ? Strings.NotAGameFolder(string.Join(" / ", _targets.All.Select(t => t.FolderHint)))
            : null;

    public PickFilter GameCopyFilter => _targets.Get(Settings.Target).GameCopyFilter;

    public string? GameCopyProblem(string path) => _targets.Get(Settings.Target).GameCopyProblem(path);
}
