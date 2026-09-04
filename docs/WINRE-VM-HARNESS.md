# Disposable Windows VM WinRE harness contract

`scripts/winre-vm-harness.ps1` is the fail-closed host orchestrator consumed by
`CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS`. It does not install a hypervisor and
does not contain a provider-specific fallback. An operator must explicitly provide
a VM configuration and a provider adapter.

## Environment and configuration

```powershell
$env:CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS = '<repo>\scripts\winre-vm-harness.ps1'
$env:CLEAN_SWITCH_WINRE_VM_CONFIG = '<operator-controlled>\vm-harness.json'
$env:CLEAN_SWITCH_RUN_WINRE_DEPLOYMENT_VM_INTEGRATION = '1'
```

```json
{
  "schemaVersion": 1,
  "disposable": true,
  "vmId": "provider-stable-vm-id",
  "providerScript": "D:\\vm-harness\\provider.ps1",
  "approvedVmStorageRoots": ["D:\\DisposableVMs\\CleanSwitch"],
  "baselineCheckpoint": "cleanswitch-pristine",
  "artifactRoot": "D:\\vm-harness\\artifacts",
  "providerTimeoutSeconds": 120,
  "sourceCommit": "<exact-committed-revision-under-test>"
}
```

The configuration stays outside the repository because it identifies a specific
hypervisor and VM. Configuration and provider inspection must both mark the VM
disposable. Every disk must be a file-backed image beneath exactly one approved,
non-reparse storage root. Device paths, passthrough disks, host disk numbers,
physical disk IDs and iSCSI identities are rejected.
Every provider call has a bounded timeout (5–1800 seconds, default 120); a timeout
terminates only that provider process and makes readiness/cycle fail closed.

## Provider interface

The provider is invoked as:

```text
provider.ps1 -Command <command> -VmId <exact-id> -ArgumentsBase64 <base64-utf8-json>
```

It emits exactly one JSON object and exits zero:

```json
{"success":true,"vmId":"provider-stable-vm-id","command":"start","data":{}}
```

Arguments use Base64-encoded UTF-8 JSON so GUIDs, paths and quotes cross the
Windows PowerShell process boundary without command-line reinterpretation. The
provider must decode the value and reject malformed JSON.

Required commands:

| Command | Required behavior |
|---|---|
| `inspect` | Return disposable flag, Windows build, firmware, GPT style, VM state and every disk. |
| `checkpoint` | Create/delete an exact named checkpoint; fail on ambiguity. |
| `restore` | Restore one exact checkpoint and verify identity. |
| `start` | Start only the configured VM and verify its ID. |
| `stop` | Gracefully stop only the configured VM. |
| `hard-poweroff` | Power off only the configured VM without guest shutdown. |
| `guest-command` | Execute one named allowlisted guest action and return structured evidence. |
| `reboot-to-winre` | Reboot the configured guest into its registered WinRE. |
| `collect-artifacts` | Copy evidence to the requested host directory and return its manifest. |

`inspect.data` includes `disposable`, `windowsBuild`, `firmware`,
`partitionStyle`, and a `disks` array. Each disk has `attachmentType: "File"`
and `hostPath`. The forbidden properties `hostDiskNumber`, `physicalDiskId`,
`passThroughDiskId`, and `iscsiTarget` must be absent or empty.

## Checkpoint proof

Readiness writes a random guest probe, creates a unique checkpoint, changes the
probe, hard-powers off the VM, restores the checkpoint, and requires the original
probe value. It removes the probe and checkpoint afterward. Any failure returns
`Ready=false`.

Allowlisted guest actions are `checkpoint-probe-write`, `checkpoint-probe-read`,
`checkpoint-probe-delete`, `pre-cycle`, `prepare-seal`, `deploy`, `review`,
`verify-winre-smoke`, `rollback`, and `post-cycle-verify`. Results must identify
the exact requested action and supply the WIM hashes, RecoveryGuid and invariant
evidence required by the orchestrator.

## Entry points

```powershell
& $env:CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS -Readiness -ResultPath D:\vm-harness\readiness.json
& $env:CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS -Command start
```

The existing xUnit test calls:

```text
winre-vm-harness.ps1 -Cycle <1..3> -ResultPath <unique-json-path>
```

No command runs without exact VM identity, dual disposable attestation,
file-backed disk isolation and a proven checkpoint restore. There is no fallback
to the physical host. Direct command forwarding also reruns the complete readiness
gate before invoking the requested provider command.
