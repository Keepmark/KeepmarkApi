using KeepmarkApi.Config;
using Microsoft.Extensions.Options;

namespace KeepmarkApi.Logging;

/// <summary>Watches settings reloads (appsettings.Local.json edits, mounted file
/// changes in Docker) and moves the verbosity gate on the fly — no restart.
/// The change itself is always logged, even at higher levels.</summary>
public sealed class LogLevelWatcher(
    IOptionsMonitor<XbsSettings> settings,
    LogLevelGate gate,
    ILogger<LogLevelWatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = settings.OnChange(s =>
        {
            var level = XbsLogLevels.Parse(s.Log.Stdout.Level);
            if (level != gate.MinLevel)
            {
                gate.MinLevel = level;
                log.LogInformation("Log level changed to {Level} (on the fly)", XbsLogLevels.Name(level));
            }
        });

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Host shutting down: subscription disposed above.
        }
    }
}
