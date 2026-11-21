// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Windows.Input;
using Mkwmp.App.Input;

namespace Mkwmp.App.ViewModels;

/// <summary>What a text box accepts; the on-screen keyboard greys out every other key.</summary>
public enum KeyboardCharset
{
    /// <summary>Anything the keyboard has.</summary>
    Any,
    /// <summary>A gamertag: A-Z a-z 0-9 _ - (no spaces).</summary>
    Gamertag,
    /// <summary>Digits only (a PIN).</summary>
    Digits,
}

public enum KeyboardKeyKind { Character, Shift, Backspace, Space, Accept }

/// <summary>One key of the on-screen keyboard.</summary>
public sealed class KeyboardKey : ObservableObject
{
    private string _label;
    private bool _isEnabled = true;
    private bool _isLit;

    internal KeyboardKey(KeyboardKeyKind kind, string lower, string upper, int span, Action<KeyboardKey> press)
    {
        Kind = kind;
        Lower = lower;
        Upper = upper;
        Span = span;
        _label = lower;
        Command = new RelayCommand(() => press(this));
    }

    public KeyboardKeyKind Kind { get; }

    internal string Lower { get; }

    internal string Upper { get; }

    /// <summary>How many columns wide (the grid has ten).</summary>
    public int Span { get; }

    /// <summary>In stage units: 132 a column, 8 between two.</summary>
    public double Width => Span * 132 + (Span - 1) * 8;

    public ICommand Command { get; }

    /// <summary>The character it types now (follows shift), or the word on Space / OK.</summary>
    public string Label
    {
        get => _label;
        internal set => Set(ref _label, value);
    }

    public string IconKey => Kind switch
    {
        KeyboardKeyKind.Shift => "Icon.Shift",
        KeyboardKeyKind.Backspace => "Icon.Backspace",
        _ => "",
    };

    /// <summary>The controller button that does the same from anywhere, shown on the key.</summary>
    public PadButton Shortcut => Kind switch
    {
        KeyboardKeyKind.Shift => PadButton.LB,
        KeyboardKeyKind.Backspace => PadButton.X,
        KeyboardKeyKind.Space => PadButton.Y,
        KeyboardKeyKind.Accept => PadButton.Start,
        _ => PadButton.A,
    };

    public bool HasShortcut => Kind != KeyboardKeyKind.Character;

    /// <summary>The key that has the focus when the keyboard opens.</summary>
    public bool IsDefault { get; internal set; }

    public bool IsEnabled
    {
        get => _isEnabled;
        internal set => Set(ref _isEnabled, value);
    }

    /// <summary>Shift while it is on.</summary>
    public bool IsLit
    {
        get => _isLit;
        internal set => Set(ref _isLit, value);
    }
}

/// <summary>
/// The on-screen keyboard: opened by <see cref="TextEntry"/> for a text box when the player has a
/// controller (or touches the box). It edits its own copy of the text; OK writes it back, B / a tap
/// outside leaves the box as it was. X backspace, Y space, LB shift, Start OK.
/// </summary>
public sealed class KeyboardViewModel : LayerViewModel
{
    private const string Digits = "1234567890";
    private const string DigitsShift = "!?'\"()&+=%";

    private readonly ShellViewModel _shell;
    private readonly Action<string> _accept;
    private readonly bool _masked;
    private readonly KeyboardKey _shiftKey;
    private string _text;
    private int _shift; // 0 off, 1 the next letter, 2 locked

    public KeyboardViewModel(ShellViewModel shell, string title, string text, int maxLength, KeyboardCharset charset, bool masked, Action<string> accept)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _accept = accept ?? throw new ArgumentNullException(nameof(accept));
        Title = title ?? "";
        _text = text ?? "";
        MaxLength = maxLength > 0 ? maxLength : 0;
        Charset = charset;
        _masked = masked;

        KeyboardKey Char(string lower, string upper) => new(KeyboardKeyKind.Character, lower, upper, 1, Press);
        var digits = Digits.Select((c, i) => Char(c.ToString(), DigitsShift[i].ToString())).ToList();
        List<KeyboardKey> Letters(string row) => [.. row.Select(c => Char(c.ToString(), c.ToString().ToUpperInvariant()))];
        var second = Letters("qwertyuiop");
        var third = Letters("asdfghjkl");
        third.Add(Char("-", "-"));
        _shiftKey = new KeyboardKey(KeyboardKeyKind.Shift, "", "", 1, Press);
        List<KeyboardKey> fourth = [_shiftKey, .. Letters("zxcvbnm"), Char("_", "_"), new KeyboardKey(KeyboardKeyKind.Backspace, "", "", 1, Press)];
        List<KeyboardKey> fifth =
        [
            Char(".", ","), Char("/", "\\"), Char(":", ";"), Char("@", "#"),
            new KeyboardKey(KeyboardKeyKind.Space, shell.S["keyboard.space"], shell.S["keyboard.space"], 4, Press),
            new KeyboardKey(KeyboardKeyKind.Accept, shell.S["common.ok"], shell.S["common.ok"], 2, Press),
        ];
        Rows = [digits, second, third, fourth, fifth];
        (charset == KeyboardCharset.Digits ? digits[0] : second[0]).IsDefault = true;
        Refresh();
    }

    public Strings S => _shell.S;

    public ShellViewModel Shell => _shell;

    /// <summary>What is being typed ("Gamertag"), above the text.</summary>
    public string Title { get; }

    /// <summary>0: no limit.</summary>
    public int MaxLength { get; }

    public KeyboardCharset Charset { get; }

    public IReadOnlyList<IReadOnlyList<KeyboardKey>> Rows { get; }

    public string Text
    {
        get => _text;
        private set
        {
            if (Set(ref _text, value))
            {
                Raise(nameof(Display));
                Raise(nameof(Counter));
                Raise(nameof(IsFull));
                Raise(nameof(IsEmpty));
            }
        }
    }

    /// <summary>The text as shown: dots for a password.</summary>
    public string Display => _masked ? new string('●', _text.Length) : _text;

    public bool IsEmpty => _text.Length == 0;

    public bool IsFull => MaxLength > 0 && _text.Length >= MaxLength;

    /// <summary>"5/16", or "" without a limit.</summary>
    public string Counter => MaxLength > 0
        ? string.Create(CultureInfo.InvariantCulture, $"{_text.Length}/{MaxLength}")
        : "";

    /// <summary>The line under the text: what the box accepts.</summary>
    public string Rule => Charset switch
    {
        KeyboardCharset.Gamertag => S["keyboard.rule.gamertag"],
        KeyboardCharset.Digits => S["keyboard.rule.digits"],
        _ => "",
    };

    public override IReadOnlyList<Hint>? Hints
    {
        get
        {
            if (_shell.InputKind == InputKind.Keyboard)
            {
                // a physical keyboard types here (MainWindow.OnKeyboardKey): its own keys, not the pad's shortcuts
                return [new Hint(PadButton.B, S["common.cancel"]), new Hint(PadButton.A, S["common.ok"])];
            }
            var hints = new List<Hint>();
            if (Charset != KeyboardCharset.Digits)
            {
                hints.Add(new Hint(PadButton.LB, S["keyboard.shift"]));
            }
            if (Charset == KeyboardCharset.Any)
            {
                hints.Add(new Hint(PadButton.Y, S["keyboard.space"]));
            }
            hints.Add(new Hint(PadButton.X, S["keyboard.backspace"]));
            hints.Add(new Hint(PadButton.B, S["common.cancel"]));
            hints.Add(new Hint(PadButton.Start, S["common.ok"]));
            return hints;
        }
    }

    public override bool OnButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.X:
                Backspace();
                return true;
            case PadButton.Y:
                Type(" ");
                return true;
            case PadButton.LB:
                Shift();
                return true;
            case PadButton.Start:
                Accept();
                return true;
            default:
                return false; // B: the shell cancels
        }
    }

    public override void OnStateChanged()
    {
        // the keyboard shows no launcher state
    }

    /// <summary>OK: the text goes back to the box.</summary>
    public void Accept()
    {
        _shell.CloseKeyboard(this);
        _accept(_text);
    }

    /// <summary>The box keeps what it had.</summary>
    public void Cancel() => _shell.CloseKeyboard(this);

    /// <summary>
    /// Text typed on a physical keyboard while this one is open (a click on a box opens it under Wine with a pad
    /// connected, as on the Steam Deck with a keyboard plugged in): each character as if its key were pressed, so
    /// the box's rules and limit hold, and any character the keyboard can type (a backslash too).
    /// </summary>
    public void TypeText(string text)
    {
        var elements = StringInfo.GetTextElementEnumerator(text ?? "");
        while (elements.MoveNext())
        {
            Type(elements.GetTextElement());
        }
    }

    /// <summary>Backspace on a physical keyboard.</summary>
    public void Erase() => Backspace();

    private void Press(KeyboardKey key)
    {
        switch (key.Kind)
        {
            case KeyboardKeyKind.Character:
                Type(key.Label);
                break;
            case KeyboardKeyKind.Space:
                Type(" ");
                break;
            case KeyboardKeyKind.Backspace:
                Backspace();
                break;
            case KeyboardKeyKind.Shift:
                Shift();
                break;
            default:
                Accept();
                break;
        }
    }

    private void Type(string character)
    {
        if (IsFull || !Allowed(character))
        {
            return;
        }
        Text = _text + character;
        if (_shift == 1)
        {
            _shift = 0;
            Refresh();
        }
    }

    private void Backspace()
    {
        if (_text.Length > 0)
        {
            // a whole text element: never half of a surrogate pair that came with the box's text
            var elements = StringInfo.ParseCombiningCharacters(_text);
            Text = _text[..elements[^1]];
        }
    }

    private void Shift()
    {
        if (Charset == KeyboardCharset.Digits)
        {
            return;
        }
        _shift = (_shift + 1) % 3;
        Refresh();
    }

    private bool Allowed(string character) => Charset switch
    {
        KeyboardCharset.Gamertag => character.Length == 1 && (char.IsAsciiLetterOrDigit(character[0]) || character[0] is '_' or '-'),
        KeyboardCharset.Digits => character.Length == 1 && char.IsAsciiDigit(character[0]),
        _ => true,
    };

    private void Refresh()
    {
        foreach (var key in Rows.SelectMany(row => row))
        {
            switch (key.Kind)
            {
                case KeyboardKeyKind.Character:
                    key.Label = _shift > 0 ? key.Upper : key.Lower;
                    key.IsEnabled = Allowed(key.Label);
                    break;
                case KeyboardKeyKind.Space:
                    key.IsEnabled = Allowed(" ");
                    break;
                case KeyboardKeyKind.Shift:
                    key.IsEnabled = Charset != KeyboardCharset.Digits;
                    key.IsLit = _shift > 0;
                    break;
            }
        }
        Raise(nameof(IsShiftLocked));
    }

    /// <summary>Shift pressed twice: every letter is a capital until it is pressed again.</summary>
    public bool IsShiftLocked => _shift == 2;
}
