// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using CodexBar.App.ViewModels;

public sealed class SessionSpendingCalculatorTests
{
    // --- CalculateCreditsSpending ---
    [Fact]
    public void CalculateCreditsSpending_NoBaseline_SetsBaselineAndReturnsZero()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 25.00m, baseline: null);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Equal(25.00m, result.SetBaseline);
    }

    [Fact]
    public void CalculateCreditsSpending_BalanceDecreased_ShowsSpending()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 22.50m, baseline: 25.00m);

        Assert.Equal("$2.50", result.SpendingText);
        Assert.Null(result.SetBaseline);
    }

    [Fact]
    public void CalculateCreditsSpending_BalanceUnchanged_ShowsZeroSpending()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 25.00m, baseline: 25.00m);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Null(result.SetBaseline);
    }

    [Fact]
    public void CalculateCreditsSpending_BalanceIncreased_ResetsBaseline()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 30.00m, baseline: 25.00m);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Equal(30.00m, result.SetBaseline);
    }

    [Fact]
    public void CalculateCreditsSpending_SmallSpending_FormatsCorrectly()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 24.99m, baseline: 25.00m);

        Assert.Equal("$0.01", result.SpendingText);
    }

    [Fact]
    public void CalculateCreditsSpending_LargeSpending_FormatsCorrectly()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 0.00m, baseline: 100.00m);

        Assert.Equal("$100.00", result.SpendingText);
    }

    // --- CalculateOverageSpending ---
    [Fact]
    public void CalculateOverageSpending_NoBaseline_SetsBaselineAndReturnsZero()
    {
        var result = SessionSpendingCalculator.CalculateOverageSpending(currentOverage: 5.00m, baseline: null);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Equal(5.00m, result.SetBaseline);
    }

    [Fact]
    public void CalculateOverageSpending_OverageIncreased_ShowsSpending()
    {
        var result = SessionSpendingCalculator.CalculateOverageSpending(currentOverage: 8.50m, baseline: 5.00m);

        Assert.Equal("$3.50", result.SpendingText);
        Assert.Null(result.SetBaseline);
    }

    [Fact]
    public void CalculateOverageSpending_OverageUnchanged_ShowsZero()
    {
        var result = SessionSpendingCalculator.CalculateOverageSpending(currentOverage: 5.00m, baseline: 5.00m);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Null(result.SetBaseline);
    }

    [Fact]
    public void CalculateOverageSpending_OverageDecreased_ResetsBaseline()
    {
        var result = SessionSpendingCalculator.CalculateOverageSpending(currentOverage: 2.00m, baseline: 5.00m);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Equal(2.00m, result.SetBaseline);
    }

    // --- FormatResetTime ---
    [Fact]
    public void FormatResetTime_Null_ReturnsNull()
    {
        Assert.Null(SessionSpendingCalculator.FormatResetTime(null));
    }

    [Fact]
    public void FormatResetTime_ValidTime_ReturnsFormattedString()
    {
        var time = new DateTimeOffset(2026, 5, 15, 14, 30, 0, TimeSpan.Zero);
        var result = SessionSpendingCalculator.FormatResetTime(time);

        var expected = time.ToLocalTime().ToString("yyyy-MM-dd hh:mm tt");
        Assert.Equal(expected, result);
    }

    // --- Mutation resilience: verify direction of comparisons ---
    [Fact]
    public void CalculateCreditsSpending_BalanceDecreasedByOneCent_ShowsOneCent()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 24.99m, baseline: 25.00m);

        Assert.Equal("$0.01", result.SpendingText);
        Assert.Null(result.SetBaseline);
    }

    [Fact]
    public void CalculateCreditsSpending_BalanceIncreasedByOneCent_ResetsBaseline()
    {
        var result = SessionSpendingCalculator.CalculateCreditsSpending(currentBalance: 25.01m, baseline: 25.00m);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Equal(25.01m, result.SetBaseline);
    }

    [Fact]
    public void CalculateOverageSpending_OverageIncreasedByOneCent_ShowsOneCent()
    {
        var result = SessionSpendingCalculator.CalculateOverageSpending(currentOverage: 5.01m, baseline: 5.00m);

        Assert.Equal("$0.01", result.SpendingText);
        Assert.Null(result.SetBaseline);
    }

    [Fact]
    public void CalculateOverageSpending_OverageDecreasedByOneCent_ResetsBaseline()
    {
        var result = SessionSpendingCalculator.CalculateOverageSpending(currentOverage: 4.99m, baseline: 5.00m);

        Assert.Equal("$0.00", result.SpendingText);
        Assert.Equal(4.99m, result.SetBaseline);
    }

    // --- SpendingResult record equality ---
    [Fact]
    public void SessionSpendingResult_EqualValues_AreEqual()
    {
        var a = new SessionSpendingResult("$1.00", 10m);
        var b = new SessionSpendingResult("$1.00", 10m);

        Assert.Equal(a, b);
    }

    [Fact]
    public void SessionSpendingResult_DifferentValues_AreNotEqual()
    {
        var a = new SessionSpendingResult("$1.00", 10m);
        var b = new SessionSpendingResult("$2.00", 10m);

        Assert.NotEqual(a, b);
    }
}
