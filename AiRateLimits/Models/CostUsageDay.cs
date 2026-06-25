namespace AiRateLimits.Models;

/// <summary>A single day's estimated cost/usage rollup.</summary>
public sealed record CostUsageDay(
    DateOnly Date,
    decimal CostUsd,
    long Tokens,
    int Requests);
