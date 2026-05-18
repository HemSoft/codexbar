// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

public sealed class ZoomHelperTests
{
    // --- EvaluateKeyInput ---
    [Fact]
    public void EvaluateKeyInput_CtrlZoomIn_ReturnsIncreasedZoom()
    {
        var result = ZoomHelper.EvaluateKeyInput(1.0, isCtrlHeld: true, ZoomKey.ZoomIn);

        Assert.NotNull(result);
        Assert.Equal(1.1, result.Value.NewZoom, 5);
    }

    [Fact]
    public void EvaluateKeyInput_CtrlZoomOut_ReturnsDecreasedZoom()
    {
        var result = ZoomHelper.EvaluateKeyInput(1.0, isCtrlHeld: true, ZoomKey.ZoomOut);

        Assert.NotNull(result);
        Assert.Equal(0.9, result.Value.NewZoom, 5);
    }

    [Fact]
    public void EvaluateKeyInput_CtrlResetZoom_ReturnsDefaultZoom()
    {
        var result = ZoomHelper.EvaluateKeyInput(2.5, isCtrlHeld: true, ZoomKey.ResetZoom);

        Assert.NotNull(result);
        Assert.Equal(1.0, result.Value.NewZoom);
    }

    [Fact]
    public void EvaluateKeyInput_NoCtrl_ReturnsNull()
    {
        var result = ZoomHelper.EvaluateKeyInput(1.0, isCtrlHeld: false, ZoomKey.ZoomIn);

        Assert.Null(result);
    }

    [Fact]
    public void EvaluateKeyInput_CtrlOtherKey_ReturnsNull()
    {
        var result = ZoomHelper.EvaluateKeyInput(1.0, isCtrlHeld: true, ZoomKey.Other);

        Assert.Null(result);
    }

    [Fact]
    public void EvaluateKeyInput_ZoomIn_ClampsAtMaximum()
    {
        var result = ZoomHelper.EvaluateKeyInput(3.0, isCtrlHeld: true, ZoomKey.ZoomIn);

        Assert.NotNull(result);
        Assert.Equal(ZoomHelper.MaxZoom, result.Value.NewZoom);
    }

    [Fact]
    public void EvaluateKeyInput_ZoomOut_ClampsAtMinimum()
    {
        var result = ZoomHelper.EvaluateKeyInput(0.5, isCtrlHeld: true, ZoomKey.ZoomOut);

        Assert.NotNull(result);
        Assert.Equal(ZoomHelper.MinZoom, result.Value.NewZoom);
    }

    [Fact]
    public void EvaluateKeyInput_ZoomInNearMax_DoesNotExceedMax()
    {
        var result = ZoomHelper.EvaluateKeyInput(2.95, isCtrlHeld: true, ZoomKey.ZoomIn);

        Assert.NotNull(result);
        Assert.Equal(ZoomHelper.MaxZoom, result.Value.NewZoom);
    }

    [Fact]
    public void EvaluateKeyInput_ZoomOutNearMin_DoesNotGoBelowMin()
    {
        var result = ZoomHelper.EvaluateKeyInput(0.55, isCtrlHeld: true, ZoomKey.ZoomOut);

        Assert.NotNull(result);
        Assert.Equal(ZoomHelper.MinZoom, result.Value.NewZoom, 5);
    }

    // --- ClampZoom ---
    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.3, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(3.0, 3.0)]
    [InlineData(3.5, 3.0)]
    [InlineData(10.0, 3.0)]
    public void ClampZoom_VariousInputs_ClampsToValidRange(double input, double expected)
    {
        Assert.Equal(expected, ZoomHelper.ClampZoom(input));
    }

    // --- Constants ---
    [Fact]
    public void Constants_WhenAccessed_HaveExpectedValues()
    {
        Assert.Equal(0.1, ZoomHelper.ZoomStep);
        Assert.Equal(0.5, ZoomHelper.MinZoom);
        Assert.Equal(3.0, ZoomHelper.MaxZoom);
    }
}
