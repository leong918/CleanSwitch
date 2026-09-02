using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class BcdRetirementTargetResolverTests
{
    [Fact]
    public void Resolve_selects_exact_boot1_guid()
    {
        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            BcdFixtures.Recovery,
            BcdFixtures.StandardSnapshot());

        Assert.True(result.Passed, result.Report.Describe());
        Assert.NotNull(result.Target);
        Assert.Equal(BcdFixtures.Boot1, result.Target.ObjectId);
        Assert.Contains(result.Report.Checks, check => check.Name == "display-name-ignored" && check.Passed);
    }

    [Fact]
    public void Resolve_fails_when_boot1_missing()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        AssertFailed(result, "boot1-guid-unique");
    }

    [Fact]
    public void Resolve_fails_when_boot2_missing()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot1, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        AssertFailed(result, "boot2-guid-unique");
    }

    [Fact]
    public void Resolve_fails_when_boot1_equals_boot2()
    {
        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot1,
            null,
            BcdFixtures.StandardSnapshot());

        AssertFailed(result, "boot1-distinct-from-boot2");
    }

    [Fact]
    public void Resolve_ignores_identical_display_names()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot1, "Windows 11"),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        Assert.True(result.Passed, result.Report.Describe());
        Assert.Equal(BcdFixtures.Boot1, result.Target!.ObjectId);
    }

    [Fact]
    public void Resolve_refuses_bootmgr()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdIdentifiers.BootManagerId,
            BcdFixtures.Boot2,
            null,
            snapshot);

        AssertFailed(result, "target-not-protected");
    }

    [Fact]
    public void Resolve_refuses_when_boot1_is_current()
    {
        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            BcdFixtures.StandardSnapshot(current: BcdFixtures.Boot1));

        AssertFailed(result, "target-is-not-current");
    }

    [Fact]
    public void Resolve_refuses_when_boot1_is_default()
    {
        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            BcdFixtures.StandardSnapshot(defaultId: BcdFixtures.Boot1));

        AssertFailed(result, "target-is-not-default");
    }

    [Fact]
    public void Resolve_refuses_recovery_object()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot1, "Windows Recovery Environment", BcdObjectKind.RecoveryLoader, device: "ramdisk=[boot]\\Recovery\\winre.wim"),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            BcdFixtures.Boot1,
            snapshot);

        Assert.False(result.Passed, result.Report.Describe());
        Assert.Contains(result.Report.Checks, check => check.Name == "target-is-not-recovery-object" && !check.Passed);
    }

    [Fact]
    public void Verify_fails_when_boot1_still_present()
    {
        var before = BcdFixtures.StandardSnapshot();
        var report = BcdRetirementTargetResolver.VerifyAfterDelete(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            before.ConcreteObjectIds(),
            before,
            before);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "boot1-bcd-gone" && !check.Passed);
    }

    [Fact]
    public void Verify_fails_when_boot2_missing()
    {
        var before = BcdFixtures.StandardSnapshot();
        var after = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.ExtraLoader, "Other Windows")
        ]);

        var report = BcdRetirementTargetResolver.VerifyAfterDelete(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            new HashSet<Guid>(before.ConcreteObjectIds()),
            before,
            after);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "boot2-bcd-present" && !check.Passed);
    }

    [Fact]
    public void Resolve_fails_when_boot1_guid_is_duplicated()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot1, "Windows 11"),
            BcdFixtures.Loader(BcdFixtures.Boot1, "Windows 11 copy"),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        AssertFailed(result, "boot1-guid-unique");
    }

    [Fact]
    public void Resolve_refuses_firmware_object()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot1, "UEFI Firmware", BcdObjectKind.FirmwareObject, path: @"\EFI\Boot\bootx64.efi"),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        AssertFailed(result, "target-is-not-firmware");
    }

    [Fact]
    public void Resolve_refuses_unresolved_current()
    {
        var snapshot = new BcdSnapshot(
            BcdFixtures.StandardSnapshot().Entries,
            currentObjectId: null,
            BcdFixtures.Boot2,
            true,
            [],
            BcdAliasResolution.Unresolved,
            BcdAliasResolution.Resolved);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        AssertFailed(result, "current-alias-resolved");
    }

    [Fact]
    public void Resolve_allows_absent_current_on_isolated_store()
    {
        var snapshot = new BcdSnapshot(
            BcdFixtures.StandardSnapshot().Entries,
            currentObjectId: null,
            BcdFixtures.Boot2,
            true,
            [],
            BcdAliasResolution.Absent,
            BcdAliasResolution.Resolved);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        Assert.True(result.Passed, result.Report.Describe());
        Assert.Equal(BcdFixtures.Boot1, result.Target!.ObjectId);
    }

    [Fact]
    public void Resolve_refuses_unresolved_default()
    {
        var snapshot = new BcdSnapshot(
            BcdFixtures.StandardSnapshot().Entries,
            BcdFixtures.Recovery,
            defaultObjectId: null,
            true,
            [],
            BcdAliasResolution.Resolved,
            BcdAliasResolution.Unresolved);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot);

        AssertFailed(result, "default-alias-resolved");
    }

    [Fact]
    public void Resolve_matches_boot1_device_gpt_when_present()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(
                BcdFixtures.Boot1,
                "Windows 11",
                device: $"partition={PinnedRetirementTargets.Boot1Gpt}"),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot,
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity());

        Assert.True(result.Passed, result.Report.Describe());
        Assert.Contains(result.Report.Checks, check => check.Name == "boot1-device-matches-stored-gpt" && check.Passed);
    }

    [Fact]
    public void Resolve_refuses_when_device_gpt_is_boot2()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(
                BcdFixtures.Boot1,
                "Windows 11",
                device: $"partition={PinnedRetirementTargets.Boot2Gpt}"),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot,
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity());

        AssertFailed(result, "boot1-device-not-boot2-gpt");
    }

    [Fact]
    public void Resolve_allows_unknown_device_after_partition_delete()
    {
        var snapshot = BcdFixtures.StandardSnapshot(
        [
            BcdFixtures.BootManager(),
            BcdFixtures.Loader(BcdFixtures.Boot1, "Windows 11", device: "unknown"),
            BcdFixtures.Loader(BcdFixtures.Boot2, "Windows 11")
        ]);

        var result = BcdRetirementTargetResolver.Resolve(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            null,
            snapshot,
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity());

        Assert.True(result.Passed, result.Report.Describe());
        Assert.Contains(result.Report.Checks, check => check.Name == "boot1-device-has-no-parseable-gpt" && check.Passed);
    }

    [Fact]
    public void Verify_fails_when_unrelated_object_disappears()
    {
        var before = BcdFixtures.StandardSnapshot();
        var afterEntries = before.Entries.Where(entry =>
            entry.ObjectId != BcdFixtures.Boot1 &&
            entry.ObjectId != BcdFixtures.ExtraLoader).ToList();
        var after = new BcdSnapshot(afterEntries, BcdFixtures.Recovery, BcdFixtures.Boot2, true, []);

        var approved = new HashSet<Guid>(before.ConcreteObjectIds());
        approved.Remove(BcdFixtures.Boot1);
        var report = BcdRetirementTargetResolver.VerifyAfterDelete(
            BcdFixtures.Boot1,
            BcdFixtures.Boot2,
            approved,
            before,
            after);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "no-unrelated-object-missing" && !check.Passed);
    }

    private static void AssertFailed(BcdResolveResult result, string checkName)
    {
        Assert.False(result.Passed, result.Report.Describe());
        Assert.Null(result.Target);
        Assert.Contains(result.Report.Checks, check => check.Name == checkName && !check.Passed);
    }
}
