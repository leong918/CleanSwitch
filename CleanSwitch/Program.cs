using System.Runtime.InteropServices;
using CleanSwitch.Recovery;
using CleanSwitch.Services;

namespace CleanSwitch;

internal static class Program
{
    private const string RecoveryRunSwitch = "--recovery-run";
    private const string RecoveryDryRunSwitch = "--recovery-dry-run";

    [STAThread]
    static int Main(string[] args)
    {
        var recoveryRun = HasSwitch(args, RecoveryRunSwitch);
        var recoveryDryRun = HasSwitch(args, RecoveryDryRunSwitch);

        if (recoveryRun || recoveryDryRun)
        {
            return RunRecoverySide(recoveryDryRun);
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>
    /// Headless entry point for the recovery environment:
    /// <c>CleanSwitch.exe --recovery-run</c> performs the Phase 2A handoff,
    /// <c>CleanSwitch.exe --recovery-dry-run</c> validates and logs without changing the BCD.
    /// </summary>
    private static int RunRecoverySide(bool dryRun)
    {
        AttachConsole(AttachParentProcess);

        try
        {
            var options = AppConfiguration.Load();
            var services = RetirementServices.Create(
                options,
                dryRun ? "recovery-dryrun" : "recovery");

            Report($"Retirement state file: {services.Coordinator.StateFilePath}");
            Report($"Log destinations: {string.Join("; ", services.Log.Destinations)}");

            var result = services.RecoveryRunner.RunAsync(dryRun).GetAwaiter().GetResult();
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

    private static bool HasSwitch(IEnumerable<string> args, string name) =>
        args.Any(argument => string.Equals(argument?.Trim(), name, StringComparison.OrdinalIgnoreCase));

    private static void Report(string message)
    {
        Console.Out.WriteLine(message);
        Console.Out.Flush();
    }

    private const int AttachParentProcess = -1;

    /// <summary>
    /// Lets the WinExe write to the console it was launched from. Failure is ignored: the
    /// file log is the authoritative record.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);
}
