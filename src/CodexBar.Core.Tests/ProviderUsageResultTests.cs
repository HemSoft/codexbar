using CodexBar.Core.Models;

namespace CodexBar.Core.Tests;

public class ProviderUsageResultTests
{
    [Fact]
    public void Failure_SetsProviderAndError()
    {
        var result = ProviderUsageResult.Failure(ProviderId.OpenRouter, "api key missing");

        Assert.Equal(ProviderId.OpenRouter, result.Provider);
        Assert.False(result.Success);
        Assert.Equal("api key missing", result.ErrorMessage);
        Assert.True(result.FetchedAt <= DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData(ProviderId.Copilot)]
    [InlineData(ProviderId.Claude)]
    [InlineData(ProviderId.OpenCodeGo)]
    [InlineData(ProviderId.OpenRouter)]
    public void Failure_AcceptsAllProviders(ProviderId id)
    {
        var result = ProviderUsageResult.Failure(id, "error");
        Assert.Equal(id, result.Provider);
    }

    [Fact]
    public void EmptySuccess_SetsSuccess()
    {
        var result = ProviderUsageResult.EmptySuccess(ProviderId.Claude);

        Assert.Equal(ProviderId.Claude, result.Provider);
        Assert.True(result.Success);
        Assert.Null(result.ErrorMessage);
    }
}
