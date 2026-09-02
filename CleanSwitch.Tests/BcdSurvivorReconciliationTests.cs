using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;
using Xunit;

namespace CleanSwitch.Tests;

public sealed class BcdSurvivorReconciliationTests
{
    [Fact]
    public void Post_2B_real_machine_state_passes_when_boot1_resume_disappears()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RealMachineRetirementFixtures.PostDeleteBcdSnapshot();
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);

        var report = BcdSurvivorReconciliation.VerifyAfterBoot1PartitionDelete(state, after, before);

        Assert.True(report.Passed, report.Describe());
        Assert.Contains(report.Checks, check =>
            check.Name == "required-bcd-survivors-present" && check.Passed);
    }

    [Fact]
    public void Legacy_state_without_exclusive_list_uses_missing_survivor_inference()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RealMachineRetirementFixtures.PostDeleteBcdSnapshot();
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);
        state.Boot1ExclusiveBcdObjectIds = null;

        var report = BcdSurvivorReconciliation.VerifyAfterBoot1PartitionDelete(state, after, before: null);

        Assert.True(report.Passed, report.Describe());
    }

    [Fact]
    public void Boot1_dependency_graph_includes_boot1_resume_via_loader_reference_not_guid_proximity()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var boot1Identity = new PartitionIdentity
        {
            GptPartitionId = RealMachineRetirementFixtures.Boot1Gpt.ToString("B"),
            ObservedDriveLetter = "C:\\"
        };

        var exclusive = BcdBoot1DependencyGraph.ComputeExclusive(
            before,
            RealMachineRetirementFixtures.Boot1Loader,
            RealMachineRetirementFixtures.Boot2Loader,
            boot1Identity);

        Assert.Contains(RealMachineRetirementFixtures.Boot1Loader, exclusive);
        Assert.Contains(RealMachineRetirementFixtures.Boot1Resume, exclusive);
        Assert.Contains(RealMachineRetirementFixtures.Boot1Recovery, exclusive);
        Assert.Contains(RealMachineRetirementFixtures.Boot1WinReRamdisk, exclusive);
        Assert.DoesNotContain(RealMachineRetirementFixtures.Boot2Loader, exclusive);
        Assert.DoesNotContain(RealMachineRetirementFixtures.Boot2Resume, exclusive);
        Assert.DoesNotContain(RealMachineRetirementFixtures.Boot2Recovery, exclusive);
        Assert.DoesNotContain(RealMachineRetirementFixtures.BootMgr, exclusive);
    }

    [Fact]
    public void Boot1_adjacent_winre_objects_are_graph_owned_but_not_required_post_delete()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RealMachineRetirementFixtures.PostDeleteBcdSnapshot();
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);

        var exclusive = BcdBoot1DependencyGraph.ResolveExclusiveIds(state, after, before);
        var required = BcdSurvivorReconciliation.RequiredSurvivorIds(
            state,
            exclusive,
            RealMachineRetirementFixtures.Boot1Loader);

        Assert.Contains(RealMachineRetirementFixtures.Boot1Recovery, exclusive);
        Assert.Contains(RealMachineRetirementFixtures.Boot1WinReRamdisk, exclusive);
        Assert.DoesNotContain(RealMachineRetirementFixtures.Boot1Recovery, required);
        Assert.DoesNotContain(RealMachineRetirementFixtures.Boot1WinReRamdisk, required);
        Assert.DoesNotContain(RealMachineRetirementFixtures.Boot1Resume, required);
    }

    [Theory]
    [InlineData("boot2-loader", "fc583d44-a29c-11f1-b0e3-e548a1d3146f", "boot2-bcd-unique")]
    [InlineData("boot2-resume", "fc583d43-a29c-11f1-b0e3-e548a1d3146f", "required-bcd-survivors-present")]
    [InlineData("boot2-recovery", "fc583d45-a29c-11f1-b0e3-e548a1d3146f", "required-bcd-survivors-present")]
    [InlineData("bootmgr", "9dea862c-5cdd-4e70-acc1-f32b344d4795", "bootmgr-present")]
    [InlineData("firmware-bootmgr", "a5a30fa2-3d06-4e9f-b5f4-a01df9d1fcba", "required-bcd-survivors-present")]
    public void Missing_non_exclusive_survivor_fails_closed(string _, string missingGuid, string expectedCheck)
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RemoveEntry(RealMachineRetirementFixtures.PostDeleteBcdSnapshot(), Guid.Parse(missingGuid));
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);

        var report = BcdSurvivorReconciliation.VerifyAfterBoot1PartitionDelete(state, after, before);

        Assert.False(report.Passed, report.Describe());
        Assert.Contains(report.Checks, check => check.Name == expectedCheck && !check.Passed);
    }

    [Fact]
    public void Persisted_survivor_list_includes_boot1_resume_full_guid()
    {
        Assert.Contains(
            "{fc583d3f-a29c-11f1-b0e3-e548a1d3146f}",
            RealMachineRetirementFixtures.PersistedSurvivorBcdObjectIds,
            StringComparer.OrdinalIgnoreCase);
    }

    private static BcdSnapshot RemoveEntry(BcdSnapshot snapshot, Guid objectId)
    {
        var remaining = snapshot.Entries
            .Where(entry => entry.ObjectId != objectId)
            .ToList();

        var defaultId = snapshot.DefaultObjectId == objectId ? null : snapshot.DefaultObjectId;
        var bootManagerPresent = objectId == BcdIdentifiers.BootManagerId
            ? false
            : snapshot.BootManagerPresent;

        return new BcdSnapshot(
            remaining,
            snapshot.CurrentObjectId,
            defaultId,
            bootManagerPresent,
            snapshot.Warnings,
            snapshot.DefaultResolution,
            snapshot.CurrentResolution);
    }
}
