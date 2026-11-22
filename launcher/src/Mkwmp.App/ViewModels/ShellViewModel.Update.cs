// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>The launcher's own update, outside its card (launcher/docs/UPDATES.md).</summary>
public sealed partial class ShellViewModel
{
    private UpdateState _updateSeen;

    /// <summary>
    /// A download that finishes while the player is elsewhere (the card was closed with "Close")
    /// is said with a toast; installing stays the player's choice, from the What's New tile or
    /// Settings › Updates. With the card open, the card itself says it.
    /// </summary>
    private void WatchUpdate()
    {
        var update = Launcher.Update;
        var before = _updateSeen;
        _updateSeen = update.State;
        // A required update: its card comes by itself and stays (UpdateModal.Blocks), over whatever was open.
        if (UpdateModal.Blocks(this) && _modal is not UpdateModal)
        {
            ShowUpdateModal();
            return;
        }
        if (before == UpdateState.Downloading && update.State == UpdateState.ReadyToInstall && _modal is not UpdateModal)
        {
            ShowToast(S["update.toast_ready"], S.Format("update.toast_ready_body", ("version", update.AvailableVersion ?? "")));
        }
    }
}
