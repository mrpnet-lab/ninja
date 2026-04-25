# Mm_ATM_v4 — Complete Strategy Specification Prompt
## For generating Mm_ATM_v5 or a fully optimized successor

---
ROLE & EXPERTISE
----------------
You are a senior C# software engineer with 15+ years in algorithmic trading systems,
specializing in NinjaTrader 8.1 (NT8) managed strategies. You are simultaneously an
expert NQ (E-mini NASDAQ-100) futures trader with deep institutional knowledge of how
Market Makers (MMs) and HFT bots operate on the NQ — stop hunts, liquidity sweeps,
false breakouts, iceberg orders, spread widening traps, trap-and-reverse setups,
news-driven fake moves, and systematic targeting of visible stop clusters.
Remember,You are a senior software engineering and an expert in Ninjatrader NT8 8.1+ version. YOu are also an expert in futures trade specially in NQ and know everything about a market makers and their bots an traps. YOu also know every indicator to be very profitable when creating strategies. 

You know that:
- NQ moves in $20/point increments, 4 ticks/point ($5/tick)
- Typical intraday ATR is 20-35 NQ points on a 1-minute chart
- MMs specifically target round numbers, prior swing highs/lows, pre-market levels
- The opening 30 min (9:30-10:00 ET) and news windows are highest-manipulation periods
- VWAP reclaim/rejection is THE institutional reference for intraday direction
- The strategy runs on: Calculate=OnEachTick, EntriesPerDirection=4
---

if you need additional details or have questions, please ask.
---

## 1. Platform & Instrument

- **Platform**: NinjaTrader 8.1+ (NT8), C#, `NinjaTrader.NinjaScript.Strategies`
- **Class**: `public class Mm_ATM_v4 : Strategy`
- **Instrument**: NQ E-mini Nasdaq Futures
- **Tick size**: 0.25 pts = 1 tick
- **Ticks per point**: 4
- **Dollars per tick**: $5
- **Dollars per point**: $20
- **Calculate mode**: `Calculate.OnEachTick` — every tick update
- **Mode**: Managed (not unmanaged), `IsUnmanaged = false`
- **EntriesPerDirection**: 40 (raised from 4 to support scale-in/out without NT silently rejecting entries)
- **EntryHandling**: `EntryHandling.AllEntries`
- **BarsRequiredToTrade**: 25
- **Secondary data series**: `AddDataSeries(BarsPeriodType.Minute, 5)` added in `State.Configure` for HTF bias
- **ErrorHandling**: Default = `StopStrategyCancelOrdersClosePositions` — **all event handlers must be wrapped in try-catch to prevent crashes from disabling the strategy**

---

## 2. Core Architecture

### State Machine
- `State.SetDefaults` — set all parameters and defaults
- `State.Configure` — add indicators, add 5-min secondary data series
- `State.DataLoaded` — initialize all fields (zero out everything), used for backtesting
- `State.Realtime` — reset daily tracking, cancel stale GTC orders, recover existing position, set `rthStartedToday` correctly via warm-start
- `State.Terminated` — tear down dashboard, close DiagLog writer

### Critical Safety Rules (learned from bugs)
- `IsExitOnSessionCloseStrategy = false` — strategy manages its own flatten; do NOT let NT close positions at session boundary
- `ConnectionLossHandling = ConnectionLossHandling.KeepRunning` — prevents position close on 6PM CME data feed hiccup
- `DisconnectDelaySeconds = 10`
- `rthStartedToday` must be reset to `false` in `State.Realtime` BEFORE the warm-start check, otherwise stale historical bar replay sets it true and triggers AUTO-FLATTEN on first overnight trade
- `Position` object can be briefly null after `Account.Flatten` — always guard with `Position == null || Position.MarketPosition == MarketPosition.Flat`
- Button click handlers set `volatile bool` flags only (e.g., `pendingLong = true`) — the actual execution happens in `OnBarUpdate` on next tick (thread safety)
- `EntriesPerDirection = 40` — NT silently rejects `EnterLong()` after N entries per direction cycle; set high to support manual scale-in workflows
- Cancel pending entry orders (`CancelPendingOrders()`) at the start of `ExecuteCloseOne()` and `ExecutePartialClose()` — prevents stale limit orders from silently refilling reduced positions

### Key Constants
```csharp
NQ_TICKS_PER_POINT   = 4.0
NQ_DOLLARS_PER_TICK   = 5.0
NQ_DOLLARS_PER_POINT  = 20.0
STALE_EXIT_TICKS      = 250      // ticks before stale exit retry
FLAT_SYNC_MAX_TICKS   = 200      // grace period for playback fill confirms
DASH_UPDATE_MS        = 333      // dashboard refresh throttle (3x/sec)
TRAIL_SPIKE_MULT      = 2.0      // ATR multiplier to trigger vol spike tightening
VALUE_AREA_PCT        = 0.70     // volume profile value area = 70% of volume
AGGRESSIVE_LIMIT_TIMEOUT_SEC = 3 // BUY ASK / SELL BID limit order timeout
```

---

## 3. All User Parameters (with defaults and rationale)

### Risk Parameters
| Parameter | Default | Notes |
|---|---|---|
| `slPoints` | 20 | Hidden SL in NQ points. Was 25; tightened to reduce avg loss |
| `tpPoints` | 55 | Hidden TP. Was 50; raised to allow runners |
| `maxDailyLossDollars` | 1000 | Realized+unrealized daily loss cap |
| `maxDailyProfitDollars` | 1000 | Daily profit target — disables new entries |
| `contracts` | 1 | Base contract size per entry |
| `maxContracts` | 4 | Hard cap on total position size (DCA limit) |
| `dcaSuggestionPoints` | 50 | Points below avg entry to draw DCA suggestion line |
| `jumpSlPercent` | 50 | % of gap to move SL toward price on JUMP SL command |
| `maxTradesPerDay` | 2 | Auto-mode entry cap per day (manual entries bypass this) |

### Entry/Timing Parameters
| Parameter | Default | Notes |
|---|---|---|
| `entryDelaySeconds` | 1 | Minimum seconds between consecutive entries |
| `autoMode` | false | Enables automatic signal-based entries |
| `autoStrategy` | 0 | 0=Momentum+VWAP, 1=KeyLevel, 2=LiqSweep, 3=ORB, 4=AutoSelect |
| `tradingStartTime` | 93000 | HHMMSS — auto entries blocked before this |
| `tradingEndTime` | 160000 | HHMMSS — auto entries blocked after this |
| `flattenTime` | 165000 | HHMMSS — auto-flatten all positions at this time (4:50 PM ET) |
| `tradingHoursEnabled` | true | When false, auto entries fire at any hour |
| `minSignalConfidence` | 45.0 | Minimum FiltBull/FiltBear score to trigger auto entry |
| `lossCooldownSeconds` | 0 | After a loss, block re-entry for N seconds (0=disabled) |

### Indicator Parameters
| Parameter | Default | Notes |
|---|---|---|
| `emaPeriodFast` | 9 | EMA fast period on 1-min bars |
| `emaPeriodSlow` | 21 | EMA slow period on 1-min bars |
| `htfEmaPeriod` | 45 | Legacy HTF EMA on 1-min bars (one vote in htfBias) |
| `rsiPeriod` | 14 | RSI period |
| `atrPeriod` | 14 | ATR period |
| `slTpAdjustStep` | 5 | Step in points for SL/TP +/- buttons on dashboard |
| `autoSelectStabilityBars` | 2 | Consecutive bars a strategy must be best before switching |

### Display Parameters
| Parameter | Default | Notes |
|---|---|---|
| `showEma` | true | Plot EMA fast (green) and slow (red) on chart |
| `showRsi` | true | Add RSI indicator to chart |
| `showAtr` | true | Add ATR indicator to chart |
| `showVwap` | true | Draw VWAP line on chart |
| `showKeyLevels` | true | Draw 20-bar swing high/low lines |
| `showSweepSignals` | true | Draw sweep reversal arrows |
| `showVolumeProfile` | true | Draw POC/VAH/VAL lines |

### Filter Parameters
| Parameter | Default | Notes |
|---|---|---|
| `htfFilterEnabled` | true | Enable 3-vote composite HTF bias filter |
| `useVolumeProfileFilters` | true | Enable VP-based signal filters |
| `enablePostEntryTrapDetector` | true | Enable trap score monitoring while in position |
| `enableSessionOverride` | true | Allow session-time strategy adjustments |

### Trailing Stop Parameters
| Parameter | Default | Notes |
|---|---|---|
| `trailEnabled` | true | Enable adaptive trailing stop |
| `trailActivationPoints` | 5 | Profit (pts) before trail activates |
| `trailMinPoints` | 4 | Minimum trail distance |
| `trailMaxPoints` | 25 | Maximum trail distance |
| `trailAtrMultiplier` | 1.5 | ATR weight in dynamic trail calculation |
| `trailMinBarsAfterEntry` | 2 | Don't activate trail within first 2 bars (prevents entry-bar gap exit) |
| `smartTrailEnabled` | true | Enable Smart Trail pause on MM sweep detection |
| `smartTrailMaxPauseBars` | 8 | Max bars trail can be paused |
| `smartSlEnabled` | true | Enable Smart SL (breakeven, tighten, loosen, trap escape) |
| `smartSlBePct` | 25 | % of TP to reach before Smart SL locks breakeven |

### Diagnostic Log Parameters
| Parameter | Default | Notes |
|---|---|---|
| `enableDiagLog` | false | Write CSV DiagLog. Only enable in playback |
| `diagAppend` | false | Append to existing file vs overwrite |
| `diagOneDayOnly` | true | Stop logging when date rolls (prevents huge files) |

---

## 4. Signal Calculation System

### Architecture
`CalculateSignals()` → calls one or more `Calc*()` methods to produce `lastBullConfidence` / `lastBearConfidence` (raw 0-100+ scores) → then `ApplySmartFilters()` adjusts to produce filtered scores → filtered scores compared to `effectiveMinConf` at entry time.

### Four Strategy Modules

#### Strategy 0: Momentum + VWAP (`CalcMomentumVwap`)
- EMA alignment + rising/falling direction: +30 bull or bear
- VWAP crossover: +35; continuation above/below (mature session): +20
- RSI directional (>55 rising or <45 falling): +20
- Price bar breakout (close > prior high): +15
- VWAP cross + bullish bar: +10
- Counter-trend EMA penalty: subtract 20 from wrong-direction score

#### Strategy 1: Key Level Breakout (`CalcKeyLevelBreakout`)
- 20-bar high/low breakout with bullish/bearish candle: +50
- Just crossing the level: +25
- Sustained beyond level: +25
- Confirmed breakout (0.15 ATR beyond): +30
- EMA alignment: +20

#### Strategy 2: Liquidity Sweep Reversal (`CalcLiquiditySweepReversal`)
- Sweep below 19-bar swing low and close back above: +45; partial: +25
- RSI oversold/overbought: +30
- Strong close relative to bar range: +25
- Volume confirmation: +10

#### Strategy 3: Opening Range Breakout (`CalcOpeningRangeBreakout`)
- Only fires 09:45-11:30 (after ORB is set at 09:30-09:45)
- Clean ORB break with directional bar: +55; just the break: +30; sustained: +30
- EMA alignment: +25
- ATR vs ORB range quality: +0 to +20

#### Auto Select (Strategy 4)
- Scores strategies 0 and 1 each bar (2 and 3 excluded based on performance data)
- Each strategy scored independently with `ApplySmartFilters()`
- Best strategy must win for `autoSelectStabilityBars` consecutive bars before switching

### 20-Filter Signal Processing (`ApplySmartFilters`)

| # | Filter | Effect |
|---|---|---|
| 1 | Volume Confirmation | Low vol (< 0.5x avg): x0.7; High vol (> 2x): x1.15 |
| 2 | Bull/Bear Conflict | When conflict/dominant > 0.80: x0.7 both |
| 3 | Momentum Exhaustion | Move > 1.5 ATR + RSI >70/<30: x0.6 |
| 4 | Bar Quality (wick) | Body < 15% of range: x0.8 both |
| 5 | Night Session Penalty | Time >=18:00 or <08:00: x0.7 both |
| 6 | HTF Multi-Vote Trend | htfBias=+1: bear x0.50 / bull x1.10; htfBias=-1: bull x0.50 / bear x1.10 |
| 7 | VWAP Distance Scaling | > 2 ATR from VWAP: progressive penalty down to x0.5 |
| 8 | Swing Structure | Lower highs (bearish): bull x0.7; Higher lows (bullish): bear x0.7 |
| 9 | Key Level Mid-Range | Price in middle 30% of value area: x0.80 both |
| 10 | Volume Profile | Near POC (< 0.3 ATR): x0.85; near VAH/VAL: x1.1 for bounce side |
| 11 | RSI/Price Divergence | Bearish div: +10 bear; Bullish div: +10 bull |
| 12 | VWAP Slope | Rising VWAP: +10 bull, -5 bear; Falling: +10 bear, -5 bull |
| 13 | 5-Bar Price Trend | Slope > 0.4 ATR: suppress counter-direction by up to 50% |
| 14 | Lunch Doldrums | 11:30-14:00 ET: x0.85 both |
| 15 | EMA Cross Direction | EMA fast < slow: bull x0.65; EMA fast > slow: bear x0.65 |
| 16 | Consec Loss Suppression | 2+ losses same dir: losing dir x max(0.6, 1-(n-1)x0.12) |
| 17 | (removed) | Was chop detection — replaced by RSI direction at entry |
| 18 | PrevDay S/R Bounce | Near prevDay high/low w/ EMA agreement: +10 boost |
| 19 | Confidence Flip Bonus | When dominant side just reversed: +15 to new direction |
| 20 | EMA Cross Momentum | Within 3 bars of EMA cross: +25/+20/+15 to crossing direction |

**Penalty floor**: After all filters, neither score can fall below 50% of its raw value.

**Score cap**: Both scores clamped to [0, 120].

---

## 5. Entry Gate (`CanEnterTrade`)

In order, these conditions block any entry:
1. `pendingExit == true` — blocked
2. `dailyLimitHit || dailyProfitHit` — blocked
3. `emergencyKillActive` — blocked
4. Auto only: `maxTradesPerDay > 0 && !isManual && dailyTradeCount >= maxTradesPerDay && flat` — blocked
5. Time 16:55-18:00 (CME maintenance window) — blocked
6. Auto only: `tradingHoursEnabled && (time < tradingStartTime || time >= flattenTime)` — blocked
7. Opposing direction when in position — `ExecutePartialClose(contracts)` (Chart Trader behavior)
8. `Position.Quantity >= maxContracts` (same direction) — blocked (Max contracts reached)
9. `entryDelaySeconds` cooldown — blocked

### Additional Auto-Entry Guards (in `OnBarUpdate` before `CanEnterTrade`)
- `postLossCooldown`: skip next 10 bars after any loss
- `tooSoonAfterTrade`: minimum 5-bar spacing between trades
- `trapEscapeCooldown`: skip 10 bars after a trap escape exit
- `rsiDown` / `rsiUp`: RSI 2-bar slope must agree with direction
- Midday (11:00-14:00): `effectiveMinConf = max(minSignalConfidence, 50.0)`
- Overextension guard: price > 1.5 ATR from EmaFast (or 3.0 ATR on confirmed trend day) — blocked
- Volatility spike bar: bar range > 2.0 ATR — blocked
- `nearPrevDayHigh`: approaching prevDay high from below (within 0.5 ATR) — LONG blocked
- `nearPrevDayLow`: at or above prevDay low (within 0.5 ATR) — SHORT blocked; BELOW prevDay low = breakdown, SHORT allowed
- HTF directional block: `htfBias < 0 || sessionConfirmedBias < 0` — LONG blocked; opposite — SHORT blocked
- Min EMA gap: confirmed trend day = 0.10pt min; unconfirmed day = 1.0pt min
- EMA fresh cross block: non-flip trade when EMA just crossed from strongly opposing position

---

## 6. Six Entry Methods

| Method | Button | Order Type | Direction |
|---|---|---|---|
| `ExecuteLongEntry(isManual)` | BUY MKT | `EnterLong(contracts, signalName)` | Long market |
| `ExecuteShortEntry(isManual)` | SELL MKT | `EnterShort(contracts, signalName)` | Short market |
| `ExecuteBuyAskEntry()` | BUY ASK | `EnterLongLimit(contracts, ask, signalName)` | Long limit at ask |
| `ExecuteSellBidEntry()` | SELL BID | `EnterShortLimit(contracts, bid, signalName)` | Short limit at bid |
| `ExecuteLongLimitEntry()` | BUY LMT | `EnterLongLimit(contracts, bid, signalName)` | Long limit at bid (passive) |
| `ExecuteShortLimitEntry()` | SELL LMT | `EnterShortLimit(contracts, ask, signalName)` | Short limit at ask (passive) |

**On every entry:**
- If flat: `tradeSequence++; dailyTradeCount++`
- `openDcaCount++`
- `activeEntrySignals.Add(signalName)` (unique signal names required by NT managed mode)
- `openTradeDirection` set
- `lastEntryWallTime` set (for cooldown)
- For ASK/BID: `aggressiveLimitSubmitTime` set (3-sec timeout before cancel)
- Pre-entry trap check: if `CalcPreEntryTrapScore() >= 60` — auto entry blocked; manual entry shows warning

### Position Recovery (Sync)
`SyncPositionState()` runs every tick. When position exists but `stopsArmed == false`:
- Recovers `openTradeDirection`, `averageEntryPrice`, `totalContracts`
- Calls `ArmHiddenStops()` to set SL/TP
- Logs `SYNC_RECOVER`

---

## 7. Hidden SL / TP System (`ArmHiddenStops`)

- All stops are **software-managed** — no native NT stops submitted to broker
- Checked in `MonitorHiddenStops()` every tick when `stopsArmed == true`
- **SL**: `averageEntryPrice +/- slPoints x 4 x TickSize`
- **TP**: `averageEntryPrice +/- tpPoints x 4 x TickSize`
- **Runner Mode**: When `htfBias` agrees with trade direction, `effectiveTp = 500` pts (no TP, trail-only exit)
- **Dynamic TP**: TP clamped to prevDay level if it sits between entry and original TP (with 8pt minimum buffer)
- On SL/TP hit: `ExitLong/Short(" ", " ")`, `stopsArmed = false`, `pendingExit = true`
- **Critical**: `ArmHiddenStops()` resets trail state (trailActive, trailPrice, trailMaxProfitPts, etc.) — prevents stale max-profit values producing corrupted tier floors

---

## 8. Adaptive Trailing Stop System

### Activation
- Wait `trailMinBarsAfterEntry` bars after entry (default 2) — prevents gap-open bar exits
- Profit must reach `dynamicActivation = max(trailActivationPoints, ATR x 0.4)`
- Chop regime: if `trailTrendScore < 0.4` and HTF doesn't agree, use `min(4.0, dynamicActivation)`

### Trail Distance Calculation (`CalculateTrailDistance`)
Five-factor weighted score [0,1]:
- ATR expansion (25%): current ATR vs 10-bar avg
- EMA spread (25%): |EmaFast - EmaSlow| / ATR
- Directional bars (25%): % of last 8 bars closing in trade direction
- RSI distance (10%): |RSI - 50| / 50
- Bar range (15%): barRange / ATR

Final distance: `lerp(trailMin, trailMax, trailTrendScore) x 0.6 + ATR x multiplier x 0.4`
- Chop multiplier: if score < 0.5, multiply by `0.5 + score`
- Volatility spike: if ATR > 2x ATR-avg, multiply by 0.8
- Counter-EMA tightening: if EMAs against trade direction, multiply by 0.75

### Profit Tier Floors (prevents trail from locking in too early)
| Tier | Trigger | Floor |
|---|---|---|
| T3-Runner | maxProfit >= activation x 4 | 50% (trend) or 45% (mixed) of max profit. Cap = tpPoints distance |
| T2-Strong | maxProfit >= activation x 2.5 | 40% of max profit |
| T1-BE | maxProfit >= activation x 1.5 AND NOT runner mode | 1 tick (breakeven lock) |
| Active | below T1 | No floor, pure trail distance |

### Runner Mode Widening
When EMA gap expanding in trade direction OR htfBias agrees + profit >= 10pts:
- Trail widens to `max(trailDist x 2.0, ATR x 0.8)` up to `trailMaxPoints`
- `trailTierName = "Runner"`

### Structural Snap
Finds nearest swing structure (5-bar lookback) and snaps trail to it if tighter but still >= `trailMinPoints` from price.

### Chop Regime
If `trailTrendScore < 0.4` AND HTF doesn't agree — use `trailMinPoints` distance (exit on first reversal in choppy market).

---

## 9. Smart Trail — MM Sweep Pause

Pauses trail for up to `smartTrailMaxPauseBars` bars when all conditions met:
1. Profit >= 1.5x activation
2. Pullback detected: prior candle high - current close > 0.3 ATR
3. EMAs still aligned with trade direction
4. Low volume on pullback (< 1.5x avg vol)
5. Not near major S/R (swing level within 1 ATR)

While paused: trail price frozen at `smartTrailFrozenPrice`. If price breaks frozen trail — exit immediately.

Resume conditions: profit drops below activation, OR ATR spike, OR `smartTrailMaxPauseBars` exceeded.

**Stop Hunt Detection** (in `MonitorPostEntryTrap`): when current bar makes new extreme vs 5-bar history but closes back inside — `trapSuspendBarsLeft = 4` (suppress trap detection for 4 bars, identified as MM stop sweep).

---

## 10. Smart SL System

Three modes triggered by `MonitorSmartSL()` every bar while `stopsArmed`:

**A) Breakeven Lock**: When profit >= `tpPoints x (smartSlBePct / 100)` pts — move SL to `averageEntryPrice + TickSize`. Once done, never reverts.

**B) Trap Tighten**: When `trapDetected && trapScore >= 45` — tighten SL to 75% of original distance from entry.

**B2) Trap Escape**:
- Immediate: `trapScore >= 65 && profitPts < -10` — `ExitLong/Short()` immediately
- Gradual: `trapScore >= 50 && profitPts < -5` for 2 consecutive bars — exit. Sets `trapEscapeCooldownBar`.

**C) High-Confidence Loosen**: When `smartSlEntryConfidence >= minSignalConfidence + 8`, adverse move < 0.5 ATR, EMAs agree — extend SL by up to `min(15pt, slPoints x 30%)`. Once done, never repeat.

---

## 11. Post-Entry Trap Detector

Runs every bar while in position (`MonitorPostEntryTrap()`). Produces `trapScore` [0-100]:

**Score components:**
- Adverse move ratio (vs normalized ATR, min 10pt): up to +30, plus absolute adverse bonus (+15 if > 6pts)
- EMAs against trade direction: +20
- High volume adverse bar: +15
- Significant wick against position: +10

**Enhanced detection:**
- **Stop Hunt Spike**: new extreme vs 5-bar history + close back inside — `trapSuspendBarsLeft = 4`, score x0.5 during suspension
- **Volume Absorption**: large range bar + high volume + close near midpoint — score x0.6 (absorption detected, not a trap)
- **Time-based Decay**: new score < trapScore: blend `trapScore x 0.85 + newScore x 0.15`

`trapDetected = trapScore >= 50`

---

## 12. Session & Daily Management

### Session Reset (`ResetDailyTracking`)
Fires when `Time[0].Date != sessionDate`:
- Saves `currDayHigh/Low/Open` as `prevDayHigh/Low/Close/Open`
- Resets all daily counters: `dailyRealizedPnL`, `dailyTradeCount`, `processedTradeCount`
- Resets all flags: `flattenFired`, `rthStartedToday`, `emergencyKillActive`
- Resets loss tracking: `consecutiveLosses`, `lastLossDirection`, `lastLossBarNumber`
- Resets HTF session tracking: `htfBiasConsecBull/Bear`, `sessionConfirmedBias`
- Clears volume profile, ORB levels

### Auto-Flatten Logic
- `rthStartedToday` set to `true` when first RTH bar (>= `tradingStartTime` and < `flattenTime`) is seen
- At `flattenTime` (16:50): if `rthStartedToday && !flattenFired && not flat` — `ExecuteFlatten()`
- CME 5PM-6PM window: additional flatten gate (16:55-18:00) for CME maintenance
- `rthStartedToday = false` in `State.Realtime` prevents overnight re-flatten bug

### Daily P&L Tracking
- Incremental: processes only new trades since last check via `SystemPerformance.AllTrades` index
- `pnlBaselineOffset` subtracted so only today's trades count
- Live P&L check (every tick in position): `realized + unrealized` vs limits
- On limit breach: set `dailyLimitHit/Profit`, call `ExecuteFlatten()`

### HTF Bias System (3-Vote Composite)
Each bar, three votes:
1. **5-min EMA alignment**: `indEma5mFast > indEma5mSlow` = +1 (bull) / -1 (bear)
2. **Session open bias**: `Close - currDayOpen > threshold(0.3 ATR)` = +1 / -1 / 0
3. **Legacy 45-EMA trend**: `Close vs EMA45 x 1.5 ATR` = +1 / -1 / 0

`htfBias = +1` if votes >= 2, `-1` if votes <= -2, `0` if mixed.

**Session Confirmed Bias** (`sessionConfirmedBias`): after 10 consecutive bars of same `htfBias`, direction is confirmed. Relaxes EMA gap minimum to 0.10pt; switches to intermediate filter penalty (x0.65) when `htfBias` temporarily reads 0.

---

## 13. Flatten / Exit Methods

| Method | Trigger | Action |
|---|---|---|
| `ExecuteFlatten()` | Time-based, daily limit, CME, button | `CancelPendingOrders()` + `Account.Flatten([Instrument])` in Realtime, `ManagedExitAll()` in Historical |
| `ExecuteCloseTrade()` | CLOSE ALL button | `CancelPendingOrders()` + managed `ExitLong/Short(" ", " ")` |
| `ExecuteCloseOne()` | CLOSE 1 button | `CancelPendingOrders()` + `ExitLong/Short(1, "", sig)`, decrement `openDcaCount` and `totalContracts`, re-arm stops |
| `ExecutePartialClose(qty)` | Opposing direction button | `CancelPendingOrders()` + partial exit, update tracking |
| `ExecuteEmergencyKill()` | EMERGENCY KILL button | Sets `emergencyKillActive = true`, `dailyLimitHit = true`, flattens |
| `ExecuteJumpSL()` | JUMP SL button | Moves hidden SL by `jumpSlPercent%` of gap toward price |
| `ManagedExitAll()` | Fallback when `Account.Flatten` fails | `ExitLong/Short(" ", " ")` |

**`CancelPendingOrders()`** scans `Account.Orders` for Working/Accepted/Submitted orders on this instrument and cancels them. Returns `bool` (whether any were cancelled).

---

## 14. EMA-Against Exit

Every bar while `stopsArmed && !pendingExit && in position`:
- Check if EMAs are aligned **against** trade direction (`openTradeDirection == 1 && emaFast < emaSlow`)
- Check if trade is underwater > 2pts (`unrealPtsNow < -2.0 x TickSize x NQ_TICKS_PER_POINT`)
- If both: `emaBarsAgainstTrade++`; if counter reaches 2 — exit

Catches sustained reversals with position underwater — not just momentary EMA fluctuations.

---

## 15. Dashboard (WPF On-Chart Panel)

### Layout (top to bottom)
1. **Title bar**: "NQ  Mm-ATM  v4" — draggable (click-drag to move panel)
2. **Mode toggles**: MANUAL | AUTO
3. **Strategy selector**: < [Strategy Name] > + "Active: —" label
4. **Separator**
5. **Qty row**: - | [value] | + (1 to maxContracts); max label
6. **SL row**: - | [pts | ticks | $] | + (step = slTpAdjustStep; triggers `pendingRearm`)
7. **TP row**: same
8. **Separator**
9. **Jump SL row**: JUMP SL | - | [pct%] | +
10. **Separator**
11. **BUY MKT / SELL MKT** (large, 125x36)
12. **BUY ASK / SELL BID** (medium, 125x30)
13. **BUY LMT / SELL LMT** (medium, 125x30)
14. **CLOSE 1 | CLOSE ALL** row
15. **EMERGENCY KILL** (full width, red)
16. **Separator**
17. **Status line** (color-coded)
18. **Pos**: qty/max | avg entry
19. **Hidden SL** (red)
20. **Hidden TP** (green)
21. **TRAIL: ON/OFF** toggle + trail info
22. **VWAP value**
23. **TRAP: ON/OFF** toggle + trap score
24. **S-TRAIL** toggle + smart trail status
25. **SMART SL** toggle + smart SL status
26. **Separator**
27. **Signal Confidence**: Bull: X% | Bear: X%
28. **Separator**
29. **Unrealized P&L** (live, manual calc tick-by-tick)
30. **Daily P&L**
31. **Total P&L**
32. **Account P&L** (from account object)
33. **Balance**
34. **Separator**
35. **HOURS: ON/OFF** toggle + trading status
36. **Resize grip** (drag corner to resize)

### Dashboard Behavior
- Built in `ChartControl.Dispatcher.InvokeAsync` (UI thread)
- Retries up to 5 times if chart not ready
- Draggable via title bar; resizable via corner grip
- All button clicks set `volatile bool` flags — execution deferred to `OnBarUpdate`
- MANUAL/AUTO buttons toggle `autoMode` and re-color themselves
- Strategy < > buttons cycle `autoStrategy` 0-4; frozen while in trade
- SL/TP -/+ buttons update `slPoints`/`tpPoints` and set `pendingRearm = true` to re-arm stops
- JUMP SL -/+ buttons adjust `jumpSlPercent` (10-95%) in 10% steps
- TRAIL/TRAP/S-TRAIL/SMART SL toggles flip respective enable flags live

### Dashboard Color Codes
- Status: CornflowerBlue (flat), LimeGreen (long), OrangeRed (short/loss limit), Gold (profit target), Yellow (transitioning), Orange (warning)
- Bull confidence: LimeGreen with opacity = score/120
- Bear confidence: OrangeRed with opacity = score/120
- Unrealized P&L: Green if positive, Red if negative

---

## 16. Chart Drawing

All drawn objects use `Draw.*` API:
- **Hidden SL line**: OrangeRed dashed-dotdot, width 2
- **Hidden TP line**: LimeGreen dashed-dotdot, width 2
- **Avg entry line**: DodgerBlue dotted, width 1
- **Trail line**: Magenta dash-dot, width 2 + text label showing pts and regime
- **DCA suggestion**: DeepSkyBlue dotted (at `dcaSuggestionPoints` below avg entry)
- **VWAP**: Yellow solid line (segment per bar, removes old segments after 50 bars)
- **Key Levels**: Cyan dashed (20-bar swing high/low)
- **Sweep signals**: Magenta dotted swing lines + LimeGreen/OrangeRed arrows
- **Volume Profile**: Gold POC solid (width 5), DodgerBlue VAH/VAL dashed
- **ORB levels**: LimeGreen dashed (high), OrangeRed dashed (low)
- **SL label text**: pts | ticks | $ at offset below/above SL line
- **TP label text**: same above/below TP line

---

## 17. Volume Profile

- Tick-by-tick: `volumeAtPrice[typicalPrice] += volume` (SortedDictionary)
- Recalculates `POC/VAH/VAL` every 20 bars AND when at least 5 new price levels added
- Value area = 70% of session volume expanding from POC (bidirectional)
- Session resets volume profile on date change

---

## 18. VWAP (Manual Tick-Safe Implementation)

Not using NT's built-in VWAP — implements custom cumulative TPV/Vol to avoid cross-bar accumulation bugs in Calculate.OnEachTick mode.

- On `IsFirstTickOfBar`: commit prior bar's TPV/Vol to cumulative totals; start new bar accumulators
- Each tick: update `currBarTPV = typicalPrice x volume`, `currBarVol = volume`
- `vwapValue = (vwapCumTPV + currBarTPV) / (vwapCumVol + currBarVol)`
- Resets on session date change via `ResetVwap()`

---

## 19. Diagnostic Log (CSV)

**Location**: `Documents\NinjaTrader 8\MmATM_DiagLog_YYYYMMDD.csv`

**38 columns**: DateTime, Bar, OHLCV, VWAP, EmaFast, EmaSlow, EmaFSlope, RSI, RSISlope, ATR, POC, VAH, VAL, RawBull, RawBear, FiltBull, FiltBear, EmaAlign, RSIDir, Slope5ATR, ConsecLoss, LastLossDir, AutoStrat, Position, OpenDir, TrapScore, UnrealPts, Action, Detail, PrevDayHigh, PrevDayLow, PrevDayClose, PrevDayOpen, HtfBias, SessOpenBias

**Action types**: SIGNAL, ENTRY_LONG, ENTRY_SHORT, ENTRY_BLOCKED, EXIT_WIN, EXIT_LOSS, FLATTEN, RTH_ARMED, TRADE_BAR

**Use**: Enable `diagOneDayOnly=true`, `enableDiagLog=true` in NT playback only. Analyze to tune `minSignalConfidence`, identify which filters block good setups, and diagnose which entry patterns have positive expectancy.

---

## 20. Thread Safety Architecture

All UI button events run on the WPF dispatcher thread. All NinjaScript logic runs on the NT strategy thread. Bridge:

```csharp
// In button click (UI thread):
pendingLong = true;  // volatile bool set

// In OnBarUpdate (NT strategy thread):
if (pendingLong) { pendingLong = false; ExecuteLongEntry(true); }
```

All 12 pending flags are `volatile bool`:
`pendingLong`, `pendingShort`, `pendingLongLimit`, `pendingShortLimit`, `pendingBuyAsk`, `pendingSellBid`, `pendingFlatten`, `pendingCloseTrade`, `pendingCloseOne`, `pendingJumpSL`, `pendingRearm`, `pendingExit`

Also `volatile int` for `slPoints`, `tpPoints`, `contracts` (modified from UI thread adjust buttons).

---

## 21. Known Bugs Fixed in v4 (Lessons Learned)

| Bug | Root Cause | Fix |
|---|---|---|
| Position closed after 6PM | `rthStartedToday` stale from historical bars + `flattenFired` reset in `State.Realtime` = immediate flatten | Reset `rthStartedToday = false` in State.Realtime before warm-start check |
| Strategy disabling after fill | Unhandled exception in event handlers triggering NT ErrorHandling | Wrap all 5 event handlers in try-catch; print exception to Output |
| Trail at NQ price level (36428) | `trailMaxProfitPts` stale when `averageEntryPrice=0` on tick, profit = price/1.0 stored | Reset trail state in `ArmHiddenStops()`; cap tier floor at `tpPoints` |
| Re-buy blocked after close | Stale BUY LMT orders filling closed contracts, pushing qty back to maxContracts | `CancelPendingOrders()` at start of `ExecuteCloseOne()` and `ExecutePartialClose()` |
| Buy blocked after 4+ DCA cycle | `EntriesPerDirection = 4` silent NT rejection | Raise to 40 |
| null ref at `Position.MarketPosition` after Account.Flatten | NT `Position` briefly null on next tick | Guard: `Position == null || Position.MarketPosition == MarketPosition.Flat` |
| Manual entries blocked after auto hit maxTradesPerDay | `maxTradesPerDay` check didn't discriminate manual vs auto | Add `!isManual` condition |
| Strategy reloads close overnight position | `IsExitOnSessionCloseStrategy = true` | Set to `false` |
| 6PM CME session roll closes position | Default `ConnectionLossHandling.Recalculate` restarts strategy on brief disconnect | Set `ConnectionLossHandling.KeepRunning` |
| Stale GTC orders fill on reload | Old orders remaining from prior strategy instance | `CancelPendingOrders()` in `State.Realtime` startup |
| Short entries blocked near prevDayLow breakdown | `nearPrevDayLow` blocked ALL shorts near prevDayLow | Only block when `Close >= prevDayLow`; allow when price breaks below |
| Trail exits too early | `trailMinBarsAfterEntry = 0` — exit on gap-open entry bar itself | Added `trailMinBarsAfterEntry = 2` guard |
| T1-BE exit in runner mode | Breakeven lock fires even when htfBias confirms strong trend | Skip T1-BE tier when `htfAgrees == true` |

---

## 22. Recommended Improvements for v5

### Entry Quality
- **ML signal scoring**: Replace static point-based scoring with a trained classifier using DiagLog CSV data. Features: EMA gap, RSI level/slope, VWAP distance, ATR expansion, volume ratio, prevDay S/R distance, time of day, htfBias.
- **Order flow confirmation**: Add bid/ask volume imbalance check at entry bar. Requires market depth subscription.
- **Session regime detection**: Classify session as Trend vs Chop vs Reversal using statistical measures of ATR, EMA spread, and directional efficiency ratio — selects optimal strategy and parameters for that regime automatically.
- **Better ORB timing**: Allow ORB strategy to re-fire after lunchtime consolidation (currently blocked until 11:30).

### Risk Management
- **Dynamic SL based on ATR**: Instead of fixed 20pts, use `SL = max(15, min(30, ATR x 1.5))` so SL breathes with market volatility.
- **Dynamic TP based on session range**: Scale TP to prevDay range x 0.40 — 55pts is aggressive when daily range was 60pts.
- **Scaled entries**: First entry at 1 contract, optional DCA only if HTF agrees (never add to losers counter-trend).
- **Asymmetric SL/TP on runner**: In confirmed runner mode, remove TP entirely and use min trail distance for maximum capture.

### Trail Improvements
- **Breakeven lock at 50% of SL distance** — current 25% of TP is too generous for unsuccessful entries. Lock BE faster when not in runner mode.
- **Time-based trail tightening**: After 2 bars of no new high-water mark, begin tightening trail by 10% per bar.
- **VWAP-relative trail**: Anchor trail distance to VWAP deviation — widen when price has momentum away from VWAP, tighten when price is re-approaching VWAP.

### Architecture
- **Separate auto and manual position tracking**: Allow manual and auto trades to coexist with independent SL/TP tracking.
- **Replay DiagLog analyzer**: Add a button in the dashboard that shows today's DiagLog stats inline (trades, win rate, avg win/loss, signal distribution).
- **Persistent dashboard position**: Save/restore dashboard X/Y position across strategy restarts using NinjaTrader's instrument storage.
- **Alert sounds**: Play different tones for entry, SL hit, TP hit, trail activation, daily limit.
- **Separate v5 into partial classes**: Split into logical files (Signals.cs, Trail.cs, Dashboard.cs, Risk.cs, Execution.cs) to make each ~800-1000 lines instead of one 5000+ line file.

---

## 23. File & Build Info

- **VS Code workspace**: `c:\Github\mrpnet-lab\Strategies\Mm_ATM_v4.cs` (5246 lines)
- **NT compile target**: `C:\Users\Marcio\Documents\NinjaTrader 8\bin\Custom\Strategies\MmATMv4.cs`
- **Prior versions**: v1, v2, v3 in same folder (v3 is current production; v4 is next-gen dev)
- **Build**: NT8 native compile (NinjaScript editor or Import NinjaScript)

---

*Generated from full code analysis of `Mm_ATM_v4.cs` (5246 lines, April 2026). All parameters, logic flows, bugs, and fixes are derived from the actual compiled code and live trading session logs.*
