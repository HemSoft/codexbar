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
}

public sealed class ProviderSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}
