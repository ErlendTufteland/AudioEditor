using System.Globalization;

namespace AudioEditor.Core;

/// <summary>Formats and parses times as m:ss.fff (or h:mm:ss.fff).</summary>
public static class TimeFormat
{
    public static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "–";
        if (seconds < 0)
            return "-" + Format(-seconds);

        long total = (long)Math.Round(seconds * 1000);
        long hours = total / 3_600_000;
        long minutes = total / 60_000 % 60;
        long secs = total / 1000 % 60;
        long millis = total % 1000;
        return hours > 0
            ? $"{hours}:{minutes:00}:{secs:00}.{millis:000}"
            : $"{minutes}:{secs:00}.{millis:000}";
    }

    /// <summary>
    /// Accepts "83.5", "1:23.5" and "1:01:23.5". Both "." and "," work as decimal separator.
    /// </summary>
    public static bool TryParse(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length > 3)
            return false;

        double total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            var style = last ? NumberStyles.AllowDecimalPoint : NumberStyles.None;
            if (!double.TryParse(parts[i], style, CultureInfo.InvariantCulture, out double value))
                return false;
            if (i > 0 && value >= 60)
                return false;
            total = total * 60 + value;
        }

        seconds = total;
        return true;
    }
}
