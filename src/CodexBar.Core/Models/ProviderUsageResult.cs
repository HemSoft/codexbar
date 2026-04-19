namespace CodexBar.Core.Models;

/// <summary>
/// The result of a provider usage fetch operation.
/// </summary>
public sealed record ProviderUsageResult
{
    public required ProviderId Provider { get; init; }
    public required bool Success { get; init; }

    /// <summary>Session/short-window usage (e.g., 5-hour window for Claude).</summary>
    public UsageSnapshot? SessionUsage { get; init; }

    /// <summary>Weekly/long-window usage.</summary>
    public UsageSnapshot? WeeklyUsage { get; init; }

    /// <summary>Credit balance if the provider uses credits.</summary>
    public decimal? CreditsRemaining { get; init; }

    /// <summary>Error message if the fetch failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When this result was fetched.</summary>
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;

    public static ProviderUsageResult Failure(ProviderId provider, string error) =>
        new()
        {
            Provider = provider,
            Success = false,
            ErrorMessage = error
        };
}
