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
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private DateTime _periodStart;
    private bool _disabled;

    public RotatingFileLoggerProvider(string path, string level, string rotationPeriod, int keep, LogLevelGate gate)
    {
        _path = path;
        _baseLevel = XbsLogLevels.Parse(level);
        _keep = Math.Max(0, keep);
        _period = PeriodParser.Parse(rotationPeriod);
        _gate = gate;
        _periodStart = PeriodParser.StartOf(DateTime.Now, _period);
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

        if (_writer is not null && PeriodParser.StartOf(DateTime.Now, _period) == _periodStart)
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
            _periodStart = PeriodParser.StartOf(DateTime.Now, _period);
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
                    line.Append(" :: ").Append(ex);
                }

                parent._writer.WriteLine(line.ToString());
            }
        }
    }
}

/// <summary>Parses Node rotationPeriod values: "$number$scope" (ms,h,d,w,m,y)
/// or names hourly/daily/weekly/monthly/yearly.</summary>
public static class PeriodParser
{
    public static TimeSpan Parse(string? period)
        => period?.Trim().ToLowerInvariant() switch
        {
            "hourly" => TimeSpan.FromHours(1),
            "daily" => TimeSpan.FromDays(1),
            "weekly" => TimeSpan.FromDays(7),
            "monthly" => TimeSpan.FromDays(30),
            "yearly" => TimeSpan.FromDays(365),
            { } p when TrySuffixed(p, out var ts) => ts,
            _ => TimeSpan.FromDays(1),
        };

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
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            "w" => TimeSpan.FromDays(n * 7),
            "m" => TimeSpan.FromDays(n * 30),
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
