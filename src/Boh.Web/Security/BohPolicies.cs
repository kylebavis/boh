namespace Boh.Web.Security;

public static class BohPolicies
{
    /// <summary>Pages private even under <c>BOH_PUBLIC_READ</c>. Waived when auth is off.</summary>
    public const string CanWrite = "CanWrite";

    /// <summary>Instance-wide configuration. Waived when auth is off.</summary>
    public const string IsAdmin = "IsAdmin";

    /// <summary>API reads: open when pages are, else a token.</summary>
    public const string ApiRead = "ApiRead";

    /// <summary>API writes: a token unless auth is off.</summary>
    public const string ApiWrite = "ApiWrite";
}
