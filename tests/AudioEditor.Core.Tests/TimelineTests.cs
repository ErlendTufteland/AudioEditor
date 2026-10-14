namespace AudioEditor.Core.Tests;

public class TimelineTests
{
    private static readonly OutputFormat Mono48k = new(48000, 1);

    [Fact]
    public void Clips_play_trimmed_and_in_order_sample_exact()
    {
        using var folder = new TempFolder();
        string a = TestAudio.WriteRamp(folder.File("a.wav"), 48000, 48000);
        string b = TestAudio.WriteRamp(folder.File("b.wav"), 48000, 24000, sign: -1);
        var clips = new[]
        {
            new ClipSpec(a, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.75)),
            new ClipSpec(b, TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.4)),
        };

        using var timeline = new TimelineSampleProvider(clips, Mono48k);
        float[] samples = TestAudio.ReadAll(timeline);

        var expected = Enumerable.Range(12000, 24000).Select(f => TestAudio.RampValue(f))
            .Concat(Enumerable.Range(4800, 14400).Select(f => TestAudio.RampValue(f, -1)))
            .ToArray();
        Assert.Equal(expected, samples);
    }

    [Fact]
    public void Starting_part_way_begins_inside_the_right_clip()
    {
        using var folder = new TempFolder();
        string a = TestAudio.WriteRamp(folder.File("a.wav"), 48000, 48000);
        string b = TestAudio.WriteRamp(folder.File("b.wav"), 48000, 48000, sign: -1);
        var clips = new[]
        {
            new ClipSpec(a, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)),
            new ClipSpec(b, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(0.7)),
        };

        using var timeline = new TimelineSampleProvider(clips, Mono48k, startAt: TimeSpan.FromSeconds(0.6));
        float[] samples = TestAudio.ReadAll(timeline);

        // 0.1 s into clip b, which starts 0.2 s into its file: frame 14400.
        Assert.Equal(TestAudio.RampValue(14400, -1), samples[0]);
        Assert.Equal(19200, samples.Length);
    }

    [Fact]
    public void Different_rates_and_channel_counts_are_converted()
    {
        using var folder = new TempFolder();
        string mono = TestAudio.WriteSine(folder.File("mono.wav"), 44100, 1, seconds: 1);
        string stereo = TestAudio.WriteSine(folder.File("stereo.wav"), 48000, 2, seconds: 0.5);
        var clips = new[]
        {
            new ClipSpec(mono, TimeSpan.Zero, TimeSpan.FromSeconds(1)),
            new ClipSpec(stereo, TimeSpan.Zero, TimeSpan.FromSeconds(0.5)),
        };

        using var timeline = new TimelineSampleProvider(clips, new OutputFormat(48000, 2));
        float[] samples = TestAudio.ReadAll(timeline);

        double seconds = samples.Length / 2.0 / 48000;
        Assert.InRange(seconds, 1.495, 1.505);
        Assert.InRange(samples.Max(), 0.45f, 0.55f);
    }

    [Fact]
    public void Stereo_is_averaged_to_mono()
    {
        using var folder = new TempFolder();
        string stereo = TestAudio.WriteSine(folder.File("stereo.wav"), 48000, 2, seconds: 0.25, amplitude: 0.8);
        var clips = new[] { new ClipSpec(stereo, TimeSpan.Zero, TimeSpan.FromSeconds(0.25)) };

        using var timeline = new TimelineSampleProvider(clips, Mono48k);
        float[] samples = TestAudio.ReadAll(timeline);

        Assert.Equal(12000, samples.Length);
        Assert.InRange(samples.Max(), 0.79f, 0.81f);
    }

    [Fact]
    public void Gain_and_fades_shape_the_clip()
    {
        using var folder = new TempFolder();
        string path = TestAudio.WriteConstant(folder.File("dc.wav"), 48000, 48000, 0.5f);
        var clip = new ClipSpec(path, TimeSpan.Zero, TimeSpan.FromSeconds(1))
        {
            GainDb = 20 * Math.Log10(0.5),
            FadeIn = TimeSpan.FromSeconds(0.1),
            FadeOut = TimeSpan.FromSeconds(0.1),
        };

        using var reader = new ClipReader(clip);
        float[] samples = TestAudio.ReadAll(reader);

        Assert.Equal(48000, samples.Length);
        Assert.Equal(0f, samples[0]);
        Assert.Equal(0.125f, samples[2400], 0.001f);
        Assert.Equal(0.25f, samples[24000], 0.0001f);
        Assert.Equal(0.125f, samples[48000 - 1 - 2400], 0.001f);
        Assert.Equal(0f, samples[^1]);
    }

    [Fact]
    public void Clips_without_length_are_skipped()
    {
        using var folder = new TempFolder();
        string a = TestAudio.WriteRamp(folder.File("a.wav"), 48000, 4800);
        var clips = new[]
        {
            new ClipSpec(a, TimeSpan.FromSeconds(0.05), TimeSpan.FromSeconds(0.05)),
            new ClipSpec(a, TimeSpan.Zero, TimeSpan.FromSeconds(0.1)),
        };

        using var timeline = new TimelineSampleProvider(clips, Mono48k);

        Assert.Equal(4800, TestAudio.ReadAll(timeline).Length);
    }

    [Theory]
    [InlineData(0.0, 0, 0.0)]
    [InlineData(0.3, 0, 0.3)]
    [InlineData(0.5, 1, 0.0)]
    [InlineData(1.2, 1, 0.7)]
    [InlineData(1.5, 2, 0.0)]
    [InlineData(9.0, 2, 0.0)]
    public void Locate_finds_clip_and_offset(double time, int index, double offset)
    {
        var clips = new[]
        {
            new ClipSpec("a", TimeSpan.Zero, TimeSpan.FromSeconds(0.5)),
            new ClipSpec("b", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
        };

        var located = Timeline.Locate(clips, TimeSpan.FromSeconds(time));

        Assert.Equal(index, located.Index);
        Assert.Equal(offset, located.Offset.TotalSeconds, precision: 6);
        Assert.Equal(1.5, Timeline.TotalLength(clips).TotalSeconds, precision: 6);
    }
}
