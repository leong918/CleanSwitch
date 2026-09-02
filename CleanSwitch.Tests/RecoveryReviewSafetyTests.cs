using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class RecoveryReviewSafetyTests
{
    [Fact]
    public async Task Recovery_review_does_not_invoke_disk_command_or_restart()
    {
        var command = new FakeDestructiveDiskCommand();
        var bcdCommand = new FakeDestructiveBcdCommand();
        var boot = new FakeBootManager();
        var coordinator = new FakeRetirementCoordinator();
        var log = new RecordingOperationLog();
        var options = RetirementFixtures.Options(enableDestructive: true);
        var executor = new RetirementExecutor(
            options,
            log,
            new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            command,
            new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            bcdCommand);
        var runner = new RecoveryRunner(
            boot,
            coordinator,
            new DiskValidator(log),
            new BootEntryValidator(boot, log),
            executor,
            options,
            log);

        var result = await runner.RunAsync(new RecoveryRunRequest(
            DryRun: false,
            ReviewOnly: true,
            ExecuteDeletion: true));

        Assert.Equal(RecoveryRunOutcome.Failed, result.Outcome);
        Assert.Contains(RetirementHardwareReview.MustRegenerateMessage, result.Message, StringComparison.Ordinal);
        Assert.Contains("Disk command executed: False", result.Message, StringComparison.Ordinal);
        Assert.Contains("BCD command executed: False", result.Message, StringComparison.Ordinal);
        Assert.Equal(0, command.ExecuteCount);
        Assert.Equal(0, bcdCommand.ExecuteCount);
        Assert.False(boot.RestartCalled);
        Assert.Null(coordinator.LastRetired);
        Assert.DoesNotContain("destructiveDeletionOccurred=true", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_runner_never_invokes_phase_2c_delete()
    {
        var path = FindRecoveryRunnerSource();
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("await _executor.DeleteBoot1BcdEntryAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_executor.DeleteBoot1BcdEntryAsync(", source, StringComparison.Ordinal);
        Assert.Contains("Phase 2C BCD deletion is SKIPPED", source, StringComparison.Ordinal);
    }

    private static string FindRecoveryRunnerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "CleanSwitch", "Recovery", "RecoveryRunner.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "CleanSwitch", "Recovery", "RecoveryRunner.cs"));
    }
}
