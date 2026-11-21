// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.IO;
using System.Text;
using Mkwmp.Core;
using Mkwmp.Core.Modding;
using Mkwmp.Core.Targets;

namespace Mkwmp.App.Design;

/// <summary>
/// The demo's mods (launcher/docs/MODS.md): a pretend game with MKWMP's own mod and four others, one
/// with options (through the real <c>options.json</c> reader), and a pretend GameBanana: made-up mods
/// without pictures, so a screenshot never shows anybody's work. Nothing touches a disk or the network.
/// </summary>
public sealed partial class DesignLauncher
{
    private const string DemoOptions = """
        {
          "schema": 1,
          "note": "The mod reads these when the game starts.",
          "options": [
            {"key": "show", "type": "toggle", "label": "Show the Timer", "description": "The lap timer in the corner of the screen during a race.", "default": true, "group": "Timer"},
            {"key": "corner", "type": "choice", "label": "Corner", "default": "tr", "group": "Timer",
             "choices": [{"value": "tl", "label": "Top Left"}, {"value": "tr", "label": "Top Right"}, {"value": "bl", "label": "Bottom Left"}, {"value": "br", "label": "Bottom Right"}],
             "visible_when": {"key": "show", "values": ["1"]}},
            {"key": "scale", "type": "range", "label": "Size", "min": 50, "max": 200, "step": 10, "unit": "%", "default": 100, "group": "Timer", "visible_when": {"key": "show", "values": ["1"]}},
            {"key": "splits", "type": "toggle", "label": "Split Times", "description": "The difference to your best lap, after each lap.", "default": false, "group": "Laps"},
            {"key": "tag", "type": "text", "label": "Ghost Name", "description": "The name saved with your best laps.", "max_length": 12, "default": "", "group": "Laps"}
          ]
        }
        """;

    private readonly Dictionary<string, string> _modValues = new(StringComparer.Ordinal) { ["scale"] = "120" };
    private List<GameMod> _mods =
    [
        new("mkwmp", "MKWMP online play") { Version = "1.4.0", Enabled = true, HasPlugin = true, Managed = true, Locked = "MKWMP's own mod: the launcher installs it, updates it and keeps it on.", Size = 3_400_000 },
        new("Neon Karts", "Neon Karts") { Version = "2.1", Author = "pixelfox", Description = "Glowing paint for every kart, bike and ATV.", Enabled = true, HasFiles = true, FileCount = 48, Size = 86_000_000,
            Source = new ModSource("https://gamebanana.com/mods/600101", 600101, 1700001, "2.1", 1_790_000_000) },
        new("Lap Timer HUD", "Lap Timer HUD") { Version = "0.9.2", Author = "tenth", Description = "A lap timer with split times, drawn over the race.", Enabled = true, HasPlugin = true, Size = 1_200_000,
            Options = Mkwmp.Core.Targets.ModOptions.Parse(Encoding.UTF8.GetBytes(DemoOptions), null, out _) },
        new("Retro Sounds", "Retro Sounds") { Version = "1.0", Author = "bitcrush", Description = "Item and menu sounds in 16-bit style.", HasFiles = true, FileCount = 212, Size = 31_000_000,
            Problems = ["Shares 3 game files with Neon Karts: the one turned on last wins."] },
        new("Classic GP", "Classic GP") { Author = "mkw-recomp", HasFiles = true, FileCount = 6, ChangesCode = true, Size = 900_000,
            Locked = "This mod changes the game's code, and the game has to be built again for it. Manage it on the Mods page of mkw-recomp's own start screen." },
    ];
    private bool _showMature;

    public ModsView Mods
    {
        get
        {
            lock (_gate)
            {
                var running = Session.GameRunning;
                return new ModsView(true, true, _mods, !running, running ? "Close the game to change its mods" : null, _showMature,
                    new Dictionary<string, GbFile> { ["Neon Karts"] = new(1700002, "neon_karts_v2_2.zip", 88_000_000, "", true, "clean", "", "2.2", 1_795_000_000) });
            }
        }
    }

    public Task RefreshModsAsync() => Task.CompletedTask;

    public Task<string?> SetModEnabledAsync(string id, bool on, bool acceptCode)
    {
        string? why = null;
        Change(() =>
        {
            var mod = _mods.FirstOrDefault(m => m.Id == id);
            why = mod is null ? "No such mod" : Session.GameRunning ? "Close the game to change its mods" : mod.Locked;
            if (why is null)
            {
                _mods = [.. _mods.Select(m => m.Id == id ? m with { Enabled = on } : m)];
            }
        });
        return Task.FromResult(why);
    }

    public Task<string?> UninstallModAsync(string id)
    {
        Change(() => _mods = [.. _mods.Where(m => m.Id != id || m.Managed)]);
        return Task.FromResult<string?>(null);
    }

    public async Task<ModInstallResult> InstallModAsync(ModInstallRequest request, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (var i = 1; i <= 10; i++)
        {
            await Task.Delay(120, cancel).ConfigureAwait(false);
            progress?.Report(new ModProgress("unpack", i, 10));
        }
        var name = request.Name ?? Path.GetFileNameWithoutExtension(request.Path);
        Change(() => _mods = [.. _mods.Where(m => m.Id != name), new GameMod(name, name) { Enabled = true, HasFiles = true, FileCount = 12, Size = 9_000_000, Source = request.Source }]);
        return new ModInstallResult(ModInstallCode.Done, $"{name} is installed.", name, Enabled: true);
    }

    public async Task<string> DownloadModAsync(string link, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        await PretendDownload(progress, cancel).ConfigureAwait(false);
        return "demo-mod.zip";
    }

    public async Task<string> DownloadModAsync(GbDetails mod, GbFile file, IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        ArgumentNullException.ThrowIfNull(file);
        await PretendDownload(progress, cancel).ConfigureAwait(false);
        return file.Name;
    }

    private static async Task PretendDownload(IProgress<ModProgress>? progress, CancellationToken cancel)
    {
        for (var i = 1; i <= 12; i++)
        {
            await Task.Delay(120, cancel).ConfigureAwait(false);
            progress?.Report(new ModProgress("download", i * 4_000_000L, 48_000_000));
        }
    }

    public GameSettingsView ModOptions(string id)
    {
        lock (_gate)
        {
            var running = Session.GameRunning;
            return _mods.FirstOrDefault(m => m.Id == id)?.Options is { } schema
                ? new GameSettingsView(schema, schema.Effective(_modValues), new HashSet<string>(_modValues.Keys, StringComparer.OrdinalIgnoreCase), !running, null, false)
                : new GameSettingsView(null, new Dictionary<string, string>(), new HashSet<string>(), false, null, false);
        }
    }

    public Task SetModOptionAsync(string id, string key, string? value)
    {
        Change(() =>
        {
            if (value is null)
            {
                _modValues.Remove(key);
            }
            else
            {
                _modValues[key] = value;
            }
        });
        return Task.CompletedTask;
    }

    public Task<string?> SetShowMatureModsAsync(bool show)
    {
        Change(() => _showMature = show);
        return Task.FromResult<string?>(null);
    }

    public Task CheckModUpdatesAsync(CancellationToken cancel) => Task.CompletedTask;

    private static readonly (string Name, string Author, string Category, int Likes, bool Rated)[] DemoStore =
    [
        ("Neon Karts", "pixelfox", "Skins", 412, false), ("Lap Timer HUD", "tenth", "Game files", 388, false), ("Retro Sounds", "bitcrush", "Sounds", 251, false),
        ("Sunset Circuit", "mapleleaf", "Maps", 240, false), ("Chrome Wheels", "pixelfox", "Skins", 199, false), ("Big Minimap", "ordinal", "UI", 187, false),
        ("After Dark Billboards", "nightowl", "Skins", 91, true), ("Paper Racers", "origami", "Skins", 164, false), ("Jazz Menu Music", "bluenote", "Sounds", 143, false),
        ("Rainbow Trails", "prism", "Effects", 120, false), ("Clean HUD", "ordinal", "UI", 96, false), ("Winter Circuit", "mapleleaf", "Maps", 88, false),
    ];

    private static GbMod StoreMod(int index)
    {
        var (name, author, category, likes, rated) = DemoStore[index];
        return new GbMod(600101 + index, name, author, likes, likes * 37, category, $"https://gamebanana.com/mods/{600101 + index}", "", rated, 1_790_000_000, 1_795_000_000, index % 3 == 0 ? "2.1" : "1.0");
    }

    public Task<GbPage> BrowseModsAsync(GbSort sort, string query, int page, CancellationToken cancel)
    {
        var all = Enumerable.Range(0, DemoStore.Length).Select(StoreMod).Where(m => string.IsNullOrWhiteSpace(query) || m.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase));
        var mods = (sort switch { GbSort.Liked or GbSort.Popular => all.OrderByDescending(m => m.Likes), GbSort.Newest => all.Reverse(), _ => all.OrderBy(m => m.Name, StringComparer.Ordinal) }).ToList();
        return Task.FromResult(new GbPage(mods, page, string.IsNullOrWhiteSpace(query) ? 122 : mods.Count));
    }

    public Task<GbDetails> ModPageAsync(int id, CancellationToken cancel)
    {
        var mod = StoreMod(Math.Clamp(id - 600101, 0, DemoStore.Length - 1));
        var slug = mod.Name.ToLowerInvariant().Replace(' ', '_');
        return Task.FromResult(new GbDetails(
            mod,
            "A made-up mod of the launcher's demo.\nWorks in every mode, online too: only you see it.\nInstall it, turn it on, start the game.",
            "CC BY-NC 4.0",
            mod.Rated ? "Suggestive Themes" : "",
            [
                new GbFile(1700002, slug + "_v2_2.zip", 88_000_000, "", true, "clean", "Fixes for the newest game update", "2.2", 1_795_000_000),
                new GbFile(1700001, slug + "_v2_1.zip", 86_000_000, "", true, "clean", "", "2.1", 1_790_000_000),
                new GbFile(1700000, slug + "_setup.zip", 2_000_000, "", false, "flagged", "", "1.0", 1_780_000_000),
            ],
            [new GbImage("", ""), new GbImage("", ""), new GbImage("", "")]));
    }

    public Task<string?> ModImageAsync(string url, CancellationToken cancel) => Task.FromResult<string?>(null);
}