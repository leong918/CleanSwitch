using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class Phase2AHandoffBehaviorTests
{
    private const string Boot1 = "{fc583d49-a29c-11f1-b0e3-e548a1d3146f}";
    private const string Boot2 = "{fc583d44-a29c-11f1-b0e3-e548a1d3146f}";
    private const string Recovery = "{fc583d45-a29c-11f1-b0e3-e548a1d3146f}";
    private const string Unknown = "{aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb}";

    [Fact]
    public async Task Current_boot1_and_configured_boot2_complete_phase2a_in_order()
    {
        var context = CreateContext(Layout(Boot1, Boot2));

        var state = await context.Handoff.ExecuteAsync(context.Layout);

        Assert.Equal(RetirementStatus.Pending, state.Status);
        Assert.Equal(2, state.SchemaVersion);
        Assert.Equal(Boot1, state.Boot1BcdObjectId);
        Assert.Equal(Boot2, state.Boot2BcdObjectId);
        Assert.Equal(1, context.Coordinator.BeginRetirementCallCount);
        Assert.Equal(Boot2, context.BootManager.DefaultBootTarget);
        Assert.Equal(Recovery, context.BootManager.NextBootTarget);
        Assert.True(context.BootManager.RestartCalled);
        Assert.Equal(new[] { "capture-boot1", "capture-boot2", "default", "bootsequence", "restart" }, context.Events);
    }

    [Fact]
    public async Task Current_configured_boot2_fails_before_capture_state_bcd_or_reboot()
    {
        var context = CreateContext(Layout(Boot2, Boot1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Handoff.ExecuteAsync(context.Layout));

        Assert.Equal(Phase2ARetirementGuard.RunningSurvivorMessage, exception.Message);
        AssertNoSideEffects(context, expectCapture: false);
    }

    [Fact]
    public async Task Other_loader_that_is_not_configured_boot2_fails_closed()
    {
        var context = CreateContext(Layout(Boot1, Unknown));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Handoff.ExecuteAsync(context.Layout));

        Assert.Contains("is not the configured Boot 2 survivor", exception.Message, StringComparison.Ordinal);
        AssertNoSideEffects(context, expectCapture: false);
    }

    [Fact]
    public async Task Capture_identity_failure_never_mutates_bcd_or_restarts()
    {
        var context = CreateContext(Layout(Boot1, Boot2));
        context.Identity.Boot2Identity = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Handoff.ExecuteAsync(context.Layout));

        Assert.Equal(0, context.Coordinator.BeginRetirementCallCount);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
        Assert.Equal(0, context.BootManager.SetNextBootCallCount);
        Assert.False(context.BootManager.RestartCalled);
    }

    [Fact]
    public async Task Default_failure_marks_pending_failed_and_never_sets_bootsequence_or_restarts()
    {
        var context = CreateContext(Layout(Boot1, Boot2));
        context.BootManager.OnSetDefaultBootAsync = _ => throw new BootManagerException("default failed");

        await Assert.ThrowsAsync<BootManagerException>(() => context.Handoff.ExecuteAsync(context.Layout));

        Assert.Equal(1, context.Coordinator.BeginRetirementCallCount);
        Assert.Equal(1, context.Coordinator.MarkFailedCallCount);
        Assert.Equal(RetirementStatus.Failed, context.Coordinator.State!.Status);
        Assert.Equal(0, context.BootManager.SetNextBootCallCount);
        Assert.False(context.BootManager.RestartCalled);
    }

    [Fact]
    public async Task Bootsequence_failure_marks_pending_failed_and_never_restarts()
    {
        var context = CreateContext(Layout(Boot1, Boot2));
        context.BootManager.OnSetNextBootAsync = _ => throw new BootManagerException("bootsequence failed");

        await Assert.ThrowsAsync<BootManagerException>(() => context.Handoff.ExecuteAsync(context.Layout));

        Assert.Equal(1, context.BootManager.SetDefaultBootCallCount);
        Assert.Equal(1, context.BootManager.SetNextBootCallCount);
        Assert.False(context.BootManager.RestartCalled);
        Assert.Equal(RetirementStatus.Failed, context.Coordinator.State!.Status);
    }

    [Fact]
    public async Task Failure_after_pending_has_explicit_failed_transition_and_audit_reason()
    {
        var context = CreateContext(Layout(Boot1, Boot2));
        context.BootManager.OnSetNextBootAsync = _ => Task.FromResult(false);

        await Assert.ThrowsAsync<BootManagerException>(() => context.Handoff.ExecuteAsync(context.Layout));

        var state = Assert.IsType<RetirementState>(context.Coordinator.State);
        var transition = state.Transitions[^1];
        Assert.Equal(RetirementStatus.Pending, transition.From);
        Assert.Equal(RetirementStatus.Failed, transition.To);
        Assert.Contains("Phase 2A failed closed", transition.Reason, StringComparison.Ordinal);
        Assert.Contains("one-time boot target", state.LastError, StringComparison.Ordinal);
        Assert.False(context.BootManager.RestartCalled);
    }

    [Fact]
    public async Task Boot2_already_default_is_idempotent_and_flow_continues()
    {
        var context = CreateContext(Layout(Boot1, Boot2));
        context.BootManager.OnSetDefaultBootAsync = id => Task.FromResult(id == Boot2);

        await context.Handoff.ExecuteAsync(context.Layout);

        Assert.Equal(1, context.BootManager.SetDefaultBootCallCount);
        Assert.Equal(1, context.BootManager.SetNextBootCallCount);
        Assert.True(context.BootManager.RestartCalled);
    }

    [Fact]
    public async Task Current_target_reversal_fails_closed()
    {
        var context = CreateContext(Layout(Boot2, Boot1));

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.Handoff.ExecuteAsync(context.Layout));

        AssertNoSideEffects(context, expectCapture: false);
    }

    [Fact]
    public async Task Retiring_loader_equal_to_survivor_fails_closed()
    {
        var context = CreateContext(Layout(Boot1, Boot1));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Handoff.ExecuteAsync(context.Layout));

        Assert.Contains("identical", exception.Message, StringComparison.Ordinal);
        AssertNoSideEffects(context, expectCapture: false);
    }

    [Fact]
    public void Persisted_role_drift_fails_before_bcd_mutation()
    {
        var layout = Layout(Boot1, Boot2);
        var state = new RetirementState
        {
            SchemaVersion = RetirementState.CurrentSchemaVersion,
            Status = RetirementStatus.Pending,
            Boot1BcdObjectId = Unknown,
            Boot2BcdObjectId = Boot2
        };

        var exception = Assert.Throws<InvalidOperationException>(
            () => Phase2ARetirementGuard.ValidatePersistedRoles(state, layout, Boot2));

        Assert.Contains("persisted PENDING loader roles", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Guard_is_checked_at_ui_and_coordinator_boundaries()
    {
        var mainForm = File.ReadAllText(FindRepoFile("CleanSwitch", "MainForm.cs"));
        var handoff = File.ReadAllText(FindRepoFile("CleanSwitch", "Services", "Phase2AHandoff.cs"));

        Assert.Contains("Phase2ARetirementGuard.Validate(_layout, options.Boot2Guid)", mainForm, StringComparison.Ordinal);
        Assert.True(
            handoff.Split("Phase2ARetirementGuard.Validate(layout, _options.Boot2Guid)", StringSplitOptions.None).Length - 1 >= 3,
            "Guard must run before capture and again before BCD mutation.");
        Assert.Contains("Phase2ARetirementGuard.ValidatePersistedRoles(state, layout, _options.Boot2Guid)", handoff, StringComparison.Ordinal);
    }

    private static TestContext CreateContext(BootLayout layout)
    {
        var events = new List<string>();
        var bootManager = new FakeBootManager
        {
            OnSetDefaultBootAsync = _ =>
            {
                events.Add("default");
                return Task.FromResult(true);
            },
            OnSetNextBootAsync = _ =>
            {
                events.Add("bootsequence");
                return Task.FromResult(true);
            },
            OnRestartAsync = _ =>
            {
                events.Add("restart");
                return Task.CompletedTask;
            }
        };
        var coordinator = new FakeRetirementCoordinator();
        var identity = new FakeIdentitySource(events);
        var options = RetirementFixtures.Options();
        options.Boot2Guid = Boot2;
        options.RecoveryGuid = Recovery;
        options.RestartDelaySeconds = 5;
        return new TestContext(
            layout,
            new Phase2AHandoff(options, bootManager, coordinator, identity),
            bootManager,
            coordinator,
            identity,
            events);
    }

    private static BootLayout Layout(string current, string target) =>
        new(new BootEntry(current, "current"), new BootEntry(target, "target"));

    private static void AssertNoSideEffects(TestContext context, bool expectCapture)
    {
        Assert.Equal(expectCapture ? 1 : 0, context.Identity.RecoveryCallCount);
        Assert.Equal(0, context.Coordinator.BeginRetirementCallCount);
        Assert.Equal(0, context.BootManager.SetDefaultBootCallCount);
        Assert.Equal(0, context.BootManager.SetNextBootCallCount);
        Assert.False(context.BootManager.RestartCalled);
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(parts[^1]);
    }

    private sealed class FakeIdentitySource(List<string> events) : IBootEntryValidator
    {
        public int RecoveryCallCount { get; private set; }

        public PartitionIdentity? Boot1Identity { get; set; } = RetirementFixtures.Boot1Identity();

        public PartitionIdentity? Boot2Identity { get; set; } = RetirementFixtures.Boot2Identity();

        public Task<RecoveryEntryResolution> ResolveRecoveryEntryAsync(string? configuredGuid)
        {
            RecoveryCallCount++;
            var report = new ValidationReport("recovery");
            report.Pass("configured", "configured recovery exists");
            return Task.FromResult(new RecoveryEntryResolution(Recovery, null, report));
        }

        public Task<PartitionIdentity?> TryDescribeBootEntryVolumeAsync(string bootGuid)
        {
            if (string.Equals(bootGuid, Boot1, StringComparison.OrdinalIgnoreCase))
            {
                events.Add("capture-boot1");
                return Task.FromResult(Boot1Identity);
            }

            events.Add("capture-boot2");
            return Task.FromResult(Boot2Identity);
        }
    }

    private sealed record TestContext(
        BootLayout Layout,
        Phase2AHandoff Handoff,
        FakeBootManager BootManager,
        FakeRetirementCoordinator Coordinator,
        FakeIdentitySource Identity,
        List<string> Events);
}
