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
}
