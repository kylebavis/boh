namespace Boh.Web;

/// <summary>
/// "5 minutes ago", for moments recent enough that a clock time would make the reader work out
/// how long ago it was — and which, rendered on the server, would be in the server's time zone.
/// </summary>
public static class RelativeTime
{
    public static string Since(DateTimeOffset moment, DateTimeOffset now)
    {
        var elapsed = now - moment;

        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return Ago((int)elapsed.TotalMinutes, "minute");
        if (elapsed < TimeSpan.FromDays(1)) return Ago((int)elapsed.TotalHours, "hour");

        return Ago((int)elapsed.TotalDays, "day");
    }

    private static string Ago(int count, string unit) => count == 1 ? $"1 {unit} ago" : $"{count} {unit}s ago";
}
