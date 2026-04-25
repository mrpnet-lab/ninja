# Mm_ATM_v4 — NinjaTrader 8 Strategy Documentation

**Class:** `Mm_ATM_v4`  
**Platform:** NinjaTrader 8.1+  
**Instrument:** NQ Futures (E-mini Nasdaq-100)  
**Execution model:** Managed, hidden SL/TP (no native bracket orders)  
**Calculation:** `OnEachTick`  

---

## Table of Contents

1. [Overview](#overview)
2. [Constants](#constants)
3. [Parameters Reference](#parameters-reference)
4. [SL / TP System](#sl--tp-system)
5. [Adaptive Trailing Stop](#adaptive-trailing-stop)
6. [Smart Trail](#smart-trail)
7. [Smart SL](#smart-sl)
8. [Daily Loss / Profit Limits](#daily-loss--profit-limits)
9. [Auto Strategies](#auto-strategies)
10. [Signal Filters & Entry Gates](#signal-filters--entry-gates)
11. [Post-Entry Trap Detector](#post-entry-trap-detector)
12. [Pre-Entry Trap Filter](#pre-entry-trap-filter)
13. [HTF Bias & Session Confirmed Bias](#htf-bias--session-confirmed-bias)
14. [Runner Mode](#runner-mode)
15. [Breakeven Lock](#breakeven-lock)
16. [EMA-Against Exit](#ema-against-exit)
17. [Dashboard](#dashboard)
18. [Entry Order Types](#entry-order-types)
19. [Session & Position Management](#session--position-management)
20. [Volume Profile](#volume-profile)
21. [VWAP](#vwap)
22. [ORB (Opening Range Breakout)](#orb-opening-range-breakout)
23. [Diagnostic Logging](#diagnostic-logging)
24. [Properties Groups Summary](#properties-groups-summary)

---

## Overview

Mm_ATM_v4 is a full-featured semi-automated NQ scalping strategy that combines:

- **Hidden SL/TP** — stops and targets are internal to the strategy; NinjaTrader never sees a bracket order, making them invisible to Market Makers
- **Adaptive Trailing Stop** — ATR/EMA/momentum composite trail with regime tiers
- **Smart Trail** — pause the trail during suspected MM stop-hunt pullbacks
- **Smart SL** — dynamic stop adjustment based on profit, trap score, and entry confidence
- **4 Auto Strategies** + Auto Select mode with confidence scoring
- **Post-entry Trap Detector** — detects being caught in an MM trap after entry
- **Pre-entry Trap Filter** — scores and blocks entries with high trap probability
- **HTF Bias** — composite 3-vote higher-timeframe directional filter (1-min EMA, 5-min EMA, session open price)
- **Volume Profile** — real-time intraday POC/VAH/VAL with filter integration
- **Daily SL/TP caps** — live realized + unrealized check every tick
- **On-chart WPF dashboard** — draggable/resizable floating overlay with full trade controls

---

## Constants

| Constant | Value | Purpose |
|---|---|---|
| `NQ_TICKS_PER_POINT` | 4.0 | NQ tick size = 0.25 pt |
| `NQ_DOLLARS_PER_TICK` | $5.00 | Per tick P&L per contract |
| `NQ_DOLLARS_PER_POINT` | $20.00 | Per point P&L per contract |
| `STALE_EXIT_TICKS` | 250 | Force-flatten if exit pending this many ticks |
| `FLAT_SYNC_MAX_TICKS` | 200 | Grace period before safety state reset when flat |
| `DASH_UPDATE_MS` | 333 ms | Dashboard refresh throttle (~3×/sec in Realtime) |
| `TRAIL_SPIKE_MULT` | 2.0 | ATR multiplier above average that triggers trail tightening |
| `VALUE_AREA_PCT` | 0.70 | Volume profile value area (70%) |
| `AGGRESSIVE_LIMIT_TIMEOUT_SEC` | 3 | Cancel unfilled ASK/BID limit order after this timeout |

---

## Parameters Reference

### Group 1 — Risk Management

| Parameter | Default | Range | Description |
|---|---|---|---|
| `SlPoints` | 20 pts | 1–500 | Hidden SL distance from average entry ($400/ct) |
| `TpPoints` | 55 pts | 1–500 | Hidden TP distance from average entry ($1,100/ct) |
| `MaxDailyLossDollars` | $1,000 | $500–$10,000 | Hard daily loss cap (realized + unrealized) |
| `MaxDailyProfitDollars` | $1,000 | $500–$20,000 | Daily profit target — halts trading when hit |
| `MaxTradesPerDay` | 2 | 0–999 | Max auto entries per session. 0 = unlimited |
| `Contracts` | 1 | 1–10 | Contracts per entry (adjustable on dashboard) |

### Group 2 — Position Sizing

| Parameter | Default | Range | Description |
|---|---|---|---|
| `MaxContracts` | 4 | 1–10 | Maximum total contracts across all DCA adds |
| `DcaSuggestionPoints` | 50 pts | 0–200 | Visual DCA line distance on chart. 0 = off. No auto-entry |

### Group 3 — Trading Mode

| Parameter | Default | Description |
|---|---|---|
| `AutoMode` | false | Enable fully automatic entry scanning |
| `AutoStrategy` | 0 | 0=Momentum+VWAP, 1=Key Level, 2=Liq Sweep Rev, 3=ORB, 4=Auto Select |
| `MinSignalConfidence` | 45.0% | Minimum confidence score to allow auto entry |
| `EntryDelaySeconds` | 1 | Minimum seconds between consecutive entries |
| `EnableSessionOverride` | true | Auto Select: force Liq Sweep 9:30–10, Momentum 11:30–14 |

### Group 4 — Indicators

| Parameter | Default | Range | Description |
|---|---|---|---|
| `EmaPeriodFast` | 9 | 3–50 | Fast EMA period (chart + signals) |
| `EmaPeriodSlow` | 21 | 10–200 | Slow EMA period (chart + signals) |
| `RsiPeriod` | 14 | 5–30 | RSI period |
| `AtrPeriod` | 14 | 5–30 | ATR period (used for trail, filters, trap) |
| `HtfEmaPeriod` | 45 | 10–200 | Single-series HTF EMA for trend filter |

### Group 5 — Display

| Parameter | Default | Description |
|---|---|---|
| `SlTpAdjustStep` | 5 pts | Dashboard SL/TP ± button increment |
| `JumpSlPercent` | 50% | JUMP SL moves stop this % of the distance toward entry |
| `ShowEma` | true | Render EMA fast/slow on chart |
| `ShowRsi` | true | Render RSI panel |
| `ShowAtr` | true | Render ATR panel |
| `ShowVwap` | true | Render VWAP line |
| `ShowKeyLevels` | true | Render S/R key level lines |
| `ShowSweepSignals` | true | Render liquidity sweep signal markers |

### Group 6 — Trading Hours

| Parameter | Default | Description |
|---|---|---|
| `TradingHoursEnabled` | true | Gate auto entries to the window below. Toggleable on dashboard |
| `TradingStartTime` | 93000 | 9:30 AM ET (HHMMSS format) |
| `TradingEndTime` | 160000 | 4:00 PM ET |
| `FlattenTime` | 165000 | 4:50 PM ET — auto-flatten time |

> **CME Maintenance guard:** strategy also force-flattens any open position between 16:55–18:00 ET.

### Group 7 — Adaptive Trail

| Parameter | Default | Range | Description |
|---|---|---|---|
| `TrailEnabled` | true | — | Enable the adaptive trailing stop |
| `TrailActivationPoints` | 5 pts | 1–100 | Minimum profit to activate trail |
| `TrailMinPoints` | 4 pts | 1–100 | Minimum allowed trail distance |
| `TrailMaxPoints` | 25 pts | 2–200 | Maximum allowed trail distance |
| `TrailAtrMultiplier` | 1.5 | 0.5–5.0 | ATR weight in trail distance calculation |

### Group 8 — Trap Detector

| Parameter | Default | Description |
|---|---|---|
| `EnablePostEntryTrapDetector` | true | Monitor for adverse price action after entry |
| `VolatilitySpikeGuardBars` | 0 | Skip trail update when barRange > 3× ATR. 0 = disabled |

### Group 9 — Signal Quality

| Parameter | Default | Description |
|---|---|---|
| `HtfFilterEnabled` | true | Reduce confidence when signal opposes HTF EMA trend |
| `UseVolumeProfileFilters` | true | Use POC/VAH/VAL for mid-range penalty and bounce boost |
| `LossCooldownSeconds` | 0 | Pause auto entries after a loss. 0 = disabled |

### Group 10 — Smart Trail

| Parameter | Default | Range | Description |
|---|---|---|---|
| `SmartTrailEnabled` | true | — | Auto-pause trail on suspected MM stop-hunt pullbacks |
| `SmartTrailMaxPauseBars` | 8 | 2–30 | Safety: auto-resume after this many paused bars |

### Group 11 — Smart SL

| Parameter | Default | Range | Description |
|---|---|---|---|
| `SmartSlEnabled` | true | — | Dynamically adjust hidden SL during the trade |
| `SmartSlBePct` | 25% | 10–90 | Move SL to break-even when profit ≥ this % of TP |

### Group 12 — Diagnostics

| Parameter | Default | Description |
|---|---|---|
| `EnableDiagLog` | false | Write per-bar CSV to `Documents\NinjaTrader 8\MmATM_DiagLog_YYYYMMDD.csv` |
| `DiagOneDayOnly` | true | Stop logging after first trading date completes |
| `DiagAppend` | false | Append to existing daily log instead of overwriting |

---

## SL / TP System

All stops and targets are **hidden** — NinjaTrader has no bracket orders. The strategy tracks `hiddenStopPrice` and `hiddenTargetPrice` internally and fires `ExitLong` / `ExitShort` from `MonitorHiddenStops()` on every tick.

### Price used for triggers

| Mode | Long SL/TP checks against | Short SL/TP checks against |
|---|---|---|
| Realtime | `GetCurrentBid()` | `GetCurrentAsk()` |
| Historical | `Close[0]` | `Close[0]` |

### Stop calculation (`ArmHiddenStops`)

```
slOffset = slPoints × 4 × TickSize
tpOffset = tpPoints × 4 × TickSize   (or 500pt in Runner mode)

Long:   hiddenStopPrice   = avgEntry − slOffset
        hiddenTargetPrice = avgEntry + tpOffset

Short:  hiddenStopPrice   = avgEntry + slOffset
        hiddenTargetPrice = avgEntry − tpOffset
```

### Dynamic TP clamp

If `prevDayHigh` / `prevDayLow` sits between entry and the original TP, the target is clamped to that level ± 1 tick to avoid trading through a major key level. Minimum 8-point buffer must remain.

### Re-arming

Whenever `slPoints` or `tpPoints` is changed via the dashboard, `pendingRearm = true` is set. On the next `OnBarUpdate` tick the stops are recalculated with the new values.

---

## Adaptive Trailing Stop

Activated when profit ≥ `max(trailActivationPoints, 0.4 × ATR_pts)`.

### Trail distance formula (`CalculateTrailDistance`)

Five weighted scores → `trailTrendScore` (0.0 – 1.0):

| Component | Weight | Measures |
|---|---|---|
| ATR expansion vs 10-bar avg | 25% | Volatility expansion |
| EMA fast/slow spread ÷ ATR | 25% | Trend strength |
| Bars closing in trade direction (last 8) | 25% | Momentum persistence |
| RSI distance from 50 ÷ 50 | 10% | Overbought/oversold momentum |
| Bar range ÷ ATR | 15% | Price velocity |

```
chopMultiplier = trailTrendScore < 0.5 ? (0.5 + trailTrendScore) : 1.0
atrDist        = ATR × trailAtrMultiplier ÷ tickPt
regimeDist     = lerp(trailMinPoints, trailMaxPoints, trailTrendScore)
finalDist      = (regimeDist × 0.6 + atrDist × 0.4) × chopMultiplier × spikeMult
```

- **Volatility spike** (ATR > 2× avg): `spikeMult = 0.8`
- **EMA fights trade**: `finalDist × 0.75` (exit counter-trend trades sooner)
- **Clamp**: always between `trailMinPoints` and `trailMaxPoints`

### Regime tiers

| Tier | Condition | Distance |
|---|---|---|
| **Chop-Quick** | `trailTrendScore < 0.4` AND HTF bias not agreeing | `trailMinPoints` (tight, exit fast) |
| **Active** | Standard | Calculated distance |
| **Runner** | EMA gap widening in trade direction OR `htfBias` agrees | `max(2× calc, 0.8× ATR)`, capped at `trailMaxPoints` |

### Profit floor tiers (ratchet — trail never retreats below floor)

| Tier | Trigger | Floor |
|---|---|---|
| **T1-BE** | `maxProfit ≥ 1.5× dynActivation` | Entry + 1 tick |
| **T2-Strong** | `maxProfit ≥ 2.5× dynActivation` | 40% of max profit |
| **T3-Runner** | `maxProfit ≥ 4.0× dynActivation` | 45% (choppy) or 50% (trend) of max profit |

### Structural snap

Each bar, the trail also considers the nearest swing low (longs) or swing high (shorts) over the last 5 bars. If the structural level is tighter than the calculated trail but still ≥ `trailMinPoints` away from price, the trail snaps to that level.

### Trap tightening

When `trapDetected = true` and `trapSuspendBarsLeft = 0`, trail distance is reduced by 40% (`× 0.60`).

---

## Smart Trail

**Purpose:** Prevent the trail from stopping out a good trade during a brief MM stop-hunt spike.

### Pause conditions (all must be true simultaneously)

1. In profit ≥ 1.5× trail activation
2. Price pulling back ≥ 0.3× ATR from prior bar's extreme
3. EMA fast still aligned with trade direction (trend intact)
4. Volume < 1.5× 20-bar average (not a genuine reversal)
5. Not within 1× ATR of a 20-bar swing level
6. `CurrentBar ≥ 3`

When paused, `smartTrailFrozenPrice` is recorded. The trail does not move, but the frozen level still exits if hit.

### Resume conditions (any one)

1. `smartTrailPauseBars ≥ smartTrailMaxPauseBars` (safety timeout)
2. Price makes new highs/lows (in trade direction) for 2 consecutive bars
3. Reversal pin-bar confirmed after a stop-hunt (Low[0] > Low[1] + bullish close for longs)

### Dashboard display

| State | Label | Color |
|---|---|---|
| Paused | `S-Trail: PAUSED (n/max)` | Orange |
| Active & trail live | `S-Trail: monitoring` | LimeGreen |
| Waiting for trail activation | `S-Trail: —` | Gray |
| Feature off | `S-Trail: OFF` | Gray |

---

## Smart SL

**Purpose:** Dynamically adjust the hidden SL during the trade based on profit, trap score, and entry confidence.

### Phase A — Break-even lock

Triggers when: `profitPoints ≥ tpPoints × (smartSlBePct / 100)`  
Default: `55 × 0.25 = 13.75 pts`

Moves SL to `avgEntry + 1 tick` (longs) or `avgEntry − 1 tick` (shorts). Only moves stop toward BE, never away. Skipped in Runner mode.

### Phase B — Tighten on trap

Triggers when: `trapScore ≥ 45` and stop has not already been tightened

Tightens SL to 75% of the original offset from entry. Only fires if the new level is tighter (closer to entry) than the current stop.

### Phase B2 — Trap Escape (immediate)

| Condition | Action |
|---|---|
| `trapScore ≥ 65` AND `profitPts < −10` | Exit immediately |
| `trapScore ≥ 50` AND `profitPts < −5` for 2 consecutive bars | Exit |

After a trap escape, `trapEscapeCooldownBar` is set and re-entry is blocked for 10 bars.

### Phase C — Loosen on high-confidence entry

Triggers when (all):
- Break-even not yet locked
- Stop not already loosened  
- `entryConfidence ≥ minSignalConfidence + 8`
- EMAs agree with trade
- Adverse move < 0.5× ATR

Extends SL by up to 30% of original offset (maximum 15 pts) to give a high-conviction trade more breathing room.

### Dashboard display

| State | Label | Color |
|---|---|---|
| BE locked | `Smart SL: BE @ X.XX` | LimeGreen |
| Tightened | `Smart SL: TIGHT @ X.XX` | Orange |
| Loosened | `Smart SL: EXTENDED` | Cyan |
| Monitoring | `Smart SL: monitoring` | Gray |
| Feature off | `Smart SL: OFF` | Gray |

---

## Daily Loss / Profit Limits

Checked **every tick** while a position is open (not just on close):

```
unrealPnL = (Close − avgEntry) × direction × $20/pt × qty
livePnL   = dailyRealizedPnL + unrealPnL

if livePnL ≤ −maxDailyLossDollars  → dailyLimitHit = true → ExecuteFlatten()
if livePnL ≥ +maxDailyProfitDollars → dailyProfitHit = true → ExecuteFlatten()
```

Once either flag is set, all new auto and manual entries are blocked for the rest of the session. Dashboard shows status in red (loss) or gold (profit target).

Session resets at midnight: `ResetDailyTracking()` clears `dailyRealizedPnL`, `dailyTradeCount`, `dailyLimitHit`, `dailyProfitHit`, and all cooldown counters.

---

## Auto Strategies

Four signal algorithms scored independently each bar. In Auto Select (strategy 4), the highest-scoring strategy over `autoSelectStabilityBars` consecutive bars wins.

| Index | Name | Signal Basis |
|---|---|---|
| 0 | Momentum+VWAP | EMA momentum + price relative to VWAP |
| 1 | Key Lvl Breakout | S/R key level breakout confirmation |
| 2 | Liq Sweep Reversal | Liquidity sweep with reversal candle (pre-entry trap filter skipped) |
| 3 | Opening Range B/O | ORB breakout above/below first-bar range |
| 4 | ★ Auto Select | Highest scorer from 0–3 over stability window |

With `EnableSessionOverride = true`:
- 9:30–10:00 ET → forced Liquidity Sweep (s=2)
- 11:30–14:00 ET → forced Momentum+VWAP (s=0)

---

## Signal Filters & Entry Gates

Auto entries require **all** of the following to pass:

### Confidence
- `lastBullConfidence ≥ minSignalConfidence` (45% default)
- During 11:00–14:00 ET midday: `≥ max(minConf, 50%)`

### RSI direction
- Long: RSI slope not declining (`rsiSlope > −0.5`)
- Short: RSI slope not rising (`rsiSlope < +0.5`)

### HTF bias (3-vote composite)
- Long blocked when: `htfBias < 0` OR `sessionConfirmedBias < 0`
- Short blocked when: `htfBias > 0` OR `sessionConfirmedBias > 0`

### EMA gap
- Unconfirmed (choppy) day: gap must be ≥ 1.0 pt in entry direction
- Confirmed trend day: gap must be ≥ 0.10 pt (pullback tolerance)

### EMA fresh-cross trap
- Blocks entry when the EMA just crossed from a strongly opposing position this bar (MM trap pattern)
- Exemption: `confidenceFlip` entries and confirmed trend days with small prior gap (< 1.0 pt)

### Overextension guard
- Long: `distFromEma > overextMult × ATR` is blocked
- `overextMult` = 1.5 (normal) or 3.0 (confirmed trend in trade direction)

### Previous-day level proximity
- Block long within 0.5× ATR of `prevDayHigh`
- Block short within 0.5× ATR of `prevDayLow`

### Volatility spike bar
- Block entry when `barRange > 2.0 × ATR` — the move already happened on that bar

### Cooldown guards
| Guard | Duration |
|---|---|
| Post-loss bar cooldown | 10 bars after any loss |
| Minimum trade spacing | 5 bars after last exit |
| Trap escape cooldown | 10 bars after trap escape exit |
| Loss cooldown (optional) | `lossCooldownSeconds` (0 = disabled) |

### Max trades
- Blocked when `dailyTradeCount ≥ maxTradesPerDay` (if > 0)

---

## Post-Entry Trap Detector

Runs once per bar while in a position. Scores 0–100:

| Factor | Max score | Condition |
|---|---|---|
| Adverse move ratio vs ATR (norm'd to 10pt min) | 30 | moveRatio > 0.5 |
| Absolute adverse move bonus | 15 | adverseMove > 6 pts |
| EMA conflict | 20 | EMAs pointing against trade |
| High volume adverse bar | 15 | Volume > 1.5× avg while adverse |
| Rejection wick | 10 | Upper/lower wick > 50% of bar range |
| **Total possible** | **90** | |

### Enhanced features

**Stop Hunt Spike Detection:** If bar sweeps beyond prior 5-bar extreme then closes back inside, `trapSuspendBarsLeft = 4` is set — trap score is halved and trail is relaxed for 4 bars.

**Volume Absorption:** Large range + high volume bar with close near midpoint (< 20% from center) → absorptiondetected, trap score × 0.60.

**Decay:** If new score < prior score, blend: `trapScore = prev × 0.85 + new × 0.15`. If stop hunt active: `× 0.5`.

`trapDetected = true` when `trapScore ≥ 50`.

---

## Pre-Entry Trap Filter

Scored before every entry (auto blocked at ≥ 60; manual shows warning only). Liquidity Sweep strategy (s=2) is exempt.

| Factor | Score | Condition |
|---|---|---|
| Recent move in entry direction vs ATR | up to 30 | Normalized move ratio |
| EMA alignment conflict | 25 | EMA fast/slow opposing entry |
| Rejection wick | 20 | Wick > 45% of bar range in entry direction |
| Prior 20-bar swing proximity | 25 | Within 2× ATR |
| Round number proximity | 20 | Within 0.5× ATR of nearest $50 NQ level |
| Opening trap window | 20 | 9:30–10:00 ET |
| VWAP overextension | 20 | Price > 2× ATR from VWAP in entry direction |
| Consecutive candles | 25 / 15 | 4+ / 3 same-direction candles |

**HTF adjustment:** When `htfBias` agrees with entry direction, the score is halved (effective threshold becomes 120 — trend-following entries are expected to show extension).

---

## HTF Bias & Session Confirmed Bias

### `htfBias` (composite 3-vote)

Each bar, three sub-votes are tallied:

1. **1-min HTF EMA** (`htfEmaPeriod = 45`): +1 if `Close > EMA`, −1 if below
2. **5-min fast EMA** (9-period): +1 if 5m Close > 5m EMA, −1 below
3. **Session open bias**: +1 if `Close > currDayOpen`, −1 below (with dead zone ±0.25pt)

`htfBias = sum of votes` → range −3 to +3. Dashboard and entries use sign only: positive = bullish, negative = bearish, zero = neutral.

### `sessionConfirmedBias`

Sustained trends are tracked:
- `htfBiasConsecBull` / `htfBiasConsecBear` count consecutive bars with `htfBias > 0` / `< 0`
- After `autoSelectStabilityBars` (2) consecutive matching bars, `sessionConfirmedBias` is set (+1 or −1)
- Resets to 0 when bias reverses

A confirmed session bias:
- Relaxes EMA gap threshold to 0.10 pt
- Relaxes fresh-cross block for small prior gaps
- Doubles overextension multiplier in trend direction (3.0× ATR)
- Activates Runner-width trail from the start of a new trail

---

## Runner Mode

Activated in `ArmHiddenStops` when `htfBias` agrees with trade direction:

```
runnerModeActive = (openTradeDirection == 1 && htfBias > 0)
                || (openTradeDirection == -1 && htfBias < 0)
```

Effects:
- TP is set to 500 points (effectively disabled)
- Adaptive trail manages exit exclusively
- Breakeven lock is skipped
- Trail activates with Runner-width distance immediately

Runner mode allows capturing 100+ point trending moves without a hard TP cap.

---

## Breakeven Lock

Independent of Smart SL. Fires once `profitPoints ≥ 8.0 pts` (hardcoded):
- SL is moved to exact `avgEntry` price with `breakevenActive = true`
- SL never moves away from BE once set
- **Skipped in Runner mode** (trail manages exit)

---

## EMA-Against Exit

Each first tick of a bar, while in position:

```
emaAgainst = (long && emaFast < emaSlow) || (short && emaFast > emaSlow)
isUnderwater = unrealPts < −2 pts
```

If both are true for **2 consecutive bars**, the position is exited immediately. This catches sustained reversals without waiting for the hidden SL.

---

## Dashboard

Floating WPF overlay on the chart. Draggable via title bar, resizable via bottom-right grip.

### Button controls

| Button | Action | Thread mechanism |
|---|---|---|
| **▲ BUY MKT** | Market long entry | `pendingLong = true` |
| **▼ SELL MKT** | Market short entry | `pendingShort = true` |
| **△ BUY ASK** | Aggressive limit at ask | `pendingBuyAsk = true` |
| **▽ SELL BID** | Aggressive limit at bid | `pendingSellBid = true` |
| **△ BUY LMT** | Passive limit at bid | `pendingLongLimit = true` |
| **▽ SELL LMT** | Passive limit at ask | `pendingShortLimit = true` |
| **▣ CLOSE 1** | Exit `contracts` quantity | `pendingCloseOne = true` |
| **⏹ CLOSE ALL** | Close entire position | `pendingCloseTrade = true` |
| **⇥ JUMP SL** | Move SL `jumpSlPercent`% toward price | `pendingJumpSL = true` |
| **⚠ EMERGENCY KILL** | Flatten + lock out new entries | `pendingFlatten = true` |
| **MANUAL / AUTO** | Toggle auto mode | Immediate |
| **◄ / ► Strategy** | Cycle strategy 0–4 (frozen while in trade) | Immediate |
| **SL − / +** | Adjust `slPoints` ± step, re-arm | `pendingRearm = true` |
| **TP − / +** | Adjust `tpPoints` ± step, re-arm | `pendingRearm = true` |
| **Qty − / +** | Adjust contracts (1 to maxContracts) | Immediate |
| **TRAIL: ON/OFF** | Toggle trail; clears trail state if turned off | Immediate |
| **TRAP: ON/OFF** | Toggle post-entry trap detector | Immediate |
| **S-TRAIL ✓** | Toggle Smart Trail pause feature | Immediate |
| **SMART SL ✓** | Toggle Smart SL dynamic adjustments | Immediate |
| **HOURS: ON/OFF** | Toggle trading hours enforcement | Immediate |

> All `pending*` flags are consumed on the data thread at the top of `OnBarUpdate()` — this is the NT8 thread-safe pattern for WPF button interactions.

### Display labels

| Label | Shows |
|---|---|
| `lblStatus` | Position direction, scan status, daily limit state |
| `lblPosition` | Qty/maxContracts + average entry price |
| `lblHiddenSL` | SL price + pts + ticks + $ at risk |
| `lblHiddenTP` | TP price + pts + ticks + $ target |
| `lblTrailInfo` | Trail price, distance to price, regime, score %, tier name |
| `lblTrapInfo` | Trap score %, detection state, bars in trade |
| `lblSmartTrailInfo` | Smart Trail state (PAUSED / monitoring / OFF) |
| `lblSmartSlInfo` | Smart SL state (BE price / TIGHT / EXTENDED) |
| `lblConfBull` | Bull confidence % (opacity dims below threshold) |
| `lblConfBear` | Bear confidence % (opacity dims below threshold) |
| `lblActiveStrategy` | Strategy name last used + Auto Select scores |
| `lblUnrealized` | Live mark-to-market P&L |
| `lblPnL` | Daily P&L (realized + unrealized) + trade count |
| `lblTotalPnL` | Same as Daily P&L |
| `lblAccountPnL` | Account-level realized + unrealized P&L |
| `lblAccountBal` | Account cash balance |
| `lblVwapVal` | VWAP + POC price |
| `lblTradeHours` | Trading window status + time range |

---

## Entry Order Types

| Method | Order type | Price |
|---|---|---|
| `ExecuteLongEntry` | `EnterLong` (market) | Current close |
| `ExecuteShortEntry` | `EnterShort` (market) | Current close |
| `ExecuteBuyAskEntry` | `EnterLongLimit` | Current ask |
| `ExecuteSellBidEntry` | `EnterShortLimit` | Current bid |
| `ExecuteLongLimitEntry` | `EnterLongLimit` | Current bid (passive) |
| `ExecuteShortLimitEntry` | `EnterShortLimit` | Current ask (passive) |

ASK/BID aggressive orders time out after `AGGRESSIVE_LIMIT_TIMEOUT_SEC` (3 s) if unfilled.

All entry methods run through `CanEnterTrade()` which checks: pendingExit, daily limits, emergency kill, max trades/day, CME maintenance window, trading hours, opposing position (→ partial close), max contracts cap, and entry delay cooldown.

---

## Session & Position Management

### Position sync (`SyncPositionState`)

Called every tick before entry logic. Recovers from desyncs:
- Direction mismatch → corrects `openTradeDirection`
- Missing entry price → restores from NT position
- Quantity behind → updates from NT position
- Missing DCA count → recalculates
- Missing signal names → pads list
- Stops not armed → fires `ArmHiddenStops`

### Flat detection

When `Position.MarketPosition == Flat` after a pending exit:
- `ResetPositionState()` clears all trade state
- `flatSyncGraceTicks` provides a `FLAT_SYNC_MAX_TICKS` (200 tick) buffer for playback fill delays

### State.Realtime transition

On switching to Realtime (live trading / playback start):
- Full counters reset
- Position recovery: if a position already exists (strategy reload), direction/qty/avgEntry are recovered and stops are re-armed

### Session date rollover

`Time[0].Date != sessionDate` → `ResetDailyTracking()` + `ResetVwap()` + ORB reset

### Previous-day levels

Tracked intraday: `currDayHigh`, `currDayLow`, `currDayOpen`. On session reset these become `prevDayHigh`, `prevDayLow`, `prevDayClose`, `prevDayOpen` and are used for:
- Dynamic TP clamp
- Entry block near key levels
- Diag log columns

---

## Volume Profile

Real-time intraday volume-at-price histogram using a `SortedDictionary<double, double>`. Updated every bar (`UpdateVolumeProfile`).

Computed levels:
- **POC** (Point of Control): price with highest volume
- **VAH** (Value Area High): upper bound of 70% value area
- **VAL** (Value Area Low): lower bound of 70% value area

Used in signal filters:
- **POC bounce boost**: price near POC when signal aligns → confidence multiplier
- **Mid-range penalty**: price in middle 30–65% of value area → 20% confidence reduction
- `ShowVolumeProfile = true` renders the levels as chart lines

---

## VWAP

Manually computed (tick-safe, not using NT's built-in VWAP):

```
each tick:  currBarTPV += (H+L+C)/3 × volume
            currBarVol += volume

first tick of new bar:
  vwapCumTPV += currBarTPV
  vwapCumVol += currBarVol
  vwapValue   = vwapCumTPV / vwapCumVol
  reset currBarTPV / currBarVol for new bar
```

Resets at session date change. Displayed on chart as `ShowVwap` line and in `lblVwapVal` on the dashboard.

---

## ORB (Opening Range Breakout)

`orbHigh` and `orbLow` are set from the **first completed bar** of the session (first `IsFirstTickOfBar` after a date change). Used exclusively by strategy index 3 (Opening Range B/O) as the breakout reference level.

---

## Diagnostic Logging

When `EnableDiagLog = true`, a CSV file is written to:  
`Documents\NinjaTrader 8\MmATM_DiagLog_YYYYMMDD.csv`

### CSV columns

```
DateTime, Bar, Open, High, Low, Close, Volume,
VWAP, EmaFast, EmaSlow, EmaFSlope, RSI, RSISlope, ATR,
POC, VAH, VAL,
RawBull, RawBear, FiltBull, FiltBear,
EmaAlign, RSIDir, Slope5ATR, ConsecLoss, LastLossDir,
AutoStrat, Position, OpenDir, TrapScore, UnrealPts,
Action, Detail,
PrevDayHigh, PrevDayLow, PrevDayClose, PrevDayOpen, HtfBias, SessOpenBias
```

### Action values logged

| Action | When |
|---|---|
| `ENTRY_LONG` | Long entry executed |
| `ENTRY_SHORT` | Short entry executed |
| `ENTRY_BLOCKED` | Signal met threshold but filter blocked entry |
| `TRADE_BAR` | Each bar while in position (trap score + unrealized P&L) |
| `ENTRY_LONG/SHORT` (SYNC) | Ghost entry recovery from position sync |

`DiagOneDayOnly = true` closes the file when the session date rolls. `DiagAppend = true` appends to the existing file instead of overwriting (useful for multi-run analysis on the same date).

---

## Properties Groups Summary

| Group | Purpose |
|---|---|
| 1 - Risk Management | SL, TP, daily loss/profit caps, contracts, max trades |
| 2 - Position Sizing | Max contracts, visual DCA suggestion distance |
| 3 - Trading Mode | Auto mode, strategy selector, confidence threshold, delay |
| 4 - Indicators | EMA, RSI, ATR, HTF EMA periods |
| 5 - Display | Step sizes, chart indicator visibility |
| 6 - Trading Hours | Start, end, flatten time, hours toggle |
| 7 - Adaptive Trail | Enable, activation, min/max distance, ATR multiplier |
| 8 - Trap Detector | Enable post-entry detector, volatility spike guard |
| 9 - Signal Quality | HTF filter, volume profile filters, loss cooldown |
| 10 - Smart Trail | Enable, max pause bars |
| 11 - Smart SL | Enable, break-even % of TP |
| 12 - Diagnostics | CSV log enable/configure |
