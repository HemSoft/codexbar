// <copyright file="IUsageProvider.cs" company="PlaceholderCompany">
// Copyright (c) PlaceholderCompany. All rights reserved.
// </copyright>

namespace CodexBar.Core.Providers;

using CodexBar.Core.Models;

/// <summary>
/// Contract for all AI provider usage fetchers.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Gets static metadata about this provider.</summary>
    ProviderMetadata Metadata { get; }

    /// <summary>Whether the provider is currently configured and ready to fetch.</summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Fetch the current usage snapshot.</summary>
    /// <returns><placeholder>A <see cref="Task"/> representing the asynchronous operation.</placeholder></returns>
    Task<ProviderUsageResult> FetchUsageAsync(CancellationToken ct = default);
}
