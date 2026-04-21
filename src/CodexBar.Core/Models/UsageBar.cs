namespace CodexBar.Core.Models;

/// <summary>
/// A labelled usage bar representing one rate-limit window (e.g., "5-hour limit", "Weekly · all models").
/// Providers that expose multiple limits return a list of these so the UI can render one progress bar per window.
/// </summary>
public sealed record UsageBar
{
    /// <summary>Label shown to the left of the bar (e.g., "5-hour limit").</summary>
    public required string Label { get; init; }

    /// <summary>Usage as a percentage (0.0–1.0).</summary>
    public double UsedPercent { get; init; }

    /// <summary>Human-readable reset countdown (e.g., "Resets 2h").</summary>
    public string? ResetDescription { get; init; }

    /// <summary>When the window resets (UTC).</summary>
    public DateTimeOffset? ResetsAt { get; init; }
}
