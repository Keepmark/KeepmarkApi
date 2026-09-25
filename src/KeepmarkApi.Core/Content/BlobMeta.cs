using System.Security.Cryptography;
using System.Text;

namespace KeepmarkApi.Content;

/// <summary>Server-side blob metadata (v1.2 contract). The blob stays opaque:
/// hash/size describe the ciphertext and leak nothing beyond its length,
/// which the operator already sees in the document size.</summary>
public static class BlobMeta
{
    public static string HashOf(string? blob)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(blob ?? string.Empty))).ToLowerInvariant();

    public static long SizeOf(string? blob)
        => Encoding.UTF8.GetByteCount(blob ?? string.Empty);
}
