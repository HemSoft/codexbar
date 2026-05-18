// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.ViewModels;

using System.Collections.ObjectModel;
using CodexBar.Core.Models;

/// <summary>
/// Reconciles provider usage data into <see cref="ProviderCardViewModel"/> instances.
/// Extracted from <see cref="MainViewModel"/> to reduce cyclomatic complexity and enable unit testing.
/// </summary>
internal sealed class ItemCardReconciler
{
    private const double HighUsageThreshold = 0.8;

    private readonly Dictionary<string, ProviderCardViewModel> _cardsByKey;
    private readonly ObservableCollection<ProviderCardViewModel> _providers;
    private readonly Action<ProviderCardViewModel> _updateOverageSessionSpending;
    private readonly Func<string, System.Windows.Input.ICommand> _createResetCommand;

    internal ItemCardReconciler(
        Dictionary<string, ProviderCardViewModel> cardsByKey,
        ObservableCollection<ProviderCardViewModel> providers,
        Action<ProviderCardViewModel> updateOverageSessionSpending,
        Func<string, System.Windows.Input.ICommand> createResetCommand)
    {
        this._cardsByKey = cardsByKey;
        this._providers = providers;
        this._updateOverageSessionSpending = updateOverageSessionSpending;
        this._createResetCommand = createResetCommand;
    }

    /// <summary>
    /// Reconciles a provider's usage result into the card collection.
    /// Creates, updates, and removes cards to match the current state.
    /// </summary>
    internal void Reconcile(ProviderId providerId, string keyPrefix, ProviderUsageResult result)
    {
        var items = result.Items;
        var errorKey = $"{keyPrefix}error";

        if (items is null || items.Count == 0)
        {
            this.HandleEmptyItems(providerId, keyPrefix, errorKey, result.ErrorMessage);
            return;
        }

        this.RemoveCard(errorKey);

        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            currentKeys.Add(item.Key);
            this.ReconcileSingleItem(providerId, item);
        }

        this.RemoveStaleCards(keyPrefix, currentKeys);
    }

    /// <summary>
    /// Handles the case when a provider returns no items — shows an error card
    /// and removes any stale account cards.
    /// </summary>
    internal void HandleEmptyItems(ProviderId providerId, string keyPrefix, string errorKey, string? errorMessage)
    {
        this.RemoveAllAccountCards(keyPrefix, errorKey);
        this.EnsureErrorCard(providerId, errorKey, errorMessage);
    }

    /// <summary>
    /// Removes all cards matching the prefix except the specified exclude key.
    /// Used when items are empty to clear stale account cards.
    /// </summary>
    internal void RemoveAllAccountCards(string keyPrefix, string excludeKey)
    {
        var staleKeys = this._cardsByKey.Keys
            .Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && k != excludeKey)
            .ToList();

        foreach (var key in staleKeys)
        {
            this.RemoveCard(key);
        }
    }

    /// <summary>
    /// Removes cards whose keys match the prefix but are not in the current keys set.
    /// Used after reconciliation to clean up accounts that no longer exist.
    /// </summary>
    internal void RemoveStaleCards(string keyPrefix, HashSet<string> currentKeys)
    {
        var staleKeys = this._cardsByKey.Keys
            .Where(k => k.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && !currentKeys.Contains(k))
            .ToList();

        foreach (var key in staleKeys)
        {
            this.RemoveCard(key);
        }
    }

    /// <summary>
    /// Creates or updates an error card for the given key.
    /// </summary>
    internal void EnsureErrorCard(ProviderId providerId, string errorKey, string? errorMessage)
    {
        var displayName = providerId.ToString();
        var message = errorMessage ?? "No accounts";

        if (!this._cardsByKey.TryGetValue(errorKey, out var errorCard))
        {
            errorCard = new ProviderCardViewModel
            {
                ProviderId = providerId,
                CardKey = errorKey,
                DisplayName = displayName,
                StatusText = message,
                IsError = true,
                ShowUsagePercent = false,
            };
            this._providers.Add(errorCard);
            this._cardsByKey[errorKey] = errorCard;
            return;
        }

        ResetCardToError(errorCard, message);
    }

    /// <summary>
    /// Resets an existing card to an error state, clearing all usage data.
    /// </summary>
    internal static void ResetCardToError(ProviderCardViewModel card, string errorMessage)
    {
        card.StatusText = errorMessage;
        card.IsError = true;
        card.ShowUsagePercent = false;
        card.UsedPercent = 0;
        card.WeeklyText = null;
        card.WeeklyPercent = 0;
        card.ResetText = null;
        card.IsHighUsage = false;
        card.IsCreditsDisplay = false;
        card.CreditsBalance = null;
        card.Bars.Clear();
        card.HasBars = false;
    }

    /// <summary>
    /// Reconciles a single usage item: creates or updates its card.
    /// </summary>
    internal void ReconcileSingleItem(ProviderId providerId, UsageItem item)
    {
        var card = this.GetOrCreateItemCard(providerId, item);
        card.DisplayName = item.DisplayName;

        if (!item.Success)
        {
            ApplyItemError(card, item.ErrorMessage);
            return;
        }

        this.ApplyItemSuccess(card, item);
    }

    /// <summary>
    /// Gets an existing card or creates a new one for the given item.
    /// </summary>
    internal ProviderCardViewModel GetOrCreateItemCard(ProviderId providerId, UsageItem item)
    {
        if (this._cardsByKey.TryGetValue(item.Key, out var card))
        {
            return card;
        }

        card = new ProviderCardViewModel
        {
            ProviderId = providerId,
            CardKey = item.Key,
            DisplayName = item.DisplayName,
            StatusText = "Loading…",
            ResetSessionSpendingCommand = this._createResetCommand(item.Key),
        };
        this._providers.Add(card);
        this._cardsByKey[item.Key] = card;
        return card;
    }

    /// <summary>
    /// Applies error state to a card when an item's fetch failed.
    /// </summary>
    internal static void ApplyItemError(ProviderCardViewModel card, string? errorMessage)
    {
        card.StatusText = errorMessage ?? "Error";
        card.UsedPercent = 0;
        card.ResetText = null;
        card.WeeklyText = null;
        card.WeeklyPercent = 0;
        card.IsHighUsage = false;
        card.ShowUsagePercent = false;
        card.IsError = true;
        card.IsCreditsDisplay = false;
        card.CreditsBalance = null;
        card.Bars.Clear();
        card.HasBars = false;
        card.OverageCost = null;
        card.SessionSpending = null;
        card.SessionResetTime = null;
    }

    /// <summary>
    /// Applies successful item data, dispatching to multi-bar or legacy display.
    /// </summary>
    internal void ApplyItemSuccess(ProviderCardViewModel card, UsageItem item)
    {
        card.IsError = false;
        card.IsCreditsDisplay = false;
        card.CreditsBalance = null;

        if (item.Bars is { Count: > 0 })
        {
            ApplyMultiBarDisplay(card, item);
            return;
        }

        this.ApplyLegacyDisplay(card, item);
    }

    /// <summary>
    /// Applies multi-bar display when the item provides labelled bars.
    /// </summary>
    internal static void ApplyMultiBarDisplay(ProviderCardViewModel card, UsageItem item)
    {
        ReconcileBars(card, item.Bars!);
        card.HasBars = true;
        card.ShowUsagePercent = false;
        card.WeeklyText = null;
        card.WeeklyPercent = 0;
        card.ResetText = null;

        if (item.PrimaryUsage is not null)
        {
            card.StatusText = item.PrimaryUsage.UsageLabel ?? "No data";
            card.UsedPercent = item.PrimaryUsage.UsedPercent;
            card.IsHighUsage = item.PrimaryUsage.UsedPercent >= HighUsageThreshold;
        }
        else
        {
            card.StatusText = "No data";
            card.UsedPercent = 0;
            card.IsHighUsage = false;
        }

        card.OverageCost = null;
        card.SessionSpending = null;
        card.SessionResetTime = null;
    }

    /// <summary>
    /// Applies legacy single-bar display with primary/fallback usage.
    /// </summary>
    internal void ApplyLegacyDisplay(ProviderCardViewModel card, UsageItem item)
    {
        card.Bars.Clear();
        card.HasBars = false;

        ApplyPrimaryOrFallbackUsage(card, item);
        ApplySecondaryAsWeekly(card, item);
        this.ApplyOverageCost(card, item);
    }

    /// <summary>
    /// Applies primary usage, or falls back to credits, secondary usage, or "No data".
    /// </summary>
    internal static void ApplyPrimaryOrFallbackUsage(ProviderCardViewModel card, UsageItem item)
    {
        if (item.PrimaryUsage is not null)
        {
            card.UsedPercent = item.PrimaryUsage.UsedPercent;
            card.StatusText = item.PrimaryUsage.UsageLabel ?? $"{item.PrimaryUsage.UsedPercent:P0} used";
            card.ResetText = item.PrimaryUsage.ResetDescription;
            card.IsHighUsage = item.PrimaryUsage.UsedPercent >= HighUsageThreshold;
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
            card.UsedPercent = item.SecondaryUsage.UsedPercent;
            card.StatusText = item.SecondaryUsage.UsageLabel ?? $"{item.SecondaryUsage.UsedPercent:P0} used";
            card.ResetText = item.SecondaryUsage.ResetDescription;
            card.IsHighUsage = item.SecondaryUsage.UsedPercent >= HighUsageThreshold;
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
    }

    /// <summary>
    /// Applies secondary usage as weekly display when primary is also present.
    /// </summary>
    internal static void ApplySecondaryAsWeekly(ProviderCardViewModel card, UsageItem item)
    {
        if (item.PrimaryUsage is not null && item.SecondaryUsage is not null)
        {
            card.WeeklyText = item.SecondaryUsage.UsageLabel;
            card.WeeklyPercent = item.SecondaryUsage.UsedPercent;
        }
        else
        {
            card.WeeklyText = null;
            card.WeeklyPercent = 0;
        }
    }

    /// <summary>
    /// Applies overage cost data to the card and triggers session spending update.
    /// </summary>
    internal void ApplyOverageCost(ProviderCardViewModel card, UsageItem item)
    {
        if (item.OverageCost is not null)
        {
            card.OverageCost = item.OverageCost;
            this._updateOverageSessionSpending(card);
        }
        else
        {
            card.OverageCost = null;
            card.SessionSpending = null;
            card.SessionResetTime = null;
        }
    }

    /// <summary>
    /// Reconciles the bars collection on a card VM to match the incoming bar data.
    /// </summary>
    internal static void ReconcileBars(ProviderCardViewModel card, IReadOnlyList<UsageBar> bars)
    {
        for (int i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            if (i < card.Bars.Count)
            {
                var existing = card.Bars[i];
                existing.Label = bar.Label;
                existing.UsedPercent = bar.UsedPercent;
                existing.ResetDescription = bar.ResetDescription;
                existing.IsHighUsage = bar.UsedPercent >= HighUsageThreshold;
            }
            else
            {
                card.Bars.Add(new UsageBarViewModel
                {
                    Label = bar.Label,
                    UsedPercent = bar.UsedPercent,
                    ResetDescription = bar.ResetDescription,
                    IsHighUsage = bar.UsedPercent >= HighUsageThreshold,
                });
            }
        }

        while (card.Bars.Count > bars.Count)
        {
            card.Bars.RemoveAt(card.Bars.Count - 1);
        }
    }

    private void RemoveCard(string key)
    {
        if (this._cardsByKey.TryGetValue(key, out var card))
        {
            this._providers.Remove(card);
            this._cardsByKey.Remove(key);
        }
    }
}
