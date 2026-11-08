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
/// The notch: a black "HeyClicky pocket" at the top centre with living button eyes (face lane U1/U2).
/// All sizes and timings come from docs/design/boo-notch-spec.json (chosen face); the behaviour lives in
/// Boo.Core (FaceStateResolver, GazeSolver/GazeSpring, BlinkScheduler, NotchLayout, NotchRevealTimeline)
/// and this class is the thin WPF/Win32 glue. Compiled only in the Linux container: not verified on Windows.
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
    private static readonly MediaBrush PupilBrush = Frozen(new SolidColorBrush(Colors.Black));

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
    private readonly Rectangle[] _listeningBars = new Rectangle[FaceIndicators.BarCount];
    private readonly Ellipse[] _thinkingDots = new Ellipse[FaceIndicators.DotCount];
    private readonly double _windowWidthDip;
    private readonly double _windowHeightDip;
    private NotchAppearance _appearance = new();
    private NotchRowParts _row = new(default, default, default, default, default, 0);
    private string _caption = string.Empty;
    private string _captionShown = string.Empty;
    private TimeSpan _captionStart;
    private FaceInputs _appInputs = new();
    private bool _hovered;
    private NotchFaceState _state = NotchFaceState.Hidden;
    private TimeSpan _stateSince;
    private SizeD _sizeFrom;
    private SizeD _sizeTo;
    private SizeD _sizeNow;
    private SizeD _builtSize;
    private TimeSpan _resizeStart;
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

        _windowWidthDip = Enum.GetValues<NotchFaceState>().Max(s => _face.SizeFor(s).Width) + 2 * _face.Shape.Shoulder + 2 * ShakePadDip;
        _windowWidthDip = Math.Max(_windowWidthDip, _spec.HoverStrip.Width);
        _windowHeightDip = Enum.GetValues<NotchFaceState>().Max(s => _face.SizeFor(s).Height) + DisclosureReserveDip;
        _sizeFrom = _sizeTo = _sizeNow = _face.SizeFor(NotchFaceState.Hidden);

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
        LayoutBody(_sizeNow);

        SystemParameters.StaticPropertyChanged += SystemParameters_OnStaticPropertyChanged;
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

    /// <summary>Renders the notch in <paramref name="state"/> at time <paramref name="at"/> into a bitmap (no window shown).</summary>
    internal System.Windows.Media.Imaging.BitmapSource RenderGalleryFrame(NotchFaceState state, TimeSpan at, double scale)
    {
        if (ReferenceEquals(Content, Root))
        {
            Content = null; // render the canvas standalone; the window is never shown in gallery mode
        }
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
        ApplyState(state, start);
        _reducedMotion = false; // render full motion even when the CI desktop has animations off
        _motion.ReducedMotion = false;
        _sizeFrom = _sizeTo = _sizeNow = _face.SizeFor(state);
        LayoutBody(_sizeNow);
        NotchHost.Visibility = Visibility.Visible;
        RevealTranslate.Y = 0;
        RevealScale.ScaleY = 1;
        // Settle the eyes: pointer below-right of the notch, as if the user just moved it there.
        // Pointer keeps moving slightly (no idle wander) below-right; skip ahead past any blink so eyes are open.
        var jitter = 0;
        FaceFrame frame = default;
        var end = at;
        for (var t = start; t <= end; t += TimeSpan.FromMilliseconds(16))
        {
            frame = _motion.Tick(t, new PointD(150 + (jitter++ % 2), 110));
            var mood = FaceMoods.For(state, t.TotalMilliseconds);
            if (t + TimeSpan.FromMilliseconds(16) > end && mood.Blinks && frame.Openness < mood.Open * 0.97 && end < at + TimeSpan.FromMilliseconds(600))
            {
                end += TimeSpan.FromMilliseconds(16);
            }
        }
        ApplyFrame(frame);
        at = end;
        UpdateIndicators(at);
        UpdateCaption(at);
        UpdateDisclosure();
        HoverStrip.Visibility = Visibility.Collapsed;

        var pad = 12d;
        var left = BodyLeftDip(_sizeNow) - _face.Shape.Shoulder - pad;
        var width = _sizeNow.Width + 2 * (_face.Shape.Shoulder + pad);
        var height = _sizeNow.Height + pad + (ActiveDisclosure.Visibility == Visibility.Visible ? 44 : 0);
        // Detached canvas: force a full layout + render-data pass (twice, with a dispatcher turn between) so shapes whose
        // geometry changed this frame (the pocket path) are re-rendered before capture, then render the canvas itself.
        for (var pass = 0; pass < 2; pass++)
        {
            foreach (var element in new UIElement[] { Root, NotchHost, BodyPath, FaceLayer })
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
        var crop = new Int32Rect((int)Math.Max(0, Math.Floor(left * scale)), 0,
            (int)Math.Min(full.PixelWidth - Math.Max(0, Math.Floor(left * scale)), Math.Ceiling(width * scale)),
            (int)Math.Min(full.PixelHeight, Math.Ceiling(height * scale)));
        var cropped = new System.Windows.Media.Imaging.CroppedBitmap(full, crop);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF3, 0xF1)), null, new Rect(0, 0, crop.Width, crop.Height));
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

        _sizeFrom = _sizeNow;
        _sizeTo = _face.SizeFor(state);
        _resizeStart = now;
        if (_reducedMotion || wasHidden)
        {
            _sizeFrom = _sizeTo;
            _sizeNow = _sizeTo;
        }

        var happy = state == NotchFaceState.Happy;
        var showPupils = !happy && _face.Eye.Pupil > 0;
        foreach (var white in new[] { LeftWhite, RightWhite })
        {
            white.Visibility = happy ? Visibility.Collapsed : Visibility.Visible;
        }
        foreach (var part in new UIElement[] { LeftPupil, RightPupil, LeftGlint, RightGlint })
        {
            part.Visibility = showPupils ? Visibility.Visible : Visibility.Collapsed;
        }
        LeftHappy.Visibility = RightHappy.Visibility = happy ? Visibility.Visible : Visibility.Collapsed;

        static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        ListeningBars.Visibility = Show(state is NotchFaceState.Listening or NotchFaceState.Responding);
        ThinkingDots.Visibility = Show(state == NotchFaceState.Thinking);
        RecordDot.Visibility = RecordRing.Visibility = StopSquare.Visibility = Show(state == NotchFaceState.Capture);
        PauseBars.Visibility = Show(state == NotchFaceState.Paused);
        var captioned = state is NotchFaceState.Responding or NotchFaceState.Nudge;
        Caption.Visibility = Show(captioned);
        NudgeActions.Visibility = Visibility.Collapsed; // shown once the caption has finished typing (studio)
        if (captioned)
        {
            RestartCaption(now);
        }
        LayoutBody(_sizeNow);
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
        return !SystemParameters.ClientAreaAnimation || app;
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

        // Resize (sideways growth from the centre) over resizeMs.
        if (_sizeNow != _sizeTo)
        {
            var t = (now - _resizeStart).TotalMilliseconds / _spec.Timings.ResizeMs;
            if (_reducedMotion || t >= 1)
            {
                _sizeNow = _sizeTo;
            }
            else
            {
                var k = CubicBezierEase.Resize.Evaluate(t);
                _sizeNow = new SizeD(
                    _sizeFrom.Width + (_sizeTo.Width - _sizeFrom.Width) * k,
                    _sizeFrom.Height + (_sizeTo.Height - _sizeFrom.Height) * k);
            }
            LayoutBody(_sizeNow);
        }

        ApplyReveal(phase, now);
        if (_state != NotchFaceState.Hidden)
        {
            ApplyFrame(_motion.Tick(now, PointerFromBodyCentre()));
            UpdateIndicators(now);
            UpdateCaption(now);
        }

        if (NotchRenderPolicy.MayStop(phase, _state, sizeSettled: _sizeNow == _sizeTo))
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

    private void ApplyFrame(FaceFrame frame)
    {
        ApplyEye(frame, _row.LeftEye, LeftEye, LeftBlink, LeftShift, LeftPupil, LeftGlint);
        ApplyEye(frame, _row.RightEye, RightEye, RightBlink, RightShift, RightPupil, RightGlint);
        _talkLevel = frame.TalkLevel;
    }

    private double _talkLevel;

    private void ApplyEye(FaceFrame frame, PointD centre, Canvas eyeCanvas, ScaleTransform blink, TranslateTransform shift, Ellipse pupil, Ellipse glint)
    {
        SetIfChanged(blink, ScaleTransform.ScaleYProperty, frame.Happy ? 1 : frame.Openness);
        SetIfChanged(shift, TranslateTransform.XProperty, frame.EyeShift.X);
        SetIfChanged(shift, TranslateTransform.YProperty, frame.EyeShift.Y);
        SetIfChanged(eyeCanvas, OpacityProperty, frame.EyeOpacity);

        var eye = _face.Eye;
        if (eye.Pupil <= 0 || frame.Happy)
        {
            return;
        }
        // Pupil (scaled per mood) with the glint in its upper right (studio: right 1px, top 1px).
        var size = eye.Pupil * frame.PupilScale;
        var px = centre.X + frame.PupilOffset.X;
        var py = centre.Y + frame.PupilOffset.Y;
        SetIfChanged(pupil, WidthProperty, size);
        SetIfChanged(pupil, HeightProperty, size);
        SetIfChanged(pupil, Canvas.LeftProperty, px - size / 2);
        SetIfChanged(pupil, Canvas.TopProperty, py - size / 2);
        var g = eye.Glint * frame.PupilScale;
        var inset = (eye.Pupil / 2 - 1 - eye.Glint / 2) * frame.PupilScale;
        SetIfChanged(glint, WidthProperty, g);
        SetIfChanged(glint, HeightProperty, g);
        SetIfChanged(glint, Canvas.LeftProperty, px + inset - g / 2);
        SetIfChanged(glint, Canvas.TopProperty, py - inset - g / 2);
    }

    private void UpdateIndicators(TimeSpan now)
    {
        var ms = now.TotalMilliseconds;
        var since = (now - _stateSince).TotalMilliseconds;
        var cy = _row.RowHeight / 2;
        switch (_state)
        {
            case NotchFaceState.Thinking:
                for (var i = 0; i < _thinkingDots.Length; i++)
                {
                    var (opacity, offset) = _reducedMotion ? (0.25, 0d) : FaceIndicators.ThinkingDot(i, since);
                    SetIfChanged(_thinkingDots[i], OpacityProperty, opacity);
                    SetIfChanged(_thinkingDots[i], Canvas.TopProperty, cy - FaceIndicators.DotSize / 2 + offset);
                }
                break;
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
            case NotchFaceState.Listening:
            case NotchFaceState.Responding:
                for (var i = 0; i < _listeningBars.Length; i++)
                {
                    var h = _state == NotchFaceState.Listening
                        ? FaceIndicators.ListeningBarHeight(i, ms)
                        : FaceIndicators.TalkingBarHeight(i, ms, _talkLevel);
                    var bar = _listeningBars[i];
                    SetIfChanged(bar, HeightProperty, h);
                    SetIfChanged(bar, Canvas.TopProperty, cy - h / 2);
                }
                break;
        }
    }

    // ---------------------------------------------------------------- caption

    private void SetCaption(string? caption)
    {
        var text = CaptionText(caption);
        if (string.Equals(text, _caption, StringComparison.Ordinal))
        {
            return;
        }
        // Streaming speech grows the same caption: keep typing from where it is instead of restarting.
        var continues = _caption.Length > 0 && text.StartsWith(_caption, StringComparison.Ordinal);
        _caption = text;
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
        SetIfChanged(Caption, OpacityProperty, _captionReveal.OpacityAt(elapsed, _reducedMotion));
        var shown = _captionReveal.VisibleText(text, elapsed, _reducedMotion);
        if (!string.Equals(shown, _captionShown, StringComparison.Ordinal))
        {
            _captionShown = shown;
            Caption.Text = shown;
        }
        _captionDone = shown.Length == text.Length && Caption.Opacity >= 1;
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

    private void LayoutBody(SizeD size)
    {
        var left = BodyLeftDip(size);
        var shoulder = _face.Shape.Shoulder;
        if (size != _builtSize)
        {
            BodyPath.Data = BuildPocket(size.Width, size.Height, shoulder, _face.Shape.BottomRadius);
            _builtSize = size;
        }
        Canvas.SetLeft(BodyPath, left - shoulder);
        Canvas.SetTop(BodyPath, 0);
        Canvas.SetLeft(FaceLayer, left);
        Canvas.SetTop(FaceLayer, 0);
        Canvas.SetLeft(OpenButton, left);
        Canvas.SetTop(OpenButton, 0);
        OpenButton.Width = size.Width;
        OpenButton.Height = size.Height;

        var (lead, trail) = NotchRowLayout.SlotWidths(_state);
        _row = NotchRowLayout.Place(_face, size, lead, trail);
        _parts = new NotchFaceParts(_row.LeftEye, _row.RightEye, _row.Trail);
        PositionEye(_row.LeftEye, LeftEye, LeftBlink, LeftWhite, LeftPupil, LeftGlint, LeftHappy);
        PositionEye(_row.RightEye, RightEye, RightBlink, RightWhite, RightPupil, RightGlint, RightHappy);
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

    /// <summary>
    /// HeyClicky pocket: concave shoulders (radius = shoulder) flowing from the screen edge into the sides,
    /// straight sides, rounded bottom corners. Path origin = left end of the left shoulder.
    /// </summary>
    private static Geometry BuildPocket(double width, double height, double shoulder, double bottomRadius)
    {
        var s = Math.Max(0, Math.Min(shoulder, height / 2));
        var r = Math.Max(0, Math.Min(bottomRadius, Math.Min(width / 2, height - s)));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(0, 0), isFilled: true, isClosed: true);
            ctx.QuadraticBezierTo(new System.Windows.Point(s, 0), new System.Windows.Point(s, s), true, true);
            ctx.LineTo(new System.Windows.Point(s, height - r), true, true);
            ctx.ArcTo(new System.Windows.Point(s + r, height), new System.Windows.Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new System.Windows.Point(s + width - r, height), true, true);
            ctx.ArcTo(new System.Windows.Point(s + width, height - r), new System.Windows.Size(r, r), 0, false, SweepDirection.Counterclockwise, true, true);
            ctx.LineTo(new System.Windows.Point(s + width, s), true, true);
            ctx.QuadraticBezierTo(new System.Windows.Point(s + width, 0), new System.Windows.Point(width + 2 * s, 0), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private void BuildFace()
    {
        var eye = _face.Eye;
        var radius = Math.Min(eye.Width, eye.Height) / 2;
        foreach (var white in new[] { LeftWhite, RightWhite })
        {
            white.Width = eye.Width;
            white.Height = eye.Height;
            white.RadiusX = radius;
            white.RadiusY = radius;
        }
        foreach (var pupil in new[] { LeftPupil, RightPupil })
        {
            pupil.Width = pupil.Height = eye.Pupil;
            pupil.Fill = PupilBrush;
        }
        foreach (var glint in new[] { LeftGlint, RightGlint })
        {
            glint.Width = glint.Height = eye.Glint;
        }
        // Happy: studio ".eye.happy" = the top border (3 px) of an eye-sized circle dropped 6 px, i.e. a smiling arc.
        // Drawn in eye-box coordinates: circle centre (w/2, h/2 + 3), stroke-centre radius w/2 - 1.5, 210..330 degrees.
        var arc = new StreamGeometry();
        var r = eye.Width / 2 - 1.5;
        var acx = eye.Width / 2;
        var acy = eye.Height / 2 + 3;
        System.Windows.Point At(double degrees) => new(acx + r * Math.Cos(degrees * Math.PI / 180), acy + r * Math.Sin(degrees * Math.PI / 180));
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(At(210), false, false);
            ctx.ArcTo(At(330), new System.Windows.Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
        }
        arc.Freeze();
        LeftHappy.Data = arc;
        RightHappy.Data = arc;
    }

    private void PositionEye(PointD centre, Canvas eyeCanvas, ScaleTransform blink, Rectangle white, Ellipse pupil, Ellipse glint, System.Windows.Shapes.Path happy)
    {
        var eye = _face.Eye;
        Canvas.SetLeft(eyeCanvas, 0);
        blink.CenterX = centre.X;
        blink.CenterY = centre.Y;
        // Studio ".eye{overflow:hidden}": the pupil never leaves the white.
        var clip = new EllipseGeometry(new System.Windows.Point(centre.X, centre.Y), eye.Width / 2, eye.Height / 2);
        clip.Freeze();
        eyeCanvas.Clip = clip;
        Canvas.SetLeft(white, centre.X - eye.Width / 2);
        Canvas.SetTop(white, centre.Y - eye.Height / 2);
        Canvas.SetLeft(happy, centre.X - eye.Width / 2);
        Canvas.SetTop(happy, centre.Y - eye.Height / 2);
        if (eye.Pupil > 0)
        {
            Canvas.SetLeft(pupil, centre.X - eye.Pupil / 2);
            Canvas.SetTop(pupil, centre.Y - eye.Pupil / 2);
            Canvas.SetLeft(glint, centre.X + eye.Pupil * 0.05 - eye.Glint / 2);
            Canvas.SetTop(glint, centre.Y - eye.Pupil * 0.2 - eye.Glint / 2);
        }
    }

    private void BuildIndicators()
    {
        for (var i = 0; i < _listeningBars.Length; i++)
        {
            var bar = new Rectangle { Width = FaceIndicators.BarWidth, Height = 4, RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetLeft(bar, i * (FaceIndicators.BarWidth + FaceIndicators.BarGap));
            _listeningBars[i] = bar;
            ListeningBars.Children.Add(bar);
        }
        for (var i = 0; i < _thinkingDots.Length; i++)
        {
            var dot = new Ellipse { Width = FaceIndicators.DotSize, Height = FaceIndicators.DotSize, Opacity = 0.25 };
            Canvas.SetLeft(dot, i * (FaceIndicators.DotSize + FaceIndicators.DotGap));
            _thinkingDots[i] = dot;
            ThinkingDots.Children.Add(dot);
        }
        RecordDot.Width = RecordDot.Height = 7;
        for (var i = 0; i < 2; i++)
        {
            var bar = new Rectangle { Width = 3, Height = 10, RadiusX = 1, RadiusY = 1, Opacity = 0.7 };
            Canvas.SetLeft(bar, i * 6);
            PauseBars.Children.Add(bar);
        }
    }

    private void PositionIndicators()
    {
        // Studio row: lead slot (record dot) left of the eyes, trail slot (bars, dots, pause, stop) right-aligned.
        // Bars and dots are positioned vertically per frame; containers sit at the slot's left edge, top 0.
        var cy = _row.RowHeight / 2;
        Canvas.SetLeft(ListeningBars, _row.Trail.X);
        Canvas.SetTop(ListeningBars, 0);
        Canvas.SetLeft(ThinkingDots, _row.Trail.X);
        Canvas.SetTop(ThinkingDots, 0);
        foreach (var dot in _thinkingDots)
        {
            Canvas.SetTop(dot, cy - FaceIndicators.DotSize / 2);
        }
        foreach (var bar in _listeningBars)
        {
            Canvas.SetTop(bar, cy - bar.Height / 2);
        }
        Canvas.SetLeft(PauseBars, _row.Trail.X);
        Canvas.SetTop(PauseBars, cy - 5);
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
        if (SystemParameters.HighContrast)
        {
            body = MediaSystemColors.WindowBrush;
            eyes = MediaSystemColors.WindowTextBrush;
        }
        else
        {
            body = ToBrush(_appearance.BodyColor, MediaBrushes.Black);
            eyes = ToBrush(_appearance.EyeColor, MediaBrushes.White);
        }
        BodyPath.Fill = body;
        LeftWhite.Fill = RightWhite.Fill = eyes;
        LeftGlint.Fill = RightGlint.Fill = eyes;
        LeftHappy.Stroke = RightHappy.Stroke = eyes;
        // Studio ".pupil{background:var(--body)}": pupils take the body colour.
        var pupil = SystemParameters.HighContrast ? MediaSystemColors.WindowBrush : body;
        LeftPupil.Fill = RightPupil.Fill = pupil;
        foreach (var bar in _listeningBars)
        {
            bar.Fill = eyes;
        }
        foreach (var dot in _thinkingDots)
        {
            dot.Fill = eyes;
        }
        foreach (var child in PauseBars.Children.OfType<Rectangle>())
        {
            child.Fill = eyes;
        }
        Caption.Foreground = eyes;
        StopSquareInner.Background = eyes;
        StopSquare.Background = WithAlpha(eyes, 0.16);
        // Studio pills: primary = eye colour with body-coloured text; secondary = eye colour at 14%.
        NudgeBackButton.Background = eyes;
        NudgeBackButton.Foreground = body;
        NudgeBreakButton.Background = WithAlpha(eyes, 0.14);
        NudgeBreakButton.Foreground = eyes;
        ActiveDisclosure.Background = SystemParameters.HighContrast ? MediaSystemColors.WindowBrush : MediaBrushes.White;
        ActiveDisclosure.BorderBrush = SystemParameters.HighContrast ? MediaSystemColors.ActiveBorderBrush : MediaBrushes.Black;
        ActiveDisclosureText.Foreground = SystemParameters.HighContrast ? MediaSystemColors.WindowTextBrush : MediaBrushes.Black;
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
