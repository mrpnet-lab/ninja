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
Changed from every-tick-in-trade to every-2nd-tick-in-trade and every-5th-tick-when-flat (was every-3rd):
```csharp
int dashInterval = (inPosition || stopsArmed || pendingExit) ? 2 : 5;
if (IsFirstTickOfBar || dashUpdateTickCounter >= dashInterval)
```

---

## Files Modified
- `Mm_ATM_v2.cs` — All fixes above (3319 lines, braces balanced 531/531, 0 VS Code errors)

## Previous Bug Fixes (commit 461b53b)
- Bug 5: Emergency Kill — `pendingEmergencyKill` flag + `ExecuteEmergencyKill()`
- Bug 6: Signal names — changed from `" "` to `"Entry_" + openDcaCount`
- Bug 7: Dashboard speed — build immediately on first Realtime tick
- Bug 8: Qty cap — `Math.Min(maxContracts, ...)` instead of `Math.Min(10, ...)`
