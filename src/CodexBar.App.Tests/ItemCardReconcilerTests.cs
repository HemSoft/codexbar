// Copyright (c) HemSoft Developments. All rights reserved.

namespace CodexBar.App.Tests;

using System.Collections.ObjectModel;
using System.Windows.Input;
using CodexBar.App.ViewModels;
using CodexBar.Core.Models;

public sealed class ItemCardReconcilerTests
{
    private readonly Dictionary<string, ProviderCardViewModel> _cardsByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<ProviderCardViewModel> _providers = [];
    private readonly List<ProviderCardViewModel> _overageUpdates = [];
    private readonly ItemCardReconciler _sut;

    public ItemCardReconcilerTests()
    {
        this._sut = new ItemCardReconciler(
            this._cardsByKey,
            this._providers,
            card => this._overageUpdates.Add(card),
            key => new RelayCommand(_ => { }));
    }

    // ---------------------------------------------------------------
    // Reconcile — empty/null items
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_NullItems_CreatesErrorCard()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = false,
            ErrorMessage = "API down",
            Items = null,
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.Single(this._providers);
        var card = this._providers[0];
        Assert.Equal("claude:error", card.CardKey);
        Assert.Equal("API down", card.StatusText);
        Assert.True(card.IsError);
        Assert.False(card.ShowUsagePercent);
    }

    [Fact]
    public void Reconcile_EmptyItems_ShowsDefaultErrorMessage()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items = [],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.Single(this._providers);
        Assert.Equal("No accounts", this._providers[0].StatusText);
    }

    [Fact]
    public void Reconcile_EmptyItems_RemovesStaleAccountCards()
    {
        var existingCard = new ProviderCardViewModel
        {
            ProviderId = ProviderId.Claude,
            CardKey = "claude:acct1",
            DisplayName = "Account 1",
        };
        this._providers.Add(existingCard);
        this._cardsByKey["claude:acct1"] = existingCard;

        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = false,
            ErrorMessage = "Auth failed",
            Items = null,
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.Single(this._providers);
        Assert.Equal("claude:error", this._providers[0].CardKey);
        Assert.False(this._cardsByKey.ContainsKey("claude:acct1"));
    }

    [Fact]
    public void Reconcile_EmptyItemsTwice_UpdatesExistingErrorCard()
    {
        var result1 = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = false,
            ErrorMessage = "Error 1",
            Items = null,
        };
        this._sut.Reconcile(ProviderId.Claude, "claude:", result1);

        var result2 = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = false,
            ErrorMessage = "Error 2",
            Items = null,
        };
        this._sut.Reconcile(ProviderId.Claude, "claude:", result2);

        Assert.Single(this._providers);
        Assert.Equal("Error 2", this._providers[0].StatusText);
    }

    // ---------------------------------------------------------------
    // Reconcile — items arrive after error
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_ItemsArrive_RemovesErrorCard()
    {
        // First: error state
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = false,
            ErrorMessage = "Down",
            Items = null,
        });
        Assert.Contains(this._cardsByKey, kv => kv.Key == "claude:error");

        // Then: items arrive
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Account 1",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.5, UsageLabel = "50%" },
                },
            ],
        };
        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.DoesNotContain(this._cardsByKey, kv => kv.Key == "claude:error");
        Assert.Single(this._providers);
        Assert.Equal("claude:acct1", this._providers[0].CardKey);
    }

    // ---------------------------------------------------------------
    // Reconcile — new item creates card
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_NewItem_CreatesCard()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot · HemSoft",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.3, UsageLabel = "30 / 100" },
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Copilot, "copilot:", result);

        Assert.Single(this._providers);
        var card = this._providers[0];
        Assert.Equal("copilot:HemSoft", card.CardKey);
        Assert.Equal("Copilot · HemSoft", card.DisplayName);
        Assert.Equal("30 / 100", card.StatusText);
        Assert.Equal(0.3, card.UsedPercent);
        Assert.False(card.IsError);
    }

    // ---------------------------------------------------------------
    // Reconcile — existing item updates card
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_ExistingItem_UpdatesCard()
    {
        // Initial reconciliation
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot · HemSoft",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.3, UsageLabel = "30 / 100" },
                },
            ],
        });

        // Update with new data
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot · HemSoft (updated)",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.7, UsageLabel = "70 / 100" },
                },
            ],
        });

        Assert.Single(this._providers);
        var card = this._providers[0];
        Assert.Equal("Copilot · HemSoft (updated)", card.DisplayName);
        Assert.Equal("70 / 100", card.StatusText);
        Assert.Equal(0.7, card.UsedPercent);
    }

    // ---------------------------------------------------------------
    // Reconcile — stale cards removed
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_RemovedAccount_RemovesStaleCard()
    {
        // Initial: two accounts
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem { Key = "copilot:A", DisplayName = "A", Success = true },
                new UsageItem { Key = "copilot:B", DisplayName = "B", Success = true },
            ],
        });
        Assert.Equal(2, this._providers.Count);

        // Update: only one account
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem { Key = "copilot:A", DisplayName = "A", Success = true },
            ],
        });

        Assert.Single(this._providers);
        Assert.Equal("copilot:A", this._providers[0].CardKey);
        Assert.False(this._cardsByKey.ContainsKey("copilot:B"));
    }

    // ---------------------------------------------------------------
    // ApplyItemError
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_FailedItem_AppliesErrorState()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Account 1",
                    Success = false,
                    ErrorMessage = "Rate limited",
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        var card = this._providers[0];
        Assert.Equal("Rate limited", card.StatusText);
        Assert.True(card.IsError);
        Assert.Equal(0, card.UsedPercent);
        Assert.Null(card.OverageCost);
        Assert.Null(card.SessionSpending);
        Assert.False(card.HasBars);
    }

    [Fact]
    public void Reconcile_FailedItemWithNullMessage_ShowsDefaultError()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Account 1",
                    Success = false,
                    ErrorMessage = null,
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.Equal("Error", this._providers[0].StatusText);
    }

    // ---------------------------------------------------------------
    // Multi-bar display
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_ItemWithBars_AppliesMultiBarDisplay()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot · HemSoft",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.5, UsageLabel = "50% used" },
                    Bars =
                    [
                        new UsageBar { Label = "5-hour", UsedPercent = 0.3 },
                        new UsageBar { Label = "Weekly", UsedPercent = 0.6 },
                    ],
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Copilot, "copilot:", result);

        var card = this._providers[0];
        Assert.True(card.HasBars);
        Assert.Equal(2, card.Bars.Count);
        Assert.Equal("5-hour", card.Bars[0].Label);
        Assert.Equal("Weekly", card.Bars[1].Label);
        Assert.False(card.ShowUsagePercent);
        Assert.Equal("50% used", card.StatusText);
        Assert.Null(card.OverageCost);
    }

    [Fact]
    public void Reconcile_ItemWithBarsNoPrimaryUsage_ShowsNoData()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot · HemSoft",
                    Success = true,
                    Bars = [new UsageBar { Label = "5-hour", UsedPercent = 0.3 }],
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Copilot, "copilot:", result);

        var card = this._providers[0];
        Assert.Equal("No data", card.StatusText);
        Assert.Equal(0, card.UsedPercent);
        Assert.False(card.IsHighUsage);
    }

    // ---------------------------------------------------------------
    // Legacy display — PrimaryUsage
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_PrimaryUsage_SetsCorrectFields()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot
                    {
                        UsedPercent = 0.85,
                        UsageLabel = "85 / 100",
                        ResetDescription = "Resets in 2h",
                    },
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        var card = this._providers[0];
        Assert.Equal("85 / 100", card.StatusText);
        Assert.Equal(0.85, card.UsedPercent);
        Assert.True(card.IsHighUsage);
        Assert.Equal("Resets in 2h", card.ResetText);
        Assert.False(card.HasBars);
    }

    [Fact]
    public void Reconcile_PrimaryUsageUnlimited_HidesPercentBar()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot
                    {
                        UsedPercent = 0.5,
                        UsageLabel = "50 / ∞",
                        IsUnlimited = true,
                    },
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.False(this._providers[0].ShowUsagePercent);
    }

    [Fact]
    public void Reconcile_PrimaryUsageNoLabel_ShowsPercentFormat()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.45, UsageLabel = null },
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.Contains("45", this._providers[0].StatusText);
    }

    // ---------------------------------------------------------------
    // Legacy display — CreditsRemaining
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_CreditsRemaining_ShowsCreditsDisplay()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.OpenCodeGo,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "opencode-go:acct1",
                    DisplayName = "OpenCode",
                    Success = true,
                    CreditsRemaining = 12.50m,
                },
            ],
        };

        this._sut.Reconcile(ProviderId.OpenCodeGo, "opencode-go:", result);

        var card = this._providers[0];
        Assert.Equal("$12.50", card.StatusText);
        Assert.Equal(0, card.UsedPercent);
        Assert.True(card.IsCreditsDisplay);
        Assert.False(card.ShowUsagePercent);
    }

    // ---------------------------------------------------------------
    // Legacy display — SecondaryUsage promoted
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_OnlySecondaryUsage_PromotesToPrimary()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    SecondaryUsage = new UsageSnapshot
                    {
                        UsedPercent = 0.6,
                        UsageLabel = "Weekly: 60%",
                        ResetDescription = "Resets Monday",
                    },
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        var card = this._providers[0];
        Assert.Equal("Weekly: 60%", card.StatusText);
        Assert.Equal(0.6, card.UsedPercent);
        Assert.Equal("Resets Monday", card.ResetText);
        Assert.Null(card.WeeklyText);
    }

    // ---------------------------------------------------------------
    // Legacy display — No data
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_NoUsageData_ShowsNoData()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        var card = this._providers[0];
        Assert.Equal("No data", card.StatusText);
        Assert.Equal(0, card.UsedPercent);
        Assert.True(card.ShowUsagePercent);
        Assert.False(card.IsHighUsage);
    }

    // ---------------------------------------------------------------
    // Secondary as weekly
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_BothPrimaryAndSecondary_ShowsWeekly()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.3, UsageLabel = "Daily: 30%" },
                    SecondaryUsage = new UsageSnapshot { UsedPercent = 0.7, UsageLabel = "Weekly: 70%" },
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        var card = this._providers[0];
        Assert.Equal("Weekly: 70%", card.WeeklyText);
        Assert.Equal(0.7, card.WeeklyPercent);
    }

    [Fact]
    public void Reconcile_OnlyPrimaryNoSecondary_ClearsWeekly()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.3, UsageLabel = "Daily: 30%" },
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Claude, "claude:", result);

        Assert.Null(this._providers[0].WeeklyText);
        Assert.Equal(0, this._providers[0].WeeklyPercent);
    }

    // ---------------------------------------------------------------
    // Overage cost
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_OverageCost_CallsDelegate()
    {
        var result = new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot · HemSoft",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.5, UsageLabel = "50%" },
                    OverageCost = 2.50m,
                },
            ],
        };

        this._sut.Reconcile(ProviderId.Copilot, "copilot:", result);

        Assert.Single(this._overageUpdates);
        Assert.Equal(2.50m, this._overageUpdates[0].OverageCost);
    }

    [Fact]
    public void Reconcile_NoOverageCost_ClearsSessionSpending()
    {
        // First set up overage
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.5 },
                    OverageCost = 2.50m,
                },
            ],
        });
        this._providers[0].SessionSpending = "$2.50";

        // Then no overage
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:HemSoft",
                    DisplayName = "Copilot",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.5 },
                },
            ],
        });

        Assert.Null(this._providers[0].OverageCost);
        Assert.Null(this._providers[0].SessionSpending);
    }

    // ---------------------------------------------------------------
    // ReconcileBars
    // ---------------------------------------------------------------
    [Fact]
    public void ReconcileBars_AddsBarsToEmpty()
    {
        var card = new ProviderCardViewModel();
        var bars = new List<UsageBar>
        {
            new() { Label = "A", UsedPercent = 0.1 },
            new() { Label = "B", UsedPercent = 0.9 },
        };

        ItemCardReconciler.ReconcileBars(card, bars);

        Assert.Equal(2, card.Bars.Count);
        Assert.Equal("A", card.Bars[0].Label);
        Assert.False(card.Bars[0].IsHighUsage);
        Assert.Equal("B", card.Bars[1].Label);
        Assert.True(card.Bars[1].IsHighUsage);
    }

    [Fact]
    public void ReconcileBars_UpdatesExisting()
    {
        var card = new ProviderCardViewModel();
        card.Bars.Add(new UsageBarViewModel { Label = "Old", UsedPercent = 0.1 });

        var bars = new List<UsageBar>
        {
            new() { Label = "New", UsedPercent = 0.5, ResetDescription = "2h" },
        };

        ItemCardReconciler.ReconcileBars(card, bars);

        Assert.Single(card.Bars);
        Assert.Equal("New", card.Bars[0].Label);
        Assert.Equal(0.5, card.Bars[0].UsedPercent);
        Assert.Equal("2h", card.Bars[0].ResetDescription);
    }

    [Fact]
    public void ReconcileBars_RemovesExcess()
    {
        var card = new ProviderCardViewModel();
        card.Bars.Add(new UsageBarViewModel { Label = "A", UsedPercent = 0.1 });
        card.Bars.Add(new UsageBarViewModel { Label = "B", UsedPercent = 0.2 });
        card.Bars.Add(new UsageBarViewModel { Label = "C", UsedPercent = 0.3 });

        var bars = new List<UsageBar>
        {
            new() { Label = "X", UsedPercent = 0.5 },
        };

        ItemCardReconciler.ReconcileBars(card, bars);

        Assert.Single(card.Bars);
        Assert.Equal("X", card.Bars[0].Label);
    }

    // ---------------------------------------------------------------
    // Transition tests (rubber-duck recommended)
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_SuccessToFailure_ClearsAllUsageState()
    {
        // Success first
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.9, UsageLabel = "90%" },
                    SecondaryUsage = new UsageSnapshot { UsedPercent = 0.7, UsageLabel = "Weekly: 70%" },
                },
            ],
        });

        // Then failure
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = false,
                    ErrorMessage = "Auth expired",
                },
            ],
        });

        var card = this._providers[0];
        Assert.True(card.IsError);
        Assert.Equal("Auth expired", card.StatusText);
        Assert.Equal(0, card.UsedPercent);
        Assert.Null(card.WeeklyText);
        Assert.False(card.HasBars);
    }

    [Fact]
    public void Reconcile_FailureToSuccess_ClearsErrorState()
    {
        // Failure first
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = false,
                    ErrorMessage = "Down",
                },
            ],
        });

        // Then success
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.3, UsageLabel = "30%" },
                },
            ],
        });

        var card = this._providers[0];
        Assert.False(card.IsError);
        Assert.Equal("30%", card.StatusText);
    }

    [Fact]
    public void Reconcile_MultiBarToLegacy_ClearsBars()
    {
        // Multi-bar first
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:A",
                    DisplayName = "A",
                    Success = true,
                    Bars =
                    [
                        new UsageBar { Label = "5-hour", UsedPercent = 0.5 },
                        new UsageBar { Label = "Weekly", UsedPercent = 0.7 },
                    ],
                },
            ],
        });
        Assert.True(this._providers[0].HasBars);

        // Then legacy
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:A",
                    DisplayName = "A",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.4, UsageLabel = "40%" },
                },
            ],
        });

        var card = this._providers[0];
        Assert.False(card.HasBars);
        Assert.Empty(card.Bars);
        Assert.Equal("40%", card.StatusText);
    }

    [Fact]
    public void Reconcile_LegacyToMultiBar_SwitchesDisplay()
    {
        // Legacy first
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:A",
                    DisplayName = "A",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.3, UsageLabel = "30%" },
                },
            ],
        });

        // Then multi-bar
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:A",
                    DisplayName = "A",
                    Success = true,
                    Bars = [new UsageBar { Label = "5-hour", UsedPercent = 0.6 }],
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.6, UsageLabel = "60%" },
                },
            ],
        });

        var card = this._providers[0];
        Assert.True(card.HasBars);
        Assert.False(card.ShowUsagePercent);
    }

    [Fact]
    public void Reconcile_OverageToNoOverage_ClearsSpending()
    {
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:A",
                    DisplayName = "A",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.5 },
                    OverageCost = 5.00m,
                },
            ],
        });

        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "copilot:A",
                    DisplayName = "A",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = 0.5 },
                },
            ],
        });

        var card = this._providers[0];
        Assert.Null(card.OverageCost);
        Assert.Null(card.SessionSpending);
        Assert.Null(card.SessionResetTime);
    }

    // ---------------------------------------------------------------
    // High usage threshold
    // ---------------------------------------------------------------
    [Theory]
    [InlineData(0.79, false)]
    [InlineData(0.80, true)]
    [InlineData(0.99, true)]
    public void Reconcile_PrimaryUsage_HighUsageThreshold(double percent, bool expectedHigh)
    {
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem
                {
                    Key = "claude:acct1",
                    DisplayName = "Claude",
                    Success = true,
                    PrimaryUsage = new UsageSnapshot { UsedPercent = percent, UsageLabel = "test" },
                },
            ],
        });

        Assert.Equal(expectedHigh, this._providers[0].IsHighUsage);
    }

    // ---------------------------------------------------------------
    // Dictionary/collection consistency
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_Always_KeepsDictionaryAndCollectionInSync()
    {
        // Create
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem { Key = "copilot:A", DisplayName = "A", Success = true },
                new UsageItem { Key = "copilot:B", DisplayName = "B", Success = true },
            ],
        });

        Assert.Equal(this._providers.Count, this._cardsByKey.Count);
        foreach (var card in this._providers)
        {
            Assert.True(this._cardsByKey.ContainsKey(card.CardKey));
        }

        // Remove one
        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem { Key = "copilot:A", DisplayName = "A", Success = true },
            ],
        });

        Assert.Equal(this._providers.Count, this._cardsByKey.Count);
        foreach (var card in this._providers)
        {
            Assert.True(this._cardsByKey.ContainsKey(card.CardKey));
        }
    }

    // ---------------------------------------------------------------
    // ResetCardToError static method
    // ---------------------------------------------------------------
    [Fact]
    public void ResetCardToError_ClearsAllFields()
    {
        var card = new ProviderCardViewModel
        {
            StatusText = "OK",
            UsedPercent = 0.9,
            WeeklyText = "Weekly",
            WeeklyPercent = 0.5,
            ResetText = "Resets soon",
            IsHighUsage = true,
            ShowUsagePercent = true,
            HasBars = true,
        };
        card.Bars.Add(new UsageBarViewModel { Label = "test" });

        ItemCardReconciler.ResetCardToError(card, "Server error");

        Assert.Equal("Server error", card.StatusText);
        Assert.True(card.IsError);
        Assert.False(card.ShowUsagePercent);
        Assert.Equal(0, card.UsedPercent);
        Assert.Null(card.WeeklyText);
        Assert.Equal(0, card.WeeklyPercent);
        Assert.Null(card.ResetText);
        Assert.False(card.IsHighUsage);
        Assert.Empty(card.Bars);
        Assert.False(card.HasBars);
    }

    // ---------------------------------------------------------------
    // Cross-provider isolation
    // ---------------------------------------------------------------
    [Fact]
    public void Reconcile_DifferentProviders_DontInterfere()
    {
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = true,
            Items =
            [
                new UsageItem { Key = "claude:acct1", DisplayName = "Claude 1", Success = true },
            ],
        });

        this._sut.Reconcile(ProviderId.Copilot, "copilot:", new ProviderUsageResult
        {
            Provider = ProviderId.Copilot,
            Success = true,
            Items =
            [
                new UsageItem { Key = "copilot:HemSoft", DisplayName = "Copilot", Success = true },
            ],
        });

        Assert.Equal(2, this._providers.Count);

        // Remove all Claude items
        this._sut.Reconcile(ProviderId.Claude, "claude:", new ProviderUsageResult
        {
            Provider = ProviderId.Claude,
            Success = false,
            Items = null,
        });

        // Copilot card should still be there
        Assert.Equal(2, this._providers.Count); // error card + copilot card
        Assert.True(this._cardsByKey.ContainsKey("copilot:HemSoft"));
    }

    // ---------------------------------------------------------------
    // GetOrCreateItemCard — command wiring
    // ---------------------------------------------------------------
    [Fact]
    public void GetOrCreateItemCard_NewCard_HasResetCommand()
    {
        var item = new UsageItem { Key = "copilot:A", DisplayName = "A", Success = true };
        var card = this._sut.GetOrCreateItemCard(ProviderId.Copilot, item);

        Assert.NotNull(card.ResetSessionSpendingCommand);
    }

    [Fact]
    public void GetOrCreateItemCard_ExistingCard_ReturnsSameInstance()
    {
        var item = new UsageItem { Key = "copilot:A", DisplayName = "A", Success = true };
        var card1 = this._sut.GetOrCreateItemCard(ProviderId.Copilot, item);
        var card2 = this._sut.GetOrCreateItemCard(ProviderId.Copilot, item);

        Assert.Same(card1, card2);
    }
}
