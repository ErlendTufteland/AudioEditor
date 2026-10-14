namespace AudioEditor.Core;

/// <summary>A tempo that applies from the start of a bar until the next change.</summary>
/// <param name="Bar">The bar the tempo starts at; 1 is the first bar.</param>
/// <param name="Bpm">Beats per minute.</param>
public sealed record TempoChange(int Bar, double Bpm);

/// <summary>
/// The beat grid of the timeline: bars of <see cref="BeatsPerBar"/> beats, the first one starting
/// at <see cref="Offset"/>, with tempo changes at the start of bars. Times are seconds on the
/// timeline. Beats are counted from the start of bar 1, so beat 0 is at the offset; before it,
/// the first tempo carries on backwards.
/// </summary>
/// <remarks>Immutable; the <c>With…</c> methods return a changed copy.</remarks>
public sealed class TempoMap
{
    public const double DefaultBpm = 120;
    public const double MinBpm = 10;
    public const double MaxBpm = 999;
    public const int MaxBeatsPerBar = 32;

    private readonly TempoChange[] changes;
    private readonly double[] startBeats;
    private readonly double[] startTimes;
    private readonly double[] secondsPerBeat;

    public TempoMap(double bpm = DefaultBpm, int beatsPerBar = 4, double offset = 0)
        : this([new TempoChange(1, bpm)], beatsPerBar, offset)
    {
    }

    /// <param name="changes">
    /// Tempo changes in any order. When two are at the same bar the last one wins, and the
    /// earliest one is moved to bar 1. Tempos are clamped to <see cref="MinBpm"/>–<see cref="MaxBpm"/>.
    /// </param>
    /// <param name="beatsPerBar">Beats in every bar.</param>
    /// <param name="offset">Where on the timeline bar 1 starts, in seconds.</param>
    public TempoMap(IEnumerable<TempoChange> changes, int beatsPerBar = 4, double offset = 0)
    {
        ArgumentNullException.ThrowIfNull(changes);
        BeatsPerBar = Math.Clamp(beatsPerBar, 1, MaxBeatsPerBar);
        Offset = double.IsFinite(offset) ? offset : 0;

        var byBar = new SortedDictionary<int, double>();
        foreach (var change in changes)
            byBar[Math.Max(1, change.Bar)] = ClampBpm(change.Bpm);
        if (byBar.Count == 0)
            byBar[1] = DefaultBpm;

        this.changes = byBar.Select(pair => new TempoChange(pair.Key, pair.Value)).ToArray();
        this.changes[0] = this.changes[0] with { Bar = 1 };

        int count = this.changes.Length;
        startBeats = new double[count];
        startTimes = new double[count];
        secondsPerBeat = new double[count];
        for (int i = 0; i < count; i++)
        {
            startBeats[i] = (this.changes[i].Bar - 1) * (double)BeatsPerBar;
            secondsPerBeat[i] = 60 / this.changes[i].Bpm;
            startTimes[i] = i == 0
                ? Offset
                : startTimes[i - 1] + (startBeats[i] - startBeats[i - 1]) * secondsPerBeat[i - 1];
        }
        HighestBpm = this.changes.Max(c => c.Bpm);
    }

    /// <summary>The tempo changes in order. The first one is always at bar 1.</summary>
    public IReadOnlyList<TempoChange> Changes => changes;

    public int BeatsPerBar { get; }

    /// <summary>Where on the timeline bar 1 starts, in seconds.</summary>
    public double Offset { get; }

    /// <summary>The fastest tempo in the map, where the grid is densest.</summary>
    public double HighestBpm { get; }

    /// <summary>Where on the timeline a beat falls. Beat 0 is the start of bar 1.</summary>
    public double TimeAt(double beat)
    {
        int i = Segment(startBeats, beat);
        return startTimes[i] + (beat - startBeats[i]) * secondsPerBeat[i];
    }

    /// <summary>The beat at a time on the timeline, with a fraction.</summary>
    public double BeatAt(double time)
    {
        int i = Segment(startTimes, time);
        return startBeats[i] + (time - startTimes[i]) / secondsPerBeat[i];
    }

    /// <summary>Where on the timeline a bar starts.</summary>
    public double BarStart(int bar) => TimeAt((bar - 1) * (double)BeatsPerBar);

    /// <summary>The index in <see cref="Changes"/> of the tempo that applies at a time.</summary>
    public int IndexAt(double time) => Segment(startTimes, time);

    public double BpmAt(double time) => changes[IndexAt(time)].Bpm;

    /// <summary>The bar and the beat in it at a time, both counted from 1.</summary>
    public (int Bar, int Beat) Locate(double time)
    {
        // A little tolerance, so a time computed from a beat counts as being on that beat.
        double beat = Math.Floor(BeatAt(time) + 1e-6);
        double bar = Math.Floor(beat / BeatsPerBar);
        return ((int)bar + 1, (int)(beat - bar * BeatsPerBar) + 1);
    }

    /// <summary>
    /// The grid line nearest to <paramref name="time"/>, with lines every <paramref name="step"/>
    /// beats counted from the start of bar 1. Use a step that divides a bar, so tempo changes
    /// fall on a line.
    /// </summary>
    public double Snap(double time, double step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        double lower = Math.Floor(BeatAt(time) / step) * step;
        double before = TimeAt(lower);
        double after = TimeAt(lower + step);
        return time - before <= after - time ? before : after;
    }

    /// <summary>The grid lines from <paramref name="from"/> to <paramref name="to"/>, every <paramref name="step"/> beats.</summary>
    public IEnumerable<(double Beat, double Time)> Lines(double from, double to, double step)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(step);
        double first = Math.Ceiling(BeatAt(from) / step - 1e-9) * step;
        for (long i = 0; ; i++)
        {
            double beat = first + i * step;
            double time = TimeAt(beat);
            if (time > to)
                yield break;
            yield return (beat, time);
        }
    }

    /// <summary>The tempo change at the start of <paramref name="bar"/>, if there is one.</summary>
    public TempoChange? ChangeAt(int bar) => Array.Find(changes, c => c.Bar == bar);

    /// <summary>Sets the tempo from the start of <paramref name="bar"/>, adding a change there if there is none.</summary>
    public TempoMap WithTempo(int bar, double bpm) =>
        new(changes.Append(new TempoChange(bar, bpm)), BeatsPerBar, Offset);

    /// <summary>Removes the change at <paramref name="bar"/>. The first tempo cannot be removed.</summary>
    public TempoMap WithoutChange(int bar) =>
        bar <= 1 ? this : new(changes.Where(c => c.Bar != bar), BeatsPerBar, Offset);

    /// <summary>
    /// Moves the change at <paramref name="bar"/> to <paramref name="newBar"/>, but not onto or past
    /// its neighbours. The first tempo stays at bar 1.
    /// </summary>
    public TempoMap WithChangeMoved(int bar, int newBar)
    {
        int index = Array.FindIndex(changes, c => c.Bar == bar);
        if (index <= 0)
            return this;

        int min = changes[index - 1].Bar + 1;
        int max = index + 1 < changes.Length ? changes[index + 1].Bar - 1 : int.MaxValue;
        var moved = (TempoChange[])changes.Clone();
        moved[index] = moved[index] with { Bar = Math.Clamp(newBar, min, max) };
        return new(moved, BeatsPerBar, Offset);
    }

    public TempoMap WithBeatsPerBar(int beatsPerBar) => new(changes, beatsPerBar, Offset);

    public TempoMap WithOffset(double offset) => new(changes, BeatsPerBar, offset);

    private static double ClampBpm(double bpm) =>
        double.IsFinite(bpm) ? Math.Clamp(bpm, MinBpm, MaxBpm) : DefaultBpm;

    /// <summary>The last segment that starts at or before <paramref name="value"/>, or the first one.</summary>
    private static int Segment(double[] starts, double value)
    {
        int index = Array.BinarySearch(starts, value);
        if (index < 0)
            index = ~index - 1;
        return Math.Max(0, index);
    }
}
