[CmdletBinding(DefaultParameterSetName = 'Readiness')]
param(
    [Parameter(ParameterSetName = 'Cycle', Mandatory = $true)]
    [ValidateRange(1, 1000)]
    [int] $Cycle,
    [Parameter(ParameterSetName = 'Cycle', Mandatory = $true)]
    [Parameter(ParameterSetName = 'Command')]
    [Parameter(ParameterSetName = 'Readiness')]
    [string] $ResultPath,
    [Parameter(ParameterSetName = 'Command', Mandatory = $true)]
    [ValidateSet('checkpoint', 'restore', 'start', 'stop', 'hard-poweroff',
        'guest-command', 'wait-for-guest', 'collect-artifacts')]
    [string] $Command,
    [Parameter(ParameterSetName = 'Command')]
    [string] $ArgumentsJson = '{}',
    [Parameter(ParameterSetName = 'Readiness')]
    [switch] $Readiness
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RequiredProviderCommands = @('inspect', 'checkpoint', 'restore', 'start', 'stop',
    'hard-poweroff', 'guest-command', 'wait-for-guest', 'collect-artifacts')
$script:AllowedDiskExtensions = @('.vhd', '.vhdx', '.vmdk', '.qcow2')

function Write-DurableJson {
    param([Parameter(Mandatory = $true)] [string] $Path,
          [Parameter(Mandatory = $true)] [object] $Value)
    $full = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $full
    if (-not [string]::IsNullOrWhiteSpace($parent)) { [IO.Directory]::CreateDirectory($parent) | Out-Null }
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Value | ConvertTo-Json -Depth 20))
    $stream = [IO.FileStream]::new($full, [IO.FileMode]::Create, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function ConvertTo-Hashtable {
    param([Parameter(Mandatory = $true)] [object] $InputObject)
    $table = @{}
    foreach ($property in $InputObject.PSObject.Properties) { $table[$property.Name] = $property.Value }
    return $table
}

function Get-RequiredProperty {
    param([Parameter(Mandatory = $true)] [object] $Object,
          [Parameter(Mandatory = $true)] [string] $Name)
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value -or
        ($property.Value -is [string] -and [string]::IsNullOrWhiteSpace($property.Value))) {
        throw "Required property '$Name' is absent."
    }
    return $property.Value
}

function Get-Sha256 {
    param([string] $Path)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '') }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

function Get-HarnessConfiguration {
    $configPath = $env:CLEAN_SWITCH_WINRE_VM_CONFIG
    if ([string]::IsNullOrWhiteSpace($configPath)) { throw 'CLEAN_SWITCH_WINRE_VM_CONFIG is required.' }
    $configPath = [IO.Path]::GetFullPath($configPath)
    if (-not [IO.File]::Exists($configPath)) { throw "VM harness configuration does not exist: $configPath" }
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    if ((Get-RequiredProperty $config 'schemaVersion') -ne 2) { throw 'VM harness configuration schemaVersion must be 2.' }
    if ((Get-RequiredProperty $config 'disposable') -ne $true) {
        throw 'The VM configuration is not explicitly marked disposable=true.'
    }
    $vmId = [string](Get-RequiredProperty $config 'vmId')
    if ($vmId -notmatch '^[A-Za-z0-9._-]{1,128}$') { throw 'VM identity contains unsupported characters.' }
    $vmGuid = [string](Get-RequiredProperty $config 'vmGuid')
    $checkpointGuid = [string](Get-RequiredProperty $config 'pristineCheckpointGuid')
    if ($vmGuid -notmatch '^\{?[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}?$') {
        throw 'vmGuid must be a concrete GUID.'
    }
    if ($checkpointGuid -notmatch '^\{?[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}?$') {
        throw 'pristineCheckpointGuid must be a concrete GUID.'
    }
    $sourceCommit = [string](Get-RequiredProperty $config 'sourceCommit')
    if ($sourceCommit -notmatch '^[0-9a-fA-F]{40}$') { throw 'sourceCommit must be an exact 40-hex commit SHA.' }
    $providerPath = [IO.Path]::GetFullPath([string](Get-RequiredProperty $config 'providerScript'))
    if (-not [IO.File]::Exists($providerPath)) { throw "VM provider script does not exist: $providerPath" }
    if ([IO.Path]::GetExtension($providerPath) -ine '.ps1') { throw 'VM provider must be an explicit PowerShell script.' }
    $roots = @((Get-RequiredProperty $config 'approvedVmStorageRoots'))
    if ($roots.Count -eq 0) { throw 'At least one approved VM storage root is required.' }
    $resolvedRoots = foreach ($root in $roots) {
        $full = [IO.Path]::GetFullPath([string]$root).TrimEnd('\')
        if (-not [IO.Directory]::Exists($full)) { throw "Approved VM storage root does not exist: $full" }
        $item = Get-Item -LiteralPath $full -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Approved VM storage root must not be a reparse point: $full"
        }
        $full
    }
    $config | Add-Member -NotePropertyName ResolvedProviderScript -NotePropertyValue $providerPath -Force
    $config | Add-Member -NotePropertyName ResolvedStorageRoots -NotePropertyValue @($resolvedRoots) -Force
    $config | Add-Member -NotePropertyName ConfigurationPath -NotePropertyValue $configPath -Force
    $config | Add-Member -NotePropertyName ConfigurationSha256 -NotePropertyValue (Get-Sha256 $configPath) -Force
    return $config
}

function Invoke-Provider {
    param([object] $Config, [string] $ProviderCommand, [object] $Arguments = @{})
    if ($script:RequiredProviderCommands -notcontains $ProviderCommand) { throw "Unsupported provider command: $ProviderCommand" }
    $argumentJson = $Arguments | ConvertTo-Json -Depth 20 -Compress
    $argumentBase64 = [Convert]::ToBase64String([Text.UTF8Encoding]::new($false).GetBytes($argumentJson))
    if ([string]$Config.ResolvedProviderScript -match '"') { throw 'Provider script path contains an unsupported quote.' }
    $timeoutSeconds = 120
    if ($null -ne $Config.PSObject.Properties['providerTimeoutSeconds']) {
        $timeoutSeconds = [int]$Config.providerTimeoutSeconds
        if ($timeoutSeconds -lt 5 -or $timeoutSeconds -gt 1800) { throw 'providerTimeoutSeconds must be between 5 and 1800.' }
    }
    $captureRoot = Join-Path ([IO.Path]::GetTempPath()) ("cleanswitch-vm-provider-" + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($captureRoot) | Out-Null
    $stdoutPath = Join-Path $captureRoot 'stdout.txt'
    $stderrPath = Join-Path $captureRoot 'stderr.txt'
    $providerArguments = @(
        '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass',
        '-File', ('"' + [string]$Config.ResolvedProviderScript + '"'),
        '-Command', $ProviderCommand, '-VmId', [string]$Config.vmId,
        '-ArgumentsBase64', $argumentBase64)
    $process = $null
    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $providerArguments `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath `
            -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit($timeoutSeconds * 1000)) {
            try { $process.Kill() } catch { }
            throw "Provider command '$ProviderCommand' exceeded its bounded timeout of $timeoutSeconds seconds."
        }
        $text = if ([IO.File]::Exists($stdoutPath)) { [IO.File]::ReadAllText($stdoutPath).Trim() } else { '' }
        $errorText = if ([IO.File]::Exists($stderrPath)) { [IO.File]::ReadAllText($stderrPath).Trim() } else { '' }
        $process.Refresh()
        $exitCode = if ($process.HasExited) { [int]$process.ExitCode } else { -1 }
    }
    finally {
        if ($null -ne $process) { $process.Dispose() }
        if ([IO.Directory]::Exists($captureRoot)) { [IO.Directory]::Delete($captureRoot, $true) }
    }
    if ($exitCode -ne 0) { throw "Provider command '$ProviderCommand' failed with exit code $exitCode. Output: $text" }
    if (-not [string]::IsNullOrWhiteSpace($errorText)) { throw "Provider command '$ProviderCommand' wrote to stderr: $errorText" }
    try { $response = $text | ConvertFrom-Json }
    catch { throw "Provider command '$ProviderCommand' did not return one JSON object. Output: $text" }
    if ((Get-RequiredProperty $response 'success') -ne $true) { throw "Provider command '$ProviderCommand' returned success=false." }
    if ([string](Get-RequiredProperty $response 'vmId') -cne [string]$Config.vmId) { throw "Provider command '$ProviderCommand' returned the wrong VM identity." }
    if ([string](Get-RequiredProperty $response 'command') -cne $ProviderCommand) { throw "Provider response command mismatch for '$ProviderCommand'." }
    return $response
}

function Test-PathWithinRoot {
    param([string] $Path, [string] $Root)
    return $Path.StartsWith($Root.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Assert-NoReparseChain {
    param([string] $Path, [string] $Root)
    $current = [IO.DirectoryInfo]::new([IO.Path]::GetDirectoryName($Path))
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    while ($null -ne $current) {
        if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Guest disk path traverses a reparse point: $($current.FullName)"
        }
        if ($current.FullName.TrimEnd('\').Equals($rootFull, [StringComparison]::OrdinalIgnoreCase)) { return }
        $current = $current.Parent
    }
    throw "Guest disk path did not terminate at its approved storage root: $Path"
}

function Assert-GuestDiskIsolation {
    param([object] $Config, [object] $Inspection)
    $data = Get-RequiredProperty $Inspection 'data'
    if ((Get-RequiredProperty $data 'disposable') -ne $true) { throw 'Provider inspection did not independently mark the VM disposable.' }
    if ([string](Get-RequiredProperty $data 'firmware') -notmatch '^(UEFI|Generation2)$') { throw 'Disposable VM must use UEFI/Generation 2 firmware.' }
    if ([string](Get-RequiredProperty $data 'partitionStyle') -cne 'GPT') { throw 'Disposable VM system disk must use GPT.' }
    $disks = @((Get-RequiredProperty $data 'disks'))
    if ($disks.Count -eq 0) { throw 'Provider returned no guest disks.' }
    foreach ($disk in $disks) {
        $attachmentType = [string](Get-RequiredProperty $disk 'attachmentType')
        if ($attachmentType -cne 'File') { throw "Guest disk is not file-backed: attachmentType=$attachmentType" }
        foreach ($forbidden in 'hostDiskNumber', 'physicalDiskId', 'passThroughDiskId', 'iscsiTarget') {
            $property = $disk.PSObject.Properties[$forbidden]
            if ($null -ne $property -and -not [string]::IsNullOrWhiteSpace([string]$property.Value)) {
                throw "Guest disk exposes forbidden physical-host identity '$forbidden'."
            }
        }
        $hostPathText = [string](Get-RequiredProperty $disk 'hostPath')
        if ($hostPathText -match '^(\\\\\.\\PhysicalDrive|\\\\\?\\GLOBALROOT|\\\\\.\\|\\Device\\)') {
            throw "Guest disk resolves to a host device path: $hostPathText"
        }
        $hostPath = [IO.Path]::GetFullPath($hostPathText)
        if (-not [IO.File]::Exists($hostPath)) { throw "Guest disk image does not exist: $hostPath" }
        $item = Get-Item -LiteralPath $hostPath -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Guest disk image must not be a reparse point: $hostPath" }
        if ($script:AllowedDiskExtensions -notcontains [IO.Path]::GetExtension($hostPath).ToLowerInvariant()) { throw "Guest disk image type is not approved: $hostPath" }
        $matchingRoots = @($Config.ResolvedStorageRoots | Where-Object { Test-PathWithinRoot $hostPath $_ })
        if ($matchingRoots.Count -ne 1) {
            throw "Guest disk must resolve beneath exactly one approved VM storage root: $hostPath"
        }
        Assert-NoReparseChain $hostPath $matchingRoots[0]
    }
}

function Assert-GuestResult {
    param([object] $Response, [string] $Action)
    $data = Get-RequiredProperty $Response 'data'
    if ([int](Get-RequiredProperty $data 'exitCode') -ne 0) { throw "Guest action '$Action' returned a non-zero exit code." }
    $result = Get-RequiredProperty $data 'result'
    if ([string](Get-RequiredProperty $result 'action') -cne $Action) { throw "Guest action result mismatch for '$Action'." }
    return $result
}

function Assert-TrueProperty {
    param([object] $Object, [string] $Name, [string] $Failure)
    if ((Get-RequiredProperty $Object $Name) -ne $true) { throw $Failure }
}

function Get-RequiredSha256 {
    param([object] $Object, [string] $Name)
    $value = [string](Get-RequiredProperty $Object $Name)
    if ($value -notmatch '^[0-9A-Fa-f]{64}$') { throw "Property '$Name' is not a SHA256 value." }
    return $value.ToUpperInvariant()
}

function Get-RequiredGuid {
    param([object] $Object, [string] $Name)
    $value = [string](Get-RequiredProperty $Object $Name)
    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($value, [ref]$parsed)) { throw "Property '$Name' is not a concrete GUID." }
    return $parsed.ToString('D')
}

function Invoke-GuestAction {
    param([object] $Config, [string] $Action, [hashtable] $Extra = @{})
    $arguments = @{ action = $Action; correlationId = [Guid]::NewGuid().ToString('N'); timeoutSeconds = 1800 }
    foreach ($key in $Extra.Keys) { $arguments[$key] = $Extra[$key] }
    return Assert-GuestResult (Invoke-Provider $Config 'guest-command' $arguments) $Action
}

function Test-CheckpointRestore {
    param([object] $Config)
    $checkpoint = "cleanswitch-readiness-$([Guid]::NewGuid().ToString('N'))"
    $before = [Guid]::NewGuid().ToString('N'); $after = [Guid]::NewGuid().ToString('N'); $created = $false
    try {
        Invoke-Provider $Config 'start' @{} | Out-Null
        Invoke-GuestAction $Config 'checkpoint-probe-write' @{ value = $before } | Out-Null
        Invoke-Provider $Config 'checkpoint' @{ action = 'create'; name = $checkpoint } | Out-Null
        $created = $true
        Invoke-GuestAction $Config 'checkpoint-probe-write' @{ value = $after } | Out-Null
        Invoke-Provider $Config 'hard-poweroff' @{ reason = 'checkpoint-restore-readiness-proof' } | Out-Null
        Invoke-Provider $Config 'restore' @{ name = $checkpoint } | Out-Null
        Invoke-Provider $Config 'start' @{} | Out-Null
        $result = Invoke-GuestAction $Config 'checkpoint-probe-read'
        if ([string](Get-RequiredProperty $result 'value') -cne $before) { throw 'Checkpoint restore proof failed: guest probe was not reverted.' }
        Invoke-GuestAction $Config 'checkpoint-probe-delete' | Out-Null
        Invoke-Provider $Config 'stop' @{ graceful = $true } | Out-Null
        Invoke-Provider $Config 'checkpoint' @{ action = 'delete'; name = $checkpoint } | Out-Null
        $created = $false
        return $true
    }
    finally {
        if ($created) {
            try { Invoke-Provider $Config 'checkpoint' @{ action = 'delete'; name = $checkpoint } | Out-Null }
            catch { Write-Warning "Checkpoint cleanup requires operator attention: $checkpoint ($($_.Exception.Message))" }
        }
    }
}

function Get-ReadinessReport {
    param([object] $Config)
    $inspection = Invoke-Provider $Config 'inspect' @{}
    Assert-GuestDiskIsolation $Config $inspection
    $data = Get-RequiredProperty $inspection 'data'
    $build = [int](Get-RequiredProperty $data 'windowsBuild')
    if ($build -lt 22000) { throw "Guest is not Windows 11 compatible: build $build" }
    $observedVmGuid = Get-RequiredGuid $data 'vmGuid'
    $expectedVmGuid = ([Guid]([string](Get-RequiredProperty $Config 'vmGuid'))).ToString('D')
    if ($observedVmGuid -cne $expectedVmGuid) { throw 'Provider inspection returned the wrong VM GUID.' }
    $diskPaths = @((Get-RequiredProperty $data 'disks') | ForEach-Object {
        [IO.Path]::GetFullPath([string](Get-RequiredProperty $_ 'hostPath'))
    })
    return [ordered]@{
        Ready = $true; VmId = [string]$Config.vmId; VmGuid = $observedVmGuid
        Disposable = $true; WindowsBuild = $build
        Firmware = [string]$data.firmware; PartitionStyle = [string]$data.partitionStyle
        GuestDiskCount = @($data.disks).Count; GuestDiskPaths = $diskPaths; GuestDisksFileBacked = $true
        PhysicalHostDiskReferenceFound = $false; CheckpointRestoreProven = (Test-CheckpointRestore $Config)
        SourceCommit = [string](Get-RequiredProperty $Config 'sourceCommit')
        ConfigurationSha256 = [string]$Config.ConfigurationSha256
        RequiredProviderCommands = $script:RequiredProviderCommands
    }
}

function Invoke-Cycle {
    param([object] $Config, [int] $CycleNumber)
    $readiness = Get-ReadinessReport $Config
    $baseline = [string](Get-RequiredProperty $Config 'baselineCheckpoint')
    Invoke-Provider $Config 'hard-poweroff' @{ reason = "cycle-$CycleNumber-baseline-restore" } | Out-Null
    $restore = Invoke-Provider $Config 'restore' @{ name = $baseline }
    $restoredCheckpointGuid = Get-RequiredGuid (Get-RequiredProperty $restore 'data') 'checkpointGuid'
    $expectedCheckpointGuid = ([Guid]([string](Get-RequiredProperty $Config 'pristineCheckpointGuid'))).ToString('D')
    if ($restoredCheckpointGuid -cne $expectedCheckpointGuid) { throw 'Provider restored the wrong pristine checkpoint GUID.' }
    Invoke-Provider $Config 'start' @{} | Out-Null
    $pre = Invoke-GuestAction $Config 'pre-retirement' @{ cycle = $CycleNumber; expectedSourceCommit = $readiness.SourceCommit }
    if ([string](Get-RequiredProperty $pre 'sourceCommit') -cne $readiness.SourceCommit) { throw 'Guest source commit mismatch.' }
    foreach ($check in 'pristineLayoutVerified', 'boot1BcdLoaderPresent', 'terminalOrAbsentRetirementState', 'noUnresolvedJournal') {
        Assert-TrueProperty $pre $check "Pristine pre-retirement invariant failed: $check"
    }
    $cleanSwitchExe = Get-RequiredSha256 $pre 'cleanSwitchExecutableSha256'
    $cleanSwitchConfig = Get-RequiredSha256 $pre 'cleanSwitchConfigurationSha256'
    $originalWim = Get-RequiredSha256 $pre 'originalWimSha256'
    $preGpt = Get-RequiredSha256 $pre 'preRetirementGptFingerprint'
    $preBcd = Get-RequiredSha256 $pre 'preRetirementBcdFingerprint'
    $boot1Gpt = Get-RequiredGuid $pre 'boot1PartitionGptId'
    $boot2Gpt = Get-RequiredGuid $pre 'boot2PartitionGptId'
    $recoveryDataGpt = Get-RequiredGuid $pre 'recoveryDataPartitionGptId'
    $espGpt = Get-RequiredGuid $pre 'espPartitionGptId'
    $boot2RecoveryGpt = Get-RequiredGuid $pre 'boot2RecoveryPartitionGptId'
    $originalRecoveryGuid = Get-RequiredGuid $pre 'recoveryGuid'
    $prepared = Invoke-GuestAction $Config 'prepare-seal' @{ cycle = $CycleNumber }
    Assert-TrueProperty $prepared 'sealedBundleVerified' 'Prepared WinRE bundle was not sealed and verified.'
    $preparedWim = Get-RequiredSha256 $prepared 'preparedWimSha256'
    $preparedBundle = Get-RequiredSha256 $prepared 'preparedBundleSha256'
    $deployed = Invoke-GuestAction $Config 'deploy' @{ cycle = $CycleNumber }
    if ([string](Get-RequiredProperty $deployed 'deploymentJournalStage') -cne 'AwaitingSmoke') {
        throw 'WinRE deployment did not stop at the exact independently reviewable AwaitingSmoke boundary.'
    }
    $deploymentTransactionId = [string](Get-RequiredProperty $deployed 'deploymentTransactionId')
    $parsedTransaction = [Guid]::Empty
    if (-not [Guid]::TryParseExact($deploymentTransactionId, 'N', [ref]$parsedTransaction)) {
        throw 'WinRE deployment returned a malformed transaction id.'
    }
    $deployedWim = Get-RequiredSha256 $deployed 'deployedWimSha256'
    if ($preparedWim -cne $deployedWim) { throw 'Deployed WinRE hash does not equal the sealed prepared WIM hash.' }
    $deployedRecoveryGuid = Get-RequiredGuid $deployed 'recoveryGuid'
    if ($deployedRecoveryGuid -cne $originalRecoveryGuid) { throw 'WinRE deployment changed RecoveryGuid.' }
    $review = Invoke-GuestAction $Config 'review' @{ cycle = $CycleNumber }
    Assert-TrueProperty $review 'passed' 'Post-deployment launcher review failed.'
    $launcherContract = Get-RequiredSha256 $review 'launcherContractSha256'
    $reviewRecoveryGuid = Get-RequiredGuid $review 'recoveryGuid'
    if ($reviewRecoveryGuid -cne $originalRecoveryGuid) { throw 'Launcher review RecoveryGuid differs from the pristine identity.' }

    $committed = Invoke-GuestAction $Config 'commit-winre-deployment' @{
        cycle = $CycleNumber; deploymentTransactionId = $deploymentTransactionId
    }
    Assert-TrueProperty $committed 'deploymentJournalTerminal' 'Explicit WinRE deployment commit did not produce a terminal journal.'
    Assert-TrueProperty $committed 'noUnresolvedJournal' 'Explicit WinRE deployment commit left an unresolved journal.'
    if ([string](Get-RequiredProperty $committed 'deploymentJournalResult') -cne 'COMMITTED') {
        throw 'Explicit WinRE deployment commit did not finish COMMITTED.'
    }
    if ([string](Get-RequiredProperty $committed 'deploymentTransactionId') -cne $deploymentTransactionId) {
        throw 'Explicit WinRE deployment commit returned the wrong transaction id.'
    }
    if ((Get-RequiredSha256 $committed 'installedWimSha256') -cne $deployedWim -or
        (Get-RequiredGuid $committed 'recoveryGuid') -cne $originalRecoveryGuid) {
        throw 'Explicit WinRE deployment commit changed the installed WIM or RecoveryGuid identity.'
    }
    $deploymentJournal = Get-RequiredSha256 $committed 'deploymentJournalSha256'

    $handoff = Invoke-GuestAction $Config 'start-retirement' @{ cycle = $CycleNumber }
    foreach ($check in 'acceptedByProduct', 'phase2AHandoffCommitted', 'boot2PersistentDefault', 'boot2RecoveryOneShotArmed') {
        Assert-TrueProperty $handoff $check "Phase2A retirement handoff invariant failed: $check"
    }
    if ([string](Get-RequiredProperty $handoff 'productPath') -cne 'RETIRE SYSTEM') {
        throw 'Retirement was not initiated through the product-supported RETIRE SYSTEM path.'
    }
    if ([string](Get-RequiredProperty $handoff 'handoffAuthorizationState') -cne 'COMMITTED') {
        throw 'Phase2A handoff authorization is not COMMITTED.'
    }
    $phase2AState = Get-RequiredSha256 $handoff 'phase2AStateSha256'
    $handoffBinding = Get-RequiredSha256 $handoff 'handoffBindingSha256'

    $wait = Invoke-Provider $Config 'wait-for-guest' @{ cycle = $CycleNumber; expectedWindowsRole = 'Boot2'; timeoutSeconds = 1800 }
    $waitData = Get-RequiredProperty $wait 'data'
    Assert-TrueProperty $waitData 'booted' 'Boot2 Windows did not return after retirement.'
    if ([string](Get-RequiredProperty $waitData 'windowsRole') -cne 'Boot2') { throw 'The post-retirement guest is not Boot2.' }

    $post = Invoke-GuestAction $Config 'verify-retirement' @{ cycle = $CycleNumber }
    foreach ($check in 'winpeshlRecoveryLaunchObserved', 'dispatcherSelectedRetirement',
        'recoveryRunnerCompleted', 'phase2BFreshValidationPassed', 'boot1PartitionAbsent',
        'boot1BcdLoaderAbsent', 'boot2PartitionPresent', 'boot2LoaderPersistentDefault',
        'recoveryDataPresent', 'espPresent', 'boot2RecoveryPartitionPresent', 'boot2WinRePresent',
        'retirementStateComplete', 'completeStartupRecorded', 'noUnresolvedJournal',
        'postRetirementReAgentCValid') {
        Assert-TrueProperty $post $check "End-to-end retirement invariant failed: $check"
    }
    if ([int](Get-RequiredProperty $post 'destructiveDeletionCount') -ne 1) {
        throw 'Boot1 destructive deletion must execute exactly once.'
    }
    $bcdDeleteCount = [int](Get-RequiredProperty $post 'bcdDeletionCount')
    if ($bcdDeleteCount -lt 0 -or $bcdDeleteCount -gt 1) { throw 'Boot1 BCD deletion may execute at most once.' }
    if ([string](Get-RequiredProperty $post 'retirementStateStatus') -cne 'COMPLETE') {
        throw 'Final retirement state is not COMPLETE.'
    }
    foreach ($identity in @{
        boot1PartitionGptId = $boot1Gpt; boot2PartitionGptId = $boot2Gpt
        recoveryDataPartitionGptId = $recoveryDataGpt; espPartitionGptId = $espGpt
        boot2RecoveryPartitionGptId = $boot2RecoveryGpt; recoveryGuid = $originalRecoveryGuid
    }.GetEnumerator()) {
        if ((Get-RequiredGuid $post $identity.Key) -cne $identity.Value) {
            throw "Post-retirement identity mismatch: $($identity.Key)"
        }
    }
    $postGpt = Get-RequiredSha256 $post 'postDeleteGptFingerprint'
    $finalBcd = Get-RequiredSha256 $post 'finalBcdFingerprint'
    if ($postGpt -ceq $preGpt) { throw 'GPT fingerprint did not change after the verified Boot1 deletion.' }
    if ($finalBcd -ceq $preBcd) { throw 'BCD fingerprint did not change after verified Boot1 loader removal.' }
    $recoveryLog = Get-RequiredSha256 $post 'recoveryRunnerLogSha256'
    $finalState = Get-RequiredSha256 $post 'finalRetirementStateSha256'
    $reAgentC = [string](Get-RequiredProperty $post 'postRetirementReAgentCStatus')
    $resolvedBoot1 = Get-RequiredProperty $post 'resolvedBoot1Identity'
    if ((Get-RequiredGuid $resolvedBoot1 'partitionGptId') -cne $boot1Gpt -or
        [int](Get-RequiredProperty $resolvedBoot1 'diskNumber') -lt 0 -or
        [int](Get-RequiredProperty $resolvedBoot1 'partitionNumber') -lt 1) {
        throw 'Resolved destructive Boot1 identity is incomplete or does not match the pristine Boot1 GPT.'
    }

    $artifactDestination = Join-Path ([IO.Path]::GetFullPath([string](Get-RequiredProperty $Config 'artifactRoot'))) "cycle-$CycleNumber"
    $artifacts = Invoke-Provider $Config 'collect-artifacts' @{ cycle = $CycleNumber; destination = $artifactDestination }
    $artifactData = Get-RequiredProperty $artifacts 'data'
    $artifactManifest = [IO.Path]::GetFullPath([string](Get-RequiredProperty $artifactData 'manifestPath'))
    $artifactManifestSha256 = Get-RequiredSha256 $artifactData 'manifestSha256'
    $artifactRoot = [IO.Path]::GetFullPath($artifactDestination).TrimEnd('\')
    if (-not $artifactManifest.StartsWith($artifactRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.File]::Exists($artifactManifest) -or
        (Get-Sha256 $artifactManifest) -cne $artifactManifestSha256) {
        throw 'Artifact manifest is missing, outside the cycle destination, or its SHA256 does not match.'
    }
    Invoke-Provider $Config 'stop' @{ graceful = $true } | Out-Null
    Invoke-Provider $Config 'hard-poweroff' @{ reason = "cycle-$CycleNumber-post-evidence-reset" } | Out-Null
    $reset = Invoke-Provider $Config 'restore' @{ name = $baseline }
    if ((Get-RequiredGuid (Get-RequiredProperty $reset 'data') 'checkpointGuid') -cne $expectedCheckpointGuid) {
        throw 'Post-evidence reset restored the wrong pristine checkpoint GUID.'
    }
    return [ordered]@{
        Passed = $true; Cycle = $CycleNumber; SourceCommit = $readiness.SourceCommit
        VmGuid = $readiness.VmGuid; PristineCheckpointGuid = $restoredCheckpointGuid
        GuestDiskPaths = $readiness.GuestDiskPaths; GuestDisksFileBacked = $true
        CleanSwitchExecutableSha256 = $cleanSwitchExe; CleanSwitchConfigurationSha256 = $cleanSwitchConfig
        OriginalWimSha256 = $originalWim; PreparedWimSha256 = $preparedWim
        PreparedBundleSha256 = $preparedBundle; DeployedWimSha256 = $deployedWim
        DeploymentTransactionId = $deploymentTransactionId; DeploymentJournalSha256 = $deploymentJournal
        LauncherContractSha256 = $launcherContract; RecoveryGuid = $originalRecoveryGuid
        PreRetirementGptFingerprint = $preGpt; PreRetirementBcdFingerprint = $preBcd
        Phase2AStateSha256 = $phase2AState; HandoffBindingSha256 = $handoffBinding
        RecoveryRunnerLogSha256 = $recoveryLog
        ResolvedBoot1Identity = $resolvedBoot1; DestructiveDeletionCount = 1
        BcdDeletionCount = $bcdDeleteCount; PostDeleteGptFingerprint = $postGpt
        FinalBcdFingerprint = $finalBcd; FinalRetirementStateSha256 = $finalState
        RetirementStateStatus = 'COMPLETE'; PostRetirementReAgentCStatus = $reAgentC
        NoUnresolvedJournal = $true; ArtifactManifest = $artifactManifest
        ArtifactManifestSha256 = $artifactManifestSha256; CheckpointRestoredAfterEvidence = $true
    }
}

try {
    $config = Get-HarnessConfiguration
    if ($PSCmdlet.ParameterSetName -eq 'Cycle') {
        $result = Invoke-Cycle $config $Cycle; Write-DurableJson $ResultPath $result
        $result | ConvertTo-Json -Depth 20; exit 0
    }
    if ($PSCmdlet.ParameterSetName -eq 'Command') {
        # A direct provider operation receives the same safety envelope as a cycle.
        # This intentionally exercises checkpoint restore before forwarding it.
        [void](Get-ReadinessReport $config)
        try { $arguments = $ArgumentsJson | ConvertFrom-Json } catch { throw 'ArgumentsJson is not valid JSON.' }
        $response = Invoke-Provider $config $Command (ConvertTo-Hashtable $arguments)
        if (-not [string]::IsNullOrWhiteSpace($ResultPath)) { Write-DurableJson $ResultPath $response }
        $response | ConvertTo-Json -Depth 20; exit 0
    }
    $report = Get-ReadinessReport $config
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) { Write-DurableJson $ResultPath $report }
    $report | ConvertTo-Json -Depth 20; exit 0
}
catch {
    $failure = [ordered]@{ Ready = $false; Error = $_.Exception.Message; ParameterSet = $PSCmdlet.ParameterSetName; Utc = [DateTimeOffset]::UtcNow.ToString('O') }
    if (-not [string]::IsNullOrWhiteSpace($ResultPath)) { try { Write-DurableJson $ResultPath $failure } catch { } }
    [Console]::Error.WriteLine(($failure | ConvertTo-Json -Depth 10 -Compress)); exit 1
}
