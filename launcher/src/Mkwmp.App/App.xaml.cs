// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Mkwmp.App.Design;
using Mkwmp.App.ViewModels;
using Mkwmp.Core;

namespace Mkwmp.App;

public partial class App : Application
{
    private ILauncher? _launcher;
    private Overlay.OverlayStartup? _startup;

    public App()
    {
        // A fault in one action never takes the launcher (and the play session it holds) down:
        // it is written to the log, shown as a toast, and the shell keeps running.
        DispatcherUnhandledException += OnDispatcherUnhandled;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("unobserved task: " + e.Exception);
            e.SetObserved();
        };
        InstallFatalLog(Environment.GetCommandLineArgs());
    }

    /// <summary>
    /// What the two handlers above cannot catch (a fault on another thread, a native fault, a stack
    /// overflow, a kill) goes to logs/launcher-crash.log (Mkwmp.Core.Diagnostics.FatalLog): under Proton
    /// such an end left no trace at all. Not for the console modes (their standard error is the
    /// terminal's), the screenshot harness and the demo.
    /// </summary>
    private static void InstallFatalLog(string[] args)
    {
        string[] not = ["--headless", "--save-logs", "--version", "-V", "--help", "-h", "--shot", "--icon"];
        if (args.Any(a => not.Contains(a, StringComparer.OrdinalIgnoreCase) || a.StartsWith("--demo", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        var before = Mkwmp.Core.Diagnostics.FatalLog.Install(
            LauncherOptions.LauncherFolder,
            LauncherOptions.AssemblyVersion,
            standardError: !Mkwmp.Core.Diagnostics.FatalLog.HasStandardError || Mkwmp.Core.Platform.WineHost.Detected);
        if (before is not null)
        {
            Log($"the launcher's last session ended without saying so (a crash or a kill); {Mkwmp.Core.Diagnostics.FatalLog.FileName} has:{Environment.NewLine}{before}");
        }
    }

    /// <summary>The flags Core understands; every other argument is the window's (harness, --demo…).</summary>
    private static readonly string[] CoreFlags = ["--headless", "--tray", "--no-launch", "--no-update", "--save-logs", "--version", "-V", "--help", "-h"];

    /// <summary>
    /// Core's arguments out of the command line: its flags and a client.toml path. The window's own
    /// options (some take a value, like <c>--shot out.png</c>) must not be read as a config path.
    /// </summary>
    internal static string[] CoreArguments(string[] args) =>
        [.. args.Where((a, i) => CoreFlags.Contains(a, StringComparer.Ordinal)
            || (a.EndsWith(".toml", StringComparison.OrdinalIgnoreCase) && !(i > 0 && ValueOptions.Contains(args[i - 1], StringComparer.OrdinalIgnoreCase))))];

    /// <summary>
    /// The window's options that take a value: what follows one is never client.toml (the overlay
    /// host's <c>--session .../mkwmp-overlay.toml</c> ends in .toml too).
    /// </summary>
    private static readonly string[] ValueOptions =
    [
        "--session", "--parent-pid", "--shot", "--icon", "--size", "--page", "--category", "--press", "--then", "--theme", "--battery",
        "--network", "--input", "--locale", "--modal", "--state", "--overlay-frame", "--overlay-progress", "--overlay-return",
    ];

    /// <summary>The launcher behind the window. <c>--demo</c>: the design-time fake.</summary>
    private static ILauncher CreateLauncher(bool demo, LauncherOptions options)
    {
        if (demo)
        {
            var args = Environment.GetCommandLineArgs();
            return new DesignLauncher(args.Contains("--demo-fresh", StringComparer.OrdinalIgnoreCase) ? DemoStart.Fresh
                : args.Contains("--demo-offline", StringComparer.OrdinalIgnoreCase) ? DemoStart.Offline
                : DemoStart.Online);
        }
        var folder = LauncherOptions.LauncherFolder;
        return new Launcher(options.ConfigPath ?? LauncherOptions.DefaultConfigPath(folder), folder, options);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var demo = args.Any(a => a.StartsWith("--demo", StringComparison.OrdinalIgnoreCase));
        var options = LauncherOptions.Parse(CoreArguments(args));
        if (options.ShowVersion || options.ShowHelp || options.SaveLogs || options.Headless)
        {
            Headless.AttachParentConsole();
            Shutdown(options switch
            {
                { ShowVersion: true } => Print(options.VersionLine),
                { ShowHelp: true } => Print(LauncherOptions.Usage),
                { SaveLogs: true } => Headless.SaveLogs(options),
                _ => Headless.RunHeadlessAsync(options, CancellationToken.None).GetAwaiter().GetResult(),
            });
            return;
        }
        var harness = Option(args, "--shot") is not null || Option(args, "--icon") is not null;
        // One launcher per user, and the overlay host (launcher/docs/OVERLAY.md §7). On this thread: the
        // instance's mutex belongs to it. The demo and the harness are no launcher: they run beside one.
        if (!demo && !harness)
        {
            _startup = Overlay.OverlayStartup.Begin(args);
            if (_startup is null)
            {
                Shutdown(0); // the running launcher took the request over (its window shows; or it stands by for the game)
                return;
            }
        }
        // Started by a game's mod to stand by as its overlay: no window until the player asks, no tray,
        // nothing started (the game runs and holds the seat), gone with the game.
        var hosted = _startup is { Hosted: true };
        if (hosted)
        {
            options = options with { OverlayHost = true, Tray = false };
            ShutdownMode = ShutdownMode.OnExplicitShutdown; // a window that was never shown ends nothing
        }
        // The system's language and formats, before the launcher's own choice replaces the thread's culture.
        ShellViewModel.SystemCulture = CultureInfo.CurrentUICulture;
        ShellViewModel.SystemFormats = CultureInfo.CurrentCulture;
        var catalogs = Path.Combine(AppContext.BaseDirectory, "i18n");
        if (demo)
        {
            // the fake launcher writes its texts once, when it is made: in the language of this run
            ShellViewModel.UseLanguage(Strings.Load(catalogs, Locales.For(Option(args, "--locale"), ShellViewModel.SystemCulture), ShellViewModel.SystemFormats));
        }
        try
        {
            _launcher = CreateLauncher(demo, options);
        }
        catch (LauncherException error)
        {
            // a client.toml that cannot be read: say so instead of starting over with defaults (a host has nobody to tell)
            if (!hosted)
            {
                MessageBox.Show(error.Message, "MKWMP", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(1);
            return;
        }

        // The language: --locale for this run, else the saved one, else the closest to the system's (Mkwmp.Core.Locales).
        var locale = Locales.For(Option(args, "--locale") ?? _launcher.Settings.Ui.Locale, ShellViewModel.SystemCulture);
        var strings = Strings.Load(catalogs, locale, ShellViewModel.SystemFormats);
        var shell = new ShellViewModel(_launcher, strings) { ForcedLocale = Locales.Find(Option(args, "--locale"))?.Id };
        var window = new MainWindow(shell, args);
        MainWindow = window;
        // tray, close-to-tray, system notifications (nothing of it in the screenshot harness, nor for a host)
        Desktop.DesktopIntegration? desktop = null;
        if (hosted)
        {
            shell.QuitRequested += (_, _) => Dispatcher.BeginInvoke(() => Shutdown());
            window.Closed += (_, _) => Shutdown();
        }
        else
        {
            desktop = Desktop.DesktopIntegration.Attach(window, shell, options.Tray, harness);
        }
        // the overlay: the game asks, the window shows over it (the harness draws its moments itself)
        var overlay = harness ? null : new Overlay.OverlayController(window, shell, _launcher as Launcher, _startup);
        window.OverlayControl = overlay;
        if (!hosted && desktop is not { StartHidden: true })
        {
            window.Show(); // --tray: the window stays hidden until the tray icon opens it
        }
        _ = StartAsync(_launcher, window, shell, overlay, options.Tray);
        if (Option(args, "--crash-test") is { } crash)
        {
            CrashTest(crash);
        }
    }

    private async Task StartAsync(ILauncher launcher, MainWindow window, ShellViewModel shell, Overlay.OverlayController? overlay, bool tray)
    {
        try
        {
            await launcher.StartAsync(CancellationToken.None).ConfigureAwait(true);
        }
#pragma warning disable CA1031 // a failed start is reported, the window stays
        catch (Exception e)
#pragma warning restore CA1031
        {
            Report(e);
        }
        if (_startup is not { } startup || launcher is not Launcher real)
        {
            return;
        }
        var wasHost = startup.Hosted;
        // the local API is up: where this launcher is goes on record (for the mod and for a second launcher),
        // and a host writes the session file its game waits for
        startup.Attach(
            real,
            show: () =>
            {
                // the player started the launcher (again): a host is the ordinary launcher from now on
                if (wasHost && ShutdownMode == ShutdownMode.OnExplicitShutdown)
                {
                    ShutdownMode = ShutdownMode.OnMainWindowClose;
                    real.LeaveOverlayHostMode();
                    _ = Desktop.DesktopIntegration.Attach(window, shell, tray, harness: false);
                }
                overlay?.ShownByPlayer();
                if (!shell.IsOverlay)
                {
                    window.RestoreAfterOverlay();
                }
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = shell.IsFullscreen ? WindowState.Maximized : WindowState.Normal;
                }
                window.Show();
                _ = window.Activate();
            },
            exit: () => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _startup?.Dispose();
        if (_launcher is { } launcher)
        {
            try
            {
                // gives the seat back; never hangs the exit
                launcher.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException error)
            {
                Log("dispose: " + error);
            }
        }
        base.OnExit(e);
    }

    private static int Print(string line)
    {
        Console.WriteLine(line);
        return 0;
    }

    /// <summary>The value after a <c>--name</c> argument, or null.</summary>
    internal static string? Option(string[] args, string name)
    {
        var at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    private static void OnDispatcherUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Report(e.Exception);
        e.Handled = true;
    }

    /// <summary>Logs a fault of an action and tells the player with a toast.</summary>
    internal static void Report(Exception error)
    {
        Log("error: " + error);
        if (Current?.MainWindow?.DataContext is ShellViewModel shell && Current.Dispatcher.CheckAccess())
        {
            shell.ShowToast(error.Message);
        }
    }

    internal static void Log(string line)
    {
        try
        {
            // portable: beside the exe, with the launcher's other logs
            var dir = Path.Combine(Mkwmp.Core.LauncherOptions.LauncherFolder, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "launcher-ui.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging never breaks the shell.
        }
    }
}
