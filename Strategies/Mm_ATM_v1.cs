// ============================================================
//  Mm-ATM v1.0 Strategy for NinjaTrader 8
//  Full documentation: see Mm_ATM_v1.md
// ============================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    public class Mm_ATM_v1 : Strategy
    {
        #region Fields

        // ─── Constants ────────────────────────────────────────────
        private const double NQ_TICKS_PER_POINT  = 4.0;
        private const double NQ_DOLLARS_PER_TICK  = 5.0;
        private const double NQ_DOLLARS_PER_POINT = 20.0;

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
        private int    slTpAdjustStep;
        private int    jumpSlPercent;
        private int    maxTradesPerDay;
        private int    dailyTradeCount;
        private bool   showEma;
        private bool   showRsi;
        private bool   showAtr;
        private bool   showVwap;
        private bool   showKeyLevels;
        private bool   showSweepSignals;
        private int    tradingStartTime;
        private int    tradingEndTime;
        private int    flattenTime;
        private bool   flattenFired;
        private bool   tradingHoursEnabled;
        private bool   enablePostEntryTrapDetector;

        // ─── Indicator references ─────────────────────────────────
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaFast;
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaSlow;
        private NinjaTrader.NinjaScript.Indicators.RSI indRsi;
        private NinjaTrader.NinjaScript.Indicators.ATR indAtr;

        // ─── Session / daily tracking ─────────────────────────────
        private double   dailyRealizedPnL;
        private double   pnlBaselineOffset;
        private double   sessionRealizedPnL;
        private bool     dailyLimitHit;
        private bool     dailyProfitHit;
        private DateTime sessionDate;

        // ─── Hidden SL/TP state ───────────────────────────────────
        private double hiddenStopPrice;
        private double hiddenTargetPrice;
        private bool   stopsArmed;
        private int    openTradeDirection;
        private int    defaultSlPoints;
        private int    defaultTpPoints;
        private int    defaultContracts;
        private int    defaultJumpSlPercent;

        // ─── DCA tracking ─────────────────────────────────────────
        private int    openDcaCount;
        private double averageEntryPrice;
        private double totalContracts;
        private int    tradeSequence;
        private readonly List<string> activeEntrySignals = new List<string>();

        // ─── Manual VWAP (tick-safe) ──────────────────────────────
        private double vwapCumTPV;
        private double vwapCumVol;
        private double vwapValue;
        private double prevBarVwap;
        private double currBarTPV;
        private double currBarVol;

        // ─── Thread-safe button flags & cooldown ──────────────────
        private bool     pendingLong;
        private bool     pendingShort;
        private bool     pendingLongLimit;
        private bool     pendingShortLimit;
        private bool     pendingBuyAsk;
        private bool     pendingSellBid;
        private bool     pendingFlatten;
        private bool     pendingCloseTrade;
        private bool     pendingCloseOne;
        private bool     pendingJumpSL;
        private bool     pendingRearm;
        private bool     pendingExit;
        private bool     pendingReverseLong;
        private bool     pendingReverseShort;
        private bool     pendingReverseLongLmt;
        private bool     pendingReverseShortLmt;
        private bool     pendingReverseLongAsk;
        private bool     pendingReverseShortBid;
        private bool     pendingLimitFlatten;
        private int      pendingExitTicks;
        private int      flatSyncGraceTicks;
        private DateTime lastEntryWallTime;
        private bool     firstBarSeen;
        private bool     emergencyKillActive;
        private int      reversePendingTicks;
        private DateTime aggressiveLimitSubmitTime;

        // ─── Dashboard build retry ────────────────────────────────
        private int  dashBuildRetryCount;
        private int  dashBuildTickCounter;

        // ─── ORB state ────────────────────────────────────────────
        private double orbHigh;
        private double orbLow;
        private bool   orbSet;

        // ─── Signal confidence tracking ───────────────────────────
        private double lastBullConfidence;
        private double lastBearConfidence;
        private static readonly string[] StrategyNames = new string[]
        {
            "Momentum+VWAP",
            "Key Lvl Breakout",
            "Liq Sweep Rev",
            "Opening Range B/O",
            "\u2605 Auto Select"
        };

        // ─── On-chart dashboard elements ──────────────────────────
        private Grid      dashboardPanel;
        private Button    btnBuyMkt;
        private Button    btnSellMkt;
        private Button    btnBuyAsk;
        private Button    btnSellBid;
        private Button    btnBuyLmt;
        private Button    btnSellLmt;
        private Button    btnCloseTrade;
        private Button    btnCloseOne;
        private Button    btnExit;
        private Button    btnJumpSL;
        private Button    btnModeManual;
        private Button    btnModeAuto;
        private Button    btnHoursToggle;
        private TextBlock lblStatus;
        private TextBlock lblPnL;
        private TextBlock lblUnrealized;
        private TextBlock lblTotalPnL;
        private TextBlock lblAccountBal;
        private TextBlock lblConfBull;
        private TextBlock lblConfBear;
        private TextBlock lblPosition;
        private TextBlock lblHiddenSL;
        private TextBlock lblHiddenTP;
        private TextBlock lblStratName;
        private TextBlock lblQtyVal;
        private TextBlock lblSlVal;
        private TextBlock lblTpVal;
        private TextBlock lblVwapVal;
        private TextBlock lblTradeHours;
        private TextBlock lblJumpPct;
        private TextBlock lblTrapInfo;
        private StackPanel stratPanel;

        // ─── Dashboard placement & drag/resize ────────────────────
        private int            bestAutoStrategy;
        private bool           dashboardAttached;
        private Panel          dashboardHostPanel;
        private TranslateTransform dashTranslate;
        private bool           dashDragging;
        private bool           dashResizing;
        private Point          dashDragStart;
        private Point          dashResizeStart;
        private double         dashOrigWidth;
        private double         dashOrigHeight;
        private Border         dashBorder;
        private ScrollViewer   dashScroll;
        private Border         dashTitleBar;
        private Border         dashResizeGrip;

        // ─── Adaptive Trailing Stop ───────────────────────────────
        private bool      trailEnabled;
        private int       trailActivationPoints;
        private int       trailMinPoints;
        private int       trailMaxPoints;
        private double    trailAtrMultiplier;
        private double    trailPrice;
        private bool      trailActive;
        private double    trailTrendScore;
        private double    trailMaxProfitPts;
        private string    trailTierName;
        private TextBlock lblTrailInfo;
        private Button    btnTrailToggle;
        private int       volatilitySpikeGuardBars;

        // ─── Smart Signal Filters ─────────────────────────────────
        private double lastRawBull;
        private double lastRawBear;

        // ─── Post-Entry Trap Detector ─────────────────────────────
        private bool   trapDetectorEnabled;
        private int    trapDetectWindowBars;
        private int    trapReverseCount;
        private double trapRangeHigh;
        private double trapRangeLow;
        private bool   trapPatternActive;
        private int    trapPatternBarsActive;
        private int    trapAdaptiveMode;
        private double trapOriginalTP;
        private int    trapExpiryCandidateBars;
        private readonly Queue<int> trapDirectionHistory = new Queue<int>();
        private Button btnTrapToggle;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Mm-ATM v1.0 — Native Order Types + Post-Entry Trap Detector + 24H Mode + Hardened Enable/Disable";
                Name         = "Mm_ATM_v1";
                Calculate    = Calculate.OnEachTick;
                EntriesPerDirection          = 4;
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
                dcaDistancePoints     = 0;
                entryDelaySeconds     = 5;
                autoMode              = false;
                autoStrategy          = 0;
                emaPeriodFast         = 9;
                emaPeriodSlow         = 21;
                rsiPeriod             = 14;
                atrPeriod             = 14;
                minSignalConfidence   = 68.0;
                slTpAdjustStep        = 5;
                jumpSlPercent         = 50;
                maxTradesPerDay       = 999;
                showEma               = true;
                showRsi               = true;
                showAtr               = true;
                showVwap              = true;
                showKeyLevels         = true;
                showSweepSignals      = true;
                tradingStartTime      = 93000;
                tradingEndTime        = 160000;
                flattenTime           = 165000;
                tradingHoursEnabled   = true;
                enablePostEntryTrapDetector = false;

                trailEnabled          = true;
                trailActivationPoints = 8;
                trailMinPoints        = 3;
                trailMaxPoints        = 25;
                trailAtrMultiplier    = 1.5;
            }
            else if (State == State.Configure)
            {
                indEmaFast = EMA(emaPeriodFast);
                indEmaSlow = EMA(emaPeriodSlow);
                indRsi     = RSI(rsiPeriod, 3);
                indAtr     = ATR(atrPeriod);

                if (showEma)
                {
                    AddChartIndicator(indEmaFast);
                    AddChartIndicator(indEmaSlow);
                }
                if (showRsi)
                    AddChartIndicator(indRsi);
                if (showAtr)
                    AddChartIndicator(indAtr);
            }
            else if (State == State.DataLoaded)
            {
                ResetVwap();
                dailyRealizedPnL = 0;
                dailyLimitHit    = false;
                dailyProfitHit   = false;
                defaultSlPoints  = slPoints;
                defaultTpPoints  = tpPoints;
                defaultContracts = contracts;
                defaultJumpSlPercent = jumpSlPercent;
            }
            else if (State == State.Realtime)
            {
                // ── FULL RESET on Historical → Realtime (Section 0a) ──
                dailyTradeCount        = 0;
                dailyRealizedPnL       = 0;
                sessionRealizedPnL     = 0;
                dailyLimitHit          = false;
                dailyProfitHit         = false;
                flattenFired           = false;
                emergencyKillActive    = false;

                pendingExit            = false;
                pendingExitTicks       = 0;
                pendingLimitFlatten    = false;
                pendingReverseLong     = false;
                pendingReverseShort    = false;
                pendingReverseLongLmt  = false;
                pendingReverseShortLmt = false;
                pendingReverseLongAsk  = false;
                pendingReverseShortBid = false;
                pendingLong            = false;
                pendingShort           = false;
                pendingLongLimit       = false;
                pendingShortLimit      = false;
                pendingBuyAsk          = false;
                pendingSellBid         = false;
                pendingFlatten         = false;
                pendingCloseTrade      = false;
                pendingCloseOne        = false;
                pendingJumpSL          = false;
                pendingRearm           = false;
                reversePendingTicks    = 0;

                stopsArmed             = false;
                openTradeDirection     = 0;
                openDcaCount           = 0;
                totalContracts         = 0;
                averageEntryPrice      = 0;
                hiddenStopPrice        = 0;
                hiddenTargetPrice      = 0;
                flatSyncGraceTicks     = 0;
                lastEntryWallTime      = DateTime.MinValue;
                aggressiveLimitSubmitTime = DateTime.MinValue;
                activeEntrySignals.Clear();

                trailPrice             = 0;
                trailActive            = false;
                trailTrendScore        = 0;
                trailMaxProfitPts      = 0;
                trailTierName          = "";
                volatilitySpikeGuardBars = 0;

                trapPatternActive      = false;
                trapAdaptiveMode       = 0;
                trapPatternBarsActive  = 0;
                trapOriginalTP         = 0;
                trapExpiryCandidateBars = 0;
                trapReverseCount       = 0;
                trapRangeHigh          = 0;
                trapRangeLow           = 0;
                trapDetectWindowBars   = 20;
                trapDetectorEnabled    = enablePostEntryTrapDetector;
                trapDirectionHistory.Clear();

                orbHigh                = 0;
                orbLow                 = 0;
                orbSet                 = false;

                lastBullConfidence     = 0;
                lastBearConfidence     = 0;
                lastRawBull            = 0;
                lastRawBear            = 0;
                bestAutoStrategy       = 0;

                firstBarSeen           = false;
                dashDragging           = false;
                dashResizing           = false;
                dashBuildRetryCount    = 0;
                dashBuildTickCounter   = 0;

                // Reset runtime values to defaults (Section 0f)
                slPoints      = defaultSlPoints > 0 ? defaultSlPoints : slPoints;
                tpPoints      = defaultTpPoints > 0 ? defaultTpPoints : tpPoints;
                contracts     = defaultContracts > 0 ? defaultContracts : contracts;
                jumpSlPercent = defaultJumpSlPercent > 0 ? defaultJumpSlPercent : jumpSlPercent;

                // Snapshot existing P&L so display starts at $0 (Section 0d)
                pnlBaselineOffset = 0;
                if (SystemPerformance != null && SystemPerformance.AllTrades != null)
                {
                    foreach (Trade t in SystemPerformance.AllTrades)
                        if (t.Entry.Time.Date == sessionDate)
                            pnlBaselineOffset += t.ProfitCurrency;
                }
                dailyRealizedPnL = 0;

                Print("State.Realtime: FULL RESET — pnlBaseline=" + pnlBaselineOffset.ToString("C2")
                    + " SL=" + slPoints + " TP=" + tpPoints + " contracts=" + contracts);
                Print("State.Realtime: dailyLimitHit RESET — daily loss guard cleared");
                Print("State.Realtime: emergencyKillActive RESET — emergency kill cleared");
            }
            else if (State == State.Terminated)
            {
                RemoveDashboard();
            }
        }

        #endregion

        #region OnBarUpdate

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            // ═════ CRITICAL PATH ══════════════════════════════════
            if (State == State.Realtime && pendingCloseTrade)
            {
                pendingCloseTrade = false;
                ExecuteCloseTrade();
            }
            if (State == State.Realtime && pendingFlatten)
            {
                pendingFlatten = false;
                ExecuteFlatten();
            }

            // Position state sync
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (pendingExit)
                {
                    Print(Time[0] + " | Position sync: exit confirmed FLAT, resetting state");
                    ResetPositionState();
                    flatSyncGraceTicks = 0;
                }
                else if (openTradeDirection != 0)
                {
                    flatSyncGraceTicks++;
                    if (flatSyncGraceTicks > 30)
                    {
                        Print(Time[0] + " | SAFETY: still flat after " + flatSyncGraceTicks + " ticks — resetting state");
                        ResetPositionState();
                        flatSyncGraceTicks = 0;
                    }
                }
                else
                {
                    flatSyncGraceTicks = 0;
                }
            }
            else
            {
                flatSyncGraceTicks = 0;
            }

            // Block entries while exit pending + stale exit safety net
            if (pendingExit)
            {
                if (pendingLong)    { pendingLong    = false; UpdateDashboardStatus("\u26a0 LONG blocked: exit pending", Brushes.Orange); }
                if (pendingShort)   { pendingShort   = false; UpdateDashboardStatus("\u26a0 SHORT blocked: exit pending", Brushes.Orange); }
                if (pendingBuyAsk)  { pendingBuyAsk  = false; UpdateDashboardStatus("\u26a0 BUY ASK blocked: exit pending", Brushes.Orange); }
                if (pendingSellBid) { pendingSellBid = false; UpdateDashboardStatus("\u26a0 SELL BID blocked: exit pending", Brushes.Orange); }

                pendingExitTicks++;
                if (pendingExitTicks >= 50 && Position.MarketPosition != MarketPosition.Flat)
                {
                    Print(Time[0] + " | STALE EXIT DETECTED (" + pendingExitTicks + " ticks) — Account.Flatten");
                    try { Account.Flatten(new[] { Instrument }); }
                    catch (Exception ex) { Print("Stale-exit Account.Flatten failed: " + ex.Message); }
                    pendingExitTicks = 0;
                }
            }
            else
            {
                pendingExitTicks = 0;
            }

            // Aggressive limit order timeout (3 seconds)
            if (State == State.Realtime && aggressiveLimitSubmitTime != DateTime.MinValue
                && (DateTime.Now - aggressiveLimitSubmitTime).TotalSeconds >= 3
                && Position.MarketPosition == MarketPosition.Flat && openTradeDirection != 0)
            {
                Print(Time[0] + " | ASK/BID order expired (3s timeout) — cancelling");
                CancelPendingOrders();
                ResetPositionState();
                aggressiveLimitSubmitTime = DateTime.MinValue;
                UpdateDashboardStatus("\u26a0 ASK/BID order expired — not filled", Brushes.Orange);
            }

            // Button entries
            if (State == State.Realtime && !pendingExit)
            {
                // Reverse entries with 2-tick delay (Section 1d)
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    bool anyReversePending = pendingReverseLong || pendingReverseShort
                        || pendingReverseLongLmt || pendingReverseShortLmt
                        || pendingReverseLongAsk || pendingReverseShortBid;
                    if (anyReversePending) reversePendingTicks++;

                    if (reversePendingTicks >= 2)
                    {
                        if (pendingReverseLong)          { pendingReverseLong = false;       reversePendingTicks = 0; ExecuteLongEntry(true); }
                        else if (pendingReverseShort)     { pendingReverseShort = false;      reversePendingTicks = 0; ExecuteShortEntry(true); }
                        else if (pendingReverseLongLmt)   { pendingReverseLongLmt = false;    reversePendingTicks = 0; ExecuteLongLimitEntry(); }
                        else if (pendingReverseShortLmt)  { pendingReverseShortLmt = false;   reversePendingTicks = 0; ExecuteShortLimitEntry(); }
                        else if (pendingReverseLongAsk)   { pendingReverseLongAsk = false;    reversePendingTicks = 0; ExecuteBuyAskEntry(); }
                        else if (pendingReverseShortBid)  { pendingReverseShortBid = false;   reversePendingTicks = 0; ExecuteSellBidEntry(); }
                    }
                }

                if (pendingLong)       { pendingLong       = false; ExecuteLongEntry(true); }
                if (pendingShort)      { pendingShort      = false; ExecuteShortEntry(true); }
                if (pendingBuyAsk)     { pendingBuyAsk     = false; ExecuteBuyAskEntry(); }
                if (pendingSellBid)    { pendingSellBid    = false; ExecuteSellBidEntry(); }
                if (pendingLongLimit)  { pendingLongLimit  = false; ExecuteLongLimitEntry(); }
                if (pendingShortLimit) { pendingShortLimit = false; ExecuteShortLimitEntry(); }
                if (pendingCloseOne)   { pendingCloseOne   = false; ExecuteCloseOne(); }
                if (pendingJumpSL)     { pendingJumpSL     = false; ExecuteJumpSL(); }
                if (pendingRearm)      { pendingRearm      = false; if (stopsArmed) ArmHiddenStops(); }
            }

            // Hidden SL/TP monitor
            if (stopsArmed && !pendingExit && Position.MarketPosition != MarketPosition.Flat)
                MonitorHiddenStops();

            // Adaptive trailing stop monitor
            if (trailEnabled && !pendingExit && Position.MarketPosition != MarketPosition.Flat)
                MonitorAdaptiveTrail();

            // Post-entry trap detector (once per bar)
            if (trapDetectorEnabled && IsFirstTickOfBar && !pendingExit && Position.MarketPosition != MarketPosition.Flat)
                MonitorPostEntryTrap();

            // ═════ SESSION HOUSEKEEPING ════════════════════════════
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

            // Dashboard build with retry (Section 0c)
            if (State == State.Realtime && !dashboardAttached && dashBuildRetryCount < 10)
            {
                dashBuildTickCounter++;
                if (dashBuildTickCounter >= 5)
                {
                    dashBuildTickCounter = 0;
                    BuildDashboard();
                }
            }

            // ─── Auto-flatten & CME maintenance (Section 0b: guarded) ──
            if (State == State.Realtime)
            {
                int currentTime = ToTime(Time[0]);

                // Auto-flatten at flattenTime
                if (currentTime >= flattenTime && !flattenFired && Position.MarketPosition != MarketPosition.Flat)
                {
                    flattenFired = true;
                    Print(Time[0] + " | AUTO-FLATTEN at " + Time[0].ToString("HH:mm:ss"));
                    ExecuteFlatten();
                }

                // CME maintenance hard flatten (4:55 PM – 6:00 PM ET)
                if (currentTime >= 165500 && currentTime < 180000 && Position.MarketPosition != MarketPosition.Flat)
                {
                    Print(Time[0] + " | CME SESSION CLOSE FLATTEN at " + Time[0].ToString("HH:mm:ss"));
                    ExecuteFlatten();
                }

                // Deferred daily limit flatten
                if (pendingLimitFlatten && Position.MarketPosition != MarketPosition.Flat)
                {
                    pendingLimitFlatten = false;
                    Print(Time[0] + " | DEFERRED FLATTEN executing from OnBarUpdate");
                    ExecuteFlatten();
                }
                else if (pendingLimitFlatten)
                {
                    pendingLimitFlatten = false;
                }

                if (dailyLimitHit)
                    UpdateDashboardStatus("DAILY LOSS LIMIT — NO TRADES", Brushes.OrangeRed);
            }

            // ─── Calculate signals + auto entry ───────────────────
            CalculateSignals();

            if (State == State.Realtime)
            {
                int currentTime2 = ToTime(Time[0]);
                bool insideTradingHours = !tradingHoursEnabled
                    || (currentTime2 >= tradingStartTime && currentTime2 < tradingEndTime);

                if (autoMode && !dailyLimitHit && !dailyProfitHit && !emergencyKillActive
                    && insideTradingHours
                    && Position.MarketPosition == MarketPosition.Flat && openTradeDirection == 0
                    && (maxTradesPerDay <= 0 || dailyTradeCount < maxTradesPerDay))
                {
                    if (lastBullConfidence >= minSignalConfidence)
                        ExecuteLongEntry(false);
                    else if (lastBearConfidence >= minSignalConfidence)
                        ExecuteShortEntry(false);
                }
                else if (autoMode && CurrentBar % 100 == 0 && Position.MarketPosition == MarketPosition.Flat)
                {
                    string reason = "";
                    if (dailyLimitHit) reason = "dailyLossLimit";
                    else if (dailyProfitHit) reason = "dailyProfitHit";
                    else if (emergencyKillActive) reason = "emergencyKill";
                    else if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay) reason = "maxTradesPerDay";
                    else if (!insideTradingHours) reason = "outsideHours";
                    else if (openTradeDirection != 0) reason = "openTradeDir=" + openTradeDirection;
                    else reason = "lowConfidence(Bull=" + lastBullConfidence.ToString("F0") + "% Bear=" + lastBearConfidence.ToString("F0") + "%)";
                    Print(Time[0] + " | AUTO-SCAN: no entry — " + reason);
                }
            }

            // P&L sanity check every 50 bars (Section 5a)
            if (State == State.Realtime && CurrentBar % 50 == 0)
            {
                double check = 0;
                if (SystemPerformance != null && SystemPerformance.AllTrades != null)
                {
                    foreach (Trade t in SystemPerformance.AllTrades)
                        if (t.Entry.Time.Date == sessionDate)
                            check += t.ProfitCurrency;
                }
                check -= pnlBaselineOffset;
                if (Math.Abs(check - dailyRealizedPnL) > 10)
                {
                    Print(Time[0] + " | P&L RESYNC: was=" + dailyRealizedPnL.ToString("C2") + " corrected=" + check.ToString("C2"));
                    dailyRealizedPnL = check;
                    sessionRealizedPnL = check;
                }
            }

            // ─── Chart & dashboard ────────────────────────────────
            UpdateOrbLevels();
            DrawChartAnnotations();
            DrawDcaLevels();
            DrawVwapLine();
            DrawKeyLevels();
            DrawSweepSignals();
            UpdateDashboard();
        }

        #endregion

        #region Monitors

        // ═══════════════════════════════════════════════════════════
        //  HIDDEN SL/TP MONITOR
        // ═══════════════════════════════════════════════════════════

        private void MonitorHiddenStops()
        {
            // Section 4e: daily limit check at top
            if (dailyLimitHit || emergencyKillActive) return;

            double price = Close[0];
            var signals = activeEntrySignals.ToList();
            if (signals.Count == 0) return;

            if (openTradeDirection == 1)
            {
                if (price <= hiddenStopPrice)
                {
                    Print(Time[0] + " | HIDDEN SL LONG hit @ " + price.ToString("F2"));
                    foreach (string sig in signals)
                        ExitLong("Close", sig);
                    stopsArmed  = false;
                    pendingExit = true;
                }
                else if (price >= hiddenTargetPrice)
                {
                    Print(Time[0] + " | HIDDEN TP LONG hit @ " + price.ToString("F2"));
                    foreach (string sig in signals)
                        ExitLong("Close", sig);
                    stopsArmed  = false;
                    pendingExit = true;
                }
            }
            else if (openTradeDirection == -1)
            {
                if (price >= hiddenStopPrice)
                {
                    Print(Time[0] + " | HIDDEN SL SHORT hit @ " + price.ToString("F2"));
                    foreach (string sig in signals)
                        ExitShort("Close", sig);
                    stopsArmed  = false;
                    pendingExit = true;
                }
                else if (price <= hiddenTargetPrice)
                {
                    Print(Time[0] + " | HIDDEN TP SHORT hit @ " + price.ToString("F2"));
                    foreach (string sig in signals)
                        ExitShort("Close", sig);
                    stopsArmed  = false;
                    pendingExit = true;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  ADAPTIVE TRAILING STOP
        // ═══════════════════════════════════════════════════════════

        private void MonitorAdaptiveTrail()
        {
            if (!trailEnabled || !stopsArmed || openTradeDirection == 0) return;

            // Section 4d: volatility spike guard
            if (volatilitySpikeGuardBars > 0 && CurrentBar > 1)
            {
                double barRange = High[0] - Low[0];
                double atrNow = indAtr[0];
                if (atrNow > 0 && barRange > atrNow * 3.0)
                {
                    Print(Time[0] + " | TRAIL: volatility spike guard (bar range " + barRange.ToString("F2") + " > 3×ATR " + (atrNow * 3).ToString("F2") + ") — skipping " + volatilitySpikeGuardBars + " bars");
                    return;
                }
            }

            double price = Close[0];
            double profitPoints = 0;

            if (openTradeDirection == 1)
                profitPoints = (price - averageEntryPrice) / (TickSize * NQ_TICKS_PER_POINT);
            else if (openTradeDirection == -1)
                profitPoints = (averageEntryPrice - price) / (TickSize * NQ_TICKS_PER_POINT);

            if (profitPoints > trailMaxProfitPts)
                trailMaxProfitPts = profitPoints;

            if (!trailActive)
            {
                if (profitPoints >= trailActivationPoints)
                {
                    trailActive = true;
                    trailTierName = "Active";
                    double initDist = CalculateTrailDistance();
                    double distOff  = initDist * NQ_TICKS_PER_POINT * TickSize;
                    if (openTradeDirection == 1)
                        trailPrice = price - distOff;
                    else
                        trailPrice = price + distOff;
                    trailPrice = Math.Round(trailPrice / TickSize) * TickSize;
                    Print(Time[0] + " | TRAIL ACTIVATED @ profit=" + profitPoints.ToString("F1")
                        + "pts, dist=" + initDist.ToString("F1") + "pts, trail=" + trailPrice.ToString("F2")
                        + " trend=" + (trailTrendScore * 100).ToString("F0") + "%");
                }
                return;
            }

            double trailDist = CalculateTrailDistance();
            double trailOff  = trailDist * NQ_TICKS_PER_POINT * TickSize;

            var signals = activeEntrySignals.ToList();
            if (signals.Count == 0) return;

            double tierFloor = 0;
            double tickPt = TickSize * NQ_TICKS_PER_POINT;

            if (trailMaxProfitPts >= trailActivationPoints * 4)
            {
                tierFloor = 0.55 * trailMaxProfitPts * tickPt;
                trailTierName = "T3-Runner";
            }
            else if (trailMaxProfitPts >= trailActivationPoints * 2.5)
            {
                tierFloor = 0.45 * trailMaxProfitPts * tickPt;
                trailTierName = "T2-Strong";
            }
            else if (trailMaxProfitPts >= trailActivationPoints * 2)
            {
                tierFloor = TickSize;
                trailTierName = "T1-BE";
            }
            else
            {
                trailTierName = "Active";
            }

            if (openTradeDirection == 1)
            {
                double newTrail = Math.Round((price - trailOff) / TickSize) * TickSize;
                if (newTrail > trailPrice)
                    trailPrice = newTrail;

                double floorPrice = Math.Round((averageEntryPrice + tierFloor) / TickSize) * TickSize;
                if (tierFloor > 0 && trailPrice < floorPrice)
                    trailPrice = floorPrice;

                if (price <= trailPrice)
                {
                    Print(Time[0] + " | ADAPTIVE TRAIL HIT (LONG) @ " + price.ToString("F2")
                        + " trail=" + trailPrice.ToString("F2") + " dist=" + trailDist.ToString("F1")
                        + "pts trend=" + (trailTrendScore * 100).ToString("F0") + "% tier=" + trailTierName
                        + " maxProfit=" + trailMaxProfitPts.ToString("F1") + "pts");
                    foreach (string sig in signals)
                        ExitLong("Close", sig);
                    stopsArmed  = false;
                    pendingExit = true;
                }
            }
            else if (openTradeDirection == -1)
            {
                double newTrail = Math.Round((price + trailOff) / TickSize) * TickSize;
                if (newTrail < trailPrice)
                    trailPrice = newTrail;

                double floorPrice = Math.Round((averageEntryPrice - tierFloor) / TickSize) * TickSize;
                if (tierFloor > 0 && trailPrice > floorPrice)
                    trailPrice = floorPrice;

                if (price >= trailPrice)
                {
                    Print(Time[0] + " | ADAPTIVE TRAIL HIT (SHORT) @ " + price.ToString("F2")
                        + " trail=" + trailPrice.ToString("F2") + " dist=" + trailDist.ToString("F1")
                        + "pts trend=" + (trailTrendScore * 100).ToString("F0") + "% tier=" + trailTierName
                        + " maxProfit=" + trailMaxProfitPts.ToString("F1") + "pts");
                    foreach (string sig in signals)
                        ExitShort("Close", sig);
                    stopsArmed  = false;
                    pendingExit = true;
                }
            }
        }

        private double CalculateTrailDistance()
        {
            trailTrendScore = 0;

            if (CurrentBar < emaPeriodSlow + 5)
            {
                trailTrendScore = 0.5;
                return LerpD(trailMinPoints, trailMaxPoints, 0.5);
            }

            double atr = indAtr[0];
            if (atr <= 0) atr = TickSize;

            double atrSum = 0;
            int lookback = Math.Min(10, CurrentBar);
            for (int i = 0; i < lookback; i++)
                atrSum += indAtr[i];
            double atrAvg = atrSum / lookback;
            double atrExpansion = atrAvg > 0 ? (atr / atrAvg) : 1.0;
            double atrScore = Math.Min(1.0, Math.Max(0.0, (atrExpansion - 0.9) / 0.5));

            double emaSpread = Math.Abs(indEmaFast[0] - indEmaSlow[0]) / atr;
            double emaScore = Math.Min(1.0, emaSpread / 2.5);

            int dirBars = 0;
            int checkBars = Math.Min(8, CurrentBar - 1);
            for (int i = 0; i < checkBars; i++)
            {
                if (openTradeDirection == 1 && Close[i] > Close[i + 1]) dirBars++;
                else if (openTradeDirection == -1 && Close[i] < Close[i + 1]) dirBars++;
            }
            double dirScore = checkBars > 0 ? (double)dirBars / checkBars : 0.5;

            double rsiDist = Math.Abs(indRsi[0] - 50.0) / 50.0;
            double rsiScore = Math.Min(1.0, rsiDist * 1.5);

            double barRange = High[0] - Low[0];
            double rangeRatio = atr > 0 ? barRange / atr : 1.0;
            double rangeScore = Math.Min(1.0, Math.Max(0.0, (rangeRatio - 0.3) / 0.9));

            trailTrendScore = atrScore * 0.25 + emaScore * 0.25 + dirScore * 0.25 + rsiScore * 0.10 + rangeScore * 0.15;
            trailTrendScore = Math.Min(1.0, Math.Max(0.0, trailTrendScore));

            double chopMultiplier = trailTrendScore < 0.5
                ? 0.5 + trailTrendScore
                : 1.0;

            double atrDist = atr * trailAtrMultiplier / (TickSize * NQ_TICKS_PER_POINT);

            double regimeDist = LerpD(trailMinPoints, trailMaxPoints, trailTrendScore);
            double finalDist  = (regimeDist * 0.6 + atrDist * 0.4) * chopMultiplier;

            finalDist = Math.Max(trailMinPoints, Math.Min(trailMaxPoints, finalDist));
            return finalDist;
        }

        private double LerpD(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        // ═══════════════════════════════════════════════════════════
        //  POST-ENTRY TRAP DETECTOR (Section 7)
        // ═══════════════════════════════════════════════════════════

        private void MonitorPostEntryTrap()
        {
            if (!trapDetectorEnabled || openTradeDirection == 0 || !stopsArmed) return;
            if (CurrentBar < 5) return;

            double price = Close[0];
            double atr = indAtr[0];
            if (atr <= 0) return;

            // Check for adverse move since entry
            double adverseMove = 0;
            if (openTradeDirection == 1)
                adverseMove = (averageEntryPrice - price) / (TickSize * NQ_TICKS_PER_POINT);
            else
                adverseMove = (price - averageEntryPrice) / (TickSize * NQ_TICKS_PER_POINT);

            // Trap score: accumulate evidence of being trapped
            trapScore = 0;

            // Factor 1: Adverse move relative to ATR
            double atrPts = atr / (TickSize * NQ_TICKS_PER_POINT);
            if (atrPts > 0 && adverseMove > 0)
            {
                double moveRatio = adverseMove / atrPts;
                if (moveRatio > 0.5) trapScore += moveRatio * 30;  // up to ~30% per 1x ATR
            }

            // Factor 2: Momentum reversed against position
            if (openTradeDirection == 1 && indEmaFast[0] < indEmaSlow[0]) trapScore += 20;
            else if (openTradeDirection == -1 && indEmaFast[0] > indEmaSlow[0]) trapScore += 20;

            // Factor 3: Volume spike on adverse bar (stops being hunted)
            double volSum = 0;
            int vlb = Math.Min(10, CurrentBar - 1);
            for (int i = 1; i <= vlb; i++) volSum += Volume[i];
            double volAvg = vlb > 0 ? volSum / vlb : Volume[0];
            if (volAvg > 0 && Volume[0] > volAvg * 1.5 && adverseMove > 0) trapScore += 15;

            // Factor 4: Bar rejecting in adverse direction
            double barRange = High[0] - Low[0];
            if (barRange > 0)
            {
                if (openTradeDirection == 1)
                {
                    double upperWick = High[0] - Math.Max(Close[0], Open[0]);
                    if (upperWick / barRange > 0.5) trapScore += 10;  // rejection off highs while long
                }
                else
                {
                    double lowerWick = Math.Min(Close[0], Open[0]) - Low[0];
                    if (lowerWick / barRange > 0.5) trapScore += 10;  // rejection off lows while short
                }
            }

            trapBarsInTrade++;
            trapDetected = trapScore >= 50;

            if (trapDetected && trapBarsInTrade >= 3)
            {
                Print(Time[0] + " | TRAP DETECTED: score=" + trapScore.ToString("F0")
                    + " adverse=" + adverseMove.ToString("F1") + "pts bars=" + trapBarsInTrade);
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  ORDER UPDATE (Section 1e — fill confirmation hub)
        // ═══════════════════════════════════════════════════════════

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;

            if (orderState == OrderState.Filled)
            {
                // Arm stops on fill for aggressive limit entries
                if (order.IsLong && openTradeDirection == 1 && !stopsArmed)
                {
                    averageEntryPrice = averageFillPrice > 0 ? averageFillPrice : order.AverageFillPrice;
                    ArmHiddenStops();
                    aggressiveLimitSubmitTime = DateTime.MinValue;
                    Print(Time[0] + " | OnOrderUpdate: LONG fill confirmed @ " + averageEntryPrice.ToString("F2") + " — stops armed");
                }
                else if (order.IsShort && openTradeDirection == -1 && !stopsArmed)
                {
                    averageEntryPrice = averageFillPrice > 0 ? averageFillPrice : order.AverageFillPrice;
                    ArmHiddenStops();
                    aggressiveLimitSubmitTime = DateTime.MinValue;
                    Print(Time[0] + " | OnOrderUpdate: SHORT fill confirmed @ " + averageEntryPrice.ToString("F2") + " — stops armed");
                }
            }
            else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
            {
                if (aggressiveLimitSubmitTime != DateTime.MinValue)
                {
                    aggressiveLimitSubmitTime = DateTime.MinValue;
                    if (Position.MarketPosition == MarketPosition.Flat && openTradeDirection != 0)
                    {
                        Print(Time[0] + " | OnOrderUpdate: order " + orderState + " — resetting state (was unfilled)");
                        ResetPositionState();
                    }
                }
            }
        }

        #endregion

        #region Entry / Exit

        // ═══════════════════════════════════════════════════════════
        //  ENTRY GUARD (Section 9a — refactored)
        // ═══════════════════════════════════════════════════════════

        private bool CanEnterTrade(string label, bool isManual, int direction)
        {
            if (pendingExit) { Print("Blocked " + label + ": exit pending"); UpdateDashboardStatus("\u26a0 " + label + " blocked: exit pending", Brushes.Orange); return false; }
            if (dailyLimitHit || dailyProfitHit) { Print("Blocked " + label + ": daily limit hit"); UpdateDashboardStatus("\u26a0 " + label + " blocked: daily limit", Brushes.OrangeRed); return false; }
            if (emergencyKillActive) { Print("Blocked " + label + ": emergency kill active"); UpdateDashboardStatus("\u26a0 " + label + " blocked: KILL active", Brushes.OrangeRed); return false; }
            if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay && Position.MarketPosition == MarketPosition.Flat)
            { Print("Blocked " + label + ": max trades/day (" + dailyTradeCount + "/" + maxTradesPerDay + ")"); UpdateDashboardStatus("\u26a0 " + label + " blocked: max trades/day", Brushes.Orange); return false; }
            int ct = ToTime(Time[0]);
            if (ct >= 165500 && ct < 180000) { Print("Blocked " + label + ": CME maintenance (" + ct + ")"); UpdateDashboardStatus("\u26a0 " + label + " blocked: CME maintenance", Brushes.OrangeRed); return false; }
            if (!isManual && (ct < tradingStartTime || ct >= flattenTime)) { Print("Blocked " + label + ": outside auto hours (time=" + ct + ")"); UpdateDashboardStatus("\u26a0 " + label + " blocked: outside auto hours", Brushes.Orange); return false; }
            // Opposing position: partial close or reverse
            if (direction == 1 && Position.MarketPosition == MarketPosition.Short)
            {
                if (Position.Quantity > contracts)
                { Print(label + " pressed while SHORT DCA — closing 1 contract"); ExecuteCloseOne(); }
                else
                { Print(label + ": reversing — closing short first"); pendingReverseLong = (label == "BUY MKT"); pendingReverseLongAsk = (label == "BUY ASK"); pendingReverseLongLmt = (label == "BUY LMT"); ExecuteCloseTrade(); }
                return false;
            }
            if (direction == -1 && Position.MarketPosition == MarketPosition.Long)
            {
                if (Position.Quantity > contracts)
                { Print(label + " pressed while LONG DCA — closing 1 contract"); ExecuteCloseOne(); }
                else
                { Print(label + ": reversing — closing long first"); pendingReverseShort = (label == "SELL MKT"); pendingReverseShortBid = (label == "SELL BID"); pendingReverseShortLmt = (label == "SELL LMT"); ExecuteCloseTrade(); }
                return false;
            }
            if (!dcaEnabled && Position.MarketPosition != MarketPosition.Flat) { Print("Blocked " + label + ": DCA disabled"); UpdateDashboardStatus("\u26a0 " + label + " blocked: DCA off", Brushes.Orange); return false; }
            int expectedPos = direction == 1 ? (int)MarketPosition.Long : (int)MarketPosition.Short;
            if ((int)Position.MarketPosition == expectedPos && openDcaCount >= dcaMaxPositions)
            { Print("Blocked " + label + ": max DCA reached (" + dcaMaxPositions + ")"); UpdateDashboardStatus("\u26a0 " + label + " blocked: max DCA", Brushes.Orange); return false; }
            if (entryDelaySeconds > 0 && ((State == State.Realtime ? DateTime.Now : Time[0]) - lastEntryWallTime).TotalSeconds < entryDelaySeconds)
            { Print("Blocked " + label + ": cooldown (" + entryDelaySeconds + "s)"); UpdateDashboardStatus("\u26a0 " + label + " blocked: cooldown", Brushes.Orange); return false; }
            if (dcaDistancePoints > 0 && (int)Position.MarketPosition == expectedPos)
            {
                double distPts = Math.Abs(Close[0] - averageEntryPrice) / (TickSize * NQ_TICKS_PER_POINT);
                if (distPts < dcaDistancePoints) { Print("Blocked " + label + ": DCA too close (" + distPts.ToString("F1") + " pts, need " + dcaDistancePoints + ")"); UpdateDashboardStatus("\u26a0 " + label + " blocked: DCA too close", Brushes.Orange); return false; }
            }
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        //  MARKET ENTRIES
        // ═══════════════════════════════════════════════════════════

        private void ExecuteLongEntry(bool isManual = false)
        {
            if (!CanEnterTrade("BUY MKT", isManual, 1)) return;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            string signalName = " ";
            EnterLong(contracts, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = 1;
            lastEntryWallTime = (State == State.Realtime) ? DateTime.Now : Time[0];

            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0
                ? (averageEntryPrice * totalContracts + Close[0] * contracts) / newQty
                : Close[0];
            totalContracts = newQty;

            ArmHiddenStops();
            Print(Time[0] + " | LONG #" + openDcaCount + " qty=" + contracts
                + " @ " + Close[0].ToString("F2")
                + " | AvgEntry=" + averageEntryPrice.ToString("F2")
                + " | HiddenSL=" + hiddenStopPrice.ToString("F2")
                + " | HiddenTP=" + hiddenTargetPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf LONG #" + openDcaCount + " @ " + Close[0].ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteShortEntry(bool isManual = false)
        {
            if (!CanEnterTrade("SELL MKT", isManual, -1)) return;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            string signalName = " ";
            EnterShort(contracts, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = -1;
            lastEntryWallTime = (State == State.Realtime) ? DateTime.Now : Time[0];

            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0
                ? (averageEntryPrice * totalContracts + Close[0] * contracts) / newQty
                : Close[0];
            totalContracts = newQty;

            ArmHiddenStops();
            Print(Time[0] + " | SHORT #" + openDcaCount + " qty=" + contracts
                + " @ " + Close[0].ToString("F2")
                + " | AvgEntry=" + averageEntryPrice.ToString("F2")
                + " | HiddenSL=" + hiddenStopPrice.ToString("F2")
                + " | HiddenTP=" + hiddenTargetPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf SHORT #" + openDcaCount + " @ " + Close[0].ToString("F2"), Brushes.OrangeRed);
        }

        // ═══════════════════════════════════════════════════════════
        //  AGGRESSIVE LIMIT ENTRIES (Section 1b — BUY ASK / SELL BID)
        // ═══════════════════════════════════════════════════════════

        private void ExecuteBuyAskEntry()
        {
            if (!CanEnterTrade("BUY ASK", true, 1)) return;

            double limitPrice = GetCurrentAsk();
            if (limitPrice <= 0) limitPrice = Close[0] + TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            string signalName = " ";
            EnterLongLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = 1;
            lastEntryWallTime = (State == State.Realtime) ? DateTime.Now : Time[0];
            aggressiveLimitSubmitTime = DateTime.Now;

            double newQty = totalContracts + contracts;
            averageEntryPrice = 0;  // will be set on fill via OnOrderUpdate
            totalContracts = newQty;

            Print(Time[0] + " | BUY ASK #" + openDcaCount + " qty=" + contracts + " @ ask " + limitPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf BUY ASK #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteSellBidEntry()
        {
            if (!CanEnterTrade("SELL BID", true, -1)) return;

            double limitPrice = GetCurrentBid();
            if (limitPrice <= 0) limitPrice = Close[0] - TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            string signalName = " ";
            EnterShortLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = -1;
            lastEntryWallTime = (State == State.Realtime) ? DateTime.Now : Time[0];
            aggressiveLimitSubmitTime = DateTime.Now;

            double newQty = totalContracts + contracts;
            averageEntryPrice = 0;  // will be set on fill via OnOrderUpdate
            totalContracts = newQty;

            Print(Time[0] + " | SELL BID #" + openDcaCount + " qty=" + contracts + " @ bid " + limitPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf SELL BID #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.OrangeRed);
        }

        // ═══════════════════════════════════════════════════════════
        //  PASSIVE LIMIT ENTRIES (Section 1c — BUY LMT / SELL LMT)
        // ═══════════════════════════════════════════════════════════

        private void ExecuteLongLimitEntry()
        {
            if (!CanEnterTrade("BUY LMT", true, 1)) return;

            // Passive: buy at bid (sit in book)
            double limitPrice = GetCurrentBid();
            if (limitPrice <= 0) limitPrice = Close[0] - TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            string signalName = " ";
            EnterLongLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = 1;
            lastEntryWallTime = (State == State.Realtime) ? DateTime.Now : Time[0];

            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0
                ? (averageEntryPrice * totalContracts + limitPrice * contracts) / newQty
                : limitPrice;
            totalContracts = newQty;

            ArmHiddenStops();
            Print(Time[0] + " | BUY LMT #" + openDcaCount + " qty=" + contracts + " @ limit " + limitPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf BUY LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteShortLimitEntry()
        {
            if (!CanEnterTrade("SELL LMT", true, -1)) return;

            // Passive: sell at ask (sit in book)
            double limitPrice = GetCurrentAsk();
            if (limitPrice <= 0) limitPrice = Close[0] + TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            string signalName = " ";
            EnterShortLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = -1;
            lastEntryWallTime = (State == State.Realtime) ? DateTime.Now : Time[0];

            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0
                ? (averageEntryPrice * totalContracts + limitPrice * contracts) / newQty
                : limitPrice;
            totalContracts = newQty;

            ArmHiddenStops();
            Print(Time[0] + " | SELL LMT #" + openDcaCount + " qty=" + contracts + " @ limit " + limitPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf SELL LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.OrangeRed);
        }

        // ═══════════════════════════════════════════════════════════
        //  STOPS ARMING
        // ═══════════════════════════════════════════════════════════

        private void ArmHiddenStops()
        {
            double slOff = slPoints * NQ_TICKS_PER_POINT * TickSize;
            double tpOff = tpPoints * NQ_TICKS_PER_POINT * TickSize;

            if (openTradeDirection == 1)
            {
                hiddenStopPrice   = averageEntryPrice - slOff;
                hiddenTargetPrice = averageEntryPrice + tpOff;
            }
            else if (openTradeDirection == -1)
            {
                hiddenStopPrice   = averageEntryPrice + slOff;
                hiddenTargetPrice = averageEntryPrice - tpOff;
            }
            stopsArmed = true;

            Print(Time[0] + " | Stops armed: SL=" + hiddenStopPrice.ToString("F2")
                + " TP=" + hiddenTargetPrice.ToString("F2")
                + " (SL " + slPoints + " pts / TP " + tpPoints + " pts)");
        }

        // ═══════════════════════════════════════════════════════════
        //  EXIT METHODS
        // ═══════════════════════════════════════════════════════════

        private void ExecuteFlatten()
        {
            CancelPendingOrders();

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                ResetPositionState();
                Print(Time[0] + " | FLATTEN: already flat — state reset");
                UpdateDashboardStatus("\u25cf Already flat — state reset", Brushes.CornflowerBlue);
                return;
            }

            try
            {
                Account.Flatten(new[] { Instrument });
                Print(Time[0] + " | FLATTEN via Account.Flatten() — guaranteed close");
            }
            catch (Exception ex)
            {
                Print(Time[0] + " | Account.Flatten failed: " + ex.Message);
                var signals = activeEntrySignals.ToList();
                if (signals.Count > 0 && Position.MarketPosition == MarketPosition.Long)
                    foreach (string sig in signals) ExitLong("Close", sig);
                else if (signals.Count > 0 && Position.MarketPosition == MarketPosition.Short)
                    foreach (string sig in signals) ExitShort("Close", sig);
                else if (Position.MarketPosition != MarketPosition.Flat)
                {
                    if (Position.MarketPosition == MarketPosition.Long)
                        ExitLong("Close", "");
                    else
                        ExitShort("Close", "");
                }
            }

            stopsArmed  = false;
            pendingExit = true;
            pendingReverseLong = false; pendingReverseShort = false;
            pendingReverseLongLmt = false; pendingReverseShortLmt = false;
            pendingReverseLongAsk = false; pendingReverseShortBid = false;
            Print(Time[0] + " | Flatten submitted (pendingExit=true)");
        }

        private void ExecuteCloseTrade()
        {
            bool hadPending = CancelPendingOrders();

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (hadPending)
                {
                    ResetPositionState();
                    Print(Time[0] + " | Close Trade: cancelled pending limit order(s), reset state");
                    UpdateDashboardStatus("\u25cf Cancelled pending order", Brushes.Yellow);
                }
                else
                {
                    Print(Time[0] + " | Close Trade: already flat, nothing to do");
                    UpdateDashboardStatus("\u25cf Already flat", Brushes.CornflowerBlue);
                }
                return;
            }

            var signals = activeEntrySignals.ToList();
            if (signals.Count > 0)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    foreach (string sig in signals) ExitLong("Close", sig);
                else if (Position.MarketPosition == MarketPosition.Short)
                    foreach (string sig in signals) ExitShort("Close", sig);
            }
            else
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("Close", "");
                else
                    ExitShort("Close", "");
            }

            stopsArmed  = false;
            pendingExit = true;
            Print(Time[0] + " | Close Trade submitted (managed exits, pendingExit=true)");
            UpdateDashboardStatus("\u25cf Closing trade...", Brushes.Yellow);
        }

        private bool CancelPendingOrders()
        {
            bool cancelled = false;
            try
            {
                var working = new List<Order>();
                foreach (Order order in Account.Orders)
                {
                    if (order.Instrument == Instrument
                        && (order.OrderState == OrderState.Working
                            || order.OrderState == OrderState.Accepted
                            || order.OrderState == OrderState.Submitted))
                    {
                        working.Add(order);
                    }
                }
                if (working.Count > 0)
                {
                    Account.Cancel(working.ToArray());
                    cancelled = true;
                    Print(Time[0] + " | Cancelled " + working.Count + " pending order(s)");
                }
            }
            catch (Exception ex)
            {
                Print(Time[0] + " | CancelPendingOrders error: " + ex.Message);
            }
            return cancelled;
        }

        private void ExecuteCloseOne()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                UpdateDashboardStatus("\u25cf Already flat", Brushes.CornflowerBlue);
                return;
            }
            if (Position.Quantity <= 1) { ExecuteCloseTrade(); return; }

            string sig = activeEntrySignals.Count > 0 ? activeEntrySignals[activeEntrySignals.Count - 1] : "";
            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(1, "Close", sig);
            else
                ExitShort(1, "Close", sig);

            if (activeEntrySignals.Count > 0)
                activeEntrySignals.RemoveAt(activeEntrySignals.Count - 1);
            openDcaCount = Math.Max(0, openDcaCount - 1);
            totalContracts = Math.Max(0, totalContracts - 1);

            Print(Time[0] + " | CLOSE 1 — remaining qty\u2248" + (Position.Quantity - 1) + " DCA=" + openDcaCount);
            UpdateDashboardStatus("\u25cf Closed 1 contract", Brushes.Yellow);
        }

        private void ExecuteJumpSL()
        {
            if (!stopsArmed || Position.MarketPosition == MarketPosition.Flat)
            {
                UpdateDashboardStatus("\u26a0 No active SL to jump", Brushes.Orange);
                return;
            }

            double price = Close[0];
            double pct = jumpSlPercent / 100.0;

            if (openTradeDirection == 1)
            {
                double gap = price - hiddenStopPrice;
                if (gap <= 1.0 * NQ_TICKS_PER_POINT * TickSize) { UpdateDashboardStatus("\u26a0 SL already at minimum", Brushes.Orange); return; }
                double newSl = hiddenStopPrice + gap * pct;
                double minSl = price - 1.0 * NQ_TICKS_PER_POINT * TickSize;
                if (newSl > minSl) newSl = minSl;
                hiddenStopPrice = Math.Round(newSl / TickSize) * TickSize;
                slPoints = (int)Math.Round((price - hiddenStopPrice) / (NQ_TICKS_PER_POINT * TickSize));
            }
            else if (openTradeDirection == -1)
            {
                double gap = hiddenStopPrice - price;
                if (gap <= 1.0 * NQ_TICKS_PER_POINT * TickSize) { UpdateDashboardStatus("\u26a0 SL already at minimum", Brushes.Orange); return; }
                double newSl = hiddenStopPrice - gap * pct;
                double maxSl = price + 1.0 * NQ_TICKS_PER_POINT * TickSize;
                if (newSl < maxSl) newSl = maxSl;
                hiddenStopPrice = Math.Round(newSl / TickSize) * TickSize;
                slPoints = (int)Math.Round((hiddenStopPrice - price) / (NQ_TICKS_PER_POINT * TickSize));
            }

            slPoints = Math.Max(1, slPoints);
            Print(Time[0] + " | JUMP SL: new SL=" + hiddenStopPrice.ToString("F2") + " (" + slPoints + " pts)");
            if (ChartControl != null)
                ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());
            UpdateDashboardStatus("\u25cf SL jumped to " + hiddenStopPrice.ToString("F2") + " (" + slPoints + "pt)", Brushes.Yellow);
        }

        // Section 2c: Emergency Kill
        private void ExecuteEmergencyKill()
        {
            emergencyKillActive = true;
            dailyLimitHit = true;
            CancelPendingOrders();

            if (Position.MarketPosition != MarketPosition.Flat)
            {
                try { Account.Flatten(new[] { Instrument }); }
                catch (Exception ex) { Print("Emergency kill Account.Flatten failed: " + ex.Message); }
            }

            stopsArmed = false;
            pendingExit = true;
            pendingReverseLong = false; pendingReverseShort = false;
            pendingReverseLongLmt = false; pendingReverseShortLmt = false;
            pendingReverseLongAsk = false; pendingReverseShortBid = false;
            Print(Time[0] + " | EMERGENCY KILL — all trading halted until strategy restart");
            UpdateDashboardStatus("\u26a0 EMERGENCY KILL — trading halted", Brushes.OrangeRed);
        }

        private void ResetPositionState()
        {
            stopsArmed         = false;
            pendingExit        = false;
            pendingExitTicks   = 0;
            flatSyncGraceTicks = 0;
            openTradeDirection = 0;
            openDcaCount       = 0;
            totalContracts     = 0;
            averageEntryPrice  = 0;
            hiddenStopPrice    = 0;
            hiddenTargetPrice  = 0;
            aggressiveLimitSubmitTime = DateTime.MinValue;
            activeEntrySignals.Clear();

            // Reset adaptive trail state
            trailPrice         = 0;
            trailActive        = false;
            trailTrendScore    = 0;
            trailMaxProfitPts  = 0;
            trailTierName      = "";

            // Reset trap detector state
            trapScore          = 0;
            trapDetected       = false;
            trapBarsInTrade    = 0;

            // Reset SL/TP to defaults for next trade (Section 0a)
            if (defaultSlPoints > 0) slPoints = defaultSlPoints;
            if (defaultTpPoints > 0) tpPoints = defaultTpPoints;
            contracts = defaultContracts;
            jumpSlPercent = defaultJumpSlPercent;
            if (ChartControl != null)
                ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());

            RemoveDrawObject("hiddenSL");
            RemoveDrawObject("hiddenTP");
            RemoveDrawObject("avgEntryLine");
            RemoveDrawObject("slLabel");
            RemoveDrawObject("tpLabel");
            RemoveDrawObject("dcaLevelAbove");
            RemoveDrawObject("dcaLabelAbove");
            RemoveDrawObject("dcaLevelBelow");
            RemoveDrawObject("dcaLabelBelow");
            RemoveDrawObject("adaptiveTrail");
            RemoveDrawObject("trailLabel");
        }

        // ═══════════════════════════════════════════════════════════
        //  POSITION STATE SYNC
        // ═══════════════════════════════════════════════════════════

        protected override void OnPositionUpdate(Position position, double averagePrice,
            int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
            {
                Print("OnPositionUpdate: position is FLAT — resetting all state");
                ResetPositionState();
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  DAILY P&L TRACKING
        // ═══════════════════════════════════════════════════════════

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution.Order == null) return;

            dailyRealizedPnL = 0;
            foreach (Trade t in SystemPerformance.AllTrades)
                if (t.Entry.Time.Date == sessionDate)
                    dailyRealizedPnL += t.ProfitCurrency;
            dailyRealizedPnL -= pnlBaselineOffset;
            sessionRealizedPnL = dailyRealizedPnL;

            if (dailyRealizedPnL <= -maxDailyLossDollars && !dailyLimitHit)
            {
                dailyLimitHit = true;
                pendingLimitFlatten = true;
                Print("*** DAILY LOSS LIMIT HIT: " + dailyRealizedPnL.ToString("C0") + " — flatten deferred to OnBarUpdate ***");
            }
            if (dailyRealizedPnL >= maxDailyProfitDollars && !dailyProfitHit)
            {
                dailyProfitHit = true;
                pendingLimitFlatten = true;
                Print("*** DAILY PROFIT TARGET HIT: " + dailyRealizedPnL.ToString("C0") + " — flatten deferred to OnBarUpdate ***");
            }
        }

        #endregion

        #region VWAP and Signals

        // ═══════════════════════════════════════════════════════════
        //  MANUAL VWAP
        // ═══════════════════════════════════════════════════════════

        private void ResetVwap()
        {
            vwapCumTPV  = 0;
            vwapCumVol  = 0;
            vwapValue   = 0;
            prevBarVwap = 0;
            currBarTPV  = 0;
            currBarVol  = 0;
        }

        private void UpdateVwap()
        {
            double tp  = (High[0] + Low[0] + Close[0]) / 3.0;
            double vol = Volume[0];
            if (vol <= 0) return;

            if (IsFirstTickOfBar && CurrentBar > 0)
            {
                vwapCumTPV += currBarTPV;
                vwapCumVol += currBarVol;
                prevBarVwap = vwapValue;
                currBarTPV  = 0;
                currBarVol  = 0;
            }

            currBarTPV = tp * vol;
            currBarVol = vol;

            double totalTPV = vwapCumTPV + currBarTPV;
            double totalVol = vwapCumVol + currBarVol;
            vwapValue = totalVol > 0 ? totalTPV / totalVol : Close[0];
        }

        // ═══════════════════════════════════════════════════════════
        //  SIGNAL CALCULATION
        // ═══════════════════════════════════════════════════════════

        private void CalculateSignals()
        {
            if (autoStrategy == 4)
            {
                double bestBull = 0, bestBear = 0, bestScore = 0;
                bestAutoStrategy = 0;

                for (int s = 0; s < 4; s++)
                {
                    lastBullConfidence = 0;
                    lastBearConfidence = 0;
                    switch (s)
                    {
                        case 0: CalcMomentumVwap();           break;
                        case 1: CalcKeyLevelBreakout();       break;
                        case 2: CalcLiquiditySweepReversal(); break;
                        case 3: CalcOpeningRangeBreakout();   break;
                    }
                    ApplySmartFilters();
                    double score = Math.Max(lastBullConfidence, lastBearConfidence);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestBull  = lastBullConfidence;
                        bestBear  = lastBearConfidence;
                        bestAutoStrategy = s;
                    }
                }
                lastBullConfidence = bestBull;
                lastBearConfidence = bestBear;
                return;
            }

            lastBullConfidence = 0;
            lastBearConfidence = 0;

            switch (autoStrategy)
            {
                case 0: CalcMomentumVwap();           break;
                case 1: CalcKeyLevelBreakout();       break;
                case 2: CalcLiquiditySweepReversal(); break;
                case 3: CalcOpeningRangeBreakout();   break;
            }

            ApplySmartFilters();
        }

        private void ApplySmartFilters()
        {
            if (CurrentBar < emaPeriodSlow + 5) return;

            lastRawBull = lastBullConfidence;
            lastRawBear = lastBearConfidence;

            double atr = indAtr[0];
            if (atr <= 0) return;

            // Filter 1: Volume Confirmation
            double volSum = 0;
            int volLookback = Math.Min(20, CurrentBar - 1);
            for (int i = 1; i <= volLookback; i++)
                volSum += Volume[i];
            double volAvg = volLookback > 0 ? volSum / volLookback : Volume[0];
            double volRatio = volAvg > 0 ? Volume[0] / volAvg : 1.0;

            if (volRatio < 0.6)
            { lastBullConfidence *= 0.65; lastBearConfidence *= 0.65; }
            else if (volRatio < 0.85)
            { lastBullConfidence *= 0.85; lastBearConfidence *= 0.85; }
            else if (volRatio > 1.5)
            { lastBullConfidence *= 1.10; lastBearConfidence *= 1.10; }

            // Filter 2: Bull/Bear Conflict
            double maxConf = Math.Max(lastBullConfidence, lastBearConfidence);
            double minConf = Math.Min(lastBullConfidence, lastBearConfidence);
            if (maxConf > 30 && minConf > 0)
            {
                double conflictRatio = minConf / maxConf;
                if (conflictRatio > 0.65)
                {
                    double penalty = 0.55 + (1.0 - conflictRatio) * 1.25;
                    penalty = Math.Min(1.0, Math.Max(0.5, penalty));
                    lastBullConfidence *= penalty;
                    lastBearConfidence *= penalty;
                }
            }

            // Filter 3: Momentum Exhaustion
            if (CurrentBar > 20)
            {
                double recentHigh = MAX(High, 20)[0];
                double recentLow  = MIN(Low,  20)[0];
                double recentRange = recentHigh - recentLow;
                double price = Close[0];

                double bullExhaustion = recentRange > 0 ? (price - recentLow) / recentRange : 0.5;
                double bearExhaustion = recentRange > 0 ? (recentHigh - price) / recentRange : 0.5;

                double moveFromLow  = (price - recentLow)  / atr;
                double moveFromHigh = (recentHigh - price)  / atr;

                if (bullExhaustion > 0.82 && moveFromLow > 2.0)
                {
                    double exhaust = Math.Min(0.45, (bullExhaustion - 0.82) * 2.5);
                    lastBullConfidence *= (1.0 - exhaust);
                }
                if (bearExhaustion > 0.82 && moveFromHigh > 2.0)
                {
                    double exhaust = Math.Min(0.45, (bearExhaustion - 0.82) * 2.5);
                    lastBearConfidence *= (1.0 - exhaust);
                }
            }

            // Filter 4: Bar Quality (Wick Rejection)
            double bodySize = Math.Abs(Close[0] - Open[0]);
            double barRange = High[0] - Low[0];
            if (barRange > 0)
            {
                double upperWick = High[0] - Math.Max(Close[0], Open[0]);
                double lowerWick = Math.Min(Close[0], Open[0]) - Low[0];
                double upperWickPct = upperWick / barRange;
                double lowerWickPct = lowerWick / barRange;

                if (upperWickPct > 0.45)
                    lastBullConfidence *= (1.0 - (upperWickPct - 0.45) * 1.5);
                if (lowerWickPct > 0.45)
                    lastBearConfidence *= (1.0 - (lowerWickPct - 0.45) * 1.5);
            }

            // Section 9c: Filter 5 — Night session penalty
            int ct = ToTime(Time[0]);
            if (ct >= 180000 || ct < 80000)
            {
                lastBullConfidence *= 0.70;
                lastBearConfidence *= 0.70;
            }

            lastBullConfidence = Math.Max(0, Math.Min(120, lastBullConfidence));
            lastBearConfidence = Math.Max(0, Math.Min(120, lastBearConfidence));
        }

        // ─── Strategy 0: Momentum + VWAP ─────────────────────────
        private void CalcMomentumVwap()
        {
            if (CurrentBar < emaPeriodSlow + 2) return;
            if (ToTime(Time[0]) >= 93000 && ToTime(Time[0]) <= 93500) return;

            double emaF  = indEmaFast[0];
            double emaS  = indEmaSlow[0];
            double rsi   = indRsi[0];
            double price = Close[0];
            double prev  = Close[1];

            double barRange = High[0] - Low[0];
            bool bullishClose = barRange > 0 && (price - Low[0]) / barRange > 0.65;
            bool bearishClose = barRange > 0 && (High[0] - price) / barRange > 0.65;

            bool longEma       = emaF > emaS;
            bool longVwapCross = price > vwapValue && prev <= vwapValue;
            bool longVwapAbove = price > vwapValue;
            bool longRsi       = rsi > 50 && rsi < 75;
            bool longMom       = Close[0] > High[1];

            if (longEma)       lastBullConfidence += 30;
            if (longVwapCross) lastBullConfidence += 35;
            else if (longVwapAbove) lastBullConfidence += 20;
            if (longRsi)       lastBullConfidence += 20;
            if (longMom)       lastBullConfidence += 15;
            if (longVwapCross && bullishClose) lastBullConfidence += 10;

            bool shortEma       = emaF < emaS;
            bool shortVwapCross = price < vwapValue && prev >= vwapValue;
            bool shortVwapBelow = price < vwapValue;
            bool shortRsi       = rsi < 50 && rsi > 25;
            bool shortMom       = Close[0] < Low[1];

            if (shortEma)       lastBearConfidence += 30;
            if (shortVwapCross) lastBearConfidence += 35;
            else if (shortVwapBelow) lastBearConfidence += 20;
            if (shortRsi)       lastBearConfidence += 20;
            if (shortMom)       lastBearConfidence += 15;
            if (shortVwapCross && bearishClose) lastBearConfidence += 10;
        }

        // ─── Strategy 1: Key Level Breakout ──────────────────────
        private void CalcKeyLevelBreakout()
        {
            if (CurrentBar < 22) return;
            if (ToTime(Time[0]) >= 93000 && ToTime(Time[0]) <= 93500) return;
            if (ToTime(Time[0]) >= 120000 && ToTime(Time[0]) <= 130000) return;

            double priorHigh = MAX(High, 20)[1];
            double priorLow  = MIN(Low,  20)[1];
            double atr       = indAtr[0];
            double price     = Close[0];
            double prevClose = Close[1];

            double barRange = High[0] - Low[0];
            bool bullConviction = barRange > 0 && (price - Low[0]) / barRange > 0.60;
            bool bearConviction = barRange > 0 && (High[0] - price) / barRange > 0.60;

            bool longBreakCross = price > priorHigh && prevClose <= priorHigh;
            bool longBreakAbove = price > priorHigh;
            bool longAtrConf    = atr > 0 && (price - priorHigh) > atr * 0.15;
            bool longEmaAlign   = indEmaFast[0] > indEmaSlow[0];

            if (longBreakCross && bullConviction) lastBullConfidence += 50;
            else if (longBreakCross) lastBullConfidence += 25;
            else if (longBreakAbove) lastBullConfidence += 25;
            if (longAtrConf)  lastBullConfidence += 30;
            if (longEmaAlign) lastBullConfidence += 20;

            bool shortBreakCross = price < priorLow && prevClose >= priorLow;
            bool shortBreakBelow = price < priorLow;
            bool shortAtrConf    = atr > 0 && (priorLow - price) > atr * 0.15;
            bool shortEmaAlign   = indEmaFast[0] < indEmaSlow[0];

            if (shortBreakCross && bearConviction) lastBearConfidence += 50;
            else if (shortBreakCross) lastBearConfidence += 25;
            else if (shortBreakBelow) lastBearConfidence += 25;
            if (shortAtrConf)  lastBearConfidence += 30;
            if (shortEmaAlign) lastBearConfidence += 20;
        }

        // ─── Strategy 2: Liquidity Sweep Reversal ────────────────
        private void CalcLiquiditySweepReversal()
        {
            if (CurrentBar < 12) return;

            double swingHigh = MAX(High, 10)[1];
            double swingLow  = MIN(Low,  10)[1];
            double atr       = indAtr[0];
            double price     = Close[0];
            double prevLow   = Low[1];
            double prevHigh  = High[1];

            double volSum = 0;
            int vlb = Math.Min(20, CurrentBar - 1);
            for (int i = 1; i <= vlb; i++) volSum += Volume[i];
            double volAvg = vlb > 0 ? volSum / vlb : Volume[0];
            bool volSpike = volAvg > 0 && Volume[0] > volAvg * 1.2;

            bool longSweep     = prevLow < swingLow && price > swingLow;
            bool longRecovery  = price > swingLow && MIN(Low, 5)[1] < swingLow;
            bool longRsiConf   = indRsi[0] < 40;
            bool longSnapback  = atr > 0 && (price - prevLow) > atr * 0.3;

            if (longSweep)          lastBullConfidence += 45;
            else if (longRecovery)  lastBullConfidence += 25;
            if (longRsiConf)        lastBullConfidence += 30;
            if (longSnapback)       lastBullConfidence += 25;
            if ((longSweep || longRecovery) && volSpike) lastBullConfidence += 10;

            bool shortSweep     = prevHigh > swingHigh && price < swingHigh;
            bool shortRecovery  = price < swingHigh && MAX(High, 5)[1] > swingHigh;
            bool shortRsiConf   = indRsi[0] > 60;
            bool shortSnapback  = atr > 0 && (prevHigh - price) > atr * 0.3;

            if (shortSweep)          lastBearConfidence += 45;
            else if (shortRecovery)  lastBearConfidence += 25;
            if (shortRsiConf)        lastBearConfidence += 30;
            if (shortSnapback)       lastBearConfidence += 25;
            if ((shortSweep || shortRecovery) && volSpike) lastBearConfidence += 10;
        }

        // ─── Strategy 3: Opening Range Breakout ──────────────────
        private void CalcOpeningRangeBreakout()
        {
            if (!orbSet) return;
            if (ToTime(Time[0]) < 94500)  return;
            if (ToTime(Time[0]) > 113000) return;

            double price     = Close[0];
            double prevClose = Close[1];
            bool   emaAlign  = indEmaFast[0] > indEmaSlow[0];
            double atr       = indAtr[0];

            double orbRange = orbHigh - orbLow;
            double orbQuality = (atr > 0 && orbRange > 0) ? Math.Min(1.0, atr / orbRange) : 1.0;

            double barRange = High[0] - Low[0];
            bool bullConviction = barRange > 0 && (price - Low[0]) / barRange > 0.60;
            bool bearConviction = barRange > 0 && (High[0] - price) / barRange > 0.60;

            bool longBreakCross = price > orbHigh && prevClose <= orbHigh;
            bool longBreakAbove = price > orbHigh;

            if (longBreakCross && bullConviction) lastBullConfidence += 55;
            else if (longBreakCross) lastBullConfidence += 30;
            else if (longBreakAbove) lastBullConfidence += 30;
            if (emaAlign) lastBullConfidence += 25;
            if (atr > 0)  lastBullConfidence += (int)(20 * orbQuality);

            bool shortBreakCross = price < orbLow && prevClose >= orbLow;
            bool shortBreakBelow = price < orbLow;

            if (shortBreakCross && bearConviction) lastBearConfidence += 55;
            else if (shortBreakCross) lastBearConfidence += 30;
            else if (shortBreakBelow) lastBearConfidence += 30;
            if (!emaAlign) lastBearConfidence += 25;
            if (atr > 0)   lastBearConfidence += (int)(20 * orbQuality);
        }

        // ═══════════════════════════════════════════════════════════
        //  ORB LEVEL TRACKING
        // ═══════════════════════════════════════════════════════════

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

        // ═══════════════════════════════════════════════════════════
        //  CHART ANNOTATIONS
        // ═══════════════════════════════════════════════════════════

        private void DrawChartAnnotations()
        {
            if (!stopsArmed || averageEntryPrice == 0) return;
            Draw.HorizontalLine(this, "hiddenSL",     false, hiddenStopPrice,   Brushes.OrangeRed,  DashStyleHelper.DashDotDot, 2);
            Draw.HorizontalLine(this, "hiddenTP",     false, hiddenTargetPrice,  Brushes.LimeGreen,  DashStyleHelper.DashDotDot, 2);
            Draw.HorizontalLine(this, "avgEntryLine", false, averageEntryPrice,  Brushes.DodgerBlue, DashStyleHelper.Dot,        1);

            int slTk = slPoints * 4;
            int tpTk = tpPoints * 4;
            double slDol = slPoints * NQ_DOLLARS_PER_POINT * totalContracts;
            double tpDol = tpPoints * NQ_DOLLARS_PER_POINT * totalContracts;

            Draw.Text(this, "slLabel",
                "SL " + hiddenStopPrice.ToString("F2") + "  (" + slPoints + "pt | " + slTk + "tk | " + slDol.ToString("C0") + ")",
                0, hiddenStopPrice + (openTradeDirection == 1 ? -2 * TickSize : 2 * TickSize), Brushes.OrangeRed);
            Draw.Text(this, "tpLabel",
                "TP " + hiddenTargetPrice.ToString("F2") + "  (" + tpPoints + "pt | " + tpTk + "tk | " + tpDol.ToString("C0") + ")",
                0, hiddenTargetPrice + (openTradeDirection == 1 ? 2 * TickSize : -2 * TickSize), Brushes.LimeGreen);

            if (trailEnabled && trailActive && trailPrice > 0)
            {
                Draw.HorizontalLine(this, "adaptiveTrail", false, trailPrice, Brushes.Magenta, DashStyleHelper.DashDot, 2);
                string regimeStr = trailTrendScore > 0.6 ? "Trending" : (trailTrendScore < 0.35 ? "Choppy" : "Mixed");
                double trailDistPts = openTradeDirection == 1
                    ? (Close[0] - trailPrice) / (TickSize * NQ_TICKS_PER_POINT)
                    : openTradeDirection == -1
                        ? (trailPrice - Close[0]) / (TickSize * NQ_TICKS_PER_POINT) : 0;
                Draw.Text(this, "trailLabel",
                    "Trail " + trailPrice.ToString("F2") + "  (" + trailDistPts.ToString("F1") + "pt | " + regimeStr + " " + (trailTrendScore * 100).ToString("F0") + "%)",
                    0, trailPrice + (openTradeDirection == 1 ? -6 * TickSize : 6 * TickSize), Brushes.Magenta);
            }
            else
            {
                RemoveDrawObject("adaptiveTrail");
                RemoveDrawObject("trailLabel");
            }
        }

        private void DrawDcaLevels()
        {
            if (!stopsArmed || !dcaEnabled || openDcaCount >= dcaMaxPositions || averageEntryPrice == 0)
            {
                RemoveDrawObject("dcaLevelAbove"); RemoveDrawObject("dcaLabelAbove");
                RemoveDrawObject("dcaLevelBelow"); RemoveDrawObject("dcaLabelBelow");
                return;
            }

            double distOffset = dcaDistancePoints > 0 ? dcaDistancePoints * NQ_TICKS_PER_POINT * TickSize : 0;
            int remaining = dcaMaxPositions - openDcaCount;
            string dcaInfo = "DCA #" + (openDcaCount + 1) + " (" + remaining + " left, qty " + contracts + ")";

            if (openTradeDirection == 1)
            {
                double dcaBelow = averageEntryPrice - distOffset;
                Draw.HorizontalLine(this, "dcaLevelBelow", false, dcaBelow, Brushes.DeepSkyBlue, DashStyleHelper.Dash, 1);
                Draw.Text(this, "dcaLabelBelow", dcaInfo, 0, dcaBelow - 4 * TickSize, Brushes.DeepSkyBlue);
                RemoveDrawObject("dcaLevelAbove"); RemoveDrawObject("dcaLabelAbove");
            }
            else if (openTradeDirection == -1)
            {
                double dcaAbove = averageEntryPrice + distOffset;
                Draw.HorizontalLine(this, "dcaLevelAbove", false, dcaAbove, Brushes.DeepSkyBlue, DashStyleHelper.Dash, 1);
                Draw.Text(this, "dcaLabelAbove", dcaInfo, 0, dcaAbove + 4 * TickSize, Brushes.DeepSkyBlue);
                RemoveDrawObject("dcaLevelBelow"); RemoveDrawObject("dcaLabelBelow");
            }
        }

        private void DrawVwapLine()
        {
            if (!showVwap || CurrentBar < BarsRequiredToTrade + 1 || vwapValue == 0) return;

            if (IsFirstTickOfBar || CurrentBar == BarsRequiredToTrade + 1)
            {
                if (prevBarVwap > 0)
                    Draw.Line(this, "vwap_" + CurrentBar, false,
                        1, prevBarVwap, 0, vwapValue,
                        Brushes.Yellow, DashStyleHelper.Solid, 2);
            }
            else
            {
                if (prevBarVwap > 0)
                    Draw.Line(this, "vwap_" + CurrentBar, false,
                        1, prevBarVwap, 0, vwapValue,
                        Brushes.Yellow, DashStyleHelper.Solid, 2);
            }
        }

        private void DrawKeyLevels()
        {
            if (!showKeyLevels || CurrentBar < 22) return;

            double priorHigh = MAX(High, 20)[1];
            double priorLow  = MIN(Low,  20)[1];

            Draw.HorizontalLine(this, "keyResist",  false, priorHigh, Brushes.Cyan, DashStyleHelper.Dash, 1);
            Draw.HorizontalLine(this, "keySupport", false, priorLow,  Brushes.Cyan, DashStyleHelper.Dash, 1);
            Draw.Text(this, "keyResistLbl", "Key Resist " + priorHigh.ToString("F2"), 0, priorHigh + 2 * TickSize, Brushes.Cyan);
            Draw.Text(this, "keySupportLbl", "Key Support " + priorLow.ToString("F2"), 0, priorLow - 2 * TickSize, Brushes.Cyan);
        }

        private void DrawSweepSignals()
        {
            if (!showSweepSignals || CurrentBar < 12) return;

            double swingHigh = MAX(High, 10)[1];
            double swingLow  = MIN(Low,  10)[1];

            Draw.HorizontalLine(this, "swingHi", false, swingHigh, Brushes.Magenta, DashStyleHelper.Dot, 1);
            Draw.HorizontalLine(this, "swingLo", false, swingLow,  Brushes.Magenta, DashStyleHelper.Dot, 1);

            double prevLow  = Low[1];
            double prevHigh = High[1];
            double price    = Close[0];

            if (prevLow < swingLow && price > swingLow)
            {
                Draw.ArrowUp(this, "sweepUp_" + CurrentBar, false, 0, Low[0] - 8 * TickSize, Brushes.LimeGreen);
                Draw.Text(this, "sweepUpTxt_" + CurrentBar, "Sweep\u2191", 0, Low[0] - 16 * TickSize, Brushes.LimeGreen);
            }
            if (prevHigh > swingHigh && price < swingHigh)
            {
                Draw.ArrowDown(this, "sweepDn_" + CurrentBar, false, 0, High[0] + 8 * TickSize, Brushes.OrangeRed);
                Draw.Text(this, "sweepDnTxt_" + CurrentBar, "Sweep\u2193", 0, High[0] + 16 * TickSize, Brushes.OrangeRed);
            }
        }

        private void ResetDailyTracking()
        {
            sessionDate       = Time[0].Date;
            dailyRealizedPnL  = 0;
            sessionRealizedPnL = 0;
            pnlBaselineOffset = 0;
            dailyLimitHit     = false;
            dailyProfitHit    = false;
            dailyTradeCount   = 0;
            flattenFired      = false;
            emergencyKillActive = false;
            orbSet            = false;
            orbHigh           = 0;
            orbLow            = 0;
            Print("Session reset: " + sessionDate.ToShortDateString()
                + " MaxLoss=$" + maxDailyLossDollars + " ProfitTarget=$" + maxDailyProfitDollars
                + " MaxTrades=" + (maxTradesPerDay > 0 ? maxTradesPerDay.ToString() : "\u221e"));
        }

        #endregion

        #region Dashboard

        // ═══════════════════════════════════════════════════════════
        //  DASHBOARD UI
        // ═══════════════════════════════════════════════════════════

        private void BuildDashboard()
        {
            if (ChartControl == null || dashboardAttached) return;
            dashboardAttached = true;
            dashBuildRetryCount++;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
              try
              {
                if (!dashboardAttached) return;

                dashboardPanel = new Grid();
                dashTranslate  = new TranslateTransform(0, 0);
                dashboardPanel.RenderTransform = dashTranslate;

                dashBorder = new Border
                {
                    Background      = new SolidColorBrush(Color.FromArgb(235, 18, 20, 28)),
                    CornerRadius    = new CornerRadius(6),
                    Padding         = new Thickness(0),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(55, 60, 75)),
                    BorderThickness = new Thickness(1),
                    Width           = 260
                };

                var outerStack = new StackPanel();

                // Title bar
                dashTitleBar = new Border
                {
                    Background   = new SolidColorBrush(Color.FromRgb(30, 35, 48)),
                    CornerRadius = new CornerRadius(5, 5, 0, 0),
                    Padding      = new Thickness(10, 6, 10, 6),
                    Cursor       = Cursors.SizeAll
                };
                dashTitleBar.Child = new TextBlock
                {
                    Text = "\u2630  NQ  Mm-ATM  v1",
                    Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                outerStack.Children.Add(dashTitleBar);

                dashScroll = new ScrollViewer
                {
                    MaxHeight = 480,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };

                var stack = new StackPanel { Margin = new Thickness(10, 4, 10, 4) };

                // Mode toggle
                var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnModeManual = MakeToggleButton("MANUAL", !autoMode, (s, e) => { autoMode = false; UpdateModeButtons(); });
                btnModeAuto   = MakeToggleButton("AUTO",    autoMode, (s, e) => { autoMode = true;  UpdateModeButtons(); });
                modeRow.Children.Add(btnModeManual);
                modeRow.Children.Add(btnModeAuto);
                stack.Children.Add(modeRow);

                // Strategy selector
                stratPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                stratPanel.Children.Add(MakeLabel("Auto Strategy:", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left));
                var stratRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var btnStratPrev = MakeSmallButton("\u25c4", (s, e) => { autoStrategy = (autoStrategy + 4) % 5; UpdateStratLabel(); });
                lblStratName = MakeLabel(StrategyNames[autoStrategy], Brushes.White, 11, FontWeights.SemiBold, HorizontalAlignment.Center);
                lblStratName.Width = 140;
                lblStratName.TextAlignment = TextAlignment.Center;
                var btnStratNext2 = MakeSmallButton("\u25ba", (s, e) => { autoStrategy = (autoStrategy + 1) % 5; UpdateStratLabel(); });
                stratRow.Children.Add(btnStratPrev);
                stratRow.Children.Add(lblStratName);
                stratRow.Children.Add(btnStratNext2);
                stratPanel.Children.Add(stratRow);
                stack.Children.Add(stratPanel);

                stack.Children.Add(MakeSeparator());

                // Qty row
                var qtyRow = MakeAdjustRow("Qty:", contracts.ToString(),
                    (s, e) => { contracts = Math.Max(1, contracts - 1); UpdateAdjustLabels(); },
                    (s, e) => { contracts = Math.Min(10, contracts + 1); UpdateAdjustLabels(); },
                    "/ max " + dcaMaxPositions);
                lblQtyVal = (TextBlock)((StackPanel)qtyRow).Children[3];
                stack.Children.Add(qtyRow);

                // SL row
                var slRow = MakeAdjustRow("SL:", slPoints + "pt | " + (slPoints * 4) + "tk | $" + (slPoints * 20),
                    (s, e) => { slPoints = Math.Max(1, slPoints - slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    (s, e) => { slPoints = Math.Min(500, slPoints + slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    "");
                lblSlVal = (TextBlock)((StackPanel)slRow).Children[3];
                stack.Children.Add(slRow);

                // TP row
                var tpRow = MakeAdjustRow("TP:", tpPoints + "pt | " + (tpPoints * 4) + "tk | $" + (tpPoints * 20),
                    (s, e) => { tpPoints = Math.Max(1, tpPoints - slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    (s, e) => { tpPoints = Math.Min(500, tpPoints + slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    "");
                lblTpVal = (TextBlock)((StackPanel)tpRow).Children[3];
                stack.Children.Add(tpRow);

                stack.Children.Add(MakeSeparator());

                // Jump SL row
                var jumpRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnJumpSL = new Button
                {
                    Content = "\u21e5 JUMP SL", Width = 90, Height = 26,
                    Background = new SolidColorBrush(Color.FromRgb(120, 80, 20)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                jumpRow.Children.Add(btnJumpSL);
                jumpRow.Children.Add(MakeSmallButton("\u2212", (s, e) => { jumpSlPercent = Math.Max(10, jumpSlPercent - 10); UpdateJumpLabel(); }));
                lblJumpPct = MakeLabel(jumpSlPercent + "%", Brushes.White, 10, FontWeights.Bold, HorizontalAlignment.Center);
                lblJumpPct.Width = 36;
                lblJumpPct.VerticalAlignment = VerticalAlignment.Center;
                lblJumpPct.TextAlignment = TextAlignment.Center;
                jumpRow.Children.Add(lblJumpPct);
                jumpRow.Children.Add(MakeSmallButton("+", (s, e) => { jumpSlPercent = Math.Min(95, jumpSlPercent + 10); UpdateJumpLabel(); }));
                stack.Children.Add(jumpRow);

                stack.Children.Add(MakeSeparator());

                // === Row 1: BUY MKT / SELL MKT ===
                var btnRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyMkt = new Button
                {
                    Content = "\u25b2 BUY MKT", Width = 120, Height = 36,
                    Background = new SolidColorBrush(Color.FromRgb(22, 140, 100)),
                    Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellMkt = new Button
                {
                    Content = "\u25bc SELL MKT", Width = 120, Height = 36,
                    Background = new SolidColorBrush(Color.FromRgb(190, 45, 45)),
                    Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0)
                };
                btnRow1.Children.Add(btnBuyMkt);
                btnRow1.Children.Add(btnSellMkt);
                stack.Children.Add(btnRow1);

                // === Row 2: BUY ASK / SELL BID (Section 1b) ===
                var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyAsk = new Button
                {
                    Content = "\u25b3 BUY ASK", Width = 120, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(18, 120, 90)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellBid = new Button
                {
                    Content = "\u25bd SELL BID", Width = 120, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(160, 38, 38)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0)
                };
                btnRow2.Children.Add(btnBuyAsk);
                btnRow2.Children.Add(btnSellBid);
                stack.Children.Add(btnRow2);

                // === Row 3: BUY LMT / SELL LMT ===
                var lmtRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyLmt = new Button
                {
                    Content = "\u25b3 BUY LMT", Width = 120, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(18, 105, 78)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellLmt = new Button
                {
                    Content = "\u25bd SELL LMT", Width = 120, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(145, 35, 35)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0)
                };
                lmtRow.Children.Add(btnBuyLmt);
                lmtRow.Children.Add(btnSellLmt);
                stack.Children.Add(lmtRow);

                // Close 1 + Close All
                var closeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                btnCloseOne = new Button
                {
                    Content = "\u25a3 CLOSE 1", Width = 90, Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(130, 90, 0)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnCloseTrade = new Button
                {
                    Content = "\u23f9 CLOSE ALL", Width = 148, Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(160, 100, 0)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0)
                };
                closeRow.Children.Add(btnCloseOne);
                closeRow.Children.Add(btnCloseTrade);
                stack.Children.Add(closeRow);

                // Emergency Kill
                btnExit = new Button
                {
                    Content = "\u26a0  EMERGENCY KILL", Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(140, 20, 20)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 4, 0, 0), BorderThickness = new Thickness(0)
                };
                stack.Children.Add(btnExit);

                stack.Children.Add(MakeSeparator());

                // Position info
                lblStatus   = MakeLabel("\u25cf Flat \u2014 ready", Brushes.CornflowerBlue, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                lblPosition = MakeLabel("DCA: 0/" + dcaMaxPositions + "  |  Avg: \u2014", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                lblHiddenSL = MakeLabel("Hidden SL: \u2014", Brushes.OrangeRed, 10, FontWeights.Normal, HorizontalAlignment.Left);
                lblHiddenTP = MakeLabel("Hidden TP: \u2014", Brushes.LimeGreen, 10, FontWeights.Normal, HorizontalAlignment.Left);
                lblVwapVal  = MakeLabel("VWAP: \u2014", Brushes.Yellow, 10, FontWeights.Normal, HorizontalAlignment.Left);
                stack.Children.Add(lblStatus);
                stack.Children.Add(lblPosition);
                stack.Children.Add(lblHiddenSL);
                stack.Children.Add(lblHiddenTP);

                // Trail toggle + info
                var trailRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
                btnTrailToggle = new Button
                {
                    Content = trailEnabled ? "TRAIL: ON" : "TRAIL: OFF",
                    Width = 80, Height = 22,
                    Background = trailEnabled
                        ? new SolidColorBrush(Color.FromRgb(120, 50, 140))
                        : new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                    Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 6, 0)
                };
                trailRow.Children.Add(btnTrailToggle);
                lblTrailInfo = MakeLabel("Trail: \u2014", Brushes.Magenta, 10, FontWeights.Normal, HorizontalAlignment.Left);
                trailRow.Children.Add(lblTrailInfo);
                stack.Children.Add(trailRow);

                stack.Children.Add(lblVwapVal);

                // Section 7: Trap toggle + info
                var trapRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
                btnTrapToggle = new Button
                {
                    Content = trapDetectorEnabled ? "TRAP: ON" : "TRAP: OFF",
                    Width = 80, Height = 22,
                    Background = trapDetectorEnabled
                        ? new SolidColorBrush(Color.FromRgb(140, 90, 20))
                        : new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                    Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 6, 0)
                };
                trapRow.Children.Add(btnTrapToggle);
                lblTrapInfo = MakeLabel("Trap: \u2014", Brushes.Orange, 10, FontWeights.Normal, HorizontalAlignment.Left);
                trapRow.Children.Add(lblTrapInfo);
                stack.Children.Add(trapRow);

                stack.Children.Add(MakeSeparator());

                // Confidence scores
                stack.Children.Add(MakeLabel("Signal Confidence:", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left));
                lblConfBull = MakeLabel("Bull: 0%", Brushes.LimeGreen, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                lblConfBear = MakeLabel("Bear: 0%", Brushes.OrangeRed, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                var confRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                lblConfBull.Width = 120;
                lblConfBear.Width = 120;
                confRow.Children.Add(lblConfBull);
                confRow.Children.Add(lblConfBear);
                stack.Children.Add(confRow);

                stack.Children.Add(MakeSeparator());

                // P&L display (Section 5b — 4 rows)
                lblUnrealized = MakeLabel("Unrealized:  $0.00", Brushes.White, 11, FontWeights.Normal, HorizontalAlignment.Left);
                lblPnL        = MakeLabel("Daily P&L:   $0.00", Brushes.White, 11, FontWeights.Normal, HorizontalAlignment.Left);
                lblTotalPnL   = MakeLabel("Total P&L:   $0.00", Brushes.White, 11, FontWeights.Normal, HorizontalAlignment.Left);
                lblAccountPnL = MakeLabel("Account P&L: $0.00", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                lblAccountBal = MakeLabel("Balance:     $0.00", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                stack.Children.Add(lblUnrealized);
                stack.Children.Add(lblPnL);
                stack.Children.Add(lblTotalPnL);
                stack.Children.Add(lblAccountPnL);
                stack.Children.Add(lblAccountBal);

                stack.Children.Add(MakeSeparator());

                // Trading hours status + toggle (Section 6)
                var hoursRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 0) };
                btnHoursToggle = new Button
                {
                    Content = tradingHoursEnabled ? "HOURS: ON" : "HOURS: OFF",
                    Width = 80, Height = 22,
                    Background = tradingHoursEnabled
                        ? new SolidColorBrush(Color.FromRgb(30, 90, 50))
                        : new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                    Foreground = Brushes.White, FontSize = 9, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 6, 0)
                };
                hoursRow.Children.Add(btnHoursToggle);
                lblTradeHours = MakeLabel("\u25cf Trading allowed", Brushes.LimeGreen, 10, FontWeights.SemiBold, HorizontalAlignment.Left);
                hoursRow.Children.Add(lblTradeHours);
                stack.Children.Add(hoursRow);

                // Resize grip
                dashResizeGrip = new Border
                {
                    Height = 14, Background = Brushes.Transparent,
                    Cursor = Cursors.SizeNWSE,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0, 0, 4, 2)
                };
                dashResizeGrip.Child = new TextBlock
                {
                    Text = "\u22f1", FontSize = 11,
                    Foreground = new SolidColorBrush(Color.FromRgb(80, 85, 100)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom
                };

                // Assemble
                dashScroll.Content = stack;
                outerStack.Children.Add(dashScroll);
                outerStack.Children.Add(dashResizeGrip);
                dashBorder.Child = outerStack;
                dashboardPanel.Children.Add(dashBorder);

                // Mouse handlers
                dashBorder.PreviewMouseDown += (s, e) =>
                {
                    if (e.ChangedButton == MouseButton.Left)
                    {
                        if (e.OriginalSource is DependencyObject src)
                        {
                            DependencyObject current = src;
                            while (current != null && current != dashBorder)
                            {
                                if (current == dashTitleBar || VisualTreeHelper.GetParent(current) == dashTitleBar)
                                {
                                    dashDragging  = true;
                                    dashDragStart = e.GetPosition(dashboardPanel.Parent as UIElement);
                                    dashBorder.CaptureMouse();
                                    break;
                                }
                                if (current == dashResizeGrip || VisualTreeHelper.GetParent(current) == dashResizeGrip)
                                {
                                    dashResizing    = true;
                                    dashResizeStart = e.GetPosition(dashboardPanel.Parent as UIElement);
                                    dashOrigWidth   = dashBorder.ActualWidth > 0 ? dashBorder.ActualWidth : 260;
                                    dashOrigHeight  = dashScroll.MaxHeight;
                                    dashBorder.CaptureMouse();
                                    break;
                                }
                                current = VisualTreeHelper.GetParent(current);
                            }
                        }
                    }
                    e.Handled = true;
                };

                dashBorder.PreviewMouseMove += (s, e) =>
                {
                    if (dashDragging)
                    {
                        Point pos = e.GetPosition(dashboardPanel.Parent as UIElement);
                        dashTranslate.X += pos.X - dashDragStart.X;
                        dashTranslate.Y += pos.Y - dashDragStart.Y;
                        dashDragStart    = pos;
                    }
                    else if (dashResizing)
                    {
                        Point pos = e.GetPosition(dashboardPanel.Parent as UIElement);
                        double dw = pos.X - dashResizeStart.X;
                        double dh = pos.Y - dashResizeStart.Y;
                        dashBorder.Width     = Math.Max(200, dashOrigWidth + dw);
                        dashScroll.MaxHeight = Math.Max(150, dashOrigHeight + dh);
                    }
                    e.Handled = true;
                };

                dashBorder.PreviewMouseUp += (s, e) =>
                {
                    if (e.ChangedButton == MouseButton.Left)
                    {
                        if (dashDragging || dashResizing)
                        {
                            dashDragging = false;
                            dashResizing = false;
                            dashBorder.ReleaseMouseCapture();
                        }
                        else if (e.OriginalSource is DependencyObject src)
                        {
                            DependencyObject current = src;
                            while (current != null && current != dashBorder)
                            {
                                if (current is Button btn)
                                {
                                    if (btn == btnBuyMkt)           { pendingLong = true; if (lblStatus != null) { lblStatus.Text = "\u25cf BUY MKT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnSellMkt)     { pendingShort = true; if (lblStatus != null) { lblStatus.Text = "\u25cf SELL MKT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnBuyAsk)      { pendingBuyAsk = true; if (lblStatus != null) { lblStatus.Text = "\u25cf BUY ASK queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnSellBid)     { pendingSellBid = true; if (lblStatus != null) { lblStatus.Text = "\u25cf SELL BID queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnBuyLmt)      { pendingLongLimit = true; if (lblStatus != null) { lblStatus.Text = "\u25cf BUY LMT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnSellLmt)     { pendingShortLimit = true; if (lblStatus != null) { lblStatus.Text = "\u25cf SELL LMT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnCloseOne)    { pendingCloseOne = true; if (lblStatus != null) { lblStatus.Text = "\u25cf CLOSE 1 queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnCloseTrade)  { pendingCloseTrade = true; if (lblStatus != null) { lblStatus.Text = "\u25cf CLOSE queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnJumpSL)      { pendingJumpSL = true; if (lblStatus != null) { lblStatus.Text = "\u25cf JUMP SL queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnExit)        { pendingFlatten = true; if (lblStatus != null) { lblStatus.Text = "\u26a0 KILL queued..."; lblStatus.Foreground = Brushes.OrangeRed; } }
                                    else if (btn == btnModeManual)  { autoMode = false; UpdateModeButtons(); }
                                    else if (btn == btnModeAuto)    { autoMode = true;  UpdateModeButtons(); }
                                    else if (btn == btnTrailToggle)
                                    {
                                        trailEnabled = !trailEnabled;
                                        btnTrailToggle.Content = trailEnabled ? "TRAIL: ON" : "TRAIL: OFF";
                                        btnTrailToggle.Background = trailEnabled
                                            ? new SolidColorBrush(Color.FromRgb(120, 50, 140))
                                            : new SolidColorBrush(Color.FromRgb(50, 55, 65));
                                        if (!trailEnabled) { trailActive = false; trailPrice = 0; trailMaxProfitPts = 0; trailTierName = ""; RemoveDrawObject("adaptiveTrail"); RemoveDrawObject("trailLabel"); }
                                    }
                                    else if (btn == btnTrapToggle)
                                    {
                                        trapDetectorEnabled = !trapDetectorEnabled;
                                        btnTrapToggle.Content = trapDetectorEnabled ? "TRAP: ON" : "TRAP: OFF";
                                        btnTrapToggle.Background = trapDetectorEnabled
                                            ? new SolidColorBrush(Color.FromRgb(140, 90, 20))
                                            : new SolidColorBrush(Color.FromRgb(50, 55, 65));
                                    }
                                    else if (btn == btnHoursToggle)
                                    {
                                        tradingHoursEnabled = !tradingHoursEnabled;
                                        btnHoursToggle.Content = tradingHoursEnabled ? "HOURS: ON" : "HOURS: OFF";
                                        btnHoursToggle.Background = tradingHoursEnabled
                                            ? new SolidColorBrush(Color.FromRgb(30, 90, 50))
                                            : new SolidColorBrush(Color.FromRgb(50, 55, 65));
                                    }
                                    else
                                    {
                                        btn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, btn));
                                    }
                                    break;
                                }
                                current = VisualTreeHelper.GetParent(current);
                            }
                        }
                    }
                    e.Handled = true;
                };

                // Floating overlay placement
                dashboardPanel.HorizontalAlignment = HorizontalAlignment.Left;
                dashboardPanel.VerticalAlignment   = VerticalAlignment.Top;

                bool placed = false;
                if (ChartControl.Parent is Grid chartGrid)
                {
                    double startX = Math.Max(10, chartGrid.ActualWidth - 280);
                    dashTranslate.X = startX;
                    dashTranslate.Y = 10;
                    chartGrid.Children.Add(dashboardPanel);
                    dashboardHostPanel = chartGrid;
                    placed = true;
                    Print("Dashboard: floating overlay at (" + startX.ToString("F0") + ", 10)");
                }

                if (!placed)
                {
                    DependencyObject parent = ChartControl;
                    while (parent != null)
                    {
                        parent = VisualTreeHelper.GetParent(parent);
                        if (parent is Grid g && g.Children.Count > 0)
                        {
                            dashTranslate.X = 10; dashTranslate.Y = 10;
                            g.Children.Add(dashboardPanel);
                            dashboardHostPanel = g;
                            placed = true;
                            Print("Dashboard: floating overlay (fallback grid)");
                            break;
                        }
                    }
                }

                if (!placed)
                {
                    Print("Dashboard: FAILED to find host — will retry");
                    dashboardPanel    = null;
                    dashboardAttached = false;
                }
              }
              catch (Exception ex)
              {
                Print("Dashboard build error: " + ex.Message + " — will retry");
                dashboardPanel    = null;
                dashboardAttached = false;
              }
            });
        }

        // ─── Dashboard helpers ────────────────────────────────────

        private StackPanel MakeAdjustRow(string label, string value,
            RoutedEventHandler minusHandler, RoutedEventHandler plusHandler, string suffix)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left };
            var lbl = MakeLabel(label, Brushes.Gray, 11, FontWeights.Normal, HorizontalAlignment.Left);
            lbl.Width = 28; lbl.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(lbl);
            row.Children.Add(MakeSmallButton("\u2212", minusHandler));
            row.Children.Add(MakeSmallButton("+", plusHandler));
            var val = MakeLabel(value, Brushes.White, 10, FontWeights.Bold, HorizontalAlignment.Left);
            val.VerticalAlignment = VerticalAlignment.Center; val.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(val);
            if (!string.IsNullOrEmpty(suffix))
            {
                var suf = MakeLabel(suffix, Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                suf.VerticalAlignment = VerticalAlignment.Center; suf.Margin = new Thickness(4, 0, 0, 0);
                row.Children.Add(suf);
            }
            return row;
        }

        private Button MakeSmallButton(string text, RoutedEventHandler handler)
        {
            var btn = new Button
            {
                Content = text, Width = 28, Height = 24,
                Background = new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0), Margin = new Thickness(2, 0, 2, 0)
            };
            btn.Click += handler;
            return btn;
        }

        private Button MakeToggleButton(string text, bool active, RoutedEventHandler handler)
        {
            return new Button
            {
                Content = text, Width = 110, Height = 30,
                Background = active
                    ? new SolidColorBrush(Color.FromRgb(30, 90, 160))
                    : new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0), Margin = new Thickness(2, 0, 2, 0)
            };
        }

        private TextBlock MakeLabel(string text, Brush color, double size, FontWeight weight, HorizontalAlignment hAlign)
        {
            return new TextBlock
            {
                Text = text, Foreground = color, FontSize = size, FontWeight = weight,
                HorizontalAlignment = hAlign, Margin = new Thickness(0, 1, 0, 1)
            };
        }

        private Border MakeSeparator()
        {
            return new Border
            {
                Height = 1,
                Background = new SolidColorBrush(Color.FromRgb(55, 60, 75)),
                Margin = new Thickness(0, 6, 0, 6)
            };
        }

        private void UpdateModeButtons()
        {
            if (btnModeManual == null || btnModeAuto == null) return;
            btnModeManual.Background = !autoMode
                ? new SolidColorBrush(Color.FromRgb(30, 90, 160))
                : new SolidColorBrush(Color.FromRgb(50, 55, 65));
            btnModeAuto.Background = autoMode
                ? new SolidColorBrush(Color.FromRgb(30, 90, 160))
                : new SolidColorBrush(Color.FromRgb(50, 55, 65));
        }

        private void UpdateStratLabel()
        {
            if (lblStratName == null) return;
            if (autoStrategy == 4)
                lblStratName.Text = "\u2605 Auto \u2192 " + StrategyNames[bestAutoStrategy];
            else
                lblStratName.Text = StrategyNames[autoStrategy];
        }

        private void UpdateAdjustLabels()
        {
            if (lblQtyVal != null) lblQtyVal.Text = contracts.ToString();
            if (lblSlVal  != null) lblSlVal.Text  = slPoints + "pt | " + (slPoints * 4) + "tk | $" + (slPoints * 20);
            if (lblTpVal  != null) lblTpVal.Text  = tpPoints + "pt | " + (tpPoints * 4) + "tk | $" + (tpPoints * 20);
        }

        private void UpdateJumpLabel()
        {
            if (lblJumpPct != null) lblJumpPct.Text = jumpSlPercent + "%";
        }

        private string FormatTime(int hhmmss)
        {
            int h = hhmmss / 10000;
            int m = (hhmmss / 100) % 100;
            int ap = h >= 12 ? 1 : 0;
            int h12 = h > 12 ? h - 12 : (h == 0 ? 12 : h);
            return h12 + ":" + m.ToString("D2") + (ap == 1 ? " PM" : " AM");
        }

        private void UpdateDashboardStatus(string text, Brush color)
        {
            if (lblStatus == null || ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (lblStatus != null) { lblStatus.Text = text; lblStatus.Foreground = color; }
            });
        }

        // ─── Full dashboard refresh ──────────────────────────────

        private void UpdateDashboard()
        {
            if (ChartControl == null) return;
            // Section 8e: null guards
            if (lblPnL == null || lblStatus == null || lblPosition == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                // Guard again on UI thread
                if (lblPnL == null || lblStatus == null) return;

                // P&L
                double unrealizedPnL = 0;
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    try { unrealizedPnL = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]); }
                    catch { unrealizedPnL = 0; }
                }

                if (lblUnrealized != null)
                {
                    lblUnrealized.Text       = "Unrealized:  " + unrealizedPnL.ToString("C2");
                    lblUnrealized.Foreground  = unrealizedPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                }

                double totalPnL = dailyRealizedPnL + unrealizedPnL;
                string tradeCountStr = maxTradesPerDay > 0
                    ? "  [Trades: " + dailyTradeCount + "/" + maxTradesPerDay + "]"
                    : "  [Trades: " + dailyTradeCount + "]";
                lblPnL.Text       = "Daily P&L:   " + dailyRealizedPnL.ToString("C2") + tradeCountStr;
                lblPnL.Foreground = dailyRealizedPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;

                if (lblTotalPnL != null)
                {
                    lblTotalPnL.Text       = "Total P&L:   " + totalPnL.ToString("C2");
                    lblTotalPnL.Foreground = totalPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                }

                // Account-level P&L
                if (lblAccountPnL != null)
                {
                    try
                    {
                        double acctRealized   = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                        double acctUnrealized = Account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                        double acctTotal      = acctRealized + acctUnrealized;
                        lblAccountPnL.Text       = "Account P&L: " + acctTotal.ToString("C2") + "  (R: " + acctRealized.ToString("C2") + "  U: " + acctUnrealized.ToString("C2") + ")";
                        lblAccountPnL.Foreground = acctTotal >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                    }
                    catch { lblAccountPnL.Text = "Account P&L: N/A"; lblAccountPnL.Foreground = Brushes.Gray; }
                }

                if (lblAccountBal != null)
                {
                    try
                    {
                        double bal = Account.Get(AccountItem.CashValue, Currency.UsDollar);
                        lblAccountBal.Text       = "Balance:     " + bal.ToString("C2");
                        lblAccountBal.Foreground = Brushes.Gray;
                    }
                    catch { lblAccountBal.Text = "Balance:     N/A"; }
                }

                // Position status
                if (pendingExit)
                {
                    lblStatus.Text       = "\u25cf CLOSING... (exit pending)";
                    lblStatus.Foreground = Brushes.Yellow;
                }
                else if (Position.MarketPosition != MarketPosition.Flat)
                {
                    string dir = Position.MarketPosition == MarketPosition.Long ? "LONG" : "SHORT";
                    int qty = Position.Quantity;
                    if (openTradeDirection != 0 && stopsArmed)
                    {
                        lblStatus.Text       = "\u25cf " + dir + "  \u00d7" + qty;
                        lblStatus.Foreground = Position.MarketPosition == MarketPosition.Long ? Brushes.LimeGreen : Brushes.OrangeRed;
                    }
                    else
                    {
                        lblStatus.Text       = "\u26a0 " + dir + " \u00d7" + qty + " (state desync!)";
                        lblStatus.Foreground = Brushes.Yellow;
                    }
                    if (lblPosition != null)
                        lblPosition.Text = "DCA: " + openDcaCount + "/" + dcaMaxPositions
                                         + "  |  Avg: " + (averageEntryPrice > 0 ? averageEntryPrice.ToString("F2") : Position.AveragePrice.ToString("F2"));
                    if (lblHiddenSL != null)
                        lblHiddenSL.Text = hiddenStopPrice > 0
                            ? "SL: " + hiddenStopPrice.ToString("F2")
                              + "  (" + slPoints + "pt | " + (slPoints * 4) + "tk | " + (slPoints * NQ_DOLLARS_PER_POINT * Math.Max(totalContracts, qty)).ToString("C0") + ")"
                            : "SL: \u2014  (stops not armed)";
                    if (lblHiddenTP != null)
                        lblHiddenTP.Text = hiddenTargetPrice > 0
                            ? "TP: " + hiddenTargetPrice.ToString("F2")
                              + "  (" + tpPoints + "pt | " + (tpPoints * 4) + "tk | " + (tpPoints * NQ_DOLLARS_PER_POINT * Math.Max(totalContracts, qty)).ToString("C0") + ")"
                            : "TP: \u2014  (stops not armed)";

                    // Trail info
                    if (lblTrailInfo != null)
                    {
                        if (trailEnabled && trailActive && trailPrice > 0)
                        {
                            double dist = openTradeDirection == 1
                                ? (Close[0] - trailPrice) / (TickSize * NQ_TICKS_PER_POINT)
                                : openTradeDirection == -1
                                    ? (trailPrice - Close[0]) / (TickSize * NQ_TICKS_PER_POINT) : 0;
                            string regime = trailTrendScore > 0.6 ? "Trend" : (trailTrendScore < 0.35 ? "Chop" : "Mix");
                            string tierStr = !string.IsNullOrEmpty(trailTierName) ? " " + trailTierName : "";
                            lblTrailInfo.Text = "Trail: " + trailPrice.ToString("F2") + " (" + dist.ToString("F1") + "pt " + regime + " " + (trailTrendScore * 100).ToString("F0") + "%" + tierStr + ")";
                            if (trailTierName == "T3-Runner") lblTrailInfo.Foreground = Brushes.Gold;
                            else if (trailTierName == "T2-Strong") lblTrailInfo.Foreground = Brushes.Cyan;
                            else if (trailTierName == "T1-BE") lblTrailInfo.Foreground = Brushes.Yellow;
                            else lblTrailInfo.Foreground = Brushes.Magenta;
                        }
                        else if (trailEnabled && !trailActive)
                        {
                            double profitPts = 0;
                            if (openTradeDirection == 1) profitPts = (Close[0] - averageEntryPrice) / (TickSize * NQ_TICKS_PER_POINT);
                            else if (openTradeDirection == -1) profitPts = (averageEntryPrice - Close[0]) / (TickSize * NQ_TICKS_PER_POINT);
                            lblTrailInfo.Text = "Trail: waiting (" + profitPts.ToString("F1") + "/" + trailActivationPoints + "pt)";
                            lblTrailInfo.Foreground = Brushes.Gray;
                        }
                        else
                        {
                            lblTrailInfo.Text = "Trail: OFF";
                            lblTrailInfo.Foreground = Brushes.Gray;
                        }
                    }

                    // Trap info
                    if (lblTrapInfo != null)
                    {
                        if (trapDetectorEnabled && trapDetected)
                        {
                            lblTrapInfo.Text = "Trap: DETECTED (" + trapScore.ToString("F0") + "%) bars=" + trapBarsInTrade;
                            lblTrapInfo.Foreground = Brushes.OrangeRed;
                        }
                        else if (trapDetectorEnabled)
                        {
                            lblTrapInfo.Text = "Trap: score " + trapScore.ToString("F0") + "% bars=" + trapBarsInTrade;
                            lblTrapInfo.Foreground = Brushes.Orange;
                        }
                        else
                        {
                            lblTrapInfo.Text = "Trap: OFF";
                            lblTrapInfo.Foreground = Brushes.Gray;
                        }
                    }
                }
                else
                {
                    // Flat status
                    if (dailyLimitHit)
                    {
                        lblStatus.Text       = "\u25a0 DAILY LOSS LIMIT \u2014 halted (" + dailyRealizedPnL.ToString("C0") + ")";
                        lblStatus.Foreground = Brushes.OrangeRed;
                    }
                    else if (emergencyKillActive)
                    {
                        lblStatus.Text       = "\u26a0 EMERGENCY KILL \u2014 halted";
                        lblStatus.Foreground = Brushes.OrangeRed;
                    }
                    else if (dailyProfitHit)
                    {
                        lblStatus.Text       = "\u25a0 DAILY PROFIT TARGET \u2014 halted (" + dailyRealizedPnL.ToString("C0") + ")";
                        lblStatus.Foreground = Brushes.Gold;
                    }
                    else if (flattenFired)
                    {
                        lblStatus.Text       = "\u25a0 EOD flatten \u2014 done for today";
                        lblStatus.Foreground = Brushes.Orange;
                    }
                    else if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay)
                    {
                        lblStatus.Text       = "\u25a0 Max trades reached (" + dailyTradeCount + "/" + maxTradesPerDay + ") \u2014 done";
                        lblStatus.Foreground = Brushes.Orange;
                    }
                    else
                    {
                        int ct2 = ToTime(Time[0]);
                        bool inAutoHours = !tradingHoursEnabled || (ct2 >= tradingStartTime && ct2 < tradingEndTime);
                        if (autoMode && !inAutoHours)
                        {
                            lblStatus.Text       = "\u25cb Flat \u2014 outside auto hours";
                            lblStatus.Foreground = Brushes.Gray;
                        }
                        else if (autoMode)
                        {
                            double best = Math.Max(lastBullConfidence, lastBearConfidence);
                            string dirStr = lastBullConfidence >= lastBearConfidence ? "Bull" : "Bear";
                            lblStatus.Text       = "\u25cf Flat \u2014 scanning (" + dirStr + " " + best.ToString("F0") + "% / need " + minSignalConfidence.ToString("F0") + "%)";
                            lblStatus.Foreground = best >= minSignalConfidence ? Brushes.LimeGreen : Brushes.CornflowerBlue;
                        }
                        else
                        {
                            lblStatus.Text       = "\u25cf Flat \u2014 manual mode";
                            lblStatus.Foreground = Brushes.CornflowerBlue;
                        }
                    }
                    if (lblPosition != null) lblPosition.Text = "DCA: 0/" + dcaMaxPositions + "  |  Avg: \u2014";
                    if (lblHiddenSL != null) lblHiddenSL.Text = "SL: \u2014  (" + slPoints + "pt | " + (slPoints * 4) + "tk | $" + (slPoints * 20) + "/ct)";
                    if (lblHiddenTP != null) lblHiddenTP.Text = "TP: \u2014  (" + tpPoints + "pt | " + (tpPoints * 4) + "tk | $" + (tpPoints * 20) + "/ct)";
                    if (lblTrailInfo != null)
                    {
                        lblTrailInfo.Text = trailEnabled ? "Trail: \u2014" : "Trail: OFF";
                        lblTrailInfo.Foreground = Brushes.Gray;
                    }
                    if (lblTrapInfo != null)
                    {
                        lblTrapInfo.Text = trapDetectorEnabled ? "Trap: \u2014" : "Trap: OFF";
                        lblTrapInfo.Foreground = Brushes.Gray;
                    }
                }

                // VWAP
                if (lblVwapVal != null)
                    lblVwapVal.Text = "VWAP: " + (vwapValue > 0 ? vwapValue.ToString("F2") : "\u2014");

                // Confidence
                if (lblConfBull != null)
                {
                    lblConfBull.Text       = "Bull: " + lastBullConfidence.ToString("F0") + "%";
                    lblConfBull.Foreground = lastBullConfidence >= minSignalConfidence ? Brushes.LimeGreen : Brushes.Gray;
                }
                if (lblConfBear != null)
                {
                    lblConfBear.Text       = "Bear: " + lastBearConfidence.ToString("F0") + "%";
                    lblConfBear.Foreground = lastBearConfidence >= minSignalConfidence ? Brushes.OrangeRed : Brushes.Gray;
                }

                // Trading hours
                if (lblTradeHours != null)
                {
                    int ct = ToTime(Time[0]);
                    bool inHours = !tradingHoursEnabled || (ct >= tradingStartTime && ct < flattenTime);
                    if (dailyLimitHit || emergencyKillActive)
                    {
                        lblTradeHours.Text       = "\u25cf Daily limit hit \u2014 trading halted";
                        lblTradeHours.Foreground  = Brushes.OrangeRed;
                    }
                    else if (flattenFired)
                    {
                        lblTradeHours.Text       = "\u25cf EOD flatten fired \u2014 done for today";
                        lblTradeHours.Foreground  = Brushes.Orange;
                    }
                    else if (inHours)
                    {
                        lblTradeHours.Text       = "\u25cf Trading allowed  (" + FormatTime(tradingStartTime) + "\u2013" + FormatTime(flattenTime) + ")";
                        lblTradeHours.Foreground  = Brushes.LimeGreen;
                    }
                    else
                    {
                        lblTradeHours.Text       = "\u25cb Outside hours  (" + FormatTime(tradingStartTime) + "\u2013" + FormatTime(flattenTime) + ")";
                        lblTradeHours.Foreground  = Brushes.Gray;
                    }
                }
            });
        }

        private void RemoveDashboard()
        {
            var panel = dashboardPanel;
            var host  = dashboardHostPanel;
            var chart = ChartControl;

            dashboardPanel     = null;
            dashboardHostPanel = null;
            dashboardAttached  = false;

            if (panel == null) return;
            if (chart == null)
            {
                try { if (host != null) host.Children.Remove(panel); } catch { }
                return;
            }

            try
            {
                chart.Dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        if (host != null) host.Children.Remove(panel);
                        else if (chart.Parent is Grid g) g.Children.Remove(panel);
                    }
                    catch { }
                });
            }
            catch { }
        }

        #endregion

        // ═══════════════════════════════════════════════════════════
        //  PROPERTIES
        // ═══════════════════════════════════════════════════════════

        #region Properties

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Stop Loss (NQ points)", Order = 1, GroupName = "1 \u2014 Risk Management",
                 Description = "Hidden SL distance from average entry.")]
        public int SlPoints { get { return slPoints; } set { slPoints = value; } }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Take Profit (NQ points)", Order = 2, GroupName = "1 \u2014 Risk Management",
                 Description = "Hidden TP distance from average entry.")]
        public int TpPoints { get { return tpPoints; } set { tpPoints = value; } }

        [NinjaScriptProperty]
        [Range(500, 10000)]
        [Display(Name = "Max Daily Loss ($)", Order = 3, GroupName = "1 \u2014 Risk Management",
                 Description = "Hard daily loss cap.")]
        public int MaxDailyLossDollars { get { return maxDailyLossDollars; } set { maxDailyLossDollars = value; } }

        [NinjaScriptProperty]
        [Range(500, 20000)]
        [Display(Name = "Daily Profit Target ($)", Order = 4, GroupName = "1 \u2014 Risk Management")]
        public int MaxDailyProfitDollars { get { return maxDailyProfitDollars; } set { maxDailyProfitDollars = value; } }

        [NinjaScriptProperty]
        [Range(0, 999)]
        [Display(Name = "Max Trades Per Day", Order = 5, GroupName = "1 \u2014 Risk Management",
                 Description = "0 = unlimited.")]
        public int MaxTradesPerDay { get { return maxTradesPerDay; } set { maxTradesPerDay = value; } }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Contracts per entry", Order = 6, GroupName = "1 \u2014 Risk Management")]
        public int Contracts { get { return contracts; } set { contracts = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Enable DCA", Order = 1, GroupName = "2 \u2014 DCA Settings")]
        public bool DcaEnabled { get { return dcaEnabled; } set { dcaEnabled = value; } }

        [NinjaScriptProperty]
        [Range(1, 4)]
        [Display(Name = "Max DCA Adds", Order = 2, GroupName = "2 \u2014 DCA Settings")]
        public int DcaMaxPositions { get { return dcaMaxPositions; } set { dcaMaxPositions = value; } }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "DCA Min Distance (NQ points)", Order = 3, GroupName = "2 \u2014 DCA Settings")]
        public int DcaDistancePoints { get { return dcaDistancePoints; } set { dcaDistancePoints = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Auto Mode (default)", Order = 1, GroupName = "3 \u2014 Trading Mode")]
        public bool AutoMode { get { return autoMode; } set { autoMode = value; } }

        [NinjaScriptProperty]
        [Range(0, 4)]
        [Display(Name = "Auto Strategy (0-4)", Order = 2, GroupName = "3 \u2014 Trading Mode",
                 Description = "0=Momentum+VWAP  1=Key Level  2=Liquidity Sweep  3=ORB  4=Auto Select")]
        public int AutoStrategy { get { return autoStrategy; } set { autoStrategy = value; } }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Min Signal Confidence (%)", Order = 3, GroupName = "3 \u2014 Trading Mode")]
        public double MinSignalConfidence { get { return minSignalConfidence; } set { minSignalConfidence = value; } }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "Entry Delay (seconds)", Order = 4, GroupName = "3 \u2014 Trading Mode")]
        public int EntryDelaySeconds { get { return entryDelaySeconds; } set { entryDelaySeconds = value; } }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "EMA Fast Period", Order = 1, GroupName = "4 \u2014 Indicators")]
        public int EmaPeriodFast { get { return emaPeriodFast; } set { emaPeriodFast = value; } }

        [NinjaScriptProperty]
        [Range(10, 200)]
        [Display(Name = "EMA Slow Period", Order = 2, GroupName = "4 \u2014 Indicators")]
        public int EmaPeriodSlow { get { return emaPeriodSlow; } set { emaPeriodSlow = value; } }

        [NinjaScriptProperty]
        [Range(5, 30)]
        [Display(Name = "RSI Period", Order = 3, GroupName = "4 \u2014 Indicators")]
        public int RsiPeriod { get { return rsiPeriod; } set { rsiPeriod = value; } }

        [NinjaScriptProperty]
        [Range(5, 30)]
        [Display(Name = "ATR Period", Order = 4, GroupName = "4 \u2014 Indicators")]
        public int AtrPeriod { get { return atrPeriod; } set { atrPeriod = value; } }

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "SL/TP Adjust Step (points)", Order = 1, GroupName = "5 \u2014 Display")]
        public int SlTpAdjustStep { get { return slTpAdjustStep; } set { slTpAdjustStep = value; } }

        [NinjaScriptProperty]
        [Range(10, 95)]
        [Display(Name = "Jump SL % (toward price)", Order = 2, GroupName = "5 \u2014 Display")]
        public int JumpSlPercent { get { return jumpSlPercent; } set { jumpSlPercent = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA on chart", Order = 2, GroupName = "5 \u2014 Display")]
        public bool ShowEma { get { return showEma; } set { showEma = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show RSI panel", Order = 3, GroupName = "5 \u2014 Display")]
        public bool ShowRsi { get { return showRsi; } set { showRsi = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show ATR panel", Order = 4, GroupName = "5 \u2014 Display")]
        public bool ShowAtr { get { return showAtr; } set { showAtr = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP line", Order = 5, GroupName = "5 \u2014 Display")]
        public bool ShowVwap { get { return showVwap; } set { showVwap = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show Key Levels", Order = 6, GroupName = "5 \u2014 Display")]
        public bool ShowKeyLevels { get { return showKeyLevels; } set { showKeyLevels = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show Sweep Signals", Order = 7, GroupName = "5 \u2014 Display")]
        public bool ShowSweepSignals { get { return showSweepSignals; } set { showSweepSignals = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trading Start (HHMMSS ET)", Order = 1, GroupName = "6 \u2014 Trading Hours")]
        public int TradingStartTime { get { return tradingStartTime; } set { tradingStartTime = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trading End (HHMMSS ET)", Order = 2, GroupName = "6 \u2014 Trading Hours")]
        public int TradingEndTime { get { return tradingEndTime; } set { tradingEndTime = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Auto-Flatten Time (HHMMSS ET)", Order = 3, GroupName = "6 \u2014 Trading Hours")]
        public int FlattenTime { get { return flattenTime; } set { flattenTime = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Enable Trading Hours Filter", Order = 0, GroupName = "6 \u2014 Trading Hours",
                 Description = "When OFF, auto entries can fire at any time. Toggleable on dashboard.")]
        public bool TradingHoursEnabled { get { return tradingHoursEnabled; } set { tradingHoursEnabled = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Enable Adaptive Trail", Order = 1, GroupName = "7 \u2014 Adaptive Trail")]
        public bool TrailEnabled { get { return trailEnabled; } set { trailEnabled = value; } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trail Activation (NQ points)", Order = 2, GroupName = "7 \u2014 Adaptive Trail")]
        public int TrailActivationPoints { get { return trailActivationPoints; } set { trailActivationPoints = value; } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trail Min Distance (NQ points)", Order = 3, GroupName = "7 \u2014 Adaptive Trail")]
        public int TrailMinPoints { get { return trailMinPoints; } set { trailMinPoints = value; } }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "Trail Max Distance (NQ points)", Order = 4, GroupName = "7 \u2014 Adaptive Trail")]
        public int TrailMaxPoints { get { return trailMaxPoints; } set { trailMaxPoints = value; } }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Trail ATR Multiplier", Order = 5, GroupName = "7 \u2014 Adaptive Trail")]
        public double TrailAtrMultiplier { get { return trailAtrMultiplier; } set { trailAtrMultiplier = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Enable Post-Entry Trap Detector", Order = 1, GroupName = "8 \u2014 Trap Detector",
                 Description = "Monitor for adverse price action after entry. Dashboard shows trap score and alert.")]
        public bool EnablePostEntryTrapDetector { get { return enablePostEntryTrapDetector; } set { enablePostEntryTrapDetector = value; } }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Volatility Spike Guard Bars", Order = 2, GroupName = "8 \u2014 Trap Detector",
                 Description = "Skip trail updates when bar range > 3×ATR. 0 = disabled.")]
        public int VolatilitySpikeGuardBars { get { return volatilitySpikeGuardBars; } set { volatilitySpikeGuardBars = value; } }

        #endregion
    }
}
