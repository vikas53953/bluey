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
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint EmergencyKey = 0x7B;
    private const string SpecResourceName = "Boo.App.boo-notch-spec.json";
    private const double ShakePadDip = 6;
    private const double DisclosureReserveDip = 76;
    private const double GazeReachDip = 220;
    private const int ThinkingDotCycleMs = 1200;
    private const int RecordPulseMs = 2000;
    private const int NudgeShakeMs = 320;
    private const double NudgeShakeDip = 3;
    private const int HappyBounceMs = 420;
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
    private readonly GazeSpring _leftGaze;
    private readonly GazeSpring _rightGaze;
    private readonly PressHoldTracker _press = new();
    private readonly DispatcherTimer _holdTimer;
    private readonly Rectangle[] _listeningBars = new Rectangle[3];
    private readonly Ellipse[] _thinkingDots = new Ellipse[3];
    private readonly double _windowWidthDip;
    private readonly double _windowHeightDip;
    private NotchAppearance _appearance = new();
    private BlinkScheduler _blink;
    private TimeSpan _blinkEpoch;
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
    private bool _reducedMotion;
    private NotchFaceParts _parts = new(default, default, default);
    private bool _captureActive;
    private string? _captureDisplayName;
    private bool _microphoneActive;
    private string? _status;
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

    /// <summary>
    /// Press-and-hold on the eyes: true once a left press has lasted 200 ms, false on release or lost capture.
    /// A shorter click opens the full window instead and raises nothing.
    /// TODO(talk lane): App/MainWindow should subscribe here to start/stop hold-to-talk
    /// (e.g. in App.OnStartup: _notch.EyesHeldChanged += held => _panel?.SetHoldToTalk(held);).
    /// </summary>
    public event Action<bool>? EyesHeldChanged;

    /// <summary>The spec in use (chosen face), for diagnostics.</summary>
    public NotchSpec Spec => _spec;

    /// <summary>Current resolved face state.</summary>
    public NotchFaceState FaceState => _state;

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
        _leftGaze = new GazeSpring(_spec.Timings.GazeStiffness, _spec.Timings.GazeDamping);
        _rightGaze = new GazeSpring(_spec.Timings.GazeStiffness, _spec.Timings.GazeDamping);
        _reducedMotion = ComputeReducedMotion();
        _blink = new BlinkScheduler(_spec.Timings, new Random(), _reducedMotion);
        _holdTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = PressHoldTracker.HoldThreshold };
        _holdTimer.Tick += HoldTimer_OnTick;

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
            if (EmergencyStopRegistered)
            {
                UnregisterHotKey(_handle, EmergencyHotKeyId);
            }
            if (OpenShortcutRegistered)
            {
                UnregisterHotKey(_handle, OpenHotKeyId);
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
            var requestedScreen = display is null ? null : FindScreen(display.Id);
            var screen = requestedScreen ?? Forms.Screen.PrimaryScreen;
            if (screen is null)
            {
                return;
            }

            var info = requestedScreen is not null && display is not null
                ? display
                : new DisplayInfo(
                    screen.DeviceName,
                    screen.Primary ? "Primary display" : screen.DeviceName,
                    new RectD(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height),
                    _handle == IntPtr.Zero ? 1d : Math.Max(1d, GetDpiForWindow(_handle) / 96d));
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
        Recompute();
    }

    public void SetStatus(string status)
    {
        _status = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        // Status is never drawn on the collapsed notch; screen readers still get it.
        System.Windows.Automation.AutomationProperties.SetHelpText(this, _status ?? string.Empty);
        UpdateDisclosure();
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
        _state = state;
        _stateSince = now;
        _reducedMotion = ComputeReducedMotion();

        _sizeFrom = _sizeNow;
        _sizeTo = _face.SizeFor(state);
        _resizeStart = now;
        if (_reducedMotion || wasHidden)
        {
            _sizeFrom = _sizeTo;
            _sizeNow = _sizeTo;
        }

        if (wasHidden && state != NotchFaceState.Hidden)
        {
            // Fresh blink timeline per appearance keeps the schedule short-lived and honours reduced motion.
            _blink = new BlinkScheduler(_spec.Timings, new Random(), _reducedMotion);
            _blinkEpoch = now;
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

        ListeningBars.Visibility = state == NotchFaceState.Listening ? Visibility.Visible : Visibility.Collapsed;
        ThinkingDots.Visibility = state == NotchFaceState.Thinking ? Visibility.Visible : Visibility.Collapsed;
        RecordDot.Visibility = state == NotchFaceState.Capture ? Visibility.Visible : Visibility.Collapsed;
        PauseBars.Visibility = state == NotchFaceState.Paused ? Visibility.Visible : Visibility.Collapsed;
        RecordDot.Opacity = 1;
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
        if (_timeline.PhaseAt(now) == RevealPhase.Hidden && _state == NotchFaceState.Hidden)
        {
            NotchHost.Visibility = Visibility.Collapsed;
            return;
        }
        _rendering = true;
        _lastFrame = now;
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
                var k = CubicBezierEase.EaseOut.Evaluate(t);
                _sizeNow = new SizeD(
                    _sizeFrom.Width + (_sizeTo.Width - _sizeFrom.Width) * k,
                    _sizeFrom.Height + (_sizeTo.Height - _sizeFrom.Height) * k);
            }
            LayoutBody(_sizeNow);
        }

        ApplyReveal(phase, now);
        if (_state != NotchFaceState.Hidden)
        {
            UpdateGaze(dt);
            UpdateBlink(now);
            UpdateIndicators(now);
        }

        if (phase == RevealPhase.Hidden && _state == NotchFaceState.Hidden && _sizeNow == _sizeTo)
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

        var shake = 0d;
        var since = (now - _stateSince).TotalMilliseconds;
        if (_state == NotchFaceState.Nudge && !_reducedMotion && since < 2 * NudgeShakeMs)
        {
            shake = NudgeShakeDip * Math.Sin(2 * Math.PI * since / NudgeShakeMs);
        }
        SetIfChanged(RevealTranslate, TranslateTransform.XProperty, shake);
    }

    private void UpdateGaze(TimeSpan dt)
    {
        if (_face.Eye.GazeX <= 0 && _face.Eye.GazeY <= 0)
        {
            return;
        }

        var hasCursor = GetCursorPos(out var cursor);
        var pointer = hasCursor ? new PointD(cursor.X, cursor.Y) : new PointD(double.NaN, double.NaN);
        var bodyLeft = BodyLeftDip(_sizeNow);
        var reach = GazeReachDip * _scale;

        _leftGaze.Target = GazeFor(pointer, bodyLeft, _parts.LeftEye, reach);
        _rightGaze.Target = GazeFor(pointer, bodyLeft, _parts.RightEye, reach);
        if (_reducedMotion)
        {
            _leftGaze.SnapTo(_leftGaze.Target);
            _rightGaze.SnapTo(_rightGaze.Target);
        }
        else
        {
            _leftGaze.Step(dt);
            _rightGaze.Step(dt);
        }

        PlaceGaze(_leftGaze.Position, LeftWhite, LeftPupil, LeftGlint, _parts.LeftEye);
        PlaceGaze(_rightGaze.Position, RightWhite, RightPupil, RightGlint, _parts.RightEye);
    }

    private PointD GazeFor(PointD pointer, double bodyLeftDip, PointD eyeDip, double reachPx)
    {
        var eyePx = new PointD(_windowLeftPx + (bodyLeftDip + eyeDip.X) * _scale, _windowTopPx + eyeDip.Y * _scale);
        // Spec gaze radii are DIP; the solver works in px, so scale in and out.
        var offset = GazeSolver.Target(pointer, eyePx, _face.Eye.GazeX * _scale, _face.Eye.GazeY * _scale, reachPx);
        return new PointD(offset.X / _scale, offset.Y / _scale);
    }

    private void PlaceGaze(PointD offset, Rectangle white, Ellipse pupil, Ellipse glint, PointD centre)
    {
        var eye = _face.Eye;
        if (eye.Pupil > 0)
        {
            var px = centre.X + offset.X - eye.Pupil / 2;
            var py = centre.Y + offset.Y - eye.Pupil / 2;
            SetIfChanged(pupil, Canvas.LeftProperty, px);
            SetIfChanged(pupil, Canvas.TopProperty, py);
            // Glint sits in the upper right of the pupil.
            SetIfChanged(glint, Canvas.LeftProperty, px + eye.Pupil * 0.55 - eye.Glint / 2);
            SetIfChanged(glint, Canvas.TopProperty, py + eye.Pupil * 0.3 - eye.Glint / 2);
        }
        else
        {
            // Faces without pupils (pebble, peek) move the whole eye.
            SetIfChanged(white, Canvas.LeftProperty, centre.X + offset.X - eye.Width / 2);
            SetIfChanged(white, Canvas.TopProperty, centre.Y + offset.Y - eye.Height / 2);
        }
    }

    private void UpdateBlink(TimeSpan now)
    {
        var openness = _state == NotchFaceState.Happy ? 1 : _blink.OpennessAt(now - _blinkEpoch);
        // Never fully flat so the eye stays a visible sliver.
        var scaleY = Math.Max(0.08, openness);
        SetIfChanged(LeftBlink, ScaleTransform.ScaleYProperty, scaleY);
        SetIfChanged(RightBlink, ScaleTransform.ScaleYProperty, scaleY);

        var bounce = 0d;
        var since = (now - _stateSince).TotalMilliseconds;
        if (_state == NotchFaceState.Happy && !_reducedMotion && since < HappyBounceMs)
        {
            bounce = -2 * Math.Sin(Math.PI * since / HappyBounceMs);
        }
        SetIfChanged(LeftEye, Canvas.TopProperty, bounce);
        SetIfChanged(RightEye, Canvas.TopProperty, bounce);
    }

    private void UpdateIndicators(TimeSpan now)
    {
        var ms = now.TotalMilliseconds;
        switch (_state)
        {
            case NotchFaceState.Thinking:
                for (var i = 0; i < _thinkingDots.Length; i++)
                {
                    var phase = (ms / ThinkingDotCycleMs - i / 3.0) * 2 * Math.PI;
                    var opacity = _reducedMotion ? 0.8 : 0.3 + 0.7 * Math.Max(0, Math.Sin(phase));
                    SetIfChanged(_thinkingDots[i], OpacityProperty, opacity);
                }
                break;
            case NotchFaceState.Capture:
                // Pulses but never hides while capture is on (minimum opacity 0.55).
                var pulse = _reducedMotion ? 1 : 0.55 + 0.45 * (0.5 + 0.5 * Math.Cos(2 * Math.PI * ms / RecordPulseMs));
                SetIfChanged(RecordDot, OpacityProperty, pulse);
                RecordDot.Visibility = Visibility.Visible;
                break;
            case NotchFaceState.Listening:
                for (var i = 0; i < _listeningBars.Length; i++)
                {
                    var h = _reducedMotion ? 10 : 7 + 6 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * ms / 900 + i * 1.7));
                    var bar = _listeningBars[i];
                    SetIfChanged(bar, HeightProperty, h);
                    SetIfChanged(bar, Canvas.TopProperty, (14 - h) / 2);
                }
                break;
        }
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

        _parts = NotchFaceGeometry.Place(_face, size);
        PositionEye(_parts.LeftEye, LeftEye, LeftBlink, LeftWhite, LeftPupil, LeftGlint, LeftHappy);
        PositionEye(_parts.RightEye, RightEye, RightBlink, RightWhite, RightPupil, RightGlint, RightHappy);
        PositionIndicators(_parts.IndicatorSlot);
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
        // Happy: 3 px "^" arc the width of the eye.
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(new System.Windows.Point(1.5, eye.Height / 2 + 2), false, false);
            ctx.QuadraticBezierTo(new System.Windows.Point(eye.Width / 2, eye.Height / 2 - 7), new System.Windows.Point(eye.Width - 1.5, eye.Height / 2 + 2), true, true);
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
            var bar = new Rectangle { Width = 3, Height = 10, RadiusX = 1.5, RadiusY = 1.5 };
            Canvas.SetLeft(bar, i * 6);
            Canvas.SetTop(bar, 2);
            _listeningBars[i] = bar;
            ListeningBars.Children.Add(bar);
        }
        for (var i = 0; i < _thinkingDots.Length; i++)
        {
            var dot = new Ellipse { Width = 5, Height = 5, Opacity = 0.6 };
            Canvas.SetLeft(dot, i * 9);
            Canvas.SetTop(dot, 0);
            _thinkingDots[i] = dot;
            ThinkingDots.Children.Add(dot);
        }
        RecordDot.Width = RecordDot.Height = 9;
        for (var i = 0; i < 2; i++)
        {
            var bar = new Rectangle { Width = 3, Height = 11, RadiusX = 1, RadiusY = 1 };
            Canvas.SetLeft(bar, i * 7);
            PauseBars.Children.Add(bar);
        }
    }

    private void PositionIndicators(RectD slot)
    {
        // Listening bars: 15 x 14; thinking dots: 23 x 5; record dot: 9 x 9; pause bars: 10 x 11. Centred, nudged toward the eyes.
        PlaceInSlot(ListeningBars, slot, 15, 14);
        PlaceInSlot(ThinkingDots, slot, 23, 5);
        PlaceInSlot(RecordDot, slot, 9, 9);
        PlaceInSlot(PauseBars, slot, 10, 11);
    }

    private static void PlaceInSlot(UIElement element, RectD slot, double width, double height)
    {
        var x = slot.Width <= 0 ? slot.X : slot.X + Math.Max(0, (slot.Width - width) / 2 - Math.Min(6, slot.Width / 6));
        var y = slot.Y + (slot.Height - height) / 2;
        Canvas.SetLeft(element, x);
        Canvas.SetTop(element, y);
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

        _scale = scale > 0 && double.IsFinite(scale) ? scale : 1;
        _windowLeftPx = Math.Round(bounds.X);
        _windowTopPx = Math.Round(bounds.Y);
        SetWindowPos(_handle, IntPtr.Zero,
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height),
            SwpNoActivate | SwpNoZOrder);
    }

    // ---------------------------------------------------------------- disclosure

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
        var collapsed = _state is NotchFaceState.Hidden or NotchFaceState.Hover or NotchFaceState.Idle;
        if (activity.Count == 0 && !collapsed && !string.IsNullOrWhiteSpace(_status))
        {
            activity.Add(_status);
        }

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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(_handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(_handle, GwlExstyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
        var source = HwndSource.FromHwnd(_handle);
        source?.AddHook(EmergencyWindowProc);
        EmergencyStopRegistered = RegisterHotKey(_handle, EmergencyHotKeyId, ModControl | ModAlt, EmergencyKey);
        OpenShortcutRegistered = RegisterHotKey(_handle, OpenHotKeyId, ModControl | ModAlt, 0x42);
        if (!EmergencyStopRegistered)
        {
            SetStatus("Tray Stop ready");
        }
        if (!OpenShortcutRegistered)
        {
            SetStatus("Open via tray");
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
        var pupil = SystemParameters.HighContrast ? MediaSystemColors.WindowBrush : PupilBrush;
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
            _blink = new BlinkScheduler(_spec.Timings, new Random(), _reducedMotion);
            _blinkEpoch = _clock.Elapsed;
        }
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

    private static Forms.Screen? FindScreen(string id)
    {
        return Forms.Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, id, StringComparison.OrdinalIgnoreCase));
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
        if (msg == WmHotKey && wParam.ToInt32() == EmergencyHotKeyId)
        {
            _onStop();
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
