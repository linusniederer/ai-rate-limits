using System.IO;
using System.Text.Json;
using AiRateLimits.Models;
using Serilog;

namespace AiRateLimits.Providers.Codex.Cost;

/// <summary>
/// Estimates current- and previous-month Codex spend from local session JSONL logs and public
/// model prices. This is an API-rate estimate, not an official bill, and never affects health.
/// </summary>
public sealed class CodexCostScanner
{
    private readonly CodexModelPricing _pricing;

    public CodexCostScanner(CodexModelPricing pricing) => _pricing = pricing;

    public async Task<CostUsageSnapshot?> ScanAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var prevMonthStart = monthStart.AddMonths(-1);

        var current = new MonthAccumulator();
        var previous = new MonthAccumulator();

        foreach (var file in SessionFiles(prevMonthStart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanFile(file, monthStart, prevMonthStart, current, previous);
        }

        if (!current.HasAny && !previous.HasAny)
        {
            return null;
        }

        var table = await _pricing.LoadAsync(cancellationToken).ConfigureAwait(false);

        var prevSnapshot = previous.ToSnapshot(table, prevMonthStart, monthStart, includePrevious: false);
        return current.ToSnapshot(table, monthStart, monthStart.AddMonths(1), includePrevious: false)
            with { PreviousMonth = prevSnapshot };
    }

    private static IEnumerable<string> SessionFiles(DateTimeOffset since)
    {
        var roots = new[] { CodexPaths.SessionsDir, CodexPaths.ArchivedSessionsDir };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                // A session can contain events from before its last write, so include a day of slack.
                DateTime written;
                try { written = File.GetLastWriteTime(file); }
                catch { continue; }

                if (written >= since.LocalDateTime.AddDays(-1))
                {
                    yield return file;
                }
            }
        }
    }

    private static void ScanFile(
        string path, DateTimeOffset monthStart, DateTimeOffset prevMonthStart,
        MonthAccumulator current, MonthAccumulator previous)
    {
        try
        {
            // Codex may still be writing active session files.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? model = null;
            long prevInput = 0, prevCached = 0, prevOutput = 0;
            var hasPrev = false;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JsonDocument doc;
                try { doc = JsonDocument.Parse(line); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    var type = typeEl.GetString();
                    if (type == "turn_context")
                    {
                        if (root.TryGetProperty("payload", out var p) &&
                            p.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String)
                        {
                            model = m.GetString();
                        }
                        continue;
                    }

                    if (type != "event_msg" ||
                        !root.TryGetProperty("payload", out var payload) ||
                        GetString(payload, "type") != "token_count" ||
                        !payload.TryGetProperty("info", out var info) ||
                        !info.TryGetProperty("total_token_usage", out var total))
                    {
                        continue;
                    }

                    var input = GetLong(total, "input_tokens");
                    var cached = GetLong(total, "cached_input_tokens");
                    var output = GetLong(total, "output_tokens");

                    // Delta vs the previous cumulative total in this session; a drop means a reset.
                    long dInput, dCached, dOutput;
                    if (hasPrev && input >= prevInput && output >= prevOutput)
                    {
                        dInput = input - prevInput;
                        dCached = Math.Max(0, cached - prevCached);
                        dOutput = output - prevOutput;
                    }
                    else
                    {
                        dInput = input;
                        dCached = cached;
                        dOutput = output;
                    }

                    prevInput = input; prevCached = cached; prevOutput = output; hasPrev = true;

                    if (dInput == 0 && dOutput == 0)
                    {
                        continue; // repeated row with only metadata changes
                    }

                    var when = ParseTimestamp(root)?.ToLocalTime();
                    if (when is null)
                    {
                        continue;
                    }

                    var nonCachedInput = Math.Max(0, dInput - dCached);
                    if (when >= monthStart)
                    {
                        current.Add(model, nonCachedInput, dCached, dOutput);
                    }
                    else if (when >= prevMonthStart)
                    {
                        previous.Add(model, nonCachedInput, dCached, dOutput);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed scanning Codex session file {File}", path);
        }
    }

    private static DateTimeOffset? ParseTimestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(ts.GetString(), out var parsed)
            ? parsed
            : null;

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt64(out var l)
            ? l
            : 0;

    /// <summary>Per-model token totals for one month.</summary>
    private sealed class MonthAccumulator
    {
        private readonly Dictionary<string, (long NonCached, long Cached, long Output)> _byModel =
            new(StringComparer.OrdinalIgnoreCase);

        public int Requests { get; private set; }

        public bool HasAny => _byModel.Count > 0;

        public void Add(string? model, long nonCached, long cached, long output)
        {
            var key = string.IsNullOrWhiteSpace(model) ? "unknown" : model;
            _byModel.TryGetValue(key, out var t);
            _byModel[key] = (t.NonCached + nonCached, t.Cached + cached, t.Output + output);
            Requests++;
        }

        public CostUsageSnapshot ToSnapshot(
            CodexModelPricing.Table table, DateTimeOffset start, DateTimeOffset end, bool includePrevious)
        {
            decimal cost = 0;
            long tokens = 0;
            foreach (var (model, t) in _byModel)
            {
                var price = table.Resolve(model);
                cost += (decimal)(
                    t.NonCached / 1_000_000.0 * price.InputPerMillion +
                    t.Cached / 1_000_000.0 * price.CachedInputPerMillion +
                    t.Output / 1_000_000.0 * price.OutputPerMillion);
                tokens += t.NonCached + t.Cached + t.Output;
            }

            return new CostUsageSnapshot(
                MonthCostUsd: Math.Round(cost, 2),
                MonthTokens: tokens,
                MonthRequests: Requests,
                MonthCredits: null,
                MonthStartsAt: start,
                MonthEndsAt: end,
                Days: Array.Empty<CostUsageDay>(),
                Source: table.Source,
                Error: null);
        }
    }
}
