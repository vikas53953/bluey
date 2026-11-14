using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Boo.Core;

namespace Boo.App;

/// <summary>
/// Accessibility behaviours of the companion window: Windows text scaling, F6 region cycling.
/// UI Automation names, heading levels and groups live in MainWindow.xaml; High Contrast brushes in App.xaml.cs.
/// </summary>
public partial class MainWindow
{
    // The XAML MinWidth/MinHeight at 100% text size.
    private const double BaseMinWidth = 760;
    private const double BaseMinHeight = 520;

    private double _appliedTextScale = 1.0;
    private bool _textScaleWatched;

    /// <summary>The text scale factor currently applied to the window content (1.0 = none).</summary>
    public double AppliedTextScale => _appliedTextScale;

    /// <summary>
    /// Honours Settings &gt; Accessibility &gt; Text size. WPF does not do this on its own: it only follows DPI.
    /// The whole content grid is scaled with a LayoutTransform so text, controls and hit targets grow together
    /// and the two columns reflow instead of clipping; the minimum window size grows with it but never past
    /// the work area.
    /// </summary>
    private void ApplyTextScale()
    {
        if (!_textScaleWatched)
        {
            _textScaleWatched = true;
            AccessibilitySettings.WatchTextScale(Dispatcher, ApplyTextScale);
        }

        var factor = AccessibilitySettings.TextScaleFactor();
        if (Math.Abs(factor - _appliedTextScale) < 0.001)
        {
            return;
        }
        _appliedTextScale = factor;

        if (TextScale.IsScaled(factor))
        {
            var transform = new ScaleTransform(factor, factor);
            transform.Freeze();
            MainSurface.LayoutTransform = transform;
        }
        else
        {
            MainSurface.LayoutTransform = Transform.Identity;
        }

        var work = SystemParameters.WorkArea;
        MinWidth = TextScale.ScaledMinimum(BaseMinWidth, factor, work.Width);
        MinHeight = TextScale.ScaledMinimum(BaseMinHeight, factor, work.Height);
        if (Width < MinWidth)
        {
            Width = MinWidth;
        }
        if (Height < MinHeight)
        {
            Height = MinHeight;
        }
    }

    /// <summary>
    /// F6 / Shift+F6 cycle the keyboard focus between the three regions (Ask, Focus, Settings), the same
    /// convention as Explorer and browsers, so a keyboard user does not have to Tab through every control.
    /// </summary>
    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.F6 || (Keyboard.Modifiers & ~ModifierKeys.Shift) != ModifierKeys.None)
        {
            return;
        }

        var regions = new IInputElement[] { InputBox, FocusTaskBox, DisplayBox };
        var current = -1;
        for (var i = 0; i < regions.Length; i++)
        {
            if (regions[i] is DependencyObject region && IsFocusWithinRegion(region))
            {
                current = i;
                break;
            }
        }

        var backwards = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var next = current < 0
            ? (backwards ? regions.Length - 1 : 0)
            : (current + (backwards ? regions.Length - 1 : 1)) % regions.Length;

        var target = regions[next];
        (target as FrameworkElement)?.BringIntoView();
        target.Focus();
        Keyboard.Focus(target);
        e.Handled = true;
    }

    /// <summary>True when keyboard focus is inside the section (BooSection) that holds <paramref name="anchor"/>.</summary>
    private static bool IsFocusWithinRegion(DependencyObject anchor)
    {
        var focused = Keyboard.FocusedElement as DependencyObject;
        if (focused is null)
        {
            return false;
        }
        var section = FindSection(anchor);
        if (section is null)
        {
            return ReferenceEquals(focused, anchor);
        }
        return section.IsKeyboardFocusWithin;
    }

    private static Boo.App.Controls.BooSection? FindSection(DependencyObject element)
    {
        var node = element;
        while (node is not null)
        {
            if (node is Boo.App.Controls.BooSection section)
            {
                return section;
            }
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
