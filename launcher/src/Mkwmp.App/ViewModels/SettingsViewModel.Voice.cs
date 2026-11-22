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

/// <summary>A muted player: UNMUTE, or REPORT (opens Settings › Online › Report Abuse with the name filled in).</summary>
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
/// Settings › Voice &amp; Camera, two sections built the same way: a switch ("Use voice / camera in
/// private rooms"), then the device, then its test. Voice: the switch, devices and volumes are
/// client.toml's (the game polls them through the local API). Camera: the switch is the mod's two
/// settings in the game's own file (<see cref="ModSettings.Camera"/>, <see cref="ModSettings.CameraView"/>),
/// changed while the game is closed; "Show My Camera" under it is the one finer choice. Then the
/// muted players. Voice and camera exist only inside the game (private rooms, friends added on the
/// server, up to four at a time); this pane only configures and tests. Locked until a server is
/// set, as every category of <see cref="OnlineOnly"/>.
/// </summary>
public sealed partial class SettingsViewModel
{
    private static readonly TimeSpan ModRefresh = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, string?> _mod = [];
    private readonly Dictionary<string, string?> _demoMod = new() { [ModSettings.Camera] = "1", [ModSettings.CameraView] = "1", ["consent18"] = "1" };

    /// <summary>The harness: the mod's file as another launcher or the game left it ("camera=0,camera_view=1"), then read again.</summary>
    internal void PretendMod(string pairs)
    {
        foreach (var pair in pairs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (pair.Split('=') is [var key, var value])
            {
                _demoMod[key] = value;
            }
        }
        ReadMod(force: true);
        RaiseAll();
    }
    private IReadOnlyList<AudioDevice> _labInputs = [];
    private IReadOnlyList<AudioDevice> _labOutputs = [];
    private IReadOnlyList<CameraDevice> _cameras = [];
    private DispatcherTimer? _gainSave;
    private int? _inputGain;
    private int? _outputGain;
    private DateTime _modRead;
    private bool _gameRunning;
    private bool _modSupported;
    private bool _playingSound;

    /// <summary>The microphone test (level meter, hear myself).</summary>
    public MicTester Mic { get; }

    public ObservableCollection<MuteRow> Mutes { get; } = [];

    public ICommand InputCommand { get; private set; } = null!;

    public ICommand OutputCommand { get; private set; } = null!;

    public ICommand TestOutputCommand { get; private set; } = null!;

    public ICommand CameraCommand { get; private set; } = null!;

    public ICommand TestCameraCommand { get; private set; } = null!;

    private void InitVoice()
    {
        InputCommand = RelayCommand.Async(() => PickAudioAsync(input: true));
        OutputCommand = RelayCommand.Async(() => PickAudioAsync(input: false));
        TestOutputCommand = RelayCommand.Async(TestOutputAsync, () => !_playingSound);
        CameraCommand = RelayCommand.Async(PickCameraAsync, () => CameraTestAvailable);
        TestCameraCommand = new RelayCommand(() => Shell.Navigate(new CameraTestViewModel(Shell)), () => CameraTestAvailable);
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
        set
        {
            if (!value)
            {
                Mic.Stop(); // its test goes with the rows that are no longer there
                FlushGains();
            }
            Save(s => s with { Voice = s.Voice with { Enabled = value } });
        }
    }

    /// <summary>What the game reported is authoritative; until it has run, what this launcher lists itself.</summary>
    private IReadOnlyList<AudioDevice> Inputs => Launcher.Voice.Inputs.Count > 0 ? Launcher.Voice.Inputs : _labInputs;

    private IReadOnlyList<AudioDevice> Outputs => Launcher.Voice.Outputs.Count > 0 ? Launcher.Voice.Outputs : _labOutputs;

    private string DeviceName(string selected, IReadOnlyList<AudioDevice> devices) => selected.Length == 0
        ? CoreStrings.SystemDefault
        : devices.FirstOrDefault(d => d.Id == selected)?.Name ?? S["settings.chat.not_connected"];

    public string InputName => DeviceName(Launcher.Settings.Voice.Input, Inputs);

    public string OutputName => DeviceName(Launcher.Settings.Voice.Output, Outputs);

    /// <summary>Where the device lists come from, said honestly (in the list a device is picked from).</summary>
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

    /// <summary>The camera's device and test rows are shown (greyed out while mkwmp-camera.dll is missing); not under Wine, where nothing can be captured.</summary>
#pragma warning disable CA1822 // XAML binds to instance properties
    public bool CanTestCamera => CanCapture;
#pragma warning restore CA1822

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

    /// <summary>Writes the switches that differ from the file (each a rewrite of it); a refusal is a toast and the pane shows the file again.</summary>
    private void WriteMod(params (string Key, bool On)[] changes)
    {
        try
        {
            foreach (var (key, on) in changes)
            {
                if (ModOn(key) == on && _mod.GetValueOrDefault(key) is not null)
                {
                    continue;
                }
                if (IsDemo)
                {
                    _demoMod[key] = on ? "1" : "0";
                }
                else if (ModHost is not { } host || !host.Target.WriteModSetting(host.Install, key, on ? "1" : "0"))
                {
                    break;
                }
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
    private static bool CanCapture => !Links.IsWine;

    /// <summary>
    /// "Use camera in private rooms": one switch for the mod's two (<see cref="ModSettings.Camera"/>, my camera is
    /// shown; <see cref="ModSettings.CameraView"/>, I see my friends'). On when either is on, so what an older
    /// launcher saved reads as it was meant: any camera use is "on". Turning it on sets both (showing mine only
    /// where there is a capture), off clears both; <see cref="ShowMyCamera"/> is the one finer choice under it.
    /// </summary>
    public bool CameraEnabled
    {
        get => ModOn(ModSettings.Camera) || ModOn(ModSettings.CameraView);
        set => WriteMod((ModSettings.CameraView, value), (ModSettings.Camera, value && CanCapture));
    }

    public bool CanShowMyCamera => CanChangeGameCamera && CanCapture;

    /// <summary>
    /// "Show My Camera". With the camera in use, friends' cameras are seen: a file an older launcher left with only
    /// "show mine" on gets "see friends'" with the next change made here.
    /// </summary>
    public bool ShowMyCamera
    {
        get => ModOn(ModSettings.Camera);
        set => WriteMod((ModSettings.CameraView, true), (ModSettings.Camera, value));
    }

    /// <summary>Under the switch: what it does; why it cannot be changed now; the 18+ confirmation still missing.</summary>
    public string CameraCaption
    {
        get
        {
            if (!_modSupported)
            {
                return Launcher.Game.Install is null ? S["settings.game.not_found"] : ModSettings.NoProfileYet;
            }
            if (_gameRunning)
            {
                return S["settings.chat.game_camera_running"];
            }
            return CameraEnabled && !ModOn("consent18")
                ? S["settings.chat.camera_enabled_caption"] + "\n" + S["settings.chat.game_camera_consent"]
                : S["settings.chat.camera_enabled_caption"];
        }
    }

    /// <summary>Under "Show My Camera": what off means, or that this system cannot capture a camera yet.</summary>
    public string ShowMineCaption => CanCapture ? S["settings.chat.show_mine_caption"] : S["settings.chat.camera_linux"];

    /// <summary>Why there is no camera device to choose or test on Windows (mkwmp-camera.dll); "" otherwise (Linux says it under Show My Camera).</summary>
    public string CameraDeviceNote => Links.IsWine || Launcher.Devices.CameraAvailable ? "" : Launcher.Devices.CameraProblem ?? CoreStrings.CameraDllMissing;
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
                Mutes.Add(new MuteRow(muted, m => RelayCommand.Async(() => Launcher.UnmuteAsync(m.Account)).Execute(null), m => Shell.Navigate(new ReportViewModel(Shell, m.Name))));
            }
        }
    }
}
