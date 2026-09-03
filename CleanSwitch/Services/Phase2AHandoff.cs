using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Services;

/// <summary>
/// Fail-closed Phase 2A coordinator. It captures a fresh schema-v2 operation, makes the
/// configured Boot 2 loader the persistent default, selects WinRE for one boot, then restarts.
/// It never performs disk or BCD deletion.
/// </summary>
public sealed class Phase2AHandoff
{
    private readonly CleanSwitchOptions _options;
    private readonly IBootManager _bootManager;
    private readonly IRetirementCoordinator _coordinator;
    private readonly IBootEntryValidator _identitySource;
    private readonly IWinReLauncherValidator _launcherValidator;
    private readonly IOperationLog _log;

    public Phase2AHandoff(
        CleanSwitchOptions options,
        IBootManager bootManager,
        IRetirementCoordinator coordinator,
        IBootEntryValidator identitySource,
        IWinReLauncherValidator launcherValidator,
        IOperationLog? log = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _bootManager = bootManager ?? throw new ArgumentNullException(nameof(bootManager));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _identitySource = identitySource ?? throw new ArgumentNullException(nameof(identitySource));
        _launcherValidator = launcherValidator ?? throw new ArgumentNullException(nameof(launcherValidator));
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<RetirementState> ExecuteAsync(BootLayout layout, Action<string>? reportStage = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        // First guard: reject reversal before recovery resolution or GPT identity capture.
        Phase2ARetirementGuard.Validate(layout, _options.Boot2Guid);

        RetirementState? state = null;
        try
        {
            reportStage?.Invoke("Validating the configured recovery environment...");
            var recovery = await _identitySource.ResolveRecoveryEntryAsync(_options.RecoveryGuid);
            if (recovery.Identifier is null)
            {
                throw new InvalidOperationException(
                    "The Windows Recovery Environment boot entry could not be validated. Nothing was changed." +
                    Environment.NewLine + recovery.Report.Describe());
            }

            // A stock WinRE entry is not a recovery continuation. Byte-copy the exact WIM
            // selected by RecoveryGuid into the validated machine-level workspace, then mount
            // and inspect only that copy before capturing identities or writing PENDING.
            // The manifest, winpeshl.ini, embedded executable, appsettings, ProductVersion,
            // hashes and official RecoveryRunner arguments must all match this running build.
            reportStage?.Invoke("Verifying the CleanSwitch launcher inside the selected WinRE image...");
            var launcher = await _launcherValidator.ValidateAsync(recovery);
            if (!launcher.Passed)
            {
                throw new InvalidOperationException(
                    "RETIRE SYSTEM refused before creating PENDING: the selected recovery environment is not " +
                    "provisioned with the exact approved CleanSwitch recovery launcher." +
                    Environment.NewLine + launcher.Report.Describe());
            }

            // Second guard: immediately before reading either GPT identity.
            Phase2ARetirementGuard.Validate(layout, _options.Boot2Guid);
            reportStage?.Invoke("Recording Boot 1 and Boot 2 partition identities...");
            var boot1Identity = await _identitySource.TryDescribeBootEntryVolumeAsync(layout.Current.Identifier);
            var boot2Identity = await _identitySource.TryDescribeBootEntryVolumeAsync(layout.Target.Identifier);
            if (boot1Identity is null || boot2Identity is null)
            {
                throw new InvalidOperationException(
                    "Boot 1 or Boot 2 partition identity could not be recorded from the partition table. " +
                    "No BCD mutation or restart was attempted." + Environment.NewLine +
                    $"Boot 1: {boot1Identity?.Describe() ?? "No identity returned."}{Environment.NewLine}" +
                    $"Boot 2: {boot2Identity?.Describe() ?? "No identity returned."}");
            }

            RetirementStateIdentityRequirements.ValidateForNewPending(
                layout.Current.Identifier,
                layout.Target.Identifier,
                boot1Identity,
                boot2Identity);

            _log.Info("phase2a", $"Boot 1 identity: {boot1Identity.Describe()}");
            _log.Info("phase2a", $"Boot 2 survivor identity: {boot2Identity.Describe()}");

            state = _coordinator.BeginRetirement(
                layout.Current.Identifier,
                layout.Target.Identifier,
                recovery.Identifier,
                boot1Identity,
                boot2Identity);

            // Third guard: no BCD mutation is reachable unless the persisted roles still match
            // the configured survivor contract.
            Phase2ARetirementGuard.Validate(layout, _options.Boot2Guid);
            Phase2ARetirementGuard.ValidatePersistedRoles(state, layout, _options.Boot2Guid);

            reportStage?.Invoke("Setting Boot 2 as the surviving default...");
            if (!await _bootManager.SetDefaultBootAsync(layout.Target.Identifier))
            {
                throw new BootManagerException("BCDEdit did not confirm Boot 2 as the persistent default.");
            }

            reportStage?.Invoke("Setting the recovery environment as the next boot...");
            if (!await _bootManager.SetNextBootAsync(recovery.Identifier))
            {
                throw new BootManagerException("BCDEdit did not confirm WinRE as the one-time boot target.");
            }

            reportStage?.Invoke($"Restarting into the recovery environment in {_options.RestartDelaySeconds} second(s)...");
            await _bootManager.RestartAsync(_options.RestartDelaySeconds);
            return state;
        }
        catch (Exception exception)
        {
            if (state is not null)
            {
                var failure = "Phase 2A failed closed before a confirmed restart: " + exception.Message;
                _log.Warn("phase2a", failure);
                try
                {
                    _coordinator.MarkFailed(state, failure);
                }
                catch (Exception auditException) when (
                    auditException is RetirementStorageException or RetirementStateException)
                {
                    _log.Warn("phase2a", "Could not persist the Phase 2A failure audit: " + auditException.Message);
                }
            }

            throw;
        }
    }
}

public static class Phase2ARetirementGuard
{
    public const string RunningSurvivorMessage =
        "当前正在运行的是配置的 Boot 2 survivor。请先启动进入 Boot 1，再执行 RETIRE SYSTEM。";

    public static void Validate(BootLayout layout, string? configuredBoot2Guid)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!BcdIdentifiers.TryParseObjectId(configuredBoot2Guid, out var configuredBoot2) ||
            BcdIdentifiers.IsProtectedObject(configuredBoot2))
        {
            throw new InvalidOperationException("CleanSwitch:Boot2Guid must be a concrete, non-protected BCD loader GUID.");
        }

        if (!BcdIdentifiers.TryParseObjectId(layout.Current.Identifier, out var retiring) ||
            !BcdIdentifiers.TryParseObjectId(layout.Target.Identifier, out var survivor))
        {
            throw new InvalidOperationException("The retiring and survivor loaders must resolve to concrete BCD GUIDs.");
        }

        if (retiring == configuredBoot2)
        {
            throw new InvalidOperationException(RunningSurvivorMessage);
        }

        if (retiring == survivor)
        {
            throw new InvalidOperationException("RETIRE SYSTEM refused: the retiring loader and survivor loader are identical.");
        }

        if (survivor != configuredBoot2)
        {
            throw new InvalidOperationException(
                $"RETIRE SYSTEM refused: the selected survivor {BcdIdentifiers.Format(survivor)} is not the configured " +
                $"Boot 2 survivor {BcdIdentifiers.Format(configuredBoot2)}.");
        }
    }

    public static void ValidatePersistedRoles(
        RetirementState state,
        BootLayout layout,
        string? configuredBoot2Guid)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(layout, configuredBoot2Guid);

        if (state.SchemaVersion != RetirementState.CurrentSchemaVersion ||
            state.Status != RetirementStatus.Pending ||
            !BcdIdentifiers.TryParseObjectId(state.Boot1BcdObjectId, out var persistedRetiring) ||
            !BcdIdentifiers.TryParseObjectId(state.Boot2BcdObjectId, out var persistedSurvivor) ||
            !BcdIdentifiers.TryParseObjectId(layout.Current.Identifier, out var retiring) ||
            !BcdIdentifiers.TryParseObjectId(layout.Target.Identifier, out var survivor) ||
            !BcdIdentifiers.TryParseObjectId(configuredBoot2Guid, out var configuredSurvivor) ||
            persistedRetiring != retiring ||
            persistedSurvivor != survivor ||
            persistedSurvivor != configuredSurvivor)
        {
            throw new InvalidOperationException(
                "Phase 2A refused BCD mutation: the persisted PENDING loader roles do not exactly match " +
                "the validated retiring loader and configured Boot 2 survivor.");
        }
    }
}
