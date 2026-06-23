// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class CopilotProviderTests
{
    [Fact]
    public void FormatDisplayName_WithPlan_ReturnsLabel()
    {
        var name = CopilotProvider.FormatDisplayName("user", "enterprise");
        Assert.Equal("Copilot · user (Ent)", name);
    }

    [Fact]
    public void FormatDisplayName_WithoutPlan_ReturnsSimple()
    {
        var name = CopilotProvider.FormatDisplayName("user", null);
        Assert.Equal("Copilot · user", name);
    }

    [Fact]
    public void FormatQuotaLabel_KnownLabels_ReturnsMapped()
    {
        Assert.Equal("Premium interactions", CopilotProvider.FormatQuotaLabel("premium"));
        Assert.Equal("Chat", CopilotProvider.FormatQuotaLabel("chat"));
        Assert.Equal("other", CopilotProvider.FormatQuotaLabel("other"));
    }

    [Fact]
    public void ExtractUsername_AccountPrefix_ReturnsName()
    {
        var name = CopilotProvider.ExtractUsername("Logged in to github.com account alice (oauth)");
        Assert.Equal("alice", name);
    }

    [Fact]
    public void ExtractUsername_AsPrefix_ReturnsName()
    {
        var name = CopilotProvider.ExtractUsername("Logged in to github.com as bob");
        Assert.Equal("bob", name);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_ParsesMultipleLines()
    {
        var stderr = "Logged in to github.com account alice\nLogged in to github.com as bob\nother line";
        var users = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);
        Assert.Equal(2, users.Count);
        Assert.Contains("alice", users);
        Assert.Contains("bob", users);
    }

    [Fact]
    public void ParseReset_ValidDate_ReturnsDescription()
    {
        var future = DateTimeOffset.UtcNow.AddDays(3).ToString("O");
        var (resetsAt, description) = CopilotProvider.ParseReset(future);
        Assert.NotNull(resetsAt);
        Assert.StartsWith("Resets in", description);
    }

    [Fact]
    public void ParseReset_Null_ReturnsNulls()
    {
        var (resetsAt, description) = CopilotProvider.ParseReset(null);
        Assert.Null(resetsAt);
        Assert.Null(description);
    }

    [Fact]
    public void ComputeUsageMetrics_Unlimited_ReturnsZeroAndUnlimited()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = true };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, pct);
        Assert.Equal("Unlimited", label);
        Assert.True(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_NoEntitlement_ReturnsNoQuota()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = false, Entitlement = 0 };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0, pct);
        Assert.Equal("No quota", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_WithUsage_ReturnsPercentAndLabel()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = false, Entitlement = 100, Remaining = 30 };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(0.7, pct);
        Assert.Equal("70 / 100", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_WithOverage_ReturnsOverageLabel()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = false, Entitlement = 100, Remaining = -5, OverageCount = 5, OveragePermitted = true };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal(1.05, pct);
        Assert.Equal("105 - $0.20", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_UnlimitedQuotaWithEntitlement_ReturnsNumericUsage()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot
        {
            Unlimited = true,
            Entitlement = 100,
            Remaining = -5,
            OverageCount = 5,
            OveragePermitted = true,
        };
        var (pct, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(1.05, pct);
        Assert.Equal("105 - $0.20", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_ChatLabel_KeepsQuotaType()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot { Unlimited = false, Entitlement = 300, Remaining = 100 };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "chat");
        Assert.Equal("200 / 300 Chat", label);
    }

    [Fact]
    public void ComputeUsageMetrics_OverageNotPermitted_ShowsOverLimit()
    {
        var quota = new CodexBar.Core.Models.CopilotQuotaSnapshot
        {
            Unlimited = false,
            Entitlement = 100,
            Remaining = -10,
            OverageCount = 10,
            OveragePermitted = false,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.Equal("110 / 100 (over limit)", label);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_EmptyInput_ReturnsEmpty()
    {
        var names = CopilotProvider.ExtractUsernamesFromGhStatus(string.Empty);
        Assert.Empty(names);
    }

    [Fact]
    public void ExtractUsername_ValidParenthesizedLine_ExtractsName()
    {
        var line = "  ✓ Logged in to github.com account (testuser)";
        var name = CopilotProvider.ExtractUsername(line);
        Assert.Equal("(testuser)", name);
    }

    [Fact]
    public void ExtractUsername_NoMatch_ReturnsNull()
    {
        var line = "  X Could not authenticate";
        var name = CopilotProvider.ExtractUsername(line);
        Assert.Null(name);
    }

    [Fact]
    public void ExtractUsername_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(CopilotProvider.ExtractUsername(null));
        Assert.Null(CopilotProvider.ExtractUsername(string.Empty));
    }

    [Fact]
    public void ComputeUsageMetrics_LargeEntitlement_FormatsWithCommas()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = 500,
            OverageCount = 0,
            OveragePermitted = false,
            Unlimited = false,
        };

        var (usedPercent, usageLabel, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");
        Assert.False(isUnlimited);
        Assert.Equal(0.75, usedPercent, 2);
        Assert.Equal("1,500 / 2,000", usageLabel);
    }

    [Fact]
    public void ParseReset_InvalidString_ReturnsNulls()
    {
        var (resetsAt, resetDescription) = CopilotProvider.ParseReset("not-a-date");
        Assert.Null(resetsAt);
        Assert.Null(resetDescription);
    }

    [Fact]
    public void ProjectMonthEndCredits_MidMonth_ScalesByElapsedMonth()
    {
        var now = new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero);
        var projected = CopilotProvider.ProjectMonthEndCredits(1500m, 2026, 6, now);

        Assert.Equal(3000m, projected);
    }

    [Fact]
    public void ProjectMonthEndCredits_AfterMonthEnd_ReturnsConsumed()
    {
        var now = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var projected = CopilotProvider.ProjectMonthEndCredits(1500m, 2026, 6, now);

        Assert.Equal(1500m, projected);
    }

    [Fact]
    public void GetBillingDays_CurrentMonth_ReturnsElapsedDays()
    {
        var now = new DateTimeOffset(2026, 6, 16, 0, 0, 0, TimeSpan.Zero);
        var days = CopilotProvider.GetBillingDays(2026, 6, now);

        Assert.Equal(Enumerable.Range(1, 16), days);
    }

    [Fact]
    public void GetBillingDays_PastMonth_ReturnsFullMonth()
    {
        var now = new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var days = CopilotProvider.GetBillingDays(2026, 6, now);

        Assert.Equal(Enumerable.Range(1, 30), days);
    }

    [Fact]
    public async Task FetchUsageAsync_WhenUserBillingAvailable_AddsCurrentProjectionBar()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(["alice"]);
        settings.Load().Returns(new AppSettings
        {
            CopilotEnterprise = "bertelsmann",
            CopilotOrganization = "Relias-Engineering",
        });

        var handler = new MockHttpMessageHandler(request =>
        {
            var uri = request.RequestUri!.ToString();
            if (uri.Contains("/copilot_internal/user", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "copilot_plan": "enterprise",
                      "organization_login_list": ["Relias-Engineering"],
                      "quota_snapshots": {
                        "premium_interactions": { "entitlement": 300, "remaining": 250 }
                      }
                    }
                    """);
            }

            if (uri.Contains("/settings/billing/usage/summary", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                    {
                      "timePeriod": { "year": 2026, "month": 6 },
                      "usageItems": [{ "grossQuantity": 200 }]
                    }
                    """);
            }

            if (uri.Contains("/copilot/billing", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""{ "seat_breakdown": { "total": 1 } }""");
            }

            if (uri.Contains("/settings/billing/premium_request/usage", StringComparison.OrdinalIgnoreCase))
            {
                if (!uri.Contains("day=1&", StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse("""{ "usageItems": [] }""");
                }

                return JsonResponse("""
                    {
                      "usageItems": [
                        { "sku": "Copilot AI Credits", "model": "Claude Opus 4.8", "grossQuantity": 30 },
                        { "sku": "Copilot AI Credits", "model": "Code Review model", "grossQuantity": 20 },
                        { "sku": "Copilot Premium Request", "model": "Claude Opus 4.8", "grossQuantity": 5 }
                      ]
                    }
                    """);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler));
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            httpFactory,
            settings)
        {
            TokenResolverOverride = (_, _) => Task.FromResult<string?>("fake-token"),
        };

        var result = await provider.FetchUsageAsync();

        Assert.NotNull(result.Items);
        var user = Assert.Single(result.Items, item => item.Key == "copilot:alice");
        Assert.NotNull(user.Bars);
        Assert.Equal("Current · 55 / 7,000", user.Bars![0].Label);
        Assert.StartsWith("Month end est. · ", user.Bars[1].Label);
        Assert.Equal(55m, user.Bars[1].ProjectionCurrent);
        Assert.Equal(7000m, user.Bars[1].ProjectionLimit);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), user.Bars[1].ProjectionPeriodStart);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), user.Bars[1].ProjectionPeriodEnd);
        Assert.Equal("Share of org · 55 / 200", user.Bars[2].Label);
    }

    [Fact]
    public void Metadata_WhenProviderIsConstructed_ReturnsExpectedMetadata()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(),
            settings);

        Assert.Equal(ProviderId.Copilot, provider.Metadata.Id);
        Assert.Equal("Copilot", provider.Metadata.DisplayName);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json),
    };

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
