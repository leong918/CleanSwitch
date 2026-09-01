using System.Diagnostics;
using System.Globalization;
using System.Text;
using CleanSwitch.Models;
using CleanSwitch.Recovery;

namespace CleanSwitch.Services;

public sealed class WindowsBootManager : IBootManager
{
    private readonly IOperationLog _log;

    /// <param name="log">
    /// Persistent audit log. Every bcdedit and shutdown invocation is written to it with
    /// its exit code and captured output, so the flow can be reconstructed after a reboot.
    /// </param>
    public WindowsBootManager(IOperationLog? log = null)
    {
        _log = log ?? NullOperationLog.Instance;
    }

    public async Task<BootLayout> DetectAsync(string? preferredOtherGuid)
    {
        var currentOutput = await ReadBcdEditOutputAsync(["/enum", "{current}", "/v"]);
        var loaderOutput = await ReadBcdEditOutputAsync(["/enum", "OSLOADER", "/v"]);

        var currentEntries = BcdEditTextParser.Parse(currentOutput);
        var current = currentEntries.FirstOrDefault(IsSwitchableWindows)
            ?? currentEntries.FirstOrDefault(entry => BcdIdentifiers.TryParseObjectId(entry.Identifier, out _))
            ?? throw new BootManagerException(
                "Could not detect the currently running Windows boot entry from BCDEdit.");

        var loaders = BcdEditTextParser.Parse(loaderOutput)
            .Where(IsSwitchableWindows)
            .GroupBy(entry => entry.Identifier, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        var currentFromLoaders = loaders.FirstOrDefault(entry => IdsEqual(entry.Identifier, current.Identifier));
        if (currentFromLoaders is not null)
        {
            current = currentFromLoaders;
        }
        else
        {
            loaders.Add(current);
        }

        var others = loaders
            .Where(entry => !IdsEqual(entry.Identifier, current.Identifier))
            .ToList();

        var target = SelectTarget(current, others, preferredOtherGuid);
        return new BootLayout(
            ToBootEntry(current),
            ToBootEntry(target));
    }

    public async Task<bool> SetNextBootAsync(string bootGuid)
    {
        var normalizedGuid = NormalizeBootGuid(bootGuid);

        Trace.WriteLine($"Validating that target BCD identifier {normalizedGuid} exists.");
        var enumeration = await RunProcessAsync(
            "bcdedit.exe",
            ["/enum", normalizedGuid, "/v"]);

        if (enumeration.ExitCode != 0)
        {
            throw new BootManagerException(
                $"Target BCD identifier {normalizedGuid} was not found or could not be read." +
                FormatProcessOutput(enumeration));
        }

        Trace.WriteLine($"Setting {normalizedGuid} as the next one-time boot target.");
        var bootSequence = await RunProcessAsync(
            "bcdedit.exe",
            ["/bootsequence", normalizedGuid]);

        if (bootSequence.ExitCode != 0)
        {
            Trace.WriteLine($"BCDEdit failed while setting the one-time boot target. ExitCode={bootSequence.ExitCode}.");
            throw new BootManagerException(
                $"BCDEdit failed while setting the one-time boot target. The computer will not be restarted." +
                FormatProcessOutput(bootSequence));
        }

        Trace.WriteLine($"BCDEdit successfully set the next one-time boot target to {normalizedGuid}.");
        _log.Info("bcdedit", $"One-time boot sequence set to {normalizedGuid}.");
        return true;
    }

    public async Task<IReadOnlyList<BcdEntry>> EnumerateAsync(string scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            throw new BootManagerException("A BCD enumeration scope is required.");
        }

        var output = await ReadBcdEditOutputAsync(["/enum", scope.Trim(), "/v"]);
        return BcdEditTextParser.Parse(output);
    }

    public async Task<BcdEntry?> TryGetEntryAsync(string bootGuid)
    {
        var normalizedGuid = NormalizeBootGuid(bootGuid);
        var result = await RunProcessAsync("bcdedit.exe", ["/enum", normalizedGuid, "/v"]);
        if (result.ExitCode != 0)
        {
            _log.Warn(
                "bcdedit",
                $"BCD identifier {normalizedGuid} could not be read." + FormatProcessOutput(result));
            return null;
        }

        var entries = BcdEditTextParser.Parse(result.StdOut);
        var match = entries.FirstOrDefault(entry => IdsEqual(entry.Identifier, normalizedGuid))
            ?? entries.FirstOrDefault();

        if (match is null)
        {
            // bcdedit can print output in an encoding the default reader mangles; retry the
            // encoding fallback path before concluding the entry is unreadable.
            try
            {
                var fallbackOutput = await ReadBcdEditOutputAsync(["/enum", normalizedGuid, "/v"]);
                match = BcdEditTextParser.Parse(fallbackOutput)
                    .FirstOrDefault(entry => IdsEqual(entry.Identifier, normalizedGuid));
            }
            catch (BootManagerException exception)
            {
                _log.Warn("bcdedit", $"Encoding fallback for {normalizedGuid} failed: {exception.Message}");
                return null;
            }
        }

        return match;
    }

    public async Task RestartAsync(int delaySeconds)
    {
        if (delaySeconds < 0)
        {
            throw new BootManagerException("RestartDelaySeconds must be zero or greater.");
        }

        Trace.WriteLine($"Scheduling a restart in {delaySeconds} seconds.");
        var restart = await RunProcessAsync(
            "shutdown.exe",
            ["/r", "/t", delaySeconds.ToString(CultureInfo.InvariantCulture)]);

        if (restart.ExitCode != 0)
        {
            throw new BootManagerException(
                $"Windows shutdown.exe failed." + FormatProcessOutput(restart));
        }

        Trace.WriteLine("Windows restart successfully scheduled.");
        _log.Info("restart", $"Restart scheduled in {delaySeconds} second(s).");
    }

    private static BcdEntry SelectTarget(
        BcdEntry current,
        IReadOnlyList<BcdEntry> others,
        string? preferredOtherGuid)
    {
        if (others.Count == 0)
        {
            throw new BootManagerException(
                $"Only one Windows boot entry was found: {FormatEntry(current)}. " +
                "CleanSwitch needs a second Windows Boot Loader entry to switch to.");
        }

        if (others.Count == 1)
        {
            return others[0];
        }

        if (!string.IsNullOrWhiteSpace(preferredOtherGuid) &&
            Guid.TryParse(preferredOtherGuid.Trim(), out var preferredGuid))
        {
            var preferredId = FormatGuid(preferredGuid);
            if (!IdsEqual(preferredId, current.Identifier))
            {
                return others.FirstOrDefault(entry => IdsEqual(entry.Identifier, preferredId))
                    ?? throw new BootManagerException(
                        $"Configured Boot2Guid {preferredId} was not found among the other Windows boot entries.");
            }
        }

        var named = others.FirstOrDefault(entry =>
            ContainsIgnoreCase(entry.Description, "Boot 1") ||
            ContainsIgnoreCase(entry.Description, "Main") ||
            ContainsIgnoreCase(entry.Description, "Clean"));
        if (named is not null)
        {
            return named;
        }

        var found = string.Join(", ", others.Select(FormatEntry));
        throw new BootManagerException(
            "Multiple other Windows boot entries were found. Set CleanSwitch:Boot2Guid in appsettings.json to choose one. Found: " +
            found);
    }

    private async Task<string> ReadBcdEditOutputAsync(string[] arguments)
    {
        ProcessResult? lastFailure = null;
        foreach (var encoding in new[] { Encoding.Unicode, Encoding.UTF8, Encoding.Default })
        {
            var result = await RunProcessAsync("bcdedit.exe", arguments, encoding);
            if (result.ExitCode == 0 && BcdEditTextParser.Parse(result.StdOut).Count > 0)
            {
                return result.StdOut;
            }

            lastFailure = result;
        }

        if (lastFailure is null || lastFailure.ExitCode != 0)
        {
            throw new BootManagerException(
                "BCDEdit could not enumerate boot entries." +
                (lastFailure is null ? string.Empty : FormatProcessOutput(lastFailure)));
        }

        throw new BootManagerException(
            "BCDEdit ran, but no Windows boot entries could be parsed from its output.");
    }

    private async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        Encoding? encoding = null)
    {
        var argumentList = arguments.ToArray();
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding
        };

        foreach (var argument in argumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var commandLine = $"{fileName} {string.Join(" ", argumentList)}";
        Trace.WriteLine($"Executing {fileName} with arguments: {string.Join(" ", argumentList)}.");
        _log.Info("process", $"Executing: {commandLine}");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new BootManagerException($"Could not start {fileName}.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new BootManagerException($"Could not start {fileName}: {exception.Message}", exception);
        }

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        var result = new ProcessResult(
            process.ExitCode,
            await standardOutputTask,
            await standardErrorTask);

        Trace.WriteLine(
            $"{fileName} completed. ExitCode={result.ExitCode}, " +
            $"StdOut={FormatForLog(result.StdOut)}, StdErr={FormatForLog(result.StdErr)}.");

        _log.Write(
            result.ExitCode == 0 ? OperationLogLevel.Info : OperationLogLevel.Warning,
            "process",
            $"Completed: {commandLine} | exitCode={result.ExitCode} | stdout={FormatForLog(result.StdOut)} | " +
            $"stderr={FormatForLog(result.StdErr)}");

        return result;
    }

    private static bool IsSwitchableWindows(BcdEntry entry)
    {
        if (!BcdIdentifiers.TryParseObjectId(entry.Identifier, out _))
        {
            return false;
        }

        if (entry.IsResumeLoader)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(entry.Path) && !entry.IsWindowsLoader)
        {
            return false;
        }

        return !ContainsIgnoreCase(entry.Description, "Recovery");
    }

    private static string NormalizeBootGuid(string bootGuid)
    {
        if (string.IsNullOrWhiteSpace(bootGuid) ||
            !Guid.TryParse(bootGuid.Trim(), out var parsedGuid))
        {
            throw new BootManagerException(
                $"The target boot identifier is not a valid Windows BCD GUID. Received '{bootGuid}'.");
        }

        return FormatGuid(parsedGuid);
    }

    private static BootEntry ToBootEntry(BcdEntry entry)
    {
        var description = string.IsNullOrWhiteSpace(entry.Description)
            ? $"Windows {entry.Identifier}"
            : entry.Description.Trim();
        return new BootEntry(entry.Identifier, description);
    }

    private static string FormatEntry(BcdEntry entry) =>
        $"{(string.IsNullOrWhiteSpace(entry.Description) ? "Windows" : entry.Description)} ({entry.Identifier})";

    private static string FormatGuid(Guid guid) => $"{{{guid:D}}}";

    private static bool IdsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsIgnoreCase(string? value, string part) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string FormatProcessOutput(ProcessResult result)
    {
        var stdout = string.IsNullOrWhiteSpace(result.StdOut) ? "<empty>" : result.StdOut.Trim();
        var stderr = string.IsNullOrWhiteSpace(result.StdErr) ? "<empty>" : result.StdErr.Trim();
        return $"{Environment.NewLine}{Environment.NewLine}Exit code: {result.ExitCode}{Environment.NewLine}Output: {stdout}{Environment.NewLine}Error: {stderr}";
    }

    private static string FormatForLog(string value) =>
        string.IsNullOrWhiteSpace(value) ? "<empty>" : value.Trim();

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}

public sealed class BootManagerException : Exception
{
    public BootManagerException(string message)
        : base(message)
    {
    }

    public BootManagerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
