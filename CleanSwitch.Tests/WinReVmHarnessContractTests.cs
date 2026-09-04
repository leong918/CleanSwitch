using System.Diagnostics;
using System.Text.Json;

namespace CleanSwitch.Tests;

public sealed class WinReVmHarnessContractTests
{
    private static string HarnessPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "winre-vm-harness.ps1"));
    private static string FakeProviderPath => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "winre-vm-provider-fake.ps1"));

    [Fact]
    public void Harness_declares_all_required_provider_commands_and_cycle_interface()
    {
        var source = File.ReadAllText(HarnessPath);
        foreach (var command in new[] { "checkpoint", "restore", "start", "stop", "hard-poweroff", "guest-command", "wait-for-guest", "collect-artifacts" })
            Assert.Contains($"'{command}'", source, StringComparison.Ordinal);
        Assert.Contains("[int] $Cycle", source, StringComparison.Ordinal);
        Assert.Contains("[string] $ResultPath", source, StringComparison.Ordinal);
        Assert.Contains("CLEAN_SWITCH_WINRE_VM_CONFIG", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_contains_fail_closed_disposable_disk_and_checkpoint_guards()
    {
        var source = File.ReadAllText(HarnessPath);
        Assert.Contains("disposable=true", source, StringComparison.Ordinal);
        Assert.Contains("attachmentType", source, StringComparison.Ordinal);
        Assert.Contains("hostDiskNumber", source, StringComparison.Ordinal);
        Assert.Contains("physicalDiskId", source, StringComparison.Ordinal);
        Assert.Contains("passThroughDiskId", source, StringComparison.Ordinal);
        Assert.Contains("PhysicalDrive", source, StringComparison.Ordinal);
        Assert.Contains("Approved VM storage root must not be a reparse point", source, StringComparison.Ordinal);
        Assert.Contains("Checkpoint restore proof failed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Cycle_contract_is_destructive_retirement_and_uses_checkpoint_only_as_host_reset()
    {
        var source = File.ReadAllText(HarnessPath);
        foreach (var action in new[] { "pre-retirement", "prepare-seal", "deploy", "review", "commit-winre-deployment", "start-retirement", "verify-retirement" })
            Assert.Contains($"'{action}'", source, StringComparison.Ordinal);
        Assert.Contains("deploymentTransactionId = $deploymentTransactionId", source, StringComparison.Ordinal);
        Assert.Contains("productPath') -cne 'RETIRE SYSTEM'", source, StringComparison.Ordinal);
        Assert.Contains("dispatcherSelectedRetirement", source, StringComparison.Ordinal);
        Assert.Contains("destructiveDeletionCount') -ne 1", source, StringComparison.Ordinal);
        Assert.Contains("retirementStateStatus') -cne 'COMPLETE'", source, StringComparison.Ordinal);
        Assert.Contains("cycle-$CycleNumber-post-evidence-reset", source, StringComparison.Ordinal);
        var collect = source.IndexOf("'collect-artifacts'", source.IndexOf("function Invoke-Cycle", StringComparison.Ordinal), StringComparison.Ordinal);
        var reset = source.IndexOf("cycle-$CycleNumber-post-evidence-reset", StringComparison.Ordinal);
        Assert.True(collect >= 0 && reset > collect, "Pristine checkpoint reset must occur only after evidence collection.");
        Assert.DoesNotContain("verify-winre-smoke", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-GuestAction $Config 'rollback'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'reboot-to-winre'", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Harness_without_explicit_configuration_returns_no_go_and_does_not_invoke_a_provider()
    {
        var resultPath = Path.Combine(Path.GetTempPath(), $"cleanswitch-vm-readiness-{Guid.NewGuid():N}.json");
        try
        {
            var start = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", HarnessPath, "-Readiness", "-ResultPath", resultPath })
                start.ArgumentList.Add(argument);
            start.Environment.Remove("CLEAN_SWITCH_WINRE_VM_CONFIG");
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.NotEqual(0, process.ExitCode);
            Assert.Empty(await stdout);
            Assert.Contains("CLEAN_SWITCH_WINRE_VM_CONFIG is required", await stderr, StringComparison.Ordinal);
            Assert.False(JsonDocument.Parse(File.ReadAllText(resultPath))
                .RootElement.GetProperty("Ready").GetBoolean());
        }
        finally { if (File.Exists(resultPath)) File.Delete(resultPath); }
    }

    [Fact]
    public async Task Readiness_requires_and_proves_checkpoint_restore_on_an_isolated_file_backed_fixture()
    {
        var fixture = CreateFixture("File", null);
        try
        {
            var result = await RunReadinessAsync(fixture);
            Assert.True(result.ExitCode == 0, $"stdout={result.Stdout}; stderr={result.Stderr}");
            using var document = JsonDocument.Parse(File.ReadAllText(fixture.ResultPath));
            Assert.True(document.RootElement.GetProperty("Ready").GetBoolean());
            Assert.True(document.RootElement.GetProperty("CheckpointRestoreProven").GetBoolean());
            Assert.Empty(Directory.GetFiles(fixture.StateRoot, "checkpoint-*.txt"));
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    [Fact]
    public async Task Readiness_rejects_a_provider_disk_with_a_physical_host_identity_before_checkpoint_proof()
    {
        var fixture = CreateFixture("File", "0");
        try
        {
            var result = await RunReadinessAsync(fixture);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("forbidden physical-host identity", result.Stderr,
                StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(fixture.StateRoot, "checkpoint-*.txt"));
            Assert.False(File.Exists(Path.Combine(fixture.StateRoot, "probe.txt")));
        }
        finally { Directory.Delete(fixture.Root, true); }
    }

    private static HarnessFixture CreateFixture(string attachmentType, string? hostDiskNumber)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cleanswitch-vm-harness-{Guid.NewGuid():N}");
        var storage = Path.Combine(root, "vm-storage");
        var state = Path.Combine(root, "provider-state");
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(state);
        var disk = Path.Combine(storage, "guest.vhdx");
        File.WriteAllBytes(disk, new byte[4096]);
        var config = Path.Combine(root, "config.json");
        File.WriteAllText(config, $$"""
            {
              "schemaVersion": 2,
              "disposable": true,
              "vmId": "fake-disposable-vm",
              "vmGuid": "11111111-1111-1111-1111-111111111111",
              "providerScript": "{{FakeProviderPath.Replace("\\", "\\\\")}}",
              "approvedVmStorageRoots": ["{{storage.Replace("\\", "\\\\")}}"],
              "baselineCheckpoint": "baseline",
              "pristineCheckpointGuid": "22222222-2222-2222-2222-222222222222",
              "artifactRoot": "{{Path.Combine(root, "artifacts").Replace("\\", "\\\\")}}",
              "providerTimeoutSeconds": 15,
              "sourceCommit": "b86575c7c2faaeebb81b01a901c4959a07c5ebc8"
            }
            """);
        return new HarnessFixture(root, state, disk, config, Path.Combine(root, "readiness.json"),
            attachmentType, hostDiskNumber);
    }

    private static async Task<ProcessResult> RunReadinessAsync(HarnessFixture fixture)
    {
        var start = new ProcessStartInfo { FileName = "powershell.exe", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", HarnessPath, "-Readiness", "-ResultPath", fixture.ResultPath })
            start.ArgumentList.Add(argument);
        start.Environment["CLEAN_SWITCH_WINRE_VM_CONFIG"] = fixture.ConfigPath;
        start.Environment["CLEAN_SWITCH_FAKE_VM_STATE_ROOT"] = fixture.StateRoot;
        start.Environment["CLEAN_SWITCH_FAKE_VM_DISK_PATH"] = fixture.DiskPath;
        start.Environment["CLEAN_SWITCH_FAKE_VM_ATTACHMENT_TYPE"] = fixture.AttachmentType;
        start.Environment["CLEAN_SWITCH_FAKE_VM_GUID"] = "11111111-1111-1111-1111-111111111111";
        start.Environment["CLEAN_SWITCH_FAKE_VM_CHECKPOINT_GUID"] = "22222222-2222-2222-2222-222222222222";
        if (fixture.HostDiskNumber is not null)
            start.Environment["CLEAN_SWITCH_FAKE_VM_HOST_DISK_NUMBER"] = fixture.HostDiskNumber;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    private sealed record HarnessFixture(string Root, string StateRoot, string DiskPath,
        string ConfigPath, string ResultPath, string AttachmentType, string? HostDiskNumber);
    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
