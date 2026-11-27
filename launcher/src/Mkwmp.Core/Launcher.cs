// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Security.Cryptography;
using Mkwmp.Core.Devices;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;
using Mkwmp.Core.Updates;

namespace Mkwmp.Core;

/// <summary>
/// The launcher's engine (the port of rust/client's Core), shared by the window and
/// <c>--headless</c>: settings, the server's public status, the profile, friends, the PLAY session
/// (seat + game), the game's integration and updates. It knows no game: everything about one way
/// of running the game is behind <see cref="IGameTarget"/>, picked by
/// <see cref="LauncherSettings.Target"/>.
/// <para>
/// Thread-safe. The views are immutable and replaced as a whole under one lock;
/// <see cref="Changed"/> is raised outside it, on whatever thread made the change.
/// </para>
/// </summary>
public sealed partial class Launcher : ILauncher, ILocalApiHost
{
    private readonly Lock _gate = new();
    /// <summary>One config write at a time (the window and the local API both save).</summary>
    private readonly Lock _configWrite = new();
    private readonly LauncherOptions _options;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly TargetRegistry _targets;
    private readonly Updater _updater;
    private readonly LocalApi _local;
    private readonly DeviceLab _devices;
    private readonly CancellationTokenSource _life = new();
    private readonly string _userAgent;
    private readonly IReadOnlyList<TargetInfo> _targetInfos;

    // ---- the state the views are built from (under _gate) ---------------------------------
    private ClientConfig _config;
    private RegisterState _register;
    private string? _registerMessage;
    private ServerView _server = new(ServerState.Checking, null, 0, 0, 0, null);
    private SessionState _sessionState;
    private SeatSnapshot? _seatSnapshot;
    private bool _gameRunning;
    private string? _sessionError;
    private string? _saveError;
    private FriendList? _friendList;
    private (string Text, bool IsError)? _friendsNote;
    private bool _friendsBusy;
    private bool? _reportLoaded;
    private OpenReport? _openReport;
    private (string Text, bool IsError)? _reportNote;
    private bool _reportBusy;
    private GameInstall? _install;
    private TargetCheck? _check;
    private IntegrationReport? _integration;
    private bool _searching;
    private bool _gameWorking;
    private (string Text, bool IsError)? _gameNote;
    private bool _localApiUp;
    private int _stars;

    // ---- the views (replaced by Rebuild, read without the lock) ---------------------------
    private volatile LauncherSettings _settingsView;
    private volatile ServerView _serverView;
    private volatile ProfileView _profileView;
    private volatile SessionView _sessionView;
    private volatile FriendsView _friendsView;
    private volatile GameView _gameView;
    private volatile VoiceView _voiceView;
    private volatile ReportView _reportView;
    private volatile UpdateView _updateView = new(UpdateState.Checking, null, null, 0, null, "");
    private volatile string? _playProblem;
    private volatile PlayChecklist _checklist = PlayChecklist.Empty;

    /// <summary>
    /// Opens the launcher on a settings file. Throws <see cref="LauncherException"/> when
    /// client.toml exists but cannot be read or parsed (the player must fix or delete it; starting
    /// with defaults would overwrite their PIN on the first save).
    /// </summary>
    /// <param name="configPath">client.toml (it need not exist yet).</param>
    /// <param name="launcherFolder">The folder of the executable: <c>logs/</c> and the bundled <c>game/</c> folder live there.</param>
    /// <param name="options">The command line and host overrides.</param>
    /// <param name="http">Every request goes through this handler (tests: a fake server).</param>
    /// <param name="clock">Time, for tests.</param>
    public Launcher(string configPath, string launcherFolder, LauncherOptions? options = null, HttpMessageHandler? http = null, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(configPath);
        ArgumentException.ThrowIfNullOrEmpty(launcherFolder);
        ConfigPath = configPath;
        LauncherFolder = launcherFolder;
        _options = options ?? new LauncherOptions();
        _clock = clock ?? TimeProvider.System;
        Version = _options.Version ?? LauncherOptions.AssemblyVersion;
        _userAgent = $"mkwmp-launcher/{Version}";
        // Each call sets its own time limit (a status check: 10 s; the friends long-poll: 40 s).
        _http = http is null ? new HttpClient() : new HttpClient(http, disposeHandler: false);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        Log = new SessionLog(launcherFolder, _clock, echo: true);
        AppDomain.CurrentDomain.UnhandledException += OnCrash;
        Strings.Changed += OnLanguageChanged;
        _config = ClientConfig.LoadOrDefault(configPath);
        _targets = new TargetRegistry(_options.Targets ?? [new MkwRecompTarget(Version, Log.Note)]);
        _targetInfos = _targets.Infos;
        _updater = new Updater(_http, _userAgent, _options.UpdateApi, _options.ReleaseKeys);
        _local = new LocalApi(this, _clock);
        _nextendoApi = new NextendoApi(_http, _userAgent);
        var vault = NextendoVault.Beside(configPath);
        _nextendo = new NextendoAccount(_nextendoApi, vault, _clock);
        _nxFriends = new NextendoRoster(_nextendo, _nextendoApi, _clock);
        _nxPresence = new NextendoPresence(_nextendo, _nextendoApi, _clock);
        _nxLobbies = new NextendoLobbies(_nextendo, _nextendoApi, _clock);
        // Pictures are asked without the sign-in and never followed to another site (Launcher.NextendoAvatars.cs).
        _avatarHttp = http is null ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan } : null;
        _nxAvatars = new NextendoAvatars(_avatarHttp ?? _http, _userAgent, Path.Combine(launcherFolder, CacheFolderName, NextendoAvatars.FolderName), _clock, Log.Note);
        _nxAvatars.Changed += RaiseChanged;
        // An account client.toml names without its sign-in beside it (the file was copied from another PC, or the
        // sign-in deleted) is not linked here: the choice of network stays, and on Nextendo the window asks to sign in.
        if (_config.Nextendo.Linked && !vault.Exists)
        {
            _config = _config with { Nextendo = _config.Nextendo.Unlinked() };
        }
        // Where PLAY goes online is settled here, once: what the file says, else Nextendo Network, else (an install
        // that already has an MKWMP setup) MKWMP servers. Kept from now on, and written at the next save: it never
        // changes under the player because a server or a profile came or went.
        _config = _config with { Nextendo = _config.Nextendo.Playing(_config.PlayOn) };
        WatchOverlayHost();
        _devices = new DeviceLab(CameraFolders, Log.Note);
        Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        // A launcher dropped into the game folder (or beside it) finds the game next to it.
        if (_config.Settings.Game.Length == 0
            && GameFinder.Find(_targets, null, null, launcherFolder, _config.Settings.Target, searchRoots: []) is { } near)
        {
            _config = _config with { Settings = _config.Settings with { Game = near.Install.Root, Target = near.Target.Id } };
        }
        // Someone who already plays online here (a server, a registered name, the consent) never meets the first-start welcome.
        if (!_config.Settings.Ui.SetupDone && IsOnline(_config.Settings) && _config.Settings.AdultConsent && Ident(_config.Settings) is not null)
        {
            _config = _config with { Settings = _config.Settings with { Ui = _config.Settings.Ui with { SetupDone = true } } };
        }
        // Offline (no server chosen): the server is never asked, so there is nothing to be "checking".
        if (!IsOnline(_config.Settings))
        {
            _server = NotAsked;
        }
        _localApiUp = !_options.BindLocalApi;
        _settingsView = _config.Settings;
        _serverView = _server;
        _profileView = null!;
        _sessionView = null!;
        _friendsView = null!;
        _gameView = null!;
        _voiceView = null!;
        _reportView = null!;
        InspectGame(out _install, out _check, out _integration);
        _setupStatus = InspectSetup(_install);
        FollowCover(_install, _setupStatus); // the Play card's picture, already there when the window opens (Launcher.Cover.cs)
        lock (_gate)
        {
            Rebuild();
        }
    }

    public event EventHandler? Changed;

    public event EventHandler<Toast>? Toasted;

    public string ConfigPath { get; }

    public string LauncherFolder { get; }

    /// <summary>This session's log (the window may add its own lines).</summary>
    public SessionLog Log { get; }

    public string Version { get; }

    /// <summary>
    /// The version as a bug report names it, with the update channel: "1.5.1-rc.2 (alpha channel)", and ", debug mode on"
    /// while Debug mode is on (its logs carry the mod's deep trace; the launcher log names it at each start).
    /// </summary>
    private string ReportVersion => UpdateChannels.Describe(Version, Settings.UpdateChannel) + (Settings.DebugMode ? ", debug mode on" : "");

    /// <summary>The bearer token of the local API, random per start, written into the game's session file.</summary>
    internal string Token { get; }

    /// <summary>The port the local API listens on (0 until bound).</summary>
    public int LocalApiPort => _local.Port;

    /// <summary>
    /// The game's local API is listening in this process. While it is not (another launcher holds
    /// the port), PLAY is refused: the game would reach the other launcher.
    /// </summary>
    public bool LocalApiUp
    {
        get
        {
            lock (_gate)
            {
                return _localApiUp;
            }
        }
    }

    public LauncherSettings Settings => _settingsView;

    public ServerView Server => _serverView;

    public ProfileView Profile => _profileView;

    public SessionView Session => _sessionView;

    public FriendsView Friends => _friendsView;

    public GameView Game => _gameView;

    public VoiceView Voice => _voiceView;

    public ReportView Report => _reportView;

    public UpdateView Update => _updateView;

    public int GitHubStars
    {
        get
        {
            lock (_gate)
            {
                return _stars;
            }
        }
    }

    public IDeviceLab Devices => _devices;

    public string? PlayProblem => _playProblem;

    public PlayChecklist Checklist => _checklist;

    private static readonly ServerView NotAsked = new(ServerState.NotAsked, null, 0, 0, 0, null);

    private volatile IReadOnlyList<SavedServer> _savedServersView = [];

    public IReadOnlyList<SavedServer> SavedServers => _savedServersView;

    /// <summary>A server is chosen (<see cref="ILauncher.OnlineEnabled"/>): without one, no server is ever contacted and PLAY starts the game offline.</summary>
    internal static bool HasServer(LauncherSettings s) => !string.IsNullOrWhiteSpace(s.Server);

    /// <summary>
    /// The one gate of everything that talks to the game server: a server is chosen, and this run is not offline only
    /// (<see cref="OnlineBlockedByUpdate"/>: a required update could not be installed and the player went on without
    /// it). False is the launcher's offline state, the same in both cases: no status, presence, friends or reports,
    /// PLAY starts the game with no seat, and the game's own request for one is refused.
    /// </summary>
    /// <remarks>
    /// Also false while Nextendo Network is where PLAY goes online (<see cref="OnNextendo"/>, Launcher.Nextendo.cs),
    /// with or without a linked account: no MKWMP server is contacted then, whatever server is set. MKWMP servers
    /// are the player's own choice (Settings › Online).
    /// </remarks>
    internal bool IsOnline(LauncherSettings s) => HasServer(s) && !_offlineOnly && !OnNextendo;

    public bool OnlineEnabled => IsOnline(Settings);

    public bool OnlineBlockedByUpdate => _offlineOnly;

    /// <summary>Why an online action is refused now, in the player's language.</summary>
    private string OnlineOffWhy => _offlineOnly ? Strings.UpdateToPlayOnline : Strings.OnlineOff;

    bool ILocalApiHost.Online => IsOnline(Settings);

    string ILocalApiHost.OfflineText => _offlineOnly ? Strings.UpdateToPlayOnlineInGame
        : OnNextendo ? Strings.NextendoSignInInGame
        : Strings.OnlineOffInGame;

    /// <summary>The registered game targets (the selected one first is not implied: see <see cref="LauncherSettings.Target"/>).</summary>
    public TargetRegistry Targets => _targets;

    // ILocalApiHost
    string ILocalApiHost.Token => Token;

    SeatSession? ILocalApiHost.Seat => _seat;

    IReadOnlyList<Friend>? ILocalApiHost.FriendsOrNull
    {
        get
        {
            lock (_gate)
            {
                return _friendList?.Friends;
            }
        }
    }

    ClientConfig ILocalApiHost.Config => Config;

    string? ILocalApiHost.EditVoice(Func<ClientConfig, ClientConfig> edit)
    {
        try
        {
            EditConfig(edit, wakeFriends: false);
            return null;
        }
        catch (LauncherException e)
        {
            return e.Message;
        }
    }

    internal ClientConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    private bool LaunchesGame(LauncherSettings s) => s.Launch && !_options.NoLaunch;

    /// <summary>Always, for a player (client.toml's old <c>update = false</c> is read and ignored); off only with <c>--no-update</c>.</summary>
    private bool UpdatesOn(LauncherSettings s) => !_options.NoUpdate;

    private void Note(string message) => Log.Note(message);

    // ---- state changes --------------------------------------------------------------------

    /// <summary>Changes the state under the lock, rebuilds the views, and tells the window when one of them is different.</summary>
    private void Mutate(Action change)
    {
        if (MutateQuietly(change))
        {
            RaiseChanged();
        }
    }

    /// <summary>As <see cref="Mutate"/>, for a caller that holds another lock: it raises <see cref="Changed"/> itself after letting go.</summary>
    private bool MutateQuietly(Action change)
    {
        lock (_gate)
        {
            change();
            return Rebuild();
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            // a window that is closing must not stop a background loop
        }
    }

    private void Toast(Toast toast)
    {
        try
        {
            Toasted?.Invoke(this, toast);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException or TaskCanceledException)
        {
            // as above
        }
    }

    /// <summary>
    /// What is missing in the settings before PLAY can work, in the order the player fixes it.
    /// Offline (no server) only the game is asked for: consent, gamertag and PIN belong to a server.
    /// </summary>
    internal static string? ConfigProblem(LauncherSettings s, bool gameFound, string folderHint, bool? online = null)
    {
        if (online ?? HasServer(s))
        {
            // The order of the flow: a server, a profile on it (sign in), then the consent that server keeps.
            if (!ClientConfig.ValidName(s.Name))
            {
                return Strings.PickGamertag;
            }
            if (!ClientConfig.ValidPin(s.Pin))
            {
                return Strings.NeedPin;
            }
            if (!s.AdultConsent)
            {
                return Strings.NoConsent;
            }
        }
        if (!gameFound)
        {
            return Strings.SetGameFolder(folderHint);
        }
        return s.Patch ? null : Strings.InstallPatchFirst;
    }

    /// <summary>Rebuilds every view from the state (under the lock); true when one changed.</summary>
    private bool Rebuild()
    {
        var changed = false;
        void Put<T>(ref T field, T value)
            where T : class
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                changed = true;
            }
        }

        var s = _config.Settings;
        var target = _targets.Get(s.Target);
        var configProblem = ConfigProblem(s, _install is not null, target.FolderHint, IsOnline(s));
        var settings = _settingsView;
        Put(ref settings, s);
        _settingsView = settings;

        var server = _serverView;
        Put(ref server, _server);
        _serverView = server;

        var online = IsOnline(s);
        var saved = _savedServersView;
        if (!saved.Select(p => (p.Address, p.Name)).SequenceEqual(_config.Servers.Select(p => (p.Server, p.Name))))
        {
            _savedServersView = [.. _config.Servers.Select(p => new SavedServer(p.Server, p.Name, true))];
            changed = true;
        }

        var nameValid = ClientConfig.ValidName(s.Name);
        var (message, isError) = s.Name.Length > 0 && !nameValid ? (Strings.NameRule, true)
            : _register == RegisterState.Failed ? (_registerMessage ?? Strings.ServerError, true)
            : !online ? (!nameValid ? Strings.PickLocalName : OnNextendo ? Strings.LocalNameNextendo : Strings.LocalNameOnly, false)
            : _register == RegisterState.Deleted && s.Name.Length == 0 ? (Strings.ProfileDeleted, false)
            : _register == RegisterState.Taken && s.Pin.Length != 6 ? (Strings.NameIsRegistered, true)
            : s.Pin.Length == 6 ? (Strings.KeepYourPin, false)
            : (Strings.RegisterToPlay, false);
        var profile = _profileView;
        // Signed in is about a server: a local name alone (offline) is not a profile anywhere.
        Put(ref profile, new ProfileView(s.Name, online ? s.Pin : "", nameValid, online && nameValid && s.Pin.Length == 6, _register, message, isError));
        _profileView = profile;

        var steam = !LaunchesGame(s);
        var snap = _seatSnapshot;
        var offlinePlay = _offlineSession;
        var nextendoPlay = _nextendoSession; // also an offline one for MKWMP: no seat, no phase
        var (status, tone) = _sessionState switch
        {
            SessionState.Idle => configProblem is null ? (NextendoMode ? Strings.NextendoReady : online ? Strings.Ready : Strings.ReadyOffline, "plain") : (Strings.Upper(configProblem), "warn"),
            SessionState.Starting => (nextendoPlay ? Strings.NextendoConnecting : offlinePlay ? Strings.StartingGame : Strings.Connecting, "info"),
            SessionState.Failed => (_sessionError ?? "", "bad"),
            _ when nextendoPlay => _gameRunning ? (Strings.NextendoInGame, "good") : (steam ? Strings.StartFromSteam : Strings.StartingGame, "info"),
            _ when offlinePlay => _gameRunning ? (Strings.PlayingOffline, "good") : (steam ? Strings.StartFromSteam : Strings.StartingGame, "info"),
            _ => snap switch
            {
                { Phase: SeatPhase.Refused } => (snap.Message ?? "", "bad"),
                { Phase: SeatPhase.Busy } => (snap.Message ?? "", "warn"),
                { Phase: SeatPhase.Queued } => (Strings.Queued(snap.Position), "warn"),
                { Phase: SeatPhase.WaitingForGame } => (steam && !_gameRunning ? Strings.StartFromSteam : Strings.PickOnlinePlay, "info"),
                { Phase: SeatPhase.Seated } => (steam && !_gameRunning ? Strings.SeatedStartFromSteam : Strings.SeatedHaveFun, "good"),
                _ => (Strings.TakingASeat, "info"),
            },
        };
        if (_saveError is not null)
        {
            (status, tone) = (_saveError, "bad");
        }
        var running = _sessionState == SessionState.Running;
        var session = _sessionView;
        Put(ref session, new SessionView(
            _sessionState,
            running && !offlinePlay ? snap?.Phase ?? SeatPhase.Joining : null, // offline: no seat, so no phase
            running && snap is { Phase: SeatPhase.Queued } ? snap.Position : 0,
            running && _gameRunning,
            steam,
            status,
            tone));
        _sessionView = session;

        var friends = _friendsView;
        Put(ref friends, new FriendsView(
            _friendList is not null,
            _friendList?.Friends ?? [],
            _friendList?.Incoming ?? [],
            _friendList?.Outgoing ?? [],
            _friendsNote?.Text,
            _friendsNote?.IsError ?? false,
            _friendsBusy));
        _friendsView = friends;

        var nextendo = _nextendoView;
        Put(ref nextendo, NextendoNow());
        _nextendoView = nextendo;

        var nextendoFriends = _nxView;
        Put(ref nextendoFriends, NextendoFriendsNow());
        _nxView = nextendoFriends;

        var voice = _voiceView;
        Put(ref voice, new VoiceView(_config.Inputs, _config.Outputs, _config.Mutes));
        _voiceView = voice;

        var report = _reportView;
        Put(ref report, new ReportView(_reportLoaded, _openReport, Strings.ReportReasons, _reportNote?.Text, _reportNote?.IsError ?? false, _reportBusy));
        _reportView = report;

        // What blocks PLAY, in the Rust window's order: the settings, the game's gates, the local API, the integration.
        var check = _check;
        var integrationProblem = _integration?.Problem;
        // A setup the launcher must run first (Launcher.GameSetup.cs) comes after the game folder and its gates, and
        // before everything about the integration: its switch in the settings, the local API, its files.
        var setupProblem = SetupProblem(check);
        // The runtime the launcher keeps comes before its setup: an update of ours that is not installed yet (this
        // launcher is made for the pinned build, and the game is set up again for it), or its install under way.
        var runtime = _runtimeView;
        Put(ref runtime, RuntimeNow());
        _runtimeView = runtime;
        var runtimeProblem = _install is null || runtime.PinnedVersion is null ? null
            : runtime.State == RuntimeState.UpdateAvailable ? Strings.RuntimeUpdateNeeded(target.DisplayName, runtime.PinnedLabel ?? runtime.PinnedVersion)
            : runtime.Busy ? Strings.RuntimeBusy(target.DisplayName)
            : null;
        var gate = runtimeProblem ?? setupProblem;
        var beforePatchSwitch = gate is not null && configProblem == Strings.InstallPatchFirst;
        var problem = (beforePatchSwitch ? gate : configProblem) ?? HostedGameProblem ?? check?.Problem ?? gate ?? LocalApiProblem(s) ?? integrationProblem;
        var setup = _gameSetupView;
        Put(ref setup, GameSetupNow(check));
        _gameSetupView = setup;
        if (check is { Status: TargetStatus.Ready } && setupProblem is not null)
        {
            check = check with { Status = TargetStatus.NeedsRebuild, Problem = setupProblem };
        }
        else if (check is { Status: TargetStatus.Ready } && integrationProblem is not null)
        {
            check = check with { Status = TargetStatus.NoIntegration, Problem = integrationProblem };
        }
        var game = _gameView;
        Put(ref game, new GameView(_targetInfos, _install, check, _integration, _searching, _gameWorking, _gameNote?.Text, _gameNote?.IsError ?? false));
        _gameView = game;

        // The play checklist, from the same views; what blocks PLAY is never something it does not show, and the other way round.
        var checklist = _checklist;
        Put(ref checklist, PlayChecklist.Build(new ChecklistFacts(s, server, game, runtime, setup, _updateView, Version, problem) { Nextendo = nextendo }));
        _checklist = checklist;
        problem ??= checklist.Blocking.FirstOrDefault()?.Status;

        if (_playProblem != problem)
        {
            _playProblem = problem;
            changed = true;
        }
        return changed;
    }

    private string? LocalApiProblem(LauncherSettings s) => _localApiUp ? null : Strings.AnotherLauncher(s.LocalPort);

    // ---- settings -------------------------------------------------------------------------

    /// <summary>Changes client.toml in place and saves it at once when something changed; throws <see cref="LauncherException"/> when it cannot be written.</summary>
    private void EditConfig(Func<ClientConfig, ClientConfig> edit, bool wakeFriends)
    {
        bool changed;
        lock (_configWrite)
        {
            var before = Config;
            var after = edit(before).Remembering(); // the profile in use is also the one kept for its server
            if (after.SameAs(before))
            {
                return;
            }
            after.Save(ConfigPath);
            changed = MutateQuietly(() => _config = after);
        }
        // Outside the lock: a window that handles this by saving something itself must not wait on us.
        if (changed)
        {
            RaiseChanged();
        }
        if (wakeFriends)
        {
            // Another name or PIN signs in again; the status pill follows another server at once.
            _friendsWake.Set();
            _statusWake.Set();
            _nxWake.Set(); // the notification settings are also those of Nextendo friends
        }
    }

    public Task SaveSettingsAsync(LauncherSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Off the caller's thread (the window's): the file write, and the look at a new game folder.
        return Task.Run(() => SaveSettingsCore(settings));
    }

    private void SaveSettingsCore(LauncherSettings settings)
    {
        var before = Settings;
        settings = settings with { Server = ClientConfig.ServerBase(settings.Server) };
        var serverChanged = !ClientConfig.SameServer(settings.Server, before.Server);
        // A PIN belongs to one name on one server: another name, or another server, never goes out with this one's PIN.
        // The one kept for that name on that server comes back instead (connecting again, switching servers).
        if ((serverChanged || !string.Equals(settings.Name, before.Name, StringComparison.OrdinalIgnoreCase)) && settings.Pin == before.Pin)
        {
            settings = settings with { Pin = Config.PinFor(settings.Server, settings.Name) };
        }
        // The consent is given to one server (its own policy): another server starts without it, a known one with what
        // was given there. (A server that keeps consent itself says so again at the sign-in.) Offline it means nothing.
        // Choosing an MKWMP server is choosing MKWMP servers: nothing would reach it while PLAY goes to Nextendo.
        // (The window offers a server only once MKWMP servers are chosen; this keeps every other way in consistent.)
        var toMkwmp = serverChanged && HasServer(settings) && OnNextendo && !_offlineOnly && !Session.Busy;
        if (serverChanged && (IsOnline(settings) || toMkwmp) && settings.AdultConsent == before.AdultConsent)
        {
            settings = settings with { AdultConsent = Config.ConsentFor(settings.Server, settings.Name) };
        }
        string? error = null;
        try
        {
            // The voice lists the game owns (its devices, its mutes) are kept as they are now.
            EditConfig(c => c with { Settings = settings, Nextendo = toMkwmp ? c.Nextendo.Playing(PlayNetwork.Mkwmp) : c.Nextendo }, wakeFriends: true);
        }
        catch (LauncherException e)
        {
            error = e.Message;
        }
        if (error is null && serverChanged)
        {
            ServerChanged(before.Server);
        }
        if (error is null && settings.Background.StartWithSystem != before.Background.StartWithSystem)
        {
            error = ApplyAutostart(settings.Background.StartWithSystem);
        }
        Mutate(() =>
        {
            _saveError = error;
            if (settings.Name != before.Name || serverChanged)
            {
                (_register, _registerMessage) = (RegisterState.Idle, null); // the last REGISTER outcome was about another name
            }
        });
        if (error is null && settings.AdultConsent != before.AdultConsent)
        {
            ConsentChanged();
        }
        if (settings.Game != before.Game || settings.Target != before.Target)
        {
            RefreshGame();
        }
        if (error is null && settings.Ui.OverlayShortcut != before.Ui.OverlayShortcut)
        {
            OverlayShortcutChanged();
        }
    }


    private string? ApplyAutostart(bool on)
    {
        var exe = _options.Executable ?? LauncherOptions.SelfExecutable();
        if (exe is null)
        {
            return null; // run from the dotnet host (development): nothing to register
        }
        try
        {
            (_options.Autostart ?? new RegistryAutostart()).Apply(on, exe);
            return null;
        }
        catch (LauncherException e)
        {
            Note(e.Message);
            return e.Message;
        }
    }

    public void ShowTestToast() => Toast(new Toast(Strings.TestToastTitle, Strings.TestToastBody, ToastKind.FriendOnline));

    /// <summary>
    /// The window was closed into the tray: says so once ever (the player may think the launcher
    /// quit, and friends would see them online).
    /// </summary>
    public void ShowTrayHint()
    {
        var s = Settings;
        if (s.Background.TrayHintShown)
        {
            return;
        }
        try
        {
            EditConfig(c => c with { Settings = c.Settings with { Background = c.Settings.Background with { TrayHintShown = true } } }, wakeFriends: false);
        }
        catch (LauncherException e)
        {
            Note(e.Message);
        }
        Toast(new Toast(Strings.TrayHintTitle, Strings.TrayHintBody, ToastKind.Info));
    }

    public Task<string> SaveBugReportAsync() => Task.Run(() =>
    {
        var s = Settings;
        var target = _targets.Get(s.Target);
        var (_, zip) = SupportBundle.Save(LauncherFolder, ReportVersion, target, GameFinder.Detect(target, s.Game), _clock);
        Note($"bug report saved: {zip}");
        return zip;
    });

    private IEnumerable<string> CameraFolders()
    {
        yield return LauncherFolder;
        var s = Settings;
        var target = _targets.Get(s.Target);
        if (GameFinder.Detect(target, s.Game) is { } install)
        {
            foreach (var folder in target.NativeLibraryFolders(install))
            {
                yield return folder;
            }
        }
    }

    internal HttpClient HttpClient => _http;

    private SeatApi Api(string server) => new(_http, server, _userAgent);

    private SeatApi Api() => Api(Settings.Server);


    /// <summary>
    /// The player chose another language (<see cref="Strings.Use"/>): the views are rebuilt with
    /// its texts, and what is read from the game folder (the patch's state, the setup's plan, the
    /// game's settings) is read again.
    /// </summary>
    private void OnLanguageChanged()
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                Mutate(() => { });
                RefreshGame();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException or InvalidOperationException)
            {
                // the two-second refresh does it
            }
        });
    }

    /// <summary>A crash is the first thing a bug report needs: it goes to the session log before the process dies.</summary>
    private void OnCrash(object sender, UnhandledExceptionEventArgs e) => Log.Line($"PANIC: {e.ExceptionObject}");

    public async ValueTask DisposeAsync()
    {
        if (_life.IsCancellationRequested)
        {
            return;
        }
        AppDomain.CurrentDomain.UnhandledException -= OnCrash;
        Strings.Changed -= OnLanguageChanged;
        global::Mkwmp.Core.Targets.MkwRecomp.Recomp.SetDevExes(this, ""); // this launcher's developer builds are no one else's
        // Window closed: free the seat and show offline before leaving.
        Interlocked.Increment(ref _nxQuiet); // nothing more is said of the player on Nextendo, but "offline"
        await Task.WhenAll(WaitIdleAsync(TimeSpan.FromSeconds(5)), NextendoOfflineAsync(TimeSpan.FromSeconds(3))).ConfigureAwait(false);
        await _life.CancelAsync().ConfigureAwait(false);
        _local.Dispose();
        _nxFriends.Dispose();
        _nxPresence.Dispose();
        _nxLobbies.Dispose();
        _nxAvatars.Changed -= RaiseChanged;
        _nxAvatars.Dispose();
        _avatarHttp?.Dispose();
        _nextendo.Dispose();
        _devices.Dispose();
        _http.Dispose();
        Log.Dispose();
    }
}

/// <summary>
/// A wake-up that is remembered when nobody waits yet (tokio's Notify): a settings save between
/// two long-polls must not be lost.
/// </summary>
internal sealed class Signal : IDisposable
{
    public void Dispose() => _permit.Dispose();

    private readonly SemaphoreSlim _permit = new(0, 1);
    private readonly Lock _gate = new();

    public void Set()
    {
        lock (_gate)
        {
            if (_permit.CurrentCount == 0)
            {
                _permit.Release();
            }
        }
    }

    /// <summary>True when woken; false after <paramref name="timeout"/>.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancel) => _permit.WaitAsync(timeout, cancel);

    /// <summary>Forgets a wake-up nobody waited for.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            if (_permit.CurrentCount > 0)
            {
                _permit.Wait(0);
            }
        }
    }
}
