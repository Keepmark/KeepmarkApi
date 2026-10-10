using KeepmarkApi.Config;
using KeepmarkApi.Logic;
using KeepmarkApi.Models;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace KeepmarkApi.Data;

/// <summary>Mongo wiring mirroring api/src/db.ts. Credentials fall back to
/// XBROWSERSYNC_DB_USER / XBROWSERSYNC_DB_PWD when settings are empty.
/// Ensures the TTL + ipAddress indexes from api/README.md at startup, plus the
/// optional lastAccessed TTL driven by SyncExpiryDays.</summary>
public sealed class MongoContext
{
    private readonly IMongoDatabase _db;

    public MongoContext(XbsSettings settings)
    {
        var user = string.IsNullOrEmpty(settings.Db.Username)
            ? Environment.GetEnvironmentVariable("XBROWSERSYNC_DB_USER") ?? string.Empty
            : settings.Db.Username;
        var pwd = string.IsNullOrEmpty(settings.Db.Password)
            ? Environment.GetEnvironmentVariable("XBROWSERSYNC_DB_PWD") ?? string.Empty
            : settings.Db.Password;
        var creds = !string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pwd)
            ? $"{Uri.EscapeDataString(user)}:{Uri.EscapeDataString(pwd)}@"
            : string.Empty;

        string url = settings.Db.UseSrv
            ? $"mongodb+srv://{creds}{settings.Db.Host}/{settings.Db.Name}"
            : $"mongodb://{creds}{settings.Db.Host}:{settings.Db.Port}/{settings.Db.Name}";
        if (!string.IsNullOrEmpty(settings.Db.AuthSource))
        {
            url += $"?authSource={settings.Db.AuthSource}";
        }

        var clientSettings = MongoClientSettings.FromConnectionString(url);
        clientSettings.ConnectTimeout = TimeSpan.FromMilliseconds(settings.Db.ConnTimeout);
        if (settings.Db.UseSrv || settings.Db.Ssl)
        {
            clientSettings.UseTls = true;
        }

        var client = new MongoClient(clientSettings);
        _db = client.GetDatabase(settings.Db.Name);
    }

    public IMongoCollection<BookmarkDocument> Bookmarks => _db.GetCollection<BookmarkDocument>("bookmarks");
    public IMongoCollection<NewSyncLogDocument> NewSyncLogs => _db.GetCollection<NewSyncLogDocument>("newsynclogs");

    public async Task EnsureIndexesAsync(int syncExpiryDays, ILogger log, CancellationToken ct = default)
    {
        await EnsureExpiryIndexAsync(syncExpiryDays, log, ct).ConfigureAwait(false);
        await NewSyncLogs.Indexes.CreateOneAsync(
            new CreateIndexModel<NewSyncLogDocument>(
                Builders<NewSyncLogDocument>.IndexKeys.Ascending(x => x.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            cancellationToken: ct).ConfigureAwait(false);
        await NewSyncLogs.Indexes.CreateOneAsync(
            new CreateIndexModel<NewSyncLogDocument>(
                Builders<NewSyncLogDocument>.IndexKeys.Ascending(x => x.IpAddress)),
            cancellationToken: ct).ConfigureAwait(false);
        // Per-IP daily counting is a CountDocuments per create; the compound
        // index keeps it an index-only scan as the collection turns over daily.
        await NewSyncLogs.Indexes.CreateOneAsync(
            new CreateIndexModel<NewSyncLogDocument>(
                Builders<NewSyncLogDocument>.IndexKeys
                    .Ascending(x => x.IpAddress)
                    .Ascending(x => x.ExpiresAt)),
            cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Converges the bookmarks.lastAccessed TTL to SyncExpiryDays
    /// (decision: SyncExpiryRules). Adopts the manual Node index too. Changes
    /// are drop + create, not collMod: the least-privilege readWrite user
    /// cannot collMod.</summary>
    private async Task EnsureExpiryIndexAsync(int days, ILogger log, CancellationToken ct)
    {
        var cursor = await Bookmarks.Indexes.ListAsync(ct).ConfigureAwait(false);
        var existing = (await cursor.ToListAsync(ct).ConfigureAwait(false)).FirstOrDefault(IsLastAccessedIndex);
        long? ttl = existing is not null && existing.TryGetValue("expireAfterSeconds", out var e) && e.IsNumeric
            ? e.ToInt64()
            : null;

        var action = SyncExpiryRules.Decide(days, existing is not null, ttl);
        if (action is ExpiryIndexAction.Drop or ExpiryIndexAction.Recreate)
        {
            await Bookmarks.Indexes.DropOneAsync(existing!["name"].AsString, ct).ConfigureAwait(false);
        }

        if (action is ExpiryIndexAction.Create or ExpiryIndexAction.Recreate)
        {
            await Bookmarks.Indexes.CreateOneAsync(
                new CreateIndexModel<BookmarkDocument>(
                    Builders<BookmarkDocument>.IndexKeys.Ascending(x => x.LastAccessed),
                    new CreateIndexOptions { ExpireAfter = TimeSpan.FromDays(days) }),
                cancellationToken: ct).ConfigureAwait(false);
        }

        if (action == ExpiryIndexAction.Drop)
        {
            log.LogWarning("Sync expiry disabled: dropped existing lastAccessed TTL index ({Seconds}s)", ttl);
        }
        else if (days > 0)
        {
            log.LogInformation("Sync expiry: syncs not accessed for {Days} days are deleted (index {Action})",
                days, action);
        }
    }

    private static bool IsLastAccessedIndex(BsonDocument index)
        => index.TryGetValue("key", out var key)
            && key is BsonDocument k
            && SyncExpiryRules.IsLastAccessedKey(k.Elements
                .Select(e => new KeyValuePair<string, double?>(e.Name, e.Value.IsNumeric ? e.Value.ToDouble() : null))
                .ToList());
}
