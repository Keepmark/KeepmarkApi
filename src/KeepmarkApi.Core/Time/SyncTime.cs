namespace KeepmarkApi.Time;

/// <summary>Node-parity timestamp formatting: JavaScript toISOString()
/// is always UTC with exactly 3 fractional digits ("...000Z").</summary>
public static class SyncTime
{
    public static string ToIsoString(DateTime dt)
        => (dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
}
