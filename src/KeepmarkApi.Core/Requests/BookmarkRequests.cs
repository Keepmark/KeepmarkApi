using KeepmarkApi.Errors;

namespace KeepmarkApi.Requests;

/// <summary>Request-body validation mirroring bookmarks.router.ts handlers:
/// missing/empty required fields → RequiredDataNotFoundException (400).
/// Pure (System.Text.Json only) so it is unit-testable.</summary>
public static class BookmarkRequests
{
    public static string ParseCreateV1(System.Text.Json.JsonElement body)
        => RequiredString(body, "bookmarks");

    public static string ParseCreateV2(System.Text.Json.JsonElement body)
        => RequiredString(body, "version");

    public static (string Bookmarks, string? LastUpdated, string? Version) ParseUpdate(System.Text.Json.JsonElement body)
        => (RequiredString(body, "bookmarks"), OptionalString(body, "lastUpdated"), OptionalString(body, "version"));

    private static string RequiredString(System.Text.Json.JsonElement body, string name)
    {
        var s = OptionalString(body, name);
        if (string.IsNullOrEmpty(s))
        {
            throw new RequiredDataNotFoundException();
        }

        return s;
    }

    private static string? OptionalString(System.Text.Json.JsonElement body, string name)
        => body.ValueKind == System.Text.Json.JsonValueKind.Object
            && body.TryGetProperty(name, out var el)
            && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString()
            : null;
}
