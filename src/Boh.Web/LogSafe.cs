namespace Boh.Web;

/// <summary>Strips line breaks from user input before logging, so it can't forge log entries.</summary>
public static class LogSafe
{
    public static string Value(string? value) =>
        (value ?? "").Replace("\r", "").Replace("\n", "");
}
