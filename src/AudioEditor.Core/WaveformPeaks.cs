namespace AudioEditor.Core;

/// <summary>
/// A compact overview of a sound file for drawing: the lowest and highest sample value
/// (across all channels) in each short, fixed-length bucket of the file.
/// </summary>
public sealed class WaveformPeaks
{
    private readonly float[] min;
    private readonly float[] max;

    public WaveformPeaks(float[] min, float[] max, double bucketsPerSecond)
    {
        ArgumentNullException.ThrowIfNull(min);
        ArgumentNullException.ThrowIfNull(max);
        if (min.Length != max.Length)
            throw new ArgumentException("Min and max must have the same length.", nameof(max));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketsPerSecond);

        this.min = min;
        this.max = max;
        BucketsPerSecond = bucketsPerSecond;
    }

    public int Count => min.Length;

    public double BucketsPerSecond { get; }

    /// <summary>
    /// The lowest and highest sample value between <paramref name="startSeconds"/> and
    /// <paramref name="endSeconds"/>. Returns (0, 0) outside the file.
    /// </summary>
    public (float Min, float Max) GetRange(double startSeconds, double endSeconds)
    {
        if (Count == 0 || double.IsNaN(startSeconds) || double.IsNaN(endSeconds))
            return (0, 0);

        long first = (long)Math.Floor(startSeconds * BucketsPerSecond);
        long last = (long)Math.Ceiling(endSeconds * BucketsPerSecond);
        if (last <= first)
            last = first + 1;
        if (last <= 0 || first >= Count)
            return (0, 0);

        int from = (int)Math.Max(0, first);
        int to = (int)Math.Min(Count, last);

        float lo = float.MaxValue;
        float hi = float.MinValue;
        for (int i = from; i < to; i++)
        {
            if (min[i] < lo) lo = min[i];
            if (max[i] > hi) hi = max[i];
        }
        return (lo, hi);
    }
}
