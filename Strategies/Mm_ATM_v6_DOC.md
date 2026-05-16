# Mm_ATM v6 — User Documentation

**Version:** 6.4.2 (2026-04-30)
**Instrument:** NQ / MNQ futures (NinzaRenko 64/16 brick chart recommended)
**Platform:** NinjaTrader 8

---

## 1. What This Strategy Is

`Mm_ATM_v6` is an **anti-Market-Maker brick-trail strategy** for NQ.
It blends adaptive ATR-based stops with NinzaRenko brick logic to:

1. **Beat MM stop-hunts** — multi-tier loss caps + smart break-even ladder + flip-grace windows.
2. **Let runners run** — body-anchor brick trail + per-regime peak-lock floors + auto-tuning.
3. **Skip toxic regimes** — chop filter, SL cluster cooldown, directional lockout, news blackout.
4. **Catch monster moves early** — early-entry-on-flip + pre-trend-chase + pullback re-entry.
5. **Self-protect daily** — adaptive daily target with soft cap, hard kill, and giveback protector.

---

## 2. Trade Lifecycle (How A Trade Flows)

```
┌──────────────────────────────────────────────────────────────────────┐
│ 1. SIGNAL  → bull/bear confidence ≥ MinSignalConfidence             │
│              + regime (TREND_UP/DN, CHOP, UNKNOWN)                   │
│              + HTF bias check                                        │
├──────────────────────────────────────────────────────────────────────┤
│ 2. GATES   → time-of-day, daily limits, news, chop, extension,      │
│              SL cluster, directional lockout, post-win cooldown     │
│   (BYPASS  → TrendChase / PreTrendChase / EarlyFlip / Pullback)     │
├──────────────────────────────────────────────────────────────────────┤
│ 3. ENTRY   → bracket OCO with hard SL + TP, EntryDelaySeconds wait  │
├──────────────────────────────────────────────────────────────────────┤
│ 4. PROTECT → Smart BE Ladder (T1 loss-cap, T2 soft-BE)              │
│            + Profit Safeguard tiers (T1-T4 ratchet)                 │
│            + Fast Reversal Exit (early bars)                        │
├──────────────────────────────────────────────────────────────────────┤
│ 5. TRAIL   → Brick Trail (body-anchor) → Peak-Lock Floor (tiered)   │
│            + In-Bar tick trail + Px-Stop (ADX-adaptive retrace)     │
│            + Per-regime multiplier (TREND 1.20× / CHOP 0.80×)       │
├──────────────────────────────────────────────────────────────────────┤
│ 6. EXIT    → Brick-flip / Px-Stop / In-Bar / Trail / Hard SL/TP     │
│   (RECORD  → capture-ratio sample → nightly trail auto-tune)        │
├──────────────────────────────────────────────────────────────────────┤
│ 7. RE-ENTRY→ Brick re-entry (90s window) or Pullback re-entry (15m) │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 3. Quick-Start Parameter Cheat-Sheet

These are the knobs you adjust **most often**. Defaults are tuned for NQ + NinzaRenko 64/16.

| Knob | Default | When to change |
|------|---------|----------------|
| `Contracts` | 1 | Increase position size |
| `SlPoints` | 18 | Wider on volatile days, tighter in chop |
| `TpPoints` | 25 | Mostly irrelevant — trail handles exits |
| `MaxDailyLossDollars` | 1000 | Your hard daily floor |
| `MaxDailyProfitDollars` | 2000 | Soft cap; doubles to hard kill |
| `MaxTradesPerDay` | 20 | 0 = unlimited |
| `MinSignalConfidence` | 55 | Higher = fewer/better entries |
| `EnableDiagLog` | false | **Turn ON** for forensics during testing |
| `AutoMode` | true | Off = manual buttons only |

---

## 4. Full Parameter Reference (by category)

### 4.1 Risk Management

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `SlPoints` | int | 18 | 1–500 | Initial stop-loss in NQ pts (×$20) |
| `TpPoints` | int | 25 | 1–500 | Initial take-profit in NQ pts |
| `MaxDailyLossDollars` | int | 1000 | 500–20000 | Hard daily loss kill |
| `MaxDailyProfitDollars` | int | 2000 | 500–50000 | **Soft cap**; 2× = hard kill (Phase 3) |
| `MaxTradesPerDay` | int | 20 | 0–999 | 0 = unlimited |
| `AllowMultiEntryPerBar` | bool | true | — | Multiple entries per bar after exit |
| `Contracts` | int | 1 | 1–10 | Contracts per entry |
| `MaxContracts` | int | 4 | 1–20 | Max total position |
| `SkipPrevDayClampOnHighAdx` | bool | true | — | Don't clamp TP to prevDay H/L on strong-trend days |
| `HighAdxThreshold` | double | 28.0 | 15–60 | ADX above which clamp skipped |
| `ResetDailyOnRestart` | bool | true | — | Disable+enable clears today's PnL |

### 4.2 Fast Reversal Exit (anti-MM sweep, early bars)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `FastReversalExitEnabled` | bool | true | — | Cut loss in half on early adverse + 2 confirms |
| `FastReversalAtrFactor` | double | 0.6 | 0.2–1.5 | Adverse > factor × ATR triggers |
| `FastReversalMaxBars` | int | 4 | 1–12 | Only first N bars after entry |
| `FastReversalMinAdversePts` | double | 4.0 | 1–20 | Hard floor on adverse pts |

### 4.3 Mode

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `AutoMode` | bool | true | — | Auto-entry signals fire |
| `AutoStrategy` | int | 2 | 0–2 | 0=Mom+VWAP, 1=KeyLevelBounce, 2=Auto |
| `MinSignalConfidence` | double | 55 | 20–100 | Min bull/bear conf % for entry |
| `EntryDelaySeconds` | int | 5 | 0–60 | Wait sec before order submit |

### 4.4 Trail / Stop-Loss (Legacy ATR Trail)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `TrailEnabled` | bool | true | — | Master ON for ATR trail |
| `TrailActivationPoints` | int | 8 | 1–100 | Engage trail at N pts profit |
| `TrailAtrMultiplier` | double | 2.0 | 0.5–5 | Trail dist = factor × ATR |
| `SmartTrailBacktrackTicks` | int | 4 | 0–12 | Anti-MM relax (ticks) on detected hunt |
| `AggressiveTrailMaxAtrFactor` | double | 0.5 | 0.1–2.0 | TRL NOW factor |
| `RunnerAtrFactor` | double | 1.5 | 0.5–5.0 | Runner Mode trail width |
| `RunnerMinPts` | double | 4.0 | 1–20 | Runner Mode floor |
| `BreakevenAtPoints` | int | 8 | 2–40 | Move SL to BE at N pts (off if SmartBE on) |
| `BeSafeAtrFactor` | double | 0.35 | 0–1.5 | Safety distance from price |
| `BeSafeMinTicks` | int | 6 | 2–40 | Hard floor for BE distance |
| `BreakevenEnabled` | bool | true | — | Master ON for auto BE |
| `JumpSlPercent` | int | 50 | 10–95 | Jump SL button % of distance-to-profit |
| `SlTpAdjustStep` | int | 1 | 1–50 | SL/TP ± nudge (pts) |
| `ManualTickMode` | bool | true | — | ± buttons use tick precision |

### 4.5 Auto-Tighten / Auto-Widen (streak-based trail)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `AutoTightenOnLosses` | bool | false | — | Tighten trail after N losses |
| `AutoTightenLossN` | int | 2 | 1–10 | Loss-streak threshold |
| `AutoTightenFactor` | double | 0.5 | 0.1–1.0 | Trail × factor (lower = tighter) |
| `AutoWidenOnWins` | bool | false | — | Widen trail after N wins |
| `AutoWidenWinN` | int | 3 | 1–10 | Win-streak threshold |
| `AutoWidenFactor` | double | 1.5 | 1.0–3.0 | Trail × factor (higher = looser) |

### 4.6 Profit Safeguard ($-based ratchet, fires in ALL modes incl. Runner)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `ProfitSafeguardEnabled` | bool | true | — | Master ON |
| `ProfitSafeguardT1Pts` / `T1SlPts` | double | 10 / -2 | — | Tier 1: peak 10 → SL entry−2 |
| `ProfitSafeguardT2Pts` / `T2SlPts` | double | 20 / 1 | — | Tier 2: peak 20 → soft BE+1 |
| `ProfitSafeguardT3Pts` / `T3SlPts` | double | 30 / 8 | — | Tier 3: peak 30 → lock +8 |
| `ProfitSafeguardT4Pts` / `T4SlPts` | double | 50 / 15 | — | Tier 4: peak 50 → lock +15 |

### 4.7 Smart BE Ladder (Phase 3.0 — replaces legacy BE)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `SmartBeEnabled` | bool | true | — | 3-tier loss-cap → soft-BE → handoff |
| `SmartBeTier1PeakPts` | double | 4.0 | 2–12 | T1 arm (peak ≥ N) |
| `SmartBeTier1MaxLossPts` | double | 6.0 | 2–12 | T1 cap distance below entry |
| `SmartBeTier2PeakPts` | double | 6.0 | 3–15 | T2 arm |
| `SmartBeTier2LockPts` | double | 0.75 | 0.25–4 | T2 lock above entry |

### 4.8 Brick Trail Suite (Phase 2.3 — primary exit engine)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EnableBrickTrail` | bool | true | — | **MASTER** — body-anchor + flip + in-bar + PxStop |
| `BrickModeAutoOnly` | bool | true | — | Brick logic AUTO trades only |
| `TrlNowOverridesBrick` | bool | true | — | TRL NOW button bypasses brick mode |
| `BrickTrailTightnessPct` | double | 50 | 20–100 | % of brick for trail dist |
| `BrickTrailPriceStopEnabled` | bool | true | — | Enforce price-stop exit |
| `BrickTrailPriceStopMinPeakPts` | double | 8.0 | 2–30 | PxStop only after peak ≥ N |
| `BrickTrailExtremeNearPct` | double | 25 | 0–50 | Tighten near brick extreme |
| `BrickTrailExtremeTightnessPct` | double | 25 | 10–75 | Tightness % when near extreme |
| `BrickTrailPriceStopMinRetracePts` | double | 5.0 | 0–20 | Noise filter: peak−curr ≥ N |
| `BrickTrailMinStreak` | int | 4 | 2–20 | Min streak before brick trail engages |
| `BrickTrailBufferTicks` | double | 4.0 | 1–20 | Trail price buffer past prev brick |
| `BrickTrailRequireTrendRegime` | bool | true | — | Trend regime required |
| `BrickTrailMaxGivebackPct` | int | 0 | 0–100 | 0 = disabled (recommended) |
| `BrickTrailGivebackMinPeakPts` | double | 20 | 5–100 | Giveback only after peak ≥ N |
| `BrickTrailGivebackStallSec` | int | 45 | 10–600 | Stall-gate seconds |

### 4.9 In-Bar Tick Trail

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `InBarTrailEnabled` | bool | true | — | Aggressive intra-bar tick trail |
| `InBarTrailMinPeakPts` | double | 25 | 5–100 | Arm threshold |
| `InBarTrailGivebackPts` | double | 16 | 4–40 | Max retrace from peak |
| `InBarTrailStallSec` | int | 25 | 0–120 | Stall-gate seconds |

### 4.10 Brick Re-Entry (Phase 2.8)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `BrickReentryEnabled` | bool | true | — | Re-enter after brick-trail win |
| `BrickReentryWindowSec` | int | 90 | 15–600 | Arm window seconds |
| `BrickReentryMinStreak` | int | 3 | 2–10 | Min same-dir streak required |
| `BrickReentryMaxCount` | int | 1 | 1–5 | Max re-entries per parent win |

### 4.11 Adaptive BrickTrail Retrace (peak-scaled, Phase 3.0)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `BrickTrailAdaptiveRetraceEnabled` | bool | true | — | Scale PxStop retrace by peak |
| `BrickTrailAdaptiveRetracePctOfPeak` | double | 0.30 | 0.1–0.6 | Pct of peak as retrace |
| `BrickTrailAdaptiveRetraceMinPts` | double | 2.5 | 1–8 | Floor |
| `BrickTrailAdaptiveRetraceMaxPts` | double | 7.5 | 3–15 | Ceiling |

### 4.12 Smart Trail (anti-flip-trap + ADX-adaptive, Phase 2.7)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `FlipExitGraceSec` | int | 45 | 0–300 | Suppress flip exit first N sec |
| `FlipExitMinOppositeCnt` | int | 1 | 1–5 | Required opposite bricks before exit |
| `PxStopAdaptiveEnabled` | bool | true | — | Scale by ADX slope + streak |
| `PxStopAdxRisingMult` | double | 1.5 | 1.0–3.0 | Strong trend wider |
| `PxStopAdxFallingMult` | double | 0.7 | 0.3–1.0 | Dying trend tighter |
| `PxStopStrongStreakMin` | int | 5 | 2–15 | Streak qualifying as strong |

### 4.13 Regime Classifier (Phase 1.1)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EnableRegimeClassifier` | bool | false | — | Label TREND_UP/DN/CHOP/SQUEEZE/UNKNOWN |
| `EnableBrickAnalytics` | bool | false | — | Track run length / wick / brick speed |

### 4.14 UNKNOWN-Regime Block (Phase 2.4)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EnableUnknownRegimeBlock` | bool | false | — | Block AUTO when regime UNKNOWN |
| `UnknownBlockMinStreak` | int | 6 | 2–20 | Override if streak ≥ N |
| `UnknownBlockMinAdxSlope` | double | 0.0 | -5–10 | Override if ADX rising |

### 4.15 Early Entry On Flip (Phase 2.7)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EnableEarlyEntryOnFlip` | bool | true | — | Bypass blocks on fresh brick flip |
| `EarlyEntryMinStreak` | int | 3 | 2–8 | Min flip streak |
| `EarlyEntryMaxStreak` | int | 6 | 3–15 | Max — past = not fresh |
| `EarlyEntryMinDistVwapAtr` | double | 0.8 | 0–3.0 | Min VWAP distance (ATRs) |
| `EarlyEntryMaxDistVwapAtr` | double | 3.0 | 1.5–6.0 | Trap filter cap |
| `EarlyEntryMaxRunFavPts` | double | 25 | 10–60 | Trap: max favorable already run |
| `EarlyEntryConfRelief` | double | 15 | 0–30 | Relax MinSignalConfidence by N |
| `EarlyEntryAlsoBypassHtf` | bool | true | — | Also bypass HTF bias |

### 4.16 TrendChase (Phase 3.2 — bypass gates in clear trend)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `TrendChaseEnabled` | bool | true | — | Bypass extension/htf/tape/cooldown in TREND |
| `TrendChaseMinAdx` | double | 22 | 20–60 | Min ADX |
| `TrendChaseMinStreak` | int | 3 | 2–10 | Min brick streak |
| `TrendChaseMaxStreak` | int | 60 | 10–80 | Exhaustion cap (raised v4.1) |
| `CounterTrendBlockEnabled` | bool | true | — | Block opposite dir in trend |
| `CounterTrendMinAdx` | double | 40 | 25–70 | Block above this ADX |

### 4.17 PreTrendChase (Phase 3.3 — catch ahead of ADX)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `PreTrendChaseEnabled` | bool | true | — | Treat UNKNOWN+streak as TrendChase |
| `PreTrendChaseMinStreak` | int | 5 | 3–12 | Min brick streak |
| `PreTrendChaseMaxStreak` | int | 25 | 5–40 | Exhaustion cap (raised v4.1) |
| `PreTrendChaseMinConf` | double | 55 | 30–120 | Min bull/bear conf (v4.1 lowered) |
| `PreTrendChaseMinAdxSlope` | double | -0.25 | -2–2 | Min ADX slope |
| `HtfStalenessOverrideStreak` | int | 5 | 4–30 | Bypass HTF block (v4.0 lowered) |

### 4.18 Pullback Re-Entry (Phase 3.7)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `PullbackReentryEnabled` | bool | true | — | Re-enter after big run + retrace |
| `PullbackReentryMinRunLen` | int | 20 | 10–80 | Min run length to arm |
| `PullbackReentryMinMaxFavPts` | double | 40 | 20–200 | Min run favorable pts |
| `PullbackReentryRetracePctNormal` | double | 0.30 | 0.1–0.6 | Required retrace % |
| `PullbackReentryRetracePctStrongHtf` | double | 0.20 | 0.1–0.5 | Strong-HTF retrace % |
| `PullbackReentryStrongHtfThresh` | int | 7 | 4–20 | HTF consec threshold |
| `PullbackReentryMinContBricks` | int | 2 | 1–10 | Min continuation bricks |
| `PullbackReentryWindowSec` | int | 900 | 60–3600 | Arm window seconds |

### 4.19 Extension Filter + Auto-Loosen

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `ExtensionFilterEnabled` | bool | true | — | Block over-extended entries |
| `ExtensionMaxAtrFromVwap` | double | 5.0 | 2–15 | Block above N ATRs from VWAP |
| `ExtensionMinAtrPoints` | double | 8.0 | 1–50 | Only enforce when ATR ≥ N |
| `ExtensionAutoLoosenEnabled` | bool | true | — | Loosen on strong HTF + streak |
| `ExtLooseMinHtfConsec` | int | 7 | 4–20 | Min HTF consec |
| `ExtLooseMinStreak` | int | 3 | 1–15 | Min streak |
| `ExtLooseMaxAtrFromVwap` | double | 8.0 | 5–25 | Loose cap |

### 4.20 Chop Filter

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `ChopFilterEnabled` | bool | true | — | Block in chop |
| `ChopAdxMin` | double | 18 | 5–40 | ADX below = chop |
| `ChopAdxFallingBars` | int | 3 | 1–10 | Falling bars to confirm |
| `ChopEmaSepMinAtr` | double | 0.30 | 0.05–2.0 | Min EMA gap (ATRs) |
| `ChopRangeBars` | int | 5 | 2–30 | Range measurement window |
| `ChopRangeMaxAtr` | double | 1.0 | 0.2–5.0 | Max range/ATR before chop |
| `ChopBlockOppositeTape` | bool | true | — | Block on opposite tape |
| `ChopOppositeTapeMin` | double | 0.05 | 0–1.0 | Tape delta threshold |
| `ChopTrendAdxGate` | double | 22 | 10–60 | Skip chop tests above this ADX |

### 4.21 Cooldowns & Lockouts

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `SlClusterCooldownEnabled` | bool | true | — | Toxic-regime cooldown |
| `SlClusterCount` | int | 2 | 2–10 | SL exits triggering |
| `SlClusterWindowMin` | int | 60 | 5–240 | Sliding window min |
| `SlClusterCooldownMin` | int | 15 | 1–120 | Cooldown min |
| `PostWinSameDirCooldownEnabled` | bool | true | — | Block same-dir after win |
| `PostWinSameDirCooldownMin` | int | 7 | 1–60 | Block min |
| `PostWinDistanceReleaseEnabled` | bool | true | — | Release on price proof |
| `PostWinDistanceReleasePts` | double | 8 | 2–30 | Pts to release |
| `DirLockoutEnabled` | bool | true | — | Lock dir after N losses |
| `DirLockoutLossN` | int | 2 | 2–10 | Loss count |
| `DirLockoutWindowMin` | int | 30 | 5–240 | Window min |
| `DirLockoutCooldownMin` | int | 30 | 5–240 | Lockout min |

### 4.22 Adaptive Window (loss-streak conf boost)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `AdaptiveWindowEnabled` | bool | true | — | Tighten conf on loss streak |
| `AdaptiveWindowSize` | int | 5 | 2–20 | Rolling window size |
| `AdaptiveWindowLossThreshold` | int | 3 | 1–20 | Losses to activate |
| `AdaptiveWindowClearWins` | int | 2 | 1–20 | Wins to clear |
| `AdaptiveConfBoost` | int | 5 | 0–50 | +Conf when active |

### 4.23 Liquidity Sweep + DCA Suppression

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `LiquiditySweepBoostEnabled` | bool | true | — | Boost opposite signal after MM sweep |
| `LiquiditySweepLookback` | int | 30 | 5–200 | Lookback bars |
| `LiquiditySweepConfBoost` | double | 8 | 0–30 | Pts off MinConf |
| `SuppressDcaOnLossStreak` | bool | true | — | Block DCA after losses |
| `SuppressDcaLossN` | int | 2 | 1–10 | Loss threshold |

### 4.24 Aggressive Exits

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `AggressiveExitsEnabled` | bool | true | — | AGGR mode master |
| `AggrBeAtPoints` | int | 3 | 1–30 | BE lock |
| `AggrTrailActivationPts` | double | 4.0 | 1–30 | Trail arm |
| `AggrTrailDistPts` | double | 2.0 | 0.5–20 | Trail dist |
| `AggrPullbackAtrFactor` | double | 0.4 | 0.1–2.0 | Pullback × ATR |
| `AggrPullbackMaxBars` | int | 2 | 1–30 | Pullback bars |
| `AggrAdverseExitEnabled` | bool | false | — | OPT-IN (worse perf in tests) |
| `AggrAdverseMaxBars` | int | 2 | 1–10 | Adverse window |
| `AggrAdverseAtrFactor` | double | 0.5 | 0.1–2.0 | Adverse threshold |
| `AggrAdverseMinPts` | double | 5.0 | 1–20 | Adverse floor |
| `AggrAdverseDisarmPeak` | double | 3.0 | 0.5–20 | Disarm at peak |

### 4.25 Hours / Time-of-Day SL / News

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `TradingHoursEnabled` | bool | true | — | Restrict trading hours |
| `TradingStartTime` | int | 93000 | HHMMSS | Start (9:30 ET) |
| `FlattenTime` | int | 160000 | HHMMSS | Auto-flatten (4:00 ET) |
| `TimeOfDaySlSizingEnabled` | bool | false | — | Per-window SL multiplier |
| `SodOpenStart/End/SlMult` | — | 93000/103000/1.30 | — | Open window (wider) |
| `SodMiddayStart/End/SlMult` | — | 103000/140000/0.80 | — | Midday (tighter) |
| `SodCloseStart/End/SlMult` | — | 150000/160000/1.20 | — | Close (wider) |
| `NewsBlackoutEnabled` | bool | true | — | Block around news times |
| `NewsBlackoutTimes` | string | "083000,100000,140000" | — | HHMMSS list |
| `NewsBlackoutWindowMin` | int | 2 | 0–60 | ± min |

### 4.26 Indicators

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EmaPeriodFast` | int | 9 | 3–50 | Fast EMA |
| `EmaPeriodSlow` | int | 21 | 10–200 | Slow EMA |
| `RsiPeriod` | int | 14 | 5–30 | RSI |
| `AtrPeriod` | int | 14 | 5–30 | ATR |
| `HtfEmaPeriod` | int | 50 | 10–200 | HTF 5-min EMA |

### 4.27 Renko / NinzaRenko

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EnableRenkoSeries` | bool | true | — | 64/16 Renko secondary |
| `RenkoBrickSize` | int | 64 | 4–256 | Brick (ticks) |
| `RenkoBrickOffset` | int | 16 | 0–256 | Offset (ticks) |
| `EnableNinzaRenkoSeries` | bool | true | — | NinzaRenko 3rd-party series |
| `NinzaCustomSlot` | int | 0 | 0–9999 | Raw BarsPeriodType int |
| `UsePrimaryAsNinzaRenko` | bool | true | — | Use chart bars directly if NinzaRenko chart |

### 4.28 Smart Logic / Display

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EnableTrapDetector` | bool | true | — | MM trap signature scoring |
| `OrderFlowFilterEnabled` | bool | true | — | Block on aggressive opposite tape |
| `ShowEma` | bool | true | — | Draw EMAs |
| `ShowVwap` | bool | true | — | Draw VWAP |

### 4.29 Diagnostics

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `EnableDiagLog` | bool | false | — | Write CSV to `Documents\NinjaTrader 8\MmATM_v6_DiagLog_*.csv` |

### 4.30 Regime Quality Gate (Phase 3.0)

| Property | Type | Default | Range | What it does |
|----------|------|---------|-------|--------------|
| `RegimeQualityGateEnabled` | bool | true | — | Tighten entries for losing regime only |
| `RegimeQualityNegativeThresh` | double | -200 | -1000–-50 | $ threshold per regime |
| `RegimeQualityUnknownMinDistVwapAtr` | double | 3.0 | 1–6 | UNKNOWN tightened: VWAP dist required |

---

## 5. Internal Phases (no user knobs — always-on)

| Phase | Feature | Behavior |
|-------|---------|----------|
| **4.0** | **Peak-Lock Trail Floor** | Tiered max-giveback: peak ≥15→cap 10, ≥25→12, ≥40→15, ≥60→18, ≥80→22 pts. Per-regime mult: TREND ×1.20 (let runners run), CHOP/UNKNOWN ×0.80 (lock faster). |
| **4.1** | **Streak-Hold Trail** | Requires ≥1 brick retrace before flip exit when streak alive in dir. |
| **4.2 / Phase 3** | **Adaptive Daily Target** | Soft cap at `MaxDailyProfitDollars` (TrendChase-only mode). Hard kill at 2× target. Giveback protector flattens at -30% from peak. |
| **4.2 / Phase 4** | **Silent-Block Diagnostics** | Emits `BLOCK_AUTO,reason=...` row whenever a hard gate (hours/daily/kill/maxTrades) silently rejects an entry. Throttled per (reason+bar). |
| **4.2 / Phase 5** | **Trail Auto-Tune** | Captures (profitPts/peakPts) per regime, 60-sample rolling. Once per day if ≥30 samples: avg<0.50 → widen tiers +2pt, avg>0.80 → tighten -2pt. Emits `TRAIL_TUNE_DAILY` row. |

---

## 6. Diagnostics Log

When `EnableDiagLog=true`, CSV written to:
`C:\Users\<you>\Documents\NinjaTrader 8\MmATM_v6_DiagLog_<date>.csv`

**Key event types to grep:**

| Tag | Meaning |
|-----|---------|
| `ENTRY_LONG` / `ENTRY_SHORT` | Entry fired with regime/conf/streak |
| `EXIT_WIN_*` / `EXIT_LOSS_*` | Trade closed with reason suffix |
| `BLOCK_AUTO,reason=` | Silent gate rejection (Phase 4) |
| `DAILY_SOFT_CAP` | Soft profit cap engaged (Phase 3) |
| `DAILY_GIVEBACK` | Giveback protector flattened (Phase 3) |
| `TRAIL_TUNE_DAILY` | Nightly auto-tune (Phase 5) |
| `PEAK_LOCK_T*` | Peak-Lock tier engaged |
| `BRICK_FLIP_EXIT` | Flip exit fired |
| `PX_STOP_EXIT` | Price-stop exit fired |
| `INBAR_TRAIL_EXIT` | In-bar tick trail exit |
| `REGIME_PNL` | Per-regime intraday $ tracker |
| `SESSION_ROLLOVER` | 18:00 daily reset |

---

## 7. Manual Buttons (Dashboard)

| Button | Action |
|--------|--------|
| **BUY / SELL** | Manual market entry (bypasses auto gates) |
| **FLAT** | Close THIS strategy's position only (no disable) |
| **KILL** | Flatten + set `emergencyKillActive` (no new entries until rearmed) |
| **TRL NOW** | Force aggressive ATR trail (bypasses brick mode for rest of trade) |
| **BE** | Move SL to breakeven now |
| **JUMP SL** | Move SL by `JumpSlPercent` of distance to current price |
| **+SL / -SL / +TP / -TP** | Nudge by `SlTpAdjustStep` (or 1 tick if `ManualTickMode`) |
| **RUNNER** | Toggle Runner Mode (wider trail, suppresses Smart BE) |

---

## 8. Recommended Tuning Workflow

1. **Baseline:** keep defaults, run 1 week of playback on representative days.
2. **Enable diag** (`EnableDiagLog=true`) → review CSV for `BLOCK_AUTO`, `EXIT_*` reasons.
3. **If too few entries:** lower `MinSignalConfidence` (55→50) or `PreTrendChaseMinConf`.
4. **If too many losers:** raise `AdaptiveWindowLossThreshold` trigger or enable `EnableUnknownRegimeBlock`.
5. **If runners cut short:** raise `BrickTrailAdaptiveRetracePctOfPeak` (0.30→0.40), or `InBarTrailGivebackPts`.
6. **If giving back too much:** lower `BrickTrailAdaptiveRetracePctOfPeak`, or shrink Profit Safeguard tiers.
7. **Watch `TRAIL_TUNE_DAILY`** rows — auto-tune adjusts tiers ±2pt nightly based on capture ratio.

---

## 9. Known Constants

```
NQ_TICKS_PER_POINT  = 4.0
NQ_DOLLARS_PER_POINT = 20.0
1 brick (NinzaRenko 64) = 16 NQ pts = $320
```

---

## 10. Version History

| Version | Highlights |
|---------|-----------|
| **v6 4.2** | Adaptive daily target (soft cap/hard kill/giveback), silent-block diag, trail auto-tune, per-regime peak-lock mult |
| **v6 4.1** | Streak-Hold trail, TrendChaseMaxStreak 25→60, PreTrendChase relaxed |
| **v6 4.0** | Peak-Lock Trail Floor (5 tiers), cooldown 8→4, htfStaleness 8→5 |
| **v6 3.7** | Pullback Re-Entry, Extension Auto-Loosen |
| **v6 3.3** | PreTrendChase (catch ahead of ADX) |
| **v6 3.2** | TrendChase override (bypass extension/HTF/tape in TREND) |
| **v6 3.0** | Smart BE Ladder, Adaptive Retrace, Regime-Quality Gate |
| **v6 2.8** | Brick Re-Entry |
| **v6 2.7** | Smart Trail (anti-flip-trap, ADX-adaptive PxStop), Early-Entry on Flip |
| **v6 2.3** | Brick Trail suite (master engine) |
| **v6 1.1** | Regime Classifier |
