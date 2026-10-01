// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.ViewModels;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;

public sealed class AccountOptionViewModel : INotifyPropertyChanged
{
    private readonly ProviderAccountSettings _original;
    private string _displayLabel;
    private bool _enabled;
    private ProviderAuthenticationMethod _authenticationMethod;
    private string? _externalAccountId;
    private string? _workspaceId;

    public AccountOptionViewModel(ProviderAccountSettings account)
    {
        this._original = account;
        this._displayLabel = account.DisplayLabel;
        this._enabled = account.Enabled;
        this._authenticationMethod = account.AuthenticationMethod;
        this._externalAccountId = account.ExternalAccountId;
        this._workspaceId = account.WorkspaceId;
    }

    public string Id => this._original.Id;

    public ProviderId ProviderId => this._original.ProviderId;

    public string ProviderName => this.ProviderId.ToString();

    public bool IsOpenCodeGo => this.ProviderId == ProviderId.OpenCodeGo;

    public bool IsCopilot => this.ProviderId == ProviderId.Copilot;

    public string DisplayLabel { get => this._displayLabel; set => this.SetField(ref this._displayLabel, value); }

    public bool Enabled { get => this._enabled; set => this.SetField(ref this._enabled, value); }

    public ProviderAuthenticationMethod AuthenticationMethod { get => this._authenticationMethod; set => this.SetField(ref this._authenticationMethod, value); }

    public string? ExternalAccountId { get => this._externalAccountId; set => this.SetField(ref this._externalAccountId, value); }

    public string? WorkspaceId { get => this._workspaceId; set => this.SetField(ref this._workspaceId, value); }

    public IReadOnlyList<AuthenticationMethodOption> AuthenticationMethods { get; } =
    [
        new(ProviderAuthenticationMethod.Automatic, "Automatic"),
        new(ProviderAuthenticationMethod.ApiKey, "API key"),
        new(ProviderAuthenticationMethod.CommandLine, "CLI"),
        new(ProviderAuthenticationMethod.OAuth, "OAuth"),
        new(ProviderAuthenticationMethod.BrowserSession, "Browser session"),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProviderAccountSettings ToSettings() => AccountConfiguration.Validate(this._original with
    {
        DisplayLabel = this.DisplayLabel,
        Enabled = this.Enabled,
        AuthenticationMethod = this.AuthenticationMethod,
        ExternalAccountId = this.ExternalAccountId,
        WorkspaceId = this.WorkspaceId,
    });

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record AuthenticationMethodOption(ProviderAuthenticationMethod Method, string Label);
