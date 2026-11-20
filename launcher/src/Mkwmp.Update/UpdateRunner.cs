// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;

namespace Mkwmp.Update;

public enum UpdateOutcome
{
    /// <summary>The new version is installed and running.</summary>
    Installed,
    /// <summary>Nothing was changed (or everything was put back); the previous launcher was started again.</summary>
    Kept,
    /// <summary>The launcher never closed: nothing was touched, nothing was started.</summary>
    LauncherStillRunning,
    /// <summary>A file could not be put back: the install is mixed, its originals are in <c>backup/</c>.</summary>
    Broken,
}

/// <summary>
/// The helper's whole job, without its window: wait for the launcher to close, install
/// (<see cref="UpdateInstaller"/>), start the new launcher, and on any failure put the old one
/// back and start that instead.
/// </summary>
public sealed class UpdateRunner(UpdateInstaller installer, Action<string> log)
{
    /// <summary>The new launcher is told not to look for an update again at once (a release whose own version is wrong would loop).</summary>
    public const string UpdatedVariable = "MKWMP_UPDATED";

    /// <summary>The old launcher, started again after a failed install, is told why.</summary>
    public const string FailedVariable = "MKWMP_UPDATE_FAILED";

    /// <summary>How long the launcher gets to close (it frees its seat first).</summary>
    public TimeSpan LauncherPatience { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Starts the launcher of the plan; <c>failure</c> is null for the new version. False when it
    /// cannot be started or ends with an error at once. (Tests replace it.)
    /// </summary>
    public Func<UpdatePlan, string?, bool> StartLauncher { get; init; } = Start;

    /// <summary>Called with the reason before the old launcher is started again: the window shows it for a moment.</summary>
    public Action<string, UpdateOutcome>? Failed { get; init; }

    public Action<UpdateStep, double>? Progress { get; init; }

    public UpdateOutcome Run(UpdatePlan plan, int launcherPid)
    {
        ArgumentNullException.ThrowIfNull(plan);
        log($"installing {plan.FromVersion} -> {plan.Version} ({plan.Kind}: {plan.Asset}) in {plan.Root}");
        Progress?.Invoke(UpdateStep.Waiting, -1);
        if (!UpdateInstaller.WaitForExit(launcherPid, LauncherPatience))
        {
            return Fail($"the launcher (process {launcherPid}) did not close", UpdateOutcome.LauncherStillRunning, plan);
        }
        InstalledUpdate installed;
        try
        {
            installed = installer.Apply(plan);
        }
        catch (UpdateException e)
        {
            return Fail(e.Message, e.Incomplete ? UpdateOutcome.Broken : UpdateOutcome.Kept, plan);
        }
        Progress?.Invoke(UpdateStep.Starting, -1);
        if (StartLauncher(plan, null))
        {
            installed.Commit();
            log($"installed {plan.Version}: {installed.Changed.Count} files changed");
            return UpdateOutcome.Installed;
        }
        try
        {
            installed.Rollback();
        }
        catch (UpdateException e)
        {
            return Fail($"the new version did not start, and {e.Message}", UpdateOutcome.Broken, plan);
        }
        return Fail("the new version did not start", UpdateOutcome.Kept, plan);
    }

    private UpdateOutcome Fail(string why, UpdateOutcome outcome, UpdatePlan plan)
    {
        log($"not installed: {why}");
        Progress?.Invoke(UpdateStep.Failed, -1);
        Failed?.Invoke(why, outcome);
        if (outcome != UpdateOutcome.LauncherStillRunning && !StartLauncher(plan, why))
        {
            log("the previous launcher could not be started either");
        }
        return outcome;
    }

    /// <summary>Starts the launcher with its original arguments; false when it is gone again with an error within a moment.</summary>
    public static bool Start(UpdatePlan plan, string? failure)
    {
        ArgumentNullException.ThrowIfNull(plan);
        try
        {
            var start = new ProcessStartInfo(plan.LauncherPath) { UseShellExecute = false, WorkingDirectory = plan.Root };
            foreach (var argument in plan.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
            start.Environment[UpdatedVariable] = "1";
            if (failure is not null)
            {
                start.Environment[FailedVariable] = failure.Length > 400 ? failure[..400] : failure;
            }
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            // A launcher that cannot even start (a broken release) ends at once with an error; a
            // second instance handing over to a running one ends with 0.
            return failure is not null || !process.WaitForExit(TimeSpan.FromSeconds(2.5)) || process.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
