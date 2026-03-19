# NQ Adaptive VWAP + Stop-Hunt Strategy

**Platform:** NinjaTrader 8 | **Language:** NinjaScript (C#)  
**Instruments:** NQ, MNQ | **Recommended Timeframe:** 5-minute chart

---

## Strategy Overview

This strategy enters **after a confirmed stop-hunt sweep** — where price briefly violates a recent swing extreme and reverses. It uses:

- **ATR-adaptive stop loss and take profit** (scales with volatility)
- **VWAP directional bias** (session-anchored)
- **EMA trend filter** (9/21 crossover)
- **Session gating** (trades only 9:30 AM – 3:45 PM ET)
- **Partial exits** (60% at TP1, runner to TP2 with trailing stop)
- **Daily trade limit** (default 3 trades/day)

### Entry Types

1. **Stop-Hunt Reversal** — Primary entry. Detects when price sweeps a swing high/low and reverses, confirmed by trend alignment and momentum.
2. **VWAP Reclaim** — Secondary entry. Triggers when price crosses back above/below VWAP with EMA trend confirmation and consecutive closes in the direction.

---

## Recommended Timeframe

| Timeframe | Suitability | Notes |
|-----------|-------------|-------|
| **5-min** | **Best** | Ideal balance — stop-hunt wicks visible, ATR(14) covers ~70 min, EMAs filter noise well |
| 3-min | Good | More signals but noisier; consider tightening SL ATR mult to 1.0 |
| 15-min | Acceptable | Fewer signals (1–2/day); widen swing lookback to 7–8 bars |
| 1-min | Avoid | Too noisy — excessive false stop-hunt signals |

---

## NinjaTrader 8 Setup — Step by Step

### Step 1: Install the Strategy File

1. Copy `NQ_AdaptiveVWAP_Strategy.cs` to:
   ```
   Documents\NinjaTrader 8\bin\Custom\Strategies\
   ```
2. Open NinjaTrader 8 → **New** → **NinjaScript Editor**
3. Press **F5** to compile
4. Verify no errors in the output pane

### Step 2: Open Your Chart

1. **New** → **Chart**
2. **Instrument:** `NQ 06-26` (or current front-month) — or `MNQ 06-26` for Micro
3. **Data Series:**
   - Type: **Minute**
   - Value: **5**
   - Trading Hours: **CME US Index Futures RTH** (recommended — strategy already gates 9:30–3:45 ET)
4. **Days to load:** 5–10 days for backtesting, 1 day for live
5. Click **OK**

### Step 3: Apply the Strategy

1. Right-click chart → **Strategies...**
2. Click **Add**
3. Select **NQ_AdaptiveVWAP** from the dropdown

### Step 4: Configure Parameters

#### Trend Filter

| Parameter | Default | Notes |
|-----------|---------|-------|
| Fast EMA period | **9** | Responsive to short-term momentum |
| Slow EMA period | **21** | Trend direction filter |

#### Volatility

| Parameter | Default | Notes |
|-----------|---------|-------|
| ATR period | **14** | Standard; captures recent volatility |
| SL ATR multiplier | **1.2** | Stop loss = 1.2 × ATR (~15–25 NQ pts on 5-min) |
| TP1 ATR multiplier | **1.8** | First target (60% exit) ~22–36 pts |
| TP2 ATR multiplier | **3.0** | Runner target (40% exit) ~37–60 pts |

#### Stop Hunt Detection

| Parameter | Default | Notes |
|-----------|---------|-------|
| Swing lookback (bars) | **5** | Looks back 25 min on 5-min chart |
| Hunt confirm bars | **2** | Waits 10 min after sweep to confirm reversal |

#### Risk Management

| Parameter | Default | Notes |
|-----------|---------|-------|
| Max trades per day | **3** | Prevents overtrading; increase to 4–5 only after backtesting |
| Flatten at end of day | **True** | Closes all positions at 3:55 PM ET |

### Step 5: Set Order Properties

In the Strategies dialog:

- **Account:** Select your account (**Sim first!**)
- **Default quantity:**
  - NQ: **1 contract** minimum ($20/pt, ~$300–500 risk per trade)
  - MNQ: **2–4 contracts** ($2/pt, allows cleaner partial exits)
- **Order fill resolution:** **High** (uses 1-min sub-bars for more accurate fills)

### Step 6: Backtest First

1. Set strategy to **Enabled** in **Historical** mode
2. Click **OK** — strategy processes historical bars
3. **New** → **Strategy Analyzer** for detailed results
4. **What to look for:** Profit factor > 1.5, win rate > 45%, max drawdown < 2% of account

### Step 7: Run on Sim Account

1. Right-click chart → **Strategies...**
2. Ensure **Account** = **Sim101**
3. Check **Enabled**
4. Monitor live for **2–3 weeks minimum** before going live

### Step 8: Go Live

1. Switch account from Sim to live
2. Start with **1 NQ contract** or **2 MNQ contracts**
3. Monitor the first few sessions manually

---

## Trading Tips

- **Best trading windows:** 9:30–11:30 AM ET and 2:00–3:30 PM ET — stop-hunts are most frequent around VWAP during these periods
- **Avoid:** FOMC days, CPI/NFP releases — disable the strategy or reduce max trades to 1
- **MNQ vs NQ:** Use MNQ if account < $15K — the 60/40 partial exit works better with 4+ MNQ contracts than 1 NQ contract
- **Session management:** The strategy gates 9:30 AM – 3:45 PM ET and flattens at 3:55 PM automatically

---

## Position Management Logic

1. **Entry** → Sets SL at 1.2× ATR, profit target at 3.0× ATR
2. **TP1 hit** (1.8× ATR) → Exits 60% of position, moves SL to breakeven + 5 ticks
3. **After TP1** → ATR trailing stop (1.0× ATR from current price) protects remaining 40%
4. **TP2 hit** (3.0× ATR) → Remaining position closed by profit target
5. **EOD** → All positions flattened at 3:55 PM ET regardless of P&L

---

## Files

| File | Description |
|------|-------------|
| `NQ_AdaptiveVWAP_Strategy.cs` | Strategy source code |
| `NQ_AdaptiveVWAP_README.md` | This documentation |
