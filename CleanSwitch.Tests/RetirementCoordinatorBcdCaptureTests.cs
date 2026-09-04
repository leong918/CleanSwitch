using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class RetirementCoordinatorBcdCaptureTests
{
    [Fact]
    public void BeginRetirement_refuses_alias_and_does_not_write_state()
    {
        using var workspace = new TempStateWorkspace();
        var coordinator = workspace.CreateCoordinator();

        var exception = Assert.Throws<RetirementStateException>(() =>
            coordinator.BeginRetirement(
                "{current}",
                BcdIdentifiers.Format(BcdFixtures.Boot2),
                BcdIdentifiers.Format(BcdFixtures.Recovery),
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity()));

        Assert.Contains("concrete object GUID", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(coordinator.StateFilePath));
    }

    [Fact]
    public void BeginRetirement_stores_schema_v2_concrete_bcd_object_ids()
    {
        using var workspace = new TempStateWorkspace();
        var coordinator = workspace.CreateCoordinator();
        var boot1 = BcdIdentifiers.Format(BcdFixtures.Boot1);
        var boot2 = BcdIdentifiers.Format(BcdFixtures.Boot2);

        var state = coordinator.BeginRetirement(
            boot1,
            boot2,
            BcdIdentifiers.Format(BcdFixtures.Recovery),
            RetirementFixtures.Boot1Identity(),
            RetirementFixtures.Boot2Identity());

        Assert.Equal(RetirementState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(4, state.SchemaVersion);
        Assert.Equal(boot1, state.Boot1Id);
        Assert.Equal(boot2, state.Boot2Id);
        Assert.Equal(boot1, state.Boot1BcdObjectId);
        Assert.Equal(boot2, state.Boot2BcdObjectId);

        var json = File.ReadAllText(coordinator.StateFilePath);
        Assert.Contains("\"schemaVersion\": 4", json, StringComparison.Ordinal);
        Assert.Contains("\"boot1BcdObjectId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"boot2BcdObjectId\":", json, StringComparison.Ordinal);
        Assert.Contains(boot1, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{current}", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows 11", json, StringComparison.Ordinal);

        var loaded = coordinator.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(boot1, loaded.Boot1BcdObjectId);
        Assert.Equal(boot2, loaded.Boot2BcdObjectId);
        Assert.Equal(4, loaded.SchemaVersion);
        AssertCompleteDestructiveIdentity(loaded.Boot1Identity, RetirementFixtures.Boot1Identity());
        AssertCompleteDestructiveIdentity(loaded.Boot2Identity, RetirementFixtures.Boot2Identity());
        Assert.Contains("\"diskGptUniqueId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"partitionStartingOffset\":", json, StringComparison.Ordinal);
        Assert.Contains("\"partitionSizeBytes\":", json, StringComparison.Ordinal);
        Assert.Contains("\"gptPartitionType\":", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("diskGpt")]
    [InlineData("gpt")]
    [InlineData("type")]
    [InlineData("offset")]
    [InlineData("size")]
    public void BeginRetirement_refuses_incomplete_boot1_identity_and_does_not_write(string missingField)
    {
        using var workspace = new TempStateWorkspace();
        var coordinator = workspace.CreateCoordinator();
        var boot1 = ClearField(RetirementFixtures.Boot1Identity(), missingField);

        var exception = Assert.Throws<RetirementStateException>(() =>
            coordinator.BeginRetirement(
                BcdIdentifiers.Format(BcdFixtures.Boot1),
                BcdIdentifiers.Format(BcdFixtures.Boot2),
                BcdIdentifiers.Format(BcdFixtures.Recovery),
                boot1,
                RetirementFixtures.Boot2Identity()));

        Assert.Contains(RetirementStateIdentityRequirements.IncompletePendingMessage, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(coordinator.StateFilePath));
    }

    [Theory]
    [InlineData("diskGpt")]
    [InlineData("gpt")]
    [InlineData("type")]
    [InlineData("offset")]
    [InlineData("size")]
    public void BeginRetirement_refuses_incomplete_boot2_identity_and_does_not_write(string missingField)
    {
        using var workspace = new TempStateWorkspace();
        var coordinator = workspace.CreateCoordinator();
        var boot2 = ClearField(RetirementFixtures.Boot2Identity(), missingField);

        var exception = Assert.Throws<RetirementStateException>(() =>
            coordinator.BeginRetirement(
                BcdIdentifiers.Format(BcdFixtures.Boot1),
                BcdIdentifiers.Format(BcdFixtures.Boot2),
                BcdIdentifiers.Format(BcdFixtures.Recovery),
                RetirementFixtures.Boot1Identity(),
                boot2));

        Assert.Contains(RetirementStateIdentityRequirements.IncompletePendingMessage, exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(coordinator.StateFilePath));
    }

    [Theory]
    [InlineData("", "{bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2}")]
    [InlineData("{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1}", "")]
    [InlineData("{current}", "{bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbb2}")]
    [InlineData("{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1}", "{default}")]
    public void BeginRetirement_refuses_missing_or_alias_bcd_and_does_not_write(string boot1Id, string boot2Id)
    {
        using var workspace = new TempStateWorkspace();
        var coordinator = workspace.CreateCoordinator();

        var exception = Assert.Throws<RetirementStateException>(() =>
            coordinator.BeginRetirement(
                boot1Id,
                boot2Id,
                BcdIdentifiers.Format(BcdFixtures.Recovery),
                RetirementFixtures.Boot1Identity(),
                RetirementFixtures.Boot2Identity()));

        Assert.False(File.Exists(coordinator.StateFilePath));
        Assert.True(
            exception.Message.Contains("concrete", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("both the Boot 1 and Boot 2", StringComparison.Ordinal));
    }

    private static PartitionIdentity ClearField(PartitionIdentity identity, string missingField)
    {
        switch (missingField)
        {
            case "diskGpt":
                identity.DiskGptUniqueId = null;
                break;
            case "gpt":
                identity.GptPartitionId = null;
                break;
            case "type":
                identity.GptPartitionType = null;
                break;
            case "offset":
                identity.PartitionStartingOffset = null;
                break;
            case "size":
                identity.PartitionSizeBytes = null;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(missingField), missingField, null);
        }

        return identity;
    }

    private static void AssertCompleteDestructiveIdentity(PartitionIdentity? actual, PartitionIdentity expected)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.DiskGptUniqueId, actual.DiskGptUniqueId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.GptPartitionId, actual.GptPartitionId, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.GptPartitionType, actual.GptPartitionType, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected.PartitionStartingOffset, actual.PartitionStartingOffset);
        Assert.Equal(expected.PartitionSizeBytes, actual.PartitionSizeBytes);
    }

    private sealed class TempStateWorkspace : IDisposable
    {
        public TempStateWorkspace()
        {
            FolderName = $"cs-bcd-capture-{Guid.NewGuid():N}";
            Root = Path.Combine(Path.GetTempPath(), FolderName);
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string FolderName { get; }

        public RetirementCoordinator CreateCoordinator()
        {
            var options = new CleanSwitchOptions
            {
                RecoveryDataPath = Root,
                RecoveryDataFolderName = FolderName,
                StateFileName = "retirement-state-test.json",
                AllowStateOnSystemVolume = true
            };
            var store = new RetirementStateStore(options, new RecordingOperationLog());
            store.EnsureWritable();
            return new RetirementCoordinator(store, new RecordingOperationLog());
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }
}
