using System.Text.Json.Serialization;

namespace CodexBar.Core.Configuration;

/// <summary>
/// Persisted application settings stored at ~/.codexbar/settings.json.
/// </summary>
public sealed class AppSettings
{
    [JsonPropertyName("refreshIntervalSeconds")]
    public int RefreshIntervalSeconds { get; set; } = 120;

    [JsonPropertyName("providers")]
    public Dictionary<string, ProviderSettings> Providers { get; set; } = new();

    /// <summary>
    /// GitHub usernames for multi-account Copilot tracking.
    /// Uses <c>gh auth token --user &lt;name&gt;</c> for per-account tokens.
    /// When empty, falls back to auto-discovery from the gh CLI.
    /// </summary>
    [JsonPropertyName("copilotAccounts")]
    public List<string> CopilotAccounts { get; set; } = new();

    /// <summary>
    /// OpenCode Go workspace ID used to construct the dashboard scrape URL.
    /// Can also be supplied via the OPENCODE_GO_WORKSPACE_ID environment variable.
    /// </summary>
    [JsonPropertyName("openCodeGoWorkspaceId")]
    public string? OpenCodeGoWorkspaceId { get; set; }

    /// <summary>UI zoom level (1.0 = 100%). Persisted across restarts.</summary>
    [JsonPropertyName("zoomLevel")]
    public double ZoomLevel { get; set; } = 1.0;

    /// <summary>Saved window width (null = use default).</summary>
    [JsonPropertyName("windowWidth")]
    public double? WindowWidth { get; set; }

    /// <summary>Saved window height (null = use default).</summary>
    [JsonPropertyName("windowHeight")]
    public double? WindowHeight { get; set; }
    /// <summary>Saved window left position (null = position near tray).</summary>
    [JsonPropertyName("windowLeft")]
    public double? WindowLeft { get; set; }

    /// <summary>Saved window top position (null = position near tray).</summary>
    [JsonPropertyName("windowTop")]
    public double? WindowTop { get; set; }
}

public sealed class ProviderSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
