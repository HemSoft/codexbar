// <copyright file="ProviderUsageResult.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Models;

/// <summary>
/// The result of a provider usage fetch operation.
/// </summary>
public sealed record ProviderUsageResult
{
    public required ProviderId Provider { get; init; }

    public required bool Success { get; init; }

    /// <summary>Gets session/short-window usage (e.g., quota window for Gemini).</summary>
    public UsageSnapshot? SessionUsage { get; init; }

    /// <summary>Gets weekly/long-window usage.</summary>
    public UsageSnapshot? WeeklyUsage { get; init; }

    /// <summary>Gets credit balance if the provider uses credits.</summary>
    public decimal? CreditsRemaining { get; init; }

    /// <summary>Gets error message if the fetch failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets when this result was fetched.</summary>
    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets per-subject usage items. Multi-account providers (e.g., Copilot) return
    /// one item per account. Single-account providers return one item or leave null
    /// (in which case <see cref="SessionUsage"/>/<see cref="WeeklyUsage"/> are used).
    /// </summary>
    public IReadOnlyList<UsageItem>? Items { get; init; }

    public static ProviderUsageResult Failure(ProviderId provider, string error) =>
        new()
        {
            Provider = provider,
            Success = false,
            ErrorMessage = error,
        };

    public static ProviderUsageResult EmptySuccess(ProviderId provider) =>
        new()
        {
            Provider = provider,
            Success = true,
        };
}
