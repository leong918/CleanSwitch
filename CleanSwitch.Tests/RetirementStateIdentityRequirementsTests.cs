using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class RetirementStateIdentityRequirementsTests
{
    [Fact]
    public void Complete_identities_are_accepted()
    {
        RetirementStateIdentityRequirements.ValidateForDestructiveExecution(
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity());
    }

    [Theory]
    [InlineData("diskGpt")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("type")]
    [InlineData("gpt")]
    public async Task Engine_refuses_incomplete_legacy_state_without_calling_command(string missingField)
    {
        var boot1 = RetirementFixtures.Boot1Identity();
        switch (missingField)
        {
            case "diskGpt":
                boot1.DiskGptUniqueId = null;
                break;
            case "offset":
                boot1.PartitionStartingOffset = null;
                break;
            case "size":
                boot1.PartitionSizeBytes = null;
                break;
            case "type":
                boot1.GptPartitionType = null;
                break;
            case "gpt":
                boot1.GptPartitionId = null;
                break;
        }

        var command = new FakeDestructiveDiskCommand();
        var engine = new DestructiveRetirementEngine(
            RetirementFixtures.Options(enableDestructive: true),
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            command,
            new RecordingOperationLog(),
            destructiveOperationsImplemented: true);

        var exception = await Assert.ThrowsAsync<RetirementExecutionException>(() =>
            engine.ExecuteAsync(
                boot1,
                RetirementFixtures.Boot2Identity(),
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true));

        Assert.Contains(
            RetirementStateIdentityRequirements.MustRegenerateMessage,
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("must be regenerated", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
    }

    [Fact]
    public void Does_not_backfill_missing_fields_from_live_layout()
    {
        var incomplete = RetirementFixtures.Boot1Identity();
        incomplete.DiskGptUniqueId = null;
        incomplete.PartitionStartingOffset = null;
        incomplete.PartitionSizeBytes = null;
        incomplete.GptPartitionType = null;

        var exception = Assert.Throws<RetirementExecutionException>(() =>
            RetirementStateIdentityRequirements.ValidateForDestructiveExecution(
                incomplete,
                RetirementFixtures.Boot2Identity()));

        Assert.Contains("disk GPT identity", exception.Message, StringComparison.Ordinal);
        Assert.Contains("partition start offset", exception.Message, StringComparison.Ordinal);
        Assert.Contains("partition size", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GPT type", exception.Message, StringComparison.Ordinal);
        Assert.Contains("must be regenerated", exception.Message, StringComparison.Ordinal);
    }
}
