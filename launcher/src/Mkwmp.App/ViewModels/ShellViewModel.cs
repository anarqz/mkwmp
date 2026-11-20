// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using Mkwmp.App.Controls;
using Mkwmp.App.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>A disc of the top bar: the player, then the friends who are on.</summary>
public sealed record TopAvatar(string Name, AvatarPresence Presence, string Count, bool IsSelf);

/// <summary>
/// The window's state around <see cref="ILauncher"/>: it brings the launcher's changes and toasts
/// to the UI thread, and owns what is not the launcher's: the page stack, the modal, the bottom
/// sheet, the friends panel, the toasts, the hint bar, the theme. Pages reach all of it through
/// <see cref="PageViewModel.Shell"/>.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ToastStay = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private readonly CancellationTokenSource _lifetime = new();
    private int _changePending;
    private ModalViewModel? _modal;
    private LayerViewModel? _sheet;
    private bool _isFriendsOpen;
    private InputKind _inputKind = InputKind.Keyboard;
    private bool _gamepadConnected;
    private bool _isFullscreen;
    private string _clockText = "";
    private string _appliedTheme = "";
    private IReadOnlyList<Hint> _hints = [];
    private PageViewModel? _hintSource;
    private IReadOnlyList<TopAvatar> _topAvatars = [];

    public ShellViewModel(ILauncher launcher, Strings strings)
    {
        Launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        S = strings ?? throw new ArgumentNullException(nameof(strings));
        _dispatcher = Dispatcher.CurrentDispatcher;
        _isFullscreen = launcher.Settings.Ui.Fullscreen;
        Nav = new Navigator();
        Nav.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Navigator.Current))
            {
                OnPageChanged();
            }
        };
        FriendsPanel = new FriendsPanelViewModel(this);
        Routes.RegisterAll(this);

        launcher.Changed += OnLauncherChanged;
        launcher.Toasted += OnLauncherToasted;
        ApplyTheme(launcher.Settings.Ui.Theme);

        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Tick();
        _clock.Start();
        Tick();
        RefreshHints();
    }

    public ILauncher Launcher { get; }

    public Strings S { get; }

    public Navigator Nav { get; }

    public FriendsPanelViewModel FriendsPanel { get; }

    /// <summary>The launcher's state changed. Always on the UI thread, at most once per burst of changes.</summary>
    public event EventHandler? StateChanged;

    // ── the launcher's views, for bindings in the shell's own chrome ──

    public ServerView Server => Launcher.Server;

    public ProfileView Profile => Launcher.Profile;

    public FriendsView Friends => Launcher.Friends;

    public SessionView Session => Launcher.Session;

    public UpdateView Update => Launcher.Update;

    public LauncherSettings Settings => Launcher.Settings;

    public bool IsServerOnline => Launcher.Server.State == ServerState.Online;

    /// <summary>
    /// The player, then up to five friends who are playing or online. Without a server (locked
    /// online): only the player, from the local name, with no request count.
    /// </summary>
    public IReadOnlyList<TopAvatar> TopAvatars
    {
        get
        {
            var list = new List<TopAvatar>();
            var profile = Launcher.Profile;
            if (!OnlineEnabled)
            {
                if (Launcher.Settings.Name.Trim() is { Length: > 0 } local)
                {
                    list.Add(new TopAvatar(local, AvatarPresence.None, "", true));
                }
            }
            else
            {
                if (profile.SignedIn)
                {
                    var requests = Launcher.Friends.Incoming.Count;
                    list.Add(new TopAvatar(profile.Name, AvatarPresence.None, requests > 0 ? requests.ToString(CultureInfo.CurrentCulture) : "", true));
                }
                list.AddRange(Launcher.Friends.Friends
                    .Where(f => f.State != FriendState.Offline)
                    .OrderBy(f => f.State)
                    .Take(5)
                    .Select(f => new TopAvatar(f.Name, f.State == FriendState.Playing ? AvatarPresence.Playing : AvatarPresence.Online, "", false)));
            }
            if (!list.SequenceEqual(_topAvatars))
            {
                _topAvatars = list; // the same instance while nothing changed: the discs (and a focus on one) stay
            }
            return _topAvatars;
        }
    }

    // ── about ──

    public string AboutTitle => $"MKWMP Launcher {Launcher.Version}";

    public static string AboutBasedOn => "Based on Infinity Launcher - https://github.com/anarqz/InfinityHorizon";

    public static string AboutLicense => "GPL-3.0-or-later";

    /// <summary>The three lines of the About box.</summary>
    public string AboutText => $"{AboutTitle}\n{AboutBasedOn}\n{AboutLicense}";

    // ── pages ──

    public PageViewModel? Page => Nav.Current;

    public bool ShowTopBar => Page?.ShowTopBar ?? false;

    public void Navigate(string route) => Nav.Navigate(route);

    public void Navigate(PageViewModel page) => Nav.Navigate(page);

    public bool GoBack() => Nav.GoBack();

    /// <summary>Home as the only page (after the first-run setup, or from anywhere).</summary>
    public void GoHome() => Nav.ResetTo(Routes.Home);

    private void OnPageChanged()
    {
        if (_hintSource is not null)
        {
            _hintSource.PropertyChanged -= OnPagePropertyChanged;
        }
        _hintSource = Page;
        if (_hintSource is not null)
        {
            _hintSource.PropertyChanged += OnPagePropertyChanged;
        }
        Raise(nameof(Page));
        Raise(nameof(ShowTopBar));
        RefreshHints();
    }

    private void OnPagePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.Hints))
        {
            RefreshHints();
        }
        else if (e.PropertyName is nameof(PageViewModel.ShowTopBar))
        {
            Raise(nameof(ShowTopBar));
        }
    }

    // ── modal ──

    public ModalViewModel? Modal
    {
        get => _modal;
        private set
        {
            var old = _modal;
            if (Set(ref _modal, value))
            {
                old?.OnClosed();
                value?.OnOpened();
                Raise(nameof(IsModalOpen));
                RefreshHints();
            }
        }
    }

    public bool IsModalOpen => _modal is not null;

    /// <summary>Shows the modal (replacing one that is open).</summary>
    public void ShowModal(ModalViewModel modal) => Modal = modal ?? throw new ArgumentNullException(nameof(modal));

    /// <summary>Closes the modal; with an argument, only if that one is still the open one.</summary>
    public void CloseModal(ModalViewModel? modal = null)
    {
        if (modal is null || ReferenceEquals(modal, _modal))
        {
            Modal = null;
        }
    }

    /// <summary>B / Esc / a tap on the dimmed page.</summary>
    public void DismissModal()
    {
        if (_modal is { CanDismiss: true } modal)
        {
            Modal = null;
            modal.Dismiss();
        }
    }

    /// <summary>Asks a yes / no question; true for <paramref name="yes"/>. Dismissing it is a no.</summary>
    public Task<bool> ConfirmAsync(string title, string body, string? yes = null, string? no = null)
    {
        var answer = new TaskCompletionSource<bool>();
        var modal = new ModalViewModel { Title = title, Body = body };
        modal.Actions.Add(new ModalAction(yes ?? S["common.ok"], () =>
        {
            CloseModal(modal);
            answer.TrySetResult(true);
        }));
        modal.Bottom = new ModalAction(no ?? S["common.cancel"], () =>
        {
            CloseModal(modal);
            answer.TrySetResult(false);
        });
        modal.Dismissed += (_, _) => answer.TrySetResult(false);
        ShowModal(modal);
        return answer.Task;
    }

    /// <summary>Says something; completes when the player closes it.</summary>
    public Task MessageAsync(string title, string body, string? close = null)
    {
        var done = new TaskCompletionSource();
        var modal = new ModalViewModel { Title = title, Body = body };
        modal.Bottom = new ModalAction(close ?? S["common.ok"], () =>
        {
            CloseModal(modal);
            done.TrySetResult();
        });
        modal.Dismissed += (_, _) => done.TrySetResult();
        ShowModal(modal);
        return done.Task;
    }

    /// <summary>A modal that cannot be dismissed: a spinner, and a bar once you <see cref="ProgressModal.Report"/>. Close it yourself.</summary>
    public ProgressModal ShowProgress(string title, string body = "")
    {
        var modal = new ProgressModal(this, title, body);
        ShowModal(modal);
        return modal;
    }

    /// <summary>The update card (what is new, download, restart).</summary>
    public void ShowUpdateModal() => ShowModal(new UpdateModal(this));

    // ── bottom sheet ──

    /// <summary>The open bottom sheet's view model (its view: a DataTemplate in ViewMap.xaml).</summary>
    public LayerViewModel? Sheet
    {
        get => _sheet;
        private set
        {
            var old = _sheet;
            if (Set(ref _sheet, value))
            {
                old?.OnClosed();
                value?.OnOpened();
                Raise(nameof(IsSheetOpen));
                RefreshHints();
            }
        }
    }

    public bool IsSheetOpen => _sheet is not null;

    public void OpenSheet(LayerViewModel sheet) => Sheet = sheet ?? throw new ArgumentNullException(nameof(sheet));

    public void CloseSheet() => Sheet = null;

    // ── friends panel ──

    public bool IsFriendsOpen
    {
        get => _isFriendsOpen;
        set
        {
            if (Set(ref _isFriendsOpen, value))
            {
                if (value)
                {
                    FriendsPanel.OnOpened();
                }
                else
                {
                    FriendsPanel.OnClosed();
                }
                RefreshHints();
            }
        }
    }

    public void ToggleFriends() => IsFriendsOpen = !IsFriendsOpen;

    // ── toasts ──

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    /// <summary>Shows a toast for a few seconds (at most four at a time; the oldest goes first).</summary>
    public void ShowToast(string title, string body = "", ToastKind kind = ToastKind.Info)
    {
        var toast = new ToastViewModel(title, body, kind);
        Toasts.Add(toast);
        while (Toasts.Count > 4)
        {
            Toasts.RemoveAt(0);
        }
        _ = RemoveLater(toast);
    }

    private async Task RemoveLater(ToastViewModel toast)
    {
        try
        {
            await Task.Delay(ToastStay, _lifetime.Token).ConfigureAwait(true);
            toast.Leaving = true;
            await Task.Delay(260, _lifetime.Token).ConfigureAwait(true);
            Toasts.Remove(toast);
        }
        catch (OperationCanceledException)
        {
            // the window is closing
        }
    }

    // ── input and hints ──

    /// <summary>What the player touched last (the window sets it).</summary>
    public InputKind InputKind
    {
        get => _inputKind;
        set => Set(ref _inputKind, value);
    }

    public bool GamepadConnected
    {
        get => _gamepadConnected;
        set => Set(ref _gamepadConnected, value);
    }

    /// <summary>The hint bar now: the top layer's, else the page's.</summary>
    public IReadOnlyList<Hint> Hints
    {
        get => _hints;
        private set => Set(ref _hints, value);
    }

    /// <summary>The window's handler for a button press (so a tap on a hint does what the button does).</summary>
    public Func<PadButton, bool>? ButtonSink { get; set; }

    /// <summary>Presses a button as if on the controller.</summary>
    public void Press(PadButton button)
    {
        if (ButtonSink is { } sink)
        {
            sink(button);
        }
        else
        {
            HandleButton(button);
        }
    }

    public void RefreshHints()
    {
        if (_keyboard is { } keyboard)
        {
            Hints = keyboard.Hints ?? [];
        }
        else if (_modal is { } modal)
        {
            Hints = modal.Hints ?? (modal.CanDismiss ? [new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["hint.ok"])] : [new Hint(PadButton.A, S["hint.ok"])]);
        }
        else if (_sheet is { } sheet)
        {
            Hints = sheet.Hints ?? [new Hint(PadButton.B, S["hint.close"]), new Hint(PadButton.A, S["hint.ok"])];
        }
        else if (_isFriendsOpen)
        {
            Hints = FriendsPanel.Hints ?? [new Hint(PadButton.B, S["hint.close"]), new Hint(PadButton.A, S["hint.ok"])];
        }
        else
        {
            Hints = Page?.Hints ?? DefaultHints();
        }
    }

    /// <summary>Friends, Back (when there is a page to go back to), OK.</summary>
    public IReadOnlyList<Hint> DefaultHints()
    {
        var hints = new List<Hint> { new(PadButton.Y, S["hint.friends"]) };
        if (Nav.CanGoBack)
        {
            hints.Add(new Hint(PadButton.B, S["hint.back"]));
        }
        hints.Add(new Hint(PadButton.A, S["hint.ok"]));
        return hints;
    }

    /// <summary>
    /// A button that is not a direction or A: the top layer gets it first, then the page, then the
    /// shell's own meaning (B back, Y / RB friends, Start settings). True when something used it.
    /// </summary>
    public bool HandleButton(PadButton button)
    {
        if (HandleKeyboardButton(button))
        {
            return true; // the on-screen keyboard is above everything
        }
        if (_modal is { } modal)
        {
            if (modal.OnButton(button))
            {
                return true;
            }
            if (button == PadButton.B)
            {
                DismissModal();
            }
            return true; // a modal swallows everything else
        }
        if (_sheet is { } sheet)
        {
            if (sheet.OnButton(button))
            {
                return true;
            }
            if (button == PadButton.B)
            {
                CloseSheet();
            }
            return true;
        }
        if (_isFriendsOpen)
        {
            if (FriendsPanel.OnButton(button))
            {
                return true;
            }
            if (button is PadButton.B or PadButton.Y or PadButton.RB)
            {
                IsFriendsOpen = false;
            }
            return true;
        }
        if (Page is { } page && button != PadButton.B && page.OnButton(button))
        {
            return true;
        }
        switch (button)
        {
            case PadButton.B:
                return Page?.OnBack() == true || GoBack();
            case PadButton.Y or PadButton.RB:
                IsFriendsOpen = true;
                return true;
            case PadButton.Start:
                if (Page is not SettingsViewModel)
                {
                    Navigate(Routes.Settings);
                }
                return true;
            default:
                return false;
        }
    }

    // ── theme, window ──

    /// <summary>"light" or "dark" (<c>UiSettings.Theme</c>).</summary>
    public string Theme => _appliedTheme;

    public bool IsDark => _appliedTheme == "dark";

    /// <summary>Switches the palette now and saves the choice.</summary>
    public Task SetThemeAsync(string theme)
    {
        ApplyTheme(theme);
        return UpdateSettingsAsync(s => s with { Ui = s.Ui with { Theme = _appliedTheme } });
    }

    private void ApplyTheme(string theme)
    {
        theme = theme == "dark" ? "dark" : "light";
        if (_appliedTheme == theme)
        {
            return;
        }
        _appliedTheme = theme;
        ThemeManager.Apply(theme);
        Raise(nameof(Theme));
        Raise(nameof(IsDark));
    }

    /// <summary>Borderless full screen, or a normal window (F11). The window follows this.</summary>
    public bool IsFullscreen
    {
        get => _isFullscreen;
        private set => Set(ref _isFullscreen, value);
    }

    public Task SetFullscreenAsync(bool fullscreen)
    {
        IsFullscreen = fullscreen;
        return UpdateSettingsAsync(s => s with { Ui = s.Ui with { Fullscreen = fullscreen } });
    }

    /// <summary>Saves a changed copy of the settings (no save when nothing changed).</summary>
    public async Task UpdateSettingsAsync(Func<LauncherSettings, LauncherSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var before = Launcher.Settings;
        var after = change(before);
        if (!Equals(before, after))
        {
            await Launcher.SaveSettingsAsync(after).ConfigureAwait(true);
        }
    }

    /// <summary>The window closes itself on this.</summary>
    public event EventHandler? QuitRequested;

    public void Quit()
    {
        IsQuitting = true;
        QuitRequested?.Invoke(this, EventArgs.Empty);
    }

    // ── the launcher's events ──

    private void OnLauncherChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind, RaiseState);
        }
    }

    private void RaiseState()
    {
        Interlocked.Exchange(ref _changePending, 0);
        ApplyTheme(Launcher.Settings.Ui.Theme);
        RaiseAll();
        Page?.OnStateChanged();
        FriendsPanel.OnStateChanged();
        _sheet?.OnStateChanged();
        _modal?.OnStateChanged();
        OfferCrash(); // the game crashed and the player has not answered yet (ShellViewModel.Crash.cs)
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnLauncherToasted(object? sender, Toast toast) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!OnlineEnabled && toast.Kind is ToastKind.FriendOnline or ToastKind.FriendPlaying or ToastKind.FriendRequest)
            {
                return; // locked online: there are no friends to hear from, here or in the system's notifications
            }
            if (ToastSink?.Invoke(toast) != true)
            {
                ShowToast(toast.Title, toast.Body, toast.Kind);
            }
        });

    private void Tick()
    {
        ClockText = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (DateTime.UtcNow >= _nextBatteryRead)
        {
            ReadBattery();
        }
    }

    public string ClockText
    {
        get => _clockText;
        private set => Set(ref _clockText, value);
    }

    public void Dispose()
    {
        _clock.Stop();
        Launcher.Changed -= OnLauncherChanged;
        Launcher.Toasted -= OnLauncherToasted;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

/// <summary>Swaps the palette (Themes/Colors.Light.xaml or Colors.Dark.xaml): the first merged dictionary of the app.</summary>
public static class ThemeManager
{
    public static string Current { get; private set; } = "light";

    public static void Apply(string theme)
    {
        Current = theme == "dark" ? "dark" : "light";
        if (System.Windows.Application.Current is not { } app)
        {
            return;
        }
        var colors = new System.Windows.ResourceDictionary
        {
            Source = new Uri($"/mkwmp-launcher;component/Themes/Colors.{(Current == "dark" ? "Dark" : "Light")}.xaml", UriKind.Relative),
        };
        var merged = app.Resources.MergedDictionaries;
        if (merged.Count > 0)
        {
            merged[0] = colors;
        }
        else
        {
            merged.Add(colors);
        }
    }
}
