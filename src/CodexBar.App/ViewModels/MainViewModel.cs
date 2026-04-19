using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexBar.Core.Models;
using CodexBar.Core.Services;

namespace CodexBar.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly UsageRefreshService _refreshService;

    public ObservableCollection<ProviderCardViewModel> Providers { get; } = new();

    private bool _isRefreshing;
    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => SetField(ref _isRefreshing, value);
    }

    public MainViewModel(UsageRefreshService refreshService)
    {
        _refreshService = refreshService;
        _refreshService.UsageUpdated += OnUsageUpdated;

        // Initialize cards for all known providers
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            Providers.Add(new ProviderCardViewModel
            {
                ProviderId = id,
                DisplayName = id.ToString(),
                StatusText = "Waiting…",
                UsedPercent = 0
            });
        }
    }

    private void OnUsageUpdated(ProviderId id, ProviderUsageResult result)
    {
        // Marshal to UI thread
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            var card = Providers.FirstOrDefault(p => p.ProviderId == id);
            if (card is null) return;

            if (!result.Success)
            {
                card.StatusText = result.ErrorMessage ?? "Error";
                card.UsedPercent = 0;
                card.ResetText = null;
                card.WeeklyText = null;
                card.WeeklyPercent = 0;
                card.IsHighUsage = false;
                card.IsError = true;
                return;
            }

            card.IsError = false;

            // Reset all fields to avoid stale data from a previous result shape
            card.StatusText = "No data";
            card.UsedPercent = 0;
            card.ResetText = null;
            card.WeeklyText = null;
            card.WeeklyPercent = 0;
            card.IsHighUsage = false;
            card.ShowUsagePercent = true;

            if (result.SessionUsage is not null)
            {
                card.UsedPercent = result.SessionUsage.UsedPercent;
                card.StatusText = result.SessionUsage.UsageLabel ?? $"{result.SessionUsage.UsedPercent:P0} used";
                card.ResetText = result.SessionUsage.ResetDescription;
                card.IsHighUsage = result.SessionUsage.UsedPercent >= 0.8;
            }
            else if (result.CreditsRemaining is not null)
            {
                card.StatusText = $"${result.CreditsRemaining:F2} remaining";
                card.UsedPercent = 0;
                card.IsHighUsage = false;
                card.ShowUsagePercent = false;
            }
            else
            {
                card.StatusText = "No data";
            }

            if (result.WeeklyUsage is not null)
            {
                card.WeeklyText = result.WeeklyUsage.UsageLabel;
                card.WeeklyPercent = result.WeeklyUsage.UsedPercent;
            }
        });
    }

    public void Dispose()
    {
        _refreshService.UsageUpdated -= OnUsageUpdated;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class ProviderCardViewModel : INotifyPropertyChanged
{
    public ProviderId ProviderId { get; init; }

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    private string _statusText = "Waiting…";
    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    private string? _resetText;
    public string? ResetText
    {
        get => _resetText;
        set => SetField(ref _resetText, value);
    }

    private string? _weeklyText;
    public string? WeeklyText
    {
        get => _weeklyText;
        set => SetField(ref _weeklyText, value);
    }

    private double _usedPercent;
    public double UsedPercent
    {
        get => _usedPercent;
        set => SetField(ref _usedPercent, value);
    }

    private double _weeklyPercent;
    public double WeeklyPercent
    {
        get => _weeklyPercent;
        set => SetField(ref _weeklyPercent, value);
    }

    private bool _isError;
    public bool IsError
    {
        get => _isError;
        set => SetField(ref _isError, value);
    }

    private bool _isHighUsage;
    public bool IsHighUsage
    {
        get => _isHighUsage;
        set => SetField(ref _isHighUsage, value);
    }

    private bool _showUsagePercent = true;
    public bool ShowUsagePercent
    {
        get => _showUsagePercent;
        set => SetField(ref _showUsagePercent, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
