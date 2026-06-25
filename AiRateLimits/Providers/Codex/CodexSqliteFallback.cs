using System.IO;
using Microsoft.Data.Sqlite;

namespace AiRateLimits.Providers.Codex;

/// <summary>
/// Reads the newest cached codex.rate_limits event from logs_2.sqlite. The stored
/// feedback_log_body is a tracing line with the JSON embedded after "websocket event: ",
/// so the event object is extracted by brace matching. Used only when the live endpoint
/// fails. Schema is unstable and may change.
/// </summary>
public static class CodexSqliteFallback
{
    private const string Marker = "\"type\":\"codex.rate_limits\"";

    private const string Sql =
        "SELECT feedback_log_body " +
        "FROM logs " +
        "WHERE feedback_log_body LIKE '%\"type\":\"codex.rate_limits\"%' " +
        "ORDER BY ts DESC, ts_nanos DESC, id DESC " +
        "LIMIT 1";

    /// <summary>
    /// Returns the newest cached codex.rate_limits event as a standalone JSON object string,
    /// or null if none is available or the embedded object cannot be extracted.
    /// </summary>
    public static string? TryReadLatestBody(string? sqlitePath = null)
    {
        var path = sqlitePath ?? CodexPaths.LogsSqlite;
        if (!File.Exists(path))
        {
            return null;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = Sql;

        using var reader = command.ExecuteReader();
        if (reader.Read() && !reader.IsDBNull(0))
        {
            return ExtractEventObject(reader.GetString(0));
        }

        return null;
    }

    /// <summary>
    /// Extracts the JSON object containing the codex.rate_limits marker from a log line by
    /// finding the enclosing braces. String literals and escapes are respected so braces
    /// inside strings do not throw off the depth count.
    /// </summary>
    internal static string? ExtractEventObject(string body)
    {
        var markerIndex = body.IndexOf(Marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return null;
        }

        var start = body.LastIndexOf('{', markerIndex);
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escape = false;

        for (var i = start; i < body.Length; i++)
        {
            var ch = body[i];

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                }
                else if (ch == '\\')
                {
                    escape = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return body.Substring(start, i - start + 1);
                    }

                    break;
            }
        }

        return null;
    }
}
