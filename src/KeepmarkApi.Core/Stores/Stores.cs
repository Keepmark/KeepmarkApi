namespace KeepmarkApi.Stores;

/// <summary>Persistence shape mirroring BookmarkDocument (web) without any driver dependency.</summary>
public sealed record StoredBookmark(
    byte[] Id,
    string? Bookmarks,
    DateTime LastAccessed,
    DateTime LastUpdated,
    string? Version,
    long Revision = 0,
    string? Hash = null,
    long Size = 0,
    DateTime? CreatedAt = null,
    DateTime? MetaStamp = null);

/// <summary>Validity of the v1.2 metadata. Every write by this server stamps
/// metaStamp = lastUpdated; a write by another server sharing the database
/// (Node xBrowserSync during a migration or rollback) moves lastUpdated but
/// leaves revision/hash/metaStamp behind. Such syncs — and pre-1.2 ones — are
/// served as legacy (revision 0, no hash) until this server writes them again,
/// so stale hashes never answer 304 and stale revisions never win a CAS.</summary>
public static class BookmarkMeta
{
    public static bool IsCurrent(StoredBookmark d) => d.Revision > 0 && d.MetaStamp == d.LastUpdated;

    public static StoredBookmark Effective(StoredBookmark d)
        => IsCurrent(d) ? d : d with { Revision = 0, Hash = null, Size = 0 };

    /// <summary>Revision CAS predicate (MongoStores mirrors it as a filter):
    /// base 0 matches any legacy sync, base N only a current revision N.</summary>
    public static bool MatchesBaseRevision(StoredBookmark d, long baseRevision)
        => baseRevision == 0 ? !IsCurrent(d) : IsCurrent(d) && d.Revision == baseRevision;

    /// <summary>One-time stamp for syncs written by servers older than
    /// metaStamp: their lastUpdated when the hash still matches the content
    /// (metadata current), else the epoch (content changed elsewhere → legacy).</summary>
    public static DateTime BackfillStamp(StoredBookmark d)
        => d.Hash is not null && d.Hash == Content.BlobMeta.HashOf(d.Bookmarks) ? d.LastUpdated : DateTime.UnixEpoch;
}

/// <summary>Single definition of the write-payload rule (revision bump,
/// refreshed hash/size, version preserved unless replaced). Used by logic
/// and fakes; MongoStores.WriteUpdate is its BSON mirror.</summary>
public static class BookmarkWrites
{
    public static StoredBookmark ApplyWrite(StoredBookmark current, string bookmarks, string? version, DateTime now)
        => current with
        {
            Bookmarks = bookmarks,
            LastAccessed = now,
            LastUpdated = now,
            Version = string.IsNullOrEmpty(version) ? current.Version : version,
            Revision = current.Revision + 1,
            Hash = Content.BlobMeta.HashOf(bookmarks),
            Size = Content.BlobMeta.SizeOf(bookmarks),
            MetaStamp = now,
        };
}

public interface IBookmarkStore
{
    Task InsertAsync(StoredBookmark doc, CancellationToken ct);
    Task<StoredBookmark?> FindByIdAsync(byte[] id, CancellationToken ct);
    /// <summary>Sets lastAccessed=now; returns the document after the touch, null when missing.</summary>
    Task<StoredBookmark?> TouchAsync(byte[] id, DateTime now, CancellationToken ct);
    /// <summary>v1 update (no existence check — mirrors the Node quirk).</summary>
    Task<StoredBookmark?> UpdateDataAsync(byte[] id, string bookmarks, DateTime now, CancellationToken ct);
    Task<StoredBookmark?> UpdateDataV2Async(byte[] id, string bookmarks, string? version, DateTime now, CancellationToken ct);
    /// <summary>Atomic compare-and-swap: updates only when lastUpdated still
    /// equals the base. Returns (updated, current): updated set on win (current
    /// repeats it, ignore it); both null when the sync does not exist; on loss
    /// updated is null and current is the pre-write document.</summary>
    Task<(StoredBookmark? Updated, StoredBookmark? Current)> CompareAndSwapAsync(
        byte[] id, string bookmarks, string? version, DateTime baseTime, DateTime now, CancellationToken ct);
    /// <summary>Same, but matching on revision instead of timestamp (v1.2 contract).</summary>
    Task<(StoredBookmark? Updated, StoredBookmark? Current)> CompareAndSwapByRevisionAsync(
        byte[] id, string bookmarks, string? version, long baseRevision, DateTime now, CancellationToken ct);
    Task<bool> DeleteAsync(byte[] id, CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
}

public interface ISyncLogStore
{
    Task AddAsync(string ip, DateTime expiresAt, DateTime created, CancellationToken ct);
    Task<long> CountByIpAsync(string ip, CancellationToken ct);
}
