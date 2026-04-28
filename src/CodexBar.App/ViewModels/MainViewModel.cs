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

        // Initialize cards for non-Copilot/non-Claude/non-OpenCodeGo providers
        // (those three use dynamic cards via Items reconciliation)
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            if (id is ProviderId.Copilot or ProviderId.Claude or ProviderId.OpenCodeGo) continue;

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

            if (id == ProviderId.OpenCodeGo)
            {
                ReconcileItemCards(ProviderId.OpenCodeGo, "opencode-go:", result);
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
                card.Bars.Clear();
                card.HasBars = false;
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
            card.Bars.Clear();
            card.HasBars = false;

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
                card.StatusText = $"${result.CreditsRemaining:F2}";
                card.UsedPercent = 0;
                card.IsHighUsage = false;
                card.ShowUsagePercent = false;
                card.IsCreditsDisplay = true;
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
                errorCard.Bars.Clear();
                errorCard.HasBars = false;
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
                card.Bars.Clear();
                card.HasBars = false;
                continue;
            }

            card.IsError = false;
            card.IsCreditsDisplay = false;

            // Multi-bar display: if the item provides labelled bars, use them
            if (item.Bars is { Count: > 0 })
            {
                ReconcileBars(card, item.Bars);
                card.HasBars = true;

                // Hide the legacy single-bar display
                card.ShowUsagePercent = false;
                card.WeeklyText = null;
                card.WeeklyPercent = 0;
                card.ResetText = null;

                // Use PrimaryUsage for the status text only
                if (item.PrimaryUsage is not null)
                {
                    card.StatusText = item.PrimaryUsage.UsageLabel ?? "No data";
                    card.UsedPercent = item.PrimaryUsage.UsedPercent;
                    card.IsHighUsage = item.PrimaryUsage.UsedPercent >= 0.8;
                }
                else
                {
                    card.StatusText = "No data";
                    card.UsedPercent = 0;
                    card.IsHighUsage = false;
                }
                continue;
            }

            // Legacy single-bar path
            card.Bars.Clear();
            card.HasBars = false;

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
                card.StatusText = $"${item.CreditsRemaining:F2}";
                card.UsedPercent = 0;
                card.IsHighUsage = false;
                card.ShowUsagePercent = false;
                card.IsCreditsDisplay = true;
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

    /// <summary>
    /// Reconciles the bars collection on a card VM to match the incoming bar data.
    /// </summary>
    private static void ReconcileBars(ProviderCardViewModel card, IReadOnlyList<UsageBar> bars)
    {
        // Simple reconciliation: update in-place where possible, add/remove as needed
        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (i < card.Bars.Count)
            {
                var existing = card.Bars[i];
                existing.Label = bar.Label;
                existing.UsedPercent = bar.UsedPercent;
                existing.ResetDescription = bar.ResetDescription;
                existing.IsHighUsage = bar.UsedPercent >= 0.8;
            }
            else
            {
                card.Bars.Add(new UsageBarViewModel
                {
                    Label = bar.Label,
                    UsedPercent = bar.UsedPercent,
                    ResetDescription = bar.ResetDescription,
                    IsHighUsage = bar.UsedPercent >= 0.8
                });
            }
        }

        // Remove excess bars
        while (card.Bars.Count > bars.Count)
            card.Bars.RemoveAt(card.Bars.Count - 1);
    }

    public void Dispose()
    {
        _refreshService.UsageUpdated -= OnUsageUpdated;
    }
}

public sealed class UsageBarViewModel : INotifyPropertyChanged
{
    private string _label = "";
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    private double _usedPercent;
    public double UsedPercent
    {
        get => _usedPercent;
        set => SetField(ref _usedPercent, value);
    }

    private string? _resetDescription;
    public string? ResetDescription
    {
        get => _resetDescription;
        set => SetField(ref _resetDescription, value);
    }

    private bool _isHighUsage;
    public bool IsHighUsage
    {
        get => _isHighUsage;
        set => SetField(ref _isHighUsage, value);
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

    private bool _hasBars;
    public bool HasBars
    {
        get => _hasBars;
        set
        {
            if (_hasBars == value) return;
            _hasBars = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBars)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressBar)));
        }
    }

    private bool _isCreditsDisplay;
    public bool IsCreditsDisplay
    {
        get => _isCreditsDisplay;
        set
        {
            if (_isCreditsDisplay == value) return;
            _isCreditsDisplay = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCreditsDisplay)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowProgressBar)));
        }
    }

    /// <summary>True when the card should show a progress bar (not a credits display, not a multi-bar card).</summary>
    public bool ShowProgressBar => !HasBars && !IsCreditsDisplay;

    /// <summary>
    /// Multi-bar usage display. When populated, the UI renders one bar per entry
    /// instead of the legacy single progress bar.
    /// </summary>
    public ObservableCollection<UsageBarViewModel> Bars { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
