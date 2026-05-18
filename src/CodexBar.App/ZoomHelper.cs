// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

/// <summary>
/// Pure helper for zoom-level keyboard input decisions.
/// Extracted from <see cref="MainWindow.OnKeyDown"/> to reduce cyclomatic complexity and enable unit testing.
/// </summary>
internal static class ZoomHelper
{
    internal const double ZoomStep = 0.1;
    internal const double MinZoom = 0.5;
    internal const double MaxZoom = 3.0;

    /// <summary>
    /// Determines the new zoom level based on the current zoom and key input.
    /// Returns null if the key combination is not a zoom command.
    /// </summary>
    internal static ZoomResult? EvaluateKeyInput(double currentZoom, bool isCtrlHeld, ZoomKey key)
    {
        if (!isCtrlHeld)
        {
            return null;
        }

        return key switch
        {
            ZoomKey.ZoomIn => new ZoomResult(ClampZoom(currentZoom + ZoomStep)),
            ZoomKey.ZoomOut => new ZoomResult(ClampZoom(currentZoom - ZoomStep)),
            ZoomKey.ResetZoom => new ZoomResult(1.0),
            _ => null,
        };
    }

    /// <summary>
    /// Clamps a zoom value to the allowed range.
    /// </summary>
    internal static double ClampZoom(double zoom) => Math.Clamp(zoom, MinZoom, MaxZoom);
}

/// <summary>
/// Normalized key identifiers for zoom operations.
/// </summary>
internal enum ZoomKey
{
    Other,
    ZoomIn,
    ZoomOut,
    ResetZoom,
}

/// <summary>
/// Result of a zoom input evaluation.
/// </summary>
internal readonly record struct ZoomResult(double NewZoom);
