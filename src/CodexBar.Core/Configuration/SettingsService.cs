using System.Runtime.InteropServices;
#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
#endif
using System.Text.Json;
using CodexBar.Core.Models;
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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly object _lock = new();
    private AppSettings? _cached;

    public SettingsService(ILogger<SettingsService> logger) => _logger = logger;

    public AppSettings Load()
    {
        lock (_lock)
        {
            return DeepCopy(EnsureCached());
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
                        Enabled = kvp.Value?.Enabled ?? true,
                        ApiKey = string.IsNullOrWhiteSpace(kvp.Value?.ApiKey) ? null : kvp.Value.ApiKey
                    })
            };

            Directory.CreateDirectory(SettingsDir);
            RestrictDirectoryPermissions(SettingsDir);
            var json = JsonSerializer.Serialize(sanitized, JsonOptions);

            // Create the temp file and restrict permissions BEFORE writing content
            // to avoid a window where sensitive data is world-readable.
            var tempPath = SettingsPath + ".tmp";
            using (File.Create(tempPath)) { }
            RestrictFilePermissions(tempPath);
            // Permissions persist across writes on both Windows (NTFS ACL) and Unix (inode mode).
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, SettingsPath, overwrite: true);

            _cached = sanitized;
            _logger.LogDebug("Settings saved to {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", SettingsPath);
        }
    }

    public string? GetApiKey(ProviderId providerId)
    {
        lock (_lock)
        {
            var settings = EnsureCached();
            return settings.Providers.TryGetValue(providerId.ToString(), out var ps) ? ps?.ApiKey : null;
        }
    }

    public bool IsProviderEnabled(ProviderId providerId)
    {
        lock (_lock)
        {
            var settings = EnsureCached();
            return !settings.Providers.TryGetValue(providerId.ToString(), out var ps) || ps is null || ps.Enabled;
        }
    }

    /// <summary>
    /// Returns the cached settings, initializing from disk if needed.
    /// Must be called while holding <see cref="_lock"/>. Does NOT deep-copy.
    /// </summary>
    private AppSettings EnsureCached()
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
            NormalizeProviders(_cached.Providers);

            try
            {
                RestrictDirectoryPermissions(SettingsDir);
                RestrictFilePermissions(SettingsPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restrict settings file permissions for {Path}", SettingsPath);
            }

            _logger.LogDebug("Settings loaded from {Path}", SettingsPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings from {Path}, using defaults", SettingsPath);
            _cached = CreateDefaults();
        }

        return _cached;
    }

    private static AppSettings CreateDefaults() => new()
    {
        RefreshIntervalSeconds = 120,
        Providers = new Dictionary<string, ProviderSettings>
        {
            [ProviderId.Claude.ToString()] = new() { Enabled = true },
            [ProviderId.Gemini.ToString()] = new() { Enabled = true },
            [ProviderId.OpenRouter.ToString()] = new() { Enabled = true },
            [ProviderId.Copilot.ToString()] = new() { Enabled = true }
        }
    };

    private static AppSettings DeepCopy(AppSettings source) => new()
    {
        RefreshIntervalSeconds = source.RefreshIntervalSeconds,
        Providers = (source.Providers ?? new Dictionary<string, ProviderSettings>())
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is null ? new ProviderSettings() : new ProviderSettings
                {
                    Enabled = kvp.Value.Enabled,
                    ApiKey = kvp.Value.ApiKey
                })
    };

    /// <summary>
    /// Replaces null <see cref="ProviderSettings"/> values with defaults to prevent NREs.
    /// </summary>
    private static void NormalizeProviders(Dictionary<string, ProviderSettings> providers)
    {
        foreach (var key in providers.Keys.ToList())
        {
            providers[key] ??= new ProviderSettings();
        }
    }

    /// <summary>
    /// Restricts file permissions so only the current user can read/write.
    /// On Windows: sets an explicit ACL granting FullControl only to the current user.
    /// On Unix: sets file mode to owner read/write (chmod 600).
    /// </summary>
    private void RestrictFilePermissions(string filePath)
    {
        try
        {
#if WINDOWS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var fileInfo = new FileInfo(filePath);

                var currentUser = WindowsIdentity.GetCurrent().User;
                if (currentUser is not null)
                {
                    // Build a fresh protected ACL so no pre-existing rules remain.
                    var security = new FileSecurity();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(
                        currentUser,
                        FileSystemRights.FullControl,
                        AccessControlType.Allow));
                    fileInfo.SetAccessControl(security);
                }
            }
            else
#endif
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not restrict file permissions on {Path}", filePath);
        }
    }

    /// <summary>
    /// Restricts directory permissions so only the current user can access it.
    /// On Windows: sets an explicit ACL granting FullControl only to the current user.
    /// On Unix: sets directory mode to owner-only (chmod 700).
    /// </summary>
    private void RestrictDirectoryPermissions(string dirPath)
    {
        try
        {
#if WINDOWS
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var dirInfo = new DirectoryInfo(dirPath);

                var currentUser = WindowsIdentity.GetCurrent().User;
                if (currentUser is not null)
                {
                    var security = new DirectorySecurity();
                    security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    security.AddAccessRule(new FileSystemAccessRule(
                        currentUser,
                        FileSystemRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow));
                    dirInfo.SetAccessControl(security);
                }
            }
            else
#endif
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(dirPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not restrict directory permissions on {Path}", dirPath);
        }
    }
}
