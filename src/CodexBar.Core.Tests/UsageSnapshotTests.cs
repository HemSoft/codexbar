using CodexBar.Core.Models;

namespace CodexBar.Core.Tests;

public class UsageSnapshotTests
{
    [Fact]
    public void Defaults_AreCorrect()
    {
        var snap = new UsageSnapshot();

        Assert.Equal(0.0, snap.UsedPercent);
        Assert.Null(snap.UsageLabel);
        Assert.Null(snap.ResetsAt);
        Assert.Null(snap.ResetDescription);
        Assert.False(snap.IsUnlimited);
        Assert.True(snap.CapturedAt <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void UsageLabel_TruncatesLongValues()
    {
        var snap = new UsageSnapshot { UsageLabel = "a very long label that exceeds 50 chars easily done now" };
        Assert.Contains("exceeds", snap.UsageLabel);
    }
}
