// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Configuration;

using System.Text.Json.Serialization;

/// <summary>
/// Persisted application settings stored at ~/.codexbar/settings.json.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("refreshIntervalSeconds")]
    public int RefreshIntervalSeconds { get; set; } = 120;

    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderSettings> Providers { get; set; } = [];

    /// <summary>
    /// Gets or sets provider card keys in the user's preferred vertical display order.
    /// Unknown keys are ignored so renamed or removed providers do not break startup.
    /// </summary>
    [JsonPropertyName("providerCardOrder")]
    public List<string> ProviderCardOrder { get; set; } = [];

    /// <summary>
    /// Gets or sets gitHub usernames for multi-account Copilot tracking.
    /// Uses <c>gh auth token --user &lt;name&gt;</c> for per-account tokens.
    /// When empty, falls back to auto-discovery from the gh CLI.
    /// </summary>
    [JsonPropertyName("copilotAccounts")]
    public List<string> CopilotAccounts { get; set; } = [];

    /// <summary>
    /// Gets or sets the GitHub enterprise slug used for Copilot billing lookups.
    /// </summary>
    [JsonPropertyName("copilotEnterprise")]
    public string CopilotEnterprise { get; set; } = "bertelsmann";

    /// <summary>
    /// Gets or sets the GitHub organization slug used for Copilot billing lookups.
    /// </summary>
    [JsonPropertyName("copilotOrganization")]
    public string CopilotOrganization { get; set; } = "Relias-Engineering";

    /// <summary>
    /// Gets or sets an explicit monthly Copilot AI credit pool override.
    /// When null (default), CodexBar computes the pool from org seat count × credits-per-seat.
    /// </summary>
    [JsonPropertyName("copilotPoolTotal")]
    public decimal? CopilotPoolTotal { get; set; }

    /// <summary>
    /// Gets or sets openCode Go workspace ID used to construct the dashboard scrape URL.
    /// Can also be supplied via the OPENCODE_GO_WORKSPACE_ID environment variable.
    /// </summary>
    [JsonPropertyName("openCodeGoWorkspaceId")]
    public string? OpenCodeGoWorkspaceId { get; set; }

    /// <summary>Gets or sets uI zoom level (1.0 = 100%). Persisted across restarts.</summary>
    [JsonPropertyName("zoomLevel")]
    public double ZoomLevel { get; set; } = 1.0;

    /// <summary>Gets or sets saved window width (null = use default).</summary>
    [JsonPropertyName("windowWidth")]
    public double? WindowWidth { get; set; }

    /// <summary>Gets or sets saved window height (null = use default).</summary>
    [JsonPropertyName("windowHeight")]
    public double? WindowHeight { get; set; }

    /// <summary>Gets or sets saved window left position (null = position near tray).</summary>
    [JsonPropertyName("windowLeft")]
    public double? WindowLeft { get; set; }

    /// <summary>Gets or sets saved window top position (null = position near tray).</summary>
    [JsonPropertyName("windowTop")]
    public double? WindowTop { get; set; }

    /// <summary>
    /// Gets or sets the credit balance baseline per provider for session-spending tracking.
    /// Key = <see cref="ProviderId"/>.ToString(), value = balance at last reset.
    /// </summary>
    [JsonPropertyName("sessionSpendingBaselines")]
    public Dictionary<string, decimal> SessionSpendingBaselines { get; set; } = [];

    /// <summary>
    /// Gets or sets the timestamp of the last session-spending reset per key.
    /// Key matches <see cref="SessionSpendingBaselines"/>.
    /// </summary>
    [JsonPropertyName("sessionSpendingResetTimes")]
    public Dictionary<string, DateTimeOffset> SessionSpendingResetTimes { get; set; } = [];
}

public sealed class ProviderSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
