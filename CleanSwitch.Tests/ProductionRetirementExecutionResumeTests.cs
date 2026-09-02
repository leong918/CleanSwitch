using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;
using Xunit;

namespace CleanSwitch.Tests;

public sealed class ProductionRetirementExecutionResumeTests
{
    [LiveTestBuildFact]
    public async Task Boot1_retired_resume_skips_diskpart_and_bcd_delete_when_loader_already_absent()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RealMachineRetirementFixtures.PostDeleteBcdSnapshot();
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);

        var diskCommand = new FakeDestructiveDiskCommand();
        var bcdCommand = new FakeDestructiveBcdCommand();
        var bcdStore = new FakeBcdStoreSource(after);
        var log = new RecordingOperationLog();
        var options = RetirementFixtures.Options(enableDestructive: true);
        var executor = new RetirementExecutor(
            options,
            log,
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            diskCommand,
            bcdStore,
            bcdCommand);
        var coordinator = new FakeRetirementCoordinator { State = state };
        var execution = new ProductionRetirementExecution(
            new FakeBootManager(),
            coordinator,
            new DiskValidator(log),
            executor,
            new RetirementHardwareReview(
                new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
                bcdStore,
                log),
            bcdStore,
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            options,
            log);

        var result = await execution.RunAsync(
            state,
            new RecoveryRunRequest(DryRun: true, ReviewOnly: false, ExecuteDeletion: true));

        Assert.Equal(RecoveryRunOutcome.DryRunCompleted, result.Outcome);
        Assert.Contains("required-bcd-survivors-present", result.Message, StringComparison.Ordinal);
        Assert.Contains("[PASS]", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, diskCommand.ExecuteCount);
        Assert.Equal(0, bcdCommand.ExecuteCount);
        Assert.Equal(RetirementStatus.Boot1Retired, coordinator.State!.Status);
    }

    [LiveTestBuildFact]
    public async Task Boot1_retired_resume_never_invokes_diskpart_even_with_execute_deletion()
    {
        var before = RealMachineRetirementFixtures.PreDeleteBcdSnapshot();
        var after = RealMachineRetirementFixtures.PostDeleteBcdSnapshot();
        var state = RealMachineRetirementFixtures.Boot1RetiredState(before);

        var diskCommand = new FakeDestructiveDiskCommand { ThrowOnExecute = true };
        var bcdStore = new FakeBcdStoreSource(after);
        var log = new RecordingOperationLog();
        var options = RetirementFixtures.Options(enableDestructive: true);
        var executor = new RetirementExecutor(
            options,
            log,
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            diskCommand,
            bcdStore,
            new FakeDestructiveBcdCommand());
        var execution = CreateExecution(state, executor, bcdStore, log, options);

        var result = await execution.RunAsync(
            state,
            new RecoveryRunRequest(DryRun: true, ReviewOnly: false, ExecuteDeletion: true));

        Assert.NotEqual(RecoveryRunOutcome.Failed, result.Outcome);
        Assert.Equal(0, diskCommand.ExecuteCount);
    }

    private static ProductionRetirementExecution CreateExecution(
        RetirementState state,
        RetirementExecutor executor,
        FakeBcdStoreSource bcdStore,
        RecordingOperationLog log,
        CleanSwitchOptions options)
    {
        var coordinator = new FakeRetirementCoordinator { State = state };
        return new ProductionRetirementExecution(
            new FakeBootManager(),
            coordinator,
            new DiskValidator(log),
            executor,
            new RetirementHardwareReview(
                new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
                bcdStore,
                log),
            bcdStore,
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            options,
            log);
    }
}
