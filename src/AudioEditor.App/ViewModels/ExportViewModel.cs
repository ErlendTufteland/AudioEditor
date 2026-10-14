using System.Collections.ObjectModel;
using System.Globalization;
using AudioEditor.App.Services;
using AudioEditor.Core;

namespace AudioEditor.App.ViewModels;

public sealed record Choice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

public sealed record FormatChoice(ExportFormat Format, string Name, bool IsAvailable)
{
    public override string ToString() => Name;
}

public sealed class ExportViewModel : ObservableObject
{
    private FormatChoice selectedFormat;
    private Choice<int>? selectedSampleRate;
    private Choice<int>? selectedBitRate;
    private Choice<int> selectedChannels;
    private int preferredSampleRate;
    private int preferredBitRate;

    public ExportViewModel(ExportDialogInput input)
    {
        Input = input;
        FormatChoice[] all =
        [
            new(ExportFormat.Wav16, "WAV – 16-bit PCM", true),
            new(ExportFormat.Wav24, "WAV – 24-bit PCM", true),
            new(ExportFormat.WavFloat, "WAV – 32-bit float", true),
            new(ExportFormat.Flac, "FLAC – lossless", AudioExporter.IsAvailable(ExportFormat.Flac)),
            new(ExportFormat.Mp3, "MP3", AudioExporter.IsAvailable(ExportFormat.Mp3)),
            new(ExportFormat.Aac, "AAC (.m4a)", AudioExporter.IsAvailable(ExportFormat.Aac)),
        ];
        Formats = all.Where(f => f.IsAvailable).ToList();
        var missing = all.Where(f => !f.IsAvailable).Select(f => f.Name.Split(' ')[0]).ToList();
        MissingFormatsText = missing.Count == 0
            ? string.Empty
            : $"{string.Join(", ", missing)} export needs an encoder that is missing from this Windows installation.";
        Channels = [new(2, "Stereo"), new(1, "Mono")];

        var previous = input.Previous;
        preferredSampleRate = previous?.Output.SampleRate ?? input.Suggested.SampleRate;
        preferredBitRate = previous?.BitRate ?? 192_000;
        selectedFormat = Formats.FirstOrDefault(f => f.Format == previous?.Format && f.IsAvailable) ?? Formats[0];
        selectedChannels = Channels.First(c => c.Value == (previous?.Output.Channels ?? input.Suggested.Channels));
        RefreshChoices();

        Summary = $"{input.ClipCount} {(input.ClipCount == 1 ? "clip" : "clips")} · {TimeFormat.Format(input.Length.TotalSeconds)} in total";
    }

    public ExportDialogInput Input { get; }

    public IReadOnlyList<FormatChoice> Formats { get; }

    public string MissingFormatsText { get; }

    public bool HasMissingFormats => MissingFormatsText.Length > 0;

    public IReadOnlyList<Choice<int>> Channels { get; }

    public ObservableCollection<Choice<int>> SampleRates { get; } = [];

    public ObservableCollection<Choice<int>> BitRates { get; } = [];

    public string Summary { get; }

    public FormatChoice SelectedFormat
    {
        get => selectedFormat;
        set
        {
            if (value is null)
                return;
            if (SetProperty(ref selectedFormat, value))
            {
                RefreshChoices();
                OnPropertyChanged(nameof(IsLossy));
            }
        }
    }

    public bool IsLossy => AudioExporter.IsLossy(selectedFormat.Format);

    public Choice<int>? SelectedSampleRate
    {
        get => selectedSampleRate;
        set
        {
            if (SetProperty(ref selectedSampleRate, value) && value is not null)
                preferredSampleRate = value.Value;
        }
    }

    public Choice<int> SelectedChannels
    {
        get => selectedChannels;
        set
        {
            if (value is not null)
                SetProperty(ref selectedChannels, value);
        }
    }

    public Choice<int>? SelectedBitRate
    {
        get => selectedBitRate;
        set
        {
            if (SetProperty(ref selectedBitRate, value) && value is not null)
                preferredBitRate = value.Value;
        }
    }

    public string Extension => AudioExporter.GetExtension(selectedFormat.Format);

    public string FileFilter => selectedFormat.Format switch
    {
        ExportFormat.Flac => "FLAC audio|*.flac",
        ExportFormat.Mp3 => "MP3 audio|*.mp3",
        ExportFormat.Aac => "AAC audio|*.m4a;*.mp4",
        _ => "WAV audio|*.wav",
    };

    /// <summary>Extensions the chosen format may be saved with; the first one is the default.</summary>
    public IReadOnlyList<string> AllowedExtensions => selectedFormat.Format == ExportFormat.Aac
        ? [".m4a", ".mp4"]
        : [Extension];

    public ExportOptions BuildOptions() => new(
        selectedFormat.Format,
        new OutputFormat(selectedSampleRate?.Value ?? preferredSampleRate, selectedChannels.Value),
        selectedBitRate?.Value ?? preferredBitRate);

    private void RefreshChoices()
    {
        int sampleRate = preferredSampleRate;
        int bitRate = preferredBitRate;
        var format = selectedFormat.Format;

        SampleRates.Clear();
        foreach (int rate in AudioExporter.GetSampleRates(format))
        {
            string name = rate.ToString("#,0", CultureInfo.InvariantCulture).Replace(',', ' ') + " Hz";
            if (rate == Input.Suggested.SampleRate)
                name += " (source)";
            SampleRates.Add(new(rate, name));
        }
        int closestRate = AudioExporter.ClosestSampleRate(format, sampleRate);
        SelectedSampleRate = SampleRates.First(r => r.Value == closestRate);

        BitRates.Clear();
        foreach (int rate in AudioExporter.GetBitRates(format))
            BitRates.Add(new(rate, $"{rate / 1000} kbps"));
        SelectedBitRate = BitRates.Count == 0 ? null : BitRates.MinBy(r => Math.Abs(r.Value - bitRate));

        preferredSampleRate = sampleRate;
        preferredBitRate = bitRate;
        OnPropertyChanged(nameof(Extension));
        OnPropertyChanged(nameof(FileFilter));
    }
}
