namespace CodexBar.Core.Models;

/// <summary>
/// A single usage item within a provider result. Providers that track multiple
/// subjects (e.g., Copilot with multiple GitHub accounts) return one item per subject.
/// Single-subject providers may return one item or leave
/// <see cref="ProviderUsageResult.Items"/> null and use the top-level snapshots instead.
/// </summary>
public sealed record UsageItem
{
    /// <summary>Stable key for reconciliation (e.g., "gemini", "copilot:HemSoft").</summary>
    public required string Key { get; init; }

    /// <summary>Display name shown on the card (e.g., "Copilot · HemSoft").</summary>
    public required string DisplayName { get; init; }

    /// <summary>Primary usage snapshot (e.g., premium interactions).</summary>
    public UsageSnapshot? PrimaryUsage { get; init; }

    /// <summary>Optional secondary usage snapshot (e.g., chat or weekly).</summary>
    public UsageSnapshot? SecondaryUsage { get; init; }

    /// <summary>Credit balance if this item uses a credit system.</summary>
    public decimal? CreditsRemaining { get; init; }

    /// <summary>Whether this item fetched successfully.</summary>
    public bool Success { get; init; } = true;

    /// <summary>Error message if this item failed.</summary>
    public string? ErrorMessage { get; init; }
}
