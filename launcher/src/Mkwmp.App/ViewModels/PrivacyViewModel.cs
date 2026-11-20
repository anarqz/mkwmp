// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The privacy policy, read inside the launcher (never a browser): the server's own
/// (<c>GET /privacy</c>, Markdown) rendered by Views/Settings/MarkdownView, else MKWMP's short
/// text. A sub-page: pushed from the consent page and from Settings › Online, B goes back. A
/// link of the text is a row; it opens (in the browser) only when chosen.
/// </summary>
public sealed class PrivacyViewModel : PageViewModel
{
    private PrivacyPolicy? _policy;

    public PrivacyViewModel(ShellViewModel shell)
        : base(shell)
    {
        LinkCommand = new RelayCommand(url => Links.Open(Shell, url as string));
        RelayCommand.Async(LoadAsync).Execute(null);
    }

    public bool Loading => _policy is null;

    public string Markdown => _policy?.Markdown ?? "";

    /// <summary>Whose policy it is: the server's (with its address), or MKWMP's.</summary>
    public string Source => _policy is null ? ""
        : _policy.FromServer ? S.Format("privacy.from_server", ("server", ProfileForm.HostOf(Launcher.Settings.Server)))
        : S["privacy.from_project"];

    /// <summary>Why the server's own policy is not the one shown.</summary>
    public string Note => _policy?.Note ?? "";

    /// <summary>The first characters of the version a consent would name.</summary>
    public string Version => _policy?.Version is { Length: >= 12 } version ? S.Format("privacy.version", ("version", version[..12])) : "";

    public ICommand LinkCommand { get; }

    private async Task LoadAsync()
    {
        _policy = await Launcher.GetPrivacyPolicyAsync().ConfigureAwait(true);
        RaiseAll();
    }

    public override void OnStateChanged()
    {
        // the text does not follow the launcher's state
    }

    public override IReadOnlyList<Hint> Hints => [new Hint(Input.PadButton.B, S["hint.back"]), new Hint(Input.PadButton.A, S["hint.ok"])];
}
