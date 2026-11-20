// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;

namespace Mkwmp.Core;

/// <summary>A language of the launcher.</summary>
/// <param name="Id">The catalog's name (<c>i18n/&lt;id&gt;.json</c>) and what <see cref="UiSettings.Locale"/> saves.</param>
/// <param name="Name">The language in its own words, as the selector lists it.</param>
/// <param name="Culture">The .NET culture whose dates and numbers it uses.</param>
public sealed record Locale(string Id, string Name, string Culture);

/// <summary>
/// The languages the launcher has a catalog for, and which one a player gets: the one saved in the
/// settings, else the closest to the system's language, else English.
/// </summary>
public static class Locales
{
    public const string English = "en";

    /// <summary>In the order the selector lists them: English, then the Latin-script ones by name, then the others.</summary>
    public static IReadOnlyList<Locale> All { get; } =
    [
        new("en", "English", "en-US"),
        new("de", "Deutsch", "de-DE"),
        new("es", "Español", "es-ES"),
        new("fr", "Français", "fr-FR"),
        new("it", "Italiano", "it-IT"),
        new("nl", "Nederlands", "nl-NL"),
        new("pl", "Polski", "pl-PL"),
        new("pt-BR", "Português (Brasil)", "pt-BR"),
        new("pt-PT", "Português (Portugal)", "pt-PT"),
        new("tr", "Türkçe", "tr-TR"),
        new("ru", "Русский", "ru-RU"),
        new("ja", "日本語", "ja-JP"),
        new("ko", "한국어", "ko-KR"),
        new("zh-Hans", "简体中文", "zh-CN"),
        new("zh-Hant", "繁體中文", "zh-TW"),
    ];

    private static readonly string[] TraditionalRegions = ["-TW", "-HK", "-MO"];

    /// <summary>The language with that id (case does not matter), or null.</summary>
    public static Locale? Find(string? id) => All.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The saved choice when it is a language the launcher has, else <see cref="Closest"/> to the system's.</summary>
    public static string For(string? saved, CultureInfo culture) => Find(saved)?.Id ?? Closest(culture);

    /// <summary>
    /// The language for a system culture: Brazilian Portuguese for Brazil and European Portuguese
    /// for every other Portuguese; Simplified Chinese unless the culture writes Traditional
    /// (Taiwan, Hong Kong, Macao); Spanish for every Spanish; otherwise the culture's language, or English.
    /// </summary>
    public static string Closest(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        var name = culture.Name;
        switch (culture.TwoLetterISOLanguageName)
        {
            case "pt":
                return name.EndsWith("-BR", StringComparison.OrdinalIgnoreCase) || name.Equals("pt", StringComparison.OrdinalIgnoreCase) ? "pt-BR" : "pt-PT";
            case "zh":
                var traditional = name.Contains("Hant", StringComparison.OrdinalIgnoreCase)
                    || TraditionalRegions.Any(region => name.EndsWith(region, StringComparison.OrdinalIgnoreCase));
                return traditional ? "zh-Hant" : "zh-Hans";
            default:
                return Find(culture.TwoLetterISOLanguageName)?.Id ?? English;
        }
    }

    /// <summary>The culture whose dates and numbers a language uses; the system's own when it speaks that language (pt-BR on a pt-BR system, es-MX for Spanish in Mexico).</summary>
    public static CultureInfo CultureOf(string id, CultureInfo system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var locale = Find(id) ?? All[0];
        return !system.IsNeutralCulture && system.Name.Length > 0 && Closest(system) == locale.Id && (locale.Id != English || system.TwoLetterISOLanguageName == "en")
            ? system
            : CultureInfo.GetCultureInfo(locale.Culture);
    }
}
