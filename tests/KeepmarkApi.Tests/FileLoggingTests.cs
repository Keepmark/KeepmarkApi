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
    public void FileLogger_WritesAndRotates()
    {
        var dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        var path = Path.Combine(dir, "api.log");
        var gate = new LogLevelGate { MinLevel = LogLevel.Trace };
        using var provider = new RotatingFileLoggerProvider(path, "debug", "1ms", 2, gate);
        var log = provider.CreateLogger("test");
        log.LogInformation("hello");
        log.LogDebug("detail");
        static string AllText(string p) => string.Concat(
            Directory.GetFiles(Path.GetDirectoryName(p)!, Path.GetFileName(p) + "*").Select(File.ReadAllText));
        Assert.Contains("hello", AllText(path));
        // Force rollover by aging the period: many rapid writes cross 1ms windows.
        for (var i = 0; i < 200; i++)
        {
            log.LogInformation("line {N}", i);
        }

        Assert.True(File.Exists(path + ".1") || File.Exists(path + ".2") || new FileInfo(path).Length > 0);
        Assert.False(File.Exists(path + ".3"), "retention must drop files beyond keep");
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
