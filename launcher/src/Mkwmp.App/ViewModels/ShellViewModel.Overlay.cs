// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Media;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Overlay mode (launcher/docs/OVERLAY.md): the launcher is over a running game, as a console's
/// HOME menu is. Home's Play tile is then "Back to the game" with the game's picture on it, B on
/// home goes back, and PLAY starts nothing. The window work (showing over the game, the shrink
/// and grow animation, giving the game back) is Overlay/OverlayController's; the shell only holds
/// the state the pages read.
/// </summary>
public sealed partial class ShellViewModel
{
    private bool _isOverlay;
    private ImageSource? _overlayThumb;
    private bool _overlayHasMenu;

    /// <summary>The launcher is shown over a running game.</summary>
    public bool IsOverlay
    {
        get => _isOverlay;
        private set => Set(ref _isOverlay, value);
    }

    /// <summary>The game's picture when the overlay was asked for (a drawn card when the game sent none); null outside overlay mode.</summary>
    public ImageSource? OverlayThumb
    {
        get => _overlayThumb;
        private set => Set(ref _overlayThumb, value);
    }

    /// <summary>The game can open its own menu when told to: home offers it.</summary>
    public bool OverlayHasMenu
    {
        get => _overlayHasMenu;
        private set => Set(ref _overlayHasMenu, value);
    }

    /// <summary>
    /// The window's way back to the game (the grow animation, then the game gets the focus);
    /// the argument is "with the game's own menu open". Without one, leaving only ends the mode.
    /// </summary>
    public Action<bool>? OverlayReturnSink { get; set; }

    /// <summary>
    /// "Quit Game" (Overlay/OverlayController): ends the running game, cleanly first; the argument
    /// is "and then quit the launcher too". Null where no game can be ended (the harness).
    /// </summary>
    public Func<bool, Task>? QuitGameSink { get; set; }

    /// <summary>A game the launcher can end runs now (set by the controller); over a game it always does.</summary>
    public Func<bool>? GameRunsProbe { get; set; }

    /// <summary>A game runs that "Quit Game" can end: the one under the overlay, or one the launcher sees beside it.</summary>
    public bool GameRuns => IsOverlay || GameRunsProbe?.Invoke() == true;

    /// <summary>
    /// "Quit Game": asks first (what is not saved is lost; online, the seat is given back), then
    /// ends the game for real, not only the launcher. <paramref name="thenQuit"/>: the launcher
    /// quits after it. <paramref name="confirmed"/>: the question was already asked.
    /// </summary>
    public async Task QuitGameAsync(bool thenQuit = false, bool confirmed = false)
    {
        if (!GameRuns)
        {
            return;
        }
        var body = Launcher.OnlineEnabled ? S["quitgame.body"] + " " + S["quitgame.seat"] : S["quitgame.body"];
        if (!confirmed && !await ConfirmAsync(S["quitgame.title"], body, S["quitgame.yes"]).ConfigureAwait(true))
        {
            return;
        }
        if (QuitGameSink is { } sink)
        {
            await sink(thenQuit).ConfigureAwait(true);
        }
        else
        {
            EndOverlay(); // nothing behind the picture (the harness)
        }
    }

    /// <summary>
    /// The game asked for the launcher: home, with the Play tile as "Back to the game" showing
    /// <paramref name="frame"/>. <paramref name="again"/>: it was already up and only comes to
    /// the front again, so the player stays where they were.
    /// </summary>
    public void EnterOverlay(ImageSource? frame, bool again)
    {
        OverlayThumb = frame;
        OverlayHasMenu = Launcher.OverlayBridge.GameHasMenu;
        if (!(again && IsOverlay))
        {
            // a console's HOME: whatever was open is put away, the game's card has the focus
            CloseKeyboard();
            CloseSheet();
            IsFriendsOpen = false;
            DismissModal();
            if (Page is not HomeViewModel)
            {
                GoHome();
            }
        }
        IsOverlay = true;
        Page?.OnStateChanged();
        RefreshHints();
    }

    /// <summary>Back to the game (the Play tile, B on home, the Guide button), or to the game's own menu.</summary>
    public void LeaveOverlay(bool toMenu = false)
    {
        if (!IsOverlay)
        {
            return;
        }
        if (OverlayReturnSink is { } sink)
        {
            sink(toMenu);
        }
        else
        {
            EndOverlay();
        }
    }

    /// <summary>Overlay mode is over (the game has the screen again, or it ended): the ordinary launcher.</summary>
    public void EndOverlay()
    {
        if (!IsOverlay)
        {
            return;
        }
        IsOverlay = false;
        OverlayThumb = null;
        OverlayHasMenu = false;
        Page?.OnStateChanged();
        RefreshHints();
    }
}
