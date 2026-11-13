using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using Boo.Core;
using Boo.Providers;
using Boo.Storage;
using Boo.Windows;
using Forms = System.Windows.Forms;
using MediaSystemColors = System.Windows.SystemColors;

namespace Boo.App;

public partial class MainWindow : Window
{
    private const string ResponseInterruptedMessage = "OpenAI response was interrupted before completion. Try again.";
    private const string HistoryUnavailableMessage = "Focus history is unavailable.";
    private const string DisplaySelectionPersistenceMessage = "Display selection changed for this session; settings could not be saved.";
    private readonly string _dataDirectory;
    private readonly Action _onClose;
    private readonly Action _onStop;
    private readonly Action<DisplayInfo?> _onDisplayChanged;
    private readonly Func<double> _verticalOffset;
    private readonly Func<IReadOnlyList<long>> _ownedWindows;
    private readonly Func<bool> _captureExclusion;
    private readonly Action<bool, string?> _onCaptureActivity;
    private readonly Action<bool> _onMicrophoneActivity;
    private readonly Action<NotchAppearance> _onAppearanceChanged;
    private readonly RequestCoordinator _coordinator = new();
    private readonly CaptureActivityTracker _captureActivities = new();
    private readonly ObservableCollection<TranscriptLine> _transcript = new();
    private readonly RealtimeTranscript _realtimeTranscript = new();
    private readonly Dictionary<(string ItemId, int ContentIndex, string Speaker), int> _realtimeTranscriptRows = new();
    private readonly DispatcherTimer _focusTimer;
    private readonly DispatcherTimer _focusCaptureTimer;
    private readonly Dictionary<string, DisplayInfo> _displays = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AnnotationWindow> _annotationWindows = new();
    private readonly Queue<ToolRequest> _pendingActions = new();
    private CancellationTokenSource _requestCancellation = new();
    private RequestLease? _requestLease;
    private TargetObserver? _targets;
    private ControlObservationReader? _controlReader;
    private ScreenCapture? _capture;
    private AudioSession? _audio;
    private AudioUploadQueue? _audioUploads;
    private IScreenAssistant? _responses;
    private IFocusClassifier? _decisions;
    private RealtimeClient? _realtime;
    private DesktopTools? _desktop;
    private SettingsStore? _settingsStore;
    private SessionStore? _sessionStore;
    // OpenAI key store: voice (RealtimeClient) always uses it; text uses it only when OpenAI is the text provider.
    private CredentialStore? _credentialStore;
    // One DPAPI-protected file per provider (AiProviders.CredentialFileName), created on first use.
    private readonly Dictionary<AiProvider, CredentialStore> _credentialStores = new();
    private HttpClient? _http;
    private FocusSessionController? _focus;
    private CancellationTokenSource _focusCancellation = new();
    private bool _focusCaptureInFlight;
    private long _focusCaptureId;
    private readonly FocusClock _focusClock;
    private double _lastFocusSaveRealElapsed;
    private ToolRequest? _pendingAction;
    private ActionPreviewModel? _pendingPreview;
    private bool _approvalInFlight;
    private TaskAuthority? _pendingAuthority;
    private WindowIdentity? _pendingTarget;
    private ControlObservation? _pendingControls;
    private string? _pendingRealtimeCallId;
    private DisplayInfo? _selectedDisplay;
    private AppSettings _settings = new();
    private long _generation;
    private long _realtimeGeneration;
    private bool _realtimeConnected;
    private bool _listening;
    private int _realtimeToolsInFlight;
    private readonly TimeProvider _timeProvider;
    private readonly ChirpPlayer _chirps;
    private bool _voiceTaskAuthorized;
    private bool _closingForExit;
    private bool _displayAffinitySupported = true;
    private NotchAppearance _appearance;
    private AppearanceStore? _appearanceStore;
    private bool _shellInitialized;
    private bool _initialPlacementApplied;
    private bool _refreshingDisplay;
    private bool _synchronizingDisplaySelection;

    public MainWindow(
        string dataDirectory,
        Action onClose,
        Action onStop,
        Action<DisplayInfo?> onDisplayChanged,
        Func<double> verticalOffset,
        Func<IReadOnlyList<long>> ownedWindows,
        Func<bool> captureExclusion,
        Action<bool, string?> onCaptureActivity,
        Action<bool> onMicrophoneActivity,
        Action<NotchAppearance> onAppearanceChanged,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _chirps = new ChirpPlayer(_timeProvider);
        // 30 s, not 10: a briefly busy UI thread must not look like sleep (real suspend already pauses via StopAll).
        _focusClock = new FocusClock(_timeProvider, maximumTickSeconds: 30);
        _dataDirectory = dataDirectory;
        _onClose = onClose;
        _onStop = onStop;
        _onDisplayChanged = onDisplayChanged;
        _verticalOffset = verticalOffset;
        _ownedWindows = ownedWindows;
        _captureExclusion = captureExclusion;
        _onCaptureActivity = onCaptureActivity;
        _onMicrophoneActivity = onMicrophoneActivity;
        _onAppearanceChanged = onAppearanceChanged;
        _appearance = new NotchAppearance();
        InitializeComponent();

        _focusTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _focusTimer.Tick += FocusTimer_OnTick;
        _focusCaptureTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _focusCaptureTimer.Tick += FocusCaptureTimer_OnTick;
        TranscriptList.ItemsSource = _transcript;
        SourceInitialized += Window_OnSourceInitialized;
        SystemParameters.StaticPropertyChanged += SystemParameters_OnStaticPropertyChanged;
        Closed += (_, _) => SystemParameters.StaticPropertyChanged -= SystemParameters_OnStaticPropertyChanged;
    }

    public DisplayInfo? SelectedDisplay => _selectedDisplay;
    public double VerticalOffset => OffsetSlider.Value;
    public NotchAppearance CurrentAppearance => _appearance;

    // Face lane (U1/U2): app-level signals for the notch face; App forwards them to NotchWindow.UpdateFace.
    // Capture and microphone keep flowing through onCaptureActivity/onMicrophoneActivity.
    public event Action<FaceInputs>? FaceInputsChanged;
    public bool ReducedMotionEnabled => _settings.ReducedMotion;
    private FaceInputs _faceInputs = new();
    private DispatcherTimer? _faceMomentTimer;

    private readonly FaceMomentSchedule _faceMoments = new();

    private void SetFace(FaceInputs next, TimeSpan? momentFor = null, FaceMomentKind moment = FaceMomentKind.None)
    {
        // Responding / nudge / happy are moments: each is cleared when its own hold runs out, so a reply finishing
        // does not take a happy face (session complete) or a nudge with it, and vice versa.
        var now = Environment.TickCount64;
        if (momentFor is { } duration && moment != FaceMomentKind.None)
        {
            _faceMoments.Hold(moment, now, duration);
        }
        var switchedOff = FaceMomentKind.None;
        if (!next.Responding) switchedOff |= FaceMomentKind.Responding;
        if (!next.NudgeShowing) switchedOff |= FaceMomentKind.Nudge;
        if (!next.Happy) switchedOff |= FaceMomentKind.Happy;
        _faceMoments.Clear(switchedOff);
        ArmFaceMomentTimer(now);
        if (next == _faceInputs)
        {
            return;
        }
        _faceInputs = next;
        FaceInputsChanged?.Invoke(next);
    }

    private void ArmFaceMomentTimer(long nowMs)
    {
        var due = _faceMoments.NextDue(nowMs);
        _faceMomentTimer ??= CreateFaceMomentTimer();
        _faceMomentTimer.Stop();
        if (due is { } interval)
        {
            _faceMomentTimer.Interval = interval;
            _faceMomentTimer.Start();
        }
    }

    private DispatcherTimer CreateFaceMomentTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var expired = _faceMoments.Expire(Environment.TickCount64);
            SetFace(_faceInputs with
            {
                Responding = _faceInputs.Responding && !expired.HasFlag(FaceMomentKind.Responding),
                NudgeShowing = _faceInputs.NudgeShowing && !expired.HasFlag(FaceMomentKind.Nudge),
                Happy = _faceInputs.Happy && !expired.HasFlag(FaceMomentKind.Happy),
            });
        };
        return timer;
    }

    public void PrepareInitialPlacement()
    {
        InitializeBeforeNotch();
        if (!_initialPlacementApplied)
        {
            // Text scaling first, so PositionNearDisplay clamps the scaled minimum to the selected display.
            ApplyTextScale();
            PositionNearDisplay();
            _initialPlacementApplied = true;
        }
    }

    public void InitializeBeforeNotch()
    {
        if (_shellInitialized)
        {
            return;
        }

        LoadSettings();
        LoadAppearance();
        LoadDisplays();
        InitializeHistory();
        InitializeHoldToTalk();
        _shellInitialized = true;
    }

    /// <summary>
    /// Called before the panel is shown.  This preserves the external target
    /// while Boo is still non-foreground, before any panel activation occurs.
    /// </summary>
    public void PrimeExternalTarget()
    {
        if (_selectedDisplay is not null)
        {
            EnsureTargets().Snapshot(_selectedDisplay);
            return;
        }
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return;
        }
        _targets ??= new TargetObserver();
        var display = new DisplayInfo(
            screen.DeviceName,
            screen.Primary ? "Primary display" : screen.DeviceName,
            new RectD(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height),
            LiveMonitors.ScaleFor(screen));
        _targets.Snapshot(display);
    }

    public void ShowSettings()
    {
        SettingsSection.Visibility = Visibility.Visible;
        if (IsVisible)
        {
            DisplayBox.Focus();
        }
    }

    public void StopAll()
    {
        StopHoldToTalk();
        _generation++;
        _coordinator.Stop();

        _requestCancellation.Cancel();
        _requestCancellation.Dispose();
        _requestCancellation = new CancellationTokenSource();
        _requestLease = null;

        StopAudioUploads();

        try
        {
            _audio?.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            StatusText.Text = SanitizedError(ex, "Audio stopped");
        }

        var realtimeToStop = _realtime;
        _realtime = null;
        if (realtimeToStop is not null)
        {
            _ = InterruptRealtimeAsync(realtimeToStop);
        }

        _listening = false;
        _realtimeConnected = false;
        _voiceTaskAuthorized = false;
        _onMicrophoneActivity(false);
        TalkButton.Content = "Talk";
        ListeningStatus.Text = "Microphone stopped. Screen activity is shown in the sharing indicator.";
        ClearAnnotations();
        ClearActionPreview();
        _ = CompletePendingRealtimeToolAsync("Action stopped before approval or commit.");
        if (_focus is { Snapshot.Status: Boo.Core.FocusStatus.Running })
        {
            _focus.Pause("Stopped");
            StopFocusMonitoring();
            SaveFocusSnapshot();
            _focusTimer.Stop();
            FocusStatus.Text = "Paused by Stop; Resume is explicit.";
        }
        UpdateCaptureIndicator();
        SetFace(new FaceInputs(FocusPaused: _focus is { Snapshot.Status: Boo.Core.FocusStatus.Paused }));
    }

    public void DisposeShell()
    {
        _closingForExit = true;
        StopAll();
        DisposeHoldToTalk();
        _focusTimer.Stop();
        _focusCaptureTimer.Stop();
        _focusCancellation.Cancel();
        foreach (var annotation in _annotationWindows.ToArray())
        {
            annotation.Close();
        }
        _annotationWindows.Clear();
        _audio?.Dispose();
        _targets?.Dispose();
        _http?.Dispose();
        _capture?.Dispose(); // releases the cached Direct3D capture device
        _capture = null;
        var realtimeToDispose = _realtime;
        _realtime = null;
        if (realtimeToDispose is not null)
        {
            _ = realtimeToDispose.DisposeAsync();
        }
        _coordinator.Dispose();
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyTextScale();
        PrepareInitialPlacement();
        InputBox.Focus();
    }

    private void Window_OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _displayAffinitySupported = SetWindowDisplayAffinity(handle, App.CaptureExclusion.DisplayAffinity) && _captureExclusion(); // WDA_EXCLUDEFROMCAPTURE unless BOO_QA_CAPTURABLE=1
        if (!_displayAffinitySupported)
        {
            StatusText.Text = "Capture paused: Boo could not confirm UI exclusion.";
        }
        if (App.CaptureExclusion.Warning is { } qaWarning) { StatusText.Text = qaWarning; Title += " [QA: capturable]"; }
    }

    private void SystemParameters_OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            ApplyAppearanceToHeader();
        }
    }

    private void ApplyAppearanceToHeader()
    {
        if (SystemParameters.HighContrast)
        {
            HeaderBody.Background = MediaSystemColors.WindowBrush;
            HeaderLeftEye.Fill = MediaSystemColors.WindowTextBrush;
            HeaderRightEye.Fill = MediaSystemColors.WindowTextBrush;
            return;
        }

        HeaderBody.Background = ToBrush(_appearance.BodyColor, System.Windows.Media.Brushes.Black);
        HeaderLeftEye.Fill = ToBrush(_appearance.EyeColor, System.Windows.Media.Brushes.White);
        HeaderRightEye.Fill = ToBrush(_appearance.EyeColor, System.Windows.Media.Brushes.White);
    }

    private static System.Windows.Media.Brush ToBrush(string value, System.Windows.Media.Brush fallback)
    {
        try
        {
            var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!;
            brush.Freeze();
            return brush;
        }
        catch (Exception) when (value is not null)
        {
            return fallback;
        }
    }

    private void LoadSettings()
    {
        var settingsPath = Path.Combine(_dataDirectory, "settings.json");
        try
        {
            _settingsStore = new SettingsStore(settingsPath);
            _settings = _settingsStore.Load();
        }
        catch (InvalidDataException)
        {
            // SettingsStore reports an unreadable/corrupt file this way. InvalidDataException is not an
            // IOException, so without this catch a corrupt settings.json crashed Boo at startup.
            _settings = new AppSettings();
            StatusText.Text = "Saved settings could not be read; using safe defaults. Save settings to replace them.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _settings = new AppSettings();
            StatusText.Text = "Local settings are not available yet; using safe defaults.";
        }

        OffsetSlider.Value = _settings.VerticalOffset;
        ReducedMotionBox.IsChecked = _settings.ReducedMotion;
        SpeechMute.IsChecked = !_settings.SpeechEnabled;
        ChirpMute.IsChecked = !_settings.ChirpsEnabled;
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        FileRootBox.Text = _settings.FileRoot ?? string.Empty;
        LoadProviderSettings();
        LoadMicrophones();
        LoadUpdateSettings();
    }

    private void LoadProviderSettings()
    {
        var provider = _settings.Provider ?? new ProviderSettings();
        // Combo items are in AiProvider enum order (OpenAI, Anthropic, Gemini).
        TextProviderBox.SelectedIndex = Math.Clamp((int)provider.TextProvider, 0, Math.Max(0, TextProviderBox.Items.Count - 1));
        AnthropicModelBox.Text = provider.AnthropicModel;
        AnthropicFocusModelBox.Text = provider.AnthropicFocusModel;
        GeminiModelBox.Text = provider.GeminiModel;
        GeminiFocusModelBox.Text = provider.GeminiFocusModel;
    }

    /// <summary>The protected key file for one provider; OpenAI keeps its historical credential.bin.</summary>
    private CredentialStore StoreFor(AiProvider provider)
    {
        if (!_credentialStores.TryGetValue(provider, out var store))
        {
            store = new CredentialStore(Path.Combine(_dataDirectory, AiProviders.CredentialFileName(provider)));
            _credentialStores[provider] = store;
        }
        return store;
    }

    private void LoadAppearance()
    {
        try
        {
            _appearanceStore = new AppearanceStore(Path.Combine(_dataDirectory, "appearance.json"));
            _appearance = _appearanceStore.Load();
            AppearanceValidationText.Text = string.Empty;
            AppearanceValidationText.Visibility = Visibility.Collapsed;
        }
        catch (InvalidDataException)
        {
            _appearance = new NotchAppearance();
            AppearanceValidationText.Text = "Saved appearance is corrupt or invalid; using default colors until it is replaced.";
            AppearanceValidationText.Visibility = Visibility.Visible;
        }

        BodyColorBox.Text = _appearance.BodyColor;
        EyeColorBox.Text = _appearance.EyeColor;
        ApplyAppearanceToHeader();
    }

    private void LoadMicrophones()
    {
        MicrophoneBox.Items.Clear();
        MicrophoneBox.Items.Add("Default microphone");
        try
        {
            using var audio = new AudioSession();
            foreach (var device in audio.InputDevices())
            {
                MicrophoneBox.Items.Add(device);
            }
        }
        catch (InvalidOperationException)
        {
            // The default device remains selectable; an actual start failure
            // is surfaced by the explicit Talk action.
        }
        MicrophoneBox.SelectedIndex = Math.Clamp(_settings.MicrophoneDevice + 1, 0, Math.Max(0, MicrophoneBox.Items.Count - 1));
    }

    private void LoadDisplays()
    {
        DisplayBox.Items.Clear();
        _displays.Clear();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var bounds = screen.Bounds;
            var info = new DisplayInfo(
                screen.DeviceName,
                screen.Primary ? "Primary display" : screen.DeviceName,
                new RectD(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                LiveMonitors.ScaleFor(screen));
            _displays[info.Id] = info;
            DisplayBox.Items.Add($"{info.Name} · {bounds.Width}×{bounds.Height}");
        }

        // Saved display unplugged -> the primary (not merely the first enumerated monitor).
        var resolved = MonitorLayout.Resolve(LiveMonitors.Snapshot(), _settings.DisplayId);
        _selectedDisplay = resolved is not null && _displays.TryGetValue(resolved.Display.Id, out var saved)
            ? saved
            : _displays.Values.FirstOrDefault();
        if (_selectedDisplay is not null)
        {
            DisplayBox.SelectedIndex = Math.Max(0, _displays.Keys.ToList().IndexOf(_selectedDisplay.Id));
            UpdateCaptureIndicator();
        }
    }

    private void DisplayBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingDisplaySelection)
        {
            return;
        }
        if (DisplayBox.SelectedIndex < 0 || DisplayBox.SelectedIndex >= _displays.Count)
        {
            return;
        }
        _selectedDisplay = _displays.Values.ElementAt(DisplayBox.SelectedIndex);
        ClearAnnotations();
        UpdateCaptureIndicator();
        _onDisplayChanged(_selectedDisplay);
    }

    private void PositionNearDisplay()
    {
        if (_selectedDisplay is null)
        {
            return;
        }
        var screen = Forms.Screen.AllScreens.FirstOrDefault(s =>
            string.Equals(s.DeviceName, _selectedDisplay.Id, StringComparison.OrdinalIgnoreCase))
            ?? Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return;
        }

        var work = screen.WorkingArea;
        // The target monitor's own DPI (the panel may be leaving a monitor with another scale).
        var scale = MonitorLayout.SanitizeScale(LiveMonitors.ScaleFor(screen));
        var maxWidthDip = work.Width / scale;
        var maxHeightDip = work.Height / scale;
        if (double.IsFinite(maxWidthDip) && maxWidthDip > 0)
        {
            if (maxWidthDip < MinWidth)
            {
                MinWidth = maxWidthDip;
            }
            Width = Math.Min(Width, maxWidthDip);
        }
        if (double.IsFinite(maxHeightDip) && maxHeightDip > 0)
        {
            if (maxHeightDip < MinHeight)
            {
                MinHeight = maxHeightDip;
            }
            Height = Math.Min(Height, maxHeightDip);
        }

        var placement = MonitorLayout.CenterInWorkArea(
            new RectD(work.Left, work.Top, work.Width, work.Height), Width, Height, scale).PhysicalBounds;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || WindowState == WindowState.Maximized)
        {
            Left = placement.X / scale;
            Top = placement.Y / scale;
            return;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var dpiBefore = GetDpiForWindow(handle);
            SetWindowPos(handle, IntPtr.Zero,
                (int)placement.X,
                (int)placement.Y,
                (int)placement.Width,
                (int)placement.Height,
                SwpNoActivate | SwpNoZOrder);
            // Crossing into a monitor with another DPI lets WPF apply the WM_DPICHANGED
            // suggested rect, which can shift the panel; place it once more on arrival.
            if (GetDpiForWindow(handle) == dpiBefore)
            {
                break;
            }
        }
    }

    public void RefreshSelectedDisplay()
    {
        if (_refreshingDisplay || _selectedDisplay is null)
        {
            return;
        }

        _refreshingDisplay = true;
        try
        {
            var screen = Forms.Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, _selectedDisplay.Id, StringComparison.OrdinalIgnoreCase));
            if (screen is null)
            {
                StopAll();
                var liveScreens = Forms.Screen.AllScreens;
                var fallback = liveScreens.FirstOrDefault(candidate => candidate.Primary)
                    ?? liveScreens.FirstOrDefault();
                if (fallback is null)
                {
                    _selectedDisplay = null;
                    _settings = _settings with { DisplayId = null };
                    DisplayBox.Items.Clear();
                    _displays.Clear();
                    UpdateCaptureIndicator();
                    _onDisplayChanged(null);
                    return;
                }

                RebuildDisplaySelection(liveScreens, fallback);
                _onDisplayChanged(_selectedDisplay);
                return;
            }

            // The monitor's own DPI: the panel window may be on another monitor (or hidden),
            // so its window DPI says nothing about the selected display's scale.
            var refreshed = _selectedDisplay with
            {
                PixelBounds = new RectD(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height),
                Scale = MonitorLayout.SanitizeScale(LiveMonitors.ScaleFor(screen))
            };
            if (refreshed == _selectedDisplay)
            {
                return;
            }

            _selectedDisplay = refreshed;
            _displays[refreshed.Id] = refreshed;
            UpdateCaptureIndicator();
            _onDisplayChanged(refreshed);
        }
        finally
        {
            _refreshingDisplay = false;
        }
    }

    private void RebuildDisplaySelection(IReadOnlyList<Forms.Screen> liveScreens, Forms.Screen fallback)
    {
        DisplayBox.Items.Clear();
        _displays.Clear();
        foreach (var screen in liveScreens)
        {
            var bounds = screen.Bounds;
            var info = new DisplayInfo(
                screen.DeviceName,
                screen.Primary ? "Primary display" : screen.DeviceName,
                new RectD(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
                LiveMonitors.ScaleFor(screen));
            _displays[info.Id] = info;
            DisplayBox.Items.Add($"{info.Name} · {bounds.Width}×{bounds.Height}");
        }

        if (!_displays.TryGetValue(fallback.DeviceName, out var selected))
        {
            return;
        }

        _selectedDisplay = selected;
        _settings = _settings with { DisplayId = selected.Id };
        _synchronizingDisplaySelection = true;
        try
        {
            DisplayBox.SelectedIndex = _displays.Keys.ToList().IndexOf(selected.Id);
        }
        finally
        {
            _synchronizingDisplaySelection = false;
        }

        try
        {
            (_settingsStore ??= new SettingsStore(Path.Combine(_dataDirectory, "settings.json"))).Save(_settings);
        }
        catch (Exception)
        {
            StatusText.Text = DisplaySelectionPersistenceMessage;
        }

        UpdateCaptureIndicator();
    }

    private void UpdateCaptureIndicator()
    {
        var displays = _captureActivities.ActiveDisplays;
        var active = displays.Count > 0;
        var name = active
            ? string.Join(", ", displays.Select(display => display.Name))
            : _selectedDisplay?.Name ?? "display not selected";
        CaptureIndicator.Text = active ? $"Screen sharing on · {name}" : $"Screen sharing off · {name}";
        _onCaptureActivity(active, active ? name : null);
    }

    private void EndCaptureActivity(CaptureActivityOperation activity)
    {
        activity.Dispose();
        UpdateCaptureIndicator();
    }

    private void InputBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SendButton_OnClick(sender, e);
        }
    }

    private async void SendButton_OnClick(object sender, RoutedEventArgs e)
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0)
        {
            StatusText.Text = "Type a question first.";
            InputBox.Focus();
            return;
        }

        if (_listening)
        {
            StopListening();
        }
        var lease = BeginRequest();
        SetFace(_faceInputs with { Thinking = true, Responding = false, Caption = null });
        AppendTranscript("You", text);
        InputBox.Clear();
        StatusText.Text = "Thinking about the selected context…";
        var activity = _captureActivities.CreateOperation();
        var handedToRealtime = false;
        try
        {
            var frame = await CaptureForRequestAsync(lease, activity);
            if (frame is null)
            {
                EndCaptureActivity(activity);
            }
            if (!IsCurrent(lease.Generation))
            {
                return;
            }
            var intent = RequestIntent.Classify(text);
            if (intent == RequestKind.Undo)
            {
                await HandleUndoRequestAsync(frame, lease);
                return;
            }
            var speechEnabled = SpeechMute.IsChecked != true && _settings.SpeechEnabled;
            if (speechEnabled && !_realtimeConnected)
            {
                try
                {
                    var realtime = EnsureRealtime();
                    await realtime.ConnectAsync(lease.Token);
                    if (!IsCurrent(lease.Generation) || !ReferenceEquals(_realtime, realtime))
                    {
                        return;
                    }
                    _realtimeConnected = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
                {
                    if (!IsCurrent(lease.Generation))
                    {
                        return;
                    }
                    _realtimeConnected = false;
                    var failedRealtime = _realtime;
                    _realtime = null;
                    if (failedRealtime is not null)
                    {
                        _ = InterruptRealtimeAsync(failedRealtime);
                    }
                }
            }
            if (speechEnabled && _realtimeConnected && _realtime is not null)
            {
                _voiceTaskAuthorized = intent == RequestKind.Task;
                await _realtime.SendTextAsync(text, lease.Token);
                StatusText.Text = "Sent to the voice conversation; waiting for the response…";
                // The realtime reply (audio delta / response.done / error) clears Thinking.
                handedToRealtime = IsCurrent(lease.Generation);
                return;
            }

            var explicitTask = intent == RequestKind.Task;
            ControlObservation? controls = null;
            if (explicitTask && frame is not null)
            {
                controls = await ReadRequestedControlsAsync(frame, lease);
                if (controls is null)
                {
                    return;
                }
            }

            var client = EnsureResponses();
            var request = new AssistantRequest(
                text,
                lease.Generation,
                frame,
                Research: intent == RequestKind.Research,
                AllowActions: explicitTask);
            var reply = await client.RespondAsync(request, lease.Token, controls);
            if (!IsCurrent(lease.Generation))
            {
                return;
            }
            RenderReply(reply, lease.Generation, frame, request.AllowActions, controls, request.Research);
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(lease.Generation))
            {
                StatusText.Text = ResponseInterruptedMessage;
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(lease.Generation))
            {
                StatusText.Text = SanitizedError(ex, "The provider is unavailable; typing remains ready.");
            }
        }
        finally
        {
            EndCaptureActivity(activity);
            if (!handedToRealtime && IsCurrent(lease.Generation))
            {
                SetFace(_faceInputs with { Thinking = false });
            }
        }
    }

    private async void TalkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_listening)
        {
            StopListening();
            return;
        }

        var lease = BeginRequest();
        try
        {
            var realtime = EnsureRealtime();
            await realtime.ConnectAsync(lease.Token);
            if (!IsCurrent(lease.Generation))
            {
                return;
            }
            _realtimeConnected = true;
            _voiceTaskAuthorized = false;
            var uploads = new AudioUploadQueue(realtime.SendAudioAsync, lease.Token);
            _audioUploads = uploads;
            uploads.Failed += message => PostAudioFailure(message, lease.Generation, uploads);
            _audio?.Dispose();
            _audio = CreateAudio(uploads, lease.Generation);
            if (!_audio.TryStart(_settings.MicrophoneDevice, out var micFailure))
            {
                StopListening();
                ListeningStatus.Text = "Microphone unavailable. You can still type a request.";
                StatusText.Text = micFailure;
                return;
            }
            _listening = true;
            _onMicrophoneActivity(true);
            TalkButton.Content = "Stop talk";
            ListeningStatus.Text = "Listening… press Stop talk or the global Stop to interrupt.";
            StatusText.Text = "Microphone active by explicit request.";
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(lease.Generation))
            {
                StopListening();
                _onMicrophoneActivity(false);
                ListeningStatus.Text = "Listening stopped.";
            }
        }
        catch (Exception ex)
        {
            if (IsCurrent(lease.Generation))
            {
                StopListening();
                ListeningStatus.Text = "Microphone unavailable. You can still type a request.";
                StatusText.Text = SanitizedError(ex, "Microphone or realtime setup failed.");
            }
        }
    }

    private void StopListening()
    {
        var hadVoiceSession = _listening || _realtimeConnected || _audioUploads is not null || _realtime is not null;
        DiscardPushToTalkUplink();
        StopAudioUploads();
        try
        {
            _audio?.Stop();
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            StatusText.Text = SanitizedError(ex, "Microphone stopped");
        }
        _listening = false;
        _realtimeConnected = false;
        _voiceTaskAuthorized = false;
        _onMicrophoneActivity(false);
        if (hadVoiceSession)
        {
            _coordinator.Stop();
            _requestCancellation.Cancel();
            _requestLease = null;
            _generation++;
            ClearActionPreview();
            _pendingRealtimeCallId = null;
            ClearAnnotations();
        }
        var realtimeToStop = _realtime;
        _realtime = null;
        if (realtimeToStop is not null)
        {
            _ = InterruptRealtimeAsync(realtimeToStop);
        }
        TalkButton.Content = "Talk";
        ListeningStatus.Text = "Microphone idle. Activate Talk when you want to speak.";
        // Errors, disconnects and superseded requests (BeginRequest) end here; never strand Thinking/Responding.
        _realtimeToolsInFlight = 0;
        SetFace(_faceInputs with { Thinking = false, Responding = false });
    }

    private void RenderReply(AssistantReply reply, long generation, CapturedFrame? frame, bool explicitTask, ControlObservation? controls, bool research = false)
    {
        ClearAnnotations();
        SetFace(_faceInputs with { Thinking = false, Responding = true, Caption = reply.Text }, TimeSpan.FromSeconds(4), FaceMomentKind.Responding);
        AppendTranscript("Boo", reply.Text);
        RenderSources(reply, research);

        if (reply.Annotations is not null && frame is not null)
        {
            TryRenderAnnotations(reply.Annotations, frame);
        }
        else if (frame is not null)
        {
            ReportAnnotationOutcome("Boo explained the screen without a verified mark.");
        }

        if (!IsCurrent(generation) || UndoOwnsPendingActions)
        {
            return;
        }

        var action = explicitTask ? reply.Actions.FirstOrDefault() : null;
        _pendingControls = action is null ? null : controls;
        if (action is not null)
        {
            action = action with { Generation = generation };
            _pendingActions.Clear();
            foreach (var remaining in reply.Actions.Skip(1))
            {
                _pendingActions.Enqueue(remaining with { Generation = generation });
            }
            var authority = new TaskAuthority(
                generation,
                ExplicitUserTask: true,
                new HashSet<string>(ActionPolicy.ToolNames, StringComparer.OrdinalIgnoreCase),
                _settings.FileRoot);
            ShowActionPreview(action, authority, frame?.Context.Target ?? TryGetTarget(),
                _pendingActions.Count == 0 ? string.Empty : $"Step 1 of {reply.Actions.Count}: ");
        }
        else
        {
            _pendingActions.Clear();
            if (reply.Actions.Count > 0 && !explicitTask)
            {
                StatusText.Text = "Boo suggested an action, but no explicit task request authorized it.";
            }
            if (reply.Actions.Count == 0)
            {
                StatusText.Text = "Reply complete. No action was committed.";
            }
        }
    }

    private void TryRenderAnnotations(AnnotationProposal proposal, CapturedFrame frame)
    {
        try
        {
            var validation = AnnotationValidator.Validate(proposal, frame.Context, _timeProvider.GetUtcNow());
            if (!validation.Valid)
            {
                ReportAnnotationOutcome($"Boo explained the screen without drawing: {validation.Reason}");
                return;
            }

            var window = new AnnotationWindow(frame.Context, proposal.Marks)
            {
                ReducedMotion = AccessibilitySettings.ReducedMotion(ReducedMotionEnabled)
            };
            new WindowInteropHelper(window).EnsureHandle();
            if (!window.DisplayAvailable)
            {
                window.Close();
                ReportAnnotationOutcome("Boo explained the screen without drawing: the display changed. Ask again to see marks.");
                return;
            }
            if (!window.CaptureExclusionAvailable)
            {
                window.Close();
                _displayAffinitySupported = false;
                StopAll();
                ReportAnnotationOutcome("Capture paused: Windows could not exclude Boo's annotation window. No mark was shown.");
                return;
            }
            window.Show();
            _annotationWindows.Add(window);
            ReportAnnotationOutcome("Screen explanation is shown. Marks are click-through and tied to this observation.");
        }
        catch (Exception ex)
        {
            ReportAnnotationOutcome(SanitizedError(ex, "Boo explained the screen but could not draw a verified mark."));
        }
    }

    /// <summary>
    /// U6: word the nudge and, when the model names a visible phrase, OCR the last focus
    /// frame (memory only), match it outside Boo's own windows, and fly the pointer + ring
    /// there. Any miss or failure leaves the text-only nudge.
    /// </summary>
    private async Task ShowNudgePointerAsync(CapturedFrame frame, string task, CancellationToken token)
    {
        // A talk/explain turn or Stop bumps _generation; a pointer from this older
        // focus frame must never clear or cover that newer turn's marks.
        var generation = _generation;
        try
        {
            var nudge = await EnsureResponses().NudgeAsync(frame, task, token);
            if (token.IsCancellationRequested || _focus is not { ShouldMonitor: true } || _generation != generation)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(nudge.Text))
            {
                FocusStatus.Text = $"Nudge: {nudge.Text}";
            }
            if (string.IsNullOrWhiteSpace(nudge.PointerTarget) || !_displayAffinitySupported)
            {
                return;
            }

            var words = await OcrReader.ReadAsync(frame, token);
            var match = OcrNudgeTargetMatcher.Match(words, nudge.PointerTarget, OwnedWindowRects());
            if (match is null || token.IsCancellationRequested || _focus is not { ShouldMonitor: true }
                || _generation != generation || !Equals(frame.Context.Display, _selectedDisplay)
                || GetForegroundWindow() != new IntPtr(frame.Context.Target.Handle))
            {
                // The OCR boxes describe the captured window; if the user has switched
                // away since, the ring would land on unrelated content.
                return;
            }

            ClearAnnotations();
            var window = AnnotationWindow.ForPhysicalTarget(frame.Context, match.Bounds);
            window.ReducedMotion = AccessibilitySettings.ReducedMotion(ReducedMotionEnabled);
            new WindowInteropHelper(window).EnsureHandle();
            if (!window.DisplayAvailable || !window.CaptureExclusionAvailable)
            {
                window.Close();
                return;
            }
            window.Show();
            _annotationWindows.Add(window);
        }
        catch (Exception)
        {
            // Pointer guidance is optional: the text nudge already showed.
        }
    }

    private List<RectD> OwnedWindowRects()
    {
        var rects = new List<RectD>();
        foreach (var handle in OwnedWindowHandles())
        {
            if (GetWindowRect(new IntPtr(handle), out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                rects.Add(new RectD(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
            }
        }
        return rects;
    }

    private void ReportAnnotationOutcome(string message)
    {
        AnnotationDetail.Text = message;
        AnnotationDetail.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private void ShowActionPreview(ToolRequest action, TaskAuthority authority, WindowIdentity? target, string prefix)
    {
        var preview = ActionPreviewBuilder.Create(action, authority, target, Environment.ProcessId);
        _pendingPreview = preview;
        _pendingAction = preview.Request;
        _pendingAuthority = authority;
        _pendingTarget = target;
        ActionPreview.DataContext = preview;
        ActionPreviewText.Text = prefix + preview.Title;
        ActionBlockedReason.Text = preview.BlockedReason ?? string.Empty;
        ActionBlockedReason.Visibility = preview.CanApprove ? Visibility.Collapsed : Visibility.Visible;
        ApproveButton.IsEnabled = preview.CanApprove && !_approvalInFlight;
        ActionPreview.Visibility = Visibility.Visible;
        StatusText.Text = preview.CanApprove
            ? "Review the exact target and values before Boo can act."
            : "This action cannot run. Review the reason or cancel it.";

        var requestLease = _requestLease;
        var generation = _generation;
        var currentFocus = Keyboard.FocusedElement;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!ReferenceEquals(_pendingPreview, preview)
                || requestLease is null
                || !ReferenceEquals(_requestLease, requestLease)
                || _generation != generation
                || !IsCurrent(generation)
                || requestLease.Generation != generation
                || _requestLease?.Generation != generation
                || authority.Generation != generation
                || _pendingAuthority is null
                || _pendingAuthority.Generation != authority.Generation
                || _closingForExit
                || !IsVisible
                || !IsActive
                || ActionPreview.Visibility != Visibility.Visible
                || _approvalInFlight
                || !ReferenceEquals(Keyboard.FocusedElement, currentFocus))
            {
                return;
            }

            ActionPreview.BringIntoView();
            CancelButton.Focus();
        }));
    }

    private async void ApproveButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_approvalInFlight || _pendingPreview is not { CanApprove: true } preview
            || _pendingAction is null || _pendingAuthority is null)
        {
            return;
        }

        var action = preview.Request;
        var authority = _pendingAuthority;
        var lease = _requestLease;
        var keepPreview = false;
        if (lease is null || !IsCurrent(lease.Generation))
        {
            StatusText.Text = "This approval expired; request a fresh action.";
            CancelPendingAction();
            return;
        }

        _approvalInFlight = true;
        ApproveButton.IsEnabled = false;
        var committedBeforeFailure = false;
        try
        {
            var decision = ActionPolicy.Evaluate(action, authority);
            if (decision.Verdict == PermissionVerdict.Denied)
            {
                StatusText.Text = $"Action denied: {decision.Reason}";
                CancelPendingAction();
                return;
            }

            var approval = ActionPolicy.Approve(action);
            var target = _pendingTarget;
            var needsWindow = action.Name is "click" or "type_text" or "inspect";
            if (_pendingControls is { } controls && (controls.Context.Generation != lease.Generation
                || (needsWindow && controls.Context.Target != target)))
            {
                StatusText.Text = "Approval expired because its screen controls belong to a different request or window.";
                CancelPendingAction();
                return;
            }
            if (needsWindow && target is null)
            {
                StatusText.Text = "Approval expired because the original target is no longer available.";
                CancelPendingAction();
                return;
            }
            var currentTarget = needsWindow ? TryGetTarget() : null;
            if (needsWindow && (currentTarget is null || currentTarget.Handle != target!.Handle
                || currentTarget.ProcessId != target.ProcessId
                || !string.Equals(currentTarget.ProcessName, target.ProcessName, StringComparison.Ordinal)
                || currentTarget.Bounds != target.Bounds))
            {
                StatusText.Text = "Approval expired because the target changed; no action was committed.";
                CancelPendingAction();
                return;
            }
            var preActionControls = _pendingControls;
            var fileSnapshot = SnapshotFileForUndo(action, authority);
            var outcome = await EnsureDesktop().ExecuteAsync(action, authority, approval, target, lease.Token);
            committedBeforeFailure = outcome.Committed;
            outcome = await ReadBackActionAsync(action, outcome, preActionControls, target, lease);
            if (outcome.Success && outcome.Committed)
            {
                RecordFileEdit(action, fileSnapshot);
            }
            if (!IsCurrent(lease.Generation))
            {
                StatusText.Text = outcome.Committed
                    ? $"Stopped after a partial effect was committed: {outcome.Message}"
                    : "Stopped; no action was committed.";
                await CompletePendingRealtimeToolAsync(outcome.Committed
                    ? $"Partial outcome before stop: {outcome.Message}"
                    : "Stopped before commit; no effect occurred.");
                return;
            }

            StatusText.Text = outcome.Success && outcome.Committed
                ? $"Completed after verification: {outcome.Message}"
                : $"Not completed: {outcome.Message}";
            if (outcome.Success && outcome.Committed && _pendingActions.Count > 0)
            {
                var next = _pendingActions.Dequeue();
                ShowActionPreview(next, authority, TryGetTarget(), "Next step: ");
                keepPreview = true;
            }
            else
            {
                await CompletePendingRealtimeToolAsync(outcome.Success && outcome.Committed
                    ? $"Verified outcome: {outcome.Message}"
                    : $"Action not completed: {outcome.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = committedBeforeFailure
                ? "Stopped after the action committed; its result was not verified."
                : "Stopped before the action committed.";
            await CompletePendingRealtimeToolAsync(committedBeforeFailure
                ? "The action committed, then stopped before its result was verified."
                : "Action stopped before commit; no later effect occurred.");
        }
        catch (Exception ex)
        {
            StatusText.Text = committedBeforeFailure
                ? "The action committed, but Boo could not verify its result."
                : SanitizedError(ex, "The action could not be verified.");
            await CompletePendingRealtimeToolAsync(committedBeforeFailure
                ? "The action committed, but its result could not be verified."
                : "Action failed before a verified commit.");
        }
        finally
        {
            _approvalInFlight = false;
            if (!keepPreview && ReferenceEquals(_pendingPreview, preview))
            {
                CancelPendingAction();
            }
            ApproveButton.IsEnabled = _pendingPreview?.CanApprove == true;
        }
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e) => CancelPendingAction();

    private void CancelPendingAction()
    {
        ClearActionPreview();
        _ = CompletePendingRealtimeToolAsync("Action cancelled; no side effect was requested.");
        if (StatusText.Text.StartsWith("Review", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "Action cancelled; no side effect was requested.";
        }
    }

    private void ClearActionPreview()
    {
        ActionPreview.Visibility = Visibility.Collapsed;
        ActionPreview.DataContext = null;
        ActionPreviewText.Text = string.Empty;
        ActionBlockedReason.Text = string.Empty;
        ApproveButton.IsEnabled = false;
        _pendingPreview = null;
        _pendingAction = null;
        _pendingAuthority = null;
        _pendingTarget = null;
        _pendingControls = null;
        _pendingActions.Clear();
    }

    private void FocusStartButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(FocusTaskBox.Text))
        {
            FocusStatus.Text = "Name the focus task first.";
            return;
        }
        if (!double.TryParse(FocusDurationBox.Text, out var minutes) || minutes <= 0)
        {
            FocusStatus.Text = "Enter a positive duration in minutes.";
            return;
        }

        try
        {
            _focusCancellation.Cancel();
            _focusCancellation.Dispose();
            _focusCancellation = new CancellationTokenSource();
            if (_focus is { Status: not Boo.Core.FocusStatus.Ended } superseded)
            {
                // Start while a session is live replaces it explicitly: settle it as ended and save it, so it is not
                // left Running in sessions.db and reported as "interrupted when Boo closed" at the next start.
                // No happy face or chirp: those belong to the session the user finishes.
                superseded.SessionEnded -= Focus_OnSessionEnded;
                superseded.EndQuietly();
                SaveFocusSnapshot(superseded);
            }
            _focus = new FocusSessionController(FocusTaskBox.Text.Trim(), Math.Clamp(minutes * 60d, 60d, 14400d),
                DeveloperModeBox.IsChecked == true, _timeProvider);
            _focus.SessionEnded += Focus_OnSessionEnded;
            _focusCaptureInFlight = false;
            _lastFocusSaveRealElapsed = 0;
            _focusClock.Start();
            _focusTimer.Start();
            _focusCaptureTimer.Start();
            SaveFocusSnapshot();
            UpdateFocusView();
            FocusStatus.Text = "Running; monitoring pauses on Stop, lock, sleep, or Pause.";
            SetFace(_faceInputs with { FocusPaused = false, Happy = false });
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Focus session is not available yet.");
        }
    }

    private void FocusPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_focus is null)
        {
            return;
        }
        try
        {
            _focus.Pause("User pause");
            _focusTimer.Stop();
            StopFocusMonitoring();
            SaveFocusSnapshot();
            UpdateFocusView();
            FocusStatus.Text = "Paused. Resume is explicit; unlock/reconnect will not resume it.";
            SetFace(_faceInputs with { FocusPaused = true });
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Could not pause focus.");
        }
    }

    /// <summary>Nudge pills on the notch: "Back to it" dismisses the nudge; "5 min break" pauses focus (resume stays explicit).</summary>
    public void HandleNudgeAction(bool takeBreak)
    {
        SetFace(_faceInputs with { NudgeShowing = false });
        if (takeBreak && _focus is { Status: Boo.Core.FocusStatus.Running })
        {
            FocusPauseButton_OnClick(this, new RoutedEventArgs());
            FocusStatus.Text = "On a 5 minute break. Press Resume when you are back.";
        }
    }

    private void FocusResumeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_focus is null)
        {
            return;
        }
        try
        {
            if (_focus.Status != Boo.Core.FocusStatus.Paused)
            {
                return;
            }
            _focus.Resume();
            _focusCancellation.Dispose();
            _focusCancellation = new CancellationTokenSource();
            _focusCaptureTimer.Start();
            _focusClock.Start();
            _focusTimer.Start();
            UpdateFocusView();
            FocusStatus.Text = "Running after explicit Resume.";
            SetFace(_faceInputs with { FocusPaused = false });
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Could not resume focus.");
        }
    }

    private void FocusEndButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_focus is null)
        {
            return;
        }
        try
        {
            _focus.End(); // Focus_OnSessionEnded stops, saves and updates the view.
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Could not end focus.");
        }
    }

    private void FocusTimer_OnTick(object? sender, EventArgs e)
    {
        if (_focus is null)
        {
            return;
        }
        try
        {
            // Count real monotonic time, not one second per tick: DispatcherTimer drifts and can be starved.
            var tick = _focusClock.Tick();
            if (tick.GapDetected && _focus.ShouldMonitor)
            {
                // Sleep or a long hang that no suspend event reported: do not count it, and pause.
                _focus.Pause(FocusClock.GapPauseReason);
                _focusTimer.Stop();
                StopFocusMonitoring();
                SaveFocusSnapshot();
                UpdateFocusView();
                FocusStatus.Text = "Paused after sleep. Press Resume to continue.";
                SetFace(_faceInputs with { FocusPaused = true });
                return;
            }
            _focus.Tick(tick.Seconds); // Focus_OnSessionEnded stops, saves and updates on completion.
            UpdateFocusView();
            if (_focus.Snapshot.Status != Boo.Core.FocusStatus.Running)
            {
                _focusClock.Stop();
                _focusTimer.Stop();
            }
            else if (FocusRecovery.CheckpointDue(_lastFocusSaveRealElapsed, _focus.Snapshot.RealElapsed))
            {
                SaveFocusSnapshot(); // Bounds progress lost if Boo is killed mid-session.
            }
        }
        catch (Exception ex)
        {
            // Settle the session as Paused rather than leaving it Running: stopping only the tick timer kept the
            // periodic capture checks firing on a session whose time no longer advanced, with Resume disabled.
            _focusClock.Stop();
            _focusTimer.Stop();
            StopFocusMonitoring();
            try
            {
                if (_focus is { Status: Boo.Core.FocusStatus.Running })
                {
                    _focus.Pause("Focus monitoring stopped");
                }
                SaveFocusSnapshot();
                UpdateFocusView();
                SetFace(_faceInputs with { FocusPaused = true });
            }
            catch (Exception)
            {
                // The original failure is the one worth reporting.
            }
            FocusStatus.Text = SanitizedError(ex, "Focus monitoring stopped safely.");
        }
    }

    private void FocusCaptureTimer_OnTick(object? sender, EventArgs e)
    {
        if (_focus is null || !_focus.IsCheckDue || _focusCaptureInFlight)
        {
            return;
        }
        _ = RunFocusCheckAsync();
    }

    private async Task RunFocusCheckAsync()
    {
        if (_focus is null || !_focus.ShouldMonitor || _focusCaptureInFlight)
        {
            return;
        }

        _focusCaptureInFlight = true;
        var captureId = ++_focusCaptureId;
        var token = _focusCancellation.Token;
        var activity = _captureActivities.CreateOperation();
        try
        {
            var snapshot = _focus.Snapshot;
            var leaseGeneration = _generation;
            var frame = await CaptureFocusFrameAsync(leaseGeneration, token, activity);
            if (frame is null || token.IsCancellationRequested || _focus is null || !_focus.ShouldMonitor)
            {
                _focus?.SkipCheck();
                return;
            }

            var decision = await EnsureDecisions().ClassifyAsync(frame, snapshot, token);
            if (token.IsCancellationRequested || _focus is null || !_focus.ShouldMonitor)
            {
                return;
            }

            var check = new FocusCheck(
                snapshot.Elapsed,
                decision.Activity,
                decision.Probability,
                decision.Confidence,
                decision.Probabilities);
            var age = Math.Max(0, (_timeProvider.GetUtcNow() - frame.Context.CapturedAt).TotalSeconds);
            if (_focus.RecordCheck(check, age))
            {
                FocusStatus.Text = "Nudge: return to the task when you are ready. Boo did not change your apps.";
                var minutesLeft = Math.Max(1, (int)Math.Ceiling((snapshot.Duration - snapshot.Elapsed) / 60));
                var nudgeTask = string.IsNullOrWhiteSpace(snapshot.Task) ? "your task" : snapshot.Task.Trim();
                SetFace(_faceInputs with
                {
                    NudgeShowing = true,
                    Caption = $"Still on {nudgeTask}? {minutesLeft} minute{(minutesLeft == 1 ? "" : "s")} left in this focus block.",
                }, TimeSpan.FromSeconds(6), FaceMomentKind.Nudge);
                _ = ShowNudgePointerAsync(frame, snapshot.Task, token);
                _chirps.PlayNudge(_settings.ChirpsEnabled, _listening);
            }
            else
            {
                FocusStatus.Text = $"Observed {check.Activity.ToString().ToLowerInvariant()} · periodic estimate";
            }
            SaveFocusSnapshot();
        }
        catch (OperationCanceledException)
        {
            // Pause, Stop, lock, and sleep intentionally leave an honest gap.
        }
        catch (Exception ex)
        {
            if (_focus is { ShouldMonitor: true })
            {
                var snapshot = _focus.Snapshot;
                var unknown = new FocusCheck(snapshot.Elapsed, FocusActivity.Uncertain, 0, 0);
                _focus.RecordCheck(unknown, 0);
                SaveFocusSnapshot();
                FocusStatus.Text = SanitizedError(ex, "Focus observation unavailable; this interval is unknown.");
            }
        }
        finally
        {
            // A Pause/Resume or new Start may have begun a newer capture; only the current one clears the flag.
            if (captureId == _focusCaptureId)
            {
                _focusCaptureInFlight = false;
            }
            EndCaptureActivity(activity);
        }
    }

    private async Task<CapturedFrame?> CaptureFocusFrameAsync(long generation, CancellationToken token, CaptureActivityOperation activity)
    {
        var display = _selectedDisplay;
        if (display is null || !_displayAffinitySupported)
        {
            return null;
        }
        var target = EnsureTargets().Snapshot(display);
        if (target is null)
        {
            return null;
        }
        activity.Start(display);
        UpdateCaptureIndicator();
        await Dispatcher.Yield(DispatcherPriority.Background);
        token.ThrowIfCancellationRequested();
        if (_focus is not { ShouldMonitor: true } || !Equals(display, _selectedDisplay))
        {
            return null;
        }
        var frame = await (_capture ??= new ScreenCapture(_timeProvider)).CaptureAsync(
            display,
            target,
            OwnedWindowHandles(),
            generation,
            token);
        return !token.IsCancellationRequested && Equals(display, _selectedDisplay) ? frame : null;
    }

    private IFocusClassifier EnsureDecisions()
    {
        if (_decisions is not null)
        {
            return _decisions;
        }

        var providerSettings = _settings.Provider ?? new ProviderSettings();
        var provider = providerSettings.TextProvider;
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _decisions = AssistantProviders.CreateFocusClassifier(provider, _http, () => StoreFor(provider).Load(), providerSettings);
        return _decisions;
    }

    private void StopFocusMonitoring()
    {
        _focusCaptureTimer.Stop();
        _focusCancellation.Cancel();
        _focusCaptureInFlight = false;
        UpdateCaptureIndicator();
    }

    private void SaveFocusSnapshot(FocusSessionController? controller = null)
    {
        controller ??= _focus;
        if (controller is null || _sessionStore is null)
        {
            return;
        }
        try
        {
            var snapshot = controller.Snapshot;
            _sessionStore.Save(snapshot);
            _lastFocusSaveRealElapsed = snapshot.RealElapsed;
            LoadHistory();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _sessionStore = null;
            SetHistoryUnavailable();
        }
    }

    private void InitializeHistory()
    {
        try
        {
            _sessionStore = new SessionStore(Path.Combine(_dataDirectory, "sessions.db"));
            if (_sessionStore.RecoverInterrupted() > 0)
            {
                FocusStatus.Text = "Your last focus session was interrupted when Boo closed. It is saved as paused in history.";
            }
            LoadHistory();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _sessionStore = null;
            SetHistoryUnavailable();
        }
    }

    private void LoadHistory()
    {
        if (_sessionStore is null)
        {
            SetHistoryUnavailable();
            return;
        }
        try
        {
            var rows = _sessionStore.List()
                .Take(20)
                .Select(session => FocusTimeline.ToHistoryRow(session, HistoryBarWidth))
                .ToList();
            HistoryList.ItemsSource = rows;
            HistoryStatus.Text = rows.Count == 0
                ? "No saved sessions. Unknown gaps and pauses remain distinct when a session is recorded."
                : "Periodic observations are estimates; unknown gaps and pauses are not counted as exact active time.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or OverflowException)
        {
            _sessionStore = null;
            SetHistoryUnavailable();
        }
    }

    private void SetHistoryUnavailable()
    {
        HistoryList.ItemsSource = Array.Empty<FocusHistoryRow>();
        HistoryStatus.Text = HistoryUnavailableMessage;
    }

    private void RefreshHistoryButton_OnClick(object sender, RoutedEventArgs e) => LoadHistory();

    private void UpdateFocusView()
    {
        if (_focus is null)
        {
            FocusTimer.Text = "Not running";
            return;
        }
        var remaining = Math.Max(0, _focus.Remaining);
        FocusTimer.Text = TimeSpan.FromSeconds(remaining).ToString(@"mm\:ss");
    }

    private void SaveSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var nextAppearance = new NotchAppearance(BodyColorBox.Text.Trim(), EyeColorBox.Text.Trim());
            if (!TrySaveAppearance(nextAppearance))
            {
                return;
            }

            var previousProvider = _settings.Provider ?? new ProviderSettings();
            var nextProvider = previousProvider with
            {
                TextProvider = TextProviderBox.SelectedIndex >= 0 && TextProviderBox.SelectedIndex < AiProviders.All.Count
                    ? AiProviders.All[TextProviderBox.SelectedIndex]
                    : previousProvider.TextProvider,
                AnthropicModel = ModelIdOrDefault(AnthropicModelBox.Text, AiProviders.DefaultAnthropicModel),
                AnthropicFocusModel = ModelIdOrDefault(AnthropicFocusModelBox.Text, AiProviders.DefaultAnthropicFocusModel),
                GeminiModel = ModelIdOrDefault(GeminiModelBox.Text, AiProviders.DefaultGeminiModel),
                GeminiFocusModel = ModelIdOrDefault(GeminiFocusModelBox.Text, AiProviders.DefaultGeminiFocusModel)
            };
            if (!AiProviders.IsValidModelId(nextProvider.AnthropicModel) ||
                !AiProviders.IsValidModelId(nextProvider.AnthropicFocusModel) ||
                !AiProviders.IsValidModelId(nextProvider.GeminiModel) ||
                !AiProviders.IsValidModelId(nextProvider.GeminiFocusModel))
            {
                StatusText.Text = "Settings were not saved: a model id may only use letters, digits, '-', '.', '_' and ':'. Leave a model box blank for the default.";
                return;
            }

            // Read (and clear) the pasted GitHub token only once nothing below can return early, so a
            // rejected model id does not silently swallow a valid token.
            var (updateSettings, pendingUpdateToken, updateNote) = ReadUpdateSettingsFromUi();

            _settings = new AppSettings(
                DisplayId: _selectedDisplay?.Id,
                SpeechEnabled: SpeechMute.IsChecked != true,
                ChirpsEnabled: ChirpMute.IsChecked != true,
                ReducedMotion: ReducedMotionBox.IsChecked == true,
                VerticalOffset: OffsetSlider.Value,
                MicrophoneDevice: Math.Max(-1, MicrophoneBox.SelectedIndex - 1),
                FileRoot: string.IsNullOrWhiteSpace(FileRootBox.Text) ? null : Path.GetFullPath(FileRootBox.Text.Trim()),
                Provider: nextProvider,
                StartWithWindows: StartWithWindowsBox.IsChecked == true,
                Updates: updateSettings);
            string? startupNote = null;
            if (_settings.StartWithWindows && Environment.ProcessPath is { } runningPath)
            {
                startupNote = StartupRegistration.UnstableLocationReason(runningPath, Path.GetTempPath());
                if (startupNote is not null)
                {
                    // Never point the Run key at a temp copy; keep the saved setting truthful.
                    _settings = _settings with { StartWithWindows = false };
                    StartWithWindowsBox.IsChecked = false;
                }
            }
            (_settingsStore ??= new SettingsStore(Path.Combine(_dataDirectory, "settings.json"))).Save(_settings);
            if (Environment.ProcessPath is { } exePath) StartupRegistration.Apply(_settings.StartWithWindows, exePath, new RegistryStartupStore());
            if (nextProvider != previousProvider)
            {
                // Record equality: a changed provider or any model id rebuilds the text clients on the next request.
                _responses = null;
                _decisions = null;
            }
            if (pendingUpdateToken is not null)
            {
                SaveUpdateToken(pendingUpdateToken);
            }

            var notes = new List<string>(5);
            if (SaveCredentialIfEntered(CredentialBox, AiProvider.OpenAI) is { } openAiNote) notes.Add(openAiNote);
            if (SaveCredentialIfEntered(AnthropicCredentialBox, AiProvider.Anthropic) is { } anthropicNote) notes.Add(anthropicNote);
            if (SaveCredentialIfEntered(GeminiCredentialBox, AiProvider.Gemini) is { } geminiNote) notes.Add(geminiNote);

            RefreshUpdatesAfterSave();
            if (startupNote is not null) notes.Add($"Start with Windows was not turned on: {startupNote}");
            if (updateNote is not null) notes.Add(updateNote);
            StatusText.Text = notes.Count == 0
                ? "Settings saved. Credential input was protected and cleared from the UI."
                : $"Settings saved, but {string.Join("; ", notes)}";
            _onDisplayChanged(_selectedDisplay);
        }
        catch (Exception ex)
        {
            StatusText.Text = SanitizedError(ex, "Settings could not be saved; no credential was displayed.");
        }
    }

    private static string ModelIdOrDefault(string text, string fallback)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 ? fallback : trimmed;
    }

    /// <summary>
    /// Protects a typed key with DPAPI into that provider's own file and clears the box. The key is never echoed
    /// anywhere. Text that cannot be an API key (whitespace, control characters, too short) is not stored; the
    /// returned note says so without repeating it.
    /// </summary>
    private string? SaveCredentialIfEntered(PasswordBox box, AiProvider provider)
    {
        if (box.SecurePassword.Length == 0)
        {
            return null;
        }

        var pasted = new System.Net.NetworkCredential(string.Empty, box.SecurePassword).Password;
        box.Clear();
        if (AiProviders.CredentialRejectReason(provider, pasted) is { } reason)
        {
            return $"the {AiProviders.VendorName(provider)} key was not saved: {reason}";
        }

        StoreFor(provider).Save(pasted.Trim());
        return null;
    }

    private bool TrySaveAppearance(NotchAppearance nextAppearance)
    {
        try
        {
            nextAppearance.Validate();
            (_appearanceStore ??= new AppearanceStore(Path.Combine(_dataDirectory, "appearance.json"))).Save(nextAppearance);
            _appearance = nextAppearance;
            BodyColorBox.Text = nextAppearance.BodyColor;
            EyeColorBox.Text = nextAppearance.EyeColor;
            ApplyAppearanceToHeader();
            AppearanceValidationText.Text = string.Empty;
            AppearanceValidationText.Visibility = Visibility.Collapsed;
            _onAppearanceChanged(nextAppearance);
            return true;
        }
        catch (ArgumentException ex)
        {
            AppearanceValidationText.Text = $"Appearance was not saved: {ex.Message}";
            AppearanceValidationText.Visibility = Visibility.Visible;
            StatusText.Text = "Appearance was not saved. Check the color details.";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppearanceValidationText.Text = "Appearance was not saved; the last applied colors remain in use.";
            AppearanceValidationText.Visibility = Visibility.Visible;
            StatusText.Text = SanitizedError(ex, "Appearance could not be saved.");
            return false;
        }
    }

    private void ResetAppearanceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (TrySaveAppearance(new NotchAppearance()))
        {
            StatusText.Text = "Appearance reset to black with white eyes.";
        }
    }

    private void BodyColorButton_OnClick(object sender, RoutedEventArgs e) => ChooseAppearanceColor(BodyColorBox);

    private void EyeColorButton_OnClick(object sender, RoutedEventArgs e) => ChooseAppearanceColor(EyeColorBox);

    private void ChooseAppearanceColor(System.Windows.Controls.TextBox target)
    {
        if (_captureActivities.ActiveDisplays.Count > 0
            || _listening
            || _focus is { Snapshot.Status: Boo.Core.FocusStatus.Running })
        {
            _onStop();
            StatusText.Text = "Active capture, microphone, or focus monitoring stopped before opening the color chooser.";
        }

        using var dialog = new Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            Color = ParseColor(target.Text)
        };
        var owner = new NativeWindowOwner(new WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) == Forms.DialogResult.OK)
        {
            target.Text = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        }
    }

    private static System.Drawing.Color ParseColor(string value)
    {
        try
        {
            return System.Drawing.ColorTranslator.FromHtml(value);
        }
        catch (Exception)
        {
            return System.Drawing.Color.Black;
        }
    }

    private void StopButton_OnClick(object sender, RoutedEventArgs e) => _onStop();

    private void BrowseFolderButton_OnClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the folder Boo may use for requested local files",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(FileRootBox.Text) ? FileRootBox.Text : string.Empty,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            FileRootBox.Text = dialog.SelectedPath;
        }
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _onClose();
    }

    private void Window_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (ActionPreview.Visibility == Visibility.Visible)
            {
                CancelPendingAction();
            }
            else
            {
                _onClose();
            }
            e.Handled = true;
        }
    }

    private void Window_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingForExit)
        {
            e.Cancel = true;
            _onClose();
        }
    }

    private RequestLease BeginRequest()
    {
        StopListening();
        _audio?.Dispose();
        _audio = null;
        _generation++;
        var realtimeToReplace = _realtime;
        _realtime = null;
        _realtimeConnected = false;
        if (realtimeToReplace is not null)
        {
            _ = InterruptRealtimeAsync(realtimeToReplace);
        }
        _requestCancellation.Cancel();
        _requestCancellation.Dispose();
        _requestCancellation = new CancellationTokenSource();
        _requestLease = _coordinator.Begin();
        _generation = _requestLease.Generation;
        _realtimeGeneration = _requestLease.Generation;
        ClearAnnotations();
        ClearActionPreview();
        _ = CompletePendingRealtimeToolAsync("A newer request superseded this action.");
        _pendingRealtimeCallId = null;
        SourceLinksList.Children.Clear();
        ClearSourceStatus();
        return _requestLease;
    }

    private bool IsCurrent(long generation)
    {
        if (_requestCancellation.IsCancellationRequested || generation != _generation)
        {
            return false;
        }
        return _coordinator.IsCurrent(generation);
    }

    private async Task<CapturedFrame?> CaptureForRequestAsync(RequestLease lease, CaptureActivityOperation activity)
    {
        if (!IsCurrent(lease.Generation))
        {
            return null;
        }
        CaptureDetail.Text = string.Empty;
        CaptureDetail.Visibility = Visibility.Collapsed;
        var display = _selectedDisplay;
        if (display is null)
        {
            ReportCaptureProblem("Choose a display in Settings before asking about the screen.");
            return null;
        }

        if (!_displayAffinitySupported)
        {
            ReportCaptureProblem("Capture is paused because Windows could not exclude Boo's own windows.");
            return null;
        }

        try
        {
            var targets = EnsureTargets();
            var target = targets.Snapshot(display, out var limitation);
            if (target is null)
            {
                ReportCaptureProblem(limitation ?? "No stable external window was selected. Bring the window forward, then reopen Boo and retry.");
                return null;
            }
            activity.Start(display);
            UpdateCaptureIndicator();
            await Dispatcher.Yield(DispatcherPriority.Background);
            lease.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(lease.Generation) || !Equals(display, _selectedDisplay))
            {
                return null;
            }
            var frame = await (_capture ??= new ScreenCapture(_timeProvider)).CaptureAsync(
                display,
                target,
                OwnedWindowHandles(),
                lease.Generation,
                lease.Token);
            return IsCurrent(lease.Generation) && Equals(display, _selectedDisplay) ? frame : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (IsCurrent(lease.Generation))
            {
                ReportCaptureProblem(ex is CaptureLimitationException
                    ? ex.Message
                    : $"Screen capture failed ({ex.GetType().Name}, error 0x{ex.HResult:X8}). No frame was sent.");
            }
            return null;
        }
    }

    private void ReportCaptureProblem(string message)
    {
        CaptureDetail.Text = message;
        CaptureDetail.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private TargetObserver EnsureTargets()
    {
        return _targets ??= new TargetObserver();
    }

    private async Task<ControlObservation?> ReadRequestedControlsAsync(
        CapturedFrame frame, RequestLease lease, RealtimeClient? realtimeSource = null)
    {
        var targets = EnsureTargets();
        var reader = _controlReader ??= new ControlObservationReader(targets);
        var controlReadFailed = false;
        ControlObservation controls;
        try
        {
            controls = await reader.ReadAsync(frame.Context, lease.Token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            controlReadFailed = true;
            controls = ControlObservation.Create(frame.Context, Array.Empty<ControlCandidate>());
        }

        lease.Token.ThrowIfCancellationRequested();
        if (!IsCurrent(lease.Generation) || !ReferenceEquals(_requestLease, lease)
            || !ReferenceEquals(_targets, targets) || !ReferenceEquals(_controlReader, reader)
            || (realtimeSource is not null && (!_realtimeConnected || !ReferenceEquals(_realtime, realtimeSource))))
        {
            return null;
        }
        if (controls.Context != frame.Context || frame.Context.Generation != lease.Generation
            || frame.Context.Display != _selectedDisplay || !targets.IsCurrent(frame.Context.Target))
        {
            ReportCaptureProblem("The selected window changed. Ask again to use its current screen controls.");
            return null;
        }
        if (controlReadFailed)
        {
            ReportCaptureProblem("Screen controls could not be read. Boo can explain the image; typing actions are unavailable.");
        }
        return controls;
    }

    private IScreenAssistant EnsureResponses()
    {
        if (_responses is not null)
        {
            return _responses;
        }

        var providerSettings = _settings.Provider ?? new ProviderSettings();
        var provider = providerSettings.TextProvider;
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _responses = AssistantProviders.CreateScreenAssistant(provider, _http, () => StoreFor(provider).Load(), providerSettings);
        return _responses;
    }

    private RealtimeClient EnsureRealtime(bool pushToTalk = false)
    {
        if (_realtime is not null)
        {
            return _realtime;
        }

        _credentialStore ??= StoreFor(AiProvider.OpenAI);
        var realtime = new RealtimeClient(() => _credentialStore.Load(), _settings.Provider ?? new ProviderSettings(), pushToTalk);
        _realtimePushToTalk = pushToTalk;
        _realtimeTranscript.Reset();
        _realtimeTranscriptRows.Clear();
        var generation = _generation;
        realtime.Received += value => Realtime_OnReceived(realtime, generation, value);
        AttachVoiceCommands(realtime);
        _realtime = realtime;
        return _realtime;
    }

    private AudioSession CreateAudio(AudioUploadQueue? uploads = null, long? generation = null)
    {
        var audio = new AudioSession();
        var sessionGeneration = generation ?? _generation;
        audio.Failed += message => PostAudioFailure(message, sessionGeneration, uploads, audio);
        if (uploads is not null)
        {
            audio.Captured += pcm =>
            {
                try { uploads.TryEnqueue(pcm); }
                catch (ArgumentException)
                {
                    uploads.Stop();
                    PostAudioFailure("The microphone returned an unsupported audio frame.", sessionGeneration, uploads, audio);
                }
            };
        }
        return audio;
    }

    private void PostAudioFailure(string message, long generation, AudioUploadQueue? uploads, AudioSession? source = null)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_closingForExit || !IsCurrent(generation) || _requestLease?.Generation != generation
                    || (uploads is not null && !ReferenceEquals(_audioUploads, uploads))
                    || (source is not null && !ReferenceEquals(_audio, source)))
                {
                    return;
                }
                FailVoiceSession(message, "Audio paused. Typing remains available; activate Talk to retry.");
            });
        }
        catch (InvalidOperationException) { /* Dispatcher shutdown has already stopped the session. */ }
    }

    private void StopAudioUploads()
    {
        var uploads = _audioUploads;
        _audioUploads = null;
        if (uploads is null)
        {
            return;
        }
        uploads.Stop();
        _ = DisposeAudioUploadsAsync(uploads);
    }

    private static async Task DisposeAudioUploadsAsync(AudioUploadQueue uploads)
    {
        try { await uploads.DisposeAsync(); }
        catch (Exception) { }
    }

    private DesktopTools EnsureDesktop()
    {
        _desktop ??= new DesktopTools(_coordinator, EnsureTargets());
        return _desktop;
    }

    private WindowIdentity? TryGetTarget()
    {
        if (_selectedDisplay is null)
        {
            return null;
        }
        try
        {
            return EnsureTargets().Snapshot(_selectedDisplay);
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            StatusText.Text = SanitizedError(ex, "Target is no longer available; approval must be refreshed.");
            return null;
        }
    }

    private async void Realtime_OnReceived(RealtimeClient source, long generation, RealtimeEvent realtimeEvent)
    {
        if (!ReferenceEquals(_realtime, source) || !IsCurrent(generation)
            || !_realtimeConnected || _requestLease is null || _requestLease.Generation != _realtimeGeneration
            || _requestCancellation.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await Dispatcher.InvokeAsync(async () =>
            {
                if (!ReferenceEquals(_realtime, source) || !IsCurrent(generation)
                    || !_realtimeConnected || _requestLease is null || _requestLease.Generation != _realtimeGeneration
                    || _requestCancellation.IsCancellationRequested)
                {
                    return;
                }
                ObserveTalkEvent(realtimeEvent);
                ObserveSleepAfterReply(source, realtimeEvent);
                switch (realtimeEvent.Kind)
                {
                    case "input_audio_buffer.speech_started":
                    case "input_audio_buffer.speech_stopped":
                    case "input_audio_buffer.committed":
                        ListeningStatus.Text = realtimeEvent.Kind.EndsWith("started", StringComparison.Ordinal)
                            ? "Listening…"
                            : "Thinking…";
                        SetFace(_faceInputs with { Thinking = !realtimeEvent.Kind.EndsWith("started", StringComparison.Ordinal), Caption = null });
                        break;
                    case "conversation.item.input_audio_transcription.delta":
                    case "conversation.item.input_audio_transcription.completed":
                    case "response.output_audio_transcript.delta":
                    case "response.output_audio_transcript.done":
                    case "response.output_text.delta":
                    case "response.output_text.done":
                        ApplyRealtimeTranscript(realtimeEvent);
                        if (realtimeEvent.Kind == "conversation.item.input_audio_transcription.completed"
                            && !string.IsNullOrWhiteSpace(realtimeEvent.Text))
                        {
                            var spokenIntent = RequestIntent.Classify(realtimeEvent.Text);
                            _voiceTaskAuthorized = spokenIntent == RequestKind.Task;
                            if (spokenIntent == RequestKind.Undo)
                            {
                                await HandleVoiceUndoAsync(source);
                            }
                        }
                        break;
                    case "response.output_audio.delta":
                        // Moment refreshed by every audio delta so an error/disconnect before response.done
                        // cannot leave the notch stuck on Responding.
                        SetFace(_faceInputs with { Thinking = false, Responding = true }, TimeSpan.FromSeconds(4), FaceMomentKind.Responding);
                        if (realtimeEvent.Audio is not null && SpeechMute.IsChecked != true)
                        {
                            try
                            {
                                (_audio ??= CreateAudio()).Play(realtimeEvent.Audio);
                            }
                            catch (Exception ex) when (ex is InvalidOperationException)
                            {
                                StatusText.Text = SanitizedError(ex, "Spoken reply is unavailable; text remains visible.");
                            }
                        }
                        break;
                    case "response.function_call_arguments.done":
                        await CompleteRealtimeToolAsync(realtimeEvent);
                        break;
                    case "response.done":
                        StatusText.Text = realtimeEvent.Text ?? "Reply complete. Text and spoken output share the same turn.";
                        var remainingPlayback = TimeSpan.FromMilliseconds(
                            Interlocked.Read(ref _playbackUntilMs) - Environment.TickCount64);
                        SetFace(_faceInputs with { Thinking = _realtimeToolsInFlight > 0 },
                            FaceMoments.AfterReplyDone(remainingPlayback), FaceMomentKind.Responding);
                        break;
                    case "disconnect":
                    case "error":
                        AbortHoldToTalk();
                        StopListening();
                        if (_focus is { Snapshot.Status: Boo.Core.FocusStatus.Running })
                        {
                            _focus.Pause("Provider disconnected");
                            _focusTimer.Stop();
                            StopFocusMonitoring();
                            SaveFocusSnapshot();
                            SetFace(_faceInputs with { FocusPaused = true });
                        }
                        StatusText.Text = realtimeEvent.Kind == "error" && realtimeEvent.Text is { Length: > 0 } errorText
                                          && errorText != RealtimeProtocol.GenericErrorText
                            ? errorText // classified and body-free, e.g. quota used up or key rejected
                            : "Realtime connection stopped; typing remains available. Activate Talk to retry.";
                        break;
                    default:
                        ListeningStatus.Text = $"Realtime: {realtimeEvent.Kind}";
                        break;
                }
            }).Task.Unwrap();
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            PostAudioFailure("The voice session could not continue. Activate Talk to retry.", generation, null);
        }
    }

    private async Task CompleteRealtimeToolAsync(RealtimeEvent realtimeEvent)
    {
        var realtime = _realtime;
        if (!_realtimeConnected || realtime is null || _requestLease is null
            || _requestLease.Generation != _realtimeGeneration
            || _requestCancellation.IsCancellationRequested
            || string.IsNullOrWhiteSpace(realtimeEvent.ItemId))
        {
            return;
        }

        var question = realtimeEvent.Text ?? string.Empty;
        var mode = "screen_help";
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(question);
            var root = document.RootElement;
            question = root.TryGetProperty("question", out var q) ? q.GetString() ?? string.Empty : question;
            mode = root.TryGetProperty("mode", out var m) ? m.GetString() ?? mode : mode;
        }
        catch (System.Text.Json.JsonException)
        {
            // Tool arguments are provider text; malformed arguments fail closed.
        }

        var lease = _requestLease!;
        if (await DeclineRealtimeToolDuringUndoAsync(realtime, realtimeEvent.ItemId, lease))
        {
            return;
        }
        var activity = _captureActivities.CreateOperation();
        _realtimeToolsInFlight++;
        SetFace(_faceInputs with { Thinking = true });
        try
        {
            var frame = await CaptureForRequestAsync(lease, activity);
            if (frame is null)
            {
                EndCaptureActivity(activity);
            }
            if (!IsCurrent(lease.Generation) || !ReferenceEquals(_realtime, realtime))
            {
                return;
            }
            var explicitTask = mode.Equals("task", StringComparison.OrdinalIgnoreCase) && _voiceTaskAuthorized;
            ControlObservation? controls = null;
            if (explicitTask && frame is not null)
            {
                controls = await ReadRequestedControlsAsync(frame, lease, realtime);
                if (controls is null)
                {
                    if (IsCurrent(lease.Generation) && ReferenceEquals(_requestLease, lease)
                        && _realtimeConnected && ReferenceEquals(_realtime, realtime))
                    {
                        await realtime.CompleteToolAsync(realtimeEvent.ItemId,
                            "The selected window changed. Request a fresh screen observation; no action was proposed.", lease.Token);
                    }
                    return;
                }
            }
            var reply = await EnsureResponses().RespondAsync(
                new AssistantRequest(question, lease.Generation, frame, Research: mode == "research", AllowActions: explicitTask),
                lease.Token, controls);
            if (!IsCurrent(lease.Generation) || !_realtimeConnected || !ReferenceEquals(_realtime, realtime))
            {
                return;
            }
            if (await DeclineRealtimeToolDuringUndoAsync(realtime, realtimeEvent.ItemId, lease))
            {
                return;
            }
            RenderReply(reply, lease.Generation, frame, explicitTask, controls, mode == "research");
            if (explicitTask && reply.Actions.Count > 0)
            {
                _pendingRealtimeCallId = realtimeEvent.ItemId;
            }
            else
            {
                await realtime.CompleteToolAsync(realtimeEvent.ItemId, reply.Text, lease.Token);
            }
        }
        catch (Exception ex) when (ex is ResponseTimeoutException or OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            if (!IsCurrent(lease.Generation) || !_realtimeConnected || !ReferenceEquals(_realtime, realtime))
            {
                return;
            }
            var failure = ex is ResponseTimeoutException
                ? "The screen-help request timed out. Ask the user to try again."
                : "Tool unavailable; explain without taking an action.";
            await realtime.CompleteToolAsync(realtimeEvent.ItemId, failure, lease.Token);
            if (IsCurrent(lease.Generation) && _realtimeConnected && ReferenceEquals(_realtime, realtime))
            {
                StatusText.Text = ex is OperationCanceledException
                    ? ResponseInterruptedMessage
                    : SanitizedError(ex, "Screen-help tool failed safely.");
            }
        }
        finally
        {
            EndCaptureActivity(activity);
            if (_realtimeToolsInFlight > 0)
            {
                _realtimeToolsInFlight--;
            }
        }
    }

    private static async Task InterruptRealtimeAsync(RealtimeClient realtime)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await realtime.InterruptAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A disconnected peer cannot acknowledge cancellation; local teardown still runs.
        }
        finally
        {
            try { await realtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private async Task CompletePendingRealtimeToolAsync(string result)
    {
        var callId = _pendingRealtimeCallId;
        _pendingRealtimeCallId = null;
        if (callId is null || _realtime is null)
        {
            return;
        }
        try
        {
            await _realtime.CompleteToolAsync(callId, result, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            StatusText.Text = "Realtime action result could not be returned; the local outcome remains visible.";
        }
    }

    private void AppendTranscript(string speaker, string text)
    {
        if (text.Length == 0)
        {
            return;
        }
        _transcript.Add(new TranscriptLine(speaker, text));
        ScrollTranscriptToEnd();
    }

    private void AppendSourceLink(SourceLink source)
    {
        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            AppendTranscript("Source", $"{source.Title} · source URL was rejected");
            return;
        }

        var line = new TextBlock
        {
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var link = new Hyperlink(new Run(source.Title))
        {
            NavigateUri = uri,
            ToolTip = uri.AbsoluteUri
        };
        AutomationProperties.SetName(link, $"Open source {source.Title}");
        link.RequestNavigate += SourceLink_OnRequestNavigate;
        line.Inlines.Add(link);
        SourceLinksList.Children.Add(line);
    }

    private static void SourceLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        if (e.Uri.Scheme is "http" or "https")
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        e.Handled = true;
    }

    private void ApplyRealtimeTranscript(RealtimeEvent value)
    {
        var update = _realtimeTranscript.Apply(value);
        if (update is null)
        {
            return;
        }
        if (update.Speaker == "Boo" && update.Text.Length > 0)
        {
            // Boo's spoken words are typed into the notch while it is responding.
            SetFace(_faceInputs with { Caption = update.Text });
        }
        if (update.Text.Length == 0 && value.Kind is not
            ("conversation.item.input_audio_transcription.completed" or
             "response.output_audio_transcript.done" or "response.output_text.done"))
        {
            return;
        }

        var key = (update.ItemId, update.ContentIndex, update.Speaker);
        if (_realtimeTranscriptRows.TryGetValue(key, out var index))
        {
            if (update.Text.Length == 0)
            {
                RemoveRealtimeTranscriptRow(key, index);
            }
            else
            {
                _transcript[index] = new TranscriptLine(update.Speaker, update.Text);
            }
        }
        else if (update.Text.Length > 0)
        {
            _realtimeTranscriptRows.Add(key, _transcript.Count);
            AppendTranscript(update.Speaker, update.Text);
            return;
        }
        ScrollTranscriptToEnd();
    }

    private void ScrollTranscriptToEnd()
    {
        if (_transcript.Count > 0)
        {
            // The non-virtualizing list generates the new container synchronously but has not measured it
            // yet; without UpdateLayout, ScrollIntoView would scroll to a stale zero-sized rect.
            TranscriptList.UpdateLayout();
            TranscriptList.ScrollIntoView(_transcript[^1]);
        }
    }

    private void RemoveRealtimeTranscriptRow((string ItemId, int ContentIndex, string Speaker) key, int index)
    {
        _transcript.RemoveAt(index);
        _realtimeTranscriptRows.Remove(key);
        foreach (var laterKey in _realtimeTranscriptRows.Keys.ToArray())
        {
            if (_realtimeTranscriptRows[laterKey] > index)
            {
                _realtimeTranscriptRows[laterKey]--;
            }
        }
    }

    private void ClearAnnotations()
    {
        foreach (var annotation in _annotationWindows.ToArray())
        {
            annotation.Close();
        }
        _annotationWindows.Clear();
        AnnotationDetail.Text = string.Empty;
        AnnotationDetail.Visibility = Visibility.Collapsed;
    }

    private IReadOnlyList<long> OwnedWindowHandles()
    {
        var handles = new List<long>();
        if (new WindowInteropHelper(this).Handle is var panel && panel != IntPtr.Zero)
        {
            handles.Add(panel.ToInt64());
        }
        handles.AddRange(_ownedWindows());
        foreach (var annotation in _annotationWindows)
        {
            var annotationHandle = new WindowInteropHelper(annotation).Handle;
            if (annotationHandle != IntPtr.Zero)
            {
                handles.Add(annotationHandle.ToInt64());
            }
        }
        return handles;
    }

    private static string SanitizedError(Exception ex, string fallback)
    {
        return ex switch
        {
            ResponseTimeoutException timeout => timeout.Message,
            InvalidOperationException { Message: ResponseInterruptedMessage } => ResponseInterruptedMessage,
            // Thrown by the provider clients when no key is saved; matched exactly per vendor, never shown raw.
            InvalidOperationException { Message: var m } when AiProviders.ProviderOfMissingCredentialMessage(m) is { } p =>
                $"No {AiProviders.DisplayName(p)} key yet. Add it in Settings, then try again.",
            OperationCanceledException => "Stopped safely.",
            UnauthorizedAccessException => "Access was denied. Check the selected target or permission.",
            ProviderException provider => provider.Failure.UserMessage,
            HttpRequestException http => ProviderFailureClassifier.FromException(http)?.UserMessage
                                         ?? "Provider connection failed. Check network and protected setup.",
            _ => fallback
        };
    }

    // A class, not a record: every row must be its own item. Two equal records collapsed into ONE row for screen readers
    // (WPF keys a list item's automation peer by the item) and made ScrollIntoView jump to the older copy (issue #73).
    private sealed class TranscriptLine(string speaker, string text)
    {
        public string Speaker { get; } = speaker;
        public string Text { get; } = text;
    }

    private sealed class NativeWindowOwner : Forms.IWin32Window
    {
        public NativeWindowOwner(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeWindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeWindowRect rect);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
