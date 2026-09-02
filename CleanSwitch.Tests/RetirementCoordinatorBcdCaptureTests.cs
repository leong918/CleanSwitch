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
        Assert.Equal(2, state.SchemaVersion);
        Assert.Equal(boot1, state.Boot1Id);
        Assert.Equal(boot2, state.Boot2Id);
        Assert.Equal(boot1, state.Boot1BcdObjectId);
        Assert.Equal(boot2, state.Boot2BcdObjectId);

        var json = File.ReadAllText(coordinator.StateFilePath);
        Assert.Contains("\"schemaVersion\": 2", json, StringComparison.Ordinal);
        Assert.Contains("\"boot1BcdObjectId\":", json, StringComparison.Ordinal);
        Assert.Contains("\"boot2BcdObjectId\":", json, StringComparison.Ordinal);
        Assert.Contains(boot1, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("{current}", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Windows 11", json, StringComparison.Ordinal);

        var loaded = coordinator.TryLoad();
        Assert.NotNull(loaded);
        Assert.Equal(boot1, loaded.Boot1BcdObjectId);
        Assert.Equal(boot2, loaded.Boot2BcdObjectId);
        Assert.Equal(2, loaded.SchemaVersion);
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
