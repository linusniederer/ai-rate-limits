using System.IO;

namespace AiRateLimits.Providers.Codex;

/// <summary>
/// Resolves Codex on-disk locations. Honors CODEX_HOME; otherwise uses %USERPROFILE%\.codex.
/// </summary>
public static class CodexPaths
{
    public static string Home
    {
        get
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(codexHome))
            {
                return codexHome;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, ".codex");
        }
    }

    public static string AuthJson => Path.Combine(Home, "auth.json");

    public static string LogsSqlite => Path.Combine(Home, "logs_2.sqlite");

    public static string SessionsDir => Path.Combine(Home, "sessions");

    public static string ArchivedSessionsDir => Path.Combine(Home, "archived_sessions");
}
