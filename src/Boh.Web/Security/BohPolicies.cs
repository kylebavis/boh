namespace Boh.Web.Security;

public static class BohPolicies
{
    /// <summary>
    /// Guards pages that must stay private even when <c>BOH_PUBLIC_READ</c> opens browsing
    /// to everyone — upload, import and tag administration.
    /// </summary>
    /// <remarks>
    /// A named policy rather than a bare <c>[Authorize]</c> because the requirement is
    /// conditional: with <c>BOH_AUTH_MODE=none</c> there is no way to become authenticated,
    /// so an unconditional attribute locks the operator out of the very pages that mode is
    /// supposed to open.
    /// </remarks>
    public const string CanWrite = "CanWrite";

    /// <summary>
    /// Guards instance-wide configuration: user management, and the tag graph and maintenance
    /// actions that change what everyone else sees.
    /// </summary>
    /// <remarks>
    /// Like <see cref="CanWrite"/>, this falls away entirely under <c>BOH_AUTH_MODE=none</c>,
    /// where there is no identity to be an administrator.
    /// </remarks>
    public const string IsAdmin = "IsAdmin";
}
