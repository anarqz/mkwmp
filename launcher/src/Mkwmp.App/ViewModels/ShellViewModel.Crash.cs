// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using Mkwmp.App.Controls;
using Mkwmp.Core.Diagnostics;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The crash screen and "Found a bug?" (Infinity Launcher's pattern, OD-241): after the game
/// crashed a modal says so with two answers, <b>Report</b> and <b>Ignore</b>; either one answers
/// the crash for good (Core remembers its id). Report, and "Found a bug?" in Settings › Support
/// and the Power menu, run one flow: Core builds a zip on the Desktop and the GitHub issue's link
/// and text; the text goes to the clipboard, the link to the browser, and a last card says where
/// the zip is and to drag it into the issue. Under Wine the browser may not open (Game Mode) or
/// open behind the launcher, so that card always shows the address and the file.
/// </summary>
public sealed partial class ShellViewModel
{
    private string? _crashOffered;

    /// <summary>The crash screen's picture, and the report card's.</summary>
    private const string CrashArt = "pack://application:,,,/Assets/Art/empty-crash.png";

    /// <summary>
    /// Shows the crash screen for a crash not answered yet, once per crash while the launcher runs
    /// (closing it without an answer leaves it for the next start). Not over another modal, the
    /// welcome flow, or a game that is running again.
    /// </summary>
    internal void OfferCrash()
    {
        if (Launcher.PendingCrash is not { } crash || crash.Id == _crashOffered || IsModalOpen || IsOverlay || Page is SetupViewModel)
        {
            return;
        }
        _crashOffered = crash.Id;
        ShowCrash(crash);
    }

    private void ShowCrash(CrashView crash)
    {
        var modal = new ModalViewModel { Title = S["crash.title"], Body = CrashText(crash), Art = CrashArt };
        modal.Actions.Add(new ModalAction(S["crash.report"], () =>
        {
            CloseModal(modal);
            RelayCommand.Async(() => ReportAsync(crash.Id)).Execute(null);
        }, ButtonKind.Outline, isDefault: true));
        modal.Bottom = new ModalAction(S["crash.ignore"], () =>
        {
            CloseModal(modal);
            RelayCommand.Async(() => Launcher.DismissCrashAsync(crash.Id)).Execute(null);
        });
        ShowModal(modal);
    }

    private string CrashText(CrashView crash)
    {
        var culture = S.Culture;
        var lines = new List<string> { crash.What, S.Format("crash.when", ("time", crash.Time.ToLocalTime().ToString("g", culture))) };
        if (crash.Players is { } players)
        {
            lines.Add(S.Format(crash.Host == true ? "crash.room_host" : "crash.room", ("n", players.ToString(culture))));
        }
        if (crash.Earlier > 0)
        {
            lines.Add(S.Format(crash.Earlier == 1 ? "crash.earlier_one" : "crash.earlier_many", ("n", crash.Earlier.ToString(culture))));
        }
        return string.Join("\n", lines) + "\n\n" + S[crash.HasDump ? "crash.body_dump" : "crash.body"];
    }

    /// <summary>"Found a bug?": the same report, without a crash.</summary>
    public Task ReportBugAsync() => ReportAsync(null);

    /// <summary>
    /// Builds the report and hands it over: the text on the clipboard, the issue in the browser,
    /// then the card with the zip and the address. <paramref name="open"/> false (the screenshot
    /// harness): nothing leaves the window.
    /// </summary>
    internal async Task ReportAsync(string? crashId, bool open = true)
    {
        var progress = ShowProgress(S["report.preparing"], S["report.preparing_body"]);
        BugReport report;
        try
        {
            report = await Launcher.PrepareReportAsync(crashId).ConfigureAwait(true);
        }
        finally
        {
            progress.Close();
        }
        var copied = open && Copy(report.Text);
        var opened = open && Links.Open(report.Url);
        if (crashId is not null)
        {
            await Launcher.DismissCrashAsync(crashId).ConfigureAwait(true); // reported: answered
        }
        ShowReport(report, opened || !open, copied || !open);
    }

    private void ShowReport(BugReport report, bool opened, bool copied)
    {
        // what to do, in order; the address is the short one (the text to paste is on the clipboard and in the zip)
        var address = BugReportBuilder.NewIssue + "?template=" + (report.Crash ? "crash.yml" : "bug_report.yml");
        var steps = new List<string>
        {
            // the file's name on a line of its own (on the Desktop it is all there is to find), so the
            // line never breaks inside it at a hyphen ("mkwmp-" / "crash-…zip")
            report.Zip is { } zip
                ? S[report.OnDesktop ? "report.step_zip_desktop" : "report.step_zip"] + "\n"
                    + (report.OnDesktop ? System.IO.Path.GetFileName(zip) : System.IO.Path.GetDirectoryName(zip) + System.IO.Path.DirectorySeparatorChar + "\n" + System.IO.Path.GetFileName(zip))
                : S.Format("report.step_no_zip", ("why", report.ZipProblem ?? "")),
            S[opened ? "report.step_issue" : "report.step_issue_closed"] + "\n" + address,
            S[copied ? "report.step_text" : "report.step_text_failed"],
        };
        var modal = new ModalViewModel
        {
            Art = report.Crash ? CrashArt : null,
            Title = S[report.Crash ? "report.ready_crash" : "report.ready_bug"],
            Body = string.Join("\n", steps.Select((step, i) => string.Create(CultureInfo.InvariantCulture, $"{i + 1}. {step}"))),
        };
        if (report.Zip is { } file)
        {
            modal.Actions.Add(new ModalAction(S["settings.support.open_folder"], () => Links.ShowInFolder(file), ButtonKind.Outline, isDefault: true));
        }
        modal.Actions.Add(new ModalAction(S["report.open_again"], () =>
        {
            if (!Links.Open(report.Url))
            {
                ShowToast(S["settings.link_failed"], address);
            }
        }));
        modal.Bottom = new ModalAction(S["common.close"], () => CloseModal(modal));
        ShowModal(modal);
    }

    /// <summary>Text to the clipboard; false when another program holds it (or there is none: some Wine sessions).</summary>
    private static bool Copy(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (Exception e) when (e is COMException or ExternalException or InvalidOperationException)
            {
                Thread.Sleep(60);
            }
        }
        App.Log("clipboard: the report text could not be copied");
        return false;
    }
}
