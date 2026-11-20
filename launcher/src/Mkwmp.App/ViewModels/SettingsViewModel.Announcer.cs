// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows.Input;
using Mkwmp.Core.Net;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// Settings › Online › Server list: where the list of servers comes from (the beholder, the
/// directory servers announce themselves to). It arrives set to MKWMP's own; a player can point it
/// at another announcer (a community's, a LAN one) or go back to the default. Saved as
/// <c>beholder</c> in client.toml; the list is asked again at once.
/// </summary>
public sealed partial class SettingsViewModel
{
    private string? _announcerDraft;
    private string? _announcerNote;
    private ICommand? _saveAnnouncer;
    private ICommand? _defaultAnnouncer;

    /// <summary>The address in effect: the player's, else the release default.</summary>
    public string AnnouncerInEffect => Launcher.Settings.Beholder.Length > 0 ? Launcher.Settings.Beholder : Announcer.DefaultUrl;

    public bool AnnouncerIsDefault => Launcher.Settings.Beholder.Length == 0
        || string.Equals(Launcher.Settings.Beholder.TrimEnd('/'), Announcer.DefaultUrl, StringComparison.OrdinalIgnoreCase);

    public bool AnnouncerCustom => !AnnouncerIsDefault;

    /// <summary>What the box shows: the draft being typed, else the address in effect.</summary>
    public string AnnouncerAddress
    {
        get => _announcerDraft ?? AnnouncerInEffect;
        set
        {
            _announcerDraft = (value ?? "").Trim();
            _announcerNote = null;
            Raise();
            Raise(nameof(AnnouncerNote));
            Raise(nameof(AnnouncerNoteIsError));
        }
    }

    public string AnnouncerNote => _announcerNote
        ?? (AnnouncerIsDefault
            ? S.Format("settings.online.announcer_default", ("host", ProfileForm.HostOf(Announcer.DefaultUrl)))
            : S.Format("settings.online.announcer_custom", ("host", ProfileForm.HostOf(AnnouncerInEffect))));

    public bool AnnouncerNoteIsError { get; private set; }

    public ICommand SaveAnnouncerCommand => _saveAnnouncer ??= RelayCommand.Async(SaveAnnouncerAsync, () => !Busy);

    public ICommand DefaultAnnouncerCommand => _defaultAnnouncer ??= RelayCommand.Async(() => SetAnnouncerAsync(""), () => !Busy && !AnnouncerIsDefault);

    private Task SaveAnnouncerAsync()
    {
        var typed = (_announcerDraft ?? AnnouncerInEffect).Trim();
        if (typed.Length > 0 && !typed.Contains("://", StringComparison.Ordinal))
        {
            typed = "https://" + typed; // a bare host is an https announcer
        }
        if (typed.Length > 0 && !Announcer.IsAllowed(typed))
        {
            _announcerNote = S["settings.online.announcer_invalid"];
            AnnouncerNoteIsError = true;
            RaiseAnnouncer();
            return Task.CompletedTask;
        }
        // the default typed out is the default: keep client.toml clean so a later release can move it
        var keep = string.Equals(typed.TrimEnd('/'), Announcer.DefaultUrl, StringComparison.OrdinalIgnoreCase) ? "" : typed.TrimEnd('/');
        return SetAnnouncerAsync(keep);
    }

    private async Task SetAnnouncerAsync(string address)
    {
        _announcerDraft = null;
        _announcerNote = null;
        AnnouncerNoteIsError = false;
        await Shell.UpdateSettingsAsync(s => s with { Beholder = address }).ConfigureAwait(true);
        RaiseAnnouncer();
        await Launcher.RefreshServersAsync().ConfigureAwait(true);
    }

    private void RaiseAnnouncer()
    {
        Raise(nameof(AnnouncerAddress));
        Raise(nameof(AnnouncerInEffect));
        Raise(nameof(AnnouncerIsDefault));
        Raise(nameof(AnnouncerCustom));
        Raise(nameof(AnnouncerNote));
        Raise(nameof(AnnouncerNoteIsError));
        CommandManager.InvalidateRequerySuggested();
    }
}
