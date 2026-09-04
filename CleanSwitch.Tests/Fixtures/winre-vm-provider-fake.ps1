param(
    [Parameter(Mandatory = $true)] [string] $Command,
    [Parameter(Mandatory = $true)] [string] $VmId,
    [Parameter(Mandatory = $true)] [string] $ArgumentsBase64
)

$ErrorActionPreference = 'Stop'
$arguments = [Text.UTF8Encoding]::new($false).GetString([Convert]::FromBase64String($ArgumentsBase64)) |
    ConvertFrom-Json
$stateRoot = $env:CLEAN_SWITCH_FAKE_VM_STATE_ROOT
$diskPath = $env:CLEAN_SWITCH_FAKE_VM_DISK_PATH
if ([string]::IsNullOrWhiteSpace($stateRoot) -or [string]::IsNullOrWhiteSpace($diskPath)) { exit 90 }
[IO.Directory]::CreateDirectory($stateRoot) | Out-Null
$data = @{}

switch ($Command) {
    'inspect' {
        $disk = [ordered]@{ attachmentType = $env:CLEAN_SWITCH_FAKE_VM_ATTACHMENT_TYPE; hostPath = $diskPath }
        if (-not [string]::IsNullOrWhiteSpace($env:CLEAN_SWITCH_FAKE_VM_HOST_DISK_NUMBER)) {
            $disk.hostDiskNumber = $env:CLEAN_SWITCH_FAKE_VM_HOST_DISK_NUMBER
        }
        $data = [ordered]@{
            disposable = $true; windowsBuild = 26100; firmware = 'UEFI'; partitionStyle = 'GPT'
            state = 'Off'; disks = @($disk)
        }
    }
    'checkpoint' {
        $checkpointPath = Join-Path $stateRoot ("checkpoint-" + $arguments.name + '.txt')
        if ($arguments.action -eq 'create') {
            Copy-Item -LiteralPath (Join-Path $stateRoot 'probe.txt') -Destination $checkpointPath
        }
        elseif ($arguments.action -eq 'delete') {
            Remove-Item -LiteralPath $checkpointPath -ErrorAction SilentlyContinue
        }
        else { exit 91 }
    }
    'restore' {
        $checkpointPath = Join-Path $stateRoot ("checkpoint-" + $arguments.name + '.txt')
        Copy-Item -LiteralPath $checkpointPath -Destination (Join-Path $stateRoot 'probe.txt') -Force
    }
    'guest-command' {
        $result = [ordered]@{ action = $arguments.action }
        switch ($arguments.action) {
            'checkpoint-probe-write' { [IO.File]::WriteAllText((Join-Path $stateRoot 'probe.txt'), [string]$arguments.value) }
            'checkpoint-probe-read' { $result['value'] = [IO.File]::ReadAllText((Join-Path $stateRoot 'probe.txt')) }
            'checkpoint-probe-delete' { [IO.File]::Delete((Join-Path $stateRoot 'probe.txt')) }
            default { exit 92 }
        }
        $data = [ordered]@{ exitCode = 0; result = $result }
    }
    { $_ -in @('start', 'stop', 'hard-poweroff', 'reboot-to-winre', 'collect-artifacts') } { }
    default { exit 93 }
}

[ordered]@{ success = $true; vmId = $VmId; command = $Command; data = $data } |
    ConvertTo-Json -Depth 10 -Compress
