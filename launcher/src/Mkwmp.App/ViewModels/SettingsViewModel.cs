// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.IO;
using System.Windows.Input;
using Mkwmp.App.Controls;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings: categories at the left, the chosen one's rows at the right. Everything the Rust
/// window's SETTINGS modal and PLAYER card could change is here (rust/client/src/gui.rs is the
/// reference for every rule and text). A change is saved at once (<c>SaveSettingsAsync</c>);
/// what the old window disabled while playing is disabled while <c>Session.Busy</c>.
/// Voice &amp; Camera's pane, and the lock of the categories that need a server, are in SettingsViewModel.Voice.cs;
/// Settings › Online (with its Report Abuse row, a page of its own: ReportViewModel) in SettingsViewModel.Online.cs.
/// </summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private CategoryEntry _category;
    private bool _savingLogs;

    public SettingsViewModel(ShellViewModel shell)
        : base(shell)
    {
        Categories =
        [
            new CategoryEntry("profile", S["settings.profile"]),
            new CategoryEntry(NextendoCategory, S["settings.nextendo"]), // the Nextendo account PLAY goes online with: sign in, create one, how it stands (SettingsViewModel.Nextendo.cs)
            new CategoryEntry(Routes.OnlineCategory, S["settings.online"]), // where PLAY goes online: Nextendo, and the alternative, an MKWMP server with its profile and privacy (SettingsViewModel.Online.cs)
            new CategoryEntry("game", S["settings.game"]),
            new CategoryEntry("voice", S["settings.voice"]),
            new CategoryEntry("notifications", S["settings.notifications"]),
            new CategoryEntry("appearance", S["settings.appearance"], SeparatorBefore: true),
            new CategoryEntry("updates", S["settings.updates"]),
            new CategoryEntry("support", S["settings.support"]),
            new CategoryEntry("about", S["settings.about"]),
        ];
        _category = Categories[0];
        Profile = new ProfileForm(shell);
        Game = new GameForm(shell);
        Game.Refresh();
        Mic = new MicTester(Launcher);

        SeatCommand = RelayCommand.Async(PickSeatAsync, () => !Busy);
        TestToastCommand = new RelayCommand(Launcher.ShowTestToast);
        GuideCommand = new RelayCommand(() => Links.Open(Shell, CoreStrings.GuideUrl));
        IssuesCommand = new RelayCommand(() => Links.Open(Shell, Links.Issues));
        RepoCommand = new RelayCommand(() => Links.Open(Shell, Links.Repo));
        RecompRepoCommand = new RelayCommand(() => Links.Open(Shell, RecompRepoUrl));
        RecompCreatorCommand = new RelayCommand(() => Links.Open(Shell, RecompCreatorUrl));
        RecompLicenseCommand = new RelayCommand(() => Links.Open(Shell, RecompLicenseUrl));
        RecompSourceCommand = new RelayCommand(() => Links.Open(Shell, RecompSourceUrl));
        WhatsNewCommand = new RelayCommand(() => Shell.Navigate(new WhatsNewViewModel(Shell)));
        ThemeCommand = RelayCommand.Async(() => Shell.SetThemeAsync(Shell.IsDark ? "light" : "dark"));
        ContrastCommand = RelayCommand.Async(PickContrastAsync);
        LanguageCommand = RelayCommand.Async(Shell.PickLanguageAsync);
        CheckUpdateCommand = RelayCommand.Async(() => Launcher.CheckForUpdatesAsync(), () => Launcher.Update.State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Restarting));
        UpdateCommand = new RelayCommand(Shell.ShowUpdateModal);
        UpdateChannelCommand = RelayCommand.Async(PickUpdateChannelAsync, () => !UpdateBusy);
        StableReturnCommand = RelayCommand.Async(OfferStableReturnAsync, () => !UpdateBusy);
        SaveLogsCommand = RelayCommand.Async(SaveLogsAsync, () => !_savingLogs);
        FoundBugCommand = RelayCommand.Async(Shell.ReportBugAsync);
        InitVoice();
        InitGameSettings();
        InitOnline();
        InitNextendo();
        Profile.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileForm.PinRevealed))
            {
                RefreshHints(); // the eye was pressed: X now says the other thing
            }
        };
    }

    /// <summary>The fixed categories, and one per page of the game's own settings (SettingsViewModel.GameSettings.cs).</summary>
    public System.Collections.ObjectModel.ObservableCollection<CategoryEntry> Categories { get; }

    public CategoryEntry Category
    {
        get => _category;
        set
        {
            if (value is not null && Set(ref _category, value))
            {
                FollowServers(true);
                if (value.Key != "voice")
                {
                    Mic.Stop();
                }
                else
                {
                    EnterVoice(); // the devices are listed only once Voice & Camera is opened
                }
                EnterNextendo();
                SyncGameRows();
                RaiseAll();
                RefreshHints();
            }
        }
    }

    /// <summary>The pane to show ("gs" for every page of the game's own settings; "locked" for one that needs a server while none is set).</summary>
    public string CategoryKey => IsGameSettings ? "gs" : CategoryLocked ? "locked" : _category.Key;

    /// <summary>Opens on a category (<c>Shell.Navigate(new SettingsViewModel(Shell).At("voice"))</c>).</summary>
    public SettingsViewModel At(string key)
    {
        if (Categories.FirstOrDefault(c => c.Key == key) is { } entry)
        {
            Category = entry;
        }
        return this;
    }

    public ProfileForm Profile { get; }

    public GameForm Game { get; }

    /// <summary>Playing (or starting): the Rust window disabled the gamertag, the server and the game folder then.</summary>
    public bool Busy => Launcher.Session.Busy;

    public bool NotBusy => !Busy;

    public string BusyNote => Busy ? S["settings.busy"] : "";

    private void Save(Func<LauncherSettings, LauncherSettings> change) =>
        RelayCommand.Async(() => Shell.UpdateSettingsAsync(change)).Execute(null);

    public override void OnEnter()
    {
        Profile.Refresh();
        Game.Refresh();
        if (_category.Key == "voice")
        {
            EnterVoice();
        }
        FollowServers(true);
        EnterNextendo();
    }

    /// <summary>The window lost the focus, or the overlay went back to the game: the microphone test stops.</summary>
    public override void OnWindowDeactivated()
    {
        Mic.Stop();
        Raise(nameof(MicTesting));
    }

    public override void OnLeave()
    {
        FollowServers(false);
        LeaveVoice();
        FlushGameRows();
    }

    public override void OnStateChanged()
    {
        if (_localName == Launcher.Settings.Name)
        {
            _localName = null; // saved: the box follows the settings again
        }
        Profile.Refresh();
        Game.Refresh();
        Servers.Sync();
        FollowServers(true);
        RefreshVoice();
        SyncGameCategories();
        base.OnStateChanged();
        RefreshHints();
        CommandManager.InvalidateRequerySuggested();
    }

    // ── game: advanced ──

    public string SeatName => Launcher.Settings.Seat == SeatMode.Online ? S["settings.game.seat_online"] : S["settings.game.seat_start"];

    public ICommand SeatCommand { get; }

    private async Task PickSeatAsync()
    {
        var picked = await Choice.AskAsync(Shell, S["settings.game.seat"], S["settings.game.seat_body"],
        [
            new ChoiceOption("start", S["settings.game.seat_start"], S["settings.game.seat_start_caption"]),
            new ChoiceOption("online", S["settings.game.seat_online"], S["settings.game.seat_online_caption"]),
        ], Launcher.Settings.Seat == SeatMode.Online ? "online" : "start").ConfigureAwait(true);
        if (picked is not null)
        {
            await Shell.UpdateSettingsAsync(s => s with { Seat = picked == "online" ? SeatMode.Online : SeatMode.Start }).ConfigureAwait(true);
        }
    }

    /// <summary>A command that starts the game instead of mkw.exe ("" = the launcher starts it itself).</summary>
    public string LaunchCommandText
    {
        get => Launcher.Settings.LaunchCommand;
        set => Save(s => s with { LaunchCommand = (value ?? "").Trim() });
    }

    /// <summary>The port of the local API the game calls (1..65535).</summary>
    public string LocalPortText
    {
        get => Launcher.Settings.LocalPort.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535)
            {
                Save(s => s with { LocalPort = port });
            }
            else
            {
                Raise(); // not a port: the box shows the saved one again
            }
        }
    }

    // ── support ──

    /// <summary>The mod's deep trace at the next PLAYs (asked again before each one): Settings › Support › Bug reports.</summary>
    public bool DebugMode
    {
        get => Launcher.Settings.DebugMode;
        set => Save(s => s with { DebugMode = value });
    }

    // ── notifications and background ──

    public bool NotifyOnline
    {
        get => Launcher.Settings.Background.NotifyOnline;
        set => Save(s => s with { Background = s.Background with { NotifyOnline = value } });
    }

    public bool NotifyPlaying
    {
        get => Launcher.Settings.Background.NotifyPlaying;
        set => Save(s => s with { Background = s.Background with { NotifyPlaying = value } });
    }

    public bool NotifyRequests
    {
        get => Launcher.Settings.Background.NotifyRequests;
        set => Save(s => s with { Background = s.Background with { NotifyRequests = value } });
    }

    /// <summary>No system tray in Steam Deck Game Mode: closing quits there.</summary>
#pragma warning disable CA1822 // XAML binds to instance properties
    public bool HasTray => !Links.GameMode;
#pragma warning restore CA1822

    public bool CloseToTray
    {
        get => Launcher.Settings.Background.CloseToTray;
        set => Save(s => s with { Background = s.Background with { CloseToTray = value } });
    }

    /// <summary>Under the two switches: no tray here (Game Mode), and that the background takes no seat. A seat is an MKWMP server's: on Nextendo Network that line is left out.</summary>
    public string TrayCaption => string.Join("\n", new[] { HasTray ? "" : S["settings.background.no_tray"], NextendoFirst ? "" : S["settings.background.caption"] }.Where(line => line.Length > 0));

    /// <summary>Under "Keep Running in the System Tray": "so friends see me online", which is an MKWMP server's (nothing on Nextendo Network).</summary>
    public string TrayRowCaption => NextendoFirst && !ShowNextendoFriends ? "" : S["settings.background.tray_caption"]; // true of Nextendo friends too, once signed in

    /// <summary>Under Wine / Proton the registry's Run key starts nothing: Steam starts the launcher there.</summary>
#pragma warning disable CA1822 // XAML binds to instance properties
    public bool CanStartWithSystem => !Links.IsWine;

    public bool IsWine => Links.IsWine;
#pragma warning restore CA1822

    public bool StartWithSystem
    {
        get => Launcher.Settings.Background.StartWithSystem;
        set => Save(s => s with { Background = s.Background with { StartWithSystem = value } });
    }

    public ICommand TestToastCommand { get; }

    // ── server (its address is chosen in the connect flow: SettingsViewModel.Online.cs) ──

    public string ServerStatus => CoreStrings.ServerPill(Launcher.Server);

    public string ServerTone => Launcher.Server.State switch
    {
        ServerState.Online => "good",
        ServerState.Offline => "bad",
        ServerState.Checking => "busy",
        _ => "muted",
    };

    // ── appearance ──

    public string ThemeName => Shell.IsDark ? S["settings.theme_dark"] : S["settings.theme_light"];

    public ICommand ThemeCommand { get; }

    /// <summary>How far cards, rows and the dock stand from the background (Themes/Palette.cs).</summary>
    public string ContrastName => S["settings.contrast." + Shell.Contrast];

    public ICommand ContrastCommand { get; }

    private async Task PickContrastAsync()
    {
        var options = Themes.Palette.Steps.Select(step => new ChoiceOption(step, S["settings.contrast." + step])).ToList();
        if (await Choice.AskAsync(Shell, S["settings.contrast"], S["settings.contrast.caption"], options, Shell.Contrast).ConfigureAwait(true) is { } picked)
        {
            await Shell.SetContrastAsync(picked).ConfigureAwait(true);
            Raise(nameof(ContrastName));
        }
    }

    public bool Fullscreen
    {
        get => Shell.IsFullscreen;
        set => RelayCommand.Async(() => Shell.SetFullscreenAsync(value)).Execute(null);
    }

    /// <summary>The saved language in its own name, or "System default" (the selector and the switch itself: ShellViewModel.Language.cs).</summary>
    public string LanguageName => Shell.LanguageName;

    public string LanguageCaption => Shell.LanguageSetting.Length == 0
        ? S.Format("settings.language.system_caption", ("name", ShellViewModel.SystemLanguageName))
        : S["settings.language.caption"];

    public ICommand LanguageCommand { get; }

    /// <summary>The language changed: Settings again, on the same category.</summary>
    public override PageViewModel? Recreate() => new SettingsViewModel(Shell).At(_category.Key);

    // ── updates ──

    /// <summary>"1.5.1-rc.2 · Alpha": the version and the channel it follows.</summary>
    public string VersionText => Shell.VersionAndChannel;

    // The update channel (Mkwmp.Core's UpdateChannel; launcher/docs/UPDATES.md, "Channels"): stable, or alpha for the
    // test versions (-rc) too. Changing it looks for an update at once; leaving alpha on a test version offers the
    // way back to the newest stable release when no stable release has caught up yet.

    public string UpdateChannelName => S[Launcher.Settings.UpdateChannel == UpdateChannel.Alpha ? "settings.updates.channel.alpha" : "settings.updates.channel.stable"];

    public ICommand UpdateChannelCommand { get; }

    private async Task PickUpdateChannelAsync()
    {
        var now = Launcher.Settings.UpdateChannel;
        var picked = await Choice.AskAsync(Shell, S["settings.updates.channel"], S["settings.updates.channel.caption"],
        [
            new ChoiceOption(UpdateChannels.StableName, S["settings.updates.channel.stable"], S["settings.updates.channel.stable_caption"]),
            new ChoiceOption(UpdateChannels.AlphaName, S["settings.updates.channel.alpha"], S["settings.updates.channel.alpha_caption"]),
        ], UpdateChannels.Name(now)).ConfigureAwait(true);
        if (picked is null)
        {
            return;
        }
        var channel = UpdateChannels.Parse(picked);
        if (channel == now)
        {
            return;
        }
        // Saved, and asked for at once: alpha's newest test version is then installed like any update.
        await Launcher.SetUpdateChannelAsync(channel).ConfigureAwait(true);
        RaiseAll();
        if (channel == UpdateChannel.Stable && Launcher.Update.StableReturn is not null)
        {
            await OfferStableReturnAsync().ConfigureAwait(true);
        }
    }

    /// <summary>A test version runs on the stable channel and no stable release has caught up: the newest stable one can be put back.</summary>
    public bool ShowStableReturn => Launcher.Update.StableReturn is not null && !UpdateBusy;

    public string StableReturnLabel => S.Format("settings.updates.return", ("version", Launcher.Update.StableReturn ?? ""));

    public string StableReturnCaption => S.Format("settings.updates.return_caption", ("version", Launcher.Update.StableReturn ?? ""));

    public ICommand StableReturnCommand { get; }

    /// <summary>
    /// "Return to Stable": asked, never assumed. Yes installs the older stable release (the core checks every
    /// condition again, and so does the installer); no keeps this version, which from then on gets stable updates only.
    /// </summary>
    private async Task OfferStableReturnAsync()
    {
        if (Launcher.Update.StableReturn is not { } version)
        {
            return;
        }
        var body = S.Format("settings.updates.return_body", ("current", Launcher.Version), ("version", version));
        if (!await Shell.ConfirmAsync(S["settings.updates.return_title"], body, S["settings.updates.return_yes"], S["settings.updates.return_no"]).ConfigureAwait(true))
        {
            return;
        }
        var progress = Shell.ShowProgress(S["settings.updates.returning"], S["update.installing_body"]);
        try
        {
            await Launcher.ReturnToStableAsync(CancellationToken.None).ConfigureAwait(true);
        }
        finally
        {
            progress.Close(); // still here: it was not installed (else the launcher has closed)
        }
        if (Launcher.Update.State == UpdateState.Failed)
        {
            await Shell.MessageAsync(S["update.failed"], Launcher.Update.Status).ConfigureAwait(true);
        }
    }

    public string UpdateStatus => Launcher.Update.Status;

    public bool UpdateBusy => Launcher.Update.State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Restarting;

    public bool UpdateAvailable => Launcher.Update.State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall or UpdateState.Failed && Launcher.Update.AvailableVersion is not null;

    public string UpdateInstallLabel => S.Format(Launcher.Update.State == UpdateState.ReadyToInstall ? "settings.updates.ready" : "settings.updates.install", ("version", Launcher.Update.AvailableVersion ?? ""));

    public string UpdateNotes => Launcher.Update.Notes ?? "";

    public ICommand CheckUpdateCommand { get; }

    public ICommand UpdateCommand { get; }

    // ── support ──

    public ICommand SaveLogsCommand { get; }

    /// <summary>"Found a bug?": the report flow (a zip on the Desktop, the GitHub issue opened with the details).</summary>
    public ICommand FoundBugCommand { get; }

    public ICommand IssuesCommand { get; }

    public ICommand GuideCommand { get; }

    public ICommand RepoCommand { get; }

    /// <summary>Whose work the game's runtime is (About's mkw-recomp rows): the installed runtime's credit, else the pinned build's.</summary>
    private Mkwmp.Core.Targets.RuntimeCredit RecompCredit => Launcher.Runtime.Credit ?? Mkwmp.Core.Targets.MkwRecomp.RecompRuntime.Pinned.Credit;

    public string RecompRepoUrl => RecompCredit.ProjectUrl.AbsoluteUri;

    public string RecompCreatorUrl => RecompCredit.AuthorUrl?.AbsoluteUri ?? RecompRepoUrl;

    /// <summary>The creator's GitHub name, from their page's address (adriadam10).</summary>
    public string RecompCreatorHandle => RecompCredit.AuthorUrl?.AbsolutePath.Trim('/') ?? "";

    public string RecompLicenseUrl => RecompCredit.LicenseUrl?.AbsoluteUri ?? RecompRepoUrl;

    public string RecompLicense => RecompCredit.License;

    /// <summary>The source of the exact build MKWMP installs (adriadam10/mkw-recomp at the pinned tag): MIT and GPL both want it reachable.</summary>
    public string RecompSourceUrl => RecompCredit.SourceUrl.AbsoluteUri;

    public ICommand RecompRepoCommand { get; }

    public ICommand RecompCreatorCommand { get; }

    public ICommand RecompLicenseCommand { get; }

    public ICommand RecompSourceCommand { get; }

    /// <summary>The What's New page (the launcher's changelog), also from Settings › Updates.</summary>
    public ICommand WhatsNewCommand { get; }

    public string StarsText => Launcher.GitHubStars > 0 ? S.Format("settings.support.stars", ("n", Launcher.GitHubStars.ToString("N0", CultureInfo.CurrentCulture))) : "";

    /// <summary>The Rust window's SAVE LOGS FOR A BUG REPORT: a zip without PIN or ticket, then the folder it is in.</summary>
    private async Task SaveLogsAsync()
    {
        _savingLogs = true;
        var progress = Shell.ShowProgress(S["settings.support.saving"], S["settings.support.logs_caption"]);
        string zip;
        try
        {
            zip = await Launcher.SaveBugReportAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or LauncherException)
        {
            progress.Close();
            _savingLogs = false;
            await Shell.MessageAsync(S["settings.support.failed"], e.Message).ConfigureAwait(true);
            return;
        }
        progress.Close();
        _savingLogs = false;
        if (await Shell.ConfirmAsync(CoreStrings.LogsSavedTitle, CoreStrings.Saved(zip) + "\n\n" + S["settings.support.saved_body"], S["settings.support.open_folder"], S["common.close"]).ConfigureAwait(true))
        {
            Links.ShowInFolder(zip);
        }
    }
}
