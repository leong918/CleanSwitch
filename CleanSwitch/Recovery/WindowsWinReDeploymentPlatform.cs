using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

internal sealed record WinReProtectedBcdSnapshot(
    string CurrentGuid,
    string DefaultGuid,
    string BootManagerSha256,
    string OsLoaderFingerprint,
    string FullBcdText);

public static class WinReDeploymentPlanBuilder
{
    public static async Task<WinReDeploymentPlan> BuildAsync(
        CleanSwitchOptions options,
        string preparedWimPath,
        IOperationLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        log ??= NullOperationLog.Instance;
        preparedWimPath = Path.GetFullPath(preparedWimPath);
        if (!File.Exists(preparedWimPath))
            throw new InvalidOperationException($"Prepared WIM is unavailable: '{preparedWimPath}'.");
        var machinePreparationRoot = Path.GetFullPath(WindowsWinReWorkspaceFactory.MachineRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sourceCopyDirectory = Path.GetDirectoryName(preparedWimPath)!;
        var operationDirectory = Directory.GetParent(sourceCopyDirectory)?.FullName ?? string.Empty;
        if (!preparedWimPath.StartsWith(machinePreparationRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(preparedWimPath), "Winre.wim", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(sourceCopyDirectory), "source-copy", StringComparison.Ordinal) ||
            !Path.GetFileName(operationDirectory).StartsWith("operation-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(operationDirectory)["operation-".Length..], "N", out _))
            throw new InvalidOperationException("Prepared WIM is not in an owned machine-level CleanSwitch preparation workspace.");

        var bundlePath = Path.Combine(
            Directory.GetParent(Path.GetDirectoryName(preparedWimPath)!)?.FullName
                ?? throw new InvalidOperationException("Prepared WIM is not inside a preparation workspace."),
            "prepared-winre-bundle.json");
        if (!File.Exists(bundlePath))
            throw new InvalidOperationException("Prepared WIM has no sealed preparation bundle manifest.");
        var bundle = JsonSerializer.Deserialize<PreparedWinReBundleManifest>(File.ReadAllText(bundlePath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Prepared WIM bundle manifest is unreadable.");
        if (bundle.SchemaVersion != PreparedWinReBundleManifest.CurrentSchemaVersion ||
            !string.Equals(bundle.PreparedWimFileName, Path.GetFileName(preparedWimPath), StringComparison.Ordinal) ||
            bundle.PreparedWimSize != new FileInfo(preparedWimPath).Length ||
            !string.Equals(bundle.PreparedWimSha256, HashFile(preparedWimPath), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prepared WIM does not match its sealed preparation bundle.");

        var services = RetirementServices.CreateForExistingOperation(options, "winre-deploy-plan");
        var state = services.Coordinator.TryLoad()
            ?? throw new InvalidOperationException("A terminal ABORTED retirement state is required before WinRE deployment.");
        if (!state.IsTerminal || state.Status != RetirementStatus.Aborted ||
            state.DestructiveDeletionPerformed || state.BcdDeletionPerformed)
        {
            throw new InvalidOperationException("WinRE deployment requires an untouched terminal ABORTED retirement operation.");
        }

        var layout = await services.BootManager.DetectAsync(options.Boot2Guid);
        if (!BcdIdentifiers.IdsEqual(layout.Current.Identifier, options.Boot2Guid))
            throw new InvalidOperationException("WinRE deployment must run from configured Boot 2.");

        var recovery = await services.BootEntryValidator.ResolveRecoveryEntryAsync(options.RecoveryGuid);
        if (recovery.Identifier is null || recovery.Entry is null ||
            !WinReImagePathResolver.TryResolve(recovery.Entry, out var liveWim, out var diagnostic))
            throw new InvalidOperationException("RecoveryGuid/live WIM resolution failed closed: " + recovery.Report.Describe());
        log.Info("winre-deploy-plan", diagnostic);
        var expected = WindowsWinReLauncherValidator.CreateCurrentExpectation(options, recovery.Identifier);
        if (!LauncherManifestMatches(bundle.Launcher, expected.Manifest) ||
            !string.Equals(Path.GetFullPath(bundle.OriginalLiveWimPath), Path.GetFullPath(liveWim), StringComparison.OrdinalIgnoreCase) ||
            bundle.OriginalLiveWimSize != new FileInfo(liveWim).Length ||
            !string.Equals(bundle.OriginalLiveWimSha256, HashFile(liveWim), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prepared bundle is stale or belongs to another build, configuration, RecoveryGuid, or live WIM.");

        var located = LocateVolumeForPath(liveWim);
        if (located.GptPartitionGuid is null)
            throw new InvalidOperationException("The registered WinRE WIM is not on one uniquely identified GPT volume.");

        var bcd = await WindowsWinReDeploymentPlatform.CaptureProtectedBcdAsync(log);
        var gptFingerprint = WindowsWinReDeploymentPlatform.CaptureGptFingerprint();
        var statePath = services.Coordinator.StateFilePath;
        var dataRoot = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException("RecoveryData root is unavailable.");
        var transactionId = Guid.NewGuid().ToString("N");
        var archive = Path.Combine(dataRoot, "winre-deployment-archive", transactionId);
        var liveFull = Path.GetFullPath(liveWim);
        var recoveryDirectory = Path.GetDirectoryName(liveFull)
            ?? throw new InvalidOperationException("Registered WinRE directory is unavailable.");

        return new WinReDeploymentPlan
        {
            TransactionId = transactionId,
            PreparedWimPath = preparedWimPath,
            PreparedWimSha256 = bundle.PreparedWimSha256,
            PreparedBundlePath = Path.GetFullPath(bundlePath),
            PreparedBundleSha256 = HashFile(bundlePath),
            LiveWimPath = liveFull,
            OriginalWimSha256 = bundle.OriginalLiveWimSha256,
            BackupWimPath = Path.Combine(archive, "original", "Winre.wim"),
            IncomingWimPath = Path.Combine(recoveryDirectory, "Winre.wim.incoming"),
            RecoveryDirectory = recoveryDirectory,
            ExpectedRecoveryGuid = recovery.Identifier,
            Boot2Guid = BcdIdentifiers.Format(Guid.Parse(options.Boot2Guid)),
            RetirementStateSha256 = HashFile(statePath),
            ProtectedBcdFingerprint = WindowsWinReDeploymentPlatform.Fingerprint(bcd),
            GptLayoutFingerprint = gptFingerprint,
            RecoveryPartitionGptId = VolumeLocator.FormatGptId(located.GptPartitionGuid.Value),
            RecoveryDataVolumeGptId = options.RecoveryDataVolumeGptId,
            ProductVersion = bundle.Launcher.ProductVersion,
            ExecutableSha256 = bundle.Launcher.ExecutableSha256,
            ConfigurationSha256 = bundle.Launcher.ConfigurationSha256
        };
    }

    private static bool LauncherManifestMatches(WinReLauncherManifest actual, WinReLauncherManifest expected) =>
        actual.SchemaVersion == expected.SchemaVersion &&
        string.Equals(actual.RecoveryGuid, expected.RecoveryGuid, StringComparison.Ordinal) &&
        string.Equals(actual.ExecutableRelativePath, expected.ExecutableRelativePath, StringComparison.Ordinal) &&
        string.Equals(actual.ConfigurationRelativePath, expected.ConfigurationRelativePath, StringComparison.Ordinal) &&
        string.Equals(actual.FallbackExecutablePath, expected.FallbackExecutablePath, StringComparison.Ordinal) &&
        string.Equals(actual.ExecutableSha256, expected.ExecutableSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.ConfigurationSha256, expected.ConfigurationSha256, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.ProductVersion, expected.ProductVersion, StringComparison.Ordinal) &&
        string.Equals(actual.RecoveryDataVolumeGptId, expected.RecoveryDataVolumeGptId, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.RecoveryDataFolderName, expected.RecoveryDataFolderName, StringComparison.Ordinal) &&
        actual.Arguments.SequenceEqual(expected.Arguments, StringComparer.Ordinal);

    private static LocatedVolume LocateVolumeForPath(string path)
    {
        var volumePath = VolumeIdentity.TryGetVolumeGuidPath(path)
            ?? throw new InvalidOperationException("The registered WIM path has no resolvable volume identity.");
        var matches = VolumeLocator.Enumerate().Volumes.Where(candidate =>
            VolumeIdentity.AreSameVolume(candidate.VolumeGuidPath, volumePath)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidOperationException($"Registered WIM volume resolved to {matches.Length} candidates.");
    }

    internal static string HashFile(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

/// <summary>Windows implementation. It never imports a BCD store and never runs diskpart.</summary>
public sealed class WindowsWinReDeploymentPlatform : IWinReDeploymentPlatform
{
    private readonly CleanSwitchOptions _options;
    private readonly IOperationLog _log;
    private WinReProtectedBcdSnapshot? _protectedBcd;

    public WindowsWinReDeploymentPlatform(CleanSwitchOptions options, IOperationLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<WinReDeploymentVerification> VerifyD0Async(WinReDeploymentPlan plan)
    {
        var pathValidation = ValidatePlanPaths(plan);
        if (!pathValidation.Passed) return pathValidation;
        var servicing = WindowsServicingPreflight.DescribeBlocker();
        if (servicing is not null) return Fail(servicing);
        if (Path.GetFullPath(plan.PreparedWimPath).Equals(Path.GetFullPath(plan.LiveWimPath), StringComparison.OrdinalIgnoreCase))
            return Fail("Prepared and live WIM paths are identical.");
        if (!File.Exists(plan.PreparedWimPath) || !File.Exists(plan.LiveWimPath))
            return Fail("Prepared or live WIM is missing.");
        if (!HashEquals(plan.PreparedBundlePath, plan.PreparedBundleSha256))
            return Fail("Sealed preparation bundle manifest hash drifted.");
        if (File.Exists(plan.IncomingWimPath)) return Fail("A stale incoming WIM exists.");
        var attributes = File.GetAttributes(plan.PreparedWimPath);
        const FileAttributes rejected = FileAttributes.ReparsePoint | FileAttributes.Compressed | FileAttributes.Encrypted;
        if ((attributes & rejected) != 0) return Fail("Prepared WIM has reparse/compressed/encrypted attributes.");
        if (!PathIsOnExpectedGpt(plan.LiveWimPath, plan.RecoveryPartitionGptId) ||
            !PathIsOnExpectedGpt(plan.BackupWimPath, plan.RecoveryDataVolumeGptId))
            return Fail("Live WinRE or backup path does not resolve to its persisted GPT identity.");
        if (!HasDeploymentCapacity(plan, out var capacityDiagnostic)) return Fail(capacityDiagnostic);
        if (!HashEquals(plan.PreparedWimPath, plan.PreparedWimSha256) ||
            !HashEquals(plan.LiveWimPath, plan.OriginalWimSha256))
            return Fail("Prepared or original WIM hash drifted before deployment.");

        var statePath = RetirementStateStore.ResolveStateFilePath(_options);
        if (!HashEquals(statePath, plan.RetirementStateSha256)) return Fail("Retirement state hash drifted.");
        if (!string.Equals(CaptureGptFingerprint(), plan.GptLayoutFingerprint, StringComparison.OrdinalIgnoreCase))
            return Fail("GPT layout drifted before deployment.");
        var state = RetirementServices.CreateForExistingOperation(_options, "winre-deploy-d0").Coordinator.TryLoad();
        if (state is null || !state.IsTerminal || state.Status != RetirementStatus.Aborted ||
            state.DestructiveDeletionPerformed || state.BcdDeletionPerformed)
            return Fail("Retirement state is not untouched terminal ABORTED.");

        var bcd = await CaptureProtectedBcdAsync(_log);
        if (!string.Equals(Fingerprint(bcd), plan.ProtectedBcdFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !BcdIdentifiers.IdsEqual(bcd.CurrentGuid, plan.Boot2Guid))
            return Fail("Protected BCD/current-loader invariants drifted.");
        _protectedBcd = bcd;
        return Pass("D0 identity, state, build, WIM and protected BCD checks passed.");
    }

    public async Task<WinReDeploymentVerification> CaptureSnapshotsAsync(WinReDeploymentPlan plan)
    {
        var audit = Path.GetDirectoryName(Path.GetDirectoryName(plan.BackupWimPath)!)!;
        Directory.CreateDirectory(audit);
        var protectedBcd = await CaptureProtectedBcdAsync(_log);
        if (!string.Equals(Fingerprint(protectedBcd), plan.ProtectedBcdFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !UnchangedStateAndGpt(plan))
            return Fail("BCD, retirement state, or GPT identity drifted between D0 and D1.");
        var reagent = await RunRequiredAsync("reagentc.exe", ["/info"]);
        var firmware = await RunRequiredAsync("bcdedit.exe", ["/enum", "firmware", "/v"]);
        var bcdExportPath = Path.Combine(audit, "bcd-store-before.bak");
        var export = await RunRequiredAsync("bcdedit.exe", ["/export", bcdExportPath]);
        if (export.ExitCode != 0 || !File.Exists(bcdExportPath)) return Fail("Full binary BCD export failed.");
        FlushFile(bcdExportPath);
        WriteDurable(Path.Combine(audit, "reagentc-before.txt"), reagent.StdOut + reagent.StdErr);
        WriteDurable(Path.Combine(audit, "bcd-all-before.txt"), protectedBcd.FullBcdText);
        WriteDurable(Path.Combine(audit, "bcd-firmware-before.txt"), firmware.StdOut + firmware.StdErr);
        WriteDurable(Path.Combine(audit, "deployment-plan.json"), JsonSerializer.Serialize(plan, new JsonSerializerOptions { WriteIndented = true }));
        WriteDurable(Path.Combine(audit, "gpt-layout-fingerprint-before.txt"), plan.GptLayoutFingerprint + Environment.NewLine);
        WriteDurable(Path.Combine(audit, "bcd-store-before.sha256.txt"),
            WinReDeploymentPlanBuilder.HashFile(bcdExportPath) + Environment.NewLine);
        var reagentXml = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "Recovery", "ReAgent.xml");
        if (File.Exists(reagentXml))
        {
            var xmlBackup = Path.Combine(audit, "ReAgent-before.xml");
            File.Copy(reagentXml, xmlBackup, overwrite: false);
            FlushFile(xmlBackup);
            WriteDurable(Path.Combine(audit, "ReAgent-before.sha256.txt"),
                WinReDeploymentPlanBuilder.HashFile(xmlBackup) + Environment.NewLine);
        }
        return Pass("REAgentC and full BCD snapshots durably recorded.");
    }

    public async Task<WinReDeploymentVerification> BackupOriginalAsync(WinReDeploymentPlan plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(plan.BackupWimPath)!);
        File.Copy(plan.LiveWimPath, plan.BackupWimPath, overwrite: false);
        FlushFile(plan.BackupWimPath);
        if (!HashEquals(plan.BackupWimPath, plan.OriginalWimSha256)) return Fail("Original WIM backup hash mismatch.");
        var bcd = await CaptureProtectedBcdAsync(_log);
        if (!string.Equals(Fingerprint(bcd), plan.ProtectedBcdFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !UnchangedStateAndGpt(plan))
            return Fail("BCD, retirement state, or GPT identity drifted before the first mutation.");
        var info = await RunRequiredAsync("dism.exe", ["/English", "/Get-WimInfo", $"/WimFile:{plan.BackupWimPath}"]);
        return info.ExitCode == 0 ? Pass("Original WIM backup is byte-exact and DISM-readable.") : Fail(LoggedProcess.Describe(info));
    }

    public Task DisableAsync(WinReDeploymentPlan plan) => RunMutationAsync("reagentc.exe", ["/disable"]);

    public async Task<WinReDeploymentVerification> VerifyDisabledAsync(WinReDeploymentPlan plan) =>
        await VerifyRegistrationAndBcdAsync(plan, expectedEnabled: false, requireLocation: false);

    public Task RemoveOriginalAsync(WinReDeploymentPlan plan)
    {
        if (!HashEquals(plan.BackupWimPath, plan.OriginalWimSha256)) throw new InvalidOperationException("Verified original backup is unavailable.");
        if (File.Exists(plan.LiveWimPath))
        {
            if (!HashEquals(plan.LiveWimPath, plan.OriginalWimSha256))
                throw new InvalidOperationException("Live original WIM drifted before removal.");
            File.Delete(plan.LiveWimPath);
        }
        return Task.CompletedTask;
    }

    public Task<WinReDeploymentVerification> VerifyOriginalRemovedAsync(WinReDeploymentPlan plan) =>
        Task.FromResult(!File.Exists(plan.LiveWimPath) && HashEquals(plan.BackupWimPath, plan.OriginalWimSha256) && UnchangedStateAndGpt(plan)
            ? Pass("Original absent and verified backup present.") : Fail("Original-removal state is not proven."));

    public async Task CopyIncomingAsync(WinReDeploymentPlan plan, Action duringCopy)
    {
        if (File.Exists(plan.IncomingWimPath)) throw new InvalidOperationException("Incoming WIM path already exists.");
        await using var source = new FileStream(plan.PreparedWimPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        await using var destination = new FileStream(plan.IncomingWimPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.WriteThrough);
        var buffer = new byte[1024 * 1024];
        var injected = false;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read));
            if (!injected)
            {
                await destination.FlushAsync();
                duringCopy();
                injected = true;
            }
        }
        destination.Flush(flushToDisk: true);
        if (!injected) duringCopy();
    }

    public async Task<WinReDeploymentVerification> VerifyIncomingAsync(WinReDeploymentPlan plan)
    {
        if (!HashEquals(plan.IncomingWimPath, plan.PreparedWimSha256) || !UnchangedStateAndGpt(plan))
            return Fail("Incoming WIM hash, retirement state, or GPT layout mismatch.");
        var info = await RunRequiredAsync("dism.exe", ["/English", "/Get-WimInfo", $"/WimFile:{plan.IncomingWimPath}"]);
        return info.ExitCode == 0 ? Pass("Incoming WIM hash and index metadata verified.") : Fail(LoggedProcess.Describe(info));
    }

    public Task ActivateIncomingAsync(WinReDeploymentPlan plan)
    {
        if (File.Exists(plan.LiveWimPath)) throw new InvalidOperationException("Final WIM path unexpectedly exists.");
        File.Move(plan.IncomingWimPath, plan.LiveWimPath);
        return Task.CompletedTask;
    }

    public Task<WinReDeploymentVerification> VerifyFinalInstalledAsync(WinReDeploymentPlan plan) =>
        Task.FromResult(HashEquals(plan.LiveWimPath, plan.PreparedWimSha256) && !File.Exists(plan.IncomingWimPath) && UnchangedStateAndGpt(plan)
            ? Pass("Final prepared WIM hash verified.") : Fail("Final WIM activation was not proven."));

    public Task SetReImageAsync(WinReDeploymentPlan plan) =>
        RunMutationAsync("reagentc.exe", ["/setreimage", "/path", plan.RecoveryDirectory]);

    public async Task<WinReDeploymentVerification> VerifySetReImageAsync(WinReDeploymentPlan plan) =>
        await VerifyRegistrationAndBcdAsync(plan, expectedEnabled: false, requireLocation: true);

    public Task EnableAsync(WinReDeploymentPlan plan) => RunMutationAsync("reagentc.exe", ["/enable"]);

    public async Task<WinReDeploymentVerification> VerifyEnabledAsync(WinReDeploymentPlan plan)
    {
        var verified = await VerifyRegistrationAndBcdAsync(plan, expectedEnabled: true, requireLocation: true);
        if (!verified.Passed) return verified;
        var boot = new WindowsBootManager(_log);
        var recovery = await new BootEntryValidator(boot, _log).ResolveRecoveryEntryAsync(null);
        return recovery.Identifier is null
            ? Fail("Enabled WinRE has no uniquely resolved RecoveryGuid.")
            : verified with { RecoveryGuid = recovery.Identifier };
    }

    public async Task<WinReDeploymentVerification> ReviewLauncherAsync(WinReDeploymentPlan plan)
    {
        var boot = new WindowsBootManager(_log);
        var recovery = await new BootEntryValidator(boot, _log).ResolveRecoveryEntryAsync(plan.ExpectedRecoveryGuid);
        if (recovery.Identifier is null || recovery.Entry is null) return Fail(recovery.Report.Describe());
        var result = await new WindowsWinReLauncherValidator(_options, _log).ValidateAsync(recovery);
        return result.Passed ? Pass(result.Report.Describe()) : Fail(result.Report.Describe());
    }

    public async Task<WinReDeploymentVerification> VerifyPostSmokeAsync(WinReDeploymentPlan plan)
    {
        if (!HashEquals(plan.LiveWimPath, plan.PreparedWimSha256)) return Fail("Prepared live WIM hash drifted after smoke.");
        var statePath = RetirementStateStore.ResolveStateFilePath(_options);
        if (!HashEquals(statePath, plan.RetirementStateSha256)) return Fail("Retirement state changed during smoke.");
        var enabled = await VerifyEnabledAsync(plan);
        if (!enabled.Passed || !BcdIdentifiers.IdsEqual(enabled.RecoveryGuid, plan.ExpectedRecoveryGuid))
            return Fail("Recovery registration/GUID or protected BCD invariants changed during smoke.");
        return await ReviewLauncherAsync(plan);
    }

    public async Task RollbackAsync(WinReDeploymentPlan plan)
    {
        var pathValidation = ValidatePlanPaths(plan);
        if (!pathValidation.Passed) throw new InvalidOperationException(pathValidation.Detail);
        if (!HashEquals(plan.BackupWimPath, plan.OriginalWimSha256))
            throw new InvalidOperationException("Rollback refuses because the original backup hash is unavailable.");

        var info = await RunRequiredAsync("reagentc.exe", ["/info"]);
        if (!TryParseEnabled(info.StdOut, out var enabled))
            throw new InvalidOperationException("Rollback cannot parse the current REAgentC enabled state.");
        if (enabled) await RunMutationAsync("reagentc.exe", ["/disable"]);
        if (File.Exists(plan.IncomingWimPath)) File.Delete(plan.IncomingWimPath);
        if (File.Exists(plan.LiveWimPath))
        {
            if (!HashEquals(plan.LiveWimPath, plan.PreparedWimSha256) &&
                !HashEquals(plan.LiveWimPath, plan.OriginalWimSha256))
                throw new InvalidOperationException("Rollback found an unrecognized live WIM and will not overwrite it.");
            File.Delete(plan.LiveWimPath);
        }

        var rollbackIncoming = plan.LiveWimPath + ".rollback-incoming";
        if (File.Exists(rollbackIncoming)) File.Delete(rollbackIncoming);
        File.Copy(plan.BackupWimPath, rollbackIncoming, overwrite: false);
        FlushFile(rollbackIncoming);
        if (!HashEquals(rollbackIncoming, plan.OriginalWimSha256))
            throw new InvalidOperationException("Rollback incoming WIM hash mismatch.");
        File.Move(rollbackIncoming, plan.LiveWimPath);
        await RunMutationAsync("reagentc.exe", ["/setreimage", "/path", plan.RecoveryDirectory]);
        await RunMutationAsync("reagentc.exe", ["/enable"]);
    }

    public async Task<WinReDeploymentVerification> VerifyRollbackAsync(WinReDeploymentPlan plan)
    {
        if (!HashEquals(plan.LiveWimPath, plan.OriginalWimSha256)) return Fail("Original live WIM hash was not restored.");
        var info = await RunRequiredAsync("dism.exe", ["/English", "/Get-WimInfo", $"/WimFile:{plan.LiveWimPath}"]);
        if (info.ExitCode != 0) return Fail("Restored original WIM is not DISM-readable: " + LoggedProcess.Describe(info));
        var verification = await VerifyRegistrationAndBcdAsync(plan, expectedEnabled: true, requireLocation: true);
        if (!verification.Passed) return verification;
        var boot = new WindowsBootManager(_log);
        var recovery = await new BootEntryValidator(boot, _log).ResolveRecoveryEntryAsync(null);
        return recovery.Identifier is null
            ? Fail("Rollback restored the original WIM but could not resolve the enabled recovery registration.")
            : new WinReDeploymentVerification(true,
                "Original WIM hash restored; WinRE enabled; protected Boot1/Boot2/default/displayorder invariants preserved.",
                recovery.Identifier);
    }

    private async Task<WinReDeploymentVerification> VerifyRegistrationAndBcdAsync(
        WinReDeploymentPlan plan, bool expectedEnabled, bool requireLocation)
    {
        var info = await RunRequiredAsync("reagentc.exe", ["/info"]);
        if (!TryParseEnabled(info.StdOut, out var enabled) || enabled != expectedEnabled)
            return Fail($"REAgentC enabled state is missing, ambiguous, or not {expectedEnabled}.");
        if (requireLocation && !ContainsPath(info.StdOut, plan.RecoveryDirectory))
            return Fail("REAgentC did not report the exact registered recovery directory.");
        if (!PathIsOnExpectedGpt(plan.LiveWimPath, plan.RecoveryPartitionGptId))
            return Fail("Registered WinRE path no longer resolves to the persisted recovery-partition GPT identity.");
        var bcd = await CaptureProtectedBcdAsync(_log);
        if (!string.Equals(Fingerprint(bcd), plan.ProtectedBcdFingerprint, StringComparison.OrdinalIgnoreCase))
            return Fail("Protected BCD loader/current/default/displayorder invariants changed.");
        if (!UnchangedStateAndGpt(plan)) return Fail("Retirement state or GPT layout changed.");
        return Pass("REAgentC state/location and protected BCD invariants verified.");
    }

    internal static async Task<WinReProtectedBcdSnapshot> CaptureProtectedBcdAsync(IOperationLog log)
    {
        var bootmgr = await RunRequiredStaticAsync("bcdedit.exe", ["/enum", "{bootmgr}", "/v"], log);
        var current = await RunRequiredStaticAsync("bcdedit.exe", ["/enum", "{current}", "/v"], log);
        var @default = await RunRequiredStaticAsync("bcdedit.exe", ["/enum", "{default}", "/v"], log);
        var loaders = await RunRequiredStaticAsync("bcdedit.exe", ["/enum", "OSLOADER", "/v"], log);
        var all = await RunRequiredStaticAsync("bcdedit.exe", ["/enum", "all", "/v"], log);
        var currentId = BcdEditTextParser.Parse(current.StdOut).FirstOrDefault()?.Identifier ?? string.Empty;
        var defaultId = BcdEditTextParser.Parse(@default.StdOut).FirstOrDefault()?.Identifier ?? string.Empty;
        var protectedLoaders = BcdEditTextParser.Parse(loaders.StdOut)
            .Where(entry => entry.IsWindowsLoader && !entry.LooksLikeRecoveryEnvironment)
            .OrderBy(entry => entry.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(entry => string.Join('|', entry.Identifier, entry.Description, entry.Path,
                entry.Device, entry.OsDevice, entry.ResumeObject))
            .ToArray();
        return new WinReProtectedBcdSnapshot(
            currentId,
            defaultId,
            HashText(bootmgr.StdOut),
            HashText(string.Join("\n", protectedLoaders)),
            all.StdOut);
    }

    internal static string Fingerprint(WinReProtectedBcdSnapshot snapshot) => HashText(string.Join("\n",
        snapshot.CurrentGuid, snapshot.DefaultGuid, snapshot.BootManagerSha256, snapshot.OsLoaderFingerprint));

    internal static string CaptureGptFingerprint()
    {
        var snapshot = new VolumeLocatorGptLayoutSource().Capture();
        if (snapshot.Warnings.Count != 0) throw new InvalidOperationException(
            "Cannot capture a complete GPT layout: " + string.Join("; ", snapshot.Warnings));
        var rows = snapshot.Partitions
            .OrderBy(partition => partition.DiskNumber)
            .ThenBy(partition => partition.PartitionNumber)
            .Select(partition => string.Join('|',
                partition.DiskNumber, partition.PartitionNumber,
                partition.DiskGptId?.ToString("D") ?? string.Empty,
                partition.PartitionGptId.ToString("D"),
                partition.PartitionType?.ToString("D") ?? string.Empty,
                partition.StartingOffset, partition.SizeBytes));
        return HashText(string.Join("\n", rows));
    }

    private async Task<LoggedProcessResult> RunRequiredAsync(string file, IReadOnlyList<string> args) =>
        await RunRequiredStaticAsync(file, args, _log);

    private async Task RunMutationAsync(string file, IReadOnlyList<string> args)
    {
        var result = await RunRequiredAsync(file, args);
        if (result.ExitCode != 0) throw new InvalidOperationException(LoggedProcess.Describe(result));
    }

    private static async Task<LoggedProcessResult> RunRequiredStaticAsync(
        string file, IReadOnlyList<string> args, IOperationLog log)
    {
        var result = await LoggedProcess.RunAsync(file, args, log);
        if (result.ExitCode != 0) throw new InvalidOperationException(LoggedProcess.Describe(result));
        return result;
    }

    private static bool TryParseEnabled(string text, out bool enabled)
    {
        enabled = false;
        var statusLines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("Windows RE status", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (statusLines.Length != 1) return false;
        var hasEnabled = statusLines[0].Contains("Enabled", StringComparison.OrdinalIgnoreCase);
        var hasDisabled = statusLines[0].Contains("Disabled", StringComparison.OrdinalIgnoreCase);
        if (hasEnabled == hasDisabled) return false;
        enabled = hasEnabled;
        return true;
    }

    private static bool PathIsOnExpectedGpt(string path, string expectedGpt)
    {
        var probe = File.Exists(path) || Directory.Exists(path) ? path : ExistingAncestor(path);
        var volumePath = VolumeIdentity.TryGetVolumeGuidPath(probe);
        if (volumePath is null || !VolumeLocator.TryParseGptId(expectedGpt, out var expected)) return false;
        var matches = VolumeLocator.Enumerate().Volumes.Where(candidate =>
            VolumeIdentity.AreSameVolume(candidate.VolumeGuidPath, volumePath) &&
            candidate.GptPartitionGuid == expected).ToArray();
        return matches.Length == 1;
    }

    private static WinReDeploymentVerification ValidatePlanPaths(WinReDeploymentPlan plan)
    {
        if (!Guid.TryParseExact(plan.TransactionId, "N", out _) ||
            !BcdIdentifiers.TryParseObjectId(plan.ExpectedRecoveryGuid, out _) ||
            !BcdIdentifiers.TryParseObjectId(plan.Boot2Guid, out _) ||
            !VolumeLocator.TryParseGptId(plan.RecoveryDataVolumeGptId, out var dataGpt) ||
            !VolumeLocator.TryParseGptId(plan.RecoveryPartitionGptId, out var recoveryGpt) ||
            dataGpt == recoveryGpt)
            return Fail("Deployment plan contains a malformed transaction/GUID identity.");
        var live = Path.GetFullPath(plan.LiveWimPath);
        var incoming = Path.GetFullPath(plan.IncomingWimPath);
        var recovery = Path.GetFullPath(plan.RecoveryDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var preparationRoot = Path.GetFullPath(WindowsWinReWorkspaceFactory.MachineRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!string.Equals(Path.GetFileName(live), "Winre.wim", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(live)?.TrimEnd(Path.DirectorySeparatorChar), recovery, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(incoming, live + ".incoming", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(plan.BackupWimPath), live, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFullPath(plan.PreparedWimPath), live, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(plan.PreparedWimPath).StartsWith(preparationRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFullPath(plan.PreparedBundlePath).StartsWith(preparationRoot, StringComparison.OrdinalIgnoreCase))
            return Fail("Deployment plan filesystem paths do not satisfy the bounded WinRE replacement contract.");
        return Pass("Deployment plan paths and identities are bounded.");
    }

    private static bool HasDeploymentCapacity(WinReDeploymentPlan plan, out string diagnostic)
    {
        var recoveryProbe = ExistingAncestor(plan.RecoveryDirectory);
        var backupProbe = ExistingAncestor(Path.GetDirectoryName(plan.BackupWimPath)!);
        if (!GetDiskFreeSpaceEx(recoveryProbe, out var recoveryFree, out _, out _) ||
            !GetDiskFreeSpaceEx(backupProbe, out var backupFree, out _, out _))
        {
            diagnostic = "Could not query recovery/backup volume free space.";
            return false;
        }

        const ulong reserve = 64UL * 1024 * 1024;
        var prepared = checked((ulong)new FileInfo(plan.PreparedWimPath).Length);
        var original = checked((ulong)new FileInfo(plan.LiveWimPath).Length);
        if (recoveryFree + original < prepared + reserve)
        {
            diagnostic = $"Recovery volume cannot hold prepared WIM after original removal plus {reserve} reserved bytes.";
            return false;
        }
        if (backupFree < original + reserve)
        {
            diagnostic = $"Backup volume cannot hold the exact original WIM plus {reserve} reserved bytes.";
            return false;
        }
        diagnostic = "Recovery and backup capacity verified.";
        return true;
    }

    private static string ExistingAncestor(string path)
    {
        var current = Path.GetFullPath(path);
        while (!Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException($"No existing ancestor for '{path}'.");
        }
        return current;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName,
        out ulong freeBytesAvailable,
        out ulong totalNumberOfBytes,
        out ulong totalNumberOfFreeBytes);

    private static bool ContainsPath(string text, string path) =>
        text.Replace('/', '\\').Contains(path.TrimEnd('\\').Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);

    private static bool HashEquals(string path, string expected) => File.Exists(path) &&
        string.Equals(WinReDeploymentPlanBuilder.HashFile(path), expected, StringComparison.OrdinalIgnoreCase);

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"))));

    private static WinReDeploymentVerification Pass(string detail) => new(true, detail);
    private static WinReDeploymentVerification Fail(string detail) => new(false, detail);

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteDurable(string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private bool UnchangedStateAndGpt(WinReDeploymentPlan plan)
    {
        var statePath = RetirementStateStore.ResolveStateFilePath(_options);
        return HashEquals(statePath, plan.RetirementStateSha256) &&
               string.Equals(CaptureGptFingerprint(), plan.GptLayoutFingerprint, StringComparison.OrdinalIgnoreCase);
    }
}

internal static class WindowsServicingPreflight
{
    public static string? DescribeBlocker()
    {
        using var sessionManager = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager", writable: false);
        if (sessionManager?.GetValue("PendingFileRenameOperations") is not null)
            return "Windows servicing preflight failed: PendingFileRenameOperations exists.";
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
            return "Windows servicing preflight failed: CBS RebootPending exists.";
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
            return "Windows servicing preflight failed: Windows Update RebootRequired exists.";
        if (KeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\PackagesPending"))
            return "Windows servicing preflight failed: CBS PackagesPending exists.";
        if (System.Diagnostics.Process.GetProcessesByName("MoUsoCoreWorker").Length != 0)
            return "Windows servicing preflight failed: MoUsoCoreWorker is active.";
        return null;
    }

    private static bool KeyExists(string path)
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path, writable: false);
        return key is not null;
    }
}
