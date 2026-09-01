# Releasing CleanSwitch

CleanSwitch ships as a **self-contained, single-file, win-x64** build, so users do
not need to install the .NET runtime.

## What a release contains

A single zip named `CleanSwitch-<version>-win-x64.zip` holding exactly two files:

| File | Why it is there |
| --- | --- |
| `CleanSwitch.exe` | The whole app plus a bundled .NET 8 runtime. Requests administrator rights via `app.manifest`, so Windows shows a UAC prompt. |
| `appsettings.json` | Runtime configuration. **Must stay next to the exe.** |

`appsettings.json` is not embedded in the exe on purpose. CleanSwitch reads it
from `Path.Combine(AppContext.BaseDirectory, "appsettings.json")` and throws on
startup if it is missing, and under single-file publish `AppContext.BaseDirectory`
is the folder containing the exe rather than an extracted temp folder. Shipping a
bare exe would produce an app that cannot start.

Expect roughly **68 MB extracted / 63 MB zipped**. Bundling the .NET runtime and
the WinForms stack is what accounts for the size.

## Cutting a release

1. Land all the changes you want in the release on `main`.
2. Decide the version, `X.Y.Z`. There is no `<Version>` property in
   `CleanSwitch.csproj`; the version is passed to `dotnet publish` from the tag,
   so there is nothing to bump in a file. If you would rather pin the version in
   source, add `<Version>` to `CleanSwitch.csproj` and keep it in sync with the
   tag.
3. Tag and push **the tag only**:

   ```powershell
   git tag -a v1.2.0 -m "CleanSwitch 1.2.0"
   git push origin v1.2.0
   ```

4. The `Release` workflow (`.github/workflows/release.yml`) picks up the `v*` tag
   and does the rest: publishes, verifies, zips, creates the GitHub Release, and
   attaches the zip. Watch it under the repository's **Actions** tab.
   If a Release for that tag already exists (for example you created it from the
   GitHub UI and it only has source zips), the workflow **uploads the build zip
   onto the existing Release** instead of failing.
5. Check the published release page and the attached zip, then announce it.
   (The workflow publishes directly rather than drafting. If you would rather
   review before it goes live, add `--draft` to the `gh release create` call in
   the workflow.)

Do not push the same tag twice. A second workflow run on `v1.0.1` used to fail
with "a release with the same tag name already exists". After the upload-if-exists
change, a re-run attaches the zip; it still will not create a second Release.

Tag names must start with `v`. A tag such as `v1.2.0-rc1` is published as a
GitHub **prerelease** automatically (any version containing `-` is treated as a
prerelease).

## Dry runs

Trigger the workflow manually from **Actions → Release → Run workflow** with an
optional version. A manual run builds, verifies, and uploads the zip as a
workflow artifact, but **never creates a GitHub Release** — release creation is
gated on the trigger being a tag push. Use this to confirm a build is healthy
before committing to a tag.

## Building locally

```powershell
.\scripts\publish-release.ps1 -Version 1.2.0
```

By default the output goes to `%TEMP%\CleanSwitch-release`, outside the working
tree, so a local publish never dirties the repo. Override with `-OutputRoot`; the
script refuses paths inside the repo other than the gitignored `artifacts/`
folder.

Two optional switches, both defaulted to match the workflow:

- `-ReadyToRun` enables `PublishReadyToRun`. Off by default: measured on this
  project it grows the exe from 154 MB to 170 MB while buying almost no startup
  time, because framework assemblies already ship ReadyToRun and the UAC prompt
  dominates perceived launch time.
- `-NoCompression` disables `EnableCompressionInSingleFile`. Compression is on by
  default because it takes the extracted exe from 154 MB to 68 MB. It hardly
  changes the zip (62.6 MB vs 63.0 MB — the zip compresses either way), but it is
  what the user is left with on disk after extracting.

> **Do not run the produced `CleanSwitch.exe` on a machine you care about.**
> CleanSwitch edits Windows boot configuration and can trigger restarts. On the
> dual-boot test PC, build and inspect only; test the exe on a VM or a disposable
> machine.

## The publish command

Both the workflow and the local script run the same thing:

```powershell
dotnet publish CleanSwitch/CleanSwitch.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:Version=<version> `
  -p:AssemblyVersion=<numeric quad> `
  -p:FileVersion=<numeric quad> `
  -o publish
```

These settings are deliberately kept out of `CleanSwitch.csproj` so that everyday
development builds and F5 debugging are unaffected.

`-r win-x64` with `net8.0-windows` and `UseWindowsForms` means the build only
works on a Windows runner; the workflow uses `windows-latest`.

## Permissions

The workflow authenticates with the built-in `GITHUB_TOKEN` and declares
`permissions: contents: write`. No secrets need to be configured, and none should
be added.
