# AdaptiveNQScalper - NinjaTrader 8 Strategy

## Overview

An adaptive NQ (Nasdaq 100 E-mini) futures strategy that dynamically adjusts to current market conditions. It detects market regimes, uses multi-indicator confluence for high-probability entries, and automatically manages stop losses and take profits based on real-time volatility.

**Instrument:** NQ (MNQ also supported)  
**Contracts:** 1  
**Timeframe:** Recommended 5-minute or 15-minute chart  
**Target Win Rate:** 60%+  

---

## How It Works

### 1. Market Regime Detection

The strategy classifies the market into 4 regimes every bar:

| Regime | Condition | Trading Approach |
|--------|-----------|------------------|
| **Strong Trend** | ADX ≥ 40 | Trend-following with wider targets |
| **Weak Trend** | ADX ≥ 25, normal volatility | Standard trend-following |
| **Ranging** | ADX < 25, normal volatility | Mean-reversion (BB bounces) |
| **Volatile** | High ATR ratio | Reduce signals, quick profit-taking |

### 2. Multi-Indicator Signal Scoring (8 factors)

Each bar, the strategy calculates a **Long Score** and **Short Score** (0-8+ scale):

| Factor | Max Points | Description |
|--------|-----------|-------------|
| EMA Alignment | 2.0 | Fast/Slow/Trend EMA stack + crossovers |
| RSI Conditions | 1.5 | Regime-aware: pullbacks in trends, O/S in ranges |
| MACD Confluence | 1.5 | Line vs signal + histogram momentum |
| Price Action | 1.0 | Body/wick ratios, pin bar detection |
| Bollinger Bands | 1.0 | Mean-reversion or breakout depending on regime |
| Volume | 0.5 | Above-average volume confirmation |
| Regime Boost/Penalty | ±15-25% | Multiplier based on current regime |
| Conflict Penalty | -50% | Reduces scores when signals are mixed |

A trade triggers when `score ≥ MinSignalScore × adaptiveMultiplier` (default threshold: 3.0).

### 3. Adaptive Stop Loss & Take Profit

All stops and targets are **ATR-based** and adjust per regime:

| Regime | Stop Adjustment | Target Adjustment |
|--------|----------------|-------------------|
| Strong Trend | ×0.85 (tighter) | ×1.30 (wider) |
| Weak Trend | ×1.00 | ×1.00 |
| Ranging | ×1.10 (wider) | ×0.90 |
| Volatile | ×1.30 (wider) | ×0.85 (tighter) |

**Defaults:** Stop = 2.0 × ATR, Target = 3.0 × ATR → **1.5:1 R:R ratio minimum**

### 4. Active Trade Management

Once in a trade, the strategy manages it through 4 layers:

1. **Breakeven Stop** — Moves stop to entry + 2 ticks when +20 ticks in profit
2. **Trailing Stop** — Activates at 1.5× ATR profit, trails by 8 ticks from high/low
3. **Time-Based Exit** — Exits profitable trades after 50 bars to avoid reversals
4. **Volatility Exit** — Takes quick profit (+8 ticks) if regime shifts to Volatile

### 5. Adaptive Performance Tracking

The strategy tracks its own recent performance (last 20 trades) and adjusts:

| Recent Win Rate | Signal Threshold Multiplier | Behavior |
|----------------|----------------------------|----------|
| ≥ 65% | 0.90 (lower) | More trades — it's working |
| 55-64% | 1.00 | Standard |
| 45-54% | 1.15 (higher) | Fewer, more selective trades |
| < 45% | 1.30 (higher) | Very selective, protective mode |
| 3+ consecutive losses | ≥ 1.35 | Maximum selectivity |

---

## Installation

### Method 1: Direct File Copy
1. Copy `AdaptiveNQScalper.cs` to:
   ```
   Documents\NinjaTrader 8\bin\Custom\Strategies\
   ```
2. Open NinjaTrader 8
3. Go to **New → NinjaScript Editor**
4. Press **F5** to compile

### Method 2: Import via NinjaScript Editor
1. Open NinjaTrader 8
2. **Tools → Import → NinjaScript Add-On**
3. Select the `.cs` file
4. Compile

---

## Configuration Guide

### Recommended Settings by Chart Timeframe

#### 5-Minute Chart (Active Scalping)
| Parameter | Value | Reason |
|-----------|-------|--------|
| Fast EMA | 9 | Quick trend detection |
| Slow EMA | 21 | Intermediate trend |
| ATR Stop Mult | 1.5 | Tighter for scalps |
| ATR Target Mult | 2.5 | Reasonable target |
| Max Daily Trades | 6 | More opportunities |
| Min Signal Score | 3.0 | Standard selectivity |

#### 15-Minute Chart (Swing Scalping)
| Parameter | Value | Reason |
|-----------|-------|--------|
| Fast EMA | 9 | Standard |
| Slow EMA | 21 | Standard |
| ATR Stop Mult | 2.0 | Wider for swings |
| ATR Target Mult | 3.5 | Larger targets |
| Max Daily Trades | 4 | Fewer, higher quality |
| Min Signal Score | 3.5 | More selective |

### Key Parameters to Optimize

Use NinjaTrader's Strategy Analyzer with these optimization ranges:

| Parameter | Range | Step |
|-----------|-------|------|
| ATR Stop Multiplier | 1.0 - 3.0 | 0.25 |
| ATR Target Multiplier | 2.0 - 5.0 | 0.25 |
| Min Signal Score | 2.5 - 4.5 | 0.25 |
| Fast EMA Period | 5 - 15 | 1 |
| Slow EMA Period | 15 - 30 | 1 |
| Breakeven Activation | 12 - 30 | 2 |

---

## Backtesting Instructions

1. Open **Strategy Analyzer** (New → Strategy Analyzer)
2. Select **AdaptiveNQScalper**
3. Set instrument to **NQ** (or MNQ for Micro)
4. Set timeframe to **5 min** or **15 min**
5. Set date range to at least **6 months** of data
6. Set **Commission** to match your broker (typically $4.04 RT for NQ)
7. Set **Slippage** to 1 tick
8. Click **Run**

### What to Look For
- **Win Rate** ≥ 60%
- **Profit Factor** ≥ 1.5
- **Max Drawdown** < 15% of net profit
- **Average Winner** > **Average Loser** (positive R:R)

---

## Risk Disclaimer

⚠️ **IMPORTANT: This strategy is for educational purposes.**

- **No strategy guarantees profits.** Past performance does not guarantee future results.
- **Always test on SIM first** — Run at least 2-4 weeks on simulation before going live.
- **Use proper position sizing** — 1 contract NQ = ~$20/tick. Ensure your account can handle the drawdowns.
- **Monitor daily** — Even adaptive strategies can fail in unprecedented market conditions.
- **Start with MNQ (Micro)** — 1/10th the risk of NQ while you validate performance.

---

## Architecture Diagram

```
Market Data (NQ 5m/15m bars)
         │
         ▼
┌─────────────────────┐
│  Session Filter      │──── Outside hours? → SKIP
│  (9:30-15:45 ET)     │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Regime Detection    │
│  ADX + ATR Ratio +   │──── StrongTrend | WeakTrend | Ranging | Volatile
│  Bollinger Width     │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Signal Scoring      │
│  EMA + RSI + MACD +  │──── LongScore / ShortScore (0-8+)
│  PA + BB + Volume    │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Adaptive Threshold  │
│  Score ≥ MinScore ×  │──── Based on recent win rate
│  adaptiveMultiplier  │
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Dynamic Stops/TPs   │
│  ATR × Regime Adj.   │──── Entry with 1 contract
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Trade Management    │
│  Breakeven → Trail → │──── Active position management
│  Time Exit → Vol Exit│
└─────────┬───────────┘
          │
          ▼
┌─────────────────────┐
│  Performance Track   │
│  Update win rate,     │──── Feeds back to adaptive threshold
│  streaks, multiplier │
└─────────────────────┘
```
