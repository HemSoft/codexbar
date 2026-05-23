# Test Metrics

## Coverage & Quality Snapshot

| Metric                  | Before | After | Delta |
|-------------------------|--------|-------|-------|
| Line coverage %         | 100%   | 100%  | —     |
| Branch coverage %       | 100%   | 100%  | —     |
| Function coverage %     | 100%   | 100%  | —     |
| Mutation score %        | 84.23% | 84.23%| —     |
| Total test count        | 1607   | 1213  | -394  |
| Duplicate tests removed | —      | 120   | —     |
| Test files consolidated | —      | 6     | —     |
| Test types present      | Unit   | Unit  | —     |
| Avg assertions per test | 1.93   | 1.97  | +0.04 |

## Improvements Made

### Phase 8 — Consolidate Fragmented Test Files & Strengthen Assertions

**Consolidated 6 orphan test files** into canonical locations:

- **MoreClaudeProviderTests.cs** → `ClaudeProviderTests.cs` (3 unique tests kept, 6 duplicates dropped)
- **MoreCopilotProviderTests.cs** → `CopilotProviderTests.cs` (7 unique tests kept, 10 duplicates dropped)
- **BranchCoverageStartupManagerTests.cs** → `StartupManagerTests.cs` (2 tests + NullReturningStore helper)
- **CrapScoreImprovementEnvVarTests.cs** → `OpenRouterProviderTests.cs` (env var clearing merged)
- **CrapScoreImprovementClaudeFileIoTests.cs** → `ClaudeProviderFileIoTests.cs` (2 tests + DelegatingHandlerFunc)

**Strengthened 3 weak assertions**:

- **ClaudeProviderFetchTests.cs** (2 tests): `Assert.NotNull(result)` → `Assert.Equal` with expected values
- **ItemCardReconcilerTests.cs** (1 test): Added `Assert.True(CanExecute)` to complement bare `Assert.NotNull`

Test count: 1230 → 1213 (removed 17 duplicates via consolidation, zero unique tests lost)
Test files: reduced by 6 (from 74 to 68)
Coverage: 100% line, 100% branch, 100% method (unchanged)

### Phase 7 — Deduplicate Test Suite & Fix Tautological Assertions

**Removed 103 duplicate test methods** across 22 files:

- **BranchCoverageTests.cs**: Removed 12 tests duplicated in canonical provider files
- **CrapScoreImprovementTests.cs**: Removed 18 tests duplicated in canonical provider files
- **MutationKillingRound2Tests.cs**: Removed 5 tests duplicated in canonical provider files
- **ClaudeProviderFetchTests.cs**: Removed 6 static method tests (kept in ClaudeProviderTests)
- **ClaudeProviderFullCoverageTests.cs**: Removed 9 static method tests
- **ClaudeProviderMutationTests.cs**: Removed 7 static method tests
- **ClaudeProviderAsyncTests.cs**: Removed 4 static method tests
- **ClaudeProviderEdgeTests.cs**: Removed 1 static method test
- **MoreClaudeProviderTests.cs**: Removed 7 static method tests
- **CopilotProviderMutationTests.cs**: Removed 6 tests (ParseReset, ExtractUsername, FormatDisplayName)
- **CopilotProviderFullCoverageTests.cs**: Removed 4 tests (IsAvailableAsync, ExtractUsernamesFromGhStatus, ComputeUsageMetrics)
- **SettingsServiceMutationTests.cs**: Removed 4 tests (IsProviderEnabled, GetApiKey, Save_ZoomLevel, GetCopilotAccounts)
- **UsageRefreshServiceMutationTests.cs**: Removed 3 tests (StopAsync, RefreshAllAsync, RaisesUsageUpdated)
- **ClaudeProviderAsyncTests.cs**: Removed 7 tests (IsAvailableAsync, BuildUsageBars, BuildWeeklySnapshot)
- **OpenCodeZenProviderMutationTests.cs**: Removed 5 tests (IsAvailableAsync, FetchUsageAsync)
- **MoreCopilotProviderTests.cs**: Removed 2 tests (ParseReset, ComputeUsageMetrics)
- **OpenRouterProviderMutationTests.cs**: Removed 2 tests (IsAvailableAsync, FetchUsageAsync)
- **OpenRouterProviderCoverageTests.cs**: Removed 1 test (FetchUsageAsync)
- **UsageRefreshServiceMoreTests.cs**: Removed 1 test (Dispose)

**Fixed 1 tautological assertion**:

- **StartupManagerCoverageTests.cs**: `Assert.IsType<bool>(result)` (always true for bool)
  → replaced with `Record.Exception` + `Assert.Null(ex)` pattern

Test count: 1333 → 1230 (removed 103 duplicates, zero unique tests lost)
Coverage: 100% line, 100% branch (unchanged)

### Phase 6 — Eliminate Assertion-less Tests & Rename "DoesNotThrow" Methods

**Strengthened 4 assertion-less tests** with meaningful verification:

- **ClaudeProviderFileIoTests** (2 tests):
  - `PersistCredentials_FileDoesNotExist_DoesNothing` → `PersistCredentials_FileDoesNotExist_SilentlySkips`
    with `Record.Exception` + `Assert.Null(ex)`
  - `PersistCredentials_InvalidJson_DoesNotThrow` → `PersistCredentials_InvalidJson_SwallowsParseError`
    with `Record.Exception` + `Assert.Null(ex)`
- **ClaudeProviderFetchTests** (1 test):
  - `IsAvailableAsync_Enabled_DoesNotThrow` → `IsAvailableAsync_Enabled_CompletesSuccessfully`
    replaced tautological `Assert.IsType<bool>` with `Record.ExceptionAsync` + `Assert.Null(ex)`
- **UsageRefreshServiceFullCoverageTests** (2 tests):
  - `NextRefreshChanged_WhenSubscriberThrows_DoesNotCrashService` →
    `NextRefreshChanged_WhenSubscriberThrows_ServiceContinuesRunning`
    with `Assert.True(started.Task.IsCompleted)` + `Assert.NotNull(NextRefreshAtUtc)`
  - `Start_CalledTwice_DoesNotCreateSecondLoop` → added
    `Assert.NotNull(NextRefreshAtUtc)` before Dispose + `Assert.Null` after

**Removed 2 duplicate tests**:

- `ParseCopilotApiResponse_NullLogger_DoesNotThrow` from `BranchCoverageTests.cs`
  (kept in `CopilotProviderFullCoverageTests.cs` as `ParseCopilotApiResponse_NullLogger_ReturnsSuccess`)
- `ParseCopilotApiResponse_WithNullLogger_DoesNotThrow` from `CrapScoreImprovementTests.cs`

**Renamed all 21 remaining "DoesNotThrow" test methods** to describe actual behavior:

Names now follow `[Method]_[Condition]_[ExpectedResult]` convention across 13 test files.
Zero "DoesNotThrow" references remain in the test suite.

### Phase 5 — Test Quality Hardening

**Strengthened 13 weak "DoesNotThrow" tests** with meaningful assertions:

Tests that previously relied on implicit "no exception = pass" now use
explicit `Record.Exception` + `Assert.Null(ex)` and/or state assertions:

- **UsageRefreshServiceMutationTests** (4 tests):
  - `Start_CalledTwice_DoesNotThrow` → `Start_CalledTwice_IsIdempotent`
    with `Assert.NotNull(NextRefreshAtUtc)`
  - `StopAsync_WhenNotStarted_DoesNotThrow` → `StopAsync_WhenNotStarted_LeavesNextRefreshNull`
    with `Assert.Null(NextRefreshAtUtc)` + `Assert.Empty(LatestResults)`
  - `Dispose_CalledTwice_DoesNotThrow` → `Dispose_CalledTwice_IsIdempotent`
    with `Assert.Null(NextRefreshAtUtc)`
  - `RefreshAllAsync_UsageUpdatedHandlerThrows_DoesNotCrash` →
    `RefreshAllAsync_UsageUpdatedHandlerThrows_StillUpdatesLatestResults`
    with `Record.ExceptionAsync` + `Assert.NotEmpty(LatestResults)`
- **UsageRefreshServiceFullCoverageTests** (2 tests):
  - `StopAsync_WhenNotStarted_DoesNotThrow` → `StopAsync_WhenNotStarted_LeavesNextRefreshNull`
  - `Dispose_WhenNotStarted_DoesNotThrow` → `Dispose_WhenNotStarted_LeavesNextRefreshNull`
- **UsageRefreshServiceTests** (1 test):
  - `StartStop_DoesNotThrow` → `Start_ThenStopAsync_ClearsNextRefreshAtUtc`
    with `Assert.Null(NextRefreshAtUtc)` + `Assert.Empty(LatestResults)`
- **CopilotProviderBestEffortKillTests** (3 tests):
  - All three tests now use `Record.Exception` + `Assert.Null(ex)` and
    have names describing the swallowed exception type
- **CopilotProviderFullCoverageTests** (1 test):
  - `BestEffortKillAndDrain_ProcessAlreadyExited_DoesNotThrow` →
    `BestEffortKillAndDrain_ProcessAlreadyExited_SwallowsKillException`
    with `Record.Exception` + `Assert.Null(ex)`
- **FileLoggerTests** (2 tests):
  - `Log_AfterProviderDisposed_DoesNotThrow` →
    `Log_AfterProviderDisposed_SilentlyIgnoresWrite`
    with `Record.Exception` + `Assert.Null(ex)`
  - `Dispose_CalledTwice_DoesNotThrow` → `Dispose_CalledTwice_IsIdempotent`
    with `Record.Exception` + `Assert.Null(ex)`

**Removed 1 duplicate test**:

- `BestEffortKillAndDrain_FaultedTasks_DoesNotThrow` removed from
  `CopilotProviderFullCoverageTests.cs` (kept in
  `CopilotProviderBestEffortKillTests.cs` with strengthened assertions)

### Phase 2c — Mutation Killing Round 2

**Added boundary and behavioral tests** (`MutationKillingRound2Tests`):

35 tests targeting surviving mutants and improving test resilience:

- **CopilotProvider.ParseReset** (8 tests): Boundary tests for all switch expression
  arms (`< 0`, `< 1`, `< 2`, `>= 2`) — kills equality mutations on time comparisons
- **CopilotProvider.BuildFetchResult** (3 tests): Verifies the `&&` condition requiring
  both `Success` AND `PremiumInteractions is not null` — kills logical mutations
- **CopilotProvider.DiscoverAccounts cache** (1 test): Verifies 5-minute empty discovery
  cache prevents repeated calls — kills equality mutation on time comparison
- **CopilotProvider.InvalidateTokenForUserAsync** (1 test): Verifies 401 response
  invalidates token cache and forces re-resolution on next fetch
- **CopilotProvider.LogNonZeroGhTokenExit** (2 tests): Exercises stderr truncation
  at >200 chars and empty stderr placeholder
- **SettingsService.IsProviderEnabled** (3 tests): Tests the three-part OR chain
  (`!TryGetValue || ps is null || ps.Enabled`) — kills logical mutation `||` to `&&`
- **SettingsService merge operations** (2 tests): Verifies MergeProviders preserves
  API keys and MergeSessionBaselines preserves disk baselines during save
- **ClaudeProvider.ResolvePricing** (5 tests): Exact match, prefix match, family
  fallback (opus/haiku/sonnet) — kills equality mutations on `prefix.Length > bestLength`
- **ClaudeProvider.ParseRateLimitHeaders** (3 tests): No headers → null, partial
  headers → defaults, full headers → all values parsed correctly
- **ClaudeProvider.TryGetFreshCachedLimits** (1 test): Verifies cache prevents
  redundant HTTP calls within TTL window
- **UsageRefreshService lifecycle** (4 tests): Verifies UsageUpdated events fire
  for each provider, unavailable providers notify with failure, Start/Stop/Dispose
  properly set and clear NextRefreshAtUtc and fire NextRefreshChanged events
- **StartupManager.SetEnabled** (2 tests): Verifies TestStore conditional (L43)
  — writes "test-exe" when TestStore is set, deletes entry on disable
- **OpenRouter zero credits** (1 test): Boundary test for `totalCredits > 0` ternary

Remaining survivors (117) are predominantly equivalent mutants:

- Statement removal in logging/catch blocks (no observable effect)
- `ProcessStartInfo` object initializer mutations (tested via process override, not per-property)
- Time-dependent boundary mutations using `DateTimeOffset.UtcNow` internally
- Block removal of error-recovery catch blocks that just log and return stale cache

### Phase 2 — Test Quality

**Strengthened weak disposal tests** (`UsageRefreshServiceMoreTests`):

- `Dispose_WhenStarted_StopsLoop` → renamed to `Dispose_WhenStarted_ClearsNextRefreshAtUtc`
  with explicit state assertion (`Assert.Null(service.NextRefreshAtUtc)`)
- `Dispose_WhenNotStarted_DoesNotThrow` → renamed to
  `Dispose_WhenNotStarted_LeavesNextRefreshNull` with state assertion
- `DisposeAsync_WhenStarted_StopsLoop` → renamed to
  `DisposeAsync_WhenStarted_ClearsNextRefreshAtUtc` with state assertion
- `StopAsync_CalledWithoutStart_DoesNotThrow` → renamed to
  `StopAsync_CalledWithoutStart_LeavesNextRefreshNull` with assertions on both
  `NextRefreshAtUtc` and `LatestResults`

**Added concurrency resilience test** (`UsageRefreshServiceMoreTests`):

- `RefreshAllAsync_ConcurrentCalls_DoNotCorruptState` — 10 concurrent calls to
  verify thread-safe dictionary access doesn't corrupt state

**Added result transition test** (`UsageRefreshServiceMoreTests`):

- `RefreshAllAsync_ProviderResultChanges_ReflectsLatestResult` — verifies that
  when a provider changes from success to failure, `LatestResults` reflects the
  latest value (catches bugs where stale results persist)

**Added boundary mutation-killing tests** (`SessionSpendingCalculatorTests`):

- `CalculateCreditsSpending_BalanceDecreasedByOneCent_ShowsOneCent` — verifies
  one-cent difference is detected (catches off-by-one in comparison)
- `CalculateCreditsSpending_BalanceIncreasedByOneCent_ResetsBaseline` — verifies
  one-cent increase triggers baseline reset
- `CalculateOverageSpending_OverageIncreasedByOneCent_ShowsOneCent`
- `CalculateOverageSpending_OverageDecreasedByOneCent_ResetsBaseline`
- `SessionSpendingResult_EqualValues_AreEqual` — record struct equality
- `SessionSpendingResult_DifferentValues_AreNotEqual` — record struct inequality

### Phase 2b — Mutation Score Improvement

**Added boundary mutation-killing tests** (`BoundaryMutationKillingTests`):

34 tests targeting surviving mutants across 5 source files:

- **ClaudeProvider FormatBarReset/FormatResetCountdown** (8 tests): Exact boundary
  tests at 0s, 1h, and 1d remaining to kill `<=`/`>=` mutations
- **CopilotProvider Metadata booleans** (3 tests): Assert exact boolean values for
  `SupportsSessionUsage`, `SupportsWeeklyUsage`, `SupportsCredits`
- **CopilotProvider ExtractUsername** (5 tests): Space-position boundary tests for
  both "account" and "as" patterns
- **CopilotProvider BuildFetchResult** (3 tests via FetchUsageAsync): Mixed
  success/failure accounts to kill `All`→`Any` and `&&`→`||` mutations
- **CopilotProvider/ClaudeProvider IsAvailableAsync** (4 tests): Explicit true/false
  assertions to kill boolean literal mutations
- **SettingsService** (5 tests): IsProviderEnabled with null entries, merge from
  disk with null dictionaries, provider entry preservation
- **UsageRefreshService** (4 tests): Dispose/Stop idempotency, NextRefreshChanged
  event firing, async disposal
- **SettingsService defaults** (3 tests): Assert exact Enabled values for
  OpenRouter (true), Claude (false), OpenCodeGo (true)

**Mutation score**: 83.15% → 84.10% (+7 mutants killed, 118→remaining survivors)

Remaining survivors are predominantly:

- Equivalent mutants in logging/catch blocks (statement removal has no observable effect)
- Time-dependent boundary mutations (methods use `DateTimeOffset.UtcNow` internally)
- Object initializer mutations on ProcessStartInfo (tested via integration, not property-by-property)

### Phase 3 — Test Architecture Assessment

The codebase has unit tests only, which is appropriate:

- **Integration tests**: Not needed — external HTTP calls are tested via
  `MockHttpMessageHandler` which simulates the full response lifecycle.
- **E2E tests**: Not practical — WPF UI requires a desktop environment.
- **Contract tests**: Not applicable — no inter-service API boundaries.
- **Property-based tests**: Considered for formatting methods but the existing
  `[Theory]`/`[InlineData]` tests cover the domain boundaries well.
- **Performance tests**: Not warranted — no hot paths or large data processing.

The existing test suite is comprehensive with dedicated mutation-killing test
files for each major class, thorough edge-case coverage, and well-structured
AAA patterns throughout.

### Phase 4 — ViewModel Quality & Boundary Hardening

**Added PropertyChanged cascading tests** (`ProviderCardViewModelPropertyChangedTests`):

37 tests covering WPF data-binding contract correctness:

- **HasBars setter cascading** (3 tests): Verifies `ShowProgressBar`,
  `ShowSingleCreditsDisplay`, and `ShowStatusTextLine` fire PropertyChanged
  when `HasBars` changes — critical for WPF UI reactivity
- **IsCreditsDisplay setter cascading** (3 tests): Same pattern for
  credits display state changes
- **IsPairedCredits setter cascading** (3 tests): Same pattern for
  paired credits state changes
- **SetField deduplication** (18 tests): Verifies that setting a property
  to its current value does NOT fire PropertyChanged — catches duplicate
  notification bugs across all settable properties
- **Computed property correctness** (10 tests): Verifies `ShowProgressBar`,
  `ShowSingleCreditsDisplay`, and `ShowStatusTextLine` return correct values
  for all state combinations

**Added high-usage threshold boundary tests** (`ItemCardReconcilerTests`):

7 tests at the 0.8 boundary that kill `>=` to `>` mutations:

- `Reconcile_PrimaryUsageExactly80Percent_SetsHighUsage` — exactly 0.8 = high
- `Reconcile_PrimaryUsageJustBelow80Percent_DoesNotSetHighUsage` — 0.79 ≠ high
- `ReconcileBars_MultiBarPrimaryUsageExactly80Percent_SetsHighUsage`
- `ReconcileBars_MultiBarPrimaryUsageJustBelow80Percent_DoesNotSetHighUsage`
- `Reconcile_PromotedSecondaryUsageExactly80Percent_SetsHighUsage`
- `Reconcile_PromotedSecondaryUsageJustBelow80Percent_DoesNotSetHighUsage`
- `ReconcileBars_SessionExactly80Percent_SetsHighUsage`

**Added legacy provider boundary tests** (`ApplyLegacyProviderResultTests`):

3 tests targeting legacy path mutations:

- `ApplyLegacyProviderResult_PrimaryUsageExactly80Percent_SetsHighUsage`
- `ApplyLegacyProviderResult_PrimaryUsageJustBelow80Percent_DoesNotSetHighUsage`
- `ApplyLegacyProviderResult_ErrorResult_ClearsCreditsBalance` — regression test
  verifying error state properly clears stale balance data
