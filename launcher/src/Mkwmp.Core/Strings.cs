// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;

namespace Mkwmp.Core;

/// <summary>
/// Every text the player can read that the core produces, in one place so the window can
/// localize it later. The English is the Rust launcher's, verbatim: the server's refusals are
/// matched against some of these (<see cref="NotRegistered"/>), and the tests pin them.
/// </summary>
public static class Strings
{
    // ---- seat API -------------------------------------------------------------------------
    public const string Unreachable = "Server unreachable: retry";
    public const string WrongPin = "Wrong PIN for this gamertag";
    public const string NotRegistered = "Register this gamertag first";
    public const string NoConsent = "Confirm you're 18 or older and accept the privacy policy (SETTINGS > PRIVACY)";
    public const string PrivacyUrl = "https://anarqz.github.io/mkwmp/#privacy";
    public const string GuideUrl = "https://anarqz.github.io/mkwmp/";
    public const string InUse = "This gamertag is in use (another launcher, or a seat not expired yet): retrying";
    public const string QueueFull = "The server and its queue are full: retrying";
    public const string SessionExpired = "session expired";
    public const string Taken = "that gamertag is already registered";
    public const string SignedOut = "signed out";
    public const string ServerError = "server error";
    public const string BadAnswer = "bad answer";
    public const string CouldNotRegister = "could not register";
    public const string CouldNotDelete = "could not delete the profile";
    public const string ServerTooOld = "the server is too old (no lobbyEndpoint)";
    public const string CantReachNow = "can't reach the server right now";
    public const string RegisterFirst = "register your gamertag first";
    public const string NotConnectedYet = "not connected to the server yet";

    public static string CannotReach(string why) => $"cannot reach the server: {why}";
    public static string ServerAnswered(int status) => $"server answered {status.ToString(CultureInfo.InvariantCulture)}";
    public static string NotAnMkwmpServer(string why) => $"not an mkwmp server: {why}";
    public static string BadAnswerWhy(string why) => $"bad answer: {why}";
    public static string Limited(long seconds) => $"too many tries; wait {WaitText(seconds)}";

    /// <summary>"45 s" under a minute and a half, whole minutes (rounded up) after.</summary>
    public static string WaitText(long seconds) => seconds < 90
        ? $"{Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture)} s"
        : $"{((seconds + 59) / 60).ToString(CultureInfo.InvariantCulture)} min";

    // ---- what blocks PLAY -----------------------------------------------------------------
    public const string PickGamertag = "Pick a gamertag: 3-16 letters, digits, - or _ (no spaces)";
    public const string NeedPin = "Register your gamertag, or enter its 6-digit PIN";
    public const string SetServer = "Set the server URL";
    public const string InstallPatchFirst = "Install the game patch first: SETTINGS, GAME PATCH, INSTALL";
    public const string StopBeforeDelete = "Stop playing before deleting the profile";
    public const string PressStopFirst = "Press STOP first";

    /// <summary>PLAY while the launcher stands by for a game that signed in by itself (the overlay host).</summary>
    public const string GameRunsAlready = "The game is already running and holds its own seat: close it to PLAY from here";

    /// <summary>"Set the game folder (the one with mkw.exe)": the hint is the target's.</summary>
    public static string SetGameFolder(string folderHint) => $"Set the game folder ({folderHint})";

    public static string NotAGameFolder(string folderHints) => $"No game in that folder: pick {folderHints}";

    public static string AnotherLauncher(int port) =>
        $"Another MKWMP launcher is running (port {port.ToString(CultureInfo.InvariantCulture)} is in use): close it";

    public static string ExpiredAndTaken(string name) =>
        $"{name} expired on the server and someone else registered it: pick another gamertag";

    // ---- the status pill (the session's state machine) ------------------------------------
    public const string Ready = "READY · PRESS PLAY";
    public const string Connecting = "CONNECTING…";
    public const string TakingASeat = "TAKING A SEAT…";
    public const string StartFromSteam = "START THE GAME FROM STEAM";
    public const string PickOnlinePlay = "IN GAME · PICK ONLINE PLAY";
    public const string SeatedStartFromSteam = "SEATED · START THE GAME FROM STEAM";
    public const string SeatedHaveFun = "SEATED · HAVE FUN!";

    public static string Queued(int position) => $"SERVER FULL · YOU ARE #{position.ToString(CultureInfo.InvariantCulture)} IN THE QUEUE";

    // ---- the server pill ------------------------------------------------------------------
    public const string CheckingServer = "CHECKING THE SERVER…";
    public const string NotConnected = "NOT CONNECTED";
    public const string ServerOffline = "SERVER OFFLINE";

    /// <summary>The server pill, as the Rust window wrote it.</summary>
    public static string ServerPill(ServerView server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.State switch
        {
            ServerState.Checking => CheckingServer,
            ServerState.NotAsked => NotConnected,
            ServerState.Offline => ServerOffline,
            _ => string.Create(CultureInfo.InvariantCulture, $"ONLINE · {server.SeatsFree}/{server.SeatsTotal} SEATS FREE")
                + (server.Queued > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {server.Queued} IN QUEUE") : ""),
        };
    }

    // ---- profile card ---------------------------------------------------------------------
    public const string NameRule = "3-16 letters, digits, - or _ (no spaces)";
    public const string ProfileDeleted = "Profile deleted. Pick a gamertag to create a new one.";
    public const string NameIsRegistered = "That gamertag is registered. If it's yours, enter its PIN.";
    public const string KeepYourPin = "Your PIN: keep it to use this gamertag on another device.";
    public const string RegisterToPlay = "Register a gamertag to play. You'll get a 6-digit PIN.";

    // ---- friends --------------------------------------------------------------------------
    public const string RegisterToAddFriends = "Register your gamertag to add friends.";
    public const string LoadingFriends = "Loading your friends…";
    public const string FriendsAreOnServer = "Friends are kept on the server, and are your friends in the game.";
    public const string SignedOutTryAgain = "signed out; try again";

    public static string FriendsNow(string name) => $"You and {name} are friends now.";
    public static string RequestSent(string name) => $"Request sent to {name}.";
    public static string RequestDeclined(string name) => $"Request from {name} declined.";
    public static string RequestCancelled(string name) => $"Request to {name} cancelled.";
    public static string FriendRemoved(string name) => $"{name} removed from your friends.";

    // ---- reports --------------------------------------------------------------------------
    public const string ReportWithdrawn = "Report withdrawn.";
    public const string ReportChecking = "Checking…";
    public const string ReportCantCheck = "Can't check right now.";
    public const int ReportDetailsMax = 500;

    public static string ReportSent(string reported) => $"Report about {reported} sent. The server's host will review it.";

    /// <summary>Why a player can be reported (the server's codes), with what the launcher shows.</summary>
    public static IReadOnlyList<ReportReason> ReportReasons { get; } =
    [
        new("harassment", "Harassment or bullying"),
        new("hate", "Hate speech or slurs"),
        new("sexual", "Sexual content"),
        new("threats", "Threats or doxxing"),
        new("disruption", "Loud noise, music or spam"),
        new("other", "Something else"),
    ];

    // ---- notifications --------------------------------------------------------------------
    public const string FriendOnlineBody = "Your friend just opened MKWMP.";
    public const string FriendPlayingBody = "Your friend is in Mario Kart World online.";
    public const string FriendRequestBody = "Open MKWMP to accept or decline.";
    public const string TrayHintTitle = "MKWMP is still running";
    public const string TrayHintBody = "It's in the system tray, so friends can see you online. Quit from the tray icon.";
    public const string TestToastTitle = "Ana is online";
    public const string TestToastBody = "This is how news about your friends will look.";
    public const string LogsSavedTitle = "Logs saved";

    public static string IsOnline(string name) => $"{name} is online";
    public static string IsPlaying(string name) => $"{name} is playing";
    public static string WantsToBeFriend(string name) => $"{name} wants to be your friend";
    public static string Saved(string path) => $"Saved {path}";

    // ---- updates --------------------------------------------------------------------------
    public const string UpdatesOff = "updates off";
    public const string CheckingForUpdates = "checking for updates…";
    public const string UpdatedRestarting = "updated; restarting…";
    public const string UpToDate = "up to date";
    public const string CannotUpdateItself = "this build of the launcher cannot update itself";

    public static string UpdateAvailable(string version) => $"version {version} is available";
    public static string Downloading(string version) => $"downloading {version}…";
    public static string UpdateFailed(string why) => $"update failed: {why}";

    // ---- the game's integration (generic part) --------------------------------------------
    public const string PatchInstalled = "Game patch installed.";
    public const string PatchRemoved = "Removed. PLAY needs INSTALL again.";
    public const string PatchWorking = "Working on the game patch…";
    public const string NoBundleAndUpdatesOff = "no game/ folder next to the launcher, and updates are off";
    public const string PressPlayInLauncher = "press PLAY in the mkwmp launcher";
    public const string FriendsStillLoading = "friends are still loading: retry";

    // ---- devices --------------------------------------------------------------------------
    public const string SystemDefault = "System default";
    public const string CameraDllMissing = "mkwmp-camera.dll was not found next to the launcher or in the game's MKWMP mod";

    public static string CameraDllOtherAbi(int abi) =>
        $"mkwmp-camera.dll speaks another version ({abi.ToString(CultureInfo.InvariantCulture)}): update MKWMP";

    public static string CameraDllBroken(string why) => $"mkwmp-camera.dll could not be loaded: {why}";

    // ---- the game's own settings ----------------------------------------------------------
    public const string GameSettingsCloseGame = "Close the game to change its settings";

    public static string GameSettingsNone(string game) => $"{game} has no settings the launcher can change";

    // ---- installing the game's runtime (the pinned build) ---------------------------------
    public const string RuntimeFolderOccupied = "That folder has other files in it: pick an empty folder, or a new one";
    public const string RuntimePaused = "Download paused: it continues from where it stopped";
    public const string RuntimeNone = "This game has no runtime the launcher can install";

    public static string RuntimeFolderForeign(string name, string? found) =>
        $"That folder holds {(found is null ? $"a {name} MKWMP does not know" : $"{name} {found}")}, which MKWMP did not install. "
        + "MKWMP leaves it alone: pick another folder and MKWMP's build goes beside it";

    public static string RuntimeCloseGame(string name) => $"Close the game to update {name}";

    public static string RuntimeNoSpace(string drive, long need, long free) =>
        $"Not enough free space on {drive}: {Gigabytes(need)} needed, {Gigabytes(free)} free";

    public static string RuntimeDownloadFailed(string asset, string why) => $"download {asset}: {why}";

    public static string RuntimeBadDownload(string asset) =>
        $"{asset} is not the file this MKWMP expects (size or checksum mismatch): it was thrown away, try again";

    public static string RuntimeUnsafeArchive(string entry) => $"the download holds a file with an unsafe name ({entry}): nothing was installed";

    public static string RuntimeArchiveLacks(string file) => $"the download holds no {file}: nothing was installed";

    public static string RuntimeNotRecognised(string name, string version) =>
        $"what was installed is not {name} {version}: everything was put back as it was";

    public static string RuntimeInstallFailed(string name, string why) => $"installing {name} failed, everything was put back as it was: {why}";

    public static string RuntimeInstalled(string name, string version) => $"{name} {version} installed";

    public static string RuntimeUpdated(string name, string version) => $"{name} updated to {version}";

    // ---- setting the game up (the runtime's setup, run by the launcher) --------------------
    public const string SetupNotSetUp = "Set the game up: MKWMP builds it on this device from your own copy of the game";
    public const string SetupUnfinished = "The game's setup did not finish: continue it";
    public const string SetupGameMismatch = "The game was built for another version of its runtime: it must be built again";
    public const string SetupRunning = "The game is being set up";
    public const string SetupDone = "The game is set up";
    public const string SetupCancelled = "Setup stopped: it continues from where it stopped";
    public const string SetupNone = "This game needs no setup from the launcher";
    public const string SetupCloseGame = "Close the game to set it up";
    public const string SetupAlreadyRunning = "A setup of this game is already running";
    public const string SetupWaitForInstall = "Wait for the install to finish, then set the game up";
    public const string SetupStarting = "Starting the setup";
    public const string SetupWaitingForLock = "Waiting for another setup of this game to finish";
    public const string SetupPickDump = "Pick your copy of the game: a .nup or .nx file, or the folder that holds exefs and romfs";
    public const string SetupToolsMissing = "The setup tools are missing from the game folder: install the game's runtime again";
    public const string SetupCpu = "This processor cannot run the game: it needs AVX2, FMA and BMI2 (Intel from 2013, AMD from 2015)";
    public const string SetupShadersOnline = "This step downloads a tool from NVIDIA the first time: check the internet connection and try again";
    public const string SetupShadersProton = "This step is not confirmed to work under Proton yet: send the log with a bug report";

    public static string SetupRuntimeChanged(string name, string version) =>
        $"{name} {version} is new here: the game is set up again for it (only what changed)";

    public static string SetupDumpGone(string path) => $"Your copy of the game is no longer at {path}: pick it again";

    public static string SetupDumpNotFound(string path) => $"Nothing at {path}: pick your copy of the game again";

    public static string SetupDumpNotAGame(string path) =>
        $"{path} is not a copy of the game: pick a .nup or .nx file, or the folder that holds exefs and romfs";

    public static string SetupPathTooLong(string folder, int limit) =>
        $"The game folder's path is too long ({folder.Length.ToString(CultureInfo.InvariantCulture)} characters, at most {limit.ToString(CultureInfo.InvariantCulture)}): install the game in a folder with a shorter path";

    public static string SetupNoSpace(string drive, long need, long free) =>
        $"Not enough free space on {drive} to set the game up: {Gigabytes(need)} needed, {Gigabytes(free)} free";

    public static string SetupWorking(string step) => $"Working ({step})";

    /// <summary>A step of the setup, for the player (mkw-recomp's prepare.py: check, exefs, romfs, generate, compile, shaders, warm, cleanup).</summary>
    public static string SetupStep(string step, bool copy) => step switch
    {
        "check" => "Reading your game",
        "exefs" => "Copying the game's program",
        "romfs" => copy ? "Copying the game's data" : "Finding the game's data",
        "generate" => "Translating the game",
        "compile" => "Building the game",
        "shaders" => "Preparing shaders",
        "warm" => "Warming up the graphics",
        "cleanup" => "Finishing up",
        "" => SetupStarting,
        _ => SetupWorking(step),
    };

    /// <summary>
    /// A failed setup, for the player: the setup's code (mkw-recomp's prepare.py and the three its own
    /// start screen adds) with its detail inside. An unknown code shows the detail as it came.
    /// </summary>
    /// <param name="step">The label of the step that was under way, or null.</param>
    public static string SetupError(string code, string? detail, string? step, int? exitCode)
    {
        var d = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim();
        return code switch
        {
            "source" when d is null || d.StartsWith("no earlier setup", StringComparison.Ordinal) => SetupPickDump,
            "source" => $"{d} could not be unpacked: the file may be damaged or incomplete",
            "moved" => SetupDumpGone(d ?? "the folder the last setup used"),
            "nogame" => d is null ? SetupPickDump : SetupDumpNotAGame(d),
            "encrypted" => "This copy of the game is still encrypted: the setup needs a decrypted one" + (d is null ? "" : $" ({d})"),
            "version" => $"This version of the game is not supported{(d is null ? "" : $" (build {d})")}: the setup knows 1.0.0 and 1.8.0",
            "romfs" => $"This copy of the game is incomplete: its data has only {d ?? "some"} of the game's files",
            "mismatch" => $"This copy mixes two versions of the game{(d is null ? "" : $" ({d})")}: the program and the data must be from the same one",
            "space" => $"Not enough free disk space: the setup needs {d ?? "more room"}",
            "failed" => $"{(step is null ? "The setup" : $"\"{step}\"")} failed: {d ?? "no reason given"}. Try again: it continues from where it stopped",
            "io" => $"A file could not be read or written: {d ?? "no reason given"}. Try again: it continues from where it stopped",
            "no_setup" => SetupToolsMissing,
            "cancelled" => SetupCancelled,
            "stopped" => $"The setup ended unexpectedly{(step is null ? "" : $" during \"{step}\"")}"
                + $"{(exitCode is { } exit ? $" (exit code {exit.ToString(CultureInfo.InvariantCulture)})" : "")}. Try again: it continues from where it stopped",
            _ => d ?? $"The setup failed ({code})",
        };
    }

    private static string Gigabytes(long bytes) => (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
}
