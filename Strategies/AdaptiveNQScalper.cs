#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Gui.Tools;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    /// <summary>
    /// AdaptiveNQScalper - An adaptive NQ futures strategy that detects market regime
    /// (trending vs ranging), uses multi-indicator confluence, and dynamically adjusts
    /// stop losses and take profits based on real-time volatility.
    /// 
    /// Key Features:
    /// - Market Regime Detection (ADX + Bollinger Band Width)
    /// - Adaptive ATR-based Stop Loss and Take Profit
    /// - Multi-indicator confluence (EMA, VWAP, RSI, MACD, Volume)
    /// - Session filtering (trades only during high-liquidity hours)
    /// - Trailing stop management
    /// - Recent performance tracking for adaptive behavior
    /// - 1 contract per trade
    /// </summary>
    public class AdaptiveNQScalper : Strategy
    {
        #region Private Variables

        // ── Indicators ──
        private EMA emaFast;
        private EMA emaSlow;
        private EMA emaTrend;
        private ATR atr;
        private ADX adx;
        private RSI rsi;
        private MACD macd;
        private Bollinger bollinger;
        private VOL volume;

        // ── Market Regime ──
        private MarketRegime currentRegime;
        private double bollingerBandWidth;

        // ── Adaptive Performance Tracking ──
        private List<double> recentTradePnL;
        private int recentWins;
        private int recentLosses;
        private double adaptiveMultiplier;
        private int maxTrackingWindow;

        // ── Session Management ──
        private bool isInTradingSession;
        private TimeSpan sessionStart;
        private TimeSpan sessionEnd;
        private TimeSpan lunchStart;
        private TimeSpan lunchEnd;

        // ── Trade Management ──
        private double entryPrice;
        private double currentStopLoss;
        private double currentTakeProfit;
        private bool isTrailing;
        private int barsInTrade;
        private double highestSinceEntry;
        private double lowestSinceEntry;

        // ── Signal Scoring ──
        private double longScore;
        private double shortScore;

        // ── Cooldown ──
        private int barsSinceLastTrade;
        private int dailyTradeCount;
        private DateTime lastTradeDate;

        #endregion

        #region Enums

        private enum MarketRegime
        {
            StrongTrend,
            WeakTrend,
            Ranging,
            Volatile
        }

        #endregion

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = @"Adaptive NQ Futures Scalper with market regime detection, " +
                              "dynamic stop/target management, and multi-indicator confluence.";
                Name = "AdaptiveNQScalper";
                Calculate = Calculate.OnBarClose;
                EntriesPerDirection = 1;
                EntryHandling = EntryHandling.AllEntries;
                IsExitOnSessionCloseStrategy = true;
                ExitOnSessionCloseSeconds = 30;
                IsFillLimitOnTouch = false;
                MaximumBarsLookBack = MaximumBarsLookBack.TwoHundredFiftySix;
                OrderFillResolution = OrderFillResolution.Standard;
                Slippage = 1;
                StartBehavior = StartBehavior.WaitUntilFlat;
                TimeInForce = TimeInForce.Gtc;
                TraceOrders = false;
                RealtimeErrorHandling = RealtimeErrorHandling.StopCancelClose;
                StopTargetHandling = StopTargetHandling.PerEntryExecution;
                BarsRequiredToTrade = 50;
                IsInstantiatedOnEachOptimizationIteration = true;

                // ── User Parameters ──
                FastEMAPeriod = 9;
                SlowEMAPeriod = 21;
                TrendEMAPeriod = 50;
                ATRPeriod = 14;
                ADXPeriod = 14;
                RSIPeriod = 14;
                MACDFast = 12;
                MACDSlow = 26;
                MACDSignal = 9;
                BollingerPeriod = 20;
                BollingerStdDev = 2.0;

                // ── Regime Thresholds ──
                ADXTrendThreshold = 25;
                ADXStrongTrendThreshold = 40;
                VolatilityThreshold = 1.5;

                // ── Risk Management ──
                ATRStopMultiplier = 2.0;
                ATRTargetMultiplier = 3.0;
                MaxStopLossTicks = 80;
                MinStopLossTicks = 16;
                TrailingActivationMultiplier = 1.5;
                TrailingStepTicks = 8;
                BreakevenActivationTicks = 20;

                // ── Signal Thresholds ──
                MinSignalScore = 3.0;
                RSIOverbought = 70;
                RSIOversold = 30;

                // ── Session & Trade Management ──
                SessionStartHour = 9;
                SessionStartMinute = 30;
                SessionEndHour = 15;
                SessionEndMinute = 45;
                AvoidLunchHour = true;
                MaxDailyTrades = 6;
                CooldownBars = 5;
                PerformanceWindow = 20;

                // ── Adaptive Behavior ──
                EnableAdaptiveMode = true;
                AggressiveAfterWins = 3;
                DefensiveAfterLosses = 2;
            }
            else if (State == State.Configure)
            {
                // No additional data series needed for primary timeframe strategy
            }
            else if (State == State.DataLoaded)
            {
                // ── Initialize Indicators ──
                emaFast = EMA(FastEMAPeriod);
                emaSlow = EMA(SlowEMAPeriod);
                emaTrend = EMA(TrendEMAPeriod);
                atr = ATR(ATRPeriod);
                adx = ADX(ADXPeriod);
                rsi = RSI(RSIPeriod, 3);
                macd = MACD(MACDFast, MACDSlow, MACDSignal);
                bollinger = Bollinger(BollingerStdDev, BollingerPeriod);
                volume = VOL();

                // Add indicators to chart for visualization
                AddChartIndicator(emaFast);
                AddChartIndicator(emaSlow);
                AddChartIndicator(emaTrend);
                AddChartIndicator(rsi);
                AddChartIndicator(macd);
                AddChartIndicator(bollinger);

                // ── Initialize Tracking ──
                recentTradePnL = new List<double>();
                recentWins = 0;
                recentLosses = 0;
                adaptiveMultiplier = 1.0;
                maxTrackingWindow = PerformanceWindow;
                barsSinceLastTrade = CooldownBars + 1; // Allow immediate first trade
                dailyTradeCount = 0;
                lastTradeDate = DateTime.MinValue;

                // ── Session Times (Eastern) ──
                sessionStart = new TimeSpan(SessionStartHour, SessionStartMinute, 0);
                sessionEnd = new TimeSpan(SessionEndHour, SessionEndMinute, 0);
                lunchStart = new TimeSpan(11, 30, 0);
                lunchEnd = new TimeSpan(13, 0, 0);
            }
        }

        protected override void OnBarUpdate()
        {
            // Ensure enough bars for all indicators
            if (CurrentBar < BarsRequiredToTrade)
                return;

            // Reset daily trade count on new day
            if (Time[0].Date != lastTradeDate.Date)
            {
                dailyTradeCount = 0;
                lastTradeDate = Time[0].Date;
            }

            barsSinceLastTrade++;

            // ── Determine Session ──
            UpdateSessionStatus();

            // ── Detect Market Regime ──
            DetectMarketRegime();

            // ── Manage Existing Position ──
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                ManageOpenTrade();
                return; // Don't look for new signals while in a trade
            }

            // ── Check Entry Conditions ──
            if (!CanEnterTrade())
                return;

            // ── Calculate Signal Scores ──
            CalculateSignalScores();

            // ── Execute Trades ──
            double adjustedThreshold = MinSignalScore * adaptiveMultiplier;

            if (longScore >= adjustedThreshold)
                EnterLongTrade();
            else if (shortScore >= adjustedThreshold)
                EnterShortTrade();
        }

        #region Market Regime Detection

        private void DetectMarketRegime()
        {
            double adxValue = adx[0];
            double atrValue = atr[0];

            // Calculate Bollinger Band width as % of price (volatility measure)
            bollingerBandWidth = (bollinger.Upper[0] - bollinger.Lower[0]) / Close[0] * 100;

            // Calculate average ATR over last 50 bars for comparison
            double avgATR = 0;
            for (int i = 0; i < Math.Min(50, CurrentBar); i++)
                avgATR += atr[i];
            avgATR /= Math.Min(50, CurrentBar);

            double volatilityRatio = atrValue / avgATR;

            // ── Regime Classification ──
            if (adxValue >= ADXStrongTrendThreshold)
                currentRegime = MarketRegime.StrongTrend;
            else if (adxValue >= ADXTrendThreshold && volatilityRatio < VolatilityThreshold)
                currentRegime = MarketRegime.WeakTrend;
            else if (volatilityRatio >= VolatilityThreshold)
                currentRegime = MarketRegime.Volatile;
            else
                currentRegime = MarketRegime.Ranging;
        }

        #endregion

        #region Signal Scoring

        private void CalculateSignalScores()
        {
            longScore = 0;
            shortScore = 0;

            // ══════════════════════════════════════════
            // 1. EMA ALIGNMENT (max 2 points)
            // ══════════════════════════════════════════
            bool fastAboveSlow = emaFast[0] > emaSlow[0];
            bool priceAboveTrend = Close[0] > emaTrend[0];
            bool emaCrossUp = CrossAbove(emaFast, emaSlow, 1);
            bool emaCrossDown = CrossBelow(emaFast, emaSlow, 1);

            if (fastAboveSlow && priceAboveTrend)
                longScore += 1.5;
            else if (!fastAboveSlow && !priceAboveTrend)
                shortScore += 1.5;

            if (emaCrossUp) longScore += 0.5;
            if (emaCrossDown) shortScore += 0.5;

            // ══════════════════════════════════════════
            // 2. RSI CONDITIONS (max 1.5 points)
            // ══════════════════════════════════════════
            double rsiValue = rsi[0];

            if (currentRegime == MarketRegime.StrongTrend || currentRegime == MarketRegime.WeakTrend)
            {
                // In trends, RSI pullbacks are entry signals
                if (rsiValue > 40 && rsiValue < 60)
                {
                    if (fastAboveSlow) longScore += 1.0;
                    else shortScore += 1.0;
                }
                // Momentum confirmation
                if (rsiValue > 50 && rsiValue < RSIOverbought) longScore += 0.5;
                if (rsiValue < 50 && rsiValue > RSIOversold) shortScore += 0.5;
            }
            else
            {
                // In ranges, use overbought/oversold for mean reversion
                if (rsiValue < RSIOversold) longScore += 1.5;
                if (rsiValue > RSIOverbought) shortScore += 1.5;
            }

            // ══════════════════════════════════════════
            // 3. MACD CONFLUENCE (max 1.5 points)
            // ══════════════════════════════════════════
            double macdLine = macd[0];
            double macdSignalLine = macd.Avg[0];
            double macdHist = macd.Diff[0];

            if (macdLine > macdSignalLine && macdHist > 0)
                longScore += 1.0;
            else if (macdLine < macdSignalLine && macdHist < 0)
                shortScore += 1.0;

            // MACD histogram increasing = momentum building
            if (CurrentBar > 1)
            {
                if (macd.Diff[0] > macd.Diff[1] && macdHist > 0)
                    longScore += 0.5;
                else if (macd.Diff[0] < macd.Diff[1] && macdHist < 0)
                    shortScore += 0.5;
            }

            // ══════════════════════════════════════════
            // 4. PRICE ACTION / CANDLE ANALYSIS (max 1 point)
            // ══════════════════════════════════════════
            double bodySize = Math.Abs(Close[0] - Open[0]);
            double totalRange = High[0] - Low[0];
            double upperWick = High[0] - Math.Max(Open[0], Close[0]);
            double lowerWick = Math.Min(Open[0], Close[0]) - Low[0];

            // Strong bullish/bearish candle
            if (totalRange > 0)
            {
                double bodyRatio = bodySize / totalRange;
                if (bodyRatio > 0.6 && Close[0] > Open[0]) longScore += 0.5;
                if (bodyRatio > 0.6 && Close[0] < Open[0]) shortScore += 0.5;
            }

            // Rejection wicks (pin bars)
            if (totalRange > 0)
            {
                if (lowerWick > bodySize * 2 && Close[0] > Open[0]) longScore += 0.5;
                if (upperWick > bodySize * 2 && Close[0] < Open[0]) shortScore += 0.5;
            }

            // ══════════════════════════════════════════
            // 5. BOLLINGER BAND POSITION (max 1 point)
            // ══════════════════════════════════════════
            double bbMid = bollinger[0];
            double bbUpper = bollinger.Upper[0];
            double bbLower = bollinger.Lower[0];

            if (currentRegime == MarketRegime.Ranging)
            {
                // Mean reversion in ranges
                if (Close[0] <= bbLower) longScore += 1.0;
                if (Close[0] >= bbUpper) shortScore += 1.0;
            }
            else
            {
                // Breakout confirmation in trends
                if (Close[0] > bbMid && Close[1] <= bollinger[1]) longScore += 0.5;
                if (Close[0] < bbMid && Close[1] >= bollinger[1]) shortScore += 0.5;
            }

            // ══════════════════════════════════════════
            // 6. VOLUME CONFIRMATION (max 0.5 points)
            // ══════════════════════════════════════════
            double avgVolume = 0;
            for (int i = 1; i <= 20 && i < CurrentBar; i++)
                avgVolume += Volume[i];
            avgVolume /= Math.Min(20, CurrentBar - 1);

            if (Volume[0] > avgVolume * 1.3)
            {
                // Above-average volume confirms direction
                if (Close[0] > Open[0]) longScore += 0.5;
                else if (Close[0] < Open[0]) shortScore += 0.5;
            }

            // ══════════════════════════════════════════
            // 7. REGIME-BASED ADJUSTMENTS
            // ══════════════════════════════════════════
            switch (currentRegime)
            {
                case MarketRegime.StrongTrend:
                    // Boost trend-following signals in strong trends
                    if (emaFast[0] > emaSlow[0]) longScore *= 1.15;
                    else shortScore *= 1.15;
                    break;

                case MarketRegime.Volatile:
                    // Reduce all scores in volatile markets (be more selective)
                    longScore *= 0.75;
                    shortScore *= 0.75;
                    break;

                case MarketRegime.Ranging:
                    // Slightly reduce scores (ranging is harder to trade)
                    longScore *= 0.90;
                    shortScore *= 0.90;
                    break;
            }

            // ══════════════════════════════════════════
            // 8. CONFLICTING SIGNAL PENALTY
            // ══════════════════════════════════════════
            // If both long and short are elevated, market is indecisive
            if (longScore > 2 && shortScore > 2)
            {
                longScore *= 0.5;
                shortScore *= 0.5;
            }
        }

        #endregion

        #region Trade Entry

        private void EnterLongTrade()
        {
            double atrValue = atr[0];
            double stopDistance = CalculateStopDistance(atrValue, true);
            double targetDistance = CalculateTargetDistance(atrValue, true);

            // Clamp stop loss
            stopDistance = Math.Max(stopDistance, MinStopLossTicks * TickSize);
            stopDistance = Math.Min(stopDistance, MaxStopLossTicks * TickSize);

            entryPrice = Close[0];
            currentStopLoss = entryPrice - stopDistance;
            currentTakeProfit = entryPrice + targetDistance;
            isTrailing = false;
            barsInTrade = 0;
            highestSinceEntry = entryPrice;
            lowestSinceEntry = entryPrice;

            SetStopLoss(CalculationMode.Ticks, stopDistance / TickSize);
            SetProfitTarget(CalculationMode.Ticks, targetDistance / TickSize);

            EnterLong(1, "AdaptiveLong");
            barsSinceLastTrade = 0;
            dailyTradeCount++;

            Print(string.Format("[{0}] LONG Entry | Price: {1:F2} | Stop: {2:F2} | Target: {3:F2} | Score: {4:F1} | Regime: {5}",
                Time[0], entryPrice, currentStopLoss, currentTakeProfit, longScore, currentRegime));
        }

        private void EnterShortTrade()
        {
            double atrValue = atr[0];
            double stopDistance = CalculateStopDistance(atrValue, false);
            double targetDistance = CalculateTargetDistance(atrValue, false);

            // Clamp stop loss
            stopDistance = Math.Max(stopDistance, MinStopLossTicks * TickSize);
            stopDistance = Math.Min(stopDistance, MaxStopLossTicks * TickSize);

            entryPrice = Close[0];
            currentStopLoss = entryPrice + stopDistance;
            currentTakeProfit = entryPrice - targetDistance;
            isTrailing = false;
            barsInTrade = 0;
            highestSinceEntry = entryPrice;
            lowestSinceEntry = entryPrice;

            SetStopLoss(CalculationMode.Ticks, stopDistance / TickSize);
            SetProfitTarget(CalculationMode.Ticks, targetDistance / TickSize);

            EnterShort(1, "AdaptiveShort");
            barsSinceLastTrade = 0;
            dailyTradeCount++;

            Print(string.Format("[{0}] SHORT Entry | Price: {1:F2} | Stop: {2:F2} | Target: {3:F2} | Score: {4:F1} | Regime: {5}",
                Time[0], entryPrice, currentStopLoss, currentTakeProfit, shortScore, currentRegime));
        }

        #endregion

        #region Dynamic Stop/Target Calculation

        private double CalculateStopDistance(double atrValue, bool isLong)
        {
            double baseStop = atrValue * ATRStopMultiplier;

            // ── Regime-Based Adjustment ──
            switch (currentRegime)
            {
                case MarketRegime.StrongTrend:
                    baseStop *= 0.85; // Tighter stops in strong trends (trend protects)
                    break;
                case MarketRegime.Volatile:
                    baseStop *= 1.3;  // Wider stops in volatile markets
                    break;
                case MarketRegime.Ranging:
                    baseStop *= 1.1;  // Slightly wider in ranges
                    break;
            }

            // ── Adaptive Adjustment Based on Recent Performance ──
            if (EnableAdaptiveMode)
            {
                if (recentLosses >= DefensiveAfterLosses)
                {
                    // After consecutive losses, tighten stops to limit further damage
                    baseStop *= 0.85;
                }
                else if (recentWins >= AggressiveAfterWins)
                {
                    // After wins streak, allow slightly wider stops for bigger moves
                    baseStop *= 1.1;
                }
            }

            return baseStop;
        }

        private double CalculateTargetDistance(double atrValue, bool isLong)
        {
            double baseTarget = atrValue * ATRTargetMultiplier;

            // ── Regime-Based Adjustment ──
            switch (currentRegime)
            {
                case MarketRegime.StrongTrend:
                    baseTarget *= 1.3;  // Larger targets in strong trends
                    break;
                case MarketRegime.Volatile:
                    baseTarget *= 0.85; // Smaller targets in volatile markets (take profits fast)
                    break;
                case MarketRegime.Ranging:
                    baseTarget *= 0.9;  // Moderate targets in ranges
                    break;
            }

            // ── Adaptive R:R Adjustment ──
            if (EnableAdaptiveMode && recentWins >= AggressiveAfterWins)
            {
                baseTarget *= 1.15; // Let winners run when on a hot streak
            }

            return baseTarget;
        }

        #endregion

        #region Trade Management

        private void ManageOpenTrade()
        {
            barsInTrade++;

            if (Position.MarketPosition == MarketPosition.Long)
                ManageLongTrade();
            else if (Position.MarketPosition == MarketPosition.Short)
                ManageShortTrade();
        }

        private void ManageLongTrade()
        {
            highestSinceEntry = Math.Max(highestSinceEntry, High[0]);
            double unrealizedTicks = (Close[0] - Position.AveragePrice) / TickSize;

            // ── 1. Breakeven Stop ──
            if (!isTrailing && unrealizedTicks >= BreakevenActivationTicks)
            {
                double breakevenStop = Position.AveragePrice + (2 * TickSize); // 2 ticks above entry
                if (breakevenStop > currentStopLoss)
                {
                    currentStopLoss = breakevenStop;
                    SetStopLoss(CalculationMode.Price, currentStopLoss);
                    Print(string.Format("[{0}] LONG Breakeven activated at {1:F2}", Time[0], currentStopLoss));
                }
            }

            // ── 2. Trailing Stop ──
            double trailingActivation = Position.AveragePrice + (atr[0] * TrailingActivationMultiplier);
            if (Close[0] >= trailingActivation)
            {
                isTrailing = true;
                double newTrailingStop = highestSinceEntry - (TrailingStepTicks * TickSize);

                if (newTrailingStop > currentStopLoss)
                {
                    currentStopLoss = newTrailingStop;
                    SetStopLoss(CalculationMode.Price, currentStopLoss);
                    Print(string.Format("[{0}] LONG Trailing stop updated to {1:F2}", Time[0], currentStopLoss));
                }
            }

            // ── 3. Time-Based Exit (avoid holding too long) ──
            if (barsInTrade >= 50 && unrealizedTicks > 0)
            {
                ExitLong("TimeExit", "AdaptiveLong");
                Print(string.Format("[{0}] LONG Time-based exit at {1:F2}", Time[0], Close[0]));
            }

            // ── 4. Regime Change Exit ──
            if (currentRegime == MarketRegime.Volatile && unrealizedTicks > 8)
            {
                // Take profits quickly if market turns volatile
                ExitLong("VolatilityExit", "AdaptiveLong");
                Print(string.Format("[{0}] LONG Volatility exit at {1:F2}", Time[0], Close[0]));
            }
        }

        private void ManageShortTrade()
        {
            lowestSinceEntry = Math.Min(lowestSinceEntry, Low[0]);
            double unrealizedTicks = (Position.AveragePrice - Close[0]) / TickSize;

            // ── 1. Breakeven Stop ──
            if (!isTrailing && unrealizedTicks >= BreakevenActivationTicks)
            {
                double breakevenStop = Position.AveragePrice - (2 * TickSize);
                if (breakevenStop < currentStopLoss)
                {
                    currentStopLoss = breakevenStop;
                    SetStopLoss(CalculationMode.Price, currentStopLoss);
                    Print(string.Format("[{0}] SHORT Breakeven activated at {1:F2}", Time[0], currentStopLoss));
                }
            }

            // ── 2. Trailing Stop ──
            double trailingActivation = Position.AveragePrice - (atr[0] * TrailingActivationMultiplier);
            if (Close[0] <= trailingActivation)
            {
                isTrailing = true;
                double newTrailingStop = lowestSinceEntry + (TrailingStepTicks * TickSize);

                if (newTrailingStop < currentStopLoss)
                {
                    currentStopLoss = newTrailingStop;
                    SetStopLoss(CalculationMode.Price, currentStopLoss);
                    Print(string.Format("[{0}] SHORT Trailing stop updated to {1:F2}", Time[0], currentStopLoss));
                }
            }

            // ── 3. Time-Based Exit ──
            if (barsInTrade >= 50 && unrealizedTicks > 0)
            {
                ExitShort("TimeExit", "AdaptiveShort");
                Print(string.Format("[{0}] SHORT Time-based exit at {1:F2}", Time[0], Close[0]));
            }

            // ── 4. Regime Change Exit ──
            if (currentRegime == MarketRegime.Volatile && unrealizedTicks > 8)
            {
                ExitShort("VolatilityExit", "AdaptiveShort");
                Print(string.Format("[{0}] SHORT Volatility exit at {1:F2}", Time[0], Close[0]));
            }
        }

        #endregion

        #region Session & Entry Validation

        private void UpdateSessionStatus()
        {
            TimeSpan currentTime = Time[0].TimeOfDay;

            isInTradingSession = currentTime >= sessionStart && currentTime <= sessionEnd;

            // Optionally avoid the low-liquidity lunch hour
            if (AvoidLunchHour && currentTime >= lunchStart && currentTime <= lunchEnd)
                isInTradingSession = false;
        }

        private bool CanEnterTrade()
        {
            // Must be in active trading session
            if (!isInTradingSession)
                return false;

            // Respect daily trade limit
            if (dailyTradeCount >= MaxDailyTrades)
                return false;

            // Cooldown between trades
            if (barsSinceLastTrade < CooldownBars)
                return false;

            // Don't trade in extremely volatile regime unless signals are very strong
            if (currentRegime == MarketRegime.Volatile && adx[0] < 20)
                return false;

            return true;
        }

        #endregion

        #region Performance Tracking

        protected override void OnExecutionUpdate(Execution execution, string executionId,
            double price, int quantity, MarketPosition marketPosition, string orderId, DateTime time)
        {
            // Track completed trades for adaptive behavior
            if (Position.MarketPosition == MarketPosition.Flat && SystemPerformance.AllTrades.Count > 0)
            {
                Trade lastTrade = SystemPerformance.AllTrades[SystemPerformance.AllTrades.Count - 1];
                double tradePnL = lastTrade.ProfitCurrency;

                recentTradePnL.Add(tradePnL);

                // Maintain rolling window
                if (recentTradePnL.Count > maxTrackingWindow)
                    recentTradePnL.RemoveAt(0);

                // Update win/loss streaks
                if (tradePnL > 0)
                {
                    recentWins++;
                    recentLosses = 0;
                }
                else
                {
                    recentLosses++;
                    recentWins = 0;
                }

                // ── Update Adaptive Multiplier ──
                UpdateAdaptiveMultiplier();

                // ── Log Performance ──
                double winRate = CalculateRecentWinRate();
                Print(string.Format("[{0}] Trade Closed | PnL: ${1:F2} | Recent Win Rate: {2:F1}% | Streak: {3} | Multiplier: {4:F2}",
                    time, tradePnL, winRate * 100, tradePnL > 0 ? "W" + recentWins : "L" + recentLosses, adaptiveMultiplier));
            }
        }

        private void UpdateAdaptiveMultiplier()
        {
            if (!EnableAdaptiveMode || recentTradePnL.Count < 5)
            {
                adaptiveMultiplier = 1.0;
                return;
            }

            double winRate = CalculateRecentWinRate();

            if (winRate >= 0.65)
            {
                // Performing well - slightly lower threshold to take more trades
                adaptiveMultiplier = 0.90;
            }
            else if (winRate >= 0.55)
            {
                // Performing adequately - standard threshold
                adaptiveMultiplier = 1.0;
            }
            else if (winRate >= 0.45)
            {
                // Below target - raise threshold to be more selective
                adaptiveMultiplier = 1.15;
            }
            else
            {
                // Poor performance - significantly raise threshold
                adaptiveMultiplier = 1.30;
            }

            // After consecutive losses, be even more selective
            if (recentLosses >= 3)
                adaptiveMultiplier = Math.Max(adaptiveMultiplier, 1.35);
        }

        private double CalculateRecentWinRate()
        {
            if (recentTradePnL.Count == 0) return 0.5;

            int wins = recentTradePnL.Count(p => p > 0);
            return (double)wins / recentTradePnL.Count;
        }

        #endregion

        #region Properties

        // ════════════════════════════════════════════════
        //  INDICATOR SETTINGS
        // ════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "Fast EMA Period", Order = 1, GroupName = "1. Indicators")]
        public int FastEMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "Slow EMA Period", Order = 2, GroupName = "1. Indicators")]
        public int SlowEMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(20, 200)]
        [Display(Name = "Trend EMA Period", Order = 3, GroupName = "1. Indicators")]
        public int TrendEMAPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ATR Period", Order = 4, GroupName = "1. Indicators")]
        public int ATRPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "ADX Period", Order = 5, GroupName = "1. Indicators")]
        public int ADXPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "RSI Period", Order = 6, GroupName = "1. Indicators")]
        public int RSIPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "MACD Fast", Order = 7, GroupName = "1. Indicators")]
        public int MACDFast { get; set; }

        [NinjaScriptProperty]
        [Range(10, 100)]
        [Display(Name = "MACD Slow", Order = 8, GroupName = "1. Indicators")]
        public int MACDSlow { get; set; }

        [NinjaScriptProperty]
        [Range(3, 50)]
        [Display(Name = "MACD Signal", Order = 9, GroupName = "1. Indicators")]
        public int MACDSignal { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Bollinger Period", Order = 10, GroupName = "1. Indicators")]
        public int BollingerPeriod { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 4.0)]
        [Display(Name = "Bollinger Std Dev", Order = 11, GroupName = "1. Indicators")]
        public double BollingerStdDev { get; set; }

        // ════════════════════════════════════════════════
        //  REGIME THRESHOLDS
        // ════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Range(15, 40)]
        [Display(Name = "ADX Trend Threshold", Order = 1, GroupName = "2. Market Regime")]
        public int ADXTrendThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(30, 60)]
        [Display(Name = "ADX Strong Trend Threshold", Order = 2, GroupName = "2. Market Regime")]
        public int ADXStrongTrendThreshold { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 3.0)]
        [Display(Name = "Volatility Threshold", Order = 3, GroupName = "2. Market Regime")]
        public double VolatilityThreshold { get; set; }

        // ════════════════════════════════════════════════
        //  RISK MANAGEMENT
        // ════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Range(0.5, 5.0)]
        [Display(Name = "ATR Stop Multiplier", Order = 1, GroupName = "3. Risk Management")]
        public double ATRStopMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(1.0, 8.0)]
        [Display(Name = "ATR Target Multiplier", Order = 2, GroupName = "3. Risk Management")]
        public double ATRTargetMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(20, 200)]
        [Display(Name = "Max Stop Loss (Ticks)", Order = 3, GroupName = "3. Risk Management")]
        public int MaxStopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(4, 40)]
        [Display(Name = "Min Stop Loss (Ticks)", Order = 4, GroupName = "3. Risk Management")]
        public int MinStopLossTicks { get; set; }

        [NinjaScriptProperty]
        [Range(0.5, 4.0)]
        [Display(Name = "Trailing Activation (ATR Mult)", Order = 5, GroupName = "3. Risk Management")]
        public double TrailingActivationMultiplier { get; set; }

        [NinjaScriptProperty]
        [Range(2, 40)]
        [Display(Name = "Trailing Step (Ticks)", Order = 6, GroupName = "3. Risk Management")]
        public int TrailingStepTicks { get; set; }

        [NinjaScriptProperty]
        [Range(8, 60)]
        [Display(Name = "Breakeven Activation (Ticks)", Order = 7, GroupName = "3. Risk Management")]
        public int BreakevenActivationTicks { get; set; }

        // ════════════════════════════════════════════════
        //  SIGNAL SETTINGS
        // ════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Range(1.0, 6.0)]
        [Display(Name = "Min Signal Score", Order = 1, GroupName = "4. Signal Settings")]
        public double MinSignalScore { get; set; }

        [NinjaScriptProperty]
        [Range(60, 90)]
        [Display(Name = "RSI Overbought", Order = 2, GroupName = "4. Signal Settings")]
        public int RSIOverbought { get; set; }

        [NinjaScriptProperty]
        [Range(10, 40)]
        [Display(Name = "RSI Oversold", Order = 3, GroupName = "4. Signal Settings")]
        public int RSIOversold { get; set; }

        // ════════════════════════════════════════════════
        //  SESSION & TRADE MANAGEMENT
        // ════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Session Start Hour", Order = 1, GroupName = "5. Session")]
        public int SessionStartHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Session Start Minute", Order = 2, GroupName = "5. Session")]
        public int SessionStartMinute { get; set; }

        [NinjaScriptProperty]
        [Range(0, 23)]
        [Display(Name = "Session End Hour", Order = 3, GroupName = "5. Session")]
        public int SessionEndHour { get; set; }

        [NinjaScriptProperty]
        [Range(0, 59)]
        [Display(Name = "Session End Minute", Order = 4, GroupName = "5. Session")]
        public int SessionEndMinute { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Avoid Lunch Hour", Order = 5, GroupName = "5. Session")]
        public bool AvoidLunchHour { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Max Daily Trades", Order = 6, GroupName = "5. Session")]
        public int MaxDailyTrades { get; set; }

        [NinjaScriptProperty]
        [Range(1, 20)]
        [Display(Name = "Cooldown Bars", Order = 7, GroupName = "5. Session")]
        public int CooldownBars { get; set; }

        [NinjaScriptProperty]
        [Range(5, 50)]
        [Display(Name = "Performance Window", Order = 8, GroupName = "5. Session")]
        public int PerformanceWindow { get; set; }

        // ════════════════════════════════════════════════
        //  ADAPTIVE BEHAVIOR
        // ════════════════════════════════════════════════

        [NinjaScriptProperty]
        [Display(Name = "Enable Adaptive Mode", Order = 1, GroupName = "6. Adaptive")]
        public bool EnableAdaptiveMode { get; set; }

        [NinjaScriptProperty]
        [Range(2, 10)]
        [Display(Name = "Aggressive After Wins", Order = 2, GroupName = "6. Adaptive")]
        public int AggressiveAfterWins { get; set; }

        [NinjaScriptProperty]
        [Range(1, 5)]
        [Display(Name = "Defensive After Losses", Order = 3, GroupName = "6. Adaptive")]
        public int DefensiveAfterLosses { get; set; }

        #endregion
    }
}
