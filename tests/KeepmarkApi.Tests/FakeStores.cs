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

    public void Seed(StoredBookmark doc) => Docs[Key(doc.Id)] = doc;

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

    public Task<StoredBookmark?> UpdateDataAsync(byte[] id, string bookmarks, DateTime now, CancellationToken ct)
    {
        if (!Docs.TryGetValue(Key(id), out var d))
        {
            return Task.FromResult<StoredBookmark?>(null);
        }

        var updated = d with { Bookmarks = bookmarks, LastAccessed = now, LastUpdated = now };
        Docs[Key(id)] = updated;
        return Task.FromResult<StoredBookmark?>(updated);
    }

    public Task<StoredBookmark?> UpdateDataV2Async(byte[] id, string bookmarks, string? version, DateTime now, CancellationToken ct)
    {
        if (!Docs.TryGetValue(Key(id), out var d))
        {
            return Task.FromResult<StoredBookmark?>(null);
        }

        var updated = d with
        {
            Bookmarks = bookmarks,
            LastAccessed = now,
            LastUpdated = now,
            Version = string.IsNullOrEmpty(version) ? d.Version : version,
        };
        Docs[Key(id)] = updated;
        return Task.FromResult<StoredBookmark?>(updated);
    }

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
