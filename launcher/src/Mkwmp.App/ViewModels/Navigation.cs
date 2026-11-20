// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using Mkwmp.App.Input;
using Mkwmp.Core;

namespace Mkwmp.App.ViewModels;

/// <summary>
/// One entry of the hint bar: the button, what it does here, and (for the mouse and touch) what a
/// tap on the hint does; without <paramref name="Invoke"/> the tap presses the button.
/// </summary>
public sealed record Hint(PadButton Button, string Label, Action? Invoke = null);

/// <summary>
/// Anything that sits above the page and takes the input while it is open: a modal, a bottom
/// sheet, the friends panel. The shell asks it for the hints and offers it the buttons first.
/// </summary>
public abstract class LayerViewModel : ObservableObject
{
    /// <summary>The hint bar while this layer is on top; null for the shell's default (Back, OK).</summary>
    public virtual IReadOnlyList<Hint>? Hints => null;

    /// <summary>A button other than the directions and A. True when used.</summary>
    public virtual bool OnButton(PadButton button) => false;

    /// <summary>The launcher's state changed (UI thread). The default re-reads every binding.</summary>
    public virtual void OnStateChanged() => RaiseAll();

    /// <summary>The layer came on top / went away.</summary>
    public virtual void OnOpened()
    {
    }

    public virtual void OnClosed()
    {
    }
}

/// <summary>
/// The base of a page's view model. A page reads <see cref="Launcher"/>'s views in its properties
/// (they are re-read on every change: <see cref="OnStateChanged"/>), and asks <see cref="Shell"/>
/// for navigation, modals, toasts.
/// </summary>
public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel(ShellViewModel shell) => Shell = shell ?? throw new ArgumentNullException(nameof(shell));

    public ShellViewModel Shell { get; }

    public Strings S => Shell.S;

    public ILauncher Launcher => Shell.Launcher;

    /// <summary>Home shows the top bar (avatars, clock, battery); the other pages have their own PageHeader.</summary>
    public virtual bool ShowTopBar => false;

    /// <summary>The hint bar of this page. Default: Friends, Back (when there is a page to go back to), OK.</summary>
    public virtual IReadOnlyList<Hint> Hints => Shell.DefaultHints();

    /// <summary>The page became the visible one (also when coming back to it).</summary>
    public virtual void OnEnter()
    {
    }

    /// <summary>Another page covers it, or it was closed.</summary>
    public virtual void OnLeave()
    {
    }

    /// <summary>The launcher's state changed (UI thread). The default re-reads every binding.</summary>
    public virtual void OnStateChanged() => RaiseAll();

    /// <summary>B / Esc. True when the page used it (closed its own sub-step); false lets the shell go back.</summary>
    public virtual bool OnBack() => false;

    /// <summary>X, LB, Start, Back (Y and RB are the friends panel's). True when used.</summary>
    public virtual bool OnButton(PadButton button) => false;

    /// <summary>Call when <see cref="Hints"/> would now return something else.</summary>
    protected void RefreshHints()
    {
        Raise(nameof(Hints));
        Shell.RefreshHints();
    }
}

public enum NavKind { Forward, Back, Replace }

/// <summary>The page stack. Pages are registered by route in <see cref="Routes"/>; sub-pages are just pushed.</summary>
public sealed class Navigator : ObservableObject
{
    private readonly List<PageViewModel> _stack = [];
    private readonly Dictionary<string, Func<PageViewModel>> _routes = new(StringComparer.OrdinalIgnoreCase);

    public PageViewModel? Current => _stack.Count > 0 ? _stack[^1] : null;

    public bool CanGoBack => _stack.Count > 1;

    /// <summary>How <see cref="Current"/> was reached (the transition's direction).</summary>
    public NavKind LastKind { get; private set; }

    /// <summary>A page left the stack for good (its view can be dropped).</summary>
    public event EventHandler<PageViewModel>? Removed;

    public IReadOnlyCollection<string> RouteNames => _routes.Keys;

    public void Register(string route, Func<PageViewModel> factory) => _routes[route] = factory;

    /// <summary>Pushes the page of a registered route; false for an unknown route.</summary>
    public bool Navigate(string route)
    {
        if (!_routes.TryGetValue(route, out var factory))
        {
            return false;
        }
        Navigate(factory());
        return true;
    }

    public void Navigate(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var old = Current;
        _stack.Add(page);
        Changed(old, NavKind.Forward);
    }

    public bool GoBack()
    {
        if (!CanGoBack)
        {
            return false;
        }
        var old = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        Changed(old, NavKind.Back);
        Removed?.Invoke(this, old);
        return true;
    }

    /// <summary>Drops the whole stack and shows this page (after setup: home).</summary>
    public void ResetTo(PageViewModel page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var old = Current;
        var dropped = _stack.ToList();
        _stack.Clear();
        _stack.Add(page);
        Changed(old, NavKind.Replace);
        foreach (var gone in dropped)
        {
            Removed?.Invoke(this, gone);
        }
    }

    public bool ResetTo(string route)
    {
        if (!_routes.TryGetValue(route, out var factory))
        {
            return false;
        }
        ResetTo(factory());
        return true;
    }

    private void Changed(PageViewModel? old, NavKind kind)
    {
        LastKind = kind;
        old?.OnLeave();
        Current?.OnEnter();
        Raise(nameof(Current));
        Raise(nameof(CanGoBack));
    }
}

/// <summary>The route names of the registered pages (<c>Shell.Navigate(Routes.Settings)</c>).</summary>
public static class Routes
{
    public const string Home = "home";
    public const string Settings = "settings";
    public const string Chat = "chat";
    public const string Setup = "setup";
    public const string Gallery = "gallery";

    /// <summary>Every top-level page. Add a line here for a new one (and its DataTemplate in Views/ViewMap.xaml).</summary>
    public static void RegisterAll(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        shell.Nav.Register(Home, () => new HomeViewModel(shell));
        shell.Nav.Register(Settings, () => new SettingsViewModel(shell));
        shell.Nav.Register(Chat, () => new ChatViewModel(shell));
        shell.Nav.Register(Setup, () => new SetupViewModel(shell));
        shell.Nav.Register(Gallery, () => new GalleryViewModel(shell));
    }
}
