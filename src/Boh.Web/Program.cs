using Boh.Web;
using Boh.Web.Data;
using Boh.Web.Endpoints;
using Boh.Web.Security;
using Boh.Web.Services;
using Boh.Web.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var options = BohOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);

// The framework default of ~28.6 MB would reject most video before it reached our code.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = options.MaxUploadBytes);
builder.Services.Configure<FormOptions>(f =>
{
    f.MultipartBodyLengthLimit = options.MaxUploadBytes;
    f.ValueLengthLimit = int.MaxValue;
});

builder.Services.AddDbContext<BohDbContext>(o => o
    .UseSqlite(options.ConnectionString)
    .AddInterceptors(new SqlitePragmaInterceptor()));

// Process-wide caps on what any single ImageMagick decode may consume.
MagickMediaProcessor.ApplyResourceLimits();

builder.Services.AddSingleton<IFileStore, ContentAddressedFileStore>();
builder.Services.AddSingleton<ProcessRunner>();

// Order matters: the registry takes the first processor that recognizes a file, and
// ImageMagick is cheaper to ask than spawning ffprobe.
builder.Services.AddSingleton<IMediaProcessor, MagickMediaProcessor>();
builder.Services.AddSingleton<IMediaProcessor, VideoMediaProcessor>();
builder.Services.AddSingleton<MediaProcessorRegistry>();

builder.Services.AddScoped<GalleryDlImporter>();
builder.Services.AddScoped<PostService>();
builder.Services.AddScoped<TagService>();
builder.Services.AddScoped<UserService>();

builder.Services.AddScoped<RevalidateUserEvents>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        // Deleting or demoting a user has to take effect now, not whenever their cookie
        // happens to expire.
        o.EventsType = typeof(RevalidateUserEvents);
        o.LoginPath = "/Account/Login";
        o.AccessDeniedPath = "/Account/Login";
        o.ExpireTimeSpan = TimeSpan.FromDays(30);
        o.SlidingExpiration = true;
        o.Cookie.Name = "boh.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // SameAsRequest, not Always: plain-HTTP use on a LAN has to keep working, while
        // an HTTPS deployment still gets the Secure flag.
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization(o =>
{
    // Applied to the pages that stay private even under BOH_PUBLIC_READ. When auth is
    // switched off nobody can sign in, so the requirement has to fall away with it.
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

    // Reads are open when auth is off or public browsing is enabled; otherwise every page
    // requires a signed-in user. Writes are handled separately by the page filter, since
    // they must stay restricted even when reads are public.
    if (!options.AuthDisabled && !options.PublicRead)
    {
        o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    }
});

builder.Services.AddRazorPages(o => o.Conventions.ConfigureFilter(
    new RequireAuthForWritesFilter(options)));

// HTMX cannot post a hidden form field on every request, so the token travels in a header
// that the layout attaches once via hx-headers.
builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

// Keys default to the user profile, which is not persisted in the container. Without this,
// every restart invalidates antiforgery tokens and (later) auth cookies.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(options.KeysDir))
    .SetApplicationName("boh");

builder.Services.Configure<ForwardedHeadersOptions>(f =>
{
    f.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Self-hosted proxies sit at arbitrary addresses and the operator controls both ends.
    f.KnownIPNetworks.Clear();
    f.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// No HTTPS redirection: the container speaks plain HTTP and TLS terminates at the proxy.
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapFileEndpoints();

// Must stay reachable without credentials or the container healthcheck fails.
app.MapGet("/healthz", () => Results.Ok("ok")).WithName("Health").AllowAnonymous();

await InitializeAsync(app);

app.Run();

static async Task InitializeAsync(WebApplication app)
{
    var options = app.Services.GetRequiredService<BohOptions>();
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Boh.Startup");

    // Each location may be its own mount, so none can be assumed to exist or be writable.
    // Checked up front so a permissions problem names the path instead of surfacing later
    // as a failed upload.
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

    var store = app.Services.GetRequiredService<IFileStore>();
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

/// <summary>
/// Flags a storage layout that will misbehave. Warnings only: a misidentified filesystem
/// should never stop the application from starting.
/// </summary>
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
