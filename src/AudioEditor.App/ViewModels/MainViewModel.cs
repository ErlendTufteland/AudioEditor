using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using AudioEditor.App.Services;
using AudioEditor.Core;

namespace AudioEditor.App.ViewModels;

public enum PlaybackMode
{
    None,

    /// <summary>The finished result: all clips, trimmed, one after another.</summary>
    Timeline,

    /// <summary>The selected clip's whole source file, to find where to trim.</summary>
    Preview,
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Margin and border around each block on the timeline, in pixels.</summary>
    public const double TimelineItemChrome = 10;

    private const string AppName = "Audio Editor";
    private const double MinPixelsPerSecond = 0.2;
    private const double MaxPixelsPerSecond = 2000;
    private const int MaxParallelLoads = 3;

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".m4a", ".aac", ".wma", ".aif", ".aiff", ".mp4",
    };

    private static readonly HashSet<string> ArrangementProperties =
    [
        string.Empty,
        nameof(ClipViewModel.TrimStart),
        nameof(ClipViewModel.TrimEnd),
        nameof(ClipViewModel.GainDb),
        nameof(ClipViewModel.FadeIn),
        nameof(ClipViewModel.FadeOut),
    ];

    private readonly IDialogService dialogs;
    private readonly RecentFiles recentFiles;
    private readonly PlaybackService playback = new();
    private readonly DispatcherTimer positionTimer;
    private readonly DispatcherTimer resumeTimer;
    private readonly DispatcherTimer fitTimer;
    private readonly SemaphoreSlim loadGate = new(MaxParallelLoads);

    private ClipViewModel? selectedClip;
    private double pixelsPerSecond = 40;
    private bool autoFit = true;
    private double timelineViewportWidth;
    private double playheadTime;
    private PlaybackMode mode;
    private PlaybackMode resumeMode;
    private double playbackOrigin;
    private ClipViewModel? previewClip;
    private MetronomeSampleProvider? metronome;
    private bool updatingPositions;
    private int loadingCount;
    private bool isExporting;
    private double exportProgress;
    private string statusText = "Add audio files to get started.";
    private string? lastExportPath;
    private CancellationTokenSource? exportCancellation;
    private ExportOptions? lastExportOptions;
    private string? lastExportFolder;

    /// <summary>The open project's file, or null until it is saved.</summary>
    private string? projectPath;

    private bool isDirty;

    /// <summary>The clips and tempo map as they were opened or last saved, to tell whether anything has changed.</summary>
    private IReadOnlyList<ProjectClip> savedClips = [];

    private TempoMap savedMap = new();

    /// <summary>Goes up with every project opened or started, so files that finish loading late don't report on an old one.</summary>
    private int projectGeneration;

    public MainViewModel(IDialogService dialogs, RecentFiles recentFiles)
    {
        this.dialogs = dialogs;
        this.recentFiles = recentFiles;
        Clips.CollectionChanged += OnClipsChanged;
        BeatGrid.PropertyChanged += OnBeatGridChanged;
        playback.Ended += OnPlaybackEnded;

        // Input priority: moving the playhead must never hold up mouse and keyboard handling.
        positionTimer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        positionTimer.Tick += (_, _) => UpdatePlaybackPosition();
        resumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        resumeTimer.Tick += (_, _) => Resume();
        fitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        fitTimer.Tick += (_, _) =>
        {
            fitTimer.Stop();
            if (autoFit)
                FitTimeline();
        };

        NewProjectCommand = new RelayCommand(NewProject);
        OpenProjectCommand = new RelayCommand(PickProject);
        SaveProjectCommand = new RelayCommand(() => SaveProject());
        SaveProjectAsCommand = new RelayCommand(() => SaveProjectAs());
        AddFilesCommand = new RelayCommand(PickFiles);
        RemoveClipCommand = new RelayCommand(RemoveSelected, () => selectedClip is not null);
        MoveLeftCommand = new RelayCommand(() => MoveSelected(-1), () => CanMoveSelected(-1));
        MoveRightCommand = new RelayCommand(() => MoveSelected(1), () => CanMoveSelected(1));
        PlayPauseCommand = new RelayCommand(TogglePlay, () => mode == PlaybackMode.Timeline || CanPlay);
        StopCommand = new RelayCommand(Stop, () => mode != PlaybackMode.None);
        GoToStartCommand = new RelayCommand(GoToStart, () => HasClips);
        PreviewCommand = new RelayCommand(TogglePreview, () => selectedClip?.IsLoaded == true);
        SetTrimStartCommand = new RelayCommand(SetTrimStartAtCursor, () => selectedClip?.IsLoaded == true);
        SetTrimEndCommand = new RelayCommand(SetTrimEndAtCursor, () => selectedClip?.IsLoaded == true);
        ResetTrimCommand = new RelayCommand(() => selectedClip?.ResetTrim(), () => selectedClip?.IsTrimmed == true);
        ZoomInCommand = new RelayCommand(() => ZoomTimeline(1.5), () => HasClips);
        ZoomOutCommand = new RelayCommand(() => ZoomTimeline(1 / 1.5), () => HasClips);
        ZoomFitCommand = new RelayCommand(FitTimelineToWindow, () => HasClips);
        AddTempoChangeCommand = new RelayCommand(AddTempoChange);
        ExportCommand = new RelayCommand(() => _ = ExportAsync(), () => !isExporting && loadingCount == 0 && CanPlay);
        CancelExportCommand = new RelayCommand(() => exportCancellation?.Cancel(), () => isExporting);
        ShowExportedFileCommand = new RelayCommand(ShowExportedFile, () => lastExportPath is not null);
        RefreshRecentProjects();
    }

    public ObservableCollection<ClipViewModel> Clips { get; } = [];

    public BeatGridViewModel BeatGrid { get; } = new();

    /// <summary>File › Open recent.</summary>
    public ObservableCollection<RecentProjectViewModel> RecentProjects { get; } = [];

    public bool HasRecentProjects => RecentProjects.Count > 0;

    /// <summary>"Song – Audio Editor", with a * in front while there are unsaved changes.</summary>
    public string WindowTitle => projectPath is null && !isDirty
        ? AppName
        : $"{(isDirty ? "*" : string.Empty)}{ProjectName} – {AppName}";

    public string ProjectName => projectPath is null ? "Untitled" : Path.GetFileNameWithoutExtension(projectPath);

    public ICommand NewProjectCommand { get; }
    public ICommand OpenProjectCommand { get; }
    public ICommand SaveProjectCommand { get; }
    public ICommand SaveProjectAsCommand { get; }
    public ICommand AddFilesCommand { get; }
    public ICommand RemoveClipCommand { get; }
    public ICommand MoveLeftCommand { get; }
    public ICommand MoveRightCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand GoToStartCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand SetTrimStartCommand { get; }
    public ICommand SetTrimEndCommand { get; }
    public ICommand ResetTrimCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomFitCommand { get; }
    public ICommand AddTempoChangeCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand CancelExportCommand { get; }
    public ICommand ShowExportedFileCommand { get; }

    public ClipViewModel? SelectedClip
    {
        get => selectedClip;
        set
        {
            if (!SetProperty(ref selectedClip, value))
                return;
            if (mode == PlaybackMode.Preview)
                StopPlayback();
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasNoSelection));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelection => selectedClip is not null;

    public bool HasNoSelection => selectedClip is null;

    public bool HasClips => Clips.Count > 0;

    public bool IsEmpty => Clips.Count == 0;

    public bool IsLoading => loadingCount > 0;

    public double TotalDuration => Clips.Sum(c => c.Length);

    public double PlayheadTime => playheadTime;

    public string PositionText => $"{TimeFormat.Format(playheadTime)} / {TimeFormat.Format(TotalDuration)}";

    public string ClipCountText => Clips.Count switch
    {
        0 => "No clips",
        1 => "1 clip",
        int n => $"{n} clips",
    };

    public PlaybackMode Mode
    {
        get => mode;
        private set
        {
            if (!SetProperty(ref mode, value))
                return;
            OnPropertyChanged(nameof(IsTimelinePlaying));
            OnPropertyChanged(nameof(IsPreviewing));
            OnPropertyChanged(nameof(PlayGlyph));
            OnPropertyChanged(nameof(PlayText));
            OnPropertyChanged(nameof(PreviewGlyph));
            OnPropertyChanged(nameof(PreviewText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsTimelinePlaying => mode == PlaybackMode.Timeline;

    public bool IsPreviewing => mode == PlaybackMode.Preview;

    public string PlayGlyph => IsTimelinePlaying ? "" : "";

    public string PlayText => IsTimelinePlaying ? "Pause" : "Play";

    public string PreviewGlyph => IsPreviewing ? "" : "";

    public string PreviewText => IsPreviewing ? "Pause preview" : "Preview from cursor";

    public double PixelsPerSecond => pixelsPerSecond;

    /// <summary>Width of the visible timeline, set by the view; used to fit all clips.</summary>
    public double TimelineViewportWidth
    {
        get => timelineViewportWidth;
        set
        {
            if (SetProperty(ref timelineViewportWidth, value) && autoFit)
                FitTimeline();
        }
    }

    public bool IsExporting
    {
        get => isExporting;
        private set
        {
            if (SetProperty(ref isExporting, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    public double ExportProgress
    {
        get => exportProgress;
        private set
        {
            if (SetProperty(ref exportProgress, value))
                OnPropertyChanged(nameof(ExportProgressText));
        }
    }

    public string ExportProgressText => $"{exportProgress * 100:0}%";

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool HasExportedFile => lastExportPath is not null;

    private bool CanPlay => Clips.Any(c => c.Length > 0);

    /// <summary>
    /// Opens files from the command line, a drop or the Add files dialog. A project replaces the
    /// timeline; audio files (and the audio files in any folders) are added to it.
    /// </summary>
    public Task OpenFilesAsync(IReadOnlyList<string> paths, int? insertAt = null) =>
        paths.FirstOrDefault(ProjectFile.IsProjectFile) is { } project
            ? OpenProjectAsync(project)
            : AddFilesAsync(paths, insertAt);

    /// <summary>
    /// Asks whether to save unsaved changes before the project is closed. Returns false when the
    /// user cancels, or when saving fails or its dialog is cancelled.
    /// </summary>
    public bool ConfirmCloseProject()
    {
        if (!isDirty)
            return true;
        return dialogs.AskToSaveChanges(ProjectName) switch
        {
            UnsavedChangesChoice.Save => SaveProject(),
            UnsavedChangesChoice.Discard => true,
            _ => false,
        };
    }

    /// <summary>
    /// Adds files (and the audio files in any folders) to the timeline, at
    /// <paramref name="insertAt"/> or at the end. Files are decoded in the background.
    /// </summary>
    private async Task AddFilesAsync(IEnumerable<string> paths, int? insertAt = null)
    {
        var files = ExpandPaths(paths);
        if (files.Count == 0)
        {
            StatusText = "No audio files found.";
            return;
        }

        int index = Math.Clamp(insertAt ?? Clips.Count, 0, Clips.Count);
        var added = new List<ClipViewModel>();
        foreach (string file in files)
        {
            var clip = new ClipViewModel(file) { PixelsPerSecond = pixelsPerSecond };
            Clips.Insert(index++, clip);
            added.Add(clip);
        }
        SelectedClip ??= added[0];

        int generation = projectGeneration;
        StatusText = added.Count == 1 ? $"Opening {added[0].FileName}…" : $"Opening {added.Count} files…";
        var errors = await LoadClipsAsync(added);
        if (generation != projectGeneration)
            return;

        int loaded = added.Count(c => c.IsLoaded);
        StatusText = loaded switch
        {
            0 => "No files were added.",
            1 => $"Added {added.First(c => c.IsLoaded).FileName}.",
            _ => $"Added {loaded} files.",
        };
        ShowLoadErrors(errors);
    }

    /// <summary>Decodes clips in the background. A clip whose file can't be read is taken off the timeline.</summary>
    /// <returns>A message for each clip that was taken off.</returns>
    private async Task<List<string>> LoadClipsAsync(List<ClipViewModel> clips)
    {
        loadingCount += clips.Count;
        OnPropertyChanged(nameof(IsLoading));

        var errors = new List<string>();
        await Task.WhenAll(clips.Select(clip => LoadAsync(clip, errors)));

        loadingCount -= clips.Count;
        OnPropertyChanged(nameof(IsLoading));
        CommandManager.InvalidateRequerySuggested();
        return errors;
    }

    private void ShowLoadErrors(List<string> errors)
    {
        if (errors.Count > 0)
        {
            dialogs.ShowError(
                errors.Count == 1 ? "A file could not be opened" : "Some files could not be opened",
                string.Join(Environment.NewLine + Environment.NewLine, errors));
        }
    }

    /// <summary>Moves a clip so that it ends up before the clip now at <paramref name="insertAt"/>.</summary>
    public void MoveClip(ClipViewModel clip, int insertAt)
    {
        int from = Clips.IndexOf(clip);
        if (from < 0)
            return;

        int to = Math.Clamp(insertAt > from ? insertAt - 1 : insertAt, 0, Clips.Count - 1);
        if (to != from)
            Clips.Move(from, to);
        SelectedClip = clip;
    }

    /// <summary>Selects a clip and puts the playhead at a point in it (0 = start, 1 = end).</summary>
    /// <param name="snap">Move the playhead to the nearest grid line or clip edge, if the grid is on.</param>
    public void SeekTimeline(ClipViewModel clip, double fraction, bool snap = false)
    {
        SelectedClip = clip;
        if (!clip.IsLoaded)
            return;

        double start = clip.TimelineStart;
        double end = start + clip.Length;
        double time = start + Math.Clamp(fraction, 0, 1) * clip.Length;
        if (snap && BeatGrid.IsEnabled)
        {
            double clicked = time;
            time = new[] { BeatGrid.Snap(clicked), start, end }.MinBy(t => Math.Abs(t - clicked));
        }
        SeekTo(time);
    }

    /// <summary>Zooms the timeline by mouse wheel notches (positive zooms in).</summary>
    public void ZoomTimelineBy(double notches) => ZoomTimeline(Math.Pow(1.25, notches));

    public void Dispose()
    {
        positionTimer.Stop();
        resumeTimer.Stop();
        fitTimer.Stop();
        exportCancellation?.Cancel();
        playback.Dispose();
    }

    private void PickFiles()
    {
        var files = dialogs.PickAudioFiles();
        if (files.Count > 0)
            _ = OpenFilesAsync(files);
    }

    private async Task LoadAsync(ClipViewModel clip, List<string> errors)
    {
        await loadGate.WaitAsync();
        try
        {
            var progress = new Progress<double>(p => clip.LoadProgress = p);
            var source = await Task.Run(() => AudioSource.Load(clip.FilePath, progress));
            if (Clips.Contains(clip))
                clip.SetSource(source);
        }
        catch (Exception ex)
        {
            // A clip that is gone already (another project was opened meanwhile) needs no message.
            if (Clips.Remove(clip))
                errors.Add($"{clip.FileName}: {FriendlyMessage(ex)}");
        }
        finally
        {
            loadGate.Release();
        }
    }

    private static List<string> ExpandPaths(IEnumerable<string> paths)
    {
        var comparer = StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
        var files = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.EnumerateFiles(path).Where(f => AudioExtensions.Contains(Path.GetExtension(f))));
            else if (File.Exists(path))
                files.Add(path);
        }
        // Selections and drops arrive in no useful order; "02 Verse" should follow "01 Intro".
        // Full paths, so a project saved later doesn't depend on the folder the app was started in.
        return files.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Path.GetFileName, comparer).ToList();
    }

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ClipViewModel clip in e.OldItems)
                clip.PropertyChanged -= OnClipPropertyChanged;
        }
        if (e.NewItems is not null)
        {
            foreach (ClipViewModel clip in e.NewItems)
                clip.PropertyChanged += OnClipPropertyChanged;
        }

        OnPropertyChanged(nameof(HasClips));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ClipCountText));
        OnArrangementChanged(fitNow: true);
        UpdateIsDirty();
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var clip = (ClipViewModel)sender!;
        string name = e.PropertyName ?? string.Empty;
        if (ArrangementProperties.Contains(name))
        {
            OnArrangementChanged(fitNow: name.Length == 0);
            UpdateIsDirty();
        }
        else if (name == nameof(ClipViewModel.EditorPosition) && !updatingPositions)
            OnEditorPositionChangedByUser(clip);
        else if (name == nameof(ClipViewModel.TimelineOrigin) && clip == previewClip && metronome is not null)
            metronome.TimelineStart = clip.TimelineOrigin + playbackOrigin; // Trimmed while previewing.
    }

    /// <param name="fitNow">
    /// Refit the timeline at once (clips added, removed or loaded). Trimming refits once it
    /// pauses instead, so every block does not resize on every mouse move.
    /// </param>
    private void OnArrangementChanged(bool fitNow)
    {
        if (mode == PlaybackMode.Timeline)
            PauseTimeline();

        double start = 0;
        foreach (var clip in Clips)
        {
            clip.TimelineStart = start;
            start += clip.Length;
        }

        if (autoFit)
        {
            fitTimer.Stop();
            if (fitNow)
                FitTimeline();
            else
                fitTimer.Start();
        }

        // Keep the editor cursor where it is and move the playhead along with it.
        if (selectedClip is { } selected && selected.Contains(selected.EditorPosition) && mode != PlaybackMode.Preview)
            SetPlayhead(selected.TimelineOrigin + selected.EditorPosition, syncEditor: false);
        else
            SetPlayhead(Math.Min(playheadTime, TotalDuration), syncEditor: false);

        CommandManager.InvalidateRequerySuggested();
    }

    private void OnEditorPositionChangedByUser(ClipViewModel clip)
    {
        if (clip != selectedClip)
            return;

        // The cursor was moved while playing: continue from there once it stops moving.
        var resume = mode != PlaybackMode.None ? mode : resumeMode;
        if (resume != PlaybackMode.None)
        {
            StopPlayback();
            resumeMode = resume;
            resumeTimer.Start();
        }

        if (clip.Contains(clip.EditorPosition))
            SetPlayhead(clip.TimelineOrigin + clip.EditorPosition, syncEditor: false);
    }

    private void Resume()
    {
        resumeTimer.Stop();
        var resume = resumeMode;
        resumeMode = PlaybackMode.None;
        if (resume == PlaybackMode.Preview)
            StartPreview();
        else if (resume == PlaybackMode.Timeline && selectedClip is { } clip && clip.Contains(clip.EditorPosition))
            PlayTimeline();
    }

    private void SetPlayhead(double time, bool syncEditor)
    {
        playheadTime = Math.Clamp(time, 0, TotalDuration);
        OnPropertyChanged(nameof(PlayheadTime));
        OnPropertyChanged(nameof(PositionText));
        BeatGrid.SetPlayhead(playheadTime);

        updatingPositions = true;
        try
        {
            double start = 0;
            var last = Clips.LastOrDefault(c => c.Length > 0);
            foreach (var clip in Clips)
            {
                double length = clip.Length;
                bool inside = length > 0 && playheadTime >= start &&
                    (playheadTime < start + length || (clip == last && playheadTime <= start + length));
                double position = inside ? clip.TrimStart + (playheadTime - start) : double.NaN;
                clip.TimelinePlayhead = position;
                if (inside && syncEditor && clip == selectedClip)
                    clip.EditorPosition = position;
                start += length;
            }
        }
        finally
        {
            updatingPositions = false;
        }
    }

    private void TogglePlay()
    {
        if (mode == PlaybackMode.Timeline)
            PauseTimeline();
        else
            PlayTimeline();
    }

    private void PlayTimeline()
    {
        var clips = Clips.Where(c => c.Length > 0).Select(c => c.ToSpec()).ToList();
        if (clips.Count == 0)
            return;

        StopPlayback();
        if (playheadTime >= TotalDuration - 0.005)
            SetPlayhead(0, syncEditor: true);

        var format = OutputFormat.Suggest(Clips.Where(c => c.Length > 0).Select(c => c.Source!));
        if (!StartPlayback(new TimelineSampleProvider(clips, format, TimeSpan.FromSeconds(playheadTime)), playheadTime))
            return;

        playbackOrigin = playheadTime;
        Mode = PlaybackMode.Timeline;
        StatusText = "Playing the timeline.";
    }

    private void PauseTimeline()
    {
        double position = CurrentTimelinePosition();
        StopPlayback();
        SetPlayhead(position, syncEditor: true);
        StatusText = "Paused.";
    }

    private void TogglePreview()
    {
        if (mode == PlaybackMode.Preview)
        {
            UpdatePlaybackPosition();
            StopPlayback();
            StatusText = "Preview paused. Press I or O to trim at the cursor.";
        }
        else
        {
            StartPreview();
        }
    }

    private void StartPreview()
    {
        if (selectedClip is not { Source: { } source } clip)
            return;

        StopPlayback();
        double from = clip.EditorPosition >= clip.Duration - 0.01 ? 0 : clip.EditorPosition;
        var whole = new ClipSpec(clip.FilePath, TimeSpan.Zero, source.Duration);
        var format = new OutputFormat(source.SampleRate, Math.Min(2, source.Channels));
        if (!StartPlayback(new TimelineSampleProvider([whole], format, TimeSpan.FromSeconds(from)), clip.TimelineOrigin + from))
            return;

        previewClip = clip;
        playbackOrigin = from;
        SetEditorPosition(clip, from);
        Mode = PlaybackMode.Preview;
        StatusText = $"Previewing {clip.FileName} (the whole file, ignoring the trim).";
    }

    private void Stop()
    {
        var stopped = mode;
        StopPlayback();
        if (stopped == PlaybackMode.Timeline)
            SetPlayhead(playbackOrigin, syncEditor: true);
        StatusText = "Stopped.";
    }

    private void GoToStart() => SeekTo(0);

    /// <summary>Moves the playhead; timeline playback carries on from there.</summary>
    private void SeekTo(double time)
    {
        var resume = mode;
        StopPlayback();
        SetPlayhead(time, syncEditor: true);
        if (resume == PlaybackMode.Timeline)
            PlayTimeline();
    }

    private void AddTempoChange()
    {
        int bar = BeatGrid.AddChange(playheadTime);
        SeekTo(BeatGrid.Map.BarStart(bar));
        StatusText = $"The tempo can now change at bar {bar}. Type the new tempo in the BPM box.";
    }

    private void OnBeatGridChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BeatGridViewModel.Map):
                UpdateIsDirty();
                break;
            case nameof(BeatGridViewModel.IsEnabled):
                StatusText = BeatGrid.IsEnabled
                    ? "Beat grid on: the cursor, trim handles and playhead snap to it. Hold Alt to place them freely."
                    : "Beat grid off.";
                break;
            case nameof(BeatGridViewModel.IsMetronomeOn):
                StatusText = BeatGrid.IsMetronomeOn
                    ? "Metronome on: playback clicks on every beat of the grid."
                    : "Metronome off.";
                break;
            case nameof(BeatGridViewModel.MetronomeMap) when metronome is not null:
                metronome.Map = BeatGrid.MetronomeMap;
                break;
            case nameof(BeatGridViewModel.MetronomeVolume) when metronome is not null:
                metronome.Volume = (float)BeatGrid.MetronomeVolume;
                break;
        }
    }

    /// <param name="timelineStart">
    /// Where on the timeline the audio starts, so the metronome clicks on the grid's beats.
    /// </param>
    private bool StartPlayback(TimelineSampleProvider provider, double timelineStart)
    {
        // The metronome is always in the chain, so it can be switched on while playing.
        var withClicks = new MetronomeSampleProvider(provider, timelineStart)
        {
            Map = BeatGrid.MetronomeMap,
            Volume = (float)BeatGrid.MetronomeVolume,
        };
        try
        {
            playback.Play(withClicks);
            metronome = withClicks;
            positionTimer.Start();
            return true;
        }
        catch (Exception ex)
        {
            withClicks.Dispose();
            dialogs.ShowError("Playback failed", FriendlyMessage(ex));
            return false;
        }
    }

    private void StopPlayback()
    {
        resumeTimer.Stop();
        resumeMode = PlaybackMode.None;
        playback.Stop();
        positionTimer.Stop();
        previewClip = null;
        metronome = null;
        Mode = PlaybackMode.None;
    }

    private void OnPlaybackEnded(object? sender, Exception? error)
    {
        var ended = mode;
        var clip = previewClip;
        positionTimer.Stop();
        previewClip = null;
        metronome = null;
        Mode = PlaybackMode.None;

        if (error is not null)
        {
            dialogs.ShowError("Playback stopped", FriendlyMessage(error));
            return;
        }

        if (ended == PlaybackMode.Timeline)
        {
            SetPlayhead(playbackOrigin, syncEditor: true);
            StatusText = "Reached the end of the timeline.";
        }
        else if (ended == PlaybackMode.Preview && clip is not null)
        {
            SetEditorPosition(clip, playbackOrigin);
            StatusText = "Reached the end of the file.";
        }
    }

    private void UpdatePlaybackPosition()
    {
        if (mode == PlaybackMode.Timeline)
            SetPlayhead(CurrentTimelinePosition(), syncEditor: true);
        else if (mode == PlaybackMode.Preview && previewClip is { } clip)
            SetEditorPosition(clip, playbackOrigin + playback.Position.TotalSeconds);
    }

    private double CurrentTimelinePosition() => mode == PlaybackMode.Timeline
        ? Math.Min(TotalDuration, playbackOrigin + playback.Position.TotalSeconds)
        : playheadTime;

    private void SetEditorPosition(ClipViewModel clip, double position)
    {
        updatingPositions = true;
        try
        {
            clip.EditorPosition = position;
        }
        finally
        {
            updatingPositions = false;
        }
    }

    private void SetTrimStartAtCursor()
    {
        if (selectedClip is { } clip)
            clip.TrimStart = clip.EditorPosition;
    }

    private void SetTrimEndAtCursor()
    {
        if (selectedClip is { } clip)
            clip.TrimEnd = clip.EditorPosition;
    }

    private void RemoveSelected()
    {
        if (selectedClip is not { } clip)
            return;

        if (mode != PlaybackMode.None)
            StopPlayback();
        int index = Clips.IndexOf(clip);
        Clips.Remove(clip);
        SelectedClip = Clips.Count == 0 ? null : Clips[Math.Min(index, Clips.Count - 1)];
        StatusText = $"Removed {clip.FileName}.";
    }

    private bool CanMoveSelected(int step)
    {
        if (selectedClip is null)
            return false;
        int target = Clips.IndexOf(selectedClip) + step;
        return target >= 0 && target < Clips.Count;
    }

    private void MoveSelected(int step)
    {
        if (!CanMoveSelected(step))
            return;
        int from = Clips.IndexOf(selectedClip!);
        Clips.Move(from, from + step);
    }

    private void ZoomTimeline(double factor)
    {
        autoFit = false;
        SetPixelsPerSecond(pixelsPerSecond * factor);
    }

    private void FitTimelineToWindow()
    {
        autoFit = true;
        FitTimeline();
    }

    private void FitTimeline()
    {
        double total = TotalDuration;
        if (total <= 0 || timelineViewportWidth <= 0)
            return;
        double available = timelineViewportWidth - Clips.Count * TimelineItemChrome - 4;
        SetPixelsPerSecond(Math.Max(MinPixelsPerSecond, available / total));
    }

    private void SetPixelsPerSecond(double value)
    {
        value = Math.Clamp(value, MinPixelsPerSecond, MaxPixelsPerSecond);
        if (!SetProperty(ref pixelsPerSecond, value, nameof(PixelsPerSecond)))
            return;
        foreach (var clip in Clips)
            clip.PixelsPerSecond = value;
    }

    private async Task ExportAsync()
    {
        var clips = Clips.Where(c => c.Length > 0).ToList();
        if (clips.Count == 0)
            return;

        var input = new ExportDialogInput(
            OutputFormat.Suggest(clips.Select(c => c.Source!)),
            lastExportOptions,
            TimeSpan.FromSeconds(TotalDuration),
            clips.Count,
            lastExportFolder ?? Path.GetDirectoryName(clips[0].FilePath),
            projectPath is not null ? ProjectName
                : clips.Count == 1 ? Path.GetFileNameWithoutExtension(clips[0].FileName) + " (edited)"
                : "Combined");
        var request = dialogs.AskForExport(input);
        if (request is null)
            return;

        lastExportOptions = request.Options;
        lastExportFolder = Path.GetDirectoryName(request.Path);
        lastExportPath = null;
        OnPropertyChanged(nameof(HasExportedFile));

        var specs = clips.Select(c => c.ToSpec()).ToList();
        string fileName = Path.GetFileName(request.Path);
        using var cancellation = new CancellationTokenSource();
        exportCancellation = cancellation;
        ExportProgress = 0;
        IsExporting = true;
        StatusText = $"Exporting {fileName}…";
        var progress = new Progress<double>(p =>
        {
            if (isExporting)
                ExportProgress = p;
        });

        try
        {
            await Task.Run(() => AudioExporter.Export(specs, request.Path, request.Options, progress, cancellation.Token));
            lastExportPath = request.Path;
            OnPropertyChanged(nameof(HasExportedFile));
            StatusText = $"Exported {TimeFormat.Format(input.Length.TotalSeconds)} to {request.Path}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Export cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = "Export failed.";
            dialogs.ShowError("Export failed", FriendlyMessage(ex));
        }
        finally
        {
            exportCancellation = null;
            IsExporting = false;
        }
    }

    private void ShowExportedFile()
    {
        if (lastExportPath is { } path && File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    // ----- Projects -----

    private void NewProject()
    {
        if (!ConfirmCloseProject())
            return;
        ShowProject(null, new Project(), []);
        StatusText = "New project.";
    }

    private void PickProject()
    {
        string? near = projectPath ?? recentFiles.Paths.FirstOrDefault();
        if (dialogs.PickProjectToOpen(near is null ? null : Path.GetDirectoryName(near)) is { } path)
            _ = OpenProjectAsync(path);
    }

    /// <summary>
    /// Opens a project in place of the open one, after asking whether to save that. The clips'
    /// files are decoded in the background, as when files are added.
    /// </summary>
    private async Task OpenProjectAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path))
        {
            ForgetRecentProject(path);
            dialogs.ShowError("The project could not be opened", $"{path} no longer exists.");
            return;
        }
        if (!ConfirmCloseProject())
            return;

        Project project;
        try
        {
            project = ProjectFile.Load(path);
        }
        catch (Exception ex)
        {
            dialogs.ShowError("The project could not be opened", FriendlyMessage(ex));
            return;
        }

        var added = ShowProject(path, project, FindMissingFiles(project.Clips, path));
        RememberRecentProject(path);

        int generation = projectGeneration;
        StatusText = $"Opening {ProjectName}…";
        var errors = await LoadClipsAsync(added);
        if (generation != projectGeneration)
            return;

        int leftOut = project.Clips.Count - added.Count(c => c.IsLoaded);
        StatusText = leftOut == 0
            ? $"Opened {ProjectName}."
            : $"Opened {ProjectName} without {(leftOut == 1 ? "1 clip" : $"{leftOut} clips")} whose file could not be found or read.";
        ShowLoadErrors(errors);
    }

    /// <summary>
    /// Lets the user find the files that are not where the project expects them. Once one is
    /// found, the other missing files are looked for in the same folder first.
    /// </summary>
    /// <returns>The clips whose file exists, with the paths that were found.</returns>
    private List<ProjectClip> FindMissingFiles(IReadOnlyList<ProjectClip> clips, string projectFile)
    {
        var missing = clips.Select(c => c.FilePath).Where(p => !File.Exists(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (missing.Count > 0 && dialogs.AskToFindMissingFiles(missing))
        {
            var folders = new List<string>();
            bool asking = true;
            foreach (string file in missing)
            {
                string name = Path.GetFileName(file);
                string? path = folders.Select(f => Path.Combine(f, name)).FirstOrDefault(File.Exists);
                if (path is null && asking)
                {
                    path = dialogs.FindMissingFile(file, folders.FirstOrDefault() ?? Path.GetDirectoryName(projectFile));
                    asking = path is not null; // Once the user gives up, stop asking about the rest.
                }
                if (path is null)
                    continue;

                found[file] = path;
                string folder = Path.GetDirectoryName(path)!;
                folders.Remove(folder);
                folders.Insert(0, folder);
            }
        }

        return clips
            .Where(c => found.ContainsKey(c.FilePath) || File.Exists(c.FilePath))
            .Select(c => found.TryGetValue(c.FilePath, out string? path) ? c with { FilePath = path } : c)
            .ToList();
    }

    /// <summary>Puts a project's clips and beat grid in place of the open project's.</summary>
    /// <param name="path">The project's file, or null for a new project.</param>
    /// <param name="clips">The clips to show: the project's, less any whose file is missing.</param>
    /// <returns>The new clips, which still have to be loaded.</returns>
    private List<ClipViewModel> ShowProject(string? path, Project project, IReadOnlyList<ProjectClip> clips)
    {
        projectGeneration++;
        projectPath = path;
        savedClips = project.Clips;
        savedMap = project.TempoMap;
        OnPropertyChanged(nameof(ProjectName));

        StopPlayback();
        SelectedClip = null;
        // Clear() doesn't tell OnClipsChanged which clips went.
        foreach (var clip in Clips)
            clip.PropertyChanged -= OnClipPropertyChanged;
        Clips.Clear();
        autoFit = true;

        BeatGrid.Map = project.TempoMap;
        BeatGrid.IsEnabled = project.IsGridEnabled;
        BeatGrid.Resolution = project.SnapTo;
        BeatGrid.IsMetronomeOn = project.IsMetronomeOn;
        BeatGrid.MetronomeVolume = project.MetronomeVolume;
        // A project without export settings (a new one) starts from the last ones used.
        lastExportOptions = project.Export ?? lastExportOptions;
        if (project.ExportFolder is { } folder && Directory.Exists(folder))
            lastExportFolder = folder;

        var added = clips.Select(c => new ClipViewModel(c.FilePath, c) { PixelsPerSecond = pixelsPerSecond }).ToList();
        foreach (var clip in added)
            Clips.Add(clip);
        SelectedClip = added.FirstOrDefault();

        UpdateIsDirty();
        OnPropertyChanged(nameof(WindowTitle));
        return added;
    }

    /// <summary>Saves to the project's file, or asks where when it has none yet.</summary>
    /// <returns>Whether the project was saved.</returns>
    private bool SaveProject() => projectPath is { } path ? SaveProjectTo(path) : SaveProjectAs();

    private bool SaveProjectAs()
    {
        // A new project is suggested next to its audio, so the folder holds everything.
        string? near = projectPath ?? Clips.FirstOrDefault()?.FilePath;
        string? path = dialogs.PickProjectSavePath(ProjectName, near is null ? null : Path.GetDirectoryName(near));
        return path is not null && SaveProjectTo(path);
    }

    private bool SaveProjectTo(string path)
    {
        var project = new Project
        {
            Clips = Clips.Select(c => c.ToProjectClip()).ToList(),
            TempoMap = BeatGrid.Map,
            IsGridEnabled = BeatGrid.IsEnabled,
            SnapTo = BeatGrid.Resolution,
            IsMetronomeOn = BeatGrid.IsMetronomeOn,
            MetronomeVolume = BeatGrid.MetronomeVolume,
            Export = lastExportOptions,
            ExportFolder = lastExportFolder,
        };
        try
        {
            ProjectFile.Save(project, path);
        }
        catch (Exception ex)
        {
            dialogs.ShowError("The project could not be saved", FriendlyMessage(ex));
            return false;
        }

        projectPath = Path.GetFullPath(path);
        savedClips = project.Clips;
        savedMap = project.TempoMap;
        OnPropertyChanged(nameof(ProjectName));
        OnPropertyChanged(nameof(WindowTitle));
        UpdateIsDirty();
        RememberRecentProject(projectPath);
        StatusText = $"Saved {ProjectName}.";
        return true;
    }

    /// <summary>
    /// Compares the clips and the tempo map with how they were opened or last saved, so undoing an
    /// edit by hand leaves nothing to save. The grid, snap, metronome and export settings are saved
    /// too, but changing them doesn't count as a change to the project.
    /// </summary>
    private void UpdateIsDirty()
    {
        var map = BeatGrid.Map;
        bool dirty = map.BeatsPerBar != savedMap.BeatsPerBar || map.Offset != savedMap.Offset
            || !map.Changes.SequenceEqual(savedMap.Changes)
            || !Clips.Select(c => c.ToProjectClip()).SequenceEqual(savedClips);
        if (dirty == isDirty)
            return;
        isDirty = dirty;
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void RememberRecentProject(string path)
    {
        recentFiles.Add(path);
        RefreshRecentProjects();
    }

    private void ForgetRecentProject(string path)
    {
        recentFiles.Remove(path);
        RefreshRecentProjects();
    }

    private void RefreshRecentProjects()
    {
        RecentProjects.Clear();
        foreach (string path in recentFiles.Paths)
            RecentProjects.Add(new RecentProjectViewModel(path, new RelayCommand(() => _ = OpenProjectAsync(path))));
        OnPropertyChanged(nameof(HasRecentProjects));
    }

    private static string FriendlyMessage(Exception ex) => ex switch
    {
        COMException => "The file type is not supported, or the file is damaged.",
        FileNotFoundException => "The file no longer exists.",
        UnauthorizedAccessException => "Access was denied.",
        _ => ex.Message,
    };
}

/// <summary>A project in File › Open recent.</summary>
public sealed class RecentProjectViewModel(string path, ICommand openCommand)
{
    public string Path { get; } = path;

    public string Name { get; } = System.IO.Path.GetFileNameWithoutExtension(path);

    /// <summary>The end of the project's folder, such as "…\Music\Song", to tell projects with the same name apart.</summary>
    public string Folder { get; } = ShortFolder(path);

    public ICommand OpenCommand { get; } = openCommand;

    private static string ShortFolder(string path)
    {
        string folder = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
        string[] parts = folder.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? folder : "…" + System.IO.Path.DirectorySeparatorChar + string.Join(System.IO.Path.DirectorySeparatorChar, parts[^2..]);
    }
}
