namespace AiRateLimits.Providers.Codex.Cost;

/// <summary>USD price per 1,000,000 tokens, split by token kind.</summary>
public sealed record ModelPrice(double InputPerMillion, double CachedInputPerMillion, double OutputPerMillion);
