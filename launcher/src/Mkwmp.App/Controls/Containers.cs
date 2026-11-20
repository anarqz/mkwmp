// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Mkwmp.App.Input;

namespace Mkwmp.App.Controls;

/// <summary>
/// A slider as a settings row: label, track, value. With the focus on it, left / right (D-pad,
/// stick, arrows) change the value by <c>SmallChange</c>; the mouse and touch drag the knob.
/// </summary>
public sealed class ConsoleSlider : Slider
{
    public static readonly DependencyProperty LabelProperty = Dp.Register<ConsoleSlider, string>(nameof(Label), "");
    public static readonly DependencyProperty UnitProperty = Dp.Register<ConsoleSlider, string>(nameof(Unit), "", (d, _) => ((ConsoleSlider)d).UpdateText());
    public static readonly DependencyProperty ValueTextProperty = Dp.Register<ConsoleSlider, string>(nameof(ValueText), "");

    public ConsoleSlider() => UpdateText();

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Put after the number ("%").</summary>
    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string ValueText
    {
        get => (string)GetValue(ValueTextProperty);
        private set => SetValue(ValueTextProperty, value);
    }

    /// <summary>One step down (-1) or up (+1); false at the end of the range.</summary>
    public bool Nudge(int direction)
    {
        var next = Math.Clamp(Value + direction * SmallChange, Minimum, Maximum);
        if (Math.Abs(next - Value) < double.Epsilon)
        {
            return false;
        }
        SetCurrentValue(ValueProperty, next);
        return true;
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateText();
    }

    /// <summary>The value's text as the page wants it ("1.25x"), instead of the rounded number and <see cref="Unit"/>. Null or "": the default.</summary>
    public string? DisplayText
    {
        get => (string?)GetValue(DisplayTextProperty);
        set => SetValue(DisplayTextProperty, value);
    }

    public static readonly DependencyProperty DisplayTextProperty = Dp.Register<ConsoleSlider, string?>(nameof(DisplayText), null, (d, _) => ((ConsoleSlider)d).UpdateText());

    private void UpdateText() => ValueText = string.IsNullOrEmpty(DisplayText) ? Math.Round(Value).ToString(CultureInfo.CurrentCulture) + Unit : DisplayText;
}

/// <summary>One entry of a <see cref="CategoryList"/>.</summary>
/// <param name="Key">What the page switches on.</param>
/// <param name="SeparatorBefore">A thin rule above this entry (a new group).</param>
/// <param name="Heading">A small group heading above this entry ("Game settings"); "" for none.</param>
/// <param name="Icon">An icon before the title, by its name in Icons.xaml without "Icon." ("Volume"); "" for none.</param>
public sealed record CategoryEntry(string Key, string Title, bool SeparatorBefore = false, string Heading = "", string Icon = "")
{
    /// <summary>The resource key of <see cref="Icon"/> ("Icon.Volume"), or "".</summary>
    public string IconKey => Icon.Length == 0 ? "" : "Icon." + Icon;
}

/// <summary>
/// The left navigation of a settings page: <c>ItemsSource</c> of <see cref="CategoryEntry"/>,
/// <c>SelectedItem</c> two-way. The entry under the focus becomes the selected one (the pane on
/// the right follows the focus); coming back from the pane returns to the selected entry.
/// </summary>
public sealed class CategoryList : ListBox
{
    public CategoryList()
    {
        Focusable = false;
        FocusNav.SetIsGroup(this, true);
        AddHandler(GotFocusEvent, new RoutedEventHandler(OnItemFocus));
    }

    private void OnItemFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ListBoxItem item && ItemContainerGenerator.ItemFromContainer(item) is { } entry
            && entry != DependencyProperty.UnsetValue && !Equals(SelectedItem, entry))
        {
            SetCurrentValue(SelectedItemProperty, entry);
        }
    }
}

/// <summary>
/// The launcher's scroll viewer: keeps the focused control in view with a short glide instead of a
/// jump (<see cref="EdgePadding"/> of room around it; <see cref="Centered"/> keeps it in the
/// middle, for a carousel). The wheel and touch panning work as usual. <c>Padding</c> is applied
/// as the margin of the content (do not set a Margin on the content element yourself).
/// </summary>
public sealed class SmoothScrollViewer : ScrollViewer
{
    private static readonly DependencyProperty GlideVerticalProperty = DependencyProperty.Register(
        "GlideVertical", typeof(double), typeof(SmoothScrollViewer),
        new PropertyMetadata(0.0, (d, e) => ((SmoothScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));

    private static readonly DependencyProperty GlideHorizontalProperty = DependencyProperty.Register(
        "GlideHorizontal", typeof(double), typeof(SmoothScrollViewer),
        new PropertyMetadata(0.0, (d, e) => ((SmoothScrollViewer)d).ScrollToHorizontalOffset((double)e.NewValue)));

    public static readonly DependencyProperty EdgePaddingProperty = Dp.Register<SmoothScrollViewer, double>(nameof(EdgePadding), 48.0);
    public static readonly DependencyProperty CenteredProperty = Dp.Register<SmoothScrollViewer, bool>(nameof(Centered), false);

    static SmoothScrollViewer()
    {
        PaddingProperty.OverrideMetadata(typeof(SmoothScrollViewer),
            new FrameworkPropertyMetadata(default(Thickness), (d, _) => ((SmoothScrollViewer)d).ApplyPadding()));
        // before ScrollViewer's own class handler, which would jump
        EventManager.RegisterClassHandler(typeof(SmoothScrollViewer), RequestBringIntoViewEvent,
            new RequestBringIntoViewEventHandler((sender, e) => ((SmoothScrollViewer)sender).OnBringIntoView(e)));
    }

    public SmoothScrollViewer() => Focusable = false;

    public double EdgePadding
    {
        get => (double)GetValue(EdgePaddingProperty);
        set => SetValue(EdgePaddingProperty, value);
    }

    public bool Centered
    {
        get => (bool)GetValue(CenteredProperty);
        set => SetValue(CenteredProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        ApplyPadding();
    }

    /// <summary>
    /// The padding is room INSIDE the scrolled area (it becomes the content's margin), so a focus
    /// card that is wider than its row is not cut at the viewport's edge.
    /// </summary>
    private void ApplyPadding()
    {
        if (Content is FrameworkElement content && Padding != default)
        {
            content.Margin = Padding;
        }
    }

    private void OnBringIntoView(RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject is not Visual target || Content is not Visual content || ReferenceEquals(target, this) || !target.IsDescendantOf(content))
        {
            return;
        }
        e.Handled = true;
        var size = target is UIElement element ? element.RenderSize : default;
        // in the coordinates of the scrolled extent: where it is in the viewport now, plus how far that is scrolled
        var box = target.TransformToAncestor(this).TransformBounds(e.TargetRect.IsEmpty ? new Rect(size) : e.TargetRect);
        box.Offset(HorizontalOffset, VerticalOffset);
        if (ScrollableHeight > 0)
        {
            Glide(GlideVerticalProperty, VerticalOffset, Aim(box.Top, box.Bottom, VerticalOffset, ViewportHeight, ScrollableHeight));
        }
        if (ScrollableWidth > 0)
        {
            Glide(GlideHorizontalProperty, HorizontalOffset, Aim(box.Left, box.Right, HorizontalOffset, ViewportWidth, ScrollableWidth));
        }
    }

    private double Aim(double near, double far, double offset, double viewport, double scrollable)
    {
        double aim;
        if (Centered)
        {
            aim = (near + far) / 2 - viewport / 2;
        }
        else
        {
            var pad = Math.Min(EdgePadding, Math.Max(0, (viewport - (far - near)) / 2));
            aim = near - pad < offset ? near - pad : far + pad > offset + viewport ? far + pad - viewport : offset;
        }
        return Math.Clamp(aim, 0, scrollable);
    }

    private void Glide(DependencyProperty property, double from, double to)
    {
        if (Math.Abs(from - to) < 0.5)
        {
            return;
        }
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(property, null);
            SetValue(property, to);
            return;
        }
        SetValue(property, to); // the base value: where the glide ends, and stays
        BeginAnimation(property, new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        }, HandoffBehavior.SnapshotAndReplace);
    }
}

/// <summary>From which edge a <see cref="SlideHost"/> comes in.</summary>
public enum SlideEdge { Right, Bottom, Left, Top }

/// <summary>
/// Content that slides in from an edge while <see cref="IsOpen"/> and is collapsed (out of the
/// focus order, out of layout) while closed. The friends panel and <see cref="BottomSheet"/> use it.
/// </summary>
public class SlideHost : ContentControl
{
    public static readonly DependencyProperty IsOpenProperty = Dp.Register<SlideHost, bool>(nameof(IsOpen), false, (d, _) => ((SlideHost)d).Apply(true));
    public static readonly DependencyProperty EdgeProperty = Dp.Register<SlideHost, SlideEdge>(nameof(Edge), SlideEdge.Right);

    private readonly TranslateTransform _shift = new();

    public SlideHost()
    {
        Focusable = false;
        RenderTransform = _shift;
        Visibility = Visibility.Collapsed;
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public SlideEdge Edge
    {
        get => (SlideEdge)GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    private void Apply(bool animate)
    {
        var horizontal = Edge is SlideEdge.Left or SlideEdge.Right;
        var property = horizontal ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        if (IsOpen)
        {
            Visibility = Visibility.Visible;
            UpdateLayout();
        }
        var extent = Math.Max(120, horizontal ? ActualWidth : ActualHeight);
        var away = Edge is SlideEdge.Left or SlideEdge.Top ? -extent : extent;
        if (!animate || !IsLoaded)
        {
            _shift.BeginAnimation(property, null);
            _shift.SetValue(property, IsOpen ? 0.0 : away);
            Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        var slide = new DoubleAnimation(IsOpen ? away : 0, IsOpen ? 0 : away, TimeSpan.FromMilliseconds(IsOpen ? 240 : 180))
        {
            EasingFunction = new CubicEase { EasingMode = IsOpen ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        if (!IsOpen)
        {
            slide.Completed += (_, _) =>
            {
                if (!IsOpen)
                {
                    Visibility = Visibility.Collapsed;
                }
            };
        }
        _shift.BeginAnimation(property, slide);
    }
}

/// <summary>A sheet that rises from the bottom over the page (participants, quick toggles). <c>IsOpen</c>, <c>Content</c>.</summary>
public sealed class BottomSheet : SlideHost
{
    public BottomSheet()
    {
        Edge = SlideEdge.Bottom;
        VerticalAlignment = VerticalAlignment.Bottom;
    }
}
