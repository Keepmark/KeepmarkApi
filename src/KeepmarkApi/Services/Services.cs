using KeepmarkApi.Config;
using KeepmarkApi.Data;
using KeepmarkApi.Errors;
using KeepmarkApi.Logging;
using KeepmarkApi.Models;
using KeepmarkApi.Text;
using KeepmarkApi.Uuid;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;

namespace KeepmarkApi.Services;

/// <summary>Client IP extraction mirroring newSyncLogs.service.ts getClientIpAddress.
/// With ForwardedHeaders middleware enabled (behindProxy), RemoteIpAddress
/// already reflects X-Forwarded-For.</summary>
public sealed class NewSyncLogsService(MongoContext mongo, IOptions<XbsSettings> options, ILogger<NewSyncLogsService> log)
{
    public string? GetClientIpAddress(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString();

    public async Task CreateLogAsync(HttpContext ctx, CancellationToken ct = default)
    {
        var ip = GetClientIpAddress(ctx);
        if (string.IsNullOrEmpty(ip))
        {
            log.LogInformation("Unable to determine client IP address");
            return;
        }

        await mongo.NewSyncLogs.InsertOneAsync(new NewSyncLogDocument
        {
            ExpiresAt = DateTime.Today.AddDays(1), // mirrors moment().add(1,'days').startOf('day')
            IpAddress = ip,
            SyncCreated = DateTime.UtcNow,
        }, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<bool> NewSyncsLimitHitAsync(HttpContext ctx, CancellationToken ct = default)
    {
        var ip = GetClientIpAddress(ctx);
        if (string.IsNullOrEmpty(ip))
        {
            log.LogInformation("Unable to determine client IP address");
            return false;
        }

        var count = await mongo.NewSyncLogs
            .CountDocumentsAsync(x => x.IpAddress == ip, cancellationToken: ct)
            .ConfigureAwait(false);
        return count >= options.Value.DailyNewSyncsLimit;
    }
}

/// <summary>Business logic mirroring api/src/services/bookmarks.service.ts,
/// including its quirks (v1 update returns {} instead of 401 when missing).</summary>
public sealed class BookmarksService(
    MongoContext mongo,
    NewSyncLogsService syncLogs,
    IOptions<XbsSettings> options,
    ILogger<BookmarksService> log)
{
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void CheckServiceAvailable()
    {
        if (!options.Value.Status.Online)
        {
            throw new ServiceNotAvailableException();
        }
    }

    public async Task<bool> IsAcceptingNewSyncsAsync(CancellationToken ct = default)
    {
        if (!options.Value.Status.AllowNewSyncs)
        {
            return false;
        }

        if (options.Value.MaxSyncs == 0)
        {
            return true;
        }

        var count = await mongo.Bookmarks.EstimatedDocumentCountAsync(cancellationToken: ct).ConfigureAwait(false);
        return count < options.Value.MaxSyncs;
    }

    private async Task GuardNewSyncAsync(HttpContext ctx, CancellationToken ct)
    {
        CheckServiceAvailable();
        if (!await IsAcceptingNewSyncsAsync(ct).ConfigureAwait(false))
        {
            throw new NewSyncsForbiddenException();
        }

        if (options.Value.DailyNewSyncsLimit > 0
            && await syncLogs.NewSyncsLimitHitAsync(ctx, ct).ConfigureAwait(false))
        {
            throw new NewSyncsLimitExceededException();
        }
    }

    private async Task LogNewSyncAsync(HttpContext ctx, CancellationToken ct)
    {
        if (options.Value.DailyNewSyncsLimit > 0)
        {
            await syncLogs.CreateLogAsync(ctx, ct).ConfigureAwait(false);
        }

        log.LogInformation("New bookmarks sync created {Path}", LogSanitizer.Path(ctx.Request.Path.ToString()));
    }

    public async Task<CreateBookmarksResponse> CreateBookmarksV1Async(string bookmarksData, HttpContext ctx, CancellationToken ct = default)
    {
        await GuardNewSyncAsync(ctx, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var doc = new BookmarkDocument
        {
            Id = new BsonBinaryData(SyncId.New(), BsonBinarySubType.UuidStandard),
            Bookmarks = bookmarksData,
            LastAccessed = now,
            LastUpdated = now,
        };
        await mongo.Bookmarks.InsertOneAsync(doc, cancellationToken: ct).ConfigureAwait(false);
        await LogNewSyncAsync(ctx, ct).ConfigureAwait(false);
        return new CreateBookmarksResponse { Id = SyncId.ToString(doc.Id.Bytes), LastUpdated = doc.LastUpdated };
    }

    public async Task<CreateBookmarksResponse> CreateBookmarksV2Async(string syncVersion, HttpContext ctx, CancellationToken ct = default)
    {
        await GuardNewSyncAsync(ctx, ct).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var doc = new BookmarkDocument
        {
            Id = new BsonBinaryData(SyncId.New(), BsonBinarySubType.UuidStandard),
            Version = syncVersion,
            LastAccessed = now,
            LastUpdated = now,
        };
        await mongo.Bookmarks.InsertOneAsync(doc, cancellationToken: ct).ConfigureAwait(false);
        await LogNewSyncAsync(ctx, ct).ConfigureAwait(false);
        return new CreateBookmarksResponse { Id = SyncId.ToString(doc.Id.Bytes), LastUpdated = doc.LastUpdated, Version = doc.Version };
    }

    private static FilterDefinition<BookmarkDocument> ById(byte[] idBytes)
        => Builders<BookmarkDocument>.Filter.Eq(x => x.Id, new BsonBinaryData(idBytes, BsonBinarySubType.UuidStandard));

    private static UpdateDefinition<BookmarkDocument> TouchAccessed()
        => Builders<BookmarkDocument>.Update.Set(x => x.LastAccessed, DateTime.UtcNow);

    public async Task<GetBookmarksResponse> GetBookmarksAsync(string id, HttpContext ctx, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var idBytes = SyncId.Parse(id);
        var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
            ById(idBytes), TouchAccessed(),
            new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
            ct).ConfigureAwait(false);
        if (doc is null)
        {
            throw new SyncNotFoundException();
        }

        return new GetBookmarksResponse { Bookmarks = doc.Bookmarks, Version = doc.Version, LastUpdated = doc.LastUpdated };
    }

    public async Task<GetLastUpdatedResponse> GetLastUpdatedAsync(string id, HttpContext ctx, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var idBytes = SyncId.Parse(id);
        var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
            ById(idBytes), TouchAccessed(),
            new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
            ct).ConfigureAwait(false);
        if (doc is null)
        {
            throw new SyncNotFoundException();
        }

        return new GetLastUpdatedResponse { LastUpdated = doc.LastUpdated };
    }

    public async Task<GetVersionResponse> GetVersionAsync(string id, HttpContext ctx, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var idBytes = SyncId.Parse(id);
        var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
            ById(idBytes), TouchAccessed(),
            new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
            ct).ConfigureAwait(false);
        if (doc is null)
        {
            throw new SyncNotFoundException();
        }

        return new GetVersionResponse { Version = doc.Version };
    }

    public async Task<UpdateBookmarksResponse> UpdateBookmarksV1Async(string id, string bookmarksData, HttpContext ctx, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var idBytes = SyncId.Parse(id);
        var now = DateTime.UtcNow;
        var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
            ById(idBytes),
            Builders<BookmarkDocument>.Update
                .Set(x => x.Bookmarks, bookmarksData)
                .Set(x => x.LastAccessed, now)
                .Set(x => x.LastUpdated, now),
            new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
            ct).ConfigureAwait(false);
        // Node quirk: missing sync → 200 with {} (no lastUpdated), not 401.
        return new UpdateBookmarksResponse { LastUpdated = doc?.LastUpdated };
    }

    public async Task<UpdateBookmarksResponse> UpdateBookmarksV2Async(
        string id, string bookmarksData, string? lastUpdated, string? syncVersion, HttpContext ctx, CancellationToken ct = default)
    {
        CheckServiceAvailable();
        var idBytes = SyncId.Parse(id);
        var existing = await mongo.Bookmarks.Find(ById(idBytes)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (existing is null)
        {
            throw new SyncNotFoundException();
        }

        // Node compares the raw string against toISOString(); reproduce that format.
        if (!string.IsNullOrEmpty(lastUpdated)
            && !string.Equals(lastUpdated, ToIsoString(existing.LastUpdated), StringComparison.Ordinal))
        {
            throw new SyncConflictException();
        }

        var now = DateTime.UtcNow;
        var update = Builders<BookmarkDocument>.Update
            .Set(x => x.Bookmarks, bookmarksData)
            .Set(x => x.LastAccessed, now)
            .Set(x => x.LastUpdated, now);
        if (!string.IsNullOrEmpty(syncVersion))
        {
            update = update.Set(x => x.Version, syncVersion);
        }

        var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
            ById(idBytes), update,
            new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
            ct).ConfigureAwait(false);
        return new UpdateBookmarksResponse { LastUpdated = doc!.LastUpdated };
    }

    internal static string ToIsoString(DateTime dt)
        => dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")
            : dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}

/// <summary>Mirrors api/src/services/info.service.ts.</summary>
public sealed class InfoService(
    BookmarksService bookmarks,
    IOptions<XbsSettings> options,
    ILogger<InfoService> log)
{
    public async Task<ServiceInfoResponse> GetInfoAsync(HttpContext ctx, CancellationToken ct = default)
    {
        var cfg = options.Value;
        var info = new ServiceInfoResponse
        {
            Location = string.IsNullOrEmpty(cfg.Location) ? null : cfg.Location.ToUpperInvariant(),
            MaxSyncSize = cfg.MaxSyncSize,
            Message = HtmlSanitizer.StripScripts(cfg.Status.Message),
            Status = (int)ServiceStatus.Offline,
            Version = XbsVersions.ApiVersion,
        };

        if (cfg.Status.Online)
        {
            try
            {
                info.Status = await bookmarks.IsAcceptingNewSyncsAsync(ct).ConfigureAwait(false)
                    ? (int)ServiceStatus.Online
                    : (int)ServiceStatus.NoNewSyncs;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Exception occurred in InfoService.getInfo");
            }
        }

        return info;
    }
}
