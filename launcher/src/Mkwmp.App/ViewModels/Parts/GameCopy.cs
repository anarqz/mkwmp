// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;
using Mkwmp.Core.Targets;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// "Use another copy of the game", once the game is set up (Settings › Game, and the Game Setup card when it has
/// nothing else to do): the launcher's own picker with the first setup's check, then how the game takes it (read in
/// place or copied; the current way first), then the setup again with it (<see cref="ILauncher.ChangeGameCopyAsync"/>),
/// shown in the Game Setup card. Controller-first: every step is a list or a card.
/// </summary>
public static class GameCopy
{
    /// <summary>A copy of the game is known and nothing holds the game folder now (the launcher says the rest when asked).</summary>
    public static bool CanChange(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var launcher = shell.Launcher;
        return launcher.GameSetup is { Available: true, Busy: false, DumpPath: not null } && launcher.Game.Install is not null
            && !launcher.Runtime.Busy && !launcher.Session.Busy && !shell.GameRuns;
    }

    /// <summary>Shown at all: the game is one the launcher sets up, and it was set up once (a copy is on record).</summary>
    public static bool Applies(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return shell.Launcher.GameSetup is { Available: true, DumpPath: not null } && shell.Launcher.Game.Install is not null;
    }

    /// <summary>"In use: D:\Games\Mario Kart World.nup, read where it is."; "" when no copy is on record.</summary>
    public static string InUse(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var setup = shell.Launcher.GameSetup;
        return setup.DumpPath is { } path
            ? shell.S.Format("gamecopy.in_use", ("path", path), ("mode", shell.S[setup.Mode == GameDataMode.Copy ? "gamecopy.mode.copy" : "gamecopy.mode.link"]))
            : "";
    }

    public static async Task ChangeAsync(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        if (!CanChange(shell))
        {
            return;
        }
        var (s, launcher) = (shell.S, shell.Launcher);
        var setup = launcher.GameSetup;
        // The same picker and check as the first setup: only a copy the setup takes comes back.
        if (await shell.PickAsync(PickerRequests.GameCopy(shell, folderOnly: false, setup.DumpPath)).ConfigureAwait(true) is not { } path)
        {
            return;
        }
        var picked = await Choice.AskAsync(shell, s["gamecopy.mode_title"], s.Format("gamecopy.mode_body", ("path", path)),
        [
            new ChoiceOption("link", s["gamecopy.link"], s["gamecopy.link_caption"]),
            new ChoiceOption("copy", s["gamecopy.copy"], s["gamecopy.copy_caption"]),
        ], setup.Mode == GameDataMode.Copy ? "copy" : "link").ConfigureAwait(true);
        if (picked is null)
        {
            return;
        }
        var mode = picked == "copy" ? GameDataMode.Copy : GameDataMode.Link;
        // What changed while the player chose (the game started, a download began) is said before anything is shown as running.
        if (await Task.Run(() => launcher.GameCopyChangeProblem(path)).ConfigureAwait(true) is { } why)
        {
            await shell.MessageAsync(s["gamecopy.refused"], why).ConfigureAwait(true);
            return;
        }
        App.Log($"game copy: {setup.DumpPath} ({setup.Mode}) -> {path} ({mode})");
        var run = launcher.ChangeGameCopyAsync(path, mode, CancellationToken.None);
        shell.ShowGameWork(); // the card follows the setup: its steps, Stop, and how it ended
        if (await run.ConfigureAwait(true) is { } late)
        {
            await shell.MessageAsync(s["gamecopy.refused"], late).ConfigureAwait(true);
        }
    }
}
