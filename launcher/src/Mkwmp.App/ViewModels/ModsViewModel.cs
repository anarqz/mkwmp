// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using Mkwmp.App.Input;
using Mkwmp.Core;
using Mkwmp.Core.Modding;
using Mkwmp.Core.Targets;
using CoreStrings = Mkwmp.Core.Strings;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// The questions and the progress of installing a mod, the same from a file, a link and GameBanana
/// (launcher/docs/MODS.md): the archive is fetched, the core checks and unpacks it, and what it asks
/// back (which mod of several, replace the one installed, a mod that runs code) is asked here.
/// </summary>
public static class ModInstaller
{
    /// <summary>
    /// Runs an install to its end. <paramref name="obtain"/> gives the archive's path (a download, or the
    /// file the player picked). The id of the mod now in the game, or null: cancelled or failed (said in a modal).
    /// </summary>
    public static async Task<string?> RunAsync(ShellViewModel shell, Func<IProgress<ModProgress>, CancellationToken, Task<string>> obtain, ModInstallRequest template)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(obtain);
        ArgumentNullException.ThrowIfNull(template);
        var s = shell.S;
        using var cancel = new CancellationTokenSource();
        var modal = shell.ShowProgress(s["mods.install.title"], s["mods.install.downloading"]);
        modal.Bottom = new ModalAction(s["common.cancel"], cancel.Cancel);
        var progress = new Progress<ModProgress>(p =>
        {
            modal.Body = s[p.Stage switch { "download" => "mods.install.downloading", "unpack" => "mods.install.unpacking", "check" => "mods.install.checking", _ => "mods.install.placing" }];
            if (!double.IsNaN(p.Fraction))
            {
                modal.Report(p.Fraction, p.Stage == "download" ? $"{CoreStrings.Size(p.Done)} / {CoreStrings.Size(p.Total)}" : "");
            }
        });
        try
        {
            string path;
            try
            {
                path = await obtain(progress, cancel.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (LauncherException e)
            {
                modal.Close();
                await shell.MessageAsync(s["mods.install.failed"], e.Message).ConfigureAwait(true);
                return null;
            }
            var request = template with { Path = path };
            while (true)
            {
                modal.Body = s["mods.install.unpacking"];
                var result = await shell.Launcher.InstallModAsync(request, progress, cancel.Token).ConfigureAwait(true);
                switch (result.Code)
                {
                    case ModInstallCode.Done:
                        modal.Close();
                        if (result is { HasPlugin: true, Enabled: false, ModId: { } plugin })
                        {
                            await EnableAsync(shell, plugin, shell.Launcher.Mods.Find(plugin)?.Name ?? plugin).ConfigureAwait(true);
                        }
                        shell.ShowToast(s["mods.install.done"], result.Message);
                        return result.ModId;
                    case ModInstallCode.Variants when result.Variants is { Count: > 0 } variants:
                        modal.Close();
                        var pick = await Choice.AskAsync(shell, s["mods.install.variants"], result.Message, [.. variants.Select(v => new ChoiceOption(v, v == "." ? Path.GetFileNameWithoutExtension(path) : v))], null).ConfigureAwait(true);
                        if (pick is null)
                        {
                            return null;
                        }
                        request = request with { Variant = pick };
                        break;
                    case ModInstallCode.Exists when result.ModId is { } taken:
                        modal.Close();
                        if (!await shell.ConfirmAsync(s.Format("mods.install.replace.title", ("name", taken)), s["mods.install.replace.body"], s["mods.install.replace.yes"]).ConfigureAwait(true))
                        {
                            return null;
                        }
                        request = request with { Replace = true };
                        break;
                    case ModInstallCode.Cancelled:
                        return null;
                    default:
                        modal.Close();
                        await shell.MessageAsync(s["mods.install.failed"], result.Message).ConfigureAwait(true);
                        return null;
                }
                shell.ShowModal(modal); // the question is answered: the progress again
            }
        }
        finally
        {
            modal.Close();
        }
    }

    /// <summary>
    /// Turns a mod on; one that carries native code only after the player read what that means and said
    /// yes. True when it is on. A refusal of the core (the game runs) is said in a modal.
    /// </summary>
    public static async Task<bool> EnableAsync(ShellViewModel shell, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var s = shell.S;
        var mod = shell.Launcher.Mods.Find(id);
        var code = mod?.HasPlugin == true;
        if (code && !await shell.ConfirmAsync(s["mods.code.title"], s.Format("mods.code.body", ("name", name)), s["mods.code.yes"], s["mods.code.no"]).ConfigureAwait(true))
        {
            return false;
        }
        if (await shell.Launcher.SetModEnabledAsync(id, on: true, acceptCode: code).ConfigureAwait(true) is { } why)
        {
            await shell.MessageAsync(name, why).ConfigureAwait(true);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Installs the newer file GameBanana has for an installed mod, in its place (the old folder kept as the
    /// backup), on or off as it was. Nothing when there is no update for it.
    /// </summary>
    public static async Task UpdateAsync(ShellViewModel shell, string id)
    {
        ArgumentNullException.ThrowIfNull(shell);
        var launcher = shell.Launcher;
        if (launcher.Mods.Find(id) is not { Source: { GameBananaMod: > 0 } source } mod || !launcher.Mods.Updates.TryGetValue(id, out var file))
        {
            return;
        }
        await RunAsync(shell, async (progress, cancel) =>
        {
            var details = await launcher.ModPageAsync(source.GameBananaMod, cancel).ConfigureAwait(true);
            var newest = details.Files.FirstOrDefault(f => f.Id == file.Id) ?? throw new LauncherException(CoreStrings.GameBananaNotThisGame);
            return await launcher.DownloadModAsync(details, newest, progress, cancel).ConfigureAwait(true);
        }, new ModInstallRequest("")
        {
            Name = mod.Id,
            Replace = true, // an update: the old folder is kept as the backup
            Enable = false, // on or off as it was
            Source = new ModSource(source.Url, source.GameBananaMod, file.Id, file.Version, file.Added),
        }).ConfigureAwait(true);
    }

    /// <summary>The risk of any mod, asked before one is fetched from the network. True: go on.</summary>
    public static Task<bool> AskAsync(ShellViewModel shell, string name)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return shell.ConfirmAsync(shell.S.Format("mods.install.risk.title", ("name", name)), shell.S["mods.install.risk.body"], shell.S["mods.install.yes"]);
    }
}

/// <summary>An installed mod, as its row shows it.</summary>
public sealed class ModRow : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _busy;

    internal ModRow(ShellViewModel shell, GameMod mod, bool update)
    {
        _shell = shell;
        Mod = mod;
        HasUpdate = update;
        OpenCommand = new RelayCommand(Open);
        UpdateCommand = RelayCommand.Async(() => ModInstaller.UpdateAsync(_shell, Mod.Id), () => HasUpdate && _shell.Launcher.Mods.Editable);
    }

    public GameMod Mod { get; private set; }

    public bool HasUpdate { get; private set; }

    public string Id => Mod.Id;

    public Strings S => _shell.S;

    public string Name => Mod.Managed ? "MKWMP" : Mod.Name;

    /// <summary>"1.2 · Ana", small beside the name when it fits; for MKWMP's own mod, that the launcher manages it.</summary>
    public string Detail => Mod.Managed
        ? _shell.S["mods.own.detail"]
        : string.Join(" · ", new[] { Mod.Version, Mod.Author }.Where(t => t.Length > 0));

    /// <summary>"86 MB", at the right of the row.</summary>
    public string Size => Mod.Size > 0 ? CoreStrings.Size(Mod.Size) : "";

    public string Icon => Mod.Managed ? "Icon.Lock" : Mod.HasPlugin ? "Icon.Program" : "Icon.Package";

    /// <summary>The quiet chip of the row: it runs or patches code.</summary>
    public bool ShowsCode => !Mod.Managed && (Mod.HasPlugin || Mod.ChangesCode);

    /// <summary>The row's "Options" button: the mod declares options (it opens the mod's panel, as A on the row does).</summary>
    public bool ShowsOptions => !Mod.Managed && Mod.HasOptions;

    /// <summary>The row's "Update" button: GameBanana has a newer file.</summary>
    public ICommand UpdateCommand { get; }

    /// <summary>The row's switch (Settings' own): on, off, or locked where it is (MKWMP's own, a code mod, the game running).</summary>
    public bool IsOn
    {
        get => Mod.Enabled;
        set
        {
            if (value != Mod.Enabled)
            {
                RelayCommand.Async(ToggleAsync).Execute(null);
            }
        }
    }

    /// <summary>For a screen reader: the switch of this mod.</summary>
    public string SwitchName => _shell.S.Format("mods.switch", ("name", Name));

    /// <summary>X on the row, or its switch: on or off, when the mod can be switched here.</summary>
    public bool CanToggle => ModsView.Switchable(Mod) && _shell.Launcher.Mods.Editable;

    public ICommand OpenCommand { get; }

    internal void Update(GameMod mod, bool update)
    {
        Mod = mod;
        HasUpdate = update;
        RaiseAll();
    }

    private void Open()
    {
        if (Mod.Managed)
        {
            _shell.OpenSettings("game"); // MKWMP's own mod is the integration's: its install and repair live there
        }
        else
        {
            _shell.OpenDrawer(new ModDetailViewModel(_shell, Mod.Id)); // over the list, which stays where it was
        }
    }

    internal async Task ToggleAsync()
    {
        if (!CanToggle || _busy)
        {
            await Task.Yield(); // after the binding that called: then it reads the value again
            Raise(nameof(IsOn)); // the switch goes back to where the game has it
            return;
        }
        _busy = true;
        try
        {
            if (!Mod.Enabled)
            {
                await ModInstaller.EnableAsync(_shell, Mod.Id, Mod.Name).ConfigureAwait(true); // a mod with native code asks first
            }
            else if (await _shell.Launcher.SetModEnabledAsync(Mod.Id, on: false, acceptCode: false).ConfigureAwait(true) is { } why)
            {
                await _shell.MessageAsync(Mod.Name, why).ConfigureAwait(true);
            }
        }
        finally
        {
            _busy = false;
            Raise(nameof(IsOn)); // a "Leave Off" or a refusal leaves the switch where the game has it
        }
    }
}

/// <summary>One of the chips over the list of mods: which of them it shows.</summary>
public sealed class ModFilter : ObservableObject
{
    private bool _isSelected;
    private bool _isVisible = true;

    internal ModFilter(string key, string label, Action<ModFilter> select)
    {
        Key = key;
        Label = label;
        SelectCommand = new RelayCommand(() => select(this));
    }

    /// <summary>all, on, off, updates.</summary>
    public string Key { get; }

    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => Set(ref _isSelected, value);
    }

    /// <summary>"Updates" shows only while GameBanana has a newer file for one of them.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        internal set => Set(ref _isVisible, value);
    }

    public ICommand SelectCommand { get; }

    internal bool Shows(GameMod mod, bool update) => Key switch
    {
        "on" => mod.Enabled,
        "off" => !mod.Enabled,
        "updates" => update,
        _ => true,
    };
}

/// <summary>
/// Mods (the Home dock): the three ways to get a mod in one line (GameBanana, a link, a file), then the
/// mods in the game, each on or off, filtered by the chips beside "Installed". A on a mod opens its drawer
/// (its switch, its options, uninstall); X switches it from the list; Y turns them all off and back on.
/// Everything is the target's (<see cref="ILauncher.Mods"/>): nothing here knows a game.
/// </summary>
public sealed class ModsViewModel : PageViewModel
{
    private ModRow? _focused;
    private ModFilter _filter;

    public ModsViewModel(ShellViewModel shell)
        : base(shell)
    {
        BrowseCommand = new RelayCommand(() => Shell.Navigate(new ModBrowserViewModel(Shell)), () => View.Supported);
        LinkCommand = new RelayCommand(() => Shell.Navigate(new ModLinkViewModel(Shell)), () => View.Supported);
        FileCommand = RelayCommand.Async(FromFileAsync, () => View.Supported);
        AllCommand = RelayCommand.Async(AllAsync, () => HasAll);
        Filters =
        [
            new ModFilter("all", S["mods.filter.all"], Select),
            new ModFilter("on", S["mods.on"], Select),
            new ModFilter("off", S["mods.off"], Select),
            new ModFilter("updates", S["mods.filter.updates"], Select),
        ];
        _filter = Filters[0];
        _filter.IsSelected = true;
        Sync();
    }

    private ModsView View => Launcher.Mods;

    public ObservableCollection<ModRow> Rows { get; } = [];

    /// <summary>The chips beside "Installed": All, On, Off, Updates.</summary>
    public IReadOnlyList<ModFilter> Filters { get; }

    public bool Loading => !View.Loaded;

    public bool Supported => View.Loaded && View.Supported;

    /// <summary>Why nothing can be changed (the game runs), or why there is no list (no game, a game that takes no mods).</summary>
    public string Note => View.Loaded ? View.Note ?? "" : "";

    /// <summary>No mod of the player's in the game (MKWMP's own aside).</summary>
    public bool IsEmpty => Supported && View.Mods.All(m => m.Managed);

    /// <summary>The chips are worth showing: there are mods of the player's to sort.</summary>
    public bool HasFilters => !IsEmpty;

    /// <summary>The chip chosen hides every mod.</summary>
    public bool IsFilteredEmpty => !IsEmpty && Rows.Count == 0;

    public ICommand BrowseCommand { get; }

    public ICommand LinkCommand { get; }

    public ICommand FileCommand { get; }

    /// <summary>The row under the focus (the view tells): X switches it.</summary>
    public ModRow? Focused
    {
        get => _focused;
        set
        {
            if (Set(ref _focused, value))
            {
                RefreshHints();
            }
        }
    }

    /// <summary>"Turn all off" and "Turn all back on" ask first from this many mods on.</summary>
    internal const int ManyMods = 5;

    /// <summary>The list kept by "Turn all off" has mods to bring back: the action brings them back.</summary>
    private bool BringsBack => View.ToBringBack > 0;

    /// <summary>There is something for "Turn all off" or "Turn all back on" to do (the Y button, the button over the list).</summary>
    public bool HasAll => Supported && (BringsBack || View.OnNow > 0);

    public string AllLabel => BringsBack ? S["mods.all.on"] : S["mods.all.off"];

    public ICommand AllCommand { get; }

    public override IReadOnlyList<Hint> Hints
    {
        get
        {
            var hints = new List<Hint>();
            if (HasAll)
            {
                hints.Add(new Hint(PadButton.Y, AllLabel));
            }
            else if (!Shell.NextendoFirst)
            {
                hints.Add(new Hint(PadButton.Y, S["hint.friends"])); // friends are an MKWMP server's (ShellViewModel.DefaultHints)
            }
            if (_focused is { CanToggle: true })
            {
                hints.Add(new Hint(PadButton.X, S["mods.hint.toggle"]));
            }
            hints.AddRange(Shell.DefaultHints().Where(h => h.Button != PadButton.Y));
            return hints;
        }
    }

    public override bool OnButton(PadButton button)
    {
        if (button == PadButton.X && _focused is { CanToggle: true } row)
        {
            RelayCommand.Async(row.ToggleAsync).Execute(null);
            return true;
        }
        if (button == PadButton.Y && HasAll)
        {
            AllCommand.Execute(null); // RB still opens Friends
            return true;
        }
        return false;
    }

    /// <summary>
    /// "Turn all off": every mod the player can switch, never MKWMP's own or a locked one, the list kept to bring
    /// back. "Turn all back on": that list, in its order. The game reads its list when it starts: nothing while it runs.
    /// </summary>
    private async Task AllAsync()
    {
        var view = View;
        var on = view.ToBringBack > 0;
        var count = on ? view.ToBringBack : view.OnNow;
        var label = AllLabel;
        if (count == 0)
        {
            return;
        }
        if (!view.Editable)
        {
            await Shell.MessageAsync(label, S["mods.all.running"]).ConfigureAwait(true);
            return;
        }
        var n = ("n", count.ToString(CoreStrings.Culture));
        if (count >= ManyMods && !await Shell.ConfirmAsync(S.Format(on ? "mods.all.on.title" : "mods.all.off.title", n),
                S[on ? "mods.all.on.body" : "mods.all.off.body"], S[on ? "mods.all.on.yes" : "mods.all.off.yes"]).ConfigureAwait(true))
        {
            return;
        }
        var (changed, why) = await Launcher.SetAllModsEnabledAsync(on).ConfigureAwait(true);
        if (why is not null)
        {
            await Shell.MessageAsync(label, why).ConfigureAwait(true);
            return;
        }
        if (changed > 0)
        {
            Shell.ShowToast(S.Format(on ? "mods.all.on.done" : "mods.all.off.done", ("n", changed.ToString(CoreStrings.Culture))), S["mods.restart"]);
        }
    }

    public override PageViewModel? Recreate() => new ModsViewModel(Shell);

    public override void OnEnter()
    {
        RelayCommand.Async(async () =>
        {
            await Launcher.RefreshModsAsync().ConfigureAwait(true);
            if (Launcher.Mods.Mods.Any(m => m.Source is { GameBananaMod: > 0 }))
            {
                await Launcher.CheckModUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }).Execute(null);
    }

    public override void OnStateChanged()
    {
        Sync();
        base.OnStateChanged();
        RefreshHints();
    }

    private void Select(ModFilter filter)
    {
        if (ReferenceEquals(filter, _filter))
        {
            return;
        }
        _filter.IsSelected = false;
        _filter = filter;
        filter.IsSelected = true;
        Sync();
        Raise(nameof(IsFilteredEmpty));
    }

    /// <summary>The rows follow the list and the chip, each kept while its mod is there (the focus stays where it is).</summary>
    private void Sync()
    {
        var view = View;
        var updates = Filters[^1];
        updates.IsVisible = view.Updates.Count > 0;
        if (!updates.IsVisible && ReferenceEquals(_filter, updates))
        {
            _filter.IsSelected = false;
            _filter = Filters[0]; // the last update is in: back to all of them
            _filter.IsSelected = true;
        }
        // MKWMP's own first: a reference row, not one of the player's mods
        var mods = view.Mods.Where(m => _filter.Shows(m, view.Updates.ContainsKey(m.Id))).OrderByDescending(m => m.Managed).ToList();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!mods.Any(m => m.Id == Rows[i].Id))
            {
                Rows.RemoveAt(i);
            }
        }
        for (var i = 0; i < mods.Count; i++)
        {
            var update = view.Updates.ContainsKey(mods[i].Id);
            if (i < Rows.Count && Rows[i].Id == mods[i].Id)
            {
                Rows[i].Update(mods[i], update);
            }
            else
            {
                Rows.Insert(i, new ModRow(Shell, mods[i], update));
            }
        }
    }

    private async Task FromFileAsync()
    {
        var path = await Shell.PickAsync(new PickerRequest(S["mods.file"], "mod", PickKind.Either)
        {
            Caption = S["mods.file.pick"],
            Filter = new Mkwmp.Core.Files.PickFilter
            {
                Files = true,
                Extensions = [".zip", ".7z", ".rar"],
                FolderMatches = folder => Directory.Exists(Path.Combine(folder, "romfs")) || Directory.Exists(Path.Combine(folder, "plugin")),
            },
            Icon = "Icon.Package",
        }).ConfigureAwait(true);
        if (path is not null && await ModInstaller.RunAsync(Shell, (_, _) => Task.FromResult(path), new ModInstallRequest(path)).ConfigureAwait(true) is { } id
            && ReferenceEquals(Shell.Page, this))
        {
            Shell.OpenDrawer(new ModDetailViewModel(Shell, id)); // its switch and its options, right away
        }
    }
}

/// <summary>
/// One installed mod, in the drawer over the page (<see cref="ShellViewModel.OpenDrawer"/>): its switch, the
/// options it declares (drawn with the game settings' own rows and saved to its settings file), what it is
/// and where it came from, an update when GameBanana has a newer file. Y uninstalls it, B closes the drawer.
/// </summary>
public sealed class ModDetailViewModel : LayerViewModel
{
    private readonly string _id;
    private GameSettingsSchema? _rowsSchema;

    public ModDetailViewModel(ShellViewModel shell, string id)
    {
        Shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _id = id;
        UninstallCommand = RelayCommand.Async(UninstallAsync, () => CanChange && CanUninstall);
        PageCommand = new RelayCommand(() => Links.Open(Shell, Mod?.Source?.Url), () => Mod?.Source?.Url is not null);
        UpdateCommand = RelayCommand.Async(() => ModInstaller.UpdateAsync(Shell, _id), () => HasUpdate && Launcher.Mods.Editable);
        SyncRows();
    }

    public ShellViewModel Shell { get; }

    public Strings S => Shell.S;

    private ILauncher Launcher => Shell.Launcher;

    private GameMod? Mod => Launcher.Mods.Find(_id);

    private GameSettingsView Options => Launcher.ModOptions(_id);

    public string Title => Mod?.Name ?? _id;

    public string Icon => Mod?.HasPlugin == true ? "Icon.Program" : "Icon.Package";

    public string Description => Mod?.Description ?? "";

    private bool CanChange => Mod is { Locked: null } && Launcher.Mods.Editable;

    public bool CanToggle => CanChange;

    public bool IsOn
    {
        get => Mod?.Enabled == true;
        set
        {
            if (value == IsOn || Mod is not { } mod)
            {
                return;
            }
            RelayCommand.Async(async () =>
            {
                if (value)
                {
                    await ModInstaller.EnableAsync(Shell, mod.Id, mod.Name).ConfigureAwait(true);
                }
                else if (await Launcher.SetModEnabledAsync(mod.Id, on: false, acceptCode: false).ConfigureAwait(true) is { } why)
                {
                    await Shell.MessageAsync(mod.Name, why).ConfigureAwait(true);
                }
                RaiseAll(); // a refusal leaves the switch where the game has it
            }).Execute(null);
        }
    }

    /// <summary>Why the switch is locked, what the mod carries, and that the game reads the list when it starts.</summary>
    public string SwitchCaption => string.Join("\n", new[]
    {
        Mod?.Locked ?? "",
        Mod is { HasPlugin: true, Locked: null } ? S["mods.detail.plugin"] : "",
        S["mods.restart"],
    }.Where(t => t.Length > 0));

    /// <summary>The game runs, or a change was refused.</summary>
    public string Note => Launcher.Mods.Note ?? "";

    public string Problems => string.Join("\n", Mod?.Problems ?? []);

    public ObservableCollection<GameSettingRow> OptionRows { get; } = [];

    public bool HasOptions => OptionRows.Count > 0;

    public string OptionsNote => string.Join("\n", new[] { Options.Schema?.Note ?? "", Options.NoteIsError ? Options.Note ?? "" : "" }.Where(t => t.Length > 0));

    public bool OptionsNoteIsError => Options.NoteIsError;

    public string Version => Mod?.Version ?? "";

    public string Author => Mod?.Author ?? "";

    public string Size => Mod is { } mod ? CoreStrings.Size(mod.Size) : "";

    /// <summary>"12 game files", "runs code": what is inside.</summary>
    public string Contents => Mod is not { } mod ? "" : string.Join(" · ", new[]
    {
        mod.HasFiles ? S.Format("mods.detail.files", ("n", mod.FileCount.ToString(CoreStrings.Culture))) : "",
        mod.HasPlugin ? S["mods.badge.plugin"] : "",
        mod.ChangesCode ? S["mods.badge.code"] : "",
    }.Where(t => t.Length > 0));

    public string Source => Mod?.Source is not { } source ? "" : source.GameBananaMod > 0 ? "GameBanana" : source.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";

    public bool HasPage => Mod?.Source?.Url is not null;

    public bool HasUpdate => Launcher.Mods.Updates.ContainsKey(_id);

    public string UpdateLabel => Launcher.Mods.Updates.TryGetValue(_id, out var file)
        ? file.Version.Length > 0 ? S.Format("mods.detail.update", ("version", file.Version)) : S["mods.badge.update"]
        : "";

    public string UpdateCaption => Launcher.Mods.Updates.TryGetValue(_id, out var file) ? S.Format("mods.detail.update.caption", ("file", file.Name)) : "";

    public bool CanUninstall => Mod is { Managed: false, ChangesCode: false };

    public ICommand UninstallCommand { get; }

    public ICommand PageCommand { get; }

    public ICommand UpdateCommand { get; }

    /// <summary>Y uninstalls (when it can be), B closes the drawer.</summary>
    public override IReadOnlyList<Hint> Hints
    {
        get
        {
            var hints = new List<Hint>();
            if (CanUninstall && CanChange)
            {
                hints.Add(new Hint(PadButton.Y, S["mods.detail.uninstall"]));
            }
            hints.Add(new Hint(PadButton.B, S["hint.back"]));
            hints.Add(new Hint(PadButton.A, S["hint.ok"]));
            return hints;
        }
    }

    public override bool OnButton(PadButton button)
    {
        if (button == PadButton.Y && CanUninstall && CanChange)
        {
            UninstallCommand.Execute(null);
        }
        return button is not PadButton.B; // Y and RB are not the friends panel's while the drawer is open
    }

    public override void OnClosed()
    {
        foreach (var row in OptionRows)
        {
            row.Flush();
        }
    }

    public override void OnStateChanged()
    {
        if (Mod is null)
        {
            Shell.CloseDrawer(this); // uninstalled (here, or under the launcher's feet)
            return;
        }
        SyncRows();
        base.OnStateChanged();
        Shell.RefreshHints(); // Uninstall comes and goes with the game running
    }

    /// <summary>The option rows: built once per schema, refreshed in place after (as Settings › Game settings does).</summary>
    private void SyncRows()
    {
        var view = Options;
        if (view.Schema is null)
        {
            OptionRows.Clear();
            _rowsSchema = null;
            return;
        }
        if (!ReferenceEquals(_rowsSchema, view.Schema))
        {
            OptionRows.Clear();
            string? group = null;
            foreach (var setting in view.Schema.Settings)
            {
                OptionRows.Add(new GameSettingRow(Shell, setting, view, setting.Group is not null && setting.Group != group,
                    (key, value) => Launcher.SetModOptionAsync(_id, key, value), key => Launcher.SetModOptionAsync(_id, key, null)));
                group = setting.Group;
            }
            _rowsSchema = view.Schema;
            return;
        }
        foreach (var row in OptionRows)
        {
            row.Refresh(view);
        }
    }

    private async Task UninstallAsync()
    {
        if (Mod is not { } mod || !await Shell.ConfirmAsync(S.Format("mods.detail.uninstall.title", ("name", mod.Name)), S["mods.detail.uninstall.body"], S["mods.detail.uninstall"]).ConfigureAwait(true))
        {
            return;
        }
        if (await Launcher.UninstallModAsync(mod.Id).ConfigureAwait(true) is { } why)
        {
            await Shell.MessageAsync(mod.Name, why).ConfigureAwait(true);
        }
    }

}

/// <summary>"Install from a link": the address of an archive, typed or pasted (the on-screen keyboard pastes), downloaded and installed.</summary>
public sealed class ModLinkViewModel : PageViewModel
{
    private string _link = "";

    public ModLinkViewModel(ShellViewModel shell, string link = "")
        : base(shell)
    {
        _link = link;
        InstallCommand = RelayCommand.Async(InstallAsync, () => IsLink);
    }

    public string Link
    {
        get => _link;
        set
        {
            if (Set(ref _link, (value ?? "").Trim()))
            {
                Raise(nameof(IsLink));
                Raise(nameof(Problem));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsLink => ModDownloader.Link(_link) is not null;

    /// <summary>Why what was typed is not a link the launcher downloads from; "" while empty or fine.</summary>
    public string Problem => _link.Length == 0 || IsLink ? "" : CoreStrings.ModsBadLink;

    public ICommand InstallCommand { get; }

    public override PageViewModel? Recreate() => new ModLinkViewModel(Shell, _link);

    private async Task InstallAsync()
    {
        if (ModDownloader.Link(_link) is not { } uri || !await ModInstaller.AskAsync(Shell, uri.Host).ConfigureAwait(true))
        {
            return;
        }
        var id = await ModInstaller.RunAsync(Shell, (progress, cancel) => Launcher.DownloadModAsync(uri.AbsoluteUri, progress, cancel),
            new ModInstallRequest("") { Source = new ModSource(uri.AbsoluteUri) }).ConfigureAwait(true);
        if (id is not null && ReferenceEquals(Shell.Page, this))
        {
            Shell.GoBack();
            Shell.OpenDrawer(new ModDetailViewModel(Shell, id)); // its options, right away, over the list
        }
    }
}
