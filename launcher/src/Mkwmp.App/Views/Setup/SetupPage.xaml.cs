// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Mkwmp.App.Input;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views.Setup;

public partial class SetupPage : UserControl
{
    public SetupPage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old)
            {
                old.PropertyChanged -= OnViewModelChanged;
            }
            if (e.NewValue is INotifyPropertyChanged now)
            {
                now.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private SetupViewModel? Flow => DataContext as SetupViewModel;

    /// <summary>A new page of the flow: the focus goes to what that page is about (its button; its box while the button cannot be pressed yet).</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SetupViewModel.Step))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsVisible && Flow is { } flow)
                {
                    // the consent's box is what the page asks for; elsewhere the button, or the first stop while it is disabled
                    if (flow.Step != SetupStep.Consent || !FocusNav.Focus(AdultBox))
                    {
                        FocusNav.FocusDefault(this);
                    }
                }
            });
        }
    }

    /// <summary>Enter in a box (or OK on the on-screen keyboard) is the page's button.</summary>
    private void OnSubmitted(object sender, RoutedEventArgs e)
    {
        if (Flow is { CanPrimary: true } flow)
        {
            flow.PrimaryCommand.Execute(null);
        }
    }

    private void OnAddressSubmitted(object sender, RoutedEventArgs e) => Flow?.SubmitAddress();

    private void OnDumpSubmitted(object sender, RoutedEventArgs e) => Flow?.SubmitDump();
}
