# Future: delete Boot 1 after switch

This is **not** implemented in the current CleanSwitch app.

## Planned later flow

```text
Boot 1 (Main)
  ↓
User clicks Switch
  ↓
Set Boot 2 as next one-time boot
  ↓
Restart
  ↓
PC is running Boot 2 (Clean)
  ↓
THEN delete / remove Boot 1
```

Boot 1 must only be removed **after** Boot 2 is confirmed running. If Boot 1
is deleted while still on Boot 1, the PC can be left unbootable.

## This version still only

1. Detect the current Windows
2. Set the other Windows for the next boot (`bcdedit /bootsequence`)
3. Restart

No format, partition delete, WinPE, or BitLocker wipe.
