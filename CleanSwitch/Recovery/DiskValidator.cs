using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>Well-known GPT partition type GUIDs. Used to refuse protected targets.</summary>
public static class GptPartitionTypes
{
    public static readonly Guid EfiSystem = Guid.Parse("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");
    public static readonly Guid MicrosoftReserved = Guid.Parse("e3c9e316-0b5c-4db8-817d-f92df00215ae");
    public static readonly Guid BasicData = Guid.Parse("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");
    public static readonly Guid MicrosoftRecovery = Guid.Parse("de94bba4-06d1-4d40-a16a-bfd50179d6ac");

    public static string Describe(Guid? type)
    {
        if (type is null)
        {
            return "(unknown type)";
        }

        if (type == EfiSystem)
        {
            return "EFI System Partition";
        }

        if (type == MicrosoftReserved)
        {
            return "Microsoft Reserved";
        }

        if (type == BasicData)
        {
            return "Basic Data";
        }

        if (type == MicrosoftRecovery)
        {
            return "Windows Recovery";
        }

        return VolumeLocator.FormatGptId(type.Value);
    }

    public static bool TryParse(string? raw, out Guid type)
    {
        type = Guid.Empty;
        return !string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw.Trim(), out type);
    }
}

/// <summary>
/// Decides whether a partition may be treated as the retirement target.
/// Identification is a hard gate. Deletion is still not authorised by this class.
/// </summary>
public sealed class DiskValidator
{
    private readonly IOperationLog _log;
    private readonly IGptLayoutSource? _gptObservationSource;

    public DiskValidator(IOperationLog? log = null, IGptLayoutSource? gptObservationSource = null)
    {
        _log = log ?? NullOperationLog.Instance;
        _gptObservationSource = gptObservationSource;
    }

    /// <summary>Identity of the volume the currently running Windows boots from.</summary>
    public PartitionIdentity DescribeRunningSystemVolume()
    {
        var located = VolumeLocator.Enumerate();
        var running = located.Volumes.FirstOrDefault(volume => volume.IsRunningSystemVolume);
        if (running is not null)
        {
            var identity = running.ToPartitionIdentity(
                "Running system volume (partition table + Win32 current-directory volume)");
            _log.Info("disk-validator", $"Running system volume: {identity.Describe()}");
            return identity;
        }

        var fallback = new PartitionIdentity
        {
            VolumeGuidPath = VolumeIdentity.TryGetRunningSystemVolumeGuidPath(),
            ObservedDriveLetter = VolumeIdentity.TryGetVolumeMountPoint(Environment.SystemDirectory),
            Source = "Running system volume (Win32 GetVolumeNameForVolumeMountPoint only)"
        };
        _log.Info("disk-validator", $"Running system volume: {fallback.Describe()}");
        return fallback;
    }

    /// <summary>
    /// Looks up a volume by the GPT unique partition GUID recorded on Boot 1.
    /// Drive letters are not used. Ambiguous matches fail.
    /// </summary>
    public PartitionIdentity? TryObserveByGptId(string? gptPartitionId, string source, out string? error)
    {
        error = null;
        if (!VolumeLocator.TryParseGptId(gptPartitionId, out var gptId))
        {
            error = $"'{gptPartitionId}' is not a GPT partition GUID.";
            return null;
        }

        if (_gptObservationSource is not null)
        {
            var matches = _gptObservationSource.Capture().WithGptId(gptId);
            if (matches.Count != 1)
            {
                error = matches.Count == 0
                    ? $"No partition in the injected GPT layout has GPT partition GUID {VolumeLocator.FormatGptId(gptId)}."
                    : $"GPT partition GUID {VolumeLocator.FormatGptId(gptId)} matched {matches.Count} partitions in the injected GPT layout. Refusing to choose.";
                return null;
            }

            var partition = matches[0];
            var injectedIdentity = new PartitionIdentity
            {
                DiskNumber = partition.DiskNumber,
                PartitionNumber = partition.PartitionNumber,
                GptPartitionId = VolumeLocator.FormatGptId(partition.PartitionGptId),
                GptPartitionType = partition.PartitionType is null
                    ? null
                    : VolumeLocator.FormatGptId(partition.PartitionType.Value),
                DiskGptUniqueId = partition.DiskGptId is null
                    ? null
                    : VolumeLocator.FormatGptId(partition.DiskGptId.Value),
                PartitionStartingOffset = partition.StartingOffset,
                PartitionSizeBytes = partition.SizeBytes,
                ObservedDriveLetter = partition.MountPoint,
                Source = source
            };
            _log.Info("disk-validator", $"Observed {injectedIdentity.Describe()}");
            return injectedIdentity;
        }

        var volume = VolumeLocator.TryFindUniqueByGptId(gptId, out error);
        if (volume is null)
        {
            return null;
        }

        var identity = volume.ToPartitionIdentity(source);
        _log.Info("disk-validator", $"Observed {identity.Describe()}");
        return identity;
    }

    /// <summary>
    /// Hard gate for Phase 2B identification. Every check must pass before the recovery
    /// run may continue. Passing this gate does NOT authorise deletion.
    /// </summary>
    public ValidationReport ValidateRetirementTarget(
        PartitionIdentity? expected,
        PartitionIdentity? observed,
        PartitionIdentity? boot2Identity)
    {
        var report = new ValidationReport("Retirement target (identification gate)");

        if (expected is null || observed is null)
        {
            report.Fail(
                "identities-present",
                "Both the expected identity (recorded on Boot 1) and the observed identity (read in recovery " +
                "by GPT GUID) are required.");
            LogReport(report);
            return report;
        }

        report.Pass("identities-present", $"expected=[{expected.Describe()}] observed=[{observed.Describe()}]");

        report.Add(
            "expected-identity-stable",
            expected.HasStableIdentifiers,
            expected.HasStableIdentifiers
                ? $"Expected identity carries {expected.StableIdentifierCount} reboot-stable identifiers " +
                  "(disk+partition and GPT unique id)."
                : "Expected identity does not carry both disk+partition number and GPT unique partition id.");

        report.Add(
            "observed-identity-stable",
            observed.HasStableIdentifiers,
            observed.HasStableIdentifiers
                ? $"Observed identity carries {observed.StableIdentifierCount} reboot-stable identifiers."
                : "Observed identity does not carry both disk+partition number and GPT unique partition id.");

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
        else
        {
            report.Fail(
                "disk-partition-number-match",
                "Disk number and partition number are required on both expected and observed identities.");
        }

        if (!string.IsNullOrWhiteSpace(expected.GptPartitionId) &&
            !string.IsNullOrWhiteSpace(observed.GptPartitionId))
        {
            var same = string.Equals(
                expected.GptPartitionId.Trim(),
                observed.GptPartitionId.Trim(),
                StringComparison.OrdinalIgnoreCase);
            report.Add(
                "gpt-partition-id-match",
                same,
                $"expected {expected.GptPartitionId} vs observed {observed.GptPartitionId}.");
            if (same)
            {
                matches++;
            }
        }
        else
        {
            report.Fail(
                "gpt-partition-id-match",
                "GPT unique partition id is required on both expected and observed identities.");
        }

        report.Add(
            "independent-identifier-agreement",
            matches >= 2,
            $"{matches} independent reboot-stable identifier(s) agreed. Disk+partition and GPT unique id " +
            "must both agree. Win32 volume GUIDs are ignored because WinPE assigns new ones.");

        var running = DescribeRunningSystemVolume();
        var isRunningVolume = GptIdsEqual(observed.GptPartitionId, running.GptPartitionId) ||
                              (string.IsNullOrWhiteSpace(observed.GptPartitionId) &&
                               VolumeIdentity.AreSameVolume(observed.VolumeGuidPath, running.VolumeGuidPath));
        report.Add(
            "target-is-not-running-system",
            !isRunningVolume,
            isRunningVolume
                ? "The observed target is the volume this process is running from. Refusing under all circumstances."
                : "Observed target is not the running system volume.");

        GptPartitionTypes.TryParse(observed.GptPartitionType, out var observedType);
        var typeKnown = observed.GptPartitionType is not null && observedType != Guid.Empty;
        report.Add(
            "target-gpt-type-known",
            typeKnown,
            typeKnown
                ? $"Observed GPT type is {GptPartitionTypes.Describe(observedType)}."
                : "Observed GPT partition type is unknown, so the target cannot be proven not to be ESP or Recovery.");

        report.Add(
            "target-is-not-esp",
            observedType != GptPartitionTypes.EfiSystem,
            observedType == GptPartitionTypes.EfiSystem
                ? "Target is the EFI System Partition. Refusing."
                : "Target is not the EFI System Partition.");

        report.Add(
            "target-is-not-msr",
            observedType != GptPartitionTypes.MicrosoftReserved,
            observedType == GptPartitionTypes.MicrosoftReserved
                ? "Target is the Microsoft Reserved partition. Refusing."
                : "Target is not Microsoft Reserved.");

        report.Add(
            "target-is-not-recovery-partition",
            observedType != GptPartitionTypes.MicrosoftRecovery,
            observedType == GptPartitionTypes.MicrosoftRecovery
                ? "Target is a Windows Recovery partition. Refusing."
                : "Target is not a Windows Recovery partition.");

        report.Add(
            "target-is-basic-data",
            observedType == GptPartitionTypes.BasicData,
            observedType == GptPartitionTypes.BasicData
                ? "Target GPT type is Basic Data (an installed Windows volume)."
                : $"Target GPT type is {GptPartitionTypes.Describe(observedType)}, not Basic Data. Refusing.");

        if (boot2Identity?.GptPartitionId is not null)
        {
            var sameAsBoot2 = GptIdsEqual(observed.GptPartitionId, boot2Identity.GptPartitionId);
            report.Add(
                "target-is-not-boot2",
                !sameAsBoot2,
                sameAsBoot2
                    ? $"Target GPT id {observed.GptPartitionId} is Boot 2 ({boot2Identity.GptPartitionId}). Refusing."
                    : $"Target GPT id differs from Boot 2 ({boot2Identity.GptPartitionId}).");
        }
        else
        {
            report.Fail(
                "target-is-not-boot2",
                "Boot 2 identity was not recorded at PENDING time, so the target cannot be proven distinct from Boot 2.");
        }

        report.Pass(
            "deletion-not-authorised",
            "Identification passed or failed independently of deletion. Deletion remains NOT IMPLEMENTED.");

        LogReport(report);
        return report;
    }

    private static bool GptIdsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return VolumeLocator.TryParseGptId(left, out var leftGuid) &&
               VolumeLocator.TryParseGptId(right, out var rightGuid) &&
               leftGuid == rightGuid;
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
