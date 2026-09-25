using System.Globalization;
using System.Threading.RateLimiting;
using KeepmarkApi.Config;
using KeepmarkApi.Data;
using KeepmarkApi.Errors;
using KeepmarkApi.Logic;
using KeepmarkApi.Logging;
using KeepmarkApi.Middleware;
using Microsoft.AspNetCore.HttpOverrides;

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

if (!IsValidLocation(settings.Location))
{
    Console.Error.WriteLine("Location is not a valid country code, exiting");
    return 1;
}

// Body cap mirrors express.json({limit: maxSyncSize}).
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = settings.EffectiveMaxSyncSize;
    kestrel.ListenAnyIP(settings.Server.Port, listen =>
    {
        if (settings.Server.Https.Enabled)
        {
            listen.UseHttps(settings.Server.Https.CertPath, settings.Server.Https.KeyPath);
        }
    });
});

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<MongoContext>();
builder.Services.AddScoped<KeepmarkApi.Stores.IBookmarkStore, MongoBookmarkStore>();
builder.Services.AddScoped<KeepmarkApi.Stores.ISyncLogStore, MongoSyncLogStore>();
builder.Services.AddScoped<BookmarksLogic>();
builder.Services.AddScoped<InfoLogic>();
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    o.JsonSerializerOptions.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
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
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    });
}

if (!string.IsNullOrEmpty(settings.Server.RelativePath) && settings.Server.RelativePath != "/")
{
    app.UsePathBase(settings.Server.RelativePath);
}

// Security headers (helmet + nocache equivalents).
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers.CacheControl = "no-store";
    await next().ConfigureAwait(false);
});

app.UseCors();
if (settings.Throttle.MaxRequests > 0)
{
    app.UseRateLimiter();
}

app.UseMiddleware<OriginGuardMiddleware>();
app.UseMiddleware<ErrorHandlerMiddleware>();

// Static brand assets (logo.png, favicon.png). Exact files only —
// missing paths fall through to controllers and keep the JSON 404 contract.
app.UseStaticFiles();

// Request log with redacted sync IDs (IDs are bearer tokens — never log full).
// Info: one summary line. Debug (verbose): IP, version header, sizes, timing.
app.Logger.LogInformation("Service started at {Url} (log level: {Level})",
    BuildUrl(settings), XbsLogLevels.Name(gate.MinLevel));
app.Logger.LogDebug("Effective settings: {Settings}", DescribeSettings(settings));
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    await next().ConfigureAwait(false);
    sw.Stop();
    var path = LogSanitizer.Path(ctx.Request.Path.ToString());
    app.Logger.LogInformation("{Method} {Path} {Status} {Ms}ms",
        ctx.Request.Method, path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
    app.Logger.LogDebug("{Method} {Path} {Status} {Ms}ms ip={Ip} version={Version} reqBytes={Req} resBytes={Res}",
        ctx.Request.Method, path, ctx.Response.StatusCode, sw.ElapsedMilliseconds,
        ctx.Connection.RemoteIpAddress,
        ctx.Request.Headers.TryGetValue("Accept-Version", out var v) ? v.ToString() : "(default)",
        ctx.Request.ContentLength, ctx.Response.ContentLength);
});

app.MapControllers();

// TTL + ipAddress indexes from api/README.md (plus Mongo connection check).
try
{
    await app.Services.GetRequiredService<MongoContext>().EnsureIndexesAsync().ConfigureAwait(false);
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
    + $"allowNewSyncs={s.Status.AllowNewSyncs} online={s.Status.Online} "
    + $"throttle={s.Throttle.MaxRequests}/{s.Throttle.TimeWindow}ms";

static bool IsValidLocation(string location)
{
    if (string.IsNullOrEmpty(location))
    {
        return true;
    }

    try
    {
        _ = new RegionInfo(location);
        return true;
    }
    catch
    {
        return false;
    }
}
