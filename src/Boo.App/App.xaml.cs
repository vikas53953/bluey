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
    private readonly Boo.Core.CrashLoopGuard _crashGuard = new(maxRecoveries: 5, window: TimeSpan.FromMinutes(1));
    private Boo.Storage.CrashLogStore? _crashLog;
    private volatile bool _fatalCrashLogged;
    // Until OnStartup finishes, an unhandled error is fatal: recovering would leave an invisible Boo holding the mutex.
    private bool _startupComplete;
    private Boo.Core.ShortcutStatus _shortcuts = Boo.Core.ShortcutStatus.Pending;
    private Forms.ToolStripItem? _trayShowItem;
    private Forms.ToolStripItem? _trayStopItem;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var gallery = Array.IndexOf(e.Args, NotchGallery.Argument);
        if (gallery >= 0)
        {
            // CI/QA only: render every notch face state to PNGs and exit (no window, no capture, no settings).
            ApplyThemeResources();
            var output = gallery + 1 < e.Args.Length ? e.Args[gallery + 1] : Path.Combine(Environment.CurrentDirectory, "notch-gallery");
            Shutdown(NotchGallery.Run(output));
            return;
        }
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
        _crashLog = new Boo.Storage.CrashLogStore(_dataDirectory);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        var crashedLastTime = _crashLog.ConsumeRecoveredNotice(DateTimeOffset.UtcNow, TimeSpan.FromDays(7));
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
            verticalOffset: () => _panel?.VerticalOffset ?? 0,
            appReducedMotion: () => _panel?.ReducedMotionEnabled == true);
        _panel.FaceInputsChanged += OnFaceInputsChanged;
        _notch.EyesHeldChanged += held => _panel?.SetEyesHeld(held);
        _notch.ShowNotch();

        CreateTrayIcon();
        RefreshShortcutStatus(announce: true);
        if (crashedLastTime)
        {
            var shortcutMessage = _shortcuts.PanelMessage;
            _panel?.ShowCrashNotice(shortcutMessage is null
                ? Boo.Core.CrashReport.RecoveredNotice
                : Boo.Core.CrashReport.RecoveredNotice + " " + shortcutMessage);
        }
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _startupComplete = true;
    }

    internal void OpenPanel(bool settings)
    {
        if (_panel is null)
        {
            _panel = CreatePanel();
            _panel.InitializeBeforeNotch();
            RefreshShortcutStatus(announce: false);
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
        if (_notch is not null)
        {
            panel.FaceInputsChanged += OnFaceInputsChanged;
        }
        panel.StateChanged += (_, _) =>
        {
            if (panel.WindowState != WindowState.Minimized)
            {
                _restorePanelState = panel.WindowState;
            }
        };
        return panel;
    }

    private void OnFaceInputsChanged(Boo.Core.FaceInputs inputs) => _notch?.UpdateFace(inputs);

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _panel?.RefreshSelectedDisplay();
            RetryShortcuts();
        });
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
        // The tray never depends on global hotkeys: it is the fallback when another app owns Ctrl+Alt+B / Ctrl+Alt+F12.
        var menu = new Forms.ContextMenuStrip();
        _trayShowItem = menu.Items.Add(_shortcuts.TrayShowLabel, null, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _notch?.ShowNotch();
            OpenPanel(false);
        }));
        _trayStopItem = menu.Items.Add(_shortcuts.TrayStopLabel, null, (_, _) => Dispatcher.BeginInvoke(StopAll));
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
        else if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
        {
            Dispatcher.BeginInvoke(RetryShortcuts);
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            Dispatcher.BeginInvoke(StopAll);
        }
    }

    /// <summary>Another app may have released Ctrl+Alt+B / Ctrl+Alt+F12 since startup; claim them if so.</summary>
    private void RetryShortcuts()
    {
        if (_notch?.RetryHotkeys() == true)
        {
            RefreshShortcutStatus(announce: true);
        }
    }

    /// <summary>
    /// Folds the notch's hotkey registrations and the panel's hold-to-talk hook into one truthful status and shows it
    /// on the panel (persistent settings line), the notch (short hint), the tray labels and, once, a tray balloon.
    /// </summary>
    private void RefreshShortcutStatus(bool announce)
    {
        if (_notch is null)
        {
            return;
        }

        var previous = _shortcuts;
        var holdToTalk = _panel?.HoldToTalkAvailable ?? (previous.IsPending || previous.HoldToTalk);
        _shortcuts = new Boo.Core.ShortcutStatus(_notch.EmergencyStopRegistered, _notch.OpenShortcutRegistered, holdToTalk);
        var changed = previous.IsPending || previous != _shortcuts;

        _panel?.ShowShortcutStatus(_shortcuts, announce && changed);
        if (_trayShowItem is not null) _trayShowItem.Text = _shortcuts.TrayShowLabel;
        if (_trayStopItem is not null) _trayStopItem.Text = _shortcuts.TrayStopLabel;

        if (changed && _notch.CaptureExclusionAvailable)
        {
            // "Capture paused" outranks a shortcut hint, so only touch the notch when capture exclusion is fine.
            if (_shortcuts.NotchMessage is { } hint)
            {
                _notch.SetStatus(hint);
            }
            else if (!previous.IsPending && previous.NotchMessage is not null)
            {
                _notch.SetStatus(string.Empty);
            }
        }

        if (announce && changed && (!_shortcuts.OpenPanel || !_shortcuts.EmergencyStop) && _shortcuts.PanelMessage is { } message)
        {
            try
            {
                _tray?.ShowBalloonTip(6000, "Boo shortcuts", message, Forms.ToolTipIcon.Info);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    // ---------------------------------------------------------------- crash handling

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var recover = _startupComplete && Boo.Core.CrashReport.IsRecoverable(e.Exception) && _crashGuard.TryRecover(DateTimeOffset.UtcNow);
        WriteCrashLog(e.Exception, "Dispatcher", recover);
        if (!recover)
        {
            // Let it terminate; the AppDomain handler sees it next and must not log it twice.
            _fatalCrashLogged = true;
            TryStopAll();
            return;
        }

        e.Handled = true;
        TryStopAll();
        try
        {
            _panel?.ShowCrashNotice(Boo.Core.CrashReport.KeptRunningNotice);
            if (_notch?.CaptureExclusionAvailable != false)
            {
                _notch?.SetStatus("Recovered");
            }
        }
        catch (Exception)
        {
            // The notice is best-effort; the log is already written and Boo stays up.
        }
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (!_fatalCrashLogged)
        {
            WriteCrashLog(e.ExceptionObject, "AppDomain", recovered: false);
            _fatalCrashLogged = true;
        }

        if (!e.IsTerminating)
        {
            return;
        }

        try
        {
            // Release the microphone/capture and the tray icon before the process dies.
            if (Dispatcher.CheckAccess())
            {
                TryStopAll();
                _tray?.Dispose();
            }
            else if (!Dispatcher.HasShutdownStarted)
            {
                Dispatcher.Invoke(() =>
                {
                    TryStopAll();
                    _tray?.Dispose();
                }, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1));
            }
        }
        catch (Exception)
        {
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        if (e.Exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            return; // cancelled work after Stop is expected, not a problem worth a log
        }

        WriteCrashLog(e.Exception, "UnobservedTask", recovered: true);
    }

    private void TryStopAll()
    {
        try
        {
            StopAll();
        }
        catch (Exception)
        {
            // Stopping is best-effort while handling a crash; never throw from a crash handler.
        }
    }

    private void WriteCrashLog(object? error, string source, bool recovered)
    {
        try
        {
            var store = _crashLog ??= new Boo.Storage.CrashLogStore(
                string.IsNullOrEmpty(_dataDirectory) ? ResolveDataDirectory() : _dataDirectory);
            store.Write(error, source, DateTimeOffset.UtcNow, recovered, typeof(App).Assembly.GetName().Version?.ToString());
        }
        catch (Exception)
        {
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
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
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

