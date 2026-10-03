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
    private string _settingsPath;
    private readonly string _fallbackSettingsPath;

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
        this._fallbackSettingsPath = Path.Combine(this._settingsDir, "codexbar-settings.json");
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
        this._fallbackSettingsPath = Path.Combine(this._settingsDir, "codexbar-settings.json");
    }

    public AppSettings Load()
    {
        lock (this._lock)
        {
            var cached = this.EnsureCached();
            AccountConfiguration.Migrate(cached);
            var draft = DeepCopy(cached);
            draft.AccountSnapshot = new AccountConfigurationSnapshot(draft);
            return draft;
        }
    }

    public void Save(AppSettings settings)
    {
        lock (this._lock)
        {
            this.SaveInternal(settings);
        }
    }

    /// <summary>
    /// Merges provider entries and credential fields that exist on disk but are absent from
    /// the in-memory settings. Prevents an in-flight Save from clobbering credentials that
    /// were added to the file while the app was running.
    /// </summary>
    private AppSettings MergeFromDisk(AppSettings settings, string? baselineKey)
    {
        this.UseFallbackSettingsPathIfPrimaryIsMissing();
        if (!File.Exists(this._settingsPath))
        {
            return settings;
        }

        var diskJson = File.ReadAllText(this._settingsPath);
        AppSettings? disk;
        try
        {
            disk = DeserializeDiskSettings(diskJson);
        }
        catch (JsonException ex)
        {
            this._logger.LogDebug(ex, "MergeFromDisk skipped — legacy settings are malformed at {Path}", this._settingsPath);
            return settings;
        }

        if (disk is null)
        {
            return settings;
        }

        if (baselineKey is not null)
        {
            // A background update owns only this baseline, not the cached account draft.
            // Apply its delta to the latest disk snapshot while holding the writer lock.
            disk.SessionSpendingBaselines ??= [];
            disk.SessionSpendingResetTimes ??= [];
            disk.SessionSpendingBaselines[baselineKey] = settings.SessionSpendingBaselines[baselineKey];
            disk.SessionSpendingResetTimes[baselineKey] = settings.SessionSpendingResetTimes[baselineKey];
            return disk;
        }

        if (settings.AccountConfigurationVersion == 0 && disk.AccountConfigurationVersion > 0)
        {
            settings.AccountConfigurationVersion = disk.AccountConfigurationVersion;
            settings.Accounts = disk.Accounts;
        }
        else if (disk.AccountConfigurationVersion > 0 && settings.AccountSnapshot is { } snapshot)
        {
            var proposed = MergeAccountDraft(settings, disk, snapshot);
            PreserveUnchangedAccountCompatibility(settings, disk, snapshot, proposed);
        }

        MergeProviders(settings, disk);
        MergeProviderCardOrder(settings, disk);
        MergeWorkspaceId(settings, disk);
        MergeCopilotBillingSettings(settings, disk);
        MergeCopilotKnownAccounts(settings, disk);
        MergeSessionBaselines(settings, disk);
        MergeSessionResetTimes(settings, disk);
        return settings;
    }

    private static List<ProviderAccountSettings> MergeAccountDraft(AppSettings settings, AppSettings disk, AccountConfigurationSnapshot snapshot)
    {
        var proposed = AccountConfiguration.Normalize(settings.Accounts);
        if (proposed.SequenceEqual(snapshot.Accounts))
        {
            settings.Accounts = disk.Accounts;
        }
        else if (!disk.Accounts.SequenceEqual(snapshot.Accounts) && !proposed.SequenceEqual(disk.Accounts))
        {
            throw new InvalidOperationException("Account configuration changed in another process. Do not overwrite it.");
        }

        return proposed;
    }

    private static void PreserveUnchangedAccountCompatibility(AppSettings settings, AppSettings disk, AccountConfigurationSnapshot snapshot, IEnumerable<ProviderAccountSettings> proposed)
    {
        if (settings.OpenCodeGoWorkspaceId == snapshot.WorkspaceId)
        {
            settings.OpenCodeGoWorkspaceId = disk.OpenCodeGoWorkspaceId;
        }

        if ((settings.CopilotAccounts ?? []).SequenceEqual(snapshot.CopilotAccounts))
        {
            settings.CopilotAccounts = (disk.CopilotAccounts ?? []).ToList();
        }

        if ((settings.CopilotKnownAccounts ?? []).SequenceEqual(snapshot.CopilotKnownAccounts))
        {
            settings.CopilotKnownAccounts = (disk.CopilotKnownAccounts ?? []).ToList();
        }

        var providers = settings.Providers ?? [];
        foreach (var (key, provider) in providers.ToList())
        {
            var originalStates = ProviderAccountStates(snapshot.Accounts, key).ToList();
            var statesUnchanged = ProviderAccountStates(proposed, key).SequenceEqual(originalStates);
            var snapshotKey = FindProviderKey(snapshot.ProviderStates, key) ?? key;
            var diskKey = FindProviderKey(disk.Providers, key);
            var credentialUnchanged = provider is not null && snapshot.ProviderApiKeys.TryGetValue(snapshotKey, out var originalCredential) && provider.ApiKey == originalCredential;
            if (credentialUnchanged && diskKey is null)
            {
                provider!.ApiKey = null;
            }

            if (provider is not null && snapshot.ProviderStates.TryGetValue(snapshotKey, out var original) && provider.Enabled == original && statesUnchanged)
            {
                if (diskKey is not null && disk.Providers?.TryGetValue(diskKey, out var saved) == true)
                {
                    provider.Enabled = saved?.Enabled ?? true;
                }
                else if (credentialUnchanged)
                {
                    providers.Remove(key);
                }
                else
                {
                    // Preserve a newly entered credential with the provider's default state.
                    provider.Enabled = true;
                }
            }

            var adoptedStates = ProviderAccountStates(settings.Accounts, key).ToList();
            if (provider is not null && statesUnchanged && originalStates.Count > 0 &&
                !adoptedStates.SequenceEqual(originalStates) && !adoptedStates.Any(account => account.Enabled))
            {
                // Visibility computed from the old draft cannot enable deleted/disabled disk accounts.
                provider.Enabled = false;
                providers[key] = provider;
            }
        }
    }

    private static string? FindProviderKey<T>(IReadOnlyDictionary<string, T>? providers, string key) =>
        providers?.Keys.FirstOrDefault(candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Id, bool Enabled)> ProviderAccountStates(IEnumerable<ProviderAccountSettings> accounts, string key) =>
        accounts.Where(account => string.Equals(account.ProviderId.ToString(), key, StringComparison.OrdinalIgnoreCase))
            .OrderBy(account => account.Id, StringComparer.Ordinal)
            .Select(account => (account.Id, account.Enabled));

    private static AppSettings? DeserializeDiskSettings(string json)
    {
        var version = ReadAccountVersion(json);
        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
        }
        catch (JsonException) when (version > 0)
        {
            throw new InvalidOperationException("Account configuration could not be read. Do not overwrite it.");
        }

        ValidateProviderKeys(settings?.Providers);
        if (settings is not null && version > 0)
        {
            settings.Accounts = NormalizeDiskAccounts(settings.Accounts);
        }

        return settings;
    }

    private static void ValidateProviderKeys(IReadOnlyDictionary<string, ProviderSettings>? providers)
    {
        if (providers?.Keys.GroupBy(key => key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1) == true)
        {
            throw new InvalidOperationException("Provider configuration contains ambiguous case variants. Do not overwrite it.");
        }
    }

    private static List<ProviderAccountSettings> NormalizeDiskAccounts(IEnumerable<ProviderAccountSettings>? accounts)
    {
        try
        {
            return AccountConfiguration.Normalize(accounts ?? throw new ArgumentException("Account configuration must contain an account list.", nameof(accounts)));
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("Account configuration could not be read. Do not overwrite it.");
        }
    }

    private static int ReadAccountVersion(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("accountConfigurationVersion", out var version))
        {
            return 0;
        }

        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number > AccountConfiguration.CurrentVersion || number < 0)
        {
            throw new InvalidOperationException("Account configuration version is not supported. Do not overwrite it.");
        }

        if (number > 0 && (!document.RootElement.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array))
        {
            throw new InvalidOperationException("Versioned account configuration must contain an accounts array. Do not overwrite it.");
        }

        return number;
    }

    /// <summary>
    /// Merges provider entries from disk into memory: preserves providers missing from
    /// memory and restores API keys that were cleared in memory but exist on disk.
    /// </summary>
    private static void MergeProviders(AppSettings settings, AppSettings disk)
    {
        settings.Providers ??= [];
        foreach (var (key, diskProvider) in disk.Providers ?? [])
        {
            var memoryKey = FindProviderKey(settings.Providers, key) ?? key;
            if (diskProvider is not null && settings.AccountSnapshot is { } snapshot &&
                snapshot.ProviderApiKeys.TryGetValue(FindProviderKey(snapshot.ProviderApiKeys, key) ?? key, out var original) &&
                settings.Providers.TryGetValue(memoryKey, out var memory) && memory is not null && memory.ApiKey == original)
            {
                // Credential changes are independent of account edits and visibility.
                memory.ApiKey = diskProvider.ApiKey;
            }

            MergeProviderEntry(settings.Providers, memoryKey, diskProvider);
        }
    }

    private static void MergeProviderEntry(Dictionary<string, ProviderSettings> providers, string key, ProviderSettings? diskProvider)
    {
        if (!providers.TryGetValue(key, out var memProvider))
        {
            providers[key] = diskProvider ?? new ProviderSettings();
            return;
        }

        if (diskProvider?.ApiKey is not null && string.IsNullOrWhiteSpace(memProvider.ApiKey))
        {
            memProvider.ApiKey = diskProvider.ApiKey;
        }
    }

    private static void MergeProviderCardOrder(AppSettings settings, AppSettings disk)
    {
        if ((settings.ProviderCardOrder?.Count ?? 0) == 0 && disk.ProviderCardOrder is { Count: > 0 })
        {
            settings.ProviderCardOrder = disk.ProviderCardOrder.ToList();
        }
    }

    /// <summary>
    /// Preserves the OpenCode Go workspace ID from disk when memory has none.
    /// </summary>
    private static void MergeWorkspaceId(AppSettings settings, AppSettings disk)
    {
        if ((settings.AccountConfigurationVersion == 0 || settings.Accounts.SequenceEqual(disk.Accounts)) && string.IsNullOrWhiteSpace(settings.OpenCodeGoWorkspaceId) && !string.IsNullOrWhiteSpace(disk.OpenCodeGoWorkspaceId))
        {
            settings.OpenCodeGoWorkspaceId = disk.OpenCodeGoWorkspaceId;
        }
    }

    /// <summary>
    /// Preserves Copilot billing settings from disk when memory has no override.
    /// </summary>
    private static void MergeCopilotBillingSettings(AppSettings settings, AppSettings disk)
    {
        if (ShouldPreserveStringSetting(settings.CopilotEnterprise, disk.CopilotEnterprise))
        {
            settings.CopilotEnterprise = disk.CopilotEnterprise;
        }

        if (ShouldPreserveStringSetting(settings.CopilotOrganization, disk.CopilotOrganization))
        {
            settings.CopilotOrganization = disk.CopilotOrganization;
        }

        if (ShouldPreserveValue(settings.CopilotPoolTotal, disk.CopilotPoolTotal))
        {
            settings.CopilotPoolTotal = disk.CopilotPoolTotal;
        }
    }

    private static void MergeCopilotKnownAccounts(AppSettings settings, AppSettings disk)
    {
        if ((settings.AccountConfigurationVersion == 0 || settings.Accounts.SequenceEqual(disk.Accounts)) && (settings.CopilotKnownAccounts?.Count ?? 0) == 0 && disk.CopilotKnownAccounts is { Count: > 0 })
        {
            settings.CopilotKnownAccounts = disk.CopilotKnownAccounts.ToList();
        }
    }

    private static bool ShouldPreserveStringSetting(string? settingsValue, string? diskValue) =>
        string.IsNullOrWhiteSpace(settingsValue) && !string.IsNullOrWhiteSpace(diskValue);

    private static bool ShouldPreserveValue<T>(T? settingsValue, T? diskValue)
        where T : struct =>
        settingsValue is null && diskValue is not null;

    /// <summary>
    /// Preserves session spending baselines from disk that are not in memory.
    /// </summary>
    private static void MergeSessionBaselines(AppSettings settings, AppSettings disk)
    {
        settings.SessionSpendingBaselines ??= [];
        foreach (var (key, diskBaseline) in disk.SessionSpendingBaselines ?? [])
        {
            settings.SessionSpendingBaselines.TryAdd(key, diskBaseline);
        }
    }

    /// <summary>
    /// Preserves session spending reset times from disk that are not in memory.
    /// </summary>
    private static void MergeSessionResetTimes(AppSettings settings, AppSettings disk)
    {
        settings.SessionSpendingResetTimes ??= [];
        foreach (var (key, diskTime) in disk.SessionSpendingResetTimes ?? [])
        {
            settings.SessionSpendingResetTimes.TryAdd(key, diskTime);
        }
    }

    private void SaveInternal(AppSettings settings, string? baselineKey = null)
    {
        try
        {
            // Every writer, including session-baseline updates, uses this protocol.
            // Exclusive sharing rejects another process instead of blocking the UI.
            Directory.CreateDirectory(this._settingsDir);
            this.RestrictDirectoryPermissions(this._settingsDir);
            using var writeLock = new FileStream(Path.Combine(this._settingsDir, "settings.write.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            ValidateProviderKeys(settings.Providers);
            settings = this.MergeFromDisk(settings, baselineKey);
            AccountConfiguration.Migrate(settings);
            var sanitized = SanitizeForPersistence(settings);
            var persistedPath = this.WriteSettingsFileWithFallback(sanitized);

            this._cached = sanitized;
            settings.AccountSnapshot = new AccountConfigurationSnapshot(sanitized);
            this._logger.LogDebug("Settings saved to {Path}", persistedPath);
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Failed to save settings to {Path}", this._settingsPath);
            throw;
        }
    }

    private string WriteSettingsFileWithFallback(AppSettings sanitized)
    {
        try
        {
            this.WriteSettingsFile(sanitized, this._settingsPath);
            return this._settingsPath;
        }
        catch (Exception ex) when (IsSettingsPathUnavailable(ex) && !string.Equals(this._settingsPath, this._fallbackSettingsPath, StringComparison.OrdinalIgnoreCase))
        {
            this._logger.LogWarning(ex, "Settings path {Path} is not writable; falling back to {FallbackPath}", this._settingsPath, this._fallbackSettingsPath);
            this.WriteSettingsFile(sanitized, this._fallbackSettingsPath);
            this._settingsPath = this._fallbackSettingsPath;
            return this._settingsPath;
        }
    }

    private static bool IsSettingsPathUnavailable(Exception ex) =>
        ex is UnauthorizedAccessException or IOException;

    private void WriteSettingsFile(AppSettings sanitized, string settingsPath)
    {
        Directory.CreateDirectory(this._settingsDir);
        this.RestrictDirectoryPermissions(this._settingsDir);
        var json = JsonSerializer.Serialize(sanitized, JsonOptions);

        // Write to a temp file and atomically move into place.
        // Permissions are set at creation time (see FileSecurityHelper.WriteRestrictedFile)
        // so no window exists where the file is world-readable.
        var tempPath = settingsPath + ".tmp";
        FileSecurityHelper.WriteRestrictedFile(tempPath, json);
        try
        {
            File.Move(tempPath, settingsPath, overwrite: true);
        }
        catch
        {
            BestEffortDelete(tempPath);
            throw;
        }
    }

    private static AppSettings SanitizeForPersistence(AppSettings settings)
    {
        return new AppSettings
        {
            AccountConfigurationVersion = settings.AccountConfigurationVersion,
            Accounts = AccountConfiguration.Normalize(settings.Accounts),
            RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
            CopilotAccounts = NormalizeStringList(settings.CopilotAccounts),
            CopilotKnownAccounts = NormalizeStringList(settings.CopilotKnownAccounts),
            CopilotEnterprise = NormalizeCopilotEnterprise(settings.CopilotEnterprise),
            CopilotOrganization = NormalizeCopilotOrganization(settings.CopilotOrganization),
            CopilotPoolTotal = NormalizeCopilotPoolTotal(settings.CopilotPoolTotal),
            ProviderCardOrder = NormalizeProviderCardOrder(settings.ProviderCardOrder),
            OpenCodeGoWorkspaceId = NullIfWhitespace(settings.OpenCodeGoWorkspaceId),
            ZoomLevel = NormalizeZoom(settings.ZoomLevel),
            WindowWidth = settings.WindowWidth,
            WindowHeight = settings.WindowHeight,
            WindowLeft = settings.WindowLeft,
            WindowTop = settings.WindowTop,
            SessionSpendingBaselines = CloneDictionary(settings.SessionSpendingBaselines),
            SessionSpendingResetTimes = CloneDictionary(settings.SessionSpendingResetTimes),
            Providers = SanitizeProviders(settings.Providers),
        };
    }

    private static string? NullIfWhitespace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string NormalizeCopilotEnterprise(string? value) =>
        string.IsNullOrWhiteSpace(value) ? new AppSettings().CopilotEnterprise : value.Trim();

    private static string NormalizeCopilotOrganization(string? value) =>
        string.IsNullOrWhiteSpace(value) ? new AppSettings().CopilotOrganization : value.Trim();

    private static decimal? NormalizeCopilotPoolTotal(decimal? value) =>
        value is > 0 ? value : null;

    private static double NormalizeZoom(double? value) =>
        value is > 0 and <= 5 ? value.Value : 1.0;

    private static Dictionary<TKey, TValue> CloneDictionary<TKey, TValue>(Dictionary<TKey, TValue>? source)
        where TKey : notnull =>
        (source ?? []).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

    private static List<string> NormalizeStringList(IEnumerable<string>? values) =>
        (values ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> NormalizeProviderCardOrder(IEnumerable<string>? order) =>
        (order ?? [])
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, ProviderSettings> SanitizeProviders(Dictionary<string, ProviderSettings>? providers) =>
        (providers ?? []).ToDictionary(
            kvp => kvp.Key,
            kvp => new ProviderSettings
            {
                Enabled = kvp.Value?.Enabled ?? true,
                ApiKey = NullIfWhitespace(kvp.Value?.ApiKey),
            });

    public string? GetApiKey(ProviderId providerId)
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            return settings.Providers.FirstOrDefault(entry => string.Equals(entry.Key, providerId.ToString(), StringComparison.OrdinalIgnoreCase)).Value?.ApiKey;
        }
    }

    public bool IsProviderEnabled(ProviderId providerId)
    {
        lock (this._lock)
        {
            var settings = this.EnsureCached();
            var entry = settings.Providers.FirstOrDefault(entry => string.Equals(entry.Key, providerId.ToString(), StringComparison.OrdinalIgnoreCase));
            return entry.Key is not null
                ? entry.Value is null || entry.Value.Enabled
                : providerId != ProviderId.Moonshot;
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
            var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this.EnsureCached(), JsonOptions), JsonOptions)!;
            settings.SessionSpendingBaselines[key] = baseline;
            settings.SessionSpendingResetTimes[key] = DateTimeOffset.Now;
            this.SaveInternal(settings, key);
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

        this.UseFallbackSettingsPathIfPrimaryIsMissing();
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
            this._cached = DeserializeDiskSettings(json) ?? CreateDefaults();
            this._cached.Providers ??= [];
            NormalizeProviders(this._cached.Providers);
            this._cached.CopilotEnterprise = NormalizeCopilotEnterprise(this._cached.CopilotEnterprise);
            this._cached.CopilotOrganization = NormalizeCopilotOrganization(this._cached.CopilotOrganization);
            this._cached.CopilotPoolTotal = NormalizeCopilotPoolTotal(this._cached.CopilotPoolTotal);

            this.SafeRestrictPermissions();

            this._logger.LogDebug("Settings loaded from {Path}", this._settingsPath);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            this._logger.LogWarning(ex, "Failed to load settings from {Path}, using defaults", this._settingsPath);
            this._cached = CreateDefaults();
        }

        return this._cached;
    }

    private void UseFallbackSettingsPathIfPrimaryIsMissing()
    {
        if (!File.Exists(this._settingsPath) &&
            File.Exists(this._fallbackSettingsPath))
        {
            this._settingsPath = this._fallbackSettingsPath;
        }
    }

    private static AppSettings CreateDefaults() => new()
    {
        RefreshIntervalSeconds = 120,
        Providers = new Dictionary<string, ProviderSettings>
        {
            [ProviderId.OpenRouter.ToString()] = new() { Enabled = true },
            [ProviderId.Copilot.ToString()] = new() { Enabled = true },
            [ProviderId.Claude.ToString()] = new() { Enabled = false },
            [ProviderId.Codex.ToString()] = new() { Enabled = true },
            [ProviderId.Cursor.ToString()] = new() { Enabled = true },
            [ProviderId.OpenCodeGo.ToString()] = new() { Enabled = true },
            [ProviderId.OpenCodeZen.ToString()] = new() { Enabled = true },
            [ProviderId.Moonshot.ToString()] = new() { Enabled = false }
        },
    };

    private static AppSettings DeepCopy(AppSettings source) => new()
    {
        AccountConfigurationVersion = source.AccountConfigurationVersion,
        Accounts = AccountConfiguration.Normalize(source.Accounts),
        AccountSnapshot = source.AccountSnapshot,
        RefreshIntervalSeconds = source.RefreshIntervalSeconds,
        CopilotAccounts = NormalizeStringList(source.CopilotAccounts),
        CopilotKnownAccounts = NormalizeStringList(source.CopilotKnownAccounts),
        CopilotEnterprise = NormalizeCopilotEnterprise(source.CopilotEnterprise),
        CopilotOrganization = NormalizeCopilotOrganization(source.CopilotOrganization),
        CopilotPoolTotal = NormalizeCopilotPoolTotal(source.CopilotPoolTotal),
        ProviderCardOrder = NormalizeProviderCardOrder(source.ProviderCardOrder),
        OpenCodeGoWorkspaceId = source.OpenCodeGoWorkspaceId,
        ZoomLevel = source.ZoomLevel,
        WindowWidth = source.WindowWidth,
        WindowHeight = source.WindowHeight,
        WindowLeft = source.WindowLeft,
        WindowTop = source.WindowTop,
        SessionSpendingBaselines = (source.SessionSpendingBaselines ?? []).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        SessionSpendingResetTimes = (source.SessionSpendingResetTimes ?? []).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        Providers = source.Providers.ToDictionary(
            kvp => kvp.Key,
            kvp => new ProviderSettings
            {
                Enabled = kvp.Value.Enabled,
                ApiKey = kvp.Value.ApiKey,
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

    /// <summary>
    /// Applies restrictive file-system permissions as a defense-in-depth measure.
    /// Delegates to RestrictDirectoryPermissions/RestrictFilePermissions which each
    /// have their own try/catch and are [ExcludeFromCodeCoverage].
    /// </summary>
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
