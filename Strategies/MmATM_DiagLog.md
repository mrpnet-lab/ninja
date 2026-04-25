# Mm_ATM_v3 — Diagnostic Log Guide

## Where the File Is Created

```
Documents\NinjaTrader 8\MmATM_DiagLog_YYYYMMDD.csv
```

---

## Parameters (Group 12 - Diagnostics)

| Parameter | Default | Description |
|---|---|---|
| `Enable Diagnostic Log` | OFF | Turn ON in playback only. Zero overhead when OFF. |
| `Diag Log One Day Only` | ON | Stops logging when the date rolls. Prevents large files in extended playback runs. |

---

## How to Run a Playback Session

1. Pick a **replay date with ≥2 bad trades** (e.g., a day with wrong long signals in a downtrend)
2. Set strategy params: `AutoMode=ON`, `EnableDiagLog=ON`, `DiagOneDayOnly=ON`
3. Set `MinSignalConfidence=45` for data-collection runs (see threshold guide below)
4. **Raise daily limits** to avoid cutting the trade sample short (see note below)
5. Run full playback at max speed
6. Upload `Documents\NinjaTrader 8\MmATM_DiagLog_YYYYMMDD.csv` to the chat

### Daily Loss / Profit Limits During Playback

`SIGNAL` rows are logged **regardless** of whether the daily limit has been hit — the strategy keeps calculating bull/bear scores all day. So the limit does not make the signal data incomplete.

However `ENTRY_LONG`/`ENTRY_SHORT` rows stop firing after the limit is reached, which means fewer trades to analyze in the afternoon session. For a **full-day trade analysis**, raise the limits temporarily:

| Param | Normal | Playback diagnostic |
|---|---|---|
| `Max Daily Loss $` | 2000 | 5000 |
| `Max Daily Profit $` | 4000 | 10000 |

These only apply in playback — no real money is at risk. Reset them to normal values before going live.

---

## Recommended MinSignalConfidence

| Goal | Value | Reason |
|---|---|---|
| Normal trading | 55 (default) | Conservative, avoids weak signals |
| **Data collection run** | **45** | Gets more trades to analyze; shows the threshold curve |
| Too many bad trades | 62–65 | Tighter gate |

Use **45** for the first analysis run. The goal is to generate enough `ENTRY_*` rows to find the right cut-off from real data.

---

## If Zero Trades Fire

Every flat bar still logs a `SIGNAL` row (~390 rows/day on a 1-min chart). From those alone it is possible to see:
- The distribution of `FiltBull`/`FiltBear` scores (e.g., max was 48% all day → threshold too high)
- Which filters are killing signals (large gap between `RawBull` and `FiltBull`)
- Whether EMA alignment blocked signals all day

If 0 `ENTRY_*` rows appear and trades are needed for analysis, lower `MinSignalConfidence` to **40** and re-run the same date.

---

## CSV Columns (33 total)

| Column | What it means |
|---|---|
| `DateTime` | Timestamp to the second — `2026-04-03 09:45:00` |
| `Bar` | NinjaTrader bar index |
| `Open/High/Low/Close/Volume` | OHLCV for that 1-min bar |
| `VWAP` | Session VWAP value at bar close |
| `EmaFast / EmaSlow` | EMA(9) and EMA(21) values |
| `EmaFSlope` | EmaFast[0] − EmaFast[1] (1-bar slope) |
| `RSI / RSISlope` | RSI(14) value and 2-bar direction |
| `ATR` | ATR(14) in points |
| `POC / VAH / VAL` | Volume Profile levels |
| `RawBull / RawBear` | Score **before** ApplySmartFilters |
| `FiltBull / FiltBear` | Score **after** all 16 filters — what the entry decision uses |
| `EmaAlign` | `BULL` (fast > slow) or `BEAR` (fast < slow) |
| `RSIDir` | `UP` / `DOWN` / `FLAT` (2-bar slope threshold ±0.5) |
| `Slope5ATR` | 5-bar price slope in ATR units (Filter 13 uses 0.4 threshold) |
| `ConsecLoss` | Consecutive loss count at that bar |
| `LastLossDir` | `1`=last loss was long, `-1`=short, `0`=none |
| `AutoStrat` | Active strategy: 0=MomVwap, 1=KeyLvl, 2=LiqSweep, 3=ORB, 4=Auto |
| `Position` | `FLAT / LONG / SHORT` |
| `OpenDir` | `1`=long, `-1`=short, `0`=flat |
| `TrapScore` | Post-entry trap score (0–100) |
| `UnrealPts` | Unrealized P&L in NQ points while in position |
| `Action` | See action types below |
| `Detail` | Extra info per action type |

---

## Action Types

| Action | When logged | Detail field |
|---|---|---|
| `SIGNAL` | Every flat bar after CalcXxx + ApplySmartFilters | *(empty)* |
| `ENTRY_LONG` | Auto long entry fired | `bull=XX.X raw=YY.Y` |
| `ENTRY_SHORT` | Auto short entry fired | `bear=XX.X raw=YY.Y` |
| `TRADE_BAR` | Every bar while in position | `unreal=X.XX trap=YY.Y` |
| `EXIT_WIN` | Trade closed with profit | `pnl=XXX.XX` |
| `EXIT_LOSS` | Trade closed with loss | `pnl=-XXX.XX consec=N` |

---

## Prompt to Use When Sharing a New CSV

Copy-paste this when uploading a new log file:

> I ran a playback DiagLog for [DATE]. The attached CSV is from `MmATM_DiagLog_YYYYMMDD.csv`. Please analyze it to: (1) explain why each `ENTRY_LONG` or `ENTRY_SHORT` fired — was the EMA alignment, RSI, and slope supportive or not; (2) identify which filters are suppressing signals most aggressively (compare `RawBull` vs `FiltBull`); (3) recommend a new `minSignalConfidence` threshold based on the actual score distribution; (4) flag any bars where signals fired in a clearly wrong market structure. Suggest specific parameter changes.

---

## Key Analysis Points

When the CSV is shared, the following will be reviewed:

1. **Filter impact**: `RawBull − FiltBull` gap shows how much each filter layer is suppressing
2. **Wrong entries**: `ENTRY_LONG` rows where `EmaAlign=BEAR` (signal fired against market structure)
3. **Threshold calibration**: Distribution of `FiltBull`/`FiltBear` across all `SIGNAL` rows → optimal `minSignalConfidence`
4. **Trap effectiveness**: `TRADE_BAR` rows with rising `TrapScore` → did Smart SL tighten in time?
5. **Filter 15 (EMA cross) and Filter 16 (consecutive loss)**: Are they actually reducing `FiltBull` when `EmaAlign=BEAR`?
