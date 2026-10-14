namespace AudioEditor.Core;

/// <summary>
/// One clip on the timeline: a source file and the part of it that ends up in the result.
/// </summary>
/// <param name="FilePath">The audio file the clip reads from.</param>
/// <param name="TrimStart">Where in the source file the clip starts.</param>
/// <param name="TrimEnd">Where in the source file the clip ends (exclusive).</param>
public sealed record ClipSpec(string FilePath, TimeSpan TrimStart, TimeSpan TrimEnd)
{
    /// <summary>Volume change in decibels. 0 leaves the audio unchanged.</summary>
    public double GainDb { get; init; }

    /// <summary>Length of the linear fade in at the start of the clip.</summary>
    public TimeSpan FadeIn { get; init; }

    /// <summary>Length of the linear fade out at the end of the clip.</summary>
    public TimeSpan FadeOut { get; init; }

    /// <summary>How long the clip lasts on the timeline.</summary>
    public TimeSpan Length => TrimEnd > TrimStart ? TrimEnd - TrimStart : TimeSpan.Zero;
}
