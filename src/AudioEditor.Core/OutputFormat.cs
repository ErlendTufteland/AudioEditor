namespace AudioEditor.Core;

/// <summary>The sample rate and channel count everything on the timeline is converted to.</summary>
public sealed record OutputFormat(int SampleRate, int Channels)
{
    public static OutputFormat Default { get; } = new(44100, 2);

    /// <summary>
    /// The highest sample rate among the sources (so nothing is downsampled), and stereo
    /// unless every source is mono.
    /// </summary>
    public static OutputFormat Suggest(IEnumerable<AudioSource> sources)
    {
        var list = sources.ToList();
        if (list.Count == 0)
            return Default;

        int sampleRate = list.Max(s => s.SampleRate);
        int channels = list.Any(s => s.Channels > 1) ? 2 : 1;
        return new OutputFormat(sampleRate, channels);
    }
}
