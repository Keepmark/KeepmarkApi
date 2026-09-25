using System.Text.Json;
using KeepmarkApi.Config;
using KeepmarkApi.Errors;
using KeepmarkApi.Logic;
using KeepmarkApi.Requests;
using KeepmarkApi.Routing;
using KeepmarkApi.Services;
using Microsoft.AspNetCore.Mvc;

namespace KeepmarkApi.Controllers;

internal static class VersionHeader
{
    // Missing header defaults to the server version (server.ts: req.version = header || version).
    public static string Resolve(HttpRequest request)
        => ApiVersioning.ResolveHeader(
            request.Headers.TryGetValue("Accept-Version", out var v) ? v.ToString() : null,
            XbsVersions.ApiVersion);
}

/// <summary>Routes mirror api/src/routers/bookmarks.router.ts.
/// Version dispatch: '~1.0.0' → v1 handlers, '^1.1.3' → v2 handlers.</summary>
[ApiController]
[Route("bookmarks")]
public sealed class BookmarksController(BookmarksLogic logic, ILogger<BookmarksController> log) : ControllerBase
{
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (ApiVersioning.BookmarksRouteVersion(VersionHeader.Resolve(Request)) == 1)
        {
            var res = await logic.CreateV1Async(BookmarkRequests.ParseCreateV1(body), Ip, now, ct).ConfigureAwait(false);
            log.LogInformation("New bookmarks sync created");
            return Ok(res);
        }

        var res2 = await logic.CreateV2Async(BookmarkRequests.ParseCreateV2(body), Ip, now, ct).ConfigureAwait(false);
        log.LogInformation("New bookmarks sync created");
        return Ok(res2);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
        => Ok(await logic.GetAsync(id, DateTime.UtcNow, ct).ConfigureAwait(false));

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (bookmarks, lastUpdated, version) = BookmarkRequests.ParseUpdate(body);
        if (ApiVersioning.BookmarksRouteVersion(VersionHeader.Resolve(Request)) == 1)
        {
            return Ok(await logic.UpdateV1Async(id, bookmarks, DateTime.UtcNow, ct).ConfigureAwait(false));
        }

        return Ok(await logic.UpdateV2Async(id, bookmarks, lastUpdated, version, DateTime.UtcNow, ct).ConfigureAwait(false));
    }

    [HttpGet("{id}/lastUpdated")]
    public async Task<IActionResult> GetLastUpdated(string id, CancellationToken ct)
    {
        EnsureVersionSupported("1.0.0");
        return Ok(await logic.GetLastUpdatedAsync(id, DateTime.UtcNow, ct).ConfigureAwait(false));
    }

    [HttpGet("{id}/version")]
    public async Task<IActionResult> GetVersion(string id, CancellationToken ct)
    {
        EnsureVersionSupported("1.1.3");
        return Ok(await logic.GetVersionAsync(id, DateTime.UtcNow, ct).ConfigureAwait(false));
    }

    private void EnsureVersionSupported(string minimum)
    {
        if (ApiVersioning.Compare(VersionHeader.Resolve(Request), minimum) < 0)
        {
            throw new UnsupportedVersionException();
        }
    }
}

/// <summary>Mirrors api/src/routers/info.router.ts: GET /info.</summary>
[ApiController]
[Route("info")]
public sealed class InfoController(InfoLogic logic) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(await logic.GetInfoAsync(ct).ConfigureAwait(false));
}

/// <summary>Placeholder for the docs homepage (Node serves a webpack site at /).
/// Returns service identity; full docs page is future work.</summary>
[ApiController]
[Route("/")]
public sealed class RootController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { version = XbsVersions.ApiVersion, info = "info" });
}
