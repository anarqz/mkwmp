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

    /// <summary>
    /// Asks a server who it is (its public status), without saving anything: what "Connect" shows
    /// before the address becomes <see cref="LauncherSettings.Server"/> through
    /// <see cref="SaveSettingsAsync"/>. Never throws: the answer says unreachable, not an MKWMP
    /// server, or too old. (The consent comes later: after signing in on the server, which keeps it.)
    /// </summary>
    Task<ServerProbe> ProbeServerAsync(string address);

    /// <summary>
    /// The servers this launcher has a profile on (name + PIN kept per server, so that switching
    /// or disconnecting never loses one), the most recently used last. Saving a server that is
    /// listed here with the current name brings its PIN back.
    /// </summary>
    IReadOnlyList<SavedServer> SavedServers { get; }

    /// <summary>Starts the background loops (status, friends, notifications, local API, update check).</summary>
    Task StartAsync(CancellationToken cancel);

    /// <summary>Saves the settings (keeps the game-owned voice lists), wakes the friends long-poll.</summary>
    Task SaveSettingsAsync(LauncherSettings settings);

    /// <summary>
    /// Gives or withdraws the consent (18+, the privacy policy). It belongs to the profile on the
    /// server: asked for after signing in, stored there (with the version of the policy shown by
    /// <see cref="GetPrivacyPolicyAsync"/>) and mirrored in <see cref="LauncherSettings.AdultConsent"/>,
    /// which a sign-in brings back on another device. A server that should have stored it and
    /// could not leaves it unchanged (<see cref="ProfileView.Message"/> says why). A server older
    /// than server-side consent: kept here only, as before.
    /// </summary>
    Task SetConsentAsync(bool adult);

    /// <summary>
    /// The privacy policy of the server in use, as Markdown to show inside the launcher (never in a
    /// browser): the server's own (<c>GET /privacy</c>), else the project's short text with its link.
    /// Never throws.
    /// </summary>
    Task<PrivacyPolicy> GetPrivacyPolicyAsync();

    /// <summary>
    /// The servers the beholder lists (a small public directory servers announce themselves to),
    /// each with its latency to the server itself, sorted by it. <see cref="ServerListState.Off"/>
    /// when no beholder is set: then only a typed address connects.
    /// </summary>
    ServerListView Servers { get; }

    /// <summary>Reads the list again and measures every server (the page asks when it opens, and for its refresh button).</summary>
    Task RefreshServersAsync();

    /// <summary>Measures the latencies again, without reading the list (the page asks every few seconds while it is open).</summary>
    Task MeasureServersAsync();

    /// <summary>
    /// Takes a server out of the list on this PC (<see cref="ServerListView.Hidden"/>, kept in client.toml by its
    /// public key, else its address): the next nearest takes its place. False, and nothing changes, for the server in
    /// use (<see cref="LauncherSettings.Server"/>): disconnect first.
    /// </summary>
    Task<bool> HideServerAsync(ListedServer server);

    /// <summary>"Show again": a hidden server (by <see cref="HiddenServer.Id"/>) is listed again.</summary>
    Task ShowServerAsync(string id);

    /// <summary>
    /// The game's own cover picture for the Play card, as a file beside the launcher (made once from the player's own
    /// copy of the game by the target: <see cref="IGameTarget.CoverArt"/>); null while there is none. Shown on this PC
    /// only: never uploaded, never put in a report.
    /// </summary>
    string? GameCover => null;

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

    // The launcher's own file picker (Files/FileBrowser.cs): what each purpose lists, and the target's own word on a choice
    /// <summary>What the picker lists and marks for a game folder: folders, with the ones a target recognises marked.</summary>
    Files.PickFilter GameFolderFilter => Files.PickFilter.Folders;

    /// <summary>
    /// Why a folder cannot be the game folder (no target recognises it), or null: the check
    /// <see cref="ChooseGameFolderAsync"/> makes, without saving anything. Reads files: call it off the window's thread.
    /// </summary>
    string? GameFolderProblem(string folder) => null;

    /// <summary>What the picker lists and marks for the player's own copy of the game (the target in use says: <see cref="IGameTarget.GameCopyFilter"/>).</summary>
    Files.PickFilter GameCopyFilter => Files.PickFilter.Any;

    /// <summary>
    /// Why a file or a folder is not a copy of the game the setup takes, or null: the setup's own first check
    /// (<see cref="IGameTarget.GameCopyProblem"/>). Reads a few bytes and folders: call it off the window's thread.
    /// </summary>
    string? GameCopyProblem(string path) => null;

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

    // Updates (modal with progress; launcher/docs/UPDATES.md)
    Task CheckForUpdatesAsync();

    /// <summary>
    /// Downloads the available release into the staging folder (continuing a download that was cut
    /// short) and verifies it against its signed SHA256SUMS; then <see cref="Update"/> says
    /// <see cref="UpdateState.ReadyToInstall"/>. Nothing installed is touched, the launcher keeps running.
    /// </summary>
    Task DownloadUpdateAsync(CancellationToken cancel);

    /// <summary>
    /// "Install and restart": hands the downloaded release to the helper (updater.exe), frees
    /// the seat and ends this launcher; the helper swaps the files and starts the new one. Downloads
    /// first when that was not done (<c>--headless</c>). Refused while a game runs
    /// (<see cref="UpdateView.GameBlocks"/>): a game is never interrupted.
    /// </summary>
    Task ApplyUpdateAsync(CancellationToken cancel);

    /// <summary>
    /// True: the downloaded update is installed by itself as soon as no game runs any more
    /// (<see cref="UpdateView.AfterGame"/>). False takes that back.
    /// </summary>
    void InstallUpdateAfterGame(bool on);

    /// <summary>Collects logs into a zip; returns its path (or throws IOException with the reason).</summary>
    Task<string> SaveBugReportAsync();

    // Crashes and "Found a bug?" (Diagnostics/): nothing is sent by the launcher, the player files the issue
    /// <summary>
    /// The game crashed and the player has not answered yet: the newest crash (a record the game
    /// process wrote in its folder, or only an abnormal exit code), with the count of earlier ones.
    /// Null when there is none. Set when the game ends and when the launcher starts; answered by
    /// <see cref="DismissCrashAsync"/>, after which the same crash is never offered again.
    /// </summary>
    Diagnostics.CrashView? PendingCrash { get; }

    /// <summary>
    /// Builds a report: a zip on the Desktop (the crash record and minidump, the game's and the
    /// launcher's logs, the redacted session file, system.txt; no PIN, ticket or token) and the
    /// GitHub issue to open, with its full text for the clipboard. <paramref name="crashId"/> null:
    /// "Found a bug?", without a crash. Never throws: a zip that could not be written is
    /// <see cref="Diagnostics.BugReport.ZipProblem"/>, and the link and the text are still there.
    /// </summary>
    Task<Diagnostics.BugReport> PrepareReportAsync(string? crashId);

    /// <summary>The player answered the crash (reported or ignored), and the earlier ones shown with it: not offered again.</summary>
    Task DismissCrashAsync(string id);

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

    /// <summary>
    /// Why the player's copy of the game cannot be changed now, or null (<see cref="ChangeGameCopyAsync"/>): a game with
    /// no setup of the launcher's, a setup or the runtime's install under way, PLAY holding the game, the game running;
    /// with <paramref name="dumpPath"/>, the first setup's own check of that copy. Off the window's thread.
    /// </summary>
    string? GameCopyChangeProblem(string? dumpPath);

    /// <summary>
    /// "Use another copy of the game" (Settings › Game), once the game is set up: the setup again from
    /// <paramref name="dumpPath"/>, read in place or copied (<paramref name="mode"/>; null: as the last setup did), redoing
    /// only what depends on the copy. Null when it ran (the outcome is in <see cref="GameSetup"/>); else why it did not
    /// start (<see cref="GameCopyChangeProblem"/>), and nothing changed.
    /// </summary>
    Task<string?> ChangeGameCopyAsync(string dumpPath, GameDataMode? mode, CancellationToken cancel);

    /// <summary>Stops a setup in progress, or clears the note of the last one.</summary>
    Task CancelGameSetup();

    // Mods of the game (launcher/docs/MODS.md): whatever the target in use lists
    /// <summary>The installed mods and whether they can be changed now. Empty until <see cref="RefreshModsAsync"/> ran once.</summary>
    ModsView Mods => ModsView.None;

    /// <summary>Reads the game's mods again (the Mods page asks when it opens; every change does it itself).</summary>
    Task RefreshModsAsync() => Task.CompletedTask;

    /// <summary>
    /// Turns a mod on or off. Null when done; else why not, for the player. A mod with native code is only
    /// turned on with <paramref name="acceptCode"/>: the window asks first (<see cref="GameMod.HasPlugin"/>).
    /// </summary>
    Task<string?> SetModEnabledAsync(string id, bool on, bool acceptCode) => Task.FromResult<string?>(null);

    /// <summary>Removes a mod. Null when done; else why not.</summary>
    Task<string?> UninstallModAsync(string id) => Task.FromResult<string?>(null);

    /// <summary>
    /// Installs a mod from an archive or folder on this PC (<see cref="IGameTarget.InstallMod"/>). Never throws.
    /// An archive in the launcher's own downloads folder is deleted once the install has its answer
    /// (kept while the answer is a question: <see cref="ModInstallCode.Variants"/>, <see cref="ModInstallCode.Exists"/>).
    /// </summary>
    Task<ModInstallResult> InstallModAsync(ModInstallRequest request, IProgress<ModProgress>? progress, CancellationToken cancel) =>
        Task.FromResult(new ModInstallResult(ModInstallCode.NoGame, ""));

    /// <summary>Downloads a mod's archive from an https link into <c>downloads\</c> beside the launcher; its path. Throws <see cref="LauncherException"/> for the player.</summary>
    Task<string> DownloadModAsync(string link, IProgress<ModProgress>? progress, CancellationToken cancel) => throw new LauncherException(Strings.ModsBadLink);

    /// <summary>The same for a file of a GameBanana mod (only a file its scan calls clean, from gamebanana.com, its checksum verified).</summary>
    Task<string> DownloadModAsync(Modding.GbDetails mod, Modding.GbFile file, IProgress<ModProgress>? progress, CancellationToken cancel) => throw new LauncherException(Strings.ModsBadLink);

    /// <summary>A mod's options as the window draws them: its schema, the values in effect, whether they can be changed now.</summary>
    GameSettingsView ModOptions(string id) => new(null, new Dictionary<string, string>(), new HashSet<string>(), false, null, false);

    /// <summary>Changes one option of a mod (null: its default again). Never throws: a refusal is the <see cref="ModOptions"/> note.</summary>
    Task SetModOptionAsync(string id, string key, string? value) => Task.CompletedTask;

    /// <summary>
    /// GameBanana's mods of the game, a page of twenty: in an order, or (with a <paramref name="query"/>) a search.
    /// Throws <see cref="LauncherException"/> for the player when GameBanana does not answer.
    /// </summary>
    Task<Modding.GbPage> BrowseModsAsync(Modding.GbSort sort, string query, int page, CancellationToken cancel) => Task.FromResult(new Modding.GbPage([], 1, 0));

    /// <summary>A GameBanana mod's page: description, pictures, files.</summary>
    Task<Modding.GbDetails> ModPageAsync(int id, CancellationToken cancel) => throw new LauncherException(Strings.GameBananaNotThisGame);

    /// <summary>A preview picture of GameBanana's as a file of the launcher's cache; null when it did not come.</summary>
    Task<string?> ModImageAsync(string url, CancellationToken cancel) => Task.FromResult<string?>(null);

    /// <summary>Shows the pictures of mods GameBanana marks as mature (the game's own <c>gb_mature</c> setting). Null when done; else why not.</summary>
    Task<string?> SetShowMatureModsAsync(bool show) => Task.FromResult<string?>(null);

    /// <summary>Asks GameBanana whether the mods installed from it have a newer file; the answer is <see cref="ModsView.Updates"/>.</summary>
    Task CheckModUpdatesAsync(CancellationToken cancel) => Task.CompletedTask;

// The console-style overlay (launcher/docs/OVERLAY.md)
    /// <summary>The game asks for the launcher over it (<c>Requested</c>); the window answers with <c>ReturnToGame</c>.</summary>
    Overlay.OverlayBridge OverlayBridge { get; }
}

/// <summary>The mods of the game in use, for the window.</summary>
/// <param name="Supported">The game in use takes mods the launcher can list.</param>
/// <param name="Loaded">The list was read at least once.</param>
/// <param name="Editable">False while nothing can be changed: no game, or it is running.</param>
/// <param name="Note">Why not, for the player; null when fine.</param>
/// <param name="ShowMature">GameBanana's rated mods are shown as they are (else their pictures are blurred).</param>
/// <param name="Updates">The mods GameBanana has a newer file for, by mod id.</param>
public sealed record ModsView(bool Supported, bool Loaded, IReadOnlyList<GameMod> Mods, bool Editable, string? Note, bool ShowMature, IReadOnlyDictionary<string, Modding.GbFile> Updates)
{
    public static ModsView None { get; } = new(false, false, [], false, null, false, new Dictionary<string, Modding.GbFile>());

    public GameMod? Find(string id) => Mods.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
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

public enum ServerProbeState
{
    /// <summary>An MKWMP server answered: its name and seats are in the probe.</summary>
    Ok,

    /// <summary>The address is empty or not one.</summary>
    Invalid,

    /// <summary>Nothing answered there (no network, wrong address, the server is down).</summary>
    Unreachable,

    /// <summary>Something answered, but it is not an MKWMP server.</summary>
    NotMkwmp,

    /// <summary>An MKWMP server too old for this launcher (it names no lobby).</summary>
    TooOld,
}

/// <summary>What <see cref="ILauncher.ProbeServerAsync"/> learned.</summary>
/// <param name="Address">The address as it would be saved (a bare host:port is http).</param>
/// <param name="Name">The server's own name; null unless <see cref="ServerProbeState.Ok"/> or <see cref="ServerProbeState.TooOld"/>.</param>
/// <param name="Message">What went wrong, for the player; null when <see cref="ServerProbeState.Ok"/>.</param>
public sealed record ServerProbe(ServerProbeState State, string Address, string? Name, int SeatsTotal, int Seated, int Queued, string? Message)
{
    public bool Ok => State == ServerProbeState.Ok;

    public int SeatsFree => Math.Max(0, SeatsTotal - Seated);
}

/// <summary>A privacy policy to show (<see cref="ILauncher.GetPrivacyPolicyAsync"/>).</summary>
/// <param name="Markdown">The text, as Markdown.</param>
/// <param name="Version">The sha256 of the server's text (what a consent names); null for the built-in text.</param>
/// <param name="FromServer">The server's own policy; false: there is none to show (the server publishes none, could not be reached, or no server is chosen).</param>
/// <param name="Link">Where the server serves it (<c>{server}/privacy</c>); null without one. The launcher renders the Markdown itself.</param>
/// <param name="Note">Why there is no policy to show; else null.</param>
public sealed record PrivacyPolicy(string Markdown, string? Version, bool FromServer, string? Link, string? Note)
{
    /// <summary>
    /// No policy: the launcher has none of its own (the server's is the only one), so <paramref name="note"/>
    /// says why there is nothing, and no other page stands in for it.
    /// </summary>
    public static PrivacyPolicy None(string note) => new("", null, false, null, note);
}

public enum ServerListState
{
    /// <summary>No beholder is set: there is no list.</summary>
    Off,
    Loading,
    Ready,

    /// <summary>The beholder did not answer: the last good list is shown.</summary>
    Cached,

    /// <summary>The beholder did not answer and there is no earlier list.</summary>
    Failed,
}

/// <summary>A server of the beholder's list. Its fields are the server's own words until <paramref name="Online"/> is true: then seats and queue are what the server itself just answered.</summary>
/// <param name="Address">The server's address, as it would be saved.</param>
/// <param name="LatencyMs">The median round trip to the server itself; null until measured, and when it did not answer.</param>
/// <param name="Online">null: not measured yet; false: it did not answer as an MKWMP server.</param>
public sealed record ListedServer(string Name, string Address, string? Region, string? Version, int Capacity, int Seated, int Queued, int? LatencyMs, bool? Online)
{
    /// <summary>The key the server announces itself with (it stays when the address moves); "" when not known.</summary>
    public string PublicKey { get; init; } = "";

    /// <summary>What a hidden server is remembered by: its public key, else its address.</summary>
    public string Id => PublicKey.Length > 0 ? PublicKey : Address;

    /// <summary>0 (no answer, or not measured) to 3 (under 60 ms; 2 under 150 ms).</summary>
    public int Bars => LatencyMs switch { null => 0, < 60 => 3, < 150 => 2, _ => 1 };
}

/// <summary>A server the player took out of the list (<see cref="ILauncher.HideServerAsync"/>).</summary>
/// <param name="Id">Its public key, else its address: what it is recognised by in a later list.</param>
/// <param name="Name">Its name when it was hidden, for "Hidden servers".</param>
/// <param name="Address">Its address when it was hidden.</param>
public sealed record HiddenServer(string Id, string Name, string Address)
{
    /// <summary>Whether a listed server is this one: the same key, or (hidden by address) the same address.</summary>
    public bool Is(ListedServer server) =>
        server is not null && (string.Equals(Id, server.Id, StringComparison.Ordinal) || ClientConfig.SameServer(Id, server.Address));
}

/// <param name="Items">
/// The listed servers the player did not hide, nearest first: the ones that answered by latency, then the ones not
/// measured (yet, or at all: <see cref="MeasuredAtMost"/>), then the ones that did not answer. The window shows the
/// first <see cref="PageSize"/> and more on request.
/// </param>
/// <param name="Busy">The list is being read or the servers measured.</param>
/// <param name="Note">Why the list is cached or missing; null when fine.</param>
public sealed record ServerListView(ServerListState State, IReadOnlyList<ListedServer> Items, bool Busy, string? Note)
{
    /// <summary>How many servers the list shows at first, and how many more each "Show more" adds.</summary>
    public const int PageSize = 5;

    /// <summary>
    /// Of a long list, only the first this many (in the beholder's order, hidden ones not counted) are measured, plus
    /// every server the player has a profile on: hundreds of listings never mean hundreds of servers contacted.
    /// </summary>
    public const int MeasuredAtMost = 50;

    public static ServerListView Off { get; } = new(ServerListState.Off, [], false, null);

    /// <summary>The servers hidden on this PC, in the order they were hidden (also those no longer listed).</summary>
    public IReadOnlyList<HiddenServer> Hidden { get; init; } = [];
}

/// <summary>A server the launcher keeps a profile for (<see cref="ILauncher.SavedServers"/>). The PIN itself stays in the settings file.</summary>
public sealed record SavedServer(string Address, string Name, bool HasPin);

public sealed record Toast(string Title, string Body, ToastKind Kind);

public enum ToastKind { FriendOnline, FriendPlaying, FriendRequest, Info }

public enum FriendRemoval { Reject, Cancel, Unfriend }

/// <summary>client.toml, as the window edits it.</summary>
public sealed record LauncherSettings
{
    /// <summary>The server's address; "" (a fresh install) is offline: no server is contacted and online features are locked.</summary>
    public string Server { get; init; } = "";
    /// <summary>The player's name: local until a server is chosen, then the gamertag registered on it.</summary>
    public string Name { get; init; } = "";
    /// <summary>The PIN of <see cref="Name"/> on <see cref="Server"/> (kept per server in the file; "" while offline or not registered).</summary>
    public string Pin { get; init; } = "";
    /// <summary>The game target id (<c>mkw-recomp</c>).</summary>
    public string Target { get; init; } = "mkw-recomp";
    public string Game { get; init; } = "";
    /// <summary>PLAY starts the game (also under Proton, where the launcher is the Steam shortcut and the game its child); false: the player starts it some other way.</summary>
    public bool Launch { get; init; } = true;
    /// <summary>
    /// <c>launch_chosen = true</c>: <see cref="Launch"/> was set by the player (Settings › Game), not left at a default.
    /// Only then is a <c>launch = false</c> kept on SteamOS, where the launcher once wrote it as its default.
    /// </summary>
    public bool LaunchChosen { get; init; }
    public SeatMode Seat { get; init; } = SeatMode.Start;
    public int LocalPort { get; init; } = 47820;
    public bool AutoUpdate { get; init; } = true;
    public int Slot { get; init; } = 1;
    public string LaunchCommand { get; init; } = "";
    /// <summary>The beholder (server directory) to list servers from; "" = this release's default (none yet).</summary>
    public string Beholder { get; init; } = "";
    public bool Patch { get; init; } = true;
    /// <summary>
    /// <c>dev_package = true</c>, written by hand (or by <c>publish.ps1 -Dev</c>), never by the window: the launcher
    /// takes an unsigned development package from its <c>game/</c> folder (launcher/docs/UPDATES.md). Off in every release.
    /// </summary>
    public bool DevPackage { get; init; }
    /// <summary>The consent (18+, privacy policy). With a server that keeps it, a mirror of what that server holds for the profile.</summary>
    public bool AdultConsent { get; init; }
    public BackgroundSettings Background { get; init; } = new();
    public VoiceSettings Voice { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    /// <summary>Where the launcher's file picker was last used, per purpose (<c>[picker]</c>).</summary>
    public Files.PickerFolders Picker { get; init; } = Files.PickerFolders.Empty;
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
    /// <summary>Kept for old settings files (the launcher once had an introduction to its chat pages); nothing reads it.</summary>
    public bool ChatIntroDone { get; init; }
}

public sealed record UiSettings
{
    public bool Fullscreen { get; init; } = true;
    /// <summary>"light" or "dark".</summary>
    public string Theme { get; init; } = "light";
    public string Locale { get; init; } = "";
    /// <summary>The first-start welcome flow (name, game, "go online?") finished, or was never needed (a working online profile was already here).</summary>
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

/// <summary>
/// <see cref="ReadyToInstall"/>: the release is downloaded and verified, waiting for "Install and
/// restart" (also after the launcher was closed and opened again). <see cref="Restarting"/>: the
/// helper has the update and this launcher is closing.
/// </summary>
public enum UpdateState { Off, Checking, UpToDate, Available, Downloading, ReadyToInstall, Restarting, Failed }

/// <param name="Progress">0..1 while downloading.</param>
/// <param name="Received">Bytes downloaded so far, of <paramref name="Total"/> (0: not known).</param>
/// <param name="BytesPerSecond">The download's speed over the last seconds.</param>
/// <param name="GameBlocks">Ready, but a game runs: installing would restart the launcher under it.</param>
/// <param name="AfterGame">Ready, and it is installed when the game closes (<see cref="ILauncher.InstallUpdateAfterGame"/>).</param>
public sealed record UpdateView(
    UpdateState State, string? AvailableVersion, string? Notes, double Progress, TimeSpan? Remaining, string Status,
    long Received = 0, long Total = 0, double BytesPerSecond = 0, bool GameBlocks = false, bool AfterGame = false);
