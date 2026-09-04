using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch;

internal static class Program
{
    private const string RecoveryRunSwitch = "--recovery-run";
    private const string RecoveryDryRunSwitch = "--recovery-dry-run";
    private const string RecoveryReviewSwitch = "--recovery-review";
    private const string RecoveryResumePreviewSwitch = "--recovery-resume-preview";
    private const string HardwareReviewSwitch = "--retirement-hardware-review";
    private const string ExecuteDeletionSwitch = "--execute-deletion";
    private const string ListVolumesSwitch = "--list-volumes";
    private const string RepairPendingHandoffSwitch = "--repair-pending-handoff";
    private const string RepairPendingHandoffReviewSwitch = "--repair-pending-handoff-review";
    private const string ProvisionWinReLauncherSwitch = "--provision-winre-launcher";
    private const string WinReLauncherReviewSwitch = "--winre-launcher-review";
    private const string DeployWinReLauncherSwitch = "--deploy-winre-launcher";
    private const string ExecuteWinReDeploymentSwitch = "--execute-winre-deployment";
    private const string RecoverWinReDeploymentSwitch = "--recover-winre-deployment";
    private const string WinReDeploymentStatusSwitch = "--winre-deployment-status";
    private const string RecoverySmokeSwitch = "--recovery-smoke";
    private const string CompleteWinReSmokeSwitch = "--complete-winre-smoke";
    private const string RecoveryLaunchSwitch = "--recovery-launch";
    private const string OperationTokenOption = "--operation-token";
    private const string ReconcileLegacyJournalsSwitch = "--reconcile-legacy-winre-journals";

    [STAThread]
    static int Main(string[] args)
    {
        var recoveryRun = HasSwitch(args, RecoveryRunSwitch);
        var recoveryDryRun = HasSwitch(args, RecoveryDryRunSwitch);
        var recoveryReview = HasSwitch(args, RecoveryReviewSwitch);
        var hardwareReview = HasSwitch(args, HardwareReviewSwitch);
        var executeDeletion = HasSwitch(args, ExecuteDeletionSwitch);
        var repairPendingHandoff = HasSwitch(args, RepairPendingHandoffSwitch);
        var repairPendingHandoffReview = HasSwitch(args, RepairPendingHandoffReviewSwitch);
        var provisionWinReLauncher = HasSwitch(args, ProvisionWinReLauncherSwitch);
        var winReLauncherReview = HasSwitch(args, WinReLauncherReviewSwitch);
        var deployWinReLauncher = HasSwitch(args, DeployWinReLauncherSwitch);
        var recoverWinReDeployment = HasSwitch(args, RecoverWinReDeploymentSwitch);
        var winReDeploymentStatus = HasSwitch(args, WinReDeploymentStatusSwitch);
        var recoverySmoke = HasSwitch(args, RecoverySmokeSwitch);
        var completeWinReSmoke = HasSwitch(args, CompleteWinReSmokeSwitch);
        var recoveryLaunch = HasSwitch(args, RecoveryLaunchSwitch);
        var reviewOnly = (recoveryReview || hardwareReview) && !recoveryRun;

        if (recoverySmoke)
        {
            return RunRecoverySmoke(args);
        }

        if (winReDeploymentStatus)
        {
            return RunWinReDeploymentStatus(args.Length != 1);
        }

        if (recoverWinReDeployment)
        {
            return RunWinReDeploymentRecovery(args);
        }

        if (completeWinReSmoke)
        {
            return RunCompleteWinReSmoke(args);
        }

        if (HasSwitch(args, ReconcileLegacyJournalsSwitch))
        {
            return RunLegacyJournalReconciliation(args);
        }

        var deploymentInventory = WinReDeploymentJournalDiscovery.Inspect(AppConfiguration.Load());
        if (deploymentInventory.Invalid.Count > 0 || deploymentInventory.Active.Count > 0)
        {
            ConsoleHost.Attach(allocateIfMissing: true);
            Report("An incomplete or invalid WinRE deployment transaction exists.");
            Report("CleanSwitch will not start a new operation or its GUI until deterministic rollback recovery completes.");
            foreach (var item in deploymentInventory.Invalid) Report("INVALID: " + item);
            foreach (var item in deploymentInventory.Active)
                Report($"ACTIVE: {item.Path} stage={item.Last.Stage} sequence={item.Last.Sequence}");
            Report("Use --winre-deployment-status or --recover-winre-deployment. No mutation was attempted.");
            return 3;
        }

        if (deployWinReLauncher)
        {
            return RunWinReDeployment(args);
        }

        if (recoveryLaunch)
        {
            return RunRecoveryLaunch(args);
        }

        if (provisionWinReLauncher || winReLauncherReview)
        {
            var combined = provisionWinReLauncher == winReLauncherReview ||
                           recoveryRun || recoveryDryRun || recoveryReview || hardwareReview || executeDeletion ||
                           repairPendingHandoff || repairPendingHandoffReview ||
                           HasSwitch(args, RecoveryResumePreviewSwitch) ||
                           HasSwitch(args, RetirementAbandonCommand.Switch) ||
                           HasSwitch(args, ListVolumesSwitch);
            return RunWinReLauncher(args, provisionWinReLauncher, combined);
        }

        if (repairPendingHandoff || repairPendingHandoffReview)
        {
            var combined = repairPendingHandoff == repairPendingHandoffReview ||
                           recoveryRun || recoveryDryRun || recoveryReview || hardwareReview || executeDeletion ||
                           HasSwitch(args, RecoveryResumePreviewSwitch) ||
                           HasSwitch(args, RetirementAbandonCommand.Switch) ||
                           HasSwitch(args, ListVolumesSwitch);
            return RunPendingHandoffRepair(repairPendingHandoffReview, combined);
        }

        if (HasSwitch(args, ListVolumesSwitch))
        {
            return ListVolumes();
        }

        if (HasSwitch(args, RecoveryResumePreviewSwitch))
        {
            return RunRecoveryResumePreview();
        }

        if (HasSwitch(args, RetirementAbandonCommand.Switch))
        {
            return RunAbandonRetirement(recoveryRun || recoveryDryRun || reviewOnly || executeDeletion);
        }

        if (executeDeletion && !recoveryRun && !recoveryReview && !hardwareReview)
        {
            ConsoleHost.Attach(allocateIfMissing: true);
            Report("--execute-deletion is only accepted with --recovery-run, --recovery-review, or --retirement-hardware-review.");
            Report("This invocation did not start diskpart.");
            return 2;
        }

        if (recoveryRun || recoveryDryRun || reviewOnly)
        {
            return RunRecoverySide(new RecoveryRunRequest(
                DryRun: recoveryDryRun && !recoveryRun,
                ReviewOnly: reviewOnly,
                ExecuteDeletion: executeDeletion && !reviewOnly,
                OperationToken: GetOptionValue(args, OperationTokenOption)));
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static int RunRecoveryLaunch(string[] args)
    {
        try
        {
            EnsureOnlyOptions(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { RecoveryLaunchSwitch });
            var options = AppConfiguration.Load();
            var services = RetirementServices.CreateForExistingOperation(options, "recovery-launch");
            var state = services.Coordinator.TryLoad()
                ?? throw new InvalidOperationException("No active retirement handoff exists.");
            return RunRecoverySide(new RecoveryRunRequest(false, false, true, state.HandoffAuthorizationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or RetirementStorageException)
        {
            Report("Recovery launcher failed closed: " + exception.Message);
            return 2;
        }
    }

    private static int RunLegacyJournalReconciliation(string[] args)
    {
        try
        {
            EnsureOnlyOptions(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ReconcileLegacyJournalsSwitch });
            var marker = WinReDeploymentJournalDiscovery.ReconcileLegacyJournals(AppConfiguration.Load());
            Report("Legacy WinRE journal reconciliation completed: " + marker);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or RetirementStorageException)
        {
            Report("Legacy WinRE journal reconciliation failed closed: " + exception.Message);
            return 2;
        }
    }

    private static int RunRecoverySmoke(string[] args)
    {
        // winpeshl LaunchApps waits for each process to exit before starting the next entry.
        // Never allocate or pause an interactive console here; stock RecEnv must run next.
        ConsoleHost.Attach(allocateIfMissing: false);
        try
        {
            EnsureOnlyOptions(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                RecoverySmokeSwitch, "--deployment-transaction"
            });
            var transactionId = GetOptionValue(args, "--deployment-transaction")
                ?? throw new InvalidOperationException("--recovery-smoke requires --deployment-transaction <id>.");
            var result = new RecoverySmokeRunner(
                AppConfiguration.Load(), new WindowsRecoverySmokeEnvironment(), transactionId).Run();
            Report(result.Message);
            Report($"Smoke receipt: {result.ReceiptPath}");
            Report($"Smoke receipt SHA256: {result.ReceiptSha256}");
            Report("No RecoveryRunner, RetirementExecutor, disk command, BCD command, or retirement state store was instantiated.");
            return result.Passed ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Report("Recovery smoke failed closed: " + exception.Message);
            Report("No retirement executor or destructive command was instantiated.");
            return 2;
        }
    }

    private static int RunWinReDeploymentStatus(bool combined)
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);
        if (combined)
        {
            Report("--winre-deployment-status cannot be combined with another mode.");
            return PauseIfOwned(ownsConsole, 2);
        }

        var inventory = WinReDeploymentJournalDiscovery.Inspect(AppConfiguration.Load());
        foreach (var invalid in inventory.Invalid) Report("INVALID: " + invalid);
        foreach (var active in inventory.Active)
            Report($"ACTIVE: {active.Path} stage={active.Last.Stage} sequence={active.Last.Sequence}");
        if (inventory.Invalid.Count == 0 && inventory.Active.Count == 0) Report("No unresolved WinRE deployment transaction.");
        return PauseIfOwned(ownsConsole, inventory.Invalid.Count == 0 ? 0 : 2);
    }

    private static int RunWinReDeployment(string[] args)
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);
        try
        {
            if (!ProductionRetirementGates.WinReDeploymentImplemented)
                throw new InvalidOperationException("This safe build has WinReDeploymentImplemented=false.");
            if (!HasSwitch(args, ExecuteWinReDeploymentSwitch))
                throw new InvalidOperationException("Live WinRE deployment additionally requires explicit --execute-winre-deployment.");
            var prepared = GetOptionValue(args, "--prepared-winre")
                ?? throw new InvalidOperationException("--prepared-winre <absolute prepared Winre.wim path> is required.");
            var expectedOriginalHash = GetOptionValue(args, "--expected-original-winre-sha256")
                ?? throw new InvalidOperationException("--expected-original-winre-sha256 <SHA256> is required.");
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                DeployWinReLauncherSwitch, ExecuteWinReDeploymentSwitch, "--prepared-winre",
                "--expected-original-winre-sha256"
            };
            EnsureOnlyOptions(args, allowed);

            var options = AppConfiguration.Load();
            var log = FileOperationLog.Create(RetirementStateStore.ResolveLogDirectory(options), "winre-deploy");
            var plan = WinReDeploymentPlanBuilder.BuildAsync(options, prepared, expectedOriginalHash, log).GetAwaiter().GetResult();
            var journalPath = Path.Combine(
                WinReDeploymentJournalDiscovery.ResolveAuthoritativeRoot(options),
                plan.TransactionId,
                "deployment-journal.ndjson");
            var transaction = new WinReDeploymentTransaction(
                new FileWinReDeploymentJournal(journalPath),
                new WindowsWinReDeploymentPlatform(options, log));
            var result = transaction.DeployAsync(plan).GetAwaiter().GetResult();
            Report(result.Message);
            Report($"Journal: {result.JournalPath}");
            Report("RETIRE SYSTEM remains forbidden until smoke evidence is recorded and the journal becomes terminal.");
            return PauseIfOwned(ownsConsole, result.Passed ? 0 : 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or RetirementStorageException)
        {
            Report("WinRE deployment failed closed: " + exception.Message);
            Report("Inspect --winre-deployment-status; an incomplete journal requires --recover-winre-deployment.");
            return PauseIfOwned(ownsConsole, 2);
        }
    }

    private static int RunWinReDeploymentRecovery(string[] args)
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);
        try
        {
            if (!ProductionRetirementGates.WinReDeploymentImplemented)
                throw new InvalidOperationException("This safe build cannot mutate WinRE during rollback recovery.");
            if (!HasSwitch(args, ExecuteWinReDeploymentSwitch))
                throw new InvalidOperationException("Rollback recovery requires explicit --execute-winre-deployment.");
            EnsureOnlyOptions(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                RecoverWinReDeploymentSwitch, ExecuteWinReDeploymentSwitch
            });
            var inventory = WinReDeploymentJournalDiscovery.Inspect(AppConfiguration.Load());
            if (inventory.Invalid.Count != 0 || inventory.Active.Count != 1)
                throw new InvalidOperationException("Recovery requires exactly one valid unresolved deployment journal.");
            var active = inventory.Active[0];
            var options = AppConfiguration.Load();
            var log = FileOperationLog.Create(RetirementStateStore.ResolveLogDirectory(options), "winre-deploy-recovery");
            var transaction = new WinReDeploymentTransaction(
                new FileWinReDeploymentJournal(active.Path),
                new WindowsWinReDeploymentPlatform(options, log));
            var result = transaction.RecoverToRollbackAsync().GetAwaiter().GetResult();
            Report(result.Message);
            Report($"Journal: {result.JournalPath}");
            return PauseIfOwned(ownsConsole, result.Passed ? 0 : 1);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or RetirementStorageException)
        {
            Report("WinRE deployment recovery failed closed: " + exception.Message);
            return PauseIfOwned(ownsConsole, 2);
        }
    }

    private static int RunCompleteWinReSmoke(string[] args)
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);
        try
        {
            EnsureOnlyOptions(args, new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                CompleteWinReSmokeSwitch, "--receipt"
            });
            var receipt = GetOptionValue(args, "--receipt")
                ?? throw new InvalidOperationException("--receipt <recovery smoke receipt path> is required.");
            var inventory = WinReDeploymentJournalDiscovery.Inspect(AppConfiguration.Load());
            if (inventory.Invalid.Count != 0 || inventory.Active.Count != 1 ||
                inventory.Active[0].Last.Stage != WinReDeploymentStage.AwaitingSmoke)
                throw new InvalidOperationException("Smoke completion requires exactly one valid AwaitingSmoke deployment journal.");
            var active = inventory.Active[0];
            var receiptHash = RecoverySmokeReceiptVerifier.Verify(receipt, active.Plan);
            var options = AppConfiguration.Load();
            var transaction = new WinReDeploymentTransaction(
                new FileWinReDeploymentJournal(active.Path),
                new WindowsWinReDeploymentPlatform(options));
            transaction.RecordSmokeVerified(receiptHash);
            var result = transaction.CommitAfterSmokeAsync().GetAwaiter().GetResult();
            Report(result.Message);
            Report($"Journal: {result.JournalPath}");
            return PauseIfOwned(ownsConsole, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            Report("WinRE smoke completion failed closed: " + exception.Message);
            return PauseIfOwned(ownsConsole, 2);
        }
    }

    private static string? GetOptionValue(string[] args, string option)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                return args[index][(option.Length + 1)..];
            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1];
        }
        return null;
    }

    private static void EnsureOnlyOptions(string[] args, HashSet<string> allowed)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var raw = args[index];
            var name = raw.Split('=', 2)[0];
            if (!name.StartsWith("--", StringComparison.Ordinal))
                throw new InvalidOperationException($"Unexpected positional argument '{raw}'.");
            if (!allowed.Contains(name)) throw new InvalidOperationException($"Unexpected option '{name}'.");
            if (!seen.Add(name)) throw new InvalidOperationException($"Duplicate option '{name}'.");
            if ((string.Equals(name, "--prepared-winre", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "--receipt", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "--deployment-transaction", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(name, "--expected-original-winre-sha256", StringComparison.OrdinalIgnoreCase)) && !raw.Contains('='))
            {
                if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new InvalidOperationException($"Option '{name}' requires a value.");
                index++;
            }
        }
    }

    private static int RunWinReLauncher(string[] args, bool provision, bool combinedWithOtherModes)
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);
        if (combinedWithOtherModes)
        {
            Report("The WinRE launcher provisioning/review switches cannot be combined with any other mode.");
            Report("No WIM, BCD, state, disk, boot sequence, or reboot was changed.");
            return PauseIfOwned(ownsConsole, 2);
        }

        try
        {
            EnsureOnlyOptions(args, provision
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ProvisionWinReLauncherSwitch, "--expected-original-winre-sha256"
                }
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WinReLauncherReviewSwitch });
            var expectedOriginalHash = provision
                ? GetOptionValue(args, "--expected-original-winre-sha256")
                    ?? throw new InvalidOperationException("--expected-original-winre-sha256 <SHA256> is required for preparation.")
                : null;
            var options = AppConfiguration.Load();
            var log = FileOperationLog.Create(
                RetirementStateStore.ResolveLogDirectory(options),
                provision ? "winre-provision" : "winre-launcher-review");
            var bootManager = new WindowsBootManager(log);
            var bootValidator = new BootEntryValidator(bootManager, log);
            var recovery = bootValidator.ResolveRecoveryEntryAsync(options.RecoveryGuid).GetAwaiter().GetResult();
            if (recovery.Identifier is null || recovery.Entry is null)
            {
                Report("WinRE launcher operation failed closed: RecoveryGuid did not resolve to one exact WinRE BCD entry.");
                Report(recovery.Report.Describe());
                return PauseIfOwned(ownsConsole, 2);
            }

            WinReLauncherValidationResult result;
            if (provision)
            {
                var existing = RetirementServices.CreateForExistingOperation(options, "winre-provision-preflight")
                    .Coordinator.TryLoad();
                try
                {
                    WinReLauncherProvisioningGuard.Validate(
                        existing,
                        options,
                        ProductionRetirementGates.DestructiveOperationsImplemented,
                        ProductionRetirementGates.BcdOperationsImplemented);
                }
                catch (InvalidOperationException exception)
                {
                    Report(exception.Message);
                    Report("The existing state was read only. No WIM, BCD, state, disk, boot sequence, or reboot was changed.");
                    return PauseIfOwned(ownsConsole, 2);
                }

                result = new WindowsWinReLauncherProvisioner(options, log)
                    .ProvisionAsync(recovery, expectedOriginalHash!).GetAwaiter().GetResult();
            }
            else
            {
                result = new WindowsWinReLauncherValidator(options, log)
                    .ValidateAsync(recovery).GetAwaiter().GetResult();
            }

            Report(result.Report.Describe());
            if (!string.IsNullOrWhiteSpace(result.ImagePath))
            {
                Report($"Validated WinRE image: {result.ImagePath}");
            }

            if (!string.IsNullOrWhiteSpace(result.PreparedImagePath))
            {
                Report($"Prepared WinRE image: {result.PreparedImagePath}");
            }
            if (!string.IsNullOrWhiteSpace(result.PreparedBundlePath))
            {
                Report($"Prepared WinRE deployment bundle: {result.PreparedBundlePath}");
            }

            Report(provision
                ? "Preparation changed only a verified machine-level WIM copy. Deploying it to live WinRE requires a separate explicit operation. No live WIM, BCD, retirement state, partition, bootsequence, or reboot was changed."
                : "Review copied the live WIM and inspected only that temporary copy. No live WIM, BCD, retirement state, partition, bootsequence, or reboot was changed.");
            return PauseIfOwned(ownsConsole, result.Passed ? 0 : 1);
        }
        catch (Exception exception) when (
            exception is RetirementStorageException or RetirementExecutionException or InvalidOperationException or
                BootManagerException or IOException or UnauthorizedAccessException)
        {
            Report("WinRE launcher operation failed closed.");
            Report("No live WIM, BCD, retirement state, disk, boot sequence, or reboot was changed.");
            Report(exception.Message);
            return PauseIfOwned(ownsConsole, 2);
        }
    }

    private static int RunPendingHandoffRepair(bool reviewOnly, bool combinedWithOtherModes)
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);
        if (combinedWithOtherModes)
        {
            Report("The PENDING handoff repair switches cannot be combined with any other mode.");
            Report("No BCD command, disk command, state write, boot sequence, or reboot was attempted.");
            return PauseIfOwned(ownsConsole, 2);
        }

        try
        {
            var options = AppConfiguration.Load();
            var services = RetirementServices.CreateForExistingOperation(
                options,
                reviewOnly ? "handoff-repair-review" : "handoff-repair");
            var state = services.Coordinator.TryLoad();
            var statePath = services.Coordinator.StateFilePath;
            var beforeHash = HashFile(statePath);

            var repair = new PendingHandoffRepair(
                new VolumeLocatorGptLayoutSource(),
                new BootManagerBcdStoreSource(services.BootManager),
                services.BootManager,
                services.Log);
            var result = reviewOnly
                ? repair.ReviewAsync(state).GetAwaiter().GetResult()
                : repair.ExecuteAsync(state).GetAwaiter().GetResult();

            var afterHash = HashFile(statePath);
            Report(result.Describe(reviewOnly));
            Report($"State SHA256 before: {beforeHash}");
            Report($"State SHA256 after : {afterHash}");
            Report($"State unchanged: {string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase)}");

            return PauseIfOwned(ownsConsole, result.Passed && beforeHash == afterHash ? 0 : 1);
        }
        catch (Exception exception) when (
            exception is RetirementStorageException or RetirementExecutionException or
                InvalidOperationException or BootManagerException or IOException or UnauthorizedAccessException)
        {
            Report("PENDING handoff repair failed closed.");
            Report("No disk command, BCD delete, boot sequence, state write, or reboot was attempted by this command.");
            Report(exception.Message);
            return PauseIfOwned(ownsConsole, 2);
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// Operator-visible abandon of a PENDING retirement state. Non-destructive:
    /// archives first, then marks ABORTED. Never starts recovery, deletion, or a reboot.
    /// </summary>
    private static int RunAbandonRetirement(bool combinedWithOtherModes)
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);

        if (combinedWithOtherModes)
        {
            Report("--abandon-retirement cannot be combined with recovery or deletion switches.");
            Report("This invocation did not start diskpart, bcdedit, or a reboot.");
            return PauseIfOwned(ownsConsole, 2);
        }

        return PauseIfOwned(ownsConsole, RetirementAbandonCommand.Run(Report));
    }

    private static int PauseIfOwned(bool ownsConsole, int exitCode)
    {
        if (ownsConsole)
        {
            Report(string.Empty);
            Report("Press Enter to close this window...");
            Console.In.ReadLine();
        }

        return exitCode;
    }

    /// <summary>
    /// Read-only preview of resuming from BOOT1_RETIRED. Never runs diskpart, bcdedit /delete,
    /// state persistence, or reboot.
    /// </summary>
    private static int RunRecoveryResumePreview()
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);

        try
        {
            var options = AppConfiguration.Load();
            // Read-only preview may load state from the running Boot 2 volume during operator
            // verification. No state file is written in this code path.
            var services = RetirementServices.CreateForExistingOperation(options, "resume-preview");

            Report($"Retirement state file: {services.Coordinator.StateFilePath}");
            Report($"Log destinations: {string.Join("; ", services.Log.Destinations)}");

            var state = services.Coordinator.TryLoad();
            if (state is null)
            {
                Report("ResumePreviewFailed: No retirement state file was found. State modified: False");
                return PauseIfOwned(ownsConsole, 1);
            }

            var preview = services.RecoveryRunner.RunResumePreviewAsync(state).GetAwaiter().GetResult();
            Report(preview.Describe());

            return PauseIfOwned(ownsConsole, preview.Readiness == "PASS" ? 0 : 1);
        }
        catch (Exception exception) when (
            exception is RetirementStorageException or InvalidOperationException or BootManagerException)
        {
            Report("CleanSwitch could not run the resume preview. State modified: False");
            Report(exception.Message);
            return PauseIfOwned(ownsConsole, 2);
        }
    }

    /// <summary>
    /// Headless entry point for the recovery environment:
    /// <c>--recovery-run</c> validates and hands off to Boot 2.
    /// <c>--recovery-dry-run</c> validates and prints the deletion plan.
    /// <c>--recovery-review</c> / <c>--retirement-hardware-review</c> print a read-only
    /// Phase 2B + 2C hardware review. No disk or BCD command is constructed or started.
    /// <c>--recovery-resume-preview</c> previews BOOT1_RETIRED resume with survivor reconciliation.
    /// <c>--execute-deletion</c> is the runtime opt-in; it does nothing while live delete is disabled.
    /// </summary>
    private static int RunRecoverySide(RecoveryRunRequest request)
    {
        ConsoleHost.Attach();

        try
        {
            var options = AppConfiguration.Load();
            var prefix = request.ReviewOnly ? "hardware-review" : request.DryRun ? "recovery-dryrun" : "recovery";
            var services = RetirementServices.CreateForExistingOperation(options, prefix);

            Report($"Retirement state file: {services.Coordinator.StateFilePath}");
            Report($"Log destinations: {string.Join("; ", services.Log.Destinations)}");

            var result = services.RecoveryRunner.RunAsync(request).GetAwaiter().GetResult();
            Report($"{result.Outcome}: {result.Message}");

            return result.Outcome == RecoveryRunOutcome.Failed ? 1 : 0;
        }
        catch (Exception exception) when (
            exception is RetirementStorageException or InvalidOperationException or BootManagerException)
        {
            Report("CleanSwitch could not run the recovery-side step. No boot change was made.");
            Report(exception.Message);
            return 2;
        }
    }

    /// <summary>
    /// <c>CleanSwitch.exe --list-volumes</c>: read-only inventory of every volume with the
    /// disk, partition and GPT partition GUID behind it.
    /// <para>
    /// This is how the operator discovers the value for
    /// <c>CleanSwitch:RecoveryDataVolumeGptId</c>. It reads the partition table and nothing
    /// else: no configuration is loaded, no state file is written, no BCD entry is read or
    /// changed, and the PC is never restarted.
    /// </para>
    /// </summary>
    private static int ListVolumes()
    {
        var ownsConsole = ConsoleHost.Attach(allocateIfMissing: true);

        Report("CleanSwitch volume report (read-only). No boot entry, partition or file was changed.");
        Report(string.Empty);

        var located = VolumeLocator.Enumerate();

        if (located.Volumes.Count == 0)
        {
            Report("No volumes could be enumerated.");
        }
        else
        {
            WriteVolumeTable(located);
        }

        if (located.Warnings.Count > 0)
        {
            Report(string.Empty);
            Report("Enumeration warnings:");
            foreach (var warning in located.Warnings)
            {
                Report("  ! " + warning);
            }
        }

        var unidentified = located.Volumes
            .Where(volume => volume.Outcome != VolumeIdentityOutcome.Identified)
            .ToList();

        if (unidentified.Count > 0)
        {
            Report(string.Empty);
            Report("Volumes without a GPT partition identity:");
            foreach (var volume in unidentified)
            {
                Report($"  {volume.VolumeGuidPath}");
                Report($"    {volume.Outcome}: {volume.Diagnostic}");
            }
        }

        Report(string.Empty);
        ReportConfiguredLocation();

        Report(string.Empty);
        Report("Partitions with no volume device - Microsoft Reserved, unformatted, or unrecognised - have no");
        Report("row here by design: this lists volumes, not every partition table entry. CD/DVD and network");
        Report("volumes are skipped. Sizes are decimal GB/MB, taken from the partition table where available.");
        Report(string.Empty);
        Report("The GPT partition GUID is read from the partition table on the disk, so it is the same value");
        Report("from Boot 1, from WinRE and from Boot 2. Drive letters and \\\\?\\Volume{...} GUIDs are not:");
        Report("both are assigned per Windows instance, and WinPE mints its own volume GUIDs.");
        Report(string.Empty);
        Report("Copy the GPT partition GUID of the volume that should hold the retirement state file into");
        Report("CleanSwitch:RecoveryDataVolumeGptId in appsettings.json, and set");
        Report("CleanSwitch:RecoveryDataFolderName to the folder name on that volume (default CleanSwitchData).");

        if (ownsConsole)
        {
            Report(string.Empty);
            Report("Press Enter to close this window...");
            Console.In.ReadLine();
        }

        return 0;
    }

    /// <summary>
    /// Shows what the current configuration resolves to, without creating the folder or
    /// writing anything. Never fails the command: a broken configuration is something this
    /// diagnostic should report, not something it should die on.
    /// </summary>
    private static void ReportConfiguredLocation()
    {
        Report("Configured retirement data location:");

        Models.CleanSwitchOptions options;
        try
        {
            options = AppConfiguration.Load();
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            Report($"  appsettings.json could not be read: {exception.Message}");
            return;
        }

        Report($"  RecoveryDataVolumeGptId : {Or(options.RecoveryDataVolumeGptId, "(not set)")}");
        Report($"  RecoveryDataFolderName  : {Or(options.RecoveryDataFolderName, "(not set)")}");
        Report($"  RecoveryDataPath        : {Or(options.RecoveryDataPath, "(not set)")}");

        var preview = RetirementStateStore.PreviewLocation(options);

        if (preview.Error is not null)
        {
            Report("  Resolves to             : NOTHING - the retirement flow would refuse to start.");
            foreach (var line in preview.Error.Split(Environment.NewLine))
            {
                Report("    " + line);
            }

            return;
        }

        Report($"  Resolved by             : {preview.Source}");
        Report($"  Resolves to             : {preview.Root}");
        Report(
            $"  State file would be     : {preview.StateFilePath} " +
            (File.Exists(preview.StateFilePath!) ? "(exists)" : "(does not exist yet)"));

        if (preview.VolumeIdentity is not null)
        {
            Report($"  On volume               : {preview.VolumeIdentity.Describe()}");
        }
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static void WriteVolumeTable(VolumeLocatorResult located)
    {
        var rows = located.Volumes
            .OrderBy(volume => volume.DiskNumber ?? int.MaxValue)
            .ThenBy(volume => volume.PartitionNumber ?? int.MaxValue)
            .Select(volume => new[]
            {
                volume.DiskNumber?.ToString() ?? "?",
                volume.PartitionNumber?.ToString() ?? "?",
                volume.GptPartitionId ?? "(none)",
                LocatedVolume.FormatSize(volume.SizeBytes),
                volume.FileSystem ?? "(none)",
                volume.MountPoints.Count == 0 ? "(none)" : string.Join(" ", volume.MountPoints),
                volume.DriveType.ToString(),
                volume.IsRunningSystemVolume ? "YES" : "no"
            })
            .ToList();

        string[] headers = ["Disk", "Part", "GPT partition GUID", "Size", "FS", "Mounts", "Drive type", "Running OS"];

        var widths = headers
            .Select((header, column) => rows
                .Select(row => row[column].Length)
                .Append(header.Length)
                .Max())
            .ToArray();

        Report(FormatRow(headers, widths));
        Report(FormatRow(widths.Select(width => new string('-', width)).ToArray(), widths));

        foreach (var row in rows)
        {
            Report(FormatRow(row, widths));
        }
    }

    private static string FormatRow(IReadOnlyList<string> cells, IReadOnlyList<int> widths)
    {
        var builder = new StringBuilder();
        for (var column = 0; column < cells.Count; column++)
        {
            if (column > 0)
            {
                builder.Append("  ");
            }

            builder.Append(cells[column].PadRight(widths[column]));
        }

        return builder.ToString().TrimEnd();
    }

    private static bool HasSwitch(IEnumerable<string> args, string name) =>
        args.Any(argument => string.Equals(argument?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private static void Report(string message)
    {
        Console.Out.WriteLine(message);
        Console.Out.Flush();
    }
}

/// <summary>
/// Gives this <c>WinExe</c> a usable console for its command-line modes. A WinForms app has
/// no console of its own, so without this every <c>Console.Write</c> goes nowhere.
/// </summary>
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;
    private const int ErrorAccessDenied = 5;

    private static bool _attached;

    /// <summary>
    /// Attaches to the console this process was launched from. Failure is ignored: the file
    /// log is the authoritative record for the unattended recovery modes.
    /// </summary>
    /// <param name="allocateIfMissing">
    /// When true and there is no parent console, allocate one. That is the case when an
    /// elevated <c>requireAdministrator</c> process is started from a non-elevated prompt,
    /// because UAC hands it no console. Only interactive modes should ask for this: the
    /// allocated window disappears the moment the process exits.
    /// </param>
    /// <returns>True when a console was allocated, so an interactive mode should pause before exiting.</returns>
    public static bool Attach(bool allocateIfMissing = false)
    {
        if (_attached)
        {
            return false;
        }

        _attached = true;

        var ownsConsole = false;

        if (!AttachConsole(AttachParentProcess))
        {
            // ERROR_ACCESS_DENIED means this process already has a console, which is fine.
            var error = Marshal.GetLastWin32Error();
            if (allocateIfMissing && error != ErrorAccessDenied)
            {
                ownsConsole = AllocConsole();
            }
        }

        RedirectStandardStreams();
        return ownsConsole;
    }

    /// <summary>
    /// Console.Out may already have been bound to the null stream before the console existed,
    /// so rebind it to the real handles.
    /// </summary>
    private static void RedirectStandardStreams()
    {
        try
        {
            var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(output);

            var error = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(error);

            Console.SetIn(new StreamReader(Console.OpenStandardInput()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // No console is available. The file log is the authoritative record.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();
}
