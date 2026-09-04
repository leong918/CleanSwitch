using System.Diagnostics;
using System.Text.Json;
using CleanSwitch.Tests.Support;

namespace CleanSwitch.Tests;

public sealed class WinReDeploymentVmIntegrationTests
{
    [WinReDeploymentVmIntegrationFact]
    public async Task Three_disposable_VM_cycles_prepare_deploy_review_smoke_and_restore_original()
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
                Assert.True(result.Prepared);
                Assert.True(result.Deployed);
                Assert.True(result.ReviewPassed);
                Assert.True(result.SmokePassed);
                Assert.True(result.RolledBack);
                Assert.Equal(result.OriginalWimSha256, result.RestoredWimSha256, ignoreCase: true);
                Assert.True(result.ProtectedBcdUnchanged);
                Assert.True(result.GptUnchanged);
                Assert.True(result.RetirementStateUnchanged);
            }
            finally
            {
                if (File.Exists(output)) File.Delete(output);
            }
        }
    }

    private sealed record VmCycleResult(
        bool Prepared,
        bool Deployed,
        bool ReviewPassed,
        bool SmokePassed,
        bool RolledBack,
        string OriginalWimSha256,
        string RestoredWimSha256,
        bool ProtectedBcdUnchanged,
        bool GptUnchanged,
        bool RetirementStateUnchanged);
}
