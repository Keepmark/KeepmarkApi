using System.Diagnostics;
using KeepmarkApi.Logging;
using KeepmarkApi.Models;
using KeepmarkApi.Stores;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;

namespace KeepmarkApi.Data;

/// <summary>Mongo implementation of the Core store contracts.
/// Debug logs every operation with timing and redacted IDs (verbose mode).</summary>
public sealed class MongoBookmarkStore(MongoContext mongo, ILogger<MongoBookmarkStore>? logger = null) : IBookmarkStore
{
    private readonly ILogger<MongoBookmarkStore> _log = logger ?? NullLogger<MongoBookmarkStore>.Instance;

    private static FilterDefinition<BookmarkDocument> ById(byte[] id)
        => Builders<BookmarkDocument>.Filter.Eq(x => x.Id, new BsonBinaryData(id, BsonBinarySubType.UuidStandard));

    private static StoredBookmark ToRecord(BookmarkDocument d)
        => new(d.Id.Bytes, d.Bookmarks, d.LastAccessed, d.LastUpdated, d.Version,
            d.Revision, d.Hash, d.Size, d.CreatedAt);

    private static BookmarkDocument ToDocument(StoredBookmark doc)
        => new()
        {
            Id = new BsonBinaryData(doc.Id, BsonBinarySubType.UuidStandard),
            Bookmarks = doc.Bookmarks,
            LastAccessed = doc.LastAccessed,
            LastUpdated = doc.LastUpdated,
            Version = doc.Version,
            Revision = doc.Revision,
            Hash = doc.Hash,
            Size = doc.Size,
            CreatedAt = doc.CreatedAt,
        };

    /// <summary>v1.2 write payload: data + revision bump + refreshed hash/size.</summary>
    private static UpdateDefinition<BookmarkDocument> WriteUpdate(string bookmarks, string? version, DateTime now)
    {
        var update = Builders<BookmarkDocument>.Update
            .Set(x => x.Bookmarks, bookmarks)
            .Set(x => x.LastAccessed, now)
            .Set(x => x.LastUpdated, now)
            .Inc(x => x.Revision, 1)
            .Set(x => x.Hash, KeepmarkApi.Content.BlobMeta.HashOf(bookmarks))
            .Set(x => x.Size, KeepmarkApi.Content.BlobMeta.SizeOf(bookmarks));
        if (!string.IsNullOrEmpty(version))
        {
            update = update.Set(x => x.Version, version);
        }

        return update;
    }

    private async Task<T> Timed<T>(string op, string? syncId, Func<Task<T>> run)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await run().ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            _log.LogDebug("mongo bookmarks.{Op} id={Id} {Ms}ms", op, LogSanitizer.SyncId(syncId), sw.ElapsedMilliseconds);
        }
    }

    public Task InsertAsync(StoredBookmark doc, CancellationToken ct)
        => Timed<object?>("insert", Uuid.SyncId.ToString(doc.Id), async () =>
        {
            await mongo.Bookmarks.InsertOneAsync(ToDocument(doc), cancellationToken: ct).ConfigureAwait(false);
            return null;
        });

    public Task<StoredBookmark?> FindByIdAsync(byte[] id, CancellationToken ct)
        => Timed("findById", Uuid.SyncId.ToString(id),
            async () =>
            {
                var doc = await mongo.Bookmarks.Find(ById(id)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return doc is null ? null : ToRecord(doc);
            });

    public Task<StoredBookmark?> TouchAsync(byte[] id, DateTime now, CancellationToken ct)
        => Timed("touch", Uuid.SyncId.ToString(id),
            async () =>
            {
                var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
                    ById(id),
                    Builders<BookmarkDocument>.Update.Set(x => x.LastAccessed, now),
                    new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
                    ct).ConfigureAwait(false);
                return doc is null ? null : ToRecord(doc);
            });

    public Task<StoredBookmark?> UpdateDataAsync(byte[] id, string bookmarks, DateTime now, CancellationToken ct)
        => Timed("updateV1", Uuid.SyncId.ToString(id),
            async () =>
            {
                var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
                    ById(id), WriteUpdate(bookmarks, null, now),
                    new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
                    ct).ConfigureAwait(false);
                return doc is null ? null : ToRecord(doc);
            });

    public Task<StoredBookmark?> UpdateDataV2Async(byte[] id, string bookmarks, string? version, DateTime now, CancellationToken ct)
        => Timed("updateV2", Uuid.SyncId.ToString(id),
            async () =>
            {
                var doc = await mongo.Bookmarks.FindOneAndUpdateAsync(
                    ById(id), WriteUpdate(bookmarks, version, now),
                    new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
                    ct).ConfigureAwait(false);
                return doc is null ? null : ToRecord(doc);
            });

    private async Task<(StoredBookmark? Updated, StoredBookmark? Current)> CasAsync(
        FilterDefinition<BookmarkDocument> match, byte[] id, string bookmarks, string? version, DateTime now,
        string op, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // Single atomic operation: matched only while the base still holds.
            var won = await mongo.Bookmarks.FindOneAndUpdateAsync(
                match, WriteUpdate(bookmarks, version, now),
                new FindOneAndUpdateOptions<BookmarkDocument> { ReturnDocument = ReturnDocument.After },
                ct).ConfigureAwait(false);
            if (won is not null)
            {
                return (ToRecord(won), ToRecord(won));
            }

            // Lost or missing: one read to tell conflict apart from not-found.
            var current = await mongo.Bookmarks.Find(ById(id)).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return (null, current is null ? null : ToRecord(current));
        }
        finally
        {
            sw.Stop();
            _log.LogDebug("mongo bookmarks.{Op} {Ms}ms", op, sw.ElapsedMilliseconds);
        }
    }

    public Task<(StoredBookmark? Updated, StoredBookmark? Current)> CompareAndSwapAsync(
        byte[] id, string bookmarks, string? version, DateTime baseTime, DateTime now, CancellationToken ct)
        => CasAsync(
            Builders<BookmarkDocument>.Filter.And(
                ById(id), Builders<BookmarkDocument>.Filter.Eq(x => x.LastUpdated, baseTime)),
            id, bookmarks, version, now, "cas", ct);

    public Task<(StoredBookmark? Updated, StoredBookmark? Current)> CompareAndSwapByRevisionAsync(
        byte[] id, string bookmarks, string? version, long baseRevision, DateTime now, CancellationToken ct)
        => CasAsync(
            Builders<BookmarkDocument>.Filter.And(
                ById(id), Builders<BookmarkDocument>.Filter.Eq(x => x.Revision, baseRevision)),
            id, bookmarks, version, now, "casRev", ct);

    public async Task<bool> DeleteAsync(byte[] id, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var res = await mongo.Bookmarks.DeleteOneAsync(ById(id), ct).ConfigureAwait(false);
            return res.DeletedCount > 0;
        }
        finally
        {
            sw.Stop();
            _log.LogDebug("mongo bookmarks.delete {Ms}ms", sw.ElapsedMilliseconds);
        }
    }

    public async Task<long> CountAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await mongo.Bookmarks.EstimatedDocumentCountAsync(cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            _log.LogDebug("mongo bookmarks.count {Ms}ms", sw.ElapsedMilliseconds);
        }
    }
}

public sealed class MongoSyncLogStore(MongoContext mongo, ILogger<MongoSyncLogStore>? logger = null) : ISyncLogStore
{
    private readonly ILogger<MongoSyncLogStore> _log = logger ?? NullLogger<MongoSyncLogStore>.Instance;

    public async Task AddAsync(string ip, DateTime expiresAt, DateTime created, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await mongo.NewSyncLogs.InsertOneAsync(new NewSyncLogDocument
            {
                ExpiresAt = expiresAt,
                IpAddress = ip,
                SyncCreated = created,
            }, cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            _log.LogDebug("mongo newsynclogs.insert {Ms}ms", sw.ElapsedMilliseconds);
        }
    }

    public async Task<long> CountByIpAsync(string ip, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await mongo.NewSyncLogs.CountDocumentsAsync(x => x.IpAddress == ip, cancellationToken: ct).ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            _log.LogDebug("mongo newsynclogs.countByIp {Ms}ms", sw.ElapsedMilliseconds);
        }
    }
}
