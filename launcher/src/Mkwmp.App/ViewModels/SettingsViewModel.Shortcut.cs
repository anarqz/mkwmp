// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core.Overlay;

namespace Mkwmp.App.ViewModels;

/// <summary>Settings › Game, "In-Game Overlay": the controller shortcut that opens MKWMP over the game (launcher/docs/OVERLAY.md §2).</summary>
public sealed partial class SettingsViewModel
{
    private ICommand? _shortcutCommand;

    public string ShortcutName => ShortcutText.Name(S, Launcher.Settings.Ui.OverlayShortcut);

    /// <summary>What the chosen one may run into (Steam, the Xbox Game Bar, the game's own buttons).</summary>
    public string ShortcutNote => ShortcutText.Note(S, Launcher.Settings.Ui.OverlayShortcut);

    public ICommand ShortcutCommand => _shortcutCommand ??= RelayCommand.Async(PickShortcutAsync);

    private async Task PickShortcutAsync()
    {
        var current = Launcher.Settings.Ui.OverlayShortcut;
        var picked = await Choice.AskAsync(Shell, S["settings.game.shortcut"], S["settings.game.shortcut_warning"],
            [.. OverlayShortcuts.All.Select(s => new ChoiceOption(s.WireName(), ShortcutText.Name(S, s), ShortcutText.Note(S, s)))],
            current.WireName()).ConfigureAwait(true);
        if (picked is not null)
        {
            var shortcut = OverlayShortcuts.Parse(picked);
            await Shell.UpdateSettingsAsync(s => s with { Ui = s.Ui with { OverlayShortcut = shortcut } }).ConfigureAwait(true);
        }
    }
}

/// <summary>The overlay shortcut's name and note in the player's language (<c>overlay.shortcut.*</c>).</summary>
public static class ShortcutText
{
    public static string Name(Strings s, OverlayShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s["overlay.shortcut." + shortcut.WireName()];
    }

    public static string Note(Strings s, OverlayShortcut shortcut)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s["overlay.shortcut." + shortcut.WireName() + ".note"];
    }
}
