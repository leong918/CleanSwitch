using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class DestructiveTargetResolverTests
{
    [Fact]
    public void Resolve_correct_gpt_target_passes_and_pins_live_numbers()
    {
        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout());

        Assert.True(result.Passed, result.Report.Describe());
        Assert.NotNull(result.Target);
        Assert.Equal(PinnedRetirementTargets.Boot1GptId, result.Target.TargetGptId);
        Assert.Equal(RetirementFixtures.DiskGptId, result.Target.DiskGptId);
        Assert.Equal(PinnedRetirementTargets.Boot1Disk, result.Target.DiskNumber);
        Assert.Equal(PinnedRetirementTargets.Boot1Partition, result.Target.PartitionNumber);
        Assert.Equal(GptPartitionTypes.BasicData, result.Target.PartitionType);
        Assert.Equal(RetirementFixtures.Boot1Offset, result.Target.StartingOffset);
        Assert.Equal(RetirementFixtures.Boot1Size, result.Target.SizeBytes);
        Assert.Contains(result.Report.Checks, check => check.Name == "drive-letter-ignored" && check.Passed);
    }

    [Fact]
    public void Resolve_fails_when_target_is_missing()
    {
        var layout = RetirementFixtures.StandardLayout()
            .Without(PinnedRetirementTargets.Boot1GptId);

        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            layout);

        AssertFailed(result, "boot1-gpt-unique");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_target_is_duplicate()
    {
        var extra = RetirementFixtures.Partition(
            PinnedRetirementTargets.Boot1GptId,
            disk: 1,
            partition: 1,
            GptPartitionTypes.BasicData,
            offset: 99,
            size: 99);
        var layout = RetirementFixtures.StandardLayout().Adding(extra);

        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            layout);

        AssertFailed(result, "boot1-gpt-unique");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_disk_number_changed()
    {
        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(disk: 1),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout());

        AssertFailed(result, "disk-number-consistent");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_partition_number_changed()
    {
        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(partition: 7),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout());

        AssertFailed(result, "partition-number-consistent");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_ignores_drive_letter_change()
    {
        var layout = RetirementFixtures.StandardLayout(
            RetirementFixtures.StandardPartitions(boot1Mount: "X:\\"));

        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(letter: "C:\\"),
            RetirementFixtures.Boot2Identity(),
            layout);

        Assert.True(result.Passed, result.Report.Describe());
        Assert.NotNull(result.Target);
        Assert.Equal(PinnedRetirementTargets.Boot1GptId, result.Target.TargetGptId);
        Assert.Contains(result.Report.Checks, check => check.Name == "drive-letter-ignored" && check.Passed);
    }

    [Fact]
    public void Resolve_fails_when_boot1_lands_on_boot2()
    {
        var hijacked = RetirementFixtures.Partition(
            PinnedRetirementTargets.Boot1GptId,
            PinnedRetirementTargets.Boot2Disk,
            PinnedRetirementTargets.Boot2Partition,
            GptPartitionTypes.BasicData,
            RetirementFixtures.Boot1Offset,
            RetirementFixtures.Boot1Size);
        var layout = RetirementFixtures.StandardLayout()
            .Without(PinnedRetirementTargets.Boot1GptId)
            .Adding(hijacked);

        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(
                disk: PinnedRetirementTargets.Boot2Disk,
                partition: PinnedRetirementTargets.Boot2Partition),
            RetirementFixtures.Boot2Identity(),
            layout);

        AssertFailed(result, "target-is-not-boot2");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_target_is_esp()
    {
        AssertTypeGuard(GptPartitionTypes.EfiSystem, "target-is-not-esp");
    }

    [Fact]
    public void Resolve_fails_when_target_is_msr()
    {
        AssertTypeGuard(GptPartitionTypes.MicrosoftReserved, "target-is-not-msr");
    }

    [Fact]
    public void Resolve_fails_when_target_is_recovery()
    {
        AssertTypeGuard(GptPartitionTypes.MicrosoftRecovery, "target-is-not-recovery-partition");
    }

    [Fact]
    public void Resolve_fails_when_target_is_current_winre_volume()
    {
        var running = RetirementFixtures.Partition(
            PinnedRetirementTargets.Boot1GptId,
            PinnedRetirementTargets.Boot1Disk,
            PinnedRetirementTargets.Boot1Partition,
            GptPartitionTypes.BasicData,
            RetirementFixtures.Boot1Offset,
            RetirementFixtures.Boot1Size,
            running: true,
            mount: "X:\\");
        var layout = RetirementFixtures.StandardLayout()
            .Replacing(PinnedRetirementTargets.Boot1GptId, running);

        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            layout);

        AssertFailed(result, "target-is-not-running-system");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_physical_disk_identity_mismatches()
    {
        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(diskGpt: "{bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb}"),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout());

        AssertFailed(result, "disk-gpt-consistent");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_size_mismatches()
    {
        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(size: 1),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout());

        AssertFailed(result, "size-consistent");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_offset_mismatches()
    {
        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(offset: 1),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout());

        AssertFailed(result, "offset-consistent");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_fails_when_gpt_type_mismatches()
    {
        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(type: VolumeLocator.FormatGptId(GptPartitionTypes.MicrosoftRecovery)),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout());

        AssertFailed(result, "gpt-type-consistent");
        Assert.Null(result.Target);
    }

    [Fact]
    public void Resolve_with_injected_identities_does_not_use_production_pins()
    {
        var injected = new RetirementIdentitySet
        {
            Boot1GptId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Boot2GptId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Boot2Disk = 3,
            Boot2Partition = 5,
            ProtectedGptIds = [Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")]
        };

        var productionResult = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.StandardLayout(),
            injected);

        Assert.False(productionResult.Passed, productionResult.Report.Describe());
        Assert.Null(productionResult.Target);
        Assert.Contains(
            productionResult.Report.Checks,
            check => check.Name == "boot1-gpt-pinned" && !check.Passed);
    }

    [Fact]
    public void VerifyAfterDelete_fails_when_target_still_exists()
    {
        var before = RetirementFixtures.StandardLayout();
        var report = DestructiveTargetResolver.VerifyAfterDelete(
            PinnedRetirementTargets.Boot1GptId,
            PinnedRetirementTargets.Boot2GptId,
            Protected(),
            before,
            before);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "boot1-gpt-gone" && !check.Passed);
    }

    [Fact]
    public void VerifyAfterDelete_fails_when_boot2_disappears()
    {
        var before = RetirementFixtures.StandardLayout();
        var after = before
            .Without(PinnedRetirementTargets.Boot1GptId)
            .Without(PinnedRetirementTargets.Boot2GptId);

        var report = DestructiveTargetResolver.VerifyAfterDelete(
            PinnedRetirementTargets.Boot1GptId,
            PinnedRetirementTargets.Boot2GptId,
            Protected(),
            before,
            after);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "boot2-gpt-present" && !check.Passed);
    }

    private static void AssertTypeGuard(Guid liveType, string expectedCheck)
    {
        var typed = RetirementFixtures.Partition(
            PinnedRetirementTargets.Boot1GptId,
            PinnedRetirementTargets.Boot1Disk,
            PinnedRetirementTargets.Boot1Partition,
            liveType,
            RetirementFixtures.Boot1Offset,
            RetirementFixtures.Boot1Size);
        var layout = RetirementFixtures.StandardLayout()
            .Replacing(PinnedRetirementTargets.Boot1GptId, typed);

        var result = DestructiveTargetResolver.Resolve(
            RetirementFixtures.Boot1Identity(type: VolumeLocator.FormatGptId(liveType)),
            RetirementFixtures.Boot2Identity(),
            layout);

        AssertFailed(result, expectedCheck);
        Assert.Null(result.Target);
    }

    private static void AssertFailed(TargetResolveResult result, string checkName)
    {
        Assert.False(result.Passed, result.Report.Describe());
        Assert.Contains(result.Report.Checks, check => check.Name == checkName && !check.Passed);
    }

    private static Guid[] Protected() =>
    [
        PinnedRetirementTargets.Boot2GptId,
        Guid.Parse(PinnedRetirementTargets.EfiGpt),
        Guid.Parse(PinnedRetirementTargets.Boot1WinReGpt),
        Guid.Parse(PinnedRetirementTargets.Boot2WinReGpt)
    ];
}
