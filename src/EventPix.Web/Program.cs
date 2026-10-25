using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using EventPix.Web.Cli;
using EventPix.Web.Configuration;
using EventPix.Web.Data;
using EventPix.Web.Features;
using EventPix.Web.Media;
using EventPix.Web.Notifications;
using EventPix.Web.Security;
using EventPix.Web.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration.GetSection(EventPixOptions.Section);
var opts = cfg.Get<EventPixOptions>() ?? new EventPixOptions();
var dataPath = Path.GetFullPath(opts.DataPath);
Directory.CreateDirectory(dataPath);

// ---- configuration --------------------------------------------------------------------------
builder.Services.Configure<EventPixOptions>(cfg);
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.Section));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.Section));
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = opts.MaxUploadBytes; });
builder.Services.Configure<FormOptions>(f => f.MultipartBodyLengthLimit = opts.MaxUploadBytes);
builder.Services.ConfigureHttpJsonOptions(j => j.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

if (opts.TrustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();   // only safe when the app is reachable solely through your reverse proxy
    });
}

// ---- data, keys, services ---------------------------------------------------------------------
builder.Services.AddDbContext<EventPixDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataPath, "eventpix.db")}"));
builder.Services.AddDataProtection().SetApplicationName("EventPix")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataPath, "keys")));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SecretPepper>();
builder.Services.AddSingleton<EventTokenService>();
builder.Services.AddSingleton<IMediaStorage, FileSystemMediaStorage>();
builder.Services.AddSingleton<MediaQueue>();
builder.Services.AddSingleton<VideoFrameExtractor>();
builder.Services.AddSingleton<VideoProbe>();
builder.Services.AddScoped<OtpService>();
builder.Services.AddHostedService<MediaProcessingWorker>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<TelegramUpdateWorker>();

var smtpConfigured = !string.IsNullOrWhiteSpace(builder.Configuration[$"{SmtpOptions.Section}:Host"]);
if (smtpConfigured)
{
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}
else if (builder.Environment.IsDevelopment())
{
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
}
else
{
    throw new InvalidOperationException("Smtp:Host must be configured outside Development.");
}

builder.Services.AddSingleton<INotificationChannel, EmailChannel>();
builder.Services.AddSingleton<INotificationChannel, TelegramChannel>();

// ---- authentication: cookie session issued after the e-mail code is verified --------------------
builder.Services.AddAuthentication("Cookies").AddCookie("Cookies", o =>
{
    o.Cookie.Name = "eventpix.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Lax;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;   // https is terminated by your proxy; forwarded proto fixes this
    o.ExpireTimeSpan = TimeSpan.FromDays(30);
    o.SlidingExpiration = true;
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(r =>
{
    r.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    r.AddPolicy("auth", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
    r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c => RateLimitPartition.GetFixedWindowLimiter(
        Ip(c), _ => new FixedWindowRateLimiterOptions { PermitLimit = 3000, Window = TimeSpan.FromMinutes(1) }));
});

// ---- theme ---------------------------------------------------------------------------------------
var themeName = opts.ActiveTheme;
if (!ThemeName.Valid().IsMatch(themeName))
{
    throw new InvalidOperationException("EventPix:ActiveTheme may only contain letters, digits, '-' and '_'.");
}

var themeDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, opts.ThemesPath, themeName));
if (!File.Exists(Path.Combine(themeDir, "index.html")))
{
    throw new DirectoryNotFoundException($"Theme '{themeName}' not found (expected {themeDir}/index.html).");
}

var app = builder.Build();

// ---- database bootstrap ------------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<EventPixDb>();
    await db.Database.MigrateAsync();
}

if (CliCommands.IsCommand(args))
{
    return await CliCommands.RunAsync(app.Services, args);
}

// ---- pipeline --------------------------------------------------------------------------------------------
if (opts.TrustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.Use((ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; " +
        "media-src 'self' blob:; font-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    h.XContentTypeOptions = "nosniff";
    h["Referrer-Policy"] = "no-referrer";                       // the secret URL must never leak via Referer
    h["X-Robots-Tag"] = "noindex, nofollow, noarchive";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    if (ctx.Request.Path.StartsWithSegments("/api"))
    {
        h.CacheControl = "no-store";
    }

    return next();
});

// CSRF: state-changing API calls must carry a custom header, which cross-site forms cannot set.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(ctx.Request.Method) &&
        !HttpMethods.IsHead(ctx.Request.Method) && ctx.Request.Headers.XRequestedWith != "eventpix")
    { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    await next();
});

// Core JS and theme files are revalidated on every load (ETag makes it a cheap 304): operators edit
// themes in place, so browsers must never heuristically cache stale markup/JS.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(themeDir),
    RequestPath = "/theme",
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// ---- routes -------------------------------------------------------------------------------------------------
app.MapGet("/robots.txt", () => Results.Text("User-agent: *\nDisallow: /\n"));
app.MapGet("/e/{token}", () =>                                   // SPA shell supplied by the theme
    Results.File(Path.Combine(themeDir, "index.html"), "text/html"));
// Unsubscribe page /u/{id}.{sig}: core JS renders the theme's "unsubscribe" template and calls the API below.
app.MapGet("/u/{token}", () => Results.File(Path.Combine(themeDir, "index.html"), "text/html"));

var open = app.MapGroup("/api/e/{token}").WithEventScope(requireMember: false);
AuthEndpoints.Map(open);

var api = app.MapGroup("/api/e/{token}").WithEventScope(requireMember: true);
TabsEndpoints.Map(api);
MediaEndpoints.Map(api);
DownloadEndpoints.Map(api);
SubscriptionEndpoints.Map(api);
InviteEndpoints.Map(api);
SubscriptionEndpoints.MapUnsubscribe(app);

await app.RunAsync();
return 0;

// Marker for WebApplicationFactory<Program> in integration tests (visible via InternalsVisibleTo).
internal partial class Program { }
