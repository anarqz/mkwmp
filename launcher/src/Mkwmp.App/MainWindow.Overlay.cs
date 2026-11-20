// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Mkwmp.App.Overlay;
using Mkwmp.App.Views;

namespace Mkwmp.App;

/// <summary>
/// The overlay's animation (launcher/docs/OVERLAY.md): the picture the game was showing fills the
/// stage, then shrinks into home's Play tile while home comes in around it; "back to the game" is
/// the reverse. One number drives everything (<see cref="_fxAt"/>, 0..1), so any moment of it can
/// be drawn alone (the harness's <c>--overlay-progress</c>). Per frame only a clip rectangle, two
/// transforms and some opacities change: no layout pass.
/// </summary>
public partial class MainWindow
{
    /// <summary>The open: 380 ms of travel and 100 ms to settle.</summary>
    private const double OpenMilliseconds = 480;
    private const double ReturnMilliseconds = 350;

    /// <summary>The part of the open spent travelling; the rest settles from 98 % to the tile's size.</summary>
    private const double Travel = 0.8;
    private const double TileRadius = 20;

    private enum FxMode
    {
        Idle,
        Open,
        Return,
    }

    private FxMode _fxMode;
    private double _fxAt;
    private EventHandler? _fxTick;
    private DispatcherTimer? _fxWatchdog;
    private TranslateTransform? _topShift;
    private TranslateTransform? _hintShift;
    private (WindowState State, Rect Bounds)? _beforeOverlay;

    /// <summary>The overlay's wiring for this window (App sets it; null in the screenshot harness).</summary>
    internal OverlayController? OverlayControl { get; set; }

    /// <summary>The picture is on its way: buttons wait.</summary>
    public bool IsOverlayAnimating => _fxMode != FxMode.Idle;

    private HomePage? Home => Pages.Child as HomePage;

    // ── the window over the game ──

    /// <summary>
    /// Borderless and exactly over the game's picture (<paramref name="game"/>, screen pixels), or
    /// over the whole screen when that is not known: what shrinks is then where the game was.
    /// Call before the window is shown, and again after (a first show places the window itself).
    /// </summary>
    internal void CoverForOverlay(Int32Rect? game)
    {
        if (_harness)
        {
            return;
        }
        _beforeOverlay ??= (WindowState, WindowState == WindowState.Normal && IsLoaded ? new Rect(Left, Top, Width, Height) : RestoreBounds);
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (game is { } pixels)
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            GameWindow.Place(this, pixels);
        }
        else if (WindowStyle != WindowStyle.None || WindowState != WindowState.Maximized)
        {
            WindowState = WindowState.Normal; // so that maximizing covers the task bar
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>The window as the player had it (full screen or a window) once overlay mode is over.</summary>
    internal void RestoreAfterOverlay()
    {
        if (_beforeOverlay is not { } before)
        {
            return;
        }
        _beforeOverlay = null;
        ApplyFullscreen();
        if (WindowState == WindowState.Normal && before.Bounds is { IsEmpty: false, Width: > 0, Height: > 0 } bounds)
        {
            (Left, Top, Width, Height) = (bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        }
    }

    // ── the animation ──

    /// <summary>
    /// The first thing drawn when the window comes up over the game: <paramref name="picture"/>
    /// over the whole stage. Call before showing the window, then <see cref="BeginOverlayOpen"/>.
    /// </summary>
    public void PrepareOverlayOpen(ImageSource picture)
    {
        ArgumentNullException.ThrowIfNull(picture);
        StopFx();
        SetFxPicture(picture);
        (_fxMode, _fxAt) = (FxMode.Open, 0);
        OverlayFx.Visibility = Visibility.Visible;
        ApplyFx();
    }

    /// <summary>Shrinks the picture into the Play tile (after the layout the window's show caused).</summary>
    public void BeginOverlayOpen(Action? done = null) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_fxMode != FxMode.Open)
            {
                return;
            }
            Stage.UpdateLayout();
            if (ReferenceEquals(ActiveRoot, BaseLayer))
            {
                Home?.FocusPlayTile();
            }
            RunFx(FxMode.Open, OpenMilliseconds, () =>
            {
                ResetOverlayFx();
                done?.Invoke();
            });
        });

    /// <summary>
    /// Grows <paramref name="picture"/> from the Play tile back over the whole stage, then calls
    /// <paramref name="done"/> (which hides the window; the last frame stays until
    /// <see cref="ResetOverlayFx"/>). Always completes, also when nothing is drawn.
    /// </summary>
    public void BeginOverlayReturn(ImageSource picture, Action done)
    {
        ArgumentNullException.ThrowIfNull(picture);
        ArgumentNullException.ThrowIfNull(done);
        StopFx();
        SetFxPicture(picture);
        (_fxMode, _fxAt) = (FxMode.Return, 0);
        OverlayFx.Visibility = Visibility.Visible;
        ApplyFx();
        RunFx(FxMode.Return, ReturnMilliseconds, done);
    }

    /// <summary>The stage without the animation: every part in its place.</summary>
    public void ResetOverlayFx()
    {
        StopFx();
        _fxMode = FxMode.Idle;
        OverlayFx.Visibility = Visibility.Collapsed;
        FxImage.Source = null;
        FxCard.ClearValue(OpacityProperty);
        Home?.ClearOverlayMotion();
        Chrome(TopChrome, ref _topShift, 1, 0);
        Chrome(Hints, ref _hintShift, 1, 0);
    }

    /// <summary>One moment of the open (0: the picture over everything, 1: home) or of the return, for the harness.</summary>
    internal void FreezeOverlay(ImageSource picture, double at, bool returning)
    {
        StopFx();
        SetFxPicture(picture);
        Stage.UpdateLayout();
        Home?.FocusPlayTile();
        Stage.UpdateLayout();
        (_fxMode, _fxAt) = (returning ? FxMode.Return : FxMode.Open, Math.Clamp(at, 0, 1));
        if (!returning && _fxAt >= 1)
        {
            ResetOverlayFx();
            return;
        }
        OverlayFx.Visibility = Visibility.Visible;
        ApplyFx();
    }

    private void SetFxPicture(ImageSource picture)
    {
        FxImage.Source = picture;
        var aspect = picture.Width > 0 && picture.Height > 0 ? picture.Width / picture.Height : 16.0 / 9;
        FxImage.Height = FxImage.Width / aspect;
    }

    private void RunFx(FxMode mode, double milliseconds, Action done)
    {
        StopFx();
        _fxMode = mode;
        long start = 0;
        var finished = false;
        void Finish()
        {
            if (finished)
            {
                return;
            }
            finished = true;
            StopFx();
            _fxAt = 1;
            ApplyFx();
            done();
        }
        _fxTick = (_, _) =>
        {
            // the clock starts with the first frame that is drawn, not with the call: a slow first layout costs no motion
            var now = Stopwatch.GetTimestamp();
            if (start == 0)
            {
                start = now;
            }
            var at = Stopwatch.GetElapsedTime(start, now).TotalMilliseconds / milliseconds;
            if (at >= 1)
            {
                Finish();
                return;
            }
            _fxAt = at;
            ApplyFx();
        };
        CompositionTarget.Rendering += _fxTick;
        // nothing is rendered for a window that is not on the screen: the game is given back all the same
        _fxWatchdog = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(milliseconds + 700) };
        _fxWatchdog.Tick += (_, _) => Finish();
        _fxWatchdog.Start();
    }

    private void StopFx()
    {
        if (_fxTick is { } tick)
        {
            CompositionTarget.Rendering -= tick;
            _fxTick = null;
        }
        _fxWatchdog?.Stop();
        _fxWatchdog = null;
    }

    private static double Clamp01(double x) => Math.Clamp(x, 0, 1);

    private static double OutCubic(double x) => 1 - Math.Pow(1 - Clamp01(x), 3);

    private static double InCubic(double x) => Math.Pow(Clamp01(x), 3);

    private static double InOutCubic(double x)
    {
        x = Clamp01(x);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    private static double Smooth(double x)
    {
        x = Clamp01(x);
        return x * x * (3 - 2 * x);
    }

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    private static Rect Lerp(Rect from, Rect to, double t) =>
        new(Lerp(from.X, to.X, t), Lerp(from.Y, to.Y, t), Lerp(from.Width, to.Width, t), Lerp(from.Height, to.Height, t));

    private static Rect Scaled(Rect rect, double scale) =>
        new(rect.X + rect.Width * (1 - scale) / 2, rect.Y + rect.Height * (1 - scale) / 2, rect.Width * scale, rect.Height * scale);

    /// <summary>Draws the moment <see cref="_fxAt"/> of the running mode.</summary>
    private void ApplyFx()
    {
        double width = Stage.ActualWidth, height = Stage.ActualHeight;
        if (_fxMode == FxMode.Idle || width <= 0 || height <= 0)
        {
            return;
        }
        var full = new Rect(0, 0, width, height);
        var home = Home;
        // the tile the picture belongs to; with something over home (or another page) a card in the middle of the stage
        var card = ReferenceEquals(ActiveRoot, BaseLayer) ? home?.PlayCardBounds(Stage) : null;
        var target = card ?? Scaled(full, 0.34);
        var corner = card is null ? TileRadius : TileRadius * target.Height / 372;
        var at = _fxAt;
        Rect rect;
        double radius, picture, play;
        Func<int, double> part;
        double top, hints;
        if (_fxMode == FxMode.Open)
        {
            // travel with a long tail (ease-out), arriving a touch small, then settle to the tile's size
            var move = OutCubic(at / Travel);
            var settle = 0.98 + 0.02 * OutCubic((at - Travel) / (1 - Travel));
            rect = Lerp(full, Scaled(target, settle), move);
            radius = corner * move;
            // the tile comes up under the picture (its focus ring around it), then the picture fades off it
            play = card is null ? 1 : Smooth((at - 0.66) / 0.14);
            picture = 1 - Smooth((at - 0.82) / 0.18);
            top = OutCubic((at - 0.08) / 0.46);
            hints = OutCubic((at - 0.32) / 0.48);
            part = slot => slot switch
            {
                HomePage.StatusSlot => OutCubic((at - 0.30) / 0.46),
                HomePage.DockSlot => OutCubic((at - 0.20) / 0.50),
                _ => OutCubic((at - 0.14 - 0.06 * slot) / 0.44), // the tiles, one after the other
            };
        }
        else
        {
            var move = InOutCubic(at);
            rect = Lerp(target, full, move);
            radius = corner * (1 - move);
            picture = Smooth(at / 0.12);
            play = card is null ? 1 : 1 - Smooth((at - 0.10) / 0.06);
            var rest = 1 - InCubic(at / 0.6);
            (top, hints) = (rest, rest);
            part = _ => rest;
        }

        FxClip.Rect = rect;
        FxClip.RadiusX = radius;
        FxClip.RadiusY = radius;
        // the picture covers the rectangle (as the tile's does), centred
        var scale = Math.Max(rect.Width / FxImage.Width, rect.Height / FxImage.Height);
        FxScale.ScaleX = scale;
        FxScale.ScaleY = scale;
        FxShift.X = rect.X + (rect.Width - FxImage.Width * scale) / 2;
        FxShift.Y = rect.Y + (rect.Height - FxImage.Height * scale) / 2;
        FxCard.Opacity = picture;

        home?.SetOverlayMotion(play, part);
        Chrome(TopChrome, ref _topShift, top, -44);
        Chrome(Hints, ref _hintShift, hints, 34);
    }

    private static void Chrome(FrameworkElement element, ref TranslateTransform? shift, double presence, double dy)
    {
        if (shift is null)
        {
            if (presence >= 1)
            {
                return;
            }
            element.RenderTransform = shift = new TranslateTransform();
        }
        shift.Y = (1 - presence) * dy;
        if (presence >= 1)
        {
            element.ClearValue(OpacityProperty);
        }
        else
        {
            element.Opacity = presence;
        }
    }
}
