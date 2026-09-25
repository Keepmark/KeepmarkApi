using KeepmarkApi.Errors;

namespace KeepmarkApi.Routing;

/// <summary>Accept-Version routing mirroring api/src/routers/api.router.ts
/// (express-routes-versioning). framework-free so tests run without the
/// ASP.NET Core runtime.</summary>
public static class ApiVersioning
{
    public static string ResolveHeader(string? header, string serverVersion)
        => string.IsNullOrWhiteSpace(header) ? serverVersion : header.Trim();

    /// <summary>POST|PUT /bookmarks: '~1.0.0' → v1, '^1.1.3' → v2 (see bookmarks.router.ts).</summary>
    public static int BookmarksRouteVersion(string acceptVersion)
    {
        var (major, minor, patch) = Parse(acceptVersion);
        if (major == 1 && minor == 0)
        {
            return 1; // ~1.0.0: >=1.0.0 <1.1.0
        }

        if (major == 1 && (minor > 1 || (minor == 1 && patch >= 3)))
        {
            return 2; // ^1.1.3: >=1.1.3 <2.0.0
        }

        throw new UnsupportedVersionException();
    }

    public static (int Major, int Minor, int Patch) Parse(string version)
    {
        var parts = version.TrimStart('v', 'V').Split('.');
        int Num(int i) => i < parts.Length && int.TryParse(new string(parts[i].TakeWhile(char.IsDigit).ToArray()), out var n) ? n : 0;
        return (Num(0), Num(1), Num(2));
    }

    public static int Compare(string a, string b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        foreach (var d in new[] { pa.Major - pb.Major, pa.Minor - pb.Minor, pa.Patch - pb.Patch })
        {
            if (d != 0)
            {
                return Math.Sign(d);
            }
        }

        return 0;
    }

    /// <summary>v1.2 extended contract (^1.2.0): revision/hash metadata,
    /// conditional requests, DELETE. Anything older gets the byte-identical
    /// v1.1.x shapes.</summary>
    public static bool IsExtendedContract(string acceptVersion)
    {
        var (major, minor, _) = Parse(acceptVersion);
        return major == 1 && minor >= 2;
    }
}
