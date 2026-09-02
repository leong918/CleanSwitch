using System.Text;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

/// <summary>
/// Production diskpart runner. Must not be constructed on a path that skipped the
/// three live-delete gates. Tests should inject <c>FakeDestructiveDiskCommand</c> instead.
/// </summary>
public sealed class DiskpartDestructiveDiskCommand : IDestructiveDiskCommand
{
    private readonly IOperationLog _log;

    public DiskpartDestructiveDiskCommand(IOperationLog? log = null)
    {
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<DestructiveCommandResult> ExecuteAsync(ResolvedDeletionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var script = string.Join(
            Environment.NewLine,
            [
                $"select disk {target.DiskNumber}",
                $"select partition {target.PartitionNumber}",
                "delete partition override"
            ]);

        var scriptPath = Path.Combine(Path.GetTempPath(), "cleanswitch-retire-boot1.txt");
        await File.WriteAllTextAsync(scriptPath, script + Environment.NewLine, Encoding.ASCII);

        var written = Normalize(await File.ReadAllTextAsync(scriptPath, Encoding.ASCII));
        var expected = Normalize(script);
        if (!string.Equals(written, expected, StringComparison.Ordinal))
        {
            throw new RetirementExecutionException(
                "diskpart script on disk did not match the in-memory script. Refusing to start diskpart.");
        }

        if (written.Contains("select volume", StringComparison.OrdinalIgnoreCase) ||
            !written.Contains($"select disk {target.DiskNumber}", StringComparison.Ordinal) ||
            !written.Contains($"select partition {target.PartitionNumber}", StringComparison.Ordinal))
        {
            throw new RetirementExecutionException(
                "diskpart script failed the pin check. Refusing to start diskpart.");
        }

        _log.Info(
            "diskpart",
            "Production disk command about to start. " +
            $"reResolvedGpt={VolumeLocator.FormatGptId(target.TargetGptId)} " +
            $"diskIdentity={(target.DiskGptId is null ? "(none)" : VolumeLocator.FormatGptId(target.DiskGptId.Value))} " +
            $"diskNumber={target.DiskNumber} partitionNumber={target.PartitionNumber} " +
            $"gptType={GptPartitionTypes.Describe(target.PartitionType)} " +
            $"offset={target.StartingOffset} size={target.SizeBytes}");
        _log.Info("diskpart", $"Script {scriptPath}: {written}");
        var process = await LoggedProcess.RunAsync("diskpart.exe", ["/s", scriptPath], _log);
        return new DestructiveCommandResult(process.ExitCode, process.StdOut, process.StdErr, process.CommandLine);
    }

    private static string Normalize(string script) =>
        string.Join(
            "\n",
            script.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0));
}
