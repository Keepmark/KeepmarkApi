using System.Text.Json;
using KeepmarkApi.Config;
using KeepmarkApi.Errors;
using KeepmarkApi.Logic;
using KeepmarkApi.Logging;
using KeepmarkApi.Requests;
using KeepmarkApi.Time;
using KeepmarkApi.Uuid;
using Microsoft.Extensions.Logging;

namespace KeepmarkApi.Tests;

public sealed class MappingTests
{
    private static JsonElement Body(string json)
    {
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---- levels ----

    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("fatal", LogLevel.Critical)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData(" verbose ", LogLevel.Information)]
    [InlineData(null, LogLevel.Information)]
    [InlineData("", LogLevel.Information)]
    public void ParseLevel_MapsAllNames(string? input, LogLevel expected)
    {
        Assert.Equal(expected, XbsLogLevels.Parse(input));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "trace")]
    [InlineData(LogLevel.Debug, "debug")]
    [InlineData(LogLevel.Information, "info")]
    [InlineData(LogLevel.Warning, "warn")]
    [InlineData(LogLevel.Error, "error")]
    [InlineData(LogLevel.Critical, "fatal")]
    public void LevelName_RoundTrips(LogLevel level, string expected)
    {
        Assert.Equal(expected, XbsLogLevels.Name(level));
    }

    // ---- error map ----

    public static TheoryData<Exception, int, string> ErrorCases() => new()
    {
        { new InvalidSyncIdException(), 401, nameof(InvalidSyncIdException) },
        { new SyncNotFoundException(), 401, nameof(SyncNotFoundException) },
        { new RequiredDataNotFoundException(), 400, nameof(RequiredDataNotFoundException) },
        { new NewSyncsForbiddenException(), 405, nameof(NewSyncsForbiddenException) },
        { new NewSyncsLimitExceededException(), 406, nameof(NewSyncsLimitExceededException) },
        { new RouteNotImplementedException(), 404, "NotImplementedException" }, // wire name matches Node
        { new SyncConflictException(), 409, nameof(SyncConflictException) },
        { new UnsupportedVersionException(), 412, nameof(UnsupportedVersionException) },
        { new SyncDataLimitExceededException(), 413, nameof(SyncDataLimitExceededException) },
        { new RequestThrottledException(), 429, nameof(RequestThrottledException) },
        { new OriginNotPermittedException(), 500, nameof(OriginNotPermittedException) },
        { new UnspecifiedException(), 500, nameof(UnspecifiedException) },
        { new ServiceNotAvailableException(), 503, nameof(ServiceNotAvailableException) },
    };

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public void ErrorMap_PreservesCodeAndStatus(Exception ex, int status, string code)
    {
        var (s, c, m) = ErrorMap.Map(ex);
        Assert.Equal(status, s);
        Assert.Equal(code, c);
        Assert.False(string.IsNullOrEmpty(m));
    }

    [Fact]
    public void ErrorMap_GenericBecomesUnspecified500()
    {
        var (s, c, m) = ErrorMap.Map(new InvalidOperationException("boom"));
        Assert.Equal(500, s);
        Assert.Equal(nameof(UnspecifiedException), c);
        Assert.False(string.IsNullOrEmpty(m));
    }

    [Fact]
    public void ErrorMap_CustomMessagePropagates()
    {
        var (_, _, m) = ErrorMap.Map(new SyncNotFoundException("gone"));
        Assert.Equal("gone", m);
    }

    [Fact]
    public void ErrorMap_NotImplementedWireNameMatchesNode()
    {
        var (s, c, _) = ErrorMap.Map(new RouteNotImplementedException());
        Assert.Equal(404, s);
        Assert.Equal("NotImplementedException", c); // Node exception.ts name
    }

    // ---- requests ----

    [Fact]
    public void Requests_ParseCreate()
    {
        Assert.Equal("blob", BookmarkRequests.ParseCreateV1(Body("""{"bookmarks":"blob"}""")));
        Assert.Equal("1.8.0", BookmarkRequests.ParseCreateV2(Body("""{"version":"1.8.0"}""")));
        Assert.Throws<RequiredDataNotFoundException>(() => BookmarkRequests.ParseCreateV1(Body("""{}""")));
        Assert.Throws<RequiredDataNotFoundException>(() => BookmarkRequests.ParseCreateV1(Body("""{"bookmarks":""}""")));
        Assert.Throws<RequiredDataNotFoundException>(() => BookmarkRequests.ParseCreateV2(Body("""{"version":42}""")));
    }

    [Fact]
    public void Requests_ParseUpdate()
    {
        var (b, lu, v) = BookmarkRequests.ParseUpdate(Body("""{"bookmarks":"x","lastUpdated":"t","version":"1.8.0"}"""));
        Assert.Equal(("x", "t", "1.8.0"), (b, lu, v));
        var (_, lu2, v2) = BookmarkRequests.ParseUpdate(Body("""{"bookmarks":"x"}"""));
        Assert.Null(lu2);
        Assert.Null(v2);
        Assert.Throws<RequiredDataNotFoundException>(() => BookmarkRequests.ParseUpdate(Body("""{"lastUpdated":"t"}""")));
        Assert.Throws<RequiredDataNotFoundException>(() => BookmarkRequests.ParseUpdate(Body("""[1,2]""")));
    }

    // ---- sanitizer ----

    [Theory]
    [InlineData(null, "…")]
    [InlineData("", "…")]
    [InlineData("short", "…")]
    [InlineData("426e20b680054872a46c3e1ce9a2c54e", "426e…4e")]
    public void Sanitizer_TruncatesIds(string? input, string expected)
    {
        Assert.Equal(expected, LogSanitizer.SyncId(input));
    }

    [Fact]
    public void Sanitizer_RedactsPaths()
    {
        var id = "426e20b680054872a46c3e1ce9a2c54e";
        var redacted = LogSanitizer.Path($"/bookmarks/{id}/lastUpdated");
        Assert.DoesNotContain(id, redacted);
        Assert.Contains("426e…4e", redacted);
        Assert.Equal("/info", LogSanitizer.Path("/info"));
        Assert.Equal("/", LogSanitizer.Path(null));
    }

    // ---- time ----

    [Fact]
    public void SyncTime_FormatsLikeToISOString()
    {
        Assert.Equal("2026-09-25T12:00:00.000Z",
            SyncTime.ToIsoString(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal("2026-09-25T12:00:00.000Z",
            SyncTime.ToIsoString(new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Unspecified)));
    }

    [Theory]
    [InlineData("2026-09-25T12:00:00.000Z", true)]
    [InlineData("2026-09-25T12:00:00.00Z", true)] // short spelling, same instant
    [InlineData("2026-09-25T12:00:00.0001000Z", true)] // sub-ms echo truncates equal
    [InlineData("2026-09-25T12:00:00.001Z", false)]
    [InlineData("not-a-date", false)]
    [InlineData(null, true)] // absent base skips the check
    public void SyncTime_SameInstant(string? incoming, bool expected)
    {
        var existing = new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(expected, SyncTime.SameInstant(incoming, existing));
    }

    // ---- settings ----

    [Fact]
    public void Settings_DefaultsMirrorNode()
    {
        var s = new XbsSettings();
        Assert.Equal(3, s.DailyNewSyncsLimit);
        Assert.Equal(5242, s.MaxSyncs);
        Assert.Equal(512000, s.MaxSyncSize);
        Assert.Equal(512000, s.EffectiveMaxSyncSize);
        Assert.Empty(s.AllowedOrigins);
        Assert.True(s.Status.Online);
        Assert.True(s.Status.AllowNewSyncs);
        Assert.Equal(1000, s.Throttle.MaxRequests);
        Assert.Equal(300000, s.Throttle.TimeWindow);
        Assert.Equal(8080, s.Server.Port);
    }

    [Fact]
    public void Settings_ZeroSizeFallsBack()
    {
        Assert.Equal(512000, new XbsSettings { MaxSyncSize = 0 }.EffectiveMaxSyncSize);
    }

    // ---- v1.2 contract ----

    [Theory]
    [InlineData("1.2.0", true)]
    [InlineData("1.2.7", true)]
    [InlineData("1.1.13", false)]
    [InlineData("1.0.5", false)]
    [InlineData("2.0.0", false)]
    public void ExtendedContract_Gated(string version, bool expected)
    {
        Assert.Equal(expected, KeepmarkApi.Routing.ApiVersioning.IsExtendedContract(version));
    }

    [Theory]
    [InlineData("""{"baseRevision":3}""", 3L)]
    [InlineData("""{"baseRevision":"7"}""", 7L)]
    [InlineData("""{}""", null)]
    [InlineData("""{"baseRevision":"x"}""", null)]
    [InlineData("""{"baseRevision":1.5}""", null)]
    public void ParseBaseRevision_Matrix(string json, long? expected)
    {
        Assert.Equal(expected, BookmarkRequests.ParseBaseRevision(Body(json)));
    }

    [Fact]
    public void BlobMeta_HashAndSize()
    {
        Assert.Equal(64, KeepmarkApi.Content.BlobMeta.HashOf("x").Length);
        Assert.Equal(KeepmarkApi.Content.BlobMeta.HashOf("x"), KeepmarkApi.Content.BlobMeta.HashOf("x"));
        Assert.NotEqual(KeepmarkApi.Content.BlobMeta.HashOf("x"), KeepmarkApi.Content.BlobMeta.HashOf("y"));
        Assert.Equal(0, KeepmarkApi.Content.BlobMeta.SizeOf(null));
        Assert.Equal(3, KeepmarkApi.Content.BlobMeta.SizeOf("abc"));
    }

    // ---- legacy shape freeze: stripped DTOs must not contain new keys ----

    [Fact]
    public void LegacyShapes_HaveNoMetaOrCapabilitiesKeys()
    {
        var meta = new KeepmarkApi.Services.SyncMetadata { Revision = 5, Hash = "abc", Size = 3 };
        var create = KeepmarkApi.Shaping.ResponseShapes.Strip(
            new KeepmarkApi.Services.CreateBookmarksResponse2 { Id = "i", Meta = meta });
        var get = KeepmarkApi.Shaping.ResponseShapes.Strip(
            new KeepmarkApi.Services.GetBookmarksResponse2 { Bookmarks = "b", Meta = meta });
        var update = KeepmarkApi.Shaping.ResponseShapes.Strip(
            new KeepmarkApi.Services.UpdateBookmarksResponse2 { Meta = meta });
        foreach (var dto in new object[] { create, get, update })
        {
            var json = System.Text.Json.JsonSerializer.Serialize(dto);
            Assert.DoesNotContain("meta", json);
        }

        var info = new KeepmarkApi.Services.ServiceInfoResponse
            { Status = 1, Version = "1.2.0", Message = string.Empty };
        Assert.DoesNotContain("capabilities", System.Text.Json.JsonSerializer.Serialize(info));
        info.Capabilities = new KeepmarkApi.Services.CapabilitiesResponse();
        Assert.Contains("capabilities", System.Text.Json.JsonSerializer.Serialize(info));
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Events = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
            => Events.Add((level, formatter(state, ex)));
    }

    [Fact]
    public async Task Logs_ConflictShowsBaseAndCurrent_RedactedId()
    {
        var store = new FakeBookmarkStore();
        var idBytes = SyncId.New();
        var id = SyncId.ToString(idBytes);
        store.Seed(new KeepmarkApi.Stores.StoredBookmark(idBytes, "b",
            new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc), "1.1.13"));
        var log = new TestLogger<BookmarksLogic>();
        var logic = new BookmarksLogic(store, new FakeSyncLogStore(), new XbsSettings(), log);
        await Assert.ThrowsAsync<SyncConflictException>(() =>
            logic.UpdateV2Async(id, "nb", "1970-01-01T00:00:00.000Z", null,
                new DateTime(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc)));
        var entry = Assert.Single(log.Events, e => e.Level == LogLevel.Information);
        Assert.Contains("1970-01-01T00:00:00.000Z", entry.Message); // base
        Assert.Contains("2026-09-20T08:00:00.000Z", entry.Message); // current
        Assert.DoesNotContain(id, entry.Message); // redacted
    }

    [Fact]
    public async Task Logs_CreateShowsRedactedId()
    {
        var log = new TestLogger<BookmarksLogic>();
        var logic = new BookmarksLogic(new FakeBookmarkStore(), new FakeSyncLogStore(), new XbsSettings(), log);
        var res = await logic.CreateV1Async("d", null, DateTime.UtcNow);
        var entry = Assert.Single(log.Events, e => e.Level == LogLevel.Information);
        Assert.DoesNotContain(res.Id!, entry.Message);
        Assert.Contains(LogSanitizer.SyncId(res.Id), entry.Message);
    }

    [Fact]
    public async Task Logs_GuardRefusalIsAuditable()
    {
        var log = new TestLogger<BookmarksLogic>();
        var s = new XbsSettings();
        s.Status.AllowNewSyncs = false;
        var logic = new BookmarksLogic(new FakeBookmarkStore(), new FakeSyncLogStore(), s, log);
        await Assert.ThrowsAsync<NewSyncsForbiddenException>(() => logic.CreateV1Async("d", null, DateTime.UtcNow));
        Assert.Contains(log.Events, e => e.Level == LogLevel.Information);
        Assert.Contains(log.Events, e => e.Level == LogLevel.Debug); // decision trace
    }
}
