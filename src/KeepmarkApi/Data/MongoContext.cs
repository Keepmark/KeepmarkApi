using KeepmarkApi.Config;
using KeepmarkApi.Models;
using MongoDB.Driver;

namespace KeepmarkApi.Data;

/// <summary>Mongo wiring mirroring api/src/db.ts. Credentials fall back to
/// XBROWSERSYNC_DB_USER / XBROWSERSYNC_DB_PWD when settings are empty.
/// Ensures the TTL + ipAddress indexes from api/README.md at startup.</summary>
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

    public async Task EnsureIndexesAsync(CancellationToken ct = default)
    {
        await NewSyncLogs.Indexes.CreateOneAsync(
            new CreateIndexModel<NewSyncLogDocument>(
                Builders<NewSyncLogDocument>.IndexKeys.Ascending(x => x.ExpiresAt),
                new CreateIndexOptions { ExpireAfter = TimeSpan.Zero }),
            cancellationToken: ct).ConfigureAwait(false);
        await NewSyncLogs.Indexes.CreateOneAsync(
            new CreateIndexModel<NewSyncLogDocument>(
                Builders<NewSyncLogDocument>.IndexKeys.Ascending(x => x.IpAddress)),
            cancellationToken: ct).ConfigureAwait(false);
    }
}
