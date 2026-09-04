[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$required = @{
    CLEANSWITCH_VHD_TESTS = 'Disposable VHD fixture'
    CLEANSWITCH_BCD_TESTS = 'Isolated BCD store fixture'
    CLEAN_SWITCH_RUN_WINRE_WIM_INTEGRATION = 'Disposable WIM fixture'
    CLEAN_SWITCH_RUN_WINRE_DEPLOYMENT_VM_INTEGRATION = 'Disposable VM deployment profile'
    CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS = 'Disposable VM harness path'
}

$missing = foreach ($entry in $required.GetEnumerator()) {
    $value = [Environment]::GetEnvironmentVariable($entry.Key)
    if ([string]::IsNullOrWhiteSpace($value) -or
        ($entry.Key -ne 'CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS' -and $value -ne '1') -or
        ($entry.Key -eq 'CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS' -and -not (Test-Path -LiteralPath $value))) {
        "$($entry.Key) ($($entry.Value))"
    }
}

if ($missing) {
    throw "Mandatory pre-physical-deployment fixtures are unavailable: $($missing -join '; '). No readiness claim is permitted."
}

$root = Split-Path -Parent $PSScriptRoot
& dotnet test (Join-Path $root 'CleanSwitch.sln') --nologo -p:CleanSwitchLiveTestBuild=true
if ($LASTEXITCODE -ne 0) {
    throw "Mandatory pre-physical-deployment test profile failed with exit code $LASTEXITCODE."
}
