using System.Globalization;
using System.IO;
using AudioEditor.App.Controls;
using AudioEditor.Core;

namespace AudioEditor.App.ViewModels;

/// <summary>One clip on the timeline. Times are in seconds into the source file.</summary>
public sealed class ClipViewModel : ObservableObject
{
    private const double MinimumBlockWidth = 110;

    /// <summary>The clip's settings from a project, applied once the file has loaded.</summary>
    private readonly ProjectClip? saved;

    private AudioSource? source;
    private bool isLoading = true;
    private double loadProgress;
    private double trimStart;
    private double trimEnd;
    private double gainDb;
    private double fadeIn;
    private double fadeOut;
    private double editorPosition;
    private double timelinePlayhead = double.NaN;
    private double timelineStart;
    private double pixelsPerSecond = 40;

    /// <param name="saved">
    /// The clip's settings from a project. Trims and fades depend on the file's length, so they
    /// are applied once it has loaded.
    /// </param>
    public ClipViewModel(string filePath, ProjectClip? saved = null)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
        this.saved = saved;
        if (saved is not null)
            gainDb = ClampGain(saved.GainDb);
    }

    public string FilePath { get; }

    public string FileName { get; }

    public AudioSource? Source => source;

    public bool IsLoaded => source is not null;

    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    public double LoadProgress
    {
        get => loadProgress;
        set => SetProperty(ref loadProgress, value);
    }

    public WaveformPeaks? Peaks => source?.Peaks;

    public double Duration => source?.Duration.TotalSeconds ?? 0;

    public string Description => source is null
        ? "Loading…"
        : $"{source.Description} · {TimeFormat.Format(Duration)}";

    public double TrimStart
    {
        get => trimStart;
        set
        {
            if (double.IsNaN(value))
                return;
            value = Math.Clamp(value, 0, Math.Max(0, trimEnd - WaveformView.MinimumLength));
            if (SetProperty(ref trimStart, value))
                OnTrimChanged();
        }
    }

    public double TrimEnd
    {
        get => trimEnd;
        set
        {
            if (double.IsNaN(value))
                return;
            value = Math.Clamp(value, Math.Min(Duration, trimStart + WaveformView.MinimumLength), Duration);
            if (SetProperty(ref trimEnd, value))
                OnTrimChanged();
        }
    }

    public double Length => Math.Max(0, trimEnd - trimStart);

    public string LengthText => TimeFormat.Format(Length);

    public bool IsTrimmed => IsLoaded && (trimStart > 0 || trimEnd < Duration);

    public double GainDb
    {
        get => gainDb;
        set
        {
            if (double.IsNaN(value))
                return;
            if (SetProperty(ref gainDb, ClampGain(value)))
            {
                OnPropertyChanged(nameof(GainLinear));
                OnPropertyChanged(nameof(GainText));
                OnPropertyChanged(nameof(GainBadge));
            }
        }
    }

    /// <summary>The volume change for the timeline block; empty when there is none.</summary>
    public string GainBadge => gainDb == 0 ? string.Empty : GainText;

    public double GainLinear => Math.Pow(10, gainDb / 20);

    public string GainText => gainDb == 0
        ? "0 dB"
        : gainDb.ToString("+0.0 dB;-0.0 dB", CultureInfo.InvariantCulture);

    public double FadeIn
    {
        get => fadeIn;
        set
        {
            if (!double.IsNaN(value))
                SetProperty(ref fadeIn, Math.Clamp(value, 0, Duration));
        }
    }

    public double FadeOut
    {
        get => fadeOut;
        set
        {
            if (!double.IsNaN(value))
                SetProperty(ref fadeOut, Math.Clamp(value, 0, Duration));
        }
    }

    /// <summary>The cursor in the clip editor.</summary>
    public double EditorPosition
    {
        get => editorPosition;
        set
        {
            if (!double.IsNaN(value))
                SetProperty(ref editorPosition, Math.Clamp(value, 0, Duration));
        }
    }

    /// <summary>Where the timeline playhead is inside this clip, or NaN when it is elsewhere.</summary>
    public double TimelinePlayhead
    {
        get => timelinePlayhead;
        set => SetProperty(ref timelinePlayhead, value);
    }

    /// <summary>Where the clip starts on the timeline, in seconds.</summary>
    public double TimelineStart
    {
        get => timelineStart;
        set
        {
            if (SetProperty(ref timelineStart, value))
                OnPropertyChanged(nameof(TimelineOrigin));
        }
    }

    /// <summary>
    /// Where the start of the source file would be on the timeline. Adding it to a time in the
    /// file gives the time on the timeline, which is what the beat grid uses.
    /// </summary>
    public double TimelineOrigin => timelineStart - trimStart;

    public double PixelsPerSecond
    {
        get => pixelsPerSecond;
        set
        {
            if (SetProperty(ref pixelsPerSecond, value))
                OnPropertyChanged(nameof(BlockWidth));
        }
    }

    public double BlockWidth => Math.Max(MinimumBlockWidth, Length * pixelsPerSecond);

    public bool Contains(double sourceTime) => IsLoaded && sourceTime >= trimStart && sourceTime < trimEnd;

    public void SetSource(AudioSource loaded)
    {
        source = loaded;
        double duration = loaded.Duration.TotalSeconds;
        trimStart = 0;
        trimEnd = duration;
        if (saved is not null)
        {
            // Kept inside the file the way the TrimEnd and TrimStart setters do, in case it changed.
            trimEnd = Math.Clamp(saved.TrimEnd ?? duration, Math.Min(duration, WaveformView.MinimumLength), duration);
            trimStart = Math.Clamp(saved.TrimStart, 0, Math.Max(0, trimEnd - WaveformView.MinimumLength));
            fadeIn = Math.Clamp(saved.FadeIn, 0, duration);
            fadeOut = Math.Clamp(saved.FadeOut, 0, duration);
        }
        editorPosition = trimStart;
        isLoading = false;
        OnPropertyChanged(string.Empty);
    }

    public void ResetTrim()
    {
        TrimStart = 0;
        TrimEnd = Duration;
    }

    /// <summary>Used as the accessible name of the clip on the timeline.</summary>
    public override string ToString() => FileName;

    public ClipSpec ToSpec() => new(FilePath, TimeSpan.FromSeconds(trimStart), TimeSpan.FromSeconds(trimEnd))
    {
        GainDb = gainDb,
        FadeIn = TimeSpan.FromSeconds(Math.Min(fadeIn, Length)),
        FadeOut = TimeSpan.FromSeconds(Math.Min(fadeOut, Length)),
    };

    /// <summary>
    /// The clip as a project saves it. An end at the end of the file is saved as such, so the clip
    /// still plays to the end if the file is replaced by a longer one. Until the file has loaded,
    /// this is what the clip was opened with.
    /// </summary>
    public ProjectClip ToProjectClip() => source is null
        ? (saved ?? new ProjectClip(FilePath)) with { GainDb = gainDb }
        : new ProjectClip(FilePath)
        {
            TrimStart = trimStart,
            TrimEnd = trimEnd < Duration ? trimEnd : null,
            GainDb = gainDb,
            FadeIn = fadeIn,
            FadeOut = fadeOut,
        };

    private static double ClampGain(double db) => Math.Clamp(Math.Round(db, 1), -30, 12);

    private void OnTrimChanged()
    {
        OnPropertyChanged(nameof(Length));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(BlockWidth));
        OnPropertyChanged(nameof(IsTrimmed));
        OnPropertyChanged(nameof(TimelineOrigin));
    }
}
