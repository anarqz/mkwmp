// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text.Json;

namespace Mkwmp.Core.Targets;

/// <summary>
/// One mod of the game, as a target lists it (launcher/docs/MODS.md): data only, so the window draws
/// the Mods page for any target without knowing it.
/// </summary>
/// <param name="Id">The mod's stable name in the game (mkw-recomp: its folder under <c>mods/</c>, what <c>mods = a,b</c> lists).</param>
/// <param name="Name">What the player reads.</param>
public sealed record GameMod(string Id, string Name)
{
    public string Version { get; init; } = "";

    public string Author { get; init; } = "";

    public string Description { get; init; } = "";

    /// <summary>The game loads it at its next start.</summary>
    public bool Enabled { get; init; }

    /// <summary>It carries native code the game loads into its own process (mkw-recomp: a <c>plugin/</c> folder): it can do anything the game can on this PC.</summary>
    public bool HasPlugin { get; init; }

    /// <summary>It replaces or adds files of the game (mkw-recomp: a <c>romfs/</c> folder).</summary>
    public bool HasFiles { get; init; }

    /// <summary>How many game files it replaces or adds.</summary>
    public int FileCount { get; init; }

    /// <summary>It patches the game's own code: the launcher lists it and cannot turn it on or off (see <see cref="Locked"/>).</summary>
    public bool ChangesCode { get; init; }

    /// <summary>The launcher's own mod (MKWMP's): installed, updated and turned on by the integration, never uninstalled here.</summary>
    public bool Managed { get; init; }

    /// <summary>Why it cannot be turned on or off here, for the player; null when it can.</summary>
    public string? Locked { get; init; }

    /// <summary>Bytes on disk.</summary>
    public long Size { get; init; }

    /// <summary>The settings the mod declares (its <c>options.json</c>), drawn with the game settings' own rows; null when it declares none.</summary>
    public GameSettingsSchema? Options { get; init; }

    /// <summary>Where the launcher got it; null for a mod it did not install.</summary>
    public ModSource? Source { get; init; }

    /// <summary>What the player should know, a line each: a conflict with another mod, files the game treats its own way, a list the environment overrides.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];

    public bool HasOptions => Options is { } o && o.Settings.Any();
}

/// <summary>Where an installed mod came from (kept beside it, for "Update" and the link to its page).</summary>
/// <param name="Url">The link it was downloaded from, or the mod's page; null for a local file.</param>
/// <param name="GameBananaMod">Its GameBanana id; 0 when it is not from there.</param>
/// <param name="GameBananaFile">The file of that mod that was installed; 0 when unknown.</param>
/// <param name="Version">The version its page gave when it was installed ("" when none).</param>
/// <param name="FileAdded">When GameBanana got that file (Unix seconds): a newer file of the mod is an update.</param>
public sealed record ModSource(string? Url, int GameBananaMod = 0, int GameBananaFile = 0, string Version = "", long FileAdded = 0);

public enum ModInstallCode
{
    Done,

    /// <summary>The game runs: nothing of it is written.</summary>
    GameRunning,

    /// <summary>No game folder, or this game takes no mods.</summary>
    NoGame,

    /// <summary>Nothing of a mod inside (mkw-recomp: no <c>romfs/</c>, <c>plugin/</c> or <c>exefs/</c>).</summary>
    Layout,

    /// <summary>The archive holds several mods: <see cref="ModInstallResult.Variants"/> names them; ask again with one.</summary>
    Variants,

    /// <summary>No usable name.</summary>
    Name,

    /// <summary>A mod of that name is installed: ask again with <see cref="ModInstallRequest.Replace"/>.</summary>
    Exists,

    /// <summary>Over the unpacking limits (a zip bomb, or not a mod).</summary>
    TooBig,

    /// <summary>A name that would leave the mod's folder, a link, or a file the launcher never unpacks.</summary>
    Unsafe,

    /// <summary>Not an archive the launcher reads.</summary>
    Unpack,

    /// <summary>It patches the game's code: the game must be built again for it, which the launcher does not do.</summary>
    ChangesCode,

    /// <summary>It replaces game files, and the game's own files could not be read to describe them.</summary>
    GameFiles,

    /// <summary>The launcher's own mod: the integration installs it.</summary>
    Managed,

    Io,
    Download,
    Cancelled,
}

/// <param name="Message">For the player, in their language.</param>
/// <param name="ModId">The mod now in the game, when <see cref="ModInstallCode.Done"/> (or the one in the way, for <see cref="ModInstallCode.Exists"/>).</param>
/// <param name="Variants">The mods found in the archive, as paths inside it (<see cref="ModInstallCode.Variants"/>).</param>
/// <param name="HasPlugin">The mod carries native code: turning it on asks first.</param>
/// <param name="Enabled">The install turned it on.</param>
public sealed record ModInstallResult(ModInstallCode Code, string Message, string? ModId = null, IReadOnlyList<string>? Variants = null, bool HasPlugin = false, bool Enabled = false)
{
    public bool Ok => Code == ModInstallCode.Done;
}

/// <param name="Path">An archive (.zip, .7z or .rar) or an unpacked folder.</param>
public sealed record ModInstallRequest(string Path)
{
    /// <summary>The name to install it under; null: the mod's own.</summary>
    public string? Name { get; init; }

    /// <summary>The mod to take from an archive with several, as <see cref="ModInstallResult.Variants"/> listed it.</summary>
    public string? Variant { get; init; }

    /// <summary>Replace an installed mod of the same name (its files are kept as a backup until the new ones are in place, and once more beside the mods).</summary>
    public bool Replace { get; init; }

    /// <summary>
    /// Turn it on when it is in. A mod with native code is only turned on with <see cref="AcceptCode"/>:
    /// without it the install leaves it off and says <see cref="ModInstallResult.HasPlugin"/>.
    /// </summary>
    public bool Enable { get; init; } = true;

    /// <summary>The player read that the mod runs code on this PC, and said yes.</summary>
    public bool AcceptCode { get; init; }

    public ModSource? Source { get; init; }
}

/// <summary>How far an install is: a download's bytes, then the unpacking and checking.</summary>
/// <param name="Stage">"download", "unpack", "check", "install".</param>
public sealed record ModProgress(string Stage, long Done, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : double.NaN;
}

/// <summary>
/// A mod's declarative options: <c>options.json</c> beside its <c>mod.json</c> (launcher/docs/MODS.md has
/// the format). Each option is one key of the mod's per-player settings file, the same one its plugin
/// reads, so the launcher draws them with the game settings' rows and the mod needs no window of its own.
/// </summary>
public static class ModOptions
{
    public const string FileName = "options.json";
    public const int MaxBytes = 256 * 1024;
    public const int MaxOptions = 96;

    /// <summary>The host's rule for a key: letters, digits, '.', '_', '-', at most 64.</summary>
    public static bool ValidKey(string key) =>
        key is { Length: > 0 and <= 64 } && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>
    /// The schema of an <c>options.json</c>, texts in <paramref name="locale"/> when the file has them
    /// ("pt-BR", then "pt", then "en", then whatever comes first). Null when the file declares no
    /// usable option; <paramref name="problem"/> then says why when it is broken (not when it is just empty).
    /// An entry that is wrong (an unknown type, a bad key, a range without ends) is left out, the others stay.
    /// </summary>
    public static GameSettingsSchema? Parse(ReadOnlySpan<byte> json, string? locale, out string? problem)
    {
        problem = null;
        if (json.Length > MaxBytes)
        {
            problem = "options.json is too large";
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("options", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                problem = "options.json has no \"options\" list";
                return null;
            }
            var settings = new List<GameSetting>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in list.EnumerateArray())
            {
                if (settings.Count >= MaxOptions)
                {
                    break;
                }
                if (One(entry, locale) is { } setting && seen.Add(setting.Id))
                {
                    settings.Add(setting);
                }
            }
            // a condition on a key the file does not declare would hide its option for ever
            settings = [.. settings.Select(s => s.VisibleWhen is { } when && !seen.Contains(when.Setting) ? s with { VisibleWhen = null } : s)];
            if (settings.Count == 0)
            {
                return null;
            }
            var note = root.TryGetProperty("note", out var n) ? Text(n, locale) : "";
            return new GameSettingsSchema([new GameSettingsCategory("options", "", "Settings", settings)], note.Length > 0 ? note : null);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            problem = "options.json: " + e.Message;
            return null;
        }
    }

    private static GameSetting? One(JsonElement e, string? locale)
    {
        if (e.ValueKind != JsonValueKind.Object || Str(e, "key") is not { } key || !ValidKey(key))
        {
            return null;
        }
        var label = e.TryGetProperty("label", out var l) ? Text(l, locale) : "";
        if (label.Length == 0)
        {
            label = key;
        }
        var description = e.TryGetProperty("description", out var d) ? Text(d, locale) : "";
        var restart = !e.TryGetProperty("restart", out var r) || r.ValueKind != JsonValueKind.False; // a mod reads its settings when the game starts, unless it says otherwise
        var fallback = e.TryGetProperty("default", out var def) ? def : default;
        GameSetting? setting;
        switch (Str(e, "type"))
        {
            case "toggle":
                setting = GameSetting.Toggle(key, label, description, fallback.ValueKind == JsonValueKind.True || Scalar(fallback) == "1", restart);
                break;
            case "choice":
                var options = new List<GameSettingOption>();
                if (e.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in choices.EnumerateArray().Take(64))
                    {
                        var value = c.ValueKind == JsonValueKind.Object ? (c.TryGetProperty("value", out var v) ? Scalar(v) : null) : Scalar(c);
                        if (value is null || !Line(value) || options.Any(o => o.Value == value))
                        {
                            continue;
                        }
                        var text = c.ValueKind == JsonValueKind.Object && c.TryGetProperty("label", out var cl) ? Text(cl, locale) : "";
                        options.Add(new GameSettingOption(value, text.Length > 0 ? text : value));
                    }
                }
                if (options.Count == 0)
                {
                    return null;
                }
                var chosen = Scalar(fallback);
                setting = GameSetting.Choice(key, label, description, chosen is not null && options.Any(o => o.Value == chosen) ? chosen : options[0].Value, restart, [.. options]);
                break;
            case "range":
                if (Num(e, "min") is not { } min || Num(e, "max") is not { } max || !(max > min))
                {
                    return null;
                }
                var step = Num(e, "step") is { } s && s > 0 && s <= max - min ? s : 1;
                var start = fallback.ValueKind == JsonValueKind.Number && fallback.TryGetDouble(out var number) ? Math.Clamp(number, min, max) : min;
                setting = GameSetting.Range(key, label, description, start, restart, min, max, step, Cut(Str(e, "unit") ?? "", 8));
                break;
            case "text":
                var length = Num(e, "max_length") is { } m && m >= 1 ? (int)Math.Min(m, 1024) : 128;
                var initial = Scalar(fallback) ?? "";
                setting = GameSetting.Text(key, label, description, Line(initial) && initial.Length <= length ? initial : "", restart, length, Str(e, "pattern") is { Length: > 0 and <= 256 } pattern ? pattern : null);
                break;
            default:
                return null;
        }
        if (e.TryGetProperty("group", out var g) && Text(g, locale) is { Length: > 0 } group)
        {
            setting = setting with { Group = group };
        }
        if (e.TryGetProperty("visible_when", out var when) && when.ValueKind == JsonValueKind.Object && Str(when, "key") is { } other && ValidKey(other)
            && when.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            var wanted = values.EnumerateArray().Select(Scalar).OfType<string>().Take(64).ToList();
            if (wanted.Count > 0 && other != key)
            {
                setting = setting with { VisibleWhen = new GameSettingCondition(other, wanted) };
            }
        }
        return setting;
    }

    /// <summary>A text of the file: a string, or one per language (<c>{"en": "…", "pt-BR": "…"}</c>).</summary>
    private static string Text(JsonElement e, string? locale)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            return Clean(e.GetString());
        }
        if (e.ValueKind != JsonValueKind.Object)
        {
            return "";
        }
        string? Of(string? id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Name.Equals(id, StringComparison.OrdinalIgnoreCase))
                {
                    return p.Value.GetString();
                }
            }
            return null;
        }
        var language = locale?.Split('-')[0];
        var first = e.EnumerateObject().FirstOrDefault(p => p.Value.ValueKind == JsonValueKind.String);
        return Clean(Of(locale) ?? Of(language) ?? Of("en") ?? (first.Value.ValueKind == JsonValueKind.String ? first.Value.GetString() : null));
    }

    private static string Clean(string? text) => Cut(new string([.. (text ?? "").Where(c => !char.IsControl(c) || c == '\n')]).Trim(), 600);

    private static string Cut(string text, int most) => text.Length <= most ? text : text[..most];

    private static bool Line(string value) => value.Length <= 1024 && !value.Any(char.IsControl);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>A value as the settings file holds it: a string as it is, true and false as "1" and "0", a number in its shortest form.</summary>
    private static string? Scalar(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.True => "1",
        JsonValueKind.False => "0",
        JsonValueKind.Number when e.TryGetDouble(out var d) => d.ToString("0.######", CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// What a mod's settings file holds for the schema's options (by key), each kept only when the
    /// option takes it (a value from an older version of the mod reads as the default).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Known(GameSettingsSchema schema, IReadOnlyDictionary<string, string> file)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(file);
        var set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in schema.Settings)
        {
            if (!file.TryGetValue(s.Id, out var value))
            {
                continue;
            }
            try
            {
                set[s.Id] = s.Kind == GameSettingKind.Toggle ? (value == "1" ? "1" : "0") : s.Normalize(value);
            }
            catch (LauncherException)
            {
                // not a value of this option any more
            }
        }
        return set;
    }
}
