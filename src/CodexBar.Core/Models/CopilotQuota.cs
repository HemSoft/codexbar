// <copyright file="CopilotQuota.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;

namespace CodexBar.Core.Models;

/// <summary>
/// Typed DTO for the /copilot_internal/user API response.
/// Properties use <see cref="JsonPropertyNameAttribute"/> to match the API's snake_case naming.
/// </summary>
public sealed class CopilotUserResponse
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("copilot_plan")]
    public string? CopilotPlan { get; set; }

    [JsonPropertyName("organization_login_list")]
    public List<string>? OrganizationLoginList { get; set; }

    [JsonPropertyName("quota_reset_date")]
    public string? QuotaResetDate { get; set; }

    [JsonPropertyName("quota_reset_date_utc")]
    public string? QuotaResetDateUtc { get; set; }

    [JsonPropertyName("quota_snapshots")]
    public CopilotQuotaSnapshots? QuotaSnapshots { get; set; }
}

public sealed class CopilotQuotaSnapshots
{
    [JsonPropertyName("chat")]
    public CopilotQuotaSnapshot? Chat { get; set; }

    [JsonPropertyName("completions")]
    public CopilotQuotaSnapshot? Completions { get; set; }

    [JsonPropertyName("premium_interactions")]
    public CopilotQuotaSnapshot? PremiumInteractions { get; set; }
}

public sealed class CopilotQuotaSnapshot
{
    [JsonPropertyName("entitlement")]
    public int Entitlement { get; set; }

    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }

    [JsonPropertyName("overage_count")]
    public int OverageCount { get; set; }

    [JsonPropertyName("overage_permitted")]
    public bool OveragePermitted { get; set; }

    [JsonPropertyName("percent_remaining")]
    public double PercentRemaining { get; set; }

    [JsonPropertyName("unlimited")]
    public bool Unlimited { get; set; }

    [JsonPropertyName("quota_id")]
    public string? QuotaId { get; set; }

    [JsonPropertyName("timestamp_utc")]
    public string? TimestampUtc { get; set; }
}

/// <summary>
/// Per-account Copilot quota result, used when multi-account is enabled.
/// </summary>
public sealed record CopilotAccountResult
{
    /// <summary>Gets gitHub username for this account.</summary>
    public required string Username { get; init; }

    /// <summary>Gets plan type: enterprise, individual_pro, etc.</summary>
    public string? Plan { get; init; }

    /// <summary>Gets orgs this account belongs to.</summary>
    public IReadOnlyList<string>? Organizations { get; init; }

    /// <summary>Gets premium interactions quota snapshot.</summary>
    public CopilotQuotaSnapshot? PremiumInteractions { get; init; }

    /// <summary>Gets chat quota snapshot.</summary>
    public CopilotQuotaSnapshot? Chat { get; init; }

    /// <summary>Gets when the quota resets (UTC ISO 8601).</summary>
    public string? QuotaResetDateUtc { get; init; }

    /// <summary>Gets a value indicating whether whether this account fetch succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Gets error message if fetch failed.</summary>
    public string? ErrorMessage { get; init; }
}
