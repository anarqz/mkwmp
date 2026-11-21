// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.Core.Targets;

/// <summary>
/// One way of running the game that MKWMP can hook into: mkw-recomp today; an emulator or another
/// recompilation later. The server is one service for all of them; a target owns everything that
/// differs per solution: finding and identifying an install, the version gates, the files that
/// put MKWMP into it (the probe beside the exe, or a mod), the session file the hook reads, and
/// how the game starts. The launcher core and the window only ever talk to this interface.
/// </summary>
public interface IGameTarget
{
    /// <summary>Stable id, saved in the settings (<c>target = "mkw-recomp"</c>).</summary>
    string Id { get; }

    /// <summary>What the player sees, e.g. "mkw-recomp".</summary>
    string DisplayName { get; }

    /// <summary>What a folder must hold, for the folder picker's title and hints ("the folder with mkw.exe").</summary>
    string FolderHint { get; }

    TargetCapabilities Capabilities { get; }

    /// <summary>The install in this folder, or null when the folder is not one (never throws for a bad folder).</summary>
    GameInstall? Detect(string folder);

    /// <summary>Where to look when nothing was chosen yet (shallow look-around roots).</summary>
    IEnumerable<string> SearchRoots();

    /// <summary>Version and rebuild gates for an install: what blocks play, and how to fix it.</summary>
    TargetCheck Check(GameInstall install);

    /// <summary>
    /// The versions of the game MKWMP plays on with this target (mkw-recomp: 1.8.0); empty: any. <see cref="Check"/>
    /// refuses an install set up from another one (<see cref="TargetGate.GameVersion"/>); the play checklist names them.
    /// </summary>
    IReadOnlyList<string> GameVersions => [];

    /// <summary>The state of MKWMP's integration in this install (probe/mod files), against the package the launcher carries.</summary>
    IntegrationReport Inspect(GameInstall install, IntegrationPackage? package);

    /// <summary>Installs or repairs the integration. The game must be closed.</summary>
    Task<IntegrationReport> InstallAsync(GameInstall install, IntegrationPackage package, CancellationToken cancel);

    /// <summary>Removes every MKWMP file and puts back what was there.</summary>
    Task UninstallAsync(GameInstall install, CancellationToken cancel);

    /// <summary>Writes (atomically) what the hook reads at start: the session file (mkwmp.toml for mkw-recomp), the lobby CA.</summary>
    void WriteSession(GameInstall install, SessionFile session);

    /// <summary>Drops the ticket from the session file (after a profile delete).</summary>
    void ForgetTicket(GameInstall install);

    /// <summary>Starts the game; null when the player starts it elsewhere (Steam).</summary>
    IGameProcess? Launch(GameInstall install, LaunchOptions options);

    /// <summary>Whether this target's game is running (also when not started by us).</summary>
    bool IsRunning(GameInstall install);

    /// <summary>
    /// The process ids of this target's running game, as this process sees them (under Wine: this
    /// prefix's): the window looks for the game's window with them. Empty when not running or not known.
    /// </summary>
    IReadOnlyList<int> ProcessIds(GameInstall install) => [];

    /// <summary>Extra files for a bug report (game logs, redacted session file, a system line per file).</summary>
    IEnumerable<SupportItem> SupportItems(GameInstall install);

    // ---- additions with defaults (a target that needs none of them implements nothing) -----

    /// <summary>
    /// The release assets that make this target's <see cref="IntegrationPackage"/>. The launcher
    /// core finds them (the signed <c>game/</c> folder next to the launcher, else this launcher's
    /// GitHub release), verifies them against the signed SHA256SUMS and hands them over.
    /// </summary>
    IReadOnlyList<PackageAsset> PackageAssets => [];

    /// <summary>
    /// The assets this install needs (mkw-recomp: the MKWMP mod on a runtime that loads mods, the
    /// game patch on an older one). The default: <see cref="PackageAssets"/>, whatever the install.
    /// </summary>
    IReadOnlyList<PackageAsset> PackageAssetsFor(GameInstall install) => PackageAssets;

    /// <summary>
    /// False when <see cref="InstallAsync"/> needs no files for this install right now (mkw-recomp
    /// with the MKWMP mod enabled: installing then only takes the launcher's own patch out). The
    /// core then calls it with <see cref="IntegrationPackage.Empty"/> even when offline.
    /// </summary>
    bool NeedsPackage(GameInstall install) => true;

    /// <summary>
    /// A developer keeps their own build of the integration in the game (MKWMP_DEV_PROBE=1): the
    /// core then installs only when the player presses INSTALL, never by itself on start or PLAY.
    /// </summary>
    bool KeepsOwnIntegration => false;

    /// <summary>Folders of this install that may hold MKWMP's native libraries (mkwmp-camera.dll) for the launcher's own device tests.</summary>
    IEnumerable<string> NativeLibraryFolders(GameInstall install) => [];

    /// <summary>
    /// A per-player setting that MKWMP's hook keeps inside the game (mkw-recomp: the mod's
    /// <c>camera</c> / <c>camera_view</c>, see <see cref="MkwRecomp.ModSettings"/>); null when unset
    /// or when this target has none. The default serves mkw-recomp installs and no other.
    /// </summary>
    string? ReadModSetting(GameInstall install, string key) => MkwRecomp.ModSettings.Read(install, key);

    /// <summary>
    /// Changes such a setting (null removes it) while the game is closed. False: this target has
    /// none. Throws <see cref="LauncherException"/> with what the player must do (close the game,
    /// start it once) or why the file could not be written.
    /// </summary>
    bool WriteModSetting(GameInstall install, string key, string? value) => MkwRecomp.ModSettings.Write(this, install, key, value);

    /// <summary>
    /// The game's own settings (video, sound, controls...) that the launcher may edit for this
    /// install, as data the window draws (launcher/docs/TARGETS.md, "Game settings"); null when
    /// this target has none. Called often: keep it to reading a small file, and return the same
    /// object while nothing changed. The default serves mkw-recomp installs and no other.
    /// </summary>
    GameSettingsSchema? GameSettings(GameInstall install) => MkwRecomp.RecompSettings.Schema(install);

    /// <summary>
    /// The values the game has set, by setting id; a setting at its default is absent (see
    /// <see cref="GameSettingsSchema.Effective"/>). Never throws: an unreadable file reads as empty.
    /// </summary>
    IReadOnlyDictionary<string, string> ReadGameSettings(GameInstall install) => MkwRecomp.RecompSettings.Read(install);

    /// <summary>
    /// Changes settings (id to value; null puts a setting back to its default), atomically and
    /// keeping everything else in the game's file as it is. Throws <see cref="LauncherException"/>
    /// with a message for the player: an unknown setting, a value it does not take
    /// (<see cref="GameSetting.Normalize"/>), a locked setting, a game that runs and would not see
    /// the change or would write over it, a file that cannot be written.
    /// </summary>
    void WriteGameSettings(GameInstall install, IReadOnlyDictionary<string, string?> changes) => MkwRecomp.RecompSettings.Write(this, install, changes);

    /// <summary>
    /// The one build of this target's runtime that the launcher installs and updates itself
    /// (<see cref="RuntimeInstaller"/>), pinned in the launcher's source; null when the player
    /// installs the runtime themselves. Never "the newest release": see <see cref="RuntimeManifest"/>.
    /// </summary>
    RuntimeManifest? Runtime => null;

    /// <summary>
    /// The setup that makes the game playable in this install, when the launcher runs it (for
    /// mkw-recomp: building the game from the player's own copy): whether one is needed now and
    /// the copy the last one used. Null (the default): this target has none, or the player does it
    /// elsewhere (an emulator needs none). Called every 2 s: read at most a small file.
    /// </summary>
    GameSetupStatus? SetupStatus(GameInstall install) => null;

    /// <summary>Why the game must be set up before PLAY, or null: it blocks PLAY, after the game folder and before the integration.</summary>
    GameSetupPlan? SetupNeeded(GameInstall install) => SetupStatus(install)?.Needed;

    /// <summary>
    /// Runs the setup to its end, reporting where it is. Never throws for a failure: the result
    /// carries a stable code and a message for the player. Refused while the game runs and while
    /// another setup of the install runs. Cancelling ends everything it started and throws
    /// <see cref="OperationCanceledException"/>; a setup that was stopped must continue, not start
    /// over, when it is run again. The player's copy of the game is only read.
    /// </summary>
    Task<GameSetupResult> RunSetupAsync(GameInstall install, GameSetupRequest request, IProgress<GameSetupProgress>? progress, CancellationToken cancel) =>
        Task.FromResult(GameSetupResult.Refused("none", Strings.SetupNone));

    /// <summary>
    /// What the launcher's file picker lists for the player's own copy of the game: the files that can be one (by
    /// extension) and the folders that hold one (marked in the list). The default: every file.
    /// </summary>
    Files.PickFilter GameCopyFilter => Files.PickFilter.Any;

    /// <summary>
    /// Why <paramref name="path"/> is not a copy of the game <see cref="RunSetupAsync"/> takes, in words for the
    /// player; null when it is one (or when this target has no setup). Never throws; reads little.
    /// </summary>
    string? GameCopyProblem(string? path) => null;

    /// <summary>
    /// Whether a folder looks like an install, for the picker's mark: one or two file checks, no
    /// reading (<see cref="Detect"/> stays the judge of a choice).
    /// </summary>
    bool LooksLikeInstall(string folder) => Detect(folder) is not null;

    /// <summary>
    /// Writes the game settings the launcher keeps for an install it manages (shown locked in
    /// <see cref="GameSettings"/>). Called after the launcher installed or updated the runtime and
    /// before a setup; does nothing while the game runs. Returns what it changed, for the log.
    /// </summary>
    IReadOnlyList<string> ApplyOwnedSettings(GameInstall install) => [];

    /// <summary>
    /// Settings every player of an online race must share: locked in the launcher while a server is
    /// chosen, and written before an online session. (Id, value, why: shown on the locked row.)
    /// </summary>
    IReadOnlyList<(string Id, string Value, string Why)> OnlineOwnedSettings => [];

    /// <summary>
    /// The game's own cover picture for the launcher's Play card: the path of an image file (PNG or JPEG) under
    /// <paramref name="cacheFolder"/> (the launcher's <c>cache\</c>, beside its exe), made once from the player's own
    /// copy of the game and found again on later calls; null when this target has none or the copy cannot be read
    /// now (never throws). It may read large files: the core calls it off the window's thread. The picture is the
    /// player's own: shown on this PC only, never sent anywhere and never put in a report or a release.
    /// </summary>
    string? CoverArt(GameInstall install, string cacheFolder) => null;

    // ---- mods (launcher/docs/MODS.md): the defaults serve mkw-recomp installs and no other ----

    /// <summary>Whether this install takes mods the launcher can list (mkw-recomp: a runtime that loads mods).</summary>
    bool SupportsMods(GameInstall install) => MkwRecomp.RecompMods.Capable(install);

    /// <summary>
    /// The mods in this install, with what the game is told to load. <paramref name="locale"/>: the
    /// language for the texts of a mod's own options ("pt-BR"; null: English). Reads folders and small
    /// files: call it off the window's thread. Never throws.
    /// </summary>
    IReadOnlyList<GameMod> Mods(GameInstall install, string? locale) => MkwRecomp.RecompMods.List(install, locale);

    /// <summary>
    /// Turns a mod on or off for the game's next start. Throws <see cref="LauncherException"/> with what the
    /// player must know: the game runs, the mod is locked (<see cref="GameMod.Locked"/>), or it carries native
    /// code and <paramref name="acceptCode"/> is false (the player was not asked yet).
    /// </summary>
    void SetModEnabled(GameInstall install, string id, bool on, bool acceptCode) => MkwRecomp.RecompMods.SetEnabled(this, install, id, on, acceptCode);

    /// <summary>Removes a mod from the game (its settings stay). Throws <see cref="LauncherException"/> like <see cref="SetModEnabled"/>; never the launcher's own mod.</summary>
    void UninstallMod(GameInstall install, string id) => MkwRecomp.RecompMods.Uninstall(this, install, id);

    /// <summary>
    /// Installs a mod from an archive or a folder on this PC: checked, unpacked beside the mods and put
    /// in place whole or not at all. Never throws for a refusal: the result says which
    /// (<see cref="ModInstallCode"/>) and what to tell the player. Refused while the game runs.
    /// </summary>
    ModInstallResult InstallMod(GameInstall install, ModInstallRequest request, IProgress<ModProgress>? progress, CancellationToken cancel) =>
        MkwRecomp.ModImport.Install(this, install, request, progress, cancel);

    /// <summary>The values a mod's own settings file has for the options it declares (<see cref="GameMod.Options"/>), by key.</summary>
    IReadOnlyDictionary<string, string> ReadModOptions(GameInstall install, GameMod mod) => MkwRecomp.RecompMods.ReadOptions(install, mod);

    /// <summary>Changes options of a mod (null: back to the default), checked against its schema. Throws <see cref="LauncherException"/> for the player.</summary>
    void WriteModOptions(GameInstall install, GameMod mod, IReadOnlyDictionary<string, string?> changes) => MkwRecomp.RecompMods.WriteOptions(this, install, mod, changes);
}

/// <param name="Name">The file's name in the release and in SHA256SUMS.</param>
/// <param name="Required">A release without it has no integration for this target.</param>
/// <param name="Label">What the player calls it, for errors ("game patch download failed: …").</param>
/// <param name="Unpacked">
/// For a zip: the folder that may stand for it in the launcher's <c>game/</c> folder, with every
/// file listed as <c>&lt;Unpacked&gt;/&lt;path&gt;</c> in SHA256SUMS (<c>mods/mkwmp</c> for
/// <c>mkwmp-mod.zip</c>). The package then carries those files by their listed names.
/// </param>
public sealed record PackageAsset(string Name, bool Required, string Label, string? Unpacked = null);

[Flags]
public enum TargetCapabilities
{
    None = 0,
    Friends = 1,
    VoiceChat = 2,
    Camera = 4,
    FriendRooms = 8,
    /// <summary>The launcher can start the game itself (else: "start it from Steam / the emulator").</summary>
    Launch = 16,
}

/// <summary>A found install. <paramref name="Version"/> is the runtime's (mkw-recomp 0.1.15-mkwmp), <paramref name="GameVersion"/> the game's (1.8.0).</summary>
public sealed record GameInstall(string TargetId, string Root, string Executable, string? Version, string? GameVersion, string? DataRoot);

public enum TargetStatus { Missing, Unsupported, NeedsRebuild, NoIntegration, Ready }

/// <param name="Problem">What blocks play, verbatim for the player; null when nothing does.</param>
/// <param name="Explain">Longer text (hover/details).</param>
/// <param name="FixUrl">Where to get a supported build (opened in the browser), if any.</param>
/// <param name="FixLabel">Button text for <paramref name="FixUrl"/>, e.g. "Get 0.1.15-mkwmp".</param>
public sealed record TargetCheck(TargetStatus Status, string? Problem, string? Explain, Uri? FixUrl, string? FixLabel)
{
    /// <summary>Which gate <see cref="Problem"/> is: the runtime, the game's version, or a game built for another runtime.</summary>
    public TargetGate Gate { get; init; }
}

/// <summary>What a <see cref="TargetCheck"/> refuses: the play checklist puts it on the row it belongs to.</summary>
public enum TargetGate
{
    /// <summary>Nothing, or not said (a target that does not tell: the runtime's row).</summary>
    None,

    /// <summary>The runtime is missing, unknown or not one MKWMP supports.</summary>
    Runtime,

    /// <summary>The game was set up from a version MKWMP does not play on (<see cref="IGameTarget.GameVersions"/>).</summary>
    GameVersion,

    /// <summary>The game was built for another runtime and must be set up again.</summary>
    Rebuild,
}

/// <summary><c>Disabled</c>: the files are in the game, and the game is not told to load them (mkw-recomp: the mod is not in mkw.ini's <c>mods</c>).</summary>
public enum IntegrationState { Installed, Unverified, Missing, Outdated, Damaged, HostMissing, Dev, Mod, Disabled }

/// <param name="Lines">Per-file lines ("winmm.dll: v1.4.0, verified").</param>
public sealed record IntegrationReport(IntegrationState State, string Summary, IReadOnlyList<string> Lines)
{
    public bool Ok => State is IntegrationState.Installed or IntegrationState.Unverified or IntegrationState.Dev or IntegrationState.Mod;

    /// <summary>The integration of this install is a mod of the game (mkw-recomp: the MKWMP mod), not files beside its executable: the window words it so.</summary>
    public bool AsMod { get; init; }

    /// <summary>The mod is in use and the launcher's older game patch (winmm.dll beside mkw.exe) is still there: INSTALL takes it out.</summary>
    public bool LeftoverPatch { get; init; }

    /// <summary>Under Wine / Proton the game patch is there but the DLL override that makes Wine load it is not: INSTALL sets it.</summary>
    public bool OverrideMissing { get; init; }

    /// <summary>What blocks PLAY about MKWMP's files in the game, for the player; null when nothing does.</summary>
    public string? Problem => !Ok ? Summary
        : LeftoverPatch ? Strings.PatchState("leftover")
        : OverrideMissing ? Strings.PatchState("no_override")
        : null;
}

/// <summary>The verified files the launcher carries or downloaded for this version (name → bytes on disk).</summary>
public sealed record IntegrationPackage(string Version, IReadOnlyDictionary<string, string> Files, IReadOnlyDictionary<string, string> Sha256)
{
    /// <summary>No files: for an install whose target says <see cref="IGameTarget.NeedsPackage"/> is false.</summary>
    public static IntegrationPackage Empty(string version) => new(version, new Dictionary<string, string>(), new Dictionary<string, string>());

    /// <summary>
    /// A development package: its list of hashes is not signed by the release key. The launcher
    /// takes one only when told to (<c>MKWMP_DEV_PACKAGE=1</c>, or <c>dev_package = true</c> in
    /// client.toml), and the report of what it installed says so.
    /// </summary>
    public bool Development { get; init; }
}

/// <summary>What the hook reads (mkwmp.toml): see docs/contracts/LAUNCHER_API.md.</summary>
/// <param name="LauncherExe">The launcher's executable with forward slashes, for the mod to start it as its overlay host later; null when unknown (run from the dotnet host).</param>
public sealed record SessionFile(string Server, string Name, string Launcher, string LauncherToken, int VoicePort, string? Ticket, int Slot, string? LobbyCaPem, string? LauncherExe = null)
{
    /// <summary>
    /// The session file of a game started with no server chosen: only how to reach this launcher
    /// (for the overlay). A target writes no lobby, name, ticket or certificate for it, so the hook
    /// stays out of the game's online play.
    /// </summary>
    public static SessionFile Offline(string launcher, string launcherToken, string? launcherExe) =>
        new("", "", launcher, launcherToken, 0, null, 1, null, launcherExe);

    /// <summary>No server: see <see cref="Offline"/>.</summary>
    public bool IsOffline => Server.Length == 0;
}

public sealed record LaunchOptions(string? CustomCommand);

public interface IGameProcess : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
}

public sealed record SupportItem(string Name, string? SourcePath, string? Text);
