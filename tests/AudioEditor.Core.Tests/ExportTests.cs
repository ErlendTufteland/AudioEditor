using NAudio.Wave;
using Xunit.Abstractions;

namespace AudioEditor.Core.Tests;

public class ExportTests(ITestOutputHelper output)
{
    [Fact]
    public void Lists_the_encoders_on_this_machine()
    {
        foreach (var format in Enum.GetValues<ExportFormat>())
            output.WriteLine($"{format}: {(AudioExporter.IsAvailable(format) ? "available" : "missing")}");

        Assert.True(AudioExporter.IsAvailable(ExportFormat.Wav16));
    }

    [Fact]
    public void Wav_export_contains_exactly_the_trimmed_clips()
    {
        using var folder = new TempFolder();
        string a = TestAudio.WriteRamp(folder.File("a.wav"), 48000, 48000);
        string b = TestAudio.WriteRamp(folder.File("b.wav"), 48000, 24000, sign: -1);
        var clips = new[]
        {
            new ClipSpec(a, TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.75)),
            new ClipSpec(b, TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.4)),
        };
        string output = folder.File("out.wav");

        AudioExporter.Export(clips, output, new ExportOptions(ExportFormat.WavFloat, new OutputFormat(48000, 1)));

        float[] samples = TestAudio.ReadAll(output, out var format);
        Assert.Equal(48000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(24000 + 14400, samples.Length);
        Assert.Equal(TestAudio.RampValue(12000), samples[0]);
        Assert.Equal(TestAudio.RampValue(35999), samples[23999]);
        Assert.Equal(TestAudio.RampValue(4800, -1), samples[24000]);
        Assert.Equal(TestAudio.RampValue(19199, -1), samples[^1]);
        Assert.Single(Directory.GetFiles(folder.Path, "out*"));
    }

    [Theory]
    [InlineData(ExportFormat.Wav16, 16)]
    [InlineData(ExportFormat.Wav24, 24)]
    public void Pcm_wav_export_uses_the_chosen_bit_depth(ExportFormat format, int bits)
    {
        using var folder = new TempFolder();
        string tone = TestAudio.WriteSine(folder.File("tone.wav"), 44100, 2, seconds: 0.5);
        string output = folder.File("out.wav");

        AudioExporter.Export([new ClipSpec(tone, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))], output,
            new ExportOptions(format, new OutputFormat(44100, 2)));

        using var reader = new WaveFileReader(output);
        Assert.Equal(bits, reader.WaveFormat.BitsPerSample);
        Assert.Equal(22050, reader.SampleCount);
    }

    [Theory]
    [EncoderData(ExportFormat.Mp3)]
    [EncoderData(ExportFormat.Aac)]
    [EncoderData(ExportFormat.Flac)]
    public void Encoded_export_can_be_read_back(ExportFormat format)
    {
        using var folder = new TempFolder();
        string tone = TestAudio.WriteSine(folder.File("tone.wav"), 48000, 2, seconds: 2);
        string output = folder.File("out" + AudioExporter.GetExtension(format));
        var clips = new[] { new ClipSpec(tone, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(2)) };

        AudioExporter.Export(clips, output, new ExportOptions(format, new OutputFormat(44100, 2), BitRate: 192_000));

        var exported = AudioSource.Load(output);
        Assert.InRange(exported.Duration.TotalSeconds, 1.45, 1.6);
        Assert.Equal(44100, exported.SampleRate);
        Assert.Single(Directory.GetFiles(folder.Path, "out*"));
    }

    [EncoderFact(ExportFormat.Mp3)]
    public void Unsupported_sample_rate_is_replaced_by_the_closest_supported_one()
    {
        using var folder = new TempFolder();
        string tone = TestAudio.WriteSine(folder.File("tone.wav"), 96000, 2, seconds: 0.5);
        string output = folder.File("out.mp3");

        AudioExporter.Export([new ClipSpec(tone, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))], output,
            new ExportOptions(ExportFormat.Mp3, new OutputFormat(96000, 2)));

        Assert.Equal(48000, AudioSource.Load(output).SampleRate);
    }

    [Fact]
    public void Cancelled_export_leaves_no_files_behind()
    {
        using var folder = new TempFolder();
        string tone = TestAudio.WriteSine(folder.File("tone.wav"), 44100, 2, seconds: 1);
        string output = folder.File("out.wav");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => AudioExporter.Export(
            [new ClipSpec(tone, TimeSpan.Zero, TimeSpan.FromSeconds(1))], output,
            new ExportOptions(ExportFormat.Wav16, OutputFormat.Default), cancellationToken: cancellation.Token));

        Assert.Empty(Directory.GetFiles(folder.Path, "out*"));
    }

    [Fact]
    public void Existing_file_is_replaced()
    {
        using var folder = new TempFolder();
        string tone = TestAudio.WriteSine(folder.File("tone.wav"), 44100, 1, seconds: 0.5);
        string output = folder.File("out.wav");
        File.WriteAllText(output, "not audio");

        AudioExporter.Export([new ClipSpec(tone, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))], output,
            new ExportOptions(ExportFormat.Wav16, new OutputFormat(44100, 1)));

        Assert.Equal(22050, AudioSource.Load(output).FrameCount);
    }

    [Fact]
    public void Export_refuses_to_overwrite_a_source_file()
    {
        using var folder = new TempFolder();
        string tone = TestAudio.WriteSine(folder.File("tone.wav"), 44100, 1, seconds: 0.5);

        Assert.Throws<InvalidOperationException>(() => AudioExporter.Export(
            [new ClipSpec(tone, TimeSpan.Zero, TimeSpan.FromSeconds(0.5))], tone,
            new ExportOptions(ExportFormat.Wav16, OutputFormat.Default)));
    }

    [Fact]
    public void Export_requires_at_least_one_clip_with_length()
    {
        using var folder = new TempFolder();

        Assert.Throws<InvalidOperationException>(() => AudioExporter.Export(
            [], folder.File("out.wav"), new ExportOptions(ExportFormat.Wav16, OutputFormat.Default)));
    }
}
