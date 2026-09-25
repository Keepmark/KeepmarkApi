using KeepmarkApi.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace KeepmarkApi.Tests;

public sealed class FileLoggingTests
{
    [Theory]
    [InlineData("hourly", 1)]
    [InlineData("daily", 24)]
    [InlineData("weekly", 168)]
    [InlineData("monthly", 720)]
    [InlineData("yearly", 8760)]
    [InlineData("1d", 24)]
    [InlineData("12h", 12)]
    [InlineData("500ms", 0)]
    [InlineData("2w", 336)]
    [InlineData("bogus", 24)]
    [InlineData(null, 24)]
    public void PeriodParser_ParsesNodeFormats(string? input, int expectedHours)
    {
        var ts = PeriodParser.Parse(input);
        if (expectedHours == 0)
        {
            Assert.Equal(TimeSpan.FromMilliseconds(500), ts);
        }
        else
        {
            Assert.Equal(expectedHours, (int)ts.TotalHours);
        }
    }

    [Fact]
    public void FileLogger_RotatesDeterministically_WithInjectedClock()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(dir, "api.log");
        var gate = new LogLevelGate { MinLevel = LogLevel.Trace };
        var now = new DateTime(2026, 9, 25, 12, 0, 0);
        var clock = () => now;
        using var provider = new RotatingFileLoggerProvider(path, "debug", "1h", 2, gate, clock);
        var log = provider.CreateLogger("test");
        log.LogInformation("first-hour");
        now = now.AddHours(2); // cross into a new period, then another
        log.LogInformation("third-hour");
        now = now.AddHours(2);
        log.LogInformation("fifth-hour");
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".1"));
        Assert.True(File.Exists(path + ".2"));
        Assert.False(File.Exists(path + ".3"), "retention must drop files beyond keep");
        Assert.Contains("first-hour", File.ReadAllText(path + ".2"));
        Assert.Contains("fifth-hour", File.ReadAllText(path));
    }

    [Fact]
    public void FileLogger_RespectsBaseAndGateLevels()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(dir, "api.log");
        var gate = new LogLevelGate { MinLevel = LogLevel.Error }; // gate stricter than file level
        using var provider = new RotatingFileLoggerProvider(path, "debug", "1d", 1, gate);
        var log = provider.CreateLogger("test");
        log.LogWarning("suppressed by gate");
        log.LogError("kept");
        var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        Assert.DoesNotContain("suppressed", text);
        Assert.Contains("kept", text);
    }

    [Fact]
    public void FileLogger_UnwritablePathDisablesItself()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // /proc trick is Linux-only
        }

        var gate = new LogLevelGate();
        // /proc is not writable: must not throw, must swallow logs.
        using var provider = new RotatingFileLoggerProvider("/proc/xbs-test/api.log", "debug", "1d", 1, gate);
        var log = provider.CreateLogger("test");
        var ex = Record.Exception(() => log.LogInformation("lost"));
        Assert.Null(ex);
    }

    [Fact]
    public void NullLogger_AvailableForLogic()
    {
        Assert.NotNull(NullLogger.Instance);
    }
}
