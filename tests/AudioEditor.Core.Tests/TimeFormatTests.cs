namespace AudioEditor.Core.Tests;

public class TimeFormatTests
{
    [Theory]
    [InlineData(0, "0:00.000")]
    [InlineData(83.456, "1:23.456")]
    [InlineData(59.9996, "1:00.000")]
    [InlineData(3723.5, "1:02:03.500")]
    [InlineData(-1.5, "-0:01.500")]
    [InlineData(double.NaN, "–")]
    public void Format(double seconds, string expected) =>
        Assert.Equal(expected, TimeFormat.Format(seconds));

    [Theory]
    [InlineData("83.5", 83.5)]
    [InlineData("83,5", 83.5)]
    [InlineData("1:23.456", 83.456)]
    [InlineData(" 1:02:03.5 ", 3723.5)]
    [InlineData("0:05", 5)]
    public void Parse_accepts(string text, double expected)
    {
        Assert.True(TimeFormat.TryParse(text, out double seconds));
        Assert.Equal(expected, seconds, precision: 9);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    [InlineData("1:")]
    [InlineData("-3")]
    [InlineData("1.5:20")]
    [InlineData("1:2:3:4")]
    public void Parse_rejects(string text) =>
        Assert.False(TimeFormat.TryParse(text, out _));
}
