using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Automation;
using System.Windows.Threading;
using Boo.App;

namespace Boo.App.Tests;

/// <summary>
/// The companion window's render acknowledgment (PR #161): <c>MainWindow</c> publishes UI Automation ItemStatus
/// "rendered" from WPF's ContentRendered event and from nothing earlier. The demo fixture requires that marker
/// before it captures, so an acknowledgment that arrives before the first render pass (from the constructor, or
/// from Loaded, which WPF raises after layout but before the first render) would let a capture of a blank window
/// through. This test drives the real MainWindow in-process on one dispatcher: handler order is deterministic
/// (the app subscribes in its constructor, the test afterwards), so what the test reads at each step is exactly
/// what UI Automation would read at that step. The published app's pixel probe is a separate gate and is not
/// involved here.
/// </summary>
public sealed class RenderAcknowledgmentTests
{
    [Fact]
    public void TheRenderedMarker_IsAbsentAtConstructionAndLoaded_AndPresentOnceContentRendered()
    {
        var timeline = RunOnStaThread(() =>
        {
            EnsureApplicationResources();
            var dataDirectory = Directory.CreateTempSubdirectory("boo-app-tests-").FullName;
            var window = new MainWindow(
                dataDirectory,
                onClose: () => { },
                onStop: () => { },
                onDisplayChanged: _ => { },
                verticalOffset: () => 0,
                ownedWindows: () => Array.Empty<long>(),
                captureExclusion: () => true,
                onCaptureActivity: (_, _) => { },
                onMicrophoneActivity: _ => { },
                onAppearanceChanged: _ => { });
            var seen = new List<string> { "constructed:" + Marker(window) };
            window.Loaded += (_, _) => seen.Add("loaded:" + Marker(window));
            window.ContentRendered += (_, _) => seen.Add("content-rendered:" + Marker(window));
            window.ShowActivated = false;
            window.Show();
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (!seen.Any(step => step.StartsWith("content-rendered:", StringComparison.Ordinal)) && DateTime.UtcNow < deadline)
            {
                PumpDispatcher();
            }
            PumpDispatcher();
            seen.Add("after-render:" + Marker(window));
            window.DisposeShell();
            window.Close();
            PumpDispatcher();
            return seen;
        });

        Assert.Equal(
            ["constructed:", "loaded:", "content-rendered:rendered", "after-render:rendered"],
            timeline);
    }

    /// <summary>UI Automation ItemStatus as the fixture reads it; WPF's default is the empty string.</summary>
    private static string Marker(Window window) => AutomationProperties.GetItemStatus(window);

    /// <summary>
    /// MainWindow.xaml resolves its brushes and fonts from App.xaml, so the real App's resources are loaded once
    /// on the STA thread. App.OnStartup (mutex, tray, notch) never runs: the Application is never Run().
    /// </summary>
    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
        {
            return;
        }
        var app = new App();
        app.InitializeComponent();
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static T RunOnStaThread<T>(Func<T> body)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(90)))
        {
            throw new TimeoutException("The STA test thread did not finish within 90 s.");
        }
        if (failure is not null)
        {
            throw new InvalidOperationException("The STA test body failed: " + failure.Message, failure);
        }
        return result!;
    }
}
