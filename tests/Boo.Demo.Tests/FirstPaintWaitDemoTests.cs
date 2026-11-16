using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FlaUI.Core.AutomationElements;
using WpfWindow = System.Windows.Window;
using WpfGrid = System.Windows.Controls.Grid;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace Boo.Demo.Tests;

/// <summary>
/// The first-paint wait on real WPF windows the test owns, through the fixture's own <see cref="BooAppFixture.WaitFor{T}"/>
/// (the primitive <c>WaitForPanel</c> uses, with its deadline and timeout screenshot): a WPF window that never draws
/// content keeps the wait waiting until it fails closed with a timeout and a retained timeout capture (the red path), and
/// a WPF window that draws its content late is handed back only after that content exists (the delayed-first-render
/// control). The expected timeout's capture is scoped to Boo's own windows (BOO_CAPTURE_SCOPE, as the live smoke sets
/// it) for that one call, so a deliberate red-path capture of a bare desktop never enters the desktop evidence that the
/// visual check judges; the fixture still writes its capture note. Boo runs during this class, as for every demo class.
/// </summary>
public sealed class FirstPaintWaitDemoTests : IClassFixture<BooAppFixture>
{
    private readonly BooAppFixture _boo;

    public FirstPaintWaitDemoTests(BooAppFixture boo)
    {
        _boo = boo;
    }

    [Fact]
    public void ANeverPaintedWpfWindow_TimesOutAndKeepsTheTimeoutScreenshot()
    {
        using var probe = WpfProbeWindow.Show(contentDelay: null);
        var window = _boo.Automation.FromHandle(probe.Handle).AsWindow();
        var clock = Stopwatch.StartNew();

        TimeoutException timeout;
        var previousScope = Environment.GetEnvironmentVariable("BOO_CAPTURE_SCOPE");
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        try
        {
            timeout = Assert.Throws<TimeoutException>(() =>
                _boo.WaitFor(() => BooAppFixture.IsPainted(window) ? window : null, TimeSpan.FromSeconds(3), "a never-painted probe window (expected)"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", previousScope);
        }

        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(3), $"the wait gave up after {clock.Elapsed.TotalSeconds:0.0} s");
        Assert.Contains("never-painted probe window", timeout.Message);
        var stem = Path.Combine(_boo.ArtifactsDirectory, "timeout-a-never-painted-probe-window--expected-");
        Assert.True(File.Exists(stem + ".capture.txt"), "no timeout capture note was kept for the never-painted window");
    }

    [Fact]
    public void AWpfWindowThatPaintsLate_IsHandedBackOnlyAfterItsContentExists()
    {
        var delay = TimeSpan.FromMilliseconds(1500);
        using var probe = WpfProbeWindow.Show(delay);
        var window = _boo.Automation.FromHandle(probe.Handle).AsWindow();
        var clock = Stopwatch.StartNew();

        var painted = _boo.WaitFor(() => BooAppFixture.IsPainted(window) ? window : null, TimeSpan.FromSeconds(15), "the late-painting probe window");
        var returnedAt = DateTime.UtcNow;

        Assert.True(clock.Elapsed >= delay - TimeSpan.FromMilliseconds(100),
            $"the wait returned after {clock.Elapsed.TotalMilliseconds:0} ms, before the content was drawn at {delay.TotalMilliseconds:0} ms");
        var shownAt = probe.ContentShownAt;
        Assert.True(shownAt is not null, "the wait returned although the window never set its content");
        Assert.True(shownAt <= returnedAt,
            $"the wait returned at {returnedAt:HH:mm:ss.fff}, before the window set its content at {shownAt:HH:mm:ss.fff}");
        Assert.True(BooAppFixture.IsPainted(painted), "the window handed back was not painted");
    }

    /// <summary>A borderless 240 x 180 WPF window on its own STA dispatcher thread: white until its content is set.</summary>
    private sealed class WpfProbeWindow : IDisposable
    {
        private readonly Thread _thread;
        private WpfWindow? _window;
        private Dispatcher? _dispatcher;
        private long _contentShownTicks;

        public IntPtr Handle { get; private set; }

        /// <summary>When the dispatcher thread started setting the window's content (UTC), or null while it is still blank. Recorded before the content exists, so a wait that returns earlier than this returned on a blank window.</summary>
        public DateTime? ContentShownAt
        {
            get
            {
                var ticks = Volatile.Read(ref _contentShownTicks);
                return ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);
            }
        }

        private WpfProbeWindow(TimeSpan? contentDelay)
        {
            using var ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                _window = new WpfWindow
                {
                    Title = "Boo demo first-paint probe",
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Width = 240,
                    Height = 180,
                    Left = 0,
                    Top = 0,
                    Background = MediaBrushes.White,
                };
                _window.Show();
                Handle = new WindowInteropHelper(_window).Handle;
                _dispatcher = Dispatcher.CurrentDispatcher;
                if (contentDelay is { } wait)
                {
                    var timer = new DispatcherTimer { Interval = wait };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        Volatile.Write(ref _contentShownTicks, DateTime.UtcNow.Ticks);
                        _window.Content = BuildContent();
                        _window.UpdateLayout();
                    };
                    timer.Start();
                }
                ready.Set();
                Dispatcher.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            if (!ready.Wait(TimeSpan.FromSeconds(15)))
            {
                throw new TimeoutException("The WPF probe window did not open within 15 s.");
            }
        }

        public static WpfProbeWindow Show(TimeSpan? contentDelay) => new(contentDelay);

        private static UIElement BuildContent()
        {
            var grid = new WpfGrid
            {
                Background = new LinearGradientBrush(MediaColor.FromRgb(20, 80, 160), MediaColor.FromRgb(180, 210, 240), 35),
            };
            var rows = new StackPanel { Margin = new Thickness(12) };
            for (var i = 0; i < 6; i++)
            {
                rows.Children.Add(new Border { Background = MediaBrushes.White, Height = 8, Margin = new Thickness(0, 6, 0, 6) });
            }
            grid.Children.Add(rows);
            return grid;
        }

        public void Dispose()
        {
            _dispatcher?.Invoke(() => _window?.Close());
            _dispatcher?.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}
