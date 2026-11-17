using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using System.Windows.Threading;
using Boo.App;
using Boo.Core;
using Boo.Providers;
using Application = System.Windows.Application;
using CheckBox = System.Windows.Controls.CheckBox;

namespace Boo.App.Tests;

/// <summary>
/// Issue #177, the app's half: OpenAI's real silent answer (run 44022596617: 4.05 s of digital silence in 11 audio
/// deltas, transcript "Five") is replayed through MainWindow's own realtime event handler, in-process. Boo must ask for
/// speech of the transcript once and hand audible speech to its player; it must leave a real spoken answer alone, do
/// nothing when speech is muted, and play nothing when Stop comes before the speech arrives. The speech request and the
/// player are swapped for recorders (private fields, set by reflection), so no network or sound device is involved.
/// </summary>
public sealed class SilentReplyWiringTests
{
    private const int WireChunk = 19200;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void A_silent_reply_with_a_transcript_is_read_aloud_and_a_spoken_one_is_left_alone()
    {
        var outcome = RunOnStaThread(() =>
        {
            var lines = new List<string>();
            WithWindow(window =>
            {
                var silent = Replay(window, "answer-silent-from-provider-177.wav", "Five", muted: false, stopBeforeSpeech: false);
                lines.Add($"silent: asked={string.Join("|", silent.Asked)} played={silent.Played.Count} speech={silent.PlayedSpeechSeconds:0.00} status={silent.Status}");
                var spoken = Replay(window, "answer-five-real-speech.wav", "5", muted: false, stopBeforeSpeech: false);
                lines.Add($"spoken: asked={spoken.Asked.Count} played={spoken.Played.Count}");
                var muted = Replay(window, "answer-silent-from-provider-177.wav", "Five", muted: true, stopBeforeSpeech: false);
                lines.Add($"muted: asked={muted.Asked.Count} played={muted.Played.Count}");
                var stopped = Replay(window, "answer-silent-from-provider-177.wav", "Five", muted: false, stopBeforeSpeech: true);
                lines.Add($"stopped: asked={stopped.Asked.Count} played={stopped.Played.Count}");
            });
            return lines;
        });

        Assert.Equal(4, outcome.Count);
        Assert.StartsWith("silent: asked=Five played=1 speech=", outcome[0], StringComparison.Ordinal);
        Assert.EndsWith(" status=" + MainWindowStatus("RecoveredReplyStatus"), outcome[0], StringComparison.Ordinal);
        Assert.DoesNotContain("speech=0.00", outcome[0], StringComparison.Ordinal);
        Assert.Equal("spoken: asked=0 played=0", outcome[1]);
        Assert.Equal("muted: asked=0 played=0", outcome[2]);
        Assert.Equal("stopped: asked=1 played=0", outcome[3]);
    }

    private sealed record Run(List<string> Asked, List<byte[]> Played, string Status)
    {
        public double PlayedSpeechSeconds => Played.Sum(audio => ReplyAudio.SpeechSeconds(audio, 24000));
    }

    /// <summary>Replays one reply's frames through MainWindow's realtime handler for a freshly begun request.</summary>
    private static Run Replay(MainWindow window, string fixture, string transcript, bool muted, bool stopBeforeSpeech)
    {
        var asked = new List<string>();
        var played = new List<byte[]>();
        var speech = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(window, "_speakText", new Func<string, CancellationToken, Task<byte[]>>((text, _) =>
        {
            asked.Add(text);
            return speech.Task;
        }));
        Set(window, "_playReplyAudio", new Action<byte[]>(played.Add));
        ((CheckBox)window.FindName("SpeechMute")).IsChecked = muted;

        var lease = (RequestLease)Method("BeginRequest").Invoke(window, null)!;
        var source = new RealtimeClient(() => "fixture-key", new ProviderSettings(), pushToTalk: true);
        Set(window, "_realtime", source);
        Set(window, "_realtimeConnected", true);
        void Send(RealtimeEvent e)
        {
            Method("Realtime_OnReceived").Invoke(window, [source, lease.Generation, e]);
            Pump();
        }

        Send(new RealtimeEvent("response.created"));
        var pcm = Fixture(fixture);
        for (var at = 0; at < pcm.Length; at += WireChunk)
        {
            Send(new RealtimeEvent("response.output_audio.delta", Audio: pcm[at..Math.Min(pcm.Length, at + WireChunk)]));
        }
        Send(new RealtimeEvent("response.output_audio_transcript.done", Text: transcript));
        Send(new RealtimeEvent("response.done"));
        if (stopBeforeSpeech)
        {
            window.StopAll();
            Pump();
        }
        speech.TrySetResult(Fixture("answer-five-real-speech.wav"));
        Pump();
        Pump();
        var status = ((TextBlock)window.FindName("StatusText")).Text;
        Set(window, "_realtime", null);
        Set(window, "_realtimeConnected", false);
        return new Run(asked, played, status);
    }

    private static string MainWindowStatus(string name) =>
        (string)(typeof(MainWindow).GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetRawConstantValue()
                 ?? throw new MissingFieldException(nameof(MainWindow), name));

    private static MethodInfo Method(string name) =>
        typeof(MainWindow).GetMethod(name, Private) ?? throw new MissingMethodException(nameof(MainWindow), name);

    private static void Set(MainWindow window, string field, object? value) =>
        (typeof(MainWindow).GetField(field, Private) ?? throw new MissingFieldException(nameof(MainWindow), field)).SetValue(window, value);

    private static void WithWindow(Action<MainWindow> body)
    {
        if (Application.Current is null)
        {
            new App().InitializeComponent();
        }
        var window = new MainWindow(
            Directory.CreateTempSubdirectory("boo-app-silent-reply-").FullName,
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
