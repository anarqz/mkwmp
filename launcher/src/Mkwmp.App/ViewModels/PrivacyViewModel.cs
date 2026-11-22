// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The privacy policy, read inside the launcher (never a browser): the server's own
/// (<c>GET /privacy</c>, Markdown) rendered by Views/Settings/MarkdownView. The launcher has no
/// policy of its own: a server that publishes none (before 1.4.0) gets a note saying so, and
/// nothing stands in for it. And, under the text, its acceptance ("I'm 18 or older and I accept the
/// privacy policy"): given or withdrawn here, at once, stored by the server. The one privacy
/// component (view: Views/Settings/PrivacyPolicyPanel.xaml), the content of the welcome flow's consent
/// page; Settings › Online's Privacy Policy row opens that same page alone (SetupViewModel.PrivacyPolicy).
/// Not a page by itself (it is a PageViewModel for the shell, the strings and the launcher it needs). A link of
/// the text is a row; it opens (in the browser) only when chosen.
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

    /// <summary>Whose policy it is: the server's, with its address (nothing when there is none).</summary>
    public string Source => _policy is { FromServer: true }
        ? S.Format("privacy.from_server", ("server", ProfileForm.HostOf(Launcher.Settings.Server)))
        : "";

    /// <summary>Why there is no policy to show (the server publishes none, or could not be reached).</summary>
    public string Note => _policy?.Note ?? "";

    /// <summary>The first characters of the version a consent would name.</summary>
    public string Version => _policy?.Version is { Length: >= 12 } version ? S.Format("privacy.version", ("version", version[..12])) : "";

    public ICommand LinkCommand { get; }

    // ── the acceptance, under the text ──

    /// <summary>A line over the text (the consent step: "You are signed in on …. One last thing").</summary>
    public string Lead { get; init; } = "";

    /// <summary>There is a profile on the server to keep a consent with.</summary>
    public bool ShowAccept => Launcher.OnlineEnabled && Launcher.Profile.SignedIn;

    /// <summary>No profile yet: what to do first.</summary>
    public string NoProfileText => S["setup.done.online_no_profile_detail"];

    public bool CanChange => !Launcher.Session.Busy && Launcher.Profile.Register != RegisterState.Working;

    /// <summary>The server holds this profile's consent. Set: given or withdrawn at once (the switch shows what the server kept).</summary>
    public bool Accepted
    {
        get => Launcher.Settings.AdultConsent;
        set
        {
            if (value != Launcher.Settings.AdultConsent)
            {
                RelayCommand.Async(async () =>
                {
                    await Launcher.SetConsentAsync(value).ConfigureAwait(true);
                    RaiseAccept();
                }).Execute(null);
            }
        }
    }

    /// <summary>Under the switch: why it was not stored; else what off does, or that the server has none yet.</summary>
    public string AcceptCaption => !Accepted && Launcher.Profile is { Register: RegisterState.Failed, Message: { Length: > 0 } why } ? why
        : S[Accepted ? "settings.privacy.adult_caption" : "settings.privacy.review_caption"];

    /// <summary>For Settings › Online's row: accepted on the server in use.</summary>
    public static bool IsAccepted(ILauncher launcher) =>
        (launcher ?? throw new ArgumentNullException(nameof(launcher))) is { OnlineEnabled: true, Profile.SignedIn: true, Settings.AdultConsent: true };

    private void RaiseAccept()
    {
        Raise(nameof(Accepted));
        Raise(nameof(AcceptCaption));
        Raise(nameof(ShowAccept));
        Raise(nameof(CanChange));
    }


    private async Task LoadAsync()
    {
        _policy = await Launcher.GetPrivacyPolicyAsync().ConfigureAwait(true);
        RaiseAll();
    }

    /// <summary>The text does not follow the launcher's state; the switch under it does.</summary>
    public override void OnStateChanged() => RaiseAccept();

}
