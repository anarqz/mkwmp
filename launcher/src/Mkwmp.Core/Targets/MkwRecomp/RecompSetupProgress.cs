// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;

namespace Mkwmp.Core.Targets.MkwRecomp;

/// <summary>
/// The event lines of mkw-recomp's setup (<c>setup/src/tools/prepare/prepare.py</c>, one per line on
/// its standard output, written by <c>machine.emit</c>), turned into a progress the window can show.
/// <code>
/// step &lt;name&gt;               the step started (check exefs romfs generate compile shaders warm cleanup)
/// skip &lt;name&gt;               its key is unchanged: it does not run
/// progress &lt;done&gt; &lt;total&gt;   of the step under way (units differ per step)
/// version &lt;name&gt;            the game version of the player's copy ("1.8.0"), from check
/// done                      everything finished (exit 0)
/// error &lt;code&gt; &lt;detail...&gt;  exit 1, or 2 for a bad command line and "source"
/// </code>
/// A line that is none of these is ignored (the script is free to say more). The same in 0.1.15
/// and 0.1.16; from 0.1.16 the lines are UTF-8 (before: the system's code page), which
/// <see cref="RecompSetup.Decode"/> takes either way. 0.1.16's HDR tone mapper programs
/// (<c>spirv-renodx/</c>, the key <c>tonemap</c> in setup-state.json) are no step: prepare.py makes
/// them in a few seconds when the warm step's turn comes, before its <c>step warm</c> line, also
/// when warm itself is then skipped, and says nothing on its standard output.
/// <para>
/// The steps have no common unit, so the whole is weighed in seconds of a reference machine
/// (launcher/docs/RECOMP_INTEGRATION.md 1.6: 16 threads, game 1.8.0, the data linked): compile
/// 186 s, shaders 266 s beside the others of which 80 s after compile, warm 76 s, cleanup 24 s;
/// check, exefs, romfs and generate were not logged there (about 3 min together) and are
/// estimates. How fast this machine really is shows after the first steps, and scales what is left.
/// </para>
/// Not thread-safe: the caller feeds it lines and asks from one place at a time.
/// </summary>
internal sealed class RecompSetupProgress
{
    /// <summary>prepare.py <c>STEPS</c>, in order.</summary>
    public static readonly IReadOnlyList<string> Steps = ["check", "exefs", "romfs", "generate", "compile", "shaders", "warm", "cleanup"];

    /// <summary>The shaders step when nothing ran beside it (an update that only changed the translator).</summary>
    internal const double ShadersAlone = 266;

    /// <summary>A step the launcher has no figure for (a later release's).</summary>
    internal const double UnknownStep = 20;

    /// <summary>Without a line for this long at the very start, another setup of the folder holds its lock.</summary>
    internal static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    private enum Status { Pending, Running, Done, Skipped }

    private sealed class StepState(string name)
    {
        public string Name { get; } = name;
        public Status Status { get; set; }
        public TimeSpan Started { get; set; }
        public TimeSpan Took { get; set; }
        public long Done { get; set; }
        public long Total { get; set; }
    }

    private readonly List<StepState> _steps = [.. Steps.Select(s => new StepState(s))];
    private readonly bool _copy;
    private int _current = -1;
    private bool _anyEvent;
    private double _shown;

    /// <param name="copy">The game's data is copied (22 GiB) rather than read in place.</param>
    public RecompSetupProgress(bool copy) => _copy = copy;

    /// <summary>The step under way ("" before the first).</summary>
    public string Step => _current < 0 ? "" : _steps[_current].Name;

    public string? GameVersion { get; private set; }

    /// <summary><c>done</c> was read.</summary>
    public bool Finished { get; private set; }

    /// <summary>The <c>error</c> line: its code and the rest of the line.</summary>
    public (string Code, string Detail)? Error { get; private set; }

    /// <summary>The steps that ran and the ones that were skipped, by name (for the log and the tests).</summary>
    public IEnumerable<string> Ran => _steps.Where(s => s.Status is Status.Done or Status.Running).Select(s => s.Name);

    public IEnumerable<string> Skipped => _steps.Where(s => s.Status == Status.Skipped).Select(s => s.Name);

    /// <summary>Seconds a step takes on the reference machine.</summary>
    private double Weight(StepState step) => step.Name switch
    {
        "check" => 4,
        "exefs" => 6,
        "romfs" => _copy ? 900 : 1,
        "generate" => 160,
        "compile" => 186,
        // It starts with generate and runs beside it and compile: only its rest is waited for after them.
        "shaders" => Was("generate", Status.Skipped) && Was("compile", Status.Skipped) ? ShadersAlone : 80,
        "warm" => 76,
        "cleanup" => 24,
        _ => UnknownStep,
    };

    private bool Was(string name, Status status) => _steps.Find(s => s.Name == name)?.Status == status;

    /// <summary>One line of the setup's output, read <paramref name="at"/> this long after it started. True when it was an event.</summary>
    public bool Line(string line, TimeSpan at)
    {
        ArgumentNullException.ThrowIfNull(line);
        var text = line.Trim();
        var space = text.IndexOf(' ', StringComparison.Ordinal);
        var (word, rest) = space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
        switch (word)
        {
            case "step" when rest.Length > 0:
                Start(rest, at);
                break;
            case "skip" when rest.Length > 0:
                if (Find(rest, insert: false) is { } skipped && skipped.Status != Status.Running)
                {
                    skipped.Status = Status.Skipped;
                }
                break;
            case "progress":
                var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (_current >= 0 && parts.Length >= 2
                    && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var done)
                    && long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var total))
                {
                    (_steps[_current].Done, _steps[_current].Total) = (done, total);
                }
                else
                {
                    return false;
                }
                break;
            case "version" when rest.Length > 0:
                GameVersion = rest;
                break;
            case "done" when rest.Length == 0:
                End(at);
                foreach (var step in _steps.Where(s => s.Status == Status.Pending))
                {
                    step.Status = Status.Skipped;
                }
                Finished = true;
                break;
            case "error" when rest.Length > 0:
                var cut = rest.IndexOf(' ', StringComparison.Ordinal);
                Error = cut < 0 ? (rest, "") : (rest[..cut], rest[(cut + 1)..].Trim());
                break;
            default:
                return false;
        }
        _anyEvent = true;
        return true;
    }

    /// <summary>
    /// The warm step's real count, from the runtime's own log (<c>logs/warm.log</c>: "pairs prewarm: N
    /// of M shader libraries"); prepare.py itself only says 0 of 1 and 1 of 1.
    /// </summary>
    public void WarmCount(long done, long total)
    {
        if (Step == "warm" && total > 1 && done >= 0 && done <= total && _steps[_current].Done < done)
        {
            (_steps[_current].Done, _steps[_current].Total) = (done, total);
        }
    }

    private StepState? Find(string name, bool insert)
    {
        if (_steps.Find(s => s.Name == name) is { } known)
        {
            return known;
        }
        if (!insert)
        {
            return null;
        }
        // A step this launcher does not know: it stands where it started.
        var added = new StepState(name);
        _steps.Insert(_current + 1, added);
        return added;
    }

    private void End(TimeSpan at)
    {
        if (_current >= 0 && _steps[_current].Status == Status.Running)
        {
            _steps[_current].Status = Status.Done;
            _steps[_current].Took = at - _steps[_current].Started;
        }
    }

    private void Start(string name, TimeSpan at)
    {
        End(at);
        var step = Find(name, insert: true)!;
        var index = _steps.IndexOf(step);
        // Steps before it that were never named did not need to run; a step named again (compile sends the setup back
        // to generate once when a generated file is damaged) starts over, and so does what follows it.
        for (var i = 0; i < _steps.Count; i++)
        {
            if (i < index && _steps[i].Status == Status.Pending)
            {
                _steps[i].Status = Status.Skipped;
            }
            else if (i > index && _steps[i].Status is Status.Done or Status.Running)
            {
                (_steps[i].Status, _steps[i].Done, _steps[i].Total) = (Status.Pending, 0, 0);
            }
        }
        (step.Status, step.Started, step.Took, step.Done, step.Total) = (Status.Running, at, TimeSpan.Zero, 0, 0);
        _current = index;
    }

    /// <summary>A count the bar can follow: 0 of 1 (the warm step's own) is not one.</summary>
    private static bool Counted(StepState step) => step.Total > 1;

    /// <summary>
    /// How much slower (above 1) or faster this machine is than the reference one, from the steps
    /// that ran here; 1 until about 20 reference seconds of work were seen.
    /// </summary>
    internal double Speed(TimeSpan now)
    {
        double reference = 0, real = 0;
        foreach (var step in _steps)
        {
            if (step.Status == Status.Done)
            {
                reference += Weight(step);
                real += step.Took.TotalSeconds;
            }
            else if (step.Status == Status.Running && Counted(step) && step.Done * 20 >= step.Total)
            {
                reference += Weight(step) * Math.Clamp((double)step.Done / step.Total, 0, 1);
                real += (now - step.Started).TotalSeconds;
            }
        }
        return reference >= 20 && real > 0 ? Math.Clamp(real / reference, 0.2, 12) : 1;
    }

    /// <summary>Where the setup is, <paramref name="now"/> this long after it started.</summary>
    public GameSetupProgress At(TimeSpan now)
    {
        if (Finished)
        {
            return new GameSetupProgress(Step, Strings.SetupStep(Step, _copy), Math.Max(_current, 0), _steps.Count, 1, false, 1, TimeSpan.Zero, GameVersion);
        }
        if (_current < 0)
        {
            var waiting = !_anyEvent && now >= LockWait;
            return new GameSetupProgress("", waiting ? Strings.SetupWaitingForLock : Strings.SetupStarting, 0, _steps.Count, 0, true, 0, null, GameVersion);
        }
        var speed = Speed(now);
        var current = _steps[_current];
        var running = current.Status == Status.Running;
        var counted = Counted(current);
        double fraction;
        if (!running)
        {
            fraction = 1;
        }
        else if (counted)
        {
            fraction = Math.Clamp((double)current.Done / current.Total, 0, 1);
        }
        else
        {
            // No count from this step (check, generate, the cleanup): the clock against what it usually takes, never full.
            fraction = Math.Clamp((now - current.Started).TotalSeconds / (Weight(current) * speed), 0, 0.95);
        }
        double total = 0, done = 0;
        for (var i = 0; i < _steps.Count; i++)
        {
            var step = _steps[i];
            if (step.Status == Status.Skipped)
            {
                continue;
            }
            var weight = Weight(step);
            total += weight;
            done += step.Status == Status.Done ? weight : i == _current && running ? weight * fraction : 0;
        }
        // Skips and a changed weight move the total: the bar never goes back.
        _shown = Math.Max(_shown, total > 0 ? Math.Clamp(done / total, 0, 0.999) : 0);
        var left = TimeSpan.FromSeconds(Math.Max(0, total - done) * speed);
        return new GameSetupProgress(
            current.Name,
            Strings.SetupStep(current.Name, _copy),
            _current,
            _steps.Count,
            fraction,
            running && !counted,
            _shown,
            left,
            GameVersion);
    }
}
