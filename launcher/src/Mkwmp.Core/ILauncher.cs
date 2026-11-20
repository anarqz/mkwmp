// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// Everything the window shows and can ask for. <see cref="Launcher"/> is the real one (port of
/// rust/client's Core); Mkwmp.App also has a design-time fake. All state is immutable records
/// replaced as a whole; <see cref="Changed"/> fires (on any thread) after any of it changes.
/// Methods never throw for expected failures: they leave a note in the matching view.
/// </summary>
public interface ILauncher : IAsyncDisposable
{
    event EventHandler? Changed;

    /// <summary>Raised for a toast (friend online/playing/request, tray hint, test). Any thread.</summary>
    event EventHandler<Toast>? Toasted;

    string Version { get; }

    LauncherSettings Settings { get; }
    ServerView Server { get; }
    ProfileView Profile { get; }
    SessionView Session { get; }
    FriendsView Friends { get; }
    GameView Game { get; }
    VoiceView Voice { get; }
    ReportView Report { get; }
    UpdateView Update { get; }
    int GitHubStars { get; }

    /// <summary>Camera/mic tests for the settings screens.</summary>
    Devices.IDeviceLab Devices { get; }

    /// <summary>The first thing that blocks PLAY (verbatim), or null.</summary>
    string? PlayProblem { get; }

    /// <summary>
    /// Online features are on: a server address is set (Settings › Online). Off by default on a
    /// fresh install: the launcher is then a shell for the game (PLAY starts it offline, no seat,
    /// no server contacted), and friends, seats, voice/camera, reports and the server profile stay
    /// locked behind "Configure online".
    /// </summary>
    bool OnlineEnabled => !string.IsNullOrWhiteSpace(Settings.Server);

    /// <summary>Starts the background loops (status, friends, notifications, local API, update check).</summary>
    Task StartAsync(CancellationToken cancel);

    /// <summary>Saves the settings (keeps the game-owned voice lists), wakes the friends long-poll.</summary>
    Task SaveSettingsAsync(LauncherSettings settings);

    Task SetConsentAsync(bool adult);

    // Profile
    Task RegisterAsync(string name);
    Task UsePinAsync(string name, string pin);
    Task DeleteProfileAsync();

    // Game folder and integration (Infinity's picker pattern: explicit, saved choice, launcher folder, look-around)
    Task FindGameAsync();
    /// <summary>False when the folder is not an install of any known target.</summary>
    Task<bool> ChooseGameFolderAsync(string folder);
    Task InstallIntegrationAsync();
    Task UninstallIntegrationAsync();

    // Play
    Task PlayAsync();
    Task StopAsync();

    // Friends
    Task AddFriendAsync(string name);
    Task AcceptFriendAsync(string name);
    Task RemoveFriendAsync(string name, FriendRemoval why);

    // Voice (devices and mutes are reported by the game; the launcher can also list devices itself)
    Task UnmuteAsync(string account);

    // Reports
    Task SendReportAsync(string name, string reason, string? details);
    Task WithdrawReportAsync();

    // Updates (modal with progress)
    Task CheckForUpdatesAsync();
    /// <summary>Downloads and verifies, then restarts into the new launcher. Progress in <see cref="Update"/>.</summary>
    Task ApplyUpdateAsync(CancellationToken cancel);

    /// <summary>Collects logs into a zip; returns its path (or throws IOException with the reason).</summary>
    Task<string> SaveBugReportAsync();

    void ShowTestToast();

    // The game's own settings (video, sound, controls...), declared by the target in use
    /// <summary>What "Game settings" shows: the target's schema, every value in effect, and whether they can be changed now.</summary>
    GameSettingsView GameSettings { get; }

    /// <summary>
    /// Changes one game setting (a preset also sets what it presets). Never throws: a refusal
    /// (unknown setting, bad value, the game is running) is the view's <see cref="GameSettingsView.Note"/>.
    /// </summary>
    Task SetGameSettingAsync(string id, string value);

    /// <summary>Puts one game setting back to its default.</summary>
    Task ResetGameSettingAsync(string id);

    /// <summary>Puts every game setting of the schema back to its default (locked ones stay).</summary>
    Task ResetGameSettingsAsync();

    // The game's runtime (mkw-recomp), installed by the launcher from one pinned release
    /// <summary>The pinned runtime and its install: not installed, installed, an update of ours to apply, or the install in progress.</summary>
    RuntimeView Runtime { get; }

    /// <summary>
    /// Downloads (continuing a partial download), verifies and installs the pinned runtime, or
    /// updates the install; then makes it the game folder. <paramref name="folder"/> null: the
    /// install in use when it is MKWMP's build, else <see cref="RuntimeView.Folder"/>. Never
    /// throws: progress and the outcome are in <see cref="Runtime"/>. Cancelling keeps the download.
    /// </summary>
    Task InstallRuntimeAsync(string? folder, CancellationToken cancel);

    /// <summary>Stops an install in progress (what was downloaded stays), or clears a failure's note.</summary>
    Task CancelRuntimeInstall();

    // The game's setup (mkw-recomp: building the game from the player's own copy), run by the launcher
    /// <summary>
    /// Whether the game must be set up before PLAY and why, the setup in progress (step, fractions, time left), how the
    /// last one ended, the player's copy the last one used and the log of the last run. A setup that is needed is also
    /// <see cref="PlayProblem"/>.
    /// </summary>
    GameSetupView GameSetup { get; }

    /// <summary>
    /// Runs the setup. <paramref name="dumpPath"/>: the player's own copy of the game (a .nup or .nx file, or a folder
    /// with exefs and romfs), read where it is and never copied; null repeats the last setup (after a runtime update, or
    /// to continue one that stopped). Never throws: progress and the outcome are in <see cref="GameSetup"/>. Cancelling
    /// ends it; the next run continues from the last finished step.
    /// </summary>
    Task RunGameSetupAsync(string? dumpPath, CancellationToken cancel);

    /// <summary>Stops a setup in progress, or clears the note of the last one.</summary>
    Task CancelGameSetup();

    // The console-style overlay (launcher/docs/OVERLAY.md)
    /// <summary>The game asks for the launcher over it (<c>Requested</c>); the window answers with <c>ReturnToGame</c>.</summary>
    Overlay.OverlayBridge OverlayBridge { get; }
}

/// <summary>The game's own settings, for the window (see <see cref="IGameTarget.GameSettings"/>).</summary>
/// <param name="Schema">The pages and settings; null when there is no game folder or this game has none (see <paramref name="Note"/>).</param>
/// <param name="Values">The value in effect of every setting of the schema, by id: what the game has set, else the default.</param>
/// <param name="Changed">The ids the game has a value set for (the others are at their default; resetting one takes it out).</param>
/// <param name="Editable">False while nothing can be changed: no schema, or the game is running.</param>
/// <param name="Note">Why it is not editable, or what the last change was refused for; null when fine.</param>
public sealed record GameSettingsView(
    GameSettingsSchema? Schema,
    IReadOnlyDictionary<string, string> Values,
    IReadOnlySet<string> Changed,
    bool Editable,
    string? Note,
    bool NoteIsError)
{
    /// <summary>The value in effect of a setting ("" for an id the schema lacks).</summary>
    public string Value(string id) => Values.TryGetValue(id, out var v) ? v : "";

    /// <summary>Whether a setting's condition holds now (<see cref="GameSetting.VisibleWhen"/>).</summary>
    public bool IsVisible(GameSetting setting) => GameSettingsSchema.IsVisible(setting, Values);
}

public sealed record Toast(string Title, string Body, ToastKind Kind);

public enum ToastKind { FriendOnline, FriendPlaying, FriendRequest, Info }

public enum FriendRemoval { Reject, Cancel, Unfriend }

/// <summary>client.toml, as the window edits it.</summary>
public sealed record LauncherSettings
{
    public string Server { get; init; } = "https://mkw.anarqz.dev";
    public string Name { get; init; } = "";
    public string Pin { get; init; } = "";
    /// <summary>The game target id (<c>mkw-recomp</c>).</summary>
    public string Target { get; init; } = "mkw-recomp";
    public string Game { get; init; } = "";
    public bool Launch { get; init; } = true;
    public SeatMode Seat { get; init; } = SeatMode.Start;
    public int LocalPort { get; init; } = 47820;
    public bool AutoUpdate { get; init; } = true;
    public int Slot { get; init; } = 1;
    public string LaunchCommand { get; init; } = "";
    public bool Patch { get; init; } = true;
    public bool AdultConsent { get; init; }
    public BackgroundSettings Background { get; init; } = new();
    public VoiceSettings Voice { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
}

public enum SeatMode { Start, Online }

public sealed record BackgroundSettings
{
    public bool CloseToTray { get; init; } = true;
    public bool StartWithSystem { get; init; }
    public bool NotifyOnline { get; init; } = true;
    public bool NotifyPlaying { get; init; } = true;
    public bool NotifyRequests { get; init; } = true;
    public bool TrayHintShown { get; init; }
}

public sealed record VoiceSettings
{
    public bool Enabled { get; init; } = true;
    public string Input { get; init; } = "";
    public string Output { get; init; } = "";
    public int InputGain { get; init; } = 100;
    public int OutputGain { get; init; } = 100;
    /// <summary>Camera device id for the launcher's camera test ("" = first). The in-game camera on/off is the mod's setting.</summary>
    public string Camera { get; init; } = "";
    public bool CameraMirror { get; init; } = true;
    /// <summary>The Party Chat introduction and consent pages were seen and accepted.</summary>
    public bool ChatIntroDone { get; init; }
}

public sealed record UiSettings
{
    public bool Fullscreen { get; init; } = true;
    /// <summary>"light" or "dark".</summary>
    public string Theme { get; init; } = "light";
    public string Locale { get; init; } = "";
    /// <summary>First-run setup (welcome, consent, gamertag, game folder) finished.</summary>
    public bool SetupDone { get; init; }
}

public enum ServerState { Checking, NotAsked, Online, Offline }

public sealed record ServerView(ServerState State, string? Name, int SeatsTotal, int Seated, int Queued, string? Message)
{
    public int SeatsFree => Math.Max(0, SeatsTotal - Seated);
}

public enum RegisterState { Idle, Working, Taken, Deleted, Failed }

/// <param name="SignedIn">A valid name and a 6-digit PIN.</param>
public sealed record ProfileView(string Name, string Pin, bool NameValid, bool SignedIn, RegisterState Register, string? Message, bool MessageIsError);

public enum SessionState { Idle, Starting, Running, Failed }

public enum SeatPhase { Joining, Queued, Seated, WaitingForGame, Busy, Refused }

/// <param name="Status">The status pill text (verbatim from the Rust launcher's state machine).</param>
/// <param name="Tone">good / warn / bad / info / plain.</param>
public sealed record SessionView(SessionState State, SeatPhase? Phase, int QueuePosition, bool GameRunning, bool FromSteam, string Status, string Tone)
{
    public bool Busy => State is SessionState.Starting or SessionState.Running;
}

public enum FriendState { Playing, Online, Offline }

public sealed record Friend(string Name, FriendState State);

public sealed record FriendRequest(string Name, DateTimeOffset Since);

/// <param name="Loaded">False until the first list arrives (or when not signed in).</param>
public sealed record FriendsView(bool Loaded, IReadOnlyList<Friend> Friends, IReadOnlyList<FriendRequest> Incoming, IReadOnlyList<FriendRequest> Outgoing, string? Note, bool NoteIsError, bool Busy)
{
    public int OnlineCount => Friends.Count(f => f.State != FriendState.Offline);
}

/// <param name="Searching">The look-around for an install is running.</param>
public sealed record GameView(
    IReadOnlyList<TargetInfo> Targets,
    GameInstall? Install,
    TargetCheck? Check,
    IntegrationReport? Integration,
    bool Searching,
    bool Working,
    string? Note,
    bool NoteIsError);

public sealed record TargetInfo(string Id, string DisplayName, string FolderHint, TargetCapabilities Capabilities);

public sealed record AudioDevice(string Id, string Name);

public sealed record MutedPlayer(string Account, string Name);

public sealed record VoiceView(IReadOnlyList<AudioDevice> Inputs, IReadOnlyList<AudioDevice> Outputs, IReadOnlyList<MutedPlayer> Mutes);

public sealed record ReportReason(string Code, string Label);

public sealed record OpenReport(string Reported, string Reason, string? Details, DateTimeOffset Created);

/// <param name="Loaded">null: not asked / not signed; false: checking or failed (see Note).</param>
public sealed record ReportView(bool? Loaded, OpenReport? Open, IReadOnlyList<ReportReason> Reasons, string? Note, bool NoteIsError, bool Busy);

public enum UpdateState { Off, Checking, UpToDate, Available, Downloading, Restarting, Failed }

/// <param name="Progress">0..1 while downloading.</param>
public sealed record UpdateView(UpdateState State, string? AvailableVersion, string? Notes, double Progress, TimeSpan? Remaining, string Status);
