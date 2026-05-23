// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Tests;

using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers.Copilot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

/// <summary>
/// Comprehensive coverage tests for CopilotProvider targeting all uncovered branches:
/// ResolveTokenForUserAsync, ResolveTokenViaOverrideAsync, ResolveTokenViaGhCliAsync,
/// DiscoverGhAccountsAsync, GetAccountsToFetchAsync, FetchAccountQuotaAsync,
/// BuildUsageSnapshot, ComputeUsageMetrics, BuildUsageLabel, ParseReset,
/// ComputeOverageRequests, FormatDisplayName, ToUsageItem, BuildFetchResult,
/// InvalidateTokenForUserAsync, ExtractUsernamesFromGhStatus, ExtractUsername,
/// WaitForGhProcessAsync, BestEffortKillAndDrain.
/// </summary>
public class CopilotProviderFullCoverageTests
{
    private const string FakeToken = "gho_test_token_12345";

    // --- FormatDisplayName ---
    [Fact]
    public void FormatDisplayName_EnterprisePlan_ShowsEnt()
    {
        var result = CopilotProvider.FormatDisplayName("alice", "enterprise");
        Assert.Equal("Copilot · alice (Ent)", result);
    }

    [Fact]
    public void FormatDisplayName_IndividualProPlan_ShowsPro()
    {
        var result = CopilotProvider.FormatDisplayName("bob", "individual_pro");
        Assert.Equal("Copilot · bob (Pro)", result);
    }

    [Fact]
    public void FormatDisplayName_BusinessPlan_ShowsBiz()
    {
        var result = CopilotProvider.FormatDisplayName("carol", "business");
        Assert.Equal("Copilot · carol (Biz)", result);
    }

    [Fact]
    public void FormatDisplayName_UnknownPlan_ShowsFormatted()
    {
        var result = CopilotProvider.FormatDisplayName("dave", "some_custom_plan");
        Assert.Equal("Copilot · dave (some custom plan)", result);
    }

    [Fact]
    public void FormatDisplayName_NullPlan_ShowsJustUsername()
    {
        var result = CopilotProvider.FormatDisplayName("eve", null);
        Assert.Equal("Copilot · eve", result);
    }

    // --- FormatQuotaLabel ---
    [Fact]
    public void FormatQuotaLabel_Premium_ReturnsPrettyLabel()
    {
        var result = CopilotProvider.FormatQuotaLabel("premium");
        Assert.Equal("Premium interactions", result);
    }

    [Fact]
    public void FormatQuotaLabel_Chat_ReturnsPrettyLabel()
    {
        var result = CopilotProvider.FormatQuotaLabel("chat");
        Assert.Equal("Chat", result);
    }

    [Fact]
    public void FormatQuotaLabel_Unknown_ReturnsRawLabel()
    {
        var result = CopilotProvider.FormatQuotaLabel("completions");
        Assert.Equal("completions", result);
    }

    // --- ComputeUsageMetrics ---
    [Fact]
    public void ComputeUsageMetrics_WithEntitlement_CalculatesPercentage()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 2000,
            Remaining = 500,
            OverageCount = 0,
            OveragePermitted = false,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(0.75, usedPercent);
        Assert.Contains("1,500", label);
        Assert.Contains("2,000", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_Unlimited_ReturnsUnlimited()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 0,
            Remaining = 0,
            Unlimited = true,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(0, usedPercent);
        Assert.Equal("Unlimited", label);
        Assert.True(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_NoQuota_ReturnsNoQuota()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 0,
            Remaining = 0,
            Unlimited = false,
        };
        var (usedPercent, label, isUnlimited) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(0, usedPercent);
        Assert.Equal("No quota", label);
        Assert.False(isUnlimited);
    }

    [Fact]
    public void ComputeUsageMetrics_WithOveragePermitted_ShowsOverageCost()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -10,
            OverageCount = 10,
            OveragePermitted = true,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Contains("$", label); // Shows overage cost
    }

    [Fact]
    public void ComputeUsageMetrics_WithOverageNotPermitted_ShowsOverLimit()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -5,
            OverageCount = 5,
            OveragePermitted = false,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Contains("over limit", label);
    }

    [Fact]
    public void ComputeUsageMetrics_NonPremiumQuota_IncludesQuotaLabel()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 500,
            Remaining = 300,
            OverageCount = 0,
            OveragePermitted = false,
        };
        var (_, label, _) = CopilotProvider.ComputeUsageMetrics(quota, "chat");

        Assert.Contains("Chat", label);
    }

    [Fact]
    public void ComputeUsageMetrics_NegativeRemaining_UsedDoesNotExceedEntitlement()
    {
        var quota = new CopilotQuotaSnapshot
        {
            Entitlement = 100,
            Remaining = -50,
            OverageCount = 0,
            OveragePermitted = false,
        };
        var (usedPercent, _, _) = CopilotProvider.ComputeUsageMetrics(quota, "premium");

        Assert.Equal(1.5, usedPercent); // 150/100
    }

    // --- ParseReset ---
    [Fact]
    public void ParseReset_NullDate_ReturnsNull()
    {
        var (resetsAt, desc) = CopilotProvider.ParseReset(null);
        Assert.Null(resetsAt);
        Assert.Null(desc);
    }

    [Fact]
    public void ParseReset_InvalidDate_ReturnsNull()
    {
        var (resetsAt, desc) = CopilotProvider.ParseReset("not-a-date");
        Assert.Null(resetsAt);
        Assert.Null(desc);
    }

    [Fact]
    public void ParseReset_FutureMoreThanTwoDays_ShowsDays()
    {
        var future = DateTimeOffset.UtcNow.AddDays(5).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(future);

        Assert.NotNull(resetsAt);
        Assert.NotNull(desc);
        Assert.Contains("Resets in", desc);
        Assert.Contains("d", desc);
    }

    [Fact]
    public void ParseReset_FutureBetweenOneAndTwoDays_ShowsTomorrow()
    {
        var future = DateTimeOffset.UtcNow.AddHours(30).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(future);

        Assert.NotNull(desc);
        Assert.Equal("Resets tomorrow", desc);
    }

    [Fact]
    public void ParseReset_FutureLessThanOneDay_ShowsHoursAndMinutes()
    {
        var future = DateTimeOffset.UtcNow.AddHours(5).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(future);

        Assert.NotNull(desc);
        Assert.Contains("Resets in", desc);
        Assert.Contains("h", desc);
    }

    [Fact]
    public void ParseReset_PastDate_ShowsOverdue()
    {
        var past = DateTimeOffset.UtcNow.AddHours(-2).ToString("o");
        var (resetsAt, desc) = CopilotProvider.ParseReset(past);

        Assert.NotNull(desc);
        Assert.Equal("Reset overdue", desc);
    }

    // --- ExtractUsername ---
    [Fact]
    public void ExtractUsername_AccountFormat_ExtractsUsername()
    {
        var result = CopilotProvider.ExtractUsername("✓ Logged in to github.com account alice (token)");
        Assert.Equal("alice", result);
    }

    [Fact]
    public void ExtractUsername_AsFormat_ExtractsUsername()
    {
        var result = CopilotProvider.ExtractUsername("✓ Logged in to github.com as bob (token)");
        Assert.Equal("bob", result);
    }

    [Fact]
    public void ExtractUsername_AccountFormatNoSpace_ReturnsRest()
    {
        var result = CopilotProvider.ExtractUsername("Logged in to github.com account alice");
        Assert.Equal("alice", result);
    }

    [Fact]
    public void ExtractUsername_NoAccountOrAs_ReturnsNull()
    {
        var result = CopilotProvider.ExtractUsername("Some other line");
        Assert.Null(result);
    }

    // --- ExtractUsernamesFromGhStatus ---
    [Fact]
    public void ExtractUsernamesFromGhStatus_MultipleAccounts_ExtractsAll()
    {
        var stderr = """
        github.com
          ✓ Logged in to github.com account alice (token)
          ✓ Logged in to github.com account bob (keyring)
        """;
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);

        Assert.Equal(2, result.Count);
        Assert.Contains("alice", result);
        Assert.Contains("bob", result);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_NoMatches_ReturnsEmpty()
    {
        var stderr = "No accounts found\nSome other output";
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(stderr);
        Assert.Empty(result);
    }

    [Fact]
    public void ExtractUsernamesFromGhStatus_EmptyString_ReturnsEmpty()
    {
        var result = CopilotProvider.ExtractUsernamesFromGhStatus(string.Empty);
        Assert.Empty(result);
    }

    // --- ParseCopilotApiResponse ---
    [Fact]
    public void ParseCopilotApiResponse_FullResponse_ParsesAllFields()
    {
        var json = """
        {
            "login": "alice",
            "copilot_plan": "enterprise",
            "organization_login_list": ["org1", "org2"],
            "quota_reset_date_utc": "2026-06-01T00:00:00Z",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 2000,
                    "remaining": 500,
                    "overage_count": 0,
                    "overage_permitted": true,
                    "percent_remaining": 25.0,
                    "unlimited": false
                },
                "chat": {
                    "entitlement": 1000,
                    "remaining": 800,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "percent_remaining": 80.0,
                    "unlimited": false
                }
            }
        }
        """;
        var result = CopilotProvider.ParseCopilotApiResponse(json, "alice");

        Assert.True(result.Success);
        Assert.Equal("enterprise", result.Plan);
        Assert.NotNull(result.Organizations);
        Assert.Equal(2, result.Organizations!.Count);
        Assert.NotNull(result.PremiumInteractions);
        Assert.Equal(2000, result.PremiumInteractions!.Entitlement);
        Assert.NotNull(result.Chat);
    }

    [Fact]
    public void ParseCopilotApiResponse_NullDeserialization_ReturnsError()
    {
        var result = CopilotProvider.ParseCopilotApiResponse("null", "alice");
        Assert.False(result.Success);
        Assert.Equal("Empty API response", result.ErrorMessage);
    }

    [Fact]
    public void ParseCopilotApiResponse_MinimalResponse_SucceedsWithDefaults()
    {
        var json = """{"login":"alice"}""";
        var result = CopilotProvider.ParseCopilotApiResponse(json, "alice");

        Assert.True(result.Success);
        Assert.Null(result.Plan);
        Assert.Null(result.PremiumInteractions);
    }

    [Fact]
    public void ParseCopilotApiResponse_WithLogger_LogsDebug()
    {
        var json = """
        {
            "copilot_plan": "individual_pro",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 500,
                    "remaining": 100
                }
            }
        }
        """;
        var result = CopilotProvider.ParseCopilotApiResponse(json, "alice", NullLogger<CopilotProvider>.Instance);
        Assert.True(result.Success);
    }

    [Fact]
    public void ParseCopilotApiResponse_NullLogger_ReturnsSuccess()
    {
        var json = """{"copilot_plan":"pro"}""";
        var result = CopilotProvider.ParseCopilotApiResponse(json, "bob", null);
        Assert.True(result.Success);
    }

    // --- FetchUsageAsync - account discovery ---
    [Fact]
    public async Task FetchUsageAsync_NoConfiguredAccounts_UsesAutoDiscovery()
    {
        var json = BuildCopilotUserJson();
        var settings = CreateSettingsNoAccounts();
        var handler = new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);
        provider.AccountDiscoveryOverride = _ => Task.FromResult(new List<string> { "discovered-user" });
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(FakeToken);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Single(result.Items!);
        Assert.Contains("discovered-user", result.Items!.First().Key);
    }

    [Fact]
    public async Task FetchUsageAsync_DiscoveryReturnsEmpty_CachesEmptyAndReturnsError()
    {
        var settings = CreateSettingsNoAccounts();
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);
        provider.AccountDiscoveryOverride = _ => Task.FromResult(new List<string>());

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No Copilot accounts found", result.ErrorMessage!);
    }

    [Fact]
    public async Task FetchUsageAsync_DiscoveryError_ReturnsStoredError()
    {
        var settings = CreateSettingsNoAccounts();
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);

        // Simulate gh process returning non-zero
        provider.GhStatusProcessOverride = () => CreateFakeProcess(string.Empty, "auth failed", 1);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("auth failed", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_GhNotOnPath_ReturnsInstallError()
    {
        var settings = CreateSettingsNoAccounts();
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);

        // Override to throw Win32Exception (simulating gh not found)
        provider.GhStatusProcessOverride = () => throw new Win32Exception("The system cannot find the file specified.");

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_TokenResolutionReturnsNull_ReturnsTokenMissing()
    {
        var settings = CreateSettings("testuser");
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(null);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No token", result.Items!.First().ErrorMessage!);
    }

    [Fact]
    public async Task FetchUsageAsync_401Response_InvalidatesTokenAndRetries()
    {
        var settings = CreateSettings("testuser");
        int callCount = 0;
        var handler = new DelegatingHandlerFunc((_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(FakeToken);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("expired or invalid", result.Items!.First().ErrorMessage!);
    }

    [Fact]
    public async Task FetchUsageAsync_WithOverage_CalculatesOverageCost()
    {
        var json = """
        {
            "copilot_plan": "individual_pro",
            "quota_reset_date_utc": "2026-06-01T00:00:00Z",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 100,
                    "remaining": -20,
                    "overage_count": 20,
                    "overage_permitted": true,
                    "unlimited": false
                }
            }
        }
        """;
        var settings = CreateSettings("alice");
        var handler = new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(FakeToken);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items!.First();
        Assert.NotNull(item.OverageCost);
        Assert.Equal(0.80m, item.OverageCost!.Value); // 20 * $0.04
    }

    [Fact]
    public async Task FetchUsageAsync_WithChatQuota_IncludesSecondaryUsage()
    {
        var json = """
        {
            "copilot_plan": "individual_pro",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 2000,
                    "remaining": 500,
                    "overage_permitted": false,
                    "unlimited": false
                },
                "chat": {
                    "entitlement": 1000,
                    "remaining": 800,
                    "overage_permitted": false,
                    "unlimited": false
                }
            }
        }
        """;
        var settings = CreateSettings("alice");
        var handler = new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(FakeToken);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items!.First();
        Assert.NotNull(item.PrimaryUsage);
        Assert.NotNull(item.SecondaryUsage);
    }

    [Fact]
    public async Task FetchUsageAsync_OverageNotPermittedWithNegativeRemaining_ShowsOverLimit()
    {
        var json = """
        {
            "copilot_plan": "individual_pro",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 100,
                    "remaining": -5,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "unlimited": false
                }
            }
        }
        """;
        var settings = CreateSettings("alice");
        var handler = new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(FakeToken);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items!.First();
        Assert.Contains("over limit", item.PrimaryUsage!.UsageLabel);
    }

    [Fact]
    public async Task FetchUsageAsync_UnlimitedQuota_SetsIsUnlimited()
    {
        var json = """
        {
            "copilot_plan": "enterprise",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 0,
                    "remaining": 0,
                    "unlimited": true,
                    "overage_permitted": false
                }
            }
        }
        """;
        var settings = CreateSettings("alice");
        var handler = new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(FakeToken);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        var item = result.Items!.First();
        Assert.True(item.PrimaryUsage!.IsUnlimited);
    }

    [Fact]
    public async Task FetchUsageAsync_NoPremiumInteractions_NullSessionUsage()
    {
        var json = """{"copilot_plan":"individual_pro","quota_snapshots":{}}""";
        var settings = CreateSettings("alice");
        var handler = new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);
        provider.TokenResolverOverride = (_, _) => Task.FromResult<string?>(FakeToken);

        var result = await provider.FetchUsageAsync();

        Assert.True(result.Success);
        Assert.Null(result.SessionUsage); // No premium → no aggregate session
    }

    // --- Token resolution via gh CLI ---
    [Fact]
    public async Task FetchUsageAsync_GhTokenTimesOut_ReturnsNull()
    {
        var json = BuildCopilotUserJson();
        var settings = CreateSettings("alice");
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        }));
        var provider = CreateProvider(settings, factory);
        provider.TokenTimeoutOverride = TimeSpan.FromMilliseconds(1);
        provider.GhTokenProcessOverride = _ => CreateSlowProcess();

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("No token", result.Items!.First().ErrorMessage!);
    }

    [Fact]
    public async Task FetchUsageAsync_GhTokenNonZeroExit_ReturnsNull()
    {
        var settings = CreateSettings("alice");
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);
        provider.GhTokenProcessOverride = _ => CreateFakeProcess(string.Empty, "some error that is longer than 200 chars to test truncation: " + new string('x', 300), 1);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_GhTokenEmptyOutput_ReturnsNull()
    {
        var settings = CreateSettings("alice");
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);
        provider.GhTokenProcessOverride = _ => CreateFakeProcess(string.Empty, string.Empty, 0);

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    [Fact]
    public async Task FetchUsageAsync_GhTokenSuccess_CachesToken()
    {
        var json = BuildCopilotUserJson();
        var settings = CreateSettings("alice");
        var handler = new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        var factory = CreateFactory(handler);
        var provider = CreateProvider(settings, factory);

        int processCreations = 0;
        provider.GhTokenProcessOverride = _ =>
        {
            Interlocked.Increment(ref processCreations);
            return CreateFakeProcess(FakeToken, string.Empty, 0);
        };

        await provider.FetchUsageAsync();
        await provider.FetchUsageAsync(); // Second call should use cache

        Assert.Equal(1, processCreations);
    }

    [Fact]
    public async Task FetchUsageAsync_GhTokenThrows_ReturnsNull()
    {
        var settings = CreateSettings("alice");
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);
        provider.GhTokenProcessOverride = _ => throw new InvalidOperationException("Process error");

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
    }

    // --- Discovery flow tests ---
    [Fact]
    public async Task FetchUsageAsync_DiscoveryTimeoutProcess_ReturnsTimeoutError()
    {
        var settings = CreateSettingsNoAccounts();
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);
        provider.DiscoveryTimeoutOverride = TimeSpan.FromMilliseconds(1);
        provider.GhStatusProcessOverride = CreateSlowProcess;

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("timed out", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchUsageAsync_DiscoveryGenericException_ReturnsError()
    {
        var settings = CreateSettingsNoAccounts();
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);
        provider.GhStatusProcessOverride = () => throw new InvalidOperationException("Test error");

        var result = await provider.FetchUsageAsync();

        Assert.False(result.Success);
        Assert.Contains("Test error", result.ErrorMessage!);
    }

    [Fact]
    public async Task FetchUsageAsync_EmptyDiscoveryCachedForFiveMinutes_SecondCallReturnsEmpty()
    {
        var settings = CreateSettingsNoAccounts();
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);

        int discoveryCalls = 0;
        provider.AccountDiscoveryOverride = _ =>
        {
            Interlocked.Increment(ref discoveryCalls);
            return Task.FromResult(new List<string>());
        };

        await provider.FetchUsageAsync(); // First discovery → empty
        await provider.FetchUsageAsync(); // Second call → should use cached empty

        // The override is called only once due to caching
        Assert.Equal(1, discoveryCalls);
    }

    // --- ResolveTokenViaOverrideAsync ---
    [Fact]
    public async Task FetchUsageAsync_OverrideReturnsWhitespace_TokenNotCached()
    {
        var settings = CreateSettings("alice");
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);

        int callCount = 0;
        provider.TokenResolverOverride = (_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult<string?>("   "); // Whitespace
        };

        // First call
        await provider.FetchUsageAsync();

        // Second call - since whitespace wasn't cached, override is called again
        await provider.FetchUsageAsync();

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task FetchUsageAsync_OverrideReturnsNull_TokenNotCached()
    {
        var settings = CreateSettings("alice");
        var factory = CreateFactory(new CloneableHandler(new HttpResponseMessage(HttpStatusCode.OK)));
        var provider = CreateProvider(settings, factory);

        int callCount = 0;
        provider.TokenResolverOverride = (_, _) =>
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult<string?>(null);
        };

        await provider.FetchUsageAsync();
        await provider.FetchUsageAsync();

        Assert.Equal(2, callCount);
    }

    // --- BuildCopilotApiRequest ---
    [Fact]
    public void BuildCopilotApiRequest_SetsCorrectHeadersAndUrl()
    {
        using var request = CopilotProvider.BuildCopilotApiRequest("gho_test_token");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.github.com/copilot_internal/user", request.RequestUri!.ToString());
        Assert.Equal("token", request.Headers.Authorization!.Scheme);
        Assert.Equal("gho_test_token", request.Headers.Authorization.Parameter);
    }

    // --- BestEffortKillAndDrain ---
    [Fact]
    public void BestEffortKillAndDrain_ProcessAlreadyExited_SwallowsKillException()
    {
        using var process = CreateFakeProcess("stdout", "stderr", 0);
        process.Start();
        process.WaitForExit();

        var stderrTask = Task.FromResult("stderr data");
        var stdoutTask = Task.FromResult("stdout data");

        var ex = Record.Exception(() =>
            CopilotProvider.BestEffortKillAndDrain(process, stderrTask, stdoutTask));
        Assert.Null(ex);
    }

    // --- IsAvailableAsync ---
    [Fact]
    public async Task IsAvailableAsync_Enabled_ReturnsTrue()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(), settings);

        Assert.True(await provider.IsAvailableAsync());
    }

    [Fact]
    public async Task IsAvailableAsync_Disabled_ReturnsFalse()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(false);
        var provider = new CopilotProvider(
            NullLogger<CopilotProvider>.Instance,
            Substitute.For<IHttpClientFactory>(), settings);

        Assert.False(await provider.IsAvailableAsync());
    }

    // --- Helpers ---
    private static CopilotProvider CreateProvider(ISettingsService settings, IHttpClientFactory factory)
    {
        return new CopilotProvider(NullLogger<CopilotProvider>.Instance, factory, settings);
    }

    private static ISettingsService CreateSettings(params string[] accounts)
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(accounts.ToList());
        return settings;
    }

    private static ISettingsService CreateSettingsNoAccounts()
    {
        var settings = Substitute.For<ISettingsService>();
        settings.IsProviderEnabled(ProviderId.Copilot).Returns(true);
        settings.GetCopilotAccounts().Returns(new List<string>());
        return settings;
    }

    private static IHttpClientFactory CreateFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    private static string BuildCopilotUserJson(string plan = "individual_pro")
    {
        var resetDate = DateTimeOffset.UtcNow.AddDays(15).ToString("o");
        return $$"""
        {
            "login": "testuser",
            "copilot_plan": "{{plan}}",
            "organization_login_list": ["org1"],
            "quota_reset_date_utc": "{{resetDate}}",
            "quota_snapshots": {
                "premium_interactions": {
                    "entitlement": 2000,
                    "remaining": 500,
                    "overage_count": 0,
                    "overage_permitted": false,
                    "percent_remaining": 25.0,
                    "unlimited": false
                }
            }
        }
        """;
    }

    private static Process CreateFakeProcess(string stdout, string stderr, int exitCode)
    {
        // Use cmd /c to create a process that exits with specific output
        var escapedStdout = stdout.Replace("\"", "\\\"");
        var escapedStderr = stderr.Replace("\"", "\\\"");
        var args = exitCode == 0
            ? $"/c \"echo {escapedStdout}\" 1>&2 && echo {escapedStdout}"
            : $"/c \"echo {escapedStderr}\" 1>&2 && exit {exitCode}";

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
    }

    private static Process CreateSlowProcess()
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd",
                Arguments = "/c timeout /t 30 /nobreak >nul",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
    }

    private sealed class DelegatingHandlerFunc(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }

    private sealed class CloneableHandler(HttpResponseMessage template) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var clone = new HttpResponseMessage(template.StatusCode);
            if (template.Content is not null)
            {
                var content = template.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
                clone.Content = new StringContent(content, Encoding.UTF8, "application/json");
            }

            return Task.FromResult(clone);
        }
    }
}
