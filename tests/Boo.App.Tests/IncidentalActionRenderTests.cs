using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using Boo.App;
using Boo.Core;
using Application = System.Windows.Application;

namespace Boo.App.Tests;

/// <summary>
/// Issue #181 (j15e), the app's half: MainWindow's own reply rendering must show the exact acknowledgment
/// "Boo suggested an action, but no explicit task request authorized it." when a plain question's reply carries an
/// action, and must never show it for an explicit task. The real MainWindow runs in-process on one dispatcher and its
/// reply renderer (the private RenderReply, called through reflection so no app code changes for the test) gets
/// hand-made replies; the status line is read straight after each call. A renderer that showed the generic completion
/// (mutation M03) or inverted the task condition (M04) fails here.
/// </summary>
public sealed class IncidentalActionRenderTests
{
    private const string Acknowledgment = "Boo suggested an action, but no explicit task request authorized it.";
    private const string NoObservation = "Boo proposed a screen action, but no screen observation backs it. Nothing was offered.";
    private const string Completion = "Reply complete. No action was committed.";

    [Fact]
    public void A_plain_questions_action_is_acknowledged_and_an_explicit_task_never_gets_that_acknowledgment()
    {
        var seen = RunOnStaThread(() =>
        {
            EnsureApplicationResources();
            var window = new MainWindow(
                Directory.CreateTempSubdirectory("boo-app-incidental-").FullName,
                onClose: () => { },
                onStop: () => { },
                onDisplayChanged: _ => { },
                verticalOffset: () => 0,
                ownedWindows: () => Array.Empty<long>(),
                captureExclusion: () => true,
                onCaptureActivity: (_, _) => { },
                onMicrophoneActivity: _ => { },
                onAppearanceChanged: _ => { });
            try
            {
                window.ShowActivated = false;
                window.Show();
                Pump();
                var status = (TextBlock)window.FindName("StatusText");
                var write = new ToolRequest("write_file", "MainWindow.xaml",
                    new Dictionary<string, string> { ["path"] = "MainWindow.xaml", ["content"] = "QA-EDIT" }, 0);
                var click = new ToolRequest("click", "Save button",
                    new Dictionary<string, string> { ["automationId"] = "save", ["name"] = "Save" }, 0);

                string Render(bool explicitTask, params ToolRequest[] actions)
                {
                    var offered = Invoke(window, new AssistantReply("An answer.", Array.Empty<SourceLink>(), null, actions), explicitTask);
                    Pump();
                    return (offered is null ? "offered=none " : "offered=" + ((ToolRequest)offered).Name + " ") + status.Text;
                }

                return new List<string>
                {
                    "plain+write: " + Render(false, write),
                    "plain+click: " + Render(false, click),
                    "plain+none: " + Render(false),
                    "task+click-unobserved: " + Render(true, click),
                };
            }
            finally
            {
                window.DisposeShell();
                window.Close();
                Pump();
            }
        });

        Assert.Equal(
            [
                "plain+write: offered=none " + Acknowledgment,
                "plain+click: offered=none " + Acknowledgment,
                "plain+none: offered=none " + Completion,
                "task+click-unobserved: offered=none " + NoObservation,
            ],
            seen);
    }

    /// <summary>Calls MainWindow's private reply renderer for the current request generation, as a reply would arrive.</summary>
    private static object? Invoke(MainWindow window, AssistantReply reply, bool explicitTask)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        var generation = typeof(MainWindow).GetField("_generation", Private)
            ?? throw new MissingFieldException(nameof(MainWindow), "_generation");
        var render = typeof(MainWindow).GetMethod("RenderReply", Private)
            ?? throw new MissingMethodException(nameof(MainWindow), "RenderReply");
        return render.Invoke(window, [reply, (long)generation.GetValue(window)!, null, explicitTask, null, false]);
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

    private static void Pump()
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
            throw new InvalidOperationException("The STA test body failed: " + (failure.InnerException ?? failure).Message, failure);
        }
        return result!;
    }
}
