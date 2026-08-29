<#
.SYNOPSIS
    Builds the same self-contained, single-file, win-x64 CleanSwitch artifact that
    .github/workflows/release.yml produces, but locally.

.DESCRIPTION
    Publishes CleanSwitch as a self-contained single-file win-x64 executable and
    packages CleanSwitch.exe + appsettings.json into a zip.

    Why appsettings.json is a loose file and not embedded: CleanSwitch loads its
    configuration from Path.Combine(AppContext.BaseDirectory, "appsettings.json")
    and throws when the file is missing. Under single-file publish
    AppContext.BaseDirectory is the folder containing the exe (not an extracted
    temp folder), so appsettings.json has to sit next to the exe. Never ship the
    bare exe on its own.

    ############################################################################
    #  DO NOT RUN THE PRODUCED CleanSwitch.exe ON THIS MACHINE.                #
    #                                                                          #
    #  This is a live dual-boot test PC. CleanSwitch edits Windows boot         #
    #  configuration (bcdedit) and can trigger restarts. Launching the exe      #
    #  "just to see if it works" risks making the machine unbootable. This      #
    #  script only ever builds and zips; it never executes the output, and      #
    #  neither should you. Test on a VM or a disposable machine.                #
    ############################################################################

.PARAMETER Version
    Version to stamp into the assembly, without a leading "v" (e.g. "1.2.0").
    Defaults to 0.0.0-dev.

.PARAMETER OutputRoot
    Where to place the publish folder and zip. Defaults to
    "$env:TEMP\CleanSwitch-release", which is outside the repository working tree
    so local publishing can never dirty the repo. If you point this inside the
    repo, use the gitignored "artifacts" folder.

.PARAMETER ReadyToRun
    Enable PublishReadyToRun. Off by default: measured on this project it adds
    ~16 MB to the exe for a startup win that is lost in the noise of the UAC
    prompt, since framework assemblies already ship ReadyToRun.

.PARAMETER NoCompression
    Disable EnableCompressionInSingleFile. Compression is on by default because
    it takes the on-disk exe from ~154 MB down to ~68 MB. It barely changes the
    zip size (the zip compresses either way), but it is what the user is left
    with after extracting.

.EXAMPLE
    .\scripts\publish-release.ps1 -Version 1.0.0

.EXAMPLE
    .\scripts\publish-release.ps1 -Version 1.0.0 -OutputRoot D:\builds\cleanswitch
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0-dev',
    [string]$OutputRoot = (Join-Path $env:TEMP 'CleanSwitch-release'),
    [switch]$ReadyToRun,
    [switch]$NoCompression
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'CleanSwitch\CleanSwitch.csproj'

if (-not (Test-Path $project)) {
    throw "Could not find the CleanSwitch project at '$project'."
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet was not found on PATH. Install the .NET 8 SDK, or add "C:\Program Files\dotnet" to PATH.'
}

$Version = $Version.TrimStart('v', 'V')
if ($Version -notmatch '^[0-9A-Za-z.\-+]+$') {
    throw "Version '$Version' contains unexpected characters."
}

# AssemblyVersion/FileVersion accept numeric quads only, so drop any
# -prerelease / +build metadata and pad out to four parts.
$parts = @((($Version -split '[-+]')[0] -split '\.') | Where-Object { $_ -match '^\d+$' })
while ($parts.Count -lt 4) { $parts += '0' }
$assemblyVersion = ($parts[0..3]) -join '.'

$publishDir = Join-Path $OutputRoot "publish-$Version"
$zipPath = Join-Path $OutputRoot "CleanSwitch-$Version-win-x64.zip"

# Refuse to publish into the tracked source tree. bin/ and obj/ inside
# CleanSwitch/ are also where a normal build writes, and a concurrent build
# there would fight over file locks.
$normalizedOut = [IO.Path]::GetFullPath($OutputRoot)
$normalizedRepo = [IO.Path]::GetFullPath($repoRoot)
$artifactsDir = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if ($normalizedOut.StartsWith($normalizedRepo, [StringComparison]::OrdinalIgnoreCase) -and
    -not $normalizedOut.StartsWith($artifactsDir, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputRoot '$normalizedOut' is inside the repository. Use a path outside the repo, or the gitignored '$artifactsDir'."
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# Publish settings live here rather than in CleanSwitch.csproj so ordinary
# development builds are untouched.
$publishArgs = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:EnableCompressionInSingleFile=$(if ($NoCompression) { 'false' } else { 'true' })",
    "-p:PublishReadyToRun=$(if ($ReadyToRun) { 'true' } else { 'false' })",
    "-p:Version=$Version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    '-o', $publishDir
)

Write-Host "Publishing CleanSwitch $Version (assembly $assemblyVersion)" -ForegroundColor Cyan
Write-Host "  dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

$exe = Get-Item (Join-Path $publishDir 'CleanSwitch.exe')
$settings = Join-Path $publishDir 'appsettings.json'
if (-not (Test-Path $settings)) {
    throw 'appsettings.json was not emitted next to CleanSwitch.exe; the app would throw at startup.'
}

$strays = Get-ChildItem $publishDir -Recurse -File -Filter '*.dll'
if ($strays) {
    throw "Publish is not single-file; found loose assemblies: $($strays.Name -join ', ')"
}

Compress-Archive -Path $exe.FullName, $settings -DestinationPath $zipPath -CompressionLevel Optimal
$zip = Get-Item $zipPath

Write-Host ''
Write-Host 'Publish output' -ForegroundColor Green
Get-ChildItem $publishDir -Recurse -File |
    Sort-Object Length -Descending |
    Select-Object Name, @{ n = 'Size (MB)'; e = { [math]::Round($_.Length / 1MB, 2) } } |
    Format-Table -AutoSize

Write-Host 'Artifact' -ForegroundColor Green
Write-Host ('  zip      : {0}' -f $zip.FullName)
Write-Host ('  zip size : {0:N1} MB' -f ($zip.Length / 1MB))
Write-Host ('  exe size : {0:N1} MB (extracted)' -f ($exe.Length / 1MB))
Write-Host ('  contents : CleanSwitch.exe + appsettings.json')
Write-Host ''
Write-Host 'Keep CleanSwitch.exe and appsettings.json in the same folder; the app throws at startup without appsettings.json.' -ForegroundColor Yellow
Write-Host 'Do NOT launch this exe on the dual-boot test machine. It edits boot configuration.' -ForegroundColor Yellow
