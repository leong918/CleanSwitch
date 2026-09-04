using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch;

internal static class Program
{
    private const string RecoveryLaunchSwitch = "--recovery-launch";

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1 || !string.Equals(args[0], RecoveryLaunchSwitch, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("CleanSwitch Recovery accepts only --recovery-launch.");
                Console.Error.WriteLine("No retirement executor or destructive command was instantiated.");
                return 2;
            }

            var options = AppConfiguration.Load();
            var services = RecoveryHostServices.Create(options, "recovery-launch");
            var state = services.Coordinator.TryLoad()
                ?? throw new InvalidOperationException("No active retirement handoff exists.");
            var result = services.RecoveryRunner.RunAsync(
                new RecoveryRunRequest(false, false, true, state.HandoffAuthorizationToken)).GetAwaiter().GetResult();
            Console.WriteLine($"{result.Outcome}: {result.Message}");
            return result.Outcome == RecoveryRunOutcome.Failed ? 1 : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                RetirementStorageException or RetirementExecutionException or BootManagerException)
        {
            Console.Error.WriteLine("Recovery launcher failed closed: " + exception.Message);
            return 2;
        }
    }
}
