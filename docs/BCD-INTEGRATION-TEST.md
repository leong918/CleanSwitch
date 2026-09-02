# Isolated BCD integration test

Phase 2C production deletion stays disabled (`BcdOperationsImplemented = false`).
This harness is the only place that may run real `bcdedit /delete`, and only against
a **temporary store file**.

## Why this does not touch the host BCD

Windows `bcdedit` can operate on a file store:

```
bcdedit /createstore %TEMP%\cleanswitch-bcd-....bcd
bcdedit /store %TEMP%\cleanswitch-bcd-....bcd /create ...
bcdedit /store %TEMP%\cleanswitch-bcd-....bcd /delete {GUID}
```

When `/store` is present, bcdedit opens that file instead of the system BCD
(`\EFI\Microsoft\Boot\BCD` or `\Boot\BCD`). The test:

1. Creates a new file under `%TEMP%` and canonicalizes that path.
2. Refuses any path that looks like the system BCD, and refuses `/delete` without `/store`.
3. Requires the executed command line to contain `/store` and that temp path. Bare `bcdedit /delete` is refused.
4. Creates `{bootmgr}`, fake Boot 1, fake Boot 2 (also the default), a recovery-like object, and an unrelated loader.
5. Deletes the file in `finally`.

The system store is never named on the command line.

A `/createstore` file has no running OS, so `{current}` is absent. The isolated
source reports that as `BcdAliasResolution.Absent` instead of pretending a
running loader exists. Production `BootManagerBcdStoreSource` never treats a
missing `{current}` or `{default}` as success: unresolved aliases fail closed.
The `{current}` / `{default}` refusal cases are covered by unit tests, not by
this file-store harness.

## How to run

Normal `dotnet test` skips this test.

```powershell
$env:CLEANSWITCH_BCD_TESTS = "1"
dotnet test C:\CleanSwitch\CleanSwitch.sln --filter Category=BcdIntegration
```

`bcdedit` usually needs an elevated process even for a file store. If `/createstore`
fails, the opted-in test fails closed rather than falling back to the system store.

## What the fake-command tests already cover

Always-on unit tests inject `FakeDestructiveBcdCommand`. They prove GUID selection,
alias refusal, missing/duplicate/collision cases, `{bootmgr}` / recovery rejection,
executor throw / non-zero exit, post-delete Boot 1 still present, Boot 2 missing,
unrelated object disappearance, and "delete is invoked exactly once with the
resolved Boot 1 GUID". Those tests never start `bcdedit.exe`.
