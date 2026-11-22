// Copyright (C) 2026 Alexandre Moraes and contributors. GPL-3.0-or-later; see LICENSE.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mkwmp.App.Input;

namespace Mkwmp.App.Views.Settings;

/// <summary>
/// The age confirmation and the privacy policy it accepts, as one card of two rows: the one
/// component for privacy consent, in Settings › Online and on the welcome flow's consent page.
/// </summary>
public partial class PrivacyConsent : UserControl
{
    public static readonly DependencyProperty AcceptedProperty = DependencyProperty.Register(nameof(Accepted), typeof(bool), typeof(PrivacyConsent),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ShowAcceptProperty = DependencyProperty.Register(nameof(ShowAccept), typeof(bool), typeof(PrivacyConsent), new PropertyMetadata(true));

    public static readonly DependencyProperty CanChangeProperty = DependencyProperty.Register(nameof(CanChange), typeof(bool), typeof(PrivacyConsent), new PropertyMetadata(true));

    public static readonly DependencyProperty AcceptCaptionProperty = DependencyProperty.Register(nameof(AcceptCaption), typeof(string), typeof(PrivacyConsent), new PropertyMetadata(""));

    public static readonly DependencyProperty PolicyCommandProperty = DependencyProperty.Register(nameof(PolicyCommand), typeof(ICommand), typeof(PrivacyConsent), new PropertyMetadata(null));

    public PrivacyConsent() => InitializeComponent();

    /// <summary>"I'm 18 or older and I accept the privacy policy" (two-way).</summary>
    public bool Accepted
    {
        get => (bool)GetValue(AcceptedProperty);
        set => SetValue(AcceptedProperty, value);
    }

    /// <summary>False: only the policy row (nobody to take a consent from yet).</summary>
    public bool ShowAccept
    {
        get => (bool)GetValue(ShowAcceptProperty);
        set => SetValue(ShowAcceptProperty, value);
    }

    public bool CanChange
    {
        get => (bool)GetValue(CanChangeProperty);
        set => SetValue(CanChangeProperty, value);
    }

    /// <summary>Under the switch: what it means here (what withdrawing does; that the server has none yet).</summary>
    public string AcceptCaption
    {
        get => (string)GetValue(AcceptCaptionProperty);
        set => SetValue(AcceptCaptionProperty, value);
    }

    /// <summary>Opens the server's policy (rendered in the launcher).</summary>
    public ICommand? PolicyCommand
    {
        get => (ICommand?)GetValue(PolicyCommandProperty);
        set => SetValue(PolicyCommandProperty, value);
    }

    /// <summary>The focus on the switch (the consent page starts there).</summary>
    public bool FocusAccept() => FocusNav.Focus(AcceptRow);
}
