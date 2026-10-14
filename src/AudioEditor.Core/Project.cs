namespace AudioEditor.Core;

/// <summary>What dragging snaps to while the beat grid is on.</summary>
public enum GridResolution
{
    Bar,
    Beat,
    HalfBeat,
    QuarterBeat,
}

/// <summary>
/// One clip in a project. Times are seconds into the source file, as the editor keeps them, so a
/// time that was snapped to the grid is still exactly on it after saving and opening.
/// </summary>
/// <param name="FilePath">The full path of the source file.</param>
public sealed record ProjectClip(string FilePath)
{
    public double TrimStart { get; init; }

    /// <summary>Where the clip ends in the source file, or null for the end of the file.</summary>
    public double? TrimEnd { get; init; }

    /// <summary>Volume change in decibels.</summary>
    public double GainDb { get; init; }

    public double FadeIn { get; init; }

    public double FadeOut { get; init; }
}

/// <summary>
/// Everything a project file holds: the clips in timeline order, the beat grid, and the export
/// settings to start from.
/// </summary>
public sealed record Project
{
    public IReadOnlyList<ProjectClip> Clips { get; init; } = [];

    public TempoMap TempoMap { get; init; } = new();

    public bool IsGridEnabled { get; init; }

    public GridResolution SnapTo { get; init; } = GridResolution.Beat;

    public bool IsMetronomeOn { get; init; }

    /// <summary>Level of the metronome click, from 0 to 1.</summary>
    public double MetronomeVolume { get; init; } = 0.5;

    /// <summary>The settings of the last export, or null if there are none.</summary>
    public ExportOptions? Export { get; init; }

    /// <summary>The folder of the last export, or null if there is none.</summary>
    public string? ExportFolder { get; init; }
}
