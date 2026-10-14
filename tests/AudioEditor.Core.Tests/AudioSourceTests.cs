using NAudio.Wave;

namespace AudioEditor.Core.Tests;

public class AudioSourceTests
{
    [Fact]
    public void Load_measures_exact_length_format_and_peaks()
    {
        using var folder = new TempFolder();
        string path = TestAudio.WriteSine(folder.File("tone.wav"), 44100, 2, seconds: 1.5, amplitude: 0.5);

        var source = AudioSource.Load(path);

        Assert.Equal(66150, source.FrameCount);
        Assert.Equal(44100, source.SampleRate);
        Assert.Equal(2, source.Channels);
        Assert.Equal(1.5, source.Duration.TotalSeconds, precision: 6);
        Assert.Equal("WAV · 44.1 kHz · Stereo", source.Description);

        var (min, max) = source.Peaks.GetRange(0, 1.5);
        Assert.InRange(max, 0.49f, 0.51f);
        Assert.InRange(min, -0.51f, -0.49f);
        Assert.Equal((0f, 0f), source.Peaks.GetRange(10, 11));
    }

    [Fact]
    public void Load_reports_progress_up_to_complete()
    {
        using var folder = new TempFolder();
        string path = TestAudio.WriteSine(folder.File("tone.wav"), 48000, 1, seconds: 2);
        var reports = new List<double>();

        AudioSource.Load(path, new SynchronousProgress(reports.Add));

        Assert.NotEmpty(reports);
        Assert.Equal(1, reports[^1]);
        Assert.Equal(reports.OrderBy(r => r), reports);
    }

    [Fact]
    public void Load_rejects_a_file_without_audio()
    {
        using var folder = new TempFolder();
        string path = folder.File("empty.wav");
        using (new WaveFileWriter(path, new WaveFormat(44100, 16, 2))) { }

        Assert.Throws<InvalidDataException>(() => AudioSource.Load(path));
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
