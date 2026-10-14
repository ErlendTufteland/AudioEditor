using NAudio.Wave;

namespace AudioEditor.Core.Tests;

/// <summary>A temporary folder that is deleted after the test.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AudioEditorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

internal static class TestAudio
{
    /// <summary>
    /// A float WAV where every sample tells which frame it is, so tests can check exactly
    /// which frames were kept: frame f has the value <c>sign * (f + 1) * 1e-5</c>.
    /// </summary>
    public static string WriteRamp(string path, int sampleRate, int frames, float sign = 1)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        for (int f = 0; f < frames; f++)
            writer.WriteSample(RampValue(f, sign));
        return path;
    }

    public static float RampValue(long frame, float sign = 1) => sign * (frame + 1) * 1e-5f;

    public static string WriteConstant(string path, int sampleRate, int frames, float value)
    {
        using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        for (int f = 0; f < frames; f++)
            writer.WriteSample(value);
        return path;
    }

    /// <summary>A 16-bit PCM sine tone.</summary>
    public static string WriteSine(string path, int sampleRate, int channels, double seconds, double frequency = 440, double amplitude = 0.5)
    {
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, channels));
        int frames = (int)Math.Round(seconds * sampleRate);
        var buffer = new short[frames * channels];
        for (int f = 0; f < frames; f++)
        {
            short value = (short)Math.Round(Math.Sin(2 * Math.PI * frequency * f / sampleRate) * amplitude * short.MaxValue);
            for (int c = 0; c < channels; c++)
                buffer[f * channels + c] = value;
        }
        writer.WriteSamples(buffer, 0, buffer.Length);
        return path;
    }

    public static float[] ReadAll(ISampleProvider provider)
    {
        var result = new List<float>();
        var buffer = new float[4096];
        int read;
        while ((read = provider.Read(buffer)) > 0)
            result.AddRange(buffer.AsSpan(0, read).ToArray());
        return result.ToArray();
    }

    public static float[] ReadAll(string path, out WaveFormat format)
    {
        using var reader = new AudioFileReader(path);
        format = reader.WaveFormat;
        return ReadAll(reader);
    }
}
