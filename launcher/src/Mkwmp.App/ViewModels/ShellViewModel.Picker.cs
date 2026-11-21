// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.Core.Files;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The launcher's own file and folder picker: a panel from the right, above the page and above a
/// modal (the game setup's card opens it), under the on-screen keyboard (its path box uses it).
/// Every folder or file the launcher asks for is chosen here, never in the system's dialog: the
/// same look and the same rules on Windows and under Proton, and it works with a controller.
/// </summary>
public sealed partial class ShellViewModel
{
    private PickerViewModel? _picker;
    private PickerViewModel? _pickerShown;

    /// <summary>This machine's files as the picker sees them (the harness and the tests of a page put a made-up machine here).</summary>
    public FileBrowser Files { get; set; } = new(FileHost.Current());

    /// <summary>The picker's panel: the open one, and after it closed still the last (it slides out with its content).</summary>
    public PickerViewModel? Picker => _pickerShown;

    public bool IsPickerOpen => _picker is not null;

    /// <summary>
    /// Opens the picker and completes with the path the player chose (a path that passed the
    /// request's <see cref="PickerRequest.Validate"/>), or null when they closed it. A pick asked
    /// for while another is open closes that one first (its caller gets null).
    /// </summary>
    public Task<string?> PickAsync(PickerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _picker?.Cancel();
        var picker = new PickerViewModel(this, Files, request);
        _picker = _pickerShown = picker;
        Raise(nameof(Picker));
        Raise(nameof(IsPickerOpen));
        RefreshHints();
        picker.OnOpened();
        return picker.Result;
    }

    /// <summary>A folder (files are listed only on request, and never picked).</summary>
    public Task<string?> PickFolderAsync(PickerRequest request) =>
        PickAsync((request ?? throw new ArgumentNullException(nameof(request))) with { Kind = PickKind.Folder });

    /// <summary>A file; a request of <see cref="PickKind.Either"/> also takes the open folder.</summary>
    public Task<string?> PickFileAsync(PickerRequest request) =>
        PickAsync((request ?? throw new ArgumentNullException(nameof(request))) is { Kind: PickKind.Folder } folder ? folder with { Kind = PickKind.File } : request);

    /// <summary>Closes the picker without a pick (the dimmed page, the overlay coming up).</summary>
    public void CancelPicker() => _picker?.Cancel();

    /// <summary>The picker's own way out (it has its result): the panel slides away.</summary>
    internal void ClosePicker(PickerViewModel picker)
    {
        if (ReferenceEquals(_picker, picker))
        {
            _picker = null;
            Raise(nameof(IsPickerOpen));
            RefreshHints();
        }
    }
}
