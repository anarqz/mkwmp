// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Mkwmp.App.Design;
using Mkwmp.App.Input;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App;

/// <summary>
/// The screenshot harness (see docs/DESIGN.md, "Checking your page"):
/// <c>mkwmp-launcher --demo --shot out.png [--page home|settings|setup|gallery|privacy|whatsnew|connect] [--category key]
/// [--modal update|available|ready|ready-game|confirm|message|progress|crash|crash-report|bug-report|gamework|gameupdate|install]
/// [--state crashed|crashed-solo|crashed-exit|downloading|installing|download-failed|setting-up|setup-failed|need-dump|setup-again|runtime-update|install-fails|dev-package|uptodate|update-ready|no-policy] [--friends] [--toast]
/// [--press right,down,a,…] [--size 1920x1080] [--theme dark] [--battery 93] [--input keyboard] [--locale pt-BR]
/// [--overlay [--overlay-frame png|none] [--overlay-no-menu] [--overlay-progress 0..1 | --overlay-return 0..1]]</c>
/// renders the stage at that size once everything has settled, saves the PNG and exits. The window
/// stays off-screen and never takes the focus. <c>--icon dir</c> writes the app icon instead.
/// </summary>
public partial class MainWindow
{
    partial void ConfigureHarness(string[] args, ref bool handled)
    {
        var shot = App.Option(args, "--shot");
        var icon = App.Option(args, "--icon");
        if (shot is null && icon is null)
        {
            return;
        }
        handled = true;

        var (width, height) = (1920, 1080);
        if (App.Option(args, "--size")?.Split('x') is [var w, var h]
            && int.TryParse(w, CultureInfo.InvariantCulture, out var parsedWidth) && int.TryParse(h, CultureInfo.InvariantCulture, out var parsedHeight))
        {
            (width, height) = (parsedWidth, parsedHeight);
        }

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft - width - 200;
        Top = SystemParameters.VirtualScreenTop;
        SizeToContent = SizeToContent.WidthAndHeight;
        Root.Width = width;
        Root.Height = height;
        // a canvas never clips: the stage keeps its full size also when the screen is smaller than the shot
        Content = null;
        Content = new System.Windows.Controls.Canvas { Width = 640, Height = 360, Children = { Root } };

        if (icon is not null)
        {
            Loaded += (_, _) =>
            {
                WriteIcon(icon);
                Application.Current.Shutdown();
            };
            return;
        }

        // A screenshot shows the state it was asked for: the launcher starts the game's update by itself only for --modal gameupdate.
        _vm.AutoGameUpdate = false;
        var keyboard = string.Equals(App.Option(args, "--input"), "keyboard", StringComparison.OrdinalIgnoreCase);
        _vm.InputKind = keyboard ? InputKind.Keyboard : InputKind.Gamepad;
        _vm.GamepadConnected = !keyboard;
        // --battery 93 | 40c (charging) | 100p (on the charger, full)
        if (App.Option(args, "--battery") is { Length: > 0 } batteryArg
            && int.TryParse(batteryArg.TrimEnd('c', 'p'), CultureInfo.InvariantCulture, out var battery))
        {
            _vm.OverrideBattery(battery, batteryArg[^1] switch
            {
                'c' => Platform.PowerSource.Charging,
                'p' => Platform.PowerSource.PluggedIn,
                _ => Platform.PowerSource.OnBattery,
            });
        }
        // --network wifi0..wifi3 | wired | offline
        if (App.Option(args, "--network") is { Length: > 0 } net)
        {
            _vm.OverrideNetwork(net.StartsWith("wifi", StringComparison.OrdinalIgnoreCase)
                ? new Platform.NetworkStatus(Platform.NetworkKind.Wifi, int.TryParse(net[4..], CultureInfo.InvariantCulture, out var bars) ? bars : 3)
                : new Platform.NetworkStatus(net.Equals("wired", StringComparison.OrdinalIgnoreCase) ? Platform.NetworkKind.Wired : Platform.NetworkKind.Offline, 0));
        }
        if (App.Option(args, "--theme") is { } theme)
        {
            _ = _vm.SetThemeAsync(theme);
        }
        var page = App.Option(args, "--page") ?? Routes.Home;
        if (page is Routes.Home or Routes.Setup)
        {
            _vm.Nav.ResetTo(page);
        }
        else if (page == "privacy")
        {
            // the privacy policy as the launcher renders it (the server's Markdown), over Settings › Online
            _vm.Nav.ResetTo(Routes.Home);
            _vm.Navigate(Routes.SettingsOnline);
            _vm.Navigate(new PrivacyViewModel(_vm));
        }
        else if (page == "whatsnew")
        {
            // Home's What's New tile: the changelog, with the demo's update as its banner (--state uptodate: none)
            _vm.Nav.ResetTo(Routes.Home);
            _vm.Navigate(new WhatsNewViewModel(_vm));
        }
        else if (page == "connect")
        {
            // Settings › Online's "Connect to a Server": the welcome flow's online pages over Settings
            _vm.Nav.ResetTo(Routes.Home);
            _vm.Navigate(Routes.SettingsOnline);
            _vm.Navigate(SetupViewModel.Connect(_vm));
        }
        else
        {
            _vm.Nav.ResetTo(Routes.Home);
            _vm.Navigate(page);
        }
        if (App.Option(args, "--category") is { } category && _vm.Page is SettingsViewModel settings
            && settings.Categories.FirstOrDefault(c => c.Key == category) is { } entry)
        {
            settings.Category = entry;
        }

        Loaded += async (_, _) =>
        {
            try
            {
                await ShootAsync(args, shot!, width, height).ConfigureAwait(true);
            }
            finally
            {
                Application.Current.Shutdown();
            }
        };
    }

    private async Task ShootAsync(string[] args, string path, int width, int height)
    {
        // --state blocked|offline|signedout|nofriends|uptodate|queued|seated: another demo situation; --with-tray: as on Windows
        (_vm.Launcher as DesignLauncher)?.Pretend(App.Option(args, "--state"));
        // --cover <png>: the game's cover on the Play card (a picture made on this PC, never one of the repository)
        if (App.Option(args, "--cover") is { Length: > 0 } cover)
        {
            (_vm.Launcher as DesignLauncher)?.PretendCover(Path.GetFullPath(cover));
        }
        // --step welcome|name|game|online|consent|server|register|pin|done[=text]: that page of the welcome flow
        // (--page setup), or of Settings › Online's connect pages (--page connect); "=text" types into it (server=mkw.example.net)
        if (App.Option(args, "--step") is { } step && _vm.Page is SetupViewModel flow)
        {
            await Task.Delay(120).ConfigureAwait(true); // the pretended state first
            flow.JumpTo(step);
            if (step.Contains('=', StringComparison.Ordinal))
            {
                await Task.Delay(900).ConfigureAwait(true); // a server asked answers
            }
        }
        _vm.TrayAvailable = args.Contains("--with-tray");
        // --overlay [--overlay-frame <png>|none] [--overlay-no-menu]: overlay mode, as after a request of the game
        ImageSource? overlayPicture = null;
        if (args.Contains("--overlay"))
        {
            var picture = App.Option(args, "--overlay-frame") switch
            {
                "none" => Overlay.OverlayArt.Placeholder("Mario Kart World 1.8.0", _vm.S["home.overlay.placeholder"]),
                { } png => Overlay.OverlayArt.Load(png) ?? Overlay.OverlayArt.SyntheticGame(),
                null => Overlay.OverlayArt.SyntheticGame(),
            };
            var bridge = _vm.Launcher.OverlayBridge;
            bridge.Requested += (_, _) => { };
            bridge.Enabled = true;
            _ = bridge.RequestFromLauncher(0, Overlay.OverlayArt.ToFrame(picture), gameHasMenu: !args.Contains("--overlay-no-menu"));
            _vm.EnterOverlay(picture, again: false);
            overlayPicture = picture;
        }
        await Task.Delay(350).ConfigureAwait(true);
        SyncFocus();
        foreach (var name in (App.Option(args, "--press") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<PadButton>(name, ignoreCase: true, out var button))
            {
                OnButton(button);
                await Task.Delay(260).ConfigureAwait(true);
                SyncFocus();
            }
        }
        if (args.Contains("--friends"))
        {
            _vm.IsFriendsOpen = true;
        }
        switch (App.Option(args, "--modal"))
        {
            case "update":
                (_vm.Launcher as DesignLauncher)?.FreezeDownloadAt(0.4);
                _vm.ShowUpdateModal();
                break;
            case "available":
                _vm.ShowUpdateModal();
                break;
            // downloaded and verified: "Install and Restart"; and the same with a game running
            case "ready":
            case "ready-game":
                (_vm.Launcher as DesignLauncher)?.ReadyToInstall(gameRuns: App.Option(args, "--modal") == "ready-game");
                _vm.ShowUpdateModal();
                break;
            case "confirm":
                _ = _vm.ConfirmAsync(_vm.S["quit.title"], _vm.S["quit.body"], _vm.S["quit.yes"]);
                break;
            // "Quit Game" over a game (with --overlay): the question, the wait, the game that does not end, and
            // "Quit MKWMP" while a game runs
            case "quitgame":
                _ = _vm.QuitGameAsync();
                break;
            case "quitgame-closing":
                _ = _vm.ShowProgress(_vm.S["quitgame.closing"], _vm.S["quitgame.closing_body"]);
                break;
            case "quitgame-stuck":
                _ = _vm.ConfirmAsync(_vm.S["quitgame.stuck.title"], _vm.S["quitgame.stuck.body"], _vm.S["quitgame.force"], _vm.S["quitgame.wait"]);
                break;
            case "power":
                (_vm.Page as HomeViewModel)?.PowerCommand.Execute(null);
                break;
            case "quit-which":
                (_vm.Page as HomeViewModel)?.PowerCommand.Execute(null);
                await Task.Delay(300).ConfigureAwait(true);
                _vm.Modal?.Actions.LastOrDefault()?.Command.Execute(null); // "Quit MKWMP", the last of the menu
                break;
            // Settings > Appearance > Language, open (also from the welcome's first page)
            case "language":
                _ = _vm.PickLanguageAsync();
                break;
            case "message":
                _ = _vm.MessageAsync("The game is set up", "Mario Kart World 1.8.0 is ready on this device.");
                break;
            // the crash screen (with --state crashed | crashed-solo | crashed-exit it is already up: the launcher offers it
            // by itself) and the card after Report / "Found a bug?"; nothing is opened or copied from a screenshot
            case "crash":
                (_vm.Launcher as DesignLauncher)?.PretendCrash("crashed");
                break;
            case "crash-report":
                (_vm.Launcher as DesignLauncher)?.PretendCrash("crashed");
                await Task.Delay(200).ConfigureAwait(true);
                await _vm.ReportAsync(_vm.Launcher.PendingCrash?.Id, open: false).ConfigureAwait(true);
                break;
            case "bug-report":
                await _vm.ReportAsync(null, open: false).ConfigureAwait(true);
                break;
            // "Game setup" (with --state downloading | installing | download-failed | setting-up | setup-failed | need-dump |
            // setup-again | runtime-update): the card the Game tile, PLAY and Settings › Game open; gameupdate: the flow the
            // launcher starts by itself after an update of its runtime; install: Settings › Game's INSTALL, to its end
            case "gamework":
                _vm.ShowGameWork();
                break;
            case "gameupdate":
                _vm.ShowGameWork(driving: true);
                break;
            case "install":
                if (_vm.Page is SettingsViewModel inGame)
                {
                    inGame.Game.InstallCommand.Execute(null);
                    await Task.Delay(2200).ConfigureAwait(true);
                }
                break;
            case "progress":
                _vm.ShowProgress("Installing…", "Copying the online play files into the game folder.").Report(0.65, "3 files left");
                break;
        }
        // --picker copy|copy-folder|game [--fake-wine] [--picker-at <path>]: the launcher's own file picker (MainWindow.Harness.Picker.cs)
        await OpenHarnessPickerAsync(args).ConfigureAwait(true);
        if (args.Contains("--toast"))
        {
            _vm.Launcher.ShowTestToast();
        }
        // --switch ja: the language changes now, with the page already up (what the selector does, without saving)
        if (App.Option(args, "--switch") is { } other)
        {
            _vm.ForcedLocale = other;
            _vm.ApplyLanguage();
            await Task.Delay(400).ConfigureAwait(true);
        }
        await Task.Delay(300).ConfigureAwait(true);
        SyncFocus();
        // --then a,down,…: pressed after the panel / modal is open (into the on-screen keyboard, a menu of the panel)
        foreach (var name in (App.Option(args, "--then") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<PadButton>(name, ignoreCase: true, out var button))
            {
                OnButton(button);
                await Task.Delay(320).ConfigureAwait(true);
                SyncFocus();
            }
        }
        await Task.Delay(1300).ConfigureAwait(true);

        // --overlay-progress 0..1: that moment of the open animation; --overlay-return 0..1: of the way back
        if (overlayPicture is not null && (App.Option(args, "--overlay-return") ?? App.Option(args, "--overlay-progress")) is { } moment
            && double.TryParse(moment, NumberStyles.Float, CultureInfo.InvariantCulture, out var at))
        {
            FreezeOverlay(overlayPicture, at, returning: App.Option(args, "--overlay-return") is not null);
        }

        Root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Root);
        Save(bitmap, Path.GetFullPath(path));
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    // ── the app icon: MKWMP's own mark (a chequered flag on the focus gradient), drawn here ──

    private static void WriteIcon(string directory)
    {
        Directory.CreateDirectory(directory);
        Save(RenderMark(256), Path.Combine(directory, "mkwmp-256.png"));
        int[] sizes = [256, 64, 48, 32, 24, 16];
        var images = sizes.Select(size =>
        {
            using var memory = new MemoryStream();
            if (size == 256)
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(RenderMark(size)));
                encoder.Save(memory);
                return memory.ToArray();
            }
            // the small sizes as classic 32-bit bitmaps (bottom-up BGRA, then an empty AND mask): every reader knows them
            var straight = new FormatConvertedBitmap(RenderMark(size), PixelFormats.Bgra32, null, 0);
            var pixels = new byte[size * size * 4];
            straight.CopyPixels(pixels, size * 4, 0);
            using var writer = new BinaryWriter(memory);
            writer.Write(40);
            writer.Write(size);
            writer.Write(size * 2);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0);
            writer.Write(pixels.Length);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            for (var row = size - 1; row >= 0; row--)
            {
                writer.Write(pixels, row * size * 4, size * 4);
            }
            writer.Write(new byte[(size + 31) / 32 * 4 * size]);
            writer.Flush();
            return memory.ToArray();
        }).ToList();

        using var file = File.Create(Path.Combine(directory, "mkwmp.ico"));
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images)
        {
            writer.Write(image);
        }
    }

    private static RenderTargetBitmap RenderMark(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            var back = new LinearGradientBrush(Color.FromRgb(0x2F, 0x74, 0xFF), Color.FromRgb(0x8A, 0x55, 0xFF), new Point(0, 0), new Point(1, 1));
            var tile = new RectangleGeometry(new Rect(0, 0, 256, 256), 58, 58);
            dc.PushClip(tile);
            dc.DrawRectangle(back, null, new Rect(0, 0, 256, 256));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), null, new Point(214, 30), 120, 120);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), null, new Point(20, 250), 96, 96);
            dc.Pop();

            // the pole
            var white = Brushes.White;
            dc.DrawRoundedRectangle(white, null, new Rect(62, 50, 18, 160), 9, 9);

            // the flag: a waving 4 x 3 chequer
            var flag = Geometry.Parse("M80,60 C108,46 136,78 164,64 C178,57 190,56 204,60 L204,150 C190,146 178,147 164,154 C136,168 108,136 80,150 Z");
            dc.DrawGeometry(white, null, flag);
            dc.PushClip(flag);
            var dark = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x8F));
            const double CellWidth = 31, CellHeight = 30;
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    if ((row + column) % 2 == 1)
                    {
                        // each column follows the wave a little
                        var lift = column switch { 0 => -6, 1 => 4, 2 => 6, _ => -2 };
                        dc.DrawRectangle(dark, null, new Rect(80 + column * CellWidth, 48 + lift + row * CellHeight, CellWidth, CellHeight));
                    }
                }
            }
            dc.Pop();
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }
}
