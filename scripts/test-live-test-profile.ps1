param(
    [switch]$Live
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path) | Out-Null
Set-Location ..

if ($Live) {
    Write-Host "Running live-test profile tests..."
    dotnet test CleanSwitch.sln --no-restore -p:CleanSwitchLiveTestBuild=true
} else {
    Write-Host "Running default safe profile tests..."
    dotnet test CleanSwitch.sln --no-restore -p:CleanSwitchLiveTestBuild=false
}
