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

    /// <summary>The first thing that blocks PLAY (verbatim), or null. Never null while a row of <see cref="Checklist"/> blocks.</summary>
    string? PlayProblem { get; }

    /// <summary>
    /// The play checklist: everything PLAY needs (the runtime, the player's copy and its version, the setup, MKWMP's
    /// files, the launcher's update, online play), each with its state and the action that fixes it. Made from the same
    /// views as <see cref="PlayProblem"/>; replaced with them (<see cref="Changed"/>).
    /// </summary>
    PlayChecklist Checklist { get; }

    /// <summary>
    /// Online features are on: a server address is set (Settings › Online). Off by default on a
    /// fresh install: the launcher is then a shell for the game (PLAY starts it offline, no seat,
    /// no server contacted), and friends, seats, voice/camera, reports and the server profile stay
    /// locked behind "Configure online".
    /// </summary>
    bool OnlineEnabled => !string.IsNullOrWhiteSpace(Settings.Server);

    /// <summary>
    /// This run is offline only although a server is set: a required update could not be installed and the player
    /// chose to go on (<see cref="ContinueWithoutUpdate"/>). <see cref="OnlineEnabled"/> is false, so every locked
    /// place shows; they say "Update MKWMP to play online" and lead to the update instead of Settings › Online.
    /// Over at the next start, or when the update is installed.
    /// </summary>
    bool OnlineBlockedByUpdate => false;

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

    /// <summary>
    /// Measures the latencies again, without reading the list (the page asks every few seconds while it is open).
    /// Does nothing while <see cref="LauncherSettings.ClosestServers"/> is off.
    /// </summary>
    Task MeasureServersAsync();

    /// <summary>
    /// "Show closest to me" on or off (<see cref="LauncherSettings.ClosestServers"/>, saved): the list is ordered again
    /// at once (nearest first, or the directory's order without latencies) and, when on, measured now.
    /// </summary>
    Task SetClosestServersAsync(bool on);

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

    /// <summary>
    /// Settings › Game, "Open mkw-recomp": the runtime's own start screen, not the game (<see cref="Targets.IGameTarget.OpenOwnLauncher"/>).
    /// Refused while PLAY holds the game, while the game runs, and while a setup or the runtime's install runs. Never
    /// throws: the outcome is <see cref="GameView.Note"/>.
    /// </summary>
    Task OpenGameLauncherAsync();
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

    /// <summary>
    /// The way on after a required update could not be installed (<see cref="UpdateState.Failed"/> with
    /// <see cref="UpdateView.Required"/>): this launcher goes on <b>offline only</b> until it is closed
    /// (<see cref="OnlineBlockedByUpdate"/>: the game plays offline, nothing reaches the game server), and
    /// requires the update again at its next start. Nothing in any other state.
    /// </summary>
    void ContinueWithoutUpdate();

    /// <summary>
    /// Settings › Updates › Update channel: saves the choice (<see cref="LauncherSettings.UpdateChannel"/>) and looks
    /// for an update on it at once. To <see cref="UpdateChannel.Alpha"/>: a newer release candidate is then required
    /// like any update. To <see cref="UpdateChannel.Stable"/> on a release candidate: the next stable release at or
    /// above this version's numbers when there is one, else <see cref="UpdateView.StableReturn"/> names the stable
    /// release <see cref="ReturnToStableAsync"/> can go back to. Nothing is ever installed by this call alone.
    /// </summary>
    Task SetUpdateChannelAsync(UpdateChannel channel) => SaveSettingsAsync(Settings with { UpdateChannel = channel });

    /// <summary>
    /// "Return to Stable", confirmed by the player: downloads the newest stable release
    /// (<see cref="UpdateView.StableReturn"/>), verifies it like any update, and installs it over the release
    /// candidate that runs, although it is an older version. The only way the launcher ever goes down a version:
    /// only on the stable channel, only from a release candidate, only to a stable release, never by itself.
    /// client.toml, logs, mods and the game are not part of a release and stay as they are. Nothing in any other
    /// case; with a game running <see cref="Update"/> says why not.
    /// </summary>
    Task ReturnToStableAsync(CancellationToken cancel) => Task.CompletedTask;

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

    /// <summary>
    /// "Set up the game again": every step that builds the game runs again from the copy the last setup used (as long
    /// as the first setup: the game code and its shaders), keeping the saves, the mods and the game's settings. Null when
    /// it ran (the outcome is in <see cref="GameSetup"/>); else why it did not start (the game, PLAY or a setup is
    /// running, the game was never set up), and nothing changed.
    /// </summary>
    Task<string?> RebuildGameAsync(CancellationToken cancel);

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

    /// <summary>
    /// "Turn all off" (<paramref name="on"/> false: the list is kept, <see cref="ModsView.TurnedOff"/>) and "Turn all back on"
    /// (that list again, in its order). Never MKWMP's own mod or a locked one. How many changed, and why not, for the player.
    /// </summary>
    Task<(int Changed, string? Error)> SetAllModsEnabledAsync(bool on) => Task.FromResult<(int, string?)>((0, null));

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

    // Nextendo Network (docs/contracts/NEXTENDO_API.md): a linked account, and the choice of where PLAY goes online
    /// <summary>The linked Nextendo account, the network PLAY goes to, and how the account stands there.</summary>
    NextendoView Nextendo => NextendoView.None;

    /// <summary>
    /// Links a Nextendo account: opens the sign-in page with <paramref name="openBrowser"/> (false: it could not be
    /// opened) and waits for the browser to come back to this PC. The link stays here: nothing of it reaches an MKWMP
    /// server. Never throws: progress and the outcome are in <see cref="Nextendo"/>.
    /// </summary>
    Task LinkNextendoAsync(Func<Uri, bool> openBrowser) => Task.CompletedTask;

    /// <summary>Gives up a link that waits for the browser.</summary>
    void CancelNextendoLink()
    {
    }

    /// <summary>Unlinks: the sign-in is revoked and forgotten; the choice of network stays (on Nextendo, PLAY is offline until signed in again). Refused while PLAY runs.</summary>
    Task UnlinkNextendoAsync() => Task.CompletedTask;

    /// <summary>
    /// Where PLAY goes online from now on (saved). Refused while PLAY runs. On Nextendo no MKWMP server is contacted
    /// (no seat, friends, voice or camera), with or without an account; choosing MKWMP servers is what lets one be
    /// contacted at all, and its consent is asked for when a server is chosen.
    /// </summary>
    Task SetPlayOnAsync(PlayNetwork network) => Task.CompletedTask;

    /// <summary>Asks Nextendo how the linked account stands (its name, and whether it may go online now); the answer is <see cref="Nextendo"/>.</summary>
    Task RefreshNextendoAsync() => Task.CompletedTask;

    // Friends on Nextendo Network (Launcher.NextendoFriends.cs): the linked account's own, managed through Nextendo's API
    /// <summary>
    /// The friends of the linked Nextendo account, the requests, the players met lately and the blocked ones. Kept
    /// current while PLAY goes to Nextendo Network (<see cref="NextendoFriendsView.Enabled"/>); news of it arrives as
    /// <see cref="Toasted"/>, under the same three settings as an MKWMP server's friends.
    /// </summary>
    NextendoFriendsView NextendoFriends => NextendoFriendsView.Off;

    /// <summary>Asks the owner of a friend code to be friends (the code as typed: it is normalized). The outcome is the view's note.</summary>
    Task AddNextendoFriendAsync(string friendCode) => Task.CompletedTask;

    Task AcceptNextendoFriendAsync(long pid) => Task.CompletedTask;

    Task DeclineNextendoFriendAsync(long pid) => Task.CompletedTask;

    Task RemoveNextendoFriendAsync(long pid) => Task.CompletedTask;

    /// <summary>Blocks a player: no longer a friend, and their requests no longer arrive.</summary>
    Task BlockNextendoPlayerAsync(long pid) => Task.CompletedTask;

    Task UnblockNextendoPlayerAsync(long pid) => Task.CompletedTask;

    Task FavoriteNextendoFriendAsync(long pid, bool favorite) => Task.CompletedTask;

    /// <summary>The friends panel (or the blocked list) is on screen: the list is asked for now, with the players met lately and the blocked ones.</summary>
    Task RefreshNextendoFriendsAsync() => Task.CompletedTask;

    /// <summary>
    /// How someone of <see cref="NextendoFriends"/> is drawn: their Nextendo profile picture once it is on this PC,
    /// and the colour Nextendo puts behind it. <see cref="NextendoAvatar.None"/> off Nextendo (the window then draws
    /// its own disc, as it always did). Cheap: it reads no file and asks nothing; <see cref="Changed"/> says when a
    /// picture arrived.
    /// </summary>
    NextendoAvatar NextendoAvatarOf(long pid) => NextendoAvatar.None;

    /// <summary>The same for the linked account itself, while PLAY goes to Nextendo Network.</summary>
    NextendoAvatar NextendoOwnAvatar => NextendoAvatar.None;

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

    /// <summary>The list the last "Turn all off" kept, in the game's order (empty: nothing to bring back).</summary>
    public IReadOnlyList<string> TurnedOff { get; init; } = [];

    /// <summary>A mod "Turn all off" changes: not MKWMP's own, not locked.</summary>
    public static bool Switchable(GameMod mod) => mod is { Managed: false, Locked: null };

    /// <summary>The switchable mods that are on now: what "Turn all off" turns off.</summary>
    public int OnNow => Mods.Count(m => Switchable(m) && m.Enabled);

    /// <summary>The mods "Turn all back on" turns on: kept by "Turn all off", still installed, switchable and off now.</summary>
    public int ToBringBack => TurnedOff.Count(id => Find(id) is { Enabled: false } mod && Switchable(mod));
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

/// <summary>Where PLAY goes online (client.toml <c>[nextendo] play_on</c>).</summary>
public enum PlayNetwork
{
    /// <summary>An MKWMP server: the alternative, chosen in Settings › Online (and what an install from before Nextendo stays on).</summary>
    Mkwmp,

    /// <summary>Nextendo Network, with the linked account: the default.</summary>
    Nextendo,
}

/// <summary>The linked Nextendo account, for the window (<see cref="ILauncher.Nextendo"/>). No token is ever in it.</summary>
/// <param name="Linked">An account is linked on this PC.</param>
/// <param name="Username">Its name on Nextendo ("" when not linked).</param>
/// <param name="FriendCode">Its friend code ("" when not linked or not known).</param>
/// <param name="PlayOn">
/// The saved choice. <see cref="PlayNetwork.Nextendo"/> is the default, also with no account linked: PLAY then starts
/// the game offline, and the window asks to sign in instead of showing an MKWMP server.
/// </param>
/// <param name="Active">PLAY goes to Nextendo now: linked, chosen, and this run is not offline only.</param>
/// <param name="Linking">The sign-in page is open in the browser and the launcher waits for it.</param>
/// <param name="Checking">Nextendo is being asked how the account stands.</param>
/// <param name="Status">One line: how the account stands on Nextendo ("" when not linked or not asked yet).</param>
/// <param name="Tone">good / warn / bad / muted, for <paramref name="Status"/>.</param>
/// <param name="Note">The outcome of the last link, unlink or switch, when there is something to say.</param>
public sealed record NextendoView(bool Linked, string Username, string FriendCode, PlayNetwork PlayOn, bool Active, bool Linking, bool Checking,
    string Status, string Tone, string? Note, bool NoteIsError)
{
    /// <summary>No account, PLAY goes to Nextendo Network: a fresh install.</summary>
    public static NextendoView None { get; } = new(false, "", "", PlayNetwork.Nextendo, false, false, false, "", "muted", null, false);

    /// <summary>Nextendo Network is where this launcher goes online (the default): its pages speak Nextendo, and MKWMP servers are Settings › Online's alternative.</summary>
    public bool Preferred => PlayOn == PlayNetwork.Nextendo;

    /// <summary>Nextendo is where PLAY goes online and no account is linked yet: PLAY is offline until the player signs in.</summary>
    public bool NeedsSignIn => Preferred && !Linked;
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
    /// <summary>
    /// <c>recomp_update_check_restored = true</c>: mkw-recomp's own update check, which launchers up to 1.4.6 kept off
    /// (<c>update_check = 0</c> in mkw.ini), was given back its default (on) once. Afterwards the player's choice stays.
    /// </summary>
    public bool RecompUpdateCheckRestored { get; init; }
    /// <summary>
    /// <c>recomp_start_screen_restored = true</c>: mkw-recomp's start screen, which launchers up to 1.4.7 kept on Skip
    /// (<c>start_screen = skip</c> in mkw.ini, locked), was given back its default (Automatic) once. Afterwards the player's choice stays.
    /// </summary>
    public bool RecompStartScreenRestored { get; init; }
    public SeatMode Seat { get; init; } = SeatMode.Start;
    public int LocalPort { get; init; } = 47820;
    public bool AutoUpdate { get; init; } = true;
    /// <summary>
    /// <c>update_channel</c>: which releases the launcher updates to. <see cref="UpdateChannel.Stable"/> (the default;
    /// the key is then not written) or <see cref="UpdateChannel.Alpha"/> (<c>"alpha"</c>: also the test versions,
    /// <c>-rc.N</c>). Settings › Updates.
    /// </summary>
    public UpdateChannel UpdateChannel { get; init; } = UpdateChannel.Stable;
    public int Slot { get; init; } = 1;
    public string LaunchCommand { get; init; } = "";
    /// <summary>The beholder (server directory) to list servers from; "" = this release's default (none yet).</summary>
    public string Beholder { get; init; } = "";
    /// <summary>
    /// "Show closest to me" (<c>closest_servers</c>): while the server list is on screen the launcher times each listed
    /// server (a request to the server itself) and shows the nearest first. Off: no server of the list is contacted to
    /// be timed, and the list keeps the directory's order, without bars or ms. Never the player's location.
    /// </summary>
    public bool ClosestServers { get; init; } = true;
    public bool Patch { get; init; } = true;
    /// <summary>
    /// <c>dev_package = true</c>, written by hand (or by <c>publish.ps1 -Dev</c>), never by the window: the launcher
    /// takes an unsigned development package from its <c>game/</c> folder (launcher/docs/UPDATES.md). Off in every release.
    /// </summary>
    public bool DevPackage { get; init; }
    /// <summary>
    /// <c>recomp_exe_sha256</c> under <c>[dev]</c>, written by hand, never by the window: the SHA-256 of test builds of
    /// mkw.exe (lower-case hex, comma-separated) taken as the pinned mkw-recomp (launcher/docs/RECOMP_INTEGRATION.md,
    /// "Testing a fork build"). Empty in every release. <c>MKWMP_DEV_RECOMP_SHA256</c> adds to it.
    /// </summary>
    public string DevRecompExes { get; init; } = "";
    /// <summary>The consent (18+, privacy policy). With a server that keeps it, a mirror of what that server holds for the profile.</summary>
    public bool AdultConsent { get; init; }
    public BackgroundSettings Background { get; init; } = new();
    public VoiceSettings Voice { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    /// <summary>Where the launcher's file picker was last used, per purpose (<c>[picker]</c>).</summary>
    public Files.PickerFolders Picker { get; init; } = Files.PickerFolders.Empty;
    /// <summary>
    /// <c>[nextendo] show_online</c> (on unless the file says otherwise): while the launcher runs and PLAY goes to
    /// Nextendo Network, Nextendo friends see the player online. Off: the launcher says nothing of the player there
    /// (in a race the game's own servers still show them playing).
    /// </summary>
    public bool NextendoShowOnline { get; init; } = true;
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
    /// <summary>
    /// <see cref="Camera"/>'s name when it was last picked or tested (<c>camera_name</c>): Settings shows it without
    /// listing the cameras on every visit. "" = not known yet.
    /// </summary>
    public string CameraName { get; init; } = "";
    public bool CameraMirror { get; init; } = true;
    /// <summary>Kept for old settings files (the launcher once had an introduction to its chat pages); nothing reads it.</summary>
    public bool ChatIntroDone { get; init; }
}

public sealed record UiSettings
{
    public bool Fullscreen { get; init; } = true;
    /// <summary>"light" or "dark".</summary>
    public string Theme { get; init; } = "light";
    /// <summary>How far the surfaces stand from the background: "low", "normal", "high" or "higher".</summary>
    public string Contrast { get; init; } = "normal";
    public string Locale { get; init; } = "";
    /// <summary>The first-start welcome flow (name, game, "go online?") finished, or was never needed (a working online profile was already here).</summary>
    public bool SetupDone { get; init; }
    /// <summary>Home's invitation to Nextendo Network (shown to an install that plays on MKWMP servers) was closed: it stays away.</summary>
    public bool NextendoInviteDismissed { get; init; }
    /// <summary>The controller shortcut that opens the overlay in the game (<c>overlay_shortcut</c>; the mod takes a change at its next poll).</summary>
    public Overlay.OverlayShortcut OverlayShortcut { get; init; }
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

public sealed record TargetInfo(string Id, string DisplayName, string FolderHint, TargetCapabilities Capabilities)
{
    /// <summary>The game versions MKWMP plays on with this target (<see cref="IGameTarget.GameVersions"/>); empty: any.</summary>
    public IReadOnlyList<string> GameVersions { get; init; } = [];
}

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

/// <summary>
/// Which releases the launcher updates to (Settings › Updates; <c>update_channel</c> in client.toml).
/// <see cref="Stable"/>: stable releases only. <see cref="Alpha"/>: also the test versions published before a
/// release (release candidates, <c>1.5.1-rc.1</c>), whichever is newest; they may be unstable. An update is
/// required on either channel, by the same rule; a release candidate is never required of a stable player.
/// </summary>
public enum UpdateChannel { Stable, Alpha }

/// <summary>The channel's name in client.toml and in a bug report (English, never translated).</summary>
public static class UpdateChannels
{
    public const string StableName = "stable", AlphaName = "alpha";

    public static string Name(UpdateChannel channel) => channel == UpdateChannel.Alpha ? AlphaName : StableName;

    /// <summary>What a file says: <c>"alpha"</c> (any case) is alpha; anything else, or nothing, is stable.</summary>
    public static UpdateChannel Parse(string? name) =>
        string.Equals(name?.Trim(), AlphaName, StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Alpha : UpdateChannel.Stable;

    /// <summary>"1.5.1-rc.2 (alpha channel)": the version as a bug report names it.</summary>
    public static string Describe(string version, UpdateChannel channel) => $"{version} ({Name(channel)} channel)";
}

/// <param name="Progress">0..1 while downloading.</param>
/// <param name="Received">Bytes downloaded so far, of <paramref name="Total"/> (0: not known).</param>
/// <param name="BytesPerSecond">The download's speed over the last seconds.</param>
/// <param name="GameBlocks">Ready, but a game runs: installing would restart the launcher under it.</param>
/// <param name="AfterGame">Ready, and it is installed when the game closes (<see cref="ILauncher.InstallUpdateAfterGame"/>).</param>
/// <param name="Required">
/// A newer release was found and this launcher must install it before it goes on: it downloads by itself, PLAY is
/// refused, and the window shows the update card with no way around it, unless a game runs (<paramref name="GameBlocks"/>:
/// then it installs after the game). False again once installing failed and the player chose to go on
/// (<see cref="ILauncher.ContinueWithoutUpdate"/>), for this run.
/// </param>
/// <param name="StableReturn">
/// "Return to Stable" is on offer: this launcher runs a release candidate on the stable channel and no stable
/// release has reached its numbers yet; this is the version of the newest stable release, an older one
/// (<see cref="ILauncher.ReturnToStableAsync"/>). Never required, never installed by itself. Null otherwise.
/// </param>
public sealed record UpdateView(
    UpdateState State, string? AvailableVersion, string? Notes, double Progress, TimeSpan? Remaining, string Status,
    long Received = 0, long Total = 0, double BytesPerSecond = 0, bool GameBlocks = false, bool AfterGame = false, bool Required = false,
    string? StableReturn = null);
