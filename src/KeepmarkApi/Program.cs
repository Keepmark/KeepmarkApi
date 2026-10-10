using System.Globalization;
using System.Threading.RateLimiting;
using KeepmarkApi.Config;
using KeepmarkApi.Data;
using KeepmarkApi.Errors;
using KeepmarkApi.Json;
using KeepmarkApi.Logic;
using KeepmarkApi.Logging;
using KeepmarkApi.Middleware;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Console;

var builder = WebApplication.CreateBuilder(args);

// Settings mirror api/config/settings.default.json; ops can overlay
// appsettings.Local.json (git-ignored, hot-reloaded) or environment variables
// (Docker: Log__Stdout__Level, Db__Host, ... — __ maps to nested sections).
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.Configure<XbsSettings>(builder.Configuration);
var settings = builder.Configuration.Get<XbsSettings>() ?? new XbsSettings();

// Verbosity gate: the MEL filter below reads gate.MinLevel on every event,
// and LogLevelWatcher updates it when settings reload — trace/debug can be
// switched on the fly (edit appsettings.Local.json or LOG_LEVEL + recreate)
// without restart.
var gate = new LogLevelGate { MinLevel = XbsLogLevels.Parse(settings.Log.Stdout.Level) };
builder.Services.AddSingleton(gate);
builder.Services.AddHostedService<LogLevelWatcher>();
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddFilter((_, _, level) => level >= gate.MinLevel);
// Console sink: one provider with UTC timestamps + single lines (docker
// logs carry no format of their own — the default SimpleConsole prints no
// timestamp). Honors log.stdout.enabled.
builder.Logging.ClearProviders();
if (settings.Log.Stdout.Enabled)
{
    builder.Logging.AddSimpleConsole(o =>
    {
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        o.UseUtcTimestamp = true;
        o.SingleLine = true;
    });
}
// ASP.NET pipeline chatter (routing/MVC/diagnostics) at info buries the app
// lines (request summaries, creations, refusals). Cap it at warn; the hot
// gate still applies on top.
builder.Logging.AddFilter("Microsoft.AspNetCore",
    level => level >= LogLevel.Warning && level >= gate.MinLevel);

// File sink (log.file settings). Failures disable it with a warning;
// the gate still applies on the fly like the console sink.
if (settings.Log.File.Enabled)
{
    builder.Logging.AddProvider(new RotatingFileLoggerProvider(
        settings.Log.File.Path, settings.Log.File.Level,
        settings.Log.File.RotationPeriod, settings.Log.File.RotatedFilesToKeep, gate));
}

if (!IsValidLocation(settings.Location))
{
    Console.Error.WriteLine("Location is not a valid country code, exiting");
    return 1;
}

if (!SyncExpiryRules.IsValidDays(settings.SyncExpiryDays))
{
    Console.Error.WriteLine($"SyncExpiryDays must be 0-{SyncExpiryRules.MaxDays}, exiting");
    return 1;
}

// Body cap mirrors express.json({limit: maxSyncSize}).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = settings.EffectiveMaxSyncSize;
    kestrel.AddServerHeader = false;
    kestrel.ListenAnyIP(settings.Server.Port, listen =>
    {
        if (settings.Server.Https.Enabled)
        {
            // Cert+key are PEM files (Node parity): build the cert explicitly.
            // The (pfxPath, password) overload must NOT be used with PEM paths.
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate2
                .CreateFromPemFile(settings.Server.Https.CertPath, settings.Server.Https.KeyPath);
            listen.UseHttps(cert);
        }
    });
});

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<MongoContext>();
builder.Services.AddScoped<KeepmarkApi.Stores.IBookmarkStore, MongoBookmarkStore>();
builder.Services.AddScoped<KeepmarkApi.Stores.ISyncLogStore, MongoSyncLogStore>();
builder.Services.AddScoped<BookmarksLogic>();
builder.Services.AddScoped<InfoLogic>();
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    // Malformed JSON must keep the {code,message} contract: let actions run
    // with an invalid ModelState and map it to Unspecified (Node: 500).
    options.SuppressModelStateInvalidFilter = true;
});
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    // Whole-second instants keep .000 like JavaScript toISOString().
    o.JsonSerializerOptions.Converters.Add(new IsoDateTimeConverter());
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (settings.AllowedOrigins.Count == 0)
    {
        p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    }
    else
    {
        p.WithOrigins([.. settings.AllowedOrigins]).AllowAnyHeader().AllowAnyMethod();
    }
}));

// Throttle mirrors express-rate-limit (per-IP fixed window; 0 = disabled).
if (settings.Throttle.MaxRequests > 0)
{
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = 429;
        o.OnRejected = async (ctx, _) =>
        {
            var logger = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Throttle: rejected {Method} {Path} from {Ip}",
                ctx.HttpContext.Request.Method,
                LogSanitizer.Path(ctx.HttpContext.Request.Path.ToString()),
                ctx.HttpContext.Connection.RemoteIpAddress);
            ctx.HttpContext.Response.ContentType = "application/json";
            // Tell well-behaved clients when the window resets (seconds).
            var retryAfter = Math.Max(1, (int)Math.Ceiling(settings.Throttle.TimeWindow / 1000.0));
            ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
            var (status, code, message) = ErrorMap.Map(new RequestThrottledException());
            ctx.HttpContext.Response.StatusCode = status;
            await ctx.HttpContext.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new ErrorBody
                {
                    Code = code,
                    Message = message,
                }));
        };
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = settings.Throttle.MaxRequests,
                Window = TimeSpan.FromMilliseconds(settings.Throttle.TimeWindow),
            });
        });
    });
}

var app = builder.Build();

if (settings.Server.BehindProxy)
{
    // Single trusted hop only: longer chains let clients forge X-Forwarded-For
    // and rotate identities past the throttle/quota. Restrict to known proxies
    // in stricter deployments (KnownProxies/KnownNetworks).
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
    });
}

if (!string.IsNullOrEmpty(settings.Server.RelativePath) && settings.Server.RelativePath != "/")
{
    app.UsePathBase(settings.Server.RelativePath);
}

// HSTS only when serving HTTPS directly (behind a TLS proxy the proxy owns it).
if (settings.Server.Https.Enabled)
{
    app.UseHsts();
}

// Security headers (helmet + nocache equivalents).
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-DNS-Prefetch-Control"] = "off";
    ctx.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    ctx.Response.Headers.CacheControl = "no-store";
    await next().ConfigureAwait(false);
});

// Request log with redacted sync IDs (IDs are bearer tokens — never log full).
// Info: one summary line. Debug (verbose): IP, version header, sizes, timing.
// Registered OUTSIDE the error handler and in a finally, so error responses
// (401/409/412/503…) and throttle rejections are summarized too.
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        await next().ConfigureAwait(false);
    }
    finally
    {
        sw.Stop();
        var path = LogSanitizer.Path(ctx.Request.Path.ToString());
        app.Logger.LogInformation("{Method} {Path} {Status} {Ms}ms",
            ctx.Request.Method, path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
        app.Logger.LogDebug("{Method} {Path} {Status} {Ms}ms ip={Ip} version={Version} reqBytes={Req} resBytes={Res}",
            ctx.Request.Method, path, ctx.Response.StatusCode, sw.ElapsedMilliseconds,
            ctx.Connection.RemoteIpAddress,
            ctx.Request.Headers.TryGetValue("Accept-Version", out var v) ? v.ToString() : "(default)",
            ctx.Request.ContentLength, ctx.Response.ContentLength);
    }
});

app.UseCors();
if (settings.Throttle.MaxRequests > 0)
{
    app.UseRateLimiter();
}

// ErrorHandler is outer so OriginGuard rejections keep the {code,message} shape.
app.UseMiddleware<ErrorHandlerMiddleware>();
app.UseMiddleware<OriginGuardMiddleware>();

// Static brand assets (logo.svg, logo.png, favicon.png). Exact files only —
// missing paths fall through to controllers and keep the JSON 404 contract.
app.UseStaticFiles();

app.MapControllers();

// TTL + ipAddress indexes from api/README.md, lastAccessed TTL per
// SyncExpiryDays (plus Mongo connection check).
try
{
    await app.Services.GetRequiredService<MongoContext>()
        .EnsureIndexesAsync(settings.SyncExpiryDays, app.Logger, app.Lifetime.ApplicationStopping).ConfigureAwait(false);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Unable to connect to database");
    return 1;
}

await app.RunAsync().ConfigureAwait(false);
return 0;

static string BuildUrl(XbsSettings s)
{
    var protocol = s.Server.Https.Enabled ? "https" : "http";
    return $"{protocol}://{s.Server.Host}:{s.Server.Port}{s.Server.RelativePath}";
}

/// <summary>Settings dump for verbose mode. The DB password is never included.</summary>
static string DescribeSettings(XbsSettings s)
    => $"origins={s.AllowedOrigins.Count} dailyLimit={s.DailyNewSyncsLimit} "
    + $"db={s.Db.Host}:{s.Db.Port}/{s.Db.Name} authSource={s.Db.AuthSource} user={s.Db.Username} ssl={s.Db.Ssl} srv={s.Db.UseSrv} "
    + $"location={s.Location} maxSyncs={s.MaxSyncs} maxSyncSize={s.MaxSyncSize} "
    + $"behindProxy={s.Server.BehindProxy} https={s.Server.Https.Enabled} relativePath={s.Server.RelativePath} "
    + $"allowNewSyncs={s.Status.AllowNewSyncs} online={s.Status.Online} syncExpiryDays={s.SyncExpiryDays} "
    + $"throttle={s.Throttle.MaxRequests}/{s.Throttle.TimeWindow}ms";

static bool IsValidLocation(string location)
{
    if (string.IsNullOrEmpty(location))
    {
        return true;
    }

    // ISO 3166 alpha-2 only: RegionInfo alone also accepts culture names
    // ("en-US") and 3-letter codes, which Node's country-list rejects.
    if (location.Length != 2 || !location.All(char.IsAsciiLetter))
    {
        return false;
    }

    try
    {
        _ = new RegionInfo(location);
        return true;
    }
    catch (ArgumentException)
    {
        return false;
    }
}
