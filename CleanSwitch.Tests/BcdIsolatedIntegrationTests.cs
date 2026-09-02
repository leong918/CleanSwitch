using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;
using Xunit.Abstractions;

namespace CleanSwitch.Tests;

public sealed class BcdIsolatedIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public BcdIsolatedIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [BcdIntegrationFact]
    public async Task Isolated_store_deletes_only_fake_boot1_guid()
    {
        Assert.True(BcdIntegrationGuard.IsEnabled);
        IsolatedBcdStoreSession? session = null;
        try
        {
            session = IsolatedBcdStoreSession.Create();
            IsolatedBcdStoreSession.CanonicalizeTempStorePath(session.StorePath);
            Assert.NotEqual(session.Boot1Id, session.Boot2Id);
            Assert.NotEqual(session.Boot1Id, session.ExtraId);

            var store = session.CreateStoreSource();
            var before = await store.CaptureAsync();
            Assert.Single(before.WithObjectId(session.Boot1Id));
            Assert.Single(before.WithObjectId(session.Boot2Id));
            Assert.Single(before.WithObjectId(session.ExtraId));
            Assert.True(before.BootManagerPresent);
            Assert.Equal(BcdAliasResolution.Absent, before.CurrentResolution);
            Assert.Equal(session.Boot2Id, before.DefaultObjectId);

            var boot1 = Assert.Single(before.WithObjectId(session.Boot1Id));
            Assert.NotEqual(BcdObjectKind.BootManager, boot1.Kind);
            Assert.NotEqual(BcdObjectKind.RecoveryLoader, boot1.Kind);
            Assert.NotEqual(BcdObjectKind.FirmwareObject, boot1.Kind);
            Assert.NotEqual(BcdObjectKind.FirmwareBootManager, boot1.Kind);
            Assert.False(BcdIdentifiers.IsProtectedObject(boot1.ObjectId));

            var state = BcdFixtures.CompleteState();
            state.Boot1BcdObjectId = BcdIdentifiers.Format(session.Boot1Id);
            state.Boot2BcdObjectId = BcdIdentifiers.Format(session.Boot2Id);
            state.RecoveryId = BcdIdentifiers.Format(session.RecoveryId);

            var log = new RecordingOperationLog();
            var command = (StoreBoundBcdCommand)session.CreateBoundCommand(log);
            _output.WriteLine($"isolated store={session.StorePath}");
            _output.WriteLine($"fake Boot 1={BcdIdentifiers.Format(session.Boot1Id)}");
            _output.WriteLine($"fake Boot 2={BcdIdentifiers.Format(session.Boot2Id)}");
            _output.WriteLine($"unrelated={BcdIdentifiers.Format(session.ExtraId)}");

            var engine = new DestructiveBcdRetirementEngine(
                store,
                command,
                log,
                bcdOperationsImplemented: true);

            var result = await engine.ExecuteAsync(state, true, RetirementFixtures.PassingValidation());
            Assert.Equal(RetirementExecutionKind.Succeeded, result.Kind);
            Assert.Equal(1, command.ExecuteCount);
            Assert.NotNull(command.LastCommandLine);
            Assert.Contains("/store", command.LastCommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(session.StorePath, command.LastCommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BcdIdentifiers.Format(session.Boot1Id), command.LastCommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.False(StoreBoundBcdCommand.LooksLikeBareDelete(command.LastCommandLine));
            Assert.DoesNotContain(@"\EFI\Microsoft\Boot\BCD", command.LastCommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\Boot\BCD", command.LastCommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(session.StorePath, string.Join('\n', log.Entries), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(BcdIdentifiers.Format(session.Boot1Id), string.Join('\n', log.Entries), StringComparison.OrdinalIgnoreCase);

            _output.WriteLine($"command={command.LastCommandLine}");
            foreach (var entry in log.Entries)
            {
                _output.WriteLine(entry);
            }

            var after = await session.CreateStoreSource().CaptureAsync();
            Assert.Empty(after.WithObjectId(session.Boot1Id));
            Assert.Single(after.WithObjectId(session.Boot2Id));
            Assert.Single(after.WithObjectId(session.ExtraId));
            Assert.True(after.BootManagerPresent);
            Assert.Equal(session.Boot2Id, after.DefaultObjectId);
        }
        finally
        {
            var path = session?.StorePath;
            session?.Dispose();
            if (path is not null)
            {
                Assert.False(File.Exists(path), "Temporary BCD store was left behind: " + path);
                _output.WriteLine($"cleaned store={path} exists={File.Exists(path)}");
            }
        }
    }
}
