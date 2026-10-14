using System.Reflection;
using Xunit.Sdk;

namespace AudioEditor.Core.Tests;

/// <summary>
/// A test that needs one of the encoders Windows provides. Where it is missing (Windows N
/// editions, Windows Server without Media Foundation) the test is reported as skipped, so a run
/// without it doesn't look like it covered that format.
/// </summary>
internal sealed class EncoderFactAttribute : FactAttribute
{
    public EncoderFactAttribute(ExportFormat format)
    {
        if (!AudioExporter.IsAvailable(format))
            Skip = SkipReason(format);
    }

    internal static string SkipReason(ExportFormat format) => $"This machine has no {format} encoder.";
}

/// <summary>A theory row for a format whose encoder Windows provides; skipped where it is missing.</summary>
internal sealed class EncoderDataAttribute(ExportFormat format) : DataAttribute
{
    public override string? Skip => AudioExporter.IsAvailable(format) ? null : EncoderFactAttribute.SkipReason(format);

    public override IEnumerable<object[]> GetData(MethodInfo testMethod) => [[format]];
}
