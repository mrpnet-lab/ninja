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
    public class RM_Pro_2_3 : Strategy
    {
        // Thread Safety Flag
        private bool positionChangedFlag = false;

        // Internal State Variables
        private double virtualSLPrice = 0.0;
        private double virtualTPPrice = 0.0;
        private bool isStealthActive = false;
        private bool hasTrailedToBreakeven = false;
        private double trailReferencePrice = 0.0; 
        private DateTime entryTime; 
        
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
        
        // Account Position Tracking
        private int currentPosQty = 0;
        private double currentPosPrice = 0.0;
        private MarketPosition currentPosType = MarketPosition.Flat;

        // Drawing Objects
        private HorizontalLine slLineObj;
        private HorizontalLine tpLineObj;
        private const string slTag = "SL_Line";
        private const string tpTag = "TP_Line";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Risk Manager 2.3 - Core Thread-Safety Fix";
                Name = "RM Pro 2.3";
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
                // Subscribe to account events safely
                if (Account != null) Account.PositionUpdate += OnAccountPositionUpdate;
            }
            else if (State == State.Terminated)
            {
                if (Account != null) Account.PositionUpdate -= OnAccountPositionUpdate;
            }
        }

        protected override void OnBarUpdate()
        {
            if (UseTrendFilter && trendEma != null && CurrentBar >= TrendEmaPeriod)
            {
                currentEmaValue = trendEma[0];
            }
        }

        // -------------------------------------------------------------------------
        // THE BUG FIX: The Account Thread can NEVER draw lines or submit orders.
        // It must only record the data and pass a flag to the Main Thread.
        // -------------------------------------------------------------------------
        private void OnAccountPositionUpdate(object sender, PositionEventArgs e)
        {
            if (e.Position.Instrument.FullName != Instrument.FullName) return;

            currentPosQty = e.Quantity;
            currentPosPrice = e.AveragePrice;
            currentPosType = e.MarketPosition;

            // Trigger the main thread to handle this change safely
            positionChangedFlag = true;
        }

        // This runs strictly on the main NinjaScript thread
        private void HandlePositionChangeOnMainThread()
        {
            if (Core.Globals.Now.Date != lastTradeDate)
            {
                dailyTradeCount = 0;
                sessionRealizedPnL = 0.0;
                isDailyLossHit = false;
                lastTradeDate = Core.Globals.Now.Date;
                Print($"RM_Pro: New Session Started. PnL and Trade Counts Reset to 0.");
            }

            // PnL TRACKING
            if (currentPosType == MarketPosition.Flat && lastPosType != MarketPosition.Flat)
            {
                double exitPrice = (lastPosType == MarketPosition.Long) ? GetCurrentBid() : GetCurrentAsk();
                if (exitPrice != 0) 
                {
                    double pnl = 0;
                    if (lastPosType == MarketPosition.Long) 
                        pnl = (exitPrice - lastPosPrice) * Instrument.MasterInstrument.PointValue * lastPosQty;
                    else if (lastPosType == MarketPosition.Short) 
                        pnl = (lastPosPrice - exitPrice) * Instrument.MasterInstrument.PointValue * lastPosQty;
                    
                    sessionRealizedPnL += pnl;
                    Print($"RM_Pro: Trade Closed. Realized PnL: ${pnl:F2}. Session Total: ${sessionRealizedPnL:F2}");
                }
            }

            // ENTRY LOGIC
            if ((currentPosType == MarketPosition.Long || currentPosType == MarketPosition.Short) && !isStealthActive)
            {
                if (isDailyLossHit)
                {
                    Print($"RM_Pro: TILT-SWITCH ACTIVE! Session loss is ${sessionRealizedPnL:F2}. Trade rejected.");
                    FlattenPosition();
                    return;
                }

                if (dailyTradeCount >= MaxDailyTrades)
                {
                    Print($"RM_Pro: Max daily trades ({MaxDailyTrades}) reached. Trade rejected.");
                    FlattenPosition();
                    return;
                }

                if (UseTrendFilter && currentEmaValue > 0)
                {
                    if (currentPosType == MarketPosition.Long && currentPosPrice < currentEmaValue)
                    {
                        Print($"RM_Pro: Long entry {currentPosPrice} below EMA {currentEmaValue:F2}. Rejected.");
                        FlattenPosition();
                        return;
                    }
                    if (currentPosType == MarketPosition.Short && currentPosPrice > currentEmaValue)
                    {
                        Print($"RM_Pro: Short entry {currentPosPrice} above EMA {currentEmaValue:F2}. Rejected.");
                        FlattenPosition();
                        return;
                    }
                }

                // Trade Approved
                dailyTradeCount++;
                isStealthActive = true;
                hasTrailedToBreakeven = false;
                trailReferencePrice = 0.0;
                entryTime = Core.Globals.Now; 
                
                int stopTicks = UseVolatilityMode ? VolStealthStopTicks : StealthStopTicks;
                int targetTicks = UseVolatilityMode ? VolStealthTargetTicks : StealthTargetTicks;

                if (currentPosType == MarketPosition.Long)
                {
                    virtualSLPrice = currentPosPrice - (stopTicks * TickSize);
                    virtualTPPrice = currentPosPrice + (targetTicks * TickSize);
                }
                else
                {
                    virtualSLPrice = currentPosPrice + (stopTicks * TickSize);
                    virtualTPPrice = currentPosPrice - (targetTicks * TickSize);
                }
                
                Print($"RM_Pro: Trade Approved! Drawing Lines -> SL: {virtualSLPrice}, TP: {virtualTPPrice}");
                DrawStealthLines();
            }
            // EXIT LOGIC
            else if (currentPosType == MarketPosition.Flat)
            {
                isStealthActive = false;
                virtualSLPrice = 0.0;
                virtualTPPrice = 0.0;
                RemoveDrawObject(slTag);
                RemoveDrawObject(tpTag);
                slLineObj = null;
                tpLineObj = null;
            }

            lastPosType = currentPosType;
            lastPosPrice = currentPosPrice;
            lastPosQty = currentPosQty;
        }

        private void FlattenPosition()
        {
            if (currentPosType == MarketPosition.Long)
                SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, currentPosQty, 0, 0, "", " ");
            else if (currentPosType == MarketPosition.Short)
                SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, currentPosQty, 0, 0, "", " ");
        }

        private double RoundToTick(double price)
        {
            return Math.Round(price / TickSize) * TickSize;
        }

        private int GetActiveTrailDistanceTicks(double currentRefPrice)
        {
            int baseDistance = UseVolatilityMode ? VolBaseTrailDistanceTicks : StealthTrailDistanceTicks;
            double profitTicks = 0;
            if (currentPosType == MarketPosition.Long) profitTicks = (currentRefPrice - currentPosPrice) / TickSize;
            else if (currentPosType == MarketPosition.Short) profitTicks = (currentPosPrice - currentRefPrice) / TickSize;

            int currentDistance = baseDistance;
            if (profitTicks >= 120) currentDistance = Math.Min(baseDistance, 10); 
            else if (profitTicks >= 80) currentDistance = Math.Min(baseDistance, 20);  

            return currentDistance;  
        }

        private void SyncInteractiveLines()
        {
            if (slLineObj != null)
            {
                double linePrice = RoundToTick(slLineObj.StartAnchor.Price);
                double internalSL = RoundToTick(virtualSLPrice);

                if (Math.Abs(linePrice - internalSL) > (TickSize / 2))
                {
                    virtualSLPrice = linePrice;

                    if (currentPosType == MarketPosition.Long)
                    {
                        if (virtualSLPrice < currentPosPrice + (1 * TickSize))
                        {
                            hasTrailedToBreakeven = false;
                            trailReferencePrice = 0.0;
                        }
                        else
                        {
                            int dynamicDist = GetActiveTrailDistanceTicks(trailReferencePrice > 0 ? trailReferencePrice : currentPosPrice);
                            trailReferencePrice = virtualSLPrice + (dynamicDist * TickSize);
                        }
                    }
                    else if (currentPosType == MarketPosition.Short)
                    {
                        if (virtualSLPrice > currentPosPrice - (1 * TickSize))
                        {
                            hasTrailedToBreakeven = false;
                            trailReferencePrice = 0.0;
                        }
                        else
                        {
                            int dynamicDist = GetActiveTrailDistanceTicks(trailReferencePrice > 0 ? trailReferencePrice : currentPosPrice);
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

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            // PROCESS THE ACCOUNT QUEUE SAFELY ON THE MAIN THREAD
            if (positionChangedFlag)
            {
                positionChangedFlag = false;
                HandlePositionChangeOnMainThread();
            }

            if (!isStealthActive || currentPosType == MarketPosition.Flat) return;

            SyncInteractiveLines();

            double currentAsk = GetCurrentAsk();
            double currentBid = GetCurrentBid();

            if (currentAsk == 0 || currentBid == 0) return;

            // TILT-SWITCH LOGIC
            if (UseDailyLossLimit && !isDailyLossHit)
            {
                double openPnL = 0;
                if (currentPosType == MarketPosition.Long) 
                    openPnL = (currentBid - currentPosPrice) * Instrument.MasterInstrument.PointValue * currentPosQty;
                else if (currentPosType == MarketPosition.Short) 
                    openPnL = (currentPosPrice - currentAsk) * Instrument.MasterInstrument.PointValue * currentPosQty;

                if ((sessionRealizedPnL + openPnL) <= -DailyLossLimitUsd)
                {
                    isDailyLossHit = true;
                    Print($"RM_Pro: TILT SWITCH FIRED! Floating loss crossed limit. Locking strategy.");
                    FlattenPosition();
                    return;
                }
            }

            // TIME-BOMB LOGIC
            if (UseTimeBomb)
            {
                if ((Core.Globals.Now - entryTime).TotalSeconds >= TimeBombSeconds)
                {
                    bool notInProfit = false;
                    if (currentPosType == MarketPosition.Long && currentBid <= currentPosPrice) notInProfit = true;
                    if (currentPosType == MarketPosition.Short && currentAsk >= currentPosPrice) notInProfit = true;

                    if (notInProfit)
                    {
                        isStealthActive = false;
                        Print($"RM_Pro: Time-Bomb Executed! Trade stagnated for {TimeBombSeconds}s.");
                        FlattenPosition();
                        return;
                    }
                }
            }

            int beTriggerTicks = UseVolatilityMode ? VolStealthTrailTriggerTicks : StealthTrailTriggerTicks;

            if (currentPosType == MarketPosition.Long)
            {
                if (UseVolatilityMode && (Core.Globals.Now - entryTime).TotalSeconds <= VolBailoutSeconds)
                {
                    if (currentBid <= currentPosPrice - (VolBailoutTicks * TickSize))
                    {
                        isStealthActive = false; 
                        Print("RM_Pro: Volatility Bailout Fired!");
                        FlattenPosition();
                        return;
                    }
                }

                if (!hasTrailedToBreakeven)
                {
                    if (currentBid >= currentPosPrice + (beTriggerTicks * TickSize))
                    {
                        double bePrice = currentPosPrice + (1 * TickSize);
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
                        int activeTrailDist = GetActiveTrailDistanceTicks(trailReferencePrice);
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
                    FlattenPosition();
                }
            }
            else if (currentPosType == MarketPosition.Short)
            {
                if (UseVolatilityMode && (Core.Globals.Now - entryTime).TotalSeconds <= VolBailoutSeconds)
                {
                    if (currentAsk >= currentPosPrice + (VolBailoutTicks * TickSize))
                    {
                        isStealthActive = false; 
                        Print("RM_Pro: Volatility Bailout Fired!");
                        FlattenPosition();
                        return;
                    }
                }

                if (!hasTrailedToBreakeven)
                {
                    if (currentAsk <= currentPosPrice - (beTriggerTicks * TickSize))
                    {
                        double bePrice = currentPosPrice - (1 * TickSize);
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
                        int activeTrailDist = GetActiveTrailDistanceTicks(trailReferencePrice);
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
                    FlattenPosition();
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
