// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.App.Controls;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings: categories at the left, the chosen one's rows at the right. STUB of the design
/// system: Appearance and About are real, the other panes are placeholders. The settings agent
/// owns this file and everything under Views/Settings/.
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private CategoryEntry _category;

    public SettingsViewModel(ShellViewModel shell)
        : base(shell)
    {
        Categories =
        [
            new CategoryEntry("profile", S["settings.profile"]),
            new CategoryEntry("game", S["settings.game"]),
            new CategoryEntry("chat", S["settings.chat"]),
            new CategoryEntry("notifications", S["settings.notifications"]),
            new CategoryEntry("appearance", S["settings.appearance"], SeparatorBefore: true),
            new CategoryEntry("updates", S["settings.updates"]),
            new CategoryEntry("about", S["settings.about"]),
        ];
        _category = Categories[4];
        ThemeCommand = RelayCommand.Async(() => Shell.SetThemeAsync(Shell.IsDark ? "light" : "dark"));
        UpdateCommand = new RelayCommand(Shell.ShowUpdateModal);
        TestToastCommand = new RelayCommand(Launcher.ShowTestToast);
    }

    public IReadOnlyList<CategoryEntry> Categories { get; }

    public CategoryEntry Category
    {
        get => _category;
        set
        {
            if (value is not null && Set(ref _category, value))
            {
                Raise(nameof(CategoryKey));
            }
        }
    }

    /// <summary>The pane to show.</summary>
    public string CategoryKey => _category.Key;

    public string ThemeName => Shell.IsDark ? S["settings.theme_dark"] : S["settings.theme_light"];

    public bool Fullscreen
    {
        get => Shell.IsFullscreen;
        set => RelayCommand.Async(() => Shell.SetFullscreenAsync(value)).Execute(null);
    }

    public bool NotifyOnline
    {
        get => Launcher.Settings.Background.NotifyOnline;
        set => RelayCommand.Async(() => Shell.UpdateSettingsAsync(s => s with { Background = s.Background with { NotifyOnline = value } })).Execute(null);
    }

    public int OutputGain
    {
        get => Launcher.Settings.Voice.OutputGain;
        set => RelayCommand.Async(() => Shell.UpdateSettingsAsync(s => s with { Voice = s.Voice with { OutputGain = value } })).Execute(null);
    }

    public string GameSummary => Launcher.Game.Install is { } install ? $"{install.TargetId} {install.Version}" : S["home.no_game"];

    public string IntegrationSummary => Launcher.Game.Integration?.Summary ?? "";

    public string UpdateSummary => Launcher.Update.Status;

    public ICommand ThemeCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand TestToastCommand { get; }
}
