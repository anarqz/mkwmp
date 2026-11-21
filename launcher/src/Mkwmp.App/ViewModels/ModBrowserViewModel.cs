// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using Mkwmp.Core;
using Mkwmp.Core.Modding;
using Mkwmp.Core.Targets;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// A preview picture of GameBanana's: asked for when it is first shown, drawn when its file is in the
/// launcher's cache. Until then (and when it never comes) the card keeps its drawn art.
/// </summary>
public sealed class ModPicture : ObservableObject
{
    private ImageSource? _image;

    public ModPicture(ILauncher launcher, string url, bool blurred, string seed)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        Blurred = blurred;
        Seed = seed;
        if (!string.IsNullOrEmpty(url))
        {
            RelayCommand.Async(async () =>
            {
                var path = await launcher.ModImageAsync(url, CancellationToken.None).ConfigureAwait(true);
                Image = path is null ? null : await Task.Run(() => Decode(path, blurred)).ConfigureAwait(true);
            }).Execute(null);
        }
    }

    public ImageSource? Image
    {
        get => _image;
        private set
        {
            if (Set(ref _image, value))
            {
                Raise(nameof(HasImage));
            }
        }
    }

    public bool HasImage => _image is not null;

    /// <summary>
    /// The picture of a file; a blurred one is decoded a dozen pixels wide and drawn large (as mkw-recomp's
    /// own page does), so the sharp picture is never in memory. Null for a file that is not a picture.
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapImage? Decode(string path, bool blurred)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
            bitmap.DecodePixelWidth = blurred ? 12 : 640;
            bitmap.UriSource = new Uri(path);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A mod GameBanana marks as mature, while the player has not chosen to see such pictures: shown blurred, never sharp.</summary>
    public bool Blurred { get; }

    /// <summary>For the drawn art that stands in for the picture.</summary>
    public string Seed { get; }
}

/// <summary>A mod of GameBanana's list, as its card shows it.</summary>
public sealed class ModCard
{
    internal ModCard(ShellViewModel shell, GbMod mod, bool blurred, bool installed)
    {
        Mod = mod;
        Picture = new ModPicture(shell.Launcher, mod.Thumb, blurred, "#" + (mod.Id % 9));
        Subtitle = string.Join(" · ", new[] { mod.Author, mod.Category }.Where(t => t.Length > 0));
        Badge = installed ? shell.S["mods.browse.installed"] : mod.Likes > 0 ? "♥ " + mod.Likes.ToString(CoreStrings.Culture) : "";
        OpenCommand = new RelayCommand(() => shell.Navigate(new ModPageViewModel(shell, mod)));
    }

    public GbMod Mod { get; }

    public string Title => Mod.Name;

    public string Subtitle { get; }

    public string Badge { get; }

    public ModPicture Picture { get; }

    public ICommand OpenCommand { get; }
}

/// <summary>
/// "Get mods": GameBanana's Mario Kart World mods as a grid of cards, twenty a page, in an order or
/// from a search (the text field opens the on-screen keyboard). A on a card opens the mod's page.
/// Nothing is hosted by MKWMP: the list, the pictures and the files come from GameBanana when asked.
/// </summary>
public sealed class ModBrowserViewModel : PageViewModel
{
    private string _query = "";
    private string _asked = "";
    private GbSort _sort = GbSort.Popular;
    private int _page = 1;
    private int _pages = 1;
    private bool _loading;
    private string _error = "";
    private int _generation;

    public ModBrowserViewModel(ShellViewModel shell)
        : base(shell)
    {
        SearchCommand = new RelayCommand(() => Load(1));
        SortCommand = RelayCommand.Async(PickSortAsync);
        MatureCommand = RelayCommand.Async(PickMatureAsync);
        NextCommand = new RelayCommand(() => Load(_page + 1), () => !_loading && _page < _pages);
        PreviousCommand = new RelayCommand(() => Load(_page - 1), () => !_loading && _page > 1);
        Load(1);
    }

    public ObservableCollection<ModCard> Cards { get; } = [];

    /// <summary>What is searched for; empty: the whole list, in the chosen order.</summary>
    public string Query
    {
        get => _query;
        set
        {
            if (Set(ref _query, value ?? "") && _query.Trim() != _asked)
            {
                Load(1); // the field gives its text when the keyboard closes or the focus leaves
            }
        }
    }

    public bool Loading
    {
        get => _loading;
        private set => Set(ref _loading, value);
    }

    public string Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    public bool IsEmpty => !_loading && _error.Length == 0 && Cards.Count == 0;

    public bool IsSearching => _asked.Length > 0;

    public string SortText => S[SortKey(_sort)];

    public string MatureText => Launcher.Mods.ShowMature ? S["mods.browse.mature.show"] : S["mods.browse.mature.blur"];

    public string PageText => S.Format("mods.browse.page", ("page", _page.ToString(CoreStrings.Culture)), ("pages", _pages.ToString(CoreStrings.Culture)));

    public bool HasPages => _pages > 1;

    public ICommand SearchCommand { get; }

    public ICommand SortCommand { get; }

    public ICommand MatureCommand { get; }

    public ICommand NextCommand { get; }

    public ICommand PreviousCommand { get; }

    public override PageViewModel? Recreate() => new ModBrowserViewModel(Shell) { _query = _query, _sort = _sort };

    private static string SortKey(GbSort sort) => sort switch
    {
        GbSort.Newest => "mods.browse.sort.new",
        GbSort.Updated => "mods.browse.sort.updated",
        GbSort.Liked => "mods.browse.sort.liked",
        _ => "mods.browse.sort.popular",
    };

    private void Load(int page)
    {
        var generation = ++_generation;
        var query = _query.Trim();
        _asked = query;
        Loading = true;
        Error = "";
        RelayCommand.Async(async () =>
        {
            try
            {
                var result = await Launcher.BrowseModsAsync(_sort, query, page, CancellationToken.None).ConfigureAwait(true);
                if (generation != _generation)
                {
                    return; // a newer question is on its way
                }
                var mature = Launcher.Mods.ShowMature;
                var installed = Launcher.Mods.Mods.Where(m => m.Source is { GameBananaMod: > 0 }).Select(m => m.Source!.GameBananaMod).ToHashSet();
                Cards.Clear();
                foreach (var mod in result.Mods)
                {
                    Cards.Add(new ModCard(Shell, mod, mod.Rated && !mature, installed.Contains(mod.Id)));
                }
                (_page, _pages) = (result.Page, result.Pages);
            }
            catch (LauncherException e)
            {
                if (generation == _generation)
                {
                    Error = e.Message;
                }
            }
            if (generation == _generation)
            {
                Loading = false;
                RaiseAll();
                CommandManager.InvalidateRequerySuggested();
            }
        }).Execute(null);
    }

    private async Task PickSortAsync()
    {
        var options = Enum.GetValues<GbSort>().Select(v => new ChoiceOption(v.ToString(), S[SortKey(v)])).ToList();
        if (await Choice.AskAsync(Shell, S["mods.browse.sort"], "", options, _sort.ToString()).ConfigureAwait(true) is { } picked && Enum.TryParse<GbSort>(picked, out var sort) && sort != _sort)
        {
            _sort = sort;
            Load(1);
        }
    }

    private async Task PickMatureAsync()
    {
        var now = Launcher.Mods.ShowMature ? "show" : "blur";
        var picked = await Choice.AskAsync(Shell, S["mods.browse.mature"], S["mods.browse.mature.caption"],
            [new ChoiceOption("blur", S["mods.browse.mature.blur"]), new ChoiceOption("show", S["mods.browse.mature.show"])], now).ConfigureAwait(true);
        if (picked is null || picked == now)
        {
            return;
        }
        if (await Launcher.SetShowMatureModsAsync(picked == "show").ConfigureAwait(true) is { } why)
        {
            await Shell.MessageAsync(S["mods.browse.mature"], why).ConfigureAwait(true);
            return;
        }
        Load(_page);
    }
}

/// <summary>A file of a GameBanana mod, as its row shows it.</summary>
public sealed class ModFileRow
{
    internal ModFileRow(GbFile file, string caption, ICommand install)
    {
        File = file;
        Caption = caption;
        InstallCommand = install;
    }

    public GbFile File { get; }

    public string Name => File.Name;

    public string Size => CoreStrings.Size(File.Size);

    public string Caption { get; }

    public ICommand InstallCommand { get; }
}

/// <summary>
/// A GameBanana mod's page: its pictures, who made it and under which license, its description and
/// its files, each with Install. Only a file GameBanana's scan calls clean can be installed.
/// </summary>
public sealed class ModPageViewModel : PageViewModel
{
    private readonly GbMod _mod;
    private GbDetails? _details;
    private string _error = "";

    public ModPageViewModel(ShellViewModel shell, GbMod mod)
        : base(shell)
    {
        _mod = mod ?? throw new ArgumentNullException(nameof(mod));
        OpenCommand = new RelayCommand(() => Links.Open(Shell, _mod.Url));
        RelayCommand.Async(LoadAsync).Execute(null);
    }

    public string Title => _details?.Mod.Name ?? _mod.Name;

    public bool Loading => _details is null && _error.Length == 0;

    public string Error => _error;

    public bool Loaded => _details is not null;

    /// <summary>"by lintu! · Other/Misc · 1.0.0".</summary>
    public string Byline => string.Join(" · ", new[]
    {
        _mod.Author.Length > 0 ? S.Format("mods.page.by", ("author", _mod.Author)) : "",
        _mod.Category,
        (_details?.Mod.Version ?? _mod.Version) is { Length: > 0 } version ? version : "",
    }.Where(t => t.Length > 0));

    public string License => _details is { License.Length: > 0 } d ? S.Format("mods.page.license", ("license", d.License)) : "";

    public string Rated => _details is { Ratings.Length: > 0 } d ? S.Format("mods.page.rated", ("ratings", d.Ratings)) : "";

    private bool Blurred => (_details?.Mod.Rated ?? _mod.Rated) && !Launcher.Mods.ShowMature;

    public string BlurNote => Blurred ? S["mods.page.blurred"] : "";

    public string Description => _details?.Text ?? "";

    public ObservableCollection<ModPicture> Shots { get; } = [];

    public bool HasShots => Shots.Count > 0;

    public ObservableCollection<ModFileRow> Files { get; } = [];

    public ICommand OpenCommand { get; }

    public override PageViewModel? Recreate() => new ModPageViewModel(Shell, _mod);

    private async Task LoadAsync()
    {
        try
        {
            _details = await Launcher.ModPageAsync(_mod.Id, CancellationToken.None).ConfigureAwait(true);
            foreach (var image in _details.Images.Take(4))
            {
                Shots.Add(new ModPicture(Launcher, image.Small, Blurred, "#" + (Shots.Count + (_mod.Id % 5))));
            }
            foreach (var file in _details.Files.OrderByDescending(f => f.Added))
            {
                var caption = string.Join(" · ", new[] { CoreStrings.Size(file.Size), file.Version, file.Clean ? file.Note : S["mods.page.unsafe"] }.Where(t => t.Length > 0));
                Files.Add(new ModFileRow(file, caption, RelayCommand.Async(() => InstallAsync(file), () => file.Clean)));
            }
        }
        catch (LauncherException e)
        {
            _error = e.Message;
        }
        RaiseAll();
    }

    private async Task InstallAsync(GbFile file)
    {
        if (_details is not { } details || !await ModInstaller.AskAsync(Shell, details.Mod.Name).ConfigureAwait(true))
        {
            return;
        }
        // an update of a mod installed from this page keeps its name in the game; else the page's title names it
        var installed = Launcher.Mods.Mods.FirstOrDefault(m => m.Source?.GameBananaMod == details.Mod.Id);
        var id = await ModInstaller.RunAsync(Shell, (progress, cancel) => Launcher.DownloadModAsync(details, file, progress, cancel), new ModInstallRequest("")
        {
            Name = installed?.Id ?? details.Mod.Name,
            Replace = installed is not null,
            Source = new ModSource(details.Mod.Url, details.Mod.Id, file.Id, file.Version.Length > 0 ? file.Version : details.Mod.Version, file.Added),
        }).ConfigureAwait(true);
        if (id is not null && ReferenceEquals(Shell.Page, this))
        {
            Shell.Navigate(new ModDetailViewModel(Shell, id)); // its switch and its options, right away
        }
    }
}
