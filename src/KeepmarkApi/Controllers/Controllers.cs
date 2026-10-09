using System.Text.Json;
using KeepmarkApi.Config;
using KeepmarkApi.Errors;
using KeepmarkApi.Logic;
using KeepmarkApi.Requests;
using KeepmarkApi.Routing;
using KeepmarkApi.Services;
using KeepmarkApi.Shaping;
using KeepmarkApi.Time;
using Microsoft.AspNetCore.Mvc;

namespace KeepmarkApi.Controllers;

internal static class VersionHeader
{
    // Missing header defaults to the last legacy version (Node: package version).
    public static string Resolve(HttpRequest request)
        => ApiVersioning.ResolveHeader(
            request.Headers.TryGetValue("Accept-Version", out var v) ? v.ToString() : null,
            XbsVersions.DefaultVersion);
}

/// <summary>Routes mirror api/src/routers/bookmarks.router.ts.
/// Version dispatch: '~1.0.0' → v1 handlers, '^1.1.3' → v2 handlers.</summary>
[ApiController]
[Route("bookmarks")]
public sealed class BookmarksController(BookmarksLogic logic) : ControllerBase
{
    private string? Ip => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string Accepted => VersionHeader.Resolve(Request);
    private bool Extended => ApiVersioning.IsExtendedContract(Accepted);

    /// <summary>Reads the body like express.json(): non-JSON content types and
    /// empty bodies are {} (→ 400 RequiredData), malformed JSON → 500
    /// Unspecified. The Kestrel body cap still applies while reading (413).</summary>
    private async Task<JsonElement> ReadBodyAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct).ConfigureAwait(false);
        return BookmarkRequests.ParseBody(Request.ContentType, ms.GetBuffer().AsSpan(0, (int)ms.Length));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct).ConfigureAwait(false);
        var route = ApiVersioning.BookmarksRouteVersion(Accepted);
        var now = DateTime.UtcNow;
        if (route == 1)
        {
            var res = await logic.CreateV1Async(BookmarkRequests.ParseCreateV1(body), Ip, now, ct).ConfigureAwait(false);
            return Ok(Extended ? res : ResponseShapes.Strip(res));
        }

        var res2 = await logic.CreateV2Async(BookmarkRequests.ParseCreateV2(body), Ip, now, ct).ConfigureAwait(false);
        return Ok(Extended ? res2 : ResponseShapes.Strip(res2));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        EnsureMajorOne();
        // v1.2 conditional GET: matching content hash → 304, no body.
        // Precheck is read-only (no touch); the full read below touches once.
        if (Extended && Request.Headers.TryGetValue("If-None-Match", out var tag))
        {
            var meta = await logic.GetMetadataAsync(id, ct).ConfigureAwait(false);
            if (meta.Hash is not null && string.Equals(tag.ToString().Trim('"'), meta.Hash, StringComparison.OrdinalIgnoreCase))
            {
                return StatusCode(304);
            }
        }

        var res = await logic.GetAsync(id, DateTime.UtcNow, ct).ConfigureAwait(false);
        return Ok(Extended ? res : ResponseShapes.Strip(res));
    }

    [HttpHead("{id}")]
    public async Task<IActionResult> Head(string id, CancellationToken ct)
    {
        EnsureExtended();
        var meta = await logic.GetMetadataAsync(id, ct).ConfigureAwait(false);
        if (meta.Hash is not null)
        {
            Response.Headers["ETag"] = $"\"{meta.Hash}\"";
        }

        Response.Headers["Last-Modified"] = SyncTime.ToIsoString(meta.LastUpdated);
        Response.Headers["X-Sync-Revision"] = meta.Revision.ToString();
        Response.Headers["X-Sync-Size"] = meta.Size.ToString();
        return Ok();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        EnsureExtended();
        await logic.DeleteAsync(id, ct).ConfigureAwait(false);
        return Ok(new { });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, CancellationToken ct)
    {
        var body = await ReadBodyAsync(ct).ConfigureAwait(false);
        var route = ApiVersioning.BookmarksRouteVersion(Accepted);
        if (route == 1)
        {
            var (bookmarks, _, _) = BookmarkRequests.ParseUpdate(body);
            var res1 = await logic.UpdateV1Async(id, bookmarks, DateTime.UtcNow, ct).ConfigureAwait(false);
            return Ok(Extended ? res1 : ResponseShapes.Strip(res1));
        }

        var (bookmarks2, lastUpdated, version) = BookmarkRequests.ParseUpdate(body);
        long? baseRevision = Extended ? BookmarkRequests.ParseBaseRevision(body) : null;
        var res = await logic.UpdateV2Async(id, bookmarks2, lastUpdated, version, DateTime.UtcNow, baseRevision, ct)
            .ConfigureAwait(false);
        return Ok(Extended ? res : ResponseShapes.Strip(res));
    }

    [HttpGet("{id}/lastUpdated")]
    public async Task<IActionResult> GetLastUpdated(string id, CancellationToken ct)
    {
        EnsureMajorOne();
        return Ok(await logic.GetLastUpdatedAsync(id, DateTime.UtcNow, ct).ConfigureAwait(false));
    }

    [HttpGet("{id}/version")]
    public async Task<IActionResult> GetVersion(string id, CancellationToken ct)
    {
        EnsureMajorOne();
        return Ok(await logic.GetVersionAsync(id, DateTime.UtcNow, ct).ConfigureAwait(false));
    }

    /// <summary>Node's ^ ranges match any 1.x (naive major compare); anything else 412s.</summary>
    private void EnsureMajorOne()
    {
        if (ApiVersioning.Parse(Accepted).Major != 1)
        {
            throw new UnsupportedVersionException();
        }
    }

    /// <summary>Unknown to legacy servers: Node answers its 404 catch-all, not 412.</summary>
    private void EnsureExtended()
    {
        if (!Extended)
        {
            throw new RouteNotImplementedException();
        }
    }
}

/// <summary>Mirrors api/src/routers/info.router.ts: GET /info.
/// v1.2 adds the capabilities advertisement; legacy shape is untouched.</summary>
[ApiController]
[Route("info")]
public sealed class InfoController(InfoLogic logic) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var extended = ApiVersioning.IsExtendedContract(VersionHeader.Resolve(Request));
        var res = await logic.GetInfoAsync(ct, extended).ConfigureAwait(false);
        return Ok(res);
    }
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
        if (!string.IsNullOrEmpty(env.WebRootPath))
        {
            var path = Path.Combine(env.WebRootPath, "favicon.png");
            if (System.IO.File.Exists(path))
            {
                return PhysicalFile(path, "image/png");
            }
        }

        return NoContent();
    }

    private const string LandingPage = """
        <!DOCTYPE html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Keepmark API</title>
        <link rel="icon" href="favicon.ico">
        <style>
          body { font-family: system-ui, sans-serif; color: #e8eaed; margin: 0; min-height: 100vh;
                 background: radial-gradient(ellipse at top, #2c4596 0%, #1a2a5e 45%, #0e1630 100%);
                 display: flex; justify-content: center; padding: 4rem 1rem; box-sizing: border-box; }
          main { max-width: 34rem; text-align: center; }
          .logo { width: 220px; max-width: 60vw; height: auto; display: block; margin: 0 auto;
                  filter: drop-shadow(0 12px 32px rgba(0, 0, 0, 0.35)); }
          h1 { font-size: 1.15rem; font-weight: 600; letter-spacing: 0.12em; text-transform: uppercase;
               color: #b8c4e6; margin: 1.75rem 0 0.25rem; }
          .version { color: #9aa8cc; margin-bottom: 1.5rem; }
          .status { display: inline-block; padding: 0.3rem 0.9rem; border-radius: 999px;
                    background: rgba(255, 255, 255, 0.12); font-size: 0.9rem; }
          .status.online { background: #1d4620; } .status.warn { background: #5c3d00; }
          .status.offline { background: #5c1a1a; }
          footer { margin-top: 2.5rem; font-size: 0.8rem; color: #9aa8cc; }
          a { color: #a9c4ff; } code { background: rgba(0, 0, 0, 0.28); padding: 0.1rem 0.4rem; border-radius: 4px; }
        </style>
        </head>
        <body>
        <main>
          <img class="logo" src="logo.svg" alt="Keepmark">
          <h1>Sync API</h1>
          <div class="version">v{version} — xBrowserSync-compatible sync service for Keepmark and xBrowserSync clients</div>
          <div><span class="status" id="status">checking…</span></div>
          <p>Service contract (JSON <code>{code,message}</code> on errors):</p>
          <p><code>GET <a href="info">info</a></code> ·
             <code>POST bookmarks</code> ·
             <code>GET|PUT bookmarks/:id</code> ·
             <code>GET bookmarks/:id/lastUpdated</code> ·
             <code>GET bookmarks/:id/version</code></p>
          <p>v1.2 (send <code>Accept-Version: 1.2.0</code>): <code>meta{revision,hash,size,createdAt}</code> on
             responses, <code>PUT</code> with <code>baseRevision</code>,
             <code>HEAD bookmarks/:id</code> + <code>If-None-Match</code> → <code>304</code>,
             <code>DELETE bookmarks/:id</code></p>
          <footer>
            Keepmark Server — independent alternative compatible with the
            <a href="https://github.com/xbrowsersync/api">xBrowserSync API</a> v{version}.
            © 2026 Yari Melzani, free software under the AGPL-3.0
            (<a href="https://github.com/Keepmark">source code</a>). Not affiliated with or endorsed by xBrowserSync.
            Clients never send plaintext: bookmarks stay encrypted end-to-end.<br>
            Service operator? Drop-in replacement, 10-minute migration, zero user impact.
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
