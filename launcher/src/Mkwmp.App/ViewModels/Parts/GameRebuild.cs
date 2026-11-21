// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.App.ViewModels;

/// <summary>
/// "Set up the game again" (the play checklist's setup row, Settings › Game): asks first, since it takes as long as the
/// first setup, then runs every step that builds the game from the same copy (<see cref="Core.ILauncher.RebuildGameAsync"/>),
/// shown in the Game Setup card. A refusal (the game, PLAY or a setup is running) is said in a card.
/// </summary>
public static class GameRebuild
{
    /// <summary>The game is one the launcher sets up, set up once from a copy it knows, and nothing holds it now.</summary>
    public static bool CanRun(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return GameCopy.CanChange(shell);
    }

    public static async Task AskAsync(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var s = shell.S;
        if (!await shell.ConfirmAsync(s["checklist.rebuild.title"], s["checklist.rebuild.body"], s["checklist.rebuild.yes"]).ConfigureAwait(true))
        {
            return;
        }
        App.Log("game setup: set up again, asked by the player");
        var run = shell.Launcher.RebuildGameAsync(CancellationToken.None);
        shell.ShowGameWork(); // the card follows the setup: its steps, Stop, and how it ended
        if (await run.ConfigureAwait(true) is { } why)
        {
            shell.CloseModal();
            await shell.MessageAsync(s["settings.game.rebuild_row"], why).ConfigureAwait(true);
        }
    }
}
