using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Threading;
using Boo.App;
using Boo.Core;
using Boo.Providers;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Boo.App.Tests;

/// <summary>
/// Issue #177, review of PR #186 (P2): Stop during recovered playback. Ctrl+Alt+F12, the notch right-click, the tray Stop
/// and the panel Stop button all end in MainWindow.StopAll (through App.StopAll), which this test calls once the recovered
/// speech of OpenAI's real silent reply (run 44022596617) has been handed to the player. Stop must stop that playback and
/// clear the playback clock, and a second silent reply's recovery whose speech arrives after the Stop must never play.
/// In-process with player recorders: a GitHub-hosted VM has no audio output device (waveOutGetNumDevs = 0), so the
/// published app cannot reach "during playback" there.
/// </summary>
public sealed class SilentReplyStopTests
{
    private const int WireChunk = 19200;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void Stop_during_recovered_playback_stops_it_and_a_late_recovery_never_plays()
    {
        var events = RunOnStaThread(() =>
        {
            var log = new List<string>();
            WithWindow(window =>
            {
                ((CheckBox)window.FindName("SpeechMute")).IsChecked = false;
                var deltas = 0;
                var recording = false;
                Set(window, "_playReplyAudio", new Action<byte[]>(audio =>
                {
                    if (recording) log.Add($"played speech={ReplyAudio.SpeechSeconds(audio, 24000) > 0}");
                    else deltas++;
                }));
                Set(window, "_stopReplyAudio", new Action(() => log.Add("player stopped")));
                var lease = (RequestLease)Method("BeginRequest").Invoke(window, null)!;
                var source = new RealtimeClient(() => "fixture-key", new ProviderSettings(), pushToTalk: true);
                Set(window, "_realtime", source);
                Set(window, "_realtimeConnected", true);

                var first = SilentReply(window, source, lease.Generation);
                recording = true;
                first.TrySetResult(Fixture("answer-five-real-speech.wav"));
                Pump();
                log.Add($"clock running={(long)Field(window, "_playbackUntilMs") > Environment.TickCount64}");

                var second = SilentReply(window, source, lease.Generation, before: () => recording = false);
                recording = true;
                window.StopAll();   // what Ctrl+Alt+F12 runs, through App.StopAll
                Pump();
                log.Add($"clock cleared={(long)Field(window, "_playbackUntilMs") <= Environment.TickCount64}");
                second.TrySetResult(Fixture("answer-five-real-speech.wav"));
                Pump();
                Pump();
                log.Add($"deltas={deltas}");
            });
            return log;
        });

        Assert.Equal(
        [
            "played speech=True",
            "clock running=True",
            "player stopped",
            "clock cleared=True",
            "deltas=22",
        ], events);
    }

    /// <summary>Replays OpenAI's silent reply; returns its held recovery speech.</summary>
    private static TaskCompletionSource<byte[]> SilentReply(MainWindow window, RealtimeClient source, long generation, Action? before = null)
    {
        before?.Invoke();
        var speech = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(window, "_speakText", new Func<string, CancellationToken, Task<byte[]>>((_, _) => speech.Task));
        void Send(RealtimeEvent e)
        {
            Method("Realtime_OnReceived").Invoke(window, [source, generation, e]);
            Pump();
        }
        Send(new RealtimeEvent("response.created"));
        var pcm = Fixture("answer-silent-from-provider-177.wav");
        for (var at = 0; at < pcm.Length; at += WireChunk)
        {
            Send(new RealtimeEvent("response.output_audio.delta", Audio: pcm[at..Math.Min(pcm.Length, at + WireChunk)]));
        }
        Send(new RealtimeEvent("response.output_audio_transcript.done", Text: "Five"));
        Send(new RealtimeEvent("response.done"));
        return speech;
    }

    private static MethodInfo Method(string name) =>
        typeof(MainWindow).GetMethod(name, Private) ?? throw new MissingMethodException(nameof(MainWindow), name);

    private static object Field(MainWindow window, string name) =>
        (typeof(MainWindow).GetField(name, Private) ?? throw new MissingFieldException(nameof(MainWindow), name)).GetValue(window)!;

    private static void Set(MainWindow window, string field, object? value) =>
        (typeof(MainWindow).GetField(field, Private) ?? throw new MissingFieldException(nameof(MainWindow), field)).SetValue(window, value);

    private static void WithWindow(Action<MainWindow> body)
    {
        if (Application.Current is null)
        {
            new App().InitializeComponent();
        }
        var window = new MainWindow(
            Directory.CreateTempSubdirectory("boo-app-silent-stop-").FullName,
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
            body(window);
        }
        finally
        {
            Set(window, "_realtime", null);
            Set(window, "_realtimeConnected", false);
            Set(window, "_stopReplyAudio", null);
            window.DisposeShell();
            window.Close();
            Pump();
        }
    }

    private static byte[] Fixture(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "tools", "Boo.LiveSmoke", "Voice", name));
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "data")
            {
                return bytes[(at + 8)..(at + 8 + size - size % 2)];
            }
            at += 8 + size + size % 2;
        }
        throw new InvalidDataException(name + " has no data chunk");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boo.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Boo.sln not found above " + AppContext.BaseDirectory);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
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
            throw new InvalidOperationException("The STA test body failed: " + (failure.InnerException ?? failure).Message, failure);
        }
        return result!;
    }
}
