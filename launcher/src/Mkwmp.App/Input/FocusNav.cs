// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Mkwmp.App.Input;

/// <summary>
/// Directional focus for a controller: from the focused control to the nearest one in a direction,
/// inside one root (the page, or the modal / sheet / friends panel above it). A focus stop is any
/// enabled, visible button, text box, slider or list item (or anything with
/// <c>FocusNav.IsStop="True"</c>). Attached properties:
/// <list type="bullet">
/// <item><c>IsDefault</c>: the stop that gets the focus when its page or layer opens.</item>
/// <item><c>IsGroup</c>: a container that remembers its last focused stop; coming back into it
/// from outside returns there (a list keeps its place; a category list its selected item).</item>
/// <item><c>Skip</c>: nothing inside is a stop.</item>
/// </list>
/// </summary>
public static class FocusNav
{
    public static readonly DependencyProperty IsStopProperty =
        DependencyProperty.RegisterAttached("IsStop", typeof(bool), typeof(FocusNav), new PropertyMetadata(false));

    public static readonly DependencyProperty IsDefaultProperty =
        DependencyProperty.RegisterAttached("IsDefault", typeof(bool), typeof(FocusNav), new PropertyMetadata(false));

    public static readonly DependencyProperty IsGroupProperty =
        DependencyProperty.RegisterAttached("IsGroup", typeof(bool), typeof(FocusNav), new PropertyMetadata(false));

    public static readonly DependencyProperty SkipProperty =
        DependencyProperty.RegisterAttached("Skip", typeof(bool), typeof(FocusNav), new PropertyMetadata(false));

    /// <summary>
    /// On a container: up and down never leave it (a column beside another column, like the two
    /// halves of Settings: from the top row of the pane, up does not jump into the category list).
    /// </summary>
    public static readonly DependencyProperty IsColumnProperty =
        DependencyProperty.RegisterAttached("IsColumn", typeof(bool), typeof(FocusNav), new PropertyMetadata(false));

    public static bool GetIsColumn(DependencyObject o) => (bool)(o ?? throw new ArgumentNullException(nameof(o))).GetValue(IsColumnProperty);
    public static void SetIsColumn(DependencyObject o, bool value) => (o ?? throw new ArgumentNullException(nameof(o))).SetValue(IsColumnProperty, value);

    private static readonly DependencyProperty LastProperty =
        DependencyProperty.RegisterAttached("Last", typeof(WeakReference<UIElement>), typeof(FocusNav), new PropertyMetadata(null));

    public static bool GetIsStop(DependencyObject o) => (bool)(o ?? throw new ArgumentNullException(nameof(o))).GetValue(IsStopProperty);
    public static void SetIsStop(DependencyObject o, bool value) => (o ?? throw new ArgumentNullException(nameof(o))).SetValue(IsStopProperty, value);
    public static bool GetIsDefault(DependencyObject o) => (bool)(o ?? throw new ArgumentNullException(nameof(o))).GetValue(IsDefaultProperty);
    public static void SetIsDefault(DependencyObject o, bool value) => (o ?? throw new ArgumentNullException(nameof(o))).SetValue(IsDefaultProperty, value);
    public static bool GetIsGroup(DependencyObject o) => (bool)(o ?? throw new ArgumentNullException(nameof(o))).GetValue(IsGroupProperty);
    public static void SetIsGroup(DependencyObject o, bool value) => (o ?? throw new ArgumentNullException(nameof(o))).SetValue(IsGroupProperty, value);
    public static bool GetSkip(DependencyObject o) => (bool)(o ?? throw new ArgumentNullException(nameof(o))).GetValue(SkipProperty);
    public static void SetSkip(DependencyObject o, bool value) => (o ?? throw new ArgumentNullException(nameof(o))).SetValue(SkipProperty, value);

    /// <summary>The focused stop of the window that holds <paramref name="any"/> (logical focus: also right when the window is not the active one).</summary>
    public static UIElement? Current(DependencyObject any)
    {
        var window = any as Window ?? (any is null ? null : Window.GetWindow(any));
        return window is null ? null : FocusManager.GetFocusedElement(window) as UIElement;
    }

    /// <summary>Gives the focus (logical, and the keyboard's when the window is active) and scrolls the stop into view.</summary>
    public static bool Focus(UIElement? element)
    {
        if (element is null || !element.Focusable || !element.IsEnabled || !element.IsVisible)
        {
            return false;
        }
        if (Window.GetWindow(element) is { } window)
        {
            FocusManager.SetFocusedElement(window, element);
        }
        Keyboard.Focus(element);
        Remember(element);
        (element as FrameworkElement)?.BringIntoView();
        return true;
    }

    /// <summary>Notes the stop in every group around it (the window calls this for any focus change, also the mouse's).</summary>
    public static void Remember(UIElement element)
    {
        var reference = new WeakReference<UIElement>(element);
        for (DependencyObject? at = Parent(element); at is not null; at = Parent(at))
        {
            if (GetIsGroup(at))
            {
                at.SetValue(LastProperty, reference);
            }
        }
    }

    /// <summary>Whether the stop can still hold the focus and is inside <paramref name="root"/>.</summary>
    public static bool IsAlive(UIElement? element, FrameworkElement root)
    {
        if (element is null || !element.IsVisible || !element.IsEnabled || !element.Focusable)
        {
            return false;
        }
        for (DependencyObject? at = element; at is not null; at = Parent(at))
        {
            if (ReferenceEquals(at, root))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Focuses the root's <c>IsDefault</c> stop, else its first stop (reading order).</summary>
    public static bool FocusDefault(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var stops = Stops(root).ToList();
        if (stops.Count == 0)
        {
            return false;
        }
        var chosen = stops.FirstOrDefault(GetIsDefault);
        if (chosen is null)
        {
            chosen = stops
                .Select(s => (Stop: s, Box: Bounds(s, root)))
                .OrderBy(s => Math.Round(s.Box.Top / 40.0)).ThenBy(s => s.Box.Left)
                .First().Stop;
        }
        return Focus(Preferred(chosen, null, root, null));
    }

    /// <summary>Moves the focus one step; false when there is nothing that way (the focus stays).</summary>
    public static bool Move(FrameworkElement root, NavDirection direction)
    {
        ArgumentNullException.ThrowIfNull(root);
        var current = Current(root);
        if (!IsAlive(current, root))
        {
            return FocusDefault(root);
        }
        var from = Bounds(current!, root);
        var stops = Stops(root).Where(s => !ReferenceEquals(s, current)).Select(s => (Stop: s, Box: Bounds(s, root)))
            .Where(s => !s.Box.IsEmpty && s.Box.Width > 0 && s.Box.Height > 0).ToList();
        if (direction is NavDirection.Up or NavDirection.Down)
        {
            // inside a column, up and down stay in it
            for (DependencyObject? at = Parent(current!); at is not null && !ReferenceEquals(at, root); at = Parent(at))
            {
                if (GetIsColumn(at))
                {
                    var column = at;
                    stops = [.. stops.Where(s => IsInside(s.Stop, column))];
                    break;
                }
            }
        }

        // first what is on the same line (sideways) or anything that way (up, down); then, sideways, anything that way
        // at all: from a category at the bottom of its list into a pane whose rows are all higher
        foreach (var strict in (bool[])[true, false])
        {
            UIElement? best = null;
            var bestScore = double.MaxValue;
            foreach (var (stop, to) in stops)
            {
                var score = Score(from, to, direction, strict);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = stop;
                }
            }
            if (best is not null)
            {
                return Focus(Preferred(best, current, root, direction));
            }
        }
        return false;
    }

    private static double Score(Rect from, Rect to, NavDirection direction, bool strict)
    {
        double primary, overlap, centers, slack;
        bool ahead;
        var horizontal = direction is NavDirection.Left or NavDirection.Right;
        if (horizontal)
        {
            slack = Math.Min(from.Width, to.Width) * 0.3;
            ahead = direction == NavDirection.Right
                ? to.Left >= from.Right - slack && Center(to).X > Center(from).X
                : to.Right <= from.Left + slack && Center(to).X < Center(from).X;
            primary = direction == NavDirection.Right ? to.Left - from.Right : from.Left - to.Right;
            overlap = Math.Min(from.Bottom, to.Bottom) - Math.Max(from.Top, to.Top);
            centers = Math.Abs(Center(to).Y - Center(from).Y);
        }
        else
        {
            slack = Math.Min(from.Height, to.Height) * 0.3;
            ahead = direction == NavDirection.Down
                ? to.Top >= from.Bottom - slack && Center(to).Y > Center(from).Y
                : to.Bottom <= from.Top + slack && Center(to).Y < Center(from).Y;
            primary = direction == NavDirection.Down ? to.Top - from.Bottom : from.Top - to.Bottom;
            overlap = Math.Min(from.Right, to.Right) - Math.Max(from.Left, to.Left);
            centers = Math.Abs(Center(to).X - Center(from).X);
        }
        if (!ahead)
        {
            return double.MaxValue;
        }
        primary = Math.Max(0, primary);
        if (overlap > 0)
        {
            return primary + centers * 0.05;
        }
        var gap = -overlap;
        // sideways, only what is roughly on the same line; up and down, anything (the nearest wins)
        if (strict && horizontal && gap > Math.Max(from.Height, to.Height) * 0.5)
        {
            return double.MaxValue;
        }
        return primary + gap * 3 + centers * 0.2 + 40;
    }

    /// <summary>When the step enters a group, the group's remembered (or selected) stop instead of the nearest.</summary>
    private static UIElement Preferred(UIElement target, UIElement? from, FrameworkElement root, NavDirection? direction)
    {
        DependencyObject? group = null;
        for (DependencyObject? at = Parent(target); at is not null && !ReferenceEquals(at, root); at = Parent(at))
        {
            if (GetIsGroup(at) && (from is null || !IsInside(from, at)))
            {
                group = at; // the outermost group being entered
            }
        }
        if (group is null)
        {
            return target;
        }
        if (group is Selector { SelectedItem: not null } selector
            && selector.ItemContainerGenerator.ContainerFromItem(selector.SelectedItem) is UIElement selected
            && IsAlive(selected, root))
        {
            return selected;
        }
        if (group.GetValue(LastProperty) is WeakReference<UIElement> reference
            && reference.TryGetTarget(out var last)
            && IsAlive(last, root) && IsInside(last, group))
        {
            return last;
        }
        if (direction is NavDirection.Left or NavDirection.Right && group is FrameworkElement container)
        {
            // a group entered sideways for the first time starts at its top (a settings pane at its first row)
            var first = Stops(container).Select(s => (Stop: s, Box: Bounds(s, root))).Where(s => !s.Box.IsEmpty)
                .OrderBy(s => Math.Round(s.Box.Top / 40.0)).ThenBy(s => s.Box.Left).Select(s => s.Stop).FirstOrDefault();
            return first ?? target;
        }
        return target;
    }

    private static bool IsInside(DependencyObject element, DependencyObject container)
    {
        for (DependencyObject? at = element; at is not null; at = Parent(at))
        {
            if (ReferenceEquals(at, container))
            {
                return true;
            }
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject o) =>
        o is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(o) ?? LogicalTreeHelper.GetParent(o)
            : LogicalTreeHelper.GetParent(o);

    private static Point Center(Rect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static Rect Bounds(UIElement element, FrameworkElement root)
    {
        try
        {
            return element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty; // left the tree between two frames
        }
    }

    /// <summary>Every focus stop below <paramref name="root"/>.</summary>
    public static IEnumerable<UIElement> Stops(DependencyObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is not UIElement element)
            {
                continue;
            }
            if (!element.IsVisible || !element.IsEnabled || GetSkip(element))
            {
                continue;
            }
            if (element.Focusable && (element is ButtonBase or TextBoxBase or PasswordBox or Slider or ListBoxItem || GetIsStop(element)))
            {
                yield return element;
                continue;
            }
            foreach (var inner in Stops(element))
            {
                yield return inner;
            }
        }
    }
}
