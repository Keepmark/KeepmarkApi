using System.Net;
using System.Text.Json;
using KeepmarkApi.Config;
using KeepmarkApi.Errors;
using KeepmarkApi.Logging;

namespace KeepmarkApi.Middleware;

/// <summary>Error mapping mirroring api/src/server.ts handleError:
/// XbsException → its {code,status}; 413 body-too-large → SyncDataLimitExceeded;
/// anything else → Unspecified (500). Only JSON is served.</summary>
public sealed class ErrorHandlerMiddleware(RequestDelegate next, ILogger<ErrorHandlerMiddleware> log)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await next(ctx).ConfigureAwait(false);
            if (ctx.Response.StatusCode == 404 && !ctx.Response.HasStarted)
            {
                await WriteAsync(ctx, new RouteNotImplementedException()).ConfigureAwait(false);
            }
        }
        catch (XbsException ex)
        {
            if (!ctx.Response.HasStarted)
            {
                await WriteAsync(ctx, ex).ConfigureAwait(false);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == 413)
        {
            if (!ctx.Response.HasStarted)
            {
                await WriteAsync(ctx, new SyncDataLimitExceededException()).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error on {Path}", LogSanitizer.Path(ctx.Request.Path.ToString()));
            if (!ctx.Response.HasStarted)
            {
                await WriteAsync(ctx, new UnspecifiedException()).ConfigureAwait(false);
            }
        }
    }

    private static Task WriteAsync(HttpContext ctx, XbsException ex)
    {
        ctx.Response.StatusCode = ex.StatusCode;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new ErrorBody
        {
            Code = ex.Code,
            Message = ex.Message,
        }, Json));
    }
}

/// <summary>Rejects origins not in AllowedOrigins, mirroring the cors()
/// callback in server.ts initApplication (OriginNotPermittedException).
/// Empty list = all origins permitted. Only string equality is supported
/// (Node also accepts RegExp entries — documented in README).</summary>
public sealed class OriginGuardMiddleware(RequestDelegate next, XbsSettings settings)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        if (settings.AllowedOrigins.Count > 0)
        {
            var origin = ctx.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin) && !settings.AllowedOrigins.Contains(origin))
            {
                throw new OriginNotPermittedException();
            }
        }

        return next(ctx);
    }
}
