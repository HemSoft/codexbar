using CodexBar.Core.Models;

namespace CodexBar.Core.Providers;

/// <summary>
/// Contract for all AI provider usage fetchers.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Static metadata about this provider.</summary>
    ProviderMetadata Metadata { get; }

    /// <summary>Whether the provider is currently configured and ready to fetch.</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Fetch the current usage snapshot.</summary>
    Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default);
}
