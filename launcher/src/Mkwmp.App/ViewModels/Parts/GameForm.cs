// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Windows;
using System.Windows.Input;
using Mkwmp.Core;
using Mkwmp.Core.Targets;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>A line with a status light. <paramref name="Tone"/>: good / warn / bad / muted / busy (a spinner).</summary>
public sealed record StatusLine(string Tone, string Text, string Detail = "");

/// <summary>
/// The game target, its folder and MKWMP's files in it, as Settings › Game and the first-run setup
/// both show it. Infinity Launcher's picker logic: the launcher looks for the game by itself; the
/// player can choose the folder in the launcher's own picker (<c>Shell.PickFolderAsync</c>; kept only
/// when a target recognises it) or have the launcher search again; lines with lights say what was found.
/// </summary>
public sealed class GameForm : ObservableObject
{
    private readonly ShellViewModel _shell;
    private IReadOnlyList<StatusLine> _lines = [];
    private IReadOnlyList<string> _files = [];

    public GameForm(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        BrowseCommand = RelayCommand.Async(BrowseAsync, () => CanChange);
        SearchCommand = RelayCommand.Async(() => Launcher.FindGameAsync(), () => CanChange && !Game.Searching);
        TargetCommand = RelayCommand.Async(PickTargetAsync, () => CanChange);
        FixCommand = new RelayCommand(() => Links.Open(_shell, Game.Check?.FixUrl?.AbsoluteUri), () => HasFix);
        InstallCommand = RelayCommand.Async(InstallAsync, () => CanInstall);
        SetupCommand = new RelayCommand(() => _shell.ShowGameWork());
        UninstallCommand = RelayCommand.Async(UninstallAsync, () => CanUninstall);
    }

    public Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    private GameView Game => Launcher.Game;

    private TargetInfo? Target => Game.Targets.FirstOrDefault(t => string.Equals(t.Id, Launcher.Settings.Target, StringComparison.OrdinalIgnoreCase))
        ?? (Game.Targets.Count > 0 ? Game.Targets[0] : null);

    public string TargetName => Target?.DisplayName ?? Launcher.Settings.Target;

    private string FolderHint => Target?.FolderHint ?? "";

    /// <summary>The Rust window disabled the folder and the Steam box while playing.</summary>
    public bool CanChange => !Launcher.Session.Busy;

    public bool Found => Game.Install is not null;

    public bool Searching => Game.Searching;

    public bool Working => Game.Working;

    public string Folder => Launcher.Settings.Game;

    /// <summary>The end of the path (the folder's own name matters, not the drive).</summary>
    public string FolderShort
    {
        get
        {
            var folder = Launcher.Settings.Game.TrimEnd('\\', '/');
            if (folder.Length == 0)
            {
                return S["settings.game.folder_none"];
            }
            const int Max = 28;
            if (folder.Length <= Max)
            {
                return folder;
            }
            var tail = folder[^Max..];
            var cut = tail.IndexOfAny(['\\', '/']);
            return "…" + (cut >= 0 && cut < Max - 8 ? tail[cut..] : tail);
        }
    }

    public string FolderCaption => Folder.Length == 0
        ? CoreStrings.SetGameFolder(FolderHint)
        : S.Format("settings.game.folder_caption", ("path", Folder));

    /// <summary>Found / runtime / game version / what blocks play / MKWMP's files: one line each, with a light.</summary>
    public IReadOnlyList<StatusLine> Lines => _lines;

    /// <summary>The per-file lines of the integration report.</summary>
    public IReadOnlyList<string> Files => _files;

    public bool HasFiles => _files.Count > 0;

    public string? Problem => Game.Check is { Status: not (TargetStatus.Ready or TargetStatus.NoIntegration) } check ? check.Problem : null;

    public bool HasFix => Game.Check is { FixUrl: not null };

    public string FixLabel => Game.Check?.FixLabel ?? "";

    public string FixCaption => Game.Check?.FixUrl?.AbsoluteUri ?? "";

    /// <summary>The integration of this install is the MKWMP mod (an mkw-recomp that loads mods), not the game patch beside mkw.exe: the page says "MKWMP mod".</summary>
    public bool AsMod => Game.Integration is { AsMod: true };

    private string WorkingText => AsMod ? CoreStrings.ModWorking : CoreStrings.PatchWorking;

    public string FilesTitle => AsMod ? S["settings.game.mod"] : S["settings.game.files"];

    public string FilesCaption => AsMod ? S["settings.game.mod_caption"] : S["settings.game.files_caption"];

    public string InstallCaption => AsMod ? S["settings.game.mod_install_caption"] : S["settings.game.install_caption"];

    public string UninstallCaption => AsMod ? S["settings.game.mod_uninstall_caption"] : S["settings.game.uninstall_caption"];

    public string IntegrationSummary => Game.Working ? WorkingText : Game.Integration?.Summary ?? "";

    // ── the game's setup (and its runtime's download): a row that leads to its card ──

    /// <summary>"Running · 42%", "Failed", "Needed", "Update needed", "Done"; no row when the launcher sets nothing up here.</summary>
    public string SetupState => _shell.GameWorkState;

    public bool HasSetupRow => SetupState.Length > 0;

    public string SetupCaption => _shell.GameWorkText ?? (_shell.HasGameWork ? Launcher.PlayProblem ?? "" : S["settings.game.setup_caption"]);

    public ICommand SetupCommand { get; }

    /// <summary>INSTALL when the files are missing or wrong, REPAIR when they are fine.</summary>
    public string InstallLabel => Game.Integration is { Ok: true } ? S["settings.game.repair"] : S["settings.game.install"];

    public bool CanInstall => Found && !Game.Working;

    public bool CanUninstall => Found && !Game.Working && Game.Integration is { State: not IntegrationState.Missing };

    public bool NeedsInstall => Found && Game.Integration is { Ok: false };

    public string Note => Game.Working ? S["settings.game.working"] : Game.Note ?? "";

    public bool NoteIsError => !Game.Working && Game.NoteIsError;

    /// <summary>"I start the game from Steam (Steam Deck)": the launcher then only holds the seat.</summary>
    public bool FromSteam
    {
        get => !Launcher.Settings.Launch;
        set => RelayCommand.Async(() => _shell.UpdateSettingsAsync(s => s with { Launch = !value })).Execute(null);
    }

    public ICommand BrowseCommand { get; }

    public ICommand SearchCommand { get; }

    public ICommand TargetCommand { get; }

    public ICommand FixCommand { get; }

    public ICommand InstallCommand { get; }

    public ICommand UninstallCommand { get; }

    public void Refresh()
    {
        var lines = BuildLines();
        if (!lines.SequenceEqual(_lines))
        {
            _lines = lines;
        }
        var files = Game.Integration?.Lines ?? [];
        if (!files.SequenceEqual(_files))
        {
            _files = [.. files];
        }
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
    }

    private List<StatusLine> BuildLines()
    {
        var lines = new List<StatusLine>();
        if (Game.Searching)
        {
            lines.Add(new StatusLine("busy", S["settings.game.searching"]));
            return lines;
        }
        if (Game.Install is not { } install)
        {
            lines.Add(new StatusLine("bad", S["settings.game.not_found"], CoreStrings.SetGameFolder(FolderHint)));
            return lines;
        }
        var check = Game.Check;
        var blocked = check is { Status: TargetStatus.Missing or TargetStatus.Unsupported or TargetStatus.NeedsRebuild };
        lines.Add(new StatusLine("good", S.Format("settings.game.found", ("path", install.Root))));
        lines.Add(new StatusLine(
            install.Version is null ? "warn" : blocked ? "warn" : "good",
            S.Format("settings.game.runtime", ("name", TargetName), ("version", install.Version ?? S["settings.game.unknown"]))));
        lines.Add(install.GameVersion is { } version
            ? new StatusLine(blocked ? "warn" : "good", S.Format("settings.game.version", ("version", version)))
            : new StatusLine("muted", S["settings.game.version_unknown"]));
        if (blocked && check?.Problem is { } problem)
        {
            lines.Add(new StatusLine("bad", problem, check.Explain ?? ""));
        }
        else if (check?.Explain is { Length: > 0 } explain && !blocked)
        {
            lines[1] = lines[1] with { Detail = explain };
        }
        if (Game.Working)
        {
            lines.Add(new StatusLine("busy", WorkingText));
        }
        else if (Game.Integration is { } report)
        {
            lines.Add(new StatusLine(report.Ok ? "good" : "bad", report.Summary));
        }
        return lines;
    }

    /// <summary>
    /// The launcher's own folder picker (never the system's dialog), titled with what the folder must hold, starting at
    /// the current one (else where the last one was picked). It only gives back a folder a target recognises: a wrong
    /// one is refused inside it, with the reason.
    /// </summary>
    private async Task BrowseAsync()
    {
        var current = Launcher.Settings.Game;
        var folder = await _shell.PickFolderAsync(PickerRequests.GameFolder(_shell, TargetName, FolderHint, current.Length > 0 ? current : null)).ConfigureAwait(true);
        if (folder is not null)
        {
            await ChooseAsync(folder).ConfigureAwait(true);
        }
    }

    private async Task ChooseAsync(string folder)
    {
        if (!await Launcher.ChooseGameFolderAsync(folder).ConfigureAwait(true) && Launcher.Game.Note is { } why)
        {
            _shell.ShowToast(S["settings.game.not_a_game"], why);
        }
        Refresh();
    }

    /// <summary>The folder typed by hand (Settings › Game › Advanced). Kept only when it is a game.</summary>
    public string TypedFolder
    {
        get => Launcher.Settings.Game;
        set
        {
            var folder = (value ?? "").Trim().Trim('"');
            if (folder.Length == 0 || !CanChange || string.Equals(folder, Launcher.Settings.Game, StringComparison.OrdinalIgnoreCase))
            {
                Raise();
                return;
            }
            RelayCommand.Async(() => ChooseAsync(folder)).Execute(null);
        }
    }

    public string TypedFolderCaption => Links.IsWine ? S["settings.game.typed_caption_wine"] : S["settings.game.typed_caption"];

    private async Task PickTargetAsync()
    {
        var options = Game.Targets.Select(t => new ChoiceOption(t.Id, t.DisplayName, t.FolderHint)).ToList();
        var picked = await Choice.AskAsync(_shell, S["settings.game.target"], S["settings.game.target_body"], options, Target?.Id).ConfigureAwait(true);
        if (picked is not null && picked != Launcher.Settings.Target)
        {
            await _shell.UpdateSettingsAsync(s => s with { Target = picked }).ConfigureAwait(true);
            await Launcher.FindGameAsync().ConfigureAwait(true);
        }
    }

    /// <summary>INSTALL / REPAIR. Never silent: a failure is said in a card, with the reason; a success in a toast (the row's note says both too).</summary>
    private async Task InstallAsync()
    {
        await Launcher.InstallIntegrationAsync().ConfigureAwait(true);
        Refresh();
        if (Game.NoteIsError)
        {
            await _shell.MessageAsync(S["settings.game.install_failed"], Game.Note ?? Game.Integration?.Summary ?? "").ConfigureAwait(true);
        }
        else
        {
            _shell.ShowToast(Game.Note ?? CoreStrings.PatchInstalled, Game.Integration?.Summary ?? "");
        }
    }

    private async Task UninstallAsync()
    {
        var mod = AsMod;
        if (await _shell.ConfirmAsync(S["settings.game.uninstall_title"], mod ? S["settings.game.mod_uninstall_body"] : S["settings.game.uninstall_body"], S["settings.game.uninstall"]).ConfigureAwait(true))
        {
            await Launcher.UninstallIntegrationAsync().ConfigureAwait(true);
            Refresh();
            if (Game.NoteIsError)
            {
                await _shell.MessageAsync(S["settings.game.uninstall_failed"], Game.Note ?? "").ConfigureAwait(true);
            }
            else
            {
                _shell.ShowToast(Game.Note ?? CoreStrings.PatchRemoved);
            }
        }
    }
}
