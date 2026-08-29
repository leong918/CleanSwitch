using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Decides whether a partition may be treated as the retirement target.
/// <para>
/// Phase 2A uses this in report-only mode: it records what it can identify and never
/// authorises anything. Phase 2B adds the partition-table lookup that fills in disk
/// number, partition number and GPT partition id.
/// </para>
/// <para>
/// This class runs no disk-modifying commands. It never calls diskpart, Format-Volume,
/// Remove-Partition, Clear-Disk or mountvol. The only OS calls it makes are the read-only
/// Win32 volume mount-point queries in <see cref="VolumeIdentity"/>.
/// </para>
/// </summary>
public sealed class DiskValidator
{
    private readonly IOperationLog _log;

    public DiskValidator(IOperationLog? log = null)
    {
        _log = log ?? NullOperationLog.Instance;
    }

    /// <summary>Identity of the volume the currently running Windows boots from.</summary>
    public PartitionIdentity DescribeRunningSystemVolume()
    {
        var identity = new PartitionIdentity
        {
            VolumeGuidPath = VolumeIdentity.TryGetRunningSystemVolumeGuidPath(),
            ObservedDriveLetter = VolumeIdentity.TryGetVolumeMountPoint(Environment.SystemDirectory),
            Source = "Running system volume (Win32 GetVolumeNameForVolumeMountPoint)"
        };

        _log.Info("disk-validator", $"Running system volume: {identity.Describe()}");
        return identity;
    }

    /// <summary>
    /// Resolves a path to a volume identity. Only the volume GUID is filled in; disk and
    /// partition numbers require the Phase 2B partition-table lookup.
    /// </summary>
    public PartitionIdentity? TryDescribeVolumeForPath(string path, string source)
    {
        var volumeGuidPath = VolumeIdentity.TryGetVolumeGuidPath(path);
        if (volumeGuidPath is null)
        {
            _log.Warn("disk-validator", $"Could not resolve a volume for path '{path}'.");
            return null;
        }

        var identity = new PartitionIdentity
        {
            VolumeGuidPath = volumeGuidPath,
            ObservedDriveLetter = VolumeIdentity.TryGetVolumeMountPoint(path),
            Source = source
        };

        _log.Info("disk-validator", $"Resolved '{path}' to {identity.Describe()}");
        return identity;
    }

    /// <summary>
    /// Phase 2A report-only pass. Records what is known about the retirement target and
    /// always reports that no destructive authorisation was granted.
    /// </summary>
    public ValidationReport ReportRetirementTarget(PartitionIdentity? target)
    {
        var report = new ValidationReport("Retirement target (report only)");

        if (target is null)
        {
            report.Fail(
                "target-identity-present",
                "No identity information is available for the Boot 1 volume. Phase 2B must populate disk " +
                "number, partition number, volume GUID and GPT partition id before any deletion.");
        }
        else
        {
            report.Pass("target-identity-present", $"Recorded target identity: {target.Describe()}");
            report.Add(
                "target-identity-stable",
                target.HasStableIdentifiers,
                target.HasStableIdentifiers
                    ? $"{target.StableIdentifierCount} independent stable identifiers recorded."
                    : $"Only {target.StableIdentifierCount} stable identifier(s) recorded. Phase 2B requires at " +
                      "least two of (disk+partition number, volume GUID, GPT partition id).");
        }

        report.Pass(
            "phase-2a-no-authorisation",
            "Phase 2A is report only. No partition was validated for deletion and no deletion was authorised.");

        LogReport(report);
        return report;
    }

    /// <summary>
    /// Phase 2B gate. Every check must pass before <see cref="RetirementExecutor"/> may be
    /// given a target. Identity matching uses only reboot-stable identifiers; drive letters
    /// are ignored because WinRE reassigns them.
    /// </summary>
    public ValidationReport ValidateRetirementTarget(
        PartitionIdentity? expected,
        PartitionIdentity? observed)
    {
        var report = new ValidationReport("Retirement target (destructive gate)");

        if (expected is null || observed is null)
        {
            report.Fail(
                "identities-present",
                "Both the expected identity (recorded on Boot 1) and the observed identity (read in recovery) " +
                "are required.");
            LogReport(report);
            return report;
        }

        report.Pass("identities-present", $"expected=[{expected.Describe()}] observed=[{observed.Describe()}]");

        report.Add(
            "expected-identity-stable",
            expected.HasStableIdentifiers,
            expected.HasStableIdentifiers
                ? $"Expected identity carries {expected.StableIdentifierCount} stable identifiers."
                : "Expected identity does not carry two independent stable identifiers.");

        report.Add(
            "observed-identity-stable",
            observed.HasStableIdentifiers,
            observed.HasStableIdentifiers
                ? $"Observed identity carries {observed.StableIdentifierCount} stable identifiers."
                : "Observed identity does not carry two independent stable identifiers.");

        var matches = 0;

        if (expected.DiskNumber is not null && expected.PartitionNumber is not null &&
            observed.DiskNumber is not null && observed.PartitionNumber is not null)
        {
            var same = expected.DiskNumber == observed.DiskNumber &&
                       expected.PartitionNumber == observed.PartitionNumber;
            report.Add(
                "disk-partition-number-match",
                same,
                $"expected disk {expected.DiskNumber}/partition {expected.PartitionNumber} vs " +
                $"observed disk {observed.DiskNumber}/partition {observed.PartitionNumber}.");
            if (same)
            {
                matches++;
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.VolumeGuidPath) &&
            !string.IsNullOrWhiteSpace(observed.VolumeGuidPath))
        {
            var same = VolumeIdentity.AreSameVolume(expected.VolumeGuidPath, observed.VolumeGuidPath);
            report.Add(
                "volume-guid-match",
                same,
                $"expected {expected.VolumeGuidPath} vs observed {observed.VolumeGuidPath}.");
            if (same)
            {
                matches++;
            }
        }

        if (!string.IsNullOrWhiteSpace(expected.GptPartitionId) &&
            !string.IsNullOrWhiteSpace(observed.GptPartitionId))
        {
            var same = string.Equals(
                expected.GptPartitionId.Trim(),
                observed.GptPartitionId.Trim(),
                StringComparison.OrdinalIgnoreCase);
            report.Add("gpt-partition-id-match", same, $"expected {expected.GptPartitionId} vs observed {observed.GptPartitionId}.");
            if (same)
            {
                matches++;
            }
        }

        report.Add(
            "independent-identifier-agreement",
            matches >= 2,
            $"{matches} independent identifier(s) agreed. At least 2 are required.");

        var runningSystemVolume = VolumeIdentity.TryGetRunningSystemVolumeGuidPath();
        var isRunningVolume = VolumeIdentity.AreSameVolume(observed.VolumeGuidPath, runningSystemVolume);
        report.Add(
            "target-is-not-running-system",
            !isRunningVolume,
            isRunningVolume
                ? $"The target volume {observed.VolumeGuidPath} is the volume this process is running from. " +
                  "Refusing under all circumstances."
                : $"Target volume differs from the running system volume ({runningSystemVolume ?? "unknown"}).");

        LogReport(report);
        return report;
    }

    private void LogReport(ValidationReport report)
    {
        foreach (var check in report.Checks)
        {
            _log.Write(
                check.Passed ? OperationLogLevel.Info : OperationLogLevel.Warning,
                "disk-validator",
                $"{report.Subject} | {check}");
        }
    }
}
