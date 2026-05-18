// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.ViewModels;

/// <summary>
/// Pure calculation helpers for session spending tracking.
/// Extracted from <see cref="MainViewModel"/> to enable unit testing without WPF Dispatcher.
/// </summary>
internal static class SessionSpendingCalculator
{
    /// <summary>
    /// Calculates the session spending display for a credits-based provider.
    /// Returns (spending, shouldResetBaseline, newBaseline).
    /// </summary>
    internal static SessionSpendingResult CalculateCreditsSpending(decimal currentBalance, decimal? baseline)
    {
        if (baseline is null)
        {
            return new SessionSpendingResult("$0.00", SetBaseline: currentBalance);
        }

        if (currentBalance > baseline.Value)
        {
            // Balance increased (top-up) — auto-reset baseline
            return new SessionSpendingResult("$0.00", SetBaseline: currentBalance);
        }

        var spending = baseline.Value - currentBalance;
        return new SessionSpendingResult($"${spending:F2}", SetBaseline: null);
    }

    /// <summary>
    /// Calculates session spending for overage-cost tracking (e.g., Copilot premium).
    /// </summary>
    internal static SessionSpendingResult CalculateOverageSpending(decimal currentOverage, decimal? baseline)
    {
        if (baseline is null)
        {
            return new SessionSpendingResult("$0.00", SetBaseline: currentOverage);
        }

        if (currentOverage < baseline.Value)
        {
            // Overage decreased (monthly quota reset) — auto-reset baseline
            return new SessionSpendingResult("$0.00", SetBaseline: currentOverage);
        }

        var spending = currentOverage - baseline.Value;
        return new SessionSpendingResult($"${spending:F2}", SetBaseline: null);
    }

    /// <summary>
    /// Formats a reset time for display.
    /// </summary>
    internal static string? FormatResetTime(DateTimeOffset? resetTime) =>
        resetTime?.ToLocalTime().ToString("yyyy-MM-dd hh:mm tt");
}

/// <summary>
/// Result of a session spending calculation.
/// </summary>
/// <param name="SpendingText">Formatted spending string (e.g., "$1.23").</param>
/// <param name="SetBaseline">If non-null, the caller should update the baseline to this value.</param>
internal readonly record struct SessionSpendingResult(string SpendingText, decimal? SetBaseline);
