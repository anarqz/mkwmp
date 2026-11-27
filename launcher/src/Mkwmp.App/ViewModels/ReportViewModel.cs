// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core;

using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings › Online › Report Abuse: a player reported to the host of the server in use (who, what
/// happened, optional details). One report waits for review at a time: while one does, the page
/// shows it and offers to withdraw it. A sub-page of Settings; "Report" beside a muted player
/// (Voice &amp; Camera) opens it with that name filled in; Previous Races opens it with the name and, as the details,
/// the player's id and the names they used. (View: Views/Settings/ReportPage.xaml.)
/// </summary>
public sealed class ReportViewModel : PageViewModel
{
    private string _name;
    private string _reason = "";
    private string _details = "";

    public ReportViewModel(ShellViewModel shell, string name = "", string details = "")
        : base(shell)
    {
        _name = Clean(name);
        _details = details is { Length: > CoreStrings.ReportDetailsMax } ? details[..CoreStrings.ReportDetailsMax] : details ?? "";
        ReasonCommand = RelayCommand.Async(PickReasonAsync);
        SendCommand = RelayCommand.Async(SendAsync, () => CanSend);
        WithdrawCommand = RelayCommand.Async(() => Launcher.WithdrawReportAsync(), () => !Launcher.Report.Busy);
    }

    /// <summary>Registered and 18+ confirmed: the server may be asked. (Settings' row leads here only then.)</summary>
    public static bool Allowed(ILauncher launcher) =>
        (launcher ?? throw new ArgumentNullException(nameof(launcher))).Settings.AdultConsent && launcher.Profile.SignedIn;

    private ReportView Report => Launcher.Report;

    /// <summary>gate (not signed up) / checking / open (one is waiting) / form.</summary>
    public string State => !Allowed(Launcher) ? "gate" : Report.Loaded != true ? "checking" : Report.Open is not null ? "open" : "form";

    public string Checking => Report.Busy || Report.Loaded is null ? CoreStrings.ReportChecking : CoreStrings.ReportCantCheck;

    /// <summary>The server the report goes to ("mkw.example.net").</summary>
    public string Server => ProfileForm.HostOf(Launcher.Settings.Server);

    private string ReasonLabel(string code) => Report.Reasons.FirstOrDefault(r => r.Code == code)?.Label ?? code;

    /// <summary>"Your report about Kai (harassment) is waiting for the host's review."; "" while none waits.</summary>
    public string OpenText => WaitingText(Launcher, S);

    /// <summary>The same line for Settings › Online's row.</summary>
    public static string WaitingText(ILauncher launcher, Strings s)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(s);
        return launcher.Report.Open is { } open
            ? s.Format("settings.report.waiting", ("name", open.Reported),
                ("reason", launcher.Report.Reasons.FirstOrDefault(r => r.Code == open.Reason)?.Label ?? open.Reason))
            : "";
    }

    public string Name
    {
        get => _name;
        set
        {
            var clean = Clean(value);
            if (Set(ref _name, clean))
            {
                CommandManager.InvalidateRequerySuggested();
            }
            else if (clean != value)
            {
                Raise();
            }
        }
    }

    private static string Clean(string? name) => new([.. (name ?? "").Where(c => !char.IsWhiteSpace(c)).Take(16)]);

    private string ReasonCode => Report.Reasons.Any(r => r.Code == _reason) ? _reason : (Report.Reasons.Count > 0 ? Report.Reasons[0].Code : "");

    public string ReasonName => ReasonLabel(ReasonCode);

    public string Details
    {
        get => _details;
        set => Set(ref _details, value is { Length: > CoreStrings.ReportDetailsMax } ? value[..CoreStrings.ReportDetailsMax] : value ?? "");
    }

    public static int DetailsMax => CoreStrings.ReportDetailsMax;

    public bool CanSend => !Report.Busy && Links.ValidName(_name)
        && !string.Equals(_name, Launcher.Settings.Name, StringComparison.OrdinalIgnoreCase);

    public string Note => Report.Note ?? "";

    public bool NoteIsError => Report.NoteIsError;

    public ICommand ReasonCommand { get; }

    public ICommand SendCommand { get; }

    public ICommand WithdrawCommand { get; }

    /// <summary>The language changed: the same page, with the name typed so far.</summary>
    public override PageViewModel? Recreate() => new ReportViewModel(Shell, _name, _details);

    public override void OnStateChanged()
    {
        base.OnStateChanged();
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task PickReasonAsync()
    {
        var picked = await Choice.AskAsync(Shell, S["settings.report.what"], "", [.. Report.Reasons.Select(r => new ChoiceOption(r.Code, r.Label))], ReasonCode).ConfigureAwait(true);
        if (picked is not null)
        {
            _reason = picked;
            Raise(nameof(ReasonName));
        }
    }

    private async Task SendAsync()
    {
        var (name, reason, details) = (_name, ReasonCode, _details.Trim());
        (_name, _details, _reason) = ("", "", "");
        RaiseAll();
        await Launcher.SendReportAsync(name, reason, details.Length == 0 ? null : details).ConfigureAwait(true);
    }
}
