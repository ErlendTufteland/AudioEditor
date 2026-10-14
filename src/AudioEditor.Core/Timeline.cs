namespace AudioEditor.Core;

/// <summary>Time calculations for clips placed one after another.</summary>
public static class Timeline
{
    public static TimeSpan TotalLength(IEnumerable<ClipSpec> clips) =>
        clips.Aggregate(TimeSpan.Zero, (total, clip) => total + clip.Length);

    /// <summary>
    /// Finds the clip that plays at <paramref name="time"/> and how far into it that is.
    /// Returns an index equal to the clip count when the time is at or after the end.
    /// </summary>
    public static (int Index, TimeSpan Offset) Locate(IReadOnlyList<ClipSpec> clips, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(clips);
        if (time <= TimeSpan.Zero)
            return (0, TimeSpan.Zero);

        var start = TimeSpan.Zero;
        for (int i = 0; i < clips.Count; i++)
        {
            var end = start + clips[i].Length;
            if (time < end)
                return (i, time - start);
            start = end;
        }
        return (clips.Count, TimeSpan.Zero);
    }
}
