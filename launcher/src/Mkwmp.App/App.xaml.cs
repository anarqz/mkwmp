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
    }

    /// <summary>The flags Core understands; every other argument is the window's (harness, --demo…).</summary>
    private static readonly string[] CoreFlags = ["--headless", "--tray", "--no-launch", "--no-update", "--save-logs", "--version", "-V", "--help", "-h"];

    /// <summary>
    /// Core's arguments out of the command line: its flags and a client.toml path. The window's own
    /// options (some take a value, like <c>--shot out.png</c>) must not be read as a config path.
    /// </summary>
    internal static string[] CoreArguments(string[] args) =>
        [.. args.Where(a => CoreFlags.Contains(a, StringComparer.Ordinal) || a.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))];

    /// <summary>The launcher behind the window. <c>--demo</c>: the design-time fake.</summary>
    private static ILauncher CreateLauncher(bool demo, LauncherOptions options)
    {
        if (demo)
        {
            return new DesignLauncher();
        }
        var folder = LauncherOptions.LauncherFolder;
        return new Launcher(options.ConfigPath ?? LauncherOptions.DefaultConfigPath(folder), folder, options);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        var demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
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
        try
        {
            _launcher = CreateLauncher(demo, options);
        }
        catch (LauncherException error)
        {
            // a client.toml that cannot be read: say so instead of starting over with defaults
            MessageBox.Show(error.Message, "MKWMP", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var locale = Strings.LocaleFor(Option(args, "--locale") ?? _launcher.Settings.Ui.Locale, CultureInfo.CurrentUICulture);
        var strings = Strings.Load(Path.Combine(AppContext.BaseDirectory, "i18n"), locale);
        var shell = new ShellViewModel(_launcher, strings);
        var window = new MainWindow(shell, args);
        MainWindow = window;
        window.Show();
        _ = StartAsync(_launcher);
    }

    private static async Task StartAsync(ILauncher launcher)
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
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mkwmp");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "launcher-ui.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {line}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Logging never breaks the shell.
        }
    }
}
