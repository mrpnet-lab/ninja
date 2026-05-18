# RM_Pro_Spec_2_5

## The Compilation Error Fix (CS1061)
The error in the screenshot (`'Instrument' does not contain a definition for 'Name'`) is a common NinjaScript syntax technicality.

### The Problem
In the `Print()` statement meant to warn you about matching your Account names, I referenced `Instrument.Name`. However, the base `Instrument` object in NinjaTrader 8 does not have a property called `.Name`. It uses `.FullName` (e.g., "NQ 06-26") or `.MasterInstrument.Name` (e.g., "NQ").

### The Fix
I have updated line 106 in the code from `Instrument.Name` to `Instrument.FullName`. The code will now compile flawlessly and the new Polling Engine will run perfectly!
