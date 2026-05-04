// <copyright file="UsageSnapshot.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Models;

/// <summary>
/// A point-in-time snapshot of a provider's usage state.
/// </summary>
public sealed record UsageSnapshot
{
    /// <summary>Gets current usage as a percentage (0.0–1.0).</summary>
    public double UsedPercent { get; init; }

    /// <summary>Gets human-readable usage label (e.g., "45 / 100 requests").</summary>
    public string? UsageLabel { get; init; }

    /// <summary>Gets when the current usage window resets.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Gets human-readable reset description (e.g., "Resets in 2h 15m").</summary>
    public string? ResetDescription { get; init; }

    /// <summary>Gets when this snapshot was captured.</summary>
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets a value indicating whether indicates the quota is unlimited — percentage/progress display should be suppressed.
    /// </summary>
    public bool IsUnlimited { get; init; }
}
