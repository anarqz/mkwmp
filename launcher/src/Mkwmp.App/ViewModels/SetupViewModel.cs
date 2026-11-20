// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The pages of the welcome flow, in their order. The four before Done are the online part ("Go
/// Online", or Settings › Online's Connect): a server, signing in on it, then the consent (which
/// that server keeps, so it can only come after signing in), then the PIN.
/// </summary>
public enum SetupStep { Welcome, Name, Game, Online, Server, Register, Consent, Pin, Done }

/// <summary>
/// The first-start welcome, a paged flow (shown while <c>Ui.SetupDone</c> is false; it resumes at
/// the first step that is still open):
/// Welcome → Your Name (local) → Your Game (download mkw-recomp, the player's own game, the setup,
/// MKWMP's files: SetupViewModel.Game.cs) → "Want to go online?". "Not Now" ends it: home, with
/// everything online locked behind "Configure Online". "Go Online" continues with the server (from
/// the list, or a typed address), signing in on it (registering the name, or recovering a profile
/// with its PIN), the consent to that server's privacy policy, the PIN (masked), then Done.
/// <para>
/// The online part alone is also Settings › Online's "Connect to a Server"
/// (<see cref="Connect"/>): the same pages, pushed over Settings, ending back there.
/// </para>
/// Offline is a complete state: nothing here contacts a server before "Go Online", and the game
/// steps can wait ("Later": PLAY then says what is missing).
/// </summary>
public sealed partial class SetupViewModel : PageViewModel, IDisposable
{
    private readonly bool _connectOnly;
    private SetupStep _step;
    private bool _goOnline;
    private string _name;
    private bool _adult;
    private string _address;
    private ServerProbe? _probe;
    private bool _probing;
    private bool _pinRevealed;

    public SetupViewModel(ShellViewModel shell)
        : this(shell, connectOnly: false)
    {
    }

    private SetupViewModel(ShellViewModel shell, bool connectOnly, SetupStep? start = null, string? address = null)
        : base(shell)
    {
        _connectOnly = connectOnly;
        Servers = new ServerPicker(shell, PickServer);
        var s = Launcher.Settings;
        _name = s.Name;
        _adult = s.AdultConsent;
        _address = Launcher.OnlineEnabled ? ProfileForm.HostOf(s.Server)
            : Launcher.SavedServers.Count > 0 ? ProfileForm.HostOf(Launcher.SavedServers[^1].Address) : "";
        _goOnline = connectOnly || Launcher.OnlineEnabled;
        Profile = new ProfileForm(shell) { RevealAfterRegister = false };
        Profile.SignedInNow += (_, _) =>
        {
            if (_step == SetupStep.Register)
            {
                Step = AfterSignIn;
            }
        };
        Game = new GameForm(shell);
        Game.Refresh();
        PrimaryCommand = RelayCommand.Async(PrimaryAsync, () => CanPrimary);
        SecondaryCommand = RelayCommand.Async(SecondaryAsync, () => ShowSecondary);
        PolicyCommand = new RelayCommand(() => Links.Open(Shell, CoreStrings.PrivacyUrl));
        PrivacyCommand = new RelayCommand(() => Shell.Navigate(new PrivacyViewModel(Shell)));
        InitGame();
        _step = start ?? (connectOnly ? SetupStep.Server : FirstOpenStep());
        if (address is { Length: > 0 })
        {
            _address = ProfileForm.HostOf(address);
            _pickedAddress = address;
            SubmitAddress(); // a server chosen from the list: asked at once
        }
    }

    /// <summary>
    /// Settings › Online's "Connect to a Server": only the online pages (server, sign in, consent,
    /// PIN), then back. <paramref name="address"/>: a server chosen from the list, asked at once.
    /// </summary>
    public static SetupViewModel Connect(ShellViewModel shell, string? address = null) => new(shell, connectOnly: true, address: address);

    /// <summary>Settings › Online's "Review and Accept": the consent page alone, for a profile that is signed in without it.</summary>
    public static SetupViewModel ConsentOnly(ShellViewModel shell) => new(shell, connectOnly: true, start: SetupStep.Consent);

    /// <summary>The servers to choose from (the beholder's list), on the server page.</summary>
    public ServerPicker Servers { get; }

    /// <summary>The privacy policy, rendered in the launcher (a page over this one).</summary>
    public ICommand PrivacyCommand { get; }

    public bool IsFirstRun => !_connectOnly;

    public void Dispose()
    {
        _runtimeCancel?.Dispose();
        _setupCancel?.Dispose();
    }

    /// <summary>Where a welcome that was left half-way goes on.</summary>
    private SetupStep FirstOpenStep()
    {
        var s = Launcher.Settings;
        if (!Links.ValidName(s.Name))
        {
            return s.Name.Length == 0 ? SetupStep.Welcome : SetupStep.Name;
        }
        if (Stage != GameStage.Ready)
        {
            return SetupStep.Game;
        }
        if (!Launcher.OnlineEnabled)
        {
            return SetupStep.Online;
        }
        return !Launcher.Profile.SignedIn ? SetupStep.Register : !s.AdultConsent ? SetupStep.Consent : SetupStep.Done;
    }

    /// <summary>The pages of this run, in order (the dots).</summary>
    private IReadOnlyList<SetupStep> Path => _connectOnly
        ? [SetupStep.Server, SetupStep.Register, SetupStep.Consent, SetupStep.Pin]
        : _goOnline
            ? [SetupStep.Welcome, SetupStep.Name, SetupStep.Game, SetupStep.Online, SetupStep.Server, SetupStep.Register, SetupStep.Consent, SetupStep.Pin, SetupStep.Done]
            : [SetupStep.Welcome, SetupStep.Name, SetupStep.Game, SetupStep.Online, SetupStep.Done];

    public int StepCount => Path.Count;

    public int StepIndex => Math.Max(0, Path.ToList().IndexOf(_step));

    public SetupStep Step
    {
        get => _step;
        private set
        {
            if (!Set(ref _step, value))
            {
                return;
            }
            PinRevealed = false; // a PIN is dots again whenever its page is left
            Profile.EntryRevealed = false;
            EnterStep();
            RaiseAll();
            RefreshHints();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>"welcome", "name", "game"…: which page's content shows.</summary>
    public string StepName => _step.ToString().ToLowerInvariant();

    public string Title => _step switch
    {
        SetupStep.Welcome => S["setup.welcome.title"],
        SetupStep.Name => S["setup.name.title"],
        SetupStep.Game => S["setup.game.title"],
        SetupStep.Online => S["setup.online.title"],
        SetupStep.Consent => S["setup.consent.title"],
        SetupStep.Server => S["setup.server.title"],
        SetupStep.Register => S["setup.register.title"],
        SetupStep.Pin => S["setup.pin.title"],
        _ => S["setup.done.title"],
    };

    public ProfileForm Profile { get; }

    public GameForm Game { get; }

    public ICommand PrimaryCommand { get; }

    public ICommand SecondaryCommand { get; }

    public ICommand PolicyCommand { get; }

    // ── your name (local: nothing is asked of a server) ──

    /// <summary>The name being typed: no spaces, at most 16.</summary>
    public string NameText
    {
        get => _name;
        set
        {
            var clean = new string([.. (value ?? "").Where(c => !char.IsWhiteSpace(c)).Take(16)]);
            if (Set(ref _name, clean))
            {
                Raise(nameof(NameNote));
                Raise(nameof(NameIsError));
                Raise(nameof(CanPrimary));
                CommandManager.InvalidateRequerySuggested();
            }
            else if (clean != value)
            {
                Raise();
            }
        }
    }

    public bool NameIsError => _name.Length > 0 && !Links.ValidName(_name);

    public string NameNote => NameIsError ? CoreStrings.NameRule : S["setup.name.rule"];

    // ── the consent (nothing reaches a server before it) ──

    /// <summary>"I'm 18 or older and I accept the privacy policy".</summary>
    public bool Adult
    {
        get => _adult;
        set
        {
            if (Set(ref _adult, value))
            {
                Raise(nameof(CanPrimary));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    // ── the server ──

    /// <summary>A row of the list was chosen: its address goes into the box and the server is asked.</summary>
    private void PickServer(ListedServer server)
    {
        _address = ProfileForm.HostOf(server.Address);
        _pickedAddress = server.Address; // with its scheme: a bare host would be asked over http
        _probe = null;
        if (_step != SetupStep.Server)
        {
            Step = SetupStep.Server;
        }
        RaiseAll();
        SubmitAddress();
    }

    /// <summary>The whole address of the server chosen from the list, while the box still shows it.</summary>
    private string? _pickedAddress;

    private string AddressToAsk => _pickedAddress is { } picked && ProfileForm.HostOf(picked) == _address ? picked : _address;

    public string Address
    {
        get => _address;
        set
        {
            if (Set(ref _address, (value ?? "").Trim()))
            {
                _probe = null; // the answer was about another address
                RaiseAll();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool Probing => _probing;

    /// <summary>The server answered and is one of ours: Next saves it.</summary>
    public bool ProbeOk => _probe is { Ok: true };

    public bool HasProbe => _probe is not null && !_probing;

    public string ProbeTone => _probing ? "busy" : _probe is null ? "muted" : _probe.Ok ? "good" : "bad";

    public string ProbeText => _probing ? S["setup.server.asking"]
        : _probe is null ? ""
        : _probe.Ok ? CoreStrings.ProbeOk(_probe)
        : _probe.Message ?? CoreStrings.ProbeUnreachable;

    public string ProbeDetail => _probe is { Ok: true } ok
        ? string.Join(" · ", new[] { ok.Name, ProfileForm.HostOf(ok.Address) }.Where(t => !string.IsNullOrWhiteSpace(t)))
        : "";

    public IReadOnlyList<StatusLine> ProbeLines => _probing || _probe is not null ? [new StatusLine(ProbeTone, ProbeText, ProbeDetail)] : [];

    private async Task ProbeAsync()
    {
        if (_probing || _address.Length == 0)
        {
            return;
        }
        var asked = _address;
        _probing = true;
        _probe = null;
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
        var probe = await Launcher.ProbeServerAsync(AddressToAsk).ConfigureAwait(true);
        _probing = false;
        if (asked == _address)
        {
            _probe = probe;
        }
        RaiseAll();
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Connect from the address box (Enter, or OK on the on-screen keyboard).</summary>
    public void SubmitAddress() => RelayCommand.Async(ProbeAsync).Execute(null);

    // ── register, and the PIN ──

    public string ServerShort => ProfileForm.HostOf(Launcher.Settings.Server);

    public string RegisterText => S.Format("setup.register.text", ("server", ServerShort));

    public string SavedPin => Launcher.Profile.Pin;

    public string SavedName => Launcher.Profile.Name;

    /// <summary>The PIN page's eye (and X).</summary>
    public bool PinRevealed
    {
        get => _pinRevealed;
        set
        {
            if (Set(ref _pinRevealed, value))
            {
                RefreshHints();
            }
        }
    }

    public string PinText => S.Format("setup.pin.text", ("name", SavedName), ("server", ServerShort));

    /// <summary>The consent page's lead: whose policy is being agreed to.</summary>
    public string ConsentText => S.Format("setup.consent.text", ("server", ServerShort));

    /// <summary>Why the consent was not stored (the server could not be reached), under the box.</summary>
    public string ConsentNote => _step == SetupStep.Consent && Launcher.Profile is { Register: RegisterState.Failed, Message: { } why } ? why : "";

    /// <summary>After signing in: the consent when the server holds none for this profile, else straight to the PIN.</summary>
    private SetupStep AfterSignIn => Launcher.Settings.AdultConsent ? SetupStep.Pin : SetupStep.Consent;

    // ── done ──

    /// <summary>What is ready and what waits, with lights.</summary>
    public IReadOnlyList<StatusLine> Summary
    {
        get
        {
            var s = Launcher.Settings;
            var lines = new List<StatusLine>
            {
                Links.ValidName(s.Name) ? new("good", S.Format("setup.done.name", ("name", s.Name))) : new("warn", S["setup.done.no_name"]),
            };
            if (Stage == GameStage.Ready && Launcher.Game.Install is { } install)
            {
                lines.Add(new StatusLine("good", S.Format("setup.done.game", ("name", Game.TargetName), ("version", install.Version ?? S["settings.game.unknown"]))));
            }
            else
            {
                lines.Add(new StatusLine("warn", S["setup.done.no_game"], StageProblem));
            }
            lines.Add(!Launcher.OnlineEnabled ? new StatusLine("muted", S["setup.done.offline"], S["setup.done.offline_detail"])
                : Launcher.Profile.SignedIn ? new StatusLine("good", S.Format("setup.done.online", ("name", s.Name), ("server", ServerShort)))
                : new StatusLine("warn", S.Format("setup.done.online_no_profile", ("server", ServerShort)), S["setup.done.online_no_profile_detail"]));
            return lines;
        }
    }

    public string DoneText => Stage != GameStage.Ready ? S["setup.done.almost"]
        : Launcher.OnlineEnabled && Launcher.Profile.SignedIn ? S["setup.done.ready"]
        : S["setup.done.ready_offline"];

    // ── the two buttons under every page ──

    public string PrimaryLabel => _step switch
    {
        SetupStep.Game => GamePrimaryLabel,
        SetupStep.Online => S["setup.online.go"],
        SetupStep.Server => ProbeOk ? S["common.next"] : S["setup.server.connect"],
        SetupStep.Register when !Launcher.Profile.SignedIn => Profile.Working ? "…"
            : Profile.ShowPinEntry ? S["setup.register.mine"] // the name is someone's: with its PIN, it is the player's
            : S.Format("setup.register.go", ("name", Profile.Name)),
        SetupStep.Pin => S["settings.profile.pin_ok"],
        SetupStep.Done => S["setup.done.start"],
        _ => S["common.next"],
    };

    public bool CanPrimary => _step switch
    {
        SetupStep.Name => Links.ValidName(_name),
        SetupStep.Game => GameCanPrimary,
        SetupStep.Consent => _adult && Launcher.Profile.Register != RegisterState.Working,
        SetupStep.Server => !_probing && _address.Length > 0 && !Launcher.Session.Busy,
        SetupStep.Register => Launcher.Profile.SignedIn ? !Profile.Editing : Profile.ShowPinEntry ? Profile.CanUsePin : Profile.CanRegister,
        _ => true,
    };

    public string SecondaryLabel => _step == SetupStep.Online ? S["setup.online.not_now"] : S["setup.later"];

    /// <summary>"Not Now" on the online question; "Later" where a step can wait (the game; a registration the server did not answer).</summary>
    public bool ShowSecondary => _step switch
    {
        SetupStep.Online => true,
        SetupStep.Game => GameCanWait,
        SetupStep.Register => IsFirstRun && !Launcher.Profile.SignedIn && !Profile.Working,
        _ => false,
    };

    private async Task PrimaryAsync()
    {
        switch (_step)
        {
            case SetupStep.Name:
                if (_name != Launcher.Settings.Name)
                {
                    await Shell.UpdateSettingsAsync(s => s with { Name = _name }).ConfigureAwait(true);
                }
                Step = SetupStep.Game;
                break;
            case SetupStep.Game:
                await GamePrimaryAsync().ConfigureAwait(true);
                break;
            case SetupStep.Online:
                _goOnline = true;
                Step = SetupStep.Server;
                break;
            case SetupStep.Consent:
                // Stored on the server first (with the version of its policy), then here; it stays on this page when that failed.
                if (!Launcher.Settings.AdultConsent)
                {
                    await Launcher.SetConsentAsync(true).ConfigureAwait(true);
                }
                if (Launcher.Settings.AdultConsent)
                {
                    Step = SetupStep.Pin;
                }
                else
                {
                    RaiseAll();
                }
                break;
            case SetupStep.Server:
                if (_probe is not { Ok: true } probe)
                {
                    await ProbeAsync().ConfigureAwait(true);
                    break;
                }
                // Connect: the address becomes the server. A PIN kept for this name on it comes back by itself.
                await Shell.UpdateSettingsAsync(s => s with { Server = probe.Address }).ConfigureAwait(true);
                Profile.Refresh();
                Step = Launcher.Profile.SignedIn ? AfterSignIn : SetupStep.Register;
                break;
            case SetupStep.Register:
                if (Launcher.Profile.SignedIn)
                {
                    Step = AfterSignIn;
                }
                else if (Profile.ShowPinEntry)
                {
                    Profile.UsePinCommand.Execute(null); // "It's Mine": Profile.SignedInNow moves on
                }
                else
                {
                    Profile.RegisterCommand.Execute(null); // signed in: Profile.SignedInNow moves on; taken: the page asks for its PIN
                }
                break;
            case SetupStep.Pin:
                if (_connectOnly)
                {
                    Shell.GoBack();
                }
                else
                {
                    Step = SetupStep.Done;
                }
                break;
            case SetupStep.Done:
                await FinishAsync().ConfigureAwait(true);
                break;
            default:
                Step = SetupStep.Name;
                break;
        }
    }

    private Task SecondaryAsync()
    {
        switch (_step)
        {
            case SetupStep.Online:
                _goOnline = Launcher.OnlineEnabled;
                Step = SetupStep.Done;
                break;
            case SetupStep.Game:
                Step = SetupStep.Online;
                break;
            case SetupStep.Register:
                Step = SetupStep.Done;
                break;
        }
        return Task.CompletedTask;
    }

    private async Task FinishAsync()
    {
        await Shell.UpdateSettingsAsync(s => s with { Ui = s.Ui with { SetupDone = true } }).ConfigureAwait(true);
        Shell.GoHome();
    }

    public override IReadOnlyList<Hint> Hints
    {
        get
        {
            var hints = new List<Hint>();
            if (_step == SetupStep.Pin)
            {
                hints.Add(PinHint.For(S, _pinRevealed));
            }
            if (_connectOnly || StepIndex > 0)
            {
                hints.Add(new Hint(PadButton.B, S["hint.back"]));
            }
            hints.Add(new Hint(PadButton.A, S["hint.ok"]));
            return hints;
        }
    }

    public override bool OnButton(PadButton button)
    {
        if (button == PadButton.X && _step == SetupStep.Pin)
        {
            PinRevealed = !PinRevealed;
            return true;
        }
        return button is PadButton.Start; // no Settings from inside the flow: it would be a way around it
    }

    public override void OnEnter()
    {
        Profile.Refresh();
        Game.Refresh();
        EnterStep();
    }

    public override void OnLeave()
    {
        PinRevealed = false;
        Profile.EntryRevealed = false;
        Servers.Stop();
    }

    public override void OnStateChanged()
    {
        Profile.Refresh();
        Game.Refresh();
        Servers.Sync();
        FollowGame();
        base.OnStateChanged();
        RefreshHints();
        CommandManager.InvalidateRequerySuggested();
    }

    public override bool OnBack()
    {
        var path = Path;
        var at = StepIndex;
        if (_step == SetupStep.Register && Profile.Editing)
        {
            Profile.CancelEditCommand.Execute(null);
            return true;
        }
        if (at <= 0)
        {
            return !_connectOnly; // the welcome cannot be left with B; the connect pages go back to Settings
        }
        var back = path[at - 1];
        // Pages that have nothing to ask any more are passed over: a consent already given, a profile already there.
        if (back == SetupStep.Consent && Launcher.Settings.AdultConsent)
        {
            back = SetupStep.Register;
        }
        if (back == SetupStep.Register && Launcher.Profile.SignedIn)
        {
            back = SetupStep.Server;
        }
        if (_connectOnly && back == SetupStep.Server && _step is SetupStep.Consent or SetupStep.Pin && Launcher.Profile.SignedIn)
        {
            return false; // signed in already: B leaves the pages (the server is changed from Settings)
        }
        Step = back;
        return true;
    }

    private void EnterStep()
    {
        if (_step == SetupStep.Game)
        {
            EnterGame();
        }
        else if (_step == SetupStep.Register)
        {
            Profile.Refresh();
        }
        if (_step == SetupStep.Server)
        {
            Servers.Start(); // the list, and a new measure every few seconds while this page shows
        }
        else
        {
            Servers.Stop();
        }
    }

    /// <summary>For the screenshot harness (<c>--step server=mkw.example.net</c>): that page, with what was typed on it.</summary>
    public void JumpTo(string step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var (name, arg) = step.Split('=', 2) is [var n, var a] ? (n, a) : (step, "");
        if (!Enum.TryParse<SetupStep>(name, ignoreCase: true, out var to))
        {
            return;
        }
        _goOnline |= to is SetupStep.Consent or SetupStep.Server or SetupStep.Register or SetupStep.Pin || Launcher.OnlineEnabled;
        _adult = Launcher.Settings.AdultConsent;
        Step = to;
        if (arg.Length == 0)
        {
            return;
        }
        switch (to)
        {
            case SetupStep.Name:
                NameText = arg;
                break;
            case SetupStep.Consent:
                Adult = arg is "1" or "yes";
                break;
            case SetupStep.Server:
                Address = arg;
                SubmitAddress();
                break;
            case SetupStep.Pin:
                PinRevealed = arg is "show";
                break;
        }
    }

    internal static string Minutes(Strings s, TimeSpan left) => left.TotalMinutes >= 1.5
        ? s.Format("update.minutes_left", ("n", Math.Round(left.TotalMinutes).ToString(CultureInfo.CurrentCulture)))
        : s.Format("update.seconds_left", ("n", Math.Max(1, Math.Round(left.TotalSeconds)).ToString(CultureInfo.CurrentCulture)));

    internal static string Bytes(long bytes) => bytes >= 1_000_000_000
        ? (bytes / 1e9).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : Math.Round(bytes / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";
}
