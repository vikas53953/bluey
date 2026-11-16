using System.Collections;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Boo.Core;
using Boo.Windows;

namespace Boo.CaptureAffinity;

/// <summary>
/// Issue #146: after a screen capture, every Boo-owned window whose display affinity was changed for the capture must
/// be back at its ORIGINAL value (not merely "not excluded"), on every way out of the capture, for every window.
/// </summary>
/// <remarks>
/// Each test opens four real windows in this process:
/// plain (visible, WDA_NONE), monitorOnly (visible, WDA_MONITOR), alreadyExcluded (visible, WDA_EXCLUDEFROMCAPTURE)
/// and hidden (never shown, WDA_NONE). It hands all four handles (plus a duplicate) to the real
/// WindowsFrameCaptureBackend, reads each window's affinity from Windows at the moment the exclusion has been applied
/// (so the test proves the restore had real work to do), then reads them again after the capture returns.
/// GitHub-hosted runners only: this changes real window affinity.
/// </remarks>
public sealed class CaptureAffinityRestoreTests
{
    private const uint WdaNone = 0x0;
    private const uint WdaMonitor = 0x1;
    private const uint WdaExcludeFromCapture = 0x11;
    private static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task FailedCapture_RestoresEveryChangedWindowToItsOriginalAffinity()
    {
        using var lab = WindowLab.Open();
        var owned = lab.OwnedWindows();

        var error = await Record.ExceptionAsync(() => lab.Capture(MissingDisplay(), owned, CancellationToken.None));

        var limitation = Assert.IsType<CaptureLimitationException>(error);
        Assert.Contains("no longer available", limitation.Message, StringComparison.Ordinal);
        lab.AssertExcludedDuringCapture(owned);
        lab.AssertAllBackToOriginal();
    }

    [Fact]
    public async Task CancelledCapture_RestoresEveryChangedWindowToItsOriginalAffinity()
    {
        using var lab = WindowLab.Open();
        using var cancel = new CancellationTokenSource();
        // Cancel at the moment every exclusion is in place: the backend checks the token right after excluding.
        var owned = lab.OwnedWindows(onExcluded: cancel.Cancel);

        var error = await Record.ExceptionAsync(() => lab.Capture(PrimaryDisplay(), owned, cancel.Token));

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        lab.AssertExcludedDuringCapture(owned);
        lab.AssertAllBackToOriginal();
    }

    [Fact]
    public async Task SuccessfulCapture_RestoresEveryChangedWindowToItsOriginalAffinity()
    {
        using var lab = WindowLab.Open();
        var owned = lab.OwnedWindows();
        using var timeout = new CancellationTokenSource(CaptureTimeout);

        var frame = await lab.Capture(PrimaryDisplay(), owned, timeout.Token);

        Assert.NotEmpty(frame.Jpeg);
        Assert.True(frame.Context.FrameWidth > 0 && frame.Context.FrameHeight > 0, "the capture returned an empty frame");
        lab.AssertExcludedDuringCapture(owned);
        lab.AssertAllBackToOriginal();
    }

    [Fact]
    public async Task OneFailedRestore_StillRestoresTheOtherWindows_AndReportsTheFailure()
    {
        using var lab = WindowLab.Open();
        // Destroy the first changed window while it is excluded, so restoring it must fail; the window after it in
        // the saved list must still be put back.
        var owned = lab.OwnedWindows(onExcluded: lab.DestroyPlain);

        var error = await Record.ExceptionAsync(() => lab.Capture(MissingDisplay(), owned, CancellationToken.None));

        var cleanup = Assert.IsType<CaptureLimitationException>(error);
        Assert.Contains("capture-exclusion restoration", cleanup.Message, StringComparison.Ordinal);
        var both = Assert.IsType<AggregateException>(cleanup.InnerException);
        Assert.Equal(2, both.InnerExceptions.Count);
        Assert.Contains("no longer available", both.InnerExceptions[0].Message, StringComparison.Ordinal);
        var restore = Assert.IsType<CaptureLimitationException>(both.InnerExceptions[1]);
        var failures = Assert.IsType<AggregateException>(restore.InnerException);
        var failure = Assert.Single(failures.InnerExceptions);
        Assert.Contains($"owned window {lab.PlainHandle}", failure.Message, StringComparison.Ordinal);

        lab.AssertExcludedDuringCapture(owned);
        Assert.False(lab.PlainAlive, "the plain window should have been destroyed mid-capture");
        lab.AssertAllBackToOriginal();
    }

    private static DisplayInfo MissingDisplay() =>
        new(@"\\.\BOO-NO-SUCH-DISPLAY", "missing", new RectD(0, 0, 1, 1), 1.0);

    private static DisplayInfo PrimaryDisplay()
    {
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("This runner has no primary display.");
        var b = screen.Bounds;
        return new DisplayInfo(screen.DeviceName, screen.DeviceName, new RectD(b.X, b.Y, b.Width, b.Height), 1.0);
    }

    /// <summary>Real windows on a dedicated UI thread, plus Boo's real capture backend.</summary>
    private sealed class WindowLab : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, (Form Form, long Handle, uint Original)> _windows = new();
        private readonly Dictionary<long, uint> _duringCapture = new();
        private readonly IFrameCaptureBackend _backend;
        private Form? _pump;
        private bool _destroyedPlain;

        private WindowLab()
        {
            var type = typeof(IFrameCaptureBackend).Assembly.GetType("Boo.Windows.WindowsFrameCaptureBackend", throwOnError: true)!;
            _backend = (IFrameCaptureBackend)Activator.CreateInstance(type, new object?[] { null })!;
            _thread = new Thread(Run) { IsBackground = true, Name = "capture-affinity-windows" };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public long PlainHandle => _windows["plain"].Handle;

        public bool PlainAlive => IsWindow(new IntPtr(PlainHandle));

        public static WindowLab Open()
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "This test changes real window display affinity, so it runs only on a GitHub-hosted runner " +
                    "(RUNNER_ENVIRONMENT=github-hosted), never on a personal PC.");
            }

            var lab = new WindowLab();
            lab._thread.Start();
            if (!lab._ready.Task.Wait(TimeSpan.FromSeconds(30)))
            {
                throw new TimeoutException("The test windows did not open within 30 s.");
            }

            lab._ready.Task.GetAwaiter().GetResult();
            return lab;
        }

        /// <summary>
        /// The owned-window list handed to the backend: every test window plus a duplicate. When the backend has
        /// walked the list and every window that needs it is excluded, the affinities are recorded and
        /// <paramref name="onExcluded"/> runs (on the windows' own thread, inside the capture).
        /// </summary>
        public IReadOnlyList<long> OwnedWindows(Action? onExcluded = null)
        {
            var handles = new[]
            {
                _windows["plain"].Handle,
                _windows["monitorOnly"].Handle,
                _windows["alreadyExcluded"].Handle,
                _windows["hidden"].Handle,
                _windows["plain"].Handle,
            };
            return new WatchedList(handles, () =>
            {
                if (_duringCapture.Count > 0 ||
                    Affinity(_windows["plain"].Handle) != WdaExcludeFromCapture ||
                    Affinity(_windows["monitorOnly"].Handle) != WdaExcludeFromCapture)
                {
                    return;
                }

                foreach (var (_, window) in _windows)
                {
                    _duringCapture[window.Handle] = Affinity(window.Handle);
                }

                onExcluded?.Invoke();
            });
        }

        /// <summary>Starts the capture on the windows' thread (so the exclusion step runs there) and awaits it here.</summary>
        public Task<CapturedFrame> Capture(DisplayInfo display, IReadOnlyList<long> owned, CancellationToken token)
        {
            var target = new WindowIdentity(_windows["plain"].Handle, Environment.ProcessId, "Boo.CaptureAffinity", new RectD(0, 0, 1, 1));
            return Invoke(() => _backend.CaptureAsync(display, target, owned, 1, token));
        }

        public void DestroyPlain()
        {
            _windows["plain"].Form.Dispose();
            _destroyedPlain = true;
        }

        /// <summary>The capture really excluded the two visible, not-yet-excluded windows and left the others alone.</summary>
        public void AssertExcludedDuringCapture(IReadOnlyList<long> owned)
        {
            Assert.True(_duringCapture.Count > 0, "The backend never reached the state with every capture exclusion applied.");
            Assert.Equal(WdaExcludeFromCapture, _duringCapture[_windows["plain"].Handle]);
            Assert.Equal(WdaExcludeFromCapture, _duringCapture[_windows["monitorOnly"].Handle]);
            Assert.Equal(WdaExcludeFromCapture, _duringCapture[_windows["alreadyExcluded"].Handle]);
            Assert.Equal(WdaNone, _duringCapture[_windows["hidden"].Handle]);
            Assert.Equal(5, owned.Count);
        }

        /// <summary>Every live window reads back exactly the affinity it had before the capture.</summary>
        public void AssertAllBackToOriginal()
        {
            var wrong = new List<string>();
            foreach (var (name, window) in _windows)
            {
                if (name == "plain" && _destroyedPlain)
                {
                    continue;
                }

                var now = Invoke(() => Affinity(window.Handle));
                if (now != window.Original)
                {
                    wrong.Add($"{name}: before 0x{window.Original:X}, after 0x{now:X}");
                }
            }

            Assert.True(wrong.Count == 0, "Not restored to the original display affinity: " + string.Join("; ", wrong));
        }

        public void Dispose()
        {
            if (_pump is { IsDisposed: false } pump)
            {
                try
                {
                    pump.Invoke(() =>
                    {
                        foreach (var (_, window) in _windows)
                        {
                            window.Form.Dispose();
                        }

                        Application.ExitThread();
                    });
                }
                catch (InvalidOperationException)
                {
                    // The UI thread is already gone.
                }
            }

            _thread.Join(TimeSpan.FromSeconds(10));
        }

        private void Run()
        {
            try
            {
                _pump = new Form { ShowInTaskbar = false };
                _ = _pump.Handle; // never shown, never handed to the backend: only marshals calls to this thread.
                Add("plain", visible: true, WdaNone, x: 40);
                Add("monitorOnly", visible: true, WdaMonitor, x: 220);
                Add("alreadyExcluded", visible: true, WdaExcludeFromCapture, x: 400);
                Add("hidden", visible: false, WdaNone, x: 580);
                _ready.TrySetResult();
            }
            catch (Exception error)
            {
                _ready.TrySetException(error);
                return;
            }

            Application.Run();
        }

        private void Add(string name, bool visible, uint original, int x)
        {
            var form = new Form
            {
                Text = $"Boo capture-affinity test: {name}",
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Bounds = new Rectangle(x, 120, 160, 120),
                BackColor = Color.Teal,
            };
            if (visible)
            {
                form.Show();
            }

            var hwnd = form.Handle;
            if (original != WdaNone && !SetWindowDisplayAffinity(hwnd, original))
            {
                throw new InvalidOperationException(
                    $"Setup: Windows refused display affinity 0x{original:X} on {name} (Win32 {Marshal.GetLastWin32Error()}).");
            }

            if (IsWindowVisible(hwnd) != visible || Affinity((long)hwnd) != original)
            {
                throw new InvalidOperationException($"Setup: {name} is not in its starting state.");
            }

            _windows[name] = (form, (long)hwnd, original);
        }

        private T Invoke<T>(Func<T> work) => (T)_pump!.Invoke(work);

        private static uint Affinity(long handle)
        {
            if (!GetWindowDisplayAffinity(new IntPtr(handle), out var value))
            {
                throw new InvalidOperationException(
                    $"Could not read display affinity of window {handle} (Win32 {Marshal.GetLastWin32Error()}).");
            }

            return value;
        }
    }

    /// <summary>A plain list that runs a callback each time it has been walked to the end.</summary>
    private sealed class WatchedList(long[] items, Action walkedToEnd) : IReadOnlyList<long>
    {
        public int Count => items.Length;

        public long this[int index] => items[index];

        public IEnumerator<long> GetEnumerator()
        {
            foreach (var item in items)
            {
                yield return item;
            }

            walkedToEnd();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
