using NAudio.Wave;

namespace AudioEditor.Core;

/// <summary>
/// Reads the trimmed part of one clip in the source file's own sample rate and channel
/// layout, with the clip's gain and fades applied.
/// </summary>
public sealed class ClipReader : ISampleProvider, IDisposable
{
    private readonly AudioFileReader reader;
    private readonly int channels;
    private readonly long startFrame;
    private readonly long endFrame;
    private readonly long fadeInFrames;
    private readonly long fadeOutFrames;
    private readonly float gain;
    private long position;

    /// <param name="clip">The clip to read.</param>
    /// <param name="offset">How far into the clip (after the trim start) to begin.</param>
    public ClipReader(ClipSpec clip, TimeSpan offset = default)
    {
        ArgumentNullException.ThrowIfNull(clip);

        reader = new AudioFileReader(clip.FilePath);
        try
        {
            var format = reader.WaveFormat;
            int sampleRate = format.SampleRate;
            channels = format.Channels;

            startFrame = Math.Max(0, ToFrames(clip.TrimStart, sampleRate));
            endFrame = Math.Max(startFrame, ToFrames(clip.TrimEnd, sampleRate));
            fadeInFrames = Math.Max(0, ToFrames(clip.FadeIn, sampleRate));
            fadeOutFrames = Math.Max(0, ToFrames(clip.FadeOut, sampleRate));
            gain = (float)Math.Pow(10, clip.GainDb / 20);

            position = Math.Clamp(startFrame + ToFrames(offset, sampleRate), startFrame, endFrame);
            if (position > 0)
                reader.Position = position * format.BlockAlign;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    public WaveFormat WaveFormat => reader.WaveFormat;

    /// <summary>True once the end of the clip (or of the file) has been reached.</summary>
    public bool IsFinished { get; private set; }

    public int Read(Span<float> buffer)
    {
        long remaining = endFrame - position;
        if (IsFinished || remaining <= 0)
        {
            IsFinished = true;
            return 0;
        }

        int frames = (int)Math.Min(buffer.Length / channels, remaining);
        if (frames == 0)
            return 0;

        var target = buffer[..(frames * channels)];
        int read = reader.Read(target);
        read -= read % channels;
        if (read == 0)
        {
            IsFinished = true;
            return 0;
        }

        ApplyEnvelope(target[..read]);
        position += read / channels;
        if (position >= endFrame)
            IsFinished = true;
        return read;
    }

    public void Dispose() => reader.Dispose();

    internal static long ToFrames(TimeSpan time, int sampleRate) =>
        (long)Math.Round(time.Ticks * (double)sampleRate / TimeSpan.TicksPerSecond);

    private void ApplyEnvelope(Span<float> samples)
    {
        long clipFrames = endFrame - startFrame;
        long first = position - startFrame;
        int frames = samples.Length / channels;

        bool inFadeIn = first < fadeInFrames;
        bool inFadeOut = fadeOutFrames > 0 && first + frames > clipFrames - fadeOutFrames;
        if (gain == 1f && !inFadeIn && !inFadeOut)
            return;

        for (int frame = 0; frame < frames; frame++)
        {
            long index = first + frame;
            float factor = gain;
            if (index < fadeInFrames)
                factor *= (float)index / fadeInFrames;
            long left = clipFrames - 1 - index;
            if (left < fadeOutFrames)
                factor *= (float)left / fadeOutFrames;

            int offset = frame * channels;
            for (int channel = 0; channel < channels; channel++)
                samples[offset + channel] *= factor;
        }
    }
}
