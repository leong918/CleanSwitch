# Phase 2A manual test procedure (historical/non-production)

> Production RETIRE SYSTEM now refuses to create PENDING unless a byte-exact machine-level
> copy of the WinRE image selected by `RecoveryGuid` contains the verified CleanSwitch
> `winpeshl.ini`, manifest,
> executable and configuration payload. Manual launch from stock WinRE is not accepted as
> a production continuation. The steps below are retained only as historical test evidence.

Validates the Boot 1 -> Recovery -> Boot 2 handoff. **Nothing is deleted.**

This document predates the production WinRE launcher provisioning and validation gate.

## This machine

Established from `bcdedit /enum all /v` and `Get-Partition` on 2026-08-28.

| Thing | Value |
| --- | --- |
| Boot 1 - Main | `{fc583d40-a29c-11f1-b0e3-e548a1d3146f}` — disk 0 partition 3, `C:` when running Boot 1 |
| Boot 2 - Clean | `{fc583d44-a29c-11f1-b0e3-e548a1d3146f}` — disk 0 partition 5, `D:` when running Boot 1 |
| WinRE (Boot 1's) | `{fc583d41-a29c-11f1-b0e3-e548a1d3146f}` — ramdisk on partition 4 |
| WinRE (Boot 2's) | `{fc583d45-a29c-11f1-b0e3-e548a1d3146f}` — ramdisk on partition 6, **configured** |
| Boot manager default | already `{fc583d44}` (Boot 2) |

Staged build: `D:\CleanSwitchRecovery\` (`CleanSwitch.exe` + `appsettings.json`).

## Drive letters are not stable — pin the volume by GPT GUID

Drive letters are assigned per Windows instance, so the same volume has a
different letter in each of the three environments:

| Volume | From Boot 1 | In WinRE | From Boot 2 |
| --- | --- | --- | --- |
| Boot 1 (partition 3) | `C:` | varies | likely `D:` |
| Boot 2 (partition 5) | `D:` | varies | likely `C:` |

So `D:\CleanSwitchData` means Boot 2's volume only while Boot 1 is running. From
Boot 2, `D:` most likely means **Boot 1** — the volume slated for deletion.

Win32 volume GUIDs (`\\?\Volume{...}`) do not fix this either: WinPE's Mount
Manager generates its own, so they are not stable across OS instances. The only
identifier that is the same everywhere is the **GPT unique partition GUID**,
because it lives in the partition table on the disk itself.

Note that `Get-Partition`'s `UniqueId` is *not* the GPT partition GUID. On this
machine it is a synthetic value, and disk 0 partition 1 and disk 1 partition 1
both report `{00000000-0000-0000-0000-100000000000}...`. Do not use it.

### Procedure

Run this on Boot 1, from an elevated PowerShell:

```powershell
cd C:\CleanSwitch\CleanSwitch\bin\Release\net8.0-windows
.\CleanSwitch.exe --list-volumes
```

It is read-only: it reads the partition table, prints a table, and exits 0. It
loads no configuration, writes no state file, and touches no boot entry.

Verified output on this machine (2026-08-28, from Boot 1):

| Disk | Part | GPT partition GUID | Size | FS | Letter | Role |
| --- | --- | --- | --- | --- | --- | --- |
| 0 | 1 | `{2d168deb-a7d0-4580-9a99-c8220f1559e5}` | 209.72 MB | FAT32 | none | EFI System |
| 0 | 3 | `{eab2ae6c-4d1b-4181-873c-3b8f06a1e465}` | 750.72 GB | NTFS | `C:` | **Boot 1 - Main** |
| 0 | 4 | `{ded053b0-a130-4aee-a47b-66e520fb853b}` | 895.48 MB | NTFS | none | Boot 1's WinRE |
| 0 | 5 | `{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}` | 247.54 GB | NTFS | `D:` | **Boot 2 - Clean** |
| 0 | 6 | `{2c26f280-e758-4f5e-9dc6-1083cc7aeba8}` | 817.89 MB | NTFS | none | Boot 2's WinRE |
| 1 | 1 | `{ea673daf-39a2-48b8-9fa5-4c13e0e3d23f}` | 31.04 GB | FAT32 | `E:` | Lexar USB (removable) |

Partition 2 (16 MB Microsoft Reserved) has no volume device, so it has no row.

Then set, once, in `appsettings.json`:

```json
"RecoveryDataVolumeGptId": "{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}",
"RecoveryDataFolderName": "CleanSwitchData"
```

That is Boot 2's volume. **Editing `RecoveryDataPath` per environment is no
longer required.** CleanSwitch resolves the GPT GUID to whatever letter that
volume happens to have in the environment it is running in, then uses
`<letter>\CleanSwitchData`.

Two behaviours worth knowing before you start:

- If `RecoveryDataVolumeGptId` names a volume that is not present, CleanSwitch
  **fails loudly and stops**. It deliberately does not fall back to
  `RecoveryDataPath`, because that fallback could put the state file on the
  volume being retired.
- On the read side (WinRE, Boot 2), if nothing is at the configured location
  CleanSwitch scans every fixed volume for
  `<volume>\CleanSwitchData\retirement-state.json`, accepts only files whose
  `operation` is `RETIRE_BOOT1` with a matching `schemaVersion`, and logs loudly
  that it found the file by scan. If **two or more** volumes hold a valid state
  file it stops and lists them rather than choosing one.

## Step 1 — Boot 1: start the handoff

The PC restarts at the end of this step. Save your work first.

1. Run `D:\CleanSwitchRecovery\CleanSwitch.exe` as Administrator.
2. Confirm **Current system** reads `Boot 1 - Main` and **Target** reads `Boot 2 - Clean`.
3. Click **RETIRE SYSTEM** and confirm once.
4. The app writes `PENDING` state, sets the recovery entry as the next boot, and restarts.

If anything is wrong it shows an error and does **not** restart. Expected state
afterwards: `D:\CleanSwitchData\retirement-state.json` (`D:` being what
`{4a16be66-...}` resolves to while Boot 1 is running) with `"status": "PENDING"`.
Its `stateVolumeIdentity` should record disk 0, partition 5 and that same GPT
GUID, which is what later phases compare against.

## Step 2 — WinRE: run the recovery half

The PC boots into the Windows Recovery Environment.

1. **Troubleshoot** -> **Advanced options** -> **Command Prompt**.
2. Find the volume letters:

   ```
   diskpart
   list volume
   exit
   ```

   Identify the ~247 GB NTFS volume (Boot 2) and the ~750 GB one (Boot 1).
3. Switch to the staged folder, substituting the letter WinRE assigned:

   ```
   E:
   cd \CleanSwitchRecovery
   ```

4. Confirm the volume identities WinRE sees, which also confirms the GPT GUID is
   unchanged here:

   ```
   CleanSwitch.exe --list-volumes
   ```

   Boot 2's volume must still show `{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}`,
   whatever letter WinRE gave it. No config edit is needed.
5. Run the recovery half:

   ```
   CleanSwitch.exe --recovery-run
   ```

Expected: loads the state, moves `PENDING` -> `RECOVERY_STARTED`, validates
Boot 2, **skips deletion**, sets Boot 2 as next boot, records `BCD_UPDATED` ->
`VERIFIED`, restarts. Deletion is logged as skipped with its justification.

If it reports no operation found, check the log: it will say whether the
configured volume was found, and whether the fixed-volume scan found a state
file somewhere else.

## Step 3 — Boot 2: confirm completion

1. The PC boots into `Boot 2 - Clean`.
2. Copy the staged folder across if needed and start `CleanSwitch.exe`. No config
   edit is needed: `RecoveryDataVolumeGptId` resolves to Boot 2's own volume,
   which is `C:` here.
3. It should detect the `VERIFIED` state, confirm it is running as the recorded
   Boot 2, and transition to `COMPLETE`.

This is the step the old letter-based config could not survive: from Boot 2,
`D:\CleanSwitchData` points at Boot 1, not at the state file.

## What proves the test passed

- The state file records the full transition chain with timestamps and reasons.
- `destructiveDeletionPerformed` is `false`.
- Both Windows installs still boot; no partition changed.
- Logs exist under `<retirement data folder>\logs` and
  `%ProgramData%\CleanSwitch\logs`.
- Exactly one `retirement-state.json` exists on the machine, on Boot 2's volume,
  and it was reached by configuration rather than by the fallback scan in all
  three environments. A "FOUND BY VOLUME SCAN" warning in the log means the
  configured GPT GUID is wrong.

## Recovering if it stalls

Nothing here is destructive, so recovery is just cleanup:

- `bcdedit /bootsequence` only affects the next boot; a normal restart clears it.
- Boot manager default is already Boot 2, and its 30 second menu lets you pick
  either system.
- To abandon an operation, delete `retirement-state.json` from whichever volume
  holds it.
