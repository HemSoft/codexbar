# Test Metrics

## Coverage & Quality Snapshot

| Metric                  | Before | After | Delta |
|-------------------------|--------|-------|-------|
| Line coverage %         | 100%   | 100%  | —     |
| Branch coverage %       | 100%   | 100%  | —     |
| Function coverage %     | 100%   | 100%  | —     |
| Total test count        | 1417   | 1425  | +8    |
| Test types present      | Unit   | Unit  | —     |
| Avg assertions per test | 1.87   | 1.89  | +0.02 |

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
