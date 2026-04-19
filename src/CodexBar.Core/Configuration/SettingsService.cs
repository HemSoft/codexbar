using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodexBar.Core.Configuration;

/// <summary>
/// Reads and writes settings to ~/.codexbar/settings.json.
/// <para>
/// ⚠️ Security note: API keys are stored in plaintext. On Windows, consider
/// using DPAPI (ProtectedData) for encryption. The settings file should be
/// protected by OS-level user permissions (~/.codexbar/).
/// </para>
/// </summary>
public sealed class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codexbar");

    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly object _lock = new();
    private AppSettings? _cached;

    public SettingsService(ILogger<SettingsService> logger) => _logger = logger;

    public AppSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            if (!File.Exists(SettingsPath))
            {
                _logger.LogInformation("No settings file found at {Path}, using defaults", SettingsPath);
                _cached = CreateDefaults();
                SaveInternal(_cached);
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(SettingsPath);
                _cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefaults();
                _cached.Providers ??= new Dictionary<string, ProviderSettings>();
                _logger.LogDebug("Settings loaded from {Path}", SettingsPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load settings from {Path}, using defaults", SettingsPath);
                _cached = CreateDefaults();
            }

            return _cached;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            SaveInternal(settings);
        }
    }

    private void SaveInternal(AppSettings settings)
    {
        try
        {
            var providers = settings.Providers ?? new Dictionary<string, ProviderSettings>();

            // Strip null/empty API keys to avoid persisting empty credential fields
            var sanitized = new AppSettings
            {
                RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
                Providers = providers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new ProviderSettings
                    {
                        Enabled = kvp.Value.Enabled,
                        ApiKey = string.IsNullOrWhiteSpace(kvp.Value.ApiKey) ? null : kvp.Value.ApiKey
                    })
            };

            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(sanitized, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            _cached = sanitized;
            _logger.LogDebug("Settings saved to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", SettingsPath);
        }
    }

    public string? GetApiKey(string providerId)
    {
        lock (_lock)
        {
            return Load().Providers.TryGetValue(providerId, out var ps) ? ps.ApiKey : null;
        }
    }

    public bool IsProviderEnabled(string providerId)
    {
        lock (_lock)
        {
            return !Load().Providers.TryGetValue(providerId, out var ps) || ps.Enabled;
        }
    }

    private static AppSettings CreateDefaults() => new()
    {
        RefreshIntervalSeconds = 120,
        Providers = new Dictionary<string, ProviderSettings>
        {
            ["Claude"] = new() { Enabled = true },
            ["Gemini"] = new() { Enabled = true },
            ["OpenRouter"] = new() { Enabled = true },
            ["Copilot"] = new() { Enabled = true }
        }
    };
}
