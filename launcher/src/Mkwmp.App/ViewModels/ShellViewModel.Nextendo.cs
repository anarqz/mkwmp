// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// "Play on: MKWMP servers | Nextendo Network": the one choice of where PLAY goes online, offered wherever the
/// player goes online (home's status line, Settings › Online, Settings › Nextendo) once a Nextendo account is linked.
/// It is saved (client.toml), and the next PLAY starts the game on that network.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>A Nextendo account is linked: the choice is shown.</summary>
    public bool NextendoLinked => Launcher.Nextendo.Linked;

    /// <summary>PLAY goes to Nextendo Network now: what is an MKWMP server's (friends, voice, camera) says so instead of "Configure online".</summary>
    public bool PlaysOnNextendo => Launcher.Nextendo.Active;

    /// <summary>"MKWMP servers" or "Nextendo Network": the saved choice.</summary>
    public string PlayOnName => S[Launcher.Nextendo.PlayOn == PlayNetwork.Nextendo ? "playon.nextendo" : "playon.mkwmp"];

    /// <summary>The choice, as a list of two; not while PLAY runs (the game is on one of them).</summary>
    public async Task PickPlayOnAsync()
    {
        var nextendo = Launcher.Nextendo;
        if (!nextendo.Linked)
        {
            return;
        }
        if (Launcher.Session.Busy)
        {
            ShowToast(S["playon.label"], S["playon.busy"]);
            return;
        }
        var current = nextendo.PlayOn == PlayNetwork.Nextendo ? "nextendo" : "mkwmp";
        var picked = await Choice.AskAsync(this, S["playon.label"], S["playon.body"],
        [
            new ChoiceOption("mkwmp", S["playon.mkwmp"], S["playon.mkwmp_caption"]),
            new ChoiceOption("nextendo", S["playon.nextendo"], S.Format("playon.nextendo_caption", ("name", nextendo.Username))),
        ], current).ConfigureAwait(true);
        if (picked is null || picked == current)
        {
            return;
        }
        await Launcher.SetPlayOnAsync(picked == "nextendo" ? PlayNetwork.Nextendo : PlayNetwork.Mkwmp).ConfigureAwait(true);
        if (Launcher.Nextendo is { NoteIsError: true, Note: { } why })
        {
            ShowToast(S["playon.label"], why);
        }
    }
}
