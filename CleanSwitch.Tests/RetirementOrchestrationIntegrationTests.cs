using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;
using CleanSwitch.Tests.Support.Vhd;
using Xunit.Abstractions;

namespace CleanSwitch.Tests;

public sealed class RetirementOrchestrationIntegrationTests(ITestOutputHelper output)
{
    [CombinedRetirementIntegrationFact]
    public async Task Verified_launcher_contract_delivers_pending_to_recovery_runner_on_bounded_fixtures()
    {
        Assert.True(ProductionRetirementGates.DestructiveOperationsImplemented,
            "This RecoveryRunner integration must run with CleanSwitchLiveTestBuild=true.");
        Assert.True(ProductionRetirementGates.BcdOperationsImplemented);

        DisposableVhdSession? vhd = null;
        IsolatedBcdStoreSession? bcd = null;
        try
        {
            vhd = DisposableVhdSession.Create(output.WriteLine);
            bcd = IsolatedBcdStoreSession.Create();
            var proof = vhd.Reprove();
            Assert.NotEqual(0, proof.DiskNumber);
            Assert.False(proof.HostsRunningSystemVolume);

            var boot1 = BcdIdentifiers.Format(bcd.Boot1Id);
            var boot2 = BcdIdentifiers.Format(bcd.Boot2Id);
            var recovery = BcdIdentifiers.Format(bcd.RecoveryId);
            var options = RetirementFixtures.Options(enableDestructive: true);
            options.Boot2Guid = boot2;
            options.RecoveryGuid = recovery;
            options.RestartDelaySeconds = 0;

            var coordinator = new FakeRetirementCoordinator();
            var state = coordinator.BeginRetirement(
                boot1,
                boot2,
                recovery,
                vhd.Boot1Identity,
                vhd.Boot2Identity);
            var log = new RecordingOperationLog();
            var layout = new SingleDiskGptLayoutSource(vhd.DiskNumber);
            var bcdStore = new ResolvedAliasBcdStoreSource(
                bcd.CreateStoreSource(),
                bcd.RecoveryId);
            var diskCommand = new VhdBoundDiskCommand(vhd, log);
            var bcdCommand = bcd.CreateBoundCommand(log);
            var bootManager = new FakeBootManager();
            var executor = new RetirementExecutor(
                options,
                log,
                layout,
                diskCommand,
                bcdStore,
                bcdCommand,
                bootManager,
                vhd.Identities);
            var runner = new RecoveryRunner(
                bootManager,
                coordinator,
                new DiskValidator(log),
                new BootEntryValidator(bootManager, log),
                executor,
                options,
                log,
                new RetirementHardwareReview(layout, bcdStore, log),
                bcdStore,
                layout);

            Assert.Equal(
                ["--recovery-launch"],
                WinReLauncherContract.RecoveryArguments);
            var result = await runner.RunAsync(
                new RecoveryRunRequest(DryRun: true, ReviewOnly: false, ExecuteDeletion: true));

            output.WriteLine(result.Message);
            Assert.Equal(RecoveryRunOutcome.DryRunCompleted, result.Outcome);
            Assert.Equal(0, diskCommand.ExecuteCount);
            Assert.Equal(RetirementStatus.Phase2BReady, coordinator.State!.Status);
            Assert.False(coordinator.State.DestructiveDeletionPerformed);
            Assert.False(coordinator.State.BcdDeletionPerformed);
            Assert.Single(vhd.CaptureLayout().WithGptId(vhd.Boot1.PartitionGptId));
            var afterBcd = await bcdStore.CaptureAsync();
            Assert.Empty(afterBcd.WithObjectId(bcd.Boot1Id));
            Assert.Single(afterBcd.WithObjectId(bcd.Boot2Id));
            output.WriteLine(
                $"RecoveryRunner accepted PENDING; disk={proof.DiskNumber}; disk0Excluded={proof.DiskNumber != 0}; " +
                $"liveBcdExcluded=True; diskCommands={diskCommand.ExecuteCount}; bcdDeletion={coordinator.State.BcdDeletionPerformed}");
        }
        finally
        {
            var vhdPath = vhd?.VhdxPath;
            var bcdPath = bcd?.StorePath;
            bcd?.Dispose();
            vhd?.Dispose();
            if (vhdPath is not null)
            {
                Assert.False(File.Exists(vhdPath));
            }

            if (bcdPath is not null)
            {
                Assert.False(File.Exists(bcdPath));
            }
        }
    }

    [CombinedRetirementIntegrationFact]
    public async Task Phase2a_to_vhd_phase2b_to_isolated_bcd_phase2c_is_fully_bounded()
    {
        DisposableVhdSession? vhd = null;
        IsolatedBcdStoreSession? bcd = null;
        try
        {
            vhd = DisposableVhdSession.Create(output.WriteLine);
            bcd = IsolatedBcdStoreSession.Create();
            var proof = vhd.Reprove();
            Assert.NotEqual(0, proof.DiskNumber);
            Assert.True(StorageBusProbe.IsVirtualBus(proof.BusType), proof.Describe());
            Assert.False(proof.HostsRunningSystemVolume, proof.Describe());

            var boot1 = BcdIdentifiers.Format(bcd.Boot1Id);
            var boot2 = BcdIdentifiers.Format(bcd.Boot2Id);
            var recovery = BcdIdentifiers.Format(bcd.RecoveryId);
            var options = RetirementFixtures.Options(enableDestructive: true);
            options.Boot2Guid = boot2;
            options.RecoveryGuid = recovery;
            options.RestartDelaySeconds = 0;

            var bootManager = new FakeBootManager();
            var coordinator = new FakeRetirementCoordinator();
            var capture = new VhdPhase2AIdentitySource(boot1, recovery, vhd.Boot1Identity, vhd.Boot2Identity);
            var phase2a = new Phase2AHandoff(
                options,
                bootManager,
                coordinator,
                capture,
                new FakeWinReLauncherValidator());
            var state = await phase2a.ExecuteAsync(
                new BootLayout(new BootEntry(boot1, "Fake Boot 1"), new BootEntry(boot2, "Fake Boot 2")));

            Assert.Equal(RetirementStatus.Pending, state.Status);
            Assert.True(bootManager.RestartCalled);
            Assert.Equal(boot2, bootManager.DefaultBootTarget);
            Assert.Equal(recovery, bootManager.NextBootTarget);

            var beforeDisk = vhd.CaptureLayout();
            var diskCommand = new VhdBoundDiskCommand(vhd);
            var diskEngine = new DestructiveRetirementEngine(
                options,
                new SingleDiskGptLayoutSource(vhd.DiskNumber),
                diskCommand,
                log: null,
                destructiveOperationsImplemented: true,
                identities: vhd.Identities);
            var diskResult = await diskEngine.ExecuteAsync(
                state.Boot1Identity!,
                state.Boot2Identity!,
                RetirementFixtures.PassingValidation(),
                explicitOptIn: true);
            Assert.True(diskResult.DestructiveDeletionOccurred);
            Assert.Equal(1, diskCommand.ExecuteCount);

            var afterDisk = vhd.CaptureLayout();
            Assert.Empty(afterDisk.WithGptId(vhd.Boot1.PartitionGptId));
            foreach (var preserved in vhd.PreservedPartitions)
            {
                var live = Assert.Single(afterDisk.WithGptId(preserved.PartitionGptId));
                Assert.Equal(preserved.PartitionType, live.PartitionType);
                Assert.Equal(preserved.StartingOffset, live.StartingOffset);
                Assert.Equal(preserved.SizeBytes, live.SizeBytes);
                Assert.Equal(preserved.DiskGptId, live.DiskGptId);
            }

            state.Status = RetirementStatus.Boot1Retired;
            state.DestructiveDeletionPerformed = true;
            var bcdCommand = (StoreBoundBcdCommand)bcd.CreateBoundCommand();
            var bcdEngine = new DestructiveBcdRetirementEngine(
                bcd.CreateStoreSource(),
                bcdCommand,
                log: null,
                bcdOperationsImplemented: true);
            var bcdResult = await bcdEngine.ExecuteAsync(
                state,
                explicitOptIn: true,
                RetirementFixtures.PassingValidation());
            Assert.Equal(RetirementExecutionKind.Succeeded, bcdResult.Kind);
            Assert.Equal(1, bcdCommand.ExecuteCount);
            Assert.Contains("/store", bcdCommand.LastCommandLine, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"\EFI\Microsoft\Boot\BCD", bcdCommand.LastCommandLine, StringComparison.OrdinalIgnoreCase);

            var afterBcd = await bcd.CreateStoreSource().CaptureAsync();
            Assert.Empty(afterBcd.WithObjectId(bcd.Boot1Id));
            Assert.Single(afterBcd.WithObjectId(bcd.Boot2Id));
            Assert.Single(afterBcd.WithObjectId(bcd.ExtraId));
            Assert.Equal(bcd.Boot2Id, afterBcd.DefaultObjectId);

            output.WriteLine($"Phase2A state schema={state.SchemaVersion} boot1={boot1} boot2={boot2} recovery={recovery}");
            output.WriteLine($"Phase2B disk={vhd.DiskNumber} physical={proof.PhysicalDrivePath} commandCount={diskCommand.ExecuteCount}");
            output.WriteLine($"Phase2C isolatedStore={bcd.StorePath} command={bcdCommand.LastCommandLine}");
            output.WriteLine($"Disk 0 excluded={vhd.DiskNumber != 0}; live BCD excluded=True");
        }
        finally
        {
            var vhdPath = vhd?.VhdxPath;
            var bcdPath = bcd?.StorePath;
            bcd?.Dispose();
            vhd?.Dispose();
            if (vhdPath is not null)
            {
                Assert.False(File.Exists(vhdPath), "Temporary VHDX was left behind: " + vhdPath);
            }

            if (bcdPath is not null)
            {
                Assert.False(File.Exists(bcdPath), "Temporary BCD store was left behind: " + bcdPath);
            }
        }
    }

    private sealed class VhdPhase2AIdentitySource(
        string boot1,
        string recovery,
        PartitionIdentity boot1Identity,
        PartitionIdentity boot2Identity) : IBootEntryValidator
    {
        public Task<RecoveryEntryResolution> ResolveRecoveryEntryAsync(string? configuredGuid)
        {
            var report = new ValidationReport("recovery");
            report.Add("configured", configuredGuid == recovery, $"configured={configuredGuid}; expected={recovery}");
            return Task.FromResult(new RecoveryEntryResolution(
                configuredGuid == recovery ? recovery : null,
                null,
                report));
        }

        public Task<PartitionIdentity?> TryDescribeBootEntryVolumeAsync(string bootGuid) =>
            Task.FromResult<PartitionIdentity?>(
                string.Equals(bootGuid, boot1, StringComparison.OrdinalIgnoreCase)
                    ? boot1Identity
                    : boot2Identity);
    }

    /// <summary>
    /// A /createstore file has no live {current} alias. The combined fixture models WinRE
    /// explicitly by resolving {current} to the isolated recovery loader, while every BCD
    /// enumeration and delete still goes to the temporary /store file.
    /// </summary>
    private sealed class ResolvedAliasBcdStoreSource(IBcdStoreSource inner, Guid current) : IBcdStoreSource
    {
        public async Task<BcdSnapshot> CaptureAsync()
        {
            var snapshot = await inner.CaptureAsync();
            return new BcdSnapshot(
                snapshot.Entries,
                current,
                snapshot.DefaultObjectId,
                snapshot.BootManagerPresent,
                snapshot.Warnings,
                BcdAliasResolution.Resolved,
                snapshot.DefaultResolution);
        }
    }
}
