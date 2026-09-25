using KeepmarkApi.Config;
using KeepmarkApi.Errors;
using KeepmarkApi.Logging;
using KeepmarkApi.Services;
using KeepmarkApi.Stores;
using KeepmarkApi.Text;
using KeepmarkApi.Time;
using KeepmarkApi.Uuid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeepmarkApi.Logic;

/// <summary>Bookmark business rules mirroring api/src/services/bookmarks.service.ts
/// (quirks included). Framework-free: time (now) and client IP are parameters,
/// persistence goes through store interfaces — fully unit-testable.
/// Verbosity: Info = creations + warnings; Debug/Trace = every decision with values.
/// Bodies and full sync IDs are never logged (see LogSanitizer).</summary>
public sealed class BookmarksLogic(
    IBookmarkStore store,
    ISyncLogStore logs,
    XbsSettings settings,
    ILogger<BookmarksLogic>? logger = null)
{
    private readonly ILogger<BookmarksLogic> _log = logger ?? NullLogger<BookmarksLogic>.Instance;

    public void CheckServiceAvailable()
    {
        if (!settings.Status.Online)
        {
            throw new ServiceNotAvailableException();
        }
    }

    public Task<bool> IsAcceptingNewSyncsAsync(CancellationToken ct = default)
        => IsAcceptingNewSyncsAsync(settings, store.CountAsync, _log, ct);

    internal static async Task<bool> IsAcceptingNewSyncsAsync(
        XbsSettings cfg, Func<CancellationToken, Task<long>> count, ILogger? log, CancellationToken ct)
    {
        if (!cfg.Status.AllowNewSyncs)
        {
            log?.LogDebug("AcceptingNewSyncs: no (allowNewSyncs=false)");
            return false;
        }

        if (cfg.MaxSyncs == 0)
        {
            log?.LogDebug("AcceptingNewSyncs: yes (maxSyncs disabled)");
            return true;
        }

        var n = await count(ct).ConfigureAwait(false);
        var ok = n < cfg.MaxSyncs;
        log?.LogDebug("AcceptingNewSyncs: {Ok} (count={Count} maxSyncs={Max})", ok, n, cfg.MaxSyncs);
        return ok;
    }

    private async Task GuardNewSyncAsync(string? ip, CancellationToken ct)
    {
        CheckServiceAvailable();
        if (!await IsAcceptingNewSyncsAsync(ct).ConfigureAwait(false))
        {
            _log.LogInformation("New sync refused: service not accepting new syncs");
            throw new NewSyncsForbiddenException();
        }

        if (settings.DailyNewSyncsLimit > 0)
        {
            var hits = await logs.NewSyncsLimitHitAsync(ip, settings.DailyNewSyncsLimit, ct).ConfigureAwait(false);
            _log.LogDebug("NewSyncsLimit: hit={Hit} ipSet={HasIp} limit={Limit}", hits, !string.IsNullOrEmpty(ip), settings.DailyNewSyncsLimit);
            if (hits)
            {
                _log.LogInformation("New sync refused: daily limit exceeded");
                throw new NewSyncsLimitExceededException();
            }
        }
        else
        {
            _log.LogDebug("NewSyncsLimit: skipped (disabled)");
        }
    }

    private Task LogNewSyncAsync(string? ip, CancellationToken ct)
        => settings.DailyNewSyncsLimit > 0 ? logs.CreateLogAsync(ip, ct) : Task.CompletedTask;

    public async Task<CreateBookmarksResponse> CreateV1Async(string bookmarks, string? ip, DateTime now, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        await GuardNewSyncAsync(ip, ct).ConfigureAwait(false);
        var doc = new StoredBookmark(SyncId.New(), bookmarks, now, now, null);
        await store.InsertAsync(doc, ct).ConfigureAwait(false);
        await LogNewSyncAsync(ip, ct).ConfigureAwait(false);
        _log.LogInformation("New bookmarks sync created id={Id}", LogSanitizer.SyncId(SyncId.ToString(doc.Id)));
        return new CreateBookmarksResponse { Id = SyncId.ToString(doc.Id), LastUpdated = doc.LastUpdated };
    }

    public async Task<CreateBookmarksResponse> CreateV2Async(string version, string? ip, DateTime now, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        await GuardNewSyncAsync(ip, ct).ConfigureAwait(false);
        var doc = new StoredBookmark(SyncId.New(), null, now, now, version);
        await store.InsertAsync(doc, ct).ConfigureAwait(false);
        await LogNewSyncAsync(ip, ct).ConfigureAwait(false);
        _log.LogInformation("New bookmarks sync created id={Id} version={Version}",
            LogSanitizer.SyncId(SyncId.ToString(doc.Id)), version);
        return new CreateBookmarksResponse { Id = SyncId.ToString(doc.Id), LastUpdated = doc.LastUpdated, Version = doc.Version };
    }

    public async Task<GetBookmarksResponse> GetAsync(string id, DateTime now, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var doc = await store.TouchAsync(SyncId.Parse(id), now, ct).ConfigureAwait(false);
        if (doc is null)
        {
            _log.LogDebug("Get: not found id={Id}", LogSanitizer.SyncId(id));
            throw new SyncNotFoundException();
        }

        _log.LogDebug("Get: ok id={Id} bytes={Bytes}", LogSanitizer.SyncId(id), doc.Bookmarks?.Length ?? 0);
        return new GetBookmarksResponse { Bookmarks = doc.Bookmarks, Version = doc.Version, LastUpdated = doc.LastUpdated };
    }

    public async Task<GetLastUpdatedResponse> GetLastUpdatedAsync(string id, DateTime now, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var doc = await store.TouchAsync(SyncId.Parse(id), now, ct).ConfigureAwait(false)
            ?? throw new SyncNotFoundException();
        _log.LogDebug("GetLastUpdated: ok id={Id} lastUpdated={LastUpdated}",
            LogSanitizer.SyncId(id), SyncTime.ToIsoString(doc.LastUpdated));
        return new GetLastUpdatedResponse { LastUpdated = doc.LastUpdated };
    }

    public async Task<GetVersionResponse> GetVersionAsync(string id, DateTime now, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var doc = await store.TouchAsync(SyncId.Parse(id), now, ct).ConfigureAwait(false)
            ?? throw new SyncNotFoundException();
        _log.LogDebug("GetVersion: ok id={Id} version={Version}", LogSanitizer.SyncId(id), doc.Version);
        return new GetVersionResponse { Version = doc.Version };
    }

    public async Task<UpdateBookmarksResponse> UpdateV1Async(string id, string bookmarks, DateTime now, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        CheckServiceAvailable();
        // Node quirk: missing sync → 200 with {} (no lastUpdated), not 401.
        var doc = await store.UpdateDataAsync(SyncId.Parse(id), bookmarks, now, ct).ConfigureAwait(false);
        if (doc is null)
        {
            _log.LogDebug("UpdateV1: no document id={Id} (returning empty, Node quirk)", LogSanitizer.SyncId(id));
        }
        else
        {
            _log.LogDebug("UpdateV1: ok id={Id} bytes={Bytes}", LogSanitizer.SyncId(id), bookmarks.Length);
        }

        return new UpdateBookmarksResponse { LastUpdated = doc?.LastUpdated };
    }

    public async Task<UpdateBookmarksResponse> UpdateV2Async(
        string id, string bookmarks, string? lastUpdated, string? version, DateTime now, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        CheckServiceAvailable();
        var idBytes = SyncId.Parse(id);

        // No base supplied → legacy path (check existence, then update).
        if (string.IsNullOrEmpty(lastUpdated))
        {
            var found = await store.FindByIdAsync(idBytes, ct).ConfigureAwait(false);
            if (found is null)
            {
                _log.LogDebug("UpdateV2: not found id={Id}", LogSanitizer.SyncId(id));
                throw new SyncNotFoundException();
            }

            _log.LogDebug("UpdateV2: no base, updating id={Id}", LogSanitizer.SyncId(id));
            var doc = await store.UpdateDataV2Async(idBytes, bookmarks, version, now, ct).ConfigureAwait(false);
            return new UpdateBookmarksResponse { LastUpdated = doc!.LastUpdated };
        }

        if (!SyncTime.TryParseInstant(lastUpdated, out var baseTime))
        {
            _log.LogInformation("UpdateV2: unparseable base id={Id} base={Base}", LogSanitizer.SyncId(id), lastUpdated);
            throw new SyncConflictException();
        }

        // Atomic compare-and-swap: only one concurrent writer with the same
        // base can win; the loser gets 409 instead of silently overwriting.
        var (updated, current) = await store.CompareAndSwapAsync(idBytes, bookmarks, version, baseTime, now, ct).ConfigureAwait(false);
        if (updated is not null)
        {
            _log.LogDebug("UpdateV2: CAS won id={Id}", LogSanitizer.SyncId(id));
            return new UpdateBookmarksResponse { LastUpdated = updated.LastUpdated };
        }

        if (current is null)
        {
            _log.LogDebug("UpdateV2: not found id={Id}", LogSanitizer.SyncId(id));
            throw new SyncNotFoundException();
        }

        _log.LogInformation("UpdateV2: conflict id={Id} base={Base} current={Current}",
            LogSanitizer.SyncId(id), lastUpdated, SyncTime.ToIsoString(current.LastUpdated));
        throw new SyncConflictException();
    }
}

/// <summary>Abuse-tracking rules mirroring newSyncLogs.service.ts.
/// expiresAt = start of tomorrow (server local), like moment().add(1,'days').startOf('day').</summary>
public static class SyncLogRules
{
    public static bool IsLimitHit(long countForIp, int dailyLimit) => countForIp >= dailyLimit;

    public static DateTime ExpiresAt() => DateTime.Today.AddDays(1);
}

/// <summary>Extension over ISyncLogStore expressing the Node service defaults
/// (missing IP → no tracking, returns false like the Node null return).</summary>
public static class SyncLogStoreExtensions
{
    public static async Task<bool> NewSyncsLimitHitAsync(
        this ISyncLogStore logs, string? ip, int dailyLimit, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return false;
        }

        var count = await logs.CountByIpAsync(ip, ct).ConfigureAwait(false);
        return SyncLogRules.IsLimitHit(count, dailyLimit);
    }

    public static Task CreateLogAsync(this ISyncLogStore logs, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        return logs.AddAsync(ip, SyncLogRules.ExpiresAt(), now, ct);
    }
}

/// <summary>Service-info rules mirroring api/src/services/info.service.ts.</summary>
public sealed class InfoLogic(BookmarksLogic bookmarks, XbsSettings settings, ILogger<InfoLogic>? logger = null)
{
    private readonly ILogger<InfoLogic> _log = logger ?? NullLogger<InfoLogic>.Instance;

    public async Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default)
    {
        var info = new ServiceInfoResponse
        {
            Location = string.IsNullOrEmpty(settings.Location) ? null : settings.Location.ToUpperInvariant(),
            MaxSyncSize = settings.MaxSyncSize,
            Message = HtmlSanitizer.StripScripts(settings.Status.Message),
            Status = (int)ServiceStatus.Offline,
            Version = XbsVersions.ApiVersion,
        };

        if (settings.Status.Online)
        {
            try
            {
                var accepting = await bookmarks.IsAcceptingNewSyncsAsync(ct).ConfigureAwait(false);
                info.Status = accepting ? (int)ServiceStatus.Online : (int)ServiceStatus.NoNewSyncs;
                _log.LogDebug("GetInfo: status={Status}", info.Status);
            }
            catch (Exception ex)
            {
                // Node swallows the error and keeps the offline status; verbose shows why.
                _log.LogDebug(ex, "GetInfo: count failed, reporting offline");
            }
        }
        else
        {
            _log.LogDebug("GetInfo: status=offline (configured)");
        }

        return info;
    }
}
