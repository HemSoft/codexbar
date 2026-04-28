using CodexBar.Core.Models;

namespace CodexBar.Core.Tests;

public class CopilotQuotaSnapshotTests
{
    [Fact]
    public void Defaults_AreZero()
    {
        var q = new CopilotQuotaSnapshot();

        Assert.Equal(0, q.Entitlement);
        Assert.Equal(0, q.Remaining);
        Assert.Equal(0, q.OverageCount);
        Assert.False(q.OveragePermitted);
        Assert.Equal(0.0, q.PercentRemaining);
        Assert.False(q.Unlimited);
        Assert.Null(q.QuotaId);
        Assert.Null(q.TimestampUtc);
    }
}
