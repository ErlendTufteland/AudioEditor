using System.Globalization;
using NAudio.Wave;

namespace AudioEditor.Core;

/// <summary>
/// An audio file that has been decoded once to find its exact length and waveform.
/// </summary>
public sealed class AudioSource
{
    /// <summary>Resolution of the waveform overview (about 5 ms per bucket).</summary>
    private const int TargetBucketsPerSecond = 200;

    private AudioSource(string filePath, int sampleRate, int channels, long frameCount, WaveformPeaks peaks)
    {
        FilePath = filePath;
        SampleRate = sampleRate;
        Channels = channels;
        FrameCount = frameCount;
        Peaks = peaks;
    }

    public string FilePath { get; }

    public string FileName => Path.GetFileName(FilePath);

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Number of sample frames (one sample per channel) in the file.</summary>
    public long FrameCount { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / SampleRate);

    public WaveformPeaks Peaks { get; }

    /// <summary>For example "MP3 · 44.1 kHz · Stereo".</summary>
    public string Description
    {
        get
        {
            string type = Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
            string channels = Channels switch { 1 => "Mono", 2 => "Stereo", _ => $"{Channels} channels" };
            return string.Create(CultureInfo.InvariantCulture, $"{type} · {SampleRate / 1000.0:0.###} kHz · {channels}");
        }
    }

    /// <summary>
    /// Decodes the whole file. Supports WAV, AIFF and MP3 directly and anything Windows Media
    /// Foundation can read (for example FLAC, M4A/AAC and WMA).
    /// </summary>
    public static AudioSource Load(string filePath, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string fullPath = Path.GetFullPath(filePath);
        using var reader = new AudioFileReader(fullPath);

        var format = reader.WaveFormat;
        int channels = format.Channels;
        int sampleRate = format.SampleRate;
        int framesPerBucket = Math.Max(1, sampleRate / TargetBucketsPerSecond);
        long estimatedFrames = reader.Length / format.BlockAlign;

        int capacity = (int)Math.Clamp(estimatedFrames / framesPerBucket + 1, 16, 1 << 24);
        var minimums = new List<float>(capacity);
        var maximums = new List<float>(capacity);

        var buffer = new float[framesPerBucket * channels * 64];
        float bucketMin = float.MaxValue;
        float bucketMax = float.MinValue;
        int framesInBucket = 0;
        long frames = 0;
        double reported = 0;

        int read;
        while ((read = reader.Read(buffer.AsSpan())) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int readFrames = read / channels;
            for (int frame = 0; frame < readFrames; frame++)
            {
                int offset = frame * channels;
                for (int channel = 0; channel < channels; channel++)
                {
                    float sample = buffer[offset + channel];
                    if (sample < bucketMin) bucketMin = sample;
                    if (sample > bucketMax) bucketMax = sample;
                }

                if (++framesInBucket == framesPerBucket)
                {
                    minimums.Add(bucketMin);
                    maximums.Add(bucketMax);
                    bucketMin = float.MaxValue;
                    bucketMax = float.MinValue;
                    framesInBucket = 0;
                }
            }
            frames += readFrames;

            if (progress is not null && estimatedFrames > 0)
            {
                double fraction = Math.Min(1, (double)frames / estimatedFrames);
                if (fraction - reported >= 0.02)
                {
                    reported = fraction;
                    progress.Report(fraction);
                }
            }
        }

        if (framesInBucket > 0)
        {
            minimums.Add(bucketMin);
            maximums.Add(bucketMax);
        }

        if (frames == 0)
            throw new InvalidDataException("The file contains no audio.");

        progress?.Report(1);
        var peaks = new WaveformPeaks(minimums.ToArray(), maximums.ToArray(), (double)sampleRate / framesPerBucket);
        return new AudioSource(fullPath, sampleRate, channels, frames, peaks);
    }
}
