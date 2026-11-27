// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings › Online, and Settings › Profile's local name. On top where PLAY goes online: Nextendo Network (the
/// account, or the ways to sign in: SettingsViewModel.Nextendo.cs), then "Alternative: MKWMP Servers" with its
/// switch. Only with that switch on is the rest here an MKWMP server's: offline, "Online is off" and Connect to a
/// Server (SetupViewModel's connect pages: server, register, that server's consent, PIN); online, the server and its
/// status, the profile on it with its PIN (masked), Disconnect, the consent, and deleting the profile there.
/// </summary>
public sealed partial class SettingsViewModel
{
    private string? _localName;

    private void InitOnline()
    {
        ConnectCommand = new RelayCommand(() => Shell.Navigate(SetupViewModel.Connect(Shell)), () => !Busy);
        Servers = new ServerPicker(Shell, server =>
        {
            if (!Busy)
            {
                Shell.Navigate(SetupViewModel.Connect(Shell, server.Address)); // asked at once; then sign in, consent, PIN
            }
        });
        Servers.HintsChanged += (_, _) => RefreshHints(); // a row has the focus: Y hides it
        HiddenServersCommand = new RelayCommand(() => Shell.Navigate(new HiddenServersViewModel(Shell)), () => HasHiddenServers);
        PrivacyCommand = new RelayCommand(() => Shell.Navigate(SetupViewModel.PrivacyPolicy(Shell)));
        DisconnectCommand = RelayCommand.Async(DisconnectAsync, () => !Busy && OnlineEnabled);
        ReportCommand = new RelayCommand(() => Shell.Navigate(new ReportViewModel(Shell)), () => ReportViewModel.Allowed(Launcher));
        OnlineCommand = new RelayCommand(() => At(NextendoFirst ? NextendoCategory : Routes.OnlineCategory));
    }

    /// <summary>Settings › Profile's row to where the player goes online: "Nextendo", or "Online" for an install on MKWMP servers.</summary>
    public string ProfileOnlineLabel => S[NextendoFirst ? "nextendo.title" : "settings.online"];

    public string ProfileOnlineCaption => S[NextendoFirst ? "nextendo.about" : "settings.profile.online_caption"];

    public bool IsOnlinePane => _category.Key == Routes.OnlineCategory;

    /// <summary>Offline only until MKWMP is updated (a required update failed and the player went on): no server list, no connecting; the way back is the update.</summary>
    public bool UpdateOffline => Launcher.OnlineBlockedByUpdate;

    /// <summary>"Online is off" with its server list: no server is set (and it is not <see cref="UpdateOffline"/>).</summary>
    public bool ShowOnlineOff => OnlineLocked && !UpdateOffline && !NextendoFirst;

    /// <summary>The MKWMP parts of the pane (the server list's source, hidden servers): only while MKWMP servers are chosen.</summary>
    public bool ShowMkwmpOnline => !NextendoFirst;

    /// <summary>The servers to choose from while offline (the beholder's list, nearest first).</summary>
    public ServerPicker Servers { get; private set; } = null!;

    /// <summary>The list is read and measured only while it can be seen: the Online pane, offline.</summary>
    private void FollowServers(bool visible)
    {
        if (Servers is null)
        {
            return; // a category chosen while the page is still being made
        }
        if (visible && IsOnlinePane && ShowOnlineOff)
        {
            Servers.Start();
        }
        else
        {
            Servers.Stop();
        }
    }

    // ── hidden servers: Y on a row of the list hides it; "Hidden servers (n)" shows them again ──

    /// <summary>The list is on screen (the Online pane, offline) and one of its rows has the focus: "Y Hide" instead of "Y Friends".</summary>
    private IReadOnlyList<Hint>? ServerHintsNow => IsOnlinePane && ShowOnlineOff && Servers.Hint is { } hide
        ? [hide, .. Shell.DefaultHints().Where(h => h.Button != PadButton.Y)]
        : null;

    private bool HideServer(PadButton button) => IsOnlinePane && ShowOnlineOff && Servers.OnButton(button);

    public bool HasHiddenServers => Launcher.Servers.Hidden.Count > 0;

    /// <summary>"Hidden servers (2)".</summary>
    public string HiddenServersLabel => S.Format("servers.hidden", ("n", Launcher.Servers.Hidden.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)));

    /// <summary>The page that lists them, each with "Show again".</summary>
    public ICommand HiddenServersCommand { get; private set; } = null!;

    /// <summary>The privacy page (the welcome flow's own, alone): the policy of the server in use and its acceptance.</summary>
    public ICommand PrivacyCommand { get; private set; } = null!;

    /// <summary>
    /// The server row's small line: "ONLINE · 12/32 PLAYERS · 3 in queue · its name", and under it what the server
    /// said when something is wrong. (The seats are in the status itself: no second line repeats them.)
    /// </summary>
    public string ServerSummary
    {
        get
        {
            var server = Launcher.Server;
            var parts = new List<string> { ServerStatus };
            if (server.State == ServerState.Online && server.Queued > 0)
            {
                parts.Add(S.Format("servers.queued", ("n", server.Queued.ToString(System.Globalization.CultureInfo.CurrentCulture))));
            }
            if (!string.IsNullOrWhiteSpace(server.Name) && !string.Equals(server.Name, ServerShort, StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(server.Name);
            }
            var line = string.Join(" · ", parts);
            return string.IsNullOrWhiteSpace(server.Message) ? line : line + "\n" + server.Message;
        }
    }

    /// <summary>Settings › Online › Report Abuse (ReportViewModel): for a registered player who confirmed their age.</summary>
    public ICommand ReportCommand { get; private set; } = null!;

    /// <summary>Under the row: why it is closed, the report that waits, else what a report is.</summary>
    public string ReportCaption => !ReportViewModel.Allowed(Launcher) ? S["settings.report.gate"]
        : ReportViewModel.WaitingText(Launcher, S) is { Length: > 0 } waiting ? waiting
        : S["settings.report.caption"];

    public string TypeAddressLabel => Servers.HasList ? S["settings.online.type"] : S["settings.online.connect"];

    /// <summary>Signed in, and the server holds no consent for its current policy.</summary>
    public bool ConsentNeeded => Registered && !Launcher.Settings.AdultConsent;

    /// <summary>The Privacy Policy row's mark: this server holds the profile's consent.</summary>
    public bool PrivacyAccepted => PrivacyViewModel.IsAccepted(Launcher);

    /// <summary>Under the row: what it opens; or that this server waits for the consent (given on that page).</summary>
    public string PrivacyCaption => S[ConsentNeeded ? "settings.privacy.review_caption" : "settings.privacy.policy_caption"];

    // ── Online ──

    /// <summary>The server in use, without its scheme ("mkw.example.net").</summary>
    public string ServerShort => ProfileForm.HostOf(Launcher.Settings.Server);

    /// <summary>The server asked for last (to connect again in one step), when offline.</summary>
    public string LastServer => Launcher.SavedServers.Count > 0 ? ProfileForm.HostOf(Launcher.SavedServers[^1].Address) : "";

    public string ConnectCaption => LastServer.Length > 0 ? S.Format("settings.online.connect_again", ("server", LastServer)) : S["settings.online.connect_caption"];

    /// <summary>Connected and registered: the profile card with its PIN.</summary>
    public bool Registered => OnlineEnabled && Profile.SignedIn;

    /// <summary>Connected, no profile on that server yet (or another gamertag is being entered): the register form.</summary>
    public bool NeedsProfile => OnlineEnabled && Profile.ShowEntry;

    /// <summary>The consent was taken back while connected: nothing reaches the server until it is given again.</summary>
    public bool ConsentMissing => OnlineEnabled && !Launcher.Settings.AdultConsent;

    public ICommand ConnectCommand { get; private set; } = null!;

    public ICommand DisconnectCommand { get; private set; } = null!;

    /// <summary>Settings › Online, from Settings › Profile.</summary>
    public ICommand OnlineCommand { get; private set; } = null!;

    /// <summary>Disconnect = no server: presence ends, online is locked again; the name stays and the PIN is kept for that server.</summary>
    private async Task DisconnectAsync()
    {
        var server = ServerShort;
        if (await Shell.ConfirmAsync(
                S["settings.online.disconnect_title"],
                S.Format("settings.online.disconnect_body", ("server", server)),
                S["settings.online.disconnect"]).ConfigureAwait(true))
        {
            Profile.PinRevealed = false;
            await Shell.UpdateSettingsAsync(s => s with { Server = "" }).ConfigureAwait(true);
        }
    }

    // ── Profile: the local name ──

    /// <summary>
    /// The player's name on this device. Free to change while offline; with a profile on a server it
    /// is that gamertag, changed in Online (a PIN belongs to one name).
    /// </summary>
    public string LocalName
    {
        get => _localName ?? Launcher.Settings.Name;
        set
        {
            var clean = new string([.. (value ?? "").Where(c => !char.IsWhiteSpace(c)).Take(16)]);
            _localName = clean;
            if (Links.ValidName(clean) && CanEditLocalName && clean != Launcher.Settings.Name)
            {
                Save(s => s with { Name = clean });
            }
            Raise();
            Raise(nameof(LocalNameNote));
            Raise(nameof(LocalNameIsError));
        }
    }

    public bool CanEditLocalName => !Busy && !Registered;

    /// <summary>The name box and its rule: not while PLAY goes to Nextendo Network, where the name is the account's username.</summary>
    public bool ShowLocalNameBox => CanEditLocalName && !NextendoMode;

    public bool ShowLocalNameSection => !NextendoMode;

    public bool LocalNameIsError => LocalName.Length > 0 && !Links.ValidName(LocalName);

    public string LocalNameNote => NextendoMode ? S["settings.profile.nextendo_name"]
        : LocalNameIsError ? CoreStrings.NameRule
        : Registered ? S.Format("settings.profile.local_registered", ("server", ServerShort))
        : OnlineEnabled ? S["settings.profile.local_online"]
        : S["settings.profile.local_note"];

    /// <summary>Under the name's box: its rule; for an install on MKWMP servers also that it is the name registered there.</summary>
    public string LocalNameCaption => S[NextendoFirst ? "setup.name.rule" : "settings.profile.local_caption"];

    /// <summary>The profile card's name: on Nextendo Network the account's username (the name other players see in races), else the name on this device.</summary>
    public string LocalNameShown => NextendoMode && Nextendo.Username.Length > 0 ? Nextendo.Username
        : Launcher.Settings.Name.Length > 0 ? Launcher.Settings.Name : S["settings.profile.local_none"];

    /// <summary>The profile card's picture: the Nextendo account's own while PLAY goes there; null, the initials.</summary>
    public string? ProfilePicture => NextendoMode ? Launcher.NextendoOwnAvatar.Path : null;

    public string ProfileColor => NextendoMode ? Launcher.NextendoOwnAvatar.Color : "";

    // ── the PIN's button (X) while its card is on screen ──

    private bool PinOnScreen => IsOnlinePane && Registered;

    private Hint? PinHintNow => PinOnScreen ? PinHint.For(S, Profile.PinRevealed) : null;

    private bool TogglePin(PadButton button)
    {
        if (button != PadButton.X || !PinOnScreen)
        {
            return false;
        }
        Profile.PinRevealed = !Profile.PinRevealed;
        RefreshHints();
        return true;
    }
}
