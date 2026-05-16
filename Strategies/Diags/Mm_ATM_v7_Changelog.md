# Mm_ATM_v7 — Version & PnL Changelog

A running ledger of every v7 release, what it changed, and how it performed in playback.
This complements `Mm_ATM_v7_spec.md` (design intent) and `Mm_ATM_v7_DOC.md` (user reference).

---

## v7 1.0 (2026-05-01) — "Reversal Rider" — REVERTED ❌

**Status: ROLLED BACK to v7 0.3 same day. PnL was MOST NEGATIVE of any v7 release.** Backup: `Old/Mm_ATM_v7_BKP_v1.0_FAILED_20260501.cs`. See `Mm_ATM_v7_spec.md` §21.3 for full root-cause analysis & lessons.

**One-line lesson:** Entering on the FIRST opposite-color brick in `TREND_*` is a trap-magnet (16% of all runs are 1-brick MM traps), and replacing v0.3's adaptive-retrace PxStop with a tight touch-exit gave back ALL the noise tolerance that protects winners.

**Code DELETED. v7 0.3 ($2,250) is the active baseline again.**

---

## v7 1.0 (2026-05-01) — "Reversal Rider" — original release notes (preserved for forensic)

**Status: ROLLED BACK to v7 0.3 base + 3 surgical patches. v7 0.4 and v7 0.5 logic DELETED entirely.**

**Goal:** Early entry on the first opposite-color brick in TREND_X (no more late entries waiting for streak≥12). Trail one brick behind with hidden SL synced to trail. Re-enter on MM-trap fakeouts.

**Why rollback:** v7 0.4 (+$1,770, RIDE auto-engage) and v7 0.5 (-$685, SL_CHASE) both regressed vs v7 0.3 (+$2,250). Their toggles were guesswork that became dead code. User correctly identified the pattern: "we are creating garbage of logic lines we will never use."

### Backups (kept for forensic, NOT in active code)
- `Old/Mm_ATM_v7_BKP_v0.3_2K_winner_20260501.cs` — v7 0.3 +$2,250 winner (now the active baseline)
- `Old/Mm_ATM_v7_BKP_v0.5_FAILED_20260501.cs` — v7 0.5 -$685 failure (so we don't repeat mistakes)

### Changes shipped (3 patches, ONE master switch)

| # | Change | Behavior |
|---|--------|----------|
| A | **RR Entry** | On opposite-color brick close in TREND_X → arm reversal candidate (record body). Next bar, if color matches AND body ≥ `reversalConfirmBodyPct` (50%) of armed body → enter at market. Bypasses streak/conf gates that caused late entries. Diag: `RR_ARMED`, `RR_FIRE`, `RR_CANCEL`. |
| B | **RR Tight Trail** | While in trade in TREND_X with same-dir streak ≥ 2: trail = `prevBrickLow − 3tk` (LONG) / `prevBrickHigh + 3tk` (SHORT). Hidden SL ratcheted to trail itself (LONG: SL up to trail; SHORT: SL down to trail). **Touch-exit** (no retrace gate, no min-peak gate). Diag: `RR_TIGHT_HIT`. |
| C | **RR Trap Re-entry** | After RR_TIGHT exit, if 1-2 bricks later the same color resumes AND we are still in matching TREND → re-enter at market. Max `maxTrapReentries` (2) per trend. Resets when regime leaves TREND_*. Diag: `RR_TRAP_REENTRY`. |

### Parameters (all ON by default — no dead toggles)

| Field | Default | Purpose |
|-------|---------|---------|
| `reversalRiderEnabled` | true | Single master switch for all 3 patches above |
| `tightTrailBufferTicks` | 3 | Buffer beyond prev-brick extreme |
| `reversalConfirmBodyPct` | 50.0 | Confirm brick body must be ≥ this % of armed brick body |
| `maxTrapReentries` | 2 | Max re-entries per trend after RR_TIGHT_HIT |

### Build status: ✅ no compile errors

### Validation checklist (run after playback)
- [ ] PnL > $2,250 (must beat v7 0.3 baseline OR roll back)
- [ ] At least one `RR_ARMED` + `RR_FIRE` pair in 09:35 morning SHORT setup
- [ ] At least one `RR_TIGHT_HIT` followed by `RR_TRAP_REENTRY` (proves trap re-entry works)
- [ ] Entry timestamps EARLIER than v7 0.3 (less `emaXAgo` = entered closer to reversal)
- [ ] No catastrophic SL hits ≥ -$300 (tight trail + SL=trail should cap losses tight)

### Failure modes & rollback rule
**HARD RULE:** if v7 1.0 PnL < v7 0.3 ($2,250), revert. Backup `Old/Mm_ATM_v7_BKP_v0.3_2K_winner_20260501.cs` is the rollback target — single-command restore.

| Failure | Likely cause | Tweak |
|---|---|---|
| RR_FIRE never triggers | reversalConfirmBodyPct too high | lower 50 → 30 |
| RR_TIGHT_HIT chops every winner | tightTrailBufferTicks too tight | raise 3 → 5 |
| RR_TRAP_REENTRY enters into reversals | regime check too loose | require streak ≥ 3 of resumed color |

---

## v7 0.5 (2026-05-01) — "Tight Trail or Range Adapt" — REVERTED ❌

**Result: -$685 / 13 trades (all SL exits, zero trail wins).** SL_CHASE put hard SL 2 ticks closer than trail → SL hit before trail logic could engage. Code DELETED in v7 1.0 rollback. Backup: `Old/Mm_ATM_v7_BKP_v0.5_FAILED_20260501.cs`.

## v7 0.4 (2026-05-01) — "Ride The Trend" — REVERTED ❌

**Result: +$1,770 / 12 trades.** Captured the +$2,000 single-best trade ever (14:25 LONG runner) but morning RIDE held into 2 SLs (-$260, -$415). Net regression vs v7 0.3. Code DELETED in v7 1.0 rollback.

---

## v7 0.3 (2026-04-30) — "Ride The Brick" — 🏆 BEST EVER (now baseline for v7 1.0)

**Goal:** Keep RIDE's home-run upside (the +$2,000 14:25 winner) while fixing the morning blow-ups (-$260, -$415) where RIDE held into reversals. Plus adapt to range-bound when the trend dies.

**Trigger forensic (v7 0.4 Playback-4, +$1,770 / 12 trades):** RIDE_AUTO_ON fired 8x. One trade printed +$2,000 (best single ever). Two morning RIDE trades took -$260 + -$415 SLs because PxStop was suppressed and brick-flip didn't fire fast enough. Net -$770 vs v7 0.3 in the morning, +$970 vs v7 0.3 in the afternoon big runner.

**User insight (verbatim):** *"trail one brick behind (top of brick for SHORT, bottom for LONG), VERY tight. When price hits the trail, exit. If still trending, re-enter. Move SL as close as possible to the trail. If no trend, smart trail must adapt to range bound."*

### Changes shipped

| # | Change | Code surface | Behavior |
|---|--------|--------------|----------|
| A | **RIDE_TIGHT_TRAIL** — 1-brick-behind exit | brick-trail body-anchor block (~L2245) | When RIDE is ON AND regime is TREND_*, exit on first tick crossing `prevBrickHigh + rideTightBufferTicks (1)` (SHORT) / `prevBrickLow - rideTightBufferTicks` (LONG). Catches reversals 1 brick faster than brick-flip. |
| B | **RIDE_REGIME_FLIP_OFF** | brick close handler (~L5210) | If `runnerModeActive_user==true` AND auto-engaged AND regime degrades to CHOP/UNKNOWN/SQUEEZE, auto-disengage so normal PxStop+InBar resume (range-bound mode). |
| C | **SL_CHASE** | brick-trail body-anchor block (~L2230) | Every brick close, ratchet `hiddenStopPrice` toward `trailPrice ± slChaseGapTicks (2)`. Never widens, never crosses trail, never crosses current price. Works in RIDE and non-RIDE. |

### New parameters

| Field | Default | Purpose |
|-------|---------|---------|
| `rideTightTrailEnabled` | true | Master switch for 1-brick-behind exit during RIDE+TREND |
| `rideTightBufferTicks` | 1 | Buffer beyond prev-brick extreme |
| `rideRegimeFlipOff` | true | Auto-disengage RIDE when regime leaves TREND_* |
| `slChaseEnabled` | true | Master switch for SL→trail ratchet |
| `slChaseGapTicks` | 2 | Hidden SL stays this many ticks behind trail |

### New diag tags

| Tag | When | Use |
|-----|------|-----|
| `RIDE_TIGHT_TRAIL_HIT` | 1-brick-behind exit fired during RIDE+TREND | Confirms patch caught a reversal cleanly |
| `RIDE_REGIME_FLIP_OFF` | Auto-RIDE disengaged on regime degradation | Confirms range-bound adapt fired |
| `SL_CHASE` | Hidden SL ratcheted toward trail | Audit how close SL is sitting |

### Build status: ✅ no compile errors

### Validation checklist (run after playback)
- [ ] PnL ≥ +$2,500 (recover v7 0.3 baseline + capture more upside)
- [ ] Morning 09:35 SHORT: at least one `RIDE_TIGHT_TRAIL_HIT` row instead of -$260 SL
- [ ] 14:25 LONG runner: still captures ≥ +$1,500 (tight-trail follows behind 1 brick on a clean run)
- [ ] At least one `RIDE_REGIME_FLIP_OFF` row when regime flips during a held position
- [ ] `SL_CHASE` rows accumulate during long holds (proves SL is sitting behind trail)
- [ ] No `EXIT_LOSS_SL` >= -$300 during RIDE-active windows

### Failure modes & next-step plan
- **Tight-trail too tight — chops every winner:** raise `rideTightBufferTicks` from 1 to 2 or 3.
- **SL_CHASE crosses trail and stops out early:** widen `slChaseGapTicks` from 2 to 4.
- **RIDE_REGIME_FLIP_OFF too aggressive (one-bar regime dip kills RIDE):** add hysteresis (require N bars of non-TREND).

### Deferred (for v7 0.6+)
- Range-bound MEAN-REVERT adapter: when regime is CHOP and we hold a position, replace giveback PxStop with VWAP/EMA target exit
- Dormant-trail SL tightening (peak ≥ 5pt while trail still dormant)
- 10:40–11:30 LONG miss fix: streak=51 / 188pt run blocked by post-loss cooldown + htfBlockL + extension filter
- REGIME_CHANGE hysteresis (single-bar adxSlope dip flipping TREND_X → UNKNOWN)
- Phase 5 cross-session capture-ratio persistence

---

## v7 0.4 (2026-05-01) — "Ride The Trend"

**Goal:** Stop slicing alive trends into 4-6 small wins. Let the trail follow behind the bricks until a real reversal (brick-flip) closes the position.

**Trigger forensic (v7 0.3 Playback-3):** Best-ever PnL **+$2,250 / 32 trades**, but the morning 09:35–09:47 SHORT cluster was sliced into **6 entries** all on the same downtrend (sum +$60 net incl. one -$415 trap). The 17-brick R streak printed ~80pt favourable but PxStop kept firing on 4-7pt intra-streak retraces. RIDE_LOCK (v7 0.3) widened the retrace gate but PxStop is still the wrong shape of exit for an alive impulse — the only correct exit is **brick-flip** (or hard SL).

**User insight (verbatim):** *"add a toggle or use the run off (current set to off) to let the trail knows that it should follow behind in the opposite side of the bar following it. This will allow the trail follows several bricks without exit until the reverse."*

### Changes shipped

| # | Change | Code surface | Behavior |
|---|--------|--------------|----------|
| A | **RIDE MODE** — repurpose dashboard `RUN ON/OFF` button | InBar exit (~L2049) + PxStop exit (~L2280) wrapped with `!runnerModeActive_user` | When RUN is ON, both PxStop and InBar exits are SUPPRESSED. The trade exits ONLY on (1) brick-flip, (2) hard SL, (3) PEAK-LOCK floor. BrickTrail anchor (body+buffer) still ratchets and shows on the chart. |
| B | **AUTO-RIDE** — auto-engage on strong streak | `ProcessPrimaryAsNinzaRenkoBar` (~L5210) | When alive same-dir streak ≥ `rideAutoStreak (7)` AND regime is TREND_X AND htfBias agrees, auto-set `runnerModeActive_user = true`. Cleared on flat (existing OnPositionClose reset). |
| C | **Diag emits** | both PxStop and InBar paths | `RIDE_MODE_HOLD` (per-bar, when an exit was suppressed) + `RIDE_AUTO_ON` / `RIDE_AUTO_OFF`. |

### New parameters

| Field | Default | Purpose |
|-------|---------|---------|
| `rideModeAutoEnabled` | true | Master switch for AUTO ride engage (manual button always works) |
| `rideAutoStreak` | 7 | Streak gate to auto-engage RIDE |
| `rideRequireHtfAgree` | true | Also require htfBias matches direction before auto-engaging |

### New diag tags

| Tag | When | Use |
|-----|------|-----|
| `RIDE_MODE_HOLD` | PxStop or InBar would have fired but RIDE is active | Proves patch held a winner that v7 0.3 would have cut |
| `RIDE_AUTO_ON` | Auto-engage triggered on brick close | Confirms streak gate fired |
| `RIDE_AUTO_OFF` | Position went flat with auto-engage active | Cleanup audit |

### Build status: ✅ no compile errors

### Validation checklist (run after playback)
- [ ] PnL ≥ +$2,500 (lower bound) / ≥ +$3,500 stretch
- [ ] At least one `RIDE_AUTO_ON` row in 09:35–09:42 window
- [ ] At least one `RIDE_MODE_HOLD,src=px_stop` row in same window (proves the noise PxStop was suppressed)
- [ ] Morning SHORT cluster collapses from 6 trades to 1–2
- [ ] No new SL hits caused by RIDE holding too long (hard-SL count should stay near v7 0.3's 6)
- [ ] PEAK-LOCK still fires on extreme peaks (catastrophe net intact)

### Failure modes & next-step plan
- **RIDE holds too long, gives back winnings:** raise `rideAutoStreak` to 10; OR add a peak-trim that auto-disengages RIDE if `(peak-cur)/peak >= 0.40`.
- **Brick-flip too slow on flash reversals:** raise `flipExitMinOppositeCnt` from 1 to 2 (already configurable).
- **Auto-engages in chop:** make `rideRequireHtfAgree` mandatory or add ADX > 25 gate.

### Deferred (for v7 0.5+)
- Dormant-trail SL tightening (peak ≥ 5pt while trail still dormant)
- 10:40–11:30 LONG miss fix: streak=51 / 188pt run blocked by post-loss cooldown + htfBlockL + extension filter
- REGIME_CHANGE hysteresis (single-bar adxSlope dip flipping TREND_X → UNKNOWN)
- Phase 5 cross-session capture-ratio persistence

---

## v7 0.3 (2026-04-30) — "Ride The Brick"

**Goal:** Stop the trail from bailing on alive same-direction brick streaks. v7 0.2 playback proved over-trading inside single trends is the next big leak.

**Trigger forensic:** 4/30 09:35 SHORT entered TREND_DN streak=13, exited 51 sec later at +$190 on a 4.0pt retrace. The streak was STILL ALIVE 17 R bricks; RUN_END logged maxFav=80pt (~$1,600/contract). Strategy then re-entered 4 more times slicing the same trend into 5 trades. Root cause: `pxStopAdxFallingMult (0.7x)` tightened `effRetracePts` from 2.9 → 2.03 on a tiny ADX wobble (slope=-0.57) inside an alive 17-brick streak.

### Changes shipped (1 surgical patch)

| # | Change | Code surface | Behavior |
|---|--------|--------------|----------|
| A | **RIDE_LOCK in PxStop** | brick-trail PxStop adaptive block | When alive same-dir streak ≥ `rideStreakMin (5)` AND last same-color brick within `rideRecentBrickSec (30s)` AND `lastAdxSlope > rideMinSlope (-2.0)`: compute `rideMult = min(1.5, 1 + (streak−min) × 0.05)`. Final `effRetracePts = max(adaptive-output, baseline × rideMult)`. Override falling-mult; trail breathes wider as streak grows. Emits `RIDE_LOCK_PXSTOP` (throttled per-bar). |

### New parameters

| Field | Default | Purpose |
|-------|---------|---------|
| `rideLockEnabled` | true | Master switch |
| `rideStreakMin` | 5 | Min streak to enable RIDE_LOCK |
| `rideMinSlope` | -2.0 | Override fallingMult only when slope > this (not in true collapse) |
| `rideStreakPerBrickBonus` | 0.05 | +5% per brick beyond rideStreakMin |
| `rideStreakMaxMult` | 1.5 | Cap streak-scaled multiplier |
| `rideRecentBrickSec` | 30 | Last same-color brick must be this fresh |

### New diag tag

| Tag | When | Use |
|-----|------|-----|
| `RIDE_LOCK_PXSTOP` | Ride bonus inflated effRetracePts in PxStop check | Confirms patch fired; logs `streak/slope/base/prevMult_eff/rideMult/newEff/peak/cur` |

### Build status: ✅ no compile errors

### Validation checklist (run after playback)
- [ ] PnL ≥ +$1,500 (lower bound: trail holds the morning SHORT cluster)
- [ ] Stretch PnL ≥ +$2,500
- [ ] At least one `RIDE_LOCK_PXSTOP` row in 09:35–09:42 window (the canonical case)
- [ ] Trade count drops materially (today's 9 → expect 5–7 with same or better PnL per trade)
- [ ] No premium-bypass entries that fail (continued v7 0.2 health)
- [ ] PROFIT_SAFEGUARD ladder still fires correctly on big peaks (RIDE_LOCK does not interfere)

### Failure modes & next-step plan
- **RIDE_LOCK holds too long, gives back winnings:** lower `rideStreakMaxMult` to 1.3 OR raise `rideStreakMin` to 7 in v7 0.4.
- **Still over-trading after RIDE_LOCK:** add Brick-Reentry suppression (skip BRICK_REENTRY_FIRE if alive same-dir streak still in flight) in v7 0.4.
- **Misses true trend collapse:** raise `rideMinSlope` to -1.0 (more strict on slope health).

### Deferred (for v7 0.4+)
- Dormant-trail SL tightening (peak ≥ 5pt while trail still dormant → move SL closer but maintain `dormantSlGap` from price)
- 10:40–11:30 LONG miss fix: streak=51 / 188pt run blocked by post-loss cooldown + htfBlockL + extension filter
- REGIME_CHANGE hysteresis (single-bar adxSlope dip flipping TREND_X → UNKNOWN)
- Phase 5 cross-session capture-ratio persistence

---

## v7 0.2 (2026-04-30) — "Premium Re-Arm"

**Goal:** Stop the daily trade cap from locking us out of clean afternoon trends. Validated against v7 0.1 playback log: $975 result with 20-cap reached at 11:55, then 43 silent `BLOCK_AUTO,reason=maxTrades` rows during the 13:00–14:00 streak-43 TREND_UP run (price 27363 → 27547, ≈ +$3,440/contract raw move that we couldn't touch).

### Changes shipped

| # | Change | Code surface | Rationale |
|---|--------|--------------|-----------|
| A | **PREMIUM_BYPASS of `maxTradesPerDay`** | `TryAutoEntry` maxTrades branch + `CanProceedToEntry` helper. New `IsPremiumBypassEligible(direction)` helper. | When cap is hit, allow entry IFF `regime ∈ {TREND_UP,TREND_DN}` AND brick color matches dir AND `nrBrickStreakCount ≥ premiumStreak (12)` AND `bull/bearConfidence ≥ premiumMinConf (85)` AND `dailyRealizedPnL ≥ premiumMinPnl ($0)`. Capped at `premiumExtraTrades (5)` per session. |
| B | **Cap refund on winners** | `OnExecutionUpdate` win branch | When a closed trade nets `≥ cdScratchThreshold ($25)`, decrement `dailyTradeCount` (floor 0). Removes "death by tiny wins" exhaustion. |
| C | **New diag tags** | `PREMIUM_BYPASS` (per fire), `CAP_REFUND` (per qualifying winner). WOULD_TRADE audit kept active for forensic continuity. |

### New parameters

| Field | Default | Purpose |
|-------|---------|---------|
| `premiumBypassEnabled` | true | Master switch for (A) |
| `premiumStreak` | 12 | Min `nrBrickStreakCount` to qualify for bypass |
| `premiumMinConf` | 85 | Min matching confidence to qualify |
| `premiumMinPnl` | 0.0 | `dailyRealizedPnL` must be ≥ this ($) |
| `premiumExtraTrades` | 5 | Hard sub-cap on bypasses per session |
| `premiumExtraUsed` | 0 | Session counter (auto-reset) |
| `cdScratchThreshold` | 25.0 | Winners ≥ this ($) refund a slot |

### New diag tags

| Tag | When | Use |
|-----|------|-----|
| `PREMIUM_BYPASS` | maxTrades cap hit AND eligibility passes | Confirms (A) fired; logs `dir/regime/streak/conf/dailyPnl/extraUsed` |
| `CAP_REFUND` | Winner ≥ `cdScratchThreshold` decremented `dailyTradeCount` | Confirms (B) fired; logs `pnl/thr/tradesToday` |

### Build status: ✅ no compile errors

### Guardrails (all must hold for bypass)
- Regime ∈ {TREND_UP, TREND_DN} (no chop bypasses)
- Brick color matches intended direction (no fade entries)
- Streak ≥ 12 (no noise re-entries)
- Confidence ≥ 85 (high-quality only)
- `dailyPnL ≥ $0` (never double down on a losing day)
- `premiumExtraUsed < 5` (worst case 25 total trades, not unbounded)

### Validation checklist (run after playback)
- [ ] PnL ≥ $1,200 (lower bound: 1-2 premium trades captured)
- [ ] Stretch PnL ≥ $2,000 (upper bound: 5-bypass utilization across 13:xx, 14:xx, 15:xx trends)
- [ ] At least one `PREMIUM_BYPASS` row in 13:xx hour (TREND_UP streak-43 area)
- [ ] `CAP_REFUND` rows fire on each ≥ $25 winner — does it materially extend the budget?
- [ ] WOULD_TRADE rows still emitted for non-bypassed blocks (forensic continuity intact)
- [ ] No premium-bypass entries that immediately hit SL (failure mode: tighten `premiumStreak` to 15 or `premiumMinConf` to 95 in v7 0.3)

### Failure modes & next-step plan
- **All bypasses lose:** raise `premiumMinConf` to 95 OR `premiumStreak` to 15 in v7 0.3.
- **No bypasses fire:** lower `premiumStreak` to 10 OR `premiumMinConf` to 75.
- **Cap refund refunds too aggressively (over-trading):** raise `cdScratchThreshold` to $50.

---

## v7 0.1 (2026-04-30) — Forked from v6 4.2 stable

**Goal:** Apply the three v4.3 forensic recommendations + add WOULD_TRADE instrumentation.

### Changes shipped

| # | Change | Code surface | Rationale |
|---|--------|--------------|-----------|
| 1 | **Pre-RTH bypass** | `TryAutoEntry` outsideHours gate | Allow AUTO entries before RTH when `regime ∈ TREND_*` AND `nrBrickStreakCount ≥ preRthTrendMinStreak (12)` AND brick color matches direction. Targets the 8 outsideHours blocks observed on 4/30 v4.2 playback (~$150–300 missed). |
| 2 | **Streak-Hold loosen** | Brick-trail PxStop section | Disable streak-hold guard when `nrBrickStreakCount ≥ streakHoldMaxStreak (8)` — clean impulse, legacy retrace catches reversal. Saves ~$45/session. |
| 3 | **Chop peak-lock mult lift** | `peakLockChopMult` 0.80 → 0.85 | UNKNOWN regime trade T#5 SL'd at +$10 in v4.2 playback because mult was overtight. |
| 5 | **WOULD_TRADE audit** | `LogSilentBlock` | When a block fires AND brick streak ≥ 8 with matching color in TREND/UNKNOWN, emit `WOULD_TRADE,blockedBy=<reason> dir=<L/S> regime=<r> streak=<n>`. Pure observation — quantifies missed alpha per gate before relaxing live. |

### New parameters

| Field | Default | Purpose |
|-------|---------|---------|
| `preRthTrendBypassEnabled` | true | Master switch for #1 |
| `preRthTrendMinStreak` | 12 | Required streak for pre-RTH bypass |
| `streakHoldMaxStreak` | 8 | Streak ≥ this disables streak-hold guard |
| `enableWouldTradeAudit` | true | Master switch for #5 |
| `wouldTradeMinStreak` | 8 | Required streak for WOULD_TRADE emit |

### New diag tags

| Tag | When | Use |
|-----|------|-----|
| `PRE_RTH_BYPASS` | Pre-RTH entry gate bypassed by streak-trend rule | Confirms #1 fired |
| `WOULD_TRADE` | Any LogSilentBlock + strong-signal | Quantify missed alpha per `blockedBy` reason |

### Build status: ✅ no compile errors

### Awaiting playback validation

Expected log artifacts:
- 1+ `PRE_RTH_BYPASS` rows on a session with overnight TREND
- WOULD_TRADE rows grouped by `blockedBy` (especially `outsideHours`, `dailyLimitHit`)
- Higher PnL than v4.2 baseline ($445) on same RTH 2026-04-30 playback dataset

### Validation checklist (run after playback)
- [ ] PnL vs v4.2 (target: ≥ +$530, beating v4.0 baseline)
- [ ] Did `PRE_RTH_BYPASS` fire? Did the resulting trades win?
- [ ] How many WOULD_TRADE rows by reason? Highest-leverage gate to relax next?
- [ ] Did Streak-Hold loosen produce any same-pattern re-entries that v4.2 had blocked?
- [ ] Phase 7 chop mult: any UNKNOWN trade now caught more profit?

---

## PnL Ledger

| Version | Playback dataset | Trades | PnL | Notes |
|---------|------------------|--------|-----|-------|
| v6 4.0 | RTH 2026-04-30 (Playback-v16-2) | 6 | **+$530** | Peak-Lock T3 fired 10:17 +$735 single trade |
| v6 4.2 | RTH 2026-04-30 (Playback-v16-v42-1) | 3 (+8 blocked) | **+$445** | Phase 7 +$200 on T#3; 8 outsideHours blocks cost ~$150-300 |
| **v7 0.1** | RTH 2026-04-30 (Playback-1) | 20 (cap hit 11:55) | **+$975** | +84% vs v4.0, +119% vs v4.2. Locked out 11:55 onward — 43 silent blocks during 13:00-14:00 streak-43 TREND_UP run worth ~$3k/contract. Motivated v7 0.2. |
| **v7 0.2** | RTH 2026-04-30 (Playback-2) | 9 | **+$1,275** | +35% vs v7 0.1. CAP_REFUND fired 21x — never reached cap, PREMIUM_BYPASS unused. New leak found: over-trading inside single trends (09:35 SHORT cluster sliced 1 trend into 5 trades). Motivated v7 0.3. |
| **v7 0.3** | RTH 2026-04-30 (Playback-3) | 32 | **+$2,250** | **BEST EVER**: +76% vs v7 0.2, +325% vs v6 4.0. RIDE_LOCK fired 4x. CAP_REFUND fired 23x. Big winners: 14:01 LONG +$885, 10:17 SHORT +$735. Remaining leak: morning SHORT cluster sliced into 6 entries on a single downtrend (PxStop too eager mid-streak). Motivated v7 0.4. |
| **v7 0.4** | RTH 2026-04-30 (Playback-4) | 12 | **+$1,770** | -21% vs v7 0.3. Captured +$2,000 single trade but morning RIDE took 2 SLs. Code DELETED. |
| **v7 0.5** | RTH 2026-04-30 (Playback-5) | 13 (all SLs) | **−$685** | -130% vs v7 0.3. SL_CHASE put SL too close to trail → 13 of 13 exits were SL. Code DELETED. |
| **v7 1.0** | RTH 2026-04-30 (Playback-6) | _negative_ | **NEGATIVE** | -100%+ vs v7 0.3. RR Entry on first opposite brick = trap-magnet. Tight touch-exit replaced PxStop adaptive-retrace = chopped on noise. Code DELETED. See spec §21.3. |

---

## Forensic Methodology

For every v7 release, we run the same playback dataset and compare:
1. **Total PnL** vs prior version
2. **Per-trade quality** (avg $ / trade)
3. **Phase activations** — count occurrences of new tags
4. **Missed opportunities** — WOULD_TRADE windows by gate
5. **Capture ratio** — `profitPts / peakPts` per regime (Phase 5 sample stream)

Diag CSVs are staged in `Strategies/Diags/v<ver>_<dataset>.csv` for reproducibility.
