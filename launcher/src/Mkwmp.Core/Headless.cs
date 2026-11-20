// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;
using Mkwmp.Core.Net;
using Mkwmp.Core.Targets;

namespace Mkwmp.Core;

/// <summary>
/// The launcher without a window (<c>--headless</c>, <c>--save-logs</c>): what scripts, services
/// and the two-client test rigs run. Messages go to the console and the session log.
/// </summary>
public static class Headless
{
    private const uint AttachParentProcess = 0xFFFF_FFFF;

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int AttachConsole(uint processId);

    /// <summary>A windowed program has no console of its own: borrow the terminal's when one started us (for --version, --help, --headless).</summary>
    public static void AttachParentConsole()
    {
        try
        {
            _ = AttachConsole(AttachParentProcess);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // not Windows enough: stay silent
        }
    }

    private static int Fatal(SessionLog? log, string message)
    {
        Console.Error.WriteLine($"mkwmp: {message}");
        log?.Line($"fatal: {message}");
        return 1;
    }

    /// <summary><c>--save-logs</c>: writes the bug-report zip and says where; the exit code.</summary>
    public static int SaveLogs(LauncherOptions options, string? launcherFolder = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var folder = launcherFolder ?? LauncherOptions.LauncherFolder;
        var path = options.ConfigPath ?? LauncherOptions.DefaultConfigPath(folder);
        var version = options.Version ?? LauncherOptions.AssemblyVersion;
        try
        {
            var settings = ClientConfig.LoadOrDefault(path).Settings;
            var targets = new TargetRegistry(options.Targets ?? [new Targets.MkwRecomp.MkwRecompTarget(version)]);
            var target = targets.Get(settings.Target);
            var (_, zip) = SupportBundle.Save(folder, version, target, GameFinder.Detect(target, settings.Game));
            Console.WriteLine($"mkwmp: {Strings.Saved(zip)}");
            return 0;
        }
        catch (Exception e) when (e is LauncherException or IOException)
        {
            return Fatal(null, e.Message);
        }
    }

    /// <summary>
    /// <c>--headless</c>: update, register the name when it is free (the PIN is printed once),
    /// PLAY, and stay until the session ends or <paramref name="cancel"/> (Ctrl-C). The exit code.
    /// </summary>
    public static async Task<int> RunHeadlessAsync(LauncherOptions options, CancellationToken cancel, string? launcherFolder = null, HttpMessageHandler? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var folder = launcherFolder ?? LauncherOptions.LauncherFolder;
        options = options with { Headless = true };
        Launcher launcher;
        try
        {
            launcher = new Launcher(options.ConfigPath ?? LauncherOptions.DefaultConfigPath(folder), folder, options, http);
        }
        catch (LauncherException e)
        {
            return Fatal(null, e.Message);
        }
        await using (launcher.ConfigureAwait(false))
        {
            await launcher.StartAsync(cancel).ConfigureAwait(false);
            if (!launcher.LocalApiUp)
            {
                return Fatal(launcher.Log, $"cannot open 127.0.0.1:{launcher.Settings.LocalPort}; is another launcher running?");
            }
            try
            {
                await launcher.StartupUpdates.WaitAsync(cancel).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            // No PIN yet: register the name if it's free (the PIN is printed once).
            var s = launcher.Settings;
            // (With no server in client.toml the launcher is offline: nothing is registered, and PLAY starts the game alone.)
            if (Launcher.IsOnline(s) && s.Pin.Length == 0 && ClientConfig.ValidName(s.Name) && s.AdultConsent)
            {
                try
                {
                    var api = new SeatApi(launcher.HttpClient, s.Server, $"mkwmp-launcher/{launcher.Version}");
                    if (await api.ProfileAsync(s.Name, cancel).ConfigureAwait(false) is not (false, false))
                    {
                        return Fatal(launcher.Log, $"{s.Name} is registered: put its 6-digit PIN in client.toml (pin = \"123456\")");
                    }
                    await launcher.RegisterAsync(s.Name).ConfigureAwait(false);
                    if (launcher.Profile.Register is RegisterState.Failed or RegisterState.Taken)
                    {
                        return Fatal(launcher.Log, launcher.Profile.Message ?? Strings.CouldNotRegister);
                    }
                    Console.WriteLine($"mkwmp: registered {s.Name}. Your PIN is {launcher.Settings.Pin}: write it down, it is not shown again.");
                }
                catch (ApiException e)
                {
                    return Fatal(launcher.Log, e.Message);
                }
                catch (OperationCanceledException)
                {
                    return 0;
                }
            }
            s = launcher.Settings;
            if (Launcher.ConfigProblem(s, launcher.Game.Install is not null, launcher.Targets.Get(s.Target).FolderHint) is { } problem)
            {
                return Fatal(launcher.Log, problem);
            }
            await launcher.PlayAsync().ConfigureAwait(false);
            while (true)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    launcher.Log.Note("terminated; releasing the seat");
                    return 0; // disposing frees the seat and signs out
                }
                var session = launcher.Session;
                if (session.State == SessionState.Idle)
                {
                    return 0;
                }
                if (session.State == SessionState.Failed)
                {
                    return Fatal(launcher.Log, session.Status);
                }
            }
        }
    }
}
