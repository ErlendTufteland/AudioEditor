namespace AudioEditor.Core.Tests;

public class TempoMapTests
{
    /// <summary>4/4, 120 BPM for bars 1–2 (4 s), then 60 BPM from bar 3.</summary>
    private static readonly TempoMap Slowing = new([new TempoChange(1, 120), new TempoChange(3, 60)]);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0.5)]
    [InlineData(4, 2)]
    [InlineData(-2, -1)]
    public void Constant_tempo_maps_beats_to_seconds(double beat, double seconds)
    {
        var map = new TempoMap(120);

        Assert.Equal(seconds, map.TimeAt(beat), precision: 9);
        Assert.Equal(beat, map.BeatAt(seconds), precision: 9);
    }

    [Fact]
    public void Offset_moves_bar_one()
    {
        var map = new TempoMap(120, offset: 1.25);

        Assert.Equal(1.25, map.BarStart(1), precision: 9);
        Assert.Equal(3.25, map.BarStart(2), precision: 9);
        Assert.Equal(-0.75, map.BarStart(0), precision: 9);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(8, 4)]
    [InlineData(9, 5)]
    [InlineData(12, 8)]
    public void Tempo_changes_at_the_start_of_a_bar(double beat, double seconds)
    {
        Assert.Equal(seconds, Slowing.TimeAt(beat), precision: 9);
        Assert.Equal(beat, Slowing.BeatAt(seconds), precision: 9);
    }

    [Fact]
    public void Tempo_and_bar_at_a_time()
    {
        Assert.Equal(120, Slowing.BpmAt(3.99));
        Assert.Equal(60, Slowing.BpmAt(4));
        Assert.Equal(1, Slowing.IndexAt(100));
        Assert.Equal((1, 1), Slowing.Locate(0));
        Assert.Equal((2, 3), Slowing.Locate(3.1));
        Assert.Equal((3, 2), Slowing.Locate(5));
        Assert.Equal((0, 4), Slowing.Locate(-0.2));
    }

    [Theory]
    [InlineData(1.2, 1.0, 1.0)]
    [InlineData(1.3, 1.0, 1.5)]
    [InlineData(1.3, 0.25, 1.25)]
    [InlineData(2.9, 4.0, 2.0)]
    [InlineData(3.1, 4.0, 4.0)]
    [InlineData(4.4, 1.0, 4.0)]
    [InlineData(4.6, 1.0, 5.0)]
    [InlineData(-0.3, 1.0, -0.5)]
    public void Snap_finds_the_nearest_line(double time, double step, double expected) =>
        Assert.Equal(expected, Slowing.Snap(time, step), precision: 9);

    [Fact]
    public void Lines_follow_the_tempo()
    {
        var lines = Slowing.Lines(2.8, 6, 2).ToList();

        Assert.Equal([6.0, 8, 10], lines.Select(l => l.Beat));
        Assert.Equal([3.0, 4, 6], lines.Select(l => Math.Round(l.Time, 9)));
    }

    [Fact]
    public void Changes_are_sorted_and_cleaned_up()
    {
        var map = new TempoMap(
            [new TempoChange(5, 90), new TempoChange(3, 2000), new TempoChange(5, 100), new TempoChange(9, double.NaN)],
            beatsPerBar: 3);

        Assert.Equal(
            [new TempoChange(1, TempoMap.MaxBpm), new TempoChange(5, 100), new TempoChange(9, TempoMap.DefaultBpm)],
            map.Changes);
        Assert.Equal(TempoMap.MaxBpm, map.HighestBpm);
        Assert.Equal(3, map.BeatsPerBar);
        Assert.Single(new TempoMap([]).Changes);
    }

    [Fact]
    public void Edits_return_a_changed_copy()
    {
        var map = Slowing.WithTempo(5, 90).WithTempo(1, 128);

        Assert.Equal([1, 3, 5], map.Changes.Select(c => c.Bar));
        Assert.Equal(128, map.Changes[0].Bpm);
        Assert.Equal(120, Slowing.Changes[0].Bpm);

        Assert.Equal([1, 5], map.WithoutChange(3).Changes.Select(c => c.Bar));
        Assert.Same(map, map.WithoutChange(1));

        Assert.Equal([1, 4, 5], map.WithChangeMoved(3, 9).Changes.Select(c => c.Bar));
        Assert.Equal([1, 2, 5], map.WithChangeMoved(3, -1).Changes.Select(c => c.Bar));
        Assert.Same(map, map.WithChangeMoved(1, 2));

        // Bar 3 is 8 beats in at 128 BPM, or 6 beats in with 3 beats per bar.
        Assert.Equal(4.25, map.WithOffset(0.5).BarStart(3), precision: 9);
        Assert.Equal(2.8125, map.WithBeatsPerBar(3).BarStart(3), precision: 9);
    }
}
