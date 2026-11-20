// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.App.Input;

namespace Mkwmp.App.ViewModels;

/// <summary>A PIN inside a modal (its view: Views/Settings/PinReveal.xaml): masked until its eye, or X, shows it.</summary>
public sealed class PinRevealViewModel : ObservableObject
{
    private bool _isRevealed;

    public PinRevealViewModel(string name, string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        Name = name;
        Pin = pin;
    }

    public string Name { get; }

    /// <summary>The digits, for a PinField only.</summary>
    public string Pin { get; }

    public bool IsRevealed
    {
        get => _isRevealed;
        set => Set(ref _isRevealed, value);
    }
}

/// <summary>A modal that shows a PIN: X shows and hides it (and the hint bar says so), as the eye button does.</summary>
public sealed class PinModal : ModalViewModel
{
    private readonly Strings _s;
    private readonly PinRevealViewModel _pin;

    public PinModal(Strings strings, PinRevealViewModel pin)
    {
        _s = strings ?? throw new ArgumentNullException(nameof(strings));
        _pin = pin ?? throw new ArgumentNullException(nameof(pin));
        Content = pin;
        pin.PropertyChanged += (_, _) => Raise(nameof(Hints));
    }

    public override IReadOnlyList<Hint>? Hints =>
    [
        PinHint.For(_s, _pin.IsRevealed),
        .. CanDismiss ? (Hint[])[new Hint(PadButton.B, _s["hint.back"])] : [],
        new Hint(PadButton.A, _s["hint.ok"]),
    ];

    public override bool OnButton(PadButton button)
    {
        if (button != PadButton.X)
        {
            return false;
        }
        _pin.IsRevealed = !_pin.IsRevealed;
        return true;
    }

    public override void OnClosed() => _pin.IsRevealed = false;
}

/// <summary>The hint of the button that shows or hides a PIN (X), wherever one is on screen.</summary>
public static class PinHint
{
    public static Hint For(Strings s, bool revealed)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Hint(PadButton.X, revealed ? s["pin.hide"] : s["pin.show"]);
    }
}
