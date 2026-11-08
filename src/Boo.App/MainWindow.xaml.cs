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
    private ResponsesClient? _responses;
    private DecisionsClient? _decisions;
    private RealtimeClient? _realtime;
    private DesktopTools? _desktop;
    private SettingsStore? _settingsStore;
    private SessionStore? _sessionStore;
    private CredentialStore? _credentialStore;
    private HttpClient? _http;
    private FocusSession? _focus;
    private NudgePolicy? _nudge;
    private CancellationTokenSource _focusCancellation = new();
    private bool _focusCaptureInFlight;
    private double _lastFocusCaptureElapsed;
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
    private readonly ChirpPlayer _chirps = new();
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
        Action<NotchAppearance> onAppearanceChanged)
    {
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

        _focusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _focusTimer.Tick += FocusTimer_OnTick;
        _focusCaptureTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30)
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

    public void PrepareInitialPlacement()
    {
        InitializeBeforeNotch();
        if (!_initialPlacementApplied)
        {
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
            1d);
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
    }

    public void DisposeShell()
    {
        _closingForExit = true;
        StopAll();
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
        LoadMicrophones();
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
                1d);
            _displays[info.Id] = info;
            DisplayBox.Items.Add($"{info.Name} · {bounds.Width}×{bounds.Height}");
        }

        _selectedDisplay = _settings.DisplayId is not null && _displays.TryGetValue(_settings.DisplayId, out var saved)
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
        var scale = _selectedDisplay.Scale > 0 ? _selectedDisplay.Scale : 1d;
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

        var widthPixels = Width * scale;
        var heightPixels = Height * scale;
        var leftPixels = work.Left + Math.Max(0d, (work.Width - widthPixels) / 2d);
        var topPixels = work.Top + Math.Max(0d, (work.Height - heightPixels) / 2d);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || WindowState == WindowState.Maximized)
        {
            Left = leftPixels / scale;
            Top = topPixels / scale;
            return;
        }

        SetWindowPos(handle, IntPtr.Zero,
            (int)Math.Round(leftPixels),
            (int)Math.Round(topPixels),
            (int)Math.Round(widthPixels),
            (int)Math.Round(heightPixels),
            SwpNoActivate | SwpNoZOrder);
        var actualScale = GetDpiForWindow(handle) / 96d;
        if (double.IsFinite(actualScale) && actualScale > 0 && Math.Abs(actualScale - scale) > 0.01)
        {
            _selectedDisplay = _selectedDisplay with { Scale = actualScale };
            _displays[_selectedDisplay.Id] = _selectedDisplay;
            PositionNearDisplay();
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

            var scale = _selectedDisplay.Scale;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero)
            {
                var currentScreen = Forms.Screen.FromHandle(handle);
                if (string.Equals(currentScreen.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    scale = GetDpiForWindow(handle) / 96d;
                }
            }

            var refreshed = _selectedDisplay with
            {
                PixelBounds = new RectD(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height),
                Scale = double.IsFinite(scale) && scale > 0 ? scale : 1d
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
                1d);
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
        AppendTranscript("You", text);
        InputBox.Clear();
        StatusText.Text = "Thinking about the selected context…";
        var activity = _captureActivities.CreateOperation();
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
                _voiceTaskAuthorized = LooksLikeExplicitTask(text);
                await _realtime.SendTextAsync(text, lease.Token);
                StatusText.Text = "Sent to the voice conversation; waiting for the response…";
                return;
            }

            var explicitTask = LooksLikeExplicitTask(text);
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
                Research: LooksLikeResearch(text),
                AllowActions: explicitTask);
            var reply = await client.RespondAsync(request, lease.Token, controls);
            if (!IsCurrent(lease.Generation))
            {
                return;
            }
            RenderReply(reply, lease.Generation, frame, request.AllowActions, controls);
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
            _listening = true;
            _audio.Start(_settings.MicrophoneDevice);
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
    }

    private void RenderReply(AssistantReply reply, long generation, CapturedFrame? frame, bool explicitTask, ControlObservation? controls)
    {
        ClearAnnotations();
        AppendTranscript("Boo", reply.Text);
        SourceLinksList.Children.Clear();
        foreach (var source in reply.Sources)
        {
            AppendSourceLink(source);
        }

        if (reply.Annotations is not null && frame is not null)
        {
            TryRenderAnnotations(reply.Annotations, frame);
        }
        else if (frame is not null)
        {
            ReportAnnotationOutcome("Boo explained the screen without a verified mark.");
        }

        if (!IsCurrent(generation))
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
            var validation = AnnotationValidator.Validate(proposal, frame.Context, DateTimeOffset.UtcNow);
            if (!validation.Valid)
            {
                ReportAnnotationOutcome($"Boo explained the screen without drawing: {validation.Reason}");
                return;
            }

            var window = new AnnotationWindow(frame.Context, proposal.Marks);
            new WindowInteropHelper(window).EnsureHandle();
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

    private void ReportAnnotationOutcome(string message)
    {
        AnnotationDetail.Text = message;
        AnnotationDetail.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private void ShowActionPreview(ToolRequest action, TaskAuthority authority, WindowIdentity? target, string prefix)
    {
        var preview = ActionPreviewBuilder.Create(action, authority);
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
            var outcome = await EnsureDesktop().ExecuteAsync(action, authority, approval, target, lease.Token);
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
            StatusText.Text = "Stopped before the action committed.";
            await CompletePendingRealtimeToolAsync("Action stopped before commit; no later effect occurred.");
        }
        catch (Exception ex)
        {
            StatusText.Text = SanitizedError(ex, "The action could not be verified.");
            await CompletePendingRealtimeToolAsync("Action failed before a verified commit.");
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
            _focus = new FocusSession(FocusTaskBox.Text.Trim(), Math.Clamp(minutes * 60d, 60d, 14400d));
            _nudge = new NudgePolicy();
            _lastFocusCaptureElapsed = 0;
            _focusCaptureInFlight = false;
            _focusTimer.Start();
            _focusCaptureTimer.Start();
            UpdateFocusView();
            FocusStatus.Text = "Running; monitoring pauses on Stop, lock, sleep, or Pause.";
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
        }
        catch (Exception ex)
        {
            FocusStatus.Text = SanitizedError(ex, "Could not pause focus.");
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
            _focus.Resume();
            _focusCancellation.Dispose();
            _focusCancellation = new CancellationTokenSource();
            _focusCaptureTimer.Start();
            _focusTimer.Start();
            UpdateFocusView();
            FocusStatus.Text = "Running after explicit Resume.";
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
            _focus.End();
            _chirps.PlaySessionEnd(_settings.ChirpsEnabled, _listening);
            _focusTimer.Stop();
            StopFocusMonitoring();
            SaveFocusSnapshot();
            UpdateFocusView();
            FocusStatus.Text = "Ended. Start a new session explicitly.";
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
            _focus.Advance(1);
            UpdateFocusView();
            if (_focus.Snapshot.Status != Boo.Core.FocusStatus.Running)
            {
                if (_focus.Snapshot.Status == Boo.Core.FocusStatus.Ended) _chirps.PlaySessionEnd(_settings.ChirpsEnabled, _listening);
                _focusTimer.Stop();
            }
        }
        catch (Exception ex)
        {
            _focusTimer.Stop();
            FocusStatus.Text = SanitizedError(ex, "Focus monitoring stopped safely.");
        }
    }

    private void FocusCaptureTimer_OnTick(object? sender, EventArgs e)
    {
        if (_focus is null || !_focus.ShouldMonitor || _focusCaptureInFlight
            || _focus.Snapshot.Elapsed < _lastFocusCaptureElapsed + 30)
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
        var token = _focusCancellation.Token;
        var activity = _captureActivities.CreateOperation();
        try
        {
            var snapshot = _focus.Snapshot;
            var leaseGeneration = _generation;
            var frame = await CaptureFocusFrameAsync(leaseGeneration, token, activity);
            if (frame is null || token.IsCancellationRequested || _focus is null || !_focus.ShouldMonitor)
            {
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
            _focus.AddObservation(check);
            _lastFocusCaptureElapsed = snapshot.Elapsed;
            var age = Math.Max(0, (DateTimeOffset.UtcNow - frame.Context.CapturedAt).TotalSeconds);
            if (_nudge?.Observe(check, age) == true)
            {
                FocusStatus.Text = "Nudge: return to the task when you are ready. Boo did not change your apps.";
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
                _focus.AddObservation(unknown);
                SaveFocusSnapshot();
                FocusStatus.Text = SanitizedError(ex, "Focus observation unavailable; this interval is unknown.");
            }
        }
        finally
        {
            _focusCaptureInFlight = false;
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
        var frame = await (_capture ??= new ScreenCapture()).CaptureAsync(
            display,
            target,
            OwnedWindowHandles(),
            generation,
            token);
        return !token.IsCancellationRequested && Equals(display, _selectedDisplay) ? frame : null;
    }

    private DecisionsClient EnsureDecisions()
    {
        _credentialStore ??= new CredentialStore(Path.Combine(_dataDirectory, "credential.bin"));
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        return _decisions ??= new DecisionsClient(_http, () => _credentialStore.Load(), _settings.Provider ?? new ProviderSettings());
    }

    private void StopFocusMonitoring()
    {
        _focusCaptureTimer.Stop();
        _focusCancellation.Cancel();
        _focusCaptureInFlight = false;
        _nudge?.Reset();
        UpdateCaptureIndicator();
    }

    private void SaveFocusSnapshot()
    {
        if (_focus is null || _sessionStore is null)
        {
            return;
        }
        try
        {
            _sessionStore.Save(_focus.Snapshot);
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
                .Take(5)
                .Select(session =>
                    $"{session.Task} · {session.Status} · {TimeSpan.FromSeconds(session.RealElapsed):hh\\:mm\\:ss} · {session.Checks.Count} periodic observations")
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
        HistoryList.ItemsSource = Array.Empty<string>();
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

            _settings = new AppSettings(
                DisplayId: _selectedDisplay?.Id,
                SpeechEnabled: SpeechMute.IsChecked != true,
                ChirpsEnabled: ChirpMute.IsChecked != true,
                ReducedMotion: ReducedMotionBox.IsChecked == true,
                VerticalOffset: OffsetSlider.Value,
                MicrophoneDevice: Math.Max(-1, MicrophoneBox.SelectedIndex - 1),
                FileRoot: string.IsNullOrWhiteSpace(FileRootBox.Text) ? null : Path.GetFullPath(FileRootBox.Text.Trim()),
                Provider: _settings.Provider ?? new ProviderSettings(),
                StartWithWindows: StartWithWindowsBox.IsChecked == true);
            (_settingsStore ??= new SettingsStore(Path.Combine(_dataDirectory, "settings.json"))).Save(_settings);
            if (Environment.ProcessPath is { } exePath) StartupRegistration.Apply(_settings.StartWithWindows, exePath, new RegistryStartupStore());

            if (CredentialBox.SecurePassword.Length > 0)
            {
                (_credentialStore ??= new CredentialStore(Path.Combine(_dataDirectory, "credential.bin"))).Save(new System.Net.NetworkCredential(string.Empty, CredentialBox.SecurePassword).Password);
                CredentialBox.Clear();
            }

            StatusText.Text = "Settings saved. Credential input was protected and cleared from the UI.";
            _onDisplayChanged(_selectedDisplay);
        }
        catch (Exception ex)
        {
            StatusText.Text = SanitizedError(ex, "Settings could not be saved; no credential was displayed.");
        }
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
            var frame = await (_capture ??= new ScreenCapture()).CaptureAsync(
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

    private ResponsesClient EnsureResponses()
    {
        if (_responses is not null)
        {
            return _responses;
        }

        _credentialStore ??= new CredentialStore(Path.Combine(_dataDirectory, "credential.bin"));
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _responses = new ResponsesClient(_http, () => _credentialStore.Load(), _settings.Provider ?? new ProviderSettings());
        return _responses;
    }

    private RealtimeClient EnsureRealtime()
    {
        if (_realtime is not null)
        {
            return _realtime;
        }

        _credentialStore ??= new CredentialStore(Path.Combine(_dataDirectory, "credential.bin"));
        var realtime = new RealtimeClient(() => _credentialStore.Load(), _settings.Provider ?? new ProviderSettings());
        _realtimeTranscript.Reset();
        _realtimeTranscriptRows.Clear();
        var generation = _generation;
        realtime.Received += value => Realtime_OnReceived(realtime, generation, value);
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
                StopListening();
                ListeningStatus.Text = "Audio paused. Typing remains available; activate Talk to retry.";
                StatusText.Text = message;
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
                switch (realtimeEvent.Kind)
                {
                    case "input_audio_buffer.speech_started":
                    case "input_audio_buffer.speech_stopped":
                        ListeningStatus.Text = realtimeEvent.Kind.EndsWith("started", StringComparison.Ordinal)
                            ? "Listening…"
                            : "Thinking…";
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
                            _voiceTaskAuthorized = LooksLikeExplicitTask(realtimeEvent.Text);
                        }
                        break;
                    case "response.output_audio.delta":
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
                        break;
                    case "disconnect":
                    case "error":
                        StopListening();
                        if (_focus is { Snapshot.Status: Boo.Core.FocusStatus.Running })
                        {
                            _focus.Pause("Provider disconnected");
                            _focusTimer.Stop();
                            StopFocusMonitoring();
                            SaveFocusSnapshot();
                        }
                        StatusText.Text = "Realtime connection stopped; typing remains available. Activate Talk to retry.";
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
        var activity = _captureActivities.CreateOperation();
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
            RenderReply(reply, lease.Generation, frame, explicitTask, controls);
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
        TranscriptScroll.ScrollToEnd();
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
        TranscriptScroll.ScrollToEnd();
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

    private static bool LooksLikeResearch(string text)
    {
        return text.StartsWith("find ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("search ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("research ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExplicitTask(string text)
    {
        return text.StartsWith("task ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("please open ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("open ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("please create ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("create ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("type ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("click ", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizedError(Exception ex, string fallback)
    {
        return ex switch
        {
            ResponseTimeoutException timeout => timeout.Message,
            InvalidOperationException { Message: ResponseInterruptedMessage } => ResponseInterruptedMessage,
            OperationCanceledException => "Stopped safely.",
            UnauthorizedAccessException => "Access was denied. Check the selected target or permission.",
            HttpRequestException => "Provider connection failed. Check network and protected setup.",
            _ => fallback
        };
    }

    private sealed record TranscriptLine(string Speaker, string Text);

    private sealed class NativeWindowOwner : Forms.IWin32Window
    {
        public NativeWindowOwner(IntPtr handle) => Handle = handle;

        public IntPtr Handle { get; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
