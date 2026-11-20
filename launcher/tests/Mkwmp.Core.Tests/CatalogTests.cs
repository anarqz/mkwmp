// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mkwmp.Core;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>
/// The launcher's catalogs (<c>src/Mkwmp.App/i18n/*.json</c>, copied beside the tests): every
/// language has English's keys and placeholders, and the core's English is the English catalog.
/// </summary>
public sealed partial class CatalogTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "i18n");

    [GeneratedRegex(@"\{[A-Za-z_]+\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static Dictionary<string, string> Read(string locale) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Folder, locale + ".json")))!;

    private static string[] Holes(string text) => [.. Placeholder().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    public static TheoryData<string> Translations => [.. Locales.All.Select(l => l.Id).Where(id => id != "en")];

    [Fact]
    public void Every_language_of_the_selector_has_a_catalog_and_every_catalog_a_language()
    {
        var files = Directory.GetFiles(Folder, "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Locales.All.Select(l => l.Id).Order(StringComparer.Ordinal).ToArray(), files);
    }

    [Fact]
    public void English_has_no_empty_text()
    {
        Assert.Empty(Read("en").Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_catalog_has_exactly_the_keys_of_English_with_the_same_placeholders(string locale)
    {
        var english = Read("en");
        var catalog = Read(locale);
        Assert.Empty(english.Keys.Except(catalog.Keys).Order(StringComparer.Ordinal).Select(k => "missing: " + k));
        Assert.Empty(catalog.Keys.Except(english.Keys).Order(StringComparer.Ordinal).Select(k => "not in en: " + k));
        Assert.Empty(catalog.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "empty: " + p.Key));
        Assert.Empty(catalog.Where(p => !Holes(p.Value).SequenceEqual(Holes(english[p.Key])))
            .Select(p => $"placeholders of {p.Key}: {string.Join(' ', Holes(p.Value))} (en: {string.Join(' ', Holes(english[p.Key]))})"));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void A_catalog_keeps_the_names_that_are_never_translated(string locale)
    {
        var english = Read("en");
        var catalog = Read(locale);
        string[] names = ["MKWMP", "mkw-recomp", "Mario Kart World"];
        Assert.Empty(catalog.Where(p => names.Any(n => english[p.Key].Contains(n, StringComparison.Ordinal) && !p.Value.Contains(n, StringComparison.Ordinal)))
            .Select(p => p.Key));
    }

    [Fact]
    public void The_core_speaks_the_English_catalog()
    {
        var english = Read("en");
        Assert.Equal(english.Count, Strings.English.Count);
        Assert.Equal("Server unreachable: retry", Strings.Unreachable);
        Assert.Equal(english["core.status.ready"], Strings.Ready);
        Assert.Equal("too many tries; wait 2 min", Strings.Limited(95));
        Assert.Equal("Not enough free space on C:\\: 1.5 GB needed, 0.2 GB free", Strings.RuntimeNoSpace("C:\\", 1_500_000_000, 200_000_000));
    }

    /// <summary>A key typed wrong in Strings.cs would show as the key itself.</summary>
    [Fact]
    public void Every_text_of_the_core_is_in_the_catalog()
    {
        var texts = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(string))
            .Select(p => (p.Name, Text: (string)p.GetValue(null)!))
            .ToList();
        Assert.True(texts.Count > 100);
        Assert.Empty(texts.Where(t => t.Text.StartsWith("core.", StringComparison.Ordinal)).Select(t => t.Name));
        Assert.Empty(Strings.ReportReasons.Where(r => r.Label.StartsWith("core.", StringComparison.Ordinal)).Select(r => r.Code));
        foreach (var state in new[] { "installed", "unverified", "missing", "outdated", "damaged", "host_missing", "dev", "mod" })
        {
            Assert.DoesNotContain("core.", Strings.PatchState(state, "1.0.0", "X"), StringComparison.Ordinal);
        }
        foreach (var code in new[] { "source", "moved", "nogame", "encrypted", "version", "romfs", "mismatch", "space", "failed", "io", "no_setup", "cancelled", "stopped", "?" })
        {
            foreach (var detail in new[] { null, "detail" })
            {
                Assert.DoesNotContain("core.", Strings.SetupError(code, detail, null, null), StringComparison.Ordinal);
                Assert.DoesNotContain("core.", Strings.SetupError(code, detail, "Building the game", 3), StringComparison.Ordinal);
            }
        }
        foreach (var step in RecompSetupProgress.Steps)
        {
            Assert.DoesNotContain("core.", Strings.SetupStep(step, copy: true) + Strings.SetupStep(step, copy: false), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The game's settings are a table in the core (RecompSettings.Build) and <c>gs.*</c> keys in the
    /// catalogs. When this fails after a change to the table, the file it names holds the keys English must have.
    /// </summary>
    [Fact]
    public void The_game_settings_of_the_catalog_are_the_table_s()
    {
        var want = RecompSettings.AllTexts();
        var have = Read("en").Where(p => p.Key.StartsWith("gs.", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (want.Count != have.Count || want.Any(p => !have.TryGetValue(p.Key, out var text) || text != p.Value))
        {
            var dump = Path.Combine(Path.GetTempPath(), "mkwmp-gs-en.json");
            File.WriteAllText(dump, JsonSerializer.Serialize(want, Indented));
            Assert.Fail($"en.json's gs.* keys differ from RecompSettings' table: what they must be is in {dump}");
        }
    }

    /// <summary>An old launcher that updated itself into this one has only the exe: no i18n folder beside it.</summary>
    [Theory]
    [MemberData(nameof(Translations))]
    public void Without_the_i18n_folder_the_launcher_still_speaks_every_language(string locale)
    {
        var nowhere = Path.Combine(Path.GetTempPath(), "mkwmp-no-such-folder-" + Guid.NewGuid().ToString("N"));
        var file = Read(locale);
        Assert.Equal(file, Catalogs.BuiltIn(locale));
        Assert.Equal(file, Catalogs.Load(nowhere, locale));
        Assert.Equal(file, Catalogs.Load(null, locale));
        Assert.Equal(Read("en"), Catalogs.Load(nowhere, "en"));
        Assert.NotEqual(Read("en")["common.cancel"], Catalogs.Load(nowhere, locale)["common.cancel"]);
    }

    [Fact]
    public void A_catalog_beside_the_exe_wins_key_by_key_and_a_broken_one_is_ignored()
    {
        var folder = Rig.NewFolder("catalogs");
        File.WriteAllText(Path.Combine(folder, "de.json"), """{ "common.cancel": "Abbruch!", "common.ok": "", "only.here": "nur hier" }""");
        var german = Catalogs.Load(folder, "de");
        Assert.Equal("Abbruch!", german["common.cancel"]);
        Assert.Equal("nur hier", german["only.here"]);
        Assert.Equal(Read("de")["common.ok"], german["common.ok"]); // an empty text is no text
        Assert.Equal(Read("de")["common.close"], german["common.close"]);
        File.WriteAllText(Path.Combine(folder, "fr.json"), "{ not json");
        Assert.Equal(Read("fr"), Catalogs.Load(folder, "fr"));
        Assert.Equal(Read("ja"), Catalogs.Load(folder, "ja")); // no file for it there
        Assert.Empty(Catalogs.BuiltIn("xx"));
        Assert.Empty(Catalogs.Load(folder, "..\\de"));
    }

    [Theory]
    [InlineData(null, "pt-BR", "pt-BR")]
    [InlineData(null, "pt-PT", "pt-PT")]
    [InlineData(null, "pt-AO", "pt-PT")]
    [InlineData(null, "es-MX", "es")]
    [InlineData(null, "es-ES", "es")]
    [InlineData(null, "fr-CA", "fr")]
    [InlineData(null, "de-AT", "de")]
    [InlineData(null, "ja-JP", "ja")]
    [InlineData(null, "ko-KR", "ko")]
    [InlineData(null, "zh-CN", "zh-Hans")]
    [InlineData(null, "zh-SG", "zh-Hans")]
    [InlineData(null, "zh-TW", "zh-Hant")]
    [InlineData(null, "zh-HK", "zh-Hant")]
    [InlineData(null, "zh-Hant-MO", "zh-Hant")]
    [InlineData(null, "ru-RU", "ru")]
    [InlineData(null, "uk-UA", "en")]
    [InlineData(null, "ar-SA", "en")]
    [InlineData(null, "en-GB", "en")]
    [InlineData("de", "ja-JP", "de")]
    [InlineData("xx", "tr-TR", "tr")]
    [InlineData("", "pl-PL", "pl")]
    public void The_language_is_the_saved_one_else_the_closest_to_the_system_s(string? saved, string culture, string expected)
    {
        Assert.Equal(expected, Locales.For(saved, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void Each_language_names_itself_and_has_a_culture()
    {
        Assert.Equal("en", Locales.All[0].Id);
        Assert.All(Locales.All, l =>
        {
            Assert.False(string.IsNullOrWhiteSpace(l.Name));
            Assert.Equal(l.Culture, CultureInfo.GetCultureInfo(l.Culture).Name);
        });
        Assert.Equal(Locales.All.Count, Locales.All.Select(l => l.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
