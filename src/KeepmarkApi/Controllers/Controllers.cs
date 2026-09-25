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

/// <summary>Landing page: logo, live status from /info, credits.
/// Exact path "/" only — unknown paths still return the JSON 404 contract.</summary>
[ApiController]
[Route("/")]
public sealed class RootController(IWebHostEnvironment env) : ControllerBase
{
    [HttpGet]
    public ContentResult Get()
    {
        var html = LandingPage.Replace("{version}", XbsVersions.ApiVersion, StringComparison.Ordinal);
        return Content(html, "text/html");
    }

    [HttpGet("favicon.ico")]
    public IActionResult Favicon()
    {
        var path = Path.Combine(env.WebRootPath, "favicon.png");
        return System.IO.File.Exists(path) ? PhysicalFile(path, "image/png") : NoContent();
    }

    private const string LandingPage = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Keepmark API</title>
        <style>
          body { font-family: system-ui, sans-serif; background: #14171c; color: #e8eaed;
                 display: flex; justify-content: center; padding: 4rem 1rem; margin: 0; }
          main { max-width: 34rem; text-align: center; }
          img { width: 96px; height: auto; }
          h1 { font-size: 1.6rem; margin: 1rem 0 0.25rem; }
          .version { color: #9aa0a6; margin-bottom: 1.5rem; }
          .status { display: inline-block; padding: 0.3rem 0.9rem; border-radius: 999px;
                    background: #333; font-size: 0.9rem; }
          .status.online { background: #1d4620; } .status.warn { background: #5c3d00; }
          .status.offline { background: #5c1a1a; }
          footer { margin-top: 2.5rem; font-size: 0.8rem; color: #9aa0a6; }
          a { color: #8ab4f8; } code { background: #24282f; padding: 0.1rem 0.4rem; border-radius: 4px; }
        </style>
        </head>
        <body>
        <main>
          <img src="logo.png" alt="Keepmark logo">
          <h1>Keepmark API</h1>
          <div class="version">v{version} — drop-in service for Keepmark clients</div>
          <div><span class="status" id="status">checking…</span></div>
          <p>Service contract: <code>GET /info</code> · <code>/bookmarks</code> (JSON <code>{code,message}</code> on errors)</p>
          <footer>
            Keepmark API (.NET 10) — independent, interoperable reimplementation of the
            <a href="https://github.com/xbrowsersync/api">xBrowserSync API</a> v{version}
            (GPL-3.0). Not affiliated with or endorsed by xBrowserSync.
            Clients never send plaintext: bookmarks stay encrypted end-to-end.
          </footer>
        </main>
        <script>
          fetch('info').then(function (r) { return r.json(); }).then(function (i) {
            var el = document.getElementById('status');
            var map = { 1: ['Online', 'online'], 3: ['Not accepting new syncs', 'warn'] };
            var s = map[i.status] || ['Offline', 'offline'];
            el.textContent = s[0]; el.className = 'status ' + s[1];
          }).catch(function () {
            var el = document.getElementById('status');
            el.textContent = 'Offline'; el.className = 'status offline';
          });
        </script>
        </body>
        </html>
        """;
}
