// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Mkwmp.Core;

namespace Mkwmp.App;

/// <summary>
/// The launcher's strings in the player's language (Infinity Launcher's catalogs: flat
/// <c>key → text</c> JSON with <c>{name}</c> placeholders, one file per language in <c>i18n/</c>
/// beside the exe; the languages are <see cref="Locales.All"/>). A key missing in a language falls
/// back to English, then to the key itself. Bind with <c>{Binding S[key]}</c>.
/// <para>
/// There is one instance for the whole window, and it changes language in place
/// (<see cref="Switch"/>): every <c>S[key]</c> binding reads again, with no restart. The same
/// catalogs hold the core's texts (<c>core.*</c>, <c>gs.*</c>), which the core asks for through
/// <see cref="Find"/> (<see cref="Mkwmp.Core.Strings.Use"/>).
/// </para>
/// </summary>
public sealed class Strings : INotifyPropertyChanged
{
    private readonly string? _directory;
    private readonly IReadOnlyDictionary<string, string> _english;
    private IReadOnlyDictionary<string, string> _chosen;

    private Strings(string? directory, string locale, IReadOnlyDictionary<string, string> chosen, IReadOnlyDictionary<string, string> english, CultureInfo culture)
    {
        _directory = directory;
        Locale = locale;
        _chosen = chosen;
        _english = english;
        Culture = culture;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The language in use: an id of <see cref="Locales.All"/> ("en", "pt-BR", "ja"...).</summary>
    public string Locale { get; private set; }

    /// <summary>The culture dates, times and numbers are written in: the language's, or the system's own when it speaks that language.</summary>
    public CultureInfo Culture { get; private set; }

    /// <summary>
    /// Never throws. The catalogs are built into the launcher (<see cref="Catalogs"/>); the files in
    /// <paramref name="directory"/> are read on top, so a missing folder or file changes nothing.
    /// </summary>
    public static Strings Load(string directory, string locale, CultureInfo? system = null)
    {
        var english = Catalogs.Load(directory, Locales.English);
        var id = Locales.Find(locale)?.Id ?? Locales.English;
        return new Strings(directory, id, id == Locales.English ? english : Catalogs.Load(directory, id), english,
            Locales.CultureOf(id, system ?? CultureInfo.CurrentUICulture));
    }

    public static Strings FromJson(string locale, string chosenJson, string englishJson) =>
        new(null, locale, Parse(chosenJson), Parse(englishJson), Locales.CultureOf(locale, CultureInfo.InvariantCulture));

    /// <summary>
    /// Changes the language in place and tells every binding. False when it is the one in use already.
    /// </summary>
    public bool Switch(string locale, CultureInfo system)
    {
        var id = Locales.Find(locale)?.Id ?? Locales.English;
        if (id == Locale || _directory is null)
        {
            return false;
        }
        _chosen = id == Locales.English ? _english : Catalogs.Load(_directory, id);
        Locale = id;
        Culture = Locales.CultureOf(id, system);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Locale)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]")); // every S[key] binding
        return true;
    }

    public string this[string key] => Find(key) ?? key;

    /// <summary>The text of a key in the language in use, else in English, else null (the core then keeps its own English).</summary>
    public string? Find(string key) => _chosen.TryGetValue(key, out var text) || _english.TryGetValue(key, out text) ? text : null;

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

    /// <summary>A whole number as the language in use writes it.</summary>
    public string Number(long value) => value.ToString(Culture);

    private static Dictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
}
