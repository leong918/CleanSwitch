using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch.Tests;

public sealed class SafetyFindingsImplementationTests
{
    [Fact]
    public void Destructive_authorization_requires_token_winpe_and_exact_current_recovery()
    {
        var options = new CleanSwitchOptions
        {
            Boot2Guid = "{22222222-2222-2222-2222-222222222222}",
            RecoveryGuid = "{33333333-3333-3333-3333-333333333333}"
        };
        var state = Authorized(options);
        var recovery = Guid.Parse(options.RecoveryGuid);
        RetirementExecutionAuthorization.RequireCommitted(state, options, state.HandoffAuthorizationToken,
            new RecoveryRuntimeEvidence(true, BcdAliasResolution.Resolved, recovery));
        Assert.Throws<RetirementExecutionException>(() => RetirementExecutionAuthorization.RequireCommitted(
            state, options, null, new RecoveryRuntimeEvidence(true, BcdAliasResolution.Resolved, recovery)));
        Assert.Throws<RetirementExecutionException>(() => RetirementExecutionAuthorization.RequireCommitted(
            state, options, Guid.NewGuid().ToString(), new RecoveryRuntimeEvidence(true, BcdAliasResolution.Resolved, recovery)));
        Assert.Throws<RetirementExecutionException>(() => RetirementExecutionAuthorization.RequireCommitted(
            state, options, state.HandoffAuthorizationToken, new RecoveryRuntimeEvidence(false, BcdAliasResolution.Resolved, recovery)));
        Assert.Throws<RetirementExecutionException>(() => RetirementExecutionAuthorization.RequireCommitted(
            state, options, state.HandoffAuthorizationToken,
            new RecoveryRuntimeEvidence(true, BcdAliasResolution.Resolved, Guid.NewGuid())));
    }

    [Fact]
    public void Transition_table_exhaustively_blocks_destructive_ambiguity_shortcuts()
    {
        foreach (var to in Enum.GetValues<RetirementStatus>())
            Assert.Equal(to is RetirementStatus.Boot1Retired or RetirementStatus.RecoveryRequired,
                RetirementStateMachine.IsLegal(RetirementStatus.DestructiveIntent, to));
        foreach (var from in new[] { RetirementStatus.Boot1Retired, RetirementStatus.BcdUpdated, RetirementStatus.Verified })
        {
            Assert.False(RetirementStateMachine.IsLegal(from, RetirementStatus.Failed));
            Assert.False(RetirementStateMachine.IsLegal(from, RetirementStatus.Aborted));
        }
        Assert.False(RetirementStateMachine.IsLegal(RetirementStatus.Boot2Validated, RetirementStatus.DestructiveIntent));
    }

    [Fact]
    public void Bootsequence_parser_preserves_duplicates_and_rejects_unknown_or_localized_output()
    {
        const string id = "{11111111-1111-1111-1111-111111111111}";
        var english = BcdBootSequenceParser.Parse(
            "Windows Boot Manager\r\n--------------------\r\nidentifier {9dea862c-5cdd-4e70-acc1-f32b344d4795}\r\n" +
            $"bootsequence {id}\r\n             {id}\r\ntimeout 5\r\n");
        Assert.True(english.Confident);
        Assert.Equal(2, english.Identifiers.Count);
        Assert.False(BcdBootSequenceParser.Parse(
            "Windows-Start-Manager\r\n--------------------\r\nBezeichner {9dea862c-5cdd-4e70-acc1-f32b344d4795}\r\n").Confident);
        Assert.False(BcdBootSequenceParser.Parse("unexpected").Confident);
    }

    [Fact]
    public void Boot2_default_requires_windows_loader_and_exact_device_gpt_bindings()
    {
        var boot2 = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var boot2Gpt = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var state = new RetirementState
        {
            Boot2BcdObjectId = BcdIdentifiers.Format(boot2),
            Boot2Identity = new PartitionIdentity { GptPartitionId = VolumeLocator.FormatGptId(boot2Gpt) }
        };
        BcdSnapshot Snapshot(BcdObjectKind kind, Guid deviceGpt) => new(
            [
                new BcdEntryIdentity
                {
                    ObjectId = boot2,
                    FormattedId = BcdIdentifiers.Format(boot2),
                    Kind = kind,
                    Path = @"\Windows\system32\winload.efi",
                    SystemRoot = @"\Windows",
                    Device = $"partition={{{deviceGpt:D}}}",
                    OsDevice = $"partition={{{deviceGpt:D}}}"
                }
            ],
            boot2, boot2, true, [], BcdAliasResolution.Resolved, BcdAliasResolution.Resolved);

        Assert.True(Boot2DefaultInvariant.Verify(state, Snapshot(BcdObjectKind.WindowsLoader, boot2Gpt), "test").Passed);
        Assert.False(Boot2DefaultInvariant.Verify(state, Snapshot(BcdObjectKind.RecoveryLoader, boot2Gpt), "test").Passed);
        Assert.False(Boot2DefaultInvariant.Verify(state, Snapshot(BcdObjectKind.WindowsLoader, Guid.NewGuid()), "test").Passed);
    }

    private static RetirementState Authorized(CleanSwitchOptions options)
    {
        var state = new RetirementState
        {
            Status = RetirementStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            MachineName = "test",
            Boot1BcdObjectId = "{11111111-1111-1111-1111-111111111111}",
            Boot2BcdObjectId = options.Boot2Guid,
            RecoveryId = options.RecoveryGuid,
            HandoffRecoveryBcdObjectId = options.RecoveryGuid,
            HandoffAuthorizationState = HandoffAuthorizationStates.Committed,
            HandoffAuthorizationToken = Guid.NewGuid().ToString("D"),
            HandoffCommittedAtUtc = DateTimeOffset.UtcNow
        };
        state.HandoffAuthorizationBindingSha256 = RetirementExecutionAuthorization.ComputeBinding(state);
        return state;
    }
}
