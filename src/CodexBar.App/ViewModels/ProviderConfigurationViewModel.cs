// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Providers;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class ProviderConfigurationViewModel
{
    private readonly ISettingsService _settingsService;
    private readonly Action _close;
    private AppSettings _settings;

    public ProviderConfigurationViewModel(
        ISettingsService settingsService,
        IEnumerable<IUsageProvider> providers,
        Action close,
        IEnumerable<ProviderCardViewModel>? currentProviderCards = null)
    {
        this._settingsService = settingsService;
        this._close = close;
        this._settings = settingsService.Load();
        this.Providers = new ObservableCollection<ProviderOptionViewModel>(
            providers
                .Select(p => ProviderOptionViewModel.From(p.Metadata, this.GetProviderSettings(p.Metadata.Id).Enabled))
                .OrderBy(p => p.ProviderId));
        this.CopilotAccounts = BuildCopilotAccountOptions(this._settings, currentProviderCards);
        this.SaveCommand = new RelayCommand(_ => this.Save());
        this.CancelCommand = new RelayCommand(_ => close());
    }

    public ObservableCollection<ProviderOptionViewModel> Providers { get; }

    public ObservableCollection<CopilotAccountOptionViewModel> CopilotAccounts { get; }

    public bool HasCopilotAccounts => this.CopilotAccounts.Count > 0;

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public event EventHandler? Saved;

    private ProviderSettings GetProviderSettings(ProviderId providerId)
    {
        this._settings.Providers ??= [];
        var key = providerId.ToString();
        if (!this._settings.Providers.TryGetValue(key, out var providerSettings) || providerSettings is null)
        {
            providerSettings = new ProviderSettings { Enabled = providerId != ProviderId.Moonshot };
            this._settings.Providers[key] = providerSettings;
        }

        return providerSettings;
    }

    private void Save()
    {
        this._settings = this._settingsService.Load();
        this._settings.Providers ??= [];
        foreach (var provider in this.Providers)
        {
            var providerSettings = this.GetProviderSettings(provider.ProviderId);
            providerSettings.Enabled = provider.IsDisplayed;
        }

        this.SaveCopilotAccountSettings();
        this._settingsService.Save(this._settings);
        this.Saved?.Invoke(this, EventArgs.Empty);
        this._close();
    }

    private void SaveCopilotAccountSettings()
    {
        if (this.CopilotAccounts.Count == 0)
        {
            return;
        }

        var selectedAccounts = this.CopilotAccounts
            .Where(account => account.IsEnabled)
            .Select(account => account.Username)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        this._settings.CopilotKnownAccounts = this.CopilotAccounts
            .Select(account => account.Username)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedAccounts.Count > 0)
        {
            this._settings.CopilotAccounts = selectedAccounts;
            return;
        }

        var copilotOption = this.Providers.FirstOrDefault(provider => provider.ProviderId == ProviderId.Copilot);
        if (copilotOption?.IsDisplayed == true)
        {
            copilotOption.IsDisplayed = false;
            this.GetProviderSettings(ProviderId.Copilot).Enabled = false;
        }

        this._settings.CopilotAccounts = [];
    }

    private static ObservableCollection<CopilotAccountOptionViewModel> BuildCopilotAccountOptions(
        AppSettings settings,
        IEnumerable<ProviderCardViewModel>? currentProviderCards)
    {
        var configuredAccounts = NormalizeAccountList(settings.CopilotAccounts);
        var knownAccounts = NormalizeAccountList(settings.CopilotKnownAccounts);
        var configuredSet = new HashSet<string>(configuredAccounts, StringComparer.OrdinalIgnoreCase);
        var accounts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var isCopilotProviderEnabled = IsCopilotProviderEnabled(settings);

        foreach (var card in currentProviderCards ?? [])
        {
            if (card.ProviderId != ProviderId.Copilot ||
                !TryGetCopilotUsername(card.CardKey, out var username))
            {
                continue;
            }

            accounts[username] = GetCopilotAccountDisplayName(username);
        }

        foreach (var username in knownAccounts.Concat(configuredAccounts))
        {
            accounts.TryAdd(username, GetCopilotAccountDisplayName(username));
        }

        var hasExplicitSelection = configuredSet.Count > 0;
        return new ObservableCollection<CopilotAccountOptionViewModel>(
            accounts
                .OrderBy(account => GetCopilotAccountSortOrder(account.Key))
                .ThenBy(account => account.Value, StringComparer.OrdinalIgnoreCase)
                .Select(account => new CopilotAccountOptionViewModel(
                    account.Key,
                    account.Value,
                    isEnabled: isCopilotProviderEnabled && (!hasExplicitSelection || configuredSet.Contains(account.Key)))));
    }

    private static List<string> NormalizeAccountList(IEnumerable<string>? accounts) =>
        (accounts ?? [])
            .Where(account => !string.IsNullOrWhiteSpace(account))
            .Select(account => account.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsCopilotProviderEnabled(AppSettings settings) =>
        settings.Providers is null ||
        !settings.Providers.TryGetValue(ProviderId.Copilot.ToString(), out var providerSettings) ||
        providerSettings?.Enabled != false;

    private static bool TryGetCopilotUsername(string cardKey, out string username)
    {
        const string Prefix = "copilot:";
        username = string.Empty;

        if (!cardKey.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        username = cardKey[Prefix.Length..];
        return username.Length > 0 &&
               !string.Equals(username, "error", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCopilotAccountDisplayName(string username) =>
        username.ToLowerInvariant() switch
        {
            "fhemmerrelias" => "Copilot Work (fhemmerrelias)",
            "hemsoft" => "Copilot Home (HemSoft)",
            _ => $"Copilot {username}",
        };

    private static int GetCopilotAccountSortOrder(string username) =>
        username.ToLowerInvariant() switch
        {
            "fhemmerrelias" => 0,
            "hemsoft" => 1,
            _ => 2,
        };
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class ProviderOptionViewModel : INotifyPropertyChanged
{
    private bool _isDisplayed;

    private ProviderOptionViewModel(ProviderMetadata metadata, bool isDisplayed)
    {
        this.ProviderId = metadata.Id;
        this.DisplayName = metadata.DisplayName;
        this.Description = metadata.Description;
        this.IsDisplayed = isDisplayed;
    }

    public ProviderId ProviderId { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool IsDisplayed
    {
        get => this._isDisplayed;
        set => this.SetField(ref this._isDisplayed, value);
    }

    public static ProviderOptionViewModel From(ProviderMetadata metadata, bool isDisplayed) => new(metadata, isDisplayed);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class CopilotAccountOptionViewModel : INotifyPropertyChanged
{
    private bool _isEnabled;

    public CopilotAccountOptionViewModel(string username, string displayName, bool isEnabled)
    {
        this.Username = username;
        this.DisplayName = displayName;
        this.IsEnabled = isEnabled;
    }

    public string Username { get; }

    public string DisplayName { get; }

    public bool IsEnabled
    {
        get => this._isEnabled;
        set => this.SetField(ref this._isEnabled, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
