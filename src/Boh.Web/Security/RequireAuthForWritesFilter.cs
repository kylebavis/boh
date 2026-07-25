using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Boh.Web.Security;

/// <summary>
/// Requires an authenticated user for anything that changes state.
/// </summary>
/// <remarks>
/// Reads are governed by the fallback authorization policy, which lets everyone in when
/// <c>BOH_PUBLIC_READ</c> is on. That is page-level, but a page like post detail mixes a
/// public GET with privileged POST handlers, and Razor Pages cannot attribute individual
/// handlers — so the method, not the page, decides here.
/// </remarks>
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
