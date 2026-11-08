using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;
using MediaSystemColors = System.Windows.SystemColors;

namespace Boo.App;

public partial class App : System.Windows.Application
{
    /// <summary>One shared capture-exclusion decision (BOO_QA_CAPTURABLE) for every Boo window.</summary>
    internal static Boo.Core.CaptureExclusionPolicy CaptureExclusion { get; } = Boo.Core.CaptureExclusionPolicy.FromEnvironment();

    /// <summary>True when launched by the Run key ("--background"): only the notch appears, never the panel.</summary>
    internal static bool StartedInBackground { get; private set; }

    private Mutex? _singleInstance;
    private NotchWindow? _notch;
    private MainWindow? _panel;
    private Forms.NotifyIcon? _tray;
    private string _dataDirectory = string.Empty;
    private bool _ownsMutex;
    private bool _hasShownPanel;
    private WindowState _restorePanelState = WindowState.Normal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        StartedInBackground = e.Args.Contains(Boo.Core.StartupRegistration.BackgroundArgument, StringComparer.OrdinalIgnoreCase);

        _singleInstance = new Mutex(true, "Local\\Boo.Companion.SingleInstance", out var created);
        _ownsMutex = created;
        if (!created)
        {
            Shutdown(0);
            return;
        }

        _dataDirectory = ResolveDataDirectory();
        Directory.CreateDirectory(_dataDirectory);
        ApplyThemeResources();
        SystemParameters.StaticPropertyChanged += SystemParameters_OnStaticPropertyChanged;

        _panel = CreatePanel();
        _panel.InitializeBeforeNotch();
        _notch = new NotchWindow(
            onOpenPanel: () => OpenPanel(false),
            onStop: StopAll,
            selectedDisplay: () => _panel?.SelectedDisplay,
            appearance: _panel.CurrentAppearance,
            onDisplayLayoutChanged: () => _panel?.RefreshSelectedDisplay(),
            verticalOffset: () => _panel?.VerticalOffset ?? 0);
        _notch.ShowNotch();

        CreateTrayIcon();
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    internal void OpenPanel(bool settings)
    {
        if (_panel is null)
        {
            _panel = CreatePanel();
            _panel.InitializeBeforeNotch();
        }

        if (_panel.WindowState == WindowState.Minimized)
        {
            _panel.WindowState = _restorePanelState;
        }
        _panel.PrimeExternalTarget();
        if (settings)
        {
            _panel.ShowSettings();
        }

        if (!_panel.IsVisible)
        {
            if (!_hasShownPanel)
            {
                _panel.PrepareInitialPlacement();
                _panel.WindowState = WindowState.Maximized;
                _restorePanelState = WindowState.Maximized;
                _hasShownPanel = true;
            }
            _panel.Show();
        }
        else
        {
            _panel.Activate();
        }
        _panel.RefreshSelectedDisplay();
    }

    private MainWindow CreatePanel()
    {
        var panel = new MainWindow(
            dataDirectory: _dataDirectory,
            onClose: ClosePanel,
            onStop: StopAll,
            onDisplayChanged: display => _notch?.PositionOnDisplay(display),
            verticalOffset: () => _panel?.VerticalOffset ?? 0,
            ownedWindows: () => _notch is null ? Array.Empty<long>() : new[] { new System.Windows.Interop.WindowInteropHelper(_notch).Handle.ToInt64() },
            captureExclusion: () => _notch?.CaptureExclusionAvailable != false,
            onCaptureActivity: (active, displayName) => _notch?.SetCaptureActivity(active, displayName),
            onMicrophoneActivity: active => _notch?.SetMicrophoneActivity(active),
            onAppearanceChanged: appearance => _notch?.SetAppearance(appearance));
        panel.Closed += (_, _) => _panel = null;
        panel.StateChanged += (_, _) =>
        {
            if (panel.WindowState != WindowState.Minimized)
            {
                _restorePanelState = panel.WindowState;
            }
        };
        return panel;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() => _panel?.RefreshSelectedDisplay());
    }

    private void ClosePanel()
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
        }
        _notch?.RestoreExternalFocus();
    }

    private void StopAll()
    {
        if (_panel is not null)
        {
            _panel.StopAll();
        }
        else
        {
            _notch?.SetStatus("Stopped");
        }
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show Boo (Ctrl+Alt+B)", null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _notch?.ShowNotch();
            OpenPanel(false);
        }));
        menu.Items.Add("Stop", null, (_, _) => Dispatcher.BeginInvoke(StopAll));
        menu.Items.Add("Settings", null, (_, _) => Dispatcher.BeginInvoke(() => OpenPanel(true)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Dispatcher.BeginInvoke(Shutdown));

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Boo companion",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => OpenPanel(false));
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.SessionLogoff)
        {
            Dispatcher.BeginInvoke(StopAll);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Dispatcher.BeginInvoke(StopAll);
        }
    }

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("BOO_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Boo");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= SystemParameters_OnStaticPropertyChanged;
        _tray?.Dispose();
        _panel?.DisposeShell();
        _panel?.Close();
        _notch?.Close();
        if (_ownsMutex)
        {
            _singleInstance?.ReleaseMutex();
        }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void SystemParameters_OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            ApplyThemeResources();
        }
    }

    private void ApplyThemeResources()
    {
        var highContrast = SystemParameters.HighContrast;
        SetThemeBrush("BooInk", highContrast ? MediaSystemColors.WindowColor : MediaColor.FromRgb(0x17, 0x17, 0x19));
        SetThemeBrush("BooPanel", highContrast ? MediaSystemColors.WindowColor : MediaColor.FromRgb(0x20, 0x20, 0x23));
        SetThemeBrush("BooPanelRaised", highContrast ? MediaSystemColors.ControlColor : MediaColor.FromRgb(0x2A, 0x2A, 0x2E));
        SetThemeBrush("BooBorder", highContrast ? MediaSystemColors.ActiveBorderColor : MediaColor.FromRgb(0x41, 0x41, 0x46));
        SetThemeBrush("BooText", highContrast ? MediaSystemColors.WindowTextColor : MediaColor.FromRgb(0xFA, 0xF8, 0xF4));
        SetThemeBrush("BooMuted", highContrast ? MediaSystemColors.WindowTextColor : MediaColor.FromRgb(0xB8, 0xB4, 0xAE));
        SetThemeBrush("BooAccent", highContrast ? MediaSystemColors.HighlightColor : MediaColor.FromRgb(0xF3, 0xB5, 0x9B));
        SetThemeBrush("BooAccentDark", highContrast ? MediaSystemColors.HighlightColor : MediaColor.FromRgb(0xA9, 0x6E, 0x5C));
        SetThemeBrush("BooDanger", highContrast ? MediaSystemColors.WindowTextColor : MediaColor.FromRgb(0xF1, 0x8C, 0x87));
        SetThemeBrush("BooInput", highContrast ? MediaSystemColors.WindowColor : MediaColor.FromRgb(0x29, 0x29, 0x2D));
        SetThemeBrush("BooNotchBody", highContrast ? MediaSystemColors.WindowColor : MediaColor.FromRgb(0x00, 0x00, 0x00));
        SetThemeBrush("BooNotchEyes", highContrast ? MediaSystemColors.WindowTextColor : MediaColor.FromRgb(0xFF, 0xFF, 0xFF));
    }

    private void SetThemeBrush(string key, MediaColor color)
    {
        if (Resources[key] is SolidColorBrush brush)
        {
            var replacement = new SolidColorBrush(color);
            replacement.Freeze();
            Resources[key] = replacement;
        }
    }
}

