using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        var reviewOnly = (recoveryReview || hardwareReview) && !recoveryRun;

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
                ExecuteDeletion: executeDeletion && !reviewOnly));
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
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
            options.AllowStateOnSystemVolume = true;
            var services = RetirementServices.Create(options, reviewOnly ? "handoff-repair-review" : "handoff-repair");
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
            options.AllowStateOnSystemVolume = true;
            var services = RetirementServices.Create(options, "resume-preview");

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
            var services = RetirementServices.Create(options, prefix);

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
