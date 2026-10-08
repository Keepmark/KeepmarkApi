using System.Text;
using KeepmarkApi.Logging;
using Microsoft.Extensions.Logging;

namespace KeepmarkApi.Logging;

/// <summary>File sink honoring the Node log.file settings (path, level,
/// rotationPeriod, rotatedFilesToKeep). Bunyan-compatible rotation:
/// on rollover path → path.0 → path.1 … dropping beyond the keep count.
/// Creates the directory (mkdirp equivalent); if it cannot write, it disables
/// itself with a console warning instead of crashing the service.</summary>
public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly LogLevel _baseLevel;
    private readonly LogLevelGate _gate;
    private readonly int _keep;
    private readonly TimeSpan _period;
    private readonly Func<DateTime> _clock;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateTime _periodStart;
    private bool _disabled;

    public RotatingFileLoggerProvider(string path, string level, string rotationPeriod, int keep, LogLevelGate gate)
        : this(path, level, rotationPeriod, keep, gate, null)
    {
    }

    /// <param name="clock">Override for deterministic tests (defaults to local now).</param>
    public RotatingFileLoggerProvider(
        string path, string level, string rotationPeriod, int keep, LogLevelGate gate, Func<DateTime>? clock)
    {
        _path = path;
        _baseLevel = XbsLogLevels.Parse(level);
        _keep = Math.Max(0, keep);
        _period = PeriodParser.Parse(rotationPeriod);
        _gate = gate;
        _clock = clock ?? (() => DateTime.Now);
        _periodStart = PeriodParser.StartOf(_clock(), _period);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private bool EnsureWriter()
    {
        if (_disabled)
        {
            return false;
        }

        if (_writer is not null && PeriodParser.StartOf(_clock(), _period) == _periodStart)
        {
            return true;
        }

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (_writer is not null)
            {
                Rotate();
            }

            _writer = new StreamWriter(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
            {
                AutoFlush = true,
            };
            _periodStart = PeriodParser.StartOf(_clock(), _period);
            return true;
        }
        catch (Exception ex)
        {
            _disabled = true;
            Console.Error.WriteLine($"Failed to initialise log file '{_path}': {ex.Message}. File logging disabled.");
            return false;
        }
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        // Shift path.(keep-1) out, path.N → path.N+1, path → path.0.
        var oldest = $"{_path}.{_keep}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _keep - 1; i >= 0; i--)
        {
            var src = i == 0 ? _path : $"{_path}.{i}";
            if (File.Exists(src))
            {
                File.Move(src, $"{_path}.{i + 1}", overwrite: true);
            }
        }
    }

    private sealed class FileLogger(RotatingFileLoggerProvider parent, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level)
            => level >= parent._baseLevel && level >= parent._gate.MinLevel;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level))
            {
                return;
            }

            lock (parent._sync)
            {
                if (!parent.EnsureWriter() || parent._writer is null)
                {
                    return;
                }

                var line = new StringBuilder()
                    .Append(DateTimeOffset.UtcNow.ToString("o")).Append(' ')
                    .Append(XbsLogLevels.Name(level).ToUpperInvariant()).Append(' ')
                    .Append(category).Append(' ')
                    .Append(formatter(state, ex));
                if (ex is not null)
                {
                    // Exceptions (e.g. Mongo driver errors) can echo filter
                    // values — sync IDs are bearer tokens, scrub them too.
                    line.Append(" :: ").Append(LogSanitizer.Path(ex.ToString()));
                }

                parent._writer.WriteLine(line.ToString());
            }
        }
    }
}

/// <summary>Parses Node rotationPeriod values: "$number$scope" (ms,s,m,h,d,w,M,y)
/// or names hourly/daily/weekly/monthly/yearly. Suffixes are case-sensitive:
/// "m" = minutes, "M" = months (30d), "s" = seconds — lowercasing them (as
/// before) mapped "30m" to 900 days and dropped "5s" to the 1-day fallback.</summary>
public static class PeriodParser
{
    public static TimeSpan Parse(string? period)
    {
        if (period is not null)
        {
            switch (period.Trim().ToLowerInvariant())
            {
                case "hourly": return TimeSpan.FromHours(1);
                case "daily": return TimeSpan.FromDays(1);
                case "weekly": return TimeSpan.FromDays(7);
                case "monthly": return TimeSpan.FromDays(30);
                case "yearly": return TimeSpan.FromDays(365);
            }

            if (TrySuffixed(period.Trim(), out var ts))
            {
                return ts;
            }
        }

        return TimeSpan.FromDays(1);
    }

    private static bool TrySuffixed(string p, out TimeSpan ts)
    {
        ts = TimeSpan.FromDays(1);
        string num, scope;
        if (p.EndsWith("ms", StringComparison.Ordinal))
        {
            num = p[..^2];
            scope = "ms";
        }
        else if (p.Length >= 2)
        {
            num = p[..^1];
            scope = p[^1..];
        }
        else
        {
            return false;
        }

        if (!double.TryParse(num, out var n))
        {
            return false;
        }

        ts = scope switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            "w" => TimeSpan.FromDays(n * 7),
            "M" => TimeSpan.FromDays(n * 30),
            "y" => TimeSpan.FromDays(n * 365),
            _ => TimeSpan.FromDays(-1),
        };
        return ts.TotalMilliseconds > 0;
    }

    public static DateTime StartOf(DateTime now, TimeSpan period)
    {
        if (period >= TimeSpan.FromDays(365))
        {
            return new DateTime(now.Year, 1, 1);
        }

        if (period >= TimeSpan.FromDays(28))
        {
            return new DateTime(now.Year, now.Month, 1);
        }

        if (period >= TimeSpan.FromDays(7))
        {
            // Weeks start Sunday (bunyan weekly).
            return now.Date.AddDays(-(int)now.DayOfWeek);
        }

        if (period >= TimeSpan.FromDays(1))
        {
            return now.Date;
        }

        if (period >= TimeSpan.FromHours(1))
        {
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        }

        return now;
    }
}
