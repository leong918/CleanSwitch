namespace CleanSwitch.Tests;

public sealed class Phase2AHandoffSourceTests
{
    [Fact]
    public void Retire_handoff_sets_survivor_default_before_one_time_recovery_boot()
    {
        var source = File.ReadAllText(FindSource("Services", "Phase2AHandoff.cs"));
        var setDefault = source.IndexOf(
            "SetDefaultBootAsync(layout.Target.Identifier)",
            StringComparison.Ordinal);
        var setRecovery = source.IndexOf(
            "SetNextBootAsync(recovery.Identifier)",
            StringComparison.Ordinal);

        Assert.True(setDefault >= 0, "Phase 2A must set Boot 2 as the persistent default.");
        Assert.True(setRecovery > setDefault, "Boot 2 default must be established before scheduling WinRE.");

        var bootManager = File.ReadAllText(FindSource("Services", "WindowsBootManager.cs"));
        Assert.Contains("[\"/default\", normalizedGuid]", bootManager, StringComparison.Ordinal);
    }

    [Fact]
    public void Pending_handoff_repair_has_only_the_narrow_default_mutation()
    {
        var source = File.ReadAllText(FindSource("Recovery", "PendingHandoffRepair.cs"));

        Assert.Contains("SetDefaultBootAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetNextBootAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RestartAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDestructiveDiskCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IDestructiveBcdCommand", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkFailed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Persist(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Transition(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Repair_review_cli_is_dispatched_and_reports_human_readable_output()
    {
        var program = File.ReadAllText(FindSource(string.Empty, "Program.cs"));
        var project = File.ReadAllText(FindSource(string.Empty, "CleanSwitch.csproj"));

        Assert.Contains("--repair-pending-handoff-review", program, StringComparison.Ordinal);
        Assert.Contains("RunPendingHandoffRepair(repairPendingHandoffReview", program, StringComparison.Ordinal);
        Assert.Contains("Report(result.Describe(reviewOnly))", program, StringComparison.Ordinal);
        Assert.Contains("State SHA256 before:", program, StringComparison.Ordinal);
        Assert.Contains("State SHA256 after :", program, StringComparison.Ordinal);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", project, StringComparison.Ordinal);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>", project, StringComparison.Ordinal);
    }

    private static string FindSource(string folder, string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "CleanSwitch", folder, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(fileName);
    }
}
