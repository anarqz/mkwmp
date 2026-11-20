// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text.RegularExpressions;

namespace Mkwmp.Core.Targets;

/// <summary>How a game setting is edited. Every value travels as a string, in the game's own spelling.</summary>
public enum GameSettingKind
{
    /// <summary>"0" is off, anything else is on; written as "0" or "1".</summary>
    Toggle,

    /// <summary>One of <see cref="GameSetting.Options"/>.</summary>
    Choice,

    /// <summary>A number from <see cref="GameSetting.Min"/> to <see cref="GameSetting.Max"/> in steps of <see cref="GameSetting.Step"/>.</summary>
    Range,

    /// <summary>Free text of at most <see cref="GameSetting.MaxLength"/> characters matching <see cref="GameSetting.Pattern"/>.</summary>
    Text,
}

/// <summary>
/// The settings of the game itself (video, sound, controls...) that a target lets the launcher
/// edit: data only, so the window draws "Game settings" for any target without knowing it. A
/// target builds one per install (<see cref="IGameTarget.GameSettings"/>): defaults and locks may
/// depend on the machine.
/// </summary>
/// <param name="Categories">The pages, in the order shown.</param>
/// <param name="Note">A line over all of them ("Changes apply the next time the game starts"), or null.</param>
public sealed record GameSettingsSchema(IReadOnlyList<GameSettingsCategory> Categories, string? Note = null)
{
    public IEnumerable<GameSetting> Settings => Categories.SelectMany(c => c.Settings);

    /// <summary>The setting with that id (ids compare without case), or null.</summary>
    public GameSetting? Find(string id) => Settings.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Every value in effect, by setting id: the value the game has (<paramref name="set"/>, from
    /// <see cref="IGameTarget.ReadGameSettings"/>), else the setting's default.
    /// </summary>
    public IReadOnlyDictionary<string, string> Effective(IReadOnlyDictionary<string, string> set)
    {
        ArgumentNullException.ThrowIfNull(set);
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Settings)
        {
            all[s.Id] = set.TryGetValue(s.Id, out var v) ? v : s.Default;
        }
        return all;
    }

    /// <summary>Whether a setting's <see cref="GameSetting.VisibleWhen"/> holds for these values in effect (see <see cref="Effective"/>).</summary>
    public static bool IsVisible(GameSetting setting, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(values);
        return setting.VisibleWhen is not { } when
            || (values.TryGetValue(when.Setting, out var v) && when.Values.Contains(v, StringComparer.Ordinal));
    }

    /// <summary>
    /// What changing one setting writes: the setting itself (checked and in the game's spelling,
    /// see <see cref="GameSetting.Normalize"/>) and, for a choice that is a preset
    /// (<see cref="GameSettingOption.Sets"/>), the settings it sets. A null
    /// <paramref name="value"/> resets: the setting, and what its presets set, go back to their
    /// defaults (null in the result). Throws <see cref="LauncherException"/> for an unknown id or
    /// a value the setting does not take.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Changes(string id, string? value)
    {
        ArgumentNullException.ThrowIfNull(id);
        var setting = Find(id) ?? throw new LauncherException(Strings.GameSettingsNotASetting(id));
        var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (value is null)
        {
            changes[setting.Id] = null;
            foreach (var other in setting.Options.SelectMany(o => o.Sets ?? EmptySets).Select(p => p.Key))
            {
                changes[other] = null;
            }
            return changes;
        }
        var normal = setting.Normalize(value);
        changes[setting.Id] = normal;
        if (setting.Options.FirstOrDefault(o => o.Value == normal)?.Sets is { } sets)
        {
            foreach (var (other, v) in sets)
            {
                changes[other] = (Find(other) ?? throw new LauncherException(Strings.GameSettingsNotASetting(other))).Normalize(v);
            }
        }
        return changes;
    }

    private static readonly Dictionary<string, string> EmptySets = [];
}

/// <param name="Id">Stable id ("screen").</param>
/// <param name="Title">What the player reads ("Screen").</param>
/// <param name="Icon">A hint for the window's icon, by name ("Fullscreen", "Volume", "Gamepad", "Globe", "Settings"); unknown names get a default.</param>
/// <param name="Note">A line under the title (a warning on a page of testing options), or null.</param>
/// <param name="Advanced">A page most players never need: shown last and quieter, or behind "Advanced".</param>
public sealed record GameSettingsCategory(string Id, string Title, string Icon, IReadOnlyList<GameSetting> Settings, string? Note = null, bool Advanced = false);

/// <param name="Value">What is written.</param>
/// <param name="Label">What the player reads.</param>
/// <param name="Sets">A preset: choosing this option also sets these settings (id to value).</param>
public sealed record GameSettingOption(string Value, string Label, IReadOnlyDictionary<string, string>? Sets = null);

/// <summary>Shown (or enabled) only while <paramref name="Setting"/>'s value in effect is one of <paramref name="Values"/>.</summary>
public sealed record GameSettingCondition(string Setting, IReadOnlyList<string> Values);

/// <summary>One setting. Make them with <see cref="Toggle"/>, <see cref="Choice"/>, <see cref="Range"/> and <see cref="Text"/>.</summary>
public sealed record GameSetting
{
    private GameSetting(string id, string label, string description, GameSettingKind kind, string @default, bool requiresRestart)
    {
        (Id, Label, Description, Kind, Default, RequiresRestart) = (id, label, description, kind, @default, requiresRestart);
    }

    /// <summary>Stable id, unique in the schema (for mkw-recomp: the mkw.ini key, "fullscreen").</summary>
    public string Id { get; }

    public string Label { get; init; }

    public string Description { get; init; }

    public GameSettingKind Kind { get; }

    /// <summary>The value in effect when the game has none set ("" for a choice: its automatic option).</summary>
    public string Default { get; init; }

    /// <summary>
    /// True: the game reads it only when it starts. False: the game's own menu applies it at once.
    /// The launcher writes while the game is closed, so its changes always count from the next start.
    /// </summary>
    public bool RequiresRestart { get; }

    /// <summary>A heading inside the page ("Resolution"); consecutive settings with the same one belong together. Null: none.</summary>
    public string? Group { get; init; }

    /// <summary><see cref="GameSettingKind.Choice"/>: the options in order. Empty for the other kinds.</summary>
    public IReadOnlyList<GameSettingOption> Options { get; init; } = [];

    /// <summary><see cref="GameSettingKind.Range"/>.</summary>
    public double Min { get; init; }

    public double Max { get; init; }

    public double Step { get; init; } = 1;

    /// <summary><see cref="GameSettingKind.Range"/>: what follows the number ("%", "ms"), or "".</summary>
    public string Unit { get; init; } = "";

    /// <summary><see cref="GameSettingKind.Text"/>.</summary>
    public int MaxLength { get; init; } = 128;

    /// <summary><see cref="GameSettingKind.Text"/>: a regular expression the whole text must match, or null.</summary>
    public string? Pattern { get; init; }

    /// <summary>When set, the setting only matters (show or enable it) under this condition.</summary>
    public GameSettingCondition? VisibleWhen { get; init; }

    /// <summary>Why it cannot be changed here ("set by the environment variable MKW_FPS"); null when it can.</summary>
    public string? Locked { get; init; }

    /// <summary><see cref="GameSettingKind.Range"/>: the decimals of <see cref="Step"/> (0.05 has 2), which values are rounded to.</summary>
    public int Decimals
    {
        get
        {
            var text = Step.ToString("0.######", CultureInfo.InvariantCulture);
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            return dot < 0 ? 0 : text.Length - dot - 1;
        }
    }

    public static GameSetting Toggle(string id, string label, string description, bool @default, bool requiresRestart) =>
        new(id, label, description, GameSettingKind.Toggle, @default ? "1" : "0", requiresRestart);

    public static GameSetting Choice(string id, string label, string description, string @default, bool requiresRestart, params GameSettingOption[] options) =>
        new(id, label, description, GameSettingKind.Choice, @default, requiresRestart) { Options = options };

    public static GameSetting Range(string id, string label, string description, double @default, bool requiresRestart, double min, double max, double step = 1, string unit = "")
    {
        var s = new GameSetting(id, label, description, GameSettingKind.Range, "", requiresRestart) { Min = min, Max = max, Step = step, Unit = unit };
        return s with { Default = s.Format(@default) };
    }

    public static GameSetting Text(string id, string label, string description, string @default, bool requiresRestart, int maxLength = 128, string? pattern = null) =>
        new(id, label, description, GameSettingKind.Text, @default, requiresRestart) { MaxLength = maxLength, Pattern = pattern };

    /// <summary>A number as this range writes it: rounded to <see cref="Decimals"/>, with a '.', no trailing zeros.</summary>
    public string Format(double value)
    {
        var decimals = Decimals;
        return Math.Round(value, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The value as it is written, or a <see cref="LauncherException"/> saying why the setting
    /// does not take it: a toggle takes "0" or "1", a choice one of its options, a range a number
    /// inside it, a text one line that fits and matches.
    /// </summary>
    public string Normalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        switch (Kind)
        {
            case GameSettingKind.Toggle:
                return value is "0" or "1" ? value : throw Refused(value, Strings.GameSettingRefusedToggle);
            case GameSettingKind.Choice:
                return Options.Any(o => o.Value == value) ? value : throw Refused(value, Strings.GameSettingRefusedChoice(string.Join(", ", Options.Select(o => o.Value.Length == 0 ? "\"\"" : o.Value))));
            case GameSettingKind.Range:
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                {
                    throw Refused(value, Strings.GameSettingRefusedNumber);
                }
                // compared after rounding to the step's decimals, so 1.5000001 from a slider is 1.5
                number = Math.Round(number, Decimals);
                return number >= Min && number <= Max ? Format(number) : throw Refused(value, Strings.GameSettingRefusedRange(Format(Min), Format(Max)));
            default:
                if (value.Length > MaxLength)
                {
                    throw Refused(value, Strings.GameSettingRefusedLength(MaxLength));
                }
                if (value.Any(char.IsControl) || value != value.Trim() || (value.Length >= 2 && value[0] == '"' && value[^1] == '"'))
                {
                    throw Refused(value, Strings.GameSettingRefusedLine);
                }
                if (value.Length > 0 && Pattern is { } pattern && !Regex.IsMatch(value, $"^(?:{pattern})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                {
                    throw Refused(value, Strings.GameSettingRefusedForm);
                }
                return value;
        }
    }

    private LauncherException Refused(string value, string why) => new(Strings.GameSettingRefused(Label, value, why));
}
