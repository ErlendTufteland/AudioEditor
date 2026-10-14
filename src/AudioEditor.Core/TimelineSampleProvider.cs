using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioEditor.Core;

/// <summary>
/// Plays clips one after another, converted to a common sample rate and channel count.
/// Each file is opened when its clip starts and closed when it ends, on the thread that
/// reads from this provider.
/// </summary>
public sealed class TimelineSampleProvider : ISampleProvider, IDisposable
{
    /// <summary>Calls in a row that may return nothing before a clip is treated as done.</summary>
    private const int MaxEmptyReads = 4;

    private readonly ClipSpec[] clips;
    private int nextIndex;
    private TimeSpan nextOffset;
    private ClipReader? clipReader;
    private ISampleProvider? current;
    private int emptyReads;

    /// <param name="clips">The clips in playing order.</param>
    /// <param name="format">The format every clip is converted to.</param>
    /// <param name="startAt">Where on the timeline to start.</param>
    public TimelineSampleProvider(IEnumerable<ClipSpec> clips, OutputFormat format, TimeSpan startAt = default)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(format);

        this.clips = clips.Where(c => c.Length > TimeSpan.Zero).ToArray();
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);
        (nextIndex, nextOffset) = Timeline.Locate(this.clips, startAt);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int channels = WaveFormat.Channels;
        int requested = buffer.Length - buffer.Length % channels;
        int total = 0;

        while (total < requested)
        {
            if (current is null && !OpenNext())
                break;

            int read = current!.Read(buffer[total..requested]);
            total += read;
            if (read > 0)
            {
                emptyReads = 0;
                continue;
            }

            // A resampler can return nothing while it still holds input, so only move on
            // once the clip itself is used up.
            if (clipReader!.IsFinished || ++emptyReads > MaxEmptyReads)
                CloseCurrent();
        }
        return total;
    }

    public void Dispose()
    {
        CloseCurrent();
        nextIndex = clips.Length;
    }

    private bool OpenNext()
    {
        if (nextIndex >= clips.Length)
            return false;

        var clip = clips[nextIndex++];
        clipReader = new ClipReader(clip, nextOffset);
        nextOffset = TimeSpan.Zero;
        current = Convert(clipReader, WaveFormat.SampleRate, WaveFormat.Channels);
        emptyReads = 0;
        return true;
    }

    private void CloseCurrent()
    {
        clipReader?.Dispose();
        clipReader = null;
        current = null;
    }

    private static ISampleProvider Convert(ISampleProvider source, int sampleRate, int channels)
    {
        // Change the channel count on the side where there are fewer channels to resample.
        var provider = source;
        bool fewerChannels = channels < provider.WaveFormat.Channels;
        if (fewerChannels)
            provider = new ChannelMapSampleProvider(provider, channels);
        if (provider.WaveFormat.SampleRate != sampleRate)
            provider = new WdlResamplingSampleProvider(provider, sampleRate);
        if (!fewerChannels && provider.WaveFormat.Channels != channels)
            provider = new ChannelMapSampleProvider(provider, channels);
        return provider;
    }
}
