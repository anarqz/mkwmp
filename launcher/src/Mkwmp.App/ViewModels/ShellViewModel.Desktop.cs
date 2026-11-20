// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.App.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// What home, the friends panel and the desktop integration ask the shell for: a settings
/// category, the friends panel on one friend, the on-screen keyboard (the layer above all the
/// others), the tray.
/// </summary>
public sealed partial class ShellViewModel
{
    private KeyboardViewModel? _keyboard;
    private bool _trayAvailable;

    // ── shortcuts into pages ──

    /// <summary>Settings, on one category (<c>profile</c>, <c>game</c>, <c>chat</c>, <c>notifications</c>, <c>appearance</c>, <c>updates</c>, <c>about</c>).</summary>
    public void OpenSettings(string? category = null)
    {
        if (Page is not SettingsViewModel)
        {
            Navigate(Routes.Settings);
        }
        if (category is not null && Page is SettingsViewModel settings
            && settings.Categories.FirstOrDefault(c => string.Equals(c.Key, category, StringComparison.OrdinalIgnoreCase)) is { } entry)
        {
            settings.Category = entry;
        }
    }

    /// <summary>The friends panel, with the focus on that friend (or request) when the name is in the list.</summary>
    public void OpenFriends(string? focusName = null)
    {
        FriendsPanel.FocusOn(focusName);
        IsFriendsOpen = true;
    }

    // ── the on-screen keyboard ──

    /// <summary>The open on-screen keyboard: the top layer (above a modal), it takes every button.</summary>
    public KeyboardViewModel? Keyboard
    {
        get => _keyboard;
        private set
        {
            var old = _keyboard;
            if (Set(ref _keyboard, value))
            {
                old?.OnClosed();
                value?.OnOpened();
                Raise(nameof(IsKeyboardOpen));
                RefreshHints();
            }
        }
    }

    public bool IsKeyboardOpen => _keyboard is not null;

    /// <summary>Opens the on-screen keyboard (see <see cref="TextEntry"/>: text boxes open it by themselves).</summary>
    public void OpenKeyboard(KeyboardViewModel keyboard) => Keyboard = keyboard ?? throw new ArgumentNullException(nameof(keyboard));

    /// <summary>Closes it; with an argument, only if that one is still the open one.</summary>
    public void CloseKeyboard(KeyboardViewModel? keyboard = null)
    {
        if (keyboard is null || ReferenceEquals(keyboard, _keyboard))
        {
            Keyboard = null;
        }
    }

    /// <summary>True while the keyboard is open: it used the button (B closes it without typing anything).</summary>
    private bool HandleKeyboardButton(PadButton button)
    {
        if (_keyboard is not { } keyboard)
        {
            return false;
        }
        if (!keyboard.OnButton(button) && button == PadButton.B)
        {
            keyboard.Cancel();
        }
        return true;
    }

    // ── desktop: tray, quitting, system notifications ──

    /// <summary>There is a tray icon to hide into (never in the Steam Deck's Game Mode, nor in the harness).</summary>
    public bool TrayAvailable
    {
        get => _trayAvailable;
        set => Set(ref _trayAvailable, value);
    }

    /// <summary>Set by <see cref="Quit"/>: the window really closes now, whatever "close to tray" says.</summary>
    public bool IsQuitting { get; private set; }

    /// <summary>The window hides into the tray on this (the desktop integration listens).</summary>
    public event EventHandler? HideRequested;

    public void HideToTray() => HideRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Sees every toast of the launcher first (UI thread); true when the system showed it and the
    /// in-app toast is not wanted (the window is hidden).
    /// </summary>
    public Func<Toast, bool>? ToastSink { get; set; }
}
