using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Boh.Web.Security;

/// <summary>
/// Requires sign-in for non-GET handlers. Per method rather than per page, since pages like
/// post detail mix public reads with writes.
/// </summary>
public sealed class RequireAuthForWritesFilter(BohOptions options) : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(
        PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        if (IsAllowed(context))
        {
            await next();
            return;
        }

        // Challenge rather than forbid: an unauthenticated writer should get the login page.
        context.Result = new Microsoft.AspNetCore.Mvc.ChallengeResult();
    }

    private bool IsAllowed(PageHandlerExecutingContext context)
    {
        if (options.AuthDisabled) return true;

        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return true;

        // The login form itself has to accept an anonymous POST.
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any()) return true;

        return context.HttpContext.User.Identity?.IsAuthenticated == true;
    }
}
