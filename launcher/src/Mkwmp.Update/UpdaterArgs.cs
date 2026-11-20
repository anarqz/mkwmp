// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;

namespace Mkwmp.Update;

/// <summary>
/// The helper's command line, as the launcher writes it:
/// <code>
/// worker.exe --plan &lt;staging folder&gt;\plan.json --pid &lt;launcher pid&gt; --theme light|dark --locale &lt;xx[-YY]&gt; [--quiet]
/// </code>
/// and, to look at its window without installing anything (nothing is shown on the desktop):
/// <code>
/// mkwmp-updater.exe --shot out.png [--theme dark] [--locale pt-BR] [--step waiting|verifying|replacing|starting|failed]
///                   [--progress 0.6] [--version 1.5.0] [--scale 2] [--i18n &lt;folder&gt;]
/// </code>
/// </summary>
public sealed record UpdaterArgs
{
    public string? Plan { get; init; }

    /// <summary>The launcher to wait for (0: none).</summary>
    public int Pid { get; init; }

    public bool Dark { get; init; }

    public string Locale { get; init; } = "en";

    /// <summary>No window (<c>--headless</c>, or nobody to show it to).</summary>
    public bool Quiet { get; init; }

    public string? Shot { get; init; }

    public UpdateStep Step { get; init; } = UpdateStep.Replacing;

    /// <summary>0..1; negative: the bar that only moves.</summary>
    public double Progress { get; init; } = -1;

    public string? Version { get; init; }

    public double Scale { get; init; } = 1;

    /// <summary>The catalogs to read instead of the launcher's (<c>--shot</c>).</summary>
    public string? I18n { get; init; }

    public const string Usage = "usage: mkwmp-updater --plan <plan.json> [--pid <n>] [--theme light|dark] [--locale <xx>] [--quiet]";

    /// <summary>The arguments the launcher passes for <paramref name="plan"/>.</summary>
    public static IReadOnlyList<string> For(UpdatePlan plan, int pid, bool dark, string locale, bool quiet)
    {
        ArgumentNullException.ThrowIfNull(plan);
        List<string> args = ["--plan", plan.PlanPath, "--pid", pid.ToString(CultureInfo.InvariantCulture), "--theme", dark ? "dark" : "light", "--locale", ValidLocale(locale) ? locale : "en"];
        if (quiet)
        {
            args.Add("--quiet");
        }
        return args;
    }

    /// <summary><c>en</c>, <c>pt-BR</c>, <c>zh-Hans</c>…: a catalog's file name, never a path.</summary>
    public static bool ValidLocale(string? locale) =>
        locale is { Length: >= 2 and <= 16 } && char.IsAsciiLetter(locale[0]) && locale.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>Null with <paramref name="error"/> set for a command line the launcher would not write.</summary>
    public static UpdaterArgs? Parse(IReadOnlyList<string> args, out string? error)
    {
        ArgumentNullException.ThrowIfNull(args);
        var parsed = new UpdaterArgs();
        error = null;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == "--quiet")
            {
                parsed = parsed with { Quiet = true };
                continue;
            }
            if (i + 1 >= args.Count)
            {
                error = name.StartsWith("--", StringComparison.Ordinal) ? $"{name} needs a value" : $"unknown argument {name}";
                return null;
            }
            var value = args[++i];
            switch (name)
            {
                case "--plan":
                    parsed = parsed with { Plan = value };
                    break;
                case "--pid" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid):
                    parsed = parsed with { Pid = pid };
                    break;
                case "--theme" when value is "light" or "dark":
                    parsed = parsed with { Dark = value == "dark" };
                    break;
                case "--locale" when ValidLocale(value):
                    parsed = parsed with { Locale = value };
                    break;
                case "--shot":
                    parsed = parsed with { Shot = value };
                    break;
                case "--step" when Enum.TryParse<UpdateStep>(value, ignoreCase: true, out var step) && Enum.IsDefined(step):
                    parsed = parsed with { Step = step };
                    break;
                case "--progress" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var progress) && progress <= 1:
                    parsed = parsed with { Progress = progress };
                    break;
                case "--version" when UpdatePlan.ValidVersion(value):
                    parsed = parsed with { Version = value };
                    break;
                case "--scale" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale) && scale is >= 0.5 and <= 4:
                    parsed = parsed with { Scale = scale };
                    break;
                case "--i18n":
                    parsed = parsed with { I18n = value };
                    break;
                case "--pid" or "--theme" or "--locale" or "--step" or "--progress" or "--version" or "--scale":
                    error = $"{name}: \"{value}\" is not valid";
                    return null;
                default:
                    error = $"unknown argument {name}";
                    return null;
            }
        }
        if (parsed.Plan is null && parsed.Shot is null)
        {
            error = "no --plan";
            return null;
        }
        return parsed;
    }
}
