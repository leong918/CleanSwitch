using CleanSwitch.Models;
using CleanSwitch.Services;

namespace CleanSwitch;

/// <summary>
/// CLI entry for <c>--abandon-retirement</c>. Builds only the state store and
/// coordinator. Does not construct <c>RetirementServices</c>,
/// <c>RetirementExecutor</c>, diskpart, or bcdedit.
/// </summary>
internal static class RetirementAbandonCommand
{
    public const string Switch = "--abandon-retirement";

    /// <summary>
    /// Abandon may load and update retirement state on the running system volume during
    /// operator cleanup. Does not change the persisted appsettings default.
    /// </summary>
    internal static void ConfigureForAbandon(CleanSwitchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AllowStateOnSystemVolume = true;
    }

    public static int Run(Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);

        report("CleanSwitch --abandon-retirement");
        report("Non-destructive: no diskpart, no bcdedit /delete, no partition change,");
        report("no active BCD store change, and no reboot.");
        report(string.Empty);

        try
        {
            var options = AppConfiguration.Load();
            ConfigureForAbandon(options);
            var log = FileOperationLog.Create(RetirementStateStore.ResolveLogDirectory(options), "abandon");
            report($"Log destinations: {string.Join("; ", log.Destinations)}");

            var store = new RetirementStateStore(options, log);
            var coordinator = new RetirementCoordinator(store, log);
            var abandoner = new RetirementAbandoner(coordinator, log);
            abandoner.Execute(report);

            report(string.Empty);
            report("Abandon completed. Use RETIRE SYSTEM on Boot 1 to capture a fresh schema-v2 PENDING state.");
            return 0;
        }
        catch (Exception exception) when (
            exception is RetirementStateException or RetirementStorageException or InvalidOperationException)
        {
            report(string.Empty);
            report("Abandon refused or did not complete.");
            report(exception.Message);
            return 2;
        }
    }
}
