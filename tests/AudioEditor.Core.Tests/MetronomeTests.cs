using NAudio.Wave;

namespace AudioEditor.Core.Tests;

public class MetronomeTests
{
    private const int Rate = 48000;
    private static readonly int ClickFrames = (int)(MetronomeSampleProvider.ClickSeconds * Rate);

    [Fact]
    public void Clicks_on_every_beat_and_accents_the_bar()
    {
        // 120 BPM: a beat every 24000 frames.
        var metronome = new MetronomeSampleProvider(new Silence(1, Rate * 5 / 2)) { Map = new TempoMap(120), Volume = 1 };

        float[] samples = TestAudio.ReadAll(metronome);

        Assert.Equal(Rate * 5 / 2, samples.Length);
        foreach (int beat in new[] { 0, 1, 2, 3, 4 })
            Assert.True(Peak(samples, beat * 24000) > 0.3f, $"no click on beat {beat}");
        Assert.Equal(0f, Peak(samples, 24000 + ClickFrames, 24000 - ClickFrames));
        Assert.True(Peak(samples, 0) > Peak(samples, 24000) * 1.4f);
        Assert.True(Peak(samples, 4 * 24000) > Peak(samples, 3 * 24000) * 1.4f);
    }

    [Fact]
    public void Starting_part_way_keeps_the_clicks_on_the_beat()
    {
        var metronome = new MetronomeSampleProvider(new Silence(1, Rate), timelineStart: 0.3) { Map = new TempoMap(120) };

        float[] samples = TestAudio.ReadAll(metronome);

        // The beat at 0.5 s on the timeline is 0.2 s in.
        Assert.Equal(0f, Peak(samples, 0, 9600));
        Assert.True(Peak(samples, 9600) > 0);
    }

    [Fact]
    public void A_click_that_started_earlier_is_heard_to_its_end()
    {
        var metronome = new MetronomeSampleProvider(new Silence(1, Rate), timelineStart: 0.01) { Map = new TempoMap(120) };

        float[] samples = TestAudio.ReadAll(metronome);

        Assert.True(Peak(samples, 0, 24) > 0);
    }

    [Fact]
    public void Tempo_changes_move_the_clicks()
    {
        // 120 BPM for bar 1 (2 s), then 60 BPM: beats at 2 s, 3 s, 4 s.
        var map = new TempoMap([new TempoChange(1, 120), new TempoChange(2, 60)]);
        var metronome = new MetronomeSampleProvider(new Silence(1, Rate * 4), timelineStart: 1.9) { Map = map };

        float[] samples = TestAudio.ReadAll(metronome);

        Assert.True(Peak(samples, (int)(0.1 * Rate)) > 0);
        Assert.Equal(0f, Peak(samples, (int)(0.1 * Rate) + ClickFrames, Rate - ClickFrames));
        Assert.True(Peak(samples, (int)(1.1 * Rate)) > 0);
    }

    [Fact]
    public void Silent_without_a_map_or_volume()
    {
        var withoutMap = new MetronomeSampleProvider(new Silence(1, Rate));
        var withoutVolume = new MetronomeSampleProvider(new Silence(1, Rate)) { Map = new TempoMap(), Volume = 0 };

        Assert.All(TestAudio.ReadAll(withoutMap), s => Assert.Equal(0f, s));
        Assert.All(TestAudio.ReadAll(withoutVolume), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Same_result_whatever_the_buffer_size_and_on_every_channel()
    {
        var map = new TempoMap(137, beatsPerBar: 3);
        var whole = TestAudio.ReadAll(new MetronomeSampleProvider(new Silence(2, Rate * 2), 0.123) { Map = map });

        var pieces = new List<float>();
        var small = new MetronomeSampleProvider(new Silence(2, Rate * 2), 0.123) { Map = map };
        var buffer = new float[2 * 333];
        int read;
        while ((read = small.Read(buffer)) > 0)
            pieces.AddRange(buffer.AsSpan(0, read).ToArray());

        Assert.Equal(whole, pieces);
        for (int frame = 0; frame < whole.Length / 2; frame++)
            Assert.Equal(whole[2 * frame], whole[2 * frame + 1]);
    }

    private static float Peak(float[] samples, int from, int? length = null)
    {
        int count = Math.Min(length ?? ClickFrames, samples.Length - from);
        return samples.AsSpan(from, count).ToArray().Max(Math.Abs);
    }

    /// <summary>A given number of silent frames.</summary>
    private sealed class Silence(int channels, int frames) : ISampleProvider
    {
        private int left = frames;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, channels);

        public int Read(Span<float> buffer)
        {
            int count = Math.Min(left, buffer.Length / channels);
            buffer[..(count * channels)].Clear();
            left -= count;
            return count * channels;
        }
    }
}
