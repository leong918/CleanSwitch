# VHD/VHDX integration test

Live deletion stays disabled in production (`DestructiveOperationsImplemented = false`,
`EnableDestructiveRetirement = false`). This harness is the only place that may run
real `diskpart` against a **disposable VHDX**. It never targets the machine's physical
disk.

## How to run

Normal `dotnet test` skips the VHD test.

```powershell
$env:CLEANSWITCH_VHD_TESTS = "1"
dotnet test C:\CleanSwitch\CleanSwitch.sln --filter Category=VhdIntegration
```

Requires an elevated process. The test creates `%TEMP%\cleanswitch-vhd-*.vhdx`, attaches
it, deletes only the fake Boot 1 partition on that virtual disk, then detaches and
deletes the file.

## How the test proves it is a VHD, not a physical disk

Before any `select disk` / `delete partition` the harness must prove all of the following.
Any failure is fail-closed; diskpart is not started.

1. **File-to-disk mapping (primary).** `virtdisk.dll` `OpenVirtualDisk` +
   `GetVirtualDiskPhysicalPath` is called on the **exact temporary .vhdx path** created
   by this test. Windows returns `\\.\PhysicalDriveN`. That `N` is the only disk number
   the test will use.
2. **Disk 0 is refused.** If the mapping returns disk 0, the test aborts. Disk 0 is the
   physical NVMe on this PC.
3. **Bus type.** `IOCTL_STORAGE_QUERY_PROPERTY` on `\\.\PHYSICALDRIVE{N}` must report
   `BusTypeVirtual` or `BusTypeFileBackedVirtual`. NVMe / SCSI / ATA / USB fail closed.
4. **Not the running system disk.** Volume enumeration must not show the running OS
   volume on disk `N`.
5. **Identity isolation.** GPT unique ids are read from the VHD after creation and
   injected as `IRetirementIdentitySet`. Production `PinnedRetirementTargets` (this PC's
   Boot 1 / Boot 2 / ESP / WinRE GUIDs) are never used. A collision with those pins
   aborts.
6. **Single-disk layout.** `SingleDiskGptLayoutSource` reads only disk `N`. The physical
   GPT table is not part of resolve/verify.
7. **Re-proof immediately before diskpart.** `VhdBoundDiskCommand` repeats steps 1–6
   against the resolved target. The resolved disk number, disk GPT id, and Boot 1 GPT
   id must still match the VHD. Protected GPT ids (ESP, MSR, Boot 2, both Recovery)
   cannot be the target.
8. **Opt-in gate.** `VhdBoundDiskCommand` refuses unless `CLEANSWITCH_VHD_TESTS=1`.

`DiskpartDestructiveDiskCommand` is invoked only after that wrapper accepts the target.

## Cleanup

`DisposableVhdSession.Dispose` always detaches the VHDX and deletes the temporary file,
including on failure.
