// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings › Nextendo (Views/Settings/NextendoPane.xaml), and the Nextendo part of Settings › Online. Not signed
/// in: what it is, "Sign in with Nextendo" (the sign-in page in the browser; the launcher waits for it) and "Create a
/// Nextendo Account". Signed in: the account, its friend code, how it stands, the choice of network ("Play on") and
/// Unlink. The link stays on this PC (docs/contracts/NEXTENDO_API.md).
/// <para>
/// Nextendo Network is where PLAY goes online by default (<see cref="NextendoFirst"/>). The alternative, MKWMP
/// servers, is Settings › Online's switch (<see cref="UseMkwmp"/>): only with it on does that pane show a server
/// list, a profile and its PIN, and only then is anything sent to an MKWMP server (after that server's consent).
/// </para>
/// </summary>
public sealed partial class SettingsViewModel
{
    public const string NextendoCategory = "nextendo";

    private void InitNextendo()
    {
        // the pane waits itself (a spinner and Cancel in place of the two rows): no card over it
        LinkNextendoCommand = new RelayCommand(
            () => RelayCommand.Async(() => Launcher.LinkNextendoAsync(page => Links.Open(page))).Execute(null),
            () => !Nextendo.Linking && !(Busy && NextendoFirst));
        CancelNextendoLinkCommand = new RelayCommand(Launcher.CancelNextendoLink, () => Nextendo.Linking);
        CreateNextendoCommand = new RelayCommand(Shell.CreateNextendoAccount);
        UnlinkNextendoCommand = RelayCommand.Async(UnlinkNextendoAsync, () => Nextendo.Linked && !(Busy && NextendoMode));
        PlayOnCommand = RelayCommand.Async(Shell.PickPlayOnAsync, () => !Busy);
        NextendoPaneCommand = new RelayCommand(() => At(NextendoCategory));
        BlockedPlayersCommand = new RelayCommand(() => Shell.Navigate(new BlockedPlayersViewModel(Shell)));
    }

    // ── friends on Nextendo: who sees the player, and who is blocked ──

    /// <summary>PLAY goes to Nextendo Network with this account: its friends are in the friends panel, and these two rows are shown.</summary>
    public bool ShowNextendoFriends => Launcher.NextendoFriends.Enabled;

    /// <summary>"Show Me Online to Friends": while the launcher runs, Nextendo friends see the player online (client.toml, <c>[nextendo] show_online</c>).</summary>
    public bool NextendoShowOnline
    {
        get => Launcher.Settings.NextendoShowOnline;
        set => Save(s => s with { NextendoShowOnline = value });
    }

    /// <summary>The players blocked on Nextendo, each with Unblock (a sub-page).</summary>
    public ICommand BlockedPlayersCommand { get; private set; } = null!;

    /// <summary>How many are blocked ("" for none, or not known yet).</summary>
    public string BlockedPlayersCount => Launcher.NextendoFriends.Blocked.Count is > 0 and var n ? n.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

    private NextendoView Nextendo => Launcher.Nextendo;

    public bool IsNextendoPane => _category.Key == NextendoCategory;

    public bool NextendoLinked => Nextendo.Linked;

    public bool NextendoUnlinked => !Nextendo.Linked;

    /// <summary>PLAY goes to Nextendo Network now (signed in, chosen, a run that may go online).</summary>
    public bool NextendoMode => Nextendo.Active;

    /// <summary>Nextendo Network is where PLAY goes online, signed in or not: Settings › Online shows it instead of a server, and what is an MKWMP server's is locked.</summary>
    public bool NextendoFirst => Nextendo.Preferred;

    /// <summary>Settings › Online's Nextendo part (the account, or the two ways in): not in a run that is offline only until MKWMP is updated (its one card says so).</summary>
    public bool ShowNextendoOnline => NextendoFirst && !UpdateOffline;

    /// <summary>In it: the account PLAY goes online with.</summary>
    public bool ShowNextendoAccount => ShowNextendoOnline && Nextendo.Linked;

    /// <summary>In it: nobody is signed in (what signing in does; the rows that sign in or create an account, or Cancel while the browser is awaited).</summary>
    public bool ShowNextendoSignIn => ShowNextendoOnline && !Nextendo.Linked;

    /// <summary>The row that leads to Nextendo while MKWMP servers are chosen: "Nextendo Network", with who is signed in or "Sign in with Nextendo".</summary>
    public bool ShowNextendoRow => !NextendoFirst && !UpdateOffline;

    public string NextendoRowValue => Nextendo.Linked ? Nextendo.Username : S["nextendo.signin"];

    // ── the alternative: MKWMP servers ──

    /// <summary>"Use MKWMP Servers Instead" (Settings › Online): shown unless this run is offline only.</summary>
    public bool ShowUseMkwmp => !UpdateOffline;

    /// <summary>
    /// The switch itself: on, PLAY goes to an MKWMP server and the pane shows its list, the profile and its privacy
    /// (nothing is sent to a server before one is chosen and its consent given); off, Nextendo Network. Not while PLAY runs.
    /// </summary>
    public bool UseMkwmp
    {
        get => !NextendoFirst;
        set
        {
            if (value != UseMkwmp)
            {
                RelayCommand.Async(() => Shell.SetPlayOnAsync(value ? PlayNetwork.Mkwmp : PlayNetwork.Nextendo)).Execute(null);
            }
        }
    }

    /// <summary>Under the switch: what MKWMP servers are; while PLAY runs, why it cannot be changed.</summary>
    public string UseMkwmpCaption => Busy ? S["playon.busy"] : S["settings.online.use_mkwmp_caption"];

    public bool NextendoLinking => Nextendo.Linking;

    /// <summary>Not linked and not waiting for the browser: the two ways in.</summary>
    public bool NextendoCanLink => !Nextendo.Linked && !Nextendo.Linking;

    public string NextendoName => Nextendo.Username;

    /// <summary>The account's own profile picture on Nextendo (a file), while PLAY goes there; null: the disc with its initials.</summary>
    public string? NextendoPicture => Launcher.NextendoOwnAvatar.Path;

    public string NextendoFriendCode => Nextendo.FriendCode;

    public bool HasNextendoFriendCode => Nextendo.FriendCode.Length > 0;

    /// <summary>How the account stands on Nextendo; "Linked" until it was asked (it is asked while PLAY goes there).</summary>
    public string NextendoStatus => Nextendo.Status.Length > 0 ? Nextendo.Status : S["nextendo.status.linked"];

    /// <summary>good / warn / bad / busy / muted, for the light beside <see cref="NextendoStatus"/>.</summary>
    public string NextendoTone => Nextendo.Checking ? "busy" : Nextendo.Status.Length > 0 ? Nextendo.Tone : "good";

    /// <summary>The outcome of the last link, unlink or switch.</summary>
    public string NextendoNote => Nextendo.Note ?? "";

    public bool NextendoNoteIsError => Nextendo.NoteIsError;

    /// <summary>The note's own line: not while the first link waits for the browser (it is under Cancel then).</summary>
    public bool ShowNextendoNote => NextendoNote.Length > 0 && !(Nextendo.Linking && !Nextendo.Linked);

    /// <summary>"PLAY signs you in on Nextendo as bia. No MKWMP server is contacted."</summary>
    public string NextendoOnlineText => S.Format("settings.online.nextendo_text", ("name", Nextendo.Username));

    // ── the choice of network ──

    public string PlayOnName => Shell.PlayOnName;

    /// <summary>Under the row: what the choice does; while PLAY runs, why it cannot be changed.</summary>
    public string PlayOnCaption => Busy ? S["playon.busy"] : S["playon.body"];

    public ICommand PlayOnCommand { get; private set; } = null!;

    public ICommand LinkNextendoCommand { get; private set; } = null!;

    public ICommand CancelNextendoLinkCommand { get; private set; } = null!;

    /// <summary>nextendo.network's sign-up page, in the browser.</summary>
    public ICommand CreateNextendoCommand { get; private set; } = null!;

    public ICommand UnlinkNextendoCommand { get; private set; } = null!;

    /// <summary>Settings › Nextendo, from Settings › Online.</summary>
    public ICommand NextendoPaneCommand { get; private set; } = null!;

    private async Task UnlinkNextendoAsync()
    {
        if (await Shell.ConfirmAsync(S["nextendo.unlink_title"], S.Format("nextendo.unlink_body", ("name", Nextendo.Username)), S["nextendo.unlink"]).ConfigureAwait(true))
        {
            await Launcher.UnlinkNextendoAsync().ConfigureAwait(true);
        }
    }

    /// <summary>The pane is on screen (or Online's, on Nextendo): how the account stands is asked again.</summary>
    private void EnterNextendo()
    {
        if (Nextendo.Linked && (IsNextendoPane || (IsOnlinePane && NextendoFirst)))
        {
            RelayCommand.Async(Launcher.RefreshNextendoAsync).Execute(null);
        }
        if (IsNextendoPane && ShowNextendoFriends)
        {
            RelayCommand.Async(Launcher.RefreshNextendoFriendsAsync).Execute(null); // how many are blocked
        }
    }
}
