// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Update;

namespace Mkwmp.UpdateHelper;

/// <summary>
/// mkwmp-updater.exe (run as <c>worker.exe</c> from the staging folder): waits for the launcher to
/// close, swaps the files of the downloaded release in, starts the new launcher. See
/// <see cref="UpdaterArgs"/> for the command line and launcher/docs/UPDATES.md for the whole flow.
/// Exit codes: 0 installed; 1 not installed, the previous version runs again; 2 a bad command line
/// or plan; 3 the launcher never closed; 4 the previous version could not be fully put back.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (UpdaterArgs.Parse(args, out _) is not { } options)
        {
            return 2;
        }
        if (options.Shot is { } shot)
        {
            return Shoot(options, shot);
        }
        UpdatePlan plan;
        try
        {
            plan = UpdatePlan.Read(options.Plan!);
        }
        catch (UpdateException)
        {
            return 2; // no plan: no folder to log into, no launcher to start
        }
        var log = new UpdateLog(plan.LogPath);
        var strings = UpdaterStrings.Load(Path.Combine(plan.Root, "i18n"), options.Locale);
        var caption = plan.FromVersion.Length > 0 ? $"{plan.FromVersion}  →  {plan.Version}" : strings.Format(UpdaterStrings.Version, "version", plan.Version);
        var window = !options.Quiet && UpdaterWindow.Create(new Scene(options.Dark, strings[UpdaterStrings.Title], strings[UpdaterStrings.Waiting], -1, 0, caption, Failed: false, Rounded: false));
        var runner = new UpdateRunner(new UpdateInstaller(ReleaseSignature.Verify, log: log.Line)
        {
            Progress = (step, fraction) => Step(window, strings, step, fraction),
        }, log.Line)
        {
            Progress = (step, fraction) => Step(window, strings, step, fraction),
            Failed = (_, outcome) =>
            {
                if (window)
                {
                    // Long enough to read; a click or a key ends it sooner. Then the previous launcher opens again.
                    UpdaterWindow.Fail(strings[UpdaterStrings.FailedTitle], strings[outcome == UpdateOutcome.Broken ? UpdaterStrings.FailedBroken : UpdaterStrings.FailedBody], TimeSpan.FromSeconds(12));
                }
            },
        };
        var code = 1;
        var work = new Thread(() =>
        {
            try
            {
                code = runner.Run(plan, options.Pid) switch
                {
                    UpdateOutcome.Installed => 0,
                    UpdateOutcome.Kept => 1,
                    UpdateOutcome.LauncherStillRunning => 3,
                    _ => 4,
                };
            }
#pragma warning disable CA1031 // the last line of defence: whatever went wrong is logged, and the window closes
            catch (Exception e)
#pragma warning restore CA1031
            {
                log.Line($"unexpected: {e.GetType().Name}: {e.Message}");
            }
            finally
            {
                if (window)
                {
                    UpdaterWindow.Close();
                }
            }
        })
        { IsBackground = false, Name = "install" };
        work.Start();
        if (window)
        {
            UpdaterWindow.Run();
        }
        work.Join();
        return code;
    }

    private static void Step(bool window, UpdaterStrings strings, UpdateStep step, double fraction)
    {
        if (window && step != UpdateStep.Failed)
        {
            UpdaterWindow.Show(strings.For(step), step == UpdateStep.Replacing ? fraction : -1);
        }
    }

    /// <summary><c>--shot</c>: one frame as a PNG, no window.</summary>
    private static int Shoot(UpdaterArgs options, string path)
    {
        if (!Painter.Start())
        {
            return 1;
        }
        var strings = UpdaterStrings.Load(options.I18n, options.Locale);
        var failed = options.Step == UpdateStep.Failed;
        var version = options.Version ?? "1.5.0";
        var scene = new Scene(
            options.Dark,
            strings[failed ? UpdaterStrings.FailedTitle : UpdaterStrings.Title],
            strings.For(options.Step),
            options.Step == UpdateStep.Replacing ? options.Progress : -1,
            0.55,
            $"1.4.0  →  {version}",
            failed,
            Rounded: true);
        return Painter.Save(scene, options.Scale, Path.GetFullPath(path)) ? 0 : 1;
    }
}
