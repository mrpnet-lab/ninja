// ============================================================
//  Mm-ATM Strategy for NinjaTrader 8
//  Version : 2.0  (Smart Signal Filters + Profit-Tier Trail)
//  Author  : Custom Build — NQ Semi-Auto / Auto Trader
//
//  Based on NQ MM-Trap v2.1 with Intelligent Adaptive Trailing Stop.
//  v2.0 adds trap detection, volume filters, breakeven lock, and
//  profit-tiered trailing to avoid giving back gains.
//
// ─────────────────────────────────────────────────────────────
//  CORE FEATURES
// ─────────────────────────────────────────────────────────────
//  - Floating overlay dashboard (drag title bar, resize grip)
//  - Buy Mkt / Sell Mkt / Buy Lmt / Sell Lmt buttons
//  - Close 1 (partial close), Close Trade, Emergency Kill
//  - Jump SL button (move SL closer by configurable %)
//  - Adjustable hidden SL/TP via +/- buttons (live mid-trade)
//  - Auto / Manual mode toggle on dashboard
//  - Auto strategy selector (5 modes: 0-3 + Auto Select)
//  - Adjustable order qty on dashboard
//  - Unrealized + Daily P&L + trade count display
//  - Confidence score display for selected auto strategy
//  - EMA / RSI / ATR / VWAP indicators on chart (toggleable)
//  - Liquidity sweep reversal markers on chart
//  - Key level breakout lines on chart
//  - DCA: press same-direction button to add (up to max)
//  - SL/TP reset to defaults after each trade closes
//
// ─────────────────────────────────────────────────────────────
//  ADAPTIVE TRAILING STOP — THE MAIN ADDITION
// ─────────────────────────────────────────────────────────────
//
//  ★ WHAT IT DOES
//  A hidden (no resting orders) trailing stop that dynamically
//  adjusts its distance from price based on real-time market
//  conditions. It aims to:
//    • Lock in gains quickly in choppy/ranging markets
//    • Give trades room to run in trending markets
//    • Fire a market order exit the instant price touches it
//
//  ★ HOW IT ACTIVATES
//  The trail stays dormant until unrealized profit reaches a
//  configurable threshold (default: 8 NQ points = $160/ct).
//  This prevents premature trailing on normal noise after entry.
//  Once activated, the trail NEVER deactivates until the trade
//  closes — it only ratchets in favor of the trade.
//
//  ★ REGIME DETECTION (Trend Score 0–100%)
//  Every tick, the strategy scores the market on a 0–100% scale:
//
//    Factor 1 — ATR Expansion (30% weight)
//      Compares current ATR to its 10-bar average.
//      Rising ATR → expanding volatility → likely trending.
//      Flat/falling ATR → contracting → likely ranging.
//      Score: (currentATR / avgATR - 0.8) / 0.6, clamped 0–1.
//
//    Factor 2 — EMA Spread (30% weight)
//      Measures fast/slow EMA gap relative to ATR.
//      Wide gap → strong directional move → trending.
//      Narrow gap → indecision → choppy.
//      Score: |EMA_fast - EMA_slow| / ATR / 3.0, clamped 0–1.
//
//    Factor 3 — Directional Bars (25% weight)
//      Counts last 8 bars moving in the trade direction.
//      8/8 in direction → strong trend. 4/8 → mixed.
//      Score: directional_bars / total_bars.
//
//    Factor 4 — RSI Extremity (15% weight)
//      Measures how far RSI is from neutral 50.
//      RSI at 75 or 25 → directional conviction → trending.
//      RSI near 50 → no conviction → choppy.
//      Score: |RSI - 50| / 50 × 1.5, clamped 0–1.
//
//    Composite: 0.30×ATR + 0.30×EMA + 0.25×Dir + 0.15×RSI
//
//  ★ TRAIL DISTANCE CALCULATION
//  The trend score maps to a trail distance in NQ points:
//
//    1. Regime distance = lerp(minPoints, maxPoints, trendScore)
//       - Score 0% (pure chop) → uses trailMinPoints (default 4)
//       - Score 100% (strong trend) → uses trailMaxPoints (default 25)
//
//    2. ATR-scaled distance = ATR × trailAtrMultiplier / ticksPerPoint
//       - Adapts to the current volatility amplitude
//
//    3. Final distance = average(regimeDist, atrDist), clamped to [min, max]
//
//  Example scenarios (1 contract, NQ):
//    Choppy market (score 20%) → trail ~5 pts ($100 from price)
//    Mixed market  (score 50%) → trail ~12 pts ($240 from price)
//    Strong trend  (score 85%) → trail ~22 pts ($440 from price)
//
//  ★ RATCHET BEHAVIOR
//  - Long trades:  trail moves UP only (new = max(old, price - dist))
//  - Short trades: trail moves DOWN only (new = min(old, price + dist))
//  - Trail is recalculated every tick, but NEVER moves against you
//  - As trend score changes, the distance adapts, but the trail
//    price itself can only improve
//
//  ★ EXIT MECHANICS
//  - When price touches or crosses the trail → market exit fires
//  - Exit signal name: "Close" (same as manual trading)
//  - Entry signal name: DCA count ("1", "2", etc.)
//  - Sets pendingExit=true, same flow as hidden SL/TP exits
//
//  ★ INTERACTION WITH FIXED SL/TP
//  Both systems run simultaneously on every tick:
//    - Fixed SL/TP = hard safety net (always armed, never moves*)
//    - Adaptive trail = profit-maximizing layer
//  Whichever is hit first triggers the exit. In practice:
//    - Losing trade → fixed SL fires (trail never activated)
//    - Small winner → fixed TP may fire before trail activated
//    - Big winner → trail activates, ratchets up, locks gain
//    - Trail often exits BETTER than the fixed TP because it
//      follows the move and captures excess profit
//  * Note: SL/TP can be adjusted via dashboard +/- or Jump SL
//
//  ★ DASHBOARD DISPLAY
//  A row below SL/TP shows:
//    [TRAIL: ON]  Trail: 21345.50 (8.2pt Trend 72%)
//    [TRAIL: OFF] Trail: OFF
//  States:
//    - "Trail: —"            = enabled, flat (no trade)
//    - "Trail: waiting (X/8pt)" = in trade, below activation threshold
//    - "Trail: 21345 (Xpt Regime %)" = active, showing distance+regime
//    - "Trail: OFF"          = disabled via toggle or parameter
//
//  ★ CHART VISUALIZATION
//  When trail is active:
//    - Magenta dash-dot line at trail price
//    - Text label: "Trail 21345.50 (8.2pt | Trending 72%)"
//  Lines auto-remove when trade closes or trail disabled.
//
//  ★ CONFIGURABLE PARAMETERS (Group 7 — Adaptive Trail)
//    TrailEnabled          (bool)   ON/OFF (default: true)
//    TrailActivationPoints (int)    Profit threshold (default: 8 pts)
//    TrailMinPoints        (int)    Tightest distance (default: 4 pts)
//    TrailMaxPoints        (int)    Widest distance (default: 25 pts)
//    TrailAtrMultiplier    (double) ATR scale factor (default: 1.5)
//
// ─────────────────────────────────────────────────────────────
//  v2.0 — SMART SIGNAL FILTERS (Trap Detection)
// ─────────────────────────────────────────────────────────────
//  Post-processing layer applied after EVERY signal calculation.
//  Prevents entries into common NQ traps and low-quality setups.
//
//  Filter 1: Volume Confirmation
//    - Compares current bar volume to 20-bar average
//    - <60% of avg → 35% penalty (thin breakouts reverse)
//    - <85% of avg → 15% penalty (below-average conviction)
//    - >150% of avg → 10% bonus (strong institutional flow)
//
//  Filter 2: Bull/Bear Conflict (Whipsaw Rejection)
//    - When both bull and bear confidence are high (>65% ratio)
//    - Market is indecisive → both signals penalized
//    - Prevents chop-induced back-to-back losing entries
//
//  Filter 3: Momentum Exhaustion
//    - Tracks price position within 20-bar range
//    - If price is >82% into the range AND moved >2× ATR
//    - Penalizes same-direction entries (chasing a spent move)
//
//  Filter 4: Bar Quality (Wick Rejection)
//    - Upper wick >45% of bar → penalizes bullish signals
//    - Lower wick >45% of bar → penalizes bearish signals
//    - Detects rejection/trap candles at key levels
//
//  Strategy-level enhancements:
//    - Momentum+VWAP: bar conviction bonus on VWAP cross
//    - Key Level Breakout: fake-out filter (close must be near
//      extreme for full credit; weak close = half credit)
//    - Liq Sweep Reversal: volume spike required for full credit
//    - ORB: conviction filter + ORB range quality scaling
//
// ─────────────────────────────────────────────────────────────
//  v2.0 — PROFIT-TIER TRAILING STOP
// ─────────────────────────────────────────────────────────────
//  Tracks the max profit reached during each trade and enforces
//  progressively tighter floor prices to prevent giving back gains.
//
//  Tier 1 (T1-BE): profit ≥ 2× activation (default 16pt)
//    → Trail can never go below breakeven (entry + 1 tick)
//    → You'll never lose money on a trade that was +16pts
//
//  Tier 2 (T2-Strong): profit ≥ 2.5× activation (default 20pt)
//    → Trail floor = 45% of max profit above entry
//    → E.g. max profit 24pts → floor at +10.8pts ($216/ct)
//
//  Tier 3 (T3-Runner): profit ≥ 4× activation (default 32pt)
//    → Trail floor = 55% of max profit above entry
//    → E.g. max profit 40pts → floor at +22pts ($440/ct)
//
//  Dashboard shows tier name + color-coded trail info:
//    Active = Magenta, T1-BE = Yellow, T2-Strong = Cyan,
//    T3-Runner = Gold
//
// ─────────────────────────────────────────────────────────────
//  RISK MANAGEMENT
// ─────────────────────────────────────────────────────────────
//  - Hidden SL/TP (no resting orders on exchange)
//  - Adaptive trailing stop (regime-aware, hidden)
//  - Max daily loss $ — flattens and halts trading
//  - Daily profit target $ — flattens and halts trading
//  - Max trades per day (default 4, 0 = unlimited)
//  - CME maintenance window block (4:55-5:59 PM ET)
//  - Auto-flatten at configurable time (default 3:59 PM ET)
//
// ─────────────────────────────────────────────────────────────
//  AUTO STRATEGIES (0-4)
// ─────────────────────────────────────────────────────────────
//  Each strategy scores confidence 0-100% with two tiers:
//    - Crossover signals  = full credit (first entry)
//    - Continuation signals = partial credit (re-entry)
//
//  0: Momentum + VWAP
//     Crossover: price crosses VWAP (+35%), EMA align (+30),
//                RSI 50-75 (+20), momentum vs prior bar (+15)
//     Continuation: price stays above/below VWAP (+20)
//
//  1: Key Level Breakout
//     Crossover: price breaks 20-bar high/low (+50),
//                ATR confirmation (+30), EMA align (+20)
//     Continuation: price holds above/below level (+25)
//
//  2: Liquidity Sweep Reversal
//     Crossover: sweep + snap-back on same bar (+45),
//                RSI confirmation (+30), ATR snapback (+25)
//     Continuation: recent sweep within 5 bars (+25)
//
//  3: Opening Range Breakout (9:45 AM - 11:30 AM ET)
//     Crossover: price breaks ORB high/low (+55),
//                EMA align (+25), ATR > 0 (+20)
//     Continuation: price holds above/below ORB (+30)
//
//  4: Auto Select — evaluates all 4, picks highest confidence
//
// ─────────────────────────────────────────────────────────────
//  DASHBOARD STATUS (when flat)
// ─────────────────────────────────────────────────────────────
//  - "■ DAILY LOSS LIMIT"      = loss cap hit, halted
//  - "■ DAILY PROFIT TARGET"   = profit cap hit, halted
//  - "■ Max trades reached"    = trade limit hit, done
//  - "■ EOD flatten"           = auto-flatten fired
//  - "○ Flat — outside hours"  = before/after trading window
//  - "● Flat — scanning (X%)"  = auto mode, showing confidence
//  - "● Flat — manual mode"    = waiting for button press
//
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
    public class Mm_ATM : Strategy
    {
        #region Fields

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
        private int    jumpSlPercent;       // % jump toward current price (default 50)
        private int    maxTradesPerDay;     // max auto+manual round-trip trades per session
        private int    dailyTradeCount;     // completed round-trip trades today
        private bool   showEma;
        private bool   showRsi;
        private bool   showAtr;
        private bool   showVwap;
        private bool   showKeyLevels;
        private bool   showSweepSignals;
        private int    tradingStartTime;     // HHMMSS ET
        private int    tradingEndTime;       // HHMMSS ET
        private int    flattenTime;          // HHMMSS ET — auto-flatten
        private bool   flattenFired;         // prevent multiple flatten calls per session

        // ─── Indicator references ─────────────────────────────────
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaFast;
        private NinjaTrader.NinjaScript.Indicators.EMA indEmaSlow;
        private NinjaTrader.NinjaScript.Indicators.RSI indRsi;
        private NinjaTrader.NinjaScript.Indicators.ATR indAtr;

        // ─── Session / daily tracking ─────────────────────────────
        private double   dailyRealizedPnL;
        private bool     dailyLimitHit;
        private bool     dailyProfitHit;
        private DateTime sessionDate;

        // ─── Hidden SL/TP state ───────────────────────────────────
        private double hiddenStopPrice;
        private double hiddenTargetPrice;
        private bool   stopsArmed;
        private int    openTradeDirection; // 1=long, -1=short, 0=flat
        private int    defaultSlPoints;    // saved from SetDefaults for reset
        private int    defaultTpPoints;    // saved from SetDefaults for reset

        // ─── DCA tracking ─────────────────────────────────────────
        private int    openDcaCount;
        private double averageEntryPrice;
        private double totalContracts;
        private int    tradeSequence;       // round-trip trade counter (for internal logging)
        private int    orderCounter;        // session-wide entry counter → used as signal name ("1","2",...)
        private readonly List<string> activeEntrySignals = new List<string>(); // actual signal names used

        // ─── Manual VWAP (tick-safe) ──────────────────────────────
        private double vwapCumTPV;          // cumulative for completed bars
        private double vwapCumVol;
        private double vwapValue;
        private double prevBarVwap;
        private double currBarTPV;          // current bar running contribution
        private double currBarVol;

        // ─── Thread-safe button flags & cooldown ──────────────────
        private bool     pendingLong;
        private bool     pendingShort;
        private bool     pendingLongLimit;
        private bool     pendingShortLimit;
        private bool     pendingFlatten;
        private bool     pendingCloseTrade;
        private bool     pendingCloseOne;
        private bool     pendingJumpSL;
        private bool     pendingRearm;      // re-arm stops after SL/TP change
        private bool     pendingExit;       // exit orders submitted, waiting for fill
        private bool     pendingLimitFlatten;  // deferred flatten from OnExecutionUpdate (daily limit)
        private int      pendingExitTicks;  // ticks since pendingExit became true (safety net)
        private int      flatSyncGraceTicks; // grace ticks for entry order to fill before state reset
        private DateTime lastEntryWallTime;  // cooldown reference: DateTime.Now in Realtime, Time[0] in Historical
        private bool     firstBarSeen;

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
        private Button    btnBuyLmt;
        private Button    btnSellLmt;
        private Button    btnCloseTrade;
        private Button    btnCloseOne;       // partial close 1 contract
        private Button    btnExit;           // Emergency Kill
        private Button    btnJumpSL;
        private Button    btnModeManual;
        private Button    btnModeAuto;
        private TextBlock lblStatus;
        private TextBlock lblPnL;
        private TextBlock lblUnrealized;
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
        private int       trailActivationPoints;   // profit (NQ points) before trail activates
        private int       trailMinPoints;          // tightest trail distance (choppy/ranging)
        private int       trailMaxPoints;          // widest trail distance (trending)
        private double    trailAtrMultiplier;      // scale factor for ATR-based trail
        private double    trailPrice;              // current adaptive trail stop price
        private bool      trailActive;             // trail has been activated this trade
        private double    trailTrendScore;         // 0.0 (ranging) to 1.0 (trending)
        private double    trailMaxProfitPts;      // max profit reached this trade (for breakeven/tiers)
        private string    trailTierName;           // current tier name for dashboard display
        private TextBlock lblTrailInfo;            // dashboard trail info label
        private Button    btnTrailToggle;          // dashboard trail on/off button

        // ─── Smart Signal Filters (trap detection) ────────────────
        private double lastRawBull;   // raw bull confidence before filters
        private double lastRawBear;   // raw bear confidence before filters

        #endregion

        // ═══════════════════════════════════════════════════════════
        //  STATE MANAGEMENT
        // ═══════════════════════════════════════════════════════════

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description  = "Mm-ATM v2.0 — Smart Signal Filters, Profit-Tier Trail, Hidden SL/TP, Dashboard";
                Name         = "Mm_ATM";
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
                dcaDistancePoints     = 0;   // 0 = no distance requirement
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
                maxTradesPerDay       = 4;
                showEma               = true;
                showRsi               = true;
                showAtr               = true;
                showVwap              = true;
                showKeyLevels         = true;
                showSweepSignals      = true;
                tradingStartTime      = 93000;   // 9:30:00 AM ET
                tradingEndTime        = 160000;  // 4:00:00 PM ET
                flattenTime           = 155900;  // 3:59:00 PM ET

                // ─── Adaptive Trailing Stop defaults ──────────────
                trailEnabled          = true;
                trailActivationPoints = 8;     // activate after 8 NQ pts profit
                trailMinPoints        = 3;     // tightest: 3 pts ($60/ct) in chop
                trailMaxPoints        = 25;    // widest: 25 pts ($500/ct) in trend
                trailAtrMultiplier    = 1.5;   // ATR multiplier for dynamic distance
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
            }
            else if (State == State.Realtime)
            {
                // Reset daily limits on Historical → Realtime transition.
                // In playback mode, disabling and re-enabling replays all historical
                // bars which re-triggers auto trades and re-increments dailyTradeCount.
                // Without this reset, re-enabling after max trades just hits the limit
                // again immediately. dailyRealizedPnL is recalculated from
                // SystemPerformance.AllTrades on each fill, so it self-corrects.
                dailyTradeCount  = 0;
                dailyRealizedPnL = 0;
                dailyLimitHit    = false;
                dailyProfitHit   = false;
                flattenFired     = false;
                Print("State.Realtime: daily limits reset (tradeCount=0, PnL=$0, limits cleared)");
            }
            else if (State == State.Terminated)
            {
                RemoveDashboard();
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  MAIN UPDATE LOOP
        // ═══════════════════════════════════════════════════════════

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            // ═════ CRITICAL PATH — lowest latency ═════════════════
            // 1. Close trade / emergency kill
            if ((State == State.Realtime || State == State.Historical) && pendingCloseTrade)
            {
                pendingCloseTrade = false;
                ExecuteCloseTrade();
            }
            if ((State == State.Realtime || State == State.Historical) && pendingFlatten)
            {
                pendingFlatten = false;
                ExecuteFlatten();
            }

            // 2. Position state sync — careful NOT to reset while entry order is pending
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (pendingExit)
                {
                    // Exit was pending and position is now flat — expected, reset immediately
                    Print(Time[0] + " | Position sync: exit confirmed FLAT, resetting state");
                    ResetPositionState();
                    flatSyncGraceTicks = 0;
                }
                else if (openTradeDirection != 0)
                {
                    // We submitted an entry but it hasn't filled yet (order pending).
                    // Give the order time to fill before assuming it was rejected.
                    flatSyncGraceTicks++;
                    if (flatSyncGraceTicks > 30)
                    {
                        Print(Time[0] + " | SAFETY: still flat after " + flatSyncGraceTicks
                            + " ticks — entry likely rejected, resetting state");
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

            // 3. Block entries while exit is pending + stale exit safety net
            if (pendingExit)
            {
                if (pendingLong)  { pendingLong  = false; UpdateDashboardStatus("⚠ LONG blocked: exit pending", Brushes.Orange); }
                if (pendingShort) { pendingShort = false; UpdateDashboardStatus("⚠ SHORT blocked: exit pending", Brushes.Orange); }

                pendingExitTicks++;
                // Safety net: if exit has been pending for 50+ ticks and still not flat, nuke
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

            // 4. Button entries (manual BUY/SELL MKT/LMT + partial close + jump SL)
            if ((State == State.Realtime || State == State.Historical) && !pendingExit)
            {
                if (pendingLong)       { pendingLong       = false; ExecuteLongEntry(true); }
                if (pendingShort)      { pendingShort      = false; ExecuteShortEntry(true); }
                if (pendingLongLimit)  { pendingLongLimit  = false; ExecuteLongLimitEntry(); }
                if (pendingShortLimit) { pendingShortLimit = false; ExecuteShortLimitEntry(); }
                if (pendingCloseOne)   { pendingCloseOne   = false; ExecuteCloseOne(); }
                if (pendingJumpSL)     { pendingJumpSL     = false; ExecuteJumpSL(); }
                if (pendingRearm) { pendingRearm = false; if (stopsArmed) ArmHiddenStops(); }
            }

            // 5. Hidden SL/TP monitor — every tick
            if (stopsArmed && !pendingExit && Position.MarketPosition != MarketPosition.Flat)
                MonitorHiddenStops();

            // 5b. Adaptive trailing stop monitor — every tick
            if (trailEnabled && !pendingExit && Position.MarketPosition != MarketPosition.Flat)
                MonitorAdaptiveTrail();

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

            // ─── Build dashboard on first real-time bar ───────────
            if ((State == State.Realtime || State == State.Historical) && !dashboardAttached)
                BuildDashboard();

            // ─── Auto-flatten & CME maintenance ──────────────────
            int currentTime = ToTime(Time[0]);
            bool insideTradingHours = currentTime >= tradingStartTime && currentTime < tradingEndTime;

            // Auto-flatten at flattenTime — auto mode only
            if (autoMode && currentTime >= flattenTime && !flattenFired && Position.MarketPosition != MarketPosition.Flat)
            {
                flattenFired = true;
                Print(Time[0] + " | AUTO-FLATTEN at " + Time[0].ToString("HH:mm:ss") + " — auto mode cutoff");
                ExecuteFlatten();
            }

            // Hard flatten ALL positions before CME daily close (4:55 PM ET) — manual AND auto
            if (currentTime >= 165500 && currentTime < 180000 && Position.MarketPosition != MarketPosition.Flat)
            {
                Print(Time[0] + " | CME SESSION CLOSE FLATTEN at " + Time[0].ToString("HH:mm:ss") + " — closing all positions");
                ExecuteFlatten();
            }

            // ─── Deferred daily limit flatten (from OnExecutionUpdate) ─
            // NEVER call ExecuteFlatten inside OnExecutionUpdate — NT8 holds
            // an internal lock during execution callbacks and Account.Flatten
            // tries to acquire the same lock → deadlock → NinjaTrader freeze.
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

            // ─── Daily limit guard ────────────────────────────────
            if (dailyLimitHit)
                UpdateDashboardStatus("DAILY LOSS LIMIT — NO TRADES", Brushes.OrangeRed);

            // ─── Calculate signals + auto entry ───────────────────
            CalculateSignals();

            if (autoMode && !dailyLimitHit && !dailyProfitHit && insideTradingHours
                && Position.MarketPosition == MarketPosition.Flat && openTradeDirection == 0
                && (maxTradesPerDay <= 0 || dailyTradeCount < maxTradesPerDay))
            {
                if (lastBullConfidence >= minSignalConfidence)
                    ExecuteLongEntry(false);
                else if (lastBearConfidence >= minSignalConfidence)
                    ExecuteShortEntry(false);
            }
            // Diagnostic: log why auto entry didn't fire (every 100 bars to avoid spam)
            else if (autoMode && CurrentBar % 100 == 0 && Position.MarketPosition == MarketPosition.Flat)
            {
                string reason = "";
                if (dailyLimitHit) reason = "dailyLossLimit";
                else if (dailyProfitHit) reason = "dailyProfitHit";
                else if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay) reason = "maxTradesPerDay(" + dailyTradeCount + "/" + maxTradesPerDay + ")";
                else if (!insideTradingHours) reason = "outsideHours";
                else if (openTradeDirection != 0) reason = "openTradeDir=" + openTradeDirection;
                else reason = "lowConfidence(Bull=" + lastBullConfidence.ToString("F0") + "% Bear=" + lastBearConfidence.ToString("F0") + "% need=" + minSignalConfidence.ToString("F0") + "%)";
                Print(Time[0] + " | AUTO-SCAN: no entry — " + reason + " | Trades=" + dailyTradeCount + "/" + maxTradesPerDay + " | DailyPnL=" + dailyRealizedPnL.ToString("C0"));
            }

            // ─── Chart & dashboard (non-latency path) ─────────────
            UpdateOrbLevels();
            DrawChartAnnotations();
            DrawDcaLevels();
            DrawVwapLine();
            DrawKeyLevels();
            DrawSweepSignals();
            UpdateDashboard();
        }

        // ═══════════════════════════════════════════════════════════
        //  HIDDEN SL/TP MONITOR
        // ═══════════════════════════════════════════════════════════

        private void MonitorHiddenStops()
        {
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

            double price = Close[0];
            double profitPoints = 0;

            if (openTradeDirection == 1)
                profitPoints = (price - averageEntryPrice) / (TickSize * 4.0);
            else if (openTradeDirection == -1)
                profitPoints = (averageEntryPrice - price) / (TickSize * 4.0);

            // Track max profit for breakeven lock & profit tiers
            if (profitPoints > trailMaxProfitPts)
                trailMaxProfitPts = profitPoints;

            // Activate trailing only after reaching profit threshold
            if (!trailActive)
            {
                if (profitPoints >= trailActivationPoints)
                {
                    trailActive = true;
                    trailTierName = "Active";
                    double initDist = CalculateTrailDistance();
                    double distOff  = initDist * 4.0 * TickSize;
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

            // Calculate adaptive trail distance based on market regime
            double trailDist = CalculateTrailDistance();
            double trailOff  = trailDist * 4.0 * TickSize;

            var signals = activeEntrySignals.ToList();
            if (signals.Count == 0) return;

            // ── Profit Tier System ────────────────────────────────
            // As profit grows, enforce progressively tighter floor prices
            // to prevent giving back large gains. The trail can NEVER move
            // below these floors.
            double tierFloor = 0;
            double tickPt = TickSize * 4.0;  // 1 NQ point in price terms

            if (trailMaxProfitPts >= trailActivationPoints * 4)
            {
                // Tier 3: Runner mode (32+ pts default) — lock 55% of max profit
                tierFloor = 0.55 * trailMaxProfitPts * tickPt;
                trailTierName = "T3-Runner";
            }
            else if (trailMaxProfitPts >= trailActivationPoints * 2.5)
            {
                // Tier 2: Strong profit (20+ pts default) — lock 45% of max profit
                tierFloor = 0.45 * trailMaxProfitPts * tickPt;
                trailTierName = "T2-Strong";
            }
            else if (trailMaxProfitPts >= trailActivationPoints * 2)
            {
                // Tier 1: Breakeven lock (16+ pts default) — never let it go red
                tierFloor = TickSize;  // 1 tick above entry
                trailTierName = "T1-BE";
            }
            else
            {
                trailTierName = "Active";
            }

            if (openTradeDirection == 1)
            {
                // Ratchet trail UP only (never moves down for longs)
                double newTrail = Math.Round((price - trailOff) / TickSize) * TickSize;
                if (newTrail > trailPrice)
                    trailPrice = newTrail;

                // Enforce tier floor: trail can never drop below entry + tierFloor
                double floorPrice = Math.Round((averageEntryPrice + tierFloor) / TickSize) * TickSize;
                if (tierFloor > 0 && trailPrice < floorPrice)
                    trailPrice = floorPrice;

                // Check if trail hit
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
                // Ratchet trail DOWN only (never moves up for shorts)
                double newTrail = Math.Round((price + trailOff) / TickSize) * TickSize;
                if (newTrail < trailPrice)
                    trailPrice = newTrail;

                // Enforce tier floor: trail can never rise above entry - tierFloor
                double floorPrice = Math.Round((averageEntryPrice - tierFloor) / TickSize) * TickSize;
                if (tierFloor > 0 && trailPrice > floorPrice)
                    trailPrice = floorPrice;

                // Check if trail hit
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

        /// <summary>
        /// Calculate adaptive trail distance in NQ points based on market regime.
        /// Trending = wider (let profits run), Choppy = tighter (lock gains).
        /// </summary>
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

            // Factor 1: ATR contraction/expansion (25%) — falling ATR = ranging
            double atrSum = 0;
            int lookback = Math.Min(10, CurrentBar);
            for (int i = 0; i < lookback; i++)
                atrSum += indAtr[i];
            double atrAvg = atrSum / lookback;
            double atrExpansion = atrAvg > 0 ? (atr / atrAvg) : 1.0;
            // Shifted range: <0.9 = contracting (chop), >1.2 = expanding (trend)
            double atrScore = Math.Min(1.0, Math.Max(0.0, (atrExpansion - 0.9) / 0.5));

            // Factor 2: EMA spread (25%) — wider gap = stronger trend
            double emaSpread = Math.Abs(indEmaFast[0] - indEmaSlow[0]) / atr;
            double emaScore = Math.Min(1.0, emaSpread / 2.5);  // tighter threshold

            // Factor 3: Directional consistency (25%) — bars moving in trade dir
            int dirBars = 0;
            int checkBars = Math.Min(8, CurrentBar - 1);
            for (int i = 0; i < checkBars; i++)
            {
                if (openTradeDirection == 1 && Close[i] > Close[i + 1]) dirBars++;
                else if (openTradeDirection == -1 && Close[i] < Close[i + 1]) dirBars++;
            }
            double dirScore = checkBars > 0 ? (double)dirBars / checkBars : 0.5;

            // Factor 4: RSI extremity (10%) — away from 50 = directional
            double rsiDist = Math.Abs(indRsi[0] - 50.0) / 50.0;
            double rsiScore = Math.Min(1.0, rsiDist * 1.5);

            // Factor 5: Range compression (15%) — NEW: bar range vs ATR
            // Small bars relative to ATR = choppy/indecisive
            double barRange = High[0] - Low[0];
            double rangeRatio = atr > 0 ? barRange / atr : 1.0;
            double rangeScore = Math.Min(1.0, Math.Max(0.0, (rangeRatio - 0.3) / 0.9));

            // Weighted composite — more sensitive to chop signals
            trailTrendScore = atrScore * 0.25 + emaScore * 0.25 + dirScore * 0.25 + rsiScore * 0.10 + rangeScore * 0.15;
            trailTrendScore = Math.Min(1.0, Math.Max(0.0, trailTrendScore));

            // Chop squeeze: when trend score is very low, apply exponential
            // tightening to make the trail snap much closer to price
            // score 0.0 → multiplier 0.5 (half of min), score 0.3 → ~0.85, score 0.5+ → 1.0
            double chopMultiplier = trailTrendScore < 0.5
                ? 0.5 + trailTrendScore  // linear ramp: 0→0.5, 0.5→1.0
                : 1.0;

            // ATR-scaled dynamic distance (in NQ points)
            double atrDist = atr * trailAtrMultiplier / (TickSize * 4.0);

            // Blend regime-based range with ATR scaling
            double regimeDist = LerpD(trailMinPoints, trailMaxPoints, trailTrendScore);
            double finalDist  = (regimeDist * 0.6 + atrDist * 0.4) * chopMultiplier;

            // Clamp to user-defined min/max
            finalDist = Math.Max(trailMinPoints, Math.Min(trailMaxPoints, finalDist));
            return finalDist;
        }

        private double LerpD(double a, double b, double t)
        {
            return a + (b - a) * t;
        }

        // ═══════════════════════════════════════════════════════════
        //  ENTRY / EXIT
        // ═══════════════════════════════════════════════════════════

        private void ExecuteLongEntry(bool isManual = false)
        {
            if (pendingExit) { Print("Blocked LONG: exit pending"); UpdateDashboardStatus("⚠ LONG blocked: exit pending", Brushes.Orange); return; }
            if (dailyLimitHit || dailyProfitHit) { Print("Blocked LONG: daily limit hit"); UpdateDashboardStatus("⚠ LONG blocked: daily limit", Brushes.OrangeRed); return; }
            if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay && Position.MarketPosition == MarketPosition.Flat)
            { Print("Blocked LONG: max trades/day (" + dailyTradeCount + "/" + maxTradesPerDay + ")"); UpdateDashboardStatus("⚠ LONG blocked: max trades/day", Brushes.Orange); return; }
            int ct = ToTime(Time[0]);
            // CME maintenance window 4:55 PM - 5:59 PM ET — block ALL entries
            if (ct >= 165500 && ct < 180000) { Print("Blocked LONG: CME maintenance (" + ct + ")"); UpdateDashboardStatus("⚠ LONG blocked: CME maintenance", Brushes.OrangeRed); return; }
            // Trading hours apply only to auto entries
            if (!isManual && (ct < tradingStartTime || ct >= flattenTime)) { Print("Blocked LONG: outside auto hours (time=" + ct + ")"); UpdateDashboardStatus("⚠ LONG blocked: outside auto hours", Brushes.Orange); return; }
            if (Position.MarketPosition == MarketPosition.Short) { Print("Blocked LONG: close short first"); UpdateDashboardStatus("⚠ LONG blocked: close short first", Brushes.Orange); return; }
            if (!dcaEnabled && Position.MarketPosition != MarketPosition.Flat) { Print("Blocked LONG: DCA disabled"); UpdateDashboardStatus("⚠ LONG blocked: DCA off", Brushes.Orange); return; }
            if (Position.MarketPosition == MarketPosition.Long && openDcaCount >= dcaMaxPositions)
            { Print("Blocked LONG: max DCA reached (" + dcaMaxPositions + ")"); UpdateDashboardStatus("⚠ LONG blocked: max DCA", Brushes.Orange); return; }
            if (entryDelaySeconds > 0 && ((State == State.Realtime ? DateTime.Now : Time[0]) - lastEntryWallTime).TotalSeconds < entryDelaySeconds)
            { Print("Blocked LONG: cooldown (" + entryDelaySeconds + "s)"); UpdateDashboardStatus("⚠ LONG blocked: cooldown", Brushes.Orange); return; }

            // DCA distance check (skip if dcaDistancePoints == 0)
            if (dcaDistancePoints > 0 && Position.MarketPosition == MarketPosition.Long)
            {
                double distPts = Math.Abs(Close[0] - averageEntryPrice) / (TickSize * 4.0);
                if (distPts < dcaDistancePoints)
                {
                    Print("Blocked LONG: DCA too close (" + distPts.ToString("F1") + " pts, need " + dcaDistancePoints + ")");
                    UpdateDashboardStatus("⚠ LONG blocked: DCA too close", Brushes.Orange);
                    return;
                }
            }

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                tradeSequence++;
                dailyTradeCount++;
            }
            openDcaCount++;
            string signalName = (++orderCounter).ToString();
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
                + " | HiddenTP=" + hiddenTargetPrice.ToString("F2")
                + " | sig=" + signalName);
            UpdateDashboardStatus("● LONG #" + openDcaCount + " @ " + Close[0].ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteShortEntry(bool isManual = false)
        {
            if (pendingExit) { Print("Blocked SHORT: exit pending"); UpdateDashboardStatus("⚠ SHORT blocked: exit pending", Brushes.Orange); return; }
            if (dailyLimitHit || dailyProfitHit) { Print("Blocked SHORT: daily limit hit"); UpdateDashboardStatus("⚠ SHORT blocked: daily limit", Brushes.OrangeRed); return; }
            if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay && Position.MarketPosition == MarketPosition.Flat)
            { Print("Blocked SHORT: max trades/day (" + dailyTradeCount + "/" + maxTradesPerDay + ")"); UpdateDashboardStatus("⚠ SHORT blocked: max trades/day", Brushes.Orange); return; }
            int ct = ToTime(Time[0]);
            // CME maintenance window 4:55 PM - 5:59 PM ET — block ALL entries
            if (ct >= 165500 && ct < 180000) { Print("Blocked SHORT: CME maintenance (" + ct + ")"); UpdateDashboardStatus("⚠ SHORT blocked: CME maintenance", Brushes.OrangeRed); return; }
            // Trading hours apply only to auto entries
            if (!isManual && (ct < tradingStartTime || ct >= flattenTime)) { Print("Blocked SHORT: outside auto hours (time=" + ct + ")"); UpdateDashboardStatus("⚠ SHORT blocked: outside auto hours", Brushes.Orange); return; }
            if (Position.MarketPosition == MarketPosition.Long) { Print("Blocked SHORT: close long first"); UpdateDashboardStatus("⚠ SHORT blocked: close long first", Brushes.Orange); return; }
            if (!dcaEnabled && Position.MarketPosition != MarketPosition.Flat) { Print("Blocked SHORT: DCA disabled"); UpdateDashboardStatus("⚠ SHORT blocked: DCA off", Brushes.Orange); return; }
            if (Position.MarketPosition == MarketPosition.Short && openDcaCount >= dcaMaxPositions)
            { Print("Blocked SHORT: max DCA reached (" + dcaMaxPositions + ")"); UpdateDashboardStatus("⚠ SHORT blocked: max DCA", Brushes.Orange); return; }
            if (entryDelaySeconds > 0 && ((State == State.Realtime ? DateTime.Now : Time[0]) - lastEntryWallTime).TotalSeconds < entryDelaySeconds)
            { Print("Blocked SHORT: cooldown (" + entryDelaySeconds + "s)"); UpdateDashboardStatus("⚠ SHORT blocked: cooldown", Brushes.Orange); return; }

            if (dcaDistancePoints > 0 && Position.MarketPosition == MarketPosition.Short)
            {
                double distPts = Math.Abs(Close[0] - averageEntryPrice) / (TickSize * 4.0);
                if (distPts < dcaDistancePoints)
                {
                    Print("Blocked SHORT: DCA too close (" + distPts.ToString("F1") + " pts, need " + dcaDistancePoints + ")");
                    UpdateDashboardStatus("⚠ SHORT blocked: DCA too close", Brushes.Orange);
                    return;
                }
            }

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                tradeSequence++;
                dailyTradeCount++;
            }
            openDcaCount++;
            string signalName = (++orderCounter).ToString();
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
            UpdateDashboardStatus("● SHORT #" + openDcaCount + " @ " + Close[0].ToString("F2"), Brushes.OrangeRed);
        }

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

        private void ExecuteFlatten()
        {
            // PRIMARY: Account.Flatten — 100% reliable, closes ALL positions on this instrument
            try
            {
                Account.Flatten(new[] { Instrument });
                Print(Time[0] + " | FLATTEN via Account.Flatten() — guaranteed close");
            }
            catch (Exception ex)
            {
                Print(Time[0] + " | Account.Flatten failed: " + ex.Message);

                // FALLBACK: managed exits
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
            Print(Time[0] + " | Flatten submitted (pendingExit=true)");
        }

        private void ExecuteCloseTrade()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                Print(Time[0] + " | Close Trade: already flat, nothing to do");
                UpdateDashboardStatus("● Already flat", Brushes.CornflowerBlue);
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
            UpdateDashboardStatus("● Closing trade...", Brushes.Yellow);
        }

        // ─── Limit order entries ──────────────────────────────────
        private void ExecuteLongLimitEntry()
        {
            if (pendingExit) { UpdateDashboardStatus("⚠ BUY LMT blocked: exit pending", Brushes.Orange); return; }
            if (dailyLimitHit || dailyProfitHit) { UpdateDashboardStatus("⚠ BUY LMT blocked: daily limit", Brushes.OrangeRed); return; }
            if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay && Position.MarketPosition == MarketPosition.Flat)
            { UpdateDashboardStatus("⚠ BUY LMT blocked: max trades/day", Brushes.Orange); return; }
            int ct = ToTime(Time[0]);
            if (ct >= 165500 && ct < 180000) { UpdateDashboardStatus("⚠ BUY LMT blocked: CME maintenance", Brushes.OrangeRed); return; }
            if (Position.MarketPosition == MarketPosition.Short) { UpdateDashboardStatus("⚠ BUY LMT blocked: close short first", Brushes.Orange); return; }
            if (!dcaEnabled && Position.MarketPosition != MarketPosition.Flat) { UpdateDashboardStatus("⚠ BUY LMT blocked: DCA off", Brushes.Orange); return; }
            if (Position.MarketPosition == MarketPosition.Long && openDcaCount >= dcaMaxPositions) { UpdateDashboardStatus("⚠ BUY LMT blocked: max DCA", Brushes.Orange); return; }
            if (entryDelaySeconds > 0 && ((State == State.Realtime ? DateTime.Now : Time[0]) - lastEntryWallTime).TotalSeconds < entryDelaySeconds) { UpdateDashboardStatus("⚠ BUY LMT blocked: cooldown", Brushes.Orange); return; }

            double limitPrice = GetCurrentBid();
            if (limitPrice <= 0) limitPrice = Close[0] - TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                tradeSequence++;
                dailyTradeCount++;
            }
            openDcaCount++;
            string signalName = (++orderCounter).ToString();
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
            UpdateDashboardStatus("● BUY LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteShortLimitEntry()
        {
            if (pendingExit) { UpdateDashboardStatus("⚠ SELL LMT blocked: exit pending", Brushes.Orange); return; }
            if (dailyLimitHit || dailyProfitHit) { UpdateDashboardStatus("⚠ SELL LMT blocked: daily limit", Brushes.OrangeRed); return; }
            if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay && Position.MarketPosition == MarketPosition.Flat)
            { UpdateDashboardStatus("⚠ SELL LMT blocked: max trades/day", Brushes.Orange); return; }
            int ct = ToTime(Time[0]);
            if (ct >= 165500 && ct < 180000) { UpdateDashboardStatus("⚠ SELL LMT blocked: CME maintenance", Brushes.OrangeRed); return; }
            if (Position.MarketPosition == MarketPosition.Long) { UpdateDashboardStatus("⚠ SELL LMT blocked: close long first", Brushes.Orange); return; }
            if (!dcaEnabled && Position.MarketPosition != MarketPosition.Flat) { UpdateDashboardStatus("⚠ SELL LMT blocked: DCA off", Brushes.Orange); return; }
            if (Position.MarketPosition == MarketPosition.Short && openDcaCount >= dcaMaxPositions) { UpdateDashboardStatus("⚠ SELL LMT blocked: max DCA", Brushes.Orange); return; }
            if (entryDelaySeconds > 0 && ((State == State.Realtime ? DateTime.Now : Time[0]) - lastEntryWallTime).TotalSeconds < entryDelaySeconds) { UpdateDashboardStatus("⚠ SELL LMT blocked: cooldown", Brushes.Orange); return; }

            double limitPrice = GetCurrentAsk();
            if (limitPrice <= 0) limitPrice = Close[0] + TickSize;

            if (Position.MarketPosition == MarketPosition.Flat)
            {
                tradeSequence++;
                dailyTradeCount++;
            }
            openDcaCount++;
            string signalName = (++orderCounter).ToString();
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
            UpdateDashboardStatus("● SELL LMT #" + openDcaCount + " @ " + limitPrice.ToString("F2"), Brushes.OrangeRed);
        }

        // ─── Partial close: exit 1 contract ──────────────────────
        private void ExecuteCloseOne()
        {
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                UpdateDashboardStatus("● Already flat", Brushes.CornflowerBlue);
                return;
            }
            if (Position.Quantity <= 1)
            {
                UpdateDashboardStatus("⚠ Only 1 qty — use CLOSE TRADE", Brushes.Orange);
                return;
            }

            // Exit the LAST signal (most recent DCA add) for 1 contract
            string sig = activeEntrySignals.Count > 0 ? activeEntrySignals[activeEntrySignals.Count - 1] : "";

            if (Position.MarketPosition == MarketPosition.Long)
                ExitLong(1, "Close", sig);
            else
                ExitShort(1, "Close", sig);

            // Update tracking
            if (activeEntrySignals.Count > 0)
                activeEntrySignals.RemoveAt(activeEntrySignals.Count - 1);
            openDcaCount = Math.Max(0, openDcaCount - 1);
            totalContracts = Math.Max(0, totalContracts - 1);

            Print(Time[0] + " | CLOSE 1 — remaining qty≈" + (Position.Quantity - 1) + " DCA=" + openDcaCount);
            UpdateDashboardStatus("● Closed 1 contract", Brushes.Yellow);
        }

        // ─── Jump SL closer to current price ─────────────────────
        private void ExecuteJumpSL()
        {
            if (!stopsArmed || Position.MarketPosition == MarketPosition.Flat)
            {
                UpdateDashboardStatus("⚠ No active SL to jump", Brushes.Orange);
                return;
            }

            double price = Close[0];
            double pct = jumpSlPercent / 100.0;

            if (openTradeDirection == 1)
            {
                // Long: SL is below price. Jump it closer by pct of the gap.
                double gap = price - hiddenStopPrice;
                if (gap <= 1.0 * 4.0 * TickSize) { UpdateDashboardStatus("⚠ SL already at minimum", Brushes.Orange); return; }
                double newSl = hiddenStopPrice + gap * pct;
                // Enforce minimum 1 point from current price
                double minSl = price - 1.0 * 4.0 * TickSize;
                if (newSl > minSl) newSl = minSl;
                hiddenStopPrice = Math.Round(newSl / TickSize) * TickSize;
                slPoints = (int)Math.Round((price - hiddenStopPrice) / (4.0 * TickSize));
            }
            else if (openTradeDirection == -1)
            {
                // Short: SL is above price. Jump it closer by pct of the gap.
                double gap = hiddenStopPrice - price;
                if (gap <= 1.0 * 4.0 * TickSize) { UpdateDashboardStatus("⚠ SL already at minimum", Brushes.Orange); return; }
                double newSl = hiddenStopPrice - gap * pct;
                double maxSl = price + 1.0 * 4.0 * TickSize;
                if (newSl < maxSl) newSl = maxSl;
                hiddenStopPrice = Math.Round(newSl / TickSize) * TickSize;
                slPoints = (int)Math.Round((hiddenStopPrice - price) / (4.0 * TickSize));
            }

            slPoints = Math.Max(1, slPoints);
            Print(Time[0] + " | JUMP SL: new SL=" + hiddenStopPrice.ToString("F2") + " (" + slPoints + " pts)");
            if (ChartControl != null)
                ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());
            UpdateDashboardStatus("● SL jumped to " + hiddenStopPrice.ToString("F2") + " (" + slPoints + "pt)", Brushes.Yellow);
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
            activeEntrySignals.Clear();

            // Reset adaptive trail state
            trailPrice         = 0;
            trailActive        = false;
            trailTrendScore    = 0;
            trailMaxProfitPts  = 0;
            trailTierName      = "";

            // Reset SL/TP to default values for next trade
            if (defaultSlPoints > 0) slPoints = defaultSlPoints;
            if (defaultTpPoints > 0) tpPoints = defaultTpPoints;
            if (ChartControl != null)
                ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());

            // Remove trade-related chart lines
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
        //  POSITION STATE SYNC (reliable, called by NT8 on fills)
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

        // ═══════════════════════════════════════════════════════════
        //  MANUAL VWAP (tick-safe cumulation)
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

            // On new bar, finalize previous bar's contribution
            if (IsFirstTickOfBar && CurrentBar > 0)
            {
                vwapCumTPV += currBarTPV;
                vwapCumVol += currBarVol;
                prevBarVwap = vwapValue;
                currBarTPV  = 0;
                currBarVol  = 0;
            }

            // Current bar running values (replace, don't accumulate)
            currBarTPV = tp * vol;
            currBarVol = vol;

            double totalTPV = vwapCumTPV + currBarTPV;
            double totalVol = vwapCumVol + currBarVol;
            vwapValue = totalVol > 0 ? totalTPV / totalVol : Close[0];
        }

        // ═══════════════════════════════════════════════════════════
        //  SIGNAL CALCULATION (always runs for dashboard display)
        // ═══════════════════════════════════════════════════════════

        private void CalculateSignals()
        {
            // ★ Auto Select — evaluate all 4 strategies, pick highest confidence
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
                    // Apply smart filters to each strategy before comparing
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

            // Apply smart filters (trap detection, volume, exhaustion, conflict)
            ApplySmartFilters();
        }

        /// <summary>
        /// Post-processing intelligence layer applied after every raw signal calculation.
        /// Detects traps, fake-outs, volume anomalies, and conflicting signals.
        /// Stores raw scores for dashboard comparison.
        /// </summary>
        private void ApplySmartFilters()
        {
            if (CurrentBar < emaPeriodSlow + 5) return;

            lastRawBull = lastBullConfidence;
            lastRawBear = lastBearConfidence;

            double atr = indAtr[0];
            if (atr <= 0) return;

            // ── Filter 1: Volume Confirmation ─────────────────────
            // Thin-volume breakouts reverse; high-volume confirms conviction.
            double volSum = 0;
            int volLookback = Math.Min(20, CurrentBar - 1);
            for (int i = 1; i <= volLookback; i++)
                volSum += Volume[i];
            double volAvg = volLookback > 0 ? volSum / volLookback : Volume[0];
            double volRatio = volAvg > 0 ? Volume[0] / volAvg : 1.0;

            if (volRatio < 0.6)
            {
                // Very thin volume — heavy penalty, likely trap / fake move
                lastBullConfidence *= 0.65;
                lastBearConfidence *= 0.65;
            }
            else if (volRatio < 0.85)
            {
                // Below-average volume — mild penalty
                lastBullConfidence *= 0.85;
                lastBearConfidence *= 0.85;
            }
            else if (volRatio > 1.5)
            {
                // Strong volume confirmation — bonus
                lastBullConfidence *= 1.10;
                lastBearConfidence *= 1.10;
            }

            // ── Filter 2: Bull/Bear Conflict (Whipsaw Rejection) ──
            // When both sides have high confidence, market is indecisive → chop trap.
            double maxConf = Math.Max(lastBullConfidence, lastBearConfidence);
            double minConf = Math.Min(lastBullConfidence, lastBearConfidence);
            if (maxConf > 30 && minConf > 0)
            {
                double conflictRatio = minConf / maxConf;
                if (conflictRatio > 0.65)
                {
                    // Heavy conflict — both directions scoring high, likely whipsaw
                    double penalty = 0.55 + (1.0 - conflictRatio) * 1.25;  // ratio 0.65→0.99, penalty 0.99→0.56
                    penalty = Math.Min(1.0, Math.Max(0.5, penalty));
                    lastBullConfidence *= penalty;
                    lastBearConfidence *= penalty;
                }
            }

            // ── Filter 3: Momentum Exhaustion ─────────────────────
            // If price has already moved significantly in one direction,
            // don't chase — it's likely near a reversal or pullback zone.
            if (CurrentBar > 20)
            {
                double recentHigh = MAX(High, 20)[0];
                double recentLow  = MIN(Low,  20)[0];
                double recentRange = recentHigh - recentLow;
                double price = Close[0];

                // How far are we from the 20-bar low (bullish exhaustion)
                double bullExhaustion = recentRange > 0 ? (price - recentLow) / recentRange : 0.5;
                // How far are we from the 20-bar high (bearish exhaustion)
                double bearExhaustion = recentRange > 0 ? (recentHigh - price) / recentRange : 0.5;

                // Also check ATR-relative move size
                double moveFromLow  = (price - recentLow)  / atr;
                double moveFromHigh = (recentHigh - price)  / atr;

                // Penalize bullish entries when already near the top of the range AND extended
                if (bullExhaustion > 0.82 && moveFromLow > 2.0)
                {
                    double exhaust = Math.Min(0.45, (bullExhaustion - 0.82) * 2.5);  // up to 45% penalty
                    lastBullConfidence *= (1.0 - exhaust);
                }
                // Penalize bearish entries when already near the bottom of the range AND extended
                if (bearExhaustion > 0.82 && moveFromHigh > 2.0)
                {
                    double exhaust = Math.Min(0.45, (bearExhaustion - 0.82) * 2.5);
                    lastBearConfidence *= (1.0 - exhaust);
                }
            }

            // ── Filter 4: Bar Quality (Wick Rejection) ────────────
            // Long upper wick on a bullish signal = rejection, likely trap
            double bodySize = Math.Abs(Close[0] - Open[0]);
            double barRange = High[0] - Low[0];
            if (barRange > 0)
            {
                double upperWick = High[0] - Math.Max(Close[0], Open[0]);
                double lowerWick = Math.Min(Close[0], Open[0]) - Low[0];
                double upperWickPct = upperWick / barRange;
                double lowerWickPct = lowerWick / barRange;

                // Big upper wick (>45% of bar) penalizes bullish signals
                if (upperWickPct > 0.45)
                    lastBullConfidence *= (1.0 - (upperWickPct - 0.45) * 1.5);

                // Big lower wick (>45% of bar) penalizes bearish signals
                if (lowerWickPct > 0.45)
                    lastBearConfidence *= (1.0 - (lowerWickPct - 0.45) * 1.5);
            }

            // Clamp to [0, 120] — allow slight boost above 100 from volume bonus
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

            // Bar quality: closing near extreme = conviction
            double barRange = High[0] - Low[0];
            bool bullishClose = barRange > 0 && (price - Low[0]) / barRange > 0.65;  // close in top 35%
            bool bearishClose = barRange > 0 && (High[0] - price) / barRange > 0.65;  // close in bottom 35%

            // ── Long ──
            bool longEma       = emaF > emaS;
            bool longVwapCross = price > vwapValue && prev <= vwapValue;  // crossover (primary)
            bool longVwapAbove = price > vwapValue;                       // continuation (staying above)
            bool longRsi       = rsi > 50 && rsi < 75;
            bool longMom       = Close[0] > High[1];

            if (longEma)       lastBullConfidence += 30;
            if (longVwapCross) lastBullConfidence += 35;  // full credit on crossover
            else if (longVwapAbove) lastBullConfidence += 20;  // partial credit for continuation
            if (longRsi)       lastBullConfidence += 20;
            if (longMom)       lastBullConfidence += 15;
            // Bar quality bonus: VWAP cross with a strong close = high conviction
            if (longVwapCross && bullishClose) lastBullConfidence += 10;

            // ── Short ──
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
            // Bar quality bonus
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

            // Bar quality for fake-out filter: breakout bar should close near the extreme
            double barRange = High[0] - Low[0];
            bool bullConviction = barRange > 0 && (price - Low[0]) / barRange > 0.60;  // close in top 40%
            bool bearConviction = barRange > 0 && (High[0] - price) / barRange > 0.60;  // close in bottom 40%

            // ── Long ──
            bool longBreakCross = price > priorHigh && prevClose <= priorHigh;  // crossover
            bool longBreakAbove = price > priorHigh;                             // continuation
            bool longAtrConf    = atr > 0 && (price - priorHigh) > atr * 0.15;
            bool longEmaAlign   = indEmaFast[0] > indEmaSlow[0];

            // Fake-out filter: on fresh crossover, require bullish close conviction
            if (longBreakCross && bullConviction)
                lastBullConfidence += 50;
            else if (longBreakCross)  // crossover but bar closing weak = possible trap
                lastBullConfidence += 25;
            else if (longBreakAbove)
                lastBullConfidence += 25;  // holding above breakout level
            if (longAtrConf)        lastBullConfidence += 30;
            if (longEmaAlign)       lastBullConfidence += 20;

            // ── Short ──
            bool shortBreakCross = price < priorLow && prevClose >= priorLow;
            bool shortBreakBelow = price < priorLow;
            bool shortAtrConf    = atr > 0 && (priorLow - price) > atr * 0.15;
            bool shortEmaAlign   = indEmaFast[0] < indEmaSlow[0];

            if (shortBreakCross && bearConviction)
                lastBearConfidence += 50;
            else if (shortBreakCross)
                lastBearConfidence += 25;
            else if (shortBreakBelow)
                lastBearConfidence += 25;
            if (shortAtrConf)        lastBearConfidence += 30;
            if (shortEmaAlign)       lastBearConfidence += 20;
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

            // Volume spike: real sweeps are driven by stops hitting → volume surges
            double volSum = 0;
            int vlb = Math.Min(20, CurrentBar - 1);
            for (int i = 1; i <= vlb; i++) volSum += Volume[i];
            double volAvg = vlb > 0 ? volSum / vlb : Volume[0];
            bool volSpike = volAvg > 0 && Volume[0] > volAvg * 1.2;  // 20%+ above average

            // ── Long (sweep lows then recover) ──
            bool longSweep     = prevLow < swingLow && price > swingLow;              // immediate snap-back
            bool longRecovery  = price > swingLow && MIN(Low, 5)[1] < swingLow;      // recent sweep within 5 bars
            bool longRsiConf   = indRsi[0] < 40;
            bool longSnapback  = atr > 0 && (price - prevLow) > atr * 0.3;

            if (longSweep)          lastBullConfidence += 45;
            else if (longRecovery)  lastBullConfidence += 25;  // continuation after recent sweep
            if (longRsiConf)        lastBullConfidence += 30;
            if (longSnapback)       lastBullConfidence += 25;
            // Volume spike confirms genuine liquidity grab (not just a drift)
            if ((longSweep || longRecovery) && volSpike) lastBullConfidence += 10;

            // ── Short (sweep highs then drop) ──
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

            // ORB range quality: narrow ORB = cleaner breakout; wide ORB = noisy
            double orbRange = orbHigh - orbLow;
            double orbQuality = (atr > 0 && orbRange > 0) ? Math.Min(1.0, atr / orbRange) : 1.0;

            // Bar conviction: close near the breakout extreme
            double barRange = High[0] - Low[0];
            bool bullConviction = barRange > 0 && (price - Low[0]) / barRange > 0.60;
            bool bearConviction = barRange > 0 && (High[0] - price) / barRange > 0.60;

            // ── Long ──
            bool longBreakCross = price > orbHigh && prevClose <= orbHigh;  // crossover
            bool longBreakAbove = price > orbHigh;                           // continuation

            if (longBreakCross && bullConviction)
                lastBullConfidence += 55;
            else if (longBreakCross)
                lastBullConfidence += 30;  // weak conviction crossover
            else if (longBreakAbove) lastBullConfidence += 30;  // holding above ORB high
            if (emaAlign)            lastBullConfidence += 25;
            if (atr > 0)             lastBullConfidence += (int)(20 * orbQuality);  // scale by ORB quality

            // ── Short ──
            bool shortBreakCross = price < orbLow && prevClose >= orbLow;
            bool shortBreakBelow = price < orbLow;

            if (shortBreakCross && bearConviction)
                lastBearConfidence += 55;
            else if (shortBreakCross)
                lastBearConfidence += 30;
            else if (shortBreakBelow) lastBearConfidence += 30;
            if (!emaAlign)            lastBearConfidence += 25;
            if (atr > 0)              lastBearConfidence += (int)(20 * orbQuality);
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

            // SL/TP labels with points, ticks, and dollar risk
            int slTk = slPoints * 4;
            int tpTk = tpPoints * 4;
            double slDol = slPoints * 20.0 * totalContracts;
            double tpDol = tpPoints * 20.0 * totalContracts;

            Draw.Text(this, "slLabel",
                "SL " + hiddenStopPrice.ToString("F2") + "  (" + slPoints + "pt | " + slTk + "tk | " + slDol.ToString("C0") + ")",
                0, hiddenStopPrice + (openTradeDirection == 1 ? -2 * TickSize : 2 * TickSize), Brushes.OrangeRed);
            Draw.Text(this, "tpLabel",
                "TP " + hiddenTargetPrice.ToString("F2") + "  (" + tpPoints + "pt | " + tpTk + "tk | " + tpDol.ToString("C0") + ")",
                0, hiddenTargetPrice + (openTradeDirection == 1 ? 2 * TickSize : -2 * TickSize), Brushes.LimeGreen);

            // Adaptive trail line on chart
            if (trailEnabled && trailActive && trailPrice > 0)
            {
                Draw.HorizontalLine(this, "adaptiveTrail", false, trailPrice, Brushes.Magenta, DashStyleHelper.DashDot, 2);
                string regimeStr = trailTrendScore > 0.6 ? "Trending" : (trailTrendScore < 0.35 ? "Choppy" : "Mixed");
                double trailDist = openTradeDirection == 1
                    ? (Close[0] - trailPrice) / (TickSize * 4.0)
                    : openTradeDirection == -1
                        ? (trailPrice - Close[0]) / (TickSize * 4.0) : 0;
                Draw.Text(this, "trailLabel",
                    "Trail " + trailPrice.ToString("F2") + "  (" + trailDist.ToString("F1") + "pt | " + regimeStr + " " + (trailTrendScore * 100).ToString("F0") + "%)",
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
            // When flat or DCA disabled or max reached — remove DCA lines
            if (!stopsArmed || !dcaEnabled || openDcaCount >= dcaMaxPositions || averageEntryPrice == 0)
            {
                RemoveDrawObject("dcaLevelAbove");
                RemoveDrawObject("dcaLabelAbove");
                RemoveDrawObject("dcaLevelBelow");
                RemoveDrawObject("dcaLabelBelow");
                return;
            }

            // Calculate DCA zone: either dcaDistancePoints from avg entry, or SL midpoint if distance=0
            double distOffset;
            if (dcaDistancePoints > 0)
                distOffset = dcaDistancePoints * 4.0 * TickSize;
            else
                distOffset = 0;  // no distance restriction — show zone at avg entry

            int remaining = dcaMaxPositions - openDcaCount;
            string dcaInfo = "DCA #" + (openDcaCount + 1) + " (" + remaining + " left, qty " + contracts + ")";

            if (openTradeDirection == 1)
            {
                // Long: DCA add happens if price drops below entry
                double dcaBelow = averageEntryPrice - distOffset;
                Draw.HorizontalLine(this, "dcaLevelBelow", false, dcaBelow, Brushes.DeepSkyBlue, DashStyleHelper.Dash, 1);
                Draw.Text(this, "dcaLabelBelow", dcaInfo,
                    0, dcaBelow - 4 * TickSize, Brushes.DeepSkyBlue);
                // Remove the opposite side
                RemoveDrawObject("dcaLevelAbove");
                RemoveDrawObject("dcaLabelAbove");
            }
            else if (openTradeDirection == -1)
            {
                // Short: DCA add happens if price rises above entry
                double dcaAbove = averageEntryPrice + distOffset;
                Draw.HorizontalLine(this, "dcaLevelAbove", false, dcaAbove, Brushes.DeepSkyBlue, DashStyleHelper.Dash, 1);
                Draw.Text(this, "dcaLabelAbove", dcaInfo,
                    0, dcaAbove + 4 * TickSize, Brushes.DeepSkyBlue);
                // Remove the opposite side
                RemoveDrawObject("dcaLevelBelow");
                RemoveDrawObject("dcaLabelBelow");
            }
        }

        private void DrawVwapLine()
        {
            if (!showVwap || CurrentBar < BarsRequiredToTrade + 1 || vwapValue == 0) return;

            // Draw segment from previous bar's VWAP to current VWAP
            if (IsFirstTickOfBar || CurrentBar == BarsRequiredToTrade + 1)
            {
                if (prevBarVwap > 0)
                    Draw.Line(this, "vwap_" + CurrentBar, false,
                        1, prevBarVwap, 0, vwapValue,
                        Brushes.Yellow, DashStyleHelper.Solid, 2);
            }
            else
            {
                // Update current bar's endpoint on each tick
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

            Draw.HorizontalLine(this, "keyResist",  false, priorHigh, Brushes.Cyan,    DashStyleHelper.Dash, 1);
            Draw.HorizontalLine(this, "keySupport", false, priorLow,  Brushes.Cyan,    DashStyleHelper.Dash, 1);
            Draw.Text(this, "keyResistLbl", "Key Resist " + priorHigh.ToString("F2"),
                0, priorHigh + 2 * TickSize, Brushes.Cyan);
            Draw.Text(this, "keySupportLbl", "Key Support " + priorLow.ToString("F2"),
                0, priorLow - 2 * TickSize, Brushes.Cyan);
        }

        private void DrawSweepSignals()
        {
            if (!showSweepSignals || CurrentBar < 12) return;

            double swingHigh = MAX(High, 10)[1];
            double swingLow  = MIN(Low,  10)[1];

            // Draw swing levels
            Draw.HorizontalLine(this, "swingHi", false, swingHigh, Brushes.Magenta, DashStyleHelper.Dot, 1);
            Draw.HorizontalLine(this, "swingLo", false, swingLow,  Brushes.Magenta, DashStyleHelper.Dot, 1);

            // Detect and mark sweep events
            double prevLow  = Low[1];
            double prevHigh = High[1];
            double price    = Close[0];

            // Bullish sweep: price dipped below swing low then recovered
            if (prevLow < swingLow && price > swingLow)
            {
                Draw.ArrowUp(this, "sweepUp_" + CurrentBar, false, 0,
                    Low[0] - 8 * TickSize, Brushes.LimeGreen);
                Draw.Text(this, "sweepUpTxt_" + CurrentBar, "Sweep↑",
                    0, Low[0] - 16 * TickSize, Brushes.LimeGreen);
            }

            // Bearish sweep: price spiked above swing high then fell back
            if (prevHigh > swingHigh && price < swingHigh)
            {
                Draw.ArrowDown(this, "sweepDn_" + CurrentBar, false, 0,
                    High[0] + 8 * TickSize, Brushes.OrangeRed);
                Draw.Text(this, "sweepDnTxt_" + CurrentBar, "Sweep↓",
                    0, High[0] + 16 * TickSize, Brushes.OrangeRed);
            }
        }

        private void ResetDailyTracking()
        {
            sessionDate      = Time[0].Date;
            dailyRealizedPnL = 0;
            dailyLimitHit    = false;
            dailyProfitHit   = false;
            dailyTradeCount  = 0;
            flattenFired     = false;
            orbSet           = false;
            orbHigh          = 0;
            orbLow           = 0;
            Print("Session reset: " + sessionDate.ToShortDateString()
                + " MaxLoss=$" + maxDailyLossDollars + " ProfitTarget=$" + maxDailyProfitDollars
                + " MaxTrades=" + (maxTradesPerDay > 0 ? maxTradesPerDay.ToString() : "∞"));
        }

        // ═══════════════════════════════════════════════════════════
        //  DASHBOARD UI
        // ═══════════════════════════════════════════════════════════

        private void BuildDashboard()
        {
            if (ChartControl == null || dashboardAttached) return;
            // Set flag on caller thread FIRST to prevent re-entrant calls
            // before the dispatcher runs (prevents duplicate dashboards)
            dashboardAttached = true;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                // Safety: if RemoveDashboard ran between enqueue and execution
                if (!dashboardAttached) return;

                // ─── Main container with drag support ─────────────
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

                // ─── Drag title bar ───────────────────────────────
                dashTitleBar = new Border
                {
                    Background   = new SolidColorBrush(Color.FromRgb(30, 35, 48)),
                    CornerRadius = new CornerRadius(5, 5, 0, 0),
                    Padding      = new Thickness(10, 6, 10, 6),
                    Cursor       = Cursors.SizeAll
                };
                dashTitleBar.Child = new TextBlock
                {
                    Text                = "☰  NQ  Mm-ATM  v1.0",
                    Foreground          = Brushes.White,
                    FontSize            = 13,
                    FontWeight          = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                outerStack.Children.Add(dashTitleBar);

                dashScroll = new ScrollViewer
                {
                    MaxHeight                   = 420,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                };

                var stack = new StackPanel { Margin = new Thickness(10, 4, 10, 4) };

                // ─── Mode toggle ──────────────────────────────────
                var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnModeManual = MakeToggleButton("MANUAL", !autoMode, (s, e) => { autoMode = false; UpdateModeButtons(); });
                btnModeAuto   = MakeToggleButton("AUTO",    autoMode, (s, e) => { autoMode = true;  UpdateModeButtons(); });
                modeRow.Children.Add(btnModeManual);
                modeRow.Children.Add(btnModeAuto);
                stack.Children.Add(modeRow);

                // ─── Strategy selector ────────────────────────────
                stratPanel = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
                stratPanel.Children.Add(MakeLabel("Auto Strategy:", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left));
                var stratRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                var btnStratPrev = MakeSmallButton("◄", (s, e) => { autoStrategy = (autoStrategy + 4) % 5; UpdateStratLabel(); });
                lblStratName = MakeLabel(StrategyNames[autoStrategy], Brushes.White, 11, FontWeights.SemiBold, HorizontalAlignment.Center);
                lblStratName.Width = 140;
                lblStratName.TextAlignment = TextAlignment.Center;
                var btnStratNext2 = MakeSmallButton("►", (s, e) => { autoStrategy = (autoStrategy + 1) % 5; UpdateStratLabel(); });
                stratRow.Children.Add(btnStratPrev);
                stratRow.Children.Add(lblStratName);
                stratRow.Children.Add(btnStratNext2);
                stratPanel.Children.Add(stratRow);
                stack.Children.Add(stratPanel);

                stack.Children.Add(MakeSeparator());

                // ─── Qty row ──────────────────────────────────────
                var qtyRow = MakeAdjustRow("Qty:", contracts.ToString(),
                    (s, e) => { contracts = Math.Max(1, contracts - 1); UpdateAdjustLabels(); },
                    (s, e) => { contracts = Math.Min(10, contracts + 1); UpdateAdjustLabels(); },
                    "/ max " + dcaMaxPositions);
                lblQtyVal = (TextBlock)((StackPanel)qtyRow).Children[3];
                stack.Children.Add(qtyRow);

                // ─── SL row ───────────────────────────────────────
                var slRow = MakeAdjustRow("SL:", slPoints + "pt | " + (slPoints * 4) + "tk | $" + (slPoints * 20), 
                    (s, e) => { slPoints = Math.Max(1, slPoints - slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    (s, e) => { slPoints = Math.Min(500, slPoints + slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    "");
                lblSlVal = (TextBlock)((StackPanel)slRow).Children[3];
                stack.Children.Add(slRow);

                // ─── TP row ───────────────────────────────────────
                var tpRow = MakeAdjustRow("TP:", tpPoints + "pt | " + (tpPoints * 4) + "tk | $" + (tpPoints * 20), 
                    (s, e) => { tpPoints = Math.Max(1, tpPoints - slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    (s, e) => { tpPoints = Math.Min(500, tpPoints + slTpAdjustStep); UpdateAdjustLabels(); pendingRearm = true; },
                    "");
                lblTpVal = (TextBlock)((StackPanel)tpRow).Children[3];
                stack.Children.Add(tpRow);

                stack.Children.Add(MakeSeparator());

                // ─── Jump SL row ──────────────────────────────────
                var jumpRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnJumpSL = new Button
                {
                    Content = "⇥ JUMP SL", Width = 90, Height = 26,
                    Background = new SolidColorBrush(Color.FromRgb(120, 80, 20)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                jumpRow.Children.Add(btnJumpSL);
                jumpRow.Children.Add(MakeSmallButton("−", (s, e) => { jumpSlPercent = Math.Max(10, jumpSlPercent - 10); UpdateJumpLabel(); }));
                lblJumpPct = MakeLabel(jumpSlPercent + "%", Brushes.White, 10, FontWeights.Bold, HorizontalAlignment.Center);
                lblJumpPct.Width = 36;
                lblJumpPct.VerticalAlignment = VerticalAlignment.Center;
                lblJumpPct.TextAlignment = TextAlignment.Center;
                jumpRow.Children.Add(lblJumpPct);
                jumpRow.Children.Add(MakeSmallButton("+", (s, e) => { jumpSlPercent = Math.Min(95, jumpSlPercent + 10); UpdateJumpLabel(); }));
                stack.Children.Add(jumpRow);

                stack.Children.Add(MakeSeparator());

                // ─── BUY MKT / SELL MKT buttons ──────────────────
                var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyMkt = new Button
                {
                    Content = "▲ BUY MKT", Width = 120, Height = 36,
                    Background = new SolidColorBrush(Color.FromRgb(22, 140, 100)),
                    Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellMkt = new Button
                {
                    Content = "▼ SELL MKT", Width = 120, Height = 36,
                    Background = new SolidColorBrush(Color.FromRgb(190, 45, 45)),
                    Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0)
                };
                btnRow.Children.Add(btnBuyMkt);
                btnRow.Children.Add(btnSellMkt);
                stack.Children.Add(btnRow);

                // ─── BUY LMT / SELL LMT buttons ──────────────────
                var lmtRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                btnBuyLmt = new Button
                {
                    Content = "△ BUY LMT", Width = 120, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(18, 105, 78)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnSellLmt = new Button
                {
                    Content = "▽ SELL LMT", Width = 120, Height = 30,
                    Background = new SolidColorBrush(Color.FromRgb(145, 35, 35)),
                    Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(4, 0, 0, 0)
                };
                lmtRow.Children.Add(btnBuyLmt);
                lmtRow.Children.Add(btnSellLmt);
                stack.Children.Add(lmtRow);

                // ─── Close 1 + Close All row ──────────────────────
                var closeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                btnCloseOne = new Button
                {
                    Content = "▣ CLOSE 1", Width = 90, Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(130, 90, 0)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 4, 0)
                };
                btnCloseTrade = new Button
                {
                    Content = "⏹ CLOSE ALL", Width = 148, Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(160, 100, 0)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    BorderThickness = new Thickness(0)
                };
                closeRow.Children.Add(btnCloseOne);
                closeRow.Children.Add(btnCloseTrade);
                stack.Children.Add(closeRow);

                // ─── Emergency Kill button ────────────────────────
                btnExit = new Button
                {
                    Content = "⚠  EMERGENCY KILL", Height = 28,
                    Background = new SolidColorBrush(Color.FromRgb(140, 20, 20)),
                    Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.Bold,
                    Margin = new Thickness(0, 4, 0, 0), BorderThickness = new Thickness(0)
                };
                stack.Children.Add(btnExit);

                stack.Children.Add(MakeSeparator());

                // ─── Position info ────────────────────────────────
                lblStatus   = MakeLabel("● Flat — ready",   Brushes.CornflowerBlue, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                lblPosition = MakeLabel("DCA: 0/" + dcaMaxPositions + "  |  Avg: —", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                lblHiddenSL = MakeLabel("Hidden SL: —",     Brushes.OrangeRed,  10, FontWeights.Normal, HorizontalAlignment.Left);
                lblHiddenTP = MakeLabel("Hidden TP: —",     Brushes.LimeGreen,  10, FontWeights.Normal, HorizontalAlignment.Left);
                lblVwapVal  = MakeLabel("VWAP: —",          Brushes.Yellow,     10, FontWeights.Normal, HorizontalAlignment.Left);
                stack.Children.Add(lblStatus);
                stack.Children.Add(lblPosition);
                stack.Children.Add(lblHiddenSL);
                stack.Children.Add(lblHiddenTP);

                // ─── Adaptive trail toggle + info ─────────────────
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
                lblTrailInfo = MakeLabel("Trail: —", Brushes.Magenta, 10, FontWeights.Normal, HorizontalAlignment.Left);
                trailRow.Children.Add(lblTrailInfo);
                stack.Children.Add(trailRow);

                stack.Children.Add(lblVwapVal);

                stack.Children.Add(MakeSeparator());

                // ─── Confidence scores ────────────────────────────
                stack.Children.Add(MakeLabel("Signal Confidence:", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left));
                lblConfBull = MakeLabel("Bull: 0%",  Brushes.LimeGreen,  11, FontWeights.SemiBold, HorizontalAlignment.Left);
                lblConfBear = MakeLabel("Bear: 0%",  Brushes.OrangeRed,  11, FontWeights.SemiBold, HorizontalAlignment.Left);
                var confRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
                lblConfBull.Width = 120;
                lblConfBear.Width = 120;
                confRow.Children.Add(lblConfBull);
                confRow.Children.Add(lblConfBear);
                stack.Children.Add(confRow);

                stack.Children.Add(MakeSeparator());

                // ─── P&L display ──────────────────────────────────
                lblUnrealized = MakeLabel("Unrealized:  $0.00",  Brushes.White, 11, FontWeights.Normal, HorizontalAlignment.Left);
                lblPnL        = MakeLabel("Daily P&L:   $0.00",  Brushes.White, 11, FontWeights.Normal, HorizontalAlignment.Left);
                stack.Children.Add(lblUnrealized);
                stack.Children.Add(lblPnL);

                stack.Children.Add(MakeSeparator());

                // ─── Trading hours status ─────────────────────────
                lblTradeHours = MakeLabel("● Trading allowed", Brushes.LimeGreen, 10, FontWeights.SemiBold, HorizontalAlignment.Left);
                stack.Children.Add(lblTradeHours);

                // ─── Resize grip ──────────────────────────────────
                dashResizeGrip = new Border
                {
                    Height              = 14,
                    Background          = Brushes.Transparent,
                    Cursor              = Cursors.SizeNWSE,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Padding             = new Thickness(0, 0, 4, 2)
                };
                dashResizeGrip.Child = new TextBlock
                {
                    Text                = "⋱",
                    FontSize            = 11,
                    Foreground          = new SolidColorBrush(Color.FromRgb(80, 85, 100)),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment   = VerticalAlignment.Bottom
                };

                // ─── Assemble ─────────────────────────────────────
                dashScroll.Content = stack;
                outerStack.Children.Add(dashScroll);
                outerStack.Children.Add(dashResizeGrip);
                dashBorder.Child = outerStack;
                dashboardPanel.Children.Add(dashBorder);

                // ─── Mouse handlers: drag, resize, click routing ──
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
                                    if (btn == btnBuyMkt)           { pendingLong = true; if (lblStatus != null) { lblStatus.Text = "● BUY MKT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnSellMkt)     { pendingShort = true; if (lblStatus != null) { lblStatus.Text = "● SELL MKT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnBuyLmt)      { pendingLongLimit = true; if (lblStatus != null) { lblStatus.Text = "● BUY LMT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnSellLmt)     { pendingShortLimit = true; if (lblStatus != null) { lblStatus.Text = "● SELL LMT queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnCloseOne)    { pendingCloseOne = true; if (lblStatus != null) { lblStatus.Text = "● CLOSE 1 queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnCloseTrade)  { pendingCloseTrade = true; if (lblStatus != null) { lblStatus.Text = "● CLOSE queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnJumpSL)      { pendingJumpSL = true; if (lblStatus != null) { lblStatus.Text = "● JUMP SL queued..."; lblStatus.Foreground = Brushes.Yellow; } }
                                    else if (btn == btnExit)        { pendingFlatten = true; if (lblStatus != null) { lblStatus.Text = "⚠ KILL queued..."; lblStatus.Foreground = Brushes.OrangeRed; } }
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

                // ═══ FLOATING OVERLAY PLACEMENT ═══════════════════
                // Dashboard is a floating overlay that the user can drag
                // to any position on the chart. Default: top-right.
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
                    Print("Dashboard: floating overlay at (" + startX.ToString("F0") + ", 10) — drag title bar to move, grip to resize");
                }

                // Fallback: walk up to find any parent Grid
                if (!placed)
                {
                    DependencyObject parent = ChartControl;
                    while (parent != null)
                    {
                        parent = VisualTreeHelper.GetParent(parent);
                        if (parent is Grid g && g.Children.Count > 0)
                        {
                            dashTranslate.X = 10;
                            dashTranslate.Y = 10;
                            g.Children.Add(dashboardPanel);
                            dashboardHostPanel = g;
                            placed = true;
                            Print("Dashboard: floating overlay (fallback grid)");
                            break;
                        }
                    }
                }

                if (!placed)
                    Print("Dashboard: FAILED to find host — not attached");
            });
        }

        // ─── Dashboard helper: labeled adjust row ─────────────────
        private StackPanel MakeAdjustRow(string label, string value,
            RoutedEventHandler minusHandler, RoutedEventHandler plusHandler, string suffix)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 3, 0, 3),
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var lbl = MakeLabel(label, Brushes.Gray, 11, FontWeights.Normal, HorizontalAlignment.Left);
            lbl.Width = 28;
            lbl.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(lbl);

            // [-] and [+] side by side for easy pressing
            row.Children.Add(MakeSmallButton("−", minusHandler));
            row.Children.Add(MakeSmallButton("+", plusHandler));

            var val = MakeLabel(value, Brushes.White, 10, FontWeights.Bold, HorizontalAlignment.Left);
            val.VerticalAlignment = VerticalAlignment.Center;
            val.Margin = new Thickness(6, 0, 0, 0);
            row.Children.Add(val);

            if (!string.IsNullOrEmpty(suffix))
            {
                var suf = MakeLabel(suffix, Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                suf.VerticalAlignment = VerticalAlignment.Center;
                suf.Margin = new Thickness(4, 0, 0, 0);
                row.Children.Add(suf);
            }

            return row;
        }

        // ─── Dashboard helper: small +/- button ──────────────────
        private Button MakeSmallButton(string text, RoutedEventHandler handler)
        {
            var btn = new Button
            {
                Content    = text,
                Width      = 28,
                Height     = 24,
                Background = new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                Foreground = Brushes.White,
                FontSize   = 13,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Margin     = new Thickness(2, 0, 2, 0)
            };
            btn.Click += handler;
            return btn;
        }

        // ─── Dashboard helper: mode toggle button ────────────────
        private Button MakeToggleButton(string text, bool active, RoutedEventHandler handler)
        {
            var btn = new Button
            {
                Content    = text,
                Width      = 110,
                Height     = 30,
                Background = active
                    ? new SolidColorBrush(Color.FromRgb(30, 90, 160))
                    : new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                Foreground = Brushes.White,
                FontSize   = 12,
                FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(0),
                Margin     = new Thickness(2, 0, 2, 0)
            };
            // Click event raised by border-level router
            return btn;
        }

        // ─── Dashboard helper: text label ─────────────────────────
        private TextBlock MakeLabel(string text, Brush color, double size, FontWeight weight, HorizontalAlignment hAlign)
        {
            return new TextBlock
            {
                Text                = text,
                Foreground          = color,
                FontSize            = size,
                FontWeight          = weight,
                HorizontalAlignment = hAlign,
                Margin              = new Thickness(0, 1, 0, 1)
            };
        }

        // ─── Dashboard helper: separator line ─────────────────────
        private Border MakeSeparator()
        {
            return new Border
            {
                Height     = 1,
                Background = new SolidColorBrush(Color.FromRgb(55, 60, 75)),
                Margin     = new Thickness(0, 6, 0, 6)
            };
        }

        // ─── UI update: mode buttons ──────────────────────────────
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

        // ─── UI update: strategy label ────────────────────────────
        private void UpdateStratLabel()
        {
            if (lblStratName == null) return;
            if (autoStrategy == 4)
                lblStratName.Text = "\u2605 Auto \u2192 " + StrategyNames[bestAutoStrategy];
            else
                lblStratName.Text = StrategyNames[autoStrategy];
        }

        // ─── UI update: adjust labels after +/- clicks ───────────
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

        // ─── Helper: format HHMMSS int to readable string ────────
        private string FormatTime(int hhmmss)
        {
            int h = hhmmss / 10000;
            int m = (hhmmss / 100) % 100;
            int ap = h >= 12 ? 1 : 0;
            int h12 = h > 12 ? h - 12 : (h == 0 ? 12 : h);
            return h12 + ":" + m.ToString("D2") + (ap == 1 ? " PM" : " AM");
        }

        // ─── UI update: status label ──────────────────────────────
        private void UpdateDashboardStatus(string text, Brush color)
        {
            if (lblStatus == null || ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                lblStatus.Text       = text;
                lblStatus.Foreground = color;
            });
        }

        // ─── Full dashboard refresh (called from OnBarUpdate) ─────
        private void UpdateDashboard()
        {
            if (lblPnL == null || ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                // ─── P&L ──────────────────────────────────────────
                double unrealizedPnL = 0;
                if (Position.MarketPosition != MarketPosition.Flat)
                {
                    try { unrealizedPnL = Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0]); }
                    catch { unrealizedPnL = 0; }
                }

                lblUnrealized.Text       = "Unrealized:  " + unrealizedPnL.ToString("C2");
                lblUnrealized.Foreground  = unrealizedPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;

                double totalPnL = dailyRealizedPnL + unrealizedPnL;
                string tradeCountStr = maxTradesPerDay > 0
                    ? "  [Trades: " + dailyTradeCount + "/" + maxTradesPerDay + "]"
                    : "  [Trades: " + dailyTradeCount + "]";
                lblPnL.Text       = "Daily P&L:   " + dailyRealizedPnL.ToString("C2") + "  (Total: " + totalPnL.ToString("C2") + ")" + tradeCountStr;
                lblPnL.Foreground = dailyRealizedPnL >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;

                // ─── Position status ──────────────────────────────
                if (pendingExit)
                {
                    lblStatus.Text       = "● CLOSING... (exit pending)";
                    lblStatus.Foreground = Brushes.Yellow;
                }
                else if (Position.MarketPosition != MarketPosition.Flat)
                {
                    // Show actual NT8 position even if our state is desynced
                    string dir = Position.MarketPosition == MarketPosition.Long ? "LONG" : "SHORT";
                    int qty = Position.Quantity;
                    if (openTradeDirection != 0 && stopsArmed)
                    {
                        lblStatus.Text       = "● " + dir + "  ×" + qty;
                        lblStatus.Foreground = Position.MarketPosition == MarketPosition.Long ? Brushes.LimeGreen : Brushes.OrangeRed;
                    }
                    else
                    {
                        // State desync — show warning with actual position
                        lblStatus.Text       = "⚠ " + dir + " ×" + qty + " (state desync!)";
                        lblStatus.Foreground = Brushes.Yellow;
                    }
                    lblPosition.Text     = "DCA: " + openDcaCount + "/" + dcaMaxPositions
                                         + "  |  Avg: " + (averageEntryPrice > 0 ? averageEntryPrice.ToString("F2") : Position.AveragePrice.ToString("F2"));
                    lblHiddenSL.Text     = hiddenStopPrice > 0
                        ? "SL: " + hiddenStopPrice.ToString("F2")
                          + "  (" + slPoints + "pt | " + (slPoints * 4) + "tk | " + (slPoints * 20.0 * Math.Max(totalContracts, qty)).ToString("C0") + ")"
                        : "SL: —  (stops not armed)";
                    lblHiddenTP.Text     = hiddenTargetPrice > 0
                        ? "TP: " + hiddenTargetPrice.ToString("F2")
                          + "  (" + tpPoints + "pt | " + (tpPoints * 4) + "tk | " + (tpPoints * 20.0 * Math.Max(totalContracts, qty)).ToString("C0") + ")"
                        : "TP: —  (stops not armed)";

                    // Trail info (in position)
                    if (lblTrailInfo != null)
                    {
                        if (trailEnabled && trailActive && trailPrice > 0)
                        {
                            double dist = openTradeDirection == 1
                                ? (Close[0] - trailPrice) / (TickSize * 4.0)
                                : openTradeDirection == -1
                                    ? (trailPrice - Close[0]) / (TickSize * 4.0) : 0;
                            string regime = trailTrendScore > 0.6 ? "Trend" : (trailTrendScore < 0.35 ? "Chop" : "Mix");
                            string tierStr = !string.IsNullOrEmpty(trailTierName) ? " " + trailTierName : "";
                            lblTrailInfo.Text = "Trail: " + trailPrice.ToString("F2") + " (" + dist.ToString("F1") + "pt " + regime + " " + (trailTrendScore * 100).ToString("F0") + "%" + tierStr + ")";
                            // Color by tier: runner=gold, strong=cyan, BE=magenta, active=magenta
                            if (trailTierName == "T3-Runner")
                                lblTrailInfo.Foreground = Brushes.Gold;
                            else if (trailTierName == "T2-Strong")
                                lblTrailInfo.Foreground = Brushes.Cyan;
                            else if (trailTierName == "T1-BE")
                                lblTrailInfo.Foreground = Brushes.Yellow;
                            else
                                lblTrailInfo.Foreground = Brushes.Magenta;
                        }
                        else if (trailEnabled && !trailActive)
                        {
                            double profitPts = 0;
                            if (openTradeDirection == 1) profitPts = (Close[0] - averageEntryPrice) / (TickSize * 4.0);
                            else if (openTradeDirection == -1) profitPts = (averageEntryPrice - Close[0]) / (TickSize * 4.0);
                            lblTrailInfo.Text = "Trail: waiting (" + profitPts.ToString("F1") + "/" + trailActivationPoints + "pt)";
                            lblTrailInfo.Foreground = Brushes.Gray;
                        }
                        else
                        {
                            lblTrailInfo.Text = "Trail: OFF";
                            lblTrailInfo.Foreground = Brushes.Gray;
                        }
                    }
                }
                else
                {
                    // Show specific reason why we're flat / not trading
                    if (dailyLimitHit)
                    {
                        lblStatus.Text       = "■ DAILY LOSS LIMIT — halted (" + dailyRealizedPnL.ToString("C0") + ")";
                        lblStatus.Foreground = Brushes.OrangeRed;
                    }
                    else if (dailyProfitHit)
                    {
                        lblStatus.Text       = "■ DAILY PROFIT TARGET — halted (" + dailyRealizedPnL.ToString("C0") + ")";
                        lblStatus.Foreground = Brushes.Gold;
                    }
                    else if (flattenFired)
                    {
                        lblStatus.Text       = "■ EOD flatten — done for today";
                        lblStatus.Foreground = Brushes.Orange;
                    }
                    else if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay)
                    {
                        lblStatus.Text       = "■ Max trades reached (" + dailyTradeCount + "/" + maxTradesPerDay + ") — done";
                        lblStatus.Foreground = Brushes.Orange;
                    }
                    else
                    {
                        int ct2 = ToTime(Time[0]);
                        bool inAutoHours = ct2 >= tradingStartTime && ct2 < tradingEndTime;
                        if (autoMode && !inAutoHours)
                        {
                            lblStatus.Text       = "○ Flat — outside auto hours";
                            lblStatus.Foreground = Brushes.Gray;
                        }
                        else if (autoMode)
                        {
                            double best = Math.Max(lastBullConfidence, lastBearConfidence);
                            string dir  = lastBullConfidence >= lastBearConfidence ? "Bull" : "Bear";
                            lblStatus.Text       = "● Flat — scanning (" + dir + " " + best.ToString("F0") + "% / need " + minSignalConfidence.ToString("F0") + "%)";
                            lblStatus.Foreground = best >= minSignalConfidence ? Brushes.LimeGreen : Brushes.CornflowerBlue;
                        }
                        else
                        {
                            lblStatus.Text       = "● Flat — manual mode";
                            lblStatus.Foreground = Brushes.CornflowerBlue;
                        }
                    }
                    lblPosition.Text     = "DCA: 0/" + dcaMaxPositions + "  |  Avg: —";
                    lblHiddenSL.Text     = "SL: —  (" + slPoints + "pt | " + (slPoints * 4) + "tk | $" + (slPoints * 20) + "/ct)";
                    lblHiddenTP.Text     = "TP: —  (" + tpPoints + "pt | " + (tpPoints * 4) + "tk | $" + (tpPoints * 20) + "/ct)";
                    if (lblTrailInfo != null)
                    {
                        lblTrailInfo.Text = trailEnabled ? "Trail: —" : "Trail: OFF";
                        lblTrailInfo.Foreground = Brushes.Gray;
                    }
                }

                // ─── VWAP value ───────────────────────────────────
                lblVwapVal.Text = "VWAP: " + (vwapValue > 0 ? vwapValue.ToString("F2") : "—");

                // ─── Confidence scores ────────────────────────────
                lblConfBull.Text       = "Bull: " + lastBullConfidence.ToString("F0") + "%";
                lblConfBull.Foreground = lastBullConfidence >= minSignalConfidence ? Brushes.LimeGreen : Brushes.Gray;

                lblConfBear.Text       = "Bear: " + lastBearConfidence.ToString("F0") + "%";
                lblConfBear.Foreground = lastBearConfidence >= minSignalConfidence ? Brushes.OrangeRed : Brushes.Gray;

                // ─── Trading hours status ───────────────────────
                if (lblTradeHours != null)
                {
                    int ct = ToTime(Time[0]);
                    bool inHours = ct >= tradingStartTime && ct < flattenTime;
                    if (dailyLimitHit)
                    {
                        lblTradeHours.Text       = "● Daily limit hit — trading halted";
                        lblTradeHours.Foreground  = Brushes.OrangeRed;
                    }
                    else if (flattenFired)
                    {
                        lblTradeHours.Text       = "● EOD flatten fired — done for today";
                        lblTradeHours.Foreground  = Brushes.Orange;
                    }
                    else if (inHours)
                    {
                        lblTradeHours.Text       = "● Trading allowed  (" + FormatTime(tradingStartTime) + "–" + FormatTime(flattenTime) + ")";
                        lblTradeHours.Foreground  = Brushes.LimeGreen;
                    }
                    else
                    {
                        lblTradeHours.Text       = "○ Outside hours  (" + FormatTime(tradingStartTime) + "–" + FormatTime(flattenTime) + ")";
                        lblTradeHours.Foreground  = Brushes.Gray;
                    }
                }
            });
        }

        // ─── Remove dashboard on termination ──────────────────────
        private void RemoveDashboard()
        {
            // Capture references before they go null
            var panel = dashboardPanel;
            var host  = dashboardHostPanel;
            var chart = ChartControl;

            // Clear state immediately on caller thread
            dashboardPanel     = null;
            dashboardHostPanel = null;
            dashboardAttached  = false;

            if (panel == null) return;
            if (chart == null)
            {
                // ChartControl already gone — try direct removal (we're likely on the UI thread during teardown)
                try
                {
                    if (host != null) host.Children.Remove(panel);
                }
                catch { }
                return;
            }

            try
            {
                // Use Invoke (blocking) — guarantees removal completes before Terminated finishes
                chart.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (host != null)
                            host.Children.Remove(panel);
                        else if (chart.Parent is Grid g)
                            g.Children.Remove(panel);
                    }
                    catch { }
                });
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════
        //  PROPERTIES  (NinjaTrader strategy dialog)
        // ═══════════════════════════════════════════════════════════

        #region Properties

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Stop Loss (NQ points)", Order = 1, GroupName = "1 — Risk Management",
                 Description = "Hidden SL distance from average entry. Adjustable on dashboard with +/- buttons. Min 1 point.")]
        public int SlPoints { get { return slPoints; } set { slPoints = value; } }

        [NinjaScriptProperty]
        [Range(1, 500)]
        [Display(Name = "Take Profit (NQ points)", Order = 2, GroupName = "1 — Risk Management",
                 Description = "Hidden TP distance from average entry. Adjustable on dashboard with +/- buttons. Min 1 point.")]
        public int TpPoints { get { return tpPoints; } set { tpPoints = value; } }

        [NinjaScriptProperty]
        [Range(500, 10000)]
        [Display(Name = "Max Daily Loss ($)", Order = 3, GroupName = "1 — Risk Management",
                 Description = "Hard daily loss cap — all trading halts and position is flattened when hit.")]
        public int MaxDailyLossDollars { get { return maxDailyLossDollars; } set { maxDailyLossDollars = value; } }

        [NinjaScriptProperty]
        [Range(500, 20000)]
        [Display(Name = "Daily Profit Target ($)", Order = 4, GroupName = "1 — Risk Management",
                 Description = "Alert when daily profit target is reached.")]
        public int MaxDailyProfitDollars { get { return maxDailyProfitDollars; } set { maxDailyProfitDollars = value; } }

        [NinjaScriptProperty]
        [Range(0, 20)]
        [Display(Name = "Max Trades Per Day", Order = 5, GroupName = "1 — Risk Management",
                 Description = "Maximum round-trip trades per session (0 = unlimited). Includes auto and manual entries.")]
        public int MaxTradesPerDay { get { return maxTradesPerDay; } set { maxTradesPerDay = value; } }

        [NinjaScriptProperty]
        [Range(1, 10)]
        [Display(Name = "Contracts per entry", Order = 6, GroupName = "1 — Risk Management",
                 Description = "Default contracts per entry. Adjustable on dashboard.")]
        public int Contracts { get { return contracts; } set { contracts = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Enable DCA", Order = 1, GroupName = "2 — DCA Settings",
                 Description = "Allow adding to position by pressing LONG/SHORT again.")]
        public bool DcaEnabled { get { return dcaEnabled; } set { dcaEnabled = value; } }

        [NinjaScriptProperty]
        [Range(1, 4)]
        [Display(Name = "Max DCA Adds", Order = 2, GroupName = "2 — DCA Settings",
                 Description = "Maximum number of position entries (first + adds).")]
        public int DcaMaxPositions { get { return dcaMaxPositions; } set { dcaMaxPositions = value; } }

        [NinjaScriptProperty]
        [Range(0, 200)]
        [Display(Name = "DCA Min Distance (NQ points)", Order = 3, GroupName = "2 — DCA Settings",
                 Description = "Minimum distance from avg entry before DCA add. 0 = no restriction.")]
        public int DcaDistancePoints { get { return dcaDistancePoints; } set { dcaDistancePoints = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Auto Mode (default)", Order = 1, GroupName = "3 — Trading Mode",
                 Description = "Initial mode. Toggleable on dashboard: Manual = buttons only, Auto = algorithm entries.")]
        public bool AutoMode { get { return autoMode; } set { autoMode = value; } }

        [NinjaScriptProperty]
        [Range(0, 4)]
        [Display(Name = "Auto Strategy (0-4)", Order = 2, GroupName = "3 — Trading Mode",
                 Description = "0=Momentum+VWAP  1=Key Level Breakout  2=Liquidity Sweep  3=ORB  4=Auto Select. Changeable on dashboard.")]
        public int AutoStrategy { get { return autoStrategy; } set { autoStrategy = value; } }

        [NinjaScriptProperty]
        [Range(50, 100)]
        [Display(Name = "Min Signal Confidence (%)", Order = 3, GroupName = "3 — Trading Mode",
                 Description = "Auto mode only enters when composite confidence meets this threshold.")]
        public double MinSignalConfidence { get { return minSignalConfidence; } set { minSignalConfidence = value; } }

        [NinjaScriptProperty]
        [Range(0, 120)]
        [Display(Name = "Entry Delay (seconds)", Order = 4, GroupName = "3 — Trading Mode",
                 Description = "Cooldown between entries to prevent double-clicks.")]
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

        [NinjaScriptProperty]
        [Range(1, 50)]
        [Display(Name = "SL/TP Adjust Step (points)", Order = 1, GroupName = "5 — Display",
                 Description = "How many points each +/- click adjusts SL or TP.")]
        public int SlTpAdjustStep { get { return slTpAdjustStep; } set { slTpAdjustStep = value; } }

        [NinjaScriptProperty]
        [Range(10, 95)]
        [Display(Name = "Jump SL % (toward price)", Order = 2, GroupName = "5 — Display",
                 Description = "Percentage of gap to jump SL closer to current price. 50% = halfway, 75% = very close.")]
        public int JumpSlPercent { get { return jumpSlPercent; } set { jumpSlPercent = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show EMA on chart", Order = 2, GroupName = "5 — Display")]
        public bool ShowEma { get { return showEma; } set { showEma = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show RSI panel", Order = 3, GroupName = "5 — Display")]
        public bool ShowRsi { get { return showRsi; } set { showRsi = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show ATR panel", Order = 4, GroupName = "5 — Display")]
        public bool ShowAtr { get { return showAtr; } set { showAtr = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP line", Order = 5, GroupName = "5 — Display")]
        public bool ShowVwap { get { return showVwap; } set { showVwap = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show Key Levels (20-bar H/L)", Order = 6, GroupName = "5 — Display",
                 Description = "Draw 20-bar high/low resistance and support lines.")]
        public bool ShowKeyLevels { get { return showKeyLevels; } set { showKeyLevels = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show Sweep Signals", Order = 7, GroupName = "5 — Display",
                 Description = "Draw liquidity sweep arrows and 10-bar swing levels.")]
        public bool ShowSweepSignals { get { return showSweepSignals; } set { showSweepSignals = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trading Start (HHMMSS ET)", Order = 1, GroupName = "6 — Trading Hours",
                 Description = "Earliest time new entries are allowed. Default 93000 = 9:30 AM ET.")]
        public int TradingStartTime { get { return tradingStartTime; } set { tradingStartTime = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Trading End (HHMMSS ET)", Order = 2, GroupName = "6 — Trading Hours",
                 Description = "Latest time the session is considered active. Default 160000 = 4:00 PM ET.")]
        public int TradingEndTime { get { return tradingEndTime; } set { tradingEndTime = value; } }

        [NinjaScriptProperty]
        [Range(0, 235959)]
        [Display(Name = "Auto-Flatten Time (HHMMSS ET)", Order = 3, GroupName = "6 — Trading Hours",
                 Description = "Auto-flatten any open position at this time. Default 155900 = 3:59 PM ET.")]
        public int FlattenTime { get { return flattenTime; } set { flattenTime = value; } }

        // ─── Adaptive Trailing Stop properties ────────────────────

        [NinjaScriptProperty]
        [Display(Name = "Enable Adaptive Trail", Order = 1, GroupName = "7 — Adaptive Trail",
                 Description = "Hidden adaptive trailing stop. Adjusts distance based on market regime: aggressive in chop, conservative in trends.")]
        public bool TrailEnabled { get { return trailEnabled; } set { trailEnabled = value; } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trail Activation (NQ points)", Order = 2, GroupName = "7 — Adaptive Trail",
                 Description = "Minimum unrealized profit in NQ points before adaptive trail activates. Prevents premature trailing.")]
        public int TrailActivationPoints { get { return trailActivationPoints; } set { trailActivationPoints = value; } }

        [NinjaScriptProperty]
        [Range(1, 100)]
        [Display(Name = "Trail Min Distance (NQ points)", Order = 3, GroupName = "7 — Adaptive Trail",
                 Description = "Tightest trailing distance used in choppy/ranging markets. Locks gains aggressively. Default 4 pts ($80/ct).")]
        public int TrailMinPoints { get { return trailMinPoints; } set { trailMinPoints = value; } }

        [NinjaScriptProperty]
        [Range(2, 200)]
        [Display(Name = "Trail Max Distance (NQ points)", Order = 4, GroupName = "7 — Adaptive Trail",
                 Description = "Widest trailing distance used in trending markets. Lets profits run. Default 25 pts ($500/ct).")]
        public int TrailMaxPoints { get { return trailMaxPoints; } set { trailMaxPoints = value; } }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "Trail ATR Multiplier", Order = 5, GroupName = "7 — Adaptive Trail",
                 Description = "ATR multiplier blended with regime distance. Higher = wider trail. Default 1.5.")]
        public double TrailAtrMultiplier { get { return trailAtrMultiplier; } set { trailAtrMultiplier = value; } }

        #endregion
    }
}
