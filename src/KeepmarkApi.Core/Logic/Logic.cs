using KeepmarkApi.Config;
using KeepmarkApi.Content;
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

    /// <summary>Cheap store round trip for /info: with MaxSyncs = 0 the
    /// acceptance check never touches the DB, so a dead DB would still report
    /// "online" without this probe.</summary>
    public Task<long> ProbeStoreAsync(CancellationToken ct = default) => store.CountAsync(ct);

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
                _log.LogInformation("New sync refused: daily new-sync limit exceeded (ip={Ip} limit={Limit}/day)",
                    ip ?? "(unknown)", settings.DailyNewSyncsLimit);
                throw new NewSyncsLimitExceededException();
            }
        }
        else
        {
            _log.LogDebug("NewSyncsLimit: skipped (disabled)");
        }
    }

    private Task LogNewSyncAsync(string? ip, DateTime now, CancellationToken ct)
        => settings.DailyNewSyncsLimit > 0 ? logs.CreateLogAsync(ip, now, ct) : Task.CompletedTask;

    public async Task<CreateBookmarksResponse2> CreateV1Async(string bookmarks, string? ip, DateTime now, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        await GuardNewSyncAsync(ip, ct).ConfigureAwait(false);
        var doc = new StoredBookmark(SyncId.New(), bookmarks, now, now, null,
            Revision: 1, Hash: BlobMeta.HashOf(bookmarks), Size: BlobMeta.SizeOf(bookmarks), CreatedAt: now);
        await store.InsertAsync(doc, ct).ConfigureAwait(false);
        await LogNewSyncAsync(ip, now, ct).ConfigureAwait(false);
        _log.LogInformation("New bookmarks sync created id={Id}", LogSanitizer.SyncId(SyncId.ToString(doc.Id)));
        var res = ExtendedResponses.Create(doc, SyncId.ToString(doc.Id));
        return res;
    }

    public async Task<CreateBookmarksResponse2> CreateV2Async(string version, string? ip, DateTime now, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        await GuardNewSyncAsync(ip, ct).ConfigureAwait(false);
        var doc = new StoredBookmark(SyncId.New(), null, now, now, version,
            Revision: 1, Hash: BlobMeta.HashOf(null), Size: 0, CreatedAt: now);
        await store.InsertAsync(doc, ct).ConfigureAwait(false);
        await LogNewSyncAsync(ip, now, ct).ConfigureAwait(false);
        _log.LogInformation("New bookmarks sync created id={Id} version={Version}",
            LogSanitizer.SyncId(SyncId.ToString(doc.Id)), version);
        return ExtendedResponses.Create(doc, SyncId.ToString(doc.Id));
    }

    public async Task<GetBookmarksResponse2> GetAsync(string id, DateTime now, CancellationToken ct = default)
    {
        // Node validates the ID in the router before checking availability.
        var idBytes = SyncId.Parse(id);
        CheckServiceAvailable();
        var doc = await store.TouchAsync(idBytes, now, ct).ConfigureAwait(false);
        if (doc is null)
        {
            _log.LogDebug("Get: not found id={Id}", LogSanitizer.SyncId(id));
            throw new SyncNotFoundException();
        }

        _log.LogDebug("Get: ok id={Id} bytes={Bytes}", LogSanitizer.SyncId(id), doc.Bookmarks?.Length ?? 0);
        return ExtendedResponses.Get(doc);
    }

    /// <summary>Metadata for HEAD / conditional GET. Read-only: no lastAccessed
    /// touch, so pure metadata polling neither extends TTL nor costs a write.</summary>
    public async Task<StoredBookmark> GetMetadataAsync(string id, CancellationToken ct = default)
    {
        var idBytes = SyncId.Parse(id);
        CheckServiceAvailable();
        return await store.FindByIdAsync(idBytes, ct).ConfigureAwait(false)
            ?? throw new SyncNotFoundException();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        var idBytes = SyncId.Parse(id);
        CheckServiceAvailable();
        if (!await store.DeleteAsync(idBytes, ct).ConfigureAwait(false))
        {
            _log.LogDebug("Delete: not found id={Id}", LogSanitizer.SyncId(id));
            throw new SyncNotFoundException();
        }

        _log.LogInformation("Sync deleted id={Id}", LogSanitizer.SyncId(id));
    }

    public async Task<GetLastUpdatedResponse> GetLastUpdatedAsync(string id, DateTime now, CancellationToken ct = default)
    {
        var idBytes = SyncId.Parse(id);
        CheckServiceAvailable();
        var doc = await store.TouchAsync(idBytes, now, ct).ConfigureAwait(false)
            ?? throw new SyncNotFoundException();
        _log.LogDebug("GetLastUpdated: ok id={Id} lastUpdated={LastUpdated}",
            LogSanitizer.SyncId(id), SyncTime.ToIsoString(doc.LastUpdated));
        return new GetLastUpdatedResponse { LastUpdated = doc.LastUpdated };
    }

    public async Task<GetVersionResponse> GetVersionAsync(string id, DateTime now, CancellationToken ct = default)
    {
        var idBytes = SyncId.Parse(id);
        CheckServiceAvailable();
        var doc = await store.TouchAsync(idBytes, now, ct).ConfigureAwait(false)
            ?? throw new SyncNotFoundException();
        _log.LogDebug("GetVersion: ok id={Id} version={Version}", LogSanitizer.SyncId(id), doc.Version);
        return new GetVersionResponse { Version = doc.Version };
    }

    public async Task<UpdateBookmarksResponse2> UpdateV1Async(string id, string bookmarks, DateTime now, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        var idBytes = SyncId.Parse(id);
        CheckServiceAvailable();
        // Node quirk: missing sync → 200 with {} (no lastUpdated), not 401.
        var doc = await store.UpdateDataAsync(idBytes, bookmarks, now, ct).ConfigureAwait(false);
        if (doc is null)
        {
            _log.LogDebug("UpdateV1: no document id={Id} (returning empty, Node quirk)", LogSanitizer.SyncId(id));
            return new UpdateBookmarksResponse2();
        }

        _log.LogDebug("UpdateV1: ok id={Id} bytes={Bytes}", LogSanitizer.SyncId(id), bookmarks.Length);
        return ExtendedResponses.Updated(doc);
    }

    public Task<UpdateBookmarksResponse2> UpdateV2Async(
        string id, string bookmarks, string? lastUpdated, string? version, DateTime now,
        long? baseRevision = null, CancellationToken ct = default)
    {
        now = SyncTime.TruncateToMs(now);
        var idBytes = SyncId.Parse(id);
        CheckServiceAvailable();

        // Revision base (v1.2) wins over timestamp base when both are sent.
        if (baseRevision is not null)
        {
            return UpdateV2ByRevisionAsync(id, idBytes, bookmarks, version, baseRevision.Value, now, ct);
        }

        // No base at all → legacy path (check existence, then update).
        if (string.IsNullOrEmpty(lastUpdated))
        {
            return UpdateV2NoBaseAsync(id, idBytes, bookmarks, version, now, ct);
        }

        return UpdateV2ByTimestampAsync(id, idBytes, bookmarks, lastUpdated, version, now, ct);
    }

    private async Task<UpdateBookmarksResponse2> UpdateV2NoBaseAsync(
        string id, byte[] idBytes, string bookmarks, string? version, DateTime now, CancellationToken ct)
    {
        var found = await store.FindByIdAsync(idBytes, ct).ConfigureAwait(false);
        if (found is null)
        {
            _log.LogDebug("UpdateV2: not found id={Id}", LogSanitizer.SyncId(id));
            throw new SyncNotFoundException();
        }

        _log.LogDebug("UpdateV2: no base, updating id={Id}", LogSanitizer.SyncId(id));
        var doc = await store.UpdateDataV2Async(idBytes, bookmarks, version, now, ct).ConfigureAwait(false);
        if (doc is null)
        {
            _log.LogDebug("UpdateV2: lost race on no-base update id={Id}", LogSanitizer.SyncId(id));
            throw new SyncNotFoundException();
        }

        return ExtendedResponses.Updated(doc);
    }

    private async Task<UpdateBookmarksResponse2> UpdateV2ByRevisionAsync(
        string id, byte[] idBytes, string bookmarks, string? version, long baseRevision, DateTime now, CancellationToken ct)
    {
        var (revUpdated, revCurrent) = await store.CompareAndSwapByRevisionAsync(
            idBytes, bookmarks, version, baseRevision, now, ct).ConfigureAwait(false);
        if (revUpdated is not null)
        {
            _log.LogDebug("UpdateV2: revision CAS won id={Id} rev={Rev}",
                LogSanitizer.SyncId(id), revUpdated.Revision);
            return ExtendedResponses.Updated(revUpdated);
        }

        if (revCurrent is null)
        {
            _log.LogDebug("UpdateV2: not found id={Id}", LogSanitizer.SyncId(id));
            throw new SyncNotFoundException();
        }

        _log.LogInformation("UpdateV2: revision conflict id={Id} baseRev={Base} currentRev={Current}",
            LogSanitizer.SyncId(id), baseRevision, revCurrent.Revision);
        throw new SyncConflictException();
    }

    private async Task<UpdateBookmarksResponse2> UpdateV2ByTimestampAsync(
        string id, byte[] idBytes, string bookmarks, string lastUpdated, string? version, DateTime now, CancellationToken ct)
    {
        // Timestamp base (v1.1): parse, then atomic CAS on lastUpdated.
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
            return ExtendedResponses.Updated(updated);
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
/// expiresAt = start of tomorrow (UTC day boundary).</summary>
public static class SyncLogRules
{
    public static bool IsLimitHit(long countForIp, int dailyLimit) => countForIp >= dailyLimit;

    public static DateTime ExpiresAt(DateTime now) => now.ToUniversalTime().Date.AddDays(1);
}

/// <summary>What startup does to the bookmarks.lastAccessed TTL index.</summary>
public enum ExpiryIndexAction
{
    None,
    Create,
    Recreate,
    Drop,
}

/// <summary>Inactive-sync expiry (SyncExpiryDays). Node leaves it to a manual
/// TTL index (api/README.md suggests 3 weeks); here the setting owns the index:
/// every startup converges it to the configured value, 0 removes it. Pure
/// decision — MongoContext applies it.</summary>
public static class SyncExpiryRules
{
    /// <summary>Mongo caps expireAfterSeconds at int32 max (~68 years).</summary>
    public const int MaxDays = int.MaxValue / 86400;

    public static bool IsValidDays(int days) => days is >= 0 and <= MaxDays;

    public static long? TtlSeconds(int days) => days > 0 ? days * 86400L : null;

    /// <summary>True when an index key is exactly <c>{lastAccessed: 1}</c>.
    /// Directions arrive as int or double (mongosh vs legacy shell); non-numeric
    /// ones ("hashed", "text") are passed as null. Compound keys never match.</summary>
    public static bool IsLastAccessedKey(IReadOnlyList<KeyValuePair<string, double?>> key)
        => key.Count == 1 && key[0].Key == "lastAccessed" && key[0].Value == 1;

    /// <param name="exists">An index keyed exactly <c>{lastAccessed: 1}</c> exists.</param>
    /// <param name="currentTtlSeconds">Its expireAfterSeconds; null when it is not a TTL index.</param>
    public static ExpiryIndexAction Decide(int days, bool exists, long? currentTtlSeconds)
    {
        var wanted = TtlSeconds(days);
        if (wanted is null)
        {
            // Disabled: remove a TTL (it would keep deleting), leave a plain index alone.
            return exists && currentTtlSeconds is not null ? ExpiryIndexAction.Drop : ExpiryIndexAction.None;
        }

        if (!exists)
        {
            return ExpiryIndexAction.Create;
        }

        return currentTtlSeconds == wanted ? ExpiryIndexAction.None : ExpiryIndexAction.Recreate;
    }
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

    public static Task CreateLogAsync(this ISyncLogStore logs, string? ip, DateTime now, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ip))
        {
            return Task.CompletedTask;
        }

        return logs.AddAsync(ip, SyncLogRules.ExpiresAt(now), now, ct);
    }
}

/// <summary>Service-info rules mirroring api/src/services/info.service.ts.
/// extended=true adds the v1.2 capabilities advertisement.</summary>
public sealed class InfoLogic(BookmarksLogic bookmarks, XbsSettings settings, ILogger<InfoLogic>? logger = null)
{
    private readonly ILogger<InfoLogic> _log = logger ?? NullLogger<InfoLogic>.Instance;

    public async Task<ServiceInfoResponse> GetInfoAsync(CancellationToken ct = default, bool extended = false)
    {
        var info = new ServiceInfoResponse
        {
            Location = string.IsNullOrEmpty(settings.Location) ? string.Empty : settings.Location.ToUpperInvariant(),
            MaxSyncSize = settings.MaxSyncSize,
            Message = HtmlSanitizer.StripScripts(settings.Status.Message),
            Status = (int)ServiceStatus.Offline,
            Version = XbsVersions.ApiVersion,
            Capabilities = extended ? new CapabilitiesResponse() : null,
        };

        if (settings.Status.Online)
        {
            try
            {
                if (settings.MaxSyncs == 0)
                {
                    await bookmarks.ProbeStoreAsync(ct).ConfigureAwait(false);
                }

                var accepting = await bookmarks.IsAcceptingNewSyncsAsync(ct).ConfigureAwait(false);
                info.Status = accepting ? (int)ServiceStatus.Online : (int)ServiceStatus.NoNewSyncs;
                _log.LogDebug("GetInfo: status={Status}", info.Status);
            }
            catch (Exception ex)
            {
                // Node swallows the error and keeps the offline status, but a
                // failing count means the DB is likely down — operators must see it.
                _log.LogWarning(ex, "GetInfo: count failed, reporting offline");
            }
        }
        else
        {
            _log.LogDebug("GetInfo: status=offline (configured)");
        }

        return info;
    }
}
