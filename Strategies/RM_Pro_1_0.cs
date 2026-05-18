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
    public class RM_Pro_1_0 : Strategy
    {
        // Internal State Variables
        private double virtualSLPrice = 0.0;
        private double virtualTPPrice = 0.0;
        private bool isStealthActive = false;
        private bool hasTrailedToBreakeven = false;
        
        // Account Position Tracking
        private int currentPosQty = 0;
        private double currentPosPrice = 0.0;
        private MarketPosition currentPosType = MarketPosition.Flat;

        // Drawing Tags
        private const string slTag = "SL_Line";
        private const string tpTag = "TP_Line";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description = "Risk Manager 1.0";
                Name = "RM Pro 1.0";
                Calculate = Calculate.OnEachTick; 
                IsInstantiatedOnEachOptimizationIteration = true;
                
                // REQUIRED: This allows the bot to manage manual Chart Trader entries
                IsUnmanaged = true; 
                
                StealthStopTicks = 20;
                StealthTargetTicks = 20;
                StealthTrailTriggerTicks = 10;
            }
            else if (State == State.Realtime)
            {
                // Hook into your actual broker account to "listen" for your manual clicks
                if (Account != null)
                    Account.PositionUpdate += OnAccountPositionUpdate;
            }
            else if (State == State.Terminated)
            {
                // Clean up the hook when strategy is turned off
                if (Account != null)
                    Account.PositionUpdate -= OnAccountPositionUpdate;
            }
        }

        // The custom account listener for manual Chart Trader entries
        private void OnAccountPositionUpdate(object sender, PositionEventArgs e)
        {
            // Only care about the instrument this strategy is currently running on
            if (e.Position.Instrument != Instrument) return;

            currentPosQty = e.Quantity;
            currentPosPrice = e.AveragePrice;
            currentPosType = e.MarketPosition;

            if (currentPosType == MarketPosition.Long && !isStealthActive)
            {
                isStealthActive = true;
                hasTrailedToBreakeven = false;
                virtualSLPrice = currentPosPrice - (StealthStopTicks * TickSize);
                virtualTPPrice = currentPosPrice + (StealthTargetTicks * TickSize);
                DrawStealthLines();
            }
            else if (currentPosType == MarketPosition.Short && !isStealthActive)
            {
                isStealthActive = true;
                hasTrailedToBreakeven = false;
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
            }
        }

        protected override void OnMarketData(MarketDataEventArgs marketDataUpdate)
        {
            if (!isStealthActive || currentPosType == MarketPosition.Flat) return;

            // THE FIX: Use NinjaTrader's engine variables to prevent "0.0" ghost ticks
            double currentAsk = GetCurrentAsk();
            double currentBid = GetCurrentBid();

            // Safety protocol: If the broker feed drops momentarily, ignore it.
            if (currentAsk == 0 || currentBid == 0) return;

            // --- LONG MANAGEMENT ---
            if (currentPosType == MarketPosition.Long)
            {
                // Trailing Logic
                if (!hasTrailedToBreakeven && currentBid >= currentPosPrice + (StealthTrailTriggerTicks * TickSize))
                {
                    virtualSLPrice = currentPosPrice + (1 * TickSize);
                    hasTrailedToBreakeven = true;
                    DrawStealthLines(); 
                }

                // If Stop Loss OR Take Profit is hit, fire Unmanaged Market Sell
                if (currentBid <= virtualSLPrice || currentBid >= virtualTPPrice)
                {
                    isStealthActive = false; 
                    SubmitOrderUnmanaged(0, OrderAction.Sell, OrderType.Market, currentPosQty, 0, 0, "", ""); 
                }
            }
            // --- SHORT MANAGEMENT ---
            else if (currentPosType == MarketPosition.Short)
            {
                // Trailing Logic
                if (!hasTrailedToBreakeven && currentAsk <= currentPosPrice - (StealthTrailTriggerTicks * TickSize))
                {
                    virtualSLPrice = currentPosPrice - (1 * TickSize);
                    hasTrailedToBreakeven = true;
                    DrawStealthLines(); 
                }

                // If Stop Loss OR Take Profit is hit, fire Unmanaged Market BuyToCover
                if (currentAsk >= virtualSLPrice || currentAsk <= virtualTPPrice)
                {
                    isStealthActive = false; 
                    SubmitOrderUnmanaged(0, OrderAction.BuyToCover, OrderType.Market, currentPosQty, 0, 0, "", ""); 
                }
            }
        }

        private void DrawStealthLines()
        {
            Draw.HorizontalLine(this, slTag, virtualSLPrice, Brushes.Crimson, DashStyleHelper.Dash, 2);
            Draw.HorizontalLine(this, tpTag, virtualTPPrice, Brushes.LimeGreen, DashStyleHelper.Solid, 2);
            ForceRefresh(); 
        }

        #region Properties
        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Stealth Stop (Ticks)", Description="Ticks for invisible stop loss", Order=1, GroupName="Parameters")]
        public int StealthStopTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Stealth Target (Ticks)", Description="Ticks for invisible take profit", Order=2, GroupName="Parameters")]
        public int StealthTargetTicks { get; set; }

        [NinjaScriptProperty]
        [Range(1, int.MaxValue)]
        [Display(Name="Trail Trigger (Ticks)", Description="Ticks in profit to trigger Breakeven", Order=3, GroupName="Parameters")]
        public int StealthTrailTriggerTicks { get; set; }
        #endregion
    }
}