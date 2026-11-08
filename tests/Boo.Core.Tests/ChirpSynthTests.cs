using Boo.Core;

namespace Boo.Core.Tests;

public sealed class ChirpSynthTests
{
    private static int Samples(int sampleRate, int milliseconds) =>
        (int)Math.Round(sampleRate * milliseconds / 1000.0, MidpointRounding.AwayFromZero);

    private static (int ToneStart, int ToneLength, int GapStart, int GapLength, int Tone2Start) Layout(int sampleRate)
    {
        var tone = Samples(sampleRate, 90);
        var gap = Samples(sampleRate, 40);
        return (0, tone, tone, gap, tone + gap);
    }

    private static double EstimateFrequency(short[] pcm, int start, int length, int sampleRate)
    {
        // Measure inside the tone, away from the 5 ms fades.
        var fade = Samples(sampleRate, 5);
        var from = start + fade;
        var to = start + length - fade;
        var crossings = 0;
        var previousSign = 0;
        for (var i = from; i < to; i++)
        {
            var sign = Math.Sign(pcm[i]);
            if (sign == 0)
                continue;
            if (previousSign != 0 && sign != previousSign)
                crossings++;
            previousSign = sign;
        }

        var seconds = (to - from) / (double)sampleRate;
        return crossings / 2.0 / seconds;
    }

    [Fact]
    public void Spec_constants_match_the_design_decision()
    {
        Assert.Equal(880, ChirpSynth.FirstToneHz);
        Assert.Equal(1320, ChirpSynth.SecondToneHz);
        Assert.Equal(90, ChirpSynth.ToneMilliseconds);
        Assert.Equal(40, ChirpSynth.GapMilliseconds);
        Assert.Equal(5, ChirpSynth.FadeMilliseconds);
        Assert.Equal(220, ChirpSynth.TotalMilliseconds);
        Assert.Equal(0.12, ChirpSynth.DefaultVolume);
        Assert.Equal(44100, ChirpSynth.DefaultSampleRate);
    }

    [Theory]
    [InlineData(16000, 3520)]
    [InlineData(44100, 9702)]
    [InlineData(48000, 10560)]
    public void Total_length_is_exactly_220_ms_of_samples(int sampleRate, int expected)
    {
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate);

        Assert.Equal(expected, pcm.Length);
    }

    [Fact]
    public void Default_render_uses_44100_Hz()
    {
        Assert.Equal(9702, ChirpSynth.Render(ChirpKind.Nudge).Length);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Gap_between_tones_is_exact_digital_silence(int sampleRate)
    {
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate);
        var layout = Layout(sampleRate);

        for (var i = layout.GapStart; i < layout.GapStart + layout.GapLength; i++)
            Assert.True(pcm[i] == 0, $"gap sample {i} was {pcm[i]}");
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void First_tone_is_880_Hz_within_three_percent(int sampleRate)
    {
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate);
        var layout = Layout(sampleRate);

        var hz = EstimateFrequency(pcm, layout.ToneStart, layout.ToneLength, sampleRate);

        Assert.InRange(hz, 880 * 0.97, 880 * 1.03);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Second_tone_is_1320_Hz_within_three_percent(int sampleRate)
    {
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate);
        var layout = Layout(sampleRate);

        var hz = EstimateFrequency(pcm, layout.Tone2Start, layout.ToneLength, sampleRate);

        Assert.InRange(hz, 1320 * 0.97, 1320 * 1.03);
    }

    [Theory]
    [InlineData(0.12)]
    [InlineData(0.35)]
    [InlineData(1.0)]
    public void Peak_amplitude_never_exceeds_volume_and_reaches_close_to_it(double volume)
    {
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, 44100, volume);
        var limit = volume * short.MaxValue;

        var peak = pcm.Max(s => Math.Abs((int)s));

        Assert.True(peak <= limit + 1, $"peak {peak} exceeds {limit}");
        Assert.True(peak >= limit * 0.9, $"peak {peak} is far below volume {limit}");
    }

    [Fact]
    public void Default_volume_peak_is_at_most_spec_gain()
    {
        var pcm = ChirpSynth.Render(ChirpKind.Nudge);

        var peak = pcm.Max(s => Math.Abs((int)s));

        Assert.True(peak <= 0.12 * short.MaxValue + 1, $"peak {peak}");
        Assert.True(peak > 0, "default chirp must be audible");
    }

    [Fact]
    public void Zero_volume_renders_full_length_silence()
    {
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, 44100, 0);

        Assert.Equal(9702, pcm.Length);
        Assert.All(pcm, s => Assert.Equal(0, s));
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void Each_tone_starts_and_ends_near_zero(int sampleRate)
    {
        const double volume = 0.35;
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate, volume);
        var layout = Layout(sampleRate);
        var nearZero = 0.02 * volume * short.MaxValue;

        foreach (var start in new[] { layout.ToneStart, layout.Tone2Start })
        {
            var end = start + layout.ToneLength - 1;
            Assert.True(Math.Abs((int)pcm[start]) <= nearZero, $"tone at {start} starts at {pcm[start]}");
            Assert.True(Math.Abs((int)pcm[end]) <= nearZero, $"tone at {start} ends at {pcm[end]}");
        }
    }

    [Fact]
    public void Each_tone_fades_in_and_out_over_five_ms()
    {
        const int sampleRate = 44100;
        const double volume = 0.35;
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate, volume);
        var layout = Layout(sampleRate);
        var oneMs = Samples(sampleRate, 1);
        var fullScale = volume * short.MaxValue;

        foreach (var start in new[] { layout.ToneStart, layout.Tone2Start })
        {
            var end = start + layout.ToneLength;
            var firstMsPeak = pcm.Skip(start).Take(oneMs).Max(s => Math.Abs((int)s));
            var lastMsPeak = pcm.Skip(end - oneMs).Take(oneMs).Max(s => Math.Abs((int)s));
            var middlePeak = pcm.Skip(start + Samples(sampleRate, 10)).Take(Samples(sampleRate, 70)).Max(s => Math.Abs((int)s));

            Assert.True(firstMsPeak <= 0.5 * fullScale, $"fade-in too abrupt at {start}: {firstMsPeak}");
            Assert.True(lastMsPeak <= 0.5 * fullScale, $"fade-out too abrupt at {start}: {lastMsPeak}");
            Assert.True(middlePeak >= 0.9 * fullScale, $"tone body at {start} too quiet: {middlePeak}");
        }
    }

    [Fact]
    public void No_sample_to_sample_jump_larger_than_a_smooth_1320_Hz_sine_allows()
    {
        const int sampleRate = 44100;
        const double volume = 0.35;
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate, volume);
        // Max slope of A*sin(2*pi*1320*t) per sample is A*2*pi*1320/44100 ~= 0.188*A.
        var limit = 0.2 * volume * short.MaxValue;

        for (var i = 1; i < pcm.Length; i++)
        {
            var delta = Math.Abs(pcm[i] - pcm[i - 1]);
            Assert.True(delta <= limit, $"click at sample {i}: jump {delta} > {limit}");
        }
    }

    [Fact]
    public void Session_end_chirp_is_the_same_rising_chirp_as_nudge()
    {
        var nudge = ChirpSynth.Render(ChirpKind.Nudge);
        var sessionEnd = ChirpSynth.Render(ChirpKind.SessionEnd);

        Assert.Equal(nudge, sessionEnd);
    }

    [Fact]
    public void Rendering_is_deterministic()
    {
        Assert.Equal(ChirpSynth.Render(ChirpKind.Nudge, 48000, 0.2), ChirpSynth.Render(ChirpKind.Nudge, 48000, 0.2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-44100)]
    [InlineData(7999)]
    [InlineData(192001)]
    public void Invalid_sample_rate_is_rejected(int sampleRate)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChirpSynth.Render(ChirpKind.Nudge, sampleRate));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Invalid_volume_is_rejected(double volume)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ChirpSynth.Render(ChirpKind.Nudge, 44100, volume));
    }

    [Fact]
    public void Wav_wraps_the_same_pcm_in_a_mono_16_bit_riff_header()
    {
        const int sampleRate = 48000;
        var pcm = ChirpSynth.Render(ChirpKind.Nudge, sampleRate);
        var wav = ChirpSynth.RenderWav(ChirpKind.Nudge, sampleRate);

        Assert.Equal(44 + pcm.Length * 2, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BitConverter.ToInt32(wav, 16));
        Assert.Equal(1, BitConverter.ToInt16(wav, 20)); // PCM
        Assert.Equal(1, BitConverter.ToInt16(wav, 22)); // mono
        Assert.Equal(sampleRate, BitConverter.ToInt32(wav, 24));
        Assert.Equal(sampleRate * 2, BitConverter.ToInt32(wav, 28)); // byte rate
        Assert.Equal(2, BitConverter.ToInt16(wav, 32)); // block align
        Assert.Equal(16, BitConverter.ToInt16(wav, 34)); // bits per sample
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(pcm.Length * 2, BitConverter.ToInt32(wav, 40));
        for (var i = 0; i < pcm.Length; i++)
            Assert.Equal(pcm[i], BitConverter.ToInt16(wav, 44 + i * 2));
    }
}
