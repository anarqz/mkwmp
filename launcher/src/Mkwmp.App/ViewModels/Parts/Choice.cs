// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;

namespace Mkwmp.App.ViewModels;

/// <summary>One line of a selection list.</summary>
public sealed record ChoiceOption(string Id, string Label, string Caption = "");

/// <summary>A line of <see cref="ChoiceListViewModel"/> as the view binds it.</summary>
public sealed class ChoiceItem
{
    public ChoiceItem(ChoiceOption option, bool isSelected, Action<string> pick)
    {
        ArgumentNullException.ThrowIfNull(option);
        Label = option.Label;
        Caption = option.Caption;
        IsSelected = isSelected;
        Command = new RelayCommand(() => pick(option.Id));
    }

    public string Label { get; }

    public string Caption { get; }

    public bool IsSelected { get; }

    public ICommand Command { get; }
}

/// <summary>The content of a selection modal (its view: Views/Settings/ChoiceList.xaml).</summary>
public sealed class ChoiceListViewModel
{
    public ChoiceListViewModel(IReadOnlyList<ChoiceItem> items) => Items = items;

    public IReadOnlyList<ChoiceItem> Items { get; }
}

/// <summary>The big digits of a PIN inside a modal (its view: Views/Settings/PinReveal.xaml).</summary>
public sealed class PinRevealViewModel
{
    public PinRevealViewModel(string name, string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        Name = name;
        Pin = pin.Length == 6 ? $"{pin[..3]} {pin[3..]}" : pin;
    }

    public string Name { get; }

    /// <summary>"482 913".</summary>
    public string Pin { get; }
}

/// <summary>A controller-friendly selection list in a modal: every option is a row, the current one is ticked and has the focus.</summary>
public static class Choice
{
    /// <summary>The id of the picked option, or null when the modal was closed without picking.</summary>
    public static Task<string?> AskAsync(ShellViewModel shell, string title, string body, IReadOnlyList<ChoiceOption> options, string? selected)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(options);
        var answer = new TaskCompletionSource<string?>();
        var modal = new ModalViewModel { Title = title, Body = body };
        modal.Content = new ChoiceListViewModel([.. options.Select(o => new ChoiceItem(o, o.Id == selected, id =>
        {
            shell.CloseModal(modal);
            answer.TrySetResult(id);
        }))]);
        modal.Bottom = new ModalAction(shell.S["common.cancel"], () =>
        {
            shell.CloseModal(modal);
            answer.TrySetResult(null);
        });
        modal.Dismissed += (_, _) => answer.TrySetResult(null);
        shell.ShowModal(modal);
        return answer.Task;
    }
}
