using KeepmarkApi.Services;

namespace KeepmarkApi.Shaping;

/// <summary>Contract shaping: v1.1.x responses must be byte-identical to Node
/// (no meta/capabilities keys). Pure mapping — unit-tested.</summary>
public static class ResponseShapes
{
    public static CreateBookmarksResponse Strip(CreateBookmarksResponse2 r)
        => new() { Id = r.Id, LastUpdated = r.LastUpdated, Version = r.Version };

    public static GetBookmarksResponse Strip(GetBookmarksResponse2 r)
        => new() { Bookmarks = r.Bookmarks, Version = r.Version, LastUpdated = r.LastUpdated };

    public static UpdateBookmarksResponse Strip(UpdateBookmarksResponse2 r)
        => new() { LastUpdated = r.LastUpdated };

    public static string SerializeKeys(object dto)
        => System.Text.Json.JsonSerializer.Serialize(dto);
}
