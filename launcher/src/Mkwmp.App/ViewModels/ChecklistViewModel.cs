// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>A row of the checklist page: the core's row, with its icon and the window's commands. Kept while its row stays, so the focus stays.</summary>
public sealed class ChecklistRow : ObservableObject
{
    private string _title = "";
    private string _status = "";
    private string _state = "na";
    private double _progress = double.NaN;
    private CheckAction? _action;
    private CheckAction? _more;

    public ChecklistRow(string key, string iconKey, Func<CheckAction, Task> run)
    {
        Key = key;
        IconKey = iconKey;
        ActionCommand = RelayCommand.Async(() => _action is { } a ? run(a) : Task.CompletedTask);
        MoreCommand = RelayCommand.Async(() => _more is { } a ? run(a) : Task.CompletedTask);
    }

    public string Key { get; }

    public string IconKey { get; }

    public string Title
    {
        get => _title;
        private set => Set(ref _title, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    /// <summary>ok / needed / warning / working / na: the badge (✓, !, a spinner) and the tone.</summary>
    public string State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    /// <summary>0..1 of the work under way; NaN when there is none or it is not known.</summary>
    public double Progress
    {
        get => _progress;
        private set
        {
            if (Set(ref _progress, value))
            {
                Raise(nameof(HasProgress));
            }
        }
    }

    public bool HasProgress => !double.IsNaN(_progress);

    public string ActionLabel => _action?.Label ?? "";

    public bool HasAction => _action is not null;

    public string MoreLabel => _more?.Label ?? "";

    public bool HasMore => _more is not null;

    /// <summary>The second action shares the button's place: shown when there is no fix to offer.</summary>
    public bool ShowMore => _more is not null && _action is null;

    /// <summary>A on the row, or its button (Right, then A): the row's action.</summary>
    public ICommand ActionCommand { get; }

    public ICommand MoreCommand { get; }

    public void Update(string title, string status, string state, double progress, CheckAction? action, CheckAction? more)
    {
        Title = title;
        Status = status;
        State = state;
        Progress = progress;
        if (_action != action)
        {
            _action = action;
            Raise(nameof(ActionLabel));
            Raise(nameof(HasAction));
            Raise(nameof(ShowMore));
        }
        if (_more != more)
        {
            _more = more;
            Raise(nameof(MoreLabel));
            Raise(nameof(HasMore));
            Raise(nameof(ShowMore));
        }
    }
}

/// <summary>
/// "Ready to Play": the play checklist (<see cref="ILauncher.Checklist"/>), a row per thing PLAY needs with its state and
/// the button that fixes it, live while downloads and the setup run; the controller as a last row (a warning, never a
/// block). Opened from Home's dock, from PLAY when something blocks it, and from Settings › Game.
/// </summary>
public sealed class ChecklistViewModel : PageViewModel
{
    private string _summary = "";
    private string _caption = "";
    private string _summaryState = "ok";

    public ChecklistViewModel(ShellViewModel shell)
        : base(shell)
    {
        Shell.PropertyChanged += OnShellChanged;
        Refresh();
    }

    public override PageViewModel? Recreate() => new ChecklistViewModel(Shell);

    public ObservableCollection<ChecklistRow> Rows { get; } = [];

    /// <summary>"Ready to play", or "2 things to fix before you can play".</summary>
    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string SummaryCaption
    {
        get => _caption;
        private set => Set(ref _caption, value);
    }

    /// <summary>ok / needed / working.</summary>
    public string SummaryState
    {
        get => _summaryState;
        private set => Set(ref _summaryState, value);
    }

    public override void OnStateChanged() => Refresh();

    public override void OnLeave() => Shell.PropertyChanged -= OnShellChanged;

    public override void OnEnter()
    {
        Shell.PropertyChanged -= OnShellChanged;
        Shell.PropertyChanged += OnShellChanged;
        Refresh();
    }

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.GamepadConnected) or nameof(ShellViewModel.GamepadName))
        {
            Refresh();
        }
    }

    private static string IconOf(CheckId id) => id switch
    {
        CheckId.Runtime => "Icon.Program",
        CheckId.GameCopy => "Icon.File",
        CheckId.GameVersion => "Icon.Kart",
        CheckId.GameSetup => "Icon.Refresh",
        CheckId.MkwmpFiles => "Icon.Package",
        CheckId.LauncherUpdate => "Icon.Download",
        CheckId.Online => "Icon.Globe",
        _ => "Icon.Warning",
    };

    private static string StateOf(CheckState state) => state switch
    {
        CheckState.Ok => "ok",
        CheckState.Needed => "needed",
        CheckState.Warning => "warning",
        CheckState.Working => "working",
        _ => "na",
    };

    private void Refresh()
    {
        var list = Launcher.Checklist;
        var wanted = list.Items.Select(i => i.Id.ToString()).Append("controller").ToList();
        if (!Rows.Select(r => r.Key).SequenceEqual(wanted))
        {
            // rows come and go rarely (the "something else" row): the ones that stay keep their object, and the focus
            var kept = Rows.ToDictionary(r => r.Key);
            Rows.Clear();
            foreach (var key in wanted)
            {
                var icon = key == "controller" ? "Icon.Gamepad" : IconOf(Enum.Parse<CheckId>(key));
                Rows.Add(kept.TryGetValue(key, out var row) ? row : new ChecklistRow(key, icon, RunAsync));
            }
        }
        for (var i = 0; i < list.Items.Count; i++)
        {
            var item = list.Items[i];
            Rows[i].Update(item.Title, item.Status, StateOf(item.State), item.State == CheckState.Working ? item.Progress ?? double.NaN : double.NaN, item.Action, item.More);
        }
        var pad = Shell.GamepadConnected;
        // the controller's own name when SDL gives one (a product name: no translation)
        var status = !pad ? S["checklist.controller_none"] : Shell.GamepadName is { Length: > 0 } name ? name : S["checklist.controller_ok"];
        Rows[^1].Update(S["checklist.controller"], status, pad ? "ok" : "warning", double.NaN, null, null);

        var needed = list.NeededCount;
        (Summary, SummaryCaption, SummaryState) = list.Ready ? (S["checklist.ready"], S["checklist.ready_caption"], "ok")
            : needed == 0 ? (S["checklist.wait"], S["checklist.fix_caption"], "working")
            : (needed == 1 ? S["checklist.fix_one"] : S.Format("checklist.fix_many", ("n", needed.ToString(S.Culture))), S["checklist.fix_caption"], "needed");
    }

    /// <summary>A row's button: the command the launcher already has for it.</summary>
    private async Task RunAsync(CheckAction action)
    {
        switch (action.Kind)
        {
            case CheckActionKind.InstallRuntime:
                RelayCommand.Async(() => Launcher.InstallRuntimeAsync(null, CancellationToken.None)).Execute(null);
                Shell.ShowGameWork(); // the download, live; it closes into what is next
                break;
            case CheckActionKind.ShowProgress:
                Shell.ShowGameWork();
                break;
            case CheckActionKind.ChooseCopy:
                if (await Shell.PickAsync(PickerRequests.GameCopy(Shell, folderOnly: false, Launcher.GameSetup.DumpPath)).ConfigureAwait(true) is { } path)
                {
                    RelayCommand.Async(() => Launcher.RunGameSetupAsync(path, CancellationToken.None)).Execute(null);
                    Shell.ShowGameWork();
                }
                break;
            case CheckActionKind.ChangeCopy:
                await GameCopy.ChangeAsync(Shell).ConfigureAwait(true);
                break;
            case CheckActionKind.RunSetup:
                RelayCommand.Async(() => Launcher.RunGameSetupAsync(null, CancellationToken.None)).Execute(null);
                Shell.ShowGameWork();
                break;
            case CheckActionKind.Rebuild:
                await GameRebuild.AskAsync(Shell).ConfigureAwait(true);
                break;
            case CheckActionKind.RepairFiles:
                await Launcher.InstallIntegrationAsync().ConfigureAwait(true);
                if (Launcher.Game.NoteIsError)
                {
                    await Shell.MessageAsync(S["settings.game.install_failed"], Launcher.Game.Note ?? Launcher.Game.Integration?.Summary ?? "").ConfigureAwait(true);
                }
                else
                {
                    Shell.ShowToast(Launcher.Game.Note ?? Core.Strings.PatchInstalled, Launcher.Game.Integration?.Summary ?? "");
                }
                break;
            case CheckActionKind.InstallUpdate:
                Shell.ShowUpdateModal();
                break;
            // an MKWMP server's rows (only while MKWMP servers are chosen): Settings › Online has the server, the profile and its privacy
            case CheckActionKind.ChooseServer or CheckActionKind.SignIn or CheckActionKind.ReviewPrivacy:
                Shell.OpenOnlineSettings();
                break;
            case CheckActionKind.SignInNextendo:
                Shell.SignInNextendo();
                break;
            case CheckActionKind.OpenNextendo:
                Shell.OpenNextendo();
                break;
            default:
                Shell.OpenSettings("game");
                break;
        }
    }
}
