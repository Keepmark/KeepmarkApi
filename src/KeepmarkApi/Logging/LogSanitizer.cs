using System.Text.RegularExpressions;

namespace KeepmarkApi.Logging;

/// <summary>Server-side redaction gate. Sync IDs are bearer tokens (capability
/// URLs) and must never land in logs in full. Mirrors the Keepmark client
/// LogSanitizer policy: truncate, never drop the log line.</summary>
public static partial class LogSanitizer
{
    [GeneratedRegex("[0-9a-fA-F]{32}")]
    private static partial Regex SyncIdRegex();

    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex DashedSyncIdRegex();

    public static string SyncId(string? syncId)
    {
        if (string.IsNullOrEmpty(syncId) || syncId.Length <= 8)
        {
            return "…";
        }

        return $"{syncId[..4]}…{syncId[^2..]}";
    }

    public static string Path(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "/";
        }

        path = DashedSyncIdRegex().Replace(path, m => SyncId(m.Value));
        return SyncIdRegex().Replace(path, m => SyncId(m.Value));
    }
}
