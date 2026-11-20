// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Party Chat's introduction, shown the first time Party Chat is opened (and again from Settings):
/// three pages about what it is, voice and camera, then the consent page whose Next stays disabled
/// until the box is ticked. Finishing sets <c>Voice.ChatIntroDone</c> and opens the Party Chat sheet.
/// </summary>
public sealed class ChatViewModel : PageViewModel
{
    private const int ConsentStep = 3;

    private readonly bool _replay;
    private int _step;
    private bool _understood;

    public ChatViewModel(ShellViewModel shell, bool replay = false)
        : base(shell)
    {
        _replay = replay;
        _understood = replay && Launcher.Settings.Voice.ChatIntroDone;
        NextCommand = RelayCommand.Async(NextAsync, () => _step != ConsentStep || _understood);
        PolicyCommand = new RelayCommand(() => Links.Open(Shell, CoreStrings.PrivacyUrl));
    }

    public static int StepCount => 4;

    public int Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                RaiseAll();
                RefreshHints();
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsConsent => _step == ConsentStep;

    public bool IsIntro => _step != ConsentStep;

    public string Title => IsConsent ? S["chat.consent.title"] : S["chat.intro.title"];

    public string Heading => S[$"chat.intro{_step + 1}.heading"];

    public string Text => S[$"chat.intro{_step + 1}.text"];

    public string Note => S[$"chat.intro{_step + 1}.note"];

    public string NextLabel => IsConsent ? S["common.done"] : S["common.next"];

    /// <summary>"I understand: friend rooms only, live, never recorded."</summary>
    public bool Understood
    {
        get => _understood;
        set
        {
            if (Set(ref _understood, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public ICommand NextCommand { get; }

    public ICommand PolicyCommand { get; }

    public override IReadOnlyList<Hint> Hints => [new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["hint.ok"])];

    public override bool OnBack()
    {
        if (_step == 0)
        {
            return false;
        }
        Step--;
        return true;
    }

    private async Task NextAsync()
    {
        if (_step < StepCount - 1)
        {
            Step++;
            return;
        }
        await Shell.UpdateSettingsAsync(s => s with { Voice = s.Voice with { ChatIntroDone = true } }).ConfigureAwait(true);
        Shell.GoBack();
        if (!_replay)
        {
            Shell.OpenSheet(new ChatSheetViewModel(Shell));
        }
    }
}

/// <summary>Someone in the Party Chat sheet: the player (first), then the friends who are racing now.</summary>
public sealed record ChatCard(string Name, string Seed, bool IsSelf);

/// <summary>
/// The Party Chat sheet, over the current page: the player's own card (with the local camera
/// preview and the microphone level while testing) and a card per friend who is racing now; a pill
/// of round toggles: voice on / off (<c>voice.enabled</c>, which the game polls), the camera
/// preview and the microphone test (both local, only this screen), then Settings. The room's voice
/// and camera themselves run inside the game: the sheet says so.
/// </summary>
public sealed class ChatSheetViewModel : LayerViewModel
{
    private readonly ShellViewModel _shell;

    public ChatSheetViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Mic = new MicTester(shell.Launcher);
        Preview = new CameraPreview(shell.Launcher.Devices);
        Mic.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MicTester.Running))
            {
                Raise(nameof(MicIcon));
                Raise(nameof(MicLabel));
                Raise(nameof(MicTesting));
            }
        };
        Preview.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(CameraPreview.Running) or nameof(CameraPreview.Status))
            {
                Raise(nameof(CameraIcon));
                Raise(nameof(CameraLabel));
                Raise(nameof(CameraOn));
            }
        };
        LeaveCommand = new RelayCommand(shell.CloseSheet);
        VoiceCommand = RelayCommand.Async(() => shell.UpdateSettingsAsync(s => s with { Voice = s.Voice with { Enabled = !s.Voice.Enabled } }));
        CameraCommand = new RelayCommand(ToggleCamera, () => CameraAvailable);
        MicCommand = new RelayCommand(() => Mic.Running = !Mic.Running);
        SettingsCommand = new RelayCommand(() =>
        {
            shell.CloseSheet();
            shell.Navigate(new SettingsViewModel(shell).At("chat"));
        });
        SyncCards();
    }

    public Strings S => _shell.S;

    private ILauncher Launcher => _shell.Launcher;

    public MicTester Mic { get; }

    public CameraPreview Preview { get; }

    public ObservableCollection<ChatCard> Cards { get; } = [];

    public bool VoiceOn => Launcher.Settings.Voice.Enabled;

    public string VoiceIcon => VoiceOn ? "Icon.Volume" : "Icon.VolumeOff";

    public string VoiceLabel => VoiceOn ? S["chat.voice_on"] : S["chat.voice_off"];

    public bool CameraAvailable => !Links.IsWine && Launcher.Devices.CameraAvailable;

    public bool CameraOn => Preview.Running;

    public string CameraIcon => Preview.Running ? "Icon.Camera" : "Icon.CameraOff";

    public string CameraLabel => Preview.Running ? S["chat.camera_on"] : S["chat.camera_off"];

    public bool MicTesting => Mic.Running;

    public string MicIcon => Mic.Running ? "Icon.Mic" : "Icon.MicOff";

    public string MicLabel => Mic.Running ? S["chat.mic_on"] : S["chat.mic_off"];

    /// <summary>Who is racing, in a sentence.</summary>
    public string Status
    {
        get
        {
            var racing = Launcher.Friends.Friends.Where(f => f.State == FriendState.Playing).Select(f => f.Name).ToList();
            return racing.Count switch
            {
                0 => S["chat.nobody"],
                1 => S.Format("chat.one_racing", ("name", racing[0])),
                _ => S.Format("chat.many_racing", ("n", racing.Count.ToString(System.Globalization.CultureInfo.CurrentCulture))),
            };
        }
    }

    /// <summary>The honest line: where the chat really happens.</summary>
    public string Caption => !CameraAvailable && Links.IsWine ? S["chat.caption"] + " " + S["chat.caption_linux"] : S["chat.caption"];

    public ICommand LeaveCommand { get; }

    public ICommand VoiceCommand { get; }

    public ICommand CameraCommand { get; }

    public ICommand MicCommand { get; }

    public ICommand SettingsCommand { get; }

    public override IReadOnlyList<Hint> Hints => [new Hint(PadButton.B, S["hint.close_menu"]), new Hint(PadButton.A, S["hint.ok"])];

    public override void OnStateChanged()
    {
        SyncCards();
        RaiseAll();
    }

    public override void OnClosed()
    {
        Mic.Stop();
        Preview.Stop();
    }

    private void ToggleCamera()
    {
        if (Preview.Running)
        {
            Preview.Stop();
        }
        else
        {
            var voice = Launcher.Settings.Voice;
            Preview.Start(voice.Camera, voice.CameraMirror);
        }
    }

    /// <summary>In place: a card under the focus stays while its player is still there.</summary>
    private void SyncCards()
    {
        var self = Launcher.Profile.Name.Length > 0 ? Launcher.Profile.Name : S["chat.you"];
        var wanted = new List<ChatCard> { new(self, "#5", true) };
        wanted.AddRange(Launcher.Friends.Friends.Where(f => f.State == FriendState.Playing).Take(3).Select(f => new ChatCard(f.Name, f.Name, false)));
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Cards[i]))
            {
                Cards.RemoveAt(i);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (!Cards.Contains(wanted[i]))
            {
                Cards.Insert(Math.Min(i, Cards.Count), wanted[i]);
            }
        }
    }
}
