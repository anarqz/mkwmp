// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Mkwmp.App;

/// <summary>
/// The window's own strings in English and pt-BR (Infinity Launcher's catalogs: flat
/// <c>key → text</c> JSON with <c>{name}</c> placeholders, in <c>i18n/</c> beside the exe). A key
/// missing in pt-BR falls back to English, then to the key itself. Bind with <c>{Binding S[key]}</c>.
/// </summary>
public sealed class Strings
{
    private readonly IReadOnlyDictionary<string, string> _chosen;
    private readonly IReadOnlyDictionary<string, string> _english;

    private Strings(string locale, IReadOnlyDictionary<string, string> chosen, IReadOnlyDictionary<string, string> english)
    {
        Locale = locale;
        _chosen = chosen;
        _english = english;
    }

    /// <summary><c>en</c> or <c>pt-BR</c>.</summary>
    public string Locale { get; }

    /// <summary>The saved choice when it is a known locale, else <c>pt-BR</c> for any Portuguese UI culture, else <c>en</c>.</summary>
    public static string LocaleFor(string? saved, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return saved is "en" or "pt-BR" ? saved : culture.TwoLetterISOLanguageName == "pt" ? "pt-BR" : "en";
    }

    /// <summary>Never throws: a missing or broken catalog gives the keys themselves.</summary>
    public static Strings Load(string directory, string locale)
    {
        var english = Read(Path.Combine(directory, "en.json"));
        var chosen = locale == "en" ? english : Read(Path.Combine(directory, $"{locale}.json"));
        return new Strings(locale, chosen, english);
    }

    public static Strings FromJson(string locale, string chosenJson, string englishJson) =>
        new(locale, Parse(chosenJson), Parse(englishJson));

    public string this[string key] => _chosen.TryGetValue(key, out var text) || _english.TryGetValue(key, out text) ? text : key;

    /// <summary>The text of <paramref name="key"/> with each <c>{name}</c> replaced.</summary>
    public string Format(string key, params (string Name, string Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var text = this[key];
        foreach (var (name, value) in args)
        {
            text = text.Replace("{" + name + "}", value, StringComparison.Ordinal);
        }
        return text;
    }

    private static Dictionary<string, string> Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}
