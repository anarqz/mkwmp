// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mkwmp.App.Controls;
using Mkwmp.App.Input;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Views;

public partial class HomePage : UserControl
{
    /// <summary>The title never leaves the page's margins, also above the first and the last tile.</summary>
    private const double Edge = 72;

    private Tile? _focused;
    private bool _titleShown;

    public HomePage()
    {
        InitializeComponent();
        SizeChanged += (_, _) => PlaceTitle();
        TitleHost.SizeChanged += (_, _) => PlaceTitle();
    }

    private void OnTileFocus(object sender, RoutedEventArgs e)
    {
        if (sender is Tile tile && DataContext is HomeViewModel home)
        {
            _focused = tile;
            home.SelectedTile = tile.DataContext as HomeTile;
            TitleHost.UpdateLayout(); // the new title's width, before it is centred
            PlaceTitle();
            ShowTitle(true);
        }
    }

    private void OnTileBlur(object sender, RoutedEventArgs e) =>
        // after the focus has landed: on another tile the title stays, anywhere else (dock, a modal) it goes
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => ShowTitle(FocusNav.Current(this) is Tile tile && tile.IsDescendantOf(Carousel)));

    private void OnCarouselScrolled(object sender, ScrollChangedEventArgs e) => PlaceTitle();

    /// <summary>Centres the title above the focused tile (it follows the carousel's glide).</summary>
    private void PlaceTitle()
    {
        if (_focused is not { IsVisible: true } tile || !tile.IsDescendantOf(this) || ActualWidth <= 0)
        {
            return;
        }
        var centre = tile.TransformToAncestor(this).Transform(new Point(tile.ActualWidth / 2, 0)).X;
        var width = TitleHost.ActualWidth;
        TitleShift.X = Math.Round(Math.Clamp(centre - width / 2, Edge, Math.Max(Edge, ActualWidth - Edge - width)));
    }

    // ── the overlay animation (MainWindow.Overlay.cs drives it; launcher/docs/OVERLAY.md) ──

    /// <summary>The slots of <see cref="SetOverlayMotion"/> after the tiles.</summary>
    public const int StatusSlot = 100;
    public const int DockSlot = 101;

    private IEnumerable<Tile> TileViews()
    {
        for (var i = 0; i < TilesHost.Items.Count; i++)
        {
            if (TilesHost.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement container
                && System.Windows.Media.VisualTreeHelper.GetChildrenCount(container) > 0
                && System.Windows.Media.VisualTreeHelper.GetChild(container, 0) is Tile tile)
            {
                yield return tile;
            }
        }
    }

    private Tile? PlayTile => TileViews().FirstOrDefault(t => t.DataContext is HomeTile { Key: "play" });

    /// <summary>The focus on the Play tile ("Back to the game" in overlay mode); false while the page has no layout yet.</summary>
    public bool FocusPlayTile() => PlayTile is { IsVisible: true } tile && FocusNav.Focus(tile);

    /// <summary>
    /// Where the Play tile's card is drawn now, in <paramref name="relativeTo"/>'s units (an
    /// ancestor), with the focus pop and the carousel's glide in it: the rectangle the game's
    /// picture shrinks into. Null while it is not on the screen.
    /// </summary>
    public Rect? PlayCardBounds(System.Windows.Media.Visual relativeTo)
    {
        if (PlayTile is not { IsVisible: true } tile || !tile.IsDescendantOf(relativeTo))
        {
            return null;
        }
        var card = tile.Template?.FindName("Card", tile) as FrameworkElement ?? tile;
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0)
        {
            return null;
        }
        return card.TransformToAncestor(relativeTo).TransformBounds(new Rect(card.RenderSize));
    }

    /// <summary>
    /// One frame of the overlay animation: how much of each part of home is there, 0 (not yet) to
    /// 1 (in its place). <paramref name="play"/> is the Play tile with its focus ring and title
    /// (the game's picture is drawn over it meanwhile); <paramref name="part"/> answers for the
    /// other tiles by their index, <see cref="StatusSlot"/> and <see cref="DockSlot"/>. Only
    /// opacity and render transforms change: no layout.
    /// </summary>
    public void SetOverlayMotion(double play, Func<int, double> part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var index = 0;
        foreach (var tile in TileViews())
        {
            if (tile.DataContext is HomeTile { Key: "play" })
            {
                tile.Opacity = play;
            }
            else
            {
                Place(tile, part(index), 46, 0);
            }
            index++;
        }
        TitleFade.Opacity = play;
        Place(StatusLine, part(StatusSlot), 0, 22);
        Place(DockHost, part(DockSlot), 0, 54);
    }

    /// <summary>Home as it always is: every part in its place.</summary>
    public void ClearOverlayMotion()
    {
        foreach (var tile in TileViews())
        {
            Clear(tile);
        }
        TitleFade.ClearValue(OpacityProperty);
        Clear(StatusLine);
        Clear(DockHost);
    }

    private static void Place(FrameworkElement element, double presence, double dx, double dy)
    {
        if (element.RenderTransform is not System.Windows.Media.TranslateTransform { IsFrozen: false } shift)
        {
            element.RenderTransform = shift = new System.Windows.Media.TranslateTransform();
        }
        element.Opacity = presence;
        shift.X = (1 - presence) * dx;
        shift.Y = (1 - presence) * dy;
    }

    private static void Clear(FrameworkElement element)
    {
        element.ClearValue(OpacityProperty);
        if (element.RenderTransform is System.Windows.Media.TranslateTransform { IsFrozen: false } shift)
        {
            shift.X = 0;
            shift.Y = 0;
        }
    }

    private void ShowTitle(bool show)
    {
        if (_titleShown == show)
        {
            return;
        }
        _titleShown = show;
        TitleHost.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 140 : 90)));
    }
}
