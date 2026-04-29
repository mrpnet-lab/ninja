// ============================================================
//  Mm-ATM v6.0 Strategy for NinjaTrader 8
//  Complete rewrite: simplified architecture, 12 filters, unified exits,
//  smart-trail backtrack vs MM stop-hunts, optional order-flow tape filter,
//  redesigned dashboard with explicit BUY/SELL trade-signal indicator.
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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class Mm_ATM_v6 : Strategy
    {
        // ===========================================================
        //  CONSTANTS
        // ===========================================================
        #region Constants
        private const double NQ_TICKS_PER_POINT  = 4.0;
        private const double NQ_DOLLARS_PER_POINT = 20.0;
        private const int    DASH_UPDATE_MS       = 333;
        private const int    AGGRESSIVE_LIMIT_TIMEOUT_SEC = 3;
        private const int    STALE_EXIT_TICKS     = 250;
        private const int    FLAT_SYNC_MAX_TICKS  = 200;
        private const string TAG = "[Mm-ATM v6] ";
        #endregion

        // ===========================================================
        //  USER PARAMETERS (backing fields)
        // ===========================================================
        #region User parameter fields
        private int    slPoints;
        private int    tpPoints;
        private int    maxDailyLossDollars;
        private int    maxDailyProfitDollars;
        private int    maxTradesPerDay;
        private int    contracts;
        private int    maxContracts;
        private bool   autoMode;
        private int    autoStrategy;          // 0=Mom+VWAP, 1=KeyLvl, 2=Auto
        private double minSignalConfidence;
        private int    entryDelaySeconds;
        private int    emaPeriodFast;
        private int    emaPeriodSlow;
        private int    rsiPeriod;
        private int    atrPeriod;
        private int    htfEmaPeriod;
        private int    slTpAdjustStep;
        private int    jumpSlPercent;
        private int    breakevenAtPoints;
        private bool   breakevenEnabled;          // master toggle (button on dashboard mirrors this)
        private bool   allowMultiEntryPerBar;     // when true, enteredThisBar guard is bypassed
        private bool   trailEnabled;
        private int    trailActivationPoints;
        private double trailAtrMultiplier;
        private int    smartTrailBacktrackTicks;
        private bool   orderFlowFilterEnabled;
        private bool   enableTrapDetector;
        private int    tradingStartTime;
        private int    flattenTime;
        private bool   tradingHoursEnabled;
        private bool   showVwap;
        private bool   showEma;
        private bool   enableDiagLog;
        // ---- v6 Phase 0.2 Renko (data plumbing only — no logic uses these yet) ----
        // Default ON so brick state shows up in diag log; toggleable from property panel.
        // Read by ProcessRenkoBar (BarsInProgress==2) and WriteDiagRow (brickColor/brickStreak cols).
        private bool   enableRenkoSeries;
        private int    renkoBrickSize;       // ticks per brick (NinzaRenko equiv: 64)
        private int    renkoBrickOffset;     // reversal offset in ticks (NinzaRenko equiv: 16)
        // Stock NT8 Renko (BarsArray[2]) — sanity-comparison series
        private string lastBrickColor;       // "G" / "R" / ""
        private int    brickStreakCount;     // consecutive same-color bricks (1 on first)
        private double lastBrickHigh, lastBrickLow, lastBrickClose;
        private int    renkoBarsSeen;
        private int    lastProcessedRenkoBar; // dedupe — set to CurrentBars[2] of last brick processed
        private bool   seriesAnnounced;       // one-shot diag print of BarsArray composition
        // v6 0.2.3 — NinzaRenko (third-party) on BarsArray[3] via NT8 Custom0..Custom9 slot.
        // This is the PRIMARY brick signal (matches what user trades off visually).
        // Stock Renko (BarsArray[2]) is kept as a sanity comparison.
        private bool   enableNinzaRenkoSeries;   // default ON
        private int    ninzaCustomSlot;          // 0..9 — which Custom slot NinzaRenko is registered to in NT8
        private bool   ninzaSeriesAdded;         // true if AddDataSeries call succeeded at Configure
        private bool   usePrimaryAsNinzaRenko;   // v6 0.2.4 — read NR brick color from primary BarsArray[0] (typical setup)
        private string lastNrBrickColor;         // NinzaRenko brick color
        private int    nrBrickStreakCount;       // NinzaRenko streak
        private double lastNrBrickHigh, lastNrBrickLow, lastNrBrickClose;
        private int    nrBarsSeen;
        private int    lastProcessedNrBar;
        private const int NINZA_BIP = 3;         // BarsArray index for NinzaRenko

        // ---- v6 1.1 — F1 Regime Classifier (label-only) ----
        // Pure observation. Populates `Regime` diag column + dashboard row (Phase 1.2).
        // NO entry/exit logic reads currentRegime yet — that arrives in Phase 2.x.
        private bool   enableRegimeClassifier;   // default OFF
        private string currentRegime = "UNKNOWN";
        private string prevRegime    = "UNKNOWN";
        private int    regimeChangedBar;          // CurrentBar of last regime transition (for dashboard age)
        private int    regimeFlipsLast20;         // count of NR brick color flips in last 20 bricks (chop signal)

        // ---- v6 1.2/1.3/1.4 — Brick Run Tracker / Wick Analyzer / MM Pattern Recorder ----
        // All observation-only. Default OFF. When ON, populate diag log with RUN_END / WICK_TAG /
        // MM_PATTERN rows that we mine to design Phase 2 entry/exit/trail logic.
        // Goal: identify how many bricks the typical "runner" trend lasts so the trail can ride
        // monster moves (we observed streaks of 30 bricks = 480 NQ pts on one playback day).
        private bool   enableBrickAnalytics;     // master toggle for Phase 1.2-1.4 observation

        // ---- v6 2.1/2.2 — Beat-the-MM Block Bypasses (ACTIVE LOGIC, default OFF) ----
        // Phase 2.1 Smart Cooldown: shrinks the 7-min post-win cooldown to 60s when conditions prove
        // we're in a real trend continuation (not the MM stop-run + fade pattern the cooldown protects against).
        // Phase 2.2 Extension TREND-Bypass: lets us follow strong trends past the VWAP-extension guard.
        // BOTH default OFF for safety. They are RISK-INCREASING tools — they create more entries by relaxing guards.
        // Only enable AFTER you've validated EnableRegimeClassifier and EnableBrickAnalytics on the same session.
        private bool   enableSmartCooldown;        // Phase 2.1
        private int    smartCooldownMinSec     = 60;   // shortened cooldown when bypass triggers
        private int    smartCooldownStreakMin  = 4;    // require this brick-streak in trend dir to bypass
        private bool   enableExtensionTrendBypass; // Phase 2.2
        private int    extensionBypassStreakMax = 8;   // only bypass extension if streak <= this (still avoid late chases)
        // adxSlope cached from regime classifier so entry guards can read it without recomputing.
        private double lastAdxSlope;

        // ---- v6 2.3 — Brick-Trail Mode (lets monster runs run; default OFF) ----
        // 2026-04-28 WIN#2 forensic: entered SHORT @ 27126.50 brick #9, trail kicked us out at
        // 27106.50 brick #15 (+20pt = $400). Run continued to brick #30 with maxFav=132pt = $2640
        // missed. Brick-trail solves it: trail price = previous closed brick's far extreme + buffer.
        // Each new same-direction brick ratchets the trail one brick at a time. First opposite-color
        // brick that prints will pierce the trail instantly = clean exit on actual reversal signal.
        private bool   enableBrickTrail;            // master toggle, default OFF
        private int    brickTrailMinStreak = 4;     // require >= N same-color bricks before brick-trail engages
        private double brickTrailBufferTicks = 4;   // ticks above prev-brick-high (SHORT) / below low (LONG)
        private bool   brickTrailRequireTrendRegime = true; // require regime=TREND_DN (SHORT) / TREND_UP (LONG)
        // v6 2.6 — Brick-Trail v2: brick-close-based exit (wick-immune) + body-extreme anchor.
        // streakMinBodyHigh = running min of brick BODY high (max(open,close)) across current streak — SHORT anchor.
        // streakMaxBodyLow  = running max of brick BODY low  (min(open,close)) across current streak — LONG  anchor.
        // Body (not wick) ignores intra-brick spikes that MM uses to fake reversals.
        private double streakMinBodyHigh = double.MaxValue;
        private double streakMaxBodyLow  = double.MinValue;
        // Set by ProcessPrimaryAsNinzaRenkoBar when a NEW brick of OPPOSITE color closes while we hold a position
        // and brick-trail is enabled. MonitorAdaptiveTrail consumes the flag and exits at market.
        private bool   pendingBrickFlipExit;
        // Max % of peak profit we'll allow to bleed before we exit anyway (catches stalls/wicks where
        // bricks haven't flipped yet but the move is clearly dying). 0 = disabled (recommended). Default 0.
        // ONLY fires once peak profit >= BrickTrailGivebackMinPeakPts (default 20pt) so small winners aren't
        // whip-sawed by ordinary consolidations. STALL-GATE: only fires after no new same-color brick has
        // closed for BrickTrailGivebackStallSec seconds (default 45) — so an active run is never interrupted.
        private int      brickTrailMaxGivebackPct       = 0;
        private double   brickTrailGivebackMinPeakPts   = 20.0;
        private int      brickTrailGivebackStallSec     = 45;
        private DateTime lastSameColorBrickTime         = DateTime.MinValue;
        // v6 2.6.2 - PROFIT-LOCK at brick close (catches violent V-reversal bricks that finish past breakeven).
        // Independent of giveback (which is tick-based / time-gated). Profit-lock fires at the SAME bar a
        // big reversal brick closes, so a single 20pt+ reversal brick can't wipe a 30pt+ peak winner.
        // v6 2.6.3 - DISABLED BY DEFAULT (giveback% = 0). Was killing monster runs on continuation bricks
        // due to peak being tick-based and profit-at-close being body-based (apples-vs-oranges). The
        // intended use case (V-reversal) is already covered by brick-flip exit. In-bar tick trail (below)
        // is the better tool for catching V-reversals BEFORE the opposite brick closes.
        private double   brickTrailProfitLockMinPeakPts    = 20.0;
        private int      brickTrailProfitLockGivebackPct   = 0;
        // v6 2.6.3 - IN-BAR TICK TRAIL (the user-requested aggressive mid-brick trail).
        // Once peak profit reaches MinPeakPts, exit immediately if intra-bar tick price retreats from
        // peak by GivebackPts. WICK-VULNERABLE by design - acceptable because we can re-enter on the
        // same trend if it resumes. Default GivebackPts=16 (one brick height) is wide enough to ignore
        // normal continuation-brick wicks but will catch any genuine V-reversal long before the opposite
        // brick closes (which has a structural ~20pt cost from the NinzaRenko 16-tick offset).
        private bool     inBarTrailEnabled                  = true;
        private double   inBarTrailMinPeakPts               = 25.0;
        private double   inBarTrailGivebackPts              = 16.0;
        // (v6 2.5 N-back lookback was REMOVED in 2.6 — the body-extreme anchor is monotonic by construction
        //  and exit is brick-close based, so an Nth-back anchor is no longer needed.)
        // Cache prior brick high/low so trail can ratchet one brick behind the just-closed brick.
        private double prevNrBrickHigh;
        private double prevNrBrickLow;

        // ---- v6 2.4 — UNKNOWN-Regime Auto-Block (prevents the -$370/-$375 trades; default OFF) ----
        // 2026-04-28 forensic: BOTH losses (LOSS#1 R×1 -$370, LOSS#2 R×8 -$375) entered with
        // currentRegime="UNKNOWN". The classifier is uncertain ⇒ we should be too. This guard
        // blocks AUTO entries when regime=UNKNOWN unless the brick streak + ADX slope prove
        // momentum is real. Manual entries always pass (you override).
        private bool enableUnknownRegimeBlock;       // default OFF
        private int  unknownBlockMinStreak = 6;      // require streak >= N to enter in UNKNOWN
        private double unknownBlockMinAdxSlope = 0;  // require ADX slope >= this to enter in UNKNOWN
        // Run tracker (live counters):
        private int    runCurrentLen;             // = nrBrickStreakCount but kept independent in case
        private string runCurrentColor = "";      // "G"/"R"
        private double runStartPrice;             // Open of first brick in current run
        private double runMaxFavPts;              // best favorable excursion within the run (pts from runStartPrice)
        private DateTime runStartTime;
        private int    runMaxLast10 = 0;          // moving max of last 10 completed runs (regime helper)
        private System.Collections.Generic.Queue<int> runLast10 = new System.Collections.Generic.Queue<int>();
        // Brick speed (inter-arrival):
        private DateTime lastBrickTime;
        private double   lastBrickIntervalSec;
        private int      fastBricksLast10;        // count of <10sec bricks in last 10
        private System.Collections.Generic.Queue<double> brickIntervalsLast10 = new System.Collections.Generic.Queue<double>();
        // MM pattern ring buffer (last 20 bricks):
        private System.Collections.Generic.Queue<string> brickColorRing = new System.Collections.Generic.Queue<string>();
        private DateTime lastMmPatternEmit;
        // Wick analytics on most recent brick:
        private double   lastBrickBodyPts;
        private double   lastBrickWickUpPts;
        private double   lastBrickWickDnPts;
        private double   lastBrickWickRatio;     // (wickUp+wickDn)/body
        #endregion

        // ===========================================================
        //  INDICATORS
        // ===========================================================
        #region Indicators
        private EMA indEmaFast, indEmaSlow, indEmaHtf, indEma5mFast, indEma5mSlow;
        private RSI indRsi;
        private ATR indAtr;
        private ADX indAdx;
        #endregion

        // ===========================================================
        //  STATE — daily/session
        // ===========================================================
        #region Session state
        private DateTime sessionDate;
        private DateTime lastFuturesSessionResetDate;  // tracks the 18:00 ET futures session boundary
        private double   dailyRealizedPnL;
        private int      dailyTradeCount;
        private int      processedTradeCount;
        private bool     dailyLimitHit;
        private bool     dailyProfitHit;
        // True when ArmHiddenStops/ResizeHiddenStops had to pull the hidden TP in to a prevDay
        // level. Used to FORCE the TP check to honor the hidden target even in runner mode
        // (otherwise the visible green line is at e.g. entry+18pt while the runner-mode bypass
        // skips the comparison and the price flies past it).
        private bool     tpClampedByPrevDay;
        private bool     emergencyKillActive;
        private bool     flattenFired;
        private bool     rthStartedToday;
        private double   prevDayHigh, prevDayLow, prevDayClose, prevDayOpen;
        private double   currDayHigh, currDayLow, currDayOpen;
        private bool     firstBarSeen;
        #endregion

        // ===========================================================
        //  STATE — position / order management
        // ===========================================================
        #region Position state
        private int      openTradeDirection;       // 1=long, -1=short, 0=flat
        private double   averageEntryPrice;
        private double   totalContracts;
        private int      openDcaCount;
        private int      tradeSequence;
        private double   hiddenStopPrice;
        private double   hiddenTargetPrice;
        private double   originalSlPrice;          // first-arm reference for SmartSL backtrack
        private bool     stopsArmed;
        private bool     breakevenLocked;
        private bool     runnerModeActive;
        private int      entryBar;
        private DateTime lastEntryWallTime;
        private DateTime aggressiveLimitSubmitTime;
        private bool     enteredThisBar;           // single-shot guard per bar
        // Originals snapshotted at DataLoaded so each new arm RESETS to user-configured values
        // (not the previous trade's nudged values).
        private int      origSlPoints, origTpPoints, origJumpSlPercent;
        private bool     originalsSnapshotted;
        private readonly List<string> activeEntrySignals = new List<string>();
        #endregion

        // ===========================================================
        //  STATE — pending button flags (volatile = thread-safe)
        // ===========================================================
        #region Pending flags
        private volatile bool pendingLong, pendingShort;
        private volatile bool pendingBuyAsk, pendingSellBid;
        private volatile bool pendingLongLimit, pendingShortLimit;
        private volatile bool pendingFlatten, pendingCloseTrade, pendingCloseOne;
        private volatile bool pendingJumpSL, pendingRearm, pendingSlTpResize, pendingEmergencyKill;
        private volatile bool pendingResetDaily;
        private volatile bool pendingTrailActivate;
        private volatile int  pendingTrailNudgePoints;          // signed: + = looser (away from price), − = tighter
        private volatile int  pendingSlNudge;                    // signed: + = TIGHTEN (toward price), − = WIDEN (away from price)
        private double        trailNudgeStepPoints = 1.0;       // 1 NQ point = 4 ticks = $20
        private double        aggressiveTrailMaxAtrFactor = 0.5; // TRL NOW initial distance = max(2pt, factor×ATR)
        private double        beSafeAtrFactor             = 0.35; // Smart BE: SL must stay >= max(beSafeMinTicks, factor×ATR) below price
        private int           beSafeMinTicks              = 6;    // Hard floor in ticks (1.5pt on NQ) so MM stop-hunts can't tag us
        // ----- Streak-adaptive trail (auto-tighten on losses / auto-widen on wins) -----
        private bool          autoTightenOnLossesEnabled  = false;
        private int           autoTightenLossN            = 2;    // after N consecutive losses
        private double        autoTightenFactor           = 0.5;  // multiply aggressiveTrailMaxAtrFactor by this (lower = tighter)
        private bool          autoWidenOnWinsEnabled      = false;
        private int           autoWidenWinN               = 3;    // after N consecutive wins
        private double        autoWidenFactor             = 1.5;  // multiply aggressiveTrailMaxAtrFactor by this (higher = looser)
        private int           consecutiveWins;                    // win-side streak counter (mirror of consecutiveLosses)
        private double        baseAggressiveTrailFactor;          // snapshot for restoring after streak adjustment
        // ----- ADX-aware prevDay TP-clamp skip -----
        private bool          skipPrevDayClampOnHighAdx   = true;
        private double        highAdxThreshold            = 28.0;
        // ----- Last exit reason (used to differentiate EXIT_* tags in the CSV) -----
        // Set by the code path that triggers the ExitLong/ExitShort; consumed by OnExecutionUpdate when SystemPerformance reports the trade.
        private string        lastExitReason              = "UNKNOWN";
        // ----- Reset-daily-on-restart -----
        private bool          resetDailyOnRestart         = true;
        // ----- Fast reversal exit (item 4: smarter than MM/algo sweep) -----
        // Within first N bars after entry, if adverse move > factor×ATR AND tape/EMA confirm
        // the reversal, exit at market BEFORE hidden SL would trigger — cuts loss in half.
        private bool          fastReversalExitEnabled     = true;
        private double        fastReversalAtrFactor       = 0.6;   // adverse > 0.6×ATR triggers consideration
        private int           fastReversalMaxBars         = 4;     // only within first N bars after entry
        private double        fastReversalAdverseMinPts   = 4.0;   // hard floor: ignore tiny adverse moves
        private int           lastFastReversalBar         = -1;    // throttle so we only fire once per trade
        // ----- Aggressive Exits Mode (manual + auto, default OFF) -----
        // When ON: BE locks earlier, trail starts faster + tighter, pullback after peak triggers exit.
        private bool          aggressiveExitsEnabled      = true;
        // ----- Runner Mode (per-trade override; user toggles on dashboard) -----
        // When ON, the trail uses a SINGLE wide distance (RunnerAtrFactor × ATR, floor 4pt) and
        // disables: profit-aggression multipliers, time ratchet, trap-tighten, EMA-tighten, AGGR
        // override, and tier floors (so a strong runner doesn't get cut by 65% peak floor).
        // BE / hidden SL / structural snap still work. Auto-clears when position goes flat.
        private bool          runnerModeActive_user       = false;
        private double        runnerAtrFactor             = 1.5;
        private double        runnerMinPts                = 4.0;
        private int           aggrBeAtPoints              = 3;     // BE locks at +3pt instead of breakevenAtPoints
        private double        aggrTrailActivationPts      = 4.0;   // Trail activates at +4pt
        private double        aggrTrailDistPts            = 2.0;   // Trail distance 2pt
        private double        aggrPullbackAtrFactor       = 0.4;   // Adverse 0.4xATR after peak -> exit
        private int           aggrPullbackMaxBars         = 2;     // Pullback exit valid within N bars after entry
        // ----- AGGR adverse-exit (anti stop-hunt) — OPT-IN, default OFF -----
        // History: tested ON with various tunings (0.5*ATR, 0.4*ATR) and consistently produced
        // worse results than letting the static SL handle losses. Reason: 1-min bars on a high-ATR
        // (>20pt) NQ morning routinely have 10-15pt intrabar wicks, so any adverse-trigger near or
        // below that threshold fires on the entry bar's first tick — BEFORE the bar plays out.
        // The AGGR_PULLBACK exit (which only arms after trade reaches activation profit) plus the
        // static SL handles loss control correctly. Leave OFF.
        private bool          aggrAdverseExitEnabled      = false; // Default OFF.
        private int           aggrAdverseMaxBars          = 2;
        private double        aggrAdverseAtrFactor        = 0.5;
        private double        aggrAdverseMinPts           = 5.0;   // Floor raised from 3 — if you DO opt in, set higher to avoid same-bar noise
        private double        aggrAdverseDisarmPeak       = 3.0;
        // ----- AGGR static-SL cap — OFF by default; raise only if you want a hard cap. -----
        // NOTE: 12pt cap was tested and WORSENED results because NQ first-bar wicks routinely
        // hit 10-15pt on volatile mornings, killing trades that would have run to peak +22pt.
        // Leave at 0 (disabled). The AGGR adverse + pullback exits already protect capital correctly.
        private double        aggrSlCapPoints             = 0.0;   // 0=disabled. When >0 and AGGR ON, SL distance capped at this many points.
        // ----- SL-cluster cooldown — pauses entries after consecutive SL exits in a short window -----
        private bool          slClusterCooldownEnabled    = true;
        private int           slClusterCount              = 2;     // After this many SL exits within window -> cooldown
        private int           slClusterWindowMin          = 60;    // Sliding window in minutes
        private int           slClusterCooldownMin        = 15;    // Block new entries for this many minutes
        private DateTime[]    slExitTimes;                          // ring buffer of recent SL-exit timestamps
        private int           slExitTimesIndex;
        private DateTime      slClusterCooldownUntil      = DateTime.MinValue;
        // ----- Post-win same-direction cooldown (anti trail-and-trap) -----
        // After a winning exit, the MM often reverses price 1-2 bars to clear stops above the recent
        // pullback before resuming trend. If we re-enter same direction within ~5min, we get caught
        // by that very wick. This cooldown blocks same-direction re-entries for a short window after
        // any winning exit. Doesn't apply after losses (re-entry post-loss is fine if structure justifies).
        private bool          postWinSameDirCooldownEnabled = true;
        private int           postWinSameDirCooldownMin     = 7;     // minutes to block same-direction re-entry after a win
        private DateTime      lastWinExitTime               = DateTime.MinValue;
        private int           lastWinExitDirection          = 0;     // +1=long win, -1=short win
        // ----- Directional lockout (per-direction loss-streak block) -----
        // After N losing SL/AGGR_ADVERSE exits in the SAME direction within a sliding window, lock
        // that direction for L minutes. Defends the pattern seen on 2026-03-20 PM where the strategy
        // kept short-entering at fresh local lows (3 consecutive SL losses 10:05/10:29/10:56) — each
        // entry was caught by a stop-running up-wick before the trend resumed. SL_CLUSTER is direction
        // agnostic and fires too late; this layer surgically blocks repeat losers in one direction
        // while still allowing the OPPOSITE direction (so a true reversal can be taken).
        private bool          dirLockoutEnabled             = true;
        private int           dirLockoutLossN               = 2;     // number of same-direction SL/ADVERSE losses to trigger
        private int           dirLockoutWindowMin           = 30;    // sliding window in minutes
        private int           dirLockoutCooldownMin         = 30;    // block that direction for this many minutes
        private System.Collections.Generic.List<DateTime> dirLossTimesLong = new System.Collections.Generic.List<DateTime>();
        private System.Collections.Generic.List<DateTime> dirLossTimesShort = new System.Collections.Generic.List<DateTime>();
        private DateTime      longLockoutUntil              = DateTime.MinValue;
        private DateTime      shortLockoutUntil             = DateTime.MinValue;
        // ----- Extension filter (don't chase late entries far from VWAP) -----
        // Empirically on 2026-03-20: every losing entry was at |close-vwap|/ATR > 6.0; the only big
        // winner (+$1,125) was taken at -4.5×ATR — the breakout, not the chase. After ~5×ATR
        // extension, MM has plenty of room to ramp price for a stop-run before the trend resumes,
        // and our 18pt SL is too tight to absorb that bounce. This filter blocks entries when price
        // is already over-extended from VWAP relative to current ATR, allowing the strategy to wait
        // for either a pullback or fresh consolidation — not chase the move.
        private bool          extensionFilterEnabled        = true;
        private double        extensionMaxAtrFromVwap       = 5.0;   // block entry if |close-vwap| > N × ATR
        private double        extensionMinAtrPoints         = 8.0;   // only enforce when ATR >= this (avoids over-blocking quiet sessions)
        // ----- Chop filter (default ON, applies to manual + auto) -----
        private bool          chopFilterEnabled           = true;
        private double        chopAdxMin                  = 18.0;  // ADX below this is considered chop
        private int           chopAdxFallingBars          = 3;     // ADX must be falling for this many bars
        private double        chopEmaSepMinAtr            = 0.30;  // EmaFast-EmaSlow gap < this x ATR -> no trend
        private int           chopRangeBars               = 5;     // Last N closes range
        private double        chopRangeMaxAtr             = 1.0;   // If close-range < this x ATR -> chop
        private bool          chopBlockOppositeTape       = true;  // Block when tape sign opposite to entry direction
        private double        chopOppositeTapeMin         = 0.05;  // Min |tape| to count as opposite
        private double        chopTrendAdxGate            = 22.0;  // If ADX >= this, skip EMA-gap & range tests (consolidation in trend)
        // ----- Adaptive intra-day window (default ON) -----
        // Tracks last N trade outcomes; if losses >= threshold, tightens entries until cleared.
        private bool          adaptiveWindowEnabled       = true;
        private int           adaptiveWindowSize          = 5;
        private int           adaptiveWindowLossThreshold = 3;
        private int           adaptiveWindowClearWins     = 2;     // N consecutive wins clears tightening
        private int           adaptiveConfBoost           = 5;     // +N min-confidence while tightened
        private int[]         recentTradeOutcomes;                  // ring buffer: 1=win, -1=loss, 0=empty
        private int           recentTradeIndex;
        private bool          adaptiveTightenActive;                // true while window is in protect-mode
        private int           adaptiveWinsSinceTighten;             // consecutive wins after tighten activated
        // ----- Time-of-day SL sizing (item: customizable) -----
        // SL = baseSlPoints × multiplier_for_current_window. Three customizable windows.
        // If multiple overlap, FIRST match wins (Open > Close > Midday in eval order).
        private bool          timeOfDaySlSizingEnabled    = false;
        private int           sodOpenStart                = 93000;
        private int           sodOpenEnd                  = 103000;
        private double        sodOpenSlMult               = 1.30;   // wider on the open (volatile)
        private int           sodMiddayStart              = 103000;
        private int           sodMiddayEnd                = 140000;
        private double        sodMiddaySlMult             = 0.80;   // tighter in chop
        private int           sodCloseStart               = 150000;
        private int           sodCloseEnd                 = 160000;
        private double        sodCloseSlMult              = 1.20;   // wider near close (whipsaw)
        // ----- News blackout window (item: customizable) -----
        // Block ALL entries within ±NewsBlackoutWindowMin of any time in NewsBlackoutTimes.
        // Times are comma-separated HHMMSS (e.g. "083000,100000,140000").
        private bool          newsBlackoutEnabled         = true;
        private string        newsBlackoutTimes           = "083000,100000,140000";  // CPI/PPI, ISM, FOMC
        private int           newsBlackoutWindowMin       = 2;
        private int[]         newsBlackoutMinutesCache;             // parsed from newsBlackoutTimes (minutes since midnight)
        // ----- Liquidity-sweep entry boost (item: smarter MM-detection bonus) -----
        // If last bar wicked beyond N-bar high/low then closed back inside (classic stop-run),
        // BOOST opposite-direction signals: bypass overextension + lower effective min-confidence.
        private bool          liquiditySweepBoostEnabled  = true;
        private int           liquiditySweepLookback      = 30;
        private double        liquiditySweepConfBoost     = 8.0;    // points subtracted from effMin
        // ----- Auto-DCA disable on loss streak -----
        private bool          suppressDcaOnLossStreak     = true;
        private int           suppressDcaLossN            = 2;
        private volatile bool pendingPositionFlat;
        private volatile bool pendingExit;
        private int  pendingExitTicks;
        private int  flatSyncGraceTicks;
        #endregion

        // ===========================================================
        //  STATE — adaptive trail
        // ===========================================================
        #region Trail state
        private bool   trailActive;
        private bool   manualTrailEarlyStart;  // TRL NOW set this -> activation profit threshold bypassed
        private double manualTrailOffsetPoints; // signed offset added to auto trail distance (− = tighter, + = looser)
        private double trailPrice;
        private double trailMaxProfitPts;
        private double trailTrendScore;
        private string trailTierName = "";
        private double cachedTrailDistance;
        private int    cachedTrailBar = -1;
        private DateTime lastTrailDiagTime = DateTime.MinValue;
        private int    backtrackUsedBar = -1;       // bar# when MM-avoidance backtrack last applied
        #endregion

        // ===========================================================
        //  STATE — trap detector
        // ===========================================================
        #region Trap state
        private double trapScore;
        private bool   trapDetected;
        private int    trapBarsInTrade;
        private int    trapEscapeBars;
        private int    trapEscapeCooldownBar;
        private int    stopHuntSuspendBars;        // when >0, MM stop-hunt just fired → trail paused / backtrack window open
        #endregion

        // ===========================================================
        //  STATE — signals / confidence
        // ===========================================================
        #region Signal state
        private double lastBullConfidence;
        private double lastBearConfidence;
        private double rawBullConfidence;
        private double rawBearConfidence;
        private int    htfBias;            // +1 bull, -1 bear, 0 mixed
        private int    sessionOpenBias;
        private int    htfBiasConsecBull;
        private int    htfBiasConsecBear;
        private int    sessionConfirmedBias;
        private int    bestAutoStrategy;
        private string lastAutoStrategyUsed = "";
        private int    consecutiveLosses;
        private int    lastLossDirection;
        private int    lastLossBarNumber;
        private int    lastTradeExitBar;
        private double[] strategyScores = new double[2];
        private int    prevDominantDir;
        private bool   confidenceFlip;
        private int    emaCrossBarsAgo;
        private int    emaCrossDir;
        private int    openType;           // 1=Open-Drive bull, -1=Open-Drive bear, 0=other
        private bool   openTypeSet;
        private int    voidBarsRemaining;  // liquidity-void cooldown
        // Trade signal explicit indicator for manual trading
        private int    manualSignalLevel;  // -2 strong sell, -1 sell, 0 wait, 1 buy, 2 strong buy
        private string manualSignalReason = "";
        #endregion

        // ===========================================================
        //  STATE — VWAP / volume profile
        // ===========================================================
        #region VWAP / VP state
        private double vwapCumTPV, vwapCumVol, vwapValue, prevBarVwap;
        private double currBarTPV, currBarVol;
        private SortedDictionary<double, double> volumeAtPrice;
        private double pocLevel, vahLevel, valLevel;
        private double cachedAvgVolume;
        private int    cachedAvgVolumeBar = -1;
        #endregion

        // ===========================================================
        //  STATE — order-flow tape (live only; OnMarketData)
        // ===========================================================
        #region Order-flow state
        private double tapeBidVol, tapeAskVol;     // rolling 30s window
        private DateTime tapeWindowStart;
        private double cachedTapeDelta;            // -1..+1 (bid-side bias .. ask-side bias)
        #endregion

        // ===========================================================
        //  STATE — dashboard / WPF
        // ===========================================================
        #region Dashboard state
        private bool      dashboardAttached;
        private int       dashBuildRetryCount;
        private int       dashBuildTickCounter;
        private DateTime  lastDashboardTime = DateTime.MinValue;
        private Grid      dashboardPanel;
        private Panel     dashboardHostPanel;
        private TranslateTransform dashTranslate;
        private bool      dashDragging;
        private Point     dashDragStart;
        private Border    dashTitleBar;
        // resize state
        private Border    dashOuterBorder;
        private ScrollViewer dashScroller;
        private bool      dashResizing;
        private Point     dashResizeStart;
        private double    dashResizeStartW, dashResizeStartH;
        private double    dashWidth  = 340;   // user-resizable, persists in field
        private double    dashHeight = 720;
        // labels
        private TextBlock lblSignal;          // BIG trade-signal indicator
        private TextBlock lblSignalReason;
        private TextBlock lblStatus;
        private TextBlock lblPnL;
        private TextBlock lblUnrealized;
        private TextBlock lblAccountPnL;
        private TextBlock lblAccountBal;
        private TextBlock lblPosition;
        private TextBlock lblHiddenSL;
        private TextBlock lblHiddenTP;
        private TextBlock lblTrailInfo;
        private TextBlock lblTrapInfo;
        private TextBlock lblConfBull;
        private TextBlock lblConfBear;
        private TextBlock lblVwapVal;
        private TextBlock lblTradeHours;
        private TextBlock lblBrickInfo;       // v6 0.2.1 — read-only Renko brick color/streak
        private TextBlock lblRunInfo;         // v6 1.2 — read-only NinzaRenko run tracker (color×len, pts captured)
        private TextBlock lblQtyVal;
        private TextBlock lblSlVal;
        private TextBlock lblTpVal;
        private TextBlock lblJumpPct;
        private TextBlock lblTrailDistVal;        // shows current trail distance in pt | tk
        private TextBlock lblStratName;
        private TextBlock lblActiveStrategy;
        private TextBlock lblTapeDelta;
        // buttons
        private Button btnBuyMkt, btnSellMkt;
        private Button btnBuyAsk, btnSellBid;
        private Button btnBuyLmt, btnSellLmt;
        private Button btnCloseTrade, btnCloseOne;
        private Button btnJumpSL, btnFlatten, btnKill, btnResetDaily;
        private Button btnTrailNow;
        private Button btnModeManual, btnModeAuto;
        private Button btnHoursToggle;
        private Button btnTrailToggle, btnTrapToggle;
        private Button btnBeToggle;
        private Button btnAggrToggle, btnChopToggle, btnAdaptToggle;
        private Button btnRunnerToggle;
        private Button btnStratPrev, btnStratNext;
        #endregion

        // ===========================================================
        //  STATE — diagnostics
        // ===========================================================
        #region Diagnostics state
        private System.IO.StreamWriter diagWriter;
        private bool diagHeaderWritten;
        private DateTime diagLogDate;
        private DateTime lastHeartbeatTime = DateTime.MinValue;
        #endregion

        // ===========================================================
        //  ON STATE CHANGE
        // ===========================================================
        #region OnStateChange
        protected override void OnStateChange()
        {
            try
            {
                if (State == State.SetDefaults)
                {
                    Description  = "Mm-ATM v6 — unified exits, 12 filters, MM-avoidance trail, optional order-flow tape, manual trade-signal indicator.";
                    Name         = "Mm_ATM_v6";
                    Calculate    = Calculate.OnEachTick;
                    EntriesPerDirection = 40;
                    EntryHandling = EntryHandling.AllEntries;
                    IsExitOnSessionCloseStrategy = false;
                    ExitOnSessionCloseSeconds    = 30;
                    ConnectionLossHandling = ConnectionLossHandling.KeepRunning;
                    DisconnectDelaySeconds = 10;
                    IsUnmanaged = false;
                    BarsRequiredToTrade = 25;

                    // Risk
                    slPoints              = 18;
                    tpPoints              = 50;
                    maxDailyLossDollars   = 1000;
                    maxDailyProfitDollars = 1000;
                    maxTradesPerDay       = 20;
                    contracts             = 1;
                    maxContracts          = 4;

                    // Mode
                    autoMode              = false;
                    autoStrategy          = 2;       // Auto-Select
                    minSignalConfidence   = 55.0;
                    entryDelaySeconds     = 1;

                    // Indicators
                    emaPeriodFast         = 9;
                    emaPeriodSlow         = 21;
                    rsiPeriod             = 14;
                    atrPeriod             = 14;
                    htfEmaPeriod          = 45;

                    // Trail
                    trailEnabled          = true;
                    trailActivationPoints = 5;
                    trailAtrMultiplier    = 1.5;
                    smartTrailBacktrackTicks = 4;
                    aggressiveTrailMaxAtrFactor = 0.5;
                    baseAggressiveTrailFactor   = aggressiveTrailMaxAtrFactor;
                    autoTightenOnLossesEnabled  = false;
                    autoTightenLossN            = 2;
                    autoTightenFactor           = 0.5;
                    autoWidenOnWinsEnabled      = false;
                    autoWidenWinN               = 3;
                    autoWidenFactor             = 1.5;
                    skipPrevDayClampOnHighAdx   = true;
                    highAdxThreshold            = 28.0;
                    resetDailyOnRestart         = true;
                    fastReversalExitEnabled     = true;
                    fastReversalAtrFactor       = 0.6;
                    fastReversalMaxBars         = 4;
                    fastReversalAdverseMinPts   = 4.0;
                    aggressiveExitsEnabled      = true;
                    aggrBeAtPoints              = 3;
                    aggrTrailActivationPts      = 4.0;
                    aggrTrailDistPts            = 2.0;
                    aggrPullbackAtrFactor       = 0.4;
                    aggrPullbackMaxBars         = 2;
                    aggrAdverseExitEnabled      = false;
                    aggrAdverseMaxBars          = 2;
                    aggrAdverseAtrFactor        = 0.5;
                    aggrAdverseMinPts           = 5.0;
                    aggrAdverseDisarmPeak       = 3.0;
                    aggrSlCapPoints             = 0.0;
                    slClusterCooldownEnabled    = true;
                    slClusterCount              = 2;
                    slClusterWindowMin          = 90;
                    slClusterCooldownMin        = 25;
                    postWinSameDirCooldownEnabled = true;
                    postWinSameDirCooldownMin     = 7;
                    dirLockoutEnabled             = true;
                    dirLockoutLossN               = 2;
                    dirLockoutWindowMin           = 30;
                    dirLockoutCooldownMin         = 30;
                    if (dirLossTimesLong != null)  dirLossTimesLong.Clear();
                    if (dirLossTimesShort != null) dirLossTimesShort.Clear();
                    longLockoutUntil  = DateTime.MinValue;
                    shortLockoutUntil = DateTime.MinValue;
                    extensionFilterEnabled        = true;
                    extensionMaxAtrFromVwap       = 5.0;
                    extensionMinAtrPoints         = 8.0;
                    chopFilterEnabled           = true;
                    chopAdxMin                  = 18.0;
                    chopAdxFallingBars          = 3;
                    chopEmaSepMinAtr            = 0.30;
                    chopRangeBars               = 5;
                    chopRangeMaxAtr             = 1.0;
                    chopBlockOppositeTape       = true;
                    chopOppositeTapeMin         = 0.05;
                    chopTrendAdxGate            = 22.0;
                    adaptiveWindowEnabled       = true;
                    adaptiveWindowSize          = 5;
                    adaptiveWindowLossThreshold = 3;
                    adaptiveWindowClearWins     = 2;
                    adaptiveConfBoost           = 5;
                    timeOfDaySlSizingEnabled    = false;
                    sodOpenStart                = 93000;
                    sodOpenEnd                  = 103000;
                    sodOpenSlMult               = 1.30;
                    sodMiddayStart              = 103000;
                    sodMiddayEnd                = 140000;
                    sodMiddaySlMult             = 0.80;
                    sodCloseStart               = 150000;
                    sodCloseEnd                 = 160000;
                    sodCloseSlMult              = 1.20;
                    newsBlackoutEnabled         = true;
                    newsBlackoutTimes           = "083000,100000,140000";
                    newsBlackoutWindowMin       = 2;
                    liquiditySweepBoostEnabled  = true;
                    liquiditySweepLookback      = 30;
                    liquiditySweepConfBoost     = 8.0;
                    suppressDcaOnLossStreak     = true;
                    suppressDcaLossN            = 2;

                    // SmartSL / JumpSL
                    breakevenAtPoints     = 8;
                    beSafeAtrFactor       = 0.20;   // was 0.35 — less restrictive so BE actually fires on small profitable trades
                    beSafeMinTicks        = 4;      // was 6 (1pt floor instead of 1.5pt)
                    breakevenEnabled      = true;
                    allowMultiEntryPerBar = true;     // default ALLOW
                    jumpSlPercent         = 50;
                    slTpAdjustStep        = 5;

                    // Hours
                    tradingStartTime      = 93000;
                    flattenTime           = 160000;
                    tradingHoursEnabled   = true;

                    // Smart logic
                    orderFlowFilterEnabled = true;
                    enableTrapDetector     = true;

                    // Display
                    showVwap = true;
                    showEma  = true;
                    enableDiagLog = false;
                    // Renko 64/16 plumbing (Phase 0.2). Wired but not yet read by entry/exit logic.
                    enableRenkoSeries = true;
                    renkoBrickSize    = 64;
                    renkoBrickOffset  = 16;
                    lastBrickColor    = "";
                    brickStreakCount  = 0;
                    lastProcessedRenkoBar = -1;
                    // v6 0.2.3 — NinzaRenko (third-party) defaults
                    enableNinzaRenkoSeries = true;
                    ninzaCustomSlot   = 0;       // most common default; user can change in property panel
                    usePrimaryAsNinzaRenko = true;  // v6 0.2.4 — default ON; works when chart primary IS NinzaRenko
                    lastNrBrickColor  = "";
                    nrBrickStreakCount = 0;
                    lastProcessedNrBar = -1;
                    // v6 1.1 — F1 Regime Classifier defaults (label-only, OFF by default).
                    enableRegimeClassifier = false;
                    currentRegime          = "UNKNOWN";
                    prevRegime             = "UNKNOWN";
                    regimeChangedBar       = 0;
                    regimeFlipsLast20      = 0;
                    // v6 1.2-1.4 — Brick analytics (run tracker, wick, MM pattern) defaults
                    enableBrickAnalytics   = false;   // OFF by default — turn ON to collect data
                    runCurrentLen          = 0;
                    runCurrentColor        = "";
                    runMaxFavPts           = 0;
                    runMaxLast10           = 0;
                    lastBrickIntervalSec   = 0;
                    fastBricksLast10       = 0;
                    // v6 2.1 — Smart Cooldown defaults (ACTIVE LOGIC, OFF by default).
                    enableSmartCooldown        = false;
                    smartCooldownMinSec        = 60;
                    smartCooldownStreakMin     = 4;
                    // v6 2.2 — Extension TREND-Bypass defaults (ACTIVE LOGIC, OFF by default).
                    enableExtensionTrendBypass = false;
                    extensionBypassStreakMax   = 8;
                    lastAdxSlope               = 0;
                    // v6 2.3 — Brick-Trail Mode defaults (ACTIVE LOGIC, OFF by default).
                    enableBrickTrail              = false;
                    brickTrailMinStreak           = 4;
                    brickTrailBufferTicks         = 4;
                    brickTrailRequireTrendRegime  = true;
                    brickTrailMaxGivebackPct      = 0;
                    brickTrailGivebackMinPeakPts  = 20.0;
                    brickTrailGivebackStallSec    = 45;
                    brickTrailProfitLockMinPeakPts  = 20.0;
                    brickTrailProfitLockGivebackPct = 0;
                    inBarTrailEnabled               = true;
                    inBarTrailMinPeakPts            = 25.0;
                    inBarTrailGivebackPts           = 16.0;
                    prevNrBrickHigh               = 0;
                    prevNrBrickLow                = 0;
                    // v6 2.4 — UNKNOWN regime block defaults (ACTIVE LOGIC, OFF by default).
                    enableUnknownRegimeBlock = false;
                    unknownBlockMinStreak    = 6;
                    unknownBlockMinAdxSlope  = 0;
                }
                else if (State == State.Configure)
                {
                    indEmaFast = EMA(emaPeriodFast);
                    indEmaSlow = EMA(emaPeriodSlow);
                    indEmaHtf  = EMA(htfEmaPeriod);
                    indRsi     = RSI(rsiPeriod, 3);
                    indAtr     = ATR(atrPeriod);
                    indAdx     = ADX(14);

                    AddDataSeries(BarsPeriodType.Minute, 5);
                    indEma5mFast = EMA(BarsArray[1], 9);
                    indEma5mSlow = EMA(BarsArray[1], 21);

                    // v6 Phase 0.2: Renko 64-tick / 16-offset secondary series (BarsArray[2]).
                    // Data-plumbing only — ProcessRenkoBar updates brick color/streak fields
                    // for diag-log MM analysis. No entry/exit logic reads these yet (W6 Phase 2.1).
                    if (enableRenkoSeries)
                    {
                        // Defend against legacy templates loading with 0 values.
                        if (renkoBrickSize   < 4) renkoBrickSize   = 64;
                        if (renkoBrickOffset < 0) renkoBrickOffset = 16;
                        AddDataSeries(new BarsPeriod
                        {
                            BarsPeriodType = BarsPeriodType.Renko,
                            Value          = renkoBrickSize,
                            Value2         = renkoBrickOffset
                        });
                        Print(TAG + "Configure: AddDataSeries Renko " + renkoBrickSize + "/" + renkoBrickOffset + " requested. BarsArray will be index 2.");
                    }
                    else
                    {
                        Print(TAG + "Configure: enableRenkoSeries=false — NO Renko series added. BrickColor will stay blank.");
                    }

                    // v6 0.2.3: NinzaRenko (third-party) on BarsArray[3] via Custom0..Custom9 slot.
                    // NinzaRenko registers itself in NT8 under one of the Custom* enum values; the user
                    // sees it as "NinzaRenko" in the bar-type picker. Which Custom slot it occupies
                    // depends on NT8 install order — user can change ninzaCustomSlot in property panel.
                    ninzaSeriesAdded = false;
                    if (enableNinzaRenkoSeries && !usePrimaryAsNinzaRenko)
                    {
                        // NinzaRenko (third-party) registers itself in NT8 under a Custom BarsPeriodType
                        // whose underlying int value depends on install order. The user selects the right
                        // value via NinzaCustomSlot (try 100, 101, ... or whatever NinzaRenko ended up at).
                        // The raw cast lets us reach it without naming the enum at compile time.
                        BarsPeriodType nrType = (BarsPeriodType)ninzaCustomSlot;
                        try
                        {
                            AddDataSeries(new BarsPeriod
                            {
                                BarsPeriodType = nrType,
                                Value          = renkoBrickSize,
                                Value2         = renkoBrickOffset
                            });
                            ninzaSeriesAdded = true;
                            Print(TAG + "Configure: AddDataSeries NinzaRenko (BarsPeriodType=" + (int)nrType + "/" + nrType + ") " + renkoBrickSize + "/" + renkoBrickOffset + " requested. Expected at BarsArray[3].");
                        }
                        catch (Exception ex)
                        {
                            Print(TAG + "Configure: NinzaRenko AddDataSeries (BarsPeriodType int=" + ninzaCustomSlot + ") FAILED: " + ex.Message
                                + ". Wrong slot for NinzaRenko on this NT8 install. Try a different NinzaCustomSlot value (common NinzaRenko ints: 100, 101, 102, ...). See NT8 Output window for the correct value when NinzaRenko loads on a chart.");
                        }
                    }
                    else
                    {
                        Print(TAG + "Configure: enableNinzaRenkoSeries=false — NinzaRenko skipped.");
                    }

                    if (showEma)
                    {
                        indEmaFast.Plots[0].Brush = Brushes.LimeGreen; indEmaFast.Plots[0].Width = 2;
                        indEmaSlow.Plots[0].Brush = Brushes.Red;       indEmaSlow.Plots[0].Width = 2;
                        AddChartIndicator(indEmaFast);
                        AddChartIndicator(indEmaSlow);
                    }
                }
                else if (State == State.DataLoaded)
                {
                    ResetVwap();
                    volumeAtPrice = new SortedDictionary<double, double>();
                    ResetSessionFlags();
                    ResetPositionStateInternal(true);
                    // Snapshot user's property-panel values so each new arm restores them
                    if (!originalsSnapshotted)
                    {
                        origSlPoints      = slPoints;
                        origTpPoints      = tpPoints;
                        origJumpSlPercent = jumpSlPercent;
                        originalsSnapshotted = true;
                    }
                }
                else if (State == State.Realtime)
                {
                    ResetSessionFlags();
                    ResetPositionStateInternal(true);
                    // ----- ResetDailyOnRestart -----
                    // SystemPerformance.AllTrades persists across strategy disable/re-enable.
                    // If reset is enabled (default), advance processedTradeCount past whatever is
                    // already there so we DO NOT re-credit prior PnL into dailyRealizedPnL on the
                    // first new fill. Without this, after disable+enable today's prior trades
                    // would re-trigger DAILY PROFIT/LOSS LIMIT instantly on the next entry.
                    if (resetDailyOnRestart && SystemPerformance != null && SystemPerformance.AllTrades != null)
                    {
                        int existing = SystemPerformance.AllTrades.Count;
                        processedTradeCount = existing;
                        dailyRealizedPnL = 0;
                        consecutiveLosses = 0;
                        consecutiveWins = 0;
                        Print(TAG + "DAILY counters RESET on restart (skipped " + existing + " prior trades). Set ResetDailyOnRestart=false to keep daily PnL across restarts.");
                    }
                    if (Position.MarketPosition != MarketPosition.Flat)
                    {
                        openTradeDirection = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
                        totalContracts     = Position.Quantity;
                        averageEntryPrice  = Position.AveragePrice;
                        openDcaCount       = (int)Math.Max(1, totalContracts / Math.Max(1, contracts));
                        for (int i = 1; i <= openDcaCount; i++) activeEntrySignals.Add(" ");
                        ArmHiddenStops();
                        Print(TAG + "Realtime POSITION RECOVERY " + Position.MarketPosition + " qty=" + totalContracts);
                    }
                    CancelPendingOrders();

                    rthStartedToday = false;
                    int warmStart = ToTime(Time[0]);
                    if (warmStart >= tradingStartTime && warmStart < flattenTime)
                        rthStartedToday = true;
                    Print(TAG + "Realtime ready. SL=" + slPoints + " TP=" + tpPoints + " contracts=" + contracts);
                }
                else if (State == State.Terminated)
                {
                    RemoveDashboard();
                    CloseDiagLog();
                }
            }
            catch (Exception ex)
            {
                Print(TAG + "OnStateChange EX " + State + ": " + ex.Message + " | " + ex.StackTrace);
            }
        }

        private void ResetSessionFlags()
        {
            dailyTradeCount = 0;
            dailyRealizedPnL = 0;
            processedTradeCount = 0;
            dailyLimitHit = false;
            dailyProfitHit = false;
            flattenFired = false;
            emergencyKillActive = false;
            consecutiveLosses = 0;
            consecutiveWins = 0;
            // Restore base aggressive trail factor (any streak-adjustment is per-session)
            if (baseAggressiveTrailFactor > 0) aggressiveTrailMaxAtrFactor = baseAggressiveTrailFactor;
            lastLossDirection = 0;
            lastLossBarNumber = 0;
            lastTradeExitBar = 0;
            trapEscapeCooldownBar = 0;
            voidBarsRemaining = 0;
            firstBarSeen = false;
            openType = 0;
            openTypeSet = false;
            htfBiasConsecBull = htfBiasConsecBear = 0;
            sessionConfirmedBias = 0;
            tapeBidVol = tapeAskVol = 0;
            cachedTapeDelta = 0;
            tapeWindowStart = DateTime.MinValue;
        }
        #endregion

        // ===========================================================
        //  ON BAR UPDATE  (main pump)
        // ===========================================================
        #region OnBarUpdate
        protected override void OnBarUpdate()
        {
            // v6 Phase 0.2: Renko brick events (BarsArray[2] when enabled).
            // Process FIRST so diag-log brick fields stay current for primary-series rows.
            if (BarsInProgress == 2) { ProcessRenkoBar(); return; }
            // v6 0.2.3: NinzaRenko brick events (BarsArray[3] when enabled & Custom slot correct)
            if (BarsInProgress == NINZA_BIP) { ProcessNinzaRenkoBar(); return; }
            if (BarsInProgress != 0) return;
            // v6 0.2.4: when primary chart IS NinzaRenko, read brick state directly from BarsArray[0].
            // Each closed primary bar = one brick. No separate AddDataSeries needed.
            if (enableNinzaRenkoSeries && usePrimaryAsNinzaRenko && CurrentBar >= 1)
            {
                ProcessPrimaryAsNinzaRenkoBar();
            }
            // One-shot: announce how many series are actually subscribed (helps diagnose missing Renko).
            if (!seriesAnnounced && CurrentBar > 5)
            {
                seriesAnnounced = true;
                int n = BarsArray != null ? BarsArray.Length : 0;
                Print(TAG + "OnBarUpdate first BIP=0 fire — BarsArray.Length=" + n
                    + " (expected 4: primary + 5min + Renko + NinzaRenko); enableRenkoSeries=" + enableRenkoSeries
                    + " enableNinzaRenkoSeries=" + enableNinzaRenkoSeries + " ninzaCustomSlot=" + ninzaCustomSlot);
                if (enableRenkoSeries && n < 3)
                    Print(TAG + "WARNING: Stock Renko series NOT in BarsArray. ProcessRenkoBar will never fire. Reload strategy.");
                if (enableNinzaRenkoSeries && n < 4)
                    Print(TAG + "WARNING: NinzaRenko series NOT in BarsArray. Wrong Custom slot? Try a different value of NinzaCustomSlot in the property panel.");
            }
            if (CurrentBar < BarsRequiredToTrade) return;

            try
            {
                if (!firstBarSeen)
                {
                    firstBarSeen = true;
                    sessionDate  = Time[0].Date;
                    lastFuturesSessionResetDate = Time[0].Date;
                    Print(TAG + "OnBarUpdate ALIVE first bar @ " + Time[0]);
                }
                if (IsFirstTickOfBar) enteredThisBar = false;

                // -------- New futures session rollover (~18:00 ET) --------
                // CME futures (NQ/ES/etc) session starts 18:00 ET on the prior calendar day.
                // If we cross 18:00 on a NEW calendar date AND we have not yet reset for it,
                // wipe daily-counter state so manual / auto trading is not blocked by yesterday's flags.
                MaybeFuturesSessionRollover();

                // -------- DiagLog daily file --------
                if (enableDiagLog && IsFirstTickOfBar)
                {
                    if (diagWriter == null) { diagLogDate = Time[0].Date; OpenDiagLog(); }
                    else if (Time[0].Date > diagLogDate) { CloseDiagLog(); diagLogDate = Time[0].Date; OpenDiagLog(); }
                    // Periodic state heartbeat (every 10 minutes) so we capture regime even when no
                    // entries/exits/blocks fire. Helps post-mortem correlate "why was nothing happening?"
                    if ((Time[0] - lastHeartbeatTime).TotalMinutes >= 10)
                    {
                        lastHeartbeatTime = Time[0];
                        WriteDiagRow("HEARTBEAT", "tradesToday=" + dailyTradeCount + " openDca=" + openDcaCount);
                    }
                }

                // -------- v6 1.1: F1 Regime Classifier (label-only, default OFF) --------
                // Runs every bar after warmup; result stored in currentRegime, surfaces in diag log.
                // Cheap (a few comparisons + 4-bar brick-flip scan), so safe to leave on.
                if (enableRegimeClassifier && IsFirstTickOfBar)
                    UpdateRegimeClassifier();

                // -------- CRITICAL pending button paths --------
                ProcessPendingButtons();

                // -------- Position state sync --------
                SyncPositionState();

                // -------- Pending exit watchdog --------
                if (pendingExit)
                {
                    pendingExitTicks++;
                    if (pendingExitTicks >= STALE_EXIT_TICKS && Position.MarketPosition != MarketPosition.Flat)
                    {
                        Print(TAG + "STALE EXIT — force flatten");
                        if (State == State.Realtime)
                            try { Account.Flatten(new[] { Instrument }); } catch { ManagedExitAll(); }
                        else
                            ManagedExitAll();
                        pendingExitTicks = STALE_EXIT_TICKS / 2;
                    }
                }
                else pendingExitTicks = 0;

                // -------- Aggressive limit timeout --------
                if (aggressiveLimitSubmitTime != DateTime.MinValue
                    && ((State == State.Realtime ? DateTime.Now : Time[0]) - aggressiveLimitSubmitTime).TotalSeconds >= AGGRESSIVE_LIMIT_TIMEOUT_SEC
                    && Position.MarketPosition == MarketPosition.Flat && openTradeDirection != 0)
                {
                    Print(TAG + "ASK/BID order expired — cancel");
                    CancelPendingOrders();
                    ResetPositionStateInternal(false);
                    aggressiveLimitSubmitTime = DateTime.MinValue;
                    UpdateDashboardStatus("ASK/BID order expired", Brushes.Orange);
                }

                // -------- Live exit monitors (every tick when in trade) --------
                if (Position.MarketPosition != MarketPosition.Flat && stopsArmed && !pendingExit)
                {
                    MonitorHiddenStops();
                    if (trailEnabled) MonitorAdaptiveTrail();
                    if (IsFirstTickOfBar && enableTrapDetector) MonitorTrapDetector();
                    if (fastReversalExitEnabled && IsFirstTickOfBar) MonitorFastReversalExit();
                    LiveDailyPnLCheck();
                }

                // -------- Session housekeeping --------
                if (Time[0].Date != sessionDate)
                {
                    ResetDailyTracking();
                    ResetVwap();
                }
                UpdateVwap();
                if (IsFirstTickOfBar)
                {
                    if (High[0] > currDayHigh) currDayHigh = High[0];
                    if (Low[0]  < currDayLow || currDayLow == 0) currDayLow = Low[0];
                    UpdateHtfBias();
                    TrackEmaCross();
                    UpdateOpenTypeClassification();
                    UpdateLiquidityVoid();
                    UpdateVolumeProfile();
                    if (CurrentBar % 20 == 0) RecalcVolumeProfileLevels();
                }

                // -------- Build dashboard --------
                if (ChartControl != null && !dashboardAttached && dashBuildRetryCount < 10)
                {
                    if (dashBuildRetryCount == 0) BuildDashboard();
                    else
                    {
                        dashBuildTickCounter++;
                        if (dashBuildTickCounter >= 5) { dashBuildTickCounter = 0; BuildDashboard(); }
                    }
                }

                // -------- Auto flatten / CME maintenance --------
                AutoFlattenCheck();

                // -------- Signal calc + auto-entry (once per bar when flat) --------
                if (IsFirstTickOfBar && Position.MarketPosition == MarketPosition.Flat)
                {
                    CalculateSignals();
                    UpdateManualSignal();      // explicit manual indicator
                    if (autoMode) TryAutoEntry();
                }

                // -------- Chart annotations --------
                DrawChartAnnotations();
                if (IsFirstTickOfBar) DrawSessionLevels();

                // -------- Dashboard update (throttled) --------
                if (dashboardAttached)
                {
                    if (State == State.Realtime)
                    {
                        if ((DateTime.Now - lastDashboardTime).TotalMilliseconds >= DASH_UPDATE_MS)
                        { lastDashboardTime = DateTime.Now; UpdateDashboard(); }
                    }
                    else if (IsFirstTickOfBar) UpdateDashboard();
                }
            }
            catch (Exception ex)
            {
                Print(TAG + "OnBarUpdate EX: " + ex.Message + " | " + ex.StackTrace);
            }
        }

        private void ProcessPendingButtons()
        {
            if (State != State.Realtime && State != State.Historical) return;
            if (pendingResetDaily)    { pendingResetDaily    = false; ExecuteResetDaily();    return; }
            if (pendingEmergencyKill) { pendingEmergencyKill = false; ExecuteEmergencyKill(); return; }
            if (pendingCloseTrade)    { pendingCloseTrade    = false; ExecuteCloseTrade();    return; }
            if (pendingFlatten)       { pendingFlatten       = false; ExecuteFlatten();       return; }
            if (pendingExit) return;
            if (pendingLong)       { pendingLong       = false; ExecuteLongEntry(true); }
            if (pendingShort)      { pendingShort      = false; ExecuteShortEntry(true); }
            if (pendingBuyAsk)     { pendingBuyAsk     = false; ExecuteBuyAskEntry(); }
            if (pendingSellBid)    { pendingSellBid    = false; ExecuteSellBidEntry(); }
            if (pendingLongLimit)  { pendingLongLimit  = false; ExecuteLongLimitEntry(); }
            if (pendingShortLimit) { pendingShortLimit = false; ExecuteShortLimitEntry(); }
            if (pendingCloseOne)   { pendingCloseOne   = false; ExecuteCloseOne(); }
            if (pendingJumpSL)     { pendingJumpSL     = false; ExecuteJumpSL(); }
            if (pendingRearm)      { pendingRearm      = false; if (stopsArmed) { ArmHiddenStops(); RedrawAnnotationsSafe(); } }
            if (pendingSlTpResize) { pendingSlTpResize = false; if (stopsArmed) { ResizeHiddenStops(); RedrawAnnotationsSafe(); } }
            if (pendingTrailActivate) { pendingTrailActivate = false; ActivateTrailManual(); }
            if (pendingTrailNudgePoints != 0) { int n = pendingTrailNudgePoints; pendingTrailNudgePoints = 0; NudgeTrailDistancePoints(n); }
            if (pendingSlNudge != 0) { int n = pendingSlNudge; pendingSlNudge = 0; if (stopsArmed) NudgeSlPricePoints(n); }
        }

        private void SyncPositionState()
        {
            if (pendingPositionFlat)
            {
                pendingPositionFlat = false;
                if (Position.MarketPosition == MarketPosition.Flat)
                {
                    ResetPositionStateInternal(false);
                    flatSyncGraceTicks = 0;
                    return;
                }
            }
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (pendingExit) { ResetPositionStateInternal(false); flatSyncGraceTicks = 0; }
                else if (openTradeDirection != 0)
                {
                    flatSyncGraceTicks++;
                    if (flatSyncGraceTicks > FLAT_SYNC_MAX_TICKS)
                    { ResetPositionStateInternal(false); flatSyncGraceTicks = 0; }
                }
                else flatSyncGraceTicks = 0;
            }
            else
            {
                flatSyncGraceTicks = 0;
                int posDir = Position.MarketPosition == MarketPosition.Long ? 1 : -1;
                bool desync = false;
                if (openTradeDirection != posDir) { openTradeDirection = posDir; desync = true; }
                if (averageEntryPrice == 0 && Position.AveragePrice > 0) { averageEntryPrice = Position.AveragePrice; desync = true; }
                if (totalContracts < Position.Quantity) { totalContracts = Position.Quantity; desync = true; }
                if (openDcaCount == 0 && totalContracts > 0) { openDcaCount = (int)Math.Max(1, totalContracts / Math.Max(1, contracts)); desync = true; }
                if (activeEntrySignals.Count == 0 && openDcaCount > 0)
                    for (int i = 1; i <= openDcaCount; i++) activeEntrySignals.Add(" ");
                if (!stopsArmed && averageEntryPrice > 0 && !pendingExit) { ArmHiddenStops(); desync = true; }
                if (desync) Print(TAG + "SYNC " + Position.MarketPosition + " qty=" + totalContracts + " avg=" + averageEntryPrice.ToString("F2"));
            }
        }

        private void AutoFlattenCheck()
        {
            int currentTime = ToTime(Time[0]);
            if (!rthStartedToday && currentTime >= tradingStartTime && currentTime < flattenTime)
                rthStartedToday = true;
            if (rthStartedToday && currentTime >= flattenTime && !flattenFired
                && Position.MarketPosition != MarketPosition.Flat)
            {
                flattenFired = true;
                Print(TAG + "AUTO-FLATTEN @ " + Time[0].ToString("HH:mm:ss"));
                lastExitReason = "AUTO_FLATTEN";
                ExecuteFlatten();
            }
            if (rthStartedToday && currentTime >= 165500 && currentTime < 180000
                && Position.MarketPosition != MarketPosition.Flat)
            {
                Print(TAG + "CME maintenance flatten");
                lastExitReason = "CME_MAINT";
                ExecuteFlatten();
            }
        }

        private void LiveDailyPnLCheck()
        {
            if (dailyLimitHit || dailyProfitHit) return;
            double unreal = 0;
            if (Position.MarketPosition == MarketPosition.Long)
                unreal = (Close[0] - averageEntryPrice) * NQ_DOLLARS_PER_POINT * Math.Max(totalContracts, Position.Quantity);
            else if (Position.MarketPosition == MarketPosition.Short)
                unreal = (averageEntryPrice - Close[0]) * NQ_DOLLARS_PER_POINT * Math.Max(totalContracts, Position.Quantity);
            double live = dailyRealizedPnL + unreal;
            if (live <= -maxDailyLossDollars)
            {
                dailyLimitHit = true;
                Print(TAG + "LIVE LOSS LIMIT " + live.ToString("C0") + " — closing position. Strategy stays ENABLED. Press RESET or wait for 18:00 ET session rollover to resume.");
                lastExitReason = "DAILY_LOSS";
                ExecuteFlatten();
                UpdateDashboardStatus("⛔ DAILY LOSS LIMIT " + live.ToString("C0") + " — position closed. Strategy STILL ENABLED. Press RESET to resume.", Brushes.Red);
            }
            else if (live >= maxDailyProfitDollars)
            {
                dailyProfitHit = true;
                Print(TAG + "LIVE PROFIT TARGET " + live.ToString("C0") + " — closing position. Strategy stays ENABLED. Press RESET or wait for 18:00 ET session rollover to resume.");
                lastExitReason = "DAILY_PROFIT";
                ExecuteFlatten();
                UpdateDashboardStatus("💰 DAILY PROFIT TARGET " + live.ToString("C0") + " — position closed. Strategy STILL ENABLED. Press RESET to resume.", Brushes.Gold);
            }
        }
        #endregion

        // ===========================================================
        //  HIDDEN SL/TP — fail-safe layer
        // ===========================================================
        #region Hidden SL/TP
        private void MonitorHiddenStops()
        {
            if (dailyLimitHit || emergencyKillActive) return;
            if (averageEntryPrice <= 0) return;

            double priceLong  = Close[0];
            double priceShort = Close[0];
            if (State == State.Realtime)
            {
                double bid = GetCurrentBid(0); if (bid > 0) priceLong  = bid;
                double ask = GetCurrentAsk(0); if (ask > 0) priceShort = ask;
            }

            // ===== AGGR ADVERSE EXIT (PREEMPTS STATIC SL) — OPT-IN, default OFF =====
            // When enabled: exit before SL if trade going wrong fast. WARNING: tested defaults
            // can fire on entry-bar intrabar wicks on high-ATR (>20pt) mornings. Recommended
            // tuning if enabled: AdverseAtrFactor 0.6, MinPts 8, MaxBars 2, DisarmPeak 4.
            // Also requires (CurrentBar - entryBar) >= 1 to skip the noisy entry bar.
            if (aggressiveExitsEnabled && aggrAdverseExitEnabled
                && openTradeDirection != 0
                && (CurrentBar - entryBar) >= 1
                && (CurrentBar - entryBar) <= aggrAdverseMaxBars
                && trailMaxProfitPts < aggrAdverseDisarmPeak
                && lastFastReversalBar != CurrentBar
                && indAtr != null && indAtr[0] > 0)
            {
                double tickPtAdv = TickSize * NQ_TICKS_PER_POINT;
                double atrPtsAdv = indAtr[0] / tickPtAdv;
                double pxAdv = openTradeDirection == 1 ? priceLong : priceShort;
                double profitPtsAdv = openTradeDirection == 1
                    ? (pxAdv - averageEntryPrice) / tickPtAdv
                    : (averageEntryPrice - pxAdv) / tickPtAdv;
                if (profitPtsAdv > trailMaxProfitPts) trailMaxProfitPts = profitPtsAdv; // keep peak fresh
                double adverse = -profitPtsAdv;
                double adverseTrigger = Math.Max(aggrAdverseMinPts, aggrAdverseAtrFactor * atrPtsAdv);
                if (adverse >= adverseTrigger)
                {
                    lastFastReversalBar = CurrentBar;
                    lastExitReason = "AGGR_ADVERSE";
                    if (enableDiagLog) WriteDiagRow("AGGR_ADVERSE_EXIT", "adv=" + adverse.ToString("F1") + " trig=" + adverseTrigger.ToString("F1") + " bars=" + (CurrentBar - entryBar) + " peak=" + trailMaxProfitPts.ToString("F1"));
                    Print(TAG + "AGGR ADVERSE EXIT  adverse=" + adverse.ToString("F1") + " trigger=" + adverseTrigger.ToString("F1"));
                    if (openTradeDirection == 1) ExitLong(" ", " ");
                    else if (openTradeDirection == -1) ExitShort(" ", " ");
                    stopsArmed = false; pendingExit = true;
                    return;
                }
            }

            // SMART BE — dynamic trigger and ratcheting micro-BE
            //  Trigger = max(BreakevenAtPoints, 0.5×ATR, 0.4×TP)  — prevents firing too early on volatile NQ
            //  Lock 1: at trigger → SL = entry + 2 ticks (locks $10 + commission cushion)
            //  Lock 2+: every additional 5 pts of profit → SL ratchets +2 ticks above entry
            //  Capped at TP-2pt to never get stopped out at TP-mark
            if (breakevenEnabled && !runnerModeActive)
            {
                double profitPx2 = openTradeDirection == 1 ? priceLong : priceShort;
                double tickPt2 = TickSize * NQ_TICKS_PER_POINT;
                double profitPts2 = openTradeDirection == 1
                    ? (profitPx2 - averageEntryPrice) / tickPt2
                    : (averageEntryPrice - profitPx2) / tickPt2;
                double atrPts2 = indAtr[0] / tickPt2;
                int    effBeAt = aggressiveExitsEnabled ? Math.Min(breakevenAtPoints, aggrBeAtPoints) : breakevenAtPoints;
                double smartTrigger = aggressiveExitsEnabled
                    ? effBeAt   // AGGR: lock BE strictly at user-defined small offset, ignore ATR/TP floors
                    : Math.Max(effBeAt, Math.Max(atrPts2 * 0.5, tpPoints * 0.4));
                if (profitPts2 >= smartTrigger)
                {
                    // Compute target BE-stop: entry + (2tk + extra ratchet from profit beyond trigger)
                    int extraTicks = 2 + (int)Math.Floor(Math.Max(0, profitPts2 - smartTrigger) / 5.0) * 2;
                    double beOffset = extraTicks * TickSize;
                    double targetBe = openTradeDirection == 1
                        ? averageEntryPrice + beOffset
                        : averageEntryPrice - beOffset;
                    // Cap at TP - 2pt so we don't camp at TP
                    double tpCap = openTradeDirection == 1
                        ? hiddenTargetPrice - 2 * tickPt2
                        : hiddenTargetPrice + 2 * tickPt2;
                    if (openTradeDirection == 1 && targetBe > tpCap) targetBe = tpCap;
                    if (openTradeDirection == -1 && targetBe < tpCap) targetBe = tpCap;
                    targetBe = Math.Round(targetBe / TickSize) * TickSize;
                    // ANTI-STOP-HUNT SAFETY: never let SL sit too close to current price.
                    //  MM algos love to wick 4-8 ticks past obvious BE/round-number levels then reverse.
                    //  Force a buffer = max(BeSafeMinTicks, BeSafeAtrFactor×ATR_ticks) below price.
                    double safeBufTicks = Math.Max(beSafeMinTicks, atrPts2 * NQ_TICKS_PER_POINT * beSafeAtrFactor);
                    double safeBufPx = safeBufTicks * TickSize;
                    if (openTradeDirection == 1)
                    {
                        double maxAllowed = profitPx2 - safeBufPx;
                        if (targetBe > maxAllowed) targetBe = Math.Round(maxAllowed / TickSize) * TickSize;
                        // FALLBACK: if buffer pushed SL below entry+1tk, lock at entry+1tk anyway
                        // (true risk-free BE — better than leaving full SL exposed).
                        if (targetBe < averageEntryPrice + TickSize) targetBe = averageEntryPrice + TickSize;
                        // But never above maxAllowed (so MM can't tag it 1tk past)
                        if (targetBe > maxAllowed && maxAllowed > averageEntryPrice) targetBe = Math.Round(maxAllowed / TickSize) * TickSize;
                        // Final guard: if even entry+1tk is closer than 1 tick to price, defer.
                        if (targetBe >= profitPx2 - TickSize)
                        { if (enableDiagLog) WriteDiagRow("BE_DEFER", "too-close px=" + profitPx2.ToString("F2") + " target=" + targetBe.ToString("F2")); goto SkipBe; }
                    }
                    else
                    {
                        double minAllowed = profitPx2 + safeBufPx;
                        if (targetBe < minAllowed) targetBe = Math.Round(minAllowed / TickSize) * TickSize;
                        if (targetBe > averageEntryPrice - TickSize) targetBe = averageEntryPrice - TickSize;
                        if (targetBe < minAllowed && minAllowed < averageEntryPrice) targetBe = Math.Round(minAllowed / TickSize) * TickSize;
                        if (targetBe <= profitPx2 + TickSize)
                        { if (enableDiagLog) WriteDiagRow("BE_DEFER", "too-close px=" + profitPx2.ToString("F2") + " target=" + targetBe.ToString("F2")); goto SkipBe; }
                    }
                    // Ratchet only — never weaken SL
                    bool moved = false;
                    if (openTradeDirection == 1 && targetBe > hiddenStopPrice)
                    { hiddenStopPrice = targetBe; moved = true; }
                    else if (openTradeDirection == -1 && targetBe < hiddenStopPrice)
                    { hiddenStopPrice = targetBe; moved = true; }
                    if (moved)
                    {
                        if (!breakevenLocked)
                        {
                            breakevenLocked = true;
                            Print(TAG + "BREAKEVEN LOCK (smart) @ " + hiddenStopPrice.ToString("F2")
                                + " profit=" + profitPts2.ToString("F1") + "pt trigger=" + smartTrigger.ToString("F1") + "pt");
                        }
                        else
                        {
                            Print(TAG + "BE RATCHET +" + extraTicks + "tk @ " + hiddenStopPrice.ToString("F2") + " profit=" + profitPts2.ToString("F1") + "pt");
                        }
                    }
                    SkipBe:;
                }
            }

            if (openTradeDirection == 1)
            {
                if (priceLong <= hiddenStopPrice)
                {
                    Print(TAG + "HIDDEN SL LONG @ " + priceLong.ToString("F2"));
                    lastExitReason = breakevenLocked ? "BE" : "SL";
                    ExitLong(" ", " "); stopsArmed = false; pendingExit = true;
                }
                else if (priceLong >= hiddenTargetPrice && (!runnerModeActive || tpClampedByPrevDay))
                {
                    Print(TAG + "HIDDEN TP LONG @ " + priceLong.ToString("F2") + (tpClampedByPrevDay ? " (prevDay-clamp)" : ""));
                    lastExitReason = tpClampedByPrevDay ? "TP_PD" : "TP";
                    ExitLong(" ", " "); stopsArmed = false; pendingExit = true;
                }
            }
            else if (openTradeDirection == -1)
            {
                if (priceShort >= hiddenStopPrice)
                {
                    Print(TAG + "HIDDEN SL SHORT @ " + priceShort.ToString("F2"));
                    lastExitReason = breakevenLocked ? "BE" : "SL";
                    ExitShort(" ", " "); stopsArmed = false; pendingExit = true;
                }
                else if (priceShort <= hiddenTargetPrice && (!runnerModeActive || tpClampedByPrevDay))
                {
                    Print(TAG + "HIDDEN TP SHORT @ " + priceShort.ToString("F2") + (tpClampedByPrevDay ? " (prevDay-clamp)" : ""));
                    lastExitReason = tpClampedByPrevDay ? "TP_PD" : "TP";
                    ExitShort(" ", " "); stopsArmed = false; pendingExit = true;
                }
            }
        }
        #endregion

        // ===========================================================
        //  ADAPTIVE TRAIL (with MM-avoidance backtrack)
        // ===========================================================
        #region Adaptive Trail
        private void MonitorAdaptiveTrail()
        {
            // Allow execution when master toggle is off ONLY if user explicitly armed via TRL NOW
            // (manualTrailEarlyStart) or trail was already active before toggle was flipped.
            if (averageEntryPrice <= 0) return;
            if (!trailEnabled && !trailActive && !manualTrailEarlyStart) return;
            double price = Close[0];
            if (State == State.Realtime)
            {
                double v = openTradeDirection == 1 ? GetCurrentBid(0) : GetCurrentAsk(0);
                if (v > 0) price = v;
            }
            double tickPt = TickSize * NQ_TICKS_PER_POINT;
            double profitPts = openTradeDirection == 1
                ? (price - averageEntryPrice) / tickPt
                : (averageEntryPrice - price) / tickPt;
            if (profitPts > trailMaxProfitPts) trailMaxProfitPts = profitPts;

            // ===========================================================
            //  v6 2.6 - BRICK-TRAIL v2 (wick-immune, brick-close-based)
            //  Replaces v6 2.3/2.5 price-tick exits which were vulnerable to MM wick stop-runs.
            //  Three exit triggers, in priority order:
            //    1) BRICK FLIP   - first opposite-color brick closes while engaged (definitive reversal)
            //    2) GIVEBACK     - peak profit eroded past BrickTrailMaxGivebackPct (catches stalls)
            //    3) trail price  - INFORMATIONAL ONLY (displayed/logged, NOT used for exit anymore)
            //  Anchor uses brick BODY extremes (max/min of open,close) NOT wicks - MM intra-brick
            //  spikes can no longer fake the trail. Tracking is a running min(SHORT)/max(LONG) across
            //  the current streak so the ratchet is monotonic by construction (no Nth-back guesswork).
            // ===========================================================
            // Trigger 1: confirmed brick flip (set by ProcessPrimaryAsNinzaRenkoBar on opposite-color close).
            if (pendingBrickFlipExit && enableBrickTrail && trailTierName == "BrickTrail")
            {
                pendingBrickFlipExit = false;
                lastExitReason = "TRAIL_BrickTrail_Flip";
                if (enableDiagLog)
                    WriteDiagRow("BRICK_TRAIL_HIT",
                        "reason=brick_flip color=" + lastNrBrickColor + " streak=" + nrBrickStreakCount
                        + " px=" + price.ToString("F2") + " profit=" + profitPts.ToString("F1") + "pt"
                        + " peak=" + trailMaxProfitPts.ToString("F1") + "pt");
                if (openTradeDirection == 1) ExitLong(); else if (openTradeDirection == -1) ExitShort();
                pendingExit = true;
                return;
            }
            // v6 2.6.3 - IN-BAR TICK TRAIL (aggressive mid-brick exit on intra-bar retracement).
            // Fires whenever brick-trail tier is engaged AND we already had a meaningful peak. Uses
            // current tick price (not brick close) so it can catch V-reversals BEFORE the opposite
            // brick closes - which is the structural ~20pt giveback weakness of brick-flip exit.
            // Default giveback (16pt) is wide enough that normal continuation-brick wicks won't trip it.
            if (inBarTrailEnabled && enableBrickTrail && trailTierName == "BrickTrail"
                && trailMaxProfitPts >= inBarTrailMinPeakPts
                && profitPts < trailMaxProfitPts - inBarTrailGivebackPts)
            {
                lastExitReason = "TRAIL_BrickTrail_InBar";
                if (enableDiagLog)
                    WriteDiagRow("BRICK_TRAIL_HIT",
                        "reason=in_bar peak=" + trailMaxProfitPts.ToString("F1")
                        + "pt cur=" + profitPts.ToString("F1")
                        + "pt giveback=" + inBarTrailGivebackPts.ToString("F1")
                        + "pt px=" + price.ToString("F2"));
                if (openTradeDirection == 1) ExitLong(); else if (openTradeDirection == -1) ExitShort();
                pendingExit = true;
                return;
            }
            bool brickTrailEngaged = false;
            if (enableBrickTrail && nrBrickStreakCount >= brickTrailMinStreak
                && lastNrBrickHigh > 0 && lastNrBrickLow > 0
                && trailMaxProfitPts >= (aggressiveExitsEnabled ? aggrTrailActivationPts : trailActivationPoints))
            {
                bool brickAgrees = (openTradeDirection == -1 && lastNrBrickColor == "R")
                                || (openTradeDirection ==  1 && lastNrBrickColor == "G");
                bool regimeOk = !brickTrailRequireTrendRegime
                              || (openTradeDirection == -1 && currentRegime == "TREND_DN")
                              || (openTradeDirection ==  1 && currentRegime == "TREND_UP");
                bool haveAnchor = streakMinBodyHigh < double.MaxValue && streakMaxBodyLow > double.MinValue;
                if (brickAgrees && regimeOk && haveAnchor)
                {
                    double bufPt = brickTrailBufferTicks * TickSize;
                    double anchorPx = openTradeDirection == -1 ? streakMinBodyHigh : streakMaxBodyLow;
                    double btPrice = openTradeDirection == -1
                        ? Math.Round((anchorPx + bufPt) / TickSize) * TickSize
                        : Math.Round((anchorPx - bufPt) / TickSize) * TickSize;
                    // Ratchet INWARD only - INFORMATIONAL trail price for display/diag (no price exit).
                    bool first = !trailActive || trailTierName != "BrickTrail";
                    if (first
                        || (openTradeDirection == -1 && btPrice < trailPrice)
                        || (openTradeDirection ==  1 && btPrice > trailPrice))
                    {
                        trailPrice = btPrice;
                        trailActive = true;
                        trailTierName = "BrickTrail";
                        if (enableDiagLog && first)
                            WriteDiagRow("BRICK_TRAIL_ARMED",
                                "dir=" + openTradeDirection + " streak=" + nrBrickStreakCount
                                + " bodyHi=" + streakMinBodyHigh.ToString("F2")
                                + " bodyLo=" + streakMaxBodyLow.ToString("F2")
                                + " trail=" + trailPrice.ToString("F2")
                                + " (info-only; exit on brick-flip or giveback>=" + brickTrailMaxGivebackPct + "%)");
                    }
                    brickTrailEngaged = true;
                    // Trigger 2: giveback safety with TWO guards:
                    //   (a) peak must already be >= BrickTrailGivebackMinPeakPts (default 20pt) so small
                    //       winners are not whip-sawed by ordinary consolidations
                    //   (b) STALL-GATE: must have gone >= BrickTrailGivebackStallSec since the last
                    //       same-color brick closed (default 45s). If new same-color bricks are still
                    //       printing, the run is alive - never interrupt it.
                    bool stalled = lastSameColorBrickTime != DateTime.MinValue
                        && (Time[0] - lastSameColorBrickTime).TotalSeconds >= brickTrailGivebackStallSec;
                    if (brickTrailMaxGivebackPct > 0
                        && trailMaxProfitPts >= brickTrailGivebackMinPeakPts
                        && stalled
                        && profitPts < trailMaxProfitPts * (1.0 - brickTrailMaxGivebackPct / 100.0))
                    {
                        lastExitReason = "TRAIL_BrickTrail_Giveback";
                        if (enableDiagLog)
                            WriteDiagRow("BRICK_TRAIL_HIT",
                                "reason=giveback peak=" + trailMaxProfitPts.ToString("F1")
                                + "pt cur=" + profitPts.ToString("F1") + "pt limit="
                                + brickTrailMaxGivebackPct + "% stallSec="
                                + ((int)(Time[0] - lastSameColorBrickTime).TotalSeconds));
                        if (openTradeDirection == 1) ExitLong(); else if (openTradeDirection == -1) ExitShort();
                        pendingExit = true;
                        return;
                    }
                    // No price-tick exit - wick-immune by design.
                    return; // Brick-trail in charge - skip the legacy ATR/tier logic entirely.
                }
            }
            double atrPts = indAtr[0] / tickPt;
            double dynamicActivation = aggressiveExitsEnabled
                ? aggrTrailActivationPts
                : Math.Max(trailActivationPoints, atrPts * 0.4);
            bool htfAgrees = (openTradeDirection == 1 && htfBias > 0) || (openTradeDirection == -1 && htfBias < 0);

            // AGGRESSIVE PULLBACK EXIT (anti-MM-trap): once we've been in profit beyond activation,
            // any retracement >= aggrPullbackAtrFactor*ATR within aggrPullbackMaxBars of entry
            // forces a market exit so MM stop-runs can't flip a winner into a loser.
            if (aggressiveExitsEnabled && trailMaxProfitPts >= dynamicActivation
                && (CurrentBar - entryBar) <= aggrPullbackMaxBars
                && lastFastReversalBar != CurrentBar)
            {
                double pullback = trailMaxProfitPts - profitPts;
                if (pullback >= aggrPullbackAtrFactor * atrPts && pullback >= 1.0)
                {
                    lastFastReversalBar = CurrentBar;
                    lastExitReason = "AGGR_PULLBACK";
                    if (enableDiagLog) WriteDiagRow("AGGR_PULLBACK_EXIT", "peak=" + trailMaxProfitPts.ToString("F1") + " cur=" + profitPts.ToString("F1") + " pb=" + pullback.ToString("F1"));
                    Print(TAG + "AGGR PULLBACK EXIT  peak=" + trailMaxProfitPts.ToString("F1") + " cur=" + profitPts.ToString("F1"));
                    if (openTradeDirection == 1) ExitLong();
                    else if (openTradeDirection == -1) ExitShort();
                    pendingExit = true;
                    return;
                }
            }

            if (!trailActive)
            {
                // Bar-guard: skip first 2 bars after entry to avoid noise — BYPASSED if user pressed TRL NOW.
                if (!manualTrailEarlyStart && (CurrentBar - entryBar) < 2)
                {
                    if (enableDiagLog && (DateTime.Now - lastTrailDiagTime).TotalSeconds >= 5)
                    { lastTrailDiagTime = DateTime.Now; Print(TAG + "TRAIL diag: waiting bar-guard barsSinceEntry=" + (CurrentBar - entryBar)); }
                    return;
                }
                // Profit-activation gate — BYPASSED if user pressed TRL NOW (early-start)
                if (!manualTrailEarlyStart && profitPts < dynamicActivation)
                {
                    if (enableDiagLog && (DateTime.Now - lastTrailDiagTime).TotalSeconds >= 5)
                    {
                        lastTrailDiagTime = DateTime.Now;
                        Print(TAG + "TRAIL diag: waiting profit " + profitPts.ToString("F2") + " / activation " + dynamicActivation.ToString("F2") + "pt (need " + (dynamicActivation - profitPts).ToString("F2") + " more)");
                    }
                    return;
                }
                trailActive = true;
                // Runner Mode override: single wide distance, ignore all aggression sources.
                double dist;
                if (runnerModeActive_user)
                {
                    dist = Math.Max(runnerMinPts, atrPts * runnerAtrFactor);
                    trailTierName = "RUNNER";
                }
                else if (aggressiveExitsEnabled)
                {
                    dist = Math.Max(1.0, aggrTrailDistPts);
                    trailTierName = "Aggr-Mode";
                }
                else if (manualTrailEarlyStart)
                {
                    dist = Math.Max(2.0, atrPts * aggressiveTrailMaxAtrFactor);
                    trailTierName = "Aggr";
                }
                else if (htfAgrees)
                {
                    dist = Math.Max(GetTrailDistance() * 1.8, atrPts * 0.8);
                    trailTierName = "Runner";
                }
                else
                {
                    dist = GetTrailDistance();
                    trailTierName = "Active";
                }
                dist = Math.Max(1.0, dist + manualTrailOffsetPoints);
                trailPrice = openTradeDirection == 1
                    ? Math.Round((price - dist * tickPt) / TickSize) * TickSize
                    : Math.Round((price + dist * tickPt) / TickSize) * TickSize;
                Print(TAG + "TRAIL ACTIVATED " + (manualTrailEarlyStart ? "(TRL NOW — aggressive) " : "")
                    + "profit=" + profitPts.ToString("F1") + " dist=" + dist.ToString("F1") + " tier=" + trailTierName);
                return;
            }

            // ----- compute candidate new trail price -----
            // Runner Mode: bypass all aggression — single wide distance, no multipliers, no floors.
            // Structural snap (below) still applies so the trail follows swing structure.
            double curDist;
            if (runnerModeActive_user)
            {
                curDist = Math.Max(runnerMinPts, atrPts * runnerAtrFactor) + manualTrailOffsetPoints;
                if (curDist < runnerMinPts) curDist = runnerMinPts;
                trailTierName = "RUNNER";
            }
            else
            {
                // Manual offset applied every pass so user nudges persist through auto-ratchet.
                curDist = Math.Max(1.0, GetTrailDistance() + manualTrailOffsetPoints);
                // Time-based ratchet: every 5 bars in profit, tighten 10%
                int barsInTrade = CurrentBar - entryBar;
                if (barsInTrade > 0 && barsInTrade % 5 == 0 && profitPts > dynamicActivation * 1.5)
                    curDist *= 0.90;
                // PROFIT-AGGRESSION ladder — the more we're up, the tighter we follow.
                if (trailMaxProfitPts >= dynamicActivation * 4) curDist *= 0.70;
                if (trailMaxProfitPts >= dynamicActivation * 6) curDist *= 0.75;
                if (trailMaxProfitPts >= dynamicActivation * 8)
                {
                    double hardCap = Math.Max(1.0, atrPts * 0.25);
                    if (curDist > hardCap) curDist = hardCap;
                }
                // Trap tighten
                if (trapDetected && stopHuntSuspendBars <= 0) curDist *= 0.60;
                // EMA against → tighter
                if (openTradeDirection == 1 && indEmaFast[0] < indEmaSlow[0]) curDist *= 0.75;
                else if (openTradeDirection == -1 && indEmaFast[0] > indEmaSlow[0]) curDist *= 0.75;
            }

            // Profit tier floors (skipped in Runner Mode — don't cap a strong trade at 65% peak)
            double tierFloor = 0;
            if (!runnerModeActive_user)
            {
                if (trailMaxProfitPts >= dynamicActivation * 6)
                { tierFloor = 0.65 * trailMaxProfitPts * tickPt; trailTierName = "T4-Big"; }
                else if (trailMaxProfitPts >= dynamicActivation * 4)
                { tierFloor = (htfAgrees ? 0.50 : 0.45) * trailMaxProfitPts * tickPt; trailTierName = "T3-Runner"; }
                else if (trailMaxProfitPts >= dynamicActivation * 2.5)
                { tierFloor = 0.40 * trailMaxProfitPts * tickPt; trailTierName = "T2-Strong"; }
                else if (!htfAgrees && trailMaxProfitPts >= dynamicActivation * 1.5)
                { tierFloor = TickSize; trailTierName = "T1-BE"; }
                else trailTierName = htfAgrees ? "Runner" : "Active";
            }

            double off = curDist * tickPt;
            double candidateNewTrail = openTradeDirection == 1
                ? Math.Round((price - off) / TickSize) * TickSize
                : Math.Round((price + off) / TickSize) * TickSize;

            // Structural snap (last 5 bars structure)
            if (CurrentBar >= 6)
            {
                if (openTradeDirection == 1)
                {
                    double lo = double.MaxValue;
                    for (int i = 1; i <= 5; i++) lo = Math.Min(lo, Low[i]);
                    double s = lo - TickSize;
                    if (s > candidateNewTrail && (price - s) / tickPt >= trailActivationPoints * 0.6)
                        candidateNewTrail = s;
                }
                else
                {
                    double hi = double.MinValue;
                    for (int i = 1; i <= 5; i++) hi = Math.Max(hi, High[i]);
                    double s = hi + TickSize;
                    if (s < candidateNewTrail && (s - price) / tickPt >= trailActivationPoints * 0.6)
                        candidateNewTrail = s;
                }
            }

            // Floor
            if (tierFloor > 0)
            {
                double fp = openTradeDirection == 1
                    ? averageEntryPrice + tierFloor
                    : averageEntryPrice - tierFloor;
                fp = Math.Round(fp / TickSize) * TickSize;
                if (openTradeDirection == 1 && candidateNewTrail < fp) candidateNewTrail = fp;
                else if (openTradeDirection == -1 && candidateNewTrail > fp) candidateNewTrail = fp;
            }

            // -------- MM-AVOIDANCE BACKTRACK --------
            // If a stop-hunt was just detected (suspend window open) AND the candidate trail
            // would put us closer to price than now, instead allow trail to RELAX up to
            // smartTrailBacktrackTicks behind its current value (away from price).
            // This sacrifices small ticks intentionally to avoid being swept by an MM spike.
            if (stopHuntSuspendBars > 0 && smartTrailBacktrackTicks > 0
                && backtrackUsedBar != CurrentBar)
            {
                double backDist = smartTrailBacktrackTicks * TickSize;
                if (openTradeDirection == 1)
                {
                    double relaxed = trailPrice - backDist;
                    // never below original SL; never above price - minDist
                    if (relaxed < originalSlPrice) relaxed = originalSlPrice;
                    if (relaxed < candidateNewTrail)
                    {
                        Print(TAG + "TRAIL BACKTRACK long " + trailPrice.ToString("F2") + " -> " + relaxed.ToString("F2") + " (MM avoid)");
                        candidateNewTrail = relaxed;
                        backtrackUsedBar  = CurrentBar;
                    }
                }
                else if (openTradeDirection == -1)
                {
                    double relaxed = trailPrice + backDist;
                    if (relaxed > originalSlPrice) relaxed = originalSlPrice;
                    if (relaxed > candidateNewTrail)
                    {
                        Print(TAG + "TRAIL BACKTRACK short " + trailPrice.ToString("F2") + " -> " + relaxed.ToString("F2") + " (MM avoid)");
                        candidateNewTrail = relaxed;
                        backtrackUsedBar  = CurrentBar;
                    }
                }
            }

            // Apply (ratchet only — never moves against profit unless backtrack just rewrote it)
            double prevTrail = trailPrice;
            if (openTradeDirection == 1)
            {
                if (candidateNewTrail > trailPrice || backtrackUsedBar == CurrentBar) trailPrice = candidateNewTrail;
                if (price <= trailPrice)
                {
                    Print(TAG + "TRAIL HIT LONG @ " + price.ToString("F2") + " trail=" + trailPrice.ToString("F2") + " tier=" + trailTierName);
                    lastExitReason = "TRAIL_" + trailTierName;
                    ExitLong(" ", " "); stopsArmed = false; pendingExit = true;
                }
            }
            else if (openTradeDirection == -1)
            {
                if (candidateNewTrail < trailPrice || backtrackUsedBar == CurrentBar) trailPrice = candidateNewTrail;
                if (price >= trailPrice)
                {
                    Print(TAG + "TRAIL HIT SHORT @ " + price.ToString("F2") + " trail=" + trailPrice.ToString("F2") + " tier=" + trailTierName);
                    lastExitReason = "TRAIL_" + trailTierName;
                    ExitShort(" ", " "); stopsArmed = false; pendingExit = true;
                }
            }

            // Diagnostic: log every move, plus throttled "why no move" pings.
            if (Math.Abs(trailPrice - prevTrail) > TickSize * 0.5)
            {
                Print(TAG + "TRAIL MOVE " + prevTrail.ToString("F2") + " -> " + trailPrice.ToString("F2")
                    + " (price=" + price.ToString("F2") + " off=" + curDist.ToString("F2") + "pt tier=" + trailTierName + " maxProf=" + trailMaxProfitPts.ToString("F1") + "pt)");
            }
            else if (enableDiagLog && (DateTime.Now - lastTrailDiagTime).TotalSeconds >= 5)
            {
                lastTrailDiagTime = DateTime.Now;
                Print(TAG + "TRAIL diag: stable trail=" + trailPrice.ToString("F2")
                    + " cand=" + candidateNewTrail.ToString("F2") + " price=" + price.ToString("F2")
                    + " off=" + curDist.ToString("F2") + "pt prof=" + profitPts.ToString("F2") + "pt max=" + trailMaxProfitPts.ToString("F2") + "pt tier=" + trailTierName
                    + (openTradeDirection == 1 ? " (cand<=trail → hold)" : " (cand>=trail → hold)"));
            }
        }

        private double GetTrailDistance()
        {
            if (cachedTrailBar == CurrentBar) return cachedTrailDistance;
            cachedTrailBar = CurrentBar;
            cachedTrailDistance = CalculateTrailDistance();
            return cachedTrailDistance;
        }

        private double CalculateTrailDistance()
        {
            trailTrendScore = 0.5;
            if (CurrentBar < emaPeriodSlow + 5) return Math.Max(3, trailActivationPoints * 0.6);
            double atr = indAtr[0]; if (atr <= 0) atr = TickSize;
            double atrSum = 0;
            int lb = Math.Min(10, CurrentBar);
            for (int i = 0; i < lb; i++) atrSum += indAtr[i];
            double atrAvg = atrSum / lb;
            double atrExp = atrAvg > 0 ? atr / atrAvg : 1;
            double atrScore = Clamp01((atrExp - 0.9) / 0.5);
            double emaScore = Clamp01(Math.Abs(indEmaFast[0] - indEmaSlow[0]) / atr / 2.5);
            int dirBars = 0; int chk = Math.Min(8, CurrentBar - 1);
            for (int i = 0; i < chk; i++)
                if (openTradeDirection == 1 && Close[i] > Close[i + 1]) dirBars++;
                else if (openTradeDirection == -1 && Close[i] < Close[i + 1]) dirBars++;
            double dirScore = chk > 0 ? (double)dirBars / chk : 0.5;
            trailTrendScore = Clamp01(atrScore * 0.3 + emaScore * 0.3 + dirScore * 0.4);

            double atrDist = atr * trailAtrMultiplier / (TickSize * NQ_TICKS_PER_POINT);
            double regimeDist = LerpD(3, 25, trailTrendScore);
            double dist = regimeDist * 0.6 + atrDist * 0.4;
            return Math.Max(3, Math.Min(40, dist));
        }
        #endregion

        // ===========================================================
        //  FAST REVERSAL EXIT (item 4 — beat MM/algo sweeps)
        // ===========================================================
        // Logic: within first N bars after entry, if adverse move > factor×ATR
        //   AND (EMA flipped against us OR strong opposite tape OR high-vol adverse bar)
        //   AND we are NOT already in profit (trail/BE will handle that),
        // then ExitLong/ExitShort immediately. Cuts the loss roughly in half vs waiting for hidden SL.
        // Fires at most ONCE per trade (lastFastReversalBar guards re-fire).
        private void MonitorFastReversalExit()
        {
            if (averageEntryPrice <= 0 || openTradeDirection == 0) return;
            if (lastFastReversalBar == entryBar) return;       // already fired for this trade
            int barsSince = CurrentBar - entryBar;
            if (barsSince < 1 || barsSince > fastReversalMaxBars) return;
            double atr = indAtr[0]; if (atr <= 0) return;
            double tickPt = TickSize * NQ_TICKS_PER_POINT;
            double price = Close[0];
            if (State == State.Realtime)
            {
                double v = openTradeDirection == 1 ? GetCurrentBid(0) : GetCurrentAsk(0);
                if (v > 0) price = v;
            }
            double adversePts = openTradeDirection == 1
                ? (averageEntryPrice - price) / tickPt
                : (price - averageEntryPrice) / tickPt;
            if (adversePts < fastReversalAdverseMinPts) return;
            double atrPts = atr / tickPt;
            if (adversePts < atrPts * fastReversalAtrFactor) return;
            // CONFIRMATION (need at least 2 of 3 anti-MM signals):
            int confirms = 0;
            // 1) EMA fast vs slow flipped against us
            bool emaFlip = (openTradeDirection == 1 && indEmaFast[0] < indEmaSlow[0])
                        || (openTradeDirection == -1 && indEmaFast[0] > indEmaSlow[0]);
            if (emaFlip) confirms++;
            // 2) Tape (cumulative-delta proxy) strongly against
            bool tapeAgainst = (openTradeDirection == 1 && cachedTapeDelta < -0.15)
                            || (openTradeDirection == -1 && cachedTapeDelta >  0.15);
            if (tapeAgainst) confirms++;
            // 3) High-volume adverse bar — MM dump
            double avgVol = GetCachedAvgVolume();
            bool volSpike = avgVol > 0 && Volume[0] > avgVol * 1.4;
            if (volSpike) confirms++;
            // 4) Trap detector says we're in a trap (use trapScore as a strong tiebreaker)
            if (trapScore >= 40) confirms++;
            if (confirms < 2) return;
            // FIRE — cut the loss now.
            lastFastReversalBar = entryBar;
            Print(TAG + "FAST-REVERSAL EXIT (anti-MM) adv=" + adversePts.ToString("F1")
                + "pt atr=" + atrPts.ToString("F1") + " confirms=" + confirms
                + " emaFlip=" + emaFlip + " tape=" + cachedTapeDelta.ToString("F2")
                + " volSpk=" + volSpike + " trap=" + trapScore.ToString("F0"));
            if (enableDiagLog) WriteDiagRow("FAST_REVERSAL", "adv=" + adversePts.ToString("F1") + " confirms=" + confirms);
            lastExitReason = "FAST_REV";
            if (openTradeDirection == 1) ExitLong(" ", " ");
            else                          ExitShort(" ", " ");
            stopsArmed = false; pendingExit = true;
        }

        // ===========================================================
        //  FUTURES SESSION ROLLOVER (item 8 — ~18:00 ET new session)
        // ===========================================================
        private void MaybeFuturesSessionRollover()
        {
            int ct = ToTime(Time[0]);
            // Trigger once per calendar date when we cross 18:00 boundary.
            if (ct >= 180000 && Time[0].Date != lastFuturesSessionResetDate)
            {
                lastFuturesSessionResetDate = Time[0].Date;
                Print(TAG + "FUTURES SESSION ROLLOVER @ " + Time[0].ToString("yyyy-MM-dd HH:mm:ss")
                    + " — clearing daily flags so new session can trade.");
                dailyRealizedPnL    = 0;
                dailyTradeCount     = 0;
                consecutiveLosses   = 0;
                consecutiveWins     = 0;
                dailyLimitHit       = false;
                dailyProfitHit      = false;
                emergencyKillActive = false;
                flattenFired        = false;
                rthStartedToday     = false;
                if (baseAggressiveTrailFactor > 0) aggressiveTrailMaxAtrFactor = baseAggressiveTrailFactor;
                if (SystemPerformance != null && SystemPerformance.AllTrades != null)
                    processedTradeCount = SystemPerformance.AllTrades.Count;
                if (enableDiagLog) WriteDiagRow("SESSION_ROLLOVER", "auto=18:00");
                UpdateDashboardStatus("New futures session — daily counters reset", Brushes.MediumPurple);
            }
        }

        // ===========================================================
        //  HELPERS — time-of-day SL, news blackout, liquidity sweep
        // ===========================================================
        // Returns the (possibly-scaled) SL points for the current time of day.
        // Eval order: Open window → Close window → Midday window. First match wins.
        private int GetEffectiveSlPoints()
        {
            int baseSl = slPoints;
            if (timeOfDaySlSizingEnabled)
            {
                int ct = ToTime(Time[0]);
                double mult = 1.0;
                if (IsTimeInWindow(ct, sodOpenStart, sodOpenEnd))   mult = sodOpenSlMult;
                else if (IsTimeInWindow(ct, sodCloseStart, sodCloseEnd)) mult = sodCloseSlMult;
                else if (IsTimeInWindow(ct, sodMiddayStart, sodMiddayEnd)) mult = sodMiddaySlMult;
                baseSl = (int)Math.Round(slPoints * mult);
            }
            // AGGR static-SL cap — when aggressive exits are armed, the AGGR pullback / adverse
            // logic catches normal losses, so the static SL is only there as a catastrophe brake.
            // Cap it tighter than the manual default to limit worst-case dollar loss per trade.
            if (aggressiveExitsEnabled && aggrSlCapPoints > 0 && baseSl > aggrSlCapPoints)
                baseSl = (int)Math.Round(aggrSlCapPoints);
            return Math.Max(2, baseSl); // never below 2pt
        }

        // Inclusive of start, exclusive of end. Handles wrap-around (start > end).
        private bool IsTimeInWindow(int t, int start, int end)
        {
            if (start <= end) return t >= start && t < end;
            return t >= start || t < end;
        }

        // News blackout: convert HHMMSS → minutes-of-day, check against parsed cache.
        private bool IsInNewsBlackout()
        {
            if (newsBlackoutMinutesCache == null) ParseNewsBlackoutTimes();
            if (newsBlackoutMinutesCache == null || newsBlackoutMinutesCache.Length == 0) return false;
            int ct = ToTime(Time[0]);
            int curMin = (ct / 10000) * 60 + ((ct / 100) % 100);
            for (int i = 0; i < newsBlackoutMinutesCache.Length; i++)
                if (Math.Abs(curMin - newsBlackoutMinutesCache[i]) <= newsBlackoutWindowMin) return true;
            return false;
        }
        private void ParseNewsBlackoutTimes()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(newsBlackoutTimes)) { newsBlackoutMinutesCache = new int[0]; return; }
                var parts = newsBlackoutTimes.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var list = new System.Collections.Generic.List<int>();
                foreach (var p in parts)
                {
                    int t;
                    if (int.TryParse(p.Trim(), out t) && t >= 0 && t <= 235959)
                        list.Add((t / 10000) * 60 + ((t / 100) % 100));
                }
                newsBlackoutMinutesCache = list.ToArray();
            }
            catch (Exception ex) { Print(TAG + "ParseNewsBlackoutTimes EX: " + ex.Message); newsBlackoutMinutesCache = new int[0]; }
        }

        // Liquidity-sweep detection (classic MM stop-run reversal):
        //   Bull sweep: prior bar Low broke below N-bar low THEN closed back above that level
        //               by >= 0.3×ATR — BUY signal.
        //   Bear sweep: mirror image.
        private bool IsLiquiditySweepBull()
        {
            if (CurrentBar < liquiditySweepLookback + 2 || indAtr == null || indAtr[0] <= 0) return false;
            double prevLow = double.MaxValue;
            for (int i = 2; i <= liquiditySweepLookback + 1; i++) if (Low[i] < prevLow) prevLow = Low[i];
            double atr = indAtr[0];
            // Bar 1 wicked below, closed back above by 30% of ATR
            return Low[1] < prevLow && Close[1] >= prevLow + 0.3 * atr;
        }
        private bool IsLiquiditySweepBear()
        {
            if (CurrentBar < liquiditySweepLookback + 2 || indAtr == null || indAtr[0] <= 0) return false;
            double prevHigh = double.MinValue;
            for (int i = 2; i <= liquiditySweepLookback + 1; i++) if (High[i] > prevHigh) prevHigh = High[i];
            double atr = indAtr[0];
            return High[1] > prevHigh && Close[1] <= prevHigh - 0.3 * atr;
        }

        // ===========================================================
        //  CHOP DETECTOR — protect capital in low-trend conditions
        // ===========================================================
        // Returns true if ANY of these are true:
        //   1) ADX < chopAdxMin AND falling N bars in a row
        //   2) |EmaFast - EmaSlow| < chopEmaSepMinAtr × ATR  (no separation = no trend)  [GATED: skipped when ADX >= chopTrendAdxGate]
        //   3) Close range over last N bars < chopRangeMaxAtr × ATR  (visual chop)         [GATED: skipped when ADX >= chopTrendAdxGate]
        //   4) Tape sign opposite to entry direction by at least chopOppositeTapeMin
        // direction: +1=long attempt, -1=short attempt.
        private bool IsChoppy(int direction, out string reason)
        {
            reason = "";
            if (CurrentBar < Math.Max(chopRangeBars, chopAdxFallingBars) + 2 || indAtr == null || indAtr[0] <= 0) return false;
            double atr = indAtr[0];
            // 1) ADX collapse
            if (indAdx != null && indAdx[0] < chopAdxMin)
            {
                bool falling = true;
                for (int i = 0; i < chopAdxFallingBars; i++)
                    if (indAdx[i] >= indAdx[i + 1]) { falling = false; break; }
                if (falling) { reason = "ADX<" + chopAdxMin.ToString("F0") + " falling " + chopAdxFallingBars + "b (" + indAdx[0].ToString("F1") + ")"; return true; }
            }
            // ADX trend gate: when ADX is strong, EMAs riding close together and small close-ranges are
            // consolidations inside a trend — NOT chop. Skip tests #2 and #3 in that regime.
            bool trendStrong = (indAdx != null && indAdx[0] >= chopTrendAdxGate);
            // 2) EMA convergence (skipped in strong trend)
            if (!trendStrong && indEmaFast != null && indEmaSlow != null)
            {
                double sep = Math.Abs(indEmaFast[0] - indEmaSlow[0]);
                if (sep < chopEmaSepMinAtr * atr) { reason = "EMA gap " + sep.ToString("F2") + " < " + (chopEmaSepMinAtr * atr).ToString("F2") + " (ADX " + (indAdx != null ? indAdx[0].ToString("F1") : "?") + ")"; return true; }
            }
            // 3) Close-range collapse (skipped in strong trend)
            if (!trendStrong)
            {
                double hi = double.MinValue, lo = double.MaxValue;
                for (int i = 0; i < chopRangeBars; i++)
                { if (Close[i] > hi) hi = Close[i]; if (Close[i] < lo) lo = Close[i]; }
                if ((hi - lo) < chopRangeMaxAtr * atr) { reason = "range " + (hi - lo).ToString("F2") + " < " + (chopRangeMaxAtr * atr).ToString("F2") + " over " + chopRangeBars + "b (ADX " + (indAdx != null ? indAdx[0].ToString("F1") : "?") + ")"; return true; }
            }
            // 4) Opposite tape — only when tape data is meaningful
            if (chopBlockOppositeTape && orderFlowFilterEnabled && Math.Abs(cachedTapeDelta) >= chopOppositeTapeMin)
            {
                if (direction == 1 && cachedTapeDelta < -chopOppositeTapeMin) { reason = "tape against long " + cachedTapeDelta.ToString("F2"); return true; }
                if (direction == -1 && cachedTapeDelta >  chopOppositeTapeMin) { reason = "tape against short " + cachedTapeDelta.ToString("F2"); return true; }
            }
            return false;
        }

        // ===========================================================
        //  ADAPTIVE INTRA-DAY WINDOW — tighten after loss cluster
        // ===========================================================
        // Called from OnExecutionUpdate when a trade closes (win=true if profit > 0).
        private void RecordTradeOutcome(bool win)
        {
            if (recentTradeOutcomes == null || recentTradeOutcomes.Length != adaptiveWindowSize)
                recentTradeOutcomes = new int[Math.Max(2, adaptiveWindowSize)];
            recentTradeOutcomes[recentTradeIndex % recentTradeOutcomes.Length] = win ? 1 : -1;
            recentTradeIndex++;
            // Count losses in window
            int losses = 0, wins = 0, filled = 0;
            for (int i = 0; i < recentTradeOutcomes.Length; i++)
            { if (recentTradeOutcomes[i] == 0) continue; filled++; if (recentTradeOutcomes[i] < 0) losses++; else wins++; }
            if (!adaptiveWindowEnabled) { adaptiveTightenActive = false; return; }
            if (!adaptiveTightenActive)
            {
                // Activate tighten when threshold is hit
                if (filled >= adaptiveWindowLossThreshold && losses >= adaptiveWindowLossThreshold)
                {
                    adaptiveTightenActive = true;
                    adaptiveWinsSinceTighten = 0;
                    if (enableDiagLog) WriteDiagRow("ADAPT_WINDOW_ON", "losses=" + losses + "/" + filled + " thr=" + adaptiveWindowLossThreshold);
                    Print(TAG + "ADAPTIVE TIGHTEN active (losses=" + losses + "/" + filled + ")");
                }
            }
            else
            {
                // Clear tighten after N consecutive wins
                if (win) adaptiveWinsSinceTighten++; else adaptiveWinsSinceTighten = 0;
                if (adaptiveWinsSinceTighten >= adaptiveWindowClearWins)
                {
                    adaptiveTightenActive = false;
                    adaptiveWinsSinceTighten = 0;
                    if (enableDiagLog) WriteDiagRow("ADAPT_WINDOW_OFF", "wins=" + adaptiveWindowClearWins);
                    Print(TAG + "ADAPTIVE TIGHTEN cleared after " + adaptiveWindowClearWins + " wins");
                }
            }
        }


        // ===========================================================
        //  TRAP DETECTOR + STOP-HUNT
        // ===========================================================
        #region Trap detector
        private void MonitorTrapDetector()
        {
            if (averageEntryPrice <= 0) return;
            if (CurrentBar < 5) return;
            double price = Close[0];
            double atr = indAtr[0]; if (atr <= 0) return;
            double tickPt = TickSize * NQ_TICKS_PER_POINT;
            double adverse = openTradeDirection == 1
                ? (averageEntryPrice - price) / tickPt
                : (price - averageEntryPrice) / tickPt;

            double s = 0;
            double atrPts = Math.Max(atr / tickPt, 10.0);
            if (adverse > 0)
            {
                s += Math.Min(30.0, adverse / atrPts * 30);
                if (adverse > 6) s += Math.Min(15.0, adverse - 6);
            }
            // EMA conflict
            if (openTradeDirection == 1 && indEmaFast[0] < indEmaSlow[0]) s += 20;
            else if (openTradeDirection == -1 && indEmaFast[0] > indEmaSlow[0]) s += 20;
            // High-volume adverse
            double avgVol = GetCachedAvgVolume();
            if (avgVol > 0 && Volume[0] > avgVol * 1.5 && adverse > 0) s += 15;

            // Stop-hunt spike detection (sets suspend window)
            if (stopHuntSuspendBars > 0) stopHuntSuspendBars--;
            if (CurrentBar >= 6)
            {
                bool huntDetected = false;
                if (openTradeDirection == 1)
                {
                    double pl = double.MaxValue; for (int i = 1; i <= 5; i++) pl = Math.Min(pl, Low[i]);
                    if (Low[0] < pl && Close[0] > Low[1]) huntDetected = true;
                }
                else
                {
                    double ph = double.MinValue; for (int i = 1; i <= 5; i++) ph = Math.Max(ph, High[i]);
                    if (High[0] > ph && Close[0] < High[1]) huntDetected = true;
                }
                if (huntDetected)
                {
                    stopHuntSuspendBars = 4;
                    Print(TAG + "STOP-HUNT SPIKE — backtrack window 4 bars");
                    UpdateDashboardStatus("MM stop-hunt — backtrack armed", Brushes.Yellow);
                }
            }
            // Decay if stop-hunt is suspending
            if (stopHuntSuspendBars > 0) s *= 0.5;

            // Smooth blend
            if (trapScore > 0 && s < trapScore) trapScore = trapScore * 0.85 + s * 0.15;
            else trapScore = s;

            trapBarsInTrade++;
            trapDetected = trapScore >= 50;

            // Trap escape
            if (trapScore >= 65 && adverse > 10)
            {
                Print(TAG + "TRAP ESCAPE IMMEDIATE score=" + trapScore.ToString("F0") + " adv=" + adverse.ToString("F1"));
                trapEscapeCooldownBar = CurrentBar;
                lastExitReason = "TRAP_IMM";
                if (openTradeDirection == 1) ExitLong(" ", " ");
                else ExitShort(" ", " ");
                stopsArmed = false; pendingExit = true;
                return;
            }
            if (trapScore >= 50 && adverse > 5)
            {
                trapEscapeBars++;
                if (trapEscapeBars >= 2)
                {
                    Print(TAG + "TRAP ESCAPE GRAD bars=" + trapEscapeBars + " adv=" + adverse.ToString("F1"));
                    trapEscapeCooldownBar = CurrentBar;
                    lastExitReason = "TRAP_GRAD";
                    if (openTradeDirection == 1) ExitLong(" ", " ");
                    else ExitShort(" ", " ");
                    stopsArmed = false; pendingExit = true;
                }
            }
            else trapEscapeBars = 0;
        }
        #endregion

        // ===========================================================
        //  ENTRY / EXIT EXECUTION
        // ===========================================================
        #region Entries & exits
        private bool CanEnterTrade(string label, bool isManual, int direction)
        {
            if (pendingExit) { UpdateDashboardStatus(label + " blocked: exit pending", Brushes.Orange); return false; }
            if (dailyLimitHit || dailyProfitHit) { UpdateDashboardStatus(label + " blocked: daily limit", Brushes.OrangeRed); return false; }
            if (emergencyKillActive) { UpdateDashboardStatus(label + " blocked: KILL", Brushes.OrangeRed); return false; }
            // News blackout (manual + auto)
            if (newsBlackoutEnabled && IsInNewsBlackout())
            { UpdateDashboardStatus(label + " blocked: NEWS blackout ±" + newsBlackoutWindowMin + "min", Brushes.Orange); if (enableDiagLog) WriteDiagRow("BLOCK_NEWS", label); return false; }
            // Chop filter (AUTO ONLY — manual entries bypass): protect capital when ADX collapses,
            // EMAs converge, close-range collapses, OR tape is fighting the entry direction.
            if (!isManual && chopFilterEnabled)
            {
                string chopReason;
                if (IsChoppy(direction, out chopReason))
                {
                    UpdateDashboardStatus(label + " blocked: CHOP " + chopReason, Brushes.Orange);
                    if (enableDiagLog) WriteDiagRow("BLOCK_CHOP", chopReason);
                    return false;
                }
            }
            // v6 2.4 — UNKNOWN-Regime Auto-Block (AUTO ONLY — manual entries bypass).
            //   The Regime Classifier marks a bar UNKNOWN when none of TREND_UP/DN/CHOP/SQUEEZE
            //   conditions are clearly met. 2026-04-28 forensic showed BOTH losses (-$370, -$375)
            //   entered with regime=UNKNOWN. When uncertain, the safer move is to wait. We allow
            //   entries in UNKNOWN ONLY when momentum proves itself: brick streak >= N AND ADX slope
            //   >= threshold AND brick color agrees with trade direction.
            if (!isManual && enableUnknownRegimeBlock && currentRegime == "UNKNOWN")
            {
                bool brickAgrees = (direction == 1 && lastNrBrickColor == "G")
                                || (direction == -1 && lastNrBrickColor == "R");
                bool streakOk    = nrBrickStreakCount >= unknownBlockMinStreak;
                bool adxOk       = lastAdxSlope >= unknownBlockMinAdxSlope;
                if (!brickAgrees || !streakOk || !adxOk)
                {
                    UpdateDashboardStatus(label + " blocked: UNKNOWN regime", Brushes.Orange);
                    if (enableDiagLog) WriteDiagRow("BLOCK_UNKNOWN_REGIME",
                        "dir=" + direction + " brick=" + lastNrBrickColor + "x" + nrBrickStreakCount
                        + " adxSlope=" + lastAdxSlope.ToString("F2")
                        + " need brick=" + (direction == 1 ? "G" : "R") + " streak>=" + unknownBlockMinStreak
                        + " adxSlope>=" + unknownBlockMinAdxSlope);
                    return false;
                }
            }
            // SL-cluster cooldown (AUTO ONLY — manual entries bypass): if we just hit N stop-losses
            // in a short window, the regime is toxic for our system right now. Pause new entries so
            // we don't chain another -$400.
            if (!isManual && slClusterCooldownEnabled && Time[0] < slClusterCooldownUntil)
            {
                int remainSec = (int)(slClusterCooldownUntil - Time[0]).TotalSeconds;
                UpdateDashboardStatus(label + " blocked: SL CLUSTER cooldown " + (remainSec / 60) + "m", Brushes.OrangeRed);
                if (enableDiagLog) WriteDiagRow("BLOCK_SL_CLUSTER", "cooldown_remaining_sec=" + remainSec);
                return false;
            }
            // Post-win same-direction cooldown (AUTO ONLY — manual entries bypass): block re-entry
            // in the SAME direction as the most recent winning exit for a short window. Catches MM
            // stop-runs that ramp price against us right after our trail kicked out, then continue trend.
            if (!isManual && postWinSameDirCooldownEnabled && lastWinExitDirection != 0 && lastWinExitDirection == direction)
            {
                double minsSinceWin = (Time[0] - lastWinExitTime).TotalMinutes;
                if (minsSinceWin < postWinSameDirCooldownMin)
                {
                    // v6 2.1 SMART COOLDOWN BYPASS:
                    //   The 7-min post-win cooldown exists because MM often runs our stop right after
                    //   we exit on trail — then continues the trend. That hurts when chop, but on real
                    //   confirmed trends we leave 50pt+ runners on the table (validated 2026-04-28: 21
                    //   BLOCK_POST_WIN events in TREND_DN, including 5 consecutive at 09:55 covering
                    //   a ~50pt continuation). Bypass only when ALL of these prove a true trend:
                    //     (1) Regime classifier says TREND_UP/DN AND agrees with our trade direction
                    //     (2) NinzaRenko brick streak ≥ smartCooldownStreakMin (default 4) in trend color
                    //     (3) ADX slope is RISING (>0)
                    //     (4) At least smartCooldownMinSec (default 60s) has passed since the win
                    //   When all true, we shrink the effective cooldown to 60s instead of 7min.
                    bool smartBypass = false;
                    if (enableSmartCooldown)
                    {
                        bool regimeAgrees = (direction == 1 && currentRegime == "TREND_UP")
                                         || (direction == -1 && currentRegime == "TREND_DN");
                        bool brickAgrees  = (direction == 1 && lastNrBrickColor == "G" && nrBrickStreakCount >= smartCooldownStreakMin)
                                         || (direction == -1 && lastNrBrickColor == "R" && nrBrickStreakCount >= smartCooldownStreakMin);
                        bool adxRising    = lastAdxSlope > 0;
                        bool minTimeOk    = (Time[0] - lastWinExitTime).TotalSeconds >= smartCooldownMinSec;
                        smartBypass = regimeAgrees && brickAgrees && adxRising && minTimeOk;
                        if (smartBypass && enableDiagLog)
                            WriteDiagRow("SMART_COOLDOWN_BYPASS",
                                "dir=" + direction + " regime=" + currentRegime
                                + " brick=" + lastNrBrickColor + "x" + nrBrickStreakCount
                                + " adxSlope=" + lastAdxSlope.ToString("F2")
                                + " sec_since_win=" + (int)(Time[0] - lastWinExitTime).TotalSeconds);
                    }
                    if (!smartBypass)
                    {
                        int remainSec = (int)((postWinSameDirCooldownMin - minsSinceWin) * 60);
                        UpdateDashboardStatus(label + " blocked: POST-WIN cooldown " + remainSec + "s", Brushes.Orange);
                        if (enableDiagLog) WriteDiagRow("BLOCK_POST_WIN", "dir=" + direction + " mins_since_win=" + minsSinceWin.ToString("F1") + " cooldown=" + postWinSameDirCooldownMin + "m");
                        return false;
                    }
                }
            }
            // Directional lockout (AUTO ONLY — manual entries bypass): block this direction if it
            // has been losing repeatedly. Opposite direction is still allowed.
            if (!isManual && dirLockoutEnabled)
            {
                DateTime lockoutEnd = direction == 1 ? longLockoutUntil : (direction == -1 ? shortLockoutUntil : DateTime.MinValue);
                if (Time[0] < lockoutEnd)
                {
                    int remainMin = (int)Math.Ceiling((lockoutEnd - Time[0]).TotalMinutes);
                    UpdateDashboardStatus(label + " blocked: DIR LOCKOUT " + remainMin + "m", Brushes.OrangeRed);
                    if (enableDiagLog) WriteDiagRow("BLOCK_DIR_LOCKOUT", "dir=" + direction + " remain_min=" + remainMin);
                    return false;
                }
            }
            // Extension filter (AUTO ONLY — manual entries bypass): block late chases. When price
            // is over-extended from VWAP relative to ATR, the move has likely already moved enough
            // that MM stop-runs become high-probability. Only enforced when ATR is meaningful
            // (>= extensionMinAtrPoints) so we don't over-block quiet midday sessions.
            if (!isManual && extensionFilterEnabled && vwapValue > 0 && indAtr != null && indAtr.IsValidDataPoint(0))
            {
                double atrNow = indAtr[0];
                if (atrNow >= extensionMinAtrPoints)
                {
                    double dist = Math.Abs(Close[0] - vwapValue);
                    double ratio = dist / atrNow;
                    if (ratio > extensionMaxAtrFromVwap)
                    {
                        // v6 2.2 EXTENSION TREND-BYPASS:
                        //   Extension guard exists because chasing late entries far from VWAP
                        //   gives MM room to ramp price and stop us out before trend resumes.
                        //   But strong trends extend by definition — in TREND_DN we observed
                        //   13 BLOCK_EXTENSION events on 2026-04-28, missing real follow-through.
                        //   Bypass when ALL of these prove the move is healthy, not late:
                        //     (1) Regime classifier says TREND_UP/DN AND agrees with trade dir
                        //     (2) NinzaRenko brick streak <= extensionBypassStreakMax (default 8)
                        //         (still skip if streak is already monstrous = mean reversion risk)
                        //     (3) ADX slope is RISING (>0)
                        //     (4) Brick color agrees with trade direction
                        bool trendBypass = false;
                        if (enableExtensionTrendBypass)
                        {
                            bool regimeAgrees = (direction == 1 && currentRegime == "TREND_UP")
                                             || (direction == -1 && currentRegime == "TREND_DN");
                            bool brickAgrees  = (direction == 1 && lastNrBrickColor == "G")
                                             || (direction == -1 && lastNrBrickColor == "R");
                            bool streakOk     = nrBrickStreakCount > 0 && nrBrickStreakCount <= extensionBypassStreakMax;
                            bool adxRising    = lastAdxSlope > 0;
                            trendBypass = regimeAgrees && brickAgrees && streakOk && adxRising;
                            if (trendBypass && enableDiagLog)
                                WriteDiagRow("EXTENSION_BYPASS",
                                    "dir=" + direction + " regime=" + currentRegime
                                    + " brick=" + lastNrBrickColor + "x" + nrBrickStreakCount
                                    + " adxSlope=" + lastAdxSlope.ToString("F2")
                                    + " ratio=" + ratio.ToString("F2"));
                        }
                        if (!trendBypass)
                        {
                            UpdateDashboardStatus(label + " blocked: EXTENSION " + ratio.ToString("F1") + "xATR", Brushes.Orange);
                            if (enableDiagLog) WriteDiagRow("BLOCK_EXTENSION", "dir=" + direction + " dist=" + dist.ToString("F1") + " atr=" + atrNow.ToString("F1") + " ratio=" + ratio.ToString("F2") + " max=" + extensionMaxAtrFromVwap.ToString("F2"));
                            return false;
                        }
                    }
                }
            }
            if (enteredThisBar && !allowMultiEntryPerBar) { UpdateDashboardStatus(label + " blocked: already entered this bar", Brushes.Orange); return false; }
            if (maxTradesPerDay > 0 && !isManual && dailyTradeCount >= maxTradesPerDay
                && Position.MarketPosition == MarketPosition.Flat)
            { UpdateDashboardStatus(label + " blocked: max trades/day", Brushes.Orange); return false; }
            int ct = ToTime(Time[0]);
            if (ct >= 165500 && ct < 180000) { UpdateDashboardStatus(label + " blocked: CME maint", Brushes.OrangeRed); return false; }
            if (!isManual && tradingHoursEnabled && (ct < tradingStartTime || ct >= flattenTime))
            { UpdateDashboardStatus(label + " blocked: outside auto hours", Brushes.Orange); return false; }
            if (direction == 1 && Position.MarketPosition == MarketPosition.Short) { ExecutePartialClose(contracts); return false; }
            if (direction == -1 && Position.MarketPosition == MarketPosition.Long) { ExecutePartialClose(contracts); return false; }
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                bool sameDir = (direction == 1 && Position.MarketPosition == MarketPosition.Long)
                            || (direction == -1 && Position.MarketPosition == MarketPosition.Short);
                if (sameDir && Position.Quantity >= maxContracts)
                { UpdateDashboardStatus("Max contracts (" + maxContracts + ")", Brushes.Orange); return false; }
                // Suppress same-direction DCA add after consecutive losses (capital protection)
                if (sameDir && suppressDcaOnLossStreak && consecutiveLosses >= suppressDcaLossN)
                { UpdateDashboardStatus(label + " blocked: DCA suppressed (" + consecutiveLosses + " losses)", Brushes.Orange); if (enableDiagLog) WriteDiagRow("BLOCK_DCA", "consec=" + consecutiveLosses); return false; }
            }
            if (entryDelaySeconds > 0
                && ((State == State.Realtime ? DateTime.Now : Time[0]) - lastEntryWallTime).TotalSeconds < entryDelaySeconds)
            { UpdateDashboardStatus(label + " blocked: cooldown", Brushes.Orange); return false; }
            return true;
        }

        private void TryAutoEntry()
        {
            int ct = ToTime(Time[0]);
            bool insideHours = !tradingHoursEnabled || (ct >= tradingStartTime && ct < flattenTime);
            if (!insideHours || dailyLimitHit || dailyProfitHit || emergencyKillActive) return;
            if (maxTradesPerDay > 0 && dailyTradeCount >= maxTradesPerDay) return;

            // Bar-based cooldowns
            bool postLossCD = consecutiveLosses >= 1 && lastLossBarNumber > 0
                              && (CurrentBar - lastLossBarNumber) < 8;
            bool tooSoon    = lastTradeExitBar > 0 && (CurrentBar - lastTradeExitBar) < 4;
            bool trapCD     = trapEscapeCooldownBar > 0 && (CurrentBar - trapEscapeCooldownBar) < 8;
            bool voidCD     = voidBarsRemaining > 0;
            if (postLossCD || tooSoon || trapCD || voidCD) return;

            // RSI direction + slope
            double rsiSlope = (CurrentBar > 2 && indRsi != null) ? indRsi[0] - indRsi[2] : 0;
            bool rsiUp = rsiSlope > 0.5, rsiDown = rsiSlope < -0.5;

            // Midday penalty: require +5 conf
            int hour = ct / 10000;
            bool isMidday = hour >= 11 && hour < 14;
            double effMin = isMidday ? Math.Max(minSignalConfidence, minSignalConfidence + 5) : minSignalConfidence;
            // Adaptive intra-day window: while tighten is active (3+ losses in last 5), require +N conf
            if (adaptiveWindowEnabled && adaptiveTightenActive)
            {
                effMin += adaptiveConfBoost;
                if (enableDiagLog && IsFirstTickOfBar) WriteDiagRow("ADAPT_TIGHTEN_ACTIVE", "effMin=" + effMin.ToString("F1"));
            }

            // Volatility spike block
            double atrNow = indAtr[0];
            bool volSpike = atrNow > 0 && (High[0] - Low[0]) > 2.0 * atrNow;

            // Overextension
            double distFromEma = Math.Abs(Close[0] - indEmaFast[0]);
            double overextL = sessionConfirmedBias > 0 ? 3.0 : 1.5;
            double overextS = sessionConfirmedBias < 0 ? 3.0 : 1.5;
            bool overLong  = atrNow > 0 && distFromEma > overextL * atrNow;
            bool overShort = atrNow > 0 && distFromEma > overextS * atrNow;

            // Near prevDay levels (block only on approach side)
            bool nearH = prevDayHigh > 0 && Close[0] <= prevDayHigh + atrNow * 0.1 && Close[0] > prevDayHigh - atrNow * 0.5;
            bool nearL = prevDayLow  > 0 && Close[0] >= prevDayLow                 && Close[0] < prevDayLow  + atrNow * 0.5;

            // HTF block
            bool htfBlockL = htfBias < 0 || sessionConfirmedBias < 0;
            bool htfBlockS = htfBias > 0 || sessionConfirmedBias > 0;

            // Order-flow tape block (only realtime; data-driven)
            double tape = orderFlowFilterEnabled ? cachedTapeDelta : 0;
            bool tapeBlockL = orderFlowFilterEnabled && tape < -0.3;
            bool tapeBlockS = orderFlowFilterEnabled && tape >  0.3;

            // Liquidity-sweep boost: classic MM stop-hunt reversal pattern
            //   Bull sweep => boost LONG (and let it bypass overextension)
            //   Bear sweep => boost SHORT (and let it bypass overextension)
            bool sweepBull = liquiditySweepBoostEnabled && IsLiquiditySweepBull();
            bool sweepBear = liquiditySweepBoostEnabled && IsLiquiditySweepBear();
            double effMinL = sweepBull ? Math.Max(35.0, effMin - liquiditySweepConfBoost) : effMin;
            double effMinS = sweepBear ? Math.Max(35.0, effMin - liquiditySweepConfBoost) : effMin;
            if (sweepBull) overLong  = false;
            if (sweepBear) overShort = false;
            if ((sweepBull || sweepBear) && enableDiagLog)
                WriteDiagRow("SWEEP_BOOST", "bull=" + sweepBull + " bear=" + sweepBear + " lookback=" + liquiditySweepLookback);

            if (lastBullConfidence >= effMinL && !rsiDown && !overLong && !nearH && !volSpike
                && !htfBlockL && !tapeBlockL)
            {
                ExecuteLongEntry(false);
            }
            else if (lastBearConfidence >= effMinS && !rsiUp && !overShort && !nearL && !volSpike
                && !htfBlockS && !tapeBlockS)
            {
                ExecuteShortEntry(false);
            }
        }

        private void ExecuteLongEntry(bool isManual)
        {
            if (!CanEnterTrade("BUY MKT", isManual, 1)) return;
            if (Position.MarketPosition == MarketPosition.Flat) { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            lastAutoStrategyUsed = StrategyDisplay(autoStrategy == 2 ? bestAutoStrategy : autoStrategy);
            EnterLong(contracts, " ");
            activeEntrySignals.Add(" ");
            openTradeDirection = 1;
            lastEntryWallTime  = State == State.Realtime ? DateTime.Now : Time[0];
            enteredThisBar = true;
            // qty + avg recomputed in OnOrderUpdate.Filled (do NOT pre-increment)
            Print(TAG + "ENTER LONG #" + openDcaCount + " qty=" + contracts);
            UpdateDashboardStatus("LONG #" + openDcaCount, Brushes.LimeGreen);
            if (enableDiagLog)
            {
                // Capture the full decision context at entry time (helps post-trade analysis).
                double atrNow = indAtr != null && indAtr.IsValidDataPoint(0) ? indAtr[0] : 0;
                double distVw = vwapValue > 0 ? Close[0] - vwapValue : 0;
                WriteDiagRow("ENTRY_LONG",
                    "manual=" + (isManual ? "T" : "F")
                    + " bull=" + lastBullConfidence.ToString("F1")
                    + " bear=" + lastBearConfidence.ToString("F1")
                    + " sig=" + manualSignalLevel
                    + " htf=" + htfBias
                    + " tape=" + cachedTapeDelta.ToString("F2")
                    + " emaXAgo=" + emaCrossBarsAgo
                    + " distVwap=" + distVw.ToString("F1")
                    + " atr=" + atrNow.ToString("F1")
                    + " px=" + Close[0].ToString("F2")
                    + " sl=" + hiddenStopPrice.ToString("F2")
                    + " tp=" + hiddenTargetPrice.ToString("F2"));
            }
        }

        private void ExecuteShortEntry(bool isManual)
        {
            if (!CanEnterTrade("SELL MKT", isManual, -1)) return;
            if (Position.MarketPosition == MarketPosition.Flat) { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            lastAutoStrategyUsed = StrategyDisplay(autoStrategy == 2 ? bestAutoStrategy : autoStrategy);
            EnterShort(contracts, " ");
            activeEntrySignals.Add(" ");
            openTradeDirection = -1;
            lastEntryWallTime  = State == State.Realtime ? DateTime.Now : Time[0];
            enteredThisBar = true;
            Print(TAG + "ENTER SHORT #" + openDcaCount + " qty=" + contracts);
            UpdateDashboardStatus("SHORT #" + openDcaCount, Brushes.OrangeRed);
            if (enableDiagLog)
            {
                double atrNow = indAtr != null && indAtr.IsValidDataPoint(0) ? indAtr[0] : 0;
                double distVw = vwapValue > 0 ? Close[0] - vwapValue : 0;
                WriteDiagRow("ENTRY_SHORT",
                    "manual=" + (isManual ? "T" : "F")
                    + " bull=" + lastBullConfidence.ToString("F1")
                    + " bear=" + lastBearConfidence.ToString("F1")
                    + " sig=" + manualSignalLevel
                    + " htf=" + htfBias
                    + " tape=" + cachedTapeDelta.ToString("F2")
                    + " emaXAgo=" + emaCrossBarsAgo
                    + " distVwap=" + distVw.ToString("F1")
                    + " atr=" + atrNow.ToString("F1")
                    + " px=" + Close[0].ToString("F2")
                    + " sl=" + hiddenStopPrice.ToString("F2")
                    + " tp=" + hiddenTargetPrice.ToString("F2"));
            }
        }

        private void ExecuteBuyAskEntry()
        {
            if (!CanEnterTrade("BUY ASK", true, 1)) return;
            double lp = GetCurrentAsk(); if (lp <= 0) lp = Close[0] + TickSize;
            if (Position.MarketPosition == MarketPosition.Flat) { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            EnterLongLimit(contracts, lp, " ");
            activeEntrySignals.Add(" ");
            openTradeDirection = 1;
            lastEntryWallTime = State == State.Realtime ? DateTime.Now : Time[0];
            aggressiveLimitSubmitTime = lastEntryWallTime;
            enteredThisBar = true;
            Print(TAG + "BUY ASK @ " + lp.ToString("F2"));
            UpdateDashboardStatus("BUY ASK @ " + lp.ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteSellBidEntry()
        {
            if (!CanEnterTrade("SELL BID", true, -1)) return;
            double lp = GetCurrentBid(); if (lp <= 0) lp = Close[0] - TickSize;
            if (Position.MarketPosition == MarketPosition.Flat) { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            EnterShortLimit(contracts, lp, " ");
            activeEntrySignals.Add(" ");
            openTradeDirection = -1;
            lastEntryWallTime = State == State.Realtime ? DateTime.Now : Time[0];
            aggressiveLimitSubmitTime = lastEntryWallTime;
            enteredThisBar = true;
            Print(TAG + "SELL BID @ " + lp.ToString("F2"));
            UpdateDashboardStatus("SELL BID @ " + lp.ToString("F2"), Brushes.OrangeRed);
        }

        private void ExecuteLongLimitEntry()
        {
            if (!CanEnterTrade("BUY LMT", true, 1)) return;
            double lp = GetCurrentBid(); if (lp <= 0) lp = Close[0] - TickSize;
            if (Position.MarketPosition == MarketPosition.Flat) { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            EnterLongLimit(contracts, lp, " ");
            activeEntrySignals.Add(" ");
            openTradeDirection = 1;
            lastEntryWallTime = State == State.Realtime ? DateTime.Now : Time[0];
            enteredThisBar = true;
            Print(TAG + "BUY LMT @ " + lp.ToString("F2"));
            UpdateDashboardStatus("BUY LMT @ " + lp.ToString("F2"), Brushes.LimeGreen);
        }

        private void ExecuteShortLimitEntry()
        {
            if (!CanEnterTrade("SELL LMT", true, -1)) return;
            double lp = GetCurrentAsk(); if (lp <= 0) lp = Close[0] + TickSize;
            if (Position.MarketPosition == MarketPosition.Flat) { tradeSequence++; dailyTradeCount++; }
            openDcaCount++;
            EnterShortLimit(contracts, lp, " ");
            activeEntrySignals.Add(" ");
            openTradeDirection = -1;
            lastEntryWallTime = State == State.Realtime ? DateTime.Now : Time[0];
            enteredThisBar = true;
            Print(TAG + "SELL LMT @ " + lp.ToString("F2"));
            UpdateDashboardStatus("SELL LMT @ " + lp.ToString("F2"), Brushes.OrangeRed);
        }

        private void ArmHiddenStops()
        {
            if (averageEntryPrice <= 0)
            {
                Print(TAG + "ArmHiddenStops skipped — avgEntry=0 (will arm on fill)");
                return;
            }
            // -------- RESET to ORIGINAL config (never carry-over from previous trade) --------
            // Manual nudges from prior trade are wiped here so each new trade starts clean.
            if (originalsSnapshotted)
            {
                if (slPoints      != origSlPoints)      { slPoints      = origSlPoints;      Print(TAG + "SL reset to original " + slPoints + "pt"); }
                if (tpPoints      != origTpPoints)      { tpPoints      = origTpPoints;      Print(TAG + "TP reset to original " + tpPoints + "pt"); }
                if (jumpSlPercent != origJumpSlPercent) { jumpSlPercent = origJumpSlPercent; Print(TAG + "Jump% reset to original " + jumpSlPercent + "%"); }
                if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => { UpdateAdjustLabels(); UpdateJumpLabel(); });
            }
            runnerModeActive = (openTradeDirection == 1 && htfBias > 0)
                            || (openTradeDirection == -1 && htfBias < 0);
            int effTp = runnerModeActive ? 500 : tpPoints;
            double tickPt = NQ_TICKS_PER_POINT * TickSize;
            int effSl = GetEffectiveSlPoints();
            double slOff = effSl * tickPt;
            double tpOff = effTp * tickPt;
            breakevenLocked = false;
            entryBar = CurrentBar;
            // reset trail state on every arm — user nudges from prior trade are CLEARED
            trailActive = false; trailPrice = 0; trailMaxProfitPts = 0; trailTierName = "";
            manualTrailEarlyStart = false;
            manualTrailOffsetPoints = 0;
            trapScore = 0; trapDetected = false; trapBarsInTrade = 0; trapEscapeBars = 0;
            stopHuntSuspendBars = 0;

            if (openTradeDirection == 1)
            {
                hiddenStopPrice = averageEntryPrice - slOff;
                hiddenTargetPrice = averageEntryPrice + tpOff;
            }
            else if (openTradeDirection == -1)
            {
                hiddenStopPrice = averageEntryPrice + slOff;
                hiddenTargetPrice = averageEntryPrice - tpOff;
            }
            originalSlPrice = hiddenStopPrice;
            // Clamp TP to prevDay levels if relevant
            tpClampedByPrevDay = false;
            double minTpDist = 8 * tickPt;
            // Skip the clamp on high-ADX trend days (price more likely to slice through prevDay levels):
            double adxNow = (indAdx != null && CurrentBar > 14) ? indAdx[0] : 0;
            bool clampAllowed = !skipPrevDayClampOnHighAdx || adxNow < highAdxThreshold;
            if (clampAllowed && prevDayHigh > 0 && openTradeDirection == 1
                && prevDayHigh > averageEntryPrice + minTpDist
                && prevDayHigh < hiddenTargetPrice)
            { hiddenTargetPrice = prevDayHigh - TickSize; tpClampedByPrevDay = true; }
            if (clampAllowed && prevDayLow > 0 && openTradeDirection == -1
                && prevDayLow < averageEntryPrice - minTpDist
                && prevDayLow > hiddenTargetPrice)
            { hiddenTargetPrice = prevDayLow + TickSize; tpClampedByPrevDay = true; }
            if (!clampAllowed && enableDiagLog) WriteDiagRow("CLAMP_SKIP", "adx=" + adxNow.ToString("F1") + " thr=" + highAdxThreshold.ToString("F1"));
            stopsArmed = true;
            if (runnerModeActive) Print(TAG + "RUNNER mode armed dir=" + openTradeDirection);
            if (enableDiagLog) WriteDiagRow("STOPS_ARMED",
                "dir=" + openTradeDirection + " entry=" + averageEntryPrice.ToString("F2")
                + " sl=" + hiddenStopPrice.ToString("F2") + " tp=" + hiddenTargetPrice.ToString("F2")
                + " slPt=" + slPoints + " tpPt=" + tpPoints + (runnerModeActive ? " RUNNER" : "")
                + (tpClampedByPrevDay ? " PD_CLAMP" : ""));
        }

        // Non-destructive SL/TP resize: keeps trail, BE lock, trap state, and original SL reference intact.
        // Called when user nudges SL or TP via dashboard +/- buttons.
        private void ResizeHiddenStops()
        {
            if (averageEntryPrice <= 0 || openTradeDirection == 0) return;
            double tickPt = NQ_TICKS_PER_POINT * TickSize;
            double newSl, newTp;
            if (openTradeDirection == 1)
            {
                newSl = averageEntryPrice - (GetEffectiveSlPoints() * tickPt);
                newTp = averageEntryPrice + ((runnerModeActive ? 500 : tpPoints) * tickPt);
            }
            else
            {
                newSl = averageEntryPrice + (GetEffectiveSlPoints() * tickPt);
                newTp = averageEntryPrice - ((runnerModeActive ? 500 : tpPoints) * tickPt);
            }
            // PrevDay clamp on TP
            tpClampedByPrevDay = false;
            double minTpDist = 8 * tickPt;
            double adxNow = (indAdx != null && CurrentBar > 14) ? indAdx[0] : 0;
            bool clampAllowed = !skipPrevDayClampOnHighAdx || adxNow < highAdxThreshold;
            if (clampAllowed && prevDayHigh > 0 && openTradeDirection == 1
                && prevDayHigh > averageEntryPrice + minTpDist && prevDayHigh < newTp)
            { newTp = prevDayHigh - TickSize; tpClampedByPrevDay = true; }
            if (clampAllowed && prevDayLow > 0 && openTradeDirection == -1
                && prevDayLow < averageEntryPrice - minTpDist && prevDayLow > newTp)
            { newTp = prevDayLow + TickSize; tpClampedByPrevDay = true; }

            // SL: if breakeven is locked OR trail has already moved SL favorable,
            // never RELAX SL backwards (would expose more risk than user expects).
            if (breakevenLocked)
            {
                if (openTradeDirection == 1 && newSl < hiddenStopPrice) newSl = hiddenStopPrice;
                if (openTradeDirection == -1 && newSl > hiddenStopPrice) newSl = hiddenStopPrice;
            }
            double oldSl = hiddenStopPrice, oldTp = hiddenTargetPrice;
            hiddenStopPrice = newSl;
            hiddenTargetPrice = newTp;
            // Update originalSlPrice only if we LOOSENED (so trail backtrack respects new floor)
            if (openTradeDirection == 1 && newSl < originalSlPrice) originalSlPrice = newSl;
            else if (openTradeDirection == -1 && newSl > originalSlPrice) originalSlPrice = newSl;
            Print(TAG + "RESIZE SL=" + hiddenStopPrice.ToString("F2") + " TP=" + hiddenTargetPrice.ToString("F2")
                + " (slPt=" + slPoints + " tpPt=" + tpPoints + ")");
            if (enableDiagLog)
            {
                if (Math.Abs(newTp - oldTp) > TickSize / 2.0)
                    WriteDiagRow("TP_NUDGE", "src=manual old=" + oldTp.ToString("F2") + " new=" + hiddenTargetPrice.ToString("F2")
                        + " tpPt=" + tpPoints + (tpClampedByPrevDay ? " PD_CLAMP" : ""));
                if (Math.Abs(newSl - oldSl) > TickSize / 2.0)
                    WriteDiagRow("SL_NUDGE", "src=resize old=" + oldSl.ToString("F2") + " new=" + hiddenStopPrice.ToString("F2")
                        + " slPt=" + slPoints);
            }
        }

        // Thread-safe trigger from WPF UI thread.
        // NOTE: Do NOT redraw here — pendingSlTpResize is processed on next OnBarUpdate by
        // ProcessPendingButtons, which calls ResizeHiddenStops THEN RedrawAnnotationsSafe.
        // Drawing here would render the STALE hiddenTargetPrice (race condition).
        private void RequestSlTpResize() { pendingSlTpResize = true; }

        // Direct SL nudge (price-space). Negative dPts = WIDEN (move away from price), positive = TIGHTEN.
        // Works correctly after JumpSL/BE-lock because it operates on hiddenStopPrice, not slPoints.
        // NOTE: redraw happens in ProcessPendingButtons after NudgeSlPricePoints runs (avoid stale-draw race).
        private void RequestSlNudgePoints(int dPts)
        {
            pendingSlNudge += dPts;
        }

        private void NudgeSlPricePoints(int dPts)
        {
            if (!stopsArmed || averageEntryPrice <= 0 || openTradeDirection == 0)
            { UpdateDashboardStatus("SL nudge ignored (flat)", Brushes.Orange); return; }
            double price = Close[0];
            if (State == State.Realtime)
            {
                double v = openTradeDirection == 1 ? GetCurrentBid(0) : GetCurrentAsk(0);
                if (v > 0) price = v;
            }
            double tickPt = NQ_TICKS_PER_POINT * TickSize;
            double delta = Math.Abs(dPts) * tickPt;
            double oldSl = hiddenStopPrice;
            if (openTradeDirection == 1)
            {
                // dPts > 0 (TIGHTEN): SL moves UP (closer to price)
                // dPts < 0 (WIDEN):   SL moves DOWN (further from price)
                hiddenStopPrice += dPts > 0 ? +delta : -delta;
                // Clamp: never above price-1tk (would self-stop), never below originalSl-200pt (sanity floor)
                double maxSl = price - TickSize;
                if (hiddenStopPrice > maxSl) hiddenStopPrice = maxSl;
                double sanityFloor = averageEntryPrice - 200 * tickPt;
                if (hiddenStopPrice < sanityFloor) hiddenStopPrice = sanityFloor;
            }
            else
            {
                // SHORT: dPts > 0 (TIGHTEN) → SL moves DOWN (closer to price)
                //        dPts < 0 (WIDEN)   → SL moves UP   (further from price)
                hiddenStopPrice += dPts > 0 ? -delta : +delta;
                double minSl = price + TickSize;
                if (hiddenStopPrice < minSl) hiddenStopPrice = minSl;
                double sanityCap = averageEntryPrice + 200 * tickPt;
                if (hiddenStopPrice > sanityCap) hiddenStopPrice = sanityCap;
            }
            hiddenStopPrice = Math.Round(hiddenStopPrice / TickSize) * TickSize;
            // Keep slPoints in sync with the ACTUAL distance from price (so dashboard reads correctly).
            double slDistPts = openTradeDirection == 1
                ? (price - hiddenStopPrice) / tickPt
                : (hiddenStopPrice - price) / tickPt;
            slPoints = Math.Max(1, (int)Math.Round(Math.Abs(slDistPts)));
            // Tightening = manual lock; matches Jump SL behavior and prevents auto-BE from undoing it.
            if (dPts > 0) breakevenLocked = true;
            // Update originalSlPrice if we widened (so trail backtrack respects new floor).
            if (dPts < 0)
            {
                if (openTradeDirection == 1 && hiddenStopPrice < originalSlPrice) originalSlPrice = hiddenStopPrice;
                if (openTradeDirection == -1 && hiddenStopPrice > originalSlPrice) originalSlPrice = hiddenStopPrice;
            }
            if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());
            RedrawAnnotationsSafe();
            Print(TAG + "SL NUDGE " + (dPts > 0 ? "+" : "") + dPts + "pt  " + oldSl.ToString("F2") + " -> " + hiddenStopPrice.ToString("F2")
                + "  (price=" + price.ToString("F2") + " dist=" + slDistPts.ToString("F1") + "pt)");
            if (enableDiagLog) WriteDiagRow("SL_NUDGE", "src=manual delta=" + (dPts > 0 ? "+" : "") + dPts + "pt old=" + oldSl.ToString("F2")
                + " new=" + hiddenStopPrice.ToString("F2") + " price=" + price.ToString("F2")
                + (breakevenLocked ? " BE_LOCK" : ""));
            UpdateDashboardStatus("SL " + (dPts > 0 ? "tightened" : "widened") + " " + Math.Abs(dPts) + "pt -> " + hiddenStopPrice.ToString("F2"),
                dPts > 0 ? Brushes.LimeGreen : Brushes.Yellow);
        }

        // -----------------------------------------------------------
        //  MANUAL TRAIL CONTROL — invoked from dashboard buttons.
        //  ActivateTrailManual : set manualTrailEarlyStart=true so MonitorAdaptiveTrail
        //                        activates trail IMMEDIATELY on next tick using AGGRESSIVE
        //                        distance (max(2pt, 0.5×ATR)). Auto-ratchet then continues.
        //  NudgeTrailDistancePoints(±n) : adjust manualTrailOffsetPoints; auto-trail honors
        //                        the offset on every subsequent ratchet, so user adjustment
        //                        STICKS even as trail moves.
        // -----------------------------------------------------------
        private void ActivateTrailManual()
        {
            if (!stopsArmed || averageEntryPrice <= 0 || openTradeDirection == 0)
            {
                Print(TAG + "TRAIL manual-activate skipped (no live position)");
                return;
            }
            // If trail already active, just lock it tighter NOW (re-anchor at aggressive distance).
            // If not active, set the early-start flag — next tick of MonitorAdaptiveTrail will arm it.
            manualTrailEarlyStart = true;
            if (trailActive)
            {
                // Re-anchor immediately at aggressive distance.
                double price = Close[0];
                if (State == State.Realtime)
                {
                    double v = openTradeDirection == 1 ? GetCurrentBid(0) : GetCurrentAsk(0);
                    if (v > 0) price = v;
                }
                double tickPt = NQ_TICKS_PER_POINT * TickSize;
                double atrPts = indAtr[0] / tickPt;
                double dist = Math.Max(2.0, atrPts * aggressiveTrailMaxAtrFactor) + manualTrailOffsetPoints;
                if (dist < 1.0) dist = 1.0;
                double newTrail = openTradeDirection == 1
                    ? Math.Round((price - dist * tickPt) / TickSize) * TickSize
                    : Math.Round((price + dist * tickPt) / TickSize) * TickSize;
                // Safety: never beyond originalSL (no extra risk), never past current price (no self-stop).
                if (originalSlPrice > 0)
                {
                    if (openTradeDirection == 1 && newTrail < originalSlPrice) newTrail = originalSlPrice;
                    if (openTradeDirection == -1 && newTrail > originalSlPrice) newTrail = originalSlPrice;
                }
                if (openTradeDirection == 1 && newTrail >= price - TickSize) newTrail = price - TickSize;
                if (openTradeDirection == -1 && newTrail <= price + TickSize) newTrail = price + TickSize;
                // RATCHET-only: never relax during re-anchor (use the more favorable of old vs new)
                if (openTradeDirection == 1)  trailPrice = Math.Max(trailPrice, newTrail);
                else                          trailPrice = Math.Min(trailPrice, newTrail);
                trailTierName = "Aggr";
                Print(TAG + "TRAIL RE-ANCHOR (TRL NOW) @ " + trailPrice.ToString("F2") + " dist=" + dist.ToString("F1") + "pt");
            }
            else
            {
                Print(TAG + "TRAIL EARLY-START armed (TRL NOW) — will activate on next monitor tick");
            }
            UpdateDashboardStatus("TRL NOW armed (aggressive)", Brushes.Magenta);
        }

        private void NudgeTrailDistancePoints(int dPoints)
        {
            if (!stopsArmed || openTradeDirection == 0) { Print(TAG + "TRAIL nudge ignored (flat)"); return; }
            // First nudge auto-arms trail (so user doesn't have to press TRL NOW separately).
            if (!trailActive) { manualTrailEarlyStart = true; ActivateTrailManual(); }
            // Add to the persistent offset — auto-ratchet honors this on every pass.
            manualTrailOffsetPoints += dPoints;
            // Apply the offset IMMEDIATELY by recomputing trail (don't wait for next monitor tick).
            if (trailActive)
            {
                double tickPt = NQ_TICKS_PER_POINT * TickSize;
                double price = Close[0];
                if (State == State.Realtime)
                {
                    double v = openTradeDirection == 1 ? GetCurrentBid(0) : GetCurrentAsk(0);
                    if (v > 0) price = v;
                }
                double dist = Math.Max(1.0, GetTrailDistance() + manualTrailOffsetPoints);
                double newTrail = openTradeDirection == 1
                    ? Math.Round((price - dist * tickPt) / TickSize) * TickSize
                    : Math.Round((price + dist * tickPt) / TickSize) * TickSize;
                // Safety clamps:
                if (openTradeDirection == 1 && newTrail >= price - TickSize) newTrail = price - TickSize;
                if (openTradeDirection == -1 && newTrail <= price + TickSize) newTrail = price + TickSize;
                if (originalSlPrice > 0)
                {
                    if (openTradeDirection == 1 && newTrail < originalSlPrice) newTrail = originalSlPrice;
                    if (openTradeDirection == -1 && newTrail > originalSlPrice) newTrail = originalSlPrice;
                }
                // Tightening (−): ALWAYS move trail to the new tighter price (locks profit immediately).
                // Loosening (+): only allowed if it doesn't move trail BACK against current trail (ratchet-safe).
                double oldTrail = trailPrice;
                if (dPoints < 0)
                {
                    // Tighten: take the more favorable of new vs current
                    if (openTradeDirection == 1)  trailPrice = Math.Max(trailPrice, newTrail);
                    else                          trailPrice = Math.Min(trailPrice, newTrail);
                }
                else
                {
                    // Loosen: only if not yet ratcheted past this point
                    if (openTradeDirection == 1  && newTrail < trailPrice) trailPrice = newTrail;
                    if (openTradeDirection == -1 && newTrail > trailPrice) trailPrice = newTrail;
                }
                Print(TAG + "TRAIL NUDGE " + (dPoints > 0 ? "+" : "") + dPoints + "pt  offset=" + manualTrailOffsetPoints.ToString("F1") + "pt  "
                    + oldTrail.ToString("F2") + " -> " + trailPrice.ToString("F2"));
            }
        }

        private void RequestTrailActivate() { pendingTrailActivate = true; }
        private void RequestTrailNudge(int dPoints) { pendingTrailNudgePoints += dPoints; }

        private void ExecuteFlatten()
        {
            // IMPORTANT: do NOT call Account.Flatten() here. Account.Flatten() unmanaged-flattens
            // the position out from under the managed strategy and NinjaTrader 8 will disable
            // the strategy because its position is no longer in sync. The user wants FLATTEN
            // to ONLY close THIS strategy's position (and cancel its working orders) without
            // disabling the strategy. KILL is the only path that should disable trading
            // (and even KILL leaves the strategy enabled — it just sets emergencyKillActive).
            CancelPendingOrders();
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                ResetPositionStateInternal(false);
                UpdateDashboardStatus("Already flat — state reset", Brushes.CornflowerBlue);
                return;
            }
            lastExitReason = "FLATTEN";
            ManagedExitAll();
            stopsArmed = false; pendingExit = true;
            UpdateDashboardStatus("FLATTEN — closing position (strategy stays enabled)", Brushes.OrangeRed);
        }

        private void ManagedExitAll()
        {
            if (Position.MarketPosition == MarketPosition.Long) ExitLong(" ", " ");
            else if (Position.MarketPosition == MarketPosition.Short) ExitShort(" ", " ");
        }

        private void ExecuteCloseTrade()
        {
            bool hadPending = CancelPendingOrders();
            if (Position.MarketPosition == MarketPosition.Flat)
            {
                if (hadPending) { ResetPositionStateInternal(false); UpdateDashboardStatus("Cancelled pending", Brushes.Yellow); }
                else UpdateDashboardStatus("Already flat", Brushes.CornflowerBlue);
                return;
            }
            lastExitReason = "CLOSE";
            ManagedExitAll();
            stopsArmed = false; pendingExit = true;
            UpdateDashboardStatus("Closing trade...", Brushes.Yellow);
        }

        private void ExecuteCloseOne()
        {
            if (Position.MarketPosition == MarketPosition.Flat) { UpdateDashboardStatus("Already flat", Brushes.CornflowerBlue); return; }
            if (Position.Quantity <= 1) { ExecuteCloseTrade(); return; }
            CancelPendingOrders();
            string sig = activeEntrySignals.Count > 0 ? activeEntrySignals[activeEntrySignals.Count - 1] : "";
            lastExitReason = "CLOSE_ONE";
            if (Position.MarketPosition == MarketPosition.Long) ExitLong(1, "", sig);
            else ExitShort(1, "", sig);
            if (activeEntrySignals.Count > 0) activeEntrySignals.RemoveAt(activeEntrySignals.Count - 1);
            openDcaCount = Math.Max(0, openDcaCount - 1);
            totalContracts = Math.Max(0, totalContracts - 1);
            ArmHiddenStops();
            UpdateDashboardStatus("Closed 1 contract", Brushes.Yellow);
        }

        private void ExecutePartialClose(int qty)
        {
            if (Position.MarketPosition == MarketPosition.Flat) return;
            int posQty = Position.Quantity;
            int closeQty = Math.Min(qty, posQty);
            if (closeQty >= posQty) { ExecuteCloseTrade(); return; }
            CancelPendingOrders();
            string sig = activeEntrySignals.Count > 0 ? activeEntrySignals[activeEntrySignals.Count - 1] : "";
            if (Position.MarketPosition == MarketPosition.Long) ExitLong(closeQty, "", sig);
            else ExitShort(closeQty, "", sig);
            for (int i = 0; i < closeQty && activeEntrySignals.Count > 0; i++)
                activeEntrySignals.RemoveAt(activeEntrySignals.Count - 1);
            totalContracts = Math.Max(0, totalContracts - closeQty);
            openDcaCount = Math.Max(1, (int)Math.Ceiling(totalContracts / Math.Max(1.0, contracts)));
            ArmHiddenStops();
            UpdateDashboardStatus("Closed " + closeQty + " contract(s)", Brushes.Yellow);
        }

        private void ExecuteJumpSL()
        {
            if (!stopsArmed || Position.MarketPosition == MarketPosition.Flat)
            { UpdateDashboardStatus("No SL to jump", Brushes.Orange); return; }
            // Use bid/ask in realtime for FAST, accurate SL placement (was using stale Close[0]).
            double price = Close[0];
            if (State == State.Realtime)
            {
                double v = openTradeDirection == 1 ? GetCurrentBid(0) : GetCurrentAsk(0);
                if (v > 0) price = v;
            }
            double pct = jumpSlPercent / 100.0;
            double tickPt = NQ_TICKS_PER_POINT * TickSize;
            if (openTradeDirection == 1)
            {
                double gap = price - hiddenStopPrice;
                if (gap <= 1.0 * tickPt) { UpdateDashboardStatus("SL at minimum", Brushes.Orange); return; }
                double newSl = hiddenStopPrice + gap * pct;
                double minSl = price - 1.0 * tickPt;
                if (newSl > minSl) newSl = minSl;
                hiddenStopPrice = Math.Round(newSl / TickSize) * TickSize;
                slPoints = Math.Max(1, (int)Math.Round((price - hiddenStopPrice) / tickPt));
            }
            else if (openTradeDirection == -1)
            {
                double gap = hiddenStopPrice - price;
                if (gap <= 1.0 * tickPt) { UpdateDashboardStatus("SL at minimum", Brushes.Orange); return; }
                double newSl = hiddenStopPrice - gap * pct;
                double maxSl = price + 1.0 * tickPt;
                if (newSl < maxSl) newSl = maxSl;
                hiddenStopPrice = Math.Round(newSl / TickSize) * TickSize;
                slPoints = Math.Max(1, (int)Math.Round((hiddenStopPrice - price) / tickPt));
            }
            // Treat manual SL move as a BE-equivalent lock so auto-BE doesn't undo it.
            if (slPoints > 0) breakevenLocked = true;
            // Force-redraw the chart annotations immediately so user sees the new line on next paint.
            if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());
            DrawChartAnnotations();
            Print(TAG + "JUMP SL -> " + hiddenStopPrice.ToString("F2") + " (" + slPoints + "pt) price=" + price.ToString("F2"));
            UpdateDashboardStatus("SL jumped to " + hiddenStopPrice.ToString("F2"), Brushes.Yellow);
        }

        private void ExecuteEmergencyKill()
        {
            emergencyKillActive = true;
            dailyLimitHit = true;
            lastExitReason = "KILL";
            CancelPendingOrders();
            if (Position.MarketPosition != MarketPosition.Flat) ManagedExitAll();
            stopsArmed = false; pendingExit = true;
            UpdateDashboardStatus("⛔ KILL — closed + halted (no new entries until disable+enable)", Brushes.OrangeRed);
        }

        private void ExecuteResetDaily()
        {
            // Manually clear all daily counters/flags. Use at start of new session OR
            // when ResetDailyOnRestart=false and you want to wipe today's stats by hand.
            dailyRealizedPnL    = 0;
            dailyTradeCount     = 0;
            consecutiveLosses   = 0;
            consecutiveWins     = 0;
            lastLossDirection   = 0;
            dailyLimitHit       = false;
            dailyProfitHit      = false;
            emergencyKillActive = false;
            flattenFired        = false;
            // Restore aggressive trail factor to its base if streak-adapted
            if (baseAggressiveTrailFactor > 0) aggressiveTrailMaxAtrFactor = baseAggressiveTrailFactor;
            // Sync processedTradeCount past existing SystemPerformance trades so we don't re-count.
            if (SystemPerformance != null && SystemPerformance.AllTrades != null)
                processedTradeCount = SystemPerformance.AllTrades.Count;
            sessionDate = Time[0].Date;
            Print(TAG + "MANUAL RESET DAILY — counters cleared.");
            if (enableDiagLog) WriteDiagRow("RESET_DAILY", "manual=true");
            UpdateDashboardStatus("Daily counters reset", Brushes.MediumPurple);
        }

        private bool CancelPendingOrders()
        {
            bool any = false;
            try
            {
                var w = new List<Order>();
                foreach (Order o in Account.Orders)
                    if (o.Instrument == Instrument
                        && (o.OrderState == OrderState.Working
                         || o.OrderState == OrderState.Accepted
                         || o.OrderState == OrderState.Submitted))
                        w.Add(o);
                if (w.Count > 0) { Account.Cancel(w.ToArray()); any = true; }
            }
            catch (Exception ex) { Print(TAG + "Cancel: " + ex.Message); }
            return any;
        }

        private void ResetPositionStateInternal(bool fullInit)
        {
            if (!fullInit && Position.MarketPosition != MarketPosition.Flat)
            {
                Print(TAG + "ResetPositionState blocked — Position not flat");
                return;
            }
            stopsArmed = false; pendingExit = false; pendingExitTicks = 0;
            flatSyncGraceTicks = 0; openTradeDirection = 0;
            openDcaCount = 0; totalContracts = 0; averageEntryPrice = 0;
            hiddenStopPrice = 0; hiddenTargetPrice = 0; originalSlPrice = 0;
            tpClampedByPrevDay = false;
            aggressiveLimitSubmitTime = DateTime.MinValue;
            activeEntrySignals.Clear();
            trailPrice = 0; trailActive = false; trailMaxProfitPts = 0; trailTierName = "";
            // v6 2.6 — clear brick-trail per-trade state on flat (anchor stays per-streak).
            pendingBrickFlipExit = false;
            lastSameColorBrickTime = DateTime.MinValue;
            // Auto-clear Runner Mode when position goes flat — it's a per-trade opt-in.
            if (runnerModeActive_user)
            {
                runnerModeActive_user = false;
                if (btnRunnerToggle != null && ChartControl != null)
                {
                    try { ChartControl.Dispatcher.InvokeAsync(() => {
                        btnRunnerToggle.Content = "RUN OFF";
                        btnRunnerToggle.Background = Brushes.DarkRed; }); } catch { }
                }
                Print(TAG + "Runner Mode auto-cleared on flat");
            }
            trapScore = 0; trapDetected = false; trapBarsInTrade = 0; trapEscapeBars = 0;
            stopHuntSuspendBars = 0;
            breakevenLocked = false; runnerModeActive = false;
            backtrackUsedBar = -1;
            lastAutoStrategyUsed = "";
            lastBullConfidence = 0; lastBearConfidence = 0;
            rawBullConfidence = 0; rawBearConfidence = 0;
            if (ChartControl != null) ChartControl.Dispatcher.InvokeAsync(() => UpdateAdjustLabels());
            RemoveDrawObject("hiddenSL"); RemoveDrawObject("hiddenTP");
            RemoveDrawObject("avgEntryLine"); RemoveDrawObject("slLabel"); RemoveDrawObject("tpLabel");
            RemoveDrawObject("adaptiveTrail"); RemoveDrawObject("trailLabel");
        }
        #endregion

        // ===========================================================
        //  ORDER UPDATES — single source of truth for entry pricing
        // ===========================================================
        #region Order/Position/Execution updates
        protected override void OnOrderUpdate(Order order, double limitPrice, double stopPrice,
            int quantity, int filled, double averageFillPrice, OrderState orderState,
            DateTime time, ErrorCode error, string comment)
        {
            if (order == null) return;
            try
            {
                if (orderState == OrderState.Filled)
                {
                    double fp = averageFillPrice > 0 ? averageFillPrice : order.AverageFillPrice;
                    int q   = order.Filled > 0 ? order.Filled : quantity;
                    if (order.IsLong && openTradeDirection == 1)
                    {
                        // recompute strategy avg from actual fills
                        double prevTotal = totalContracts;
                        double newQty = prevTotal + q;
                        averageEntryPrice = prevTotal > 0
                            ? (averageEntryPrice * prevTotal + fp * q) / newQty
                            : fp;
                        totalContracts = newQty;
                        ArmHiddenStops();
                        RedrawAnnotationsSafe();   // render TP/SL lines IMMEDIATELY on fill (no wait for next tick)
                        aggressiveLimitSubmitTime = DateTime.MinValue;
                        Print(TAG + "LONG fill q=" + q + " @ " + fp.ToString("F2") + " avg=" + averageEntryPrice.ToString("F2"));
                    }
                    else if (order.IsShort && openTradeDirection == -1)
                    {
                        double prevTotal = totalContracts;
                        double newQty = prevTotal + q;
                        averageEntryPrice = prevTotal > 0
                            ? (averageEntryPrice * prevTotal + fp * q) / newQty
                            : fp;
                        totalContracts = newQty;
                        ArmHiddenStops();
                        RedrawAnnotationsSafe();   // render TP/SL lines IMMEDIATELY on fill (no wait for next tick)
                        aggressiveLimitSubmitTime = DateTime.MinValue;
                        Print(TAG + "SHORT fill q=" + q + " @ " + fp.ToString("F2") + " avg=" + averageEntryPrice.ToString("F2"));
                    }
                }
                else if (orderState == OrderState.Cancelled || orderState == OrderState.Rejected)
                {
                    if (aggressiveLimitSubmitTime != DateTime.MinValue)
                    {
                        aggressiveLimitSubmitTime = DateTime.MinValue;
                        if (Position.MarketPosition == MarketPosition.Flat && openTradeDirection != 0)
                        {
                            Print(TAG + "Order " + orderState + " — reset");
                            ResetPositionStateInternal(false);
                        }
                    }
                }
            }
            catch (Exception ex) { Print(TAG + "OnOrderUpdate EX: " + ex.Message); }
        }

        protected override void OnPositionUpdate(Position position, double averagePrice,
            int quantity, MarketPosition marketPosition)
        {
            try
            {
                if (marketPosition == MarketPosition.Flat) pendingPositionFlat = true;
            }
            catch (Exception ex) { Print(TAG + "OnPositionUpdate EX: " + ex.Message); }
        }

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            if (execution.Order == null) return;
            try
            {
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
                    if (total > 0)
                    {
                        Trade last = SystemPerformance.AllTrades[total - 1];
                        if (last.Exit.Time >= time.AddSeconds(-2))
                        {
                            lastTradeExitBar = CurrentBar;
                            string reason = string.IsNullOrEmpty(lastExitReason) ? "UNK" : lastExitReason;
                            if (last.ProfitCurrency < 0)
                            {
                                consecutiveLosses++;
                                consecutiveWins = 0;
                                lastLossDirection = last.Entry.MarketPosition == MarketPosition.Long ? 1 : -1;
                                lastLossBarNumber = CurrentBar;
                                RecordTradeOutcome(false);
                                // SL-cluster tracker: count SL hits AND AGGR_ADVERSE bails (both signal a hostile regime).
                                // Excludes BE / AGGR_PULLBACK (those are healthy small-loss exits from peak).
                                if (slClusterCooldownEnabled && (reason == "SL" || reason == "AGGR_ADVERSE"))
                                {
                                    if (slExitTimes == null || slExitTimes.Length != slClusterCount)
                                    { slExitTimes = new DateTime[slClusterCount]; slExitTimesIndex = 0; }
                                    slExitTimes[slExitTimesIndex] = Time[0];
                                    slExitTimesIndex = (slExitTimesIndex + 1) % slClusterCount;
                                    // Check: are ALL slots within the window?
                                    bool allWithin = true;
                                    DateTime windowStart = Time[0].AddMinutes(-slClusterWindowMin);
                                    for (int j = 0; j < slClusterCount; j++)
                                        if (slExitTimes[j] < windowStart) { allWithin = false; break; }
                                    if (allWithin)
                                    {
                                        slClusterCooldownUntil = Time[0].AddMinutes(slClusterCooldownMin);
                                        if (enableDiagLog) WriteDiagRow("SL_CLUSTER_COOLDOWN", "count=" + slClusterCount + " window=" + slClusterWindowMin + "m cooldown=" + slClusterCooldownMin + "m until=" + slClusterCooldownUntil.ToString("HH:mm"));
                                        Print(TAG + "SL CLUSTER -> cooldown until " + slClusterCooldownUntil.ToString("HH:mm"));
                                    }
                                }
                                // Directional lockout tracker: per-direction loss list, prune to window, trigger lockout if N+ losses.
                                if (dirLockoutEnabled && (reason == "SL" || reason == "AGGR_ADVERSE") && lastLossDirection != 0)
                                {
                                    var list = lastLossDirection == 1 ? dirLossTimesLong : dirLossTimesShort;
                                    list.Add(Time[0]);
                                    DateTime cutoff = Time[0].AddMinutes(-dirLockoutWindowMin);
                                    list.RemoveAll(d => d < cutoff);
                                    if (list.Count >= dirLockoutLossN)
                                    {
                                        DateTime until = Time[0].AddMinutes(dirLockoutCooldownMin);
                                        if (lastLossDirection == 1) longLockoutUntil = until; else shortLockoutUntil = until;
                                        if (enableDiagLog) WriteDiagRow("DIR_LOCKOUT_TRIGGER", "dir=" + lastLossDirection + " losses=" + list.Count + " window=" + dirLockoutWindowMin + "m lockout=" + dirLockoutCooldownMin + "m until=" + until.ToString("HH:mm"));
                                        Print(TAG + "DIR LOCKOUT -> dir=" + lastLossDirection + " until " + until.ToString("HH:mm"));
                                    }
                                }
                                if (enableDiagLog) WriteDiagRow("EXIT_LOSS_" + reason, "pnl=" + last.ProfitCurrency.ToString("F2") + " consec=" + consecutiveLosses);
                                // Auto-tighten on N consecutive losses
                                if (autoTightenOnLossesEnabled && consecutiveLosses >= autoTightenLossN && baseAggressiveTrailFactor > 0)
                                {
                                    double newF = Math.Max(0.1, baseAggressiveTrailFactor * autoTightenFactor);
                                    if (Math.Abs(newF - aggressiveTrailMaxAtrFactor) > 0.001)
                                    {
                                        aggressiveTrailMaxAtrFactor = newF;
                                        if (enableDiagLog) WriteDiagRow("ADAPT_TIGHTEN", "consec=" + consecutiveLosses + " factor=" + newF.ToString("F2"));
                                        Print(TAG + "AUTO-TIGHTEN trail factor -> " + newF.ToString("F2") + " (consecLosses=" + consecutiveLosses + ")");
                                    }
                                }
                            }
                            else
                            {
                                consecutiveLosses = 0;
                                consecutiveWins++;
                                lastLossDirection = 0;
                                RecordTradeOutcome(true);
                                // Track for post-win same-direction cooldown
                                lastWinExitTime = Time[0];
                                lastWinExitDirection = last.Entry.MarketPosition == MarketPosition.Long ? 1 : -1;
                                if (enableDiagLog) WriteDiagRow("EXIT_WIN_" + reason, "pnl=" + last.ProfitCurrency.ToString("F2") + " consec=" + consecutiveWins);
                                // Auto-widen on N consecutive wins
                                if (autoWidenOnWinsEnabled && consecutiveWins >= autoWidenWinN && baseAggressiveTrailFactor > 0)
                                {
                                    double newF = Math.Min(2.0, baseAggressiveTrailFactor * autoWidenFactor);
                                    if (Math.Abs(newF - aggressiveTrailMaxAtrFactor) > 0.001)
                                    {
                                        aggressiveTrailMaxAtrFactor = newF;
                                        if (enableDiagLog) WriteDiagRow("ADAPT_WIDEN", "consec=" + consecutiveWins + " factor=" + newF.ToString("F2"));
                                        Print(TAG + "AUTO-WIDEN trail factor -> " + newF.ToString("F2") + " (consecWins=" + consecutiveWins + ")");
                                    }
                                }
                            }
                            lastExitReason = "";
                        }
                    }
                }
                if (dailyRealizedPnL <= -maxDailyLossDollars && !dailyLimitHit)
                { dailyLimitHit = true; ExecuteFlatten(); Print(TAG + "DAILY LOSS LIMIT " + dailyRealizedPnL.ToString("C0") + " — closing position. Strategy stays ENABLED. Press RESET or wait for 18:00 ET session rollover."); UpdateDashboardStatus("⛔ DAILY LOSS LIMIT " + dailyRealizedPnL.ToString("C0") + " — position closed. Strategy STILL ENABLED.", Brushes.Red); }
                if (dailyRealizedPnL >= maxDailyProfitDollars && !dailyProfitHit)
                { dailyProfitHit = true; ExecuteFlatten(); Print(TAG + "DAILY PROFIT TARGET " + dailyRealizedPnL.ToString("C0") + " — closing position. Strategy stays ENABLED. Press RESET or wait for 18:00 ET session rollover."); UpdateDashboardStatus("💰 DAILY PROFIT TARGET " + dailyRealizedPnL.ToString("C0") + " — position closed. Strategy STILL ENABLED.", Brushes.Gold); }
            }
            catch (Exception ex) { Print(TAG + "OnExecutionUpdate EX: " + ex.Message); }
        }

        // ----- Order-flow tape: live only -----
        protected override void OnMarketData(MarketDataEventArgs e)
        {
            if (!orderFlowFilterEnabled) return;
            if (State != State.Realtime) return;
            if (e.MarketDataType != MarketDataType.Last) return;
            try
            {
                if (tapeWindowStart == DateTime.MinValue || (DateTime.Now - tapeWindowStart).TotalSeconds > 30)
                {
                    tapeWindowStart = DateTime.Now;
                    tapeBidVol = tapeAskVol = 0;
                }
                double trade = e.Price;
                if (trade >= e.Ask) tapeAskVol += e.Volume;            // aggressor buys
                else if (trade <= e.Bid) tapeBidVol += e.Volume;        // aggressor sells
                double tot = tapeBidVol + tapeAskVol;
                if (tot > 0) cachedTapeDelta = (tapeAskVol - tapeBidVol) / tot;
            }
            catch { }
        }
        #endregion

        // ===========================================================
        //  SIGNAL CALCULATION + 12 FILTERS
        // ===========================================================
        #region Signals
        private void CalculateSignals()
        {
            if (CurrentBar < emaPeriodSlow + 5) return;
            int currDom = lastBullConfidence > lastBearConfidence ? 1
                        : lastBearConfidence > lastBullConfidence ? -1 : 0;

            lastBullConfidence = 0; lastBearConfidence = 0;

            if (autoStrategy == 2)
            {
                double[] bull = new double[2], bear = new double[2];
                for (int s = 0; s < 2; s++)
                {
                    lastBullConfidence = 0; lastBearConfidence = 0;
                    if (s == 0) CalcMomentumVwap();
                    else CalcKeyLevelBreakout();
                    ApplySmartFilters();
                    bull[s] = lastBullConfidence; bear[s] = lastBearConfidence;
                }
                int bestIdx = 0; double bestTot = 0;
                for (int s = 0; s < 2; s++)
                {
                    double t = Math.Max(bull[s], bear[s]);
                    if (t > bestTot) { bestTot = t; bestIdx = s; }
                    strategyScores[s] = t;
                }
                bestAutoStrategy = bestIdx;
                lastBullConfidence = bull[bestIdx];
                lastBearConfidence = bear[bestIdx];
                rawBullConfidence  = lastBullConfidence;
                rawBearConfidence  = lastBearConfidence;
            }
            else
            {
                if (autoStrategy == 0) CalcMomentumVwap();
                else CalcKeyLevelBreakout();
                rawBullConfidence = lastBullConfidence;
                rawBearConfidence = lastBearConfidence;
                ApplySmartFilters();
            }

            int newDom = lastBullConfidence > lastBearConfidence ? 1
                       : lastBearConfidence > lastBullConfidence ? -1 : 0;
            confidenceFlip = (prevDominantDir != 0 && newDom != 0 && newDom != prevDominantDir);
            prevDominantDir = newDom;

            if (enableDiagLog) WriteDiagRow("SIGNAL");
        }

        // Strategy 0: Momentum + VWAP
        private void CalcMomentumVwap()
        {
            if (CurrentBar < emaPeriodSlow + 5) return;
            int ct = ToTime(Time[0]);
            if (ct >= 93000 && ct < 94500) return;     // skip first 15 min
            double emaF = indEmaFast[0], emaS = indEmaSlow[0], rsi = indRsi[0];
            bool emaFastUp   = CurrentBar > 1 && indEmaFast[0] > indEmaFast[1];
            bool emaFastDown = CurrentBar > 1 && indEmaFast[0] < indEmaFast[1];
            if (emaF > emaS && emaFastUp)   lastBullConfidence += 30;
            if (emaF < emaS && emaFastDown) lastBearConfidence += 30;
            bool vwMature = ct >= 94500;
            if (CrossAbove(Close, vwapValue, 1)) lastBullConfidence += 35;
            else if (Close[0] > vwapValue && vwMature) lastBullConfidence += 20;
            if (CrossBelow(Close, vwapValue, 1)) lastBearConfidence += 35;
            else if (Close[0] < vwapValue && vwMature) lastBearConfidence += 20;
            if (rsi > 55 && CurrentBar > 2 && indRsi[0] > indRsi[2]) lastBullConfidence += 20;
            if (rsi < 45 && CurrentBar > 2 && indRsi[0] < indRsi[2]) lastBearConfidence += 20;
            if (Close[0] > High[1]) lastBullConfidence += 15;
            if (Close[0] < Low[1]) lastBearConfidence += 15;
            if (emaF < emaS) lastBullConfidence = Math.Max(0, lastBullConfidence - 20);
            if (emaF > emaS) lastBearConfidence = Math.Max(0, lastBearConfidence - 20);
        }

        // Strategy 1: Key Level Breakout
        private void CalcKeyLevelBreakout()
        {
            if (CurrentBar < 25) return;
            int ct = ToTime(Time[0]);
            if ((ct >= 93000 && ct < 93500) || (ct >= 120000 && ct < 133000)) return;
            double hi20 = MAX(High, 20)[1];
            double lo20 = MIN(Low, 20)[1];
            double atr = indAtr[0];
            if (Close[0] > hi20 && CrossAbove(Close, hi20, 1) && Close[0] > Open[0]) lastBullConfidence += 50;
            else if (CrossAbove(Close, hi20, 1)) lastBullConfidence += 25;
            else if (Close[0] > hi20) lastBullConfidence += 25;
            if (atr > 0 && (Close[0] - hi20) > 0.15 * atr) lastBullConfidence += 30;
            if (indEmaFast[0] > indEmaSlow[0]) lastBullConfidence += 20;
            if (Close[0] < lo20 && CrossBelow(Close, lo20, 1) && Close[0] < Open[0]) lastBearConfidence += 50;
            else if (CrossBelow(Close, lo20, 1)) lastBearConfidence += 25;
            else if (Close[0] < lo20) lastBearConfidence += 25;
            if (atr > 0 && (lo20 - Close[0]) > 0.15 * atr) lastBearConfidence += 30;
            if (indEmaFast[0] < indEmaSlow[0]) lastBearConfidence += 20;
        }

        // ----- 12 filters -----
        private void ApplySmartFilters()
        {
            if (CurrentBar < emaPeriodSlow + 5) return;
            double atr = indAtr[0];
            double rawB = lastBullConfidence, rawS = lastBearConfidence;

            // F1 Volume Confirmation (uses prior-bar volume)
            double avgVol = GetCachedAvgVolume();
            double vr = (avgVol > 0 && CurrentBar > 1) ? Volume[1] / avgVol : 1;
            if (vr < 0.5) { lastBullConfidence *= 0.7; lastBearConfidence *= 0.7; }
            else if (vr > 2.0) { lastBullConfidence *= 1.15; lastBearConfidence *= 1.15; }

            // F2 Bull/Bear Conflict
            double conflict = Math.Min(lastBullConfidence, lastBearConfidence);
            double dominant = Math.Max(lastBullConfidence, lastBearConfidence);
            if (dominant > 0 && conflict / dominant > 0.80)
            { lastBullConfidence *= 0.7; lastBearConfidence *= 0.7; }

            // F3 HTF Bias composite
            if (htfBias > 0)       { lastBearConfidence *= 0.50; lastBullConfidence *= 1.10; }
            else if (htfBias < 0)  { lastBullConfidence *= 0.50; lastBearConfidence *= 1.10; }
            else if (sessionConfirmedBias != 0)
            {
                if (sessionConfirmedBias > 0) { lastBearConfidence *= 0.65; lastBullConfidence *= 1.05; }
                else                          { lastBullConfidence *= 0.65; lastBearConfidence *= 1.05; }
            }

            // F4 VWAP slope
            if (vwapValue > 0 && prevBarVwap > 0 && atr > 0)
            {
                double slope = vwapValue - prevBarVwap;
                double thr = atr * 0.05;
                if (slope > thr) { lastBullConfidence += 10; lastBearConfidence -= 5; }
                else if (slope < -thr) { lastBearConfidence += 10; lastBullConfidence -= 5; }
            }

            // F5 5-bar slope direction
            if (CurrentBar >= 6 && atr > 0)
            {
                double slope5 = (Close[0] - Close[5]) / atr;
                if (slope5 < -0.4) lastBullConfidence *= Math.Max(0.5, 1.0 - (Math.Abs(slope5) - 0.4) * 0.3);
                else if (slope5 > 0.4) lastBearConfidence *= Math.Max(0.5, 1.0 - (slope5 - 0.4) * 0.3);
            }

            // F6 EMA cross structure (always-on penalty)
            if (indEmaFast[0] < indEmaSlow[0]) lastBullConfidence *= 0.65;
            if (indEmaFast[0] > indEmaSlow[0]) lastBearConfidence *= 0.65;

            // F7 RSI/Price divergence
            if (CurrentBar >= 5 && atr > 0)
            {
                double r0 = indRsi[0], r4 = indRsi[4];
                if (r0 < r4 - 5 && Close[0] > Close[4]) lastBearConfidence += 10;
                if (r0 > r4 + 5 && Close[0] < Close[4]) lastBullConfidence += 10;
            }

            // Penalty floor — never < 50% of raw
            lastBullConfidence = Math.Max(lastBullConfidence, rawB * 0.50);
            lastBearConfidence = Math.Max(lastBearConfidence, rawS * 0.50);

            // F8 Confidence flip bonus
            if (confidenceFlip)
            {
                if (lastBullConfidence > lastBearConfidence) lastBullConfidence += 15;
                else if (lastBearConfidence > lastBullConfidence) lastBearConfidence += 15;
            }

            // F9 EMA cross momentum boost
            if (emaCrossBarsAgo <= 3)
            {
                double cb = emaCrossBarsAgo == 0 ? 25.0 : (emaCrossBarsAgo <= 1 ? 20.0 : 15.0);
                if (emaCrossDir == 1) lastBullConfidence += cb;
                if (emaCrossDir == -1) lastBearConfidence += cb;
            }

            // F10 Order-flow tape (live only — neutral elsewhere)
            if (orderFlowFilterEnabled)
            {
                double td = cachedTapeDelta;
                if (td > 0.25) { lastBullConfidence += 10; lastBearConfidence -= 5; }
                else if (td < -0.25) { lastBearConfidence += 10; lastBullConfidence -= 5; }
            }

            // F11 Liquidity void (handled at entry-time also; here we soften confidence)
            if (voidBarsRemaining > 0)
            { lastBullConfidence *= 0.7; lastBearConfidence *= 0.7; }

            // F12 Open-type alignment boost
            if (openTypeSet)
            {
                if (openType == 1) lastBullConfidence += 8;
                else if (openType == -1) lastBearConfidence += 8;
            }

            lastBullConfidence = Math.Max(0, Math.Min(120, lastBullConfidence));
            lastBearConfidence = Math.Max(0, Math.Min(120, lastBearConfidence));
        }

        private void UpdateHtfBias()
        {
            int votes = 0;
            if (indEma5mFast != null && indEma5mSlow != null
                && BarsArray.Length > 1 && BarsArray[1].Count > 25)
            {
                if (indEma5mFast[0] > indEma5mSlow[0]) votes++;
                else if (indEma5mFast[0] < indEma5mSlow[0]) votes--;
            }
            if (currDayOpen > 0 && indAtr != null && indAtr[0] > 0)
            {
                double d = Close[0] - currDayOpen;
                double th = indAtr[0] * 0.3;
                if (d > th) { sessionOpenBias = 1; votes++; }
                else if (d < -th) { sessionOpenBias = -1; votes--; }
                else sessionOpenBias = 0;
            }
            // Legacy 45-EMA
            if (indEmaHtf != null && CurrentBar >= htfEmaPeriod + 5 && indAtr != null && indAtr[0] > 0)
            {
                double dist = (Close[0] - indEmaHtf[0]) / indAtr[0];
                if (dist > 1.5) votes++;
                else if (dist < -1.5) votes--;
            }
            htfBias = votes >= 2 ? 1 : (votes <= -2 ? -1 : 0);
            if (htfBias < 0) { htfBiasConsecBear++; htfBiasConsecBull = 0; }
            else if (htfBias > 0) { htfBiasConsecBull++; htfBiasConsecBear = 0; }
            else
            {
                if (htfBiasConsecBear > 0) htfBiasConsecBear--;
                if (htfBiasConsecBull > 0) htfBiasConsecBull--;
            }
            if (htfBiasConsecBear >= 6) sessionConfirmedBias = -1;
            else if (htfBiasConsecBull >= 6) sessionConfirmedBias = 1;
            else if (sessionConfirmedBias == -1 && htfBiasConsecBull >= 12) sessionConfirmedBias = 0;
            else if (sessionConfirmedBias == 1  && htfBiasConsecBear >= 12) sessionConfirmedBias = 0;
        }

        private void TrackEmaCross()
        {
            if (CurrentBar < 2 || indEmaFast == null || indEmaSlow == null) return;
            bool wasBull = indEmaFast[1] >= indEmaSlow[1];
            bool nowBull = indEmaFast[0] >= indEmaSlow[0];
            if (nowBull && !wasBull) { emaCrossDir = 1; emaCrossBarsAgo = 0; }
            else if (!nowBull && wasBull) { emaCrossDir = -1; emaCrossBarsAgo = 0; }
            else emaCrossBarsAgo++;
        }

        // Open-type classification (first 30 min RTH)
        private void UpdateOpenTypeClassification()
        {
            int ct = ToTime(Time[0]);
            if (ct < 93000 || ct >= 100000 || openTypeSet) return;
            // After first 30 min, classify
            if (ct >= 95900 && currDayOpen > 0 && indAtr != null && indAtr[0] > 0)
            {
                double moveFromOpen = Close[0] - currDayOpen;
                double atr30 = indAtr[0];
                if (moveFromOpen > atr30 * 1.5) openType = 1;        // Open-Drive bull
                else if (moveFromOpen < -atr30 * 1.5) openType = -1; // Open-Drive bear
                else openType = 0;
                openTypeSet = true;
            }
        }

        // Liquidity void: 3 consecutive bars range > 2xATR with widening volume
        private void UpdateLiquidityVoid()
        {
            if (voidBarsRemaining > 0) voidBarsRemaining--;
            if (CurrentBar < 4 || indAtr == null || indAtr[0] <= 0) return;
            double atr = indAtr[0];
            int hits = 0;
            for (int i = 0; i < 3; i++) if ((High[i] - Low[i]) > 2.0 * atr) hits++;
            if (hits >= 3) voidBarsRemaining = 2;
        }
        #endregion

        // ===========================================================
        //  MANUAL TRADE-SIGNAL INDICATOR
        // ===========================================================
        #region Manual signal
        // Combines confidence + HTF + tape + EMA + flip into a single
        // explicit BUY / SELL / WAIT recommendation. Driven once per bar.
        private void UpdateManualSignal()
        {
            int level = 0;
            string r = "";
            double dom = Math.Max(lastBullConfidence, lastBearConfidence);
            int dir = lastBullConfidence > lastBearConfidence ? 1 : (lastBearConfidence > lastBullConfidence ? -1 : 0);
            bool meetsConf = dom >= minSignalConfidence;
            bool htfOk = (dir == 1 && htfBias >= 0) || (dir == -1 && htfBias <= 0);
            bool emaOk = (dir == 1 && indEmaFast[0] > indEmaSlow[0]) || (dir == -1 && indEmaFast[0] < indEmaSlow[0]);
            bool tapeOk = !orderFlowFilterEnabled || (dir == 1 && cachedTapeDelta >= -0.1)
                                                 || (dir == -1 && cachedTapeDelta <= 0.1);
            bool noVoid = voidBarsRemaining == 0;
            bool freshCross = emaCrossBarsAgo <= 3 && emaCrossDir == dir;

            if (dir != 0 && meetsConf && htfOk && emaOk && tapeOk && noVoid)
            {
                int conflu = (htfOk?1:0) + (emaOk?1:0) + (tapeOk?1:0) + (freshCross?1:0) + (confidenceFlip && prevDominantDir == dir ? 1 : 0);
                level = (dom >= minSignalConfidence + 15 && conflu >= 4) ? (dir * 2) : dir;
                r = (level == 2 ? "STRONG BUY" : level == -2 ? "STRONG SELL" : level == 1 ? "BUY" : "SELL")
                    + " conf=" + dom.ToString("F0") + " htf=" + htfBias + " tape=" + cachedTapeDelta.ToString("F2");
            }
            else
            {
                level = 0;
                r = "WAIT — ";
                if (!meetsConf) r += "low conf ";
                if (!htfOk) r += "htf-against ";
                if (!emaOk) r += "ema-against ";
                if (!tapeOk) r += "tape-against ";
                if (!noVoid) r += "liq-void ";
            }
            manualSignalLevel = level;
            manualSignalReason = r;
        }
        #endregion

        // ===========================================================
        //  VWAP / VOLUME PROFILE
        // ===========================================================
        #region VWAP & VP
        private void ResetVwap()
        {
            vwapCumTPV = vwapCumVol = vwapValue = 0;
            prevBarVwap = currBarTPV = currBarVol = 0;
        }
        private void UpdateVwap()
        {
            double tp = (High[0] + Low[0] + Close[0]) / 3.0;
            double v  = Volume[0];
            if (IsFirstTickOfBar && CurrentBar > 0)
            {
                vwapCumTPV += currBarTPV; vwapCumVol += currBarVol;
                currBarTPV = tp * v; currBarVol = v;
            }
            else
            {
                currBarTPV = tp * v; currBarVol = v;
            }
            double tt = vwapCumTPV + currBarTPV;
            double tv = vwapCumVol + currBarVol;
            prevBarVwap = vwapValue;
            vwapValue = tv > 0 ? tt / tv : Close[0];
        }
        private void UpdateVolumeProfile()
        {
            if (volumeAtPrice == null) return;
            double tp = Math.Round(((High[0] + Low[0] + Close[0]) / 3.0) / TickSize) * TickSize;
            double v = Volume[0];
            if (volumeAtPrice.ContainsKey(tp)) volumeAtPrice[tp] += v;
            else volumeAtPrice[tp] = v;
        }
        private void RecalcVolumeProfileLevels()
        {
            if (volumeAtPrice == null || volumeAtPrice.Count == 0) return;
            double maxVol = 0; pocLevel = 0;
            foreach (var kv in volumeAtPrice) if (kv.Value > maxVol) { maxVol = kv.Value; pocLevel = kv.Key; }
            double total = 0;
            foreach (var kv in volumeAtPrice) total += kv.Value;
            double target = total * 0.70;
            var sorted = new List<double>(volumeAtPrice.Keys);
            int pi = sorted.BinarySearch(pocLevel);
            if (pi < 0) { vahLevel = pocLevel; valLevel = pocLevel; return; }
            double area = volumeAtPrice[pocLevel];
            int lo = pi, hi = pi;
            while (area < target && (lo > 0 || hi < sorted.Count - 1))
            {
                double vb = lo > 0 ? volumeAtPrice[sorted[lo - 1]] : 0;
                double va = hi < sorted.Count - 1 ? volumeAtPrice[sorted[hi + 1]] : 0;
                if (va >= vb && hi < sorted.Count - 1) { hi++; area += volumeAtPrice[sorted[hi]]; }
                else if (lo > 0) { lo--; area += volumeAtPrice[sorted[lo]]; }
                else { hi++; area += volumeAtPrice[sorted[hi]]; }
            }
            valLevel = sorted[lo]; vahLevel = sorted[hi];
        }
        private double GetCachedAvgVolume()
        {
            if (cachedAvgVolumeBar == CurrentBar) return cachedAvgVolume;
            cachedAvgVolumeBar = CurrentBar;
            double sum = 0; int lb = Math.Min(20, CurrentBar);
            for (int i = 1; i <= lb; i++) sum += Volume[i];
            cachedAvgVolume = lb > 0 ? sum / lb : 1;
            return cachedAvgVolume;
        }
        #endregion

        // ===========================================================
        //  DAILY RESET / CHART
        // ===========================================================
        #region Daily reset & chart
        private void ResetDailyTracking()
        {
            if (currDayHigh > 0)
            {
                prevDayHigh  = currDayHigh;
                prevDayLow   = currDayLow;
                prevDayClose = Close[1] > 0 ? Close[1] : Close[0];
                prevDayOpen  = currDayOpen;
                Print(TAG + "PrevDay H=" + prevDayHigh.ToString("F2") + " L=" + prevDayLow.ToString("F2"));
            }
            currDayHigh = High[0]; currDayLow = Low[0]; currDayOpen = Open[0];
            sessionDate = Time[0].Date;
            ResetSessionFlags();
            if (volumeAtPrice != null) volumeAtPrice.Clear();
            pocLevel = vahLevel = valLevel = 0;
            Print(TAG + "Session reset " + sessionDate.ToShortDateString());
        }

        // Single-source-of-truth redraw helper. Wraps DrawChartAnnotations in try/catch
        // because it's called from multiple threads (OnOrderUpdate, OnBarUpdate, button handlers,
        // ProcessPendingButtons). Draw.* is safe to invoke cross-thread in NT8 8.1+, but Close[0]
        // and indicator series can throw if accessed before first bar — defend that.
        private void RedrawAnnotationsSafe()
        {
            if (CurrentBar < 1) return;
            try { DrawChartAnnotations(); }
            catch (Exception ex) { Print(TAG + "RedrawAnnotationsSafe EX: " + ex.Message); }
        }

        private void DrawChartAnnotations()
        {
            if (!stopsArmed || averageEntryPrice == 0) return;
            Draw.HorizontalLine(this, "hiddenSL", false, hiddenStopPrice, Brushes.OrangeRed, DashStyleHelper.DashDotDot, 2);
            Draw.HorizontalLine(this, "hiddenTP", false, hiddenTargetPrice, Brushes.LimeGreen, DashStyleHelper.DashDotDot, 2);
            Draw.HorizontalLine(this, "avgEntryLine", false, averageEntryPrice, Brushes.DodgerBlue, DashStyleHelper.Dot, 1);
            // ALWAYS compute label distances from the ACTUAL hidden line price (not the
            // entry-based slPoints/tpPoints settings), so prevDay clamp + manual SL nudges
            // and Jump SL show the truth on the chart.
            double tickPtX = TickSize * NQ_TICKS_PER_POINT;
            double slDistPts = openTradeDirection == 1
                ? (averageEntryPrice - hiddenStopPrice) / tickPtX
                : (hiddenStopPrice - averageEntryPrice) / tickPtX;
            double tpDistPts = openTradeDirection == 1
                ? (hiddenTargetPrice - averageEntryPrice) / tickPtX
                : (averageEntryPrice - hiddenTargetPrice) / tickPtX;
            int slTk = (int)Math.Round(Math.Abs(slDistPts) * NQ_TICKS_PER_POINT);
            int tpTk = (int)Math.Round(Math.Abs(tpDistPts) * NQ_TICKS_PER_POINT);
            double slDol = Math.Abs(slDistPts) * NQ_DOLLARS_PER_POINT * totalContracts;
            double tpDol = Math.Abs(tpDistPts) * NQ_DOLLARS_PER_POINT * totalContracts;
            string slSign = slDistPts < 0 ? "+" : ""; // negative = SL is in profit (after Jump SL)
            Draw.Text(this, "slLabel",
                "SL " + hiddenStopPrice.ToString("F2") + "  (" + slSign + slDistPts.ToString("F1") + "pt | " + slTk + "tk | " + slDol.ToString("C0") + ")",
                0, hiddenStopPrice + (openTradeDirection == 1 ? -2 * TickSize : 2 * TickSize), Brushes.OrangeRed);
            Draw.Text(this, "tpLabel",
                "TP " + hiddenTargetPrice.ToString("F2") + "  (" + tpDistPts.ToString("F1") + "pt | " + tpTk + "tk | " + tpDol.ToString("C0") + (tpClampedByPrevDay ? " — PD" : "") + ")",
                0, hiddenTargetPrice + (openTradeDirection == 1 ? 2 * TickSize : -2 * TickSize), Brushes.LimeGreen);
            if (trailEnabled && trailActive && trailPrice > 0)
            {
                Draw.HorizontalLine(this, "adaptiveTrail", false, trailPrice, Brushes.Magenta, DashStyleHelper.DashDot, 2);
                double td = openTradeDirection == 1
                    ? (Close[0] - trailPrice) / (TickSize * NQ_TICKS_PER_POINT)
                    : (trailPrice - Close[0]) / (TickSize * NQ_TICKS_PER_POINT);
                Draw.Text(this, "trailLabel",
                    "Trail " + trailPrice.ToString("F2") + "  (" + td.ToString("F1") + "pt " + trailTierName + ")",
                    0, trailPrice + (openTradeDirection == 1 ? -6 * TickSize : 6 * TickSize), Brushes.Magenta);
            }
            else
            {
                RemoveDrawObject("adaptiveTrail"); RemoveDrawObject("trailLabel");
            }
        }

        private void DrawSessionLevels()
        {
            if (showVwap && vwapValue > 0)
                Draw.HorizontalLine(this, "vwapLine", false, vwapValue, Brushes.Yellow, DashStyleHelper.Solid, 1);
            if (prevDayHigh > 0) Draw.HorizontalLine(this, "pdH", false, prevDayHigh, Brushes.Cyan,    DashStyleHelper.Dash, 1);
            if (prevDayLow  > 0) Draw.HorizontalLine(this, "pdL", false, prevDayLow,  Brushes.Cyan,    DashStyleHelper.Dash, 1);
            if (pocLevel    > 0) Draw.HorizontalLine(this, "vpPOC", false, pocLevel, Brushes.Gold,     DashStyleHelper.Solid, 2);
            if (vahLevel    > 0) Draw.HorizontalLine(this, "vpVAH", false, vahLevel, Brushes.DodgerBlue, DashStyleHelper.Dash, 1);
            if (valLevel    > 0) Draw.HorizontalLine(this, "vpVAL", false, valLevel, Brushes.DodgerBlue, DashStyleHelper.Dash, 1);
        }

        // -----------------------------------------------------------
        //  v6 Phase 0.2 — Renko brick processor (BarsArray[2], 64-tick / 16-offset).
        //  Data plumbing only. Updates lastBrickColor / brickStreakCount + emits BRICK_CLOSE
        //  diag rows when enabled. NO entry/exit logic reads these yet — that's W6 Phase 2.1.
        //  Called from OnBarUpdate when BarsInProgress == 2.
        //  v6 0.2.2: dedupe via lastProcessedRenkoBar instead of IsFirstTickOfBar (which
        //  can be unreliable on secondary series under Calculate.OnBarClose).
        // -----------------------------------------------------------
        private void ProcessRenkoBar()
        {
            if (CurrentBars[2] < 1) return;
            int b = CurrentBars[2];
            if (b == lastProcessedRenkoBar) return;   // already processed this brick index
            lastProcessedRenkoBar = b;
            renkoBarsSeen++;
            if (renkoBarsSeen == 1)
                Print(TAG + "Stock Renko series ALIVE — first brick seen, size=" + renkoBrickSize + "tk off=" + renkoBrickOffset + "tk");
            int idx = CurrentBars[2] >= 2 ? 1 : 0;
            double bOpen  = Opens[2][idx];
            double bClose = Closes[2][idx];
            double bHigh  = Highs[2][idx];
            double bLow   = Lows[2][idx];
            string color  = bClose > bOpen ? "G" : (bClose < bOpen ? "R" : (lastBrickColor ?? ""));
            if (color == lastBrickColor && color != "") brickStreakCount++;
            else { brickStreakCount = 1; lastBrickColor = color; }
            lastBrickHigh  = bHigh;
            lastBrickLow   = bLow;
            lastBrickClose = bClose;
            if (enableDiagLog)
                WriteDiagRow("BRICK_CLOSE",
                    "src=stock color=" + color + " streak=" + brickStreakCount
                    + " o=" + bOpen.ToString("F2") + " c=" + bClose.ToString("F2")
                    + " hi=" + bHigh.ToString("F2") + " lo=" + bLow.ToString("F2")
                    + " size=" + renkoBrickSize + "tk off=" + renkoBrickOffset + "tk");
        }

        // -----------------------------------------------------------
        //  v6 0.2.4 — Treat primary BarsArray[0] AS NinzaRenko bricks.
        //  Used when the chart's primary bar type IS NinzaRenko (typical user setup).
        //  Bypasses the AddDataSeries/Custom-slot dance — each primary bar = one brick.
        // -----------------------------------------------------------
        private void ProcessPrimaryAsNinzaRenkoBar()
        {
            if (CurrentBar == lastProcessedNrBar) return;
            lastProcessedNrBar = CurrentBar;
            nrBarsSeen++;
            if (nrBarsSeen == 1)
                Print(TAG + "NinzaRenko (primary) ALIVE — reading bricks from BarsArray[0]. First bar O=" + Open[0].ToString("F2") + " C=" + Close[0].ToString("F2"));
            // Use last fully-closed bar (index 1) when available; else current.
            int idx = CurrentBar >= 1 ? 1 : 0;
            double bOpen  = Open[idx];
            double bClose = Close[idx];
            double bHigh  = High[idx];
            double bLow   = Low[idx];
            string color  = bClose > bOpen ? "G" : (bClose < bOpen ? "R" : (lastNrBrickColor ?? ""));
            string prevColor = lastNrBrickColor ?? "";
            if (color == lastNrBrickColor && color != "") nrBrickStreakCount++;
            else { nrBrickStreakCount = 1; lastNrBrickColor = color; }
            // v6 2.3 — cache PRIOR closed brick extremes BEFORE overwriting with the just-closed brick.
            // Brick-trail uses these so a new same-color brick ratchets stop one brick behind, while
            // a fresh opposite-color brick will pierce the trail at its own extreme = clean exit.
            prevNrBrickHigh = lastNrBrickHigh;
            prevNrBrickLow  = lastNrBrickLow;
            lastNrBrickHigh  = bHigh;
            lastNrBrickLow   = bLow;
            lastNrBrickClose = bClose;
            // v6 2.6 — Brick-Trail v2 streak-extreme tracking (body, not wick) + brick-flip exit signal.
            if (nrBrickStreakCount == 1)
            {
                streakMinBodyHigh = double.MaxValue;
                streakMaxBodyLow  = double.MinValue;
                // First brick of a NEW streak is OPPOSITE color of the prior streak.
                // If we're in a position and the new brick closed against us, fire the brick-flip exit.
                if (openTradeDirection != 0 && enableBrickTrail
                    && ((openTradeDirection == -1 && color == "G") || (openTradeDirection == 1 && color == "R")))
                {
                    pendingBrickFlipExit = true;
                }
            }
            double bodyHighPx = Math.Max(bOpen, bClose);
            double bodyLowPx  = Math.Min(bOpen, bClose);
            if (bodyHighPx < streakMinBodyHigh) streakMinBodyHigh = bodyHighPx;
            if (bodyLowPx  > streakMaxBodyLow)  streakMaxBodyLow  = bodyLowPx;
            // v6 2.6.1 - timestamp every same-color brick close for the giveback STALL-GATE.
            // While we hold a position, only "trade-direction-agreeing" closes refresh this stamp -
            // so a long pause without same-direction bricks indicates a real stall.
            if (openTradeDirection != 0
                && ((openTradeDirection == -1 && color == "R") || (openTradeDirection == 1 && color == "G")))
            {
                lastSameColorBrickTime = Time[0];
            }
            // v6 2.6.2 - PROFIT-LOCK at brick close (wick-immune, catches violent V-reversals).
            // Once peak profit reached >= MinPeakPts AND profit-at-this-brick-close has retraced
            // past the giveback threshold, mark for exit. Uses the brick CLOSE price (not wick) so
            // MM intra-brick spikes can't trigger it. Catches the case where a single big reversal
            // brick wipes out most of the run BEFORE brick-flip exit fires (e.g. PB8 trade #1).
            // v6 2.6.3 - SAME-COLOR GUARD: never fire on a continuation brick (same color as our
            // position) - peak is tick-based but profitAtClose is body-based, mismatch was killing
            // monster runs (PB9 trade #2 exited at brick 11 R during a 22-brick down-run).
            bool isOppositeBrick = (openTradeDirection == -1 && color == "G")
                                || (openTradeDirection ==  1 && color == "R");
            if (openTradeDirection != 0 && enableBrickTrail && trailTierName == "BrickTrail"
                && brickTrailProfitLockGivebackPct > 0
                && isOppositeBrick
                && trailMaxProfitPts >= brickTrailProfitLockMinPeakPts)
            {
                double tickPtPL = TickSize * NQ_TICKS_PER_POINT;
                double profitAtBrickClose = openTradeDirection == 1
                    ? (bClose - averageEntryPrice) / tickPtPL
                    : (averageEntryPrice - bClose) / tickPtPL;
                double floorPts = trailMaxProfitPts * (1.0 - brickTrailProfitLockGivebackPct / 100.0);
                if (profitAtBrickClose < floorPts)
                {
                    pendingBrickFlipExit = true; // reuse the existing exit pipeline
                    if (enableDiagLog)
                        WriteDiagRow("BRICK_PROFIT_LOCK",
                            "peak=" + trailMaxProfitPts.ToString("F1") + "pt floor=" + floorPts.ToString("F1")
                            + "pt brickClose=" + bClose.ToString("F2")
                            + " profitAtClose=" + profitAtBrickClose.ToString("F1") + "pt");
                }
            }
            ninzaSeriesAdded = true;  // mark NR active so dashboard shows color, not "off"
            if (enableDiagLog)
                WriteDiagRow("BRICK_CLOSE",
                    "src=primary color=" + color + " streak=" + nrBrickStreakCount
                    + " o=" + bOpen.ToString("F2") + " c=" + bClose.ToString("F2")
                    + " hi=" + bHigh.ToString("F2") + " lo=" + bLow.ToString("F2"));
            // v6 1.2-1.4 brick analytics hook (label-only)
            if (enableBrickAnalytics)
                UpdateBrickAnalytics(color, prevColor, bOpen, bClose, bHigh, bLow);
        }

        // -----------------------------------------------------------
        //  Same logic as stock Renko handler but reads BarsArray[NINZA_BIP] and updates
        //  the lastNrBrickColor / nrBrickStreakCount fields. This is the PRIMARY brick
        //  signal (matches what user visually trades off of).
        // -----------------------------------------------------------
        private void ProcessNinzaRenkoBar()
        {
            if (BarsArray == null || BarsArray.Length <= NINZA_BIP) return;
            if (CurrentBars[NINZA_BIP] < 1) return;
            int b = CurrentBars[NINZA_BIP];
            if (b == lastProcessedNrBar) return;
            lastProcessedNrBar = b;
            nrBarsSeen++;
            if (nrBarsSeen == 1)
                Print(TAG + "NinzaRenko series ALIVE (BarsPeriodType int=" + ninzaCustomSlot + ") — first brick seen, size=" + renkoBrickSize + "tk trend=" + renkoBrickOffset + "tk");
            int idx = CurrentBars[NINZA_BIP] >= 2 ? 1 : 0;
            double bOpen  = Opens[NINZA_BIP][idx];
            double bClose = Closes[NINZA_BIP][idx];
            double bHigh  = Highs[NINZA_BIP][idx];
            double bLow   = Lows[NINZA_BIP][idx];
            string color  = bClose > bOpen ? "G" : (bClose < bOpen ? "R" : (lastNrBrickColor ?? ""));
            if (color == lastNrBrickColor && color != "") nrBrickStreakCount++;
            else { nrBrickStreakCount = 1; lastNrBrickColor = color; }
            lastNrBrickHigh  = bHigh;
            lastNrBrickLow   = bLow;
            lastNrBrickClose = bClose;
            if (enableDiagLog)
                WriteDiagRow("BRICK_CLOSE",
                    "src=ninza color=" + color + " streak=" + nrBrickStreakCount
                    + " o=" + bOpen.ToString("F2") + " c=" + bClose.ToString("F2")
                    + " hi=" + bHigh.ToString("F2") + " lo=" + bLow.ToString("F2")
                    + " size=" + renkoBrickSize + "tk trend=" + renkoBrickOffset + "tk");
        }

        // -----------------------------------------------------------
        //  v6 1.1 — F1 Regime Classifier (label-only, default OFF).
        //  Combines NinzaRenko brick streak + ADX/ADX-slope + ATR + EMA stack +
        //  distance-to-VWAP-in-ATRs into one of:
        //     TREND_UP / TREND_DN / CHOP / SQUEEZE / UNKNOWN
        //  Pure observation. Result lives in currentRegime, surfaces in diag log Regime
        //  column and (Phase 1.2) the dashboard "Regime:" row. NO trade logic reads it yet.
        //  Designed to be cheap (a few comparisons + a 4-bar brick scan) so safe every bar.
        //  Goal of this classifier: identify the moments MM bots/algos are most active
        //  (CHOP = stop-hunt zone, SQUEEZE = compression-then-breakout) and the moments
        //  they're trapped (TREND_UP/DN with strong streak) so downstream phases can
        //  weight signals to BEAT the MM rather than fight it.
        // -----------------------------------------------------------
        private void UpdateRegimeClassifier()
        {
            if (CurrentBar < 25 || indAdx == null || indAtr == null) return;
            double adx     = indAdx[0];
            double adxPrev = CurrentBar >= 5 ? indAdx[5] : adx;
            double adxSlope = adx - adxPrev;
            lastAdxSlope = adxSlope;   // v6 2.1 cache for entry-guard bypass logic
            double atr     = indAtr[0];
            if (atr <= 0) return;
            // ATR baseline (20-bar avg) for SQUEEZE detection.
            double atrAvg = 0; int atrCnt = 0;
            for (int i = 0; i < 20 && i < CurrentBar; i++) { atrAvg += indAtr[i]; atrCnt++; }
            if (atrCnt > 0) atrAvg /= atrCnt; else atrAvg = atr;
            // EMA stack proxy.
            int eStack = 0;
            if (indEmaFast != null && indEmaSlow != null)
            {
                double ef = indEmaFast[0], es = indEmaSlow[0], px = Close[0];
                if (px > ef && ef > es) eStack = 1;
                else if (px < ef && ef < es) eStack = -1;
            }
            // Distance from VWAP in ATR multiples.
            double distVwapAtr = 0;
            if (vwapValue > 0) distVwapAtr = (Close[0] - vwapValue) / atr;
            // NinzaRenko brick info — streak (consecutive same-color) + recent flip count.
            string nrColor = lastNrBrickColor ?? "";
            int    nrStreak = nrBrickStreakCount;
            // Approx "flip count in last 4 bricks" using primary bars (NinzaRenko=primary).
            // Not an exact brick history; good enough as a chop tell.
            int recentFlips = 0;
            for (int i = 0; i < 4 && i + 1 < CurrentBar; i++)
            {
                int s0 = Math.Sign(Close[i]   - Open[i]);
                int s1 = Math.Sign(Close[i+1] - Open[i+1]);
                if (s0 != 0 && s1 != 0 && s0 != s1) recentFlips++;
            }
            regimeFlipsLast20 = recentFlips;  // reused name; really last-4 here, cheap

            // ----- classification rules (v1, conservative) -----
            string r;
            if (adx >= 22 && adxSlope > 0 && eStack == 1
                && nrColor == "G" && nrStreak >= 3 && distVwapAtr >  0.5)
                r = "TREND_UP";
            else if (adx >= 22 && adxSlope > 0 && eStack == -1
                && nrColor == "R" && nrStreak >= 3 && distVwapAtr < -0.5)
                r = "TREND_DN";
            else if (adx < 15 && atr < 0.6 * atrAvg)
                r = "SQUEEZE";
            else if (adx < 22 && (recentFlips >= 2 || Math.Abs(distVwapAtr) < 0.5))
                r = "CHOP";
            else
                r = "UNKNOWN";

            if (r != currentRegime)
            {
                prevRegime = currentRegime;
                currentRegime = r;
                regimeChangedBar = CurrentBar;
                if (enableDiagLog)
                    WriteDiagRow("REGIME_CHANGE",
                        "from=" + prevRegime + " to=" + r
                        + " adx=" + adx.ToString("F1") + " slope=" + adxSlope.ToString("F2")
                        + " atr/avg=" + (atr/atrAvg).ToString("F2") + " eStack=" + eStack
                        + " distVwapAtr=" + distVwapAtr.ToString("F2")
                        + " nr=" + nrColor + "x" + nrStreak + " flips4=" + recentFlips);
            }
        }

        // -----------------------------------------------------------
        //  v6 1.2/1.3/1.4 — Brick Analytics: Run Tracker + Wick Analyzer + MM Pattern Recorder.
        //  Called from ProcessPrimaryAsNinzaRenkoBar() once per closed brick.
        //  100% observation — no entry/exit logic touches these fields yet. Designed to feed
        //  Phase 2.0 "Brick Mode" entry/exit logic. Goal: capture the moment a NEW brick run
        //  starts (best entry) AND the moment MM is trapping (slow bricks + alternating colors
        //  + small body + big wick = absorption / stop-hunt zone).
        //  All metrics emitted as RUN_END / WICK_TAG / MM_PATTERN diag rows.
        // -----------------------------------------------------------
        private void UpdateBrickAnalytics(string color, string prevColor, double bOpen, double bClose, double bHigh, double bLow)
        {
            // ---- 1.3 wick analyzer (per-brick) ----
            double bodyPts = Math.Abs(bClose - bOpen);
            double wickUp  = bHigh - Math.Max(bOpen, bClose);
            double wickDn  = Math.Min(bOpen, bClose) - bLow;
            lastBrickBodyPts   = bodyPts;
            lastBrickWickUpPts = wickUp;
            lastBrickWickDnPts = wickDn;
            lastBrickWickRatio = bodyPts > 0 ? (wickUp + wickDn) / bodyPts : 0;
            // Tag bricks with strong rejection wicks (potential reversal precursor).
            // Upper wick on a green brick = sellers capping; lower wick on red = buyers absorbing.
            bool absorption =
                (color == "G" && wickUp > bodyPts * 0.8 && wickUp >= 6)
             || (color == "R" && wickDn > bodyPts * 0.8 && wickDn >= 6);
            if (absorption && enableDiagLog)
                WriteDiagRow("WICK_TAG",
                    "absorption color=" + color
                    + " body=" + bodyPts.ToString("F1")
                    + " wickUp=" + wickUp.ToString("F1")
                    + " wickDn=" + wickDn.ToString("F1")
                    + " ratio=" + lastBrickWickRatio.ToString("F2"));

            // ---- brick speed (inter-arrival) ----
            DateTime now = Time[0];
            if (lastBrickTime != DateTime.MinValue)
                lastBrickIntervalSec = (now - lastBrickTime).TotalSeconds;
            lastBrickTime = now;
            if (lastBrickIntervalSec > 0)
            {
                brickIntervalsLast10.Enqueue(lastBrickIntervalSec);
                while (brickIntervalsLast10.Count > 10) brickIntervalsLast10.Dequeue();
                fastBricksLast10 = 0;
                foreach (var d in brickIntervalsLast10) if (d < 10) fastBricksLast10++;
            }

            // ---- 1.2 run tracker ----
            if (color != prevColor && prevColor != "")
            {
                // RUN_END: emit summary of run that just ended
                if (enableDiagLog && runCurrentLen > 0)
                {
                    double runPts = Math.Abs(bClose - runStartPrice);
                    double runDurSec = (now - runStartTime).TotalSeconds;
                    WriteDiagRow("RUN_END",
                        "color=" + runCurrentColor + " len=" + runCurrentLen
                        + " pts=" + runPts.ToString("F1")
                        + " maxFav=" + runMaxFavPts.ToString("F1")
                        + " durSec=" + runDurSec.ToString("F0")
                        + " startPx=" + runStartPrice.ToString("F2")
                        + " endPx=" + bClose.ToString("F2"));
                }
                // Update last-10 max (regime context: "are we in a high-streak environment?")
                if (runCurrentLen > 0)
                {
                    runLast10.Enqueue(runCurrentLen);
                    while (runLast10.Count > 10) runLast10.Dequeue();
                    runMaxLast10 = 0;
                    foreach (var v in runLast10) if (v > runMaxLast10) runMaxLast10 = v;
                }
                // Start new run
                runCurrentColor = color;
                runCurrentLen   = 1;
                runStartPrice   = bOpen;
                runStartTime    = now;
                runMaxFavPts    = 0;
            }
            else
            {
                runCurrentLen++;
                // Track best favorable excursion within run (long run: highest hi above start;
                // short run: lowest lo below start)
                double favPts = runCurrentColor == "G"
                    ? Math.Max(0, bHigh - runStartPrice)
                    : Math.Max(0, runStartPrice - bLow);
                if (favPts > runMaxFavPts) runMaxFavPts = favPts;
            }

            // ---- 1.4 MM pattern recorder ----
            brickColorRing.Enqueue(color);
            while (brickColorRing.Count > 20) brickColorRing.Dequeue();
            // Emit MM_PATTERN snapshot every 5 minutes
            if (enableDiagLog && (now - lastMmPatternEmit).TotalMinutes >= 5 && brickColorRing.Count >= 10)
            {
                lastMmPatternEmit = now;
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                int flips = 0; string prev = ""; int maxRunInRing = 0; int curRun = 0;
                foreach (var c in brickColorRing)
                {
                    sb.Append(c);
                    if (prev != "" && c != prev) flips++;
                    if (c == prev) curRun++; else curRun = 1;
                    if (curRun > maxRunInRing) maxRunInRing = curRun;
                    prev = c;
                }
                double avgInterval = 0; int n = 0;
                foreach (var d in brickIntervalsLast10) { avgInterval += d; n++; }
                if (n > 0) avgInterval /= n;
                WriteDiagRow("MM_PATTERN",
                    "ring=" + sb.ToString()
                    + " flips=" + flips
                    + " maxRun=" + maxRunInRing
                    + " avgIntvSec=" + avgInterval.ToString("F1")
                    + " fast10=" + fastBricksLast10
                    + " runMaxLast10=" + runMaxLast10);
            }
        }
        #endregion

        // ===========================================================
        //  UTILITIES
        // ===========================================================
        #region Utilities
        private double Clamp01(double v) { return Math.Max(0.0, Math.Min(1.0, v)); }
        private double LerpD(double a, double b, double t) { return a + (b - a) * t; }
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
        private string StrategyDisplay(int idx)
        {
            if (idx == 0) return "Momentum+VWAP";
            if (idx == 1) return "Key Lvl Breakout";
            return "Auto";
        }
        private string FormatTime(int hhmmss)
        {
            int h = hhmmss / 10000;
            int m = (hhmmss / 100) % 100;
            int ap = h >= 12 ? 1 : 0;
            int h12 = h > 12 ? h - 12 : (h == 0 ? 12 : h);
            return h12 + ":" + m.ToString("D2") + (ap == 1 ? " PM" : " AM");
        }
        #endregion

        // ===========================================================
        //  DASHBOARD — redesigned
        // ===========================================================
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

                    dashOuterBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.FromArgb(240, 18, 20, 28)),
                        CornerRadius = new CornerRadius(6),
                        BorderBrush = new SolidColorBrush(Color.FromRgb(55, 60, 75)),
                        BorderThickness = new Thickness(1),
                        Width = dashWidth
                    };
                    var border = dashOuterBorder;
                    var outer = new StackPanel();

                    // Title bar (drag)
                    dashTitleBar = new Border
                    {
                        Background = new SolidColorBrush(Color.FromRgb(30, 35, 48)),
                        CornerRadius = new CornerRadius(5, 5, 0, 0),
                        Padding = new Thickness(10, 6, 10, 6),
                        Cursor = Cursors.SizeAll
                    };
                    dashTitleBar.Child = new TextBlock
                    {
                        Text = "≡  NQ  Mm-ATM v6",
                        Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    dashTitleBar.MouseLeftButtonDown += (s, e) =>
                    { dashDragging = true; dashDragStart = e.GetPosition(ChartControl); dashTitleBar.CaptureMouse(); };
                    dashTitleBar.MouseLeftButtonUp += (s, e) =>
                    { dashDragging = false; dashTitleBar.ReleaseMouseCapture(); };
                    dashTitleBar.MouseMove += (s, e) =>
                    {
                        if (!dashDragging) return;
                        var p = e.GetPosition(ChartControl);
                        dashTranslate.X += (p.X - dashDragStart.X);
                        dashTranslate.Y += (p.Y - dashDragStart.Y);
                        dashDragStart = p;
                    };
                    outer.Children.Add(dashTitleBar);

                    var stack = new StackPanel { Margin = new Thickness(10, 6, 10, 8) };

                    // BIG SIGNAL
                    lblSignal = new TextBlock
                    {
                        Text = "WAIT", Foreground = Brushes.Gray,
                        FontSize = 22, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 4, 0, 0)
                    };
                    lblSignalReason = new TextBlock
                    {
                        Text = "—", Foreground = Brushes.Gray,
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    stack.Children.Add(lblSignal);
                    stack.Children.Add(lblSignalReason);
                    stack.Children.Add(MakeSep());

                    // Mode + Hours
                    var modeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
                    btnModeManual = MakeToggle("MANUAL", !autoMode, (s, e) => { autoMode = false; UpdateModeButtons(); if (enableDiagLog) WriteDiagRow("MODE_CHANGE", "MANUAL"); });
                    btnModeAuto   = MakeToggle("AUTO",    autoMode, (s, e) => { autoMode = true;  UpdateModeButtons(); if (enableDiagLog) WriteDiagRow("MODE_CHANGE", "AUTO"); });
                    btnHoursToggle = MakeToggle(tradingHoursEnabled ? "HRS ON" : "HRS OFF", tradingHoursEnabled,
                        (s, e) => { tradingHoursEnabled = !tradingHoursEnabled;
                                    btnHoursToggle.Content = tradingHoursEnabled ? "HRS ON" : "HRS OFF";
                                    btnHoursToggle.Background = tradingHoursEnabled ? Brushes.DarkSlateGray : Brushes.DarkRed;
                                    if (enableDiagLog) WriteDiagRow("TOGGLE_HOURS", tradingHoursEnabled ? "ON" : "OFF"); });
                    modeRow.Children.Add(btnModeManual);
                    modeRow.Children.Add(btnModeAuto);
                    modeRow.Children.Add(btnHoursToggle);
                    stack.Children.Add(modeRow);

                    // Strategy selector
                    var stratRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                    btnStratPrev = MakeSmallBtn("◄", (s, e) => { autoStrategy = (autoStrategy + 2) % 3; UpdateStratLabel(); });
                    lblStratName = new TextBlock { Text = StrategyDisplay(autoStrategy), Width = 150, TextAlignment = TextAlignment.Center,
                        Foreground = Brushes.White, FontSize = 11, FontWeight = FontWeights.SemiBold };
                    btnStratNext = MakeSmallBtn("►", (s, e) => { autoStrategy = (autoStrategy + 1) % 3; UpdateStratLabel(); });
                    stratRow.Children.Add(btnStratPrev);
                    stratRow.Children.Add(lblStratName);
                    stratRow.Children.Add(btnStratNext);
                    stack.Children.Add(stratRow);

                    lblActiveStrategy = MakeLabel("Active: —", Brushes.Cyan, 10, FontWeights.Normal, HorizontalAlignment.Center);
                    stack.Children.Add(lblActiveStrategy);

                    stack.Children.Add(MakeSep());

                    // Confidence
                    lblConfBull = MakeLabel("Bull: 0%", Brushes.LimeGreen, 12, FontWeights.SemiBold, HorizontalAlignment.Left);
                    lblConfBear = MakeLabel("Bear: 0%", Brushes.OrangeRed, 12, FontWeights.SemiBold, HorizontalAlignment.Left);
                    stack.Children.Add(lblConfBull);
                    stack.Children.Add(lblConfBear);
                    lblTapeDelta = MakeLabel("Tape Δ: —", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    stack.Children.Add(lblTapeDelta);

                    stack.Children.Add(MakeSep());

                    // Status
                    lblStatus = MakeLabel("Flat", Brushes.CornflowerBlue, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                    stack.Children.Add(lblStatus);
                    lblPosition = MakeLabel("Pos: 0/" + maxContracts, Brushes.White, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    stack.Children.Add(lblPosition);
                    lblHiddenSL = MakeLabel("SL: —", Brushes.OrangeRed, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    lblHiddenTP = MakeLabel("TP: —", Brushes.LimeGreen, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    lblTrailInfo = MakeLabel("Trail: —", Brushes.Magenta, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    lblTrapInfo  = MakeLabel("Trap: —", Brushes.Orange, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    stack.Children.Add(lblHiddenSL);
                    stack.Children.Add(lblHiddenTP);
                    stack.Children.Add(lblTrailInfo);
                    stack.Children.Add(lblTrapInfo);

                    stack.Children.Add(MakeSep());

                    // PnL
                    lblUnrealized = MakeLabel("Unrealized: $0", Brushes.LimeGreen, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                    lblPnL        = MakeLabel("Daily P&L: $0", Brushes.LimeGreen, 11, FontWeights.SemiBold, HorizontalAlignment.Left);
                    lblAccountPnL = MakeLabel("Account P&L: —", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    lblAccountBal = MakeLabel("Balance: —", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    stack.Children.Add(lblUnrealized);
                    stack.Children.Add(lblPnL);
                    stack.Children.Add(lblAccountPnL);
                    stack.Children.Add(lblAccountBal);

                    stack.Children.Add(MakeSep());

                    // VWAP / hours
                    lblVwapVal    = MakeLabel("VWAP: —", Brushes.Yellow, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    lblTradeHours = MakeLabel("Hours: —", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    // v6 0.2.1: read-only Renko brick info (data plumbing from Phase 0.2 already maintains the values).
                    // Hidden when Renko series is disabled. Color matches brick color so you can eyeball confluence.
                    lblBrickInfo  = MakeLabel("Brick: —", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    stack.Children.Add(lblVwapVal);
                    stack.Children.Add(lblTradeHours);
                    stack.Children.Add(lblBrickInfo);
                    lblRunInfo = MakeLabel("Run: —", Brushes.Gray, 10, FontWeights.Normal, HorizontalAlignment.Left);
                    stack.Children.Add(lblRunInfo);

                    stack.Children.Add(MakeSep());

                    // Quick action buttons row 1: BUY/SELL MKT
                    var row1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnBuyMkt  = MakeBtn("BUY MKT",  Brushes.LimeGreen, (s, e) => pendingLong  = true);
                    btnSellMkt = MakeBtn("SELL MKT", Brushes.OrangeRed, (s, e) => pendingShort = true);
                    row1.Children.Add(btnBuyMkt);
                    row1.Children.Add(btnSellMkt);
                    stack.Children.Add(row1);

                    var row2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnBuyAsk  = MakeBtn("BUY ASK",  Brushes.SeaGreen,    (s, e) => pendingBuyAsk  = true);
                    btnSellBid = MakeBtn("SELL BID", Brushes.IndianRed,   (s, e) => pendingSellBid = true);
                    row2.Children.Add(btnBuyAsk);
                    row2.Children.Add(btnSellBid);
                    stack.Children.Add(row2);

                    var row3 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnBuyLmt  = MakeBtn("BUY LMT",  Brushes.DarkSeaGreen, (s, e) => pendingLongLimit  = true);
                    btnSellLmt = MakeBtn("SELL LMT", Brushes.RosyBrown,    (s, e) => pendingShortLimit = true);
                    row3.Children.Add(btnBuyLmt);
                    row3.Children.Add(btnSellLmt);
                    stack.Children.Add(row3);

                    // CLOSE / FLATTEN row — directly under BUY/SELL block for fast access
                    var rowClose = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnCloseTrade = MakeBtn("CLOSE",   Brushes.Goldenrod, (s, e) => pendingCloseTrade = true);
                    btnFlatten    = MakeBtn("FLATTEN", Brushes.OrangeRed, (s, e) => pendingFlatten = true);
                    rowClose.Children.Add(btnCloseTrade);
                    rowClose.Children.Add(btnFlatten);
                    stack.Children.Add(rowClose);

                    // CLOSE 1 / KILL / RESET DAILY row
                    var rowKill = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnCloseOne   = MakeBtn("CLOSE 1", Brushes.DarkGoldenrod, (s, e) => pendingCloseOne = true);
                    btnKill       = MakeBtn("KILL",    Brushes.DarkRed,        (s, e) => pendingEmergencyKill = true);
                    btnResetDaily = MakeBtn("RESET",   Brushes.DarkSlateBlue,  (s, e) => pendingResetDaily = true);
                    btnResetDaily.ToolTip = "Reset daily counters (PnL, trade count, loss/win streaks, daily-limit flags). Use at start of new session or after restart if ResetDailyOnRestart=false.";
                    rowKill.Children.Add(btnCloseOne);
                    rowKill.Children.Add(btnKill);
                    rowKill.Children.Add(btnResetDaily);
                    stack.Children.Add(rowKill);

                    stack.Children.Add(MakeSep());

                    // Adjust rows — symmetric +/-; SL/TP nudge does NOT reset trail/BE
                    stack.Children.Add(MakeAdjustRow("Qty:", contracts.ToString(),
                        (s, e) => { int nv = contracts - 1; contracts = Math.Max(1, nv); UpdateAdjustLabels(); },
                        (s, e) => { int nv = contracts + 1; contracts = Math.Min(maxContracts, nv); UpdateAdjustLabels(); },
                        out lblQtyVal));
                    stack.Children.Add(MakeAdjustRow("SL:", slPoints + "pt | $" + (slPoints * 20),
                        // Convention matches TP +/-: "-" decreases the points number, "+" increases it.
                        // For SL that means "-" = TIGHTER (smaller risk distance, SL closer to price)
                        //                    "+" = WIDER  (more risk room, SL further from price).
                        // Operates on hiddenStopPrice DIRECTLY so it works correctly even after
                        // Jump SL has moved SL into profit (where slPoints/entry-math becomes ambiguous).
                        (s, e) => { RequestSlNudgePoints(+slTpAdjustStep); },  // "-" tightens => positive nudge to RequestSlNudgePoints (which TIGHTENS by convention of that helper)
                        (s, e) => { RequestSlNudgePoints(-slTpAdjustStep); },  // "+" widens   => negative nudge
                        out lblSlVal));
                    stack.Children.Add(MakeAdjustRow("TP:", tpPoints + "pt | $" + (tpPoints * 20),
                        (s, e) => { int nv = tpPoints - slTpAdjustStep; tpPoints = Math.Max(1, nv); UpdateAdjustLabels(); RequestSlTpResize(); },
                        (s, e) => { int nv = tpPoints + slTpAdjustStep; tpPoints = Math.Min(500, nv); UpdateAdjustLabels(); RequestSlTpResize(); },
                        out lblTpVal));
                    stack.Children.Add(MakeAdjustRow("Jump%:", jumpSlPercent + "%",
                        (s, e) => { int nv = jumpSlPercent - 5; jumpSlPercent = Math.Max(10, nv); UpdateJumpLabel(); },
                        (s, e) => { int nv = jumpSlPercent + 5; jumpSlPercent = Math.Min(95, nv); UpdateJumpLabel(); },
                        out lblJumpPct));

                    // Trail distance manual nudge — − tightens (locks more profit), + loosens (gives room)
                    // Step is in POINTS (1 pt = 4 ticks = $20 on NQ). Activates manual mode.
                    stack.Children.Add(MakeAdjustRow("Trail ±pt:", "—",
                        (s, e) => { RequestTrailNudge(-(int)trailNudgeStepPoints); },
                        (s, e) => { RequestTrailNudge(+(int)trailNudgeStepPoints); },
                        out lblTrailDistVal));

                    stack.Children.Add(MakeSep());

                    // Bottom action row 1: TRL NOW + JUMP SL (the two FAST profit-protection actions)
                    var rowTrailJump = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnTrailNow = MakeBtn("TRL NOW", Brushes.Magenta,    (s, e) => RequestTrailActivate());
                    btnJumpSL   = MakeBtn("JUMP SL", Brushes.DodgerBlue, (s, e) => pendingJumpSL = true);
                    btnTrailNow.ToolTip = "Activate the hidden trail RIGHT NOW with aggressive distance (max 2pt or 0.5×ATR). Auto-ratchet continues afterward; nudges via Trail ±pt persist.";
                    btnJumpSL.ToolTip   = "Move SL closer to current price by Jump% of the current SL gap. Uses live bid/ask.";
                    rowTrailJump.Children.Add(btnTrailNow);
                    rowTrailJump.Children.Add(btnJumpSL);
                    stack.Children.Add(rowTrailJump);

                    // Bottom action row 2: TRL ON / TRP ON / BE ON master toggles
                    var rowToggles = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnTrailToggle = MakeToggle(trailEnabled ? "TRL ON" : "TRL OFF", trailEnabled, (s, e) =>
                    { trailEnabled = !trailEnabled;
                      btnTrailToggle.Content = trailEnabled ? "TRL ON" : "TRL OFF";
                      btnTrailToggle.Background = trailEnabled ? Brushes.DarkSlateGray : Brushes.DarkRed;
                      // When user turns TRL OFF, fully disarm any active/manual-early trail state so
                      // the price-cross check no longer fires. Without this, a previously-active TRL
                      // NOW would keep exiting at the in-memory trailPrice even though the UI says OFF.
                      if (!trailEnabled)
                      {
                          trailActive = false;
                          manualTrailEarlyStart = false;
                          trailPrice = 0;
                          trailMaxProfitPts = 0;
                          trailTierName = "";
                          Print(TAG + "TRL OFF -> trail fully disarmed (active+manualEarly cleared)");
                      } });
                    btnTrapToggle = MakeToggle(enableTrapDetector ? "TRP ON" : "TRP OFF", enableTrapDetector, (s, e) =>
                    { enableTrapDetector = !enableTrapDetector;
                      btnTrapToggle.Content = enableTrapDetector ? "TRP ON" : "TRP OFF";
                      btnTrapToggle.Background = enableTrapDetector ? Brushes.DarkSlateGray : Brushes.DarkRed; });
                    btnTrapToggle.ToolTip = "Trap Detector — watches for MM stop-hunt patterns (sudden adverse spike + reversal). When ON, may tighten trail or skip entries against suspected trap moves.";
                    btnBeToggle = MakeToggle(breakevenEnabled ? "BE ON" : "BE OFF", breakevenEnabled, (s, e) =>
                    { breakevenEnabled = !breakevenEnabled;
                      btnBeToggle.Content = breakevenEnabled ? "BE ON" : "BE OFF";
                      btnBeToggle.Background = breakevenEnabled ? Brushes.DarkSlateGray : Brushes.DarkRed; });
                    btnBeToggle.ToolTip = "Smart Break-Even — trigger = max(BE-pts, 0.5×ATR, 0.4×TP). First lock at entry+2tk, then ratchets +2tk per 5pt of further profit.";
                    rowToggles.Children.Add(btnTrailToggle);
                    rowToggles.Children.Add(btnTrapToggle);
                    rowToggles.Children.Add(btnBeToggle);
                    stack.Children.Add(rowToggles);

                    // Bottom action row 3: AGGR / CHOP / ADAPT smart-mode toggles
                    var rowSmart = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 2) };
                    btnAggrToggle = MakeToggle(aggressiveExitsEnabled ? "AGGR ON" : "AGGR OFF", aggressiveExitsEnabled, (s, e) =>
                    { aggressiveExitsEnabled = !aggressiveExitsEnabled;
                      btnAggrToggle.Content = aggressiveExitsEnabled ? "AGGR ON" : "AGGR OFF";
                      btnAggrToggle.Background = aggressiveExitsEnabled ? Brushes.DarkOrange : Brushes.DarkRed;
                      UpdateDashboardStatus("Aggressive exits " + (aggressiveExitsEnabled ? "ON" : "OFF"), Brushes.LightGoldenrodYellow);
                      if (enableDiagLog) WriteDiagRow("TOGGLE_AGGR", aggressiveExitsEnabled ? "ON" : "OFF"); });
                    btnAggrToggle.ToolTip = "AGGRESSIVE Exits  BE locks at +" + aggrBeAtPoints + "pt, trail starts at +" + aggrTrailActivationPts + "pt with " + aggrTrailDistPts + "pt distance, and pullback >= " + aggrPullbackAtrFactor + "×ATR within " + aggrPullbackMaxBars + " bars after entry forces a market exit. Default ON.";
                    btnChopToggle = MakeToggle(chopFilterEnabled ? "CHOP ON" : "CHOP OFF", chopFilterEnabled, (s, e) =>
                    { chopFilterEnabled = !chopFilterEnabled;
                      btnChopToggle.Content = chopFilterEnabled ? "CHOP ON" : "CHOP OFF";
                      btnChopToggle.Background = chopFilterEnabled ? Brushes.DarkSlateGray : Brushes.DarkRed;
                      UpdateDashboardStatus("Chop filter " + (chopFilterEnabled ? "ON" : "OFF"), Brushes.LightGoldenrodYellow);
                      if (enableDiagLog) WriteDiagRow("TOGGLE_CHOP", chopFilterEnabled ? "ON" : "OFF"); });
                    btnChopToggle.ToolTip = "CHOP filter  blocks BOTH manual and auto entries when ADX collapses, EMAs converge, recent close-range tightens, or tape fights the entry direction. Default ON.";
                    btnAdaptToggle = MakeToggle(adaptiveWindowEnabled ? "ADAPT ON" : "ADAPT OFF", adaptiveWindowEnabled, (s, e) =>
                    { adaptiveWindowEnabled = !adaptiveWindowEnabled;
                      if (!adaptiveWindowEnabled) adaptiveTightenActive = false;
                      btnAdaptToggle.Content = adaptiveWindowEnabled ? "ADAPT ON" : "ADAPT OFF";
                      btnAdaptToggle.Background = adaptiveWindowEnabled ? Brushes.DarkSlateGray : Brushes.DarkRed;
                      UpdateDashboardStatus("Adaptive window " + (adaptiveWindowEnabled ? "ON" : "OFF"), Brushes.LightGoldenrodYellow);
                      if (enableDiagLog) WriteDiagRow("TOGGLE_ADAPT", adaptiveWindowEnabled ? "ON" : "OFF"); });
                    btnAdaptToggle.ToolTip = "ADAPTIVE intra-day window  rolling " + adaptiveWindowSize + "-trade tracker. After " + adaptiveWindowLossThreshold + " losses in window, requires +" + adaptiveConfBoost + " min-confidence; clears after " + adaptiveWindowClearWins + " wins. Default ON.";
                    rowSmart.Children.Add(btnAggrToggle);
                    rowSmart.Children.Add(btnChopToggle);
                    rowSmart.Children.Add(btnAdaptToggle);
                    btnRunnerToggle = MakeToggle(runnerModeActive_user ? "RUN ON" : "RUN OFF", runnerModeActive_user, (s, e) =>
                    { runnerModeActive_user = !runnerModeActive_user;
                      btnRunnerToggle.Content = runnerModeActive_user ? "RUN ON" : "RUN OFF";
                      btnRunnerToggle.Background = runnerModeActive_user ? Brushes.DarkGreen : Brushes.DarkRed;
                      // Reset trail max so the wider distance computes from current price, not stale peak
                      if (runnerModeActive_user) trailMaxProfitPts = 0;
                      UpdateDashboardStatus("Runner Mode " + (runnerModeActive_user ? "ON — wide trail, no aggression" : "OFF"), Brushes.LightGoldenrodYellow);
                      Print(TAG + "Runner Mode " + (runnerModeActive_user ? "ON" : "OFF"));
                      if (enableDiagLog) WriteDiagRow("TOGGLE_RUNNER", runnerModeActive_user ? "ON" : "OFF"); });
                    btnRunnerToggle.ToolTip = "RUNNER Mode (per-trade) — wide trail (1.5×ATR, floor 4pt), disables profit-aggression multipliers, time ratchet, trap/EMA tighten, AGGR override, and tier floors. Use when you spot a strong-trend setup and want to let the runner run. Auto-clears when position goes flat.";
                    rowSmart.Children.Add(btnRunnerToggle);
                    stack.Children.Add(rowSmart);

                    dashScroller = new ScrollViewer { Content = stack, MaxHeight = dashHeight, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    outer.Children.Add(dashScroller);

                    // Bottom-right resize grip — drag to enlarge dashboard.
                    var gripRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 4) };
                    var grip = new TextBlock
                    {
                        Text = "↙ resize ↘",
                        Foreground = new SolidColorBrush(Color.FromRgb(140, 150, 170)),
                        FontSize = 9,
                        Cursor = Cursors.SizeNWSE,
                        Padding = new Thickness(8, 2, 4, 2)
                    };
                    grip.MouseLeftButtonDown += (s, e) =>
                    {
                        dashResizing = true;
                        dashResizeStart = e.GetPosition(ChartControl);
                        dashResizeStartW = dashOuterBorder.Width;
                        dashResizeStartH = dashScroller.MaxHeight;
                        grip.CaptureMouse();
                        e.Handled = true;
                    };
                    grip.MouseLeftButtonUp += (s, e) => { dashResizing = false; grip.ReleaseMouseCapture(); e.Handled = true; };
                    grip.MouseMove += (s, e) =>
                    {
                        if (!dashResizing) return;
                        var p = e.GetPosition(ChartControl);
                        double nw = Math.Max(280, Math.Min(700, dashResizeStartW + (p.X - dashResizeStart.X)));
                        double nh = Math.Max(400, Math.Min(1400, dashResizeStartH + (p.Y - dashResizeStart.Y)));
                        dashOuterBorder.Width = nw;
                        dashScroller.MaxHeight = nh;
                        dashWidth = nw;
                        dashHeight = nh;
                    };
                    gripRow.Children.Add(grip);
                    outer.Children.Add(gripRow);
                    border.Child = outer;
                    dashboardPanel.Children.Add(border);

                    dashboardPanel.HorizontalAlignment = HorizontalAlignment.Right;
                    dashboardPanel.VerticalAlignment   = VerticalAlignment.Top;
                    dashboardPanel.Margin = new Thickness(0, 60, 16, 0);

                    if (ChartControl.Parent is Grid g)
                    {
                        g.Children.Add(dashboardPanel);
                        dashboardHostPanel = g;
                    }

                    UpdateModeButtons();
                    UpdateAdjustLabels();
                    UpdateJumpLabel();
                }
                catch (Exception ex)
                {
                    Print(TAG + "BuildDashboard EX: " + ex.Message);
                    dashboardAttached = false;
                }
            });
        }

        private void UpdateDashboard()
        {
            if (ChartControl == null || lblPnL == null) return;

            // Snapshot data on data thread
            double snapClose = Close[0];
            MarketPosition mp = Position.MarketPosition;
            int posQty = Position.Quantity;
            double posAvg = Position.AveragePrice;
            int snapTime = ToTime(Time[0]);
            double snapUnreal = 0;
            if (mp != MarketPosition.Flat && averageEntryPrice > 0)
            {
                double pd = mp == MarketPosition.Long
                    ? snapClose - averageEntryPrice
                    : averageEntryPrice - snapClose;
                double q = Math.Max(totalContracts, posQty);
                snapUnreal = pd * NQ_DOLLARS_PER_POINT * q;
            }
            int snapTradeDir = openTradeDirection;
            bool snapStopsArmed = stopsArmed;
            bool snapPendingExit = pendingExit;
            double snapHiddenSL = hiddenStopPrice;
            double snapHiddenTP = hiddenTargetPrice;
            int snapSlPts = slPoints, snapTpPts = tpPoints;
            double snapTotalContracts = totalContracts;
            double snapTrailPrice = trailPrice;
            bool snapTrailActive = trailActive;
            string snapTrailTier = trailTierName;
            double snapTrapScore = trapScore;
            bool snapTrapDetect = trapDetected;
            int snapTrapBars = trapBarsInTrade;
            double snapBull = lastBullConfidence, snapBear = lastBearConfidence;
            double snapDailyPnL = dailyRealizedPnL;
            int snapDailyTrades = dailyTradeCount;
            bool snapDailyLimit = dailyLimitHit, snapProfitHit = dailyProfitHit;
            bool snapEmergKill = emergencyKillActive, snapFlattenDone = flattenFired;
            double snapVwap = vwapValue;
            double snapTape = cachedTapeDelta;
            int snapSignal = manualSignalLevel;
            string snapSignalReason = manualSignalReason;
            string snapLastStrat = lastAutoStrategyUsed;
            // v6 0.2.1 Renko brick snapshot (cheap reads of Phase 0.2 fields)
            bool   snapRenkoOn     = enableRenkoSeries;
            string snapBrickColor  = lastBrickColor ?? "";
            int    snapBrickStreak = brickStreakCount;
            int    snapBrickSize   = renkoBrickSize;
            int    snapBrickOff    = renkoBrickOffset;
            // v6 0.2.3 NinzaRenko snapshot (PRIMARY brick signal)
            bool   snapNrOn        = enableNinzaRenkoSeries && ninzaSeriesAdded;
            string snapNrColor     = lastNrBrickColor ?? "";
            int    snapNrStreak    = nrBrickStreakCount;
            // v6 1.2 brick-run snapshot
            bool   snapBrickAnOn   = enableBrickAnalytics;
            string snapRunColor    = runCurrentColor ?? "";
            int    snapRunLen      = runCurrentLen;
            double snapRunMaxFav   = runMaxFavPts;
            int    snapRunMaxLast10 = runMaxLast10;
            int    snapFastBricks  = fastBricksLast10;

            double snapAcctRealized = 0, snapAcctUnreal = 0, snapAcctBal = 0;
            bool snapAcctOk = false;
            try
            {
                snapAcctRealized = Account.Get(AccountItem.RealizedProfitLoss, Currency.UsDollar);
                snapAcctUnreal   = Account.Get(AccountItem.UnrealizedProfitLoss, Currency.UsDollar);
                snapAcctBal      = Account.Get(AccountItem.CashValue, Currency.UsDollar);
                snapAcctOk = true;
            }
            catch { }

            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (lblSignal != null)
                    {
                        switch (snapSignal)
                        {
                            case 2:  lblSignal.Text = "▲ STRONG BUY";  lblSignal.Foreground = Brushes.LimeGreen; break;
                            case 1:  lblSignal.Text = "↑ BUY";          lblSignal.Foreground = Brushes.Lime; break;
                            case 0:  lblSignal.Text = "● WAIT";         lblSignal.Foreground = Brushes.Gray; break;
                            case -1: lblSignal.Text = "↓ SELL";         lblSignal.Foreground = Brushes.OrangeRed; break;
                            case -2: lblSignal.Text = "▼ STRONG SELL"; lblSignal.Foreground = Brushes.Red; break;
                        }
                    }
                    if (lblSignalReason != null) lblSignalReason.Text = snapSignalReason;

                    if (lblConfBull != null)
                    {
                        lblConfBull.Text = "Bull: " + snapBull.ToString("F0") + "%";
                        lblConfBull.Opacity = snapBull >= minSignalConfidence ? 1.0 : Math.Max(0.35, snapBull / Math.Max(1.0, minSignalConfidence));
                    }
                    if (lblConfBear != null)
                    {
                        lblConfBear.Text = "Bear: " + snapBear.ToString("F0") + "%";
                        lblConfBear.Opacity = snapBear >= minSignalConfidence ? 1.0 : Math.Max(0.35, snapBear / Math.Max(1.0, minSignalConfidence));
                    }
                    if (lblTapeDelta != null)
                    {
                        if (orderFlowFilterEnabled && State == State.Realtime)
                        {
                            string arrow = snapTape > 0.15 ? "▲ buyers" : (snapTape < -0.15 ? "▼ sellers" : "● balanced");
                            lblTapeDelta.Text = "Tape Δ: " + snapTape.ToString("F2") + "  " + arrow;
                            lblTapeDelta.Foreground = snapTape > 0.15 ? Brushes.LimeGreen : (snapTape < -0.15 ? Brushes.OrangeRed : Brushes.Gray);
                        }
                        else lblTapeDelta.Text = "Tape Δ: off";
                    }

                    // Status
                    if (snapPendingExit) { lblStatus.Text = "● CLOSING..."; lblStatus.Foreground = Brushes.Yellow; }
                    else if (snapDailyLimit) { lblStatus.Text = "■ DAILY LOSS HALT"; lblStatus.Foreground = Brushes.OrangeRed; }
                    else if (snapProfitHit)  { lblStatus.Text = "■ PROFIT TARGET HIT"; lblStatus.Foreground = Brushes.Gold; }
                    else if (snapEmergKill)  { lblStatus.Text = "⚠ EMERGENCY KILL"; lblStatus.Foreground = Brushes.OrangeRed; }
                    else if (mp != MarketPosition.Flat)
                    {
                        string dir = mp == MarketPosition.Long ? "LONG" : "SHORT";
                        lblStatus.Text = "● " + dir + " ×" + posQty;
                        lblStatus.Foreground = mp == MarketPosition.Long ? Brushes.LimeGreen : Brushes.OrangeRed;
                    }
                    else if (snapFlattenDone) { lblStatus.Text = "■ EOD flatten — done"; lblStatus.Foreground = Brushes.Orange; }
                    else if (autoMode) { lblStatus.Text = "● Flat — AUTO scanning"; lblStatus.Foreground = Brushes.CornflowerBlue; }
                    else { lblStatus.Text = "● Flat — manual"; lblStatus.Foreground = Brushes.CornflowerBlue; }

                    if (mp != MarketPosition.Flat)
                    {
                        int dq = (int)Math.Max(snapTotalContracts, posQty);
                        lblPosition.Text = "Pos: " + dq + "/" + maxContracts + "  Avg: "
                            + (averageEntryPrice > 0 ? averageEntryPrice.ToString("F2") : posAvg.ToString("F2"));
                        // R-multiple
                        double rMult = 0;
                        if (snapStopsArmed && snapHiddenSL > 0 && averageEntryPrice > 0 && snapTradeDir != 0)
                        {
                            double slDist = Math.Abs(averageEntryPrice - (snapTradeDir == 1 ? averageEntryPrice - snapSlPts : averageEntryPrice + snapSlPts));
                            // simpler: compute in points using current
                            double profitPts = snapTradeDir == 1 ? (snapClose - averageEntryPrice) : (averageEntryPrice - snapClose);
                            profitPts /= (TickSize * NQ_TICKS_PER_POINT);
                            if (snapSlPts > 0) rMult = profitPts / snapSlPts;
                        }
                        lblHiddenSL.Text = snapHiddenSL > 0
                            ? "SL: " + snapHiddenSL.ToString("F2") + "  (" + snapSlPts + "pt | $" + (snapSlPts * NQ_DOLLARS_PER_POINT * Math.Max(snapTotalContracts, posQty)).ToString("F0") + ")"
                            : "SL: — (not armed)";
                        lblHiddenTP.Text = snapHiddenTP > 0
                            ? "TP: " + snapHiddenTP.ToString("F2") + "  (" + snapTpPts + "pt | $" + (snapTpPts * NQ_DOLLARS_PER_POINT * Math.Max(snapTotalContracts, posQty)).ToString("F0") + ")  R=" + rMult.ToString("F2")
                            : "TP: — (not armed)";
                        if (trailEnabled && snapTrailActive)
                        {
                            double td = snapTradeDir == 1
                                ? (snapClose - snapTrailPrice) / (TickSize * NQ_TICKS_PER_POINT)
                                : (snapTrailPrice - snapClose) / (TickSize * NQ_TICKS_PER_POINT);
                            lblTrailInfo.Text = "Trail: " + snapTrailPrice.ToString("F2") + " (" + td.ToString("F1") + "pt " + snapTrailTier + ")";
                            lblTrailInfo.Foreground = snapTrailTier == "T3-Runner" ? Brushes.Gold
                                                    : snapTrailTier == "T2-Strong" ? Brushes.Cyan
                                                    : snapTrailTier == "T1-BE" ? Brushes.Yellow : Brushes.Magenta;
                        }
                        else if (trailEnabled)
                        {
                            double pp = 0;
                            if (snapTradeDir == 1) pp = (snapClose - averageEntryPrice) / (TickSize * NQ_TICKS_PER_POINT);
                            else if (snapTradeDir == -1) pp = (averageEntryPrice - snapClose) / (TickSize * NQ_TICKS_PER_POINT);
                            lblTrailInfo.Text = "Trail: waiting (" + pp.ToString("F1") + "/" + trailActivationPoints + "pt)";
                            lblTrailInfo.Foreground = Brushes.Gray;
                        }
                        else { lblTrailInfo.Text = "Trail: OFF"; lblTrailInfo.Foreground = Brushes.Gray; }
                        if (enableTrapDetector)
                        {
                            lblTrapInfo.Text = (snapTrapDetect ? "Trap: DETECTED " : "Trap: ") + snapTrapScore.ToString("F0") + "% bars=" + snapTrapBars;
                            lblTrapInfo.Foreground = snapTrapDetect ? Brushes.OrangeRed : Brushes.Orange;
                        }
                        else { lblTrapInfo.Text = "Trap: OFF"; lblTrapInfo.Foreground = Brushes.Gray; }
                    }
                    else
                    {
                        lblPosition.Text = "Pos: 0/" + maxContracts;
                        lblHiddenSL.Text = "SL: —";
                        lblHiddenTP.Text = "TP: —";
                        lblTrailInfo.Text = trailEnabled ? "Trail: —" : "Trail: OFF";
                        lblTrapInfo.Text  = enableTrapDetector ? "Trap: —" : "Trap: OFF";
                    }

                    lblUnrealized.Text = "Unrealized: " + snapUnreal.ToString("C2");
                    lblUnrealized.Foreground = snapUnreal >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                    double dailyTotal = snapDailyPnL + snapUnreal;
                    string tc = maxTradesPerDay > 0 ? "  [" + snapDailyTrades + "/" + maxTradesPerDay + "]" : "  [" + snapDailyTrades + "]";
                    lblPnL.Text = "Daily P&L: " + dailyTotal.ToString("C2") + tc;
                    lblPnL.Foreground = dailyTotal >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                    if (snapAcctOk)
                    {
                        double at = snapAcctRealized + snapAcctUnreal;
                        lblAccountPnL.Text = "Account P&L: " + at.ToString("C2") + " (R " + snapAcctRealized.ToString("C2") + " / U " + snapAcctUnreal.ToString("C2") + ")";
                        lblAccountPnL.Foreground = at >= 0 ? Brushes.LimeGreen : Brushes.OrangeRed;
                        lblAccountBal.Text = "Balance: " + snapAcctBal.ToString("C2");
                    }
                    else { lblAccountPnL.Text = "Account P&L: N/A"; lblAccountBal.Text = "Balance: N/A"; }

                    lblVwapVal.Text = "VWAP: " + (snapVwap > 0 ? snapVwap.ToString("F2") : "—");
                    bool inHr = !tradingHoursEnabled || (snapTime >= tradingStartTime && snapTime < flattenTime);
                    lblTradeHours.Text = "Hours: " + (inHr ? "● open " : "○ closed ") + FormatTime(tradingStartTime) + "–" + FormatTime(flattenTime);
                    lblTradeHours.Foreground = inHr ? Brushes.LimeGreen : Brushes.Gray;

                    // v6 0.2.1 brick row — read-only Renko 64/16 status
                    // v6 0.2.3: shows BOTH NinzaRenko (primary) and stock Renko (sanity).
                    if (lblBrickInfo != null)
                    {
                        if (!snapRenkoOn && !snapNrOn)
                        {
                            lblBrickInfo.Text = "Brick: off";
                            lblBrickInfo.Foreground = Brushes.DimGray;
                        }
                        else
                        {
                            string nrPart, srPart;
                            // NinzaRenko part (primary)
                            if (!snapNrOn) nrPart = "NR=off";
                            else if (string.IsNullOrEmpty(snapNrColor)) nrPart = "NR=—";
                            else nrPart = "NR=" + (snapNrColor == "G" ? "GREEN" : snapNrColor == "R" ? "RED" : snapNrColor) + "×" + snapNrStreak;
                            // Stock Renko part (sanity)
                            if (!snapRenkoOn) srPart = "SR=off";
                            else if (string.IsNullOrEmpty(snapBrickColor)) srPart = "SR=—";
                            else srPart = "SR=" + snapBrickColor + "×" + snapBrickStreak;
                            lblBrickInfo.Text = "Brick: " + nrPart + "  " + srPart + "  (" + snapBrickSize + "/" + snapBrickOff + ")";
                            // Color based on PRIMARY (NinzaRenko); fall back to stock if NR missing.
                            string primaryColor = !string.IsNullOrEmpty(snapNrColor) ? snapNrColor : snapBrickColor;
                            lblBrickInfo.Foreground = primaryColor == "G" ? Brushes.LimeGreen
                                                    : (primaryColor == "R" ? Brushes.OrangeRed : Brushes.Gray);
                        }
                    }
                    // v6 1.2 — run row (only meaningful when EnableBrickAnalytics is ON)
                    if (lblRunInfo != null)
                    {
                        if (!snapBrickAnOn)
                        {
                            lblRunInfo.Text = "Run: off";
                            lblRunInfo.Foreground = Brushes.DimGray;
                        }
                        else if (string.IsNullOrEmpty(snapRunColor) || snapRunLen <= 0)
                        {
                            lblRunInfo.Text = "Run: —";
                            lblRunInfo.Foreground = Brushes.Gray;
                        }
                        else
                        {
                            lblRunInfo.Text = "Run: " + snapRunColor + "×" + snapRunLen
                                            + "  fav=" + snapRunMaxFav.ToString("F0") + "pt"
                                            + "  max10=" + snapRunMaxLast10
                                            + "  fast=" + snapFastBricks + "/10";
                            lblRunInfo.Foreground = snapRunColor == "G" ? Brushes.LimeGreen
                                                  : (snapRunColor == "R" ? Brushes.OrangeRed : Brushes.Gray);
                        }
                    }

                    if (lblActiveStrategy != null)
                    {
                        string at = "Active: " + (string.IsNullOrEmpty(snapLastStrat) ? "—" : snapLastStrat);
                        if (autoStrategy == 2 && strategyScores != null)
                            at += "  [M+V=" + strategyScores[0].ToString("F0") + " KLB=" + strategyScores[1].ToString("F0") + "]";
                        lblActiveStrategy.Text = at;
                    }

                    bool tradeFrozen = mp != MarketPosition.Flat;
                    if (btnStratPrev != null) btnStratPrev.IsEnabled = !tradeFrozen;
                    if (btnStratNext != null) btnStratNext.IsEnabled = !tradeFrozen;

                    // Refresh adjust labels (keeps Trail \u00b1tk distance live)
                    UpdateAdjustLabels();
                    if (btnTrailNow != null) btnTrailNow.IsEnabled = (mp != MarketPosition.Flat) && trailEnabled;
                }
                catch { }
            });
        }

        private void UpdateModeButtons()
        {
            if (btnModeManual == null || btnModeAuto == null) return;
            btnModeManual.Background = !autoMode ? new SolidColorBrush(Color.FromRgb(30, 90, 160)) : new SolidColorBrush(Color.FromRgb(50, 55, 65));
            btnModeAuto.Background   =  autoMode ? new SolidColorBrush(Color.FromRgb(30, 90, 160)) : new SolidColorBrush(Color.FromRgb(50, 55, 65));
        }
        private void UpdateStratLabel()
        {
            if (lblStratName != null) lblStratName.Text = StrategyDisplay(autoStrategy);
        }
        private void UpdateAdjustLabels()
        {
            if (lblQtyVal != null) lblQtyVal.Text = contracts.ToString();
            if (lblSlVal  != null) lblSlVal.Text  = slPoints + "pt | $" + (slPoints * 20);
            if (lblTpVal  != null) lblTpVal.Text  = tpPoints + "pt | $" + (tpPoints * 20);
            if (lblTrailDistVal != null)
            {
                if (trailActive && trailPrice > 0 && averageEntryPrice > 0 && openTradeDirection != 0)
                {
                    double price = Close[0];
                    double tickPt = TickSize * NQ_TICKS_PER_POINT;
                    double distPt = openTradeDirection == 1
                        ? (price - trailPrice) / tickPt
                        : (trailPrice - price) / tickPt;
                    int distTk = (int)Math.Round(distPt * NQ_TICKS_PER_POINT);
                    string flag = manualTrailEarlyStart ? " *" : "";
                    string offTxt = Math.Abs(manualTrailOffsetPoints) > 0.01
                        ? "  off=" + (manualTrailOffsetPoints > 0 ? "+" : "") + manualTrailOffsetPoints.ToString("F0") + "pt"
                        : "";
                    lblTrailDistVal.Text = distPt.ToString("F1") + "pt | " + distTk + "tk" + flag + offTxt;
                }
                else lblTrailDistVal.Text = trailEnabled ? "— (waiting)" : "OFF";
            }
        }
        private void UpdateJumpLabel()
        {
            if (lblJumpPct != null) lblJumpPct.Text = jumpSlPercent + "%";
        }

        private void UpdateDashboardStatus(string text, Brush color)
        {
            if (lblStatus == null || ChartControl == null) return;
            ChartControl.Dispatcher.InvokeAsync(() =>
            {
                if (lblStatus != null) { lblStatus.Text = text; lblStatus.Foreground = color; }
            });
        }

        private void RemoveDashboard()
        {
            var panel = dashboardPanel;
            var host  = dashboardHostPanel;
            var chart = ChartControl;
            dashboardPanel = null; dashboardHostPanel = null; dashboardAttached = false;
            if (panel == null) return;
            if (chart == null) { try { if (host != null) host.Children.Remove(panel); } catch { } return; }
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

        // ---------- WPF helpers ----------
        private TextBlock MakeLabel(string text, Brush fg, double fontSize, FontWeight weight, HorizontalAlignment ha)
        {
            return new TextBlock
            {
                Text = text, Foreground = fg, FontSize = fontSize, FontWeight = weight,
                HorizontalAlignment = ha, Margin = new Thickness(2, 1, 2, 1)
            };
        }
        private Border MakeSep()
        {
            return new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(45, 50, 60)),
                Height = 1, Margin = new Thickness(0, 4, 0, 4)
            };
        }
        private Button MakeBtn(string text, Brush bg, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, Width = 78, Height = 26,
                Margin = new Thickness(2, 2, 2, 2),
                Background = bg, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0)
            };
            if (click != null) b.Click += click;
            return b;
        }
        private Button MakeSmallBtn(string text, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, Width = 28, Height = 24,
                Margin = new Thickness(2, 0, 2, 0),
                Background = new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                Foreground = Brushes.White, FontSize = 11, BorderThickness = new Thickness(0)
            };
            if (click != null) b.Click += click;
            return b;
        }
        private Button MakeToggle(string text, bool active, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, Width = 78, Height = 24,
                Margin = new Thickness(2, 2, 2, 2),
                Background = active ? new SolidColorBrush(Color.FromRgb(30, 90, 160)) : new SolidColorBrush(Color.FromRgb(50, 55, 65)),
                Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0)
            };
            if (click != null) b.Click += click;
            return b;
        }
        private StackPanel MakeAdjustRow(string label, string value, RoutedEventHandler dec, RoutedEventHandler inc, out TextBlock valLbl)
        {
            // Fixed widths so −/+ buttons line up across all adjust rows.
            // Total width ≈ 60 + 44 + 150 + 44 = 298 px, fits inside 340-px dashboard with margin.
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2) };
            var lbl = MakeLabel(label, Brushes.Gray, 11, FontWeights.Normal, HorizontalAlignment.Left);
            lbl.Width = 60;
            lbl.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(lbl);
            row.Children.Add(MakeAdjustBtn("−", dec));
            valLbl = MakeLabel(value, Brushes.White, 11, FontWeights.SemiBold, HorizontalAlignment.Center);
            valLbl.Width = 150;
            valLbl.TextAlignment = TextAlignment.Center;
            valLbl.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(valLbl);
            row.Children.Add(MakeAdjustBtn("+", inc));
            return row;
        }

        // Larger / clearer adjust button (different visual from MakeSmallBtn used by strategy nav)
        private Button MakeAdjustBtn(string text, RoutedEventHandler click)
        {
            var b = new Button
            {
                Content = text, Width = 36, Height = 28,
                Margin = new Thickness(4, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(60, 70, 90)),
                Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.Bold,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromRgb(90, 105, 130)),
                Cursor = Cursors.Hand
            };
            b.MouseEnter += (s, e) => b.Background = new SolidColorBrush(Color.FromRgb(80, 110, 160));
            b.MouseLeave += (s, e) => b.Background = new SolidColorBrush(Color.FromRgb(60, 70, 90));
            if (click != null) b.Click += click;
            return b;
        }
        #endregion

        // ===========================================================
        //  DIAGNOSTIC LOG
        // ===========================================================
        #region Diagnostics
        private void OpenDiagLog()
        {
            try
            {
                string docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string dirPath = System.IO.Path.Combine(docPath, "NinjaTrader 8");
                if (!System.IO.Directory.Exists(dirPath)) System.IO.Directory.CreateDirectory(dirPath);
                string fileName = "MmATM_v6_DiagLog_" + (Time.Count > 0 ? Time[0].ToString("yyyyMMdd") : DateTime.Now.ToString("yyyyMMdd")) + ".csv";
                string fullPath = System.IO.Path.Combine(dirPath, fileName);
                diagWriter = new System.IO.StreamWriter(fullPath, false, System.Text.Encoding.UTF8);
                diagWriter.AutoFlush = true;
                diagHeaderWritten = false;
                Print(TAG + "DiagLog open: " + fullPath);
            }
            catch (Exception ex) { Print(TAG + "DiagLog open EX: " + ex.Message); }
        }
        private void CloseDiagLog()
        {
            if (diagWriter != null)
            {
                try { diagWriter.Flush(); diagWriter.Close(); } catch { }
                diagWriter = null;
            }
        }
        private void WriteDiagHeader()
        {
            if (diagWriter == null || diagHeaderWritten) return;
            // v14.20: extended header (39 columns) for deeper post-mortem analysis.
            //   Added: Mode, AutoStrat, Toggles, Signal, MinConf, DistVwapAtr, VoidBars,
            //          OpenType, ProfitPts, PeakPts.
            // v6 0.1.1 (Apr 28, 2026): MM-analysis columns added (now 54 cols total).
            //   Added: Bid, Ask, SpreadTk, TapeBuyVol, TapeSellVol, EmaStack, AdxSlope,
            //          DistPocPts, DistVahPts, DistValPts, DistPdHiPts, DistPdLoPts,
            //          BrickColor, BrickStreak, Regime.
            //   Why each:
            //     Bid/Ask/SpreadTk     - MM widens spread to fade retail; rolling spread spikes
            //                            correlate with stop-runs.
            //     TapeBuy/SellVol      - separated buy vs sell aggressor volume (cachedTapeDelta
            //                            collapses both into one number; raw helps post-mortem).
            //     EmaStack             - +1/0/-1 fast trend proxy (used until F1 Regime is built).
            //     AdxSlope             - ADX[0]-ADX[5], rising trend strength = +.
            //     DistPoc/Vah/Val      - distance to volume-profile pivots (positive = above level)
            //                            seeds the F2 POC/VA-aware range trader.
            //     DistPdHi/PdLo        - distance to prior-day H/L (the levels MM defends most).
            //     BrickColor/Streak    - Renko 64/16 brick state (filled by W6/Phase 0.2 plumbing).
            //     Regime               - F1 classifier output (filled in Phase 1).
            diagWriter.WriteLine(
                "DateTime,Bar,Close,VWAP,EmaF,EmaS,RSI,ATR,ADX,RawBull,RawBear,Bull,Bear,Tape,htfBias,trapScore,"
                + "Pos,Qty,AvgEntry,HiddenSL,HiddenTP,TrailPx,TrailTier,ManualOff,ConsecLoss,ConsecWin,DailyPnL,"
                + "Mode,AutoStrat,Toggles,Signal,MinConf,DistVwapAtr,VoidBars,OpenType,ProfitPts,PeakPts,"
                + "Bid,Ask,SpreadTk,TapeBuyVol,TapeSellVol,EmaStack,AdxSlope,"
                + "DistPocPts,DistVahPts,DistValPts,DistPdHiPts,DistPdLoPts,"
                + "BrickColor,BrickStreak,Regime,"
                + "Action,Detail");
            diagHeaderWritten = true;
        }
        private void WriteDiagRow(string action, string detail = "")
        {
            if (!enableDiagLog || diagWriter == null) return;
            if (!diagHeaderWritten) WriteDiagHeader();
            // Always log primary-series state regardless of which BIP fired this call.
            // (ProcessRenkoBar runs in BIP=2 — raw Close[0]/Time[0]/CurrentBar would reference
            // the Renko brick, not the primary chart.)
            if (CurrentBars[0] < 1) return;
            double pxClose = Closes[0][0];
            DateTime pxTime = Times[0][0];
            int      pxBar   = CurrentBars[0];
            try
            {
                double ef = indEmaFast != null ? indEmaFast[0] : 0;
                double es = indEmaSlow != null ? indEmaSlow[0] : 0;
                double rs = indRsi != null ? indRsi[0] : 0;
                double at = indAtr != null ? indAtr[0] : 0;
                double ax = (indAdx != null && CurrentBars[0] > 14) ? indAdx[0] : 0;
                int pos = openTradeDirection;
                int qty = Position.Quantity;

                // ---- New v14.20 derived diagnostics ----
                string mode = emergencyKillActive ? "KILL"
                            : (dailyLimitHit ? "LOSS_HALT"
                            : (dailyProfitHit ? "PROFIT_HALT"
                            : (autoMode ? "AUTO" : "MANUAL")));
                string stratUsed = lastAutoStrategyUsed ?? "";
                // Compact toggle state — "+" on, "-" off — useful to correlate behavior with config
                string toggles = (aggressiveExitsEnabled ? "AGGR+" : "AGGR-")
                               + (chopFilterEnabled ? "CHP+" : "CHP-")
                               + (adaptiveWindowEnabled ? "ADP+" : "ADP-")
                               + (runnerModeActive_user ? "RUN+" : "RUN-")
                               + (extensionFilterEnabled ? "EXT+" : "EXT-")
                               + (postWinSameDirCooldownEnabled ? "PW+" : "PW-")
                               + (dirLockoutEnabled ? "DLK+" : "DLK-")
                               + (slClusterCooldownEnabled ? "SLC+" : "SLC-")
                               + (orderFlowFilterEnabled ? "TPE+" : "TPE-")
                               + (newsBlackoutEnabled ? "NWS+" : "NWS-");
                // Manual-signal label (set by UpdateManualSignal)
                string sigLabel;
                switch (manualSignalLevel)
                {
                    case 2:  sigLabel = "STRONG_BUY"; break;
                    case 1:  sigLabel = "BUY"; break;
                    case 0:  sigLabel = "WAIT"; break;
                    case -1: sigLabel = "SELL"; break;
                    case -2: sigLabel = "STRONG_SELL"; break;
                    default: sigLabel = "?"; break;
                }
                // Effective min-conf reflects adaptive boost (so we know what threshold the entry actually had to clear)
                double effMin = minSignalConfidence + (adaptiveTightenActive ? adaptiveConfBoost : 0);
                // Distance from VWAP in ATR units (the same metric the EXTENSION filter checks)
                double distVwapAtr = (vwapValue > 0 && at > 0) ? Math.Abs(pxClose - vwapValue) / at : 0;
                int openTypeOut = openTypeSet ? openType : 0;
                // Live trade-profit metrics (zero if flat)
                double profitPts = 0, peakPts = trailMaxProfitPts;
                if (pos != 0 && averageEntryPrice > 0)
                {
                    profitPts = pos == 1
                        ? (pxClose - averageEntryPrice) / TickSize / NQ_TICKS_PER_POINT
                        : (averageEntryPrice - pxClose) / TickSize / NQ_TICKS_PER_POINT;
                }

                // ---- v6 0.1.1 MM-analysis derived metrics ----
                double tickPtMm = NQ_TICKS_PER_POINT * TickSize;
                // Bid/Ask + spread (live only; historical replay returns 0)
                double bidPx = 0, askPx = 0; int spreadTk = 0;
                if (State == State.Realtime)
                {
                    try
                    {
                        bidPx = GetCurrentBid(0);
                        askPx = GetCurrentAsk(0);
                        if (bidPx > 0 && askPx > 0 && askPx > bidPx)
                            spreadTk = (int)Math.Round((askPx - bidPx) / TickSize);
                    }
                    catch { }
                }
                // EMA stack: +1 = Close > EmaF > EmaS (bull stack), -1 = inverse, 0 = mixed
                int emaStack = 0;
                if (ef > 0 && es > 0)
                {
                    if (pxClose > ef && ef > es) emaStack = 1;
                    else if (pxClose < ef && ef < es) emaStack = -1;
                }
                // ADX slope (rising trend strength = positive). 5-bar look-back.
                double adxSlope = 0;
                if (indAdx != null && CurrentBars[0] > 20)
                {
                    try { adxSlope = ax - indAdx[5]; } catch { adxSlope = 0; }
                }
                // Volume-profile distances (signed: + = price ABOVE level)
                double distPocPts = pocLevel > 0 ? (pxClose - pocLevel) / tickPtMm : 0;
                double distVahPts = vahLevel > 0 ? (pxClose - vahLevel) / tickPtMm : 0;
                double distValPts = valLevel > 0 ? (pxClose - valLevel) / tickPtMm : 0;
                // Prior-day H/L distances — the levels MM defends most
                double distPdHiPts = prevDayHigh > 0 ? (pxClose - prevDayHigh) / tickPtMm : 0;
                double distPdLoPts = prevDayLow  > 0 ? (pxClose - prevDayLow)  / tickPtMm : 0;
                // Renko placeholders — filled by W6 Phase 0.2 plumbing
                // v6 0.2.3: BrickColor/Streak now report NinzaRenko (primary) when available,
                // else stock Renko. Keeps the diag-log format stable while upgrading the source.
                string brickColor = !string.IsNullOrEmpty(lastNrBrickColor) ? lastNrBrickColor
                                    : (lastBrickColor ?? "");
                int brickStreak = !string.IsNullOrEmpty(lastNrBrickColor) ? nrBrickStreakCount
                                    : brickStreakCount;
                // Regime placeholder — filled by F1 in Phase 1
                string regime = currentRegime ?? "";

                diagWriter.WriteLine(string.Format(
                    "{0},{1},{2:F2},{3:F2},{4:F2},{5:F2},{6:F2},{7:F2},{8:F1},{9:F1},{10:F1},{11:F1},{12:F1},{13:F2},{14},{15:F1},"
                    + "{16},{17},{18:F2},{19:F2},{20:F2},{21:F2},{22},{23:F1},{24},{25},{26:F2},"
                    + "{27},{28},{29},{30},{31:F1},{32:F2},{33},{34},{35:F2},{36:F2},"
                    + "{37:F2},{38:F2},{39},{40:F0},{41:F0},{42},{43:F1},"
                    + "{44:F2},{45:F2},{46:F2},{47:F2},{48:F2},"
                    + "{49},{50},{51},"
                    + "{52},{53}",
                    pxTime.ToString("yyyy-MM-dd HH:mm:ss"), pxBar, pxClose, vwapValue,
                    ef, es, rs, at, ax, rawBullConfidence, rawBearConfidence,
                    lastBullConfidence, lastBearConfidence, cachedTapeDelta, htfBias, trapScore,
                    pos, qty, averageEntryPrice, hiddenStopPrice, hiddenTargetPrice, trailPrice,
                    (trailTierName ?? ""), manualTrailOffsetPoints, consecutiveLosses, consecutiveWins,
                    dailyRealizedPnL,
                    mode, stratUsed.Replace(",", ";"), toggles, sigLabel, effMin, distVwapAtr,
                    voidBarsRemaining, openTypeOut, profitPts, peakPts,
                    bidPx, askPx, spreadTk, tapeAskVol, tapeBidVol, emaStack, adxSlope,
                    distPocPts, distVahPts, distValPts, distPdHiPts, distPdLoPts,
                    brickColor, brickStreak, regime,
                    action, (detail ?? "").Replace(",", ";")));
            }
            catch (Exception ex) { Print(TAG + "DiagLog write EX: " + ex.Message); }
        }
        #endregion

        // ===========================================================
        //  PROPERTIES (NinjaTrader UI)
        // ===========================================================
        #region Properties
        // Group 1 — Risk
        [NinjaScriptProperty][Range(1, 500)]
        [Display(Name = "Stop Loss (NQ pts)", Order = 1, GroupName = "1 - Risk")]
        public int SlPoints { get { return slPoints; } set { slPoints = value; } }

        [NinjaScriptProperty][Range(1, 500)]
        [Display(Name = "Take Profit (NQ pts)", Order = 2, GroupName = "1 - Risk")]
        public int TpPoints { get { return tpPoints; } set { tpPoints = value; } }

        [NinjaScriptProperty][Range(500, 20000)]
        [Display(Name = "Max Daily Loss ($)", Order = 3, GroupName = "1 - Risk")]
        public int MaxDailyLossDollars { get { return maxDailyLossDollars; } set { maxDailyLossDollars = value; } }

        [NinjaScriptProperty][Range(500, 50000)]
        [Display(Name = "Daily Profit Target ($)", Order = 4, GroupName = "1 - Risk")]
        public int MaxDailyProfitDollars { get { return maxDailyProfitDollars; } set { maxDailyProfitDollars = value; } }

        [NinjaScriptProperty][Range(0, 999)]
        [Display(Name = "Max Trades Per Day (0=∞)", Order = 5, GroupName = "1 - Risk")]
        public int MaxTradesPerDay { get { return maxTradesPerDay; } set { maxTradesPerDay = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Allow Multiple Entries Per Bar", Order = 8, GroupName = "1 - Risk",
            Description = "When ON (default), a new entry can be taken on the same bar after a previous fill/exit. When OFF, only one entry per bar is allowed.")]
        public bool AllowMultiEntryPerBar { get { return allowMultiEntryPerBar; } set { allowMultiEntryPerBar = value; } }

        [NinjaScriptProperty][Range(1, 10)]
        [Display(Name = "Contracts Per Entry", Order = 6, GroupName = "1 - Risk")]
        public int Contracts { get { return contracts; } set { contracts = value; } }

        [NinjaScriptProperty][Range(1, 20)]
        [Display(Name = "Max Contracts Total", Order = 7, GroupName = "1 - Risk")]
        public int MaxContracts { get { return maxContracts; } set { maxContracts = value; } }

        // Group 2 — Mode
        [NinjaScriptProperty]
        [Display(Name = "Auto Mode", Order = 1, GroupName = "2 - Mode")]
        public bool AutoMode { get { return autoMode; } set { autoMode = value; } }

        [NinjaScriptProperty][Range(0, 2)]
        [Display(Name = "Strategy (0=M+V 1=KLB 2=Auto)", Order = 2, GroupName = "2 - Mode")]
        public int AutoStrategy { get { return autoStrategy; } set { autoStrategy = value; } }

        [NinjaScriptProperty][Range(20, 100)]
        [Display(Name = "Min Signal Confidence (%)", Order = 3, GroupName = "2 - Mode")]
        public double MinSignalConfidence { get { return minSignalConfidence; } set { minSignalConfidence = value; } }

        [NinjaScriptProperty][Range(0, 60)]
        [Display(Name = "Entry Delay (sec)", Order = 4, GroupName = "2 - Mode")]
        public int EntryDelaySeconds { get { return entryDelaySeconds; } set { entryDelaySeconds = value; } }

        // Group 3 — Trail / SL
        [NinjaScriptProperty]
        [Display(Name = "Adaptive Trail Enabled", Order = 1, GroupName = "3 - Trail / SL")]
        public bool TrailEnabled { get { return trailEnabled; } set { trailEnabled = value; } }

        [NinjaScriptProperty][Range(1, 100)]
        [Display(Name = "Trail Activation (pts)", Order = 2, GroupName = "3 - Trail / SL")]
        public int TrailActivationPoints { get { return trailActivationPoints; } set { trailActivationPoints = value; } }

        [NinjaScriptProperty][Range(0.5, 5.0)]
        [Display(Name = "Trail ATR Multiplier", Order = 3, GroupName = "3 - Trail / SL")]
        public double TrailAtrMultiplier { get { return trailAtrMultiplier; } set { trailAtrMultiplier = value; } }

        [NinjaScriptProperty][Range(0, 12)]
        [Display(Name = "Smart Trail Backtrack (ticks)", Order = 4, GroupName = "3 - Trail / SL",
            Description = "When MM stop-hunt detected, allow trail to RELAX up to this many ticks (anti-MM avoidance). 0 = disabled. Default 4.")]
        public int SmartTrailBacktrackTicks { get { return smartTrailBacktrackTicks; } set { smartTrailBacktrackTicks = value; } }

        [NinjaScriptProperty][Range(0.1, 2.0)]
        [Display(Name = "TRL NOW Aggr ATR Factor", Order = 41, GroupName = "3 - Trail / SL",
            Description = "TRL NOW initial trail distance = max(2pt, factor × ATR). Lower = tighter / locks more profit faster but riskier on noise. Default 0.5.")]
        public double AggressiveTrailMaxAtrFactor { get { return aggressiveTrailMaxAtrFactor; } set { aggressiveTrailMaxAtrFactor = value; baseAggressiveTrailFactor = value; } }

        [NinjaScriptProperty][Range(0.5, 5.0)]
        [Display(Name = "Runner ATR Factor", Order = 48, GroupName = "3 - Trail / SL",
            Description = "When Runner Mode is toggled ON via the dashboard (RUN button), trail distance = max(RunnerMinPts, factor × ATR). Higher = wider / lets runners run further. Default 1.5.")]
        public double RunnerAtrFactor { get { return runnerAtrFactor; } set { runnerAtrFactor = value; } }

        [NinjaScriptProperty][Range(1.0, 20.0)]
        [Display(Name = "Runner Min Pts", Order = 49, GroupName = "3 - Trail / SL",
            Description = "Minimum trail distance (points) when Runner Mode is ON. Floor for low-ATR sessions. Default 4.")]
        public double RunnerMinPts { get { return runnerMinPts; } set { runnerMinPts = value; } }

        // ----- Streak-adaptive trail (auto-tighten on N consecutive losses / auto-widen on N consecutive wins) -----
        [NinjaScriptProperty]
        [Display(Name = "Auto-Tighten on Losses", Order = 42, GroupName = "3 - Trail / SL",
            Description = "When N consecutive losing trades occur, multiply TRL NOW Aggr ATR Factor by 'Auto-Tighten Factor' (tightens trail). Default OFF.")]
        public bool AutoTightenOnLosses { get { return autoTightenOnLossesEnabled; } set { autoTightenOnLossesEnabled = value; } }

        [NinjaScriptProperty][Range(1, 10)]
        [Display(Name = "Tighten After N Losses", Order = 43, GroupName = "3 - Trail / SL",
            Description = "Number of CONSECUTIVE losses before auto-tightening trail. Default 2.")]
        public int AutoTightenLossN { get { return autoTightenLossN; } set { autoTightenLossN = value; } }

        [NinjaScriptProperty][Range(0.1, 1.0)]
        [Display(Name = "Auto-Tighten Factor", Order = 44, GroupName = "3 - Trail / SL",
            Description = "Multiplier applied to base TRL NOW factor on loss streak (lower = tighter). Default 0.5.")]
        public double AutoTightenFactor { get { return autoTightenFactor; } set { autoTightenFactor = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Auto-Widen on Wins", Order = 45, GroupName = "3 - Trail / SL",
            Description = "When N consecutive winning trades occur, multiply TRL NOW Aggr ATR Factor by 'Auto-Widen Factor' (loosens trail to let winners run). Default OFF.")]
        public bool AutoWidenOnWins { get { return autoWidenOnWinsEnabled; } set { autoWidenOnWinsEnabled = value; } }

        [NinjaScriptProperty][Range(1, 10)]
        [Display(Name = "Widen After N Wins", Order = 46, GroupName = "3 - Trail / SL",
            Description = "Number of CONSECUTIVE wins before auto-widening trail. Default 3.")]
        public int AutoWidenWinN { get { return autoWidenWinN; } set { autoWidenWinN = value; } }

        [NinjaScriptProperty][Range(1.0, 3.0)]
        [Display(Name = "Auto-Widen Factor", Order = 47, GroupName = "3 - Trail / SL",
            Description = "Multiplier applied to base TRL NOW factor on win streak (higher = looser). Default 1.5.")]
        public double AutoWidenFactor { get { return autoWidenFactor; } set { autoWidenFactor = value; } }

        // ----- ADX-aware prevDay TP-clamp skip -----
        [NinjaScriptProperty]
        [Display(Name = "Skip PrevDay TP Clamp on High ADX", Order = 6, GroupName = "1 - Risk",
            Description = "On strong-trend days (ADX above threshold) DO NOT clamp TP to prior-day H/L — let winners run through pivots. Default ON.")]
        public bool SkipPrevDayClampOnHighAdx { get { return skipPrevDayClampOnHighAdx; } set { skipPrevDayClampOnHighAdx = value; } }

        [NinjaScriptProperty][Range(15.0, 60.0)]
        [Display(Name = "High ADX Threshold", Order = 7, GroupName = "1 - Risk",
            Description = "ADX(14) value above which prevDay TP clamp is skipped. Typical trend regime starts ~25; default 28.")]
        public double HighAdxThreshold { get { return highAdxThreshold; } set { highAdxThreshold = value; } }

        // ----- Reset daily counters on strategy restart -----
        [NinjaScriptProperty]
        [Display(Name = "Reset Daily on Restart", Order = 8, GroupName = "1 - Risk",
            Description = "If ON (default), disabling+re-enabling the strategy clears today's PnL/trades so DAILY LIMIT does not re-trigger from prior fills. Turn OFF to keep persistent daily PnL across restarts.")]
        public bool ResetDailyOnRestart { get { return resetDailyOnRestart; } set { resetDailyOnRestart = value; } }

        // ----- Fast Reversal Exit (anti-MM/algo sweep) -----
        [NinjaScriptProperty]
        [Display(Name = "Fast Reversal Exit (anti-MM)", Order = 9, GroupName = "1 - Risk",
            Description = "Within first N bars after entry, if adverse move > factor×ATR AND 2+ confirms (EMA flip / tape against / vol spike / trap≥40), exit at market BEFORE hidden SL would hit. Cuts loss roughly in half on MM sweeps. Default ON.")]
        public bool FastReversalExitEnabled { get { return fastReversalExitEnabled; } set { fastReversalExitEnabled = value; } }

        [NinjaScriptProperty][Range(0.2, 1.5)]
        [Display(Name = "Fast Reversal ATR Factor", Order = 10, GroupName = "1 - Risk",
            Description = "Adverse move must exceed factor × current ATR before fast-exit can trigger. Lower = more aggressive cut. Default 0.6.")]
        public double FastReversalAtrFactor { get { return fastReversalAtrFactor; } set { fastReversalAtrFactor = value; } }

        [NinjaScriptProperty][Range(1, 12)]
        [Display(Name = "Fast Reversal Max Bars", Order = 11, GroupName = "1 - Risk",
            Description = "Only consider fast-exit within first N bars after entry. After that, normal trail/BE/trap logic governs. Default 4.")]
        public int FastReversalMaxBars { get { return fastReversalMaxBars; } set { fastReversalMaxBars = value; } }

        [NinjaScriptProperty][Range(1.0, 20.0)]
        [Display(Name = "Fast Reversal Min Adverse (pts)", Order = 12, GroupName = "1 - Risk",
            Description = "Hard floor — never fire fast-exit unless adverse is at least this many points (avoids over-reacting to noise). Default 4.")]
        public double FastReversalMinAdversePts { get { return fastReversalAdverseMinPts; } set { fastReversalAdverseMinPts = value; } }

        // ===== Group 8 — Time-of-day SL sizing (customizable windows) =====
        [NinjaScriptProperty]
        [Display(Name = "Enable Time-of-Day SL Sizing", Order = 1, GroupName = "8 - Time-of-Day SL",
            Description = "Multiply base SL by per-window factor. Open/Close = wider, Midday = tighter. Default OFF (uses base SL).")]
        public bool TimeOfDaySlSizingEnabled { get { return timeOfDaySlSizingEnabled; } set { timeOfDaySlSizingEnabled = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Open Window Start (HHMMSS)", Order = 2, GroupName = "8 - Time-of-Day SL")]
        public int SodOpenStart { get { return sodOpenStart; } set { sodOpenStart = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Open Window End (HHMMSS)", Order = 3, GroupName = "8 - Time-of-Day SL")]
        public int SodOpenEnd { get { return sodOpenEnd; } set { sodOpenEnd = value; } }

        [NinjaScriptProperty][Range(0.3, 3.0)]
        [Display(Name = "Open SL Multiplier", Order = 4, GroupName = "8 - Time-of-Day SL",
            Description = "Multiplier applied to base SL during the Open window (default 1.30 = wider).")]
        public double SodOpenSlMult { get { return sodOpenSlMult; } set { sodOpenSlMult = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Midday Window Start (HHMMSS)", Order = 5, GroupName = "8 - Time-of-Day SL")]
        public int SodMiddayStart { get { return sodMiddayStart; } set { sodMiddayStart = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Midday Window End (HHMMSS)", Order = 6, GroupName = "8 - Time-of-Day SL")]
        public int SodMiddayEnd { get { return sodMiddayEnd; } set { sodMiddayEnd = value; } }

        [NinjaScriptProperty][Range(0.3, 3.0)]
        [Display(Name = "Midday SL Multiplier", Order = 7, GroupName = "8 - Time-of-Day SL",
            Description = "Multiplier applied during the Midday window (default 0.80 = tighter, chop).")]
        public double SodMiddaySlMult { get { return sodMiddaySlMult; } set { sodMiddaySlMult = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Close Window Start (HHMMSS)", Order = 8, GroupName = "8 - Time-of-Day SL")]
        public int SodCloseStart { get { return sodCloseStart; } set { sodCloseStart = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Close Window End (HHMMSS)", Order = 9, GroupName = "8 - Time-of-Day SL")]
        public int SodCloseEnd { get { return sodCloseEnd; } set { sodCloseEnd = value; } }

        [NinjaScriptProperty][Range(0.3, 3.0)]
        [Display(Name = "Close SL Multiplier", Order = 10, GroupName = "8 - Time-of-Day SL",
            Description = "Multiplier applied during the Close window (default 1.20 = wider, whipsaw).")]
        public double SodCloseSlMult { get { return sodCloseSlMult; } set { sodCloseSlMult = value; } }

        // ===== Group 9 — News blackout (customizable times) =====
        [NinjaScriptProperty]
        [Display(Name = "Enable News Blackout", Order = 1, GroupName = "9 - News Blackout",
            Description = "Block all entries within ± window-min of any time in News Blackout Times. Default ON.")]
        public bool NewsBlackoutEnabled { get { return newsBlackoutEnabled; } set { newsBlackoutEnabled = value; } }

        [NinjaScriptProperty]
        [Display(Name = "News Blackout Times (CSV HHMMSS)", Order = 2, GroupName = "9 - News Blackout",
            Description = "Comma-separated HHMMSS times. Default '083000,100000,140000' = CPI/PPI 8:30, ISM 10:00, FOMC 14:00 ET.")]
        public string NewsBlackoutTimes
        {
            get { return newsBlackoutTimes; }
            set { newsBlackoutTimes = value; newsBlackoutMinutesCache = null; }
        }

        [NinjaScriptProperty][Range(0, 60)]
        [Display(Name = "News Blackout Window (min)", Order = 3, GroupName = "9 - News Blackout",
            Description = "± minutes around each blackout time during which entries are blocked. Default 2.")]
        public int NewsBlackoutWindowMin { get { return newsBlackoutWindowMin; } set { newsBlackoutWindowMin = value; } }

        // ===== Group 10 — Liquidity sweep (anti-MM bonus) =====
        [NinjaScriptProperty]
        [Display(Name = "Liquidity Sweep Boost", Order = 1, GroupName = "10 - Liquidity Sweep",
            Description = "If prior bar wicked past N-bar high/low then closed back inside, BOOST opposite-direction signals (lower min-confidence and bypass overextension). Default ON.")]
        public bool LiquiditySweepBoostEnabled { get { return liquiditySweepBoostEnabled; } set { liquiditySweepBoostEnabled = value; } }

        [NinjaScriptProperty][Range(5, 200)]
        [Display(Name = "Liquidity Sweep Lookback (bars)", Order = 2, GroupName = "10 - Liquidity Sweep",
            Description = "How many bars to look back for the prior swing high/low. Default 30.")]
        public int LiquiditySweepLookback { get { return liquiditySweepLookback; } set { liquiditySweepLookback = value; } }

        [NinjaScriptProperty][Range(0.0, 30.0)]
        [Display(Name = "Liquidity Sweep Conf Boost", Order = 3, GroupName = "10 - Liquidity Sweep",
            Description = "Points subtracted from required min-signal-confidence when sweep is detected (effective floor 35). Default 8.")]
        public double LiquiditySweepConfBoost { get { return liquiditySweepConfBoost; } set { liquiditySweepConfBoost = value; } }

        // ===== Group 11 — DCA suppression =====
        [NinjaScriptProperty]
        [Display(Name = "Suppress DCA on Loss Streak", Order = 1, GroupName = "11 - DCA Suppression",
            Description = "After N consecutive losses, block same-direction adds to the open position (saves capital on choppy days). Default ON.")]
        public bool SuppressDcaOnLossStreak { get { return suppressDcaOnLossStreak; } set { suppressDcaOnLossStreak = value; } }

        [NinjaScriptProperty][Range(1, 10)]
        [Display(Name = "Suppress DCA After N Losses", Order = 2, GroupName = "11 - DCA Suppression",
            Description = "Consecutive-loss threshold above which DCA adds are blocked. Default 2.")]
        public int SuppressDcaLossN { get { return suppressDcaLossN; } set { suppressDcaLossN = value; } }

        // ===== Group 12 — Aggressive Exits Mode (manual + auto) =====
        [NinjaScriptProperty]
        [Display(Name = "Aggressive Exits Enabled", Order = 1, GroupName = "12 - Aggressive Exits",
            Description = "When ON: BE locks at +AggrBeAtPoints, trail starts at +AggrTrailActivationPts with AggrTrailDistPts distance, and any pullback >= AggrPullbackAtrFactor x ATR within AggrPullbackMaxBars of entry forces a market exit. Default ON (mirrors AGGR dashboard button).")]
        public bool AggressiveExitsEnabled { get { return aggressiveExitsEnabled; } set { aggressiveExitsEnabled = value; } }

        [NinjaScriptProperty][Range(1, 30)]
        [Display(Name = "Aggr BE At Points", Order = 2, GroupName = "12 - Aggressive Exits",
            Description = "While AGGR ON, lock breakeven once profit >= this many points. Default 3.")]
        public int AggrBeAtPoints { get { return aggrBeAtPoints; } set { aggrBeAtPoints = value; } }

        [NinjaScriptProperty][Range(1.0, 30.0)]
        [Display(Name = "Aggr Trail Activation (pts)", Order = 3, GroupName = "12 - Aggressive Exits",
            Description = "While AGGR ON, trail activates once profit >= this many points. Default 4.")]
        public double AggrTrailActivationPts { get { return aggrTrailActivationPts; } set { aggrTrailActivationPts = value; } }

        [NinjaScriptProperty][Range(0.5, 20.0)]
        [Display(Name = "Aggr Trail Distance (pts)", Order = 4, GroupName = "12 - Aggressive Exits",
            Description = "While AGGR ON, trail follows price by this many points. Default 2.")]
        public double AggrTrailDistPts { get { return aggrTrailDistPts; } set { aggrTrailDistPts = value; } }

        [NinjaScriptProperty][Range(0.1, 2.0)]
        [Display(Name = "Aggr Pullback (x ATR)", Order = 5, GroupName = "12 - Aggressive Exits",
            Description = "While AGGR ON, if profit pulls back this many x ATR from peak (within AggrPullbackMaxBars), exit at market. Default 0.4.")]
        public double AggrPullbackAtrFactor { get { return aggrPullbackAtrFactor; } set { aggrPullbackAtrFactor = value; } }

        [NinjaScriptProperty][Range(1, 30)]
        [Display(Name = "Aggr Pullback Max Bars", Order = 6, GroupName = "12 - Aggressive Exits",
            Description = "Pullback exit only fires within this many bars after entry. Default 2.")]
        public int AggrPullbackMaxBars { get { return aggrPullbackMaxBars; } set { aggrPullbackMaxBars = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Aggr Adverse Exit Enabled", Order = 7, GroupName = "12 - Aggressive Exits",
            Description = "OPT-IN, DEFAULT OFF. Tested ON and produced WORSE results than letting the static SL handle losses, because 1-min bars on a high-ATR NQ morning routinely have 10-15pt intrabar wicks that fire the adverse trigger on the entry bar's first tick. Now requires bar 1+ after entry (skips entry-bar noise). If enabling, recommend AdverseAtrFactor=0.6, MinPts=8, DisarmPeak=4.")]
        public bool AggrAdverseExitEnabled { get { return aggrAdverseExitEnabled; } set { aggrAdverseExitEnabled = value; } }

        [NinjaScriptProperty][Range(1, 10)]
        [Display(Name = "Aggr Adverse Max Bars", Order = 8, GroupName = "12 - Aggressive Exits",
            Description = "Adverse exit only fires within this many bars after entry. Default 2.")]
        public int AggrAdverseMaxBars { get { return aggrAdverseMaxBars; } set { aggrAdverseMaxBars = value; } }

        [NinjaScriptProperty][Range(0.1, 2.0)]
        [Display(Name = "Aggr Adverse (x ATR)", Order = 9, GroupName = "12 - Aggressive Exits",
            Description = "Adverse excursion threshold as multiple of ATR. Default 0.5. Used only if AggrAdverseExitEnabled is ON.")]
        public double AggrAdverseAtrFactor { get { return aggrAdverseAtrFactor; } set { aggrAdverseAtrFactor = value; } }

        [NinjaScriptProperty][Range(1.0, 20.0)]
        [Display(Name = "Aggr Adverse Min (pts)", Order = 10, GroupName = "12 - Aggressive Exits",
            Description = "Minimum adverse points to trigger (floor). Default 5.0. Used only if AggrAdverseExitEnabled is ON.")]
        public double AggrAdverseMinPts { get { return aggrAdverseMinPts; } set { aggrAdverseMinPts = value; } }

        [NinjaScriptProperty][Range(0.5, 20.0)]
        [Display(Name = "Aggr Adverse Disarm Peak (pts)", Order = 11, GroupName = "12 - Aggressive Exits",
            Description = "Once trade peak profit reaches this many points, the adverse-exit is disarmed for this trade and AGGR_PULLBACK / trail take over. Default 3.0 (lets short blips disarm noise-only entries while still protecting fast-collapsing trades).")]
        public double AggrAdverseDisarmPeak { get { return aggrAdverseDisarmPeak; } set { aggrAdverseDisarmPeak = value; } }

        [NinjaScriptProperty][Range(0.0, 50.0)]
        [Display(Name = "Aggr SL Cap (pts, 0=off)", Order = 12, GroupName = "12 - Aggressive Exits",
            Description = "When AGGR ON, the static stop-loss distance is capped at this many points. Default 0 (DISABLED). Testing showed that capping below 18pt on NQ kills trend trades on first-bar wicks. The AGGR adverse + pullback exits already handle capital protection. Only enable (>=18) if you want a hard catastrophe brake.")]
        public double AggrSlCapPoints { get { return aggrSlCapPoints; } set { aggrSlCapPoints = value; } }

        // ===== Group 13 — Chop Filter (manual + auto) =====
        [NinjaScriptProperty]
        [Display(Name = "Chop Filter Enabled", Order = 1, GroupName = "13 - Chop Filter",
            Description = "Block ALL entries when ADX collapses, EMAs converge, close-range tightens, or tape opposes direction. Applies to manual AND auto. Default ON (mirrors CHOP dashboard button).")]
        public bool ChopFilterEnabled { get { return chopFilterEnabled; } set { chopFilterEnabled = value; } }

        [NinjaScriptProperty][Range(5.0, 40.0)]
        [Display(Name = "Chop ADX Min", Order = 2, GroupName = "13 - Chop Filter",
            Description = "ADX below this threshold counts as chop. Default 18.")]
        public double ChopAdxMin { get { return chopAdxMin; } set { chopAdxMin = value; } }

        [NinjaScriptProperty][Range(1, 10)]
        [Display(Name = "Chop ADX Falling Bars", Order = 3, GroupName = "13 - Chop Filter",
            Description = "ADX must be falling for this many consecutive bars to trigger ADX-based chop. Default 3.")]
        public int ChopAdxFallingBars { get { return chopAdxFallingBars; } set { chopAdxFallingBars = value; } }

        [NinjaScriptProperty][Range(0.05, 2.0)]
        [Display(Name = "Chop EMA Sep Min (x ATR)", Order = 4, GroupName = "13 - Chop Filter",
            Description = "If |EmaFast - EmaSlow| < this x ATR, no trend separation = chop. Default 0.30.")]
        public double ChopEmaSepMinAtr { get { return chopEmaSepMinAtr; } set { chopEmaSepMinAtr = value; } }

        [NinjaScriptProperty][Range(2, 30)]
        [Display(Name = "Chop Range Bars", Order = 5, GroupName = "13 - Chop Filter",
            Description = "How many bars to measure close-range over. Default 5.")]
        public int ChopRangeBars { get { return chopRangeBars; } set { chopRangeBars = value; } }

        [NinjaScriptProperty][Range(0.2, 5.0)]
        [Display(Name = "Chop Range Max (x ATR)", Order = 6, GroupName = "13 - Chop Filter",
            Description = "If close-range over ChopRangeBars < this x ATR, market is choppy. Default 1.0.")]
        public double ChopRangeMaxAtr { get { return chopRangeMaxAtr; } set { chopRangeMaxAtr = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Chop Block Opposite Tape", Order = 7, GroupName = "13 - Chop Filter",
            Description = "Block entry when realtime tape sign opposes the entry direction by >= ChopOppositeTapeMin. Default ON.")]
        public bool ChopBlockOppositeTape { get { return chopBlockOppositeTape; } set { chopBlockOppositeTape = value; } }

        [NinjaScriptProperty][Range(0.0, 1.0)]
        [Display(Name = "Chop Opposite Tape Min |delta|", Order = 8, GroupName = "13 - Chop Filter",
            Description = "Minimum |tape delta| to count as opposite-direction tape. Default 0.05.")]
        public double ChopOppositeTapeMin { get { return chopOppositeTapeMin; } set { chopOppositeTapeMin = value; } }

        [NinjaScriptProperty][Range(10.0, 60.0)]
        [Display(Name = "Chop Trend ADX Gate", Order = 9, GroupName = "13 - Chop Filter",
            Description = "When ADX >= this value, the EMA-gap and close-range chop tests are SKIPPED (treats tight EMAs / small ranges as consolidation inside a trend, not chop). The ADX-collapse and opposite-tape tests still apply. Default 22 — set higher (e.g. 30) to be more cautious in weak trends, lower (e.g. 18) to allow more entries.")]
        public double ChopTrendAdxGate { get { return chopTrendAdxGate; } set { chopTrendAdxGate = value; } }

        // ===== Group 14 — Adaptive Intra-Day Window =====
        [NinjaScriptProperty]
        [Display(Name = "Adaptive Window Enabled", Order = 1, GroupName = "14 - Adaptive Window",
            Description = "Track last N trade outcomes; if >= LossThreshold losses, require AdaptiveConfBoost more confidence until cleared by N wins. Default ON (mirrors ADAPT dashboard button).")]
        public bool AdaptiveWindowEnabled { get { return adaptiveWindowEnabled; } set { adaptiveWindowEnabled = value; if (!value) adaptiveTightenActive = false; } }

        [NinjaScriptProperty][Range(2, 20)]
        [Display(Name = "Adaptive Window Size", Order = 2, GroupName = "14 - Adaptive Window",
            Description = "Rolling number of trade outcomes to track. Default 5.")]
        public int AdaptiveWindowSize
        {
            get { return adaptiveWindowSize; }
            set { adaptiveWindowSize = value; recentTradeOutcomes = null; }
        }

        [NinjaScriptProperty][Range(1, 20)]
        [Display(Name = "Adaptive Loss Threshold", Order = 3, GroupName = "14 - Adaptive Window",
            Description = "Activate tighten mode when losses-in-window reach this count. Default 3.")]
        public int AdaptiveWindowLossThreshold { get { return adaptiveWindowLossThreshold; } set { adaptiveWindowLossThreshold = value; } }

        [NinjaScriptProperty][Range(1, 20)]
        [Display(Name = "Adaptive Clear-Wins Required", Order = 4, GroupName = "14 - Adaptive Window",
            Description = "How many consecutive wins clear tighten mode. Default 2.")]
        public int AdaptiveWindowClearWins { get { return adaptiveWindowClearWins; } set { adaptiveWindowClearWins = value; } }

        [NinjaScriptProperty][Range(0, 50)]
        [Display(Name = "Adaptive Confidence Boost", Order = 5, GroupName = "14 - Adaptive Window",
            Description = "Points added to required min-confidence while tighten is active. Default 5.")]
        public int AdaptiveConfBoost { get { return adaptiveConfBoost; } set { adaptiveConfBoost = value; } }

        // ===== Group 15 — SL Cluster Cooldown =====
        [NinjaScriptProperty]
        [Display(Name = "SL Cluster Cooldown Enabled", Order = 1, GroupName = "15 - SL Cluster Cooldown",
            Description = "After SlClusterCount stop-loss exits within SlClusterWindowMin minutes, block new entries for SlClusterCooldownMin minutes. Detects 'toxic regime' periods where the strategy is being stop-hunted and pauses to let conditions reset. Default ON.")]
        public bool SlClusterCooldownEnabled { get { return slClusterCooldownEnabled; } set { slClusterCooldownEnabled = value; } }

        [NinjaScriptProperty][Range(2, 10)]
        [Display(Name = "SL Cluster Count", Order = 2, GroupName = "15 - SL Cluster Cooldown",
            Description = "Number of SL exits within window that triggers cooldown. Default 2.")]
        public int SlClusterCount { get { return slClusterCount; } set { slClusterCount = value; if (slExitTimes != null && slExitTimes.Length != value) { slExitTimes = null; } } }

        [NinjaScriptProperty][Range(5, 240)]
        [Display(Name = "SL Cluster Window (min)", Order = 3, GroupName = "15 - SL Cluster Cooldown",
            Description = "Sliding window in minutes used to count recent SL exits. Default 90.")]
        public int SlClusterWindowMin { get { return slClusterWindowMin; } set { slClusterWindowMin = value; } }

        [NinjaScriptProperty][Range(1, 120)]
        [Display(Name = "SL Cluster Cooldown (min)", Order = 4, GroupName = "15 - SL Cluster Cooldown",
            Description = "Block new entries for this many minutes after the cluster trigger fires. Default 25.")]
        public int SlClusterCooldownMin { get { return slClusterCooldownMin; } set { slClusterCooldownMin = value; } }

        // ===== Group 16 — Post-Win Same-Direction Cooldown (anti trail-and-trap) =====
        [NinjaScriptProperty]
        [Display(Name = "Post-Win Same-Dir Cooldown Enabled", Order = 1, GroupName = "16 - Post-Win Cooldown",
            Description = "After a winning exit, block same-direction re-entries for PostWinSameDirCooldownMin minutes. Defends against the MM trail-and-trap: trail kicks out for small profit, MM ramps price against to clear stops, we re-enter and get hunted. Default ON.")]
        public bool PostWinSameDirCooldownEnabled { get { return postWinSameDirCooldownEnabled; } set { postWinSameDirCooldownEnabled = value; } }

        [NinjaScriptProperty][Range(1, 60)]
        [Display(Name = "Post-Win Cooldown (min)", Order = 2, GroupName = "16 - Post-Win Cooldown",
            Description = "Minutes to block same-direction re-entry after a winning exit. Default 7 (covers the typical 5-6 minute MM trail-and-trap re-entry trap).")]
        public int PostWinSameDirCooldownMin { get { return postWinSameDirCooldownMin; } set { postWinSameDirCooldownMin = value; } }

        // ===== Group 17 — Directional Lockout (per-direction loss-streak block) =====
        [NinjaScriptProperty]
        [Display(Name = "Directional Lockout Enabled", Order = 1, GroupName = "17 - Directional Lockout",
            Description = "After N losing SL/AGGR_ADVERSE exits in the SAME direction within DirLockoutWindowMin minutes, block that direction for DirLockoutCooldownMin minutes. Opposite direction stays allowed (so a true reversal can be taken). Surgical defense against the MM pattern of stop-running repeated continuation entries chasing fresh local extremes. Default ON.")]
        public bool DirLockoutEnabled { get { return dirLockoutEnabled; } set { dirLockoutEnabled = value; } }

        [NinjaScriptProperty][Range(2, 10)]
        [Display(Name = "Dir Lockout Loss N", Order = 2, GroupName = "17 - Directional Lockout",
            Description = "Number of same-direction SL/AGGR_ADVERSE losses within window that triggers lockout. Default 2.")]
        public int DirLockoutLossN { get { return dirLockoutLossN; } set { dirLockoutLossN = value; } }

        [NinjaScriptProperty][Range(5, 240)]
        [Display(Name = "Dir Lockout Window (min)", Order = 3, GroupName = "17 - Directional Lockout",
            Description = "Sliding window in minutes used to count same-direction losses. Default 30.")]
        public int DirLockoutWindowMin { get { return dirLockoutWindowMin; } set { dirLockoutWindowMin = value; } }

        [NinjaScriptProperty][Range(5, 240)]
        [Display(Name = "Dir Lockout Cooldown (min)", Order = 4, GroupName = "17 - Directional Lockout",
            Description = "Block this direction for this many minutes after the lockout fires. Default 30.")]
        public int DirLockoutCooldownMin { get { return dirLockoutCooldownMin; } set { dirLockoutCooldownMin = value; } }

        // ===== Group 18 — Extension Filter (don't chase late entries) =====
        [NinjaScriptProperty]
        [Display(Name = "Extension Filter Enabled", Order = 1, GroupName = "18 - Extension Filter",
            Description = "Block new entries when price is already over-extended from VWAP relative to ATR. Empirically the best entries on trend days are taken at 2-5×ATR from VWAP (the breakout); entries at >5×ATR are late chases that the MM stop-runs before the trend resumes. Default ON.")]
        public bool ExtensionFilterEnabled { get { return extensionFilterEnabled; } set { extensionFilterEnabled = value; } }

        [NinjaScriptProperty][Range(2.0, 15.0)]
        [Display(Name = "Extension Max (xATR from VWAP)", Order = 2, GroupName = "18 - Extension Filter",
            Description = "Block entry when |close - VWAP| / ATR exceeds this ratio. Lower = more conservative (skips more late chases but may also skip strong trend continuations). Default 5.0.")]
        public double ExtensionMaxAtrFromVwap { get { return extensionMaxAtrFromVwap; } set { extensionMaxAtrFromVwap = value; } }

        [NinjaScriptProperty][Range(1.0, 50.0)]
        [Display(Name = "Extension Min ATR (pts)", Order = 3, GroupName = "18 - Extension Filter",
            Description = "Only enforce extension filter when ATR is at least this many points. Avoids over-blocking quiet midday sessions where 5×ATR is a normal price distance from VWAP. Default 8.0.")]
        public double ExtensionMinAtrPoints { get { return extensionMinAtrPoints; } set { extensionMinAtrPoints = value; } }

        [NinjaScriptProperty][Range(2, 50)]
        [Display(Name = "Breakeven At (pts)", Order = 5, GroupName = "3 - Trail / SL")]
        public int BreakevenAtPoints { get { return breakevenAtPoints; } set { breakevenAtPoints = value; } }

        [NinjaScriptProperty][Range(0.0, 1.5)]
        [Display(Name = "BE Safe ATR Factor", Order = 51, GroupName = "3 - Trail / SL",
            Description = "Smart BE safety: SL is forced to stay at least (factor×ATR) below current price so MM stop-hunts can't tag it. Default 0.35.")]
        public double BeSafeAtrFactor { get { return beSafeAtrFactor; } set { beSafeAtrFactor = value; } }

        [NinjaScriptProperty][Range(2, 40)]
        [Display(Name = "BE Safe Min Ticks", Order = 52, GroupName = "3 - Trail / SL",
            Description = "Hard floor for BE distance from current price (in ticks). Even if ATR is tiny, SL stays at least this far. Default 6 ticks (1.5pt).")]
        public int BeSafeMinTicks { get { return beSafeMinTicks; } set { beSafeMinTicks = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Breakeven Lock Enabled", Order = 5, GroupName = "3 - Trail / SL",
            Description = "Master ON/OFF for the auto break-even SL move. When OFF, SL stays at original until trail or hard SL hit. Dashboard BE button mirrors this.")]
        public bool BreakevenEnabled { get { return breakevenEnabled; } set { breakevenEnabled = value; } }

        [NinjaScriptProperty][Range(10, 95)]
        [Display(Name = "Jump SL %", Order = 6, GroupName = "3 - Trail / SL")]
        public int JumpSlPercent { get { return jumpSlPercent; } set { jumpSlPercent = value; } }

        [NinjaScriptProperty][Range(1, 50)]
        [Display(Name = "SL/TP Adjust Step (pts)", Order = 7, GroupName = "3 - Trail / SL")]
        public int SlTpAdjustStep { get { return slTpAdjustStep; } set { slTpAdjustStep = value; } }

        // Group 4 — Smart Logic
        [NinjaScriptProperty]
        [Display(Name = "Trap Detector Enabled", Order = 1, GroupName = "4 - Smart Logic")]
        public bool EnableTrapDetector { get { return enableTrapDetector; } set { enableTrapDetector = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Order-Flow Tape Filter (live only)", Order = 2, GroupName = "4 - Smart Logic",
            Description = "When ON, blocks entries against aggressive tape (live data only; no effect in backtest). Lightweight — recommended ON.")]
        public bool OrderFlowFilterEnabled { get { return orderFlowFilterEnabled; } set { orderFlowFilterEnabled = value; } }

        // Group 5 — Indicators
        [NinjaScriptProperty][Range(3, 50)]
        [Display(Name = "EMA Fast", Order = 1, GroupName = "5 - Indicators")]
        public int EmaPeriodFast { get { return emaPeriodFast; } set { emaPeriodFast = value; } }

        [NinjaScriptProperty][Range(10, 200)]
        [Display(Name = "EMA Slow", Order = 2, GroupName = "5 - Indicators")]
        public int EmaPeriodSlow { get { return emaPeriodSlow; } set { emaPeriodSlow = value; } }

        [NinjaScriptProperty][Range(5, 30)]
        [Display(Name = "RSI", Order = 3, GroupName = "5 - Indicators")]
        public int RsiPeriod { get { return rsiPeriod; } set { rsiPeriod = value; } }

        [NinjaScriptProperty][Range(5, 30)]
        [Display(Name = "ATR", Order = 4, GroupName = "5 - Indicators")]
        public int AtrPeriod { get { return atrPeriod; } set { atrPeriod = value; } }

        [NinjaScriptProperty][Range(10, 200)]
        [Display(Name = "HTF EMA", Order = 5, GroupName = "5 - Indicators")]
        public int HtfEmaPeriod { get { return htfEmaPeriod; } set { htfEmaPeriod = value; } }

        // Group 6 — Hours
        [NinjaScriptProperty]
        [Display(Name = "Trading Hours Filter", Order = 1, GroupName = "6 - Hours")]
        public bool TradingHoursEnabled { get { return tradingHoursEnabled; } set { tradingHoursEnabled = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Trading Start (HHMMSS)", Order = 2, GroupName = "6 - Hours")]
        public int TradingStartTime { get { return tradingStartTime; } set { tradingStartTime = value; } }

        [NinjaScriptProperty][Range(0, 235959)]
        [Display(Name = "Auto-Flatten Time (HHMMSS)", Order = 3, GroupName = "6 - Hours")]
        public int FlattenTime { get { return flattenTime; } set { flattenTime = value; } }

        // Group 7 — Display
        [NinjaScriptProperty]
        [Display(Name = "Show EMA on chart", Order = 1, GroupName = "7 - Display")]
        public bool ShowEma { get { return showEma; } set { showEma = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Show VWAP line", Order = 2, GroupName = "7 - Display")]
        public bool ShowVwap { get { return showVwap; } set { showVwap = value; } }

        // Group 8 — Diagnostics
        [NinjaScriptProperty]
        [Display(Name = "Enable Diagnostic Log", Order = 1, GroupName = "8 - Diagnostics")]
        public bool EnableDiagLog { get { return enableDiagLog; } set { enableDiagLog = value; } }

        // Group 9 — Renko (v6 Phase 0.2 plumbing — data only, no entry logic yet)
        [NinjaScriptProperty]
        [Display(Name = "Enable Renko Series (64/16)", Order = 1, GroupName = "9 - Renko",
            Description = "Adds a 64-tick / 16-offset Renko secondary series for MM-analysis (BarsArray[2]). Brick color & streak appear in the diag log. No entry/exit logic uses this yet — that ships in Phase 2.1 (W6 Renko thrust mode). Disable to reduce CPU load.")]
        public bool EnableRenkoSeries { get { return enableRenkoSeries; } set { enableRenkoSeries = value; } }

        [NinjaScriptProperty][Range(4, 256)]
        [Display(Name = "Renko Brick Size (ticks)", Order = 2, GroupName = "9 - Renko")]
        public int RenkoBrickSize { get { return renkoBrickSize; } set { renkoBrickSize = value; } }

        [NinjaScriptProperty][Range(0, 256)]
        [Display(Name = "Renko Reversal Offset (ticks)", Order = 3, GroupName = "9 - Renko")]
        public int RenkoBrickOffset { get { return renkoBrickOffset; } set { renkoBrickOffset = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Enable NinzaRenko Series (third-party)", Order = 4, GroupName = "9 - Renko",
            Description = "Adds NinzaRenko (third-party) on BarsArray[3] using the Custom slot below. This is the PRIMARY brick signal used by the dashboard and diag log. Stock Renko (above) is kept as a sanity comparison. If NinzaRenko is not installed, leave OFF.")]
        public bool EnableNinzaRenkoSeries { get { return enableNinzaRenkoSeries; } set { enableNinzaRenkoSeries = value; } }

        [NinjaScriptProperty][Range(0, 9999)]
        [Display(Name = "NinzaRenko BarsPeriodType (int)", Order = 5, GroupName = "9 - Renko",
            Description = "Raw integer value of the BarsPeriodType under which NinzaRenko is registered on this NT8 install. Default 0 will likely fail — watch the NT8 Output window for the FAILED message, then try common values (100, 101, 102, ...). Once 'NinzaRenko series ALIVE (BarsPeriodType int=N)' prints, save N here.")]
        public int NinzaCustomSlot { get { return ninzaCustomSlot; } set { ninzaCustomSlot = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Use Primary Chart AS NinzaRenko", Order = 6, GroupName = "9 - Renko",
            Description = "When ON (default), the strategy reads NinzaRenko brick color/streak directly from the primary chart series — use this when your chart bar type is already NinzaRenko (no AddDataSeries needed). When OFF, the strategy will try to AddDataSeries using NinzaCustomSlot above.")]
        public bool UsePrimaryAsNinzaRenko { get { return usePrimaryAsNinzaRenko; } set { usePrimaryAsNinzaRenko = value; } }

        // ===== v6 1.1 — F1 Regime Classifier =====
        [NinjaScriptProperty]
        [Display(Name = "Enable Regime Classifier (label-only)", Order = 1, GroupName = "10 - Regime",
            Description = "When ON, classifies each bar as TREND_UP / TREND_DN / CHOP / SQUEEZE / UNKNOWN based on ADX, ATR, EMA stack, NinzaRenko streak, and distance from VWAP. Result is logged in the diag CSV 'Regime' column. NO entry/exit logic uses this yet — it's pure observation. Default OFF; turn ON to start collecting regime labels for post-trade analysis.")]
        public bool EnableRegimeClassifier { get { return enableRegimeClassifier; } set { enableRegimeClassifier = value; } }

        // ===== v6 1.2/1.3/1.4 — Brick Analytics (Run Tracker + Wick + MM Pattern) =====
        [NinjaScriptProperty]
        [Display(Name = "Enable Brick Analytics (label-only)", Order = 2, GroupName = "10 - Regime",
            Description = "When ON, tracks NinzaRenko run length / favorable excursion / wick-rejection / brick-speed and emits RUN_END, WICK_TAG, MM_PATTERN diag rows. Adds 'Run:' row to dashboard. NO entry/exit logic uses these yet — pure observation feeding Phase 2.0 'Brick Mode' design. Default OFF.")]
        public bool EnableBrickAnalytics { get { return enableBrickAnalytics; } set { enableBrickAnalytics = value; } }

        // ===== v6 2.1 — Smart Cooldown (Beat-the-MM block bypass, ACTIVE LOGIC) =====
        [NinjaScriptProperty]
        [Display(Name = "Enable Smart Cooldown (TREND bypass)", Order = 10, GroupName = "10 - Regime",
            Description = "PHASE 2.1 — ACTIVE LOGIC. Default OFF. When ON, the 7-min post-win cooldown is bypassed (down to 60s) ONLY when all of: (1) Regime Classifier says TREND_UP/DN matching trade dir, (2) NinzaRenko brick streak >= 4 in trend color, (3) ADX slope rising, (4) at least 60s since the win. Lets you re-board confirmed trends after a winning scalp instead of sitting in cooldown while a 50pt runner takes off (validated 2026-04-28: 5 consecutive BLOCK_POST_WIN at 09:55 covered ~50pt missed). REQUIRES EnableRegimeClassifier=ON and EnableBrickAnalytics=ON. Watch for SMART_COOLDOWN_BYPASS rows in the diag CSV. Disable if you see consecutive losses after bypass (means regime label is wrong). DO NOT enable on choppy/low-ADX days.")]
        public bool EnableSmartCooldown { get { return enableSmartCooldown; } set { enableSmartCooldown = value; } }

        [NinjaScriptProperty, Range(15, 600)]
        [Display(Name = "  Smart Cooldown Min Seconds", Order = 11, GroupName = "10 - Regime",
            Description = "Minimum seconds since last win before Smart Cooldown bypass can fire. Default 60. Lower = more aggressive re-entry. Only used when EnableSmartCooldown=ON.")]
        public int SmartCooldownMinSec { get { return smartCooldownMinSec; } set { smartCooldownMinSec = value; } }

        [NinjaScriptProperty, Range(2, 20)]
        [Display(Name = "  Smart Cooldown Streak Min", Order = 12, GroupName = "10 - Regime",
            Description = "Minimum NinzaRenko brick streak (in trend direction) required for Smart Cooldown bypass. Default 4. Higher = more selective (fewer but stronger re-entries). Only used when EnableSmartCooldown=ON.")]
        public int SmartCooldownStreakMin { get { return smartCooldownStreakMin; } set { smartCooldownStreakMin = value; } }

        // ===== v6 2.2 — Extension TREND-Bypass (Beat-the-MM block bypass, ACTIVE LOGIC) =====
        [NinjaScriptProperty]
        [Display(Name = "Enable Extension TREND Bypass", Order = 20, GroupName = "10 - Regime",
            Description = "PHASE 2.2 — ACTIVE LOGIC. Default OFF. When ON, the 'price too far from VWAP' extension block is bypassed ONLY when all of: (1) Regime Classifier says TREND_UP/DN matching trade dir, (2) NinzaRenko brick color matches dir, (3) brick streak <= 8 (skip if already monstrous — mean reversion risk), (4) ADX slope rising. Lets you follow strong trends instead of sitting out (validated 2026-04-28: 13 BLOCK_EXTENSION events in TREND_DN). REQUIRES EnableRegimeClassifier=ON. Watch for EXTENSION_BYPASS rows in the diag CSV. Disable if late-entries get stopped repeatedly (means trend was already exhausted).")]
        public bool EnableExtensionTrendBypass { get { return enableExtensionTrendBypass; } set { enableExtensionTrendBypass = value; } }

        [NinjaScriptProperty, Range(3, 30)]
        [Display(Name = "  Extension Bypass Max Streak", Order = 21, GroupName = "10 - Regime",
            Description = "Maximum NinzaRenko brick streak that still allows extension bypass. Default 8. Above this, the trend is likely exhausted and reversal risk dominates. Only used when EnableExtensionTrendBypass=ON.")]
        public int ExtensionBypassStreakMax { get { return extensionBypassStreakMax; } set { extensionBypassStreakMax = value; } }

        // ===== v6 2.3 — Brick-Trail Mode (lets monster runs run, ACTIVE LOGIC) =====
        [NinjaScriptProperty]
        [Display(Name = "Enable Brick-Trail Mode", Order = 30, GroupName = "10 - Regime",
            Description = "PHASE 2.3 — ACTIVE LOGIC. Default OFF. When ON and ALL of (1) brick color agrees with trade dir, (2) brick streak >= BrickTrailMinStreak, (3) regime is TREND matching dir (if BrickTrailRequireTrendRegime=ON), (4) trade is in profit beyond activation — the trail is anchored to the previous CLOSED brick's far extreme + buffer ticks. Each new same-direction brick ratchets the trail one brick. First opposite-color brick pierces the trail = clean exit on actual reversal (not a wick). VALIDATED on 2026-04-28 WIN#2: legacy trail exited at +$400 (brick #15) but run continued to brick #30 with maxFav=132pt = ~$2640 missed. Brick-trail would have ridden the full move. REQUIRES EnableRegimeClassifier=ON. Watch for BRICK_TRAIL_ARMED + BRICK_TRAIL_HIT diag rows. DO NOT enable in chop — it gives back more on reversals.")]
        public bool EnableBrickTrail { get { return enableBrickTrail; } set { enableBrickTrail = value; } }

        [NinjaScriptProperty, Range(2, 20)]
        [Display(Name = "  Brick-Trail Min Streak", Order = 31, GroupName = "10 - Regime",
            Description = "Minimum NinzaRenko brick streak before Brick-Trail Mode engages. Default 4. Lower = engages sooner (catches more runners but more whipsaw on small runs). Higher = engages only on confirmed runners (safer but misses early-run profit).")]
        public int BrickTrailMinStreak { get { return brickTrailMinStreak; } set { brickTrailMinStreak = value; } }

        [NinjaScriptProperty, Range(1, 20)]
        [Display(Name = "  Brick-Trail Buffer Ticks", Order = 32, GroupName = "10 - Regime",
            Description = "Ticks above prev brick HIGH (SHORT) or below prev brick LOW (LONG) for the trail price. Default 4 (=1 NQ point). Lower = tighter (more wick stops). Higher = looser (gives back more on reversal).")]
        public double BrickTrailBufferTicks { get { return brickTrailBufferTicks; } set { brickTrailBufferTicks = value; } }

        [NinjaScriptProperty]
        [Display(Name = "  Brick-Trail Require TREND Regime", Order = 33, GroupName = "10 - Regime",
            Description = "When ON (default), Brick-Trail Mode only engages when currentRegime is TREND_DN (SHORT) or TREND_UP (LONG). Turn OFF to let brick-trail engage in UNKNOWN regimes too — riskier but catches runs the classifier doesn't label.")]
        public bool BrickTrailRequireTrendRegime { get { return brickTrailRequireTrendRegime; } set { brickTrailRequireTrendRegime = value; } }

        [NinjaScriptProperty, Range(0, 100)]
        [Display(Name = "  Brick-Trail Max Giveback %", Order = 34, GroupName = "10 - Regime",
            Description = "PHASE 2.6.1 — Brick-Trail v2 giveback safety. DEFAULT 0 (DISABLED — recommended for monster runs). When > 0, exits if profit pulls back from peak by this percent — but ONLY if BOTH guards pass: (a) peak >= BrickTrailGivebackMinPeakPts, (b) no new same-direction brick in last BrickTrailGivebackStallSec seconds. Set to 0 to rely purely on brick-flip exit (max ride = full color-streak). Try 60-70 if you see big runs that stall and reverse without flipping (rare). NOTE: brick-trail v2 IGNORES price-tick wicks for exit — only opposite-color brick CLOSE or this giveback can exit a brick-trail position.")]
        public int BrickTrailMaxGivebackPct { get { return brickTrailMaxGivebackPct; } set { brickTrailMaxGivebackPct = value; } }

        [NinjaScriptProperty, Range(5, 100)]
        [Display(Name = "  Brick-Trail Giveback Min Peak (pts)", Order = 35, GroupName = "10 - Regime",
            Description = "Giveback safety only fires after peak profit reaches this many points. Default 20. Prevents whip-sawing small winners on ordinary 5-10pt consolidations. Only used when BrickTrailMaxGivebackPct > 0.")]
        public double BrickTrailGivebackMinPeakPts { get { return brickTrailGivebackMinPeakPts; } set { brickTrailGivebackMinPeakPts = value; } }

        [NinjaScriptProperty, Range(10, 600)]
        [Display(Name = "  Brick-Trail Giveback Stall Sec", Order = 36, GroupName = "10 - Regime",
            Description = "Giveback safety STALL-GATE — only fires after this many seconds since the last same-direction brick closed. Default 45. While new same-color bricks are still printing, the run is alive and giveback is suppressed. Only used when BrickTrailMaxGivebackPct > 0.")]
        public int BrickTrailGivebackStallSec { get { return brickTrailGivebackStallSec; } set { brickTrailGivebackStallSec = value; } }

        [NinjaScriptProperty, Range(5, 100)]
        [Display(Name = "  Brick-Trail Profit-Lock Min Peak (pts)", Order = 37, GroupName = "10 - Regime",
            Description = "PHASE 2.6.2 — Wick-immune profit-lock at BRICK CLOSE. Once peak profit reaches this many points, every new brick close checks the profit-lock floor. Default 20. Below this peak, only brick-flip exit is active.")]
        public double BrickTrailProfitLockMinPeakPts { get { return brickTrailProfitLockMinPeakPts; } set { brickTrailProfitLockMinPeakPts = value; } }

        [NinjaScriptProperty, Range(0, 80)]
        [Display(Name = "  Brick-Trail Profit-Lock Giveback %", Order = 38, GroupName = "10 - Regime",
            Description = "PHASE 2.6.2 — % of peak profit allowed to bleed at BRICK CLOSE before exiting. Default 0 = DISABLED (v6 2.6.3 disabled by default; was killing monster runs because peak is tick-based and brick-close profit is body-based — mismatch caused premature exits on continuation bricks). The intended V-reversal use case is now better handled by In-Bar Trail (Order 39+). Wick-immune (uses brick CLOSE price, not tick).")]
        public int BrickTrailProfitLockGivebackPct { get { return brickTrailProfitLockGivebackPct; } set { brickTrailProfitLockGivebackPct = value; } }

        [NinjaScriptProperty]
        [Display(Name = "  In-Bar Tick Trail Enabled", Order = 39, GroupName = "10 - Regime",
            Description = "PHASE 2.6.3 — Aggressive intra-bar tick trail. Once peak profit reaches MinPeakPts, exit immediately if current tick price retreats from peak by GivebackPts. Wick-vulnerable by design — accepts occasional false stops (re-entry on resumed trend handles that). Catches V-reversals BEFORE the opposite brick closes — fixes the structural ~20pt giveback weakness of brick-flip exit. Default ON.")]
        public bool InBarTrailEnabled { get { return inBarTrailEnabled; } set { inBarTrailEnabled = value; } }

        [NinjaScriptProperty, Range(5, 100)]
        [Display(Name = "  In-Bar Tick Trail Min Peak (pts)", Order = 40, GroupName = "10 - Regime",
            Description = "PHASE 2.6.3 — Minimum peak profit (pts) before in-bar trail arms. Default 25. Below this, only brick-flip exit is active (run is still 'fresh', no profit to protect aggressively).")]
        public double InBarTrailMinPeakPts { get { return inBarTrailMinPeakPts; } set { inBarTrailMinPeakPts = value; } }

        [NinjaScriptProperty, Range(4, 40)]
        [Display(Name = "  In-Bar Tick Trail Giveback (pts)", Order = 41, GroupName = "10 - Regime",
            Description = "PHASE 2.6.3 — Max points price can retrace from peak before in-bar exit fires. Default 16 (one brick height) — wide enough to ignore normal continuation-brick wicks, tight enough to save 12+pt vs brick-flip on V-reversals. Lower (8-12) = more aggressive, more whipsaw. Higher (20-25) = looser, more like brick-flip.")]
        public double InBarTrailGivebackPts { get { return inBarTrailGivebackPts; } set { inBarTrailGivebackPts = value; } }

        // ===== v6 2.4 — UNKNOWN-Regime Auto-Block (prevents low-conviction losses, ACTIVE LOGIC) =====
        [NinjaScriptProperty]
        [Display(Name = "Enable UNKNOWN-Regime Block", Order = 40, GroupName = "10 - Regime",
            Description = "PHASE 2.4 — ACTIVE LOGIC. Default OFF. When ON, AUTO entries are BLOCKED when currentRegime='UNKNOWN' UNLESS all of: (1) brick color matches trade dir, (2) brick streak >= UnknownBlockMinStreak, (3) ADX slope >= UnknownBlockMinAdxSlope. VALIDATED on 2026-04-28: BOTH losses (-$370, -$375) entered with regime=UNKNOWN. With this ON+streak=6, both would have been blocked. Manual entries always pass. REQUIRES EnableRegimeClassifier=ON. Watch for BLOCK_UNKNOWN_REGIME diag rows. DO NOT enable if you want to test the regime classifier's coverage — turn ON only after you verify TREND labels are firing on your instrument.")]
        public bool EnableUnknownRegimeBlock { get { return enableUnknownRegimeBlock; } set { enableUnknownRegimeBlock = value; } }

        [NinjaScriptProperty, Range(2, 20)]
        [Display(Name = "  UNKNOWN Block Min Streak", Order = 41, GroupName = "10 - Regime",
            Description = "Minimum NinzaRenko brick streak (in trade dir color) required to enter when regime=UNKNOWN. Default 6. Lower = more entries (catches early moves the classifier hasn't labeled). Higher = more selective (only super-strong momentum bypasses the block).")]
        public int UnknownBlockMinStreak { get { return unknownBlockMinStreak; } set { unknownBlockMinStreak = value; } }

        [NinjaScriptProperty, Range(-5.0, 10.0)]
        [Display(Name = "  UNKNOWN Block Min AdxSlope", Order = 42, GroupName = "10 - Regime",
            Description = "Minimum ADX slope (positive=rising trend strength) to enter when regime=UNKNOWN. Default 0 (just non-negative). Increase to 1-2 for stricter trend confirmation.")]
        public double UnknownBlockMinAdxSlope { get { return unknownBlockMinAdxSlope; } set { unknownBlockMinAdxSlope = value; } }
        #endregion
    }
}
