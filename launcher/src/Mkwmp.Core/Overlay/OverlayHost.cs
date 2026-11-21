// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Mkwmp.Core.Net;

namespace Mkwmp.Core.Overlay;

/// <summary>
/// <c>mkwmp.exe --overlay-host --parent-pid N --session FILE</c>: the mod inside a game
/// that no launcher started asks for one to stand by as its overlay (no window until the player
/// asks, no seat, gone with the game). <see cref="SessionPath"/> is where the mod reads how to
/// reach it.
/// </summary>
public sealed record OverlayHostArgs(int ParentPid, string SessionPath)
{
    /// <summary>The only file name a session may have: the mod looks for it in its own folder.</summary>
    public const string SessionFile = "mkwmp-overlay.toml";

    /// <summary>Null when the command line does not ask for the overlay host (or asks for it wrongly).</summary>
    public static OverlayHostArgs? Parse(IReadOnlyList<string> args)
    {
        if (!args.Contains("--overlay-host"))
        {
            return null;
        }
        var pid = 0;
        string? session = null;
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] == "--parent-pid")
            {
                _ = int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out pid);
            }
            else if (args[i] == "--session")
            {
                session = args[i + 1];
            }
        }
        return pid > 0 && session is not null && Valid(session) ? new OverlayHostArgs(pid, Path.GetFullPath(session)) : null;
    }

    /// <summary>A session file is named <see cref="SessionFile"/> and goes into a folder that exists.</summary>
    internal static bool Valid(string session)
    {
        try
        {
            var full = Path.GetFullPath(session);
            return Path.GetFileName(full) == SessionFile && Directory.Exists(Path.GetDirectoryName(full));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <summary>
/// <c>launcher.json</c>: where this launcher is and, while it runs, how to reach it. Portable:
/// it lives beside the launcher's exe (a second launcher of that folder reads it to hand over to
/// the running one), and a copy goes into the game's mod folder (<c>mods\mkwmp\launcher.json</c>),
/// where the mod reads <c>exe</c> to start the launcher as its overlay host. Nothing is kept in the
/// user's profile.
/// </summary>
public sealed record LauncherRecord(string Exe, int Pid, int Port, string Token)
{
    public static string DefaultPath => Path.Combine(LauncherOptions.LauncherFolder, "launcher.json");

    /// <summary>Writes the record; false when the file cannot be written (the overlay host then cannot be found).</summary>
    public bool Write(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { exe = Exe, pid = Pid, port = Port, token = Token }), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static LauncherRecord? Read(string? path = null)
    {
        try
        {
            JsonElement? v = Json.Parse(File.ReadAllBytes(path ?? DefaultPath));
            var exe = v.At("exe").Str();
            var token = v.At("token").Str();
            if (exe is null || token is null || v.At("pid") is not { ValueKind: JsonValueKind.Number } pid || v.At("port") is not { ValueKind: JsonValueKind.Number } port)
            {
                return null;
            }
            return new LauncherRecord(exe, pid.GetInt32(), port.GetInt32(), token);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// One launcher per user. The first holds a named mutex; a later one (the player opening the
/// launcher while the overlay host stands by, or a mod asking for a host while the launcher is
/// open) hands its request to it over the local API and exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>The instance, or null when another launcher of this user already runs.</summary>
    public static SingleInstance? TryAcquire(string name = "mkwmp-launcher")
    {
        var mutex = new Mutex(initiallyOwned: false, $@"Local\{name}");
        try
        {
            if (mutex.WaitOne(0))
            {
                return new SingleInstance(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            return new SingleInstance(mutex); // the one before crashed: this one takes over
        }
        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Tells the running launcher to show its window (<paramref name="host"/> null) or to stand by
    /// for that game. False when it does not answer: the caller may wait and try again, or start anyway.
    /// </summary>
    public static async Task<bool> HandOffAsync(OverlayHostArgs? host, LauncherRecord? running = null, CancellationToken cancel = default)
    {
        running ??= LauncherRecord.Read();
        if (running is null || running.Port is <= 0 or > 65535)
        {
            return false;
        }
        var path = host is null
            ? "/local/v1/instance/show"
            : $"/local/v1/instance/host?pid={host.ParentPid.ToString(CultureInfo.InvariantCulture)}&session={Uri.EscapeDataString(host.SessionPath)}";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{running.Port.ToString(CultureInfo.InvariantCulture)}{path}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", running.Token);
            using var response = await http.SendAsync(request, cancel).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // disposed on another thread than the one that took it: the handle's end frees it
        }
        _mutex.Dispose();
    }
}

/// <summary>
/// The launcher standing by for a game it did not start (launcher/docs/OVERLAY.md, "Without the
/// launcher"). <see cref="Attach"/> writes the session file the mod waits for and watches the
/// game; <see cref="ParentExited"/> tells the shell to leave if it still has no window of its own.
/// Also takes the two requests of a second launcher (LocalApi: /local/v1/instance/...).
/// Events fire on a thread-pool thread.
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private string? _session;
    private int _parent;

    /// <summary>A second launcher was started by the player: show the window (and stop being only a host).</summary>
    public event EventHandler? ShowRequested;

    /// <summary>The game this launcher stands by for ended.</summary>
    public event EventHandler? ParentExited;

    /// <summary>The launcher now stands by for a game (<see cref="ParentPid"/>).</summary>
    public event EventHandler? ParentAttached;

    /// <summary>The game stood by for (0: none).</summary>
    public int ParentPid
    {
        get
        {
            lock (_gate)
            {
                return _parent;
            }
        }
    }

    /// <summary>
    /// Stands by for that game: writes its session file (the mod's "ready") and raises
    /// <see cref="ParentExited"/> when the game ends. False when the file cannot be written or
    /// the game is not running.
    /// </summary>
    public bool Attach(OverlayHostArgs args, int port, string token)
    {
        if (port is <= 0 or > 65535 || !OverlayHostArgs.Valid(args.SessionPath))
        {
            return false;
        }
        Process game;
        try
        {
            game = Process.GetProcessById(args.ParentPid);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        try
        {
            var temp = args.SessionPath + ".tmp";
            File.WriteAllText(temp, $"launcher = \"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}\"\nlauncher_token = \"{token}\"\n", new UTF8Encoding(false));
            File.Move(temp, args.SessionPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            game.Dispose();
            return false;
        }
        lock (_gate)
        {
            _session = args.SessionPath;
            _parent = args.ParentPid;
        }
        ParentAttached?.Invoke(this, EventArgs.Empty);
        _ = WatchAsync(game, args);
        return true;
    }

    private async Task WatchAsync(Process game, OverlayHostArgs args)
    {
        using (game)
        {
            try
            {
                await game.WaitForExitAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
            {
                return;
            }
        }
        lock (_gate)
        {
            if (_parent != args.ParentPid)
            {
                return; // standing by for a newer game already
            }
            _parent = 0;
            _session = null;
        }
        Forget(args.SessionPath);
        ParentExited?.Invoke(this, EventArgs.Empty);
    }

    private static void Forget(string session)
    {
        try
        {
            File.Delete(session);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a stale file names a token nobody answers to
        }
    }

    // ---- the routes (LocalApi) ---------------------------------------------------------------

    /// <summary>POST /local/v1/instance/show.</summary>
    internal LocalResponse Show()
    {
        ShowRequested?.Invoke(this, EventArgs.Empty);
        return new LocalResponse(204);
    }

    /// <summary>POST /local/v1/instance/host?pid=N&amp;session=FILE.</summary>
    internal LocalResponse Host(IReadOnlyDictionary<string, string> query, int port, string token)
    {
        if (!int.TryParse(query.GetValueOrDefault("pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0
            || query.GetValueOrDefault("session") is not { } session || !OverlayHostArgs.Valid(session))
        {
            return new LocalResponse(400, JsonSerializer.Serialize(new { state = "invalid", message = $"pid and session (a file named {OverlayHostArgs.SessionFile})" }));
        }
        return Attach(new OverlayHostArgs(pid, Path.GetFullPath(session)), port, token)
            ? new LocalResponse(204)
            : new LocalResponse(409, JsonSerializer.Serialize(new { state = "refused", message = "the game is not running, or its session file cannot be written" }));
    }

    public void Dispose()
    {
        _stop.Cancel();
        string? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is not null)
        {
            Forget(session);
        }
        _stop.Dispose();
    }
}
