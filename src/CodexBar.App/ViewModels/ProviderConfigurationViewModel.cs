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
        Action close)
    {
        this._settingsService = settingsService;
        this._close = close;
        this._settings = settingsService.Load();
        this.Providers = new ObservableCollection<ProviderOptionViewModel>(
            providers
                .Select(p => ProviderOptionViewModel.From(p.Metadata, this.GetProviderSettings(p.Metadata.Id).Enabled))
                .OrderBy(p => p.ProviderId));
        this.SaveCommand = new RelayCommand(_ => this.Save());
        this.CancelCommand = new RelayCommand(_ => close());
    }

    public ObservableCollection<ProviderOptionViewModel> Providers { get; }

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public event EventHandler? Saved;

    private ProviderSettings GetProviderSettings(ProviderId providerId)
    {
        this._settings.Providers ??= [];
        var key = providerId.ToString();
        if (!this._settings.Providers.TryGetValue(key, out var providerSettings) || providerSettings is null)
        {
            providerSettings = new ProviderSettings();
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

        this._settingsService.Save(this._settings);
        this.Saved?.Invoke(this, EventArgs.Empty);
        this._close();
    }
}

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class ProviderOptionViewModel : INotifyPropertyChanged
{
    private bool isDisplayed;

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
        get => this.isDisplayed;
        set => this.SetField(ref this.isDisplayed, value);
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
