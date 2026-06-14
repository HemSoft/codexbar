namespace CodexBar.App.Tests;

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CodexBar.App;

public sealed class ConvertersTests
{
    [Fact]
    public void NullToCollapsedConverter_NullValue_ReturnsCollapsed()
    {
        var converter = NullToCollapsedConverter.Instance;
        var result = converter.Convert(null, typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NullToCollapsedConverter_EmptyString_ReturnsCollapsed()
    {
        var converter = NullToCollapsedConverter.Instance;
        var result = converter.Convert(string.Empty, typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void NullToCollapsedConverter_NonEmptyString_ReturnsVisible()
    {
        var converter = NullToCollapsedConverter.Instance;
        var result = converter.Convert("hello", typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void NullToCollapsedConverter_ConvertBack_Throws()
    {
        var converter = NullToCollapsedConverter.Instance;

        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Visibility.Visible, typeof(Visibility), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NullToCollapsedConverter_WhitespaceOnly_ReturnsCollapsed()
    {
        var converter = NullToCollapsedConverter.Instance;
        var result = converter.Convert("   ", typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void InverseBoolToVisibilityConverter_True_ReturnsCollapsed()
    {
        var converter = InverseBoolToVisibilityConverter.Instance;
        var result = converter.Convert(true, typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Collapsed, result);
    }

    [Fact]
    public void InverseBoolToVisibilityConverter_False_ReturnsVisible()
    {
        var converter = InverseBoolToVisibilityConverter.Instance;
        var result = converter.Convert(false, typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void InverseBoolToVisibilityConverter_NonBoolValue_ReturnsVisible()
    {
        var converter = InverseBoolToVisibilityConverter.Instance;
        var result = converter.Convert("not a bool", typeof(Visibility), null, CultureInfo.InvariantCulture);

        Assert.Equal(Visibility.Visible, result);
    }

    [Fact]
    public void InverseBoolToVisibilityConverter_ConvertBack_Throws()
    {
        var converter = InverseBoolToVisibilityConverter.Instance;

        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Visibility.Visible, typeof(bool), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ProgressToArcGeometryConverter_ZeroProgress_ReturnsEmptyGeometry()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(0d, typeof(Geometry), "14", CultureInfo.InvariantCulture);

        Assert.Same(Geometry.Empty, result);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_HalfProgress_ReturnsArcGeometry()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(0.5d, typeof(Geometry), "14", CultureInfo.InvariantCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        Assert.Single(geometry.Figures);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_NegativeProgress_ReturnsEmptyGeometry()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(-0.5d, typeof(Geometry), "14", CultureInfo.InvariantCulture);

        Assert.Same(Geometry.Empty, result);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_ProgressAboveOne_ClampsToFullArc()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(1.5d, typeof(Geometry), "14", CultureInfo.InvariantCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        Assert.Single(geometry.Figures);
        var arcSegment = Assert.IsType<ArcSegment>(geometry.Figures[0].Segments[0]);
        Assert.True(arcSegment.IsLargeArc);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_SmallProgress_NoLargeArc()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(0.3d, typeof(Geometry), "14", CultureInfo.InvariantCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        var arcSegment = Assert.IsType<ArcSegment>(geometry.Figures[0].Segments[0]);
        Assert.False(arcSegment.IsLargeArc);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_ExactlyHalf_IsLargeArc()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(0.5d, typeof(Geometry), "14", CultureInfo.InvariantCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        var arcSegment = Assert.IsType<ArcSegment>(geometry.Figures[0].Segments[0]);
        Assert.True(arcSegment.IsLargeArc);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_NullParameter_UsesDefaultSize()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(0.5d, typeof(Geometry), null, CultureInfo.InvariantCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        Assert.Single(geometry.Figures);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_NonNumericParameter_UsesDefaultSize()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(0.5d, typeof(Geometry), "notanumber", CultureInfo.InvariantCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        Assert.Single(geometry.Figures);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_NullValue_ReturnsEmptyGeometry()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(null, typeof(Geometry), "14", CultureInfo.InvariantCulture);

        Assert.Same(Geometry.Empty, result);
    }

    [Fact]
    public void ProgressToArcGeometryConverter_ConvertBack_Throws()
    {
        var converter = ProgressToArcGeometryConverter.Instance;

        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Geometry.Empty, typeof(double), null, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ProgressToArcGeometryConverter_CustomSize_ProducesArc()
    {
        var converter = ProgressToArcGeometryConverter.Instance;
        var result = converter.Convert(0.75d, typeof(Geometry), "20", CultureInfo.InvariantCulture);

        var geometry = Assert.IsType<PathGeometry>(result);
        Assert.Single(geometry.Figures);
        Assert.False(geometry.Figures[0].IsClosed);
        Assert.False(geometry.Figures[0].IsFilled);
    }

    [Fact]
    public void RefreshProgressToBrushConverter_InterpolatesFromGreenToRed()
    {
        var converter = RefreshProgressToBrushConverter.Instance;
        var startBrush = Assert.IsType<SolidColorBrush>(converter.Convert(0d, typeof(Brush), null, CultureInfo.InvariantCulture));
        var endBrush = Assert.IsType<SolidColorBrush>(converter.Convert(1d, typeof(Brush), null, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(0x22, 0xC5, 0x5E), startBrush.Color);
        Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), endBrush.Color);
    }

    [Fact]
    public void RefreshProgressToBrushConverter_MidpointProgress_InterpolatesColor()
    {
        var converter = RefreshProgressToBrushConverter.Instance;
        var midBrush = Assert.IsType<SolidColorBrush>(converter.Convert(0.5d, typeof(Brush), null, CultureInfo.InvariantCulture));

        // Midpoint between (0x22, 0xC5, 0x5E) and (0xEF, 0x44, 0x44)
        var expectedR = (byte)(0x22 + ((0xEF - 0x22) * 0.5));
        var expectedG = (byte)(0xC5 + ((0x44 - 0xC5) * 0.5));
        var expectedB = (byte)(0x5E + ((0x44 - 0x5E) * 0.5));

        Assert.Equal(expectedR, midBrush.Color.R);
        Assert.Equal(expectedG, midBrush.Color.G);
        Assert.Equal(expectedB, midBrush.Color.B);
    }

    [Fact]
    public void RefreshProgressToBrushConverter_NullValue_TreatedAsZero()
    {
        var converter = RefreshProgressToBrushConverter.Instance;
        var brush = Assert.IsType<SolidColorBrush>(converter.Convert(null, typeof(Brush), null, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(0x22, 0xC5, 0x5E), brush.Color);
    }

    [Fact]
    public void RefreshProgressToBrushConverter_BrushIsFrozen()
    {
        var converter = RefreshProgressToBrushConverter.Instance;
        var brush = Assert.IsType<SolidColorBrush>(converter.Convert(0.5d, typeof(Brush), null, CultureInfo.InvariantCulture));

        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void RefreshProgressToBrushConverter_ConvertBack_Throws()
    {
        var converter = RefreshProgressToBrushConverter.Instance;

        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Brushes.Red, typeof(double), null, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(0.69, 0x22, 0xC5, 0x5E)]
    [InlineData(0.70, 0xEA, 0xB3, 0x08)]
    [InlineData(0.80, 0xEF, 0x44, 0x44)]
    public void UsagePercentToBrushConverter_Actual_UsesGreenYellowRedThresholds(
        double progress,
        byte red,
        byte green,
        byte blue)
    {
        var converter = UsagePercentToBrushConverter.Instance;
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(progress, typeof(Brush), null, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(red, green, blue), brush.Color);
    }

    [Theory]
    [InlineData(0.69, 0x86, 0xEF, 0xAC)]
    [InlineData(0.70, 0xFA, 0xCC, 0x15)]
    [InlineData(0.80, 0xF8, 0x71, 0x71)]
    public void UsagePercentToBrushConverter_Projected_UsesLighterGreenYellowRedThresholds(
        double progress,
        byte red,
        byte green,
        byte blue)
    {
        var converter = UsagePercentToBrushConverter.Instance;
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(progress, typeof(Brush), "Projected", CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(red, green, blue), brush.Color);
    }

    [Theory]
    [InlineData(-0.10, false, 0x22, 0xC5, 0x5E)]
    [InlineData(1.10, false, 0xEF, 0x44, 0x44)]
    [InlineData(-0.10, true, 0x86, 0xEF, 0xAC)]
    [InlineData(1.10, true, 0xF8, 0x71, 0x71)]
    public void UsagePercentToBrushConverter_OutOfRangeProgress_ClampsToValidRange(
        double progress,
        bool projected,
        byte red,
        byte green,
        byte blue)
    {
        var converter = UsagePercentToBrushConverter.Instance;
        var parameter = projected ? "Projected" : null;
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(progress, typeof(Brush), parameter, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(red, green, blue), brush.Color);
    }

    [Theory]
    [InlineData("projected")]
    [InlineData("PROJECTED")]
    [InlineData("pRoJeCtEd")]
    public void UsagePercentToBrushConverter_ProjectedParameter_IsCaseInsensitive(string parameter)
    {
        var converter = UsagePercentToBrushConverter.Instance;
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(0.80, typeof(Brush), parameter, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(0xF8, 0x71, 0x71), brush.Color);
    }

    [Fact]
    public void UsagePercentToBrushConverter_BrushIsFrozen()
    {
        var converter = UsagePercentToBrushConverter.Instance;
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(0.5d, typeof(Brush), null, CultureInfo.InvariantCulture));

        Assert.True(brush.IsFrozen);
    }

    [Fact]
    public void UsagePercentToBrushConverter_ConvertBack_Throws()
    {
        var converter = UsagePercentToBrushConverter.Instance;

        Assert.Throws<NotSupportedException>(() =>
            converter.ConvertBack(Brushes.Red, typeof(double), null, CultureInfo.InvariantCulture));
    }
}
