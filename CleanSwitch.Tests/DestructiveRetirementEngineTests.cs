using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class DestructiveRetirementEngineTests
{
    [Fact]
    public async Task Execute_passes_exact_resolved_target_only_when_every_guard_passes()
    {
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var command = new FakeDestructiveDiskCommand
        {
            OnExecute = _ => layout.Current = layout.Current.Without(PinnedRetirementTargets.Boot1GptId)
        };
        var log = new RecordingOperationLog();
        var engine = CreateEngine(layout, command, log, implemented: true, configEnabled: true);

        var result = await engine.ExecuteAsync(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.PassingValidation(),
            explicitOptIn: true);

        Assert.Equal(RetirementExecutionKind.Succeeded, result.Kind);
        Assert.True(result.DestructiveDeletionOccurred);
        Assert.Equal(1, command.ExecuteCount);
        Assert.NotNull(command.LastTarget);
        Assert.Equal(PinnedRetirementTargets.Boot1GptId, command.LastTarget.TargetGptId);
        Assert.Equal(RetirementFixtures.DiskGptId, command.LastTarget.DiskGptId);
        Assert.Equal(PinnedRetirementTargets.Boot1Disk, command.LastTarget.DiskNumber);
        Assert.Equal(PinnedRetirementTargets.Boot1Partition, command.LastTarget.PartitionNumber);
        Assert.Equal(GptPartitionTypes.BasicData, command.LastTarget.PartitionType);
        Assert.Equal(RetirementFixtures.Boot1Offset, command.LastTarget.StartingOffset);
        Assert.Equal(RetirementFixtures.Boot1Size, command.LastTarget.SizeBytes);
        Assert.True(log.Contains("destructiveDeletionOccurred=true"));
        Assert.True(log.Contains(VolumeLocator.FormatGptId(PinnedRetirementTargets.Boot1GptId)));
        Assert.True(log.Contains("target-is-not-esp"));
        Assert.True(log.Contains("target-is-not-msr"));
        Assert.True(log.Contains("target-is-not-recovery-partition"));
        Assert.True(log.Contains("target-is-not-running-system"));
        Assert.True(log.Contains("disk-gpt-consistent"));
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public async Task Execute_does_not_invoke_command_when_any_gate_is_closed(
        bool implemented,
        bool configEnabled,
        bool explicitOptIn,
        bool validationPassed)
    {
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var command = new FakeDestructiveDiskCommand();
        var engine = CreateEngine(layout, command, new RecordingOperationLog(), implemented, configEnabled);

        var exception = await Assert.ThrowsAnyAsync<Exception>(() =>
            engine.ExecuteAsync(
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity(),
                validationPassed ? RetirementFixtures.PassingValidation() : RetirementFixtures.FailingValidation(),
                explicitOptIn));

        Assert.True(exception is RetirementNotImplementedException or RetirementExecutionException);
        Assert.Equal(0, command.ExecuteCount);
        Assert.Null(command.LastTarget);
    }

    [Fact]
    public async Task Execute_does_not_invoke_command_when_resolve_fails()
    {
        var layout = new FakeGptLayoutSource(
            RetirementFixtures.StandardLayout().Without(PinnedRetirementTargets.Boot1GptId));
        var command = new FakeDestructiveDiskCommand();
        var engine = CreateEngine(layout, command, new RecordingOperationLog(), implemented: true, configEnabled: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains("Pre-delete GPT resolve failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
    }

    [Fact]
    public async Task Execute_fails_closed_when_executor_throws()
    {
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var command = new FakeDestructiveDiskCommand { ThrowOnExecute = true };
        var log = new RecordingOperationLog();
        var engine = CreateEngine(layout, command, log, implemented: true, configEnabled: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains("threw", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(log.Contains("destructiveDeletionOccurred=true"));
        Assert.Equal(1, command.ExecuteCount);
    }

    [Fact]
    public async Task Execute_fails_closed_when_executor_exits_nonzero()
    {
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var command = new FakeDestructiveDiskCommand { ExitCode = 7 };
        var log = new RecordingOperationLog();
        var engine = CreateEngine(layout, command, log, implemented: true, configEnabled: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains("non-zero", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(log.Contains("destructiveDeletionOccurred=true"));
    }

    [Fact]
    public async Task Execute_fails_when_post_delete_target_still_exists()
    {
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var command = new FakeDestructiveDiskCommand();
        var log = new RecordingOperationLog();
        var engine = CreateEngine(layout, command, log, implemented: true, configEnabled: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains("Post-delete GPT verification failed", exception.Message, StringComparison.Ordinal);
        Assert.False(log.Contains("destructiveDeletionOccurred=true"));
        Assert.Equal(1, command.ExecuteCount);
    }

    [Fact]
    public async Task Execute_fails_when_boot2_disappears_after_delete()
    {
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var command = new FakeDestructiveDiskCommand
        {
            OnExecute = _ => layout.Current = layout.Current
                .Without(PinnedRetirementTargets.Boot1GptId)
                .Without(PinnedRetirementTargets.Boot2GptId)
        };
        var log = new RecordingOperationLog();
        var engine = CreateEngine(layout, command, log, implemented: true, configEnabled: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains("Post-delete GPT verification failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("boot2", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(log.Contains("destructiveDeletionOccurred=true"));
    }

    [Theory]
    [InlineData(DestructiveRetirementFaultPoint.ImmediatelyBeforeDiskCommand, 0, true)]
    [InlineData(DestructiveRetirementFaultPoint.ImmediatelyAfterDiskCommand, 1, false)]
    public async Task Power_loss_faults_bracket_the_exact_disk_command(
        DestructiveRetirementFaultPoint point,
        int expectedCommandCount,
        bool targetRemains)
    {
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var command = new FakeDestructiveDiskCommand
        {
            OnExecute = _ => layout.Current = layout.Current.Without(PinnedRetirementTargets.Boot1GptId)
        };
        var engine = new DestructiveRetirementEngine(
            RetirementFixtures.Options(enableDestructive: true),
            layout,
            command,
            new RecordingOperationLog(),
            destructiveOperationsImplemented: true,
            identities: null,
            faults: new ThrowAtDestructiveFault(point));

        await Assert.ThrowsAsync<RetirementExecutionException>(() => engine.ExecuteAsync(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity(),
            RetirementFixtures.PassingValidation(),
            explicitOptIn: true));

        Assert.Equal(expectedCommandCount, command.ExecuteCount);
        Assert.Equal(targetRemains, layout.Current.WithGptId(PinnedRetirementTargets.Boot1GptId).Count == 1);
    }

    private sealed class ThrowAtDestructiveFault(DestructiveRetirementFaultPoint expected)
        : IDestructiveRetirementFaultInjector
    {
        public void Hit(DestructiveRetirementFaultPoint point)
        {
            if (point == expected) throw new InvalidOperationException("simulated power loss");
        }
    }

    private static DestructiveRetirementEngine CreateEngine(
        IGptLayoutSource layout,
        IDestructiveDiskCommand command,
        RecordingOperationLog log,
        bool implemented,
        bool configEnabled) =>
        new(
            RetirementFixtures.Options(enableDestructive: configEnabled),
            layout,
            command,
            log,
            destructiveOperationsImplemented: implemented);
}
