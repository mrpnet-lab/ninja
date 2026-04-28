# Mm_ATM_v6 â€” Design Specification & Implementation Summary

**Version:** 6.0  
**Date:** April 28, 2026  
**Base reference:** `Mm_ATM_v5.cs` v5 14.20 (frozen â€” kept intact for live trading and A/B comparison)  
**Target file:** `Mm_ATM_v6.cs`  
**Platform:** NinjaTrader 8.1+ â€” NQ Futures (MNQ compatible)

> **Note on lineage:** Historical sections below (the v4 â†’ v5 rewrite, the original 12-filter design, etc.) are preserved as-is for context. They describe how the codebase reached its v5 14.20 baseline, which is the seed `Mm_ATM_v6.cs` was forked from. New v6 work is documented in Â§18 (v6 roadmap) and Â§19 (active workplan), and all *new* version tags use `v6 0.x` numbering.

---

## Table of Contents

1. [Background & Goals](#1-background--goals)
2. [Plan: Approved Directives](#2-plan-approved-directives)
   - A. Critical Bug Fixes (A1â€“A8)
   - B. Filter Consolidation (20 â†’ 12)
   - C. Strategy Reduction
   - D. Exit Hierarchy Unification
   - E. Smart Trail + MM-Avoidance Backtrack
   - F. Parameter Consolidation
   - G. New Smart Logic (G1â€“G5)
   - H. Dashboard Redesign
   - I. Manual Trade-Signal Indicator
3. [Implementation Summary](#3-implementation-summary)
4. [Architecture Overview](#4-architecture-overview)
5. [12 Active Filters (F1â€“F12)](#5-12-active-filters-f1f12)
6. [Exit Hierarchy (Unified)](#6-exit-hierarchy-unified)
7. [Smart Trail Backtrack â€” MM Avoidance](#7-smart-trail-backtrack--mm-avoidance)
8. [Order-Flow Tape Filter (G1)](#8-order-flow-tape-filter-g1)
9. [Manual Trade-Signal Indicator](#9-manual-trade-signal-indicator)
10. [Dashboard Redesign](#10-dashboard-redesign)
11. [Properties Reference](#11-properties-reference)
12. [Backtest Validation Plan](#12-backtest-validation-plan)
13. [Known Differences vs v4](#13-known-differences-vs-v4)
14. [Post-Release Updates](#14-post-release-updates)
    - 14.1 Dashboard Resize & Width Fix
    - 14.2 Trail Diagnostics
    - 14.3 Manual Trail Control (TRL NOW / Trail Â±pt)
    - 14.4 Break-Even Master Toggle
    - 14.5 Allow Multiple Entries Per Bar
    - 14.6 Trap Detector Explained

---

## 1. Background & Goals

`Mm_ATM_v4` accumulated ~4,700 lines across iterative patches. Key problems identified before the v5 rewrite:

| Problem | Impact |
|---|---|
| 20 overlapping/canceling filters | Signal noise, false positives |
| 5 competing exit subsystems with race conditions | Phantom exits, stale `pendingExit` |
| Entry bookkeeping on submit (not fill) | Wrong avg price, bad SL/TP arm |
| `totalContracts` pre-incremented before fill | Position de-sync |
| Strategies 2 (Liquidity Sweep) and 3 (ORB) losing money | $-122/trade and $-91/trade |
| No MM stop-hunt avoidance in trail | Trail ratcheted INTO the spike |
| No explicit BUY/SELL signal for manual trading | Relying on raw Bull/Bear % alone |
| Dashboard clutter (20+ buttons, redundant subsystems) | Hard to use under pressure |

**Primary objectives for v5:**
- Be profitable against MM bots and algos, not just trend-following
- One clean, prioritized exit path â€” no races
- Invisible SL / Trail / Jump SL as the core edge
- Clear, actionable manual trading signal
- Lighter codebase (~1,800 lines target from 4,700)

---

## 2. Plan: Approved Directives

### A â€” Critical Bug Fixes (A1â€“A8)

| ID | Bug | Fix Applied |
|---|---|---|
| A1 | Class name still `Mm_ATM_v4`, Name property = "Mm_ATM_v4" | Renamed to `Mm_ATM_v6` everywhere, all log tags updated |
| A2 | BUY ASK / SELL BID pre-incremented `totalContracts` on submit | Deferred all bookkeeping to `OnOrderUpdate.Filled` |
| A3 | Limit orders counted before fill, causing de-sync | Same fix as A2 â€” `OnOrderUpdate.Filled` is single source of truth |
| A4 | `ArmHiddenStops()` called on submit using `Close[0]` as avgPrice | `ArmHiddenStops()` only called from `OnOrderUpdate.Filled` after real fill price is known |
| A5 | DCA re-arm used `Close[0]` instead of `Position.AveragePrice` | VWAP-weighted avg computed from actual fills; Realtime recovery uses `Position.AveragePrice` |
| A6 | No per-bar entry guard â€” multiple entries possible on same bar | `enteredThisBar` flag, reset on `IsFirstTickOfBar` |
| A7 | Trail evaluated against stale `Close[0]` when bid/ask = 0 in Realtime | Trail uses `GetCurrentBid/Ask`; falls back to `Close[0]` only if bid/ask > 0 check fails |
| A8 | Single `pendingExit` flag confused partial exits with full exits | Separate `pendingExit` (full) tracked with `pendingExitTicks` watchdog; partial exits handled independently |

---

### B â€” Filter Consolidation (20 â†’ 12)

**Dropped filters (8):**
- `#3` Momentum exhaustion (overlapped with F7 RSI div)
- `#4` Wick ratio (noise, no edge)
- `#7` VWAP-distance scaling (replaced by unified F4 slope)
- `#8` Swing structure (caused ORB dependency)
- `#9` Key Level midrange (dropped with ORB strategy)
- `#14` Lunch-hour time block (too blunt â€” hours filter handles this)
- `#16` Consecutive loss block (replaced by bar-based cooldown in `TryAutoEntry`)
- `#17/#18` PrevDay bounce patterns (marginal, over-fitted)

**Kept and reorganized as F1â€“F12 (see Section 5).**

---

### C â€” Strategy Reduction

| Strategy | v4 | v5 | Reason |
|---|---|---|---|
| 0 â€” Momentum + VWAP | âœ… Keep | âœ… Keep | Core profitable edge |
| 1 â€” Key Level Breakout | âœ… Keep | âœ… Keep | Profitable |
| 2 â€” Liquidity Sweep Reversal | âœ… Was S2 | âŒ Dropped | $-122/trade |
| 3 â€” Opening Range Breakout | âœ… Was S3 | âŒ Dropped | $-91/trade; replaced by Open-Type classification (G3) |
| Auto-Select | Was S4 | Is S2 | Picks best of M+V vs KLB each bar |

All ORB state variables (`orbHigh`, `orbLow`, `orbSet`, `UpdateOrbLevels()`) removed entirely.

---

### D â€” Exit Hierarchy Unification

The v4 strategy had 5 exit subsystems that could fire simultaneously:
1. HiddenSL + HiddenTP
2. Breakeven
3. Adaptive Trail
4. Smart SL (BE / Tighten / Loosen / TrapEscape)
5. EMA-against exit

**v5 single ordered hierarchy (checked each tick, first match wins):**

```
1. Hard Hidden SL (always)
2. Breakeven Lock (once, at breakevenAtPoints profit)
3. Trap Escape (trapScore â‰¥ 65 && adverse > 10pt  â†’  immediate;
                trapScore â‰¥ 50 && adverse > 5pt  â†’  2-bar graduated)
4. Adaptive Trail (after activation at trailActivationPoints)
5. Hidden TP (only if !runnerModeActive)
```

Removed: EMA-against exit, Smart SL Loosen, redundant duplicate BE logic.

---

### E â€” Smart Trail + MM-Avoidance Backtrack

**Trail simplification:**
- Single `GetTrailDistance()` (cached per bar) based on ATR + EMA separation + trend direction score
- 3 profit tiers: T1-BE, T2-Strong, T3-Runner
- Time-based ratchet: every 5 bars in profit, trail tightens 10%
- HTF-agrees path gives wider Runner distance (Ã—1.8)

**NEW: MM Stop-Hunt Avoidance Backtrack**
- `MonitorTrapDetector()` detects stop-hunt spikes: price penetrates 5-bar structure extreme but closes back inside
- Sets `stopHuntSuspendBars = 4` (4-bar window)
- While window is open, trail is allowed to **relax** (move away from price) by up to `SmartTrailBacktrackTicks` (default **4 ticks**, range 0â€“12, configurable)
- Relaxed price floored at `originalSlPrice` (never worse than original SL)
- One backtrack per bar (`backtrackUsedBar` guard)
- After 4 bars, normal ratchet resumes

This intentionally sacrifices a few ticks to survive the MM sweep zone and let price continue.

---

### F â€” Parameter Consolidation

v4 had ~30 properties across 12 groups. v5 has ~26 properties in 8 clean groups:

**Removed parameters:**
- `enableSessionOverride` (session reset auto-managed)
- `lossCooldownSeconds` (replaced by bar-based `postLossCD`)
- `dcaSuggestionPoints` (DCA fully managed in `CanEnterTrade`)
- `autoSelectStabilityBars` (auto-select simpler)
- `useVolumeProfileFilters` (VP always computed, used implicitly)
- `showRsi / showAtr / showSweepSignals / showKeyLevels` (consolidated to `showEma` + `showVwap`)
- `diagAppend / diagOneDayOnly` (always single daily file)
- Duplicate trail parameters

---

### G â€” New Smart Logic

| ID | Feature | Implementation |
|---|---|---|
| G1 | Order-flow tape filter | `OnMarketData` 30-sec rolling aggressor delta (ask-side vs bid-side volume). Default ON. Live-only â€” no backtest impact. Exposed as `OrderFlowFilterEnabled`. |
| G2 | Liquidity void detection | 3 consecutive bars with range > 2Ã—ATR â†’ `voidBarsRemaining = 2` cooldown + confidence softened 30% |
| G3 | Open-type classification | First 30 min RTH: if price moved > 1.5Ã—ATR from open = Open-Drive. Bull or bear classified. F12 applies +8 confidence boost to aligned direction. |
| G4 | Time-based SL ratchet | Every 5 bars while in-trade and profit > 1.5Ã— trail activation: trail tightened 10% |
| G5 | Adverse-tick streak detector | Folded into `MonitorTrapDetector`: 2-bar graduated trap escape at score â‰¥ 50 with adverse > 5pt |

---

### H â€” Dashboard Redesign

**Removed:**
- Dual mode button rows (collapsed to MANUAL / AUTO toggle pair)
- Subsystem enable/disable popup (TRL and TRP toggles remain inline)
- Duplicate PnL labels

**Added:**
- Big Trade-Signal indicator at top (Section I)
- R-multiple on TP label
- Daily trades counter `[done/max]`
- Account P&L breakdown: Realized / Unrealized / Balance
- Tape Î” readout with arrow: `â–² buyers` / `â–¼ sellers` / `â— balanced`
- VWAP value inline
- Hours indicator: open/closed + time range
- Active strategy score breakdown when Auto-Select mode active

---

### I â€” Manual Trade-Signal Indicator

**Rationale:** Raw Bull/Bear % alone does not communicate well under trading pressure. A composite, explicit action label is more reliable for live manual entry decisions.

**Composite inputs:**
1. `lastBullConfidence` or `lastBearConfidence` â‰¥ `minSignalConfidence`
2. HTF bias not opposed to direction
3. EMA fast/slow cross direction agrees
4. Order-flow tape not opposed (if enabled)
5. No liquidity void active
6. Bonus: fresh EMA cross (â‰¤ 3 bars ago)
7. Bonus: confidence flip (prev dominant direction just reversed)

**Output levels:**

| Level | Label | Condition |
|---|---|---|
| +2 | `â–² STRONG BUY` | All conditions + conf â‰¥ minConf+15 + confluence â‰¥ 4 |
| +1 | `â†‘ BUY` | Core conditions met |
| 0 | `â— WAIT` | Missing â‰¥1 core condition |
| -1 | `â†“ SELL` | Core conditions met bear side |
| -2 | `â–¼ STRONG SELL` | All conditions + conf â‰¥ minConf+15 + confluence â‰¥ 4 |

**Display:** Shown as large (22pt bold) text at the top of the dashboard with a reason line below listing blocking factors (`low conf / htf-against / ema-against / tape-against / liq-void`).

Bull/Bear % bars remain visible with opacity scaled to threshold (dim when below min confidence, full when above) so you can still assess relative strength.

---

## 3. Implementation Summary

### What was implemented

| Section | Status | Notes |
|---|---|---|
| Class rename to `Mm_ATM_v6` | âœ… | All `[Mm-ATM v6]` tags, Name = "Mm_ATM_v6" |
| Bug fixes A1â€“A8 | âœ… | All 8 fixed |
| 12 active filters | âœ… | F1â€“F12, single ApplySmartFilters() pass |
| Strategies 0 + 1 + Auto | âœ… | S2 (LiqSweep) and S3 (ORB) removed entirely |
| Unified exit hierarchy | âœ… | 5 subsystems â†’ 1 ordered priority list |
| Breakeven lock | âœ… | Single, at `breakevenAtPoints` |
| Adaptive trail (simplified) | âœ… | 3 profit tiers + HTF runner path |
| MM-avoidance trail backtrack | âœ… | `SmartTrailBacktrackTicks` param, stop-hunt 4-bar window |
| Time-based ratchet (G4) | âœ… | Every 5 bars in profit, -10% trail distance |
| Order-flow tape filter (G1) | âœ… | `OnMarketData`, 30-sec rolling delta, `OrderFlowFilterEnabled` |
| Liquidity void (G2) | âœ… | 3-bar 2Ã—ATR detector, 2-bar cooldown |
| Open-type classifier (G3) | âœ… | RTH first 30 min classification â†’ F12 |
| Trap detector (G5) | âœ… | Immediate + graduated escape + stop-hunt spike |
| Manual trade-signal indicator | âœ… | 5-level composite with reason text |
| Dashboard redesign | âœ… | Signal at top, R-mult, tape delta, account bal, score breakdown |
| Parameter consolidation | âœ… | 8 groups, ~26 properties |
| Diagnostic CSV log | âœ… | Daily file, optional via `EnableDiagLog` |
| VWAP + Volume Profile | âœ… | Intraday VWAP, POC/VAH/VAL |
| PrevDay levels | âœ… | H/L clamping on TP arm |
| Chart annotations | âœ… | SL/TP/Entry/Trail lines + labels with $ values |
| Session-level annotations | âœ… | VWAP, PD High/Low, POC, VAH, VAL |

### What was NOT implemented (intentional)

| Item | Decision |
|---|---|
| DCA / add-to-winner logic | Preserved via `maxContracts` and existing `CanEnterTrade` gate, but not auto-triggered in v5 (manual only via dashboard) |
| Automated backtest run | User-driven via Strategy Analyzer (see Section 12) |
| ORB | Replaced by Open-Type Classification (G3); full ORB removed |
| Adverse-tick timer (7-tick / 10-sec) | Folded into 2-bar trap escape â€” simpler, same net effect |

---

## 4. Architecture Overview

```
OnBarUpdate()
â”œâ”€â”€ ProcessPendingButtons()        â† button actions deferred from UI thread
â”œâ”€â”€ SyncPositionState()            â† detect de-syncs, recover silently
â”œâ”€â”€ Exit watchdog (pendingExit)    â† stale exit force-flatten after STALE_EXIT_TICKS
â”œâ”€â”€ Aggressive limit timeout       â† cancel ASK/BID if unfilled after 3 sec
â”œâ”€â”€ Live exit monitors (if in trade)
â”‚   â”œâ”€â”€ MonitorHiddenStops()       â† SL, BE lock, TP (unified priority 1,2,5)
â”‚   â”œâ”€â”€ MonitorAdaptiveTrail()     â† trail + backtrack window (priority 4)
â”‚   â””â”€â”€ MonitorTrapDetector()      â† trap score, escape, stop-hunt (priority 3)
â”œâ”€â”€ Session housekeeping
â”‚   â”œâ”€â”€ UpdateVwap()
â”‚   â”œâ”€â”€ UpdateHtfBias()
â”‚   â”œâ”€â”€ TrackEmaCross()
â”‚   â”œâ”€â”€ UpdateOpenTypeClassification()
â”‚   â”œâ”€â”€ UpdateLiquidityVoid()
â”‚   â””â”€â”€ UpdateVolumeProfile()
â”œâ”€â”€ AutoFlattenCheck()
â”œâ”€â”€ CalculateSignals() + ApplySmartFilters()  â† once per bar, when flat
â”œâ”€â”€ UpdateManualSignal()                      â† once per bar
â”œâ”€â”€ TryAutoEntry()                            â† once per bar, if autoMode
â”œâ”€â”€ DrawChartAnnotations()
â””â”€â”€ UpdateDashboard()              â† throttled 333ms in Realtime

OnOrderUpdate()   â† ONLY place totalContracts and averageEntryPrice are set
OnPositionUpdate() â† sets pendingPositionFlat
OnExecutionUpdate() â† PnL tracking, consecutive loss count, daily limit
OnMarketData()    â† tape delta rolling window (Realtime only)
```

---

## 5. 12 Active Filters (F1â€“F12)

| # | Filter | Direction | Logic |
|---|---|---|---|
| F1 | Volume Confirmation | Both | Prior-bar volume vs 20-bar avg. VR < 0.5 â†’ âˆ’30%; VR > 2 â†’ +15% |
| F2 | Bull/Bear Conflict | Both | If min/max > 0.80 â†’ both penalized âˆ’30% |
| F3 | HTF Bias Composite | Both | 5-min EMA cross + session open drift + HTF EMA distance. â‰¥2 votes = bias. Opposed direction âˆ’50% |
| F4 | VWAP Slope | Both | VWAP rising > 5% ATR â†’ bull +10/bear âˆ’5; falling â†’ bear +10/bull âˆ’5 |
| F5 | 5-bar Price Slope | Both | Slope > 0.4 ATR against direction â†’ penalize up to âˆ’50% |
| F6 | EMA Cross Structure | Both | EMA fast < slow â†’ bull Ã—0.65 always (and vice versa) |
| F7 | RSI/Price Divergence | Both | Classic hidden divergence: +10 bonus |
| F8 | Confidence Flip Bonus | Directional | If dominant direction just reversed â†’ +15 to new dominant |
| F9 | EMA Cross Momentum | Directional | 0/1/2 bars after cross â†’ +25/+20/+15 boost |
| F10 | Order-Flow Tape | Both | Tape Î” > 0.25 â†’ bull +10/bear âˆ’5; < âˆ’0.25 â†’ bear +10/bull âˆ’5 |
| F11 | Liquidity Void | Both | While voidBarsRemaining > 0 â†’ both Ã—0.70 |
| F12 | Open-Type Alignment | Directional | Open-Drive bull/bear classified â†’ +8 boost to aligned direction |

All filters: output clamped to [0, 120]; raw-floor = 50% of pre-filter value (prevents runaway penalization).

---

## 6. Exit Hierarchy (Unified)

Each tick, when `stopsArmed && !pendingExit`:

```
1. MonitorHiddenStops()
   â”œâ”€â”€ Check BE lock (at breakevenAtPoints profit) â€” fire once, permanently
   â”œâ”€â”€ If price â‰¤ hiddenStopPrice â†’ ExitLong/Short (SL hit)
   â””â”€â”€ If price â‰¥ hiddenTargetPrice && !runnerMode â†’ ExitLong/Short (TP hit)

2. MonitorTrapDetector() [IsFirstTickOfBar only]
   â”œâ”€â”€ Compute trapScore from adverse movement, EMA conflict, high volume
   â”œâ”€â”€ Stop-hunt spike â†’ stopHuntSuspendBars = 4
   â”œâ”€â”€ trapScore â‰¥ 65 && adverse > 10pt â†’ immediate escape (priority 3a)
   â””â”€â”€ trapScore â‰¥ 50 && adverse > 5pt for 2 bars â†’ graduated escape (priority 3b)

3. MonitorAdaptiveTrail()
   â”œâ”€â”€ Activation: after 2 bars, profit â‰¥ trailActivationPoints
   â”œâ”€â”€ Compute GetTrailDistance() (ATR-weighted, regime-adjusted)
   â”œâ”€â”€ Apply tier floor (T1/T2/T3)
   â”œâ”€â”€ Apply MM backtrack if stopHuntSuspendBars > 0
   â””â”€â”€ If price hits trailPrice â†’ ExitLong/Short (trail hit)
```

---

## 7. Smart Trail Backtrack â€” MM Avoidance

**Problem:** MM bots routinely spike price below key swing lows (where retail stops cluster), then immediately reverse. A standard ratchet trail gets hit by this spike and exits at the worst price.

**Solution:** When a stop-hunt spike is detected, temporarily allow the trail to loosen â€” moving *away from price* by up to `SmartTrailBacktrackTicks` â€” giving the trade room to survive the spike and re-enter the valid trend.

```
Normal trail:    [price â”€â”€â†’] [trail ratchets up]
Stop-hunt:       [price spikes DOWN] â†’ trail would normally follow and exit
v5 behavior:     detect spike â†’ trail BACKS DOWN by N ticks â†’ price recovers â†’ no exit
```

**Parameters:**
- `SmartTrailBacktrackTicks` â€” default **4 ticks** ($20 per contract per tick), range 0â€“12
- `stopHuntSuspendBars` â€” 4-bar window after spike detected
- Backtrack is floored at `originalSlPrice` (never creates a worse-than-SL stop)
- One backtrack applied per bar; guard via `backtrackUsedBar`

**Stop-hunt detection criteria:**
- Price penetrates the 5-bar structural low (long) or high (short)
- Close[0] recovers back inside the previous bar's range

---

## 8. Order-Flow Tape Filter (G1)

**How it works:**

```csharp
OnMarketData(e) {
    if (trade >= ask) tapeAskVol += e.Volume;   // aggressor buy
    if (trade <= bid) tapeBidVol += e.Volume;   // aggressor sell
    // Rolling 30-second window, reset on new window
    cachedTapeDelta = (tapeAskVol - tapeBidVol) / total;   // range -1 to +1
}
```

**Where it's used:**
- **Auto-entry:** `tapeBlockL = tape < -0.3` blocks longs; `tapeBlockS = tape > 0.3` blocks shorts
- **F10 filter:** soft confidence adjustment (Â±10/âˆ’5)
- **Manual signal:** `tapeOk` required for BUY/SELL output (soft block at Â±0.1 threshold)
- **Dashboard:** Tape Î” readout with directional arrow

**Notes:**
- Live (Realtime) only â€” `OnMarketData` is not called during Historical
- Default ON; can be disabled via `OrderFlowFilterEnabled = false`
- No performance impact on backtest (filter condition skipped when not Realtime)

---

## 9. Manual Trade-Signal Indicator

Designed for high-accuracy single-click manual entry decisions. Updates once per bar (when flat) via `UpdateManualSignal()`.

**Scoring confluence factors:**
```
1 point â€” HTF bias agrees (or neutral)
1 point â€” EMA fast/slow cross direction agrees
1 point â€” Order-flow tape not opposed
1 point â€” Fresh EMA cross (â‰¤ 3 bars ago, same direction)
1 point â€” Confidence flip (dominant direction just changed to this direction)
```

**STRONG BUY / STRONG SELL** requires:
- Confidence â‰¥ minSignalConfidence + 15
- Confluence â‰¥ 4 out of 5 points

**WAIT reasons shown in dashboard:**
- `low conf` â€” confidence below threshold
- `htf-against` â€” 5-min / session bias opposes
- `ema-against` â€” EMA cross opposes
- `tape-against` â€” order-flow delta opposes
- `liq-void` â€” liquidity void active

**Important:** This signal is for **entry timing only**. All risk management (SL, trail, TP) is handled by the strategy's hidden stop layer regardless of how you entered.

---

## 10. Dashboard Redesign

Layout (top to bottom, width 290px):

```
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚  â‰¡  NQ  Mm-ATM v6             â†™â†˜  â”‚  â† draggable title + resize grip
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  â–² STRONG BUY                       â”‚  â† 22pt signal label
â”‚  conf=72 htf=1 tape=0.41           â”‚  â† reason / confluences
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  [MANUAL] [AUTO] [HRS ON]           â”‚  â† mode + hours toggle
â”‚  â—„  Momentum+VWAP  â–º               â”‚  â† strategy selector
â”‚  Active: Momentum+VWAP [M+V=71 KLB=52]â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  Bull: 72%  â–ˆâ–ˆâ–ˆâ–ˆâ–ˆâ–ˆâ–ˆâ–ˆâ–‘â–‘             â”‚  â† confidence (opacity = strength)
â”‚  Bear: 31%  â–ˆâ–ˆâ–ˆâ–‘â–‘â–‘â–‘â–‘â–‘â–‘             â”‚
â”‚  Tape Î”: +0.41  â–² buyers           â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  â— LONG Ã—2                         â”‚  â† status badge
â”‚  Pos: 2/4  Avg: 21456.50           â”‚
â”‚  SL: 21438.50  (18pt | $360)       â”‚
â”‚  TP: 21506.50  (50pt | $1000) R=0.92â”‚
â”‚  Trail: 21445.00 (6.2pt T2-Strong M)â”‚  â† M = manual mode active
â”‚  Trap: 22%  bars=3                 â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  Unrealized: +$184.00              â”‚
â”‚  Daily P&L: +$264.00  [2/4]        â”‚
â”‚  Account P&L: +$264 (R+264/U+0)   â”‚
â”‚  Balance: $52,848.00               â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  VWAP: 21451.25                    â”‚
â”‚  Hours: â— open  9:30 AMâ€“4:00 PM   â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  [BUY MKT ] [SELL MKT]             â”‚
â”‚  [BUY ASK ] [SELL BID]             â”‚
â”‚  [BUY LMT ] [SELL LMT]             â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  Qty:      [âˆ’]  2           [+]    â”‚
â”‚  SL:       [âˆ’]  18pt|$360   [+]    â”‚
â”‚  TP:       [âˆ’]  50pt|$1000  [+]    â”‚
â”‚  Jump%:    [âˆ’]  50%         [+]    â”‚
â”‚  Trail Â±pt:[âˆ’]  6.2pt|25tk  [+]    â”‚  â† manual trail nudge (1 pt/click)
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  [CLOSE] [CLOSE 1] [JUMP SL]       â”‚
â”‚  [FLATTEN][KILL][TRL NOW][TRL ON]  â”‚
â”‚  [TRP ON] [BE ON]                  â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚              â†™ resize â†˜            â”‚  â† drag to resize width & height
â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
```

**Button legend (row 5 + row 6):**

| Button | Default state | Action |
|---|---|---|  
| `TRL NOW` | magenta, disabled when flat | Activates hidden trail immediately at auto-computed distance. Sets manual mode. |
| `TRL ON/OFF` | dark-slate / dark-red toggle | Master trail on/off. When OFF, trail and TRL NOW are both inactive. |
| `TRP ON/OFF` | dark-slate / dark-red toggle | Trap Detector on/off. See Â§14.6. |
| `BE ON/OFF` | dark-slate / dark-red toggle | Break-even auto-lock on/off. See Â§14.4. |

---

## 11. Properties Reference

| Group | Property | Default | Range | Notes |
|---|---|---|---|---|
| **1 - Risk** | `SlPoints` | 18 | 1â€“500 | Points |
| | `TpPoints` | 50 | 1â€“500 | Points |
| | `MaxDailyLossDollars` | 1000 | 500â€“20000 | $ per session |
| | `MaxDailyProfitDollars` | 1000 | 500â€“50000 | $ per session |
| | `MaxTradesPerDay` | 4 | 0â€“999 | 0 = unlimited |
| | `Contracts` | 1 | 1â€“10 | Per entry |
| | `MaxContracts` | 4 | 1â€“20 | Total position limit |
| | `AllowMultiEntryPerBar` | **true** | â€” | **When false, one entry per bar max** |
| **2 - Mode** | `AutoMode` | false | â€” | Toggle strategy auto-entry |
| | `AutoStrategy` | 2 | 0â€“2 | 0=M+V, 1=KLB, 2=Auto |
| | `MinSignalConfidence` | 55.0 | 20â€“100 | % threshold |
| | `EntryDelaySeconds` | 1 | 0â€“60 | Cooldown between entries |
| **3 - Trail/SL** | `TrailEnabled` | true | â€” | â€” |
| | `TrailActivationPoints` | 5 | 1â€“100 | Profit before trail starts |
| | `TrailAtrMultiplier` | 1.5 | 0.5â€“5.0 | ATR component |
| | `SmartTrailBacktrackTicks` | **4** | 0â€“12 | **MM avoidance â€” 0 to disable** |
| | `BreakevenAtPoints` | 8 | 2â€“50 | Lock SL at entry once hit |
| | `BreakevenEnabled` | **true** | â€” | **Master BE toggle; dashboard BE button mirrors** |
| | `JumpSlPercent` | 50 | 10â€“95 | Jump SL button move % |
| | `SlTpAdjustStep` | 5 | 1â€“50 | Dashboard Â±step |
| **4 - Smart Logic** | `EnableTrapDetector` | true | â€” | â€” |
| | `OrderFlowFilterEnabled` | true | â€” | Live only |
| **5 - Indicators** | `EmaPeriodFast` | 9 | 3â€“50 | â€” |
| | `EmaPeriodSlow` | 21 | 10â€“200 | â€” |
| | `RsiPeriod` | 14 | 5â€“30 | â€” |
| | `AtrPeriod` | 14 | 5â€“30 | â€” |
| | `HtfEmaPeriod` | 45 | 10â€“200 | HTF structure EMA |
| **6 - Hours** | `TradingHoursEnabled` | true | â€” | â€” |
| | `TradingStartTime` | 93000 | 0â€“235959 | HHMMSS |
| | `FlattenTime` | 160000 | 0â€“235959 | HHMMSS |
| **7 - Display** | `ShowEma` | true | â€” | EMA 9/21 on chart |
| | `ShowVwap` | true | â€” | VWAP line on chart |
| **8 - Diagnostics** | `EnableDiagLog` | false | â€” | CSV to `~/NinjaTrader 8/` |

---

## 12. Backtest Validation Plan

1. **Keep `Mm_ATM_v4.cs` intact** â€” reference baseline (do not modify)
2. Run v5 in Strategy Analyzer:
   - Instrument: NQ (or MNQ)
   - Period: 30 trading days
   - Resolution: 1-min, Calculate = OnEachTick
   - Compare: Net PnL, max drawdown, win rate, avg R-multiple per trade
3. **Evaluate specifically:**
   - Did removing S2 + S3 improve the losing-strategy drag?
   - Does the MM-avoidance backtrack reduce trail-hit losses?
   - Does the 12-filter set produce fewer low-quality entries vs 20-filter set?
4. **SIM playback validation:**
   - Load v5 on SIM account, replay last 5 high-volatility sessions
   - Manually verify signal labels match price action
   - Confirm hidden SL/TP lines draw correctly on chart
5. **Live paper trading** before funding increase

---

## 14. Post-Release Updates

> Changes applied **after** the initial v5 rewrite. Each entry records what was changed, why, and the implementation approach.

---

### 14.1 â€” Dashboard Resize & Width Fix

**Date:** April 25, 2026  
**Problem:** Dashboard `Border` was hard-coded at `Width = 290`. The ScrollViewer's scrollbar (~17 px) consumed ~17 px of that, leaving only ~253 px visible â€” not enough to show both `âˆ’` and `+` adjustment buttons. The `+` button was visually cut off and effectively un-clickable. The window could also not be resized.

**Fix:**
- Default width raised: **290 â†’ 340 px**
- Default max-height raised: **700 â†’ 720 px**
- `dashOuterBorder` and `dashScroller` stored as fields so resize handlers can mutate them
- **Bottom-right resize grip** (`â†™ resize â†˜`) added as draggable `TextBlock` at bottom of outer StackPanel
  - `Cursor = SizeNWSE`; captures mouse on press, releases on mouse-up
  - Drag updates `dashOuterBorder.Width` and `dashScroller.MaxHeight` live
  - Width clamped: **280â€“700 px**; Height clamped: **400â€“1 400 px**
  - Persists to `dashWidth` / `dashHeight` fields for the session (reset to defaults on strategy disable/enable)
- Adjust row components widened: label **56 â†’ 60 px**, value column **130 â†’ 150 px**, buttons **32Ã—26 â†’ 36Ã—28 px**

**Fields added:** `dashOuterBorder`, `dashScroller`, `dashResizing`, `dashResizeStart`, `dashResizeStartW/H`, `dashWidth = 340`, `dashHeight = 720`

---

### 14.2 â€” Trail Diagnostics (EnableDiagLog gated)

**Date:** April 25, 2026  
**Problem:** Trail appeared to not move. Root cause was that trail simply hadn't reached activation profit yet, but there was no visible feedback explaining why.

**Fix:** Added throttled `Print()` diagnostics (5-second minimum interval) throughout `MonitorAdaptiveTrail()`:

| Trigger | Output |
|---|---|
| First 2 bars after entry | `TRAIL diag: waiting bar-guard barsSinceEntry=N` |
| Profit below activation | `TRAIL diag: waiting profit X.XX / activation Y.YY pt (need Z more)` |
| Trail activated | `TRAIL ACTIVATED profit=â€¦ dist=â€¦ tier=â€¦` (**always printed**) |
| Trail price moves | `TRAIL MOVE X.XX -> Y.YY (price=â€¦ off=â€¦ tier=â€¦ maxProf=â€¦)` (**always printed**) |
| Trail stable (no new high) | `TRAIL diag: stable trail=â€¦ cand=â€¦ price=â€¦ off=â€¦ (cand<=trail â†’ hold)` |
| Trail hit | `TRAIL HIT LONG/SHORT @ â€¦` (**always printed**) |

The "stable" message is the key one â€” it proves the engine is running and shows the computed candidate vs current trail. Enable via `EnableDiagLog = true` (Group 8).

**Field added:** `lastTrailDiagTime`

---

### 14.3 â€” Manual Trail Control (TRL NOW / Trail Â±pt)

**Date:** April 25, 2026  
**Rationale:** The auto-trail activation threshold (`TrailActivationPoints = 5 pts`) may not trigger in time when a trader has already captured profit and wants to protect it immediately. Two controls were added.

#### TRL NOW button
- Dashboard button (row 5, magenta), enabled only when in a live position with `trailEnabled = true`
- Calls `RequestTrailActivate()` â†’ `pendingTrailActivate` flag â†’ `ActivateTrailManual()` on data thread
- Places trail at `GetTrailDistance()` away from current bid (long) or ask (short)
- Safety: trail is floored at `originalSlPrice` â€” never places trail worse than original SL
- Sets `manualTrailMode = true` â†’ auto-ratchet paused (no overwrite by algorithm)
- Logs: `TRAIL ACTIVATED MANUAL @ X.XX (price=â€¦ dist=â€¦pt profit=â€¦pt)`

#### Trail Â±pt adjust row
- New `MakeAdjustRow("Trail Â±pt:", â€¦)` between Jump% and the separator
- `âˆ’` button: tightens by `trailNudgeStepPoints` (default **1 pt**) â€” moves trail toward price, locking more profit
- `+` button: loosens by `trailNudgeStepPoints` â€” moves trail away from price, giving trade more room
- Activates `manualTrailMode = true` on first nudge (also activates trail if not yet active)
- Distance label shows live: `X.X pt | Y tk` plus `M` suffix when in manual mode; `â€” (waiting)` when trail not yet active; `OFF` when `trailEnabled = false`
- Safety clamps:
  - Tighten: trail cannot reach at-or-past current price (no self-stop)
  - Loosen: trail cannot go beyond `originalSlPrice` (no extra risk beyond original SL)

**Manual mode behavior:** When `manualTrailMode = true`, `MonitorAdaptiveTrail()` skips all auto-ratchet computation and only runs the hit-detection check (`price â‰¤ trailPrice` â†’ exit). The invisible exit mechanism is **identical** â€” same field, same `ExitLong/Short` market order, same chart line.

**To resume auto-ratchet:** `manualTrailMode` is reset to `false` only when `ArmHiddenStops()` is called (new trade arm). There is no manual "go back to auto" button â€” re-entering the trade resets it.

**Fields added:** `pendingTrailActivate`, `pendingTrailNudgePoints`, `trailNudgeStepPoints = 1.0`, `manualTrailMode`, `lblTrailDistVal`, `btnTrailNow`  
**Methods added:** `ActivateTrailManual()`, `NudgeTrailDistancePoints(int)`, `RequestTrailActivate()`, `RequestTrailNudge(int)`

---

### 14.4 â€” Break-Even Master Toggle (BE ON/OFF)

**Date:** April 25, 2026  
**Problem:** `BreakevenAtPoints = 8` was triggering too early in some sessions, moving SL to entry before the trade had room to breathe.

**Fix:** Added a master `breakevenEnabled` switch:

- **`BE ON / BE OFF` button** added to dashboard row 5 (dark-slate / dark-red)
- When `OFF`, the entire BE-lock branch in `MonitorHiddenStops()` is bypassed
- Tooltip: "Break-Even lock â€” when ON, moves SL to entry once profit reaches the BE-Trigger setting (Group 3). Turn OFF to let the trade run without auto-BE."
- `BreakevenEnabled` NinjaScript property added to Group 3 - Trail / SL (default `true`)

**Recommendation:**
| Situation | Suggested setting |
|---|---|
| Trending NQ session (Open Drive) | BE ON, raise trigger to 14â€“18 pt |
| Choppy / range session | BE OFF, rely on trail |
| Running with `manualTrailMode` | BE OFF (trail controls SL directly) |
| High-confidence runner (`T3-Runner`) | BE OFF â€” `runnerModeActive` already skips BE, but belt-and-suspenders |

**Fields added:** `breakevenEnabled`, `btnBeToggle`  
**Property added:** `BreakevenEnabled` (Group 3 - Trail / SL, default `true`)

---

### 14.5 â€” Allow Multiple Entries Per Bar

**Date:** April 25, 2026  
**Background:** Bug fix A6 in the original v5 rewrite added `enteredThisBar` (reset on `IsFirstTickOfBar`) to prevent double-fills on the same bar â€” a real bug that caused over-sized positions.

**Issue:** The guard also blocked **intentional** re-entries on the same bar (e.g. quick fill-and-reverse, or adding to a winner after a partial close on the same bar).

**Fix:** `enteredThisBar` guard is now conditional:

```csharp
// Before:
if (enteredThisBar) return false;

// After:
if (enteredThisBar && !allowMultiEntryPerBar) return false;
```

- **`AllowMultiEntryPerBar`** property added to Group 1 - Risk (default **`true`** = ALLOW)
- **Default is permissive** â€” behavior matches user expectation for manual trading
- Set `false` to restore the strict single-entry-per-bar discipline for auto-mode
- Note: all other gates still apply (`MaxTradesPerDay`, daily-loss halt, `KILL`, `pendingExit`)

**Field added:** `allowMultiEntryPerBar`  
**Property added:** `AllowMultiEntryPerBar` (Group 1 - Risk, Order 8, default `true`)

---

### 14.6 â€” Trap Detector Explained (TRP ON / TRP OFF)

**Date:** April 25, 2026  
**Context:** User question â€” "what is the TRP ON button?"

**`TRP ON` = Trap Detector** â€” a bar-close pattern detector for market-maker stop-hunt behavior.

**How it works:**
1. Each bar close, `MonitorTrapDetector()` computes a `trapScore` from:
   - Adverse price movement (how far price moved against the trade)
   - EMA fast/slow conflict with trade direction
   - Above-average volume on the adverse move
2. **Stop-hunt spike detection:** if price penetrated the 5-bar structural low/high but *closed back inside* the prior bar's range â†’ `stopHuntSuspendBars = 4` (4-bar window opens)
3. During the suspend window: trail is allowed to **back-track** (loosen) up to `SmartTrailBacktrackTicks` so the MM spike doesn't sweep the trail
4. **Trap escape exits:**
   - `trapScore â‰¥ 65 && adverse > 10 pts` â†’ immediate market exit (priority 3a)
   - `trapScore â‰¥ 50 && adverse > 5 pts for 2 consecutive bars` â†’ graduated exit (priority 3b)
5. Dashboard `Trap:` label shows live score + bars-in-trade

**When to keep it ON:** NQ liquid sessions (9:30â€“11:00 AM, 1:30â€“2:30 PM) where MM stop-hunt activity is highest. The stop-hunt backtrack and graduated escape together reduce "swept-and-reversed" losses.

**When you might turn it OFF:** Low-volatility range sessions, or backtesting on higher timeframes where bar-close stop-hunt patterns are less meaningful.

**Tooltip added** to dashboard `TRP ON` button for quick in-session reference.

---

## 13. Known Differences vs v4

| Area | v4 behavior | v5 behavior |
|---|---|---|
| Strategies | 4 (0â€“3) | 2 + Auto (0â€“2) |
| ORB state | Full ORB tracking | Open-Type classification (G3) only |
| Filters | 20, evaluated sequentially | 12, single pass with penalty floor |
| Exit subsystems | 5 competing | 1 ordered priority list |
| Trail bookkeeping | Multiple entry points, DCA trail | Single arm per fill |
| Phantom contracts | Possible on canceled limits | Fixed (deferred to OnOrderUpdate.Filled) |
| Trail in stop-hunt | Ratchets into spike | Backs off `SmartTrailBacktrackTicks` |
| Manual trail control | None | `TRL NOW` button + `Trail Â±pt` nudge row; `manualTrailMode` pauses auto-ratchet |
| Break-even toggle | Always on | `BE ON/OFF` button + `BreakevenEnabled` property |
| Same-bar re-entry | Blocked (A6 fix) | Configurable: `AllowMultiEntryPerBar` (default `true` = allow) |
| Dashboard width | Varies (v4 clutter) | Fixed 340 px default, user-resizable via grip (280â€“700 px) |
| Trail diagnostics | None | Throttled `Print()` via `EnableDiagLog` (waiting / stable / move / hit) |
| Manual signal display | Bull/Bear % only | Explicit 5-level signal label + reason |
| Order-flow | None | 30-sec tape delta (live only) |
| Liquidity void | Not detected | 3-bar 2Ã—ATR detector |
| Open-type | Not classified | RTH Open-Drive classification |
| Properties | ~30 / 12 groups | ~28 / 8 groups |
| Code lines | ~4,700 | ~1,800 |



---

## 14.7 ï¿½ Live Manual-Trader Hardening (Apr 25, 2026)

Set of fixes after live NQ paper-trading exposed UX + correctness gaps.

### Daily Profit / Loss ï¿½ DOES NOT KILL THE STRATEGY
**Behavior:** When the live or realized PnL crosses `MaxDailyProfitDollars` or `-MaxDailyLossDollars`, the strategy:
1. Closes the open trade (`ExecuteFlatten`)
2. Sets `dailyProfitHit` / `dailyLimitHit` so further entry attempts are *blocked*
3. Displays a prominent banner on the dashboard:
   - Profit: ?? gold "DAILY PROFIT TARGET $X ï¿½ trade closed. Disable+Enable to resume."
   - Loss:   ? red  "DAILY LOSS LIMIT $X ï¿½ trade closed. Disable+Enable to resume."
4. Logs a clarifying line: "closing trade, NOT killing strategy. Disable+Enable to resume."

The strategy itself stays in `State.Realtime` ï¿½ `Print` and dashboard still update. To resume trading the same session, the user toggles the strategy off/on (which re-runs `State.DataLoaded` and resets `dailyLimitHit / dailyProfitHit / emergencyKillActive` to `false`).

### TP draw distance + TP-not-firing-in-runner-mode (FIX)
**Bug observed:** Label "TP 50pt | 200tk | $1000" but the green horizontal line was actually at `entry + 18pt`. Price walked through it without triggering exit; only the trail eventually closed the trade.

**Root cause:**
1. `ArmHiddenStops` clamps `hiddenTargetPrice` to `prevDayHigh - 1tk` (or `prevDayLow + 1tk`) when the prev-day level sits between entry and the configured TP. The visual line moved, but the label was reading the *original* `tpPoints` setting (50), not the actual line.
2. In runner mode (HTF agrees), `MonitorHiddenStops` uses `effTp = 500pt` and **bypasses the TP comparison entirely** (`&& !runnerModeActive`). So when the prevDay clamp pulled the runner's TP back to entry+18, the line existed but the gate was off.

**Fix:**
- New field `tpClampedByPrevDay` set in `ArmHiddenStops` and `ResizeHiddenStops` whenever a prevDay level pulls TP in.
- TP exit gate now: `priceLong >= hiddenTargetPrice && (!runnerModeActive || tpClampedByPrevDay)`. So a clamped runner TP fires; an un-clamped 500pt runner TP still rides the trail.
- `DrawChartAnnotations` rewritten to compute `slDistPts` and `tpDistPts` from the **actual** `hiddenStopPrice` / `hiddenTargetPrice`, not from `slPoints` / `tpPoints` properties. Adds `ï¿½ PD` suffix on the TP label when prev-day-clamped, and `+` sign on SL label when SL is in profit (post-Jump SL).

### SL +/- after Jump SL (FIX)
**Bug:** After `JUMP SL` moved the SL into profit, pressing the SL `-` button on the dashboard did nothing.

**Root cause:** Old buttons did `slPoints -= step; ResizeHiddenStops()`. `ResizeHiddenStops` recomputes from `entry - slPoints*tickPt`. After Jump SL had stored `slPoints` as a *price-distance* (not entry-distance), the formula produced a target either nonsensical or below the current SL ï¿½ and `breakevenLocked = true` (set by Jump SL) blocked any relaxation.

**Fix:**
- New thread-safe handler `RequestSlNudgePoints(int dPts)` enqueues `pendingSlNudge` (signed); `ProcessPendingButtons` calls `NudgeSlPricePoints(n)`.
- `NudgeSlPricePoints` operates **directly on `hiddenStopPrice`** in price space:
  - `-` (`dPts < 0`): WIDEN ï¿½ SL moves further from price (more breathing room). Allowed regardless of `breakevenLocked`. Also widens `originalSlPrice` so trail backtrack respects the new floor.
  - `+` (`dPts > 0`): TIGHTEN ï¿½ SL moves toward price. Clamped to `price - 1tk`. Sets `breakevenLocked = true` so auto-BE doesn't undo it.
- After move, `slPoints` is reset to the actual distance from current price (matches Jump SL's convention) and dashboard refreshes immediately via `DrawChartAnnotations`.
- Sanity floor: SL never beyond `entry ï¿½ 200pt` from current trade.

### Trail ï¿½ extra-aggressive on huge profit
Two new ladder rungs added to `MonitorAdaptiveTrail`:

| `trailMaxProfitPts` vs activation | Multiplier on `curDist` | Tier name |
|---|---|---|
| `= 4ï¿½ activation` | `ï¿½ 0.70` | T3-Runner (existing) |
| `= 6ï¿½ activation` | additional `ï¿½ 0.75` (cumulative ï¿½ 0.525) | T4-Big (new) |
| `= 8ï¿½ activation` | hard cap `min(curDist, max(1pt, ATRï¿½0.25))` | T4-Big |

Tier-floor table now includes `T4-Big` at **65 % of `trailMaxProfitPts`** (vs 50 % for T3). On a 60-pt runner this locks ~39pt instead of 30pt.

Manual nudges (`Trail ï¿½pt`) keep flowing through the same ratchet ï¿½ `manualTrailOffsetPoints` is added every pass, and tightening (`-`) takes effect *immediately* in the same handler.

### Spec doc + .md as living history
**Convention going forward:** every behavioral or property change appends a numbered subsection here (14.x). Future-recommended enhancements are listed with status `[planned]` so they survive across sessions.

### [planned] Future improvements derived from this session's log analysis
- Persist daily PnL across NinjaTrader restarts (currently resets on `DataLoaded`)
- Add `RESET DAILY` button on dashboard that flips `dailyLimitHit / dailyProfitHit / emergencyKillActive` to false without requiring strategy re-enable
- Add `auto-tighten on N consecutive losses` (e.g. after 2 losses, halve `aggressiveTrailMaxAtrFactor` for 1 hour)
- Add `auto-widen on N consecutive wins` (let winners run further)
- Add `partial profit at 1R` toggle ï¿½ close half at `1ï¿½ slPoints` so worst case is BE on remainder
- Add a `DOUBLE` button that doubles current `Qty` on conviction signal (already throttled by `MaxContracts`)
- Heuristic to skip TP-clamping by prevDay during high-ADX trend days (clamp wastes profit when trend is breaking through)
- Live diagnostic CSV: include `hiddenTargetPrice`, `hiddenStopPrice`, `trailPrice`, `tier`, `manualTrailOffsetPoints` per row ï¿½ for post-trade replay

---

## 14.8 ï¿½ Diagnostics, daily-reset & adaptive trail (this revision)

This revision builds on 14.7 and addresses three live-trading findings:

1. The CSV showed every win as `EXIT_WIN` with no way to tell whether the exit
   was the actual TP, a trail-hit, a BE-stop, a trap-escape, a manual close,
   or a daily-limit auto-flatten.
2. After **disable + re-enable** within the same RTH session the strategy
   immediately printed `DAILY PROFIT TARGET` and flattened. Root cause:
   `SystemPerformance.AllTrades` persists across enable/disable, but
   `processedTradeCount` was being reset to 0, so the OnExecutionUpdate loop
   re-credited every prior trade into `dailyRealizedPnL` on the next fill.
3. There is no manual `RESET DAILY` button, and no way to let winners run
   through prevDay H/L on strong-trend days, and no auto-tightening when
   the day starts going against us.

### 14.8.1 Differentiated EXIT_* tags

A new `string lastExitReason` is set immediately **before** every
`ExitLong`/`ExitShort` call site. `OnExecutionUpdate` then composes the
diag tag as `EXIT_WIN_<reason>` or `EXIT_LOSS_<reason>`. Reasons in use:

| Reason | Source |
|---|---|
| `TP` | Hidden target hit, no prevDay clamp |
| `TP_PD` | Hidden target hit, target was clamped to prevDay H/L |
| `SL` | Hidden stop hit while breakeven NOT yet locked |
| `BE` | Hidden stop hit while breakeven WAS locked |
| `TRAIL_<tier>` | Adaptive trail hit, e.g. `TRAIL_T4-Big` |
| `TRAP_IMM` | Trap detector immediate escape (score = 65, adverse > 10) |
| `TRAP_GRAD` | Trap detector graduated escape (score = 50, 2+ bars adverse) |
| `CLOSE` | User pressed `CLOSE` |
| `CLOSE_ONE` | User pressed `CLOSE 1` |
| `FLATTEN` | User pressed `FLATTEN` (Account.Flatten) |
| `KILL` | User pressed `KILL` (emergency halt) |
| `AUTO_FLATTEN` | Session-end auto-flatten at `flattenTime` |
| `CME_MAINT` | CME maintenance auto-flatten (16:55ï¿½18:00) |
| `DAILY_LOSS` | LiveDailyPnLCheck loss-limit auto-flatten |
| `DAILY_PROFIT` | LiveDailyPnLCheck profit-target auto-flatten |
| `UNK` | Defensive fallback (should never appear) |

`lastExitReason` is cleared after consumption so a stale tag cannot bleed
into a later trade.

---

## 15. Entry Blockers (`CanEnterTrade`) â€” Plain-English Reference

Every entry attempt (auto OR manual button) flows through `CanEnterTrade(label, isManual, direction)` in `Mm_ATM_v6.cs` ~line 1823. If any blocker fires, the dashboard shows **`<label> blocked: <reason>`** in orange/red and (where noted) writes a `BLOCK_*` row to the diagnostic CSV. Order matters â€” first match wins.

### 15.1 Hard blockers (apply to BOTH auto and manual)

| # | Blocker | Diag tag | Trigger | Why |
|---|---|---|---|---|
| 1 | **Pending exit** | â€” | `pendingExit == true` (an exit order is in flight) | Don't pile a new entry on top of an unresolved close. |
| 2 | **Daily limit** | â€” | `dailyLimitHit` OR `dailyProfitHit` | Daily loss cap or profit target was hit; trading is paused for the session (strategy stays loaded). |
| 3 | **KILL switch** | â€” | `emergencyKillActive` (you pressed KILL) | Hard stop until you reset. |
| 4 | **NEWS blackout** | `BLOCK_NEWS` | `newsBlackoutEnabled` AND inside Â±`newsBlackoutWindowMin` of a scheduled news event | Volatility spike + spread widening = MM heaven, our edge collapses. |
| 5 | **Already entered this bar** | â€” | `enteredThisBar` AND `!allowMultiEntryPerBar` | Prevents duplicate fills on the same bar (toggle the property to allow stacking). |
| 6 | **Max trades / day** | â€” | `dailyTradeCount >= maxTradesPerDay` AND flat AND auto only | Caps overtrading. Manual bypasses. |
| 7 | **CME maintenance** | â€” | clock between 16:55 and 18:00 ET | Exchange settle window â€” orders flake. |
| 8 | **Outside auto hours** | â€” | auto only â€” clock outside `tradingStartTime`..`flattenTime` | Auto is gated by the configured session; manual is always allowed. |
| 9 | **Opposite-side reversal** | â€” | direction opposite to current open position | Not really "blocked" â€” it triggers `ExecutePartialClose` instead (closes the contrarian side). |
| 10 | **Max contracts** | â€” | adding same direction beyond `maxContracts` | Position-size cap. |
| 11 | **DCA suppression** | `BLOCK_DCA` | same-direction add AND `suppressDcaOnLossStreak` AND `consecutiveLosses >= suppressDcaLossN` | Don't average down into a losing streak â€” that's how accounts blow up. |
| 12 | **Entry cooldown** | â€” | seconds since last entry < `entryDelaySeconds` | Anti-spam between entries. |

### 15.2 Soft blockers (AUTO ONLY â€” manual buttons bypass since v14.17)

These are smart filters that protect the auto-strategy. Manual entries are treated as explicit human intent and skip them.

| # | Blocker | Diag tag | Trigger | Why |
|---|---|---|---|---|
| S1 | **CHOP** | `BLOCK_CHOP` | `chopFilterEnabled` AND `IsChoppy()` returns true. Tests: ADX collapsed, EMAs converged, recent close-range tight, OR tape fighting entry direction. | Choppy regime = high stop-out probability with low edge. The 03-20 chop-spiral autopsy showed all 7 losing shorts had every chop signature simultaneously. |
| S2 | **SL CLUSTER cooldown** | `BLOCK_SL_CLUSTER` | `slClusterCooldownEnabled` AND now < `slClusterCooldownUntil` (set after N stop-losses in a short window) | "Toxic regime" detector â€” pause new entries so we don't chain another loss. |
| S3 | **POST-WIN cooldown** | `BLOCK_POST_WIN` | `postWinSameDirCooldownEnabled` AND last winning exit was SAME direction within `postWinSameDirCooldownMin` (default 7 min) | Catches MM stop-runs that ramp price against us right after our trail kicked out, then continue trend in the original direction. |
| S4 | **DIR LOCKOUT** | `BLOCK_DIR_LOCKOUT` | `dirLockoutEnabled` AND now < per-direction lockout end (set after `dirLockoutLossN` losses on that side within `dirLockoutWindowMin`) | One direction is broken right now â€” only allow the opposite side until lockout expires. |
| S5 | **EXTENSION** | `BLOCK_EXTENSION` | `extensionFilterEnabled` AND `ATR >= extensionMinAtrPoints` AND `\|Close âˆ’ VWAP\| / ATR > extensionMaxAtrFromVwap` (default 5Ã—) | Don't chase late â€” over-extended price = high MM-stop-run probability. |

**Manual bypass policy (v14.17):** All five S1â€“S5 filters check `!isManual` first. Manual buttons are explicit human conviction and skip them. Hard blockers (15.1) still apply to manual.

### 15.3 Diagnostic-only "blocker" (not really a block)

| Tag | Where | Meaning |
|---|---|---|
| `CLAMP_SKIP` | `MonitorAdaptiveStops` at trade-arm time | Logged when the **prev-day H/L TP-clamp was bypassed** because `ADX >= highAdxThreshold` AND `skipPrevDayClampOnHighAdx` is on. NOT an exit and NOT a block â€” just a heads-up that the TP was left wider on a trend day so price could reach it. |

### 15.4 Auto-entry confidence gate (separate from `CanEnterTrade`)

Inside `EvaluateAutoEntry`, even when `CanEnterTrade` would pass, an entry must clear:
- `lastBullConfidence >= effMinL` (or bear â‰¥ `effMinS`)
- `!rsiDown` / `!rsiUp` (avoid against-momentum)
- `!overLong` / `!overShort` (overextension â€” relaxed by SWEEP boost)
- `!nearH` / `!nearL` (don't enter at session H/L)
- `!volSpike` (avoid volume-spike bars)
- `!htfBlockL` / `!htfBlockS` (HTF EMA must agree, if HTF filter on)
- `!tapeBlockL` / `!tapeBlockS` (tape delta must not be opposing > 0.3)

The **Sweep Boost** (Â§14.10) lowers `effMin` by `liquiditySweepConfBoost` (default 8.0 pts) AND clears `overLong/overShort` when a fresh bull/bear liquidity-sweep pattern prints â€” so a high-quality reversal can take a fade trade even when overextended. Logged as `SWEEP_BOOST`.

---

## 16. Exit Types â€” Complete Reference (`lastExitReason`)

Every exit sets `lastExitReason` (consumed in `OnExecutionUpdate` to build the `EXIT_WIN_*` / `EXIT_LOSS_*` diag row, then cleared). Below is the full taxonomy as wired in v14.19.

### 16.1 Hidden SL / TP family (in-memory, MM-invisible)

| Reason | Trigger | Notes |
|---|---|---|
| `SL` | Price crossed `hiddenStopPrice` | Stop is in-memory only â€” no `SetStopLoss()` call, so the MM order book never sees it. |
| `BE` | Same as `SL` but `breakevenLocked == true` | Loss is ~zero or a small win because BE was armed first. |
| `TP` | Price crossed `hiddenTargetPrice` | Hidden TP, MM-invisible. |
| `TP_PD` | Same as `TP` but `tpClampedByPrevDay == true` | Target was clamped just inside prev-day H/L (to avoid MM stop-run ladders sitting at those levels). |

### 16.2 Trail family â€” `TRAIL_<tier>`

The tier name is whatever `trailTierName` was at the moment the trail price was crossed:

| Tier | Condition that produced this tier |
|---|---|
| `TRAIL_T1-BE` | Profit â‰¥ 1.5Ã—activation, no HTF agree â†’ trail floor at +1 tick (locks BE) |
| `TRAIL_T2-Strong` | Profit â‰¥ 2.5Ã—activation â†’ floor 40% of peak |
| `TRAIL_T3-Runner` | Profit â‰¥ 4Ã—activation â†’ floor 45â€“50% of peak (50 if HTF agrees) |
| `TRAIL_T4-Big` | Profit â‰¥ 6Ã—activation â†’ floor 65% of peak (very protective) |
| `TRAIL_Aggr` | Trail started early via TRL NOW button â€” aggressive lock = max(2pt, 0.5Ã—ATR) |
| `TRAIL_Aggr-Mode` | AGGR mode override active â€” fixed `aggrTrailDistPts` distance |
| `TRAIL_Runner` | HTF EMAs agree, default-tier runner (no peak floor yet) |
| `TRAIL_Active` | Default tier â€” trail armed, no special condition |
| `TRAIL_RUNNER` | **v14.19** â€” RUN button was ON; wide `1.5Ã—ATR` (floor 4pt), no aggression multipliers / no tier floors |

### 16.3 Aggressive-exit family

| Reason | Trigger | Purpose |
|---|---|---|
| `AGGR_ADVERSE` | Within first `aggrAdverseMaxBars` AND adverse move â‰¥ `max(aggrAdverseMinPts, aggrAdverseAtrFactor Ã— ATR)` AND we hadn't yet hit `aggrAdverseDisarmPeak` profit | Cuts losers fast before they mature. Runs at top of `MonitorHiddenStops` so it preempts the static SL. |
| `AGGR_PULLBACK` | After being up â‰¥ activation, retracement from peak â‰¥ `aggrPullbackAtrFactor Ã— ATR` within first `aggrPullbackMaxBars` | Anti-MM-trap â€” stops a winner from being flipped into a loser by a stop-run. |

### 16.4 Reversal / trap family

| Reason | Meaning |
|---|---|
| `FAST_REV` | Fast-reversal exit â€” sharp counter-move detected before trail engages. Smarter than waiting for the static SL or the trail. |
| `TRAP_IMM` | **Immediate** trap â€” strong evidence of MM trap on entry bar; bail instantly. |
| `TRAP_GRAD` | **Gradual** trap â€” trap score accumulated over multiple bars then crossed threshold. |

### 16.5 Manual / system family

| Reason | Meaning |
|---|---|
| `FLATTEN` | User pressed FLATTEN (close current trade only, strategy stays enabled) |
| `CLOSE` | User pressed CLOSE-ALL (close all positions for the instrument) |
| `CLOSE_ONE` | User pressed CLOSE-ONE (reduce by 1 contract) |
| `KILL` | User pressed KILL SWITCH (close + disable strategy) |
| `AUTO_FLATTEN` | End-of-session auto-flatten before settle |
| `CME_MAINT` | Auto-flattened at CME maintenance window (16:55â€“18:00 ET) |
| `DAILY_LOSS` | `dailyRealizedPnL <= -maxDailyLossDollars` â€” daily loss cap fired |
| `DAILY_PROFIT` | `dailyRealizedPnL >= maxDailyProfitDollars` â€” daily profit target fired |
| `UNKNOWN` | Initial value (never appears for a real exit) |

### 16.6 How to read the diag CSV

`OnExecutionUpdate` wraps the reason with the trade outcome:
- `EXIT_WIN_<reason>` if `last.ProfitCurrency > 0`
- `EXIT_LOSS_<reason>` otherwise

So a row with action `EXIT_WIN_TRAIL_RUNNER` literally means: a winning trade closed by the **Runner Mode** wide trail (v14.19 RUN button was ON for that trade). Inversely, `EXIT_LOSS_AGGR_PULLBACK` means the AGGR pullback exit fired and gave back enough peak profit to close net-negative.

---

## 17. Top-Dashboard Signal Strip & Order-Flow Tape

The strip at the top of the dashboard is the **Manual Trade Signal** â€” a single one-glance recommendation built from confidence + HTF + EMA + tape + EMA-cross momentum. It is **informational only** and does not place orders; it tells you whether right now is a good moment to press BUY or SELL.

### 17.1 The labels

| Display | Color | Meaning |
|---|---|---|
| `â–² STRONG BUY` | Bright green | Long signal with strong confluence (see formula in Â§17.5) |
| `â†‘ BUY` | Lime | Long signal â€” basic gates passed |
| `â— WAIT` | Gray | Not enough alignment â€” sit on hands. Reason text shows why. |
| `â†“ SELL` | OrangeRed | Short signal â€” basic gates passed |
| `â–¼ STRONG SELL` | Red | Short signal with strong confluence |

### 17.2 The supporting metrics (right of the signal)

- **`Bull: 67%` / `Bear: 33%`** â€” the two confidence scores from the 12-filter system (`lastBullConfidence`, `lastBearConfidence`, range 0â€“120, clamped). Each side accumulates contributions from F1â€“F12 (EMA stack, RSI, ADX, sweep, EMA-cross momentum, tape, void, open-type, etc.). The dominant side drives the signal direction. Opacity dims when below `minSignalConfidence`.
- **`conf=70`** â€” `dom = max(Bull%, Bear%)`. The same number as the higher of the two percentages above.
- **`htf=+1` / `0` / `-1`** â€” Higher-timeframe bias from `UpdateHtfBias` (5-min EMA stack + session-open vs ATR + 45-EMA voting). +1 bullish HTF, âˆ’1 bearish, 0 mixed/neutral.
- **`tape=+0.32`** â€” current order-flow tape delta (see Â§17.3).

### 17.3 The "Tape Î”" label

Format: `Tape Î”: +0.32  â–² buyers` (or `â–¼ sellers` / `â— balanced` / `off`).

#### How the value is computed (`OnMarketData` ~line 2774)

- **Live-only** â€” returns immediately if not in `State.Realtime` or if `OrderFlowFilterEnabled = false`.
- Rolling **30-second window** of every Last-trade tick:
  - Trade printed `>= Ask` â†’ counted as **aggressor BUY** (`tapeAskVol += volume`)
  - Trade printed `<= Bid` â†’ counted as **aggressor SELL** (`tapeBidVol += volume`)
  - Mid-price prints (between Bid and Ask) are ignored â€” they're not aggressive.
- `cachedTapeDelta = (askVol âˆ’ bidVol) / (askVol + bidVol)` â†’ range **âˆ’1.0 to +1.0**.
- Window resets every 30 seconds.

#### Label thresholds (line 3747)

| Tape Î” | Label | Color | Interpretation |
|---|---|---|---|
| `> +0.15` | `â–² buyers` | LimeGreen | Aggressors lifting offers â€” bid-side buying pressure |
| `âˆ’0.15` to `+0.15` | `â— balanced` | Gray | Roughly equal aggression both sides â€” no edge |
| `< âˆ’0.15` | `â–¼ sellers` | OrangeRed | Aggressors hitting bids â€” sell pressure dominant |
| (off) | `Tape Î”: off` | Gray | Order-flow filter disabled OR not in live mode (backtest/replay) |

### 17.4 How the tape is USED across the strategy

The tape feeds six independent decisions:

| # | Where | Effect |
|---|---|---|
| 1 | **Dashboard display** (line 3747) | Color + arrow on the Tape Î” label |
| 2 | **Confidence boost â€” F10** (`CalculateSignals` ~line 2971) | If `tape > +0.25` â†’ `Bull% += 10, Bear% âˆ’= 5`. If `tape < âˆ’0.25` â†’ `Bear% += 10, Bull% âˆ’= 5`. |
| 3 | **Auto-entry block** (`EvaluateAutoEntry` ~line 1975) | `tapeBlockL` if `tape < âˆ’0.30` (long blocked); `tapeBlockS` if `tape > +0.30` (short blocked). |
| 4 | **Manual signal gate** (`UpdateManualSignal` ~line 3087) | `tapeOk` requires `tape â‰¥ âˆ’0.1` for long, `tape â‰¤ +0.1` for short. Failed â†’ `WAIT â€” tape-against`. |
| 5 | **CHOP filter test #4** (`IsChoppy` ~line 1681) | If `chopBlockOppositeTape` AND `\|tape\| â‰¥ chopOppositeTapeMin` AND tape opposes direction â†’ blocks auto entry as `BLOCK_CHOP â€” tape against long/short`. |
| 6 | **Fast-reversal exit** (`MonitorFastReversal` ~line 1506) | Tape against open direction (`> 0.15` magnitude) is one input to the reversal score that fires `FAST_REV` exits. |

So the tape is a **trade quality control** at three different stages: pre-entry confidence (boost or block), entry-time gate (manual signal & CHOP), and in-trade reversal detection.

### 17.5 How the BUY / STRONG BUY decision is built (`UpdateManualSignal` ~line 3078)

**Step 1 â€” Direction & dominance:**
- `dir = +1` if `Bull% > Bear%`, `âˆ’1` if `Bear% > Bull%`, else `0`
- `dom = max(Bull%, Bear%)`

**Step 2 â€” Five gates must ALL pass (else WAIT):**
| Gate | Condition |
|---|---|
| `meetsConf` | `dom â‰¥ MinSignalConfidence` (default ~55) |
| `htfOk` | HTF bias agrees with direction (or HTF = 0) |
| `emaOk` | 1-min Fast EMA on the right side of Slow EMA for `dir` |
| `tapeOk` | tape not aggressively against (â‰¥ âˆ’0.1 long, â‰¤ +0.1 short, or filter off) |
| `noVoid` | not currently inside a liquidity-void window |

If any gate fails â†’ `WAIT â€” <reason1> <reason2> ...` (e.g. `WAIT â€” low conf htf-against tape-against liq-void`).

**Step 3 â€” Strength upgrade (BUY â†’ STRONG BUY):**

A "confluence" score is built (max 5):
- +1 if HTF agrees
- +1 if EMA agrees
- +1 if tape agrees
- +1 if a **fresh EMA cross** in the same direction within the last 3 bars
- +1 if a **confidence flip** just occurred AND previous dominant matched current direction

```text
level = (dom â‰¥ MinSignalConfidence + 15  AND  confluence â‰¥ 4)
        ? dir Ã— 2   // STRONG
        : dir       // normal
```

So **STRONG BUY** = `dom â‰¥ ~70` AND at least 4 of the 5 confluences align. Plain `BUY` = the 5 basic gates passed but the strength bar wasn't cleared.

The reason string format is:
```
STRONG BUY conf=78 htf=+1 tape=+0.42
BUY conf=58 htf=0 tape=+0.05
WAIT â€” low conf htf-against
```

### 17.6 Practical reading guide

| You see | Do |
|---|---|
| `STRONG BUY conf=78 htf=+1 tape=+0.42  â–² buyers` | Press BUY (or let auto take it). Multiple systems aligned. High-quality moment. |
| `BUY conf=58 htf=0 tape=+0.05  â— balanced` | Marginal. Auto might take it; you may want to wait one more bar for confirmation. |
| `WAIT â€” tape-against` | Your direction has aggressive opponents on tape. Don't fade without a reason â€” wait for tape to roll. |
| `WAIT â€” low conf htf-against` | Fast and slow timeframes disagree. Common in chop. CHOP filter is probably also blocking auto. |
| `WAIT â€” liq-void` | Last 3 bars had range > 2Ã—ATR (extreme bars). Wait for liquidity to return â€” spreads are wide and stops are unreliable. |
| `Tape Î”: off` | You're not in live mode, or the order-flow filter is disabled in properties. Tape contribution is neutral. |

### 17.7 Tunable properties

| Property | Default | Effect |
|---|---|---|
| `OrderFlowFilterEnabled` | `true` | Master switch. When OFF, tape neither displays nor influences anything. |
| `MinSignalConfidence` | ~55 | Threshold the dominant side must clear for any BUY/SELL recommendation (vs WAIT). STRONG requires `+15` more. |
| `ChopBlockOppositeTape` | `true` | Whether opposite-side tape can trigger a CHOP block. |
| `ChopOppositeTapeMin` | ~0.20 | `\|tape\|` magnitude required to trigger CHOP test #4. |

---

---

## 19. v6 Active Workplan â€” Apr 28, 2026 onward (PHASED)

> **Status:** Forked from `Mm_ATM_v5.cs` v5 14.20. v5 is **frozen** for live trading; v6 receives all new work. Each phase ships behind toggles default OFF. Commits use `v6 0.X.Y: <desc>` numbering.

### Phased build order (agreed Apr 28, 2026 â€” supersedes the original 6-item flat list)

> **Why phased:** F1 Regime Classifier is the keystone â€” every later feature reads regime. Building anything else first means rewriting it once F1 lands. We interleave **one quick UX win up front** (TP bug) and the **highest-leverage capture feature right after foundation** (Renko thrust) so we get felt value early.

#### ðŸ› ï¸ Phase 0 â€” Housekeeping  *(target: v6 0.1.x)*

| # | Task | Spec ref | Why first |
|---|---|---|---|
| **0.1** | **B1+B2** TP-line render bug + T+/T- propagation fix | Â§19.1 | Live UX pain. Small, contained, validates the v6 codebase ships clean before bigger surgery. |
| **0.2** | Add **secondary Renko 64/16 series** via `AddRenko()` + brick-state tracker (data plumbing only, no behavior change). New diag columns: `BrickColor`, `BrickAgeSec`, `ThrustStreak`, `RenkoMode`. | Â§19.6.C | Both F1 and W6/F3 need it. Build the data pipe once, used by everything after. |

#### ðŸ§  Phase 1 â€” The Brain (foundation, the most important phase)  *(target: v6 0.2.x)*

| # | Feature | Spec ref | Why |
|---|---|---|---|
| **1.1** | **F1 Regime Classifier** â€” TREND_UP/DN, RANGE, TRANSITION, VOLATILE state machine reading Renko streaks + ATR + range bounds. New `Regime` diag column + `REGIME_CHANGE` action rows. | Â§18.8.C.F1 | Every other feature gates on this. |
| **1.2** | **F7 htfBias decay on regime change** + persistent state across NT8 restarts. | Â§18.8.C.F7 | Directly fixes Apr 28 10:30 âˆ’$380 carryover-bias loss. Tiny add once F1 emits `REGIME_CHANGE`. |
| **1.3** | **F8 Dashboard "Regime + Levels" strip** (top of dashboard, Regime + Brick streak + nearest-level placeholder). | Â§18.8.C.F8 | Makes F1 visible so user can validate the classifier in real time before letting it gate trades. |
| **1.4** | **F4 Adaptive signal confidence per regime** (TREND Ã—0.75, RANGE Ã—1.10, VOLATILE Ã—1.30, TRANSITION Ã—2.0, default OFF for first 2 weeks of observation). | Â§18.8.C.F4 | This is where F1 starts *changing behavior*. Default OFF until classifier is trusted. |

> âœ… **Phase 1 success bar:** Replay Apr 28; classifier correctly emits `TREND_DN` 00:00â†’08:34, `TRANSITION` 08:34â†’09:30, `RANGE` 09:45â†’12:50. With F4 ON, the 10:30 wrong-side SHORT entry would be blocked.

#### âš¡ Phase 2 â€” Capture (turn the brain into money)  *(target: v6 0.3.x)*

| # | Feature | Spec ref | Why now |
|---|---|---|---|
| **2.1** | **W6 NinjaRenko follow mode** (RNK OFF/ON/THRUST toggle, brick-edge trail, 3-brick thrust override). Default `RNK OFF`. | Â§19.6 | Single biggest profit lever. Foundation in place; let it fire entries. |
| **2.2** | **F3 Renko Trend-Riding pyramid** (add 1 contract per 3-brick continuation, tier-tightening trail, auto-reload on continuation). Default OFF until W6 has 5 clean live thrusts. | Â§18.8.C.F3 | Stacks on W6 once thrust is proven. |

> âœ… **Phase 2 success bar:** Replay Apr 28 with HRS OFF + RNK THRUST ON â†’ must produce `THRUST_ENTRY` after 3rd green brick post-08:34, ride to â‰¥+50 pts, no AGGR_PULLBACK exit. With F3 also ON: â‰¥+$2,000 on the same move.

#### ðŸ›¡ï¸ Phase 3 â€” Defense (stop losing to MM)  *(target: v6 0.4.x)*

| # | Feature | Spec ref |
|---|---|---|
| **3.1** | **F5 MM-Trap Detector** (3+ stop-outs at same level â†’ block same-side; allow counter-side at half conf). Pure post-trade analytics, no order-routing changes. Cheapest defense win â€” Apr 27 already shows the pattern. | Â§18.8.C.F5 |
| **3.2** | **F2 POC / VAH / VAL / ON-high/low aware range trading** + MM stop-run-failure reversal back to POC. Needs F1 RANGE detection to gate. | Â§18.8.C.F2 |
| **3.3** | **F6 Range-break continuation** (Renko brick beyond range edge = entry, no extra signal needed). Natural extension of F2. | Â§18.8.C.F6 |
| **3.4** | **W3 AGGR-L1/L2** + **W4 PACE indicator**. PACE is largely subsumed by F1; mostly UI calibration of aggression vs regime. | Â§19.4 |
| **3.5** | **F9 Intelligent Trail v2 â€” MM-aware tick-level trail (NEW, user-prioritized)**. See Â§19.11 below. | Â§19.11 |

#### ðŸŽ¯ Phase 4 â€” Polish & UX  *(target: v6 0.5.x)*

| # | Feature | Spec ref |
|---|---|---|
| **4.1** | **W1 Drag SL/TP/Trail lines on chart** (DRAG ON/OFF toggle). | Â§19.2 |
| **4.2** | **W2 Buy/Sell Limit Â±N tick offset + TTL + dashed line**. | Â§19.3 |
| **4.3** | **W5 Lead-signal entry** â€” now informed by 3+ months of v6 logs from earlier phases. | Â§19.5 |

#### ðŸ§¬ Phase 5 â€” Memory & long-term edge  *(target: v6 0.6.x and beyond)*

| # | Feature | Spec ref |
|---|---|---|
| **5.1** | **Pattern-Memory Engine** (90-day fingerprint heatmap â†’ size scaling). | Â§18.3.A |
| **5.2** | **MM Playbook Detector** (formalized version of F5). | Â§18.3.B |
| **5.3** | **Per-hour self-tuning**. | Â§18.3.C |
| **5.4** | **Kelly sizing**. | Â§18.3.D |
| **5.5** | **Fib + Candle Reversal Scalp Module** (engulf, hammer, doji-with-tail at 38.2 / 50 / 61.8 levels). | Â§18.3.F |
| **5.6** | **V-Fakeout Filter** (sharp move â†’ 3-bar consolidation â†’ reverse engulf detection). | Â§18.3.G |
| **5.7** | **Stealth Mode** (hide bot-tell `Name` field, deferred from v5). | Â§18.3.E |
| **5.8** | **Spread / Slippage Awareness**. | Â§18.3.H |

### Ordering principles applied

- **Foundation before features** â€” F1 must precede everything that reads regime.
- **Quick UX win early** â€” TP bug in Phase 0 validates the v6 codebase ships clean.
- **Highest-profit feature right after foundation** â€” W6 + F3 in Phase 2 (Apr 28 missed +$1,915 rally is too big to leave for last).
- **Defensive features after capture** â€” losses bleed slower than missed wins explode opportunity cost.
- **Memory/learning features last** â€” they need months of v6 log data to be useful.
- **Existing v5 internals**: only refactor when a new feature *forces* it (e.g. trail engine in Phase 2 to add brick-edge mode). No "clean it up just because" â€” preserves stability.

### Candle-pattern coverage

Already documented across the spec, scheduled in Phase 5:
- **Â§18.3.F** â€” Fib + Candle Reversal Scalp Module (engulf, hammer, doji-with-tail at fib levels) â†’ Phase 5.5
- **Â§18.3.G** â€” V-Fakeout Filter (sharp move â†’ consolidation â†’ reverse engulf) â†’ Phase 5.6
- **Â§19.6.B Rule 6** â€” Doji-brick thrust pause (already part of W6 Renko mode) â†’ ships in Phase 2.1

### Detailed feature specifications (referenced by phases above)

> The Â§19.1 â€“ Â§19.8 subsections below contain the full per-feature specs originally written as a flat 6-item list. The phase plan above tells you **when** each gets built; these subsections tell you **how**.

### 19.1 Item 1 â€” TP line render bug (v6 14.21)

**Symptoms (user-reported Apr 27):**
- TP line sometimes does not appear on chart for auto OR manual entries (random).
- When TP line *does* appear, the dashboard `TP +/-` buttons sometimes do nothing in auto mode (manual nudges not propagating to redrawn line).

**Investigation plan:**
1. Find the chart-draw call that renders the TP line. Suspect candidates: `Draw.Line`/`Draw.HorizontalLine` in the position-tracking section.
2. Check whether the draw is gated by a stale flag (e.g. `stopsArmed`, `manualTrailEarlyStart`) that does not refresh after `T+/T-` press.
3. Check whether `manualTpOffsetPoints` (or equivalent) is actually consumed in the auto path â€” manual T+/T- may only be wired to the manual order route, missing the auto route.
4. Reproduce: enter auto trade, press TP+ on dashboard, verify diag log shows `TP_NUDGE` row and `HiddenTP` column changes; check chart line refreshes.

**Fix scope:**
- Unify TP-line render to a single helper called every tick when `Position != Flat` and `hiddenTargetPrice > 0`.
- Make `TP+/-` buttons always mutate `hiddenTargetPrice` (and the line) regardless of entry source (auto vs manual).
- Add `WriteDiagRow("TP_NUDGE", "by=...new=...source=auto/manual")` when buttons fire.

### 19.2 Item 2 â€” Drag SL/TP/Trail lines on chart (v6 14.22)

**Spec:**
- Replace existing `Draw.Line` calls with **draggable** `Draw.HorizontalLine` instances tagged `MmATM_SL`, `MmATM_TP`, `MmATM_TRAIL`.
- Behind a **DRAG ON / OFF** toggle button (default OFF) so chart panning never accidentally moves a line. *(Recommended over always-on.)*
- Each tick, poll the line's `Y` value; if it differs from the internal value by â‰¥ 1 tick, snap the internal value (`hiddenStopPrice` / `hiddenTargetPrice` / `trailPrice`) and write `WriteDiagRow("DRAG_SL", "old=X new=Y")` etc.
- **Trail-line drag:** dragging the trail freezes auto-ratchet (manual override) for the rest of the trade. A `MANUAL` badge appears in trail-tier display. Toggling the dashboard `TRL OFF -> ON` re-enables auto-ratchet (per user spec).

### 19.3 Item 3 â€” Buy/Sell Limit Â±N tick offset (v6 14.23)

**Spec:**
- Beside the existing **BUY** / **SELL** dashboard buttons, add a small Â±N tick spinner (default 0).
- N = 0 â†’ market entry (current behavior).
- N > 0 â†’ limit order at:
  - **BUY**: `Bid âˆ’ N Ã— TickSize` (waits for pullback)
  - **SELL**: `Ask + N Ã— TickSize` (waits for spike)
- New **TTL spinner** (default 60s, 0 = no expiry). When the TTL elapses without fill, the working limit auto-cancels. Diag rows `LIMIT_PLACED` / `LIMIT_FILLED` / `LIMIT_TTL_CANCEL`.
- **Dashed price line** drawn on chart while limit is working (drag-able once Item 2 is in).

### 19.4 Item 4 â€” AGGR-L1 / AGGR-L2 + PACE indicator (v6 14.24)

**Aggression levels (toggle button cycles NORM â†’ AGGR-L1 â†’ AGGR-L2 â†’ NORM):**

| Setting | NORM | AGGR-L1 | AGGR-L2 |
|---|---|---|---|
| `MinSignalConfidence` | 55 | 50 | 45 |
| `ExtensionMaxAtrFromVwap` | 5.0 | 6.0 | 8.0 |
| `PostWinSameDirCooldownMin` | 7 | 4 | 0 |
| `ChopFilterEnabled` | ON | ON | OFF |
| `MinAtrPointsToTrade` (NEW gate) | 4.0 | 3.0 | 2.0 |
| Entry-cooldown seconds | current | half | 0 |

**Auto-revert safety:** AGGR levels auto-revert to NORM when daily realized P&L drops below `âˆ’$X` (configurable; default `âˆ’$300`). Diag `AGGR_AUTOREVERT`.

**PACE indicator (default OFF, behind its own toggle):**
- Computed from: ATR(30) percentile vs last 5 sessions, bars-per-minute trade activity (from tape ticks), tape Î” swing magnitude, ADX strength.
- 4 states displayed as a colored badge top-right of dashboard:

| State | Color | Trigger | Behavior when `PACE ON` |
|---|---|---|---|
| `ðŸ¢ SLOW` | Gray | ATR < 40th pct & ADX < 18 | **Auto-pause** new auto-entries |
| `ðŸŸ¢ SAFE` | Green | ATR 40â€“70th pct, ADX 18â€“28 | Normal trading |
| `ðŸŸ¡ AGGRESSIVE` | Yellow | ATR 70â€“90th pct, ADX > 25 | Trending; favor longer holds |
| `ðŸ”¥ VOLATILE` | OrangeRed | ATR > 90th pct OR sudden ATR spike | **Auto-tighten Runner Mode** |

When `PACE OFF` (default), badge is informational only.

### 19.5 Item 5 â€” Lead-signal entry (v6 14.25)

**Pre-work:** Analyze 3â€“5 days of v14.20 diag logs. Specifically:
- Count `SIGNAL` rows with `BUY/SELL` label that *did not* result in an `ENTRY_*` row within 30s.
- Identify what blocked them (CHOP, EXTENSION, low conf, EMA-cross-confirmation delay, etc.).
- Quantify: would lead-entry have produced more profit on those missed setups, or more whipsaws?

**Build (only after analysis confirms):**
- New **LEAD ON/OFF** toggle (default OFF).
- When ON, the moment manual signal flips to BUY/SELL with `dom â‰¥ MinSignalConfidence + 5`, queue an immediate auto-entry skipping EMA-cross-confirmation delay.
- Restricted to **trend regime** (ADX > threshold + EMA stack + HTF agree) to avoid lead-entries in chop.

### 19.6 Item 6 â€” NinjaRenko follow mode (v6 14.26 â€” biggest)

**Architecture:** Add Renko 64-tick / 16-offset as a **secondary data series** via `AddRenko()`, accessed via `BarsArray[2]`. Trades fire on the primary chart's prices; Renko is a signal/management overlay only.

**Configurable parameters:**
- `RenkoBrickTicks` (default 64)
- `RenkoOffsetTicks` (default 16)

**3-mode toggle button (default OFF):**

| Mode | Behavior |
|---|---|
| `RNK OFF` | No Renko influence (current strategy unchanged) |
| `RNK ON` | **Augment** â€” entries require BOTH standard signal AND Renko brick alignment |
| `RNK THRUST` | On 3 same-color bricks in a row, switch to wide RUNNER-style trail; otherwise behave like `RNK ON` |

**Within-brick scalping (the "stuck-in-bar" detection):**
- Track ticks-since-current-brick-open; display `Brick: 0:42` on dashboard.
- "STUCK" detection: no progress toward either side > N seconds AND tape balanced AND price oscillating in middle 50% of brick â†’ enable scalp mode.
- Scalp logic: place tight limit BUY near brick low and tight limit SELL near brick high; close on opposite side touch. Cancels both when brick finally closes.
- **Trap protection:** if a third reversal happens within the brick OR ATR spikes, kill scalp mode and revert to bar-mode trail.

**Trail in Renko mode:**
- SL = previous closed brick's far edge âˆ’ 1 tick.
- On trap detection (price reverses through brick mid) â†’ tighten to "current brick mid + 2 ticks".
- On thrust (3 same-color bricks) â†’ switch to RUNNER trail (1.5Ã—ATR, no aggression mults).

**Dashboard additions:**
- Brick state badge: `â–  â–  â– ` colored last 3 bricks.
- `Brick Time: m:ss` (since current brick open).
- `Renko Mode: OFF / ON / THRUST` indicator.

#### 19.6.A Case study â€” Apr 28, 2026 morning rally (the missed +96 pts)

**Reference chart:** `NQ 06-26 / nnZaRenko 64`, ~07:30 â†’ 08:55 ET. Big morning sell-off bottoms ~08:34 at ~27059, then a clean uptrend prints continuous green Renko bricks all the way to ~27155 by ~09:00.

**What v6 14.20 actually did (from `MmATM_v6_DiagLog_20260428.csv`):**

| Time | Close | Action | Bull | Bear | htfBias | Signal | Note |
|---|---|---|---|---|---|---|---|
| 08:34:09 | 27059.50 | EXIT_WIN_CLOSE | 0 | 0 | -1 | WAIT | Closed winning short âœ… |
| 08:34:42 | 27064.75 | EXIT_WIN_CLOSE | 0 | 0 | -1 | WAIT | Closed second winner âœ… |
| 08:35:30 | 27075.25 | SIGNAL | 10 | 20 | -1 | WAIT | Bottom in, but htfBias still âˆ’1 |
| 08:35:45 | 27083.25 | SIGNAL | **62.5** | 0 | 0 | WAIT | Bull conf strong, htfBias just flipped |
| 08:39:49 | 27087.75 | SIGNAL | **93.6** | 0 | 0 | **BUY** | Conf maxed |
| 08:40:00 | 27091.25 | SIGNAL | 57.5 | 0 | 0 | **STRONG_BUY** | Strongest moment |
| 08:40:14 | 27095.75 | SIGNAL | 60.0 | 0 | 0 | BUY |  |
| 08:43:46 | 27107.25 | SIGNAL | 55.5 | 0 | 0 | WAIT | Pullback w/ tape +0.61 |
| 08:47:04 | 27111.25 | SIGNAL | 52.3 | 0 | 0 | BUY |  |
| 08:48:49 | 27119.25 | SIGNAL | 45.5 | 0 | 0 | BUY |  |
| 08:51:32 | 27135.25 | SIGNAL | 45.0 | 0 | 0 | BUY |  |
| 08:57:33 | 27143.50 | SIGNAL | 35.0 | 0 | 0 | BUY |  |
| 09:00:47 | 27155.25 | SIGNAL | 45.5 | 0 | 0 | WAIT | Top of move |

**Result:** **Zero `ENTRY_LONG` rows in the entire window.** The bot watched +95.75 NQ pts â‰ˆ **+$1,915/contract** print without participating.

> âš ï¸ **Important context (user note Apr 28):** the 08:34 â†’ 09:00 window is **pre-RTH** (NQ regular session opens 09:30 ET). With **`HRS ON`** (the default), auto-entries are hard-blocked here regardless of signal quality. So the "miss" is only a miss in a parallel world where the user had toggled `HRS OFF` for the morning. The case study still stands as a profile of *what Renko thrust mode would do* once it is allowed to fire â€” the rally itself is the realistic, repeatable pattern.

> ðŸ¤ **Division of responsibility (confirmed Apr 28):**
> - **User decides WHEN to allow trading** â€” toggling `HRS ON/OFF` (and `AUTO/MANUAL`) is a deliberate human risk decision based on session context, news, account size, fatigue, etc. The bot does not auto-disable HRS.
> - **Strategy decides HOW to extract profit** â€” once HRS is OFF and AUTO is ON, the bot is responsible for entering smart, trailing intelligently against MM stop-hunts, and exiting only when structure breaks. No "I would have entered butâ€¦" excuses; the rules in Â§19.6.B must produce a real `THRUST_ENTRY` and ride the move with brick-edge trail.
>
> This split means Â§19.6 success is measured the day the user flips `HRS OFF` on a setup like Apr 28: did the bot pull the trigger, did it stay on, did it bank the move? That is the bar.

Likely *additional* blockers that would still have applied even with `HRS OFF`: htfBias still recovering from âˆ’1, post-win same-direction cooldown after closing winning shorts, EMA-cross-confirmation delay, `EXTENSION` filter as price ran from VWAP.

**Why Renko 64/16 + THRUST mode would have nailed this:**

- 16 ticks = **4 NQ pts per brick**. The 96-pt rally = ~24 consecutive green bricks.
- 3-brick thrust trigger fires by ~08:40 (around 27083 â†’ 27095). Even entering on the **3rd brick close** = ~27091, exit on opposite-color brick close (which never came until ~27155) = **~64 pts capture / +$1,280** with a single contract.
- Renko view collapses the choppy 1-min noise that kept `htfBias` and pullback filters whipsawing.
- THRUST trail sits at "previous brick far edge âˆ’ 1 tick" = always ~5â€“9 pts behind. No wide-stop drama, no aggressive-pullback exits.

#### 19.6.B Concrete profit recommendations (built from this case)

These are the **specific rules** to implement when we ship v6 14.26. Each is a numbered acceptance test we will run against the Apr 28 log.

1. **Thrust entry (independent of standard signal)** â€” when `RNK THRUST` mode is selected, allow entry on the **close of the 3rd consecutive same-color brick** even if the standard 12+5 entry filters block (htfBias mismatch, post-win cooldown, EMA-cross delay, extension). Rationale: standard filters are tuned for 1-min bars and reject the early phase of clean Renko thrusts. Override is gated by:
   - `MinThrustBricks = 3` (configurable, 2â€“4)
   - All 3 bricks closed within `ThrustMaxMinutes = 8` (avoid stale "thrust" from sleepy session)
   - ATR(30) > `MinAtrPointsToTrade` (still need volatility)
   - htfBias â‰  opposite (â‰¥ 0 for longs, â‰¤ 0 for shorts) â€” **0 is allowed** (this would have caught Apr 28)
   - Tape average over last 30s â‰¥ +0.15 for longs / â‰¤ âˆ’0.15 for shorts

2. **Brick-close trail (no time exit during thrust)** â€” once entered in THRUST, SL/trail = `previousBrickFarEdge âˆ’ 1 tick` (long) or `previousBrickFarEdge + 1 tick` (short). **Disable** all of: AGGR_PULLBACK, peak-giveback %, time-stop, void-bar exit. Only re-enable on first opposite-color brick close. Rationale: Apr 28 had multiple shallow pullbacks (`Bull` dropped 93â†’55â†’52â†’45) that would have triggered standard exits while Renko bricks stayed solid green.

3. **Continuation re-entry on pullback brick** â€” if a long position is exited by a single red brick but the **next brick prints green and closes above the red brick's high**, allow immediate re-entry (one re-try only, must be within `ReentryWindowMinutes = 4`). Counts as part of original trade for cooldown tracking. Rationale: clean Renko thrusts often print one "noise red" mid-rally; we should not be locked out for the rest of the move.

4. **Within-brick scalp (stuck-bar mode)** â€” only active when **NOT** in an open thrust position and **NOT** in `RNK THRUST` mode (i.e. `RNK ON` only). Conditions:
   - Current brick has been open â‰¥ `StuckSeconds = 90`
   - Tape average over last 30s in `[-0.15, +0.15]` (balanced)
   - Price oscillating in middle 50% of brick (touched both `low+25%` and `low+75%` at least twice)
   - ATR not spiking above prior 5-bar average Ã— 1.4
   
   Then place limit BUY at `brickLow + 1 tick` and limit SELL at `brickHigh âˆ’ 1 tick`, qty = 1 each. First fill activates a 6-pt fixed target and 8-pt fixed stop. **Hard kill switch:** if a third intra-brick reversal happens or brick finally breaks on momentum (ATR spike), cancel both legs and revert.

5. **Brick-edge dragon-stop (Renko-aware trail outside thrust)** â€” when in any open position and `RNK ON`, override the standard 4-tier trail with: **trail = previous closed brick's far edge âˆ’ 1 tick**, but only if it tightens (ratchets, never loosens). This replaces the existing AGGR_PULLBACK distance check with a structural one â€” MM cannot stop-hunt below a Renko brick boundary without reversing the brick.

6. **Thrust pause on doji-bricks** â€” if 2 consecutive bricks alternate color (G-R-G or R-G-R) within 3 minutes, mark thrust as **stalled** and revert to `RNK ON` rules. Don't exit, but stop counting bricks toward thrust streak.

7. **PACE integration (when both Item 4 + Item 6 ON):**
   - `ðŸ¢ SLOW`: don't fire thrust entries (low ATR rallies fizzle).
   - `ðŸŸ¢ SAFE` / `ðŸŸ¡ AGGRESSIVE`: full thrust rules.
   - `ðŸ”¥ VOLATILE`: tighten thrust trail to `previousBrickFarEdge` (no extra tick of slack).

8. **Diag log additions for Renko mode:**
   - New columns: `BrickColor` (G/R), `BrickAgeSec`, `ThrustStreak` (consecutive same-color count), `RenkoMode` (OFF/ON/THRUST).
   - New `Action` rows: `BRICK_CLOSE` (Detail = `color=G hi=27091.25 lo=27087.25 streak=3`), `THRUST_ENTRY`, `THRUST_TRAIL_RATCHET`, `THRUST_STALL`, `BRICK_REENTRY`, `STUCK_SCALP_PLACE`, `STUCK_SCALP_FILL`, `STUCK_SCALP_KILL`.

**Acceptance test for v6 14.26 (run before declaring done):** Replay Apr 28, 2026 with `RNK THRUST` mode + thrust override ON. Must produce a `THRUST_ENTRY` row at the close of the 3rd green brick following the 08:34 bottom, hold (no AGGR_PULLBACK exit) until first red brick after the 09:00 top, and book â‰¥ **+50 NQ pts** ($1,000) realized on the move.

#### 19.6.C Implementation risk notes

- `AddRenko()` requires `Bars` index handling: any reference inside `OnBarUpdate` must guard with `if (BarsInProgress != 0) { /* update Renko state only */ return; }` or trades will fire on Renko closes.
- Brick close detection: use `BarsArray[2].LastPrice` change between `IsFirstTickOfBar` events on `BarsInProgress == 2`.
- Performance: Renko is `OnPriceChange`-equivalent â€” keep brick-state updates O(1).
- **Don't** route entry orders through the Renko series; always use primary `BarsArray[0]` for entry/exit so price ladders are correct.
- Save `RenkoBrickTicks` / `RenkoOffsetTicks` as user-properties so testers can experiment with 32/8 (faster) and 96/24 (slower) without recompile.
- **HRS interaction:** thrust override does **not** bypass the `HRS ON` time-window filter. If the user wants to capture pre-RTH thrusts (like Apr 28's 08:34 rally), they must explicitly toggle `HRS OFF` first. We may later add an opt-in `THRUST_BYPASS_HRS` sub-toggle (default OFF) for users who specifically want overnight/pre-market thrust trading â€” flagged as risky and logged separately.

### 19.7 Carry-over deferred items (already in v6 roadmap Â§18)

These were considered for v5 but bumped to v6 per user decision:
- **Item 2 / Stealth Mode** â€” hide bot-tell labels in execution `Name` column. Requires unmanaged-orders refactor (~200 lines). Stays as v6 feature K.
- Pattern-Memory Engine, MM Playbook Detector, per-hour self-tuning, Kelly sizing, etc. â€” all v6.

### 19.8 Working agreement

- One item per session, committed individually with message `v6 0.<phase>.<patch>: <description>`.
- Each item ships behind a toggle (where applicable), default OFF unless user explicitly opts to default ON.
- Compile check (`get_errors`) before every commit.
- After commit, user reloads strategy in NT8 (F5) and tests. We move on only after user confirms.
- Diag log for each new feature: at least one `ACTION` row per state change, with enough Detail to post-mortem.
- Spec doc updated at the end of each implementation session.

### 19.9 v6 Changelog

| Version | Date | Scope | Notes |
|---|---|---|---|
| v6 0.0.0 | 2026-04-28 | Fork from v5 14.20 | Class/identifier rename to `Mm_ATM_v6`; v5 frozen for live trading |
| v6 0.1.1 | 2026-04-28 | Phase 0 â€” diag log MM-analysis enhancement (no behavior change) | +15 columns (54 total). See Â§19.10 |
| v6 0.1.2 | 2026-04-28 | Phase 0.1 â€” TP/SL line render race fix + nudge diag rows | (1) `RequestSlTpResize`/`RequestSlNudgePoints` no longer dispatch a stale Draw before the resize/nudge runs (was rendering OLD `hiddenTargetPrice`/`hiddenStopPrice`). (2) `ProcessPendingButtons` now calls `RedrawAnnotationsSafe()` immediately after `ArmHiddenStops`/`ResizeHiddenStops`/`NudgeSlPricePoints` so the chart shows the new line within the SAME tick the action ran. (3) `OnOrderUpdate` Filled path now calls `RedrawAnnotationsSafe()` right after `ArmHiddenStops()` â€” TP/SL lines appear synchronously with the fill instead of waiting for the next tick. (4) New `RedrawAnnotationsSafe()` helper wraps `DrawChartAnnotations` in try/catch + `CurrentBar<1` guard. (5) New diag verbs `STOPS_ARMED`, `TP_NUDGE`, `SL_NUDGE` for post-mortem of every line move. |
| v6 0.2.0 | 2026-04-28 | Phase 0.2 â€” NinzaRenko 64/16 secondary series plumbing | (1) `AddDataSeries(BarsPeriodType.Renko, Value=64, Value2=16)` adds `BarsArray[2]` in `OnStateChange.Configure`. (2) New `ProcessRenkoBar()` runs in `BarsInProgress==2`, tracks `lastBrickColor` (G/R) and `brickStreakCount` from prior closed brick (`Closes[2][1]` vs `Opens[2][1]`). (3) Diag log `BrickColor`/`BrickStreak` columns now show real values; new `BRICK_CLOSE` verb logs every brick with `o/c/hi/lo`. (4) `WriteDiagRow` hardened to use explicit primary-series indices (`Closes[0][0]`, `Times[0][0]`, `CurrentBars[0]`) â€” safe to call from any BIP. (5) New properties group **9 - Renko**: `EnableRenkoSeries` (default ON), `RenkoBrickSize` (64), `RenkoBrickOffset` (16). Defensive: legacy templates loading with `<4`/`<0` are auto-corrected. **No entry/exit logic uses Renko yet** â€” that ships in Phase 2.1 (W6). |
| v6 0.2.1 | 2026-04-28 | Phase 0.2 polish â€” read-only brick status row on dashboard | New `lblBrickInfo` between Hours and Quick-Action buttons. Reads `lastBrickColor` + `brickStreakCount` snapshots already maintained by `ProcessRenkoBar` â€” zero new computation. Format: `Brick: GREEN Ã—3  (64/16)`, color matches brick (LimeGreen/OrangeRed/Gray). Shows `Brick: off` when `EnableRenkoSeries=false`. Renko panel-properties intentionally NOT exposed as dashboard buttons because `AddDataSeries` is locked at `State.Configure` (a runtime change would silently no-op or force a reload that kills the open position). The runtime `RNK: OFF/ON/THRUST` mode toggle ships with W6 in Phase 2.1. |
| v6 0.2.2 | 2026-04-28 | Phase 0.2 fix â€” Renko brick processor reliability | User reported brick row stuck on gray "Brick: â€”" even with `EnableRenkoSeries=true` and visibly green Renko bricks. Root cause: `IsFirstTickOfBar` on a secondary series under `Calculate.OnBarClose` is unreliable â€” can stay false on the first bar after each strategy enable, and may not retrigger consistently. Fix: replaced `IsFirstTickOfBar` gate with explicit `lastProcessedRenkoBar` int dedupe (skip if `CurrentBars[2]==lastSeen`). Now processes every new brick regardless of tick-mode quirks. Also added one-shot `Print(TAG + "Renko series ALIVE â€¦")` on first brick so user can confirm in NT8 Output window that `BarsArray[2]` is wired. Hardened color streak: only increments when prior color was non-empty (previously `""==""` would inflate streak across blank starts). |

### 19.10 Diag log MM-analysis schema (v6 0.1.1)

> **Goal:** capture every parameter needed to reverse-engineer MM behavior, validate future v6 features against historical sessions, and feed pattern-memory + MM-trap detection without recompiling. Default ON. File: `~/Documents/NinjaTrader 8/MmATM_v6_DiagLog_YYYYMMDD.csv`.

#### Full column list (54)

**Block 1 â€” Bar / indicator state (16 cols)** *[unchanged from v14.20]*

`DateTime, Bar, Close, VWAP, EmaF, EmaS, RSI, ATR, ADX, RawBull, RawBear, Bull, Bear, Tape, htfBias, trapScore`

**Block 2 â€” Position / risk state (11 cols)** *[unchanged]*

`Pos, Qty, AvgEntry, HiddenSL, HiddenTP, TrailPx, TrailTier, ManualOff, ConsecLoss, ConsecWin, DailyPnL`

**Block 3 â€” Configuration & decision context (10 cols)** *[unchanged from v14.20]*

`Mode, AutoStrat, Toggles, Signal, MinConf, DistVwapAtr, VoidBars, OpenType, ProfitPts, PeakPts`

**Block 4 â€” MM-analysis NEW (15 cols, v6 0.1.1)**

| Column | Source | Why for MM analysis |
|---|---|---|
| `Bid` | `GetCurrentBid(0)` (live only, 0 in replay) | Tick-level bid for spread + slippage post-mortem |
| `Ask` | `GetCurrentAsk(0)` (live only, 0 in replay) | Tick-level ask |
| `SpreadTk` | `(Askâˆ’Bid)/TickSize` rounded | **MM tell #1** â€” MM widens spread to fade retail; spread spikes correlate with stop-runs and ladder-pulls |
| `TapeBuyVol` | `tapeAskVol` (rolling 30s) | Aggressor BUY volume â€” separated from delta so we can see *who* is hitting the offer |
| `TapeSellVol` | `tapeBidVol` (rolling 30s) | Aggressor SELL volume |
| `EmaStack` | `+1` if `Close > EmaF > EmaS`, `âˆ’1` inverse, `0` mixed | Fast trend proxy used by F4 until F1 Regime ships; matches the "EMA-stack" gate already in entry filters |
| `AdxSlope` | `ADX[0] âˆ’ ADX[5]` | Trend acceleration (rising = trend strengthening). Captures the "ADX 9â†’18 chop" signature flagged in v5 Â§16 |
| `DistPocPts` | `(Close âˆ’ pocLevel) / pt`, signed (+ above) | Distance to developing-day POC. Seeds **F2 POC/VA aware range trading** |
| `DistVahPts` | `(Close âˆ’ vahLevel) / pt` | Distance to Value Area High |
| `DistValPts` | `(Close âˆ’ valLevel) / pt` | Distance to Value Area Low |
| `DistPdHiPts` | `(Close âˆ’ prevDayHigh) / pt` | Distance to prior-day high â€” **the level MM defends most**; prior-day-clamp logic uses this |
| `DistPdLoPts` | `(Close âˆ’ prevDayLow) / pt` | Distance to prior-day low |
| `BrickColor` | `"G" / "R" / ""` | Renko 64/16 brick color. Empty until **W6 Phase 0.2** plumbing wires the secondary series |
| `BrickStreak` | int | Consecutive same-color brick count. Empty/0 until W6 Phase 0.2 |
| `Regime` | `"" / "TREND_UP" / "TREND_DN" / "RANGE" / "TRANSITION" / "VOLATILE"` | F1 Regime classifier output. Empty until **F1 Phase 1.1** |

**Block 5 â€” Event (2 cols)** *[unchanged]*

`Action, Detail`

#### Action verbs already in use (Â§14, Â§16, Â§17 evidence) â€” preserved unchanged

`SIGNAL Â· ENTRY_LONG Â· ENTRY_SHORT Â· EXIT_WIN_TP Â· EXIT_WIN_BE Â· EXIT_WIN_TRAIL_<tier> Â· EXIT_WIN_CLOSE Â· EXIT_LOSS_SL Â· EXIT_LOSS_TRAIL Â· EXIT_AGGR_PULLBACK Â· EXIT_REVERSAL Â· EXIT_TIMESTOP Â· EXIT_VOID Â· EXIT_MANUAL Â· BLOCK_<reason> Â· TOGGLE_<feature> Â· MODE_CHANGE Â· HEARTBEAT Â· TP_NUDGE Â· SL_NUDGE`

#### Action verbs queued for new phases

| Phase | New verb | When fired |
|---|---|---|
| 0.2 | `BRICK_CLOSE` | New Renko brick prints. Detail: `color=G hi=27091.25 lo=27087.25 streak=3` |
| 1.1 | `REGIME_CHANGE` | F1 classifier flips state. Detail: `from=TREND_DN to=TRANSITION reason=brick_alt_3` |
| 2.1 | `THRUST_ENTRY Â· THRUST_TRAIL_RATCHET Â· THRUST_STALL Â· BRICK_REENTRY Â· STUCK_SCALP_PLACE Â· STUCK_SCALP_FILL Â· STUCK_SCALP_KILL` | W6 Renko mode events |
| 3.1 | `MM_TRAP_DETECTED Â· MM_TRAP_COUNTER_ENTRY` | F5 MM-Trap detector |
| 3.2 | `LEVEL_TOUCH Â· POC_LEVELS` | F2 POC/VA range trader |

#### Post-mortem queries unlocked by the new schema

| Question | Filter expression |
|---|---|
| "Did MM widen spread before stopping us out?" | `SpreadTk > median(SpreadTk last 5min) * 2` in 60s before any `EXIT_LOSS_*` |
| "Were we trading INTO a major level when we lost?" | `Action="EXIT_LOSS_SL" AND ABS(DistPdHiPts) < 5` (or PdLo / Poc / Vah / Val) |
| "Did htfBias carry over from prior trend at loss time?" | (use new `Regime` once F1 ships) `Action="EXIT_LOSS_*" AND Regime="RANGE" AND htfBias != 0` |
| "Are we entering on the right side of EMA stack?" | `Action="ENTRY_LONG" AND EmaStack < 1` â†’ suspect filter weakness |
| "When does ADX rise predict our wins?" | `AdxSlope > 3 AND Action LIKE "EXIT_WIN_*"` vs `AdxSlope < -3 AND Action LIKE "EXIT_WIN_*"` correlation |
| "How often did POC act as MM defense (rejection)?" | Count price excursions: `cross(Close, pocLevel)` followed by re-cross within 3 bars |
| "Daily volume-imbalance regime?" | `SUM(TapeBuyVol)/SUM(TapeBuyVol+TapeSellVol)` per hour |

#### Notes for downstream tooling

- All numeric distances are in **NQ points** (1 pt = 4 ticks = $20).
- Live-only fields (`Bid`, `Ask`, `SpreadTk`, `TapeBuyVol`, `TapeSellVol`) are 0 in Strategy Analyzer / historical replay â€” do not interpret as "tight spread / no flow"; check `Mode != "BACKTEST"` if added later.
- The 39â†’54 column expansion is **append-only** at the end-of-row (before `Action,Detail`), so any existing CSV-importer that reads by column name still works; importers that read by index need the new schema.
- Header is rewritten only when log file is rotated daily; if you change column count mid-session you must delete the day's CSV and let the strategy re-create it.

---

### 19.11 F9 â€” Intelligent MM-Aware Trail v2 (Phase 3.5, queued)

> **Origin:** user observation (Apr 28, 2026) â€” *"the key to be profitable is to have a very intelligent trail. It has to analyze the price in tick level and determine if it should leave room for a little go in profit or decide if the MM is trapping. We need to review logs and build a very smart trail depending on market condition and MM traps being happening. We need to review this later as part of enhancements without breaking the code and making it worse."*

**Why it's queued for Phase 3.5 instead of done now:** an intelligent trail is *only* as good as the inputs that classify the market state. F9 reads:

- **Regime** (F1, Phase 1.1) â€” TREND vs RANGE vs TRANSITION drive completely different trail behavior.
- **MM-Trap detector** (F5, Phase 3.1) â€” when a stop-run is in progress, the trail must NOT tighten (that's exactly what MM is hunting).
- **Brick streak** (Phase 0.2 â€” already plumbed) â€” a 5-brick green streak earns more breathing room than a 1-brick blip.
- **POC/VA distance** (Phase 0.1 diag â€” already plumbed) â€” at MM-defended levels, trail tighter; in open air, trail looser.
- **Spread + TapeBuy/Sell** (Phase 0.1 diag â€” already plumbed) â€” widening spread + lopsided aggressor = MM about to fade; tighten or exit.

Building F9 today would mean hard-coding heuristics. Building F9 *after* Phases 0â€“3 means it can read all those signals as first-class inputs.

#### Design constraints (per user)

1. **Tick-level decisions, not bar-level.** Today's trail engine fires on `IsFirstTickOfBar` for tier promotion; F9 must run inside `MonitorAdaptiveTrail` on every tick when in trade.
2. **Asymmetric "give room vs lock profit"** â€” the trail must *recognize* the difference between healthy pullback (give room) and MM trap (lock or exit).
3. **No regression on the v5/v6 trail engine.** F9 ships as `intelligentTrailMode` toggle, default OFF. The existing 4-tier trail (T1-BE, T2-Strong, T3-Runner, Aggressive) stays the default code path. F9 is an *additional* tier set the user opts into.
4. **Self-tuning from logs, not from preset thresholds.** F9 reads the `~/Documents/NinjaTrader 8/MmATM_v6_DiagLog_*.csv` files (last 30 days) on `State.DataLoaded` and computes per-regime / per-hour / per-MM-pattern trail-tightness multipliers. Falls back to safe defaults if no logs.
5. **Reversibility.** Every F9 trail decision writes a `TRAIL_F9` diag row with `regime=â€¦ trap=â€¦ brickStreak=â€¦ spread=â€¦ distPoc=â€¦ decision=GIVE_ROOM/HOLD/TIGHTEN/EXIT_NOW reason=â€¦` so post-mortems are trivial.

#### Decision tree (preliminary â€” to be refined from logs)

```
on every tick when in trade && profit > 1pt:
  classify state with (Regime, MM-Trap, BrickStreak, SpreadTk, DistPoc, AdxSlope, ProfitPts)

  if MM_TRAP_DETECTED && profitPts >= 2:
       EXIT_NOW (lock what we have â€” F5 already does this; F9 just confirms)
  elif Regime in (TREND_UP/TREND_DN, agree with dir) && BrickStreak >= 3:
       GIVE_ROOM  (loosen trail to max(2Ã— ATR, brick-back))
  elif Regime == RANGE && |DistPoc| < 5:
       TIGHTEN    (POC magnetism â†’ MM defends; tight trail at 0.7Ã— ATR)
  elif SpreadTk > 2Ã— rolling_median(SpreadTk, 5min) && profitPts >= 5:
       TIGHTEN    (spread widening = MM about to fade)
  elif TapeBuy/TapeSell ratio reverses against trade dir for 3 consecutive ticks:
       TIGHTEN    (aggressor flow reversed â€” first warning)
  elif AdxSlope > +3 && Regime in (TREND, dir agrees):
       HOLD       (trend strengthening â€” let it run, no change)
  else:
       use default v5/v6 trail tier (no F9 override)
```

#### Implementation gates

- **Cannot start F9 before Phase 3.1 (F5) is complete** â€” half the inputs don't exist yet.
- **Must replay at least 4 weeks of v6 0.1.x+ diag logs** before tuning F9 thresholds. Phase 0â€“2 produces this dataset as a side effect.
- **Backtest harness:** F9 runs in shadow mode for 1 week (writes `TRAIL_F9_SHADOW` decisions but doesn't execute) before flipping to live. Compare shadow decisions vs actual exits to validate.

#### Acceptance test (must pass before declaring done)

Replay the worst v5 trail failures from the existing log corpus:

1. **Apr 27 stop-runs** (3Ã— identical AGGR_PULLBACK at peak=16/cur=âˆ’12/pb=28) â€” F9 must produce `EXIT_NOW` at peak â‰¥10pt instead of the âˆ’28pt stop-out.
2. **Apr 28 morning rally** (missed +96pt) â€” F9 in TREND_UP + BrickStreak â‰¥3 must produce `GIVE_ROOM`, allowing the trade to ride past the 16pt peak that v5 trail clipped.
3. **Apr 28 10:30 short** (âˆ’$380) â€” F9 must produce `TIGHTEN` or `EXIT_NOW` when SpreadTk widens 2Ã— and TapeBuy reverses, capping the loss before âˆ’19pt.

If any of the three fails, F9 stays default-OFF and we iterate before promoting.

---

## 18. v5 Stability Lock & v6 Roadmap â€” "Make MM open their mouth"

> **Status (Apr 27, 2026):** v6 14.19 is the **stable production line**. No more invasive changes go into v5 â€” only bug fixes and parameter tuning. All structural ideas below are deferred to **`Mm_ATM_v6.cs`** (new file, fork of v6 14.19).

### 18.1 Why fork to v6 instead of patching v5

v5 has earned its place: hidden SL/TP, smart trail with 4 tiers, sweep boost, runner mode, 5 soft blockers + 12 hard blockers, manual signal panel, tape integration, dashboard with live diag CSV. The +$1,710 day on 2026-03-20 proved the architecture works. Adding more layers risks regression. v6 lets us experiment safely while keeping v5 trading live.

### 18.2 v6 design pillars

1. **Adaptive over fixed** â€” replace hard-coded thresholds with rolling-window self-tuning (per-hour, per-regime).
2. **Multi-timeframe truth** â€” promote 5-min and 15-min from "filter inputs" to first-class signal sources with their own confidence.
3. **Pattern memory** â€” record what worked / what failed by setup signature (open-type Ã— hour Ã— ATR-bucket Ã— HTF) and adjust min-confidence per signature.
4. **MM behavioral modeling** â€” explicitly model the MM playbook (stop-run levels, ladder pulls, fade-the-breakout) and trade *against* the predicted MM action, not just the chart.
5. **Risk-aware position sizing** â€” Kelly-like fraction based on rolling win-rate Ã— R:R, not fixed contracts.

### 18.3 Concrete features queued for v6

#### A. Pattern-Memory Engine (priority 1)
- Record every trade with a **setup fingerprint**: `(hour, openType, atrBucket, htfBias, sweepPresent, vwapDistAtr, dirAgreesEma)`.
- Build a rolling 90-day P&L heat-map per fingerprint.
- At entry time, look up the fingerprint and **scale the size** (or block entirely) based on its historical edge.
- Diag tag: `PATTERN_LOOKUP fingerprint=... histPnL=... n=... action=BOOST/NORMAL/REDUCE/BLOCK`.
- Persist as JSON in `~/Documents/NinjaTrader 8/MmATM_v6_PatternStore.json` so it survives restarts.

#### B. Per-Hour Self-Tuning (priority 1)
- Rolling 30-day **per-hour** win-rate, avg R, expectancy.
- Auto-adjusts `MinSignalConfidence` per hour: e.g. midday low-edge hour gets +10 confidence requirement, opening 30-min gets âˆ’5.
- Auto-adjusts `ExtensionMaxAtrFromVwap` per hour (volatile hours allow more extension).
- Heat-map tile on dashboard showing each hour's win-rate as color intensity.

#### C. MM Playbook Detector (priority 1 â€” "the open mouth feature")
A separate scoring engine, parallel to confidence, that predicts the next MM move:
- **Stop-run setup**: equal lows/highs for N bars + tape balanced + low ADX = MM is preparing to sweep. Pre-arm a fade entry on the sweep tick.
- **Ladder pull**: sudden book thinning (would need L2 if NinjaTrader allows, otherwise tape compression) before a violent move = MM stepping aside. Block entries.
- **Squeeze trap**: BB width minimum + RSI mid + low volume = MM coiling for an expansion. Don't fade either side; trade the breakout direction once tape confirms.
- **Liquidity grab**: prev-day H/L + first 2 hours + low conviction tape = MM grab + reversal expected. Pre-arm reversal signal.
- Outputs: `mmIntent âˆˆ {NEUTRAL, STOP_RUN_BULL, STOP_RUN_BEAR, LADDER_PULL, SQUEEZE_BUILD, GRAB_REVERSAL}` displayed on dashboard.
- Trades are biased toward *fading* the predicted MM action.

#### D. Multi-Timeframe Confidence Stack
- Promote 5-min and 15-min into independent confidence calculators (re-use the F1â€“F12 pipeline per TF).
- Final entry confidence = weighted: `0.5Ã—conf1m + 0.3Ã—conf5m + 0.2Ã—conf15m`.
- "STRONG" upgrade requires all 3 TFs agree on direction.
- Dashboard shows 3 confidence bars stacked (1m / 5m / 15m).

#### E. Adaptive Risk-Per-Trade (Kelly-fraction sizing)
- Track rolling 30-day expectancy `E = winRate Ã— avgWin âˆ’ lossRate Ã— avgLoss`.
- Kelly fraction `f = E / avgWinÂ²` (half-Kelly for safety).
- Translate to contracts via account equity. Caps: `MinContracts = 1`, `MaxContracts = configurable`.
- Auto-reduce by 50% after `consecutiveLosses â‰¥ 3` (already partially done in v5 with DCA suppression).

#### F. Fib + Candle Reversal Scalp Module (was deferred from v14.20)
- Auto-draw fib retracement on the day's developing move.
- Detect candlestick reversal patterns (engulf, hammer, doji-with-tail) at 38.2 / 50 / 61.8 levels.
- Independent toggle, independent risk pool. Fires only when main strategy is WAIT.

#### G. V-Fakeout Filter (the 2026-03-25 lesson)
- Detect: sharp move â†’ flat 3-bar consolidation at extreme â†’ sudden reverse engulf.
- Block entries on the second leg of a V-shape until 5 bars of trend confirmation post-reverse.
- Diag tag: `BLOCK_V_FAKEOUT`.

#### H. Spread / Slippage Awareness
- Live spread monitor on dashboard (`Spread: 1.25t`).
- If spread > NÃ—typical â†’ block entries (`BLOCK_SPREAD_WIDE`).
- Track per-trade slippage (fill vs signal price); if rolling slippage worsens â†’ tighten activation thresholds.

#### I. Strategy Analyzer Walk-Forward Harness
- Built-in framework to run v6 over rolling 30-day windows, log Sharpe / max DD / expectancy per window.
- Auto-promote the best parameter set to live (with manual confirm).

#### J. Dashboard v2
- Move heavy WPF rendering to a single `CompositionTarget.Rendering` tick (currently scattered Dispatcher.InvokeAsync calls).
- Add: per-hour heat-map, pattern-fingerprint last 5 outcomes, MM intent badge, Kelly-suggested size, live spread + slippage.
- Save dashboard size + position to user settings (right now it resets).

### 18.4 v6 architecture changes

- **Fork**: copy `Mm_ATM_v6.cs` â†’ `Mm_ATM_v6.cs`, rename class, bump display name.
- **Split into partials**: `Mm_ATM_v6.Core.cs`, `.Signals.cs`, `.Trail.cs`, `.PatternMemory.cs`, `.MMPlaybook.cs`, `.Dashboard.cs`. The 4600-line monolith is fighting us.
- **Persistence layer**: `MmATM_v6_PatternStore.json` for pattern memory, `MmATM_v6_HourStats.json` for per-hour stats. Load on `State.DataLoaded`, save on flatten + on shutdown.
- **Diag CSV v2**: extend to ~40 columns to include MM intent, pattern fingerprint, Kelly size, spread.
- **Backtest mode**: a `SimulateTape` flag that synthesizes a tape-delta proxy from bar OHLCV when not in live mode (so tape-dependent logic still runs in Strategy Analyzer).

### 18.5 Migration plan (v5 â†’ v6)

1. **Freeze v6 14.19** â€” only bug-fix commits (e.g. `v6 14.19.1`) for production.
2. Open `ninja-v6` branch off current `ninja`.
3. Fork file, split into partials, get it compiling identical to v6 14.19 â†’ tag `v6 0.1 - parity`.
4. Implement features in priority order: B (per-hour) â†’ A (pattern memory) â†’ C (MM playbook) â†’ G (V-fakeout) â†’ H (spread) â†’ D (multi-TF) â†’ E (Kelly) â†’ F (Fib scalp) â†’ J (dashboard v2) â†’ I (walk-forward).
5. Each feature ships behind its own toggle, default OFF. Promote to default ON only after a 5-day live A/B vs v5.

### 18.6 What "make MM open their mouth" looks like

- **MM intent badge** on dashboard: "MM PROBE STOP-RUN BULL @ 18242.50 â€” fade ready" (you click the dashed level, strategy primes a long limit just below).
- **Pattern lookup popup** on every entry: "This setup: 14 prior occurrences, 71% win, avg +$240" (or "0 prior, no edge data â€” passing").
- **Hourly heat-map**: instantly see your worst hour and either flatten through it or boost confidence requirement.
- **Kelly badge**: "Suggested 2 contracts (rolling expectancy $185, half-Kelly)" instead of fixed sizing.
- **Walk-forward report** every Sunday: "Last 30 days: Sharpe 1.8, max DD $1,420, 64% wins. Suggested change: lower MinSignalConfidence to 53 (validated +$640 over baseline)."

### 18.7 Out of scope (won't pursue in v6)

- Machine-learning models â€” explicit rules > opaque models for trading; we can read a rule and override it. ML stays a research toy.
- Multi-instrument trading â€” one symbol at a time keeps the dashboard sane and risk obvious.
- Auto-news scraping â€” keep the manual news-blackout window, scraping adds fragility.
- Cloud / remote dashboard â€” local-only for security and simplicity.

### 18.8 v6 enhancements derived from Apr 28, 2026 NinzaRenko 64/16 study

> **Source data:** `MmATM_v6_DiagLog_20260428.csv` + two NinzaRenko 64-tick / 16-offset chart screenshots (one wide-angle showing the ~400-pt overnight downtrend, one zoom showing the 9:45 AM â†’ 12:50 PM range).

#### 18.8.A The two regimes seen in one day

| Regime | Window | Range / Move | Renko look | What v5 did |
|---|---|---|---|---|
| **Trend (overnight down)** | 00:00 â†’ 08:34 ET | High 27439 â†’ Low 27038 = **âˆ’401 pts** | Long stretches of solid red bricks, brief 3-brick green pullbacks that immediately re-fail | 174 SIGNAL rows, only **8 STRONG_SELL**; no entries (HRS ON pre-RTH). 2 small trail-T1-BE wins on a manual setup at the bottom |
| **Range (RTH morning)** | 09:45 â†’ 12:50 ET | High ~27210 â†’ Low ~27050 â‰ˆ **160 pts**, multiple 50-pt swings | Alternating 4â€“8 brick red/green legs with sharp pivots near range edges | 245 SIGNAL rows, heavy SELL bias (90 SELL + 16 STRONG_SELL vs 32 BUY + 6 STRONG_BUY) â€” **carryover bias from prior trend**. Mixed P&L: +$525 daily but with a âˆ’$380 loss at 10:30 going short into a bottom |

**Two distinct edges to capture, each needs different machinery.**

#### 18.8.B Findings the data exposed

1. **STRONG signals are too rare in real trends.** 8 STRONG_SELL across an 8-hour 400-pt downtrend = the threshold is gating us out of the meat. The current `dom â‰¥ 75` floor for STRONG was tuned in chop; in a real Renko-confirmed trend, `dom â‰¥ 60` + 3-brick alignment is plenty.

2. **Signal carryover into the range was costly.** After the overnight trend bottomed at 08:34, the BUY/SELL counter stayed SELL-heavy for the next 3 hours (90 vs 32). The 10:30 SHORT entry that lost âˆ’19 pts happened *because* we were still trading the dead trend's bias. Renko rotation (redâ†’greenâ†’redâ†’green at range edges) screamed "regime change" but our 1-min EMAs hadn't caught up.

3. **Range pivots clustered at predictable levels** â€” eyeballing the second chart, ~27200 / ~27160 / ~27100 / ~27050 acted as repeated pivots. These are very likely **prior-day POCs / value-area edges / overnight high-low** â€” exactly what MM defends and runs.

4. **Trail T4-Big won big once (10:35 â†’ 10:36 = +29 pts)** but it took a winning condition (signal aligned + sharp move) that the existing trail handled fine. The losers came when **regime mis-identification** put us on the wrong side, not when trail logic failed.

#### 18.8.C v6 features proposed (priority-ordered for "beat the MM")

> Each feature lists its **edge hypothesis**, **rule sketch**, and **measurement** so we can validate after 2 live weeks.

##### F1. Regime Classifier (the brain v5 lacks)

- **Edge:** stop trading SELL signals just because EMAs lag a regime change. Each tick, classify the current regime as one of: `TREND_UP / TREND_DN / RANGE / TRANSITION / VOLATILE`. Every other v6 feature is gated by regime.
- **Rule:** combine 5 inputs into a single state machine (Renko brick streak â‰¥ 5 same color â†’ TREND_*, alternation â‰¤ 3 with bounded range over last 30 bricks â†’ RANGE, ATR > 1.5Ã— 5-day-avg â†’ VOLATILE, regime *change* sustained 3 bricks â†’ TRANSITION).
- **Diag:** new `Regime` column, `REGIME_CHANGE` action row, regime-change badge on dashboard.
- **Measure:** count "wrong-side" entries (SELL inside TREND_UP or after a confirmed regime flip) â€” must drop â‰¥ 70% vs Apr 28 baseline.

##### F2. POC / Value-Area Aware Range Trading

- **Edge:** the chart's 27200 / 27160 / 27100 / 27050 pivots are not random. Identify them and stop fighting them.
- **Rule:** at session start, compute and store **prior-day POC, VAH, VAL, ON-high, ON-low, current-day developing POC**. Render them as horizontal lines on the chart (color-coded). When in `RANGE` regime, only allow:
  - Long entries within `(POC Â± 2 ticks) â†’ VAL` zone with bullish trigger.
  - Short entries within `(POC Â± 2 ticks) â†’ VAH` zone with bearish trigger.
  - Block any entry if price has just **broken** a level (wait for retest or regime flip).
- **MM-counter logic:** if price runs through ON-high/low, watch for a **failure-to-extend brick** (next brick fails to print same color beyond the level) â†’ trade the reversal back to POC. This is the "MM stop-run trap."
- **Diag:** `POC_LEVELS` row at session start, `LEVEL_TOUCH` rows on each pivot interaction, new dashboard mini-panel showing distance-to-nearest-level.
- **Measure:** range-day P&L vs current; aim for â‰¥ +$300 on a flat 100-pt range day (Apr 28 RTH was +$525 with a loss; should be +$800+ done cleanly).

##### F3. Renko Trend-Riding Mode (extends Â§19.6 thrust into v6)

- **Edge:** the v6 14.26 thrust mode catches the entry; v6 must scale the win.
- **Rule additions on top of v6 14.26:**
  - **Pyramid up to 3 contracts** in a confirmed `TREND_*` regime: add 1 contract on every additional 3-brick continuation past entry, each add gets its own brick-edge trail.
  - **Trail tightens by tier:** original entry rides far brick edge, add-1 rides previous brick edge, add-2 rides current brick mid â†’ forces realization on first reversal while keeping core position alive.
  - **Auto-reload on continuation:** if final position exits but next brick reasserts trend within `ContinuationWindow=2 bricks`, re-enter at half size.
- **Measure:** a 24-brick (96-pt) Renko trend like the missed Apr 28 morning rally should produce â‰¥ +$2,000 per contract base + pyramid uplift.

##### F4. Adaptive Signal Confidence (per-regime)

- **Edge:** STRONG threshold of 75 is wrong for trends and right for chop. Make it regime-aware.
- **Rule:** keep `MinSignalConfidence` as base, multiply by regime factor:
  - `TREND_*`: Ã— 0.75 (lower bar = enter earlier in confirmed trends)
  - `RANGE`: Ã— 1.10 (higher bar = avoid chop whipsaws)
  - `VOLATILE`: Ã— 1.30 (very high bar = only crystal setups)
  - `TRANSITION`: Ã— 2.0 (effectively block until regime confirmed)
- **Diag:** `EffectiveMinConf` column logs the post-regime value every signal row.
- **Measure:** trend-regime entries per hour should ~triple vs Apr 28; range-regime false-entries should halve.

##### F5. MM-Trap Detector (the active counter-attack)

- **Edge:** Â§19.6.A's Apr 27 finding (3 identical AGGR_PULLBACK exits at peak=16/cur=âˆ’12) is a textbook MM signature. Detect it and invert.
- **Rule:** maintain a rolling 5-trade buffer of `(entryPx, peakPx, exitPx, exitReason, sideTaken)`. When **3 of last 5 same-side trades stop-out within Â±2 ticks of the same level after similar peak excursion**, flip a `MM_TRAP_DETECTED` flag for the rest of the session on that side. Behavior:
  - Block further same-side entries near that level for 30 min.
  - On the next bounce off that level in the **opposite** direction, allow a counter-entry with `SignalConfidence Ã— 0.5` (because the MM hand is tipped).
- **Diag:** `MM_TRAP_DETECTED level=27087 side=SHORT count=3` row.
- **Measure:** each trap detection should produce â‰¥ 1 follow-up counter-trade with positive expectancy averaged over 90 days.

##### F6. Continuation Re-Entry After Range Break

- **Edge:** when range finally breaks, v5 was still in "WAIT" because EMAs hadn't crossed. Brick close beyond range edge IS the trigger.
- **Rule:** when in `RANGE` regime and Renko brick **closes beyond range edge by â‰¥ 1 brick**, immediately flip to `TRANSITION` then `TREND_*` if 2 more confirming bricks print. Allow entry on the 2nd post-break brick close, no extra signal needed.
- **Measure:** range-break captures should average â‰¥ 30 pts on NQ within 15 min of break.

##### F7. Position-State Memory Across Sessions (carry-flag)

- **Edge:** the overnight 400-pt down move flipped htfBias to âˆ’1 and it stayed âˆ’1 well into the RTH range, biasing signals. We need to *reset* htfBias when a regime change is confirmed.
- **Rule:** on `REGIME_CHANGE` event, **decay** htfBias by 50%. After 3 consecutive opposite-color bricks on the new regime, reset htfBias to 0. Persist regime/htf state across NT8 restarts in the existing pattern store.
- **Measure:** reduce same-direction-as-prior-trend entries in first hour of new regime by â‰¥ 60%.

##### F8. Dashboard "Regime + Levels" Strip

- **Edge:** user is the final arbiter; show the bot's worldview prominently.
- **Rule:** new top strip showing: `Regime: TREND_DN  |  Brick: â– â– â– â– â–   |  POC: 27155 (-23pt)  |  Next pivot: VAL 27090 (+42pt)  |  MM_TRAP: clear`.
- **Measure:** user feedback after 2 live weeks.

#### 18.8.D Sequencing inside v6

```
Phase 1 (foundation) :  F1 Regime Classifier  â†’  F4 Adaptive Confidence  â†’  F8 Dashboard strip
Phase 2 (capture)    :  F3 Renko Trend-Riding (pyramid + reload)
Phase 3 (defense)    :  F5 MM-Trap Detector  â†’  F2 POC/VA Aware Range  â†’  F6 Range-Break Continuation
Phase 4 (memory)     :  F7 State carry  +  Pattern-Memory Engine (existing Â§18.3.A)
```

Each phase ships behind its own toggle in v6 and is validated against the **Apr 28 day** as benchmark before moving to the next.

#### 18.8.E The "beat the MM" thesis (one paragraph)

> MM win because they **see your stops**, **fade your breakouts**, and **drift price to the level that triggers max retail pain**. v5 hides our SL/TP (good). v6 must add: (1) a *regime brain* so we don't trade the prior trend's signals into the new range; (2) *level awareness* so we trade with the structural pivots MM defend instead of into them; (3) *trap detection* that records MM's repeated stop-runs and flips us to the counter-side; (4) *Renko-anchored trail* that gives MM no inch to nibble at because exits are tied to brick structure, not arbitrary point counts. Stack those four and the MM's edge is gone â€” they have to either let us run or commit real capital to fight, and at our size they will choose the former.

### 18.9 The pact

> **v5 stays alive trading real money. v6 is the lab.** When a v6 feature proves itself over 2 live weeks, we backport the *toggle* (not the implementation) into v6 14.x.x. v5's job is to be reliable. v6's job is to make MM regret showing up.

---



### 14.8.2 `ResetDailyOnRestart` (default ON)

In the `State.Realtime` branch, after `ResetSessionFlags()`, when this
property is ON the agent advances `processedTradeCount` past every existing
`SystemPerformance.AllTrades` entry **and** zeroes `dailyRealizedPnL` /
streak counters. Net effect: only fills that arrive **after** the restart
contribute to today's PnL ï¿½ exactly matching the user's mental model that
`Disable + Enable` should "start clean" for the daily limits.

Set OFF if you actually want disable+enable to preserve the persistent
intra-day PnL counter (e.g. you intentionally cycle the strategy and want
the prior session's  profit/loss to keep counting toward the daily cap).

### 14.8.3 Manual `RESET` button

A third button is added to the `CLOSE 1 | KILL` row, making it
`CLOSE 1 | KILL | RESET`. `RESET` invokes `ExecuteResetDaily()` which:

- Zeroes `dailyRealizedPnL`, `dailyTradeCount`, `consecutiveLosses`,
  `consecutiveWins`, `lastLossDirection`.
- Clears `dailyLimitHit`, `dailyProfitHit`, `emergencyKillActive`,
  `flattenFired`.
- Restores `aggressiveTrailMaxAtrFactor` to its baseline (undoing any
  streak-adaptation).
- Advances `processedTradeCount` to `SystemPerformance.AllTrades.Count`
  so prior fills are not re-credited on the next entry.
- Re-stamps `sessionDate` to `Time[0].Date`.
- Logs `RESET_DAILY,manual=true` to the diag CSV.

### 14.8.4 Auto-tighten / auto-widen on consecutive trades

Four new properties (default OFF for both directions):

- `AutoTightenOnLosses` (bool) + `AutoTightenLossN` (int 1ï¿½10, default 2)
  + `AutoTightenFactor` (double 0.1ï¿½1.0, default 0.5).
  When N consecutive losing trades occur, the agent multiplies the
  **base** `AggressiveTrailMaxAtrFactor` by `AutoTightenFactor` and writes
  `ADAPT_TIGHTEN` to the CSV. Tighter trail => locks profit faster on a
  bad-rhythm day.
- `AutoWidenOnWins` (bool) + `AutoWidenWinN` (int 1ï¿½10, default 3)
  + `AutoWidenFactor` (double 1.0ï¿½3.0, default 1.5).
  When N consecutive winning trades occur, multiplies base factor by
  `AutoWidenFactor` (capped at 2.0). Looser trail => lets winners run on a
  good-rhythm day. Logs `ADAPT_WIDEN`.

The factor is restored to its base value on:
- `ResetSessionFlags` (new RTH session).
- `ExecuteResetDaily` (manual RESET button).
- Whenever `AggressiveTrailMaxAtrFactor` is re-applied via the property
  setter (so editing in the dashboard params resets the baseline too).

The streak counter that opens the next adaptation is reset on the **opposite**
outcome (a single win clears the loss streak, a single loss clears the win
streak).

### 14.8.5 ADX-aware prevDay TP-clamp skip

`ADX(14)` is now instantiated in `State.Configure` (`indAdx`).

Two new properties (group `1 - Risk`):

- `SkipPrevDayClampOnHighAdx` (bool, default ON).
- `HighAdxThreshold` (double 15ï¿½60, default 28).

In both `ArmHiddenStops` and `ResizeHiddenStops` the prevDay TP clamp
is now gated:

`
clampAllowed = !skipPrevDayClampOnHighAdx || indAdx[0] < highAdxThreshold;
`

When the clamp is skipped a `CLAMP_SKIP` row is logged with the live ADX
value so post-trade we can confirm whether a runaway profit was due to the
heuristic firing.

### 14.8.6 Extended diagnostic CSV

The CSV header is now:

`
DateTime,Bar,Close,VWAP,EmaF,EmaS,RSI,ATR,ADX,RawBull,RawBear,Bull,Bear,Tape,htfBias,trapScore,Pos,Qty,AvgEntry,HiddenSL,HiddenTP,TrailPx,TrailTier,ManualOff,ConsecLoss,ConsecWin,DailyPnL,Action,Detail
`

Every row now carries the live position context (direction, qty, entry,
hidden SL/TP, trail price + tier, manual trail offset, both streak counters
and the running daily PnL) so a single CSV is enough for post-trade replay
without needing to cross-reference Print logs.

**Backwards compatibility note**: any pivot/spreadsheet built against the
17-column 14.x header must be re-built against the new 29-column 14.8 header.

---

## 14.9 â€” Live-fix batch: stop-direction, BE relax, fast-reversal exit, no-disable flatten, session rollover

Eight live-trading defects / hardening items addressed in commit `94bdf82`.

### 14.9.1 `maxTradesPerDay` raised 12 â†’ 20
Default cap was choking active days. New default **20** in `ConfigureDefaults`. Existing `MaxTradesPerDay` property unchanged otherwise.

### 14.9.2 SL price-direction swap (sign convention fix)
`pendingSlNudge` semantics standardized: **positive = TIGHTEN, negative = WIDEN**, regardless of trade direction. The arming and resize paths now apply the sign correctly for both Long and Short, so a `TIGHTEN` button always moves the hidden stop closer to entry and `WIDEN` always moves it further. Previously the Short path inverted this and could flip a tighten into a widen mid-trade.

### 14.9.3 Breakeven relax + fallback
- BE-lock no longer triggers prematurely on tiny ticks. Threshold uses `max(beSafeMinTicks, beSafeAtrFactor Ã— ATR)` (defaults 4 ticks, 0.20Ã—ATR) before locking BE.
- If `breakevenAtPoints` is set very low (â‰¤ 4pt) and ATR-derived floor would block it, the floor is the fallback so BE still arms â€” never silently disabled.

### 14.9.4 Fast-reversal exit  (`MonitorFastReversalExit`)
First-tick-of-bar anti-MM exit. Inside an open trade, if **adverse move â‰¥ `fastReversalAtrFactor Ã— ATR`** AND **adverse â‰¥ `fastReversalAdverseMinPts`** (hard floor) AND it happened within **`fastReversalMaxBars`** of entry, the position is force-flattened with `EXIT_LOSS_FAST_REVERSAL`. Throttled by `lastFastReversalBar` so it fires at most once per trade. Defaults: enabled, factor 0.6, max 4 bars, floor 4pt.

### 14.9.5 FLATTEN button: no-disable
The dashboard FLATTEN button used to call `Account.Flatten()`, which causes NT8 to disable the managed strategy due to position-state desync. All flatten paths now route through `ManagedExitAll()` only â€” strategy stays enabled, dashboard stays live.

### 14.9.6 Daily-limit hit: no-disable
Same root cause: hitting `maxDailyLossDollars` / `dailyProfitTargetDollars` previously disabled the strategy. Now it sets `dailyLimitHit` / `dailyProfitHit` flags which veto new entries via `CanEnterTrade`, while leaving the strategy enabled (so trail/exit logic on any open position still runs).

### 14.9.7 TRL NOW button bypass
The "TRL NOW" dashboard button now bypasses the `trailEnabled` gate and the profit-threshold gate â€” pressing it forces `manualTrailEarlyStart = true`, `trailActive = true` and seeds `trailPrice` immediately at the requested offset. The `MonitorAdaptiveTrail` call in `OnBarUpdate` is gated by `trailEnabled || trailActive || manualTrailEarlyStart` so the manual override is honored even when auto-trail is off.

### 14.9.8 18:00 ET futures session rollover  (`MaybeFuturesSessionRollover`)
At the first bar whose timestamp crosses 18:00:00 ET (start of next CME session), daily counters are auto-reset: `dailyTradeCount`, `dailyPnL`, `dailyLimitHit`, `dailyProfitHit`, `consecutiveLosses`, `consecutiveWins`, `lastFuturesSessionResetDate`. A `SESSION_ROLLOVER` row is written to the diag CSV. This means a strategy left running 24/5 begins each new electronic session with a clean slate.

---

## 14.10 â€” Smarter than MM/algos: TOD-SL, news blackout, sweep boost, DCA suppression

This revision adds four customization layers ï¿½ every threshold/window/factor is a NinjaScript property so each user can tune to their account size and instrument.

### 14.10.1 Time-of-Day SL sizing  (Group `8 - Time-of-Day SL`)

The hidden SL distance now adapts to the time of day. Three windows (Open / Midday / Close), each with its own multiplier on the base `slPoints`. Eval order: **Open ? Close ? Midday** (first match wins). Outside any window the base SL is used unchanged.

| Window | Default times (HHMMSS) | Default mult | Rationale |
|---|---|---|---|
| Open    | 09:30:00 ï¿½ 10:30:00 | **1.30** | Wider ï¿½ opening expansion can wick 6-12pt before settling |
| Midday  | 10:30:00 ï¿½ 14:00:00 | **0.80** | Tighter ï¿½ chop, low-ATR; tighter SL preserves the small wins |
| Close   | 15:00:00 ï¿½ 16:00:00 | **1.20** | Wider ï¿½ power-hour whipsaws can sweep stops both directions |

Internal helper `GetEffectiveSlPoints()` returns `round(slPoints ï¿½ mult)` clamped to a 2pt floor. It's called from `ArmHiddenStops` and both branches of `ResizeHiddenStops`. Wrap-around windows (start > end) are supported, e.g. you could define an overnight session window.

Properties:
- `TimeOfDaySlSizingEnabled` (bool, default ON).
- `SodOpenStart` / `SodOpenEnd` (HHMMSS) + `SodOpenSlMult`.
- `SodMiddayStart` / `SodMiddayEnd` + `SodMiddaySlMult`.
- `SodCloseStart` / `SodCloseEnd` + `SodCloseSlMult`.

### 14.10.2 News blackout window  (Group `9 - News Blackout`)

Block ALL entries (manual + auto) within ï¿½ window-min of any time in a CSV list of HHMMSS times. The list is parsed once, cached as minutes-since-midnight; when `NewsBlackoutTimes` is changed via the UI the cache invalidates and re-parses on next entry attempt.

Default times (ET): **08:30** (CPI/PPI/NFP), **10:00** (ISM/JOLTS), **14:00** (FOMC). Default window: **ï¿½2 minutes**.

A `BLOCK_NEWS` row is written to the diag CSV every time an entry attempt is suppressed.

Properties:
- `NewsBlackoutEnabled` (bool, default ON).
- `NewsBlackoutTimes` (string, comma-separated HHMMSS).
- `NewsBlackoutWindowMin` (int 0ï¿½60, default 2).

### 14.10.3 Liquidity-sweep boost  (Group `10 - Liquidity Sweep`)

Classic MM stop-run reversal:
- **Bull sweep** = bar [-1] Low broke below the prior N-bar Low **AND** Close[-1] = that prior low + 0.3 ï¿½ ATR (closed back above the swept level).
- **Bear sweep** = mirror image of above.

When detected, the **opposite-direction** signal is boosted in two ways:
1. `effMin` is reduced by `LiquiditySweepConfBoost` points (floor 35).
2. The `overLong` / `overShort` overextension veto is bypassed for that direction.

This lets the strategy *participate* in the kind of move where MMs sweep one side then reverse ï¿½ exactly the inverse of getting trapped by it.

A `SWEEP_BOOST` diag row is written whenever the boost is active.

Properties:
- `LiquiditySweepBoostEnabled` (bool, default ON).
- `LiquiditySweepLookback` (int 5ï¿½200, default 30 bars).
- `LiquiditySweepConfBoost` (double 0ï¿½30, default 8.0).

### 14.10.4 DCA suppression on loss streak  (Group `11 - DCA Suppression`)

In `CanEnterTrade`, when the requested entry is **same-direction as the open position** and `consecutiveLosses = SuppressDcaLossN`, the add is blocked with a `DCA suppressed` status banner and a `BLOCK_DCA` diag row. The block applies to BOTH manual and auto entries (DCA on a losing day rarely ends well ï¿½ preserve the capital, wait for a clean win to clear the streak).

Default: **2 consecutive losses** ? DCA blocked until next win clears the streak.

Properties:
- `SuppressDcaOnLossStreak` (bool, default ON).
- `SuppressDcaLossN` (int 1ï¿½10, default 2).

### 14.10.5 Diagnostic CSV additions

New `Action` tags introduced this revision:
- `BLOCK_NEWS` ï¿½ entry attempt blocked by news blackout.
- `BLOCK_DCA` ï¿½ same-direction add blocked by loss-streak suppression.
- `SWEEP_BOOST` ï¿½ sweep detected and boost active for this bar.

(Existing 29-column header is unchanged; these tags use the existing `Action,Detail` slots.)

---

## 14.11 ï¿½ Anti-MM smarts: Aggressive Exits, Chop filter, Adaptive intra-day window

Three new behavioral layers driven by the 2026-03-20 chop-spiral autopsy: morning produced +`,640` of clean trend wins (09:50ï¿½10:16), then a low-ADX whipsaw chop window (10:24ï¿½11:34) gave back `-,070` in seven losing shorts where ADX had collapsed below 18 and the order-flow tape was actually buying. All three layers ship as both **NinjaScript properties** AND **dashboard toggles**.

### 14.11.1 Aggressive Exits Mode  (Group `12 - Aggressive Exits`, dashboard button `AGGR`, default OFF)

When ON, applies to **both manual and auto** trades:
- **BE locks early** at `+AggrBeAtPoints` (default 3pt) ï¿½ the smart-BE ATR/TP floors are bypassed in this mode.
- **Trail starts fast** at `+AggrTrailActivationPts` (default 4pt) with distance `AggrTrailDistPts` (default 2pt). Tier name in CSV is `Aggr-Mode`.
- **Pullback exit** ï¿½ once profit = activation, if it pulls back = `AggrPullbackAtrFactor ï¿½ ATR` (default 0.4) **AND** we're within `AggrPullbackMaxBars` (default 2) of entry, fire a market exit (`ExitLong/ExitShort`) tagged `AGGR_PULLBACK`. This is the anti-MM-trap defense ï¿½ once they've started reversing your fast scalp, get out before the round-trip becomes a loss.

Properties: `AggressiveExitsEnabled`, `AggrBeAtPoints`, `AggrTrailActivationPts`, `AggrTrailDistPts`, `AggrPullbackAtrFactor`, `AggrPullbackMaxBars`.

### 14.11.2 Chop Filter  (Group `13 - Chop Filter`, dashboard button `CHOP`, default ON)

Veto layer in `CanEnterTrade` ï¿½ fires for **manual AND auto**. Helper `IsChoppy(direction, out reason)` returns true if ANY of:

| # | Test | Default trigger |
|---|---|---|
| 1 | ADX-collapse | `indAdx[0] < ChopAdxMin (18)` AND ADX falling for `ChopAdxFallingBars (3)` consecutive bars |
| 2 | EMA convergence | `|EmaFast - EmaSlow| < ChopEmaSepMinAtr ï¿½ ATR (0.30 ï¿½ ATR)` |
| 3 | Close-range collapse | range of last `ChopRangeBars (5)` closes `< ChopRangeMaxAtr ï¿½ ATR (1.0 ï¿½ ATR)` |
| 4 | Opposite tape | `ChopBlockOppositeTape` ON AND `|cachedTapeDelta| = ChopOppositeTapeMin (0.05)` AND sign opposes entry direction |

Diag CSV row `BLOCK_CHOP` written with the trigger reason. Test #4 is what would have caught most of today's losing shorts (tape was buying while strategy was selling stop-runs).

Properties: `ChopFilterEnabled`, `ChopAdxMin`, `ChopAdxFallingBars`, `ChopEmaSepMinAtr`, `ChopRangeBars`, `ChopRangeMaxAtr`, `ChopBlockOppositeTape`, `ChopOppositeTapeMin`.

### 14.11.3 Adaptive Intra-Day Window  (Group `14 - Adaptive Window`, dashboard button `ADAPT`, default ON)

Rolling ring buffer of last `AdaptiveWindowSize` (default 5) trade outcomes. Updated on every closed trade via `RecordTradeOutcome(win)` from `OnExecutionUpdate`.

State machine:
- **Off ? Tighten**: when losses-in-window = `AdaptiveWindowLossThreshold` (default 3). Logs `ADAPT_WINDOW_ON`.
- While tightened: `effMin += AdaptiveConfBoost` (default +5) inside `TryAutoEntry`, requiring stronger signals before firing. The tightened state is also surfaced once per bar via the `ADAPT_TIGHTEN_ACTIVE` diag row.
- **Tighten ? Off**: after `AdaptiveWindowClearWins` (default 2) consecutive wins. Logs `ADAPT_WINDOW_OFF`.

The ring buffer auto-resizes when `AdaptiveWindowSize` is changed via the property setter (cache nulled, rebuilt on next trade).

Properties: `AdaptiveWindowEnabled`, `AdaptiveWindowSize`, `AdaptiveWindowLossThreshold`, `AdaptiveWindowClearWins`, `AdaptiveConfBoost`.

### 14.11.4 Dashboard

Three new toggle buttons on a third action row below the existing TRL/TRP/BE row:
- `AGGR ON/OFF` ï¿½ DarkOrange when active, DarkRed when off. Tooltip shows current Aggr thresholds.
- `CHOP ON/OFF` ï¿½ DarkSlateGray when active. Tooltip describes the four chop tests.
- `ADAPT ON/OFF` ï¿½ DarkSlateGray when active. Tooltip shows window size + thresholds. Toggling OFF also clears any active tighten state.

### 14.11.5 Diagnostic CSV additions

New `Action` tags introduced this revision:
- `BLOCK_CHOP` ï¿½ entry blocked by chop filter (`Detail` = trigger reason).
- `AGGR_PULLBACK_EXIT` ï¿½ Aggressive Exits market exit fired (`Detail` shows peak / current / pullback in pts).
- `ADAPT_WINDOW_ON` / `ADAPT_WINDOW_OFF` ï¿½ adaptive tighten state transitions.
- `ADAPT_TIGHTEN_ACTIVE` ï¿½ heartbeat row each bar while tighten is active (shows boosted `effMin`).

(Header unchanged at 29 columns ï¿½ these tags use the existing `Action,Detail` slots.)

### 14.11.6 Why this addresses the 2026-03-20 chop-spiral autopsy

Today's seven losing shorts had every chop signature simultaneously: ADX 9ï¿½18, EMAs flat, tape positive (buyers) while strategy fired shorts at price-wick lows. The chop filter alone (test #1 + #4) would have blocked all seven. The adaptive window would have additionally raised the bar after the first 3 losses. Aggressive exits would have flipped the few trades that *did* move our way (e.g. 11:21, 11:24) from `-` trail-stop losses into small wins by exiting on the first 0.4ï¿½ATR pullback.

Future work (deferred to 14.12):
- Fib-retracement + candle-pattern reversal scalp setup (separate toggle, default OFF).
- Per-hour outcome heat-map for self-tuning best/worst hours.



---

## 20. Phase 1.2-1.5 â€” Brick Analytics (SHIPPED v6 1.2.0, commit 238ec69, 2026-04-28)

### 20.1 What shipped
- **Run Tracker (1.2):** `runCurrentLen`, `runCurrentColor`, `runStartPrice`, `runMaxFavPts`, `runStartTime`, `runMaxLast10`, `runLast10` queue. Emits `RUN_END color=X len=N pts=P maxFav=F durSec=S startPx=â€¦ endPx=â€¦` on every color flip.
- **Wick Analyzer (1.3):** per-brick `body`, `wickUp`, `wickDn`, `wickRatio`. Emits `WICK_TAG absorption color=X body=B wickUp=U wickDn=D ratio=R` when wick > 0.8Ã— body and â‰¥ 6pt (MM defending price).
- **MM Pattern Recorder (1.4):** `brickColorRing` (20 deep), `brickIntervalsLast10` queue, `fastBricksLast10` count. Emits `MM_PATTERN ring=â€¦ flips=N maxRun=N avgIntvSec=X fast10=N runMaxLast10=N` every 5 minutes.
- **CHOP rule loosened (1.5):** `adx<22 && (recentFlips>=2 || |distVwapAtr|<0.5)` â€” now actually fires.
- **Dashboard:** new `Run:` row showing colorÃ—len, fav points, max10, fast bricks. Gated by `EnableBrickAnalytics`.
- **Property:** `EnableBrickAnalytics` Group "10 - Regime", Order 2, default OFF.

### 20.2 Validation â€” Playback 2026-04-28 (MmATM_v6_DiagLog_20260428_Playback1.csv)

**Brick statistics (75 runs, full session):**
- Avg run length 8.09 bricks; max 30 bricks
- Avg run favorable excursion **41.8 pt**; max **132 pt** (07:34 TREND_DN)
- Length distribution: 12 traps (1-brick) / 10 short / 21 mid / 11 long / 10 very-long / 11 monster (â‰¥16)
- **1-brick trap rate confirmed at 16%** (12/75)
- 28 "big runs" (â‰¥8 bricks AND â‰¥20pt fav) â€” only 3 caught by current strategy

**Trades:** 4 (2W/2L), - day:
- WIN#1 09:52â†’09:53 R6â†’R9, +11.5pt (UNKNOWN regime) â€” tiny scalp
- LOSS#1 10:30 RÃ—1, - â€” **textbook MM trap** (entered brick #1 of new R after G run had body=4pt but maxFav=24pt â†’ 20pt of upper wicks = MM defending top, then trap-flipped)
- WIN#2 10:35 RÃ—9â†’15, +20.25pt (TREND_DN) â€” only good catch
- LOSS#2 15:35 RÃ—8, - â€” distribution mode (avgIntvSec=178s, htfBias=-1, distVwap=+26.6 extended)

### 20.3 The catastrophic miss
After WIN#1 closed at 09:53:31 (+11.5pt), market entered TREND_DN with brick streak progressing 13â†’14â†’15â†’16â†’17 bricks. Strategy logged **5 consecutive `BLOCK_POST_WIN`** rows from 09:55:07-09:55:13. The market gave a **50+ point continuation runner** while we sat in 7-min cooldown. **Single highest-impact fix available.**

### 20.4 Block heatmap (TREND regimes only â€” = missed real opportunities)
- `BLOCK_POST_WIN` in TREND_DN: **21 events**
- `BLOCK_EXTENSION` in TREND_DN: **13 events**
- WAIT signals during TREND with brickStreakâ‰¥4: **54 events**

---

## 21. Phase 2 â€” Beat-the-MM Roadmap (NEXT, ordered by $$ impact)

### 21.1 Phase 2.1 â€” Smart Cooldown (TREND-aware post-win gate)

**Why first:** Single biggest waste â€” 21 BLOCK_POST_WIN in TREND_DN, including 5 consecutive at 09:55 covering a 50pt runner.

**Rule:**
- If `Regime in (TREND_DN, TREND_UP)` AND `brickStreak >= 4 same dir as last winner` AND `adxSlope > 0`:
  - Reduce cooldown from 7 min â†’ **60 sec** AND require streak >= 4 to re-enter
- Else: keep current 7-min cooldown
- Toggle: `EnableSmartCooldown` (default OFF)
- Diag: `SMART_COOLDOWN_BYPASS dir=â€¦ streak=â€¦ adxSlope=â€¦ mins_since_win=â€¦`

**Expected:** catches the 09:55-09:57 runner pattern (~+50pt = /contract on a single trade we missed).

### 21.2 Phase 2.2 â€” Extension TREND-Bypass

**Why second:** 13 BLOCK_EXTENSION in TREND_DN. Strong trends extend by definition; extension guard exists to avoid chasing in chop.

**Rule:**
- If `Regime in TREND_*` AND `brickStreak <= 8` AND `adxSlope > 0` AND signal direction agrees with regime:
  - Bypass extension block
- Toggle: `EnableExtensionTrendBypass` (default OFF)
- Diag: `EXTENSION_BYPASS regime=â€¦ streak=â€¦ adxSlope=â€¦`

### 21.3 Phase 2.3 â€” MM-Trap Re-Entry (user request, 2026-04-28)

> User: *"please understand the MM traps in the brick. Once that happens and return to the same trend, we should get back in the trend with a new trade after we are trapped by MM."*

**Trigger:** any `EXIT_LOSS_SL` where prior brickStreak == 1 (we got trap-flipped).

**Watcher arms for 90 sec:**
- If next 3 bricks resume **original pre-trap direction** (i.e., the trap was a fake reversal and trend continues), re-enter that direction
- SL: tighter than usual (12pt vs 18pt â€” the trap zone is now mapped)
- TP/trail: standard
- Toggle: `EnableTrapReEntry` (default OFF)
- Diag: `TRAP_REENTRY_ARMED original_dir=â€¦ ret=â€¦sec` then `TRAP_REENTRY_FIRED` or `TRAP_REENTRY_EXPIRED`

### 21.4 Phase 2.4 â€” Brick Mode Entry (the big one)

**Constraints (captured from user, 2026-04-28):**
- Skip brick #1 of new color (16% trap rate confirmed)
- Enter on brick #2 same color if:
  - body â‰¥ 6pt
  - wickRatio â‰¤ 1.5 (no heavy MM defense)
  - brick interval â‰¤ 30 sec (momentum, not distribution â€” LOSS#2 had 178s)
  - ADX rising OR ATR > 0.8Ã— avg
  - NOT within 0.5 ATR of POC/VAH/VAL/prevDayH/L unless TREND_*
- SL = previous opposite-color brick's far extreme + 4 ticks
- Trail brick-by-brick after brick #3 (move SL to brick #2 low, then brick #4, etc.)
- Exit on 1st opposite brick if past brick #5; wait for 2nd opposite if at bricks #2-#4
- Toggle: `EnableBrickModeEntry` (default OFF)

### 21.5 Phase 2.5 â€” Wick-Reject Gate

LOSS#1 forensics: prior G run had body=4pt but maxFav=24pt (= 20pt upper wicks). When `WICK_TAG` count in last 5 bricks â‰¥ 2 against your direction, **block entry against the rejection** but **bias next entry with the reject**.

---

## 22. Working agreement (reaffirmed)
- One toggle-gated item per session, default OFF
- `get_errors` before commit
- Commit format: `v6 0.X.Y: <desc>`
- User F5-tests + sends new playback log between items
- Each ship validated against real diag data before next ship

---

## 23. Phase 2.1 + 2.2 SHIPPED (Option C, v6 2.2.0, 2026-04-28)

### 23.1 What shipped (ACTIVE LOGIC — these CHANGE entry decisions when ON)

**Phase 2.1 — Smart Cooldown (TREND bypass)** — toggle `EnableSmartCooldown` (Group "10 - Regime", Order 10).
The 7-min `BLOCK_POST_WIN` cooldown is replaced by a 60s gate ONLY when ALL of:
1. `currentRegime` is `TREND_UP` (long re-entry) or `TREND_DN` (short re-entry)
2. NinzaRenko brick streak ≥ `SmartCooldownStreakMin` (default 4) in trend color
3. ADX slope > 0 (trend strength rising)
4. ≥ `SmartCooldownMinSec` (default 60s) since the last winning exit

When all true, emits `SMART_COOLDOWN_BYPASS` row and allows the entry. When ANY condition fails, the original 7-min block stays in force (logs `BLOCK_POST_WIN` as before).

**Phase 2.2 — Extension TREND-Bypass** — toggle `EnableExtensionTrendBypass` (Group "10 - Regime", Order 20).
The "price too far from VWAP" extension block is bypassed ONLY when ALL of:
1. `currentRegime` agrees with trade direction
2. NinzaRenko brick color agrees with trade direction
3. Brick streak ≤ `ExtensionBypassStreakMax` (default 8) — skip if already monstrous (mean reversion risk)
4. ADX slope > 0

When all true, emits `EXTENSION_BYPASS` row and allows the entry. When ANY fails, original `BLOCK_EXTENSION` is logged.

### 23.2 ✅ HOW TO ENABLE — Step-by-step

**Prerequisites (must be ON for the bypasses to make smart decisions):**
- `Enable Regime Classifier (label-only)` = **ON**
- `Enable Brick Analytics (label-only)` = **ON** (recommended for full diag visibility)
- `Enable Diag Log` = **ON** (so you can verify what's happening)

**Recommended turn-on sequence:**

1. **Day 1 — baseline.** Run a full session with prerequisites ON but BOTH bypasses **OFF**. This baseline log shows what we're missing.
2. **Day 2 — enable Smart Cooldown only.** Set `EnableSmartCooldown=ON`. Leave Extension Bypass OFF. Run full session. Compare trade count + P&L vs Day 1.
3. **Day 3 — enable both.** Add `EnableExtensionTrendBypass=ON`. Run full session. Compare again.

This staged approach isolates each bypass's contribution. If Day 2 P&L is worse than Day 1, the Regime Classifier is mislabeling your market — disable Smart Cooldown and report the diag log.

### 23.3 ⚠ WHEN TO DISABLE

Disable **immediately** if any of these patterns appear in the diag log:

| Symptom | Disable | Why |
|---|---|---|
| 2+ consecutive losses where `SMART_COOLDOWN_BYPASS` row was followed by `EXIT_LOSS_SL` | EnableSmartCooldown | Regime label is fooling the bypass; the cooldown was protecting you |
| `EXTENSION_BYPASS` rows that all become losers | EnableExtensionTrendBypass | Trend was already exhausted; chase risk dominates |
| Day P&L worse than baseline (Day 1) for 2 sessions | Both | Real-market regime doesn't match the classifier's heuristics on your instrument/timeframe |
| Major news session (FOMC / CPI / NFP) | Both, plus EnableAuto | News whipsaws will flip the regime label faster than entry guards can react |

### 23.4 📊 What to look for in the diag CSV after enabling

Filter the CSV by `Action` column:
- `SMART_COOLDOWN_BYPASS` — each occurrence is a TREND continuation we caught that the cooldown would have blocked. **Track the next entry's exit:** if it's a WIN, the bypass earned its keep; if LOSS, the regime label was wrong.
- `EXTENSION_BYPASS` — each occurrence is a TREND-following entry past the VWAP extension limit. Same rule: track the exit.
- `BLOCK_POST_WIN` and `BLOCK_EXTENSION` — these still fire when the bypass conditions aren't met. The block count should be **lower** with bypasses ON (some converted to entries), but not zero (CHOP/UNKNOWN regimes still block).

**Healthy ratio target:** of all bypass rows, ≥ 60% should result in WIN exits to validate the bypass logic. Below that, retune the streak/slope thresholds higher (more selective).

### 23.5 Tuning knobs (only after baseline data confirms behavior)

| Property | Default | Increase to... | Decrease to... |
|---|---|---|---|
| `SmartCooldownMinSec` | 60 | Wait longer after a win (more conservative) | Re-enter faster (more aggressive) |
| `SmartCooldownStreakMin` | 4 | Require stronger trend confirmation | Catch earlier in trend |
| `ExtensionBypassStreakMax` | 8 | Allow chasing further into the move | Block bigger extensions sooner |

### 23.6 What it does NOT change
- Manual entries always bypass cooldowns/extensions (unchanged).
- CHOP / SQUEEZE / UNKNOWN regimes still hit `BLOCK_POST_WIN` and `BLOCK_EXTENSION` normally.
- The trail engine, SL/TP, brick analytics, regime classifier itself — all unchanged.
- SL-cluster cooldown, directional lockout, news block, max-trades-per-day — all unchanged and still gate before these bypasses.

### 23.7 Risk note
Both bypasses are **risk-increasing** (they create entries that didn't exist before). They only earn money if the Regime Classifier labels are accurate on your instrument. If you ever run a fresh playback day and see `REGIME_CHANGE` rows that look wrong vs the actual chart move, **leave the bypasses OFF** until the classifier is retuned.

———

**Next phases queued:** 2.3 MM-Trap Re-Entry, 2.4 Brick Mode Entry, 2.5 Wick-Reject Gate. Will be designed after we validate Phase 2.1+2.2 against 2-3 sessions of real data.
