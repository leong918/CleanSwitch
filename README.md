# CleanSwitch

Local Windows 11 proof of concept for switching between two already-installed
Windows boots on one PC.

Open `CleanSwitch.exe` on the running Windows, confirm the detected current
and target boots, then click the switch button. The app sets the other Windows
as the **next boot only** and restarts. It does not change the permanent
default boot entry.

This is a single-PC desktop app. There is no HTTP API, no Mac controller, and
no network control.

## Safety

CleanSwitch does **not**:

- Format or delete disks or partitions
- Modify the other Windows install
- Change the default BCD boot entry
- Use WinPE, BitLocker wipe, Wake-on-LAN, or MAC discovery

The only boot command it runs is:

```text
bcdedit /bootsequence {TARGET_GUID}
```

If that command fails, Windows is not restarted.

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
  Program.cs
  MainForm.cs
  MainForm.Designer.cs
  appsettings.json
  app.manifest
  Services/
    IBootManager.cs
    WindowsBootManager.cs
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
    "RestartDelaySeconds": 5
  }
}
```

`Boot2Guid` is optional. The app auto-detects the other Windows install when
exactly two Windows Boot Loader entries exist. Set `Boot2Guid` only if there
are more than two Windows entries.

Do not use `{current}` or `{bootmgr}`.

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

## Future: delete Boot 1 after switch

Not implemented. After this POC is proven, a later phase can remove Boot 1
**from Boot 2** after the PC has already switched. See `FUTURE.md`.

The current button still only sets the next boot and restarts.

## Out of scope for this POC

- Disk format / partition delete
- WinPE or BitLocker erase
- HTTP API or LAN control
- Multiple PCs, Wake-on-LAN, MAC discovery
