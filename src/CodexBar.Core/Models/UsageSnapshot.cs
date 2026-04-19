namespace CodexBar.Core.Models;

/// <summary>
/// A point-in-time snapshot of a provider's usage state.
/// </summary>
public sealed record UsageSnapshot
{
    /// <summary>Current usage as a percentage (0.0–1.0).</summary>
    public double UsedPercent { get; init; }

    /// <summary>Human-readable usage label (e.g., "45 / 100 requests").</summary>
    public string? UsageLabel { get; init; }

    /// <summary>When the current usage window resets.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Human-readable reset description (e.g., "Resets in 2h 15m").</summary>
    public string? ResetDescription { get; init; }

    /// <summary>When this snapshot was captured.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}
