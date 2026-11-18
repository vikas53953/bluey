using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Boo.App;
using Boo.Core;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using TextBox = System.Windows.Controls.TextBox;

namespace Boo.App.Tests;

/// <summary>
/// PR #170 (issue #168), mutation M05: a "saved" focus status promises that the history list already shows what was
/// saved. The real MainWindow runs in-process on one dispatcher. A watcher on the status text runs synchronously at
/// the exact moment the saved text is published and reads the history list right there. While waiting, the test
/// keeps the dispatcher busy at Input priority, so any UI work posted at Background priority or lower cannot run in
/// between (a controlled barrier); work at Normal priority, where the acknowledgment arrives, still runs. If the
/// history rows were applied by separate, later or lower-priority UI work, the row read at the acknowledgment is
/// still the old one and this test fails.
/// </summary>
public sealed class FocusAcknowledgmentOrderTests
{
    [Fact]
    public void SavedStartAndPauseAcknowledgments_FindTheHistoryRowAlreadyApplied()
    {
        var seen = RunOnStaThread(() =>
        {
            EnsureApplicationResources();
            var dataDirectory = Directory.CreateTempSubdirectory("boo-app-ack-order-").FullName;
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
            var log = new List<string>();
            try
            {
                window.ShowActivated = false;
                window.Show();
                PumpUntil(() => window.IsLoaded, TimeSpan.FromSeconds(30));
                var status = (TextBlock)window.FindName("FocusStatus");
                var history = (ItemsControl)window.FindName("HistoryList");
                var task = "ack-order " + Guid.NewGuid().ToString("N")[..8];
                string? RowStatus() => (history.ItemsSource as IEnumerable<FocusHistoryRow>)?
                    .FirstOrDefault(row => row.Task == task)?.Status.ToString() ?? "<no row>";
                var watcher = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
                EventHandler onText = (_, _) =>
                {
                    if (status.Text.StartsWith("Running;", StringComparison.Ordinal)
                        || status.Text.StartsWith("Paused.", StringComparison.Ordinal))
                    {
                        log.Add($"ack '{status.Text.Split(' ')[0]}' row={RowStatus()}");
                    }
                };
                watcher.AddValueChanged(status, onText);

                ((TextBox)window.FindName("FocusTaskBox")).Text = task;
                ((TextBox)window.FindName("FocusDurationBox")).Text = "25";
                Click(window, "FocusStartButton");
                PumpWithBackgroundHeld(() => status.Text.StartsWith("Running;", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
                PumpUntil(() => RowStatus() == "Running", TimeSpan.FromSeconds(10));

                Click(window, "FocusPauseButton");
                PumpWithBackgroundHeld(() => status.Text.StartsWith("Paused.", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
                watcher.RemoveValueChanged(status, onText);
                log.Add($"final status='{status.Text}' row={RowStatus()}");
            }
            finally
            {
                window.DisposeShell();
                window.Close();
                PumpUntil(() => true, TimeSpan.FromSeconds(1));
            }
            return log;
        });

        Assert.Contains("ack 'Running;' row=Running", seen);
        Assert.Contains("ack 'Paused.' row=Paused", seen);
        Assert.DoesNotContain(seen, entry => entry.StartsWith("ack 'Paused.'", StringComparison.Ordinal) && entry != "ack 'Paused.' row=Paused");
    }

    private static void Click(Window window, string name) =>
        ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    /// <summary>
    /// Pumps the dispatcher while a self-reposting Input-priority item keeps it busy, so Background and lower work
    /// waits; Normal-priority work (the acknowledgment) and rendering still run. Stops when done() is true.
    /// </summary>
    private static void PumpWithBackgroundHeld(Func<bool> done, TimeSpan timeout)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + timeout;
        void Hold()
        {
            if (done() || DateTime.UtcNow > deadline)
            {
                frame.Continue = false;
                return;
            }
            Thread.Sleep(1);
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Hold));
        }
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(Hold));
        Dispatcher.PushFrame(frame);
        Assert.True(done(), "the acknowledgment did not arrive in time");
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (done())
            {
                return;
            }
            Thread.Sleep(5);
        }
        while (DateTime.UtcNow < deadline);
    }

    /// <summary>MainWindow.xaml needs App.xaml's resources; the Application is never Run(), so OnStartup never runs.</summary>
    private static void EnsureApplicationResources()
    {
        if (Application.Current is not null)
        {
            return;
        }
        var app = new App();
        app.InitializeComponent();
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
        if (!thread.Join(TimeSpan.FromSeconds(120)))
        {
            throw new TimeoutException("The STA test thread did not finish within 120 s.");
        }
        if (failure is not null)
        {
            throw new InvalidOperationException("The STA test body failed: " + failure.Message, failure);
        }
        return result!;
    }
}
