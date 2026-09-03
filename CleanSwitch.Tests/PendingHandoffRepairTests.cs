using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class PendingHandoffRepairTests
{
    [Fact]
    public async Task Normal_pending_with_boot1_default_passes_review_without_mutation()
    {
        var context = Create(defaultId: BcdFixtures.Boot1);

        var result = await context.Repair.ReviewAsync(context.State);

        Assert.True(result.Passed, result.Describe(reviewOnly: true));
        Assert.False(result.SafeNoOp);
        Assert.False(result.MutationPerformed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
        Assert.Equal($"bcdedit.exe /default {BcdIdentifiers.Format(BcdFixtures.Boot2)}", result.Command);
    }

    [Fact]
    public async Task Repair_changes_only_default_and_verifies_both_loaders_remain()
    {
        var context = Create(defaultId: BcdFixtures.Boot1);
        context.BootManager.OnSetDefaultBootAsync = _ =>
        {
            context.Bcd.Current = Snapshot(defaultId: BcdFixtures.Boot2);
            return Task.FromResult(true);
        };
        var stateBefore = JsonSerializer.Serialize(context.State);

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.True(result.Passed, result.Describe(reviewOnly: false));
        Assert.True(result.SafeNoOp);
        Assert.True(result.MutationPerformed);
        Assert.Equal(1, context.BootManager.SetDefaultBootCallCount);
        Assert.Equal(BcdIdentifiers.Format(BcdFixtures.Boot2), context.BootManager.DefaultBootTarget);
        Assert.Single(context.Bcd.Current.WithObjectId(BcdFixtures.Boot1));
        Assert.Single(context.Bcd.Current.WithObjectId(BcdFixtures.Boot2));
        Assert.Equal(stateBefore, JsonSerializer.Serialize(context.State));
        Assert.False(context.BootManager.RestartCalled);
    }

    [Fact]
    public async Task Boot2_already_default_is_safe_no_op()
    {
        var context = Create(defaultId: BcdFixtures.Boot2);

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.True(result.Passed, result.Describe(reviewOnly: false));
        Assert.True(result.SafeNoOp);
        Assert.False(result.MutationPerformed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
    }

    [Fact]
    public async Task Identity_drift_fails_closed()
    {
        var boot1 = RetirementFixtures.StandardPartitions()
            .Single(partition => partition.PartitionGptId == PinnedRetirementTargets.Boot1GptId);
        var drifted = RetirementFixtures.Partition(
            boot1.PartitionGptId,
            boot1.DiskNumber,
            boot1.PartitionNumber,
            boot1.PartitionType!.Value,
            boot1.StartingOffset + 4096,
            boot1.SizeBytes,
            boot1.DiskGptId);
        var context = Create(
            defaultId: BcdFixtures.Boot1,
            layout: RetirementFixtures.StandardLayout().Replacing(boot1.PartitionGptId, drifted));

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.False(result.Passed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
    }

    [Fact]
    public async Task Missing_boot2_loader_fails_closed()
    {
        var entries = Snapshot(BcdFixtures.Boot1).Entries
            .Where(entry => entry.ObjectId != BcdFixtures.Boot2)
            .ToArray();
        var context = Create(
            BcdFixtures.Boot1,
            new BcdSnapshot(entries, BcdFixtures.Recovery, BcdFixtures.Boot1, true, []));

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.False(result.Passed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
    }

    [Fact]
    public async Task Unknown_third_loader_as_default_fails_closed()
    {
        var context = Create(defaultId: BcdFixtures.ExtraLoader);

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.False(result.Passed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
    }

    [Fact]
    public async Task Non_pending_state_fails_closed()
    {
        var context = Create(defaultId: BcdFixtures.Boot1);
        context.State.Status = RetirementStatus.Failed;

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.False(result.Passed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
    }

    [Fact]
    public async Task Destructive_flag_true_fails_closed()
    {
        var context = Create(defaultId: BcdFixtures.Boot1);
        context.State.DestructiveDeletionPerformed = true;

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.False(result.Passed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
    }

    [Fact]
    public async Task Bcd_deletion_flag_true_fails_closed()
    {
        var context = Create(defaultId: BcdFixtures.Boot1);
        context.State.BcdDeletionPerformed = true;

        var result = await context.Repair.ExecuteAsync(context.State);

        Assert.False(result.Passed);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
    }

    private static RepairContext Create(
        Guid defaultId,
        BcdSnapshot? snapshot = null,
        GptLayoutSnapshot? layout = null)
    {
        var state = BcdFixtures.CompleteState();
        state.Status = RetirementStatus.Pending;
        state.Phase = "2B-identify";
        state.DestructiveDeletionPerformed = false;
        state.BcdDeletionPerformed = false;
        state.Boot1Identity = RetirementFixtures.Boot1Identity();
        state.Boot2Identity = RetirementFixtures.Boot2Identity();

        var bcd = new FakeBcdStoreSource(snapshot ?? Snapshot(defaultId));
        var bootManager = new FakeBootManager();
        var repair = new PendingHandoffRepair(
            new FakeGptLayoutSource(layout ?? RetirementFixtures.StandardLayout()),
            bcd,
            bootManager,
            new RecordingOperationLog());
        return new RepairContext(state, bcd, bootManager, repair);
    }

    private static BcdSnapshot Snapshot(Guid defaultId) =>
        BcdFixtures.StandardSnapshot(current: BcdFixtures.Boot2, defaultId: defaultId);

    private sealed record RepairContext(
        RetirementState State,
        FakeBcdStoreSource Bcd,
        FakeBootManager BootManager,
        PendingHandoffRepair Repair);
}
