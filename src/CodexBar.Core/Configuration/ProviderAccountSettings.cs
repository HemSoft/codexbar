// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.Core.Configuration;

using System.Text.Json.Serialization;
using CodexBar.Core.Models;

/// <summary>Account configuration without credential material. IDs do not change when labels change.</summary>
public sealed record ProviderAccountSettings
{
    public string Id { get; init; } = string.Empty;

    [JsonRequired]
    [JsonConverter(typeof(JsonStringEnumConverter<ProviderId>))]
    public ProviderId ProviderId { get; init; }

    public string DisplayLabel { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    [JsonConverter(typeof(JsonStringEnumConverter<ProviderAuthenticationMethod>))]
    public ProviderAuthenticationMethod AuthenticationMethod { get; init; }

    /// <summary>Gets the provider identity, such as a Copilot CLI username, when configured.</summary>
    public string? ExternalAccountId { get; init; }

    public string? WorkspaceId { get; init; }

    /// <summary>Gets the existing card key retained for compatibility with manual ordering.</summary>
    public string? LegacyCardKey { get; init; }
}

public enum ProviderAuthenticationMethod
{
    Automatic,
    ApiKey,
    CommandLine,
    OAuth,
    BrowserSession,
}
