// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>Where a row of the play checklist is.</summary>
public enum CheckState
{
    /// <summary>In place.</summary>
    Ok,

    /// <summary>Missing or wrong: PLAY is refused until it is fixed.</summary>
    Needed,

    /// <summary>Worth fixing; PLAY works.</summary>
    Warning,

    /// <summary>Under way (a download, the setup, MKWMP's files going in): <see cref="CheckItem.Progress"/> when known.</summary>
    Working,

    /// <summary>Not for now: offline, or it waits for a row above.</summary>
    NotApplicable,
}

/// <summary>The rows, in the order the player fixes them.</summary>
public enum CheckId
{
    /// <summary>What blocks PLAY that no other row is about (another launcher holds the port, a game runs already).</summary>
    Other,
    Runtime,
    GameCopy,
    GameVersion,
    GameSetup,
    MkwmpFiles,
    LauncherUpdate,
    Online,
}

/// <summary>What a row's button does: the window maps each to the command it already has.</summary>
public enum CheckActionKind
{
    /// <summary>Downloads and installs the pinned runtime (also over an older build of MKWMP's, and beside the player's own).</summary>
    InstallRuntime,

    /// <summary>The card with the runtime's download or the setup, live.</summary>
    ShowProgress,

    /// <summary>The launcher's picker for the game folder (Settings › Game).</summary>
    ChooseGameFolder,

    /// <summary>The launcher's picker for the player's own copy of the game, then the setup from it.</summary>
    ChooseCopy,

    /// <summary>"Use another copy of the game", once it is set up.</summary>
    ChangeCopy,

    /// <summary>The setup from the copy it knows: set up, continue, set up again for this runtime, try again.</summary>
    RunSetup,

    /// <summary>"Set up the game again": every step that builds it, from the same copy (asks first: it is long).</summary>
    Rebuild,

    /// <summary>INSTALL / REPAIR of MKWMP's files in the game (and the integration switched on).</summary>
    RepairFiles,

    /// <summary>The launcher's update card.</summary>
    InstallUpdate,

    /// <summary>Settings › Online: choose or change the server.</summary>
    ChooseServer,

    /// <summary>Settings › Online: the gamertag and its PIN (the welcome flow when it never ran).</summary>
    SignIn,

    /// <summary>Settings › Online: the server's privacy policy and the consent.</summary>
    ReviewPrivacy,

    /// <summary>Settings › Game.</summary>
    OpenGameSettings,
}

/// <param name="Label">The button's text, in the language in use.</param>
public sealed record CheckAction(CheckActionKind Kind, string Label);

/// <summary>One row of the play checklist.</summary>
/// <param name="Status">One line: what is there, or what is wrong and how it is fixed.</param>
/// <param name="Action">The button that fixes it (or shows it), if any.</param>
public sealed record CheckItem(CheckId Id, string Title, string Status, CheckState State, CheckAction? Action = null)
{
    /// <summary>0..1 while <see cref="CheckState.Working"/>, when known.</summary>
    public double? Progress { get; init; }

    /// <summary>A second button (the setup row's "Set up again" while the game is set up).</summary>
    public CheckAction? More { get; init; }

    /// <summary>PLAY waits for this row: it is needed, or work that PLAY must wait for is under way.</summary>
    public bool Blocks { get; init; }
}

/// <summary>
/// The play checklist (<see cref="ILauncher.Checklist"/>): every thing PLAY needs, its state and the button that fixes
/// it. It is made from the same views as <see cref="ILauncher.PlayProblem"/>, which is never null while a row blocks
/// (<see cref="Blocking"/>) and never set while none does: a problem no row is about gets a row of its own
/// (<see cref="CheckId.Other"/>).
/// </summary>
public sealed class PlayChecklist : IEquatable<PlayChecklist>
{
    public PlayChecklist(IReadOnlyList<CheckItem> items) => Items = items ?? throw new ArgumentNullException(nameof(items));

    public static PlayChecklist Empty { get; } = new([]);

    public IReadOnlyList<CheckItem> Items { get; }

    /// <summary>The rows that keep PLAY from starting, in order.</summary>
    public IEnumerable<CheckItem> Blocking => Items.Where(i => i.Blocks);

    /// <summary>The rows the player must fix (<see cref="CheckState.Needed"/>).</summary>
    public int NeededCount => Items.Count(i => i.State == CheckState.Needed);

    /// <summary>Nothing blocks PLAY.</summary>
    public bool Ready => !Items.Any(i => i.Blocks);

    public CheckItem? Find(CheckId id) => Items.FirstOrDefault(i => i.Id == id);

    public bool Equals(PlayChecklist? other) => other is not null && Items.SequenceEqual(other.Items);

    public override bool Equals(object? obj) => Equals(obj as PlayChecklist);

    public override int GetHashCode() => Items.Count;

    /// <summary>
    /// The checklist from the launcher's views. <see cref="ChecklistFacts.PlayProblem"/> is what blocks PLAY as the
    /// launcher found it; the launcher's own <see cref="ILauncher.PlayProblem"/> is that, else the first row that blocks.
    /// </summary>
    public static PlayChecklist Build(ChecklistFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var items = new List<CheckItem>
        {
            RuntimeRow(facts),
            CopyRow(facts),
            VersionRow(facts),
            SetupRow(facts),
            FilesRow(facts),
            UpdateRow(facts),
            OnlineRow(facts),
        };
        if (facts.PlayProblem is { } problem && !items.Any(i => i.Blocks))
        {
            items.Insert(0, new CheckItem(CheckId.Other, Strings.T("core.check.other"), problem, CheckState.Needed) { Blocks = true });
        }
        return new PlayChecklist(items);
    }

    private static CheckAction Act(CheckActionKind kind, string key, string? name = null) =>
        new(kind, name is null ? Strings.T(key) : Strings.F(key, ("name", name)));

    private static CheckItem Row(CheckId id, string title, string status, CheckState state, CheckAction? action = null) =>
        new(id, title, status, state, action) { Blocks = state == CheckState.Needed };

    /// <summary>The install's check refuses it for a reason a row above the setup is about (or the folder is gone).</summary>
    private static bool Refused(ChecklistFacts f) => f.Game.Check is { Status: TargetStatus.Missing or TargetStatus.Unsupported };

    // ---- 1. the runtime ------------------------------------------------------------------

    private static CheckItem RuntimeRow(ChecklistFacts f)
    {
        var name = f.TargetName;
        var title = Strings.F("core.check.runtime", ("name", name));
        var runtime = f.Runtime;
        var install = f.Game.Install;
        var pinned = runtime.PinnedLabel ?? runtime.PinnedVersion ?? "";
        if (runtime.Busy)
        {
            return new CheckItem(CheckId.Runtime, title, Strings.RuntimeBusy(name), CheckState.Working, Act(CheckActionKind.ShowProgress, "core.check.do.progress"))
            {
                Progress = runtime.Progress, Blocks = true,
            };
        }
        if (install is null)
        {
            if (runtime.State == RuntimeState.Unavailable)
            {
                return Row(CheckId.Runtime, title, Strings.SetGameFolder(f.FolderHint), CheckState.Needed, Act(CheckActionKind.ChooseGameFolder, "core.check.do.folder"));
            }
            return runtime.State == RuntimeState.Failed && runtime.Note is { } failed
                ? Row(CheckId.Runtime, title, failed, CheckState.Needed, Act(CheckActionKind.InstallRuntime, "core.check.do.retry"))
                : Row(CheckId.Runtime, title, Strings.F("core.check.runtime.missing", ("name", name), ("version", pinned)), CheckState.Needed,
                    Act(CheckActionKind.InstallRuntime, "core.check.do.install_runtime", name));
        }
        if (runtime.State == RuntimeState.UpdateAvailable)
        {
            // An earlier release the launcher updates in place (MKWMP's build, or an official one installed by hand), or the
            // build for another processor (no AVX2). Before the gate below: such an install is refused for its version,
            // and the way out is this update, never a second install beside it.
            var status = runtime.InstalledVersion == runtime.PinnedVersion
                ? Strings.F("core.check.runtime.cpu", ("name", name), ("version", pinned))
                : Strings.RuntimeUpdateNeeded(name, pinned);
            return Row(CheckId.Runtime, title, status, CheckState.Needed, Act(CheckActionKind.InstallRuntime, "core.check.do.update_runtime", name));
        }
        if (f.Game.Check is { Status: TargetStatus.Missing or TargetStatus.Unsupported } check && check.Gate != TargetGate.GameVersion)
        {
            // Not a build MKWMP runs on: the player's own is never replaced, MKWMP's goes beside it.
            return Row(CheckId.Runtime, title, check.Problem ?? Strings.SetGameFolder(f.FolderHint), CheckState.Needed, runtime.State == RuntimeState.Unavailable
                ? Act(CheckActionKind.ChooseGameFolder, "core.check.do.folder")
                : runtime.Foreign ? Act(CheckActionKind.InstallRuntime, "core.check.do.install_ours") : Act(CheckActionKind.InstallRuntime, "core.check.do.install_runtime", name));
        }
        if (runtime.State == RuntimeState.Failed && runtime.Note is { } note)
        {
            // An update of MKWMP's build that did not go in: the old one stays, and the game is not for it.
            var outdated = !runtime.Foreign && runtime.InstalledVersion is { } have && runtime.PinnedVersion is { } want && have != want;
            return Row(CheckId.Runtime, title, note, outdated ? CheckState.Needed : CheckState.Warning, Act(CheckActionKind.InstallRuntime, "core.check.do.retry"));
        }
        var version = runtime.InstalledVersion ?? install.Version ?? "";
        // A developer's test build accepted as the pinned one ([dev] recomp_exe_sha256): said, so it is never taken for the release.
        var key = runtime.DevSha256 is not null || install.DevSha256 is not null ? "core.check.runtime.dev" : runtime.Foreign ? "core.check.runtime.own" : "core.check.runtime.ok";
        return Row(CheckId.Runtime, title, Strings.F(key, ("name", name), ("version", version)), CheckState.Ok);
    }

    // ---- 2. the player's copy of the game ------------------------------------------------

    private static CheckItem CopyRow(ChecklistFacts f)
    {
        var title = Strings.T("core.check.copy");
        var setup = f.Setup;
        if (f.Game.Install is null)
        {
            return Row(CheckId.GameCopy, title, Strings.F("core.check.after_runtime", ("name", f.TargetName)), CheckState.NotApplicable);
        }
        if (!setup.Available)
        {
            return Row(CheckId.GameCopy, title, Strings.T("core.check.copy.outside"), CheckState.NotApplicable);
        }
        if (setup.NeedsDump)
        {
            var status = setup.Reason == GameSetupReason.DumpMissing && setup.Message is { } gone ? gone : Strings.T("core.check.copy.needed");
            return Row(CheckId.GameCopy, title, status, CheckState.Needed, Act(CheckActionKind.ChooseCopy, "core.check.do.choose_copy"));
        }
        var change = setup.Busy ? null : Act(CheckActionKind.ChangeCopy, "core.check.do.change_copy");
        return setup.DumpPath is { } path
            ? Row(CheckId.GameCopy, title, path, CheckState.Ok, change)
            : Row(CheckId.GameCopy, title, Strings.T("core.check.copy.unknown"), CheckState.Ok, change);
    }

    // ---- 3. the game's version -----------------------------------------------------------

    private static CheckItem VersionRow(ChecklistFacts f)
    {
        var title = Strings.T("core.check.version");
        var needed = f.GameVersions;
        var setup = f.Setup;
        var wanted = string.Join(Strings.T("core.recomp.or"), needed);
        var copy = setup.Available && !setup.Busy ? Act(CheckActionKind.ChangeCopy, "core.check.do.change_copy") : null;
        if (f.Game.Check is { Gate: TargetGate.GameVersion, Problem: { } wrong })
        {
            return Row(CheckId.GameVersion, title, wrong, CheckState.Needed, copy);
        }
        // What a setup is reading (or stopped for) is newer than what the last finished one built.
        var version = setup.Busy || setup.ErrorCode == "version" ? setup.GameVersion ?? f.Game.Install?.GameVersion : f.Game.Install?.GameVersion ?? setup.GameVersion;
        if (version is not null && needed.Count > 0 && !needed.Contains(version))
        {
            // read by a setup (running, or stopped by the launcher for it) before any game was built from that copy
            return Row(CheckId.GameVersion, title, Strings.RecompGameProblem(version, needed), CheckState.Needed, setup.NeedsDump ? Act(CheckActionKind.ChooseCopy, "core.check.do.choose_copy") : copy);
        }
        if (version is not null)
        {
            return Row(CheckId.GameVersion, title, Strings.F("core.check.version.ok", ("version", version)), CheckState.Ok);
        }
        if (setup.Busy)
        {
            return Row(CheckId.GameVersion, title, Strings.F("core.check.version.reading", ("needed", wanted)), CheckState.Working);
        }
        if (f.Game.Install is null || setup.State is GameSetupState.NeedsSetup or GameSetupState.Failed || needed.Count == 0)
        {
            return Row(CheckId.GameVersion, title, needed.Count == 0 ? Strings.T("core.check.version.any") : Strings.F("core.check.version.later", ("needed", wanted)), CheckState.NotApplicable);
        }
        return Row(CheckId.GameVersion, title, Strings.F("core.check.version.unknown", ("needed", wanted)), CheckState.Warning);
    }

    // ---- 4. the game, set up for this runtime --------------------------------------------

    private static CheckItem SetupRow(ChecklistFacts f)
    {
        var title = Strings.T("core.check.setup");
        var setup = f.Setup;
        if (f.Game.Install is not { } install)
        {
            return Row(CheckId.GameSetup, title, Strings.F("core.check.after_runtime", ("name", f.TargetName)), CheckState.NotApplicable);
        }
        if (setup.Busy)
        {
            var step = setup.StepLabel ?? Strings.SetupStarting;
            var status = setup.StepCount > 0
                ? Strings.F("core.check.setup.step", ("step", step), ("n", Strings.N(setup.StepIndex + 1)), ("of", Strings.N(setup.StepCount)))
                : step;
            return new CheckItem(CheckId.GameSetup, title, status, CheckState.Working, Act(CheckActionKind.ShowProgress, "core.check.do.progress"))
            {
                Progress = setup.Progress, Blocks = true,
            };
        }
        if (Refused(f) || f.Runtime.Busy || f.Runtime.State == RuntimeState.UpdateAvailable)
        {
            return Row(CheckId.GameSetup, title, Strings.T("core.check.after_above"), CheckState.NotApplicable);
        }
        if (setup.State == GameSetupState.Failed)
        {
            var again = setup.NeedsDump && setup.DumpPath is null || setup.ErrorCode == "version" ? Act(CheckActionKind.ChooseCopy, "core.check.do.choose_copy")
                : Act(CheckActionKind.RunSetup, "core.check.do.retry");
            return Row(CheckId.GameSetup, title, setup.Message ?? Strings.SetupNotSetUp, CheckState.Needed, again);
        }
        if (setup.State == GameSetupState.NeedsSetup)
        {
            var action = setup.NeedsDump ? Act(CheckActionKind.ChooseCopy, "core.check.do.choose_copy")
                : setup.Reason switch
                {
                    GameSetupReason.Unfinished => Act(CheckActionKind.RunSetup, "core.check.do.continue"),
                    GameSetupReason.CopyChanged => Act(CheckActionKind.RunSetup, "core.check.do.setup_again"),
                    GameSetupReason.RuntimeChanged or GameSetupReason.GameMismatch => Act(CheckActionKind.RunSetup, "core.check.do.setup_again"),
                    _ => Act(CheckActionKind.RunSetup, "core.check.do.setup"),
                };
            return Row(CheckId.GameSetup, title, setup.Message ?? Strings.SetupNotSetUp, CheckState.Needed, action);
        }
        if (f.Game.Check is { Status: TargetStatus.NeedsRebuild, Problem: { } rebuild })
        {
            return Row(CheckId.GameSetup, title, rebuild, CheckState.Needed, Act(CheckActionKind.OpenGameSettings, "core.check.do.settings"));
        }
        if (!setup.Available)
        {
            return Row(CheckId.GameSetup, title, Strings.F("core.check.setup.outside", ("name", f.TargetName)), CheckState.NotApplicable);
        }
        var built = f.Runtime.InstalledVersion ?? install.Version ?? "";
        return Row(CheckId.GameSetup, title, Strings.F("core.check.setup.ok", ("name", f.TargetName), ("version", built)), CheckState.Ok)
            with { More = Act(CheckActionKind.Rebuild, "core.check.do.rebuild") };
    }

    // ---- 5. MKWMP's files in the game ----------------------------------------------------

    private static CheckItem FilesRow(ChecklistFacts f)
    {
        var title = Strings.T("core.check.files");
        var game = f.Game;
        if (game.Install is null || Refused(f))
        {
            return Row(CheckId.MkwmpFiles, title, Strings.T("core.check.after_above"), CheckState.NotApplicable);
        }
        var mod = game.Integration is { AsMod: true };
        if (game.Working)
        {
            return new CheckItem(CheckId.MkwmpFiles, title, mod ? Strings.ModWorking : Strings.PatchWorking, CheckState.Working) { Blocks = true };
        }
        var problem = !f.Settings.Patch ? Strings.InstallPatchFirst : game.Integration?.Problem;
        if (problem is null)
        {
            return game.Integration is { } report
                ? Row(CheckId.MkwmpFiles, title, report.Summary, CheckState.Ok, Act(CheckActionKind.RepairFiles, "core.check.do.repair"))
                : Row(CheckId.MkwmpFiles, title, Strings.T("core.check.after_above"), CheckState.NotApplicable);
        }
        // Before the game is set up, MKWMP's files go in after it (the setup writes mkw.ini): said, not offered yet.
        var setupFirst = f.Setup.Busy || f.Setup.State is GameSetupState.NeedsSetup or GameSetupState.Failed || f.Runtime.Busy || f.Runtime.State == RuntimeState.UpdateAvailable;
        if (setupFirst)
        {
            return Row(CheckId.MkwmpFiles, title, Strings.T("core.check.files.after_setup"), CheckState.NotApplicable);
        }
        var install = !f.Settings.Patch || game.Integration is { State: IntegrationState.Missing };
        return Row(CheckId.MkwmpFiles, title, problem, CheckState.Needed, Act(CheckActionKind.RepairFiles, install ? "core.check.do.install" : "core.check.do.repair"));
    }

    // ---- 6. the launcher's own update ----------------------------------------------------

    private static CheckItem UpdateRow(ChecklistFacts f)
    {
        var title = Strings.T("core.check.update");
        var u = f.Update;
        var version = u.AvailableVersion ?? "";
        return u.State switch
        {
            UpdateState.Available => Row(CheckId.LauncherUpdate, title, Strings.F("core.check.update.available", ("version", version)), CheckState.Warning, Act(CheckActionKind.InstallUpdate, "core.check.do.update")),
            UpdateState.ReadyToInstall => Row(CheckId.LauncherUpdate, title, Strings.F("core.check.update.ready", ("version", version)), CheckState.Warning, Act(CheckActionKind.InstallUpdate, "core.check.do.update")),
            UpdateState.Downloading => new CheckItem(CheckId.LauncherUpdate, title, Strings.F("core.check.update.downloading", ("version", version)), CheckState.Working, Act(CheckActionKind.InstallUpdate, "core.check.do.progress")) { Progress = u.Progress },
            UpdateState.Restarting => new CheckItem(CheckId.LauncherUpdate, title, Strings.F("core.check.update.downloading", ("version", version)), CheckState.Working),
            UpdateState.Checking => new CheckItem(CheckId.LauncherUpdate, title, Strings.T("core.check.update.checking"), CheckState.Working),
            UpdateState.Failed => Row(CheckId.LauncherUpdate, title, u.Status.Length > 0 ? u.Status : Strings.UpdateFailed(""), CheckState.Warning, Act(CheckActionKind.InstallUpdate, "core.check.do.retry")),
            UpdateState.Off => Row(CheckId.LauncherUpdate, title, Strings.T("core.check.update.off"), CheckState.NotApplicable),
            _ => Row(CheckId.LauncherUpdate, title, Strings.F("core.check.update.ok", ("version", f.LauncherVersion)), CheckState.Ok),
        };
    }

    // ---- 7. online play (only with a server chosen) --------------------------------------

    private static CheckItem OnlineRow(ChecklistFacts f)
    {
        var title = Strings.T("core.check.online");
        var s = f.Settings;
        if (f.Nextendo is { Active: true } nextendo)
        {
            // PLAY goes to Nextendo Network: no MKWMP server, gamertag or PIN is needed. What Nextendo last said of the
            // account is shown; PLAY asks again and says why if it may not go online.
            var signedIn = Strings.F("core.check.online.nextendo", ("name", nextendo.Username));
            return nextendo.Tone is "warn" or "bad" && nextendo.Status.Length > 0
                ? new CheckItem(CheckId.Online, title, nextendo.Status, CheckState.Warning, Act(CheckActionKind.ChooseServer, "core.check.do.network"))
                : Row(CheckId.Online, title, signedIn, CheckState.Ok);
        }
        if (string.IsNullOrWhiteSpace(s.Server))
        {
            return Row(CheckId.Online, title, Strings.T("core.check.online.off"), CheckState.NotApplicable, Act(CheckActionKind.ChooseServer, "core.check.do.server"));
        }
        // The order of the flow (Launcher.ConfigProblem): a gamertag, its PIN, then the consent that server keeps.
        if (!ClientConfig.ValidName(s.Name))
        {
            return Row(CheckId.Online, title, Strings.PickGamertag, CheckState.Needed, Act(CheckActionKind.SignIn, "core.check.do.sign_in"));
        }
        if (!ClientConfig.ValidPin(s.Pin))
        {
            return Row(CheckId.Online, title, Strings.NeedPin, CheckState.Needed, Act(CheckActionKind.SignIn, "core.check.do.sign_in"));
        }
        if (!s.AdultConsent)
        {
            return Row(CheckId.Online, title, Strings.NoConsent, CheckState.Needed, Act(CheckActionKind.ReviewPrivacy, "core.check.do.privacy"));
        }
        var server = f.Server.Name is { Length: > 0 } named ? named : ClientConfig.ServerBase(s.Server);
        return f.Server.State switch
        {
            ServerState.Offline => Row(CheckId.Online, title, Strings.F("core.check.online.unreachable", ("server", server)), CheckState.Needed, Act(CheckActionKind.ChooseServer, "core.check.do.other_server")),
            ServerState.Checking or ServerState.NotAsked => new CheckItem(CheckId.Online, title, Strings.F("core.check.online.checking", ("server", server)), CheckState.Working),
            _ => Row(CheckId.Online, title, Strings.F("core.check.online.ok", ("server", server), ("name", s.Name)), CheckState.Ok),
        };
    }
}

/// <summary>What the play checklist is made from: the launcher's views, as they are now.</summary>
/// <param name="PlayProblem">What blocks PLAY as the launcher found it (its settings, the game's gates, the local API, the files).</param>
public sealed record ChecklistFacts(
    LauncherSettings Settings,
    ServerView Server,
    GameView Game,
    RuntimeView Runtime,
    GameSetupView Setup,
    UpdateView Update,
    string LauncherVersion,
    string? PlayProblem)
{
    /// <summary>The linked Nextendo account and whether PLAY goes there (null: as none).</summary>
    public NextendoView? Nextendo { get; init; }

    private TargetInfo? Target => Game.Targets.FirstOrDefault(t => string.Equals(t.Id, Game.Install?.TargetId ?? Settings.Target, StringComparison.OrdinalIgnoreCase))
        ?? (Game.Targets.Count > 0 ? Game.Targets[0] : null);

    /// <summary>"mkw-recomp".</summary>
    public string TargetName => Target?.DisplayName ?? Settings.Target;

    public string FolderHint => Target?.FolderHint ?? "";

    /// <summary>The versions of the game MKWMP plays on with this target; empty: any.</summary>
    public IReadOnlyList<string> GameVersions => Target?.GameVersions ?? [];
}
