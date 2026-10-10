namespace KeepmarkApi.Config;

/// <summary>Settings mirror of api/config/settings.default.json (xBrowserSync API v1.1.13).
/// Section/key names are kept identical so existing self-host operators can
/// reuse their knowledge. Bound from appsettings.json via IOptions.</summary>
public sealed class XbsSettings
{
    public List<string> AllowedOrigins { get; set; } = [];
    public int DailyNewSyncsLimit { get; set; } = 3;
    public DbSettings Db { get; set; } = new();
    public string Location { get; set; } = string.Empty;
    public LogSettings Log { get; set; } = new();
    public long MaxSyncs { get; set; } = 5242;
    public long MaxSyncSize { get; set; } = 1048576;
    public ServerSettings Server { get; set; } = new();
    public StatusSettings Status { get; set; } = new();
    /// <summary>Delete syncs not accessed for this many days (Mongo TTL on
    /// lastAccessed, applied at startup). 0 = never (default). Keepmark-only:
    /// Node has no setting, only the manual index in api/README.md.</summary>
    public int SyncExpiryDays { get; set; }
    public ThrottleSettings Throttle { get; set; } = new();

    /// <summary>Effective body limit. Node falls back to 512000 on 0; we default
    /// to 1 MB (official-service parity), keeping the 0-means-default behavior.</summary>
    public long EffectiveMaxSyncSize => MaxSyncSize > 0 ? MaxSyncSize : 1048576;

    public sealed class DbSettings
    {
        public string AuthSource { get; set; } = "admin";
        public int ConnTimeout { get; set; } = 30000;
        public string Host { get; set; } = "127.0.0.1";
        public string Name { get; set; } = "xbrowsersync";
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public int Port { get; set; } = 27017;
        public bool Ssl { get; set; }
        public bool UseSrv { get; set; }
    }

    public sealed class LogSettings
    {
        public FileLogSettings File { get; set; } = new();
        public StdoutLogSettings Stdout { get; set; } = new();

        public sealed class FileLogSettings
        {
            public bool Enabled { get; set; } = true;
            public string Level { get; set; } = "info";
            public string Path { get; set; } = "/var/log/xBrowserSync/api.log";
            public int RotatedFilesToKeep { get; set; } = 5;
            public string RotationPeriod { get; set; } = "1d";
        }

        public sealed class StdoutLogSettings
        {
            public bool Enabled { get; set; } = true;
            public string Level { get; set; } = "info";
        }
    }

    public sealed class ServerSettings
    {
        public bool BehindProxy { get; set; }
        public string Host { get; set; } = "127.0.0.1";
        public HttpsSettings Https { get; set; } = new();
        public int Port { get; set; } = 8080;
        public string RelativePath { get; set; } = "/";

        public sealed class HttpsSettings
        {
            public string CertPath { get; set; } = string.Empty;
            public bool Enabled { get; set; }
            public string KeyPath { get; set; } = string.Empty;
        }
    }

    public sealed class StatusSettings
    {
        public bool AllowNewSyncs { get; set; } = true;
        public string Message { get; set; } = string.Empty;
        public bool Online { get; set; } = true;
    }

    public sealed class ThrottleSettings
    {
        public int MaxRequests { get; set; } = 1000;
        public int TimeWindow { get; set; } = 300000;
    }
}
