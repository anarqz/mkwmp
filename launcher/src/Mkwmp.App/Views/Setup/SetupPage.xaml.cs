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

    private bool _answered;

    /// <summary>A new page of the flow: the focus goes to what that page is about (its button; its box while the button cannot be pressed yet).</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // the server answered (a row was chosen, or an address typed): the button is now Next, and the focus goes to it
        var answered = Flow is { Step: SetupStep.Server, ProbeOk: true };
        if (answered && !_answered && IsVisible)
        {
            // once the button is enabled again (commands are asked again at a lower priority than this event)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => FocusNav.Focus(ServerPrimary));
            });
        }
        _answered = answered;
        if (e.PropertyName == nameof(SetupViewModel.Step))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (IsVisible && Flow is { } flow)
                {
                    // the consent's box is what the page asks for; the nearest server on Choose a Server (the address box
                    // while there is no list yet); elsewhere the button, or the first stop while it is disabled
                    var placed = flow.Step switch
                    {
                        SetupStep.Consent => FocusNav.Focus(AdultBox),
                        SetupStep.Server when !flow.ProbeOk => ServerRows.FocusFirst() || FocusNav.Focus(AddressBox),
                        _ => false,
                    };
                    if (!placed)
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
