using CodexBar.Core.Models;

namespace CodexBar.Core.Configuration;

/// <summary>
/// Contract for reading CodexBar settings and provider credentials.
/// Implemented by <see cref="SettingsService"/> and can be substituted
/// in tests to avoid filesystem side effects.
/// </summary>
public interface ISettingsService
{
    AppSettings Load();
    void Save(AppSettings settings);
    string? GetApiKey(ProviderId providerId);
    bool IsProviderEnabled(ProviderId providerId);
    string? GetOpenCodeGoWorkspaceId();
    IReadOnlyList<string> GetCopilotAccounts();
}
