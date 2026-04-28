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

---

## 15. Entry Blockers (`CanEnterTrade`) — Plain-English Reference

Every entry attempt (auto OR manual button) flows through `CanEnterTrade(label, isManual, direction)` in `Mm_ATM_v5.cs` ~line 1823. If any blocker fires, the dashboard shows **`<label> blocked: <reason>`** in orange/red and (where noted) writes a `BLOCK_*` row to the diagnostic CSV. Order matters — first match wins.

### 15.1 Hard blockers (apply to BOTH auto and manual)

| # | Blocker | Diag tag | Trigger | Why |
|---|---|---|---|---|
| 1 | **Pending exit** | — | `pendingExit == true` (an exit order is in flight) | Don't pile a new entry on top of an unresolved close. |
| 2 | **Daily limit** | — | `dailyLimitHit` OR `dailyProfitHit` | Daily loss cap or profit target was hit; trading is paused for the session (strategy stays loaded). |
| 3 | **KILL switch** | — | `emergencyKillActive` (you pressed KILL) | Hard stop until you reset. |
| 4 | **NEWS blackout** | `BLOCK_NEWS` | `newsBlackoutEnabled` AND inside ±`newsBlackoutWindowMin` of a scheduled news event | Volatility spike + spread widening = MM heaven, our edge collapses. |
| 5 | **Already entered this bar** | — | `enteredThisBar` AND `!allowMultiEntryPerBar` | Prevents duplicate fills on the same bar (toggle the property to allow stacking). |
| 6 | **Max trades / day** | — | `dailyTradeCount >= maxTradesPerDay` AND flat AND auto only | Caps overtrading. Manual bypasses. |
| 7 | **CME maintenance** | — | clock between 16:55 and 18:00 ET | Exchange settle window — orders flake. |
| 8 | **Outside auto hours** | — | auto only — clock outside `tradingStartTime`..`flattenTime` | Auto is gated by the configured session; manual is always allowed. |
| 9 | **Opposite-side reversal** | — | direction opposite to current open position | Not really "blocked" — it triggers `ExecutePartialClose` instead (closes the contrarian side). |
| 10 | **Max contracts** | — | adding same direction beyond `maxContracts` | Position-size cap. |
| 11 | **DCA suppression** | `BLOCK_DCA` | same-direction add AND `suppressDcaOnLossStreak` AND `consecutiveLosses >= suppressDcaLossN` | Don't average down into a losing streak — that's how accounts blow up. |
| 12 | **Entry cooldown** | — | seconds since last entry < `entryDelaySeconds` | Anti-spam between entries. |

### 15.2 Soft blockers (AUTO ONLY — manual buttons bypass since v14.17)

These are smart filters that protect the auto-strategy. Manual entries are treated as explicit human intent and skip them.

| # | Blocker | Diag tag | Trigger | Why |
|---|---|---|---|---|
| S1 | **CHOP** | `BLOCK_CHOP` | `chopFilterEnabled` AND `IsChoppy()` returns true. Tests: ADX collapsed, EMAs converged, recent close-range tight, OR tape fighting entry direction. | Choppy regime = high stop-out probability with low edge. The 03-20 chop-spiral autopsy showed all 7 losing shorts had every chop signature simultaneously. |
| S2 | **SL CLUSTER cooldown** | `BLOCK_SL_CLUSTER` | `slClusterCooldownEnabled` AND now < `slClusterCooldownUntil` (set after N stop-losses in a short window) | "Toxic regime" detector — pause new entries so we don't chain another loss. |
| S3 | **POST-WIN cooldown** | `BLOCK_POST_WIN` | `postWinSameDirCooldownEnabled` AND last winning exit was SAME direction within `postWinSameDirCooldownMin` (default 7 min) | Catches MM stop-runs that ramp price against us right after our trail kicked out, then continue trend in the original direction. |
| S4 | **DIR LOCKOUT** | `BLOCK_DIR_LOCKOUT` | `dirLockoutEnabled` AND now < per-direction lockout end (set after `dirLockoutLossN` losses on that side within `dirLockoutWindowMin`) | One direction is broken right now — only allow the opposite side until lockout expires. |
| S5 | **EXTENSION** | `BLOCK_EXTENSION` | `extensionFilterEnabled` AND `ATR >= extensionMinAtrPoints` AND `\|Close − VWAP\| / ATR > extensionMaxAtrFromVwap` (default 5×) | Don't chase late — over-extended price = high MM-stop-run probability. |

**Manual bypass policy (v14.17):** All five S1–S5 filters check `!isManual` first. Manual buttons are explicit human conviction and skip them. Hard blockers (15.1) still apply to manual.

### 15.3 Diagnostic-only "blocker" (not really a block)

| Tag | Where | Meaning |
|---|---|---|
| `CLAMP_SKIP` | `MonitorAdaptiveStops` at trade-arm time | Logged when the **prev-day H/L TP-clamp was bypassed** because `ADX >= highAdxThreshold` AND `skipPrevDayClampOnHighAdx` is on. NOT an exit and NOT a block — just a heads-up that the TP was left wider on a trend day so price could reach it. |

### 15.4 Auto-entry confidence gate (separate from `CanEnterTrade`)

Inside `EvaluateAutoEntry`, even when `CanEnterTrade` would pass, an entry must clear:
- `lastBullConfidence >= effMinL` (or bear ≥ `effMinS`)
- `!rsiDown` / `!rsiUp` (avoid against-momentum)
- `!overLong` / `!overShort` (overextension — relaxed by SWEEP boost)
- `!nearH` / `!nearL` (don't enter at session H/L)
- `!volSpike` (avoid volume-spike bars)
- `!htfBlockL` / `!htfBlockS` (HTF EMA must agree, if HTF filter on)
- `!tapeBlockL` / `!tapeBlockS` (tape delta must not be opposing > 0.3)

The **Sweep Boost** (§14.10) lowers `effMin` by `liquiditySweepConfBoost` (default 8.0 pts) AND clears `overLong/overShort` when a fresh bull/bear liquidity-sweep pattern prints — so a high-quality reversal can take a fade trade even when overextended. Logged as `SWEEP_BOOST`.

---

## 16. Exit Types — Complete Reference (`lastExitReason`)

Every exit sets `lastExitReason` (consumed in `OnExecutionUpdate` to build the `EXIT_WIN_*` / `EXIT_LOSS_*` diag row, then cleared). Below is the full taxonomy as wired in v14.19.

### 16.1 Hidden SL / TP family (in-memory, MM-invisible)

| Reason | Trigger | Notes |
|---|---|---|
| `SL` | Price crossed `hiddenStopPrice` | Stop is in-memory only — no `SetStopLoss()` call, so the MM order book never sees it. |
| `BE` | Same as `SL` but `breakevenLocked == true` | Loss is ~zero or a small win because BE was armed first. |
| `TP` | Price crossed `hiddenTargetPrice` | Hidden TP, MM-invisible. |
| `TP_PD` | Same as `TP` but `tpClampedByPrevDay == true` | Target was clamped just inside prev-day H/L (to avoid MM stop-run ladders sitting at those levels). |

### 16.2 Trail family — `TRAIL_<tier>`

The tier name is whatever `trailTierName` was at the moment the trail price was crossed:

| Tier | Condition that produced this tier |
|---|---|
| `TRAIL_T1-BE` | Profit ≥ 1.5×activation, no HTF agree → trail floor at +1 tick (locks BE) |
| `TRAIL_T2-Strong` | Profit ≥ 2.5×activation → floor 40% of peak |
| `TRAIL_T3-Runner` | Profit ≥ 4×activation → floor 45–50% of peak (50 if HTF agrees) |
| `TRAIL_T4-Big` | Profit ≥ 6×activation → floor 65% of peak (very protective) |
| `TRAIL_Aggr` | Trail started early via TRL NOW button — aggressive lock = max(2pt, 0.5×ATR) |
| `TRAIL_Aggr-Mode` | AGGR mode override active — fixed `aggrTrailDistPts` distance |
| `TRAIL_Runner` | HTF EMAs agree, default-tier runner (no peak floor yet) |
| `TRAIL_Active` | Default tier — trail armed, no special condition |
| `TRAIL_RUNNER` | **v14.19** — RUN button was ON; wide `1.5×ATR` (floor 4pt), no aggression multipliers / no tier floors |

### 16.3 Aggressive-exit family

| Reason | Trigger | Purpose |
|---|---|---|
| `AGGR_ADVERSE` | Within first `aggrAdverseMaxBars` AND adverse move ≥ `max(aggrAdverseMinPts, aggrAdverseAtrFactor × ATR)` AND we hadn't yet hit `aggrAdverseDisarmPeak` profit | Cuts losers fast before they mature. Runs at top of `MonitorHiddenStops` so it preempts the static SL. |
| `AGGR_PULLBACK` | After being up ≥ activation, retracement from peak ≥ `aggrPullbackAtrFactor × ATR` within first `aggrPullbackMaxBars` | Anti-MM-trap — stops a winner from being flipped into a loser by a stop-run. |

### 16.4 Reversal / trap family

| Reason | Meaning |
|---|---|
| `FAST_REV` | Fast-reversal exit — sharp counter-move detected before trail engages. Smarter than waiting for the static SL or the trail. |
| `TRAP_IMM` | **Immediate** trap — strong evidence of MM trap on entry bar; bail instantly. |
| `TRAP_GRAD` | **Gradual** trap — trap score accumulated over multiple bars then crossed threshold. |

### 16.5 Manual / system family

| Reason | Meaning |
|---|---|
| `FLATTEN` | User pressed FLATTEN (close current trade only, strategy stays enabled) |
| `CLOSE` | User pressed CLOSE-ALL (close all positions for the instrument) |
| `CLOSE_ONE` | User pressed CLOSE-ONE (reduce by 1 contract) |
| `KILL` | User pressed KILL SWITCH (close + disable strategy) |
| `AUTO_FLATTEN` | End-of-session auto-flatten before settle |
| `CME_MAINT` | Auto-flattened at CME maintenance window (16:55–18:00 ET) |
| `DAILY_LOSS` | `dailyRealizedPnL <= -maxDailyLossDollars` — daily loss cap fired |
| `DAILY_PROFIT` | `dailyRealizedPnL >= maxDailyProfitDollars` — daily profit target fired |
| `UNKNOWN` | Initial value (never appears for a real exit) |

### 16.6 How to read the diag CSV

`OnExecutionUpdate` wraps the reason with the trade outcome:
- `EXIT_WIN_<reason>` if `last.ProfitCurrency > 0`
- `EXIT_LOSS_<reason>` otherwise

So a row with action `EXIT_WIN_TRAIL_RUNNER` literally means: a winning trade closed by the **Runner Mode** wide trail (v14.19 RUN button was ON for that trade). Inversely, `EXIT_LOSS_AGGR_PULLBACK` means the AGGR pullback exit fired and gave back enough peak profit to close net-negative.

---

## 17. Top-Dashboard Signal Strip & Order-Flow Tape

The strip at the top of the dashboard is the **Manual Trade Signal** — a single one-glance recommendation built from confidence + HTF + EMA + tape + EMA-cross momentum. It is **informational only** and does not place orders; it tells you whether right now is a good moment to press BUY or SELL.

### 17.1 The labels

| Display | Color | Meaning |
|---|---|---|
| `▲ STRONG BUY` | Bright green | Long signal with strong confluence (see formula in §17.5) |
| `↑ BUY` | Lime | Long signal — basic gates passed |
| `● WAIT` | Gray | Not enough alignment — sit on hands. Reason text shows why. |
| `↓ SELL` | OrangeRed | Short signal — basic gates passed |
| `▼ STRONG SELL` | Red | Short signal with strong confluence |

### 17.2 The supporting metrics (right of the signal)

- **`Bull: 67%` / `Bear: 33%`** — the two confidence scores from the 12-filter system (`lastBullConfidence`, `lastBearConfidence`, range 0–120, clamped). Each side accumulates contributions from F1–F12 (EMA stack, RSI, ADX, sweep, EMA-cross momentum, tape, void, open-type, etc.). The dominant side drives the signal direction. Opacity dims when below `minSignalConfidence`.
- **`conf=70`** — `dom = max(Bull%, Bear%)`. The same number as the higher of the two percentages above.
- **`htf=+1` / `0` / `-1`** — Higher-timeframe bias from `UpdateHtfBias` (5-min EMA stack + session-open vs ATR + 45-EMA voting). +1 bullish HTF, −1 bearish, 0 mixed/neutral.
- **`tape=+0.32`** — current order-flow tape delta (see §17.3).

### 17.3 The "Tape Δ" label

Format: `Tape Δ: +0.32  ▲ buyers` (or `▼ sellers` / `● balanced` / `off`).

#### How the value is computed (`OnMarketData` ~line 2774)

- **Live-only** — returns immediately if not in `State.Realtime` or if `OrderFlowFilterEnabled = false`.
- Rolling **30-second window** of every Last-trade tick:
  - Trade printed `>= Ask` → counted as **aggressor BUY** (`tapeAskVol += volume`)
  - Trade printed `<= Bid` → counted as **aggressor SELL** (`tapeBidVol += volume`)
  - Mid-price prints (between Bid and Ask) are ignored — they're not aggressive.
- `cachedTapeDelta = (askVol − bidVol) / (askVol + bidVol)` → range **−1.0 to +1.0**.
- Window resets every 30 seconds.

#### Label thresholds (line 3747)

| Tape Δ | Label | Color | Interpretation |
|---|---|---|---|
| `> +0.15` | `▲ buyers` | LimeGreen | Aggressors lifting offers — bid-side buying pressure |
| `−0.15` to `+0.15` | `● balanced` | Gray | Roughly equal aggression both sides — no edge |
| `< −0.15` | `▼ sellers` | OrangeRed | Aggressors hitting bids — sell pressure dominant |
| (off) | `Tape Δ: off` | Gray | Order-flow filter disabled OR not in live mode (backtest/replay) |

### 17.4 How the tape is USED across the strategy

The tape feeds six independent decisions:

| # | Where | Effect |
|---|---|---|
| 1 | **Dashboard display** (line 3747) | Color + arrow on the Tape Δ label |
| 2 | **Confidence boost — F10** (`CalculateSignals` ~line 2971) | If `tape > +0.25` → `Bull% += 10, Bear% −= 5`. If `tape < −0.25` → `Bear% += 10, Bull% −= 5`. |
| 3 | **Auto-entry block** (`EvaluateAutoEntry` ~line 1975) | `tapeBlockL` if `tape < −0.30` (long blocked); `tapeBlockS` if `tape > +0.30` (short blocked). |
| 4 | **Manual signal gate** (`UpdateManualSignal` ~line 3087) | `tapeOk` requires `tape ≥ −0.1` for long, `tape ≤ +0.1` for short. Failed → `WAIT — tape-against`. |
| 5 | **CHOP filter test #4** (`IsChoppy` ~line 1681) | If `chopBlockOppositeTape` AND `\|tape\| ≥ chopOppositeTapeMin` AND tape opposes direction → blocks auto entry as `BLOCK_CHOP — tape against long/short`. |
| 6 | **Fast-reversal exit** (`MonitorFastReversal` ~line 1506) | Tape against open direction (`> 0.15` magnitude) is one input to the reversal score that fires `FAST_REV` exits. |

So the tape is a **trade quality control** at three different stages: pre-entry confidence (boost or block), entry-time gate (manual signal & CHOP), and in-trade reversal detection.

### 17.5 How the BUY / STRONG BUY decision is built (`UpdateManualSignal` ~line 3078)

**Step 1 — Direction & dominance:**
- `dir = +1` if `Bull% > Bear%`, `−1` if `Bear% > Bull%`, else `0`
- `dom = max(Bull%, Bear%)`

**Step 2 — Five gates must ALL pass (else WAIT):**
| Gate | Condition |
|---|---|
| `meetsConf` | `dom ≥ MinSignalConfidence` (default ~55) |
| `htfOk` | HTF bias agrees with direction (or HTF = 0) |
| `emaOk` | 1-min Fast EMA on the right side of Slow EMA for `dir` |
| `tapeOk` | tape not aggressively against (≥ −0.1 long, ≤ +0.1 short, or filter off) |
| `noVoid` | not currently inside a liquidity-void window |

If any gate fails → `WAIT — <reason1> <reason2> ...` (e.g. `WAIT — low conf htf-against tape-against liq-void`).

**Step 3 — Strength upgrade (BUY → STRONG BUY):**

A "confluence" score is built (max 5):
- +1 if HTF agrees
- +1 if EMA agrees
- +1 if tape agrees
- +1 if a **fresh EMA cross** in the same direction within the last 3 bars
- +1 if a **confidence flip** just occurred AND previous dominant matched current direction

```text
level = (dom ≥ MinSignalConfidence + 15  AND  confluence ≥ 4)
        ? dir × 2   // STRONG
        : dir       // normal
```

So **STRONG BUY** = `dom ≥ ~70` AND at least 4 of the 5 confluences align. Plain `BUY` = the 5 basic gates passed but the strength bar wasn't cleared.

The reason string format is:
```
STRONG BUY conf=78 htf=+1 tape=+0.42
BUY conf=58 htf=0 tape=+0.05
WAIT — low conf htf-against
```

### 17.6 Practical reading guide

| You see | Do |
|---|---|
| `STRONG BUY conf=78 htf=+1 tape=+0.42  ▲ buyers` | Press BUY (or let auto take it). Multiple systems aligned. High-quality moment. |
| `BUY conf=58 htf=0 tape=+0.05  ● balanced` | Marginal. Auto might take it; you may want to wait one more bar for confirmation. |
| `WAIT — tape-against` | Your direction has aggressive opponents on tape. Don't fade without a reason — wait for tape to roll. |
| `WAIT — low conf htf-against` | Fast and slow timeframes disagree. Common in chop. CHOP filter is probably also blocking auto. |
| `WAIT — liq-void` | Last 3 bars had range > 2×ATR (extreme bars). Wait for liquidity to return — spreads are wide and stops are unreliable. |
| `Tape Δ: off` | You're not in live mode, or the order-flow filter is disabled in properties. Tape contribution is neutral. |

### 17.7 Tunable properties

| Property | Default | Effect |
|---|---|---|
| `OrderFlowFilterEnabled` | `true` | Master switch. When OFF, tape neither displays nor influences anything. |
| `MinSignalConfidence` | ~55 | Threshold the dominant side must clear for any BUY/SELL recommendation (vs WAIT). STRONG requires `+15` more. |
| `ChopBlockOppositeTape` | `true` | Whether opposite-side tape can trigger a CHOP block. |
| `ChopOppositeTapeMin` | ~0.20 | `\|tape\|` magnitude required to trigger CHOP test #4. |

---

---

## 19. v5 Active Workplan — Apr 28, 2026 onward

> **Status:** v5 14.20 shipped (enhanced diag log: 39 cols, toggle logging, 10-min heartbeat). Items below are queued and will be implemented in this order in upcoming sessions. Each ships as its own toggle, default OFF where applicable, with no breaking changes to existing v5 behavior.
>
> *(For long-term v6 ideas — pattern memory, MM playbook, Kelly sizing, etc. — see §18 below.)*

### Implementation order (agreed Apr 28, 2026)

| # | Item | Scope | Why this order | Target version |
|---|---|---|---|---|
| 1 | **TP-line render bug + manual T+/T- on auto-mode TP** | Bug fix | Live UX issue; small | v5 14.21 |
| 2 | **Drag SL/TP/Trail lines on chart** | UX feature | High value, clean addition | v5 14.22 |
| 3 | **Buy/Sell Limit ±N tick offset + TTL + dashed line** | New entry mode | Independent feature | v5 14.23 |
| 4 | **AGGR-L1 / AGGR-L2 aggression levels + PACE indicator** | New dashboard buttons | High profit potential | v5 14.24 |
| 5 | **Lead-signal entry + trend-entry mode** | Entry timing | Needs a few days of v14.20 logs first | v5 14.25 |
| 6 | **NinjaRenko follow mode (Off / On / Renko)** | Big feature | Largest scope; last in v5 | v5 14.26 |

Items 2 and 3 from the original 7-item list (hide bot labels / stealth mode) are **deferred to v6** per user decision.

### 19.1 Item 1 — TP line render bug (v5 14.21)

**Symptoms (user-reported Apr 27):**
- TP line sometimes does not appear on chart for auto OR manual entries (random).
- When TP line *does* appear, the dashboard `TP +/-` buttons sometimes do nothing in auto mode (manual nudges not propagating to redrawn line).

**Investigation plan:**
1. Find the chart-draw call that renders the TP line. Suspect candidates: `Draw.Line`/`Draw.HorizontalLine` in the position-tracking section.
2. Check whether the draw is gated by a stale flag (e.g. `stopsArmed`, `manualTrailEarlyStart`) that does not refresh after `T+/T-` press.
3. Check whether `manualTpOffsetPoints` (or equivalent) is actually consumed in the auto path — manual T+/T- may only be wired to the manual order route, missing the auto route.
4. Reproduce: enter auto trade, press TP+ on dashboard, verify diag log shows `TP_NUDGE` row and `HiddenTP` column changes; check chart line refreshes.

**Fix scope:**
- Unify TP-line render to a single helper called every tick when `Position != Flat` and `hiddenTargetPrice > 0`.
- Make `TP+/-` buttons always mutate `hiddenTargetPrice` (and the line) regardless of entry source (auto vs manual).
- Add `WriteDiagRow("TP_NUDGE", "by=...new=...source=auto/manual")` when buttons fire.

### 19.2 Item 2 — Drag SL/TP/Trail lines on chart (v5 14.22)

**Spec:**
- Replace existing `Draw.Line` calls with **draggable** `Draw.HorizontalLine` instances tagged `MmATM_SL`, `MmATM_TP`, `MmATM_TRAIL`.
- Behind a **DRAG ON / OFF** toggle button (default OFF) so chart panning never accidentally moves a line. *(Recommended over always-on.)*
- Each tick, poll the line's `Y` value; if it differs from the internal value by ≥ 1 tick, snap the internal value (`hiddenStopPrice` / `hiddenTargetPrice` / `trailPrice`) and write `WriteDiagRow("DRAG_SL", "old=X new=Y")` etc.
- **Trail-line drag:** dragging the trail freezes auto-ratchet (manual override) for the rest of the trade. A `MANUAL` badge appears in trail-tier display. Toggling the dashboard `TRL OFF -> ON` re-enables auto-ratchet (per user spec).

### 19.3 Item 3 — Buy/Sell Limit ±N tick offset (v5 14.23)

**Spec:**
- Beside the existing **BUY** / **SELL** dashboard buttons, add a small ±N tick spinner (default 0).
- N = 0 → market entry (current behavior).
- N > 0 → limit order at:
  - **BUY**: `Bid − N × TickSize` (waits for pullback)
  - **SELL**: `Ask + N × TickSize` (waits for spike)
- New **TTL spinner** (default 60s, 0 = no expiry). When the TTL elapses without fill, the working limit auto-cancels. Diag rows `LIMIT_PLACED` / `LIMIT_FILLED` / `LIMIT_TTL_CANCEL`.
- **Dashed price line** drawn on chart while limit is working (drag-able once Item 2 is in).

### 19.4 Item 4 — AGGR-L1 / AGGR-L2 + PACE indicator (v5 14.24)

**Aggression levels (toggle button cycles NORM → AGGR-L1 → AGGR-L2 → NORM):**

| Setting | NORM | AGGR-L1 | AGGR-L2 |
|---|---|---|---|
| `MinSignalConfidence` | 55 | 50 | 45 |
| `ExtensionMaxAtrFromVwap` | 5.0 | 6.0 | 8.0 |
| `PostWinSameDirCooldownMin` | 7 | 4 | 0 |
| `ChopFilterEnabled` | ON | ON | OFF |
| `MinAtrPointsToTrade` (NEW gate) | 4.0 | 3.0 | 2.0 |
| Entry-cooldown seconds | current | half | 0 |

**Auto-revert safety:** AGGR levels auto-revert to NORM when daily realized P&L drops below `−$X` (configurable; default `−$300`). Diag `AGGR_AUTOREVERT`.

**PACE indicator (default OFF, behind its own toggle):**
- Computed from: ATR(30) percentile vs last 5 sessions, bars-per-minute trade activity (from tape ticks), tape Δ swing magnitude, ADX strength.
- 4 states displayed as a colored badge top-right of dashboard:

| State | Color | Trigger | Behavior when `PACE ON` |
|---|---|---|---|
| `🐢 SLOW` | Gray | ATR < 40th pct & ADX < 18 | **Auto-pause** new auto-entries |
| `🟢 SAFE` | Green | ATR 40–70th pct, ADX 18–28 | Normal trading |
| `🟡 AGGRESSIVE` | Yellow | ATR 70–90th pct, ADX > 25 | Trending; favor longer holds |
| `🔥 VOLATILE` | OrangeRed | ATR > 90th pct OR sudden ATR spike | **Auto-tighten Runner Mode** |

When `PACE OFF` (default), badge is informational only.

### 19.5 Item 5 — Lead-signal entry (v5 14.25)

**Pre-work:** Analyze 3–5 days of v14.20 diag logs. Specifically:
- Count `SIGNAL` rows with `BUY/SELL` label that *did not* result in an `ENTRY_*` row within 30s.
- Identify what blocked them (CHOP, EXTENSION, low conf, EMA-cross-confirmation delay, etc.).
- Quantify: would lead-entry have produced more profit on those missed setups, or more whipsaws?

**Build (only after analysis confirms):**
- New **LEAD ON/OFF** toggle (default OFF).
- When ON, the moment manual signal flips to BUY/SELL with `dom ≥ MinSignalConfidence + 5`, queue an immediate auto-entry skipping EMA-cross-confirmation delay.
- Restricted to **trend regime** (ADX > threshold + EMA stack + HTF agree) to avoid lead-entries in chop.

### 19.6 Item 6 — NinjaRenko follow mode (v5 14.26 — biggest)

**Architecture:** Add Renko 64-tick / 16-offset as a **secondary data series** via `AddRenko()`, accessed via `BarsArray[2]`. Trades fire on the primary chart's prices; Renko is a signal/management overlay only.

**Configurable parameters:**
- `RenkoBrickTicks` (default 64)
- `RenkoOffsetTicks` (default 16)

**3-mode toggle button (default OFF):**

| Mode | Behavior |
|---|---|
| `RNK OFF` | No Renko influence (current strategy unchanged) |
| `RNK ON` | **Augment** — entries require BOTH standard signal AND Renko brick alignment |
| `RNK THRUST` | On 3 same-color bricks in a row, switch to wide RUNNER-style trail; otherwise behave like `RNK ON` |

**Within-brick scalping (the "stuck-in-bar" detection):**
- Track ticks-since-current-brick-open; display `Brick: 0:42` on dashboard.
- "STUCK" detection: no progress toward either side > N seconds AND tape balanced AND price oscillating in middle 50% of brick → enable scalp mode.
- Scalp logic: place tight limit BUY near brick low and tight limit SELL near brick high; close on opposite side touch. Cancels both when brick finally closes.
- **Trap protection:** if a third reversal happens within the brick OR ATR spikes, kill scalp mode and revert to bar-mode trail.

**Trail in Renko mode:**
- SL = previous closed brick's far edge − 1 tick.
- On trap detection (price reverses through brick mid) → tighten to "current brick mid + 2 ticks".
- On thrust (3 same-color bricks) → switch to RUNNER trail (1.5×ATR, no aggression mults).

**Dashboard additions:**
- Brick state badge: `■ ■ ■` colored last 3 bricks.
- `Brick Time: m:ss` (since current brick open).
- `Renko Mode: OFF / ON / THRUST` indicator.

#### 19.6.A Case study — Apr 28, 2026 morning rally (the missed +96 pts)

**Reference chart:** `NQ 06-26 / nnZaRenko 64`, ~07:30 → 08:55 ET. Big morning sell-off bottoms ~08:34 at ~27059, then a clean uptrend prints continuous green Renko bricks all the way to ~27155 by ~09:00.

**What v5 14.20 actually did (from `MmATM_v5_DiagLog_20260428.csv`):**

| Time | Close | Action | Bull | Bear | htfBias | Signal | Note |
|---|---|---|---|---|---|---|---|
| 08:34:09 | 27059.50 | EXIT_WIN_CLOSE | 0 | 0 | -1 | WAIT | Closed winning short ✅ |
| 08:34:42 | 27064.75 | EXIT_WIN_CLOSE | 0 | 0 | -1 | WAIT | Closed second winner ✅ |
| 08:35:30 | 27075.25 | SIGNAL | 10 | 20 | -1 | WAIT | Bottom in, but htfBias still −1 |
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

**Result:** **Zero `ENTRY_LONG` rows in the entire window.** The bot watched +95.75 NQ pts ≈ **+$1,915/contract** print without participating.

> ⚠️ **Important context (user note Apr 28):** the 08:34 → 09:00 window is **pre-RTH** (NQ regular session opens 09:30 ET). With **`HRS ON`** (the default), auto-entries are hard-blocked here regardless of signal quality. So the "miss" is only a miss in a parallel world where the user had toggled `HRS OFF` for the morning. The case study still stands as a profile of *what Renko thrust mode would do* once it is allowed to fire — the rally itself is the realistic, repeatable pattern.

Likely *additional* blockers that would still have applied even with `HRS OFF`: htfBias still recovering from −1, post-win same-direction cooldown after closing winning shorts, EMA-cross-confirmation delay, `EXTENSION` filter as price ran from VWAP.

**Why Renko 64/16 + THRUST mode would have nailed this:**

- 16 ticks = **4 NQ pts per brick**. The 96-pt rally = ~24 consecutive green bricks.
- 3-brick thrust trigger fires by ~08:40 (around 27083 → 27095). Even entering on the **3rd brick close** = ~27091, exit on opposite-color brick close (which never came until ~27155) = **~64 pts capture / +$1,280** with a single contract.
- Renko view collapses the choppy 1-min noise that kept `htfBias` and pullback filters whipsawing.
- THRUST trail sits at "previous brick far edge − 1 tick" = always ~5–9 pts behind. No wide-stop drama, no aggressive-pullback exits.

#### 19.6.B Concrete profit recommendations (built from this case)

These are the **specific rules** to implement when we ship v5 14.26. Each is a numbered acceptance test we will run against the Apr 28 log.

1. **Thrust entry (independent of standard signal)** — when `RNK THRUST` mode is selected, allow entry on the **close of the 3rd consecutive same-color brick** even if the standard 12+5 entry filters block (htfBias mismatch, post-win cooldown, EMA-cross delay, extension). Rationale: standard filters are tuned for 1-min bars and reject the early phase of clean Renko thrusts. Override is gated by:
   - `MinThrustBricks = 3` (configurable, 2–4)
   - All 3 bricks closed within `ThrustMaxMinutes = 8` (avoid stale "thrust" from sleepy session)
   - ATR(30) > `MinAtrPointsToTrade` (still need volatility)
   - htfBias ≠ opposite (≥ 0 for longs, ≤ 0 for shorts) — **0 is allowed** (this would have caught Apr 28)
   - Tape average over last 30s ≥ +0.15 for longs / ≤ −0.15 for shorts

2. **Brick-close trail (no time exit during thrust)** — once entered in THRUST, SL/trail = `previousBrickFarEdge − 1 tick` (long) or `previousBrickFarEdge + 1 tick` (short). **Disable** all of: AGGR_PULLBACK, peak-giveback %, time-stop, void-bar exit. Only re-enable on first opposite-color brick close. Rationale: Apr 28 had multiple shallow pullbacks (`Bull` dropped 93→55→52→45) that would have triggered standard exits while Renko bricks stayed solid green.

3. **Continuation re-entry on pullback brick** — if a long position is exited by a single red brick but the **next brick prints green and closes above the red brick's high**, allow immediate re-entry (one re-try only, must be within `ReentryWindowMinutes = 4`). Counts as part of original trade for cooldown tracking. Rationale: clean Renko thrusts often print one "noise red" mid-rally; we should not be locked out for the rest of the move.

4. **Within-brick scalp (stuck-bar mode)** — only active when **NOT** in an open thrust position and **NOT** in `RNK THRUST` mode (i.e. `RNK ON` only). Conditions:
   - Current brick has been open ≥ `StuckSeconds = 90`
   - Tape average over last 30s in `[-0.15, +0.15]` (balanced)
   - Price oscillating in middle 50% of brick (touched both `low+25%` and `low+75%` at least twice)
   - ATR not spiking above prior 5-bar average × 1.4
   
   Then place limit BUY at `brickLow + 1 tick` and limit SELL at `brickHigh − 1 tick`, qty = 1 each. First fill activates a 6-pt fixed target and 8-pt fixed stop. **Hard kill switch:** if a third intra-brick reversal happens or brick finally breaks on momentum (ATR spike), cancel both legs and revert.

5. **Brick-edge dragon-stop (Renko-aware trail outside thrust)** — when in any open position and `RNK ON`, override the standard 4-tier trail with: **trail = previous closed brick's far edge − 1 tick**, but only if it tightens (ratchets, never loosens). This replaces the existing AGGR_PULLBACK distance check with a structural one — MM cannot stop-hunt below a Renko brick boundary without reversing the brick.

6. **Thrust pause on doji-bricks** — if 2 consecutive bricks alternate color (G-R-G or R-G-R) within 3 minutes, mark thrust as **stalled** and revert to `RNK ON` rules. Don't exit, but stop counting bricks toward thrust streak.

7. **PACE integration (when both Item 4 + Item 6 ON):**
   - `🐢 SLOW`: don't fire thrust entries (low ATR rallies fizzle).
   - `🟢 SAFE` / `🟡 AGGRESSIVE`: full thrust rules.
   - `🔥 VOLATILE`: tighten thrust trail to `previousBrickFarEdge` (no extra tick of slack).

8. **Diag log additions for Renko mode:**
   - New columns: `BrickColor` (G/R), `BrickAgeSec`, `ThrustStreak` (consecutive same-color count), `RenkoMode` (OFF/ON/THRUST).
   - New `Action` rows: `BRICK_CLOSE` (Detail = `color=G hi=27091.25 lo=27087.25 streak=3`), `THRUST_ENTRY`, `THRUST_TRAIL_RATCHET`, `THRUST_STALL`, `BRICK_REENTRY`, `STUCK_SCALP_PLACE`, `STUCK_SCALP_FILL`, `STUCK_SCALP_KILL`.

**Acceptance test for v5 14.26 (run before declaring done):** Replay Apr 28, 2026 with `RNK THRUST` mode + thrust override ON. Must produce a `THRUST_ENTRY` row at the close of the 3rd green brick following the 08:34 bottom, hold (no AGGR_PULLBACK exit) until first red brick after the 09:00 top, and book ≥ **+50 NQ pts** ($1,000) realized on the move.

#### 19.6.C Implementation risk notes

- `AddRenko()` requires `Bars` index handling: any reference inside `OnBarUpdate` must guard with `if (BarsInProgress != 0) { /* update Renko state only */ return; }` or trades will fire on Renko closes.
- Brick close detection: use `BarsArray[2].LastPrice` change between `IsFirstTickOfBar` events on `BarsInProgress == 2`.
- Performance: Renko is `OnPriceChange`-equivalent — keep brick-state updates O(1).
- **Don't** route entry orders through the Renko series; always use primary `BarsArray[0]` for entry/exit so price ladders are correct.
- Save `RenkoBrickTicks` / `RenkoOffsetTicks` as user-properties so testers can experiment with 32/8 (faster) and 96/24 (slower) without recompile.
- **HRS interaction:** thrust override does **not** bypass the `HRS ON` time-window filter. If the user wants to capture pre-RTH thrusts (like Apr 28's 08:34 rally), they must explicitly toggle `HRS OFF` first. We may later add an opt-in `THRUST_BYPASS_HRS` sub-toggle (default OFF) for users who specifically want overnight/pre-market thrust trading — flagged as risky and logged separately.

### 19.7 Carry-over deferred items (already in v6 roadmap §18)

These were considered for v5 but bumped to v6 per user decision:
- **Item 2 / Stealth Mode** — hide bot-tell labels in execution `Name` column. Requires unmanaged-orders refactor (~200 lines). Stays as v6 feature K.
- Pattern-Memory Engine, MM Playbook Detector, per-hour self-tuning, Kelly sizing, etc. — all v6.

### 19.8 Working agreement

- One item per session, committed individually with message `v5 14.<NN>: <description>`.
- Each item ships behind a toggle (where applicable), default OFF unless user explicitly opts to default ON.
- Compile check (`get_errors`) before every commit.
- After commit, user reloads strategy in NT8 (F5) and tests. We move on only after user confirms.
- Diag log for each new feature: at least one `ACTION` row per state change, with enough Detail to post-mortem.
- Spec doc updated at the end of each implementation session.

---

## 18. v5 Stability Lock & v6 Roadmap — "Make MM open their mouth"

> **Status (Apr 27, 2026):** v5 14.19 is the **stable production line**. No more invasive changes go into v5 — only bug fixes and parameter tuning. All structural ideas below are deferred to **`Mm_ATM_v6.cs`** (new file, fork of v5 14.19).

### 18.1 Why fork to v6 instead of patching v5

v5 has earned its place: hidden SL/TP, smart trail with 4 tiers, sweep boost, runner mode, 5 soft blockers + 12 hard blockers, manual signal panel, tape integration, dashboard with live diag CSV. The +$1,710 day on 2026-03-20 proved the architecture works. Adding more layers risks regression. v6 lets us experiment safely while keeping v5 trading live.

### 18.2 v6 design pillars

1. **Adaptive over fixed** — replace hard-coded thresholds with rolling-window self-tuning (per-hour, per-regime).
2. **Multi-timeframe truth** — promote 5-min and 15-min from "filter inputs" to first-class signal sources with their own confidence.
3. **Pattern memory** — record what worked / what failed by setup signature (open-type × hour × ATR-bucket × HTF) and adjust min-confidence per signature.
4. **MM behavioral modeling** — explicitly model the MM playbook (stop-run levels, ladder pulls, fade-the-breakout) and trade *against* the predicted MM action, not just the chart.
5. **Risk-aware position sizing** — Kelly-like fraction based on rolling win-rate × R:R, not fixed contracts.

### 18.3 Concrete features queued for v6

#### A. Pattern-Memory Engine (priority 1)
- Record every trade with a **setup fingerprint**: `(hour, openType, atrBucket, htfBias, sweepPresent, vwapDistAtr, dirAgreesEma)`.
- Build a rolling 90-day P&L heat-map per fingerprint.
- At entry time, look up the fingerprint and **scale the size** (or block entirely) based on its historical edge.
- Diag tag: `PATTERN_LOOKUP fingerprint=... histPnL=... n=... action=BOOST/NORMAL/REDUCE/BLOCK`.
- Persist as JSON in `~/Documents/NinjaTrader 8/MmATM_v6_PatternStore.json` so it survives restarts.

#### B. Per-Hour Self-Tuning (priority 1)
- Rolling 30-day **per-hour** win-rate, avg R, expectancy.
- Auto-adjusts `MinSignalConfidence` per hour: e.g. midday low-edge hour gets +10 confidence requirement, opening 30-min gets −5.
- Auto-adjusts `ExtensionMaxAtrFromVwap` per hour (volatile hours allow more extension).
- Heat-map tile on dashboard showing each hour's win-rate as color intensity.

#### C. MM Playbook Detector (priority 1 — "the open mouth feature")
A separate scoring engine, parallel to confidence, that predicts the next MM move:
- **Stop-run setup**: equal lows/highs for N bars + tape balanced + low ADX = MM is preparing to sweep. Pre-arm a fade entry on the sweep tick.
- **Ladder pull**: sudden book thinning (would need L2 if NinjaTrader allows, otherwise tape compression) before a violent move = MM stepping aside. Block entries.
- **Squeeze trap**: BB width minimum + RSI mid + low volume = MM coiling for an expansion. Don't fade either side; trade the breakout direction once tape confirms.
- **Liquidity grab**: prev-day H/L + first 2 hours + low conviction tape = MM grab + reversal expected. Pre-arm reversal signal.
- Outputs: `mmIntent ∈ {NEUTRAL, STOP_RUN_BULL, STOP_RUN_BEAR, LADDER_PULL, SQUEEZE_BUILD, GRAB_REVERSAL}` displayed on dashboard.
- Trades are biased toward *fading* the predicted MM action.

#### D. Multi-Timeframe Confidence Stack
- Promote 5-min and 15-min into independent confidence calculators (re-use the F1–F12 pipeline per TF).
- Final entry confidence = weighted: `0.5×conf1m + 0.3×conf5m + 0.2×conf15m`.
- "STRONG" upgrade requires all 3 TFs agree on direction.
- Dashboard shows 3 confidence bars stacked (1m / 5m / 15m).

#### E. Adaptive Risk-Per-Trade (Kelly-fraction sizing)
- Track rolling 30-day expectancy `E = winRate × avgWin − lossRate × avgLoss`.
- Kelly fraction `f = E / avgWin²` (half-Kelly for safety).
- Translate to contracts via account equity. Caps: `MinContracts = 1`, `MaxContracts = configurable`.
- Auto-reduce by 50% after `consecutiveLosses ≥ 3` (already partially done in v5 with DCA suppression).

#### F. Fib + Candle Reversal Scalp Module (was deferred from v14.20)
- Auto-draw fib retracement on the day's developing move.
- Detect candlestick reversal patterns (engulf, hammer, doji-with-tail) at 38.2 / 50 / 61.8 levels.
- Independent toggle, independent risk pool. Fires only when main strategy is WAIT.

#### G. V-Fakeout Filter (the 2026-03-25 lesson)
- Detect: sharp move → flat 3-bar consolidation at extreme → sudden reverse engulf.
- Block entries on the second leg of a V-shape until 5 bars of trend confirmation post-reverse.
- Diag tag: `BLOCK_V_FAKEOUT`.

#### H. Spread / Slippage Awareness
- Live spread monitor on dashboard (`Spread: 1.25t`).
- If spread > N×typical → block entries (`BLOCK_SPREAD_WIDE`).
- Track per-trade slippage (fill vs signal price); if rolling slippage worsens → tighten activation thresholds.

#### I. Strategy Analyzer Walk-Forward Harness
- Built-in framework to run v6 over rolling 30-day windows, log Sharpe / max DD / expectancy per window.
- Auto-promote the best parameter set to live (with manual confirm).

#### J. Dashboard v2
- Move heavy WPF rendering to a single `CompositionTarget.Rendering` tick (currently scattered Dispatcher.InvokeAsync calls).
- Add: per-hour heat-map, pattern-fingerprint last 5 outcomes, MM intent badge, Kelly-suggested size, live spread + slippage.
- Save dashboard size + position to user settings (right now it resets).

### 18.4 v6 architecture changes

- **Fork**: copy `Mm_ATM_v5.cs` → `Mm_ATM_v6.cs`, rename class, bump display name.
- **Split into partials**: `Mm_ATM_v6.Core.cs`, `.Signals.cs`, `.Trail.cs`, `.PatternMemory.cs`, `.MMPlaybook.cs`, `.Dashboard.cs`. The 4600-line monolith is fighting us.
- **Persistence layer**: `MmATM_v6_PatternStore.json` for pattern memory, `MmATM_v6_HourStats.json` for per-hour stats. Load on `State.DataLoaded`, save on flatten + on shutdown.
- **Diag CSV v2**: extend to ~40 columns to include MM intent, pattern fingerprint, Kelly size, spread.
- **Backtest mode**: a `SimulateTape` flag that synthesizes a tape-delta proxy from bar OHLCV when not in live mode (so tape-dependent logic still runs in Strategy Analyzer).

### 18.5 Migration plan (v5 → v6)

1. **Freeze v5 14.19** — only bug-fix commits (e.g. `v5 14.19.1`) for production.
2. Open `ninja-v6` branch off current `ninja`.
3. Fork file, split into partials, get it compiling identical to v5 14.19 → tag `v6 0.1 - parity`.
4. Implement features in priority order: B (per-hour) → A (pattern memory) → C (MM playbook) → G (V-fakeout) → H (spread) → D (multi-TF) → E (Kelly) → F (Fib scalp) → J (dashboard v2) → I (walk-forward).
5. Each feature ships behind its own toggle, default OFF. Promote to default ON only after a 5-day live A/B vs v5.

### 18.6 What "make MM open their mouth" looks like

- **MM intent badge** on dashboard: "MM PROBE STOP-RUN BULL @ 18242.50 — fade ready" (you click the dashed level, strategy primes a long limit just below).
- **Pattern lookup popup** on every entry: "This setup: 14 prior occurrences, 71% win, avg +$240" (or "0 prior, no edge data — passing").
- **Hourly heat-map**: instantly see your worst hour and either flatten through it or boost confidence requirement.
- **Kelly badge**: "Suggested 2 contracts (rolling expectancy $185, half-Kelly)" instead of fixed sizing.
- **Walk-forward report** every Sunday: "Last 30 days: Sharpe 1.8, max DD $1,420, 64% wins. Suggested change: lower MinSignalConfidence to 53 (validated +$640 over baseline)."

### 18.7 Out of scope (won't pursue in v6)

- Machine-learning models — explicit rules > opaque models for trading; we can read a rule and override it. ML stays a research toy.
- Multi-instrument trading — one symbol at a time keeps the dashboard sane and risk obvious.
- Auto-news scraping — keep the manual news-blackout window, scraping adds fragility.
- Cloud / remote dashboard — local-only for security and simplicity.

### 18.8 The pact

> **v5 stays alive trading real money. v6 is the lab.** When a v6 feature proves itself over 2 live weeks, we backport the *toggle* (not the implementation) into v5 14.x.x. v5's job is to be reliable. v6's job is to make MM regret showing up.

---



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

## 14.9 — Live-fix batch: stop-direction, BE relax, fast-reversal exit, no-disable flatten, session rollover

Eight live-trading defects / hardening items addressed in commit `94bdf82`.

### 14.9.1 `maxTradesPerDay` raised 12 → 20
Default cap was choking active days. New default **20** in `ConfigureDefaults`. Existing `MaxTradesPerDay` property unchanged otherwise.

### 14.9.2 SL price-direction swap (sign convention fix)
`pendingSlNudge` semantics standardized: **positive = TIGHTEN, negative = WIDEN**, regardless of trade direction. The arming and resize paths now apply the sign correctly for both Long and Short, so a `TIGHTEN` button always moves the hidden stop closer to entry and `WIDEN` always moves it further. Previously the Short path inverted this and could flip a tighten into a widen mid-trade.

### 14.9.3 Breakeven relax + fallback
- BE-lock no longer triggers prematurely on tiny ticks. Threshold uses `max(beSafeMinTicks, beSafeAtrFactor × ATR)` (defaults 4 ticks, 0.20×ATR) before locking BE.
- If `breakevenAtPoints` is set very low (≤ 4pt) and ATR-derived floor would block it, the floor is the fallback so BE still arms — never silently disabled.

### 14.9.4 Fast-reversal exit  (`MonitorFastReversalExit`)
First-tick-of-bar anti-MM exit. Inside an open trade, if **adverse move ≥ `fastReversalAtrFactor × ATR`** AND **adverse ≥ `fastReversalAdverseMinPts`** (hard floor) AND it happened within **`fastReversalMaxBars`** of entry, the position is force-flattened with `EXIT_LOSS_FAST_REVERSAL`. Throttled by `lastFastReversalBar` so it fires at most once per trade. Defaults: enabled, factor 0.6, max 4 bars, floor 4pt.

### 14.9.5 FLATTEN button: no-disable
The dashboard FLATTEN button used to call `Account.Flatten()`, which causes NT8 to disable the managed strategy due to position-state desync. All flatten paths now route through `ManagedExitAll()` only — strategy stays enabled, dashboard stays live.

### 14.9.6 Daily-limit hit: no-disable
Same root cause: hitting `maxDailyLossDollars` / `dailyProfitTargetDollars` previously disabled the strategy. Now it sets `dailyLimitHit` / `dailyProfitHit` flags which veto new entries via `CanEnterTrade`, while leaving the strategy enabled (so trail/exit logic on any open position still runs).

### 14.9.7 TRL NOW button bypass
The "TRL NOW" dashboard button now bypasses the `trailEnabled` gate and the profit-threshold gate — pressing it forces `manualTrailEarlyStart = true`, `trailActive = true` and seeds `trailPrice` immediately at the requested offset. The `MonitorAdaptiveTrail` call in `OnBarUpdate` is gated by `trailEnabled || trailActive || manualTrailEarlyStart` so the manual override is honored even when auto-trail is off.

### 14.9.8 18:00 ET futures session rollover  (`MaybeFuturesSessionRollover`)
At the first bar whose timestamp crosses 18:00:00 ET (start of next CME session), daily counters are auto-reset: `dailyTradeCount`, `dailyPnL`, `dailyLimitHit`, `dailyProfitHit`, `consecutiveLosses`, `consecutiveWins`, `lastFuturesSessionResetDate`. A `SESSION_ROLLOVER` row is written to the diag CSV. This means a strategy left running 24/5 begins each new electronic session with a clean slate.

---

## 14.10 — Smarter than MM/algos: TOD-SL, news blackout, sweep boost, DCA suppression

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

---

## 14.11 � Anti-MM smarts: Aggressive Exits, Chop filter, Adaptive intra-day window

Three new behavioral layers driven by the 2026-03-20 chop-spiral autopsy: morning produced +`,640` of clean trend wins (09:50�10:16), then a low-ADX whipsaw chop window (10:24�11:34) gave back `-,070` in seven losing shorts where ADX had collapsed below 18 and the order-flow tape was actually buying. All three layers ship as both **NinjaScript properties** AND **dashboard toggles**.

### 14.11.1 Aggressive Exits Mode  (Group `12 - Aggressive Exits`, dashboard button `AGGR`, default OFF)

When ON, applies to **both manual and auto** trades:
- **BE locks early** at `+AggrBeAtPoints` (default 3pt) � the smart-BE ATR/TP floors are bypassed in this mode.
- **Trail starts fast** at `+AggrTrailActivationPts` (default 4pt) with distance `AggrTrailDistPts` (default 2pt). Tier name in CSV is `Aggr-Mode`.
- **Pullback exit** � once profit = activation, if it pulls back = `AggrPullbackAtrFactor � ATR` (default 0.4) **AND** we're within `AggrPullbackMaxBars` (default 2) of entry, fire a market exit (`ExitLong/ExitShort`) tagged `AGGR_PULLBACK`. This is the anti-MM-trap defense � once they've started reversing your fast scalp, get out before the round-trip becomes a loss.

Properties: `AggressiveExitsEnabled`, `AggrBeAtPoints`, `AggrTrailActivationPts`, `AggrTrailDistPts`, `AggrPullbackAtrFactor`, `AggrPullbackMaxBars`.

### 14.11.2 Chop Filter  (Group `13 - Chop Filter`, dashboard button `CHOP`, default ON)

Veto layer in `CanEnterTrade` � fires for **manual AND auto**. Helper `IsChoppy(direction, out reason)` returns true if ANY of:

| # | Test | Default trigger |
|---|---|---|
| 1 | ADX-collapse | `indAdx[0] < ChopAdxMin (18)` AND ADX falling for `ChopAdxFallingBars (3)` consecutive bars |
| 2 | EMA convergence | `|EmaFast - EmaSlow| < ChopEmaSepMinAtr � ATR (0.30 � ATR)` |
| 3 | Close-range collapse | range of last `ChopRangeBars (5)` closes `< ChopRangeMaxAtr � ATR (1.0 � ATR)` |
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
- `AGGR ON/OFF` � DarkOrange when active, DarkRed when off. Tooltip shows current Aggr thresholds.
- `CHOP ON/OFF` � DarkSlateGray when active. Tooltip describes the four chop tests.
- `ADAPT ON/OFF` � DarkSlateGray when active. Tooltip shows window size + thresholds. Toggling OFF also clears any active tighten state.

### 14.11.5 Diagnostic CSV additions

New `Action` tags introduced this revision:
- `BLOCK_CHOP` � entry blocked by chop filter (`Detail` = trigger reason).
- `AGGR_PULLBACK_EXIT` � Aggressive Exits market exit fired (`Detail` shows peak / current / pullback in pts).
- `ADAPT_WINDOW_ON` / `ADAPT_WINDOW_OFF` � adaptive tighten state transitions.
- `ADAPT_TIGHTEN_ACTIVE` � heartbeat row each bar while tighten is active (shows boosted `effMin`).

(Header unchanged at 29 columns � these tags use the existing `Action,Detail` slots.)

### 14.11.6 Why this addresses the 2026-03-20 chop-spiral autopsy

Today's seven losing shorts had every chop signature simultaneously: ADX 9�18, EMAs flat, tape positive (buyers) while strategy fired shorts at price-wick lows. The chop filter alone (test #1 + #4) would have blocked all seven. The adaptive window would have additionally raised the bar after the first 3 losses. Aggressive exits would have flipped the few trades that *did* move our way (e.g. 11:21, 11:24) from `-` trail-stop losses into small wins by exiting on the first 0.4�ATR pullback.

Future work (deferred to 14.12):
- Fib-retracement + candle-pattern reversal scalp setup (separate toggle, default OFF).
- Per-hour outcome heat-map for self-tuning best/worst hours.


