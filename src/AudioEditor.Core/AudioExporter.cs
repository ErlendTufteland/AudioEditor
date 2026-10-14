using System.Collections.Concurrent;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AudioEditor.Core;

public enum ExportFormat
{
    Wav16,
    Wav24,
    WavFloat,
    Flac,
    Mp3,
    Aac,
}

/// <param name="Format">File type and sample encoding.</param>
/// <param name="Output">Sample rate and channel count of the exported file.</param>
/// <param name="BitRate">Bits per second for MP3 and AAC; ignored for lossless formats.</param>
public sealed record ExportOptions(ExportFormat Format, OutputFormat Output, int BitRate = 192_000);

/// <summary>Renders the timeline to a new audio file.</summary>
public static class AudioExporter
{
    private const long Rf64Threshold = 3_900_000_000;

    private static readonly int[] AnySampleRate = [8000, 11025, 16000, 22050, 32000, 44100, 48000, 88200, 96000, 176400, 192000];
    private static readonly int[] Mp3SampleRates = [32000, 44100, 48000];
    private static readonly int[] AacSampleRates = [44100, 48000];
    private static readonly int[] Mp3BitRates = [128_000, 160_000, 192_000, 224_000, 256_000, 320_000];
    private static readonly int[] AacBitRates = [96_000, 128_000, 160_000, 192_000];

    private static readonly ConcurrentDictionary<ExportFormat, bool> Availability = new();

    public static string GetExtension(ExportFormat format) => format switch
    {
        ExportFormat.Flac => ".flac",
        ExportFormat.Mp3 => ".mp3",
        ExportFormat.Aac => ".m4a",
        _ => ".wav",
    };

    public static bool IsLossy(ExportFormat format) => format is ExportFormat.Mp3 or ExportFormat.Aac;

    /// <summary>Sample rates the format's encoder accepts.</summary>
    public static IReadOnlyList<int> GetSampleRates(ExportFormat format) => format switch
    {
        ExportFormat.Mp3 => Mp3SampleRates,
        ExportFormat.Aac => AacSampleRates,
        _ => AnySampleRate,
    };

    /// <summary>Bit rates (bits per second) offered for the format; empty for lossless formats.</summary>
    public static IReadOnlyList<int> GetBitRates(ExportFormat format) => format switch
    {
        ExportFormat.Mp3 => Mp3BitRates,
        ExportFormat.Aac => AacBitRates,
        _ => [],
    };

    /// <summary>The supported sample rate closest to <paramref name="sampleRate"/>.</summary>
    public static int ClosestSampleRate(ExportFormat format, int sampleRate) =>
        GetSampleRates(format).MinBy(rate => Math.Abs(rate - sampleRate));

    /// <summary>
    /// Whether Windows has an encoder for the format. WAV is always available; FLAC, MP3 and
    /// AAC use Media Foundation encoders, which can be missing (for example on N editions).
    /// </summary>
    public static bool IsAvailable(ExportFormat format) => Availability.GetOrAdd(format, static f =>
    {
        try
        {
            switch (f)
            {
                case ExportFormat.Flac:
                    MediaFoundationApi.Startup();
                    var types = MediaFoundationEncoder.GetOutputMediaTypes(AudioSubtypes.MFAudioFormat_FLAC);
                    foreach (var type in types)
                        type.Dispose();
                    return types.Length > 0;
                case ExportFormat.Mp3:
                    MediaFoundationApi.Startup();
                    return MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_MP3, 44100, 2).Length > 0;
                case ExportFormat.Aac:
                    MediaFoundationApi.Startup();
                    return MediaFoundationEncoder.GetEncodeBitrates(AudioSubtypes.MFAudioFormat_AAC, 44100, 2).Length > 0;
                default:
                    return true;
            }
        }
        catch (Exception)
        {
            return false;
        }
    });

    /// <summary>
    /// Writes the clips one after another to <paramref name="outputPath"/>. The file is
    /// written under a temporary name first, so a cancelled or failed export leaves any
    /// existing file untouched.
    /// </summary>
    public static void Export(
        IReadOnlyList<ClipSpec> clips,
        string outputPath,
        ExportOptions options,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);

        var playable = clips.Where(c => c.Length > TimeSpan.Zero).ToList();
        if (playable.Count == 0)
            throw new InvalidOperationException("There is nothing to export. Add at least one clip.");

        string target = Path.GetFullPath(outputPath);
        if (playable.Any(c => string.Equals(Path.GetFullPath(c.FilePath), target, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The export would overwrite a file that is used on the timeline. Choose another file name.");

        var output = options.Output;
        if (!GetSampleRates(options.Format).Contains(output.SampleRate))
            output = output with { SampleRate = ClosestSampleRate(options.Format, output.SampleRate) };

        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory,
            $"{Path.GetFileNameWithoutExtension(target)}.partial-{Guid.NewGuid():N}{Path.GetExtension(target)}");

        long totalFrames = playable.Sum(c => ClipReader.ToFrames(c.Length, output.SampleRate));

        try
        {
            using (var timeline = new TimelineSampleProvider(playable, output))
            {
                IWaveProvider samples = options.Format switch
                {
                    ExportFormat.Wav24 => new SampleToWaveProvider24(timeline),
                    ExportFormat.WavFloat => new SampleToWaveProvider(timeline),
                    _ => new SampleToWaveProvider16(timeline),
                };
                long totalBytes = totalFrames * samples.WaveFormat.BlockAlign;
                var source = new ProgressWaveProvider(samples, totalBytes, progress, cancellationToken);

                switch (options.Format)
                {
                    case ExportFormat.Flac:
                        MediaFoundationApi.Startup();
                        MediaFoundationEncoder.EncodeToFlac(source, temporary);
                        break;
                    case ExportFormat.Mp3:
                        MediaFoundationApi.Startup();
                        MediaFoundationEncoder.EncodeToMp3(source, temporary, options.BitRate);
                        break;
                    case ExportFormat.Aac:
                        MediaFoundationApi.Startup();
                        MediaFoundationEncoder.EncodeToAac(source, temporary, options.BitRate);
                        break;
                    default:
                        WriteWave(source, temporary, totalBytes > Rf64Threshold);
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void WriteWave(IWaveProvider source, string path, bool large)
    {
        var options = new WaveFileWriterOptions { EnableRf64 = large };
        using var writer = new WaveFileWriter(path, source.WaveFormat, options);

        var format = source.WaveFormat;
        var buffer = new byte[format.AverageBytesPerSecond / 4 / format.BlockAlign * format.BlockAlign];
        int read;
        while ((read = source.Read(buffer)) > 0)
            writer.Write(buffer, 0, read);
    }

    private static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch (IOException)
            {
                // An encoder may still hold the file until its COM objects are released.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    /// <summary>Reports how much has been written and stops the export when cancelled.</summary>
    private sealed class ProgressWaveProvider(
        IWaveProvider source,
        long expectedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken) : IWaveProvider
    {
        private long bytes;
        private double reported;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<byte> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = source.Read(buffer);
            bytes += read;

            if (progress is not null && expectedBytes > 0)
            {
                double fraction = Math.Min(0.99, (double)bytes / expectedBytes);
                if (fraction - reported >= 0.005)
                {
                    reported = fraction;
                    progress.Report(fraction);
                }
            }
            return read;
        }
    }
}
