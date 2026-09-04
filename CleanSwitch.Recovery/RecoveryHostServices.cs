using CleanSwitch.Models;
using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch;

internal sealed record RecoveryHostServices(
    IRetirementCoordinator Coordinator,
    RecoveryRunner RecoveryRunner)
{
    public static RecoveryHostServices Create(CleanSwitchOptions options, string logPrefix)
    {
        ArgumentNullException.ThrowIfNull(options);

        var log = FileOperationLog.Create(RetirementStateStore.ResolveLogDirectory(options), logPrefix);
        var store = new RetirementStateStore(options, log, RetirementStateAccessContext.ExistingOperation);
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
            hardwareReview,
            bcdStore,
            layout);

        return new RecoveryHostServices(coordinator, recoveryRunner);
    }
}
