// ============================================================
//  Mm-ATM v2.0 Strategy for NinjaTrader 8
//  Full documentation: see Mm_ATM_v2.md
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
    public class Mm_ATM_v2 : Strategy
    {
        #region Fields

        // ─── Constants ────────────────────────────────────────────
        private const double NQ_TICKS_PER_POINT   = 4.0;
        private const double NQ_DOLLARS_PER_TICK   = 5.0;
        private const double NQ_DOLLARS_PER_POINT  = 20.0;
        private const int    STALE_EXIT_TICKS      = 50;
        private const int    FLAT_SYNC_MAX_TICKS   = 30;
        private const double TRAIL_SPIKE_MULT      = 2.0;
        private const double VALUE_AREA_PCT        = 0.70;
        private const int    AGGRESSIVE_LIMIT_TIMEOUT_SEC = 3;

        // ─── User parameters ──────────────────────────────────────
        private int    slPoints;
        private int    tpPoints;
        private int    maxDailyLossDollars;
        private int    maxDailyProfitDollars;
        private int    contracts;
        private int    maxContracts;
        private int    dcaSuggestionPoints;
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
        private bool   showVolumeProfile;
        private int    tradingStartTime;
        private int    tradingEndTime;
        private int    flattenTime;
        private bool   flattenFired;
        private bool   tradingHoursEnabled;
        private bool   enablePostEntryTrapDetector;
        private int    autoSelectStabilityBars;

        // ─── Signal filter parameters ─────────────────────────────
        private bool   htfFilterEnabled;
        private int    htfEmaPeriod;
        private bool   useVolumeProfileFilters;
        private int    lossCooldownSeconds;

        // ─── Indicator references ─────────────────────────────────
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaFast;
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaSlow;
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaHtf;
        private NinjaTrader.NinjaScript.Indicators.RSI indRsi;
        private NinjaTrader.NinjaScript.Indicators.ATR indAtr;

        // ─── Session / daily tracking ─────────────────────────────
        private double   dailyRealizedPnL;
        private double   pnlBaselineOffset;
        private int      processedTradeCount;
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

        // ─── Position tracking ────────────────────────────────────
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

        // ─── Thread-safe button flags ─────────────────────────────
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
        private bool     pendingLimitFlatten;
        private bool     pendingEmergencyKill;
        private int      pendingExitTicks;
        private int      flatSyncGraceTicks;
        private DateTime lastEntryWallTime;
        private bool     firstBarSeen;
        private bool     emergencyKillActive;
        private DateTime aggressiveLimitSubmitTime;
        private bool     pendingRemoveTrailDraw;

        // ─── Dashboard build retry ────────────────────────────────
        private int  dashBuildRetryCount;
        private int  dashBuildTickCounter;
        private int  dashUpdateTickCounter;

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

        // ─── Auto Select stability ───────────────────────────────
        private int    autoStratConsecutiveBars;
        private int    lastBestAutoStrategy;
        private string lastAutoStrategyUsed;

        // ─── Consecutive loss cooldown ────────────────────────────
        private int      consecutiveLosses;
        private DateTime lastLossTime;

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
        private TextBlock lblAccountPnL;
        private TextBlock lblAccountBal;
        private TextBlock lblConfBull;
        private TextBlock lblConfBear;
        private TextBlock lblPosition;
        private TextBlock lblHiddenSL;
        private TextBlock lblHiddenTP;
        private TextBlock lblQtyVal;
        private TextBlock lblSlVal;
        private TextBlock lblTpVal;
        private TextBlock lblVwapVal;
        private TextBlock lblTradeHours;
        private TextBlock lblJumpPct;
        private TextBlock lblTrapInfo;
        private TextBlock lblActiveStrategy;
        private TextBlock lblQtyMax;
        private Button    btnStratPrevConf;
        private Button    btnStratNextConf;
        private StackPanel stratPanel;
        private TextBlock  lblStratName;

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

        // ─── Post-Entry Trap Detector ─────────────────────────────
        private bool   trapDetectorEnabled;
        private double trapScore;
        private bool   trapDetected;
        private int    trapBarsInTrade;
        private Button btnTrapToggle;

        // ─── Volume Profile ───────────────────────────────────────
        private SortedDictionary<double, double> volumeAtPrice;
        private double pocLevel;
        private double vahLevel;
        private double valLevel;
        private bool   volumeProfileReady;
        private double totalSessionVolume;

        #endregion

        #region OnStateChange

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Mm-ATM v2.0 \u2014 Volume Profile + HTF Filter + DCA Overhaul + Full Bug Fixes";
                Name         = "Mm_ATM_v2";
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
                maxContracts          = 4;
                dcaSuggestionPoints   = 50;
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
                showVolumeProfile     = true;
                tradingStartTime      = 93000;
                tradingEndTime        = 160000;
                flattenTime           = 165000;
                tradingHoursEnabled   = true;
                enablePostEntryTrapDetector = true;
                autoSelectStabilityBars = 2;

                trailEnabled          = true;
                trailActivationPoints = 8;
                trailMinPoints        = 3;
                trailMaxPoints        = 25;
                trailAtrMultiplier    = 1.5;

                htfFilterEnabled      = true;
                htfEmaPeriod          = 45;
                useVolumeProfileFilters = true;
                lossCooldownSeconds   = 120;
            }
            else if (State == State.Configure)
            {
                indEmaFast = EMA(emaPeriodFast);
                indEmaSlow = EMA(emaPeriodSlow);
                indEmaHtf  = EMA(htfEmaPeriod);
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
                volumeAtPrice = new SortedDictionary<double, double>();
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
                dailyTradeCount        = 0;
                dailyRealizedPnL       = 0;
                processedTradeCount    = 0;
                dailyLimitHit          = false;
                dailyProfitHit         = false;
                flattenFired           = false;
                emergencyKillActive    = false;

                pendingExit            = false;
                pendingExitTicks       = 0;
                pendingLimitFlatten    = false;
                pendingLong            = false;
                pendingShort           = false;
                pendingLongLimit       = false;
                pendingShortLimit      = false;
                pendingBuyAsk          = false;
                pendingSellBid         = false;
                pendingFlatten         = false;
                pendingCloseTrade      = false;
                pendingEmergencyKill   = false;
                pendingCloseOne        = false;
                pendingJumpSL          = false;
                pendingRearm           = false;

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

                trapScore              = 0;
                trapDetected           = false;
                trapBarsInTrade        = 0;
                trapDetectorEnabled    = enablePostEntryTrapDetector;

                orbHigh                = 0;
                orbLow                 = 0;
                orbSet                 = false;

                lastBullConfidence     = 0;
                lastBearConfidence     = 0;
                bestAutoStrategy       = 0;
                autoStratConsecutiveBars = 0;
                lastBestAutoStrategy   = -1;
                lastAutoStrategyUsed   = "";

                consecutiveLosses      = 0;
                lastLossTime           = DateTime.MinValue;

                volumeAtPrice          = new SortedDictionary<double, double>();
                pocLevel               = 0;
                vahLevel               = 0;
                valLevel               = 0;
                volumeProfileReady     = false;
                totalSessionVolume     = 0;

                firstBarSeen           = false;
                dashDragging           = false;
                dashResizing           = false;
                dashBuildRetryCount    = 0;
                dashBuildTickCounter   = 0;

                slPoints      = defaultSlPoints > 0 ? defaultSlPoints : slPoints;
                tpPoints      = defaultTpPoints > 0 ? defaultTpPoints : tpPoints;
                contracts     = defaultContracts > 0 ? defaultContracts : contracts;
                jumpSlPercent = defaultJumpSlPercent > 0 ? defaultJumpSlPercent : jumpSlPercent;

                pnlBaselineOffset = 0;
                if (SystemPerformance != null && SystemPerformance.AllTrades != null)
                {
                    foreach (Trade t in SystemPerformance.AllTrades)
                        if (t.Entry.Time.Date == sessionDate)
                            pnlBaselineOffset += t.ProfitCurrency;
                    processedTradeCount = SystemPerformance.AllTrades.Count;
                }
                dailyRealizedPnL = 0;

                Print("[Mm-ATM v2] State.Realtime: FULL RESET \u2014 pnlBaseline=" + pnlBaselineOffset.ToString("C2")
                    + " SL=" + slPoints + " TP=" + tpPoints + " contracts=" + contracts);
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
            if (State == State.Realtime && pendingEmergencyKill)
            {
                pendingEmergencyKill = false;
                ExecuteEmergencyKill();
            }
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
            if (pendingRemoveTrailDraw)
            {
                pendingRemoveTrailDraw = false;
                RemoveDrawObject("adaptiveTrail");
                RemoveDrawObject("trailLabel");
            }

            // Block entries while exit pending + stale exit safety net
            if (pendingExit)
            {
                if (pendingLong)    { pendingLong    = false; UpdateDashboardStatus("\u26a0 LONG blocked: exit pending", Brushes.Orange); }
                if (pendingShort)   { pendingShort   = false; UpdateDashboardStatus("\u26a0 SHORT blocked: exit pending", Brushes.Orange); }
                if (pendingBuyAsk)  { pendingBuyAsk  = false; UpdateDashboardStatus("\u26a0 BUY ASK blocked: exit pending", Brushes.Orange); }
                if (pendingSellBid) { pendingSellBid = false; UpdateDashboardStatus("\u26a0 SELL BID blocked: exit pending", Brushes.Orange); }

                pendingExitTicks++;
                if (pendingExitTicks >= STALE_EXIT_TICKS && Position.MarketPosition != MarketPosition.Flat)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | STALE EXIT (" + pendingExitTicks + " ticks) \u2014 Account.Flatten");
                    try { Account.Flatten(new[] { Instrument }); }
                    catch (Exception ex) { Print("[Mm-ATM v2] Stale-exit flatten failed: " + ex.Message); }
                    pendingExitTicks = 0;
                }
            }
            else
            {
                pendingExitTicks = 0;
            }

            // Aggressive limit order timeout
            if (State == State.Realtime && aggressiveLimitSubmitTime != DateTime.MinValue
                && (DateTime.Now - aggressiveLimitSubmitTime).TotalSeconds >= AGGRESSIVE_LIMIT_TIMEOUT_SEC
                && Position.MarketPosition == MarketPosition.Flat && openTradeDirection != 0)
            {
                Print("[Mm-ATM v2] " + Time[0] + " | ASK/BID order expired \u2014 cancelling");
                CancelPendingOrders();
                ResetPositionState();
                aggressiveLimitSubmitTime = DateTime.MinValue;
                UpdateDashboardStatus("\u26a0 ASK/BID order expired", Brushes.Orange);
            }

            // Button entries (no reverse logic \u2014 Bug 3 fix)
            if (State == State.Realtime && !pendingExit)
            {
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

            // Position state sync — MUST be AFTER button processing so entries aren't wiped
            SyncPositionState();

            // Hidden SL/TP monitor \u2014 every tick
            if (stopsArmed && !pendingExit && Position.MarketPosition != MarketPosition.Flat)
                MonitorHiddenStops();

            // Adaptive trailing stop \u2014 every tick
            if (trailEnabled && !pendingExit && Position.MarketPosition != MarketPosition.Flat)
                MonitorAdaptiveTrail();

            // Post-entry trap detector \u2014 once per bar
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

            // Volume profile \u2014 update every bar for performance
            if (IsFirstTickOfBar)
                UpdateVolumeProfile();

            // Dashboard build with retry — attempt immediately, then every 2 ticks
            if (State == State.Realtime && !dashboardAttached && dashBuildRetryCount < 10)
            {
                if (dashBuildRetryCount == 0)
                    BuildDashboard();
                else
                {
                    dashBuildTickCounter++;
                    if (dashBuildTickCounter >= 5)
                    {
                        dashBuildTickCounter = 0;
                        BuildDashboard();
                    }
                }
            }

            // Auto-flatten & CME maintenance
            if (State == State.Realtime)
            {
                int currentTime = ToTime(Time[0]);

                if (currentTime >= flattenTime && !flattenFired && Position.MarketPosition != MarketPosition.Flat)
                {
                    flattenFired = true;
                    Print("[Mm-ATM v2] " + Time[0] + " | AUTO-FLATTEN at " + Time[0].ToString("HH:mm:ss"));
                    ExecuteFlatten();
                }

                if (currentTime >= 165500 && currentTime < 180000 && Position.MarketPosition != MarketPosition.Flat)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | CME SESSION CLOSE FLATTEN");
                    ExecuteFlatten();
                }

                if (pendingLimitFlatten && Position.MarketPosition != MarketPosition.Flat)
                {
                    pendingLimitFlatten = false;
                    Print("[Mm-ATM v2] " + Time[0] + " | DEFERRED FLATTEN from OnBarUpdate");
                    ExecuteFlatten();
                }
                else if (pendingLimitFlatten)
                {
                    pendingLimitFlatten = false;
                }

                if (dailyLimitHit)
                    UpdateDashboardStatus("DAILY LOSS LIMIT \u2014 NO TRADES", Brushes.OrangeRed);
            }

            // Calculate signals — only needed for entries; skip entirely when in position
            bool isFlat = Position.MarketPosition == MarketPosition.Flat;
            if (isFlat && IsFirstTickOfBar)
            {
                CalculateSignals();
            }

            // Auto entry
            if (State == State.Realtime && isFlat)
            {
                int currentTime2 = ToTime(Time[0]);
                bool insideTradingHours = !tradingHoursEnabled
                    || (currentTime2 >= tradingStartTime && currentTime2 < tradingEndTime);

                bool cooldownActive = lossCooldownSeconds > 0
                    && consecutiveLosses > 0
                    && (DateTime.Now - lastLossTime).TotalSeconds < lossCooldownSeconds;

                if (autoMode && !dailyLimitHit && !dailyProfitHit && !emergencyKillActive
                    && insideTradingHours && openTradeDirection == 0
                    && (maxTradesPerDay <= 0 || dailyTradeCount < maxTradesPerDay)
                    && !cooldownActive)
                {
                    if (lastBullConfidence >= minSignalConfidence)
                        ExecuteLongEntry(false);
                    else if (lastBearConfidence >= minSignalConfidence)
                        ExecuteShortEntry(false);
                }
                else if (autoMode && CurrentBar % 100 == 0)
                {
                    string reason = "";
                    if (dailyLimitHit) reason = "dailyLossLimit";
                    else if (dailyProfitHit) reason = "dailyProfitHit";
                    else if (emergencyKillActive) reason = "emergencyKill";
                    else if (cooldownActive) reason = "lossCooldown(" + consecutiveLosses + " losses, " + ((int)(DateTime.Now - lastLossTime).TotalSeconds) + "s/" + lossCooldownSeconds + "s)";
                    else if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay) reason = "maxTradesPerDay";
                    else if (!insideTradingHours) reason = "outsideHours";
                    else reason = "lowConf(Bull=" + lastBullConfidence.ToString("F0") + "% Bear=" + lastBearConfidence.ToString("F0") + "%)";
                    Print("[Mm-ATM v2] " + Time[0] + " | AUTO-SCAN: no entry \u2014 " + reason);
                }
            }

            // Chart & dashboard (gate draws to first tick of bar when possible)
            if (IsFirstTickOfBar)
                UpdateOrbLevels();
            if (IsFirstTickOfBar)
            {
                DrawChartAnnotations();
                DrawDcaLevels();
                DrawKeyLevels();
                DrawSweepSignals();
                DrawVolumeProfileLines();
            }
            if (IsFirstTickOfBar)
                DrawVwapLine();

            // Throttle dashboard: every 3rd tick in trade, every 8th tick when flat
            bool inPosition = Position.MarketPosition != MarketPosition.Flat;
            dashUpdateTickCounter++;
            int dashInterval = (inPosition || stopsArmed || pendingExit) ? 3 : 8;
            if (IsFirstTickOfBar || dashUpdateTickCounter >= dashInterval)
            {
                dashUpdateTickCounter = 0;
                UpdateDashboard();
            }
        }

        // Consolidated position state sync \u2014 called once per tick
        private void SyncPositionState()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (pendingExit)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | Position sync: exit confirmed FLAT");
                    ResetPositionState();
                    flatSyncGraceTicks = 0;
                }
                else if (openTradeDirection != 0)
                {
                    flatSyncGraceTicks++;
                    if (flatSyncGraceTicks > FLAT_SYNC_MAX_TICKS)
                    {
                        Print("[Mm-ATM v2] " + Time[0] + " | SAFETY: flat after " + flatSyncGraceTicks + " ticks \u2014 resetting");
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
        }

        #endregion

        #region Monitors

        private void MonitorHiddenStops()
        {
            if (dailyLimitHit || emergencyKillActive) return;

            double price = Close[0];
            var signals = activeEntrySignals.ToList();
            if (signals.Count == 0) return;

            if (openTradeDirection == 1)
            {
                if (price <= hiddenStopPrice)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | HIDDEN SL LONG hit @ " + price.ToString("F2"));
                    foreach (string sig in signals) ExitLong("Close", sig);
                    stopsArmed = false; pendingExit = true;
                }
                else if (price >= hiddenTargetPrice)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | HIDDEN TP LONG hit @ " + price.ToString("F2"));
                    foreach (string sig in signals) ExitLong("Close", sig);
                    stopsArmed = false; pendingExit = true;
                }
            }
            else if (openTradeDirection == -1)
            {
                if (price >= hiddenStopPrice)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | HIDDEN SL SHORT hit @ " + price.ToString("F2"));
                    foreach (string sig in signals) ExitShort("Close", sig);
                    stopsArmed = false; pendingExit = true;
                }
                else if (price <= hiddenTargetPrice)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | HIDDEN TP SHORT hit @ " + price.ToString("F2"));
                    foreach (string sig in signals) ExitShort("Close", sig);
                    stopsArmed = false; pendingExit = true;
                }
            }
        }

        private void MonitorAdaptiveTrail()
        {
            if (!trailEnabled || !stopsArmed || openTradeDirection == 0) return;

            if (volatilitySpikeGuardBars > 0 && CurrentBar > 1)
            {
                double barRng = High[0] - Low[0];
                double atrNow = indAtr[0];
                if (atrNow > 0 && barRng > atrNow * 3.0)
                    return;
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
                    trailPrice = openTradeDirection == 1 ? price - distOff : price + distOff;
                    trailPrice = Math.Round(trailPrice / TickSize) * TickSize;
                    Print("[Mm-ATM v2] " + Time[0] + " | TRAIL ACTIVATED profit=" + profitPoints.ToString("F1")
                        + "pts dist=" + initDist.ToString("F1") + " trail=" + trailPrice.ToString("F2"));
                }
                return;
            }

            double trailDist = CalculateTrailDistance();

            // Trap-tightened trail: if trap detected, reduce distance by 40%
            if (trapDetectorEnabled && trapDetected)
                trailDist *= 0.60;

            double trailOff = trailDist * NQ_TICKS_PER_POINT * TickSize;

            var signals = activeEntrySignals.ToList();
            if (signals.Count == 0) return;

            // Profit tier floors
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
                if (newTrail > trailPrice) trailPrice = newTrail;

                double floorPrice = Math.Round((averageEntryPrice + tierFloor) / TickSize) * TickSize;
                if (tierFloor > 0 && trailPrice < floorPrice) trailPrice = floorPrice;

                if (price <= trailPrice)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | TRAIL HIT LONG @ " + price.ToString("F2")
                        + " trail=" + trailPrice.ToString("F2") + " tier=" + trailTierName);
                    foreach (string sig in signals) ExitLong("Close", sig);
                    stopsArmed = false; pendingExit = true;
                }
            }
            else if (openTradeDirection == -1)
            {
                double newTrail = Math.Round((price + trailOff) / TickSize) * TickSize;
                if (newTrail < trailPrice) trailPrice = newTrail;

                double floorPrice = Math.Round((averageEntryPrice - tierFloor) / TickSize) * TickSize;
                if (tierFloor > 0 && trailPrice > floorPrice) trailPrice = floorPrice;

                if (price >= trailPrice)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | TRAIL HIT SHORT @ " + price.ToString("F2")
                        + " trail=" + trailPrice.ToString("F2") + " tier=" + trailTierName);
                    foreach (string sig in signals) ExitShort("Close", sig);
                    stopsArmed = false; pendingExit = true;
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
            for (int i = 0; i < lookback; i++) atrSum += indAtr[i];
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

            // Volatility spike widening: if ATR > 2x avg, widen trail 50%
            double spikeMult = 1.0;
            if (atrAvg > 0 && atr > atrAvg * TRAIL_SPIKE_MULT)
                spikeMult = 1.5;

            double chopMultiplier = trailTrendScore < 0.5 ? 0.5 + trailTrendScore : 1.0;
            double atrDist = atr * trailAtrMultiplier / (TickSize * NQ_TICKS_PER_POINT);
            double regimeDist = LerpD(trailMinPoints, trailMaxPoints, trailTrendScore);
            double finalDist = (regimeDist * 0.6 + atrDist * 0.4) * chopMultiplier * spikeMult;
            return Math.Max(trailMinPoints, Math.Min(trailMaxPoints, finalDist));
        }

        private double LerpD(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        private void MonitorPostEntryTrap()
        {
            if (!trapDetectorEnabled || openTradeDirection == 0 || !stopsArmed) return;
            if (CurrentBar < 5) return;

            double price = Close[0];
            double atr = indAtr[0];
            if (atr <= 0) return;

            double adverseMove = openTradeDirection == 1
                ? (averageEntryPrice - price) / (TickSize * NQ_TICKS_PER_POINT)
                : (price - averageEntryPrice) / (TickSize * NQ_TICKS_PER_POINT);

            trapScore = 0;
            double atrPts = atr / (TickSize * NQ_TICKS_PER_POINT);
            if (atrPts > 0 && adverseMove > 0)
            {
                double moveRatio = adverseMove / atrPts;
                if (moveRatio > 0.5) trapScore += moveRatio * 30;
            }

            if (openTradeDirection == 1 && indEmaFast[0] < indEmaSlow[0]) trapScore += 20;
            else if (openTradeDirection == -1 && indEmaFast[0] > indEmaSlow[0]) trapScore += 20;

            double volSum = 0;
            int vlb = Math.Min(10, CurrentBar - 1);
            for (int i = 1; i <= vlb; i++) volSum += Volume[i];
            double volAvg = vlb > 0 ? volSum / vlb : Volume[0];
            if (volAvg > 0 && Volume[0] > volAvg * 1.5 && adverseMove > 0) trapScore += 15;

            double bRange = High[0] - Low[0];
            if (bRange > 0)
            {
                if (openTradeDirection == 1)
                {
                    double uw = High[0] - Math.Max(Close[0], Open[0]);
                    if (uw / bRange > 0.5) trapScore += 10;
                }
                else
                {
                    double lw = Math.Min(Close[0], Open[0]) - Low[0];
                    if (lw / bRange > 0.5) trapScore += 10;
                }
            }

            trapBarsInTrade++;
            trapDetected = trapScore >= 50;
            if (trapDetected && trapBarsInTrade >= 3)
                Print("[Mm-ATM v2] " + Time[0] + " | TRAP DETECTED: score=" + trapScore.ToString("F0") + " bars=" + trapBarsInTrade);
        }

        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState, DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;

            if (orderState == OrderState.Filled)
            {
                if (order.IsLong && openTradeDirection == 1 && !stopsArmed)
                {
                    averageEntryPrice = averageFillPrice > 0 ? averageFillPrice : order.AverageFillPrice;
                    ArmHiddenStops();
                    aggressiveLimitSubmitTime = DateTime.MinValue;
                    Print("[Mm-ATM v2] " + Time[0] + " | LONG fill confirmed @ " + averageEntryPrice.ToString("F2"));
                }
                else if (order.IsShort && openTradeDirection == -1 && !stopsArmed)
                {
                    averageEntryPrice = averageFillPrice > 0 ? averageFillPrice : order.AverageFillPrice;
                    ArmHiddenStops();
                    aggressiveLimitSubmitTime = DateTime.MinValue;
                    Print("[Mm-ATM v2] " + Time[0] + " | SHORT fill confirmed @ " + averageEntryPrice.ToString("F2"));
                }
            }
            else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
            {
                if (aggressiveLimitSubmitTime != DateTime.MinValue)
                {
                    aggressiveLimitSubmitTime = DateTime.MinValue;
                    if (Position.MarketPosition == MarketPosition.Flat && openTradeDirection != 0)
                    {
                        Print("[Mm-ATM v2] " + Time[0] + " | Order " + orderState + " \u2014 resetting");
                        ResetPositionState();
                    }
                }
            }
        }

        #endregion

        #region Entry / Exit

        // CanEnterTrade \u2014 simplified: no reverse logic (Bug 3 fix), no DCA distance guard
        private bool CanEnterTrade(string label, bool isManual, int direction)
        {
            if (pendingExit) { UpdateDashboardStatus("\u26a0 " + label + " blocked: exit pending", Brushes.Orange); return false; }
            if (dailyLimitHit || dailyProfitHit) { UpdateDashboardStatus("\u26a0 " + label + " blocked: daily limit", Brushes.OrangeRed); return false; }
            if (emergencyKillActive) { UpdateDashboardStatus("\u26a0 " + label + " blocked: KILL active", Brushes.OrangeRed); return false; }
            if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay && Position.MarketPosition == MarketPosition.Flat)
            { UpdateDashboardStatus("\u26a0 " + label + " blocked: max trades/day", Brushes.Orange); return false; }

            int ct = ToTime(Time[0]);
            if (ct >= 165500 && ct < 180000) { UpdateDashboardStatus("\u26a0 " + label + " blocked: CME maintenance", Brushes.OrangeRed); return false; }
            if (!isManual && tradingHoursEnabled && (ct < tradingStartTime || ct >= flattenTime))
            { UpdateDashboardStatus("\u26a0 " + label + " blocked: outside auto hours", Brushes.Orange); return false; }

            // Opposing position: close ONLY, never reverse (Bug 3 fix)
            if (direction == 1 && Position.MarketPosition == MarketPosition.Short)
            {
                if (Position.Quantity > contracts)
                    ExecuteCloseOne();
                else
                    ExecuteCloseTrade();
                return false;
            }
            if (direction == -1 && Position.MarketPosition == MarketPosition.Long)
            {
                if (Position.Quantity > contracts)
                    ExecuteCloseOne();
                else
                    ExecuteCloseTrade();
                return false;
            }

            // Max contracts guard (replaces old DCA guards)
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                bool sameDir = (direction == 1 && Position.MarketPosition == MarketPosition.Long)
                            || (direction == -1 && Position.MarketPosition == MarketPosition.Short);
                if (sameDir && Position.Quantity >= maxContracts)
                {
                    UpdateDashboardStatus("\u26a0 Max contracts reached (" + maxContracts + ")", Brushes.Orange);
                    return false;
                }
            }

            if (entryDelaySeconds > 0 && ((State == State.Realtime ? DateTime.Now : Time[0]) - lastEntryWallTime).TotalSeconds < entryDelaySeconds)
            { UpdateDashboardStatus("\u26a0 " + label + " blocked: cooldown", Brushes.Orange); return false; }

            return true;
        }

        // Pre-entry trap filter (Part 4e)
        private double CalcPreEntryTrapScore(int direction)
        {
            if (CurrentBar < 5) return 0;
            double score = 0;
            double atr = indAtr[0];
            if (atr <= 0) return 0;

            double recentMove = direction == 1
                ? Close[0] - Math.Min(Low[0], Low[1])
                : Math.Max(High[0], High[1]) - Close[0];
            double atrPts = atr / (TickSize * NQ_TICKS_PER_POINT);
            if (atrPts > 0)
                score += Math.Min(40, (recentMove / (TickSize * NQ_TICKS_PER_POINT)) / atrPts * 30);

            if (direction == 1 && indEmaFast[0] < indEmaSlow[0]) score += 25;
            if (direction == -1 && indEmaFast[0] > indEmaSlow[0]) score += 25;

            double barRange = High[0] - Low[0];
            if (barRange > 0)
            {
                if (direction == 1) { double uw = (High[0] - Math.Max(Close[0], Open[0])) / barRange; if (uw > 0.45) score += 20; }
                if (direction == -1) { double lw = (Math.Min(Close[0], Open[0]) - Low[0]) / barRange; if (lw > 0.45) score += 20; }
            }
            return Math.Min(100, score);
        }

        private void ExecuteLongEntry(bool isManual = false)
        {
            if (!CanEnterTrade("BUY MKT", isManual, 1)) return;

            // Pre-entry trap filter: block auto, warn manual
            if (!isManual)
            {
                double trapRisk = CalcPreEntryTrapScore(1);
                if (trapRisk >= 60)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | PRE-ENTRY TRAP BLOCKED: score=" + trapRisk.ToString("F0"));
                    UpdateDashboardStatus("\u26a0 Entry blocked: trap risk " + trapRisk.ToString("F0") + "%", Brushes.Orange);
                    return;
                }
            }

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            lastAutoStrategyUsed = StrategyNames[autoStrategy == 4 ? bestAutoStrategy : autoStrategy];
            string signalName = "Entry_" + openDcaCount;
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
            Print("[Mm-ATM v2] " + Time[0] + " | LONG #" + openDcaCount + " qty=" + contracts
                + " @ " + Close[0].ToString("F2") + " AvgEntry=" + averageEntryPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf LONG #" + openDcaCount + " @ " + Close[0].ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteShortEntry(bool isManual = false)
        {
            if (!CanEnterTrade("SELL MKT", isManual, -1)) return;

            if (!isManual)
            {
                double trapRisk = CalcPreEntryTrapScore(-1);
                if (trapRisk >= 60)
                {
                    Print("[Mm-ATM v2] " + Time[0] + " | PRE-ENTRY TRAP BLOCKED: score=" + trapRisk.ToString("F0"));
                    UpdateDashboardStatus("\u26a0 Entry blocked: trap risk " + trapRisk.ToString("F0") + "%", Brushes.Orange);
                    return;
                }
            }

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            lastAutoStrategyUsed = StrategyNames[autoStrategy == 4 ? bestAutoStrategy : autoStrategy];
            string signalName = "Entry_" + openDcaCount;
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
            Print("[Mm-ATM v2] " + Time[0] + " | SHORT #" + openDcaCount + " qty=" + contracts
                + " @ " + Close[0].ToString("F2") + " AvgEntry=" + averageEntryPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf SHORT #" + openDcaCount + " @ " + Close[0].ToString("F2"), Brushes.OrangeRed);
        }

        private void ExecuteBuyAskEntry()
        {
            if (!CanEnterTrade("BUY ASK", true, 1)) return;
            double limitPrice = GetCurrentAsk();
            if (limitPrice <= 0) limitPrice = Close[0] + TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            lastAutoStrategyUsed = StrategyNames[autoStrategy == 4 ? bestAutoStrategy : autoStrategy];
            string signalName = "Entry_" + openDcaCount;
            EnterLongLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = 1;
            lastEntryWallTime = DateTime.Now;
            aggressiveLimitSubmitTime = DateTime.Now;
            totalContracts += contracts;
            averageEntryPrice = 0;
            Print("[Mm-ATM v2] " + Time[0] + " | BUY ASK #" + openDcaCount + " @ ask " + limitPrice.ToString("F2"));
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
            lastAutoStrategyUsed = StrategyNames[autoStrategy == 4 ? bestAutoStrategy : autoStrategy];
            string signalName = "Entry_" + openDcaCount;
            EnterShortLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = -1;
            lastEntryWallTime = DateTime.Now;
            aggressiveLimitSubmitTime = DateTime.Now;
            totalContracts += contracts;
            averageEntryPrice = 0;
            Print("[Mm-ATM v2] " + Time[0] + " | SELL BID #" + openDcaCount + " @ bid " + limitPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf SELL BID #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.OrangeRed);
        }

        private void ExecuteLongLimitEntry()
        {
            if (!CanEnterTrade("BUY LMT", true, 1)) return;
            double limitPrice = GetCurrentBid();
            if (limitPrice <= 0) limitPrice = Close[0] - TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            lastAutoStrategyUsed = StrategyNames[autoStrategy == 4 ? bestAutoStrategy : autoStrategy];
            string signalName = "Entry_" + openDcaCount;
            EnterLongLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = 1;
            lastEntryWallTime = DateTime.Now;
            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0 ? (averageEntryPrice * totalContracts + limitPrice * contracts) / newQty : limitPrice;
            totalContracts = newQty;
            ArmHiddenStops();
            Print("[Mm-ATM v2] " + Time[0] + " | BUY LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf BUY LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteShortLimitEntry()
        {
            if (!CanEnterTrade("SELL LMT", true, -1)) return;
            double limitPrice = GetCurrentAsk();
            if (limitPrice <= 0) limitPrice = Close[0] + TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            lastAutoStrategyUsed = StrategyNames[autoStrategy == 4 ? bestAutoStrategy : autoStrategy];
            string signalName = "Entry_" + openDcaCount;
            EnterShortLimit(contracts, limitPrice, signalName);
            activeEntrySignals.Add(signalName);
            openTradeDirection = -1;
            lastEntryWallTime = DateTime.Now;
            double newQty = totalContracts + contracts;
            averageEntryPrice = totalContracts > 0 ? (averageEntryPrice * totalContracts + limitPrice * contracts) / newQty : limitPrice;
            totalContracts = newQty;
            ArmHiddenStops();
            Print("[Mm-ATM v2] " + Time[0] + " | SELL LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"));
            UpdateDashboardStatus("\u25cf SELL LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.OrangeRed);
        }

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
        }

        private void ExecuteFlatten()
        {
            CancelPendingOrders();
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                ResetPositionState();
                UpdateDashboardStatus("\u25cf Already flat \u2014 state reset", Brushes.CornflowerBlue);
                return;
            }
            try { Account.Flatten(new[] { Instrument }); }
            catch (Exception ex)
            {
                Print("[Mm-ATM v2] Account.Flatten failed: " + ex.Message);
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
                    if (Position.MarketPosition == MarketPosition.Long) ExitLong("Close", "");
                    else ExitShort("Close", "");
                }
            }
            stopsArmed = false; pendingExit = true;
        }

        private void ExecuteCloseTrade()
        {
            bool hadPending = CancelPendingOrders();
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (hadPending) { ResetPositionState(); UpdateDashboardStatus("\u25cf Cancelled pending order", Brushes.Yellow); }
                else UpdateDashboardStatus("\u25cf Already flat", Brushes.CornflowerBlue);
                return;
            }
            var signals = activeEntrySignals.ToList();
            if (signals.Count > 0)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    foreach (string sig in signals) ExitLong("Close", sig);
                else
                    foreach (string sig in signals) ExitShort("Close", sig);
            }
            else
            {
                if (Position.MarketPosition == MarketPosition.Long) ExitLong("Close", "");
                else ExitShort("Close", "");
            }
            stopsArmed = false; pendingExit = true;
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
                        working.Add(order);
                }
                if (working.Count > 0) { Account.Cancel(working.ToArray()); cancelled = true; }
            }
            catch (Exception ex) { Print("[Mm-ATM v2] CancelPendingOrders: " + ex.Message); }
            return cancelled;
        }

        private void ExecuteCloseOne()
        {
            if (Position.MarketPosition == MarketPosition.Flat) { UpdateDashboardStatus("\u25cf Already flat", Brushes.CornflowerBlue); return; }
            if (Position.Quantity <= 1) { ExecuteCloseTrade(); return; }

            string sig = activeEntrySignals.Count > 0 ? activeEntrySignals[activeEntrySignals.Count - 1] : "";
            if (Position.MarketPosition == MarketPosition.Long) ExitLong(1, "Close", sig);
            else ExitShort(1, "Close", sig);

            if (activeEntrySignals.Count > 0) activeEntrySignals.RemoveAt(activeEntrySignals.Count - 1);
            openDcaCount = Math.Max(0, openDcaCount - 1);
            totalContracts = Math.Max(0, totalContracts - 1);
            UpdateDashboardStatus("\u25cf Closed 1 contract", Brushes.Yellow);
        }

        private void ExecuteJumpSL()
        {
            if (!stopsArmed || Position.MarketPosition == MarketPosition.Flat)
            { UpdateDashboardStatus("\u26a0 No active SL to jump", Brushes.Orange); return; }

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
            if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());
            UpdateDashboardStatus("\u25cf SL jumped to " + hiddenStopPrice.ToString("F2"), Brushes.Yellow);
        }

        private void ExecuteEmergencyKill()
        {
            emergencyKillActive = true;
            dailyLimitHit = true;
            CancelPendingOrders();
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                try { Account.Flatten(new[] { Instrument }); }
                catch (Exception ex) { Print("[Mm-ATM v2] Emergency kill flatten failed: " + ex.Message); }
            }
            stopsArmed = false; pendingExit = true;
            UpdateDashboardStatus("\u26a0 EMERGENCY KILL \u2014 trading halted", Brushes.OrangeRed);
        }

        private void ResetPositionState()
        {
            stopsArmed = false; pendingExit = false; pendingExitTicks = 0;
            flatSyncGraceTicks = 0; openTradeDirection = 0;
            openDcaCount = 0; totalContracts = 0; averageEntryPrice = 0;
            hiddenStopPrice = 0; hiddenTargetPrice = 0;
            aggressiveLimitSubmitTime = DateTime.MinValue;
            activeEntrySignals.Clear();
            trailPrice = 0; trailActive = false; trailTrendScore = 0;
            trailMaxProfitPts = 0; trailTierName = "";
            trapScore = 0; trapDetected = false; trapBarsInTrade = 0;
            lastAutoStrategyUsed = "";

            if (defaultSlPoints > 0) slPoints = defaultSlPoints;
            if (defaultTpPoints > 0) tpPoints = defaultTpPoints;
            contracts = defaultContracts;
            jumpSlPercent = defaultJumpSlPercent;
            if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());

            RemoveDrawObject("hiddenSL"); RemoveDrawObject("hiddenTP");
            RemoveDrawObject("avgEntryLine"); RemoveDrawObject("slLabel"); RemoveDrawObject("tpLabel");
            RemoveDrawObject("dcaSuggestion"); RemoveDrawObject("dcaSuggestionLbl");
            RemoveDrawObject("adaptiveTrail"); RemoveDrawObject("trailLabel");
        }

        protected override void OnPositionUpdate(Position position, double averagePrice,
            int quantity, MarketPosition marketPosition)
        {
            if (marketPosition == MarketPosition.Flat)
                ResetPositionState();
        }

        // Incremental P&L tracking (Bug 2 fix)
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution.Order == null) return;

            if (SystemPerformance?.AllTrades != null)
            {
                int total = SystemPerformance.AllTrades.Count;
                for (int i = processedTradeCount; i < total; i++)
                {
                    Trade t = SystemPerformance.AllTrades[i];
                    if (t.Entry.Time.Date == sessionDate)
                        dailyRealizedPnL += t.ProfitCurrency;
                }
                processedTradeCount = total;

                // Check last closed trade for consecutive loss tracking
                if (total > 0)
                {
                    Trade lastTrade = SystemPerformance.AllTrades[total - 1];
                    if (lastTrade.Exit.Time >= time.AddSeconds(-2))
                    {
                        if (lastTrade.ProfitCurrency < 0)
                        {
                            consecutiveLosses++;
                            lastLossTime = DateTime.Now;
                        }
                        else
                        {
                            consecutiveLosses = 0;
                        }
                    }
                }
            }

            // v2 uses incremental tracking — dailyRealizedPnL already contains only
            // trades completed after Realtime start; no baseline subtraction needed
            double checkPnL = dailyRealizedPnL;
            if (checkPnL <= -maxDailyLossDollars && !dailyLimitHit)
            {
                dailyLimitHit = true;
                pendingLimitFlatten = true;
                Print("[Mm-ATM v2] *** DAILY LOSS LIMIT: " + checkPnL.ToString("C0") + " ***");
            }
            if (checkPnL >= maxDailyProfitDollars && !dailyProfitHit)
            {
                dailyProfitHit = true;
                pendingLimitFlatten = true;
                Print("[Mm-ATM v2] *** DAILY PROFIT TARGET: " + checkPnL.ToString("C0") + " ***");
            }
        }

        #endregion

        #region VWAP calculation

        private void ResetVwap()
        {
            vwapCumTPV = 0; vwapCumVol = 0; vwapValue = 0;
            prevBarVwap = 0; currBarTPV = 0; currBarVol = 0;
        }

        private void UpdateVwap()
        {
            double tp = (High[0] + Low[0] + Close[0]) / 3.0;
            double v  = Volume[0];
            if (IsFirstTickOfBar && CurrentBar > 0)
            {
                vwapCumTPV += currBarTPV;
                vwapCumVol += currBarVol;
                currBarTPV = tp * v;
                currBarVol = v;
            }
            else
            {
                currBarTPV = tp * v;
                currBarVol = v;
            }
            double totalTPV = vwapCumTPV + currBarTPV;
            double totalVol = vwapCumVol + currBarVol;
            prevBarVwap = vwapValue;
            vwapValue = totalVol > 0 ? totalTPV / totalVol : Close[0];
        }

        #endregion

        #region Volume Profile (Part 5)

        private void UpdateVolumeProfile()
        {
            if (volumeAtPrice == null) return;
            double tp = Math.Round(((High[0] + Low[0] + Close[0]) / 3.0) / TickSize) * TickSize;
            double vol = Volume[0];
            if (volumeAtPrice.ContainsKey(tp)) volumeAtPrice[tp] += vol;
            else volumeAtPrice[tp] = vol;
            // Only recalculate levels every 10 bars to avoid expensive sort/expansion
            if (CurrentBar % 10 == 0)
                CalculateVolumeProfileLevels();
        }

        private void CalculateVolumeProfileLevels()
        {
            if (volumeAtPrice == null || volumeAtPrice.Count == 0) return;

            // Find POC
            double maxVol = 0;
            pocLevel = 0;
            foreach (var kvp in volumeAtPrice)
            {
                if (kvp.Value > maxVol) { maxVol = kvp.Value; pocLevel = kvp.Key; }
            }

            // Calculate value area (70% of volume, expanding from POC)
            double totalVol = 0;
            foreach (var kvp in volumeAtPrice) totalVol += kvp.Value;
            double targetVol = totalVol * VALUE_AREA_PCT;

            // Keys are already sorted (SortedDictionary)
            var sortedPrices = new List<double>(volumeAtPrice.Keys);
            int pocIdx = sortedPrices.BinarySearch(pocLevel);
            if (pocIdx < 0) { vahLevel = pocLevel; valLevel = pocLevel; return; }

            double areaVol = volumeAtPrice[pocLevel];
            int lo = pocIdx, hi = pocIdx;
            while (areaVol < targetVol && (lo > 0 || hi < sortedPrices.Count - 1))
            {
                double volBelow = (lo > 0) ? volumeAtPrice[sortedPrices[lo - 1]] : 0;
                double volAbove = (hi < sortedPrices.Count - 1) ? volumeAtPrice[sortedPrices[hi + 1]] : 0;
                if (volAbove >= volBelow && hi < sortedPrices.Count - 1)
                { hi++; areaVol += volumeAtPrice[sortedPrices[hi]]; }
                else if (lo > 0)
                { lo--; areaVol += volumeAtPrice[sortedPrices[lo]]; }
                else
                { hi++; areaVol += volumeAtPrice[sortedPrices[hi]]; }
            }
            valLevel = sortedPrices[lo];
            vahLevel = sortedPrices[hi];
        }

        private void DrawVolumeProfileLines()
        {
            if (pocLevel > 0)
            {
                Draw.HorizontalLine(this, "vpPOC", pocLevel, Brushes.Gold, DashStyleHelper.Solid, 2);
                Draw.HorizontalLine(this, "vpVAH", vahLevel, Brushes.DodgerBlue, DashStyleHelper.Dash, 1);
                Draw.HorizontalLine(this, "vpVAL", valLevel, Brushes.DodgerBlue, DashStyleHelper.Dash, 1);
            }
        }

        #endregion

        #region Chart Annotations & Daily Reset

        private void ResetDailyTracking()
        {
            sessionDate          = Time[0].Date;
            dailyRealizedPnL     = 0;
            pnlBaselineOffset    = 0;
            dailyLimitHit        = false;
            dailyProfitHit       = false;
            dailyTradeCount      = 0;
            processedTradeCount  = 0;
            flattenFired         = false;
            emergencyKillActive  = false;
            consecutiveLosses    = 0;
            lastLossTime         = DateTime.MinValue;
            orbSet               = false;
            orbHigh              = 0;
            orbLow               = 0;
            if (volumeAtPrice != null) { volumeAtPrice.Clear(); volumeProfileReady = false; }
            pocLevel = 0; vahLevel = 0; valLevel = 0;
            Print("Session reset: " + sessionDate.ToShortDateString()
                + " MaxLoss=$" + maxDailyLossDollars + " ProfitTarget=$" + maxDailyProfitDollars
                + " MaxTrades=" + (maxTradesPerDay > 0 ? maxTradesPerDay.ToString() : "\u221e"));
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
                Draw.HorizontalLine(this, "orbHigh", false, orbHigh, Brushes.LimeGreen, DashStyleHelper.Dash, 2);
                Draw.HorizontalLine(this, "orbLow",  false, orbLow,  Brushes.OrangeRed, DashStyleHelper.Dash, 2);
            }
        }

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
            if (!stopsArmed || dcaSuggestionPoints <= 0 || totalContracts >= maxContracts || averageEntryPrice == 0)
            {
                RemoveDrawObject("dcaSuggestion"); RemoveDrawObject("dcaSuggestionLbl");
                return;
            }

            double distOffset = dcaSuggestionPoints * NQ_TICKS_PER_POINT * TickSize;
            int remaining = (int)(maxContracts - totalContracts);
            string dcaInfo = "DCA suggestion (" + remaining + " slots, qty " + contracts + ")";

            if (openTradeDirection == 1)
            {
                double dcaBelow = averageEntryPrice - distOffset;
                Draw.HorizontalLine(this, "dcaSuggestion", false, dcaBelow, Brushes.DeepSkyBlue, DashStyleHelper.Dot, 1);
                Draw.Text(this, "dcaSuggestionLbl", dcaInfo, 0, dcaBelow - 4 * TickSize, Brushes.DeepSkyBlue);
            }
            else if (openTradeDirection == -1)
            {
                double dcaAbove = averageEntryPrice + distOffset;
                Draw.HorizontalLine(this, "dcaSuggestion", false, dcaAbove, Brushes.DeepSkyBlue, DashStyleHelper.Dot, 1);
                Draw.Text(this, "dcaSuggestionLbl", dcaInfo, 0, dcaAbove + 4 * TickSize, Brushes.DeepSkyBlue);
            }
        }

        private void DrawVwapLine()
        {
            if (!showVwap || CurrentBar < BarsRequiredToTrade + 1 || vwapValue == 0) return;
            if (IsFirstTickOfBar && prevBarVwap > 0)
            {
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
            Draw.Text(this, "keyResistLbl",  "Key Resist "  + priorHigh.ToString("F2"), 0, priorHigh + 2 * TickSize, Brushes.Cyan);
            Draw.Text(this, "keySupportLbl", "Key Support " + priorLow.ToString("F2"),  0, priorLow  - 2 * TickSize, Brushes.Cyan);
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

        #endregion

        #region Signal Calculation

        private void CalculateSignals()
        {
            if (CurrentBar < emaPeriodSlow + 5) return;

            // UpdateVwap & UpdateVolumeProfile already called in OnBarUpdate

            lastBullConfidence = 0;
            lastBearConfidence = 0;

            if (autoStrategy == 4) // Auto Select
            {
                double[] bullScores = new double[4];
                double[] bearScores = new double[4];
                for (int s = 0; s < 4; s++)
                {
                    double bull = 0, bear = 0;
                    switch (s)
                    {
                        case 0: CalcMomentumVwap(ref bull, ref bear); break;
                        case 1: CalcKeyLevelBreakout(ref bull, ref bear); break;
                        case 2: CalcLiquiditySweepReversal(ref bull, ref bear); break;
                        case 3: CalcOpeningRangeBreakout(ref bull, ref bear); break;
                    }
                    bullScores[s] = bull;
                    bearScores[s] = bear;
                }
                // Pick best by max combined score
                double bestTotal = 0;
                int bestIdx = 0;
                for (int s = 0; s < 4; s++)
                {
                    double total = Math.Max(bullScores[s], bearScores[s]);
                    if (total > bestTotal) { bestTotal = total; bestIdx = s; }
                }

                // Auto-select stability filter (Part 4g):
                // require same strategy N consecutive bars before switching
                if (bestIdx == lastBestAutoStrategy)
                    autoStratConsecutiveBars++;
                else
                    autoStratConsecutiveBars = 1;
                lastBestAutoStrategy = bestIdx;

                if (autoStratConsecutiveBars >= 3)
                    bestAutoStrategy = bestIdx;
                // else keep previous bestAutoStrategy

                lastBullConfidence = bullScores[bestAutoStrategy];
                lastBearConfidence = bearScores[bestAutoStrategy];
            }
            else
            {
                switch (autoStrategy)
                {
                    case 0: CalcMomentumVwap(ref lastBullConfidence, ref lastBearConfidence); break;
                    case 1: CalcKeyLevelBreakout(ref lastBullConfidence, ref lastBearConfidence); break;
                    case 2: CalcLiquiditySweepReversal(ref lastBullConfidence, ref lastBearConfidence); break;
                    case 3: CalcOpeningRangeBreakout(ref lastBullConfidence, ref lastBearConfidence); break;
                }
            }

            ApplySmartFilters();
        }

        // --- Strategy 0: Momentum + VWAP ---
        private void CalcMomentumVwap(ref double bull, ref double bear)
        {
            if (CurrentBar < emaPeriodSlow + 5) return;
            double emaF = indEmaFast[0], emaS = indEmaSlow[0], rsi = indRsi[0];

            if (emaF > emaS) bull += 30;
            if (emaF < emaS) bear += 30;
            if (CrossAbove(Close, vwapValue, 1)) bull += 35;
            else if (Close[0] > vwapValue) bull += 20;
            if (CrossBelow(Close, vwapValue, 1)) bear += 35;
            else if (Close[0] < vwapValue) bear += 20;
            if (rsi > 50 && rsi < 75) bull += 20;
            if (rsi < 50 && rsi > 25) bear += 20;
            if (Close[0] > High[1]) bull += 15;
            if (Close[0] < Low[1]) bear += 15;
            if (CrossAbove(Close, vwapValue, 1) && Close[0] > Open[0]) bull += 10;
            if (CrossBelow(Close, vwapValue, 1) && Close[0] < Open[0]) bear += 10;
        }

        // --- Strategy 1: Key Level Breakout ---
        private void CalcKeyLevelBreakout(ref double bull, ref double bear)
        {
            if (CurrentBar < 25) return;
            int ct = ToTime(Time[0]);
            if ((ct >= 93000 && ct < 93500) || (ct >= 120000 && ct < 133000)) return;

            double hi20 = double.MinValue, lo20 = double.MaxValue;
            for (int i = 1; i <= 20; i++)
            {
                if (High[i] > hi20) hi20 = High[i];
                if (Low[i] < lo20) lo20 = Low[i];
            }
            double atr = indAtr[0];

            // Bullish
            if (Close[0] > hi20 && CrossAbove(Close, hi20, 1) && Close[0] > Open[0]) bull += 50;
            else if (CrossAbove(Close, hi20, 1)) bull += 25;
            else if (Close[0] > hi20) bull += 25;
            if (atr > 0 && (Close[0] - hi20) > 0.15 * atr) bull += 30;
            if (indEmaFast[0] > indEmaSlow[0]) bull += 20;

            // Bearish
            if (Close[0] < lo20 && CrossBelow(Close, lo20, 1) && Close[0] < Open[0]) bear += 50;
            else if (CrossBelow(Close, lo20, 1)) bear += 25;
            else if (Close[0] < lo20) bear += 25;
            if (atr > 0 && (lo20 - Close[0]) > 0.15 * atr) bear += 30;
            if (indEmaFast[0] < indEmaSlow[0]) bear += 20;
        }

        // --- Strategy 2: Liquidity Sweep Reversal ---
        private void CalcLiquiditySweepReversal(ref double bull, ref double bear)
        {
            if (CurrentBar < 25) return;
            double atr = indAtr[0]; if (atr <= 0) return;
            double swLo = double.MaxValue, swHi = double.MinValue;
            for (int i = 2; i <= 20; i++)
            {
                if (Low[i] < swLo) swLo = Low[i];
                if (High[i] > swHi) swHi = High[i];
            }
            double rsi = indRsi[0];

            // Bullish sweep
            if (Low[1] < swLo && Close[0] > swLo) bull += 45;
            else if (Low[0] < swLo && Close[0] > Low[0]) bull += 25;
            if (rsi < 40) bull += 30;
            if ((Close[0] - Low[0]) > 0.3 * atr) bull += 25;
            double avgVol = 0; for (int i = 1; i <= 20; i++) avgVol += Volume[i]; avgVol /= 20;
            if (Volume[0] > 1.5 * avgVol) bull += 10;

            // Bearish sweep
            if (High[1] > swHi && Close[0] < swHi) bear += 45;
            else if (High[0] > swHi && Close[0] < High[0]) bear += 25;
            if (rsi > 60) bear += 30;
            if ((High[0] - Close[0]) > 0.3 * atr) bear += 25;
            if (Volume[0] > 1.5 * avgVol) bear += 10;
        }

        // --- Strategy 3: Opening Range Breakout ---
        private void CalcOpeningRangeBreakout(ref double bull, ref double bear)
        {
            int ct = ToTime(Time[0]);
            if (ct >= 93000 && ct < 94500 && !orbSet)
            {
                if (High[0] > orbHigh) orbHigh = High[0];
                if (Low[0] < orbLow || orbLow == 0) orbLow = Low[0];
            }
            if (ct >= 94500 && !orbSet && orbHigh > orbLow && orbLow > 0) orbSet = true;
            if (!orbSet || ct < 94500 || ct > 113000) return;

            double atr = indAtr[0]; double rng = orbHigh - orbLow;

            if (Close[0] > orbHigh && CrossAbove(Close, orbHigh, 1) && Close[0] > Open[0]) bull += 55;
            else if (CrossAbove(Close, orbHigh, 1)) bull += 30;
            else if (Close[0] > orbHigh) bull += 30;
            if (indEmaFast[0] > indEmaSlow[0]) bull += 25;
            if (rng > 0 && atr > 0) bull += Math.Min(20, 20 * (atr / rng));

            if (Close[0] < orbLow && CrossBelow(Close, orbLow, 1) && Close[0] < Open[0]) bear += 55;
            else if (CrossBelow(Close, orbLow, 1)) bear += 30;
            else if (Close[0] < orbLow) bear += 30;
            if (indEmaFast[0] < indEmaSlow[0]) bear += 25;
            if (rng > 0 && atr > 0) bear += Math.Min(20, 20 * (atr / rng));
        }

        #endregion

        #region Smart Filters (v2 enhanced: 7 new filters)

        private void ApplySmartFilters()
        {
            if (CurrentBar < emaPeriodSlow + 5) return;
            double atr = indAtr[0];

            // --- Original 5 filters from v1 ---

            // 1. Volume Confirmation
            double avgVol = 0;
            int lookback = Math.Min(20, CurrentBar);
            for (int i = 1; i <= lookback; i++) avgVol += Volume[i];
            avgVol = lookback > 0 ? avgVol / lookback : 1;
            double vr = avgVol > 0 ? Volume[0] / avgVol : 1;
            if (vr < 0.5) { lastBullConfidence *= 0.7; lastBearConfidence *= 0.7; }
            else if (vr > 2.0) { lastBullConfidence *= 1.15; lastBearConfidence *= 1.15; }

            // 2. Bull/Bear Conflict
            double conflict = Math.Min(lastBullConfidence, lastBearConfidence);
            double dominant = Math.Max(lastBullConfidence, lastBearConfidence);
            if (dominant > 0 && conflict / dominant > 0.70)
            { lastBullConfidence *= 0.7; lastBearConfidence *= 0.7; }

            // 3. Momentum Exhaustion
            if (atr > 0)
            {
                double move = Math.Abs(Close[0] - Close[1]) / atr;
                if (move > 1.5 && indRsi[0] > 80) lastBullConfidence *= 0.6;
                if (move > 1.5 && indRsi[0] < 20) lastBearConfidence *= 0.6;
            }

            // 4. Bar Quality (Wick Rejection)
            double barRange = High[0] - Low[0];
            if (barRange > 0)
            {
                double bodyPct = Math.Abs(Close[0] - Open[0]) / barRange;
                if (bodyPct < 0.25) { lastBullConfidence *= 0.8; lastBearConfidence *= 0.8; }
            }

            // 5. Night Session Penalty
            int ct = ToTime(Time[0]);
            if (ct >= 180000 || ct < 80000)
            { lastBullConfidence *= 0.7; lastBearConfidence *= 0.7; }

            // --- New v2 Filters (Part 4) ---

            // 6. HTF Trend Filter (Part 4a)
            if (htfFilterEnabled && indEmaHtf != null)
            {
                double htfTrend = CalcHtfTrend();
                // -1 = bearish, 0 = neutral, +1 = bullish
                if (htfTrend < 0) lastBullConfidence *= 0.6;  // fighting the HTF trend
                if (htfTrend > 0) lastBearConfidence *= 0.6;
            }

            // 7. VWAP Distance Scaling (Part 4b)
            if (atr > 0 && vwapValue > 0)
            {
                double vwapDist = CalcVwapDistance();
                // Extended from VWAP = penalty (likely stretched)
                if (vwapDist > 2.0)
                {
                    double penalty = Math.Max(0.5, 1.0 - (vwapDist - 2.0) * 0.15);
                    lastBullConfidence *= penalty;
                    lastBearConfidence *= penalty;
                }
            }

            // 8. Swing Structure (Part 4c)
            double swingBias = CalcSwingStructure();
            // -1 = lower highs, +1 = higher lows
            if (swingBias > 0.5) lastBearConfidence *= 0.7;
            if (swingBias < -0.5) lastBullConfidence *= 0.7;

            // 9. Key Level Mid-Range Penalty (Part 4d)
            if (pocLevel > 0 && valLevel > 0 && vahLevel > 0 && useVolumeProfileFilters)
            {
                double penalty = CalcKeyLevelMidRangePenalty();
                lastBullConfidence *= penalty;
                lastBearConfidence *= penalty;
            }

            // 10. Volume Profile Filter (Part 5b)
            if (useVolumeProfileFilters && pocLevel > 0)
            {
                double distFromPOC = Math.Abs(Close[0] - pocLevel);
                double pocRange = atr > 0 ? distFromPOC / atr : 0;

                // Near POC (value zone) — less directional edge, penalize
                if (pocRange < 0.3)
                { lastBullConfidence *= 0.85; lastBearConfidence *= 0.85; }

                // Price near VAH/VAL — potential bounce zone supports direction
                if (Close[0] >= vahLevel && Close[0] <= vahLevel + atr * 0.3)
                    lastBearConfidence *= 1.1;  // rejection from VAH → bearish support
                if (Close[0] <= valLevel && Close[0] >= valLevel - atr * 0.3)
                    lastBullConfidence *= 1.1;  // bounce off VAL → bullish support
            }

            lastBullConfidence = Math.Max(0, Math.Min(120, lastBullConfidence));
            lastBearConfidence = Math.Max(0, Math.Min(120, lastBearConfidence));
        }

        // HTF Trend: +1 bullish, -1 bearish, 0 neutral
        private double CalcHtfTrend()
        {
            if (indEmaHtf == null || CurrentBar < htfEmaPeriod + 5) return 0;
            double htf = indEmaHtf[0];
            double price = Close[0];
            double dist = (price - htf) / (indAtr[0] > 0 ? indAtr[0] : 1);
            if (dist > 0.5) return 1;
            if (dist < -0.5) return -1;
            return 0;
        }

        // VWAP Distance in ATR multiples
        private double CalcVwapDistance()
        {
            double atr = indAtr[0];
            if (atr <= 0 || vwapValue <= 0) return 0;
            return Math.Abs(Close[0] - vwapValue) / atr;
        }

        // Swing Structure: +1 = higher lows (bull), -1 = lower highs (bear)
        private double CalcSwingStructure()
        {
            if (CurrentBar < 15) return 0;
            int hl = 0, ll = 0, hh = 0, lh = 0;
            for (int i = 1; i < 10; i++)
            {
                if (Low[i] > Low[i + 1]) hl++;
                else if (Low[i] < Low[i + 1]) ll++;
                if (High[i] > High[i + 1]) hh++;
                else if (High[i] < High[i + 1]) lh++;
            }
            double bullStruc = (hl + hh) / 18.0;   // 0..1
            double bearStruc = (ll + lh) / 18.0;
            return bullStruc - bearStruc;   // +1 full bull, -1 full bear
        }

        // Key Level Mid-Range Penalty: price stuck in mid value area → less edge
        private double CalcKeyLevelMidRangePenalty()
        {
            if (vahLevel <= valLevel || pocLevel <= 0) return 1.0;
            double range = vahLevel - valLevel;
            double midLo = valLevel + range * 0.35;
            double midHi = valLevel + range * 0.65;
            if (Close[0] >= midLo && Close[0] <= midHi) return 0.80;  // 20% penalty in mid-range
            return 1.0;
        }

        // Utility
        private double GetCurrentBid() { return GetCurrentBid(0); }
        private double GetCurrentAsk() { return GetCurrentAsk(0); }

        private bool CrossAbove(ISeries<double> series, double level, int lookback)
        {
            if (CurrentBar < lookback) return false;
            return series[0] > level && series[lookback] <= level;
        }
        private bool CrossBelow(ISeries<double> series, double level, int lookback)
        {
            if (CurrentBar < lookback) return false;
            return series[0] < level && series[lookback] >= level;
        }

        #endregion

        #region Dashboard

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
                    Width           = 270
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
                    Text = "\u2630  NQ  Mm-ATM  v2",
                    Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                outerStack.Children.Add(dashTitleBar);

                dashScroll = new ScrollViewer
                {
                    MaxHeight = 520,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };

                var stack = new StackPanel { Margin = new Thickness(10, 4, 10, 4) };

                // Mode toggle
                var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnModeManual = MakeToggleButton("MANUAL", !autoMode, null);
                btnModeAuto   = MakeToggleButton("AUTO",    autoMode, null);
                modeRow.Children.Add(btnModeManual);
                modeRow.Children.Add(btnModeAuto);
                stack.Children.Add(modeRow);

                // Strategy selector (Part 3a: moved above Qty, freeze in trade)
                stratPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                stratPanel.Children.Add(MakeLabel("Auto Strategy:", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left));
                var stratRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                btnStratPrevConf = MakeSmallButton("\u25c4", null);
                lblStratName = MakeLabel(StrategyNames[autoStrategy], Brushes.White, 11, FontWeights.SemiBold, HorizontalAlignment.Center);
                lblStratName.Width = 140;
                lblStratName.TextAlignment = TextAlignment.Center;
                btnStratNextConf = MakeSmallButton("\u25ba", null);
                stratRow.Children.Add(btnStratPrevConf);
                stratRow.Children.Add(lblStratName);
                stratRow.Children.Add(btnStratNextConf);
                stratPanel.Children.Add(stratRow);

                // Active strategy label (Part 8)
                lblActiveStrategy = MakeLabel("Active: \u2014", Brushes.Cyan, 10, FontWeights.Normal, HorizontalAlignment.Left);
                stratPanel.Children.Add(lblActiveStrategy);
                stack.Children.Add(stratPanel);

                stack.Children.Add(MakeSeparator());

                // Qty row (maxContracts replaces dcaMaxPositions)
                var qtyRow = MakeAdjustRow("Qty:", contracts.ToString(),
                    (s, e) => { contracts = Math.Max(1, contracts - 1); UpdateAdjustLabels(); },
                    (s, e) => { contracts = Math.Min(maxContracts, contracts + 1); UpdateAdjustLabels(); },
                    "");
                lblQtyVal = (TextBlock)((StackPanel)qtyRow).Children[3];
                stack.Children.Add(qtyRow);

                // Qty max label
                lblQtyMax = MakeLabel("max " + maxContracts, Brushes.Gray, 9, FontWeights.Normal, HorizontalAlignment.Left);
                lblQtyMax.Margin = new Thickness(34, 0, 0, 0);
                stack.Children.Add(lblQtyMax);

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
                lblJumpPct.Width = 36; lblJumpPct.VerticalAlignment = VerticalAlignment.Center; lblJumpPct.TextAlignment = TextAlignment.Center;
                jumpRow.Children.Add(lblJumpPct);
                jumpRow.Children.Add(MakeSmallButton("+", (s, e) => { jumpSlPercent = Math.Min(95, jumpSlPercent + 10); UpdateJumpLabel(); }));
                stack.Children.Add(jumpRow);

                stack.Children.Add(MakeSeparator());

                // Entry buttons: BUY MKT / SELL MKT
                var btnRow1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyMkt = new Button
                {
                    Content = "\u25b2 BUY MKT", Width = 125, Height = 36,
                    Background = new SolidColorBrush(Color.FromRgb(22, 140, 100)),
                    Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellMkt = new Button
                {
                    Content = "\u25bc SELL MKT", Width = 125, Height = 36,
                    Background = new SolidColorBrush(Color.FromRgb(190, 45, 45)),
                    Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0)
                };
                btnRow1.Children.Add(btnBuyMkt);
                btnRow1.Children.Add(btnSellMkt);
                stack.Children.Add(btnRow1);

                // BUY ASK / SELL BID
                var btnRow2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyAsk = new Button
                {
                    Content = "\u25b3 BUY ASK", Width = 125, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(18, 120, 90)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellBid = new Button
                {
                    Content = "\u25bd SELL BID", Width = 125, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(160, 38, 38)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0)
                };
                btnRow2.Children.Add(btnBuyAsk);
                btnRow2.Children.Add(btnSellBid);
                stack.Children.Add(btnRow2);

                // BUY LMT / SELL LMT
                var lmtRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyLmt = new Button
                {
                    Content = "\u25b3 BUY LMT", Width = 125, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(18, 105, 78)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellLmt = new Button
                {
                    Content = "\u25bd SELL LMT", Width = 125, Height = 30,
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
                    Content = "\u23f9 CLOSE ALL", Width = 155, Height = 28,
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
                lblPosition = MakeLabel("Pos: 0/" + maxContracts + "  |  Avg: \u2014", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
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

                // Trap toggle + info
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

                // Confidence scores (Bug 4: always green/red with opacity)
                stack.Children.Add(MakeLabel("Signal Confidence:", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left));
                lblConfBull = MakeLabel("Bull: 0%", Brushes.LimeGreen, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                lblConfBear = MakeLabel("Bear: 0%", Brushes.OrangeRed, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                var confRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                lblConfBull.Width = 125;
                lblConfBear.Width = 125;
                confRow.Children.Add(lblConfBull);
                confRow.Children.Add(lblConfBear);
                stack.Children.Add(confRow);

                stack.Children.Add(MakeSeparator());

                // P&L display (Bug 1: manual unrealized calc)
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

                // Trading hours status + toggle
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
                                    dashOrigWidth   = dashBorder.ActualWidth > 0 ? dashBorder.ActualWidth : 270;
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
                                    // Part 3b: freeze strategy selector in trade
                                    bool inTrade = openTradeDirection != 0;

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
                                    else if (btn == btnStratPrevConf)
                                    {
                                        if (!inTrade) { autoStrategy = (autoStrategy + 4) % 5; UpdateStratLabel(); }
                                    }
                                    else if (btn == btnStratNextConf)
                                    {
                                        if (!inTrade) { autoStrategy = (autoStrategy + 1) % 5; UpdateStratLabel(); }
                                    }
                                    else if (btn == btnTrailToggle)
                                    {
                                        trailEnabled = !trailEnabled;
                                        btnTrailToggle.Content = trailEnabled ? "TRAIL: ON" : "TRAIL: OFF";
                                        btnTrailToggle.Background = trailEnabled
                                            ? new SolidColorBrush(Color.FromRgb(120, 50, 140))
                                            : new SolidColorBrush(Color.FromRgb(50, 55, 65));
                                        if (!trailEnabled) { trailActive = false; trailPrice = 0; trailMaxProfitPts = 0; trailTierName = ""; pendingRemoveTrailDraw = true; }
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
                    double startX = Math.Max(10, chartGrid.ActualWidth - 290);
                    dashTranslate.X = startX;
                    dashTranslate.Y = 10;
                    chartGrid.Children.Add(dashboardPanel);
                    dashboardHostPanel = chartGrid;
                    placed = true;
                    Print("[Mm-ATM v2] Dashboard: floating overlay at (" + startX.ToString("F0") + ", 10)");
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
                            Print("[Mm-ATM v2] Dashboard: floating overlay (fallback grid)");
                            break;
                        }
                    }
                }

                if (!placed)
                {
                    Print("[Mm-ATM v2] Dashboard: FAILED to find host \u2014 will retry");
                    dashboardPanel    = null;
                    dashboardAttached = false;
                }

              }
              catch (Exception ex)
              {
                Print("[Mm-ATM v2] Dashboard build error: " + ex.Message + " \u2014 will retry");
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
            var btnMinus = MakeSmallButton("\u2212", minusHandler ?? ((s, e) => { }));
            var btnPlus  = MakeSmallButton("+",  plusHandler ?? ((s, e) => { }));
            row.Children.Add(btnMinus);
            row.Children.Add(btnPlus);
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
            if (handler != null) btn.Click += handler;
            return btn;
        }

        private Button MakeToggleButton(string text, bool active, RoutedEventHandler handler)
        {
            var btn = new Button
            {
                Content = text, Width = 115, Height = 30,
                Background = active
                    ? new SolidColorBrush(Color.FromRgb(30, 90, 160))
                    : new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0), Margin = new Thickness(2, 0, 2, 0)
            };
            if (handler != null) btn.Click += handler;
            return btn;
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
            if (lblPnL == null || lblStatus == null || lblPosition == null) return;

            // ── Capture ALL NinjaTrader data on the data thread (thread-safe) ──
            double snapClose         = Close[0];
            MarketPosition snapMktPos = Position.MarketPosition;
            int    snapPosQty        = Position.Quantity;
            double snapPosAvgPrice   = Position.AveragePrice;
            int    snapTimeVal       = ToTime(Time[0]);

            double snapUnrealized = 0;
            if (snapMktPos != MarketPosition.Flat && averageEntryPrice > 0)
            {
                double priceDiff = snapMktPos == MarketPosition.Long
                    ? snapClose - averageEntryPrice
                    : averageEntryPrice - snapClose;
                double qty = Math.Max(totalContracts, snapPosQty);
                snapUnrealized = priceDiff * NQ_DOLLARS_PER_POINT * qty;
            }

            // Snapshot all mutable strategy state used by dashboard
            int    snapDcaCount    = openDcaCount;
            int    snapTradeDir    = openTradeDirection;
            bool   snapStopsArmed  = stopsArmed;
            bool   snapPendingExit = pendingExit;
            double snapAvgEntry    = averageEntryPrice;
            double snapHiddenSL    = hiddenStopPrice;
            double snapHiddenTP    = hiddenTargetPrice;
            int    snapSlPts       = slPoints;
            int    snapTpPts       = tpPoints;
            double snapTotalContracts = totalContracts;
            double snapTrailPrice  = trailPrice;
            bool   snapTrailActive = trailActive;
            double snapTrailScore  = trailTrendScore;
            double snapTrailMaxPt  = trailMaxProfitPts;
            string snapTrailTier   = trailTierName;
            double snapTrapScore   = trapScore;
            bool   snapTrapDetect  = trapDetected;
            int    snapTrapBars    = trapBarsInTrade;
            double snapBullConf    = lastBullConfidence;
            double snapBearConf    = lastBearConfidence;
            double snapDailyPnL    = dailyRealizedPnL;
            int    snapDailyTrades = dailyTradeCount;
            bool   snapDailyLimit  = dailyLimitHit;
            bool   snapProfitHit   = dailyProfitHit;
            bool   snapEmergKill   = emergencyKillActive;
            bool   snapFlattenDone = flattenFired;
            double snapVwap        = vwapValue;
            double snapPoc         = pocLevel;
            string snapLastStrat   = lastAutoStrategyUsed;

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                if (lblPnL == null || lblStatus == null) return;

                double unrealizedPnL = snapUnrealized;

                if (lblUnrealized != null)
                {
                    lblUnrealized.Text       = "Unrealized:  " + unrealizedPnL.ToString("C2");
                    lblUnrealized.Foreground  = unrealizedPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                }

                double totalPnL = snapDailyPnL + unrealizedPnL;
                string tradeCountStr = maxTradesPerDay > 0
                    ? "  [Trades: " + snapDailyTrades + "/" + maxTradesPerDay + "]"
                    : "  [Trades: " + snapDailyTrades + "]";
                lblPnL.Text       = "Daily P&L:   " + snapDailyPnL.ToString("C2") + tradeCountStr;
                lblPnL.Foreground = snapDailyPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;

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
                        lblAccountBal.Text = "Balance:     " + bal.ToString("C2");
                        lblAccountBal.Foreground = Brushes.Gray;
                    }
                    catch { lblAccountBal.Text = "Balance:     N/A"; }
                }

                // Position status
                if (snapPendingExit)
                {
                    lblStatus.Text       = "\u25cf CLOSING... (exit pending)";
                    lblStatus.Foreground = Brushes.Yellow;
                }
                else if (snapMktPos != MarketPosition.Flat)
                {
                    string dir = snapMktPos == MarketPosition.Long ? "LONG" : "SHORT";
                    int qty = snapPosQty;
                    if (snapTradeDir != 0 && snapStopsArmed)
                    {
                        lblStatus.Text       = "\u25cf " + dir + "  \u00d7" + qty;
                        lblStatus.Foreground = snapMktPos == MarketPosition.Long ? Brushes.LimeGreen : Brushes.OrangeRed;
                    }
                    else
                    {
                        lblStatus.Text       = "\u26a0 " + dir + " \u00d7" + qty + " (state desync!)";
                        lblStatus.Foreground = Brushes.Yellow;
                    }
                    if (lblPosition != null)
                        lblPosition.Text = "Pos: " + snapDcaCount + "/" + maxContracts
                                         + "  |  Avg: " + (snapAvgEntry > 0 ? snapAvgEntry.ToString("F2") : snapPosAvgPrice.ToString("F2"));
                    if (lblHiddenSL != null)
                        lblHiddenSL.Text = snapHiddenSL > 0
                            ? "SL: " + snapHiddenSL.ToString("F2")
                              + "  (" + snapSlPts + "pt | " + (snapSlPts * 4) + "tk | " + (snapSlPts * NQ_DOLLARS_PER_POINT * Math.Max(snapTotalContracts, qty)).ToString("C0") + ")"
                            : "SL: \u2014  (stops not armed)";
                    if (lblHiddenTP != null)
                        lblHiddenTP.Text = snapHiddenTP > 0
                            ? "TP: " + snapHiddenTP.ToString("F2")
                              + "  (" + snapTpPts + "pt | " + (snapTpPts * 4) + "tk | " + (snapTpPts * NQ_DOLLARS_PER_POINT * Math.Max(snapTotalContracts, qty)).ToString("C0") + ")"
                            : "TP: \u2014  (stops not armed)";

                    // Trail info
                    if (lblTrailInfo != null)
                    {
                        if (trailEnabled && snapTrailActive && snapTrailPrice > 0)
                        {
                            double dist = snapTradeDir == 1
                                ? (snapClose - snapTrailPrice) / (TickSize * NQ_TICKS_PER_POINT)
                                : snapTradeDir == -1
                                    ? (snapTrailPrice - snapClose) / (TickSize * NQ_TICKS_PER_POINT) : 0;
                            string regime = snapTrailScore > 0.6 ? "Trend" : (snapTrailScore < 0.35 ? "Chop" : "Mix");
                            string tierStr = !string.IsNullOrEmpty(snapTrailTier) ? " " + snapTrailTier : "";
                            lblTrailInfo.Text = "Trail: " + snapTrailPrice.ToString("F2") + " (" + dist.ToString("F1") + "pt " + regime + " " + (snapTrailScore * 100).ToString("F0") + "%" + tierStr + ")";
                            if (snapTrailTier == "T3-Runner") lblTrailInfo.Foreground = Brushes.Gold;
                            else if (snapTrailTier == "T2-Strong") lblTrailInfo.Foreground = Brushes.Cyan;
                            else if (snapTrailTier == "T1-BE") lblTrailInfo.Foreground = Brushes.Yellow;
                            else lblTrailInfo.Foreground = Brushes.Magenta;
                        }
                        else if (trailEnabled && !snapTrailActive)
                        {
                            double profitPts = 0;
                            if (snapTradeDir == 1) profitPts = (snapClose - snapAvgEntry) / (TickSize * NQ_TICKS_PER_POINT);
                            else if (snapTradeDir == -1) profitPts = (snapAvgEntry - snapClose) / (TickSize * NQ_TICKS_PER_POINT);
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
                        if (trapDetectorEnabled && snapTrapDetect)
                        {
                            lblTrapInfo.Text = "Trap: DETECTED (" + snapTrapScore.ToString("F0") + "%) bars=" + snapTrapBars;
                            lblTrapInfo.Foreground = Brushes.OrangeRed;
                        }
                        else if (trapDetectorEnabled)
                        {
                            lblTrapInfo.Text = "Trap: score " + snapTrapScore.ToString("F0") + "% bars=" + snapTrapBars;
                            lblTrapInfo.Foreground = Brushes.Orange;
                        }
                        else
                        {
                            lblTrapInfo.Text = "Trap: OFF";
                            lblTrapInfo.Foreground = Brushes.Gray;
                        }
                    }

                    // Active strategy
                    if (lblActiveStrategy != null)
                        lblActiveStrategy.Text = "Active: " + (!string.IsNullOrEmpty(snapLastStrat) ? snapLastStrat : "\u2014");
                }
                else
                {
                    // Flat status
                    if (snapDailyLimit)
                    {
                        lblStatus.Text       = "\u25a0 DAILY LOSS LIMIT \u2014 halted (" + snapDailyPnL.ToString("C0") + ")";
                        lblStatus.Foreground = Brushes.OrangeRed;
                    }
                    else if (snapEmergKill)
                    {
                        lblStatus.Text       = "\u26a0 EMERGENCY KILL \u2014 halted";
                        lblStatus.Foreground = Brushes.OrangeRed;
                    }
                    else if (snapProfitHit)
                    {
                        lblStatus.Text       = "\u25a0 DAILY PROFIT TARGET \u2014 halted (" + snapDailyPnL.ToString("C0") + ")";
                        lblStatus.Foreground = Brushes.Gold;
                    }
                    else if (snapFlattenDone)
                    {
                        lblStatus.Text       = "\u25a0 EOD flatten \u2014 done for today";
                        lblStatus.Foreground = Brushes.Orange;
                    }
                    else if (maxTradesPerDay > 0 && snapDailyTrades >= maxTradesPerDay)
                    {
                        lblStatus.Text       = "\u25a0 Max trades reached (" + snapDailyTrades + "/" + maxTradesPerDay + ") \u2014 done";
                        lblStatus.Foreground = Brushes.Orange;
                    }
                    else
                    {
                        bool inAutoHours = !tradingHoursEnabled || (snapTimeVal >= tradingStartTime && snapTimeVal < tradingEndTime);
                        if (autoMode && !inAutoHours)
                        {
                            lblStatus.Text       = "\u25cb Flat \u2014 outside auto hours";
                            lblStatus.Foreground = Brushes.Gray;
                        }
                        else if (autoMode)
                        {
                            double best = Math.Max(snapBullConf, snapBearConf);
                            string dirStr = snapBullConf >= snapBearConf ? "Bull" : "Bear";
                            string cooldownStr = "";
                            if (lossCooldownSeconds > 0 && consecutiveLosses > 0)
                            {
                                double elapsed = (DateTime.Now - lastLossTime).TotalSeconds;
                                if (elapsed < lossCooldownSeconds)
                                    cooldownStr = " [CD:" + ((int)(lossCooldownSeconds - elapsed)) + "s]";
                            }
                            lblStatus.Text       = "\u25cf Flat \u2014 scanning (" + dirStr + " " + best.ToString("F0") + "% / need " + minSignalConfidence.ToString("F0") + "%)" + cooldownStr;
                            lblStatus.Foreground = best >= minSignalConfidence ? Brushes.LimeGreen : Brushes.CornflowerBlue;
                        }
                        else
                        {
                            lblStatus.Text       = "\u25cf Flat \u2014 manual mode";
                            lblStatus.Foreground = Brushes.CornflowerBlue;
                        }
                    }
                    if (lblPosition != null) lblPosition.Text = "Pos: 0/" + maxContracts + "  |  Avg: \u2014";
                    if (lblHiddenSL != null) lblHiddenSL.Text = "SL: \u2014  (" + snapSlPts + "pt | " + (snapSlPts * 4) + "tk | $" + (snapSlPts * 20) + "/ct)";
                    if (lblHiddenTP != null) lblHiddenTP.Text = "TP: \u2014  (" + snapTpPts + "pt | " + (snapTpPts * 4) + "tk | $" + (snapTpPts * 20) + "/ct)";
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
                    if (lblActiveStrategy != null) lblActiveStrategy.Text = "Active: \u2014";
                }

                // VWAP
                if (lblVwapVal != null)
                    lblVwapVal.Text = "VWAP: " + (snapVwap > 0 ? snapVwap.ToString("F2") : "\u2014")
                        + (snapPoc > 0 ? "  POC:" + snapPoc.ToString("F2") : "");

                // Bug 4 fix: Confidence colors always green/red with opacity
                if (lblConfBull != null)
                {
                    lblConfBull.Text = "Bull: " + snapBullConf.ToString("F0") + "%";
                    double bullOpacity = snapBullConf >= minSignalConfidence ? 1.0 : Math.Max(0.35, snapBullConf / minSignalConfidence);
                    lblConfBull.Foreground = Brushes.LimeGreen;
                    lblConfBull.Opacity    = bullOpacity;
                }
                if (lblConfBear != null)
                {
                    lblConfBear.Text = "Bear: " + snapBearConf.ToString("F0") + "%";
                    double bearOpacity = snapBearConf >= minSignalConfidence ? 1.0 : Math.Max(0.35, snapBearConf / minSignalConfidence);
                    lblConfBear.Foreground = Brushes.OrangeRed;
                    lblConfBear.Opacity    = bearOpacity;
                }

                // Trading hours
                if (lblTradeHours != null)
                {
                    bool inHours = !tradingHoursEnabled || (snapTimeVal >= tradingStartTime && snapTimeVal < flattenTime);
                    if (snapDailyLimit || snapEmergKill)
                    {
                        lblTradeHours.Text       = "\u25cf Daily limit hit \u2014 trading halted";
                        lblTradeHours.Foreground  = Brushes.OrangeRed;
                    }
                    else if (snapFlattenDone)
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

                // Strategy selector freeze (Part 3b)
                bool tradeFrozen = snapMktPos != MarketPosition.Flat;
                if (btnStratPrevConf != null) btnStratPrevConf.IsEnabled = !tradeFrozen;
                if (btnStratNextConf != null) btnStratNextConf.IsEnabled = !tradeFrozen;
                } // end try
                catch { } // Silently absorb any WPF/threading exceptions
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
        //  PROPERTIES  (9 groups per v2 spec)
        // ═══════════════════════════════════════════════════════════

        #region Properties

        // Group 1 — Risk Management
        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Stop Loss (NQ points)", Order = 1, GroupName = "1 — Risk Management",
                 Description = "Hidden SL distance from average entry.")]
        public int SlPoints { get { return slPoints; } set { slPoints = value; } }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Take Profit (NQ points)", Order = 2, GroupName = "1 — Risk Management",
                 Description = "Hidden TP distance from average entry.")]
        public int TpPoints { get { return tpPoints; } set { tpPoints = value; } }

        [NinjaScriptProperty]
        [Range(500, 10000)]
        [Display(Name = "Max Daily Loss ($)", Order = 3, GroupName = "1 — Risk Management",
                 Description = "Hard daily loss cap.")]
        public int MaxDailyLossDollars { get { return maxDailyLossDollars; } set { maxDailyLossDollars = value; } }

        [NinjaScriptProperty]
        [Range(500, 20000)]
        [Display(Name = "Daily Profit Target ($)", Order = 4, GroupName = "1 — Risk Management")]
        public int MaxDailyProfitDollars { get { return maxDailyProfitDollars; } set { maxDailyProfitDollars = value; } }

        [NinjaScriptProperty]
        [Range(0, 999)]
        [Display(Name = "Max Trades Per Day", Order = 5, GroupName = "1 — Risk Management",
                 Description = "0 = unlimited.")]
        public int MaxTradesPerDay { get { return maxTradesPerDay; } set { maxTradesPerDay = value; } }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Contracts per entry", Order = 6, GroupName = "1 — Risk Management")]
        public int Contracts { get { return contracts; } set { contracts = value; } }

        // Group 2 — Position Sizing (replaces DCA)
        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Max Contracts", Order = 1, GroupName = "2 — Position Sizing",
                 Description = "Maximum total contracts across all adds.")]
        public int MaxContracts { get { return maxContracts; } set { maxContracts = value; } }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "DCA Suggestion Distance (NQ pt)", Order = 2, GroupName = "2 — Position Sizing",
                 Description = "Visual DCA suggestion line distance. 0 = off. Chart line only, no auto entry.")]
        public int DcaSuggestionPoints { get { return dcaSuggestionPoints; } set { dcaSuggestionPoints = value; } }

        // Group 3 — Trading Mode
        [NinjaScriptProperty]
        [Display(Name = "Auto Mode (default)", Order = 1, GroupName = "3 — Trading Mode")]
        public bool AutoMode { get { return autoMode; } set { autoMode = value; } }

        [NinjaScriptProperty]
        [Range(0, 4)]
        [Display(Name = "Auto Strategy (0-4)", Order = 2, GroupName = "3 — Trading Mode",
                 Description = "0=Momentum+VWAP  1=Key Level  2=Liquidity Sweep  3=ORB  4=Auto Select")]
        public int AutoStrategy { get { return autoStrategy; } set { autoStrategy = value; } }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Min Signal Confidence (%)", Order = 3, GroupName = "3 — Trading Mode")]
        public double MinSignalConfidence { get { return minSignalConfidence; } set { minSignalConfidence = value; } }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "Entry Delay (seconds)", Order = 4, GroupName = "3 — Trading Mode")]
        public int EntryDelaySeconds { get { return entryDelaySeconds; } set { entryDelaySeconds = value; } }

        // Group 4 — Indicators
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

        [NinjaScriptProperty]
        [Range(10, 200)]
        [Display(Name = "HTF EMA Period", Order = 5, GroupName = "4 — Indicators",
                 Description = "Higher Time-Frame EMA period for trend filter.")]
        public int HtfEmaPeriod { get { return htfEmaPeriod; } set { htfEmaPeriod = value; } }

        // Group 5 — Display
        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "SL/TP Adjust Step (points)", Order = 1, GroupName = "5 — Display")]
        public int SlTpAdjustStep { get { return slTpAdjustStep; } set { slTpAdjustStep = value; } }

        [NinjaScriptProperty]
        [Range(10, 95)]
        [Display(Name = "Jump SL % (toward price)", Order = 2, GroupName = "5 — Display")]
        public int JumpSlPercent { get { return jumpSlPercent; } set { jumpSlPercent = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA on chart", Order = 3, GroupName = "5 — Display")]
        public bool ShowEma { get { return showEma; } set { showEma = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show RSI panel", Order = 4, GroupName = "5 — Display")]
        public bool ShowRsi { get { return showRsi; } set { showRsi = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show ATR panel", Order = 5, GroupName = "5 — Display")]
        public bool ShowAtr { get { return showAtr; } set { showAtr = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP line", Order = 6, GroupName = "5 — Display")]
        public bool ShowVwap { get { return showVwap; } set { showVwap = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show Key Levels", Order = 7, GroupName = "5 — Display")]
        public bool ShowKeyLevels { get { return showKeyLevels; } set { showKeyLevels = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show Sweep Signals", Order = 8, GroupName = "5 — Display")]
        public bool ShowSweepSignals { get { return showSweepSignals; } set { showSweepSignals = value; } }

        // Group 6 — Trading Hours
        [NinjaScriptProperty]
        [Display(Name = "Enable Trading Hours Filter", Order = 0, GroupName = "6 — Trading Hours",
                 Description = "When OFF, auto entries can fire at any time. Toggleable on dashboard.")]
        public bool TradingHoursEnabled { get { return tradingHoursEnabled; } set { tradingHoursEnabled = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trading Start (HHMMSS ET)", Order = 1, GroupName = "6 — Trading Hours")]
        public int TradingStartTime { get { return tradingStartTime; } set { tradingStartTime = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trading End (HHMMSS ET)", Order = 2, GroupName = "6 — Trading Hours")]
        public int TradingEndTime { get { return tradingEndTime; } set { tradingEndTime = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Auto-Flatten Time (HHMMSS ET)", Order = 3, GroupName = "6 — Trading Hours")]
        public int FlattenTime { get { return flattenTime; } set { flattenTime = value; } }

        // Group 7 — Adaptive Trail
        [NinjaScriptProperty]
        [Display(Name = "Enable Adaptive Trail", Order = 1, GroupName = "7 — Adaptive Trail")]
        public bool TrailEnabled { get { return trailEnabled; } set { trailEnabled = value; } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trail Activation (NQ points)", Order = 2, GroupName = "7 — Adaptive Trail")]
        public int TrailActivationPoints { get { return trailActivationPoints; } set { trailActivationPoints = value; } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trail Min Distance (NQ points)", Order = 3, GroupName = "7 — Adaptive Trail")]
        public int TrailMinPoints { get { return trailMinPoints; } set { trailMinPoints = value; } }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "Trail Max Distance (NQ points)", Order = 4, GroupName = "7 — Adaptive Trail")]
        public int TrailMaxPoints { get { return trailMaxPoints; } set { trailMaxPoints = value; } }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Trail ATR Multiplier", Order = 5, GroupName = "7 — Adaptive Trail")]
        public double TrailAtrMultiplier { get { return trailAtrMultiplier; } set { trailAtrMultiplier = value; } }

        // Group 8 — Trap Detector
        [NinjaScriptProperty]
        [Display(Name = "Enable Post-Entry Trap Detector", Order = 1, GroupName = "8 — Trap Detector",
                 Description = "Monitor for adverse price action after entry. Dashboard shows trap score and alert.")]
        public bool EnablePostEntryTrapDetector { get { return enablePostEntryTrapDetector; } set { enablePostEntryTrapDetector = value; } }

        [NinjaScriptProperty]
        [Range(0, 10)]
        [Display(Name = "Volatility Spike Guard Bars", Order = 2, GroupName = "8 — Trap Detector",
                 Description = "Skip trail updates when bar range > 3×ATR. 0 = disabled.")]
        public int VolatilitySpikeGuardBars { get { return volatilitySpikeGuardBars; } set { volatilitySpikeGuardBars = value; } }

        // Group 9 — Signal Quality (v2 new)
        [NinjaScriptProperty]
        [Display(Name = "HTF Trend Filter", Order = 1, GroupName = "9 — Signal Quality",
                 Description = "Reduce confidence when signal opposes higher time-frame EMA trend.")]
        public bool HtfFilterEnabled { get { return htfFilterEnabled; } set { htfFilterEnabled = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Volume Profile Filters", Order = 2, GroupName = "9 — Signal Quality",
                 Description = "Use real-time POC/VAH/VAL for mid-range penalty and bounce support.")]
        public bool UseVolumeProfileFilters { get { return useVolumeProfileFilters; } set { useVolumeProfileFilters = value; } }

        [NinjaScriptProperty]
        [Range(0, 600)]
        [Display(Name = "Loss Cooldown (seconds)", Order = 3, GroupName = "9 — Signal Quality",
                 Description = "Pause auto entries after consecutive losses. 0 = disabled.")]
        public int LossCooldownSeconds { get { return lossCooldownSeconds; } set { lossCooldownSeconds = value; } }

        #endregion
    }
}
