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
