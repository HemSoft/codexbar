// <copyright file="CopilotQuotaSnapshotTests.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Tests;

using CodexBar.Core.Models;

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
