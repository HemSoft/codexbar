// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

public sealed class WindowMessageHandlerTests
{
    private readonly WindowMessageHandler _sut = new();

    // --- WM_ENTERSIZEMOVE ---
    [Fact]
    public void HandleMessage_EnterSizeMove_SetsDraggingTrue()
    {
        var action = this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);

        Assert.Equal(MessageAction.DragStarted, action);
        Assert.True(this._sut.IsDragging);
    }

    // --- WM_EXITSIZEMOVE ---
    [Fact]
    public void HandleMessage_ExitSizeMove_SetsDraggingFalse()
    {
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);
        var action = this._sut.HandleMessage(WindowMessageHandler.WmExitSizeMove, 0);

        Assert.Equal(MessageAction.DragEnded, action);
        Assert.False(this._sut.IsDragging);
    }

    [Fact]
    public void HandleMessage_ExitSizeMove_SetsDragEndedTime()
    {
        var before = DateTime.UtcNow;
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);
        this._sut.HandleMessage(WindowMessageHandler.WmExitSizeMove, 0);

        Assert.True(this._sut.DragEndedAtUtc >= before);
        Assert.True(this._sut.DragEndedAtUtc <= DateTime.UtcNow);
    }

    // --- WM_MOVE while dragging ---
    [Fact]
    public void HandleMessage_MoveWhileDragging_TracksDragCoordinates()
    {
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);

        long lParam = EncodeLParam(100, 200);
        this._sut.HandleMessage(WindowMessageHandler.WmMove, lParam);

        Assert.Equal(100, this._sut.LastDragX);
        Assert.Equal(200, this._sut.LastDragY);
    }

    [Fact]
    public void HandleMessage_MoveNotDragging_DoesNotUpdateDragCoordinates()
    {
        long lParam = EncodeLParam(100, 200);
        this._sut.HandleMessage(WindowMessageHandler.WmMove, lParam);

        Assert.Equal(0, this._sut.LastDragX);
        Assert.Equal(0, this._sut.LastDragY);
    }

    [Fact]
    public void HandleMessage_MoveWithNegativeCoordinates_DecodesCorrectly()
    {
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);

        long lParam = EncodeLParam(-100, -200);
        this._sut.HandleMessage(WindowMessageHandler.WmMove, lParam);

        Assert.Equal(-100, this._sut.LastDragX);
        Assert.Equal(-200, this._sut.LastDragY);
    }

    // --- WM_SETCURSOR ---
    [Fact]
    public void HandleMessage_SetCursorOnCaption_ReturnsSizeAllAction()
    {
        long lParam = WindowMessageHandler.HtCaption; // HT_CAPTION in low word
        var action = this._sut.HandleMessage(WindowMessageHandler.WmSetCursor, lParam);

        Assert.Equal(MessageAction.SetSizeAllCursor, action);
    }

    [Fact]
    public void HandleMessage_SetCursorNotOnCaption_ReturnsNone()
    {
        long lParam = 1; // HTCLIENT
        var action = this._sut.HandleMessage(WindowMessageHandler.WmSetCursor, lParam);

        Assert.Equal(MessageAction.None, action);
    }

    // --- Unknown message ---
    [Fact]
    public void HandleMessage_UnknownMessage_ReturnsNone()
    {
        var action = this._sut.HandleMessage(0x9999, 0);

        Assert.Equal(MessageAction.None, action);
    }

    // --- Full drag sequence ---
    [Fact]
    public void DragSequence_EnterMoveExit_TracksLastPosition()
    {
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);
        this._sut.HandleMessage(WindowMessageHandler.WmMove, EncodeLParam(50, 60));
        this._sut.HandleMessage(WindowMessageHandler.WmMove, EncodeLParam(150, 250));
        this._sut.HandleMessage(WindowMessageHandler.WmExitSizeMove, 0);

        Assert.False(this._sut.IsDragging);
        Assert.Equal(150, this._sut.LastDragX);
        Assert.Equal(250, this._sut.LastDragY);
    }

    // --- ShouldSuppressDeactivation ---
    [Fact]
    public void ShouldSuppressDeactivation_WhileDragging_ReturnsTrue()
    {
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);

        Assert.True(this._sut.ShouldSuppressDeactivation(TimeSpan.FromMilliseconds(500)));
    }

    [Fact]
    public void ShouldSuppressDeactivation_WithinCooldown_ReturnsTrue()
    {
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);
        this._sut.HandleMessage(WindowMessageHandler.WmExitSizeMove, 0);

        // Should be within cooldown since we just ended the drag
        Assert.True(this._sut.ShouldSuppressDeactivation(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void ShouldSuppressDeactivation_NoDragHistory_ReturnsFalse()
    {
        Assert.False(this._sut.ShouldSuppressDeactivation(TimeSpan.FromMilliseconds(500)));
    }

    // --- ShouldCancelHide ---
    [Fact]
    public void ShouldCancelHide_WindowActive_ReturnsTrue()
    {
        Assert.True(this._sut.ShouldCancelHide(isWindowActive: true));
    }

    [Fact]
    public void ShouldCancelHide_Dragging_ReturnsTrue()
    {
        this._sut.HandleMessage(WindowMessageHandler.WmEnterSizeMove, 0);

        Assert.True(this._sut.ShouldCancelHide(isWindowActive: false));
    }

    [Fact]
    public void ShouldCancelHide_InactiveAndNotDragging_ReturnsFalse()
    {
        Assert.False(this._sut.ShouldCancelHide(isWindowActive: false));
    }

    // --- DecodeLowWord ---
    [Theory]
    [InlineData(0x00020001L, 1)]
    [InlineData(0x0000FFFFL, -1)]
    [InlineData(0x00000002L, 2)]
    [InlineData(0L, 0)]
    public void DecodeLowWord_ReturnsSignedLow16Bits(long input, short expected)
    {
        Assert.Equal(expected, WindowMessageHandler.DecodeLowWord(input));
    }

    // --- DecodeSignedCoordinates ---
    [Fact]
    public void DecodeSignedCoordinates_PositiveValues()
    {
        long lParam = EncodeLParam(100, 200);
        var (x, y) = WindowMessageHandler.DecodeSignedCoordinates(lParam);

        Assert.Equal(100, x);
        Assert.Equal(200, y);
    }

    [Fact]
    public void DecodeSignedCoordinates_NegativeValues()
    {
        long lParam = EncodeLParam(-100, -200);
        var (x, y) = WindowMessageHandler.DecodeSignedCoordinates(lParam);

        Assert.Equal(-100, x);
        Assert.Equal(-200, y);
    }

    [Fact]
    public void DecodeSignedCoordinates_MixedValues()
    {
        long lParam = EncodeLParam(-1920, 1080);
        var (x, y) = WindowMessageHandler.DecodeSignedCoordinates(lParam);

        Assert.Equal(-1920, x);
        Assert.Equal(1080, y);
    }

    private static long EncodeLParam(short x, short y) =>
        (long)(ushort)x | ((long)(ushort)y << 16);
}
