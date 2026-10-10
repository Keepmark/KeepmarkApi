using System.Text.Json.Serialization;
using KeepmarkApi.Stores;

namespace KeepmarkApi.Services;

/// <summary>Response DTOs mirror bookmarks.service.ts ICreate/IGet/... interfaces.</summary>
public class CreateBookmarksResponse
{
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }
    [JsonPropertyName("id")]
    public string? Id { get; set; }
    [JsonPropertyName("lastUpdated")]
    public DateTime? LastUpdated { get; set; }
}

public class GetBookmarksResponse
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

public class UpdateBookmarksResponse
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
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CapabilitiesResponse? Capabilities { get; set; }
}

/// <summary>v1.2 feature advertisement. Only present on the extended contract;
/// legacy clients keep the byte-identical shape.</summary>
public sealed class CapabilitiesResponse
{
    [JsonPropertyName("revision")]
    public bool Revision { get; set; } = true;
    [JsonPropertyName("conditionalGet")]
    public bool ConditionalGet { get; set; } = true;
    [JsonPropertyName("delete")]
    public bool Delete { get; set; } = true;
    /// <summary>Days of inactivity after which the server deletes a sync
    /// (SyncExpiryDays); 0 = never. Lets clients warn their users.</summary>
    [JsonPropertyName("syncExpiryDays")]
    public int SyncExpiryDays { get; set; }
}

/// <summary>v1.2 metadata envelope. Only present on the extended contract.</summary>
public sealed class SyncMetadata
{
    [JsonPropertyName("revision")]
    public long Revision { get; set; }
    [JsonPropertyName("hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; set; }
    [JsonPropertyName("size")]
    public long Size { get; set; }
    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedAt { get; set; }
}

public static class ExtendedResponses
{
    public static CreateBookmarksResponse2 Create(StoredBookmark doc, string id)
        => new()
        {
            Id = id,
            LastUpdated = doc.LastUpdated,
            Version = doc.Version,
            Meta = MetaOf(doc),
        };

    public static GetBookmarksResponse2 Get(StoredBookmark doc)
        => new()
        {
            Bookmarks = doc.Bookmarks,
            Version = doc.Version,
            LastUpdated = doc.LastUpdated,
            Meta = MetaOf(doc),
        };

    public static UpdateBookmarksResponse2 Updated(StoredBookmark doc)
        => new() { LastUpdated = doc.LastUpdated, Meta = MetaOf(doc) };

    public static SyncMetadata MetaOf(StoredBookmark doc)
        => new()
        {
            Revision = doc.Revision,
            Hash = doc.Hash,
            Size = doc.Size,
            CreatedAt = doc.CreatedAt,
        };
}

public sealed class CreateBookmarksResponse2 : CreateBookmarksResponse
{
    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncMetadata? Meta { get; set; }
}

public sealed class GetBookmarksResponse2 : GetBookmarksResponse
{
    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncMetadata? Meta { get; set; }
}

public sealed class UpdateBookmarksResponse2 : UpdateBookmarksResponse
{
    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SyncMetadata? Meta { get; set; }
}
