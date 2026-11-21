// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;

namespace Mkwmp.Core;

/// <summary>
/// Every text the player can read that the core produces. Each one is a key of the launcher's
/// catalogs (<c>Mkwmp.App/i18n/*.json</c>, the <c>core.*</c> keys): the English catalog is built
/// into this assembly, so the core speaks English by itself (headless, and in the tests, which pin
/// that English), and the window gives it the player's language with <see cref="Use"/>.
/// The English is the Rust launcher's, verbatim.
/// <para>
/// A text is looked up when it is asked for: the views the launcher rebuilds (the status pill,
/// problems, the game's settings) follow a language change at once (<see cref="Changed"/>); a
/// note already shown, such as the answer to the last action, stays as it was written.
/// </para>
/// <para>
/// Not localized on purpose: the session log and bug reports (read by the maintainers), and what
/// the game shows from the local API (<see cref="OnlineOffInGame"/>: plain ASCII).
/// </para>
/// </summary>
public static class Strings
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> BuiltIn = new(() => Catalogs.BuiltIn(Locales.English));
    private static Func<string, string?>? _lookup;
    private static CultureInfo _culture = CultureInfo.InvariantCulture;
    private static int _generation;

    /// <summary>The English catalog built into the core (every key, the window's too).</summary>
    public static IReadOnlyDictionary<string, string> English => BuiltIn.Value;

    /// <summary>How numbers inside the texts are written ("1.5 GB" or "1,5 GB"): invariant until <see cref="Use"/> says otherwise.</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>Goes up with each <see cref="Use"/>: what was built from the texts is stale when it differs.</summary>
    public static int Generation => Volatile.Read(ref _generation);

    /// <summary>After <see cref="Use"/>: rebuild what holds texts.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Gives the core the player's language: <paramref name="lookup"/> answers a key with its text,
    /// or null to keep the English. Null <paramref name="lookup"/>: English again.
    /// </summary>
    public static void Use(Func<string, string?>? lookup, CultureInfo? culture)
    {
        Volatile.Write(ref _lookup, lookup);
        Volatile.Write(ref _culture, culture ?? CultureInfo.InvariantCulture);
        Interlocked.Increment(ref _generation);
        Changed?.Invoke();
    }

    /// <summary>The text of a catalog key in the language in use, else in English, else the key.</summary>
    public static string T(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Volatile.Read(ref _lookup)?.Invoke(key) ?? (BuiltIn.Value.TryGetValue(key, out var text) ? text : key);
    }

    /// <summary>The text of a key that may not be in the catalogs (a game setting's label): <paramref name="english"/> then.</summary>
    public static string T(string key, string english)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Volatile.Read(ref _lookup)?.Invoke(key) ?? english;
    }

    /// <summary>The text of <paramref name="key"/> with each <c>{name}</c> replaced.</summary>
    public static string F(string key, params (string Name, string? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var text = T(key);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{" + name + "}", value ?? "", StringComparison.Ordinal);
        }
        return text;
    }

    /// <summary>A whole number as the language in use writes it.</summary>
    public static string N(long number) => number.ToString(Culture);

    /// <summary>A size for the player: "1.4 GB" from a gigabyte on, whole megabytes below ("217 MB"), as the language in use writes them.</summary>
    public static string Size(long bytes) => bytes >= 1_000_000_000
        ? Gigabytes(bytes)
        : F("core.megabytes", ("n", Math.Round(bytes / 1e6).ToString("0", Culture)));

    /// <summary>Upper case by the rules of the language in use (the status pill).</summary>
    public static string Upper(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.ToUpper(Culture);
    }

    // ---- mods (launcher/docs/MODS.md) -----------------------------------------------------
    public static string ModsCloseGame => T("core.mods.close_game");
    public static string ModsNone(string game) => F("core.mods.none", ("game", game));
    public static string ModsNotAMod(string name) => F("core.mods.not_a_mod", ("name", name));
    public static string ModsManaged => T("core.mods.managed");
    public static string ModsChangesCode => T("core.mods.changes_code");
    public static string ModsCodeInstall => T("core.mods.code_install");
    public static string ModsRunsCode => T("core.mods.runs_code");
    public static string ModsEnvironment => T("core.mods.environment");
    public static string ModsWarnShaders => T("core.mods.warn_shaders");
    public static string ModsWarnGraphics => T("core.mods.warn_graphics");
    public static string ModsShares(int files, string other) => F("core.mods.shares", ("n", N(files)), ("other", other));
    public static string ModsNoOptions => T("core.mods.no_options");
    public static string ModsFailed(string detail) => F("core.mods.failed", ("detail", detail));
    public static string ModsCancelled => T("core.mods.cancelled");
    public static string ModsNoSuchFile(string path) => F("core.mods.no_such_file", ("path", path));
    public static string ModsLayout => T("core.mods.layout");
    public static string ModsVariants => T("core.mods.variants");
    public static string ModsName => T("core.mods.name");
    public static string ModsExists(string name) => F("core.mods.exists", ("name", name));
    public static string ModsTooMany(int most) => F("core.mods.too_many", ("n", N(most)));
    public static string ModsTooBig(int files, long bytes) => F("core.mods.too_big", ("files", N(files)), ("size", Size(bytes)));
    public static string ModsUnsafe(string detail) => F("core.mods.unsafe", ("detail", detail));
    public static string ModsUnsafeName(string name) => F("core.mods.unsafe_name", ("name", name));
    public static string ModsUnsafeLink(string name) => F("core.mods.unsafe_link", ("name", name));
    public static string ModsUnsafeTwice(string name) => F("core.mods.unsafe_twice", ("name", name));
    public static string ModsUnpack(string detail) => F("core.mods.unpack", ("detail", detail));
    public static string ModsNotAnArchive => T("core.mods.not_an_archive");
    public static string ModsPassword => T("core.mods.password");
    public static string ModsGameFiles(string detail) => F("core.mods.game_files", ("detail", detail));
    public static string ModsGameFilesNone => T("core.mods.game_files_none");
    public static string ModsInstalled(string name) => F("core.mods.installed", ("name", name));
    public static string ModsBadLink => T("core.mods.bad_link");
    public static string ModsDownloadFailed(string detail) => F("core.mods.download_failed", ("detail", detail));
    public static string ModsDownloadTooBig => T("core.mods.download_too_big");
    public static string GameBananaFailed(string detail) => F("core.mods.gb_failed", ("detail", detail));
    public static string GameBananaNotThisGame => T("core.mods.gb_not_this_game");
    public static string GameBananaNotClean(string scan) => F("core.mods.gb_not_clean", ("scan", scan));
    public static string GameBananaChecksum => T("core.mods.gb_checksum");
    public static string GameBananaBusy => T("core.mods.gb_busy");

    // ---- seat API -------------------------------------------------------------------------
    public static string Unreachable => T("core.unreachable");
    public static string WrongPin => T("core.wrong_pin");
    public static string NotRegistered => T("core.not_registered");
    public static string NoConsent => T("core.no_consent");
    public const string GuideUrl = "https://anarqz.github.io/mkwmp/";
    public static string InUse => T("core.in_use");
    public static string QueueFull => T("core.queue_full");
    public static string SessionExpired => T("core.session_expired");
    public static string Taken => T("core.taken");
    public static string SignedOut => T("core.signed_out");
    public static string ServerError => T("core.server_error");
    public static string BadAnswer => T("core.bad_answer");
    public static string CouldNotRegister => T("core.could_not_register");
    public static string CouldNotDelete => T("core.could_not_delete");
    public static string ServerTooOld => T("core.server_too_old");
    public static string CantReachNow => T("core.cant_reach_now");
    public static string RegisterFirst => T("core.register_first");
    public static string NotConnectedYet => T("core.not_connected_yet");

    public static string CannotReach(string why) => F("core.cannot_reach", ("why", why));
    public static string ServerAnswered(int status) => F("core.server_answered", ("status", status.ToString(CultureInfo.InvariantCulture)));
    public static string NotAnMkwmpServer(string why) => F("core.not_an_mkwmp_server", ("why", why));
    public static string BadAnswerWhy(string why) => F("core.bad_answer_why", ("why", why));
    public static string Limited(long seconds) => F("core.limited", ("wait", WaitText(seconds)));

    /// <summary>Why a request did not get through, inside <see cref="CannotReach"/> and <see cref="RuntimeDownloadFailed"/>.</summary>
    public static string TimedOut => T("core.why.timed_out");
    public static string ServerRefused => T("core.why.refused");
    public static string ClosedEarly => T("core.why.closed_early");

    /// <summary>"45 s" under a minute and a half, whole minutes (rounded up) after.</summary>
    public static string WaitText(long seconds) => seconds < 90
        ? F("core.wait_seconds", ("n", N(Math.Max(1, seconds))))
        : F("core.wait_minutes", ("n", N((seconds + 59) / 60)));

    // ---- what blocks PLAY -----------------------------------------------------------------
    public static string PickGamertag => T("core.pick_gamertag");
    public static string NeedPin => T("core.need_pin");
    public static string SetServer => T("core.set_server");
    public static string InstallPatchFirst => T("core.install_patch_first");
    public static string StopBeforeDelete => T("core.stop_before_delete");
    public static string PressStopFirst => T("core.press_stop_first");

    /// <summary>PLAY while the launcher stands by for a game that signed in by itself (the overlay host).</summary>
    public static string GameRunsAlready => T("core.game_runs_already");

    /// <summary>"Set the game folder (the one with mkw.exe)": the hint is the target's.</summary>
    public static string SetGameFolder(string folderHint) => F("core.set_game_folder", ("hint", folderHint));

    public static string NotAGameFolder(string folderHints) => F("core.not_a_game_folder", ("hints", folderHints));

    public static string AnotherLauncher(int port) => F("core.another_launcher", ("port", port.ToString(CultureInfo.InvariantCulture)));

    public static string ExpiredAndTaken(string name) => F("core.expired_and_taken", ("name", name));

    // ---- offline first: no server chosen ---------------------------------------------------
    /// <summary>An online action asked for while no server is set (register, friends, reports).</summary>
    public static string OnlineOff => T("core.online_off");

    /// <summary>What the game's Online Play gets from the launcher while no server is set (shown by the mod as a notice).</summary>
    /// <remarks>Plain ASCII without quotes, &lt;, &gt; or &amp;: the mod reads it out of the JSON as it is written, and keeps 127 characters. So it stays English.</remarks>
    public const string OnlineOffInGame = "Online is off: connect to a server in the MKWMP launcher (Settings, Online)";

    /// <summary>The profile card while offline: the name is only on this device.</summary>
    public static string LocalNameOnly => T("core.local_name_only");

    public static string PickLocalName => T("core.pick_local_name");

    // ---- asking a server who it is (Connect) ----------------------------------------------
    public static string ProbeInvalid => T("core.probe_invalid");
    public static string ProbeUnreachable => T("core.probe_unreachable");
    public static string ProbeNotMkwmp => T("core.probe_not_mkwmp");
    public static string ProbeTooOld => T("core.probe_too_old");

    /// <summary>"MKWMP server · 20/32 seats free" (and the queue, when there is one).</summary>
    public static string ProbeOk(ServerProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        return F(probe.Queued > 0 ? "core.probe_ok_queue" : "core.probe_ok",
            ("free", N(probe.SeatsFree)), ("total", N(probe.SeatsTotal)), ("queued", N(probe.Queued)));
    }

    // ---- the server list (the beholder) and the privacy policy ----------------------------
    public static string BeholderUnreachable => T("core.beholder_unreachable");
    public static string BeholderTooLarge => T("core.beholder_too_large");
    public static string PrivacyUnreachable => T("core.privacy_unreachable");
    public static string ConsentNotStored => T("core.consent_not_stored");
    public static string PrivacyChanged => T("core.privacy_changed");

    /// <summary>A server without <c>GET /privacy</c> (before 1.4.0). The launcher has no policy of its own to show instead.</summary>
    public static string PrivacyNone => T("core.privacy_none");

    /// <summary>No server is chosen (offline): there is no policy to show.</summary>
    public static string PrivacyNoServer => T("core.privacy_no_server");

    // ---- the status pill (the session's state machine) ------------------------------------
    public static string Ready => T("core.status.ready");
    public static string ReadyOffline => T("core.status.ready_offline");
    public static string StartingGame => T("core.status.starting_game");
    public static string PlayingOffline => T("core.status.playing_offline");
    public static string Connecting => T("core.status.connecting");
    public static string TakingASeat => T("core.status.taking_a_seat");
    public static string StartFromSteam => T("core.status.start_from_steam");
    public static string PickOnlinePlay => T("core.status.pick_online_play");
    public static string SeatedStartFromSteam => T("core.status.seated_start_from_steam");
    public static string SeatedHaveFun => T("core.status.seated_have_fun");

    public static string Queued(int position) => F("core.status.queued", ("position", N(position)));

    // ---- the server pill ------------------------------------------------------------------
    public static string CheckingServer => T("core.server.checking");
    public static string NotConnected => T("core.server.not_connected");
    public static string ServerOffline => T("core.server.offline");

    /// <summary>The server pill, as the Rust window wrote it.</summary>
    public static string ServerPill(ServerView server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.State switch
        {
            ServerState.Checking => CheckingServer,
            ServerState.NotAsked => NotConnected,
            ServerState.Offline => ServerOffline,
            _ => F(server.Queued > 0 ? "core.server.online_queue" : "core.server.online",
                ("free", N(server.SeatsFree)), ("total", N(server.SeatsTotal)), ("queued", N(server.Queued))),
        };
    }

    // ---- profile card ---------------------------------------------------------------------
    public static string NameRule => T("core.name_rule");
    public static string ProfileDeleted => T("core.profile_deleted");
    public static string NameIsRegistered => T("core.name_is_registered");
    public static string KeepYourPin => T("core.keep_your_pin");
    public static string RegisterToPlay => T("core.register_to_play");

    // ---- friends --------------------------------------------------------------------------
    public static string RegisterToAddFriends => T("core.register_to_add_friends");
    public static string LoadingFriends => T("core.loading_friends");
    public static string FriendsAreOnServer => T("core.friends_are_on_server");
    public static string SignedOutTryAgain => T("core.signed_out_try_again");

    public static string FriendsNow(string name) => F("core.friends_now", ("name", name));
    public static string RequestSent(string name) => F("core.request_sent", ("name", name));
    public static string RequestDeclined(string name) => F("core.request_declined", ("name", name));
    public static string RequestCancelled(string name) => F("core.request_cancelled", ("name", name));
    public static string FriendRemoved(string name) => F("core.friend_removed", ("name", name));

    // ---- reports --------------------------------------------------------------------------
    public static string ReportWithdrawn => T("core.report_withdrawn");
    public static string ReportChecking => T("core.report_checking");
    public static string ReportCantCheck => T("core.report_cant_check");
    public const int ReportDetailsMax = 500;

    public static string ReportSent(string reported) => F("core.report_sent", ("name", reported));

    /// <summary>Why a player can be reported (the server's codes), with what the launcher shows.</summary>
    public static IReadOnlyList<ReportReason> ReportReasons =>
    [
        new("harassment", T("core.report.harassment")),
        new("hate", T("core.report.hate")),
        new("sexual", T("core.report.sexual")),
        new("threats", T("core.report.threats")),
        new("disruption", T("core.report.disruption")),
        new("other", T("core.report.other")),
    ];

    // ---- notifications --------------------------------------------------------------------
    public static string FriendOnlineBody => T("core.friend_online_body");
    public static string FriendPlayingBody => T("core.friend_playing_body");
    public static string FriendRequestBody => T("core.friend_request_body");
    public static string TrayHintTitle => T("core.tray_hint_title");
    public static string TrayHintBody => T("core.tray_hint_body");
    public static string TestToastTitle => T("core.test_toast_title");
    public static string TestToastBody => T("core.test_toast_body");
    public static string LogsSavedTitle => T("core.logs_saved_title");

    public static string IsOnline(string name) => F("core.is_online", ("name", name));
    public static string IsPlaying(string name) => F("core.is_playing", ("name", name));
    public static string WantsToBeFriend(string name) => F("core.wants_to_be_friend", ("name", name));
    public static string Saved(string path) => F("core.saved", ("path", path));

    // ---- updates --------------------------------------------------------------------------
    public static string UpdatesOff => T("core.updates_off");
    public static string CheckingForUpdates => T("core.checking_for_updates");
    public static string UpdatedRestarting => T("core.updated_restarting");
    public static string UpToDate => T("core.up_to_date");
    public static string CannotUpdateItself => T("core.cannot_update_itself");

    public static string UpdateAvailable(string version) => F("core.update_available", ("version", version));
    public static string Downloading(string version) => F("core.downloading", ("version", version));
    public static string UpdateFailed(string why) => F("core.update_failed", ("why", why));
    public static string UpdateReady(string version) => F("core.update_ready", ("version", version));
    public static string UpdateInstalling(string version) => F("core.update_installing", ("version", version));
    public static string UpdateNeedsNoGame => T("core.update_needs_no_game");
    public static string UpdateAfterGame => T("core.update_after_game");
    public static string UpdateNotInstalled(string why) => F("core.update_not_installed", ("why", why));

    // ---- the game's integration (generic part) --------------------------------------------
    public static string PatchInstalled => T("core.patch_installed");
    public static string PatchRemoved => T("core.patch_removed");
    public static string PatchWorking => T("core.patch_working");
    public static string ModInstalled => T("core.mod_installed");
    public static string ModWorking => T("core.mod_working");
    public static string PackageMissing(string version) => F("core.package_missing", ("version", version));

    /// <summary>What the game reads from the local API: English, as <see cref="OnlineOffInGame"/>.</summary>
    public const string PressPlayInLauncher = "press PLAY in the mkwmp launcher";
    public const string FriendsStillLoading = "friends are still loading: retry";

    public static string ReleaseListsNo(string what) => F("core.release_lists_no", ("what", what));
    public static string AutostartFailed(string why) => F("core.autostart_failed", ("why", why));

    // ---- devices --------------------------------------------------------------------------
    public static string SystemDefault => T("core.system_default");
    public static string CameraDllMissing => T("core.camera_dll_missing");

    public static string CameraDllOtherAbi(int abi) => F("core.camera_dll_other_abi", ("abi", abi.ToString(CultureInfo.InvariantCulture)));

    public static string CameraDllBroken(string why) => F("core.camera_dll_broken", ("why", why));

    // ---- the game's own settings ----------------------------------------------------------
    public static string GameSettingsCloseGame => T("core.gs.close_game");
    public static string GameSettingsNoneHere => T("core.gs.none_here");
    public static string GameSettingsRunning => T("core.gs.game_is_running");
    public static string GameSettingsNextStart => T("core.gs.next_start");
    public static string GameSettingsTonemapMissing => T("core.gs.tonemap_missing");
    public static string GameSettingsOwnedUpdateCheck => T("core.gs.owned_update_check");
    public static string GameSettingsOwnedStartScreen => T("core.gs.owned_start_screen");
    public static string GameSettingsOwnedLogicHz => T("core.gs.owned_logic_hz");
    public static string GameSettingRefusedToggle => T("core.gs.refused_toggle");
    public static string GameSettingRefusedNumber => T("core.gs.refused_number");
    public static string GameSettingRefusedLine => T("core.gs.refused_line");
    public static string GameSettingRefusedForm => T("core.gs.refused_form");
    public static string ModSettingsRunning => T("core.mod.game_is_running");
    public static string ModSettingsNoProfileYet => T("core.mod.no_profile_yet");

    public static string GameSettingsNone(string game) => F("core.gs.none", ("game", game));
    public static string GameSettingsLockedByEnv(string name) => F("core.gs.locked_env", ("name", name));
    public static string GameSettingsFolderGone(string folder) => F("core.gs.folder_gone", ("folder", folder));
    public static string GameSettingsReadOnly(string file) => F("core.gs.read_only", ("file", file));
    public static string GameSettingsSaveFailed(string why) => F("core.gs.save_failed", ("why", why));
    public static string GameSettingsNotASetting(string id) => F("core.gs.not_a_setting", ("id", id));
    public static string GameSettingLocked(string label, string why) => F("core.gs.locked", ("label", label), ("why", why));
    public static string GameSettingRefused(string label, string value, string why) => F("core.gs.refused", ("label", label), ("value", value), ("why", why));
    public static string GameSettingRefusedChoice(string options) => F("core.gs.refused_choice", ("options", options));
    public static string GameSettingRefusedRange(string min, string max) => F("core.gs.refused_range", ("min", min), ("max", max));
    public static string GameSettingRefusedLength(int max) => F("core.gs.refused_length", ("max", max.ToString(CultureInfo.InvariantCulture)));
    public static string ModSettingsNotASetting(string key) => F("core.mod.not_a_setting", ("key", key));

    // ---- installing the game's runtime (the pinned build) ---------------------------------
    public static string RuntimeFolderOccupied => T("core.runtime.folder_occupied");
    public static string RuntimePaused => T("core.runtime.paused");
    public static string RuntimeNone => T("core.runtime.none");

    public static string RuntimeFolderForeign(string name, string? found) => found is null
        ? F("core.runtime.folder_foreign_unknown", ("name", name))
        : F("core.runtime.folder_foreign", ("name", name), ("version", found));

    public static string RuntimeCloseGame(string name) => F("core.runtime.close_game", ("name", name));

    public static string RuntimeNoSpace(string drive, long need, long free) =>
        F("core.runtime.no_space", ("drive", drive), ("need", Gigabytes(need)), ("free", Gigabytes(free)));

    public static string RuntimeDownloadFailed(string asset, string why) => F("core.runtime.download_failed", ("asset", asset), ("why", why));

    public static string RuntimeBadDownload(string asset) => F("core.runtime.bad_download", ("asset", asset));

    public static string RuntimeUnsafeArchive(string entry) => F("core.runtime.unsafe_archive", ("entry", entry));

    public static string RuntimeArchiveLacks(string file) => F("core.runtime.archive_lacks", ("file", file));

    public static string RuntimeNotRecognised(string name, string version) => F("core.runtime.not_recognised", ("name", name), ("version", version));

    public static string RuntimeInstallFailed(string name, string why) => F("core.runtime.install_failed", ("name", name), ("why", why));

    public static string RuntimeInstalled(string name, string version) => F("core.runtime.installed", ("name", name), ("version", version));

    public static string RuntimeUpdated(string name, string version) => F("core.runtime.updated", ("name", name), ("version", version));
    public static string RuntimeUpdateNeeded(string name, string version) => F("core.runtime.update_needed", ("name", name), ("version", version));
    public static string RuntimeBusy(string name) => F("core.runtime.busy", ("name", name));

    // ---- setting the game up (the runtime's setup, run by the launcher) --------------------
    public static string SetupNotSetUp => T("core.setup.not_set_up");
    public static string SetupUnfinished => T("core.setup.unfinished");
    public static string SetupGameMismatch => T("core.setup.game_mismatch");
    public static string SetupRunning => T("core.setup.running");
    public static string SetupDone => T("core.setup.done");
    public static string SetupCancelled => T("core.setup.cancelled");
    public static string SetupNone => T("core.setup.none");
    public static string SetupCloseGame => T("core.setup.close_game");
    public static string SetupAlreadyRunning => T("core.setup.already_running");
    public static string SetupWaitForInstall => T("core.setup.wait_for_install");
    public static string SetupStarting => T("core.setup.starting");
    public static string SetupWaitingForLock => T("core.setup.waiting_for_lock");
    public static string SetupPickDump => T("core.setup.pick_dump");
    public static string SetupToolsMissing => T("core.setup.tools_missing");
    public static string SetupCpu => T("core.setup.cpu");

    /// <summary>The release's no-AVX2 package, on a processor without SSE4.2.</summary>
    public static string SetupCpuV2 => T("core.setup.cpu_v2");
    public static string SetupShadersOnline => T("core.setup.shaders_online");
    public static string SetupShadersProton => T("core.setup.shaders_proton");

    public static string SetupRuntimeChanged(string name, string version) => F("core.setup.runtime_changed", ("name", name), ("version", version));

    public static string SetupDumpGone(string path) => F("core.setup.dump_gone", ("path", path));

    public static string SetupDumpNotFound(string path) => F("core.setup.dump_not_found", ("path", path));

    public static string SetupDumpNotAGame(string path) => F("core.setup.dump_not_a_game", ("path", path));

    public static string SetupPathTooLong(string folder, int limit)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return F("core.setup.path_too_long", ("length", N(folder.Length)), ("limit", N(limit)));
    }

    public static string SetupNoSpace(string drive, long need, long free) =>
        F("core.setup.no_space", ("drive", drive), ("need", Gigabytes(need)), ("free", Gigabytes(free)));

    public static string SetupWorking(string step) => F("core.setup.working", ("step", step));

    /// <summary>A step of the setup, for the player (mkw-recomp's prepare.py: check, exefs, romfs, generate, compile, shaders, warm, cleanup).</summary>
    public static string SetupStep(string step, bool copy) => step switch
    {
        "check" => T("core.setup.step.check"),
        "exefs" => T("core.setup.step.exefs"),
        "romfs" => T(copy ? "core.setup.step.romfs_copy" : "core.setup.step.romfs_find"),
        "generate" => T("core.setup.step.generate"),
        "compile" => T("core.setup.step.compile"),
        "shaders" => T("core.setup.step.shaders"),
        "warm" => T("core.setup.step.warm"),
        "cleanup" => T("core.setup.step.cleanup"),
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
        var exit = exitCode?.ToString(CultureInfo.InvariantCulture);
        return code switch
        {
            "source" when d is null || d.StartsWith("no earlier setup", StringComparison.Ordinal) => SetupPickDump,
            "source" => F("core.setup.err.source", ("detail", d)),
            "moved" => SetupDumpGone(d ?? T("core.setup.dump_gone_last")),
            "nogame" => d is null ? SetupPickDump : SetupDumpNotAGame(d),
            "encrypted" => d is null ? T("core.setup.err.encrypted") : F("core.setup.err.encrypted_detail", ("detail", d)),
            "version" => d is null ? T("core.setup.err.version") : F("core.setup.err.version_detail", ("detail", d)),
            "romfs" => d is null ? T("core.setup.err.romfs_some") : F("core.setup.err.romfs", ("detail", d)),
            "mismatch" => d is null ? T("core.setup.err.mismatch") : F("core.setup.err.mismatch_detail", ("detail", d)),
            "space" => d is null ? T("core.setup.err.space_some") : F("core.setup.err.space", ("detail", d)),
            "failed" => F(step is null ? "core.setup.err.failed" : "core.setup.err.failed_step", ("step", step), ("detail", d ?? T("core.setup.err.no_reason"))),
            "io" => F("core.setup.err.io", ("detail", d ?? T("core.setup.err.no_reason"))),
            "no_setup" => SetupToolsMissing,
            "cancelled" => SetupCancelled,
            "stopped" => F((step, exit) switch
            {
                (null, null) => "core.setup.err.stopped",
                (null, _) => "core.setup.err.stopped_exit",
                (_, null) => "core.setup.err.stopped_step",
                _ => "core.setup.err.stopped_step_exit",
            }, ("step", step), ("exit", exit)),
            _ => d ?? F("core.setup.err.unknown", ("code", code)),
        };
    }

    // ---- mkw-recomp (the first game target) -------------------------------------------------
    public static string RecompFolderHint => T("core.recomp.folder_hint");
    public static string RecompAssetPatch => T("core.recomp.asset_patch");
    public static string RecompAssetVoice => T("core.recomp.asset_voice");
    public static string RecompAssetMod => T("core.recomp.asset_mod");
    public static string RecompExplainMissing => T("core.recomp.explain_missing");
    public static string RecompRebuild => T("core.recomp.rebuild");
    public static string RecompCredit => T("core.recomp.credit");
    public static string RecompCloseForPatch => T("core.recomp.close_for_patch");
    public static string RecompCloseForVoice => T("core.recomp.close_for_voice");
    public static string RecompStartNoProcess => T("core.recomp.start_no_process");
    public static string RecompCloseGameFirst => T("core.recomp.close_game_first");
    public static string RecompCloseForMod => T("core.recomp.close_for_mod");

    public static string RecompProblemOther(string? version, string supported) => F("core.recomp.problem_other", ("version", version), ("supported", supported));
    public static string RecompProblemUnknown(string supported) => F("core.recomp.problem_unknown", ("supported", supported));
    public static string RecompExplainSupported(string? version) => F("core.recomp.explain_supported", ("version", version));

    public static string RecompExplainOther(string supported, string recommended, string download) =>
        F("core.recomp.explain_other", ("supported", supported), ("recommended", recommended), ("download", download));

    /// <summary>"Game 1.2.0: MKWMP supports game 1.0.0 or 1.8.0 (a newer MKWMP may support 1.2.0)".</summary>
    public static string RecompGameProblem(string version, IEnumerable<string> supported) =>
        F("core.recomp.game_problem", ("version", version), ("supported", string.Join(T("core.recomp.or"), supported)));

    public static string RecompChecksum(string name) => F("core.recomp.checksum", ("name", name));
    public static string RecompNotTheMod(string name) => F("core.recomp.not_the_mod", ("name", name));
    public static string DevPackageOff(string env, string why) => F("core.dev_package_off", ("env", env), ("why", why));
    public static string RecompInstallFailed(string what, string why) => F("core.recomp.install_failed", ("what", what), ("why", why));
    public static string RecompInstallPatchFailed(string why) => F("core.recomp.install_patch_failed", ("why", why));
    public static string RecompInstallVoiceFailed(string why) => F("core.recomp.install_voice_failed", ("why", why));
    public static string RecompWriteTomlFailed(string why) => F("core.recomp.write_toml_failed", ("why", why));
    public static string RecompStartFailed(string why) => F("core.recomp.start_failed", ("why", why));

    /// <summary>The state of the online play files in the game folder, in a line.</summary>
    public static string PatchState(string state, string version = "", string env = "") =>
        F("core.patch." + state, ("version", version), ("env", env));

    private static string Gigabytes(long bytes) => F("core.gigabytes", ("n", (bytes / 1e9).ToString("0.0", Culture)));
}
