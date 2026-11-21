// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The gamertag and its PIN, as Settings › Profile and the first-run setup both show it (view:
/// Views/Settings/ProfileEntry.xaml). The Rust window's rules: REGISTER asks the server for the
/// name and shows the PIN it gives; a name that is taken asks for its PIN (USE); a PIN belongs to
/// one name; nothing can be changed while playing.
/// </summary>
public sealed class ProfileForm : ObservableObject
{
    private readonly ShellViewModel _shell;
    private string _name;
    private string _pin = "";
    private bool _editing;
    private bool _havePin;
    private bool _pinRevealed;
    private bool _entryRevealed;

    public ProfileForm(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _name = Profile.SignedIn ? "" : Profile.Name;
        RegisterCommand = RelayCommand.Async(RegisterAsync, () => CanRegister);
        UsePinCommand = RelayCommand.Async(UsePinAsync, () => CanUsePin);
        HavePinCommand = new RelayCommand(() => HavePin = !HavePin, () => CanEdit);
        EditCommand = new RelayCommand(() => Editing = true, () => CanEdit);
        CancelEditCommand = new RelayCommand(() => Editing = false);
        DeleteCommand = RelayCommand.Async(DeleteAsync, () => CanDelete);
    }

    /// <summary>
    /// After REGISTER, the PIN is shown in a modal (masked, with its eye) until "I wrote it down".
    /// False where the next page does that (the welcome flow's "Your PIN").
    /// </summary>
    public bool RevealAfterRegister { get; init; } = true;

    /// <summary>The saved PIN's eye is on (bound to the PinField; a page offers the same on X).</summary>
    public bool PinRevealed
    {
        get => _pinRevealed;
        set => Set(ref _pinRevealed, value);
    }

    /// <summary>The eye of the PIN being typed.</summary>
    public bool EntryRevealed
    {
        get => _entryRevealed;
        set => Set(ref _entryRevealed, value);
    }

    public Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    private ProfileView Profile => Launcher.Profile;

    /// <summary>A registered gamertag is shown (and not being replaced).</summary>
    public bool SignedIn => Profile.SignedIn && !_editing;

    public bool ShowEntry => !SignedIn;

    /// <summary>"Change gamertag" was chosen: the entry form over a profile that still exists.</summary>
    public bool Editing
    {
        get => _editing && Profile.SignedIn;
        private set
        {
            if (Set(ref _editing, value))
            {
                _name = "";
                _pin = "";
                _havePin = false;
                RaiseAll();
            }
        }
    }

    public string SavedName => Profile.Name;

    /// <summary>For a PinField only: a PIN is never bound to plain text.</summary>
    public string SavedPin => Profile.Pin;

    /// <summary>The server the profile is (or will be) on, without its scheme.</summary>
    public string ServerName => HostOf(Launcher.Settings.Server);

    public static string HostOf(string? server)
    {
        var s = (server ?? "").Trim();
        var at = s.IndexOf("://", StringComparison.Ordinal);
        return (at >= 0 ? s[(at + 3)..] : s).TrimEnd('/');
    }

    /// <summary>The gamertag being typed: no spaces, at most 16.</summary>
    public string Name
    {
        get => _name;
        set
        {
            var clean = new string([.. (value ?? "").Where(c => !char.IsWhiteSpace(c)).Take(16)]);
            if (Set(ref _name, clean))
            {
                _pin = "";
                RaiseAll();
                CommandManager.InvalidateRequerySuggested();
            }
            else if (clean != value)
            {
                Raise(); // the box shows what was kept
            }
        }
    }

    /// <summary>The PIN of a gamertag that is already registered: digits, at most 6.</summary>
    public string Pin
    {
        get => _pin;
        set
        {
            var clean = new string([.. (value ?? "").Where(char.IsAsciiDigit).Take(6)]);
            if (Set(ref _pin, clean))
            {
                CommandManager.InvalidateRequerySuggested();
            }
            else if (clean != value)
            {
                Raise();
            }
        }
    }

    public bool HavePin
    {
        get => _havePin;
        private set
        {
            if (Set(ref _havePin, value))
            {
                RaiseAll();
            }
        }
    }

    private bool NameIsSaved => _name.Length > 0 && string.Equals(_name, Profile.Name, StringComparison.OrdinalIgnoreCase);

    private bool NameValid => Links.ValidName(_name);

    /// <summary>The server said the name is someone's: if it is the player's, its PIN goes here.</summary>
    public bool Taken => NameIsSaved && Profile.Register == RegisterState.Taken && !Profile.SignedIn;

    public bool ShowPinEntry => ShowEntry && (Taken || _havePin);

    public bool ShowHavePin => ShowEntry && !Taken;

    public string HavePinLabel => _havePin ? S["settings.profile.no_pin"] : S["settings.profile.have_pin"];

    public bool Working => Profile.Register == RegisterState.Working;

    public bool Busy => Launcher.Session.Busy;

    /// <summary>Nothing about the profile changes while playing.</summary>
    public bool CanEdit => !Busy && !Working;

    public bool CanRegister => CanEdit && NameValid;

    public bool CanUsePin => CanEdit && NameValid && _pin.Length == 6;

    public bool CanDelete => Profile.Pin.Length == 6 && CanEdit;

    public string RegisterLabel => Working ? "…" : S["settings.profile.register"];

    /// <summary>Exactly one message line, whatever the state (the Rust window's order).</summary>
    public string Message
    {
        get
        {
            // nothing typed yet: what a gamertag is, not the PIN that comes after it
            if (_name.Length == 0 && Profile.Register is not (RegisterState.Deleted or RegisterState.Failed))
            {
                return CoreStrings.NameRule;
            }
            if (SignedIn)
            {
                return Profile.Message ?? CoreStrings.KeepYourPin;
            }
            if (_name.Length > 0 && !NameValid)
            {
                return CoreStrings.NameRule;
            }
            if (NameIsSaved || (_name.Length == 0 && Profile.Register is RegisterState.Deleted or RegisterState.Failed))
            {
                return Profile.Message ?? CoreStrings.RegisterToPlay;
            }
            return CoreStrings.RegisterToPlay;
        }
    }

    public bool MessageIsError
    {
        get
        {
            if (SignedIn)
            {
                return Profile.MessageIsError;
            }
            if (_name.Length > 0 && !NameValid)
            {
                return true;
            }
            return (NameIsSaved || (_name.Length == 0 && Profile.Register is RegisterState.Deleted or RegisterState.Failed)) && Profile.MessageIsError;
        }
    }

    /// <summary>"Stop playing to change your profile." while a session runs.</summary>
    public string BusyNote => Busy ? S["settings.busy"] : "";

    public ICommand RegisterCommand { get; }

    public ICommand UsePinCommand { get; }

    public ICommand HavePinCommand { get; }

    public ICommand EditCommand { get; }

    public ICommand CancelEditCommand { get; }

    public ICommand DeleteCommand { get; }

    /// <summary>Raised after a profile was registered or taken into use (the setup moves on).</summary>
    public event EventHandler? SignedInNow;

    /// <summary>The launcher's state changed.</summary>
    public void Refresh()
    {
        if (!Profile.SignedIn && _name.Length == 0 && Profile.Name.Length > 0 && Profile.Register != RegisterState.Deleted)
        {
            _name = Profile.Name; // a name saved earlier (or by another window) that still needs its PIN
        }
        RaiseAll();
    }

    private async Task RegisterAsync()
    {
        var name = _name;
        await Launcher.RegisterAsync(name).ConfigureAwait(true);
        var profile = Launcher.Profile;
        if (profile.SignedIn && string.Equals(profile.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            _editing = false;
            _havePin = false;
            _pin = "";
            RaiseAll();
            if (RevealAfterRegister)
            {
                await RevealAsync(profile.Name, profile.Pin).ConfigureAwait(true);
            }
            SignedInNow?.Invoke(this, EventArgs.Empty);
            return;
        }
        RaiseAll();
    }

    private async Task UsePinAsync()
    {
        var (name, pin) = (_name, _pin);
        await Launcher.UsePinAsync(name, pin).ConfigureAwait(true);
        _pin = "";
        if (Launcher.Profile.SignedIn)
        {
            _editing = false;
            _havePin = false;
            RaiseAll();
            SignedInNow?.Invoke(this, EventArgs.Empty);
            return;
        }
        RaiseAll();
    }

    /// <summary>The PIN, once and large, right after REGISTER: it is the only key to the gamertag on another device.</summary>
    private Task RevealAsync(string name, string pin)
    {
        var done = new TaskCompletionSource();
        var modal = new PinModal(S, new PinRevealViewModel(name, pin))
        {
            Title = S.Format("settings.profile.pin_title", ("name", name)),
            Body = S["settings.profile.pin_body"],
        };
        modal.Bottom = new ModalAction(S["settings.profile.pin_ok"], () =>
        {
            _shell.CloseModal(modal);
            done.TrySetResult();
        });
        modal.Dismissed += (_, _) => done.TrySetResult();
        _shell.ShowModal(modal);
        return done.Task;
    }

    /// <summary>The Rust window's DELETE YOUR PROFILE FROM SERVER? dialog.</summary>
    private async Task DeleteAsync()
    {
        // The PIN that stops working is on the card: masked, as everywhere.
        var name = Profile.Name;
        var answer = new TaskCompletionSource<bool>();
        var modal = new PinModal(S, new PinRevealViewModel(name, Profile.Pin))
        {
            Title = S["settings.profile.delete_title"],
            Body = S.Format("settings.profile.delete_body", ("name", name), ("server", ServerName)),
        };
        modal.Actions.Add(new ModalAction(S["settings.profile.delete_yes"], () =>
        {
            _shell.CloseModal(modal);
            answer.TrySetResult(true);
        }));
        modal.Bottom = new ModalAction(S["common.cancel"], () =>
        {
            _shell.CloseModal(modal);
            answer.TrySetResult(false);
        });
        modal.Dismissed += (_, _) => answer.TrySetResult(false);
        _shell.ShowModal(modal);
        if (!await answer.Task.ConfigureAwait(true))
        {
            return;
        }
        await Launcher.DeleteProfileAsync().ConfigureAwait(true);
        if (Launcher.Profile.Register == RegisterState.Deleted)
        {
            // Forgotten: an empty gamertag field and REGISTER, ready for a new one.
            _editing = false;
            _name = "";
            _pin = "";
            _havePin = false;
        }
        RaiseAll();
    }
}
