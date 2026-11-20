// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Media;

namespace Mkwmp.App;

/// <summary>
/// The launcher's letters, all inside the exe (Assets/Fonts, OFL): Nunito for Latin, Greek and
/// Cyrillic, and behind it Noto Sans JP, KR, SC and TC for Japanese, Korean and Chinese, so those
/// languages read the same on Windows and under Wine/Proton, where the system has no such fonts.
/// <para>
/// The same Han character is drawn differently in Japan, China, Taiwan and Korea, and the first
/// font of the chain that has it wins: so the chain's order follows the language in use
/// (<see cref="Apply"/>), and <c>Font.Ui</c> is a dynamic resource. What none of them has (Thai,
/// Arabic, emoji) falls to the system's fonts, as before.
/// </para>
/// </summary>
public static class Fonts
{
    public const string ResourceKey = "Font.Ui";

    private const string Folder = "./Assets/Fonts/";
    private const string Latin = "Nunito";
    private const string Japanese = "Noto Sans JP";
    private const string Korean = "Noto Sans KR";
    private const string Simplified = "Noto Sans SC";
    private const string Traditional = "Noto Sans TC";

    private static readonly Uri Pack = new("pack://application:,,,/");

    /// <summary>The chain for the language in use.</summary>
    public static FontFamily Ui { get; private set; } = For("en");

    /// <summary>Nunito, then the CJK fonts with the language's own first.</summary>
    public static FontFamily For(string locale)
    {
        string[] cjk = locale switch
        {
            "ja" => [Japanese, Simplified, Traditional, Korean],
            "ko" => [Korean, Japanese, Simplified, Traditional],
            "zh-Hant" => [Traditional, Simplified, Japanese, Korean],
            _ => [Simplified, Traditional, Japanese, Korean],
        };
        return new FontFamily(Pack, string.Join(", ", new[] { Latin }.Concat(cjk).Select(family => Folder + "#" + family)));
    }

    /// <summary>Makes <paramref name="locale"/>'s chain the window's font (<c>{DynamicResource Font.Ui}</c>).</summary>
    public static void Apply(string locale)
    {
        Ui = For(locale);
        if (Application.Current is { } app)
        {
            app.Resources[ResourceKey] = Ui;
        }
    }

    public static Typeface Face(FontWeight weight) => new(Ui, FontStyles.Normal, weight, FontStretches.Normal);
}
