// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mkwmp.App.Controls;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>A button of a modal.</summary>
public sealed class ModalAction : ObservableObject
{
    private bool _isEnabled = true;

    public ModalAction(string label, Action invoke, ButtonKind kind = ButtonKind.Outline, bool isDefault = false)
    {
        Label = label;
        Kind = kind;
        IsDefault = isDefault;
        Command = new RelayCommand(invoke, () => IsEnabled);
    }

    public string Label { get; }

    public ButtonKind Kind { get; }

    /// <summary>Gets the focus when the modal opens (else the bottom button does).</summary>
    public bool IsDefault { get; }

    public ICommand Command { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }
}

/// <summary>
/// A card over a dimmed page: title, text, optional progress (bar, percentage, a line under it) or
/// spinner, optional custom <see cref="Content"/> (a view model with a DataTemplate in ViewMap),
/// centred <see cref="Actions"/> and one full-width <see cref="Bottom"/> button. Show it with
/// <c>Shell.ShowModal</c>; the ready-made ones are <c>Shell.ConfirmAsync / MessageAsync / ShowProgress</c>.
/// </summary>
public class ModalViewModel : LayerViewModel
{
    private string _title = "";
    private string _body = "";
    private object? _content;
    private bool _showProgress;
    private bool _showSpinner;
    private double _progress;
    private string _detail = "";
    private ModalAction? _bottom;
    private bool _canDismiss = true;

    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    public string Body
    {
        get => _body;
        set => Set(ref _body, value);
    }

    /// <summary>A picture beside the title (a pack URI of Assets/Art, such as the crash screen's); null: none.</summary>
    public string? Art { get; init; }

    public bool HasArt => Art is not null;

    public object? Content
    {
        get => _content;
        set => Set(ref _content, value);
    }

    public bool ShowProgress
    {
        get => _showProgress;
        set => Set(ref _showProgress, value);
    }

    /// <summary>A spinner beside the bar (or alone, without <see cref="ShowProgress"/>).</summary>
    public bool ShowSpinner
    {
        get => _showSpinner;
        set => Set(ref _showSpinner, value);
    }

    /// <summary>0..1.</summary>
    public double Progress
    {
        get => _progress;
        set => Set(ref _progress, value);
    }

    /// <summary>The line under the bar ("13 minutes remaining").</summary>
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public ObservableCollection<ModalAction> Actions { get; } = [];

    public ModalAction? Bottom
    {
        get => _bottom;
        set => Set(ref _bottom, value);
    }

    /// <summary>B / Esc / a tap outside closes it (as <see cref="Dismiss"/>).</summary>
    public bool CanDismiss
    {
        get => _canDismiss;
        set => Set(ref _canDismiss, value);
    }

    /// <summary>Raised once when the modal is closed without one of its buttons deciding.</summary>
    public event EventHandler? Dismissed;

    internal void Dismiss() => Dismissed?.Invoke(this, EventArgs.Empty);

    public override void OnStateChanged()
    {
        // a plain modal shows no launcher state
    }
}

/// <summary>The handle of <c>Shell.ShowProgress</c>: report into it, then <see cref="Close"/>.</summary>
public sealed class ProgressModal : ModalViewModel
{
    private readonly ShellViewModel _shell;

    internal ProgressModal(ShellViewModel shell, string title, string body)
    {
        _shell = shell;
        Title = title;
        Body = body;
        ShowSpinner = true;
        CanDismiss = false;
    }

    /// <summary>Shows the bar at <paramref name="fraction"/> (0..1) with a line under it.</summary>
    public void Report(double fraction, string? detail = null)
    {
        ShowProgress = true;
        Progress = fraction;
        Detail = detail ?? "";
    }

    public void Close() => _shell.CloseModal(this);
}

/// <summary>One toast of the stack at the top right.</summary>
public sealed class ToastViewModel : ObservableObject
{
    private bool _leaving;

    public ToastViewModel(string title, string body, ToastKind kind)
    {
        Title = title;
        Body = body;
        Kind = kind;
    }

    public string Title { get; }

    public string Body { get; }

    public ToastKind Kind { get; }


    public string IconKey => Kind switch
    {
        ToastKind.FriendPlaying => "Icon.Play",
        ToastKind.FriendRequest => "Icon.UserPlus",
        ToastKind.FriendOnline => "Icon.User",
        _ => "Icon.Info",
    };

    /// <summary>Set shortly before the toast is removed: the view fades it out.</summary>
    public bool Leaving
    {
        get => _leaving;
        set => Set(ref _leaving, value);
    }
}
