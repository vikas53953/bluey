using Boo.Windows;
using NAudio.Wave;

namespace Boo.NativeVoice.Unit;

/// <summary>
/// PR #186 review (Codex BLOCK on 4d30786): the Stop proof used by SilentReplyRecoveryJourney, run against Boo's real
/// AudioSession and the hosted virtual output it uses on CI. Controls: a Stop pressed after the audio already finished
/// fails as TEST SETUP; a Stop that does nothing (the behavioural mutation) fails; a real Stop, or Boo closing the
/// session, passes.
/// </summary>
public sealed class RecoveredStopCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "boo-stop-check-" + Guid.NewGuid().ToString("N"));
    private readonly string _receipt;
    private readonly HostedVoiceAudio _device;
    private readonly AudioSession _session;

    public RecoveredStopCheckTests()
    {
        Directory.CreateDirectory(_root);
        var wav = Path.Combine(_root, "input.wav");
        using (var writer = new WaveFileWriter(wav, new WaveFormat(24000, 16, 1))) writer.Write(new byte[4800]);
        _receipt = Path.Combine(_root, "device.jsonl");
        _device = new HostedVoiceAudio(wav, _receipt);
        _session = new AudioSession(_device);
    }

    public void Dispose()
    {
        _session.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static byte[] Speech(int seconds10ths) =>
        Enumerable.Range(0, RecoveredStopCheck.BytesPerSecond * seconds10ths / 10).Select(n => (byte)(n % 251 + 1)).ToArray();

    private PlaybackReceipt WaitFor(Func<PlaybackReceipt, bool> condition)
    {
        for (var i = 0; i < 250; i++)
        {
            Thread.Sleep(20);
            if (PlaybackReceipt.ReadLast(_receipt) is { } r && condition(r)) return r;
        }
        throw new TimeoutException("TEST SETUP: the virtual output never reached the expected state.");
    }

    [Fact]
    public void A_stop_after_the_audio_finished_fails_as_test_setup()
    {
        var audio = Speech(2); // 0.2 s
        _session.Play(audio);
        var finished = WaitFor(r => r.RenderedBytes == audio.Length && r.BufferedBytes == 0);

        var failure = RecoveredStopCheck.PreconditionFailure(finished);

        Assert.NotNull(failure);
        Assert.StartsWith("TEST SETUP:", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_stop_while_audio_is_queued_passes()
    {
        _session.Play(Speech(20)); // 2 s
        var before = WaitFor(r => r.RenderedBytes >= 4800);
        Assert.Null(RecoveredStopCheck.PreconditionFailure(before));

        _session.Stop();
        Thread.Sleep(300);

        Assert.Null(RecoveredStopCheck.InterruptionFailure(before, PlaybackReceipt.ReadLast(_receipt)));
    }

    [Fact]
    public void A_stop_that_does_nothing_fails()
    {
        _session.Play(Speech(20));
        var before = WaitFor(r => r.RenderedBytes >= 4800);
        Assert.Null(RecoveredStopCheck.PreconditionFailure(before));

        Thread.Sleep(600); // the mutation: Stop is a no-op, playback goes on

        var failure = RecoveredStopCheck.InterruptionFailure(before, PlaybackReceipt.ReadLast(_receipt));
        Assert.NotNull(failure);
        Assert.Contains("continued after Stop", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void Boo_closing_the_session_writes_a_final_receipt_with_nothing_queued()
    {
        _session.Play(Speech(20));
        var before = WaitFor(r => r.RenderedBytes >= 4800);
        Thread.Sleep(30); // the clock in the final receipt must move past the one read above

        _session.Dispose();

        var after = PlaybackReceipt.ReadLast(_receipt);
        Assert.Null(RecoveredStopCheck.InterruptionFailure(before, after));
        Assert.Equal(0, after!.BufferedBytes);
    }
}
