// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>An icon of Themes/Icons.xaml with its key.</summary>
public sealed record GalleryIcon(string Key, Geometry Data, bool Filled);

/// <summary>
/// The design system on one page (<c>--page gallery</c>): every
/// icon, and one of each control. For building pages, not for players.
/// </summary>
public sealed class GalleryViewModel : PageViewModel
{
    private bool _toggle = true;
    private bool _check;
    private double _slider = 60;

    public GalleryViewModel(ShellViewModel shell)
        : base(shell)
    {
        var icons = new List<GalleryIcon>();
        foreach (var dictionary in Application.Current.Resources.MergedDictionaries)
        {
            foreach (var key in dictionary.Keys.OfType<string>().Where(k => k.StartsWith("Icon", StringComparison.Ordinal)))
            {
                if (dictionary[key] is Geometry data)
                {
                    icons.Add(new GalleryIcon(key, data, key.StartsWith("IconFill", StringComparison.Ordinal)));
                }
            }
        }
        Icons = [.. icons.OrderBy(i => i.Key, StringComparer.Ordinal)];
        ConfirmCommand = RelayCommand.Async(async () =>
        {
            var yes = await Shell.ConfirmAsync("Delete the profile?", "This removes your gamertag, your friends and your PIN from the server. It cannot be undone.", "Delete").ConfigureAwait(true);
            Shell.ShowToast(yes ? "Confirmed" : "Cancelled");
        });
        MessageCommand = RelayCommand.Async(() => Shell.MessageAsync("A message", "Something the player has to read before going on."));
        ProgressCommand = RelayCommand.Async(async () =>
        {
            var modal = Shell.ShowProgress("Installing…", "Copying the integration into the game folder.");
            for (var i = 0; i <= 20; i++)
            {
                modal.Report(i / 20.0, $"{20 - i} files left");
                await Task.Delay(120).ConfigureAwait(true);
            }
            modal.Close();
        });
        ToastCommand = new RelayCommand(() => Shell.ShowToast("Luna", "is racing online now", ToastKind.FriendPlaying));
        ThemeCommand = RelayCommand.Async(() => Shell.SetThemeAsync(Shell.IsDark ? "light" : "dark"));
    }

    public IReadOnlyList<GalleryIcon> Icons { get; }

    public bool Toggle
    {
        get => _toggle;
        set => Set(ref _toggle, value);
    }

    public bool Check
    {
        get => _check;
        set => Set(ref _check, value);
    }

    public double Slider
    {
        get => _slider;
        set => Set(ref _slider, value);
    }

    public ICommand ConfirmCommand { get; }

    public ICommand MessageCommand { get; }

    public ICommand ProgressCommand { get; }

    public ICommand ToastCommand { get; }

    public ICommand ThemeCommand { get; }

    public override void OnStateChanged()
    {
        // nothing here shows launcher state
    }
}
