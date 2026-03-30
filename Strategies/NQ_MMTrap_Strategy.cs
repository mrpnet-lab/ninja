// ============================================================
//  NQ MM-Trap Strategy for NinjaTrader 8
//  Version : 1.1  (fixed — CS0246 / CS0103 resolved)
//  Author  : Custom Build — NQ Semi-Auto / Auto Trader
//
//  FIXES IN v1.1:
//  - EMA/RSI/ATR declared using full NT8 indicator namespace types
//  - VWAP calculated manually (not a built-in NT8 strategy call)
//  - using NinjaTrader.NinjaScript.Indicators added
//
//  INSTALLATION:
//  1. NinjaTrader 8 → Tools → NinjaScript Editor
//  2. File → New → Strategy → name it NQ_MMTrap_Strategy
//  3. Select all default code → paste this entire file
//  4. Press F5 to compile
//  5. Right-click NQ chart → Strategies → Add → NQ_MMTrap_Strategy
//  6. Always run on SIM first before going live
// ============================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.NinjaScript;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class NQ_MMTrap_Strategy : Strategy
    {
        // ─── User parameters ──────────────────────────────────────
        private int    slPoints;
        private int    tpPoints;
        private int    maxDailyLossDollars;
        private int    maxDailyProfitDollars;
        private int    contracts;
        private bool   dcaEnabled;
        private int    dcaMaxPositions;
        private int    dcaDistancePoints;
        private int    entryDelaySeconds;
        private bool   autoMode;
        private int    autoStrategy;
        private int    emaPeriodFast;
        private int    emaPeriodSlow;
        private int    rsiPeriod;
        private int    atrPeriod;
        private double minSignalConfidence;

        // ─── Indicator references — CORRECT NT8 types ─────────────
        // Must use NinjaTrader.NinjaScript.Indicators.EMA etc.
        // (not bare 'EMA' which the compiler cannot resolve)
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaFast;
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaSlow;
        private NinjaTrader.NinjaScript.Indicators.RSI indRsi;
        private NinjaTrader.NinjaScript.Indicators.ATR indAtr;

        // ─── Session / daily tracking ─────────────────────────────
        private double   dailyRealizedPnL;
        private bool     dailyLimitHit;
        private bool     dailyProfitHit;
        private DateTime sessionDate;

        // ─── Hidden SL/TP state (no live resting orders) ─────────
        private double hiddenStopPrice;
        private double hiddenTargetPrice;
        private bool   stopsArmed;
        private int    openTradeDirection; // 1=long, -1=short, 0=flat

        // ─── DCA tracking ─────────────────────────────────────────
        private int    openDcaCount;
        private double averageEntryPrice;
        private double totalContracts;

        // ─── Manual VWAP (NT8 has no built-in VWAP() for strategies)
        private double   vwapCumTPV;
        private double   vwapCumVol;
        private double   vwapValue;

        // ─── Thread-safe button flags & cooldown ──────────────────
        private bool     pendingLong;
        private bool     pendingShort;
        private bool     pendingFlatten;
        private DateTime lastEntryTime;
        private bool     firstBarSeen;

        // ─── ORB state ────────────────────────────────────────────
        private double orbHigh;
        private double orbLow;
        private bool   orbSet;

        // ─── On-chart UI elements ─────────────────────────────────
        private Button    btnLong;
        private Button    btnShort;
        private Button    btnExit;
        private Grid      buttonPanel;
        private TextBlock lblStatus;
        private TextBlock lblPnL;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "NQ MM-Trap v1.1 — Hidden SL/TP Semi-Auto & Auto Strategy";
                Name         = "NQ_MMTrap_Strategy";
                Calculate    = Calculate.OnEachTick;
                EntriesPerDirection          = 2;
                EntryHandling                = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 30;
                IsUnmanaged                  = false;
                BarsRequiredToTrade          = 25;

                slPoints              = 67;
                tpPoints              = 50;
                maxDailyLossDollars   = 2000;
                maxDailyProfitDollars = 4000;
                contracts             = 1;
                dcaEnabled            = true;
                dcaMaxPositions       = 2;
                dcaDistancePoints     = 70;
                entryDelaySeconds     = 45;
                autoMode              = false;
                autoStrategy          = 0;
                emaPeriodFast         = 9;
                emaPeriodSlow         = 21;
                rsiPeriod             = 14;
                atrPeriod             = 14;
                minSignalConfidence   = 68.0;
            }
            else if (State == State.Configure)
            {
                // Correct NT8 pattern: call the method, store the typed result
                indEmaFast = EMA(emaPeriodFast);
                indEmaSlow = EMA(emaPeriodSlow);
                indRsi     = RSI(rsiPeriod, 3);
                indAtr     = ATR(atrPeriod);

                AddChartIndicator(indEmaFast);
                AddChartIndicator(indEmaSlow);
            }
            else if (State == State.DataLoaded)
            {
                ResetVwap();
                dailyRealizedPnL = 0;
                dailyLimitHit    = false;
                dailyProfitHit   = false;
            }
            else if (State == State.Terminated)
            {
                RemoveButtonPanel();
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            if (!firstBarSeen)
            {
                firstBarSeen = true;
                sessionDate  = Time[0].Date;
            }

            if (Time[0].Date != sessionDate)
            {
                ResetDailyTracking();
                ResetVwap();
            }

            UpdateVwap();

            if (State == State.Realtime && btnLong == null && !autoMode)
                BuildButtonPanel();

            // Process pending button signals on NinjaScript thread
            if (State == State.Realtime)
            {
                if (pendingLong)    { pendingLong    = false; ExecuteLongEntry(); }
                if (pendingShort)   { pendingShort   = false; ExecuteShortEntry(); }
                if (pendingFlatten) { pendingFlatten = false; ExecuteFlatten(); }
            }

            if (dailyLimitHit)
            {
                UpdateStatusLabel("DAILY LOSS LIMIT — NO TRADES", Brushes.OrangeRed);
                return;
            }

            // Core MM-trap: monitor price internally, fire market order when hit
            if (stopsArmed && Position.MarketPosition != MarketPosition.Flat)
                MonitorHiddenStops();

            if (autoMode && !dailyLimitHit && Position.MarketPosition == MarketPosition.Flat)
                RunAutoStrategy();

            if (autoStrategy == 3)
                UpdateOrbLevels();

            UpdatePnLDisplay();
            DrawChartAnnotations();
        }

        // ─── Hidden SL/TP monitor ─────────────────────────────────
        private void MonitorHiddenStops()
        {
            double price = Close[0];

            if (openTradeDirection == 1)
            {
                if (price <= hiddenStopPrice)
                {
                    Print(Time[0] + " | HIDDEN SL LONG hit @ " + price.ToString("F2") + " — market exit sent");
                    for (int i = 1; i <= openDcaCount; i++)
                        ExitLong("SL_Exit_" + i, "LongEntry_" + i);
                    ResetPositionState();
                }
                else if (price >= hiddenTargetPrice)
                {
                    Print(Time[0] + " | HIDDEN TP LONG hit @ " + price.ToString("F2") + " — market exit sent");
                    for (int i = 1; i <= openDcaCount; i++)
                        ExitLong("TP_Exit_" + i, "LongEntry_" + i);
                    ResetPositionState();
                }
            }
            else if (openTradeDirection == -1)
            {
                if (price >= hiddenStopPrice)
                {
                    Print(Time[0] + " | HIDDEN SL SHORT hit @ " + price.ToString("F2") + " — market exit sent");
                    for (int i = 1; i <= openDcaCount; i++)
                        ExitShort("SL_Exit_" + i, "ShortEntry_" + i);
                    ResetPositionState();
                }
                else if (price <= hiddenTargetPrice)
                {
                    Print(Time[0] + " | HIDDEN TP SHORT hit @ " + price.ToString("F2") + " — market exit sent");
                    for (int i = 1; i <= openDcaCount; i++)
                        ExitShort("TP_Exit_" + i, "ShortEntry_" + i);
                    ResetPositionState();
                }
            }
        }

        // ─── Long entry ───────────────────────────────────────────
        private void ExecuteLongEntry()
        {
            if (dailyLimitHit)                                                        { Print("Blocked: daily limit hit");                 return; }
            if (Position.MarketPosition == MarketPosition.Short)                      { Print("Blocked: close short first");               return; }
            if (!dcaEnabled && Position.MarketPosition != MarketPosition.Flat)        { Print("Blocked: DCA disabled");                    return; }
            if (Position.MarketPosition == MarketPosition.Long && openDcaCount >= dcaMaxPositions) { Print("Blocked: max DCA reached"); return; }
            if (entryDelaySeconds > 0 && (Time[0] - lastEntryTime).TotalSeconds < entryDelaySeconds)
            { Print("Blocked: entry cooldown (" + entryDelaySeconds + "s)"); return; }

            if (Position.MarketPosition == MarketPosition.Long)
            {
                double distPts = Math.Abs(Close[0] - averageEntryPrice) / (TickSize * 4.0);
                if (distPts < dcaDistancePoints)
                {
                    Print("Blocked: DCA too close (" + distPts.ToString("F1") + " pts, need " + dcaDistancePoints + " pts)");
                    return;
                }
            }

            openDcaCount++;
            EnterLong(contracts, "LongEntry_" + openDcaCount);
            openTradeDirection = 1;
            lastEntryTime = Time[0];

            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0
                ? (averageEntryPrice * totalContracts + Close[0] * contracts) / newQty
                : Close[0];
            totalContracts = newQty;

            ArmHiddenStops();
            Print(Time[0] + " | LONG #" + openDcaCount + " @ " + Close[0].ToString("F2")
                + " | AvgEntry: " + averageEntryPrice.ToString("F2")
                + " | HiddenSL: " + hiddenStopPrice.ToString("F2")
                + " | HiddenTP: " + hiddenTargetPrice.ToString("F2"));
        }

        // ─── Short entry ──────────────────────────────────────────
        private void ExecuteShortEntry()
        {
            if (dailyLimitHit)                                                         { Print("Blocked: daily limit hit");                 return; }
            if (Position.MarketPosition == MarketPosition.Long)                        { Print("Blocked: close long first");                return; }
            if (!dcaEnabled && Position.MarketPosition != MarketPosition.Flat)         { Print("Blocked: DCA disabled");                    return; }
            if (Position.MarketPosition == MarketPosition.Short && openDcaCount >= dcaMaxPositions) { Print("Blocked: max DCA reached"); return; }
            if (entryDelaySeconds > 0 && (Time[0] - lastEntryTime).TotalSeconds < entryDelaySeconds)
            { Print("Blocked: entry cooldown (" + entryDelaySeconds + "s)"); return; }

            if (Position.MarketPosition == MarketPosition.Short)
            {
                double distPts = Math.Abs(Close[0] - averageEntryPrice) / (TickSize * 4.0);
                if (distPts < dcaDistancePoints)
                {
                    Print("Blocked: DCA too close (" + distPts.ToString("F1") + " pts, need " + dcaDistancePoints + " pts)");
                    return;
                }
            }

            openDcaCount++;
            EnterShort(contracts, "ShortEntry_" + openDcaCount);
            openTradeDirection = -1;
            lastEntryTime = Time[0];

            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0
                ? (averageEntryPrice * totalContracts + Close[0] * contracts) / newQty
                : Close[0];
            totalContracts = newQty;

            ArmHiddenStops();
            Print(Time[0] + " | SHORT #" + openDcaCount + " @ " + Close[0].ToString("F2")
                + " | AvgEntry: " + averageEntryPrice.ToString("F2")
                + " | HiddenSL: " + hiddenStopPrice.ToString("F2")
                + " | HiddenTP: " + hiddenTargetPrice.ToString("F2"));
        }

        // ─── Arm hidden levels from average entry ─────────────────
        private void ArmHiddenStops()
        {
            // NQ: 1 point = 4 ticks, TickSize = 0.25
            double slOff = slPoints * 4.0 * TickSize;
            double tpOff = tpPoints * 4.0 * TickSize;

            if (openTradeDirection == 1)
            {
                hiddenStopPrice   = averageEntryPrice - slOff;
                hiddenTargetPrice = averageEntryPrice + tpOff;
            }
            else
            {
                hiddenStopPrice   = averageEntryPrice + slOff;
                hiddenTargetPrice = averageEntryPrice - tpOff;
            }
            stopsArmed = true;
        }

        // ─── Manual flatten ───────────────────────────────────────
        private void ExecuteFlatten()
        {
            if (Position.MarketPosition == MarketPosition.Long)
                for (int i = 1; i <= openDcaCount; i++)
                    ExitLong("Flatten_" + i, "LongEntry_" + i);
            else if (Position.MarketPosition == MarketPosition.Short)
                for (int i = 1; i <= openDcaCount; i++)
                    ExitShort("Flatten_" + i, "ShortEntry_" + i);
            ResetPositionState();
            Print(Time[0] + " | Manual flatten executed");
        }

        private void ResetPositionState()
        {
            stopsArmed         = false;
            openTradeDirection = 0;
            openDcaCount       = 0;
            totalContracts     = 0;
            averageEntryPrice  = 0;
        }

        // ─── Daily PnL tracking ───────────────────────────────────
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution.Order == null) return;

            dailyRealizedPnL = 0;
            foreach (Trade t in SystemPerformance.AllTrades)
                if (t.Entry.Time.Date == sessionDate)
                    dailyRealizedPnL += t.ProfitCurrency;

            if (dailyRealizedPnL <= -maxDailyLossDollars && !dailyLimitHit)
            {
                dailyLimitHit = true;
                Print("*** DAILY LOSS LIMIT HIT: " + dailyRealizedPnL.ToString("C0") + " — trading halted ***");
                ExecuteFlatten();
            }
            if (dailyRealizedPnL >= maxDailyProfitDollars && !dailyProfitHit)
            {
                dailyProfitHit = true;
                Print("*** DAILY PROFIT TARGET: " + dailyRealizedPnL.ToString("C0") + " — consider locking gains ***");
            }
        }

        // ─── Manual VWAP calculation ──────────────────────────────
        private void ResetVwap()
        {
            vwapCumTPV = 0;
            vwapCumVol = 0;
            vwapValue  = 0;
        }

        private void UpdateVwap()
        {
            double tp  = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Volume[0];
            if (vol <= 0) return;
            vwapCumTPV += tp * vol;
            vwapCumVol += vol;
            vwapValue   = vwapCumVol > 0 ? vwapCumTPV / vwapCumVol : Close[0];
        }

        // ─── Auto strategy dispatcher ─────────────────────────────
        private void RunAutoStrategy()
        {
            switch (autoStrategy)
            {
                case 0: StrategyMomentumVwap();           break;
                case 1: StrategyKeyLevelBreakout();       break;
                case 2: StrategyLiquiditySweepReversal(); break;
                case 3: StrategyOpeningRangeBreakout();   break;
            }
        }

        // ─── Strategy 0: Momentum + VWAP ─────────────────────────
        private void StrategyMomentumVwap()
        {
            if (CurrentBar < emaPeriodSlow + 2) return;
            if (ToTime(Time[0]) >= 93000 && ToTime(Time[0]) <= 93500) return; // skip 9:30-9:35

            double emaF  = indEmaFast[0];
            double emaS  = indEmaSlow[0];
            double rsi   = indRsi[0];
            double price = Close[0];
            double prev  = Close[1];

            bool longEma  = emaF > emaS;
            bool longVwap = price > vwapValue && prev <= vwapValue;
            bool longRsi  = rsi > 50 && rsi < 75;
            bool longMom  = Close[0] > High[1];

            double longConf = 0;
            if (longEma)  longConf += 30;
            if (longVwap) longConf += 35;
            if (longRsi)  longConf += 20;
            if (longMom)  longConf += 15;

            bool shortEma  = emaF < emaS;
            bool shortVwap = price < vwapValue && prev >= vwapValue;
            bool shortRsi  = rsi < 50 && rsi > 25;
            bool shortMom  = Close[0] < Low[1];

            double shortConf = 0;
            if (shortEma)  shortConf += 30;
            if (shortVwap) shortConf += 35;
            if (shortRsi)  shortConf += 20;
            if (shortMom)  shortConf += 15;

            if (longConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO Momentum LONG conf=" + longConf + "% VWAP=" + vwapValue.ToString("F2"));
                ExecuteLongEntry();
            }
            else if (shortConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO Momentum SHORT conf=" + shortConf + "% VWAP=" + vwapValue.ToString("F2"));
                ExecuteShortEntry();
            }
        }

        // ─── Strategy 1: Key Level Breakout ──────────────────────
        private void StrategyKeyLevelBreakout()
        {
            if (CurrentBar < 22) return;
            if (ToTime(Time[0]) >= 93000 && ToTime(Time[0]) <= 93500) return;
            if (ToTime(Time[0]) >= 120000 && ToTime(Time[0]) <= 130000) return; // skip lunch

            double priorHigh = MAX(High, 20)[1];
            double priorLow  = MIN(Low,  20)[1];
            double atr       = indAtr[0];
            double price     = Close[0];
            double prevClose = Close[1];

            bool longBreak    = price > priorHigh && prevClose <= priorHigh;
            bool longAtrConf  = atr > 0 && (price - priorHigh) > atr * 0.15;
            bool longEmaAlign = indEmaFast[0] > indEmaSlow[0];

            bool shortBreak    = price < priorLow && prevClose >= priorLow;
            bool shortAtrConf  = atr > 0 && (priorLow - price) > atr * 0.15;
            bool shortEmaAlign = indEmaFast[0] < indEmaSlow[0];

            double longConf  = 0;
            if (longBreak)    longConf += 50;
            if (longAtrConf)  longConf += 30;
            if (longEmaAlign) longConf += 20;

            double shortConf = 0;
            if (shortBreak)    shortConf += 50;
            if (shortAtrConf)  shortConf += 30;
            if (shortEmaAlign) shortConf += 20;

            if (longConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO Breakout LONG conf=" + longConf + "% level=" + priorHigh.ToString("F2"));
                ExecuteLongEntry();
            }
            else if (shortConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO Breakout SHORT conf=" + shortConf + "% level=" + priorLow.ToString("F2"));
                ExecuteShortEntry();
            }
        }

        // ─── Strategy 2: Liquidity Sweep Reversal ────────────────
        private void StrategyLiquiditySweepReversal()
        {
            if (CurrentBar < 12) return;

            double swingHigh = MAX(High, 10)[1];
            double swingLow  = MIN(Low,  10)[1];
            double atr       = indAtr[0];
            double price     = Close[0];
            double prevLow   = Low[1];
            double prevHigh  = High[1];

            bool longSweep    = prevLow < swingLow && price > swingLow;
            bool longRsiConf  = indRsi[0] < 40;
            bool longSnapback = atr > 0 && (price - prevLow) > atr * 0.3;

            bool shortSweep    = prevHigh > swingHigh && price < swingHigh;
            bool shortRsiConf  = indRsi[0] > 60;
            bool shortSnapback = atr > 0 && (prevHigh - price) > atr * 0.3;

            double longConf  = 0;
            if (longSweep)    longConf += 45;
            if (longRsiConf)  longConf += 30;
            if (longSnapback) longConf += 25;

            double shortConf = 0;
            if (shortSweep)    shortConf += 45;
            if (shortRsiConf)  shortConf += 30;
            if (shortSnapback) shortConf += 25;

            if (longConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO Sweep Reversal LONG conf=" + longConf + "% sweepLow=" + swingLow.ToString("F2"));
                ExecuteLongEntry();
            }
            else if (shortConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO Sweep Reversal SHORT conf=" + shortConf + "% sweepHigh=" + swingHigh.ToString("F2"));
                ExecuteShortEntry();
            }
        }

        // ─── Strategy 3: Opening Range Breakout ──────────────────
        private void StrategyOpeningRangeBreakout()
        {
            if (!orbSet) return;
            if (ToTime(Time[0]) < 94500)  return;
            if (ToTime(Time[0]) > 113000) return;

            double price    = Close[0];
            double prevClose = Close[1];
            bool   emaAlign = indEmaFast[0] > indEmaSlow[0];

            bool longBreak  = price > orbHigh && prevClose <= orbHigh;
            bool shortBreak = price < orbLow  && prevClose >= orbLow;

            double longConf = 0;
            if (longBreak)      longConf += 55;
            if (emaAlign)       longConf += 25;
            if (indAtr[0] > 0)  longConf += 20;

            double shortConf = 0;
            if (shortBreak)     shortConf += 55;
            if (!emaAlign)      shortConf += 25;
            if (indAtr[0] > 0)  shortConf += 20;

            if (longConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO ORB LONG conf=" + longConf + "% orbHigh=" + orbHigh.ToString("F2"));
                ExecuteLongEntry();
            }
            else if (shortConf >= minSignalConfidence)
            {
                Print(Time[0] + " | AUTO ORB SHORT conf=" + shortConf + "% orbLow=" + orbLow.ToString("F2"));
                ExecuteShortEntry();
            }
        }

        private void UpdateOrbLevels()
        {
            int t = ToTime(Time[0]);
            if (t >= 93000 && t < 94500)
            {
                if (orbHigh == 0 && orbLow == 0)
                {
                    orbHigh = High[0];
                    orbLow  = Low[0];
                }
                else
                {
                    orbHigh = Math.Max(orbHigh, High[0]);
                    orbLow  = Math.Min(orbLow,  Low[0]);
                }
                orbSet = false;
            }
            else if (t >= 94500 && !orbSet && orbHigh > 0)
            {
                orbSet = true;
                Print(Time[0] + " | ORB locked High=" + orbHigh.ToString("F2") + " Low=" + orbLow.ToString("F2"));
                Draw.HorizontalLine(this, "orbHigh", false, orbHigh, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
                Draw.HorizontalLine(this, "orbLow",  false, orbLow,  Brushes.OrangeRed, DashStyleHelper.Dash, 2);
            }
        }

        private void DrawChartAnnotations()
        {
            if (!stopsArmed || averageEntryPrice == 0) return;
            Draw.HorizontalLine(this, "hiddenSL",    false, hiddenStopPrice,   Brushes.OrangeRed,  DashStyleHelper.DashDotDot, 2);
            Draw.HorizontalLine(this, "hiddenTP",    false, hiddenTargetPrice,  Brushes.LimeGreen,  DashStyleHelper.DashDotDot, 2);
            Draw.HorizontalLine(this, "avgEntryLine",false, averageEntryPrice,  Brushes.DodgerBlue, DashStyleHelper.Dot,        1);
        }

        private void ResetDailyTracking()
        {
            sessionDate           = Time[0].Date;
            dailyRealizedPnL      = 0;
            dailyLimitHit         = false;
            dailyProfitHit        = false;
            orbSet                = false;
            orbHigh               = 0;
            orbLow                = 0;
            Print("Session reset: " + sessionDate.ToShortDateString()
                + " MaxLoss=$" + maxDailyLossDollars + " ProfitTarget=$" + maxDailyProfitDollars);
        }

        // ─── On-chart button panel ────────────────────────────────
        private void BuildButtonPanel()
        {
            if (ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                buttonPanel = new Grid
                {
                    Width  = 260,
                    Height = 175,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Top,
                    Margin     = new Thickness(0, 60, 12, 0),
                    Background = new SolidColorBrush(Color.FromArgb(210, 18, 20, 28))
                };

                var stack = new StackPanel { Margin = new Thickness(8) };

                var title = new TextBlock
                {
                    Text      = "NQ MM-TRAP  v1.1",
                    Foreground = Brushes.White,
                    FontSize   = 13,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin    = new Thickness(0, 4, 0, 2)
                };

                lblStatus = new TextBlock
                {
                    Text       = "● Flat — ready",
                    Foreground = Brushes.CornflowerBlue,
                    FontSize   = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin     = new Thickness(0, 0, 0, 2)
                };

                lblPnL = new TextBlock
                {
                    Text       = "Daily P&L:  $0",
                    Foreground = Brushes.White,
                    FontSize   = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin     = new Thickness(0, 0, 0, 8)
                };

                btnLong = new Button
                {
                    Content    = "▲  LONG",
                    Width = 112, Height = 40,
                    Background = new SolidColorBrush(Color.FromRgb(22, 140, 100)),
                    Foreground = Brushes.White,
                    FontSize   = 14, FontWeight = FontWeights.Bold,
                    Margin     = new Thickness(0, 0, 4, 0),
                    BorderThickness = new Thickness(0)
                };
                btnLong.Click += (s, e) => pendingLong = true;

                btnShort = new Button
                {
                    Content    = "▼  SHORT",
                    Width = 112, Height = 40,
                    Background = new SolidColorBrush(Color.FromRgb(190, 45, 45)),
                    Foreground = Brushes.White,
                    FontSize   = 14, FontWeight = FontWeights.Bold,
                    Margin     = new Thickness(4, 0, 0, 0),
                    BorderThickness = new Thickness(0)
                };
                btnShort.Click += (s, e) => pendingShort = true;

                btnExit = new Button
                {
                    Content    = "⏹  FLATTEN ALL (market)",
                    Height     = 32,
                    Background = new SolidColorBrush(Color.FromRgb(160, 100, 0)),
                    Foreground = Brushes.White,
                    FontSize   = 12,
                    Margin     = new Thickness(0, 8, 0, 0),
                    BorderThickness = new Thickness(0)
                };
                btnExit.Click += (s, e) => pendingFlatten = true;

                var btnRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                btnRow.Children.Add(btnLong);
                btnRow.Children.Add(btnShort);

                stack.Children.Add(title);
                stack.Children.Add(lblStatus);
                stack.Children.Add(lblPnL);
                stack.Children.Add(btnRow);
                stack.Children.Add(btnExit);
                buttonPanel.Children.Add(stack);

                if (ChartControl.Parent is Grid chartGrid)
                    chartGrid.Children.Add(buttonPanel);
            });
        }

        private void UpdateStatusLabel(string text, Brush color)
        {
            if (lblStatus == null || ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                lblStatus.Text       = text;
                lblStatus.Foreground = color;
            });
        }

        private void UpdatePnLDisplay()
        {
            if (lblPnL == null || ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                lblPnL.Text       = "Daily P&L:  " + dailyRealizedPnL.ToString("C0");
                lblPnL.Foreground = dailyRealizedPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;

                if (stopsArmed && openTradeDirection != 0)
                {
                    string dir = openTradeDirection == 1 ? "LONG" : "SHORT";
                    lblStatus.Text       = "● " + dir + "  |  Stops hidden  |  DCA " + openDcaCount + "/" + dcaMaxPositions;
                    lblStatus.Foreground = openTradeDirection == 1 ? Brushes.LimeGreen : Brushes.OrangeRed;
                }
                else
                {
                    lblStatus.Text       = "● Flat — ready";
                    lblStatus.Foreground = Brushes.CornflowerBlue;
                }
            });
        }

        private void RemoveButtonPanel()
        {
            if (buttonPanel == null || ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (ChartControl.Parent is Grid g)
                    g.Children.Remove(buttonPanel);
            });
        }

        // ─── Strategy dialog properties ───────────────────────────
        #region Properties

        [NinjaScriptProperty]
        [Range(20, 200)]
        [Display(Name = "Stop Loss (NQ points)", Order = 1, GroupName = "1 — Risk Management",
                 Description = "Hidden SL from average entry. No live order — market exit fires internally.")]
        public int SlPoints { get { return slPoints; } set { slPoints = value; } }

        [NinjaScriptProperty]
        [Range(10, 400)]
        [Display(Name = "Take Profit (NQ points)", Order = 2, GroupName = "1 — Risk Management",
                 Description = "Hidden TP from average entry.")]
        public int TpPoints { get { return tpPoints; } set { tpPoints = value; } }

        [NinjaScriptProperty]
        [Range(500, 10000)]
        [Display(Name = "Max Daily Loss ($)", Order = 3, GroupName = "1 — Risk Management",
                 Description = "Hard daily loss cap — all trading halts when hit.")]
        public int MaxDailyLossDollars { get { return maxDailyLossDollars; } set { maxDailyLossDollars = value; } }

        [NinjaScriptProperty]
        [Range(500, 20000)]
        [Display(Name = "Daily Profit Target ($)", Order = 4, GroupName = "1 — Risk Management",
                 Description = "Prints alert when daily profit target is reached.")]
        public int MaxDailyProfitDollars { get { return maxDailyProfitDollars; } set { maxDailyProfitDollars = value; } }

        [NinjaScriptProperty]
        [Range(1, 4)]
        [Display(Name = "Contracts per entry", Order = 5, GroupName = "1 — Risk Management")]
        public int Contracts { get { return contracts; } set { contracts = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Enable DCA", Order = 1, GroupName = "2 — DCA Settings")]
        public bool DcaEnabled { get { return dcaEnabled; } set { dcaEnabled = value; } }

        [NinjaScriptProperty]
        [Range(1, 2)]
        [Display(Name = "Max DCA Adds", Order = 2, GroupName = "2 — DCA Settings",
                 Description = "Hard cap on DCA adds. Recommended: 2 maximum.")]
        public int DcaMaxPositions { get { return dcaMaxPositions; } set { dcaMaxPositions = value; } }

        [NinjaScriptProperty]
        [Range(40, 150)]
        [Display(Name = "DCA Min Distance (NQ points)", Order = 3, GroupName = "2 — DCA Settings",
                 Description = "Minimum distance from average entry before DCA add is allowed.")]
        public int DcaDistancePoints { get { return dcaDistancePoints; } set { dcaDistancePoints = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Auto Mode", Order = 1, GroupName = "3 — Trading Mode",
                 Description = "True = auto entries. False = semi-auto with on-chart LONG/SHORT buttons.")]
        public bool AutoMode { get { return autoMode; } set { autoMode = value; } }

        [NinjaScriptProperty]
        [Range(0, 3)]
        [Display(Name = "Auto Strategy (0-3)", Order = 2, GroupName = "3 — Trading Mode",
                 Description = "0=Momentum+VWAP  1=Key Level Breakout  2=Liquidity Sweep Reversal  3=ORB")]
        public int AutoStrategy { get { return autoStrategy; } set { autoStrategy = value; } }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Min Signal Confidence (%)", Order = 3, GroupName = "3 — Trading Mode",
                 Description = "Auto mode only enters when composite signal score meets this threshold.")]
        public double MinSignalConfidence { get { return minSignalConfidence; } set { minSignalConfidence = value; } }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "Entry Delay (seconds, logged only)", Order = 4, GroupName = "3 — Trading Mode")]
        public int EntryDelaySeconds { get { return entryDelaySeconds; } set { entryDelaySeconds = value; } }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "EMA Fast Period", Order = 1, GroupName = "4 — Indicators")]
        public int EmaPeriodFast { get { return emaPeriodFast; } set { emaPeriodFast = value; } }

        [NinjaScriptProperty]
        [Range(10, 200)]
        [Display(Name = "EMA Slow Period", Order = 2, GroupName = "4 — Indicators")]
        public int EmaPeriodSlow { get { return emaPeriodSlow; } set { emaPeriodSlow = value; } }

        [NinjaScriptProperty]
        [Range(5, 30)]
        [Display(Name = "RSI Period", Order = 3, GroupName = "4 — Indicators")]
        public int RsiPeriod { get { return rsiPeriod; } set { rsiPeriod = value; } }

        [NinjaScriptProperty]
        [Range(5, 30)]
        [Display(Name = "ATR Period", Order = 4, GroupName = "4 — Indicators")]
        public int AtrPeriod { get { return atrPeriod; } set { atrPeriod = value; } }

        #endregion
    }
}
