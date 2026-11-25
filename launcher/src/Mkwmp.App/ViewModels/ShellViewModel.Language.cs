// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The launcher's language: <c>UiSettings.Locale</c> ("" follows the system), chosen in Settings ›
/// Appearance or on the welcome's first page, and applied at once, with no restart.
/// A change swaps the catalog in place (<see cref="Strings.Switch"/>: every <c>S[key]</c> binding
/// reads again), gives the core the new texts and the culture (dates, numbers, sizes), puts the
/// language's own font first, and rebuilds the open pages, whose view models hold texts they made
/// when they were created.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The system's language, as it was when the launcher started (before the launcher's own choice replaced the thread's culture).</summary>
    public static CultureInfo SystemCulture { get; set; } = CultureInfo.CurrentUICulture;

    /// <summary>The system's regional format (dates, numbers) at the start: kept when the system speaks the launcher's language.</summary>
    public static CultureInfo SystemFormats { get; set; } = CultureInfo.CurrentCulture;

    /// <summary>A language forced for this run (<c>--locale ja</c>): the saved one is not looked at.</summary>
    public string? ForcedLocale { get; set; }

    /// <summary>The language in use changed (the window's own texts and the tray follow).</summary>
    public event EventHandler? LanguageChanged;

    /// <summary>The saved choice: an id of <see cref="Locales.All"/>, or "" for the system's.</summary>
    public string LanguageSetting => Locales.Find(Launcher.Settings.Ui.Locale)?.Id ?? "";

    /// <summary>The saved choice in words: the language in its own name, or "System default".</summary>
    public string LanguageName => Locales.Find(LanguageSetting)?.Name ?? S["settings.language.system"];

    /// <summary>The language a system-default choice gives here, in its own name.</summary>
    public static string SystemLanguageName => Locales.Find(Locales.Closest(SystemCulture))!.Name;

    /// <summary>
    /// The selector: "System default" first, then every language in its own name. The pick is saved
    /// and in use before the modal is gone.
    /// </summary>
    public async Task PickLanguageAsync()
    {
        var options = new List<ChoiceOption>
        {
            new("", S["settings.language.system"], S.Format("settings.language.system_caption", ("name", SystemLanguageName))),
        };
        options.AddRange(Locales.All.Select(l => new ChoiceOption(l.Id, l.Name)));
        var picked = await Choice.AskAsync(this, S["settings.language"], S["settings.language.caption"], options, LanguageSetting).ConfigureAwait(true);
        if (picked is not null)
        {
            await SetLanguageAsync(picked).ConfigureAwait(true);
        }
    }

    /// <summary>Saves the choice ("" for the system's language) and switches to it now.</summary>
    public async Task SetLanguageAsync(string locale)
    {
        ForcedLocale = null; // the player's choice beats a --locale of this run
        await UpdateSettingsAsync(s => s with { Ui = s.Ui with { Locale = Locales.Find(locale)?.Id ?? "" } }).ConfigureAwait(true);
        ApplyLanguage();
    }

    /// <summary>
    /// Brings the window to the language the settings ask for. Nothing happens when it is the one in
    /// use (the launcher's every change comes through here, as for the theme).
    /// </summary>
    public void ApplyLanguage()
    {
        var locale = Locales.For(ForcedLocale ?? Launcher.Settings.Ui.Locale, SystemCulture);
        if (!S.Switch(locale, SystemFormats))
        {
            return;
        }
        UseLanguage(S);
        // pages hold texts made when they were built (categories, rows, hints): the same pages again, in the new language
        Nav.Rebuild();
        RaiseAll();
        FriendsPanel.OnStateChanged();
        _drawer?.OnStateChanged();
        RefreshHints();
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Everything outside the catalog that follows the language: the culture of this and every later
    /// thread (dates, times, numbers), the core's texts, and the order of the font chain.
    /// </summary>
    public static void UseLanguage(Strings strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        var culture = strings.Culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CoreStrings.Use(strings.Find, culture);
        Fonts.Apply(strings.Locale);
    }
}
