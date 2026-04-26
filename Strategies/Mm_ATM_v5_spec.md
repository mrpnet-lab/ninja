# Mm_ATM_v5 — Design Specification & Implementation Summary

**Version:** 5.0  
**Date:** April 25, 2026  
**Base reference:** `Mm_ATM_v4.cs` (kept intact for backtest comparison)  
**Target file:** `Mm_ATM_v5.cs`  
**Platform:** NinjaTrader 8.1+ — NQ Futures (MNQ compatible)

---

## Table of Contents

1. [Background & Goals](#1-background--goals)
2. [Plan: Approved Directives](#2-plan-approved-directives)
   - A. Critical Bug Fixes (A1–A8)
   - B. Filter Consolidation (20 → 12)
   - C. Strategy Reduction
   - D. Exit Hierarchy Unification
   - E. Smart Trail + MM-Avoidance Backtrack
   - F. Parameter Consolidation
   - G. New Smart Logic (G1–G5)
   - H. Dashboard Redesign
   - I. Manual Trade-Signal Indicator
3. [Implementation Summary](#3-implementation-summary)
4. [Architecture Overview](#4-architecture-overview)
5. [12 Active Filters (F1–F12)](#5-12-active-filters-f1f12)
6. [Exit Hierarchy (Unified)](#6-exit-hierarchy-unified)
7. [Smart Trail Backtrack — MM Avoidance](#7-smart-trail-backtrack--mm-avoidance)
8. [Order-Flow Tape Filter (G1)](#8-order-flow-tape-filter-g1)
9. [Manual Trade-Signal Indicator](#9-manual-trade-signal-indicator)
10. [Dashboard Redesign](#10-dashboard-redesign)
11. [Properties Reference](#11-properties-reference)
12. [Backtest Validation Plan](#12-backtest-validation-plan)
13. [Known Differences vs v4](#13-known-differences-vs-v4)
14. [Post-Release Updates](#14-post-release-updates)
    - 14.1 Dashboard Resize & Width Fix
    - 14.2 Trail Diagnostics
    - 14.3 Manual Trail Control (TRL NOW / Trail ±pt)
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
- One clean, prioritized exit path — no races
- Invisible SL / Trail / Jump SL as the core edge
- Clear, actionable manual trading signal
- Lighter codebase (~1,800 lines target from 4,700)

---

## 2. Plan: Approved Directives

### A — Critical Bug Fixes (A1–A8)

| ID | Bug | Fix Applied |
|---|---|---|
| A1 | Class name still `Mm_ATM_v4`, Name property = "Mm_ATM_v4" | Renamed to `Mm_ATM_v5` everywhere, all log tags updated |
| A2 | BUY ASK / SELL BID pre-incremented `totalContracts` on submit | Deferred all bookkeeping to `OnOrderUpdate.Filled` |
| A3 | Limit orders counted before fill, causing de-sync | Same fix as A2 — `OnOrderUpdate.Filled` is single source of truth |
| A4 | `ArmHiddenStops()` called on submit using `Close[0]` as avgPrice | `ArmHiddenStops()` only called from `OnOrderUpdate.Filled` after real fill price is known |
| A5 | DCA re-arm used `Close[0]` instead of `Position.AveragePrice` | VWAP-weighted avg computed from actual fills; Realtime recovery uses `Position.AveragePrice` |
| A6 | No per-bar entry guard — multiple entries possible on same bar | `enteredThisBar` flag, reset on `IsFirstTickOfBar` |
| A7 | Trail evaluated against stale `Close[0]` when bid/ask = 0 in Realtime | Trail uses `GetCurrentBid/Ask`; falls back to `Close[0]` only if bid/ask > 0 check fails |
| A8 | Single `pendingExit` flag confused partial exits with full exits | Separate `pendingExit` (full) tracked with `pendingExitTicks` watchdog; partial exits handled independently |

---

### B — Filter Consolidation (20 → 12)

**Dropped filters (8):**
- `#3` Momentum exhaustion (overlapped with F7 RSI div)
- `#4` Wick ratio (noise, no edge)
- `#7` VWAP-distance scaling (replaced by unified F4 slope)
- `#8` Swing structure (caused ORB dependency)
- `#9` Key Level midrange (dropped with ORB strategy)
- `#14` Lunch-hour time block (too blunt — hours filter handles this)
- `#16` Consecutive loss block (replaced by bar-based cooldown in `TryAutoEntry`)
- `#17/#18` PrevDay bounce patterns (marginal, over-fitted)

**Kept and reorganized as F1–F12 (see Section 5).**

---

### C — Strategy Reduction

| Strategy | v4 | v5 | Reason |
|---|---|---|---|
| 0 — Momentum + VWAP | ✅ Keep | ✅ Keep | Core profitable edge |
| 1 — Key Level Breakout | ✅ Keep | ✅ Keep | Profitable |
| 2 — Liquidity Sweep Reversal | ✅ Was S2 | ❌ Dropped | $-122/trade |
| 3 — Opening Range Breakout | ✅ Was S3 | ❌ Dropped | $-91/trade; replaced by Open-Type classification (G3) |
| Auto-Select | Was S4 | Is S2 | Picks best of M+V vs KLB each bar |

All ORB state variables (`orbHigh`, `orbLow`, `orbSet`, `UpdateOrbLevels()`) removed entirely.

---

### D — Exit Hierarchy Unification

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
3. Trap Escape (trapScore ≥ 65 && adverse > 10pt  →  immediate;
                trapScore ≥ 50 && adverse > 5pt  →  2-bar graduated)
4. Adaptive Trail (after activation at trailActivationPoints)
5. Hidden TP (only if !runnerModeActive)
```

Removed: EMA-against exit, Smart SL Loosen, redundant duplicate BE logic.

---

### E — Smart Trail + MM-Avoidance Backtrack

**Trail simplification:**
- Single `GetTrailDistance()` (cached per bar) based on ATR + EMA separation + trend direction score
- 3 profit tiers: T1-BE, T2-Strong, T3-Runner
- Time-based ratchet: every 5 bars in profit, trail tightens 10%
- HTF-agrees path gives wider Runner distance (×1.8)

**NEW: MM Stop-Hunt Avoidance Backtrack**
- `MonitorTrapDetector()` detects stop-hunt spikes: price penetrates 5-bar structure extreme but closes back inside
- Sets `stopHuntSuspendBars = 4` (4-bar window)
- While window is open, trail is allowed to **relax** (move away from price) by up to `SmartTrailBacktrackTicks` (default **4 ticks**, range 0–12, configurable)
- Relaxed price floored at `originalSlPrice` (never worse than original SL)
- One backtrack per bar (`backtrackUsedBar` guard)
- After 4 bars, normal ratchet resumes

This intentionally sacrifices a few ticks to survive the MM sweep zone and let price continue.

---

### F — Parameter Consolidation

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

### G — New Smart Logic

| ID | Feature | Implementation |
|---|---|---|
| G1 | Order-flow tape filter | `OnMarketData` 30-sec rolling aggressor delta (ask-side vs bid-side volume). Default ON. Live-only — no backtest impact. Exposed as `OrderFlowFilterEnabled`. |
| G2 | Liquidity void detection | 3 consecutive bars with range > 2×ATR → `voidBarsRemaining = 2` cooldown + confidence softened 30% |
| G3 | Open-type classification | First 30 min RTH: if price moved > 1.5×ATR from open = Open-Drive. Bull or bear classified. F12 applies +8 confidence boost to aligned direction. |
| G4 | Time-based SL ratchet | Every 5 bars while in-trade and profit > 1.5× trail activation: trail tightened 10% |
| G5 | Adverse-tick streak detector | Folded into `MonitorTrapDetector`: 2-bar graduated trap escape at score ≥ 50 with adverse > 5pt |

---

### H — Dashboard Redesign

**Removed:**
- Dual mode button rows (collapsed to MANUAL / AUTO toggle pair)
- Subsystem enable/disable popup (TRL and TRP toggles remain inline)
- Duplicate PnL labels

**Added:**
- Big Trade-Signal indicator at top (Section I)
- R-multiple on TP label
- Daily trades counter `[done/max]`
- Account P&L breakdown: Realized / Unrealized / Balance
- Tape Δ readout with arrow: `▲ buyers` / `▼ sellers` / `● balanced`
- VWAP value inline
- Hours indicator: open/closed + time range
- Active strategy score breakdown when Auto-Select mode active

---

### I — Manual Trade-Signal Indicator

**Rationale:** Raw Bull/Bear % alone does not communicate well under trading pressure. A composite, explicit action label is more reliable for live manual entry decisions.

**Composite inputs:**
1. `lastBullConfidence` or `lastBearConfidence` ≥ `minSignalConfidence`
2. HTF bias not opposed to direction
3. EMA fast/slow cross direction agrees
4. Order-flow tape not opposed (if enabled)
5. No liquidity void active
6. Bonus: fresh EMA cross (≤ 3 bars ago)
7. Bonus: confidence flip (prev dominant direction just reversed)

**Output levels:**

| Level | Label | Condition |
|---|---|---|
| +2 | `▲ STRONG BUY` | All conditions + conf ≥ minConf+15 + confluence ≥ 4 |
| +1 | `↑ BUY` | Core conditions met |
| 0 | `● WAIT` | Missing ≥1 core condition |
| -1 | `↓ SELL` | Core conditions met bear side |
| -2 | `▼ STRONG SELL` | All conditions + conf ≥ minConf+15 + confluence ≥ 4 |

**Display:** Shown as large (22pt bold) text at the top of the dashboard with a reason line below listing blocking factors (`low conf / htf-against / ema-against / tape-against / liq-void`).

Bull/Bear % bars remain visible with opacity scaled to threshold (dim when below min confidence, full when above) so you can still assess relative strength.

---

## 3. Implementation Summary

### What was implemented

| Section | Status | Notes |
|---|---|---|
| Class rename to `Mm_ATM_v5` | ✅ | All `[Mm-ATM v5]` tags, Name = "Mm_ATM_v5" |
| Bug fixes A1–A8 | ✅ | All 8 fixed |
| 12 active filters | ✅ | F1–F12, single ApplySmartFilters() pass |
| Strategies 0 + 1 + Auto | ✅ | S2 (LiqSweep) and S3 (ORB) removed entirely |
| Unified exit hierarchy | ✅ | 5 subsystems → 1 ordered priority list |
| Breakeven lock | ✅ | Single, at `breakevenAtPoints` |
| Adaptive trail (simplified) | ✅ | 3 profit tiers + HTF runner path |
| MM-avoidance trail backtrack | ✅ | `SmartTrailBacktrackTicks` param, stop-hunt 4-bar window |
| Time-based ratchet (G4) | ✅ | Every 5 bars in profit, -10% trail distance |
| Order-flow tape filter (G1) | ✅ | `OnMarketData`, 30-sec rolling delta, `OrderFlowFilterEnabled` |
| Liquidity void (G2) | ✅ | 3-bar 2×ATR detector, 2-bar cooldown |
| Open-type classifier (G3) | ✅ | RTH first 30 min classification → F12 |
| Trap detector (G5) | ✅ | Immediate + graduated escape + stop-hunt spike |
| Manual trade-signal indicator | ✅ | 5-level composite with reason text |
| Dashboard redesign | ✅ | Signal at top, R-mult, tape delta, account bal, score breakdown |
| Parameter consolidation | ✅ | 8 groups, ~26 properties |
| Diagnostic CSV log | ✅ | Daily file, optional via `EnableDiagLog` |
| VWAP + Volume Profile | ✅ | Intraday VWAP, POC/VAH/VAL |
| PrevDay levels | ✅ | H/L clamping on TP arm |
| Chart annotations | ✅ | SL/TP/Entry/Trail lines + labels with $ values |
| Session-level annotations | ✅ | VWAP, PD High/Low, POC, VAH, VAL |

### What was NOT implemented (intentional)

| Item | Decision |
|---|---|
| DCA / add-to-winner logic | Preserved via `maxContracts` and existing `CanEnterTrade` gate, but not auto-triggered in v5 (manual only via dashboard) |
| Automated backtest run | User-driven via Strategy Analyzer (see Section 12) |
| ORB | Replaced by Open-Type Classification (G3); full ORB removed |
| Adverse-tick timer (7-tick / 10-sec) | Folded into 2-bar trap escape — simpler, same net effect |

---

## 4. Architecture Overview

```
OnBarUpdate()
├── ProcessPendingButtons()        ← button actions deferred from UI thread
├── SyncPositionState()            ← detect de-syncs, recover silently
├── Exit watchdog (pendingExit)    ← stale exit force-flatten after STALE_EXIT_TICKS
├── Aggressive limit timeout       ← cancel ASK/BID if unfilled after 3 sec
├── Live exit monitors (if in trade)
│   ├── MonitorHiddenStops()       ← SL, BE lock, TP (unified priority 1,2,5)
│   ├── MonitorAdaptiveTrail()     ← trail + backtrack window (priority 4)
│   └── MonitorTrapDetector()      ← trap score, escape, stop-hunt (priority 3)
├── Session housekeeping
│   ├── UpdateVwap()
│   ├── UpdateHtfBias()
│   ├── TrackEmaCross()
│   ├── UpdateOpenTypeClassification()
│   ├── UpdateLiquidityVoid()
│   └── UpdateVolumeProfile()
├── AutoFlattenCheck()
├── CalculateSignals() + ApplySmartFilters()  ← once per bar, when flat
├── UpdateManualSignal()                      ← once per bar
├── TryAutoEntry()                            ← once per bar, if autoMode
├── DrawChartAnnotations()
└── UpdateDashboard()              ← throttled 333ms in Realtime

OnOrderUpdate()   ← ONLY place totalContracts and averageEntryPrice are set
OnPositionUpdate() ← sets pendingPositionFlat
OnExecutionUpdate() ← PnL tracking, consecutive loss count, daily limit
OnMarketData()    ← tape delta rolling window (Realtime only)
```

---

## 5. 12 Active Filters (F1–F12)

| # | Filter | Direction | Logic |
|---|---|---|---|
| F1 | Volume Confirmation | Both | Prior-bar volume vs 20-bar avg. VR < 0.5 → −30%; VR > 2 → +15% |
| F2 | Bull/Bear Conflict | Both | If min/max > 0.80 → both penalized −30% |
| F3 | HTF Bias Composite | Both | 5-min EMA cross + session open drift + HTF EMA distance. ≥2 votes = bias. Opposed direction −50% |
| F4 | VWAP Slope | Both | VWAP rising > 5% ATR → bull +10/bear −5; falling → bear +10/bull −5 |
| F5 | 5-bar Price Slope | Both | Slope > 0.4 ATR against direction → penalize up to −50% |
| F6 | EMA Cross Structure | Both | EMA fast < slow → bull ×0.65 always (and vice versa) |
| F7 | RSI/Price Divergence | Both | Classic hidden divergence: +10 bonus |
| F8 | Confidence Flip Bonus | Directional | If dominant direction just reversed → +15 to new dominant |
| F9 | EMA Cross Momentum | Directional | 0/1/2 bars after cross → +25/+20/+15 boost |
| F10 | Order-Flow Tape | Both | Tape Δ > 0.25 → bull +10/bear −5; < −0.25 → bear +10/bull −5 |
| F11 | Liquidity Void | Both | While voidBarsRemaining > 0 → both ×0.70 |
| F12 | Open-Type Alignment | Directional | Open-Drive bull/bear classified → +8 boost to aligned direction |

All filters: output clamped to [0, 120]; raw-floor = 50% of pre-filter value (prevents runaway penalization).

---

## 6. Exit Hierarchy (Unified)

Each tick, when `stopsArmed && !pendingExit`:

```
1. MonitorHiddenStops()
   ├── Check BE lock (at breakevenAtPoints profit) — fire once, permanently
   ├── If price ≤ hiddenStopPrice → ExitLong/Short (SL hit)
   └── If price ≥ hiddenTargetPrice && !runnerMode → ExitLong/Short (TP hit)

2. MonitorTrapDetector() [IsFirstTickOfBar only]
   ├── Compute trapScore from adverse movement, EMA conflict, high volume
   ├── Stop-hunt spike → stopHuntSuspendBars = 4
   ├── trapScore ≥ 65 && adverse > 10pt → immediate escape (priority 3a)
   └── trapScore ≥ 50 && adverse > 5pt for 2 bars → graduated escape (priority 3b)

3. MonitorAdaptiveTrail()
   ├── Activation: after 2 bars, profit ≥ trailActivationPoints
   ├── Compute GetTrailDistance() (ATR-weighted, regime-adjusted)
   ├── Apply tier floor (T1/T2/T3)
   ├── Apply MM backtrack if stopHuntSuspendBars > 0
   └── If price hits trailPrice → ExitLong/Short (trail hit)
```

---

## 7. Smart Trail Backtrack — MM Avoidance

**Problem:** MM bots routinely spike price below key swing lows (where retail stops cluster), then immediately reverse. A standard ratchet trail gets hit by this spike and exits at the worst price.

**Solution:** When a stop-hunt spike is detected, temporarily allow the trail to loosen — moving *away from price* by up to `SmartTrailBacktrackTicks` — giving the trade room to survive the spike and re-enter the valid trend.

```
Normal trail:    [price ──→] [trail ratchets up]
Stop-hunt:       [price spikes DOWN] → trail would normally follow and exit
v5 behavior:     detect spike → trail BACKS DOWN by N ticks → price recovers → no exit
```

**Parameters:**
- `SmartTrailBacktrackTicks` — default **4 ticks** ($20 per contract per tick), range 0–12
- `stopHuntSuspendBars` — 4-bar window after spike detected
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
- **F10 filter:** soft confidence adjustment (±10/−5)
- **Manual signal:** `tapeOk` required for BUY/SELL output (soft block at ±0.1 threshold)
- **Dashboard:** Tape Δ readout with directional arrow

**Notes:**
- Live (Realtime) only — `OnMarketData` is not called during Historical
- Default ON; can be disabled via `OrderFlowFilterEnabled = false`
- No performance impact on backtest (filter condition skipped when not Realtime)

---

## 9. Manual Trade-Signal Indicator

Designed for high-accuracy single-click manual entry decisions. Updates once per bar (when flat) via `UpdateManualSignal()`.

**Scoring confluence factors:**
```
1 point — HTF bias agrees (or neutral)
1 point — EMA fast/slow cross direction agrees
1 point — Order-flow tape not opposed
1 point — Fresh EMA cross (≤ 3 bars ago, same direction)
1 point — Confidence flip (dominant direction just changed to this direction)
```

**STRONG BUY / STRONG SELL** requires:
- Confidence ≥ minSignalConfidence + 15
- Confluence ≥ 4 out of 5 points

**WAIT reasons shown in dashboard:**
- `low conf` — confidence below threshold
- `htf-against` — 5-min / session bias opposes
- `ema-against` — EMA cross opposes
- `tape-against` — order-flow delta opposes
- `liq-void` — liquidity void active

**Important:** This signal is for **entry timing only**. All risk management (SL, trail, TP) is handled by the strategy's hidden stop layer regardless of how you entered.

---

## 10. Dashboard Redesign

Layout (top to bottom, width 290px):

```
┌─────────────────────────────────────┐
│  ≡  NQ  Mm-ATM v5             ↙↘  │  ← draggable title + resize grip
├─────────────────────────────────────┤
│  ▲ STRONG BUY                       │  ← 22pt signal label
│  conf=72 htf=1 tape=0.41           │  ← reason / confluences
├─────────────────────────────────────┤
│  [MANUAL] [AUTO] [HRS ON]           │  ← mode + hours toggle
│  ◄  Momentum+VWAP  ►               │  ← strategy selector
│  Active: Momentum+VWAP [M+V=71 KLB=52]│
├─────────────────────────────────────┤
│  Bull: 72%  ████████░░             │  ← confidence (opacity = strength)
│  Bear: 31%  ███░░░░░░░             │
│  Tape Δ: +0.41  ▲ buyers           │
├─────────────────────────────────────┤
│  ● LONG ×2                         │  ← status badge
│  Pos: 2/4  Avg: 21456.50           │
│  SL: 21438.50  (18pt | $360)       │
│  TP: 21506.50  (50pt | $1000) R=0.92│
│  Trail: 21445.00 (6.2pt T2-Strong M)│  ← M = manual mode active
│  Trap: 22%  bars=3                 │
├─────────────────────────────────────┤
│  Unrealized: +$184.00              │
│  Daily P&L: +$264.00  [2/4]        │
│  Account P&L: +$264 (R+264/U+0)   │
│  Balance: $52,848.00               │
├─────────────────────────────────────┤
│  VWAP: 21451.25                    │
│  Hours: ● open  9:30 AM–4:00 PM   │
├─────────────────────────────────────┤
│  [BUY MKT ] [SELL MKT]             │
│  [BUY ASK ] [SELL BID]             │
│  [BUY LMT ] [SELL LMT]             │
├─────────────────────────────────────┤
│  Qty:      [−]  2           [+]    │
│  SL:       [−]  18pt|$360   [+]    │
│  TP:       [−]  50pt|$1000  [+]    │
│  Jump%:    [−]  50%         [+]    │
│  Trail ±pt:[−]  6.2pt|25tk  [+]    │  ← manual trail nudge (1 pt/click)
├─────────────────────────────────────┤
│  [CLOSE] [CLOSE 1] [JUMP SL]       │
│  [FLATTEN][KILL][TRL NOW][TRL ON]  │
│  [TRP ON] [BE ON]                  │
├─────────────────────────────────────┤
│              ↙ resize ↘            │  ← drag to resize width & height
└─────────────────────────────────────┘
```

**Button legend (row 5 + row 6):**

| Button | Default state | Action |
|---|---|---|  
| `TRL NOW` | magenta, disabled when flat | Activates hidden trail immediately at auto-computed distance. Sets manual mode. |
| `TRL ON/OFF` | dark-slate / dark-red toggle | Master trail on/off. When OFF, trail and TRL NOW are both inactive. |
| `TRP ON/OFF` | dark-slate / dark-red toggle | Trap Detector on/off. See §14.6. |
| `BE ON/OFF` | dark-slate / dark-red toggle | Break-even auto-lock on/off. See §14.4. |

---

## 11. Properties Reference

| Group | Property | Default | Range | Notes |
|---|---|---|---|---|
| **1 - Risk** | `SlPoints` | 18 | 1–500 | Points |
| | `TpPoints` | 50 | 1–500 | Points |
| | `MaxDailyLossDollars` | 1000 | 500–20000 | $ per session |
| | `MaxDailyProfitDollars` | 1000 | 500–50000 | $ per session |
| | `MaxTradesPerDay` | 4 | 0–999 | 0 = unlimited |
| | `Contracts` | 1 | 1–10 | Per entry |
| | `MaxContracts` | 4 | 1–20 | Total position limit |
| | `AllowMultiEntryPerBar` | **true** | — | **When false, one entry per bar max** |
| **2 - Mode** | `AutoMode` | false | — | Toggle strategy auto-entry |
| | `AutoStrategy` | 2 | 0–2 | 0=M+V, 1=KLB, 2=Auto |
| | `MinSignalConfidence` | 55.0 | 20–100 | % threshold |
| | `EntryDelaySeconds` | 1 | 0–60 | Cooldown between entries |
| **3 - Trail/SL** | `TrailEnabled` | true | — | — |
| | `TrailActivationPoints` | 5 | 1–100 | Profit before trail starts |
| | `TrailAtrMultiplier` | 1.5 | 0.5–5.0 | ATR component |
| | `SmartTrailBacktrackTicks` | **4** | 0–12 | **MM avoidance — 0 to disable** |
| | `BreakevenAtPoints` | 8 | 2–50 | Lock SL at entry once hit |
| | `BreakevenEnabled` | **true** | — | **Master BE toggle; dashboard BE button mirrors** |
| | `JumpSlPercent` | 50 | 10–95 | Jump SL button move % |
| | `SlTpAdjustStep` | 5 | 1–50 | Dashboard ±step |
| **4 - Smart Logic** | `EnableTrapDetector` | true | — | — |
| | `OrderFlowFilterEnabled` | true | — | Live only |
| **5 - Indicators** | `EmaPeriodFast` | 9 | 3–50 | — |
| | `EmaPeriodSlow` | 21 | 10–200 | — |
| | `RsiPeriod` | 14 | 5–30 | — |
| | `AtrPeriod` | 14 | 5–30 | — |
| | `HtfEmaPeriod` | 45 | 10–200 | HTF structure EMA |
| **6 - Hours** | `TradingHoursEnabled` | true | — | — |
| | `TradingStartTime` | 93000 | 0–235959 | HHMMSS |
| | `FlattenTime` | 160000 | 0–235959 | HHMMSS |
| **7 - Display** | `ShowEma` | true | — | EMA 9/21 on chart |
| | `ShowVwap` | true | — | VWAP line on chart |
| **8 - Diagnostics** | `EnableDiagLog` | false | — | CSV to `~/NinjaTrader 8/` |

---

## 12. Backtest Validation Plan

1. **Keep `Mm_ATM_v4.cs` intact** — reference baseline (do not modify)
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

### 14.1 — Dashboard Resize & Width Fix

**Date:** April 25, 2026  
**Problem:** Dashboard `Border` was hard-coded at `Width = 290`. The ScrollViewer's scrollbar (~17 px) consumed ~17 px of that, leaving only ~253 px visible — not enough to show both `−` and `+` adjustment buttons. The `+` button was visually cut off and effectively un-clickable. The window could also not be resized.

**Fix:**
- Default width raised: **290 → 340 px**
- Default max-height raised: **700 → 720 px**
- `dashOuterBorder` and `dashScroller` stored as fields so resize handlers can mutate them
- **Bottom-right resize grip** (`↙ resize ↘`) added as draggable `TextBlock` at bottom of outer StackPanel
  - `Cursor = SizeNWSE`; captures mouse on press, releases on mouse-up
  - Drag updates `dashOuterBorder.Width` and `dashScroller.MaxHeight` live
  - Width clamped: **280–700 px**; Height clamped: **400–1 400 px**
  - Persists to `dashWidth` / `dashHeight` fields for the session (reset to defaults on strategy disable/enable)
- Adjust row components widened: label **56 → 60 px**, value column **130 → 150 px**, buttons **32×26 → 36×28 px**

**Fields added:** `dashOuterBorder`, `dashScroller`, `dashResizing`, `dashResizeStart`, `dashResizeStartW/H`, `dashWidth = 340`, `dashHeight = 720`

---

### 14.2 — Trail Diagnostics (EnableDiagLog gated)

**Date:** April 25, 2026  
**Problem:** Trail appeared to not move. Root cause was that trail simply hadn't reached activation profit yet, but there was no visible feedback explaining why.

**Fix:** Added throttled `Print()` diagnostics (5-second minimum interval) throughout `MonitorAdaptiveTrail()`:

| Trigger | Output |
|---|---|
| First 2 bars after entry | `TRAIL diag: waiting bar-guard barsSinceEntry=N` |
| Profit below activation | `TRAIL diag: waiting profit X.XX / activation Y.YY pt (need Z more)` |
| Trail activated | `TRAIL ACTIVATED profit=… dist=… tier=…` (**always printed**) |
| Trail price moves | `TRAIL MOVE X.XX -> Y.YY (price=… off=… tier=… maxProf=…)` (**always printed**) |
| Trail stable (no new high) | `TRAIL diag: stable trail=… cand=… price=… off=… (cand<=trail → hold)` |
| Trail hit | `TRAIL HIT LONG/SHORT @ …` (**always printed**) |

The "stable" message is the key one — it proves the engine is running and shows the computed candidate vs current trail. Enable via `EnableDiagLog = true` (Group 8).

**Field added:** `lastTrailDiagTime`

---

### 14.3 — Manual Trail Control (TRL NOW / Trail ±pt)

**Date:** April 25, 2026  
**Rationale:** The auto-trail activation threshold (`TrailActivationPoints = 5 pts`) may not trigger in time when a trader has already captured profit and wants to protect it immediately. Two controls were added.

#### TRL NOW button
- Dashboard button (row 5, magenta), enabled only when in a live position with `trailEnabled = true`
- Calls `RequestTrailActivate()` → `pendingTrailActivate` flag → `ActivateTrailManual()` on data thread
- Places trail at `GetTrailDistance()` away from current bid (long) or ask (short)
- Safety: trail is floored at `originalSlPrice` — never places trail worse than original SL
- Sets `manualTrailMode = true` → auto-ratchet paused (no overwrite by algorithm)
- Logs: `TRAIL ACTIVATED MANUAL @ X.XX (price=… dist=…pt profit=…pt)`

#### Trail ±pt adjust row
- New `MakeAdjustRow("Trail ±pt:", …)` between Jump% and the separator
- `−` button: tightens by `trailNudgeStepPoints` (default **1 pt**) — moves trail toward price, locking more profit
- `+` button: loosens by `trailNudgeStepPoints` — moves trail away from price, giving trade more room
- Activates `manualTrailMode = true` on first nudge (also activates trail if not yet active)
- Distance label shows live: `X.X pt | Y tk` plus `M` suffix when in manual mode; `— (waiting)` when trail not yet active; `OFF` when `trailEnabled = false`
- Safety clamps:
  - Tighten: trail cannot reach at-or-past current price (no self-stop)
  - Loosen: trail cannot go beyond `originalSlPrice` (no extra risk beyond original SL)

**Manual mode behavior:** When `manualTrailMode = true`, `MonitorAdaptiveTrail()` skips all auto-ratchet computation and only runs the hit-detection check (`price ≤ trailPrice` → exit). The invisible exit mechanism is **identical** — same field, same `ExitLong/Short` market order, same chart line.

**To resume auto-ratchet:** `manualTrailMode` is reset to `false` only when `ArmHiddenStops()` is called (new trade arm). There is no manual "go back to auto" button — re-entering the trade resets it.

**Fields added:** `pendingTrailActivate`, `pendingTrailNudgePoints`, `trailNudgeStepPoints = 1.0`, `manualTrailMode`, `lblTrailDistVal`, `btnTrailNow`  
**Methods added:** `ActivateTrailManual()`, `NudgeTrailDistancePoints(int)`, `RequestTrailActivate()`, `RequestTrailNudge(int)`

---

### 14.4 — Break-Even Master Toggle (BE ON/OFF)

**Date:** April 25, 2026  
**Problem:** `BreakevenAtPoints = 8` was triggering too early in some sessions, moving SL to entry before the trade had room to breathe.

**Fix:** Added a master `breakevenEnabled` switch:

- **`BE ON / BE OFF` button** added to dashboard row 5 (dark-slate / dark-red)
- When `OFF`, the entire BE-lock branch in `MonitorHiddenStops()` is bypassed
- Tooltip: "Break-Even lock — when ON, moves SL to entry once profit reaches the BE-Trigger setting (Group 3). Turn OFF to let the trade run without auto-BE."
- `BreakevenEnabled` NinjaScript property added to Group 3 - Trail / SL (default `true`)

**Recommendation:**
| Situation | Suggested setting |
|---|---|
| Trending NQ session (Open Drive) | BE ON, raise trigger to 14–18 pt |
| Choppy / range session | BE OFF, rely on trail |
| Running with `manualTrailMode` | BE OFF (trail controls SL directly) |
| High-confidence runner (`T3-Runner`) | BE OFF — `runnerModeActive` already skips BE, but belt-and-suspenders |

**Fields added:** `breakevenEnabled`, `btnBeToggle`  
**Property added:** `BreakevenEnabled` (Group 3 - Trail / SL, default `true`)

---

### 14.5 — Allow Multiple Entries Per Bar

**Date:** April 25, 2026  
**Background:** Bug fix A6 in the original v5 rewrite added `enteredThisBar` (reset on `IsFirstTickOfBar`) to prevent double-fills on the same bar — a real bug that caused over-sized positions.

**Issue:** The guard also blocked **intentional** re-entries on the same bar (e.g. quick fill-and-reverse, or adding to a winner after a partial close on the same bar).

**Fix:** `enteredThisBar` guard is now conditional:

```csharp
// Before:
if (enteredThisBar) return false;

// After:
if (enteredThisBar && !allowMultiEntryPerBar) return false;
```

- **`AllowMultiEntryPerBar`** property added to Group 1 - Risk (default **`true`** = ALLOW)
- **Default is permissive** — behavior matches user expectation for manual trading
- Set `false` to restore the strict single-entry-per-bar discipline for auto-mode
- Note: all other gates still apply (`MaxTradesPerDay`, daily-loss halt, `KILL`, `pendingExit`)

**Field added:** `allowMultiEntryPerBar`  
**Property added:** `AllowMultiEntryPerBar` (Group 1 - Risk, Order 8, default `true`)

---

### 14.6 — Trap Detector Explained (TRP ON / TRP OFF)

**Date:** April 25, 2026  
**Context:** User question — "what is the TRP ON button?"

**`TRP ON` = Trap Detector** — a bar-close pattern detector for market-maker stop-hunt behavior.

**How it works:**
1. Each bar close, `MonitorTrapDetector()` computes a `trapScore` from:
   - Adverse price movement (how far price moved against the trade)
   - EMA fast/slow conflict with trade direction
   - Above-average volume on the adverse move
2. **Stop-hunt spike detection:** if price penetrated the 5-bar structural low/high but *closed back inside* the prior bar's range → `stopHuntSuspendBars = 4` (4-bar window opens)
3. During the suspend window: trail is allowed to **back-track** (loosen) up to `SmartTrailBacktrackTicks` so the MM spike doesn't sweep the trail
4. **Trap escape exits:**
   - `trapScore ≥ 65 && adverse > 10 pts` → immediate market exit (priority 3a)
   - `trapScore ≥ 50 && adverse > 5 pts for 2 consecutive bars` → graduated exit (priority 3b)
5. Dashboard `Trap:` label shows live score + bars-in-trade

**When to keep it ON:** NQ liquid sessions (9:30–11:00 AM, 1:30–2:30 PM) where MM stop-hunt activity is highest. The stop-hunt backtrack and graduated escape together reduce "swept-and-reversed" losses.

**When you might turn it OFF:** Low-volatility range sessions, or backtesting on higher timeframes where bar-close stop-hunt patterns are less meaningful.

**Tooltip added** to dashboard `TRP ON` button for quick in-session reference.

---

## 13. Known Differences vs v4

| Area | v4 behavior | v5 behavior |
|---|---|---|
| Strategies | 4 (0–3) | 2 + Auto (0–2) |
| ORB state | Full ORB tracking | Open-Type classification (G3) only |
| Filters | 20, evaluated sequentially | 12, single pass with penalty floor |
| Exit subsystems | 5 competing | 1 ordered priority list |
| Trail bookkeeping | Multiple entry points, DCA trail | Single arm per fill |
| Phantom contracts | Possible on canceled limits | Fixed (deferred to OnOrderUpdate.Filled) |
| Trail in stop-hunt | Ratchets into spike | Backs off `SmartTrailBacktrackTicks` |
| Manual trail control | None | `TRL NOW` button + `Trail ±pt` nudge row; `manualTrailMode` pauses auto-ratchet |
| Break-even toggle | Always on | `BE ON/OFF` button + `BreakevenEnabled` property |
| Same-bar re-entry | Blocked (A6 fix) | Configurable: `AllowMultiEntryPerBar` (default `true` = allow) |
| Dashboard width | Varies (v4 clutter) | Fixed 340 px default, user-resizable via grip (280–700 px) |
| Trail diagnostics | None | Throttled `Print()` via `EnableDiagLog` (waiting / stable / move / hit) |
| Manual signal display | Bull/Bear % only | Explicit 5-level signal label + reason |
| Order-flow | None | 30-sec tape delta (live only) |
| Liquidity void | Not detected | 3-bar 2×ATR detector |
| Open-type | Not classified | RTH Open-Drive classification |
| Properties | ~30 / 12 groups | ~28 / 8 groups |
| Code lines | ~4,700 | ~1,800 |



---

## 14.7 � Live Manual-Trader Hardening (Apr 25, 2026)

Set of fixes after live NQ paper-trading exposed UX + correctness gaps.

### Daily Profit / Loss � DOES NOT KILL THE STRATEGY
**Behavior:** When the live or realized PnL crosses `MaxDailyProfitDollars` or `-MaxDailyLossDollars`, the strategy:
1. Closes the open trade (`ExecuteFlatten`)
2. Sets `dailyProfitHit` / `dailyLimitHit` so further entry attempts are *blocked*
3. Displays a prominent banner on the dashboard:
   - Profit: ?? gold "DAILY PROFIT TARGET $X � trade closed. Disable+Enable to resume."
   - Loss:   ? red  "DAILY LOSS LIMIT $X � trade closed. Disable+Enable to resume."
4. Logs a clarifying line: "closing trade, NOT killing strategy. Disable+Enable to resume."

The strategy itself stays in `State.Realtime` � `Print` and dashboard still update. To resume trading the same session, the user toggles the strategy off/on (which re-runs `State.DataLoaded` and resets `dailyLimitHit / dailyProfitHit / emergencyKillActive` to `false`).

### TP draw distance + TP-not-firing-in-runner-mode (FIX)
**Bug observed:** Label "TP 50pt | 200tk | $1000" but the green horizontal line was actually at `entry + 18pt`. Price walked through it without triggering exit; only the trail eventually closed the trade.

**Root cause:**
1. `ArmHiddenStops` clamps `hiddenTargetPrice` to `prevDayHigh - 1tk` (or `prevDayLow + 1tk`) when the prev-day level sits between entry and the configured TP. The visual line moved, but the label was reading the *original* `tpPoints` setting (50), not the actual line.
2. In runner mode (HTF agrees), `MonitorHiddenStops` uses `effTp = 500pt` and **bypasses the TP comparison entirely** (`&& !runnerModeActive`). So when the prevDay clamp pulled the runner's TP back to entry+18, the line existed but the gate was off.

**Fix:**
- New field `tpClampedByPrevDay` set in `ArmHiddenStops` and `ResizeHiddenStops` whenever a prevDay level pulls TP in.
- TP exit gate now: `priceLong >= hiddenTargetPrice && (!runnerModeActive || tpClampedByPrevDay)`. So a clamped runner TP fires; an un-clamped 500pt runner TP still rides the trail.
- `DrawChartAnnotations` rewritten to compute `slDistPts` and `tpDistPts` from the **actual** `hiddenStopPrice` / `hiddenTargetPrice`, not from `slPoints` / `tpPoints` properties. Adds `� PD` suffix on the TP label when prev-day-clamped, and `+` sign on SL label when SL is in profit (post-Jump SL).

### SL +/- after Jump SL (FIX)
**Bug:** After `JUMP SL` moved the SL into profit, pressing the SL `-` button on the dashboard did nothing.

**Root cause:** Old buttons did `slPoints -= step; ResizeHiddenStops()`. `ResizeHiddenStops` recomputes from `entry - slPoints*tickPt`. After Jump SL had stored `slPoints` as a *price-distance* (not entry-distance), the formula produced a target either nonsensical or below the current SL � and `breakevenLocked = true` (set by Jump SL) blocked any relaxation.

**Fix:**
- New thread-safe handler `RequestSlNudgePoints(int dPts)` enqueues `pendingSlNudge` (signed); `ProcessPendingButtons` calls `NudgeSlPricePoints(n)`.
- `NudgeSlPricePoints` operates **directly on `hiddenStopPrice`** in price space:
  - `-` (`dPts < 0`): WIDEN � SL moves further from price (more breathing room). Allowed regardless of `breakevenLocked`. Also widens `originalSlPrice` so trail backtrack respects the new floor.
  - `+` (`dPts > 0`): TIGHTEN � SL moves toward price. Clamped to `price - 1tk`. Sets `breakevenLocked = true` so auto-BE doesn't undo it.
- After move, `slPoints` is reset to the actual distance from current price (matches Jump SL's convention) and dashboard refreshes immediately via `DrawChartAnnotations`.
- Sanity floor: SL never beyond `entry � 200pt` from current trade.

### Trail � extra-aggressive on huge profit
Two new ladder rungs added to `MonitorAdaptiveTrail`:

| `trailMaxProfitPts` vs activation | Multiplier on `curDist` | Tier name |
|---|---|---|
| `= 4� activation` | `� 0.70` | T3-Runner (existing) |
| `= 6� activation` | additional `� 0.75` (cumulative � 0.525) | T4-Big (new) |
| `= 8� activation` | hard cap `min(curDist, max(1pt, ATR�0.25))` | T4-Big |

Tier-floor table now includes `T4-Big` at **65 % of `trailMaxProfitPts`** (vs 50 % for T3). On a 60-pt runner this locks ~39pt instead of 30pt.

Manual nudges (`Trail �pt`) keep flowing through the same ratchet � `manualTrailOffsetPoints` is added every pass, and tightening (`-`) takes effect *immediately* in the same handler.

### Spec doc + .md as living history
**Convention going forward:** every behavioral or property change appends a numbered subsection here (14.x). Future-recommended enhancements are listed with status `[planned]` so they survive across sessions.

### [planned] Future improvements derived from this session's log analysis
- Persist daily PnL across NinjaTrader restarts (currently resets on `DataLoaded`)
- Add `RESET DAILY` button on dashboard that flips `dailyLimitHit / dailyProfitHit / emergencyKillActive` to false without requiring strategy re-enable
- Add `auto-tighten on N consecutive losses` (e.g. after 2 losses, halve `aggressiveTrailMaxAtrFactor` for 1 hour)
- Add `auto-widen on N consecutive wins` (let winners run further)
- Add `partial profit at 1R` toggle � close half at `1� slPoints` so worst case is BE on remainder
- Add a `DOUBLE` button that doubles current `Qty` on conviction signal (already throttled by `MaxContracts`)
- Heuristic to skip TP-clamping by prevDay during high-ADX trend days (clamp wastes profit when trend is breaking through)
- Live diagnostic CSV: include `hiddenTargetPrice`, `hiddenStopPrice`, `trailPrice`, `tier`, `manualTrailOffsetPoints` per row � for post-trade replay

---

## 14.8 � Diagnostics, daily-reset & adaptive trail (this revision)

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
| `CME_MAINT` | CME maintenance auto-flatten (16:55�18:00) |
| `DAILY_LOSS` | LiveDailyPnLCheck loss-limit auto-flatten |
| `DAILY_PROFIT` | LiveDailyPnLCheck profit-target auto-flatten |
| `UNK` | Defensive fallback (should never appear) |

`lastExitReason` is cleared after consumption so a stale tag cannot bleed
into a later trade.

### 14.8.2 `ResetDailyOnRestart` (default ON)

In the `State.Realtime` branch, after `ResetSessionFlags()`, when this
property is ON the agent advances `processedTradeCount` past every existing
`SystemPerformance.AllTrades` entry **and** zeroes `dailyRealizedPnL` /
streak counters. Net effect: only fills that arrive **after** the restart
contribute to today's PnL � exactly matching the user's mental model that
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

- `AutoTightenOnLosses` (bool) + `AutoTightenLossN` (int 1�10, default 2)
  + `AutoTightenFactor` (double 0.1�1.0, default 0.5).
  When N consecutive losing trades occur, the agent multiplies the
  **base** `AggressiveTrailMaxAtrFactor` by `AutoTightenFactor` and writes
  `ADAPT_TIGHTEN` to the CSV. Tighter trail => locks profit faster on a
  bad-rhythm day.
- `AutoWidenOnWins` (bool) + `AutoWidenWinN` (int 1�10, default 3)
  + `AutoWidenFactor` (double 1.0�3.0, default 1.5).
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
- `HighAdxThreshold` (double 15�60, default 28).

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

## 14.10 � Smarter than MM/algos: TOD-SL, news blackout, sweep boost, DCA suppression

This revision adds four customization layers � every threshold/window/factor is a NinjaScript property so each user can tune to their account size and instrument.

### 14.10.1 Time-of-Day SL sizing  (Group `8 - Time-of-Day SL`)

The hidden SL distance now adapts to the time of day. Three windows (Open / Midday / Close), each with its own multiplier on the base `slPoints`. Eval order: **Open ? Close ? Midday** (first match wins). Outside any window the base SL is used unchanged.

| Window | Default times (HHMMSS) | Default mult | Rationale |
|---|---|---|---|
| Open    | 09:30:00 � 10:30:00 | **1.30** | Wider � opening expansion can wick 6-12pt before settling |
| Midday  | 10:30:00 � 14:00:00 | **0.80** | Tighter � chop, low-ATR; tighter SL preserves the small wins |
| Close   | 15:00:00 � 16:00:00 | **1.20** | Wider � power-hour whipsaws can sweep stops both directions |

Internal helper `GetEffectiveSlPoints()` returns `round(slPoints � mult)` clamped to a 2pt floor. It's called from `ArmHiddenStops` and both branches of `ResizeHiddenStops`. Wrap-around windows (start > end) are supported, e.g. you could define an overnight session window.

Properties:
- `TimeOfDaySlSizingEnabled` (bool, default ON).
- `SodOpenStart` / `SodOpenEnd` (HHMMSS) + `SodOpenSlMult`.
- `SodMiddayStart` / `SodMiddayEnd` + `SodMiddaySlMult`.
- `SodCloseStart` / `SodCloseEnd` + `SodCloseSlMult`.

### 14.10.2 News blackout window  (Group `9 - News Blackout`)

Block ALL entries (manual + auto) within � window-min of any time in a CSV list of HHMMSS times. The list is parsed once, cached as minutes-since-midnight; when `NewsBlackoutTimes` is changed via the UI the cache invalidates and re-parses on next entry attempt.

Default times (ET): **08:30** (CPI/PPI/NFP), **10:00** (ISM/JOLTS), **14:00** (FOMC). Default window: **�2 minutes**.

A `BLOCK_NEWS` row is written to the diag CSV every time an entry attempt is suppressed.

Properties:
- `NewsBlackoutEnabled` (bool, default ON).
- `NewsBlackoutTimes` (string, comma-separated HHMMSS).
- `NewsBlackoutWindowMin` (int 0�60, default 2).

### 14.10.3 Liquidity-sweep boost  (Group `10 - Liquidity Sweep`)

Classic MM stop-run reversal:
- **Bull sweep** = bar [-1] Low broke below the prior N-bar Low **AND** Close[-1] = that prior low + 0.3 � ATR (closed back above the swept level).
- **Bear sweep** = mirror image of above.

When detected, the **opposite-direction** signal is boosted in two ways:
1. `effMin` is reduced by `LiquiditySweepConfBoost` points (floor 35).
2. The `overLong` / `overShort` overextension veto is bypassed for that direction.

This lets the strategy *participate* in the kind of move where MMs sweep one side then reverse � exactly the inverse of getting trapped by it.

A `SWEEP_BOOST` diag row is written whenever the boost is active.

Properties:
- `LiquiditySweepBoostEnabled` (bool, default ON).
- `LiquiditySweepLookback` (int 5�200, default 30 bars).
- `LiquiditySweepConfBoost` (double 0�30, default 8.0).

### 14.10.4 DCA suppression on loss streak  (Group `11 - DCA Suppression`)

In `CanEnterTrade`, when the requested entry is **same-direction as the open position** and `consecutiveLosses = SuppressDcaLossN`, the add is blocked with a `DCA suppressed` status banner and a `BLOCK_DCA` diag row. The block applies to BOTH manual and auto entries (DCA on a losing day rarely ends well � preserve the capital, wait for a clean win to clear the streak).

Default: **2 consecutive losses** ? DCA blocked until next win clears the streak.

Properties:
- `SuppressDcaOnLossStreak` (bool, default ON).
- `SuppressDcaLossN` (int 1�10, default 2).

### 14.10.5 Diagnostic CSV additions

New `Action` tags introduced this revision:
- `BLOCK_NEWS` � entry attempt blocked by news blackout.
- `BLOCK_DCA` � same-direction add blocked by loss-streak suppression.
- `SWEEP_BOOST` � sweep detected and boost active for this bar.

(Existing 29-column header is unchanged; these tags use the existing `Action,Detail` slots.)
