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
/// Issue #177, review of PR #186 (P1): a silent-reply recovery belongs to one reply. On the SAME voice session and request
/// (no new BeginRequest, as push-to-talk reuses them), a newer reply or new spoken input makes reply A's pending recovery
/// stale: when A's speech arrives late it is not played and the status is not overwritten. And success is claimed only
/// when the player accepted audible speech: a player failure, silent recovery speech, a failed speech request or a
/// timeout each end on "unavailable", never on "read the answer aloud". Real MainWindow, in-process; OpenAI's real silent
/// reply (run 44022596617) is replayed; the speech request and the player are recorders set by reflection.
/// </summary>
public sealed class SilentReplyLifecycleTests
{
    private const int WireChunk = 19200;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void A_stale_or_failed_recovery_never_plays_late_and_never_claims_success()
    {
        var outcome = RunOnStaThread(() =>
        {
            var lines = new List<string>();
            WithWindow(window =>
            {
                var session = Open(window);

                // Reply A is silent; its speech is held. Reply B starts on the same session before A's speech arrives.
                var a = Silent(window, session);
                Send(window, session, new RealtimeEvent("response.created"));
                a.Speech.TrySetResult(Fixture("answer-five-real-speech.wav"));
                Pump();
                lines.Add($"newer reply: recovered={session.Played.Count - a.PlayedBefore} status={Status(window) == Status("RecoveredReplyStatus")}");

                // New spoken input (the user starts talking again) on the same session makes a pending recovery stale too.
                var c = Silent(window, session);
                Send(window, session, new RealtimeEvent("input_audio_buffer.committed"));
                c.Speech.TrySetResult(Fixture("answer-five-real-speech.wav"));
                Pump();
                lines.Add($"new input: recovered={session.Played.Count - c.PlayedBefore} status={Status(window) == Status("RecoveredReplyStatus")}");

                lines.Add("player fails: " + Failure(window, session, speech => speech.TrySetResult(Fixture("answer-five-real-speech.wav")), playerThrows: true));
                lines.Add("silent speech: " + Failure(window, session, speech => speech.TrySetResult(new byte[48000]), playerThrows: false));
                lines.Add("request fails: " + Failure(window, session, speech => speech.TrySetException(new InvalidOperationException("speech failed")), playerThrows: false));
                lines.Add("timeout: " + Failure(window, session, speech => speech.TrySetException(new TaskCanceledException("timed out")), playerThrows: false));
            });
            return lines;
        });

        var unavailable = MainWindowConst("RecoveryUnavailableStatus");
        Assert.Equal(
        [
            "newer reply: recovered=0 status=False",
            "new input: recovered=0 status=False",
            "player fails: " + unavailable,
            "silent speech: " + unavailable,
            "request fails: " + unavailable,
            "timeout: " + unavailable,
        ], outcome);
    }

    private sealed record Session(RealtimeClient Source, long Generation, List<byte[]> Played);

    /// <summary>A silent reply's held recovery speech, and how much the player had received when the reply completed.</summary>
    private sealed record Pending(TaskCompletionSource<byte[]> Speech, int PlayedBefore);

    /// <summary>One request and one voice session, reused by every reply below (as push-to-talk does).</summary>
    private static Session Open(MainWindow window)
    {
        ((CheckBox)window.FindName("SpeechMute")).IsChecked = false;
        var played = new List<byte[]>();
        Set(window, "_playReplyAudio", new Action<byte[]>(played.Add));
        var lease = (RequestLease)Method("BeginRequest").Invoke(window, null)!;
        var source = new RealtimeClient(() => "fixture-key", new ProviderSettings(), pushToTalk: true);
        Set(window, "_realtime", source);
        Set(window, "_realtimeConnected", true);
        return new Session(source, lease.Generation, played);
    }

    /// <summary>Replays OpenAI's silent reply on the session; its recovery speech is held until the caller completes it.</summary>
    private static Pending Silent(MainWindow window, Session session)
    {
        var speech = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(window, "_speakText", new Func<string, CancellationToken, Task<byte[]>>((_, _) => speech.Task));
        Send(window, session, new RealtimeEvent("response.created"));
        var pcm = Fixture("answer-silent-from-provider-177.wav");
        for (var at = 0; at < pcm.Length; at += WireChunk)
        {
            Send(window, session, new RealtimeEvent("response.output_audio.delta", Audio: pcm[at..Math.Min(pcm.Length, at + WireChunk)]));
        }
        Send(window, session, new RealtimeEvent("response.output_audio_transcript.done", Text: "Five"));
        var before = session.Played.Count;
        Send(window, session, new RealtimeEvent("response.done"));
        return new Pending(speech, before);
    }

    /// <summary>A silent reply whose recovery ends in <paramref name="finish"/>; returns the final status line.</summary>
    private static string Failure(MainWindow window, Session session, Action<TaskCompletionSource<byte[]>> finish, bool playerThrows)
    {
        var played = session.Played;
        Set(window, "_playReplyAudio", new Action<byte[]>(played.Add));
        var pending = Silent(window, session);
        if (playerThrows)
        {
            Set(window, "_playReplyAudio", new Action<byte[]>(_ => throw new InvalidOperationException("No playback device.")));
        }
        finish(pending.Speech);
        Pump();
        Pump();
        return Status(window);
    }

    private static void Send(MainWindow window, Session session, RealtimeEvent realtimeEvent)
    {
        Method("Realtime_OnReceived").Invoke(window, [session.Source, session.Generation, realtimeEvent]);
        Pump();
    }

    private static string Status(MainWindow window) => ((TextBlock)window.FindName("StatusText")).Text;

    private static string Status(string constant) => MainWindowConst(constant);

    private static string MainWindowConst(string name) =>
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
            Directory.CreateTempSubdirectory("boo-app-silent-life-").FullName,
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
