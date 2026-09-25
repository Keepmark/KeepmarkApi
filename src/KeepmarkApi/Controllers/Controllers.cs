using System.Text.Json;
using KeepmarkApi.Config;
using KeepmarkApi.Errors;
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
public sealed class BookmarksController(BookmarksService service) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] JsonElement body, CancellationToken ct)
    {
        var version = VersionHeader.Resolve(Request);
        if (ApiVersioning.BookmarksRouteVersion(version) == 1)
        {
            if (!body.TryGetProperty("bookmarks", out var b) || string.IsNullOrEmpty(b.GetString()))
            {
                throw new RequiredDataNotFoundException();
            }

            return Ok(await service.CreateBookmarksV1Async(b.GetString()!, HttpContext, ct).ConfigureAwait(false));
        }

        if (!body.TryGetProperty("version", out var v) || string.IsNullOrEmpty(v.GetString()))
        {
            throw new RequiredDataNotFoundException();
        }

        return Ok(await service.CreateBookmarksV2Async(v.GetString()!, HttpContext, ct).ConfigureAwait(false));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
        => Ok(await service.GetBookmarksAsync(id, HttpContext, ct).ConfigureAwait(false));

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] JsonElement body, CancellationToken ct)
    {
        var version = VersionHeader.Resolve(Request);
        if (!body.TryGetProperty("bookmarks", out var b) || string.IsNullOrEmpty(b.GetString()))
        {
            throw new RequiredDataNotFoundException();
        }

        if (ApiVersioning.BookmarksRouteVersion(version) == 1)
        {
            return Ok(await service.UpdateBookmarksV1Async(id, b.GetString()!, HttpContext, ct).ConfigureAwait(false));
        }

        body.TryGetProperty("lastUpdated", out var lu);
        body.TryGetProperty("version", out var v);
        return Ok(await service.UpdateBookmarksV2Async(
            id, b.GetString()!, lu.GetString(), v.GetString(), HttpContext, ct).ConfigureAwait(false));
    }

    [HttpGet("{id}/lastUpdated")]
    public async Task<IActionResult> GetLastUpdated(string id, CancellationToken ct)
    {
        EnsureVersionSupported("^1.0.0");
        return Ok(await service.GetLastUpdatedAsync(id, HttpContext, ct).ConfigureAwait(false));
    }

    [HttpGet("{id}/version")]
    public async Task<IActionResult> GetVersion(string id, CancellationToken ct)
    {
        EnsureVersionSupported("^1.1.3");
        return Ok(await service.GetVersionAsync(id, HttpContext, ct).ConfigureAwait(false));
    }

    // Minimal gate: version routes exist since ^1.0.0 / ^1.1.3; anything older → 412.
    private void EnsureVersionSupported(string minimum)
    {
        var version = VersionHeader.Resolve(Request);
        var min = minimum.TrimStart('^');
        if (ApiVersioning.Compare(version, min) < 0)
        {
            throw new UnsupportedVersionException();
        }
    }
}

/// <summary>Mirrors api/src/routers/info.router.ts: GET /info.</summary>
[ApiController]
[Route("info")]
public sealed class InfoController(InfoService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
        => Ok(await service.GetInfoAsync(HttpContext, ct).ConfigureAwait(false));
}

/// <summary>Placeholder for the docs homepage (Node serves a webpack site at /).
/// Returns service identity; full docs page is future work.</summary>
[ApiController]
[Route("/")]
public sealed class RootController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { version = Config.XbsVersions.ApiVersion, info = "info" });
}
