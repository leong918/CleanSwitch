using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class DestructiveBcdRetirementEngineTests
{
    [Fact]
    public async Task Execute_invokes_delete_once_with_exact_boot1_guid()
    {
        var store = new FakeBcdStoreSource(BcdFixtures.StandardSnapshot());
        var command = new FakeDestructiveBcdCommand
        {
            OnExecute = _ => store.Current = Remove(store.Current, BcdFixtures.Boot1)
        };
        var log = new RecordingOperationLog();
        var engine = new DestructiveBcdRetirementEngine(store, command, log, bcdOperationsImplemented: true);

        var result = await engine.ExecuteAsync(
            BcdFixtures.CompleteState(),
            explicitOptIn: true,
            RetirementFixtures.PassingValidation());

        Assert.Equal(RetirementExecutionKind.Succeeded, result.Kind);
        Assert.Equal(1, command.ExecuteCount);
        Assert.NotNull(command.LastTarget);
        Assert.Equal(BcdFixtures.Boot1, command.LastTarget.ObjectId);
        Assert.Equal(BcdIdentifiers.Format(BcdFixtures.Boot1), command.LastTarget.FormattedId);
        Assert.DoesNotContain("Windows 11", command.LastTarget.FormattedId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_refuses_legacy_state_without_bcd_object_ids()
    {
        var command = new FakeDestructiveBcdCommand();
        var engine = new DestructiveBcdRetirementEngine(
            new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            command,
            new RecordingOperationLog(),
            bcdOperationsImplemented: true);

        var legacy = BcdFixtures.CompleteState(schemaVersion: 1, boot1Bcd: null, boot2Bcd: null);
        legacy.Boot1BcdObjectId = null;
        legacy.Boot2BcdObjectId = null;

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(legacy, true, RetirementFixtures.PassingValidation()));

        Assert.Contains(
            BcdRetirementStateRequirements.MustRegenerateMessage,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
    }

    [Fact]
    public async Task Execute_does_not_infer_ids_from_legacy_boot1id()
    {
        var command = new FakeDestructiveBcdCommand();
        var engine = new DestructiveBcdRetirementEngine(
            new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            command,
            new RecordingOperationLog(),
            bcdOperationsImplemented: true);

        var state = BcdFixtures.CompleteState();
        state.Boot1Id = BcdIdentifiers.Format(BcdFixtures.Boot1);
        state.Boot2Id = BcdIdentifiers.Format(BcdFixtures.Boot2);
        state.Boot1BcdObjectId = "{current}";
        state.Boot2BcdObjectId = BcdIdentifiers.Format(BcdFixtures.Boot2);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(state, true, RetirementFixtures.PassingValidation()));

        Assert.Contains("must be regenerated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
    }

    [Fact]
    public async Task Execute_refuses_default_alias_as_boot1_id()
    {
        var command = new FakeDestructiveBcdCommand();
        var engine = new DestructiveBcdRetirementEngine(
            new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            command,
            new RecordingOperationLog(),
            bcdOperationsImplemented: true);

        var state = BcdFixtures.CompleteState();
        state.Boot1BcdObjectId = "{default}";

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(state, true, RetirementFixtures.PassingValidation()));

        Assert.Contains("must be regenerated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
    }

    [Fact]
    public async Task Execute_fails_when_command_throws()
    {
        var command = new FakeDestructiveBcdCommand { ThrowOnExecute = true };
        var log = new RecordingOperationLog();
        var engine = new DestructiveBcdRetirementEngine(
            new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            command,
            log,
            bcdOperationsImplemented: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(BcdFixtures.CompleteState(), true, RetirementFixtures.PassingValidation()));

        Assert.Contains("threw", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(log.Contains("successful bcdedit /delete and post-delete verify"));
    }

    [Fact]
    public async Task Execute_fails_when_command_exits_nonzero()
    {
        var command = new FakeDestructiveBcdCommand { ExitCode = 1 };
        var engine = new DestructiveBcdRetirementEngine(
            new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            command,
            new RecordingOperationLog(),
            bcdOperationsImplemented: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(BcdFixtures.CompleteState(), true, RetirementFixtures.PassingValidation()));

        Assert.Contains("non-zero", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_fails_when_post_delete_boot1_still_present()
    {
        var command = new FakeDestructiveBcdCommand();
        var engine = new DestructiveBcdRetirementEngine(
            new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            command,
            new RecordingOperationLog(),
            bcdOperationsImplemented: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(BcdFixtures.CompleteState(), true, RetirementFixtures.PassingValidation()));

        Assert.Contains("Post-delete BCD verification failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_fails_when_post_delete_boot2_missing()
    {
        var store = new FakeBcdStoreSource(BcdFixtures.StandardSnapshot());
        var command = new FakeDestructiveBcdCommand
        {
            OnExecute = _ => store.Current = Remove(store.Current, BcdFixtures.Boot1, BcdFixtures.Boot2)
        };
        var engine = new DestructiveBcdRetirementEngine(
            store,
            command,
            new RecordingOperationLog(),
            bcdOperationsImplemented: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(BcdFixtures.CompleteState(), true, RetirementFixtures.PassingValidation()));

        Assert.Contains("Post-delete BCD verification failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("boot2", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Production_executor_does_not_call_bcd_command()
    {
        var command = new FakeDestructiveBcdCommand();
        var executor = new RetirementExecutor(
            RetirementFixtures.Options(),
            new RecordingOperationLog(),
            bcdStore: new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            bcdCommand: command);

        Assert.False(executor.IsBcdRetirementAvailable);
        var exception = await Assert.ThrowsAsync<RetirementNotImplementedException>(() =>
            executor.DeleteBoot1BcdEntryAsync(
                BcdFixtures.CompleteState(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains("BcdOperationsImplemented is false", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
    }

    private static BcdSnapshot Remove(BcdSnapshot snapshot, params Guid[] ids)
    {
        var remaining = snapshot.Entries.Where(entry => !ids.Contains(entry.ObjectId)).ToList();
        return new BcdSnapshot(
            remaining,
            snapshot.CurrentObjectId,
            snapshot.DefaultObjectId,
            snapshot.BootManagerPresent,
            snapshot.Warnings,
            snapshot.CurrentResolution,
            snapshot.DefaultResolution);
    }
}
