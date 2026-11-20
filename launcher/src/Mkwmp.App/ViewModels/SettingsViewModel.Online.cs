// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings › Online (what Profile, Server and Privacy were), and Settings › Profile's local name.
/// Offline: "Online is off" and Connect to a Server, which is the welcome flow's online part
/// (consent, server, register, PIN) as a page of its own. Online: the server and its status, the
/// profile on it with its PIN (masked), Disconnect, the consent, and deleting the profile there.
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
        PrivacyCommand = new RelayCommand(() => Shell.Navigate(new PrivacyViewModel(Shell)));
        ConsentCommand = new RelayCommand(() => Shell.Navigate(SetupViewModel.ConsentOnly(Shell)), () => !Busy);
        DisconnectCommand = RelayCommand.Async(DisconnectAsync, () => !Busy && OnlineEnabled);
        OnlineCommand = new RelayCommand(Shell.ConfigureOnline);
    }

    public bool IsOnlinePane => _category.Key == Routes.OnlineCategory;

    /// <summary>The servers to choose from while offline (the beholder's list, nearest first).</summary>
    public ServerPicker Servers { get; private set; } = null!;

    /// <summary>The list is read and measured only while it can be seen: the Online pane, offline.</summary>
    private void FollowServers(bool visible)
    {
        if (Servers is null)
        {
            return; // a category chosen while the page is still being made
        }
        if (visible && IsOnlinePane && OnlineLocked)
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
    private IReadOnlyList<Hint>? ServerHintsNow => IsOnlinePane && OnlineLocked && Servers.Hint is { } hide
        ? [hide, .. Shell.DefaultHints().Where(h => h.Button != PadButton.Y)]
        : null;

    private bool HideServer(PadButton button) => IsOnlinePane && OnlineLocked && Servers.OnButton(button);

    public bool HasHiddenServers => Launcher.Servers.Hidden.Count > 0;

    /// <summary>"Hidden servers (2)".</summary>
    public string HiddenServersLabel => S.Format("servers.hidden", ("n", Launcher.Servers.Hidden.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)));

    /// <summary>The page that lists them, each with "Show again".</summary>
    public ICommand HiddenServersCommand { get; private set; } = null!;

    /// <summary>The privacy policy of the server in use, rendered in the launcher.</summary>
    public ICommand PrivacyCommand { get; private set; } = null!;

    /// <summary>The consent page, for a profile signed in without it.</summary>
    public ICommand ConsentCommand { get; private set; } = null!;

    /// <summary>"12/32 seats taken · 3 in queue", from the server's live status.</summary>
    public string SeatsText => Launcher.Server.State == ServerState.Online
        ? S.Format("settings.online.seats", ("seated", Launcher.Server.Seated.ToString(System.Globalization.CultureInfo.CurrentCulture)), ("total", Launcher.Server.SeatsTotal.ToString(System.Globalization.CultureInfo.CurrentCulture)))
            + (Launcher.Server.Queued > 0 ? " · " + S.Format("servers.queued", ("n", Launcher.Server.Queued.ToString(System.Globalization.CultureInfo.CurrentCulture))) : "")
        : "";

    public string TypeAddressLabel => Servers.HasList ? S["settings.online.type"] : S["settings.online.connect"];

    /// <summary>Signed in and consented: the consent can be withdrawn.</summary>
    public bool ConsentGiven => Registered && Launcher.Settings.AdultConsent;

    /// <summary>Signed in, and the server holds no consent for its current policy.</summary>
    public bool ConsentNeeded => Registered && !Launcher.Settings.AdultConsent;

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

    public string DisconnectCaption => S.Format("settings.online.disconnect_caption", ("server", ServerShort));

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

    public bool LocalNameIsError => LocalName.Length > 0 && !Links.ValidName(LocalName);

    public string LocalNameNote => LocalNameIsError ? CoreStrings.NameRule
        : Registered ? S.Format("settings.profile.local_registered", ("server", ServerShort))
        : OnlineEnabled ? S["settings.profile.local_online"]
        : S["settings.profile.local_note"];

    public string LocalNameShown => Launcher.Settings.Name.Length > 0 ? Launcher.Settings.Name : S["settings.profile.local_none"];

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
