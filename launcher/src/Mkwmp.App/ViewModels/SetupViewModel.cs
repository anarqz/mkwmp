// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The pages of the welcome flow (Welcome, Online, Name, Game, Done), and the four of an MKWMP server's connect
/// pages (Settings › Online's Connect): a server, signing in on it, then the consent (which that server keeps, so it
/// can only come after signing in), then the PIN.
/// </summary>
public enum SetupStep { Welcome, Name, Game, Online, Server, Register, Consent, Pin, Done }

/// <summary>
/// The first-start welcome, a paged flow (shown while <c>Ui.SetupDone</c> is false; it resumes at
/// the first step that is still open):
/// Welcome → "Play online with Nextendo Network" (<see cref="SetupStep.Online"/>: "Sign in with Nextendo" opens the
/// sign-in page in the browser and the page waits for it, then says who signed in; "Create a Nextendo Account" for
/// who has none; "Skip for Now" plays offline and signs in later) → Your Name (local; the Nextendo account's when it
/// fits) → Your Game (download mkw-recomp, the player's own game, the setup, MKWMP's files: SetupViewModel.Game.cs)
/// → Done.
/// <para>
/// Nothing of an MKWMP server is in the welcome: MKWMP servers are the alternative of Settings › Online, and its
/// "Connect to a Server" (<see cref="Connect"/>) is this class's other four pages, pushed over Settings and ending
/// back there: the server (from the list, or a typed address), signing in on it (registering the name, or
/// recovering a profile with its PIN), the consent to that server's privacy policy, the PIN (masked). So the
/// consent to send anything to an MKWMP server is asked for where such a server is chosen, and nowhere before.
/// </para>
/// Offline is a complete state: nothing here contacts a server before the player signs in, and the game
/// steps can wait ("Later": PLAY then says what is missing).
/// </summary>
public sealed partial class SetupViewModel : PageViewModel, IDisposable
{
    private readonly bool _connectOnly;
    private SetupStep _step;
    private bool _createdAccount;
    private string _name;
    private PrivacyViewModel? _privacy;
    private string _privacyServer = "";
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
        Servers.HintsChanged += (_, _) => RefreshHints();
        var s = Launcher.Settings;
        _name = s.Name;
        _address = Launcher.OnlineEnabled ? ProfileForm.HostOf(s.Server)
            : Launcher.SavedServers.Count > 0 ? ProfileForm.HostOf(Launcher.SavedServers[^1].Address) : "";
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
        LanguageCommand = RelayCommand.Async(Shell.PickLanguageAsync);
        CreateNextendoCommand = new RelayCommand(() =>
        {
            Shell.CreateNextendoAccount();
            _createdAccount = true; // the page then says to come back and sign in
            Raise(nameof(ShowCreatedHint));
        });
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

    /// <summary>
    /// Settings › Online's "Privacy Policy": the welcome's consent page alone (what is kept, the server's policy, its
    /// acceptance under them), the one privacy page of the launcher. Its button closes it and B leaves it: back to
    /// Settings › Online, never on to the next welcome page.
    /// </summary>
    public static SetupViewModel PrivacyPolicy(ShellViewModel shell) => new(shell, connectOnly: true, start: SetupStep.Consent) { PolicyOnly = true };

    /// <summary>Opened from Settings to read the policy and give or withdraw the consent: one page, no flow around it.</summary>
    private bool PolicyOnly { get; init; }

    /// <summary>The servers to choose from (the beholder's list), on the server page.</summary>
    public ServerPicker Servers { get; }

    public bool IsFirstRun => !_connectOnly;

    /// <summary>The welcome's first page offers the language, before there is anything to read.</summary>
    public bool ShowLanguage => _step == SetupStep.Welcome;

    /// <summary>"Language · English": the word in the language in use, the language in its own name.</summary>
    public string LanguageLabel => S["setup.welcome.language"] + " · " + (Locales.Find(S.Locale)?.Name ?? S.Locale);

    public ICommand LanguageCommand { get; }

    /// <summary>The language changed: the welcome again, where it stands (its first page offers the selector). The connect pages are left.</summary>
    public override PageViewModel? Recreate() => _connectOnly ? null : new SetupViewModel(Shell);

    public void Dispose()
    {
        _runtimeCancel?.Dispose();
        _setupCancel?.Dispose();
    }

    /// <summary>Where a welcome that was left half-way goes on (with no name yet: from its first page, so the Nextendo page comes too).</summary>
    private SetupStep FirstOpenStep()
    {
        var s = Launcher.Settings;
        if (!Links.ValidName(s.Name))
        {
            return s.Name.Length == 0 ? SetupStep.Welcome : SetupStep.Name;
        }
        return Stage != GameStage.Ready ? SetupStep.Game : SetupStep.Done;
    }

    /// <summary>The pages of this run, in order (the dots).</summary>
    private IReadOnlyList<SetupStep> Path => _connectOnly
        ? [SetupStep.Server, SetupStep.Register, SetupStep.Consent, SetupStep.Pin]
        : NameFromNextendo
            ? [SetupStep.Welcome, SetupStep.Online, SetupStep.Game, SetupStep.Done]
            : [SetupStep.Welcome, SetupStep.Online, SetupStep.Name, SetupStep.Game, SetupStep.Done];

    /// <summary>Signed in with Nextendo: the player's name is the account's username (the one other players see in races), so no name page.</summary>
    private bool NameFromNextendo => Nextendo.Linked && Links.ValidName(Nextendo.Username);

    public int StepCount => PolicyOnly ? 0 : Path.Count;

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
        SetupStep.Online => S["nextendo.title"],
        SetupStep.Consent => S[PolicyOnly ? "settings.privacy.policy" : "setup.consent.title"],
        SetupStep.Server => S["setup.server.title"],
        SetupStep.Register => S["setup.register.title"],
        SetupStep.Pin => S["setup.pin.title"],
        _ => S["setup.done.title"],
    };

    public ProfileForm Profile { get; }

    public GameForm Game { get; }

    public ICommand PrimaryCommand { get; }

    public ICommand SecondaryCommand { get; }

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

    // ── Nextendo Network: sign in (the browser), create an account, or skip for now ──

    private NextendoView Nextendo => Launcher.Nextendo;

    /// <summary>"ask" (sign in, create, skip), "wait" (the sign-in page is open in the browser) or "done" (signed in): what the Nextendo page shows.</summary>
    public string NextendoStage => Nextendo.Linked ? "done" : Nextendo.Linking ? "wait" : "ask";

    public string NextendoName => Nextendo.Username;

    /// <summary>The account's own profile picture on Nextendo (a file), once it is here; null: the disc with its initials.</summary>
    public string? NextendoPicture => Launcher.NextendoOwnAvatar.Path;

    public string NextendoFriendCode => Nextendo.FriendCode;

    public bool HasNextendoFriendCode => Nextendo.FriendCode.Length > 0;

    /// <summary>"Signed in as bia".</summary>
    public string NextendoSignedIn => CoreStrings.NextendoLinked(Nextendo.Username);

    /// <summary>What the launcher says of the sign-in: that it waits for the browser, or why it did not work.</summary>
    public string NextendoNote => Nextendo.Linked ? "" : Nextendo.Note ?? "";

    public bool NextendoNoteIsError => Nextendo.NoteIsError;

    /// <summary>"Account created? Come back here and choose Sign in with Nextendo.", once the sign-up page was opened.</summary>
    public bool ShowCreatedHint => _createdAccount && NextendoStage == "ask";

    /// <summary>nextendo.network's sign-up page, in the browser; signing in is then this page's button.</summary>
    public ICommand CreateNextendoCommand { get; }

    private void SignInNextendo() =>
        RelayCommand.Async(() => Launcher.LinkNextendoAsync(page => Links.Open(page))).Execute(null); // not awaited: the page's button is Cancel meanwhile

    // ── an MKWMP server's consent (Settings › Online's connect pages: nothing reaches that server before it) ──

    /// <summary>
    /// The consent page's content: the privacy component (the policy, and its acceptance under it). Only while that
    /// page shows (it asks the server for its policy), and made again for another server.
    /// </summary>
    public PrivacyViewModel? Privacy
    {
        get
        {
            if (_step != SetupStep.Consent)
            {
                return null;
            }
            if (_privacy is null || _privacyServer != Launcher.Settings.Server)
            {
                _privacyServer = Launcher.Settings.Server;
                _privacy = new PrivacyViewModel(Shell) { Lead = PolicyOnly ? "" : ConsentText };
            }
            return _privacy;
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
            // online: the Nextendo account; an install that plays on MKWMP servers (it met the welcome late) is told of its server
            lines.Add(Nextendo is { Preferred: true, Linked: true } ? new StatusLine("good", S.Format("core.check.online.nextendo", ("name", Nextendo.Username)))
                : Nextendo.Preferred ? new StatusLine("muted", S["setup.done.nextendo_off"], S["setup.done.nextendo_off_detail"])
                : !Launcher.OnlineEnabled ? new StatusLine("muted", S["setup.done.offline"], S["setup.done.offline_detail"])
                : Launcher.Profile.SignedIn ? new StatusLine("good", S.Format("setup.done.online", ("name", s.Name), ("server", ServerShort)))
                : new StatusLine("warn", S.Format("setup.done.online_no_profile", ("server", ServerShort)), S["setup.done.online_no_profile_detail"]));
            // how to come back here from the game: the player's overlay shortcut (Settings › Game)
            lines.Add(new StatusLine("muted", S.Format("setup.done.shortcut", ("shortcut", ShortcutText.Name(S, s.Ui.OverlayShortcut))), S["setup.done.shortcut_detail"]));
            return lines;
        }
    }

    public string DoneText => Stage != GameStage.Ready ? S["setup.done.almost"]
        : Nextendo.Active || (Launcher.OnlineEnabled && Launcher.Profile.SignedIn) ? S["setup.done.ready"]
        : S["setup.done.ready_offline"];

    // ── the two buttons under every page ──

    public string PrimaryLabel => _step switch
    {
        SetupStep.Game => GamePrimaryLabel,
        SetupStep.Consent when PolicyOnly => S["common.close"],
        SetupStep.Online => S[NextendoStage switch { "done" => "common.next", "wait" => "common.cancel", _ => "nextendo.signin" }],
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
        SetupStep.Consent => PolicyOnly || Launcher.Settings.AdultConsent && Launcher.Profile.Register != RegisterState.Working,
        SetupStep.Server => !_probing && _address.Length > 0 && !Launcher.Session.Busy,
        SetupStep.Register => Launcher.Profile.SignedIn ? !Profile.Editing : Profile.ShowPinEntry ? Profile.CanUsePin : Profile.CanRegister,
        _ => true,
    };

    public string SecondaryLabel => _step == SetupStep.Online ? S["setup.online.skip"] : S["setup.later"];

    /// <summary>"Skip for Now" on the Nextendo page (until signed in); "Later" where a step can wait (the game).</summary>
    public bool ShowSecondary => _step switch
    {
        SetupStep.Online => NextendoStage == "ask",
        SetupStep.Game => GameCanWait,
        _ => false,
    };

    /// <summary>The Nextendo page's "Skip for Now" is a quiet button: signing in is what the page is for.</summary>
    public bool SecondaryIsQuiet => _step == SetupStep.Online;

    /// <summary>"Sign in with Nextendo" is filled: the one thing its page asks for.</summary>
    public bool PrimaryIsAccent => _step == SetupStep.Online && NextendoStage == "ask";

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
                if (NameFromNextendo)
                {
                    if (Launcher.Settings.Name.Length == 0)
                    {
                        // the welcome's resume needs a name on this device: the account's, the one played under
                        var account = Nextendo.Username;
                        await Shell.UpdateSettingsAsync(s => s with { Name = account }).ConfigureAwait(true);
                    }
                    Step = SetupStep.Game;
                }
                else if (Nextendo.Linked)
                {
                    Step = SetupStep.Name;
                }
                else if (Nextendo.Linking)
                {
                    Launcher.CancelNextendoLink();
                }
                else if (Launcher.OnlineBlockedByUpdate)
                {
                    Shell.ConfigureOnline(); // offline only until MKWMP is updated: the update's card, not a sign-in
                }
                else
                {
                    SignInNextendo();
                }
                break;
            case SetupStep.Consent when PolicyOnly:
                Shell.GoBack(); // read, accepted or withdrawn: back to Settings › Online
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
                Step = SetupStep.Online;
                break;
        }
    }

    private Task SecondaryAsync()
    {
        switch (_step)
        {
            case SetupStep.Online:
                Step = SetupStep.Name; // offline for now: home and Settings › Nextendo offer the sign-in again
                break;
            case SetupStep.Game:
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
            if (_step == SetupStep.Server && Servers.Hint is { } hide)
            {
                hints.Add(hide); // a server of the list has the focus: Y hides it
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
        if (_step == SetupStep.Server && Servers.OnButton(button))
        {
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
        _privacy?.OnStateChanged();
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
        if (_step == SetupStep.Online && Nextendo.Linking)
        {
            Launcher.CancelNextendoLink(); // B while the browser is awaited gives the sign-in up, and stays on the page
            return true;
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
        else if (_step == SetupStep.Name && _name.Length == 0 && Links.ValidName(Nextendo.Username))
        {
            _name = Nextendo.Username; // signed in a page ago: the same name here, unless the player types another
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
                if (arg is "1" or "yes")
                {
                    RelayCommand.Async(() => Launcher.SetConsentAsync(true)).Execute(null);
                }
                break;
            case SetupStep.Server:
                Address = arg;
                SubmitAddress();
                break;
            case SetupStep.Pin:
                PinRevealed = arg is "show";
                break;
            case SetupStep.Online:
                // online=created: after "Create a Nextendo Account" (the browser awaited: --state nextendo-linking; signed in: --demo-nextendo)
                _createdAccount = arg is "created";
                RaiseAll();
                break;
        }
    }

    internal static string Minutes(Strings s, TimeSpan left) => left.TotalMinutes >= 1.5
        ? s.Format("update.minutes_left", ("n", Math.Round(left.TotalMinutes).ToString(CultureInfo.CurrentCulture)))
        : s.Format("update.seconds_left", ("n", Math.Max(1, Math.Round(left.TotalSeconds)).ToString(CultureInfo.CurrentCulture)));

    internal static string Bytes(long bytes) => CoreStrings.Size(bytes);
}
