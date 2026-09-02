using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;
using Xunit;

namespace CleanSwitch.Tests;

public sealed class RetirementResumePreviewTests
{
    [Fact]
    public async Task Real_machine_boot1_retired_preview_passes_with_exclusive_resume_absent()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RealMachineRetirementFixtures.PostDeleteBcdSnapshot();
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);

        var preview = await CreatePreview(after).RunAsync(state);

        Assert.Equal("PASS", preview.Readiness);
        Assert.Equal("SKIP", preview.Phase2BAction);
        Assert.Contains("NO-OP", preview.Phase2CDeleteAction, StringComparison.Ordinal);
        Assert.Contains("{fc583d3f-a29c-11f1-b0e3-e548a1d3146f}", preview.Boot1ExclusiveBcdObjectIds, StringComparer.OrdinalIgnoreCase);
        Assert.True(preview.Boot1LoaderAbsent);
        Assert.True(preview.SurvivorReconciliationPassed);
        Assert.True(preview.DefaultPointsToBoot2);

        var text = preview.Describe();
        Assert.Contains("Disk command executed: False", text, StringComparison.Ordinal);
        Assert.Contains("BCD delete command executed: False", text, StringComparison.Ordinal);
        Assert.Contains("State modified: False", text, StringComparison.Ordinal);
        Assert.Contains("Resume readiness: PASS", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preview_fails_when_boot2_loader_missing()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = Remove(
            RealMachineRetirementFixtures.PostDeleteBcdSnapshot(),
            RealMachineRetirementFixtures.Boot2Loader);
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);

        var preview = await CreatePreview(after).RunAsync(state);

        Assert.Equal("FAIL", preview.Readiness);
        Assert.False(preview.SurvivorReconciliationPassed);
    }

    private static RetirementResumePreview CreatePreview(BcdSnapshot bcd) =>
        new(
            new DiskValidator(new RecordingOperationLog()),
            new FakeBcdStoreSource(bcd));

    private static BcdSnapshot Remove(BcdSnapshot snapshot, Guid objectId)
    {
        var remaining = snapshot.Entries.Where(entry => entry.ObjectId != objectId).ToList();
        return new BcdSnapshot(
            remaining,
            snapshot.CurrentObjectId,
            snapshot.DefaultObjectId,
            snapshot.BootManagerPresent,
            snapshot.Warnings,
            snapshot.DefaultResolution,
            snapshot.CurrentResolution);
    }
}
