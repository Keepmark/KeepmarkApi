using System.Text.Json.Serialization;

namespace KeepmarkApi.Config;

/// <summary>API version sent by clients in the Accept-Version header and reported in /info.
/// Mirrors api/package.json (v1.1.13). Clients send MinApiVersion..ApiVersion.</summary>
public static class XbsVersions
{
    public const string ApiVersion = "1.1.13";
    public const string MinApiVersion = "1.1.9";
}

/// <summary>Status codes mirrored from api/src/common/enums.ts ServiceStatus.</summary>
public enum ServiceStatus
{
    Online = 1,
    Offline = 2,
    NoNewSyncs = 3
}

/// <summary>Error body. Wire shape is {code,message} like api/src/exception.ts.</summary>
public sealed class ErrorBody
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
