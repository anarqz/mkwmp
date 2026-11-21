// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Mkwmp.Core;
using Mkwmp.Core.Devices;
using Mkwmp.Core.Targets;
using Mkwmp.Core.Targets.MkwRecomp;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>A muted player: UNMUTE, or REPORT (fills the report form below).</summary>
public sealed class MuteRow
{
    public MuteRow(MutedPlayer player, Action<MutedPlayer> unmute, Action<MutedPlayer> report)
    {
        ArgumentNullException.ThrowIfNull(player);
        Account = player.Account;
        Name = player.Name;
        UnmuteCommand = new RelayCommand(() => unmute(player));
        ReportCommand = new RelayCommand(() => report(player));
    }

    public string Account { get; }

    public string Name { get; }

    public ICommand UnmuteCommand { get; }

    public ICommand ReportCommand { get; }
}

/// <summary>
/// Settings › Voice &amp; Camera: the voice switch, devices and volumes (the game polls them through
/// the local API), the launcher's own microphone / output / camera tests, the in-game camera switches
/// (the mod's settings, changed while the game is closed), muted players and the report form.
/// Voice and camera exist only inside the game (private rooms, friends added on the server, up to
/// four at a time); this pane only configures and tests. Locked until a server is set, as every
/// category of <see cref="OnlineOnly"/>.
/// </summary>
public sealed partial class SettingsViewModel
{
    private static readonly TimeSpan ModRefresh = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, string?> _mod = [];
    private readonly Dictionary<string, string?> _demoMod = new() { [ModSettings.Camera] = "1", [ModSettings.CameraView] = "1", ["consent18"] = "1" };
    private IReadOnlyList<AudioDevice> _labInputs = [];
    private IReadOnlyList<AudioDevice> _labOutputs = [];
    private IReadOnlyList<CameraDevice> _cameras = [];
    private DispatcherTimer? _gainSave;
    private int? _inputGain;
    private int? _outputGain;
    private DateTime _modRead;
    private bool _gameRunning;
    private bool _modSupported;
    private string _reportName = "";
    private string _reportReason = "";
    private string _reportDetails = "";
    private bool _playingSound;

    /// <summary>The microphone test (level meter, hear myself).</summary>
    public MicTester Mic { get; }

    public ObservableCollection<MuteRow> Mutes { get; } = [];

    public ICommand InputCommand { get; private set; } = null!;

    public ICommand OutputCommand { get; private set; } = null!;

    public ICommand TestOutputCommand { get; private set; } = null!;

    public ICommand CameraCommand { get; private set; } = null!;

    public ICommand TestCameraCommand { get; private set; } = null!;

    public ICommand ReasonCommand { get; private set; } = null!;

    public ICommand SendReportCommand { get; private set; } = null!;

    public ICommand WithdrawReportCommand { get; private set; } = null!;

    private void InitVoice()
    {
        InputCommand = RelayCommand.Async(() => PickAudioAsync(input: true), () => VoiceEnabled);
        OutputCommand = RelayCommand.Async(() => PickAudioAsync(input: false), () => VoiceEnabled);
        TestOutputCommand = RelayCommand.Async(TestOutputAsync, () => !_playingSound);
        CameraCommand = RelayCommand.Async(PickCameraAsync, () => CameraTestAvailable);
        TestCameraCommand = new RelayCommand(() => Shell.Navigate(new CameraTestViewModel(Shell)), () => CameraTestAvailable);
        ReasonCommand = RelayCommand.Async(PickReasonAsync);
        SendReportCommand = RelayCommand.Async(SendReportAsync, () => CanSendReport);
        WithdrawReportCommand = RelayCommand.Async(() => Launcher.WithdrawReportAsync(), () => !Launcher.Report.Busy);
        ConfigureOnlineCommand = new RelayCommand(Shell.ConfigureOnline);
        SyncMutes();
        SyncOnlineLock();
    }

    // ── locked online: the categories that need a server ──

    /// <summary>The categories that are nothing without a server: a lock in the list, and the "Configure online first" pane instead of theirs.</summary>
    private static readonly HashSet<string> OnlineOnly = ["voice"];

    /// <summary>A server is set (Settings › Online).</summary>
    public bool OnlineEnabled => Launcher.OnlineEnabled;

    public bool OnlineLocked => !OnlineEnabled;

    /// <summary>The chosen category needs a server and there is none: <c>CategoryKey</c> is "locked".</summary>
    private bool CategoryLocked => OnlineLocked && OnlineOnly.Contains(_category.Key);

    /// <summary>The button of every locked place: Settings › Online.</summary>
    public ICommand ConfigureOnlineCommand { get; private set; } = null!;

    /// <summary>Puts the lock on (or takes it off) the entries of <see cref="OnlineOnly"/>; the chosen one stays chosen.</summary>
    private void SyncOnlineLock()
    {
        for (var i = 0; i < Categories.Count; i++)
        {
            var entry = Categories[i];
            var locked = OnlineLocked && OnlineOnly.Contains(entry.Key);
            if (entry.Locked == locked)
            {
                continue;
            }
            var chosen = ReferenceEquals(entry, _category);
            var changed = entry with { Locked = locked };
            if (chosen)
            {
                _category = changed; // before the list tells the view its selected entry is gone
            }
            Categories[i] = changed;
            if (chosen)
            {
                Raise(nameof(Category));
                if (locked)
                {
                    Mic.Stop();
                }
            }
        }
    }

    /// <summary>
    /// Voice &amp; Camera is on screen. Nothing is opened here: the microphone only by its test switch, the camera
    /// only by the Test Camera page. The audio devices are listed (names only, waveIn/waveOut capabilities) when the
    /// game has not reported its own lists yet; the cameras only when the player picks one or tests it.
    /// </summary>
    private void EnterVoice()
    {
        if (CategoryLocked)
        {
            return;
        }
        ReadMod(force: true);
        RelayCommand.Async(LoadDevicesAsync).Execute(null);
    }

    private void LeaveVoice()
    {
        Mic.Stop();
        FlushGains();
    }

    private void RefreshVoice()
    {
        SyncOnlineLock();
        SyncMutes();
        ReadMod(force: false);
    }

    private async Task LoadDevicesAsync()
    {
        if (Launcher.Voice.Inputs.Count > 0 || Launcher.Voice.Outputs.Count > 0 || _labInputs.Count > 0 || _labOutputs.Count > 0)
        {
            return; // the names the game reported (kept in client.toml), or the ones listed earlier: nothing to ask the system
        }
        var lab = Launcher.Devices;
        var inputs = await lab.InputDevicesAsync().ConfigureAwait(true);
        var outputs = await lab.OutputDevicesAsync().ConfigureAwait(true);
        (_labInputs, _labOutputs) = (inputs, outputs);
        RaiseAll();
    }

    // ── voice ──

    /// <summary>"Use voice in private rooms" (voice.enabled; the game polls it).</summary>
    public bool VoiceEnabled
    {
        get => Launcher.Settings.Voice.Enabled;
        set => Save(s => s with { Voice = s.Voice with { Enabled = value } });
    }

    /// <summary>What the game reported is authoritative; until it has run, what this launcher lists itself.</summary>
    private IReadOnlyList<AudioDevice> Inputs => Launcher.Voice.Inputs.Count > 0 ? Launcher.Voice.Inputs : _labInputs;

    private IReadOnlyList<AudioDevice> Outputs => Launcher.Voice.Outputs.Count > 0 ? Launcher.Voice.Outputs : _labOutputs;

    private string DeviceName(string selected, IReadOnlyList<AudioDevice> devices) => selected.Length == 0
        ? CoreStrings.SystemDefault
        : devices.FirstOrDefault(d => d.Id == selected)?.Name ?? S["settings.chat.not_connected"];

    public string InputName => DeviceName(Launcher.Settings.Voice.Input, Inputs);

    public string OutputName => DeviceName(Launcher.Settings.Voice.Output, Outputs);

    /// <summary>Where the device lists come from, said honestly.</summary>
    public string DevicesCaption
    {
        get
        {
            if (Launcher.Voice.Inputs.Count > 0 || Launcher.Voice.Outputs.Count > 0)
            {
                return S["settings.chat.devices_game"];
            }
            return _labInputs.Count == 0 && _labOutputs.Count == 0 ? S["settings.chat.devices_none"] : S["settings.chat.devices_launcher"];
        }
    }

    private async Task PickAudioAsync(bool input)
    {
        var devices = input ? Inputs : Outputs;
        var selected = input ? Launcher.Settings.Voice.Input : Launcher.Settings.Voice.Output;
        var options = new List<ChoiceOption> { new("", CoreStrings.SystemDefault) };
        options.AddRange(devices.Select(d => new ChoiceOption(d.Id, d.Name)));
        if (selected.Length > 0 && devices.All(d => d.Id != selected))
        {
            options.Add(new ChoiceOption(selected, S["settings.chat.not_connected"], S["settings.chat.not_connected_caption"]));
        }
        var picked = await Choice.AskAsync(Shell, S[input ? "settings.chat.input" : "settings.chat.output"], DevicesCaption, options, selected).ConfigureAwait(true);
        if (picked is null || picked == selected)
        {
            return;
        }
        await Shell.UpdateSettingsAsync(s => s with { Voice = input ? s.Voice with { Input = picked } : s.Voice with { Output = picked } }).ConfigureAwait(true);
        Mic.Restart();
    }

    /// <summary>1..100, as the Rust window's slider; saved shortly after the last step, not on every one.</summary>
    public double InputGain
    {
        get => _inputGain ?? Math.Clamp(Launcher.Settings.Voice.InputGain, 1, 100);
        set
        {
            _inputGain = (int)Math.Clamp(Math.Round(value), 1, 100);
            SaveGainsSoon();
        }
    }

    public double OutputGain
    {
        get => _outputGain ?? Math.Clamp(Launcher.Settings.Voice.OutputGain, 1, 100);
        set
        {
            _outputGain = (int)Math.Clamp(Math.Round(value), 1, 100);
            SaveGainsSoon();
        }
    }

    private void SaveGainsSoon()
    {
        if (_gainSave is null)
        {
            _gainSave = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _gainSave.Tick += (_, _) => FlushGains();
        }
        _gainSave.Stop();
        _gainSave.Start();
    }

    private void FlushGains()
    {
        _gainSave?.Stop();
        if (_inputGain is null && _outputGain is null)
        {
            return;
        }
        var (input, output) = (_inputGain, _outputGain);
        RelayCommand.Async(async () =>
        {
            await Shell.UpdateSettingsAsync(s => s with { Voice = s.Voice with { InputGain = input ?? s.Voice.InputGain, OutputGain = output ?? s.Voice.OutputGain } }).ConfigureAwait(true);
            if (_inputGain == input)
            {
                _inputGain = null;
            }
            if (_outputGain == output)
            {
                _outputGain = null;
            }
            Mic.Restart();
        }).Execute(null);
    }

    public bool MicTesting
    {
        get => Mic.Running;
        set
        {
            FlushGains();
            Mic.Running = value;
            Raise();
        }
    }

    public bool HearMyself
    {
        get => Mic.Echo;
        set
        {
            Mic.Echo = value;
            Raise();
        }
    }

    private async Task TestOutputAsync()
    {
        _playingSound = true;
        try
        {
            await Launcher.Devices.PlayTestSoundAsync(Launcher.Settings.Voice.Output, (int)OutputGain).ConfigureAwait(true);
        }
        finally
        {
            _playingSound = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    // ── camera (the launcher's own test) ──

    /// <summary>mkwmp-camera.dll has no capture under Wine / Proton yet: the test is simply not offered there.</summary>
    public bool CameraTestAvailable => !Links.IsWine && Launcher.Devices.CameraAvailable;

    /// <summary>Why there is no camera test, in the player's words; "" when there is one.</summary>
    public string CameraNote => Links.IsWine
        ? S["settings.chat.camera_linux"]
        : Launcher.Devices.CameraAvailable ? "" : Launcher.Devices.CameraProblem ?? CoreStrings.CameraDllMissing;

    public string CameraName
    {
        get
        {
            var voice = Launcher.Settings.Voice;
            var selected = voice.Camera;
            // the cameras are not listed just to show this: the name kept from the last pick or test, until they are
            return selected.Length == 0
                ? S["settings.chat.camera_first"]
                : _cameras.FirstOrDefault(c => c.Id == selected)?.Name
                    ?? (_cameras.Count == 0 ? (voice.CameraName.Length > 0 ? voice.CameraName : selected) : S["settings.chat.not_connected"]);
        }
    }

    public bool CameraMirror
    {
        get => Launcher.Settings.Voice.CameraMirror;
        set => Save(s => s with { Voice = s.Voice with { CameraMirror = value } });
    }

    private async Task PickCameraAsync()
    {
        _cameras = await Launcher.Devices.CameraDevicesAsync().ConfigureAwait(true);
        var selected = Launcher.Settings.Voice.Camera;
        var options = new List<ChoiceOption> { new("", S["settings.chat.camera_first"]) };
        options.AddRange(_cameras.Select(c => new ChoiceOption(c.Id, c.Name)));
        if (selected.Length > 0 && _cameras.All(c => c.Id != selected))
        {
            options.Add(new ChoiceOption(selected, S["settings.chat.not_connected"], selected));
        }
        var picked = await Choice.AskAsync(Shell, S["settings.chat.camera_device"], _cameras.Count == 0 ? S["settings.chat.camera_none"] : "", options, selected).ConfigureAwait(true);
        if (picked is not null && picked != selected)
        {
            var name = _cameras.FirstOrDefault(c => c.Id == picked)?.Name ?? "";
            await Shell.UpdateSettingsAsync(s => s with { Voice = s.Voice with { Camera = picked, CameraName = name } }).ConfigureAwait(true);
        }
        RaiseAll();
    }

    // ── camera in the game (the mod's own per-player settings) ──

    private (IGameTarget Target, GameInstall Install)? ModHost =>
        Launcher is Mkwmp.Core.Launcher real && Launcher.Game.Install is { } install ? (real.Targets.Get(Launcher.Settings.Target), install) : null;

    private bool IsDemo => Launcher is Design.DesignLauncher;

    /// <summary>Reads the mod's switches from the game's settings file (at most every two seconds: it is a file).</summary>
    private void ReadMod(bool force)
    {
        if (!force && DateTime.UtcNow - _modRead < ModRefresh)
        {
            return;
        }
        _modRead = DateTime.UtcNow;
        if (IsDemo)
        {
            _modSupported = true;
            _gameRunning = Launcher.Session.GameRunning;
            foreach (var (key, value) in _demoMod)
            {
                _mod[key] = value;
            }
            return;
        }
        if (ModHost is not { } host || ModSettings.PathFor(host.Install) is null)
        {
            _modSupported = false;
            return;
        }
        _modSupported = true;
        _gameRunning = Launcher.Session.GameRunning || host.Target.IsRunning(host.Install);
        foreach (var key in (string[])[ModSettings.Camera, ModSettings.CameraView, "consent18"])
        {
            _mod[key] = host.Target.ReadModSetting(host.Install, key);
        }
    }

    private bool ModOn(string key) => _mod.GetValueOrDefault(key) == "1";

    private void WriteMod(string key, bool on)
    {
        try
        {
            if (IsDemo)
            {
                _demoMod[key] = on ? "1" : "0";
            }
            else if (ModHost is not { } host || !host.Target.WriteModSetting(host.Install, key, on ? "1" : "0"))
            {
                return;
            }
        }
        catch (LauncherException e)
        {
            Shell.ShowToast(S["settings.chat.game_camera_failed"], e.Message);
        }
        ReadMod(force: true);
        RaiseAll();
    }

    /// <summary>This game keeps MKWMP's camera switches where the launcher can reach them.</summary>
    public bool HasGameCamera => _modSupported;

    /// <summary>The game owns its settings file while it runs.</summary>
    public bool CanChangeGameCamera => _modSupported && !_gameRunning;

    /// <summary>Showing a camera needs a capture: none under Wine / Proton yet (seeing friends' cameras works there).</summary>
    public bool CanShowMyCamera => CanChangeGameCamera && !Links.IsWine;

    public bool ShowMyCamera
    {
        get => ModOn(ModSettings.Camera);
        set => WriteMod(ModSettings.Camera, value);
    }

    public bool SeeFriendsCameras
    {
        get => ModOn(ModSettings.CameraView);
        set => WriteMod(ModSettings.CameraView, value);
    }

    public string GameCameraCaption
    {
        get
        {
            if (_gameRunning)
            {
                return S["settings.chat.game_camera_running"];
            }
            return (ShowMyCamera || SeeFriendsCameras) && !ModOn("consent18")
                ? S["settings.chat.game_camera_caption"] + "\n" + S["settings.chat.game_camera_consent"]
                : S["settings.chat.game_camera_caption"];
        }
    }

    // ── muted players ──

    public bool NoMutes => Mutes.Count == 0;

    /// <summary>In place, so a focused UNMUTE stays where it is while the list is the same.</summary>
    private void SyncMutes()
    {
        var now = Launcher.Voice.Mutes;
        for (var i = Mutes.Count - 1; i >= 0; i--)
        {
            if (now.All(m => m.Account != Mutes[i].Account))
            {
                Mutes.RemoveAt(i);
            }
        }
        foreach (var muted in now)
        {
            if (Mutes.All(m => m.Account != muted.Account))
            {
                Mutes.Add(new MuteRow(muted, m => RelayCommand.Async(() => Launcher.UnmuteAsync(m.Account)).Execute(null), m => ReportName = m.Name));
            }
        }
    }

    // ── report voice abuse: one report waiting for review at a time ──

    /// <summary>Registered and 18+ confirmed: the server may be asked.</summary>
    private bool SignedUp => Launcher.Settings.AdultConsent && Launcher.Profile.SignedIn;

    private ReportView Report => Launcher.Report;

    /// <summary>gate (not signed up) / checking / open (one is waiting) / form.</summary>
    public string ReportState => !SignedUp ? "gate" : Report.Loaded != true ? "checking" : Report.Open is not null ? "open" : "form";

    public string ReportChecking => Report.Busy || Report.Loaded is null ? CoreStrings.ReportChecking : CoreStrings.ReportCantCheck;

    private string ReasonLabel(string code) => Report.Reasons.FirstOrDefault(r => r.Code == code)?.Label ?? code;

    public string OpenReportText => Report.Open is { } open
        ? S.Format("settings.report.waiting", ("name", open.Reported), ("reason", ReasonLabel(open.Reason)))
        : "";

    public string ReportName
    {
        get => _reportName;
        set
        {
            var clean = new string([.. (value ?? "").Where(c => !char.IsWhiteSpace(c)).Take(16)]);
            if (Set(ref _reportName, clean))
            {
                CommandManager.InvalidateRequerySuggested();
            }
            else if (clean != value)
            {
                Raise();
            }
        }
    }

    private string ReasonCode => Report.Reasons.Any(r => r.Code == _reportReason) ? _reportReason : (Report.Reasons.Count > 0 ? Report.Reasons[0].Code : "");

    public string ReportReasonName => ReasonLabel(ReasonCode);

    public string ReportDetails
    {
        get => _reportDetails;
        set => Set(ref _reportDetails, value is { Length: > CoreStrings.ReportDetailsMax } ? value[..CoreStrings.ReportDetailsMax] : value ?? "");
    }

    public static int ReportDetailsMax => CoreStrings.ReportDetailsMax;

    public bool CanSendReport => !Report.Busy && Links.ValidName(_reportName)
        && !string.Equals(_reportName, Launcher.Settings.Name, StringComparison.OrdinalIgnoreCase);

    public string ReportNote => Report.Note ?? "";

    public bool ReportNoteIsError => Report.NoteIsError;

    private async Task PickReasonAsync()
    {
        var picked = await Choice.AskAsync(Shell, S["settings.report.what"], "", [.. Report.Reasons.Select(r => new ChoiceOption(r.Code, r.Label))], ReasonCode).ConfigureAwait(true);
        if (picked is not null)
        {
            _reportReason = picked;
            Raise(nameof(ReportReasonName));
        }
    }

    private async Task SendReportAsync()
    {
        var (name, reason, details) = (_reportName, ReasonCode, _reportDetails.Trim());
        (_reportName, _reportDetails, _reportReason) = ("", "", "");
        RaiseAll();
        await Launcher.SendReportAsync(name, reason, details.Length == 0 ? null : details).ConfigureAwait(true);
    }
}
