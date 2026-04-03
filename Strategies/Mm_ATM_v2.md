# Mm-ATM v2.0 — NQ Strategy Documentation

**Platform:** NinjaTrader 8.1.x  
**Instrument:** NQ E-mini Futures (MNQ compatible)  
**Class:** `Mm_ATM_v2`  
**Calculate Mode:** `OnEachTick` — hidden SL/TP enforced every tick  
**Source file:** `Mm_ATM_v2.cs`

---

## Table of Contents

1. [What's New in v2](#1-whats-new-in-v2)
2. [Quick Start](#2-quick-start)
3. [Dashboard Controls](#3-dashboard-controls)
4. [Trading Modes](#4-trading-modes)
5. [Entry Methods](#5-entry-methods)
6. [Auto Mode — Signal Scoring System](#6-auto-mode--signal-scoring-system)
7. [The 4 Strategies](#7-the-4-strategies)
8. [Smart Filters — All 10 Layers](#8-smart-filters--all-10-layers)
9. [Auto Select Mode (Strategy 4★)](#9-auto-select-mode-strategy-4)
10. [Adaptive Trailing Stop](#10-adaptive-trailing-stop)
11. [Post-Entry Trap Detector](#11-post-entry-trap-detector)
12. [Pre-Entry Trap Filter](#12-pre-entry-trap-filter)
13. [Volume Profile (POC / VAH / VAL)](#13-volume-profile-poc--vah--val)
14. [Risk Management](#14-risk-management)
15. [Position Sizing (DCA Overhaul)](#15-position-sizing-dca-overhaul)
16. [Trading Hours & Auto-Flatten](#16-trading-hours--auto-flatten)
17. [Hidden SL / TP System](#17-hidden-sl--tp-system)
18. [JUMP SL](#18-jump-sl)
19. [All Parameters Reference](#19-all-parameters-reference)
20. [NQ Constants Used](#20-nq-constants-used)
21. [Dashboard Layout Map](#21-dashboard-layout-map)
22. [Bug Fixes from v1](#22-bug-fixes-from-v1)
23. [Tips & Recommended Settings](#23-tips--recommended-settings)

---

## 1. What's New in v2

| Category | v1 | v2 |
|---|---|---|
| Unrealized P&L | `Position.GetUnrealizedProfitLoss()` (NT8 bug-prone) | Manual calc from avg entry |
| P&L scan | O(n) full scan every tick | Incremental via `processedTradeCount` |
| Opposite direction button | Could trigger reverse entry | **Close only** — never reverses |
| Confidence colors | Gray when below threshold | Always green/red with opacity dimming |
| DCA | `dcaEnabled` + `dcaMaxPositions` + distance guard | `maxContracts` cap + visual suggestion line only |
| Entry signal names | All entries used `" "` (space) — NinjaTrader rejected duplicates | Unique `Entry_N` per add — fixes multi-contract and strategy disable |
| Strategy selector | Any time | **Frozen while in trade** |
| HTF trend filter | ❌ | ✅ EMA-45 trend check |
| Volume Profile | ❌ | ✅ Real-time POC / VAH / VAL |
| Pre-entry trap filter | ❌ | ✅ Blocks risky auto entries |
| Swing structure filter | ❌ | ✅ Higher-lows / lower-highs bias |
| VWAP distance scaling | ❌ | ✅ Penalizes over-extended price |
| Loss cooldown | ❌ | ✅ Pause auto after consecutive losses |
| Auto select stability | Instant switch | ✅ Requires 3 consecutive bars |
| Dashboard active strategy | ❌ | ✅ Shows which strategy fired the entry |

---

## 2. Quick Start

### Minimum setup
1. Drag **Mm_ATM_v2** onto an NQ chart (any timeframe — 1m, 3m, 5m work well)
2. Set **SL / TP** in the Properties panel (defaults: 67 pt SL, 50 pt TP)
3. Set your **Max Daily Loss** and **Daily Profit Target**
4. Leave **Auto Mode** = OFF for manual-only operation
5. Press **▲ BUY MKT** or **▼ SELL MKT** on the dashboard to enter

### To use Auto Mode
1. Set **Auto Strategy** to 0–3 (or 4 for ★ Auto Select)
2. Set **Min Signal Confidence** (default 68%)
3. Toggle **AUTO** on the dashboard — the strategy will scan and enter on its own
4. Optionally enable **Trading Hours Filter** so auto only fires during market hours

---

## 3. Dashboard Controls

The dashboard appears as a draggable floating panel over the chart. You can **drag it by the title bar** and **resize from the bottom-right grip**.

### Mode Row
| Button | Function |
|---|---|
| **MANUAL** | Disable auto entries; all entries are button-triggered |
| **AUTO** | Enable automatic entries based on signal confidence |

### Strategy Selector
| Control | Function |
|---|---|
| **◄ / ►** | Cycle through the 5 strategies (0–4) |
| Strategy name label | Shows current strategy; shows `★ Auto → [active name]` in Auto Select mode |
| **Active:** line | Shows the strategy name that fired the **most recent entry** |
| ⚠️ Frozen in trade | The ◄ / ► buttons are disabled while a position is open |

### Adjustment Rows
| Row | Buttons | Display |
|---|---|---|
| **Qty** | − / + | Current contracts per entry; max shown below |
| **SL** | − / + | Stop loss in points \| ticks \| $ |
| **TP** | − / + | Take profit in points \| ticks \| $ |
| **JUMP SL** button | Jumps SL toward price by the `%` shown | |
| **−  %  +** | Adjust the jump percentage (10%–95%) | |

Adjusting SL or TP while in trade will **re-arm** the hidden stops at the new distance.

### Entry Buttons
| Button | Type | Description |
|---|---|---|
| **▲ BUY MKT** | Market | Immediate long entry at market |
| **▼ SELL MKT** | Market | Immediate short entry at market |
| **△ BUY ASK** | Aggressive limit | Limit at current ask — expires in 3 s if unfilled |
| **▽ SELL BID** | Aggressive limit | Limit at current bid — expires in 3 s if unfilled |
| **△ BUY LMT** | Passive limit | Limit at current bid (queue below market) |
| **▽ SELL LMT** | Passive limit | Limit at current ask (queue above market) |

### Management Buttons
| Button | Function |
|---|---|
| **▣ CLOSE 1** | Exit 1 contract (for scaling out of multi-contract position) |
| **⏹ CLOSE ALL** | Close entire position |
| **⚠ EMERGENCY KILL** | Immediately flatten, halt all trading for the session |

### Toggle Buttons
| Button | Function |
|---|---|
| **TRAIL: ON/OFF** | Enable or disable the adaptive trailing stop |
| **TRAP: ON/OFF** | Enable or disable the post-entry trap detector |
| **HOURS: ON/OFF** | Enable or disable the trading hours filter for auto entries |

### Status Displays
| Section | What it shows |
|---|---|
| Status line | Current position state, pending actions, scan result |
| Pos: N/max | Contracts open vs. max allowed |
| Avg: price | Average entry price across all adds |
| SL: price (pt\|tk\|$) | Hidden stop level with tick and dollar value |
| TP: price (pt\|tk\|$) | Hidden target level with tick and dollar value |
| Trail line | Current trail price, distance, regime, tier name |
| Trap line | Post-entry trap score and detection status |
| Active: name | Strategy that generated the last entry |
| VWAP + POC | Current VWAP level and intraday POC |
| Bull / Bear % | Signal confidence with green/red color + opacity |
| Unrealized P&L | Manually calculated from avg entry price |
| Daily P&L | Cumulative realized P&L today with trade count |
| Total P&L | Daily P&L + unrealized combined |
| Account P&L | Realized + unrealized from account API |
| Balance | Account cash value |
| Hours status | Trading window status with start/end times |

---

## 4. Trading Modes

### Manual Mode
- All entries are button-triggered from the dashboard
- Auto-entry logic is completely disabled
- Risk management (SL/TP, daily limits, flatten time) remains fully active
- Best for: intraday discretionary trading with the SL/TP and trail protection in place

### Auto Mode
- Strategy scans for signal confidence every bar (first tick only when flat for performance)
- Fires a long entry when `Bull confidence ≥ minSignalConfidence`
- Fires a short entry when `Bear confidence ≥ minSignalConfidence`
- Subject to: trading hours filter, daily limits, loss cooldown, max trades/day, pre-entry trap filter
- The **Active:** label on dashboard shows which strategy generated the entry
- Printed to the Output window every 100 bars if auto cannot enter, with the reason

---

## 5. Entry Methods

### Button Entries (Manual or Auto)
All entries are queued as flags in the WPF handler and executed safely on the next `OnBarUpdate` tick. This prevents threading issues.

### Opposite Direction Behavior (Bug 3 — fixed from v1)
If you press **BUY MKT** while **SHORT**, or press **SELL MKT** while **LONG**, the strategy will **close the existing position only** — it will **never auto-reverse**. This prevents accidental position flips.

### Max Contracts Guard
Once `Position.Quantity >= maxContracts`, all further entries in the same direction are blocked with a dashboard warning. You must close contracts first.

### Entry Delay
After any entry, all further entries are blocked for `entryDelaySeconds` (default: 5 s). Applies to both manual and auto entries.

### Aggressive Limit Timeout
BUY ASK and SELL BID orders that have not been filled within **3 seconds** are automatically cancelled and position state is reset.

---

## 6. Auto Mode — Signal Scoring System

Auto mode uses a **two-stage process**: raw signal scoring followed by multi-layer filter adjustment.

### Stage 1 — Raw Score
The selected strategy scores bull and bear confidence independently on a **0–110 raw scale**. Each scoring event adds points to the relevant side. Points are NOT mutually exclusive — both bull and bear can accumulate simultaneously.

### Stage 2 — Smart Filters
All 10 filters are applied multiplicatively (percentage adjustments up or down). See [Section 8](#8-smart-filters--all-10-layers) for details.

### Final Score
After filters: `score = Max(0, Min(120, filteredScore))`  
Maximum possible after bonus: **120** (rare — requires volume spike and ideal conditions)

### Entry Trigger
```
if (lastBullConfidence >= minSignalConfidence)  → ExecuteLongEntry()
if (lastBearConfidence >= minSignalConfidence)  → ExecuteShortEntry()
```
Default threshold: **68%**. Range: 50–100 (property).

### Confidence Display
The dashboard Bull/Bear labels always display in **green/red**. When below threshold, opacity is reduced proportionally:
```
opacity = score >= threshold ? 1.0 : Max(0.35, score / threshold)
```
This means a 34% score on a 68% threshold shows at ~50% opacity — still clearly green/red, just dimmed.

---

## 7. The 4 Strategies

### Strategy 0 — Momentum + VWAP
**Best for:** Trending intraday sessions with clear EMA direction and VWAP as a reference.

| Signal | Condition | Points |
|---|---|---|
| Bull | EMA Fast > EMA Slow | +30 |
| Bull | Price crosses above VWAP (this bar) | +35 |
| Bull | Price above VWAP (not crossing) | +20 |
| Bull | RSI between 50 and 75 | +20 |
| Bull | Close > Prior High | +15 |
| Bull | VWAP cross + bullish close | +10 (bonus) |
| Bear | EMA Fast < EMA Slow | +30 |
| Bear | Price crosses below VWAP | +35 |
| Bear | Price below VWAP | +20 |
| Bear | RSI between 25 and 50 | +20 |
| Bear | Close < Prior Low | +15 |
| Bear | VWAP cross + bearish close | +10 (bonus) |

**Max raw score:** 110 (VWAP cross + all confirmations)

---

### Strategy 1 — Key Level Breakout
**Best for:** Directional breakouts from significant prior highs/lows.  
**Skipped during:** 9:30–9:35 AM and 12:00–1:30 PM (lunch chop).

Calculates the 20-bar rolling high and low.

| Signal | Condition | Points |
|---|---|---|
| Bull | Price above 20-bar high + cross + bullish close | +50 |
| Bull | Crosses above 20-bar high | +25 |
| Bull | Price above 20-bar high | +25 |
| Bull | Distance from high > 0.15 × ATR | +30 |
| Bull | EMA Fast > EMA Slow | +20 |
| Bear | (mirror of above for 20-bar low) | same |

**Max raw score:** 100

---

### Strategy 2 — Liquidity Sweep Reversal
**Best for:** Stop-hunt reversals — when price briefly sweeps below a swing low (or above a swing high) then snaps back.

Uses the 20-bar swing high and swing low as the sweep levels.

| Signal | Condition | Points |
|---|---|---|
| Bull | Prior bar swept below swing low, current bar recovered | +45 |
| Bull | Weaker form: current bar dipped then closed above low | +25 |
| Bull | RSI < 40 (oversold confirmation) | +30 |
| Bull | Body snapback > 0.3 × ATR | +25 |
| Bull | Volume spike (> 1.5× 20-bar avg) | +10 |
| Bear | (mirror: prior bar swept above swing high, reversed) | same |

**Max raw score:** 110  
**Key insight:** The strongest signal is prior bar sweeping the level AND this bar recovering cleanly. Without that, only the weaker +25 is scored.

---

### Strategy 3 — Opening Range Breakout (ORB)
**Best for:** High-follow-through setups after the opening 9:30–9:45 AM range is established.  
**Active window:** 9:45 AM – 11:30 AM only. Outside this window the strategy returns 0.

The opening range high and low are built from bars between 9:30 and 9:45 AM, then locked.

| Signal | Condition | Points |
|---|---|---|
| Bull | Price above ORB high + cross + bullish close | +55 |
| Bull | Crosses above ORB high | +30 |
| Bull | Price above ORB high | +30 |
| Bull | EMA Fast > EMA Slow | +25 |
| Bull | ATR quality bonus: `20 × (ATR / ORB range)` capped | up to +20 |
| Bear | (mirror for ORB low) | same |

**Max raw score:** 130 (high ATR relative to range means strong follow-through potential)  
**Pro tip:** Wider ORB ranges reduce the ATR bonus. Tighter ORB = more credit for ATR quality.

---

## 8. Smart Filters — All 10 Layers

All 10 filters run sequentially after the raw strategy score. They apply **multiplicative adjustments** — each one can scale confidence up or down without resetting it to zero.

### Filter 1 — Volume Confirmation
Compares current bar's volume to the 20-bar average.

| Volume Ratio | Effect |
|---|---|
| < 0.5× (very low volume) | Both sides × 0.70 (−30%) |
| > 2.0× (volume spike) | Both sides × 1.15 (+15%) |
| Normal | No change |

### Filter 2 — Bull/Bear Conflict
If both bull and bear scores are close to each other (conflict ratio > 70%), it indicates an ambiguous signal.

```
conflict = Min(bull, bear)
dominant = Max(bull, bear)
if conflict / dominant > 0.70 → both × 0.70
```

### Filter 3 — Momentum Exhaustion
Detects extreme one-direction moves with RSI at extremes — a sign the move is exhausted.

| Condition | Effect |
|---|---|
| Candle move > 1.5 × ATR **and** RSI > 80 | Bull × 0.60 |
| Candle move > 1.5 × ATR **and** RSI < 20 | Bear × 0.60 |

### Filter 4 — Bar Quality (Wick Rejection)
A bar dominated by wicks (small body) indicates indecision or rejection.

```
bodyPct = |Close - Open| / (High - Low)
if bodyPct < 0.25 → both × 0.80
```

### Filter 5 — Night Session Penalty
Trading between 6:00 PM and 8:00 AM ET (low liquidity, wide spreads).

```
if time >= 18:00 or time < 08:00 → both × 0.70
```

### Filter 6 — HTF Trend Filter *(v2 new)*
Compares current price to a longer EMA (default period 45) to determine if the trade aligns with the higher time-frame trend.

```
htfDist = (price - EMA45) / ATR
if htfDist > +0.5  → bearish signals × 0.60  (bear fights uptrend)
if htfDist < -0.5  → bullish signals × 0.60  (bull fights downtrend)
```

**Effect:** Trades against the HTF trend require ~67% more raw confidence to reach threshold.

### Filter 7 — VWAP Distance Scaling *(v2 new)*
When price is stretched far from VWAP (measured in ATR multiples), mean-reversion risk increases.

```
dist = |price - VWAP| / ATR
if dist > 2.0:
    penalty = Max(0.5, 1.0 - (dist - 2.0) × 0.15)
    both × penalty
```

At 4 ATRs from VWAP, both sides are penalized to ×0.70. At 5 ATRs, ×0.55. Floor is ×0.50.

### Filter 8 — Swing Structure *(v2 new)*
Analyzes the last 9 bars to detect whether price is making higher lows + higher highs (bullish structure) or lower highs + lower lows (bearish structure).

```
bullStruc = (higher_lows + higher_highs) / 18.0   → 0..1
bearStruc = (lower_lows  + lower_highs)  / 18.0   → 0..1
bias = bullStruc - bearStruc                        → -1..+1

if bias > +0.5  → bear × 0.70  (structure favors bulls)
if bias < -0.5  → bull × 0.70  (structure favors bears)
```

### Filter 9 — Key Level Mid-Range Penalty *(v2 new)*
When price is stuck in the middle third of the Value Area (between 35%–65% of VAH–VAL range), there is less directional edge near the POC.

```
if VAH and VAL are set:
    midLo = VAL + (VAH - VAL) × 0.35
    midHi = VAL + (VAH - VAL) × 0.65
    if price between midLo and midHi → both × 0.80
```

### Filter 10 — Volume Profile Filter *(v2 new)*
Uses the real-time session POC, VAH, and VAL to apply context-aware adjustments.

| Condition | Effect |
|---|---|
| Price within 0.3 ATR of POC (congestion zone) | Both × 0.85 |
| Price at or just above VAH (rejection zone) | Bear × 1.10 (+10%) |
| Price at or just below VAL (support zone) | Bull × 1.10 (+10%) |

---

## 9. Auto Select Mode (Strategy 4★)

Auto Select runs **all 4 strategies simultaneously** every bar and picks the best one based on maximum raw score before filters.

### Scoring
```
for each strategy 0..3:
    compute bull[s], bear[s]
    total[s] = Max(bull[s], bear[s])

bestIdx = argmax(total[s])
```

### Stability Filter
To prevent hyperactive strategy switching (oscillating every bar), the strategy only commits to a new best strategy after it has been **the best for 3 consecutive bars**.

```
if bestIdx == lastBestAutoStrategy:
    autoStratConsecutiveBars++
else:
    autoStratConsecutiveBars = 1
    lastBestAutoStrategy = bestIdx

if autoStratConsecutiveBars >= 3:
    bestAutoStrategy = bestIdx  // commit
// otherwise: keep using previous bestAutoStrategy
```

### Dashboard Display
- Strategy selector shows: `★ Auto → Key Lvl Breakout` (current committed strategy)
- **Active:** label shows which strategy fired the last actual entry

### When to Use Auto Select
Auto Select works best when you are unsure which session regime (trend, breakout, reversal, ORB) is dominant. It dynamically adapts but with a 3-bar confirmation delay to avoid noise.

---

## 10. Adaptive Trailing Stop

The trailing stop activates only after a minimum profit threshold and then follows price dynamically based on market regime.

### Activation
```
if profitPoints >= trailActivationPoints (default: 8 pt)
    → trail activates
    → initial trail price = entry ± (trailDist × 4 ticks)
```

### Trail Distance Calculation
The trail distance is a blend of 5 regime factors (0.0–1.0 each), producing a final distance between `trailMinPoints` and `trailMaxPoints`.

| Factor | Weight | What it measures |
|---|---|---|
| ATR expansion | 25% | Is current ATR above its 10-bar average? |
| EMA spread | 25% | How far apart are fast and slow EMA (normalized by ATR)? |
| Directional bars | 25% | How many of the last 8 bars moved in trade direction? |
| RSI distance from 50 | 10% | Is RSI showing clear momentum? |
| Bar range ratio | 15% | Is current bar range full (vs. ATR)? |

```
trailTrendScore = weighted sum (0.0..1.0)
regimeDist = Lerp(trailMinPoints, trailMaxPoints, trailTrendScore)
atrDist = ATR × trailAtrMultiplier / (tickSize × 4)
finalDist = (regimeDist × 0.60 + atrDist × 0.40) × chopMultiplier × spikeMult
```

- **Choppy markets** (score < 0.5): distance is tightened by `0.5 + score` multiplier
- **Volatility spike** (current ATR > 2× 10-bar avg ATR): distance widened by ×1.5

### Profit Tiers
As the trade advances, the trail floor is raised to protect a minimum return:

| Tier | Activation (profit ≥) | Floor protection |
|---|---|---|
| **T1-BE** | 2× activation pts (≥16 pt) | Floor at break-even (entry + 1 tick) |
| **T2-Strong** | 2.5× activation pts (≥20 pt) | Floor at 45% of max profit |
| **T3-Runner** | 4× activation pts (≥32 pt) | Floor at 55% of max profit |

**T3-Runner** in gold on dashboard means the trade is running powerfully — trail is wide and the floor preserves over half the maximum profit seen.

### Trap-Tightened Trail
When the post-entry trap detector scores ≥ 50 (detected), the trail distance is **multiplied by 0.60** — tightening it by 40% to protect profits faster during potentially adverse price action.

### Dashboard Trail Display
```
Trail: 21486.50 (4.2pt Trend 74% T2-Strong)
       ^price  ^dist  ^regime  ^score   ^tier
```

Color codes:
- 🟡 Gold → T3-Runner
- 🔵 Cyan → T2-Strong  
- 🟡 Yellow → T1-BE
- 🟣 Magenta → Active (below tiers)
- ⚫ Gray → Waiting for activation / Trail OFF

---

## 11. Post-Entry Trap Detector

Monitors each bar after entry for signs that you may have entered a trap (false breakout, stop-hunt reversal against you).

### Scoring (runs once per bar, after entry only)

| Factor | Max Points | Condition |
|---|---|---|
| Adverse move vs. ATR | up to +30 | Price moved against you > 0.5 ATR |
| EMA counter-alignment | +20 | Fast EMA crossed against your direction |
| Volume spike on adverse bar | +15 | Volume > 1.5× 20-bar average AND adverse move |
| Wick rejection | +10 | > 50% of bar is a wick pointing against your trade |

**Total range:** 0–75 points  
**Detection threshold:** ≥ 50 points for ≥ 3 bars in trade  

### What happens when trap is detected?
1. Dashboard `Trap:` label turns **orange-red** and shows `DETECTED`
2. Trail distance is **tightened** to 60% of normal (see [Section 10](#10-adaptive-trailing-stop))
3. The trade is NOT force-closed — you remain in control
4. Exit via SL, trail hit, or manual close

### Dashboard Display
```
Trap: score 35% bars=4      (monitoring, no detection)
Trap: DETECTED (62%) bars=7  (tightened trail active)
```

---

## 12. Pre-Entry Trap Filter

Before auto entries, a **pre-entry risk score** is calculated. If score ≥ 60, auto entry is blocked.

### Scoring

| Factor | Max Points | Condition |
|---|---|---|
| Recent move vs. ATR | up to +40 | Large move in entry direction already happened |
| EMA counter-alignment | +25 | Fast/slow EMA are misaligned with direction |
| Upper wick rejection | +20 | > 45% of bar is upper wick (for longs) |
| Lower wick rejection | +20 | > 45% of bar is lower wick (for shorts) |

**If score ≥ 60:** Auto entry is **blocked** with dashboard message `Entry blocked: trap risk 72%`  
**Manual entries:** Are never blocked by this filter — you retain full control.

---

## 13. Volume Profile (POC / VAH / VAL)

The strategy builds a real-time intraday volume profile — updated every bar using the **Typical Price** (`(H+L+C)/3`) as the price level.

### What it calculates

| Level | Name | Meaning |
|---|---|---|
| **POC** | Point of Control | Price level with the highest volume for the session |
| **VAH** | Value Area High | Upper boundary of the 70% volume zone |
| **VAL** | Value Area Low | Lower boundary of the 70% volume zone |

### Value Area Algorithm
Starting from POC, the algorithm expands upward or downward (whichever has more volume) until 70% of total session volume is captured.

### Chart Lines
| Line | Color | Style |
|---|---|---|
| POC | 🟡 Gold | Solid, 2px |
| VAH | 🔵 Dodger Blue | Dashed, 1px |
| VAL | 🔵 Dodger Blue | Dashed, 1px |

### How the Volume Profile is used for signal filtering
- **Near POC** (within 0.3 ATR): confidence reduced 15% — congestion zone, no edge
- **At/above VAH** (within 0.3 ATR): bearish confidence boosted 10% — potential rejection
- **At/below VAL** (within 0.3 ATR): bullish confidence boosted 10% — potential support
- **Mid Value Area** (35–65% of VA range): confidence reduced 20% — trapped in fair value

### Dashboard
The VWAP line on the dashboard also shows the current POC:
```
VWAP: 21482.25  POC:21475.00
```

---

## 14. Risk Management

### Daily Loss Limit
When `dailyRealizedPnL <= -maxDailyLossDollars`:
- All new entries are blocked
- Any open position is immediately flattened
- Dashboard shows: `DAILY LOSS LIMIT — NO TRADES` in orange-red
- Session trading is halted until the next day

### Daily Profit Target
When `dailyRealizedPnL >= maxDailyProfitDollars`:
- Same behavior as daily loss limit (flatten + halt)
- Dashboard shows: `DAILY PROFIT TARGET — halted`
- Color: gold

### P&L Tracking (Bug 2 fix from v1)
v2 uses **incremental tracking** — only newly closed trades are processed:
```
for i = processedTradeCount to AllTrades.Count:
    dailyRealizedPnL += AllTrades[i].ProfitCurrency
processedTradeCount = AllTrades.Count
```
This replaces the v1 O(n) full scan that ran every tick.

### Unrealized P&L (Bug 1 fix from v1)
v2 calculates unrealized P&L **manually** from the tracked average entry price:
```
priceDiff = (direction == Long) ? close - avgEntry : avgEntry - close
unrealizedPnL = priceDiff × $20/pt × quantity
```
This avoids the `Position.GetUnrealizedProfitLoss()` call that was inaccurate for multi-add positions in v1.

### Emergency Kill
- Immediately flattens all positions via `Account.Flatten`
- Sets `emergencyKillActive = true` AND `dailyLimitHit = true`
- Blocks all further entries for the session
- Prints a warning to the Output window
- Cannot be undone without restarting the strategy
- Uses its own `pendingEmergencyKill` flag — NOT `pendingFlatten` — to ensure the kill executes `ExecuteEmergencyKill()` (not the regular flatten)

### Max Trades Per Day
When `dailyTradeCount >= maxTradesPerDay` (and position is flat), all new entries are blocked.  
- Set to **999** (default) for essentially unlimited
- Each entry (first of a sequence) increments the count
- Adding to an open position (same direction) also increments

### Consecutive Loss Cooldown *(v2 new)*
After each losing trade, `consecutiveLosses` is incremented. While losses > 0:
```
if (DateTime.Now - lastLossTime) < lossCooldownSeconds → auto entry blocked
```
A winning trade resets `consecutiveLosses = 0`. Default cooldown: **120 seconds** (2 min).

Dashboard during cooldown:
```
● Flat — scanning (Bull 45% / need 68%) [CD:87s]
```

---

## 15. Position Sizing (DCA Overhaul)

### What changed from v1
- `dcaEnabled` **removed** — the feature was merged into position limits
- `dcaDistancePoints` **removed** — distance guard eliminated (was causing missed entries)
- `dcaMaxPositions` → **`maxContracts`** — renamed for clarity
- `dcaSuggestionPoints` — **new**: draws a visual suggestion line on chart only. No auto-entry is triggered by it.

### How position sizing works in v2
1. Each button press adds `contracts` lots (configurable)
2. The total quantity is limited to `maxContracts`
3. When max is reached, the dashboard shows: `⚠ Max contracts reached (4)`
4. To add more, you must close contracts first

### Suggestion Line
When a position is open and `dcaSuggestionPoints > 0`, a dotted line is drawn on the chart at `averageEntry + distance` (long) or `averageEntry - distance` (short) as a **visual reference only**. The strategy will not automatically enter at that level.

---

## 16. Trading Hours & Auto-Flatten

### Trading Hours Filter
When `tradingHoursEnabled = true`, auto entries are only allowed between `tradingStartTime` and `tradingEndTime`.

| Parameter | Default | Format |
|---|---|---|
| Trading Start | 93000 | HHMMSS (9:30:00 AM ET) |
| Trading End | 160000 | HHMMSS (4:00:00 PM ET) |
| Auto-Flatten | 165000 | HHMMSS (4:50:00 PM ET) |

The HOURS toggle on the dashboard affects **auto entries only** — manual entries and active position management are always available.

### Auto-Flatten
At `flattenTime`, if a position is open, the strategy calls `Account.Flatten()` automatically. Dashboard shows `EOD flatten — done for today`.

### CME Maintenance Block
All entries are blocked between **6:55 PM – 6:00 PM ET** (`165500–180000`). Positions are flattened at this window if the strategy is live.

---

## 17. Hidden SL / TP System

Stops are implemented entirely in software — **no native NinjaTrader stop orders are placed**. This hides your levels from your broker and avoids order-book exposure.

### Arming
Stops are armed on fill confirmation via `OnOrderUpdate`. If the order is a market entry, stops are armed at the submit tick. For limit orders, stops are armed only after confirmed fill.

### Hidden SL
```
hiddenStopPrice = avgEntry - (slPoints × 4 ticks) for LONG
hiddenStopPrice = avgEntry + (slPoints × 4 ticks) for SHORT
```

### Hidden TP
```
hiddenTargetPrice = avgEntry + (tpPoints × 4 ticks) for LONG
hiddenTargetPrice = avgEntry - (tpPoints × 4 ticks) for SHORT
```

### Monitoring
On every tick (`Calculate.OnEachTick`), the hidden stop and target are compared to `Close[0]`. When hit:
1. All active entry signals are exited
2. `stopsArmed = false`, `pendingExit = true`
3. Print to Output window

### Stale Exit Safety
If `pendingExit = true` for more than **50 ticks** and the position is still open, the strategy forces `Account.Flatten()` as a failsafe.

### Re-Arming
Pressing SL or TP +/- buttons sets `pendingRearm = true`, which re-arms stops at the new distance on the next tick.

---

## 18. JUMP SL

JUMP SL moves the hidden stop loss **toward the current price** by a fixed percentage of the gap between the current stop and price.

```
if LONG:
    gap = currentPrice - hiddenStopPrice
    newSL = hiddenStopPrice + gap × (jumpSlPercent / 100)

if SHORT:
    gap = hiddenStopPrice - currentPrice
    newSL = hiddenStopPrice - gap × (jumpSlPercent / 100)
```

**Example:** SL at 21430, price at 21500, 50% jump  
→ gap = 70 points  
→ new SL = 21430 + 35 = **21465**

The `−  %  +` controls on the dashboard adjust the jump percentage from 10% to 95%.

Floor safety: the stop will never be placed within 1 NQ point (4 ticks) of current price.

After jump, `slPoints` is recalculated from the new distance and the dashboard update shows the new level.

---

## 19. All Parameters Reference

### Group 1 — Risk Management

| Parameter | Default | Range | Description |
|---|---|---|---|
| Stop Loss (NQ pt) | 67 | 1–500 | Hidden SL distance from average entry. 67 pt = $1,340/contract |
| Take Profit (NQ pt) | 50 | 1–500 | Hidden TP distance. 50 pt = $1,000/contract |
| Max Daily Loss ($) | 2000 | 500–10000 | Hard session loss cap |
| Daily Profit Target ($) | 4000 | 500–20000 | Session profit target — halts trading when hit |
| Max Trades Per Day | 999 | 0–999 | 0 = unlimited. Counts first entries only |
| Contracts per entry | 1 | 1–10 | Lots per button press |

### Group 2 — Position Sizing

| Parameter | Default | Range | Description |
|---|---|---|---|
| Max Contracts | 4 | 1–10 | Maximum total contracts open at once |
| DCA Suggestion Distance (pt) | 50 | 0–200 | Visual suggestion line distance. 0 = off |

### Group 3 — Trading Mode

| Parameter | Default | Range | Description |
|---|---|---|---|
| Auto Mode | false | — | Start in auto mode |
| Auto Strategy | 0 | 0–4 | 0=Momentum+VWAP, 1=Key Level, 2=Liq Sweep, 3=ORB, 4=★Auto |
| Min Signal Confidence (%) | 68 | 50–100 | Minimum score to trigger auto entry |
| Entry Delay (s) | 5 | 0–120 | Minimum seconds between consecutive entries |

### Group 4 — Indicators

| Parameter | Default | Range | Description |
|---|---|---|---|
| EMA Fast Period | 9 | 3–50 | Fast EMA used in all 4 strategies |
| EMA Slow Period | 21 | 10–200 | Slow EMA for trend direction |
| RSI Period | 14 | 5–30 | RSI for scoring and exhaustion detection |
| ATR Period | 14 | 5–30 | ATR for trail distance and filter normalization |
| HTF EMA Period | 45 | 10–200 | Higher time-frame EMA for Filter 6 (HTF trend) |

### Group 5 — Display

| Parameter | Default | Description |
|---|---|---|
| SL/TP Adjust Step (pt) | 5 | How many points the +/- buttons change SL/TP |
| Jump SL % | 50 | Default jump percentage (also adjustable on dashboard) |
| Show EMA | true | Overlay fast/slow EMA on chart |
| Show RSI | true | Show RSI sub-panel |
| Show ATR | true | Show ATR sub-panel |
| Show VWAP | true | Draw VWAP line on chart |
| Show Key Levels | true | Draw 20-bar high/low reference lines |
| Show Sweep Signals | true | Mark sweep reversal signals on chart |

### Group 6 — Trading Hours

| Parameter | Default | Description |
|---|---|---|
| Enable Trading Hours Filter | true | When OFF, auto entries fire at any hour |
| Trading Start (HHMMSS) | 93000 | 9:30:00 AM ET |
| Trading End (HHMMSS) | 160000 | 4:00:00 PM ET |
| Auto-Flatten Time (HHMMSS) | 165000 | 4:50:00 PM ET — force-close at this time |

### Group 7 — Adaptive Trail

| Parameter | Default | Range | Description |
|---|---|---|---|
| Enable Adaptive Trail | true | — | Master on/off for trailing stop |
| Trail Activation (pt) | 8 | 1–100 | Profit required before trail starts |
| Trail Min Distance (pt) | 3 | 1–100 | Tightest trail in choppy conditions |
| Trail Max Distance (pt) | 25 | 2–200 | Widest trail in strong trends |
| Trail ATR Multiplier | 1.5 | 0.5–5.0 | ATR component of trail distance calculation |

### Group 8 — Trap Detector

| Parameter | Default | Description |
|---|---|---|
| Enable Post-Entry Trap Detector | true | Monitor for adverse action after entry |
| Volatility Spike Guard Bars | 0 | Skip trail update when bar > 3× ATR. 0 = disabled |

### Group 9 — Signal Quality *(v2 new)*

| Parameter | Default | Description |
|---|---|---|
| HTF Trend Filter | true | Apply Filter 6 — penalize signals against EMA-45 trend |
| Volume Profile Filters | true | Apply Filters 9 & 10 — POC/VAH/VAL context |
| Loss Cooldown (s) | 120 | Pause auto entries after a losing trade. 0 = disabled |

---

## 20. NQ Constants Used

These constants are hard-coded for the NQ/MNQ contract specification:

| Constant | Value | Meaning |
|---|---|---|
| `NQ_TICKS_PER_POINT` | 4.0 | 4 ticks = 1 NQ point |
| `NQ_DOLLARS_PER_TICK` | 5.0 | $5.00 per tick |
| `NQ_DOLLARS_PER_POINT` | 20.0 | $20.00 per point |
| `STALE_EXIT_TICKS` | 50 | Ticks before stale-exit force-flatten |
| `FLAT_SYNC_MAX_TICKS` | 30 | Ticks before safety position reset |
| `TRAIL_SPIKE_MULT` | 2.0 | ATR multiple for volatility spike widening |
| `VALUE_AREA_PCT` | 0.70 | Volume area target for POC expansion |
| `AGGRESSIVE_LIMIT_TIMEOUT_SEC` | 3 | Seconds before BUY ASK / SELL BID order expires |

> **MNQ users:** The dollar values are ×1/10 for MNQ. The point/tick math remains the same. You may want to reduce daily limits accordingly.

---

## 21. Dashboard Layout Map

```
╔════════════════════════════╗
║  ≡  NQ  Mm-ATM  v2        ║  ← drag handle (title bar)
╠════════════════════════════╣
║  [  MANUAL  ] [   AUTO   ]  ║  ← mode toggle
║  Auto Strategy: ◄ [name] ► ║  ← strategy selector (frozen in trade)
║  Active: Key Lvl Breakout  ║  ← last entry strategy
╠════════════════════════════╣
║  Qty:  [-][+]  1    max 4  ║
║  SL:   [-][+]  67pt|268tk|$1340  ║
║  TP:   [-][+]  50pt|200tk|$1000  ║
╠════════════════════════════╣
║  [⇥ JUMP SL] [-] 50% [+]  ║
╠════════════════════════════╣
║  [▲ BUY MKT ] [▼ SELL MKT] ║
║  [△ BUY ASK ] [▽ SELL BID] ║
║  [△ BUY LMT ] [▽ SELL LMT] ║
║  [▣ CLOSE 1 ][⏹ CLOSE ALL ] ║
║  [⚠   EMERGENCY KILL      ] ║
╠════════════════════════════╣
║  ● LONG  ×2                ║  ← status
║  Pos: 2/4  |  Avg: 21485   ║
║  SL: 21418  (67pt|268tk|$2680) ║
║  TP: 21585  (100pt|400tk|$4000) ║
║  [TRAIL:ON] Trail: 21462 (4pt Trend 74% T2-Strong) ║
║  VWAP: 21475.50  POC:21468.00  ║
║  [TRAP:ON ] Trap: score 28%  bars=6  ║
╠════════════════════════════╣
║  Signal Confidence:        ║
║  Bull: 84%     Bear: 12%   ║  ← always green/red
╠════════════════════════════╣
║  Unrealized:  $840.00      ║
║  Daily P&L:   $1,260.00 [Trades: 3] ║
║  Total P&L:   $2,100.00    ║
║  Account P&L: $2,100.00  (R: $1,260  U: $840) ║
║  Balance:     $52,100.00   ║
╠════════════════════════════╣
║  [HOURS:ON] ● Trading allowed (9:30–4:50PM) ║
╚════════════════════════╝⋱  ← resize grip
```

---

## 22. Bug Fixes from v1

| Bug | v1 Behavior | v2 Fix |
|---|---|---|
| **Bug 1 — Unrealized P&L** | Called `Position.GetUnrealizedProfitLoss()` — NT8 returns 0 for multi-add positions | Manual calculation: `priceDiff × $20 × qty` |
| **Bug 2 — O(n) P&L scan** | Iterated all trades in `SystemPerformance` every tick | `processedTradeCount` pointer — only new trades scanned |
| **Bug 3 — Reverse pending** | Clicking opposite direction button triggered a reversal | Clicking opposite direction now closes ONLY — no reverse |
| **Bug 4 — Confidence colors** | Below-threshold confidence was shown in gray | Always green (bull) or red (bear) with proportional opacity |
| **Bug 5 — Emergency Kill wired wrong** | Kill button set `pendingFlatten` → called `ExecuteFlatten()` (regular close, no halt) | Kill button now sets `pendingEmergencyKill` → calls `ExecuteEmergencyKill()` (flatten + halt session) |
| **Bug 6 — Signal name collision** | All entries used `signalName = " "` — NinjaTrader rejects duplicate managed signals, potentially disabling the strategy | Each entry uses `"Entry_" + openDcaCount` — unique per add, enables multi-contract adds |
| **Bug 7 — Dashboard slow open** | Built after 5 ticks × retry cycles (8–18 s delay) | Builds immediately on first Realtime tick; retries every 2 ticks if failed |
| **Bug 8 — Qty unlimited** | `+` button capped at hardcoded `10` regardless of maxContracts | Button now caps at `maxContracts` |
| **Perf — Dashboard update flood** | `UpdateDashboard()` dispatched every single tick | Throttled: every tick in trade, every 3rd tick when flat |
| **Perf — Chart draw flood** | `UpdateOrbLevels()` and `DrawVwapLine()` called every tick | Gated to `IsFirstTickOfBar` only |

---

## 23. Tips & Recommended Settings

### For Pure Manual Trading
- Set **Auto Mode = false** (dashboard default)
- Set **Max Daily Loss = $500–2000** depending on risk tolerance
- Use **JUMP SL 50%** to manually trail by clicking repeatedly
- Enable **TRAIL: ON** for automatic trailing; disable for fixed stops only
- Enable **TRAP: ON** to see whether your entry looks healthy

### For Auto Trading (Aggressive)
```
Auto Strategy = 4 (★ Auto Select)
Min Confidence = 65
Loss Cooldown = 60 s
Max Trades/Day = 10
Trail: ON, Activation = 6 pt, Min = 2 pt, Max = 20 pt
HTF Filter: ON
Volume Profile Filters: ON
```

### For Auto Trading (Conservative)
```
Auto Strategy = 0 (Momentum+VWAP)
Min Confidence = 75
Loss Cooldown = 180 s
Max Trades/Day = 5
Trail: ON, Activation = 10 pt, Min = 4 pt, Max = 30 pt
HTF Filter: ON
Volume Profile Filters: ON
```

### Best Strategy by Session
| Session | Recommended Strategy | Notes |
|---|---|---|
| Pre-market / 9:00–9:30 | 0 — Momentum+VWAP | VWAP is fresh, momentum reliable |
| 9:30–9:45 (opening) | 4 — Auto Select | ORB range building; let auto choose |
| 9:45–11:30 (ORB window) | 3 — Opening Range B/O | Best ORB signal window |
| 11:30–1:30 (lunch) | 2 — Liq Sweep Rev | Low trend, better for reversals |
| 1:30–3:30 (afternoon) | 1 — Key Level B/O | Clear level breaks are more reliable |
| 3:30–4:00 (close) | 0 — Momentum+VWAP | Closing momentum usually resumes trend |

### Avoiding False Signals
- **Enable HTF Filter** — the most impactful single setting for trend alignment
- **Raise Min Confidence to 75+** during choppy market conditions
- **Use Loss Cooldown 120–180 s** to avoid revenge-trading sequences
- **Watch the mid-range penalty** — if Bull and Bear are both dimmed and price is near the POC, the market lacks conviction
- **Use CLOSE 1** not CLOSE ALL when scaling out of a winner to retain exposure

### Dashboard Qty Explained
The **Qty** on the dashboard controls how many contracts are submitted **per entry click**. It does NOT show your current position size.

- **Range:** 1 to `maxContracts` (capped by the +/- buttons)
- Pressing BUY MKT with Qty=2 submits a 2-contract long order
- Pressing BUY MKT again (if `totalContracts + contracts ≤ maxContracts`) adds 2 more
- The **Pos:** line below the status shows your actual position: `Pos: 2/4` = 2 contracts open, max 4 allowed
- If adding `contracts` would exceed `maxContracts`, the entry is blocked with a dashboard warning

### Understanding the Trail Tiers
The tier display on the dashboard tells you how much protection you have:
- No tier shown + **Waiting**: still building to activation point
- **Active** (magenta): trail following price normally
- **T1-BE** (yellow): stop is at or above break-even
- **T2-Strong** (cyan): floor at 45% of max profit — significant protection
- **T3-Runner** (gold): floor at 55% of max profit — let it run, trail is wide

---

*Documentation version: April 2026 — matches Mm_ATM_v2.cs (bugs 5-8 + perf fixes)*
