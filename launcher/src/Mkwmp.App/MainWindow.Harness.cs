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
/// <c>mkwmp --demo --shot out.png [--page home|settings|setup|gallery|privacy|whatsnew|connect|start] [--category key]
/// [--modal update|available|ready|ready-game|confirm|message|progress|crash|crash-report|bug-report|gamework|gameupdate|install]
/// [--state crashed|crashed-solo|crashed-exit|downloading|installing|download-failed|setting-up|setup-failed|need-dump|setup-again|runtime-update|install-fails|dev-package|uptodate|update-ready|no-policy] [--friends] [--toast]
/// [--press right,down,a,east,…] [--pad-layout nintendo] [--size 1920x1080] [--theme dark] [--contrast high] [--battery 93] [--input keyboard] [--locale pt-BR]
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
        // --contrast low|normal|high|higher: Settings › Appearance › Contrast
        if (App.Option(args, "--contrast") is { } contrast)
        {
            _ = _vm.SetContrastAsync(contrast);
        }
        // --pad-layout nintendo|xbox: the game's button layout, which the window's buttons follow (--press east: the right face button)
        if (App.Option(args, "--pad-layout") is { } layout)
        {
            _ = _vm.Launcher.SetGameSettingAsync(PadLayout.SettingId, layout);
            _gamepad.Layout = PadLayout.FromSetting(layout);
        }
        var page = App.Option(args, "--page") ?? Routes.Home;
        if (page == "start")
        {
            // the page a real start opens on (welcome or home), as decided from client.toml: e.g. after an old launcher updated into this one
            page = StartRoute(_vm);
        }
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
            if (HarnessButton(name) is { } button)
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
            case "gamecopy-mode":
                // "Use another copy of the game", after the pick: how the game takes it (ViewModels/Parts/GameCopy.cs)
                _ = Choice.AskAsync(_vm, _vm.S["gamecopy.mode_title"], _vm.S.Format("gamecopy.mode_body", ("path", @"D:\Games\Mario Kart World\dump")),
                [
                    new ChoiceOption("link", _vm.S["gamecopy.link"], _vm.S["gamecopy.link_caption"]),
                    new ChoiceOption("copy", _vm.S["gamecopy.copy"], _vm.S["gamecopy.copy_caption"]),
                ], "link");
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
            if (HarnessButton(name) is { } button)
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

    // ── the app icon: the art's own (Assets/mkwmp.ico and mkwmp-256.png, made by launcher/tools/make_art.py), copied out ──

    private static void WriteIcon(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var name in new[] { "mkwmp.ico", "mkwmp-256.png" })
        {
            using var source = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/" + name))!.Stream;
            using var file = File.Create(Path.Combine(directory, name));
            source.CopyTo(file);
        }
    }
}