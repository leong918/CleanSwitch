using System.Diagnostics;
using System.Text.Json;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class WinReDeploymentVmIntegrationTests
{
    [WinReDeploymentVmIntegrationFact]
    public async Task Three_disposable_VM_cycles_execute_complete_retirement_then_restore_the_pristine_checkpoint()
    {
        var harness = Environment.GetEnvironmentVariable("CLEAN_SWITCH_WINRE_DEPLOYMENT_VM_HARNESS")!;
        Assert.EndsWith("winre-vm-harness.ps1", Path.GetFileName(harness),
            StringComparison.OrdinalIgnoreCase);
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var output = Path.Combine(Path.GetTempPath(), $"cleanswitch-winre-vm-cycle-{Guid.NewGuid():N}.json");
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                start.ArgumentList.Add("-NoProfile");
                start.ArgumentList.Add("-ExecutionPolicy");
                start.ArgumentList.Add("Bypass");
                start.ArgumentList.Add("-File");
                start.ArgumentList.Add(harness);
                start.ArgumentList.Add("-Cycle");
                start.ArgumentList.Add(cycle.ToString());
                start.ArgumentList.Add("-ResultPath");
                start.ArgumentList.Add(output);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, $"stdout={await stdout}; stderr={await stderr}");

                var result = JsonSerializer.Deserialize<VmCycleResult>(File.ReadAllText(output),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
                Assert.True(result.Passed);
                Assert.Equal(cycle, result.Cycle);
                Assert.True(result.GuestDisksFileBacked);
                Assert.Equal(result.PreparedWimSha256, result.DeployedWimSha256, ignoreCase: true);
                Assert.Equal("COMPLETE", result.RetirementStateStatus);
                Assert.Equal(1, result.DestructiveDeletionCount);
                Assert.InRange(result.BcdDeletionCount, 0, 1);
                Assert.True(result.NoUnresolvedJournal);
                Assert.True(result.CheckpointRestoredAfterEvidence);
                Assert.Matches("^[0-9A-Fa-f]{64}$", result.ArtifactManifestSha256);
            }
            finally
            {
                if (File.Exists(output)) File.Delete(output);
            }
        }
    }

    private sealed record VmCycleResult(
        bool Passed,
        int Cycle,
        bool GuestDisksFileBacked,
        string PreparedWimSha256,
        string DeployedWimSha256,
        string RetirementStateStatus,
        int DestructiveDeletionCount,
        int BcdDeletionCount,
        bool NoUnresolvedJournal,
        bool CheckpointRestoredAfterEvidence,
        string ArtifactManifestSha256);
}
