using CodexBar.Core.Models;
using Xunit;

namespace CodexBar.Core.Tests;

public class UsageBarTests
{
    [Fact]
    public void Create_SetsProperties()
    {
        var bar = new UsageBar { Label = "Test", UsedPercent = 0.5, ResetDescription = "2h", ResetsAt = DateTimeOffset.UtcNow };
        Assert.Equal("Test", bar.Label);
        Assert.Equal(0.5, bar.UsedPercent);
        Assert.Equal("2h", bar.ResetDescription);
        Assert.NotNull(bar.ResetsAt);
    }
}
