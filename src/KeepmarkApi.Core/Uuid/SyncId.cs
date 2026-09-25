using KeepmarkApi.Errors;

namespace KeepmarkApi.Uuid;

/// <summary>Sync-ID handling mirroring api/src/uuid.ts.
/// Wire form: 32 lowercase hex chars (dashes accepted, stripped).
/// Stored form: the 16 UUID bytes (RFC 4122 order) as BSON Binary subtype 4,
/// byte-compatible with mongoose.Types.Buffer(...).toObject(0x04).
/// Never use Guid.ToByteArray (mixed-endian) here.</summary>
public static class SyncId
{
    public static byte[] Parse(string? uuidString)
    {
        if (string.IsNullOrEmpty(uuidString))
        {
            throw new InvalidSyncIdException("Argument is not a valid UUID string");
        }

        var hex = uuidString.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit))
        {
            throw new InvalidSyncIdException("Argument is not a valid UUID string");
        }

        try
        {
            var bytes = Convert.FromHexString(hex);
            // Round-trip guard like the Node implementation.
            if (!string.Equals(ToString(bytes), hex, StringComparison.Ordinal))
            {
                throw new InvalidSyncIdException("Argument is not a valid UUID string");
            }

            return bytes;
        }
        catch (XbsException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new InvalidSyncIdException("Argument is not a valid UUID string");
        }
    }

    public static string ToString(byte[] bytes)
    {
        if (bytes is null || bytes.Length != 16)
        {
            throw new ArgumentException("Byte array not of the expected size", nameof(bytes));
        }

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static byte[] New() => Guid.NewGuid().ToRfc4122Bytes();

    /// <summary>RFC 4122 byte order (network order), unlike Guid.ToByteArray().</summary>
    public static byte[] ToRfc4122Bytes(this Guid guid)
    {
        var s = guid.ToString("N");
        return Convert.FromHexString(s);
    }
}
