// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Configuration;

#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
#endif

using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text.Json;
using CodexBar.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Reads and writes settings to ~/.codexbar/settings.json.
/// <para>
/// ⚠️ Security note: API keys are stored in plaintext. On Windows, consider
/// using DPAPI (ProtectedData) for encryption. The settings file should be
/// protected by OS-level user permissions (~/.codexbar/).
/// </para>
/// </summary>
public sealed class SettingsService : ISettingsService
{
    private readonly string _settingsDir;
    private readonly string _settingsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger<SettingsService> _logger;
    private readonly object _lock = new();
    private AppSettings? _cached;

    public SettingsService(ILogger<SettingsService> logger)
    {
        this._logger = logger;
        this._settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codexbar");
        this._settingsPath = Path.Combine(this._settingsDir, "settings.json");
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsService"/> class
    /// for testing with a custom settings directory.
    /// </summary>
    internal SettingsService(ILogger<SettingsService> logger, string settingsDir)
    {
        this._logger = logger;
        this._settingsDir = settingsDir;
        this._settingsPath = Path.Combine(this._settingsDir, "settings.json");
    }

    public AppSettings Load()
    {
        lock (this._lock)
        {
            return DeepCopy(this.EnsureCached());
        }
    }

    public void Save(AppSettings settings)
    {
        lock (this._lock)
        {
            this.MergeFromDisk(settings);
            this.SaveInternal(settings);
        }
    }

    /// <summary>
    /// Merges provider entries and credential fields that exist on disk but are absent from
    /// the in-memory settings. Prevents an in-flight Save from clobbering credentials that
    /// were added to the file while the app was running.
    /// </summary>
    private void MergeFromDisk(AppSettings settings)
    {
        if (!File.Exists(this._settingsPath))
        {
            return;
        }

        try
        {
            var diskJson = File.ReadAllText(this._settingsPath);
            var disk = JsonSerializer.Deserialize<AppSettings>(diskJson, JsonOptions);
            if (disk is null)
            {
                return;
            }

            settings.Providers ??= [];
            foreach (var (key, diskProvider) in disk.Providers ?? [])
            {
                if (!settings.Providers.TryGetValue(key, out var memProvider) || memProvider is null)
                {
                    // Entire provider entry missing from memory — bring it over
                    settings.Providers[key] = diskProvider ?? new ProviderSettings();
                }
                else if (diskProvider?.ApiKey is not null && string.IsNullOrWhiteSpace(memProvider.ApiKey))
                {
                    // Provider exists in memory but its ApiKey is empty — preserve disk credential
                    memProvider.ApiKey = diskProvider.ApiKey;
                }
            }

            if (string.IsNullOrWhiteSpace(settings.OpenCodeGoWorkspaceId) && !string.IsNullOrWhiteSpace(disk.OpenCodeGoWorkspaceId))
            {
                settings.OpenCodeGoWorkspaceId = disk.OpenCodeGoWorkspaceId;
            }

            // Preserve session spending baselines from disk that are not in memory
            settings.SessionSpendingBaselines ??= [];
            foreach (var (key, diskBaseline) in disk.SessionSpendingBaselines ?? [])
            {
                settings.SessionSpendingBaselines.TryAdd(key, diskBaseline);
            }

            // Preserve session spending reset times from disk that are not in memory
            settings.SessionSpendingResetTimes ??= [];
            foreach (var (key, diskTime) in disk.SessionSpendingResetTimes ?? [])
            {
                settings.SessionSpendingResetTimes.TryAdd(key, diskTime);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogDebug(ex, "MergeFromDisk skipped — could not read {Path}", this._settingsPath);
        }
    }

    private void SaveInternal(AppSettings settings)
    {
        try
        {
            var providers = settings.Providers ?? [];

            // Strip null/empty API keys to avoid persisting empty credential fields
            var sanitized = new AppSettings
            {
                RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
                CopilotAccounts = (settings.CopilotAccounts ?? []).ToList(),
                OpenCodeGoWorkspaceId = string.IsNullOrWhiteSpace(settings.OpenCodeGoWorkspaceId) ? null : settings.OpenCodeGoWorkspaceId,
                ZoomLevel = settings.ZoomLevel is > 0 and <= 5 ? settings.ZoomLevel : 1.0,
                WindowWidth = settings.WindowWidth,
                WindowHeight = settings.WindowHeight,
                WindowLeft = settings.WindowLeft,
                WindowTop = settings.WindowTop,
                SessionSpendingBaselines = (settings.SessionSpendingBaselines ?? []).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                SessionSpendingResetTimes = (settings.SessionSpendingResetTimes ?? []).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
                Providers = providers.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new ProviderSettings
                    {
                        Enabled = kvp.Value?.Enabled ?? true,
                        ApiKey = string.IsNullOrWhiteSpace(kvp.Value?.ApiKey) ? null : kvp.Value.ApiKey
                    }),
            };

            Directory.CreateDirectory(this._settingsDir);
            this.RestrictDirectoryPermissions(this._settingsDir);
            var json = JsonSerializer.Serialize(sanitized, JsonOptions);

            // Write to a temp file and atomically move into place.
            // Permissions are set at creation time (see FileSecurityHelper.WriteRestrictedFile)
            // so no window exists where the file is world-readable.
            var tempPath = this._settingsPath + ".tmp";
            FileSecurityHelper.WriteRestrictedFile(tempPath, json);
            try
            {
                File.Move(tempPath, this._settingsPath, overwrite: true);
            }
            catch
            {
                BestEffortDelete(tempPath);
                throw;
            }

            this._cached = sanitized;
            this._logger.LogDebug("Settings saved to {Path}", this._settingsPath);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Failed to save settings to {Path}", this._settingsPath);
            throw;
        }
    }

    public string? GetApiKey(ProviderId providerId)
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            return settings.Providers.TryGetValue(providerId.ToString(), out var ps) ? ps?.ApiKey : null;
        }
    }

    public bool IsProviderEnabled(ProviderId providerId)
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            return !settings.Providers.TryGetValue(providerId.ToString(), out var ps) || ps is null || ps.Enabled;
        }
    }

    /// <summary>
    /// Returns the OpenCode Go workspace ID from settings (env var takes precedence in the provider).
    /// </summary>
    /// <returns></returns>
    public string? GetOpenCodeGoWorkspaceId()
    {
        lock (this._lock)
        {
            return this.EnsureCached().OpenCodeGoWorkspaceId;
        }
    }

    /// <summary>
    /// Returns the configured Copilot account usernames.
    /// </summary>
    /// <returns></returns>
    public IReadOnlyList<string> GetCopilotAccounts()
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            return (settings.CopilotAccounts ?? []).ToList();
        }
    }

    public decimal? GetSessionBaseline(ProviderId providerId)
        => this.GetSessionBaseline(providerId.ToString());

    public void SetSessionBaseline(ProviderId providerId, decimal balance)
        => this.SetSessionBaseline(providerId.ToString(), balance);

    public decimal? GetSessionBaseline(string key)
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            return settings.SessionSpendingBaselines.TryGetValue(key, out var baseline)
                ? baseline
                : null;
        }
    }

    public void SetSessionBaseline(string key, decimal baseline)
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            settings.SessionSpendingBaselines[key] = baseline;
            settings.SessionSpendingResetTimes[key] = DateTimeOffset.Now;
            this.SaveInternal(settings);
        }
    }

    public DateTimeOffset? GetSessionResetTime(ProviderId providerId)
        => this.GetSessionResetTime(providerId.ToString());

    public DateTimeOffset? GetSessionResetTime(string key)
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            return settings.SessionSpendingResetTimes.TryGetValue(key, out var time)
                ? time
                : null;
        }
    }

    /// <summary>
    /// Returns the cached settings, initializing from disk if needed.
    /// Must be called while holding <see cref="@lock"/>. Does NOT deep-copy.
    /// </summary>
    private AppSettings EnsureCached()
    {
        if (this._cached is not null)
        {
            return this._cached;
        }

        if (!File.Exists(this._settingsPath))
        {
            this._logger.LogInformation("No settings file found at {Path}, using defaults", this._settingsPath);
            this._cached = CreateDefaults();
            try
            {
                this.SaveInternal(this._cached);
            }
            catch (Exception ex)
            {
                this._logger.LogWarning(ex, "Could not persist default settings to {Path}; continuing with in-memory defaults", this._settingsPath);
            }

            return this._cached;
        }

        try
        {
            var json = File.ReadAllText(this._settingsPath);
            this._cached = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? CreateDefaults();
            this._cached.Providers ??= [];
            NormalizeProviders(this._cached.Providers);

            this.SafeRestrictPermissions();

            this._logger.LogDebug("Settings loaded from {Path}", this._settingsPath);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Failed to load settings from {Path}, using defaults", this._settingsPath);
            this._cached = CreateDefaults();
        }

        return this._cached;
    }

    private static AppSettings CreateDefaults() => new()
    {
        RefreshIntervalSeconds = 120,
        Providers = new Dictionary<string, ProviderSettings>
        {
            [ProviderId.OpenRouter.ToString()] = new() { Enabled = true },
            [ProviderId.Copilot.ToString()] = new() { Enabled = true },
            [ProviderId.Claude.ToString()] = new() { Enabled = false },
            [ProviderId.OpenCodeGo.ToString()] = new() { Enabled = true }
        },
    };

    private static AppSettings DeepCopy(AppSettings source) => new()
    {
        RefreshIntervalSeconds = source.RefreshIntervalSeconds,
        CopilotAccounts = (source.CopilotAccounts ?? []).ToList(),
        OpenCodeGoWorkspaceId = source.OpenCodeGoWorkspaceId,
        ZoomLevel = source.ZoomLevel,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
        WindowLeft = source.WindowLeft,
        WindowTop = source.WindowTop,
        SessionSpendingBaselines = (source.SessionSpendingBaselines ?? []).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        SessionSpendingResetTimes = (source.SessionSpendingResetTimes ?? []).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        Providers = (source.Providers ?? [])
            .ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value is null ? new ProviderSettings() : new ProviderSettings
                {
                    Enabled = kvp.Value.Enabled,
                    ApiKey = kvp.Value.ApiKey
                }),
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

    [ExcludeFromCodeCoverage]
    private void SafeRestrictPermissions()
    {
        try
        {
            this.RestrictDirectoryPermissions(this._settingsDir);
            this.RestrictFilePermissions(this._settingsPath);
        }
        catch (Exception ex)
        {
            this._logger.LogWarning(ex, "Failed to restrict settings file permissions for {Path}", this._settingsPath);
        }
    }

    /// <summary>
    /// Restricts file permissions so only the current user can read/write.
    /// On Windows: sets an explicit ACL granting FullControl only to the current user.
    /// On Unix: sets file mode to owner read/write (chmod 600).
    /// </summary>
    [ExcludeFromCodeCoverage]
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
                SetUnixFilePermissions(filePath);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogDebug(ex, "Could not restrict file permissions on {Path}", filePath);
        }
    }

    /// <summary>
    /// Restricts directory permissions so only the current user can access it.
    /// On Windows: sets an explicit ACL granting FullControl only to the current user.
    /// On Unix: sets directory mode to owner-only (chmod 700).
    /// </summary>
    [ExcludeFromCodeCoverage]
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
                SetUnixDirectoryPermissions(dirPath);
            }
        }
        catch (Exception ex)
        {
            this._logger.LogDebug(ex, "Could not restrict directory permissions on {Path}", dirPath);
        }
    }

    [ExcludeFromCodeCoverage]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Only called on non-Windows platforms")]
    private static void SetUnixFilePermissions(string filePath) =>
        File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

    [ExcludeFromCodeCoverage]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "Only called on non-Windows platforms")]
    private static void SetUnixDirectoryPermissions(string dirPath) =>
        File.SetUnixFileMode(dirPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

    [ExcludeFromCodeCoverage]
    private static void BestEffortDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        { /* swallow — temp file removal is best-effort */
        }
    }
}
