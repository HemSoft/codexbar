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
    public void RefreshProgressToBrushConverter_InterpolatesFromGreenToRed()
    {
        var converter = RefreshProgressToBrushConverter.Instance;
        var startBrush = Assert.IsType<SolidColorBrush>(converter.Convert(0d, typeof(Brush), null, CultureInfo.InvariantCulture));
        var endBrush = Assert.IsType<SolidColorBrush>(converter.Convert(1d, typeof(Brush), null, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(0x22, 0xC5, 0x5E), startBrush.Color);
        Assert.Equal(Color.FromRgb(0xEF, 0x44, 0x44), endBrush.Color);
    }
}
