using CleanSwitch.Recovery;

namespace CleanSwitch.Tests;

public sealed class StartupFlowTests
{
    private const string LegacyReconciliationBlocker =
        "Legacy cross-boot journal reconciliation is not proven: " +
        "explicit --reconcile-legacy-winre-journals has not completed.";

    [Fact]
    public void NormalGuiStartup_WithReconciliationBlocker_ShowsModalAndDoesNotLaunchMainForm()
    {
        var userInterface = new RecordingStartupUserInterface();
        var inventory = BlockedInventory();

        var exitCode = StartupFlow.RunNormalDesktop(() => inventory, userInterface, _ => { });

        Assert.True(userInterface.Initialized);
        Assert.Equal(1, userInterface.SafetyBlockCount);
        Assert.False(userInterface.MainFormLaunched);
        Assert.Equal("CleanSwitch Safety Block", userInterface.Title);
        Assert.Equal(StartupFlow.DeploymentInterlockFailureExitCode, exitCode);
    }

    [Theory]
    [InlineData("--recovery-run")]
    [InlineData("--recovery-launch")]
    [InlineData("--recovery-review")]
    [InlineData("--reconcile-legacy-winre-journals")]
    [InlineData("--winre-deployment-status")]
    public void CliStartup_WithSameBlocker_DoesNotShowMessageBox(string command)
    {
        var userInterface = new RecordingStartupUserInterface();
        var inventory = BlockedInventory();

        var exitCode = StartupFlow.HandleBlockedDeploymentInterlock(
            inventory,
            StartupFlow.IsNormalDesktopStartup([command]),
            userInterface,
            _ => { });

        Assert.Equal(StartupFlow.DeploymentInterlockFailureExitCode, exitCode);
        Assert.Equal(0, userInterface.SafetyBlockCount);
        Assert.False(userInterface.MainFormLaunched);
    }

    [Fact]
    public void ReconciledSafeStartup_LaunchesMainFormNormally()
    {
        var userInterface = new RecordingStartupUserInterface { MainFormExitCode = 0 };
        var inventory = new WinReDeploymentJournalInventory([], []);

        var exitCode = StartupFlow.RunNormalDesktop(() => inventory, userInterface, _ => { });

        Assert.True(userInterface.Initialized);
        Assert.Equal(0, userInterface.SafetyBlockCount);
        Assert.True(userInterface.MainFormLaunched);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public void BlockedStartup_ExitCodeRemainsNonZero()
    {
        var exitCode = StartupFlow.RunNormalDesktop(
            BlockedInventory,
            new RecordingStartupUserInterface(),
            _ => { });

        Assert.NotEqual(0, exitCode);
        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void BlockedStartup_PreservesExactBlockerTextInModalMessage()
    {
        var userInterface = new RecordingStartupUserInterface();

        StartupFlow.RunNormalDesktop(BlockedInventory, userInterface, _ => { });

        Assert.Contains(LegacyReconciliationBlocker, userInterface.Message, StringComparison.Ordinal);
        Assert.Contains("CleanSwitch cannot start normal retirement controls.", userInterface.Message, StringComparison.Ordinal);
        Assert.Contains("No disk/BCD/WinRE change has been performed.", userInterface.Message, StringComparison.Ordinal);
        Assert.Contains("Administrator/service action is required before retirement can be enabled.", userInterface.Message, StringComparison.Ordinal);
    }

    private static WinReDeploymentJournalInventory BlockedInventory()
    {
        return new WinReDeploymentJournalInventory([], [LegacyReconciliationBlocker]);
    }

    private sealed class RecordingStartupUserInterface : IStartupUserInterface
    {
        public bool Initialized { get; private set; }
        public int SafetyBlockCount { get; private set; }
        public bool MainFormLaunched { get; private set; }
        public int MainFormExitCode { get; init; }
        public string Title { get; private set; } = string.Empty;
        public string Message { get; private set; } = string.Empty;

        public void Initialize()
        {
            Initialized = true;
        }

        public void ShowSafetyBlock(string title, string message)
        {
            SafetyBlockCount++;
            Title = title;
            Message = message;
        }

        public int RunMainForm()
        {
            MainFormLaunched = true;
            return MainFormExitCode;
        }
    }
}
