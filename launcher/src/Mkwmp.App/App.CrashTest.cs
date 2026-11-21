// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Mkwmp.App;

/// <summary>
/// <c>--crash-test &lt;kind&gt;</c>: ends the launcher on purpose three seconds after its start, to
/// check on a machine (Windows, Proton) that each kind of end leaves its record in
/// logs/launcher-crash.log. <c>thread</c>: an exception nobody catches on a pool thread;
/// <c>native</c>: an access violation in native code; <c>stack</c>: a stack overflow;
/// <c>failfast</c>: <see cref="Environment.FailFast(string)"/>; <c>kill</c>: the process ends
/// itself as a kill from outside would (no record but the missing exit line);
/// <c>ui</c>: an exception in the window's thread, which the launcher survives.
/// </summary>
public partial class App
{
    private static void CrashTest(string kind)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Log($"crash test: {kind}");
            switch (kind)
            {
                case "thread":
                    new Thread(() => throw new InvalidOperationException("crash test: thread")).Start();
                    break;
                case "native":
                    RtlZeroMemory(new IntPtr(16), 16);
                    break;
                case "stack":
                    _ = Deeper(1);
                    break;
                case "failfast":
                    Environment.FailFast("crash test: failfast");
                    break;
                case "kill":
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                    break;
                default:
                    throw new InvalidOperationException("crash test: ui");
            }
        };
        timer.Start();
    }

    private static int Deeper(int n) => n + Deeper(n + 1) + 1;

#pragma warning disable SYSLIB1054 // plain DllImport, as the rest
    [DllImport("ntdll.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void RtlZeroMemory(IntPtr at, nuint bytes);
#pragma warning restore SYSLIB1054
}
