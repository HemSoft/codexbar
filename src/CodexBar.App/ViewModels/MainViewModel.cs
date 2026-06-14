// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using CodexBar.App;
using CodexBar.Core.Configuration;
using CodexBar.Core.Models;
using CodexBar.Core.Services;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage]
public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly UsageRefreshService refreshService;
    private readonly ISettingsService settingsService;
    private readonly DispatcherTimer refreshIndicatorTimer;
    private DateTimeOffset? nextRefreshAtUtc;
    private bool isDisposed;

    public ObservableCollection<ProviderCardViewModel> Providers { get; } = [];

    private bool showRefreshIndicator;

    public bool ShowRefreshIndicator
    {
        get => this.showRefreshIndicator;
        private set => this.SetField(ref this.showRefreshIndicator, value);
    }

    private double refreshIndicatorProgress;

    public double RefreshIndicatorProgress
    {
        get => this.refreshIndicatorProgress;
        private set => this.SetField(ref this.refreshIndicatorProgress, value);
    }

    private string refreshIndicatorToolTip = "Next auto refresh unavailable";

    public string RefreshIndicatorToolTip
    {
        get => this.refreshIndicatorToolTip;
        private set => this.SetField(ref this.refreshIndicatorToolTip, value);
    }

    // Stable key → card view model for reconciliation
    private readonly Dictionary<string, ProviderCardViewModel> cardsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ItemCardReconciler _itemCardReconciler;

    public MainViewModel(UsageRefreshService refreshService, ISettingsService settingsService)
    {
        this.refreshService = refreshService;
        this.settingsService = settingsService;
        this._itemCardReconciler = new ItemCardReconciler(
            this.cardsByKey,
            this.Providers,
            this.UpdateOverageSessionSpending,
            key => new RelayCommand(_ => this.ResetOverageSessionSpending(key)));
        this.refreshService.UsageUpdated += this.OnUsageUpdated;
        this.refreshService.NextRefreshChanged += this.OnNextRefreshChanged;
        this.refreshIndicatorTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        this.refreshIndicatorTimer.Tick += this.RefreshIndicatorTimer_Tick;

        // Initialize cards for providers that do not use dynamic Items reconciliation.
        foreach (ProviderId id in Enum.GetValues<ProviderId>())
        {
            if (id is ProviderId.Copilot or ProviderId.Claude or ProviderId.Codex or ProviderId.OpenCodeGo)
            {
                continue;
            }

            var card = new ProviderCardViewModel
            {
                ProviderId = id,
                CardKey = id.ToString().ToLowerInvariant(),
                DisplayName = id.ToString(),
                StatusText = "Waiting…",
                UsedPercent = 0,
                IsCompactCard = id is ProviderId.OpenRouter or ProviderId.OpenCodeZen,
                ResetSessionSpendingCommand = new RelayCommand(_ => this.ResetSessionSpending(id)),
            };
            this.Providers.Add(card);
            this.cardsByKey[card.CardKey] = card;
        }

        this.ApplyProviderCardOrder();
        this.PairCreditsCards();
        this.ReloadProviderVisibility();
        this.ApplyRefreshIndicatorState(RefreshIndicatorState.Calculate(DateTimeOffset.UtcNow, this.refreshService.NextRefreshAtUtc, this.refreshService.RefreshInterval));
    }

    public bool MoveProviderCard(string movedCardKey, string targetCardKey, bool insertAfter)
    {
        if (string.Equals(movedCardKey, targetCardKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var moved = this.Providers.FirstOrDefault(card => string.Equals(card.CardKey, movedCardKey, StringComparison.OrdinalIgnoreCase));
        var target = this.Providers.FirstOrDefault(card => string.Equals(card.CardKey, targetCardKey, StringComparison.OrdinalIgnoreCase));
        if (moved is null || target is null || moved.IsHiddenCompanion || target.IsHiddenCompanion)
        {
            return false;
        }

        var oldIndex = this.Providers.IndexOf(moved);
        this.Providers.RemoveAt(oldIndex);
        var targetIndex = this.Providers.IndexOf(target);
        var newIndex = insertAfter ? targetIndex + 1 : targetIndex;
        this.Providers.Insert(Math.Clamp(newIndex, 0, this.Providers.Count), moved);
        this.PairCreditsCards();
        this.SaveProviderCardOrder();
        return true;
    }

    private void ApplyProviderCardOrder()
    {
        var configuredOrder = this.settingsService.Load().ProviderCardOrder;
        if (configuredOrder.Count == 0 || this.Providers.Count <= 1)
        {
            return;
        }

        var order = configuredOrder
            .Select((key, index) => new { key, index })
            .ToDictionary(item => item.key, item => item.index, StringComparer.OrdinalIgnoreCase);

        var orderedCards = this.Providers
            .Select((card, index) => new { card, index })
            .OrderBy(item => order.TryGetValue(item.card.CardKey, out var configuredIndex) ? configuredIndex : int.MaxValue)
            .ThenBy(item => item.index)
            .Select(item => item.card)
            .ToList();

        for (var i = 0; i < orderedCards.Count; i++)
        {
            if (!ReferenceEquals(this.Providers[i], orderedCards[i]))
            {
                this.Providers.Move(this.Providers.IndexOf(orderedCards[i]), i);
            }
        }
    }

    private void SaveProviderCardOrder()
    {
        var settings = this.settingsService.Load();
        settings.ProviderCardOrder = this.Providers
            .Where(card => !card.IsHiddenCompanion)
            .Select(card => card.CardKey)
            .ToList();
        this.settingsService.Save(settings);
    }

    public void ReloadProviderVisibility()
    {
        foreach (var card in this.Providers)
        {
            card.IsProviderDisplayed = this.settingsService.IsProviderEnabled(card.ProviderId);
        }

        this.PairCreditsCards();
    }

    /// <summary>
    /// Pairs OpenRouter and OpenCode Zen cards so they display side-by-side
    /// in a single card with two columns. OpenRouter is the primary display,
    /// OpenCode Zen is the companion (hidden in the list, shown inside OpenRouter).
    /// </summary>
    private void PairCreditsCards()
    {
        var openRouterCard = this.Providers.FirstOrDefault(c => c.ProviderId == ProviderId.OpenRouter);
        var zenCard = this.Providers.FirstOrDefault(c => c.ProviderId == ProviderId.OpenCodeZen);

        if (openRouterCard is not null)
        {
            openRouterCard.CompanionCard = null;
            openRouterCard.IsPairedCredits = false;
        }

        if (zenCard is not null)
        {
            zenCard.IsHiddenCompanion = false;
        }

        if (openRouterCard is not null && zenCard is not null)
        {
            if (!openRouterCard.IsProviderDisplayed || !zenCard.IsProviderDisplayed)
            {
                return;
            }

            openRouterCard.CompanionCard = zenCard;
            openRouterCard.IsPairedCredits = true;
            zenCard.IsHiddenCompanion = true;
        }
    }

    private void OnUsageUpdated(ProviderId id, ProviderUsageResult result) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
    {
        if (this.isDisposed)
        {
            return;
        }

        if (id == ProviderId.Copilot)
        {
            this.ReconcileCopilotCards(result);
            return;
        }

        if (id == ProviderId.Claude)
        {
            this.ReconcileItemCards(ProviderId.Claude, "claude:", result);
            return;
        }

        if (id == ProviderId.Codex)
        {
            this.ReconcileItemCards(ProviderId.Codex, "codex:", result);
            return;
        }

        if (id == ProviderId.OpenCodeGo)
        {
            this.ReconcileItemCards(ProviderId.OpenCodeGo, "opencode-go:", result);
            return;
        }

        // Legacy single-card path for non-Copilot providers
        var key = id.ToString().ToLowerInvariant();
        if (!this.cardsByKey.TryGetValue(key, out var card))
        {
            return;
        }

        ApplyLegacyProviderResult(card, result);
        this.UpdateSessionSpending(card);
        this.ApplyVisibilityToProvider(id);
    });

    private void OnNextRefreshChanged(DateTimeOffset? nextRefreshAtUtc) => System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
    {
        if (this.isDisposed)
        {
            return;
        }

        this.nextRefreshAtUtc = nextRefreshAtUtc;
        this.UpdateRefreshIndicator();

        if (nextRefreshAtUtc is null)
        {
            this.refreshIndicatorTimer.Stop();
        }
        else if (!this.refreshIndicatorTimer.IsEnabled)
        {
            this.refreshIndicatorTimer.Start();
        }
    });

    private void RefreshIndicatorTimer_Tick(object? sender, EventArgs e)
    {
        if (this.isDisposed)
        {
            return;
        }

        this.UpdateRefreshIndicator();
        this.UpdateDynamicBarProjections(DateTimeOffset.UtcNow);
    }

    private void UpdateRefreshIndicator() =>
        this.ApplyRefreshIndicatorState(
            RefreshIndicatorState.Calculate(DateTimeOffset.UtcNow, this.nextRefreshAtUtc, this.refreshService.RefreshInterval));

    private void UpdateDynamicBarProjections(DateTimeOffset nowUtc)
    {
        foreach (var card in this.Providers)
        {
            foreach (var bar in card.Bars)
            {
                bar.UpdateProjection(nowUtc);
            }
        }
    }

    private void ApplyRefreshIndicatorState(RefreshIndicatorSnapshot state)
    {
        this.ShowRefreshIndicator = state.IsVisible;
        this.RefreshIndicatorProgress = state.Progress;
        this.RefreshIndicatorToolTip = state.ToolTipText;
    }

    /// <summary>
    /// Reconciles Copilot cards: update existing, add new, remove stale.
    /// </summary>
    private void ReconcileCopilotCards(ProviderUsageResult result) =>
        this.ReconcileItemCards(ProviderId.Copilot, "copilot:", result);

    /// <summary>
    /// Generic reconciliation for providers that use per-item cards (e.g., Copilot, Claude).
    /// Delegates to <see cref="ItemCardReconciler"/> for testability.
    /// </summary>
    private void ReconcileItemCards(ProviderId providerId, string keyPrefix, ProviderUsageResult result)
    {
        this._itemCardReconciler.Reconcile(providerId, keyPrefix, result);
        this.ApplyProviderCardOrder();
        this.ApplyVisibilityToProvider(providerId);
    }

    private void ApplyVisibilityToProvider(ProviderId providerId)
    {
        var isDisplayed = this.settingsService.IsProviderEnabled(providerId);
        foreach (var card in this.Providers.Where(c => c.ProviderId == providerId))
        {
            card.IsProviderDisplayed = isDisplayed;
        }

        this.PairCreditsCards();
    }

    /// <summary>
    /// Applies a provider usage result to a legacy single-card view model.
    /// Extracted for testability without WPF Dispatcher dependency.
    /// </summary>
    internal static void ApplyLegacyProviderResult(ProviderCardViewModel card, ProviderUsageResult result)
    {
        if (!result.Success)
        {
            ApplyLegacyError(card, result.ErrorMessage);
            return;
        }

        ResetLegacyCardDefaults(card);
        ApplyLegacySessionUsage(card, result);
        ApplyLegacyWeeklyUsage(card, result);
    }

    private static void ApplyLegacyError(ProviderCardViewModel card, string? errorMessage)
    {
        card.StatusText = errorMessage ?? "Error";
        card.UsedPercent = 0;
        card.IsCreditsDisplay = false;
        card.CreditsBalance = null;
        card.ResetText = null;
        card.WeeklyText = null;
        card.WeeklyPercent = 0;
        card.IsHighUsage = false;
        card.ShowUsagePercent = true;
        card.IsError = true;
        card.Bars.Clear();
        card.HasBars = false;
    }

    private static void ResetLegacyCardDefaults(ProviderCardViewModel card)
    {
        card.IsError = false;
        card.IsCreditsDisplay = false;
        card.CreditsBalance = null;
        card.StatusText = "No data";
        card.UsedPercent = 0;
        card.ResetText = null;
        card.WeeklyText = null;
        card.WeeklyPercent = 0;
        card.IsHighUsage = false;
        card.ShowUsagePercent = true;
        card.Bars.Clear();
        card.HasBars = false;
    }

    private static void ApplyLegacySessionUsage(ProviderCardViewModel card, ProviderUsageResult result)
    {
        if (result.SessionUsage is not null)
        {
            card.UsedPercent = result.SessionUsage.UsedPercent;
            card.StatusText = result.SessionUsage.UsageLabel ?? $"{result.SessionUsage.UsedPercent:P0} used";
            card.ResetText = result.SessionUsage.ResetDescription;
            card.IsHighUsage = result.SessionUsage.UsedPercent >= UsageSeverityThresholds.High;
            card.ShowUsagePercent = !result.SessionUsage.IsUnlimited;
        }
        else if (result.CreditsRemaining is not null)
        {
            card.StatusText = $"${result.CreditsRemaining:F2}";
            card.UsedPercent = 0;
            card.IsHighUsage = false;
            card.ShowUsagePercent = false;
            card.IsCreditsDisplay = true;
            card.CreditsBalance = result.CreditsRemaining;
        }
    }

    private static void ApplyLegacyWeeklyUsage(ProviderCardViewModel card, ProviderUsageResult result)
    {
        if (result.WeeklyUsage is not null)
        {
            card.WeeklyText = result.WeeklyUsage.UsageLabel;
            card.WeeklyPercent = result.WeeklyUsage.UsedPercent;
        }
    }

    private void UpdateSessionSpending(ProviderCardViewModel card)
    {
        if (card.CreditsBalance is not { } balance)
        {
            card.SessionSpending = null;
            card.SessionResetTime = null;
            return;
        }

        var baseline = this.settingsService.GetSessionBaseline(card.ProviderId);
        var result = SessionSpendingCalculator.CalculateCreditsSpending(balance, baseline);

        if (result.SetBaseline is { } newBaseline)
        {
            this.settingsService.SetSessionBaseline(card.ProviderId, newBaseline);
        }

        card.SessionSpending = result.SpendingText;
        card.SessionResetTime = SessionSpendingCalculator.FormatResetTime(this.settingsService.GetSessionResetTime(card.ProviderId));
    }

    private void UpdateOverageSessionSpending(ProviderCardViewModel card)
    {
        if (card.OverageCost is not { } overage)
        {
            card.SessionSpending = null;
            card.SessionResetTime = null;
            return;
        }

        var key = card.CardKey.ToLowerInvariant();
        var baseline = this.settingsService.GetSessionBaseline(key);
        var result = SessionSpendingCalculator.CalculateOverageSpending(overage, baseline);

        if (result.SetBaseline is { } newBaseline)
        {
            this.settingsService.SetSessionBaseline(key, newBaseline);
        }

        card.SessionSpending = result.SpendingText;
        card.SessionResetTime = SessionSpendingCalculator.FormatResetTime(this.settingsService.GetSessionResetTime(key));
    }

    private void ResetSessionSpending(ProviderId providerId)
    {
        var key = providerId.ToString().ToLowerInvariant();
        if (!this.cardsByKey.TryGetValue(key, out var card))
        {
            return;
        }

        if (card.CreditsBalance is { } balance)
        {
            this.settingsService.SetSessionBaseline(providerId, balance);
        }

        card.SessionSpending = "$0.00";
        card.SessionResetTime = SessionSpendingCalculator.FormatResetTime(this.settingsService.GetSessionResetTime(providerId));
    }

    private void ResetOverageSessionSpending(string cardKey)
    {
        if (!this.cardsByKey.TryGetValue(cardKey, out var card))
        {
            return;
        }

        if (card.OverageCost is { } overage)
        {
            this.settingsService.SetSessionBaseline(cardKey.ToLowerInvariant(), overage);
        }

        card.SessionSpending = "$0.00";
        card.SessionResetTime = SessionSpendingCalculator.FormatResetTime(this.settingsService.GetSessionResetTime(cardKey.ToLowerInvariant()));
    }

    public void Dispose()
    {
        this.isDisposed = true;
        this.refreshService.NextRefreshChanged -= this.OnNextRefreshChanged;
        this.refreshService.UsageUpdated -= this.OnUsageUpdated;
        this.refreshIndicatorTimer.Tick -= this.RefreshIndicatorTimer_Tick;
        this.refreshIndicatorTimer.Stop();
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

public sealed class UsageBarViewModel : INotifyPropertyChanged
{
    private static readonly string[] EasternTimeZoneIds = ["Eastern Standard Time", "America/New_York"];
    private static readonly Lazy<TimeZoneInfo?> EasternTimeZone = new(
        () => ResolveEasternTimeZone(EasternTimeZoneIds, TimeZoneInfo.FindSystemTimeZoneById));

    private string label = string.Empty;

    public string Label
    {
        get => this.label;
        set => this.SetField(ref this.label, value);
    }

    private double usedPercent;

    public double UsedPercent
    {
        get => this.usedPercent;
        set
        {
            if (this.SetField(ref this.usedPercent, value))
            {
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowProjectedUsage)));
            }
        }
    }

    private double projectedPercent;

    public double ProjectedPercent
    {
        get => this.projectedPercent;
        set
        {
            if (this.SetField(ref this.projectedPercent, value))
            {
                this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowProjectedUsage)));
            }
        }
    }

    public bool ShowProjectedUsage => this.ProjectedPercent > this.UsedPercent;

    private string? projectionDescription;

    public string? ProjectionDescription
    {
        get => this.projectionDescription;
        set => this.SetField(ref this.projectionDescription, value);
    }

    private string? resetDescription;

    public string? ResetDescription
    {
        get => this.resetDescription;
        set => this.SetField(ref this.resetDescription, value);
    }

    private decimal? projectionCurrent;

    public decimal? ProjectionCurrent
    {
        get => this.projectionCurrent;
        set => this.SetField(ref this.projectionCurrent, value);
    }

    private decimal? projectionLimit;

    public decimal? ProjectionLimit
    {
        get => this.projectionLimit;
        set => this.SetField(ref this.projectionLimit, value);
    }

    private DateTimeOffset? projectionPeriodStart;

    public DateTimeOffset? ProjectionPeriodStart
    {
        get => this.projectionPeriodStart;
        set => this.SetField(ref this.projectionPeriodStart, value);
    }

    private DateTimeOffset? projectionPeriodEnd;

    public DateTimeOffset? ProjectionPeriodEnd
    {
        get => this.projectionPeriodEnd;
        set => this.SetField(ref this.projectionPeriodEnd, value);
    }

    private bool showProjectionOnCurrentBar;

    public bool ShowProjectionOnCurrentBar
    {
        get => this.showProjectionOnCurrentBar;
        set => this.SetField(ref this.showProjectionOnCurrentBar, value);
    }

    private bool isHighUsage;

    public bool IsHighUsage
    {
        get => this.isHighUsage;
        set => this.SetField(ref this.isHighUsage, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateProjection(DateTimeOffset nowUtc)
    {
        if (this.ProjectionCurrent is not > 0
            || this.ProjectionLimit is not > 0
            || this.ProjectionPeriodStart is not { } periodStart
            || this.ProjectionPeriodEnd is not { } periodEnd)
        {
            this.ProjectedPercent = 0;
            this.ProjectionDescription = null;
            return;
        }

        var projected = ProjectMonthEnd(this.ProjectionCurrent.Value, periodStart, periodEnd, nowUtc);
        var projectedPercent = Math.Clamp((double)(projected / this.ProjectionLimit.Value), 0.0, 1.0);
        var limitHit = FormatLimitHit(this.ProjectionCurrent.Value, this.ProjectionLimit.Value, periodStart, periodEnd, nowUtc);
        if (this.ShowProjectionOnCurrentBar)
        {
            this.ProjectedPercent = projectedPercent;
            this.ProjectionDescription = $"Projected {projectedPercent:P0} at current pace · {limitHit}";
            return;
        }

        this.Label = $"Month end est. · {projected:N0} / {this.ProjectionLimit.Value:N0}";
        this.UsedPercent = projectedPercent;
        this.ResetDescription = limitHit;
        this.ProjectedPercent = 0;
        this.ProjectionDescription = null;
    }

    private static decimal ProjectMonthEnd(decimal current, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset nowUtc)
    {
        var elapsed = (decimal)(nowUtc - periodStart).TotalSeconds;
        if (elapsed <= 0 || nowUtc >= periodEnd)
        {
            return current;
        }

        var total = (decimal)(periodEnd - periodStart).TotalSeconds;
        return current * total / elapsed;
    }

    internal static string FormatLimitHit(decimal current, decimal limit, DateTimeOffset periodStart, DateTimeOffset periodEnd, DateTimeOffset nowUtc)
    {
        if (current >= limit)
        {
            return "Limit reached";
        }

        var elapsed = (decimal)(nowUtc - periodStart).TotalSeconds;
        if (elapsed <= 0)
        {
            return "Limit hit unknown";
        }

        var ratePerSecond = current / elapsed;
        if (ratePerSecond <= 0)
        {
            return "Limit hit unknown";
        }

        var secondsToLimit = (double)(limit / ratePerSecond);
        var hitAt = periodStart.AddSeconds(secondsToLimit);
        return hitAt > periodEnd ? "Limit not reached" : $"Limit hit {FormatEasternTime(hitAt)}";
    }

    internal static string FormatEasternTime(DateTimeOffset timestamp, TimeZoneInfo? easternTimeZone)
    {
        if (easternTimeZone is null)
        {
            return $"{timestamp.ToUniversalTime():MMM d h:mm tt} UTC";
        }

        var easternTime = TimeZoneInfo.ConvertTime(timestamp, easternTimeZone);
        return $"{easternTime:MMM d h:mm tt} ET";
    }

    private static string FormatEasternTime(DateTimeOffset timestamp) => FormatEasternTime(timestamp, EasternTimeZone.Value);

    internal static TimeZoneInfo? ResolveEasternTimeZone(IEnumerable<string> timeZoneIds, Func<string, TimeZoneInfo> findTimeZoneById)
    {
        foreach (var timeZoneId in timeZoneIds)
        {
            try
            {
                return findTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return null;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

public sealed class ProviderCardViewModel : INotifyPropertyChanged
{
    public ProviderId ProviderId { get; init; }

    /// <summary>Gets stable key for reconciliation (e.g., "gemini", "copilot:HemSoft").</summary>
    public string CardKey { get; init; } = string.Empty;

    private string displayName = string.Empty;

    public string DisplayName
    {
        get => this.displayName;
        set => this.SetField(ref this.displayName, value);
    }

    private string statusText = "Waiting…";

    public string StatusText
    {
        get => this.statusText;
        set => this.SetField(ref this.statusText, value);
    }

    private string? resetText;

    public string? ResetText
    {
        get => this.resetText;
        set => this.SetField(ref this.resetText, value);
    }

    private string? weeklyText;

    public string? WeeklyText
    {
        get => this.weeklyText;
        set => this.SetField(ref this.weeklyText, value);
    }

    private double usedPercent;

    public double UsedPercent
    {
        get => this.usedPercent;
        set => this.SetField(ref this.usedPercent, value);
    }

    private double weeklyPercent;

    public double WeeklyPercent
    {
        get => this.weeklyPercent;
        set => this.SetField(ref this.weeklyPercent, value);
    }

    private bool isError;

    public bool IsError
    {
        get => this.isError;
        set => this.SetField(ref this.isError, value);
    }

    private bool isHighUsage;

    public bool IsHighUsage
    {
        get => this.isHighUsage;
        set => this.SetField(ref this.isHighUsage, value);
    }

    private bool showUsagePercent = true;

    public bool ShowUsagePercent
    {
        get => this.showUsagePercent;
        set => this.SetField(ref this.showUsagePercent, value);
    }

    private bool hasBars;

    public bool HasBars
    {
        get => this.hasBars;
        set
        {
            if (this.hasBars == value)
            {
                return;
            }

            this.hasBars = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.HasBars)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowProgressBar)));
        }
    }

    private bool isCreditsDisplay;

    public bool IsCreditsDisplay
    {
        get => this.isCreditsDisplay;
        set
        {
            if (this.isCreditsDisplay == value)
            {
                return;
            }

            this.isCreditsDisplay = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsCreditsDisplay)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowProgressBar)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowSingleCreditsDisplay)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowStatusTextLine)));
        }
    }

    /// <summary>Gets a value indicating whether true when the card should show a progress bar (not a credits display, not a multi-bar card, not a paired credits card).</summary>
    public bool ShowProgressBar => !this.HasBars && !this.IsCreditsDisplay && !this.IsPairedCredits;

    /// <summary>Gets a value indicating whether the single-card credits block should render.</summary>
    public bool ShowSingleCreditsDisplay => this.IsCreditsDisplay && !this.IsPairedCredits;

    /// <summary>Gets a value indicating whether the compact status text line should render for usage cards.</summary>
    public bool ShowStatusTextLine => !this.IsCreditsDisplay && !this.IsPairedCredits;

    private bool isCompactCard;

    /// <summary>
    /// Gets or sets a value indicating whether this card should render in compact/half-width mode
    /// to sit side-by-side with another compact card (e.g., two credits cards in a row).
    /// </summary>
    public bool IsCompactCard
    {
        get => this.isCompactCard;
        set => this.SetField(ref this.isCompactCard, value);
    }

    /// <summary>
    /// Gets multi-bar usage display. When populated, the UI renders one bar per entry
    /// instead of the legacy single progress bar.
    /// </summary>
    public ObservableCollection<UsageBarViewModel> Bars { get; } = [];

    private ProviderCardViewModel? companionCard;

    /// <summary>
    /// Gets or sets the companion card for paired credits display.
    /// When set, this card shows both its own and the companion's credits side-by-side.
    /// Used to pair OpenRouter + OpenCode Zen credits in a single card.
    /// </summary>
    public ProviderCardViewModel? CompanionCard
    {
        get => this.companionCard;
        set => this.SetField(ref this.companionCard, value);
    }

    private bool isPairedCredits;

    /// <summary>
    /// Gets or sets a value indicating whether this card displays paired credits (own + companion).
    /// </summary>
    public bool IsPairedCredits
    {
        get => this.isPairedCredits;
        set
        {
            if (this.isPairedCredits == value)
            {
                return;
            }

            this.isPairedCredits = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsPairedCredits)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowSingleCreditsDisplay)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowProgressBar)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.ShowStatusTextLine)));
        }
    }

    private bool isHiddenCompanion;

    /// <summary>
    /// Gets or sets a value indicating whether this card is hidden because it's displayed
    /// inside its companion card's paired layout.
    /// </summary>
    public bool IsHiddenCompanion
    {
        get => this.isHiddenCompanion;
        set
        {
            if (this.isHiddenCompanion == value)
            {
                return;
            }

            this.isHiddenCompanion = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsHiddenCompanion)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsCardVisible)));
        }
    }

    private bool _isProviderDisplayed = true;

    public bool IsProviderDisplayed
    {
        get => this._isProviderDisplayed;
        set
        {
            if (this._isProviderDisplayed == value)
            {
                return;
            }

            this._isProviderDisplayed = value;
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsProviderDisplayed)));
            this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(this.IsCardVisible)));
        }
    }

    public bool IsCardVisible => this.IsProviderDisplayed && !this.IsHiddenCompanion;

    private decimal? creditsBalance;

    /// <summary>
    /// Gets or sets the current numeric credit balance for session-spending calculations.
    /// </summary>
    public decimal? CreditsBalance
    {
        get => this.creditsBalance;
        set => this.SetField(ref this.creditsBalance, value);
    }

    private decimal? overageCost;

    /// <summary>
    /// Gets or sets the current overage cost for cumulative session-spending calculations (Copilot).
    /// </summary>
    public decimal? OverageCost
    {
        get => this.overageCost;
        set => this.SetField(ref this.overageCost, value);
    }

    private string? sessionSpending;

    /// <summary>
    /// Gets or sets the formatted session spending since last reset (e.g., "$1.23").
    /// </summary>
    public string? SessionSpending
    {
        get => this.sessionSpending;
        set => this.SetField(ref this.sessionSpending, value);
    }

    private string? sessionResetTime;

    /// <summary>
    /// Gets or sets the formatted time of the last session spending reset.
    /// </summary>
    public string? SessionResetTime
    {
        get => this.sessionResetTime;
        set => this.SetField(ref this.sessionResetTime, value);
    }

    private System.Windows.Input.ICommand? resetSessionSpendingCommand;

    /// <summary>
    /// Gets or sets the command to reset session spending for this provider.
    /// </summary>
    public System.Windows.Input.ICommand? ResetSessionSpendingCommand
    {
        get => this.resetSessionSpendingCommand;
        set => this.SetField(ref this.resetSessionSpendingCommand, value);
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

/// <summary>
/// Minimal relay command for binding button clicks to actions.
/// </summary>
public sealed class RelayCommand(Action<object?> execute) : ICommand
{
#pragma warning disable CS0067 // Required by ICommand; WPF infrastructure raises this event
    public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
