using System.Text.Json.Serialization;

namespace KeepmarkApi.Services;

/// <summary>Response DTOs mirror bookmarks.service.ts ICreate/IGet/... interfaces.</summary>
public sealed class CreateBookmarksResponse
{
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }
}

public sealed class GetBookmarksResponse
{
    [JsonPropertyName("bookmarks")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bookmarks { get; set; }
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }
}

public sealed class GetLastUpdatedResponse
{
    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }
}

public sealed class GetVersionResponse
{
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
}

public sealed class UpdateBookmarksResponse
{
    [JsonPropertyName("lastUpdated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? LastUpdated { get; set; }
}

public sealed class ServiceInfoResponse
{
    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; set; }
    [JsonPropertyName("maxSyncSize")]
    public long MaxSyncSize { get; set; }
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
    [JsonPropertyName("status")]
    public int Status { get; set; }
    [JsonPropertyName("version")]
    public string? Version { get; set; }
}
