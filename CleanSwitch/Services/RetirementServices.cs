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
        RetirementExecutor executor,
        RecoveryRunner recoveryRunner)
    {
        Options = options;
        Log = log;
        BootManager = bootManager;
        Coordinator = coordinator;
        DiskValidator = diskValidator;
        BootEntryValidator = bootEntryValidator;
        Executor = executor;
        RecoveryRunner = recoveryRunner;
    }

    public CleanSwitchOptions Options { get; }

    public IOperationLog Log { get; }

    public IBootManager BootManager { get; }

    public IRetirementCoordinator Coordinator { get; }

    public DiskValidator DiskValidator { get; }

    public BootEntryValidator BootEntryValidator { get; }

    public RetirementExecutor Executor { get; }

    public RecoveryRunner RecoveryRunner { get; }

    /// <summary>
    /// Creates the retirement services. The file log is created first so that a failure to
    /// resolve the state location is itself logged.
    /// </summary>
    /// <exception cref="RetirementStorageException">
    /// The configured state location is missing, unusable, or on the volume being retired.
    /// </exception>
    public static RetirementServices Create(CleanSwitchOptions options, string logPrefix)
    {
        ArgumentNullException.ThrowIfNull(options);

        var log = FileOperationLog.Create(RetirementStateStore.ResolveLogDirectory(options), logPrefix);
        log.Info("startup", $"CleanSwitch retirement services starting. Log destinations: {string.Join("; ", log.Destinations)}");

        RetirementStateStore store;
        try
        {
            store = new RetirementStateStore(options, log);
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
            executor,
            recoveryRunner);
    }
}
