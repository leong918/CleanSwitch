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
        Assert.Equal(["--recovery-run", "--execute-deletion"], WinReLauncherContract.RecoveryArguments);
        var program = File.ReadAllText(FindRepoFile("CleanSwitch", "Program.cs"));
        Assert.Contains("return RunRecoverySide(new RecoveryRunRequest(", program, StringComparison.Ordinal);
        Assert.Contains("ExecuteDeletion: executeDeletion && !reviewOnly", program, StringComparison.Ordinal);
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
