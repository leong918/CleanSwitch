# CleanSwitch

Local Windows 11 proof of concept for switching between two already-installed
Windows boots on one PC.

Open `CleanSwitch.exe` on the running Windows, confirm the detected current
and target boots, then click the switch button. The app sets the other Windows
as the **next boot only** and restarts. It does not change the permanent
default boot entry.

This is a single-PC desktop app. There is no HTTP API, no Mac controller, and
no network control.

There is a second action, **RETIRE SYSTEM**, which prepares the Boot 1 → Recovery →
Boot 2 handoff. As of Phase 2A it still deletes nothing. See
[Retiring Boot 1](#retiring-boot-1-phase-2a) and `FUTURE.md`.

## Safety

CleanSwitch does **not**:

- Format or delete disks or partitions
- Modify the other Windows install
- Change the default BCD boot entry
- Use WinPE, BitLocker wipe, Wake-on-LAN, or MAC discovery

The only boot commands it runs are:

```text
bcdedit /bootsequence {TARGET_GUID}
bcdedit /bootsequence {RECOVERY_GUID}
bcdedit /enum ... /v            (read-only)
```

If any of those fail, Windows is not restarted.

To identify volumes it also issues two read-only device IOCTLs,
`IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` and `IOCTL_DISK_GET_DRIVE_LAYOUT_EX`, on handles
opened with zero desired access. These read the partition table. CleanSwitch never mounts,
unmounts, writes to or formats a volume, and it does not use `diskpart`, `mountvol`,
`Format-Volume`, `Remove-Partition`, `Clear-Disk` or `Set-Partition`.

The destructive code path does not exist. `Recovery/RetirementExecutor.cs` is a stub whose
every entry point throws, guarded by a hard-coded `DestructiveOperationsImplemented = false`
flag, a required `explicitOptIn` argument, and the
`CleanSwitch:EnableDestructiveRetirement` setting. All three must line up before any future
deletion code could run, and the first one is false in this build.

## Requirements

- Windows 11 Pro
- Two Windows Boot Manager entries already present (Boot 1 / Boot 2)
- .NET 8 SDK
- Administrator permission (BCDEdit requires elevation)

Install .NET 8: https://dotnet.microsoft.com/download/dotnet/8.0

```powershell
dotnet --info
```

## Project layout

```text
CleanSwitch/
  Program.cs                       GUI entry point + --recovery-run / --recovery-dry-run / --list-volumes
  MainForm.cs                      Switch button and RETIRE SYSTEM button
  MainForm.Designer.cs
  AppConfiguration.cs
  appsettings.json
  app.manifest
  Models/
    BootLayout.cs                  Current / target boot entries
    BcdEntry.cs                    One parsed bcdedit entry
    CleanSwitchOptions.cs          appsettings.json shape
    PartitionIdentity.cs           Stable disk/volume identifiers (no drive letters)
    RetirementState.cs             State enum, transitions, persisted record
  Services/
    IBootManager.cs
    WindowsBootManager.cs          bcdedit / shutdown execution and parsing
    IRetirementCoordinator.cs
    RetirementCoordinator.cs       Only component that changes retirement status
    RetirementStateMachine.cs      Legal transitions, declared Phase 2A shortcuts
    RetirementStateStore.cs        Atomic JSON persistence + state location rules
    RetirementServices.cs          Composition root for the retirement flow
    IOperationLog.cs
    FileOperationLog.cs            Persistent audit log
  Recovery/
    RecoveryRunner.cs              Recovery-side Phase 2A handoff
    DiskValidator.cs               Partition identity checks (read-only)
    BootEntryValidator.cs          WinRE / Boot 2 BCD validation
    RetirementExecutor.cs          NOT IMPLEMENTED destructive stub
    ValidationReport.cs
    VolumeIdentity.cs              Read-only Win32 volume GUID lookups
    VolumeLocator.cs               Read-only volume -> disk / GPT partition GUID mapping (P/Invoke only)
ssh/
  id_ed25519.pub
  id_ed25519          (private, gitignored)
  README.md
```

`app.manifest` requests administrator rights (`requireAdministrator`) so
Windows shows a UAC prompt when the app starts.

## How it works

On startup the app detects the current Windows from BCD:

```text
bcdedit /enum {current} /v
bcdedit /enum OSLOADER /v
```

The UI shows:

- **Current system** — the Windows you are running now
- **Target** — the other Windows Boot Loader

If you are on Boot 1, the button switches to Boot 2. If you are on Boot 2,
the button switches to Boot 1.

After you confirm, it:

1. Validates the target BCD GUID
2. Confirms that BCD entry exists
3. Runs `bcdedit /bootsequence {TARGET_GUID}`
4. If successful, runs `shutdown.exe /r /t 5`

## Configuration

`appsettings.json`:

```json
{
  "CleanSwitch": {
    "Boot2Guid": "{fc583d40-a29c-11f1-b0e3-e548a1d3146f}",
    "RecoveryGuid": "",
    "RestartDelaySeconds": 5,
    "RecoveryDataPath": "D:\\CleanSwitchData",
    "RecoveryDataVolumeGptId": "",
    "RecoveryDataFolderName": "CleanSwitchData",
    "StateFileName": "retirement-state.json",
    "LogDirectory": "",
    "AllowStateOnSystemVolume": false,
    "EnableDestructiveRetirement": false
  }
}
```

| Key | Used by | Meaning |
|---|---|---|
| `Boot2Guid` | Switch + retire | Optional. The other Windows loader. Auto-detected when exactly two Windows Boot Loader entries exist. |
| `RecoveryGuid` | Retire | BCD identifier of the Windows Recovery Environment entry. When empty, CleanSwitch reads the running entry's `recoverysequence`. Validated before use. |
| `RestartDelaySeconds` | Both | Delay passed to `shutdown /r /t N`. |
| `RecoveryDataPath` | Retire | Literal folder for the retirement state file and logs. Must be on a volume that is **not** Boot 1. Used only when `RecoveryDataVolumeGptId` is empty. |
| `RecoveryDataVolumeGptId` | Retire | Optional, empty by default. GPT unique partition GUID of the volume that holds the retirement state file. Preferred over `RecoveryDataPath`, because it is the only identifier that is stable across Boot 1, WinRE and Boot 2. Find it with `--list-volumes`. |
| `RecoveryDataFolderName` | Retire | Folder name on that volume. Defaults to the last segment of `RecoveryDataPath`, so `D:\CleanSwitchData` yields `CleanSwitchData`. Must be a plain name, not a rooted path. |
| `StateFileName` | Retire | State file name inside the retirement data folder. |
| `LogDirectory` | Both | Optional override. Defaults to `<retirement data folder>\logs`. |
| `AllowStateOnSystemVolume` | Retire | Test-only. Allows the state file to sit on the running Windows volume, which is unsafe for a real retirement. |
| `EnableDestructiveRetirement` | Reserved | Phase 2B placeholder. Has no effect: the executor throws regardless. |

Do not use `{current}` or `{bootmgr}` for any GUID setting. Aliases are rejected.

### Finding the Recovery GUID

From an **elevated** prompt:

```powershell
bcdedit /enum all /v
```

Look for the entry block whose `description` is `Windows Recovery Environment` and whose
`device` is a `ramdisk=[...]\Recovery\WindowsRE\Winre.wim` value. Copy its `identifier`
into `RecoveryGuid`. The same identifier appears as the `recoverysequence` value on the
Windows Boot Loader entry it belongs to:

```powershell
bcdedit /enum {current} /v | Select-String recoverysequence
```

Leaving `RecoveryGuid` empty makes CleanSwitch use that `recoverysequence` value
automatically. Setting it explicitly is recommended, because it is checked against BCD
before anything reboots.

### Where the retirement state lives, and why

The retirement state file is the only instruction set that survives the reboot into
recovery. If its only copy lived on Boot 1's `C:\`, it would be destroyed by the very
operation it is meant to drive. So:

- CleanSwitch resolves the folder to a volume GUID (via read-only Win32 mount-point
  queries) and compares it against the running Windows volume. If they match, the
  retirement flow refuses to start and tells you to choose another volume.
- The volume must be present when the flow starts, and a write probe must succeed, before
  any state is written or any boot entry is touched.
- Choose a volume that is also visible from WinRE: a second internal disk, a data
  partition, or a USB stick.

#### Identify the volume by GPT partition GUID, not by letter

A drive letter designates a **different volume** in each environment the retirement flow
crosses. `D:` is Boot 2 while Boot 1 runs; from Boot 2, `D:` is most likely Boot 1 — the
volume slated for deletion. Win32 volume GUIDs (`\\?\Volume{...}`) do not help either,
because WinPE's Mount Manager generates its own set. The only stable on-disk identifier is
the **GPT unique partition GUID** from the partition table.

So set `RecoveryDataVolumeGptId`:

```powershell
.\CleanSwitch.exe --list-volumes
```

Copy the GPT partition GUID of the volume that should hold the state file into
`RecoveryDataVolumeGptId`, and set `RecoveryDataFolderName` (default `CleanSwitchData`).
CleanSwitch then resolves that GUID to whatever mount point the volume currently has,
in every environment, with no per-environment config editing.

Note that `Get-Partition`'s `UniqueId` is **not** the GPT partition GUID — it can be a
synthetic value that repeats across disks. Use `--list-volumes`, which reads the partition
table directly.

**Write side** (Boot 1 creating the operation):

1. If `RecoveryDataVolumeGptId` is set, locate that volume, resolve its current mount
   point, and use `<mount>\<RecoveryDataFolderName>`. If the GUID is configured but the
   volume is not found, CleanSwitch **fails and stops**. It does not fall back to the
   letter path, because that fallback could write the state onto the volume being retired.
2. Otherwise use the literal `RecoveryDataPath`, exactly as older builds did.

Then the safety checks run unchanged: reject the running system volume unless
`AllowStateOnSystemVolume`, confirm the volume is present, and write-probe the folder.

**Read side** (WinRE, Boot 2):

1. Try the configured resolution above.
2. If no state file is there, scan every fixed volume for
   `<volume>\<RecoveryDataFolderName>\<StateFileName>`, parse each candidate, and accept
   only files whose `operation` is `RETIRE_BOOT1` and whose `schemaVersion` matches.
3. Exactly one valid candidate is used, and the log says prominently that it was found by
   scan rather than by configuration, naming the volume. **Two or more** distinct valid
   candidates is a hard failure that lists them all: ambiguity stops the flow rather than
   picking one.

When the operation is created, the state volume's disk number, partition number and GPT
partition GUID are recorded in the state file as `stateVolumeIdentity`, so a later phase
can prove it is looking at the volume Boot 1 actually wrote to.

The shipped `RecoveryDataPath` value `D:\CleanSwitchData` is a documented placeholder, not
a detected location, and `RecoveryDataVolumeGptId` ships empty on purpose. Run
`--list-volumes` on your machine and fill it in before using RETIRE SYSTEM.

### Logs

Every state transition, every `bcdedit` / `shutdown` invocation with its exit code and
captured output, and every validation decision is appended to:

```text
{RecoveryDataPath}\logs\{retire|recovery|startup}-YYYYMMDD.log
%ProgramData%\CleanSwitch\logs\{...}-YYYYMMDD.log
```

Two destinations on purpose: the first survives Boot 1 being retired, the second stays
readable if the external volume disappears mid-run. Logs are plain UTF-8 text and are
readable after a reboot.

## Run

```powershell
cd C:\CleanSwitch\CleanSwitch
dotnet restore
dotnet run --configuration Release
```

If `dotnet run` does not show a UAC prompt, start the built executable:

```powershell
dotnet build --configuration Release
.\bin\Release\net8.0-windows\CleanSwitch.exe
```

## Use

1. Open CleanSwitch as Administrator.
2. Check that **Current system** and **Target** look correct.
3. Click **Switch to ...**.
4. Confirm **Continue**.
5. Windows restarts into the other boot after 5 seconds.
6. If BCDEdit fails, an error is shown and the PC stays on the current boot.

**RETIRE SYSTEM** is the separate Phase 2A handoff described in
[Retiring Boot 1](#retiring-boot-1-phase-2a). It treats the currently running Windows as
Boot 1 (the one to retire) and the detected target as Boot 2, and it deletes nothing.

### Listing volumes

```powershell
.\CleanSwitch.exe --list-volumes
```

Prints one row per volume — disk number, partition number, GPT partition GUID, size,
filesystem, current mount points, drive type, and whether it is the running system volume —
then exits 0. It loads no configuration, writes no file, reads no BCD entry and changes no
boot state; it only reads the partition table.

Run it from an **elevated** PowerShell window so the output appears inline. Started from a
non-elevated prompt, UAC gives the elevated process no console to inherit, so CleanSwitch
allocates its own window and waits for Enter before closing it.

This is how you get the value for `RecoveryDataVolumeGptId`. See
[Where the retirement state lives, and why](#where-the-retirement-state-lives-and-why).

## GitHub SSH

SSH files are backed up in this repo:

| File | Purpose |
|---|---|
| `ssh/id_ed25519.pub` | Public key. Paste this into GitHub. |
| `ssh/id_ed25519` | Private key. Local backup only. Git ignores this file. |
| `keys/cleanswitch-github.pub` | Same public key, extra copy. |

Add the public key later:

1. GitHub → **Settings** → **SSH and GPG keys** → **New SSH key**
2. Title: `CleanSwitch PC`
3. Paste `ssh/id_ed25519.pub`
4. Use `git@github.com:USER/REPO.git`

Do not push `ssh/id_ed25519`. Anyone with that private key can use this GitHub account's SSH access.

The live key pair on this PC is still:

```text
%USERPROFILE%\.ssh\id_ed25519
%USERPROFILE%\.ssh\id_ed25519.pub
```

## Retiring Boot 1 (Phase 2A)

**Phase 2A is implemented. Phases 2B and 2C are not.** Phase 2A builds and proves the
handoff; it deletes nothing at all.

What RETIRE SYSTEM does today:

1. One confirmation dialog: *This will permanently retire Boot 1 and switch this PC to
   Boot 2.*
2. Validates the recovery data location (must exist, be writable, and not be the running
   Windows volume).
3. Validates the Recovery BCD entry.
4. Writes the retirement state file with status `PENDING`.
5. Runs `bcdedit /bootsequence {RECOVERY_GUID}`.
6. Restarts.

Any failure in steps 2–5 shows an error and the PC is **not** restarted.

Then, from a recovery environment command prompt (see the runtime caveat below):

```text
X:\...\CleanSwitch.exe --list-volumes       read-only volume / GPT partition report, exits 0
X:\...\CleanSwitch.exe --recovery-dry-run   validate + log only, no BCD change, no restart
X:\...\CleanSwitch.exe --recovery-run       perform the Phase 2A handoff
```

`--recovery-run` loads the state file, moves `PENDING → RECOVERY_STARTED`, reports what it
can identify about Boot 1, validates the Boot 2 entry, **skips deletion entirely**, sets
Boot 2 as the next boot, records `BCD_UPDATED → VERIFIED`, and restarts. `COMPLETE` is
recorded by the GUI the next time it starts on Boot 2.

### Runtime caveat for the recovery side

WinRE does not ship the .NET 8 desktop runtime, so the framework-dependent build cannot
run there. Exercising `--recovery-run` inside WinRE requires a self-contained publish, for
example:

```powershell
dotnet publish --configuration Release --runtime win-x64 --self-contained true
```

placed on a volume WinRE can see. Until that is set up, use `--recovery-dry-run` from the
running Windows: it performs every validation, transition and log write, and changes no
boot state. This limitation is why Phase 2A stops at proving the handoff.

### Retirement state machine

```text
PENDING → RECOVERY_STARTED → TARGET_VALIDATED → BOOT2_VALIDATED
        → BOOT1_RETIRED → BCD_UPDATED → VERIFIED → COMPLETE
```

Any non-terminal state may also move to `FAILED` or `ABORTED`, and `FAILED` may be retried
back to `RECOVERY_STARTED`. Transitions are checked against a single table in
`Services/RetirementStateMachine.cs`; an illegal transition throws instead of being
accepted. Phase 2A takes two **declared** shortcuts, logged each time they are used:

- `RECOVERY_STARTED → BOOT2_VALIDATED` — nothing will be deleted, so the target is only
  reported, never validated for destruction.
- `BOOT2_VALIDATED → BCD_UPDATED` — `BOOT1_RETIRED` is skipped because Phase 2A retires
  nothing.

The state file is written atomically (temp file, flush to disk, then a single replace with
a `.bak` kept), so a crash mid-write cannot corrupt it. It carries `schemaVersion`, a
per-transition timestamped audit trail, and `lastError`, which is enough to resume or to
stop safely after a power loss.

```json
{
  "operation": "RETIRE_BOOT1",
  "status": "PENDING",
  "boot1Id": "{...}",
  "boot2Id": "{...}",
  "recoveryId": "{...}",
  "createdAtUtc": "2026-01-01T00:00:00.000+00:00",
  "updatedAtUtc": "2026-01-01T00:00:00.000+00:00",
  "schemaVersion": 1,
  "phase": "2A",
  "destructiveDeletionPerformed": false,
  "stateVolumeIdentity": {
    "diskNumber": 0,
    "partitionNumber": 5,
    "volumeGuidPath": "\\\\?\\Volume{...}\\",
    "gptPartitionId": "{...}",
    "source": "Volume located by GPT partition GUID {...}"
  },
  "transitions": [ { "from": "PENDING", "to": "PENDING", "atUtc": "...", "reason": "..." } ]
}
```

`stateVolumeIdentity` is optional metadata, so `schemaVersion` stays at 1 and state files
written by earlier builds still load.

### Testing the handoff safely

1. Run `CleanSwitch.exe --list-volumes`, put the GPT partition GUID of a non-Boot-1 volume
   into `RecoveryDataVolumeGptId`, and confirm the app starts without a retirement error in
   the status line.
2. Set `RecoveryGuid` from `bcdedit /enum all /v` and confirm the app accepts it.
3. Run `CleanSwitch.exe --recovery-dry-run` **from the running Windows** first. It performs
   every validation and state transition, writes the log, and changes no boot state. Check
   the log and the state file.
4. Only then use RETIRE SYSTEM, and expect to land in WinRE. Because Phase 2A deletes
   nothing, the worst case is a one-time boot into recovery, which you can leave by
   restarting normally.
5. To reset between tests, delete the state file (`retirement-state.json`) while no
   operation is in flight.

Note: `bcdedit /bootsequence` is a one-time setting. Clearing it, if needed, is
`bcdedit /deletevalue {bootmgr} bootsequence` from an elevated prompt.

## Not implemented

- **Phase 2B** — actually retiring Boot 1 (partition removal). `RetirementExecutor` throws.
- **Phase 2C** — removing the Boot 1 BCD entry and reclaiming the space.
- Automatic invocation from WinRE. Phase 2A is started by hand with `--recovery-run`.
- Verifying `stateVolumeIdentity` against the volume the state file was actually read from.
  It is recorded, but nothing compares it yet.
- Deriving Boot 1's partition identity from anything other than the drive letter in its BCD
  `device` value. That letter is only meaningful in the environment that entry belongs to,
  so `boot1Identity` is metadata a later phase must re-verify, never act on directly.

## Out of scope for this POC

- Disk format / partition delete
- WinPE or BitLocker erase
- HTTP API or LAN control
- Multiple PCs, Wake-on-LAN, MAC discovery
