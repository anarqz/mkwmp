// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>One release on the What's New page: its heading, its notes (Markdown), and whether it is the version running.</summary>
public sealed record WhatsNewRelease(string Version, string Date, string Markdown, bool IsRunning);

/// <summary>
/// What's New: the launcher's changelog (Mkwmp.Core's <see cref="Changelog"/>: CHANGELOG.md, built in,
/// or the copy beside the exe), newest first, each release's notes rendered by
/// Views/Settings/MarkdownView as on the privacy page; the running version is marked. While an update
/// is available, downloading or ready, a banner on top shows it with its notes, and its button goes on
/// with the update card's own flow (Download, Install and Restart). The changelog is English only
/// (it is written per release); the page around it is localized. Opened from Home's What's New tile,
/// and from Settings (Updates, About).
/// </summary>
public sealed class WhatsNewViewModel : PageViewModel
{
    private IReadOnlyList<WhatsNewRelease>? _releases;

    public WhatsNewViewModel(ShellViewModel shell)
        : base(shell)
    {
        LinkCommand = new RelayCommand(url => Links.Open(Shell, url as string));
        BannerCommand = new RelayCommand(GoOnWithUpdate);
        RelayCommand.Async(LoadAsync).Execute(null);
    }

    public bool Loading => _releases is null;

    public IReadOnlyList<WhatsNewRelease> Releases => _releases ?? [];

    /// <summary>The build has no changelog (and none is beside it).</summary>
    public bool Empty => _releases is { Count: 0 };

    /// <summary>"MKWMP Launcher 1.4.0", in the header.</summary>
    public string Running => Shell.AboutTitle;

    public ICommand LinkCommand { get; }

    // ── the update banner ──

    private UpdateView Update => Launcher.Update;

    public bool ShowBanner => Update.AvailableVersion is not null
        && Update.State is UpdateState.Available or UpdateState.Downloading or UpdateState.ReadyToInstall or UpdateState.Failed;

    public string BannerTitle => Update.State switch
    {
        UpdateState.Downloading => S["update.downloading"],
        UpdateState.ReadyToInstall => S.Format("update.ready", ("version", Update.AvailableVersion ?? "")),
        UpdateState.Failed => S["update.failed"],
        _ => S.Format("update.available", ("version", Update.AvailableVersion ?? "")),
    };

    /// <summary>The new version's own notes (GitHub's text up to its first rule: what follows repeats older releases).</summary>
    public string BannerNotes => Update.State == UpdateState.Failed ? "" : Changelog.Highlights(Update.Notes);

    /// <summary>Under the title: the download's progress, or why it failed.</summary>
    public string BannerDetail => Update.State switch
    {
        UpdateState.Downloading => UpdateModal.DownloadLine(S, Update),
        UpdateState.Failed => Update.Status,
        _ => "",
    };

    public bool BannerDownloading => Update.State == UpdateState.Downloading;

    public double BannerProgress => Update.Progress;

    private bool GameBlocks => Update.GameBlocks || Launcher.Session.Busy || Launcher.Session.GameRunning;

    public string BannerAction => Update.State switch
    {
        UpdateState.Downloading => S["whatsnew.details"],
        UpdateState.ReadyToInstall when Update.AfterGame => S["whatsnew.details"],
        UpdateState.ReadyToInstall when GameBlocks => S["update.install_after_game"],
        UpdateState.ReadyToInstall => S["update.install"],
        UpdateState.Failed => S["update.retry"],
        _ => S["update.download"],
    };

    public ICommand BannerCommand { get; }

    /// <summary>The update card, with its main action taken (Download, Install and Restart, Try again): its progress and answers show there.</summary>
    private void GoOnWithUpdate()
    {
        var card = new UpdateModal(Shell);
        Shell.ShowModal(card);
        card.Actions.FirstOrDefault(a => a.IsDefault)?.Command.Execute(null);
    }

    private async Task LoadAsync()
    {
        var running = Launcher.Version;
        var document = await Task.Run(() => Changelog.Load(running)).ConfigureAwait(true);
        _releases = [.. document.Releases.Select(r => new WhatsNewRelease(r.Version, r.Date ?? "", r.Markdown, r.IsRunning))];
        App.Log($"what's new: {_releases.Count} releases ({(document.Loose ? Changelog.FileName + " beside the exe" : "built in")})");
        RaiseAll();
    }

    /// <summary>Only the banner follows the launcher's state: the releases are read once.</summary>
    public override void OnStateChanged()
    {
        foreach (var name in new[] { nameof(ShowBanner), nameof(BannerTitle), nameof(BannerNotes), nameof(BannerDetail), nameof(BannerDownloading), nameof(BannerProgress), nameof(BannerAction), nameof(Running) })
        {
            Raise(name);
        }
    }

    public override PageViewModel? Recreate() => new WhatsNewViewModel(Shell);

    public override IReadOnlyList<Hint> Hints => [new Hint(Input.PadButton.B, S["hint.back"]), new Hint(Input.PadButton.A, S["hint.ok"])];
}
