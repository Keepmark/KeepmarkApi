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

    /// <summary>v1.2 revision base. Accepts numbers and numeric strings; absent otherwise.</summary>
    public static long? ParseBaseRevision(System.Text.Json.JsonElement body)
    {
        if (body.ValueKind != System.Text.Json.JsonValueKind.Object
            || !body.TryGetProperty("baseRevision", out var el))
        {
            return null;
        }

        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number when el.TryGetInt64(out var n) => n,
            System.Text.Json.JsonValueKind.String when long.TryParse(el.GetString(), out var s) => s,
            _ => null,
        };
    }

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
            ? CoerceScalar(el)
            : null;

    /// <summary>Node/mongoose coerces scalar JSON to strings (42 → "42").
    /// Objects/arrays stay absent (→ required checks fail / version preserved).</summary>
    private static string? CoerceScalar(System.Text.Json.JsonElement el)
        => el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.String => el.GetString(),
            System.Text.Json.JsonValueKind.Number => el.GetRawText(),
            System.Text.Json.JsonValueKind.True => "true",
            System.Text.Json.JsonValueKind.False => "false",
            _ => null,
        };
}
