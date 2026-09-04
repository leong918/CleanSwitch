namespace CleanSwitch.Recovery;

public enum WinReDeploymentFaultPoint
{
    AfterDisable,
    AfterOriginalRemoval,
    DuringIncomingCopy,
    AfterIncomingHashVerification,
    AfterFinalRename,
    AfterSetReImage,
    AfterEnable
}

public interface IWinReDeploymentFaultInjector
{
    void Hit(WinReDeploymentFaultPoint point);
}

public sealed class NoWinReDeploymentFaults : IWinReDeploymentFaultInjector
{
    public static readonly NoWinReDeploymentFaults Instance = new();
    private NoWinReDeploymentFaults() { }
    public void Hit(WinReDeploymentFaultPoint point) { }
}

public sealed record WinReDeploymentVerification(
    bool Passed,
    string Detail,
    string? RecoveryGuid = null);

/// <summary>
/// Boundary around every operating-system mutation. Production uses REAgentC and exact file
/// operations; tests use disposable filesystem/BCD fixtures.
/// </summary>
public interface IWinReDeploymentPlatform
{
    Task<WinReDeploymentVerification> VerifyD0Async(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> CaptureSnapshotsAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> BackupOriginalAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifyFirstMutationAsync(WinReDeploymentPlan plan);
    Task DisableAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifyDisabledAsync(WinReDeploymentPlan plan);
    Task RemoveOriginalAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifyOriginalRemovedAsync(WinReDeploymentPlan plan);
    Task CopyIncomingAsync(WinReDeploymentPlan plan, Action duringCopy);
    Task<WinReDeploymentVerification> VerifyIncomingAsync(WinReDeploymentPlan plan);
    Task ActivateIncomingAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifyFinalInstalledAsync(WinReDeploymentPlan plan);
    Task SetReImageAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifySetReImageAsync(WinReDeploymentPlan plan);
    Task EnableAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifyEnabledAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> ReviewLauncherAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifyPostSmokeAsync(WinReDeploymentPlan plan);
    Task RollbackAsync(WinReDeploymentPlan plan);
    Task<WinReDeploymentVerification> VerifyRollbackAsync(WinReDeploymentPlan plan);
}

public sealed record WinReDeploymentResult(
    bool Passed,
    WinReDeploymentStage Stage,
    string Message,
    string JournalPath);

/// <summary>
/// Fail-closed D0-D5 transaction. Every mutation is preceded by a durable intent and followed
/// by independent verification plus a durable completion record. Exceptions deliberately leave
/// the journal active; startup discovery then blocks a new transaction and requires rollback.
/// </summary>
public sealed class WinReDeploymentTransaction
{
    private readonly IWinReDeploymentJournal _journal;
    private readonly IWinReDeploymentPlatform _platform;
    private readonly IWinReDeploymentFaultInjector _faults;

    public WinReDeploymentTransaction(
        IWinReDeploymentJournal journal,
        IWinReDeploymentPlatform platform,
        IWinReDeploymentFaultInjector? faults = null)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _faults = faults ?? NoWinReDeploymentFaults.Instance;
    }

    public async Task<WinReDeploymentResult> DeployAsync(WinReDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        WinReDeploymentHashPolicy.RequireSealedPlan(plan);
        using var transactionLock = AcquireExclusiveLock();
        var existing = WinReDeploymentJournalDiscovery.Inspect(
            Directory.GetParent(Path.GetDirectoryName(_journal.Path)!)?.FullName
            ?? throw new InvalidOperationException("Deployment journal root could not be resolved."));
        if (existing.Invalid.Count > 0 || existing.Active.Count > 0 || File.Exists(_journal.Path))
        {
            throw new InvalidOperationException(
                "An unresolved or invalid WinRE deployment journal exists. " +
                "A new D0 transaction is forbidden; run deterministic deployment recovery.");
        }

        _journal.Create(plan);
        await RequireAsync(await _platform.VerifyD0Async(plan), "D0 preflight");

        await RequireAsync(await _platform.CaptureSnapshotsAsync(plan), "D1 snapshot");
        Complete(WinReDeploymentStage.D1Snapshotted, "REAgentC, full BCD, GPT, state and WIM snapshots captured.");

        await RequireAsync(await _platform.BackupOriginalAsync(plan), "D2 original WIM backup");
        Complete(WinReDeploymentStage.D2BackupVerified, "Original live WIM backup size/hash/readability verified.");

        await RequireAsync(await _platform.VerifyFirstMutationAsync(plan), "final first-mutation authorization");
        Complete(WinReDeploymentStage.FirstMutationAuthorized,
            "Registered WinRE identity, live WIM and exact backup were reverified immediately before mutation.");

        Intent(WinReDeploymentStage.D3DisableIntent, "About to run REAgentC disable.");
        await _platform.DisableAsync(plan);
        _faults.Hit(WinReDeploymentFaultPoint.AfterDisable);
        await RequireAsync(await _platform.VerifyDisabledAsync(plan), "D3 disabled state");
        Complete(WinReDeploymentStage.D3DisabledVerified, "WinRE disabled; protected BCD loader invariants verified.");

        Intent(WinReDeploymentStage.D4RemoveOriginalIntent, "About to remove the backed-up original live WIM.");
        await _platform.RemoveOriginalAsync(plan);
        _faults.Hit(WinReDeploymentFaultPoint.AfterOriginalRemoval);
        await RequireAsync(await _platform.VerifyOriginalRemovedAsync(plan), "D4 original removal");
        Complete(WinReDeploymentStage.D4OriginalRemoved, "Original live WIM absent; verified backup retained.");

        Intent(WinReDeploymentStage.D4CopyIncomingIntent, "About to copy prepared WIM to the incoming path.");
        await _platform.CopyIncomingAsync(plan, () => _faults.Hit(WinReDeploymentFaultPoint.DuringIncomingCopy));
        await RequireAsync(await _platform.VerifyIncomingAsync(plan), "D4 incoming image");
        _faults.Hit(WinReDeploymentFaultPoint.AfterIncomingHashVerification);
        Complete(WinReDeploymentStage.D4IncomingVerified, "Incoming WIM size/hash/readability verified.");

        Intent(WinReDeploymentStage.D4FinalRenameIntent, "About to atomically rename verified incoming WIM to Winre.wim.");
        await _platform.ActivateIncomingAsync(plan);
        _faults.Hit(WinReDeploymentFaultPoint.AfterFinalRename);
        await RequireAsync(await _platform.VerifyFinalInstalledAsync(plan), "D4 final image");
        Complete(WinReDeploymentStage.D4FinalInstalled, "Prepared WIM installed at the registered filename.");

        Intent(WinReDeploymentStage.D5SetReImageIntent, "About to register the exact recovery directory with REAgentC.");
        await _platform.SetReImageAsync(plan);
        _faults.Hit(WinReDeploymentFaultPoint.AfterSetReImage);
        await RequireAsync(await _platform.VerifySetReImageAsync(plan), "D5 setreimage");
        Complete(WinReDeploymentStage.D5SetReImageVerified, "REAgentC location and protected BCD invariants verified.");

        Intent(WinReDeploymentStage.D5EnableIntent, "About to enable the registered WinRE image.");
        await _platform.EnableAsync(plan);
        _faults.Hit(WinReDeploymentFaultPoint.AfterEnable);
        var enabled = await _platform.VerifyEnabledAsync(plan);
        await RequireAsync(enabled, "D5 enable");
        if (!BcdIdentifiers.TryParseObjectId(enabled.RecoveryGuid, out var actualRecovery) ||
            !BcdIdentifiers.TryParseObjectId(plan.ExpectedRecoveryGuid, out var expectedRecovery) ||
            actualRecovery != expectedRecovery)
        {
            Fail(WinReDeploymentStage.RecoveryRequired,
                $"RecoveryGuid changed from {plan.ExpectedRecoveryGuid} to {enabled.RecoveryGuid ?? "<unknown>"}; rollback and rebuild are mandatory.");
            throw new InvalidOperationException("REAgentC changed RecoveryGuid. The prepared launcher manifest is stale; rollback is required.");
        }

        Complete(WinReDeploymentStage.D5EnabledVerified, "WinRE enabled with the expected RecoveryGuid and protected BCD invariants.");
        await RequireAsync(await _platform.ReviewLauncherAsync(plan), "post-deployment launcher review");
        Complete(WinReDeploymentStage.D5ReviewVerified, "Live registered WIM passed launcher review.");
        Complete(WinReDeploymentStage.AwaitingSmoke,
            "Deployment awaits a separately authorized --recovery-smoke boot; retirement remains forbidden.");
        return new WinReDeploymentResult(true, WinReDeploymentStage.AwaitingSmoke,
            "Prepared WinRE is installed and reviewed; smoke evidence is still required.", _journal.Path);
    }

    public WinReDeploymentResult RecordSmokeVerified(string receiptSha256)
    {
        using var transactionLock = AcquireExclusiveLock();
        var current = _journal.Load();
        var validReceiptHash = receiptSha256?.Length == 64 && receiptSha256.All(Uri.IsHexDigit);
        if (current.Last.Stage != WinReDeploymentStage.AwaitingSmoke || !validReceiptHash)
        {
            throw new InvalidOperationException("Smoke completion is accepted only from AwaitingSmoke with a receipt hash.");
        }

        Complete(WinReDeploymentStage.SmokeVerified, $"Recovery smoke receipt SHA256={receiptSha256}.");
        return new WinReDeploymentResult(true, WinReDeploymentStage.SmokeVerified,
            "Recovery smoke evidence recorded; final Boot 2 verification is still required.", _journal.Path);
    }

    public async Task<WinReDeploymentResult> CommitAfterSmokeAsync()
    {
        using var transactionLock = AcquireExclusiveLock();
        var current = _journal.Load();
        if (current.Last.Stage != WinReDeploymentStage.SmokeVerified)
            throw new InvalidOperationException("Final commit is accepted only after durable SmokeVerified evidence.");
        Intent(WinReDeploymentStage.CommitIntent, "About to run final Boot 2 post-smoke verification.");
        await RequireAsync(await _platform.VerifyPostSmokeAsync(current.Plan), "post-smoke Boot 2 verification");
        Complete(WinReDeploymentStage.Committed,
            "Deployment review, WinRE smoke and final Boot 2 invariants completed.");
        return new WinReDeploymentResult(true, WinReDeploymentStage.Committed,
            "WinRE deployment transaction committed.", _journal.Path);
    }

    public async Task<WinReDeploymentResult> RecoverToRollbackAsync()
    {
        using var transactionLock = AcquireExclusiveLock();
        var current = _journal.Load();
        if (current.IsTerminal)
        {
            throw new InvalidOperationException("The WinRE deployment transaction is already terminal.");
        }

        Intent(WinReDeploymentStage.RollbackIntent,
            $"Deterministic rollback requested from {current.Last.Stage}; no new D0 transaction will be started.");
        try
        {
            var mutationIntentExists = current.Records.Any(record => record.Stage is
                WinReDeploymentStage.D3DisableIntent or
                WinReDeploymentStage.D3DisabledVerified or
                WinReDeploymentStage.D4RemoveOriginalIntent or
                WinReDeploymentStage.D4OriginalRemoved or
                WinReDeploymentStage.D4CopyIncomingIntent or
                WinReDeploymentStage.D4IncomingVerified or
                WinReDeploymentStage.D4FinalRenameIntent or
                WinReDeploymentStage.D4FinalInstalled or
                WinReDeploymentStage.D5SetReImageIntent or
                WinReDeploymentStage.D5SetReImageVerified or
                WinReDeploymentStage.D5EnableIntent or
                WinReDeploymentStage.D5EnabledVerified or
                WinReDeploymentStage.D5ReviewVerified or
                WinReDeploymentStage.AwaitingSmoke or
                WinReDeploymentStage.SmokeVerified or
                WinReDeploymentStage.CommitIntent or
                WinReDeploymentStage.RecoveryRequired);
            if (mutationIntentExists)
            {
                await _platform.RollbackAsync(current.Plan);
                var rollback = await _platform.VerifyRollbackAsync(current.Plan);
                await RequireAsync(rollback, "rollback verification");
                Complete(WinReDeploymentStage.RolledBack,
                    "Original WIM hash and protected BCD loader invariants were restored semantically. " +
                    $"Resolved RecoveryGuid={rollback.RecoveryGuid ?? "<unavailable>"}.");
            }
            else
            {
                await RequireAsync(await _platform.VerifyRollbackAsync(current.Plan), "pre-mutation no-change verification");
                Complete(WinReDeploymentStage.RolledBack,
                    "Pre-mutation journal closed after proving the original WIM and protected invariants were unchanged.");
            }
            return new WinReDeploymentResult(true, WinReDeploymentStage.RolledBack,
                "Incomplete deployment rolled back and verified.", _journal.Path);
        }
        catch (Exception exception)
        {
            Fail(WinReDeploymentStage.RecoveryRequired,
                "Rollback could not be verified and requires operator recovery: " + exception.Message);
            throw;
        }
    }

    private static Task RequireAsync(WinReDeploymentVerification result, string name)
    {
        if (!result.Passed)
        {
            throw new InvalidOperationException($"{name} failed closed: {result.Detail}");
        }

        return Task.CompletedTask;
    }

    private void Intent(WinReDeploymentStage stage, string detail) =>
        _journal.Append(stage, WinReJournalRecordKind.Intent, detail);

    private void Complete(WinReDeploymentStage stage, string detail) =>
        _journal.Append(stage, WinReJournalRecordKind.Completion, detail);

    private void Fail(WinReDeploymentStage stage, string detail) =>
        _journal.Append(stage, WinReJournalRecordKind.Failure, detail);

    private FileStream AcquireExclusiveLock()
    {
        var transactionDirectory = Path.GetDirectoryName(_journal.Path)
            ?? throw new InvalidOperationException("Deployment journal directory is unavailable.");
        var root = Directory.GetParent(transactionDirectory)?.FullName
            ?? throw new InvalidOperationException("Deployment journal root is unavailable.");
        Directory.CreateDirectory(root);
        try
        {
            return new FileStream(Path.Combine(root, "deployment.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another WinRE deployment/recovery process holds the exclusive transaction lock.", exception);
        }
    }
}
