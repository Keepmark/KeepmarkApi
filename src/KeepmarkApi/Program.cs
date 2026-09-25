using System.Globalization;
using System.Threading.RateLimiting;
using KeepmarkApi.Config;
using KeepmarkApi.Data;
using KeepmarkApi.Errors;
using KeepmarkApi.Logging;
using KeepmarkApi.Middleware;
using KeepmarkApi.Services;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Settings mirror api/config/settings.default.json; ops can overlay
// appsettings.Local.json (git-ignored) or environment variables.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Services.Configure<XbsSettings>(builder.Configuration);
var settings = builder.Configuration.Get<XbsSettings>() ?? new XbsSettings();

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
builder.Services.AddScoped<NewSyncLogsService>();
builder.Services.AddScoped<BookmarksService>();
builder.Services.AddScoped<InfoService>();
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
            ctx.HttpContext.Response.ContentType = "application/json";
            await ctx.HttpContext.Response.WriteAsync(
                System.Text.Json.JsonSerializer.Serialize(new ErrorBody
                {
                    Code = nameof(RequestThrottledException),
                    Message = new RequestThrottledException().Message,
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

// Request log with redacted sync IDs (IDs are bearer tokens — never log full).
app.Logger.LogInformation("Service started at {Url}", BuildUrl(settings));
app.Use(async (ctx, next) =>
{
    await next().ConfigureAwait(false);
    app.Logger.LogInformation("{Method} {Path} {Status}",
        ctx.Request.Method, LogSanitizer.Path(ctx.Request.Path.ToString()), ctx.Response.StatusCode);
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
