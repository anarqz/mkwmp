// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.App.Input;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Party Chat: the paged introduction. STUB of the design system (a paged flow with dots, and the
/// bottom sheet). The Party Chat agent owns this file and everything under Views/Chat/.
/// </summary>
public sealed class ChatViewModel : PageViewModel
{
    private int _step;

    public ChatViewModel(ShellViewModel shell)
        : base(shell)
    {
        NextCommand = new RelayCommand(Next);
        SheetCommand = new RelayCommand(() => Shell.OpenSheet(new ChatSheetViewModel(Shell)));
    }

    public static int StepCount => 3;

    public int Step
    {
        get => _step;
        private set
        {
            if (Set(ref _step, value))
            {
                Raise(nameof(Heading));
                Raise(nameof(Text));
                Raise(nameof(Note));
                Raise(nameof(NextLabel));
            }
        }
    }

    public string Heading => S[$"chat.intro{_step + 1}.heading"];

    public string Text => S[$"chat.intro{_step + 1}.text"];

    public string Note => S[$"chat.intro{_step + 1}.note"];

    public string NextLabel => _step == StepCount - 1 ? S["common.done"] : S["common.next"];

    public ICommand NextCommand { get; }

    /// <summary>Opens the demo of the bottom sheet (participants and the pill of toggles).</summary>
    public ICommand SheetCommand { get; }

    public override IReadOnlyList<Hint> Hints =>
    [
        new Hint(PadButton.X, S["chat.sheet_hint"]),
        new Hint(PadButton.B, S["hint.back"]),
        new Hint(PadButton.A, S["hint.ok"]),
    ];

    public override bool OnBack()
    {
        if (_step == 0)
        {
            return false;
        }
        Step--;
        return true;
    }

    public override bool OnButton(PadButton button)
    {
        if (button != PadButton.X)
        {
            return false;
        }
        SheetCommand.Execute(null);
        return true;
    }

    private void Next()
    {
        if (_step < StepCount - 1)
        {
            Step++;
        }
        else
        {
            Shell.GoBack();
        }
    }
}

/// <summary>Someone in the chat sheet.</summary>
public sealed record ChatParticipant(string Name, string Seed, bool Speaking);

/// <summary>The bottom sheet of the reference: who is in the chat, and the pill of round toggles. STUB data.</summary>
public sealed class ChatSheetViewModel : LayerViewModel
{
    private readonly ShellViewModel _shell;
    private bool _camera;
    private bool _mic = true;

    public ChatSheetViewModel(ShellViewModel shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        Participants = [new ChatParticipant(shell.Profile.Name, "#5", false), new ChatParticipant("Luna", "#1", true), new ChatParticipant("Kai", "#3", false)];
        LeaveCommand = new RelayCommand(shell.CloseSheet);
        CameraCommand = new RelayCommand(() => Camera = !Camera);
        MicCommand = new RelayCommand(() => Mic = !Mic);
        SettingsCommand = new RelayCommand(() =>
        {
            shell.CloseSheet();
            shell.Navigate(Routes.Settings);
        });
    }

    public Strings S => _shell.S;

    public IReadOnlyList<ChatParticipant> Participants { get; }

    public bool Camera
    {
        get => _camera;
        set
        {
            if (Set(ref _camera, value))
            {
                Raise(nameof(CameraIcon));
            }
        }
    }

    public bool Mic
    {
        get => _mic;
        set
        {
            if (Set(ref _mic, value))
            {
                Raise(nameof(MicIcon));
            }
        }
    }

    public string CameraIcon => _camera ? "Icon.Camera" : "Icon.CameraOff";

    public string MicIcon => _mic ? "Icon.Mic" : "Icon.MicOff";

    public ICommand LeaveCommand { get; }

    public ICommand CameraCommand { get; }

    public ICommand MicCommand { get; }

    public ICommand SettingsCommand { get; }

    public override IReadOnlyList<Hint> Hints => [new Hint(PadButton.B, S["hint.close_menu"]), new Hint(PadButton.A, S["hint.ok"])];

    public override void OnStateChanged()
    {
    }
}
