using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexBar.Core.Models;
using CodexBar.Core.Services;

namespace CodexBar.App.ViewModels;

public sealed class MainViewModel : IDisposable
{
    private readonly UsageRefreshService _refreshService;

    public ObservableCollection<ProviderCardViewModel> Providers { get; } = new();

    // Stable key → card view model for reconciliation
    private readonly Dictionary<string, ProviderCardViewModel> _cardsByKey = new(StringComparer.OrdinalIgnoreCase);

    public MainViewModel(UsageRefreshService refreshService)
    {
        _refreshService = refreshService;
        _refreshService.UsageUpdated += OnUsageUpdated;

        // Initialize cards for non-Copilot/non-Claude providers (those use dynamic cards via Items)
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            if (id is ProviderId.Copilot or ProviderId.Claude) continue;

            var card = new ProviderCardViewModel
            {
                ProviderId = id,
                CardKey = id.ToString().ToLowerInvariant(),
                DisplayName = id.ToString(),
                StatusText = "Waiting…",
                UsedPercent = 0
            };
            Providers.Add(card);
            _cardsByKey[card.CardKey] = card;
        }
    }

    private void OnUsageUpdated(ProviderId id, ProviderUsageResult result)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (id == ProviderId.Copilot)
            {
                ReconcileCopilotCards(result);
                return;
            }

            if (id == ProviderId.Claude)
            {
                ReconcileItemCards(ProviderId.Claude, "claude:", result);
                return;
            }

            // Legacy single-card path for non-Copilot providers
            var key = id.ToString().ToLowerInvariant();
            if (!_cardsByKey.TryGetValue(key, out var card)) return;

            if (!result.Success)
            {
                card.StatusText = result.ErrorMessage ?? "Error";
                card.UsedPercent = 0;
                card.ResetText = null;
                card.WeeklyText = null;
                card.WeeklyPercent = 0;
                card.IsHighUsage = false;
                card.ShowUsagePercent = true;
                card.IsError = true;
                return;
            }

            card.IsError = false;
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
                card.ShowUsagePercent = !result.SessionUsage.IsUnlimited;
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

    /// <summary>
    /// Reconciles Copilot cards: update existing, add new, remove stale.
    /// </summary>
    private void ReconcileCopilotCards(ProviderUsageResult result) =>
        ReconcileItemCards(ProviderId.Copilot, "copilot:", result);

    /// <summary>
    /// Generic reconciliation for providers that use per-item cards (e.g., Copilot, Claude).
    /// </summary>
    private void ReconcileItemCards(ProviderId providerId, string keyPrefix, ProviderUsageResult result)
    {
        var items = result.Items;
        var errorKey = $"{keyPrefix}error";
        var providerDisplayName = providerId.ToString();

        // If no items and overall failure, show a single error card
        if (items is null || items.Count == 0)
        {
            // Remove stale account cards so they don't linger alongside the error
            var staleAccountKeys = _cardsByKey.Keys
                .Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && k != errorKey)
                .ToList();
            foreach (var key in staleAccountKeys)
            {
                if (_cardsByKey.TryGetValue(key, out var staleCard))
                {
                    Providers.Remove(staleCard);
                    _cardsByKey.Remove(key);
                }
            }

            if (!_cardsByKey.TryGetValue(errorKey, out var errorCard))
            {
                errorCard = new ProviderCardViewModel
                {
                    ProviderId = providerId,
                    CardKey = errorKey,
                    DisplayName = providerDisplayName,
                    StatusText = result.ErrorMessage ?? "No accounts",
                    IsError = true,
                    ShowUsagePercent = false
                };
                Providers.Add(errorCard);
                _cardsByKey[errorKey] = errorCard;
            }
            else
            {
                errorCard.StatusText = result.ErrorMessage ?? "No accounts";
                errorCard.IsError = true;
                errorCard.ShowUsagePercent = false;
                errorCard.UsedPercent = 0;
                errorCard.WeeklyText = null;
                errorCard.WeeklyPercent = 0;
                errorCard.ResetText = null;
                errorCard.IsHighUsage = false;
            }
            return;
        }

        // Remove stale error card if items arrived
        if (_cardsByKey.TryGetValue(errorKey, out var staleError))
        {
            Providers.Remove(staleError);
            _cardsByKey.Remove(errorKey);
        }

        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            currentKeys.Add(item.Key);

            if (!_cardsByKey.TryGetValue(item.Key, out var card))
            {
                card = new ProviderCardViewModel
                {
                    ProviderId = providerId,
                    CardKey = item.Key,
                    DisplayName = item.DisplayName,
                    StatusText = "Loading…"
                };
                Providers.Add(card);
                _cardsByKey[item.Key] = card;
            }

            card.DisplayName = item.DisplayName;

            if (!item.Success)
            {
                card.StatusText = item.ErrorMessage ?? "Error";
                card.UsedPercent = 0;
                card.ResetText = null;
                card.WeeklyText = null;
                card.WeeklyPercent = 0;
                card.IsHighUsage = false;
                card.ShowUsagePercent = false;
                card.IsError = true;
                continue;
            }

            card.IsError = false;

            if (item.PrimaryUsage is not null)
            {
                card.UsedPercent = item.PrimaryUsage.UsedPercent;
                card.StatusText = item.PrimaryUsage.UsageLabel ?? $"{item.PrimaryUsage.UsedPercent:P0} used";
                card.ResetText = item.PrimaryUsage.ResetDescription;
                card.IsHighUsage = item.PrimaryUsage.UsedPercent >= 0.8;
                card.ShowUsagePercent = !item.PrimaryUsage.IsUnlimited;
            }
            else if (item.CreditsRemaining is not null)
            {
                card.StatusText = $"${item.CreditsRemaining:F2} remaining";
                card.UsedPercent = 0;
                card.IsHighUsage = false;
                card.ShowUsagePercent = false;
                card.ResetText = null;
            }
            else if (item.SecondaryUsage is not null)
            {
                // PrimaryUsage missing but secondary quota exists — show it as the main display.
                card.UsedPercent = item.SecondaryUsage.UsedPercent;
                card.StatusText = item.SecondaryUsage.UsageLabel ?? $"{item.SecondaryUsage.UsedPercent:P0} used";
                card.ResetText = item.SecondaryUsage.ResetDescription;
                card.IsHighUsage = item.SecondaryUsage.UsedPercent >= 0.8;
                card.ShowUsagePercent = !item.SecondaryUsage.IsUnlimited;
            }
            else
            {
                card.StatusText = "No data";
                card.UsedPercent = 0;
                card.ShowUsagePercent = true;
                card.ResetText = null;
                card.IsHighUsage = false;
            }

            if (item.PrimaryUsage is not null && item.SecondaryUsage is not null)
            {
                // Only show secondary as weekly when primary is present;
                // when secondary is promoted to the main display above, avoid duplication.
                card.WeeklyText = item.SecondaryUsage.UsageLabel;
                card.WeeklyPercent = item.SecondaryUsage.UsedPercent;
            }
            else
            {
                card.WeeklyText = null;
                card.WeeklyPercent = 0;
            }
        }

        // Remove cards for accounts that are no longer present
        var staleKeys = _cardsByKey.Keys
            .Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && !currentKeys.Contains(k))
            .ToList();

        foreach (var key in staleKeys)
        {
            if (_cardsByKey.TryGetValue(key, out var staleCard))
            {
                Providers.Remove(staleCard);
                _cardsByKey.Remove(key);
            }
        }
    }

    public void Dispose()
    {
        _refreshService.UsageUpdated -= OnUsageUpdated;
    }
}

public sealed class ProviderCardViewModel : INotifyPropertyChanged
{
    public ProviderId ProviderId { get; init; }

    /// <summary>Stable key for reconciliation (e.g., "gemini", "copilot:HemSoft").</summary>
    public string CardKey { get; init; } = "";

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
