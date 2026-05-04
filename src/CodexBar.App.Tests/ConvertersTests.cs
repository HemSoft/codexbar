using System.Globalization;
using System.Windows;

namespace CodexBar.App.Tests;

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
}
