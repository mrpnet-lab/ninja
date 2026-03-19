#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;
#endregion

// ============================================================================
//  NQ Adaptive VWAP + Stop-Hunt Strategy V2  |  NinjaTrader 8  |  NinjaScript
//
//  V2 IMPROVEMENTS over V1:
//  - Hard max stop loss cap (prevents oversized stops in volatile regimes)
//  - Daily P&L loss limit (circuit breaker — stops trading when down too much)
//  - ATR volatility filter (skips entries when ATR is abnormally high)
//  - Require BOTH EMA + VWAP alignment (stricter confirmation)
//  - Early breakeven at 50% of TP1 (intermediate protection)
//  - Removed VWAP reclaim secondary entry (main source of whipsaw losses)
//  - Consecutive loss cooldown (pauses after 2 losses in a row)
//  - Minimum bars between trades (prevents re-entry into same bad conditions)
//  - Stronger momentum confirmation threshold
//
//  Timeframe: 5-minute chart recommended
//  Instrument: NQ, MNQ
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class NQ_AdaptiveVWAP_V2 : Strategy
    {
        // ---- USER PARAMETERS ----
        private int    _fastEMA         = 9;
        private int    _slowEMA         = 21;
        private int    _atrPeriod       = 14;
        private double _slATRMult       = 1.0;    // SL = 1.0 × ATR (tighter than V1)
        private double _tp1ATRMult      = 1.5;    // TP1 = 1.5 × ATR (partial exit 60%)
        private double _tp2ATRMult      = 2.5;    // TP2 = 2.5 × ATR (runner — more realistic)
        private int    _swingLookback   = 5;
        private int    _huntConfirmBars = 2;
        private int    _maxTradesPerDay = 3;
        private double _maxStopPoints   = 25.0;   // Hard cap: never risk more than 25 NQ pts
        private double _dailyLossLimit  = 50.0;   // Stop trading if down 50 pts for the day
        private double _maxATRFilter    = 35.0;   // Skip entries when ATR > 35 pts (extreme vol)
        private int    _minBarsBetween  = 6;      // Min 30 min gap between trades (6 × 5-min)
        private int    _maxConsecLosses = 2;      // Pause after 2 consecutive losses

        private TimeSpan _sessionStart  = new TimeSpan(9, 45, 0);   // 9:45 ET (skip first 15 min noise)
        private TimeSpan _sessionEnd    = new TimeSpan(15, 30, 0);  // 3:30 ET (earlier cutoff)
        private bool   _flattenEOD      = true;
        private TimeSpan _flattenTime   = new TimeSpan(15, 50, 0);

        // ---- INTERNAL STATE ----
        private EMA      _emaFast, _emaSlow;
        private ATR      _atr;
        private double   _cumVolPrice, _cumVol, _vwapValue;
        private DateTime _lastSessionDate;
        private double   _entryPrice;
        private double   _currentSL, _currentTP1, _currentTP2;
        private bool     _tp1Hit;
        private bool     _beHit;        // breakeven already triggered
        private int      _tradeCount;
        private int      _lastTradeBarIdx;
        private bool     _huntDetected;
        private int      _huntBarIdx;
        private bool     _huntWasLow;
        private double   _dailyPnL;     // running daily P&L in points
        private int      _consecLosses;  // consecutive losing trades today
        private double   _lastExitPnL;   // P&L of last closed trade

        // ---- PROPERTIES ----
        [NinjaScriptProperty]
        [Display(Name = "Fast EMA period", GroupName = "1. Trend Filter", Order = 1)]
        public int FastEMA { get { return _fastEMA; } set { _fastEMA = Math.Max(3, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Slow EMA period", GroupName = "1. Trend Filter", Order = 2)]
        public int SlowEMA { get { return _slowEMA; } set { _slowEMA = Math.Max(5, value); } }

        [NinjaScriptProperty]
        [Display(Name = "ATR period", GroupName = "2. Volatility", Order = 1)]
        public int ATRPeriod { get { return _atrPeriod; } set { _atrPeriod = Math.Max(5, value); } }

        [NinjaScriptProperty]
        [Range(0.5, 3.0)]
        [Display(Name = "SL ATR multiplier", GroupName = "2. Volatility", Order = 2)]
        public double SLATRMult { get { return _slATRMult; } set { _slATRMult = value; } }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "TP1 ATR multiplier (60% exit)", GroupName = "2. Volatility", Order = 3)]
        public double TP1ATRMult { get { return _tp1ATRMult; } set { _tp1ATRMult = value; } }

        [NinjaScriptProperty]
        [Range(1.0, 8.0)]
        [Display(Name = "TP2 ATR multiplier (40% exit)", GroupName = "2. Volatility", Order = 4)]
        public double TP2ATRMult { get { return _tp2ATRMult; } set { _tp2ATRMult = value; } }

        [NinjaScriptProperty]
        [Range(5.0, 100.0)]
        [Display(Name = "Max stop loss (points)", GroupName = "3. Risk Management", Order = 1)]
        public double MaxStopPoints { get { return _maxStopPoints; } set { _maxStopPoints = value; } }

        [NinjaScriptProperty]
        [Range(10.0, 500.0)]
        [Display(Name = "Daily loss limit (points)", GroupName = "3. Risk Management", Order = 2)]
        public double DailyLossLimit { get { return _dailyLossLimit; } set { _dailyLossLimit = value; } }

        [NinjaScriptProperty]
        [Range(10.0, 100.0)]
        [Display(Name = "Max ATR filter (skip if above)", GroupName = "3. Risk Management", Order = 3)]
        public double MaxATRFilter { get { return _maxATRFilter; } set { _maxATRFilter = value; } }

        [NinjaScriptProperty]
        [Display(Name = "Max trades per day", GroupName = "3. Risk Management", Order = 4)]
        public int MaxTradesPerDay { get { return _maxTradesPerDay; } set { _maxTradesPerDay = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Min bars between trades", GroupName = "3. Risk Management", Order = 5)]
        public int MinBarsBetween { get { return _minBarsBetween; } set { _minBarsBetween = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Max consecutive losses before pause", GroupName = "3. Risk Management", Order = 6)]
        public int MaxConsecLosses { get { return _maxConsecLosses; } set { _maxConsecLosses = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Swing lookback (bars)", GroupName = "4. Stop Hunt Detection", Order = 1)]
        public int SwingLookback { get { return _swingLookback; } set { _swingLookback = Math.Max(2, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Hunt confirm bars", GroupName = "4. Stop Hunt Detection", Order = 2)]
        public int HuntConfirmBars { get { return _huntConfirmBars; } set { _huntConfirmBars = Math.Max(1, value); } }

        [NinjaScriptProperty]
        [Display(Name = "Flatten at end of day", GroupName = "5. Session", Order = 1)]
        public bool FlattenEOD { get { return _flattenEOD; } set { _flattenEOD = value; } }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "NQ Adaptive VWAP + Stop-Hunt V2 — with circuit breakers, vol filter, and loss protection";
                Name        = "NQ_AdaptiveVWAP_V2";
                Calculate   = Calculate.OnBarClose;

                EntriesPerDirection = 1;
                EntryHandling       = EntryHandling.AllEntries;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 300;

                BarsRequiredToTrade = 30;
            }
            else if (State == State.Configure)
            {
                // No additional data series
            }
            else if (State == State.DataLoaded)
            {
                _emaFast = EMA(_fastEMA);
                _emaSlow = EMA(_slowEMA);
                _atr     = ATR(_atrPeriod);
            }
        }

        protected override void OnBarUpdate()
        {
            if (CurrentBar < BarsRequiredToTrade) return;

            TimeSpan now = Time[0].TimeOfDay;

            // --- Daily reset ---
            if (now < _sessionStart)
            {
                _tradeCount    = 0;
                _huntDetected  = false;
                _dailyPnL      = 0;
                _consecLosses  = 0;
            }

            // --- EOD flatten ---
            if (_flattenEOD && now >= _flattenTime && Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("EOD_Flatten", "Hunt_Long");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("EOD_Flatten", "Hunt_Short");
                return;
            }

            // --- Session gate ---
            if (now < _sessionStart || now > _sessionEnd) return;

            // --- CIRCUIT BREAKERS ---
            // 1. Daily loss limit reached
            if (_dailyPnL <= -_dailyLossLimit) return;

            // 2. Max trades per day
            if (_tradeCount >= _maxTradesPerDay) return;

            // 3. Consecutive loss cooldown
            if (_consecLosses >= _maxConsecLosses) return;

            // --- Compute session VWAP ---
            double typicalPrice = (High[0] + Low[0] + Close[0]) / 3.0;
            if (_lastSessionDate != Time[0].Date)
            {
                _cumVolPrice = 0;
                _cumVol      = 0;
                _lastSessionDate = Time[0].Date;
            }
            _cumVolPrice += typicalPrice * Volume[0];
            _cumVol      += Volume[0];
            _vwapValue    = _cumVol > 0 ? _cumVolPrice / _cumVol : typicalPrice;

            // --- Indicators ---
            double atrVal   = _atr[0];
            double vwapVal  = _vwapValue;
            double fastEMA  = _emaFast[0];
            double slowEMA  = _emaSlow[0];
            double close    = Close[0];
            double open     = Open[0];
            double high     = High[0];
            double low      = Low[0];
            bool   bullBar  = close > open;
            bool   bearBar  = close < open;

            // --- 4. ATR volatility filter — skip when market is too wild ---
            if (atrVal > _maxATRFilter) return;

            // --- MANAGE EXISTING POSITION ---
            if (Position.MarketPosition == MarketPosition.Long)
            {
                ManageLongPosition(close, atrVal);
                return;
            }
            if (Position.MarketPosition == MarketPosition.Short)
            {
                ManageShortPosition(close, atrVal);
                return;
            }

            // --- 5. Min bars between trades ---
            if (CurrentBar - _lastTradeBarIdx < _minBarsBetween) return;

            // --- STEP 1: DETECT STOP HUNT ---
            double swingHigh = High[1];
            double swingLow  = Low[1];
            for (int i = 2; i <= _swingLookback; i++)
            {
                if (CurrentBar - i < 0) break;
                swingHigh = Math.Max(swingHigh, High[i]);
                swingLow  = Math.Min(swingLow,  Low[i]);
            }

            // Bullish: wick below swing low, strong recovery close
            bool bullHunt = low < swingLow
                         && close > (swingLow + atrVal * 0.15)    // V2: stricter recovery (15% vs 10%)
                         && bullBar;

            // Bearish: wick above swing high, strong rejection close
            bool bearHunt = high > swingHigh
                         && close < (swingHigh - atrVal * 0.15)
                         && bearBar;

            if (bullHunt && !_huntDetected)
            {
                _huntDetected = true;
                _huntWasLow   = true;
                _huntBarIdx   = CurrentBar;
            }
            else if (bearHunt && !_huntDetected)
            {
                _huntDetected = true;
                _huntWasLow   = false;
                _huntBarIdx   = CurrentBar;
            }

            // Reset stale hunts
            if (_huntDetected && CurrentBar - _huntBarIdx > _huntConfirmBars + 1)
                _huntDetected = false;

            // --- STEP 2: CONFIRM ENTRY AFTER HUNT ---
            if (_huntDetected && (CurrentBar - _huntBarIdx) >= _huntConfirmBars)
            {
                int huntBarsAgo = CurrentBar - _huntBarIdx;

                if (_huntWasLow)
                {
                    // V2: Require BOTH EMA alignment AND VWAP bias (not OR)
                    bool trendOK    = fastEMA > slowEMA && close > vwapVal;
                    bool holdingUp  = Low[0] > Low[huntBarsAgo] && Low[1] > Low[huntBarsAgo];
                    bool momentumOK = (close - open) > atrVal * 0.10;  // V2: stronger momentum (10% vs 5%)

                    if (trendOK && holdingUp && momentumOK)
                    {
                        EnterLongHunt(atrVal, close);
                        _huntDetected = false;
                    }
                }
                else
                {
                    bool trendOK    = fastEMA < slowEMA && close < vwapVal;
                    bool holdingDown= High[0] < High[huntBarsAgo] && High[1] < High[huntBarsAgo];
                    bool momentumOK = (open - close) > atrVal * 0.10;

                    if (trendOK && holdingDown && momentumOK)
                    {
                        EnterShortHunt(atrVal, close);
                        _huntDetected = false;
                    }
                }
            }

            // V2: REMOVED VWAP reclaim secondary entry — main source of whipsaw losses
        }

        // ========================================================
        //  TRADE P&L TRACKING
        // ========================================================
        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition,
            string orderId, DateTime time)
        {
            // Track daily P&L and consecutive losses when a position closes
            if (Position.MarketPosition == MarketPosition.Flat && SystemPerformance.AllTrades.Count > 0)
            {
                Trade lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                double tradePnL = lastTrade.ProfitPoints;
                _dailyPnL += tradePnL;

                if (tradePnL < 0)
                    _consecLosses++;
                else
                    _consecLosses = 0;  // Reset on a winning trade
            }
        }

        // ========================================================
        //  ENTRY HELPERS
        // ========================================================
        private void EnterLongHunt(double atrVal, double entryClose)
        {
            // Calculate stop with ATR, then cap at max points
            double atrStop = atrVal * _slATRMult;
            double actualStop = Math.Min(atrStop, _maxStopPoints);

            _currentSL  = entryClose - actualStop;
            _currentTP1 = entryClose + atrVal * _tp1ATRMult;
            _currentTP2 = entryClose + atrVal * _tp2ATRMult;
            _tp1Hit     = false;
            _beHit      = false;
            _entryPrice = entryClose;

            EnterLong(DefaultQuantity, "Hunt_Long");
            SetStopLoss("Hunt_Long", CalculationMode.Price, _currentSL, false);
            SetProfitTarget("Hunt_Long", CalculationMode.Price, _currentTP2);

            _tradeCount++;
            _lastTradeBarIdx = CurrentBar;

            DrawDot(0, entryClose, "LongEntry_" + CurrentBar, Brushes.LimeGreen);
            DrawLine("SL_Long_" + CurrentBar, 0, _currentSL, -20, _currentSL, Brushes.Red);
            DrawLine("TP1_Long_" + CurrentBar, 0, _currentTP1, -20, _currentTP1, Brushes.Green);
        }

        private void EnterShortHunt(double atrVal, double entryClose)
        {
            double atrStop = atrVal * _slATRMult;
            double actualStop = Math.Min(atrStop, _maxStopPoints);

            _currentSL  = entryClose + actualStop;
            _currentTP1 = entryClose - atrVal * _tp1ATRMult;
            _currentTP2 = entryClose - atrVal * _tp2ATRMult;
            _tp1Hit     = false;
            _beHit      = false;
            _entryPrice = entryClose;

            EnterShort(DefaultQuantity, "Hunt_Short");
            SetStopLoss("Hunt_Short", CalculationMode.Price, _currentSL, false);
            SetProfitTarget("Hunt_Short", CalculationMode.Price, _currentTP2);

            _tradeCount++;
            _lastTradeBarIdx = CurrentBar;

            DrawDot(0, entryClose, "ShortEntry_" + CurrentBar, Brushes.Red);
        }

        // ========================================================
        //  POSITION MANAGEMENT — EARLY BE + ATR TRAILING + PARTIALS
        // ========================================================
        private void ManageLongPosition(double close, double atrVal)
        {
            // V2: Early breakeven at 50% of TP1 distance
            if (!_beHit && close >= _entryPrice + (_currentTP1 - _entryPrice) * 0.5)
            {
                _beHit = true;
                double beSL = _entryPrice + (TickSize * 2); // BE + 2 ticks
                if (beSL > _currentSL)
                {
                    _currentSL = beSL;
                    SetStopLoss("Hunt_Long", CalculationMode.Price, _currentSL, false);
                }
            }

            // Partial exit at TP1 (60%) — move SL to BE + 5 ticks
            if (!_tp1Hit && close >= _currentTP1)
            {
                _tp1Hit = true;
                int exitQty = (int)Math.Round(Position.Quantity * 0.6);
                if (exitQty > 0)
                    ExitLong(exitQty, "TP1_Partial", "Hunt_Long");

                double newSL = _entryPrice + (TickSize * 5);
                if (newSL > _currentSL)
                {
                    _currentSL = newSL;
                    SetStopLoss("Hunt_Long", CalculationMode.Price, _currentSL, false);
                }
            }

            // ATR trailing stop after TP1
            if (_tp1Hit && close > _entryPrice)
            {
                double trailSL = close - atrVal * 0.8;  // V2: tighter trail (0.8 vs 1.0)
                if (trailSL > _currentSL)
                {
                    _currentSL = trailSL;
                    SetStopLoss("Hunt_Long", CalculationMode.Price, _currentSL, false);
                }
            }
        }

        private void ManageShortPosition(double close, double atrVal)
        {
            // V2: Early breakeven at 50% of TP1 distance
            if (!_beHit && close <= _entryPrice - (_entryPrice - _currentTP1) * 0.5)
            {
                _beHit = true;
                double beSL = _entryPrice - (TickSize * 2);
                if (beSL < _currentSL)
                {
                    _currentSL = beSL;
                    SetStopLoss("Hunt_Short", CalculationMode.Price, _currentSL, false);
                }
            }

            if (!_tp1Hit && close <= _currentTP1)
            {
                _tp1Hit = true;
                int exitQty = (int)Math.Round(Position.Quantity * 0.6);
                if (exitQty > 0)
                    ExitShort(exitQty, "TP1_Partial", "Hunt_Short");

                double newSL = _entryPrice - (TickSize * 5);
                if (newSL < _currentSL)
                {
                    _currentSL = newSL;
                    SetStopLoss("Hunt_Short", CalculationMode.Price, _currentSL, false);
                }
            }

            if (_tp1Hit && close < _entryPrice)
            {
                double trailSL = close + atrVal * 0.8;
                if (trailSL < _currentSL)
                {
                    _currentSL = trailSL;
                    SetStopLoss("Hunt_Short", CalculationMode.Price, _currentSL, false);
                }
            }
        }

        // ========================================================
        //  DRAWING HELPERS
        // ========================================================
        private void DrawDot(int barsAgo, double price, string tag,
                              System.Windows.Media.Brush color)
        {
            Draw.Dot(this, tag, false, barsAgo, price, color);
        }

        private void DrawLine(string tag, int startBarsAgo, double startPrice,
                              int endBarsAgo, double endPrice,
                              System.Windows.Media.Brush color)
        {
            Draw.Line(this, tag, false, startBarsAgo, startPrice, endBarsAgo, endPrice, color,
                      DashStyleHelper.Solid, 1);
        }
    }
}
