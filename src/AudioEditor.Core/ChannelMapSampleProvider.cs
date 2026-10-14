using NAudio.Wave;

namespace AudioEditor.Core;

/// <summary>
/// Converts any channel count to mono or stereo. Mono is the average of all channels;
/// stereo from mono duplicates the channel, and stereo from surround keeps the front pair.
/// </summary>
public sealed class ChannelMapSampleProvider : ISampleProvider
{
    private readonly ISampleProvider source;
    private readonly int inputChannels;
    private readonly int outputChannels;
    private float[] sourceBuffer = [];

    public ChannelMapSampleProvider(ISampleProvider source, int channels)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), "Only mono and stereo output is supported.");

        this.source = source;
        inputChannels = source.WaveFormat.Channels;
        outputChannels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(Span<float> buffer)
    {
        int frames = buffer.Length / outputChannels;
        int needed = frames * inputChannels;
        if (sourceBuffer.Length < needed)
            sourceBuffer = new float[needed];

        int read = source.Read(sourceBuffer.AsSpan(0, needed)) / inputChannels;
        for (int frame = 0; frame < read; frame++)
        {
            var input = sourceBuffer.AsSpan(frame * inputChannels, inputChannels);
            if (outputChannels == 1)
            {
                float sum = 0;
                foreach (float sample in input)
                    sum += sample;
                buffer[frame] = sum / inputChannels;
            }
            else
            {
                buffer[frame * 2] = input[0];
                buffer[frame * 2 + 1] = inputChannels == 1 ? input[0] : input[1];
            }
        }
        return read * outputChannels;
    }
}
