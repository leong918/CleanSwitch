using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>Result of resolving the WinRE boot entry.</summary>
public sealed record RecoveryEntryResolution(string? Identifier, BcdEntry? Entry, ValidationReport Report);

/// <summary>
/// Validates BCD entries used by the retirement flow: the WinRE entry the handoff boots
/// into, and the Boot 2 entry the PC must land on afterwards.
/// <para>
/// This is the genuinely new BCD work: recovery entries are not part of the switchable
/// OSLOADER set that <see cref="WindowsBootManager.DetectAsync"/> returns, because that
/// method deliberately filters out anything described as Recovery. Here we look at the
/// full <c>bcdedit /enum all /v</c> object graph instead.
/// </para>
/// </summary>
public sealed class BootEntryValidator
{
    private readonly IBootManager _bootManager;
    private readonly IOperationLog _log;

    public BootEntryValidator(IBootManager bootManager, IOperationLog? log = null)
    {
        _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
        _log = log ?? NullOperationLog.Instance;
    }

    /// <summary>
    /// Validates the configured recovery GUID, or discovers one from the current loader's
    /// <c>recoverysequence</c> when none is configured.
    /// </summary>
    public async Task<RecoveryEntryResolution> ResolveRecoveryEntryAsync(string? configuredGuid)
    {
        var report = new ValidationReport("Recovery BCD entry");

        if (string.IsNullOrWhiteSpace(configuredGuid))
        {
            var discovered = await TryDiscoverRecoveryGuidAsync();
            if (discovered is null)
            {
                report.Fail(
                    "recovery-guid-configured",
                    "CleanSwitch:RecoveryGuid is not set and no recoverysequence was found for the running " +
                    "Windows entry. Find the Windows Recovery Environment identifier with " +
                    "'bcdedit /enum all /v' from an elevated prompt and set CleanSwitch:RecoveryGuid.");
                LogReport(report);
                return new RecoveryEntryResolution(null, null, report);
            }

            report.Pass(
                "recovery-guid-configured",
                $"CleanSwitch:RecoveryGuid was empty; discovered {discovered} from the running entry's " +
                "recoverysequence.");
            configuredGuid = discovered;
        }
        else
        {
            report.Pass("recovery-guid-configured", $"Using configured CleanSwitch:RecoveryGuid {configuredGuid}.");
        }

        if (!Guid.TryParse(configuredGuid.Trim(), out var parsed))
        {
            report.Fail(
                "recovery-guid-format",
                $"'{configuredGuid}' is not a BCD GUID. Expected a value like {{xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx}}. " +
                "Aliases such as {current} or {bootmgr} are rejected on purpose.");
            LogReport(report);
            return new RecoveryEntryResolution(null, null, report);
        }

        var identifier = $"{{{parsed:D}}}";
        report.Pass("recovery-guid-format", $"Identifier normalised to {identifier}.");

        var entry = await _bootManager.TryGetEntryAsync(identifier);
        if (entry is null)
        {
            report.Fail(
                "recovery-entry-exists",
                $"BCDEdit does not know an entry with identifier {identifier}. " +
                "Re-check it with 'bcdedit /enum all /v'. No boot change will be made.");
            LogReport(report);
            return new RecoveryEntryResolution(null, null, report);
        }

        report.Pass("recovery-entry-exists", $"BCDEdit resolved {entry.Describe()}.");

        if (entry.IsResumeLoader)
        {
            report.Fail(
                "recovery-entry-kind",
                $"{identifier} is a hibernation resume loader (path {entry.Path}), not the recovery environment.");
        }
        else if (entry.LooksLikeRecoveryEnvironment)
        {
            report.Pass(
                "recovery-entry-kind",
                $"Entry looks like the Windows Recovery Environment (description '{entry.Description}', " +
                $"device '{entry.Device}').");
        }
        else
        {
            report.Fail(
                "recovery-entry-kind",
                $"{identifier} does not look like a WinRE entry: description '{entry.Description}', " +
                $"device '{entry.Device}', path '{entry.Path}'. A WinRE entry normally boots a ramdisk-backed " +
                "winre.wim. Set CleanSwitch:RecoveryGuid to the 'Windows Recovery Environment' identifier.");
        }

        LogReport(report);
        return new RecoveryEntryResolution(report.Passed ? identifier : null, entry, report);
    }

    /// <summary>
    /// Validates that the recorded Boot 2 entry is a real, distinct Windows loader before
    /// the recovery side hands control to it.
    /// </summary>
    public async Task<ValidationReport> ValidateBoot2EntryAsync(string boot2Guid, string boot1Guid)
    {
        var report = new ValidationReport("Boot 2 BCD entry");

        if (!Guid.TryParse((boot2Guid ?? string.Empty).Trim(), out var parsedBoot2))
        {
            report.Fail("boot2-guid-format", $"'{boot2Guid}' is not a BCD GUID.");
            LogReport(report);
            return report;
        }

        var identifier = $"{{{parsedBoot2:D}}}";
        report.Pass("boot2-guid-format", $"Identifier normalised to {identifier}.");

        if (string.Equals(identifier, (boot1Guid ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            report.Fail(
                "boot2-distinct-from-boot1",
                $"Boot 2 ({identifier}) is the same entry as Boot 1. Refusing to continue.");
            LogReport(report);
            return report;
        }

        report.Pass("boot2-distinct-from-boot1", $"Boot 2 {identifier} differs from Boot 1 {boot1Guid}.");

        var entry = await _bootManager.TryGetEntryAsync(identifier);
        if (entry is null)
        {
            report.Fail(
                "boot2-entry-exists",
                $"BCDEdit does not know an entry with identifier {identifier}. The PC must not be pointed at a " +
                "boot entry that does not exist.");
            LogReport(report);
            return report;
        }

        report.Pass("boot2-entry-exists", $"BCDEdit resolved {entry.Describe()}.");

        report.Add(
            "boot2-is-windows-loader",
            entry.IsWindowsLoader && !entry.IsResumeLoader,
            entry.IsWindowsLoader && !entry.IsResumeLoader
                ? $"Entry loads Windows via '{entry.Path}'."
                : $"Entry path '{entry.Path}' is not a winload Windows loader.");

        report.Add(
            "boot2-not-recovery",
            !entry.LooksLikeRecoveryEnvironment,
            entry.LooksLikeRecoveryEnvironment
                ? $"Entry looks like a recovery environment (device '{entry.Device}'), not an installed Windows."
                : "Entry is not a recovery environment.");

        var device = string.IsNullOrWhiteSpace(entry.OsDevice) ? entry.Device : entry.OsDevice;
        report.Add(
            "boot2-device-known",
            !string.IsNullOrWhiteSpace(device) &&
            !device.Contains("unknown", StringComparison.OrdinalIgnoreCase),
            string.IsNullOrWhiteSpace(device)
                ? "Entry has no device/osdevice information."
                : $"Entry device information: '{device}'.");

        LogReport(report);
        return report;
    }

    /// <summary>
    /// Describes a boot entry as stable identifiers. The BCD <c>device</c> text is always
    /// recorded; when it carries a <c>partition=X:</c> letter that resolves in the current
    /// environment, the partition table is also read so disk number, partition number and
    /// GPT partition GUID are captured.
    /// <para>
    /// This is metadata only, and Phase 2A acts on none of it. The letter inside the BCD
    /// device text is only meaningful in the environment that entry belongs to, so the
    /// recorded <c>Source</c> says which environment resolved it. Phase 2B must re-verify
    /// before trusting it.
    /// </para>
    /// </summary>
    public async Task<PartitionIdentity?> TryDescribeBootEntryVolumeAsync(string bootGuid)
    {
        var entry = await _bootManager.TryGetEntryAsync(bootGuid);
        if (entry is null)
        {
            _log.Warn("boot-validator", $"Cannot describe volume for unknown BCD entry {bootGuid}.");
            return null;
        }

        var device = string.IsNullOrWhiteSpace(entry.OsDevice) ? entry.Device : entry.OsDevice;

        var identity = new PartitionIdentity
        {
            BcdDevice = device,
            Source = $"BCD entry {entry.Identifier}"
        };

        EnrichFromPartitionTable(identity, entry.Identifier, device);

        _log.Info("boot-validator", $"BCD device information for {entry.Identifier}: {identity.Describe()}");
        return identity;
    }

    /// <summary>
    /// Resolves a BCD <c>partition=X:</c> device to the volume that letter currently means,
    /// then to that volume's GPT partition identity. Silent no-op when the device text has no
    /// letter or the letter does not resolve here.
    /// </summary>
    private void EnrichFromPartitionTable(PartitionIdentity identity, string entryIdentifier, string? bcdDevice)
    {
        var mountPoint = TryExtractPartitionMountPoint(bcdDevice);
        if (mountPoint is null)
        {
            return;
        }

        var volumeGuidPath = VolumeIdentity.TryGetVolumeGuidPath(mountPoint);
        if (volumeGuidPath is null)
        {
            _log.Info(
                "boot-validator",
                $"BCD entry {entryIdentifier} names '{mountPoint}', which does not resolve to a volume in this " +
                "environment. Drive letters are per-instance, so this is expected outside the entry's own " +
                "Windows. No partition identity recorded.");
            return;
        }

        var located = VolumeLocator.Enumerate();
        var match = located.Volumes
            .FirstOrDefault(volume => VolumeIdentity.AreSameVolume(volume.VolumeGuidPath, volumeGuidPath));

        if (match is null)
        {
            _log.Warn(
                "boot-validator",
                $"Volume '{volumeGuidPath}' for BCD entry {entryIdentifier} was not found in the partition " +
                "table enumeration, so no disk, partition number or GPT id was recorded.");
            return;
        }

        identity.VolumeGuidPath = match.VolumeGuidPath;
        identity.DiskNumber = match.DiskNumber;
        identity.PartitionNumber = match.PartitionNumber;
        identity.GptPartitionId = match.GptPartitionId;
        identity.ObservedDriveLetter = match.PrimaryMountPoint ?? mountPoint;
        identity.Source =
            $"BCD entry {entryIdentifier}, device '{bcdDevice}' resolved through the drive letter as seen by " +
            "the currently running environment, then through the partition table (metadata only; re-verify " +
            "before any destructive use)";

        if (match.Outcome != VolumeIdentityOutcome.Identified)
        {
            _log.Warn(
                "boot-validator",
                $"Partition identity for BCD entry {entryIdentifier} is incomplete ({match.Outcome}): " +
                $"{match.Diagnostic}");
        }
    }

    /// <summary>Pulls <c>X:</c> out of BCD device text such as <c>partition=C:</c>.</summary>
    private static string? TryExtractPartitionMountPoint(string? bcdDevice)
    {
        if (string.IsNullOrWhiteSpace(bcdDevice))
        {
            return null;
        }

        const string marker = "partition=";
        var start = bcdDevice.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        var value = bcdDevice[(start + marker.Length)..].Trim();
        var end = value.IndexOfAny([' ', ',', ']', ')']);
        if (end >= 0)
        {
            value = value[..end];
        }

        // Only a plain drive letter is usable. Device paths such as
        // partition=\Device\HarddiskVolume3 cannot be turned into a filesystem path here.
        return value.Length == 2 && char.IsLetter(value[0]) && value[1] == ':'
            ? value + Path.DirectorySeparatorChar
            : null;
    }

    /// <summary>
    /// Finds the WinRE identifier by reading the <c>recoverysequence</c> of the running
    /// Windows entry. This is the same relationship <c>reagentc /info</c> reports, read
    /// through BCDEdit so no extra tooling is required.
    /// </summary>
    private async Task<string?> TryDiscoverRecoveryGuidAsync()
    {
        try
        {
            var currentEntries = await _bootManager.EnumerateAsync("{current}");
            var sequence = currentEntries
                .Select(entry => entry.RecoverySequence)
                .FirstOrDefault(value => Guid.TryParse(value?.Trim(), out _));

            if (sequence is null)
            {
                _log.Warn(
                    "boot-validator",
                    "The running Windows entry has no recoverysequence value, so WinRE cannot be discovered " +
                    "automatically.");
                return null;
            }

            var normalized = $"{{{Guid.Parse(sequence.Trim()):D}}}";
            _log.Info("boot-validator", $"Discovered recoverysequence {normalized} on the running entry.");
            return normalized;
        }
        catch (BootManagerException exception)
        {
            _log.Warn("boot-validator", $"Recovery entry discovery failed: {exception.Message}");
            return null;
        }
    }

    private void LogReport(ValidationReport report)
    {
        foreach (var check in report.Checks)
        {
            _log.Write(
                check.Passed ? OperationLogLevel.Info : OperationLogLevel.Warning,
                "boot-validator",
                $"{report.Subject} | {check}");
        }
    }
}
