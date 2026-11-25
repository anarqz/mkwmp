// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;
using Mkwmp.Core.Targets;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>A card of the home carousel.</summary>
public sealed class HomeTile : ObservableObject
{
    private string _title = "";
    private string _subtitle = "";
    private string _badge = "";
    private string _iconKey;
    private double _progress = double.NaN;
    private System.Windows.Media.ImageSource? _image;
    private double _width = SquareWidth;
    private bool _alert;

    /// <summary>A tile is a square; one with a game's picture is as wide as the picture (16:9).</summary>
    public const double SquareWidth = 372;
    public const double WideWidth = 661;

    public HomeTile(string key, string iconKey, string seed, Action invoke)
    {
        Key = key;
        _iconKey = iconKey;
        Seed = seed;
        Command = new RelayCommand(invoke);
    }

    public string Key { get; }

    public string Seed { get; }

    public ICommand Command { get; }

    public string IconKey
    {
        get => _iconKey;
        set => Set(ref _iconKey, value);
    }

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => Set(ref _subtitle, value);
    }

    public string Badge
    {
        get => _badge;
        set => Set(ref _badge, value);
    }

    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    /// <summary>A picture instead of the generated art (the running game's, in overlay mode).</summary>
    public System.Windows.Media.ImageSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    public double Width
    {
        get => _width;
        set => Set(ref _width, value);
    }

    /// <summary>Something waits for the player here (an update): the badge stands out.</summary>
    public bool Alert
    {
        get => _alert;
        set => Set(ref _alert, value);
    }
}

/// <summary>
/// Home: the carousel (Play Online, Get Mods, the checklist, What's New with the update), the session's status under it,
/// and the dock (Play, Friends, the player's Mods, Settings...: nothing the carousel has). PLAY is the Rust launcher's
/// PLAY / STOP; when something blocks it, A on it explains what and leads to the place that fixes it. Without a
/// server (<see cref="Locked"/>) the launcher is a shell for the game: "Play" starts it offline, and the dock's
/// Friends button is "Configure online" with a lock (Settings › Online).
/// </summary>
public sealed class HomeViewModel : PageViewModel
{
    private readonly HomeTile _play;
    private readonly HomeTile _store;
    private readonly HomeTile _checklist;
    private readonly HomeTile _update;
    private HomeTile? _selected;

    /// <summary>The language changed: home again.</summary>
    public override PageViewModel? Recreate() => new HomeViewModel(Shell);

    public HomeViewModel(ShellViewModel shell)
        : base(shell)
    {
        PlayCommand = RelayCommand.Async(TogglePlayAsync);
        FriendsCommand = new RelayCommand(() =>
        {
            if (Locked)
            {
                Shell.ConfigureOnline();
            }
            else
            {
                Shell.OpenFriends();
            }
        });
        // What's New; while an update is on its way or waits, its card (what the dock's Updates button did)
        UpdateCommand = new RelayCommand(() =>
        {
            if (UpdatePending)
            {
                Shell.ShowUpdateModal();
            }
            else
            {
                Shell.Navigate(new WhatsNewViewModel(Shell));
            }
        });
        SettingsCommand = new RelayCommand(() => Shell.OpenSettings());
        ModsCommand = new RelayCommand(() => Shell.Navigate(Routes.Mods));
        FullscreenCommand = RelayCommand.Async(() => Shell.SetFullscreenAsync(!Shell.IsFullscreen));
        PowerCommand = new RelayCommand(ShowPowerMenu);
        GameMenuCommand = new RelayCommand(() => Shell.LeaveOverlay(toMenu: true));
        QuitGameCommand = RelayCommand.Async(() => Shell.QuitGameAsync());
        StatusCommand = new RelayCommand(() =>
        {
            if (Problem is not null)
            {
                Shell.Navigate(Routes.Checklist);
            }
        });
        // the checklist; while the runtime downloads or the game is set up, that work's card (what the Game card did)
        ChecklistCommand = new RelayCommand(() =>
        {
            if (Shell.IsGameWorking)
            {
                Shell.ShowGameWork();
            }
            else
            {
                Shell.Navigate(Routes.Checklist);
            }
        });
        StoreCommand = new RelayCommand(() => Shell.Navigate(new ModBrowserViewModel(Shell)));

        // Four big cards (players' feedback, 2026-11-25): Play, the mod store, the checklist, What's New. Friends and the
        // player's own mod list are in the dock.
        _play = new HomeTile("play", "Icon.Kart", "#0", () => PlayCommand.Execute(null));
        _store = new HomeTile("store", "Icon.Search", "#3", () => StoreCommand.Execute(null));
        _checklist = new HomeTile("checklist", "Icon.Check", "#2", () => ChecklistCommand.Execute(null));
        _update = new HomeTile("update", "Icon.Download", "#5", () => UpdateCommand.Execute(null));
        Tiles = [_play, _store, _checklist, _update];
        Refresh();
    }

    public override bool ShowTopBar => true;

    public ObservableCollection<HomeTile> Tiles { get; }

    /// <summary>The tile under the focus (the view sets it): its title is shown above it.</summary>
    public HomeTile? SelectedTile
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    // ── the session's status, under the carousel ──

    private bool Busy => Launcher.Session.Busy;

    /// <summary>Over a running game (launcher/docs/OVERLAY.md): the Play tile is the way back, PLAY starts nothing.</summary>
    private bool Overlay => Shell.IsOverlay;

    /// <summary>What blocks PLAY now (nothing while a session runs: STOP always works; nothing over a game: it runs).</summary>
    private string? Problem => Busy || Overlay ? null : Launcher.PlayProblem;

    /// <summary>The session's status, verbatim; what blocks PLAY instead while idle; "in game" over a game the launcher holds no seat for.</summary>
    public string StatusText => Overlay && !Busy ? S["home.overlay.status"]
        : Launcher.Session.State == SessionState.Failed && Launcher.Session.Status.Length > 0 ? Launcher.Session.Status
        : Problem is { } problem && Shell.GameWorkText is { } work && Shell.GameWorkExplains(problem) ? work // "Setting up the game · 42% · 6 minutes left"
        : Problem ?? Launcher.Session.Status;

    /// <summary>good / warn / bad / info / plain.</summary>
    public string StatusTone => Overlay && !Busy ? "good"
        : Launcher.Session.State == SessionState.Failed ? "bad" : Problem is null ? Launcher.Session.Tone : "warn";

    public bool HasProblem => Problem is not null;

    /// <summary>Something is on its way (starting the game, taking a seat).</summary>
    public bool IsWorking => Launcher.Session.State == SessionState.Starting || Launcher.Session.Phase is SeatPhase.Joining
        || (!Busy && !Overlay && Shell.IsGameWorking);

    public bool IsSettled => !IsWorking;

    /// <summary>No server is set: online features are locked behind "Configure online".</summary>
    public bool Locked => !Launcher.OnlineEnabled;

    /// <summary>The server's part of the status line: only with a server.</summary>
    public bool ShowServer => !Locked;

    /// <summary>The server pill: "ONLINE · 12/32 PLAYERS" (the players in seats, out of the seats; the queue after them when there is one).</summary>
    public string ServerText => CoreStrings.ServerPill(Launcher.Server);

    public bool IsServerOnline => Launcher.Server.State == ServerState.Online;

    // ── the dock ──

    public string PlayLabel => Overlay ? S["home.overlay.back"] : Busy ? S["home.stop"] : Locked ? S["home.play_offline"] : S["home.play"];

    public string PlayIcon => Overlay ? "Icon.Play" : Busy ? "Icon.Stop" : "Icon.Kart";

    /// <summary>"mkw-recomp menu": the game's own menu, offered only over a game that can be told to open it.</summary>
    public bool ShowGameMenu => Overlay && Shell.OverlayHasMenu;

    public string GameMenuLabel => S.Format("home.overlay.menu", ("game", TargetName));

    public ICommand GameMenuCommand { get; }

    /// <summary>"Quit Game" in the dock, over a game: ends the game itself (it asks first), not only the launcher.</summary>
    public bool ShowQuitGame => Overlay;

    public ICommand QuitGameCommand { get; }

    private string TargetName
    {
        get
        {
            var id = Launcher.Game.Install?.TargetId ?? Launcher.Settings.Target;
            return Launcher.Game.Targets.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? id;
        }
    }

    public string FullscreenLabel => Shell.IsFullscreen ? S["home.windowed"] : S["home.fullscreen"];

    public string FullscreenIcon => Shell.IsFullscreen ? "Icon.Window" : "Icon.Fullscreen";

    /// <summary>The Steam Deck's Game Mode has one full-screen window: nothing to switch.</summary>
    public bool CanSwitchFullscreen => !Desktop.DesktopEnvironment.IsGameMode && !Overlay; // over a game the window covers the game

    /// <summary>An update on its way or waiting: the What's New tile opens its card and its badge stands out.</summary>
    private bool UpdatePending => Launcher.Update.State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall or UpdateState.Failed or UpdateState.Restarting;

    public bool HasRequests => !Locked && Launcher.Friends.Incoming.Count > 0;

    /// <summary>The dock's Friends button; "Configure Online" with a lock while there is no server.</summary>
    public string FriendsLabel => Locked ? Shell.OnlineLockLabel : S["home.friends"];

    public string FriendsIcon => Locked ? "Icon.FriendsLocked" : "Icon.Friends";

    public ICommand PlayCommand { get; }

    /// <summary>The friends panel; Settings › Online while there is no server.</summary>
    public ICommand FriendsCommand { get; }

    /// <summary>The What's New tile: the update's card while one is pending, else What's New.</summary>
    public ICommand UpdateCommand { get; }

    public ICommand SettingsCommand { get; }

    /// <summary>The player's mods (launcher/docs/MODS.md): the dock's Mods button.</summary>
    public ICommand ModsCommand { get; }

    /// <summary>The dock's Mods: with a game in use, and not over a running one (nothing of a game changes while it runs).</summary>
    public bool ShowMods => !Overlay && Launcher.Game.Install is not null;

    /// <summary>Its dot: GameBanana has an update for one of the player's mods.</summary>
    public bool ModsHaveUpdates => Launcher.Mods.Updates.Count > 0;

    /// <summary>The Get Mods card: GameBanana's mods of the game (the mod store).</summary>
    public ICommand StoreCommand { get; }

    public ICommand FullscreenCommand { get; }

    public ICommand PowerCommand { get; }

    /// <summary>A click on the status line: the checklist, when something blocks PLAY.</summary>
    public ICommand StatusCommand { get; }

    /// <summary>The Checklist card: the play checklist ("Ready to Play"); while the game's setup works, its card.</summary>
    public ICommand ChecklistCommand { get; }

    public override IReadOnlyList<Hint> Hints => Overlay
        ?
        [
            new Hint(PadButton.Start, S["hint.options"]),
            new Hint(PadButton.Y, S["hint.friends"]),
            new Hint(PadButton.B, S["home.overlay.back"]),
            new Hint(PadButton.A, S["hint.ok"]),
        ]
        :
        [
            new Hint(PadButton.Start, S["hint.options"]),
            new Hint(PadButton.Y, S["hint.friends"]),
            new Hint(PadButton.A, S["hint.start"]),
        ];

    /// <summary>B on home, over a game: back to it.</summary>
    public override bool OnBack()
    {
        if (!Overlay)
        {
            return false;
        }
        Shell.LeaveOverlay();
        return true;
    }

    public override void OnEnter()
    {
        Shell.PropertyChanged -= OnShellChanged;
        Shell.PropertyChanged += OnShellChanged;
        if (!Launcher.Mods.Loaded)
        {
            _ = Launcher.RefreshModsAsync(); // the dock Mods' dot (an update on GameBanana): read off the window's thread
        }
        Refresh();
        RaiseAll();
    }

    public override void OnLeave() => Shell.PropertyChanged -= OnShellChanged;

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsFullscreen))
        {
            Raise(nameof(FullscreenLabel));
            Raise(nameof(FullscreenIcon));
        }
        else if (e.PropertyName is nameof(ShellViewModel.IsOverlay) or nameof(ShellViewModel.OverlayThumb) or nameof(ShellViewModel.OverlayHasMenu))
        {
            Refresh();
            RaiseAll();
            RefreshHints();
        }
    }

    public override void OnStateChanged()
    {
        Refresh();
        base.OnStateChanged();
    }

    private static string Number(int value) => value.ToString(CultureInfo.CurrentCulture);

    /// <summary>The cover last read from its file: home is made again often (a page back, another language), the file is read once.</summary>
    private static (string Path, System.Windows.Media.ImageSource? Image) _cover = ("", null);

    /// <summary>
    /// The game's own cover (<see cref="ILauncher.GameCover"/>: a file the core made from the player's copy of the
    /// game, beside the launcher) as the Play card's art; null when there is none or it cannot be read: the card then
    /// keeps its drawn art. Only ever shown here.
    /// </summary>
    private static System.Windows.Media.ImageSource? Cover(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        if (_cover.Path != path)
        {
            _cover = (path, Mkwmp.App.Overlay.OverlayArt.Load(path));
        }
        return _cover.Image;
    }

    private void Refresh()
    {
        var server = Launcher.Server;
        var game = Launcher.Game;
        var session = Launcher.Session;

        // Play Online: the game and its version; the seats of the server (or where the player is in it)
        _play.Title = PlayLabel;
        _play.IconKey = Overlay ? "Icon.Kart" : PlayIcon;
        _play.Image = Overlay ? Shell.OverlayThumb : Cover(Launcher.GameCover); // over a game its live picture wins; else the game's own cover, when there is one
        _play.Width = Overlay ? HomeTile.WideWidth : HomeTile.SquareWidth;
        _play.Subtitle = Problem is not null ? S["home.play_blocked"]
            : game.Install is { } install ? string.IsNullOrEmpty(install.GameVersion) ? "MKW" : $"MKW {install.GameVersion}"
            : Overlay ? "MKW"
            : S["home.no_game"];
        _play.Badge = Overlay ? S["home.overlay.badge"] : Locked ? S["home.offline"] : session.Phase switch
        {
            SeatPhase.Queued when session.QueuePosition > 0 => S.Format("home.queued", ("n", Number(session.QueuePosition))),
            SeatPhase.Seated => S["home.seated"],
            _ => server.State switch
            {
                ServerState.Online => S.Format("home.seats", ("seated", Number(server.Seated)), ("total", Number(server.SeatsTotal))),
                ServerState.Checking => S["home.checking"],
                _ => S["home.offline"],
            },
        };

        // Get Mods: the mod store (GameBanana); the player's own list is the dock's Mods
        _store.Title = S["mods.get"];
        _store.Subtitle = "GameBanana";

        // The checklist: ready, or what blocks PLAY first; while the runtime downloads or the game is set up, that work live
        var ready = Launcher.Checklist.Ready;
        _checklist.Title = S["home.checklist"];
        _checklist.Subtitle = Shell.GameWorkShort is { } working ? Shell.GameWorkTitle + " · " + working
            : ready ? S["checklist.ready"]
            : Launcher.PlayProblem ?? S["home.game_attention"];
        _checklist.Progress = Shell.GameWorkProgress;
        _checklist.Badge = game.Searching ? S["home.game_searching"]
            : Shell.GameWorkPhase is GameWorkPhase.Runtime or GameWorkPhase.Setup ? Shell.GameWorkPercent
            : Shell.GameWorkPhase is GameWorkPhase.RuntimeFailed or GameWorkPhase.SetupFailed ? S["gamework.state.failed"]
            : Shell.GameWorkPhase == GameWorkPhase.UpdateNeeded ? S["gamework.state.update"]
            : ready ? S["home.game_ready"]
            : S["gamework.state.needed"];
        _checklist.Alert = !ready && !Busy && !Overlay;
        // What's New, and the update: what it is doing in the subtitle, a badge that stands out while it waits, the strip while it downloads
        var update = Launcher.Update;
        _update.Title = S["home.update"];
        _update.Subtitle = update.State switch
        {
            UpdateState.Available => S.Format("home.update_available", ("version", update.AvailableVersion ?? "")),
            UpdateState.Downloading => S["home.update_downloading"],
            UpdateState.ReadyToInstall => S.Format("home.update_ready", ("version", update.AvailableVersion ?? "")),
            UpdateState.Restarting => S["update.restarting"],
            UpdateState.Checking => S["update.checking"],
            UpdateState.Failed => S["update.failed"],
            _ => S.Format("home.update_current", ("version", Launcher.Version)),
        };
        _update.Badge = update.State switch
        {
            UpdateState.Available => S["home.update_new"],
            UpdateState.Downloading => update.Progress is >= 0 and <= 1 ? Number((int)Math.Round(update.Progress * 100)) + "%" : S["home.update_new"],
            UpdateState.ReadyToInstall => S["home.update_ready_badge"],
            UpdateState.Failed => S["gamework.state.failed"],
            _ => "",
        };
        _update.Alert = UpdatePending;
        _update.IconKey = UpdatePending ? "Icon.Download" : "Icon.Star";
        _update.Progress = update.State == UpdateState.Downloading ? update.Progress : double.NaN;
    }
    // ── PLAY ──

    private Task TogglePlayAsync()
    {
        if (Overlay)
        {
            // the game runs: this is the way back to it, never a second game (and never STOP under the player's hands)
            Shell.LeaveOverlay();
            return Task.CompletedTask;
        }
        if (Busy)
        {
            return Launcher.StopAsync();
        }
        if (Launcher.PlayProblem is not null && Shell.NeedsRuntimeUpdate)
        {
            // MKWMP runs on one mkw-recomp: the update's card, which puts it in place (it was offered at start; here it is again)
            Shell.ShowGameWork(driving: true);
            return Task.CompletedTask;
        }
        if (Launcher.PlayProblem is not null)
        {
            // what blocks it, every other thing PLAY needs, and the button that fixes each: the checklist
            Shell.Navigate(Routes.Checklist);
            return Task.CompletedTask;
        }
        return Launcher.PlayAsync();
    }

    // ── power ──

    private void ShowPowerMenu()
    {
        var gameRuns = Shell.GameRuns;
        // over a game the menu is about the game first; the launcher's seat sentence is for a launcher that holds one
        var modal = new ModalViewModel { Title = S["power.title"], Body = Overlay ? S["power.game_body"] : Locked ? "" : S["quit.body"] }; // no seat to give back without a server
        void Add(string label, Action run, bool isDefault = false) => modal.Actions.Add(new ModalAction(label, () =>
        {
            Shell.CloseModal(modal);
            run();
        }, Controls.ButtonKind.Outline, isDefault));

        if (Shell.TrayAvailable)
        {
            Add(S["power.hide"], Shell.HideToTray, isDefault: true);
        }
        else if (Desktop.DesktopEnvironment.IsWine && Desktop.DesktopEnvironment.CanMinimize)
        {
            Add(S["power.minimize"], Shell.HideToTray, isDefault: true); // no tray under Wine: the window steps aside minimized
        }
        if (CanSwitchFullscreen)
        {
            Add(Shell.IsFullscreen ? S["power.windowed"] : S["power.fullscreen"],
                () => RelayCommand.Async(() => Shell.SetFullscreenAsync(!Shell.IsFullscreen)).Execute(null), isDefault: modal.Actions.Count == 0);
        }
        Add(S["power.bug"], () => RelayCommand.Async(Shell.ReportBugAsync).Execute(null), isDefault: modal.Actions.Count == 0); // "Found a bug?": the report flow
        if (gameRuns)
        {
            Add(S["power.quit_game"], () => RelayCommand.Async(() => Shell.QuitGameAsync()).Execute(null), isDefault: modal.Actions.Count == 0);
        }
        Add(S["power.quit"], () => RelayCommand.Async(QuitAsync).Execute(null), isDefault: modal.Actions.Count == 0);
        modal.Bottom = new ModalAction(S["common.cancel"], () => Shell.CloseModal(modal));
        Shell.ShowModal(modal);
    }

    /// <summary>
    /// Quits at once; asks first while a session holds a seat (quitting gives it back). While a
    /// game runs the question is which of the two: the launcher alone (the game runs on, without
    /// its overlay) or the game too.
    /// </summary>
    private async Task QuitAsync()
    {
        if (Shell.GameRuns)
        {
            var modal = new ModalViewModel { Title = S["quit.game.title"], Body = S["quit.game.body"] };
            modal.Actions.Add(new ModalAction(S["quit.game.both"], () =>
            {
                Shell.CloseModal(modal);
                RelayCommand.Async(() => Shell.QuitGameAsync(thenQuit: true, confirmed: true)).Execute(null);
            }, Controls.ButtonKind.Outline, isDefault: true));
            modal.Actions.Add(new ModalAction(S["quit.game.launcher"], () =>
            {
                Shell.CloseModal(modal);
                Shell.Quit();
            }));
            modal.Bottom = new ModalAction(S["common.cancel"], () => Shell.CloseModal(modal));
            Shell.ShowModal(modal);
            return;
        }
        if (!Busy || await Shell.ConfirmAsync(S["quit.title"], S["quit.body"], S["quit.yes"]).ConfigureAwait(true))
        {
            Shell.Quit();
        }
    }
}
