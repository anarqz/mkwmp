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
/// Party Chat's pane is in SettingsViewModel.Chat.cs.
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
            new CategoryEntry("game", S["settings.game"]),
            new CategoryEntry("chat", S["settings.chat"]),
            new CategoryEntry("notifications", S["settings.notifications"]),
            new CategoryEntry("privacy", S["settings.privacy"]),
            new CategoryEntry("server", S["settings.server"], SeparatorBefore: true),
            new CategoryEntry("appearance", S["settings.appearance"]),
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
        PolicyCommand = new RelayCommand(() => Links.Open(Shell, CoreStrings.PrivacyUrl));
        GuideCommand = new RelayCommand(() => Links.Open(Shell, CoreStrings.GuideUrl));
        IssuesCommand = new RelayCommand(() => Links.Open(Shell, Links.Issues));
        RepoCommand = new RelayCommand(() => Links.Open(Shell, Links.Repo));
        BasedOnCommand = new RelayCommand(() => Links.Open(Shell, Links.BasedOn));
        ThemeCommand = RelayCommand.Async(() => Shell.SetThemeAsync(Shell.IsDark ? "light" : "dark"));
        LanguageCommand = RelayCommand.Async(PickLanguageAsync);
        CheckUpdateCommand = RelayCommand.Async(() => Launcher.CheckForUpdatesAsync(), () => Launcher.Update.State is not (UpdateState.Checking or UpdateState.Downloading or UpdateState.Restarting));
        UpdateCommand = new RelayCommand(Shell.ShowUpdateModal);
        SaveLogsCommand = RelayCommand.Async(SaveLogsAsync, () => !_savingLogs);
        InitChat();
        InitGameSettings();
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
                if (value.Key != "chat")
                {
                    Mic.Stop();
                }
                SyncGameRows();
                RaiseAll();
                RefreshHints();
            }
        }
    }

    /// <summary>The pane to show ("gs" for every page of the game's own settings).</summary>
    public string CategoryKey => IsGameSettings ? "gs" : _category.Key;

    /// <summary>Opens on a category (<c>Shell.Navigate(new SettingsViewModel(Shell).At("chat"))</c>).</summary>
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
        EnterChat();
    }

    public override void OnLeave()
    {
        LeaveChat();
        FlushGameRows();
    }

    public override void OnStateChanged()
    {
        Profile.Refresh();
        Game.Refresh();
        RefreshChat();
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

    public string TrayCaption => HasTray ? S["settings.background.caption"] : S["settings.background.no_tray"] + "\n" + S["settings.background.caption"];

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

    // ── privacy ──

    /// <summary>"I'm 18 or older and I accept the privacy policy": without it nothing reaches the server.</summary>
    public bool Adult
    {
        get => Launcher.Settings.AdultConsent;
        set => RelayCommand.Async(() => Launcher.SetConsentAsync(value)).Execute(null);
    }

    public ICommand PolicyCommand { get; }

    // ── server ──

    public string ServerUrl
    {
        get => Launcher.Settings.Server;
        set
        {
            var url = (value ?? "").Trim();
            if (Busy || url == Launcher.Settings.Server)
            {
                Raise();
                return;
            }
            Save(s => s with { Server = url });
        }
    }

    public string ServerStatus => CoreStrings.ServerPill(Launcher.Server);

    public string ServerTone => Launcher.Server.State switch
    {
        ServerState.Online => "good",
        ServerState.Offline => "bad",
        ServerState.Checking => "busy",
        _ => "muted",
    };

    public string ServerDetail => string.Join("\n", new[] { Launcher.Server.Name, Launcher.Server.Message }.Where(t => !string.IsNullOrWhiteSpace(t)));

    public IReadOnlyList<StatusLine> ServerLines => [new StatusLine(ServerTone, ServerStatus, ServerDetail)];

    // ── appearance ──

    public string ThemeName => Shell.IsDark ? S["settings.theme_dark"] : S["settings.theme_light"];

    public ICommand ThemeCommand { get; }

    public bool Fullscreen
    {
        get => Shell.IsFullscreen;
        set => RelayCommand.Async(() => Shell.SetFullscreenAsync(value)).Execute(null);
    }

    public string LanguageName => Launcher.Settings.Ui.Locale switch
    {
        "en" => "English",
        "pt-BR" => "Português (Brasil)",
        _ => S["settings.automatic"],
    };

    public string LanguageCaption => Launcher.Settings.Ui.Locale is "en" or "pt-BR" && Launcher.Settings.Ui.Locale != S.Locale
        ? S["settings.language.restart"]
        : S["settings.language.caption"];

    public ICommand LanguageCommand { get; }

    private async Task PickLanguageAsync()
    {
        var picked = await Choice.AskAsync(Shell, S["settings.language"], S["settings.language.caption"],
        [
            new ChoiceOption("", S["settings.automatic"], S["settings.language.auto_caption"]),
            new ChoiceOption("en", "English"),
            new ChoiceOption("pt-BR", "Português (Brasil)"),
        ], Launcher.Settings.Ui.Locale is "en" or "pt-BR" ? Launcher.Settings.Ui.Locale : "").ConfigureAwait(true);
        if (picked is not null)
        {
            await Shell.UpdateSettingsAsync(s => s with { Ui = s.Ui with { Locale = picked } }).ConfigureAwait(true);
        }
    }

    // ── updates ──

    public string VersionText => Launcher.Version;

    public string UpdateStatus => Launcher.Update.Status;

    public bool UpdateBusy => Launcher.Update.State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Restarting;

    public bool UpdateAvailable => Launcher.Update.State is UpdateState.Available or UpdateState.Downloading or UpdateState.Failed && Launcher.Update.AvailableVersion is not null;

    public string UpdateInstallLabel => S.Format("settings.updates.install", ("version", Launcher.Update.AvailableVersion ?? ""));

    public string UpdateNotes => Launcher.Update.Notes ?? "";

    public bool AutoUpdate
    {
        get => Launcher.Settings.AutoUpdate;
        set => Save(s => s with { AutoUpdate = value });
    }

    public ICommand CheckUpdateCommand { get; }

    public ICommand UpdateCommand { get; }

    // ── support ──

    public ICommand SaveLogsCommand { get; }

    public ICommand IssuesCommand { get; }

    public ICommand GuideCommand { get; }

    public ICommand RepoCommand { get; }

    public ICommand BasedOnCommand { get; }

    public string StarsText => Launcher.GitHubStars > 0 ? S.Format("settings.support.stars", ("n", Launcher.GitHubStars.ToString(CultureInfo.CurrentCulture))) : "";

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
