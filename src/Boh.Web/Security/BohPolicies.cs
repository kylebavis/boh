namespace Boh.Web.Security;

public static class BohPolicies
{
    /// <summary>Pages private even under <c>BOH_PUBLIC_READ</c>. Waived when auth is off.</summary>
    public const string CanWrite = "CanWrite";

    /// <summary>Instance-wide configuration. Waived when auth is off.</summary>
    public const string IsAdmin = "IsAdmin";
}
