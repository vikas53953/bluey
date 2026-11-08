using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace Boo.App.Controls;

/// <summary>
/// A rounded card that is also a UI Automation landmark. WPF's Border and panels have no automation
/// peer, so screen readers saw Boo's window as one flat list of controls. A BooSection shows up as a
/// Group (ControlType.Group) named by AutomationProperties.Name, so a screen-reader user can jump
/// between "Ask Boo", "Focus", "Settings" and the "Review action" card. Its look is the implicit
/// BooSection style in App.xaml (no Themes/Generic.xaml, so no ThemeInfo attribute is needed).
/// </summary>
public sealed class BooSection : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(BooSection), new PropertyMetadata(new CornerRadius(16)));

    static BooSection()
    {
        // Sections are landmarks, not tab stops: Tab goes straight to the first control inside.
        FocusableProperty.OverrideMetadata(typeof(BooSection), new FrameworkPropertyMetadata(false));
        IsTabStopProperty.OverrideMetadata(typeof(BooSection), new FrameworkPropertyMetadata(false));
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new BooSectionAutomationPeer(this);

    private sealed class BooSectionAutomationPeer(BooSection owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(BooSection);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;

        // Groups are never keyboard focusable; without this WPF would report the (false) default anyway,
        // but being explicit keeps Narrator from offering the section itself as a stop.
        protected override bool IsKeyboardFocusableCore() => false;
    }
}
