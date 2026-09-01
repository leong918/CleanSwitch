using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Vhd;
using Xunit.Abstractions;

namespace CleanSwitch.Tests;

public sealed class VhdDestructiveIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public VhdDestructiveIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [VhdIntegrationFact]
    public async Task Diskpart_deletes_only_fake_boot1_on_temporary_vhdx()
    {
        Assert.True(VhdIntegrationGuard.IsEnabled);
        AssertProductionFlagsRemainDisabled();

        var appAssembly = typeof(RetirementExecutor).Assembly.Location;
        var testAssembly = typeof(VhdDestructiveIntegrationTests).Assembly.Location;
        _output.WriteLine($"appAssembly={appAssembly} writeUtc={File.GetLastWriteTimeUtc(appAssembly):o}");
        _output.WriteLine($"testAssembly={testAssembly} writeUtc={File.GetLastWriteTimeUtc(testAssembly):o}");

        DisposableVhdSession? session = null;
        try
        {
            session = DisposableVhdSession.Create();
            var proof = session.Reprove();
            Assert.NotEqual(0, proof.DiskNumber);
            Assert.True(StorageBusProbe.IsVirtualBus(proof.BusType), proof.Describe());
            Assert.False(proof.HostsRunningSystemVolume, proof.Describe());
            Assert.Equal(session.DiskNumber, proof.DiskNumber);
            Assert.Equal(session.VhdxPath, proof.VhdxPath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal($"\\\\.\\PhysicalDrive{proof.DiskNumber}", proof.PhysicalDrivePath, StringComparer.OrdinalIgnoreCase);
            Assert.NotEqual(PinnedRetirementTargets.Boot1GptId, session.Boot1.PartitionGptId);
            Assert.NotEqual(PinnedRetirementTargets.Boot2GptId, session.Boot2.PartitionGptId);
            Assert.Equal(session.DiskGptId, session.Boot1.DiskGptId);
            _output.WriteLine(proof.Describe());
            _output.WriteLine($"diskGpt={session.DiskGptId:D} boot1Gpt={session.Boot1.PartitionGptId:D}");

            var before = session.CaptureLayout();
            Assert.Single(before.WithGptId(session.Boot1.PartitionGptId));
            Assert.Single(before.WithGptId(session.Boot2.PartitionGptId));
            Assert.Single(before.WithGptId(session.Efi.PartitionGptId));
            Assert.Single(before.WithGptId(session.Msr.PartitionGptId));
            Assert.Single(before.WithGptId(session.Boot1Recovery.PartitionGptId));
            Assert.Single(before.WithGptId(session.Boot2Recovery.PartitionGptId));

            var log = new RecordingOperationLog();
            var command = new VhdBoundDiskCommand(session, log);
            var engine = new DestructiveRetirementEngine(
                RetirementFixtures.Options(enableDestructive: true),
                new SingleDiskGptLayoutSource(session.DiskNumber),
                command,
                log,
                destructiveOperationsImplemented: true,
                session.Identities);

            var result = await engine.ExecuteAsync(
                session.Boot1Identity,
                session.Boot2Identity,
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true);

            Assert.Equal(RetirementExecutionKind.Succeeded, result.Kind);
            Assert.True(result.DestructiveDeletionOccurred);
            Assert.Equal(1, command.ExecuteCount);
            Assert.NotNull(command.LastTarget);
            foreach (var entry in log.Entries)
            {
                _output.WriteLine(entry);
            }
            Assert.Equal(session.Boot1.PartitionGptId, command.LastTarget.TargetGptId);
            Assert.Equal(session.DiskNumber, command.LastTarget.DiskNumber);
            Assert.Equal(session.DiskGptId, command.LastTarget.DiskGptId);
            Assert.NotEqual(0, command.LastTarget.DiskNumber);

            var after = session.CaptureLayout();
            Assert.Empty(after.WithGptId(session.Boot1.PartitionGptId));
            Assert.Single(after.WithGptId(session.Boot2.PartitionGptId));
            Assert.Single(after.WithGptId(session.Efi.PartitionGptId));
            Assert.Single(after.WithGptId(session.Msr.PartitionGptId));
            Assert.Single(after.WithGptId(session.Boot1Recovery.PartitionGptId));
            Assert.Single(after.WithGptId(session.Boot2Recovery.PartitionGptId));

            foreach (var preserved in session.PreservedPartitions)
            {
                var live = Assert.Single(after.WithGptId(preserved.PartitionGptId));
                Assert.Equal(preserved.PartitionType, live.PartitionType);
                Assert.Equal(preserved.StartingOffset, live.StartingOffset);
                Assert.Equal(preserved.SizeBytes, live.SizeBytes);
                Assert.Equal(preserved.DiskGptId, live.DiskGptId);
            }

            Assert.DoesNotContain(
                after.Partitions,
                part => part.PartitionGptId == PinnedRetirementTargets.Boot1GptId);
        }
        finally
        {
            var path = session?.VhdxPath;
            session?.Dispose();
            if (path is not null)
            {
                Assert.False(File.Exists(path), $"Temporary VHDX was left behind: {path}");
                _output.WriteLine($"cleaned vhdx={path} exists={File.Exists(path)}");
            }

            AssertProductionFlagsRemainDisabled();
        }
    }

    private static void AssertProductionFlagsRemainDisabled()
    {
        var executor = new RetirementExecutor(
            RetirementFixtures.Options(enableDestructive: false),
            new RecordingOperationLog());
        Assert.False(executor.IsDestructiveRetirementAvailable);
        Assert.False(executor.IsConfigEnabled);

        var appsettings = FindRepoAppsettings();
        var json = File.ReadAllText(appsettings);
        Assert.Contains("\"EnableDestructiveRetirement\": false", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"EnableDestructiveRetirement\": true", json, StringComparison.Ordinal);
    }

    private static string FindRepoAppsettings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CleanSwitch", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("CleanSwitch/appsettings.json was not found.");
    }
}
