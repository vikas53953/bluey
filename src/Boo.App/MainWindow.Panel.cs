using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Boo.Core;

namespace Boo.App;

/// <summary>Which of the panel's three views is showing.</summary>
public enum PanelView { Home, Chat, Settings }

/// <summary>Docked: hangs under the notch at a fixed size. Windowed: "Open in a window", movable and resizable.</summary>
public enum PanelMode { Docked, Windowed }

/// <summary>
/// The hanging panel (round 3, 22 Nov 2026): the shell of the companion window. One dark sheet under the notch with
/// three views (Home, Chat, Settings), opened by Ctrl+Alt+B, a click on the eyes, or hovering the notch; a panel the
/// hover opened closes again when the pointer leaves both the notch and the panel. Everything the views contain
/// (asking, focus, settings, approvals) stays in the other MainWindow partials; this file only moves the furniture.
/// </summary>
public partial class MainWindow
{
    // Docked sizes in DIP, measured from HeyClicky 1.1.6 (406 x 310 panel, 620 x 500 settings) and rounded to Boo's grid.
    internal const double DockedWidth = 420;
    internal const double DockedHeight = 330;
    internal const double SettingsDockedWidth = 640;
    internal const double SettingsDockedHeight = 520;
    internal const double WindowedWidth = 760;
    internal const double WindowedHeight = 520;

    /// <summary>How long the pointer may be off both the notch and the panel before a hover-opened panel folds away.</summary>
    internal static readonly TimeSpan HoverCloseDelay = TimeSpan.FromMilliseconds(400);

    private PanelView _view = PanelView.Home;
    private PanelMode _mode = PanelMode.Docked;
    private bool _openedByHover;
    private bool _notchPointerOver;
    private DispatcherTimer? _hoverCloseTimer;
    private Rect? _windowedBounds;

    /// <summary>The view showing now.</summary>
    public PanelView CurrentView => _view;

    /// <summary>Docked under the notch, or opened in a window.</summary>
    public PanelMode Mode => _mode;

    /// <summary>True while the panel is open because the pointer hovered the notch (it folds when the pointer leaves).</summary>
    public bool OpenedByHover => _openedByHover;

    /// <summary>
    /// Shows one view and hides the other two. The hidden views leave the UI Automation control view (Collapsed), so
    /// a screen reader and the smoke tests only ever see the controls that are on screen.
    /// </summary>
    public void ShowView(PanelView view)
    {
        _view = view;
        HomeView.Visibility = view == PanelView.Home ? Visibility.Visible : Visibility.Collapsed;
        ChatView.Visibility = view == PanelView.Chat ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = view == PanelView.Settings ? Visibility.Visible : Visibility.Collapsed;
        HomeTabButton.Tag = view == PanelView.Home ? "Active" : null;
        ChatTabButton.Tag = view == PanelView.Chat ? "Active" : null;
        SettingsTabButton.Tag = view == PanelView.Settings ? "Active" : null;
        // UI Automation: the window's HelpText names the open view ("Home", "Chat", "Settings").
        AutomationProperties.SetHelpText(this, view.ToString());
        if (view == PanelView.Home)
        {
            UpdateGreeting();
        }
        ApplyPanelSize();
        if (IsVisible && IsActive)
        {
            FocusViewAnchor(view);
        }
    }

    /// <summary>The control keyboard focus lands on when a view opens.</summary>
    private void FocusViewAnchor(PanelView view)
    {
        IInputElement anchor = view switch
        {
            PanelView.Chat => InputBox,
            PanelView.Settings => DisplayBox,
            _ => FocusTaskBox,
        };
        anchor.Focus();
        Keyboard.Focus(anchor);
    }

    /// <summary>Called by App before Show(): Ctrl+Alt+B, the tray and a click on the eyes activate the panel.</summary>
    public void PrepareShow(bool fromHover)
    {
        _openedByHover = fromHover;
        _hoverCloseTimer?.Stop();
        // A hover never takes the keyboard from the app the user is working in; a deliberate open does.
        ShowActivated = !fromHover;
        if (_mode == PanelMode.Docked)
        {
            ApplyPanelSize();
            PositionNearDisplay();
        }
    }

    /// <summary>A deliberate open (hotkey, click) after a hover-open keeps the panel up when the pointer leaves.</summary>
    public void KeepOpen()
    {
        _openedByHover = false;
        _hoverCloseTimer?.Stop();
    }

    /// <summary>App forwards the notch's pointer state so a hover-opened panel knows when the pointer has left both.</summary>
    public void NotchPointerOverChanged(bool over)
    {
        _notchPointerOver = over;
        if (over)
        {
            _hoverCloseTimer?.Stop();
        }
        else
        {
            ArmHoverClose();
        }
    }

    private void ArmHoverClose()
    {
        if (!_openedByHover || !IsVisible || IsMouseOver || _notchPointerOver)
        {
            return;
        }
        _hoverCloseTimer ??= CreateHoverCloseTimer();
        _hoverCloseTimer.Stop();
        _hoverCloseTimer.Start();
    }

    private DispatcherTimer CreateHoverCloseTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = HoverCloseDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_openedByHover && IsVisible && !IsMouseOver && !_notchPointerOver)
            {
                _onClose();
            }
        };
        return timer;
    }

    private void Panel_OnMouseEnter(object sender, MouseEventArgs e) => _hoverCloseTimer?.Stop();

    private void Panel_OnMouseLeave(object sender, MouseEventArgs e) => ArmHoverClose();

    private void Panel_OnDeactivated(object? sender, EventArgs e)
    {
        // Like HeyClicky: a click somewhere else closes a panel the hover opened. A panel opened on purpose stays.
        if (_openedByHover && IsVisible && !IsMouseOver)
        {
            _onClose();
        }
    }

    // ---------------------------------------------------------------- views

    private void HomeTab_OnClick(object sender, RoutedEventArgs e) => ShowView(PanelView.Home);

    private void ChatTab_OnClick(object sender, RoutedEventArgs e) => ShowView(PanelView.Chat);

    private void SettingsTab_OnClick(object sender, RoutedEventArgs e) => ShowView(PanelView.Settings);

    private void HomeHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        ShowView(PanelView.Home);
        HistorySection.BringIntoView();
    }

    private void SettingsNav_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string target } button)
        {
            return;
        }
        foreach (var child in SettingsNav.Children)
        {
            if (child is Button other)
            {
                other.Tag = ReferenceEquals(other, button) ? "Active" : other.Tag;
            }
        }
        if (FindName(target) is FrameworkElement heading)
        {
            heading.BringIntoView();
        }
    }

    private void InputBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        InputPlaceholder.Visibility = InputBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateGreeting()
    {
        var hour = _timeProvider.GetLocalNow().Hour;
        HomeGreeting.Text = hour switch
        {
            < 5 => "Still up?",
            < 12 => "Good morning.",
            < 17 => "Good afternoon.",
            _ => "Good evening.",
        };
    }

    // ---------------------------------------------------------------- docked / windowed

    private void OpenInWindowButton_OnClick(object sender, RoutedEventArgs e) =>
        SetMode(_mode == PanelMode.Docked ? PanelMode.Windowed : PanelMode.Docked);

    /// <summary>Docks the panel under the notch again, or opens it as a movable, resizable window.</summary>
    public void SetMode(PanelMode mode)
    {
        if (_mode == mode)
        {
            return;
        }
        if (mode == PanelMode.Docked)
        {
            _windowedBounds = new Rect(Left, Top, Width, Height);
        }
        _mode = mode;
        KeepOpen();
        if (mode == PanelMode.Windowed)
        {
            PanelShell.CornerRadius = new CornerRadius(18);
            PanelShell.BorderThickness = new Thickness(1);
            if (_windowedBounds is { } saved)
            {
                Left = saved.Left;
                Top = saved.Top;
                Width = saved.Width;
                Height = saved.Height;
            }
            else
            {
                Width = WindowedWidth;
                Height = WindowedHeight;
                Top += 24; // drop it clear of the notch so the eyes stay visible
            }
            AutomationProperties.SetItemStatus(PanelShell, "windowed");
            return;
        }
        PanelShell.CornerRadius = new CornerRadius(0, 0, 18, 18);
        PanelShell.BorderThickness = new Thickness(1, 0, 1, 1);
        AutomationProperties.SetItemStatus(PanelShell, "docked");
        if (WindowState != WindowState.Normal)
        {
            WindowState = WindowState.Normal;
        }
        ApplyPanelSize();
        PositionNearDisplay();
    }

    /// <summary>Docked sizes follow the view: Settings needs the wider sheet. Windowed keeps whatever the user set.</summary>
    private void ApplyPanelSize()
    {
        if (_mode != PanelMode.Docked || WindowState != WindowState.Normal)
        {
            return;
        }
        var scale = TextScale.IsScaled(_appliedTextScale) ? _appliedTextScale : 1.0;
        var (width, height) = _view == PanelView.Settings
            ? (SettingsDockedWidth, SettingsDockedHeight)
            : (DockedWidth, DockedHeight);
        var work = SystemParameters.WorkArea;
        Width = Math.Min(width * scale, Math.Max(MinWidth, work.Width));
        Height = Math.Min(height * scale, Math.Max(MinHeight, work.Height));
        if (IsVisible)
        {
            PositionNearDisplay();
        }
    }

    /// <summary>
    /// Where a docked panel goes: horizontally centred on the selected display (the notch is centred there too) with
    /// its top edge on the screen top, pushed down by the notch offset the user chose. Physical pixels in, DIP out.
    /// </summary>
    internal static RectD DockedPlacement(RectD screenPixelBounds, double scale, double verticalOffsetDip, double widthDip, double heightDip)
    {
        var width = widthDip * scale;
        var height = heightDip * scale;
        var left = screenPixelBounds.X + (screenPixelBounds.Width - width) / 2;
        var top = screenPixelBounds.Y + verticalOffsetDip * scale;
        return MonitorLayout.SnapToPixels(new RectD(left, top, width, height));
    }

    private void TopBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != PanelMode.Windowed || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // DragMove needs the button down; a release in between is harmless.
        }
    }
}
