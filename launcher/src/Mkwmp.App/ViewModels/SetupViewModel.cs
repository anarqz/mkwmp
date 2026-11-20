// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The first-run setup, a paged flow: Welcome → Before you play online (the Rust window's consent
/// dialog: nothing reaches the server before it) → Gamertag → Game folder → Party Chat quick setup
/// → Done (sets <c>Ui.SetupDone</c>). Shown at start while the setup is not done or the consent was
/// taken back; it resumes at the first step that is still open. PLAY is blocked by
/// <c>PlayProblem</c> anyway, so the game folder can wait ("Later").
/// </summary>
public sealed class SetupViewModel : PageViewModel
{
    public const int Welcome = 0;
    public const int Consent = 1;
    public const int Gamertag = 2;
    public const int GameFolder = 3;
    public const int PartyChat = 4;
    public const int Done = 5;

    private int _step;
    private bool _adult;
    private IReadOnlyList<AudioDevice> _labInputs = [];

    public SetupViewModel(ShellViewModel shell)
        : base(shell)
    {
        _adult = Launcher.Settings.AdultConsent;
        Profile = new ProfileForm(shell);
        Game = new GameForm(shell);
        Game.Refresh();
        Mic = new MicTester(Launcher);
        Profile.SignedInNow += (_, _) =>
        {
            if (_step == Gamertag)
            {
                Step = GameFolder;
            }
        };
        NextCommand = RelayCommand.Async(NextAsync, () => CanNext);
        LaterCommand = new RelayCommand(() => Step = _step + 1, () => _step is Gamertag or GameFolder);
        PolicyCommand = new RelayCommand(() => Links.Open(Shell, CoreStrings.PrivacyUrl));
        InputCommand = RelayCommand.Async(PickInputAsync);
        _step = FirstOpenStep();
    }

    public static int StepCount => 6;

    /// <summary>Where a setup that was left half-way goes on.</summary>
    private int FirstOpenStep()
    {
        var s = Launcher.Settings;
        if (s.Ui.SetupDone)
        {
            return s.AdultConsent ? Welcome : Consent; // the consent was taken back in Settings › Privacy
        }
        if (!s.AdultConsent)
        {
            return Welcome;
        }
        return !Launcher.Profile.SignedIn ? Gamertag : Launcher.Game.Install is null ? GameFolder : PartyChat;
    }

    public ProfileForm Profile { get; }

    public GameForm Game { get; }

    public MicTester Mic { get; }

    public int Step
    {
        get => _step;
        private set
        {
            value = Math.Clamp(value, 0, StepCount - 1);
            var old = _step;
            if (!Set(ref _step, value))
            {
                return;
            }
            if (old == PartyChat)
            {
                Mic.Stop();
            }
            EnterStep();
            RaiseAll();
            RefreshHints();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Title => S[_step switch
    {
        Welcome => "setup.welcome.title",
        Consent => "setup.consent.title",
        Gamertag => "setup.name.title",
        GameFolder => "setup.game.title",
        PartyChat => "setup.chat.title",
        _ => "setup.done.title",
    }];

    /// <summary>"I'm 18 or older and I accept the privacy policy".</summary>
    public bool Adult
    {
        get => _adult;
        set
        {
            if (Set(ref _adult, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool CanNext => _step switch
    {
        Consent => _adult,
        Gamertag => Launcher.Profile.SignedIn && !Profile.Editing,
        GameFolder => Launcher.Game.Install is not null,
        _ => true,
    };

    public string NextLabel => _step == Done ? S["setup.done.start"] : S["common.next"];

    /// <summary>The gamertag can wait only when the server could not be asked; the game folder always can.</summary>
    public bool ShowLater => _step == GameFolder ? Launcher.Game.Install is null
        : _step == Gamertag && !Launcher.Profile.SignedIn && Launcher.Profile.Register == RegisterState.Failed;

    public string GameHeading => Game.Searching ? S["setup.game.searching"] : Game.Found ? S["setup.game.found"] : S["setup.game.missing"];

    // ── Party Chat quick setup ──

    private IReadOnlyList<AudioDevice> Inputs => Launcher.Voice.Inputs.Count > 0 ? Launcher.Voice.Inputs : _labInputs;

    public string InputName => Launcher.Settings.Voice.Input is { Length: > 0 } selected
        ? Inputs.FirstOrDefault(d => d.Id == selected)?.Name ?? S["settings.chat.not_connected"]
        : CoreStrings.SystemDefault;

    public bool MicTesting
    {
        get => Mic.Running;
        set
        {
            Mic.Running = value;
            Raise();
        }
    }

    public ICommand InputCommand { get; }

    private async Task PickInputAsync()
    {
        _labInputs = await Launcher.Devices.InputDevicesAsync().ConfigureAwait(true);
        var selected = Launcher.Settings.Voice.Input;
        var options = new List<ChoiceOption> { new("", CoreStrings.SystemDefault) };
        options.AddRange(Inputs.Select(d => new ChoiceOption(d.Id, d.Name)));
        var picked = await Choice.AskAsync(Shell, S["settings.chat.input"], "", options, selected).ConfigureAwait(true);
        if (picked is not null && picked != selected)
        {
            await Shell.UpdateSettingsAsync(s => s with { Voice = s.Voice with { Input = picked } }).ConfigureAwait(true);
            Mic.Restart();
        }
        Raise(nameof(InputName));
    }

    // ── done ──

    /// <summary>What is ready and what still blocks PLAY, with lights.</summary>
    public IReadOnlyList<StatusLine> Summary
    {
        get
        {
            var s = Launcher.Settings;
            var lines = new List<StatusLine>
            {
                s.AdultConsent ? new("good", S["setup.done.consent"]) : new("bad", CoreStrings.NoConsent),
                Launcher.Profile.SignedIn
                    ? new("good", S.Format("setup.done.name", ("name", Launcher.Profile.Name)))
                    : new("warn", S["setup.done.no_name"], CoreStrings.NeedPin),
            };
            if (Launcher.Game.Install is { } install)
            {
                lines.Add(new StatusLine("good", S.Format("setup.done.game", ("name", Game.TargetName), ("version", install.Version ?? S["settings.game.unknown"]))));
                if (Launcher.Game.Check is { Problem: { } problem })
                {
                    lines.Add(new StatusLine("bad", problem));
                }
            }
            else
            {
                lines.Add(new StatusLine("warn", S["setup.done.no_game"], S["setup.done.no_game_detail"]));
            }
            lines.Add(new StatusLine(s.Voice.Enabled ? "good" : "muted", s.Voice.Enabled ? S["setup.done.chat_on"] : S["setup.done.chat_off"]));
            return lines;
        }
    }

    public string DoneText => Launcher.PlayProblem is null ? S["setup.done.ready"] : S["setup.done.almost"];

    public ICommand NextCommand { get; }

    public ICommand LaterCommand { get; }

    public ICommand PolicyCommand { get; }

    public override IReadOnlyList<Hint> Hints => _step == Welcome
        ? [new Hint(PadButton.A, S["hint.ok"])]
        : [new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["hint.ok"])];

    public override void OnEnter()
    {
        Profile.Refresh();
        Game.Refresh();
        EnterStep();
    }

    public override void OnLeave() => Mic.Stop();

    public override void OnStateChanged()
    {
        Profile.Refresh();
        Game.Refresh();
        base.OnStateChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    public override bool OnBack()
    {
        if (Profile.Editing && _step == Gamertag)
        {
            Profile.CancelEditCommand.Execute(null);
        }
        else if (_step > 0)
        {
            Step--;
        }
        return true; // the setup cannot be left with B
    }

    private void EnterStep()
    {
        switch (_step)
        {
            case GameFolder when Launcher.Game.Install is null && !Launcher.Game.Searching:
                RelayCommand.Async(() => Launcher.FindGameAsync()).Execute(null); // the launcher looks by itself first
                break;
            case PartyChat:
                RelayCommand.Async(async () =>
                {
                    _labInputs = await Launcher.Devices.InputDevicesAsync().ConfigureAwait(true);
                    Raise(nameof(InputName));
                }).Execute(null);
                break;
        }
    }

    private async Task NextAsync()
    {
        if (_step == Consent && !Launcher.Settings.AdultConsent)
        {
            await Launcher.SetConsentAsync(true).ConfigureAwait(true);
        }
        if (_step < Done)
        {
            Step++;
            return;
        }
        Mic.Stop();
        await Shell.UpdateSettingsAsync(s => s with { Ui = s.Ui with { SetupDone = true } }).ConfigureAwait(true);
        Shell.GoHome();
    }
}
