// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Models;

using System.Text.Json.Serialization;

/// <summary>
/// Represents an enterprise billing usage summary response for Copilot.
/// </summary>
public sealed class BillingUsageSummaryResponse
{
    [JsonPropertyName("timePeriod")]
    public BillingTimePeriod? TimePeriod { get; set; }

    [JsonPropertyName("enterprise")]
    public string? Enterprise { get; set; }

    [JsonPropertyName("organization")]
    public string? Organization { get; set; }

    [JsonPropertyName("product")]
    public string? Product { get; set; }

    [JsonPropertyName("usageItems")]
    public List<BillingUsageItem> UsageItems { get; set; } = [];
}

/// <summary>
/// Represents a user-scoped Copilot premium request billing response.
/// </summary>
public sealed class BillingPremiumRequestResponse
{
    [JsonPropertyName("usageItems")]
    public List<BillingUsageItem> UsageItems { get; set; } = [];
}

/// <summary>
/// Represents a single Copilot billing usage item.
/// </summary>
public sealed class BillingUsageItem
{
    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("unitType")]
    public string? UnitType { get; set; }

    [JsonPropertyName("pricePerUnit")]
    public decimal? PricePerUnit { get; set; }

    [JsonPropertyName("grossQuantity")]
    public decimal GrossQuantity { get; set; }
}

/// <summary>
/// Represents the billing month associated with a usage response.
/// </summary>
public sealed class BillingTimePeriod
{
    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("month")]
    public int Month { get; set; }
}

/// <summary>
/// Represents the Copilot billing seat summary for an organization.
/// </summary>
public sealed class CopilotBillingSeatsResponse
{
    [JsonPropertyName("seat_breakdown")]
    public CopilotSeatBreakdown? SeatBreakdown { get; set; }
}

/// <summary>
/// Represents the Copilot seat breakdown returned by the billing API.
/// </summary>
public sealed class CopilotSeatBreakdown
{
    [JsonPropertyName("total")]
    public int Total { get; set; }
}
