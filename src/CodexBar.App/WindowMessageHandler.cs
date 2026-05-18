// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App;

/// <summary>
/// Pure state machine for window message handling, extracted from <see cref="MainWindow.WndProc"/>
/// to reduce cyclomatic complexity and enable unit testing without a real HWND.
/// </summary>
internal sealed class WindowMessageHandler
{
    internal const int WmNcHitTest = 0x0084;
    internal const int WmEnterSizeMove = 0x0231;
    internal const int WmExitSizeMove = 0x0232;
    internal const int WmSetCursor = 0x0020;
    internal const int WmMove = 0x0003;
    internal const int HtCaption = 2;

    internal bool IsDragging { get; private set; }

    internal int LastDragX { get; private set; }

    internal int LastDragY { get; private set; }

    internal DateTime DragEndedAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Processes a window message and returns the action to take.
    /// Does not perform any Win32 calls itself — the caller handles those.
    /// </summary>
    internal MessageAction HandleMessage(int msg, long lParam)
    {
        switch (msg)
        {
            case WmSetCursor:
                if (DecodeLowWord(lParam) == HtCaption)
                {
                    return MessageAction.SetSizeAllCursor;
                }

                return MessageAction.None;

            case WmEnterSizeMove:
                this.IsDragging = true;
                return MessageAction.DragStarted;

            case WmExitSizeMove:
                this.IsDragging = false;
                this.DragEndedAtUtc = DateTime.UtcNow;
                return MessageAction.DragEnded;

            case WmMove:
                var (x, y) = DecodeSignedCoordinates(lParam);
                if (this.IsDragging)
                {
                    this.LastDragX = x;
                    this.LastDragY = y;
                }

                return MessageAction.None;

            default:
                return MessageAction.None;
        }
    }

    /// <summary>
    /// Determines whether the deactivation should be suppressed based on drag state.
    /// </summary>
    internal bool ShouldSuppressDeactivation(TimeSpan cooldown)
    {
        if (this.IsDragging)
        {
            return true;
        }

        return (DateTime.UtcNow - this.DragEndedAtUtc) < cooldown;
    }

    /// <summary>
    /// Determines whether hiding should be cancelled because the window is active or dragging.
    /// </summary>
    internal bool ShouldCancelHide(bool isWindowActive)
    {
        return isWindowActive || this.IsDragging;
    }

    /// <summary>
    /// Extracts the low 16-bit word from a value, sign-extended.
    /// </summary>
    internal static short DecodeLowWord(long value) => (short)(value & 0xFFFF);

    /// <summary>
    /// Decodes signed screen coordinates from WM_MOVE lParam.
    /// </summary>
    internal static (short X, short Y) DecodeSignedCoordinates(long lParam) =>
        ((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
}

/// <summary>
/// Actions that the caller should take in response to a window message.
/// </summary>
internal enum MessageAction
{
    None,
    SetSizeAllCursor,
    DragStarted,
    DragEnded,
}
