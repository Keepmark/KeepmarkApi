using KeepmarkApi.Stores;

namespace KeepmarkApi.Tests;

/// <summary>In-memory fakes with Mongo-equivalent semantics:
/// no upsert (missing → null), Touch only bumps lastAccessed.</summary>
public sealed class FakeBookmarkStore : IBookmarkStore
{
    public readonly Dictionary<string, StoredBookmark> Docs = new();
    public bool ThrowOnCount { get; set; }
    public int CountCalls { get; private set; }
    public readonly List<string> Touched = [];

    private static string Key(byte[] id) => Convert.ToHexString(id);

    /// <summary>Seeds a pre-1.2 document: revision 0, no hash/size/createdAt.</summary>
    public void SeedLegacy(StoredBookmark doc) => Docs[Key(doc.Id)] = doc;

    public Task InsertAsync(StoredBookmark doc, CancellationToken ct)
    {
        Docs[Key(doc.Id)] = doc;
        return Task.CompletedTask;
    }

    public Task<StoredBookmark?> FindByIdAsync(byte[] id, CancellationToken ct)
        => Task.FromResult(Docs.TryGetValue(Key(id), out var d) ? d : null);

    public Task<StoredBookmark?> TouchAsync(byte[] id, DateTime now, CancellationToken ct)
    {
        if (!Docs.TryGetValue(Key(id), out var d))
        {
            return Task.FromResult<StoredBookmark?>(null);
        }

        Touched.Add(Key(id));
        var updated = d with { LastAccessed = now };
        Docs[Key(id)] = updated;
        return Task.FromResult<StoredBookmark?>(updated);
    }

    private static StoredBookmark WithWrite(StoredBookmark d, string bookmarks, string? version, DateTime now)
        => KeepmarkApi.Stores.BookmarkWrites.ApplyWrite(d, bookmarks, version, now);

    public Task<StoredBookmark?> UpdateDataAsync(byte[] id, string bookmarks, DateTime now, CancellationToken ct)
    {
        if (!Docs.TryGetValue(Key(id), out var d))
        {
            return Task.FromResult<StoredBookmark?>(null);
        }

        var updated = WithWrite(d, bookmarks, d.Version, now);
        Docs[Key(id)] = updated;
        return Task.FromResult<StoredBookmark?>(updated);
    }

    public Task<StoredBookmark?> UpdateDataV2Async(byte[] id, string bookmarks, string? version, DateTime now, CancellationToken ct)
    {
        if (!Docs.TryGetValue(Key(id), out var d))
        {
            return Task.FromResult<StoredBookmark?>(null);
        }

        var updated = WithWrite(d, bookmarks, version, now);
        Docs[Key(id)] = updated;
        return Task.FromResult<StoredBookmark?>(updated);
    }

    public Task<(StoredBookmark? Updated, StoredBookmark? Current)> CompareAndSwapAsync(
        byte[] id, string bookmarks, string? version, DateTime baseTime, DateTime now, CancellationToken ct)
    {
        if (!Docs.TryGetValue(Key(id), out var d))
        {
            return Task.FromResult<(StoredBookmark?, StoredBookmark?)>((null, null));
        }

        if (Time.SyncTime.TruncateToMs(d.LastUpdated) != Time.SyncTime.TruncateToMs(baseTime))
        {
            return Task.FromResult<(StoredBookmark?, StoredBookmark?)>((null, d));
        }

        var updated = WithWrite(d, bookmarks, version, now);
        Docs[Key(id)] = updated;
        return Task.FromResult<(StoredBookmark?, StoredBookmark?)>((updated, updated));
    }

    public Task<(StoredBookmark? Updated, StoredBookmark? Current)> CompareAndSwapByRevisionAsync(
        byte[] id, string bookmarks, string? version, long baseRevision, DateTime now, CancellationToken ct)
    {
        if (!Docs.TryGetValue(Key(id), out var d))
        {
            return Task.FromResult<(StoredBookmark?, StoredBookmark?)>((null, null));
        }

        if (d.Revision != baseRevision)
        {
            return Task.FromResult<(StoredBookmark?, StoredBookmark?)>((null, d));
        }

        var updated = WithWrite(d, bookmarks, version, now);
        Docs[Key(id)] = updated;
        return Task.FromResult<(StoredBookmark?, StoredBookmark?)>((updated, updated));
    }

    public Task<bool> DeleteAsync(byte[] id, CancellationToken ct)
        => Task.FromResult(Docs.Remove(Key(id)));

    public Task<long> CountAsync(CancellationToken ct)
    {
        CountCalls++;
        if (ThrowOnCount)
        {
            throw new InvalidOperationException("db down");
        }

        return Task.FromResult((long)Docs.Count);
    }
}

public sealed class FakeSyncLogStore : ISyncLogStore
{
    public readonly List<(string Ip, DateTime ExpiresAt, DateTime Created)> Logs = [];

    public Task AddAsync(string ip, DateTime expiresAt, DateTime created, CancellationToken ct)
    {
        Logs.Add((ip, expiresAt, created));
        return Task.CompletedTask;
    }

    public Task<long> CountByIpAsync(string ip, CancellationToken ct)
        => Task.FromResult((long)Logs.Count(l => l.Ip == ip));
}
