using System.Collections.ObjectModel;
using System.Windows.Input;
using AudioEditor.Core;

namespace AudioEditor.App.ViewModels;

public sealed record GridResolutionOption(GridResolution Value, string Label);

/// <summary>
/// The beat grid: the tempo map, what dragging snaps to, the tempo at the playhead and the metronome.
/// The grid is in timeline time; a clip maps its own times onto it with
/// <see cref="ClipViewModel.TimelineOrigin"/>.
/// </summary>
public sealed class BeatGridViewModel : ObservableObject
{
    private TempoMap map = new();
    private bool isEnabled;
    private bool isMetronomeOn;
    private double metronomeVolume = 0.5;
    private GridResolution resolution = GridResolution.Beat;
    private double playheadTime;
    private int playheadIndex;
    private string positionText = "Bar 1.1";

    public BeatGridViewModel()
    {
        ToggleCommand = new RelayCommand(() => IsEnabled = !IsEnabled);
        ToggleMetronomeCommand = new RelayCommand(ToggleMetronome);
        IncreaseBpmCommand = new RelayCommand(() => CurrentBpm += 1);
        DecreaseBpmCommand = new RelayCommand(() => CurrentBpm -= 1);
        SyncChanges();
    }

    public IReadOnlyList<GridResolutionOption> Resolutions { get; } =
    [
        new(GridResolution.Bar, "Bar"),
        new(GridResolution.Beat, "Beat"),
        new(GridResolution.HalfBeat, "½ beat"),
        new(GridResolution.QuarterBeat, "¼ beat"),
    ];

    /// <summary>One row per tempo change, for editing them in a list.</summary>
    public ObservableCollection<TempoChangeViewModel> Changes { get; } = [];

    public ICommand ToggleCommand { get; }

    /// <summary>Turns the metronome on or off; turning it on also shows the grid it follows.</summary>
    public ICommand ToggleMetronomeCommand { get; }

    /// <summary>Raises <see cref="CurrentBpm"/> by one; the map keeps it within its limits.</summary>
    public ICommand IncreaseBpmCommand { get; }

    /// <summary>Lowers <see cref="CurrentBpm"/> by one; the map keeps it within its limits.</summary>
    public ICommand DecreaseBpmCommand { get; }

    /// <summary>The whole tempo map. Every edit replaces it; opening a project sets it.</summary>
    public TempoMap Map
    {
        get => map;
        set
        {
            if (!SetProperty(ref map, value))
                return;
            playheadIndex = map.IndexAt(playheadTime);
            OnPropertyChanged(nameof(ActiveMap));
            OnPropertyChanged(nameof(MetronomeMap));
            OnPropertyChanged(nameof(SnapStep));
            OnPropertyChanged(nameof(BeatsPerBar));
            OnPropertyChanged(nameof(Offset));
            OnPropertyChanged(nameof(CurrentBpm));
            OnPropertyChanged(nameof(CurrentSectionText));
            UpdatePositionText();
            SyncChanges();
        }
    }

    /// <summary>The map while the grid is on, otherwise null. Waveforms draw and snap to this.</summary>
    public TempoMap? ActiveMap => isEnabled ? map : null;

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (!SetProperty(ref isEnabled, value))
                return;
            OnPropertyChanged(nameof(ActiveMap));
            OnPropertyChanged(nameof(MetronomeMap));
        }
    }

    /// <summary>Whether playback clicks on every beat. It is only heard while the grid is on.</summary>
    public bool IsMetronomeOn
    {
        get => isMetronomeOn;
        set
        {
            if (SetProperty(ref isMetronomeOn, value))
                OnPropertyChanged(nameof(MetronomeMap));
        }
    }

    /// <summary>The beats the metronome clicks on, or null when it is silent.</summary>
    public TempoMap? MetronomeMap => isEnabled && isMetronomeOn ? map : null;

    /// <summary>Level of the click, from 0 to 1.</summary>
    public double MetronomeVolume
    {
        get => metronomeVolume;
        set
        {
            if (double.IsFinite(value) && SetProperty(ref metronomeVolume, Math.Clamp(value, 0, 1)))
                OnPropertyChanged(nameof(MetronomeVolumeText));
        }
    }

    public string MetronomeVolumeText => $"{metronomeVolume * 100:0}%";

    public GridResolution Resolution
    {
        get => resolution;
        set
        {
            if (SetProperty(ref resolution, value))
                OnPropertyChanged(nameof(SnapStep));
        }
    }

    /// <summary>Distance between the lines that dragging snaps to, in beats.</summary>
    public double SnapStep => resolution switch
    {
        GridResolution.Bar => map.BeatsPerBar,
        GridResolution.Beat => 1,
        GridResolution.HalfBeat => 0.5,
        _ => 0.25,
    };

    public int BeatsPerBar
    {
        get => map.BeatsPerBar;
        set => Map = map.WithBeatsPerBar(value);
    }

    /// <summary>Where on the timeline bar 1 starts, in seconds.</summary>
    public double Offset
    {
        get => map.Offset;
        set
        {
            if (double.IsFinite(value))
                Map = map.WithOffset(value);
        }
    }

    /// <summary>
    /// The tempo at the playhead. Setting it changes the tempo of that whole section, from the
    /// last tempo change before the playhead up to the next one.
    /// </summary>
    public double CurrentBpm
    {
        get => map.Changes[playheadIndex].Bpm;
        set
        {
            if (double.IsFinite(value))
                Map = map.WithTempo(map.Changes[playheadIndex].Bar, value);
        }
    }

    /// <summary>Where the section with <see cref="CurrentBpm"/> starts; empty for the first one.</summary>
    public string CurrentSectionText => playheadIndex == 0 ? string.Empty : $"from bar {map.Changes[playheadIndex].Bar}";

    /// <summary>The playhead as bar and beat, for example "Bar 17.3".</summary>
    public string PositionText => positionText;

    public void SetPlayhead(double time)
    {
        playheadTime = time;
        int index = map.IndexAt(time);
        if (index != playheadIndex)
        {
            playheadIndex = index;
            OnPropertyChanged(nameof(CurrentBpm));
            OnPropertyChanged(nameof(CurrentSectionText));
        }
        UpdatePositionText();
    }

    /// <summary>The grid line nearest to a time on the timeline.</summary>
    public double Snap(double time) => map.Snap(time, SnapStep);

    /// <summary>
    /// Adds a tempo change at the bar nearest to <paramref name="time"/> (after bar 1), keeping
    /// the tempo that was there, or finds the change that is already there. Returns its bar.
    /// </summary>
    public int AddChange(double time)
    {
        int bar = Math.Max(2, (int)Math.Round(map.BeatAt(time) / map.BeatsPerBar) + 1);
        if (map.ChangeAt(bar) is null)
            Map = map.WithTempo(bar, map.BpmAt(map.BarStart(bar)));
        return bar;
    }

    internal void SetTempo(int index, double bpm)
    {
        if (index < map.Changes.Count && double.IsFinite(bpm))
            Map = map.WithTempo(map.Changes[index].Bar, bpm);
    }

    internal void MoveChange(int index, int bar)
    {
        if (index < map.Changes.Count)
            Map = map.WithChangeMoved(map.Changes[index].Bar, bar);
    }

    internal void RemoveChange(int index)
    {
        if (index < map.Changes.Count)
            Map = map.WithoutChange(map.Changes[index].Bar);
    }

    private void ToggleMetronome()
    {
        IsMetronomeOn = !IsMetronomeOn;
        if (IsMetronomeOn)
            IsEnabled = true;
    }

    private void UpdatePositionText()
    {
        var (bar, beat) = map.Locate(playheadTime);
        SetProperty(ref positionText, $"Bar {bar}.{beat}", nameof(PositionText));
    }

    /// <summary>
    /// Updates the rows in place rather than replacing them, so a text box that is being
    /// committed keeps its row.
    /// </summary>
    private void SyncChanges()
    {
        var changes = map.Changes;
        for (int i = 0; i < changes.Count; i++)
        {
            if (i < Changes.Count)
                Changes[i].Update(changes[i]);
            else
                Changes.Add(new TempoChangeViewModel(this, i, changes[i]));
        }
        while (Changes.Count > changes.Count)
            Changes.RemoveAt(Changes.Count - 1);
    }
}

/// <summary>One row in the list of tempo changes.</summary>
public sealed class TempoChangeViewModel : ObservableObject
{
    private readonly BeatGridViewModel grid;
    private readonly int index;
    private TempoChange change;

    public TempoChangeViewModel(BeatGridViewModel grid, int index, TempoChange change)
    {
        this.grid = grid;
        this.index = index;
        this.change = change;
        RemoveCommand = new RelayCommand(() => grid.RemoveChange(index), () => IsMovable);
    }

    public int Bar
    {
        get => change.Bar;
        set => grid.MoveChange(index, value);
    }

    public double Bpm
    {
        get => change.Bpm;
        set => grid.SetTempo(index, value);
    }

    /// <summary>The first tempo always starts at bar 1; it cannot be moved or removed.</summary>
    public bool IsMovable => index > 0;

    public ICommand RemoveCommand { get; }

    internal void Update(TempoChange value)
    {
        var previous = change;
        change = value;
        if (previous.Bar != value.Bar)
            OnPropertyChanged(nameof(Bar));
        if (previous.Bpm != value.Bpm)
            OnPropertyChanged(nameof(Bpm));
    }
}
