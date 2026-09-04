# Disposable Windows VM end-to-end retirement harness contract

`scripts/winre-vm-harness.ps1` is the fail-closed host orchestrator consumed by
`CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS`. It does not install a hypervisor,
select a physical disk, or contain a provider-specific fallback. An operator must
explicitly provide a disposable VM configuration and provider adapter.

The mandatory gate is three real CleanSwitch retirement cycles. CleanSwitch does
not restore Boot1. The host hypervisor restores the pristine VM checkpoint only
after evidence is collected and the VM is stopped; that is test reset, not a
production rollback.

## Environment and configuration

```powershell
$env:CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS = '<repo>\scripts\winre-vm-harness.ps1'
$env:CLEAN_SWITCH_WINRE_VM_CONFIG = '<operator-controlled>\vm-harness.json'
$env:CLEAN_SWITCH_RUN_WINRE_DEPLOYMENT_VM_INTEGRATION = '1'
```

```json
{
  "schemaVersion": 2,
  "disposable": true,
  "vmId": "provider-stable-vm-id",
  "vmGuid": "11111111-1111-1111-1111-111111111111",
  "providerScript": "D:\\vm-harness\\provider.ps1",
  "approvedVmStorageRoots": ["D:\\DisposableVMs\\CleanSwitch"],
  "baselineCheckpoint": "cleanswitch-pristine",
  "pristineCheckpointGuid": "22222222-2222-2222-2222-222222222222",
  "artifactRoot": "D:\\vm-harness\\artifacts",
  "providerTimeoutSeconds": 120,
  "sourceCommit": "<exact-40-hex-committed-revision-under-test>"
}
```

Configuration and provider inspection must independently identify the exact VM
and mark it disposable. `vmGuid` and `pristineCheckpointGuid` are immutable
provider identities, not display names. Every VM disk must be a file-backed image
beneath exactly one approved, non-reparse storage root. Device paths,
passthrough disks, host disk numbers, physical disk IDs, and iSCSI identities are
rejected. Every provider call has a bounded timeout of 5–1800 seconds.

## Provider interface

The provider is invoked as:

```text
provider.ps1 -Command <command> -VmId <exact-id> -ArgumentsBase64 <base64-utf8-json>
```

It emits exactly one JSON object and exits zero:

```json
{"success":true,"vmId":"provider-stable-vm-id","command":"start","data":{}}
```

Required commands:

| Command | Required behavior |
|---|---|
| `inspect` | Return the immutable VM GUID, disposable flag, Windows build, firmware, GPT style, VM state, and every disk. |
| `checkpoint` | Create/delete an exact named checkpoint and fail on ambiguity. Used only by readiness proof. |
| `restore` | Restore one exact checkpoint and return its immutable checkpoint GUID. |
| `start` | Start only the configured VM and verify its identity. |
| `stop` | Gracefully stop only the configured VM. |
| `hard-poweroff` | Power off only the configured VM without guest shutdown. |
| `guest-command` | Execute one named allowlisted guest action and return structured evidence. |
| `wait-for-guest` | Wait for the same VM to return as Boot2 Windows after the product-driven WinRE retirement sequence; it must not force or redirect boot. |
| `collect-artifacts` | Copy evidence to the requested host directory and return the manifest path and SHA256. |

`inspect.data` includes `vmGuid`, `disposable`, `windowsBuild`, `firmware`,
`partitionStyle`, and `disks`. Each disk has `attachmentType: "File"` and
`hostPath`; forbidden physical-host identity properties must be absent or empty.

## Readiness and checkpoint proof

Readiness writes a random guest probe, creates a unique checkpoint, changes the
probe, hard-powers off the VM, restores the checkpoint, and requires the original
probe value. It removes the probe and checkpoint afterward. This proves that the
provider can reset a file-backed disposable guest; it does not exercise or model
CleanSwitch rollback.

## Required destructive cycle

Each `-Cycle` invocation performs this sequence and fails at the first missing,
false, malformed, ambiguous, or mismatched item:

1. Prove readiness and file-backed VM isolation.
2. Hard-power off and restore the configured pristine checkpoint; require its GUID.
3. Start the VM and run `pre-retirement`, proving the exact source/build, pristine Boot1/Boot2/RecoveryData/ESP/Boot2-WinRE GPT layout, terminal-or-absent retirement state, no unresolved deployment journal, original WIM hash, RecoveryGuid, and pre-state GPT/BCD fingerprints.
4. Run `prepare-seal`, `deploy`, and `review`; require the sealed bundle, prepared/deployed WIM hash equality, the exact `AwaitingSmoke` transaction boundary, exact launcher-contract hash, and unchanged RecoveryGuid. Then invoke `--commit-winre-deployment --deployment-transaction <id>` through the `commit-winre-deployment` guest action and require the same transaction to become terminal `COMMITTED`, with its journal SHA256 recorded and no unresolved journal remaining.
5. Run `start-retirement`. The guest adapter must initiate the real product-supported `RETIRE SYSTEM` path—never call internal services or manufacture state—and return proof that Phase2A committed its handoff, made Boot2 the persistent default, and armed Boot2 recovery one-shot. The terminal deployment is a prerequisite only; it grants no retirement authority.
6. The product reboots itself. The provider only waits. `winpeshl.ini` must automatically run `CleanSwitch.exe --recovery-launch`; the dispatcher must select retirement, and `RecoveryRunner` must perform fresh Phase2B validation, delete only Boot1, verify its GPT absence and all required survivors, remove the Boot1 BCD loader, persist `VERIFIED`, and reboot to Boot2.
7. After Boot2 starts, run `verify-retirement`. Require Boot1 partition and loader absent; Boot2, RecoveryData, ESP, recovery partitions, and Boot2 WinRE present; Boot2 loader still persistent default; `COMPLETE` startup evidence; exactly one destructive deletion; at most one BCD deletion; valid post-retirement REAgentC; and no unresolved deployment journal.
8. Collect the full artifact set and its SHA256 manifest, stop the VM, then hard-power off and restore the same pristine checkpoint GUID for the next independent cycle.

The allowlisted guest actions are `checkpoint-probe-write`,
`checkpoint-probe-read`, `checkpoint-probe-delete`, `pre-retirement`,
`prepare-seal`, `deploy`, `review`, `commit-winre-deployment`, `start-retirement`, and
`verify-retirement`. There is no CleanSwitch rollback action in this gate and no
host command that forces the guest into WinRE.

Per-cycle evidence includes VM/checkpoint GUIDs and disk paths; source commit and
executable/config hashes; original, prepared, bundle, and deployed WIM hashes;
launcher hash/review, deployment transaction/journal hashes, and RecoveryGuid; pre/post GPT and BCD fingerprints; Phase2A
state; RecoveryRunner log; exact resolved Boot1 identity; destructive and BCD
delete counts; final state and Boot2/default proof; post-retirement REAgentC;
journal inventory; and a SHA256 artifact manifest.

## Entry points

```powershell
& $env:CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS -Readiness -ResultPath D:\vm-harness\readiness.json
& $env:CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS -Cycle 1 -ResultPath D:\vm-harness\cycle-1.json
```

The mandatory xUnit test invokes cycles 1, 2, and 3 independently. A pass requires
all three. No command falls back to the physical host, and an unavailable provider
or guest action remains a mandatory skip/failure rather than becoming PASS.

## Known implementation prerequisites

The product now exposes an explicit, transaction-bound terminalization command.
A real provider/guest adapter still cannot pass until it has reviewed ways to:

- invoke `--commit-winre-deployment --deployment-transaction <id>` after independent launcher review and return terminal journal evidence; and
- activate the existing GUI-only
  `RETIRE SYSTEM` action and return Phase2A evidence without invoking internal
  services, writing retirement state, or forcing a WinRE boot.

Until those prerequisites are implemented and reviewed, the VM gate is expected
to remain unavailable/NO-GO.
