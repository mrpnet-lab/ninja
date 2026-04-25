# Mm_ATM_v2 Bug Fix Log

## Session: April 3, 2026

### Reported Symptoms
1. **Dashboard takes ~10 seconds to open** (v1 took ~3 seconds)
2. **Trade closes 2 seconds after opening** and dashboard deactivates (strategy gets disabled by NinjaTrader)

---

## Bug 9 — Thread-Unsafe WPF Access Crashes Strategy (CRITICAL)

### Root Cause
`UpdateDashboard()` accessed NinjaTrader bar data (`Close[0]`, `Position.MarketPosition`, `Position.Quantity`, `Position.AveragePrice`, `ToTime(Time[0])`) **inside `Dispatcher.InvokeAsync()`**, which runs on the WPF/UI thread.

NinjaTrader's bar data and Position object are **NOT thread-safe** — accessing them from the WPF thread causes an unhandled exception. NinjaTrader's exception handler then:
1. Disables the strategy
2. Flattens all open positions
3. Removes the dashboard

This is why trades closed ~2 seconds after opening — that's when the first `UpdateDashboard()` call with an active position hit the cross-thread violation.

### v1 Comparison
v1 used `Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])` wrapped in try/catch inside the InvokeAsync lambda. While v1 also accessed `Close[0]` on the WPF thread for trail/trap display, the try/catch around the P&L call prevented the crash. v1 also accessed `Position.MarketPosition` on the WPF thread but got lucky — the issue was intermittent in v1.

### Fix Applied
**Snapshot pattern**: All NinjaTrader data is captured into local variables on the data thread BEFORE the `InvokeAsync` call. The lambda then uses only these snapshot values:

```csharp
// Captured on data thread (thread-safe)
double snapClose = Close[0];
MarketPosition snapMktPos = Position.MarketPosition;
int snapPosQty = Position.Quantity;
int snapTimeVal = ToTime(Time[0]);
// ... 25+ more snapshot variables

ChartControl.Dispatcher.InvokeAsync(() =>
{
    try {
        // Uses ONLY snap* variables — no NinjaTrader API calls
    } catch { }
});
```

Also added `try/catch` around the entire InvokeAsync body as a safety net.

### Secondary Fix — RemoveDrawObject from WPF Thread
The trail toggle button handler called `RemoveDrawObject("adaptiveTrail")` directly from the WPF click handler (unsafe). Changed to set `pendingRemoveTrailDraw = true` flag, which is processed on the data thread in `OnBarUpdate()`.

### Secondary Fix — Strategy Selector Freeze
The `inTrade` variable in the button handler used `Position.MarketPosition` on the WPF thread. Changed to use the data-thread-safe `openTradeDirection != 0` instead.

---

## Bug 10 — Dashboard Build Too Slow (~10s vs ~3s)

### Root Cause
Two factors:
1. **v2 dashboard has ~25-30% more WPF controls** than v1 (strategy selector arrows, active strategy label, qty/max label, wider buttons, taller ScrollViewer)
2. **Build retry interval was 2 ticks** (changed from v1's 5 ticks), causing more frequent expensive rebuild attempts that compound during the WPF rendering pipeline

### Fix Applied
Changed retry interval back to 5 ticks:
```csharp
if (dashBuildTickCounter >= 5)  // was 2
```

---

## Bug 11 — P&L Limit Check Double-Subtracted Baseline

### Root Cause
In `OnExecutionUpdate`, the daily loss/profit check used:
```csharp
double checkPnL = dailyRealizedPnL - pnlBaselineOffset;
```
But v2's incremental P&L tracking (`processedTradeCount`) already only counts trades after the Realtime transition. Subtracting `pnlBaselineOffset` again double-penalizes, potentially triggering the daily loss limit prematurely and flattening positions.

### Fix Applied
```csharp
double checkPnL = dailyRealizedPnL;  // Already incremental — no baseline needed
```

---

## Session 2: April 3, 2026 — Critical Performance & Button Fixes

### Reported Symptoms (Round 2)
1. **Trades take 8-10 seconds to execute** — "queuing" messages, extremely slow for NQ
2. **SL/TP adjustment feels broken** — changes don't seem to apply
3. **Jump SL very slow** to execute
4. **Emergency Kill button doesn't work like v1**

---

## Bug 12 — SyncPositionState Runs BEFORE Button Processing (CRITICAL)

### Root Cause
`SyncPositionState()` was called at the TOP of OnBarUpdate, BEFORE the button entry processing block. When a user clicks BUY:
1. `pendingLong = true` (set on WPF thread)
2. `ExecuteLongEntry()` runs → sets `openTradeDirection = 1`, submits market order
3. Next tick: Order is submitted but NOT YET FILLED → `Position` is still Flat
4. `SyncPositionState()` sees `openTradeDirection == 1` but `Position == Flat` → starts grace counter
5. After 30 ticks, `ResetPositionState()` WIPES everything — entry direction, stops, signals
6. This race condition between order submission and fill caused cascading state resets

### Fix Applied
Moved `SyncPositionState()` to AFTER all button processing:
```
Critical path → Block pending exits → Limit timeout → Button entries → SyncPositionState → Monitors
```

---

## Bug 13 — Emergency Kill Button Uses Wrong Flag (CRITICAL)

### Root Cause
v1: `btnExit` sets `pendingFlatten = true` → `ExecuteFlatten()` → just closes position.
v2: `btnExit` was set to `pendingEmergencyKill = true` → `ExecuteEmergencyKill()` → sets `emergencyKillActive = true` AND `dailyLimitHit = true` → **permanently disables ALL trading for the session**.

### Fix Applied
Changed button handler back to match v1: `pendingFlatten = true`

---

## Bug 14 — Volume Profile O(n log n) Sort Every Bar

### Root Cause
`CalculateVolumeProfileLevels()` ran every bar, sorting all price levels (grows all session) + linear search. By midday = 10-50ms per call, blocking OnBarUpdate.

### Fix Applied
1. `Dictionary` → `SortedDictionary` (always sorted, O(log n) insert)
2. `IndexOf` → `BinarySearch` (O(log n) vs O(n))
3. Only recalculate POC/VAH/VAL every 10 bars

---

## Performance Optimizations Applied (Rounds 1+2)

### Dashboard Throttle
- In-trade: every 3rd tick (was every tick, then every 2nd)
- Flat: every 8th tick (was every 3rd, then every 5th)

### Volume Profile
- SortedDictionary eliminates sort
- BinarySearch eliminates linear scan
- Recalc every 10 bars, not every bar

### Signal Calculation
- Only runs on first tick of bar when flat (was every tick)
- Removed redundant UpdateVwap/UpdateVolumeProfile calls inside CalculateSignals

### Chart Drawing
- All draws gated to IsFirstTickOfBar only

---

## Previous Optimizations (Round 1)

### 1. Signal Calculation Gated to First Tick Only
`CalculateSignals()` (with its 4 strategy scorers + 10 smart filters + volume profile loops) was running every tick when in a position. Since signals are only needed for entry decisions when flat:
```csharp
// Before: ran every tick (except non-first when flat and !stopsArmed)
// After: only runs on first tick of bar when flat
if (isFlat && IsFirstTickOfBar)
    CalculateSignals();
```

### 2. Removed Redundant UpdateVwap/UpdateVolumeProfile in CalculateSignals
`CalculateSignals()` called `UpdateVwap()` and `UpdateVolumeProfile()` internally, but both are already called earlier in `OnBarUpdate()`. The `UpdateVolumeProfile()` call inside `CalculateSignals()` defeated the `IsFirstTickOfBar` performance gate. Removed both redundant calls.

### 3. Chart Drawing Gated to First Tick Only
`DrawChartAnnotations()`, `DrawDcaLevels()`, `DrawKeyLevels()`, `DrawSweepSignals()`, `DrawVolumeProfileLines()` were running every tick when `stopsArmed` (i.e., during every trade). Changed to `IsFirstTickOfBar` only — chart annotations don't need tick-level updates.

### 4. Dashboard Update Throttled
Changed from tick-counting to time-based throttle: max ~3 updates/sec (333ms interval).

---

## Round 3: Performance Crisis Fixes (Session: Current)

### Reported Symptoms
- SL, Jump SL, TP lines take ~30 seconds to move on chart
- Order entry takes 1-3 seconds
- Closing a position: "exit pending" for ~1 minute, then strategy disables
- Chart scrolling very slow (several seconds lag)

---

## Bug 15 — WPF Dispatcher Queue Flood (CRITICAL)

### Root Cause
Dashboard `UpdateDashboard()` fired every 3 ticks in-trade (8 flat). On NQ at 200+ ticks/sec, that's **66+ `InvokeAsync` dispatches per second**, each with a massive lambda updating 20+ WPF labels with string formatting. This **starved the WPF thread**, preventing:
- Chart line rendering (SL/TP lines appeared to take 30 seconds)
- Chart scrolling (WPF can't repaint)
- Button feedback (click registered but visual confirmation delayed)

Additionally, `Account.Get()` calls were inside `InvokeAsync` on the WPF thread — slow and potentially blocking.

### Fix Applied
1. Changed dashboard throttle from tick-count to **time-based**: max 3 updates/sec (333ms interval) using `DateTime.Now` comparison
2. Moved `Account.Get(RealizedProfitLoss)`, `Account.Get(UnrealizedProfitLoss)`, `Account.Get(CashValue)` to data thread snapshot (before `InvokeAsync`)
3. Removed per-blocked-button `UpdateDashboardStatus` calls in the pendingExit block (each fired its own `InvokeAsync`)

## Bug 16 — Unbounded Draw Object Accumulation

### Root Cause
`DrawVwapLine()` created `"vwap_" + CurrentBar` — a new draw object every bar, never removed. Over a trading session, thousands of VWAP line segments accumulated. `DrawSweepSignals()` created `"sweepUp_" + CurrentBar` and `"sweepDn_" + CurrentBar` — same accumulation. These overwhelmed chart rendering.

### Fix Applied
- VWAP: remove segments older than 200 bars (`RemoveDrawObject("vwap_" + (CurrentBar - 200))`)
- Sweep signals: remove objects older than 50 bars

## Bug 17 — Per-Tick List Allocation via .ToList()

### Root Cause
`MonitorHiddenStops()` and `MonitorAdaptiveTrail()` both called `activeEntrySignals.ToList()` every tick — allocating a new `List<string>` ~200+ times/sec. This created GC pressure and unnecessary heap allocations.

### Fix Applied
Replaced all `.ToList()` + `foreach` with direct indexed `for` loops on the original list. Safe because list modifications only occur on the same NinjaTrader strategy thread — no concurrent access.

## Bug 18 — CalculateTrailDistance() Heavyweight Loops Every Tick

### Root Cause
`MonitorAdaptiveTrail()` called `CalculateTrailDistance()` every tick, which ran 10-iteration ATR averaging + 8-iteration direction scoring loops. At 200+ ticks/sec, that's 3600+ loop iterations/sec for values that only change once per bar.

### Fix Applied
Added `GetCachedTrailDistance()` — caches the result per bar using `cachedTrailBar` / `cachedTrailDistance`. Recalculates only on first call per new bar.

## Bug 19 — OnPositionUpdate Duplicate ResetPositionState

### Root Cause
`OnPositionUpdate` directly called `ResetPositionState()` when flat, which dispatched `UpdateAdjustLabels` via `InvokeAsync`. Then `SyncPositionState()` in the next `OnBarUpdate` also called `ResetPositionState()` → duplicate WPF dispatches and draw removals.

### Fix Applied
`OnPositionUpdate` now sets `pendingPositionFlat = true` flag. `SyncPositionState()` checks this flag first, ensuring a single Reset path and reducing WPF dispatch overhead.

## Bug 20 — Stale Exit Safety Floods Broker

### Root Cause
With `STALE_EXIT_TICKS = 50` and NQ at 200+ ticks/sec, the safety net called `Account.Flatten` every 0.25 seconds if the exit didn't clear. This flooded the broker with flatten requests, possibly triggering risk management.

### Fix Applied
1. Increased `STALE_EXIT_TICKS` from 50 to 250 (~1.25 sec on NQ) — gives exit orders more time to fill
2. After each flatten attempt, sets `pendingExitTicks = STALE_EXIT_TICKS / 2` (backoff) — waits ~0.6 sec before retry instead of immediately re-triggering

---

## Performance Summary (Round 3)

| Metric | Before | After |
|--------|--------|-------|
| Dashboard dispatches/sec | ~66 (in-trade) | ~3 (time-based) |
| Account.Get() thread | WPF (wrong) | Data (correct) |
| .ToList() allocations/sec | ~400+ | 0 |
| Trail distance calcs/sec | ~200+ | ~1/bar |
| VWAP draw objects | Unbounded | Capped 200 |
| Sweep draw objects | Unbounded | Capped 50 |
| Stale exit flatten interval | 0.25 sec | 1.25 sec + backoff |

## Files Modified
- `Mm_ATM_v2.cs` — All fixes above (braces balanced 540/540, 0 VS Code errors)

## Previous Bug Fixes (commit 461b53b)
- Bug 5: Emergency Kill — `pendingEmergencyKill` flag + `ExecuteEmergencyKill()`
- Bug 6: Signal names — changed from `" "` to `"Entry_" + openDcaCount`
- Bug 7: Dashboard speed — build immediately on first Realtime tick
- Bug 8: Qty cap — `Math.Min(maxContracts, ...)` instead of `Math.Min(10, ...)`
