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
    private const double BaseMinWidth = DockedWidth;
    private const double BaseMinHeight = DockedHeight;

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
        ApplyPanelSize();
    }

    /// <summary>
    /// F6 / Shift+F6 cycle the keyboard focus between the three views (Chat, Home, Settings), the same convention as
    /// Explorer and browsers, so a keyboard user does not have to Tab through every control. Ctrl+1/2/3 jump to a view.
    /// </summary>
    private void Window_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            var direct = e.Key switch
            {
                Key.D1 or Key.NumPad1 => PanelView.Home,
                Key.D2 or Key.NumPad2 => PanelView.Chat,
                Key.D3 or Key.NumPad3 => PanelView.Settings,
                _ => (PanelView?)null,
            };
            if (direct is { } view)
            {
                ShowView(view);
                FocusViewAnchor(view);
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.F6 || (Keyboard.Modifiers & ~ModifierKeys.Shift) != ModifierKeys.None)
        {
            return;
        }

        var order = new[] { PanelView.Chat, PanelView.Home, PanelView.Settings };
        var current = Array.IndexOf(order, _view);
        var backwards = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var next = current < 0
            ? (backwards ? order.Length - 1 : 0)
            : (current + (backwards ? order.Length - 1 : 1)) % order.Length;
        ShowView(order[next]);
        FocusViewAnchor(order[next]);
        e.Handled = true;
    }
}
