using CleanSwitch.Recovery;

namespace CleanSwitch.Tests;

public sealed class WinReLauncherDispatchTests
{
    [Fact]
    public void Program_dispatches_launcher_modes_before_winforms_startup()
    {
        var source = File.ReadAllText(FindRepoFile("CleanSwitch", "Program.cs"));
        var dispatch = source.IndexOf("if (provisionWinReLauncher || winReLauncherReview)", StringComparison.Ordinal);
        var winForms = source.IndexOf("ApplicationConfiguration.Initialize();", StringComparison.Ordinal);

        Assert.True(dispatch >= 0, "WinRE launcher CLI dispatch is missing.");
        Assert.True(winForms > dispatch, "WinForms startup must occur after WinRE launcher CLI dispatch.");
        Assert.Contains("--provision-winre-launcher", source, StringComparison.Ordinal);
        Assert.Contains("--winre-launcher-review", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase2a_validates_launcher_before_identity_capture_or_pending_write()
    {
        var source = File.ReadAllText(FindRepoFile("CleanSwitch", "Services", "Phase2AHandoff.cs"));
        var launcher = source.IndexOf("_launcherValidator.ValidateAsync(recovery)", StringComparison.Ordinal);
        var capture = source.IndexOf("TryDescribeBootEntryVolumeAsync", StringComparison.Ordinal);
        var pending = source.IndexOf("_coordinator.BeginRetirement", StringComparison.Ordinal);

        Assert.True(launcher >= 0);
        Assert.True(capture > launcher, "WinRE launcher proof must precede GPT identity capture.");
        Assert.True(pending > capture, "PENDING must be created only after launcher and identity proof.");
    }

    [Fact]
    public void Verified_launcher_invokes_recovery_runner_with_explicit_runtime_opt_in()
    {
        Assert.Equal(["--recovery-launch"], WinReLauncherContract.RecoveryArguments);
        var program = File.ReadAllText(FindRepoFile("CleanSwitch", "Program.cs"));
        Assert.Contains("return RunRecoverySide(new RecoveryRunRequest(", program, StringComparison.Ordinal);
        Assert.Contains("ExecuteDeletion: executeDeletion && !reviewOnly", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Deployment_and_smoke_dispatch_remain_noninteractive_and_are_separately_gated()
    {
        var source = File.ReadAllText(FindRepoFile("CleanSwitch", "Program.cs"));
        var desktop = source.IndexOf("if (StartupFlow.IsNormalDesktopStartup(args))", StringComparison.Ordinal);
        var smoke = source.IndexOf("if (recoverySmoke)", StringComparison.Ordinal);
        var incomplete = source.IndexOf(
            "var deploymentInventory = WinReDeploymentJournalDiscovery.Inspect(AppConfiguration.Load())",
            smoke,
            StringComparison.Ordinal);
        var deploy = source.IndexOf("if (deployWinReLauncher)", StringComparison.Ordinal);
        var gui = source.IndexOf("ApplicationConfiguration.Initialize();", StringComparison.Ordinal);

        Assert.True(desktop >= 0, "Normal desktop startup must be explicitly distinguished from CLI modes.");
        Assert.True(smoke > desktop && incomplete > smoke && deploy > incomplete && gui > deploy);
        Assert.Contains("--recovery-smoke", source, StringComparison.Ordinal);
        Assert.Contains("--execute-winre-deployment", source, StringComparison.Ordinal);
        Assert.Contains("--recover-winre-deployment", source, StringComparison.Ordinal);
        Assert.Contains("--winre-deployment-status", source, StringComparison.Ordinal);
        Assert.Contains("--complete-winre-smoke", source, StringComparison.Ordinal);
        var smokeMethod = source[source.IndexOf("private static int RunRecoverySmoke", StringComparison.Ordinal)..
            source.IndexOf("private static int RunWinReDeploymentStatus", StringComparison.Ordinal)];
        Assert.Contains("Attach(allocateIfMissing: false)", smokeMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("PauseIfOwned", smokeMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_build_cannot_execute_WinRE_deployment()
    {
#if CLEANSWITCH_LIVE_TEST_BUILD
        Assert.True(ProductionRetirementGates.WinReDeploymentImplemented);
#else
        Assert.False(ProductionRetirementGates.WinReDeploymentImplemented);
#endif
    }

    private static string FindRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(parts[^1]);
    }
}
