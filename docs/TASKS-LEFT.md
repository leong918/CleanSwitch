# Tasks left

Last updated 2026-09-01. Phase 2B dry-run passed on hardware. Live delete is implemented behind guards and is still off.

Live deletion of Boot 1 will not run in this build: `DestructiveOperationsImplemented` and `EnableDestructiveRetirement` are both false. Use `--recovery-review` to print the live path without changing a disk.

| Status | Meaning |
| --- | --- |
| Open | Not done |
| Blocked | Waiting on a prior item |
| Decide | Needs a choice before coding |

## Now — before any retirement test

1. **Done.** `RecoveryDataVolumeGptId` is `{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}`.
2. **Done.** Self-contained exe staged at `D:\CleanSwitchRecovery`.
3. **Done.** Phase 2A manual test passed: both boots remain, BCD intact, `COMPLETE`, `destructiveDeletionPerformed: false`.

## Phase 2A leftovers

4. **Prepared-copy implementation complete; live deployment still required.** Stock WinRE
   does not auto-start CleanSwitch. `--provision-winre-launcher` now byte-copies the exact
   WIM selected by `RecoveryGuid` into a validated machine-level workspace, services only
   that copy, and remounts it read-only to prove the approved EXE/config, manifest,
   RecoveryRunner arguments, and stock `%SYSTEMDRIVE%\sources\recovery\RecEnv.exe` fallback.
   Installing the prepared WIM into live WinRE is a separate, compile/runtime-gated,
   hash-chained journal transaction. The implementation snapshots REAgentC/full BCD, verifies
   an exact original-WIM backup, uses disable/setreimage/enable, rolls incomplete transactions
   back deterministically, and requires a dedicated non-retirement `--recovery-smoke` receipt.
   Real-machine deployment remains unauthorized and the disposable-VM REAgentC cycle is still
   required before hardware use.
   Phase 2A likewise validates only a fresh WIM copy before identity capture, PENDING, BCD
   mutation, or reboot.
5. **Open.** Confirm `--list-volumes` in WinRE still reports Boot 2 as `{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}` (letter may differ). That is the proof the GPT locator survived the reboot.
6. **Open.** After a successful run, keep exactly one `retirement-state.json` on Boot 2's volume, with `status: COMPLETE` and `destructiveDeletionPerformed: false`.

## Phase 2B — retire Boot 1

7. **Done (identify only).** Boot 1 and Boot 2 `PartitionIdentity` (disk, partition, GPT GUID) are recorded at PENDING. WinRE looks them up by GPT GUID, not drive letter.
8. **Done (gate only).** `DiskValidator.ValidateRetirementTarget` is a hard gate: disk+partition and GPT id must agree; target must not be the running volume, ESP, MSR, Recovery, or Boot 2. Passing prints `TARGET_VALIDATED`. Deletion is still not implemented.
9. **Done (disabled).** Live `diskpart` delete of pinned Boot 1 (disk 0 / partition 3 / `{eab2ae6c-…}`) is implemented in `RetirementExecutor`. All guards stay false. `--execute-deletion` is required at runtime in addition to the two flags. No wipe has been run on this PC.
10. **Done (code, not hardware).** Resume: if `destructiveDeletionPerformed` or status is `BOOT1_RETIRED+`, diskpart is skipped. If Boot 1 GPT is already gone and Boot 2 is unique, `AcknowledgeAlreadyDeleted` records `BOOT1_RETIRED` without starting diskpart. Still unproven after a real wipe.
11. **Blocked** on enabling live 2B. Set `"phase": "2B"` only on newly created state files after the flags are flipped.

## Phase 2C — BCD cleanup and space

12. **Blocked** on 2B. Remove the Boot 1 loader with `bcdedit /delete {fc583d40-a29c-11f1-b0e3-e548a1d3146f}` only after the partition is gone. Do not delete the EFI System Partition.
13. **Decide.** Boot manager `default` is already Boot 2 (`{fc583d44-…}`). 2C only needs to confirm that, not set it from scratch. Timeout is still 30 seconds.
14. **Decide.** Disk 0 layout is ESP, MSR, Boot 1 (`C:`, 750 GB), Recovery, Boot 2 (`D:`, 247 GB), Recovery. Windows can only extend a volume into free space that immediately follows it. Deleting Boot 1 leaves ~751 GB that Boot 2 cannot absorb without a partition move. Options: leave a new data volume, or add a move/extend step (higher risk). Not chosen yet.
15. **Blocked** on 12. Verify from Boot 2 that the Boot 1 BCD entry is gone and Boot 2 still boots with no BIOS menu interaction required.

## Release / repo

16. **Open.** No GitHub Release has been published. The workflow exists. Cutting one is: tag `v0.2.0` (or similar) and `git push origin v0.2.0`. See `docs/RELEASING.md`. Do not tag until 2A is tested if you want the zip to include a filled-in `RecoveryDataVolumeGptId`.
17. **Open.** Private key `ssh/id_ed25519` is still on disk inside the repo folder. It is gitignored, but any zip/copy of the folder includes it. Safer: keep it only in `%USERPROFILE%\.ssh`.

## Must not do

- Format, delete, or wipe any partition before items 3, 7, and 8 are done.
- Delete the EFI System Partition, Boot 2, or either Recovery partition.
- Identify Boot 1 by `C:` / `D:` alone.
- Continue if identifiers are ambiguous.
- Set `EnableDestructiveRetirement` to `true` in this phase.

## Machine facts (this PC)

| Role | BCD id | Disk 0 partition | GPT partition GUID |
| --- | --- | --- | --- |
| Boot 1 - Main | `{fc583d40-a29c-11f1-b0e3-e548a1d3146f}` | 3 | `{eab2ae6c-4d1b-4181-873c-3b8f06a1e465}` |
| Boot 2 - Clean | `{fc583d44-a29c-11f1-b0e3-e548a1d3146f}` | 5 | `{4a16be66-dfc5-4b2a-bf95-a7d7d4d2e6fb}` |
| WinRE (Boot 1) | `{fc583d41-a29c-11f1-b0e3-e548a1d3146f}` | 4 | `{ded053b0-a130-4aee-a47b-66e520fb853b}` |
| WinRE (Boot 2, configured) | `{fc583d45-a29c-11f1-b0e3-e548a1d3146f}` | 6 | `{2c26f280-e758-4f5e-9dc6-1083cc7aeba8}` |
| EFI System | `{9dea862c-5cdd-4e70-acc1-f32b344d4795}` (bootmgr device) | 1 | `{2d168deb-a7d0-4580-9a99-c8220f1559e5}` |

Related docs: `docs/PHASE-2A-MANUAL-TEST.md`, `FUTURE.md`, `docs/RELEASING.md`.
