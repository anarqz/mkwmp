// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

namespace Mkwmp.Core.Tests;

/// <summary>The core's language is one for the process: these tests run alone, and give English back.</summary>
[CollectionDefinition(nameof(OneLanguageAtATime), DisableParallelization = true)]
public sealed class OneLanguageAtATime;

/// <summary>
/// The window gives the core another language (<see cref="Strings.Use"/>): texts, numbers and the
/// game's settings follow at once, and a key the language lacks stays English.
/// </summary>
[Collection(nameof(OneLanguageAtATime))]
public sealed class LanguageTests
{
    private static readonly Dictionary<string, string> German = new(StringComparer.Ordinal)
    {
        ["core.status.ready"] = "BEREIT · DRÜCKE SPIELEN",
        ["core.set_game_folder"] = "Lege den Spielordner fest ({hint})",
        ["core.recomp.folder_hint"] = "der mit mkw.exe",
        ["core.runtime.no_space"] = "Zu wenig Platz auf {drive}: {need} nötig, {free} frei",
        ["core.gigabytes"] = "{n} GB",
        ["core.status.queued"] = "SERVER VOLL · DU BIST NR. {position} IN DER WARTESCHLANGE",
        ["gs.page.screen"] = "Bildschirm",
        ["gs.fullscreen"] = "Vollbild",
        ["gs.vk_present.opt.fifo"] = "must not be used: VSync is a name",
        ["gs.quality.opt.low"] = "Niedrig",
        ["gs.group.picture"] = "Bild",
        ["core.gs.next_start"] = "Änderungen gelten ab dem nächsten Spielstart.",
    };

    private static GameInstall Install()
    {
        var folder = Rig.NewFolder("language");
        File.WriteAllBytes(Path.Combine(folder, "mkw.exe"), Rig.TestModExe);
        return new MkwRecompTarget("1.4.0").Detect(folder)!;
    }

    [Fact]
    public void The_core_speaks_the_language_it_is_given_and_English_again_after()
    {
        var install = Install();
        var english = RecompSettings.Schema(install, _ => null)!;
        var changes = 0;
        void Count() => changes++;
        Strings.Changed += Count;
        try
        {
            Strings.Use(key => German.GetValueOrDefault(key), CultureInfo.GetCultureInfo("de-DE"));
            Assert.Equal(1, changes);
            Assert.Equal("BEREIT · DRÜCKE SPIELEN", Strings.Ready);
            Assert.Equal("Lege den Spielordner fest (der mit mkw.exe)", Strings.SetGameFolder(new MkwRecompTarget("1.4.0").FolderHint));
            Assert.Equal("Zu wenig Platz auf C:\\: 1,5 GB nötig, 0,2 GB frei", Strings.RuntimeNoSpace("C:\\", 1_500_000_000, 200_000_000));
            Assert.Equal("Server unreachable: retry", Strings.Unreachable); // not in this catalog: English
            Assert.Equal("LEGE DEN SPIELORDNER FEST", Strings.Upper("Lege den Spielordner fest"));

            var schema = RecompSettings.Schema(install, _ => null)!;
            Assert.NotSame(english, schema);
            Assert.Equal("Bildschirm", schema.Categories[0].Title);
            Assert.Equal("Graphics", schema.Categories[1].Title);
            Assert.Equal("Vollbild", schema.Find("fullscreen")!.Label);
            Assert.Equal("Bild", schema.Find("hdr")!.Group);
            Assert.Equal("Niedrig", schema.Find("quality")!.Options[0].Label);
            Assert.Equal("VSync", schema.Find("vk_present")!.Options.Single(o => o.Value == "fifo").Label);
            Assert.Equal("日本語", schema.Find("lang")!.Options.Single(o => o.Value == "ja").Label);
            Assert.Equal("Änderungen gelten ab dem nächsten Spielstart.", schema.Note);
            // ids, values and defaults are the game's: a language changes none of them
            Assert.Equal(english.Settings.Select(s => (s.Id, s.Default, s.Kind)), schema.Settings.Select(s => (s.Id, s.Default, s.Kind)));
            Assert.Equal(english.Settings.SelectMany(s => s.Options.Select(o => o.Value)), schema.Settings.SelectMany(s => s.Options.Select(o => o.Value)));
        }
        finally
        {
            Strings.Use(null, null);
            Strings.Changed -= Count;
        }
        Assert.Equal("READY · PRESS PLAY", Strings.Ready);
        Assert.Equal("Fullscreen", RecompSettings.Schema(install, _ => null)!.Find("fullscreen")!.Label);
        Assert.Same(CultureInfo.InvariantCulture, Strings.Culture);
    }

    [Fact]
    public void Turkish_upper_case_keeps_its_dotted_i()
    {
        try
        {
            Strings.Use(_ => null, CultureInfo.GetCultureInfo("tr-TR"));
            Assert.Equal("OYUN KLASÖRÜNÜ SEÇİN", Strings.Upper("Oyun klasörünü seçin"));
        }
        finally
        {
            Strings.Use(null, null);
        }
    }
}
