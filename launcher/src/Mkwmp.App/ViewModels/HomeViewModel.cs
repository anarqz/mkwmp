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
}

/// <summary>
/// Home: the carousel (Play Online, Friends, Game, What's New), the session's status under it, and
/// the dock. PLAY is the Rust launcher's PLAY / STOP; when something blocks it, A on it explains
/// what and leads to the place that fixes it. Without a server (<see cref="Locked"/>) the launcher
/// is a shell for the game: "Play" starts it offline, and the Friends tile and dock button are
/// "Configure online" with a lock (Settings › Online).
/// </summary>
public sealed class HomeViewModel : PageViewModel
{
    private readonly HomeTile _play;
    private readonly HomeTile _friends;
    private readonly HomeTile _game;
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
        GameCommand = new RelayCommand(() =>
        {
            if (Shell.HasGameWork)
            {
                Shell.ShowGameWork(); // a download or the setup runs, failed or is needed: its card, not the settings
            }
            else
            {
                Shell.OpenSettings("game");
            }
        });
        UpdateCommand = new RelayCommand(Shell.ShowUpdateModal);
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
        ChecklistCommand = new RelayCommand(() => Shell.Navigate(Routes.Checklist));

        _play = new HomeTile("play", "Icon.Kart", "#0", () => PlayCommand.Execute(null));
        _friends = new HomeTile("friends", "Icon.Friends", "#3", () => FriendsCommand.Execute(null));
        _game = new HomeTile("game", "Icon.Folder", "#2", () => GameCommand.Execute(null));
        // What's New: the changelog page, with the update (when there is one) as its banner
        _update = new HomeTile("update", "Icon.Download", "#5", () => Shell.Navigate(new WhatsNewViewModel(Shell)));
        Tiles = [_play, _friends, _game, _update];
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

    public bool HasUpdate => Launcher.Update.State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall or UpdateState.Failed;

    public bool HasRequests => !Locked && Launcher.Friends.Incoming.Count > 0;

    /// <summary>The dock's Friends button; "Configure Online" with a lock while there is no server.</summary>
    public string FriendsLabel => Locked ? S["online.configure"] : S["home.friends"];

    public string FriendsIcon => Locked ? "Icon.FriendsLocked" : "Icon.Friends";

    public ICommand PlayCommand { get; }

    /// <summary>The friends panel; Settings › Online while there is no server.</summary>
    public ICommand FriendsCommand { get; }

    public ICommand GameCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand SettingsCommand { get; }

    /// <summary>The game's mods (launcher/docs/MODS.md).</summary>
    public ICommand ModsCommand { get; }

    /// <summary>Mods in the dock: with a game in use, and not over a running one (nothing of a game changes while it runs).</summary>
    public bool ShowMods => !Overlay && Launcher.Game.Install is not null;

    public ICommand FullscreenCommand { get; }

    public ICommand PowerCommand { get; }

    /// <summary>A click on the status line: the checklist, when something blocks PLAY.</summary>
    public ICommand StatusCommand { get; }

    /// <summary>"Checklist" in the dock: the play checklist ("Ready to Play").</summary>
    public ICommand ChecklistCommand { get; }

    /// <summary>The dock's checklist button: not over a running game.</summary>
    public bool ShowChecklist => !Overlay;

    /// <summary>Its dot: something blocks PLAY.</summary>
    public bool ChecklistNeeds => !Launcher.Checklist.Ready;

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
        var friends = Launcher.Friends;

        // Play Online: the game and its version; the seats of the server (or where the player is in it)
        _play.Title = PlayLabel;
        _play.IconKey = Overlay ? "Icon.Kart" : PlayIcon;
        _play.Image = Overlay ? Shell.OverlayThumb : Cover(Launcher.GameCover); // over a game its live picture wins; else the game's own cover, when there is one
        _play.Width = Overlay ? HomeTile.WideWidth : HomeTile.SquareWidth;
        _play.Subtitle = Problem is not null ? S["home.play_blocked"]
            : game.Install is { } install ? string.IsNullOrEmpty(install.GameVersion) ? "Mario Kart World" : $"Mario Kart World {install.GameVersion}"
            : Overlay ? "Mario Kart World"
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

        // Friends; without a server the same tile is "Configure online" (it keeps its place and the focus)
        _friends.IconKey = Locked ? "Icon.Lock" : "Icon.Friends";
        _friends.Title = Locked ? S["online.configure"] : S["home.friends"];
        _friends.Subtitle = Locked ? S["home.configure_sub"]
            : !Launcher.Profile.SignedIn ? S["home.friends_signin"]
            : !friends.Loaded ? S["home.friends_loading"]
            : S.Format("home.friends_online", ("n", Number(friends.OnlineCount)));
        _friends.Badge = !Locked && friends.Incoming.Count > 0 ? "+" + Number(friends.Incoming.Count) : "";

        // Game: which way of running the game (the target) and whether MKWMP is in it
        _game.Title = S["home.game"];
        var targetId = game.Install?.TargetId ?? Launcher.Settings.Target;
        var targetName = game.Targets.FirstOrDefault(t => string.Equals(t.Id, targetId, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? targetId;
        _game.Subtitle = game.Install is { } found ? string.IsNullOrEmpty(found.Version) ? targetName : $"{targetName} {found.Version}" : S["home.no_game"];
        if (Shell.GameWorkShort is { } working)
        {
            // a download or the setup, live: what it is on the tile, how far under it, and the strip
            _game.Title = Shell.GameWorkTitle;
            _game.Subtitle = working;
        }
        _game.Progress = Shell.GameWorkProgress;
        _game.Badge = game.Searching ? S["home.game_searching"]
            : Shell.GameWorkPhase is GameWorkPhase.Runtime or GameWorkPhase.Setup ? Shell.GameWorkPercent
            : Shell.GameWorkPhase is GameWorkPhase.RuntimeFailed or GameWorkPhase.SetupFailed ? S["gamework.state.failed"]
            : Shell.GameWorkPhase == GameWorkPhase.UpdateNeeded ? S["gamework.state.update"]
            : Shell.GameWorkPhase == GameWorkPhase.SetupNeeded ? S["home.game_setup"]
            : game.Working ? S["home.game_working"]
            : game.Install is null ? S["home.game_setup"]
            : game.Check is { Status: not TargetStatus.Ready } || game.Integration is { Ok: false } ? S["home.game_attention"]
            : S["home.game_ready"];

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
        _update.Badge = update.State switch { UpdateState.Available => S["home.update_new"], UpdateState.ReadyToInstall => S["home.update_ready_badge"], _ => "" };
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
