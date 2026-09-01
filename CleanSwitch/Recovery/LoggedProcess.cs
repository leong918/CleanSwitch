using System.Diagnostics;
using CleanSwitch.Services;

namespace CleanSwitch.Recovery;

public sealed record LoggedProcessResult(int ExitCode, string StdOut, string StdErr, string CommandLine);

/// <summary>
/// Runs one process and writes command line, exit code, stdout and stderr to the audit log.
/// Used by live deletion. Callers must have already passed every safety guard.
/// </summary>
internal static class LoggedProcess
{
    public static async Task<LoggedProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IOperationLog log)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var commandLine = $"{fileName} {string.Join(" ", arguments)}";
        log.Info("process", $"Executing: {commandLine}");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new RetirementExecutionException($"Could not start {fileName}.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new RetirementExecutionException($"Could not start {fileName}: {exception.Message}", exception);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var result = new LoggedProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask,
            commandLine);

        log.Write(
            result.ExitCode == 0 ? OperationLogLevel.Info : OperationLogLevel.Warning,
            "process",
            $"Completed: {result.CommandLine} | exitCode={result.ExitCode} | " +
            $"stdout={Flatten(result.StdOut)} | stderr={Flatten(result.StdErr)}");

        return result;
    }

    public static string Describe(LoggedProcessResult result)
    {
        var stdout = string.IsNullOrWhiteSpace(result.StdOut) ? "<empty>" : result.StdOut.Trim();
        var stderr = string.IsNullOrWhiteSpace(result.StdErr) ? "<empty>" : result.StdErr.Trim();
        return
            $"Command: {result.CommandLine}{Environment.NewLine}" +
            $"Exit code: {result.ExitCode}{Environment.NewLine}" +
            $"stdout: {stdout}{Environment.NewLine}" +
            $"stderr: {stderr}";
    }

    private static string Flatten(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "<empty>";
        }

        return value.Replace("\r\n", " \\n ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Trim();
    }
}
