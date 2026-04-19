namespace CodexBar.Core.Models;

/// <summary>
/// Static metadata describing a provider.
/// </summary>
public sealed record ProviderMetadata
{
    public required ProviderId Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public string? DashboardUrl { get; init; }
    public string? StatusPageUrl { get; init; }

    /// <summary>Whether this provider tracks session-level usage.</summary>
    public bool SupportsSessionUsage { get; init; }

    /// <summary>Whether this provider tracks weekly usage.</summary>
    public bool SupportsWeeklyUsage { get; init; }

    /// <summary>Whether this provider uses a credit system.</summary>
    public bool SupportsCredits { get; init; }
}
