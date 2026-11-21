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

    internal ModRow(ShellViewModel shell, GameMod mod, bool update)
    {
        _shell = shell;
        Mod = mod;
        HasUpdate = update;
        OpenCommand = new RelayCommand(Open);
    }

    public GameMod Mod { get; private set; }

    public bool HasUpdate { get; private set; }

    public string Id => Mod.Id;

    public string Name => Mod.Managed ? "MKWMP" : Mod.Name;

    /// <summary>"1.2 · Ana · 12 MB"; for MKWMP's own mod, that the launcher manages it.</summary>
    public string Detail => Mod.Managed
        ? _shell.S["mods.own.detail"]
        : string.Join(" · ", new[] { Mod.Version, Mod.Author, CoreStrings.Size(Mod.Size) }.Where(t => t.Length > 0));

    public string Icon => Mod.Managed ? "Icon.Lock" : Mod.HasPlugin ? "Icon.Program" : "Icon.Package";

    /// <summary>"On", "Off"; "Required" for MKWMP's own.</summary>
    public string State => Mod.Managed ? _shell.S["mods.badge.required"] : Mod.Enabled ? _shell.S["mods.on"] : _shell.S["mods.off"];

    /// <summary>What marks the mod, in the row's caption: an update, native code, options, a problem.</summary>
    public string Badges
    {
        get
        {
            var s = _shell.S;
            var marks = new List<string>();
            if (HasUpdate)
            {
                marks.Add(s["mods.badge.update"]);
            }
            if (Mod.Managed)
            {
                return "";
            }
            if (Mod.HasPlugin)
            {
                marks.Add(s["mods.badge.plugin"]);
            }
            if (Mod.ChangesCode)
            {
                marks.Add(s["mods.badge.code"]);
            }
            if (Mod.HasOptions)
            {
                marks.Add(s["mods.badge.options"]);
            }
            return string.Join("  ·  ", marks);
        }
    }

    public string Caption => string.Join("\n", new[] { Detail, Badges }.Where(t => t.Length > 0));

    /// <summary>X on the row: on or off, when the mod can be switched here.</summary>
    public bool CanToggle => Mod.Locked is null && _shell.Launcher.Mods.Editable;

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
            _shell.Navigate(new ModDetailViewModel(_shell, Mod.Id));
        }
    }

    internal async Task ToggleAsync()
    {
        if (!CanToggle)
        {
            return;
        }
        if (!Mod.Enabled)
        {
            await ModInstaller.EnableAsync(_shell, Mod.Id, Mod.Name).ConfigureAwait(true);
        }
        else if (await _shell.Launcher.SetModEnabledAsync(Mod.Id, on: false, acceptCode: false).ConfigureAwait(true) is { } why)
        {
            await _shell.MessageAsync(Mod.Name, why).ConfigureAwait(true);
        }
    }
}

/// <summary>
/// Mods (the Home dock): the mods in the game, each on or off, and the three ways to get one: GameBanana,
/// a link, a file. A on a mod opens it (its switch, its options, uninstall); X switches it from the list.
/// Everything is the target's (<see cref="ILauncher.Mods"/>): nothing here knows a game.
/// </summary>
public sealed class ModsViewModel : PageViewModel
{
    private ModRow? _focused;

    public ModsViewModel(ShellViewModel shell)
        : base(shell)
    {
        BrowseCommand = new RelayCommand(() => Shell.Navigate(new ModBrowserViewModel(Shell)), () => View.Supported);
        LinkCommand = new RelayCommand(() => Shell.Navigate(new ModLinkViewModel(Shell)), () => View.Supported);
        FileCommand = RelayCommand.Async(FromFileAsync, () => View.Supported);
        Sync();
    }

    private ModsView View => Launcher.Mods;

    public ObservableCollection<ModRow> Rows { get; } = [];

    public bool Loading => !View.Loaded;

    public bool Supported => View.Loaded && View.Supported;

    /// <summary>Why nothing can be changed (the game runs), or why there is no list (no game, a game that takes no mods).</summary>
    public string Note => View.Loaded ? View.Note ?? "" : "";

    public bool IsEmpty => Supported && Rows.All(r => r.Mod.Managed);

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

    public override IReadOnlyList<Hint> Hints => _focused is { CanToggle: true }
        ? [new Hint(PadButton.X, S["mods.hint.toggle"]), new Hint(PadButton.B, S["hint.back"]), new Hint(PadButton.A, S["hint.ok"])]
        : Shell.DefaultHints();

    public override bool OnButton(PadButton button)
    {
        if (button == PadButton.X && _focused is { CanToggle: true } row)
        {
            RelayCommand.Async(row.ToggleAsync).Execute(null);
            return true;
        }
        return false;
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

    /// <summary>The rows follow the list, each kept while its mod is there (the focus stays where it is).</summary>
    private void Sync()
    {
        var view = View;
        // MKWMP's own first: a reference row, not one of the player's mods
        var mods = view.Mods.OrderByDescending(m => m.Managed).ToList();
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
            Shell.Navigate(new ModDetailViewModel(Shell, id)); // its switch and its options, right away
        }
    }
}

/// <summary>
/// One installed mod: its switch, the options it declares (drawn with the game settings' own rows and
/// saved to its settings file), what it is and where it came from, an update when GameBanana has a newer
/// file, and Uninstall.
/// </summary>
public sealed class ModDetailViewModel : PageViewModel
{
    private readonly string _id;
    private GameSettingsSchema? _rowsSchema;

    public ModDetailViewModel(ShellViewModel shell, string id)
        : base(shell)
    {
        _id = id;
        UninstallCommand = RelayCommand.Async(UninstallAsync, () => CanChange);
        PageCommand = new RelayCommand(() => Links.Open(Shell, Mod?.Source?.Url), () => Mod?.Source?.Url is not null);
        UpdateCommand = RelayCommand.Async(UpdateAsync, () => HasUpdate && Launcher.Mods.Editable);
        SyncRows();
    }

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

    public override PageViewModel? Recreate() => Mod is null ? null : new ModDetailViewModel(Shell, _id);

    public override void OnLeave()
    {
        foreach (var row in OptionRows)
        {
            row.Flush();
        }
    }

    public override void OnStateChanged()
    {
        if (Mod is null && ReferenceEquals(Shell.Page, this))
        {
            Shell.GoBack(); // uninstalled (here, or under the launcher's feet)
            return;
        }
        SyncRows();
        base.OnStateChanged();
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

    private async Task UpdateAsync()
    {
        if (Mod is not { Source: { GameBananaMod: > 0 } source } mod || !Launcher.Mods.Updates.TryGetValue(_id, out var file))
        {
            return;
        }
        GbDetails? details = null;
        await ModInstaller.RunAsync(Shell, async (progress, cancel) =>
        {
            details = await Launcher.ModPageAsync(source.GameBananaMod, cancel).ConfigureAwait(true);
            var newest = details.Files.FirstOrDefault(f => f.Id == file.Id) ?? throw new LauncherException(CoreStrings.GameBananaNotThisGame);
            return await Launcher.DownloadModAsync(details, newest, progress, cancel).ConfigureAwait(true);
        }, new ModInstallRequest("")
        {
            Name = mod.Id,
            Replace = true, // an update: the old folder is kept as the backup
            Enable = false, // on or off as it was
            Source = new ModSource(source.Url, source.GameBananaMod, file.Id, file.Version, file.Added),
        }).ConfigureAwait(true);
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
            Shell.Navigate(new ModDetailViewModel(Shell, id)); // its options, right away
        }
    }
}
