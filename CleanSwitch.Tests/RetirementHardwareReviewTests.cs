using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Tests.Support;
using CleanSwitch.Tests.Support.Bcd;

namespace CleanSwitch.Tests;

public sealed class RetirementHardwareReviewTests
{
    [Fact]
    public void Complete_schema_v2_state_passes_both_phases()
    {
        var disk = new FakeDestructiveDiskCommand();
        var bcd = new FakeDestructiveBcdCommand();
        var review = CreateReview();

        var result = review.Run(CompleteState());

        Assert.True(result.Phase2BReviewPassed, result.Describe());
        Assert.True(result.Phase2CReviewPassed, result.Describe());
        Assert.True(result.OverallPassed, result.Describe());
        Assert.Contains("Overall: PASS", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("Disk command executed: False", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("BCD command executed: False", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("select disk 0", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("select partition 3", result.Describe(), StringComparison.Ordinal);
        Assert.Contains("delete partition override", result.Describe(), StringComparison.Ordinal);
        Assert.Contains(
            $"bcdedit.exe /delete {BcdIdentifiers.Format(BcdFixtures.Boot1)}",
            result.Describe(),
            StringComparison.Ordinal);
        Assert.Contains("Boot1BcdObjectId", result.Describe(), StringComparison.Ordinal);
        Assert.Equal(0, disk.ExecuteCount);
        Assert.Equal(0, bcd.ExecuteCount);
    }

    [Fact]
    public void Legacy_schema_v1_fails_closed()
    {
        var review = CreateReview();
        var state = CompleteState();
        state.SchemaVersion = 1;
        state.Boot1BcdObjectId = null;
        state.Boot2BcdObjectId = null;

        var result = review.Run(state);

        Assert.False(result.OverallPassed);
        Assert.False(result.Phase2BReviewPassed);
        Assert.False(result.Phase2CReviewPassed);
        Assert.Contains(RetirementHardwareReview.MustRegenerateMessage, result.Describe(), StringComparison.Ordinal);
        Assert.Contains("Overall: FAIL", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_state_fails_closed()
    {
        var result = CreateReview().Run(null);

        Assert.False(result.OverallPassed);
        Assert.Contains(RetirementHardwareReview.MustRegenerateMessage, result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Overall_fails_if_phase_2c_fails()
    {
        var snapshot = BcdFixtures.StandardSnapshot(current: BcdFixtures.Boot1);
        var review = CreateReview(bcd: new FakeBcdStoreSource(snapshot));

        var result = review.Run(CompleteState());

        Assert.True(result.Phase2BReviewPassed, result.Describe());
        Assert.False(result.Phase2CReviewPassed, result.Describe());
        Assert.False(result.OverallPassed);
        Assert.DoesNotContain("bcdedit.exe /delete", result.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_does_not_invoke_destructive_commands()
    {
        var disk = new FakeDestructiveDiskCommand();
        var bcdCommand = new FakeDestructiveBcdCommand();
        var log = new RecordingOperationLog();
        var options = RetirementFixtures.Options(enableDestructive: true);
        var layout = new FakeGptLayoutSource(RetirementFixtures.StandardLayout());
        var store = new FakeBcdStoreSource(BcdFixtures.StandardSnapshot());
        var coordinator = new FakeRetirementCoordinator { State = CompleteState() };
        var executor = new RetirementExecutor(options, log, layout, disk, store, bcdCommand);
        var runner = new RecoveryRunner(
            new FakeBootManager(),
            coordinator,
            new DiskValidator(log),
            new BootEntryValidator(new FakeBootManager(), log),
            executor,
            options,
            log,
            new RetirementHardwareReview(layout, store, log));

        var result = await runner.RunAsync(new RecoveryRunRequest(false, true, true));

        Assert.Equal(RecoveryRunOutcome.ReviewCompleted, result.Outcome);
        Assert.Equal(0, disk.ExecuteCount);
        Assert.Equal(0, bcdCommand.ExecuteCount);
        Assert.Contains("Disk command executed: False", result.Message, StringComparison.Ordinal);
        Assert.Contains("BCD command executed: False", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(DiskpartDestructiveDiskCommand)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Hardware_review_source_never_names_destructive_commands()
    {
        var path = FindSource("RetirementHardwareReview.cs");
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("DiskpartDestructiveDiskCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BcdeditDestructiveBcdCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDestructiveDiskCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDestructiveBcdCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LoggedProcess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("diskpart.exe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Program_exposes_hardware_review_switch()
    {
        var source = File.ReadAllText(FindSource("Program.cs", underRecovery: false));
        Assert.Contains("--retirement-hardware-review", source, StringComparison.Ordinal);
        Assert.Contains("--recovery-review", source, StringComparison.Ordinal);
    }

    private static RetirementHardwareReview CreateReview(
        FakeGptLayoutSource? layout = null,
        FakeBcdStoreSource? bcd = null) =>
        new(
            layout ?? new FakeGptLayoutSource(RetirementFixtures.StandardLayout()),
            bcd ?? new FakeBcdStoreSource(BcdFixtures.StandardSnapshot()),
            new RecordingOperationLog());

    private static RetirementState CompleteState()
    {
        var state = BcdFixtures.CompleteState();
        state.Boot1Identity = RetirementFixtures.Boot1Identity();
        state.Boot2Identity = RetirementFixtures.Boot2Identity();
        return state;
    }

    private static string FindSource(string fileName, bool underRecovery = true)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = underRecovery
                ? Path.Combine(dir.FullName, "CleanSwitch", "Recovery", fileName)
                : Path.Combine(dir.FullName, "CleanSwitch", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
