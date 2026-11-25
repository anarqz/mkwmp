// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using Mkwmp.App.Input;
using Mkwmp.Core;
using Mkwmp.Core.Files;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>What the picker gives back: a folder, a file, or either (a file is picked from the list, a folder with "Use This Folder").</summary>
public enum PickKind { Folder, File, Either }

/// <summary>
/// What the launcher's own picker is asked for (<c>Shell.PickAsync</c>, <c>PickFolderAsync</c>, <c>PickFileAsync</c>).
/// </summary>
/// <param name="Title">The panel's title: what is being chosen.</param>
/// <param name="Purpose">
/// The key the last folder is kept under in client.toml's <c>[picker]</c> (lower-case letters, digits, <c>_</c>):
/// the next pick for the same purpose starts where this one ended.
/// </param>
public sealed record PickerRequest(string Title, string Purpose, PickKind Kind = PickKind.Folder)
{
    /// <summary>One or two lines under the list: what a right choice looks like.</summary>
    public string Caption { get; init; } = "";

    /// <summary>What is listed and marked. A <see cref="PickKind.File"/> or <see cref="PickKind.Either"/> request needs one with <see cref="PickFilter.Files"/>.</summary>
    public PickFilter Filter { get; init; } = PickFilter.Folders;

    /// <summary>
    /// The judge of a choice: why the path cannot be taken (shown under the list; the picker stays open), or null.
    /// It is awaited on the window's thread: do the reading inside a <c>Task.Run</c>.
    /// </summary>
    public Func<string, Task<string?>>? Validate { get; init; }

    /// <summary>Where to start (a file starts in its folder); else the last folder of <see cref="PickerRequest.Purpose"/>, else Downloads.</summary>
    public string? InitialFolder { get; init; }

    /// <summary>The mark on a folder the filter recognises ("Game copy").</summary>
    public string MatchBadge { get; init; } = "";

    /// <summary>Said under the list when the open folder is one the filter recognises.</summary>
    public string MatchNote { get; init; } = "";

    /// <summary>The icon beside the title, by its key in Themes/Icons.xaml.</summary>
    public string Icon { get; init; } = "Icon.Folder";
}

/// <summary>A line of the picker's list.</summary>
public sealed class PickerEntry
{
    public PickerEntry(FileEntry entry, string name, string icon)
    {
        Entry = entry ?? throw new ArgumentNullException(nameof(entry));
        Name = name;
        Icon = icon;
    }

    public FileEntry Entry { get; }
    public string Name { get; }
    public string Path => Entry.Path;
    public bool IsFolder => Entry.IsFolder;
    public string Icon { get; }

    /// <summary>The mark of a folder that holds what is looked for; "" for none.</summary>
    public string Badge { get; init; } = "";
    public string Modified { get; init; } = "";
    public string Size { get; init; } = "";

    /// <summary>A drive's or a card's free space, across both columns.</summary>
    public string Detail { get; init; } = "";

    /// <summary>A file that cannot be picked here (it shows only with "show all files").</summary>
    public bool Dimmed { get; init; }

    /// <summary>Drawn in the accent colour: folders, drives, and the files that match.</summary>
    public bool Accent => Entry.IsFolder || Entry.Match;
}

/// <summary>A quick place at the left of the picker.</summary>
public sealed class PickerPlace : ObservableObject
{
    private string _detail = "";
    private bool _isCurrent;

    public PickerPlace(Place place, string label, string icon, ICommand open)
    {
        Place = place ?? throw new ArgumentNullException(nameof(place));
        Label = label;
        Icon = icon;
        OpenCommand = open;
    }

    public Place Place { get; }
    public string Label { get; }
    public string Icon { get; }
    public ICommand OpenCommand { get; }

    /// <summary>Free space, or where the place is; "" for none.</summary>
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    /// <summary>The open folder is in this place.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
}

/// <summary>A step of the path above the list (a tap goes there).</summary>
public sealed record PickerCrumb(string Label, bool IsLast, ICommand OpenCommand);

/// <summary>Where the picker's view puts the focus.</summary>
public enum PickerZone { List, UseFolder, Places }

/// <param name="Index">For <see cref="PickerZone.List"/>: the line; -1 for the one the focus was on last.</param>
public sealed record PickerFocus(PickerZone Zone, int Index = 0);

/// <summary>
/// The launcher's own file and folder picker, a panel from the right (Views/PickerSidebar.xaml):
/// quick places at the left, the path (typed, or its steps), the folder's list, and under it what
/// is asked for or why a choice cannot be taken. The logic is Mkwmp.Core's <see cref="FileBrowser"/>;
/// this class is the state of one pick. Ported from Infinity Launcher's in-launcher browser, never
/// the system's dialog (a different window with different rules on Windows and under Proton, and
/// no use with a controller).
/// <para>
/// A opens a folder or picks a file; B goes back the way the player came, then closes; X takes the
/// open folder; Y shows every file; LB / RB jump to the places and to the list. A choice is checked
/// by the request's own judge before the panel closes: a refusal is said here and the panel stays.
/// Listings are made off the window's thread; only the newest one is shown.
/// </para>
/// </summary>
public sealed class PickerViewModel : LayerViewModel
{
    private const int MaxCrumbs = 4;

    private enum Why { Open, Enter, Typed, Back, Refresh, Watch }

    private readonly ShellViewModel _shell;
    private readonly FileBrowser _files;
    private readonly PickerRequest _request;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stack<string> _back = new();
    private readonly DispatcherTimer _watch;
    private readonly string _remembered;
    private CancellationTokenSource? _listing;
    private FolderListing _shown = new("", []);
    private IReadOnlyList<Place> _placesNow = [];
    private IReadOnlyList<PickerEntry> _entries = [];
    private IReadOnlyList<PickerCrumb> _crumbs = [];
    private int _generation;
    private bool _closed;
    private bool _watching;
    private bool _showAll;
    private bool _isLoading;
    private bool _busy;
    private bool _focusOnFile;
    private string _path = "";
    private string _pathText = "";
    private string _note = "";
    private string _noteTone = "";

    public PickerViewModel(ShellViewModel shell, FileBrowser files, PickerRequest request)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _remembered = shell.Launcher.Settings.Picker[request.Purpose];
        _note = request.Caption;
        UpCommand = new RelayCommand(Up, () => CanGoUp);
        GoCommand = RelayCommand.Async(GoAsync, () => !Busy);
        UseFolderCommand = RelayCommand.Async(() => ChooseAsync(_path, isFolder: true), () => CanUseFolder);
        CloseCommand = new RelayCommand(Cancel);
        // a card put in or taken out while the panel is open, a folder deleted under it
        _watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _watch.Tick += (_, _) => RelayCommand.Async(WatchAsync).Execute(null);
    }

    public Strings S => _shell.S;

    public ShellViewModel Shell => _shell;

    /// <summary>The path that was picked, or null when the panel was closed without a pick.</summary>
    public Task<string?> Result => _result.Task;

    /// <summary>A listing arrived: where the view puts the focus.</summary>
    public event EventHandler<PickerFocus>? FocusRequested;

    public string Title => _request.Title;

    public string Icon => _request.Icon;

    public ObservableCollection<PickerPlace> Places { get; } = [];

    /// <summary>The open folder's lines. A new list for every folder (the view starts at its top).</summary>
    public IReadOnlyList<PickerEntry> Entries => _entries;

    public IReadOnlyList<PickerCrumb> Crumbs => _crumbs;

    /// <summary>The path box: the open folder as the player reads it (the Linux path under Proton), or what they are typing.</summary>
    public string PathText
    {
        get => _pathText;
        set => Set(ref _pathText, value ?? "");
    }

    /// <summary>A listing takes longer than a blink (a network folder, a sleeping disk).</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    /// <summary>A choice is being checked.</summary>
    public bool Busy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Every file and the hidden entries, not only what can be picked.</summary>
    public bool ShowAll => _showAll;

    /// <summary>Why the list is empty ("" while it has lines): an empty folder, nothing that matches, a folder that cannot be opened.</summary>
    public string EmptyText => _shown.Problem switch
    {
        ListProblem.Denied => S["picker.denied"],
        ListProblem.NotFound => S["picker.gone"],
        ListProblem.Unreadable => S.Format("picker.unreadable", ("reason", _shown.Reason ?? "")),
        ListProblem.TooDeep => S["picker.too_deep"],
        _ when _entries.Count > 0 || IsLoading => "",
        _ when _shown.Suppressed > 0 => S.Format("picker.empty_filtered", ("n", S.Number(_shown.Suppressed))),
        _ => S["picker.empty"],
    };

    public string EmptyIcon => _shown.Problem switch
    {
        ListProblem.Denied => "Icon.Lock",
        ListProblem.None => "Icon.Folder",
        _ => "Icon.Warning",
    };

    /// <summary>Under the list: what is asked for; or that the open folder is a right one; or why the last choice was refused.</summary>
    public string Note => _note;

    /// <summary>"" (plain), "good" or "bad".</summary>
    public string NoteTone => _noteTone;

    public bool ShowUseFolder => _request.Kind != PickKind.File;

    public bool CanUseFolder => ShowUseFolder && _path.Length > 0 && !Busy && !IsLoading && _shown.Problem is not (ListProblem.NotFound or ListProblem.TooDeep);

    /// <summary>The open folder is one the filter recognises: the button is the main one.</summary>
    public bool FolderMatches => ShowUseFolder && _shown.SelfMatch;

    public bool CanGoUp => _path.Length > 0;

    public ICommand UpCommand { get; }

    public ICommand GoCommand { get; }

    public ICommand UseFolderCommand { get; }

    public ICommand CloseCommand { get; }

    public override IReadOnlyList<Hint>? Hints
    {
        get
        {
            var hints = new List<Hint>
            {
                new(PadButton.LB, S["picker.hint.places"]),
                new(PadButton.RB, S["picker.hint.list"]),
                new(PadButton.Y, S[_showAll ? "picker.hint.matching" : "picker.hint.all"]),
            };
            if (ShowUseFolder)
            {
                hints.Add(new Hint(PadButton.X, S["picker.hint.use"]));
            }
            hints.Add(new Hint(PadButton.B, S[_back.Count > 0 ? "hint.back" : "hint.close"]));
            hints.Add(new Hint(PadButton.A, S[_focusOnFile && _request.Kind != PickKind.Folder ? "picker.hint.select" : "picker.hint.open"]));
            return hints;
        }
    }

    public override bool OnButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.B:
                Back();
                break;
            case PadButton.X when CanUseFolder:
                UseFolderCommand.Execute(null);
                break;
            case PadButton.Y:
                _showAll = !_showAll;
                Raise(nameof(ShowAll));
                _shell.RefreshHints();
                Show(_path, Why.Refresh);
                break;
            case PadButton.LB:
                FocusRequested?.Invoke(this, new PickerFocus(PickerZone.Places));
                break;
            case PadButton.RB:
                FocusRequested?.Invoke(this, new PickerFocus(PickerZone.List, -1));
                break;
        }
        return true; // the panel has every button while it is open
    }

    /// <summary>The picker's own texts are made per listing; the launcher's changes are none of its business.</summary>
    public override void OnStateChanged()
    {
    }

    public override void OnOpened()
    {
        _watch.Start();
        Show(_request.InitialFolder is { Length: > 0 } start ? start : _remembered, Why.Open);
    }

    /// <summary>Closes the panel without a pick (B at the start, the dimmed page, the X).</summary>
    public void Cancel() => Close(null);

    private void Close(string? picked)
    {
        if (_closed)
        {
            return;
        }
        _closed = true;
        _watch.Stop();
        _listing?.Cancel();
        _shell.ClosePicker(this);
        _result.TrySetResult(picked);
    }

    /// <summary>The view tells which line has the focus (the hint says Open or Select).</summary>
    public void Focused(PickerEntry? entry)
    {
        var onFile = entry is { IsFolder: false };
        if (onFile != _focusOnFile)
        {
            _focusOnFile = onFile;
            _shell.RefreshHints();
        }
    }

    /// <summary>A or a click on a line: a folder opens, a file is the pick (when files are picked here).</summary>
    public void Activate(PickerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        RelayCommand.Async(async () =>
        {
            if (Busy)
            {
                return;
            }
            if (entry.IsFolder)
            {
                var files = _files;
                // a link that points back above itself opens that folder: a loop never makes the path longer
                var target = entry.Entry.Link ? await Task.Run(() => files.Enter(entry.Path)).ConfigureAwait(true) : entry.Path;
                await ShowAsync(target, Why.Enter).ConfigureAwait(true);
            }
            else if (_request.Kind == PickKind.Folder)
            {
                SetNote(S["picker.folder_only"], "bad");
            }
            else
            {
                await ChooseAsync(entry.Path, isFolder: false).ConfigureAwait(true);
            }
        }).Execute(null);
    }

    /// <summary>The folder above; above a drive, a card or <c>/</c>: every drive and card.</summary>
    private void Up()
    {
        if (_files.Parent(_path) is { } parent)
        {
            Show(parent, Why.Enter, focusPath: _path);
        }
    }

    /// <summary>B: the folder the player came from, and at the start the panel closes.</summary>
    private void Back()
    {
        if (_back.TryPop(out var before))
        {
            Show(before, Why.Back, focusPath: _path);
        }
        else
        {
            Cancel();
        }
    }

    /// <summary>Enter in the path box, OK on the on-screen keyboard, or Go.</summary>
    public void Go() => GoCommand.Execute(null);

    private async Task GoAsync()
    {
        var typed = PathText;
        if (string.IsNullOrWhiteSpace(typed))
        {
            return;
        }
        var (files, current) = (_files, _path);
        var (resolved, isFile) = await Task.Run(() =>
        {
            var path = files.Resolve(typed, current);
            return (path, path is not null && File.Exists(path));
        }).ConfigureAwait(true);
        if (_closed)
        {
            return;
        }
        if (resolved is null)
        {
            SetNote(S.Format("picker.not_found", ("path", typed.Trim())), "bad");
            return;
        }
        if (!isFile)
        {
            await ShowAsync(resolved, Why.Typed).ConfigureAwait(true);
            return;
        }
        // a file's path: its folder is shown, and the file is the pick where files are picked
        await ShowAsync(Path.GetDirectoryName(resolved) ?? "", Why.Typed, focusPath: resolved).ConfigureAwait(true);
        if (_closed)
        {
            return;
        }
        if (_request.Kind == PickKind.Folder)
        {
            SetNote(S["picker.folder_only"], "bad");
        }
        else
        {
            await ChooseAsync(resolved, isFolder: false).ConfigureAwait(true);
        }
    }

    /// <summary>The request's judge says whether the path is taken: yes closes the panel with it, no is said under the list.</summary>
    private async Task ChooseAsync(string path, bool isFolder)
    {
        if (Busy || _closed || path.Length == 0)
        {
            return;
        }
        Busy = true;
        SetNote(S["picker.checking"], "");
        string? problem;
        try
        {
            problem = _request.Validate is { } validate ? await validate(path).ConfigureAwait(true) : null;
        }
        finally
        {
            Busy = false;
        }
        if (_closed)
        {
            return;
        }
        if (problem is { Length: > 0 })
        {
            // the judge names the whole path (Z:\run\media\… under Proton): here its name says which one, the path is above
            var name = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : _files.Display(path);
            SetNote(problem.Replace(path, name, StringComparison.Ordinal), "bad");
            return;
        }
        // Remembered first, in its own save: the caller's next save (the game folder) then starts from settings that have it.
        var folder = isFolder ? path : Path.GetDirectoryName(path) ?? "";
        try
        {
            await _shell.UpdateSettingsAsync(s => s with { Picker = s.Picker.With(_request.Purpose, folder) }).ConfigureAwait(true);
        }
        catch (Exception e) when (e is LauncherException or IOException or UnauthorizedAccessException)
        {
            App.Log("picker: " + e.Message); // the pick still counts
        }
        Close(path);
    }

    private void Show(string path, Why why, string? focusPath = null) =>
        RelayCommand.Async(() => ShowAsync(path, why, focusPath)).Execute(null);

    /// <summary>
    /// Lists a folder off the window's thread and shows it, unless a newer request overtook it.
    /// A folder that is gone: at the start (a remembered folder, a card taken out) the nearest one
    /// still there is shown without a word; typed or opened, the panel stays where it is and says so.
    /// </summary>
    private async Task ShowAsync(string path, Why why, string? focusPath = null)
    {
        if (_closed)
        {
            return;
        }
        _listing?.Cancel();
        var cancel = new CancellationTokenSource();
        _listing = cancel;
        var generation = ++_generation;
        var (files, filter, all, last) = (_files, _request.Filter, _showAll, _remembered);
        var work = Task.Run(() =>
        {
            var places = files.Places(all, last);
            var target = path;
            var fellBack = false;
            if (why == Why.Open)
            {
                target = files.NearestExisting(path);
                if (target.Length == 0)
                {
                    target = (places.FirstOrDefault(p => p.Kind == PlaceKind.Downloads) ?? places.FirstOrDefault(p => p.Kind == PlaceKind.Home))?.Path ?? "";
                }
            }
            var listing = files.List(target, filter, all, cancel.Token);
            if (listing.Problem == ListProblem.NotFound && why is Why.Open or Why.Back or Why.Refresh or Why.Watch)
            {
                fellBack = true;
                listing = files.List(files.NearestExisting(target), filter, all, cancel.Token);
            }
            return (Listing: listing, Places: places, FellBack: fellBack);
        }, cancel.Token);
        try
        {
            if (await Task.WhenAny(work, Task.Delay(220, cancel.Token)).ConfigureAwait(true) != work && generation == _generation)
            {
                IsLoading = true;
                Raise(nameof(EmptyText));
            }
            var (listing, places, fellBack) = await work.ConfigureAwait(true);
            if (generation != _generation || _closed)
            {
                return;
            }
            IsLoading = false;
            if (listing.Problem == ListProblem.NotFound)
            {
                SetNote(S.Format("picker.not_found", ("path", _files.Display(path))), "bad");
                Raise(nameof(EmptyText));
                CommandManager.InvalidateRequerySuggested();
                return;
            }
            if (why is Why.Enter or Why.Typed && !FileBrowser.Same(listing.Path, _path))
            {
                _back.Push(_path);
            }
            _path = listing.Path;
            _shown = listing;
            _pathText = _files.Display(_path);
            _entries = [.. listing.Entries.Select(Row)];
            UpdatePlaces(places);
            _crumbs = BuildCrumbs(places);
            if (fellBack && why == Why.Watch)
            {
                (_note, _noteTone) = (S["picker.drive_gone"], "bad");
            }
            else if (listing.Truncated)
            {
                (_note, _noteTone) = (S.Format("picker.truncated", ("n", S.Number(_entries.Count))), "");
            }
            else if (FolderMatches && _request.MatchNote.Length > 0)
            {
                (_note, _noteTone) = (_request.MatchNote, "good");
            }
            else
            {
                (_note, _noteTone) = (_request.Caption, "");
            }
            RaiseAll();
            CommandManager.InvalidateRequerySuggested();
            _shell.RefreshHints();

            var index = focusPath is null ? -1 : _entries.ToList().FindIndex(e => string.Equals(e.Path, focusPath, StringComparison.Ordinal));
            if (index < 0 && focusPath is not null)
            {
                index = _entries.ToList().FindIndex(e => FileBrowser.Same(e.Path, focusPath));
            }
            FocusRequested?.Invoke(this, FolderMatches && CanUseFolder && index < 0 && why != Why.Refresh
                ? new PickerFocus(PickerZone.UseFolder)
                : new PickerFocus(PickerZone.List, Math.Max(0, index)));
        }
        catch (OperationCanceledException)
        {
            // overtaken by a newer listing, or the panel closed
        }
        finally
        {
            if (ReferenceEquals(_listing, cancel))
            {
                _listing = null;
            }
            cancel.Dispose();
        }
    }

    /// <summary>Every few seconds: the places again (a card arrived or left), and the open folder, when it is gone, gives way to the nearest one still there.</summary>
    private async Task WatchAsync()
    {
        if (_watching || _closed || IsLoading || Busy || _listing is not null)
        {
            return;
        }
        _watching = true;
        try
        {
            var (files, path, all, last, generation) = (_files, _path, _showAll, _remembered, _generation);
            var (places, there) = await Task.Run(() => (files.Places(all, last), path.Length == 0 || Directory.Exists(path))).ConfigureAwait(true);
            if (_closed || generation != _generation)
            {
                return;
            }
            var changed = UpdatePlaces(places);
            if (!there)
            {
                await ShowAsync(path, Why.Watch).ConfigureAwait(true);
            }
            else if (changed && path.Length == 0)
            {
                await ShowAsync("", Why.Refresh).ConfigureAwait(true); // "this device" lists them
            }
        }
        finally
        {
            _watching = false;
        }
    }

    private void SetNote(string text, string tone)
    {
        (_note, _noteTone) = (text, tone);
        Raise(nameof(Note));
        Raise(nameof(NoteTone));
    }

    // ---- words and pictures -----------------------------------------------------------------

    private PickerEntry Row(FileEntry entry)
    {
        if (entry.Place is { } place)
        {
            var (label, detail, icon) = Describe(place);
            return new PickerEntry(entry, label, icon) { Detail = detail };
        }
        return new PickerEntry(entry, entry.Name, entry.Type switch
        {
            FileType.Folder => "Icon.Folder",
            FileType.Drive => "Icon.Drive",
            FileType.Package => "Icon.Package",
            FileType.Program => "Icon.Program",
            _ => "Icon.File",
        })
        {
            Badge = entry.IsFolder && entry.Match ? _request.MatchBadge : "",
            Modified = entry.Modified?.ToString("d", S.Culture) ?? "",
            Size = entry.Size is { } bytes ? SizeText(bytes) : "",
            Dimmed = !entry.IsFolder && (!entry.Match || _request.Kind == PickKind.Folder),
        };
    }

    private string SizeText(long bytes) => bytes < 1_000_000
        ? S.Format("picker.kb", ("n", S.Number((int)Math.Ceiling(bytes / 1000.0))))
        : CoreStrings.Size(bytes);

    /// <summary>A place in words: its label, a second line (free space, or where it is), its icon.</summary>
    private (string Label, string Detail, string Icon) Describe(Place place)
    {
        var letter = place.Path.Length >= 2 && place.Path[1] == ':' ? place.Path[..2] : place.Path.TrimEnd('\\', '/');
        var free = place.Free is { } bytes ? S.Format("picker.free", ("size", CoreStrings.Size(bytes))) : "";
        var linux = _files.Host.LinuxRoot is not null;
        string Disk() => place.Name.Length > 0 ? S.Format("picker.place.drive_named", ("name", place.Name), ("letter", letter)) : S.Format("picker.place.drive", ("letter", letter));
        return place.Kind switch
        {
            PlaceKind.Last => (S["picker.place.last"], Short(_files.Display(place.Path)), "Icon.Clock"),
            PlaceKind.Home => (S["picker.place.home"], linux ? _files.Display(place.Path) : "", "Icon.Home"),
            PlaceKind.Downloads => (S["picker.place.downloads"], "", "Icon.Download"),
            PlaceKind.Desktop => (S["picker.place.desktop"], "", "Icon.Monitor"),
            PlaceKind.Documents => (S["picker.place.documents"], "", "Icon.File"),
            PlaceKind.Launcher => (S["picker.place.launcher"], "", "Icon.Kart"),
            PlaceKind.Removable when linux => (place.Name.Length > 0 ? place.Name : S["picker.place.removable"], free.Length > 0 ? free : S["picker.place.removable"], "Icon.Card"),
            PlaceKind.Removable => (Disk(), free.Length > 0 ? free : S["picker.place.removable"], "Icon.Card"),
            PlaceKind.Steam => (S["picker.place.steam"], SteamWhere(place), "Icon.Library"),
            PlaceKind.Mount => (place.Name, free, "Icon.Drive"),
            PlaceKind.Drive => (Disk(), free, "Icon.Drive"),
            PlaceKind.Network => (S.Format("picker.place.network", ("letter", letter)), "", "Icon.Globe"),
            PlaceKind.Root => (S["picker.place.root"], free, "Icon.Drive"),
            PlaceKind.Prefix => (S.Format("picker.place.prefix", ("letter", letter)), "", "Icon.Drive"),
            _ => (S["picker.place.computer"], "", "Icon.Computer"),
        };
    }

    /// <summary>Which Steam library it is: the card or the home it is on (Linux), else the library's own folder.</summary>
    private string SteamWhere(Place library)
    {
        if (_files.Host.LinuxRoot is not null)
        {
            var holder = _placesNow
                .Where(p => p.Kind is PlaceKind.Removable or PlaceKind.Mount or PlaceKind.Home && p.Path.Length > 0 && FileBrowser.IsUnder(library.Path, p.Path))
                .MaxBy(p => p.Path.Length);
            if (holder is not null)
            {
                return holder.Kind == PlaceKind.Home ? S["picker.place.home"] : holder.Name;
            }
        }
        var folder = library.Path.TrimEnd('\\', '/');
        foreach (var tail in (string[])["common", "steamapps"])
        {
            if (string.Equals(Path.GetFileName(folder), tail, StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.GetDirectoryName(folder) ?? folder;
            }
        }
        return Short(_files.Display(folder));
    }

    /// <summary>The end of a path, for a second line ("…\Games\MKW").</summary>
    private static string Short(string path)
    {
        const int Max = 24;
        var text = path.TrimEnd('\\', '/');
        if (text.Length <= Max)
        {
            return text;
        }
        var tail = text[^Max..];
        var cut = tail.IndexOfAny(['\\', '/']);
        return "…" + (cut >= 0 && cut < Max - 8 ? tail[cut..] : tail);
    }

    /// <summary>The places, updated in place while they are the same ones (the focus on one stays). True when the set changed.</summary>
    private bool UpdatePlaces(IReadOnlyList<Place> places)
    {
        var same = _placesNow.Count == places.Count && _placesNow.Zip(places).All(p => p.First.Kind == p.Second.Kind && p.First.Path == p.Second.Path && p.First.Name == p.Second.Name);
        _placesNow = places;
        if (!same)
        {
            Places.Clear();
            foreach (var place in places)
            {
                var (label, detail, icon) = Describe(place);
                var target = place.Path;
                Places.Add(new PickerPlace(place, label, icon, new RelayCommand(() => Show(target, Why.Enter))) { Detail = detail });
            }
        }
        var current = _path.Length == 0
            ? places.FirstOrDefault(p => p.Kind == PlaceKind.Computer)
            : places.Where(p => p.Path.Length > 0 && FileBrowser.IsUnder(_path, p.Path)).MaxBy(p => p.Path.TrimEnd('\\', '/').Length);
        for (var i = 0; i < Places.Count; i++)
        {
            Places[i].IsCurrent = ReferenceEquals(places[i], current);
            if (same)
            {
                Places[i].Detail = Describe(places[i]).Detail;
            }
        }
        return !same;
    }

    private List<PickerCrumb> BuildCrumbs(IReadOnlyList<Place> places)
    {
        var steps = _files.Crumbs(_path, places);
        var crumbs = new List<PickerCrumb>();
        var from = Math.Max(0, steps.Count - MaxCrumbs);
        if (from > 0)
        {
            var hidden = steps[from - 1].Path;
            crumbs.Add(new PickerCrumb("…", false, new RelayCommand(() => Show(hidden, Why.Enter))));
        }
        for (var i = from; i < steps.Count; i++)
        {
            var step = steps[i];
            var label = step.Kind is { } kind ? Describe(new Place(kind, step.Path, step.Name)).Label : step.Name;
            crumbs.Add(new PickerCrumb(label, i == steps.Count - 1, new RelayCommand(() => Show(step.Path, Why.Enter))));
        }
        return crumbs;
    }
}

/// <summary>The launcher's picks, each with its filter, its judge and its words.</summary>
internal static class PickerRequests
{
    /// <summary>The player's own copy of the game: a package file or the folder that holds exefs and romfs; <paramref name="folderOnly"/>: only a folder.</summary>
    public static PickerRequest GameCopy(ShellViewModel shell, bool folderOnly, string? last)
    {
        var (s, launcher) = (shell.S, shell.Launcher);
        var filter = launcher.GameCopyFilter;
        return new PickerRequest(s[folderOnly ? "setup.game.pick_folder_title" : "setup.game.pick_file_title"], "game_copy", folderOnly ? PickKind.Folder : PickKind.Either)
        {
            Caption = s["picker.dump.caption"],
            Filter = folderOnly ? filter with { Files = false } : filter,
            Validate = path => Task.Run(() => launcher.GameCopyProblem(path)),
            InitialFolder = last,
            MatchBadge = s["picker.badge.copy"],
            MatchNote = s["picker.here.copy"],
            Icon = "Icon.Package",
        };
    }

    /// <summary>The folder of an install of the game's runtime (mkw-recomp: the one with mkw.exe).</summary>
    public static PickerRequest GameFolder(ShellViewModel shell, string name, string hint, string? current)
    {
        var (s, launcher) = (shell.S, shell.Launcher);
        return new PickerRequest(s.Format("settings.game.pick_title", ("hint", hint)), "game_folder")
        {
            Caption = s.Format("picker.game.caption", ("name", name), ("hint", hint)),
            Filter = launcher.GameFolderFilter,
            Validate = folder => Task.Run(() => launcher.GameFolderProblem(folder)),
            InitialFolder = current,
            MatchBadge = name,
            MatchNote = s.Format("picker.here.game", ("name", name)),
            Icon = "Icon.Kart",
        };
    }
}
