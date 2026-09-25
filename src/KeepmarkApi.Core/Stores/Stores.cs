namespace KeepmarkApi.Stores;

/// <summary>Persistence shape mirroring BookmarkDocument (web) without any driver dependency.</summary>
public sealed record StoredBookmark(byte[] Id, string? Bookmarks, DateTime LastAccessed, DateTime LastUpdated, string? Version);

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
    /// equals the base. Returns (updated, current): updated set on win;
    /// both null when the sync does not exist; current set on conflict.</summary>
    Task<(StoredBookmark? Updated, StoredBookmark? Current)> CompareAndSwapAsync(
        byte[] id, string bookmarks, string? version, DateTime baseTime, DateTime now, CancellationToken ct);
    Task<long> CountAsync(CancellationToken ct);
}

public interface ISyncLogStore
{
    Task AddAsync(string ip, DateTime expiresAt, DateTime created, CancellationToken ct);
    Task<long> CountByIpAsync(string ip, CancellationToken ct);
}
