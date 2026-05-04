// <copyright file="UsageBar.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Models;

/// <summary>
/// A labelled usage bar representing one rate-limit window (e.g., "5-hour limit", "Weekly · all models").
/// Providers that expose multiple limits return a list of these so the UI can render one progress bar per window.
/// </summary>
public sealed record UsageBar
{
    /// <summary>Gets label shown to the left of the bar (e.g., "5-hour limit").</summary>
    public required string Label { get; init; }

    /// <summary>Gets usage as a percentage (0.0–1.0).</summary>
    public double UsedPercent { get; init; }

    /// <summary>Gets human-readable reset countdown (e.g., "Resets 2h").</summary>
    public string? ResetDescription { get; init; }

    /// <summary>Gets when the window resets (UTC).</summary>
    public DateTimeOffset? ResetsAt { get; init; }
}
