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
            // Node's catch-all returns 404 NotImplemented for any unmatched
            // route AND method; ASP.NET routing answers 405 on matched-route /
            // wrong-method (e.g. GET /bookmarks). Normalize both, but only when
            // nothing was written yet (explicit responses like 405
            // NewSyncsForbidden carry their own body via exceptions).
            if ((ctx.Response.StatusCode == 404 || ctx.Response.StatusCode == 405)
                && !ctx.Response.HasStarted
                && (ctx.Response.ContentLength is null or 0))
            {
                var (status, code, message) = ErrorMap.Map(new RouteNotImplementedException());
                await WriteAsync(ctx, status, code, message, ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (XbsException ex)
        {
            // Client errors (4xx) at Debug, server errors (5xx) at Error.
            if (ex.StatusCode >= 500)
            {
                log.LogError("Request failed {Code} on {Path}", ex.Code, LogSanitizer.Path(ctx.Request.Path.ToString()));
            }
            else
            {
                log.LogDebug("Request refused {Code} on {Path}", ex.Code, LogSanitizer.Path(ctx.Request.Path.ToString()));
            }

            if (!ctx.Response.HasStarted)
            {
                var (status, code, message) = ErrorMap.Map(ex);
                await WriteAsync(ctx, status, code, message, ctx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Exception mapped = ex is BadHttpRequestException bad && bad.StatusCode == 413
                ? new SyncDataLimitExceededException()
                : ex;
            if (mapped is not XbsException)
            {
                log.LogError(ex, "Unhandled error on {Path}", LogSanitizer.Path(ctx.Request.Path.ToString()));
            }

            if (!ctx.Response.HasStarted)
            {
                var (status, code, message) = ErrorMap.Map(mapped);
                await WriteAsync(ctx, status, code, message, ctx.RequestAborted).ConfigureAwait(false);
            }
        }
    }

    private static Task WriteAsync(HttpContext ctx, int status, string code, string message, CancellationToken ct)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(JsonSerializer.Serialize(new ErrorBody
        {
            Code = code,
            Message = message,
        }, Json), ct);
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
