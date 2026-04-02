# Mm_ATM_v1 — NQ Semi-Auto / Auto Strategy for NinjaTrader 8

**Version**: 1.0 (based on Mm-ATM v2.0)  
**Platform**: NinjaTrader 8.1.x — Managed Strategy Framework  
**Instrument**: NQ Futures (Micro/Mini NQ, TickSize = 0.25)

---

## Core Features

- Floating overlay dashboard (drag title bar, resize grip)
- **6 entry buttons**: Buy MKT / Sell MKT, Buy ASK / Sell BID (aggressive limit), Buy LMT / Sell LMT
- Close 1 (partial close), Close All, Emergency Kill (permanent halt)
- Jump SL button (move SL closer by configurable %)
- Adjustable hidden SL/TP via +/- buttons (live mid-trade)
- Auto / Manual mode toggle on dashboard
- Auto strategy selector (5 modes: 0-3 + Auto Select)
- Adjustable order qty on dashboard
- 4-row P&L display: Unrealized, Realized, Total, Account Cash
- Confidence score display for selected auto strategy
- EMA / RSI / ATR / VWAP indicators on chart (toggleable)
- Liquidity sweep reversal markers on chart
- Key level breakout lines on chart
- DCA: press same-direction button to add (up to max)
- SL/TP reset to defaults after each trade closes
- Post-entry trap detector with risk assessment

---

## NQ Constants

| Constant | Value | Description |
|---|---|---|
| NQ_TICKS_PER_POINT | 4.0 | Ticks per NQ point (TickSize × 4 = 1 point) |
| NQ_DOLLARS_PER_TICK | 5.0 | Dollar value per tick move per contract |
| NQ_DOLLARS_PER_POINT | 20.0 | Dollar value per point move per contract |

---

## Stealth Order Naming (prop-firm safe)

All entry signal names use a single space (`" "`) so the NT8 Executions → Name column appears blank (NT8 would substitute "Buy"/"Sell short" for truly empty strings). Exits use `"Close"`.

### Entry Signals (Name column appears BLANK)

All entries use `signalName = " "` (single space). NT8 treats it as a valid non-empty name so it won't substitute default names, but it renders as blank in the UI. With `EntryHandling.AllEntries` and `EntriesPerDirection=4`, NT8 allows up to 4 entries per direction under the same signal name, which matches the DCA limit.

Example session (Name column in Executions tab):
```
Trade 1, Buy Mkt     →  Name = " " (appears blank)
Trade 1, Buy Mkt DCA →  Name = " " (appears blank)
Trade 1 closed
Trade 2, Sell Lmt    →  Name = " " (appears blank)
```

### Exit Signals (Name column always shows "Close")

ALL exit types use `"Close"` as the exit signal name:
- Hidden SL hit → `"Close"`
- Hidden TP hit → `"Close"`
- Adaptive trail hit → `"Close"`
- Close Trade button → `"Close"`
- Close 1 (partial) → `"Close"`
- Emergency Kill → `Account.Flatten` (no signal)

### Pending Limit Order Cancellation

Close Trade and Emergency Kill buttons cancel any unfilled limit orders (Working/Accepted/Submitted state) before closing positions. If flat with only a pending limit, Close Trade cancels it and resets state.

### Internal Tracking (developer only)

`Print()` statements log full context (HIDDEN SL, TRAIL HIT, DCA #, entry price, etc.) — visible ONLY in NT8's Output tab, never in Executions or data sent to broker/prop firm.

### Fields

- `tradeSequence` — round-trip trade counter (internal logging)
- `activeEntrySignals` — list of entry signals (all `" "`) for exit pairing

---

## Adaptive Trailing Stop

### What It Does

A hidden (no resting orders) trailing stop that dynamically adjusts its distance from price based on real-time market conditions. It aims to:
- Lock in gains quickly in choppy/ranging markets
- Give trades room to run in trending markets
- Fire a market order exit the instant price touches it

### How It Activates

The trail stays dormant until unrealized profit reaches a configurable threshold (default: 8 NQ points = $160/ct). This prevents premature trailing on normal noise after entry. Once activated, the trail **NEVER** deactivates until the trade closes — it only ratchets in favor of the trade.

### Regime Detection (Trend Score 0–100%)

Every tick, the strategy scores the market on a 0–100% scale:

**Factor 1 — ATR Expansion (25% weight)**
- Compares current ATR to its 10-bar average
- Rising ATR → expanding volatility → likely trending
- Score: `(currentATR / avgATR - 0.9) / 0.5`, clamped 0–1

**Factor 2 — EMA Spread (25% weight)**
- Measures fast/slow EMA gap relative to ATR
- Wide gap → strong directional move → trending
- Score: `|EMA_fast - EMA_slow| / ATR / 2.5`, clamped 0–1

**Factor 3 — Directional Bars (25% weight)**
- Counts last 8 bars moving in the trade direction
- Score: `directional_bars / total_bars`

**Factor 4 — RSI Extremity (10% weight)**
- Measures how far RSI is from neutral 50
- Score: `|RSI - 50| / 50 × 1.5`, clamped 0–1

**Factor 5 — Range Compression (15% weight)**
- Bar range vs ATR — small bars = choppy/indecisive
- Score: `(barRange/ATR - 0.3) / 0.9`, clamped 0–1

Composite: `0.25×ATR + 0.25×EMA + 0.25×Dir + 0.10×RSI + 0.15×Range`

### Trail Distance Calculation

1. **Regime distance** = `lerp(minPoints, maxPoints, trendScore)`
   - Score 0% (pure chop) → uses trailMinPoints (default 3)
   - Score 100% (strong trend) → uses trailMaxPoints (default 25)

2. **ATR-scaled distance** = `ATR × trailAtrMultiplier / ticksPerPoint`

3. **Final distance** = `(regimeDist × 0.6 + atrDist × 0.4) × chopMultiplier`, clamped to [min, max]

Example scenarios (1 contract, NQ):
- Choppy market (score 20%) → trail ~5 pts ($100 from price)
- Mixed market (score 50%) → trail ~12 pts ($240 from price)
- Strong trend (score 85%) → trail ~22 pts ($440 from price)

### Ratchet Behavior

- Long trades: trail moves UP only (`new = max(old, price - dist)`)
- Short trades: trail moves DOWN only (`new = min(old, price + dist)`)
- Trail is recalculated every tick, but NEVER moves against you

### Volatility Spike Guard (v1)

When current ATR exceeds 2× the 20-bar average ATR, the trail distance is widened by 50% to prevent premature stops during sudden volatility explosions (news events, etc.).

### Exit Mechanics

- When price touches or crosses the trail → market exit fires
- Exit signal name: `"Close"` (stealth)
- Sets `pendingExit=true`, same flow as hidden SL/TP exits

### Interaction with Fixed SL/TP

Both systems run simultaneously on every tick:
- Fixed SL/TP = hard safety net (always armed, never moves unless adjusted)
- Adaptive trail = profit-maximizing layer

Whichever is hit first triggers the exit. In practice:
- Losing trade → fixed SL fires (trail never activated)
- Small winner → fixed TP may fire before trail activated
- Big winner → trail activates, ratchets up, locks gain

### Dashboard Display

```
[TRAIL: ON]  Trail: 21345.50 (8.2pt Trend 72%)
[TRAIL: OFF] Trail: OFF
```

States:
- `"Trail: —"` = enabled, flat (no trade)
- `"Trail: waiting (X/8pt)"` = in trade, below activation threshold
- `"Trail: 21345 (Xpt Regime %)"` = active, showing distance+regime
- `"Trail: OFF"` = disabled via toggle or parameter

### Chart Visualization

When trail is active:
- Magenta dash-dot line at trail price
- Text label: `"Trail 21345.50 (8.2pt | Trending 72%)"`
- Lines auto-remove when trade closes or trail disabled

### Configurable Parameters (Group 7)

| Parameter | Type | Default | Description |
|---|---|---|---|
| TrailEnabled | bool | true | ON/OFF toggle |
| TrailActivationPoints | int | 8 | Profit threshold in NQ points |
| TrailMinPoints | int | 3 | Tightest distance (choppy) |
| TrailMaxPoints | int | 25 | Widest distance (trending) |
| TrailAtrMultiplier | double | 1.5 | ATR scale factor |

---

## Smart Signal Filters (Trap Detection)

Post-processing layer applied after EVERY signal calculation. Prevents entries into common NQ traps and low-quality setups.

### Filter 1: Volume Confirmation
- Compares current bar volume to 20-bar average
- <60% of avg → 35% penalty (thin breakouts reverse)
- <85% of avg → 15% penalty (below-average conviction)
- >150% of avg → 10% bonus (strong institutional flow)

### Filter 2: Bull/Bear Conflict (Whipsaw Rejection)
- When both bull and bear confidence are high (>65% ratio)
- Market is indecisive → both signals penalized
- Prevents chop-induced back-to-back losing entries

### Filter 3: Momentum Exhaustion
- Tracks price position within 20-bar range
- If price is >82% into the range AND moved >2× ATR
- Penalizes same-direction entries (chasing a spent move)

### Filter 4: Bar Quality (Wick Rejection)
- Upper wick >45% of bar → penalizes bullish signals
- Lower wick >45% of bar → penalizes bearish signals
- Detects rejection/trap candles at key levels

### Filter 5: Night Session Penalty (v1)
- Between 6:00 PM – 9:30 AM ET, all signals receive a 20% penalty
- Lower liquidity, wider spreads, more false breakouts during overnight

### Strategy-level enhancements
- **Momentum+VWAP**: bar conviction bonus on VWAP cross
- **Key Level Breakout**: fake-out filter (close must be near extreme)
- **Liq Sweep Reversal**: volume spike required for full credit
- **ORB**: conviction filter + ORB range quality scaling

---

## Profit-Tier Trailing Stop

Tracks the max profit reached during each trade and enforces progressively tighter floor prices.

| Tier | Threshold | Floor | Description |
|---|---|---|---|
| T1-BE | ≥ 2× activation (16pt) | Breakeven (+1 tick) | Never lose on a trade that reached +16pts |
| T2-Strong | ≥ 2.5× activation (20pt) | 45% of max profit | Lock significant gains |
| T3-Runner | ≥ 4× activation (32pt) | 55% of max profit | Let runners run but protect most gains |

Dashboard shows tier name with color coding:
- Active = Magenta, T1-BE = Yellow, T2-Strong = Cyan, T3-Runner = Gold

---

## Risk Management

- Hidden SL/TP (no resting orders on exchange)
- Adaptive trailing stop (regime-aware, hidden)
- Max daily loss $ — flattens and halts trading
- Daily profit target $ — flattens and halts trading
- Max trades per day (default 999, 0 = unlimited)
- CME maintenance window block (4:55–5:59 PM ET)
- Auto-flatten at configurable time (default 4:50 PM ET)
- Emergency Kill — immediate Account.Flatten + permanent halt until re-enable
- Daily limit checked against unrealized P&L (not just realized)
- Trading hours toggle — can disable hour-based restrictions for overnight trading

---

## Auto Strategies (0-4)

Each strategy scores confidence 0-100% with two tiers: crossover (full credit) and continuation (partial credit).

### 0: Momentum + VWAP
- Crossover: price crosses VWAP (+35%), EMA align (+30), RSI 50-75 (+20), momentum (+15)
- Continuation: price stays above/below VWAP (+20)
- Bar quality bonus on VWAP cross

### 1: Key Level Breakout
- Crossover: price breaks 20-bar high/low (+50), ATR confirmation (+30), EMA align (+20)
- Continuation: price holds above/below level (+25)
- Fake-out filter: conviction required for full credit

### 2: Liquidity Sweep Reversal
- Crossover: sweep + snap-back on same bar (+45), RSI confirmation (+30), ATR snapback (+25)
- Continuation: recent sweep within 5 bars (+25)
- Volume spike required for full credit

### 3: Opening Range Breakout (9:45 AM – 11:30 AM ET)
- Crossover: price breaks ORB high/low (+55), EMA align (+25), ATR > 0 (+20)
- Continuation: price holds above/below ORB (+30)
- ORB range quality scaling

### 4: Auto Select
- Evaluates all 4 strategies including smart filters, picks highest confidence

---

## Post-Entry Trap Detector (v1)

Monitors trades after entry for signs of a trap:
- **HIGH risk**: Fast adverse move (>3pts in ≤5 bars) with no favorable excursion
- **Medium risk**: Slow bleed (>2pts adverse in ≤10 bars), or was profitable but gave it all back
- **Low risk**: Normal trade progression

Dashboard displays: `Trap: [Level] (MFE:X MAE:Y bars:N)`

---

## Order Execution (v1)

### 6 Button Types

| Button | Action | Price |
|---|---|---|
| BUY MKT | `EnterLong()` | Market price |
| SELL MKT | `EnterShort()` | Market price |
| BUY ASK | `EnterLongLimit()` at ask | Aggressive limit — should fill immediately |
| SELL BID | `EnterShortLimit()` at bid | Aggressive limit — should fill immediately |
| BUY LMT | `EnterLongLimit()` at bid | Passive limit — queue at bid |
| SELL LMT | `EnterShortLimit()` at ask | Passive limit — queue at ask |

### Fill Confirmation

Hidden stops (SL/TP) are armed **only after fill confirmation** in `OnExecutionUpdate`, using the actual execution price for accurate SL/TP placement. Entry methods set direction and estimate position, but stops aren't live until the order fills.

### Reverse Entries

Pressing opposite direction while in position:
- If DCA'd (multiple contracts): closes 1 contract
- If single entry: closes position, then enters opposite direction (supports all 6 button types)

---

## Dashboard Status (when flat)

| Status | Meaning |
|---|---|
| `■ DAILY LOSS LIMIT` | Loss cap hit, halted |
| `■ DAILY PROFIT TARGET` | Profit cap hit, halted |
| `■ Max trades reached` | Trade limit hit, done |
| `■ EOD flatten` | Auto-flatten fired |
| `⚠ KILLED` | Emergency kill active — re-enable to trade |
| `○ Flat — outside hours` | Before/after trading window |
| `● Flat — scanning (X%)` | Auto mode, showing confidence |
| `● Flat — manual mode` | Waiting for button press |

---

## v1 Changelog

### Section 0: Enable/Disable Cycle Bug Fix
- Comprehensive `State.Realtime` reset of ALL transient state
- Dashboard retry mechanism (up to 5 retries at 50-tick intervals)
- Historical replay guards — order submissions restricted to `State.Realtime`
- P&L baseline via incremental tracking (no O(n) AllTrades scan)
- `OnPositionUpdate` and `OnExecutionUpdate` guarded with `State.Realtime`

### Section 1: Order Execution Overhaul
- Added BUY ASK / SELL BID aggressive limit buttons
- `CanEnterTrade()` centralized guard method (eliminates duplicated checks)
- `ArmHiddenStops()` moved to `OnExecutionUpdate` fill confirmation
- Average entry price updated from actual execution price
- Reverse entries support all 6 button types

### Section 2: Close/Flatten Overhaul
- Emergency Kill sets `emergencyKillActive = true` (permanent halt)
- All entry methods check `emergencyKillActive` flag first
- Separate `ExecuteEmergencyKill()` from `ExecuteFlatten()`

### Section 3: Stealth Naming
- Preserved — all entries still use `" "`, all exits use `"Close"`

### Section 4: Hidden SL/TP Hardening
- `MonitorHiddenStops` checks unrealized P&L against daily limit
- `MonitorAdaptiveTrail` includes volatility spike guard (2× ATR → widen trail 50%)

### Section 5: P&L Display
- Incremental tracking via `processedTradeCount` (O(1) amortized)
- 4-row display: Unrealized, Realized, Total, Account Cash

### Section 6: Trading Hours
- `TradingHoursEnabled` toggle property (default true)
- `flattenTime` default changed to 165000 (4:50 PM ET)
- Dashboard toggle button for quick on/off

### Section 7: Post-Entry Trap Detector
- `MonitorPostEntryTrap()` tracks MFE/MAE and bars in trade
- Risk assessment: Low / Medium / HIGH
- Dashboard info line with color coding
- Toggleable via `EnablePostEntryTrapDetector` property

### Section 8: Dashboard Improvements
- 3 button rows: MKT, ASK/BID, LMT
- Null guards on all UI element access
- Retry build mechanism for failed dashboard construction
- New labels: Total P&L, Account Cash, Trap Info

### Section 9: Code Quality
- NQ constants: `NQ_TICKS_PER_POINT`, `NQ_DOLLARS_PER_TICK`, `NQ_DOLLARS_PER_POINT`
- Magic numbers replaced throughout
- Night session penalty (Filter 5) in `ApplySmartFilters()`
- `CanEnterTrade()` eliminates ~80 lines of duplicated guard logic

---

## Configuration Properties

### Group 1 — Risk Management
| Property | Type | Range | Default | Description |
|---|---|---|---|---|
| SlPoints | int | 1-500 | 67 | Hidden SL distance (NQ points) |
| TpPoints | int | 1-500 | 50 | Hidden TP distance (NQ points) |
| MaxDailyLossDollars | int | 500-10000 | 2000 | Hard daily loss cap |
| MaxDailyProfitDollars | int | 500-20000 | 4000 | Daily profit target |
| MaxTradesPerDay | int | 0-999 | 999 | Max round-trips (0=unlimited) |
| Contracts | int | 1-10 | 1 | Contracts per entry |

### Group 2 — DCA Settings
| Property | Type | Range | Default | Description |
|---|---|---|---|---|
| DcaEnabled | bool | — | true | Allow adding to position |
| DcaMaxPositions | int | 1-4 | 2 | Max entries (first + adds) |
| DcaDistancePoints | int | 0-200 | 0 | Min distance for DCA (0=none) |

### Group 3 — Trading Mode
| Property | Type | Range | Default | Description |
|---|---|---|---|---|
| AutoMode | bool | — | false | Start in auto mode |
| AutoStrategy | int | 0-4 | 0 | Strategy selector |
| MinSignalConfidence | double | 50-100 | 68 | Entry threshold % |
| EntryDelaySeconds | int | 0-120 | 5 | Cooldown between entries |

### Group 4 — Indicators
| Property | Type | Range | Default |
|---|---|---|---|
| EmaPeriodFast | int | 3-50 | 9 |
| EmaPeriodSlow | int | 10-200 | 21 |
| RsiPeriod | int | 5-30 | 14 |
| AtrPeriod | int | 5-30 | 14 |

### Group 5 — Display
| Property | Type | Range | Default | Description |
|---|---|---|---|---|
| SlTpAdjustStep | int | 1-50 | 5 | Points per +/- click |
| JumpSlPercent | int | 10-95 | 50 | % of gap to jump SL |
| ShowEma | bool | — | true | EMA on chart |
| ShowRsi | bool | — | true | RSI panel |
| ShowAtr | bool | — | true | ATR panel |
| ShowVwap | bool | — | true | VWAP line |
| ShowKeyLevels | bool | — | true | Key support/resistance |
| ShowSweepSignals | bool | — | true | Sweep arrows |

### Group 6 — Trading Hours
| Property | Type | Range | Default | Description |
|---|---|---|---|---|
| TradingHoursEnabled | bool | — | true | Enable hour-based restrictions |
| TradingStartTime | int | 0-235959 | 93000 | Earliest entry time (ET) |
| TradingEndTime | int | 0-235959 | 160000 | Session end (ET) |
| FlattenTime | int | 0-235959 | 165000 | Auto-flatten time (ET) |

### Group 7 — Adaptive Trail
| Property | Type | Range | Default | Description |
|---|---|---|---|---|
| TrailEnabled | bool | — | true | Enable adaptive trail |
| TrailActivationPoints | int | 1-100 | 8 | Profit threshold (pts) |
| TrailMinPoints | int | 1-100 | 3 | Tightest distance (pts) |
| TrailMaxPoints | int | 2-200 | 25 | Widest distance (pts) |
| TrailAtrMultiplier | double | 0.5-5.0 | 1.5 | ATR scale factor |

### Group 8 — Post-Entry Trap Detector
| Property | Type | Range | Default | Description |
|---|---|---|---|---|
| EnablePostEntryTrapDetector | bool | — | true | Enable trap monitoring |
