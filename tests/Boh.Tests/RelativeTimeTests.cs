using Boh.Web;

namespace Boh.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Test]
    [Arguments(10, "just now")]
    [Arguments(60, "1 minute ago")]
    [Arguments(59 * 60, "59 minutes ago")]
    [Arguments(2 * 3600, "2 hours ago")]
    [Arguments(26 * 3600, "1 day ago")]
    [Arguments(3 * 86400, "3 days ago")]
    public async Task Describes_how_long_ago_something_happened(int secondsAgo, string expected)
    {
        await Assert.That(RelativeTime.Since(Now.AddSeconds(-secondsAgo), Now)).IsEqualTo(expected);
    }
}
