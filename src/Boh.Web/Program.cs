using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Boh.Web;
using Boh.Web.Data;
using Boh.Web.Data.Entities;
using Boh.Web.Endpoints;
using Boh.Web.Jobs;
using Boh.Web.Pages.Account;
using Boh.Web.Security;
using Boh.Web.Services;
using Boh.Web.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var options = BohOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);

builder.WebHost.ConfigureKestrel(k =>
{
    // The default ~28.6 MB would reject most video.
    k.Limits.MaxRequestBodySize = options.MaxUploadBytes;

    // Don't advertise the server.
    k.AddServerHeader = false;
});
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = options.MaxUploadBytes;
    f.ValueLengthLimit = int.MaxValue;
});

var hashIndex = new PerceptualHashIndex();
builder.Services.AddSingleton(hashIndex);

var activeUsers = new ActiveUserCache();
builder.Services.AddSingleton(activeUsers);

builder.Services.AddDbContext<BohDbContext>(o => o
    .UseSqlite(options.ConnectionString)
    .AddInterceptors(new SqlitePragmaInterceptor())
    .AddInterceptors(hashIndex.Interceptors)
    .AddInterceptors(activeUsers.Interceptors));

// Process-wide caps on what any single ImageMagick decode may consume.
MagickMediaProcessor.ApplyResourceLimits();

builder.Services.AddSingleton<ContentAddressedFileStore>();
builder.Services.AddSingleton<ProcessRunner>();

// First match wins; Magick is cheaper than spawning ffprobe.
builder.Services.AddSingleton<IMediaProcessor, MagickMediaProcessor>();
builder.Services.AddSingleton<IMediaProcessor, VideoMediaProcessor>();
builder.Services.AddSingleton<MediaProcessorRegistry>();

builder.Services.AddScoped<GalleryDlImporter>();
builder.Services.AddScoped<DuplicateService>();
builder.Services.AddScoped<PostService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddScoped<UserService>();

// IdentityCore only supplies a UserManager for passkeys over boh's Users table.
// Passwords and the sign-in cookie stay boh's own, so no SignInManager.
builder.Services.AddIdentityCore<User>().AddUserStore<BohUserStore>();
builder.Services.AddScoped<IPasskeyHandler<User>, PasskeyHandler<User>>();
builder.Services.Configure<IdentityPasskeyOptions>(p => PasskeyRelyingParty.Configure(p, options));
builder.Services.AddScoped<PasskeyService>();
builder.Services.AddSingleton<PasskeyChallenge>();

builder.Services.AddSingleton<JobQueue>();
builder.Services.AddHostedService<JobWorker>();

builder.Services.AddScoped<RevalidateUserEvents>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        // Deletion or demotion applies immediately.
        o.EventsType = typeof(RevalidateUserEvents);
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Cookie.Name = "boh.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // SameAsRequest keeps plain-HTTP LAN use working.
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthentication>(ApiTokenAuthentication.SchemeName, null);

builder.Services.AddScoped<ApiTokenService>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddAuthorization(o =>
{
    // Pages private even under BOH_PUBLIC_READ; open when auth is off.
    o.AddPolicy(BohPolicies.CanWrite, policy =>
    {
        if (options.AuthDisabled) policy.RequireAssertion(_ => true);
        else policy.RequireAuthenticatedUser();
    });

    o.AddPolicy(BohPolicies.IsAdmin, policy =>
    {
        if (options.AuthDisabled) policy.RequireAssertion(_ => true);
        else policy.RequireRole(UserPrincipal.AdminRole);
    });

    o.AddPolicy(BohPolicies.ApiRead, policy =>
    {
        policy.AddAuthenticationSchemes(ApiTokenAuthentication.SchemeName);
        if (options.AuthDisabled || options.PublicRead) policy.RequireAssertion(_ => true);
        else policy.RequireAuthenticatedUser();
    });

    o.AddPolicy(BohPolicies.ApiWrite, policy =>
    {
        policy.AddAuthenticationSchemes(ApiTokenAuthentication.SchemeName);
        if (options.AuthDisabled) policy.RequireAssertion(_ => true);
        else policy.RequireAuthenticatedUser();
    });

    // Reads. Writes are gated by RequireAuthForWritesFilter.
    if (!options.AuthDisabled && !options.PublicRead)
    {
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
});

builder.Services.AddRazorPages(o => o.Conventions.ConfigureFilter(
    new RequireAuthForWritesFilter(options)));

// No lockout exists, so rate-limit login attempts (POSTs only).
builder.Services.AddRateLimiter(r =>
{
    r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    r.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter =
            LoginModel.RateLimitWindow.TotalSeconds.ToString("F0");
        return ValueTask.CompletedTask;
    };

    r.AddPolicy(LoginModel.RateLimitPolicy, context =>
    {
        if (!HttpMethods.IsPost(context.Request.Method))
            return RateLimitPartition.GetNoLimiter("read");

        // Forwarded headers already applied, so this is the client the proxy saw.
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // Sliding, so a window boundary can't let twice the limit through.
        return RateLimitPartition.GetSlidingWindowLimiter(client, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = LoginModel.RateLimitAttempts,
            Window = LoginModel.RateLimitWindow,
            SegmentsPerWindow = 5,
            QueueLimit = 0
        });
    });
});

// htmx sends the token as a header, set once via hx-headers:inherited.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

// Persist keys, or every restart invalidates tokens and cookies.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDir))
    .SetApplicationName("boh");

builder.Services.Configure<ForwardedHeadersOptions>(f =>
{
    f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    f.KnownIPNetworks.Clear();
    f.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Below the exception handler, which clears headers and re-runs from here.
app.UseBohSecurityHeaders();

// No HTTPS redirection: TLS terminates at the proxy.
app.UseRouting();

// After routing, so endpoint policies are visible.
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

// Anonymous so the sign-in page is styled.
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages().WithStaticAssets();
app.MapFileEndpoints();
app.MapApiEndpoints();

// Anonymous for the container healthcheck.
app.MapGet("/healthz", () => Results.Ok("ok")).WithName("Health").AllowAnonymous();

await InitializeAsync(app);

app.Run();

static async Task InitializeAsync(WebApplication app)
{
    var options = app.Services.GetRequiredService<BohOptions>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Boh.Startup");

    // Each path may be its own mount; fail early naming the path.
    var problems = StoragePreflight.Check(options);
    if (problems.Count > 0)
    {
        var uid = StoragePreflight.CurrentUserId();
        var hint =
            $"this container runs as uid {uid} — grant it access with " +
            $"\"chown -R {uid}:{uid} <host directory>\", or for a network share mount it with uid={uid}";

        foreach (var problem in problems)
        {
            logger.LogCritical(
                "Storage for {Purpose} is unusable at {Path}: {Reason}. Fix: {Hint}.",
                problem.Purpose, problem.Path, problem.Reason, hint);
        }

        throw new InvalidOperationException(
            $"{problems.Count} storage location(s) are not writable — see the messages above.");
    }

    var store = app.Services.GetRequiredService<ContentAddressedFileStore>();
    store.EnsureDirectories();
    store.CleanTemp(TimeSpan.FromHours(6));

    WarnAboutStorageLayout(options, logger);

    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<BohDbContext>().Database.MigrateAsync();

    if (options.AuthDisabled)
    {
        logger.LogWarning(
            "BOH_AUTH_MODE=none — every page, including upload, delete and import, is open " +
            "to anyone who can reach this port.");
    }
    else
    {
        await scope.ServiceProvider.GetRequiredService<UserService>()
            .SeedAdminAsync(options.AdminPassword, CancellationToken.None);
    }

    logger.LogInformation(
        "boh ready — db {Database}, originals {Originals}, thumbs {Thumbs}, public read {PublicRead}",
        options.DatabasePath, options.OriginalsDir, options.ThumbsDir, options.PublicRead);
}

/// <summary>Warns about storage layouts that will misbehave. Never fails startup.</summary>
static void WarnAboutStorageLayout(BohOptions options, ILogger logger)
{
    var databaseFs = FilesystemProbe.GetFilesystemType(options.DatabasePath);

    if (FilesystemProbe.IsNetworkFilesystem(databaseFs))
    {
        logger.LogWarning(
            "The SQLite database at {Path} is on a {Filesystem} filesystem. Network shares do " +
            "not provide the file locking SQLite needs and WAL mode requires shared memory they " +
            "cannot offer; this risks corruption. Point BOH_DB_PATH at local storage and keep " +
            "only BOH_ORIGINALS_PATH on the share.",
            options.DatabasePath, databaseFs);
    }

    var keysFs = FilesystemProbe.GetFilesystemType(options.KeysDir);
    if (FilesystemProbe.IsNetworkFilesystem(keysFs))
    {
        logger.LogWarning(
            "Data protection keys at {Path} are on a {Filesystem} filesystem. Keep them beside " +
            "the database on local storage.",
            options.KeysDir, keysFs);
    }

    var originalsFs = FilesystemProbe.GetFilesystemType(options.OriginalsDir);
    if (originalsFs is not null)
    {
        logger.LogInformation("Originals are on a {Filesystem} filesystem.", originalsFs);
    }
}

/// <summary>Present so the test project can reference the web assembly.</summary>
public partial class Program;
