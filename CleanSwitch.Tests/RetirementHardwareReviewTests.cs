using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class RetirementHardwareReviewTests
{
    [Fact]
    public void Complete_schema_v2_state_passes_both_phases()
    {
        var disk = new FakeDestructiveDiskCommand();
        var bcd = new FakeDestructiveBcdCommand();
        var review = CreateReview();

        var result = review.Run(CompleteState());

        Assert.True(result.Phase2BReviewPassed, result.Describe());
        Assert.True(result.Phase2CReviewPassed, result.Describe());
        Assert.True(result.OverallPassed, result.Describe());
        Assert.Contains("Overall: PASS", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("Disk command executed: False", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("BCD command executed: False", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("select disk 0", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("select partition 3", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("delete partition override", result.Describe(), StringComparison.Ordinal);
        Assert.Contains(
            $"bcdedit.exe /delete {BcdIdentifiers.Format(BcdFixtures.Boot1)}",
            result.Describe(),
            StringComparison.Ordinal);
        Assert.Contains("Boot1BcdObjectId", result.Describe(), StringComparison.Ordinal);
        Assert.Equal(0, disk.ExecuteCount);
        Assert.Equal(0, bcd.ExecuteCount);
    }

    [Fact]
    public void Fresh_reinstall_uses_schema_v2_operation_identity_and_survivor_default()
    {
        var freshBoot1 = Guid.Parse("3a11ea37-200c-4804-8d69-1ea92d452a40");
        var state = CompleteState();
        state.Status = RetirementStatus.Pending;
        state.Phase = "2B-identify";
        state.Boot1Identity!.GptPartitionId = VolumeLocator.FormatGptId(freshBoot1);

        var oldBoot1 = PinnedRetirementTargets.Boot1GptId;
        var oldPartition = RetirementFixtures.StandardPartitions()
            .Single(partition => partition.PartitionGptId == oldBoot1);
        var freshPartition = RetirementFixtures.Partition(
            freshBoot1,
            oldPartition.DiskNumber,
            oldPartition.PartitionNumber,
            oldPartition.PartitionType!.Value,
            oldPartition.StartingOffset,
            oldPartition.SizeBytes,
            oldPartition.DiskGptId,
            mount: oldPartition.MountPoint);
        var layout = RetirementFixtures.StandardLayout()
            .Replacing(oldBoot1, freshPartition);
        var bcd = BcdFixtures.StandardSnapshot(
            current: BcdFixtures.Recovery,
            defaultId: BcdFixtures.Boot2);

        var result = CreateReview(
            new FakeGptLayoutSource(layout),
            new FakeBcdStoreSource(bcd)).Run(state);

        Assert.True(result.Phase2BReviewPassed, result.Describe());
        Assert.True(result.Phase2CReviewPassed, result.Describe());
        Assert.DoesNotContain(PinnedRetirementTargets.Boot1Gpt, result.Describe(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(VolumeLocator.FormatGptId(freshBoot1), result.Describe(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fresh_reinstall_still_fails_closed_when_boot1_is_default()
    {
        var result = CreateReview(
            bcd: new FakeBcdStoreSource(BcdFixtures.StandardSnapshot(defaultId: BcdFixtures.Boot1)))
            .Run(CompleteState());

        Assert.True(result.Phase2BReviewPassed, result.Describe());
        Assert.False(result.Phase2CReviewPassed, result.Describe());
        Assert.Contains("target-is-not-default", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Operation_derived_identity_still_fails_closed_when_survivor_geometry_changes()
    {
        var boot2 = RetirementFixtures.StandardPartitions()
            .Single(partition => partition.PartitionGptId == PinnedRetirementTargets.Boot2GptId);
        var changedBoot2 = RetirementFixtures.Partition(
            boot2.PartitionGptId,
            boot2.DiskNumber,
            boot2.PartitionNumber,
            boot2.PartitionType!.Value,
            boot2.StartingOffset,
            boot2.SizeBytes + 4096,
            boot2.DiskGptId,
            mount: boot2.MountPoint);
        var layout = RetirementFixtures.StandardLayout()
            .Replacing(boot2.PartitionGptId, changedBoot2);

        var result = CreateReview(new FakeGptLayoutSource(layout)).Run(CompleteState());

        Assert.False(result.Phase2BReviewPassed, result.Describe());
        Assert.Contains("boot2-size-consistent", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_schema_v1_fails_closed()
    {
        var review = CreateReview();
        var state = CompleteState();
        state.SchemaVersion = 1;
        state.Boot1BcdObjectId = null;
        state.Boot2BcdObjectId = null;

        var result = review.Run(state);

        Assert.False(result.OverallPassed);
        Assert.False(result.Phase2BReviewPassed);
        Assert.False(result.Phase2CReviewPassed);
        Assert.Contains(RetirementHardwareReview.MustRegenerateMessage, result.Describe(), StringComparison.Ordinal);
        Assert.Contains("Overall: FAIL", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_state_fails_closed()
    {
        var result = CreateReview().Run(null);

        Assert.False(result.OverallPassed);
        Assert.Contains(RetirementHardwareReview.MustRegenerateMessage, result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Overall_fails_if_phase_2c_fails()
    {
        var snapshot = BcdFixtures.StandardSnapshot(current: BcdFixtures.Boot1);
        var review = CreateReview(bcd: new FakeBcdStoreSource(snapshot));

        var result = review.Run(CompleteState());

        Assert.True(result.Phase2BReviewPassed, result.Describe());
        Assert.False(result.Phase2CReviewPassed, result.Describe());
        Assert.False(result.OverallPassed);
        Assert.DoesNotContain("bcdedit.exe /delete", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_does_not_invoke_destructive_commands()
    {
        var disk = new FakeDestructiveDiskCommand();
        var bcdCommand = new FakeDestructiveBcdCommand();
        var log = new RecordingOperationLog();
        var options = RetirementFixtures.Options(enableDestructive: true);
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var store = new FakeBcdStoreSource(BcdFixtures.StandardSnapshot());
        var coordinator = new FakeRetirementCoordinator { State = CompleteState() };
        var executor = new RetirementExecutor(options, log, layout, disk, store, bcdCommand);
        var runner = new RecoveryRunner(
            new FakeBootManager(),
            coordinator,
            new DiskValidator(log),
            new BootEntryValidator(new FakeBootManager(), log),
            executor,
            options,
            log,
            new RetirementHardwareReview(layout, store, log));

        var result = await runner.RunAsync(new RecoveryRunRequest(false, true, true));

        Assert.Equal(RecoveryRunOutcome.ReviewCompleted, result.Outcome);
        Assert.Equal(0, disk.ExecuteCount);
        Assert.Equal(0, bcdCommand.ExecuteCount);
        Assert.Contains("Disk command executed: False", result.Message, StringComparison.Ordinal);
        Assert.Contains("BCD command executed: False", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(DiskpartDestructiveDiskCommand)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hardware_review_source_never_names_destructive_commands()
    {
        var path = FindSource("RetirementHardwareReview.cs");
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("DiskpartDestructiveDiskCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BcdeditDestructiveBcdCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDestructiveDiskCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDestructiveBcdCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoggedProcess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("diskpart.exe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_exposes_hardware_review_switch()
    {
        var source = File.ReadAllText(FindSource("Program.cs", underRecovery: false));
        Assert.Contains("--retirement-hardware-review", source, StringComparison.Ordinal);
        Assert.Contains("--recovery-review", source, StringComparison.Ordinal);
    }

    private static RetirementHardwareReview CreateReview(
        FakeGptLayoutSource? layout = null,
        FakeBcdStoreSource? bcd = null) =>
        new(
            layout ?? new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            bcd ?? new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            new RecordingOperationLog());

    private static RetirementState CompleteState()
    {
        var state = BcdFixtures.CompleteState();
        state.Boot1Identity = RetirementFixtures.Boot1Identity();
        state.Boot2Identity = RetirementFixtures.Boot2Identity();
        return state;
    }

    private static string FindSource(string fileName, bool underRecovery = true)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = underRecovery
                ? Path.Combine(dir.FullName, "CleanSwitch", "Recovery", fileName)
                : Path.Combine(dir.FullName, "CleanSwitch", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
