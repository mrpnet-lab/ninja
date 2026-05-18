# RM Pro 2.5 - Comprehensive Strategy Documentation

## Overview
**RM Pro 2.5** is an advanced, fully Unmanaged "Stealth" Risk Management Strategy for NinjaTrader 8. It is specifically designed to protect prop-firm accounts (e.g., TakeProfit Trader) by hiding physical stop-loss and take-profit orders from the broker feed until they are triggered, utilizing thread-safe polling, dynamic trailing, and strict daily risk limits.

---

## Core Architecture: The Polling Engine (v2.5 Update)
Unlike standard NinjaTrader strategies that rely on the buggy `OnAccountPositionUpdate` event handler, RM Pro 2.5 utilizes a **Main-Thread Polling Engine**.
* **How it works:** On every tick (`OnMarketData`), the strategy safely reads `Account.Positions` to detect manual Chart Trader entries.
* **Why it matters:** It is 100% thread-safe. It prevents the "Silent Death" bug where NinjaTrader aborts strategies that try to draw lines from background threads. It guarantees the strategy will never "go deaf" and ignore your manual trades.

---

## 1. Standard Parameters (Group 1)
The baseline risk management settings for standard market conditions.
* **Stealth Stop (Ticks):** The initial invisible stop loss distance. (Default: 20 ticks / 5 points)
* **Stealth Target (Ticks):** The initial invisible take profit distance. (Default: 60 ticks / 15 points)
* **BE Trigger (Ticks):** The amount of profit required before the Stop Loss automatically snaps to Break-Even (Entry +/- 1 tick). (Default: 15 ticks)
* **Trail Distance (Ticks):** Once at Break-Even, how closely the Stop Loss follows the "High Watermark" of the price. (Default: 20 ticks)

---

## 2. Volatility Mode (Group 2)
A toggleable mode designed to survive extreme market events (e.g., 9:30 AM NY Open, CPI data). It widens standard stops to avoid market maker liquidity grabs while deploying an emergency bailout.
* **Enable Volatility Mode:** Overrides standard parameters with Volatility parameters.
* **Vol: Stop / Target (Ticks):** Wide parameters to let trades breathe. (Default Stop: 80 ticks / Target: 160 ticks).
* **Vol: Base Trail Dist (Ticks):** The initial trailing distance before the Smart Choke activates. (Default: 40 ticks).
* **Bailout Time (Seconds) & Bailout Trigger (Ticks):** The "Panic Button". If the trade instantly reverses by the Trigger amount (e.g., 40 ticks) within the Bailout Time window (e.g., 15 seconds) from entry, the strategy instantly flattens the trade, bypassing the wide 80-tick stop to save capital.

---

## 3. Trading Filters (Group 3)
Prevents overtrading and counter-trend knife-catching.
* **Max Trades Per Day:** Hard limit on daily executions. If hit, the strategy instantly rejects and flattens any new manual entries. (Default: 10).
* **Enable EMA Trend Filter:** Activates a background trend monitor.
    * Automatically plots a **Cyan** line on the chart.
    * **Rule:** Longs are *only* allowed ABOVE the EMA. Shorts are *only* allowed BELOW the EMA. Violations are instantly rejected to save commissions.
* **EMA Period:** The lookback period for the filter. (Default: 200).

---

## 4. Account Protection (Group 4)
Prop-firm safety protocols to prevent account-blowing behavior.
* **Enable Time Bomb:** Prevents "hoping and praying" on stagnant, losing trades.
* **Time Bomb Limit (Seconds):** (Default: 180s / 3 minutes).
    * *Smart Reset Logic:* The bomb is only active when the trade is at break-even or in a loss. If the trade enters profit, the bomb defuses. If the trade falls back into a loss, the 3-minute timer completely *resets*, giving the pullback time to recover. 
* **Enable Daily Loss Limit (Tilt Switch):** A hard lock on daily drawdown.
* **Daily Loss Limit ($ USD):** (Default: $1,000). The strategy constantly calculates `Session Realized PnL + Current Open Floating PnL`. If this combined value drops to or below -$1,000, it instantly market-exits the open position and completely locks the user out of trading for the rest of the day.

---

## Advanced Core Logic

### A. The Global "Smart Choke"
A dynamic continuous trailing engine that actively protects deep profits. Works in both Standard and Volatility modes.
* **Tier 1 (The Squeeze):** When profit exceeds +20 points (+80 ticks), the trailing distance is mathematically forced down to a maximum of 5 points (20 ticks).
* **Tier 2 (The Hyper-Squeeze):** When profit exceeds +30 points (+120 ticks), the trailing distance is forced down to just 2.5 points (10 ticks) behind the peak. Any micro-reversal will instantly secure the massive win.

### B. Dynamic Text & Visuals
* The Stealth SL and TP lines project live, dynamically updating text 5 bars ahead of the current price action.
* Text format: `SL: +10 pts | +40 ticks | +$200.00`.
* If the Time Bomb is ticking, a live countdown is appended: `[ BOMB: 02:59 ]`.

### C. Drag-and-Drop Syncing
* Users can manually drag the Red (SL) or Green (TP) lines on the chart. 
* The strategy instantly recalculates its math, updates the dynamic text to show the locked-in $ amount, and sets a new "High Watermark" to continue auto-trailing from the user's manual placement.

### D. The Prop Firm Mask
* NinjaTrader normally tags strategy-executed orders with names like "Sell" or "Buy to cover" in the local Executions tab. 
* RM Pro 2.5 intercepts this and passes a blank string (`" "`), making the orders appear completely manual and hiding the strategy's footprint from local UI execution lists.
