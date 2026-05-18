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
    public class RM_Pro_2_5 : Strategy
    {
        // Internal State Variables
        private double virtualSLPrice = 0.0;
        private double virtualTPPrice = 0.0;
        private bool isStealthActive = false;
        private bool hasTrailedToBreakeven = false;
        private double trailReferencePrice = 0.0; 
        private DateTime entryTime; 
        private DateTime lossStartTime = DateTime.MinValue; 
        
        // Session & PnL Tracking
        private int dailyTradeCount = 0;
        private DateTime lastTradeDate = DateTime.MinValue;
        private double sessionRealizedPnL = 0.0;
        private bool isDailyLossHit = false;
        
        // Internal Last Position Tracking
        private double lastPosPrice = 0.0;
        private int lastPosQty = 0;
        private MarketPosition lastPosType = MarketPosition.Flat;

        // Indicators & Data
        private EMA trendEma;
        private double currentEmaValue = 0.0; 

        // Drawing Objects
        private HorizontalLine slLineObj;
        private HorizontalLine tpLineObj;
        private const string slTag = "SL_Line";
        private const string tpTag = "TP_Line";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Risk Manager 2.5 - The Polling Engine Fix (Compile Error Fixed)";
                Name = "RM Pro 2.5";
                Calculate = Calculate.OnEachTick; 
                IsInstantiatedOnEachOptimizationIteration = true;
                IsUnmanaged = true; 
                
                StealthStopTicks = 20;
                StealthTargetTicks = 60; 
                StealthTrailTriggerTicks = 15; 
                StealthTrailDistanceTicks = 20; 

                UseVolatilityMode = false;
                VolStealthStopTicks = 80;        
                VolStealthTargetTicks = 160;     
                VolStealthTrailTriggerTicks = 40;
                VolBaseTrailDistanceTicks = 40;  
                VolBailoutSeconds = 15;
                VolBailoutTicks = 40;            

                MaxDailyTrades = 10;
                UseTrendFilter = false;
                TrendEmaPeriod = 200;

                UseTimeBomb = true;
                TimeBombSeconds = 180; 
                UseDailyLossLimit = true;
                DailyLossLimitUsd = 1000.0;
            }
            else if (State == State.DataLoaded)
            {
                if (UseTrendFilter)
                {
                    trendEma = EMA(TrendEmaPeriod);
                    trendEma.Plots[0].Brush = Brushes.Cyan; 
                    AddChartIndicator(trendEma);
                }
            }
            else if (State == State.Realtime)
            {
                if (Account != null)
                {
                    Print("\n=======================================");
                    Print($"RM_Pro_2_5: Strategy Armed & Listening.");
                    // THE FIX: Changed Instrument.Name to Instrument.FullName
                    Print($"Monitoring Account: [{Account.Name}] for [{Instrument.FullName}].");
                    Print($"CRITICAL: Your Chart Trader MUST also be set to [{Account.Name}].");
                    Print($"=======================================\n");
                }
            }
        }

        protected override void OnBarUpdate()
        {
            if (UseTrendFilter && trendEma != null && CurrentBar >= TrendEmaPeriod)
            {
                currentEmaValue = trendEma[0];
            }
        }

        private void FlattenPosition(MarketPosition typeToFlatten, int qtyToFlatten)
        {
            if (typeToFlatten == MarketPosition.Long)
                SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, qtyToFlatten, 0, 0, "", " ");
            else if (typeToFlatten == MarketPosition.Short)
                SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, qtyToFlatten, 0, 0, "", " ");
        }

        private void ProcessPositionChange(MarketPosition liveType, int liveQty, double livePrice)
        {
            if (Core.Globals.Now.Date != lastTradeDate)
            {
                dailyTradeCount = 0;
                sessionRealizedPnL = 0.0;
                isDailyLossHit = false;
                lastTradeDate = Core.Globals.Now.Date;
                Print($"RM_Pro: New Session Started. Counters Reset.");
            }

            // PnL TRACKING (Trade Closed or Partially Closed)
            if (lastPosType != MarketPosition.Flat && (liveType == MarketPosition.Flat || liveQty < lastPosQty))
            {
                int closedQty = (liveType == MarketPosition.Flat) ? lastPosQty : (lastPosQty - liveQty);
                double exitPrice = (lastPosType == MarketPosition.Long) ? GetCurrentBid() : GetCurrentAsk();
                
                if (exitPrice != 0) 
                {
                    double pnl = 0;
                    if (lastPosType == MarketPosition.Long) 
                        pnl = (exitPrice - lastPosPrice) * Instrument.MasterInstrument.PointValue * closedQty;
                    else if (lastPosType == MarketPosition.Short) 
                        pnl = (lastPosPrice - exitPrice) * Instrument.MasterInstrument.PointValue * closedQty;
                    
                    sessionRealizedPnL += pnl;
                    Print($"RM_Pro: Executed Exit. PnL: ${pnl:F2}. Session Total: ${sessionRealizedPnL:F2}");
                }
            }

            // ENTRY LOGIC
            if (liveType != MarketPosition.Flat && !isStealthActive)
            {
                if (isDailyLossHit)
                {
                    Print($"RM_Pro: TILT-SWITCH ACTIVE. Trade rejected.");
                    FlattenPosition(liveType, liveQty);
                }
                else if (dailyTradeCount >= MaxDailyTrades)
                {
                    Print($"RM_Pro: Max daily trades reached. Trade rejected.");
                    FlattenPosition(liveType, liveQty);
                }
                else if (UseTrendFilter && currentEmaValue > 0)
                {
                    if (liveType == MarketPosition.Long && livePrice < currentEmaValue)
                    {
                        Print($"RM_Pro: Long rejected. Entry {livePrice} is below EMA {currentEmaValue:F2}.");
                        FlattenPosition(liveType, liveQty);
                    }
                    else if (liveType == MarketPosition.Short && livePrice > currentEmaValue)
                    {
                        Print($"RM_Pro: Short rejected. Entry {livePrice} is above EMA {currentEmaValue:F2}.");
                        FlattenPosition(liveType, liveQty);
                    }
                    else
                    {
                        ApproveTrade(liveType, livePrice);
                    }
                }
                else
                {
                    ApproveTrade(liveType, livePrice);
                }
            }
            // FULL EXIT LOGIC
            else if (liveType == MarketPosition.Flat)
            {
                isStealthActive = false;
                virtualSLPrice = 0.0;
                virtualTPPrice = 0.0;
                lossStartTime = DateTime.MinValue;
                RemoveDrawObject(slTag);
                RemoveDrawObject(tpTag);
                RemoveDrawObject("SL_Text");
                RemoveDrawObject("TP_Text");
                slLineObj = null;
                tpLineObj = null;
                Print("RM_Pro: Flat. Lines removed.");
            }

            lastPosType = liveType;
            lastPosQty = liveQty;
            lastPosPrice = livePrice;
        }

        private void ApproveTrade(MarketPosition liveType, double livePrice)
        {
            dailyTradeCount++;
            isStealthActive = true;
            hasTrailedToBreakeven = false;
            trailReferencePrice = 0.0;
            entryTime = Core.Globals.Now; 
            lossStartTime = Core.Globals.Now; 
            
            int stopTicks = UseVolatilityMode ? VolStealthStopTicks : StealthStopTicks;
            int targetTicks = UseVolatilityMode ? VolStealthTargetTicks : StealthTargetTicks;

            if (liveType == MarketPosition.Long)
            {
                virtualSLPrice = livePrice - (stopTicks * TickSize);
                virtualTPPrice = livePrice + (targetTicks * TickSize);
            }
            else
            {
                virtualSLPrice = livePrice + (stopTicks * TickSize);
                virtualTPPrice = livePrice - (targetTicks * TickSize);
            }
            
            Print($"RM_Pro: Trade Approved! Drawing Lines. SL: {virtualSLPrice}, TP: {virtualTPPrice}");
            DrawStealthLines();
        }

        private double RoundToTick(double price)
        {
            return Math.Round(price / TickSize) * TickSize;
        }

        private int GetActiveTrailDistanceTicks(double currentRefPrice, MarketPosition liveType, double livePrice)
        {
            int baseDistance = UseVolatilityMode ? VolBaseTrailDistanceTicks : StealthTrailDistanceTicks;
            double profitTicks = 0;
            if (liveType == MarketPosition.Long) profitTicks = (currentRefPrice - livePrice) / TickSize;
            else if (liveType == MarketPosition.Short) profitTicks = (livePrice - currentRefPrice) / TickSize;

            int currentDistance = baseDistance;
            if (profitTicks >= 120) currentDistance = Math.Min(baseDistance, 10); 
            else if (profitTicks >= 80) currentDistance = Math.Min(baseDistance, 20);  

            return currentDistance;  
        }

        private void SyncInteractiveLines(MarketPosition liveType, double livePrice)
        {
            if (slLineObj != null)
            {
                double linePrice = RoundToTick(slLineObj.StartAnchor.Price);
                double internalSL = RoundToTick(virtualSLPrice);

                if (Math.Abs(linePrice - internalSL) > (TickSize / 2))
                {
                    virtualSLPrice = linePrice;

                    if (liveType == MarketPosition.Long)
                    {
                        if (virtualSLPrice < livePrice + (1 * TickSize))
                        {
                            hasTrailedToBreakeven = false;
                            trailReferencePrice = 0.0;
                        }
                        else
                        {
                            int dynamicDist = GetActiveTrailDistanceTicks(trailReferencePrice > 0 ? trailReferencePrice : livePrice, liveType, livePrice);
                            trailReferencePrice = virtualSLPrice + (dynamicDist * TickSize);
                        }
                    }
                    else if (liveType == MarketPosition.Short)
                    {
                        if (virtualSLPrice > livePrice - (1 * TickSize))
                        {
                            hasTrailedToBreakeven = false;
                            trailReferencePrice = 0.0;
                        }
                        else
                        {
                            int dynamicDist = GetActiveTrailDistanceTicks(trailReferencePrice > 0 ? trailReferencePrice : livePrice, liveType, livePrice);
                            trailReferencePrice = virtualSLPrice - (dynamicDist * TickSize);
                        }
                    }
                }
            }

            if (tpLineObj != null)
            {
                double linePrice = RoundToTick(tpLineObj.StartAnchor.Price);
                double internalTP = RoundToTick(virtualTPPrice);
                if (Math.Abs(linePrice - internalTP) > (TickSize / 2)) virtualTPPrice = linePrice;
            }
        }

        private void UpdateDynamicText(bool inProfit, MarketPosition liveType, int liveQty, double livePrice)
        {
            if (slLineObj == null || tpLineObj == null) return;

            double currentSLPrice = slLineObj.StartAnchor.Price;
            double currentTPPrice = tpLineObj.StartAnchor.Price;

            double slDiff = (liveType == MarketPosition.Long) ? (currentSLPrice - livePrice) : (livePrice - currentSLPrice);
            double slTicks = Math.Round(slDiff / TickSize);
            double slPoints = slTicks * TickSize;
            double slValue = slTicks * TickSize * Instrument.MasterInstrument.PointValue * liveQty;
            string slSign = slTicks > 0 ? "+" : ""; 
            string slString = $"SL: {slSign}{slPoints} pts | {slSign}{slTicks} ticks | {slSign}${slValue:F2}";

            double tpDiff = (liveType == MarketPosition.Long) ? (currentTPPrice - livePrice) : (livePrice - currentTPPrice);
            double tpTicks = Math.Round(tpDiff / TickSize);
            double tpPoints = tpTicks * TickSize;
            double tpValue = tpTicks * TickSize * Instrument.MasterInstrument.PointValue * liveQty;
            string tpSign = tpTicks > 0 ? "+" : "";
            string tpString = $"TP: {tpSign}{tpPoints} pts | {tpSign}{tpTicks} ticks | {tpSign}${tpValue:F2}";

            if (UseTimeBomb && !inProfit && lossStartTime != DateTime.MinValue)
            {
                TimeSpan remaining = TimeSpan.FromSeconds(Math.Max(0, TimeBombSeconds - (Core.Globals.Now - lossStartTime).TotalSeconds));
                slString += $"   [ BOMB: {remaining.Minutes:D2}:{remaining.Seconds:D2} ]";
            }

            Draw.Text(this, "SL_Text", slString, -5, currentSLPrice, Brushes.White);
            Draw.Text(this, "TP_Text", tpString, -5, currentTPPrice, Brushes.White);
        }

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            // 1. SAFE POLLING ENGINE: Hook directly into the physical account here
            if (Account == null || Account.Positions == null) return;

            Position livePos = Account.Positions.FirstOrDefault(p => p.Instrument == Instrument);
            MarketPosition currentLiveType = livePos != null ? livePos.MarketPosition : MarketPosition.Flat;
            int currentLiveQty = livePos != null ? livePos.Quantity : 0;
            double currentLivePrice = livePos != null ? livePos.AveragePrice : 0.0;

            // 2. DETECT CHANGES
            if (currentLiveType != lastPosType || currentLiveQty != lastPosQty)
            {
                ProcessPositionChange(currentLiveType, currentLiveQty, currentLivePrice);
            }

            // 3. EXECUTION LOGIC
            if (!isStealthActive || currentLiveType == MarketPosition.Flat) return;

            SyncInteractiveLines(currentLiveType, currentLivePrice);

            double currentAsk = GetCurrentAsk();
            double currentBid = GetCurrentBid();

            if (currentAsk == 0 || currentBid == 0) return;

            bool inProfit = false;
            if (currentLiveType == MarketPosition.Long && currentBid > currentLivePrice) inProfit = true;
            if (currentLiveType == MarketPosition.Short && currentAsk < currentLivePrice) inProfit = true;

            // TILT SWITCH
            if (UseDailyLossLimit && !isDailyLossHit)
            {
                double openPnL = 0;
                if (currentLiveType == MarketPosition.Long) 
                    openPnL = (currentBid - currentLivePrice) * Instrument.MasterInstrument.PointValue * currentLiveQty;
                else if (currentLiveType == MarketPosition.Short) 
                    openPnL = (currentLivePrice - currentAsk) * Instrument.MasterInstrument.PointValue * currentLiveQty;

                if ((sessionRealizedPnL + openPnL) <= -DailyLossLimitUsd)
                {
                    isDailyLossHit = true;
                    Print($"RM_Pro: TILT SWITCH FIRED! Locking strategy.");
                    FlattenPosition(currentLiveType, currentLiveQty);
                    return;
                }
            }

            // DYNAMIC TIME BOMB
            if (UseTimeBomb)
            {
                if (inProfit)
                {
                    lossStartTime = DateTime.MinValue; 
                }
                else 
                {
                    if (lossStartTime == DateTime.MinValue) lossStartTime = Core.Globals.Now; 

                    if ((Core.Globals.Now - lossStartTime).TotalSeconds >= TimeBombSeconds)
                    {
                        isStealthActive = false;
                        Print($"RM_Pro: Time-Bomb Executed!");
                        FlattenPosition(currentLiveType, currentLiveQty);
                        return;
                    }
                }
            }

            UpdateDynamicText(inProfit, currentLiveType, currentLiveQty, currentLivePrice);

            int beTriggerTicks = UseVolatilityMode ? VolStealthTrailTriggerTicks : StealthTrailTriggerTicks;

            if (currentLiveType == MarketPosition.Long)
            {
                if (UseVolatilityMode && (Core.Globals.Now - entryTime).TotalSeconds <= VolBailoutSeconds)
                {
                    if (currentBid <= currentLivePrice - (VolBailoutTicks * TickSize))
                    {
                        isStealthActive = false; 
                        Print("RM_Pro: Volatility Bailout Fired!");
                        FlattenPosition(currentLiveType, currentLiveQty);
                        return;
                    }
                }

                if (!hasTrailedToBreakeven)
                {
                    if (currentBid >= currentLivePrice + (beTriggerTicks * TickSize))
                    {
                        double bePrice = currentLivePrice + (1 * TickSize);
                        if (virtualSLPrice < bePrice) 
                        {
                            virtualSLPrice = bePrice;
                            DrawStealthLines(); 
                        }
                        hasTrailedToBreakeven = true;
                        trailReferencePrice = currentBid; 
                    }
                }
                else 
                {
                    if (currentBid > trailReferencePrice)
                    {
                        trailReferencePrice = currentBid; 
                        int activeTrailDist = GetActiveTrailDistanceTicks(trailReferencePrice, currentLiveType, currentLivePrice);
                        double dynamicTrailPrice = trailReferencePrice - (activeTrailDist * TickSize);
                        
                        if (dynamicTrailPrice > virtualSLPrice)
                        {
                            virtualSLPrice = dynamicTrailPrice;
                            DrawStealthLines();
                        }
                    }
                }

                if (currentBid <= virtualSLPrice || currentBid >= virtualTPPrice)
                {
                    isStealthActive = false; 
                    FlattenPosition(currentLiveType, currentLiveQty);
                }
            }
            else if (currentLiveType == MarketPosition.Short)
            {
                if (UseVolatilityMode && (Core.Globals.Now - entryTime).TotalSeconds <= VolBailoutSeconds)
                {
                    if (currentAsk >= currentLivePrice + (VolBailoutTicks * TickSize))
                    {
                        isStealthActive = false; 
                        Print("RM_Pro: Volatility Bailout Fired!");
                        FlattenPosition(currentLiveType, currentLiveQty);
                        return;
                    }
                }

                if (!hasTrailedToBreakeven)
                {
                    if (currentAsk <= currentLivePrice - (beTriggerTicks * TickSize))
                    {
                        double bePrice = currentLivePrice - (1 * TickSize);
                        if (virtualSLPrice > bePrice)
                        {
                            virtualSLPrice = bePrice;
                            DrawStealthLines(); 
                        }
                        hasTrailedToBreakeven = true;
                        trailReferencePrice = currentAsk; 
                    }
                }
                else
                {
                    if (currentAsk < trailReferencePrice)
                    {
                        trailReferencePrice = currentAsk; 
                        int activeTrailDist = GetActiveTrailDistanceTicks(trailReferencePrice, currentLiveType, currentLivePrice);
                        double dynamicTrailPrice = trailReferencePrice + (activeTrailDist * TickSize);
                        
                        if (dynamicTrailPrice < virtualSLPrice)
                        {
                            virtualSLPrice = dynamicTrailPrice;
                            DrawStealthLines();
                        }
                    }
                }

                if (currentAsk >= virtualSLPrice || currentAsk <= virtualTPPrice)
                {
                    isStealthActive = false; 
                    FlattenPosition(currentLiveType, currentLiveQty);
                }
            }
        }

        private void DrawStealthLines()
        {
            slLineObj = Draw.HorizontalLine(this, slTag, virtualSLPrice, Brushes.Crimson, DashStyleHelper.Dash, 2);
            tpLineObj = Draw.HorizontalLine(this, tpTag, virtualTPPrice, Brushes.LimeGreen, DashStyleHelper.Solid, 2);
            
            slLineObj.IsLocked = false;
            tpLineObj.IsLocked = false;
            
            ForceRefresh(); 
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Stealth Stop (Ticks)", Order=1, GroupName="1. Standard Parameters")]
        public int StealthStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Stealth Target (Ticks)", Order=2, GroupName="1. Standard Parameters")]
        public int StealthTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="BE Trigger (Ticks)", Order=3, GroupName="1. Standard Parameters")]
        public int StealthTrailTriggerTicks { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Trail Distance (Ticks)", Order=4, GroupName="1. Standard Parameters")]
        public int StealthTrailDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Display(Name="1. Enable Volatility Mode", Order=1, GroupName="2. Volatility Mode")]
        public bool UseVolatilityMode { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Vol: Stop (Ticks)", Order=2, GroupName="2. Volatility Mode")]
        public int VolStealthStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Vol: Target (Ticks)", Order=3, GroupName="2. Volatility Mode")]
        public int VolStealthTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Vol: BE Trigger (Ticks)", Order=4, GroupName="2. Volatility Mode")]
        public int VolStealthTrailTriggerTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Vol: Base Trail Dist (Ticks)", Order=5, GroupName="2. Volatility Mode")]
        public int VolBaseTrailDistanceTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Vol: Bailout Time (Seconds)", Order=6, GroupName="2. Volatility Mode")]
        public int VolBailoutSeconds { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Vol: Bailout Trigger (Ticks)", Order=7, GroupName="2. Volatility Mode")]
        public int VolBailoutTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Max Trades Per Day", Order=1, GroupName="3. Trading Filters")]
        public int MaxDailyTrades { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable EMA Trend Filter", Order=2, GroupName="3. Trading Filters")]
        public bool UseTrendFilter { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="EMA Period", Order=3, GroupName="3. Trading Filters")]
        public int TrendEmaPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Time Bomb", Order=1, GroupName="4. Account Protection")]
        public bool UseTimeBomb { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Time Bomb Limit (Seconds)", Order=2, GroupName="4. Account Protection")]
        public int TimeBombSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name="Enable Daily Loss Limit (Tilt Switch)", Order=3, GroupName="4. Account Protection")]
        public bool UseDailyLossLimit { get; set; }

        [NinjaScriptProperty]
        [Range(1, double.MaxValue)]
        [Display(Name="Daily Loss Limit ($ USD)", Order=4, GroupName="4. Account Protection")]
        public double DailyLossLimitUsd { get; set; }
        #endregion
    }
}
