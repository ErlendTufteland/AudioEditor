using NAudio.Wave;

namespace AudioEditor.Core;

/// <summary>
/// Mixes a metronome click into another provider: one on every beat of a tempo map, higher and
/// louder on the first beat of each bar. It ends when the source ends. Meant for listening, so
/// <see cref="Map"/>, <see cref="Volume"/> and <see cref="TimelineStart"/> can be changed from
/// another thread while it plays.
/// </summary>
public sealed class MetronomeSampleProvider : ISampleProvider, IDisposable
{
    public const double ClickSeconds = 0.04;
    public const double AccentFrequency = 1760;
    public const double BeatFrequency = 1320;

    /// <summary>How quickly a click dies away; it is practically silent by its end.</summary>
    private const double DecaySeconds = 0.007;

    /// <summary>Level of the other beats compared with the first beat of a bar.</summary>
    private const float BeatLevel = 0.6f;

    private readonly ISampleProvider source;
    private readonly int channels;
    private readonly int sampleRate;
    private volatile TempoMap? map;
    private volatile float volume = 0.5f;
    private double timelineStart;
    private long framesRead;

    /// <param name="source">The audio to add the click to.</param>
    /// <param name="timelineStart">Where on the timeline (in seconds) the source starts.</param>
    public MetronomeSampleProvider(ISampleProvider source, double timelineStart = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        this.source = source;
        channels = source.WaveFormat.Channels;
        sampleRate = source.WaveFormat.SampleRate;
        this.timelineStart = timelineStart;
    }

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>The beats to click on. Null is silent.</summary>
    public TempoMap? Map
    {
        get => map;
        set => map = value;
    }

    /// <summary>Peak level of the loudest click, from 0 to 1.</summary>
    public float Volume
    {
        get => volume;
        set => volume = Math.Clamp(value, 0, 1);
    }

    /// <summary>Where on the timeline the source starts, in seconds.</summary>
    public double TimelineStart
    {
        get => Volatile.Read(ref timelineStart);
        set => Volatile.Write(ref timelineStart, value);
    }

    public int Read(Span<float> buffer)
    {
        int read = source.Read(buffer);
        int frames = read / channels;
        var beats = map;
        float level = volume;
        if (beats is not null && level > 0 && frames > 0)
            AddClicks(buffer[..(frames * channels)], beats, level, TimelineStart + (double)framesRead / sampleRate);
        framesRead += frames;
        return read;
    }

    public void Dispose() => (source as IDisposable)?.Dispose();

    /// <param name="start">The timeline time of the first frame in <paramref name="samples"/>.</param>
    private void AddClicks(Span<float> samples, TempoMap beats, float level, double start)
    {
        int frames = samples.Length / channels;
        double end = start + (double)frames / sampleRate;

        // Every beat whose click sounds during this block, including one that started just before it.
        long first = (long)Math.Ceiling(beats.BeatAt(start - ClickSeconds) - 1e-9);
        long last = (long)Math.Floor(beats.BeatAt(end));
        for (long beat = first; beat <= last; beat++)
        {
            double time = beats.TimeAt(beat);
            bool accent = beat % beats.BeatsPerBar == 0;
            double frequency = accent ? AccentFrequency : BeatFrequency;
            float peak = accent ? level : level * BeatLevel;

            // The tolerance keeps rounding errors from moving a click's first or last frame.
            int from = Math.Max(0, (int)Math.Ceiling((time - start) * sampleRate - 1e-6));
            int to = Math.Min(frames, (int)Math.Ceiling((time + ClickSeconds - start) * sampleRate - 1e-6));
            for (int frame = from; frame < to; frame++)
            {
                double t = start + (double)frame / sampleRate - time;
                float value = peak * (float)(Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t / DecaySeconds));
                for (int channel = 0; channel < channels; channel++)
                    samples[frame * channels + channel] += value;
            }
        }
    }
}
