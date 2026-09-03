using CleanSwitch.Models;

namespace CleanSwitch.Recovery;

public sealed class TargetResolveResult
{
    public required bool Passed { get; init; }

    public ResolvedDeletionTarget? Target { get; init; }

    public required ValidationReport Report { get; init; }

    public string? Failure => Passed ? null : Report.FirstFailure;
}

/// <summary>
/// GPT-authoritative pre-delete resolver. Disk/partition numbers from saved state are
/// compared only after a unique live GPT match; they are never used to search.
/// </summary>
public static class DestructiveTargetResolver
{
    public static TargetResolveResult Resolve(
        PartitionIdentity expectedBoot1,
        PartitionIdentity expectedBoot2,
        GptLayoutSnapshot live,
        IRetirementIdentitySet? identities = null)
    {
        var report = new ValidationReport("Destructive target resolve");

        identities ??= RetirementIdentitySet.FromPersistedOperation(expectedBoot1, expectedBoot2, live);

        if (!expectedBoot1.TryGetGptId(out var boot1Gpt))
        {
            report.Fail("boot1-gpt-present", "Saved Boot 1 GPT unique id is missing. Refusing.");
            return Fail(report);
        }

        if (boot1Gpt != identities.Boot1GptId)
        {
            report.Fail(
                "boot1-gpt-pinned",
                $"Saved Boot 1 GPT {VolumeLocator.FormatGptId(boot1Gpt)} is not the identity-set Boot 1 " +
                $"{VolumeLocator.FormatGptId(identities.Boot1GptId)}.");
            return Fail(report);
        }

        report.Pass("boot1-gpt-pinned", $"Saved Boot 1 GPT is {VolumeLocator.FormatGptId(identities.Boot1GptId)}.");

        if (!expectedBoot2.TryGetGptId(out var boot2Gpt))
        {
            report.Fail("boot2-gpt-present", "Saved Boot 2 GPT unique id is missing. Refusing.");
            return Fail(report);
        }

        if (boot2Gpt != identities.Boot2GptId)
        {
            report.Fail(
                "boot2-gpt-pinned",
                $"Saved Boot 2 GPT {VolumeLocator.FormatGptId(boot2Gpt)} is not the identity-set Boot 2.");
            return Fail(report);
        }

        report.Pass("boot2-gpt-pinned", $"Saved Boot 2 GPT is {VolumeLocator.FormatGptId(identities.Boot2GptId)}.");

        if (boot1Gpt == boot2Gpt)
        {
            report.Fail("target-is-not-boot2", "Boot 1 and Boot 2 GPT unique ids are the same. Refusing.");
            return Fail(report);
        }

        var boot1Matches = live.WithGptId(boot1Gpt);
        report.Add(
            "boot1-gpt-unique",
            boot1Matches.Count == 1,
            boot1Matches.Count == 1
                ? "Exactly one live partition has the Boot 1 GPT unique id."
                : boot1Matches.Count == 0
                    ? "Boot 1 GPT unique id is absent from the live layout."
                    : $"Boot 1 GPT unique id matched {boot1Matches.Count} partitions. Refusing to choose.");

        if (boot1Matches.Count != 1)
        {
            return Fail(report);
        }

        var liveBoot1 = boot1Matches[0];

        var boot2Matches = live.WithGptId(boot2Gpt);
        report.Add(
            "boot2-gpt-unique",
            boot2Matches.Count == 1,
            boot2Matches.Count == 1
                ? "Exactly one live partition has the Boot 2 GPT unique id."
                : $"Boot 2 GPT unique id matched {boot2Matches.Count} partitions.");

        if (boot2Matches.Count != 1)
        {
            return Fail(report);
        }

        AddPersistedIdentityGuards(report, "boot2", expectedBoot2, boot2Matches[0]);
        if (!report.Passed)
        {
            return Fail(report);
        }

        if (liveBoot1.PartitionGptId == boot2Gpt ||
            (identities.Boot2Disk is int boot2Disk &&
             identities.Boot2Partition is int boot2Part &&
             liveBoot1.DiskNumber == boot2Disk &&
             liveBoot1.PartitionNumber == boot2Part))
        {
            report.Fail("target-is-not-boot2", "Live Boot 1 GPT resolved onto Boot 2. Refusing.");
            return Fail(report);
        }

        report.Pass(
            "target-is-not-boot2",
            $"Live Boot 1 GPT is distinct from Boot 2 ({VolumeLocator.FormatGptId(identities.Boot2GptId)}).");

        AddTypeGuards(report, liveBoot1);

        var isRunning = liveBoot1.IsRunningSystemVolume ||
                        live.RunningSystemGptId == liveBoot1.PartitionGptId;
        report.Add(
            "target-is-not-running-system",
            !isRunning,
            isRunning
                ? "Live Boot 1 GPT is the running volume. Refusing."
                : "Live Boot 1 GPT is not the running volume.");

        if (expectedBoot1.DiskNumber is int savedDisk && savedDisk != liveBoot1.DiskNumber)
        {
            report.Fail(
                "disk-number-consistent",
                $"Saved disk {savedDisk} differs from live disk {liveBoot1.DiskNumber}. " +
                "Refusing; disk number is not used to search.");
        }
        else if (expectedBoot1.DiskNumber is null)
        {
            report.Fail("disk-number-consistent", "Saved disk number is missing. Fail closed.");
        }
        else
        {
            report.Pass("disk-number-consistent", $"Live disk {liveBoot1.DiskNumber} matches saved state (audit only).");
        }

        if (expectedBoot1.PartitionNumber is int savedPart && savedPart != liveBoot1.PartitionNumber)
        {
            report.Fail(
                "partition-number-consistent",
                $"Saved partition {savedPart} differs from live partition {liveBoot1.PartitionNumber}. Refusing.");
        }
        else if (expectedBoot1.PartitionNumber is null)
        {
            report.Fail("partition-number-consistent", "Saved partition number is missing. Fail closed.");
        }
        else
        {
            report.Pass(
                "partition-number-consistent",
                $"Live partition {liveBoot1.PartitionNumber} matches saved state (audit only).");
        }

        if (!expectedBoot1.TryGetDiskGptId(out var savedDiskGpt))
        {
            report.Fail("disk-gpt-consistent", "Saved physical disk GPT unique id is missing. Fail closed.");
        }
        else if (liveBoot1.DiskGptId is null)
        {
            report.Fail("disk-gpt-consistent", "Live physical disk GPT unique id is missing. Fail closed.");
        }
        else if (savedDiskGpt != liveBoot1.DiskGptId)
        {
            report.Fail(
                "disk-gpt-consistent",
                $"Saved disk GPT {VolumeLocator.FormatGptId(savedDiskGpt)} differs from live " +
                $"{VolumeLocator.FormatGptId(liveBoot1.DiskGptId.Value)}. Refusing.");
        }
        else
        {
            report.Pass("disk-gpt-consistent", $"Physical disk GPT {VolumeLocator.FormatGptId(savedDiskGpt)} matches.");
        }

        if (expectedBoot1.PartitionStartingOffset is null)
        {
            report.Fail("offset-consistent", "Saved partition start offset is missing. Fail closed.");
        }
        else if (expectedBoot1.PartitionStartingOffset != liveBoot1.StartingOffset)
        {
            report.Fail(
                "offset-consistent",
                $"Saved offset {expectedBoot1.PartitionStartingOffset} differs from live {liveBoot1.StartingOffset}.");
        }
        else
        {
            report.Pass("offset-consistent", $"Start offset {liveBoot1.StartingOffset} matches.");
        }

        if (expectedBoot1.PartitionSizeBytes is null)
        {
            report.Fail("size-consistent", "Saved partition size is missing. Fail closed.");
        }
        else if (expectedBoot1.PartitionSizeBytes != liveBoot1.SizeBytes)
        {
            report.Fail(
                "size-consistent",
                $"Saved size {expectedBoot1.PartitionSizeBytes} differs from live {liveBoot1.SizeBytes}.");
        }
        else
        {
            report.Pass("size-consistent", $"Size {liveBoot1.SizeBytes} matches.");
        }

        if (!GptPartitionTypes.TryParse(expectedBoot1.GptPartitionType, out var savedType) || savedType == Guid.Empty)
        {
            report.Fail("gpt-type-consistent", "Saved GPT type is missing. Fail closed.");
        }
        else if (liveBoot1.PartitionType is null)
        {
            report.Fail("gpt-type-consistent", "Live GPT type is missing. Fail closed.");
        }
        else if (savedType != liveBoot1.PartitionType)
        {
            report.Fail(
                "gpt-type-consistent",
                $"Saved GPT type {GptPartitionTypes.Describe(savedType)} differs from live " +
                $"{GptPartitionTypes.Describe(liveBoot1.PartitionType)}.");
        }
        else
        {
            report.Pass("gpt-type-consistent", $"GPT type {GptPartitionTypes.Describe(savedType)} matches.");
        }

        report.Pass(
            "drive-letter-ignored",
            "Drive letters were not used to locate the target. " +
            $"Live mount (informational)={liveBoot1.MountPoint ?? "(none)"}.");

        if (!report.Passed)
        {
            return Fail(report);
        }

        var target = new ResolvedDeletionTarget(
            liveBoot1.PartitionGptId,
            liveBoot1.DiskGptId,
            liveBoot1.DiskNumber,
            liveBoot1.PartitionNumber,
            liveBoot1.PartitionType,
            liveBoot1.StartingOffset,
            liveBoot1.SizeBytes);

        report.Pass("target-pinned-for-this-execution", $"Pinned for this execution only: {target.Describe()}");
        return new TargetResolveResult { Passed = true, Target = target, Report = report };
    }

    public static ValidationReport VerifyAfterDelete(
        Guid boot1Gpt,
        Guid boot2Gpt,
        IReadOnlyCollection<Guid> protectedGpts,
        GptLayoutSnapshot before,
        GptLayoutSnapshot after)
    {
        var report = new ValidationReport("Post-delete GPT verification");
        var boot1 = after.WithGptId(boot1Gpt);
        report.Add(
            "boot1-gpt-gone",
            boot1.Count == 0,
            boot1.Count == 0
                ? "Boot 1 GPT unique id is absent."
                : $"Boot 1 GPT unique id is still present ({boot1.Count} match(es)).");

        var boot2 = after.WithGptId(boot2Gpt);
        report.Add(
            "boot2-gpt-present",
            boot2.Count == 1,
            boot2.Count == 1
                ? "Boot 2 GPT unique id is still unique."
                : $"Boot 2 GPT unique id matched {boot2.Count} partition(s).");

        foreach (var gpt in protectedGpts.Distinct())
        {
            var beforeMatches = before.WithGptId(gpt);
            var afterMatches = after.WithGptId(gpt);
            if (beforeMatches.Count == 0)
            {
                report.Pass(
                    $"protected-gpt-{VolumeLocator.FormatGptId(gpt)}",
                    "Protected GPT unique id was not in the pre-delete layout; not required after.");
                continue;
            }

            report.Add(
                $"protected-gpt-{VolumeLocator.FormatGptId(gpt)}",
                afterMatches.Count == 1,
                afterMatches.Count == 1
                    ? "Protected GPT unique id is still unique."
                    : $"Protected GPT unique id {VolumeLocator.FormatGptId(gpt)} matched {afterMatches.Count}.");
        }

        foreach (var part in before.Partitions.Where(candidate => candidate.PartitionGptId != boot1Gpt))
        {
            var matches = after.WithGptId(part.PartitionGptId);
            if (matches.Count != 1)
            {
                report.Fail(
                    $"preserved-gpt-{VolumeLocator.FormatGptId(part.PartitionGptId)}",
                    $"Non-target GPT {VolumeLocator.FormatGptId(part.PartitionGptId)} matched {matches.Count} after delete.");
                continue;
            }

            var live = matches[0];
            var unchanged =
                live.PartitionType == part.PartitionType &&
                live.StartingOffset == part.StartingOffset &&
                live.SizeBytes == part.SizeBytes &&
                live.DiskGptId == part.DiskGptId;
            report.Add(
                $"preserved-gpt-{VolumeLocator.FormatGptId(part.PartitionGptId)}",
                unchanged,
                unchanged
                    ? "Non-target GPT unique id is unchanged (type, offset, size, disk GPT)."
                    : $"Non-target GPT {VolumeLocator.FormatGptId(part.PartitionGptId)} changed type/offset/size/disk GPT.");
        }

        var unexpected = after.Partitions
            .Where(part => before.WithGptId(part.PartitionGptId).Count == 0)
            .ToList();
        report.Add(
            "no-unexpected-gpt",
            unexpected.Count == 0,
            unexpected.Count == 0
                ? "No unexpected GPT unique ids appeared after delete."
                : $"Unexpected GPT unique id(s) after delete: {string.Join(", ", unexpected.Select(part => VolumeLocator.FormatGptId(part.PartitionGptId)))}.");

        return report;
    }

    private static void AddTypeGuards(ValidationReport report, LivePartition live)
    {
        var type = live.PartitionType ?? Guid.Empty;
        report.Add(
            "target-is-not-esp",
            type != GptPartitionTypes.EfiSystem,
            type == GptPartitionTypes.EfiSystem ? "Target is ESP. Refusing." : "Target is not ESP.");
        report.Add(
            "target-is-not-msr",
            type != GptPartitionTypes.MicrosoftReserved,
            type == GptPartitionTypes.MicrosoftReserved ? "Target is MSR. Refusing." : "Target is not MSR.");
        report.Add(
            "target-is-not-recovery-partition",
            type != GptPartitionTypes.MicrosoftRecovery,
            type == GptPartitionTypes.MicrosoftRecovery ? "Target is Recovery. Refusing." : "Target is not Recovery.");
        report.Add(
            "target-is-basic-data",
            type == GptPartitionTypes.BasicData,
            type == GptPartitionTypes.BasicData
                ? "Target GPT type is Basic Data."
                : $"Target GPT type is {GptPartitionTypes.Describe(type)}, not Basic Data.");
    }

    private static void AddPersistedIdentityGuards(
        ValidationReport report,
        string prefix,
        PartitionIdentity expected,
        LivePartition live)
    {
        report.Add(
            $"{prefix}-disk-consistent",
            expected.DiskNumber is int disk && disk == live.DiskNumber,
            $"Saved disk={expected.DiskNumber?.ToString() ?? "(missing)"}; live disk={live.DiskNumber}.");
        report.Add(
            $"{prefix}-partition-consistent",
            expected.PartitionNumber is int partition && partition == live.PartitionNumber,
            $"Saved partition={expected.PartitionNumber?.ToString() ?? "(missing)"}; live partition={live.PartitionNumber}.");

        var diskGptMatches = expected.TryGetDiskGptId(out var diskGpt) &&
                             live.DiskGptId is Guid liveDiskGpt && diskGpt == liveDiskGpt;
        report.Add(
            $"{prefix}-disk-gpt-consistent",
            diskGptMatches,
            diskGptMatches ? "Physical disk GPT identity matches." : "Physical disk GPT identity is missing or changed.");
        report.Add(
            $"{prefix}-offset-consistent",
            expected.PartitionStartingOffset is long offset && offset == live.StartingOffset,
            $"Saved offset={expected.PartitionStartingOffset?.ToString() ?? "(missing)"}; live offset={live.StartingOffset}.");
        report.Add(
            $"{prefix}-size-consistent",
            expected.PartitionSizeBytes is long size && size == live.SizeBytes,
            $"Saved size={expected.PartitionSizeBytes?.ToString() ?? "(missing)"}; live size={live.SizeBytes}.");

        var typeMatches = GptPartitionTypes.TryParse(expected.GptPartitionType, out var type) &&
                          live.PartitionType is Guid liveType && type == liveType;
        report.Add(
            $"{prefix}-type-consistent",
            typeMatches,
            typeMatches ? "GPT partition type matches." : "GPT partition type is missing or changed.");
    }

    private static TargetResolveResult Fail(ValidationReport report) =>
        new() { Passed = false, Target = null, Report = report };
}
