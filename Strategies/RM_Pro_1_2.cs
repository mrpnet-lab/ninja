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
    public class RM_Pro_1_2 : Strategy
    {
        // Internal State Variables
        private double virtualSLPrice = 0.0;
        private double virtualTPPrice = 0.0;
        private bool isStealthActive = false;
        private bool hasTrailedToBreakeven = false;
        private double trailReferencePrice = 0.0; // NEW: Tracks the high/low watermark for continuous trailing
        
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
                Description = "Risk Manager 1.2 - True Continuous Trailing & Reset";
                Name = "RM Pro 1.2";
                Calculate = Calculate.OnEachTick; 
                IsInstantiatedOnEachOptimizationIteration = true;
                IsUnmanaged = true; 
                
                StealthStopTicks = 20;
                StealthTargetTicks = 40;
                StealthTrailTriggerTicks = 10;
                StealthTrailDistanceTicks = 15; // NEW: How far behind price the trail follows
            }
            else if (State == State.Realtime)
            {
                if (Account != null) Account.PositionUpdate += OnAccountPositionUpdate;
            }
            else if (State == State.Terminated)
            {
                if (Account != null) Account.PositionUpdate -= OnAccountPositionUpdate;
            }
        }

        private void OnAccountPositionUpdate(object sender, PositionEventArgs e)
        {
            if (e.Position.Instrument != Instrument) return;

            currentPosQty = e.Quantity;
            currentPosPrice = e.AveragePrice;
            currentPosType = e.MarketPosition;

            if (currentPosType == MarketPosition.Long && !isStealthActive)
            {
                isStealthActive = true;
                hasTrailedToBreakeven = false;
                trailReferencePrice = 0.0;
                virtualSLPrice = currentPosPrice - (StealthStopTicks * TickSize);
                virtualTPPrice = currentPosPrice + (StealthTargetTicks * TickSize);
                DrawStealthLines();
            }
            else if (currentPosType == MarketPosition.Short && !isStealthActive)
            {
                isStealthActive = true;
                hasTrailedToBreakeven = false;
                trailReferencePrice = 0.0;
                virtualSLPrice = currentPosPrice + (StealthStopTicks * TickSize);
                virtualTPPrice = currentPosPrice - (StealthTargetTicks * TickSize);
                DrawStealthLines();
            }
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
        }

        private double RoundToTick(double price)
        {
            return Math.Round(price / TickSize) * TickSize;
        }

        // NEW LOGIC: Dynamic Drag & Drop Syncing
        private void SyncInteractiveLines()
        {
            if (slLineObj != null)
            {
                double linePrice = RoundToTick(slLineObj.StartAnchor.Price);
                double internalSL = RoundToTick(virtualSLPrice);

                if (Math.Abs(linePrice - internalSL) > (TickSize / 2))
                {
                    virtualSLPrice = linePrice;

                    // LONG Reset/Recalibrate Logic
                    if (currentPosType == MarketPosition.Long)
                    {
                        if (virtualSLPrice < currentPosPrice + (1 * TickSize))
                        {
                            // User dragged below Break-Even: RESET the system entirely.
                            hasTrailedToBreakeven = false;
                            trailReferencePrice = 0.0;
                        }
                        else
                        {
                            // User dragged while in profit: Recalibrate the peak marker so it trails cleanly from the new drop point
                            trailReferencePrice = virtualSLPrice + (StealthTrailDistanceTicks * TickSize);
                        }
                    }
                    // SHORT Reset/Recalibrate Logic
                    else if (currentPosType == MarketPosition.Short)
                    {
                        if (virtualSLPrice > currentPosPrice - (1 * TickSize))
                        {
                            hasTrailedToBreakeven = false;
                            trailReferencePrice = 0.0;
                        }
                        else
                        {
                            trailReferencePrice = virtualSLPrice - (StealthTrailDistanceTicks * TickSize);
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
            if (!isStealthActive || currentPosType == MarketPosition.Flat) return;

            SyncInteractiveLines();

            double currentAsk = GetCurrentAsk();
            double currentBid = GetCurrentBid();

            if (currentAsk == 0 || currentBid == 0) return;

            // --- LONG MANAGEMENT ---
            if (currentPosType == MarketPosition.Long)
            {
                // 1. Initial Break-Even Trigger
                if (!hasTrailedToBreakeven)
                {
                    if (currentBid >= currentPosPrice + (StealthTrailTriggerTicks * TickSize))
                    {
                        double bePrice = currentPosPrice + (1 * TickSize);
                        if (virtualSLPrice < bePrice) 
                        {
                            virtualSLPrice = bePrice;
                            DrawStealthLines(); 
                        }
                        hasTrailedToBreakeven = true;
                        trailReferencePrice = currentBid; // Set the peak watermark
                    }
                }
                // 2. True Continuous Trailing Logic
                else 
                {
                    if (currentBid > trailReferencePrice)
                    {
                        trailReferencePrice = currentBid; // Update highest seen price
                        double dynamicTrailPrice = trailReferencePrice - (StealthTrailDistanceTicks * TickSize);
                        
                        // Only step the stop loss UP, never down
                        if (dynamicTrailPrice > virtualSLPrice)
                        {
                            virtualSLPrice = dynamicTrailPrice;
                            DrawStealthLines();
                        }
                    }
                }

                // Execution
                if (currentBid <= virtualSLPrice || currentBid >= virtualTPPrice)
                {
                    isStealthActive = false; 
                    SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, currentPosQty, 0, 0, "", ""); 
                }
            }
            // --- SHORT MANAGEMENT ---
            else if (currentPosType == MarketPosition.Short)
            {
                // 1. Initial Break-Even Trigger
                if (!hasTrailedToBreakeven)
                {
                    if (currentAsk <= currentPosPrice - (StealthTrailTriggerTicks * TickSize))
                    {
                        double bePrice = currentPosPrice - (1 * TickSize);
                        if (virtualSLPrice > bePrice)
                        {
                            virtualSLPrice = bePrice;
                            DrawStealthLines(); 
                        }
                        hasTrailedToBreakeven = true;
                        trailReferencePrice = currentAsk; // Set the lowest watermark
                    }
                }
                // 2. True Continuous Trailing Logic
                else
                {
                    if (currentAsk < trailReferencePrice)
                    {
                        trailReferencePrice = currentAsk; // Update lowest seen price
                        double dynamicTrailPrice = trailReferencePrice + (StealthTrailDistanceTicks * TickSize);
                        
                        // Only step the stop loss DOWN, never up
                        if (dynamicTrailPrice < virtualSLPrice)
                        {
                            virtualSLPrice = dynamicTrailPrice;
                            DrawStealthLines();
                        }
                    }
                }

                // Execution
                if (currentAsk >= virtualSLPrice || currentAsk <= virtualTPPrice)
                {
                    isStealthActive = false; 
                    SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, currentPosQty, 0, 0, "", ""); 
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
        [Display(Name="Stealth Stop (Ticks)", Description="Initial invisible stop loss", Order=1, GroupName="Parameters")]
        public int StealthStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Stealth Target (Ticks)", Description="Invisible take profit", Order=2, GroupName="Parameters")]
        public int StealthTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="1. BE Trigger (Ticks)", Description="Ticks in profit to move to Breakeven", Order=3, GroupName="Parameters")]
        public int StealthTrailTriggerTicks { get; set; }
        
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="2. Trail Distance (Ticks)", Description="How closely the line follows the price after BE", Order=4, GroupName="Parameters")]
        public int StealthTrailDistanceTicks { get; set; }
        #endregion
    }
}
