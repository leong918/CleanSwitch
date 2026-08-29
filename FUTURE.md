# Retiring Boot 1: phase status

| Phase | Scope | Status |
|---|---|---|
| 1 | Detect both Windows entries, one-time switch, restart | Implemented |
| 2A | Boot 1 → Recovery → Boot 2 handoff, state machine, logging, validators. **No deletion.** | Implemented in code; **not yet tested on this PC** |
| 2B | Actually retire Boot 1: identify the partition from stable identifiers and remove it | **Not implemented** |
| 2C | Remove the Boot 1 BCD entry, reclaim space, verify from Boot 2 | **Not implemented** |

The remaining work list, in order, is `docs/TASKS-LEFT.md`.

Deletion of anything — partition, volume, or BCD object — is **not implemented** in any
form. `Recovery/RetirementExecutor.cs` contains no deletion code; every entry point throws
`RetirementNotImplementedException`.

## Target flow

```text
Boot 1 (Main)
  ↓  user clicks RETIRE SYSTEM, confirms once
Write retirement state (PENDING) to a volume that is NOT Boot 1
  ↓  bcdedit /bootsequence {RECOVERY_GUID}
Restart
  ↓
Recovery environment: CleanSwitch.exe --recovery-run
  ↓  RECOVERY_STARTED
Identify Boot 1 by stable identifiers        (2B validates; 2A only reports)
  ↓  TARGET_VALIDATED
Validate Boot 2 is a real, distinct Windows loader
  ↓  BOOT2_VALIDATED
Retire Boot 1                                (2B; SKIPPED ENTIRELY in 2A)
  ↓  BOOT1_RETIRED
bcdedit /bootsequence {BOOT2_GUID}
  ↓  BCD_UPDATED → VERIFIED
Restart
  ↓
PC is running Boot 2 (Clean) → COMPLETE
```

Boot 1 must only be removed **after** Boot 2 is confirmed bootable, and never from inside
Boot 1 itself. Deleting Boot 1 while running on Boot 1 can leave the PC unbootable, which
is why the destructive step is placed in the recovery environment and why Phase 2A exists
to prove the handoff before any deletion code is written.

## Phase 2A — implemented

- `RETIRE SYSTEM` button with a single confirmation dialog.
- Retirement state persisted as JSON with `schemaVersion`, timestamps per transition and
  `lastError`; written atomically (temp file + flush + replace, `.bak` retained).
- State location is configurable (`CleanSwitch:RecoveryDataPath`) and is rejected when it
  resolves onto the running Windows volume. There is no silent fallback to a Boot-1-only
  path.
- Explicit, validated state transitions. Illegal transitions throw. The two Phase 2A
  shortcuts around the deletion states are declared in the transition table and logged
  whenever they are taken.
- `bcdedit /bootsequence {RECOVERY_GUID}` handoff, with the recovery entry validated
  first. Any failure means no reboot.
- `--recovery-run` and `--recovery-dry-run` entry points for the recovery side.
- Persistent file logging of every transition, every process invocation with exit code and
  output, and every validation decision.
- `DiskValidator` / `BootEntryValidator` identification logic, using disk + partition
  number, volume GUID, GPT partition id and BCD device information. Drive letters are
  never used for identity, because WinRE reassigns them.

## Phase 2B — not implemented

Needed before any deletion is attempted:

1. Populate `PartitionIdentity` from the partition table (disk number, partition number,
   GPT unique partition id) and record it on Boot 1 at `PENDING` time, so recovery can
   compare an expected identity against an observed one.
2. Require `DiskValidator.ValidateRetirementTarget` to pass: at least two independent
   stable identifiers must agree, and the target must not be the volume the running
   process boots from.
3. Implement the removal itself in `RetirementExecutor`, behind
   `DestructiveOperationsImplemented`, `explicitOptIn` and
   `CleanSwitch:EnableDestructiveRetirement`.
4. Prove recovery-side resume: a power loss between `BOOT1_RETIRED` and `VERIFIED` must
   leave a state file that a rerun can interpret without repeating the deletion
   (`destructiveDeletionPerformed`).
5. Package the recovery-side runner so it can actually execute in WinRE. WinRE has no
   .NET 8 desktop runtime, so a self-contained `win-x64` publish is required, staged on a
   volume WinRE can see. Until that exists, `--recovery-run` is only exercisable from a
   full Windows session.
6. Only then set `phase` to `2B` in newly created state files.

## Phase 2C — not implemented

1. Remove the Boot 1 loader object (`bcdedit /delete {BOOT1_GUID}`) once the partition is
   gone.
2. Optionally extend the surviving volume into the reclaimed space.
3. Verify from Boot 2: the Boot 1 entry is absent, Boot 2 is the default, and close the
   operation as `COMPLETE`.

## Still out of scope

WinPE image building, BitLocker erase, HTTP API, LAN control, Wake-on-LAN, MAC discovery.
