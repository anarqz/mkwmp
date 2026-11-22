// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core;
using Mkwmp.Core.Devices;
using Mkwmp.Core.Targets;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.Design;

/// <summary>
/// A launcher that only pretends (<c>--demo</c>, and the screenshot harness): a server with 12 of
/// 32 seats taken, a signed-in player with friends and requests, a found game, an update to
/// download. Nothing touches the network, the disk or the game. Every action answers after a
/// short delay, as the real one would.
/// </summary>
public sealed partial class DesignLauncher : ILauncher
{
    private static readonly string[] Visitors = ["Luna", "Mateus", "Rin", "Sofia", "Kai"];

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DesignDeviceLab _devices = new();
    private CancellationTokenSource? _play;
    private int _visitor;

    public event EventHandler? Changed;

    public event EventHandler<Toast>? Toasted;

    public string Version => "1.4.0";

    /// <summary>
    /// The demo's starting point: <c>--demo</c> = online and set up; <c>--demo-offline</c> = set up,
    /// no server (online features locked); <c>--demo-fresh</c> = a first start (no profile, no
    /// game, no server: the welcome flow).
    /// </summary>
    public DesignLauncher(DemoStart start = DemoStart.Online)
    {
        Start = start;
        if (start == DemoStart.Offline)
        {
            Settings = Settings with { Server = "", Pin = "", AdultConsent = false };
            SavedServers = [new SavedServer(DemoServer, "Alex", true)]; // disconnected: the PIN is kept for that server
        }
        else if (start == DemoStart.Fresh)
        {
            // A bare machine: no name, no game (the pinned runtime not installed), no server.
            Settings = new LauncherSettings { Server = "", Ui = new UiSettings { SetupDone = false } };
            Game = Game with { Install = null, Check = null, Integration = null };
            Runtime = Runtime with { State = RuntimeState.NotInstalled, InstalledVersion = null, Folder = @"C:\Users\Alex\AppData\Local\MKWMP\mkw-recomp" };
            GameSetup = GameSetupView.None;
        }
        Sync();
    }

    private const string DemoServer = "https://mkw.example.net";
    private const string DemoPin = "482913";

    /// <summary>A problem a <c>--state</c> put there: it stays whatever the settings say.</summary>
    private string? _pretended;
    private bool _noPolicy;

    /// <summary>
    /// What the real launcher derives from its settings, in short: with no server the launcher is
    /// offline (no profile on any server, no friends, the server never asked, PLAY asks only for
    /// the game); with one, the consent, the gamertag and its PIN come first.
    /// </summary>
    private void Sync()
    {
        var s = Settings;
        var online = !string.IsNullOrWhiteSpace(s.Server);
        var named = s.Name.Length is >= 3 and <= 16 && s.Name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        var signedIn = online && named && s.Pin.Length == 6;
        if (!online)
        {
            Server = new ServerView(ServerState.NotAsked, null, 0, 0, 0, null);
            Friends = new FriendsView(false, [], [], [], null, false, false);
            Profile = new ProfileView(s.Name, "", named, false, Profile.Register == RegisterState.Failed ? RegisterState.Failed : RegisterState.Idle,
                named ? CoreStrings.LocalNameOnly : CoreStrings.PickLocalName, false);
        }
        else
        {
            if (Server.State == ServerState.NotAsked)
            {
                Server = new ServerView(ServerState.Online, "MKWMP", 32, 12, 0, null);
            }
            if (Profile.Register is not (RegisterState.Working or RegisterState.Taken or RegisterState.Deleted) || signedIn)
            {
                Profile = new ProfileView(s.Name, s.Pin, named, signedIn, RegisterState.Idle, signedIn ? CoreStrings.KeepYourPin : CoreStrings.RegisterToPlay, false);
            }
            if (!signedIn || !s.AdultConsent)
            {
                // friends are the server's, and it gives none before the consent
                Friends = new FriendsView(false, [], [], [], null, false, false);
            }
            else if (!Friends.Loaded)
            {
                Friends = DemoFriends;
            }
        }
        var problem = _pretended
            ?? (online ? !named ? CoreStrings.PickGamertag : !signedIn ? CoreStrings.NeedPin : !s.AdultConsent ? CoreStrings.NoConsent : null : null)
            ?? (Game.Install is null ? CoreStrings.SetGameFolder("the one with mkw.exe") : null)
            ?? (Runtime.State == RuntimeState.UpdateAvailable ? CoreStrings.RuntimeUpdateNeeded("mkw-recomp", DemoRuntime.Version) : Runtime.Busy ? CoreStrings.RuntimeBusy("mkw-recomp") : null)
            ?? GameSetup.State switch
            {
                GameSetupState.Running => CoreStrings.SetupRunning,
                GameSetupState.NeedsSetup or GameSetupState.Failed => GameSetup.Message ?? CoreStrings.SetupNotSetUp,
                _ => null,
            }
            ?? Game.Integration?.Problem;
        // as the real one: what blocks PLAY is never something the checklist does not show
        PlayProblem = problem ?? PlayChecklist.Build(Facts(null)).Blocking.FirstOrDefault()?.Status;
        if (!Session.Busy)
        {
            Session = problem is null
                ? new SessionView(SessionState.Idle, null, 0, false, false, online ? CoreStrings.Ready : CoreStrings.ReadyOffline, "plain")
                : new SessionView(SessionState.Idle, null, 0, false, false, problem.ToUpperInvariant(), "warn");
        }
    }

    public DemoStart Start { get; }

    public LauncherSettings Settings { get; private set; } = new()
    {
        Server = "https://mkw.example.net",
        Name = "Alex",
        Pin = "482913",
        Game = @"C:\Games\Mario Kart World\mkw-recomp",
        AdultConsent = true,
        Ui = new UiSettings { SetupDone = true },
    };

    public ServerView Server { get; private set; } = new(ServerState.Online, "MKWMP", 32, 12, 0, null);

    public ProfileView Profile { get; private set; } = new("Alex", "482913", true, true, RegisterState.Idle, null, false);

    public SessionView Session { get; private set; } = new(SessionState.Idle, null, 0, false, false, CoreStrings.Ready, "plain");

    public FriendsView Friends { get; private set; } = DemoFriends;

    private static readonly FriendsView DemoFriends = new(
        true,
        [
            new Friend("Luna", FriendState.Playing), new Friend("Kai", FriendState.Playing),
            new Friend("Mateus", FriendState.Online), new Friend("Sofia", FriendState.Online), new Friend("Rin", FriendState.Online),
            new Friend("Jonas", FriendState.Offline), new Friend("Bea", FriendState.Offline), new Friend("Theo", FriendState.Offline),
        ],
        [new FriendRequest("Nico", DateTimeOffset.Now.AddMinutes(-12)), new FriendRequest("Yuki", DateTimeOffset.Now.AddHours(-3))],
        [new FriendRequest("Carol", DateTimeOffset.Now.AddDays(-1))],
        null, false, false);

    public GameView Game { get; private set; } = new(
        [new TargetInfo("mkw-recomp", "mkw-recomp", "the folder with mkw.exe",
            TargetCapabilities.Friends | TargetCapabilities.VoiceChat | TargetCapabilities.Camera | TargetCapabilities.FriendRooms | TargetCapabilities.Launch) { GameVersions = ["1.8.0"] }],
        new GameInstall("mkw-recomp", @"C:\Games\Mario Kart World\mkw-recomp", @"C:\Games\Mario Kart World\mkw-recomp\mkw.exe", "0.1.17", "1.8.0", null),
        new TargetCheck(TargetStatus.Ready, null, null, null, null),
        DemoMod,
        false, false, null, false);

    /// <summary>The MKWMP mod, installed and verified: what an mkw-recomp that loads mods reports.</summary>
    private static readonly IntegrationReport DemoMod = new(IntegrationState.Installed, CoreStrings.PatchState("mod_installed", "1.4.0"),
        [@"mods/mkwmp: v1.4.0 (C:\Games\Mario Kart World\mkw-recomp\mods\mkwmp)", "mkw.ini [launcher] mods: enabled", "files: 7 of 7 verified against v1.4.0"]) { AsMod = true };

    /// <summary><c>--state install-fails</c>: INSTALL ends with the error of a launcher that has no package for its version.</summary>
    private bool _installFails;

    public VoiceView Voice { get; private set; } = new(
        [new AudioDevice("mic-1", "Headset Microphone"), new AudioDevice("mic-2", "Webcam Microphone")],
        [new AudioDevice("out-1", "Headset"), new AudioDevice("out-2", "TV Speakers")],
        [new MutedPlayer("acc-51", "Shellshock"), new MutedPlayer("acc-77", "DriftKing99")]);

    public ReportView Report { get; private set; } = new(
        true, null,
        [new ReportReason("cheating", "Cheating"), new ReportReason("abuse", "Abusive voice or camera"), new ReportReason("name", "Offensive name"), new ReportReason("other", "Something else")],
        null, false, false);

    public UpdateView Update { get; private set; } = new(
        UpdateState.Available, "1.4.1",
        "• Voice & Camera: the camera now follows the room you are in\n• A new full-screen launcher made for controllers\n• Fixes for the Steam Deck's microphone after sleep\n• Smaller downloads: only what changed",
        0, null, "Version 1.4.1 is available");

    public int GitHubStars => 248;

    public IDeviceLab Devices => _devices;

    /// <summary>A real bridge nobody calls from a game: the harness asks through <c>RequestFromLauncher</c>.</summary>
    public Mkwmp.Core.Overlay.OverlayBridge OverlayBridge { get; } = new(TimeProvider.System);

    public string? PlayProblem { get; private set; }

    /// <summary>The play checklist of the pretend state, made by the real builder from the views as they are now.</summary>
    public PlayChecklist Checklist => PlayChecklist.Build(Facts(PlayProblem));

    private ChecklistFacts Facts(string? problem) => new(Settings, Server, Game, Runtime, GameSetup, Update, Version, problem);

    /// <summary>For screenshots and trying things (<c>--state</c>): another situation than the happy one.</summary>
    public void Pretend(string? state)
    {
        if (state is not null && state.Contains(',', StringComparison.Ordinal))
        {
            foreach (var one in state.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                Pretend(one); // several at once: --state named,need-dump
            }
            return;
        }
        var folder = @"C:\Games\Mario Kart World\mkw-recomp";
        var bare = new GameInstall("mkw-recomp", folder, folder + @"\mkw.exe", DemoRuntime.Version, null, null);
        var ready = new TargetCheck(TargetStatus.Ready, null, null, null, null);
        var noMod = new IntegrationReport(IntegrationState.Missing, CoreStrings.PatchState("missing"), [@"mods/mkwmp: not installed (C:\Games\Mario Kart World\mkw-recomp\mods\mkwmp)"]) { AsMod = true };
        switch (state)
        {
            case "crashed" or "crashed-solo" or "crashed-exit":
                PretendCrash(state);
                break;
            // ── the welcome flow's situations (with --demo-fresh) ──
            case "named":
                Change(() =>
                {
                    Settings = Settings with { Name = "Alex" };
                    Sync();
                });
                break;
            case "downloading":
                PretendRuntime(RuntimeState.Downloading);
                Change(Sync);
                break;
            case "download-failed":
                PretendRuntime(RuntimeState.Failed);
                break;
            // ── the game on its way to playable, after the welcome: home, Settings › Game, the card (--modal gamework) ──
            case "installing":
                PretendRuntime(RuntimeState.Installing, 0.8);
                Change(Sync);
                break;
            // a launcher update brought a newer mkw-recomp: PLAY is locked until it is in (--modal gameupdate: the flow itself)
            case "runtime-update":
                PretendRuntime(RuntimeState.UpdateAvailable);
                Change(Sync);
                break;
            // the runtime changed: the game is set up again from the copy it was built from (no file is asked for)
            case "setup-again":
                PretendGameSetup(GameSetupState.NeedsSetup, GameSetupReason.RuntimeChanged);
                break;
            case "install-fails":
                _installFails = true;
                Change(() =>
                {
                    Game = Game with { Integration = noMod };
                    Sync();
                });
                break;
            case "dev-package":
                Change(() => Game = Game with
                {
                    Integration = new IntegrationReport(IntegrationState.Installed, CoreStrings.PatchState("mod_dev", "1.4.0-dev.646873e"),
                        [.. DemoMod.Lines.Take(2), "files: 7 of 7 verified against v1.4.0", "development package (unsigned): used because MKWMP_DEV_PACKAGE=1 or dev_package = true"]) { AsMod = true },
                });
                break;
            case "need-dump" or "setting-up" or "setup-failed":
                Change(() => (Game, Runtime) = (Game with { Install = bare, Check = ready, Integration = noMod }, Runtime with { State = RuntimeState.Installed, InstalledVersion = DemoRuntime.Version }));
                PretendGameSetup(state switch { "need-dump" => GameSetupState.NeedsSetup, "setting-up" => GameSetupState.Running, _ => GameSetupState.Failed });
                break;
            case "need-mod" or "game-ready":
                Change(() =>
                {
                    Game = Game with { Install = bare with { GameVersion = "1.8.0" }, Check = ready, Integration = state == "need-mod" ? noMod : DemoMod };
                    Runtime = Runtime with { State = RuntimeState.Installed, InstalledVersion = DemoRuntime.Version };
                    GameSetup = DemoSetupIdle;
                    Settings = Settings with { Game = folder };
                    Sync();
                });
                break;
            case "connected":
                // a server is chosen, no profile on it yet
                Change(() =>
                {
                    Settings = Settings with { AdultConsent = false, Server = DemoServer, Pin = "" };
                    Sync();
                });
                break;
            case "taken":
                Change(() =>
                {
                    Settings = Settings with { AdultConsent = false, Server = DemoServer, Name = "Luna", Pin = "" };
                    Sync();
                    Profile = new ProfileView("Luna", "", true, false, RegisterState.Taken, CoreStrings.NameIsRegistered, true);
                });
                break;
            case "signed-in":
                // registered on the server, the consent not given yet (it is asked for right after signing in)
                Change(() =>
                {
                    Settings = Settings with { AdultConsent = false, Server = DemoServer, Name = Settings.Name.Length > 0 ? Settings.Name : "Alex", Pin = DemoPin };
                    SavedServers = [new SavedServer(DemoServer, Settings.Name, true)];
                    Sync();
                });
                break;
            case "no-list":
                PretendServers(ServerListState.Off);
                break;
            case "list-cached":
                PretendServers(ServerListState.Cached);
                break;
            case "servers-hidden":
                PretendHidden();
                break;
            case "servers-unsorted": // "Show closest to me" off
                Change(() =>
                {
                    Settings = Settings with { ClosestServers = false };
                    Servers = Servers with { Items = Servers.Items.Count > 0 ? ShownServers() : [] };
                });
                break;
            case "registered":
                Change(() =>
                {
                    Settings = Settings with { AdultConsent = true, Server = DemoServer, Name = Settings.Name.Length > 0 ? Settings.Name : "Alex", Pin = DemoPin };
                    SavedServers = [new SavedServer(DemoServer, Settings.Name, true)];
                    Sync();
                });
                break;
            case "blocked":
                Change(() =>
                {
                    PlayProblem = "mkw-recomp 0.1.13 is too old for online play: MKWMP needs 0.1.15-mkwmp";
                    Game = Game with
                    {
                        Install = Game.Install! with { Version = "0.1.13" },
                        Check = new TargetCheck(TargetStatus.Unsupported, PlayProblem,
                            "Download the build below, extract it over your game folder (keep setup\\), then press PLAY again.",
                            new Uri("https://github.com/anarqz/mkw-recomp/releases"), "Get 0.1.15-mkwmp"),
                    };
                    _pretended = PlayProblem;
                });
                break;
            // ── the play checklist's situations (--page checklist) ──
            case "ready":
                Change(() => Update = new UpdateView(UpdateState.UpToDate, null, null, 0, null, CoreStrings.UpToDate));
                break;
            // the player's own mkw-recomp, one MKWMP does not run on: MKWMP's build goes beside it
            case "wrong-runtime":
                Change(() =>
                {
                    var problem = CoreStrings.RecompProblemOther("0.1.11", "0.1.15 or 0.1.16 or 0.1.16-mkwmp.1");
                    Game = Game with
                    {
                        Install = Game.Install! with { Version = "0.1.11" },
                        Check = new TargetCheck(TargetStatus.Unsupported, problem, null, null, null) { Gate = TargetGate.Runtime },
                    };
                    Runtime = Runtime with { InstalledVersion = "0.1.11", Foreign = true };
                    Sync();
                });
                break;
            // a game set up from another version of the player's copy
            case "wrong-version":
                Change(() =>
                {
                    var problem = CoreStrings.RecompGameProblem("1.0.0", ["1.8.0"]);
                    Game = Game with
                    {
                        Install = Game.Install! with { GameVersion = "1.0.0" },
                        Check = new TargetCheck(TargetStatus.Unsupported, problem, null, null, null) { Gate = TargetGate.GameVersion },
                    };
                    GameSetup = DemoSetupIdle with { GameVersion = "1.0.0" };
                    Sync();
                });
                break;
            // MKWMP's files changed in the mod folder, and an old winmm.dll beside mkw.exe
            case "mod-broken":
                Change(() =>
                {
                    Game = Game with
                    {
                        Integration = new IntegrationReport(IntegrationState.Damaged, CoreStrings.PatchState("damaged"),
                            [@"mods/mkwmp: v1.4.1 (C:\Games\Mario Kart World\mkw-recomp\mods\mkwmp)", "mkw.ini [launcher] mods: enabled", "plugin/mkwmp.dll: another version, or changed", "files: 6 of 7 verified against v1.4.1"])
                        { AsMod = true, LeftoverPatch = true },
                    };
                    Sync();
                });
                break;
            // the player updated their copy elsewhere since it was set up
            case "copy-changed":
                Change(() =>
                {
                    GameSetup = DemoSetupIdle with { State = GameSetupState.NeedsSetup, Reason = GameSetupReason.CopyChanged, Message = CoreStrings.SetupCopyChanged };
                    Sync();
                });
                break;
            case "offline":
                Change(() => Server = new ServerView(ServerState.Offline, null, 0, 0, 0, CoreStrings.Unreachable));
                break;
            case "signedout":
                Change(() =>
                {
                    PlayProblem = CoreStrings.PickGamertag;
                    Settings = Settings with { Name = "", Pin = "" };
                    Profile = new ProfileView("", "", false, false, RegisterState.Idle, CoreStrings.RegisterToPlay, false);
                    Friends = new FriendsView(false, [], [], [], null, false, false);
                    Session = Session with { Status = CoreStrings.PickGamertag.ToUpperInvariant(), Tone = "warn" };
                });
                break;
            case "nofriends":
                Change(() => Friends = new FriendsView(true, [], [], [], null, false, false));
                break;
            case "uptodate":
                Change(() => Update = new UpdateView(UpdateState.UpToDate, null, null, 0, null, CoreStrings.UpToDate));
                break;
            case "update-ready":
                Change(() => Update = Update with { State = UpdateState.ReadyToInstall, Progress = 1, Status = "Version 1.4.1 is downloaded and ready to install" });
                break;
            // a server from before 1.4.0: no GET /privacy
            case "no-policy":
                _noPolicy = true;
                break;
            case "queued":
                Change(() =>
                {
                    Session = new SessionView(SessionState.Running, SeatPhase.Queued, 2, true, false, CoreStrings.Queued(2), "warn");
                    Server = Server with { Seated = 32, Queued = 2 };
                });
                break;
            case "seated":
                Change(() =>
                {
                    Session = new SessionView(SessionState.Running, SeatPhase.Seated, 0, true, false, CoreStrings.SeatedHaveFun, "good");
                    Server = Server with { Seated = 13 };
                });
                break;
        }
    }

    public Task StartAsync(CancellationToken cancel)
    {
        _ = VisitAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    private async Task VisitAsync(CancellationToken cancel)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(14), cancel).ConfigureAwait(false);
            while (true)
            {
                var name = Visitors[_visitor++ % Visitors.Length];
                var playing = _visitor % 2 == 0;
                Toasted?.Invoke(this, new Toast(name, playing ? "is racing online now" : "is online", playing ? ToastKind.FriendPlaying : ToastKind.FriendOnline));
                await Task.Delay(TimeSpan.FromSeconds(24), cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // closed
        }
    }

    private void Change(Action change)
    {
        lock (_gate)
        {
            change();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static Task Pause(int milliseconds = 500) => Task.Delay(milliseconds);

    public Task SaveSettingsAsync(LauncherSettings settings)
    {
        Change(() =>
        {
            var before = Settings;
            // as the real one: a PIN belongs to one name on one server, and the one kept for a server comes back with it
            if ((settings.Server != before.Server || !string.Equals(settings.Name, before.Name, StringComparison.OrdinalIgnoreCase)) && settings.Pin == before.Pin)
            {
                var kept = SavedServers.Any(p => p.Address == settings.Server && string.Equals(p.Name, settings.Name, StringComparison.OrdinalIgnoreCase));
                settings = settings with { Pin = kept ? DemoPin : "" };
            }
            if (settings.Server != before.Server)
            {
                Server = new ServerView(ServerState.NotAsked, null, 0, 0, 0, null);
                Friends = new FriendsView(false, [], [], [], null, false, false);
                Profile = Profile with { Register = RegisterState.Idle };
            }
            Settings = settings;
            Sync();
        });
        return Task.CompletedTask;
    }

    public Task SetConsentAsync(bool adult) => SaveSettingsAsync(Settings with { AdultConsent = adult });

    /// <summary>Any address answers as an MKWMP server with 12 players in its 32 seats; one with "down", "other" or "old" in it shows that failure.</summary>
    public async Task<ServerProbe> ProbeServerAsync(string address)
    {
        var typed = (address ?? "").Trim();
        var url = typed.Length == 0 || typed.Contains("://", StringComparison.Ordinal) ? typed.TrimEnd('/') : "http://" + typed.TrimEnd('/');
        if (url.Length == 0)
        {
            return new ServerProbe(ServerProbeState.Invalid, url, null, 0, 0, 0, CoreStrings.ProbeInvalid);
        }
        await Pause(700).ConfigureAwait(false);
        return typed switch
        {
            _ when typed.Contains("down", StringComparison.OrdinalIgnoreCase) => new ServerProbe(ServerProbeState.Unreachable, url, null, 0, 0, 0, CoreStrings.ProbeUnreachable),
            _ when typed.Contains("other", StringComparison.OrdinalIgnoreCase) => new ServerProbe(ServerProbeState.NotMkwmp, url, null, 0, 0, 0, CoreStrings.ProbeNotMkwmp),
            _ when typed.Contains("old", StringComparison.OrdinalIgnoreCase) => new ServerProbe(ServerProbeState.TooOld, url, "MKWMP", 32, 12, 0, CoreStrings.ProbeTooOld),
            _ => new ServerProbe(ServerProbeState.Ok, url, "MKWMP", 32, 12, 0, null),
        };
    }

    public IReadOnlyList<SavedServer> SavedServers { get; private set; } = [];

    // ---- the beholder's list: twelve servers, near to far, one that does not answer, two not measured ----------
    private static readonly ListedServer[] DemoServers =
    [
        new("MKWMP", DemoServer, "sa-east", "1.4.0", 32, 12, 0, 23, true) { PublicKey = "demo-key-01" },
        new("Sunset Circuit Club", "https://sunset.example.org", "sa-east", "1.4.0", 24, 24, 3, 41, true) { PublicKey = "demo-key-02" },
        new("Kart Night EU", "https://kartnight.example.eu", "eu-west", "1.4.0", 32, 7, 0, 118, true) { PublicKey = "demo-key-03" },
        new("Slipstream Lounge", "https://slipstream.example.com", "us-east", "1.3.2", 16, 2, 0, 204, true) { PublicKey = "demo-key-04" },
        new("Meadow Drift", "https://meadow.example.net", "us-west", "1.4.0", 24, 9, 0, 212, true) { PublicKey = "demo-key-05" },
        new("Turnpike Racers", "https://turnpike.example.ca", "us-east", "1.4.0", 16, 16, 1, 221, true) { PublicKey = "demo-key-06" },
        new("Bazaar Grand Prix", "https://bazaar.example.de", "eu-central", "1.4.0", 32, 3, 0, 236, true) { PublicKey = "demo-key-07" },
        new("Burnout Garage", "https://burnout.example.es", "eu-south", "1.4.0", 12, 5, 0, 249, true) { PublicKey = "demo-key-08" },
        new("Stadium Nights", "https://stadium.example.au", "ap-southeast", "1.4.0", 32, 20, 0, 318, true) { PublicKey = "demo-key-09" },
        new("Beach Sprint", "https://beach.example.kr", "ap-northeast", "1.4.0", 16, 1, 0, null, null) { PublicKey = "demo-key-10" },
        new("Spaceport Kart Club", "https://spaceport.example.in", "ap-south", "1.3.2", 8, 0, 0, null, null) { PublicKey = "demo-key-11" },
        new("Mall Rush", "https://mallrush.example.jp", "ap-northeast", "1.4.0", 12, 0, 0, null, false) { PublicKey = "demo-key-12" },
    ];

    private int _measure;
    private IReadOnlyList<HiddenServer> _hiddenServers = [];

    public ServerListView Servers { get; private set; } = new(ServerListState.Ready, DemoServers, false, null);

    /// <summary>The demo's list without the hidden ones, in the real one's order (answered by latency, not measured, no answer).</summary>
    private ListedServer[] ShownServers(int turn = 0) => !Settings.ClosestServers
        // "Show closest to me" off: the directory's order, nothing measured
        ? [.. DemoServers.Where(s => !_hiddenServers.Any(h => h.Is(s))).Select(s => s with { LatencyMs = null, Online = null })]
        : [.. DemoServers.Where(s => !_hiddenServers.Any(h => h.Is(s)))
            .Select((s, i) => s.LatencyMs is { } ms && turn > 0 ? s with { LatencyMs = ms + ((turn * (i + 3)) % 7) - 3 } : s)
            .OrderBy(s => s.Online switch { true => 0, null => 1, _ => 2 }).ThenBy(s => s.LatencyMs ?? int.MaxValue)];

    public async Task SetClosestServersAsync(bool on)
    {
        await SaveSettingsAsync(Settings with { ClosestServers = on }).ConfigureAwait(false);
        Change(() => Servers = Servers with { Items = Servers.Items.Count > 0 ? ShownServers() : [], Hidden = _hiddenServers });
        if (on)
        {
            await MeasureServersAsync().ConfigureAwait(false);
        }
    }

    /// <summary>For screenshots: no beholder (only a typed address), or the list still loading.</summary>
    public void PretendServers(ServerListState state) =>
        Change(() => Servers = state switch
        {
            ServerListState.Off => ServerListView.Off with { Hidden = _hiddenServers },
            ServerListState.Loading => new ServerListView(state, [], true, null) { Hidden = _hiddenServers },
            ServerListState.Cached => new ServerListView(state, ShownServers(), false, CoreStrings.BeholderUnreachable) { Hidden = _hiddenServers },
            ServerListState.Failed => new ServerListView(state, [], false, CoreStrings.BeholderUnreachable) { Hidden = _hiddenServers },
            _ => new ServerListView(state, ShownServers(), false, null) { Hidden = _hiddenServers },
        });

    /// <summary>For screenshots (<c>--state servers-hidden</c>): the two nearest after MKWMP's own were hidden.</summary>
    public void PretendHidden()
    {
        _hiddenServers = [.. DemoServers.Skip(1).Take(2).Select(s => new HiddenServer(s.Id, s.Name, s.Address))];
        Change(() => Servers = Servers with { Items = Servers.Items.Count > 0 ? ShownServers() : [], Hidden = _hiddenServers });
    }

    public Task<bool> HideServerAsync(ListedServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (OnlineNow && server.Address == Settings.Server)
        {
            return Task.FromResult(false);
        }
        _hiddenServers = [.. _hiddenServers.Where(h => h.Id != server.Id), new HiddenServer(server.Id, server.Name, server.Address)];
        Change(() => Servers = Servers with { Items = Servers.Items.Count > 0 ? ShownServers() : [], Hidden = _hiddenServers });
        return Task.FromResult(true);
    }

    public Task ShowServerAsync(string id)
    {
        _hiddenServers = [.. _hiddenServers.Where(h => h.Id != id)];
        Change(() => Servers = Servers with { Items = Servers.State is ServerListState.Ready or ServerListState.Cached ? ShownServers() : [], Hidden = _hiddenServers });
        return Task.CompletedTask;
    }

    private bool OnlineNow => !string.IsNullOrWhiteSpace(Settings.Server);

    /// <summary>The game's cover for the Play card: none in the demo, unless a screenshot gives one (<c>--cover file</c>).</summary>
    public string? GameCover { get; private set; }

    public void PretendCover(string? path) => Change(() => GameCover = path);

    public async Task RefreshServersAsync()
    {
        if (Servers.State == ServerListState.Off || Servers.Busy)
        {
            return;
        }
        Change(() => Servers = Servers with { Busy = true });
        await Pause(900).ConfigureAwait(false);
        Jitter();
    }

    public async Task MeasureServersAsync()
    {
        if (!Settings.ClosestServers || Servers.Items.Count == 0 || Servers.Busy)
        {
            return;
        }
        Change(() => Servers = Servers with { Busy = true });
        await Pause(500).ConfigureAwait(false);
        Jitter();
    }

    /// <summary>A new measure: every latency moves a little, as real ones do.</summary>
    private void Jitter()
    {
        var turn = ++_measure;
        Change(() => Servers = Servers with { Busy = false, Items = ShownServers(turn), Hidden = _hiddenServers });
    }
    // ---- the server's own privacy policy, as Markdown ------------------------------------
    private const string DemoPrivacy =
        "# Privacy Policy\n\n"
        + "*Example Kart Club, updated on 2 November 2026.*\n\n"
        + "This server is run by volunteers. Online play, friends and voice chat are for adults: you must be **18 or older**.\n\n"
        + "## What we keep\n\n"
        + "- your **gamertag**\n- a hash of your PIN (never the PIN itself)\n- your friends list\n- reports you file, until they are reviewed\n\n"
        + "## What we never keep\n\n"
        + "1. Voice: it is relayed live to the players in your room, and never recorded.\n"
        + "2. Camera: shown only to mutual friends in your room, and never recorded.\n"
        + "3. Your game: your copy stays on your device.\n\n"
        + "## Deleting your data\n\n"
        + "Use *Settings, Online, Delete My Profile on This Server*. Unused gamertags are deleted after a month.\n\n"
        + "> If something here is unclear, ask before you agree.\n\n"
        + "## Contact\n\n"
        + "Write to the host at [mkw.example.net/contact](https://mkw.example.net/contact). The server's source is `mkwmp-server 1.4.0`.\n";

    public async Task<PrivacyPolicy> GetPrivacyPolicyAsync()
    {
        if (!((ILauncher)this).OnlineEnabled)
        {
            return PrivacyPolicy.None(CoreStrings.PrivacyNoServer);
        }
        await Pause(400).ConfigureAwait(false);
        if (_noPolicy)
        {
            return PrivacyPolicy.None(CoreStrings.PrivacyNone);
        }
        return new PrivacyPolicy(DemoPrivacy, "3f6c0a1d9b2e47c58a6f1d0e9c7b5a43210fedcba9876543210fedcba9876543", true, Settings.Server + "/privacy", null);
    }

    public async Task RegisterAsync(string name)
    {
        if (!((ILauncher)this).OnlineEnabled)
        {
            Change(() => Profile = Profile with { Register = RegisterState.Failed, Message = CoreStrings.OnlineOff, MessageIsError = true });
            return;
        }
        Change(() =>
        {
            Settings = Settings with { Name = name, Pin = "" };
            Profile = new ProfileView(name, "", true, false, RegisterState.Working, null, false);
        });
        await Pause(900).ConfigureAwait(false);
        Change(() =>
        {
            // a name of the demo's friends is someone's: the page asks for its PIN ("It's mine")
            if (DemoFriends.Friends.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Profile = new ProfileView(name, "", true, false, RegisterState.Taken, CoreStrings.NameIsRegistered, true);
                return;
            }
            Settings = Settings with { Name = name, Pin = DemoPin };
            SavedServers = [.. SavedServers.Where(p => p.Address != Settings.Server), new SavedServer(Settings.Server, name, true)];
            Profile = Profile with { Register = RegisterState.Idle };
            Sync();
        });
    }

    public async Task UsePinAsync(string name, string pin)
    {
        Change(() => Profile = Profile with { Register = RegisterState.Working });
        await Pause(700).ConfigureAwait(false);
        Change(() =>
        {
            Settings = Settings with { Name = name, Pin = pin };
            Profile = Profile with { Register = RegisterState.Idle };
            Sync();
        });
    }

    public async Task DeleteProfileAsync()
    {
        await Pause(700).ConfigureAwait(false);
        Change(() =>
        {
            SavedServers = [.. SavedServers.Where(p => p.Address != Settings.Server)];
            Settings = Settings with { Name = "", Pin = "" };
            Sync();
            Profile = new ProfileView("", "", false, false, RegisterState.Deleted, CoreStrings.ProfileDeleted, false);
        });
    }

    public async Task FindGameAsync()
    {
        Change(() => Game = Game with { Searching = true });
        await Pause(1200).ConfigureAwait(false);
        Change(() => Game = Game with { Searching = false });
    }

    public async Task<bool> ChooseGameFolderAsync(string folder)
    {
        await Pause(400).ConfigureAwait(false);
        return true;
    }

    // The file picker judges with mkw-recomp's real rules, so the demo refuses what the launcher would.
    private static readonly IGameTarget PickerTarget = new Mkwmp.Core.Targets.MkwRecomp.MkwRecompTarget("demo");

    public Mkwmp.Core.Files.PickFilter GameFolderFilter { get; } = new() { FolderMatches = PickerTarget.LooksLikeInstall };

    public string? GameFolderProblem(string folder) =>
        PickerTarget.LooksLikeInstall(folder) ? null : CoreStrings.NotAGameFolder(PickerTarget.FolderHint);

    public Mkwmp.Core.Files.PickFilter GameCopyFilter => PickerTarget.GameCopyFilter;

    public string? GameCopyProblem(string path) => PickerTarget.GameCopyProblem(path);

    public async Task InstallIntegrationAsync()
    {
        Change(() => Game = Game with { Working = true, Note = null, NoteIsError = false });
        await Pause(1500).ConfigureAwait(false);
        Change(() =>
        {
            Game = _installFails
                ? Game with { Working = false, Note = CoreStrings.PackageMissing(Version), NoteIsError = true }
                : Game with { Working = false, Integration = DemoMod, Note = CoreStrings.ModInstalled, NoteIsError = false };
            Sync();
        });
    }

    public async Task UninstallIntegrationAsync()
    {
        Change(() => Game = Game with { Working = true });
        await Pause(1000).ConfigureAwait(false);
        Change(() =>
        {
            Game = Game with
            {
                Working = false, Note = CoreStrings.PatchRemoved, NoteIsError = false,
                Integration = new IntegrationReport(IntegrationState.Missing, CoreStrings.PatchState("missing"), ["mods/mkwmp: not installed"]) { AsMod = true },
            };
            Sync();
        });
    }

    public async Task PlayAsync()
    {
        if (Session.Busy)
        {
            return;
        }
        var play = new CancellationTokenSource();
        _play = play;
        try
        {
            if (!((ILauncher)this).OnlineEnabled)
            {
                // no server: the game alone, no seat
                Change(() => Session = new SessionView(SessionState.Starting, null, 0, false, false, CoreStrings.StartingGame, "info"));
                await Task.Delay(1500, play.Token).ConfigureAwait(false);
                Change(() => Session = new SessionView(SessionState.Running, null, 0, true, false, CoreStrings.PlayingOffline, "good"));
                return;
            }
            Change(() => Session = new SessionView(SessionState.Starting, null, 0, false, false, CoreStrings.Connecting, "info"));
            await Task.Delay(1500, play.Token).ConfigureAwait(false);
            Change(() => Session = new SessionView(SessionState.Running, SeatPhase.Joining, 0, true, false, CoreStrings.TakingASeat, "info"));
            await Task.Delay(1500, play.Token).ConfigureAwait(false);
            Change(() =>
            {
                Session = new SessionView(SessionState.Running, SeatPhase.Queued, 2, true, false, CoreStrings.Queued(2), "warn");
                Server = Server with { Queued = 2 };
            });
            await Task.Delay(1800, play.Token).ConfigureAwait(false);
            Change(() =>
            {
                Session = new SessionView(SessionState.Running, SeatPhase.Seated, 0, true, false, CoreStrings.SeatedHaveFun, "good");
                Server = Server with { Queued = 0, Seated = 13 };
            });
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    public Task StopAsync()
    {
        _play?.Cancel();
        _play?.Dispose();
        _play = null;
        Change(() =>
        {
            Session = new SessionView(SessionState.Idle, null, 0, false, false, CoreStrings.Ready, "plain");
            if (Server.State == ServerState.Online)
            {
                Server = Server with { Queued = 0, Seated = 12 };
            }
            Sync();
        });
        return Task.CompletedTask;
    }

    public async Task AddFriendAsync(string name)
    {
        Change(() => Friends = Friends with { Busy = true });
        await Pause().ConfigureAwait(false);
        Change(() => Friends = Friends with
        {
            Busy = false,
            Outgoing = [.. Friends.Outgoing, new FriendRequest(name, DateTimeOffset.Now)],
            Note = CoreStrings.RequestSent(name),
            NoteIsError = false,
        });
    }

    public async Task AcceptFriendAsync(string name)
    {
        await Pause(300).ConfigureAwait(false);
        Change(() => Friends = Friends with
        {
            Incoming = [.. Friends.Incoming.Where(r => r.Name != name)],
            Friends = [.. Friends.Friends, new Friend(name, FriendState.Online)],
        });
    }

    public async Task RemoveFriendAsync(string name, FriendRemoval why)
    {
        await Pause(300).ConfigureAwait(false);
        Change(() => Friends = Friends with
        {
            Incoming = [.. Friends.Incoming.Where(r => r.Name != name)],
            Outgoing = [.. Friends.Outgoing.Where(r => r.Name != name)],
            Friends = [.. Friends.Friends.Where(f => f.Name != name)],
        });
    }

    public Task UnmuteAsync(string account)
    {
        Change(() => Voice = Voice with { Mutes = [.. Voice.Mutes.Where(m => m.Account != account)] });
        return Task.CompletedTask;
    }

    public async Task SendReportAsync(string name, string reason, string? details)
    {
        Change(() => Report = Report with { Busy = true });
        await Pause(800).ConfigureAwait(false);
        Change(() => Report = Report with { Busy = false, Open = new OpenReport(name, reason, details, DateTimeOffset.Now), Note = "Report sent.", NoteIsError = false });
    }

    public Task WithdrawReportAsync()
    {
        Change(() => Report = Report with { Open = null, Note = "Report withdrawn." });
        return Task.CompletedTask;
    }

    public async Task CheckForUpdatesAsync()
    {
        var found = Update;
        Change(() => Update = Update with { State = UpdateState.Checking, Status = "Checking for updates…" });
        await Pause(1200).ConfigureAwait(false);
        Change(() => Update = found with { State = found.AvailableVersion is null ? UpdateState.UpToDate : UpdateState.Available });
    }

    private const long DemoUpdateBytes = 78L * 1024 * 1024;

    public async Task DownloadUpdateAsync(CancellationToken cancel)
    {
        const int Steps = 160;
        try
        {
            for (var i = 0; i <= Steps; i++)
            {
                var fraction = (double)i / Steps;
                var left = TimeSpan.FromSeconds((Steps - i) * 0.05 * 60); // pretend minutes, so the text is worth reading
                Change(() => Update = Update with
                {
                    State = UpdateState.Downloading, Progress = fraction, Remaining = left, Status = "Downloading 1.4.1…",
                    Received = (long)(fraction * DemoUpdateBytes), Total = DemoUpdateBytes, BytesPerSecond = 2.4 * 1024 * 1024,
                });
                await Task.Delay(50, cancel).ConfigureAwait(false);
            }
            ReadyToInstall();
        }
        catch (OperationCanceledException)
        {
            Change(() => Update = Update with { State = UpdateState.Available, Progress = 0, Remaining = null, Status = "Version 1.4.1 is available" });
        }
    }

    public async Task ApplyUpdateAsync(CancellationToken cancel)
    {
        if (Update.State != UpdateState.ReadyToInstall)
        {
            await DownloadUpdateAsync(cancel).ConfigureAwait(false);
        }
        if (Update.State != UpdateState.ReadyToInstall)
        {
            return;
        }
        if (Session.Busy || Session.GameRunning)
        {
            Change(() => Update = Update with { GameBlocks = true, Status = "Ready to install: close the game first" });
            return;
        }
        Change(() => Update = Update with { State = UpdateState.Restarting, Progress = 1, Remaining = null, Status = "Installing 1.4.1; MKWMP restarts… (not in the demo)" });
        await Task.Delay(2500, CancellationToken.None).ConfigureAwait(false);
        Change(() => Update = new UpdateView(UpdateState.UpToDate, null, null, 0, null, "Up to date"));
    }

    public void ContinueWithoutUpdate() => Change(() => Update = Update with { Required = false });

    /// <summary>For screenshots: the update as a real launcher has it, required (--modal update | ready | ready-game | update-failed).</summary>
    public void RequireUpdate(string? failure = null) => Change(() => Update = failure is null
        ? Update with { Required = true }
        : Update with { State = UpdateState.Failed, Required = true, Progress = 0, Remaining = null, Status = failure });

    public void InstallUpdateAfterGame(bool on) =>
        Change(() => Update = Update with { AfterGame = on, GameBlocks = on || Session.Busy || Session.GameRunning, Status = on ? "Will be installed when the game closes" : "Version 1.4.1 is downloaded and ready to install" });

    /// <summary>For screenshots: the download frozen at a fraction.</summary>
    public void FreezeDownloadAt(double fraction) =>
        Change(() => Update = Update with
        {
            State = UpdateState.Downloading, Progress = fraction, Remaining = TimeSpan.FromSeconds(20), Status = "Downloading 1.4.1…",
            Received = (long)(fraction * DemoUpdateBytes), Total = DemoUpdateBytes, BytesPerSecond = 2.4 * 1024 * 1024,
        });

    /// <summary>For screenshots: the update downloaded and verified (<paramref name="gameRuns"/>: with a game running).</summary>
    public void ReadyToInstall(bool gameRuns = false) =>
        Change(() => Update = Update with
        {
            State = UpdateState.ReadyToInstall, Progress = 1, Remaining = null, GameBlocks = gameRuns, AfterGame = false,
            Status = gameRuns ? "Ready to install: close the game first" : "Version 1.4.1 is downloaded and ready to install",
        });

    public async Task<string> SaveBugReportAsync()
    {
        await Pause(900).ConfigureAwait(false);
        return @"C:\Users\Alex\Desktop\mkwmp-report-demo.zip";
    }

    // ---- crashes and "Found a bug?": a crash on demand (--state crashed | crashed-solo | crashed-exit), a made-up report

    public Mkwmp.Core.Diagnostics.CrashView? PendingCrash { get; private set; }

    /// <summary>The game "crashed": online in a room of four as the host, offline with no room, or with only an exit code.</summary>
    public void PretendCrash(string kind) => Change(() => PendingCrash = kind switch
    {
        "crashed-solo" => new("20261120T183245Z-9164", "ACCESS_VIOLATION in mkwgame.dll+0x1a2f4c0", DemoCrashTime, null, null, true, true, 0),
        "crashed-exit" => new("exit-20261120T183245Z-c0000409", "exit code 0xC0000409 (FAIL_FAST)", DemoCrashTime, null, null, false, false, 0),
        _ => new("20261120T183245Z-9164", "ACCESS_VIOLATION in mkwgame.dll+0x1a2f4c0", DemoCrashTime, 4, true, true, true, 2),
    });

    private static DateTimeOffset DemoCrashTime => new(2026, 11, 20, 18, 32, 45, TimeSpan.Zero);

    public async Task<Mkwmp.Core.Diagnostics.BugReport> PrepareReportAsync(string? crashId)
    {
        await Pause(700).ConfigureAwait(false);
        var crash = crashId is not null;
        var zip = crash ? $@"C:\Users\Alex\Desktop\mkwmp-crash-{crashId}.zip" : @"C:\Users\Alex\Desktop\mkwmp-bug-20261120-183245.zip";
        var title = crash ? "Crash: ACCESS_VIOLATION in mkwgame.dll+0x1a2f4c0 (4 players, host)" : "";
        var text = crash ? "**Mario Kart World stopped unexpectedly.**\n\n- **Crash**: `ACCESS_VIOLATION in mkwgame.dll+0x1a2f4c0`\n" : "**What happened?**\n";
        var url = Mkwmp.Core.Diagnostics.BugReportBuilder.Url(crash ? "crash.yml" : "bug_report.yml", crash ? "crash" : "bug", title, crash ? "report" : "what", crash ? text : "");
        return new(crash, title, url, text, zip, true, null);
    }

    public Task DismissCrashAsync(string id)
    {
        Change(() => PendingCrash = PendingCrash?.Id == id ? null : PendingCrash);
        return Task.CompletedTask;
    }

    public void ShowTestToast() => Toasted?.Invoke(this, new Toast("Luna", "is racing online now", ToastKind.FriendPlaying));

    // ---- the game's own settings: a few pages with every kind of row --------------------------

    /// <summary>The sample below in the language in use, through the catalogs the real schema uses (gs.*), as the game's own settings are.</summary>
    private static GameSettingsSchema DemoGameSettings
    {
        get
        {
            var generation = CoreStrings.Generation;
            if (_demoGameSettings is not { } cached || cached.Generation != generation)
            {
                var localized = Mkwmp.Core.Targets.MkwRecomp.RecompSettings.Localize(DemoGameSettingsEnglish) with { Note = Mkwmp.Core.Targets.MkwRecomp.RecompSettings.NextStart };
                localized = localized with
                {
                    Categories = [.. localized.Categories.Select(c => c with
                    {
                        Settings = [.. c.Settings.Select(s => s.Locked is null ? s : s with { Locked = CoreStrings.GameSettingsLockedByEnv("MKW_" + s.Id.ToUpperInvariant()) })],
                    })],
                };
                _demoGameSettings = cached = (generation, localized);
            }
            return cached.Schema;
        }
    }

    private static (int Generation, GameSettingsSchema Schema)? _demoGameSettings;

    private static readonly GameSettingsSchema DemoGameSettingsEnglish = new(
    [
        new("screen", "Screen", "Fullscreen",
        [
            GameSetting.Toggle("fullscreen", "Fullscreen", "Fills the screen with the game; F11 toggles it.", false, false),
            GameSetting.Choice("window_size", "Window size", "Size of the game window when not fullscreen.", "1920x1080", false,
                new("1280x720", "1280 × 720"), new("1600x900", "1600 × 900"), new("1920x1080", "1920 × 1080"), new("2560x1440", "2560 × 1440"))
                with { VisibleWhen = new("fullscreen", ["0"]) },
            GameSetting.Choice("vk_present", "Presentation", "How finished frames reach the screen.", "mailbox", false,
                new("mailbox", "Mailbox"), new("fifo", "VSync"), new("immediate", "Immediate")),
            GameSetting.Choice("aspect", "Aspect ratio", "Wider than 16:9 shows more of the course at the sides, for ultrawide screens.", "16:9", true,
                new("16:9", "16:9"), new("window", "Window"), new("21:9", "21:9"), new("32:9", "32:9")) with { Group = "Picture" },
            GameSetting.Choice("hdr", "HDR", "Automatic shows the game's own HDR picture when the display is in HDR.", "auto", false,
                new("auto", "Automatic"), new("off", "Off")) with { Group = "Picture" },
        ]),
        new("graphics", "Graphics", "Star",
        [
            GameSetting.Choice("quality", "Quality", "Sets the settings below at once. Console looks like the Switch 2.", "console", true,
                new("low", "Low", new Dictionary<string, string> { ["tex_quality"] = "quarter", ["shadow_size"] = "1024" }),
                new("console", "Console", new Dictionary<string, string> { ["tex_quality"] = "full", ["shadow_size"] = "console" }),
                new("high", "High", new Dictionary<string, string> { ["tex_quality"] = "full", ["shadow_size"] = "4096" }),
                new("custom", "Custom")),
            GameSetting.Choice("tex_quality", "Texture quality", "Detail of the game's own textures.", "full", true,
                new("full", "Full"), new("half", "Half"), new("quarter", "Quarter")),
            GameSetting.Choice("shadow_size", "Shadow quality", "Size of the map the game draws shadows from.", "console", true,
                new("console", "Console"), new("1024", "1024"), new("2048", "2048"), new("4096", "4096")),
            GameSetting.Choice("resolution", "Internal resolution", "Lines the game renders its scenes at, then scaled to the window. Lower is faster.", "original", true,
                new("original", "Original"), new("720", "720p"), new("1080", "1080p"), new("1440", "1440p"), new("2160", "2160p")) with { Group = "Resolution" },
            GameSetting.Choice("upscaler", "Scaling to the window", "FSR keeps edges and detail sharper than bilinear.", "bilinear", false,
                new("fsr", "AMD FSR 1"), new("bilinear", "Bilinear")) with { Group = "Resolution" },
            GameSetting.Range("sharpness", "FSR sharpness", "Sharpening after FSR scaling; 0 turns it off.", 80, false, 0, 100, 5, "%")
                with { Group = "Resolution", VisibleWhen = new("upscaler", ["fsr"]) },
            // mkw-recomp 0.1.17's DLSS / FSR 3.1 rows (RecompSettings.Has017Rows)
            GameSetting.Choice("temporal", "AI upscaling", "NVIDIA DLSS (RTX GPUs) or AMD FSR 3.1 (any GPU).", "off", false,
                new("off", "Off"), new("dlss", "NVIDIA DLSS"), new("fsr3", "AMD FSR 3.1")) with { Group = "Resolution" },
            GameSetting.Choice("temporal_quality", "Upscaling quality", "The share of the screen's resolution the race renders at.", "quality", true,
                new("native", "Native (DLAA)"), new("quality", "Quality"), new("balanced", "Balanced"), new("performance", "Performance"), new("ultra", "Ultra performance"))
                with { Group = "Resolution", VisibleWhen = new("temporal", ["dlss", "fsr3"]) },
            GameSetting.Choice("dlss_preset", "DLSS model", "NVIDIA's model DLSS uses.", "default", false,
                new("default", "Default"), new("j", "J"), new("k", "K"), new("l", "L"), new("m", "M")) with { Group = "Resolution", VisibleWhen = new("temporal", ["dlss"]) },
            GameSetting.Choice("fps", "Frame rate", "Frames per second the game aims for.", "auto", false,
                new("auto", "Automatic"), new("60", "60"), new("30", "30")) with { Group = "Performance", Locked = "Set by the environment variable MKW_FPS" },
            GameSetting.Choice("reflex", "NVIDIA Reflex", "NVIDIA GPUs only.", "off", false,
                new("off", "Off"), new("on", "On"), new("boost", "On + Boost")) with { Group = "Performance" },
        ]),
        new("sound", "Sound", "Volume",
        [
            GameSetting.Range("volume", "Volume", "Master volume of the game.", 100, false, 0, 100, 5, "%"),
            GameSetting.Choice("audio_channels", "Speakers", "Automatic gives 5.1 surround when the sound output has 6 channels or more.", "auto", true,
                new("auto", "Automatic"), new("2", "Stereo"), new("6", "5.1 surround")),
            GameSetting.Toggle("background_mute", "Mute in background", "No sound while the game window is not in front.", false, false),
        ]),
        new("controls", "Controls", "Gamepad",
        [
            GameSetting.Choice("pad_layout", "Button layout", "Which button confirms and accelerates, the same in the game and in this launcher.", "auto", false,
                new("auto", "Automatic"), new("nintendo", "Nintendo (A right)"), new("xbox", "Xbox (A bottom)")),
            GameSetting.Toggle("pad_p2", "First controller to player 2", "The first controller plays as player 2 and the keyboard as player 1.", false, false),
            GameSetting.Toggle("motion", "Motion controls", "Steering by tilting the controller.", true, false),
            GameSetting.Range("rumble_strength", "Vibration strength", "How strong the controller vibrates.", 130, false, 0, 300, 10, "%"),
            GameSetting.Range("rumble_curve", "Vibration curve", "Lower values make weak vibration stronger.", 0.6, false, 0.2, 1.5, 0.05),
            GameSetting.Toggle("dsu", "Motion from a DSU server", "The gyro of a controller that gives none to the game, from a Cemuhook / DSU server.", false, false),
            GameSetting.Text("dsu_server", "DSU server", "The DSU server's address and port.", "127.0.0.1:26760", false, 64, @"[A-Za-z0-9.\-]+:[0-9]{1,5}")
                with { VisibleWhen = new("dsu", ["1"]) },
        ]),
        new("advanced", "Advanced", "Settings",
        [
            GameSetting.Toggle("gpl", "Graphics pipeline library", "Uses the Vulkan pipeline library extension.", true, true) with { Group = "Shaders and pipelines" },
            GameSetting.Toggle("vk_validation", "Validation layer", "Checks every Vulkan call and slows the game down a lot.", false, true) with { Group = "Vulkan" },
        ], "These settings are for testing and troubleshooting. Change them only when you know why.", Advanced: true),
    ], "Changes apply the next time the game starts.");

    private readonly Dictionary<string, string> _gameValues = new(StringComparer.OrdinalIgnoreCase) { ["fullscreen"] = "1", ["resolution"] = "1080", ["volume"] = "85", ["temporal"] = "dlss" };
    private string? _gameSettingsNote;

    public GameSettingsView GameSettings
    {
        get
        {
            lock (_gate)
            {
                // as the real one: nothing can be changed while the game runs
                var running = Session.GameRunning;
                return new GameSettingsView(
                    DemoGameSettings,
                    DemoGameSettings.Effective(_gameValues),
                    new HashSet<string>(_gameValues.Keys, StringComparer.OrdinalIgnoreCase),
                    !running,
                    _gameSettingsNote ?? (running ? CoreStrings.GameSettingsRunning : null),
                    _gameSettingsNote is not null);
            }
        }
    }

    private Task ChangeGameSettings(Func<IReadOnlyDictionary<string, string?>> changes)
    {
        Change(() =>
        {
            try
            {
                if (Session.GameRunning)
                {
                    throw new LauncherException("Close the game first: it reads these settings when it starts");
                }
                foreach (var (id, value) in changes())
                {
                    if (DemoGameSettings.Find(id) is { Locked: { } why } locked)
                    {
                        throw new LauncherException($"{locked.Label}: {why}");
                    }
                    if (string.IsNullOrEmpty(value))
                    {
                        _gameValues.Remove(id);
                    }
                    else
                    {
                        _gameValues[id] = value;
                    }
                }
                _gameSettingsNote = null;
            }
            catch (LauncherException e)
            {
                _gameSettingsNote = e.Message;
            }
        });
        return Task.CompletedTask;
    }

    public Task SetGameSettingAsync(string id, string value) => ChangeGameSettings(() => DemoGameSettings.Changes(id, value));

    public Task ResetGameSettingAsync(string id) => ChangeGameSettings(() => DemoGameSettings.Changes(id, null));

    public Task ResetGameSettingsAsync() => ChangeGameSettings(() => _gameValues.Keys.ToDictionary(id => id, _ => (string?)null));

    // ---- the game's runtime (a pretend install of the pinned mkw-recomp) -------------------
    private static readonly RuntimeManifest DemoRuntime = Mkwmp.Core.Targets.MkwRecomp.RecompRuntime.Pinned;
    private CancellationTokenSource? _runtimeInstall;

    public RuntimeView Runtime { get; private set; } = new(
        RuntimeState.Installed, DemoRuntime.Version, DemoRuntime.Version, @"C:\Games\Mario Kart World\mkw-recomp", false,
        0, 0, 0, 0, null, DemoRuntime.Size, DemoRuntime.Size + DemoRuntime.InstalledSize + DemoRuntime.SetupSpace, null, DemoRuntime.Credit);

    /// <summary>For screenshots and trying things: the runtime as not installed, to update, or frozen mid-download.</summary>
    public void PretendRuntime(RuntimeState state, double fraction = 0.42)
    {
        var done = (long)(DemoRuntime.Size * fraction);
        Change(() => Runtime = state switch
        {
            RuntimeState.NotInstalled => Runtime with { State = state, InstalledVersion = null, Folder = @"C:\Users\Alex\AppData\Local\MKWMP\mkw-recomp", Note = null },
            RuntimeState.UpdateAvailable => Runtime with { State = state, InstalledVersion = "0.1.14-mkwmp", Note = null },
            RuntimeState.Downloading => Runtime with
            {
                State = state, Progress = fraction, BytesDone = done, BytesTotal = DemoRuntime.Size, BytesPerSecond = 6_300_000,
                Remaining = TimeSpan.FromSeconds((DemoRuntime.Size - done) / 6_300_000.0), Note = null,
            },
            RuntimeState.Failed => Runtime with { State = state, Note = CoreStrings.RuntimeBadDownload(DemoRuntime.Asset) },
            _ => Runtime with { State = state, Progress = fraction, Note = null },
        });
    }

    public async Task InstallRuntimeAsync(string? folder, CancellationToken cancel)
    {
        if (Runtime.Busy)
        {
            return;
        }
        var before = Runtime;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _lifetime.Token);
        _runtimeInstall = both;
        try
        {
            const int Steps = 120;
            const double Speed = 6_300_000;
            foreach (var (state, total) in new[] { (RuntimeState.Downloading, DemoRuntime.Size), (RuntimeState.Verifying, DemoRuntime.Size), (RuntimeState.Extracting, DemoRuntime.InstalledSize) })
            {
                var steps = state == RuntimeState.Downloading ? Steps : Steps / 4;
                for (var i = 0; i <= steps; i++)
                {
                    var done = total * i / steps;
                    Change(() => Runtime = Runtime with
                    {
                        State = state, Folder = folder ?? before.Folder, Progress = (double)done / total, BytesDone = done, BytesTotal = total,
                        BytesPerSecond = Speed, Remaining = TimeSpan.FromSeconds((total - done) / Speed), Note = null,
                    });
                    await Task.Delay(50, both.Token).ConfigureAwait(false);
                }
            }
            Change(() =>
            {
                Runtime = Runtime with
                {
                    State = RuntimeState.Installed, InstalledVersion = DemoRuntime.Version, Foreign = false, Progress = 0, BytesDone = 0, BytesTotal = 0,
                    BytesPerSecond = 0, Remaining = null, Note = CoreStrings.RuntimeInstalled(DemoRuntime.Name, DemoRuntime.Version),
                };
                if (Game.Install is null)
                {
                    // a new install: the game is not built yet, and MKWMP's files are not in it
                    var root = Runtime.Folder ?? @"C:\Games\mkw-recomp";
                    Game = Game with
                    {
                        Install = new GameInstall("mkw-recomp", root, root + @"\mkw.exe", DemoRuntime.Version, null, null),
                        Check = new TargetCheck(TargetStatus.Ready, null, null, null, null),
                        Integration = new IntegrationReport(IntegrationState.Missing, "Not installed: press INSTALL", []),
                    };
                    Settings = Settings with { Game = root };
                    GameSetup = DemoSetupIdle with { State = GameSetupState.NeedsSetup, Reason = GameSetupReason.NotSetUp, Message = CoreStrings.SetupNotSetUp, NeedsDump = true, DumpPath = null, GameVersion = null };
                }
                Sync();
            });
        }
        catch (OperationCanceledException)
        {
            Change(() => Runtime = before with { Note = CoreStrings.RuntimePaused });
        }
        finally
        {
            _runtimeInstall = null;
        }
    }

    public Task CancelRuntimeInstall()
    {
        if (_runtimeInstall is { } running)
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // it ended by itself
            }
        }
        else if (Runtime.State == RuntimeState.Failed || Runtime.Note is not null)
        {
            Change(() => Runtime = Runtime with { State = Runtime.InstalledVersion is null ? RuntimeState.NotInstalled : RuntimeState.Installed, Note = null });
        }
        return Task.CompletedTask;
    }

    // ---- the game's setup (a pretend run of mkw-recomp's, with its real steps and their measured share of the time) ----
    private const string DemoDump = @"D:\Games\Mario Kart World.nup";
    private static readonly (string Step, double Seconds)[] DemoSetupSteps =
        [("check", 4), ("exefs", 6), ("romfs", 1), ("generate", 160), ("compile", 186), ("shaders", 80), ("warm", 76), ("cleanup", 24)];

    private static readonly GameSetupView DemoSetupIdle = GameSetupView.None with { Available = true, DumpPath = DemoDump, GameVersion = "1.8.0" };
    private CancellationTokenSource? _gameSetup;

    public GameSetupView GameSetup { get; private set; } = DemoSetupIdle;

    /// <summary>The setup's view <paramref name="seconds"/> into a run on the reference machine.</summary>
    private static GameSetupView DemoSetupAt(double seconds)
    {
        var total = DemoSetupSteps.Sum(s => s.Seconds);
        var (index, before) = (0, 0.0);
        while (index < DemoSetupSteps.Length - 1 && before + DemoSetupSteps[index].Seconds <= seconds)
        {
            before += DemoSetupSteps[index++].Seconds;
        }
        var (step, length) = DemoSetupSteps[index];
        return DemoSetupIdle with
        {
            State = GameSetupState.Running,
            Step = step,
            StepLabel = CoreStrings.SetupStep(step, copy: false),
            StepIndex = index,
            StepCount = DemoSetupSteps.Length,
            StepProgress = Math.Clamp((seconds - before) / length, 0, 1),
            StepIndeterminate = step is "check" or "romfs" or "generate" or "cleanup",
            Progress = Math.Clamp(seconds / total, 0, 1),
            Remaining = TimeSpan.FromSeconds(Math.Max(0, total - seconds)),
        };
    }

    /// <summary>For screenshots and trying things: the setup as needed, frozen mid-run, failed or done.</summary>
    public void PretendGameSetup(GameSetupState state, GameSetupReason reason = GameSetupReason.NotSetUp, double fraction = 0.42)
    {
        var message = reason switch
        {
            GameSetupReason.Unfinished => CoreStrings.SetupUnfinished,
            GameSetupReason.RuntimeChanged => CoreStrings.SetupRuntimeChanged(DemoRuntime.Name, DemoRuntime.Version),
            GameSetupReason.GameMismatch => CoreStrings.SetupGameMismatch,
            GameSetupReason.DumpMissing => CoreStrings.SetupDumpGone(DemoDump),
            _ => CoreStrings.SetupNotSetUp,
        };
        var needsDump = reason is GameSetupReason.NotSetUp or GameSetupReason.DumpMissing;
        var needed = DemoSetupIdle with
        {
            State = GameSetupState.NeedsSetup, Reason = reason, Message = message, NeedsDump = needsDump,
            DumpPath = reason == GameSetupReason.NotSetUp ? null : DemoDump, GameVersion = reason == GameSetupReason.NotSetUp ? null : "1.8.0",
        };
        Change(() =>
        {
            GameSetup = state switch
            {
                GameSetupState.NeedsSetup => needed,
                GameSetupState.Running => DemoSetupAt(DemoSetupSteps.Sum(s => s.Seconds) * fraction),
                GameSetupState.Failed => needed with
                {
                    State = state, ErrorCode = "failed", LastLog = @"C:\Games\MKWMP\logs\setup-20261120-203015.log",
                    Message = CoreStrings.SetupError("failed", "compiling the game code failed: out of memory", CoreStrings.SetupStep("compile", false), 1),
                },
                GameSetupState.Done => DemoSetupIdle with { State = state, Message = CoreStrings.SetupDone },
                _ => DemoSetupIdle,
            };
            PlayProblem = GameSetup.State switch
            {
                GameSetupState.Running => CoreStrings.SetupRunning,
                GameSetupState.NeedsSetup or GameSetupState.Failed => message,
                _ => null,
            };
        });
    }

    public string? GameCopyChangeProblem(string? dumpPath) =>
        Runtime.Busy ? CoreStrings.SetupWaitForInstall
        : GameSetup.Busy ? CoreStrings.SetupAlreadyRunning
        : Session.Busy ? CoreStrings.PressStopFirst
        : Session.GameRunning ? CoreStrings.SetupCloseGame
        : dumpPath is null ? null
        : GameCopyProblem(dumpPath);

    /// <summary>The real one redoes only what depends on the copy (about 5 s from a folder to a .nup): here the same few steps, in four seconds.</summary>
    public async Task<string?> ChangeGameCopyAsync(string dumpPath, GameDataMode? mode, CancellationToken cancel)
    {
        if (await Task.Run(() => GameCopyChangeProblem(dumpPath), cancel).ConfigureAwait(false) is { } why)
        {
            return why;
        }
        var before = GameSetup;
        var data = mode ?? before.Mode;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _lifetime.Token);
        _gameSetup = both;
        try
        {
            string[] steps = ["check", "exefs", "romfs", "cleanup"];
            for (var i = 0; i < 40; i++)
            {
                var (index, at) = (i / 10, i);
                Change(() =>
                {
                    GameSetup = DemoSetupIdle with
                    {
                        State = GameSetupState.Running, Step = steps[index], StepLabel = CoreStrings.SetupStep(steps[index], data == GameDataMode.Copy),
                        StepIndex = index, StepCount = steps.Length, StepProgress = (at % 10) / 10.0, Progress = at / 40.0,
                        Remaining = TimeSpan.FromSeconds((40 - at) / 10.0), DumpPath = dumpPath, Mode = data,
                    };
                    PlayProblem = CoreStrings.SetupRunning;
                });
                await Task.Delay(100, both.Token).ConfigureAwait(false);
            }
            Change(() =>
            {
                GameSetup = DemoSetupIdle with { State = GameSetupState.Done, Message = CoreStrings.SetupDone, DumpPath = dumpPath, Mode = data };
                Sync();
            });
        }
        catch (OperationCanceledException)
        {
            Change(() =>
            {
                GameSetup = before with { Message = CoreStrings.SetupCancelled };
                PlayProblem = null;
            });
        }
        finally
        {
            _gameSetup = null;
        }
        return null;
    }

    /// <summary>"Set up the game again": the whole pretend setup, from the copy it knows.</summary>
    public async Task<string?> RebuildGameAsync(CancellationToken cancel)
    {
        if (GameCopyChangeProblem(null) is { } why)
        {
            return why;
        }
        if (GameSetup.DumpPath is null)
        {
            return CoreStrings.SetupNotYet;
        }
        await RunGameSetupAsync(null, cancel).ConfigureAwait(false);
        return null;
    }

    public async Task RunGameSetupAsync(string? dumpPath, CancellationToken cancel)
    {
        if (GameSetup.Busy)
        {
            return;
        }
        var before = GameSetup;
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, _lifetime.Token);
        _gameSetup = both;
        try
        {
            if (before.NeedsDump && string.IsNullOrWhiteSpace(dumpPath))
            {
                Change(() => GameSetup = before with { State = GameSetupState.Failed, ErrorCode = "source", Message = CoreStrings.SetupPickDump });
                return;
            }
            // Ten minutes of setup in forty seconds.
            var total = DemoSetupSteps.Sum(s => s.Seconds);
            for (var seconds = 0.0; seconds < total; seconds += total / 400)
            {
                var at = seconds;
                Change(() =>
                {
                    GameSetup = DemoSetupAt(at) with { DumpPath = dumpPath ?? before.DumpPath ?? DemoDump };
                    PlayProblem = CoreStrings.SetupRunning;
                });
                await Task.Delay(100, both.Token).ConfigureAwait(false);
            }
            Change(() =>
            {
                GameSetup = DemoSetupIdle with { State = GameSetupState.Done, Message = CoreStrings.SetupDone, DumpPath = dumpPath ?? before.DumpPath ?? DemoDump };
                if (Game.Install is { GameVersion: null } built)
                {
                    Game = Game with { Install = built with { GameVersion = "1.8.0" } };
                }
                Sync();
            });
        }
        catch (OperationCanceledException)
        {
            Change(() =>
            {
                GameSetup = before with { State = before.Reason is null ? GameSetupState.Idle : GameSetupState.NeedsSetup, Message = CoreStrings.SetupCancelled, ErrorCode = null };
                PlayProblem = before.Reason is null ? null : before.Message;
            });
        }
        finally
        {
            _gameSetup = null;
        }
    }

    public Task CancelGameSetup()
    {
        if (_gameSetup is { } running)
        {
            try
            {
                running.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // it ended by itself
            }
        }
        else if (GameSetup.State is GameSetupState.Failed or GameSetupState.Done || GameSetup.Message is not null)
        {
            Change(() => GameSetup = GameSetup.Reason is null
                ? DemoSetupIdle
                : GameSetup with { State = GameSetupState.NeedsSetup, ErrorCode = null });
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _play?.Dispose();
        _devices.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The demo's camera and microphone: moving gradient frames, a sine for the level.</summary>
public sealed class DesignDeviceLab : IDeviceLab
{
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private Timer? _mic;
    private int _width;
    private int _height;
    private bool _mirror;
    private uint _serial;

    public bool CameraAvailable => true;

    public string? CameraProblem => null;

    public CameraStatus CameraStatus { get; private set; } = CameraStatus.Idle;

    public event EventHandler<float>? MicLevel;

    public Task<IReadOnlyList<CameraDevice>> CameraDevicesAsync() =>
        Task.FromResult<IReadOnlyList<CameraDevice>>([new CameraDevice("cam-1", "USB Camera"), new CameraDevice("cam-2", "Capture Card")]);

    public void StartCamera(string deviceId, int width, int height, int fps, bool mirror)
    {
        _width = width;
        _height = height;
        _mirror = mirror;
        CameraStatus = CameraStatus.Running;
    }

    public void SetCameraMirror(bool mirror) => _mirror = mirror;

    public void StopCamera() => CameraStatus = CameraStatus.Idle;

    public bool TryCopyFrame(Span<byte> rgba, ref uint serial)
    {
        if (CameraStatus != CameraStatus.Running || rgba.Length < _width * _height * 4)
        {
            return false;
        }
        var now = (uint)(_clock.ElapsedMilliseconds / 33);
        if (now == _serial && serial == _serial)
        {
            return false;
        }
        _serial = now;
        serial = now;
        var t = _clock.Elapsed.TotalSeconds;
        var ballX = (0.5 + 0.35 * Math.Sin(t * 0.9)) * _width;
        var ballY = (0.5 + 0.3 * Math.Cos(t * 1.3)) * _height;
        var radius = _height * 0.16;
        for (var y = 0; y < _height; y++)
        {
            for (var x = 0; x < _width; x++)
            {
                var u = (double)(_mirror ? _width - 1 - x : x) / _width;
                var v = (double)y / _height;
                var i = (y * _width + x) * 4;
                var dx = (_mirror ? _width - 1 - x : x) - ballX;
                var dy = y - ballY;
                var inBall = dx * dx + dy * dy < radius * radius;
                rgba[i] = (byte)(inBall ? 255 : 40 + 120 * u + 40 * Math.Sin(t + v * 3));
                rgba[i + 1] = (byte)(inBall ? 240 : 70 + 90 * v);
                rgba[i + 2] = (byte)(inBall ? 200 : 200 - 90 * u + 30 * Math.Cos(t * 0.7));
                rgba[i + 3] = 255;
            }
        }
        return true;
    }

    public Task<IReadOnlyList<AudioDevice>> InputDevicesAsync() =>
        Task.FromResult<IReadOnlyList<AudioDevice>>([new AudioDevice("mic-1", "Headset Microphone"), new AudioDevice("mic-2", "Webcam Microphone")]);

    public Task<IReadOnlyList<AudioDevice>> OutputDevicesAsync() =>
        Task.FromResult<IReadOnlyList<AudioDevice>>([new AudioDevice("out-1", "Headset"), new AudioDevice("out-2", "TV Speakers")]);

    public void StartMicTest(string deviceId, int gainPercent, bool echoToOutput, string outputId)
    {
        StopMicTest();
        var gain = gainPercent / 100.0;
        _mic = new Timer(_ =>
        {
            var t = _clock.Elapsed.TotalSeconds;
            var level = Math.Clamp(gain * (0.35 + 0.3 * Math.Sin(t * 2.1) + 0.2 * Math.Sin(t * 7.3)), 0, 1);
            MicLevel?.Invoke(this, (float)level);
        }, null, 0, 33);
    }

    public void StopMicTest()
    {
        _mic?.Dispose();
        _mic = null;
    }

    public Task PlayTestSoundAsync(string outputId, int gainPercent) => Task.Delay(600);

    public void Dispose() => StopMicTest();
}

/// <summary>Where the demo starts (see <see cref="DesignLauncher(DemoStart)"/>).</summary>
public enum DemoStart { Online, Offline, Fresh }
