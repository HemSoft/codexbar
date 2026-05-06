// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

/// <summary>
/// Converts null/empty string to Collapsed, non-null to Visible.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public static readonly NullToCollapsedConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrWhiteSpace(value?.ToString()) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Converts true → Collapsed, false → Visible (inverse of BooleanToVisibilityConverter).
/// </summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public static readonly InverseBoolToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Converts progress (0-1) into a circular arc geometry sized for the title-bar refresh indicator.
/// </summary>
public sealed class ProgressToArcGeometryConverter : IValueConverter
{
    private const double DefaultSize = 14;
    private const double StrokeThickness = 1.5;

    public static readonly ProgressToArcGeometryConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = Math.Clamp(System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture), 0, 1);
        if (progress <= 0)
        {
            return Geometry.Empty;
        }

        var size = parameter is string rawSize
            && double.TryParse(rawSize, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSize)
            ? parsedSize
            : DefaultSize;

        var center = size / 2d;
        var radius = center - (StrokeThickness / 2d);
        const double startAngleDegrees = -90d;
        var endAngleDegrees = startAngleDegrees + (Math.Min(progress, 0.9999d) * 359.964d);

        var startPoint = PointOnCircle(center, radius, startAngleDegrees);
        var endPoint = PointOnCircle(center, radius, endAngleDegrees);

        var figure = new PathFigure
        {
            StartPoint = startPoint,
            IsClosed = false,
            IsFilled = false,
        };

        figure.Segments.Add(new ArcSegment
        {
            Point = endPoint,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = progress >= 0.5d,
        });

        return new PathGeometry([figure]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Point PointOnCircle(double center, double radius, double angleDegrees)
    {
        var angleRadians = angleDegrees * (Math.PI / 180d);
        return new Point(
            center + (radius * Math.Cos(angleRadians)),
            center + (radius * Math.Sin(angleRadians)));
    }
}

/// <summary>
/// Converts refresh progress (0-1) into a subtle green-to-red brush.
/// </summary>
public sealed class RefreshProgressToBrushConverter : IValueConverter
{
    private static readonly Color StartColor = Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly Color EndColor = Color.FromRgb(0xEF, 0x44, 0x44);

    public static readonly RefreshProgressToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = Math.Clamp(System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture), 0, 1);
        var brush = new SolidColorBrush(Interpolate(StartColor, EndColor, progress));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Color Interpolate(Color start, Color end, double progress) =>
        Color.FromRgb(
            (byte)(start.R + ((end.R - start.R) * progress)),
            (byte)(start.G + ((end.G - start.G) * progress)),
            (byte)(start.B + ((end.B - start.B) * progress)));
}
