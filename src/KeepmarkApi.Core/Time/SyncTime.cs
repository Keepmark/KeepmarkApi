namespace KeepmarkApi.Time;

/// <summary>Node-parity timestamp formatting: JavaScript toISOString()
/// is always UTC with exactly 3 fractional digits ("...000Z").</summary>
public static class SyncTime
{
    private static readonly string[] IsoFormats =
    [
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
    ];

    public static string ToIsoString(DateTime dt)
        => (dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>BSON dates carry millisecond precision; in-memory UTC ticks
    /// must be truncated on write or echoes never match.</summary>
    public static DateTime TruncateToMs(DateTime dt)
    {
        var utc = dt.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(dt, DateTimeKind.Utc)
            : dt.ToUniversalTime();
        return new DateTime(utc.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    /// <summary>Base/current comparison for optimistic concurrency. Node compares
    /// raw strings (always canonical 3-digit there); we compare parsed instants
    /// at ms precision so equivalent spellings (".87" vs ".870", sub-ms echoes)
    /// agree. Unparseable input counts as mismatch.</summary>
    public static bool SameInstant(string? incoming, DateTime existing)
    {
        if (string.IsNullOrEmpty(incoming))
        {
            return true;
        }

        return TryParseInstant(incoming, out var baseMs) && baseMs == TruncateToMs(existing);
    }

    public static bool TryParseInstant(string? incoming, out DateTime ms)
    {
        ms = default;
        if (string.IsNullOrEmpty(incoming))
        {
            return false;
        }

        // ISO-8601 only, culture-independent (Node compares canonical
        // toISOString() strings; loose local formats must never match).
        if (!DateTime.TryParseExact(incoming, IsoFormats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return false;
        }

        ms = TruncateToMs(parsed);
        return true;
    }
}
