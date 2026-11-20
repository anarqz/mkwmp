// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>A card of the home carousel.</summary>
public sealed class HomeTile : ObservableObject
{
    private string _title = "";
    private string _subtitle = "";
    private string _badge = "";
    private double _progress = double.NaN;

    public HomeTile(string key, string iconKey, string seed, Action invoke)
    {
        Key = key;
        IconKey = iconKey;
        Seed = seed;
        Command = new RelayCommand(invoke);
    }

    public string Key { get; }

    public string IconKey { get; }

    public string Seed { get; }

    public ICommand Command { get; }

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
}

/// <summary>
/// Home: the carousel and the dock. STUB of the design system: it already plays (in the demo),
/// opens the pages and the panels; the home agent owns this file and Views/HomePage.xaml(.cs).
/// </summary>
public sealed class HomeViewModel : PageViewModel
{
    private readonly HomeTile _play;
    private readonly HomeTile _chat;
    private readonly HomeTile _friends;
    private readonly HomeTile _game;
    private readonly HomeTile _update;
    private string _selectedTitle = "";

    public HomeViewModel(ShellViewModel shell)
        : base(shell)
    {
        _play = new HomeTile("play", "Icon.Kart", "#0", () => RelayCommand.Async(TogglePlayAsync).Execute(null));
        _chat = new HomeTile("chat", "Icon.Headset", "#1", () => Shell.Navigate(Routes.Chat));
        _friends = new HomeTile("friends", "Icon.Friends", "#3", () => Shell.IsFriendsOpen = true);
        _game = new HomeTile("game", "Icon.Folder", "#2", () => Shell.Navigate(Routes.Settings));
        _update = new HomeTile("update", "Icon.Download", "#5", Shell.ShowUpdateModal);
        Tiles = [_play, _chat, _friends, _game, _update];

        FriendsCommand = new RelayCommand(() => Shell.IsFriendsOpen = true);
        ChatCommand = new RelayCommand(() => Shell.Navigate(Routes.Chat));
        UpdateCommand = new RelayCommand(Shell.ShowUpdateModal);
        SettingsCommand = new RelayCommand(() => Shell.Navigate(Routes.Settings));
        FullscreenCommand = RelayCommand.Async(() => Shell.SetFullscreenAsync(!Shell.IsFullscreen));
        QuitCommand = RelayCommand.Async(AskQuitAsync);
        Refresh();
        _selectedTitle = _play.Title;
    }

    public override bool ShowTopBar => true;

    public ObservableCollection<HomeTile> Tiles { get; }

    /// <summary>The title of the tile under the focus, shown above the carousel.</summary>
    public string SelectedTitle
    {
        get => _selectedTitle;
        set => Set(ref _selectedTitle, value);
    }

    public string StatusText => Launcher.PlayProblem ?? Launcher.Session.Status;

    /// <summary>good / warn / bad / info / plain.</summary>
    public string StatusTone => Launcher.PlayProblem is null ? Launcher.Session.Tone : "bad";

    public bool IsBusy => Launcher.Session.State == SessionState.Starting || Launcher.Session.Phase is SeatPhase.Joining;

    public bool HasUpdate => Launcher.Update.State is UpdateState.Available or UpdateState.Downloading;

    public bool HasRequests => Launcher.Friends.Incoming.Count > 0;

    public ICommand FriendsCommand { get; }

    public ICommand ChatCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand FullscreenCommand { get; }

    public ICommand QuitCommand { get; }

    public override IReadOnlyList<Hint> Hints =>
    [
        new Hint(PadButton.Start, S["hint.options"]),
        new Hint(PadButton.Y, S["hint.friends"]),
        new Hint(PadButton.A, S["hint.start"]),
    ];

    public override void OnStateChanged()
    {
        Refresh();
        base.OnStateChanged();
    }

    private void Refresh()
    {
        var server = Launcher.Server;
        var game = Launcher.Game;
        var session = Launcher.Session;
        _play.Title = session.Busy ? S["home.stop"] : S["home.play"];
        _play.Subtitle = game.Install is { } install ? $"Mario Kart World {install.GameVersion}" : S["home.no_game"];
        _play.Badge = server.State == ServerState.Online
            ? S.Format("home.seats", ("seated", server.Seated.ToString(CultureInfo.CurrentCulture)), ("total", server.SeatsTotal.ToString(CultureInfo.CurrentCulture)))
            : S["home.offline"];
        _chat.Title = S["home.chat"];
        _chat.Subtitle = S["home.chat_sub"];
        _friends.Title = S["home.friends"];
        _friends.Subtitle = S.Format("home.friends_online", ("n", Launcher.Friends.OnlineCount.ToString(CultureInfo.CurrentCulture)));
        _friends.Badge = Launcher.Friends.Incoming.Count > 0 ? "+" + Launcher.Friends.Incoming.Count.ToString(CultureInfo.CurrentCulture) : "";
        _game.Title = S["home.game"];
        _game.Subtitle = game.Install is { } found ? $"{found.TargetId} {found.Version}" : S["home.no_game"];
        var update = Launcher.Update;
        _update.Title = S["home.update"];
        _update.Subtitle = update.AvailableVersion is { } version ? S.Format("home.update_version", ("version", version)) : S["update.uptodate"];
        _update.Progress = update.State == UpdateState.Downloading ? update.Progress : double.NaN;
    }

    private Task TogglePlayAsync() => Launcher.Session.Busy ? Launcher.StopAsync() : Launcher.PlayAsync();

    private async Task AskQuitAsync()
    {
        if (await Shell.ConfirmAsync(S["quit.title"], S["quit.body"], S["quit.yes"]).ConfigureAwait(true))
        {
            Shell.Quit();
        }
    }
}
