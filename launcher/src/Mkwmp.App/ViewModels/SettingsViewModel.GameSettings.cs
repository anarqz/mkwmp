// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Mkwmp.App.Controls;
using Mkwmp.App.Input;
using Mkwmp.Core;
using Mkwmp.Core.Targets;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// One setting of the game, as its row shows it. Everything comes from the target's schema
/// (<see cref="GameSetting"/>): the kind picks the control, the options fill the selection list,
/// the range gives the slider its ends and step. A change goes to <c>SetGameSettingAsync</c>; a
/// refusal is the view's note, and the row shows the game's value again on the next change.
/// </summary>
public sealed class GameSettingRow : ObservableObject
{
    private readonly ShellViewModel _shell;
    private DispatcherTimer? _later;
    private double? _pending;
    private GameSettingsView _view;

    public GameSettingRow(ShellViewModel shell, GameSetting setting, GameSettingsView view, bool startsGroup)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Setting = setting ?? throw new ArgumentNullException(nameof(setting));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        Heading = startsGroup ? setting.Group ?? "" : "";
        PickCommand = RelayCommand.Async(PickAsync, () => Enabled);
        ResetCommand = RelayCommand.Async(() => _shell.Launcher.ResetGameSettingAsync(Setting.Id), () => CanReset);
    }

    public GameSetting Setting { get; }

    /// <summary>The group's heading, on the first row of a group; "" on the others.</summary>
    public string Heading { get; }

    public string Label => Setting.Label;

    public bool IsToggle => Setting.Kind == GameSettingKind.Toggle;

    public bool IsChoice => Setting.Kind == GameSettingKind.Choice;

    public bool IsRange => Setting.Kind == GameSettingKind.Range;

    public bool IsText => Setting.Kind == GameSettingKind.Text;

    private string Current => _view.Value(Setting.Id);

    /// <summary>The setting's condition holds (<see cref="GameSetting.VisibleWhen"/>).</summary>
    public bool Visible => _view.IsVisible(Setting);

    /// <summary>The game is closed and nothing locks the setting.</summary>
    public bool Enabled => _view.Editable && Setting.Locked is null;

    /// <summary>The game has its own value for it (not the default).</summary>
    public bool IsChanged => _view.Changed.Contains(Setting.Id);

    public bool CanReset => Enabled && IsChanged;

    /// <summary>What the setting does; why it is locked; that the game reads it only when it starts.</summary>
    public string Caption
    {
        get
        {
            var parts = new List<string>();
            if (Setting.Locked is { Length: > 0 } locked)
            {
                parts.Add(locked);
            }
            if (Setting.Description.Length > 0)
            {
                parts.Add(Setting.Description);
            }
            if (Setting.RequiresRestart)
            {
                parts.Add(_shell.S["settings.gs.restart"]);
            }
            return string.Join("\n", parts);
        }
    }

    public bool IsOn
    {
        get => Current != "0";
        set => Send(value ? "1" : "0");
    }

    /// <summary>The chosen option's label; a value that matches no option shows as it is.</summary>
    public string ChoiceText => Setting.Options.FirstOrDefault(o => o.Value == Current)?.Label ?? Current;

    public double Minimum => Setting.Min;

    public double Maximum => Setting.Max;

    public double Step => Setting.Step;

    public double Number
    {
        get => _pending ?? (double.TryParse(Current, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? Math.Clamp(number, Setting.Min, Setting.Max) : Setting.Min);
        set
        {
            var rounded = Math.Clamp(Math.Round(value / Setting.Step) * Setting.Step, Setting.Min, Setting.Max);
            if (Math.Abs(rounded - Number) < Setting.Step / 1000)
            {
                return;
            }
            _pending = rounded;
            Raise(nameof(NumberText));
            if (_later is null)
            {
                _later = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                _later.Tick += (_, _) => Flush();
            }
            _later.Stop();
            _later.Start(); // saved shortly after the last step, not on every one
        }
    }

    /// <summary>The number as the game writes it, with its unit.</summary>
    public string NumberText => Setting.Format(Number) + Setting.Unit;

    public string Text
    {
        get => Current;
        set
        {
            var text = (value ?? "").Trim();
            if (text == Current)
            {
                Raise();
                return;
            }
            Send(text);
        }
    }

    public int MaxLength => Setting.MaxLength;

    public ICommand PickCommand { get; }

    public ICommand ResetCommand { get; }

    /// <summary>The view changed: show the game's values (a refused change snaps back here).</summary>
    public void Refresh(GameSettingsView view)
    {
        _view = view;
        RaiseAll();
    }

    /// <summary>Writes a slider value that is still waiting (the page is being left).</summary>
    public void Flush()
    {
        _later?.Stop();
        if (_pending is not { } value)
        {
            return;
        }
        RelayCommand.Async(async () =>
        {
            await _shell.Launcher.SetGameSettingAsync(Setting.Id, Setting.Format(value)).ConfigureAwait(true);
            if (_pending is { } now && Math.Abs(now - value) < Setting.Step / 1000)
            {
                _pending = null;
                RaiseAll();
            }
        }).Execute(null);
    }

    private void Send(string value)
    {
        if (!Enabled || value == Current)
        {
            return;
        }
        RelayCommand.Async(async () =>
        {
            await _shell.Launcher.SetGameSettingAsync(Setting.Id, value).ConfigureAwait(true);
            RaiseAll(); // a refusal changes nothing in the view: the row shows the game's value again
        }).Execute(null);
    }

    private async Task PickAsync()
    {
        var current = Current;
        var options = Setting.Options.Select(o => new ChoiceOption(o.Value, o.Label)).ToList();
        if (options.All(o => o.Id != current))
        {
            options.Add(new ChoiceOption(current, current, _shell.S["settings.gs.raw"]));
        }
        var picked = await Choice.AskAsync(_shell, Setting.Label, Setting.Description, options, current).ConfigureAwait(true);
        if (picked is not null && picked != current)
        {
            Send(picked);
        }
    }
}

/// <summary>
/// Settings › the game's own settings: one category entry per page of the schema the target in use
/// declares (<c>ILauncher.GameSettings</c>), and rows built from its settings: nothing here knows
/// a game. Only the chosen page's rows exist.
/// </summary>
public sealed partial class SettingsViewModel
{
    private const string GamePrefix = "gs:";

    private GameSettingsSchema? _rowsSchema;
    private string _rowsCategory = "";
    private GameSettingRow? _focusedRow;

    /// <summary>The rows of the chosen page.</summary>
    public ObservableCollection<GameSettingRow> GameRows { get; } = [];

    public ICommand ResetGameSettingsCommand { get; private set; } = null!;

    private GameSettingsView GameView => Launcher.GameSettings;

    public bool IsGameSettings => _category.Key.StartsWith(GamePrefix, StringComparison.Ordinal);

    private GameSettingsCategory? GameCategory => IsGameSettings
        ? GameView.Schema?.Categories.FirstOrDefault(c => GamePrefix + c.Id == _category.Key)
        : null;

    public string GameCategoryTitle => GameCategory?.Title ?? _category.Title;

    /// <summary>Why nothing can be changed (the game runs), or what the last change was refused for.</summary>
    public string GameSettingsNote => GameView.Note ?? "";

    public bool GameSettingsNoteIsError => GameView.NoteIsError;

    /// <summary>The schema's own line ("Changes apply the next time the game starts").</summary>
    public string GameSchemaNote => GameView.Schema?.Note ?? "";

    public string GameCategoryNote => GameCategory?.Note ?? "";

    public bool GameCategoryAdvanced => GameCategory?.Advanced == true;

    public string GameAdvancedNote => GameCategoryAdvanced
        ? string.Join("\n", new[] { S["settings.gs.advanced"], GameCategory?.Note }.Where(t => !string.IsNullOrEmpty(t)))
        : "";

    public string GamePlainNote => GameCategoryAdvanced ? "" : GameCategoryNote;

    public bool HasGameRows => GameRows.Count > 0;

    /// <summary>No schema (no game folder, or this game / version has no settings the launcher can change): the core's note says which.</summary>
    public string NoGameSettingsNote => GameView.Schema is null ? GameView.Note ?? "" : "";

    /// <summary>The row under the focus (the view tells): Y resets it when it is changed.</summary>
    public GameSettingRow? FocusedGameRow
    {
        get => _focusedRow;
        set
        {
            if (Set(ref _focusedRow, value))
            {
                RefreshHints();
            }
        }
    }

    public override IReadOnlyList<Hint> Hints => IsGameSettings && _focusedRow is { CanReset: true }
        ? [new Hint(PadButton.Y, S["settings.gs.reset"]), new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["hint.ok"])]
        : Shell.DefaultHints();

    public override bool OnButton(PadButton button)
    {
        if (button == PadButton.Y && IsGameSettings && _focusedRow is { CanReset: true } row)
        {
            row.ResetCommand.Execute(null);
            return true;
        }
        return false;
    }

    private void InitGameSettings()
    {
        ResetGameSettingsCommand = RelayCommand.Async(ResetAllAsync, () => GameView.Editable && GameView.Changed.Count > 0);
        SyncGameCategories();
    }

    private async Task ResetAllAsync()
    {
        if (await Shell.ConfirmAsync(S["settings.gs.reset_all"], S["settings.gs.reset_all_body"], S["settings.gs.reset_all_yes"]).ConfigureAwait(true))
        {
            await Launcher.ResetGameSettingsAsync().ConfigureAwait(true);
        }
    }

    /// <summary>An icon the launcher has for the schema's hint, else the gear.</summary>
    private static string IconFor(string hint) =>
        hint.Length > 0 && Application.Current?.TryFindResource("Icon." + hint) is Geometry ? hint : "Settings";

    /// <summary>
    /// The category list follows the schema: one entry per page, after "Game", under a heading.
    /// In place, so the list (and the focus on it) stays while the pages are the same.
    /// </summary>
    private void SyncGameCategories()
    {
        var pages = GameView.Schema?.Categories ?? [];
        var wanted = pages.Select((c, i) => new CategoryEntry(GamePrefix + c.Id, c.Title, false, i == 0 ? S["settings.gs.heading"] : "", IconFor(c.Icon))).ToList();
        var have = Categories.Where(c => c.Key.StartsWith(GamePrefix, StringComparison.Ordinal)).ToList();
        if (!have.SequenceEqual(wanted))
        {
            var selected = _category.Key;
            foreach (var old in have)
            {
                Categories.Remove(old);
            }
            var at = Categories.IndexOf(Categories.First(c => c.Key == "game")) + 1;
            foreach (var entry in wanted)
            {
                Categories.Insert(at++, entry);
            }
            // the entry after the group starts a new one
            var next = Categories[at];
            if (next.SeparatorBefore != (wanted.Count > 0))
            {
                Categories[at] = next with { SeparatorBefore = wanted.Count > 0 };
            }
            if (Categories.FirstOrDefault(c => c.Key == selected) is { } still)
            {
                if (!ReferenceEquals(still, _category))
                {
                    _category = still;
                    Raise(nameof(Category));
                }
            }
            else
            {
                Category = Categories.First(c => c.Key == "game"); // its page is gone (another target, no game)
            }
        }
        SyncGameRows();
    }

    /// <summary>Builds the chosen page's rows (only that page's), or refreshes them in place.</summary>
    private void SyncGameRows()
    {
        var view = GameView;
        var category = GameCategory;
        if (category is null)
        {
            if (GameRows.Count > 0)
            {
                FlushGameRows();
                GameRows.Clear();
                FocusedGameRow = null;
            }
            (_rowsSchema, _rowsCategory) = (null, "");
            return;
        }
        if (!ReferenceEquals(_rowsSchema, view.Schema) || _rowsCategory != category.Id)
        {
            FlushGameRows();
            GameRows.Clear();
            FocusedGameRow = null;
            string? group = null;
            foreach (var setting in category.Settings)
            {
                GameRows.Add(new GameSettingRow(Shell, setting, view, setting.Group is not null && setting.Group != group));
                group = setting.Group;
            }
            (_rowsSchema, _rowsCategory) = (view.Schema, category.Id);
            return;
        }
        foreach (var row in GameRows)
        {
            row.Refresh(view);
        }
    }

    private void FlushGameRows()
    {
        foreach (var row in GameRows)
        {
            row.Flush();
        }
    }
}
