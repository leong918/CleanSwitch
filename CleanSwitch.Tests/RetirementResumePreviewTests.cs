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
        var layout = RealMachineRetirementFixtures.PostBoot1RetiredGptSnapshot();

        var preview = await CreatePreview(after, layout).RunAsync(state);

        Assert.Empty(layout.WithGptId(RealMachineRetirementFixtures.Boot1Gpt));
        Assert.Single(layout.WithGptId(RealMachineRetirementFixtures.Boot2Gpt));
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

    [Fact]
    public async Task Preview_fails_closed_when_injected_boot2_gpt_is_missing()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RealMachineRetirementFixtures.PostDeleteBcdSnapshot();
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);
        var missingBoot2 = new GptLayoutSnapshot([], null, []);

        var preview = await CreatePreview(after, missingBoot2).RunAsync(state);

        Assert.Equal("FAIL", preview.Readiness);
        Assert.False(preview.Boot2GptObserved);
        Assert.Contains(
            VolumeLocator.FormatGptId(RealMachineRetirementFixtures.Boot2Gpt),
            preview.FailureReason,
            StringComparison.OrdinalIgnoreCase);
    }

    private static RetirementResumePreview CreatePreview(
        BcdSnapshot bcd,
        GptLayoutSnapshot? layout = null) =>
        new(
            new DiskValidator(
                new RecordingOperationLog(),
                new FakeGptLayoutSource(layout ?? RealMachineRetirementFixtures.PostBoot1RetiredGptSnapshot())),
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
