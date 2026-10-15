using DedupTool; using Xunit;
namespace DedupTool.Tests;

public class SentDatesTests
{
    [Theory]
    [InlineData("2024-03-05T14:22:00-05:00", "2024-03-05T19:22:00Z")]
    [InlineData("2024-03-05T19:22:00Z",     "2024-03-05T19:22:00Z")]
    [InlineData("2024-03-06T09:10:00-05:00", "2024-03-06T14:10:00Z")]
    [InlineData("2024-03-06T11:45:00-05:00", "2024-03-06T16:45:00Z")]
    [InlineData("2024-03-05T19:22:00.123Z",  "2024-03-05T19:22:00Z")] // fractional dropped
    [InlineData(null, "")] [InlineData("", "")]
    public void Normalize_ToUtcZFormat(string? input, string expected)
        => Assert.Equal(expected, SentDates.Normalize(input));
}
