// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.App.Controls;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Nextendo Network is where the launcher goes online (the default): home, the checklist and every locked place lead
/// to "Sign in with Nextendo" (<see cref="SignInNextendo"/>: the sign-in page in the browser, and a card that waits
/// for it). MKWMP servers are the alternative, chosen only in Settings (Settings › Online's switch, and Settings ›
/// Nextendo's "Play on" row: <see cref="PickPlayOnAsync"/>); the choice is saved (client.toml), and the next PLAY
/// starts the game on that network.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>nextendo.network's sign-up page.</summary>
    public const string NextendoRegisterUrl = "https://nextendo.network/register.html";

    /// <summary>A Nextendo account is linked.</summary>
    public bool NextendoLinked => Launcher.Nextendo.Linked;

    /// <summary>PLAY goes to Nextendo Network now (linked, chosen, not an offline-only run).</summary>
    public bool PlaysOnNextendo => Launcher.Nextendo.Active;

    /// <summary>
    /// Nextendo Network is where this launcher goes online, signed in or not: its pages speak Nextendo, and what is an
    /// MKWMP server's (friends, voice, camera) says so instead of "Configure online".
    /// </summary>
    public bool NextendoFirst => Launcher.Nextendo.Preferred;

    /// <summary>Nextendo is where PLAY goes online and nobody is signed in: PLAY is offline, and the way online is "Sign in with Nextendo".</summary>
    public bool NextendoNeedsSignIn => Launcher.Nextendo.NeedsSignIn;

    /// <summary>"MKWMP servers" or "Nextendo Network": the saved choice.</summary>
    public string PlayOnName => S[Launcher.Nextendo.PlayOn == PlayNetwork.Nextendo ? "playon.nextendo" : "playon.mkwmp"];

    /// <summary>Settings › Nextendo, from wherever (the friends panel closes): the account, its friend code, how it stands.</summary>
    public void OpenNextendo()
    {
        IsFriendsOpen = false;
        OpenSettings(SettingsViewModel.NextendoCategory);
    }

    /// <summary>nextendo.network's sign-up page, in the browser.</summary>
    public void CreateNextendoAccount() => Links.Open(this, NextendoRegisterUrl);

    /// <summary>
    /// "Sign in with Nextendo", from anywhere: the sign-in page opens in the browser and a card waits for it (Cancel
    /// gives up; "Create a Nextendo Account" for who has none yet). Signed in, PLAY goes to Nextendo Network. Not
    /// while PLAY runs on Nextendo's side (the game is offline for want of the account: STOP first), and in a run that
    /// is offline only the way on is the update.
    /// </summary>
    public void SignInNextendo()
    {
        IsFriendsOpen = false;
        if (OnlineBlockedByUpdate)
        {
            ShowUpdateModal();
            return;
        }
        if (Launcher.Session.Busy && Launcher.Nextendo.Preferred)
        {
            ShowToast(S["nextendo.signin"], S["playon.busy"]);
            return;
        }
        if (Launcher.Nextendo.Linking)
        {
            return; // its card (or the page that started it) is already waiting
        }
        RelayCommand.Async(SignInNextendoAsync).Execute(null);
    }

    private async Task SignInNextendoAsync()
    {
        var modal = new NextendoSignInModal(this);
        ShowModal(modal);
        await Launcher.LinkNextendoAsync(page => Links.Open(page)).ConfigureAwait(true);
        CloseModal(modal);
        var now = Launcher.Nextendo;
        if (now is { NoteIsError: true, Note: { } why })
        {
            await MessageAsync(S["nextendo.signin"], why).ConfigureAwait(true);
        }
        else if (now is { Linked: true, Note: { } done })
        {
            ShowToast(S["nextendo.title"], done); // "Signed in as bia"
        }
    }

    /// <summary>
    /// The choice of network, as a list of two (Settings › Nextendo's "Play on" row); not while PLAY runs (the game is
    /// on one of them).
    /// </summary>
    public async Task PickPlayOnAsync()
    {
        var nextendo = Launcher.Nextendo;
        if (Launcher.Session.Busy)
        {
            ShowToast(S["playon.label"], S["playon.busy"]);
            return;
        }
        var current = nextendo.PlayOn == PlayNetwork.Nextendo ? "nextendo" : "mkwmp";
        var picked = await Choice.AskAsync(this, S["playon.label"], S["playon.body"],
        [
            new ChoiceOption("nextendo", S["playon.nextendo"], nextendo.Linked ? S.Format("playon.nextendo_caption", ("name", nextendo.Username)) : S["nextendo.about"]),
            new ChoiceOption("mkwmp", S["playon.mkwmp"], S["playon.mkwmp_caption"]),
        ], current).ConfigureAwait(true);
        if (picked is null || picked == current)
        {
            return;
        }
        await SetPlayOnAsync(picked == "nextendo" ? PlayNetwork.Nextendo : PlayNetwork.Mkwmp).ConfigureAwait(true);
    }

    /// <summary>Saves the choice of network; says why when it could not change (PLAY runs).</summary>
    public async Task SetPlayOnAsync(PlayNetwork network)
    {
        await Launcher.SetPlayOnAsync(network).ConfigureAwait(true);
        if (Launcher.Nextendo is { NoteIsError: true, Note: { } why } && Launcher.Nextendo.PlayOn != network)
        {
            ShowToast(S["playon.label"], why);
        }
    }

    /// <summary>Home's invitation to Nextendo Network was closed: it stays away (client.toml).</summary>
    public Task DismissNextendoInviteAsync() =>
        UpdateSettingsAsync(s => s with { Ui = s.Ui with { NextendoInviteDismissed = true } });
}

/// <summary>
/// The card of "Sign in with Nextendo" while the launcher waits for the browser: a spinner, what the launcher says
/// (<see cref="NextendoView.Note"/>), "Create a Nextendo Account" for who has none, and Cancel (also B, or a tap
/// outside).
/// </summary>
public sealed class NextendoSignInModal : ModalViewModel
{
    private readonly ILauncher _launcher;

    public NextendoSignInModal(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _launcher = shell.Launcher;
        Title = shell.S["nextendo.signin"];
        Body = Mkwmp.Core.Strings.NextendoLinkWaiting;
        ShowSpinner = true;
        Actions.Add(new ModalAction(shell.S["nextendo.create"], shell.CreateNextendoAccount, ButtonKind.Outline));
        Bottom = new ModalAction(shell.S["common.cancel"], shell.Launcher.CancelNextendoLink);
        Dismissed += (_, _) => shell.Launcher.CancelNextendoLink();
    }

    public override void OnStateChanged()
    {
        if (_launcher.Nextendo is { Linking: true, Note: { Length: > 0 } note })
        {
            Body = note;
        }
    }
}
