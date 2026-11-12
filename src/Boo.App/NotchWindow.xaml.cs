using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Rectangle = System.Windows.Shapes.Rectangle;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using Boo.Core;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaSystemColors = System.Windows.SystemColors;

namespace Boo.App;

/// <summary>
/// The notch: a Living ink blot at the top centre with two cream eyes (face lane U1/U2, Living ink 2026-11-12).
/// Sizes and hover timings come from docs/design/boo-notch-spec.json (chosen face D); the behaviour lives in
/// Boo.Core (FaceStateResolver, FaceMotion gaze, NotchLayout, NotchRevealTimeline, and LivingInk: blot shapes, moods,
/// morph, wake-up, celebrate, lid blink) and this class is the thin WPF/Win32 glue.
/// </summary>
public partial class NotchWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int WmHotKey = 0x0312;
    private const int MaNoActivate = 3;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int EmergencyHotKeyId = 0xB00;
    private const int OpenHotKeyId = 0xB01;
    private const int PauseAllHotKeyId = 0xB02;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint EmergencyKey = 0x7B;
    private const uint PauseAllKey = 0x53; // 'S': Ctrl+Alt+S, Riley's "pause everything" gesture
    private static readonly TimeSpan StoppedFlash = TimeSpan.FromMilliseconds(1600);
    private const string SpecResourceName = "Boo.App.boo-notch-spec.json";
    private const double ShakePadDip = 6;
    private const double DisclosureReserveDip = 76;
    /// <summary>Room each side of the widest blot for loose ink (rising thinking dots, splatters).</summary>
    private const double InkPadDip = 30;
    /// <summary>The wake-up plays on Boo's first appearance and again only after it has been hidden this long (an "arrival", not a hover).</summary>
    private static readonly TimeSpan WakeAgainAfter = TimeSpan.FromMinutes(5);

    private readonly Action _onOpenPanel;
    private readonly Action _onStop;
    private readonly Func<DisplayInfo?> _selectedDisplay;
    private readonly Func<double> _verticalOffset;
    private readonly Action _onDisplayLayoutChanged;
    private readonly Func<bool> _appReducedMotion;
    private readonly NotchSpec _spec;
    private readonly NotchFaceSpec _face;
    private readonly NotchRevealTimeline _timeline;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private FaceMotion _motion;
    private readonly CaptionReveal _captionReveal;
    private readonly PressHoldTracker _press = new();
    private readonly DispatcherTimer _holdTimer;
    private readonly Rectangle[] _speakingBars = new Rectangle[LivingInk.BarCount];
    private readonly Ellipse[] _thinkDots = new Ellipse[LivingInk.ThinkDotSizes.Count];
    private readonly Ellipse[] _blooms = new Ellipse[LivingInk.Blooms.Count];
    private readonly Ellipse[] _splatters = new Ellipse[LivingInk.Splatters.Count];
    private readonly Rectangle _drip = new() { Width = LivingInk.DripWidth, RadiusX = LivingInk.DripWidth / 2, RadiusY = LivingInk.DripWidth / 2 };
    private readonly InkMorph _morph;
    private readonly InkWakeUp _wake;
    private readonly InkCelebrate _celebrate = new();
    private InkBlink _blink = new(new Random());
    private InkBlot? _builtBlot;
    private NotchFaceState _builtState = NotchFaceState.Hidden;
    private double _builtTopRadius = double.NaN;
    private bool _everShown;
    private TimeSpan? _hiddenSince;
    private bool _darkTheme;
    private MediaBrush _bodyBrush = MediaBrushes.Black;
    private MediaBrush _alertBrush = MediaBrushes.DarkRed;
    private readonly double _windowWidthDip;
    private readonly double _windowHeightDip;
    private NotchAppearance _appearance = new();
    private NotchRowParts _row = new(default, default, default, default, default, 0);
    private string _caption = string.Empty;
    private string _rawCaption = string.Empty; // full normalised caption as given; _caption is the drawn window of it
    private string _captionShown = string.Empty;
    private TimeSpan _captionStart;
    private FaceInputs _appInputs = new();
    private bool _hovered;
    private NotchFaceState _state = NotchFaceState.Hidden;
    private TimeSpan _stateSince;
    private SizeD _sizeNow;
    private SizeD _builtSize;
    private bool _rendering;
    private TimeSpan _lastFrame;
    private readonly FramePacer _pacer = new();
    private long _renderedFrames;
    private bool _reducedMotion;
    private NotchFaceParts _parts = new(default, default, default);
    private bool _captureActive;
    private string? _captureDisplayName;
    private bool _microphoneActive;
    private string? _status;
    private string? _flashStatus;
    private DispatcherTimer? _flashTimer;
    private IntPtr _handle;
    private IntPtr _lastExternalHandle;
    private int _lastExternalProcess;
    private bool _positioning;
    private double _scale = 1;
    private double _windowLeftPx;
    private double _windowTopPx;

    public bool CaptureExclusionAvailable { get; private set; } = true;
    public bool EmergencyStopRegistered { get; private set; }
    public bool OpenShortcutRegistered { get; private set; }
    public bool PauseAllRegistered { get; private set; }

    /// <summary>
    /// Press-and-hold on the eyes: true once a left press has lasted 200 ms, false on release or lost capture.
    /// A shorter click opens the full window instead and raises nothing.
    /// TODO(talk lane): App/MainWindow should subscribe here to start/stop hold-to-talk
    /// (e.g. in App.OnStartup: _notch.EyesHeldChanged += held => _panel?.SetHoldToTalk(held);).
    /// </summary>
    public event Action<bool>? EyesHeldChanged;

    /// <summary>Nudge caption actions: false = "Back to it" (dismiss), true = "5 min break".</summary>
    public event Action<bool>? NudgeActionChosen;

    /// <summary>The spec in use (chosen face), for diagnostics.</summary>
    public NotchSpec Spec => _spec;

    /// <summary>Current resolved face state.</summary>
    public NotchFaceState FaceState => _state;

    /// <summary>Frames the render loop has processed since launch (perf diagnostics; 0 while hidden all along).</summary>
    internal long RenderedFrames => Interlocked.Read(ref _renderedFrames);

    /// <summary>True while CompositionTarget.Rendering is subscribed, i.e. the notch is visible or animating.</summary>
    internal bool IsRenderLoopActive => _rendering;

    /// <summary>How Windows draws the notch, as the running window reports it (perf diagnostics; must be SoftwareOnly).</summary>
    internal string RenderModeName =>
        _handle != IntPtr.Zero && HwndSource.FromHwnd(_handle)?.CompositionTarget is { } target ? target.RenderMode.ToString() : string.Empty;

    /// <summary>Frames the pacer dropped on high-refresh displays (perf diagnostics).</summary>
    internal long PacerSkippedFrames => _pacer.Skipped;

    public NotchWindow(Action onOpenPanel, Action onStop, Func<DisplayInfo?> selectedDisplay,
        NotchAppearance appearance, Action onDisplayLayoutChanged, Func<double>? verticalOffset = null,
        Func<bool>? appReducedMotion = null)
    {
        _onOpenPanel = onOpenPanel;
        _onStop = onStop;
        _selectedDisplay = selectedDisplay;
        _onDisplayLayoutChanged = onDisplayLayoutChanged;
        _verticalOffset = verticalOffset ?? (() => 0d);
        _appReducedMotion = appReducedMotion ?? (() => false);
        _appearance = appearance;
        _spec = LoadSpec();
        _face = _spec.Face;
        _timeline = new NotchRevealTimeline(_spec.Timings);
        _motion = new FaceMotion(_spec, new Random());
        _captionReveal = CaptionReveal.FromSpec(_spec);
        _reducedMotion = ComputeReducedMotion();
        _motion.ReducedMotion = _reducedMotion;
        _holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = PressHoldTracker.HoldThreshold };
        _holdTimer.Tick += HoldTimer_OnTick;
        // UI automation sees the resting face state from the start (NotchFaceState name, e.g. "Hidden").
        System.Windows.Automation.AutomationProperties.SetItemStatus(this, _state.ToString());

        _windowWidthDip = Enum.GetValues<NotchFaceState>().Max(s => _face.SizeFor(s).Width) + 2 * _face.Shape.Shoulder + 2 * ShakePadDip + 2 * InkPadDip;
        _windowWidthDip = Math.Max(_windowWidthDip, _spec.HoverStrip.Width);
        _windowHeightDip = Enum.GetValues<NotchFaceState>().Max(s => _face.SizeFor(s).Height) + DisclosureReserveDip;
        _sizeNow = _face.SizeFor(NotchFaceState.Hidden);
        _morph = new InkMorph(_sizeNow);
        _wake = new InkWakeUp(_face.SizeFor(NotchFaceState.Idle));
        _darkTheme = ReadDarkTheme();

        InitializeComponent();
        Width = _windowWidthDip;
        Height = _windowHeightDip;
        Root.Width = _windowWidthDip;
        Root.Height = _windowHeightDip;
        HoverStrip.Width = _spec.HoverStrip.Width;
        HoverStrip.Height = _spec.HoverStrip.Height;
        Canvas.SetLeft(HoverStrip, (_windowWidthDip - _spec.HoverStrip.Width) / 2);
        BuildFace();
        BuildIndicators();
        ApplyAppearance();
        LayoutBody(_sizeNow, InkBlot.Idle);

        SystemParameters.StaticPropertyChanged += SystemParameters_OnStaticPropertyChanged;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_OnUserPreferenceChanged;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => CaptureExternalTarget();
        IsVisibleChanged += (_, _) =>
        {
            CaptureExternalTarget();
            if (IsVisible)
            {
                Recompute();
            }
            else
            {
                // Hidden window: no render loop at all (idle CPU).
                StopRendering();
            }
        };
        Closed += (_, _) =>
        {
            StopRendering();
            _holdTimer.Stop();
            _flashTimer?.Stop();
            if (_press.Cancel())
            {
                // Keep EyesHeldChanged(true)/(false) paired even if the window closes mid-hold.
                EyesHeldChanged?.Invoke(false);
            }
            if (EmergencyStopRegistered)
            {
                UnregisterHotKey(_handle, EmergencyHotKeyId);
            }
            if (OpenShortcutRegistered)
            {
                UnregisterHotKey(_handle, OpenHotKeyId);
            }
            if (PauseAllRegistered)
            {
                UnregisterHotKey(_handle, PauseAllHotKeyId);
            }
            SystemParameters.StaticPropertyChanged -= SystemParameters_OnStaticPropertyChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        };
        Recompute();
    }

    public void ShowNotch()
    {
        if (!IsVisible)
        {
            Show();
        }
        PositionOnDisplay(_selectedDisplay());
    }

    public void PositionOnDisplay(DisplayInfo? display)
    {
        if (_positioning)
        {
            return;
        }

        _positioning = true;
        try
        {
            // Always lay out against the LIVE monitor (bounds, work area, its own DPI): the cached
            // selection can be stale after a rearrange, and the window's DPI is the monitor it is
            // on now, not the one it is about to move to. Unplugged selection -> primary.
            var monitor = MonitorLayout.Resolve(LiveMonitors.Snapshot(), display?.Id);
            if (monitor is null)
            {
                return;
            }

            var info = monitor.Display with { PixelBounds = MonitorLayout.NotchAnchor(monitor) };
            var bounds = WindowBounds(info);
            if (_handle == IntPtr.Zero)
            {
                _scale = info.Scale;
                Left = bounds.X / info.Scale;
                Top = bounds.Y / info.Scale;
                return;
            }

            PlacePhysical(bounds, info.Scale);
            var actualScale = GetDpiForWindow(_handle) / 96d;
            if (double.IsFinite(actualScale) && actualScale > 0 && Math.Abs(actualScale - info.Scale) > 0.01)
            {
                var actualInfo = info with { Scale = actualScale };
                PlacePhysical(WindowBounds(actualInfo), actualScale);
            }
        }
        finally
        {
            _positioning = false;
        }
    }

    /// <summary>
    /// App-level face signals (thinking, responding, nudge, focus paused, happy, user hidden). Capture and
    /// microphone also arrive through <see cref="SetCaptureActivity"/> / <see cref="SetMicrophoneActivity"/>
    /// and are OR-ed in. Revealed/Hovered are owned by this window and ignored here.
    /// </summary>
    public void UpdateFace(FaceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        _appInputs = inputs;
        SetCaption(inputs.Caption);
        Recompute();
    }

    public void SetStatus(string status)
    {
        _status = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        PublishStatus();
        UpdateDisclosure();
    }

    /// <summary>
    /// Riley's "Stopped" bubble: shows "Stopped" for ~1.6 s after a stop, then returns to the standing status
    /// (for example a shortcut-busy hint), which is never overwritten.
    /// </summary>
    public void ShowStoppedBriefly()
    {
        _flashStatus = "Stopped";
        if (_flashTimer is null)
        {
            _flashTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = StoppedFlash };
            _flashTimer.Tick += (_, _) =>
            {
                _flashTimer!.Stop();
                _flashStatus = null;
                PublishStatus();
            };
        }
        _flashTimer.Stop();
        _flashTimer.Start();
        PublishStatus();
    }

    private void PublishStatus()
    {
        // Status is never drawn on the collapsed notch; screen readers still get it.
        System.Windows.Automation.AutomationProperties.SetHelpText(this, _flashStatus ?? _status ?? string.Empty);
    }

    public void SetAppearance(NotchAppearance appearance)
    {
        _appearance = appearance;
        ApplyAppearance();
    }

    public void SetCaptureActivity(bool active, string? displayName)
    {
        _captureActive = active;
        _captureDisplayName = active ? displayName : null;
        Recompute();
    }

    public void SetMicrophoneActivity(bool active)
    {
        _microphoneActive = active;
        Recompute();
    }

    public void RestoreExternalFocus()
    {
        if (_lastExternalHandle != IntPtr.Zero && IsWindow(_lastExternalHandle)
            && GetWindowProcessId(_lastExternalHandle) == _lastExternalProcess)
        {
            SetForegroundWindow(_lastExternalHandle);
        }
    }

    // ---------------------------------------------------------------- spec

    private static NotchSpec LoadSpec()
    {
        // A spec deployed beside the exe wins (lets QA swap faces by editing chosen.face), else the embedded copy.
        var deployed = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "boo-notch-spec.json");
        if (File.Exists(deployed))
        {
            try
            {
                using var file = File.OpenRead(deployed);
                return NotchSpec.Load(file);
            }
            catch (Exception ex) when (ex is NotchSpecException or IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning($"Boo notch spec '{deployed}' is invalid ({ex.Message}); using the built-in face.");
            }
        }

        using var embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(SpecResourceName);
        if (embedded is null)
        {
            Trace.TraceWarning("Boo notch spec resource is missing; using the built-in face B.");
        }
        return NotchSpec.LoadOrDefault(embedded);
    }

    // ---------------------------------------------------------------- gallery (CI screenshots)

    /// <summary>
    /// Renders the notch in <paramref name="state"/> at time <paramref name="at"/> into a bitmap (no window shown).
    /// <paramref name="dark"/> draws on a dark desktop with the dark-theme rim; <paramref name="wakeMs"/> /
    /// <paramref name="celebrateMs"/> freeze the wake-up / celebrate sequence that many ms after it started.
    /// </summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderGalleryFrame(NotchFaceState state, TimeSpan at, double scale,
        bool dark = false, int? wakeMs = null, int? celebrateMs = null)
    {
        if (ReferenceEquals(Content, Root))
        {
            Content = null; // render the canvas standalone; the window is never shown in gallery mode
        }
        _darkTheme = dark;
        ApplyAppearance();
        _reducedMotion = false;
        _state = NotchFaceState.Hidden;
        if (state is NotchFaceState.Responding or NotchFaceState.Nudge)
        {
            _caption = state == NotchFaceState.Responding
                ? "Rule 1043 allows any source to DMZ-Web on all ports. I've circled Save so you can lock in the removal."
                : "Still on the Q4 rule review? 18 minutes left in this focus block.";
        }
        var start = at - TimeSpan.FromMilliseconds(4000);
        _motion = new FaceMotion(_spec, new Random(7)); // deterministic gallery
        _blink = new InkBlink(new Random(7));
        ApplyState(state, start);
        _reducedMotion = false; // render full motion even when the CI desktop has animations off
        _motion.ReducedMotion = false;
        _wake.Cancel();
        _celebrate.Cancel();
        if (wakeMs is { } w)
        {
            _wake.Start(at - TimeSpan.FromMilliseconds(w), reducedMotion: false);
        }
        if (celebrateMs is { } c)
        {
            _celebrate.Start(at - TimeSpan.FromMilliseconds(c), reducedMotion: false);
        }
        _blink.Restart(at); // no blink caught mid-way in a still
        NotchHost.Visibility = Visibility.Visible;
        RevealTranslate.Y = 0;
        RevealScale.ScaleY = 1;
        // Settle the eyes: pointer below-right of the notch, as if the user just moved it there.
        var jitter = 0;
        FaceFrame frame = default;
        for (var t = start; t <= at; t += TimeSpan.FromMilliseconds(16))
        {
            frame = _motion.Tick(t, new PointD(150 + (jitter++ % 2), 110));
        }
        DrawInk(at, frame);
        UpdateIndicators(at);
        UpdateCaption(at);
        UpdateDisclosure();
        HoverStrip.Visibility = Visibility.Collapsed;

        var pad = 12d;
        var reach = Math.Max(_sizeNow.Width, _face.SizeFor(state).Width);
        var left = BodyLeftDip(new SizeD(reach, 0)) - _face.Shape.Shoulder - pad - InkPadDip;
        var width = reach + 2 * (_face.Shape.Shoulder + pad + InkPadDip);
        var height = Math.Max(_sizeNow.Height, _face.SizeFor(state).Height) + pad + 16 + (ActiveDisclosure.Visibility == Visibility.Visible ? 44 : 0);
        // Detached canvas: force a full layout + render-data pass (twice, with a dispatcher turn between) so shapes whose
        // geometry changed this frame (the blot path) are re-rendered before capture, then render the canvas itself.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var element in new UIElement[] { Root, NotchHost, BodyPath, FaceLayer, InkLayer })
            {
                element.InvalidateMeasure();
                element.InvalidateArrange();
                element.InvalidateVisual();
            }
            Root.Measure(new System.Windows.Size(_windowWidthDip, _windowHeightDip));
            Root.Arrange(new Rect(0, 0, _windowWidthDip, _windowHeightDip));
            Root.UpdateLayout();
            Dispatcher.Invoke(static () => { }, System.Windows.Threading.DispatcherPriority.Render);
        }

        var full = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(_windowWidthDip * scale), (int)Math.Ceiling(_windowHeightDip * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        full.Render(Root);
        var x0 = (int)Math.Max(0, Math.Floor(left * scale));
        var crop = new Int32Rect(x0, 0,
            (int)Math.Min(full.PixelWidth - x0, Math.Ceiling(width * scale)),
            (int)Math.Min(full.PixelHeight, Math.Ceiling(height * scale)));
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(full, crop);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var desk = dark ? System.Windows.Media.Color.FromRgb(0x1B, 0x1C, 0x1F) : System.Windows.Media.Color.FromRgb(0xE4, 0xDD, 0xD1);
            dc.DrawRectangle(new SolidColorBrush(desk), null, new Rect(0, 0, crop.Width, crop.Height));
            dc.DrawImage(cropped, new Rect(0, 0, crop.Width, crop.Height));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    // ---------------------------------------------------------------- state

    private void Recompute()
    {
        var now = _clock.Elapsed;
        var app = _appInputs with
        {
            CaptureActive = _appInputs.CaptureActive || _captureActive,
            Listening = _appInputs.Listening || _microphoneActive,
            Revealed = false,
            Hovered = false,
        };

        // Anything above Hidden without the pointer (capture, mic, thinking, nudge, ...) pins the notch out.
        var pinned = FaceStateResolver.Resolve(app) != NotchFaceState.Hidden;
        _timeline.SetPinned(pinned, now);

        var phase = _timeline.PhaseAt(now);
        var state = FaceStateResolver.Resolve(app with { Revealed = phase != RevealPhase.Hidden, Hovered = _hovered });
        if (state != _state)
        {
            ApplyState(state, now);
        }
        UpdateDisclosure();
        EnsureRendering();
    }

    private void ApplyState(NotchFaceState state, TimeSpan now)
    {
        var wasHidden = _state == NotchFaceState.Hidden;
        var previous = _state;
        _state = state;
        PublishFaceState(previous);
        _stateSince = now;
        _reducedMotion = ComputeReducedMotion();
        _motion.ReducedMotion = _reducedMotion;
        _motion.SetState(state, now);

        // Living ink: morph blot, size and eyes toward the new mood. A new state always cuts the running morph
        // (it restarts from the pose on screen) and the wake-up; reduced motion and a fresh appearance jump to the end pose.
        var target = _face.SizeFor(state);
        var snap = _reducedMotion || wasHidden;
        _morph.Retarget(LivingInk.BlotFor(state), InkEyePose.For(LivingInk.MoodFor(state)), target, now, LivingInk.MorphMs(state), snap);
        if (state == NotchFaceState.Hidden)
        {
            _wake.Cancel();
            _hiddenSince = now;
        }
        else if (wasHidden)
        {
            var arrival = !_everShown || (_hiddenSince is { } since && now - since >= WakeAgainAfter);
            _everShown = true;
            if (arrival && state is NotchFaceState.Hover or NotchFaceState.Idle)
            {
                _wake.Start(now, _reducedMotion);
            }
            else
            {
                _wake.Cancel();
            }
            _blink.Restart(now);
        }
        else
        {
            _wake.Cancel();
        }
        if (state == NotchFaceState.Happy && previous != NotchFaceState.Happy)
        {
            _celebrate.Start(now, _reducedMotion);
        }
        else if (state != NotchFaceState.Happy)
        {
            _celebrate.Cancel();
        }
        if (snap)
        {
            _sizeNow = target;
        }

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        SpeakingBars.Visibility = Show(LivingInk.HasBars(state));
        RecordDot.Visibility = RecordRing.Visibility = StopSquare.Visibility = Show(state == NotchFaceState.Capture);
        var captioned = state is NotchFaceState.Responding or NotchFaceState.Nudge;
        Caption.Visibility = Show(captioned);
        NudgeActions.Visibility = Visibility.Collapsed; // shown once the caption has finished typing (studio)
        if (captioned)
        {
            RestartCaption(now);
        }
        _builtBlot = null; // slots changed with the state: re-place the face parts
        LayoutBody(_sizeNow, _morph.Sample(now).Blot);
    }

    private bool ComputeReducedMotion()
    {
        bool app;
        try
        {
            app = _appReducedMotion();
        }
        catch (InvalidOperationException)
        {
            app = false;
        }
        return MotionPreference.ReducedMotion(SystemParameters.ClientAreaAnimation, app);
    }

    // ---------------------------------------------------------------- render loop

    private void EnsureRendering()
    {
        if (_rendering || !IsLoadedAndVisible())
        {
            return;
        }
        var now = _clock.Elapsed;
        // Hidden face, hidden timeline: nothing can move, so no frames at all (idle CPU). A size tween that
        // is still pending while hidden is finished on the next reveal (ApplyState snaps it), not animated unseen.
        if (!NotchRenderPolicy.NeedsFrames(_timeline.PhaseAt(now), _state, sizeSettled: true))
        {
            NotchHost.Visibility = Visibility.Collapsed;
            return;
        }
        _rendering = true;
        _lastFrame = now;
        _pacer.Reset();
        NotchHost.Visibility = Visibility.Visible;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_rendering)
        {
            return;
        }
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private bool IsLoadedAndVisible() => _handle != IntPtr.Zero && IsVisible;

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        if (!_pacer.Accept(now))
        {
            return; // 144/240 Hz panel: the face is authored for ~60 fps, skip the in-between vsyncs.
        }
        Interlocked.Increment(ref _renderedFrames);
        var dt = now - _lastFrame;
        if (dt > TimeSpan.FromMilliseconds(100))
        {
            dt = TimeSpan.FromMilliseconds(100);
        }
        _lastFrame = now;

        var phase = _timeline.PhaseAt(now);
        var stateHidden = phase == RevealPhase.Hidden;
        if (stateHidden != (_state == NotchFaceState.Hidden))
        {
            Recompute();
        }

        ApplyReveal(phase, now);
        if (_state != NotchFaceState.Hidden)
        {
            DrawInk(now, _motion.Tick(now, PointerFromBodyCentre()));
            UpdateIndicators(now);
            UpdateCaption(now);
        }

        var settled = _morph.Sample(now).Settled && !_wake.Active && !_celebrate.Active;
        if (NotchRenderPolicy.MayStop(phase, _state, sizeSettled: settled))
        {
            NotchHost.Visibility = Visibility.Collapsed;
            StopRendering();
        }
    }

    private void ApplyReveal(RevealPhase phase, TimeSpan now)
    {
        var progress = _timeline.ProgressAt(now);
        double visible = phase switch
        {
            RevealPhase.Hidden => 0,
            _ when _reducedMotion => 1,
            RevealPhase.Revealing => CubicBezierEase.Reveal.Evaluate(progress),
            RevealPhase.Retracting => 1 - CubicBezierEase.Retract.Evaluate(1 - progress),
            _ => 1,
        };
        if (_state == NotchFaceState.Hidden && phase != RevealPhase.Retracting)
        {
            visible = 0;
        }
        else if (_wake.Active)
        {
            visible = 1; // Living ink wake-up: the stain grows in place instead of sliding down
        }

        var height = _sizeNow.Height + 2;
        SetIfChanged(RevealTranslate, TranslateTransform.YProperty, (Math.Min(visible, 1) - 1) * height);
        SetIfChanged(RevealScale, ScaleTransform.ScaleYProperty, Math.Max(visible, 1));

        var shake = _state == NotchFaceState.Nudge && !_reducedMotion
            ? FaceIndicators.NudgeShakeX((now - _stateSince).TotalMilliseconds)
            : 0;
        SetIfChanged(RevealTranslate, TranslateTransform.XProperty, shake);
    }

    /// <summary>Pointer relative to the notch body centre in DIP (studio: pointer minus the notch's bounding-box centre).</summary>
    private PointD? PointerFromBodyCentre()
    {
        if (!GetCursorPos(out var cursor) || _scale <= 0)
        {
            return null;
        }
        var centreX = BodyLeftDip(_sizeNow) + _sizeNow.Width / 2;
        var centreY = _sizeNow.Height / 2;
        return new PointD((cursor.X - _windowLeftPx) / _scale - centreX, (cursor.Y - _windowTopPx) / _scale - centreY);
    }

    /// <summary>
    /// One Living ink frame: morph (blot, size, eye pose), overridden by the wake-up while it runs, squashed by the
    /// celebrate beat, plus the soft lid blink; then the blot, eyes and loose ink are drawn. Shapes are only rebuilt
    /// when they change, so a resting face costs a few property compares per frame.
    /// </summary>
    private void DrawInk(TimeSpan now, FaceFrame frame)
    {
        var morph = _morph.Sample(now);
        var blot = morph.Blot;
        var pose = morph.Pose;
        var size = morph.Size;
        var bodyOpacity = 1d;
        var eyesVisible = true;
        var blinks = !_reducedMotion && !pose.Happy && _state != NotchFaceState.Hidden;
        if (_wake.Active)
        {
            var wake = _wake.At(now);
            if (wake.Phase != InkWakePhase.Done)
            {
                blot = wake.Blot;
                size = wake.Size;
                bodyOpacity = wake.Opacity;
                eyesVisible = wake.EyesVisible;
                pose = InkEyePose.For(InkMood.Curious) with { ScaleY = wake.EyeScaleY, Lid = wake.Lid };
                blinks = false;
            }
        }
        double splatterScale = 0, splatterOpacity = 0;
        if (_celebrate.Active)
        {
            var c = _celebrate.At(now);
            if (!c.Done)
            {
                size = new SizeD(size.Width * c.SquashX, size.Height * c.SquashY);
                splatterScale = c.SplatterScale;
                splatterOpacity = c.SplatterOpacity;
            }
        }
        if (blinks)
        {
            var lid = _blink.LidAt(now, sleepy: _state == NotchFaceState.Paused, reducedMotion: false);
            if (lid > pose.Lid)
            {
                pose = pose with { Lid = lid };
            }
        }

        _sizeNow = size;
        LayoutBody(size, blot);
        SetIfChanged(BodyPath, OpacityProperty, bodyOpacity);
        SetIfChanged(InkLayer, OpacityProperty, bodyOpacity);
        ApplyEyes(pose, frame, eyesVisible);
        UpdateLooseInk(now, size, morph.Progress, splatterScale, splatterOpacity);
        _talkLevel = frame.TalkLevel;
    }

    private double _talkLevel;

    private void ApplyEyes(InkEyePose pose, FaceFrame frame, bool visible)
    {
        var happy = pose.Happy;
        var eyes = visible && !happy ? Visibility.Visible : Visibility.Collapsed;
        LeftEye.Visibility = RightEye.Visibility = eyes;
        LeftHappy.Visibility = RightHappy.Visibility = visible && happy ? Visibility.Visible : Visibility.Collapsed;
        var bounce = frame.Bounce;
        SetIfChanged(LeftHappy, Canvas.TopProperty, -bounce);
        SetIfChanged(RightHappy, Canvas.TopProperty, -bounce);
        if (eyes != Visibility.Visible)
        {
            return;
        }

        var topRadius = Math.Round(pose.TopRadius, 3);
        if (topRadius != _builtTopRadius)
        {
            _builtTopRadius = topRadius;
            ShapeEye(_row.LeftEye, LeftEye, LeftWhite, topRadius);
            ShapeEye(_row.RightEye, RightEye, RightWhite, topRadius);
        }
        var pupilFill = pose.Alert >= 0.5 ? _alertBrush : _bodyBrush;
        ApplyEye(pose, frame, _row.LeftEye, LeftBlink, LeftShift, LeftPupil, LeftGlint, LeftLid, pupilFill);
        ApplyEye(pose, frame, _row.RightEye, RightBlink, RightShift, RightPupil, RightGlint, RightLid, pupilFill);
    }

    private void ApplyEye(InkEyePose pose, FaceFrame frame, PointD centre, ScaleTransform squash, TranslateTransform shift,
        Ellipse pupil, Ellipse shine, Rectangle lid, MediaBrush pupilFill)
    {
        var eye = _face.Eye;
        SetIfChanged(squash, ScaleTransform.ScaleXProperty, pose.ScaleX);
        SetIfChanged(squash, ScaleTransform.ScaleYProperty, pose.ScaleY);
        // The whole eye drifts 1 DIP with the gaze (life), and bounces when happy / talking.
        SetIfChanged(shift, TranslateTransform.XProperty, frame.Gaze.X);
        SetIfChanged(shift, TranslateTransform.YProperty, frame.Gaze.Y - frame.Bounce);

        // Look-direction: the pupil travels toward the pointer but never leaves the cream.
        var size = pose.Pupil * eye.Width;
        var travel = pose.PupilTravel;
        var px = centre.X + pose.PupilDx * eye.Width + frame.Gaze.X * travel * eye.Width;
        var py = centre.Y + pose.PupilDy * eye.Height + frame.Gaze.Y * travel * eye.Height;
        SetIfChanged(pupil, WidthProperty, size);
        SetIfChanged(pupil, HeightProperty, size);
        SetIfChanged(pupil, Canvas.LeftProperty, px - size / 2);
        SetIfChanged(pupil, Canvas.TopProperty, py - size / 2);
        if (!ReferenceEquals(pupil.Fill, pupilFill))
        {
            pupil.Fill = pupilFill;
        }
        SetIfChanged(shine, OpacityProperty, LivingInkPalette.ShineOpacity * pose.Shine);
        SetIfChanged(lid, HeightProperty, Math.Clamp(pose.Lid, 0, 1) * (eye.Height + 2));
    }

    private void UpdateLooseInk(TimeSpan now, SizeD size, double progress, double splatterScale, double splatterOpacity)
    {
        var grow = _reducedMotion ? 1 : Math.Clamp(progress, 0, 1);

        var blooms = LivingInk.HasBlooms(_state) ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i < _blooms.Length; i++)
        {
            var bloom = _blooms[i];
            bloom.Visibility = blooms;
            if (blooms != Visibility.Visible) continue;
            var spot = LivingInk.Blooms[i];
            var c = InkBlot.Map(new PointD(spot.X, spot.Y), size);
            var rx = spot.RadiusX * size.Width * grow;
            var ry = spot.RadiusY * size.Height * grow;
            SetIfChanged(bloom, WidthProperty, 2 * rx);
            SetIfChanged(bloom, HeightProperty, 2 * ry);
            SetIfChanged(bloom, Canvas.LeftProperty, c.X - rx);
            SetIfChanged(bloom, Canvas.TopProperty, c.Y - ry);
        }

        // Thinking: a drip forms under the blot (never with reduced motion) and three ink dots rise beside it.
        var drip = LivingInk.HasDrip(_state) && !_reducedMotion;
        _drip.Visibility = drip ? Visibility.Visible : Visibility.Collapsed;
        if (drip)
        {
            var top = InkBlot.Map(new PointD(LivingInk.DripX, 0.84), size).Y - 4;
            SetIfChanged(_drip, Canvas.LeftProperty, LivingInk.DripX * size.Width - LivingInk.DripWidth / 2);
            SetIfChanged(_drip, Canvas.TopProperty, top);
            SetIfChanged(_drip, HeightProperty, 4 + LivingInk.DripHeight * grow);
        }
        var dots = LivingInk.HasThinkDots(_state);
        var since = (now - _stateSince).TotalMilliseconds;
        for (var i = 0; i < _thinkDots.Length; i++)
        {
            var dot = _thinkDots[i];
            dot.Visibility = dots ? Visibility.Visible : Visibility.Collapsed;
            if (!dots) continue;
            var d = LivingInk.ThinkDotSizes[i];
            var (opacity, rise) = _reducedMotion ? (1d, 0d) : FaceIndicators.ThinkingDot(i, since);
            var bottom = _row.RowHeight * 0.5 - 7 * i;
            SetIfChanged(dot, Canvas.LeftProperty, size.Width + 1 + 9 * i);
            SetIfChanged(dot, Canvas.TopProperty, bottom - d + 2 * rise);
            SetIfChanged(dot, OpacityProperty, 0.35 + 0.65 * opacity);
        }

        // Focus done: a few splatters around the blot that fade (never with reduced motion: the celebrate does not run).
        for (var i = 0; i < _splatters.Length; i++)
        {
            var splatter = _splatters[i];
            var on = splatterOpacity > 0.01 && splatterScale > 0.01;
            splatter.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) continue;
            var spot = LivingInk.Splatters[i];
            var c = InkBlot.Map(new PointD(spot.X, spot.Y), size);
            var r = spot.RadiusX * splatterScale;
            // Pushed slightly outward so the marks sit around the blot, not inside it.
            var outward = spot.X < 0.5 ? -6 : 6;
            SetIfChanged(splatter, WidthProperty, 2 * r);
            SetIfChanged(splatter, HeightProperty, 2 * r);
            SetIfChanged(splatter, Canvas.LeftProperty, c.X + outward - r);
            SetIfChanged(splatter, Canvas.TopProperty, c.Y + 3 - r);
            SetIfChanged(splatter, OpacityProperty, splatterOpacity);
        }
    }

    private void UpdateIndicators(TimeSpan now)
    {
        var ms = now.TotalMilliseconds;
        var since = (now - _stateSince).TotalMilliseconds;
        var cy = _row.RowHeight / 2;
        switch (_state)
        {
            case NotchFaceState.Capture:
                // The red dot never fades; only its halo pulses (studio box-shadow ring).
                var (spread, alpha) = _reducedMotion ? (3d, 0.28) : FaceIndicators.RecordRing(since);
                var d = 7 + 2 * spread;
                SetIfChanged(RecordRing, WidthProperty, d);
                SetIfChanged(RecordRing, HeightProperty, d);
                SetIfChanged(RecordRing, Canvas.LeftProperty, _row.Lead.X + 3.5 - d / 2);
                SetIfChanged(RecordRing, Canvas.TopProperty, cy - d / 2);
                SetIfChanged(RecordRing, OpacityProperty, alpha);
                break;
            case NotchFaceState.Responding:
                // Four cream ink bars, bottoms aligned, keeping time with the voice level.
                for (var i = 0; i < _speakingBars.Length; i++)
                {
                    var h = LivingInk.BarHeight(i, ms, _talkLevel, _reducedMotion);
                    var bar = _speakingBars[i];
                    SetIfChanged(bar, HeightProperty, h);
                    SetIfChanged(bar, Canvas.TopProperty, cy + 9 - h);
                }
                break;
        }
    }

    // ---------------------------------------------------------------- caption

    private void SetCaption(string? caption)
    {
        var text = CaptionText(caption);
        var raw = CaptionStream.Normalize(caption);
        if (string.Equals(text, _caption, StringComparison.Ordinal))
        {
            _rawCaption = raw;
            return;
        }
        // Streaming speech grows the same caption: keep typing from where it is instead of restarting. Judged on the
        // full text (CaptionStream), not on the drawn window: past 160 characters the "…tail" window slides with every
        // delta and consecutive windows share no prefix, which used to blank and retype the caption on each one.
        var continues = CaptionStream.Continues(_rawCaption, raw);
        _caption = text;
        _rawCaption = raw;
        if (!continues)
        {
            RestartCaption(_clock.Elapsed);
        }
    }

    private bool _captionDone;

    /// <summary>The notch shows at most three caption lines: long replies keep their latest ~160 characters.</summary>
    internal static string CaptionText(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return string.Empty;
        }
        var text = string.Join(' ', caption.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const int max = 160;
        if (text.Length <= max)
        {
            return text;
        }
        var tail = text[^max..];
        var space = tail.IndexOf(' ');
        if (space > 0 && space < 40)
        {
            tail = tail[(space + 1)..];
        }
        if (char.IsLowSurrogate(tail[0]))
        {
            tail = tail[1..];
        }
        return "…" + tail;
    }

    private void RestartCaption(TimeSpan now)
    {
        _captionStart = now;
        _captionShown = string.Empty;
        _captionDone = false;
        Caption.Text = string.Empty;
        Caption.Opacity = _reducedMotion ? 1 : 0;
        NudgeActions.Visibility = Visibility.Collapsed; // pills reappear once the new text has typed
    }

    private void UpdateCaption(TimeSpan now)
    {
        if (Caption.Visibility != Visibility.Visible)
        {
            return;
        }
        var text = _caption.Length > 0 ? _caption : DefaultCaption(_state);
        if (_captionDone && string.Equals(_captionShown, text, StringComparison.Ordinal))
        {
            return; // fully typed: no per-frame work
        }
        var elapsed = now - _captionStart;
        var shown = _captionReveal.VisibleText(text, elapsed, _reducedMotion);
        if (!string.Equals(shown, _captionShown, StringComparison.Ordinal))
        {
            _captionShown = shown;
            Caption.Text = shown;
        }
        // Completion comes from the timeline, not from Caption.Opacity: SetIfChanged skips sub-0.01 steps, so a
        // frame landing at 0.99x would otherwise leave the opacity (and the nudge pills) stuck short of done.
        _captionDone = _captionReveal.IsComplete(text, elapsed, _reducedMotion);
        if (_captionDone)
        {
            Caption.Opacity = 1;
        }
        else
        {
            SetIfChanged(Caption, OpacityProperty, _captionReveal.OpacityAt(elapsed, _reducedMotion));
        }
        if (_captionDone && _state == NotchFaceState.Nudge)
        {
            NudgeActions.Visibility = Visibility.Visible;
        }
    }

    private static string DefaultCaption(NotchFaceState state)
        => state == NotchFaceState.Nudge ? "Still on it? Your focus block is running." : string.Empty;

    private void NudgeBack_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        NudgeActionChosen?.Invoke(false);
    }

    private void NudgeBreak_OnClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        NudgeActionChosen?.Invoke(true);
    }

    // ---------------------------------------------------------------- geometry

    private double BodyLeftDip(SizeD size) => (_windowWidthDip - size.Width) / 2;

    private void LayoutBody(SizeD size, InkBlot blot)
    {
        if (size == _builtSize && ReferenceEquals(blot, _builtBlot) && _state == _builtState)
        {
            return; // nothing moved: no geometry, clip or layout work this frame
        }
        var left = BodyLeftDip(size);
        BodyPath.Data = BuildBlot(blot, size);
        _builtSize = size;
        _builtBlot = blot;
        _builtState = _state;
        Canvas.SetLeft(BodyPath, left);
        Canvas.SetTop(BodyPath, 0);
        Canvas.SetLeft(InkLayer, left);
        Canvas.SetTop(InkLayer, 0);
        Canvas.SetLeft(FaceLayer, left);
        Canvas.SetTop(FaceLayer, 0);
        Canvas.SetLeft(OpenButton, left);
        Canvas.SetTop(OpenButton, 0);
        OpenButton.Width = size.Width;
        OpenButton.Height = size.Height;

        _row = LivingInk.Place(_face, size, _state);
        _parts = new NotchFaceParts(_row.LeftEye, _row.RightEye, _row.Trail);
        PositionEye(_row.LeftEye, LeftEye, LeftBlink, LeftWhite, LeftPupil, LeftGlint, LeftLid, LeftHappy);
        PositionEye(_row.RightEye, RightEye, RightBlink, RightWhite, RightPupil, RightGlint, RightLid, RightHappy);
        _builtTopRadius = double.NaN; // eye outlines follow the new centres
        PositionIndicators();
        PositionCaption(left);
        // Studio ".clip{overflow:hidden}": caption and pills never draw outside the pocket while it resizes.
        var bodyClip = new RectangleGeometry(new Rect(0, 0, size.Width, size.Height));
        bodyClip.Freeze();
        FaceLayer.Clip = bodyClip;
        var pillsClip = new RectangleGeometry(new Rect(-Canvas.GetLeft(NudgeActions) + left, -Canvas.GetTop(NudgeActions), size.Width, size.Height));
        pillsClip.Freeze();
        NudgeActions.Clip = pillsClip;
        PositionDisclosure(size);
    }

    /// <summary>The ink blot (Boo.Core InkBlot) as a WPF path: a top line flush with the screen edge and six curves.</summary>
    private static Geometry BuildBlot(InkBlot blot, SizeD size)
    {
        var p = blot.Points(size);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(p[0].X, p[0].Y), isFilled: true, isClosed: true);
            ctx.LineTo(new System.Windows.Point(p[1].X, p[1].Y), true, true);
            for (var c = 0; c < 6; c++)
            {
                ctx.BezierTo(
                    new System.Windows.Point(p[2 + 3 * c].X, p[2 + 3 * c].Y),
                    new System.Windows.Point(p[3 + 3 * c].X, p[3 + 3 * c].Y),
                    new System.Windows.Point(p[4 + 3 * c].X, p[4 + 3 * c].Y), true, true);
            }
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Eye outline: an ellipse whose upper and lower halves have different heights (CSS border-radius
    /// "50% 50% x x / t t b b"); <paramref name="topRadius"/> is the upper share. The eye canvas is clipped to it,
    /// so the pupil and the lid never leave the cream.
    /// </summary>
    private void ShapeEye(PointD c, Canvas eyeCanvas, System.Windows.Shapes.Path white, double topRadius)
    {
        var eye = _face.Eye;
        var w = eye.Width;
        var h = eye.Height;
        var top = c.Y - h / 2;
        var rt = Math.Clamp(topRadius, 0.2, 0.8) * h;
        var rb = h - rt;
        var mid = top + rt;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(c.X - w / 2, mid), true, true);
            ctx.ArcTo(new System.Windows.Point(c.X, top), new System.Windows.Size(w / 2, rt), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new System.Windows.Point(c.X + w / 2, mid), new System.Windows.Size(w / 2, rt), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new System.Windows.Point(c.X, top + h), new System.Windows.Size(w / 2, rb), 0, false, SweepDirection.Clockwise, true, true);
            ctx.ArcTo(new System.Windows.Point(c.X - w / 2, mid), new System.Windows.Size(w / 2, rb), 0, false, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        white.Data = geometry;
        eyeCanvas.Clip = geometry;
    }

    private void BuildFace()
    {
        foreach (var glint in new[] { LeftGlint, RightGlint })
        {
            glint.Width = glint.Height = 0.28 * _face.Eye.Width;
        }
    }

    private void PositionEye(PointD centre, Canvas eyeCanvas, ScaleTransform squash, System.Windows.Shapes.Path white, Ellipse pupil,
        Ellipse shine, Rectangle lid, System.Windows.Shapes.Path happy)
    {
        var eye = _face.Eye;
        Canvas.SetLeft(eyeCanvas, 0);
        Canvas.SetTop(eyeCanvas, 0);
        // A real box (not 0×0) so WPF lays out and renders the scaled, clipped eye correctly.
        eyeCanvas.Width = centre.X + eye.Width;
        eyeCanvas.Height = centre.Y + eye.Height;
        squash.CenterX = centre.X;
        squash.CenterY = centre.Y;
        // Shine: upper-left of the eye, 28 % of it (living-ink.css .eye .shine).
        Canvas.SetLeft(shine, centre.X - eye.Width / 2 + 0.22 * eye.Width);
        Canvas.SetTop(shine, centre.Y - eye.Height / 2 + 0.18 * eye.Height);
        // Lid: ink, grows down from the top of the eye.
        lid.Width = eye.Width + 2;
        Canvas.SetLeft(lid, centre.X - eye.Width / 2 - 1);
        Canvas.SetTop(lid, centre.Y - eye.Height / 2 - 1);
        Canvas.SetLeft(pupil, centre.X - eye.Pupil / 2);
        Canvas.SetTop(pupil, centre.Y - eye.Pupil / 2);
        // Happy squint: one cream smile arc per eye (board svg "M4 4 Q18 22 32 4" in the eye box).
        var w = eye.Width;
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(centre.X - 0.44 * w, centre.Y - 0.16 * w), false, false);
            ctx.QuadraticBezierTo(new System.Windows.Point(centre.X, centre.Y + 0.40 * w), new System.Windows.Point(centre.X + 0.44 * w, centre.Y - 0.16 * w), true, true);
        }
        arc.Freeze();
        happy.Data = arc;
        happy.StrokeThickness = Math.Max(2, w * 0.1);
        _ = white;
    }

    private void BuildIndicators()
    {
        for (var i = 0; i < _speakingBars.Length; i++)
        {
            var bar = new Rectangle { Width = LivingInk.BarWidth, Height = 4, RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetLeft(bar, i * (LivingInk.BarWidth + LivingInk.BarGap));
            _speakingBars[i] = bar;
            SpeakingBars.Children.Add(bar);
        }
        for (var i = 0; i < _blooms.Length; i++)
        {
            _blooms[i] = new Ellipse { Visibility = Visibility.Collapsed };
            InkLayer.Children.Add(_blooms[i]);
        }
        _drip.Visibility = Visibility.Collapsed;
        InkLayer.Children.Add(_drip);
        for (var i = 0; i < _thinkDots.Length; i++)
        {
            var d = LivingInk.ThinkDotSizes[i];
            _thinkDots[i] = new Ellipse { Width = d, Height = d, Visibility = Visibility.Collapsed };
            InkLayer.Children.Add(_thinkDots[i]);
        }
        for (var i = 0; i < _splatters.Length; i++)
        {
            _splatters[i] = new Ellipse { Visibility = Visibility.Collapsed };
            InkLayer.Children.Add(_splatters[i]);
        }
        RecordDot.Width = RecordDot.Height = 7;
    }

    private void PositionIndicators()
    {
        // Lead slot (record dot) left of the eyes, trail slot (speaking bars, stop square) right-aligned.
        var cy = _row.RowHeight / 2;
        Canvas.SetLeft(SpeakingBars, _row.Trail.X);
        Canvas.SetTop(SpeakingBars, 0);
        Canvas.SetLeft(RecordDot, _row.Lead.X);
        Canvas.SetTop(RecordDot, cy - 3.5);
        Canvas.SetLeft(StopSquare, _row.Trail.X);
        Canvas.SetTop(StopSquare, cy - 9);
    }

    private void PositionCaption(double bodyLeft)
    {
        var area = _row.Caption;
        Canvas.SetLeft(Caption, area.X);
        Canvas.SetTop(Caption, area.Y);
        Caption.Width = Math.Max(0, area.Width);
        var actions = _state == NotchFaceState.Nudge ? 30 : 0;
        Caption.MaxHeight = Math.Max(18, Math.Floor((area.Height - actions) / 18) * 18);
        // Nudge pills sit 8 DIP under two caption lines (studio .acts margin-top 8).
        Canvas.SetLeft(NudgeActions, bodyLeft + area.X);
        Canvas.SetTop(NudgeActions, area.Y + 36 + 8);
    }

    private void PositionDisclosure(SizeD size)
    {
        var width = Math.Min(_windowWidthDip - 2 * ShakePadDip, Math.Max(size.Width, 200));
        ActiveDisclosure.Width = width;
        Canvas.SetLeft(ActiveDisclosure, (_windowWidthDip - width) / 2);
        Canvas.SetTop(ActiveDisclosure, size.Height + 6);
    }

    private RectD WindowBounds(DisplayInfo info)
    {
        // The pocket's physical rect for the idle state comes from NotchLayout (spec DIP x monitor scale,
        // centred, top edge at the screen top); the transparent window is centred on the same x.
        var scale = double.IsFinite(info.Scale) && info.Scale > 0 ? info.Scale : 1;
        var body = NotchLayout.Bounds(_face, NotchFaceState.Idle, info.PixelBounds, scale);
        var width = _windowWidthDip * scale;
        var height = _windowHeightDip * scale;
        var centreX = body.X + body.Width / 2;
        return new RectD(centreX - width / 2, body.Y + _verticalOffset() * scale, width, height);
    }

    private void PlacePhysical(RectD bounds, double scale)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        _scale = MonitorLayout.SanitizeScale(scale);
        // Snap edges (round-half-up) so a fractional-scale notch is not shifted a pixel
        // differently on negative-origin monitors than on the primary.
        var snapped = MonitorLayout.SnapToPixels(bounds);
        _windowLeftPx = snapped.X;
        _windowTopPx = snapped.Y;
        SetWindowPos(_handle, IntPtr.Zero,
            (int)snapped.X,
            (int)snapped.Y,
            (int)snapped.Width,
            (int)snapped.Height,
            SwpNoActivate | SwpNoZOrder);
    }

    // ---------------------------------------------------------------- disclosure

    /// <summary>UI automation: ItemStatus = the current NotchFaceState name ("Hidden", "Hover", "Idle", ...).</summary>
    private void PublishFaceState(NotchFaceState previous)
    {
        var name = _state.ToString();
        System.Windows.Automation.AutomationProperties.SetItemStatus(this, name);
        if (previous != _state && System.Windows.Automation.Peers.AutomationPeer.ListenerExists(System.Windows.Automation.Peers.AutomationEvents.PropertyChanged))
        {
            System.Windows.Automation.Peers.UIElementAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
                System.Windows.Automation.AutomationElementIdentifiers.ItemStatusProperty, previous.ToString(), name);
        }
    }

    private void UpdateDisclosure()
    {
        var activity = new List<string>();
        if (_captureActive)
        {
            activity.Add($"Screen shared · {_captureDisplayName ?? "selected display"}");
        }
        if (_microphoneActive)
        {
            activity.Add("Microphone active");
        }
        // Only the privacy disclosures (screen shared / microphone) are drawn under the notch; status text is for screen
        // readers (HelpText) and the full window. Captions live inside the notch (responding / nudge).
        var collapsed = _state is NotchFaceState.Hidden or NotchFaceState.Hover or NotchFaceState.Idle;

        ActiveDisclosureText.Text = string.Join(Environment.NewLine, activity);
        ActiveDisclosure.Visibility = activity.Count == 0 || collapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---------------------------------------------------------------- input

    private void OpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        // Reached through UI Automation Invoke; mouse clicks are classified in the preview handlers below.
        OpenPanelFromNotch();
    }

    private void OpenPanelFromNotch()
    {
        CaptureExternalTarget();
        _onOpenPanel();
    }

    private void OpenButton_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _press.Down(_clock.Elapsed);
        OpenButton.CaptureMouse();
        _holdTimer.Stop();
        _holdTimer.Start();
        e.Handled = true;
    }

    private void OpenButton_OnPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _holdTimer.Stop();
        var outcome = _press.Up(_clock.Elapsed);
        if (OpenButton.IsMouseCaptured)
        {
            OpenButton.ReleaseMouseCapture();
        }
        e.Handled = true;
        switch (outcome)
        {
            case PressOutcome.Click:
                OpenPanelFromNotch();
                break;
            case PressOutcome.HoldEnded:
                EyesHeldChanged?.Invoke(false);
                break;
        }
    }

    private void OpenButton_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _holdTimer.Stop();
        if (_press.Cancel())
        {
            EyesHeldChanged?.Invoke(false);
        }
    }

    private void HoldTimer_OnTick(object? sender, EventArgs e)
    {
        _holdTimer.Stop();
        if (_press.Tick(_clock.Elapsed))
        {
            EyesHeldChanged?.Invoke(true);
        }
        else if (_press.IsPressed && !_press.IsHeld)
        {
            _holdTimer.Interval = TimeSpan.FromMilliseconds(15);
            _holdTimer.Start();
            return;
        }
        _holdTimer.Interval = PressHoldTracker.HoldThreshold;
    }

    private void NotchWindow_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovered = true;
        _timeline.PointerEntered(_clock.Elapsed);
        Recompute();
    }

    private void NotchWindow_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _hovered = false;
        _timeline.PointerLeft(_clock.Elapsed);
        Recompute();
    }

    private void NotchSurface_OnMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _onStop();
        e.Handled = true;
    }

    /// <summary>
    /// Retries any global hotkey another app held earlier (it may have exited). Called on session unlock and display
    /// changes; cheap and idempotent. Returns true when something new was registered.
    /// </summary>
    public bool RetryHotkeys()
    {
        if (_handle == IntPtr.Zero || (EmergencyStopRegistered && OpenShortcutRegistered && PauseAllRegistered))
        {
            return false;
        }

        var before = (EmergencyStopRegistered, OpenShortcutRegistered, PauseAllRegistered);
        RegisterMissingHotkeys();
        return before != (EmergencyStopRegistered, OpenShortcutRegistered, PauseAllRegistered);
    }

    private void RegisterMissingHotkeys()
    {
        if (!EmergencyStopRegistered)
        {
            EmergencyStopRegistered = RegisterHotKey(_handle, EmergencyHotKeyId, ModControl | ModAlt, EmergencyKey);
        }
        if (!OpenShortcutRegistered)
        {
            OpenShortcutRegistered = RegisterHotKey(_handle, OpenHotKeyId, ModControl | ModAlt, 0x42);
        }
        if (!PauseAllRegistered)
        {
            PauseAllRegistered = RegisterHotKey(_handle, PauseAllHotKeyId, ModControl | ModAlt, PauseAllKey);
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(_handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(_handle, GwlExstyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
        var source = HwndSource.FromHwnd(_handle);
        source?.AddHook(EmergencyWindowProc);
        // The notch is a small see-through window that stays on screen all day. Drawn on the graphics card it kept
        // ~35 MB of driver memory from launch plus ~35-50 MB more once the face had come out; drawn in software it
        // costs neither, with the same frame rate and CPU (measured 2026-11-12, docs/DECISIONS.md). Only this window:
        // the panel and the annotation overlay keep the graphics card.
        if (source?.CompositionTarget is { } target)
        {
            target.RenderMode = RenderMode.SoftwareOnly;
        }
        RegisterMissingHotkeys();
        // One combined, truthful hint (two separate SetStatus calls used to overwrite each other); App refines it with the hold-to-talk result.
        if (new ShortcutStatus(EmergencyStopRegistered, OpenShortcutRegistered, HoldToTalk: true, PauseAllRegistered).NotchMessage is { } shortcutHint)
        {
            SetStatus(shortcutHint);
        }
        CaptureExclusionAvailable = SetWindowDisplayAffinity(_handle, App.CaptureExclusion.DisplayAffinity); // WDA_EXCLUDEFROMCAPTURE unless BOO_QA_CAPTURABLE=1
        if (!CaptureExclusionAvailable)
        {
            SetStatus("Capture paused");
        }
        PositionOnDisplay(_selectedDisplay());
        Recompute();
    }

    // ---------------------------------------------------------------- appearance

    private void ApplyAppearance()
    {
        MediaBrush body;
        MediaBrush eyes;
        MediaBrush? rim;
        var highContrast = SystemParameters.HighContrast;
        if (highContrast)
        {
            body = MediaSystemColors.WindowBrush;
            eyes = MediaSystemColors.WindowTextBrush;
            rim = MediaSystemColors.WindowTextBrush;
        }
        else
        {
            // Living ink: the untouched default draws ink + cream; colours the user picked are kept. The blot stays ink in
            // both Windows themes; a dark desktop only adds the very quiet light rim so the drop keeps an edge.
            var palette = LivingInkPalette.Resolve(_appearance);
            body = ToBrush(palette.BodyColor, MediaBrushes.Black);
            eyes = ToBrush(palette.EyeColor, MediaBrushes.White);
            rim = LivingInkPalette.RimFor(_darkTheme) is { } r ? ToBrush(r, MediaBrushes.Transparent) : null;
        }
        _bodyBrush = body;
        _alertBrush = highContrast ? MediaSystemColors.HighlightBrush : ToBrush(LivingInkPalette.Alert, MediaBrushes.DarkRed);
        BodyPath.Fill = body;
        BodyPath.Stroke = rim;
        BodyPath.StrokeThickness = rim is null ? 0 : 1;
        foreach (var loose in _blooms.Concat(_thinkDots).Concat(_splatters).Cast<Shape>().Append(_drip))
        {
            loose.Fill = body;
            loose.Stroke = rim;
            loose.StrokeThickness = rim is null ? 0 : 1;
        }
        LeftWhite.Fill = RightWhite.Fill = eyes;
        LeftGlint.Fill = RightGlint.Fill = highContrast ? eyes : MediaBrushes.White;
        LeftHappy.Stroke = RightHappy.Stroke = eyes;
        LeftPupil.Fill = RightPupil.Fill = body;
        LeftLid.Fill = RightLid.Fill = body;
        foreach (var bar in _speakingBars)
        {
            bar.Fill = eyes;
        }
        Caption.Foreground = eyes;
        StopSquareInner.Background = eyes;
        StopSquare.Background = WithAlpha(eyes, 0.16);
        // Studio pills: primary = eye colour with body-coloured text; secondary = eye colour at 14%.
        NudgeBackButton.Background = eyes;
        NudgeBackButton.Foreground = body;
        NudgeBreakButton.Background = WithAlpha(eyes, 0.14);
        NudgeBreakButton.Foreground = eyes;
        ActiveDisclosure.Background = highContrast ? MediaSystemColors.WindowBrush : MediaBrushes.White;
        ActiveDisclosure.BorderBrush = highContrast ? MediaSystemColors.ActiveBorderBrush : MediaBrushes.Black;
        ActiveDisclosureText.Foreground = highContrast ? MediaSystemColors.WindowTextBrush : MediaBrushes.Black;
    }

    /// <summary>Windows "Choose your app mode": dark when HKCU ...\Themes\Personalize AppsUseLightTheme is 0.</summary>
    private static bool ReadDarkTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SystemEvents_OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color))
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            var dark = ReadDarkTheme();
            if (dark != _darkTheme)
            {
                _darkTheme = dark;
                ApplyAppearance();
            }
        });
    }

    private void SystemParameters_OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            ApplyAppearance();
        }
        else if (string.Equals(e.PropertyName, nameof(SystemParameters.ClientAreaAnimation), StringComparison.Ordinal))
        {
            _reducedMotion = ComputeReducedMotion();
            _motion.ReducedMotion = _reducedMotion;
            if (_reducedMotion)
            {
                // Animation effects turned off mid-sequence: jump to the end pose.
                _wake.Cancel();
                _celebrate.Cancel();
            }
        }
    }

    private static MediaBrush WithAlpha(MediaBrush brush, double alpha)
    {
        if (brush is not SolidColorBrush solid)
        {
            return brush;
        }
        var c = solid.Color;
        var result = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)Math.Round(255 * alpha), c.R, c.G, c.B));
        result.Freeze();
        return result;
    }

    private static MediaBrush ToBrush(string value, MediaBrush fallback)
    {
        try
        {
            var brush = (MediaBrush)new BrushConverter().ConvertFromString(value)!;
            brush.Freeze();
            return brush;
        }
        catch (Exception) when (value is not null)
        {
            return fallback;
        }
    }

    private static MediaBrush Frozen(MediaBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    private static void SetIfChanged(DependencyObject target, DependencyProperty property, double value)
    {
        var current = target.GetValue(property);
        if (current is double d && (Math.Abs(d - value) < 0.01 || (double.IsNaN(d) && double.IsNaN(value))))
        {
            return;
        }
        target.SetValue(property, value);
    }

    // ---------------------------------------------------------------- focus restore / Win32

    private void CaptureExternalTarget()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _handle
            || GetWindowProcessId(foreground) == Environment.ProcessId
            || IsOwnedWindow(foreground))
        {
            return;
        }

        _lastExternalHandle = foreground;
        _lastExternalProcess = GetWindowProcessId(foreground);
    }

    private bool IsOwnedWindow(IntPtr candidate)
    {
        return candidate == _handle || (GetWindow(candidate, 4) == _handle);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }
        return IntPtr.Zero;
    }

    private IntPtr EmergencyWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmDisplayChange or WmDpiChanged)
        {
            _onDisplayLayoutChanged();
            PositionOnDisplay(_selectedDisplay());
            if (msg == WmDpiChanged)
            {
                // WPF applies the WM_DPICHANGED suggested rect after this hook returns (handled = false),
                // which would undo the placement above; place again once WPF is done.
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => PositionOnDisplay(_selectedDisplay()));
            }
            handled = false;
            return IntPtr.Zero;
        }
        if (msg == WmHotKey && wParam.ToInt32() == OpenHotKeyId)
        {
            CaptureExternalTarget();
            _onOpenPanel();
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WmHotKey && wParam.ToInt32() is EmergencyHotKeyId or PauseAllHotKeyId)
        {
            // Ctrl+Alt+F12 and Ctrl+Alt+S run the same StopAll.
            _onStop();
            ShowStoppedBriefly();
            handled = true;
            return IntPtr.Zero;
        }
        return WindowProc(hwnd, msg, wParam, lParam, ref handled);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static int GetWindowProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var processId);
        return unchecked((int)processId);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
