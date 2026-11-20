// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Mkwmp.App.Input;
using Mkwmp.App.ViewModels;

namespace Mkwmp.App.Controls;

/// <summary>
/// Shows the navigator's current page. The view of a page view model is the DataTemplate for its
/// type (Views/ViewMap.xaml); it is created once and kept while the page is on the stack, so going
/// back finds the page as it was (scroll position, focus). A page comes in with a short fade and
/// slide; the focus goes to the stop it had before, else to its default stop.
/// </summary>
public sealed class PageHost : Decorator
{
    public static readonly DependencyProperty NavigatorProperty = Dp.Register<PageHost, Navigator?>(nameof(Navigator), null,
        (d, e) => ((PageHost)d).OnNavigatorChanged((Navigator?)e.OldValue, (Navigator?)e.NewValue));

    private readonly Dictionary<PageViewModel, FrameworkElement> _views = [];
    private readonly Dictionary<FrameworkElement, WeakReference<UIElement>> _focus = [];
    private readonly TranslateTransform _shift = new();

    public Navigator? Navigator
    {
        get => (Navigator?)GetValue(NavigatorProperty);
        set => SetValue(NavigatorProperty, value);
    }

    /// <summary>Raised after a page was shown and focused.</summary>
    public event EventHandler? PageShown;

    private void OnNavigatorChanged(Navigator? old, Navigator? now)
    {
        if (old is not null)
        {
            old.PropertyChanged -= OnNavigatorProperty;
            old.Removed -= OnRemoved;
        }
        if (now is not null)
        {
            now.PropertyChanged += OnNavigatorProperty;
            now.Removed += OnRemoved;
        }
        Show();
    }

    private void OnNavigatorProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.Navigator.Current))
        {
            Show();
        }
    }

    private void OnRemoved(object? sender, PageViewModel page)
    {
        if (_views.Remove(page, out var view))
        {
            _focus.Remove(view);
        }
    }

    private void Show()
    {
        var page = Navigator?.Current;
        if (Child is FrameworkElement leaving && FocusNav.Current(this) is { } focused && FocusNav.IsAlive(focused, leaving))
        {
            _focus[leaving] = new WeakReference<UIElement>(focused);
        }
        if (page is null)
        {
            Child = null;
            return;
        }
        if (!_views.TryGetValue(page, out var view))
        {
            view = Create(page);
            _views[page] = view;
        }
        if (ReferenceEquals(Child, view))
        {
            return;
        }
        Child = view;
        view.RenderTransform = _shift;
        if (IsLoaded)
        {
            var from = Navigator!.LastKind switch { NavKind.Forward => 56.0, NavKind.Back => -56.0, _ => 0.0 };
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => FocusPage(view));
    }

    private void FocusPage(FrameworkElement view)
    {
        if (!ReferenceEquals(Child, view))
        {
            return;
        }
        var restored = _focus.TryGetValue(view, out var reference) && reference.TryGetTarget(out var last)
            && FocusNav.IsAlive(last, view) && FocusNav.Focus(last);
        if (!restored)
        {
            FocusNav.FocusDefault(view);
        }
        PageShown?.Invoke(this, EventArgs.Empty);
    }

    private FrameworkElement Create(PageViewModel page)
    {
        FrameworkElement view;
        if (TryFindResource(new DataTemplateKey(page.GetType())) is DataTemplate template && template.LoadContent() is FrameworkElement made)
        {
            view = made;
        }
        else
        {
            view = new TextBlock
            {
                Text = $"No view for {page.GetType().Name}: add a DataTemplate to Views/ViewMap.xaml",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 30,
            };
        }
        view.DataContext = page;
        return view;
    }
}
