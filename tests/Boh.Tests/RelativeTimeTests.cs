using Boh.Web;

namespace Boh.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(60, "1 minute ago")]
    [InlineData(59 * 60, "59 minutes ago")]
    [InlineData(2 * 3600, "2 hours ago")]
    [InlineData(26 * 3600, "1 day ago")]
    [InlineData(3 * 86400, "3 days ago")]
    public void Describes_how_long_ago_something_happened(int secondsAgo, string expected)
    {
        Assert.Equal(expected, RelativeTime.Since(Now.AddSeconds(-secondsAgo), Now));
    }
}
