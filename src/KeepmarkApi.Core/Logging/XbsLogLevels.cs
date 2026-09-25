using Microsoft.Extensions.Logging;

namespace KeepmarkApi.Logging;

/// <summary>Bunyan-compatible level names from api/config/settings.default.json
/// ("trace","debug","info","warn","error","fatal") mapped to MEL levels.
/// Unknown/empty → Information. Trace/Debug = verbose mode.</summary>
public static class XbsLogLevels
{
    public static LogLevel Parse(string? level)
        => level?.Trim().ToLowerInvariant() switch
        {
            "trace" => LogLevel.Trace,
            "debug" => LogLevel.Debug,
            "info" => LogLevel.Information,
            "warn" or "warning" => LogLevel.Warning,
            "error" => LogLevel.Error,
            "fatal" => LogLevel.Critical,
            _ => LogLevel.Information,
        };

    public static string Name(LogLevel level)
        => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "error",
            LogLevel.Critical => "fatal",
            _ => "info",
        };
}

/// <summary>Mutable verbosity gate. The MEL filter closure reads this on every
/// event, so changing MinLevel (file watch, Docker env reload) takes effect
/// on the fly without restart.</summary>
public sealed class LogLevelGate
{
    public volatile LogLevel MinLevel = LogLevel.Information;
}
