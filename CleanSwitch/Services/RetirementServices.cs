using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Services;

/// <summary>
/// Composition root for the retirement flow. Built lazily and only when a retirement
/// action is requested, so a misconfigured <c>RecoveryDataPath</c> cannot break the
/// ordinary boot-switch feature.
/// </summary>
public sealed class RetirementServices
{
    private RetirementServices(
        CleanSwitchOptions options,
        IOperationLog log,
        IBootManager bootManager,
        IRetirementCoordinator coordinator,
        DiskValidator diskValidator,
        BootEntryValidator bootEntryValidator,
        Phase2AHandoff phase2AHandoff,
        RetirementExecutor executor,
        RecoveryRunner recoveryRunner)
    {
        Options = options;
        Log = log;
        BootManager = bootManager;
        Coordinator = coordinator;
        DiskValidator = diskValidator;
        BootEntryValidator = bootEntryValidator;
        Phase2AHandoff = phase2AHandoff;
        Executor = executor;
        RecoveryRunner = recoveryRunner;
    }

    public CleanSwitchOptions Options { get; }

    public IOperationLog Log { get; }

    public IBootManager BootManager { get; }

    public IRetirementCoordinator Coordinator { get; }

    public DiskValidator DiskValidator { get; }

    public BootEntryValidator BootEntryValidator { get; }

    public Phase2AHandoff Phase2AHandoff { get; }

    public RetirementExecutor Executor { get; }

    public RecoveryRunner RecoveryRunner { get; }

    /// <summary>
    /// Creates services for a new operation. The file log is created first so that a failure
    /// to resolve the state location is itself logged.
    /// </summary>
    /// <exception cref="RetirementStorageException">
    /// The configured state location is missing, unusable, or on the volume being retired.
    /// </exception>
    public static RetirementServices CreateForNewOperation(CleanSwitchOptions options, string logPrefix) =>
        Create(options, logPrefix, RetirementStateAccessContext.CreateNewOperation);

    /// <summary>
    /// Creates services for reading or resuming an existing operation. Schema-v2 state-location
    /// safety is proven against the operation's persisted GPT identities when state is loaded.
    /// </summary>
    public static RetirementServices CreateForExistingOperation(CleanSwitchOptions options, string logPrefix) =>
        Create(options, logPrefix, RetirementStateAccessContext.ExistingOperation);

    private static RetirementServices Create(
        CleanSwitchOptions options,
        string logPrefix,
        RetirementStateAccessContext accessContext)
    {
        ArgumentNullException.ThrowIfNull(options);

        var log = FileOperationLog.Create(RetirementStateStore.ResolveLogDirectory(options), logPrefix);
        log.Info("startup", $"CleanSwitch retirement services starting. Log destinations: {string.Join("; ", log.Destinations)}");

        RetirementStateStore store;
        try
        {
            store = new RetirementStateStore(options, log, accessContext);
        }
        catch (RetirementStorageException exception)
        {
            log.Warn("startup", $"Retirement state location rejected: {exception.Message}");
            throw;
        }

        log.Info("startup", $"Retirement state file: {store.StateFilePath}");

        var bootManager = new WindowsBootManager(log);
        var coordinator = new RetirementCoordinator(store, log);
        var diskValidator = new DiskValidator(log);
        var bootEntryValidator = new BootEntryValidator(bootManager, log);
        var phase2AHandoff = new Phase2AHandoff(options, bootManager, coordinator, bootEntryValidator, log);
        var layout = new VolumeLocatorGptLayoutSource();
        var bcdStore = new BootManagerBcdStoreSource(bootManager);
        var executor = new RetirementExecutor(
            options,
            log,
            layout,
            new DiskpartDestructiveDiskCommand(log),
            bcdStore,
            new BcdeditDestructiveBcdCommand(log),
            bootManager);
        var hardwareReview = new RetirementHardwareReview(layout, bcdStore, log);
        var recoveryRunner = new RecoveryRunner(
            bootManager,
            coordinator,
            diskValidator,
            bootEntryValidator,
            executor,
            options,
            log,
            hardwareReview);

        return new RetirementServices(
            options,
            log,
            bootManager,
            coordinator,
            diskValidator,
            bootEntryValidator,
            phase2AHandoff,
            executor,
            recoveryRunner);
    }
}
