// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
namespace Mkwmp.App.ViewModels;

/// <summary>
/// The drawer: a page's own panel from the right, over the dimmed page (a mod's switch, options and
/// uninstall over the list of mods). It has its own hint bar, as the friends panel. A modal it asks
/// (uninstall?) comes above it; the file picker and the on-screen keyboard too.
/// </summary>
public sealed partial class ShellViewModel
{
    private LayerViewModel? _drawer;
    private LayerViewModel? _drawerShown;

    /// <summary>The drawer's content: the open one, and after it closed still the last (it slides out with it).</summary>
    public LayerViewModel? Drawer => _drawerShown;

    public bool IsDrawerOpen => _drawer is not null;

    /// <summary>Opens <paramref name="drawer"/> (in place of one that is open).</summary>
    public void OpenDrawer(LayerViewModel drawer)
    {
        ArgumentNullException.ThrowIfNull(drawer);
        SetDrawer(drawer);
    }

    /// <summary>Closes the drawer; with <paramref name="only"/>, only while that one is the open one.</summary>
    public void CloseDrawer(LayerViewModel? only = null)
    {
        if (only is null || ReferenceEquals(_drawer, only))
        {
            SetDrawer(null);
        }
    }

    private void SetDrawer(LayerViewModel? drawer)
    {
        var old = _drawer;
        if (ReferenceEquals(old, drawer))
        {
            return;
        }
        _drawer = drawer;
        if (drawer is not null)
        {
            _drawerShown = drawer;
            Raise(nameof(Drawer));
        }
        old?.OnClosed();
        drawer?.OnOpened();
        Raise(nameof(IsDrawerOpen));
        Raise(nameof(IsSidePanelOpen));
        RefreshHints();
    }
}
