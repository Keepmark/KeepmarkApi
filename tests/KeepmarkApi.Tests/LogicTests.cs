using KeepmarkApi.Config;
using KeepmarkApi.Errors;
using KeepmarkApi.Logic;
using KeepmarkApi.Stores;
using KeepmarkApi.Time;
using KeepmarkApi.Uuid;

namespace KeepmarkApi.Tests;

public sealed class LogicTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Earlier = new(2026, 9, 20, 8, 30, 0, DateTimeKind.Utc);

    private static XbsSettings Settings(Action<XbsSettings>? tweak = null)
    {
        var s = new XbsSettings();
        tweak?.Invoke(s);
        return s;
    }

    private static (BookmarksLogic Logic, FakeBookmarkStore Store, FakeSyncLogStore Logs) Make(XbsSettings s)
    {
        var store = new FakeBookmarkStore();
        var logs = new FakeSyncLogStore();
        return (new BookmarksLogic(store, logs, s), store, logs);
    }

    private static string SeedLegacy(FakeBookmarkStore store, string? bookmarks = "blob", string? version = "1.1.13")
    {
        var id = SyncId.New();
        store.SeedLegacy(new KeepmarkApi.Stores.StoredBookmark(id, bookmarks, Earlier, Earlier, version));
        return SyncId.ToString(id);
    }

    // ---- create ----

    [Fact]
    public async Task CreateV1_ReturnsIdAndTimestamps()
    {
        var (logic, store, _) = Make(Settings());
        var res = await logic.CreateV1Async("data", "1.2.3.4", Now);
        Assert.NotNull(res.Id);
        Assert.Equal(32, res.Id!.Length);
        Assert.Equal(Now, res.LastUpdated);
        Assert.Null(res.Version);
        var saved = await store.FindByIdAsync(SyncId.Parse(res.Id), default);
        Assert.NotNull(saved);
        Assert.Equal("data", saved!.Bookmarks);
        Assert.Equal(Now, saved.LastAccessed);
    }

    [Fact]
    public async Task CreateV2_ReturnsVersion()
    {
        var (logic, _, _) = Make(Settings());
        var res = await logic.CreateV2Async("1.8.0", "1.2.3.4", Now);
        Assert.Equal("1.8.0", res.Version);
        Assert.NotNull(res.Id);
    }

    [Fact]
    public async Task Create_WritesSyncLogWhenLimitEnabled()
    {
        var (logic, _, logs) = Make(Settings());
        await logic.CreateV1Async("d", "9.9.9.9", Now);
        Assert.Single(logs.Logs);
        Assert.Equal("9.9.9.9", logs.Logs[0].Ip);
        Assert.Equal(Now.Date.AddDays(1), logs.Logs[0].ExpiresAt);
    }

    [Fact]
    public async Task Create_SkipsLogWhenLimitDisabled()
    {
        var (logic, _, logs) = Make(Settings(s => s.DailyNewSyncsLimit = 0));
        await logic.CreateV1Async("d", "9.9.9.9", Now);
        Assert.Empty(logs.Logs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Create_OfflineThrows(bool v2)
    {
        var (logic, _, _) = Make(Settings(s => s.Status.Online = false));
        await Assert.ThrowsAsync<ServiceNotAvailableException>(() =>
            v2 ? logic.CreateV2Async("1.8.0", null, Now) : logic.CreateV1Async("d", null, Now));
    }

    [Fact]
    public async Task Create_ForbiddenWhenNotAcceptingNewSyncs()
    {
        var (logic, _, _) = Make(Settings(s => s.Status.AllowNewSyncs = false));
        await Assert.ThrowsAsync<NewSyncsForbiddenException>(() => logic.CreateV1Async("d", null, Now));
    }

    [Fact]
    public async Task Create_ForbiddenWhenMaxSyncsReached()
    {
        var s = Settings(s => s.MaxSyncs = 1);
        var (logic, store, _) = Make(s);
        SeedLegacy(store);
        await Assert.ThrowsAsync<NewSyncsForbiddenException>(() => logic.CreateV1Async("d", null, Now));
    }

    [Fact]
    public async Task Create_AllowedBelowMaxSyncs()
    {
        var (logic, _, _) = Make(Settings(s => s.MaxSyncs = 5));
        var res = await logic.CreateV1Async("d", null, Now);
        Assert.NotNull(res.Id);
    }

    [Fact]
    public async Task Create_MaxSyncsZeroSkipsCount()
    {
        var (logic, store, _) = Make(Settings(s => { s.MaxSyncs = 0; s.DailyNewSyncsLimit = 0; }));
        store.ThrowOnCount = true; // would explode if queried
        var res = await logic.CreateV1Async("d", null, Now);
        Assert.NotNull(res.Id);
        Assert.Equal(0, store.CountCalls);
    }

    [Fact]
    public async Task Create_LimitExceededWhenThresholdReached()
    {
        var s = Settings(s => s.DailyNewSyncsLimit = 1);
        var (logic, _, logs) = Make(s);
        await logs.AddAsync("5.5.5.5", DateTime.Today.AddDays(1), Now, default);
        await Assert.ThrowsAsync<NewSyncsLimitExceededException>(() => logic.CreateV1Async("d", "5.5.5.5", Now));
    }

    [Fact]
    public async Task Create_MissingIpSkipsLimitCheck()
    {
        var (logic, _, logs) = Make(Settings());
        var res = await logic.CreateV1Async("d", null, Now);
        Assert.NotNull(res.Id);
        Assert.Empty(logs.Logs);
    }

    // ---- get ----

    [Fact]
    public async Task Get_ReturnsBlobAndTouchesAccessed()
    {
        var (logic, store, _) = Make(Settings());
        var id = SeedLegacy(store);
        var res = await logic.GetAsync(id, Now);
        Assert.Equal("blob", res.Bookmarks);
        Assert.Equal("1.1.13", res.Version);
        Assert.Equal(Earlier, res.LastUpdated); // touch bumps accessed, not updated
        var saved = await store.FindByIdAsync(SyncId.Parse(id), default);
        Assert.Equal(Now, saved!.LastAccessed);
        Assert.Single(store.Touched);
    }

    [Fact]
    public async Task Get_MissingThrowsNotFound()
    {
        var (logic, _, _) = Make(Settings());
        await Assert.ThrowsAsync<SyncNotFoundException>(() =>
            logic.GetAsync(SyncId.ToString(SyncId.New()), Now));
    }

    [Fact]
    public async Task Get_MalformedIdThrows()
    {
        var (logic, _, _) = Make(Settings());
        await Assert.ThrowsAsync<InvalidSyncIdException>(() => logic.GetAsync("nope", Now));
    }

    [Fact]
    public async Task Get_OfflineThrows()
    {
        var (logic, _, _) = Make(Settings(s => s.Status.Online = false));
        await Assert.ThrowsAsync<ServiceNotAvailableException>(() => logic.GetAsync(SyncId.ToString(SyncId.New()), Now));
    }

    [Fact]
    public async Task GetLastUpdated_AndVersion()
    {
        var (logic, _, _) = Make(Settings());
        var store = new FakeBookmarkStore();
        var id = SeedLegacy(store);
        var logic2 = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        Assert.Equal(Earlier, (await logic2.GetLastUpdatedAsync(id, Now)).LastUpdated);
        Assert.Equal("1.1.13", (await logic2.GetVersionAsync(id, Now)).Version);
        _ = logic;
    }

    [Fact]
    public async Task GetLastUpdated_MissingThrows()
    {
        var (logic, _, _) = Make(Settings());
        await Assert.ThrowsAsync<SyncNotFoundException>(() =>
            logic.GetLastUpdatedAsync(SyncId.ToString(SyncId.New()), Now));
    }

    // ---- update v1 ----

    [Fact]
    public async Task UpdateV1_UpdatesAndReturnsNewDate()
    {
        var (logic, store, _) = Make(Settings());
        var id = SeedLegacy(store);
        var res = await logic.UpdateV1Async(id, "new-blob", Now);
        Assert.Equal(Now, res.LastUpdated);
        Assert.Equal("new-blob", (await store.FindByIdAsync(SyncId.Parse(id), default))!.Bookmarks);
    }

    [Fact]
    public async Task UpdateV1_MissingReturnsNullDate_Quirk()
    {
        var (logic, _, _) = Make(Settings());
        var res = await logic.UpdateV1Async(SyncId.ToString(SyncId.New()), "x", Now);
        Assert.Null(res.LastUpdated); // Node returns 200 {} instead of 401
    }

    // ---- update v2 ----

    [Fact]
    public async Task UpdateV2_MatchingBaseUpdates()
    {
        var (logic, store, _) = Make(Settings());
        var id = SeedLegacy(store);
        var res = await logic.UpdateV2Async(id, "nb", SyncTime.ToIsoString(Earlier), "1.8.0", Now);
        Assert.Equal(Now, res.LastUpdated);
        var saved = await store.FindByIdAsync(SyncId.Parse(id), default);
        Assert.Equal("1.8.0", saved!.Version);
    }

    [Fact]
    public async Task UpdateV2_NullBaseSkipsConflictCheck()
    {
        var (logic, _, _) = Make(Settings());
        var store = new FakeBookmarkStore();
        var id = SeedLegacy(store);
        var logic2 = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var res = await logic2.UpdateV2Async(id, "nb", null, null, Now);
        Assert.Equal(Now, res.LastUpdated);
        _ = logic;
    }

    [Fact]
    public async Task UpdateV2_NullVersionPreservesExisting()
    {
        var store = new FakeBookmarkStore();
        var id = SeedLegacy(store, version: "1.1.13");
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        await logic.UpdateV2Async(id, "nb", SyncTime.ToIsoString(Earlier), null, Now);
        Assert.Equal("1.1.13", (await store.FindByIdAsync(SyncId.Parse(id), default))!.Version);
    }

    [Fact]
    public async Task UpdateV2_StaleBaseThrowsConflict()
    {
        var (logic, _, _) = Make(Settings());
        var store = new FakeBookmarkStore();
        var id = SeedLegacy(store);
        var logic2 = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        await Assert.ThrowsAsync<SyncConflictException>(() =>
            logic2.UpdateV2Async(id, "nb", "1970-01-01T00:00:00.000Z", null, Now));
        _ = logic;
    }

    [Fact]
    public async Task UpdateV2_MissingThrowsNotFound()
    {
        var (logic, _, _) = Make(Settings());
        await Assert.ThrowsAsync<SyncNotFoundException>(() =>
            logic.UpdateV2Async(SyncId.ToString(SyncId.New()), "x", null, null, Now));
    }

    [Fact]
    public async Task UpdateV2_SecondWriterWithSameBaseLoses()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        var res = await logic.UpdateV2Async(id, "first", KeepmarkApi.Time.SyncTime.ToIsoString(Earlier), null, Now);
        Assert.Equal(Now, res.LastUpdated);
        // Same stale base again → conflict, first write intact.
        await Assert.ThrowsAsync<SyncConflictException>(() =>
            logic.UpdateV2Async(id, "second", KeepmarkApi.Time.SyncTime.ToIsoString(Earlier), null, Now.AddSeconds(1)));
        Assert.Equal("first", (await store.FindByIdAsync(SyncId.Parse(id), default))!.Bookmarks);
    }

    [Fact]
    public async Task UpdateV2_UnparseableBaseThrowsConflict()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        await Assert.ThrowsAsync<SyncConflictException>(() =>
            logic.UpdateV2Async(id, "x", "not-a-date", null, Now));
        Assert.Equal("blob", (await store.FindByIdAsync(SyncId.Parse(id), default))!.Bookmarks);
    }

    // ---- info ----

    [Fact]
    public async Task Info_OnlineWhenAccepting()
    {
        var s = Settings(s => { s.Location = "gb"; s.Status.Message = "hi <b>x</b><script>e()</script>"; s.MaxSyncSize = 123; });
        var info = await new InfoLogic(new BookmarksLogic(new FakeBookmarkStore(), new FakeSyncLogStore(), s), s).GetInfoAsync();
        Assert.Equal(1, info.Status);
        Assert.Equal("GB", info.Location);
        Assert.Equal("hi <b>x</b>", info.Message);
        Assert.Equal(123, info.MaxSyncSize);
        Assert.Equal(KeepmarkApi.Config.XbsVersions.ApiVersion, info.Version);
    }

    [Fact]
    public async Task Info_NoNewSyncsWhenFull()
    {
        var s = Settings(s => s.MaxSyncs = 1);
        var store = new FakeBookmarkStore();
        SeedLegacy(store);
        var info = await new InfoLogic(new BookmarksLogic(store, new FakeSyncLogStore(), s), s).GetInfoAsync();
        Assert.Equal(3, info.Status);
    }

    [Fact]
    public async Task Info_OfflineShortCircuits()
    {
        var s = Settings(s => s.Status.Online = false);
        var store = new FakeBookmarkStore { ThrowOnCount = true };
        var info = await new InfoLogic(new BookmarksLogic(store, new FakeSyncLogStore(), s), s).GetInfoAsync();
        Assert.Equal(2, info.Status);
        Assert.Equal(0, store.CountCalls);
    }

    [Fact]
    public async Task Info_DbErrorKeepsOffline()
    {
        var s = Settings();
        var store = new FakeBookmarkStore { ThrowOnCount = true };
        var info = await new InfoLogic(new BookmarksLogic(store, new FakeSyncLogStore(), s), s).GetInfoAsync();
        Assert.Equal(2, info.Status);
    }

    [Fact]
    public async Task Info_MaxSyncsZero_StillDetectsDeadDb()
    {
        var s = Settings(s => s.MaxSyncs = 0);
        var store = new FakeBookmarkStore { ThrowOnCount = true };
        var info = await new InfoLogic(new BookmarksLogic(store, new FakeSyncLogStore(), s), s).GetInfoAsync();
        Assert.Equal(2, info.Status);
    }

    [Fact]
    public async Task Info_MaxSyncsZero_OnlineWhenDbAnswers()
    {
        var s = Settings(s => s.MaxSyncs = 0);
        var info = await new InfoLogic(new BookmarksLogic(new FakeBookmarkStore(), new FakeSyncLogStore(), s), s).GetInfoAsync();
        Assert.Equal(1, info.Status);
    }

    [Fact]
    public async Task Info_ExtendedAddsCapabilities()
    {
        var s = Settings();
        var store = new FakeBookmarkStore();
        var logic = new InfoLogic(new BookmarksLogic(store, new FakeSyncLogStore(), s), s);
        var legacy = await logic.GetInfoAsync(ct: default, extended: false);
        var v12 = await logic.GetInfoAsync(ct: default, extended: true);
        Assert.Null(legacy.Capabilities);
        Assert.NotNull(v12.Capabilities);
        Assert.True(v12.Capabilities!.Revision && v12.Capabilities.ConditionalGet && v12.Capabilities.Delete);
        Assert.Equal(0, v12.Capabilities.SyncExpiryDays); // default: never expires
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    [InlineData(90)]
    public async Task Info_ExtendedAdvertisesSyncExpiry(int days)
    {
        var s = Settings(x => x.SyncExpiryDays = days);
        var logic = new InfoLogic(new BookmarksLogic(new FakeBookmarkStore(), new FakeSyncLogStore(), s), s);
        var v12 = await logic.GetInfoAsync(ct: default, extended: true);
        Assert.Equal(days, v12.Capabilities!.SyncExpiryDays);
        Assert.Contains($"\"syncExpiryDays\":{days}", System.Text.Json.JsonSerializer.Serialize(v12));
        // Legacy shape stays frozen even when expiry is on.
        var legacy = await logic.GetInfoAsync(ct: default, extended: false);
        Assert.DoesNotContain("syncExpiryDays", System.Text.Json.JsonSerializer.Serialize(legacy));
    }

    [Fact]
    public async Task Info_EmptyLocationAndMessage()
    {
        var s = Settings();
        var info = await new InfoLogic(new BookmarksLogic(new FakeBookmarkStore(), new FakeSyncLogStore(), s), s).GetInfoAsync();
        Assert.Equal(string.Empty, info.Location);
        Assert.Equal(string.Empty, info.Message);
    }

    // ---- v1.2 metadata ----

    [Fact]
    public async Task V12_CreateSetsRevisionOneAndHash()
    {
        var (logic, store, _) = Make(Settings());
        var res = await logic.CreateV1Async("hello", null, Now);
        Assert.Equal(1, res.Meta!.Revision);
        Assert.Equal(5, res.Meta.Size);
        Assert.Equal(KeepmarkApi.Content.BlobMeta.HashOf("hello"), res.Meta.Hash);
        Assert.Equal(Now, res.Meta.CreatedAt);
        var saved = await store.FindByIdAsync(SyncId.Parse(res.Id!), default);
        Assert.Equal(1, saved!.Revision);
    }

    [Fact]
    public async Task V12_UpdateBumpsRevisionAndRefreshesHash()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        var before = (await store.FindByIdAsync(SyncId.Parse(id), default))!.Revision;
        var res = await logic.UpdateV2Async(id, "newdata", KeepmarkApi.Time.SyncTime.ToIsoString(Earlier), null, Now);
        Assert.Equal(before + 1, res.Meta!.Revision);
        Assert.Equal(KeepmarkApi.Content.BlobMeta.HashOf("newdata"), res.Meta.Hash);
        Assert.Equal(7, res.Meta.Size);
    }

    [Fact]
    public async Task V12_BaseRevisionWinsAndLoses()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        var rev0 = (await store.FindByIdAsync(SyncId.Parse(id), default))!.Revision;
        var res = await logic.UpdateV2Async(id, "a", null, null, Now, baseRevision: rev0);
        Assert.Equal(rev0 + 1, res.Meta!.Revision);
        await Assert.ThrowsAsync<SyncConflictException>(() =>
            logic.UpdateV2Async(id, "b", null, null, Now.AddSeconds(1), baseRevision: rev0));
        Assert.Equal("a", (await store.FindByIdAsync(SyncId.Parse(id), default))!.Bookmarks);
    }

    [Fact]
    public async Task V12_BaseRevisionBeatsTimestampWhenBothSent()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        var rev0 = (await store.FindByIdAsync(SyncId.Parse(id), default))!.Revision;
        // Stale timestamp but correct revision → revision governs, update wins.
        var res = await logic.UpdateV2Async(id, "a", "1970-01-01T00:00:00.000Z", null, Now, baseRevision: rev0);
        Assert.Equal(Now, res.LastUpdated);
    }

    [Fact]
    public async Task V12_DeleteRemovesSync()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        await logic.DeleteAsync(id);
        Assert.Null(await store.FindByIdAsync(SyncId.Parse(id), default));
        await Assert.ThrowsAsync<SyncNotFoundException>(() => logic.DeleteAsync(id));
        await Assert.ThrowsAsync<InvalidSyncIdException>(() => logic.DeleteAsync("bad"));
    }

    [Fact]
    public async Task V12_MetadataReflectsStore()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        var meta = await logic.GetMetadataAsync(id);
        Assert.Equal("blob", meta.Bookmarks);
        await Assert.ThrowsAsync<SyncNotFoundException>(() =>
            logic.GetMetadataAsync(SyncId.ToString(SyncId.New())));
    }

    [Fact]
    public async Task V12_OldDocsServeZeroRevisionWithoutHash()
    {
        // Syncs written by pre-1.2 servers have no metadata; served as-is until next write.
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store); // Seed uses record defaults: revision 0, no hash
        var res = await logic.GetAsync(id, Now);
        Assert.Equal(0, res.Meta!.Revision);
        Assert.Null(res.Meta.Hash);
        Assert.Equal(0, res.Meta.Size);
        Assert.Null(res.Meta.CreatedAt);
    }

    [Fact]
    public async Task V12_V1UpdateBumpsRevisionAndHash()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        var id = SeedLegacy(store);
        var before = await store.FindByIdAsync(SyncId.Parse(id), default);
        var res = await logic.UpdateV1Async(id, "v1new", Now);
        Assert.NotNull(res.LastUpdated);
        var after = await store.FindByIdAsync(SyncId.Parse(id), default);
        Assert.Equal(before!.Revision + 1, after!.Revision);
        Assert.Equal(KeepmarkApi.Content.BlobMeta.HashOf("v1new"), after.Hash);
    }

    [Fact]
    public async Task V12_TouchKeepsRevisionAndCreatedAt()
    {
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings());
        // Seed a v1.2-era doc (createdAt set at creation, like CreateV1Async does).
        var idBytes = SyncId.New();
        var id = SyncId.ToString(idBytes);
        store.SeedLegacy(new KeepmarkApi.Stores.StoredBookmark(idBytes, "blob", Earlier, Earlier, "1.1.13",
            Revision: 3, Hash: "h", Size: 4, CreatedAt: Earlier));
        await logic.UpdateV2Async(id, "x", null, null, Now); // rev 3→4, createdAt untouched
        var t1 = Now.AddMinutes(5);
        await logic.GetAsync(id, t1); // touch only
        var doc = await store.FindByIdAsync(SyncId.Parse(id), default);
        Assert.Equal(4, doc!.Revision);
        Assert.Equal(Earlier, doc.CreatedAt);
        Assert.Equal(t1, doc.LastAccessed);
    }

    [Fact]
    public async Task V12_BaseRevisionOnMissingThrowsNotFound()
    {
        var (logic, _, _) = Make(Settings());
        await Assert.ThrowsAsync<SyncNotFoundException>(() =>
            logic.UpdateV2Async(SyncId.ToString(SyncId.New()), "x", null, null, Now, baseRevision: 1));
    }

    // ---- sync expiry (lastAccessed TTL) ----

    [Theory]
    [InlineData(0, false, null, ExpiryIndexAction.None)]
    [InlineData(0, true, null, ExpiryIndexAction.None)] // plain index: not ours
    [InlineData(0, true, 1814400L, ExpiryIndexAction.Drop)] // manual Node 21-day TTL
    [InlineData(21, false, null, ExpiryIndexAction.Create)]
    [InlineData(21, true, 1814400L, ExpiryIndexAction.None)] // adopts the Node index
    [InlineData(30, true, 1814400L, ExpiryIndexAction.Recreate)]
    [InlineData(21, true, null, ExpiryIndexAction.Recreate)] // plain index becomes TTL
    [InlineData(21, true, 1814399L, ExpiryIndexAction.Recreate)] // off by one second
    [InlineData(1, false, null, ExpiryIndexAction.Create)]
    [InlineData(1, true, 86400L, ExpiryIndexAction.None)]
    [InlineData(0, true, 0L, ExpiryIndexAction.Drop)] // expire-immediately TTL is still a TTL
    public void SyncExpiry_DecidesIndexAction(int days, bool exists, long? ttl, ExpiryIndexAction expected)
        => Assert.Equal(expected, SyncExpiryRules.Decide(days, exists, ttl));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(21, true)]
    [InlineData(SyncExpiryRules.MaxDays, true)]
    [InlineData(SyncExpiryRules.MaxDays + 1, false)]
    public void SyncExpiry_ValidatesDays(int days, bool valid)
        => Assert.Equal(valid, SyncExpiryRules.IsValidDays(days));

    [Fact]
    public void SyncExpiry_TtlSeconds()
    {
        Assert.Null(SyncExpiryRules.TtlSeconds(0));
        Assert.Null(SyncExpiryRules.TtlSeconds(-5));
        Assert.Equal(86400L, SyncExpiryRules.TtlSeconds(1));
        Assert.Equal(1814400L, SyncExpiryRules.TtlSeconds(21)); // api/README.md: 21*86400
        // MaxDays still fits Mongo's int32 expireAfterSeconds.
        Assert.True(SyncExpiryRules.TtlSeconds(SyncExpiryRules.MaxDays) <= int.MaxValue);
    }

    [Fact]
    public void SyncExpiry_DisabledByDefault()
        => Assert.Equal(0, new XbsSettings().SyncExpiryDays);

    public static TheoryData<KeyValuePair<string, double?>[], bool> ExpiryKeys() => new()
    {
        { [new("lastAccessed", 1)], true },
        { [new("lastAccessed", 1.0)], true }, // legacy shell stores doubles
        { [new("lastAccessed", -1)], false },
        { [new("lastAccessed", null)], false }, // "hashed" / "text"
        { [new("lastUpdated", 1)], false },
        { [new("lastAccessed", 1), new("_id", 1)], false }, // compound
        { [], false },
    };

    [Theory]
    [MemberData(nameof(ExpiryKeys))]
    public void SyncExpiry_MatchesOnlyTheLastAccessedKey(KeyValuePair<string, double?>[] key, bool expected)
        => Assert.Equal(expected, SyncExpiryRules.IsLastAccessedKey(key));

    [Fact]
    public async Task SyncExpiry_MetadataReadsNeverTouchEvenWhenEnabled()
    {
        // REQ-H1: HEAD / 304 stay read-only; only full reads refresh lastAccessed.
        var store = new FakeBookmarkStore();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), Settings(s => s.SyncExpiryDays = 21));
        var id = SeedLegacy(store);
        await logic.GetMetadataAsync(id);
        Assert.Empty(store.Touched);
        Assert.Equal(Earlier, (await store.FindByIdAsync(SyncId.Parse(id), default))!.LastAccessed);

        await logic.GetLastUpdatedAsync(id, Now);
        Assert.Equal(Now, (await store.FindByIdAsync(SyncId.Parse(id), default))!.LastAccessed);
    }

    // ---- shared database with another server (migration / rollback) ----

    private static readonly DateTime Later = new(2026, 9, 26, 9, 0, 0, DateTimeKind.Utc);

    private static StoredBookmark Doc(long rev, string? hash, DateTime? stamp, string blob = "blob")
        => new(SyncId.New(), blob, Earlier, Earlier, "1.8.0", rev, hash, 4, Earlier, stamp);

    public static TheoryData<string, StoredBookmark, bool> MetaCases() => new()
    {
        { "node sync (no revision)", Doc(0, null, null), false },
        { "written here", Doc(3, "h", Earlier), true },
        { "written elsewhere after", Doc(3, "h", Earlier) with { LastUpdated = Later }, false },
        { "pre-stamp sync", Doc(3, "h", null), false },
    };

    [Theory]
    [MemberData(nameof(MetaCases))]
    public void SharedDb_MetaIsCurrentOnlyWhenStampMatches(string label, StoredBookmark doc, bool current)
    {
        Assert.True(current == BookmarkMeta.IsCurrent(doc), label);
        var eff = BookmarkMeta.Effective(doc);
        Assert.Equal(current ? doc.Revision : 0, eff.Revision);
        Assert.Equal(current ? doc.Hash : null, eff.Hash);
    }

    [Fact]
    public void SharedDb_BaseRevisionMatching()
    {
        var current = Doc(3, "h", Earlier);
        var foreign = current with { LastUpdated = Later };
        var node = Doc(0, null, null);
        Assert.True(BookmarkMeta.MatchesBaseRevision(current, 3));
        Assert.False(BookmarkMeta.MatchesBaseRevision(current, 0));
        Assert.False(BookmarkMeta.MatchesBaseRevision(current, 2));
        Assert.False(BookmarkMeta.MatchesBaseRevision(foreign, 3)); // stale revision never wins
        Assert.True(BookmarkMeta.MatchesBaseRevision(foreign, 0));
        Assert.True(BookmarkMeta.MatchesBaseRevision(node, 0));
        Assert.False(BookmarkMeta.MatchesBaseRevision(node, 1));
    }

    [Fact]
    public void SharedDb_BackfillStamp()
    {
        var ok = Doc(2, KeepmarkApi.Content.BlobMeta.HashOf("blob"), null);
        Assert.Equal(ok.LastUpdated, BookmarkMeta.BackfillStamp(ok));
        Assert.Equal(DateTime.UnixEpoch, BookmarkMeta.BackfillStamp(ok with { Bookmarks = "changed" }));
        Assert.Equal(DateTime.UnixEpoch, BookmarkMeta.BackfillStamp(Doc(2, null, null)));
        // Empty v2 create: hash of "" matches a null blob.
        Assert.Equal(Earlier, BookmarkMeta.BackfillStamp(Doc(1, KeepmarkApi.Content.BlobMeta.HashOf(null), null, blob: null!)));
    }

    [Fact]
    public async Task SharedDb_NodeSyncAcceptsBaseRevisionZero()
    {
        var (logic, store, _) = Make(Settings());
        var id = SeedLegacy(store);
        var res = await logic.UpdateV2Async(id, "nb", null, null, Now, baseRevision: 0);
        Assert.Equal(1, res.Meta!.Revision);
        Assert.Equal(KeepmarkApi.Content.BlobMeta.HashOf("nb"), res.Meta.Hash);
    }

    [Fact]
    public async Task SharedDb_ForeignWriteServesLegacyMetaAndRejectsStaleRevision()
    {
        var (logic, store, _) = Make(Settings());
        var created = await logic.CreateV1Async("mine", "1.2.3.4", Earlier);
        var key = Convert.ToHexString(SyncId.Parse(created.Id!));
        // Node writes the same sync: content and lastUpdated move, metadata does not.
        store.Docs[key] = store.Docs[key] with { Bookmarks = "node", LastUpdated = Later };

        var got = await logic.GetAsync(created.Id!, Now);
        Assert.Equal("node", got.Bookmarks);
        Assert.Equal(0, got.Meta!.Revision);
        Assert.Null(got.Meta.Hash); // no stale ETag, so no wrong 304
        Assert.Null((await logic.GetMetadataAsync(created.Id!)).Hash);

        await Assert.ThrowsAsync<SyncConflictException>(() =>
            logic.UpdateV2Async(created.Id!, "overwrite", null, null, Now, baseRevision: 1));
        Assert.Equal("node", store.Docs[key].Bookmarks);

        var res = await logic.UpdateV2Async(created.Id!, "merged", null, null, Now, baseRevision: 0);
        Assert.Equal(2, res.Meta!.Revision); // monotonic past the stale value
        Assert.Equal(KeepmarkApi.Content.BlobMeta.HashOf("merged"), res.Meta.Hash);
        Assert.True(BookmarkMeta.IsCurrent(store.Docs[key]));
    }
}
