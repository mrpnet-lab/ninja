# RM_Pro_Spec_1_2

## v1.2 Updates: True Continuous Trailing & Dynamic Recalibration
The previous version (`1.1`) utilized a static Break-Even jump. Once the trigger was hit, it locked at `Entry + 1 tick` and stayed there. 
Version `1.2` upgrades this to a **True Continuous Trail**. 

### 1. The Continuous Trail Engine
* **New Parameter:** `StealthTrailDistanceTicks`.
* **How it works:** 1. Market reaches your initial trigger (e.g., +10 ticks). The Red Line snaps to Break-Even.
  2. As the market continues to trend in your favor (e.g., +25 ticks), the internal engine tracks the "High Watermark" (`trailReferencePrice`).
  3. The Red Line will now *automatically follow the price upward*, staying exactly `StealthTrailDistanceTicks` behind the peak. If the market reverses, the Red Line locks in place.

### 2. Smart User Dragging & Recalibration
Because we now have a continuous trail, user dragging requires "Smart Recalibration" to prevent the strategy from fighting the user.

* **Drag DOWN (Below Break-Even):** If you drag the line out of profit and back into loss territory, the system **fully resets**. It turns off the continuous trail and waits for the price to hit the initial `BE Trigger` again before taking over.
* **Drag UP (In Profit):**
  If the market is moving up, and you decide to drag the Red Line *tighter* to the price than the algorithm is currently tracking, the strategy adapts. It recalibrates its High Watermark to your new placement, ensuring it trails smoothly from your manually selected anchor point without snapping back.

---

### Strategy For Profitability (How to configure v1.2)
To maximize the "Win-Rate Trap" fix from the original data analysis:
1. **Initial Stop:** Keep it fixed. Give the trade room to breathe based on market structure.
2. **BE Trigger (`StealthTrailTriggerTicks`):** Set this fairly tight (e.g., +10 to +15 ticks on NQ). This protects your capital fast.
3. **Continuous Trail (`StealthTrailDistanceTicks`):** Set this wide (e.g., 20 to 30 ticks). Once you are at Break-Even, you want to let the runner *run*. A tight continuous trail will choke the trade on minor pullbacks. Give the trend room to fluctuate while locking in profit behind it.
