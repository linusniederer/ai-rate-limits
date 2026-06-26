using System.IO;
using System.Net.Http;
using System.Text.Json;
using Serilog;

namespace AiRateLimits.Providers.Codex.Cost;

/// <summary>
/// Resolves OpenAI model prices, preferring models.dev (cached 24h) and falling back to a built-in
/// table. Prices are USD per 1,000,000 tokens.
/// </summary>
public sealed class CodexModelPricing
{
    private const string ApiUrl = "https://models.dev/api.json";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Built-in fallback (USD per 1M: input / cached input / output). The GPT-5 family shares pricing.
    private static readonly ModelPrice DefaultPrice = new(1.25, 0.125, 10.0);
    private static readonly Dictionary<string, ModelPrice> Builtin = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5-codex"] = new(1.25, 0.125, 10.0),
        ["gpt-5"] = new(1.25, 0.125, 10.0),
        ["gpt-5-mini"] = new(0.25, 0.025, 2.0),
        ["gpt-5-nano"] = new(0.05, 0.005, 0.4),
        ["o4-mini"] = new(1.1, 0.275, 4.4),
    };

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AiRateLimits", "model-pricing", "models-dev-openai-v1.json");

    public sealed record Table(IReadOnlyDictionary<string, ModelPrice> Prices, string Source)
    {
        /// <summary>Resolves a model id to a price: exact models.dev, then built-in family, then default.</summary>
        public ModelPrice Resolve(string? model)
        {
            if (!string.IsNullOrWhiteSpace(model))
            {
                if (Prices.TryGetValue(model, out var exact))
                {
                    return exact;
                }

                foreach (var (key, price) in Builtin)
                {
                    if (model.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                    {
                        return price;
                    }
                }
            }

            return DefaultPrice;
        }
    }

    public async Task<Table> LoadAsync(CancellationToken cancellationToken)
    {
        var cached = TryReadCache();
        if (cached is { } fresh && fresh.Age < CacheTtl)
        {
            return new Table(fresh.Prices, "models.dev (cached)");
        }

        try
        {
            var json = await Http.GetStringAsync(ApiUrl, cancellationToken).ConfigureAwait(false);
            var prices = Parse(json);
            if (prices.Count > 0)
            {
                WriteCache(prices);
                return new Table(prices, "models.dev (live)");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "models.dev price fetch failed");
        }

        if (cached is { } stale)
        {
            return new Table(stale.Prices, "models.dev (cached, stale)");
        }

        return new Table(Builtin, "built-in pricing");
    }

    private static Dictionary<string, ModelPrice> Parse(string json)
    {
        var result = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("openai", out var openai) ||
            !openai.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var model in models.EnumerateObject())
        {
            if (!model.Value.TryGetProperty("cost", out var cost) || cost.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var input = GetDouble(cost, "input");
            var output = GetDouble(cost, "output");
            if (input is null || output is null)
            {
                continue;
            }

            var cacheRead = GetDouble(cost, "cache_read") ?? input.Value * 0.1;
            result[model.Name] = new ModelPrice(input.Value, cacheRead, output.Value);
        }

        return result;
    }

    private sealed record CacheEntry(IReadOnlyDictionary<string, ModelPrice> Prices, TimeSpan Age);

    private static CacheEntry? TryReadCache()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = doc.RootElement;
            var fetchedAt = root.GetProperty("fetchedAt").GetInt64();
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(fetchedAt);

            var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in root.GetProperty("prices").EnumerateObject())
            {
                prices[p.Name] = new ModelPrice(
                    p.Value.GetProperty("input").GetDouble(),
                    p.Value.GetProperty("cached").GetDouble(),
                    p.Value.GetProperty("output").GetDouble());
            }

            return new CacheEntry(prices, age);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(IReadOnlyDictionary<string, ModelPrice> prices)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            using var stream = File.Create(CachePath);
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            writer.WriteNumber("fetchedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteStartObject("prices");
            foreach (var (model, price) in prices)
            {
                writer.WriteStartObject(model);
                writer.WriteNumber("input", price.InputPerMillion);
                writer.WriteNumber("cached", price.CachedInputPerMillion);
                writer.WriteNumber("output", price.OutputPerMillion);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not write models.dev price cache");
        }
    }

    private static double? GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var d)
            ? d
            : null;
}
