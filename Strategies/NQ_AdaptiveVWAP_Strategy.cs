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
//  NQ Adaptive VWAP + Stop-Hunt Strategy  |  NinjaTrader 8  |  NinjaScript
//  Strategy: Enter AFTER confirmed stop-hunt sweep using ATR-adaptive SL/TP,
//            VWAP directional bias, EMA trend filter, and session gating.
//  Timeframe: 5-minute chart recommended (works on 3-min and 15-min too)
//  Instrument: NQ, MNQ
// ============================================================================

namespace NinjaTrader.NinjaScript.Strategies
{
    public class NQ_AdaptiveVWAP : Strategy
    {
        // ---- USER PARAMETERS ----
        private int    _fastEMA        = 9;
        private int    _slowEMA        = 21;
        private int    _atrPeriod      = 14;
        private double _slATRMult      = 1.2;   // SL = 1.2 × ATR (adjust per regime)
        private double _tp1ATRMult     = 1.8;   // TP1 = 1.8 × ATR  (partial exit 60%)
        private double _tp2ATRMult     = 3.0;   // TP2 = 3.0 × ATR  (remaining 40%)
        private double _vwapBandMult   = 0.5;   // VWAP band width = 0.5 × ATR
        private int    _swingLookback  = 5;     // Bars to identify swing high/low for hunt
        private int    _huntConfirmBars= 2;     // Bars AFTER sweep to confirm reversal
        private int    _maxTradesPerDay= 3;
        private TimeSpan _sessionStart = new TimeSpan(9, 30, 0);   // 9:30 ET
        private TimeSpan _sessionEnd   = new TimeSpan(15, 45, 0);  // 3:45 ET (no late entries)
        private bool   _flattenEOD     = true;
        private TimeSpan _flattenTime  = new TimeSpan(15, 55, 0);

        // ---- INTERNAL STATE ----
        private EMA     _emaFast, _emaSlow;
        private ATR     _atr;
        private double  _cumVolPrice;  // Cumulative (Volume × Typical Price) for VWAP
        private double  _cumVol;       // Cumulative Volume for VWAP
        private double  _vwapValue;    // Current session VWAP
        private DateTime _lastSessionDate;
        private double  _entryPrice;
        private double  _currentSL, _currentTP1, _currentTP2;
        private bool    _tp1Hit;
        private int     _tradeCount;
        private int     _lastTradeBarIdx;
        private bool    _huntDetected;
        private int     _huntBarIdx;
        private bool    _huntWasLow;   // true = hunted below support (bullish setup)

        // ---- PROPERTIES (displayed in Strategy Builder) ----
        [NinjaScriptProperty]
        [Display(Name = "Fast EMA period", GroupName = "Trend Filter", Order = 1)]
        public int FastEMA
        {
            get { return _fastEMA; }
            set { _fastEMA = Math.Max(3, value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Slow EMA period", GroupName = "Trend Filter", Order = 2)]
        public int SlowEMA
        {
            get { return _slowEMA; }
            set { _slowEMA = Math.Max(5, value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "ATR period", GroupName = "Volatility", Order = 1)]
        public int ATRPeriod
        {
            get { return _atrPeriod; }
            set { _atrPeriod = Math.Max(5, value); }
        }

        [NinjaScriptProperty]
        [Range(0.5, 3.0)]
        [Display(Name = "SL ATR multiplier", GroupName = "Volatility", Order = 2)]
        public double SLATRMult
        {
            get { return _slATRMult; }
            set { _slATRMult = value; }
        }

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "TP1 ATR multiplier (60% exit)", GroupName = "Volatility", Order = 3)]
        public double TP1ATRMult
        {
            get { return _tp1ATRMult; }
            set { _tp1ATRMult = value; }
        }

        [NinjaScriptProperty]
        [Range(1.0, 8.0)]
        [Display(Name = "TP2 ATR multiplier (40% exit)", GroupName = "Volatility", Order = 4)]
        public double TP2ATRMult
        {
            get { return _tp2ATRMult; }
            set { _tp2ATRMult = value; }
        }

        [NinjaScriptProperty]
        [Display(Name = "Swing lookback (bars)", GroupName = "Stop Hunt Detection", Order = 1)]
        public int SwingLookback
        {
            get { return _swingLookback; }
            set { _swingLookback = Math.Max(2, value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Hunt confirm bars", GroupName = "Stop Hunt Detection", Order = 2)]
        public int HuntConfirmBars
        {
            get { return _huntConfirmBars; }
            set { _huntConfirmBars = Math.Max(1, value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Max trades per day", GroupName = "Risk Management", Order = 1)]
        public int MaxTradesPerDay
        {
            get { return _maxTradesPerDay; }
            set { _maxTradesPerDay = Math.Max(1, value); }
        }

        [NinjaScriptProperty]
        [Display(Name = "Flatten at end of day", GroupName = "Risk Management", Order = 2)]
        public bool FlattenEOD
        {
            get { return _flattenEOD; }
            set { _flattenEOD = value; }
        }

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "NQ Adaptive VWAP + Stop-Hunt strategy with ATR-based TP/SL";
                Name        = "NQ_AdaptiveVWAP";
                Calculate   = Calculate.OnBarClose;

                // Allow 2 entries (full contracts for scaled partials)
                EntriesPerDirection = 1;
                EntryHandling       = EntryHandling.AllEntries;

                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds    = 300;

                BarsRequiredToTrade = 30;
            }
            else if (State == State.Configure)
            {
                // No additional data series required
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
            // --- Safety: need enough bars ---
            if (CurrentBar < BarsRequiredToTrade) return;

            // --- Session gate ---
            TimeSpan now = Time[0].TimeOfDay;

            // Reset daily trade counter at session open
            if (now < _sessionStart) { _tradeCount = 0; _huntDetected = false; }

            // End-of-day flatten
            if (_flattenEOD && now >= _flattenTime && Position.MarketPosition != MarketPosition.Flat)
            {
                if (Position.MarketPosition == MarketPosition.Long)
                    ExitLong("EOD_Flatten", "Hunt_Long");
                else if (Position.MarketPosition == MarketPosition.Short)
                    ExitShort("EOD_Flatten", "Hunt_Short");
                return;
            }

            // Only trade inside session window
            bool inSession = now >= _sessionStart && now <= _sessionEnd;
            if (!inSession) return;

            // Max daily trades gate
            if (_tradeCount >= _maxTradesPerDay) return;

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

            // --- STEP 1: DETECT STOP HUNT ---
            // A stop hunt occurs when price briefly violates a recent swing extreme
            // and then immediately reverses (wicks through the level).
            // We look for: (a) a new N-bar low/high made intrabar, (b) close reverses above/below it.

            // Calculate swing high/low from PRIOR bars only (exclude current bar)
            double swingHigh = High[1];
            double swingLow  = Low[1];
            for (int i = 2; i <= _swingLookback; i++)
            {
                if (CurrentBar - i < 0) break;
                swingHigh = Math.Max(swingHigh, High[i]);
                swingLow  = Math.Min(swingLow,  Low[i]);
            }

            // Bullish stop hunt: current bar made a new low but CLOSED above prior swing low
            bool bullHunt = low < swingLow                          // wick below swing low
                         && close > (swingLow + atrVal * 0.10)     // strong close recovery
                         && bullBar;                                 // bar closed bullish

            // Bearish stop hunt: current bar made a new high but CLOSED below prior swing high
            bool bearHunt = high > swingHigh                         // wick above swing high
                         && close < (swingHigh - atrVal * 0.10)
                         && bearBar;

            // Store hunt detection for confirmation on next bar(s)
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
                    // Bullish confirmation conditions:
                    // 1. Fast EMA above Slow EMA (trend aligned) OR price above VWAP
                    // 2. Recent bars holding above the hunted low
                    // 3. Strong close (not a doji)
                    bool trendOK    = (fastEMA > slowEMA) || (close > vwapVal);
                    bool holdingUp  = Low[0] > Low[huntBarsAgo] && Low[1] > Low[huntBarsAgo];
                    bool momentumOK = (close - open) > atrVal * 0.05;

                    if (trendOK && holdingUp && momentumOK)
                    {
                        EnterLongHunt(atrVal, close);
                        _huntDetected = false;
                    }
                }
                else
                {
                    // Bearish confirmation conditions:
                    bool trendOK    = (fastEMA < slowEMA) || (close < vwapVal);
                    bool holdingDown= High[0] < High[huntBarsAgo] && High[1] < High[huntBarsAgo];
                    bool momentumOK = (open - close) > atrVal * 0.05;

                    if (trendOK && holdingDown && momentumOK)
                    {
                        EnterShortHunt(atrVal, close);
                        _huntDetected = false;
                    }
                }
            }

            // --- STEP 3: SECONDARY ENTRY — VWAP RECLAIM WITH EMA CONFIRMATION ---
            // Additional entry when price crosses back above VWAP with trend alignment.
            // This catches moves that didn't have a clean stop hunt signature.
            bool vwapReclaimLong  = (Close[1] <= vwapVal && Close[0] > vwapVal)
                                 && fastEMA > slowEMA
                                 && Close[0] > Close[1]
                                 && Close[0] > Close[2];

            bool vwapReclaimShort = (Close[1] >= vwapVal && Close[0] < vwapVal)
                                 && fastEMA < slowEMA
                                 && Close[0] < Close[1]
                                 && Close[0] < Close[2];

            if (vwapReclaimLong && Position.MarketPosition == MarketPosition.Flat)
                EnterLongHunt(atrVal, close);
            else if (vwapReclaimShort && Position.MarketPosition == MarketPosition.Flat)
                EnterShortHunt(atrVal, close);
        }

        // ========================================================
        //  ENTRY HELPERS
        // ========================================================
        private void EnterLongHunt(double atrVal, double entryClose)
        {
            _currentSL  = entryClose - atrVal * _slATRMult;
            _currentTP1 = entryClose + atrVal * _tp1ATRMult;
            _currentTP2 = entryClose + atrVal * _tp2ATRMult;
            _tp1Hit     = false;
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
            _currentSL  = entryClose + atrVal * _slATRMult;
            _currentTP1 = entryClose - atrVal * _tp1ATRMult;
            _currentTP2 = entryClose - atrVal * _tp2ATRMult;
            _tp1Hit     = false;
            _entryPrice = entryClose;

            EnterShort(DefaultQuantity, "Hunt_Short");
            SetStopLoss("Hunt_Short", CalculationMode.Price, _currentSL, false);
            SetProfitTarget("Hunt_Short", CalculationMode.Price, _currentTP2);

            _tradeCount++;
            _lastTradeBarIdx = CurrentBar;

            DrawDot(0, entryClose, "ShortEntry_" + CurrentBar, Brushes.Red);
        }

        // ========================================================
        //  POSITION MANAGEMENT — ATR TRAILING + PARTIAL EXITS
        // ========================================================
        private void ManageLongPosition(double close, double atrVal)
        {
            // Partial exit at TP1 (60% of position) — move SL to break-even
            if (!_tp1Hit && close >= _currentTP1)
            {
                _tp1Hit = true;
                // Exit 60% of contracts at TP1
                int exitQty = (int)Math.Round(Position.Quantity * 0.6);
                if (exitQty > 0)
                    ExitLong(exitQty, "TP1_Partial", "Hunt_Long");

                // Move stop to break-even + 5 ticks
                double newSL = _entryPrice + (TickSize * 5);
                if (newSL > _currentSL)
                {
                    _currentSL = newSL;
                    SetStopLoss("Hunt_Long", CalculationMode.Price, _currentSL, false);
                }
            }

            // ATR trailing stop: trail at 1.0× ATR once in profit
            if (_tp1Hit && close > _entryPrice)
            {
                double trailSL = close - atrVal * 1.0;
                if (trailSL > _currentSL)
                {
                    _currentSL = trailSL;
                    SetStopLoss("Hunt_Long", CalculationMode.Price, _currentSL, false);
                }
            }
        }

        private void ManageShortPosition(double close, double atrVal)
        {
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
                double trailSL = close + atrVal * 1.0;
                if (trailSL < _currentSL)
                {
                    _currentSL = trailSL;
                    SetStopLoss("Hunt_Short", CalculationMode.Price, _currentSL, false);
                }
            }
        }

        // ========================================================
        //  HELPER: Draw dot on chart (NinjaTrader 8 compatible)
        // ========================================================
        private void DrawDot(int barsAgo, double price, string tag,
                              System.Windows.Media.Brush color)
        {
            Draw.Dot(this, tag, false, barsAgo, price, color);
        }

        private void DrawLine(string tag, int startBarsAgo, double startPrice,
                              int endBarsAgo,  double endPrice,
                              System.Windows.Media.Brush color)
        {
            Draw.Line(this, tag, false, startBarsAgo, startPrice, endBarsAgo, endPrice, color,
                      DashStyleHelper.Solid, 1);
        }
    }
}
