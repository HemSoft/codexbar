# Test Metrics

## Coverage & Quality Snapshot

| Metric                  | Before | After | Delta |
|-------------------------|--------|-------|-------|
| Line coverage %         | 100%   | 100%  | —     |
| Branch coverage %       | 100%   | 100%  | —     |
| Function coverage %     | 100%   | 100%  | —     |
| Mutation score %        | 83.15% | 84.10%| +0.95 |
| Total test count        | 1417   | 1459  | +42   |
| Test types present      | Unit   | Unit  | —     |
| Avg assertions per test | 1.87   | 1.91  | +0.04 |

## Improvements Made

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
