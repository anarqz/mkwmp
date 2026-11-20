// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Text.Json;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Overlay;

/// <summary>What opened the overlay.</summary>
public enum OverlayTrigger
{
    /// <summary>The Guide (Xbox, PS, Home) button, seen by the mod.</summary>
    Guide,

    /// <summary>Select + Start together.</summary>
    Chord,

    /// <summary>Select held for a second (what always reaches the game under Steam Input).</summary>
    Hold,

    /// <summary>Shift+Tab on the keyboard.</summary>
    Keys,

    /// <summary>The launcher itself (its own Guide watch, for a game without the mod).</summary>
    Launcher,
}

/// <summary>How the player leaves the overlay.</summary>
public enum OverlayReturn
{
    /// <summary>Back to the game.</summary>
    Resume,

    /// <summary>Back to the game with mkw-recomp's own menu open (plugin API 4).</summary>
    Menu,

    /// <summary>
    /// "Quit Game": the game ends itself as by its own menu's Quit (saves finished, the seat given
    /// back), when the mod said it can (<see cref="OverlayBridge.GameHasQuit"/>); the shell closes
    /// the game's window when it cannot or does not.
    /// </summary>
    Quit,
}

/// <summary>
/// The game's picture when the overlay was asked for: <see cref="Width"/> x <see cref="Height"/>
/// pixels, four bytes each in the order blue, green, red, 255 (WPF's Bgr32), rows top to bottom
/// with no padding.
/// </summary>
public sealed record OverlayFrame(int Width, int Height, byte[] Bgra)
{
    public const int MaxWidth = 1920;
    public const int MaxHeight = 1080;

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 4;

    /// <summary>From the mod's RGBA8 rows; null when the size and the bytes do not match or are out of range.</summary>
    public static OverlayFrame? FromRgba(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width < 16 || height < 16 || width > MaxWidth || height > MaxHeight || rgba.Length != width * height * 4)
        {
            return null;
        }
        var bgra = new byte[rgba.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = 255;
        }
        return new OverlayFrame(width, height, bgra);
    }

    /// <summary>True when every pixel is (nearly) black: a capture that got nothing.</summary>
    public bool IsBlank()
    {
        for (var i = 0; i < Bgra.Length; i += 4 * 97)
        {
            if (Bgra[i] > 8 || Bgra[i + 1] > 8 || Bgra[i + 2] > 8)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>One request to bring the launcher over the game.</summary>
/// <param name="By">What the player pressed.</param>
/// <param name="GamePid">The game's process id (0: not told).</param>
/// <param name="Frame">The game's picture, or null (plugin API 3, a minimised window).</param>
/// <param name="Again">The overlay was already open: the game took the focus back and the player asked again.</param>
public sealed record OverlayRequest(OverlayTrigger By, int GamePid, OverlayFrame? Frame, bool Again);

/// <summary>
/// The launcher's half of the console-style overlay (launcher/docs/OVERLAY.md; the contract is
/// "The overlay" in docs/contracts/LAUNCHER_API.md). The mod inside the game asks to open it with
/// the picture the game was showing; the shell shows itself over the game and, when the player
/// goes back, calls <see cref="ReturnToGame"/>, which the mod is waiting for.
/// Every member is safe from any thread; the events fire on the thread that caused them (the
/// local API's, or the caller's): the shell dispatches to its own.
/// </summary>
public sealed class OverlayBridge(TimeProvider clock)
{
    /// <summary>The longest GET /local/v1/overlay/state waits for a change.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(25);

    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _open;
    private long _seq;
    private OverlayReturn _last = OverlayReturn.Resume;
    private OverlayFrame? _frame;
    private int _gamePid;
    private bool _gameHasMenu;
    private bool _gameHasQuit;
    private long? _seenAt;

    /// <summary>The game asked for the overlay (or asked again while it is open).</summary>
    public event EventHandler<OverlayRequest>? Requested;

    /// <summary>The overlay closed: <see cref="ReturnToGame"/>, or the game went away.</summary>
    public event EventHandler<OverlayReturn>? Closed;

    /// <summary>
    /// False until the shell can show itself over a game (and while it must not: setup, an update
    /// being applied). The mod is then told no, and gives the button back to mkw-recomp's menu.
    /// </summary>
    public bool Enabled { get; set; }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _open;
            }
        }
    }

    /// <summary>The picture of the last request that had one: the Play tile's thumbnail.</summary>
    public OverlayFrame? LastFrame
    {
        get
        {
            lock (_gate)
            {
                return _frame;
            }
        }
    }

    /// <summary>The game's process id as the mod told it (0: never told).</summary>
    public int GamePid
    {
        get
        {
            lock (_gate)
            {
                return _gamePid;
            }
        }
    }

    /// <summary>
    /// The game that asked last can open mkw-recomp's own menu when told to (<c>menu=1</c> in its
    /// request: plugin API 4): only then is <see cref="OverlayReturn.Menu"/> worth offering.
    /// </summary>
    public bool GameHasMenu
    {
        get
        {
            lock (_gate)
            {
                return _gameHasMenu;
            }
        }
    }

    /// <summary>
    /// The game that asked last ends itself cleanly when told to (<c>quit=1</c> in its request: the
    /// runtime's <c>quit</c> entry): <see cref="OverlayReturn.Quit"/> is then waited for before
    /// the shell closes the game's window itself.
    /// </summary>
    public bool GameHasQuit
    {
        get
        {
            lock (_gate)
            {
                return _gameHasQuit;
            }
        }
    }

    /// <summary>A mod with the overlay asked for its state in the last 30 s: the hint bar can name the button.</summary>
    public bool GameListening
    {
        get
        {
            lock (_gate)
            {
                return _seenAt is { } at && clock.GetElapsedTime(at) < TimeSpan.FromSeconds(30);
            }
        }
    }

    /// <summary>
    /// The player goes back to the game: the mod's wait ends and the game reads the pad again
    /// once every button is up. The shell gives the focus back itself (GameWindow.Activate).
    /// False when the overlay was not open.
    /// </summary>
    public bool ReturnToGame(OverlayReturn how = OverlayReturn.Resume) => Close(how);

    /// <summary>The game ended: an open overlay is just the launcher again. Also forgets the picture.</summary>
    public void GameExited()
    {
        Close(OverlayReturn.Resume);
        lock (_gate)
        {
            _frame = null;
            _gamePid = 0;
            _gameHasMenu = false;
            _gameHasQuit = false;
            _seenAt = null;
        }
    }

    /// <summary>
    /// The launcher opens the overlay itself (its own Guide watch, for a game without the mod):
    /// the same <see cref="Requested"/>, with <see cref="OverlayTrigger.Launcher"/>. False when it is not enabled.
    /// <paramref name="gameHasMenu"/> is for the demo: a game without the mod cannot be told to open its menu.
    /// </summary>
    public bool RequestFromLauncher(int gamePid, OverlayFrame? frame, bool gameHasMenu = false) =>
        Request(new OverlayRequest(OverlayTrigger.Launcher, gamePid, frame, Again: false), gameHasMenu, hasQuit: false) is not null;

    private bool Close(OverlayReturn how)
    {
        lock (_gate)
        {
            if (!_open)
            {
                return false;
            }
            _open = false;
            _last = how;
            Bump();
        }
        Closed?.Invoke(this, how);
        return true;
    }

    /// <summary>Under the gate: a new state for the waiting poll.</summary>
    private void Bump()
    {
        _seq++;
        var done = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        done.TrySetResult();
    }

    /// <summary>The state after the request, or null when the overlay cannot open now.</summary>
    private string? Request(OverlayRequest request, bool hasMenu, bool hasQuit)
    {
        var handlers = Requested;
        if (!Enabled || handlers is null)
        {
            return null;
        }
        string state;
        lock (_gate)
        {
            request = request with { Again = _open };
            _gameHasMenu = hasMenu;
            _gameHasQuit = hasQuit;
            if (request.Frame is { } frame)
            {
                _frame = frame;
            }
            if (request.GamePid != 0)
            {
                _gamePid = request.GamePid;
            }
            if (!_open)
            {
                _open = true;
                Bump();
            }
            state = StateJson();
        }
        handlers.Invoke(this, request);
        return state;
    }

    /// <summary>Under the gate.</summary>
    private string StateJson()
    {
        var enabled = Enabled && Requested is not null;
        return _open || _seq == 0
            ? JsonSerializer.Serialize(new { open = _open, seq = _seq, enabled })
            : JsonSerializer.Serialize(new { open = _open, seq = _seq, enabled, action = _last switch { OverlayReturn.Menu => "menu", OverlayReturn.Quit => "quit", _ => "resume" } });
    }

    // ---- the routes (LocalApi) ---------------------------------------------------------------

    /// <summary>POST /local/v1/overlay/open?by=..&amp;pid=..[&amp;menu=1][&amp;quit=1][&amp;w=..&amp;h=..&amp;format=rgba] with the picture as the body.</summary>
    internal LocalResponse Open(IReadOnlyDictionary<string, string> query, ReadOnlyMemory<byte> body)
    {
        OverlayTrigger? by = query.GetValueOrDefault("by") switch
        {
            "guide" => OverlayTrigger.Guide,
            "chord" => OverlayTrigger.Chord,
            "hold" => OverlayTrigger.Hold,
            "keys" => OverlayTrigger.Keys,
            _ => null,
        };
        if (by is null)
        {
            return Invalid("by: guide, chord, hold or keys");
        }
        var pid = 0;
        if (query.TryGetValue("pid", out var rawPid) && !int.TryParse(rawPid, NumberStyles.None, CultureInfo.InvariantCulture, out pid))
        {
            return Invalid("pid: a process id");
        }
        OverlayFrame? frame = null;
        if (body.Length > 0)
        {
            if (query.GetValueOrDefault("format") != "rgba"
                || !int.TryParse(query.GetValueOrDefault("w"), NumberStyles.None, CultureInfo.InvariantCulture, out var w)
                || !int.TryParse(query.GetValueOrDefault("h"), NumberStyles.None, CultureInfo.InvariantCulture, out var h)
                || (frame = OverlayFrame.FromRgba(body.Span, w, h)) is null)
            {
                return Invalid($"a picture is format=rgba with w and h (16..{OverlayFrame.MaxWidth} x 16..{OverlayFrame.MaxHeight}) and w*h*4 bytes");
            }
            if (frame.IsBlank())
            {
                frame = null; // a capture that got nothing is no thumbnail
            }
        }
        Seen();
        return Request(new OverlayRequest(by.Value, pid, frame, Again: false), hasMenu: query.GetValueOrDefault("menu") == "1", hasQuit: query.GetValueOrDefault("quit") == "1") is { } state
            ? new LocalResponse(200, state)
            : new LocalResponse(409, JsonSerializer.Serialize(new { open = false, seq = Seq(), enabled = false }));
    }

    /// <summary>GET /local/v1/overlay/state[?wait=S&amp;seq=N]: now, or when the state is no longer N (at most S seconds later).</summary>
    internal async Task<LocalResponse> StateAsync(IReadOnlyDictionary<string, string> query, CancellationToken cancel)
    {
        Seen();
        var wait = TimeSpan.Zero;
        if (query.TryGetValue("wait", out var rawWait))
        {
            if (!int.TryParse(rawWait, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            {
                return Invalid("wait: seconds");
            }
            wait = TimeSpan.FromSeconds(Math.Min(seconds, MaxWait.TotalSeconds));
        }
        long? known = null;
        if (query.TryGetValue("seq", out var rawSeq))
        {
            if (!long.TryParse(rawSeq, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
            {
                return Invalid("seq: a number");
            }
            known = seq;
        }
        Task changed;
        lock (_gate)
        {
            if (wait == TimeSpan.Zero || known != _seq)
            {
                return new LocalResponse(200, StateJson());
            }
            changed = _changed.Task;
        }
        try
        {
            await changed.WaitAsync(wait, clock, cancel).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // nothing changed: the same state again
        }
        lock (_gate)
        {
            return new LocalResponse(200, StateJson());
        }
    }

    private long Seq()
    {
        lock (_gate)
        {
            return _seq;
        }
    }

    private void Seen()
    {
        lock (_gate)
        {
            _seenAt = clock.GetTimestamp();
        }
    }

    private static LocalResponse Invalid(string message) =>
        new(400, JsonSerializer.Serialize(new { state = "invalid", message }));
}
