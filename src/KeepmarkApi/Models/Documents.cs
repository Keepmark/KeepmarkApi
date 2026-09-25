using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace KeepmarkApi.Models;

/// <summary>Mirrors api/src/models/bookmarks.model.ts. _id is BSON Binary
/// subtype 4 (UUID) holding the 16 RFC 4122 bytes — never a Guid struct.</summary>
[BsonIgnoreExtraElements]
public sealed class BookmarkDocument
{
    [BsonId]
    public BsonBinaryData Id { get; set; } = null!;

    [BsonElement("bookmarks")]
    public string? Bookmarks { get; set; }

    [BsonElement("lastAccessed")]
    public DateTime LastAccessed { get; set; }

    [BsonElement("lastUpdated")]
    public DateTime LastUpdated { get; set; }

    [BsonElement("version")]
    [BsonIgnoreIfNull]
    public string? Version { get; set; }
}

/// <summary>Mirrors api/src/models/newSyncLogs.model.ts (collection newsynclogs).</summary>
[BsonIgnoreExtraElements]
public sealed class NewSyncLogDocument
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [BsonElement("expiresAt")]
    public DateTime ExpiresAt { get; set; }

    [BsonElement("ipAddress")]
    public string? IpAddress { get; set; }

    [BsonElement("syncCreated")]
    public DateTime SyncCreated { get; set; }
}
