namespace Boh.Web;

/// <summary>
/// Strips line breaks from a value before it reaches a log message.
/// </summary>
/// <remarks>
/// Structured logging keeps the value out of the format string, but the console and file
/// sinks still render it as text — so a newline inside a username or a URL forges a whole
/// log entry. Anything user-supplied that gets logged goes through here first.
/// </remarks>
public static class LogSafe
{
    public static string Value(string? value) =>
        (value ?? "").Replace("\r", "").Replace("\n", "");
}
