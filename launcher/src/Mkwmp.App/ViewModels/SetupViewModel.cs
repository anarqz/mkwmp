// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.App.Input;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// First-run setup (welcome, consent, gamertag, game folder). STUB of the design system: the
/// welcome and the consent step, then it marks the setup done and goes home. The setup agent owns
/// this file and everything under Views/Setup/.
/// </summary>
public sealed class SetupViewModel : PageViewModel
{
    private int _step;
    private bool _adult;

    public SetupViewModel(ShellViewModel shell)
        : base(shell)
    {
        _adult = Launcher.Settings.AdultConsent;
        NextCommand = RelayCommand.Async(NextAsync, () => _step != 1 || _adult);
    }

    public static int StepCount => 4;

    public int Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                Raise(nameof(Title));
                Raise(nameof(IsWelcome));
                Raise(nameof(IsConsent));
                Raise(nameof(IsPlaceholder));
                RefreshHints();
            }
        }
    }

    public string Title => S[_step switch { 0 => "setup.welcome.title", 1 => "setup.consent.title", 2 => "setup.name.title", _ => "setup.game.title" }];

    public bool IsWelcome => _step == 0;

    public bool IsConsent => _step == 1;

    public bool IsPlaceholder => _step >= 2;

    /// <summary>"I am 18 or older".</summary>
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

    public ICommand NextCommand { get; }

    public override IReadOnlyList<Hint> Hints => _step == 0
        ? [new Hint(PadButton.A, S["hint.ok"])]
        : [new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["hint.ok"])];

    public override bool OnBack()
    {
        if (_step > 0)
        {
            Step--;
        }
        return true; // the setup cannot be left with B
    }

    private async Task NextAsync()
    {
        if (_step == 1)
        {
            await Launcher.SetConsentAsync(_adult).ConfigureAwait(true);
        }
        if (_step < StepCount - 1)
        {
            Step++;
            return;
        }
        await Shell.UpdateSettingsAsync(s => s with { Ui = s.Ui with { SetupDone = true } }).ConfigureAwait(true);
        Shell.GoHome();
    }
}
